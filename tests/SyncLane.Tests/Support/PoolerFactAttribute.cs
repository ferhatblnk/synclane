namespace SyncLane.Tests.Support;

/// <summary>
/// A test that needs the test database reached through a pooler in transaction mode. It runs only when
/// <see cref="PostgresTest.ListenVariable"/> is set, which says that <see cref="PostgresTest.Variable"/> goes through one.
/// </summary>
public sealed class PoolerFactAttribute : FactAttribute
{
    public PoolerFactAttribute()
    {
        if (PostgresTest.ConnectionString is null || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PostgresTest.ListenVariable)))
            Skip = $"Set {PostgresTest.Variable} to a pooler in transaction mode and {PostgresTest.ListenVariable} to the server itself to run this test.";
    }
}
