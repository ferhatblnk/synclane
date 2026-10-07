namespace SyncLane.Tests.Support;

/// <summary>A test that needs PostgreSQL. It is skipped unless <see cref="PostgresTest.Variable"/> holds a connection string.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTest.ConnectionString is null)
            Skip = $"Set {PostgresTest.Variable} to a PostgreSQL connection string to run this test.";
    }
}
