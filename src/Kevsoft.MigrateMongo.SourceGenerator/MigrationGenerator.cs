using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kevsoft.MigrateMongo.SourceGenerator;

/// <summary>
/// Discovers <c>[Migration]</c> classes at compile time and emits a reflection-free migration source plus a
/// module initializer that registers it with <c>MigrationRegistry</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MigrationGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Kevsoft.MigrateMongo.MigrationAttribute";
    private const string MigrationInterface = "Kevsoft.MigrateMongo.IMigration";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var migrations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => Transform(ctx, ct))
            .Collect();

        context.RegisterSourceOutput(migrations, static (spc, items) => Emit(spc, items));
    }

    private static MigrationModel Transform(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var syntax = (ClassDeclarationSyntax)ctx.TargetNode;
        var attribute = ctx.Attributes[0];
        var location = syntax.Identifier.GetLocation();

        var id = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        var name = attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string : null;
        var transaction = 0;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Transaction" && named.Value.Value is int value)
            {
                transaction = value;
            }
        }

        var implementsInterface = symbol.AllInterfaces.Any(i => i.ToDisplayString() == MigrationInterface);
        var accessible = IsAccessible(symbol);

        // Choose the public constructor with the most parameters (parameterless if that's all there is).
        var ctor = symbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        var parameters = ctor is null
            ? ImmutableArray<string>.Empty
            : ctor.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToImmutableArray();

        ct.ThrowIfCancellationRequested();

        return new MigrationModel(
            Id: id ?? string.Empty,
            Name: name ?? string.Empty,
            TypeName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            DisplayName: symbol.ToDisplayString(),
            Transaction: transaction,
            Checksum: ComputeChecksum(syntax),
            Parameters: new EquatableArray<string>(parameters),
            ImplementsInterface: implementsInterface,
            IsAbstract: symbol.IsAbstract,
            IsAccessible: accessible,
            HasConstructor: ctor is not null,
            Location: location);
    }

    private static bool IsAccessible(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SHA-256 over the class's tokens joined by single spaces, so whitespace and comment changes don't alter
    /// the checksum but any code change does.
    /// </summary>
    internal static string ComputeChecksum(ClassDeclarationSyntax syntax)
    {
        var normalised = string.Join(" ", syntax.DescendantTokens().Select(t => t.Text));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalised));
        var builder = new StringBuilder("sha256:", 7 + (hash.Length * 2));
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<MigrationModel> items)
    {
        var valid = new List<MigrationModel>();
        foreach (var item in items)
        {
            var ok = true;
            if (!item.ImplementsInterface)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.MustImplementInterface, item.Location, item.DisplayName));
                ok = false;
            }

            if (!IsValidId(item.Id))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.InvalidId, item.Location, item.Id, item.DisplayName));
                ok = false;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingName, item.Location, item.DisplayName));
                ok = false;
            }

            if (item.IsAbstract || !item.IsAccessible || !item.HasConstructor)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotConstructible, item.Location, item.DisplayName));
                ok = false;
            }

            if (ok)
            {
                valid.Add(item);
            }
        }

        foreach (var group in valid.GroupBy(m => m.Id).Where(g => g.Count() > 1))
        {
            foreach (var duplicate in group)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DuplicateId,
                    duplicate.Location,
                    duplicate.Id,
                    string.Join(", ", group.Select(g => g.DisplayName))));
            }
        }

        if (items.Length == 0)
        {
            return;
        }

        spc.AddSource("MigrateMongo.Migrations.g.cs", Render(valid.OrderBy(m => m.Id, StringComparer.Ordinal).ToList()));
    }

    private static bool IsValidId(string id) => id.Length == 14 && id.All(c => c is >= '0' and <= '9');

    private static string Render(IReadOnlyList<MigrationModel> migrations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("namespace Kevsoft.MigrateMongo.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Kevsoft.MigrateMongo.SourceGenerator\", \"1.0\")]");
        sb.AppendLine("    internal sealed class GeneratedMigrationSource : global::Kevsoft.MigrateMongo.IMigrationSource");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::Kevsoft.MigrateMongo.MigrationDescriptor[] Migrations = new global::Kevsoft.MigrateMongo.MigrationDescriptor[]");
        sb.AppendLine("        {");
        foreach (var m in migrations)
        {
            sb.Append("            new global::Kevsoft.MigrateMongo.MigrationDescriptor(")
                .Append(Literal(m.Id)).Append(", ")
                .Append(Literal(m.Name)).Append(", ")
                .Append("\"csharp\", ")
                .Append(Literal(m.DisplayName)).Append(", ")
                .Append(Literal(m.Checksum)).Append(", ")
                .Append("(global::Kevsoft.MigrateMongo.TransactionMode)").Append(m.Transaction).Append(", ")
                .Append("static sp => new ").Append(m.TypeName).Append('(');
            for (var i = 0; i < m.Parameters.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append("Resolve<").Append(m.Parameters[i]).Append(">(sp, ").Append(Literal(m.DisplayName)).Append(')');
            }

            sb.AppendLine(")),");
        }

        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        public global::System.Collections.Generic.IReadOnlyCollection<global::Kevsoft.MigrateMongo.MigrationDescriptor> GetMigrations() => Migrations;");
        sb.AppendLine();
        sb.AppendLine("        private static T Resolve<T>(global::System.IServiceProvider sp, string migration)");
        sb.AppendLine("            => (T?)sp.GetService(typeof(T)) ?? throw new global::System.InvalidOperationException(");
        sb.AppendLine("                $\"Unable to resolve service '{typeof(T)}' for migration '{migration}'. Register it in the runner's service configuration.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static class GeneratedMigrationSourceRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("            => global::Kevsoft.MigrateMongo.MigrationRegistry.Register(typeof(GeneratedMigrationSource).Assembly, new GeneratedMigrationSource());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
