using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace SyncLane.Instances;

/// <summary>
/// Who this process is in the instances table. The id is also the prefix of the worker names written to
/// <c>leased_by</c>, so a running job can be traced to its instance.
/// </summary>
internal sealed record InstanceIdentity(string Id, string Host, string Application, string Version)
{
    public static InstanceIdentity Create(SyncLaneOptions options, IHostEnvironment? environment)
    {
        var application = options.Application
            ?? environment?.ApplicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "";

        if (!SyncLaneOptions.IsValidName(application))
            throw new SyncLaneException(SyncLaneErrors.ApplicationNameInvalid(application));

        return new InstanceIdentity($"{Environment.MachineName}:{Environment.ProcessId}", Environment.MachineName, application, PackageVersion);
    }

    /// <summary>The package version, without the build metadata after <c>+</c>.</summary>
    public static string PackageVersion { get; } =
        typeof(InstanceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
