using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class DeviceActivationRecoveryStoreTests
{
    private const string ValidCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string OtherValidCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXY0";
    private const string ApiEndpoint = "https://hotbargain.vip/pos-api/";
    private const string HardwareId = "HW-001";

    [Theory]
    [InlineData(
        " hbdev1-0123456789abcdefghjkmnpqrs-6789abcdefghjkmnpqrstvwxyz ",
        ValidCode)]
    [InlineData(
        "HBDEV1-0123456789ABC\tDEFGHJKMNPQRS-6789ABCDEFGHJK\r\nMNPQRSTVWXYZ",
        ValidCode)]
    public void Activation_code_normalizer_removes_ascii_whitespace_and_accepts_exact_format(
        string input,
        string expected)
    {
        var accepted = DeviceActivationCodeNormalizer.TryNormalize(input, out var normalized);

        Assert.True(accepted);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HBDEV-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ")]
    [InlineData("HBDEV1-0123456789ABCDEFGHIKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ")]
    [InlineData("HBDEV1-0123456789ABCDEFGHJKMNOPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ")]
    [InlineData("HBDEV1-0123456789ABCDEFGHJKMNPQRS_6789ABCDEFGHJKMNPQRSTVWXYZ")]
    [InlineData("HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ\u00A0")]
    [InlineData("HBDEV1-0123456789ABCDEFGHJKMNPQRſ-6789ABCDEFGHJKMNPQRSTVWXYZ")]
    public void Activation_code_normalizer_rejects_wrong_prefix_length_separator_or_ambiguous_letters(string input)
    {
        Assert.False(DeviceActivationCodeNormalizer.TryNormalize(input, out _));
    }

    [Fact]
    public async Task Recovery_store_protects_code_at_rest_and_clears_after_credentials_are_saved()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);

            await recoveryStore.SaveAsync(
                ValidCode,
                DeviceActivationRecoveryMode.Rebind,
                HardwareId);

            Assert.True(File.Exists(recoveryFilePath));
            Assert.False(File.Exists(Path.Combine(directoryPath, "hbpos_client.db")));
            var rawFile = await File.ReadAllBytesAsync(recoveryFilePath);
            Assert.DoesNotContain(
                ValidCode,
                System.Text.Encoding.UTF8.GetString(rawFile),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                ApiEndpoint,
                System.Text.Encoding.UTF8.GetString(rawFile),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                HardwareId,
                System.Text.Encoding.UTF8.GetString(rawFile),
                StringComparison.Ordinal);

            var restored = await recoveryStore.GetAsync();
            Assert.NotNull(restored);
            Assert.Equal(ValidCode, restored.ActivationCode);
            Assert.Equal(DeviceActivationRecoveryMode.Rebind, restored.Mode);
            Assert.Equal(ApiEndpoint, restored.ApiEndpoint);
            Assert.Equal(HardwareId, restored.HardwareId);

            await recoveryStore.ClearAsync();

            Assert.Null(await recoveryStore.GetAsync());
            Assert.False(File.Exists(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_store_same_code_and_mode_is_idempotent_without_replacing_snapshot()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);
            await recoveryStore.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Rebind, HardwareId);
            var original = Assert.IsType<DeviceActivationRecovery>(await recoveryStore.GetAsync());
            var originalBytes = await File.ReadAllBytesAsync(recoveryFilePath);

            await Task.Delay(20);
            await recoveryStore.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Rebind, HardwareId);

            var restored = Assert.IsType<DeviceActivationRecovery>(await recoveryStore.GetAsync());
            Assert.Equal(original.UpdatedAtUtc, restored.UpdatedAtUtc);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(OtherValidCode, DeviceActivationRecoveryMode.Rebind)]
    [InlineData(ValidCode, DeviceActivationRecoveryMode.Redeem)]
    public async Task Recovery_store_rejects_different_pending_intent_without_overwriting_original(
        string activationCode,
        DeviceActivationRecoveryMode mode)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);
            await recoveryStore.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Rebind, HardwareId);
            var originalBytes = await File.ReadAllBytesAsync(recoveryFilePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recoveryStore.SaveAsync(activationCode, mode, HardwareId));

            var restored = Assert.IsType<DeviceActivationRecovery>(await recoveryStore.GetAsync());
            Assert.Equal(ValidCode, restored.ActivationCode);
            Assert.Equal(DeviceActivationRecoveryMode.Rebind, restored.Mode);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_saved_on_the_direct_entry_stays_valid_when_the_pos_alias_entry_becomes_current()
    {
        // 老设备升级后默认入口换成 pos.hotbargain.vip：升级前留下的未完成激活恢复数据按规范入口记录，
        // 仍然必须被识别为"同一台服务器"，不能报「恢复数据不可读」。
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var endpointState = new ApiRuntimeEndpointState(ApiEndpoint);
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath, endpointState);
            await recoveryStore.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Rebind, HardwareId);

            endpointState.Switch("https://pos.hotbargain.vip/pos-api/");

            var restored = await recoveryStore.GetAsync();
            Assert.NotNull(restored);
            Assert.Equal(ValidCode, restored.ActivationCode);
            Assert.Equal(ApiEndpoint, restored.ApiEndpoint);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("https://other.example.com/pos-api/", HardwareId)]
    [InlineData(ApiEndpoint, "HW-002")]
    public async Task Recovery_store_rejects_changed_endpoint_or_hardware_without_overwriting_original(
        string nextEndpoint,
        string nextHardwareId)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var endpointState = new ApiRuntimeEndpointState(ApiEndpoint);
            var fingerprint = new MutableFingerprintService(HardwareId);
            var recoveryStore = CreateStore(
                new PrefixAuthorizationProtector(),
                recoveryFilePath,
                endpointState,
                fingerprint);
            await recoveryStore.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Rebind, HardwareId);
            var originalBytes = await File.ReadAllBytesAsync(recoveryFilePath);

            if (!string.Equals(nextEndpoint, ApiEndpoint, StringComparison.Ordinal))
            {
                endpointState.Switch(nextEndpoint);
            }

            fingerprint.HardwareId = nextHardwareId;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recoveryStore.SaveAsync(
                    ValidCode,
                    DeviceActivationRecoveryMode.Rebind,
                    nextHardwareId));
            await Assert.ThrowsAsync<DeviceActivationRecoveryUnreadableException>(
                () => recoveryStore.GetAsync());

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_store_keeps_corrupted_snapshot_and_reports_fail_closed_state()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        Directory.CreateDirectory(directoryPath);
        try
        {
            await File.WriteAllTextAsync(recoveryFilePath, "{not-valid-json");
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);

            await Assert.ThrowsAsync<DeviceActivationRecoveryUnreadableException>(
                () => recoveryStore.GetAsync());

            Assert.True(File.Exists(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_store_keeps_legacy_unbound_snapshot_and_reports_fail_closed_state()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        Directory.CreateDirectory(directoryPath);
        try
        {
            var protectedCode = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ValidCode));
            await File.WriteAllTextAsync(
                recoveryFilePath,
                $$"""
                {"activationCodeProtected":"{{protectedCode}}","mode":0,"updatedAtUtc":"2026-08-27T00:00:00.0000000+00:00"}
                """);
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);
            var originalBytes = await File.ReadAllBytesAsync(recoveryFilePath);

            await Assert.ThrowsAsync<DeviceActivationRecoveryUnreadableException>(
                () => recoveryStore.GetAsync());

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_store_keeps_snapshot_when_protected_code_cannot_be_unprotected()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        try
        {
            var writer = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);
            await writer.SaveAsync(ValidCode, DeviceActivationRecoveryMode.Redeem, HardwareId);
            var reader = CreateStore(new FailedUnprotectAuthorizationProtector(), recoveryFilePath);

            await Assert.ThrowsAsync<DeviceActivationRecoveryUnreadableException>(
                () => reader.GetAsync());

            Assert.True(File.Exists(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Recovery_store_keeps_existing_unreadable_path_and_reports_fail_closed_state()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"hbpos-activation-recovery-{Guid.NewGuid():N}");
        var recoveryFilePath = Path.Combine(directoryPath, "device-activation-recovery.pending");
        Directory.CreateDirectory(recoveryFilePath);
        try
        {
            var recoveryStore = CreateStore(new PrefixAuthorizationProtector(), recoveryFilePath);

            await Assert.ThrowsAsync<DeviceActivationRecoveryUnreadableException>(
                () => recoveryStore.GetAsync());

            Assert.True(Directory.Exists(recoveryFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static LocalDeviceActivationRecoveryStore CreateStore(
        IDeviceAuthorizationProtector protector,
        string recoveryFilePath,
        ApiRuntimeEndpointState? endpointState = null,
        IDeviceFingerprintService? fingerprintService = null)
    {
        return new LocalDeviceActivationRecoveryStore(
            protector,
            endpointState ?? new ApiRuntimeEndpointState(ApiEndpoint),
            fingerprintService ?? new MutableFingerprintService(HardwareId),
            recoveryFilePath);
    }

    private sealed class MutableFingerprintService(string hardwareId) : IDeviceFingerprintService
    {
        public string HardwareId { get; set; } = hardwareId;

        public string GetHardwareId() => HardwareId;
    }

    private sealed class PrefixAuthorizationProtector : IDeviceAuthorizationProtector
    {
        public string? Protect(string? value) => value is null
            ? null
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

        public string? Unprotect(string? protectedValue) => string.IsNullOrWhiteSpace(protectedValue)
            ? null
            : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }

    private sealed class FailedUnprotectAuthorizationProtector : IDeviceAuthorizationProtector
    {
        public string? Protect(string? value) => null;

        public string? Unprotect(string? protectedValue) => null;
    }
}
