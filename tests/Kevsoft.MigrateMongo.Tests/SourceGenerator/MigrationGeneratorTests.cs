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

    [Theory]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public sealed class A { }", "MM0003")]
    [InlineData("[Migration(\"2026\", \"a\")] public sealed class A : IMigration { " + Body + " }", "MM0002")]
    [InlineData("[Migration(\"2026010100000x\", \"a\")] public sealed class A : IMigration { " + Body + " }", "MM0002")]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public abstract class A : IMigration { " + Body + " }", "MM0004")]
    [InlineData("[Migration(\"20260101000001\", \"a\")] public sealed class A : IMigration { private A() { } " + Body + " }", "MM0004")]
    [InlineData("[Migration(\"20260101000001\", \" \")] public sealed class A : IMigration { " + Body + " }", "MM0005")]
    public void Invalid_migrations_are_reported(string declaration, string expectedId)
    {
        var (diagnostics, _) = Run(Usings + "namespace App;\n" + declaration);

        diagnostics.Select(d => d.Id).ShouldBe([expectedId]);
        diagnostics.Single().Severity.ShouldBe(DiagnosticSeverity.Error);
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
    public void Collection_definitions_are_registered_by_name_alongside_migrations()
    {
        var (diagnostics, assembly) = Run($$"""
            {{Usings}}
            using MongoDB.Bson;
            using MongoDB.Driver;
            namespace App;
            public sealed class Greeter { }

            [ManagedCollection("users")]
            public sealed class Users : ICollectionDefinition<BsonDocument>
            {
                public Users(Greeter greeter) { }
                public void Define(CollectionBuilder<BsonDocument> collection) =>
                    collection.Indexes.Add(k => k.Ascending("email"), new CreateIndexOptions<BsonDocument> { Unique = true });
            }

            [ManagedCollection("orders")]
            internal sealed class Orders : ICollectionDefinition<BsonDocument>
            {
                public void Define(CollectionBuilder<BsonDocument> collection) => collection.Indexes.Add(k => k.Ascending("status"));
            }
            """);

        diagnostics.ShouldBeEmpty();
        var source = MigrationRegistry.ForAssembly(assembly!)!;
        source.GetMigrations().ShouldBeEmpty();
        var collections = source.GetCollections();
        collections.Select(c => (c.Name, c.Source, c.Origin)).ShouldBe([("orders", "csharp", "App.Orders"), ("users", "csharp", "App.Users")]);

        var greeterType = assembly!.GetType("App.Greeter")!;
        var database = new MongoDB.Driver.MongoClient("mongodb://localhost:27017").GetDatabase("generated");
        var users = collections.Single(c => c.Name == "users").Declare(new Services(new() { [greeterType] = Activator.CreateInstance(greeterType)! }), database);
        users.Indexes.ShouldHaveSingleItem().Options.ShouldBe(new MongoDB.Bson.BsonDocument("unique", true));
        Should.Throw<InvalidOperationException>(() => collections.Single(c => c.Name == "users").Declare(new Services([]), database))
            .Message.ShouldBe("Unable to resolve service 'App.Greeter' for collection definition 'App.Users'. Register it in the runner's service configuration.");
    }

    [Theory]
    [InlineData("[ManagedCollection(\"users\")] public sealed class A { }", "MM0006")]
    [InlineData("[ManagedCollection(\"\")] public sealed class A : ICollectionDefinition<BsonDocument> { " + DefineBody + " }", "MM0007")]
    [InlineData("[ManagedCollection(\"system.users\")] public sealed class A : ICollectionDefinition<BsonDocument> { " + DefineBody + " }", "MM0007")]
    [InlineData("[ManagedCollection(\"a$b\")] public sealed class A : ICollectionDefinition<BsonDocument> { " + DefineBody + " }", "MM0007")]
    [InlineData("[ManagedCollection(\"users\")] public abstract class A : ICollectionDefinition<BsonDocument> { " + DefineBody + " }", "MM0008")]
    public void Invalid_collection_definitions_are_reported(string declaration, string expectedId)
    {
        var (diagnostics, _) = Run(Usings + "using MongoDB.Bson;\nnamespace App;\n" + declaration);

        diagnostics.Select(d => d.Id).ShouldBe([expectedId]);
    }

    [Fact]
    public void A_collection_defined_twice_is_reported_on_both()
    {
        var (diagnostics, _) = Run($$"""
            {{Usings}}
            using MongoDB.Bson;
            namespace App;
            [ManagedCollection("users")] public sealed class A : ICollectionDefinition<BsonDocument> { {{DefineBody}} }
            [ManagedCollection("users")] public sealed class B : ICollectionDefinition<BsonDocument> { {{DefineBody}} }
            """);

        diagnostics.Select(d => d.Id).ShouldBe(["MM0009", "MM0009"]);
    }

    private const string DefineBody = "public void Define(CollectionBuilder<BsonDocument> collection) { }";

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

    private static (IReadOnlyList<Diagnostic> GeneratorDiagnostics, Assembly? Assembly) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IMigration).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Generated_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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
