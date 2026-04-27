using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace Fluently.TUnit.Assertions;

[Generator]
public class ExtensionGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(PolyfillInput.Provide(context), GeneratePolyfill);
        context.RegisterSourceOutput(ExtensionMethodInput.Provide(context), GenerateExtensions);
    }

    private class PolyfillInput(
        bool hasOverloadResolutionPriorityAttribute,
        bool hasCallerArgumentExpressionAttribute)
    {
        public bool HasOverloadResolutionPriorityAttribute { get; } = hasOverloadResolutionPriorityAttribute;
        public bool HasCallerArgumentExpressionAttribute { get; } = hasCallerArgumentExpressionAttribute;
        public static IncrementalValueProvider<PolyfillInput> Provide(IncrementalGeneratorInitializationContext context)
            => context.CompilationProvider.Select(Convert);

        private static PolyfillInput Convert(Compilation compilation, CancellationToken cancellationToken)
        {
            return new(
                hasOverloadResolutionPriorityAttribute: compilation.HasType("System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute"),
                hasCallerArgumentExpressionAttribute: compilation.HasType("System.Runtime.CompilerServices.CallerArgumentExpressionAttribute"));
        }
    }
    private static void GeneratePolyfill(SourceProductionContext context, PolyfillInput input)
    {
        if (!input.HasOverloadResolutionPriorityAttribute)
        {
            context.AddSource("OverloadResolutionPriorityAttribute.g.cs",
                """
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class OverloadResolutionPriorityAttribute : Attribute
    {
        public OverloadResolutionPriorityAttribute(int priority)
        {
            Priority = priority;
        }
        public int Priority { get; }
    }
}
""");
        }

        if (!input.HasCallerArgumentExpressionAttribute)
        {
            context.AddSource("CallerArgumentExpressionAttribute.g.cs",
            """
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        public string ParameterName { get; }
    }
}
""");
        }
    }
    private class ExtensionMethodInput(Compilation compilation, LanguageVersion languageVersion)
    {
        public Compilation Compilation { get; } = compilation;
        public LanguageVersion LanguageVersion { get; } = languageVersion.MapSpecifiedToEffectiveVersion();
        public bool SupportOverloadResolutionPriority => LanguageVersion >= LanguageVersion.CSharp13;

        public static IncrementalValueProvider<ExtensionMethodInput> Provide(IncrementalGeneratorInitializationContext context)
            => context.CompilationProvider
                .Combine(context.ParseOptionsProvider.Select((opt, ct) => ((CSharpParseOptions)opt).LanguageVersion))
                .Select((input, cancellationToken) => new ExtensionMethodInput(input.Left, input.Right));

        public MemberDeclarationSyntax? ToExtensionMethod(IMethodSymbol method)
        {
            return new AssertMethodsConverter(Compilation, SupportOverloadResolutionPriority).ToExtensionMethod(method);
        }
    }

    private static void GenerateExtensions(SourceProductionContext context, ExtensionMethodInput input)
    {
        var assertClass = input.Compilation.GetTypeByMetadataName("TUnit.Assertions.Assert");
        if (assertClass is null)
            return;

        if (!input.SupportOverloadResolutionPriority)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "FluTUnit0001",
                    "Unsupported C# language version",
                    "The specified C# language version is not supported. C# 13 or higher is required.",
                    "Fluently.TUnit.Assertions",
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true),
                Location.None));
        }

        const string generatedNamespase = $"FluentlyTUnit";
        var sb = CompilationUnit()
            .AddUsings(
                UsingDirective(IdentifierName(generatedNamespase))
                .WithGlobalKeyword(Token(SyntaxKind.GlobalKeyword))
                .WithTrailingTrivia(TriviaList(Trivia(NullableDirectiveTrivia(Token(SyntaxKind.EnableKeyword), true))))
            )
            .AddMembers(
                NamespaceDeclaration(IdentifierName(generatedNamespase)).AddMembers(
                    ClassDeclaration(Identifier("FluentlyAssertExtensons"))
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithMembers(List(
                        assertClass.GetMembers().OfType<IMethodSymbol>()
                        .Select(input.ToExtensionMethod)
                        .OfType<MemberDeclarationSyntax>()
                    ))
                    .AddAttributeLists(AttributeList(SingletonSeparatedList(
                        Attribute(IdentifierName("global::System.ComponentModel.EditorBrowsable"))
                        .WithArgumentList(
                            AttributeArgumentList(SingletonSeparatedList(
                                AttributeArgument(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("global::System.ComponentModel.EditorBrowsableState"),
                                        IdentifierName("Never")
                                    )
                                )
                            ))
                        )
                    )))
                )
            );

        var source = sb.NormalizeWhitespace(eol: "\n").ToFullString();
        context.AddSource("FluentlyAssertExtensons.g.cs", source);
    }
}
