using System.Text.Json;
using Hbpos.Api.Controllers;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Hbpos.Contracts.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

// M28：Linkly 回调鉴权失败只写 Console / Information，中心日志（只收 Warning+）收不到；
// 匿名端点鉴权前就逐条写日志，会被公网扫描刷满不轮转的日志；500 只记异常类型名。
[Collection("LinklyLogThrottle")]
public sealed class LinklyCallbackObservabilityTests
{
    public LinklyCallbackObservabilityTests() => LogThrottle.Shared.ResetForTests();

    [Fact]
    public async Task Unauthorized_callback_raises_one_throttled_warning_with_event_name_instead_of_information_lines()
    {
        var logger = new CapturingLogger<LinklyController>();
        var controller = CreateController(new LinklyCloudCredentialFailureControllerTests.ThrowingBackendService(new LinklyCloudBackendNotificationUnauthorizedException()), logger, "Bearer wrong");
        using var payload = JsonDocument.Parse("{}");

        for (var index = 0; index < 5; index++)
        {
            var result = await controller.ReceiveCloudBackendNotification(
                "Sandbox", $"session-{index}", "display", payload.RootElement, CancellationToken.None);
            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        // 5 次失败只出一条 Warning（其余被限频），且不再有 Information 级的逐条响应日志。
        var warning = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("linkly-callback-unauthorized:Sandbox", warning.EventId.Name);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_request_log_before_authentication_is_debug_level_only()
    {
        var logger = new CapturingLogger<LinklyController>();
        var controller = CreateController(new LinklyCloudCredentialFailureControllerTests.ThrowingBackendService(new InvalidOperationException("unused"), acceptNotifications: true), logger, "Bearer ok");
        using var payload = JsonDocument.Parse("""{"Response":{"ResponseCode":"00"}}""");

        await controller.ReceiveCloudBackendNotification(
            "Sandbox", "session-1", "transaction", payload.RootElement, CancellationToken.None);

        var requestLogs = logger.Entries.Where(entry => entry.Message.Contains("\"phase\":\"request\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(requestLogs);
        Assert.Equal(LogLevel.Debug, requestLogs[0].Level);
        // 鉴权通过后的响应日志仍是 Information，保留既有排障信息。
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("\"phase\":\"response\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Notification_endpoint_is_protected_by_the_unauthorized_only_rate_limit_policy()
    {
        var attribute = Assert.Single(
            typeof(LinklyController)
                .GetMethod(nameof(LinklyController.ReceiveCloudBackendNotification))!
                .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
                .Cast<EnableRateLimitingAttribute>());
        Assert.Equal(LinklyNotificationRateLimitPolicy.PolicyName, attribute.PolicyName);
    }

    [Fact]
    public void Rate_limit_only_applies_to_requests_without_the_configured_bearer()
    {
        var policy = new LinklyNotificationRateLimitPolicy(Options.Create(new LinklyCloudBackendAsyncOptions
        {
            ProductionNotificationBearer = "production-notify",
            SandboxNotificationBearer = "sandbox-notify"
        }));

        // 正确 bearer：不限流（真实 Linkly 回调密集且来自少数出口 IP，限流会丢按键提示和回单）。
        foreach (var header in new[] { "Bearer production-notify", "Bearer sandbox-notify", "  Bearer sandbox-notify  " })
        {
            Assert.Equal("authorized", policy.GetPartition(CreateContext(header, "203.0.113.9")).PartitionKey);
        }

        // 错误或缺失：按来源 IP 限流，窗口内超过上限被拒绝。
        foreach (var header in new[] { "Bearer wrong", "bearer sandbox-notify", "", null })
        {
            var partition = policy.GetPartition(CreateContext(header, "203.0.113.9"));
            Assert.Equal("203.0.113.9", partition.PartitionKey);
        }

        var limited = policy.GetPartition(CreateContext("Bearer wrong", "203.0.113.9"));
        using var limiter = limited.Factory(limited.PartitionKey);
        for (var index = 0; index < LinklyNotificationRateLimitPolicy.UnauthorizedPermitLimit; index++)
        {
            Assert.True(limiter.AttemptAcquire().IsAcquired);
        }

        Assert.False(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void Rate_limit_is_open_for_valid_bearer_when_no_bearer_is_configured_for_either_environment()
    {
        var policy = new LinklyNotificationRateLimitPolicy(Options.Create(new LinklyCloudBackendAsyncOptions()));

        // 没配 bearer 时任何请求都视为无效鉴权，必须受限流保护（不能把空串当成“匹配”）。
        Assert.Equal("203.0.113.9", policy.GetPartition(CreateContext("Bearer ", "203.0.113.9")).PartitionKey);
        Assert.Equal("203.0.113.9", policy.GetPartition(CreateContext("Bearer", "203.0.113.9")).PartitionKey);
    }

    [Fact]
    public void Log_throttle_allows_one_event_per_window_and_reports_suppressed_count()
    {
        var clock = new ManualTimeProvider();
        var throttle = new LogThrottle(clock);
        var window = TimeSpan.FromMinutes(1);

        Assert.True(throttle.TryAcquire("k", window, out var first));
        Assert.Equal(0, first);
        Assert.False(throttle.TryAcquire("k", window, out _));
        Assert.False(throttle.TryAcquire("k", window, out _));
        Assert.True(throttle.TryAcquire("other", window, out _));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(throttle.TryAcquire("k", window, out var afterWindow));
        Assert.Equal(2, afterWindow);
    }

    [Fact]
    public async Task Unexpected_transaction_failure_logs_the_exception_not_just_its_type_name()
    {
        var logger = new CapturingLogger<LinklyController>();
        var controller = CreateController(new LinklyCloudCredentialFailureControllerTests.ThrowingBackendService(new InvalidOperationException("pending write failed")), logger, null);

        var result = await controller.StartCloudBackendTransaction(
            new Hbpos.Contracts.Linkly.LinklyCloudBackendTransactionRequest("Sandbox", "P", 100, null),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Contains("transaction start failed", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Misconfigured_transaction_request_is_logged_as_warning_instead_of_silent_400()
    {
        var logger = new CapturingLogger<LinklyController>();
        var controller = CreateController(
            new LinklyCloudCredentialFailureControllerTests.ThrowingBackendService(new LinklyCloudBackendValidationException("Linkly Cloud notification bearer is not configured.")),
            logger,
            null);

        var result = await controller.StartCloudBackendTransaction(
            new Hbpos.Contracts.Linkly.LinklyCloudBackendTransactionRequest("Sandbox", "P", 100, null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Warning);
        Assert.IsType<LinklyCloudBackendValidationException>(entry.Exception);
    }

    private static LinklyController CreateController(
        ILinklyCloudBackendAsyncService backendService,
        ILogger<LinklyController> logger,
        string? authorization)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILinklyCloudTerminalService>(new FixedLinklyCloudTerminalModeService("Active"))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        if (authorization is not null)
        {
            httpContext.Request.Headers.Authorization = authorization;
        }

        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
            new System.Security.Claims.Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
        ], "Test"));
        return new LinklyController(
            new NoOpLinklyCloudCredentialService(),
            backendService,
            new NoOpLinklyCloudPairingService(),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static DefaultHttpContext CreateContext(string? authorization, string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}

[CollectionDefinition("LinklyLogThrottle", DisableParallelization = true)]
public sealed class LinklyLogThrottleCollection;
