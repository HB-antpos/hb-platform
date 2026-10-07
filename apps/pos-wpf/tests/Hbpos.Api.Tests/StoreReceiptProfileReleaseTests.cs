using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

/// <summary>
/// 总部「下发」小票资料的收银端接口：同步（按版本号轮询）与回执。
/// 业务规则用假仓储验证；真实 SQL（表不存在降级、MERGE 单调不降、换店覆盖、并发首次回执）
/// 见 StoreReceiptProfileReleaseSqlServerIntegrationTests。
/// </summary>
public sealed class StoreReceiptProfileReleaseTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 7, 1, 2, 3, TimeSpan.Zero);

    private static StoreReceiptProfileDto Snapshot(int version, string? address = "1 Queen St") => new(
        "S001",
        "旗舰店",
        "Hot Bargain",
        address,
        "07 3000 0000",
        "12 345 678 901",
        "30 天无理由退换",
        version,
        PublishedAt);

    // ---------------------------------------------------------------- 同步规则

    [Fact]
    public async Task GetSync_never_published_returns_unchanged_without_reading_snapshot()
    {
        var repository = new FakeRepository { LatestVersion = 0 };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", 0, CancellationToken.None);

        var sync = Assert.IsType<StoreReceiptProfileSyncDto>(result.Sync);
        Assert.False(sync.Changed);
        Assert.Equal(0, sync.Version);
        Assert.Null(sync.Profile);
        Assert.Equal(0, repository.GetLatestCalls);
    }

    [Fact]
    public async Task GetSync_same_version_is_a_cheap_no_op()
    {
        var repository = new FakeRepository { LatestVersion = 3, Latest = Snapshot(3) };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", 3, CancellationToken.None);

        var sync = Assert.IsType<StoreReceiptProfileSyncDto>(result.Sync);
        Assert.False(sync.Changed);
        Assert.Equal(3, sync.Version);
        Assert.Null(sync.Profile);
        // 高频轮询的常态路径：版本没变时绝不读取快照内容。
        Assert.Equal(0, repository.GetLatestCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-5)]
    [InlineData(9)] // 客户端版本号反而更大（异常情形）：以服务端为准
    public async Task GetSync_different_version_returns_latest_snapshot(int knownVersion)
    {
        var repository = new FakeRepository { LatestVersion = 3, Latest = Snapshot(3) };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", knownVersion, CancellationToken.None);

        var sync = Assert.IsType<StoreReceiptProfileSyncDto>(result.Sync);
        Assert.True(sync.Changed);
        Assert.Equal(3, sync.Version);
        Assert.Equal(Snapshot(3), sync.Profile);
    }

    [Fact]
    public async Task GetSync_snapshot_with_control_characters_is_rejected()
    {
        var repository = new FakeRepository { LatestVersion = 2, Latest = Snapshot(2, "1 Queen St\u0007") };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", 0, CancellationToken.None);

        Assert.Null(result.Sync);
        Assert.Equal(StoreReceiptProfileReleaseService.InvalidCharactersCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetSync_snapshot_may_use_crlf_tab_in_address_and_return_policy()
    {
        var snapshot = Snapshot(2, "1 Queen St\r\nBrisbane\tQLD") with { ReturnPolicy = "第一行\n第二行" };
        var repository = new FakeRepository { LatestVersion = 2, Latest = snapshot };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", 0, CancellationToken.None);

        Assert.True(Assert.IsType<StoreReceiptProfileSyncDto>(result.Sync).Changed);
    }

    [Fact]
    public async Task GetSync_snapshot_vanishing_between_queries_is_treated_as_unchanged()
    {
        var repository = new FakeRepository { LatestVersion = 4, Latest = null };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.GetSyncAsync("S001", 1, CancellationToken.None);

        var sync = Assert.IsType<StoreReceiptProfileSyncDto>(result.Sync);
        Assert.False(sync.Changed);
        Assert.Null(sync.Profile);
    }

    [Fact]
    public async Task GetSync_blank_store_code_is_rejected()
    {
        var service = new StoreReceiptProfileReleaseService(new FakeRepository());

        var result = await service.GetSyncAsync("  ", 0, CancellationToken.None);

        Assert.Null(result.Sync);
        Assert.Equal(StoreReceiptProfileReleaseService.StoreCodeRequiredCode, result.ErrorCode);
    }

    // ---------------------------------------------------------------- 回执规则

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task Ack_rejects_version_outside_published_range(int version)
    {
        var repository = new FakeRepository { LatestVersion = 3 };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.AckAsync("S001", "POS-01", "Windows", version, CancellationToken.None);

        Assert.Null(result.Result);
        Assert.Equal(StoreReceiptProfileReleaseService.VersionInvalidCode, result.ErrorCode);
        Assert.Empty(repository.Acks);
    }

    [Fact]
    public async Task Ack_when_snapshot_table_is_missing_is_a_version_error_not_a_crash()
    {
        // 表不存在时仓储返回最新版本 0：任何回执版本都无效，不能抛异常（部署顺序错了也不应 500）。
        var service = new StoreReceiptProfileReleaseService(new FakeRepository { LatestVersion = 0 });

        var result = await service.AckAsync("S001", "POS-01", "Windows", 1, CancellationToken.None);

        Assert.Equal(StoreReceiptProfileReleaseService.VersionInvalidCode, result.ErrorCode);
    }

    [Fact]
    public async Task Ack_records_trimmed_identity_client_kind_and_utc_time_and_returns_applied_version()
    {
        var now = new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.FromHours(10));
        var repository = new FakeRepository { LatestVersion = 3, AppliedVersionToReturn = 3 };
        var service = new StoreReceiptProfileReleaseService(repository, new FixedTimeProvider(now));

        var result = await service.AckAsync(" S001 ", " POS-01 ", "iPadOS", 3, CancellationToken.None);

        Assert.Equal(3, Assert.IsType<StoreReceiptProfileAckResultDto>(result.Result).AppliedVersion);
        var ack = Assert.Single(repository.Acks);
        Assert.Equal(("POS-01", "S001", 3, StoreReceiptProfileClientKinds.Ipad), (ack.DeviceCode, ack.StoreCode, ack.Version, ack.ClientKind));
        // 库里统一存 UTC 墙钟时间。
        Assert.Equal(now.UtcDateTime, ack.NowUtc);
    }

    [Fact]
    public async Task Ack_returns_server_side_applied_version_even_when_it_is_higher_than_requested()
    {
        // 单调不降：设备已回执过 3，晚到的 2 号回执不会把记录降下来，响应里如实返回 3。
        var repository = new FakeRepository { LatestVersion = 3, AppliedVersionToReturn = 3 };
        var service = new StoreReceiptProfileReleaseService(repository);

        var result = await service.AckAsync("S001", "POS-01", "Windows", 2, CancellationToken.None);

        Assert.Equal(3, Assert.IsType<StoreReceiptProfileAckResultDto>(result.Result).AppliedVersion);
    }

    [Fact]
    public async Task Ack_without_device_code_claim_is_rejected()
    {
        var service = new StoreReceiptProfileReleaseService(new FakeRepository { LatestVersion = 3 });

        var result = await service.AckAsync("S001", " ", "Windows", 1, CancellationToken.None);

        Assert.Equal(StoreReceiptProfileReleaseService.DeviceCodeRequiredCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("Windows", "wpf")]
    [InlineData("windows", "wpf")]
    [InlineData(" Windows ", "wpf")]
    [InlineData("iPadOS", "ipad")]
    [InlineData("iOS", "ipad")]
    [InlineData("Android", "handheld")]
    [InlineData("android", "handheld")]
    [InlineData("Linux", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void ClientKind_is_derived_from_the_device_system_claim(string? deviceSystem, string expected)
    {
        Assert.Equal(expected, StoreReceiptProfileClientKinds.FromDeviceSystem(deviceSystem));
    }

    // ---------------------------------------------------------------- 控制器

    [Fact]
    public void New_endpoints_keep_route_templates_and_need_only_device_authentication()
    {
        var sync = typeof(StoresController).GetMethod(nameof(StoresController.SyncCurrentReceiptProfile))!;
        var ack = typeof(StoresController).GetMethod(nameof(StoresController.AckCurrentReceiptProfile))!;

        Assert.Equal(
            "current/receipt-profile/sync",
            Assert.Single(sync.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Equal(
            "current/receipt-profile/ack",
            Assert.Single(ack.GetCustomAttributes<HttpPostAttribute>()).Template);

        // 后台轮询不一定有收银员登录：只沿用类上的设备认证，方法上不得再叠加收银员权限策略，
        // 也不得匿名（匿名会让任何人都能写回执）。真实授权管线见 StoreReceiptProfileSyncAuthorizationHttpTests。
        var classAuthorize = Assert.Single(typeof(StoresController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Null(classAuthorize.Policy);
        foreach (var method in new[] { sync, ack })
        {
            Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public void New_endpoints_never_accept_a_store_or_device_from_the_client()
    {
        var sync = typeof(StoresController).GetMethod(nameof(StoresController.SyncCurrentReceiptProfile))!;
        var ack = typeof(StoresController).GetMethod(nameof(StoresController.AckCurrentReceiptProfile))!;

        Assert.Equal(
            ["knownVersion", "cancellationToken"],
            sync.GetParameters().Select(p => p.Name!).ToArray());
        // 回执体只有版本号：门店与设备只取认证声明。
        Assert.Equal(
            ["Version"],
            typeof(StoreReceiptProfileAckRequest).GetProperties().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("-3", 0)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    public async Task Sync_parses_known_version_tolerantly(string? raw, int expectedKnownVersion)
    {
        var release = new CapturingReleaseService();
        var controller = new StoresController(new NoopProfileService(), release);
        SetClaims(controller, storeCode: "S001");

        var result = await controller.SyncCurrentReceiptProfile(raw, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(("S001", expectedKnownVersion), (release.SyncStoreCode, release.SyncKnownVersion));
    }

    [Fact]
    public async Task Sync_returns_401_when_store_claim_is_missing()
    {
        var controller = new StoresController(new NoopProfileService(), new CapturingReleaseService());
        SetClaims(controller, storeCode: null);

        var result = await controller.SyncCurrentReceiptProfile("1", CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(
            "STORE_CODE_CLAIM_MISSING",
            Assert.IsType<ApiResult<StoreReceiptProfileSyncDto>>(unauthorized.Value).ErrorCode);
    }

    [Fact]
    public async Task Sync_maps_invalid_characters_to_400_with_the_existing_error_code()
    {
        var release = new CapturingReleaseService
        {
            SyncResult = new StoreReceiptProfileSyncLookupResult(
                null,
                StoreReceiptProfileService.InvalidCharactersCode,
                "门店资料包含不可打印控制字符"),
        };
        var controller = new StoresController(new NoopProfileService(), release);
        SetClaims(controller, storeCode: "S001");

        var result = await controller.SyncCurrentReceiptProfile("1", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(
            "STORE_PROFILE_INVALID_CHARACTERS",
            Assert.IsType<ApiResult<StoreReceiptProfileSyncDto>>(bad.Value).ErrorCode);
    }

    [Fact]
    public async Task Ack_passes_identity_from_claims_only()
    {
        var release = new CapturingReleaseService();
        var controller = new StoresController(new NoopProfileService(), release);
        SetClaims(controller, storeCode: "S001", deviceCode: "POS-07", deviceSystem: "Android");

        var result = await controller.AckCurrentReceiptProfile(
            new StoreReceiptProfileAckRequest(2),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, Assert.IsType<ApiResult<StoreReceiptProfileAckResultDto>>(ok.Value).Data!.AppliedVersion);
        Assert.Equal(("S001", "POS-07", "Android", 2), release.LastAck);
    }

    [Fact]
    public async Task Ack_maps_errors_to_401_for_missing_device_and_400_for_bad_version()
    {
        var release = new CapturingReleaseService();
        var controller = new StoresController(new NoopProfileService(), release);
        SetClaims(controller, storeCode: "S001", deviceCode: null);

        release.AckResult = new StoreReceiptProfileAckLookupResult(
            null,
            StoreReceiptProfileReleaseService.DeviceCodeRequiredCode,
            "设备编号认证声明缺失");
        var missingDevice = await controller.AckCurrentReceiptProfile(
            new StoreReceiptProfileAckRequest(1),
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(missingDevice.Result);

        release.AckResult = new StoreReceiptProfileAckLookupResult(
            null,
            StoreReceiptProfileReleaseService.VersionInvalidCode,
            "下发版本号无效");
        var badVersion = await controller.AckCurrentReceiptProfile(
            new StoreReceiptProfileAckRequest(99),
            CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(badVersion.Result);
        Assert.Equal(
            "RECEIPT_PROFILE_VERSION_INVALID",
            Assert.IsType<ApiResult<StoreReceiptProfileAckResultDto>>(bad.Value).ErrorCode);
    }

    [Fact]
    public async Task Ack_returns_401_when_store_claim_is_missing()
    {
        var controller = new StoresController(new NoopProfileService(), new CapturingReleaseService());
        SetClaims(controller, storeCode: null);

        var result = await controller.AckCurrentReceiptProfile(
            new StoreReceiptProfileAckRequest(1),
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    // ---------------------------------------------------------------- 契约 JSON

    [Fact]
    public void Dto_serializes_version_and_published_at_in_camel_case()
    {
        var json = JsonSerializer.Serialize(Snapshot(3), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"version\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"publishedAt\":\"2026-10-07T01:02:03+00:00\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Dto_from_an_old_server_without_version_deserializes_as_never_published()
    {
        // 新客户端连旧服务端：JSON 里没有 version/publishedAt，必须按「从未下发」读出而不是报错。
        const string oldServerJson =
            """{"storeCode":"S001","storeName":"Store One","brandName":"HB","address":null,"phone":null,"abn":null,"returnPolicy":null}""";

        var dto = JsonSerializer.Deserialize<StoreReceiptProfileDto>(
            oldServerJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(dto);
        Assert.Equal(0, dto.Version);
        Assert.Null(dto.PublishedAt);
    }

    [Fact]
    public void Sync_and_ack_dtos_serialize_contract_keys_in_camel_case()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var sync = JsonSerializer.Serialize(new StoreReceiptProfileSyncDto(false, 3, null), options);
        var ack = JsonSerializer.Serialize(new StoreReceiptProfileAckResultDto(3), options);
        var request = JsonSerializer.Deserialize<StoreReceiptProfileAckRequest>("""{"version":4}""", options);

        Assert.Equal("""{"changed":false,"version":3,"profile":null}""", sync);
        Assert.Equal("""{"appliedVersion":3}""", ack);
        Assert.Equal(4, request!.Version);
    }

    // ---------------------------------------------------------------- 替身

    private static void SetClaims(
        ControllerBase controller,
        string? storeCode,
        string? deviceCode = "POS-01",
        string? deviceSystem = "Windows")
    {
        var claims = new List<Claim>();
        if (storeCode is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode));
        }

        if (deviceCode is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode));
        }

        if (deviceSystem is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.DeviceSystemClaim, deviceSystem));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, DeviceAuthConstants.Scheme)),
            },
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class FakeRepository : IStoreReceiptProfileReleaseRepository
    {
        public int LatestVersion { get; set; }

        public StoreReceiptProfileDto? Latest { get; set; }

        public int AppliedVersionToReturn { get; set; }

        public int GetLatestCalls { get; private set; }

        public List<(string DeviceCode, string StoreCode, int Version, string ClientKind, DateTime NowUtc)> Acks { get; } = [];

        public Task<int> GetLatestVersionAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult(LatestVersion);

        public Task<StoreReceiptProfileDto?> GetLatestAsync(string storeCode, CancellationToken cancellationToken)
        {
            GetLatestCalls++;
            return Task.FromResult(Latest);
        }

        public Task<int> UpsertAckAsync(
            string deviceCode,
            string storeCode,
            int version,
            string clientKind,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Acks.Add((deviceCode, storeCode, version, clientKind, nowUtc));
            return Task.FromResult(AppliedVersionToReturn == 0 ? version : AppliedVersionToReturn);
        }
    }

    private sealed class CapturingReleaseService : IStoreReceiptProfileReleaseService
    {
        public string? SyncStoreCode { get; private set; }

        public int SyncKnownVersion { get; private set; } = -1;

        public (string StoreCode, string DeviceCode, string? DeviceSystem, int Version)? LastAck { get; private set; }

        public StoreReceiptProfileSyncLookupResult SyncResult { get; set; } =
            new(new StoreReceiptProfileSyncDto(false, 0, null));

        public StoreReceiptProfileAckLookupResult AckResult { get; set; } =
            new(new StoreReceiptProfileAckResultDto(2));

        public Task<StoreReceiptProfileSyncLookupResult> GetSyncAsync(
            string storeCode,
            int knownVersion,
            CancellationToken cancellationToken)
        {
            SyncStoreCode = storeCode;
            SyncKnownVersion = knownVersion;
            return Task.FromResult(SyncResult);
        }

        public Task<StoreReceiptProfileAckLookupResult> AckAsync(
            string storeCode,
            string deviceCode,
            string? deviceSystem,
            int version,
            CancellationToken cancellationToken)
        {
            LastAck = (storeCode, deviceCode, deviceSystem, version);
            return Task.FromResult(AckResult);
        }
    }

    private sealed class NoopProfileService : IStoreReceiptProfileService
    {
        public Task<StoreReceiptProfileLookupResult> GetCurrentAsync(
            string storeCode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
