using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「抓拍与比对结果」：只列已解析抓拍、评估判定与分数、门店范围、判定与命中过滤、
/// 名单命中与通知发送情况（sent / pending / skipped，只有人数）、图片编号只给有看图权限的人、
/// 图片读取先校验门店范围再交给受保护读取。全部用 SQLite 临时库与替身，不连任何真实服务。
/// </summary>
public sealed class FaceCaptureResultServiceTests : IDisposable
{
    private static readonly FaceMatchActor Admin = new("adm-1", "Admin", IsAdmin: true);
    private static readonly FaceMatchActor QldManager = new("qld-1", "Qld Manager");

    private static readonly DateTime T0 = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    private readonly FaceMatchTestDatabase _db = new();
    private readonly Mock<IFaceCaptureImageService> _images = new(MockBehavior.Strict);
    private readonly FaceCaptureResultService _service;

    public FaceCaptureResultServiceTests()
    {
        _db.AddUser(QldManager.Id, QldManager.Name, "s-1013");
        _service = new FaceCaptureResultService(_db.Context, _images.Object, new FakeFaceMatchSettings());
    }

    public void Dispose() => _db.Dispose();

    // ---------- 列表 ----------

    [Fact]
    public async Task 只列已解析的抓拍_带评估判定与分数_没有评估行的标为未评估()
    {
        AddCapture("c1", "1013", "cam-a", T0.AddMinutes(1));
        AddEvaluation("c1", decision: 1, staff: 91.4m, visit: null);
        AddCapture("c2", "1013", "cam-a", T0.AddMinutes(2));                       // 没有评估行
        AddCapture("c3", "1013", "cam-a", T0.AddMinutes(3), parseStatus: 2);       // 未解析：不列出

        var page = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, canViewImages: false);

        Assert.Equal(["c2", "c1"], page.Items.Select(i => i.CaptureId));          // 抓拍时间倒序
        var evaluated = page.Items.Single(i => i.CaptureId == "c1");
        Assert.True(evaluated.Evaluated);
        Assert.Equal("staff", evaluated.Decision);
        Assert.Equal(91, evaluated.StaffScorePercent);
        Assert.Null(evaluated.VisitScorePercent);
        Assert.Equal("Orion", evaluated.StoreName);
        var notEvaluated = page.Items.Single(i => i.CaptureId == "c2");
        Assert.False(notEvaluated.Evaluated);
        Assert.Null(notEvaluated.Decision);
    }

    [Fact]
    public async Task 非管理员只看自己关联门店的抓拍()
    {
        AddCapture("q1", "1013", "cam-a", T0.AddMinutes(1));
        AddCapture("n1", "2001", "cam-b", T0.AddMinutes(2));

        var mine = await _service.ListAsync(new FaceCaptureResultQuery(), QldManager, canViewImages: false);
        var all = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, canViewImages: false);
        var forbiddenStore = await _service.ListAsync(new FaceCaptureResultQuery { StoreCode = "2001" }, QldManager, canViewImages: false);

        Assert.Equal(["q1"], mine.Items.Select(i => i.CaptureId));
        Assert.Equal(2, all.Items.Count);
        Assert.Empty(forbiddenStore.Items);                                         // 指定别人的门店也只会得到空列表
    }

    [Fact]
    public async Task 判定与时间过滤_未评估单独一类()
    {
        AddCapture("s1", "1013", "cam-a", T0.AddMinutes(1));
        AddEvaluation("s1", decision: 1, staff: 90m, visit: null);
        AddCapture("v1", "1013", "cam-a", T0.AddMinutes(2));
        AddEvaluation("v1", decision: 3, staff: 8m, visit: 20m);
        AddCapture("x1", "1013", "cam-a", T0.AddMinutes(3));

        var newVisitor = await _service.ListAsync(new FaceCaptureResultQuery { Decision = "newVisitor" }, Admin, false);
        var notEvaluated = await _service.ListAsync(new FaceCaptureResultQuery { Decision = "notEvaluated" }, Admin, false);
        var window = await _service.ListAsync(
            new FaceCaptureResultQuery { FromUtc = T0.AddMinutes(2), ToUtc = T0.AddMinutes(3) }, Admin, false);

        Assert.Equal(["v1"], newVisitor.Items.Select(i => i.CaptureId));
        Assert.Equal(["x1"], notEvaluated.Items.Select(i => i.CaptureId));
        Assert.Equal(["v1"], window.Items.Select(i => i.CaptureId));                // 起点含、终点不含
        await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.ListAsync(new FaceCaptureResultQuery { Decision = "nonsense" }, Admin, false));
    }

    [Fact]
    public async Task 分页多取一行判断是否还有下一页()
    {
        for (var i = 0; i < 3; i++)
        {
            AddCapture($"p{i}", "1013", "cam-a", T0.AddMinutes(i));
        }

        var first = await _service.ListAsync(new FaceCaptureResultQuery { Page = 1, PageSize = 2 }, Admin, false);
        var second = await _service.ListAsync(new FaceCaptureResultQuery { Page = 2, PageSize = 2 }, Admin, false);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
    }

    // ---------- 命中与通知 ----------

    [Fact]
    public async Task 命中按摄像机加接收时间对应_通知状态分已发送_排队中_已跳过_只给人数()
    {
        AddCapture("h1", "1013", "cam-a", T0.AddMinutes(1));
        AddTask("task-sent", "1013", "cam-a", T0.AddMinutes(1), score: 92.4m, notifiedAt: T0.AddMinutes(2));
        AddNotification("task-sent", recipients: 3, read: 1, createdAt: T0.AddMinutes(2));
        AddCapture("h2", "1013", "cam-a", T0.AddMinutes(5));
        AddTask("task-pending", "1013", "cam-a", T0.AddMinutes(5), score: 64m, notifiedAt: null);
        AddCapture("h3", "1013", "cam-a", T0.AddMinutes(9));
        AddTask("task-skipped", "1013", "cam-a", T0.AddMinutes(9), score: 71m, notifiedAt: T0.AddMinutes(10));
        AddCapture("plain", "1013", "cam-a", T0.AddMinutes(12));
        AddCapture("other-cam", "1013", "cam-b", T0.AddMinutes(1));                 // 同一时间但摄像机不同：不对应

        var page = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, false);

        var sent = page.Items.Single(i => i.CaptureId == "h1").Hit!;
        Assert.Equal("task-sent", sent.TaskId);
        Assert.Equal(92, sent.ScorePercent);
        Assert.Equal("high", sent.Band);                                            // 默认档位线 90 及以上
        Assert.Equal("normal", page.Items.Single(i => i.CaptureId == "h2").Hit!.Band);
        Assert.Equal(FaceHitNotifyStates.Sent, sent.NotifyState);
        Assert.Equal(3, sent.NotifyRecipientCount);
        Assert.Equal(1, sent.NotifyReadCount);
        Assert.Equal(T0.AddMinutes(2), sent.NotifiedAtUtc);
        Assert.Equal(FaceHitNotifyStates.Pending, page.Items.Single(i => i.CaptureId == "h2").Hit!.NotifyState);
        Assert.Equal(FaceHitNotifyStates.Skipped, page.Items.Single(i => i.CaptureId == "h3").Hit!.NotifyState);
        Assert.Null(page.Items.Single(i => i.CaptureId == "plain").Hit);
        Assert.Null(page.Items.Single(i => i.CaptureId == "other-cam").Hit);
    }

    [Fact]
    public async Task 只看命中_只返回有命中任务的抓拍()
    {
        AddCapture("h1", "1013", "cam-a", T0.AddMinutes(1));
        AddTask("t1", "1013", "cam-a", T0.AddMinutes(1), score: 80m, notifiedAt: null);
        AddCapture("plain", "1013", "cam-a", T0.AddMinutes(2));

        var page = await _service.ListAsync(new FaceCaptureResultQuery { HitOnly = true }, Admin, false);

        Assert.Equal(["h1"], page.Items.Select(i => i.CaptureId));
    }

    [Fact]
    public async Task 同一抓拍多张脸命中_只展示相似度最高的一条()
    {
        AddCapture("m1", "1013", "cam-a", T0.AddMinutes(1), faceCount: 2);
        AddTask("t-low", "1013", "cam-a", T0.AddMinutes(1), score: 60m, notifiedAt: null);
        AddTask("t-high", "1013", "cam-a", T0.AddMinutes(1), score: 95m, notifiedAt: null);

        var page = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, false);

        Assert.Equal("t-high", page.Items.Single().Hit!.TaskId);
    }

    // ---------- 图片编号 ----------

    [Fact]
    public async Task 图片编号只在有看图权限时返回_人脸小图与背景图各一张()
    {
        AddCapture("i1", "1013", "cam-a", T0.AddMinutes(1));
        AddImage("img-bg", "i1", kind: 1, partIndex: 1);
        AddImage("img-face", "i1", kind: 0, partIndex: 0);

        var withPermission = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, canViewImages: true);
        var without = await _service.ListAsync(new FaceCaptureResultQuery(), Admin, canViewImages: false);

        Assert.Equal("img-face", withPermission.Items.Single().FaceImageId);
        Assert.Equal("img-bg", withPermission.Items.Single().SceneImageId);
        Assert.Null(without.Items.Single().FaceImageId);
        Assert.Null(without.Items.Single().SceneImageId);
    }

    // ---------- 图片读取 ----------

    [Fact]
    public async Task 读图_没有看图权限直接403_不查库不碰存储()
    {
        var error = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.GetImageAsync("c1", "i1", Admin, canViewImages: false));

        Assert.Equal(403, error.StatusCode);
        _images.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("c1", "i1;DROP")]
    [InlineData("", "i1")]
    [InlineData("c1", "")]
    public async Task 读图_编号格式不对一律404(string captureId, string imageId)
    {
        var error = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.GetImageAsync(captureId, imageId, Admin, canViewImages: true));

        Assert.Equal(404, error.StatusCode);
        _images.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 读图_不在自己门店范围或抓拍不存在_同一个404()
    {
        AddCapture("other", "2001", "cam-b", T0);

        var outOfScope = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.GetImageAsync("other", "i1", QldManager, canViewImages: true));
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.GetImageAsync("nope", "i1", QldManager, canViewImages: true));

        Assert.Equal(404, outOfScope.StatusCode);
        Assert.Equal(outOfScope.Code, missing.Code);
        Assert.Equal(outOfScope.Message, missing.Message);
        _images.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 读图_范围内交给受保护读取并带上摄像机编号与操作人()
    {
        AddCapture("c1", "1013", "cam-a", T0);
        _images.Setup(s => s.GetContentAsync("cam-a", "c1", "i1", It.Is<FaceCameraActor>(a => a.Id == "qld-1"), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceImageContent(FaceImageTestData.Jpeg, "image/jpeg"));

        var content = await _service.GetImageAsync("c1", "i1", QldManager, canViewImages: true);

        Assert.Equal("image/jpeg", content.ContentType);
        Assert.Equal(FaceImageTestData.Jpeg, content.Bytes);
        _images.VerifyAll();
    }

    [Fact]
    public async Task 读图_受保护读取的业务错误转成比对模块的统一错误()
    {
        AddCapture("c1", "1013", "cam-a", T0);
        _images.Setup(s => s.GetContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<FaceCameraActor>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaceCameraException(404, "FACE_IMAGE_NOT_FOUND", "图片不存在。"));

        var error = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.GetImageAsync("c1", "i1", Admin, canViewImages: true));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("FACE_IMAGE_NOT_FOUND", error.Code);
    }

    // ---------- 种子数据 ----------

    private void AddCapture(string id, string store, string camera, DateTime receivedAt, byte parseStatus = 0, int faceCount = 1) =>
        _db.Db.Insertable(new FaceCaptureEvent
        {
            CaptureId = id,
            StoreCode = store,
            CameraId = camera,
            ReceivedAtUtc = receivedAt,
            FaceCount = faceCount,
            ParseStatus = parseStatus,
        }).ExecuteCommand();

    private void AddEvaluation(string captureId, byte decision, decimal? staff, decimal? visit) =>
        _db.Db.Insertable(new FaceMatchEvaluation { CaptureId = captureId, Decision = decision, StaffScore = staff, VisitScore = visit }).ExecuteCommand();

    private void AddTask(string taskId, string store, string camera, DateTime capturedAt, decimal score, DateTime? notifiedAt) =>
        _db.Db.Insertable(new FaceMatchTask
        {
            TaskId = taskId,
            StoreCode = store,
            CameraId = camera,
            CapturedAtUtc = capturedAt,
            EntryId = "entry-1",
            Score = score,
            ImageObjectKey = "face-match/hit/x.jpg",
            ReviewStatus = FaceMatchReviewStatus.Pending,
            ImageDeleteAtUtc = capturedAt.AddDays(7),
            NotifiedAtUtc = notifiedAt,
        }).ExecuteCommand();

    /// <summary>与 <see cref="FaceHitNotifier"/> 发布时一致：DedupeKey = 前缀 + TaskId，每个收件人一行，前 read 个已读。</summary>
    private void AddNotification(string taskId, int recipients, int read, DateTime createdAt)
    {
        var notificationId = $"n-{taskId}";
        _db.Db.Insertable(new AppNotification
        {
            NotificationId = notificationId,
            Type = AppNotificationTypes.FaceUnwelcomeHit,
            Title = "Possible match alert",
            Body = "Possible match",
            DedupeKey = FaceHitNotifier.DedupeKeyPrefix + taskId,
            CreatedAtUtc = createdAt,
        }).ExecuteCommand();
        for (var i = 0; i < recipients; i++)
        {
            _db.Db.Insertable(new AppNotificationRecipient
            {
                NotificationId = notificationId,
                UserGUID = $"user-{i}",
                CreatedAtUtc = createdAt,
                ReadAtUtc = i < read ? createdAt.AddMinutes(1) : null,
            }).ExecuteCommand();
        }
    }

    private void AddImage(string imageId, string captureId, byte kind, int partIndex) =>
        _db.Db.Ado.ExecuteCommand(
            "INSERT INTO FaceCaptureImage (ImageId, CaptureId, Kind, PartIndex) VALUES (@i, @c, @k, @p)",
            new { i = imageId, c = captureId, k = kind, p = partIndex });
}
