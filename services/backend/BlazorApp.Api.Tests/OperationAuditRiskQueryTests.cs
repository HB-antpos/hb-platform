using System.Security.Claims;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.OperationAudits;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
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
    public async Task 设备选项_汇总与员工汇总都返回_不带设备条件本身_剔除空设备码_按事件数降序()
    {
        await InsertAsync(C.SaleComplete, -50, device: "POS-1", orderGuid: "a");
        await InsertAsync(C.SaleComplete, -40, device: "POS-1", orderGuid: "b");
        await InsertAsync(C.SaleComplete, -30, device: "POS-2", orderGuid: "c");
        await InsertAsync(C.SaleComplete, -20, device: "POS-3", orderGuid: "d");
        await InsertAsync(C.SaleComplete, -10, device: "", orderGuid: "e");
        await InsertAsync(C.SaleComplete, -10, store: "1042", device: "POS-9", orderGuid: "f");
        var service = CreateService("Admin");

        // 选了设备 POS-2 后，列表计数只剩它，但设备下拉仍列出该范围内的全部设备。
        var summary = await service.GetSummaryAsync(new OperationAuditQueryDto { StoreCode = "1013", DeviceCode = "POS-2" }, Now);
        var employees = await service.GetEmployeeSummaryAsync(new OperationAuditQueryDto { StoreCode = "1013", DeviceCode = "POS-2" }, Now);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, employees.Total);
        var expected = new[] { ("POS-1", 2), ("POS-2", 1), ("POS-3", 1) };
        Assert.Equal(expected, summary.Devices.Select(item => (item.DeviceCode!, item.Count)));
        Assert.Equal(expected, employees.Devices.Select(item => (item.DeviceCode!, item.Count)));
    }

    [Fact]
    public async Task 设备选项_最多返回100项()
    {
        for (var index = 0; index < 105; index++)
        {
            await InsertAsync(C.SaleComplete, -50, device: $"POS-{index:000}", orderGuid: $"o{index}");
        }

        var summary = await CreateService("Admin").GetSummaryAsync(new OperationAuditQueryDto(), Now);

        Assert.Equal(100, summary.Devices.Count);
    }

    [Fact]
    public async Task 角标_待核查异常按事件去重_危险是其子集_自动开钱箱不算危险()
    {
        // 手动开钱箱（危险）：命中两条规则，只计一个事件。
        var manualDrawer = await InsertAsync(C.CashDrawerOpen, -50);
        await FlagAsync(manualDrawer, C.Rules.NoSaleDrawer);
        await FlagAsync(manualDrawer, C.Rules.OffHours);
        // 随收款自动开钱箱（带订单）：命中异常但不危险。
        var autoDrawer = await InsertAsync(C.CashDrawerOpen, -45, orderGuid: "order-1");
        await FlagAsync(autoDrawer, C.Rules.OffHours);
        // 危险操作类型。
        var remove = await InsertAsync(C.CartItemRemove, -40);
        await FlagAsync(remove, C.Rules.BurstDelete);
        // 非危险（重打印）异常。
        var reprint = await InsertAsync(C.ReceiptReprint, -35, orderGuid: "order-2");
        await FlagAsync(reprint, C.Rules.RepeatReprint);
        // 已核查（确认正常 / 需跟进）不算待核查；撤销（0）视同待核查。
        var normal = await InsertAsync(C.CartClear, -30);
        var followUp = await InsertAsync(C.CartClear, -29);
        var revoked = await InsertAsync(C.CartClear, -28);
        foreach (var (id, result) in new[] { (normal, (byte)1), (followUp, (byte)2), (revoked, (byte)0) })
        {
            await FlagAsync(id, C.Rules.BurstDelete);
            await _db.Insertable(new PosOperationAuditReview
            {
                EventId = id, StoreCode = "1013", OccurredAtUtc = Now, Result = result,
                ReviewedByUserId = "u", ReviewedByName = "M", ReviewedAtUtc = Now, Version = 1,
            }).ExecuteCommandAsync();
        }
        // 规则命中已撤回的不算。
        var retracted = await InsertAsync(C.CartItemRemove, -20);
        await FlagAsync(retracted, C.Rules.BurstDelete, retractedAt: Now);
        // 没有命中规则的危险操作不算异常。
        await InsertAsync(C.CartItemRemove, -10);

        var badge = await CreateService("Admin").GetBadgeSummaryAsync(utcNow: Now);

        // 异常：手动开钱箱、自动开钱箱、删除、重打印、撤销核查的清空 = 5；危险：手动开钱箱、删除、撤销核查的清空 = 3。
        Assert.Equal(5, badge.AbnormalPending);
        Assert.Equal(3, badge.DangerPending);
    }

    [Fact]
    public async Task 角标_天数窗口默认7限制1到31_店长只看关联分店_无分店或无权限返回零()
    {
        var recent = await InsertAsync(C.CartItemRemove, -3600, store: "1013");
        var old = await InsertAsync(C.CartItemRemove, -3 * 86400, store: "1013");
        var tooOld = await InsertAsync(C.CartItemRemove, -9 * 86400, store: "1013");
        var otherStore = await InsertAsync(C.CartItemRemove, -3600, store: "1042");
        foreach (var id in new[] { recent, old, tooOld, otherStore })
        {
            await FlagAsync(id, C.Rules.BurstDelete);
        }

        var admin = CreateService("Admin");
        // 默认 7 天：不含 9 天前。
        Assert.Equal((3, 3), Pair(await admin.GetBadgeSummaryAsync(utcNow: Now)));
        // 1 天：只有最近一小时内的两条（两家分店）。
        Assert.Equal((2, 2), Pair(await admin.GetBadgeSummaryAsync(1, Now)));
        // 0 与负数收敛到 1 天；超过 31 收敛到 31 天。
        Assert.Equal((2, 2), Pair(await admin.GetBadgeSummaryAsync(0, Now)));
        Assert.Equal((4, 4), Pair(await admin.GetBadgeSummaryAsync(999, Now)));

        var manager = CreateService("StoreManager", ["1013"]);
        Assert.Equal((2, 2), Pair(await manager.GetBadgeSummaryAsync(utcNow: Now)));
        Assert.Equal((0, 0), Pair(await CreateService("StoreManager", []).GetBadgeSummaryAsync(utcNow: Now)));
        Assert.Equal((0, 0), Pair(await CreateService("Cashier").GetBadgeSummaryAsync(utcNow: Now)));

        static (int, int) Pair(EmployeeLogBadgeSummaryDto badge) => (badge.AbnormalPending, badge.DangerPending);
    }

    [Fact]
    public async Task 角标_按范围加天数缓存_缓存期内不重复查库_不同范围互不串用()
    {
        var first = await InsertAsync(C.CartItemRemove, -3600, store: "1013");
        await FlagAsync(first, C.Rules.BurstDelete);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var manager = CreateService("StoreManager", ["1013"], cache);

        Assert.Equal(1, (await manager.GetBadgeSummaryAsync(utcNow: Now)).AbnormalPending);

        var second = await InsertAsync(C.CartItemRemove, -1800, store: "1013");
        await FlagAsync(second, C.Rules.BurstDelete);

        // 缓存期内仍是旧值；换一个天数、换一个可见范围都不会命中这份缓存。
        Assert.Equal(1, (await manager.GetBadgeSummaryAsync(utcNow: Now)).AbnormalPending);
        Assert.Equal(2, (await manager.GetBadgeSummaryAsync(8, Now)).AbnormalPending);
        Assert.Equal(0, (await CreateService("StoreManager", ["1042"], cache).GetBadgeSummaryAsync(utcNow: Now)).AbnormalPending);
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

    private OperationAuditQueryService CreateService(string role, IReadOnlyList<string>? storeCodes = null, IMemoryCache? cache = null)
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
        return new OperationAuditQueryService(_db, scope, accessor, cache: cache);
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
