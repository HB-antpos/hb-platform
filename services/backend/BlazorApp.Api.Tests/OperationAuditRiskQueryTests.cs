using System.Security.Claims;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.OperationAudits;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;
using C = BlazorApp.Api.Services.OperationAudits.Risk.PosOperationAuditRiskCatalog;

namespace BlazorApp.Api.Tests;

/// <summary>新收银审计的风险入口、行级风险信息、风险计数、按员工汇总、前后窗口与核查（SQLite）。</summary>
public sealed class OperationAuditRiskQueryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public OperationAuditRiskQueryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(PosOperationAudit),
            typeof(PosOperationAuditItem),
            typeof(PosOperationAuditFlag),
            typeof(PosOperationAuditReview));
        // 生产由迁移 SQL 建 bigint IDENTITY；SQLite 只允许 INTEGER PRIMARY KEY 自增，测试里手工建表。
        _db.Ado.ExecuteCommand("""
            CREATE TABLE PosOperationAuditReviewHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, EventId TEXT NOT NULL, Result INTEGER NOT NULL, Note TEXT NULL,
                ActorUserId TEXT NOT NULL, ActorName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL);
            """);
    }

    [Fact]
    public async Task 危险入口_含手动开钱箱与删除_不含自动开钱箱与销售()
    {
        var remove = await InsertAsync(C.CartItemRemove, -50, before: 30m, after: 15m);
        var manual = await InsertAsync(C.CashDrawerOpen, -40);
        await InsertAsync(C.CashDrawerOpen, -30, orderGuid: "order-1");
        await InsertAsync(C.SaleComplete, -20, orderGuid: "order-1");
        var service = CreateService("Admin");

        var result = await service.QueryAsync(new OperationAuditQueryDto { RiskLens = "danger" }, Now);

        Assert.Equal(2, result.Total);
        Assert.Equal([manual, remove], result.Items.Select(item => item.EventId));
        Assert.All(result.Items, item => Assert.True(item.IsDanger));
        Assert.Equal(15m, result.Items.Single(item => item.EventId == remove).AmountImpact);
    }

    [Fact]
    public async Task 异常入口_按规则与核查状态过滤_撤回的命中不算_行上带依据与结论()
    {
        var drawer = await InsertAsync(C.CashDrawerOpen, -50);
        var discount = await InsertAsync(C.CartLineDiscountChange, -40);
        var retracted = await InsertAsync(C.CartItemRemove, -30);
        await FlagAsync(drawer, C.Rules.NoSaleDrawer, """{"windowSeconds":"120"}""");
        await FlagAsync(discount, C.Rules.BigDiscount);
        await FlagAsync(retracted, C.Rules.DeleteAfterCheckout, retractedAt: Now);
        await _db.Insertable(new PosOperationAuditReview
        {
            EventId = discount, StoreCode = "1013", OccurredAtUtc = Now, Result = 2,
            ReviewedByUserId = "u", ReviewedByName = "Manager", ReviewedAtUtc = Now, Version = 3,
        }).ExecuteCommandAsync();
        var service = CreateService("Admin");

        var abnormal = await service.QueryAsync(new OperationAuditQueryDto { RiskLens = "abnormal" }, Now);
        var drawerOnly = await service.QueryAsync(new OperationAuditQueryDto { RiskLens = "abnormal", RuleCodes = [C.Rules.NoSaleDrawer] }, Now);
        var pending = await service.QueryAsync(new OperationAuditQueryDto { RiskLens = "abnormal", ReviewStatus = "pending" }, Now);
        var followUp = await service.QueryAsync(new OperationAuditQueryDto { RiskLens = "abnormal", ReviewStatus = "followUp" }, Now);

        Assert.Equal(2, abnormal.Total);
        Assert.Equal(drawer, Assert.Single(drawerOnly.Items).EventId);
        Assert.Equal(drawer, Assert.Single(pending.Items).EventId);
        var reviewed = Assert.Single(followUp.Items);
        Assert.Equal(discount, reviewed.EventId);
        Assert.Equal("followUp", reviewed.Review!.Result);
        Assert.Equal(3, reviewed.Review.Version);
        var drawerItem = abnormal.Items.Single(item => item.EventId == drawer);
        Assert.Equal("120", Assert.Single(drawerItem.Flags).Evidence["windowSeconds"]);
    }

    [Fact]
    public async Task 汇总_风险计数忽略操作类型_待核查不含已核查()
    {
        var drawer = await InsertAsync(C.CashDrawerOpen, -50, cashier: "c1");
        var remove = await InsertAsync(C.CartItemRemove, -40, cashier: "c2");
        await InsertAsync(C.SaleComplete, -30, orderGuid: "o", cashier: "c1");
        await FlagAsync(drawer, C.Rules.NoSaleDrawer);
        await FlagAsync(remove, C.Rules.BurstDelete);
        await FlagAsync(remove, C.Rules.DeleteAfterCheckout);
        await _db.Insertable(new PosOperationAuditReview
        {
            EventId = remove, StoreCode = "1013", OccurredAtUtc = Now, Result = 1,
            ReviewedByUserId = "u", ReviewedByName = "M", ReviewedAtUtc = Now, Version = 1,
        }).ExecuteCommandAsync();
        var service = CreateService("Admin");

        var summary = await service.GetSummaryAsync(new OperationAuditQueryDto { OperationType = C.SaleComplete }, Now);

        Assert.Equal(1, summary.Total);
        Assert.Equal(2, summary.DangerTotal);
        Assert.Equal(2, summary.AbnormalTotal);
        Assert.Equal(1, summary.PendingReview);
        Assert.Equal(2, summary.AbnormalEmployees);
        Assert.Equal(3, summary.AbnormalByRule.Sum(item => item.Count));
    }

    [Fact]
    public async Task 按员工汇总_危险含手动开钱箱_金额让利含退款_待核查多的排前()
    {
        var drawer = await InsertAsync(C.CashDrawerOpen, -50, cashier: "c2");
        await InsertAsync(C.CartItemRemove, -40, cashier: "c1", before: 20m, after: 5m);
        await InsertAsync(C.ReturnRefundComplete, -30, cashier: "c1", payment: -4m, orderGuid: "r");
        await InsertAsync(C.SaleComplete, -20, cashier: "c1", orderGuid: "o");
        await FlagAsync(drawer, C.Rules.NoSaleDrawer);
        var service = CreateService("Admin");

        var result = await service.GetEmployeeSummaryAsync(new OperationAuditQueryDto(), Now);

        Assert.Equal(["c2", "c1"], result.Employees.Select(item => item.CashierId));
        var c1 = result.Employees.Single(item => item.CashierId == "c1");
        Assert.Equal(3, c1.Total);
        Assert.Equal(2, c1.DangerCount);
        Assert.Equal(19m, c1.AmountImpact);
        var c2 = result.Employees.Single(item => item.CashierId == "c2");
        Assert.Equal(1, c2.DangerCount);
        Assert.Equal(1, c2.PendingReview);
        Assert.Equal("Cashier c2", c2.CashierName);
        Assert.Equal(4, result.Total);
        Assert.Equal(3, result.DangerTotal);
    }

    [Fact]
    public async Task 多选分店_店长只保留可管理分店_收银员编号精确筛选()
    {
        await InsertAsync(C.SaleComplete, -50, store: "1013", cashier: "c1", orderGuid: "a");
        await InsertAsync(C.SaleComplete, -40, store: "1042", cashier: "c1", orderGuid: "b");
        await InsertAsync(C.SaleComplete, -30, store: "1099", cashier: "c2", orderGuid: "c");
        var manager = CreateService("StoreManager", ["1013", "1042"]);

        var both = await manager.QueryAsync(new OperationAuditQueryDto { StoreCodes = ["1013", "1042", "1099"] }, Now);
        var blocked = await manager.QueryAsync(new OperationAuditQueryDto { StoreCodes = ["1099"] }, Now);
        var cashier = await CreateService("Admin").QueryAsync(new OperationAuditQueryDto { CashierId = "c2" }, Now);

        Assert.Equal(2, both.Total);
        Assert.Equal(0, blocked.Total);
        Assert.Equal("1099", Assert.Single(cashier.Items).StoreCode);
    }

    [Fact]
    public async Task 前后窗口_只取同店同设备_默认五分钟_非法窗口回退()
    {
        var target = await InsertAsync(C.CashDrawerOpen, -600);
        await InsertAsync(C.SaleComplete, -600 - 240, orderGuid: "near");
        await InsertAsync(C.SaleComplete, -600 - 420, orderGuid: "ten-minutes");
        await InsertAsync(C.SaleComplete, -600 + 60, orderGuid: "other-device", device: "POS-2");
        var service = CreateService("Admin");

        var five = await service.GetContextAsync(target, 7);
        var ten = await service.GetContextAsync(target, 10);

        Assert.Equal(OperationAuditDetailAccessStatus.Found, five.Status);
        Assert.Equal(5, five.Data!.WindowMinutes);
        Assert.Equal(2, five.Data.Neighbors.Count);
        Assert.Equal(target, five.Data.Target.EventId);
        Assert.Equal(3, ten.Data!.Neighbors.Count);
        Assert.Equal(OperationAuditDetailAccessStatus.Forbidden,
            (await CreateService("StoreManager", ["1042"]).GetContextAsync(target, 5)).Status);
    }

    [Fact]
    public async Task 核查_首次标记_版本冲突_撤销_每次都留流水()
    {
        var drawer = await InsertAsync(C.CashDrawerOpen, -50);
        await FlagAsync(drawer, C.Rules.NoSaleDrawer);
        var reviews = CreateReviewService("Admin");

        var first = await reviews.ReviewAsync(new OperationAuditReviewRequestDto { EventId = drawer, Result = "followUp", Note = "  查监控 " }, Now);
        var stale = await reviews.ReviewAsync(new OperationAuditReviewRequestDto { EventId = drawer, Result = "normal" }, Now);
        var change = await reviews.ReviewAsync(new OperationAuditReviewRequestDto { EventId = drawer, Result = "normal", ExpectedVersion = 1 }, Now);
        var revoke = await reviews.ReviewAsync(new OperationAuditReviewRequestDto { EventId = drawer, Result = "revoked", ExpectedVersion = 2 }, Now);

        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, first.Status);
        Assert.Equal("followUp", first.Data!.Result);
        Assert.Equal("查监控", first.Data.Note);
        Assert.Equal(1, first.Data.Version);
        Assert.Equal(LegacyEmployeeLogResultStatus.Conflict, stale.Status);
        Assert.Equal(2, change.Data!.Version);
        Assert.Equal("revoked", revoke.Data!.Result);
        Assert.Equal(3, revoke.Data.Version);
        Assert.Equal(3, await _db.Queryable<PosOperationAuditReviewHistory>().CountAsync());
        // 撤销后回到待核查。
        var pending = await CreateService("Admin").QueryAsync(new OperationAuditQueryDto { RiskLens = "abnormal", ReviewStatus = "pending" }, Now);
        Assert.Equal(drawer, Assert.Single(pending.Items).EventId);
    }

    [Fact]
    public async Task 核查_未命中规则或越权或撤销未核查_拒绝()
    {
        var plain = await InsertAsync(C.SaleComplete, -50, orderGuid: "o");
        var flagged = await InsertAsync(C.CashDrawerOpen, -40);
        await FlagAsync(flagged, C.Rules.NoSaleDrawer);

        var noFlag = await CreateReviewService("Admin").ReviewAsync(new OperationAuditReviewRequestDto { EventId = plain, Result = "normal" }, Now);
        var forbidden = await CreateReviewService("StoreManager", ["1042"]).ReviewAsync(new OperationAuditReviewRequestDto { EventId = flagged, Result = "normal" }, Now);
        var revokeNew = await CreateReviewService("Admin").ReviewAsync(new OperationAuditReviewRequestDto { EventId = flagged, Result = "revoked" }, Now);
        var missing = await CreateReviewService("Admin").ReviewAsync(new OperationAuditReviewRequestDto { EventId = Guid.NewGuid(), Result = "normal" }, Now);

        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, noFlag.Status);
        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, forbidden.Status);
        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, revokeNew.Status);
        Assert.Equal(LegacyEmployeeLogResultStatus.NotFound, missing.Status);
        Assert.Equal(0, await _db.Queryable<PosOperationAuditReviewHistory>().CountAsync());
    }

    private async Task<Guid> InsertAsync(
        string type,
        int secondsBeforeNow,
        string store = "1013",
        string cashier = "c1",
        string device = "POS-1",
        string? orderGuid = null,
        decimal? before = null,
        decimal? after = null,
        decimal? payment = null
    )
    {
        var occurred = Now.AddSeconds(secondsBeforeNow);
        var audit = new PosOperationAudit
        {
            EventId = Guid.NewGuid(),
            SchemaVersion = 1,
            OccurredAtUtc = occurred,
            ReceivedAtUtc = occurred.AddSeconds(1),
            OperationType = type,
            Outcome = C.Succeeded,
            CashierId = cashier,
            CashierName = "Cashier " + cashier,
            StoreCode = store,
            DeviceCode = device,
            OrderGuid = orderGuid,
            BeforeActual = before,
            AfterActual = after,
            PaymentAmount = payment,
            CurrencyCode = "AUD",
        };
        await _db.Insertable(audit).ExecuteCommandAsync();
        return audit.EventId;
    }

    private async Task FlagAsync(Guid eventId, string ruleCode, string evidence = "{}", DateTime? retractedAt = null)
    {
        var audit = await _db.Queryable<PosOperationAudit>().FirstAsync(item => item.EventId == eventId);
        await _db.Insertable(new PosOperationAuditFlag
        {
            EventId = eventId,
            RuleCode = ruleCode,
            RuleVersion = 1,
            StoreCode = audit.StoreCode,
            DeviceCode = audit.DeviceCode,
            CashierId = audit.CashierId,
            CashierName = audit.CashierName,
            OperationType = audit.OperationType,
            OccurredAtUtc = audit.OccurredAtUtc,
            EvidenceJson = evidence,
            DetectedAtUtc = Now,
            UpdatedAtUtc = Now,
            RetractedAtUtc = retractedAt,
        }).ExecuteCommandAsync();
    }

    private OperationAuditQueryService CreateService(string role, IReadOnlyList<string>? storeCodes = null)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Role, role)],
            "Test"
        );
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        var scope = new FakeScope(new CurrentUserManageableStoreScope
        {
            IsAllowed = true,
            IsAuthenticated = true,
            StoreCodes = storeCodes ?? [],
        });
        return new OperationAuditQueryService(_db, scope, accessor);
    }

    private OperationAuditReviewService CreateReviewService(string role, IReadOnlyList<string>? storeCodes = null) =>
        new(_db, CreateService(role, storeCodes), new FakeUser());

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeUser : ICurrentUserService
    {
        public string GetCurrentUsername() => "manager";

        public string GetCurrentUserGuid() => "user-1";
    }

    private sealed class FakeScope(CurrentUserManageableStoreScope scope) : ICurrentUserManageableStoreScopeService
    {
        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(scope);

        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => Task.FromResult(scope.StoreCodes);

        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => Task.FromResult(scope.CanAccessStoreCode(storeCode));

        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);

        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(false);

        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
    }
}
