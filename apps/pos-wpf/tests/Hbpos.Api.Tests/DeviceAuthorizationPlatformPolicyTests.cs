using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class DeviceAuthorizationPlatformPolicyTests
{
    [Fact]
    public void Authorization_query_reads_the_registered_device_platform()
    {
        Assert.Contains(
            "[设备系统] AS DeviceSystem",
            DeviceAuthorizationService.AuthorizationSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Authorization_query_reads_the_transaction_permission()
    {
        Assert.Contains(
            "[是否允许交易] AS AllowTransactions",
            DeviceAuthorizationService.AuthorizationSql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Windows", "HW-001", "", true)]
    [InlineData("iPadOS", "HW-001", "", false)]
    [InlineData("iPadOS", "HW-001", "HW-001", true)]
    [InlineData("iPadOS", "HW-001", "HW-OTHER", false)]
    [InlineData("Android", "HW-001", "", false)]
    [InlineData("Android", "HW-001", "HW-001", true)]
    [InlineData("AndroidTablet", "HW-001", "", false)]
    [InlineData("AndroidTablet", "HW-001", "HW-001", true)]
    [InlineData("AndroidTablet", "HW-001", "HW-OTHER", false)]
    [InlineData("androidtablet", "HW-001", "HW-001", false)]
    [InlineData("iOS", "HW-001", "", false)]
    [InlineData("iOS", "HW-001", "HW-001", true)]
    [InlineData("watchOS", "HW-001", "HW-001", false)]
    public void Hardware_header_policy_keeps_windows_compatibility_and_requires_mobile_exact_match(
        string deviceSystem,
        string registeredHardwareId,
        string submittedHardwareId,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(
                deviceSystem,
                registeredHardwareId,
                submittedHardwareId));
    }

    [Fact]
    public void Blank_database_platform_is_the_only_legacy_value_that_falls_back_to_windows()
    {
        Assert.True(DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(null, "HW-001", null));
        Assert.True(DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted("  ", "HW-001", null));
    }
}
