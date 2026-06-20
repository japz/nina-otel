using NINA.Profile.Interfaces;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Plugin.Telemetry;

internal sealed class ProfileHostTelemetrySink : ITelemetrySink
{
    private const string ProfileNameAttribute = "profile_name";
    private const string HostNameAttribute = "host_name";

    private readonly ITelemetrySink inner;
    private readonly IProfileService profileService;
    private readonly Func<string?> hostNameProvider;

    public ProfileHostTelemetrySink(
        ITelemetrySink inner,
        IProfileService profileService,
        Func<string?>? hostNameProvider = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        this.hostNameProvider = hostNameProvider ?? (() => Environment.MachineName);
    }

    public bool TryPublish(TelemetryRecord record)
    {
        try
        {
            return inner.TryPublish(EnrichSafely(record));
        }
        catch
        {
            return false;
        }
    }

    private TelemetryRecord EnrichSafely(TelemetryRecord record)
    {
        var profileName = TryGetProfileName();
        var hostName = TryGetHostName();
        var shouldAddProfileName =
            !string.IsNullOrWhiteSpace(profileName) &&
            !record.Attributes.ContainsKey(ProfileNameAttribute);
        var shouldAddHostName =
            !string.IsNullOrWhiteSpace(hostName) &&
            !record.Attributes.ContainsKey(HostNameAttribute);

        if (!shouldAddProfileName && !shouldAddHostName)
        {
            return record;
        }

        var attributes = new Dictionary<string, object?>(record.Attributes, StringComparer.Ordinal);
        if (shouldAddProfileName)
        {
            attributes[ProfileNameAttribute] = profileName;
        }

        if (shouldAddHostName)
        {
            attributes[HostNameAttribute] = hostName;
        }

        return record with { Attributes = attributes };
    }

    private string? TryGetProfileName()
    {
        try
        {
            return profileService.ActiveProfile?.Name;
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetHostName()
    {
        try
        {
            return hostNameProvider();
        }
        catch
        {
            return null;
        }
    }
}
