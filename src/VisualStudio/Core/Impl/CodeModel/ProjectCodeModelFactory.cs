﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.VisualStudio.Shell;
using Roslyn.Utilities;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.CodeModel
{
    [Export(typeof(IProjectCodeModelFactory))]
    [Export(typeof(ProjectCodeModelFactory))]
    internal sealed class ProjectCodeModelFactory : ForegroundThreadAffinitizedObject, IProjectCodeModelFactory
    {
        private readonly ConcurrentDictionary<ProjectId, ProjectCodeModel> _projectCodeModels = new ConcurrentDictionary<ProjectId, ProjectCodeModel>();

        private readonly VisualStudioWorkspace _visualStudioWorkspace;
        private readonly IServiceProvider _serviceProvider;

        private readonly IThreadingContext _threadingContext;

        private readonly IAsynchronousOperationListener _listener;
        private readonly AsyncBatchingWorkQueue<DocumentId> _documentsToFireEventsFor;

        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public ProjectCodeModelFactory(
            VisualStudioWorkspace visualStudioWorkspace,
            [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
            IThreadingContext threadingContext,
            IAsynchronousOperationListenerProvider listenerProvider)
            : base(threadingContext, assertIsForeground: false)
        {
            _visualStudioWorkspace = visualStudioWorkspace;
            _serviceProvider = serviceProvider;
            _threadingContext = threadingContext;

            _listener = listenerProvider.GetListener(FeatureAttribute.CodeModel);

            // Queue up notifications we hear about docs changing.  that way we don't have to fire events multiple times
            // for the same documents.  Once enough time has passed, take the documents that were changed and run
            // through them, firing their latest events.
            _documentsToFireEventsFor = new AsyncBatchingWorkQueue<DocumentId>(
                TimeSpan.FromMilliseconds(visualStudioWorkspace.Options.GetOption(InternalSolutionCrawlerOptions.AllFilesWorkerBackOffTimeSpanInMS)),
                ProcessNextDocumentBatchAsync,
                // We only care about unique doc-ids, so pass in this comparer to collapse streams of changes for a
                // single document down to one notification.
                EqualityComparer<DocumentId>.Default,
                _listener,
                threadingContext.DisposalToken);

            _visualStudioWorkspace.WorkspaceChanged += OnWorkspaceChanged;
        }

        private async System.Threading.Tasks.Task ProcessNextDocumentBatchAsync(
            ImmutableArray<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            // This logic preserves the previous behavior we had with IForegroundNotificationService.
            // Specifically, we don't run on the UI thread for more than 15ms at a time.  And once we 
            // have, we wait 50ms before continuing.  These constants are just what we defined from
            // legacy, and otherwise have no special meaning.
            const int MaxTimeSlice = 15;
            var delayBetweenProcessing = TimeSpan.FromMilliseconds(50);

            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var stopwatch = SharedStopwatch.StartNew();
            foreach (var documentId in documentIds)
            {
                do
                {
                    // Keep firing events for this doc, as long as we haven't exceeded the max amount
                    // of waiting time, and there's no user input that should take precedence.
                    if (stopwatch.Elapsed.Ticks > MaxTimeSlice || IsInputPending())
                    {
                        await _listener.Delay(delayBetweenProcessing, cancellationToken).ConfigureAwait(true);
                        stopwatch = SharedStopwatch.StartNew();
                    }
                }
                while (FireEventsForDocument(documentId));
            }

            return;

            bool FireEventsForDocument(DocumentId documentId)
            {
                // If we've been asked to shutdown, don't bother reporting any more events.
                if (_threadingContext.DisposalToken.IsCancellationRequested)
                    return false;

                var projectCodeModel = this.TryGetProjectCodeModel(documentId.ProjectId);
                if (projectCodeModel == null)
                    return false;

                var filename = _visualStudioWorkspace.GetFilePath(documentId);
                if (filename == null)
                    return false;

                if (!projectCodeModel.TryGetCachedFileCodeModel(filename, out var fileCodeModelHandle))
                    return false;

                var codeModel = fileCodeModelHandle.Object;
                return codeModel.FireEvents();
            }
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            // Events that can't change existing code model items.  Can just ignore them.
            switch (e.Kind)
            {
                case WorkspaceChangeKind.SolutionAdded:
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.AdditionalDocumentAdded:
                case WorkspaceChangeKind.AdditionalDocumentRemoved:
                case WorkspaceChangeKind.AdditionalDocumentReloaded:
                case WorkspaceChangeKind.AdditionalDocumentChanged:
                case WorkspaceChangeKind.AnalyzerConfigDocumentAdded:
                case WorkspaceChangeKind.AnalyzerConfigDocumentRemoved:
                case WorkspaceChangeKind.AnalyzerConfigDocumentReloaded:
                case WorkspaceChangeKind.AnalyzerConfigDocumentChanged:
                    return;
                case WorkspaceChangeKind.DocumentRemoved:
                case WorkspaceChangeKind.DocumentChanged:
                    // Fast path when we know we affected a document that could have had code model elements in it.  No
                    // need to do a solution diff in this case.
                    _documentsToFireEventsFor.AddWork(e.DocumentId!);
                    return;
            }

            // Other type of event that could indicate a doc change/removal. Have to actually analyze the change to
            // determine what we should do here.

            var changes = e.OldSolution.GetChanges(e.NewSolution);

            foreach (var project in changes.GetRemovedProjects())
                _documentsToFireEventsFor.AddWork(project.DocumentIds);

            foreach (var projectChange in changes.GetProjectChanges())
            {
                _documentsToFireEventsFor.AddWork(projectChange.GetRemovedDocuments());
                _documentsToFireEventsFor.AddWork(projectChange.GetChangedDocuments());
            }
        }

        public IProjectCodeModel CreateProjectCodeModel(ProjectId id, ICodeModelInstanceFactory codeModelInstanceFactory)
        {
            var projectCodeModel = new ProjectCodeModel(_threadingContext, id, codeModelInstanceFactory, _visualStudioWorkspace, _serviceProvider, this);
            if (!_projectCodeModels.TryAdd(id, projectCodeModel))
            {
                throw new InvalidOperationException($"A {nameof(IProjectCodeModel)} has already been created for project with ID {id}");
            }

            return projectCodeModel;
        }

        public ProjectCodeModel GetProjectCodeModel(ProjectId id)
        {
            if (!_projectCodeModels.TryGetValue(id, out var projectCodeModel))
            {
                throw new InvalidOperationException($"No {nameof(ProjectCodeModel)} exists for project with ID {id}");
            }

            return projectCodeModel;
        }

        public IEnumerable<ProjectCodeModel> GetAllProjectCodeModels()
            => _projectCodeModels.Values;

        internal void OnProjectClosed(ProjectId projectId)
            => _projectCodeModels.TryRemove(projectId, out _);

        public ProjectCodeModel TryGetProjectCodeModel(ProjectId id)
        {
            _projectCodeModels.TryGetValue(id, out var projectCodeModel);
            return projectCodeModel;
        }

        public EnvDTE.FileCodeModel GetOrCreateFileCodeModel(ProjectId id, string filePath)
            => GetProjectCodeModel(id).GetOrCreateFileCodeModel(filePath).Handle;

        public void ScheduleDeferredCleanupTask(Action<CancellationToken> a)
        {
            _ = _threadingContext.RunWithShutdownBlockAsync(async cancellationToken =>
            {
                await _threadingContext.JoinableTaskFactory.StartOnIdle(
                    () => a(cancellationToken),
                    VsTaskRunContext.UIThreadNormalPriority);
            });
        }
    }
}
