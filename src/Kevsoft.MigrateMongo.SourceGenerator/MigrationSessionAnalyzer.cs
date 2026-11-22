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

        if (SessionIsNullInBranch(context, invocation, migration, migrationInterface))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingMigrationSession, invocation.GetLocation(), method.Name));
    }

    private static bool HasSessionOverload(IMethodSymbol method)
    {
        method = method.ReducedFrom ?? method;
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

    private static bool SessionIsNullInBranch(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol migration,
        INamedTypeSymbol migrationInterface)
    {
        var methodDeclaration = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDeclaration is null
            || context.SemanticModel.GetDeclaredSymbol(methodDeclaration, context.CancellationToken) is not IMethodSymbol method
            || !migrationInterface.GetMembers().OfType<IMethodSymbol>().Any(member =>
                SymbolEqualityComparer.Default.Equals(migration.FindImplementationForInterfaceMember(member), method)))
        {
            return false;
        }

        var migrationContext = context.SemanticModel.Compilation.GetTypeByMetadataName("Kevsoft.MigrateMongo.MigrationContext");
        var parameter = method.Parameters.FirstOrDefault(p => SymbolEqualityComparer.Default.Equals(p.Type, migrationContext));
        var session = migrationContext?.GetMembers("Session").OfType<IPropertySymbol>().FirstOrDefault();
        if (parameter is null || session is null)
        {
            return false;
        }

        if (methodDeclaration.Body is not { } body)
        {
            return false;
        }

        var methodFlow = context.SemanticModel.AnalyzeDataFlow(body);
        if (methodFlow is not { Succeeded: true }
            || methodFlow.WrittenInside.Any(s => SymbolEqualityComparer.Default.Equals(s, parameter)))
        {
            return false;
        }

        foreach (var guard in invocation.Ancestors().OfType<IfStatementSyntax>())
        {
            var inTrueBranch = guard.Statement.Span.Contains(invocation.Span);
            var branch = inTrueBranch ? guard.Statement : guard.Else?.Statement;
            if (branch is null || !branch.Span.Contains(invocation.Span)
                || !ImpliesNullSession(guard.Condition, inTrueBranch, context.SemanticModel, parameter, session, context.CancellationToken))
            {
                continue;
            }

            var conditionFlow = context.SemanticModel.AnalyzeDataFlow(guard.Condition);
            var branchFlow = context.SemanticModel.AnalyzeDataFlow(branch);
            if (conditionFlow is { Succeeded: true } && branchFlow is { Succeeded: true }
                && !conditionFlow.WrittenInside.Any(s => SymbolEqualityComparer.Default.Equals(s, parameter))
                && !branchFlow.WrittenInside.Any(s => SymbolEqualityComparer.Default.Equals(s, parameter)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImpliesNullSession(
        ExpressionSyntax condition,
        bool whenTrue,
        SemanticModel model,
        IParameterSymbol parameter,
        IPropertySymbol session,
        System.Threading.CancellationToken cancellationToken)
    {
        if (condition is ParenthesizedExpressionSyntax parenthesized)
        {
            return ImpliesNullSession(parenthesized.Expression, whenTrue, model, parameter, session, cancellationToken);
        }

        if (condition is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return ImpliesNullSession(unary.Operand, !whenTrue, model, parameter, session, cancellationToken);
        }

        if (condition is BinaryExpressionSyntax binary)
        {
            if (whenTrue && binary.IsKind(SyntaxKind.LogicalAndExpression)
                || !whenTrue && binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return ImpliesNullSession(binary.Left, whenTrue, model, parameter, session, cancellationToken)
                    || ImpliesNullSession(binary.Right, whenTrue, model, parameter, session, cancellationToken);
            }

            if ((whenTrue && binary.IsKind(SyntaxKind.EqualsExpression)
                || !whenTrue && binary.IsKind(SyntaxKind.NotEqualsExpression))
                && (binary.Left.IsKind(SyntaxKind.NullLiteralExpression)
                    && IsSessionOf(binary.Right, model, parameter, session, cancellationToken)
                    || binary.Right.IsKind(SyntaxKind.NullLiteralExpression)
                    && IsSessionOf(binary.Left, model, parameter, session, cancellationToken)))
            {
                return true;
            }
        }

        if (condition is IsPatternExpressionSyntax pattern
            && IsSessionOf(pattern.Expression, model, parameter, session, cancellationToken))
        {
            var matchingPattern = pattern.Pattern;
            if (matchingPattern is UnaryPatternSyntax notPattern && notPattern.IsKind(SyntaxKind.NotPattern))
            {
                whenTrue = !whenTrue;
                matchingPattern = notPattern.Pattern;
            }

            return whenTrue && matchingPattern is ConstantPatternSyntax constant
                && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
        }

        return false;
    }

    private static bool IsSessionOf(
        ExpressionSyntax expression,
        SemanticModel model,
        IParameterSymbol parameter,
        IPropertySymbol session,
        System.Threading.CancellationToken cancellationToken)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression is MemberAccessExpressionSyntax member
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member, cancellationToken).Symbol, session)
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression, cancellationToken).Symbol, parameter);
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
