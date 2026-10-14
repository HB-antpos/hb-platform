using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「命中流」：以命中任务为主表（Match 模式不留存抓拍也有行）、各 hitStatus 的筛选、门店范围、
/// 「未读」只看当前登录人自己的命中通知（与收件箱红色数字同一口径）、汇总数字、标已读（幂等、只改本人的行）。
/// 全部用 SQLite 临时库，不连任何真实服务。
/// </summary>
public sealed class FaceMatchHitResultServiceTests : IDisposable
{
    private static readonly FaceMatchActor Admin = new("adm-1", "Admin", IsAdmin: true);
    private static readonly FaceMatchActor QldManager = new("qld-1", "Qld Manager");
    private static readonly FaceMatchActor QldPeer = new("qld-2", "Qld Peer");

    private static readonly DateTime T0 = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    private readonly FaceMatchTestDatabase _db = new();
    private readonly FaceCameraAdminTests.MutableTimeProvider _time = new(FaceMatchTestDatabase.Now);
    private readonly FaceHitResultService _service;
    private readonly NotificationInboxService _inbox;

    public FaceMatchHitResultServiceTests()
    {
        _db.AddUser(QldManager.Id, QldManager.Name, "s-1013");
        _db.AddUser(QldPeer.Id, QldPeer.Name, "s-1013");
        _inbox = new NotificationInboxService(_db.Context, _time);
        _service = new FaceHitResultService(_db.Context, new FakeFaceMatchSettings(), _inbox, _time);
    }

    public void Dispose() => _db.Dispose();

    // ---------- 主表是任务 ----------

    [Fact]
    public async Task 命中流以任务为主表_Match模式没有留存抓拍也有行_抓拍与评估字段为空()
    {
        AddTask('a', "1013", "cam-a", T0.AddMinutes(1), score: 92.4m);            // 没有任何留存抓拍

        var page = await _service.ListAsync(new FaceHitResultQuery(), Admin, canReview: false);

        var hit = Assert.Single(page.Items);
        Assert.Equal(Id('a'), hit.TaskId);
        Assert.Null(hit.CaptureId);
        Assert.Null(hit.Decision);
        Assert.Null(hit.StaffScorePercent);
        Assert.Null(hit.VisitScorePercent);
        Assert.Null(hit.FaceCount);
        Assert.Equal(92, hit.ScorePercent);
        Assert.Equal("high", hit.Band);
        Assert.Equal("Orion", hit.StoreName);
        Assert.Equal("cam-a", hit.CameraId);
        Assert.Equal(T0.AddMinutes(1), hit.CapturedAtUtc);
        Assert.Equal(DateTimeKind.Utc, hit.CapturedAtUtc.Kind);
    }

    [Fact]
    public async Task 有留存抓拍时按同摄像机加同接收时间对应_带上抓拍编号_判定_分数_人脸数()
    {
        AddTask('a', "1013", "cam-a", T0.AddMinutes(1), score: 80m);
        AddCapture("capa", "1013", "cam-a", T0.AddMinutes(1), faceCount: 3);
        AddEvaluation("capa", decision: 3, staff: 8.4m, visit: 21.6m);
        AddTask('b', "1013", "cam-a", T0.AddMinutes(5), score: 70m);              // 同摄像机但时间不同：不对应
        AddCapture("capb", "1013", "cam-a", T0.AddMinutes(5).AddSeconds(1));
        AddTask('c', "1013", "cam-b", T0.AddMinutes(9), score: 60m);              // 同时间但摄像机不同：不对应
        AddCapture("capc", "1013", "cam-a", T0.AddMinutes(9));
        AddTask('d', "1013", "cam-a", T0.AddMinutes(12), score: 50m);             // 有抓拍没有评估：判定与分数为空、人脸数仍有
        AddCapture("capd", "1013", "cam-a", T0.AddMinutes(12), faceCount: 2);

        var page = await _service.ListAsync(new FaceHitResultQuery(), Admin, canReview: false);

        var a = page.Items.Single(i => i.TaskId == Id('a'));
        Assert.Equal("capa", a.CaptureId);
        Assert.Equal("newVisitor", a.Decision);
        Assert.Equal(8, a.StaffScorePercent);
        Assert.Equal(22, a.VisitScorePercent);
        Assert.Equal(3, a.FaceCount);
        Assert.Null(page.Items.Single(i => i.TaskId == Id('b')).CaptureId);
        Assert.Null(page.Items.Single(i => i.TaskId == Id('c')).CaptureId);
        var d = page.Items.Single(i => i.TaskId == Id('d'));
        Assert.Equal("capd", d.CaptureId);
        Assert.Null(d.Decision);
        Assert.Equal(2, d.FaceCount);
    }

    [Fact]
    public async Task 同一时刻有重复报文时优先对应不是重复的那条抓拍()
    {
        AddTask('a', "1013", "cam-a", T0.AddMinutes(1), score: 80m);
        AddCapture("aaa-dup", "1013", "cam-a", T0.AddMinutes(1), duplicateOf: "capmain");
        AddCapture("zzz-main", "1013", "cam-a", T0.AddMinutes(1));

        var page = await _service.ListAsync(new FaceHitResultQuery(), Admin, false);

        Assert.Equal("zzz-main", page.Items.Single().CaptureId);
    }

    [Fact]
    public async Task 未解析的抓拍不对应()
    {
        AddTask('a', "1013", "cam-a", T0.AddMinutes(1), score: 80m);
        AddCapture("bad", "1013", "cam-a", T0.AddMinutes(1), parseStatus: 2);

        var page = await _service.ListAsync(new FaceHitResultQuery(), Admin, false);

        Assert.Null(page.Items.Single().CaptureId);
    }

    // ---------- hitStatus ----------

    [Fact]
    public async Task 各hitStatus筛选_pending_autoConfirmed_confirmed是人工_dismissed_all()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 70m, review: FaceMatchReviewStatus.Pending);
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 99m, review: FaceMatchReviewStatus.Confirmed, reviewer: FaceMatchAuditActions.SystemActorId);
        AddTask('3', "1013", "cam-a", T0.AddMinutes(3), score: 85m, review: FaceMatchReviewStatus.Confirmed, reviewer: "qld-1");
        AddTask('4', "1013", "cam-a", T0.AddMinutes(4), score: 60m, review: FaceMatchReviewStatus.Dismissed, reviewer: "qld-1");
        AddTask('5', "1013", "cam-a", T0.AddMinutes(5), score: 88m, review: FaceMatchReviewStatus.Confirmed, reviewer: null);   // 旧数据：复核人为空，算人工确认

        async Task<List<string>> Ids(string? status) =>
            (await _service.ListAsync(new FaceHitResultQuery { HitStatus = status }, Admin, false)).Items.Select(i => i.TaskId).ToList();

        Assert.Equal([Id('5'), Id('4'), Id('3'), Id('2'), Id('1')], await Ids(null));          // 抓拍时间倒序
        Assert.Equal(await Ids(null), await Ids("all"));
        Assert.Equal([Id('1')], await Ids("pending"));
        Assert.Equal([Id('2')], await Ids("autoConfirmed"));
        Assert.Equal([Id('5'), Id('3')], await Ids("confirmed"));                              // 人工确认，不含自动确认
        Assert.Equal([Id('4')], await Ids("dismissed"));
    }

    [Fact]
    public async Task 自动确认标记只看已确认且复核人是system_待复核或已驳回的不算()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 99m, review: FaceMatchReviewStatus.Confirmed, reviewer: "system");
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 99m, review: FaceMatchReviewStatus.Dismissed, reviewer: "system");
        AddTask('3', "1013", "cam-a", T0.AddMinutes(3), score: 99m, review: FaceMatchReviewStatus.Pending);

        var page = await _service.ListAsync(new FaceHitResultQuery(), Admin, false);

        Assert.True(page.Items.Single(i => i.TaskId == Id('1')).AutoConfirmed);
        Assert.False(page.Items.Single(i => i.TaskId == Id('2')).AutoConfirmed);
        Assert.False(page.Items.Single(i => i.TaskId == Id('3')).AutoConfirmed);
        Assert.Equal("confirmed", page.Items.Single(i => i.TaskId == Id('1')).ReviewStatusName);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Unread")]            // 区分大小写：契约取值固定
    [InlineData("autoconfirmed")]
    public async Task hitStatus取值不对返回400(string status)
    {
        var error = await Assert.ThrowsAsync<FaceMatchException>(() =>
            _service.ListAsync(new FaceHitResultQuery { HitStatus = status }, Admin, false));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("FACE_MATCH_FIELD_INVALID", error.Code);
    }

    // ---------- 门店范围 ----------

    [Fact]
    public async Task 非管理员只看自己关联门店的命中_管理员不限_指定别人的门店只得空列表()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 70m);
        AddTask('2', "2001", "cam-b", T0.AddMinutes(2), score: 70m);

        var mine = await _service.ListAsync(new FaceHitResultQuery(), QldManager, false);
        var all = await _service.ListAsync(new FaceHitResultQuery(), Admin, false);
        var forbidden = await _service.ListAsync(new FaceHitResultQuery { StoreCode = "2001" }, QldManager, false);
        var onlyNsw = await _service.ListAsync(new FaceHitResultQuery { StoreCode = "2001" }, Admin, false);

        Assert.Equal([Id('1')], mine.Items.Select(i => i.TaskId));
        Assert.Equal(2, all.Items.Count);
        Assert.Empty(forbidden.Items);
        Assert.Equal([Id('2')], onlyNsw.Items.Select(i => i.TaskId));
    }

    [Fact]
    public async Task 没有任何关联门店的账号什么都看不到()
    {
        _db.AddUser("lonely", "Lonely");
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 70m);

        var page = await _service.ListAsync(new FaceHitResultQuery(), new FaceMatchActor("lonely", "Lonely"), false);
        var summary = await _service.GetSummaryAsync(new FaceHitResultQuery(), new FaceMatchActor("lonely", "Lonely"));

        Assert.Empty(page.Items);
        Assert.Equal(new FaceHitSummaryDto(), summary);
    }

    // ---------- 时间与分页 ----------

    [Fact]
    public async Task 时间条件左闭右开_分页多取一行判断是否还有下一页_每页上限50()
    {
        for (var i = 0; i < 3; i++)
        {
            AddTask((char)('1' + i), "1013", "cam-a", T0.AddMinutes(i), score: 70m);
        }

        var window = await _service.ListAsync(new FaceHitResultQuery { FromUtc = T0.AddMinutes(1), ToUtc = T0.AddMinutes(2) }, Admin, false);
        var first = await _service.ListAsync(new FaceHitResultQuery { Page = 1, PageSize = 2 }, Admin, false);
        var second = await _service.ListAsync(new FaceHitResultQuery { Page = 2, PageSize = 2 }, Admin, false);
        var capped = await _service.ListAsync(new FaceHitResultQuery { PageSize = 500 }, Admin, false);

        Assert.Equal([Id('2')], window.Items.Select(i => i.TaskId));                        // 起点含、终点不含
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Equal(FaceHitResultService.MaxPageSize, capped.PageSize);
    }

    [Fact]
    public async Task 带Z偏移的本地时间会先转回UTC_没有时区标记的按UTC理解()
    {
        AddTask('1', "1013", "cam-a", T0, score: 70m);

        // ASP.NET 绑定 "2026-10-14T01:00:00Z" 得到 Kind=Local 的时间；换算回 UTC 后恰好等于任务时间（含起点）。
        var local = T0.ToLocalTime();
        var viaLocal = await _service.ListAsync(new FaceHitResultQuery { FromUtc = local, ToUtc = local.AddSeconds(1) }, Admin, false);
        var unspecified = await _service.ListAsync(
            new FaceHitResultQuery { FromUtc = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified), ToUtc = T0.AddSeconds(1) }, Admin, false);

        Assert.Single(viaLocal.Items);
        Assert.Single(unspecified.Items);
    }

    // ---------- 未读：只看当前登录人 ----------

    [Fact]
    public async Task 未读只看当前登录人自己的通知行_别人读没读不影响我()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null), ("qld-2", T0.AddMinutes(2)));         // qld-1 没读，qld-2 已读
        AddTask('2', "1013", "cam-a", T0.AddMinutes(3), score: 90m);
        AddHitNotification(Id('2'), ("qld-1", T0.AddMinutes(4)), ("qld-2", null));         // 反过来

        var forManager = await _service.ListAsync(new FaceHitResultQuery { HitStatus = "unread" }, QldManager, false);
        var forPeer = await _service.ListAsync(new FaceHitResultQuery { HitStatus = "unread" }, QldPeer, false);
        var managerAll = await _service.ListAsync(new FaceHitResultQuery(), QldManager, false);

        Assert.Equal([Id('1')], forManager.Items.Select(i => i.TaskId));
        Assert.Equal([Id('2')], forPeer.Items.Select(i => i.TaskId));
        Assert.True(managerAll.Items.Single(i => i.TaskId == Id('1')).Unread);
        Assert.False(managerAll.Items.Single(i => i.TaskId == Id('2')).Unread);
        Assert.Equal(NotificationId(Id('1')), managerAll.Items.Single(i => i.TaskId == Id('1')).NotificationId);
        // 通知发送情况（给了几个人、几个人已读）不分是谁在看。
        var sent = managerAll.Items.Single(i => i.TaskId == Id('1'));
        Assert.Equal(FaceHitNotifyStates.Sent, sent.NotifyState);
        Assert.Equal(2, sent.NotifyRecipientCount);
        Assert.Equal(1, sent.NotifyReadCount);
    }

    [Fact]
    public async Task 没有通知行的任务_unread为false_notificationId为空_管理员没收到通知也一样()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);                      // 通知还没发
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 90m);
        AddHitNotification(Id('2'), ("qld-2", null));                                      // 只发给了别人

        var forManager = await _service.ListAsync(new FaceHitResultQuery(), QldManager, false);
        var forAdmin = await _service.ListAsync(new FaceHitResultQuery(), Admin, false);
        var unread = await _service.ListAsync(new FaceHitResultQuery { HitStatus = "unread" }, QldManager, false);

        Assert.All(forManager.Items, i => { Assert.False(i.Unread); Assert.Null(i.NotificationId); });
        Assert.All(forAdmin.Items, i => { Assert.False(i.Unread); Assert.Null(i.NotificationId); });
        Assert.Empty(unread.Items);
    }

    [Fact]
    public async Task 已撤回与已过期的通知不算未读_与收件箱未读数同一口径()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null));
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 90m);
        AddHitNotification(Id('2'), ("qld-1", null));
        RevokeNotification(Id('2'), T0);                                                    // 已撤回
        AddTask('3', "1013", "cam-a", T0.AddMinutes(3), score: 90m);
        AddHitNotification(Id('3'), ("qld-1", null));
        SetNotificationExpiry(Id('3'), FaceMatchTestDatabase.Now.AddMinutes(-1));           // 已过期
        AddTask('4', "1013", "cam-a", T0.AddMinutes(4), score: 90m);
        AddHitNotification(Id('4'), ("qld-1", null));
        SetNotificationExpiry(Id('4'), FaceMatchTestDatabase.Now.AddHours(1));              // 还没过期

        var unread = await _service.ListAsync(new FaceHitResultQuery { HitStatus = "unread" }, QldManager, false);
        var all = await _service.ListAsync(new FaceHitResultQuery(), QldManager, false);
        var summary = await _service.GetSummaryAsync(new FaceHitResultQuery(), QldManager);
        var badge = await _inbox.GetUnreadCountAsync(QldManager.Id, AppNotificationTypes.FaceUnwelcomeHit);

        Assert.Equal([Id('4'), Id('1')], unread.Items.Select(i => i.TaskId));
        Assert.Equal(["4", "1"], all.Items.Where(i => i.Unread).Select(i => i.TaskId[0].ToString()));
        // 红色数字（收件箱按类型的未读数）、汇总里的未读数、未读筛选的条数三者一致。
        Assert.Equal(2, badge);
        Assert.Equal(badge, summary.Unread);
        Assert.Equal(badge, unread.Items.Count);
        // 撤回 / 过期的通知行仍然给出通知编号（只是不算未读）。
        Assert.NotNull(all.Items.Single(i => i.TaskId == Id('2')).NotificationId);
    }

    [Fact]
    public async Task 其他类型通知的未读不计入命中未读数()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null));
        // 另一类通知的未读不影响命中未读数。
        _db.Db.Insertable(new AppNotification
        {
            NotificationId = "n-price", Type = "price.changed", Title = "t", Body = "b", CreatedAtUtc = T0, CreatedBy = "test",
        }).ExecuteCommand();
        _db.Db.Insertable(new AppNotificationRecipient { NotificationId = "n-price", UserGUID = "qld-1", CreatedAtUtc = T0 }).ExecuteCommand();

        var summary = await _service.GetSummaryAsync(new FaceHitResultQuery(), QldManager);

        Assert.Equal(1, summary.Unread);
        Assert.Equal(1, await _inbox.GetUnreadCountAsync(QldManager.Id, AppNotificationTypes.FaceUnwelcomeHit));
        Assert.Equal(2, await _inbox.GetUnreadCountAsync(QldManager.Id));
    }

    // ---------- 复核入口 ----------

    [Theory]
    [InlineData(true, FaceMatchReviewStatus.Pending, true, true)]
    [InlineData(false, FaceMatchReviewStatus.Pending, true, false)]            // 没有 Review 权限
    [InlineData(true, FaceMatchReviewStatus.Confirmed, true, false)]           // 已结案
    [InlineData(true, FaceMatchReviewStatus.Dismissed, true, false)]
    [InlineData(true, FaceMatchReviewStatus.Pending, false, false)]            // 命中图已被清理
    public async Task canReview_需要Review权限_任务待复核_命中图还在(bool hasReview, byte status, bool imageKept, bool expected)
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m, review: status, imageKey: imageKept ? "face-match/hit/x.jpg" : string.Empty);

        var page = await _service.ListAsync(new FaceHitResultQuery(), QldManager, canReview: hasReview);

        var hit = Assert.Single(page.Items);
        Assert.Equal(expected, hit.CanReview);
        Assert.Equal(imageKept, hit.ImageAvailable);
    }

    // ---------- 汇总 ----------

    [Fact]
    public async Task 汇总数字_total等于四个状态之和_unread与之正交_门店与时间条件同列表()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 70m, review: FaceMatchReviewStatus.Pending);
        AddHitNotification(Id('1'), ("qld-1", null));
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 99m, review: FaceMatchReviewStatus.Confirmed, reviewer: "system");
        AddHitNotification(Id('2'), ("qld-1", null));
        AddTask('3', "1013", "cam-a", T0.AddMinutes(3), score: 99m, review: FaceMatchReviewStatus.Confirmed, reviewer: "system");
        AddTask('4', "1013", "cam-a", T0.AddMinutes(4), score: 85m, review: FaceMatchReviewStatus.Confirmed, reviewer: "qld-2");
        AddTask('5', "1013", "cam-a", T0.AddMinutes(5), score: 60m, review: FaceMatchReviewStatus.Dismissed, reviewer: "qld-2");
        AddHitNotification(Id('5'), ("qld-1", T0.AddMinutes(6)));                          // 已读：不计未读
        AddTask('6', "2001", "cam-b", T0.AddMinutes(6), score: 70m, review: FaceMatchReviewStatus.Pending);   // 别的门店
        AddHitNotification(Id('6'), ("qld-1", null));                                       // 就算有我的未读通知，门店范围外也不计

        var summary = await _service.GetSummaryAsync(new FaceHitResultQuery(), QldManager);
        var admin = await _service.GetSummaryAsync(new FaceHitResultQuery(), Admin);
        var windowed = await _service.GetSummaryAsync(
            new FaceHitResultQuery { FromUtc = T0.AddMinutes(2), ToUtc = T0.AddMinutes(4), HitStatus = "dismissed", Page = 9 }, QldManager);

        Assert.Equal(new FaceHitSummaryDto { Total = 5, Unread = 2, Pending = 1, AutoConfirmed = 2, Confirmed = 1, Dismissed = 1 }, summary);
        Assert.Equal(summary.Pending + summary.AutoConfirmed + summary.Confirmed + summary.Dismissed, summary.Total);
        Assert.Equal(6, admin.Total);                                                       // 管理员不限门店
        Assert.Equal(new FaceHitSummaryDto { Total = 2, Unread = 1, Pending = 0, AutoConfirmed = 2, Confirmed = 0, Dismissed = 0 }, windowed);   // HitStatus 与分页被忽略
        // 汇总里的未读数与「未读」筛选的条数一致。
        var unreadPage = await _service.ListAsync(new FaceHitResultQuery { HitStatus = "unread" }, QldManager, false);
        Assert.Equal(unreadPage.Items.Count, summary.Unread);
    }

    // ---------- 标已读 ----------

    [Fact]
    public async Task 标已读_只改本人的未读行_别人的行不动_返回命中类型的剩余未读数()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null), ("qld-2", null));
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 90m);
        AddHitNotification(Id('2'), ("qld-1", null));
        // 同一个人另一类型的未读不计入返回值。
        _db.Db.Insertable(new AppNotification
        {
            NotificationId = "n-price", Type = "price.changed", Title = "t", Body = "b", CreatedAtUtc = T0, CreatedBy = "test",
        }).ExecuteCommand();
        _db.Db.Insertable(new AppNotificationRecipient { NotificationId = "n-price", UserGUID = "qld-1", CreatedAtUtc = T0 }).ExecuteCommand();

        var result = await _service.MarkReadAsync(Id('1'), QldManager);

        Assert.Equal(1, result.UnreadCount);                                                // 还剩任务 2 的命中通知
        Assert.NotNull(ReadAt(Id('1'), "qld-1"));
        Assert.Null(ReadAt(Id('1'), "qld-2"));                                              // 别人的行没动
        Assert.Null(ReadAt(Id('2'), "qld-1"));
        Assert.Null(await ReadAtOfOtherType("n-price", "qld-1"));                           // 其他类型没动
        // 列表里该任务不再未读、红色数字随之减少。
        var page = await _service.ListAsync(new FaceHitResultQuery(), QldManager, false);
        Assert.False(page.Items.Single(i => i.TaskId == Id('1')).Unread);
        Assert.Equal(1, await _inbox.GetUnreadCountAsync(QldManager.Id, AppNotificationTypes.FaceUnwelcomeHit));
    }

    [Fact]
    public async Task 标已读_幂等_重复标记不刷新已读时间()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null));
        _time.Now = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
        await _service.MarkReadAsync(Id('1'), QldManager);
        var firstReadAt = ReadAt(Id('1'), "qld-1");

        _time.Now = _time.Now.AddHours(5);
        var again = await _service.MarkReadAsync(Id('1'), QldManager);

        Assert.Equal(new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc), firstReadAt);
        Assert.Equal(firstReadAt, ReadAt(Id('1'), "qld-1"));
        Assert.Equal(0, again.UnreadCount);
    }

    [Fact]
    public async Task 标已读_没有通知行也返回200和剩余未读数_不是收件人不报错()
    {
        AddTask('1', "1013", "cam-a", T0.AddMinutes(1), score: 90m);                       // 通知还没发
        AddTask('2', "1013", "cam-a", T0.AddMinutes(2), score: 90m);
        AddHitNotification(Id('2'), ("qld-1", null));

        var result = await _service.MarkReadAsync(Id('1'), QldManager);
        var admin = await _service.MarkReadAsync(Id('2'), Admin);                           // 管理员不是收件人

        Assert.Equal(1, result.UnreadCount);
        Assert.Equal(0, admin.UnreadCount);
        Assert.Null(ReadAt(Id('2'), "qld-1"));                                              // 管理员的调用没有改到别人的行
    }

    [Fact]
    public async Task 标已读_任务不存在_不在自己门店范围_编号格式不对_都是同一个404()
    {
        AddTask('1', "2001", "cam-b", T0.AddMinutes(1), score: 90m);
        AddHitNotification(Id('1'), ("qld-1", null));

        var outOfScope = await Assert.ThrowsAsync<FaceMatchException>(() => _service.MarkReadAsync(Id('1'), QldManager));
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() => _service.MarkReadAsync(Id('f'), QldManager));
        var badFormat = await Assert.ThrowsAsync<FaceMatchException>(() => _service.MarkReadAsync("t1;DROP", QldManager));

        Assert.Equal(404, outOfScope.StatusCode);
        Assert.Equal(outOfScope.Code, missing.Code);
        Assert.Equal(outOfScope.Message, missing.Message);
        Assert.Equal(outOfScope.Code, badFormat.Code);
        Assert.Null(ReadAt(Id('1'), "qld-1"));                                              // 越权的尝试没有改任何行
    }

    // ---------- 种子数据 ----------

    /// <summary>32 位小写十六进制的任务编号（与生产一致，char(32) 列在 SQL Server 上不会被空格补齐）。</summary>
    private static string Id(char ch) => new(ch, 32);

    private void AddTask(
        char idChar, string store, string camera, DateTime capturedAt, decimal score,
        byte review = FaceMatchReviewStatus.Pending, string? reviewer = null, string imageKey = "face-match/hit/x.jpg") =>
        _db.Db.Insertable(new FaceMatchTask
        {
            TaskId = Id(idChar),
            StoreCode = store,
            CameraId = camera,
            CapturedAtUtc = capturedAt,
            EntryId = Id('e'),
            Score = score,
            ImageObjectKey = imageKey,
            ReviewStatus = review,
            FirstReviewerId = reviewer,
            ImageDeleteAtUtc = capturedAt.AddDays(7),
            NotifiedAtUtc = capturedAt,
        }).ExecuteCommand();

    private void AddCapture(string id, string store, string camera, DateTime receivedAt, byte parseStatus = 0, int faceCount = 1, string? duplicateOf = null) =>
        _db.Db.Insertable(new FaceCaptureEvent
        {
            CaptureId = id,
            StoreCode = store,
            CameraId = camera,
            ReceivedAtUtc = receivedAt,
            FaceCount = faceCount,
            ParseStatus = parseStatus,
            DuplicateOfCaptureId = duplicateOf,
        }).ExecuteCommand();

    private void AddEvaluation(string captureId, byte decision, decimal? staff, decimal? visit) =>
        _db.Db.Insertable(new FaceMatchEvaluation { CaptureId = captureId, Decision = decision, StaffScore = staff, VisitScore = visit }).ExecuteCommand();

    private static string NotificationId(string taskId) => $"n-{taskId}";

    /// <summary>与 <see cref="FaceHitNotifier"/> 发布时一致：DedupeKey = 前缀 + TaskId，类型是命中通知，每个收件人一行。</summary>
    private void AddHitNotification(string taskId, params (string User, DateTime? ReadAt)[] recipients)
    {
        _db.Db.Insertable(new AppNotification
        {
            NotificationId = NotificationId(taskId),
            Type = AppNotificationTypes.FaceUnwelcomeHit,
            Title = "Possible match alert",
            Body = "Possible match",
            DedupeKey = FaceHitNotifier.DedupeKeyPrefix + taskId,
            CreatedAtUtc = T0,
            CreatedBy = "system:face-match",
        }).ExecuteCommand();
        foreach (var (user, readAt) in recipients)
        {
            _db.Db.Insertable(new AppNotificationRecipient
            {
                NotificationId = NotificationId(taskId),
                UserGUID = user,
                CreatedAtUtc = T0,
                ReadAtUtc = readAt,
            }).ExecuteCommand();
        }
    }

    private void RevokeNotification(string taskId, DateTime at)
    {
        var notificationId = NotificationId(taskId);
        _db.Db.Updateable<AppNotification>().SetColumns(n => n.RevokedAtUtc == at).Where(n => n.NotificationId == notificationId).ExecuteCommand();
    }

    private void SetNotificationExpiry(string taskId, DateTime at)
    {
        var notificationId = NotificationId(taskId);
        _db.Db.Updateable<AppNotification>().SetColumns(n => n.ExpiresAtUtc == at).Where(n => n.NotificationId == notificationId).ExecuteCommand();
    }

    private DateTime? ReadAt(string taskId, string user)
    {
        var notificationId = NotificationId(taskId);
        return _db.Db.Queryable<AppNotificationRecipient>()
            .Where(r => r.NotificationId == notificationId && r.UserGUID == user)
            .Select(r => r.ReadAtUtc)
            .First();
    }

    private Task<DateTime?> ReadAtOfOtherType(string notificationId, string user) =>
        _db.Db.Queryable<AppNotificationRecipient>()
            .Where(r => r.NotificationId == notificationId && r.UserGUID == user)
            .Select(r => r.ReadAtUtc)
            .FirstAsync();
}
