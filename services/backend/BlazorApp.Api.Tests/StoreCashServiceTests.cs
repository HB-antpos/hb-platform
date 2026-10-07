using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.StoreCash;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 分店现金管理服务（SQLite + 假日结源 + 假对象存储）：现金池口径、日结存档默认与手选、存款多存单与图片转正、
/// 幂等重放、差异原因、T2 的 14 天可见窗口、补录回溯限制、作废时限、期初与盘点。
/// 门店时区为悉尼，固定时钟 2026-10-08 02:00 UTC = 悉尼夏令时 13:00，门店今天是 2026-10-08。
/// </summary>
public sealed class StoreCashServiceTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero));
    private readonly FakeCloseSource _closes = new();
    private readonly FakeCosService _cos = new();

    public StoreCashServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-cash.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        // 六张新表主键都不是自增，可直接 CodeFirst；生产由版本号迁移建表。
        _db.CodeFirst.InitTables<Store, User, UserStore>();
        _db.CodeFirst.InitTables<StoreCashDeposit, StoreCashDepositSlip, StoreCashExpense>();
        _db.CodeFirst.InitTables<StoreCashAttachment, StoreCashCloseSelection, StoreCashBalanceEntry>();
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "g-1", StoreCode = "S001", StoreName = "一号店", TimeZoneId = "Australia/Sydney" },
            new() { StoreGUID = "g-2", StoreCode = "S002", StoreName = "二号店", TimeZoneId = "Australia/Sydney" },
        }).ExecuteCommand();
    }

    // ───────────────────────── 现金池总览 ─────────────────────────

    [Fact]
    public async Task Summary_日结未接入时余额与流入为空_不显示成零()
    {
        _closes.IsConnected = false;
        var service = CreateService();
        await SetOpeningAsync(service, Manager(), Today, 200m);

        var summary = (await service.GetSummaryAsync(Manager(), "S001", default)).Data!;

        Assert.False(summary.DailyCloseConnected);
        Assert.False(summary.OpeningMissing);
        Assert.Null(summary.PoolBalance);
        Assert.Null(summary.InflowTotal);
        Assert.Null(summary.SuggestedDepositAmount);
        Assert.Null(summary.UncoveredCash);
        Assert.Equal(0, summary.UncoveredDayCount);
        Assert.Empty(summary.MissingCloseDates);
    }

    [Fact]
    public async Task Summary_现金池等于期初加日结减存款减支出_并给出未存营业日与逾期()
    {
        var service = CreateService();
        await SetOpeningAsync(service, Finance(), new DateOnly(2026, 10, 2), 50m);
        _closes.Archives.AddRange(new[]
        {
            Close("c1", new DateOnly(2026, 10, 2), 300m),
            Close("c2", new DateOnly(2026, 10, 3), 150m),
            Close("c3", new DateOnly(2026, 10, 3), 100m, device: "POS_2"),
            Close("c4", new DateOnly(2026, 10, 4), 400m),
            Close("c5", new DateOnly(2026, 10, 7), 120m),
            // 期初之前的日结不追溯。
            Close("c0", new DateOnly(2026, 10, 1), 999m),
        });
        // 先存掉 10-02、10-03 两天的现金（550），与当时余额 1,120 差距大，需要说明原因。
        var deposit = await service.CreateDepositAsync(Manager(), DepositRequest(
            "dep-1",
            new DateOnly(2026, 10, 5),
            (new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3)),
            reason: "只存了前两天",
            (550m, new[] { await UploadAsync(service, Manager()) })), default);
        Assert.True(deposit.Success, deposit.Message);
        await CreateExpenseAsync(service, Manager(), "exp-1", StoreCashConstants.ExpenseCategory.Salary, 80m, new DateOnly(2026, 10, 6));
        await CreateExpenseAsync(service, Manager(), "exp-2", StoreCashConstants.ExpenseCategory.Purchase, 20.5m, new DateOnly(2026, 10, 6),
            await UploadAsync(service, Manager()));

        var summary = (await service.GetSummaryAsync(Manager(), "S001", default)).Data!;

        // 50 + (300 + 250 + 400 + 120) − 550 − (80 + 20.5) = 469.5
        Assert.Equal(1070m, summary.InflowTotal);
        Assert.Equal(550m, summary.DepositTotal);
        Assert.Equal(100.5m, summary.ExpenseTotal);
        Assert.Equal(469.5m, summary.PoolBalance);
        Assert.Equal(469.5m, summary.SuggestedDepositAmount);
        // 10-04、10-07 有现金且未被覆盖；最久的 10-04 距今 4 天，超过 3 天算逾期。
        Assert.Equal(2, summary.UncoveredDayCount);
        Assert.Equal(new DateOnly(2026, 10, 4), summary.OldestUncoveredDate);
        Assert.Equal(520m, summary.UncoveredCash);
        Assert.True(summary.DepositOverdue);
        Assert.Equal(new DateOnly(2026, 10, 5), summary.LastDepositDate);
        Assert.Equal(new DateOnly(2026, 10, 7), summary.LatestCloseDate);
        Assert.Equal(Today, summary.AsOfDate);
        // 10-05、10-06 没有任何日结，提示可能缺日结；期初之前的日期不提示。
        Assert.Equal(new[] { new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6) }, summary.MissingCloseDates);
    }

    [Fact]
    public async Task Summary_没有期初时余额不可算_但存款与支出合计照常统计()
    {
        var service = CreateService();
        _closes.Archives.Add(Close("c1", new DateOnly(2026, 10, 7), 100m));
        await CreateExpenseAsync(service, Manager(), "exp-1", StoreCashConstants.ExpenseCategory.Other, 12m, Today);

        var summary = (await service.GetSummaryAsync(Manager(), "S001", default)).Data!;

        Assert.True(summary.OpeningMissing);
        Assert.Null(summary.PoolBalance);
        Assert.Null(summary.InflowTotal);
        Assert.Equal(12m, summary.ExpenseTotal);
        Assert.Equal(1, summary.UncoveredDayCount);
    }

    // ───────────────────────── 日结存档默认与手选 ─────────────────────────

    [Fact]
    public async Task Daily_同设备多份存档默认只取最新一份_手选多份求和并提示区间重叠()
    {
        var service = CreateService();
        var day = new DateOnly(2026, 10, 7);
        _closes.Archives.AddRange(new[]
        {
            Close("early", day, 180m, savedHour: 15, fromHour: 0, toHour: 15),
            Close("late", day, 260m, savedHour: 22, fromHour: 0, toHour: 24),
        });

        var row = (await service.GetDailyAsync(Manager(), "S001", day, day, default)).Data!.Rows.Single();
        var device = row.Devices.Single();
        Assert.Equal(StoreCashConstants.SelectionMode.Default, device.SelectionMode);
        Assert.Equal(260m, device.IncludedCash);
        Assert.Equal(new[] { "late" }, device.Archives.Where(item => item.Included).Select(item => item.CloseId));

        // 手选必须填原因。
        var noReason = await service.SetCloseSelectionAsync(Manager(), Selection(day, "early", "late"), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.InvalidRequest, noReason.ErrorCode);

        var manual = await service.SetCloseSelectionAsync(
            Manager(),
            new CashCloseSelectionRequest
            {
                StoreCode = "S001",
                BusinessDate = day,
                DeviceCode = "POS_1",
                Mode = StoreCashConstants.SelectionMode.Manual,
                CloseIds = new List<string> { "early", "late" },
                Reason = "中午交班点过一次",
            },
            default
        );
        Assert.True(manual.Success, manual.Message);
        Assert.Equal(440m, manual.Data!.IncludedCash);
        Assert.True(manual.Data.SelectionOverlapWarning);
        Assert.Equal("中午交班点过一次", manual.Data.SelectionReason);

        // 现金池与按日明细都按手选结果计入。
        var daily = (await service.GetDailyAsync(Manager(), "S001", day, day, default)).Data!.Rows.Single();
        Assert.Equal(440m, daily.InflowCash);
        Assert.False(daily.Devices.Single().SelectionStale);

        // 手选之后出现更新的存档：不自动切换，只标记需要确认。
        _closes.Archives.Add(Close("night", day, 300m, savedHour: 23));
        var stale = (await service.GetDailyAsync(Manager(), "S001", day, day, default)).Data!.Rows.Single().Devices.Single();
        Assert.True(stale.SelectionStale);
        Assert.Equal(440m, stale.IncludedCash);

        // 恢复默认：写一条留痕记录，改回取最新一份。
        var reset = await service.SetCloseSelectionAsync(
            Manager(),
            new CashCloseSelectionRequest { StoreCode = "S001", BusinessDate = day, DeviceCode = "POS_1", Mode = StoreCashConstants.SelectionMode.Default },
            default
        );
        Assert.True(reset.Success, reset.Message);
        Assert.Equal(300m, reset.Data!.IncludedCash);
        // 手选一条 + 恢复默认一条（缺原因那次没有落库），只有最后一条是当前记录。
        Assert.Equal(2, await _db.Queryable<StoreCashCloseSelection>().CountAsync());
        Assert.Equal(1, await _db.Queryable<StoreCashCloseSelection>().CountAsync(item => item.IsCurrent));
    }

    [Fact]
    public async Task CloseSelection_店长超出回溯范围被拒_财务不受限_日结未接入不能调整()
    {
        var service = CreateService();
        var old = Today.AddDays(-10);
        _closes.Archives.Add(Close("c1", old, 100m));
        var request = new CashCloseSelectionRequest
        {
            StoreCode = "S001",
            BusinessDate = old,
            DeviceCode = "POS_1",
            Mode = StoreCashConstants.SelectionMode.Manual,
            CloseIds = new List<string> { "c1" },
            Reason = "补选",
        };

        Assert.Equal(StoreCashConstants.ErrorCodes.DateOutOfRange, (await service.SetCloseSelectionAsync(Manager(), request, default)).ErrorCode);
        Assert.True((await service.SetCloseSelectionAsync(Finance(), request, default)).Success);

        _closes.IsConnected = false;
        Assert.Equal(
            StoreCashConstants.ErrorCodes.CloseSourceUnavailable,
            (await service.SetCloseSelectionAsync(Finance(), request, default)).ErrorCode
        );
    }

    // ───────────────────────── 存款 ─────────────────────────

    [Fact]
    public async Task Deposit_多张存单各自挂照片_图片转正为私有对象_重复提交按请求号幂等返回()
    {
        var service = CreateService();
        var first = await UploadAsync(service, Manager());
        var second = await UploadAsync(service, Manager());
        var third = await UploadAsync(service, Manager());
        var request = DepositRequest("dep-1", Today, null, null, (300m, new[] { first, second }), (120.55m, new[] { third }));

        var created = await service.CreateDepositAsync(Manager(), request, default);

        Assert.True(created.Success, created.Message);
        var detail = created.Data!;
        Assert.Equal(420.55m, detail.TotalAmount);
        Assert.Equal(2, detail.SlipCount);
        Assert.Equal(3, detail.ImageCount);
        Assert.Equal(new[] { 300m, 120.55m }, detail.Slips.Select(slip => slip.Amount));
        // 列表也带逐张存单摘要（银行对账按存单粒度）。
        var listed = (await service.ListDepositsAsync(Manager(), "S001", null, null, false, 50, 0, default)).Data!.Items.Single();
        Assert.Equal(new[] { 300m, 120.55m }, listed.SlipSummaries.Select(slip => slip.Amount));
        Assert.Equal(new[] { 2, 1 }, listed.SlipSummaries.Select(slip => slip.ImageCount));
        Assert.Equal(new[] { first, second }, detail.Slips[0].Attachments.Select(item => item.AttachmentGuid));
        Assert.All(detail.Slips.SelectMany(slip => slip.Attachments), item => Assert.Contains("cash/S001/", item.Url));
        Assert.True(detail.CanVoid);
        // 三张图都只校验并转正一次，转正目标是私有正式路径；待确认对象随后删除。
        Assert.Equal(3, _cos.Promoted.Count);
        Assert.All(_cos.Promoted, item => Assert.False(item.IsPublic));
        Assert.All(_cos.Promoted, item => Assert.StartsWith("cash/S001/202610/", item.Target));
        Assert.Equal(3, _cos.Deleted.Count(key => key.StartsWith("cash/pending/", StringComparison.Ordinal)));

        // 弱网重试同一次提交：返回同一条，不新增记录，也不再访问对象存储。
        var replay = await service.CreateDepositAsync(Manager(), request, default);
        Assert.True(replay.Success, replay.Message);
        Assert.Equal(detail.DepositGuid, replay.Data!.DepositGuid);
        Assert.Equal(1, await _db.Queryable<StoreCashDeposit>().CountAsync());
        Assert.Equal(3, _cos.Promoted.Count);

        // 已挂单的图片不能再被别的存款使用。
        var reuse = await service.CreateDepositAsync(Manager(), DepositRequest("dep-2", Today, null, null, (10m, new[] { first })), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.AttachmentInvalid, reuse.ErrorCode);
    }

    [Fact]
    public async Task Deposit_合计与现金池余额相差超过阈值必须填原因_阈值内不用()
    {
        var service = CreateService();
        await SetOpeningAsync(service, Manager(), Today.AddDays(-1), 0m);
        _closes.Archives.Add(Close("c1", Today.AddDays(-1), 500m));

        var tooFar = await service.CreateDepositAsync(
            Manager(),
            DepositRequest("dep-1", Today, null, null, (450m, new[] { await UploadAsync(service, Manager()) })),
            default
        );
        Assert.Equal(StoreCashConstants.ErrorCodes.OverrideReasonRequired, tooFar.ErrorCode);
        // 原因校验发生在图片转正之前，没有白白转正。
        Assert.Empty(_cos.Promoted);

        var withinThreshold = await service.CreateDepositAsync(
            Manager(),
            DepositRequest("dep-2", Today, null, null, (485m, new[] { await UploadAsync(service, Manager()) })),
            default
        );
        Assert.True(withinThreshold.Success, withinThreshold.Message);
    }

    [Fact]
    public async Task Deposit_每张存单至少一张照片_别人或别店上传的图片不能用_店长存款日期最多回溯七天()
    {
        var service = CreateService();

        var noPhoto = await service.CreateDepositAsync(Manager(), DepositRequest("dep-1", Today, null, null, (10m, Array.Empty<string>())), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.AttachmentRequired, noPhoto.ErrorCode);

        var othersPhoto = await UploadAsync(service, Finance());
        var stolen = await service.CreateDepositAsync(Manager(), DepositRequest("dep-2", Today, null, null, (10m, new[] { othersPhoto })), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.AttachmentInvalid, stolen.ErrorCode);

        var otherStorePhoto = await UploadAsync(service, Manager("S001", "S002"), "S002");
        var crossStore = await service.CreateDepositAsync(
            Manager("S001", "S002"),
            DepositRequest("dep-3", Today, null, null, (10m, new[] { otherStorePhoto })),
            default
        );
        Assert.Equal(StoreCashConstants.ErrorCodes.AttachmentInvalid, crossStore.ErrorCode);

        var tooOld = await service.CreateDepositAsync(
            Manager(),
            DepositRequest("dep-4", Today.AddDays(-8), null, null, (10m, new[] { await UploadAsync(service, Manager()) })),
            default
        );
        Assert.Equal(StoreCashConstants.ErrorCodes.DateOutOfRange, tooOld.ErrorCode);

        var future = await service.CreateDepositAsync(
            Manager(),
            DepositRequest("dep-5", Today.AddDays(1), null, null, (10m, new[] { await UploadAsync(service, Manager()) })),
            default
        );
        Assert.Equal(StoreCashConstants.ErrorCodes.DateOutOfRange, future.ErrorCode);
    }

    [Fact]
    public async Task 越权分店一律拒绝_记录详情对越权账号表现为不存在()
    {
        var service = CreateService();
        var request = DepositRequest("dep-1", Today, null, null, (10m, new[] { await UploadAsync(service, Manager("S002"), "S002") }));
        request.StoreCode = "S002";
        var created = await service.CreateDepositAsync(Manager("S002"), request, default);
        Assert.True(created.Success, created.Message);

        Assert.Equal(StoreCashConstants.ErrorCodes.StoreForbidden, (await service.GetSummaryAsync(Manager(), "S002", default)).ErrorCode);
        Assert.Equal(
            StoreCashConstants.ErrorCodes.StoreForbidden,
            (await service.ListDepositsAsync(Manager(), "S002", null, null, false, 50, 0, default)).ErrorCode
        );
        Assert.Equal(
            StoreCashConstants.ErrorCodes.RecordNotFound,
            (await service.GetDepositAsync(Manager(), created.Data!.DepositGuid, default)).ErrorCode
        );
        // context 只列出账号关联的分店。
        var context = (await service.GetContextAsync(Manager(), default)).Data!;
        Assert.Equal(new[] { "S001" }, context.Stores.Select(store => store.StoreCode));
        Assert.Equal(Today, context.Stores.Single().StoreToday);
    }

    // ───────────────────────── 支出与 T2 ─────────────────────────

    [Fact]
    public async Task T2_店长只看到近十四天_列表详情汇总按日明细作废都过滤_财务全部可见()
    {
        var service = CreateService();
        await SetOpeningAsync(service, Finance(), Today.AddDays(-30), 0m);
        // 超过 7 天的只能由财务补录；店长能否看到只看 14 天窗口，与谁录入无关。
        var recent = await CreateExpenseAsync(service, Finance(), "t2-recent", StoreCashConstants.ExpenseCategory.T2, 500m, Today.AddDays(-13));
        var old = await CreateExpenseAsync(service, Finance(), "t2-old", StoreCashConstants.ExpenseCategory.T2, 700m, Today.AddDays(-14));
        await CreateExpenseAsync(service, Manager(), "salary", StoreCashConstants.ExpenseCategory.Salary, 300m, Today.AddDays(-2));

        var managerList = (await service.ListExpensesAsync(Manager(), "S001", null, null, null, null, false, 50, 0, default)).Data!;
        Assert.Equal(2, managerList.Total);
        Assert.DoesNotContain(managerList.Items, item => item.ExpenseGuid == old.ExpenseGuid);
        var managerT2 = (await service.ListExpensesAsync(Manager(), "S001", null, null, "T2", null, false, 50, 0, default)).Data!;
        Assert.Equal(new[] { recent.ExpenseGuid }, managerT2.Items.Select(item => item.ExpenseGuid));

        Assert.Equal(StoreCashConstants.ErrorCodes.RecordNotFound, (await service.GetExpenseAsync(Manager(), old.ExpenseGuid, default)).ErrorCode);
        Assert.Equal(
            StoreCashConstants.ErrorCodes.RecordNotFound,
            (await service.VoidExpenseAsync(Manager(), old.ExpenseGuid, new CashVoidRequest { Reason = "试试" }, default)).ErrorCode
        );

        var managerSummary = (await service.GetSummaryAsync(Manager(), "S001", default)).Data!;
        Assert.True(managerSummary.T2Restricted);
        Assert.Equal(500m, managerSummary.ExpenseByCategory.Single(item => item.Category == "T2").Amount);
        // 余额用真实支出合计（展示层限制，不改变现金池）。
        Assert.Equal(1500m, managerSummary.ExpenseTotal);

        var oldDay = Today.AddDays(-14);
        var managerDaily = (await service.GetDailyAsync(Manager(), "S001", oldDay, oldDay, default)).Data!.Rows.Single();
        Assert.Equal(0m, managerDaily.ExpenseTotal);

        var financeList = (await service.ListExpensesAsync(Finance(), "S001", null, null, null, null, false, 50, 0, default)).Data!;
        Assert.Equal(3, financeList.Total);
        Assert.True((await service.GetExpenseAsync(Finance(), old.ExpenseGuid, default)).Success);
        var financeSummary = (await service.GetSummaryAsync(Finance(), "S001", default)).Data!;
        Assert.False(financeSummary.T2Restricted);
        Assert.Equal(1200m, financeSummary.ExpenseByCategory.Single(item => item.Category == "T2").Amount);
        var financeDaily = (await service.GetDailyAsync(Finance(), "S001", oldDay, oldDay, default)).Data!.Rows.Single();
        Assert.Equal(700m, financeDaily.ExpenseTotal);
    }

    [Fact]
    public async Task Expense_购物必须有收据_类别与金额校验_店长补录最多七天_财务不受限()
    {
        var service = CreateService();

        var noReceipt = await service.CreateExpenseAsync(Manager(), ExpenseRequest("e1", StoreCashConstants.ExpenseCategory.Purchase, 10m, Today), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.AttachmentRequired, noReceipt.ErrorCode);

        var badCategory = await service.CreateExpenseAsync(Manager(), ExpenseRequest("e2", "Bonus", 10m, Today), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.InvalidRequest, badCategory.ErrorCode);

        var threeDecimals = await service.CreateExpenseAsync(Manager(), ExpenseRequest("e3", StoreCashConstants.ExpenseCategory.Other, 1.005m, Today), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.InvalidRequest, threeDecimals.ErrorCode);

        var tooOld = await service.CreateExpenseAsync(Manager(), ExpenseRequest("e4", StoreCashConstants.ExpenseCategory.T2, 10m, Today.AddDays(-8)), default);
        Assert.Equal(StoreCashConstants.ErrorCodes.DateOutOfRange, tooOld.ErrorCode);

        var edge = await service.CreateExpenseAsync(Manager(), ExpenseRequest("e5", StoreCashConstants.ExpenseCategory.T2, 10m, Today.AddDays(-7)), default);
        Assert.True(edge.Success, edge.Message);

        var financeBackfill = await service.CreateExpenseAsync(Finance(), ExpenseRequest("e6", StoreCashConstants.ExpenseCategory.Other, 10m, Today.AddDays(-40)), default);
        Assert.True(financeBackfill.Success, financeBackfill.Message);
    }

    [Fact]
    public async Task Expense_工资关联员工时收款人取员工姓名_员工不属于该店被拒()
    {
        _db.Insertable(new List<User>
        {
            new() { UserGUID = "emp-1", Username = "alice", FullName = "Alice Wang", Email = "a@example.com" },
            new() { UserGUID = "emp-2", Username = "bob", FullName = "Bob Li", Email = "b@example.com" },
        }).ExecuteCommand();
        _db.Insertable(new UserStore { UserGUID = "emp-1", StoreGUID = "g-1" }).ExecuteCommand();
        _db.Insertable(new UserStore { UserGUID = "emp-2", StoreGUID = "g-2" }).ExecuteCommand();
        var service = CreateService();

        var request = ExpenseRequest("e1", StoreCashConstants.ExpenseCategory.Salary, 200m, Today);
        request.PayeeUserGuid = "emp-1";
        request.PayeeName = "随便写的";
        var created = await service.CreateExpenseAsync(Manager(), request, default);
        Assert.True(created.Success, created.Message);
        Assert.Equal("Alice Wang", created.Data!.PayeeName);

        var wrongStore = ExpenseRequest("e2", StoreCashConstants.ExpenseCategory.Salary, 200m, Today);
        wrongStore.PayeeUserGuid = "emp-2";
        Assert.Equal(StoreCashConstants.ErrorCodes.InvalidRequest, (await service.CreateExpenseAsync(Manager(), wrongStore, default)).ErrorCode);
    }

    // ───────────────────────── 作废 ─────────────────────────

    [Fact]
    public async Task Void_本人二十四小时内可作废_超时或他人需要作废权限_重复作废幂等()
    {
        var service = CreateService();
        var mine = await CreateExpenseAsync(service, Manager(), "e1", StoreCashConstants.ExpenseCategory.Other, 10m, Today);
        var others = await CreateExpenseAsync(service, OtherManager(), "e2", StoreCashConstants.ExpenseCategory.Other, 10m, Today);

        Assert.Equal(
            StoreCashConstants.ErrorCodes.VoidNotAllowed,
            (await service.VoidExpenseAsync(Manager(), others.ExpenseGuid, new CashVoidRequest { Reason = "录错了" }, default)).ErrorCode
        );
        Assert.Equal(
            StoreCashConstants.ErrorCodes.InvalidRequest,
            (await service.VoidExpenseAsync(Manager(), mine.ExpenseGuid, new CashVoidRequest { Reason = " " }, default)).ErrorCode
        );

        var voided = await service.VoidExpenseAsync(Manager(), mine.ExpenseGuid, new CashVoidRequest { Reason = "录错了" }, default);
        Assert.True(voided.Success, voided.Message);
        Assert.Equal(StoreCashConstants.RecordStatus.Voided, voided.Data!.Status);
        Assert.Equal("录错了", voided.Data.VoidReason);
        Assert.False(voided.Data.CanVoid);
        var again = await service.VoidExpenseAsync(Manager(), mine.ExpenseGuid, new CashVoidRequest { Reason = "再点一次" }, default);
        Assert.True(again.Success);
        Assert.Equal("录错了", again.Data!.VoidReason);

        // 作废后不再计入现金池，列表默认也不显示。
        var list = (await service.ListExpensesAsync(Manager(), "S001", null, null, null, null, false, 50, 0, default)).Data!;
        Assert.DoesNotContain(list.Items, item => item.ExpenseGuid == mine.ExpenseGuid);

        // 超过 24 小时，本人也不能再作废；有作废权限的可以。
        _time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(
            StoreCashConstants.ErrorCodes.VoidNotAllowed,
            (await service.VoidExpenseAsync(OtherManager(), others.ExpenseGuid, new CashVoidRequest { Reason = "录错了" }, default)).ErrorCode
        );
        Assert.True((await service.VoidExpenseAsync(Finance(), others.ExpenseGuid, new CashVoidRequest { Reason = "财务核对后作废" }, default)).Success);
    }

    // ───────────────────────── 期初与盘点 ─────────────────────────

    [Fact]
    public async Task Opening_每店一条有效_作废后可重录_盘点留存当时应有余额与差异()
    {
        var service = CreateService();
        var opening = await SetOpeningAsync(service, Manager(), Today.AddDays(-2), 100m);
        _closes.Archives.Add(Close("c1", Today.AddDays(-2), 250m));
        _closes.Archives.Add(Close("c2", Today.AddDays(-1), 80m));

        var duplicate = await service.SetOpeningAsync(
            Manager(),
            new SetCashOpeningRequest { ClientRequestId = "open-2", StoreCode = "S001", EntryDate = Today, Amount = 1m },
            default
        );
        Assert.Equal(StoreCashConstants.ErrorCodes.OpeningExists, duplicate.ErrorCode);

        // 盘点 10-07 结束时：100 + 250 + 80 = 430，实点 425，差 −5。
        var count = await service.CreateCountAsync(
            Manager(),
            new CreateCashCountRequest { ClientRequestId = "count-1", StoreCode = "S001", EntryDate = Today.AddDays(-1), Amount = 425m },
            default
        );
        Assert.True(count.Success, count.Message);
        Assert.Equal(430m, count.Data!.ExpectedAmount);
        Assert.Equal(-5m, count.Data.Difference);
        // 之后又来了日结，已录盘点的应有余额不漂移。
        _closes.Archives.Add(Close("c3", Today.AddDays(-1), 20m, device: "POS_2"));
        var summary = (await service.GetSummaryAsync(Manager(), "S001", default)).Data!;
        Assert.Equal(430m, summary.LastCount!.ExpectedAmount);
        Assert.Equal(450m, summary.PoolBalance);

        Assert.True((await service.VoidEntryAsync(Manager(), opening.EntryGuid, new CashVoidRequest { Reason = "期初点错" }, default)).Success);
        var reopened = await service.SetOpeningAsync(
            Manager(),
            new SetCashOpeningRequest { ClientRequestId = "open-3", StoreCode = "S001", EntryDate = Today.AddDays(-2), Amount = 120m },
            default
        );
        Assert.True(reopened.Success, reopened.Message);
        Assert.Equal(470m, (await service.GetSummaryAsync(Manager(), "S001", default)).Data!.PoolBalance);
    }

    // ───────────────────────── 多店总览 ─────────────────────────

    [Fact]
    public async Task Overview_财务看全部分店_区间统计与当前余额分开算_不可算的店让合计也不可算()
    {
        var service = CreateService();
        await SetOpeningAsync(service, Finance(), new DateOnly(2026, 10, 2), 50m);
        _closes.Archives.AddRange(new[]
        {
            Close("a1", new DateOnly(2026, 10, 2), 300m, expected: 305m),
            Close("a2", new DateOnly(2026, 10, 3), 150m, expected: 150m),
            Close("a3", new DateOnly(2026, 10, 3), 100m, device: "POS_2", expected: 98m),
            Close("b1", new DateOnly(2026, 10, 5), 200m, store: "S002"),
            // 区间外（9 月）的日结不进区间统计。
            Close("old", new DateOnly(2026, 9, 28), 999m),
        });
        var deposit = await service.CreateDepositAsync(Finance(), DepositRequest(
            "dep-1",
            new DateOnly(2026, 10, 5),
            (new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3)),
            "期初现金留店备用",
            (550m, new[] { await UploadAsync(service, Finance()) })), default);
        Assert.True(deposit.Success, deposit.Message);
        await CreateExpenseAsync(service, Finance(), "e1", StoreCashConstants.ExpenseCategory.Salary, 80m, new DateOnly(2026, 10, 6));

        var overview = (await service.GetOverviewAsync(Finance(), null, null, null, default)).Data!;

        // 默认区间：本月 1 日到今天。
        Assert.Equal(new DateOnly(2026, 10, 1), overview.From);
        Assert.Equal(Today, overview.To);
        Assert.False(overview.T2Restricted);
        Assert.Equal(new[] { "S001", "S002" }, overview.Rows.Select(row => row.StoreCode));

        var s001 = overview.Rows[0];
        // 50 + 300 + 250 − 550 − 80 = −30（10-04 之后没有日结，现金被工资花掉了）。
        Assert.Equal(-30m, s001.PoolBalance);
        Assert.Equal(550m, s001.InflowCash);
        Assert.Equal(-3m, s001.CloseVariance);
        Assert.Equal(2, s001.CloseDayCount);
        // 期初 10-02 起、今天之前，10-04 到 10-07 四天没有日结。
        Assert.Equal(4, s001.MissingCloseDayCount);
        Assert.Equal(550m, s001.DepositTotal);
        Assert.Equal(1, s001.DepositCount);
        Assert.Equal(80m, s001.ExpenseTotal);
        Assert.Equal(0, s001.UncoveredDayCount);
        Assert.Equal(new DateOnly(2026, 10, 5), s001.LastDepositDate);

        var s002 = overview.Rows[1];
        Assert.True(s002.OpeningMissing);
        Assert.Null(s002.PoolBalance);
        Assert.Equal(200m, s002.InflowCash);
        Assert.Equal(1, s002.UncoveredDayCount);
        Assert.Equal(new DateOnly(2026, 10, 5), s002.OldestUncoveredDate);

        // S002 余额不可算：合计余额也不可算；可加的金额照常合计。
        Assert.Null(overview.Totals.PoolBalance);
        Assert.Equal(750m, overview.Totals.InflowCash);
        Assert.Equal(550m, overview.Totals.DepositTotal);
        Assert.Equal(80m, overview.Totals.ExpenseByCategory.Single(item => item.Category == "Salary").Amount);
        Assert.Equal(1, overview.Totals.UncoveredDayCount);
    }

    [Fact]
    public async Task Overview_店长只看关联分店_越权分店被忽略_T2只计窗口内_区间校验()
    {
        var service = CreateService();
        await CreateExpenseAsync(service, Finance(), "t2-old", StoreCashConstants.ExpenseCategory.T2, 700m, Today.AddDays(-20));
        await CreateExpenseAsync(service, Finance(), "t2-new", StoreCashConstants.ExpenseCategory.T2, 100m, Today.AddDays(-1));

        var manager = (await service.GetOverviewAsync(Manager(), Today.AddDays(-30), Today, new[] { "S001", "S002" }, default)).Data!;
        Assert.True(manager.T2Restricted);
        var row = Assert.Single(manager.Rows);
        Assert.Equal("S001", row.StoreCode);
        Assert.Equal(800m, row.ExpenseTotal);
        Assert.Equal(100m, row.ExpenseByCategory.Single(item => item.Category == "T2").Amount);

        var onlyForbidden = (await service.GetOverviewAsync(Manager(), null, null, new[] { "S002" }, default)).Data!;
        Assert.Empty(onlyForbidden.Rows);

        Assert.Equal(
            StoreCashConstants.ErrorCodes.InvalidRequest,
            (await service.GetOverviewAsync(Finance(), Today.AddDays(-93), Today, null, default)).ErrorCode
        );
        Assert.Equal(
            StoreCashConstants.ErrorCodes.InvalidRequest,
            (await service.GetOverviewAsync(Finance(), Today, null, null, default)).ErrorCode
        );
    }

    // ───────────────────────── 核对标记 ─────────────────────────

    [Fact]
    public async Task Review_需要作废权限_存疑必须写原因_可按状态筛选_清除后回到未核_作废后不能再核()
    {
        _db.Insertable(new User { UserGUID = "u-fin", Username = "finance", FullName = "财务小王", Email = "f@example.com" }).ExecuteCommand();
        var service = CreateService();
        var expense = await CreateExpenseAsync(service, Manager(), "e1", StoreCashConstants.ExpenseCategory.Other, 30m, Today);
        Assert.False(expense.CanReview);

        Assert.Equal(
            StoreCashConstants.ErrorCodes.StoreForbidden,
            (await service.ReviewExpenseAsync(Manager(), expense.ExpenseGuid, new CashExpenseReviewRequest { ReviewStatus = "Reviewed" }, default)).ErrorCode
        );
        Assert.Equal(
            StoreCashConstants.ErrorCodes.InvalidRequest,
            (await service.ReviewExpenseAsync(Finance(), expense.ExpenseGuid, new CashExpenseReviewRequest { ReviewStatus = "Flagged" }, default)).ErrorCode
        );

        var flagged = await service.ReviewExpenseAsync(
            Finance(),
            expense.ExpenseGuid,
            new CashExpenseReviewRequest { ReviewStatus = "Flagged", Note = "没有收据" },
            default
        );
        Assert.True(flagged.Success, flagged.Message);
        Assert.Equal("Flagged", flagged.Data!.ReviewStatus);
        Assert.Equal("没有收据", flagged.Data.ReviewNote);
        Assert.Equal("财务小王", flagged.Data.ReviewedByName);
        Assert.NotNull(flagged.Data.ReviewedAtUtc);
        Assert.True(flagged.Data.CanReview);

        var onlyFlagged = (await service.ListExpensesAsync(Finance(), "S001", null, null, null, "Flagged", false, 50, 0, default)).Data!;
        Assert.Equal(new[] { expense.ExpenseGuid }, onlyFlagged.Items.Select(item => item.ExpenseGuid));
        Assert.Empty((await service.ListExpensesAsync(Finance(), "S001", null, null, null, "Reviewed", false, 50, 0, default)).Data!.Items);
        Assert.Equal(1, (await service.GetOverviewAsync(Finance(), null, null, null, default)).Data!.Totals.FlaggedExpenseCount);

        var cleared = await service.ReviewExpenseAsync(Finance(), expense.ExpenseGuid, new CashExpenseReviewRequest { ReviewStatus = "None" }, default);
        Assert.Equal("None", cleared.Data!.ReviewStatus);
        Assert.Null(cleared.Data.ReviewedByName);
        Assert.Null(cleared.Data.ReviewNote);

        Assert.True((await service.VoidExpenseAsync(Finance(), expense.ExpenseGuid, new CashVoidRequest { Reason = "重复录入" }, default)).Success);
        Assert.Equal(
            StoreCashConstants.ErrorCodes.InvalidRequest,
            (await service.ReviewExpenseAsync(Finance(), expense.ExpenseGuid, new CashExpenseReviewRequest { ReviewStatus = "Reviewed" }, default)).ErrorCode
        );
    }

    // ───────────────────────── 工具 ─────────────────────────

    private StoreCashService CreateService()
    {
        var attachments = new StoreCashAttachmentService(_db, _cos, NullLogger<StoreCashAttachmentService>.Instance, _time);
        return new StoreCashService(_db, _closes, attachments, NullLogger<StoreCashService>.Instance, _time);
    }

    private static CashAccess Manager(params string[] stores) =>
        new(
            "u-mgr",
            "店长甲",
            false,
            new HashSet<string>(stores.Length == 0 ? new[] { "S001" } : stores, StringComparer.OrdinalIgnoreCase),
            true,
            true,
            true,
            false
        );

    private static CashAccess OtherManager() =>
        new("u-mgr-2", "店长乙", false, new HashSet<string>(new[] { "S001" }, StringComparer.OrdinalIgnoreCase), true, true, true, false);

    private static CashAccess Finance() =>
        new("u-fin", "财务", true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true, true, true, true);

    private static CashCloseArchive Close(
        string closeId,
        DateOnly date,
        decimal counted,
        string device = "POS_1",
        int savedHour = 22,
        int fromHour = 0,
        int toHour = 24,
        string store = "S001",
        decimal? expected = null
    )
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new CashCloseArchive(
            store,
            date,
            device,
            closeId,
            midnight.AddHours(fromHour),
            midnight.AddHours(toHour),
            midnight.AddHours(savedHour),
            counted,
            expected ?? counted
        );
    }

    private static CashCloseSelectionRequest Selection(DateOnly date, params string[] closeIds) =>
        new()
        {
            StoreCode = "S001",
            BusinessDate = date,
            DeviceCode = "POS_1",
            Mode = StoreCashConstants.SelectionMode.Manual,
            CloseIds = closeIds.ToList(),
        };

    private async Task<string> UploadAsync(StoreCashService service, CashAccess access, string storeCode = "S001")
    {
        var signature = await service.CreateAttachmentUploadAsync(
            access,
            new CashAttachmentUploadRequest { StoreCode = storeCode, ContentType = "image/jpeg", FileSize = _cos.Bytes.Length },
            default
        );
        Assert.True(signature.Success, signature.Message);
        Assert.Equal("private", signature.Data!.Headers["x-cos-acl"]);
        // 真实对象存储里元数据的上传人来自签名头；假存储按待确认对象键登记。
        var pendingKey = await _db.Queryable<StoreCashAttachment>()
            .Where(item => item.AttachmentGuid == signature.Data.AttachmentGuid)
            .Select(item => item.PendingObjectKey)
            .FirstAsync();
        _cos.OwnerByKey[pendingKey] = access.UserGuid;
        return signature.Data.AttachmentGuid;
    }

    private static CreateCashDepositRequest DepositRequest(
        string clientRequestId,
        DateOnly depositDate,
        (DateOnly From, DateOnly To)? covered,
        string? reason,
        params (decimal Amount, string[] Attachments)[] slips
    ) =>
        new()
        {
            ClientRequestId = clientRequestId,
            StoreCode = "S001",
            DepositDate = depositDate,
            CoveredFromDate = covered?.From,
            CoveredToDate = covered?.To,
            OverrideReason = reason,
            Slips = slips
                .Select(slip => new CashDepositSlipInput { Amount = slip.Amount, AttachmentGuids = slip.Attachments.ToList() })
                .ToList(),
        };

    private static CreateCashExpenseRequest ExpenseRequest(string clientRequestId, string category, decimal amount, DateOnly date) =>
        new()
        {
            ClientRequestId = clientRequestId,
            StoreCode = "S001",
            ExpenseDate = date,
            Category = category,
            Amount = amount,
        };

    private static async Task<CashExpenseDetailDto> CreateExpenseAsync(
        StoreCashService service,
        CashAccess access,
        string clientRequestId,
        string category,
        decimal amount,
        DateOnly date,
        params string[] attachments
    )
    {
        var request = ExpenseRequest(clientRequestId, category, amount, date);
        request.AttachmentGuids = attachments.ToList();
        var result = await service.CreateExpenseAsync(access, request, default);
        Assert.True(result.Success, result.Message);
        return result.Data!;
    }

    private static async Task<CashBalanceEntryDto> SetOpeningAsync(StoreCashService service, CashAccess access, DateOnly date, decimal amount)
    {
        var result = await service.SetOpeningAsync(
            access,
            new SetCashOpeningRequest { ClientRequestId = $"open-{Guid.NewGuid():N}", StoreCode = "S001", EntryDate = date, Amount = amount },
            default
        );
        Assert.True(result.Success, result.Message);
        return result.Data!;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 临时库删除失败不影响断言。
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeCloseSource : ICashDailyCloseSource
    {
        public bool IsConnected { get; set; } = true;
        public List<CashCloseArchive> Archives { get; } = new();

        public Task<IReadOnlyList<CashCloseArchive>> GetArchivesAsync(
            IReadOnlyCollection<string> storeCodes,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CashCloseArchive>>(
            Archives
                .Where(item => storeCodes.Contains(item.StoreCode) && item.BusinessDate >= from && item.BusinessDate <= to)
                .ToList()
        );
    }

    /// <summary>假对象存储：按附件编号记录上传人，元数据与内容都与签发时声明一致；记录转正与删除。</summary>
    private sealed class FakeCosService : TencentCloudUploadService
    {
        public FakeCosService()
            : base(
                Options.Create(new TencentCloudSettings
                {
                    SecretId = "id",
                    SecretKey = "key",
                    BucketName = "bucket",
                    Region = "region",
                }),
                NullLogger<TencentCloudUploadService>.Instance,
                new HttpClient()
            )
        {
            using var image = new Image<Rgba32>(8, 8);
            using var stream = new MemoryStream();
            image.SaveAsJpeg(stream);
            Bytes = stream.ToArray();
        }

        public byte[] Bytes { get; }
        public Dictionary<string, string> OwnerByKey { get; } = new();
        public List<(string Source, string Target, bool IsPublic)> Promoted { get; } = new();
        public List<string> Deleted { get; } = new();

        public override Task<ApiResponse<CosObjectMetadata>> GetObjectMetadataAsync(
            string objectKey,
            CancellationToken cancellationToken = default
        )
        {
            string? owner;
            lock (OwnerByKey)
            {
                owner = OwnerByKey.GetValueOrDefault(objectKey);
            }

            return Task.FromResult(ApiResponse<CosObjectMetadata>.OK(new CosObjectMetadata
            {
                ContentLength = Bytes.Length,
                ContentType = "image/jpeg",
                Owner = owner,
                Kind = "cash",
                DeclaredContentType = "image/jpeg",
                DeclaredFileSize = Bytes.Length,
            }));
        }

        public override Task<ApiResponse<byte[]>> DownloadObjectBytesAsync(
            string objectKey,
            int maximumBytes,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ApiResponse<byte[]>.OK(Bytes));

        public override Task<ApiResponse<bool>> PromoteObjectAsync(
            string sourceObjectKey,
            string targetObjectKey,
            string contentType,
            bool isPublic,
            CancellationToken cancellationToken = default
        )
        {
            lock (Promoted)
            {
                Promoted.Add((sourceObjectKey, targetObjectKey, isPublic));
            }

            return Task.FromResult(ApiResponse<bool>.OK(true));
        }

        public override Task<ApiResponse<bool>> DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default
        )
        {
            lock (Promoted)
            {
                Deleted.Add(objectKey);
            }

            return Task.FromResult(ApiResponse<bool>.OK(true));
        }
    }
}
