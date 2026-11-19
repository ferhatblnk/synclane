using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Storage;

namespace SyncLane.Instances;

/// <summary>
/// Registers this process in the instances table before the planner and the workers start, and refuses to start when
/// a live instance of another application runs one of its queues: their workers would take each other's jobs and give
/// up the types they do not know. Then it says it is alive every half minute, and marks itself stopped when it stops.
/// </summary>
/// <remarks>
/// <para>
/// It also decides whether the workers, the planner and listening run: the options say so, unless an operator set a
/// switch for this application and role in the database. The switch is read at registration and with every heartbeat; while the
/// workers run, a switch announced for this application is read at once, so turning them off takes effect immediately
/// and turning them on within half a minute. A switch that would turn them on while another application runs one of the
/// queues leaves them off, and the health check says why.
/// </para>
/// <para>
/// At registration and with every heartbeat it also looks for live instances under its application's name that declare
/// none of its queues and job types, and warns: two applications that took one name share its switch and look like one
/// in the monitor.
/// </para>
/// </remarks>
internal sealed partial class InstanceRegistry(
    SyncLaneOptions options,
    InstanceStore store,
    InstanceIdentity identity,
    RoleSwitch roles,
    Monitoring.JobChangeFeed changes,
    TimeProvider time,
    Monitoring.HealthSignals health,
    ILogger<InstanceRegistry> logger) : BackgroundService
{
    private readonly SemaphoreSlim _settling = new(1, 1);
    private readonly IReadOnlyList<string> _declares = InstanceRules.Declarations(options);
    private InstanceRow? _me;
    private string? _refused;
    private string? _strangers;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (live, me, refused) = await store.RegisterAsync(
            identity, now - InstanceRules.GoneAfter, (others, set) => Decide(others, set, now, now, refuseConflicts: true), cancellationToken);
        if (me is null)
            throw new SyncLaneException(refused);

        foreach (var (mine, theirs, other) in InstanceRules.Disagreements(me.Budgets, live))
            LogBudgetDisagrees(mine.Name, mine.Limit, mine.Per, mine.Burst, other.Id, other.Application, theirs.Limit, theirs.Per, theirs.Burst);

        _me = me;
        roles.Set(me.RunsWorkers == true, me.RunsPlanner == true);
        changes.SetListening(me.Listens == true);
        health.Heartbeat(now);
        Notice(InstanceRules.Strangers(me, live));
        LogRegistered(identity.Id, identity.Application, me.Queues.Count);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(BeatAsync(stoppingToken), HearSwitchesAsync(stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_me is null)
            return;

        try
        {
            await store.StopAsync(identity.Id, time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStopFailed(exception, identity.Id, InstanceRules.GoneAfter);
        }
    }

    internal async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_me is null)
            return;

        var settle = false;
        await _settling.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var (set, namesakes) = await store.HeartbeatAsync(
                identity, _me, now, now - InstanceRules.GoneAfter, now - InstanceRules.ForgetAfter, cancellationToken);
            health.Heartbeat(now);
            Notice(InstanceRules.Strangers(_me, namesakes));
            settle = Wanted(set) != (roles.Workers, roles.Planner, changes.Listens);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogHeartbeatFailed(exception, identity.Id);
        }
        finally
        {
            _settling.Release();
        }

        if (settle)
            await SettleAsync(cancellationToken);
    }

    /// <summary>
    /// Reads this application's switch and the live instances behind the registration lock, and turns the workers and
    /// the planner on or off to match.
    /// </summary>
    internal async Task SettleAsync(CancellationToken cancellationToken)
    {
        if (_me is not { } before)
            return;

        await _settling.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var (_, me, refused) = await store.RegisterAsync(
                identity, now - InstanceRules.GoneAfter, (others, set) => Decide(others, set, before.StartedAt, now, refuseConflicts: false), cancellationToken);
            _me = me!;
            roles.Set(_me.RunsWorkers == true, _me.RunsPlanner == true);
            changes.SetListening(_me.Listens == true);
            if ((_me.RunsWorkers, _me.RunsPlanner, _me.Listens) != (before.RunsWorkers, before.RunsPlanner, before.Listens))
                LogSwitched(identity.Id, _me.RunsWorkers == true, _me.RunsPlanner == true, _me.Listens == true);

            var why = refused.Count == 0 ? null : string.Join(' ', refused.Select(error => error.Message));
            if (why is not null && why != _refused)
                LogSwitchRefused(identity.Id, why);

            _refused = why;
            health.Refused(why);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogSettleFailed(exception, identity.Id);
        }
        finally
        {
            _settling.Release();
        }
    }

    /// <summary>Warns once for each new set of strangers under this application's name, and tells the health check.</summary>
    private void Notice(List<InstanceRow> strangers)
    {
        var ids = strangers.Count == 0 ? null : string.Join(", ", strangers.Select(other => $"{other.Id} on {other.Host}"));
        if (ids is not null && ids != _strangers)
            LogStrangers(identity.Id, identity.Application, ids);

        _strangers = ids;
        health.Strangers(ids is null ? null : (identity.Application, ids));
    }

    private async Task BeatAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InstanceRules.HeartbeatInterval, time);

        // Sequential: a timer loop; one heartbeat per interval.
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await HeartbeatAsync(stoppingToken);
    }

    /// <summary>
    /// While the workers run, reads a switch announced for this application and role at once. An instance whose workers
    /// are off opens no listening connection for it and reads the switch with its next heartbeat.
    /// </summary>
    private async Task HearSwitchesAsync(CancellationToken stopping)
    {
        try
        {
            // Sequential: a switch loop; announcements are heard while the workers run.
            while (!stopping.IsCancellationRequested)
            {
                var on = await roles.WorkersOnAsync(stopping);
                using var hearing = CancellationTokenSource.CreateLinkedTokenSource(stopping, on);
                try
                {
                    // Sequential: reading the announcements in order.
                    await foreach (var signal in changes.ReadSwitchesAsync(hearing.Token))
                    {
                        if (signal.Application == identity.Application && signal.Role == identity.Role)
                            await SettleAsync(stopping);
                    }
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private (bool Workers, bool Planner, bool Listen) Wanted(SwitchRow? set) =>
        (set?.RunWorkers ?? options.RunWorkers, set?.RunPlanner ?? options.RunPlanner, set?.Listen ?? options.ListenForChanges);

    /// <summary>
    /// The row this instance writes: what it runs, given the switch and the live instances. A queue another application
    /// runs stops the start when <paramref name="refuseConflicts"/>; later it only keeps the workers and the planner off.
    /// </summary>
    private (InstanceRow? Row, IReadOnlyList<SyncError> Refused) Decide(
        IReadOnlyList<InstanceRow> live, SwitchRow? set, DateTimeOffset startedAt, DateTimeOffset now, bool refuseConflicts)
    {
        var (workers, planner, listen) = Wanted(set);
        var queues = InstanceRules.Runs(options, workers, planner);
        var conflicts = InstanceRules.Conflicts(identity, queues, live, now);
        if (conflicts.Count > 0)
        {
            if (refuseConflicts)
                return (null, conflicts);

            (workers, planner, queues) = (false, false, []);
        }

        var row = new InstanceRow(
            identity.Id, identity.Application, identity.Host, identity.Version, queues, options.DescribeBudgets(), startedAt, now,
            Role: identity.Role, RunsWorkers: workers, RunsPlanner: planner, Listens: listen, Declares: _declares);
        return (row, conflicts);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLane instance {Instance} of {Application} registered, running {Queues} queues.")]
    private partial void LogRegistered(string instance, string application, int queues);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Budget {Budget} is declared here as {Limit} per {Per} with burst {Burst}, but instance {Instance} of {Application} " +
        "declares {OtherLimit} per {OtherPer} with burst {OtherBurst}. A budget is shared, so instances that disagree pace it " +
        "differently. Declare it the same way everywhere; during a deploy that changes it this passes by itself.")]
    private partial void LogBudgetDisagrees(
        string budget, int limit, TimeSpan per, int burst, string instance, string application, int otherLimit, TimeSpan otherPer, int otherBurst);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "SyncLane instance {Instance} registers as application {Application}, and so do {Others}, which declare none of " +
        "its queues and job types: most likely another application that took the same name. Both are switched on and " +
        "off together and the monitor shows them as one. Give one of them a name of its own with options.Application " +
        "(SyncLane:Application); during a deploy that renames every queue, job type or handler this passes by itself.")]
    private partial void LogStrangers(string instance, string application, string others);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLane instance {Instance} could not say it is alive and tries again in half a minute.")]
    private partial void LogHeartbeatFailed(Exception exception, string instance);

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLane instance {Instance} switched: workers {Workers}, planner {Planner}, listening {Listening}.")]
    private partial void LogSwitched(string instance, bool workers, bool planner, bool listening);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "The switch in the database turns the workers or the planner of SyncLane instance {Instance} on, but they stay off: {Why}")]
    private partial void LogSwitchRefused(string instance, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLane instance {Instance} could not read its switch and tries again with its next heartbeat.")]
    private partial void LogSettleFailed(Exception exception, string instance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLane instance {Instance} could not mark itself stopped; it counts as gone {GoneAfter} after its last heartbeat.")]
    private partial void LogStopFailed(Exception exception, string instance, TimeSpan goneAfter);
}
