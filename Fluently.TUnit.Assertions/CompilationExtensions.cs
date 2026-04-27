using Microsoft.CodeAnalysis;

namespace Fluently.TUnit.Assertions;

internal static class CompilationExtensions
{
    extension(Compilation compilation)
    {
        public bool HasType(string metadataName)
        {
            foreach (var s in compilation.GetTypesByMetadataName(metadataName))
                if (IsAccessable(compilation.Assembly, s))
                    return true;
            return false;
        }
        private static bool IsAccessable(IAssemblySymbol assembly, INamedTypeSymbol symbol)
            => symbol.DeclaredAccessibility == Accessibility.Public
                || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, assembly) && symbol.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }
}
