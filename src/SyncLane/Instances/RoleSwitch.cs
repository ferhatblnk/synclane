namespace SyncLane.Instances;

/// <summary>
/// Whether this process runs its workers and its planner right now. The instance registry sets both from the options
/// and the switch an operator keeps in the database; until it has registered, both are off. The workers and the planner
/// wait while theirs is off, and the token they get when it comes on is cancelled the moment it goes off again.
/// </summary>
internal sealed class RoleSwitch
{
    private readonly Gate _workers = new();
    private readonly Gate _planner = new();

    public bool Workers => _workers.IsOn;

    public bool Planner => _planner.IsOn;

    public void Set(bool workers, bool planner)
    {
        _workers.Set(workers);
        _planner.Set(planner);
    }

    /// <summary>Completes when the workers are on, with a token that is cancelled when they go off.</summary>
    public Task<CancellationToken> WorkersOnAsync(CancellationToken cancellationToken) => _workers.OnAsync(cancellationToken);

    /// <summary>Completes when the planner is on, with a token that is cancelled when it goes off.</summary>
    public Task<CancellationToken> PlannerOnAsync(CancellationToken cancellationToken) => _planner.OnAsync(cancellationToken);

    private sealed class Gate
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource<CancellationToken> _on = New();
        private CancellationTokenSource? _period;

        public bool IsOn
        {
            get
            {
                lock (_lock)
                    return _period is not null;
            }
        }

        public void Set(bool on)
        {
            CancellationTokenSource? ended = null;
            lock (_lock)
            {
                if (on == _period is not null)
                    return;

                if (on)
                {
                    _period = new CancellationTokenSource();
                    _on.TrySetResult(_period.Token);
                }
                else
                {
                    ended = _period;
                    _period = null;
                    _on = New();
                }
            }

            // Outside the lock: cancelling runs the callbacks of every token linked to it.
            ended?.Cancel();
        }

        public Task<CancellationToken> OnAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
                return _on.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource<CancellationToken> New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
