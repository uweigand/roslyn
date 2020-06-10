﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Runtime.CompilerServices

Namespace Microsoft.CodeAnalysis.VisualBasic.Symbols

    ''' <summary>
    ''' Represents a synthesized lambda. 
    ''' </summary>
    Friend NotInheritable Class SynthesizedLambdaSymbol
        Inherits LambdaSymbol

        Private ReadOnly _kind As SynthesizedLambdaKind

        Public Sub New(
            kind As SynthesizedLambdaKind,
            syntaxNode As SyntaxNode,
            parameters As ImmutableArray(Of BoundLambdaParameterSymbol),
            returnType As TypeSymbol,
            binder As Binder)

            MyBase.New(syntaxNode, parameters, returnType, binder)
            Debug.Assert((returnType Is ReturnTypePendingDelegate) = kind.IsQueryLambda)

            _kind = kind
        End Sub

        Public Overrides ReadOnly Property SynthesizedKind As SynthesizedLambdaKind
            Get
                Return _kind
            End Get
        End Property

        Public Overrides ReadOnly Property IsAsync As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property IsIterator As Boolean
            Get
                Return False
            End Get
        End Property

        ''' <summary>
        ''' Gets a value indicating whether the symbol was generated by the compiler
        ''' rather than declared explicitly.
        ''' </summary>
        Public Overrides ReadOnly Property IsImplicitlyDeclared As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property DeclaringSyntaxReferences As ImmutableArray(Of SyntaxReference)
            Get
                Return ImmutableArray(Of SyntaxReference).Empty
            End Get
        End Property

        Public Overrides Function Equals(obj As Object) As Boolean
            Return obj Is Me
        End Function

        Public Overrides Function GetHashCode() As Integer
            Return RuntimeHelpers.GetHashCode(Me)
        End Function

        Friend Overrides ReadOnly Property GenerateDebugInfoImpl As Boolean
            Get
                ' Delegate relaxation stub contains no user code, only a synthesized call to the target method.
                '
                ' Late-bound AddressOf lambda contains user code, but we don't allow debugging it. 
                ' It shouldn't really contain the code but to be backward compatible we need to replicate Dev12 behavior.
                Return _kind <> SynthesizedLambdaKind.DelegateRelaxationStub AndAlso
                       _kind <> SynthesizedLambdaKind.LateBoundAddressOfLambda
            End Get
        End Property

        Public Sub SetQueryLambdaReturnType(returnType As TypeSymbol)
            Debug.Assert(_kind.IsQueryLambda)
            Debug.Assert(m_ReturnType Is ReturnTypePendingDelegate)

            m_ReturnType = returnType
        End Sub
    End Class
End Namespace
