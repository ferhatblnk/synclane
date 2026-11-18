using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;

namespace SyncLane.Instances;

/// <summary>
/// Who this process is in the instances table. The id is also the prefix of the worker names written to
/// <c>leased_by</c>, so a running job can be traced to its instance. The host and the process id alone are not unique:
/// two containers that share a hostname both run as process 1, so a random part follows them.
/// </summary>
internal sealed record InstanceIdentity(string Id, string Host, string Application, string Role, string Version)
{
    public static InstanceIdentity Create(SyncLaneOptions options, IHostEnvironment? environment)
    {
        var application = options.Application
            ?? environment?.ApplicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "";

        if (!SyncLaneOptions.IsValidName(application))
            throw new SyncLaneException(SyncLaneErrors.ApplicationNameInvalid(application));

        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        return new InstanceIdentity(id, Environment.MachineName, application, options.Role ?? "", PackageVersion);
    }

    /// <summary>The package version, without the build metadata after <c>+</c>.</summary>
    public static string PackageVersion { get; } =
        typeof(InstanceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
