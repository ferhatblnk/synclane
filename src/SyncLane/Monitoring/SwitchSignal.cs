namespace SyncLane.Monitoring;

/// <summary>A switch an operator changed, as announced to every instance, so the instances it covers read it at once.</summary>
internal sealed record SwitchSignal(string Application, string Role);
