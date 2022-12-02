﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.CSharpUseNameofInNullableAttribute;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class CSharpUseNameofInNullableAttributeDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
{
    public const string NameKey = nameof(NameKey);

    public CSharpUseNameofInNullableAttributeDiagnosticAnalyzer()
        : base(IDEDiagnosticIds.UseNameofInNullableAttributeDiagnosticId,
               EnforceOnBuildValues.UseNameofInNullableAttribute,
               option: null,
               new LocalizableResourceString(
                   nameof(CSharpAnalyzersResources.Use_nameof), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources)))
    {
    }

    public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
        => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

    protected override void InitializeWorker(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
    }

    private void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var cancellationToken = context.CancellationToken;
        var attribute = (AttributeSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        if (attribute.ArgumentList is null)
            return;

        if (attribute.Name.GetRightmostName()?.Identifier.ValueText
                is not nameof(NotNullIfNotNullAttribute)
                and not nameof(MemberNotNullAttribute)
                and not nameof(MemberNotNullWhenAttribute))
        {
            return;
        }

        INamedTypeSymbol? containingType = null;
        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.Expression is not LiteralExpressionSyntax(SyntaxKind.StringLiteralExpression) and not InterpolatedStringExpressionSyntax)
                continue;

            var constantValue = semanticModel.GetConstantValue(argument.Expression, cancellationToken);
            if (constantValue.Value is not string stringValue)
                continue;

            var position = argument.Expression.SpanStart;
            containingType ??= semanticModel.GetEnclosingNamedType(position, cancellationToken);
            if (containingType is null)
                return;

            var symbols = semanticModel.LookupSymbols(argument.Expression.SpanStart, name: stringValue);
            if (symbols.Any(s => s.IsAccessibleWithin(containingType)))
            {
                context.ReportDiagnostic(DiagnosticHelper.Create(
                    this.Descriptor,
                    argument.Expression.GetLocation(),
                    ReportDiagnostic.Info,
                    additionalLocations: null,
                    ImmutableDictionary<string, string?>.Empty.Add(NameKey, stringValue)));
            }
        }
    }
}
