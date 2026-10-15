using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers.React;
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
using Microsoft.AspNetCore.Mvc.Routing;
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
/// 抓拍图片查看相关测试共用的假数据。全部是明显的假值：假对象键、假字节，不是任何真实图片、密钥或桶。
/// </summary>
internal static class FaceImageTestData
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x46, 0x4B, 0x45];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    public static string Hex32(char ch) => new(ch, 32);

    /// <summary>与接收服务布局一致的假对象键（前缀 / images / 门店 / 年 / 月 / 日 / 摄像机 / 文件）。</summary>
    public static string Key(string cameraId, string name) => $"face-ingest/images/1013/2026/10/13/{cameraId}/{name}.jpg";
}

/// <summary>图片测试共用的内存式 COS 替身：记录被请求的对象键，按需返回字节或抛业务错误。</summary>
internal sealed class FakeFaceImageStore : IFaceImageStore
{
    public List<string> Keys { get; } = [];
    public int MaxBytesSeen { get; private set; }
    public Func<string, byte[]> Respond { get; set; } = _ => FaceImageTestData.Jpeg;
    public FaceCameraException? Failure { get; set; }

    public Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default)
    {
        Keys.Add(objectKey);
        MaxBytesSeen = maxBytes;
        if (Failure != null) throw Failure;
        return Task.FromResult(Respond(objectKey));
    }
}

/// <summary>
/// 图片测试共用的 SQLite 临时库：Store / FaceCamera / FaceCaptureEvent 由实体建表，
/// FaceCaptureImage 按迁移 20261013.001-face-capture 的列定义手工建表（后台不为它建实体，读取走原生 SQL）。
/// </summary>
internal sealed class FaceImageTestDatabase : IDisposable
{
    public const string StoreCode = "1013";
    public static readonly DateTime Now = new(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;

    public SqlSugarClient Db { get; }

    public FaceImageTestDatabase()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        Db.CodeFirst.InitTables(typeof(Store), typeof(FaceCamera), typeof(FaceCaptureEvent), typeof(FaceStoreProfile));
        Db.Ado.ExecuteCommand("""
CREATE TABLE FaceCaptureImage (
    ImageId char(32) NOT NULL PRIMARY KEY,
    CaptureId char(32) NOT NULL,
    Kind tinyint NOT NULL,
    PartIndex int NOT NULL,
    FaceIndex int NULL,
    Storage nvarchar(16) NOT NULL,
    ObjectKey nvarchar(400) NOT NULL,
    ContentType nvarchar(64) NULL,
    ByteSize int NOT NULL,
    Sha256 char(64) NOT NULL,
    Width int NULL,
    Height int NULL,
    RectX decimal(9, 4) NULL,
    RectY decimal(9, 4) NULL,
    RectW decimal(9, 4) NULL,
    RectH decimal(9, 4) NULL,
    RectNormalized bit NULL,
    FaceScore decimal(9, 4) NULL,
    CameraFaceId nvarchar(64) NULL,
    AttributesJson nvarchar(2000) NULL,
    CreatedAtUtc datetime2 NOT NULL
)
""");
        Db.Insertable(new Store { StoreGUID = "s-1013", StoreCode = StoreCode, StoreName = "Parramatta" }).ExecuteCommand();
    }

    public void AddCamera(string cameraId) =>
        Db.Insertable(new FaceCamera
        {
            CameraId = cameraId,
            StoreCode = StoreCode,
            TokenSha256 = cameraId.PadLeft(64, 'c'),
            Enabled = true,
            CreatedAtUtc = Now.AddDays(-30),
            UpdatedAtUtc = Now.AddDays(-30),
        }).ExecuteCommand();

    public void AddEvent(string captureId, string cameraId, DateTime receivedAt) =>
        Db.Insertable(new FaceCaptureEvent
        {
            CaptureId = captureId,
            CameraId = cameraId,
            ReceivedAtUtc = receivedAt,
            FaceCount = 1,
            ParseStatus = 0,
        }).ExecuteCommand();

    public void AddImage(
        string imageId,
        string captureId,
        string objectKey,
        int kind = 0,
        int partIndex = 0,
        string storage = "cos",
        string? contentType = "image/jpeg",
        int byteSize = 1234,
        int? faceIndex = null,
        int? width = null,
        int? height = null,
        decimal? rectX = null,
        decimal? rectY = null,
        decimal? rectW = null,
        decimal? rectH = null,
        bool? rectNormalized = null,
        decimal? faceScore = null) =>
        Db.Ado.ExecuteCommand(
            """
INSERT INTO FaceCaptureImage
    (ImageId, CaptureId, Kind, PartIndex, FaceIndex, Storage, ObjectKey, ContentType, ByteSize, Sha256, Width, Height,
     RectX, RectY, RectW, RectH, RectNormalized, FaceScore, CreatedAtUtc)
VALUES
    (@ImageId, @CaptureId, @Kind, @PartIndex, @FaceIndex, @Storage, @ObjectKey, @ContentType, @ByteSize, @Sha256, @Width, @Height,
     @RectX, @RectY, @RectW, @RectH, @RectNormalized, @FaceScore, @CreatedAtUtc)
""",
            new List<SugarParameter>
            {
                new("@ImageId", imageId),
                new("@CaptureId", captureId),
                new("@Kind", kind),
                new("@PartIndex", partIndex),
                new("@FaceIndex", (object?)faceIndex ?? DBNull.Value),
                new("@Storage", storage),
                new("@ObjectKey", objectKey),
                new("@ContentType", (object?)contentType ?? DBNull.Value),
                new("@ByteSize", byteSize),
                new("@Sha256", new string('0', 64)),
                new("@Width", (object?)width ?? DBNull.Value),
                new("@Height", (object?)height ?? DBNull.Value),
                new("@RectX", (object?)rectX ?? DBNull.Value),
                new("@RectY", (object?)rectY ?? DBNull.Value),
                new("@RectW", (object?)rectW ?? DBNull.Value),
                new("@RectH", (object?)rectH ?? DBNull.Value),
                new("@RectNormalized", rectNormalized.HasValue ? (rectNormalized.Value ? 1 : 0) : DBNull.Value),
                new("@FaceScore", (object?)faceScore ?? DBNull.Value),
                new("@CreatedAtUtc", Now),
            });

    /// <summary>标准数据：cam-1 有 capA（人脸图 + 背景图 + 一张本地图）、cam-2 有 capB。返回各编号方便断言。</summary>
    public SeededIds SeedStandard()
    {
        var ids = new SeededIds();
        AddCamera("cam-1");
        AddCamera("cam-2");
        AddEvent(ids.CaptureA, "cam-1", Now.AddMinutes(-1));
        AddEvent(ids.CaptureB, "cam-2", Now.AddMinutes(-2));
        AddImage(ids.FaceImage, ids.CaptureA, FaceImageTestData.Key("cam-1", ids.CaptureA + "_1"),
            kind: 0, partIndex: 1, faceIndex: 0, width: 200, height: 240, rectX: 0.25m, rectY: 0.1m, rectW: 0.2m, rectH: 0.3m,
            rectNormalized: true, faceScore: 0.9876m, byteSize: 4321);
        AddImage(ids.BackgroundImage, ids.CaptureA, FaceImageTestData.Key("cam-1", ids.CaptureA + "_0"),
            kind: 1, partIndex: 0, width: 1920, height: 1080, byteSize: 99999);
        AddImage(ids.LocalImage, ids.CaptureA, "images/2026-10-13/cam-1/" + ids.CaptureA + "_2.jpg",
            kind: 2, partIndex: 2, storage: "local");
        AddImage(ids.OtherCameraImage, ids.CaptureB, FaceImageTestData.Key("cam-2", ids.CaptureB + "_0"),
            kind: 1, partIndex: 0, width: 640, height: 480);
        return ids;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    internal sealed class SeededIds
    {
        public string CaptureA { get; } = FaceImageTestData.Hex32('a');
        public string CaptureB { get; } = FaceImageTestData.Hex32('b');
        public string FaceImage { get; } = FaceImageTestData.Hex32('1');
        public string BackgroundImage { get; } = FaceImageTestData.Hex32('2');
        public string LocalImage { get; } = FaceImageTestData.Hex32('3');
        public string OtherCameraImage { get; } = FaceImageTestData.Hex32('4');
    }
}

/// <summary>抓拍列表里的图片元数据：权限开关、批量查询、不带对象键。</summary>
public sealed class FaceCaptureImageListTests : IDisposable
{
    private readonly FaceImageTestDatabase _database = new();
    private readonly FaceCameraAdminTests.MutableTimeProvider _time = new(FaceImageTestDatabase.Now);
    private readonly AuditCapturingLoggerProvider _logs = new();

    private FaceCameraAdminService CreateService() =>
        new(
            FaceCameraAdminTests.CreateSqlSugarContext(_database.Db),
            _time,
            new ConfigurationBuilder().Build(),
            _logs.CreateLogger<FaceCameraAdminService>());

    [Fact]
    public async Task 没有查看图片权限_images为空数组_canViewImages为false_且不查图片表()
    {
        var ids = _database.SeedStandard();
        var statements = new List<string>();
        _database.Db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);

        var page = await CreateService().ListCapturesAsync("cam-1", 1, 20);
        _database.Db.Aop.OnLogExecuting = null;

        Assert.False(page.CanViewImages);
        var item = Assert.Single(page.Items);
        Assert.Equal(ids.CaptureA, item.CaptureId);
        Assert.Empty(item.Images);
        // 连图片表都不查：无权限者既拿不到数据，也不会有额外查询。
        Assert.DoesNotContain(statements, sql => sql.Contains("FaceCaptureImage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 有查看图片权限_返回该页每条抓拍的图片元数据_按序号排列且字段齐全()
    {
        var ids = _database.SeedStandard();

        var page = await CreateService().ListCapturesAsync("cam-1", 1, 20, includeImages: true);

        Assert.True(page.CanViewImages);
        var item = Assert.Single(page.Items);
        Assert.Equal([ids.BackgroundImage, ids.FaceImage, ids.LocalImage], item.Images.Select(image => image.ImageId));

        var face = item.Images[1];
        Assert.Equal(0, face.Kind);
        Assert.Equal("cos", face.Storage);
        Assert.Equal(200, face.Width);
        Assert.Equal(240, face.Height);
        Assert.Equal(4321, face.ByteSize);
        Assert.Equal(0, face.FaceIndex);
        Assert.Equal(new FaceCaptureImageRectDto(0.25m, 0.1m, 0.2m, 0.3m), face.Rect);
        Assert.True(face.RectNormalized);
        Assert.Equal(0.9876m, face.FaceScore);

        var background = item.Images[0];
        Assert.Equal(1, background.Kind);
        Assert.Null(background.Rect);
        Assert.Null(background.RectNormalized);
        Assert.Null(background.FaceScore);
        Assert.Equal(1920, background.Width);
        Assert.Equal("local", item.Images[2].Storage);
    }

    [Fact]
    public async Task 只返回本抓拍自己的图片_别的摄像机的图片不会混入()
    {
        _database.SeedStandard();

        var cam1 = await CreateService().ListCapturesAsync("cam-1", 1, 20, includeImages: true);
        var cam2 = await CreateService().ListCapturesAsync("cam-2", 1, 20, includeImages: true);

        Assert.Equal(3, cam1.Items.Single().Images.Count);
        Assert.Equal([FaceImageTestData.Hex32('4')], cam2.Items.Single().Images.Select(image => image.ImageId));
    }

    [Fact]
    public async Task 图片元数据整页只查一次_不随抓拍条数增加_且查询里没有对象键列()
    {
        _database.AddCamera("cam-1");
        for (var i = 0; i < 30; i++)
        {
            var captureId = i.ToString("x").PadLeft(32, 'e');
            _database.AddEvent(captureId, "cam-1", FaceImageTestDatabase.Now.AddMinutes(-i));
            _database.AddImage(i.ToString("x").PadLeft(32, 'f'), captureId, FaceImageTestData.Key("cam-1", captureId));
        }

        var statements = new List<string>();
        _database.Db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);
        var page = await CreateService().ListCapturesAsync("cam-1", 1, 50, includeImages: true);
        _database.Db.Aop.OnLogExecuting = null;

        Assert.Equal(30, page.Items.Count);
        Assert.All(page.Items, item => Assert.Single(item.Images));
        var imageQueries = statements.Where(sql => sql.Contains("FaceCaptureImage", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(imageQueries);
        // 列表查询根本不取对象键 / 哈希 / 属性 JSON。
        Assert.DoesNotContain("ObjectKey", imageQueries[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sha256", imageQueries[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AttributesJson", imageQueries[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 空页不发图片查询_没有图片的抓拍images为空数组()
    {
        _database.AddCamera("cam-1");
        _database.AddEvent(FaceImageTestData.Hex32('a'), "cam-1", FaceImageTestDatabase.Now);
        var statements = new List<string>();
        _database.Db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);

        var withEvent = await CreateService().ListCapturesAsync("cam-1", 1, 20, includeImages: true);
        statements.Clear();
        _database.AddCamera("cam-2");
        var empty = await CreateService().ListCapturesAsync("cam-2", 1, 20, includeImages: true);
        _database.Db.Aop.OnLogExecuting = null;

        Assert.Empty(withEvent.Items.Single().Images);
        Assert.Empty(empty.Items);
        Assert.True(empty.CanViewImages);
        Assert.DoesNotContain(statements, sql => sql.Contains("FaceCaptureImage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 序列化后的响应里没有对象键_地址_哈希字段()
    {
        _database.SeedStandard();
        var page = await CreateService().ListCapturesAsync("cam-1", 1, 20, includeImages: true);

        var json = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var word in new[] { "objectKey", "face-ingest", "myqcloud", "https://", "sha256", "url", "secret" })
        {
            Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 响应契约字段名_不含对象键_地址_哈希_原始报文()
    {
        var forbidden = new[] { "object", "url", "key", "path", "sha", "body", "raw", "base64", "bucket", "cos" };
        var names = new[]
            {
                typeof(FaceCaptureItemDto), typeof(FaceCapturePageDto), typeof(FaceCaptureImageMetaDto), typeof(FaceCaptureImageRectDto),
            }
            .SelectMany(type => type.GetProperties().Select(property => property.Name))
            .Concat(typeof(FaceCaptureEvent).GetProperties().Select(property => property.Name))
            .ToList();

        Assert.All(names, name => Assert.DoesNotContain(forbidden, word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Images", typeof(FaceCaptureItemDto).GetProperties().Select(property => property.Name));
    }

    public void Dispose() => _database.Dispose();
}

/// <summary>图片内容服务：权限、归属校验、存储位置、类型与大小、对象键来源、审计。</summary>
public sealed class FaceCaptureImageServiceTests : IDisposable
{
    private static readonly FaceCameraActor Actor = new("user-viewer-1", "viewer.tester");

    private readonly FaceImageTestDatabase _database = new();
    private readonly FakeFaceImageStore _store = new();
    private readonly FaceCameraAdminTests.MutableTimeProvider _time = new(FaceImageTestDatabase.Now);
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly FaceImageTestDatabase.SeededIds _ids;

    public FaceCaptureImageServiceTests()
    {
        _ids = _database.SeedStandard();
    }

    private FaceCaptureImageService CreateService() =>
        new(FaceCameraAdminTests.CreateSqlSugarContext(_database.Db), _store, _time, _logs.CreateLogger<FaceCaptureImageService>());

    private Task<FaceImageContent> Read(string cameraId, string captureId, string imageId, bool permitted = true) =>
        CreateService().GetContentAsync(cameraId, captureId, imageId, Actor, permitted);

    [Fact]
    public async Task 成功读取_对象键来自数据库_返回白名单类型_并记Information审计()
    {
        var content = await Read("cam-1", _ids.CaptureA, _ids.FaceImage);

        Assert.Equal(FaceImageTestData.Jpeg, content.Bytes);
        Assert.Equal("image/jpeg", content.ContentType);
        // 读取用的对象键就是库里这一行的值，而不是任何从请求里拼出来的东西。
        Assert.Equal([FaceImageTestData.Key("cam-1", _ids.CaptureA + "_1")], _store.Keys);
        Assert.Equal(FaceCaptureImageService.MaxImageBytes, _store.MaxBytesSeen);

        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Equal("ok", audit.State["Result"]);
        Assert.Equal("user-viewer-1", audit.State["ActorId"]);
        Assert.Equal("viewer.tester", audit.State["ActorName"]);
        Assert.Equal("cam-1", audit.State["CameraId"]);
        Assert.Equal(_ids.CaptureA, audit.State["CaptureId"]);
        Assert.Equal(_ids.FaceImage, audit.State["ImageId"]);
        Assert.Equal(FaceImageTestData.Jpeg.Length, audit.State["Bytes"]);
        // 时间取注入的时钟（UTC），与机器时区无关。
        Assert.Equal(FaceImageTestDatabase.Now, (DateTime)audit.State["AtUtc"]!);
    }

    [Fact]
    public async Task 审计日志不含对象键_地址_哈希_密钥()
    {
        await Read("cam-1", _ids.CaptureA, _ids.FaceImage);
        await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureB, _ids.FaceImage));
        await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.LocalImage));

        var everything = _logs.Dump();
        foreach (var forbidden in new[] { "face-ingest/images", "images/2026", ".jpg", "myqcloud", "https://", new string('0', 64), "secret" })
        {
            Assert.DoesNotContain(forbidden, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 没有查看图片权限_403_不查库_不碰存储_记Warning审计()
    {
        var statements = new List<string>();
        _database.Db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.FaceImage, permitted: false));
        _database.Db.Aop.OnLogExecuting = null;

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_FORBIDDEN", ex.Code);
        Assert.Empty(statements);
        Assert.Empty(_store.Keys);
        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal(LogLevel.Warning, audit.Level);
        Assert.Equal("FACE_IMAGE_FORBIDDEN", audit.State["Result"]);
        Assert.Equal(0, audit.State["Bytes"]);
    }

    [Fact]
    public async Task 归属校验_imageId属于别的capture_别的camera_不存在_全部同一个404且不碰存储()
    {
        var attempts = new[]
        {
            ("cam-1", _ids.CaptureB, _ids.FaceImage),             // 图片属于 capA，却声称在 capB 下
            ("cam-1", _ids.CaptureA, _ids.OtherCameraImage),      // 图片属于 cam-2 的 capB
            ("cam-2", _ids.CaptureA, _ids.FaceImage),             // capA 属于 cam-1，却声称在 cam-2 下
            ("cam-2", _ids.CaptureB, _ids.FaceImage),             // 三者两两错配
            ("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('9')), // 图片不存在
            ("cam-1", FaceImageTestData.Hex32('9'), _ids.FaceImage), // 抓拍不存在
            ("no-such-cam", _ids.CaptureA, _ids.FaceImage),       // 摄像机不存在
        };

        var errors = new List<FaceCameraException>();
        foreach (var (camera, capture, image) in attempts)
        {
            errors.Add(await Assert.ThrowsAsync<FaceCameraException>(() => Read(camera, capture, image)));
        }

        Assert.All(errors, ex =>
        {
            Assert.Equal(404, ex.StatusCode);
            Assert.Equal("FACE_IMAGE_NOT_FOUND", ex.Code);
        });
        // 消息逐字相同：看不出是「编号错了」还是「根本不存在」。
        Assert.Single(errors.Select(ex => ex.Message).Distinct());
        Assert.Empty(_store.Keys);
        Assert.Equal(attempts.Length, _logs.Entries.Count(entry => entry.State.TryGetValue("Result", out var r) && (string?)r == "FACE_IMAGE_NOT_FOUND"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e%2f")]
    [InlineData("1111111111111111111111111111111'")]
    [InlineData("' OR 1=1 --")]
    [InlineData("x; DROP TABLE FaceCaptureImage")]
    [InlineData("11111111111111111111111111111111x")]
    [InlineData("11111111111111111111111111111111\n")]
    public async Task 恶意或格式不对的编号_在查库之前就被拒绝_不能改变任何读取请求(string bad)
    {
        var statements = new List<string>();
        _database.Db.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);

        var asImage = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, bad));
        var asCapture = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", bad, _ids.FaceImage));
        _database.Db.Aop.OnLogExecuting = null;
        // 摄像机编号的格式比较宽（小写字母数字横线），个别输入本身是合法的摄像机编号，会走到查库然后查无此项；这里只要求同样的 404。
        var asCamera = await Assert.ThrowsAsync<FaceCameraException>(() => Read(bad, _ids.CaptureA, _ids.FaceImage));

        Assert.All(new[] { asImage, asCapture, asCamera }, ex => Assert.Equal("FACE_IMAGE_NOT_FOUND", ex.Code));
        Assert.Empty(statements);
        Assert.Empty(_store.Keys);
        // 表没被动过。
        Assert.Equal(4, await _database.Db.Ado.GetIntAsync("SELECT COUNT(*) FROM FaceCaptureImage"));
    }

    [Fact]
    public async Task 库里记录在本地磁盘_409且错误码为NOT_IN_COS_不碰存储()
    {
        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.LocalImage));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_NOT_IN_COS", ex.Code);
        Assert.Contains("本地", ex.Message);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Storage大小写和空白不影响判定_只有cos才会去读存储()
    {
        _database.AddImage(FaceImageTestData.Hex32('5'), _ids.CaptureA, FaceImageTestData.Key("cam-1", "x_5"), storage: " COS ", partIndex: 5);
        _database.AddImage(FaceImageTestData.Hex32('6'), _ids.CaptureA, FaceImageTestData.Key("cam-1", "x_6"), storage: "s3", partIndex: 6);

        var ok = await Read("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('5'));
        var other = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('6')));

        Assert.Equal("image/jpeg", ok.ContentType);
        Assert.Equal("FACE_IMAGE_NOT_IN_COS", other.Code);
    }

    [Fact]
    public async Task 存储返回PNG且库里声明png_按png返回()
    {
        _database.AddImage(FaceImageTestData.Hex32('7'), _ids.CaptureA, FaceImageTestData.Key("cam-1", "p_7"), partIndex: 7, contentType: "image/png");
        _store.Respond = _ => FaceImageTestData.Png;

        var content = await Read("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('7'));

        Assert.Equal("image/png", content.ContentType);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("image/gif")]
    [InlineData("image/jpg")]
    [InlineData("<script>")]
    public async Task 库里ContentType不在白名单_415_不读存储(string declared)
    {
        _database.AddImage(FaceImageTestData.Hex32('8'), _ids.CaptureA, FaceImageTestData.Key("cam-1", "t_8"), partIndex: 8, contentType: declared);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('8')));

        Assert.Equal(415, ex.StatusCode);
        Assert.Equal("FACE_IMAGE_TYPE_UNSUPPORTED", ex.Code);
        Assert.Empty(_store.Keys);
    }

    [Theory]
    [InlineData("image/jpeg; charset=binary", "image/jpeg")]
    [InlineData("IMAGE/JPEG", "image/jpeg")]
    [InlineData(null, "image/jpeg")]
    [InlineData("", "image/jpeg")]
    [InlineData("image/png", "image/png")]
    public void ContentType规范化_忽略参数与大小写_为空按jpeg(string? raw, string expected)
    {
        Assert.Equal(expected, FaceCaptureImageService.NormalizeContentType(raw));
    }

    [Fact]
    public async Task 字节头与声明类型不符_415()
    {
        // 库里声明 jpeg，存储里实际是 PNG / HTML：既不按声明回给浏览器，也不按字节猜。
        _store.Respond = _ => FaceImageTestData.Png;
        var asPng = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.FaceImage));
        _store.Respond = _ => "<html><script>alert(1)</script></html>"u8.ToArray();
        var asHtml = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.FaceImage));

        Assert.All(new[] { asPng, asHtml }, ex => Assert.Equal("FACE_IMAGE_TYPE_UNSUPPORTED", ex.Code));
    }

    [Fact]
    public async Task 库里声明的大小超过5MB_直接拒绝_不读存储()
    {
        _database.AddImage(FaceImageTestData.Hex32('9'), _ids.CaptureA, FaceImageTestData.Key("cam-1", "big_9"), partIndex: 9,
            byteSize: FaceCaptureImageService.MaxImageBytes + 1);

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, FaceImageTestData.Hex32('9')));

        Assert.Equal("FACE_IMAGE_TOO_LARGE", ex.Code);
        Assert.Empty(_store.Keys);
    }

    [Theory]
    [InlineData(503, "FACE_IMAGE_STORE_NOT_CONFIGURED")]
    [InlineData(404, "FACE_IMAGE_OBJECT_MISSING")]
    [InlineData(502, "FACE_IMAGE_STORE_DENIED")]
    [InlineData(502, "FACE_IMAGE_STORE_TIMEOUT")]
    [InlineData(502, "FACE_IMAGE_STORE_ERROR")]
    [InlineData(502, "FACE_IMAGE_TOO_LARGE")]
    public async Task 存储失败_状态码和错误码原样透传_并记Warning审计(int status, string code)
    {
        _store.Failure = new FaceCameraException(status, code, "固定文案");

        var ex = await Assert.ThrowsAsync<FaceCameraException>(() => Read("cam-1", _ids.CaptureA, _ids.FaceImage));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(code, ex.Code);
        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal(LogLevel.Warning, audit.Level);
        Assert.Equal(code, audit.State["Result"]);
    }

    [Fact]
    public async Task 审计里操作人名称的控制字符被清掉_过长的编号被截断()
    {
        var evil = new FaceCameraActor("id\r\n1", "evil\nname");
        var longId = new string('a', 500) + "\r\nFORGED";

        await Assert.ThrowsAsync<FaceCameraException>(() =>
            CreateService().GetContentAsync("cam-1", _ids.CaptureA, longId, evil, true));

        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal("id1", audit.State["ActorId"]);
        Assert.Equal("evilname", audit.State["ActorName"]);
        Assert.Equal(64, ((string)audit.State["ImageId"]!).Length);
        Assert.DoesNotContain('\n', (string)audit.State["ImageId"]!);
    }

    [Fact]
    public async Task 取消读取不会被当成业务失败记审计()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = new FaceCaptureImageService(
            FaceCameraAdminTests.CreateSqlSugarContext(_database.Db), new CancelingStore(), _time, _logs.CreateLogger<FaceCaptureImageService>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetContentAsync("cam-1", _ids.CaptureA, _ids.FaceImage, Actor, true, cts.Token));

        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
    }

    private sealed class CancelingStore : IFaceImageStore
    {
        public Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<byte[]>(cancellationToken);
    }

    public void Dispose() => _database.Dispose();
}

/// <summary>真实 MVC + 授权管道下的图片接口：权限分离、响应头、错误信封、列表里的 canViewImages。</summary>
public sealed class FaceCaptureImagePipelineTests : IDisposable
{
    private const string Manager = "user-manage-only";
    private const string Viewer = "user-manage-and-images";
    private const string ImagesOnly = "user-images-only";
    private const string Nobody = "user-nothing";

    private readonly FaceImageTestDatabase _database = new();
    private readonly FakeFaceImageStore _store = new();
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly FaceImageTestDatabase.SeededIds _ids;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public FaceCaptureImagePipelineTests()
    {
        _ids = _database.SeedStandard();

        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(service => service.UserHasPermissionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string user, string permission) => ApiResponse<bool>.OK(
                (user, permission) switch
                {
                    (Manager, Permissions.FaceCapture.Cameras.Manage) => true,
                    (Viewer, Permissions.FaceCapture.Cameras.Manage) => true,
                    (Viewer, Permissions.FaceCapture.Images.View) => true,
                    (ImagesOnly, Permissions.FaceCapture.Images.View) => true,
                    _ => false,
                }));

        var time = new FaceCameraAdminTests.MutableTimeProvider(FaceImageTestDatabase.Now);
        var context = FaceCameraAdminTests.CreateSqlSugarContext(_database.Db);
        var admin = new FaceCameraAdminService(context, time, new ConfigurationBuilder().Build(), _logs.CreateLogger<FaceCameraAdminService>());
        var images = new FaceCaptureImageService(context, _store, time, _logs.CreateLogger<FaceCaptureImageService>());

        _server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddLogging(builder => builder.AddProvider(_logs));
                    services.AddMemoryCache();
                    services.AddOptions<AppUpdatePolicyOptions>();
                    services.AddSingleton(roleService.Object);
                    services.AddSingleton<IFaceCameraAdminService>(admin);
                    services.AddSingleton<IFaceCaptureImageService>(images);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
                    services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddControllers().AddApplicationPart(typeof(FaceCamerasController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
        _client = _server.CreateClient();
    }

    private string ContentUrl(string camera, string capture, string image) =>
        $"/api/react/v1/face-cameras/{camera}/captures/{capture}/images/{image}/content";

    private async Task<HttpResponseMessage> Get(string url, string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user != null) request.Headers.Add("X-Test-User", user);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task 未登录401_没有任何相关权限403_只有查看图片权限但没有管理权限也是403()
    {
        var url = ContentUrl("cam-1", _ids.CaptureA, _ids.FaceImage);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(url, Nobody)).StatusCode);
        // 整个控制器的入口仍是管理摄像机权限：单独的查看图片权限进不来。
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(url, ImagesOnly)).StatusCode);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task 只有管理权限没有查看图片权限_内容接口403带信封_不碰存储_记审计()
    {
        var response = await Get(ContentUrl("cam-1", _ids.CaptureA, _ids.FaceImage), Manager);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
        var json = await Json(response);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("FACE_IMAGE_FORBIDDEN", json.GetProperty("errorCode").GetString());
        Assert.Empty(_store.Keys);
        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal("FACE_IMAGE_FORBIDDEN", audit.State["Result"]);
        Assert.Equal(Manager, audit.State["ActorId"]);
    }

    [Fact]
    public async Task 管理权限者的抓拍列表_不含images_canViewImages为false_其余元数据照常()
    {
        var response = await Get("/api/react/v1/face-cameras/cam-1/captures", Manager);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await Json(response)).GetProperty("data");
        Assert.False(data.GetProperty("canViewImages").GetBoolean());
        var item = data.GetProperty("items")[0];
        Assert.Equal(_ids.CaptureA, item.GetProperty("captureId").GetString());
        Assert.Equal(0, item.GetProperty("images").GetArrayLength());
        Assert.Equal(1, item.GetProperty("faceCount").GetInt32());
    }

    [Fact]
    public async Task 同时有两个权限者的抓拍列表_带images元数据_且不含对象键和地址()
    {
        var response = await Get("/api/react/v1/face-cameras/cam-1/captures", Viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.True(data.GetProperty("canViewImages").GetBoolean());
        var images = data.GetProperty("items")[0].GetProperty("images");
        Assert.Equal(3, images.GetArrayLength());
        var face = images.EnumerateArray().Single(image => image.GetProperty("imageId").GetString() == _ids.FaceImage);
        Assert.Equal("cos", face.GetProperty("storage").GetString());
        Assert.Equal(0.25m, face.GetProperty("rect").GetProperty("x").GetDecimal());
        Assert.True(face.GetProperty("rectNormalized").GetBoolean());
        foreach (var word in new[] { "objectKey", "face-ingest", "myqcloud", "https://", "sha256" })
        {
            Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 有权限者读取内容_200且响应头严格_只有图片字节()
    {
        var response = await Get(ContentUrl("cam-1", _ids.CaptureA, _ids.FaceImage), Viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FaceImageTestData.Jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", string.Join(",", response.Headers.GetValues("Pragma")));
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Null(response.Content.Headers.ContentDisposition?.FileName);
        // 不透传任何与存储有关的头，也不给浏览器做条件请求 / 范围请求的依据。
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers).Select(header => header.Key),
            name => name.StartsWith("x-cos", StringComparison.OrdinalIgnoreCase) || name.Equals("ETag", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("bytes", response.Headers.AcceptRanges);
        Assert.Equal([FaceImageTestData.Key("cam-1", _ids.CaptureA + "_1")], _store.Keys);

        var audit = Assert.Single(_logs.Entries, entry => entry.Message.Contains("人脸抓拍图片查看审计"));
        Assert.Equal(Viewer, audit.State["ActorId"]);
        Assert.Equal(Viewer + ".name", audit.State["ActorName"]);
        Assert.Equal("ok", audit.State["Result"]);
    }

    [Fact]
    public async Task 业务错误用统一信封返回_状态码与错误码对应_消息不含存储信息()
    {
        var cases = new (string Camera, string Capture, string Image, HttpStatusCode Status, string Code)[]
        {
            ("cam-1", _ids.CaptureA, _ids.OtherCameraImage, HttpStatusCode.NotFound, "FACE_IMAGE_NOT_FOUND"),
            ("cam-2", _ids.CaptureA, _ids.FaceImage, HttpStatusCode.NotFound, "FACE_IMAGE_NOT_FOUND"),
            ("cam-1", _ids.CaptureA, _ids.LocalImage, HttpStatusCode.Conflict, "FACE_IMAGE_NOT_IN_COS"),
        };
        foreach (var (camera, capture, image, status, code) in cases)
        {
            var response = await Get(ContentUrl(camera, capture, image), Viewer);
            Assert.Equal(status, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
            var json = await Json(response);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal(code, json.GetProperty("errorCode").GetString());
        }

        foreach (var (status, code) in new[]
                 {
                     (503, "FACE_IMAGE_STORE_NOT_CONFIGURED"), (502, "FACE_IMAGE_STORE_DENIED"), (502, "FACE_IMAGE_STORE_TIMEOUT"),
                     (404, "FACE_IMAGE_OBJECT_MISSING"), (502, "FACE_IMAGE_STORE_ERROR"), (415, "FACE_IMAGE_TYPE_UNSUPPORTED"),
                 })
        {
            _store.Failure = FaceImageErrorFor(code);
            var response = await Get(ContentUrl("cam-1", _ids.CaptureA, _ids.FaceImage), Viewer);
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
            foreach (var word in new[] { "myqcloud", "face-ingest", "https://", "bucket", "secret", "q-sign" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static FaceCameraException FaceImageErrorFor(string code) => code switch
    {
        "FACE_IMAGE_STORE_NOT_CONFIGURED" => FaceImageErrors.NotConfigured(),
        "FACE_IMAGE_STORE_DENIED" => FaceImageErrors.StoreDenied(),
        "FACE_IMAGE_STORE_TIMEOUT" => FaceImageErrors.StoreTimeout(),
        "FACE_IMAGE_OBJECT_MISSING" => FaceImageErrors.ObjectMissing(),
        "FACE_IMAGE_STORE_ERROR" => FaceImageErrors.StoreError(),
        "FACE_IMAGE_TYPE_UNSUPPORTED" => FaceImageErrors.Unsupported(),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    [Theory]
    [InlineData("..%2F..%2Fsecret", "11111111111111111111111111111111")]
    [InlineData("%2e%2e", "11111111111111111111111111111111")]
    [InlineData("11111111111111111111111111111111", "..%2F..%2Fsecret")]
    [InlineData("11111111111111111111111111111111", "x%0D%0AX-Forged:%201")]
    [InlineData("11111111111111111111111111111111", "%27%20OR%201%3D1%20--")]
    [InlineData("11111111111111111111111111111111", "face-ingest%2Fimages%2Fx.jpg")]
    public async Task 构造恶意编号_无论路由还是服务层都拒绝_存储永远不会被请求(string capture, string image)
    {
        var response = await Get($"/api/react/v1/face-cameras/cam-1/captures/{capture}/images/{image}/content", Viewer);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"实际状态 {(int)response.StatusCode}");
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task 内容接口只接受GET()
    {
        var url = ContentUrl("cam-1", _ids.CaptureA, _ids.FaceImage);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Test-User", Viewer);
            var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        }

        Assert.Empty(_store.Keys);
    }

    [Fact]
    public void 控制器元数据_类上仍是管理权限_内容接口是GET且无匿名_动作数为6()
    {
        var type = typeof(FaceCamerasController);

        var authorize = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal(Permissions.FaceCapture.Cameras.Manage, authorize.Policy);
        Assert.Single(type.GetCustomAttributes<FaceCameraNoStoreAttribute>(inherit: false));
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var action = type.GetMethod(nameof(FaceCamerasController.ImageContent))!;
        var http = Assert.Single(action.GetCustomAttributes<HttpMethodAttribute>(inherit: false));
        Assert.Equal(["GET"], http.HttpMethods);
        Assert.Equal("{cameraId}/captures/{captureId}/images/{imageId}/content", http.Template);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false));
        // 内容接口的权限在动作内通过授权服务判定（无权限尝试也要进审计），不是动作上的 [Authorize]。
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        var actions = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .ToList();
        Assert.Equal(6, actions.Count);
        Assert.All(actions, method =>
            Assert.DoesNotContain("DELETE", method.GetCustomAttributes<HttpMethodAttribute>().SelectMany(attribute => attribute.HttpMethods)));
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _database.Dispose();
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim("userId", user.ToString()),
                    new Claim(ClaimTypes.Name, user + ".name"),
                ],
                "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

/// <summary>新权限码 FaceCapture.Images.View 的完整登记：常量、种子（与角色模板脱钩）、入库 SQL、与管理权限分离。</summary>
public sealed class FaceCaptureImagesPermissionRegistrationTests
{
    private const string Code = "FaceCapture.Images.View";

    [Fact]
    public void 权限常量与种子一致_归入系统管理_不挂任何角色模板_与管理权限分开()
    {
        Assert.Equal(Code, Permissions.FaceCapture.Images.View);
        Assert.NotEqual(Permissions.FaceCapture.Cameras.Manage, Permissions.FaceCapture.Images.View);
        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == Code);
        Assert.Equal("查看人脸抓拍图片", seed.Name);
        Assert.Equal("系统管理", seed.Category);
        Assert.Contains("/system/face-cameras", seed.Description);
        Assert.Contains("敏感", seed.Description);
        // 只给管理员：Admin 角色靠角色放行，不写任何角色模板（含店长 / 经理）；管理摄像机的模板同样不包含它。
        Assert.All(PermissionSeedData.RolePermissionTemplates, template => Assert.DoesNotContain(Code, template.PermissionCodes));
        Assert.DoesNotContain("PosTerminal", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void 权限码没有别名关系_管理摄像机权限不会等价于查看图片权限()
    {
        Assert.DoesNotContain(Permissions.FaceCapture.Images.View, Permissions.GetEquivalentPermissionCodes(Permissions.FaceCapture.Cameras.Manage));
        Assert.DoesNotContain(Permissions.FaceCapture.Cameras.Manage, Permissions.GetEquivalentPermissionCodes(Permissions.FaceCapture.Images.View));
    }

    [Fact]
    public void 权限入库脚本_幂等_只登记这一个权限_不授予任何角色或用户_文案与种子一致()
    {
        var script = File.ReadAllText(ResolveMigrationPath());

        Assert.Contains("N'FaceCapture.Images.View'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("N'FaceCapture.Cameras.Manage'", script, StringComparison.Ordinal);
        Assert.Contains("N'系统管理'", script, StringComparison.Ordinal);
        Assert.Contains("@Actor nvarchar(100) = N'Migration_20261013_FaceCaptureImagesView'", script, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> N'HBweb'", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", script, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", script, StringComparison.Ordinal);
        Assert.Contains("SYSUTCDATETIME()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GETDATE()", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"INSERT INTO dbo\.HbwebSysPermissions\b"));
        Assert.DoesNotContain("HBwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", script, StringComparison.OrdinalIgnoreCase);
        var seed = PermissionSeedData.AllPermissions.Single(item => item.Code == Code);
        Assert.Contains($"N'{seed.Name}'", script, StringComparison.Ordinal);
        Assert.Contains($"N'{seed.Description}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 单独拥有查看图片权限_看不到摄像机管理菜单_管理权限仍可见()
    {
        var navigation = new NavigationService();

        var imagesOnly = navigation.BuildMenu(CreateUser(new Claim("permission", Code)));
        var manage = navigation.BuildMenu(CreateUser(new Claim("permission", Permissions.FaceCapture.Cameras.Manage)));

        Assert.DoesNotContain(imagesOnly.SelectMany(item => item.Children ?? []), item => item.Path == "/system/face-cameras");
        Assert.Contains(manage.SelectMany(item => item.Children ?? []), item => item.Path == "/system/face-cameras");
    }

    private static ClaimsPrincipal CreateUser(params Claim[] claims) =>
        new(new ClaimsIdentity(claims.Append(new Claim(ClaimTypes.NameIdentifier, "u-1")), "TestAuth"));

    private static string ResolveMigrationPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "BlazorApp.Api", "Data", "Migrations",
            "20261013_AddFaceCaptureImagesViewPermission.sql"));
}
