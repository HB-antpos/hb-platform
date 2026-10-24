using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 收银入口按分店设置（主 API 侧）：纯规则（在线口径 / 入口新鲜度 / 版本门槛）、GET 统计口径、
/// PUT 校验 / 范围 / 只改变化行、控制器路由与权限、设备列表 DTO 映射、迁移 SQL 形状。
/// 真实 SQL Server 上的迁移执行见 SchemaMigrationSqlServerIntegrationTests。
/// </summary>
public sealed class PosApiEntryTests : IDisposable
{
    // 2026-10-24 04:00 UTC＝悉尼 15:00（白天，非夜间静默窗口）。
    private static readonly DateTime Now = new(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc);

    private readonly string _mainPath = Path.Combine(Path.GetTempPath(), $"hb-posentry-main-{Guid.NewGuid():N}.db");
    private readonly string _posmPath = Path.Combine(Path.GetTempPath(), $"hb-posentry-posm-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _mainDb;
    private readonly SqlSugarClient _posmDb;
    private readonly FakeScope _scope = new() { Scope = Scope(isAdmin: true) };
    private readonly CapturingLogger<PosApiEntryService> _logger = new();
    private readonly PosApiEntryService _service;

    public PosApiEntryTests()
    {
        _mainDb = CreateSqlite(_mainPath);
        _posmDb = CreateSqlite(_posmPath);
        _mainDb.CodeFirst.InitTables<Store>();
        _posmDb.CodeFirst.InitTables<POSM_设备注册信息表>();
        _service = new PosApiEntryService(_mainDb, _posmDb, _scope, _logger, new FixedTimeProvider(Now));
    }

    public void Dispose()
    {
        _mainDb.Dispose();
        _posmDb.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_mainPath);
        SqliteTempFileCleanup.DeleteIfExists(_posmPath);
    }

    // ───────────────────────── 纯规则 ─────────────────────────

    [Theory]
    [InlineData("edge", "edge")]
    [InlineData(" AWS ", "aws")]
    [InlineData("Direct", "direct")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("tencent", null)]
    [InlineData("edge,aws", null)]
    public void Normalize_只认三个白名单值(string? raw, string? expected) =>
        Assert.Equal(expected, PosApiEntryModes.Normalize(raw));

    [Fact]
    public void IsOnline_白天45秒阈值_夜间静默窗口放宽到12分钟()
    {
        // 白天（悉尼 15:00）：44 秒前的心跳在线，46 秒前离线。
        Assert.True(PosApiEntryRules.IsOnline(true, Now.AddSeconds(-44), Now));
        Assert.False(PosApiEntryRules.IsOnline(true, Now.AddSeconds(-46), Now));
        // 标记离线 / 无心跳一律离线。
        Assert.False(PosApiEntryRules.IsOnline(false, Now, Now));
        Assert.False(PosApiEntryRules.IsOnline(true, null, Now));

        // 夜间：悉尼 23:00（UTC 12:00）发出的心跳，5 分钟前也算在线，13 分钟前离线。
        var night = new DateTime(2026, 10, 24, 12, 5, 0, DateTimeKind.Utc);
        Assert.True(PosApiEntryRules.IsOnline(true, night.AddMinutes(-5), night));
        Assert.False(PosApiEntryRules.IsOnline(true, night.AddMinutes(-13), night));
    }

    [Fact]
    public void FreshEntry_过期或非法值按未知()
    {
        Assert.Equal("aws", PosApiEntryRules.FreshEntry("aws", Now.AddMinutes(-14), Now));
        // 超过 15 分钟窗口（写库节流周期 10 分钟 + 余量）。
        Assert.Null(PosApiEntryRules.FreshEntry("aws", Now.AddMinutes(-16), Now));
        Assert.Null(PosApiEntryRules.FreshEntry("bogus", Now, Now));
        Assert.Null(PosApiEntryRules.FreshEntry("aws", null, Now));
        Assert.Null(PosApiEntryRules.FreshEntry(null, Now, Now));
    }

    [Theory]
    [InlineData("Windows", "1.0.88", true)]
    [InlineData("Windows", "1.0.89", false)]
    [InlineData("Windows", "1.0.100", false)]
    [InlineData("Windows", "1.1.0", false)]
    [InlineData("Windows", null, true)] // 从未上报版本的老 WPF
    [InlineData("Windows", "garbage", true)]
    [InlineData(null, "1.0.50", true)] // 设备系统为空按 Windows 展示
    [InlineData("Android", "1.0.1", false)] // 手持暂不判断
    [InlineData("iPadOS", null, false)] // iPad 暂不判断
    public void IsUnsupportedWpf_只对WPF按1_0_89判断(string? system, string? version, bool expected) =>
        Assert.Equal(expected, PosApiEntryRules.IsUnsupportedWpf(system, version));

    // ───────────────────────── GET 统计口径 ─────────────────────────

    [Fact]
    public async Task GetStores_只返回有启用收银设备的分店_并按口径统计()
    {
        SeedStore("g-1", "S001", "Store 1", mode: "aws");
        SeedStore("g-2", "S002", "Store 2");
        SeedStore("g-3", "S003", "Store 3 无设备");
        SeedStore("g-4", "S004", "Store 4 只有禁用设备");

        // S001（首选 aws）：5 台设备
        SeedDevice("S001", "POS-1", online: true, entry: "aws", entryAt: Now.AddMinutes(-3), version: "1.0.89"); // 在线 / aws
        SeedDevice("S001", "POS-2", online: true, entry: "edge", entryAt: Now.AddMinutes(-3), version: "1.0.89"); // 在线 / edge＝回退
        SeedDevice("S001", "POS-3", online: true, entry: "direct", entryAt: Now.AddMinutes(-30), version: "1.0.88"); // 在线 / 入口过期＝未知；版本过低
        SeedDevice("S001", "POS-5", online: true, entry: "edge", entryAt: Now.AddMinutes(-1), version: "1.0.87"); // 在线 / 版本过低：虽在走 edge，但只计 unsupported，不计 edge 也不算回退
        SeedDevice("S001", "POS-4", online: false, entry: "aws", entryAt: Now, version: null); // 离线：不进在线统计；版本未上报＝版本过低
        SeedDevice("S001", "PAD-1", online: true, entry: null, entryAt: null, system: "iPadOS", version: null); // 在线 / 未知；iPad 不判版本
        // S002（未设置＝edge 为首选）：1 台在线走 edge
        SeedDevice("S002", "POS-9", online: true, entry: "edge", entryAt: Now.AddMinutes(-1), version: "1.0.90");
        // S004：只有禁用设备、非 POS 类型设备都不算
        SeedDevice("S004", "POS-X", online: true, entry: "edge", entryAt: Now, version: "1.0.90", status: 0);
        SeedDevice("S003", "MOB-1", online: true, entry: "edge", entryAt: Now, version: "1.0.90", deviceType: "Mobile");

        var response = await _service.GetStoresAsync();

        Assert.True(response.Success);
        var items = response.Data!;
        Assert.Equal(["S001", "S002"], items.Select(item => item.StoreCode));

        var s1 = items[0];
        Assert.Equal("g-1", s1.StoreGuid);
        Assert.Equal("aws", s1.Mode);
        Assert.Equal("aws", s1.EffectiveMode);
        Assert.Equal(6, s1.Devices.Total);
        Assert.Equal(5, s1.Devices.Online);
        Assert.Equal(1, s1.Devices.ByEntry.Aws);
        Assert.Equal(1, s1.Devices.ByEntry.Edge); // 只有 POS-2；版本过低的 POS-5 不计入
        Assert.Equal(0, s1.Devices.ByEntry.Direct);
        Assert.Equal(3, s1.Devices.ByEntry.Unknown); // POS-3（版本过低）、POS-5（版本过低）、PAD-1（入口未知）
        Assert.Equal(5, s1.Devices.ByEntry.Aws + s1.Devices.ByEntry.Edge + s1.Devices.ByEntry.Direct + s1.Devices.ByEntry.Unknown);
        // 回退＝入口已知却不是首选 aws：只有 POS-2（POS-3 / POS-5 版本过低不算回退）。
        Assert.Equal(1, s1.Devices.FallbackCount);
        // 版本过低：POS-3（1.0.88）、POS-4（未上报）与 POS-5（1.0.87）；iPad 不判。
        Assert.Equal(3, s1.Devices.UnsupportedCount);

        var s2 = items[1];
        Assert.Null(s2.Mode);
        Assert.Equal("edge", s2.EffectiveMode);
        Assert.Equal(1, s2.Devices.Total);
        Assert.Equal(1, s2.Devices.ByEntry.Edge);
        Assert.Equal(0, s2.Devices.FallbackCount);
        Assert.Equal(0, s2.Devices.UnsupportedCount);
    }

    [Fact]
    public async Task GetStores_库里的脏mode按未设置展示()
    {
        SeedStore("g-1", "S001", "Store 1", mode: "hacked");
        SeedDevice("S001", "POS-1", online: true, entry: "edge", entryAt: Now, version: "1.0.89");

        var item = Assert.Single((await _service.GetStoresAsync()).Data!);

        Assert.Null(item.Mode);
        Assert.Equal("edge", item.EffectiveMode);
    }

    [Fact]
    public async Task GetStores_非管理员只看到可管理分店()
    {
        SeedStore("g-1", "S001", "Store 1");
        SeedStore("g-2", "S002", "Store 2");
        SeedDevice("S001", "POS-1", online: true, entry: "edge", entryAt: Now, version: "1.0.89");
        SeedDevice("S002", "POS-2", online: true, entry: "edge", entryAt: Now, version: "1.0.89");
        _scope.Scope = Scope(isAdmin: false, storeCodes: ["S002"], storeGuids: ["g-2"]);

        var items = (await _service.GetStoresAsync()).Data!;

        Assert.Equal("S002", Assert.Single(items).StoreCode);
    }

    [Fact]
    public async Task GetStores_没有可管理分店范围时拒绝()
    {
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };

        var response = await _service.GetStoresAsync();

        Assert.False(response.Success);
        Assert.Equal(PosApiEntryErrorCodes.ScopeForbidden, response.ErrorCode);
    }

    // ───────────────────────── PUT ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tencent")]
    [InlineData("null")]
    public async Task Update_mode必须是白名单三值_不提供清空(string? mode)
    {
        SeedStore("g-1", "S001", "Store 1", mode: "aws");

        var response = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1"], Mode = mode }, "alice");

        Assert.False(response.Success);
        Assert.Equal(PosApiEntryErrorCodes.InvalidMode, response.ErrorCode);
        Assert.Equal("aws", ReadStore("g-1").PosApiEntryMode);
    }

    [Fact]
    public async Task Update_storeGuids为空或超过200或含空白时拒绝()
    {
        var empty = await _service.UpdateAsync(new PosApiEntryUpdateRequestDto { StoreGuids = [], Mode = "aws" }, "alice");
        var nullList = await _service.UpdateAsync(new PosApiEntryUpdateRequestDto { Mode = "aws" }, "alice");
        var tooMany = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = Enumerable.Range(0, 201).Select(i => $"g-{i}").ToList(), Mode = "aws" },
            "alice");
        var blank = await _service.UpdateAsync(new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1", " "], Mode = "aws" }, "alice");
        var noBody = await _service.UpdateAsync(null, "alice");

        foreach (var response in new[] { empty, nullList, tooMany, blank })
        {
            Assert.False(response.Success);
            Assert.Equal(PosApiEntryErrorCodes.InvalidRequest, response.ErrorCode);
        }

        // 没有请求体时 mode 先被判无效。
        Assert.Equal(PosApiEntryErrorCodes.InvalidMode, noBody.ErrorCode);

        // 恰好 200 家是允许的边界（这里都不存在，应走到「分店不存在」而不是参数错误）。
        var boundary = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = Enumerable.Range(0, 200).Select(i => $"g-{i}").ToList(), Mode = "aws" },
            "alice");
        Assert.Equal(PosApiEntryErrorCodes.StoreNotFound, boundary.ErrorCode);
    }

    [Fact]
    public async Task Update_有分店越权时整批拒绝且不写库()
    {
        SeedStore("g-1", "S001", "Store 1");
        SeedStore("g-2", "S002", "Store 2");
        _scope.Scope = Scope(isAdmin: false, storeCodes: ["S001"], storeGuids: ["g-1"]);

        var response = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1", "g-2"], Mode = "aws" }, "alice");

        Assert.False(response.Success);
        Assert.Equal(PosApiEntryErrorCodes.StoreForbidden, response.ErrorCode);
        Assert.Null(ReadStore("g-1").PosApiEntryMode);
        Assert.Null(ReadStore("g-2").PosApiEntryMode);
    }

    [Fact]
    public async Task Update_分店不存在或已删除时整批拒绝()
    {
        SeedStore("g-1", "S001", "Store 1");
        SeedStore("g-2", "S002", "Store 2", deleted: true);

        var response = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1", "g-2", "g-404"], Mode = "aws" }, "alice");

        Assert.False(response.Success);
        Assert.Equal(PosApiEntryErrorCodes.StoreNotFound, response.ErrorCode);
        Assert.Null(ReadStore("g-1").PosApiEntryMode);
    }

    [Fact]
    public async Task Update_只写变化的行_三列_操作人和UTC时间()
    {
        SeedStore("g-1", "S001", "Store 1"); // NULL → aws：变化
        SeedStore("g-2", "S002", "Store 2", mode: "aws", updatedBy: "old", updatedAt: Now.AddDays(-3)); // 已是 aws：跳过
        SeedStore("g-3", "S003", "Store 3", mode: "direct"); // direct → aws：变化
        _mainDb.Ado.ExecuteCommand("UPDATE [Store] SET [UpdatedBy] = 'untouched' WHERE [StoreGUID] = 'g-1'");

        var response = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1", "g-2", "g-3", "g-1"], Mode = "AWS" }, "alice");

        Assert.True(response.Success);
        Assert.Equal(2, response.Data!.Updated);
        Assert.Equal(1, response.Data.Skipped);

        var g1 = ReadStore("g-1");
        Assert.Equal("aws", g1.PosApiEntryMode);
        Assert.Equal("alice", g1.PosApiEntryUpdatedBy);
        Assert.Equal(Now, g1.PosApiEntryUpdatedAt);
        // 只写这三列：BaseEntity 的 UpdatedBy 不动。
        Assert.Equal("untouched", g1.UpdatedBy);
        Assert.Equal("aws", ReadStore("g-3").PosApiEntryMode);
        // 跳过的行保持原来的修改人 / 时间。
        var g2 = ReadStore("g-2");
        Assert.Equal("old", g2.PosApiEntryUpdatedBy);
        Assert.Equal(Now.AddDays(-3), g2.PosApiEntryUpdatedAt);

        // 操作日志：结构化日志里有操作人、目标和每家分店的前后值。
        var log = Assert.Single(_logger.Messages, message => message.Contains("收银入口首选已修改"));
        Assert.Contains("alice", log);
        Assert.Contains("S001:(未设置)->aws", log);
        Assert.Contains("S003:direct->aws", log);
        Assert.DoesNotContain("S002", log);
    }

    [Fact]
    public async Task Update_全部已是目标值时不写库也不记日志()
    {
        SeedStore("g-1", "S001", "Store 1", mode: "edge");

        var response = await _service.UpdateAsync(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1"], Mode = "edge" }, "alice");

        Assert.True(response.Success);
        Assert.Equal(0, response.Data!.Updated);
        Assert.Equal(1, response.Data.Skipped);
        Assert.Empty(_logger.Messages);
    }

    // ───────────────────────── 控制器：路由与权限 ─────────────────────────

    [Fact]
    public void Controller_路由与权限()
    {
        var route = Assert.Single(typeof(PosApiEntryController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/react/v1/pos-api-entry", route.Template);
        Assert.Contains(typeof(PosApiEntryController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy is null);

        var get = typeof(PosApiEntryController).GetMethod(nameof(PosApiEntryController.GetStores))!;
        Assert.Equal("stores", get.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(Permissions.DeviceRegistration.View, get.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

        var put = typeof(PosApiEntryController).GetMethod(nameof(PosApiEntryController.UpdateStores))!;
        Assert.Equal("stores", put.GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.Equal(Permissions.DeviceRegistration.Manage, put.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public async Task Controller_失败映射状态码_成功透传操作人()
    {
        SeedStore("g-1", "S001", "Store 1");
        var controller = new PosApiEntryController(_service, new FakeCurrentUser("bob"));

        var ok = Assert.IsType<OkObjectResult>(await controller.UpdateStores(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1"], Mode = "direct" }, CancellationToken.None));
        Assert.Equal(1, Assert.IsType<ApiResponse<PosApiEntryUpdateResultDto>>(ok.Value).Data!.Updated);
        Assert.Equal("bob", ReadStore("g-1").PosApiEntryUpdatedBy);

        var badMode = Assert.IsType<ObjectResult>(await controller.UpdateStores(
            new PosApiEntryUpdateRequestDto { StoreGuids = ["g-1"], Mode = "x" }, CancellationToken.None));
        Assert.Equal(400, badMode.StatusCode);

        Assert.Equal(403, PosApiEntryController.MapStatusCode(PosApiEntryErrorCodes.StoreForbidden));
        Assert.Equal(403, PosApiEntryController.MapStatusCode(PosApiEntryErrorCodes.ScopeForbidden));
        Assert.Equal(404, PosApiEntryController.MapStatusCode(PosApiEntryErrorCodes.StoreNotFound));
        Assert.Equal(400, PosApiEntryController.MapStatusCode(PosApiEntryErrorCodes.InvalidRequest));

        var list = Assert.IsType<OkObjectResult>(await controller.GetStores(CancellationToken.None));
        Assert.IsType<ApiResponse<List<PosApiEntryStoreDto>>>(list.Value);
    }

    [Fact]
    public void Dto_序列化把mode显式输出为null()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new PosApiEntryStoreDto { StoreGuid = "g", StoreCode = "S", StoreName = "N", Mode = null },
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        Assert.Contains("\"mode\":null", json);
        Assert.Contains("\"effectiveMode\":\"edge\"", json);
        Assert.Contains("\"updatedAt\":null", json);
        Assert.Contains("\"byEntry\"", json);
    }

    // ───────────────────────── 设备列表 DTO / 迁移 SQL ─────────────────────────

    [Fact]
    public void 设备列表映射带上lastApiEntry与lastApiEntryAt_时间标成UTC()
    {
        var mapper = new AutoMapper.MapperConfiguration(
                cfg => cfg.AddProfile<BlazorApp.Api.Mappings.DeviceMappingProfile>(),
                NullLoggerFactory.Instance)
            .CreateMapper();
        var entity = new POSM_设备注册信息表
        {
            ID = 7,
            系统设备编号 = "POS-1",
            LastApiEntry = "aws",
            LastApiEntryAt = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Unspecified),
        };

        var dto = mapper.Map<DeviceListItemDto>(entity);

        Assert.Equal("aws", dto.LastApiEntry);
        Assert.Equal(DateTimeKind.Utc, dto.LastApiEntryAt!.Value.Kind);
        Assert.Null(mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表()).LastApiEntry);
    }

    [Fact]
    public void 迁移SQL_幂等补列加CHECK约束_并带精确签名门禁()
    {
        var store = StorePosApiEntrySchema.ApplySql;
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'PosApiEntryMode') IS NULL", store);
        Assert.Contains("[PosApiEntryMode] nvarchar(16) NULL", store);
        Assert.Contains("[PosApiEntryUpdatedAt] datetime2 NULL", store);
        Assert.Contains("[PosApiEntryUpdatedBy] nvarchar(64) NULL", store);
        Assert.Contains("CK_Store_PosApiEntryMode", store);
        Assert.Contains("IN (N''edge'', N''aws'', N''direct'')", store);
        var storeVerify = StorePosApiEntrySchema.VerifySql;
        Assert.Contains("[is_disabled] = 0", storeVerify);
        Assert.Contains("THROW 54101", storeVerify);

        var device = PosDeviceApiEntrySchema.ApplySql;
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'LastApiEntry') IS NULL", device);
        Assert.Contains("[LastApiEntry] nvarchar(16) NULL", device);
        Assert.Contains("[LastApiEntryAt] datetime2 NULL", device);

        // 两个启动补列入口（主 API 基线 / Hbpos.Api 初始化器）与迁移保持同一定义。
        var baseline = string.Join("\n", BlazorApp.Api.Services.React.DeviceRuntimeStatusSchemaMigrator.SqlScriptsForTests);
        Assert.Contains("[LastApiEntry] NVARCHAR(16) NULL", baseline);
        Assert.Contains("[LastApiEntryAt] DATETIME2(7) NULL", baseline);
    }

    // ───────────────────────── helpers ─────────────────────────

    private void SeedStore(
        string guid, string code, string name, string? mode = null,
        string? updatedBy = null, DateTime? updatedAt = null, bool deleted = false)
    {
        _mainDb.Insertable(new Store
        {
            StoreGUID = guid,
            StoreCode = code,
            StoreName = name,
            IsActive = true,
            IsDeleted = deleted,
            PosApiEntryMode = mode,
            PosApiEntryUpdatedBy = updatedBy,
            PosApiEntryUpdatedAt = updatedAt,
        }).ExecuteCommand();
    }

    private Store ReadStore(string guid) =>
        _mainDb.Queryable<Store>().Where(store => store.StoreGUID == guid).First();

    private void SeedDevice(
        string storeCode, string deviceCode, bool online, string? entry, DateTime? entryAt, string? version,
        string system = "Windows", int status = 1, string deviceType = "POS")
    {
        _posmDb.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = $"hw-{deviceCode}",
            系统设备编号 = deviceCode,
            分店代码 = storeCode,
            设备类型 = deviceType,
            设备系统 = system,
            设备状态 = status,
            设备授权码 = "auth",
            是否在线 = online,
            最后心跳时间 = online ? Now.AddSeconds(-10) : Now.AddHours(-3),
            应用版本 = version,
            LastApiEntry = entry,
            LastApiEntryAt = entryAt,
        }).ExecuteCommand();
    }

    private static CurrentUserManageableStoreScope Scope(
        bool isAdmin, string[]? storeCodes = null, string[]? storeGuids = null) =>
        new()
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsAdmin = isAdmin,
            StoreCodes = storeCodes ?? [],
            StoreGuids = storeGuids ?? [],
        };

    private static SqlSugarClient CreateSqlite(string path) =>
        new(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakeCurrentUser(string name) : ICurrentUserService
    {
        public string GetCurrentUsername() => name;
        public string GetCurrentUserGuid() => "guid";
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        public required CurrentUserManageableStoreScope Scope { get; set; }
        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => Task.FromResult(Scope.StoreCodes);
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => Task.FromResult(Scope.CanAccessStoreCode(storeCode));
        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);
        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(false);
        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
