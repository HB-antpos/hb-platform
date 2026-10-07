using System.Net;
using System.Net.Http.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Health;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设备注册、收银员会话续期、连接探测的中心日志：失败必须可检索、敏感字段不入日志、高频探测只按状态切换记录。
/// </summary>
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class DeviceSessionConnectivityLoggingTests
{
    private const string ActivationCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public async Task Device_api_logs_rejection_as_warning_without_activation_code_and_success_as_information()
    {
        // errorCode 用唯一值，避免与并行运行的其它设备 API 测试产生的日志混淆。
        var errorCode = $"TEST_REJECT_{Guid.NewGuid():N}";
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        DeviceApiClient.ResetLogStateForTests();
        try
        {
            var client = CreateDeviceClient(request => request.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResult<DeviceRegisterResponse>.Ok(
                        new DeviceRegisterResponse("POS-001", "1042", "Test Store", 1, true, AuthorizationCode: "auth-secret-marker")))
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(ApiResult<DeviceActivationCodeRedeemResponse>.Fail(errorCode, "rejected"))
                });

            await Assert.ThrowsAsync<CatalogApiException>(() => client.RedeemActivationCodeAsync(
                new DeviceActivationCodeRedeemRequest(ActivationCode, "HW-001", "POS-TILL-01", DeviceSystems.Windows)));
            await client.RegisterAsync(new DeviceRegisterRequest("1042", "HW-001", "POS-TILL-01"));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var warning = Assert.Single(sink.Entries, entry =>
            entry.Level == "Warning" &&
            entry.Properties is not null &&
            Equals(entry.Properties.GetValueOrDefault("errorCode"), errorCode));
        Assert.Equal("Device", warning.Category);
        Assert.Equal("api/v1/devices/activation-code/redeem", warning.RequestPath);
        Assert.Equal("POST", warning.RequestMethod);
        Assert.Equal(400, warning.StatusCode);
        Assert.Equal(nameof(CatalogApiException), warning.ExceptionType);
        Assert.Contains("elapsedMs", warning.Properties!.Keys);

        Assert.Contains(sink.Entries, entry =>
            entry.Level == "Information" &&
            entry.Category == "Device" &&
            entry.RequestPath == "api/v1/devices/register" &&
            entry.StatusCode == 200);
        Assert.DoesNotContain(sink.Entries, entry => entry.Message.Contains(ActivationCode, StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Entries, entry => entry.Message.Contains("auth-secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cashier_session_refresh_logs_rejection_with_status_and_unavailable_only_on_transition()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var session = CreateSession();
            var context = new CashierSessionContext();
            context.SetCurrent(session);
            var unavailable = CashierSessionRefreshAttempt.ApiUnavailable() with { StatusCode = 503, Reason = "http-status" };
            var rejected = CashierSessionRefreshAttempt.OnlineRejected() with
            {
                StatusCode = 401,
                ErrorCode = "CASHIER_SESSION_EXPIRED",
                Reason = "http-status"
            };
            var service = new CashierSessionRefreshService(
                new QueueRefreshApiClient(unavailable, unavailable, unavailable, rejected),
                context,
                new NoopCacheUpdater());

            for (var i = 0; i < 4; i++)
            {
                await service.RefreshOnceAsync();
            }
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var sessionEntries = sink.Entries.Where(entry => entry.Category == "CashierSession").ToArray();
        // 连续 3 次不可用只记 1 条 Warning；被拒时先记恢复（Information）再记踢下线（Warning，带状态码与 errorCode）。
        Assert.Single(sessionEntries, entry => entry.Level == "Warning" && entry.Message.Contains("refresh unavailable", StringComparison.Ordinal));
        var recovered = Assert.Single(sessionEntries, entry => entry.Level == "Information");
        Assert.Contains("failedAttempts=3", recovered.Message, StringComparison.Ordinal);
        var rejection = Assert.Single(sessionEntries, entry => entry.Message.Contains("session rejected", StringComparison.Ordinal));
        Assert.Equal("Warning", rejection.Level);
        Assert.Equal(401, rejection.StatusCode);
        Assert.Equal("CASHIER-001", rejection.UserId);
        Assert.Equal("CASHIER_SESSION_EXPIRED", rejection.Properties!["errorCode"]);
        Assert.Contains("cashierId=CASHIER-001", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionEntries, entry => entry.Message.Contains("ticket-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connectivity_logs_single_warning_for_consecutive_failures_and_single_information_on_recovery()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        ConnectivityLogState.ResetForTests();
        try
        {
            var failuresRemaining = 5;
            HttpResponseMessage Respond(HttpRequestMessage _)
            {
                if (failuresRemaining-- > 0)
                {
                    throw new HttpRequestException("network down");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResult<HealthCheckResponse>.Ok(
                        new HealthCheckResponse(true, DateTimeOffset.UtcNow, "ok")))
                };
            }

            // 主界面与考勤面板各持有一个 transient 实例：状态必须跨实例共享。
            var first = new ConnectivityApiClient(CreateHttpClient(Respond));
            var second = new ConnectivityApiClient(CreateHttpClient(Respond));
            for (var i = 0; i < 5; i++)
            {
                Assert.False(await (i % 2 == 0 ? first : second).CheckOnlineAsync());
            }

            Assert.True(await first.CheckOnlineAsync());
            Assert.True(await second.CheckOnlineAsync());
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            ConnectivityLogState.ResetForTests();
        }

        var connectivityEntries = sink.Entries.Where(entry => entry.Category == "Connectivity").ToArray();
        var warning = Assert.Single(connectivityEntries, entry => entry.Level == "Warning");
        Assert.Equal(nameof(HttpRequestException), warning.ExceptionType);
        Assert.Equal("api/v1/health", warning.RequestPath);
        var recovered = Assert.Single(connectivityEntries, entry => entry.Level == "Information");
        Assert.Contains("failedChecks=5", recovered.Message, StringComparison.Ordinal);
        Assert.Contains("offlineSeconds", recovered.Properties!.Keys);
        Assert.Equal(2, connectivityEntries.Length);
    }

    private static DeviceApiClient CreateDeviceClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(CreateHttpClient(responder));

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://pos.example.test/")
        };

    private static CashierSessionDto CreateSession() =>
        new(
            "CASHIER-001",
            "USER-GUID-001",
            "Test Cashier",
            "1042",
            "POS-001",
            ["Cashier"],
            [],
            ["1042"],
            false,
            false,
            false,
            AuthorizationToken: "ticket-secret");

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private sealed class QueueRefreshApiClient(params CashierSessionRefreshAttempt[] attempts)
        : ICashierSessionRefreshApiClient
    {
        private int _index;

        public Task<CashierSessionRefreshAttempt> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(attempts[Math.Min(_index++, attempts.Length - 1)]);
    }

    private sealed class NoopCacheUpdater : ICashierSessionCacheUpdater
    {
        public Task UpdateCachedSessionAsync(CashierSessionDto session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveCachedSessionAsync(CashierSessionDto session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
