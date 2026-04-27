using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace Fluently.TUnit.Assertions;

internal class AssertMethodsConverter(Compilation compilation, bool supportOverloadResolutionPriority)
{
    private static bool Acceptable(IMethodSymbol method)
    {
        if (method is { Parameters.Length: 0 } or { DeclaredAccessibility: not Accessibility.Public } or { Name: "Equals" or "ReferenceEquals" })
            return false;

        var lastParam = method.Parameters[method.Parameters.Length - 1];
        if (lastParam.Type.SpecialType != SpecialType.System_String)
            return false;

        return lastParam.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute");
    }
    public MemberDeclarationSyntax? ToExtensionMethod(IMethodSymbol method)
    {
        if (!Acceptable(method)) return null;

        TypeParameterListSyntax? typeParameters = null;
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses = default;

        if (method.TypeParameters.Length > 0)
        {
            typeParameters = TypeParameterList(SeparatedList(method.TypeParameters.Select(tp => TypeParameter(Identifier(tp.Name)))));
            constraintClauses = [.. method.TypeParameters
            .Select(ToTypeParameterConstraintClauseSyntax)
            .OfType<TypeParameterConstraintClauseSyntax>()];
        }

        var returnType = ToTypeSyntax(method.ReturnType);
        var parameters = method.Parameters.Select(ToParameterSyntax).ToArray();
        var attributes = method.GetAttributes()
            .Select(ToAttributeListSyntax)
            .OfType<AttributeListSyntax>();
        var returnAttributes = method.GetReturnTypeAttributes()
            .Select(ToAttributeListSyntax)
            .OfType<AttributeListSyntax>()
            .Select(a => a.WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.ReturnKeyword))));

        if (parameters[0].Default == null)
            parameters[0] = parameters[0].AddModifiers(Token(SyntaxKind.ThisKeyword));

        var invocation = InvocationExpression(MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                ToTypeSyntax(method.ContainingType),
                IdentifierName(method.Name)
                )
            )
            .WithArgumentList(ArgumentList(SeparatedList(parameters.Select(p => Argument(IdentifierName(p.Identifier.Text))))));

        return MethodDeclaration(returnType, ConvertMethodName(method.Name))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
            .WithTypeParameterList(typeParameters)
            .WithConstraintClauses(constraintClauses)
            .WithParameterList(ParameterList([.. parameters]))
            .WithAttributeLists([.. attributes, .. returnAttributes])
            .WithExpressionBody(ArrowExpressionClause(invocation))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    private static string ConvertMethodName(string name) => name switch
    {
        "That" => "Should",
        _ => $"Should{name}",
    };

    private static readonly SymbolDisplayFormat FullyQualifiedFormatNullable = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
        SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly SymbolDisplayFormat ParameterFormat = FullyQualifiedFormatNullable
        .WithParameterOptions(
            SymbolDisplayParameterOptions.IncludeModifiers |
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue
        );
    public static TypeSyntax ToTypeSyntax(ITypeSymbol type)
        => ParseTypeName(type.ToDisplayString(FullyQualifiedFormatNullable));

    public ParameterSyntax ToParameterSyntax(IParameterSymbol parameter)
    {
        var displayString = parameter.ToDisplayString(ParameterFormat);
        var attributes = parameter.GetAttributes()
            .Select(ToAttributeListSyntax)
            .OfType<AttributeListSyntax>();
        var syntax = ParseParameterList(displayString).Parameters[0]
            .WithAttributeLists(List(attributes));

        if (parameter.Type.ToDisplayString() == "TUnit.Assertions.StringValue")
        {
            syntax = syntax.WithType(IdentifierName("string?"));
        }

        return syntax;
    }

    public AttributeListSyntax? ToAttributeListSyntax(AttributeData attribute)
    {
        var attributeClass = attribute.AttributeClass;
        switch (attributeClass?.ToDisplayString())
        {
            case "System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute":
                if (!supportOverloadResolutionPriority)
                    return null;
                break;
            case "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute":
                break;
            case null:
            case var attributeClassName when attributeClassName.StartsWith("System.Runtime.CompilerServices.") || !compilation.HasType(attributeClassName):
                return null;
        }

        var name = ToTypeSyntax(attributeClass);

        return AttributeList(SingletonSeparatedList(
            Attribute((NameSyntax)name)
            .WithArgumentList(AttributeArgumentList(SeparatedList([
                .. attribute.ConstructorArguments.Select(ToConstExpression).Select(AttributeArgument),
                .. attribute.NamedArguments.Select(kv=> AttributeArgument(NameEquals(IdentifierName(kv.Key)), null,ToConstExpression(kv.Value))),
            ])))
        ));
    }


    public static ExpressionSyntax ToConstExpression(TypedConstant arg)
    {
        if (arg.Kind == TypedConstantKind.Enum)
        {
            return CastExpression(ToTypeSyntax(arg.Type!), LiteralExpression(SyntaxKind.NumericLiteralExpression, ToLiteralToken(arg.Value)));
        }
        if (arg.Kind == TypedConstantKind.Primitive)
            return arg.Value switch
            {
                null => LiteralExpression(SyntaxKind.NullLiteralExpression),
                string s => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(s)),
                char c => LiteralExpression(SyntaxKind.CharacterLiteralExpression, Literal(c)),
                bool b => LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
                float
                or double
                or decimal
                or int
                or long
                or uint
                or ulong
                or byte
                or sbyte
                or short
                or ushort => LiteralExpression(SyntaxKind.NumericLiteralExpression, ToLiteralToken(arg.Value)),
                _ => throw new NotSupportedException($"Type {arg.Type} is not supported as a literal."),
            };
        if (arg.Kind == TypedConstantKind.Type)
            return TypeOfExpression(ParseTypeName(arg.Value!.ToString()!));
        return ArrayCreationExpression(
            ArrayType(ParseTypeName(arg.Type!.ToDisplayString(FullyQualifiedFormatNullable)))
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier())))
        .WithInitializer(InitializerExpression(SyntaxKind.ArrayInitializerExpression, SeparatedList(arg.Values.Select(ToConstExpression))));
    }

    static SyntaxToken ToLiteralToken(object? value)
    {
        return value switch
        {
            string s => Literal(s),
            char c => Literal(c),
            float f => Literal(f),
            double d => Literal(d),
            decimal m => Literal(m),
            int i => Literal(i),
            long l => Literal(l),
            uint i => Literal(i),
            ulong l => Literal(l),
            byte b => Literal(b),
            sbyte b => Literal(b),
            short s => Literal(s),
            ushort s => Literal(s),
            _ => throw new NotSupportedException($"{value} is not supported as a literal."),
        };
    }

    public static TypeParameterConstraintClauseSyntax? ToTypeParameterConstraintClauseSyntax(ITypeParameterSymbol typeParameter)
    {
        var constraints = new List<TypeParameterConstraintSyntax>();
        if (typeParameter.HasReferenceTypeConstraint)
        {
            constraints.Add(ClassOrStructConstraint(SyntaxKind.ClassConstraint));
        }
        else if (typeParameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add(TypeConstraint(IdentifierName("unmanaged")));
        }
        else if (typeParameter.HasValueTypeConstraint)
        {
            constraints.Add(ClassOrStructConstraint(SyntaxKind.StructConstraint));
        }
        else if (typeParameter.HasNotNullConstraint)
        {
            constraints.Add(TypeConstraint(IdentifierName("notnull")));
        }

        foreach (var type in typeParameter.ConstraintTypes)
        {
            constraints.Add(TypeConstraint(ToTypeSyntax(type)));
        }

        if (typeParameter.HasConstructorConstraint)
            constraints.Add(ConstructorConstraint());

        if (typeParameter.AllowsRefLikeType)
        {
            constraints.Add(AllowsConstraintClause([RefStructConstraint()]));
        }
        if (constraints.Count == 0)
            return null;

        return TypeParameterConstraintClause(typeParameter.Name).WithConstraints(SeparatedList(constraints));
    }
}
