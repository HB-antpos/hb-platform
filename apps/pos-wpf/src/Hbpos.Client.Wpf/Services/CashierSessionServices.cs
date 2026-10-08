using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Wpf.Services;

public interface ICashierSessionContext
{
    CashierSessionDto? CurrentSession { get; }

    void SetCurrent(CashierSessionDto session);

    void Clear();

    bool TrySetCurrent(CashierSessionDto expected, CashierSessionDto replacement);

    bool TryClear(CashierSessionDto expected);

    bool HasPermission(string permissionCode);

    bool RequirePermission(string permissionCode, out string message);
}

public sealed class CashierSessionContext(TimeProvider? timeProvider = null) : ICashierSessionContext
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _sessionGate = new();
    private CashierSessionDto? _currentSession;
    private static readonly string[] AllPosTerminalPermissions = Permissions.GetAllPermissions()
        .Select(permission => permission.Code)
        .Where(code => code.StartsWith("Permissions.PosTerminal.", StringComparison.Ordinal))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public CashierSessionDto? CurrentSession
    {
        get
        {
            lock (_sessionGate)
            {
                return _currentSession;
            }
        }
    }

    public void SetCurrent(CashierSessionDto session)
    {
        lock (_sessionGate)
        {
            _currentSession = session;
        }
    }

    public void Clear()
    {
        lock (_sessionGate)
        {
            _currentSession = null;
        }
    }

    public bool TrySetCurrent(CashierSessionDto expected, CashierSessionDto replacement)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_currentSession, expected))
            {
                return false;
            }

            _currentSession = replacement;
            return true;
        }
    }

    public bool TryClear(CashierSessionDto expected)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_currentSession, expected))
            {
                return false;
            }

            _currentSession = null;
            return true;
        }
    }

    public bool HasPermission(string permissionCode)
    {
        var currentSession = CurrentSession;
        if (currentSession is null)
        {
            return false;
        }

        if (currentSession.IsEmergencyOverride &&
            currentSession.AuthorizationExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            TryClear(currentSession);
            return false;
        }

        // 紧急授权和后台超管只在本机上下文授予 POS 端权限，不扩展为后台权限。
        if (currentSession.IsEmergencyOverride || currentSession.IsSuperAdmin)
        {
            return permissionCode.StartsWith("Permissions.PosTerminal.", StringComparison.Ordinal);
        }

        var effectivePermissionCodes = Permissions.ExpandPermissionCodes(currentSession.PermissionCodes);
        if (effectivePermissionCodes.Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // 滚动升级窗口：旧服务只会返回整组 LineDiscount/OrderDiscount；新服务的部分授权不会生成旧码。
        return IsLegacyDiscountCompatible(permissionCode, effectivePermissionCodes);
    }

    public bool RequirePermission(string permissionCode, out string message)
    {
        if (HasPermission(permissionCode))
        {
            message = string.Empty;
            return true;
        }

        message = GetDeniedMessage(permissionCode);
        return false;
    }

    public static CashierSessionDto CreateEmergencyOverride(
        string storeCode,
        string deviceCode,
        Guid grantId,
        DateTimeOffset expiresAtUtc,
        string authorizationToken)
    {
        // 小票和历史直接使用 CashierName；紧急授权使用固定名称和可审计的 GrantId。
        return new CashierSessionDto(
            $"EMERGENCY:{grantId:N}",
            $"EMERGENCY:{grantId:N}",
            "EMERGENCY",
            storeCode,
            deviceCode,
            ["EmergencyOverride"],
            AllPosTerminalPermissions,
            [storeCode],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: true,
            AuthorizationToken: authorizationToken,
            AuthorizationExpiresAtUtc: expiresAtUtc,
            EmergencyGrantId: grantId.ToString("D"));
    }

    public static PosSessionState ApplyToSession(PosSessionState state, CashierSessionDto cashier)
    {
        return state with
        {
            StoreCode = cashier.StoreCode,
            DeviceCode = cashier.DeviceCode,
            CashierId = cashier.CashierId,
            CashierName = cashier.CashierName,
            CashierSession = cashier
        };
    }

    private static string GetDeniedMessage(string permissionCode)
    {
        return permissionCode switch
        {
            Permissions.PosTerminal.Sales.AddItem => "当前收银员没有添加商品权限",
            Permissions.PosTerminal.Sales.AddOpenItem => "当前收银员没有添加自定义商品权限",
            Permissions.PosTerminal.Sales.RemoveLine => "当前收银员没有删行权限",
            Permissions.PosTerminal.Sales.ChangeQuantity => "当前收银员没有改数量权限",
            Permissions.PosTerminal.Sales.ChangePrice => "当前收银员没有改价权限",
            Permissions.PosTerminal.Sales.LineDiscount => "当前收银员没有行折扣权限",
            Permissions.PosTerminal.Sales.OrderDiscount => "当前收银员没有整单折扣权限",
            Permissions.PosTerminal.Sales.LineManualDiscount => "当前收银员没有单行自定义折扣或金额减免权限",
            Permissions.PosTerminal.Sales.LineQuickDiscount10Percent => "当前收银员没有单行快速折扣 10% 权限",
            Permissions.PosTerminal.Sales.LineQuickDiscount20Percent => "当前收银员没有单行快速折扣 20% 权限",
            Permissions.PosTerminal.Sales.LineQuickDiscount30Percent => "当前收银员没有单行快速折扣 30% 权限",
            Permissions.PosTerminal.Sales.LineQuickDiscount40Percent => "当前收银员没有单行快速折扣 40% 权限",
            Permissions.PosTerminal.Sales.LineQuickDiscount50Percent => "当前收银员没有单行快速折扣 50% 权限",
            Permissions.PosTerminal.Sales.OrderManualDiscount => "当前收银员没有整单自定义折扣或金额减免权限",
            Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent => "当前收银员没有整单快速折扣 10% 权限",
            Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent => "当前收银员没有整单快速折扣 20% 权限",
            Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent => "当前收银员没有整单快速折扣 30% 权限",
            Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent => "当前收银员没有整单快速折扣 40% 权限",
            Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent => "当前收银员没有整单快速折扣 50% 权限",
            Permissions.PosTerminal.Sales.ClearCart => "当前收银员没有清空购物车权限",
            Permissions.PosTerminal.Sales.HoldOrder => "当前收银员没有挂单权限",
            Permissions.PosTerminal.Sales.RecallOrder => "当前收银员没有取单权限",
            Permissions.PosTerminal.Payment.View => "当前收银员没有进入付款页权限",
            Permissions.PosTerminal.Payment.TakeCash => "当前收银员没有收现金权限",
            Permissions.PosTerminal.Payment.TakeCard => "当前收银员没有收卡权限",
            Permissions.PosTerminal.Payment.TakeVoucher => "当前收银员没有收券权限",
            Permissions.PosTerminal.Payment.RemoveTender => "当前收银员没有移除付款权限",
            Permissions.PosTerminal.Payment.Confirm => "当前收银员没有确认付款权限",
            Permissions.PosTerminal.Returns.View => "当前收银员没有进入退货页权限",
            Permissions.PosTerminal.Returns.AddReceiptLine => "当前收银员没有添加小票退货行权限",
            Permissions.PosTerminal.Returns.AddNoReceiptItem => "当前收银员没有添加无票退货商品权限",
            Permissions.PosTerminal.Returns.Confirm => "当前收银员没有确认退货权限",
            Permissions.PosTerminal.SpecialProducts.View => "当前收银员没有进入特价商品页权限",
            Permissions.PosTerminal.SpecialProducts.AddToCart => "当前收银员没有添加特价商品权限",
            Permissions.PosTerminal.SpecialProducts.Manage => "当前收银员没有维护特价商品权限",
            Permissions.PosTerminal.History.View => "当前收银员没有查看交易历史权限",
            Permissions.PosTerminal.History.Recall => "当前收银员没有调取历史交易权限",
            Permissions.PosTerminal.History.Reprint => "当前收银员没有历史小票补打权限",
            Permissions.PosTerminal.CashDrawer.Open => "当前收银员没有开钱箱权限",
            Permissions.PosTerminal.Receipt.PrintLast => "当前收银员没有补打小票权限",
            Permissions.PosTerminal.Settings.View => "当前收银员没有查看设置权限",
            Permissions.PosTerminal.Settings.PaymentTerminal => "当前收银员没有支付终端设置权限",
            Permissions.PosTerminal.Settings.ReceiptPrinter => "当前收银员没有小票打印机设置权限",
            Permissions.PosTerminal.Settings.CatalogDownload => "当前收银员没有下载商品资料权限",
            Permissions.PosTerminal.Settings.CatalogReset => "当前收银员没有重置商品资料权限",
            Permissions.PosTerminal.Settings.TestDataReset => "当前收银员没有重置测试销售数据权限",
            Permissions.PosTerminal.Settings.DeviceRegistration => "当前收银员没有重新注册设备权限",
            Permissions.PosTerminal.Settings.AppUpdate => "当前收银员没有检查应用更新权限",
            Permissions.PosTerminal.DailyClose.View => "当前收银员没有查看日结权限",
            Permissions.PosTerminal.DailyClose.Save => "当前收银员没有保存日结权限",
            Permissions.PosTerminal.DailyClose.Reprint => "当前收银员没有补打日结权限",
            Permissions.PosTerminal.Installments.View => "当前收银员没有查看分期权限",
            Permissions.PosTerminal.Installments.Create => "当前收银员没有创建分期权限",
            Permissions.PosTerminal.Installments.AddRepayment => "当前收银员没有添加分期还款权限",
            Permissions.PosTerminal.Installments.Cancel => "当前收银员没有取消分期权限",
            Permissions.PosTerminal.Installments.ConfirmPickup => "当前收银员没有确认分期取货权限",
            Permissions.PosTerminal.Installments.AmendLines => "当前收银员没有修改分期商品权限",
            Permissions.PosTerminal.CustomerDisplay.Manage => "当前收银员没有管理客显权限",
            Permissions.PosTerminal.System.Sync => "当前收银员没有手动同步权限",
            _ => "当前收银员没有权限"
        };
    }

    private static bool IsLegacyDiscountCompatible(
        string permissionCode,
        IReadOnlyCollection<string> effectivePermissionCodes)
    {
        var legacyPermissionCode = permissionCode switch
        {
            Permissions.PosTerminal.Sales.LineManualDiscount or
            Permissions.PosTerminal.Sales.LineQuickDiscount10Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount20Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount30Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount40Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount50Percent => Permissions.PosTerminal.Sales.LineDiscount,
            Permissions.PosTerminal.Sales.OrderManualDiscount or
            Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent => Permissions.PosTerminal.Sales.OrderDiscount,
            _ => null
        };

        return legacyPermissionCode is not null &&
            effectivePermissionCodes.Contains(legacyPermissionCode, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CashierLoginAttempt(
    bool IsOnlineRejected,
    bool IsApiUnavailable,
    CashierSessionDto? Session,
    string Message,
    string? ErrorCode = null)
{
    public static CashierLoginAttempt OnlineAccepted(CashierSessionDto session) => new(false, false, session, string.Empty);

    public static CashierLoginAttempt OnlineRejected(string message, string? errorCode = null) =>
        new(true, false, null, message, errorCode ?? "CASHIER_LOGIN_FAILED");

    public static CashierLoginAttempt ApiUnavailable() =>
        new(false, true, null, "收银员登录服务不可用", "CASHIER_LOGIN_API_UNAVAILABLE");
}

public sealed record CashierLoginResult(
    bool Succeeded,
    CashierSessionDto? Session,
    string Message,
    string? ErrorCode = null)
{
    public static CashierLoginResult Success(CashierSessionDto session) => new(true, session, string.Empty);

    public static CashierLoginResult Fail(string message, string? errorCode = null) =>
        new(false, null, message, errorCode ?? "CASHIER_LOGIN_FAILED");
}

public interface ICashierLoginApiClient
{
    Task<CashierLoginAttempt> LoginAsync(
        CashierBarcodeLoginRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CashierLoginApiClient(HttpClient httpClient) : ICashierLoginApiClient
{
    private const string LoginPath = "api/v1/cashiers/barcode-login";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CashierLoginAttempt> LoginAsync(
        CashierBarcodeLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        // 日志只带门店/设备、状态码与 errorCode；收银员条码（request.UserBarcode）绝不入日志。
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(LoginPath, request, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                if (IsServiceUnavailable(response.StatusCode))
                {
                    LogUnavailable(request, "http-status", statusCode, stopwatch.ElapsedMilliseconds);
                    return CashierLoginAttempt.ApiUnavailable();
                }

                ApiResult<CashierSessionDto>? failed;
                try
                {
                    failed = await response.Content.ReadFromJsonAsync<ApiResult<CashierSessionDto>>(cancellationToken);
                }
                catch (JsonException ex)
                {
                    LogRejected(request, statusCode, errorCode: null, stopwatch.ElapsedMilliseconds, "unparsable-error-body", ex);
                    return CashierLoginAttempt.OnlineRejected("收银员条码无效或已停用");
                }

                LogRejected(request, statusCode, failed?.ErrorCode, stopwatch.ElapsedMilliseconds, "http-status");
                return CashierLoginAttempt.OnlineRejected(
                    failed?.Message ?? "收银员条码无效或已停用",
                    failed?.ErrorCode);
            }

            // 2xx 只有带 ApiResult 信封才是服务端结论：空对象、null、Wi-Fi 认证页 JSON 等不是 POS API 的响应，
            // 按不可用处理以允许离线缓存兜底，不能误报"条码无效或已停用"（与 DeviceApiClient 口径一致）。
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            if (!DeviceApiClient.IsApiResultEnvelope(document.RootElement))
            {
                LogUnavailable(request, "not-api-envelope", statusCode, stopwatch.ElapsedMilliseconds);
                return CashierLoginAttempt.ApiUnavailable();
            }

            var result = document.RootElement.Deserialize<ApiResult<CashierSessionDto>>(JsonOptions)!;
            if (!result.Success)
            {
                LogRejected(request, statusCode, result.ErrorCode, stopwatch.ElapsedMilliseconds, "success-false");
                return CashierLoginAttempt.OnlineRejected(
                    result.Message ?? "收银员条码无效或已停用",
                    result.ErrorCode);
            }

            // success=true 却没有会话数据属于形状异常，不是拒绝。
            if (result.Data is null)
            {
                LogUnavailable(request, "missing-session-data", statusCode, stopwatch.ElapsedMilliseconds);
                return CashierLoginAttempt.ApiUnavailable();
            }

            return CashierLoginAttempt.OnlineAccepted(result.Data);
        }
        catch (JsonException ex)
        {
            // 关键逻辑：服务端 5xx/网关错误常返回 HTML 或空响应，这不是在线拒绝，应允许离线缓存兜底。
            LogUnavailable(request, "unparsable-response", statusCode, stopwatch.ElapsedMilliseconds, ex);
            return CashierLoginAttempt.ApiUnavailable();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 行为保持不变（一律按服务不可用处理），日志里区分调用方取消与 HttpClient 超时。
            var reason = ex is TaskCanceledException
                ? cancellationToken.IsCancellationRequested ? "canceled" : "timeout"
                : "network";
            if (reason == "canceled")
            {
                ConsoleLog.WriteInformation(
                    "CashierLogin",
                    $"cashier login request canceled by caller store={request.StoreCode} device={request.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds}",
                    CreateLogContext(request, statusCode, errorCode: null, stopwatch.ElapsedMilliseconds, reason));
            }
            else
            {
                LogUnavailable(request, reason, statusCode, stopwatch.ElapsedMilliseconds, ex);
            }

            return CashierLoginAttempt.ApiUnavailable();
        }
    }

    private static void LogUnavailable(
        CashierBarcodeLoginRequest request,
        string reason,
        int? statusCode,
        long elapsedMs,
        Exception? exception = null)
    {
        ConsoleLog.WriteWarning(
            "CashierLogin",
            $"cashier login api unavailable; falling back to offline cache reason={reason} status={statusCode?.ToString() ?? "-"} store={request.StoreCode} device={request.DeviceCode} elapsedMs={elapsedMs}",
            CreateLogContext(request, statusCode, errorCode: null, elapsedMs, reason),
            exception);
    }

    private static void LogRejected(
        CashierBarcodeLoginRequest request,
        int? statusCode,
        string? errorCode,
        long elapsedMs,
        string reason,
        Exception? exception = null)
    {
        ConsoleLog.WriteWarning(
            "CashierLogin",
            $"cashier login rejected online reason={reason} status={statusCode?.ToString() ?? "-"} errorCode={errorCode ?? "-"} store={request.StoreCode} device={request.DeviceCode} elapsedMs={elapsedMs}",
            CreateLogContext(request, statusCode, errorCode, elapsedMs, reason),
            exception);
    }

    private static ApplicationLogContext CreateLogContext(
        CashierBarcodeLoginRequest request,
        int? statusCode,
        string? errorCode,
        long elapsedMs,
        string reason)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["storeCode"] = request.StoreCode,
            ["deviceCode"] = request.DeviceCode,
            ["elapsedMs"] = elapsedMs,
            ["reason"] = reason
        };
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            properties["errorCode"] = errorCode;
        }

        return new ApplicationLogContext(
            RequestPath: LoginPath,
            RequestMethod: "POST",
            StatusCode: statusCode,
            Properties: properties);
    }

    /// <summary>
    /// 服务端 cashiers/barcode-login 只以 401（条码无效/停用、设备认证失败）、403（设备越权）和 400（请求体校验）表达在线拒绝；
    /// 404/405 只会来自后端发布、网关切换或路由未就绪，与 DeviceApiClient.IsGatewayStatus 口径一致按不可用处理，
    /// 让收银员在发布窗口内仍能走离线缓存登录，而不是被误报"条码无效或已停用"。
    /// </summary>
    private static bool IsServiceUnavailable(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode >= 500 ||
            statusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests;
    }
}

public interface ICashierLoginService
{
    Task<CashierLoginResult> LoginAsync(
        string storeCode,
        string deviceCode,
        string userBarcode,
        bool attemptOnline = true,
        CancellationToken cancellationToken = default);
}

public interface ICashierSessionCacheUpdater
{
    Task UpdateCachedSessionAsync(
        CashierSessionDto session,
        CancellationToken cancellationToken = default);

    Task RemoveCachedSessionAsync(
        CashierSessionDto session,
        CancellationToken cancellationToken = default);
}

public sealed class CashierLoginService(
    ICashierLoginApiClient apiClient,
    ILocalAppSettingsRepository settingsRepository,
    IDeviceAuthorizationProtector protector,
    IEmergencyLoginTokenService? emergencyLoginTokenService = null)
    : ICashierLoginService, ICashierSessionCacheUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, CashierSessionCacheReference> _cacheKeysBySessionIdentity =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public async Task<CashierLoginResult> LoginAsync(
        string storeCode,
        string deviceCode,
        string userBarcode,
        bool attemptOnline = true,
        CancellationToken cancellationToken = default)
    {
        if (EmergencyLoginTokenCodec.HasSupportedPrefix(userBarcode))
        {
            // 紧急二维码必须在普通条码 API 和商品查询之前分流，避免令牌外泄。
            return emergencyLoginTokenService is null
                ? CashierLoginResult.Fail("紧急登录服务不可用", "EMERGENCY_LOGIN_SERVICE_UNAVAILABLE")
                : await emergencyLoginTokenService.LoginAsync(
                    userBarcode,
                    storeCode,
                    deviceCode,
                    cancellationToken);
        }

        if (!attemptOnline)
        {
            // 关键逻辑：连接状态已明确离线时直接读取加密缓存，避免等待登录 API 超时。
            var cacheStopwatch = Stopwatch.StartNew();
            var offlineCached = await ReadCachedSessionAsync(
                storeCode,
                deviceCode,
                userBarcode,
                cancellationToken);
            cacheStopwatch.Stop();
            ConsoleLog.Write(
                "CashierLoginTiming",
                $"cacheReadMs={cacheStopwatch.ElapsedMilliseconds} cacheHit={offlineCached is not null} attemptOnline=false");
            if (offlineCached is not null)
            {
                return CashierLoginResult.Success(offlineCached);
            }

            var unavailable = CashierLoginAttempt.ApiUnavailable();
            return CashierLoginResult.Fail(unavailable.Message, unavailable.ErrorCode);
        }

        var request = new CashierBarcodeLoginRequest(storeCode, userBarcode, deviceCode);
        var onlineStopwatch = Stopwatch.StartNew();
        CashierLoginAttempt attempt;
        try
        {
            attempt = await apiClient.LoginAsync(request, cancellationToken);
        }
        finally
        {
            onlineStopwatch.Stop();
            ConsoleLog.Write(
                "CashierLoginTiming",
                $"onlineAttemptMs={onlineStopwatch.ElapsedMilliseconds} attemptOnline=true");
        }
        if (attempt.IsOnlineRejected)
        {
            // 在线明确拒绝只影响本次登录；保留旧缓存供后续真正断网时继续营业。
            return CashierLoginResult.Fail(attempt.Message, attempt.ErrorCode);
        }

        if (attempt.Session is not null)
        {
            await CacheSessionAsync(storeCode, deviceCode, userBarcode, attempt.Session, cancellationToken);
            return CashierLoginResult.Success(attempt.Session);
        }

        if (!attempt.IsApiUnavailable)
        {
            return CashierLoginResult.Fail(attempt.Message, attempt.ErrorCode);
        }

        var cacheFallbackStopwatch = Stopwatch.StartNew();
        var cached = await ReadCachedSessionAsync(storeCode, deviceCode, userBarcode, cancellationToken);
        cacheFallbackStopwatch.Stop();
        ConsoleLog.Write(
            "CashierLoginTiming",
            $"cacheReadMs={cacheFallbackStopwatch.ElapsedMilliseconds} cacheHit={cached is not null} attemptOnline=true");
        // 走离线缓存降级路径：原因已由 CashierLoginApiClient 记 Warning，这里补记降级结果（不含条码）。
        ConsoleLog.WriteInformation(
            "CashierLogin",
            $"cashier login degraded to offline cache reason=api-unavailable errorCode={attempt.ErrorCode ?? "-"} cacheHit={cached is not null} store={storeCode} device={deviceCode}",
            new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["storeCode"] = storeCode,
                ["deviceCode"] = deviceCode,
                ["reason"] = "api-unavailable",
                ["mode"] = "offline-cache",
                ["result"] = cached is not null ? "cache-hit" : "cache-miss"
            }));
        return cached is null
            ? CashierLoginResult.Fail(attempt.Message, attempt.ErrorCode)
            : CashierLoginResult.Success(cached);
    }

    private async Task CacheSessionAsync(
        string storeCode,
        string deviceCode,
        string userBarcode,
        CashierSessionDto session,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(session with { IsOfflineCached = false }, JsonOptions);
        var protectedJson = protector.Protect(json)
            ?? throw new InvalidOperationException("无法保护收银员离线缓存。");
        var cacheKey = BuildCacheKey(storeCode, deviceCode, userBarcode);
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            await settingsRepository.SetValueAsync(cacheKey, protectedJson, cancellationToken);
            _cacheKeysBySessionIdentity[BuildSessionIdentity(session)] =
                CashierSessionCacheReference.Create(cacheKey, session);

            // 新格式写入成功后再清理同一身份的旧明文缓存，避免迁移期间丢失可用登录。
            await settingsRepository.DeleteValueAsync(
                BuildLegacyCacheKey(storeCode, deviceCode, userBarcode),
                cancellationToken);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<CashierSessionDto?> ReadCachedSessionAsync(
        string storeCode,
        string deviceCode,
        string userBarcode,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(storeCode, deviceCode, userBarcode);
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            var protectedJson = await settingsRepository.GetValueAsync(cacheKey, cancellationToken);
            var json = protector.Unprotect(protectedJson);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            CashierSessionDto? session;
            try
            {
                session = JsonSerializer.Deserialize<CashierSessionDto>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            if (session?.AllowedStoreCodes.Contains(storeCode, StringComparer.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            // 中文注释：离线只复用同门店、同设备、同条码缓存，并显式标记来源。
            var offlineSession = session with
            {
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                IsOfflineCached = true,
                IsEmergencyOverride = false
            };
            _cacheKeysBySessionIdentity[BuildSessionIdentity(offlineSession)] =
                CashierSessionCacheReference.Create(cacheKey, offlineSession);
            return offlineSession;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task UpdateCachedSessionAsync(
        CashierSessionDto session,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(session with { IsOfflineCached = false }, JsonOptions);
        var protectedJson = protector.Protect(json)
            ?? throw new InvalidOperationException("无法保护收银员离线缓存。");
        var identity = BuildSessionIdentity(session);
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (!_cacheKeysBySessionIdentity.TryGetValue(identity, out var cacheReference))
            {
                throw new InvalidOperationException("找不到当前收银员离线缓存定位信息。");
            }

            await settingsRepository.SetValueAsync(
                cacheReference.CacheKey,
                protectedJson,
                cancellationToken);
            _cacheKeysBySessionIdentity[identity] =
                CashierSessionCacheReference.Create(cacheReference.CacheKey, session);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task RemoveCachedSessionAsync(
        CashierSessionDto session,
        CancellationToken cancellationToken = default)
    {
        var identity = BuildSessionIdentity(session);
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (!_cacheKeysBySessionIdentity.TryGetValue(identity, out var cacheReference) ||
                !cacheReference.MatchesVersion(session))
            {
                return;
            }

            _cacheKeysBySessionIdentity.TryRemove(identity, out _);
            await settingsRepository.DeleteValueAsync(cacheReference.CacheKey, cancellationToken);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static string BuildCacheKey(string storeCode, string deviceCode, string userBarcode)
    {
        // 键名也不能泄露收银员条码；摘要只用于定位，不承担密码学认证。
        var scope = $"{storeCode.Trim()}\n{deviceCode.Trim()}\n{userBarcode.Trim()}";
        return $"cashier-session:v2:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))}";
    }

    private static string BuildLegacyCacheKey(string storeCode, string deviceCode, string userBarcode)
    {
        return $"cashier-session:{storeCode.Trim()}:{deviceCode.Trim()}:{userBarcode.Trim()}";
    }

    private static string BuildSessionIdentity(CashierSessionDto session) =>
        $"{session.UserGuid.Trim()}\n{session.StoreCode.Trim()}\n{session.DeviceCode.Trim()}";

    private sealed record CashierSessionCacheReference(
        string CacheKey,
        string? AuthorizationToken,
        DateTimeOffset? AuthorizationExpiresAtUtc)
    {
        public static CashierSessionCacheReference Create(
            string cacheKey,
            CashierSessionDto session) =>
            new(cacheKey, session.AuthorizationToken, session.AuthorizationExpiresAtUtc);

        public bool MatchesVersion(CashierSessionDto session) =>
            string.Equals(AuthorizationToken, session.AuthorizationToken, StringComparison.Ordinal) &&
            AuthorizationExpiresAtUtc == session.AuthorizationExpiresAtUtc;
    }
}
