using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public enum SquareDirectRefreshOutcome
{
    /// <summary>已领到新凭据并加密保存。</summary>
    Refreshed,

    /// <summary>服务端门店开关没开：本机凭据已删除，付款回落中转。</summary>
    ServerDisabled,

    /// <summary>断网、限流、令牌未配置等：保持原状（旧凭据没过期就继续用），下次再试。</summary>
    Failed
}

/// <summary>
/// 「收银机直连 Square」网关（默认关闭）。插在 <see cref="ConfiguredCardTerminalClient"/> 发往 Hbpos API 的 Square 请求之前：
/// 本地开关开启、凭据有效、没有处在失败冷却期时，创建结账 / 轮询 / 取消 / 关闭 / 付款详情查询五个请求直接发给 Square；
/// 任何非 2xx 结果、断网、超时一律返回 null，由调用方照旧走 Hbpos API 中转。
/// 防重复扣款靠两点：回落中转时沿用同一个幂等键与同样的请求体（Square 对同键同体只建一笔结账），
/// 轮询 / 取消 / 付款详情查询是只读或幂等操作，换通道重发不会产生新的扣款。
/// 付款详情必须直连：服务端靠 webhook 或中转轮询才记得住 payment id，直连轮询时它还不认识这笔付款，会按「归属不明」回 403，
/// 已经刷成功的卡就被误判成「结果未知」。退款、恢复中心不经过这里，仍走服务端。
/// </summary>
public interface ISquareDirectGateway
{
    /// <summary>
    /// 尝试直连。返回 Square 的 2xx 原始响应（调用方按原有 raw Square 解析路径读取）；返回 null 表示本次不直连或直连未成功，
    /// 请求尚未被确认，调用方应继续走 Hbpos API 中转。调用方取消会照常抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<HttpResponseMessage?> TrySendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken);

    /// <summary>进付款页预热：本地开关开启时保证凭据有效（接近过期或超过刷新间隔会重新领取），并补登待登记的结账归属。</summary>
    Task WarmUpAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken);

    /// <summary>立即向服务端领取凭据（设置页「刷新」/ 刚打开开关时用），结果同步写入本机服务端开关状态。</summary>
    Task<SquareDirectRefreshOutcome> RefreshCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken);
}

public sealed partial class SquareDirectGateway : ISquareDirectGateway
{
    // 创建结账最多等多久再回落：Square 通常 1~2 秒内响应；回落后中转用同一幂等键，不会重复建单。
    internal static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(8);

    // 轮询 / 取消 / 关闭的单次直连上限。取消+关闭共用 15 秒清理预算（见 SquareCleanupTimeout），所以这里要明显更短，给回落留时间。
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    // 直连出现网络类失败后，冷却这么久不再尝试直连（全程走中转），避免门店网络抖动时每次请求都先白等一次直连超时。
    internal static readonly TimeSpan NetworkFailureCooldown = TimeSpan.FromMinutes(2);

    // 令牌被 Square 拒绝（401/403）：本机凭据已删除，冷却更久，等下次预热重新领取。
    internal static readonly TimeSpan AuthFailureCooldown = TimeSpan.FromMinutes(5);

    // 服务端给的凭据有效期约 12 小时（POS API PR #896）；据此估算凭据「年龄」，每隔 RefreshInterval 重领一次，
    // 总部关闭开关后，收银机最迟约半小时内就会发现并删除令牌，而不是等满 12 小时。
    internal static readonly TimeSpan ServerCredentialLifetime = TimeSpan.FromHours(12);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    // 两次领取尝试之间的最短间隔（含失败的），服务端每台设备每小时最多发 10 次。
    internal static readonly TimeSpan MinRefreshAttemptSpacing = TimeSpan.FromMinutes(10);

    private const int MaxOriginAttempts = 3;
    private static readonly TimeSpan OriginAttemptTimeout = TimeSpan.FromSeconds(10);

    // 走中转前最多等多久让直连创建的结账完成归属登记：登记失败只会让中转查询返回 403，不值得拖住收银。
    private static readonly TimeSpan OriginWaitLimit = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^api/v1/square/checkouts/(?<id>[^/]+)(?:/(?<action>cancel|dismiss))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckoutRoutePattern();

    [GeneratedRegex(@"^api/v1/square/payments/(?<id>[^/]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PaymentRoutePattern();

    private readonly ISquareDirectCredentialStore _store;
    private readonly ISquareDirectApiClient _api;
    private readonly HttpClient _squareClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _inFlightOrigins = new(StringComparer.Ordinal);
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefreshAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPendingFlushAt = DateTimeOffset.MinValue;

    // 进程启动后第一次不知道磁盘上有没有待补登记录，先当「可能有」查一次；查空后置 false，直到本进程再写入新记录。
    private volatile bool _mayHavePendingOrigins = true;

    public SquareDirectGateway(
        ISquareDirectCredentialStore store,
        ISquareDirectApiClient api,
        HttpClient squareClient,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _api = api;
        _squareClient = squareClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>后台正在登记归属的结账数（测试与诊断用）。</summary>
    internal int InFlightOriginCount => _inFlightOrigins.Count;

    public async Task<HttpResponseMessage?> TrySendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        if (!TryParseRoute(method, relativeUrl, body, out var route))
        {
            return null;
        }

        var response = await TrySendDirectAsync(route, body, cancellationToken);
        if (response is null && route.Kind != RouteKind.Create)
        {
            // 要回落到中转查询 / 取消这笔结账了：中转按「门店 + 设备」校验归属，先确保直连创建时的归属登记已完成。
            await EnsureOriginsRegisteredBeforeRelayAsync(cancellationToken);
        }

        return response;
    }

    public async Task WarmUpAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken)
    {
        if (!await _store.IsLocalEnabledAsync(cancellationToken))
        {
            return;
        }

        var credential = await _store.LoadCredentialAsync(environment, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var needsRefresh = credential is null ||
            credential.ValidUntil - now < ServerCredentialLifetime - RefreshInterval;
        if (needsRefresh && now - _lastRefreshAttemptAt >= MinRefreshAttemptSpacing)
        {
            await RefreshCredentialAsync(environment, cancellationToken);
        }

        await FlushPendingOriginsAsync(cancellationToken);
    }

    public async Task<SquareDirectRefreshOutcome> RefreshCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            _lastRefreshAttemptAt = _timeProvider.GetUtcNow();
            var response = await _api.GetCredentialAsync(environment, cancellationToken);
            var now = _timeProvider.GetUtcNow();
            if (string.IsNullOrWhiteSpace(response.AccessToken) ||
                !string.Equals(response.Environment, environment.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Log($"credential refresh rejected environment={environment} reason=invalid-response");
                return SquareDirectRefreshOutcome.Failed;
            }

            // 服务端给的有效期只信到「现在 + 12 小时」：防止异常响应让令牌在本机留存过久。
            var validUntil = response.ValidUntil > now + ServerCredentialLifetime
                ? now + ServerCredentialLifetime
                : response.ValidUntil;
            if (validUntil <= now)
            {
                Log($"credential refresh rejected environment={environment} reason=already-expired");
                return SquareDirectRefreshOutcome.Failed;
            }

            await _store.SaveCredentialAsync(
                new SquareDirectCredential(environment, response.AccessToken, response.CredentialVersion, validUntil),
                CancellationToken.None);
            await _store.SetServerStateAsync(environment, SquareDirectServerState.Enabled, CancellationToken.None);
            // 重新领到凭据说明服务端状态正常，解除可能存在的失败冷却。
            _cooldownUntil = DateTimeOffset.MinValue;
            Log($"credential refreshed environment={environment} version={response.CredentialVersion} validUntil={validUntil:O}");
            return SquareDirectRefreshOutcome.Refreshed;
        }
        catch (SquareDirectApiException ex) when (ex.IsDisabled)
        {
            await _store.DeleteCredentialAsync(environment, CancellationToken.None);
            await _store.SetServerStateAsync(environment, SquareDirectServerState.Disabled, CancellationToken.None);
            Log($"credential refresh: store switch is off environment={environment}; local credential removed");
            return SquareDirectRefreshOutcome.ServerDisabled;
        }
        catch (SquareDirectApiException ex)
        {
            Log($"credential refresh failed environment={environment} http={(int)ex.StatusCode} errorCode={ex.ErrorCode ?? "<none>"}");
            return SquareDirectRefreshOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log($"credential refresh failed environment={environment} reason={ex.GetType().Name}");
            return SquareDirectRefreshOutcome.Failed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<HttpResponseMessage?> TrySendDirectAsync(
        DirectRoute route,
        object? body,
        CancellationToken cancellationToken)
    {
        if (!await _store.IsLocalEnabledAsync(cancellationToken))
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() < _cooldownUntil)
        {
            return null;
        }

        var credential = await _store.LoadCredentialAsync(route.Environment, cancellationToken);
        if (credential is null)
        {
            // 没有有效凭据（从未领取 / 已过期 / 服务端开关没开）：本笔走中转，凭据由预热或设置页负责领取。
            return null;
        }

        using var request = BuildSquareRequest(route, body, credential);
        var timeout = route.Kind == RouteKind.Create ? CreateTimeout : QueryTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await _squareClient.SendAsync(request, linkedCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return await AcceptSuccessAsync(route, responseBody, response.StatusCode, cancellationToken);
            }

            return await HandleNonSuccessAsync(route, response.StatusCode, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // 断网或直连超时（调用方没取消）：请求可能已到达 Square，但结果不明——不在这里下结论，
            // 回落中转：创建用同一幂等键（返回原结账），轮询 / 取消本身幂等。
            StartCooldown(NetworkFailureCooldown);
            Log($"direct {route.Describe()} failed reason={(ex is HttpRequestException ? "network-error" : "timeout")}; fallback to relay");
            return null;
        }
    }

    private async Task<HttpResponseMessage?> AcceptSuccessAsync(
        DirectRoute route,
        string responseBody,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        string? checkoutId = null;
        var resourceProperty = route.Kind == RouteKind.Payment ? "payment" : "checkout";
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty(resourceProperty, out var checkout) &&
                checkout.ValueKind == JsonValueKind.Object &&
                checkout.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                checkoutId = idElement.GetString();
            }
        }
        catch (JsonException)
        {
            checkoutId = null;
        }

        if (string.IsNullOrWhiteSpace(checkoutId))
        {
            // 2xx 却读不出结账：不当作成功，回落中转（创建则用同幂等键拿回原结账，不会再建一笔）。
            Log($"direct {route.Describe()} returned unreadable body http={(int)statusCode}; fallback to relay");
            return null;
        }

        if (route.Kind == RouteKind.Create)
        {
            // 先落盘再返回：登记归属失败 / 进程崩溃后，仍能在下次预热或走中转前补登，恢复中心才查得到这笔结账。
            await _store.AddPendingOriginAsync(
                new SquareDirectPendingOrigin(route.Environment, checkoutId, _timeProvider.GetUtcNow()),
                CancellationToken.None);
            _mayHavePendingOrigins = true;
            Log($"direct checkout created checkoutId={checkoutId} http={(int)statusCode}; registering origin");
            _inFlightOrigins[checkoutId] = Task.Run(() => RegisterOriginWithRetryAsync(route.Environment, checkoutId));
        }

        // 调用方 using 释放响应：返回新的响应对象，不把已读完的原响应交出去。
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }

    private async Task<HttpResponseMessage?> HandleNonSuccessAsync(
        DirectRoute route,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        var code = (int)statusCode;
        if (route.Kind == RouteKind.Payment && statusCode == HttpStatusCode.Forbidden)
        {
            // 付款详情 403 多半是令牌缺 PAYMENTS_READ 之类的权限；结账接口用同一令牌是通的，不能因此删掉凭据让后续付款整体回落中转。
            Log($"direct {route.Describe()} forbidden http={code}; fallback to relay");
        }
        else if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 令牌被 Square 拒绝（总部换了令牌 / 被吊销）：立即删掉本机副本，等预热重新领取。
            await _store.DeleteCredentialAsync(route.Environment, CancellationToken.None);
            StartCooldown(AuthFailureCooldown);
            Log($"direct {route.Describe()} rejected http={code}; credential removed, fallback to relay");
        }
        else if (code >= 500 || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            StartCooldown(NetworkFailureCooldown);
            Log($"direct {route.Describe()} transient failure http={code}; fallback to relay");
        }
        else
        {
            // 其他 4xx（参数错误、结账不存在等）：交给中转返回统一格式的错误，行为与未启用直连时完全一致。
            Log($"direct {route.Describe()} failed http={code}; fallback to relay");
        }

        return null;
    }

    private HttpRequestMessage BuildSquareRequest(DirectRoute route, object? body, SquareDirectCredential credential)
    {
        var baseUrl = CardTerminalSettings.GetSquareApiBaseUrl(route.Environment);
        var request = route.Kind switch
        {
            RouteKind.Create => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}terminals/checkouts")
            {
                Content = JsonBody(BuildCreatePayload(route.Environment, (SquareCreateCheckoutRequest)body!))
            },
            RouteKind.Get => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}terminals/checkouts/{Uri.EscapeDataString(route.ResourceId!)}"),
            RouteKind.Payment => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}payments/{Uri.EscapeDataString(route.ResourceId!)}"),
            RouteKind.Cancel => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}terminals/checkouts/{Uri.EscapeDataString(route.ResourceId!)}/cancel")
            {
                Content = JsonBody(new { })
            },
            _ => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}terminals/checkouts/{Uri.EscapeDataString(route.ResourceId!)}/dismiss")
            {
                Content = JsonBody(new { })
            }
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("Square-Version", CardTerminalSettings.SquareVersion);
        return request;
    }

    /// <summary>
    /// 创建结账的请求体，字段与 POS API（SquareTerminalRestClient.CreateCheckoutAsync）逐项一致，含 null 字段与 Sandbox 设备号改写。
    /// 这是「回落中转后同一幂等键仍命中同一笔结账」的前提：Square 对同键不同体会报错而不是复用，所以不能有任何差异。
    /// </summary>
    internal static object BuildCreatePayload(CardTerminalEnvironment environment, SquareCreateCheckoutRequest request)
    {
        var deviceId = environment == CardTerminalEnvironment.Sandbox
            ? SquareSandboxTerminalDeviceIds.ResolveCheckoutDeviceId(request.DeviceId)
            : request.DeviceId;
        return new
        {
            idempotency_key = request.IdempotencyKey,
            checkout = new
            {
                amount_money = new
                {
                    amount = request.AmountMoney.Amount,
                    currency = request.AmountMoney.Currency
                },
                device_options = new
                {
                    device_id = deviceId
                },
                location_id = request.LocationId,
                reference_id = request.ReferenceId,
                note = request.Note,
                order_id = request.OrderId
            }
        };
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private async Task RegisterOriginWithRetryAsync(CardTerminalEnvironment environment, string checkoutId)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxOriginAttempts; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(OriginAttemptTimeout, _timeProvider);
                    await _api.RegisterOriginAsync(environment, checkoutId, cts.Token);
                    await _store.RemovePendingOriginAsync(environment, checkoutId, CancellationToken.None);
                    Log($"origin registered checkoutId={checkoutId} attempt={attempt}");
                    return;
                }
                catch (SquareDirectApiException ex) when (ex.IsDisabled)
                {
                    // 付款中途总部关掉了开关：删凭据并停止补登（服务端不会再接受）。
                    await _store.DeleteCredentialAsync(environment, CancellationToken.None);
                    await _store.SetServerStateAsync(environment, SquareDirectServerState.Disabled, CancellationToken.None);
                    Log($"origin register stopped checkoutId={checkoutId}: store switch is off");
                    return;
                }
                catch (SquareDirectApiException ex) when ((int)ex.StatusCode is >= 400 and < 500 &&
                    ex.StatusCode != HttpStatusCode.RequestTimeout &&
                    ex.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    // 服务端明确拒绝（归属不匹配 / 已被别的设备绑定等）：重试没有意义，丢弃待补登记录。
                    await _store.RemovePendingOriginAsync(environment, checkoutId, CancellationToken.None);
                    Log($"origin register rejected checkoutId={checkoutId} http={(int)ex.StatusCode} errorCode={ex.ErrorCode ?? "<none>"}");
                    return;
                }
                catch (Exception ex) when (ex is SquareDirectApiException or HttpRequestException or OperationCanceledException)
                {
                    Log($"origin register attempt failed checkoutId={checkoutId} attempt={attempt}/{MaxOriginAttempts} reason={ex.GetType().Name}");
                    if (attempt < MaxOriginAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), _timeProvider);
                    }
                }
            }

            Log($"origin register gave up checkoutId={checkoutId}; kept in pending list for the next warm-up");
        }
        finally
        {
            _inFlightOrigins.TryRemove(checkoutId, out _);
        }
    }

    /// <summary>走中转前调用：等在途登记完成（有上限），再补登磁盘上遗留的记录（限频）。</summary>
    private async Task EnsureOriginsRegisteredBeforeRelayAsync(CancellationToken cancellationToken)
    {
        var inFlight = _inFlightOrigins.Values.ToArray();
        if (inFlight.Length > 0)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(OriginWaitLimit);
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log("waiting for origin registration timed out; continue with relay");
            }
        }

        if (_mayHavePendingOrigins && _inFlightOrigins.IsEmpty)
        {
            await FlushPendingOriginsAsync(cancellationToken);
        }
    }

    private async Task FlushPendingOriginsAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastPendingFlushAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastPendingFlushAt = now;
        var pending = await _store.ListPendingOriginsAsync(cancellationToken);
        if (pending.Count == 0)
        {
            _mayHavePendingOrigins = false;
            return;
        }

        foreach (var item in pending)
        {
            if (_inFlightOrigins.ContainsKey(item.CheckoutId))
            {
                continue;
            }

            // 补登同样有界：单次 10 秒、不重试（失败的留给下一次预热），不会拖住收银。
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(OriginAttemptTimeout);
                await _api.RegisterOriginAsync(item.Environment, item.CheckoutId, cts.Token);
                await _store.RemovePendingOriginAsync(item.Environment, item.CheckoutId, CancellationToken.None);
                Log($"pending origin registered checkoutId={item.CheckoutId}");
            }
            catch (SquareDirectApiException ex) when ((int)ex.StatusCode is >= 400 and < 500 &&
                ex.StatusCode != HttpStatusCode.RequestTimeout &&
                ex.StatusCode != HttpStatusCode.TooManyRequests)
            {
                await _store.RemovePendingOriginAsync(item.Environment, item.CheckoutId, CancellationToken.None);
                Log($"pending origin dropped checkoutId={item.CheckoutId} http={(int)ex.StatusCode} errorCode={ex.ErrorCode ?? "<none>"}");
            }
            catch (Exception ex) when (ex is SquareDirectApiException or HttpRequestException ||
                (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Log($"pending origin retry failed checkoutId={item.CheckoutId} reason={ex.GetType().Name}");
            }
        }
    }

    private void StartCooldown(TimeSpan duration)
    {
        var until = _timeProvider.GetUtcNow() + duration;
        if (until > _cooldownUntil)
        {
            _cooldownUntil = until;
        }
    }

    private static bool TryParseRoute(HttpMethod method, string relativeUrl, object? body, out DirectRoute route)
    {
        route = default;
        var queryIndex = relativeUrl.IndexOf('?', StringComparison.Ordinal);
        var path = (queryIndex < 0 ? relativeUrl : relativeUrl[..queryIndex]).TrimStart('/');
        var query = queryIndex < 0 ? string.Empty : relativeUrl[(queryIndex + 1)..];

        if (method == HttpMethod.Post && string.Equals(path, "api/v1/square/checkouts", StringComparison.Ordinal))
        {
            return body is SquareCreateCheckoutRequest create &&
                TryParseEnvironment(create.Environment, out var createEnvironment) &&
                SetRoute(RouteKind.Create, createEnvironment, null, out route);
        }

        var paymentMatch = PaymentRoutePattern().Match(path);
        if (paymentMatch.Success)
        {
            var paymentId = Uri.UnescapeDataString(paymentMatch.Groups["id"].Value);
            return method == HttpMethod.Get &&
                !string.IsNullOrWhiteSpace(paymentId) &&
                TryParseEnvironment(ReadQueryValue(query, "environment"), out var paymentEnvironment) &&
                SetRoute(RouteKind.Payment, paymentEnvironment, paymentId, out route);
        }

        var match = CheckoutRoutePattern().Match(path);
        if (!match.Success)
        {
            return false;
        }

        var checkoutId = Uri.UnescapeDataString(match.Groups["id"].Value);
        if (string.IsNullOrWhiteSpace(checkoutId))
        {
            return false;
        }

        var action = match.Groups["action"].Value;
        if (method == HttpMethod.Get && action.Length == 0)
        {
            return TryParseEnvironment(ReadQueryValue(query, "environment"), out var getEnvironment) &&
                SetRoute(RouteKind.Get, getEnvironment, checkoutId, out route);
        }

        if (method == HttpMethod.Post && body is SquareCheckoutActionRequest actionRequest &&
            TryParseEnvironment(actionRequest.Environment, out var actionEnvironment))
        {
            return action switch
            {
                "cancel" => SetRoute(RouteKind.Cancel, actionEnvironment, checkoutId, out route),
                "dismiss" => SetRoute(RouteKind.Dismiss, actionEnvironment, checkoutId, out route),
                _ => false
            };
        }

        return false;

        bool SetRoute(RouteKind kind, CardTerminalEnvironment environment, string? id, out DirectRoute result)
        {
            result = new DirectRoute(kind, environment, id);
            return true;
        }
    }

    private static bool TryParseEnvironment(string? value, out CardTerminalEnvironment environment)
    {
        return Enum.TryParse(value, ignoreCase: true, out environment) && Enum.IsDefined(environment);
    }

    private static string? ReadQueryValue(string query, string name)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? part : part[..separator];
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
            }
        }

        return null;
    }

    // 日志只写动作 / 状态码 / 结账号，绝不出现令牌或请求头。
    private static void Log(string message) => ConsoleLog.Write("Square", $"direct {message}");

    private enum RouteKind
    {
        Create,
        Get,
        Payment,
        Cancel,
        Dismiss
    }

    private readonly record struct DirectRoute(RouteKind Kind, CardTerminalEnvironment Environment, string? ResourceId)
    {
        public string Describe() => ResourceId is null
            ? Kind.ToString()
            : Kind == RouteKind.Payment ? $"{Kind} paymentId={ResourceId}" : $"{Kind} checkoutId={ResourceId}";
    }
}
