using System.Text;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;
using static BlazorApp.FaceIngest.Tests.Infrastructure.MatchSamples;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// 处理器三种模式（Off / Capture / Match）的行为与数据残留。2026-10-16 起 Match 与 Capture 一样留存照片与记录（存图、事件行 + 图片行、
/// 原始报文与 .processed 标记），入库成功后另做计数 / 访客去重 / 名单比对并写评估行；Off 仍不留任何东西。
/// 时间相关用例（营业日、DeleteAfterUtc）与进程时区无关，应在 TZ=UTC 与 TZ=America/Los_Angeles 下各跑一遍。
/// </summary>
public sealed class FaceMatchProcessingTests
{
    private static readonly DateTimeOffset Now = MatchHarness.Now;
    private const string BrisbaneGroup = "vS1001_20261012";

    // ------------------------------------------------------------------ Off

    [Fact]
    public async Task Off_不调用腾讯云_不存图_不写事件行_本地报文读完即删()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 0)]);
        await harness.PostAsync(Capture(Face(1)));
        Assert.NotEmpty(harness.LeftoverFiles());

        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), summary);
        Assert.Empty(harness.LeftoverFiles());
        Assert.Empty(harness.Writer.Events);
        Assert.Empty(harness.Writer.Images);
        Assert.Empty(harness.Images.Calls);
        Assert.Empty(harness.Engine.Searches);
        Assert.Equal(0, harness.Http.RequestCount);
        Assert.Empty(harness.Store.Buckets);
        Assert.Equal(0, harness.Writer.WriteCalls);
        Assert.Empty(harness.Store.Evaluations);
        // 摄像机最近推送时间仍照常更新（不涉及生物信息）。
        Assert.Single(harness.Writer.Touches);
        // 容量统计同步扣减。
        Assert.Equal(0, harness.Factory.GetService<RawCaptureStore>().StoredFileCount);
        Assert.Equal(0, harness.Factory.GetService<RawCaptureStore>().StoredBytes);
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await harness.RunAsync());
    }

    [Fact]
    public async Task 档案读不到或缺失_按Off处理并丢弃()
    {
        using var unreadable = await MatchHarness.CreateAsync(profilesLoadFails: true);
        using var missing = await MatchHarness.CreateAsync([Profile("OTHER", mode: 1)]);
        foreach (var harness in new[] { unreadable, missing })
        {
            await harness.PostAsync(Capture(Face(1)));
            await harness.RunAsync();

            Assert.Empty(harness.LeftoverFiles());
            Assert.Empty(harness.Writer.Events);
            Assert.Empty(harness.Images.Calls);
            Assert.Empty(harness.Engine.Searches);
        }
    }

    // ------------------------------------------------------------------ Capture

    [Fact]
    public async Task Capture_现有行为完全不变_存图写事件行写processed_不评估不调用腾讯云()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, allowedSubjects: "仅内部测试员工")]);
        await harness.PostAsync(Capture(Face(1)));

        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), summary);
        var basePath = Assert.Single(P1TestSupport.CaptureBasePaths(harness.Factory));
        Assert.True(File.Exists(basePath + ".processed"));
        Assert.True(File.Exists(basePath + ".bin"));
        Assert.Single(harness.Writer.Events);
        Assert.Equal(2, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
        Assert.Empty(harness.Engine.Searches);
        Assert.Empty(harness.Store.Evaluations);
        Assert.Empty(harness.Store.Buckets);
        Assert.Equal(0, harness.Http.RequestCount);
    }

    [Fact]
    public async Task Capture加EvaluateMatch_记分数与判定_不写计数桶_不影响留存()
    {
        var staffFace = Face(1);
        var customerFace = Face(2);
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true, staff: true, dedup: true, allowedSubjects: "仅内部测试员工")]);
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);

        await harness.PostAsync(Capture(staffFace, 1), Now);
        await harness.PostAsync(Capture(customerFace, 2), Now.AddSeconds(1));
        await harness.PostAsync(Capture(customerFace, 3), Now.AddSeconds(2));
        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(3, 3, 0, 0), summary);
        Assert.Equal(3, harness.Writer.Events.Count);
        Assert.Equal(3, harness.Store.Evaluations.Count);
        var byCapture = harness.Writer.Events.Values.Select(v => v.Event).OrderBy(e => e.ReceivedAtUtc).ToList();
        var staffEval = harness.Store.Evaluations.Single(e => e.CaptureId == byCapture[0].CaptureId);
        var newEval = harness.Store.Evaluations.Single(e => e.CaptureId == byCapture[1].CaptureId);
        var repeatEval = harness.Store.Evaluations.Single(e => e.CaptureId == byCapture[2].CaptureId);
        Assert.Equal(EvaluationDecision.Staff, staffEval.Decision);
        Assert.Equal(99m, staffEval.StaffScore);
        Assert.Equal(EvaluationDecision.NewVisitor, newEval.Decision);
        Assert.Null(newEval.VisitScore);
        Assert.Equal(EvaluationDecision.RepeatVisitor, repeatEval.Decision);
        Assert.Equal(99m, repeatEval.VisitScore);
        // 标定要看到全部分数：评估搜索不带最低分阈值。
        Assert.All(harness.Engine.Searches, search => Assert.Null(search.MinScore));
        // 评估不写计数桶，也不累加访客数；留存与原来一样（图片 + 事件行）。
        Assert.Empty(harness.Store.Buckets);
        Assert.Equal(6, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
        Assert.Equal(3, P1TestSupport.CaptureBasePaths(harness.Factory).Count);
    }

    [Fact]
    public async Task Capture评估失败_腾讯云错误或数据库错误_都不影响原有入库()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true, staff: true, allowedSubjects: "仅内部测试员工")]);
        // 第一条：腾讯云账号级错误（评估失败）。
        harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.AccountFatal, "AuthFailure.SignatureFailure", "SearchFacesReturnsByGroup"));
        await harness.PostAsync(Capture(Face(1), 1));
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        // 第二条：熔断冷却过后搜索成功，但写评估行时数据库出错（异常被吞掉，只记日志）。
        harness.Store.EvaluationFailuresRemaining = 1;
        await harness.PostAsync(Capture(Face(2), 2), Now.AddSeconds(120));
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        Assert.Equal(2, harness.Writer.Events.Count);
        Assert.All(P1TestSupport.CaptureBasePaths(harness.Factory), basePath => Assert.True(File.Exists(basePath + ".processed")));
        Assert.Empty(harness.Store.Evaluations);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == LogLevel.Warning && log.Message.Contains("evaluation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture评估只对非重复的新事件做_重复报文不再调用腾讯云()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true, staff: true, allowedSubjects: "仅内部测试员工")]);
        var body = Capture(Face(1), 1);
        await harness.PostAsync(body);
        await harness.PostAsync(body, Now.AddSeconds(5));

        await harness.RunAsync();

        Assert.Single(harness.Engine.Searches);
        Assert.Single(harness.Store.Evaluations);
    }

    [Fact]
    public async Task Capture但匹配被关闭_评估被跳过_仍是原有行为()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(mode: 1, evaluate: true, staff: true, allowedSubjects: "仅内部测试员工")],
            configure: settings => settings["FaceMatch:Tencent:Region"] = "ap-beijing");
        await harness.PostAsync(Capture(Face(1)));

        await harness.RunAsync();

        Assert.Single(harness.Writer.Events);
        Assert.Empty(harness.Engine.Searches);
        Assert.Equal(0, harness.Http.RequestCount);
    }

    // ------------------------------------------------------------------ Capture：名单检查（仅内部知情人员的端到端测试）

    [Fact]
    public async Task Capture加名单_命中内部人员照片_建待复核任务_存命中图_原有留存不变_不写计数桶()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync(
            [Profile(mode: 1, unwelcome: true, allowedSubjects: "仅内部测试员工", unwelcomeRecord: false, crossBorderRegion: null, crossBorderReviewed: false)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        await harness.PostAsync(Capture(listedFace, 1));

        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), summary);
        var task = Assert.Single(harness.Store.Tasks);
        Assert.Equal(("S1001", "cam-front-01", new string('e', 32), 99m), (task.StoreCode, task.CameraId, task.EntryId, task.Score));
        Assert.Equal(Now.UtcDateTime.AddDays(7), task.ImageDeleteAtUtc);
        var (image, key) = Assert.Single(harness.Hits.Saved);
        Assert.Equal(task.ImageObjectKey, key);
        Assert.Equal(listedFace, image.Data.ToArray());
        // 只搜名单库，且用名单阈值作最低分（不带最低分的是标定评估，这里不是）。
        var search = Assert.Single(harness.Engine.Searches);
        Assert.Equal([FaceGroups.Unwelcome], search.GroupIds.ToArray());
        Assert.Equal(95d, search.MinScore);
        // Capture 的留存完全不变：事件行 + 图片 + processed 标记；不写计数桶，不写评估行。
        Assert.Single(harness.Writer.Events);
        Assert.Equal(2, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
        Assert.True(File.Exists(Assert.Single(P1TestSupport.CaptureBasePaths(harness.Factory)) + ".processed"));
        Assert.Empty(harness.Store.Buckets);
        Assert.Empty(harness.Store.Evaluations);
    }

    [Fact]
    public async Task Capture加名单_没命中或条目过期_不建任务_照常入库()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, unwelcome: true, allowedSubjects: "仅内部测试员工")]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddSeconds(-1));

        await harness.PostAsync(Capture(Face(9), 1), Now);
        await harness.PostAsync(Capture(listedFace, 2), Now.AddSeconds(1));
        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(2, 2, 0, 0), summary);
        Assert.Empty(harness.Store.Tasks);
        Assert.Empty(harness.Hits.Saved);
        Assert.Equal(2, harness.Writer.Events.Count);
    }

    [Fact]
    public async Task Capture加名单_腾讯云错误或存命中图失败_都不影响原有入库()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, unwelcome: true, allowedSubjects: "仅内部测试员工")]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        // 第一条：腾讯云账号级错误。
        harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.AccountFatal, "AuthFailure.SignatureFailure", "SearchFacesReturnsByGroup"));
        await harness.PostAsync(Capture(listedFace, 1));
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        // 第二条：熔断冷却过后搜索命中，但存命中图时 COS 出错（异常被吞掉，只记日志）。
        harness.Hits.FailuresRemaining = 1;
        await harness.PostAsync(Capture(listedFace, 2), Now.AddSeconds(120));
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        Assert.Equal(2, harness.Writer.Events.Count);
        Assert.All(P1TestSupport.CaptureBasePaths(harness.Factory), basePath => Assert.True(File.Exists(basePath + ".processed")));
        Assert.Empty(harness.Store.Tasks);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == LogLevel.Warning && log.Message.Contains("list check in Capture mode failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture加名单_只对非重复的新事件检查_重复报文不再调用腾讯云()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, unwelcome: true, allowedSubjects: "仅内部测试员工")]);
        var body = Capture(Face(1), 1);
        await harness.PostAsync(body);
        await harness.PostAsync(body, Now.AddSeconds(5));

        await harness.RunAsync();

        Assert.Single(harness.Engine.Searches);
    }

    [Fact]
    public async Task Capture没开名单_行为与之前完全一致_不搜名单库()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1, allowedSubjects: "仅内部测试员工")]);
        await harness.PostAsync(Capture(Face(1)));

        await harness.RunAsync();

        Assert.Empty(harness.Engine.Searches);
        Assert.Equal(0, harness.Http.RequestCount);
        Assert.Empty(harness.Store.Tasks);
    }

    // ------------------------------------------------------------------ Match：数据残留

    [Fact]
    public async Task Match_与Capture一样留存_存图写事件行和图片行_写processed_另累加计数与评估()
    {
        var staffFace = Face(1);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)], configure: s => s["FaceMatch:StaffSuppressSeconds"] = "0");
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);
        await harness.PostAsync(Capture(staffFace, 1));
        await harness.PostAsync(Capture(Face(2), 2));
        await harness.PostAsync(Capture(Face(2), 3));
        await harness.PostAsync(Encoding.UTF8.GetBytes("not a multipart body at all"));

        var summary = await harness.RunAsync();

        Assert.Equal(new ProcessingSummary(4, 4, 0, 0), summary);
        AssertStoredLikeCapture(harness);
        // 与 Capture 同一留存：每条一行事件（含解析失败的那条），三条人脸抓拍各两张图（人脸 + 背景），原始报文按 Capture 配置保存。
        Assert.Equal(4, harness.Writer.Events.Count);
        Assert.Equal(6, harness.Writer.Images.Count);
        Assert.Equal(6, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
        Assert.Equal(4, harness.Images.Calls.Count(call => call.StartsWith("raw:", StringComparison.Ordinal)));
        Assert.Empty(harness.Hits.Saved);
        // 另外照旧累加计数，并给三条人脸抓拍各写一行评估（解析失败、没有人脸的那条没有判定）。
        var total = harness.Store.Total();
        Assert.Equal((3, 1, 1, 1), (total.Capture, total.StaffMatch, total.UniqueVisitor, total.Repeat));
        Assert.Equal(3, harness.Store.Evaluations.Count);
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await harness.RunAsync());
    }

    [Fact]
    public async Task Match_没有人脸图的报文照常入库_不计数_不写评估行()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        await harness.PostAsync(HikvisionSamples.XmlVmdMultipart());

        await harness.RunAsync();

        AssertStoredLikeCapture(harness);
        Assert.Single(harness.Writer.Events);
        Assert.Empty(harness.Store.Evaluations);
        Assert.Empty(harness.Store.Buckets);
        Assert.Empty(harness.Engine.Searches);
    }

    // ------------------------------------------------------------------ Match：三种去重结果与员工

    [Fact]
    public async Task Match去重三种结果_新访客_重复_员工_计数与访客库行一致()
    {
        var staffFace = Face(1);
        var visitorFace = Face(2);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);

        await harness.PostAsync(Capture(visitorFace, 1));
        await harness.RunAsync();
        Assert.Equal(1, harness.Store.Total().UniqueVisitor);
        var firstSearch = Assert.Single(harness.Engine.Searches);
        Assert.Equal([FaceGroups.Staff, BrisbaneGroup], firstSearch.GroupIds);
        // 全库一次搜索，带上最低阈值（员工 / 访客阈值默认都是 85）。
        Assert.Equal(85, firstSearch.MinScore);
        Assert.Equal([BrisbaneGroup], harness.Engine.CreatedGroups);

        await harness.PostAsync(Capture(visitorFace, 2), Now.AddSeconds(40));
        await harness.PostAsync(Capture(staffFace, 3), Now.AddSeconds(80));
        harness.Factory.Time.SetUtcNow(Now.AddSeconds(90));
        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal(3, total.Capture);
        Assert.Equal(1, total.UniqueVisitor);
        Assert.Equal(1, total.Repeat);
        Assert.Equal(1, total.StaffMatch);
        Assert.Equal(0, total.LowQuality + total.ApiFailure);
        // 每张人脸只搜一次；只为新访客建了一个人，建在当日访客库。
        Assert.Equal(3, harness.Engine.Searches.Count);
        var (groupId, personId) = Assert.Single(harness.Engine.CreatedPersons);
        Assert.Equal(BrisbaneGroup, groupId);
        Assert.StartsWith("vp", personId, StringComparison.Ordinal);
        // 营业日 = 门店本地日期；首次建组时登记 FaceVisitGroup，之后只增 VisitorCount。
        var group = Assert.Single(harness.Store.VisitGroups);
        Assert.Equal(new VisitGroupRecord(BrisbaneGroup, "S1001", new DateOnly(2026, 10, 12), Now.UtcDateTime, new DateTime(2026, 10, 12, 16, 0, 0, DateTimeKind.Utc)), group);
        Assert.Equal(1, harness.Store.VisitorCounts[BrisbaneGroup]);
        // 计数落在抓拍接收时刻所在的 UTC 整点桶。
        Assert.Contains(harness.Store.Buckets.Keys, key => key.StoreCode == "S1001" && key.Bucket == new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc));
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task Match只开员工排除_不建访客库_非员工只计抓拍数()
    {
        var staffFace = Face(1);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);
        await harness.PostAsync(Capture(staffFace, 1));
        await harness.PostAsync(Capture(Face(2), 2), Now.AddSeconds(40));

        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal(2, total.Capture);
        Assert.Equal(1, total.StaffMatch);
        Assert.Equal(0, total.UniqueVisitor + total.Repeat);
        Assert.Empty(harness.Engine.CreatedGroups);
        Assert.Empty(harness.Engine.CreatedPersons);
        Assert.Empty(harness.Store.VisitGroups);
        Assert.All(harness.Engine.Searches, search => Assert.Equal([FaceGroups.Staff], search.GroupIds));
    }

    [Fact]
    public async Task Match没开任何比对_不调用腾讯云_只计抓拍数()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(crossBorderRegion: null, crossBorderReviewed: false)]);
        await harness.PostAsync(Capture(Face(1)));

        await harness.RunAsync();

        Assert.Empty(harness.Engine.Searches);
        Assert.Equal(0, harness.Http.RequestCount);
        Assert.Equal(1, harness.Store.Total().Capture);
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 员工命中后同摄像机抑制窗口内的相邻抓拍直接计员工_不再搜索_窗口外恢复搜索()
    {
        var staffFace = Face(1);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)], configure: s => s["FaceMatch:StaffSuppressSeconds"] = "30");
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);

        await harness.PostAsync(Capture(staffFace, 1));
        await harness.RunAsync();
        Assert.Single(harness.Engine.Searches);

        // 10 秒后另一个人（不是员工）：窗口内，直接计员工，不搜索。
        await harness.PostAsync(Capture(Face(2), 2), Now.AddSeconds(10));
        await harness.RunAsync();
        Assert.Single(harness.Engine.Searches);
        Assert.Equal(2, harness.Store.Total().StaffMatch);

        // 31 秒后：窗口外，恢复搜索，非员工只计抓拍数。
        await harness.PostAsync(Capture(Face(3), 3), Now.AddSeconds(31));
        await harness.RunAsync();
        Assert.Equal(2, harness.Engine.Searches.Count);
        Assert.Equal(2, harness.Store.Total().StaffMatch);
        Assert.Equal(3, harness.Store.Total().Capture);
    }

    [Fact]
    public async Task 抑制窗口设为0时关闭抑制()
    {
        var staffFace = Face(1);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)], configure: s => s["FaceMatch:StaffSuppressSeconds"] = "0");
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);
        await harness.PostAsync(Capture(staffFace, 1));
        await harness.PostAsync(Capture(Face(2), 2), Now.AddSeconds(2));

        await harness.RunAsync();

        Assert.Equal(2, harness.Engine.Searches.Count);
    }

    [Fact]
    public async Task 同摄像机相同请求体哈希_不重复搜索也不重复计数()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        var body = Capture(Face(2), 1);
        await harness.PostAsync(body);
        await harness.PostAsync(body, Now.AddSeconds(5));
        await harness.PostAsync(body, Now.AddSeconds(10));

        await harness.RunAsync();

        Assert.Single(harness.Engine.Searches);
        Assert.Equal(1, harness.Store.Total().Capture);
        Assert.Equal(1, harness.Store.Total().UniqueVisitor);
        AssertStoredLikeCapture(harness);

        // 2026-10-16 起 Match 先按 Capture 入库：入库层「同摄像机同哈希 24 小时」判成重复的报文不再交给流水线，
        // 所以流水线 300 秒窗口之外、24 小时之内的同一请求体也不会再计数。
        await harness.PostAsync(body, Now.AddSeconds(400));
        harness.Factory.Time.SetUtcNow(Now.AddSeconds(400));
        await harness.RunAsync();
        Assert.Equal(1, harness.Store.Total().Capture);
        Assert.Single(harness.Engine.Searches);
    }

    // ------------------------------------------------------------------ Match：串行

    [Fact]
    public async Task 同一家店Match串行_同一个人相邻两条抓拍并发处理只建一人_不同店不互相阻塞()
    {
        var face = Face(2);
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true), Profile("S2002", dedup: true, staff: true)]);
        var pipeline = harness.Factory.GetService<FaceMatchPipeline>();
        var policy = harness.Factory.GetService<StoreModePolicy>();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSearch = 1;
        harness.Engine.SearchGate = async () =>
        {
            // 只卡住第一次搜索。
            if (Interlocked.Exchange(ref firstSearch, 0) == 1)
            {
                await released.Task;
            }
        };

        MatchCapture NewCapture(string store, string id, byte backgroundSeed)
            => new(id.PadLeft(32, '0'), "cam-front-01", store, Now, new string((char)('a' + backgroundSeed), 64), [new MatchFace(1, face, 120, 160)]);

        var a1 = pipeline.ProcessAsync(NewCapture("S1001", "a1", 1), policy.Resolve("S1001"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Engine.Searches.Count == 1);
        var a2 = pipeline.ProcessAsync(NewCapture("S1001", "a2", 2), policy.Resolve("S1001"), CancellationToken.None);
        await Task.Delay(150);

        // 第一条卡在搜索里；同店第二条必须排队，没有发出第二次搜索。
        Assert.Single(harness.Engine.Searches);
        Assert.False(a2.IsCompleted);

        // 另一家店不被挡住。
        var b1 = pipeline.ProcessAsync(NewCapture("S2002", "b1", 3), policy.Resolve("S2002"), CancellationToken.None);
        await b1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Engine.Searches.Count);

        released.SetResult();
        await Task.WhenAll(a1, a2);

        // S1001 的两条是同一个人：第一条建人，第二条看到刚建的人，计「重复」。
        Assert.Single(harness.Engine.CreatedPersons, person => person.GroupId == "vS1001_20261012");
        var s1001 = harness.Store.Buckets.Where(pair => pair.Key.StoreCode == "S1001").Sum(pair => pair.Value.UniqueVisitor);
        var s1001Repeat = harness.Store.Buckets.Where(pair => pair.Key.StoreCode == "S1001").Sum(pair => pair.Value.Repeat);
        Assert.Equal(1, s1001);
        Assert.Equal(1, s1001Repeat);
    }

    // ------------------------------------------------------------------ Match：质量闸门

    [Theory]
    [InlineData(60, 60)]
    [InlineData(200, 79)]
    [InlineData(79, 200)]
    public async Task 质量闸门_最小边长不足_计低质量_不调用腾讯云(int width, int height)
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        await harness.PostAsync(Capture(Face(2, width, height)));

        await harness.RunAsync();

        Assert.Empty(harness.Engine.Searches);
        Assert.Empty(harness.Engine.CreatedGroups);
        var total = harness.Store.Total();
        Assert.Equal((1, 1), (total.Capture, total.LowQuality));
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 质量闸门_体积低于下限_计低质量_默认下限4096字节()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true)],
            configure: settings => settings["FaceMatch:Quality:MinBytes"] = "4096");
        await harness.PostAsync(Capture(Face(2)));

        await harness.RunAsync();

        Assert.Empty(harness.Engine.Searches);
        Assert.Equal(1, harness.Store.Total().LowQuality);
    }

    [Fact]
    public async Task 腾讯云判定无人脸_计低质量_不重试_也不建访客()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)], realTencentClient: true);
        // 请求顺序：建访客库 → 第一次搜索（RetCode -1601 质量不过）→ 第二次搜索（NoFaceInPhoto 错误）。
        harness.Http.EnqueueJson("""{"Response":{"RequestId":"r-group"}}""");
        harness.Http.EnqueueJson("""{"Response":{"ResultsReturnsByGroup":[{"RetCode":-1601,"GroupCandidates":[]}],"RequestId":"r"}}""");
        harness.Http.EnqueueError("InvalidParameterValue.NoFaceInPhoto");
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(1));

        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal((2, 2, 0), (total.Capture, total.LowQuality, total.UniqueVisitor));
        // 建组请求 1 次 + 两次搜索，没有重试、没有建人。
        Assert.DoesNotContain(harness.Http.Requests, request => request.Headers["X-TC-Action"] == "CreatePerson");
        Assert.Equal(2, harness.Http.Requests.Count(request => request.Headers["X-TC-Action"] == "SearchFacesReturnsByGroup"));
    }

    // ------------------------------------------------------------------ Match：腾讯云失败

    [Fact]
    public async Task 瞬时错误退避重试后成功()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        var transient = new FaceMatchApiException(FaceMatchErrorKind.Transient, "InternalError", "SearchFacesReturnsByGroup");
        harness.Engine.SearchFailures.Enqueue(transient);
        harness.Engine.SearchFailures.Enqueue(transient);
        await harness.PostAsync(Capture(Face(2)));

        await harness.RunAsync();

        Assert.Equal(3, harness.Engine.Searches.Count);
        var total = harness.Store.Total();
        Assert.Equal((1, 0), (total.Capture, total.ApiFailure));
    }

    [Theory]
    [InlineData("Transient", "InternalError")]
    [InlineData("RateLimited", "RequestLimitExceeded")]
    public async Task 重试超限则丢弃并计ApiFailure_不留生物数据_日志不含敏感信息(string kindName, string code)
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        for (var i = 0; i < 10; i++)
        {
            harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(Enum.Parse<FaceMatchErrorKind>(kindName), code, "SearchFacesReturnsByGroup", "req-1"));
        }

        await harness.PostAsync(Capture(Face(2)));
        var summary = await harness.RunAsync();

        // 默认最多 3 次尝试：恰好 3 次调用，然后丢弃。
        Assert.Equal(3, harness.Engine.Searches.Count);
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), summary);
        var total = harness.Store.Total();
        Assert.Equal((1, 1, 0, 0), (total.Capture, total.ApiFailure, total.UniqueVisitor, total.StaffMatch));
        Assert.Empty(harness.Engine.CreatedPersons);
        // 调用失败只影响比对：照片与事件行照常留存，没有判定所以不写评估行。
        AssertStoredLikeCapture(harness);
        Assert.Single(harness.Writer.Events);
        Assert.Empty(harness.Store.Evaluations);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == LogLevel.Warning && log.Message.Contains($"code={code}", StringComparison.Ordinal));
        AssertLogsClean(harness);
    }

    [Fact]
    public void 退避延迟指数增长且有上限_限频更长()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.Settings["FaceIngest:Database:ConnectionString"] = P1TestSupport.FakeConnectionString;
            options.Settings["FaceMatch:Tencent:SecretId"] = FakeSecretId;
            options.Settings["FaceMatch:Tencent:SecretKey"] = FakeSecretKey;
        });
        var pipeline = factory.GetService<FaceMatchPipeline>();

        Assert.Equal(TimeSpan.FromMilliseconds(500), pipeline.ComputeRetryDelay(FaceMatchErrorKind.Transient, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.Transient, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.Transient, 4));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.Transient, 5));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.Transient, 50));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.RateLimited, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.RateLimited, 3));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), pipeline.ComputeRetryDelay(FaceMatchErrorKind.RateLimited, 99));
    }

    [Fact]
    public async Task 账号级致命错误_告警不重试_熔断期内不再调用腾讯云_冷却后恢复()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.AccountFatal, "AuthFailure.SignatureFailure", "SearchFacesReturnsByGroup"));
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.RunAsync();

        Assert.Single(harness.Engine.Searches);
        Assert.Equal(1, harness.Store.Total().ApiFailure);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == LogLevel.Error && log.Message.Contains("AuthFailure.SignatureFailure", StringComparison.Ordinal));

        // 熔断期（默认 60 秒）内：不再发起调用，直接丢弃并计失败。
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(5));
        await harness.RunAsync();
        Assert.Single(harness.Engine.Searches);
        Assert.Equal(2, harness.Store.Total().ApiFailure);
        AssertStoredLikeCapture(harness);

        // 冷却结束后恢复调用。
        await harness.PostAsync(Capture(Face(4), 3), Now.AddSeconds(70));
        await harness.RunAsync();
        Assert.Equal(2, harness.Engine.Searches.Count);
        Assert.Equal(2, harness.Store.Total().ApiFailure);
        Assert.Equal(3, harness.Store.Total().Capture);
    }

    [Theory]
    [InlineData("ResourceUnavailable.InArrears")]
    [InlineData("ResourceUnavailable.NotExist")]
    public async Task 欠费或未开通同样熔断_不重试(string code)
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)], realTencentClient: true);
        harness.Http.EnqueueError(code);
        await harness.PostAsync(Capture(Face(2)));

        await harness.RunAsync();

        Assert.Equal(1, harness.Http.RequestCount);
        Assert.Equal(1, harness.Store.Total().ApiFailure);
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 请求级致命错误_库不存在_告警不重试_只丢弃这一条_不熔断()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.RequestFatal, "InvalidParameterValue.GroupIdNotExist", "SearchFacesReturnsByGroup"));
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(1));

        await harness.RunAsync();

        Assert.Equal(2, harness.Engine.Searches.Count);
        var total = harness.Store.Total();
        Assert.Equal((2, 1), (total.Capture, total.ApiFailure));
    }

    [Fact]
    public async Task 建访客失败_计失败不计唯一访客_建访客质量不过_计低质量()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true)]);
        harness.Engine.CreatePersonFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.RequestFatal, "InvalidParameterValue.PersonGroupNotExist", "CreatePerson"));
        harness.Engine.CreatePersonFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.Quality, "InvalidParameterValue.NoFaceInPhoto", "CreatePerson"));
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(1));

        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal((2, 0, 1, 1), (total.Capture, total.UniqueVisitor, total.ApiFailure, total.LowQuality));
        Assert.Equal(0, harness.Store.VisitorCounts[BrisbaneGroup]);
    }

    [Fact]
    public async Task 建访客库失败_计失败_不搜索_下次再试建库()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true)]);
        harness.Engine.CreateGroupFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.RequestFatal, "InvalidParameterValue.GroupIdIllegal", "CreateGroup"));
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(1));

        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal((2, 1, 1), (total.Capture, total.ApiFailure, total.UniqueVisitor));
        Assert.Single(harness.Engine.Searches);
    }

    // ------------------------------------------------------------------ Match：失败语义（2026-10-16 起入库与比对分开）

    [Fact]
    public async Task 计数写库失败_入库结果仍成功_不重试_只记节流日志_该条少计()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        harness.Store.ApplyFailuresRemaining = int.MaxValue;
        await harness.PostAsync(Capture(Face(2), 1));
        await harness.PostAsync(Capture(Face(3), 2), Now.AddSeconds(1));

        Assert.Equal(new ProcessingSummary(2, 2, 0, 0), await harness.RunAsync());

        AssertStoredLikeCapture(harness);
        Assert.Equal(2, harness.Writer.Events.Count);
        Assert.Equal(4, harness.Writer.Images.Count);
        Assert.Empty(harness.Store.Buckets);
        // 流水线失败就没有判定，评估行也不写；两条同类失败只记一条 Warning（节流）。
        Assert.Empty(harness.Store.Evaluations);
        Assert.Single(harness.Factory.LogEntries, log => log.Level == LogLevel.Warning && log.Message.Contains("failed and was skipped; the capture is stored", StringComparison.Ordinal));
        // 不重试整条：下一轮没有候选，也不会重复存图。
        harness.Factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await harness.RunAsync());
        Assert.Equal(2, harness.Store.ApplyCalls);
        Assert.Equal(4, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 评估行写失败_计数照加_入库结果仍成功()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        harness.Store.EvaluationFailuresRemaining = 1;
        await harness.PostAsync(Capture(Face(2)));

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        Assert.Equal(1, harness.Store.Total().UniqueVisitor);
        Assert.Empty(harness.Store.Evaluations);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == LogLevel.Warning && log.Message.Contains("evaluation row in Match mode failed", StringComparison.Ordinal));
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 入库失败按Capture退避重试_失败期间不比对不计数_成功后只计一次()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        harness.Writer.FailuresRemaining = 1;
        await harness.PostAsync(Capture(Face(2)));

        Assert.Equal(new ProcessingSummary(1, 0, 1, 0), await harness.RunAsync());
        var basePath = Assert.Single(P1TestSupport.CaptureBasePaths(harness.Factory));
        Assert.True(File.Exists(basePath + ".retry"));
        Assert.Empty(harness.Engine.Searches);
        Assert.Empty(harness.Store.Buckets);

        harness.Factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());
        AssertStoredLikeCapture(harness);
        Assert.Single(harness.Engine.Searches);
        Assert.Equal(1, harness.Store.Total().Capture);
    }

    [Fact]
    public async Task 入库一直失败_按Capture写failed并保留原始报文_从不比对()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true)],
            configure: s => s["FaceIngest:ProcessingMaxAttempts"] = "2");
        harness.Writer.FailuresRemaining = int.MaxValue;
        await harness.PostAsync(Capture(Face(2)));

        await harness.RunAsync();
        harness.Factory.Time.Advance(TimeSpan.FromMinutes(5));
        var last = await harness.RunAsync();

        Assert.Equal(1, last.Failed);
        var basePath = Assert.Single(P1TestSupport.CaptureBasePaths(harness.Factory));
        Assert.True(File.Exists(basePath + ".failed"));
        Assert.True(File.Exists(basePath + ".bin"));
        Assert.Empty(harness.Engine.Searches);
        Assert.Empty(harness.Store.Buckets);
    }

    [Fact]
    public async Task 已入库的报文被重处理_事件行已存在_不重复计数不重复搜索()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        await harness.PostAsync(Capture(Face(2)));
        await harness.RunAsync();
        // 模拟「入库成功但 .processed 没写成」：删掉标记让它被重处理。
        var basePath = Assert.Single(P1TestSupport.CaptureBasePaths(harness.Factory));
        File.Delete(basePath + ".processed");

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());

        Assert.Single(harness.Engine.Searches);
        Assert.Equal((1, 1), (harness.Store.Total().Capture, harness.Store.Total().UniqueVisitor));
        Assert.Single(harness.Writer.Events);
        Assert.Single(harness.Store.Evaluations);
    }

    // ------------------------------------------------------------------ Match：记录内容（2026-10-16）

    [Fact]
    public async Task Match记录_事件行与图片行和Capture对同一请求体逐字段一致_另有评估行()
    {
        var body = Capture(Face(2), 1);
        using var match = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        using var capture = await MatchHarness.CreateAsync([Profile(mode: 1, allowedSubjects: "仅内部测试员工")]);
        var at = Now.AddTicks(1_234_567);
        await match.PostAsync(body, at);
        await capture.PostAsync(body, at);

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await match.RunAsync());
        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await capture.RunAsync());

        var matchEvent = Assert.Single(match.Writer.Events).Value.Event;
        var captureEvent = Assert.Single(capture.Writer.Events).Value.Event;
        // 只有抓拍编号与写入时间不同（两个宿主各自生成）；存储名、对象键口径、时间、哈希、解析结果都相同。
        Assert.Equal(captureEvent with { CaptureId = "x", CreatedAtUtc = default }, matchEvent with { CaptureId = "x", CreatedAtUtc = default });
        Assert.Equal(at.UtcDateTime, matchEvent.ReceivedAtUtc);
        static object Normalize(CaptureImageRecord image) => image with
        {
            ImageId = "x",
            CaptureId = "x",
            ObjectKey = image.ObjectKey.Replace(image.CaptureId, "x", StringComparison.Ordinal),
            CreatedAtUtc = default,
        };
        Assert.Equal(
            capture.Writer.Images.Values.OrderBy(i => i.PartIndex).Select(Normalize),
            match.Writer.Images.Values.OrderBy(i => i.PartIndex).Select(Normalize));
        Assert.Equal(2, match.Writer.Images.Count);
        Assert.Equal(capture.Images.Calls.Count, match.Images.Calls.Count);

        // Match 多出来的：计数与评估行（Capture 没开评估，两者都没有）。
        var evaluation = Assert.Single(match.Store.Evaluations);
        Assert.Equal(matchEvent.CaptureId, evaluation.CaptureId);
        Assert.Equal(EvaluationDecision.NewVisitor, evaluation.Decision);
        Assert.Equal(1, match.Store.Total().UniqueVisitor);
        Assert.Empty(capture.Store.Evaluations);
        Assert.Empty(capture.Store.Buckets);
        // 两边本地都只剩同样的留存文件（.json / .bin / .processed）。
        AssertStoredLikeCapture(match);
        AssertStoredLikeCapture(capture);
    }

    [Fact]
    public async Task Match记录_判定与分数_员工_新访客_回头客_质量不足_调用失败不写评估行()
    {
        var staffFace = Face(1);
        var visitorFace = Face(2);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)], configure: s => s["FaceMatch:StaffSuppressSeconds"] = "0");
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);
        await harness.PostAsync(Capture(staffFace, 1));
        await harness.PostAsync(Capture(visitorFace, 2), Now.AddSeconds(10));
        await harness.PostAsync(Capture(visitorFace, 3), Now.AddSeconds(20));
        await harness.PostAsync(Capture(Face(3, 60, 60), 4), Now.AddSeconds(30));
        harness.Factory.Time.SetUtcNow(Now.AddSeconds(40));
        await harness.RunAsync();
        // 第五条：账号级错误（不重试），没有判定。
        harness.Engine.SearchFailures.Enqueue(new FaceMatchApiException(FaceMatchErrorKind.AccountFatal, "AuthFailure.SignatureFailure", "SearchFacesReturnsByGroup"));
        await harness.PostAsync(Capture(Face(4), 5), Now.AddSeconds(50));
        await harness.RunAsync();

        var events = harness.Writer.Events.Values.Select(v => v.Event).OrderBy(e => e.ReceivedAtUtc).ToList();
        Assert.Equal(5, events.Count);
        EvaluationRecord? EvalOf(int i) => harness.Store.Evaluations.SingleOrDefault(e => e.CaptureId == events[i].CaptureId);

        Assert.Equal(new EvaluationRecord(events[0].CaptureId, 99m, null, EvaluationDecision.Staff), EvalOf(0));
        // 新访客：Match 的搜索带最低阈值，没过阈值的候选不会返回，所以分数为空（与 Capture 评估不带阈值不同）。
        Assert.Equal(new EvaluationRecord(events[1].CaptureId, null, null, EvaluationDecision.NewVisitor), EvalOf(1));
        Assert.Equal(new EvaluationRecord(events[2].CaptureId, null, 99m, EvaluationDecision.RepeatVisitor), EvalOf(2));
        Assert.Equal(new EvaluationRecord(events[3].CaptureId, null, null, EvaluationDecision.LowQuality), EvalOf(3));
        Assert.Null(EvalOf(4));
        var total = harness.Store.Total();
        Assert.Equal((5, 1, 1, 1, 1, 1), (total.Capture, total.StaffMatch, total.UniqueVisitor, total.Repeat, total.LowQuality, total.ApiFailure));
    }

    [Fact]
    public async Task Match记录_多张人脸按第一张判定_计数仍按每张累加()
    {
        var staffFace = Face(1);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        harness.Engine.Enroll(FaceGroups.Staff, staffFace);
        var json = Encoding.UTF8.GetBytes("""
            {"eventType":"faceCapture","eventState":"active","dateTime":"2026-10-12T12:00:00+11:00","channelID":"1",
             "faceCapture":[{"faces":[{"faceId":"A1","contentID":"facePicture1"},{"faceId":"A2","contentID":"facePicture2"}]}]}
            """);
        var body = HikvisionSamples.BuildMultipart(
            "boundary",
            "\r\n",
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"faceCapture\"\nContent-Type: application/json", json),
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"facePicture1\"; filename=\"facePicture1.jpg\"\nContent-Type: image/jpeg", Face(2)),
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"facePicture2\"; filename=\"facePicture2.jpg\"\nContent-Type: image/jpeg", staffFace));
        await harness.PostAsync(body);

        await harness.RunAsync();

        var total = harness.Store.Total();
        Assert.Equal((2, 1, 1), (total.Capture, total.UniqueVisitor, total.StaffMatch));
        Assert.Equal(2, Assert.Single(harness.Writer.Events).Value.Event.FaceCount);
        // 第一张是新访客；第二张的员工命中只进计数，不覆盖这条抓拍的判定（与 Capture 评估只看第一张同一口径）。
        Assert.Equal(EvaluationDecision.NewVisitor, Assert.Single(harness.Store.Evaluations).Decision);
    }

    [Fact]
    public async Task Match重复报文_每条都按Capture入库并标成重复_只比对计数一次_只写一行评估()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);
        var body = Capture(Face(2), 1);
        await harness.PostAsync(body);
        await harness.PostAsync(body, Now.AddSeconds(5));
        await harness.PostAsync(body, Now.AddSeconds(10));

        await harness.RunAsync();

        Assert.Equal((1, 1), (harness.Store.Total().Capture, harness.Store.Total().UniqueVisitor));
        Assert.Single(harness.Engine.Searches);
        var rows = harness.Writer.Events.Values.OrderBy(v => v.Event.ReceivedAtUtc).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].DuplicateOf);
        Assert.All(rows.Skip(1), row => Assert.Equal(rows[0].Event.CaptureId, row.DuplicateOf));
        Assert.Equal(rows[0].Event.CaptureId, Assert.Single(harness.Store.Evaluations).CaptureId);
        // 存图口径与 Capture 相同：入库层在写事件行时才判出重复，所以每条重复报文的图仍各存一份。
        Assert.Equal(6, harness.Writer.Images.Count);
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task Match记录_名单命中任务时间与事件ReceivedAtUtc完全相同_摄像机相同()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true, unwelcome: true)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        // 带亚毫秒的接收时间：两边必须来自同一个值，主 API 才能按「摄像机 + 完全相同时间」关联上。
        await harness.PostAsync(Capture(listedFace, 1), Now.AddTicks(7_654_321));

        await harness.RunAsync();

        var task = Assert.Single(harness.Store.Tasks);
        var matchEvent = Assert.Single(harness.Writer.Events).Value.Event;
        Assert.Equal(task.CapturedAtUtc.Ticks, matchEvent.ReceivedAtUtc.Ticks);
        Assert.Equal(task.CameraId, matchEvent.CameraId);
        Assert.Equal(EvaluationDecision.NewVisitor, Assert.Single(harness.Store.Evaluations).Decision);
    }

    [Fact]
    public async Task Match下不再另做Capture的评估与名单检查_每条只搜一次_只建一个任务()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(evaluate: true, staff: true, unwelcome: true, allowedSubjects: "仅内部测试员工")]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        await harness.PostAsync(Capture(listedFace, 1));

        await harness.RunAsync();

        Assert.Single(harness.Engine.Searches);
        Assert.Single(harness.Store.Tasks);
        Assert.Single(harness.Hits.Saved);
        Assert.Single(harness.Store.Evaluations);
    }

    [Fact]
    public async Task 处理中崩溃遗留的孤儿bin_按现有保留策略清理()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true)]);
        var orphanDirectory = Path.Combine(harness.Factory.RawRoot, "2026-10-01", "cam-front-01");
        Directory.CreateDirectory(orphanDirectory);
        var orphan = Path.Combine(orphanDirectory, "20261001T000000000Z_" + new string('a', 32) + ".bin");
        File.WriteAllBytes(orphan, [1, 2, 3]);
        harness.Factory.Time.SetUtcNow(Now);

        var cleaner = harness.Factory.GetService<CaptureStorageCleaner>();
        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(orphan));
    }

    // ------------------------------------------------------------------ 不受欢迎人员名单

    [Fact]
    public async Task 名单命中_建待复核任务_存图到私有前缀_通知时间留空_任务不含文案()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true, unwelcome: true)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), "S1001,S2002", Now.UtcDateTime.AddDays(30));
        await harness.PostAsync(Capture(listedFace, 1));

        await harness.RunAsync();

        var task = Assert.Single(harness.Store.Tasks);
        Assert.Equal(32, task.TaskId.Length);
        Assert.Equal(("S1001", "cam-front-01", new string('e', 32), 99m), (task.StoreCode, task.CameraId, task.EntryId, task.Score));
        Assert.Equal(Now.UtcDateTime, task.CapturedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddDays(7), task.ImageDeleteAtUtc);
        Assert.StartsWith("face-ingest/unwelcome-hits/S1001/2026/10/12/cam-front-01/", task.ImageObjectKey, StringComparison.Ordinal);
        var (image, key) = Assert.Single(harness.Hits.Saved);
        Assert.Equal(task.ImageObjectKey, key);
        Assert.Equal(listedFace, image.Data.ToArray());
        // 任务记录里只有结构化字段，没有任何面向店员的提示文案（文案由主 API 发布通知时固定生成）。
        Assert.Equal(
            ["CameraId", "CapturedAtUtc", "EntryId", "ImageDeleteAtUtc", "ImageObjectKey", "Score", "StoreCode", "TaskId"],
            typeof(MatchTaskRecord).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        // 命中的抓拍同样走一次搜索（三个库），且仍按去重口径计数；本地照样一个文件不留。
        var search = Assert.Single(harness.Engine.Searches);
        Assert.Equal(3, search.GroupIds.Count);
        Assert.Contains(FaceGroups.Unwelcome, search.GroupIds);
        Assert.Equal(1, harness.Store.Total().UniqueVisitor);
        AssertStoredLikeCapture(harness);
        // 原图照 Capture 留存，命中图另存在 unwelcome-hits 前缀（复核流程用）。
        Assert.Equal(2, harness.Images.Calls.Count(call => call.StartsWith("image:", StringComparison.Ordinal)));
        var matchEvent = Assert.Single(harness.Writer.Events).Value.Event;
        Assert.Equal(task.CapturedAtUtc, matchEvent.ReceivedAtUtc);
        Assert.Equal(task.CameraId, matchEvent.CameraId);
    }

    [Fact]
    public async Task 名单命中后计数写失败_任务已建_不重试也不重复存命中图()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        harness.Store.ApplyFailuresRemaining = 1;
        await harness.PostAsync(Capture(listedFace, 1));

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await harness.RunAsync());
        harness.Factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await harness.RunAsync());

        // 任务在写计数之前已建好：计数写失败只少计这一条，不重试整条，所以命中图只存一次、任务只有一条。
        Assert.Single(harness.Store.Tasks);
        Assert.Single(harness.Hits.Saved);
        Assert.Empty(harness.Store.Buckets);
    }

    [Fact]
    public async Task 名单不命中的各种情形_不建任务()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, unwelcome: true)],
            configure: settings => settings["FaceMatch:Stores:S1001:UnwelcomeThreshold"] = "99.5");
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));

        // 1. 分数 99 低于该店覆盖后的名单阈值 99.5。
        await harness.PostAsync(Capture(listedFace, 1));
        await harness.RunAsync();
        Assert.Empty(harness.Store.Tasks);
        Assert.Empty(harness.Hits.Saved);
        Assert.Single(harness.Engine.Searches);
        Assert.Equal(1, harness.Store.Total().Capture);
        AssertStoredLikeCapture(harness);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("other-store")]
    [InlineData("no-entry")]
    public async Task 条目过期_限定其它门店_或查不到条目_命中也不建任务(string scenario)
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        if (scenario == "expired")
        {
            harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddSeconds(-1));
        }
        else if (scenario == "other-store")
        {
            harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), "[\"S2002\",\"S3003\"]", Now.UtcDateTime.AddDays(30));
        }

        await harness.PostAsync(Capture(listedFace, 1));
        await harness.RunAsync();

        Assert.Empty(harness.Store.Tasks);
        Assert.Empty(harness.Hits.Saved);
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 档案没有启用名单时_搜索不带名单库_命中也不建任务()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true, unwelcome: false)]);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, listedFace);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        await harness.PostAsync(Capture(listedFace, 1));

        await harness.RunAsync();

        Assert.DoesNotContain(FaceGroups.Unwelcome, Assert.Single(harness.Engine.Searches).GroupIds);
        Assert.Empty(harness.Store.Tasks);
    }

    [Fact]
    public async Task 员工命中优先_不触发名单()
    {
        var face = Face(7);
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);
        harness.Engine.Enroll(FaceGroups.Staff, face);
        var personId = harness.Engine.Enroll(FaceGroups.Unwelcome, face);
        harness.Store.Entries[personId] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        await harness.PostAsync(Capture(face, 1));

        await harness.RunAsync();

        Assert.Empty(harness.Store.Tasks);
        Assert.Equal(1, harness.Store.Total().StaffMatch);
    }

    [Theory]
    [InlineData("S1,S2", "S2", true)]
    [InlineData("[\"S1\", \"S2\"]", "s2", true)]
    [InlineData("S1;S2|S3", "S3", true)]
    [InlineData("S1,S2", "S9", false)]
    [InlineData("", "S9", true)]
    [InlineData(null, "S9", true)]
    public void StoreScope解析容错(string? scope, string store, bool expected)
        => Assert.Equal(expected, FaceMatchPipeline.StoreScopeAllows(scope, store));

    // ------------------------------------------------------------------ 营业日与访客库时间（与进程时区无关）

    [Theory]
    [InlineData("Australia/Brisbane", "2026-10-12T20:30:00Z", "20261013", "2026-10-13T16:00:00Z")]
    [InlineData("Australia/Brisbane", "2026-10-12T13:59:59Z", "20261012", "2026-10-12T16:00:00Z")]
    [InlineData("Australia/Brisbane", "2026-10-12T14:00:00Z", "20261013", "2026-10-13T16:00:00Z")]
    [InlineData("America/Los_Angeles", "2026-10-12T05:30:00Z", "20261011", "2026-10-12T09:00:00Z")]
    [InlineData("America/Los_Angeles", "2026-11-01T08:30:00Z", "20261101", "2026-11-02T10:00:00Z")]
    [InlineData("Australia/Sydney", "2026-10-03T10:00:00Z", "20261003", "2026-10-03T16:00:00Z")]
    public async Task 营业日取门店本地日期_DeleteAfterUtc为次日0200本地对应的UTC(string timeZoneId, string receivedAt, string expectedDate, string expectedDeleteAfter)
    {
        var received = DateTimeOffset.Parse(receivedAt, System.Globalization.CultureInfo.InvariantCulture);
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true, timeZoneId: timeZoneId)]);
        await harness.PostAsync(Capture(Face(2)), received);

        await harness.RunAsync();

        var group = Assert.Single(harness.Store.VisitGroups);
        Assert.Equal($"vS1001_{expectedDate}", group.GroupId);
        Assert.Equal(DateOnly.ParseExact(expectedDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), group.BusinessDate);
        Assert.Equal(DateTime.Parse(expectedDeleteAfter, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), group.DeleteAfterUtc);
        Assert.Equal(DateTimeKind.Utc, group.DeleteAfterUtc.Kind);
        Assert.Equal([$"vS1001_{expectedDate}"], harness.Engine.CreatedGroups);
    }

    [Fact]
    public void DeleteAfterUtc_夏令时切换日02点不存在或出现两次_仍得到合理的UTC()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        // 悉尼 2026-10-04 02:00 本地不存在（02:00→03:00），顺延到 03:00 AEDT = 前一日 16:00 UTC。
        Assert.Equal(new DateTime(2026, 10, 3, 16, 0, 0, DateTimeKind.Utc), FaceMatchPipeline.BusinessDayDeleteAfterUtc(new DateOnly(2026, 10, 3), sydney));
        // 洛杉矶 2026-03-08 02:00 本地不存在（春季前移），03:00 PDT = 10:00 UTC。
        Assert.Equal(new DateTime(2026, 3, 8, 10, 0, 0, DateTimeKind.Utc), FaceMatchPipeline.BusinessDayDeleteAfterUtc(new DateOnly(2026, 3, 7), la));
        // 洛杉矶 2026-11-01 回拨日：02:00 本地只出现一次（PST），= 10:00 UTC。
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), FaceMatchPipeline.BusinessDayDeleteAfterUtc(new DateOnly(2026, 10, 31), la));
    }

    [Fact]
    public async Task 迟到的积压抓拍_所属营业日访客库已到删除时间_不再建库建人_只计抓拍数()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true)]);
        // 抓拍属于布里斯班 2026-10-12 营业日（DeleteAfter = 10-12T16:00Z）；处理发生在 17:00Z，库早已被主 API 删掉。
        await harness.PostAsync(Capture(Face(2)), Now);
        harness.Factory.Time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));

        await harness.RunAsync();

        Assert.Empty(harness.Engine.CreatedGroups);
        Assert.Empty(harness.Engine.CreatedPersons);
        Assert.Empty(harness.Store.VisitGroups);
        var total = harness.Store.Total();
        Assert.Equal((1, 0, 0), (total.Capture, total.UniqueVisitor, total.Repeat));
        AssertStoredLikeCapture(harness);
    }

    [Fact]
    public async Task 同一营业日同一家店只建一次库_跨营业日各建各的()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(dedup: true, staff: true)]);
        await harness.PostAsync(Capture(Face(2), 1), Now);
        await harness.PostAsync(Capture(Face(3), 2), Now.AddMinutes(1));
        harness.Factory.Time.SetUtcNow(Now.AddMinutes(2));
        await harness.RunAsync();
        await harness.PostAsync(Capture(Face(4), 3), Now.AddHours(24));
        await harness.RunAsync();

        Assert.Equal(["vS1001_20261012", "vS1001_20261013"], harness.Engine.CreatedGroups);
        Assert.Equal(2, harness.Store.VisitGroups.Count);
    }

    // ------------------------------------------------------------------ 日志不含敏感信息

    [Fact]
    public async Task 日志与health不含密钥_签名_图片_对象键_PersonId_走真实腾讯云客户端()
    {
        var listedFace = Face(7);
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, dedup: true, unwelcome: true)],
            realTencentClient: true);
        // 真实客户端路径：先建库（默认空响应），搜索返回名单命中。
        var listedPerson = "listed-person-id-0001";
        harness.Store.Entries[listedPerson] = new UnwelcomeEntryInfo(new string('e', 32), null, Now.UtcDateTime.AddDays(30));
        harness.Http.EnqueueJson("""{"Response":{"RequestId":"r-group"}}""");
        harness.Http.EnqueueJson($$$"""{"Response":{"ResultsReturnsByGroup":[{"RetCode":0,"GroupCandidates":[{"GroupId":"unwelcome","Candidates":[{"PersonId":"{{{listedPerson}}}","Score":97.5}]}]}],"RequestId":"r-search"}}""");
        harness.Http.EnqueueJson("""{"Response":{"RequestId":"r-person"}}""");
        await harness.PostAsync(Capture(listedFace, 1));

        await harness.RunAsync();

        Assert.Single(harness.Store.Tasks);
        Assert.Equal(3, harness.Http.RequestCount);
        AssertLogsClean(harness, extraSecrets: [listedPerson, Convert.ToBase64String(listedFace)]);
        var health = await harness.Client.GetStringAsync("/health");
        Assert.DoesNotContain(FakeSecretKey, health, StringComparison.Ordinal);
        Assert.DoesNotContain(listedPerson, health, StringComparison.Ordinal);
        // 日志里应当看得到正常的结果枚举与计数。
        Assert.Contains(harness.Factory.LogEntries, log => log.Message.Contains("mode=Match", StringComparison.Ordinal) && log.Message.Contains("unwelcomeTasks=1", StringComparison.Ordinal));
    }

    /// <summary>与 Capture 同一留存：每条报文都还在（.json + .bin）且已写 .processed，没有 .retry / .failed。</summary>
    private static void AssertStoredLikeCapture(MatchHarness harness)
    {
        var basePaths = P1TestSupport.CaptureBasePaths(harness.Factory).ToList();
        Assert.NotEmpty(basePaths);
        foreach (var basePath in basePaths)
        {
            Assert.True(File.Exists(basePath + ".json"), "metadata kept");
            Assert.True(File.Exists(basePath + ".bin"), "raw body kept");
            Assert.True(File.Exists(basePath + ".processed"), "processed marker written");
            Assert.False(File.Exists(basePath + ".retry"));
            Assert.False(File.Exists(basePath + ".failed"));
        }
    }

    private static void AssertLogsClean(MatchHarness harness, params string[] extraSecrets)
    {
        var secrets = new List<string> { FakeSecretKey, FakeSecretId, harness.Factory.FrontCamera.Token };
        secrets.AddRange(harness.Engine.CreatedPersons.Select(person => person.PersonId));
        secrets.AddRange(harness.Hits.Saved.Select(saved => saved.ObjectKey));
        secrets.AddRange(harness.Hits.Saved.Select(saved => saved.Image.TaskId));
        secrets.AddRange(extraSecrets);
        var allLogs = string.Join('\n', harness.Factory.LogEntries.Select(entry => entry.AllText));
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            Assert.DoesNotContain(secret, allLogs, StringComparison.Ordinal);
        }

        // 签名头、对象键前缀、密钥 ID 前缀也不得出现在任何日志里。
        Assert.DoesNotContain("TC3-HMAC-SHA256", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("unwelcome-hits", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("AKID", allLogs, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }
}
