namespace SyncLane.Tests;

public class ResultTests
{
    private static readonly SyncError Missing = new(SyncErrorCodes.QueueNotDeclared, SyncErrorKind.NotFound, "Queue 'x' is not declared.");

    [Fact]
    public void A_success_has_its_value_and_no_errors()
    {
        var result = SyncResult<int>.Success(7);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Value);
        Assert.Empty(result.Errors);
        Assert.Null(result.Error);
        Assert.Equal(7, result.GetValueOrThrow());
    }

    [Fact]
    public void A_failure_has_its_errors_and_throws_only_when_asked()
    {
        var result = SyncResult<int>.Failure(Missing);

        Assert.False(result.Succeeded);
        Assert.Equal(default, result.Value);
        Assert.Same(Missing, result.Error);
        var thrown = Assert.Throws<SyncLaneException>(() => result.GetValueOrThrow());
        Assert.Same(Missing, thrown.Error);
        Assert.Equal("SL3001: Queue 'x' is not declared.", thrown.Message);
    }

    [Fact]
    public void Throwing_a_failure_keeps_every_error()
    {
        var other = new SyncError(SyncErrorCodes.LimitOutOfRange, SyncErrorKind.Validation, "Limit 0 is out of range.");

        var thrown = Assert.Throws<SyncLaneException>(() => SyncResult.Failure([Missing, other]).ThrowIfFailed());

        Assert.Equal([Missing, other], thrown.Errors);
        Assert.Same(Missing, thrown.Error);
        Assert.Equal($"{Missing}{Environment.NewLine}{other}", thrown.Message);
    }

    [Fact]
    public void A_failure_needs_at_least_one_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncResult.Failure([]));
        Assert.Throws<ArgumentNullException>(() => SyncResult.Failure((SyncError)null!));
        Assert.Throws<ArgumentNullException>(() => SyncResult.Failure([Missing, null!]));
    }

    [Fact]
    public void A_list_page_knows_whether_more_follows()
    {
        var last = SyncListResult<string>.Success(["a"]);
        var more = SyncListResult<string>.Success(["a", "b"], nextCursor: "41", total: 90);
        var failed = SyncListResult<string>.Failure(Missing);

        Assert.False(last.HasMore);
        Assert.Null(last.Total);
        Assert.True(more.HasMore);
        Assert.Equal(("41", 90L), (more.NextCursor, more.Total));
        Assert.Empty(failed.Items);
        Assert.False(failed.Succeeded);
    }

    [Theory]
    [InlineData(SyncErrorKind.Validation, 400)]
    [InlineData(SyncErrorKind.NotFound, 404)]
    [InlineData(SyncErrorKind.Conflict, 409)]
    [InlineData(SyncErrorKind.Unauthorized, 401)]
    [InlineData(SyncErrorKind.Throttled, 429)]
    [InlineData(SyncErrorKind.Unavailable, 503)]
    [InlineData(SyncErrorKind.Configuration, 500)]
    [InlineData(SyncErrorKind.Internal, 500)]
    public void Every_kind_maps_to_an_http_status(SyncErrorKind kind, int status)
    {
        Assert.Equal(status, kind.ToHttpStatusCode());
    }
}
