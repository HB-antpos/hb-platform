using System.Text.Json;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 工资单本地快照同步（2026-11-17，方案第 12 节）：SlipKey / 对账 / 同步时间规则（纯函数）、同步服务（SQLite + 假 MYOB）、手动同步权限、后台任务开关。
/// 与 MyobPayslipTests 共用夹具（partial）；姓名与金额都是虚构的。
/// </summary>
public sealed partial class MyobPayslipTests
{
    // 悉尼本地 2026-10-29 07:00（夏令时 UTC+11）；即使本机没有时区库退化为 UTC+10，「今天」也仍是 2026-10-29。
    private static readonly DateTime Today = new(2026, 10, 29);

    private sealed class MutableTime(DateTime utcNow) : TimeProvider
    {
        private DateTime _now = utcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    private sealed class ThrowingTokens(Exception error) : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => throw error;

        public void Invalidate(string connectionGuid) { }
    }

    private async Task<MyobPayslipSyncState> StateAsync(string connection) =>
        await _db.Queryable<MyobPayslipSyncState>().Where(item => item.ConnectionGuid == connection).FirstAsync();

    private async Task<List<MyobPayslipSnapshot>> SnapshotsAsync(string connection) =>
        await _db.Queryable<MyobPayslipSnapshot>().Where(row => row.ConnectionGuid == connection).ToListAsync();

    // ───────────────────────── 识别同一张单 / 内容哈希 ─────────────────────────

    [Fact]
    public void 快照键_员工卡加支付日加周期加金额_员工卡不分大小写_金额或周期不同就是另一张单()
    {
        var slip = Slip(CardJane, "Jane Sample", "2026-10-23");
        var key = MyobPayslipSnapshotRules.ComputeSlipKey(slip);

        Assert.Equal(64, key.Length);
        Assert.Equal(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { EmployeeUid = CardJane.ToUpperInvariant() }));
        // 姓名、明细变了仍是同一张单（靠内容哈希判断是否要改写）。
        Assert.Equal(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { EmployeeName = "Jane S.", Lines = [] }));

        Assert.NotEqual(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { GrossPay = 1000.01m })); // 更正单：金额不同
        Assert.NotEqual(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { NetPay = 799m }));
        Assert.NotEqual(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { PeriodStart = slip.PeriodStart!.Value.AddDays(-7) }));
        Assert.NotEqual(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { PaymentDate = slip.PaymentDate!.Value.AddDays(1) }));
        Assert.NotEqual(key, MyobPayslipSnapshotRules.ComputeSlipKey(slip with { EmployeeUid = CardTom }));
    }

    [Fact]
    public void 内容哈希_明细或姓名变了就变_同样内容不变()
    {
        var slip = Slip(CardJane, "Jane Sample", "2026-10-23");
        var hash = MyobPayslipSnapshotRules.ComputeContentHash(slip);
        Assert.Equal(hash, MyobPayslipSnapshotRules.ComputeContentHash(Slip(CardJane, "Jane Sample", "2026-10-23")));
        Assert.NotEqual(hash, MyobPayslipSnapshotRules.ComputeContentHash(slip with { EmployeeName = "Jane Sample Jr" }));
        Assert.NotEqual(hash, MyobPayslipSnapshotRules.ComputeContentHash(slip with { Lines = [slip.Lines[0] with { Amount = 1001m }] }));
    }

    [Fact]
    public void 快照行与记录互转_明细往返一致_JSON损坏按没有明细处理_超长文本被截断_没有支付日的记录不落库()
    {
        var slip = Slip(CardJane, "Jane Sample", "2026-10-23");
        var row = MyobPayslipSnapshotRules.ToSnapshot(Inv, slip, Now)!;
        Assert.Equal(Inv, row.ConnectionGuid);
        Assert.Equal(new DateTime(2026, 10, 23), row.PaymentDate);
        Assert.Equal(24m, row.HourlyRate); // 时薪与 Reference Number 按产品决定明文保存
        Assert.Equal("1263", row.ChequeNumber);
        Assert.Equal(slip.Lines, MyobPayslipSnapshotRules.ToRecord(row).Lines);
        Assert.Equal(slip, MyobPayslipSnapshotRules.ToRecord(row) with { Lines = slip.Lines });

        row.LinesJson = "{not json";
        Assert.Empty(MyobPayslipSnapshotRules.ToRecord(row).Lines);

        var longName = MyobPayslipSnapshotRules.ToSnapshot(Inv, slip with { EmployeeName = new string('x', 500) }, Now)!;
        Assert.Equal(200, longName.EmployeeName!.Length); // 超过列长会让整批保存失败

        Assert.Null(MyobPayslipSnapshotRules.ToSnapshot(Inv, slip with { PaymentDate = null }, Now));
    }

    [Fact]
    public void 快照表实体_没有年薪生日银行TFN列_只多出时薪与支票号()
    {
        var names = typeof(MyobPayslipSnapshot).GetProperties().Select(property => property.Name).ToList();
        foreach (var banned in new[] { "AnnualSalary", "DateOfBirth", "Birth", "Bsb", "Account", "Tfn", "Classification" })
        {
            Assert.DoesNotContain(names, name => name.Contains(banned, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("HourlyRate", names);
        Assert.Contains("ChequeNumber", names);
    }

    // ───────────────────────── 窗口对账 ─────────────────────────

    private static MyobPayslipSnapshot Row(string card, string date, decimal gross = 1000m, string name = "Jane Sample", DateTime? now = null) =>
        MyobPayslipSnapshotRules.ToSnapshot(Inv, Slip(card, name, date, gross, gross - 200m), now ?? Now)!;

    [Fact]
    public void 对账_新增_更正_撤销_恢复_内容没变的行不动()
    {
        var later = Now.AddHours(8);
        var unchanged = Row(CardJane, "2026-10-23");
        var edited = Row(CardTom, "2026-10-23");
        var revoked = Row(CardStranger, "2026-10-23");
        var existing = new List<MyobPayslipSnapshot> { unchanged, edited, revoked };

        var incoming = new List<MyobPayslipSnapshot>
        {
            Row(CardJane, "2026-10-23", now: later), // 内容一样
            Row(CardTom, "2026-10-23", name: "Tom Sample Renamed", now: later), // 同一张单，姓名变了
            Row(CardJaneAus, "2026-10-23", now: later), // 新增
        };
        var plan = MyobPayslipSnapshotRules.Reconcile(existing, incoming, later);

        Assert.Equal([CardJaneAus], plan.Inserts.Select(row => row.EmployeeUid).ToArray());
        Assert.Equal([CardTom], plan.Updates.Select(row => row.EmployeeUid).ToArray());
        Assert.Equal("Tom Sample Renamed", edited.EmployeeName);
        Assert.Equal(later, edited.UpdatedAt);
        Assert.Equal([CardStranger], plan.Removals.Select(row => row.EmployeeUid).ToArray());
        Assert.Equal(later, revoked.RemovedAt);
        Assert.Equal(Now, unchanged.UpdatedAt); // 没变的行不改 UpdatedAt，也不进更新列表
        Assert.Null(unchanged.RemovedAt);

        // 撤销的单又出现了（重发）：恢复，清空 RemovedAt。已经标记找不到的行，再次对账也不会重复标记。
        var again = MyobPayslipSnapshotRules.Reconcile(existing, [Row(CardStranger, "2026-10-23", now: later.AddHours(1))], later.AddHours(1));
        Assert.Contains(again.Updates, row => row.EmployeeUid == CardStranger);
        Assert.Null(revoked.RemovedAt);
        Assert.DoesNotContain(again.Removals, row => row.EmployeeUid == CardStranger);
    }

    [Fact]
    public void 对账_空结果会把窗口内全部有效行标成找不到_所以调用方只能在完整取回后调用()
    {
        var rows = new List<MyobPayslipSnapshot> { Row(CardJane, "2026-10-23"), Row(CardTom, "2026-10-23") };
        var plan = MyobPayslipSnapshotRules.Reconcile(rows, [], Now);
        Assert.Equal(2, plan.Removals.Count);
        Assert.Empty(plan.Inserts);

        // 已经标记找不到的行不会再被重复标记（RemovedAt 不会被后来的时间覆盖）。
        var second = MyobPayslipSnapshotRules.Reconcile(rows, [], Now.AddHours(8));
        Assert.Empty(second.Removals);
        Assert.All(rows, row => Assert.Equal(Now, row.RemovedAt));
    }

    // ───────────────────────── 同步时间规则 ─────────────────────────

    private static readonly TimeZoneInfo FixedZone = TimeZoneInfo.CreateCustomTimeZone("T+10", TimeSpan.FromHours(10), "T+10", "T+10");

    [Theory]
    [InlineData("2026-11-17 00:00", "2026-11-17 00:00")] // 整点当刻就算
    [InlineData("2026-11-17 07:59", "2026-11-17 00:00")]
    [InlineData("2026-11-17 08:00", "2026-11-17 08:00")]
    [InlineData("2026-11-17 11:59", "2026-11-17 08:00")]
    [InlineData("2026-11-17 12:30", "2026-11-17 12:00")]
    [InlineData("2026-11-17 14:59", "2026-11-17 12:00")]
    [InlineData("2026-11-17 15:00", "2026-11-17 15:00")]
    [InlineData("2026-11-17 17:59", "2026-11-17 15:00")]
    [InlineData("2026-11-17 18:00", "2026-11-17 18:00")]
    [InlineData("2026-11-17 20:59", "2026-11-17 18:00")]
    [InlineData("2026-11-17 21:00", "2026-11-17 21:00")]
    [InlineData("2026-11-17 23:59", "2026-11-17 21:00")] // 「24 点」就是次日 0 点：23:59 还在 21 点那一档
    public void 同步整点_悉尼本地0_8_12_15_18_21点_取不晚于当前的最近一个(string nowLocal, string slotLocal)
    {
        var now = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(nowLocal), FixedZone);
        var slot = MyobPayslipSyncSchedule.LatestSlotUtc(now, FixedZone);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(slotLocal), FixedZone), slot);
    }

    [Theory]
    [InlineData("2026-04-05")] // 悉尼夏令时结束日（3:00 拨回 2:00）
    [InlineData("2026-10-04")] // 悉尼夏令时开始日（2:00 拨到 3:00）
    [InlineData("2026-11-17")]
    public void 同步整点_跨夏令时当天也始终落在整点上_且距离当前不超过9小时(string day)
    {
        var zone = MyobPayslipSyncSchedule.ResolveZone();
        var start = DateTime.SpecifyKind(DateTime.Parse(day).AddHours(-14), DateTimeKind.Utc);
        for (var minute = 0; minute <= 48 * 60; minute += 7)
        {
            var now = start.AddMinutes(minute);
            var slot = MyobPayslipSyncSchedule.LatestSlotUtc(now, zone);
            Assert.True(slot <= now, $"{now:O} → {slot:O}");
            // 最长间隔是 0 点 → 8 点；夏令时结束那天多一个小时（25 小时的一天），所以是 9 小时。
            Assert.True(now - slot <= TimeSpan.FromHours(9), $"{now:O} → {slot:O}");
            var local = TimeZoneInfo.ConvertTimeFromUtc(slot, zone);
            Assert.Equal(0, local.Minute);
            Assert.Contains(local.Hour, MyobPayslipSyncSchedule.SlotHours);
        }
    }

    [Fact]
    public void 是否回填_已对账的最早日期不晚于今天往前730天才算回填完整()
    {
        Assert.True(MyobPayslipSyncSchedule.NeedsBackfill(null, Today));
        Assert.True(MyobPayslipSyncSchedule.NeedsBackfill(new MyobPayslipSyncState(), Today));
        Assert.False(MyobPayslipSyncSchedule.NeedsBackfill(new MyobPayslipSyncState { CoveredFrom = Today.AddDays(-730) }, Today));
        Assert.False(MyobPayslipSyncSchedule.NeedsBackfill(new MyobPayslipSyncState { CoveredFrom = Today.AddDays(-800) }, Today));
        Assert.True(MyobPayslipSyncSchedule.NeedsBackfill(new MyobPayslipSyncState { CoveredFrom = Today.AddDays(-729) }, Today));
        // 回填那天之后每过一天，目标起点后移一天，已覆盖的起点仍然不晚于它。
        Assert.False(MyobPayslipSyncSchedule.NeedsBackfill(new MyobPayslipSyncState { CoveredFrom = Today.AddDays(-730) }, Today.AddDays(30)));
    }

    [Fact]
    public void 是否到期_整点之后没成功同步就补跑_租约有效不抢_失败后15分钟内不重试_没回填完一律要同步()
    {
        var slot = new DateTime(2026, 11, 17, 2, 0, 0, DateTimeKind.Utc);
        var now = slot.AddMinutes(30);
        var today = new DateTime(2026, 11, 17);
        MyobPayslipSyncState Healthy(DateTime lastSuccess) => new() { LastSuccessAt = lastSuccess, LastAttemptAt = lastSuccess, CoveredFrom = today.AddDays(-730) };

        Assert.True(MyobPayslipSyncSchedule.IsDue(null, slot, now, today)); // 从没同步过
        Assert.True(MyobPayslipSyncSchedule.IsDue(Healthy(slot.AddMinutes(-1)), slot, now, today)); // 整点前成功的，整点后要再同步
        Assert.False(MyobPayslipSyncSchedule.IsDue(Healthy(slot), slot, now, today)); // 整点当刻已成功
        Assert.False(MyobPayslipSyncSchedule.IsDue(Healthy(slot.AddMinutes(10)), slot, now, today));

        // 没回填完：即使最近刚成功过也要同步（回填本身就是一次成功同步，之后 CoveredFrom 会补上）。
        Assert.True(MyobPayslipSyncSchedule.IsDue(
            new MyobPayslipSyncState { LastSuccessAt = slot.AddMinutes(10) }, slot, now, today));

        // 租约有效 → 别人在同步，不抢；过期 → 可接手。
        var leased = Healthy(slot.AddMinutes(-1));
        leased.LeaseUntil = now.AddMinutes(5);
        Assert.False(MyobPayslipSyncSchedule.IsDue(leased, slot, now, today));
        leased.LeaseUntil = now.AddMinutes(-1);
        Assert.True(MyobPayslipSyncSchedule.IsDue(leased, slot, now, today));

        // 刚失败（15 分钟内）不重试；之后再试。
        var failed = Healthy(slot.AddMinutes(-60));
        failed.LastError = "MYOB 工资单报表接口返回 503";
        failed.LastAttemptAt = now.AddMinutes(-5);
        Assert.False(MyobPayslipSyncSchedule.IsDue(failed, slot, now, today));
        failed.LastAttemptAt = now.AddMinutes(-16);
        Assert.True(MyobPayslipSyncSchedule.IsDue(failed, slot, now, today));
    }

    // ───────────────────────── 同步服务 ─────────────────────────

    [Fact]
    public async Task 首次同步_整段回填24个月_落库_进度推到今天往前730天_只读MYOB_租约已释放()
    {
        GivenSlips();
        _api.Slips[Inv] = [.. _api.Slips[Inv], Slip(CardTom, "Tom Sample", "2024-10-01")]; // 窗口之外
        var outcomes = await SyncAllAsync();

        Assert.All(outcomes, outcome => Assert.Equal(MyobPayslipSyncStatuses.Synced, outcome.Status));
        Assert.All(outcomes, outcome => Assert.True(outcome.Backfilled));
        var inv = outcomes.Single(item => item.ConnectionGuid == Inv);
        Assert.Equal(4, inv.Inserted); // Jane、Tom、Stranger、Ignored（忽略名单是读取时才过滤，库里照存）
        Assert.Equal(1, outcomes.Single(item => item.ConnectionGuid == Aus).Inserted);

        // 一次请求覆盖整段：今天往前 730 天，到今天 + 7 天（含）。
        Assert.Equal(2, _api.Queries.Count);
        Assert.All(_api.Queries, query =>
        {
            Assert.Equal(Today.AddDays(-730), query.PaymentFrom);
            Assert.Equal(Today.AddDays(8), query.PaymentToExclusive);
            Assert.Null(query.EmployeeUid);
        });
        Assert.DoesNotContain(await SnapshotsAsync(Inv), row => row.PaymentDate == new DateTime(2024, 10, 1));

        var state = await StateAsync(Inv);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.Equal(Today.AddDays(-730), state.CoveredFrom);
        Assert.Null(state.LastError);
        Assert.Null(state.LeaseUntil);
        Assert.Null(state.LeaseOwner);
    }

    [Fact]
    public async Task 回填完成之后_只对账最近8周_并且重复同步不产生变化()
    {
        GivenSlips();
        await SyncAllAsync();
        _api.Queries.Clear();

        var outcomes = await SyncAllAsync();
        Assert.All(_api.Queries, query => Assert.Equal(Today.AddDays(-56), query.PaymentFrom));
        Assert.All(outcomes, outcome =>
        {
            Assert.False(outcome.Backfilled);
            Assert.Equal((0, 0, 0), (outcome.Inserted, outcome.Updated, outcome.Removed));
        });
        Assert.Equal(4, (await SnapshotsAsync(Inv)).Count);
    }

    [Fact]
    public async Task 同步_MYOB里撤销的单本地标找不到_列表不再显示_重发后恢复()
    {
        await GivenSyncedSlipsAsync();
        AsAdmin();
        _api.Slips[Inv] = _api.Slips[Inv].Where(slip => slip.EmployeeUid != CardStranger).ToList();
        var outcome = (await SyncAllAsync()).Single(item => item.ConnectionGuid == Inv);
        Assert.Equal(1, outcome.Removed);

        var removed = (await SnapshotsAsync(Inv)).Single(row => row.EmployeeUid == CardStranger);
        Assert.NotNull(removed.RemovedAt); // 不物理删除
        var list = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        Assert.DoesNotContain(list.Rows, row => row.EmployeeUid == CardStranger);
        Assert.Equal(0, list.UnassignedCount);
        // 明细也读不到被撤销的单。
        Assert.Equal(
            MyobPayslipErrorCodes.NotFound,
            (await Service().GetDetailAsync(D(Inv, CardStranger), CancellationToken.None)).ErrorCode);

        _api.Slips[Inv] = [.. _api.Slips[Inv], Slip(CardStranger, "Stan Stranger", "2026-10-23")];
        Assert.Equal(1, (await SyncAllAsync()).Single(item => item.ConnectionGuid == Inv).Updated);
        Assert.Null((await SnapshotsAsync(Inv)).Single(row => row.EmployeeUid == CardStranger).RemovedAt);
        Assert.Contains((await Service().ListAsync(Q(), CancellationToken.None)).Data!.Rows, row => row.EmployeeUid == CardStranger);
    }

    [Fact]
    public async Task 同步_MYOB里更正了明细_本地改写_没变的行不动_金额不同的更正单是另一行()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        await SyncAllAsync(time);
        var before = (await SnapshotsAsync(Inv)).ToDictionary(row => row.EmployeeUid);

        time.Advance(TimeSpan.FromHours(8));
        _api.Slips[Inv] = _api.Slips[Inv].Select(slip => slip.EmployeeUid == CardJane
            ? slip with { Lines = [.. slip.Lines, new MyobPayrollAdviceLine("Bonus", "Wage", null, null, 0m, 0m)] }
            : slip).ToList();
        var outcome = (await SyncAllAsync(time)).Single(item => item.ConnectionGuid == Inv);
        Assert.Equal((0, 1, 0), (outcome.Inserted, outcome.Updated, outcome.Removed));

        var after = (await SnapshotsAsync(Inv)).ToDictionary(row => row.EmployeeUid);
        Assert.Equal(time.GetUtcNow().UtcDateTime, after[CardJane].UpdatedAt);
        Assert.Equal(before[CardJane].FirstSeenAt, after[CardJane].FirstSeenAt);
        Assert.Equal(before[CardTom].UpdatedAt, after[CardTom].UpdatedAt); // 没变的不动

        // 同一周期同一天、金额不同的单（更正单）是另一行，不会覆盖原来那张。
        time.Advance(TimeSpan.FromHours(1));
        _api.Slips[Inv] = [.. _api.Slips[Inv], Slip(CardJane, "Jane Sample", "2026-10-23", 1200m, 950m)];
        await SyncAllAsync(time);
        Assert.Equal(2, (await SnapshotsAsync(Inv)).Count(row => row.EmployeeUid == CardJane));
    }

    [Fact]
    public async Task 同步失败_本地数据原样不动_不会被标成找不到_只记错误_租约释放()
    {
        await GivenSyncedSlipsAsync();
        var lastSuccess = (await StateAsync(Inv)).LastSuccessAt;
        var time = new MutableTime(Now.AddHours(8));
        _api.Errors[Inv] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 503");

        var outcome = (await SyncAllAsync(time)).Single(item => item.ConnectionGuid == Inv);
        Assert.Equal(MyobPayslipSyncStatuses.Failed, outcome.Status);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, outcome.ErrorCode);

        Assert.Equal(4, (await SnapshotsAsync(Inv)).Count(row => row.RemovedAt is null)); // 一张都没丢
        var state = await StateAsync(Inv);
        Assert.Equal(lastSuccess, state.LastSuccessAt); // 成功时间不前进
        Assert.Contains("503", state.LastError, StringComparison.Ordinal);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, state.LastErrorCode);
        Assert.Null(state.LeaseUntil);
        Assert.Equal(time.GetUtcNow().UtcDateTime, state.LastAttemptAt);
    }

    [Fact]
    public async Task 同步失败的错误里不出现金额与姓名_非MYOB异常统一成暂时不可用()
    {
        GivenSlips();
        _api.Errors[Inv] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 500");
        var outcomes = await SyncAllAsync();
        foreach (var outcome in outcomes.Where(item => item.Status == MyobPayslipSyncStatuses.Failed))
        {
            Assert.DoesNotContain("Jane", outcome.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("1000", outcome.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        }

        var connections = await _db.Queryable<MyobConnection>().Where(item => item.ConnectionGuid == Aus).ToListAsync();
        var broken = new MyobPayslipSyncService(_db, _api, new ThrowingTokens(new InvalidOperationException("secret detail: Jane 1000.00")), new FakeTime(Now));
        var result = Assert.Single(await broken.SyncAsync(connections, MyobPayslipSyncTrigger.Scheduled, CancellationToken.None));
        Assert.Equal(MyobPayslipSyncStatuses.Failed, result.Status);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, result.ErrorCode);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", (await StateAsync(Aus)).LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Null((await StateAsync(Aus)).LeaseUntil); // 令牌取不到也要还租约
    }

    [Fact]
    public async Task 保存失败_整个事务回滚_本地原样_错误不回显数据库异常内容()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        await SyncAllAsync(time);
        var before = (await SnapshotsAsync(Inv)).Select(row => (row.SlipKey, row.UpdatedAt, row.EmployeeName)).OrderBy(item => item.SlipKey).ToList();

        // 在改名那一行的更新上埋一个会中止的触发器：同一批里先成功的新增必须一起回滚。
        _db.Ado.ExecuteCommand(
            "CREATE TRIGGER boom BEFORE UPDATE ON MyobPayslipSnapshot WHEN NEW.EmployeeName = 'Boom Secret 1000' BEGIN SELECT RAISE(ABORT, 'boom secret'); END;");
        time.Advance(TimeSpan.FromHours(8));
        _api.Slips[Inv] =
        [
            .. _api.Slips[Inv].Select(slip => slip.EmployeeUid == CardJane ? slip with { EmployeeName = "Boom Secret 1000" } : slip),
            Slip(CardJane, "Jane Sample", "2026-10-16"), // 新增：先于更新执行
        ];
        var outcome = (await SyncAllAsync(time)).Single(item => item.ConnectionGuid == Inv);

        Assert.Equal(MyobPayslipSyncStatuses.Failed, outcome.Status);
        Assert.Equal(MyobPayslipErrorCodes.SyncSaveFailed, outcome.ErrorCode);
        Assert.DoesNotContain("secret", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var after = (await SnapshotsAsync(Inv)).Select(row => (row.SlipKey, row.UpdatedAt, row.EmployeeName)).OrderBy(item => item.SlipKey).ToList();
        Assert.Equal(before, after); // 新增没有留下，改名没有生效
        var state = await StateAsync(Inv);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.Equal(MyobPayslipErrorCodes.SyncSaveFailed, state.LastErrorCode);
        Assert.DoesNotContain("secret", state.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(state.LeaseUntil);
    }

    [Fact]
    public async Task 租约_别处正在同步就标Busy_不请求MYOB_租约过期后可接手()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        await SyncAllAsync(time);
        var calls = _api.Calls;

        var state = await StateAsync(Inv);
        state.LeaseUntil = Now.AddMinutes(5);
        state.LeaseOwner = "another-instance";
        _db.Updateable(state).ExecuteCommand();

        var busy = (await SyncAllAsync(time)).Single(item => item.ConnectionGuid == Inv);
        Assert.Equal(MyobPayslipSyncStatuses.Busy, busy.Status);
        Assert.Equal(calls + 1, _api.Calls); // 只有 Australian 请求了
        Assert.Equal("another-instance", (await StateAsync(Inv)).LeaseOwner); // 别人的租约原样保留

        // 状态里能看到「正在同步」。
        AsAdmin();
        var list = (await Service(time).ListAsync(Q(), CancellationToken.None)).Data!;
        Assert.True(list.Companies.Single(item => item.ConnectionGuid == Inv).Syncing);
        Assert.False(list.Companies.Single(item => item.ConnectionGuid == Aus).Syncing);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(MyobPayslipSyncStatuses.Synced, (await SyncAllAsync(time)).Single(item => item.ConnectionGuid == Inv).Status);
        Assert.Null((await StateAsync(Inv)).LeaseOwner);
    }

    [Fact]
    public async Task 手动同步_60秒内再点就跳过不请求MYOB_过了冷却才再次同步()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        AsAdmin();
        var service = Service(time);

        var first = (await service.SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).Data!;
        Assert.All(first.Companies, item => Assert.Equal(MyobPayslipSyncStatuses.Synced, item.Status));
        var calls = _api.Calls;

        time.Advance(TimeSpan.FromSeconds(30));
        var second = (await service.SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).Data!;
        Assert.All(second.Companies, item => Assert.Equal(MyobPayslipSyncStatuses.Skipped, item.Status));
        Assert.Equal(calls, _api.Calls);

        time.Advance(TimeSpan.FromSeconds(31));
        var third = (await service.SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).Data!;
        Assert.All(third.Companies, item => Assert.Equal(MyobPayslipSyncStatuses.Synced, item.Status));
        Assert.All(third.Companies, item => Assert.False(item.Backfilled)); // 回填只做一次
    }

    [Fact]
    public async Task 手动同步后列表立即能看到_失败时返回逐家状态_不是整体失败()
    {
        GivenSlips();
        _api.Errors[Aus] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 401", System.Net.HttpStatusCode.Unauthorized);
        AsAdmin();

        var response = await Service().SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None);
        Assert.True(response.Success);
        Assert.Equal(MyobPayslipSyncStatuses.Synced, response.Data!.Companies.Single(item => item.ConnectionGuid == Inv).Status);
        var failed = response.Data.Companies.Single(item => item.ConnectionGuid == Aus);
        Assert.Equal(MyobPayslipSyncStatuses.Failed, failed.Status);
        Assert.Contains("401", failed.ErrorMessage, StringComparison.Ordinal);

        Assert.Equal(3, (await Service().ListAsync(Q(), CancellationToken.None)).Data!.TotalCount);
    }

    [Fact]
    public async Task 手动同步_权限_未登录拒绝_仅本人拒绝_店长只同步可见公司_指定看不到的公司当作找不到()
    {
        GivenSlips();

        _scope.Scope = new CurrentUserManageableStoreScope { IsAuthenticated = false };
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, (await Service().SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).ErrorCode);

        AsEmployee("u-jane"); // 没有任何查看权限：只看本人，不能触发整家公司的同步
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, (await Service().SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).ErrorCode);
        Assert.Equal(0, _api.Calls);

        _permissions.Granted.Clear();
        AsManager("1005"); // Newcastle 由 Investment 发薪，Australian 与他无关
        var manager = (await Service().SyncAsync(new MyobPayslipSyncRequest(), CancellationToken.None)).Data!;
        Assert.Equal([Inv], manager.Companies.Select(item => item.ConnectionGuid).ToArray());
        var hidden = await Service().SyncAsync(new MyobPayslipSyncRequest { ConnectionGuid = Aus }, CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, hidden.ErrorCode);
        Assert.DoesNotContain(Aus, _api.Queried);

        _permissions.Granted.Clear();
        AsAdmin();
        var admin = (await Service().SyncAsync(new MyobPayslipSyncRequest { ConnectionGuid = Aus }, CancellationToken.None)).Data!;
        Assert.Equal([Aus], admin.Companies.Select(item => item.ConnectionGuid).ToArray());
        Assert.DoesNotContain("conn-off", _api.Queried); // 未启用的连接永远不同步
    }

    [Fact]
    public async Task 后台到期判断_首次全部到期_同步后不到期_过了整点又到期_未启用的连接不参与()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        var sync = SyncService(time);

        var first = await sync.FindDueConnectionsAsync(CancellationToken.None);
        Assert.Equal([Aus, Inv], first.Select(item => item.ConnectionGuid).OrderBy(guid => guid, StringComparer.Ordinal).ToArray()); // 没有 conn-off
        await sync.SyncAsync(first, MyobPayslipSyncTrigger.Scheduled, CancellationToken.None);
        Assert.Empty(await sync.FindDueConnectionsAsync(CancellationToken.None)); // 刚同步过

        time.Advance(TimeSpan.FromHours(3)); // 悉尼 07:00 → 10:00：越过 8 点整点，还没到 12 点
        var after = await sync.FindDueConnectionsAsync(CancellationToken.None);
        Assert.Equal(2, after.Count);
        await sync.SyncAsync(after, MyobPayslipSyncTrigger.Scheduled, CancellationToken.None);
        Assert.Empty(await sync.FindDueConnectionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 后台失败后15分钟内不重试_之后再试()
    {
        var time = new MutableTime(Now);
        GivenSlips();
        _api.Errors[Inv] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 503");
        var sync = SyncService(time);
        await sync.SyncAsync(await sync.FindDueConnectionsAsync(CancellationToken.None), MyobPayslipSyncTrigger.Scheduled, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(await sync.FindDueConnectionsAsync(CancellationToken.None), item => item.ConnectionGuid == Inv);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains(await sync.FindDueConnectionsAsync(CancellationToken.None), item => item.ConnectionGuid == Inv);
    }

    // ───────────────────────── 后台任务开关 ─────────────────────────

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("不应创建作用域：后台同步被关闭或 MYOB 未配置");
    }

    private static async Task<MyobPayslipSyncWorker> RunWorkerAsync(bool enabled, MyobOptions options)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MyobPayslipSync:Enabled"] = enabled ? "true" : "false" })
            .Build();
        var worker = new MyobPayslipSyncWorker(
            new ThrowingScopeFactory(),
            Options.Create(options),
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MyobPayslipSyncWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); // 被关闭 / 未配置时立刻结束，不会等启动延迟
        return worker;
    }

    [Fact]
    public async Task 后台任务_配置节关闭或MYOB未配置时不启动_不会碰任何服务()
    {
        var configured = new MyobOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.com/callback" };
        Assert.True(configured.IsConfigured);

        await RunWorkerAsync(enabled: false, configured);
        await RunWorkerAsync(enabled: true, new MyobOptions()); // 没有密钥的开发机不去碰库里的授权
    }

    [Fact]
    public void 日志与DTO_同步状态序列化后不含薪资字段()
    {
        var json = JsonSerializer.Serialize(new MyobPayslipSyncResultDto
        {
            Companies = [new MyobPayslipSyncCompanyResultDto { ConnectionGuid = Inv, Status = MyobPayslipSyncStatuses.Synced, Inserted = 3 }],
        });
        foreach (var banned in new[] { "gross", "net", "hourly", "annual", "tax" })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
