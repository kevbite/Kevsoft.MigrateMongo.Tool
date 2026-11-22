using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Kevsoft.MigrateMongo.SourceGenerator;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MigrationSessionAnalyzer : DiagnosticAnalyzer
{
    private const string MigrationAttribute = "Kevsoft.MigrateMongo.MigrationAttribute";
    private const string MigrationInterface = "Kevsoft.MigrateMongo.IMigration";
    private const string TransactionMode = "Kevsoft.MigrateMongo.TransactionMode";
    private const string CollectionInterface = "IMongoCollection`1";
    private const string NonGenericCollectionInterface = "IMongoCollection";
    private const string DatabaseInterface = "IMongoDatabase";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.MissingMigrationSession);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var migrationAttribute = startContext.Compilation.GetTypeByMetadataName(MigrationAttribute);
            var migrationInterface = startContext.Compilation.GetTypeByMetadataName(MigrationInterface);
            var never = startContext.Compilation.GetTypeByMetadataName(TransactionMode)?
                .GetMembers("Never")
                .OfType<IFieldSymbol>()
                .FirstOrDefault()?.ConstantValue;

            if (migrationAttribute is null || migrationInterface is null || never is null)
            {
                return;
            }

            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeInvocation(nodeContext, migrationAttribute, migrationInterface, never),
                SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol migrationAttribute,
        INamedTypeSymbol migrationInterface,
        object never)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.Parameters.Any(parameter => IsSession(parameter.Type))
            || !HasSessionOverload(method))
        {
            return;
        }

        var migrationDeclaration = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (migrationDeclaration is null
            || context.SemanticModel.GetDeclaredSymbol(migrationDeclaration, context.CancellationToken) is not INamedTypeSymbol migration
            || !migration.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, migrationInterface)))
        {
            return;
        }

        var attribute = migration.GetAttributes().FirstOrDefault(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, migrationAttribute));
        if (attribute is null || attribute.NamedArguments.Any(pair => pair.Key == "Transaction" && Equals(pair.Value.Value, never)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingMigrationSession, invocation.GetLocation(), method.Name));
    }

    private static bool HasSessionOverload(IMethodSymbol method)
    {
        if (IsMongoDatabase(method.ContainingType) || IsMongoCollection(method.ContainingType))
        {
            return HasSessionOverload(method.ContainingType, method.Name);
        }

        if (method.IsExtensionMethod
            && method.Parameters.Length > 0
            && (IsMongoDatabase(method.Parameters[0].Type) || IsMongoCollection(method.Parameters[0].Type)))
        {
            return HasSessionOverload(method.ContainingType, method.Name);
        }

        return false;
    }

    private static bool HasSessionOverload(INamedTypeSymbol type, string methodName) =>
        type.AllInterfaces
            .Prepend(type)
            .SelectMany(candidateType => candidateType.GetMembers(methodName))
            .OfType<IMethodSymbol>()
            .Any(candidate => candidate.Parameters.Any(parameter => IsSession(parameter.Type)));

    private static bool IsSession(ITypeSymbol type) =>
        type is INamedTypeSymbol named
        && named.Name == "IClientSessionHandle"
        && named.ContainingNamespace.ToDisplayString() == "MongoDB.Driver";

    private static bool IsMongoDatabase(ITypeSymbol type) =>
        type is INamedTypeSymbol named
        && named.Name == DatabaseInterface
        && named.ContainingNamespace.ToDisplayString() == "MongoDB.Driver";

    private static bool IsMongoCollection(ITypeSymbol type) =>
        type is INamedTypeSymbol named
        && named.ContainingNamespace.ToDisplayString() == "MongoDB.Driver"
        && named.MetadataName is CollectionInterface or NonGenericCollectionInterface;
}
