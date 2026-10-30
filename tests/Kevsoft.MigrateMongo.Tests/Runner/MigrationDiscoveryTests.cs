using System.Reflection;
using System.Runtime.Loader;
using Kevsoft.MigrateMongo.Runner;
using Kevsoft.MigrateMongo.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Runner;

public sealed class MigrationDiscoveryTests
{
    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    [InlineData("MongoDB.Driver")]
    [InlineData("MongoDB.Driver.Encryption")]
    [InlineData("MongoDB.Bson")]
    [InlineData("Kevsoft.MigrateMongo.Abstractions")]
    public void Framework_driver_and_library_assemblies_are_skipped(string name) =>
        MigrationDiscovery.IsSkipped(name).ShouldBeTrue();

    [Theory]
    [InlineData("MongoDBShop.Migrations")]
    [InlineData("MongoDB.Migrations")]
    [InlineData("Systems.Migrations")]
    [InlineData("MicrosoftGraphSync.Migrations")]
    [InlineData("Kevsoft.MigrateMongoSamples")]
    public void Projects_named_like_them_are_scanned(string name) =>
        MigrationDiscovery.IsSkipped(name).ShouldBeFalse();

    [Fact]
    public void Migrations_in_a_referenced_project_named_like_the_driver_are_found()
    {
        var shop = Compile("MongoDBShop.Migrations_" + Guid.NewGuid().ToString("N"), """
            using System.Threading;
            using System.Threading.Tasks;
            using Kevsoft.MigrateMongo;

            namespace MongoDBShop.Migrations;

            [Migration("20260101000001", "add-orders")]
            public sealed class AddOrders : IMigration
            {
                public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);
        var entry = Compile("Runner_" + Guid.NewGuid().ToString("N"), """
            public static class Program
            {
                public static System.Type Migration => typeof(MongoDBShop.Migrations.AddOrders);
            }
            """, shop.Reference);

        var (migrations, _) = MigrationDiscovery.Discover(new MigrateMongoRunnerOptions(), entry.Assembly);

        migrations.Select(m => m.Origin).ShouldBe(["MongoDBShop.Migrations.AddOrders"]);
    }

    /// <summary>Compiles with the migration generator and loads the assembly, as a migrations project's build would.</summary>
    private static (Assembly Assembly, MetadataReference Reference) Compile(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(IMigration).Assembly.Location))
                .Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create(new MigrationGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        emit.Success.ShouldBeTrue(string.Join(Environment.NewLine, emit.Diagnostics));
        var image = stream.ToArray();
        return (AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image)), MetadataReference.CreateFromImage(image));
    }
}
