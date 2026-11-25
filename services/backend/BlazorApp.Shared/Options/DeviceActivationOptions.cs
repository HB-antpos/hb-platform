namespace BlazorApp.Shared.Options;

public sealed class DeviceActivationOptions
{
    public const string SectionName = "DeviceActivation";

    public LegacyRegistrationOptions LegacyRegistrationEnabled { get; set; } = new();

    public bool IsLegacyRegistrationEnabled(string deviceSystem) =>
        TryNormalizeDeviceSystem(deviceSystem, out var normalized)
        && normalized switch
        {
            "Windows" => LegacyRegistrationEnabled.Windows,
            "iPadOS" => LegacyRegistrationEnabled.IpadOS,
            "Android" => LegacyRegistrationEnabled.Android,
            "iOS" => LegacyRegistrationEnabled.IOS,
            "AndroidTablet" => LegacyRegistrationEnabled.AndroidTablet,
            _ => true,
        };

    public static bool TryNormalizeDeviceSystem(string? deviceSystem, out string normalized)
    {
        var candidate = deviceSystem?.Trim();
        // AndroidTablet = 安卓平板上的大屏收银 App，与手持 Android 是两个独立开关。
        foreach (var supported in new[] { "Windows", "iPadOS", "Android", "iOS", "AndroidTablet" })
        {
            if (string.Equals(candidate, supported, StringComparison.OrdinalIgnoreCase))
            {
                normalized = supported;
                return true;
            }
        }

        normalized = string.Empty;
        return false;
    }
}

public sealed class LegacyRegistrationOptions
{
    public bool Windows { get; set; } = true;
    public bool IpadOS { get; set; } = true;
    public bool Android { get; set; } = true;
    public bool IOS { get; set; } = true;
    public bool AndroidTablet { get; set; } = true;
}
