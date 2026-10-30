using System.Text.Json;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Config;

public sealed class ExtendedJsonTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{"$numberInt":"2"}""", "2")]
    [InlineData("""{"$numberLong":"9007199254740993"}""", "9007199254740993")]
    [InlineData("""{"$numberDecimal":"12.50"}""", "12.50")]
    [InlineData("""{"$oid":"64b7f0c2a1b2c3d4e5f60718"}""", """ObjectId("64b7f0c2a1b2c3d4e5f60718")""")]
    [InlineData("""{"$date":{"$numberLong":"1790812800000"}}""", "2026-10-01T00:00:00Z")]
    [InlineData("""{"$date":"2026-10-01T00:00:00.250Z"}""", "2026-10-01T00:00:00.250Z")]
    [InlineData("""{"$binary":{"base64":"ASNFZ4mrze8BI0VniavN7w==","subType":"04"}}""", """UUID("01234567-89ab-cdef-0123-456789abcdef")""")]
    [InlineData("""{"a":{"$numberInt":"1"},"b":["x",true,null]}""", """{ a: 1, b: ["x", true, null] }""")]
    [InlineData("{}", "{}")]
    [InlineData("\"gold\"", "gold")]
    public void Values_are_formatted_in_shell_style(string json, string expected)
    {
        ExtendedJson.Format(Json(json)).ShouldBe(expected);
    }

    [Fact]
    public void Strings_can_be_quoted()
    {
        ExtendedJson.Format(Json("\"gold\""), quoteStrings: true).ShouldBe("\"gold\"");
    }
}
