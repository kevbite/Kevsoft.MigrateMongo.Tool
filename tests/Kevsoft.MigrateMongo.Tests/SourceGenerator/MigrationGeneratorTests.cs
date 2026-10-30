using System.Reflection;
using System.Runtime.Loader;
using Kevsoft.MigrateMongo.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.SourceGenerator;

public sealed class MigrationGeneratorTests
{
    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using Kevsoft.MigrateMongo;

        """;

    private const string Body = """
            public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        """;

    [Fact]
    public void Valid_migrations_produce_no_diagnostics_and_are_registered_in_id_order()
    {
        var (diagnostics, assembly) = Run($$"""
            {{Usings}}
            namespace App;
            public sealed class Greeter { }

            [Migration("20260101000002", "second", Transaction = TransactionMode.Never)]
            public sealed class Second : IMigration
            {
                public Second(Greeter greeter) { }
            {{Body}}
            }

            [Migration("20260101000001", "first")]
            internal sealed class First : IMigration
            {
            {{Body}}
            }
            """);

        diagnostics.ShouldBeEmpty();
        var migrations = MigrationRegistry.ForAssembly(assembly!)!.GetMigrations();
        migrations.Select(m => (m.Id, m.Name, m.Origin, m.Transaction)).ShouldBe([
            ("20260101000001", "first", "App.First", TransactionMode.Auto),
            ("20260101000002", "second", "App.Second", TransactionMode.Never),
        ]);
        migrations.ShouldAllBe(m => m.Source == "csharp" && m.Checksum.StartsWith("sha256:") && m.Checksum.Length == 71);
    }

    [Fact]
    public void Generated_factory_resolves_constructor_dependencies_from_services()
    {
        var (_, assembly) = Run($$"""
            {{Usings}}
            namespace App;
            public sealed class Greeter { }

            [Migration("20260101000001", "needs-greeter")]
            public sealed class NeedsGreeter : IMigration
            {
                public NeedsGreeter(Greeter greeter) => Greeter = greeter;
                public Greeter Greeter { get; }
            {{Body}}
            }
            """);

        var descriptor = MigrationRegistry.ForAssembly(assembly!)!.GetMigrations().Single();
        var greeterType = assembly!.GetType("App.Greeter")!;
        var greeter = Activator.CreateInstance(greeterType)!;

        var instance = descriptor.Create(new Services(new() { [greeterType] = greeter }));
        instance.GetType().GetProperty("Greeter")!.GetValue(instance).ShouldBeSameAs(greeter);

        Should.Throw<InvalidOperationException>(() => descriptor.Create(new Services([]))).Message.ShouldContain("App.Greeter");
    }

    [Fact]
    public void Records_are_migrations_too()
    {
        var (diagnostics, assembly) = Run($$"""
            {{Usings}}
            namespace App;
            public sealed class Greeter { }

            [Migration("20260101000001", "positional")]
            public sealed record Positional(Greeter Greeter) : IMigration
            {
            {{Body}}
            }

            [Migration("20260101000002", "record-class")]
            internal record class Plain : IMigration
            {
            {{Body}}
            }
            """);

        diagnostics.ShouldBeEmpty();
        var migrations = MigrationRegistry.ForAssembly(assembly!)!.GetMigrations();
        migrations.Select(m => (m.Id, m.Name, m.Origin)).ShouldBe([
            ("20260101000001", "positional", "App.Positional"),
            ("20260101000002", "record-class", "App.Plain"),
        ]);

        // The primary constructor's parameters are resolved from services, not the record's copy constructor.
        var greeterType = assembly!.GetType("App.Greeter")!;
        var greeter = Activator.CreateInstance(greeterType)!;
        var instance = migrations.First().Create(new Services(new() { [greeterType] = greeter }));
        instance.GetType().GetProperty("Greeter")!.GetValue(instance).ShouldBeSameAs(greeter);
    }

    [Theory]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public sealed class A { }", "MM0003")]
    [InlineData("[Migration(\"2026\", \"a\")] public sealed class A : IMigration { " + Body + " }", "MM0002")]
    [InlineData("[Migration(\"2026010100000x\", \"a\")] public sealed class A : IMigration { " + Body + " }", "MM0002")]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public abstract class A : IMigration { " + Body + " }", "MM0004")]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public sealed class A : IMigration { private A() { } " + Body + " }", "MM0004")]
    [InlineData("[Migration(\"20260101000001\", \" \")] public sealed class A : IMigration { " + Body + " }", "MM0005")]
    [InlineData("[Migration(\"2026\", \"a\")] public sealed record A : IMigration { " + Body + " }", "MM0002")]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public sealed record A;", "MM0003")]
    public void Invalid_migrations_are_reported(string declaration, string expectedId)
    {
        var (diagnostics, _) = Run(Usings + "namespace App;\n" + declaration);

        diagnostics.Select(d => d.Id).ShouldBe([expectedId]);
        diagnostics.Single().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostics_point_at_the_migrations_name()
    {
        var source = Usings + "namespace App;\n[Migration(\"20260101000001\", \"a\")]\npublic sealed class Broken { }\n";

        var (diagnostics, _) = Run(source);

        var location = diagnostics.Single().Location;
        source.Substring(location.SourceSpan.Start, location.SourceSpan.Length).ShouldBe("Broken");
        var span = location.GetLineSpan().Span;
        source.ReplaceLineEndings("\n").Split('\n')[span.Start.Line][span.Start.Character..span.End.Character].ShouldBe("Broken");
    }

    [Fact]
    public void Editing_a_migrations_file_without_changing_the_migration_reuses_the_generated_source()
    {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var source = Usings + "namespace App;\n[Migration(\"20260101000001\", \"a\")] public sealed class A : IMigration { " + Body + " }\n";
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, "M20260101000001_A.cs", cancellationToken: ct);
        var compilation = CreateCompilation(tree);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new MigrationGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, ct);

        // The edit gives the file a new syntax tree, but the migration and its position are unchanged.
        var edited = CSharpSyntaxTree.ParseText(source + "// A comment after the migration.\n", parseOptions, tree.FilePath, cancellationToken: ct);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, edited), ct);

        var output = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs);
        output.ShouldNotBeEmpty();
        output.ShouldAllBe(o => o.Reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void Duplicate_ids_are_reported_on_every_duplicate()
    {
        var (diagnostics, _) = Run($$"""
            {{Usings}}
            namespace App;
            [Migration("20260101000001", "a")] public sealed class A : IMigration { {{Body}} }
            [Migration("20260101000001", "b")] public sealed class B : IMigration { {{Body}} }
            """);

        diagnostics.Select(d => d.Id).ShouldBe(["MM0001", "MM0001"]);
    }

    [Fact]
    public void Checksum_ignores_whitespace_and_comments_but_not_code()
    {
        var original = Checksum("""
            [Migration("20260101000001", "a")]
            public sealed class A : IMigration
            {
                public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);
        var reformatted = Checksum("""
            // Explains the migration.
            [Migration("20260101000001", "a")]
            public sealed class A : IMigration {
                /* up */ public Task UpAsync(MigrationContext context,   CancellationToken cancellationToken)
                    => Task.CompletedTask;

                public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask; // down
            }
            """);
        var changed = Checksum("""
            [Migration("20260101000001", "a")]
            public sealed class A : IMigration
            {
                public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => Task.Delay(1);
                public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        reformatted.ShouldBe(original);
        changed.ShouldNotBe(original);
    }

    private static string Checksum(string declaration)
    {
        var (diagnostics, assembly) = Run(Usings + "namespace App;\n" + declaration);
        diagnostics.ShouldBeEmpty();
        return MigrationRegistry.ForAssembly(assembly!)!.GetMigrations().Single().Checksum;
    }

    private static CSharpCompilation CreateCompilation(params SyntaxTree[] trees)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IMigration).Assembly.Location))
            .ToList();

        return CSharpCompilation.Create(
            "Generated_" + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static (IReadOnlyList<Diagnostic> GeneratorDiagnostics, Assembly? Assembly) Run(string source)
    {
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)));

        CSharpGeneratorDriver.Create(new MigrationGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        if (generatorDiagnostics.Length > 0)
        {
            return (generatorDiagnostics, null);
        }

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        emit.Success.ShouldBeTrue(string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = AssemblyLoadContext.Default;
        return ([], context.LoadFromStream(stream));
    }

    private sealed class Services(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }
}
