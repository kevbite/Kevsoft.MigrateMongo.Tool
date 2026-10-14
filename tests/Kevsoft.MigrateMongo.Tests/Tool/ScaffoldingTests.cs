using Kevsoft.MigrateMongo.Tool;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tool;

public sealed class ScaffoldingTests
{
    [Theory]
    [InlineData("AddEmailIndexToUsers", "AddEmailIndexToUsers")]
    [InlineData("add email-index", "AddEmailIndex")]
    [InlineData("seed_roles", "SeedRoles")]
    [InlineData("1st", null)]
    [InlineData("---", null)]
    public void ToClassName(string input, string? expected) => Scaffolding.ToClassName(input).ShouldBe(expected);

    [Theory]
    [InlineData("AddEmailIndexToUsers", "add-email-index-to-users")]
    [InlineData("SeedV2Roles", "seed-v-2-roles")]
    [InlineData("ImportCSVData", "import-csv-data")]
    [InlineData("Initial", "initial")]
    public void ToKebabCase(string input, string expected) => Scaffolding.ToKebabCase(input).ShouldBe(expected);

    [Fact]
    public void NewId_is_a_14_digit_utc_timestamp() =>
        Scaffolding.NewId(new DateTime(2026, 10, 9, 14, 52, 10, DateTimeKind.Utc)).ShouldBe("20261009145210");

    [Theory]
    [InlineData("MyApp.Migrations", "MyApp.Migrations")]
    [InlineData("my-app.1migrations", "my_app._1migrations")]
    public void ToNamespace(string input, string expected) => Scaffolding.ToNamespace(input).ShouldBe(expected);

    [Fact]
    public void RenderMigrationTemplate_replaces_the_known_placeholders_only() =>
        Scaffolding.RenderMigrationTemplate(
                "namespace {{namespace}};\n[Migration(\"{{id}}\", \"{{name}}\")]\nclass {{className}} : BaseMigration { string S => $$\"\"\"{{other}}\"\"\"; }",
                "Shop.Migrations", "20261013091500", "AddProducts", "add-products")
            .ShouldBe("namespace Shop.Migrations;\n[Migration(\"20261013091500\", \"add-products\")]\nclass AddProducts : BaseMigration { string S => $$\"\"\"{{other}}\"\"\"; }");

    [Fact]
    public void ProjectFile_references_the_aws_package_at_the_runner_version_when_asked()
    {
        Scaffolding.ProjectFile("net10.0", "1.2.3", "3.5.0").ShouldNotContain("Kevsoft.MigrateMongo.Aws");
        Scaffolding.ProjectFile("net10.0", "1.2.3", "3.5.0", aws: true).ShouldContain("""<PackageReference Include="Kevsoft.MigrateMongo.Aws" Version="1.2.3" />""");
    }

    [Fact]
    public void ConfigFile_shows_an_aws_url_when_asked()
    {
        Scaffolding.ConfigFile(".", "shop").ShouldNotContain("MONGODB-AWS");
        Scaffolding.ConfigFile(".", "shop", aws: true).ShouldContain("authSource=%24external&authMechanism=MONGODB-AWS");
    }
}
