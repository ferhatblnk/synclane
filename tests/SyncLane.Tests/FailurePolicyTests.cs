using SyncLane.Running;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class FailurePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly QueueOptions Queue =
        new SyncLaneOptions().AddQueue("stores", queue => queue.Snapshot<ProductHandler>("products")).Queue("stores");

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void The_delay_doubles_with_every_attempt(int attempt, int minutes)
    {
        var outcome = FailurePolicy.Decide(Queue, attempt, new TimeoutException("slow"), Now);

        Assert.Equal(new FailureOutcome(JobStatus.Failed, Now.AddMinutes(minutes)), outcome);
    }

    [Fact]
    public void The_delay_never_exceeds_the_maximum()
    {
        Assert.Equal(TimeSpan.FromHours(1), FailurePolicy.Backoff(12, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromHours(1), FailurePolicy.Backoff(5000, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_throttled_job_waits_longer_than_a_failed_one()
    {
        var throttled = FailurePolicy.Decide(Queue, 1, new JobThrottledException("429"), Now);
        var again = FailurePolicy.Decide(Queue, 3, new JobThrottledException("429"), Now);

        Assert.Equal(Now.AddMinutes(5), throttled.NextRunAt);
        Assert.Equal(Now.AddMinutes(20), again.NextRunAt);
    }

    [Fact]
    public void A_wait_named_by_the_other_side_is_used_as_it_is()
    {
        var wrapped = new InvalidOperationException("page failed", new JobThrottledException("429", TimeSpan.FromSeconds(90)));

        Assert.Equal(Now.AddSeconds(90), FailurePolicy.Decide(Queue, 2, wrapped, Now).NextRunAt);
    }

    [Fact]
    public void The_last_regular_attempt_defers_the_job_and_the_one_after_abandons_it()
    {
        var failure = new TimeoutException("slow");

        Assert.Equal(JobStatus.Failed, FailurePolicy.Decide(Queue, 4, failure, Now).Status);
        Assert.Equal(new FailureOutcome(JobStatus.Deferred, Now), FailurePolicy.Decide(Queue, 5, failure, Now));
        Assert.Equal(new FailureOutcome(JobStatus.Abandoned, Now), FailurePolicy.Decide(Queue, 6, failure, Now));
    }

    [Fact]
    public void A_handler_can_abandon_a_job_at_once()
    {
        var revoked = new InvalidOperationException("page failed", new JobAbandonedException("Access was revoked."));

        Assert.Equal(new FailureOutcome(JobStatus.Abandoned, Now), FailurePolicy.Decide(Queue, 1, revoked, Now));
    }

    [Fact]
    public void The_stored_error_names_every_exception_in_the_chain()
    {
        var failure = new InvalidOperationException("Page 3 failed.", new TimeoutException("No answer in 30 s."));

        Assert.Equal("InvalidOperationException: Page 3 failed. -> TimeoutException: No answer in 30 s.", ErrorText.Describe(failure));
    }

    [Fact]
    public void The_stored_error_is_cut_to_a_fixed_length()
    {
        var text = ErrorText.Describe(new InvalidOperationException(new string('x', 3 * ErrorText.MaxLength)));

        Assert.Equal(ErrorText.MaxLength, text.Length);
    }
}
