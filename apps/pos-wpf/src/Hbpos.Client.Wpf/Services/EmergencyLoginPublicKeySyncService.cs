using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hbpos.Contracts.EmergencyLogin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hbpos.Client.Wpf.Services;

public sealed record EmergencyLoginPublicKeyFetchResult(
    bool NotModified,
    EmergencyLoginPublicKeyPackage? Package)
{
    public static EmergencyLoginPublicKeyFetchResult Unchanged() => new(true, null);

    public static EmergencyLoginPublicKeyFetchResult Changed(EmergencyLoginPublicKeyPackage package) =>
        new(false, package);
}

public interface IEmergencyLoginPublicKeyApiClient
{
    Task<EmergencyLoginPublicKeyFetchResult> GetAsync(
        long? currentVersion,
        CancellationToken cancellationToken = default);

    Task<EmergencyLoginPublicKeyAckClientResult> AcknowledgeAsync(
        long version,
        CancellationToken cancellationToken = default);
}

public sealed record EmergencyLoginPublicKeyAckClientResult(bool Acknowledged, long Version);

public sealed class EmergencyLoginPublicKeyApiClient(HttpClient httpClient)
    : IEmergencyLoginPublicKeyApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EmergencyLoginPublicKeyFetchResult> GetAsync(
        long? currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/v1/emergency-login/public-keys");
        if (currentVersion is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(
                $"\"emergency-login-keys-v{currentVersion.Value}\""));
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return EmergencyLoginPublicKeyFetchResult.Unchanged();
        }

        response.EnsureSuccessStatusCode();
        var package = await response.Content.ReadFromJsonAsync<EmergencyLoginPublicKeyPackage>(
            JsonOptions,
            cancellationToken);
        return package is null
            ? throw new JsonException("紧急登录公钥接口返回空响应。")
            : EmergencyLoginPublicKeyFetchResult.Changed(package);
    }

    public async Task<EmergencyLoginPublicKeyAckClientResult> AcknowledgeAsync(
        long version,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/emergency-login/public-keys/ack",
            new EmergencyLoginPublicKeyAckRequest(version),
            JsonOptions,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<EmergencyLoginPublicKeyAckResponse>(
                JsonOptions,
                cancellationToken);
            return new EmergencyLoginPublicKeyAckClientResult(false, conflict?.Version ?? version);
        }

        response.EnsureSuccessStatusCode();
        var acknowledged = await response.Content.ReadFromJsonAsync<EmergencyLoginPublicKeyAckResponse>(
            JsonOptions,
            cancellationToken);
        return new EmergencyLoginPublicKeyAckClientResult(true, acknowledged?.Version ?? version);
    }
}

public interface IEmergencyLoginPublicKeyCache
{
    Task<EmergencyLoginPublicKeyPackage?> GetAsync(CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        EmergencyLoginPublicKeyPackage package,
        CancellationToken cancellationToken = default);
}

public sealed class EmergencyLoginPublicKeyCache(
    ILocalAppSettingsRepository settingsRepository,
    IDeviceAuthorizationProtector protector) : IEmergencyLoginPublicKeyCache
{
    private const string CacheKey = "emergency-login:public-keys:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EmergencyLoginPublicKeyPackage?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var protectedValue = await settingsRepository.GetValueAsync(CacheKey, cancellationToken);
        var json = protector.Unprotect(protectedValue);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EmergencyLoginPublicKeyPackage>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task ReplaceAsync(
        EmergencyLoginPublicKeyPackage package,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(package, JsonOptions);
        var protectedValue = protector.Protect(json)
            ?? throw new InvalidOperationException("无法加密紧急登录公钥缓存。");
        // 单个 AppSettings 行的 UPSERT 是原子操作，失败时旧公钥包保持不变。
        return settingsRepository.SetValueAsync(CacheKey, protectedValue, cancellationToken);
    }
}

public interface IEmergencyLoginPublicKeySyncService
{
    Task<bool> SyncAsync(CancellationToken cancellationToken = default);
}

public sealed class EmergencyLoginPublicKeySyncService(
    IEmergencyLoginPublicKeyApiClient apiClient,
    IEmergencyLoginPublicKeyCache cache,
    ILogger<EmergencyLoginPublicKeySyncService>? logger = null) : IEmergencyLoginPublicKeySyncService
{
    private const string LogCategory = "EmergencyLogin";
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    // 后台失败时每分钟重试：同一失败原因连续出现只记一次。字段只在 _syncGate 内读写（服务为单例且同步串行），无需额外锁。
    private string? _lastFailureKey;
    private int _consecutiveFailures;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var current = await cache.GetAsync(cancellationToken);
            var validCurrent = current is not null && EmergencyLoginPublicKeyValidator.TryValidate(current)
                ? current
                : null;
            // 坏缓存不得参与条件请求，否则错误 304 会让客户端永远无法恢复。
            var fetched = await apiClient.GetAsync(validCurrent?.Version, cancellationToken);
            if (fetched.NotModified)
            {
                if (validCurrent is null)
                {
                    return RecordFailure("not-modified-without-valid-cache");
                }

                return RecordOutcome(await AcknowledgeWithSingleRetryAsync(validCurrent.Version, cancellationToken));
            }

            var package = fetched.Package;
            if (package is null ||
                !EmergencyLoginPublicKeyValidator.TryValidate(package) ||
                validCurrent is not null && package.Version < validCurrent.Version)
            {
                return RecordFailure(
                    package is null ? "empty-package"
                    : validCurrent is not null && package.Version < validCurrent.Version
                        ? $"version-downgrade serverVersion={package.Version} cachedVersion={validCurrent.Version}"
                        : "invalid-package");
            }

            if (validCurrent is null || package.Version > validCurrent.Version)
            {
                // 关键逻辑：整包校验通过后才原子替换缓存；ACK 必须晚于持久化成功。
                await cache.ReplaceAsync(package, cancellationToken);
            }

            return RecordOutcome(await AcknowledgeWithSingleRetryAsync(package.Version, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "同步紧急登录公钥失败，继续保留本地旧缓存");
            return RecordFailure("exception", ex);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<bool> AcknowledgeWithSingleRetryAsync(
        long version,
        CancellationToken cancellationToken)
    {
        var acknowledgement = await apiClient.AcknowledgeAsync(version, cancellationToken);
        if (acknowledgement.Acknowledged && acknowledgement.Version == version)
        {
            return true;
        }

        // 仅允许一次无 ETag 即时重拉，持续轮换时返回失败交给后台重试节流处理。
        var refreshed = await apiClient.GetAsync(null, cancellationToken);
        if (refreshed.NotModified ||
            refreshed.Package is null ||
            !EmergencyLoginPublicKeyValidator.TryValidate(refreshed.Package) ||
            refreshed.Package.Version < Math.Max(version, acknowledgement.Version))
        {
            _pendingAckFailureReason =
                $"ack-mismatch-refetch-unusable version={version} ackVersion={acknowledgement.Version} acknowledged={acknowledgement.Acknowledged}";
            return false;
        }

        await cache.ReplaceAsync(refreshed.Package, cancellationToken);
        var retried = await apiClient.AcknowledgeAsync(refreshed.Package.Version, cancellationToken);
        var acknowledged = retried.Acknowledged && retried.Version == refreshed.Package.Version;
        if (!acknowledged)
        {
            _pendingAckFailureReason =
                $"ack-retry-mismatch version={refreshed.Package.Version} ackVersion={retried.Version} acknowledged={retried.Acknowledged}";
        }

        return acknowledged;
    }

    // ACK 失败的具体原因由 AcknowledgeWithSingleRetryAsync 写入，RecordOutcome 统一记日志（同样只在 _syncGate 内访问）。
    private string? _pendingAckFailureReason;

    private bool RecordOutcome(bool succeeded)
    {
        var ackReason = _pendingAckFailureReason;
        _pendingAckFailureReason = null;
        if (!succeeded)
        {
            return RecordFailure(ackReason ?? "ack-failed");
        }

        if (_lastFailureKey is not null)
        {
            ConsoleLog.WriteInformation(
                LogCategory,
                $"emergency login public key sync recovered failedAttempts={_consecutiveFailures}",
                new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["attemptCount"] = _consecutiveFailures
                }));
        }

        _lastFailureKey = null;
        _consecutiveFailures = 0;
        return true;
    }

    /// <summary>
    /// 记录同步失败并返回 false：业务性失败（无效包/版本回退/ACK 不一致）记 Information，异常记 Warning；
    /// 同一原因连续出现只记第一次。公钥包内容不入日志。
    /// </summary>
    private bool RecordFailure(string reason, Exception? exception = null)
    {
        _consecutiveFailures++;
        var statusCode = exception is HttpRequestException { StatusCode: { } status } ? (int)status : (int?)null;
        var failureKey = exception is null
            ? reason
            : $"{reason}|{exception.GetType().Name}|{statusCode}";
        if (string.Equals(_lastFailureKey, failureKey, StringComparison.Ordinal))
        {
            return false;
        }

        _lastFailureKey = failureKey;
        var context = new ApplicationLogContext(
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["reason"] = exception is null ? reason.Split(' ')[0] : reason,
                ["attemptCount"] = _consecutiveFailures
            });
        if (exception is null)
        {
            ConsoleLog.WriteInformation(
                LogCategory,
                $"emergency login public key sync not completed; keeping local cache reason={reason}",
                context);
        }
        else
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"emergency login public key sync failed; keeping local cache error={exception.GetType().Name} status={statusCode?.ToString() ?? "-"}",
                context,
                exception);
        }

        return false;
    }
}

internal static class EmergencyLoginPublicKeyValidator
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    internal static bool TryValidate(EmergencyLoginPublicKeyPackage package)
    {
        if (package.Version < 0 || package.Keys is null || package.Keys.Count == 0)
        {
            return false;
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in package.Keys)
        {
            if (!IsValidKeyId(key.Kid) ||
                !keyIds.Add(key.Kid) ||
                !string.Equals(key.Algorithm, "ES256", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(key.PublicKeyPem) ||
                !key.PublicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
                key.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(key.PublicKeyPem);
                var parameters = ecdsa.ExportParameters(false);
                if (ecdsa.KeySize != 256 ||
                    !string.Equals(parameters.Curve.Oid.Value, P256Oid, StringComparison.Ordinal))
                {
                    return false;
                }

                var fingerprint = Convert.ToHexString(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()));
                if (!string.Equals(fingerprint, key.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(package.ActiveKeyId) || keyIds.Contains(package.ActiveKeyId);
    }

    private static bool IsValidKeyId(string? keyId) =>
        !string.IsNullOrEmpty(keyId) &&
        keyId.Length <= 32 &&
        keyId.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');
}

public sealed class EmergencyLoginPublicKeySyncHostedService(
    IEmergencyLoginPublicKeySyncService syncService,
    DeviceAuthorizationState authorizationState,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(6);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal static TimeSpan SyncIntervalForTests => SyncInterval;

    internal static bool ShouldSyncForTests(
        DateTimeOffset now,
        DateTimeOffset? lastAttempt,
        DateTimeOffset? lastSuccess,
        bool newlyAuthorized)
    {
        if (newlyAuthorized)
        {
            return true;
        }

        return lastSuccess is null
            ? lastAttempt is null || now - lastAttempt >= RetryInterval
            : now - lastSuccess >= SyncInterval &&
                (lastAttempt is null || lastAttempt <= lastSuccess || now - lastAttempt >= RetryInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastAttempt = null;
        DateTimeOffset? lastSuccess = null;
        string? lastAuthorizedDevice = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var authorization = authorizationState.Current;
            var now = _timeProvider.GetUtcNow();
            var authorizedDevice = authorization is null
                ? null
                : $"{authorization.StoreCode}\u001f{authorization.DeviceCode}\u001f{authorization.HardwareId}";
            var newlyAuthorized = authorizedDevice is not null &&
                !string.Equals(lastAuthorizedDevice, authorizedDevice, StringComparison.Ordinal);
            var due = ShouldSyncForTests(now, lastAttempt, lastSuccess, newlyAuthorized: false);

            if (authorization is not null && (newlyAuthorized || due))
            {
                lastAuthorizedDevice = authorizedDevice;
                lastAttempt = now;
                if (await syncService.SyncAsync(stoppingToken))
                {
                    lastSuccess = now;
                }
            }
            else if (authorization is null)
            {
                lastAuthorizedDevice = null;
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
