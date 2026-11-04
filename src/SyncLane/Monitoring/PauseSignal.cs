namespace SyncLane.Monitoring;

/// <summary>A pause or resume, as announced to every instance. An empty <paramref name="Owner"/> means the whole queue.</summary>
internal sealed record PauseSignal(string Queue, string Owner, bool Resumed);
