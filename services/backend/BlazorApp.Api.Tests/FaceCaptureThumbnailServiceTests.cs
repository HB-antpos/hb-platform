using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 缩略图批量接口：ok / none / gone / denied 四种状态、最长边 128 的 JPEG 且不保留元数据、整批 1–30 项、并发读取上限、
/// 单项失败不拖垮整批、<b>一次请求恰好一条审计且不含编号 / 对象键</b>、审计写失败时不返回任何图片。
/// 缩图用真实图片字节（ImageSharp 现场生成）；存储用内存替身，不连任何真实服务。
/// </summary>
public sealed class FaceCaptureThumbnailServiceTests : IDisposable
{
    private static readonly FaceMatchActor Admin = new("adm-1", "Admin", IsAdmin: true);
    private static readonly FaceMatchActor QldManager = new("qld-1", "Qld Manager");

    private const string HitKey = "face-match/hit/1013/2026/10/14/hit-object-secret.jpg";
    private const string CaptureKey = "face-ingest/images/1013/2026/10/14/cam-a/capture-object-secret.jpg";

    private static readonly DateTime T0 = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    private readonly FaceMatchTestDatabase _db = new();
    private readonly FaceCameraAdminTests.MutableTimeProvider _time = new(FaceMatchTestDatabase.Now);
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly FakeFaceImageStore _captureStore = new();
    private readonly FakeFaceMatchObjectStore _hitStore = new();
    private readonly FaceThumbnailService _service;

    public FaceCaptureThumbnailServiceTests()
    {
        _db.AddUser(QldManager.Id, QldManager.Name, "s-1013");
        var audit = new FaceMatchAudit(_db.Context, _time, _logs.CreateLogger<FaceMatchAudit>());
        _service = new FaceThumbnailService(_db.Context, _captureStore, _hitStore, audit, _logs.CreateLogger<FaceThumbnailService>());
    }

    public void Dispose() => _db.Dispose();

    // ---------- ok：缩成最长边 128 的 JPEG、不带元数据 ----------

    [Fact]
    public async Task 抓拍人脸小图_缩成最长边128的JPEG_宽高比不变_不放大小图_不带元数据()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("img1", "cap1", CaptureKey);
        _captureStore.Respond = _ => MakeJpeg(640, 320, withExif: true);

        var response = await _service.GetThumbnailsAsync(Request(("capture", "cap1")), Admin);

        var item = Assert.Single(response.Items);
        Assert.Equal(("capture", "cap1", "ok"), (item.Kind, item.Id, item.Status));
        var bytes = Convert.FromBase64String(item.ImageBase64!);
        Assert.True(FaceCaptureImageService.MatchesMagicBytes(bytes, "image/jpeg"));
        var info = Image.Identify(bytes);
        Assert.Equal((128, 64), (info.Width, info.Height));                      // 640x320 → 128x64，比例不变
        Assert.True(bytes.Length < 20 * 1024, $"缩略图应只有几 KB，实际 {bytes.Length}");
        // 元数据全部去掉：EXIF（设备 / GPS）、ICC、IPTC、XMP 一个都不留。
        Assert.True(info.Metadata.ExifProfile is null or { Values.Count: 0 });
        Assert.Null(info.Metadata.IccProfile);
        Assert.Null(info.Metadata.IptcProfile);
        Assert.Null(info.Metadata.XmpProfile);
        // 编码质量约 70：同一张图按 95 质量编码要明显更大。
        Assert.True(bytes.Length < EncodeAt(bytes, 95).Length);

        // 源图比 128 还小：不放大。
        _captureStore.Respond = _ => MakeJpeg(60, 40);
        var small = await _service.GetThumbnailsAsync(Request(("capture", "cap1")), Admin);
        var smallInfo = Image.Identify(Convert.FromBase64String(small.Items[0].ImageBase64!));
        Assert.Equal((60, 40), (smallInfo.Width, smallInfo.Height));
    }

    [Fact]
    public async Task 命中图_PNG源图也转成JPEG缩略图_按任务编号读取命中图键()
    {
        AddHitTask('a', "1013", HitKey);
        _hitStore.Objects[HitKey] = MakePng(300, 600);

        var response = await _service.GetThumbnailsAsync(Request(("hit", Id('a'))), Admin);

        var item = Assert.Single(response.Items);
        Assert.Equal("ok", item.Status);
        var bytes = Convert.FromBase64String(item.ImageBase64!);
        Assert.True(FaceCaptureImageService.MatchesMagicBytes(bytes, "image/jpeg"));
        Assert.Equal((64, 128), (Image.Identify(bytes).Width, Image.Identify(bytes).Height));
        Assert.Equal([HitKey], _hitStore.Reads);
    }

    [Fact]
    public async Task 按EXIF方向转正后再缩小_输出不再带方向信息()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("img1", "cap1", CaptureKey);
        _captureStore.Respond = _ => MakeJpeg(200, 100, orientation: 6);       // 方向 6 = 顺时针转 90°，转正后是 100x200

        var response = await _service.GetThumbnailsAsync(Request(("capture", "cap1")), Admin);

        var info = Image.Identify(Convert.FromBase64String(response.Items[0].ImageBase64!));
        Assert.Equal((64, 128), (info.Width, info.Height));
        Assert.True(info.Metadata.ExifProfile is null or { Values.Count: 0 });
    }

    [Fact]
    public async Task 抓拍有多张人脸小图时取序号最小的一张_背景大图不参与()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("img-bg", "cap1", "face-ingest/images/1013/2026/10/14/cam-a/bg.jpg", kind: 1, partIndex: 0);
        AddFaceImage("img-f2", "cap1", "face-ingest/images/1013/2026/10/14/cam-a/f2.jpg", partIndex: 2);
        AddFaceImage("img-f1", "cap1", "face-ingest/images/1013/2026/10/14/cam-a/f1.jpg", partIndex: 1);
        _captureStore.Respond = _ => MakeJpeg(100, 100);

        var response = await _service.GetThumbnailsAsync(Request(("capture", "cap1")), Admin);

        Assert.Equal("ok", response.Items[0].Status);
        Assert.Equal(["face-ingest/images/1013/2026/10/14/cam-a/f1.jpg"], _captureStore.Keys);
    }

    // ---------- none / gone / denied ----------

    [Fact]
    public async Task none_没有人脸小图_图片还在接收端本地_任务没有命中图键()
    {
        AddCapture("onlybg", "1013");
        AddFaceImage("img-bg", "onlybg", CaptureKey, kind: 1);                         // 只有背景图
        AddCapture("noimg", "1013");                                                    // 一张图都没有
        AddCapture("local", "1013");
        AddFaceImage("img-local", "local", CaptureKey, storage: "local");              // 还在接收服务本地磁盘
        AddHitTask('a', "1013", string.Empty);                                          // 命中图键为空

        var response = await _service.GetThumbnailsAsync(
            Request(("capture", "onlybg"), ("capture", "noimg"), ("capture", "local"), ("hit", Id('a'))), Admin);

        Assert.All(response.Items, i => { Assert.Equal("none", i.Status); Assert.Null(i.ImageBase64); });
        Assert.Empty(_captureStore.Keys);                                               // none 不去读存储
        Assert.Empty(_hitStore.Reads);
    }

    [Fact]
    public async Task gone_对象已被清理_存储失败_内容不合法_超大声明_键不合法_都只让该项gone()
    {
        AddCapture("missing", "1013");
        AddFaceImage("i-missing", "missing", "face-ingest/images/1013/2026/10/14/cam-a/missing.jpg");
        AddCapture("notimage", "1013");
        AddFaceImage("i-notimage", "notimage", "face-ingest/images/1013/2026/10/14/cam-a/notimage.jpg");
        AddCapture("badtype", "1013");
        AddFaceImage("i-badtype", "badtype", "face-ingest/images/1013/2026/10/14/cam-a/badtype.jpg", contentType: "image/gif");
        AddCapture("huge", "1013");
        AddFaceImage("i-huge", "huge", "face-ingest/images/1013/2026/10/14/cam-a/huge.jpg", byteSize: FaceCaptureImageService.MaxImageBytes + 1);
        AddCapture("undecodable", "1013");
        AddFaceImage("i-undecodable", "undecodable", "face-ingest/images/1013/2026/10/14/cam-a/undecodable.jpg");
        AddCapture("ok", "1013");
        AddFaceImage("i-ok", "ok", "face-ingest/images/1013/2026/10/14/cam-a/ok.jpg");
        _captureStore.Respond = key => key switch
        {
            var k when k.EndsWith("missing.jpg") => throw FaceImageErrors.ObjectMissing(),
            var k when k.EndsWith("notimage.jpg") => [0x00, 0x01, 0x02, 0x03],                     // 字节头不是 JPEG
            var k when k.EndsWith("undecodable.jpg") => FaceImageTestData.Jpeg,                   // 只有 JPEG 头、没有图像数据
            _ => MakeJpeg(50, 50),
        };
        // 命中图：键指向登记照目录（库里被改过）→ 不去读；另一个读不到。
        AddHitTask('a', "1013", "face-match/enrollment/aaaa/0.jpg");
        AddHitTask('b', "1013", "face-match/hit/1013/missing.jpg");

        var response = await _service.GetThumbnailsAsync(
            Request(("capture", "missing"), ("capture", "notimage"), ("capture", "badtype"), ("capture", "huge"),
                ("capture", "undecodable"), ("capture", "ok"), ("hit", Id('a')), ("hit", Id('b'))), Admin);

        Assert.Equal(
            ["gone", "gone", "gone", "gone", "gone", "ok", "gone", "gone"],
            response.Items.Select(i => i.Status));
        Assert.All(response.Items.Where(i => i.Status == "gone"), i => Assert.Null(i.ImageBase64));
        Assert.DoesNotContain(_captureStore.Keys, key => key.EndsWith("huge.jpg") || key.EndsWith("badtype.jpg"));   // 声明大小 / 类型不合法的不读
        Assert.DoesNotContain("face-match/enrollment/aaaa/0.jpg", _hitStore.Reads);                                 // 不能经命中图通道读登记照
    }

    [Fact]
    public async Task gone_源图尺寸超限不解码_防解压炸弹()
    {
        AddHitTask('a', "1013", HitKey);
        _hitStore.Objects[HitKey] = MakePng(9000, 1);                                  // 字节很小，但声明的边长超过 8192

        var response = await _service.GetThumbnailsAsync(Request(("hit", Id('a'))), Admin);

        Assert.Equal("gone", response.Items[0].Status);

        // 边长没超、总像素超过 1600 万（4097 x 4097）：同样不解码。
        _hitStore.Objects[HitKey] = MakePng(4097, 4097);
        var tooManyPixels = await _service.GetThumbnailsAsync(Request(("hit", Id('a'))), Admin);
        Assert.Equal("gone", tooManyPixels.Items[0].Status);

        // 4K 画面（3840 x 2160，约 830 万像素）在上限内，正常缩图。
        _hitStore.Objects[HitKey] = MakePng(3840, 2160);
        var fourK = await _service.GetThumbnailsAsync(Request(("hit", Id('a'))), Admin);
        Assert.Equal("ok", fourK.Items[0].Status);
        Assert.Equal((128, 72), (Image.Identify(Convert.FromBase64String(fourK.Items[0].ImageBase64!)).Width, Image.Identify(Convert.FromBase64String(fourK.Items[0].ImageBase64!)).Height));
    }

    [Fact]
    public async Task denied_编号格式不对_不存在_不在自己的门店范围_对外同一口径_且不读存储()
    {
        AddCapture("other", "2001");
        AddFaceImage("i-other", "other", CaptureKey);
        AddHitTask('a', "2001", HitKey);
        _hitStore.Objects[HitKey] = MakeJpeg(40, 40);

        var response = await _service.GetThumbnailsAsync(
            Request(("capture", "other"), ("capture", "nope"), ("capture", "bad;id"), ("capture", ""), ("capture", new string('a', 33)),
                ("hit", Id('a')), ("hit", Id('f')), ("hit", "SHORT"), ("hit", Id('A'))),
            QldManager);

        Assert.All(response.Items, i => { Assert.Equal("denied", i.Status); Assert.Null(i.ImageBase64); });
        Assert.Empty(_captureStore.Keys);
        Assert.Empty(_hitStore.Reads);
        // 越权与不存在返回完全一样的内容（只有请求自己带来的 kind / id 被回显），看不出「编号是否存在」。
        Assert.Equal(response.Items[0] with { Id = "x" }, response.Items[1] with { Id = "x" });
    }

    [Fact]
    public async Task 非管理员能读自己门店的抓拍与命中图_管理员不限门店()
    {
        AddCapture("mine", "1013");
        AddFaceImage("i-mine", "mine", CaptureKey);
        AddCapture("theirs", "2001");
        AddFaceImage("i-theirs", "theirs", "face-ingest/images/2001/2026/10/14/cam-b/x.jpg");
        _captureStore.Respond = _ => MakeJpeg(50, 50);

        var manager = await _service.GetThumbnailsAsync(Request(("capture", "mine"), ("capture", "theirs")), QldManager);
        var admin = await _service.GetThumbnailsAsync(Request(("capture", "mine"), ("capture", "theirs")), Admin);

        Assert.Equal(["ok", "denied"], manager.Items.Select(i => i.Status));
        Assert.Equal(["ok", "ok"], admin.Items.Select(i => i.Status));
    }

    // ---------- 请求校验 ----------

    [Fact]
    public async Task 整批1到30项_空请求_0项_31项_种类不认识都是400_30项正常()
    {
        Task<FaceMatchException> Fails(FaceThumbnailRequest? request) =>
            Assert.ThrowsAsync<FaceMatchException>(() => _service.GetThumbnailsAsync(request!, Admin));

        Assert.Equal(400, (await Fails(new FaceThumbnailRequest())).StatusCode);                              // Items 为空
        Assert.Equal(400, (await Fails(new FaceThumbnailRequest { Items = [] })).StatusCode);
        Assert.Equal(400, (await Fails(Request(Enumerable.Range(0, 31).Select(i => ("hit", Id('a'))).ToArray()))).StatusCode);
        var badKind = await Fails(Request(("hit", Id('a')), ("face", "x")));
        Assert.Equal(400, badKind.StatusCode);
        Assert.Equal("FACE_MATCH_FIELD_INVALID", badKind.Code);
        Assert.Equal(400, (await Fails(new FaceThumbnailRequest { Items = [new FaceThumbnailRequestItem { Id = Id('a') }] })).StatusCode);   // 缺 kind

        var thirty = await _service.GetThumbnailsAsync(Request(Enumerable.Range(0, 30).Select(i => ("hit", Id('a'))).ToArray()), Admin);
        Assert.Equal(30, thirty.Items.Count);
        // 校验失败的请求什么都没读、没有审计（没看任何图）。
        Assert.Single(await _db.Db.Queryable<FaceAuditLog>().ToListAsync());   // 只有上面这次 30 项的请求
    }

    [Fact]
    public async Task 返回与请求同序_重复的项只读取一次()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("i1", "cap1", CaptureKey);
        AddHitTask('a', "1013", HitKey);
        _captureStore.Respond = _ => MakeJpeg(50, 50);
        _hitStore.Objects[HitKey] = MakeJpeg(60, 60);

        var response = await _service.GetThumbnailsAsync(
            Request(("hit", Id('a')), ("capture", "cap1"), ("capture", "cap1"), ("capture", "zzz"), ("hit", Id('a'))), Admin);

        Assert.Equal(
            [("hit", Id('a'), "ok"), ("capture", "cap1", "ok"), ("capture", "cap1", "ok"), ("capture", "zzz", "denied"), ("hit", Id('a'), "ok")],
            response.Items.Select(i => (i.Kind, i.Id, i.Status)));
        Assert.Single(_captureStore.Keys);
        Assert.Single(_hitStore.Reads);
        Assert.Equal(response.Items[0].ImageBase64, response.Items[4].ImageBase64);
    }

    // ---------- 审计：一次请求恰好一条 ----------

    [Fact]
    public async Task 一次请求恰好一条审计_不逐张记_门店取同一家店_结果是成功张数_不含编号与对象键()
    {
        for (var i = 0; i < 5; i++)
        {
            AddCapture($"cap{i}", "1013");
            AddFaceImage($"img{i}", $"cap{i}", $"face-ingest/images/1013/2026/10/14/cam-a/{i}.jpg");
        }

        AddHitTask('a', "1013", HitKey);
        _captureStore.Respond = _ => MakeJpeg(50, 50);
        _hitStore.Objects[HitKey] = MakeJpeg(60, 60);

        await _service.GetThumbnailsAsync(
            Request(("capture", "cap0"), ("capture", "cap1"), ("capture", "cap2"), ("capture", "cap3"), ("capture", "cap4"), ("hit", Id('a')), ("capture", "missing")),
            QldManager);

        var rows = await _db.Db.Queryable<FaceAuditLog>().ToListAsync();
        var audit = Assert.Single(rows);                                                // 7 项请求只写 1 条
        Assert.Equal("capture.thumbnails.view", audit.Action);
        Assert.Equal(FaceMatchAuditActions.ThumbnailsView, audit.Action);
        Assert.Equal("1013", audit.StoreCode);                                          // 本批全部同一家店
        Assert.Null(audit.TargetRef);                                                   // 不放任何编号
        Assert.Equal("n=6", audit.Result);                                              // 6 张成功，denied 的不算
        Assert.Equal("qld-1", audit.ActorId);
        AssertNoSecrets(audit, rows);
    }

    [Fact]
    public async Task 审计门店_跨店为空_全部denied为空_全部none也记一条_n等于0()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("i1", "cap1", CaptureKey);
        AddCapture("cap2", "2001");
        AddFaceImage("i2", "cap2", "face-ingest/images/2001/2026/10/14/cam-b/x.jpg");
        AddCapture("none1", "1013");
        _captureStore.Respond = _ => MakeJpeg(50, 50);

        await _service.GetThumbnailsAsync(Request(("capture", "cap1"), ("capture", "cap2")), Admin);          // 跨两家店
        await _service.GetThumbnailsAsync(Request(("capture", "nope"), ("capture", "bad;id")), Admin);        // 全部 denied
        await _service.GetThumbnailsAsync(Request(("capture", "none1")), Admin);                              // 全部 none

        var rows = (await _db.Db.Queryable<FaceAuditLog>().ToListAsync()).OrderBy(r => r.AuditId).ToList();
        Assert.Equal(3, rows.Count);                                                    // 一次请求一条，哪怕一张都没成功
        Assert.Equal([null, null, "1013"], rows.Select(r => r.StoreCode));
        Assert.Equal(["n=2", "n=0", "n=0"], rows.Select(r => r.Result));
        Assert.All(rows, r => Assert.Null(r.TargetRef));
    }

    [Fact]
    public async Task 日志与审计里没有编号_对象键_图片字节_读取失败只按原因汇总计数()
    {
        AddCapture("secretcap", "1013");
        AddFaceImage("secretimg", "secretcap", CaptureKey);
        AddHitTask('a', "1013", HitKey);
        _captureStore.Respond = _ => throw FaceImageErrors.StoreTimeout();               // 存储暂时失败
        // 命中图不存在 → store NotFound

        var response = await _service.GetThumbnailsAsync(Request(("capture", "secretcap"), ("hit", Id('a'))), Admin);

        Assert.Equal(["gone", "gone"], response.Items.Select(i => i.Status));
        var dump = _logs.Dump();
        Assert.Contains("FACE_IMAGE_STORE_TIMEOUT=1", dump);                             // 有原因汇总
        Assert.Contains("store-NotFound=1", dump);
        foreach (var secret in new[] { "secretcap", "secretimg", CaptureKey, HitKey, Id('a'), "capture-object-secret", "hit-object-secret" })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
        }

        var rows = await _db.Db.Queryable<FaceAuditLog>().ToListAsync();
        AssertNoSecrets(Assert.Single(rows), rows);
    }

    [Fact]
    public async Task 审计写入失败_整个请求失败_不返回任何图片字节()
    {
        AddCapture("cap1", "1013");
        AddFaceImage("i1", "cap1", CaptureKey);
        _captureStore.Respond = _ => MakeJpeg(50, 50);
        var audit = new Mock<IFaceMatchAudit>(MockBehavior.Strict);
        audit.Setup(a => a.WriteAsync(It.IsAny<FaceMatchActor>(), FaceMatchAuditActions.ThumbnailsView, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaceMatchException(500, "FACE_MATCH_AUDIT_FAILED", "审计记录写入失败。"));
        var service = new FaceThumbnailService(_db.Context, _captureStore, _hitStore, audit.Object, _logs.CreateLogger<FaceThumbnailService>());

        var error = await Assert.ThrowsAsync<FaceMatchException>(() => service.GetThumbnailsAsync(Request(("capture", "cap1")), Admin));

        Assert.Equal("FACE_MATCH_AUDIT_FAILED", error.Code);
        audit.Verify(a => a.WriteAsync(It.IsAny<FaceMatchActor>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- 并发与单项失败 ----------

    [Fact]
    public async Task 并发读取存储最多4路_30项都能完成()
    {
        for (var i = 0; i < 30; i++)
        {
            AddCapture($"cap{i}", "1013");
            AddFaceImage($"img{i}", $"cap{i}", $"face-ingest/images/1013/2026/10/14/cam-a/{i}.jpg");
        }

        var slowStore = new ConcurrencyProbeStore(MakeJpeg(50, 50));
        var audit = new FaceMatchAudit(_db.Context, _time, _logs.CreateLogger<FaceMatchAudit>());
        var service = new FaceThumbnailService(_db.Context, slowStore, _hitStore, audit, _logs.CreateLogger<FaceThumbnailService>());

        var response = await service.GetThumbnailsAsync(Request(Enumerable.Range(0, 30).Select(i => ("capture", $"cap{i}")).ToArray()), Admin);

        Assert.Equal(30, response.Items.Count(i => i.Status == "ok"));
        Assert.Equal(30, slowStore.Calls);
        Assert.InRange(slowStore.MaxConcurrent, 2, FaceThumbnailService.MaxConcurrentReads);   // 确实并发过，但不超过上限
    }

    [Fact]
    public async Task 读取时带上图片大小上限_单项失败不影响其他项()
    {
        AddCapture("good", "1013");
        AddFaceImage("i-good", "good", CaptureKey);
        AddCapture("bad", "1013");
        AddFaceImage("i-bad", "bad", "face-ingest/images/1013/2026/10/14/cam-a/bad.jpg");
        _captureStore.Respond = key => key.EndsWith("bad.jpg") ? throw FaceImageErrors.StoreDenied() : MakeJpeg(50, 50);

        var response = await _service.GetThumbnailsAsync(Request(("capture", "bad"), ("capture", "good")), Admin);

        Assert.Equal(["gone", "ok"], response.Items.Select(i => i.Status));
        Assert.Equal(FaceCaptureImageService.MaxImageBytes, _captureStore.MaxBytesSeen);
    }

    // ---------- 缩图函数本身 ----------

    [Fact]
    public void MakeThumbnail_不是图片_截断的图片_返回null不抛异常()
    {
        Assert.Null(FaceThumbnailService.MakeThumbnail([1, 2, 3]));
        Assert.Null(FaceThumbnailService.MakeThumbnail(FaceImageTestData.Jpeg));
        Assert.Null(FaceThumbnailService.MakeThumbnail(MakeJpeg(64, 64)[..40]));
    }

    // ---------- 种子数据与工具 ----------

    private static string Id(char ch) => new(ch, 32);

    private static FaceThumbnailRequest Request(params (string Kind, string Id)[] items) =>
        new() { Items = items.Select(i => new FaceThumbnailRequestItem { Kind = i.Kind, Id = i.Id }).ToList() };

    private void AddCapture(string captureId, string store) =>
        _db.Db.Insertable(new FaceCaptureEvent
        {
            CaptureId = captureId,
            StoreCode = store,
            CameraId = "cam-a",
            ReceivedAtUtc = T0,
            FaceCount = 1,
            ParseStatus = 0,
        }).ExecuteCommand();

    private void AddFaceImage(
        string imageId, string captureId, string objectKey, byte kind = 0, int partIndex = 0,
        string storage = "cos", string contentType = "image/jpeg", int byteSize = 1024) =>
        _db.Db.Ado.ExecuteCommand(
            "INSERT INTO FaceCaptureImage (ImageId, CaptureId, Kind, PartIndex, Storage, ObjectKey, ContentType, ByteSize) VALUES (@i, @c, @k, @p, @s, @o, @t, @b)",
            new { i = imageId, c = captureId, k = kind, p = partIndex, s = storage, o = objectKey, t = contentType, b = byteSize });

    private void AddHitTask(char idChar, string store, string imageKey) =>
        _db.Db.Insertable(new FaceMatchTask
        {
            TaskId = Id(idChar),
            StoreCode = store,
            CameraId = "cam-a",
            CapturedAtUtc = T0,
            EntryId = Id('e'),
            Score = 90m,
            ImageObjectKey = imageKey,
            ReviewStatus = FaceMatchReviewStatus.Pending,
            ImageDeleteAtUtc = T0.AddDays(7),
        }).ExecuteCommand();

    private static void AssertNoSecrets(FaceAuditLog row, IReadOnlyList<FaceAuditLog> all)
    {
        var text = string.Join("|", all.Select(r => $"{r.ActorId}|{r.ActorName}|{r.Action}|{r.StoreCode}|{r.TargetRef}|{r.Result}"));
        foreach (var secret in new[] { "cap0", "cap1", "secret", "capture-object-secret", "hit-object-secret", "face-ingest", "face-match", Id('a'), "image" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(row.Result.Length <= 32);
    }

    /// <summary>一张带 EXIF（设备型号、方向）的渐变 JPEG；颜色有变化，避免被压成极小的纯色块。</summary>
    internal static byte[] MakeJpeg(int width, int height, bool withExif = false, ushort? orientation = null)
    {
        using var image = new Image<Rgb24>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)((x * 7 + y * 13) % 256));
            }
        }

        if (withExif || orientation != null)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.Model, "SECRET-DEVICE-MODEL");
            if (orientation != null)
            {
                exif.SetValue(ExifTag.Orientation, orientation.Value);
            }

            image.Metadata.ExifProfile = exif;
        }

        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoder { Quality = 90 });
        return output.ToArray();
    }

    private static byte[] MakePng(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(10, 120, 200));
        using var output = new MemoryStream();
        image.SaveAsPng(output, new PngEncoder());
        return output.ToArray();
    }

    private static byte[] EncodeAt(byte[] source, int quality)
    {
        using var image = Image.Load<Rgb24>(source);
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoder { Quality = quality });
        return output.ToArray();
    }

    /// <summary>记录同时在读的最大路数；每次读取故意拖一小会儿，让并发能叠起来。</summary>
    private sealed class ConcurrencyProbeStore(byte[] content) : IFaceImageStore
    {
        private int _current;
        private int _max;
        private int _calls;

        public int MaxConcurrent => Volatile.Read(ref _max);
        public int Calls => Volatile.Read(ref _calls);

        public async Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref _current);
            return content;
        }
    }
}
