using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public static class DeviceActivationCodeNormalizer
{
    private const string Prefix = "HBDEV1-";
    private const int SegmentLength = 26;
    private const int NormalizedLength = 60;
    private const string AllowedSegmentCharacters = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static bool TryNormalize(string? value, out string normalized)
    {
        if (string.IsNullOrEmpty(value))
        {
            normalized = string.Empty;
            return false;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsAsciiWhitespace(character))
            {
                continue;
            }

            if (character > 0x7F)
            {
                normalized = string.Empty;
                return false;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        normalized = builder.ToString();
        if (normalized.Length != NormalizedLength
            || !normalized.StartsWith(Prefix, StringComparison.Ordinal)
            || normalized[Prefix.Length + SegmentLength] != '-')
        {
            normalized = string.Empty;
            return false;
        }

        for (var index = Prefix.Length; index < normalized.Length; index++)
        {
            if (index == Prefix.Length + SegmentLength)
            {
                continue;
            }

            if (!AllowedSegmentCharacters.Contains(normalized[index], StringComparison.Ordinal))
            {
                normalized = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiWhitespace(char character)
    {
        return character is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
    }
}

public enum DeviceActivationRecoveryMode
{
    Redeem = 0,
    Rebind = 1
}

public sealed record DeviceActivationRecovery(
    string ActivationCode,
    DeviceActivationRecoveryMode Mode,
    string ApiEndpoint,
    string HardwareId,
    DateTimeOffset UpdatedAtUtc);

public sealed class DeviceActivationRecoveryUnreadableException : Exception
{
    public DeviceActivationRecoveryUnreadableException(Exception? innerException = null)
        : base("The pending device activation recovery record cannot be read safely.", innerException)
    {
    }
}

public interface IDeviceActivationRecoveryStore
{
    Task<DeviceActivationRecovery?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        string activationCode,
        DeviceActivationRecoveryMode mode,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalDeviceActivationRecoveryStore : IDeviceActivationRecoveryStore
{
    internal static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hbpos.Client",
        "device-activation-recovery.pending");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly IDeviceAuthorizationProtector _protector;
    private readonly ApiRuntimeEndpointState _endpointState;
    private readonly IDeviceFingerprintService _fingerprintService;
    private readonly string _filePath;

    public LocalDeviceActivationRecoveryStore(
        IDeviceAuthorizationProtector protector,
        ApiRuntimeEndpointState endpointState,
        IDeviceFingerprintService fingerprintService)
        : this(protector, endpointState, fingerprintService, DefaultFilePath)
    {
    }

    internal LocalDeviceActivationRecoveryStore(
        IDeviceAuthorizationProtector protector,
        ApiRuntimeEndpointState endpointState,
        IDeviceFingerprintService fingerprintService,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(endpointState);
        ArgumentNullException.ThrowIfNull(fingerprintService);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _protector = protector;
        _endpointState = endpointState;
        _fingerprintService = fingerprintService;
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<DeviceActivationRecovery?> GetAsync(CancellationToken cancellationToken = default)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recovery = await ReadRecoveryAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(recovery.ApiEndpoint, GetCurrentEndpoint(), StringComparison.Ordinal)
                || !string.Equals(recovery.HardwareId, GetCurrentHardwareId(), StringComparison.Ordinal))
            {
                // 恢复意图只能回放到原服务器和原硬件；上下文漂移时保留原文件并 fail closed。
                throw new DeviceActivationRecoveryUnreadableException();
            }

            return recovery;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (DeviceActivationRecoveryUnreadableException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new DeviceActivationRecoveryUnreadableException(ex);
        }
        catch (IOException ex)
        {
            throw new DeviceActivationRecoveryUnreadableException(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeviceActivationRecoveryUnreadableException(ex);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task SaveAsync(
        string activationCode,
        DeviceActivationRecoveryMode mode,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        if (!DeviceActivationCodeNormalizer.TryNormalize(activationCode, out var normalizedCode))
        {
            throw new ArgumentException(
                "Activation code must match HBDEV1-<26 Crockford characters>-<26 Crockford characters>.",
                nameof(activationCode));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        var currentEndpoint = GetCurrentEndpoint();
        var currentHardwareId = GetCurrentHardwareId();
        if (!string.Equals(hardwareId, currentHardwareId, StringComparison.Ordinal))
        {
            throw new DeviceActivationRecoveryUnreadableException();
        }

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryFilePath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (Directory.Exists(_filePath))
            {
                throw new DeviceActivationRecoveryUnreadableException();
            }

            if (File.Exists(_filePath))
            {
                DeviceActivationRecovery existing;
                try
                {
                    existing = await ReadRecoveryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DeviceActivationRecoveryUnreadableException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    throw new DeviceActivationRecoveryUnreadableException(ex);
                }

                if (string.Equals(existing.ActivationCode, normalizedCode, StringComparison.Ordinal)
                    && existing.Mode == mode
                    && string.Equals(existing.ApiEndpoint, currentEndpoint, StringComparison.Ordinal)
                    && string.Equals(existing.HardwareId, hardwareId, StringComparison.Ordinal))
                {
                    // 四元组完全一致才视为同一次消费意图；绝不能刷新或替换原恢复快照。
                    return;
                }

                throw new InvalidOperationException(
                    "A different device activation recovery is already pending and must be resolved first.");
            }

            var intentJson = JsonSerializer.Serialize(
                new RecoveryIntent(normalizedCode, currentEndpoint, hardwareId),
                JsonOptions);
            var protectedIntent = _protector.Protect(intentJson);
            if (string.IsNullOrWhiteSpace(protectedIntent))
            {
                throw new InvalidOperationException("Activation intent could not be protected for recovery.");
            }

            var snapshot = new RecoverySnapshot(
                protectedIntent,
                mode,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Activation recovery path has no parent directory.");
            Directory.CreateDirectory(directory);

            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 同目录临时文件先完整落盘，再原子发布首份恢复记录，避免崩溃留下半份开通码。
            File.Move(temporaryFilePath, _filePath);
        }
        finally
        {
            DeleteIfExists(temporaryFilePath);
            _fileGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteIfExists(_filePath);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task<DeviceActivationRecovery> ReadRecoveryAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        var snapshot = await JsonSerializer.DeserializeAsync<RecoverySnapshot>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null
            || string.IsNullOrWhiteSpace(snapshot.RecoveryIntentProtected)
            || !Enum.IsDefined(snapshot.Mode)
            || !DateTimeOffset.TryParse(
                snapshot.UpdatedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var updatedAtUtc))
        {
            throw new DeviceActivationRecoveryUnreadableException();
        }

        var intentJson = _protector.Unprotect(snapshot.RecoveryIntentProtected);
        if (string.IsNullOrWhiteSpace(intentJson))
        {
            throw new DeviceActivationRecoveryUnreadableException();
        }

        RecoveryIntent? intent;
        try
        {
            intent = JsonSerializer.Deserialize<RecoveryIntent>(intentJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new DeviceActivationRecoveryUnreadableException(ex);
        }

        if (intent is null
            || !DeviceActivationCodeNormalizer.TryNormalize(intent.ActivationCode, out var normalizedCode)
            || string.IsNullOrWhiteSpace(intent.ApiEndpoint)
            || string.IsNullOrWhiteSpace(intent.HardwareId))
        {
            throw new DeviceActivationRecoveryUnreadableException();
        }

        string normalizedEndpoint;
        try
        {
            normalizedEndpoint = new Uri(
                ApiServerSettingsService.NormalizeAddress(intent.ApiEndpoint),
                UriKind.Absolute).AbsoluteUri;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new DeviceActivationRecoveryUnreadableException(ex);
        }

        return new DeviceActivationRecovery(
            normalizedCode,
            snapshot.Mode,
            normalizedEndpoint,
            intent.HardwareId,
            updatedAtUtc);
    }

    private string GetCurrentEndpoint()
    {
        // 恢复数据按规范入口记录：别名入口与规范入口是同一台服务器，已记录的历史恢复数据（hotbargain.vip）依然有效。
        return ApiEndpointAliases.Canonicalize(_endpointState.CurrentAddress).AbsoluteUri;
    }

    private string GetCurrentHardwareId()
    {
        var hardwareId = _fingerprintService.GetHardwareId();
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            throw new DeviceActivationRecoveryUnreadableException();
        }

        return hardwareId;
    }

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private sealed record RecoveryIntent(
        string ActivationCode,
        string ApiEndpoint,
        string HardwareId);

    private sealed record RecoverySnapshot(
        string RecoveryIntentProtected,
        DeviceActivationRecoveryMode Mode,
        string UpdatedAtUtc);
}
