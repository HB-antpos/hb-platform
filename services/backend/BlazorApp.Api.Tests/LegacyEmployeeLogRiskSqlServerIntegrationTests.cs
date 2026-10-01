using System.Data.Common;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Moq;
using Xunit;
using C = BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskCatalog;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 异常扫描、异常入口列表、员工汇总与核查在真实 SQL Server 上的端到端验证。
/// 风险表由生产同一份迁移脚本创建（见夹具），OPENJSON 写入与排序规则都与生产 POSM 一致。
/// </summary>
public sealed partial class LegacyEmployeeLogSqlServerIntegrationTests
{
    private const string RiskStore = "1099";
    private const string RiskDevice = "POS_1099_0908";
    private const string Yilia = "AA299FC5-E7A8-4445-8B2B-DD756EE7C632";
    private const string ReprintOrder = "01A0F5BF-C743-7437-AAA2-A9E53941770F";
    private static readonly DateTime RiskDay = new(2026, 9, 30);

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 扫描写入标记与金额_重复扫描幂等_晚到日志撤回未核查标记而保留已核查标记()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedRiskDayAsync(fixture);

        var first = await ScanRiskDayAsync(fixture);
        Assert.Equal((5, 0, 0, 2), (first.Inserted, first.Updated, first.Retracted, first.ImpactsWritten));
        var flags = await ActiveFlagsAsync(fixture);
        Assert.Equal(
            new[]
            {
                ("R-CART-1", C.Rules.BigDiscount),
                ("R-DEL-1", C.Rules.DeleteAfterCheckout),
                ("R-DRAWER-1", C.Rules.NoSaleDrawer),
                ("R-LOGIN-1", C.Rules.OffHours),
                ("R-RP-3", C.Rules.RepeatReprint),
            },
            flags);
        Assert.Equal(26.04m, await fixture.Db.Ado.GetDecimalAsync("SELECT SUM(Amount) FROM dbo.LegacyEmployeeLogImpacts"));

        var second = await ScanRiskDayAsync(fixture);
        Assert.Equal((0, 0, 0, 0), (second.Inserted, second.Updated, second.Retracted, second.ImpactsWritten));

        var reviewed = await CreateReviewService(fixture).ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-DEL-1", Result = "normal" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, reviewed.Status);

        // 晚到：开钱箱后一分钟的结账让「开钱箱无交易」不再成立；结账与删除之间补传的加商品让「结账后删除」不再成立。
        await fixture.Log("R-CO-2", Yilia, "Yilia", C.Checkout, "开始结账，共1件商品，总金额:3.00", "2026-09-30 10:31:00", RiskDevice, RiskStore);
        await fixture.Log("R-ADD-1", Yilia, "Yilia", C.AddItem, "商品:GLASSES CASE，编码:HB041-12，单价:6.00", "2026-09-30 10:00:10", RiskDevice, RiskStore);
        var third = await ScanRiskDayAsync(fixture);

        Assert.Equal(1, third.Retracted);
        flags = await ActiveFlagsAsync(fixture);
        Assert.DoesNotContain(("R-DRAWER-1", C.Rules.NoSaleDrawer), flags);
        Assert.Contains(("R-DEL-1", C.Rules.DeleteAfterCheckout), flags);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 异常入口与危险入口_核查状态与规则过滤_风险汇总和员工汇总同口径()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedRiskDayAsync(fixture);
        await ScanRiskDayAsync(fixture);
        var followUp = await CreateReviewService(fixture).ReviewAsync(new LegacyEmployeeLogReviewRequestDto
        {
            LogId = "R-CART-1",
            Result = "followUp",
            Note = "八折整单，待店长解释",
        });
        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, followUp.Status);
        var service = fixture.CreateService(RiskStore);

        var abnormal = (await service.QueryAsync(RiskRequest(lens: "abnormal"))).Data!;
        Assert.Equal(5, abnormal.Total);
        Assert.All(abnormal.Items, item => Assert.NotEmpty(item.Flags));
        var drawer = abnormal.Items.Single(item => item.Id == "R-DRAWER-1");
        Assert.True(drawer.IsDanger);
        Assert.Equal("true", Assert.Single(drawer.Flags).Evidence["identityConfirmed"]);
        var cart = abnormal.Items.Single(item => item.Id == "R-CART-1");
        Assert.Equal("followUp", cart.Review!.Result);
        Assert.Equal("八折整单，待店长解释", cart.Review.Note);
        Assert.Equal(14.04m, cart.AmountImpact);

        var summary = abnormal.RiskSummary;
        Assert.Equal((3, 5, 4, 1), (summary.DangerTotal, summary.AbnormalTotal, summary.PendingReview, summary.AbnormalEmployees));
        Assert.Equal(C.AllRules.Except([C.Rules.BurstDelete]), summary.AbnormalByRule.Select(rule => rule.RuleCode));

        Assert.Equal(4, (await service.QueryAsync(RiskRequest(lens: "abnormal", review: "pending"))).Data!.Total);
        Assert.Equal(new[] { "R-CART-1" }, (await service.QueryAsync(RiskRequest(lens: "abnormal", review: "followUp"))).Data!.Items.Select(item => item.Id));
        Assert.Equal(new[] { "R-RP-3" }, (await service.QueryAsync(RiskRequest(lens: "abnormal", rules: [C.Rules.RepeatReprint]))).Data!.Items.Select(item => item.Id));
        // 关键字只命中 GLASSES CASE 那条删除。
        var keyword = RiskRequest(lens: "abnormal");
        keyword.Keyword = "GLASSES";
        var keywordData = (await service.QueryAsync(keyword)).Data!;
        Assert.Equal(new[] { "R-DEL-1" }, keywordData.Items.Select(item => item.Id));
        Assert.Equal(1, keywordData.Total);
        Assert.Equal(1, keywordData.RiskSummary.AbnormalTotal);

        var danger = (await service.QueryAsync(RiskRequest(lens: "danger"))).Data!;
        Assert.Equal(3, danger.Total);
        Assert.All(danger.Items, item => Assert.True(item.IsDanger));
        Assert.Equal(abnormal.OperationCounts.Sum(row => row.Count), danger.OperationCounts.Sum(row => row.Count));

        var employees = (await service.GetEmployeeSummaryAsync(new LegacyEmployeeLogEmployeeSummaryQueryDto
        {
            StoreCodes = [RiskStore],
            From = RiskDay,
            To = RiskDay.AddDays(1),
        })).Data!;
        var yilia = Assert.Single(employees.Employees);
        Assert.Equal((10, 3, 5, 4, 26.04m), (yilia.Total, yilia.DangerCount, yilia.AbnormalCount, yilia.PendingReview, yilia.AmountImpact));
        Assert.Equal(new[] { RiskDevice }, yilia.DeviceCodes);
        Assert.Equal(new[] { RiskStore }, yilia.StoreCodes);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 核查_乐观并发_撤销与历史流水_未命中记录与越权都拒绝()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedRiskDayAsync(fixture);
        await ScanRiskDayAsync(fixture);
        var reviews = CreateReviewService(fixture);

        var created = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-DRAWER-1", Result = "normal" });
        Assert.Equal((LegacyEmployeeLogResultStatus.Ok, "normal", 1), (created.Status, created.Data!.Result, created.Data.Version));
        Assert.Equal("周倩", created.Data.ReviewedByName);

        var stale = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-DRAWER-1", Result = "followUp" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Conflict, stale.Status);

        var changed = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-DRAWER-1", Result = "followUp", Note = " 调监控 ", ExpectedVersion = 1 });
        Assert.Equal(("followUp", "调监控", 2), (changed.Data!.Result, changed.Data.Note, changed.Data.Version));

        var revoked = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-DRAWER-1", Result = "revoked", ExpectedVersion = 2 });
        Assert.Equal(("revoked", 3), (revoked.Data!.Result, revoked.Data.Version));
        Assert.Equal(3, await fixture.Db.Ado.GetIntAsync("SELECT COUNT(*) FROM dbo.LegacyEmployeeLogReviewHistory WHERE LogId = 'R-DRAWER-1'"));
        // 撤销后回到待核查。
        var pending = (await fixture.CreateService(RiskStore).QueryAsync(RiskRequest(lens: "abnormal", review: "pending"))).Data!;
        Assert.Contains("R-DRAWER-1", pending.Items.Select(item => item.Id));

        var notFlagged = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-CO-1", Result = "normal" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, notFlagged.Status);
        var neverReviewed = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-CART-1", Result = "revoked" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, neverReviewed.Status);
        var badResult = await reviews.ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-CART-1", Result = "ok" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, badResult.Status);

        var otherStore = await CreateReviewService(fixture, "1013").ReviewAsync(new LegacyEmployeeLogReviewRequestDto { LogId = "R-CART-1", Result = "normal" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, otherStore.Status);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 上下文支持更长窗口并带回风险信息()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedRiskDayAsync(fixture);
        await ScanRiskDayAsync(fixture);
        var service = fixture.CreateService(RiskStore);

        var context = (await service.GetContextAsync("R-DRAWER-1", windowMinutes: 15)).Data!;
        Assert.Equal(15, context.WindowMinutes);
        Assert.Equal(C.Rules.NoSaleDrawer, Assert.Single(context.Target.Flags).RuleCode);
        Assert.True(context.Target.IsDanger);
        Assert.Equal(new[] { "R-DRAWER-1", "R-ID-1" }, context.Neighbors.Select(item => item.Id).Order());

        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, (await service.GetContextAsync("R-DRAWER-1", windowMinutes: 7)).Status);
    }

    private static async Task SeedRiskDayAsync(Fixture fixture)
    {
        await fixture.Log("R-LOGIN-1", Yilia, "Yilia", "登录", "员工 Yilia 登录系统", "2026-09-30 06:30:00", RiskDevice, RiskStore);
        await fixture.Log("R-CO-1", Yilia, "Yilia", C.Checkout, "开始结账，共3件商品，总金额:23.50", "2026-09-30 10:00:00", RiskDevice, RiskStore);
        await fixture.Log("R-DEL-1", Yilia, "Yilia", C.DeleteItem, "从购物车删除商品:GLASSES CASE，编码:HB041-12，数量:2，单价:6.00，总金额:12.00", "2026-09-30 10:00:20", RiskDevice, RiskStore);
        await fixture.Log("R-ID-1", Yilia, "Yilia", C.IdentityConfirm, "员工 Yilia 确认身份进行开钱箱操作", "2026-09-30 10:30:00", RiskDevice, RiskStore);
        await fixture.Log("R-DRAWER-1", Yilia, "Yilia", C.OpenDrawer, "员工Yilia操作开钱箱", "2026-09-30 10:30:00", RiskDevice, RiskStore);
        await fixture.Log("R-CART-1", Yilia, "Yilia", C.ChangeAllDiscount, "将所有商品折扣率设置为:80%，购物车商品数:6，原总金额:17.55", "2026-09-30 11:00:00", RiskDevice, RiskStore);
        for (var index = 1; index <= 3; index++)
        {
            await fixture.Log($"R-RP-{index}", Yilia, "Yilia", C.Reprint, $"重打印订单:{ReprintOrder}，金额:14.96", $"2026-09-30 12:0{index}:00", RiskDevice, RiskStore);
        }
        await fixture.Log("R-ADD-0", Yilia, "Yilia", C.AddItem, "商品:Piggy Bank，编码:PB01，单价:5.00", "2026-09-30 13:00:00", RiskDevice, RiskStore);
    }

    private static Task<LegacyRiskScanResult> ScanRiskDayAsync(Fixture fixture) =>
        LegacyEmployeeLogRiskScanner.ScanAsync(
            (DbConnection)fixture.Db.Ado.Connection,
            RiskStore,
            RiskDay,
            RiskDay.AddDays(1),
            new LegacyEmployeeLogRiskOptions(),
            DateTime.UtcNow,
            CancellationToken.None);

    private static async Task<List<(string, string)>> ActiveFlagsAsync(Fixture fixture) =>
        (await fixture.Db.Ado.GetDataTableAsync(
            "SELECT LogId, RuleCode FROM dbo.LegacyEmployeeLogFlags WHERE RetractedAtUtc IS NULL ORDER BY LogId, RuleCode"))
        .Rows.Cast<System.Data.DataRow>()
        .Select(row => ((string)row["LogId"], (string)row["RuleCode"]))
        .ToList();

    private static LegacyEmployeeLogQueryDto RiskRequest(string lens, string? review = null, List<string>? rules = null) => new()
    {
        StoreCodes = [RiskStore],
        From = RiskDay,
        To = RiskDay.AddDays(1),
        RiskLens = lens,
        ReviewStatus = review,
        RuleCodes = rules,
    };

    private static LegacyEmployeeLogReviewService CreateReviewService(Fixture fixture, string accessibleStore = RiskStore)
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(service => service.CanAccessStoreCodeAsync(It.IsAny<string>()))
            .ReturnsAsync((string code) => code == accessibleStore);
        var user = new Mock<ICurrentUserService>();
        user.Setup(service => service.GetCurrentUserGuid()).Returns("8C1D2E3F-0000-4000-8000-00000000A001");
        user.Setup(service => service.GetCurrentUsername()).Returns("周倩");
        return new LegacyEmployeeLogReviewService(fixture.Db, scope.Object, user.Object);
    }
}
