using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RecordIdentityTests
{
    [Fact]
    public void A_declared_key_reads_strings_as_they_are_and_whole_numbers_in_plain_digits()
    {
        var records = Identify(RecordIdentity.ByPaths("$.id"), """[{ "id": "A-1" }, { "id": 42 }, { "id": 4.50 }]""");

        Assert.Equal(["A-1", "42", "4.50"], records.Select(record => record.Key));
    }

    [Fact]
    public void A_composite_key_cannot_be_confused_with_another_combination_of_its_parts()
    {
        var identity = RecordIdentity.ByPaths("$.order", "$.line");

        var records = Identify(identity, """[{ "order": "1,2", "line": "3" }, { "order": "1", "line": "2,3" }]""");

        Assert.Equal(["[\"1,2\",\"3\"]", "[\"1\",\"2,3\"]"], records.Select(record => record.Key));
    }

    [Fact]
    public void Without_a_declared_key_a_unique_scalar_id_in_every_record_is_used()
    {
        var records = Identify(RecordIdentity.Inferred, """[{ "id": 1, "total": 5 }, { "id": 2, "total": 5 }]""");

        Assert.Equal(["1", "2"], records.Select(record => record.Key));
        Assert.Equal("$.id (inferred)", RecordIdentity.Inferred.Description);
    }

    [Theory]
    [InlineData("""[{ "order_id": 1, "sku": "a", "status": "x" }, { "order_id": 2, "sku": "b", "status": "x" }]""", "$.order_id, $.sku")]
    [InlineData("""[{ "id": 1, "number": "A" }, { "id": 1, "number": "B" }]""", "$.number")]
    [InlineData("""[{ "id": { "value": 1 }, "code": "A" }, { "id": { "value": 2 }, "code": "B" }]""", "$.code")]
    public void An_id_that_cannot_serve_is_refused_and_the_fields_that_could_are_named(string page, string candidates)
    {
        var error = Assert.Single(RecordIdentity.Inferred.Identify(Page(page)).Errors);

        Assert.Equal(HttpErrorCodes.RecordKeyMissing, error.Code);
        Assert.Contains($"Fields that could serve, present, scalar and unique in this page: {candidates}.", error.Message);
    }

    [Fact]
    public void A_record_without_its_declared_key_is_refused_without_naming_any_value()
    {
        var error = Assert.Single(RecordIdentity.ByPaths("$.id").Identify(Page("""[{ "id": "secret-1" }, { "name": "secret-2" }]""")).Errors);

        Assert.Equal(HttpErrorCodes.RecordKeyMissing, error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void A_content_key_is_the_same_whatever_the_order_of_properties_or_the_white_space()
    {
        var first = Identify(RecordIdentity.ByContent, """[{ "a": 1, "b": { "y": [1, 2], "x": "t" } }]""");
        var second = Identify(RecordIdentity.ByContent, """[{"b":{"x":"t","y":[1,2]},"a":1}]""");
        var changed = Identify(RecordIdentity.ByContent, """[{ "a": 2, "b": { "y": [1, 2], "x": "t" } }]""");

        Assert.Equal(first[0].Key, second[0].Key);
        Assert.NotEqual(first[0].Key, changed[0].Key);
        Assert.Equal(64, first[0].Key.Length);
    }

    [Fact]
    public void A_version_is_a_number_or_an_iso_time_in_microseconds_and_offsets_compare_as_the_same_moment()
    {
        var identity = RecordIdentity.ByPaths("$.id").VersionedBy("$.v");

        var records = Identify(identity, """
            [{ "id": 1, "v": 10 }, { "id": 2, "v": "2026-10-31T12:00:00Z" }, { "id": 3, "v": "2026-10-31T15:00:00+03:00" },
             { "id": 4, "v": null }, { "id": 5 }]
            """);

        Assert.Equal(10m, records[0].Version);
        Assert.Equal(1793448000000000m, records[1].Version);
        Assert.Equal(records[1].Version, records[2].Version);
        Assert.Null(records[3].Version);
        Assert.Null(records[4].Version);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("\"10/31/2026\"")]
    [InlineData("true")]
    [InlineData("{ \"at\": 1 }")]
    public void A_version_that_cannot_be_compared_is_refused(string version)
    {
        var identity = RecordIdentity.ByPaths("$.id").VersionedBy("$.v");

        var error = Assert.Single(identity.Identify(Page($$"""[{ "id": 1, "v": {{version}} }]""")).Errors);

        Assert.Equal(HttpErrorCodes.RecordVersionInvalid, error.Code);
    }

    [Fact]
    public void Of_several_copies_in_a_page_the_highest_version_survives_then_the_last_and_one_without_a_version_loses()
    {
        var identity = RecordIdentity.ByPaths("$.id").VersionedBy("$.v");

        var records = Identify(identity, """
            [{ "id": 1, "v": 2, "n": "newer" }, { "id": 1, "v": 1, "n": "older" },
             { "id": 2, "v": 5, "n": "first" }, { "id": 2, "v": 5, "n": "last" },
             { "id": 3, "v": 1, "n": "versioned" }, { "id": 3, "n": "unversioned" },
             { "id": 4, "n": "a" }, { "id": 4, "n": "b" }]
            """);

        Assert.Equal(["newer", "last", "versioned", "b"], records.Select(record => record.Record.GetProperty("n").GetString()));
    }

    [Fact]
    public void A_path_that_cannot_be_read_is_a_mistake_of_the_caller()
    {
        var thrown = Assert.Throws<ArgumentException>(() => RecordIdentity.ByPaths("id"));

        Assert.StartsWith(HttpErrorCodes.SinkSettingInvalid, thrown.Message);
        Assert.False(RecordIdentity.IsPath("$"));
        Assert.True(RecordIdentity.IsPath("$.parent.id"));
    }

    private static List<IdentifiedRecord> Identify(RecordIdentity identity, string records)
    {
        var result = identity.Identify(Page(records));
        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        return result.Value!.ToList();
    }

    private static RecordPage Page(string records)
    {
        var job = new Job { Id = 1, Queue = "shops", Owner = "shop-7", Lane = "shop-7", Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
        return new RecordPage(job, "test/orders", JsonDocument.Parse(records).RootElement);
    }
}
