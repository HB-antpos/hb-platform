using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Hbpos.Client.Tests;

public sealed class MainShellStartupServiceTests
{
    private static readonly PosSessionState StartupSession = new(
        "HB POS",
        "DEFAULT",
        "Default Store",
        "Terminal 04",
        "C001",
        "Alice",
        false,
        0);

    private const string NginxBadGatewayHtml =
        "<html>\r\n<head><title>502 Bad Gateway</title></head>\r\n<body>\r\n<center><h1>502 Bad Gateway</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>\r\n";
    private const string CaptivePortalHtml =
        "<!DOCTYPE html><html><head><title>Wi-Fi Login</title></head><body>Please sign in</body></html>";

    [Fact]
    public async Task EvaluateAsync_WithAuthorizedCachedDevice_AllowsOfflineStartupWithoutApi()
    {
        var authorizationState = new DeviceAuthorizationState();
        var repository = new FakeLocalDeviceRepository
        {
            Latest = CreateAllowedDevice("1042")
        };
        var service = new MainShellStartupService(
            repository,
            new FakeDeviceFingerprintService("HW-001"),
            authorizationState);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("1042", result.Session.StoreCode);
        Assert.Equal("POS-001", result.Session.DeviceCode);
        Assert.Equal(1, repository.GetLatestCallCount);
        Assert.NotNull(authorizationState.Current);
        Assert.Equal("AUTH-001", authorizationState.Current.AuthorizationCode);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyReturnsUnregistered_RequiresRegistrationAndClearsAuthorization()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-OLD", "1001", "HW-001", "AUTH-OLD"));
        var verification = new DeviceVerifyResponse(
            "POS-001",
            "1042",
            "Main Store",
            3,
            false,
            "Device is not registered.");
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(verification)
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.Equal("POS-001", apiClient.LastVerifyRequest?.DeviceCode);
        Assert.Equal("1042", apiClient.LastVerifyRequest?.StoreCode);
        Assert.Equal("HW-001", apiClient.LastVerifyRequest?.HardwareId);
        Assert.Same(verification, repository.SavedVerifyResponse);
        Assert.Equal("HW-001", repository.SavedVerifyHardwareId);
        Assert.Equal(3, repository.SavedVerifyResponse?.DeviceStatus);
        Assert.False(repository.SavedVerifyResponse?.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyReturnsEnabled_UsesServerAuthorizationAndStoreName()
    {
        var authorizationState = new DeviceAuthorizationState();
        var verification = new DeviceVerifyResponse(
            "POS-001",
            "1042",
            "Verified Store",
            1,
            true,
            "Device is enabled.",
            "AUTH-SERVER");
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(verification)
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("Verified Store", result.Session.StoreName);
        Assert.Equal("AUTH-SERVER", authorizationState.Current?.AuthorizationCode);
        Assert.Same(verification, repository.SavedVerifyResponse);
        Assert.Equal("HW-001", repository.SavedVerifyHardwareId);
        Assert.Equal("AUTH-SERVER", repository.SavedVerifyResponse?.AuthorizationCode);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyTransportFails_AllowsOfflineStartupFromCache()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-OLD", "1001", "HW-001", "AUTH-OLD"));
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new HttpRequestException("Device API is temporarily unavailable."))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("Main Store", result.Session.StoreName);
        Assert.Equal("AUTH-001", authorizationState.Current?.AuthorizationCode);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Fact]
    public async Task EvaluateAfterServerSwitchAsync_WhenRemoteVerifyFails_RequiresRegistrationWithoutOfflineFallback()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-OLD", "1001", "HW-001", "AUTH-OLD"));
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new HttpRequestException("Target server verify unavailable."))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAfterServerSwitchAsync(StartupSession, CancellationToken.None);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Same(repository.Latest, result.CachedDevice);
        Assert.Null(authorizationState.Current);
    }

    [Fact]
    public async Task EvaluateAsync_WhenDeniedResponseSaveFails_ClearsAuthorizationAndPropagatesFailure()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-001", "1042", "HW-001", "AUTH-OLD"));
        var saveFailure = new InvalidOperationException("Device cache write failed.");
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(new DeviceVerifyResponse(
                "POS-001",
                "1042",
                "Main Store",
                3,
                false,
                "Device is not registered."))
        };
        var repository = new FakeLocalDeviceRepository
        {
            Latest = CreateAllowedDevice("1042"),
            VerifySaveException = saveFailure
        };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EvaluateAsync(StartupSession, previewMode: false));

        Assert.Same(saveFailure, exception);
        Assert.Null(authorizationState.Current);
    }

    [Fact]
    public async Task EvaluateAsync_WhenEnabledResponseSaveFails_ClearsAuthorizationAndPropagatesFailure()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-001", "1042", "HW-001", "AUTH-OLD"));
        var saveFailure = new InvalidOperationException("Device cache write failed.");
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(new DeviceVerifyResponse(
                "POS-001",
                "1042",
                "Verified Store",
                1,
                true,
                "Device is enabled.",
                "AUTH-SERVER"))
        };
        var repository = new FakeLocalDeviceRepository
        {
            Latest = CreateAllowedDevice("1042"),
            VerifySaveException = saveFailure
        };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EvaluateAsync(StartupSession, previewMode: false));

        Assert.Same(saveFailure, exception);
        Assert.Null(authorizationState.Current);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task EvaluateAsync_WhenRemoteVerifyReturnsAuthorizationFailure_PersistsDeniedStateAndRequiresRegistration(
        HttpStatusCode statusCode)
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-001", "1042", "HW-001", "AUTH-001"));
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new CatalogApiException("Device authorization was rejected.", statusCode, "DEVICE_AUTH_REQUIRED"))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.Equal(3, repository.SavedVerifyResponse?.DeviceStatus);
        Assert.False(repository.SavedVerifyResponse?.IsAllowed);
        Assert.Null(repository.SavedVerifyResponse?.AuthorizationCode);
        Assert.Equal("Device authorization was rejected.", repository.SavedVerifyResponse?.Message);
        Assert.Equal("HW-001", repository.SavedVerifyHardwareId);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyReturnsServerError_AllowsOfflineStartupWithoutSaving()
    {
        var authorizationState = new DeviceAuthorizationState();
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new CatalogApiException("Temporary server failure.", HttpStatusCode.InternalServerError))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("AUTH-001", authorizationState.Current?.AuthorizationCode);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyResponseIsInvalidJson_AllowsOfflineStartupWithoutSaving()
    {
        // 非 JSON 正文说明响应不是来自 POS API（网关错误页、Wi-Fi 认证页），不能当作服务端拒绝。
        var authorizationState = new DeviceAuthorizationState();
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new JsonException("Device API returned invalid JSON."))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("AUTH-001", authorizationState.Current?.AuthorizationCode);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "text/html", NginxBadGatewayHtml)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "text/html", NginxBadGatewayHtml)]
    [InlineData(HttpStatusCode.GatewayTimeout, "text/html", NginxBadGatewayHtml)]
    [InlineData(HttpStatusCode.OK, "text/html", CaptivePortalHtml)]
    [InlineData(HttpStatusCode.BadGateway, "application/json", "{\"error\":\"upstream unavailable\"}")]
    [InlineData(HttpStatusCode.NotFound, "text/html", "<html><body>404 Not Found</body></html>")]
    [InlineData(HttpStatusCode.NotFound, "application/json", "")]
    [InlineData(HttpStatusCode.OK, "application/json", "")]
    public async Task EvaluateAsync_WhenVerifyHitsGatewayOrNonApiResponse_UsesLocalCacheWithoutPersistingDenial(
        HttpStatusCode statusCode,
        string contentType,
        string body)
    {
        var authorizationState = new DeviceAuthorizationState();
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithHttp(authorizationState, repository, statusCode, contentType, body);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("1042", result.Session.StoreCode);
        Assert.Equal("AUTH-001", authorizationState.Current?.AuthorizationCode);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Fact]
    public async Task EvaluateAsync_WhenVerifyReturnsParsableNotAllowedEnvelope_PersistsServerDenial()
    {
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-001", "1042", "HW-001", "AUTH-001"));
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithHttp(
            authorizationState,
            repository,
            HttpStatusCode.OK,
            "application/json",
            JsonSerializer.Serialize(
                ApiResult<DeviceVerifyResponse>.Ok(new DeviceVerifyResponse(
                    "POS-001",
                    "1042",
                    "Main Store",
                    0,
                    false,
                    "Device is disabled.")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.Equal(0, repository.SavedVerifyResponse?.DeviceStatus);
        Assert.False(repository.SavedVerifyResponse?.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_WhenVerifyReturnsBusinessErrorEnvelope_PersistsDeniedState()
    {
        var authorizationState = new DeviceAuthorizationState();
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithHttp(
            authorizationState,
            repository,
            HttpStatusCode.Unauthorized,
            "application/json",
            "{\"success\":false,\"data\":null,\"errorCode\":\"DEVICE_DISABLED\",\"message\":\"POS device is disabled.\"}");

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.Equal(3, repository.SavedVerifyResponse?.DeviceStatus);
        Assert.False(repository.SavedVerifyResponse?.IsAllowed);
        Assert.Equal("POS device is disabled.", repository.SavedVerifyResponse?.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task EvaluateAsync_WhenApiErrorHasNoBusinessErrorCode_UsesLocalCacheWithoutPersistingDenial(
        HttpStatusCode statusCode)
    {
        // 没有 ApiResult 业务错误码（ProblemDetails、空正文）时无法证明服务端拒绝了这台设备。
        var authorizationState = new DeviceAuthorizationState();
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromException<DeviceVerifyResponse>(
                new CatalogApiException($"Device API request failed with HTTP {(int)statusCode}.", statusCode))
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("AUTH-001", authorizationState.Current?.AuthorizationCode);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Fact]
    public async Task EvaluateAsync_WhenLocallyDeniedDeviceIsStillEnabledOnServer_RecoversAuthorization()
    {
        var authorizationState = new DeviceAuthorizationState();
        var verification = new DeviceVerifyResponse(
            "POS-001",
            "1042",
            "Verified Store",
            1,
            true,
            "Device is enabled.",
            "AUTH-SERVER");
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(verification)
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateLocallyDeniedDevice() };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.False(result.RequiresDeviceRegistration);
        Assert.Equal("POS-001", apiClient.LastVerifyRequest?.DeviceCode);
        Assert.Equal("1042", apiClient.LastVerifyRequest?.StoreCode);
        Assert.Equal("HW-001", apiClient.LastVerifyRequest?.HardwareId);
        Assert.Equal("1042", result.Session.StoreCode);
        Assert.Equal("Verified Store", result.Session.StoreName);
        Assert.Equal("POS-001", result.Session.DeviceCode);
        Assert.Equal("AUTH-SERVER", authorizationState.Current?.AuthorizationCode);
        Assert.Same(verification, repository.SavedVerifyResponse);
        Assert.Equal("HW-001", repository.SavedVerifyHardwareId);
        Assert.True(result.CachedDevice?.IsAllowed);
    }

    [Theory]
    [InlineData(0, false, "AUTH-SERVER", "POS-001", "1042")]
    [InlineData(2, false, null, "POS-001", "1042")]
    [InlineData(-1, false, null, "POS-001", "1042")]
    [InlineData(3, false, null, "POS-001", "1042")]
    [InlineData(1, true, null, "POS-001", "1042")]
    [InlineData(1, true, "AUTH-SERVER", "POS-OTHER", "1042")]
    [InlineData(1, true, "AUTH-SERVER", "POS-001", "1099")]
    public async Task EvaluateAsync_WhenLocallyDeniedDeviceIsNotEnabledOnServer_StaysOnRegistration(
        int deviceStatus,
        bool isAllowed,
        string? authorizationCode,
        string deviceCode,
        string storeCode)
    {
        var authorizationState = new DeviceAuthorizationState();
        var verification = new DeviceVerifyResponse(
            deviceCode,
            storeCode,
            "Main Store",
            deviceStatus,
            isAllowed,
            "Server decision.",
            authorizationCode);
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => Task.FromResult(verification)
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateLocallyDeniedDevice() };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.NotNull(apiClient.LastVerifyRequest);
        // 服务端结论仍是最终依据：按服务端返回落盘，注册页据此展示真实状态。
        Assert.Same(verification, repository.SavedVerifyResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "text/html", NginxBadGatewayHtml)]
    [InlineData(HttpStatusCode.OK, "text/html", CaptivePortalHtml)]
    [InlineData(HttpStatusCode.Unauthorized, "application/json", "{\"success\":false,\"errorCode\":\"DEVICE_DISABLED\",\"message\":\"POS device is disabled.\"}")]
    public async Task EvaluateAsync_WhenLocallyDeniedDeviceCannotBeConfirmed_StaysOnRegistrationWithoutRewritingCache(
        HttpStatusCode statusCode,
        string contentType,
        string body)
    {
        var authorizationState = new DeviceAuthorizationState();
        var cachedDevice = CreateLocallyDeniedDevice();
        var repository = new FakeLocalDeviceRepository { Latest = cachedDevice };
        var service = CreateServiceWithHttp(authorizationState, repository, statusCode, contentType, body);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Same(cachedDevice, result.CachedDevice);
        Assert.Null(authorizationState.Current);
        Assert.Null(repository.SavedVerifyResponse);
    }

    [Theory]
    [MemberData(nameof(NonRecoverableDeniedDevices))]
    public async Task EvaluateAsync_WithDeniedDeviceOutsideLocalDenialSignature_DoesNotCallVerify(LocalDeviceCache cachedDevice)
    {
        var authorizationState = new DeviceAuthorizationState();
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) => throw new InvalidOperationException("不应联网验证。")
        };
        var repository = new FakeLocalDeviceRepository { Latest = cachedDevice };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Same(cachedDevice, result.CachedDevice);
        Assert.Null(apiClient.LastVerifyRequest);
        Assert.Null(repository.SavedVerifyResponse);
        Assert.Null(authorizationState.Current);
    }

    public static IEnumerable<object[]> NonRecoverableDeniedDevices()
    {
        // 服务端停用（0）、锁定（2）、待审批（-1）的记录来自服务端结论，启动时不自动重试。
        yield return [CreateLocallyDeniedDevice() with { DeviceStatus = 0 }];
        yield return [CreateLocallyDeniedDevice() with { DeviceStatus = 2 }];
        yield return [CreateLocallyDeniedDevice() with { DeviceStatus = -1 }];
        // 硬件指纹不一致或身份不完整时，绝不拿本机去验证别的设备码。
        yield return [CreateLocallyDeniedDevice() with { HardwareId = "HW-OTHER" }];
        yield return [CreateLocallyDeniedDevice() with { DeviceCode = string.Empty }];
        yield return [CreateLocallyDeniedDevice() with { StoreCode = " " }];
    }

    [Fact]
    public async Task EvaluateAsync_WhenDeterministicFailureCancelsCallerDuringVerify_PersistsDeniedStateWithoutCallerToken()
    {
        using var cancellationSource = new CancellationTokenSource();
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-001", "1042", "HW-001", "AUTH-OLD"));
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, _) =>
            {
                cancellationSource.Cancel();
                return Task.FromException<DeviceVerifyResponse>(new CatalogApiException(
                    "Device authorization was rejected.",
                    HttpStatusCode.Unauthorized,
                    "DEVICE_AUTH_REQUIRED"));
            }
        };
        var repository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var service = CreateServiceWithApi(authorizationState, apiClient, repository);

        var result = await service.EvaluateAsync(
            StartupSession,
            previewMode: false,
            cancellationSource.Token);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Null(authorizationState.Current);
        Assert.NotNull(repository.SavedVerifyCancellationToken);
        Assert.NotEqual(cancellationSource.Token, repository.SavedVerifyCancellationToken.Value);
        Assert.False(repository.SavedVerifyCancellationToken.Value.IsCancellationRequested);
        Assert.Equal(3, repository.SavedVerifyResponse?.DeviceStatus);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRemoteVerifyIsCancelled_PropagatesCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var authorizationState = new DeviceAuthorizationState();
        var apiClient = new FakeDeviceApiClient
        {
            VerifyAsyncHandler = (_, cancellationToken) => Task.FromCanceled<DeviceVerifyResponse>(cancellationToken)
        };
        var service = CreateServiceWithApi(authorizationState, apiClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(
            StartupSession,
            previewMode: false,
            cancellationSource.Token));
    }

    [Theory]
    [MemberData(nameof(InvalidCachedDevices))]
    public async Task EvaluateAsync_WithMissingOrInvalidCachedDevice_RequiresRegistration(LocalDeviceCache? cachedDevice)
    {
        var authorizationState = new DeviceAuthorizationState();
        var service = new MainShellStartupService(
            new FakeLocalDeviceRepository { Latest = cachedDevice },
            new FakeDeviceFingerprintService("HW-001"),
            authorizationState);

        var result = await service.EvaluateAsync(StartupSession, previewMode: false);

        Assert.True(result.RequiresDeviceRegistration);
        Assert.Same(cachedDevice, result.CachedDevice);
        Assert.Null(authorizationState.Current);
    }

    public static IEnumerable<object?[]> InvalidCachedDevices()
    {
        yield return [null];
        yield return [CreateAllowedDevice("1042") with { IsAllowed = false }];
        yield return [CreateAllowedDevice("1042") with { AuthorizationCode = null }];
        yield return [CreateAllowedDevice("1042") with { HardwareId = "HW-OTHER" }];
    }

    private static LocalDeviceCache CreateAllowedDevice(string storeCode)
    {
        return new LocalDeviceCache(
            "POS-001",
            storeCode,
            "Main Store",
            "HW-001",
            1,
            true,
            null,
            DateTimeOffset.UtcNow,
            "AUTH-001");
    }

    private static LocalDeviceCache CreateLocallyDeniedDevice()
    {
        // 旧版本把网关 HTML 误判为拒绝后写下的本地记录：状态 3、不允许、授权码已清空、消息是 JSON 解析错误。
        return new LocalDeviceCache(
            "POS-001",
            "1042",
            "Main Store",
            "HW-001",
            3,
            false,
            "'<' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.",
            DateTimeOffset.UtcNow,
            null);
    }

    private static MainShellStartupService CreateServiceWithHttp(
        DeviceAuthorizationState authorizationState,
        FakeLocalDeviceRepository repository,
        HttpStatusCode statusCode,
        string contentType,
        string body)
    {
        // 使用真实 DeviceApiClient，覆盖"HTTP 响应 → 异常分类 → 启动决策"整条链路。
        var httpClient = new HttpClient(new StubHttpMessageHandler(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        }))
        {
            BaseAddress = new Uri("https://pos.example.test/")
        };
        return CreateServiceWithApi(authorizationState, new DeviceApiClient(httpClient), repository);
    }

    private static MainShellStartupService CreateServiceWithApi(
        DeviceAuthorizationState authorizationState,
        IDeviceApiClient apiClient,
        FakeLocalDeviceRepository? repository = null)
    {
        return new MainShellStartupService(
            repository ?? new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
            new FakeDeviceFingerprintService("HW-001"),
            authorizationState,
            apiClient);
    }

    private sealed class FakeLocalDeviceRepository : ILocalDeviceRepository
    {
        public LocalDeviceCache? Latest { get; init; }

        public int GetLatestCallCount { get; private set; }

        public DeviceVerifyResponse? SavedVerifyResponse { get; private set; }

        public string? SavedVerifyHardwareId { get; private set; }

        public CancellationToken? SavedVerifyCancellationToken { get; private set; }

        public Exception? VerifySaveException { get; init; }

        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            GetLatestCallCount++;
            return Task.FromResult(Latest);
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("启动评估不应写入设备缓存。");
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            SavedVerifyCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (VerifySaveException is not null)
            {
                return Task.FromException(VerifySaveException);
            }

            SavedVerifyResponse = response;
            SavedVerifyHardwareId = hardwareId;
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("启动评估不应写入设备缓存。");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder());
    }

    private sealed class FakeDeviceFingerprintService(string hardwareId) : IDeviceFingerprintService
    {
        public string GetHardwareId() => hardwareId;
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public required Func<DeviceVerifyRequest, CancellationToken, Task<DeviceVerifyResponse>> VerifyAsyncHandler { get; init; }

        public DeviceVerifyRequest? LastVerifyRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastVerifyRequest = request;
            return VerifyAsyncHandler(request, cancellationToken);
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
