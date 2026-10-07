using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using BlazorApp.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Attendance;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed class AttendanceQrPanelViewModel : ObservableObject, IDisposable
{
    private const string IdentitySettingsKey = "attendance.qr.identity.v2";
    private const string TrustedTimeSettingsKey = "attendance.qr.trusted-time.v1";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(AttendanceQrTokenCodec.TokenLifetimeSeconds);
    private static readonly TimeSpan RefreshLead = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly IAttendanceSigningKeyApiClient _apiClient;
    private readonly IConnectivityApiClient _connectivity;
    private readonly ILocalDeviceRepository _deviceRepository;
    private readonly IDeviceFingerprintService _fingerprint;
    private readonly ILocalAppSettingsRepository _settings;
    private readonly IDeviceAuthorizationProtector _protector;
    private readonly ILocalizationService _localization;
    private readonly TimeProvider _timeProvider;
    private readonly ApiRuntimeEndpointState? _endpointState;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    // 失败日志去重状态：刷新循环、tick、语言切换回调可能并发写日志，用独立锁保护。
    private readonly object _failureLogSync = new();
    private string? _lastLoggedFailureKey;
    private int _consecutiveFailures;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _tickLoopTask;
    private readonly Task? _refreshLoopTask;

    private SigningIdentity? _identity;
    private TrustedTime? _trustedTime;
    private LocalDeviceCache? _device;
    private DateTimeOffset? _tokenExpiresAtUtc;
    private DateTimeOffset? _lastObservedLocalUtc;
    private bool _isOnline;
    private bool _requiresOnlineResync;
    private bool _setupFailed;
    private bool _disposed;
    private long _endpointVersion;
    private string? _qrToken;
    private BitmapImage? _qrImage;
    private string _storeText = string.Empty;
    private string _deviceText = string.Empty;
    private string _messageText = string.Empty;
    private string _verificationStatusText = string.Empty;
    private int _secondsRemaining;

    public AttendanceQrPanelViewModel(
        IAttendanceSigningKeyApiClient apiClient,
        IConnectivityApiClient connectivity,
        ILocalDeviceRepository deviceRepository,
        IDeviceFingerprintService fingerprint,
        ILocalAppSettingsRepository settings,
        IDeviceAuthorizationProtector protector,
        ILocalizationService localization,
        TimeProvider? timeProvider = null,
        bool startTimer = true,
        ApiRuntimeEndpointState? endpointState = null,
        TimeSpan? tickInterval = null,
        TimeSpan? refreshInterval = null)
    {
        _apiClient = apiClient;
        _connectivity = connectivity;
        _deviceRepository = deviceRepository;
        _fingerprint = fingerprint;
        _settings = settings;
        _protector = protector;
        _localization = localization;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _endpointState = endpointState;
        _tickInterval = ValidateInterval(tickInterval ?? DefaultTickInterval, nameof(tickInterval));
        _refreshInterval = ValidateInterval(refreshInterval ?? DefaultRefreshInterval, nameof(refreshInterval));
        _endpointVersion = endpointState?.Version ?? 0;
        _localization.CultureChanged += OnCultureChanged;
        ApplyLocalizedState();
        if (startTimer)
        {
            // 签码 tick 与网络刷新必须是独立任务；PUT 卡住时，过期码仍会按秒清除并轮换。
            _tickLoopTask = Task.Run(() => RunTickLoopAsync(_stop.Token));
            _refreshLoopTask = Task.Run(() => RunRefreshLoopAsync(_stop.Token));
        }
    }

    public string? QrToken { get => _qrToken; private set => SetProperty(ref _qrToken, value); }
    public BitmapImage? QrImage { get => _qrImage; private set => SetProperty(ref _qrImage, value); }
    public string StoreText { get => _storeText; private set => SetProperty(ref _storeText, value); }
    public string DeviceText { get => _deviceText; private set => SetProperty(ref _deviceText, value); }
    public string MessageText { get => _messageText; private set => SetProperty(ref _messageText, value); }
    public string VerificationStatusText { get => _verificationStatusText; private set => SetProperty(ref _verificationStatusText, value); }
    public int SecondsRemaining { get => _secondsRemaining; private set => SetProperty(ref _secondsRemaining, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        SigningIdentity? transientIdentity = null;
        var onlineConfirmed = false;
        try
        {
            if (_endpointState is not null && _endpointState.Version != _endpointVersion)
            {
                await _stateGate.WaitAsync(cancellationToken);
                try
                {
                    // API 地址变化代表本地数据分区变化，旧分区密钥与可信时间不可复用。
                    _endpointVersion = _endpointState.Version;
                    ClearIdentityState();
                }
                finally
                {
                    _stateGate.Release();
                }
            }

            var device = await _deviceRepository.GetLatestAsync(cancellationToken);
            var identity = await LoadProtectedAsync<SigningIdentity>(IdentitySettingsKey, cancellationToken);
            transientIdentity = identity;
            var trustedTime = await LoadProtectedAsync<TrustedTime>(TrustedTimeSettingsKey, cancellationToken);
            var hardwareId = _fingerprint.GetHardwareId();
            if (!IsUsableDevice(device, hardwareId))
            {
                ClearIdentityKey(identity);
                transientIdentity = null;
                await ClearIdentityAndDeviceAsync(device, cancellationToken);
                return;
            }

            if (identity is not null && !identity.Matches(device!, hardwareId))
            {
                // 设备换店、重注册或硬件变化后，旧私钥绝不能继续签发新码。
                ClearIdentityKey(identity);
                transientIdentity = null;
                identity = null;
                trustedTime = null;
                await DeletePersistedIdentityAsync(cancellationToken);
            }

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                _device = device;
                StoreText = FormatStore(device);
                DeviceText = FormatDevice(device);
                ReplaceIdentity(identity);
                transientIdentity = null;
                _trustedTime = trustedTime;
                // 离线刷新会重读初次校时记录，但不得降低本次进程已经观测到的时间高水位。
                _lastObservedLocalUtc = MaxUtc(_lastObservedLocalUtc, trustedTime?.LocalUtc);
                TickCore();
            }
            finally
            {
                _stateGate.Release();
            }

            if (!await _connectivity.CheckOnlineAsync(cancellationToken))
            {
                await MarkOfflineAndTickAsync(cancellationToken);
                return;
            }
            onlineConfirmed = true;

            if (identity is null || !HasValidKey(identity.KeyMaterial))
            {
                await ClearIdentityOnlyAsync(cancellationToken);
                await DeletePersistedIdentityAsync(cancellationToken);
                ClearIdentityKey(identity);
                identity = CreateIdentity(device!, hardwareId);
                transientIdentity = identity;
            }

            AttendanceSigningKeyRegistrationResponse response;
            try
            {
                // 每次在线刷新都执行设备认证 PUT；同一活动密钥由服务端幂等读取，不产生周期写入。
                response = await RegisterAsync(identity, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // 冲突表示密钥已撤销或登记不一致：立即清码，生成新 kid 后只重试一次。
                await ClearIdentityOnlyAsync(cancellationToken);
                await DeletePersistedIdentityAsync(cancellationToken);
                ClearIdentityKey(identity);
                identity = CreateIdentity(device!, hardwareId);
                transientIdentity = identity;
                response = await RegisterAsync(identity, cancellationToken);
            }

            identity = identity with { RegisteredAtUtc = ToUtc(response.RegisteredAtUtc) };
            var calibratedTime = new TrustedTime(ToUtc(response.ServerTimeUtc), UtcNow());
            await SaveIdentityAsync(identity, cancellationToken);
            await SaveProtectedAsync(TrustedTimeSettingsKey, calibratedTime, cancellationToken);

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                ReplaceIdentity(identity);
                transientIdentity = null;
                _trustedTime = calibratedTime;
                _lastObservedLocalUtc = calibratedTime.LocalUtc;
                _isOnline = true;
                _setupFailed = false;
                // 关键逻辑：只有登记与可信时间保存都成功后，才解除时钟回拨锁存。
                _requiresOnlineResync = false;
                TickCore();
            }
            finally
            {
                _stateGate.Release();
            }

            LogRefreshRecovered();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or CryptographicException or JsonException or InvalidOperationException or Win32Exception or SqliteException)
        {
            if (ex is not HttpRequestException || onlineConfirmed)
            {
                await MarkSetupFailedAsync(ex, cancellationToken);
            }
            else
            {
                await MarkOfflineAndTickAsync(cancellationToken);
            }
        }
        finally
        {
            // 关键逻辑：仅清理未转交给 ViewModel 的反序列化或新建身份密钥。
            ClearIdentityKey(transientIdentity);
            _refreshGate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            TickCore();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localization.CultureChanged -= OnCultureChanged;
        var cancellationTask = _stop.CancelAsync();
        var loops = new[] { _tickLoopTask, _refreshLoopTask }.OfType<Task>().ToArray();
        _ = Task.WhenAll(loops.Append(cancellationTask)).ContinueWith(
            completedTask =>
            {
                if (completedTask.Exception is not null)
                {
                    LogFailure("dispose", completedTask.Exception);
                }

                ReplaceIdentity(null);
                _stop.Dispose();
                _refreshGate.Dispose();
                _stateGate.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunTickLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await TickSafeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        await RefreshSafeAsync(cancellationToken);
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshSafeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshSafeAsync(CancellationToken cancellationToken)
    {
        try { await RefreshAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { await MarkSetupFailedAsync(ex, cancellationToken, "refresh"); }
    }

    private async Task TickSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stateGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            try { TickCore(); }
            catch (Exception ex)
            {
                // tick 异常与清码必须在同一状态锁内提交，避免覆盖并发刷新刚生成的新二维码。
                ClearQr(_localization.T("attendance.qr.enableOnline"));
                LogFailure("tick", ex);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void TickCore()
    {
        if (_setupFailed)
        {
            // 启用失败由自动刷新重试处理；普通倒计时不得覆盖这条可操作提示。
            ClearQr(_localization.T("attendance.qr.setupFailed"));
            return;
        }

        if (_identity is null || _trustedTime is null || !HasValidKey(_identity.KeyMaterial))
        {
            ClearQr(_localization.T("attendance.qr.enableOnline"));
            return;
        }

        if (_requiresOnlineResync)
        {
            ClearQr(_localization.T("attendance.qr.clockInvalid"));
            return;
        }

        var localNow = UtcNow();
        if (localNow < _trustedTime.LocalUtc || (_lastObservedLocalUtc is not null && localNow < _lastObservedLocalUtc))
        {
            _requiresOnlineResync = true;
            // 任意本机 UTC 回拨都会使离线可信时间失效，避免延长旧二维码寿命。
            ClearQr(_localization.T("attendance.qr.clockInvalid"));
            return;
        }

        _lastObservedLocalUtc = localNow;
        var trustedUtc = _trustedTime.ServerUtc + (localNow - _trustedTime.LocalUtc);
        if (_tokenExpiresAtUtc is null || trustedUtc >= _tokenExpiresAtUtc - RefreshLead)
        {
            // 提前 5 秒清除旧图并签发新图，绑定层不会短暂保留临近失效的二维码。
            ClearQr(string.Empty);
            IssueQr(trustedUtc);
        }

        SecondsRemaining = Math.Clamp(
            (int)Math.Ceiling((_tokenExpiresAtUtc!.Value - trustedUtc).TotalSeconds),
            0,
            AttendanceQrTokenCodec.TokenLifetimeSeconds);
        VerificationStatusText = _localization.T(_isOnline ? "attendance.qr.onlineVerified" : "attendance.qr.offlineSigned");
        MessageText = _localization.T("attendance.qr.scanHint");
    }

    private void IssueQr(DateTimeOffset trustedUtc)
    {
        var payload = new AttendanceQrTokenPayload
        {
            TokenId = Guid.NewGuid(),
            StoreCode = _identity!.StoreCode,
            DeviceCode = _identity.DeviceCode,
            IssuedAtUtc = trustedUtc.UtcDateTime,
        };
        var key = _identity.KeyMaterial.ToArray();
        try
        {
            var token = AttendanceQrTokenCodec.Encrypt(payload, _identity.Kid, key);
            QrToken = token;
            QrImage = CreateQrImage(token);
            _tokenExpiresAtUtc = trustedUtc.Add(TokenLifetime);
        }
        finally
        {
            // 关键逻辑：临时密钥副本用完立即清零，持久密钥仅保留在受保护身份中。
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void ClearQr(string message)
    {
        QrToken = null;
        QrImage = null;
        _tokenExpiresAtUtc = null;
        SecondsRemaining = 0;
        VerificationStatusText = string.Empty;
        MessageText = message;
    }

    private async Task<T?> LoadProtectedAsync<T>(string key, CancellationToken cancellationToken)
    {
        var protectedJson = await _settings.GetValueAsync(key, cancellationToken);
        var json = _protector.Unprotect(protectedJson);
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException)
        {
            await _settings.DeleteValueAsync(key, cancellationToken);
            return default;
        }
    }

    private Task SaveIdentityAsync(SigningIdentity identity, CancellationToken cancellationToken) =>
        SaveProtectedAsync(IdentitySettingsKey, identity, cancellationToken);

    private async Task SaveProtectedAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        var protectedJson = _protector.Protect(JsonSerializer.Serialize(value))
            ?? throw new InvalidOperationException("DPAPI 无法保护考勤二维码配置");
        await _settings.SetValueAsync(key, protectedJson, cancellationToken);
    }

    private Task<AttendanceSigningKeyRegistrationResponse> RegisterAsync(SigningIdentity identity, CancellationToken cancellationToken) =>
        _apiClient.RegisterAsync(
            new AttendanceSigningKeyRegistrationRequest(identity.Kid, "A256GCM", Base64UrlEncode(identity.KeyMaterial)),
            cancellationToken);

    private async Task MarkOfflineAndTickAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            _isOnline = false;
            _setupFailed = false;
            TickCore();
        }
        finally { _stateGate.Release(); }
    }

    private async Task MarkSetupFailedAsync(Exception ex, CancellationToken cancellationToken, string phase = "setup")
    {
        LogFailure(phase, ex);

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            _isOnline = true;
            _setupFailed = true;
            ClearQr(_localization.T("attendance.qr.setupFailed"));
        }
        finally { _stateGate.Release(); }
    }

    private void LogFailure(string phase, Exception ex)
    {
        try
        {
            var statusCode = ex is HttpRequestException { StatusCode: { } status }
                ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            // 刷新每 15 秒一次：同一 phase+异常类型+状态码 连续出现只记第一次，变化或恢复后才再记。
            var failureKey = $"{phase}|{ex.GetType().Name}|{statusCode}";
            lock (_failureLogSync)
            {
                _consecutiveFailures++;
                if (string.Equals(_lastLoggedFailureKey, failureKey, StringComparison.Ordinal))
                {
                    return;
                }

                _lastLoggedFailureKey = failureKey;
            }

            ConsoleLog.WriteError("AttendanceQr", $"phase={phase} error={ex.GetType().Name} status={statusCode}");
        }
        catch (Exception)
        {
            // 日志基础设施失败不能再次冲击二维码刷新或 UI 事件线程。
        }
    }

    private void LogRefreshRecovered()
    {
        try
        {
            int failures;
            lock (_failureLogSync)
            {
                if (_lastLoggedFailureKey is null)
                {
                    return;
                }

                failures = _consecutiveFailures;
                _lastLoggedFailureKey = null;
                _consecutiveFailures = 0;
            }

            ConsoleLog.WriteInformation(
                "AttendanceQr",
                $"refresh recovered failedAttempts={failures}",
                new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["attemptCount"] = failures
                }));
        }
        catch (Exception)
        {
            // 日志基础设施失败不能再次冲击二维码刷新或 UI 事件线程。
        }
    }

    private async Task ClearIdentityOnlyAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try { ClearIdentityState(); }
        finally { _stateGate.Release(); }
    }

    private async Task ClearIdentityAndDeviceAsync(LocalDeviceCache? device, CancellationToken cancellationToken)
    {
        await ClearIdentityOnlyAsync(cancellationToken);
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            _device = device;
            StoreText = FormatStore(device);
            DeviceText = FormatDevice(device);
        }
        finally { _stateGate.Release(); }
        await DeletePersistedIdentityAsync(cancellationToken);
    }

    private void ClearIdentityState()
    {
        ReplaceIdentity(null);
        _trustedTime = null;
        _lastObservedLocalUtc = null;
        ClearQr(_localization.T("attendance.qr.enableOnline"));
    }

    private void ReplaceIdentity(SigningIdentity? identity)
    {
        if (_identity is not null && !ReferenceEquals(_identity.KeyMaterial, identity?.KeyMaterial))
        {
            // 关键逻辑：身份轮换或失效后立即清零旧 AES 密钥。
            CryptographicOperations.ZeroMemory(_identity.KeyMaterial);
        }

        _identity = identity;
    }

    private static void ClearIdentityKey(SigningIdentity? identity)
    {
        if (identity is not null)
        {
            CryptographicOperations.ZeroMemory(identity.KeyMaterial);
        }
    }

    private async Task DeletePersistedIdentityAsync(CancellationToken cancellationToken)
    {
        await _settings.DeleteValueAsync(IdentitySettingsKey, cancellationToken);
        await _settings.DeleteValueAsync(TrustedTimeSettingsKey, cancellationToken);
    }

    private async void OnCultureChanged(object? sender, EventArgs e)
    {
        try
        {
            await _stateGate.WaitAsync(_stop.Token);
            try
            {
                if (!_disposed)
                {
                    ApplyLocalizedState();
                }
            }
            finally
            {
                _stateGate.Release();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            LogFailure("culture", ex);
        }
    }

    private void ApplyLocalizedState()
    {
        StoreText = FormatStore(_device);
        DeviceText = FormatDevice(_device);
        if (QrToken is not null)
        {
            VerificationStatusText = _localization.T(_isOnline ? "attendance.qr.onlineVerified" : "attendance.qr.offlineSigned");
            MessageText = _localization.T("attendance.qr.scanHint");
        }
        else if (_setupFailed)
        {
            MessageText = _localization.T("attendance.qr.setupFailed");
        }
        else
        {
            MessageText = _localization.T("attendance.qr.enableOnline");
        }
    }

    private string FormatStore(LocalDeviceCache? device) => device is null ? string.Empty : $"{device.StoreName} ({device.StoreCode})";
    private static string FormatDevice(LocalDeviceCache? device) => device?.DeviceCode ?? string.Empty;

    private static bool IsUsableDevice(LocalDeviceCache? device, string hardwareId) =>
        device is { IsAllowed: true }
        && !string.IsNullOrWhiteSpace(device.DeviceCode)
        && !string.IsNullOrWhiteSpace(device.StoreCode)
        && string.Equals(device.HardwareId, hardwareId, StringComparison.Ordinal);

    private static SigningIdentity CreateIdentity(LocalDeviceCache device, string hardwareId)
    {
        var kid = Convert.ToBase64String(RandomNumberGenerator.GetBytes(10)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new SigningIdentity(
            kid,
            RandomNumberGenerator.GetBytes(AttendanceQrTokenCodec.KeyLength),
            device.StoreCode,
            device.DeviceCode,
            hardwareId,
            HashAuthorizationCode(device.AuthorizationCode),
            null);
    }

    private static bool HasValidKey(byte[]? keyMaterial) => keyMaterial?.Length == AttendanceQrTokenCodec.KeyLength;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashAuthorizationCode(string? authorizationCode) =>
        string.IsNullOrWhiteSpace(authorizationCode)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authorizationCode)));

    private static BitmapImage CreateQrImage(string token)
    {
        var rendered = AttendanceQrPngRenderer.Render(token);
        using var stream = new MemoryStream(rendered.PngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
    private static DateTimeOffset ToUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? MaxUtc(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null || left >= right ? left : right;

    private static TimeSpan ValidateInterval(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(parameterName);

    private sealed record SigningIdentity(
        string Kid,
        byte[] KeyMaterial,
        string StoreCode,
        string DeviceCode,
        string HardwareId,
        string AuthorizationMarker,
        DateTimeOffset? RegisteredAtUtc)
    {
        public bool Matches(LocalDeviceCache device, string hardwareId) =>
            string.Equals(StoreCode, device.StoreCode, StringComparison.Ordinal)
            && string.Equals(DeviceCode, device.DeviceCode, StringComparison.Ordinal)
            && string.Equals(HardwareId, hardwareId, StringComparison.Ordinal)
            && string.Equals(AuthorizationMarker, HashAuthorizationCode(device.AuthorizationCode), StringComparison.Ordinal);
    }

    private sealed record TrustedTime(DateTimeOffset ServerUtc, DateTimeOffset LocalUtc);
}
