namespace SyncLane.Http;

/// <summary>How a destination tells records apart, as set on its options: shared by every built-in destination.</summary>
internal sealed class RecordKeySettings
{
    private string[]? _keys;
    private bool _content;
    private string? _version;

    /// <summary>The identity, built by <see cref="Validate"/> once the settings are known to work.</summary>
    public RecordIdentity Identity { get; private set; } = RecordIdentity.Inferred;

    public void KeyedBy(string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _keys = paths;
        _content = false;
    }

    public void KeyedByContent()
    {
        _content = true;
        _keys = null;
    }

    public void VersionedBy(string path) => _version = path;

    public void Validate(string type, List<SyncError> errors)
    {
        var before = errors.Count;
        if (_keys is { Length: 0 })
            errors.Add(HttpErrors.SinkSettingInvalid(type, "key", "needs at least one path"));

        errors.AddRange((_keys ?? []).Where(path => !RecordIdentity.IsPath(path)).Select(path => HttpErrors.SinkSettingInvalid(type, "key", RecordIdentity.PathRule(path))));

        if (_version is not null && !RecordIdentity.IsPath(_version))
            errors.Add(HttpErrors.SinkSettingInvalid(type, "version", RecordIdentity.PathRule(_version)));

        if (errors.Count > before)
            return;

        var identity = _content ? RecordIdentity.ByContent : _keys is { } keys ? RecordIdentity.ByPaths(keys) : RecordIdentity.Inferred;
        Identity = _version is { } version ? identity.VersionedBy(version) : identity;
    }
}
