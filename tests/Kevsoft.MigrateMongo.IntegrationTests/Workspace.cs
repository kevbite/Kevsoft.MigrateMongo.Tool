using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// A throw-away copy of a fixture project in the temp directory (outside the repo so none of the repo's MSBuild
/// files apply), pointed at the local package feed and a uniquely named database.
/// </summary>
public sealed class Workspace : IAsyncDisposable
{
    private readonly IntegrationEnvironment _environment;
    private readonly IMongoClient _client;

    private Workspace(IntegrationEnvironment environment, IMongoClient client, string directory, string databaseName)
    {
        _environment = environment;
        _client = client;
        Directory = directory;
        DatabaseName = databaseName;
    }

    public string Directory { get; }

    public string DatabaseName { get; }

    public IMongoDatabase Database => _client.GetDatabase(DatabaseName);

    /// <summary>Copies <c>tests/fixtures/{fixture}</c> into a new workspace with a migrate-mongo.yaml for it.</summary>
    /// <param name="environment">The shared integration environment.</param>
    /// <param name="fixture">The fixture folder name.</param>
    /// <param name="lockWaitTimeout">The config's <c>lockWaitTimeout</c> in seconds.</param>
    /// <param name="client">Client for asserting on and cleaning up the database (default: the shared container).</param>
    public static Workspace FromFixture(IntegrationEnvironment environment, string fixture, int lockWaitTimeout = 5, IMongoClient? client = null)
    {
        var workspace = Empty(environment, client);
        workspace.AddFixture(fixture);
        workspace.WriteConfig(fixture, lockWaitTimeout);
        return workspace;
    }

    /// <summary>Copies another fixture (e.g. one that references the first) into the workspace.</summary>
    public void AddFixture(string fixture) =>
        CopyDirectory(Path.Combine(_environment.RepositoryRoot, "tests", "fixtures", fixture), Path.Combine(Directory, fixture));

    /// <summary>An empty workspace with only the NuGet/SDK plumbing, for testing <c>init</c>.</summary>
    public static Workspace Empty(IntegrationEnvironment environment, IMongoClient? client = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "migrate-mongo-it", Guid.NewGuid().ToString("N")[..12]);
        System.IO.Directory.CreateDirectory(directory);
        var workspace = new Workspace(environment, client ?? environment.Client, directory, "it_" + Guid.NewGuid().ToString("N")[..16]);

        File.WriteAllText(Path.Combine(directory, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{environment.PackageFeed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), $"""
            <Project>
              <PropertyGroup>
                <MigrateMongoVersion>{environment.PackageVersion}</MigrateMongoVersion>
                <MigrateMongoDriverVersion>{environment.DriverVersion}</MigrateMongoDriverVersion>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        File.Copy(Path.Combine(environment.RepositoryRoot, "global.json"), Path.Combine(directory, "global.json"));
        return workspace;
    }

    public string ConfigPath => Path.Combine(Directory, "migrate-mongo.yaml");

    public void WriteConfig(string project, int lockWaitTimeout) =>
        WriteConfigFile($"""
            project: {project}
            mongodb:
              url: {_environment.MongoUrl}
              databaseName: {DatabaseName}
            lockWaitTimeout: {lockWaitTimeout}
            """);

    public void WriteConfigFile(string yaml) => File.WriteAllText(ConfigPath, yaml);

    public string PathOf(params string[] parts) => Path.Combine([Directory, .. parts]);

    /// <summary>Runs the installed migrate-mongo tool in this workspace.</summary>
    public Task<ProcessResult> RunAsync(params string[] arguments) => RunAsync(arguments, null);

    public Task<ProcessResult> RunAsync(string[] arguments, IReadOnlyDictionary<string, string?>? environment)
    {
        var variables = new Dictionary<string, string?>(_environment.ProcessEnvironment);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            variables[key] = value;
        }

        return ProcessHelper.RunAsync(_environment.ToolPath, arguments, Directory, variables, TimeSpan.FromMinutes(5));
    }

    /// <summary>Runs the tool with <c>--json</c> and returns the final <c>result</c> event.</summary>
    public async Task<(ProcessResult Process, JsonElement Result)> RunJsonAsync(params string[] arguments)
    {
        var process = await RunAsync([.. arguments, "--json"]);
        var lines = process.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var events = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var result = events.LastOrDefault(e => e.GetProperty("type").GetString() == "result");
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"No result event in --json output.{Environment.NewLine}{process}");
        }

        return (process, result.GetProperty("result"));
    }

    public List<BsonDocument> Find(string collection, string sortBy = "_id") =>
        Database.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument(sortBy, 1)).ToList();

    public List<string> CollectionNames() => Database.ListCollectionNames().ToList().Order(StringComparer.Ordinal).ToList();

    public async ValueTask DisposeAsync()
    {
        await _client.DropDatabaseAsync(DatabaseName);
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Build servers can hold files briefly; the temp directory is cleaned up eventually.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is "bin" or "obj")
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
