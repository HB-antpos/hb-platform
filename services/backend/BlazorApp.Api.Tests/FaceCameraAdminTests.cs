using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 人脸抓拍摄像机后台管理服务（SQLite 临时库）。SQLite 没有 SYSUTCDATETIME 与过滤索引，
/// 表由实体 CodeFirst 创建，token 哈希唯一索引在测试里手工补建（与迁移 20261013.001 的 UX_FaceCamera_TokenSha256 同名）。
/// </summary>
public sealed class FaceCameraAdminTests : IDisposable
{
    private const string StoreCode = "1013";
    private static readonly DateTime Now = new(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc);
    private static readonly FaceCameraActor Actor = new("user-admin-1", "admin.tester");

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly MutableTimeProvider _time = new(Now);
    private readonly AuditCapturingLoggerProvider _logs = new();

    public FaceCameraAdminTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(Store), typeof(FaceCamera), typeof(FaceCaptureEvent));
        _db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_FaceCamera_TokenSha256 ON FaceCamera (TokenSha256)");
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "s-1013", StoreCode = StoreCode, StoreName = "Parramatta" },
            new() { StoreGUID = "s-2001", StoreCode = "2001", StoreName = "Bankstown" },
            new() { StoreGUID = "s-del", StoreCode = "DEL1", StoreName = "Closed", IsDeleted = true },
            new() { StoreGUID = "s-long", StoreCode = "THIS-STORE-CODE-IS-LONGER-THAN-32-CHARS", StoreName = "Long" },
        }).ExecuteCommand();
    }

    // ---------- 创建 ----------

    [Fact]
    public async Task Create_生成48位token_库里只存哈希_响应带完整地址()
    {
        var result = await CreateService().CreateAsync(NewRequest("cam-1"), Actor);

        Assert.Matches("^[0-9a-f]{48}$", result.Token);
        Assert.Equal($"https://hotbargain.vip/face-ingest/hik/v1/{result.Token}", result.CameraUrl);
        // 摄像机 URL 字段上限 128，路径部分固定 68 字符。
        Assert.Equal(68, new Uri(result.CameraUrl).AbsolutePath.Length);

        var row = await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1");
        Assert.Equal(Sha256Hex(result.Token), row.TokenSha256);
        Assert.True(row.Enabled);
        Assert.Equal(Now, row.CreatedAtUtc);
        Assert.Equal(StoreCode, row.StoreCode);
        Assert.Equal("Parramatta", result.Camera.StoreName);
        Assert.False(result.Camera.Online);
        Assert.Null(row.LastSeenAtUtc);
    }

    [Fact]
    public async Task Create_token明文不入库_整行所有列都不含明文()
    {
        var result = await CreateService().CreateAsync(NewRequest("cam-1"), Actor);

        var dump = await DumpDatabaseAsync();
        Assert.DoesNotContain(result.Token, dump, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Sha256Hex(result.Token), dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_连续创建的token互不相同()
    {
        var service = CreateService();
        var first = await service.CreateAsync(NewRequest("cam-1"), Actor);
        var second = await service.CreateAsync(NewRequest("cam-2"), Actor);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(2, await _db.Queryable<FaceCamera>().Select(camera => camera.TokenSha256).Distinct().CountAsync());
    }

    [Fact]
    public async Task Create_可选字段trim_空串存为null()
    {
        var result = await CreateService().CreateAsync(
            NewRequest("cam-1") with { Note = "  正门  ", DeviceModel = "   ", MacAddress = null }, Actor);

        Assert.Equal("正门", result.Camera.Note);
        Assert.Null(result.Camera.DeviceModel);
        Assert.Null(result.Camera.MacAddress);
    }

    [Fact]
    public async Task Create_重复CameraId返回409且错误信息不含token()
    {
        var service = CreateService();
        var first = await service.CreateAsync(NewRequest("cam-1"), Actor);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => service.CreateAsync(NewRequest("cam-1"), Actor));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_ID_EXISTS", ex.Code);
        Assert.DoesNotContain(first.Token, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public async Task Create_并发撞主键时由唯一约束兜底为409()
    {
        // 在服务「先查不存在」之后、真正 INSERT 之前，用另一条连接抢先写入同名摄像机，走到唯一约束兜底分支。
        var raced = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (raced || !sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)) return;
            raced = true;
            using var rival = new SqliteConnection(_connection.ConnectionString);
            rival.Open();
            using var command = rival.CreateCommand();
            command.CommandText =
                "INSERT INTO FaceCamera (CameraId, StoreCode, TokenSha256, Enabled, CreatedAtUtc, UpdatedAtUtc) "
                + "VALUES ('cam-1', '1013', '" + new string('b', 64) + "', 1, '2026-10-13 00:00:00', '2026-10-13 00:00:00')";
            command.ExecuteNonQuery();
        };

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateService().CreateAsync(NewRequest("cam-1"), Actor));
        _db.Aop.OnLogExecuting = null;

        Assert.True(raced);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_ID_EXISTS", ex.Code);
        Assert.Equal(1, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public async Task Create_token哈希撞车时换新token重试_反复撞车报409()
    {
        // 先占住一个哈希：用「伪造生成器」不可行，所以让所有哈希都被占——这里改为验证重试上限：
        // 预置 200 台摄像机无法穷举哈希，因此通过 Aop 在每次 INSERT 前把本次哈希抢占掉，模拟「每次都撞」。
        var attempts = 0;
        _db.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (!sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)) return;
            var hash = parameters.First(parameter => parameter.ParameterName.Contains("TokenSha256", StringComparison.OrdinalIgnoreCase)).Value!.ToString()!;
            attempts++;
            using var rival = new SqliteConnection(_connection.ConnectionString);
            rival.Open();
            using var command = rival.CreateCommand();
            command.CommandText =
                $"INSERT INTO FaceCamera (CameraId, StoreCode, TokenSha256, Enabled, CreatedAtUtc, UpdatedAtUtc) "
                + $"VALUES ('rival-{attempts}', '1013', '{hash}', 1, '2026-10-13 00:00:00', '2026-10-13 00:00:00')";
            command.ExecuteNonQuery();
        };

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateService().CreateAsync(NewRequest("cam-1"), Actor));
        _db.Aop.OnLogExecuting = null;

        Assert.Equal(3, attempts);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_TOKEN_CONFLICT", ex.Code);
        Assert.False(await _db.Queryable<FaceCamera>().AnyAsync(camera => camera.CameraId == "cam-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-abc")]
    [InlineData("ABC")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("../etc")]
    [InlineData("摄像机")]
    [InlineData("a_b")]
    public async Task Create_非法CameraId返回400(string cameraId)
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateService().CreateAsync(NewRequest(cameraId), Actor));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_ID_INVALID", ex.Code);
        Assert.Equal(0, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public async Task Create_CameraId长度边界_64位通过_65位拒绝()
    {
        var service = CreateService();
        var ok = await service.CreateAsync(NewRequest(new string('a', 64)), Actor);
        Assert.Equal(64, ok.Camera.CameraId.Length);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.CreateAsync(NewRequest(new string('a', 65)), Actor));
        Assert.Equal("FACE_CAMERA_ID_INVALID", ex.Code);
    }

    [Fact]
    public void Rules_CameraId与StoreCode用结尾锚_不放过结尾换行()
    {
        // 正则必须用 \z 而不是 $：$ 会放过结尾的 \n，而 CameraId 会进文件路径。
        Assert.False(FaceCameraRules.IsValidCameraId("abc\n"));
        Assert.False(FaceCameraRules.IsValidStoreCode("1013\n"));
        Assert.True(FaceCameraRules.IsValidCameraId("a"));
        Assert.True(FaceCameraRules.IsValidStoreCode("A_b-9"));
        Assert.False(FaceCameraRules.IsValidStoreCode(new string('1', 33)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("10 13")]
    [InlineData("10/13")]
    [InlineData("门店")]
    [InlineData("123456789012345678901234567890123")]
    public async Task Create_非法StoreCode返回400(string? storeCode)
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateService().CreateAsync(NewRequest("cam-1") with { StoreCode = storeCode }, Actor));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_STORE_CODE_INVALID", ex.Code);
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("DEL1")]
    public async Task Create_门店不存在或已删除返回400(string storeCode)
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateService().CreateAsync(NewRequest("cam-1") with { StoreCode = storeCode }, Actor));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_STORE_NOT_FOUND", ex.Code);
        Assert.Equal(0, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public async Task Create_门店表里编码超过人脸服务限制时拒绝()
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateService().CreateAsync(NewRequest("cam-1") with { StoreCode = "THIS-STORE-CODE-IS-LONGER-THAN-32-CHARS" }, Actor));

        // 输入本身 40 位已经在格式校验处被拒。
        Assert.Equal("FACE_CAMERA_STORE_CODE_INVALID", ex.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidFieldCases))]
    public async Task Create_字段超长或含控制字符返回400(FaceCameraCreateRequest request)
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateService().CreateAsync(request, Actor));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_FIELD_INVALID", ex.Code);
    }

    public static IEnumerable<object[]> InvalidFieldCases()
    {
        FaceCameraCreateRequest Base() => new() { CameraId = "cam-1", StoreCode = StoreCode };
        yield return [Base() with { Note = new string('x', 201) }];
        yield return [Base() with { Note = "a\nb" }];
        yield return [Base() with { DeviceModel = new string('x', 65) }];
        yield return [Base() with { MacAddress = new string('x', 33) }];
        yield return [Base() with { MacAddress = "aa:bb\u0000" }];
    }

    [Fact]
    public async Task Create_字段长度刚好等于上限时通过()
    {
        var result = await CreateService().CreateAsync(NewRequest("cam-1") with
        {
            Note = new string('n', 200), DeviceModel = new string('m', 64), MacAddress = new string('a', 32),
        }, Actor);

        Assert.Equal(200, result.Camera.Note!.Length);
    }

    // ---------- 轮换 ----------

    [Fact]
    public async Task RotateToken_替换哈希_旧哈希不再存在_新token可对上新哈希()
    {
        var service = CreateService();
        var created = await service.CreateAsync(NewRequest("cam-1"), Actor);
        _time.Now = Now.AddMinutes(30);

        var rotated = await service.RotateTokenAsync("cam-1", Actor);

        Assert.NotEqual(created.Token, rotated.Token);
        Assert.Matches("^[0-9a-f]{48}$", rotated.Token);
        Assert.Equal($"https://hotbargain.vip/face-ingest/hik/v1/{rotated.Token}", rotated.CameraUrl);
        var oldHash = Sha256Hex(created.Token);
        Assert.Equal(0, await _db.Queryable<FaceCamera>().Where(camera => camera.TokenSha256 == oldHash).CountAsync());
        var row = await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1");
        Assert.Equal(Sha256Hex(rotated.Token), row.TokenSha256);
        Assert.Equal(Now.AddMinutes(30), row.UpdatedAtUtc);
        Assert.Equal(Now, row.CreatedAtUtc);
        var dump = await DumpDatabaseAsync();
        Assert.DoesNotContain(rotated.Token, dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.Token, dump, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RotateToken_不影响LastSeen与启停状态()
    {
        var service = CreateService();
        await service.CreateAsync(NewRequest("cam-1"), Actor);
        var seen = Now.AddMinutes(-3);
        _db.Updateable<FaceCamera>().SetColumns(camera => camera.LastSeenAtUtc == seen).Where(camera => camera.CameraId == "cam-1").ExecuteCommand();

        await service.RotateTokenAsync("cam-1", Actor);

        var row = await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1");
        Assert.Equal(seen, row.LastSeenAtUtc);
        Assert.True(row.Enabled);
    }

    [Fact]
    public async Task RotateToken_摄像机不存在返回404()
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => CreateService().RotateTokenAsync("nope", Actor));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("FACE_CAMERA_NOT_FOUND", ex.Code);
    }

    // ---------- 更新 / 停用 / 启用 ----------

    [Fact]
    public async Task Update_停用再启用_只改管理字段_不动哈希与LastSeen与CameraId()
    {
        var service = CreateService();
        var created = await service.CreateAsync(NewRequest("cam-1"), Actor);
        var seen = Now.AddMinutes(-1);
        _db.Updateable<FaceCamera>().SetColumns(camera => camera.LastSeenAtUtc == seen).Where(camera => camera.CameraId == "cam-1").ExecuteCommand();
        _time.Now = Now.AddHours(1);

        var disabled = await service.UpdateAsync("cam-1", UpdateRequest(enabled: false), Actor);
        Assert.False(disabled.Enabled);
        Assert.False(disabled.Online);
        Assert.Equal(Now.AddHours(1), disabled.UpdatedAtUtc);

        var enabled = await service.UpdateAsync("cam-1", UpdateRequest(enabled: true), Actor);
        Assert.True(enabled.Enabled);

        var row = await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1");
        Assert.Equal(Sha256Hex(created.Token), row.TokenSha256);
        Assert.Equal(seen, row.LastSeenAtUtc);
        Assert.Equal(1, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public async Task Update_漏传enabled返回400_不会被当成停用()
    {
        var service = CreateService();
        await service.CreateAsync(NewRequest("cam-1"), Actor);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.UpdateAsync("cam-1", new FaceCameraUpdateRequest { StoreCode = StoreCode }, Actor));

        Assert.Equal("FACE_CAMERA_FIELD_INVALID", ex.Code);
        Assert.True((await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1")).Enabled);
    }

    [Fact]
    public async Task Update_修改门店备注型号与MAC()
    {
        var service = CreateService();
        await service.CreateAsync(NewRequest("cam-1"), Actor);

        var updated = await service.UpdateAsync("cam-1", new FaceCameraUpdateRequest
        {
            StoreCode = "2001", Note = "后门", DeviceModel = "DS-2CD", MacAddress = "aa:bb:cc:dd:ee:ff", Enabled = true,
        }, Actor);

        Assert.Equal("2001", updated.StoreCode);
        Assert.Equal("Bankstown", updated.StoreName);
        Assert.Equal("后门", updated.Note);
        Assert.Equal("DS-2CD", updated.DeviceModel);
        Assert.Equal("aa:bb:cc:dd:ee:ff", updated.MacAddress);
    }

    [Fact]
    public async Task Update_门店不存在_摄像机不存在_字段非法_各自报错()
    {
        var service = CreateService();
        await service.CreateAsync(NewRequest("cam-1"), Actor);

        var noStore = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.UpdateAsync("cam-1", UpdateRequest(storeCode: "9999"), Actor));
        var badStore = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.UpdateAsync("cam-1", UpdateRequest(storeCode: "a b"), Actor));
        var missing = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.UpdateAsync("nope", UpdateRequest(), Actor));
        var longNote = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.UpdateAsync("cam-1", UpdateRequest() with { Note = new string('x', 201) }, Actor));

        Assert.Equal("FACE_CAMERA_STORE_NOT_FOUND", noStore.Code);
        Assert.Equal("FACE_CAMERA_STORE_CODE_INVALID", badStore.Code);
        Assert.Equal("FACE_CAMERA_NOT_FOUND", missing.Code);
        Assert.Equal("FACE_CAMERA_FIELD_INVALID", longNote.Code);
        Assert.Equal(StoreCode, (await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == "cam-1")).StoreCode);
    }

    // ---------- 列表与统计口径 ----------

    [Theory]
    [InlineData(0, true)]        // 刚推送
    [InlineData(599, true)]
    [InlineData(600, true)]      // 恰好 10 分钟仍算在线
    [InlineData(601, false)]     // 超过 10 分钟离线
    [InlineData(-30, true)]      // 时钟偏差导致的未来时间不算离线
    public async Task List_在线判定边界(int secondsAgo, bool expectedOnline)
    {
        await SeedCameraAsync("cam-1", lastSeen: Now.AddSeconds(-secondsAgo));

        var list = await CreateService().ListAsync();

        Assert.Equal(expectedOnline, Assert.Single(list.Items).Online);
        Assert.Equal(10, list.OnlineWindowMinutes);
        Assert.Equal(24, list.StatsWindowHours);
        Assert.Equal(Now, list.GeneratedAtUtc);
    }

    [Fact]
    public async Task List_从未推送与已停用都不是在线()
    {
        await SeedCameraAsync("cam-1", lastSeen: null);
        await SeedCameraAsync("cam-2", lastSeen: Now.AddMinutes(-1), enabled: false);

        var items = (await CreateService().ListAsync()).Items.ToDictionary(item => item.CameraId);

        Assert.False(items["cam-1"].Online);
        Assert.Null(items["cam-1"].LastSeenAtUtc);
        Assert.False(items["cam-2"].Online);
        Assert.False(items["cam-2"].Enabled);
    }

    [Fact]
    public async Task List_统计口径_24小时窗口_重复报文不计人脸数_解析失败单独计数()
    {
        await SeedCameraAsync("cam-1");
        await SeedCameraAsync("cam-2");
        // 窗口内：正常 3 张脸、部分解析 2 张脸、重复报文 5 张脸（不计人脸数但计事件数）、无法解析 0 张脸。
        await SeedEventAsync("e01", "cam-1", Now.AddHours(-1), faceCount: 3, parseStatus: 0);
        await SeedEventAsync("e02", "cam-1", Now.AddHours(-2), faceCount: 2, parseStatus: 1);
        await SeedEventAsync("e03", "cam-1", Now.AddHours(-3), faceCount: 5, parseStatus: 0, duplicateOf: "e01");
        await SeedEventAsync("e04", "cam-1", Now.AddHours(-4), faceCount: 0, parseStatus: 2);
        // 窗口边界：恰好 24 小时前计入，早 1 秒不计入。
        await SeedEventAsync("e05", "cam-1", Now.AddHours(-24), faceCount: 1, parseStatus: 0);
        await SeedEventAsync("e06", "cam-1", Now.AddHours(-24).AddSeconds(-1), faceCount: 100, parseStatus: 2);
        // 别的摄像机的事件互不串。
        await SeedEventAsync("e07", "cam-2", Now.AddMinutes(-5), faceCount: 7, parseStatus: 0);

        var items = (await CreateService().ListAsync()).Items.ToDictionary(item => item.CameraId);

        var cam1 = items["cam-1"];
        Assert.Equal(5, cam1.CaptureCount24h);
        Assert.Equal(3 + 2 + 0 + 0 + 1, cam1.FaceCount24h);
        Assert.Equal(1, cam1.ParseFailureCount24h);
        Assert.Equal(Now.AddHours(-1), cam1.LastEventAtUtc);
        Assert.Equal(DateTimeKind.Utc, cam1.LastEventAtUtc!.Value.Kind);
        Assert.Equal(1, items["cam-2"].CaptureCount24h);
        Assert.Equal(7, items["cam-2"].FaceCount24h);
    }

    [Fact]
    public async Task List_最近一次事件不限24小时_无事件为空()
    {
        await SeedCameraAsync("cam-1");
        await SeedCameraAsync("cam-2");
        await SeedEventAsync("e01", "cam-1", Now.AddDays(-9), faceCount: 4, parseStatus: 0);
        await SeedEventAsync("e02", "cam-1", Now.AddDays(-3), faceCount: 4, parseStatus: 0);

        var items = (await CreateService().ListAsync()).Items.ToDictionary(item => item.CameraId);

        Assert.Equal(0, items["cam-1"].CaptureCount24h);
        Assert.Equal(Now.AddDays(-3), items["cam-1"].LastEventAtUtc);
        Assert.Null(items["cam-2"].LastEventAtUtc);
        Assert.Equal(0, items["cam-2"].CaptureCount24h);
    }

    [Fact]
    public async Task List_按门店编码与编号排序_带门店名_空表返回空列表()
    {
        Assert.Empty((await CreateService().ListAsync()).Items);

        await SeedCameraAsync("cam-b", storeCode: "2001");
        await SeedCameraAsync("cam-c", storeCode: StoreCode);
        await SeedCameraAsync("cam-a", storeCode: StoreCode);

        var items = (await CreateService().ListAsync()).Items;

        Assert.Equal(["cam-a", "cam-c", "cam-b"], items.Select(item => item.CameraId));
        Assert.Equal(["Parramatta", "Parramatta", "Bankstown"], items.Select(item => item.StoreName));
    }

    [Fact]
    public async Task List_统计只用固定条数的SQL_不随摄像机数量增长()
    {
        for (var i = 0; i < 12; i++)
        {
            await SeedCameraAsync($"cam-{i:00}");
        }

        var statements = new List<string>();
        _db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);
        await CreateService().ListAsync();
        _db.Aop.OnLogExecuting = null;

        // 摄像机、门店名、统计各一条，与摄像机数量无关（不是 N+1）。
        Assert.Equal(3, statements.Count);
    }

    // ---------- 最近抓拍 ----------

    [Fact]
    public async Task Captures_按时间倒序分页_默认20_最大50_带hasMore()
    {
        await SeedCameraAsync("cam-1");
        for (var i = 0; i < 55; i++)
        {
            await SeedEventAsync($"e{i:000}", "cam-1", Now.AddMinutes(-i), faceCount: 1, parseStatus: 0);
        }

        var service = CreateService();
        var first = await service.ListCapturesAsync("cam-1", 1, 0);
        var second = await service.ListCapturesAsync("cam-1", 2, 20);
        var last = await service.ListCapturesAsync("cam-1", 2, 50);
        var clamped = await service.ListCapturesAsync("cam-1", 1, 500);

        Assert.Equal(20, first.Items.Count);
        Assert.Equal(20, first.PageSize);
        Assert.True(first.HasMore);
        Assert.Equal("e000", first.Items[0].CaptureId);
        Assert.Equal("e020", second.Items[0].CaptureId);
        Assert.Equal(5, last.Items.Count);
        Assert.False(last.HasMore);
        Assert.Equal(50, clamped.Items.Count);
        Assert.Equal(50, clamped.PageSize);
        Assert.True(clamped.HasMore);
        Assert.True(first.Items.Zip(first.Items.Skip(1)).All(pair => pair.First.ReceivedAtUtc >= pair.Second.ReceivedAtUtc));
    }

    [Fact]
    public async Task Captures_只返回本摄像机_元数据字段齐全_重复报文有标记()
    {
        await SeedCameraAsync("cam-1");
        await SeedCameraAsync("cam-2");
        await SeedEventAsync("e01", "cam-1", Now.AddMinutes(-1), faceCount: 2, parseStatus: 1,
            eventType: "faceCapture", parseNote: "part 2 skipped", cameraEventTime: new DateTimeOffset(2026, 10, 13, 12, 59, 0, TimeSpan.FromHours(10)));
        await SeedEventAsync("e02", "cam-1", Now.AddMinutes(-2), faceCount: 2, parseStatus: 0, duplicateOf: "e01");
        await SeedEventAsync("e03", "cam-2", Now.AddMinutes(-1), faceCount: 9, parseStatus: 0);

        var page = await CreateService().ListCapturesAsync("cam-1", 1, 20);

        Assert.Equal(["e01", "e02"], page.Items.Select(item => item.CaptureId));
        var first = page.Items[0];
        Assert.Equal(Now.AddMinutes(-1), first.ReceivedAtUtc);
        Assert.Equal(DateTimeKind.Utc, first.ReceivedAtUtc.Kind);
        Assert.Equal("faceCapture", first.EventType);
        Assert.Equal(2, first.FaceCount);
        Assert.Equal(1, first.ParseStatus);
        Assert.Equal("part 2 skipped", first.ParseNote);
        // SQLite 只保存墙钟时间文本：偏移丢失，读回时还会按运行机器的本地时区重新解释，所以这里只断言墙钟部分（12:59），
        // 不断言偏移与绝对时刻；偏移保真由真实 SQL Server 用例 FaceCameraAdminSqlServerTests 覆盖。这样本测试不依赖 CI 机器的时区。
        Assert.Equal(new DateTime(2026, 10, 13, 12, 59, 0), first.CameraEventTime!.Value.DateTime);
        Assert.False(first.IsDuplicate);
        Assert.True(page.Items[1].IsDuplicate);
    }

    [Fact]
    public void Captures_响应契约不含对象键_地址_哈希_原始报文字段()
    {
        // 图片元数据（FaceCaptureImageMetaDto，仅限有权限者）可以有 images / storage，但任何契约和后台实体映射里
        // 都不能有对象键、地址、哈希、原始报文：图片字节只经专用内容接口转发。图片字段的细节契约见 FaceCaptureImageListTests。
        var forbidden = new[] { "object", "url", "key", "path", "sha", "body", "raw", "base64", "bucket", "cos" };
        var names = typeof(FaceCaptureItemDto).GetProperties().Select(property => property.Name)
            .Concat(typeof(FaceCapturePageDto).GetProperties().Select(property => property.Name))
            .Concat(typeof(FaceCaptureImageMetaDto).GetProperties().Select(property => property.Name))
            .Concat(typeof(FaceCaptureEvent).GetProperties().Select(property => property.Name))
            .ToList();

        Assert.All(names, name => Assert.DoesNotContain(forbidden, word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Captures_默认不带图片元数据_canViewImages为false()
    {
        await SeedCameraAsync("cam-1");
        await SeedEventAsync("e01", "cam-1", Now.AddMinutes(-1), faceCount: 1, parseStatus: 0);

        var page = await CreateService().ListCapturesAsync("cam-1", 1, 20);

        Assert.False(page.CanViewImages);
        Assert.Empty(page.Items.Single().Images);
    }

    [Fact]
    public async Task Captures_摄像机不存在404_页码过大400_负页码按第一页()
    {
        await SeedCameraAsync("cam-1");
        var service = CreateService();

        var missing = await Assert.ThrowsAsync<FaceCameraException>(() => service.ListCapturesAsync("nope", 1, 20));
        var tooDeep = await Assert.ThrowsAsync<FaceCameraException>(() => service.ListCapturesAsync("cam-1", 101, 20));
        var negative = await service.ListCapturesAsync("cam-1", -3, 20);

        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(400, tooDeep.StatusCode);
        Assert.Equal(1, negative.Page);
    }

    // ---------- 日志与审计 ----------

    [Fact]
    public async Task 日志_全流程不含token明文_审计包含操作人摄像机与动作()
    {
        var service = CreateService();
        var created = await service.CreateAsync(NewRequest("cam-1"), Actor);
        var rotated = await service.RotateTokenAsync("cam-1", Actor);
        await service.UpdateAsync("cam-1", UpdateRequest(enabled: false), Actor);
        await service.UpdateAsync("cam-1", UpdateRequest(enabled: true, note: "改备注"), Actor);
        await Assert.ThrowsAsync<FaceCameraException>(() => service.CreateAsync(NewRequest("cam-1"), Actor));

        var everything = _logs.Dump();
        foreach (var token in new[] { created.Token, rotated.Token })
        {
            Assert.DoesNotContain(token, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Sha256Hex(token), everything, StringComparison.OrdinalIgnoreCase);
        }

        var audits = _logs.Entries.Where(entry => entry.Message.Contains("人脸摄像机后台审计")).ToList();
        Assert.Equal(["create", "rotate-token", "disable", "enable", "update"], audits.Select(entry => entry.State["Action"]));
        Assert.All(audits, entry =>
        {
            Assert.Equal("cam-1", entry.State["CameraId"]);
            Assert.Equal(StoreCode, entry.State["StoreCode"]);
            Assert.Equal("user-admin-1", entry.State["ActorId"]);
            Assert.Equal("admin.tester", entry.State["ActorName"]);
            Assert.Equal(Now, (DateTime)entry.State["AtUtc"]);
        });
    }

    [Fact]
    public async Task 审计_操作人名称里的控制字符会被清掉_防日志注入()
    {
        await CreateService().CreateAsync(NewRequest("cam-1"), new FaceCameraActor("id\r\n1", "evil\nname"));

        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸摄像机后台审计"));
        Assert.Equal("id1", audit.State["ActorId"]);
        Assert.Equal("evilname", audit.State["ActorName"]);
    }

    [Fact]
    public void 一次性响应对象ToString与日志解构不泄露token()
    {
        var secret = new FaceCameraSecretDto
        {
            Camera = new FaceCameraDto { CameraId = "cam-1" },
            Token = "0123456789abcdef0123456789abcdef0123456789abcdef",
            CameraUrl = "https://hotbargain.vip/face-ingest/hik/v1/0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        Assert.DoesNotContain(secret.Token, secret.ToString(), StringComparison.Ordinal);
    }

    // ---------- 配置 ----------

    [Theory]
    [InlineData("https://camera.example.test", "https://camera.example.test")]
    [InlineData("https://camera.example.test/", "https://camera.example.test")]
    [InlineData("http://insecure.example.test", "https://hotbargain.vip")]
    [InlineData("not a url", "https://hotbargain.vip")]
    [InlineData("https://camera.example.test/sub", "https://hotbargain.vip")]
    [InlineData(null, "https://hotbargain.vip")]
    public async Task 地址前缀可配置_非https或带路径时回退默认值(string? configured, string expectedPrefix)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configured == null
                ? []
                : new Dictionary<string, string?> { [FaceCameraAdminService.PublicBaseUrlConfigKey] = configured })
            .Build();

        var result = await CreateService(config).CreateAsync(NewRequest("cam-1"), Actor);

        Assert.StartsWith(expectedPrefix + "/face-ingest/hik/v1/", result.CameraUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Token哈希与人脸接收服务口径一致_UTF8的SHA256小写十六进制()
    {
        // 与 BlazorApp.FaceIngest CameraRegistry.FindCameraByToken 的算法逐字节对齐。
        const string fake = "00112233445566778899aabbccddeeff0011223344556677";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fake))).ToLowerInvariant();

        Assert.Equal(expected, FaceCameraRules.HashToken(fake));
        Assert.Equal(64, FaceCameraRules.HashToken(fake).Length);
    }

    // ---------- 辅助 ----------

    private FaceCameraAdminService CreateService(IConfiguration? config = null) =>
        new(
            CreateSqlSugarContext(_db),
            _time,
            config ?? new ConfigurationBuilder().Build(),
            _logs.CreateLogger<FaceCameraAdminService>());

    private static FaceCameraCreateRequest NewRequest(string cameraId) =>
        new() { CameraId = cameraId, StoreCode = StoreCode };

    private static FaceCameraUpdateRequest UpdateRequest(string storeCode = StoreCode, bool enabled = true, string? note = null) =>
        new() { StoreCode = storeCode, Enabled = enabled, Note = note };

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task SeedCameraAsync(string cameraId, string storeCode = StoreCode, DateTime? lastSeen = null, bool enabled = true)
    {
        await _db.Insertable(new FaceCamera
        {
            CameraId = cameraId,
            StoreCode = storeCode,
            TokenSha256 = Sha256Hex("seed-" + cameraId),
            Enabled = enabled,
            CreatedAtUtc = Now.AddDays(-30),
            UpdatedAtUtc = Now.AddDays(-30),
            LastSeenAtUtc = lastSeen,
        }).ExecuteCommandAsync();
    }

    private async Task SeedEventAsync(
        string captureId, string cameraId, DateTime receivedAt, int faceCount, byte parseStatus,
        string? duplicateOf = null, string? eventType = null, string? parseNote = null, DateTimeOffset? cameraEventTime = null)
    {
        await _db.Insertable(new FaceCaptureEvent
        {
            CaptureId = captureId,
            CameraId = cameraId,
            ReceivedAtUtc = receivedAt,
            FaceCount = faceCount,
            ParseStatus = parseStatus,
            DuplicateOfCaptureId = duplicateOf,
            EventType = eventType,
            ParseNote = parseNote,
            CameraEventTime = cameraEventTime,
        }).ExecuteCommandAsync();
    }

    /// <summary>把整个测试库所有表的所有列拼成一个字符串，用来断言「明文 token 不在库里任何位置」。</summary>
    private async Task<string> DumpDatabaseAsync()
    {
        var builder = new StringBuilder();
        foreach (var table in new[] { "FaceCamera", "FaceCaptureEvent", "Store" })
        {
            var data = await _db.Ado.GetDataTableAsync($"SELECT * FROM {table}");
            foreach (System.Data.DataRow row in data.Rows)
            {
                builder.AppendLine(string.Join("|", row.ItemArray.Select(item => Convert.ToString(item))));
            }
        }

        return builder.ToString();
    }

    internal static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    internal sealed class MutableTimeProvider(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);
    }
}

/// <summary>收集结构化日志（含模板参数），测试里据此断言审计内容与「日志里没有 token」。</summary>
internal sealed class AuditCapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _entries = [];

    public IReadOnlyList<LogRecord> Entries
    {
        get { lock (_entries) { return _entries.ToList(); } }
    }

    /// <summary>所有日志的消息 + 全部模板参数 + 异常文本，拼成一个字符串。</summary>
    public string Dump()
    {
        return string.Join("\n", Entries.Select(entry =>
            entry.Message + " " + string.Join(" ", entry.State.Select(pair => $"{pair.Key}={pair.Value}")) + " " + entry.Exception));
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new Logger(this));

    public void Dispose() { }

    internal sealed record LogRecord(LogLevel Level, string Message, Dictionary<string, object?> State, Exception? Exception);

    private sealed class Logger(AuditCapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            lock (owner._entries)
            {
                owner._entries.Add(new LogRecord(logLevel, formatter(state, exception), values, exception));
            }
        }
    }

    private sealed class Logger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
