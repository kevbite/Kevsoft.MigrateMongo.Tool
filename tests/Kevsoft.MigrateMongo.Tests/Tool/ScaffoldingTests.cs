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
}
