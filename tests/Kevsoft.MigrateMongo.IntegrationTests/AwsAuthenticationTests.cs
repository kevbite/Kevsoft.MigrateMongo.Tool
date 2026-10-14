using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// MONGODB-AWS URLs. The local server doesn't do AWS IAM auth, so these check the wiring: without the
/// Kevsoft.MigrateMongo.Aws package the run fails up front with instructions; with it the driver signs and the server rejects.
/// </summary>
public sealed class AwsAuthenticationTests(IntegrationEnvironment environment)
{
    private static readonly Dictionary<string, string?> FakeCredentials = new()
    {
        ["AWS_ACCESS_KEY_ID"] = "AKIAIOSFODNN7EXAMPLE",
        ["AWS_SECRET_ACCESS_KEY"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
        ["AWS_SESSION_TOKEN"] = "fake-session-token",
    };

    private Workspace CreateWorkspace()
    {
        var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        var separator = environment.MongoUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        workspace.WriteConfigFile($"""
            project: BasicMigrations
            mongodb:
              url: {environment.MongoUrl}{separator}authSource=%24external&authMechanism=MONGODB-AWS&serverSelectionTimeoutMS=3000
              databaseName: {workspace.DatabaseName}
            lockWaitTimeout: 5
            """);
        return workspace;
    }

    [Fact]
    public async Task Without_the_aws_package_the_run_says_which_package_to_add()
    {
        await using var workspace = CreateWorkspace();

        var status = await workspace.RunAsync(["status"], FakeCredentials);

        status.ExitCode.ShouldNotBe(0, status.ToString());
        status.ToString().ShouldContain("needs the Kevsoft.MigrateMongo.Aws package");
        status.ToString().ShouldContain("dotnet add package Kevsoft.MigrateMongo.Aws");
    }

    [Fact]
    public async Task With_the_aws_package_the_driver_authenticates_with_aws()
    {
        await using var workspace = CreateWorkspace();
        var project = workspace.PathOf("BasicMigrations", "BasicMigrations.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace(
            """<PackageReference Include="MongoDB.Driver" Version="3.12.0" />""",
            """<PackageReference Include="MongoDB.Driver" Version="3.12.0" /><PackageReference Include="Kevsoft.MigrateMongo.Aws" Version="$(MigrateMongoVersion)" />"""));

        var status = await workspace.RunAsync(["status"], FakeCredentials);

        status.ExitCode.ShouldNotBe(0, status.ToString());
        status.ToString().ShouldNotContain("needs the Kevsoft.MigrateMongo.Aws package");
        status.ToString().ShouldContain("MONGODB-AWS");
        workspace.CollectionNames().ShouldBeEmpty();
    }
}
