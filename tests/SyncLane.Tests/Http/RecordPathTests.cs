using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RecordPathTests
{
    [Theory]
    [InlineData(null, "[1, 2, 3]", 3)]
    [InlineData("$", "[1, 2]", 2)]
    [InlineData("$.data.items", """{ "data": { "items": [1, 2, 3, 4] } }""", 4)]
    [InlineData("$.page-1", """{ "page-1": [] }""", 0)]
    public void The_path_leads_to_the_array_of_records(string? path, string json, int count)
    {
        var records = RecordPath.Parse(path)!.Find(JsonDocument.Parse(json).RootElement, "test").GetValueOrThrow();

        Assert.Equal(count, records.GetArrayLength());
    }

    [Fact]
    public void A_path_that_leads_nowhere_or_to_something_else_says_so()
    {
        var path = RecordPath.Parse("$.data.items")!;

        var missing = path.Find(JsonDocument.Parse("""{ "data": {} }""").RootElement, "test");
        var notArray = path.Find(JsonDocument.Parse("""{ "data": { "items": { "a": 1 } } }""").RootElement, "test");

        Assert.Equal(HttpErrorCodes.RecordsNotFound, missing.Error!.Code);
        Assert.Equal(HttpErrorCodes.RecordsNotArray, notArray.Error!.Code);
        Assert.Contains("JSON object", notArray.Error.Message);
    }

    [Theory]
    [InlineData("data.items")]
    [InlineData("$..items")]
    [InlineData("$.items[0]")]
    [InlineData("$.items[?(@.id)]")]
    [InlineData("")]
    public void Only_plain_property_paths_are_accepted(string path)
    {
        Assert.Null(RecordPath.Parse(path));
    }
}
