﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace Microsoft.CodeAnalysis.Diagnostics
{

    /// <summary>
    /// Base type for all taggers that interact with the <see cref="IDiagnosticAnalyzerService"/> and produce tags for
    /// the diagnostics with different UI presentations.
    /// </summary>
    internal abstract partial class AbstractDiagnosticsTaggerProvider<TTag>
        : ITaggerProvider, IRawDiagnosticsTaggerProviderCallback<TTag>
        where TTag : ITag
    {
        private readonly ImmutableArray<RawDiagnosticsTaggerProvider<TTag>> _rawDiagnosticsTaggerProviders;

        protected AbstractDiagnosticsTaggerProvider(
            IThreadingContext threadingContext,
            IDiagnosticService diagnosticService,
            IDiagnosticAnalyzerService analyzerService,
            IGlobalOptionService globalOptions,
            ITextBufferVisibilityTracker? visibilityTracker,
            IAsynchronousOperationListener listener)
        {
            _rawDiagnosticsTaggerProviders = ImmutableArray.Create(
                CreateRawDiagnosticsTaggerProvider(RawDiagnosticType.Syntax | RawDiagnosticType.Compiler),
                CreateRawDiagnosticsTaggerProvider(RawDiagnosticType.Syntax | RawDiagnosticType.Analyzer),
                CreateRawDiagnosticsTaggerProvider(RawDiagnosticType.Semantic | RawDiagnosticType.Compiler),
                CreateRawDiagnosticsTaggerProvider(RawDiagnosticType.Semantic | RawDiagnosticType.Analyzer));

            return;

            RawDiagnosticsTaggerProvider<TTag> CreateRawDiagnosticsTaggerProvider(RawDiagnosticType diagnosticType)
            {
                return new RawDiagnosticsTaggerProvider<TTag>(
                    this,
                    diagnosticType,
                    threadingContext,
                    diagnosticService,
                    analyzerService,
                    globalOptions,
                    visibilityTracker,
                    listener);
            }
        }

        #region IRawDiagnosticsTaggerProviderCallback

        public abstract IEnumerable<Option2<bool>> Options { get; }
        public abstract bool IsEnabled { get; }
        public abstract bool SupportsDiagnosticMode(DiagnosticMode mode);
        public abstract bool IncludeDiagnostic(DiagnosticData data);
        public abstract ITagSpan<TTag>? CreateTagSpan(Workspace workspace, SnapshotSpan span, DiagnosticData data);

        /// <summary>
        /// Get the <see cref="DiagnosticDataLocation"/> that should have the tag applied to it.
        /// In most cases, this is the <see cref="DiagnosticData.DataLocation"/> but overrides can change it (e.g. unnecessary classifications).
        /// </summary>
        /// <param name="diagnosticData">the diagnostic containing the location(s).</param>
        /// <returns>an array of locations that should have the tag applied.</returns>
        public virtual ImmutableArray<DiagnosticDataLocation> GetLocationsToTag(DiagnosticData diagnosticData)
            => diagnosticData.DataLocation is not null ? ImmutableArray.Create(diagnosticData.DataLocation) : ImmutableArray<DiagnosticDataLocation>.Empty;

        #endregion

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            using var _ = ArrayBuilder<ITagger<TTag>>.GetInstance(out var taggers);
            foreach (var tagProvider in _rawDiagnosticsTaggerProviders)
                taggers.Add(tagProvider.CreateTagger<TTag>(buffer));

            return new AggregateTagger(this, taggers.ToImmutable()) as ITagger<T>;
        }

        private sealed class AggregateTagger : ITagger<TTag>
        {
            private readonly AbstractDiagnosticsTaggerProvider<TTag> _taggerProvider;
            private readonly ImmutableArray<ITagger<TTag>> _taggers;

            public AggregateTagger(
                AbstractDiagnosticsTaggerProvider<TTag> taggerProvider,
                ImmutableArray<ITagger<TTag>> taggers)
            {
                _taggerProvider = taggerProvider;
                _taggers = taggers;
            }

            public event EventHandler<SnapshotSpanEventArgs> TagsChanged
            {
                add
                {
                    foreach (var tagger in _taggers)
                        tagger.TagsChanged += value;
                }

                remove
                {
                    foreach (var tagger in _taggers)
                        tagger.TagsChanged -= value;
                }
            }

            public IEnumerable<ITagSpan<TTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                using var _ = ArrayBuilder<ITagSpan<TTag>>.GetInstance(out var result);

                foreach (var tagger in _taggers)
                    result.AddRange(tagger.GetTags(spans));

                return result.ToImmutable();
            }
        }
    }
}
