using System.Security.Claims;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.OperationAudits;
using BlazorApp.Api.Services.OperationAudits.Risk;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Moq;
using SqlSugar;
using Xunit;
using C = BlazorApp.Api.Services.OperationAudits.Risk.PosOperationAuditRiskCatalog;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 新收银异常扫描在真实 SQL Server 上的端到端验证：OPENJSON 写回、OUTER APPLY 取商品行、
/// 门店时区换算的非营业时段读取，以及列表 / 汇总 / 员工汇总在 SQL Server 方言下的子查询。
/// 风险表用生产同一份迁移脚本创建；审计主表由 SqlSugar 按实体建（生产由 Hbpos.Api 同样按实体建）。
/// </summary>
public sealed class PosOperationAuditRiskSqlServerIntegrationTests
{
    private const string Store = "1013";
    private const string Device = "POS_1013_0222";
    private static readonly DateTime Day = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 扫描七条规则_重复扫描幂等_晚到事件撤回未核查标记而保留已核查标记()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await SeedAsync(fixture);

        var first = await ScanAsync(fixture);
        Assert.Equal((6, 0, 0), (first.Inserted, first.Updated, first.Retracted));
        Assert.Equal(
            new[]
            {
                (ids["drawer"], C.Rules.NoSaleDrawer),
                (ids["remove"], C.Rules.DeleteAfterCheckout),
                (ids["discount"], C.Rules.BigDiscount),
                (ids["reprint3"], C.Rules.RepeatReprint),
                (ids["emergency"], C.Rules.EmergencyOverride),
                (ids["early"], C.Rules.OffHours),
            }.OrderBy(item => item.Item1).ThenBy(item => item.Item2),
            (await ActiveFlagsAsync(fixture)).OrderBy(item => item.Item1).ThenBy(item => item.Item2));
        var evidence = await fixture.Db.Ado.GetStringAsync(
            "SELECT EvidenceJson FROM dbo.PosOperationAuditFlags WHERE RuleCode = 'bigDiscount'");
        Assert.Contains("\"product\":\"Big Bear\"", evidence);

        var second = await ScanAsync(fixture);
        Assert.Equal((0, 0, 0), (second.Inserted, second.Updated, second.Retracted));

        var reviewed = await fixture.CreateReviewService().ReviewAsync(new OperationAuditReviewRequestDto { EventId = ids["remove"], Result = "normal" });
        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, reviewed.Status);

        // 晚到：开钱箱后一分钟的销售让「开钱箱无交易」不再成立；收款与删除之间补传的挂单让「收款后删除」不再成立（但已核查，保留）。
        await fixture.InsertAsync(C.SaleComplete, Day.AddHours(2).AddMinutes(1), orderGuid: "late-sale");
        await fixture.InsertAsync(C.OrderHold, Day.AddHours(1).AddSeconds(20));
        var third = await ScanAsync(fixture);

        Assert.Equal(1, third.Retracted);
        var flags = await ActiveFlagsAsync(fixture);
        Assert.DoesNotContain((ids["drawer"], C.Rules.NoSaleDrawer), flags);
        Assert.Contains((ids["remove"], C.Rules.DeleteAfterCheckout), flags);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 列表汇总与员工汇总在SQLServer上同口径()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await SeedAsync(fixture);
        await ScanAsync(fixture);
        await fixture.CreateReviewService().ReviewAsync(new OperationAuditReviewRequestDto { EventId = ids["discount"], Result = "followUp" });
        var service = fixture.CreateQueryService();
        var range = new OperationAuditQueryDto { FromUtc = Day.AddDays(-1), ToUtc = Day.AddDays(1), StoreCodes = [Store] };

        var abnormal = await service.QueryAsync(Copy(range, request => request.RiskLens = "abnormal"));
        var pending = await service.QueryAsync(Copy(range, request => { request.RiskLens = "abnormal"; request.ReviewStatus = "pending"; }));
        var discountRule = await service.QueryAsync(Copy(range, request => { request.RiskLens = "abnormal"; request.RuleCodes = [C.Rules.BigDiscount]; }));
        var danger = await service.QueryAsync(Copy(range, request => request.RiskLens = "danger"));
        var summary = await service.GetSummaryAsync(range);
        var employees = await service.GetEmployeeSummaryAsync(range);

        Assert.Equal(6, abnormal.Total);
        Assert.Equal(5, pending.Total);
        var discount = Assert.Single(discountRule.Items);
        Assert.Equal("followUp", discount.Review!.Result);
        Assert.Equal(20m, discount.AmountImpact);
        Assert.Equal(3, danger.Total);
        Assert.Equal((3, 6, 5), (summary.DangerTotal, summary.AbnormalTotal, summary.PendingReview));
        var c1 = Assert.Single(employees.Employees, item => item.CashierId == "c1");
        Assert.Equal(6, c1.AbnormalCount);
        Assert.Equal(35m, c1.AmountImpact);
        Assert.Equal([Device], c1.DeviceCodes);
    }

    private static async Task<Dictionary<string, Guid>> SeedAsync(Fixture fixture)
    {
        var ids = new Dictionary<string, Guid>
        {
            ["tender"] = await fixture.InsertAsync(C.PaymentTenderAdd, Day.AddHours(1), payment: 30m),
            ["remove"] = await fixture.InsertAsync(C.CartItemRemove, Day.AddHours(1).AddSeconds(40), before: 30m, after: 15m),
            ["sale1"] = await fixture.InsertAsync(C.SaleComplete, Day.AddHours(1).AddMinutes(1), orderGuid: "o1", payment: 15m),
            ["drawer"] = await fixture.InsertAsync(C.CashDrawerOpen, Day.AddHours(2)),
            ["discount"] = await fixture.InsertAsync(C.CartLineDiscountChange, Day.AddHours(2).AddMinutes(30), before: 40m, after: 20m,
                item: new PosOperationAuditItem { DisplayName = "Big Bear", BeforeQuantity = 1m, BeforeUnitPrice = 40m, AfterUnitPrice = 40m,
                    BeforeDiscountAmount = 0m, AfterDiscountAmount = 20m, AfterGrossAmount = 40m }),
            ["sale2"] = await fixture.InsertAsync(C.SaleComplete, Day.AddHours(3), orderGuid: "o2", payment: 12m),
            ["reprint1"] = await fixture.InsertAsync(C.ReceiptReprint, Day.AddHours(3).AddSeconds(10)),
            ["reprint2"] = await fixture.InsertAsync(C.ReceiptReprint, Day.AddHours(3).AddSeconds(20)),
            ["reprint3"] = await fixture.InsertAsync(C.ReceiptReprint, Day.AddHours(3).AddSeconds(30)),
            ["emergency"] = await fixture.InsertAsync("CASHIER_LOGIN", Day.AddHours(4), emergency: true),
            // 19:30 UTC = 布里斯班次日 05:30，开门前。
            ["early"] = await fixture.InsertAsync("CASHIER_LOGIN", Day.AddHours(19).AddMinutes(30)),
        };
        // 另一分店同样的手动开钱箱不应影响本店。
        await fixture.InsertAsync(C.CashDrawerOpen, Day.AddHours(2), store: "1042");
        return ids;
    }

    private static Task<PosAuditRiskScanResult> ScanAsync(Fixture fixture) =>
        PosOperationAuditRiskScanner.ScanAsync(
            (System.Data.Common.DbConnection)fixture.Db.Ado.Connection, Store, Brisbane, Day, Day.AddDays(1),
            new LegacyEmployeeLogRiskOptions(), DateTime.UtcNow, CancellationToken.None);

    private static async Task<List<(Guid, string)>> ActiveFlagsAsync(Fixture fixture) =>
        (await fixture.Db.Queryable<PosOperationAuditFlag>().Where(flag => flag.RetractedAtUtc == null).ToListAsync())
        .Select(flag => (flag.EventId, flag.RuleCode))
        .ToList();

    private static OperationAuditQueryDto Copy(OperationAuditQueryDto source, Action<OperationAuditQueryDto> change)
    {
        var copy = new OperationAuditQueryDto { FromUtc = source.FromUtc, ToUtc = source.ToUtc, StoreCodes = source.StoreCodes };
        change(copy);
        return copy;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqlSugarClient Db { get; }

        private readonly string _masterConnectionString;
        private readonly string _databaseName;

        private Fixture(string master, string databaseName, string database)
        {
            _masterConnectionString = master;
            _databaseName = databaseName;
            Db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = database,
                DbType = SqlSugar.DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(LegacyEmployeeLogSqlServerIntegrationTests.ConnectionEnvironmentVariable)!;
            var builder = new SqlConnectionStringBuilder(baseConnectionString);
            var host = builder.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0].Trim('[', ']');
            Assert.True(host is "localhost" or "127.0.0.1", "集成测试只允许连本机 SQL Server");
            var databaseName = $"HbPosAuditRisk_{Guid.NewGuid():N}";
            var master = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var database = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = databaseName }.ConnectionString;
            await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
            var fixture = new Fixture(master, databaseName, database);
            try
            {
                fixture.Db.CodeFirst.InitTables(typeof(PosOperationAudit), typeof(PosOperationAuditItem));
                await ExecuteAsync(database, PosOperationAuditRiskSchema.ApplySql);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<Guid> InsertAsync(
            string type,
            DateTime occurredUtc,
            string store = Store,
            string? orderGuid = null,
            decimal? before = null,
            decimal? after = null,
            decimal? payment = null,
            bool emergency = false,
            PosOperationAuditItem? item = null
        )
        {
            var audit = new PosOperationAudit
            {
                EventId = Guid.NewGuid(),
                SchemaVersion = 1,
                OccurredAtUtc = occurredUtc,
                ReceivedAtUtc = occurredUtc.AddSeconds(1),
                OperationType = type,
                Outcome = C.Succeeded,
                CashierId = "c1",
                CashierName = "Gao Jian",
                StoreCode = store,
                DeviceCode = Device,
                OrderGuid = orderGuid,
                BeforeActual = before,
                AfterActual = after,
                PaymentAmount = payment,
                IsEmergencyOverride = emergency,
                CurrencyCode = "AUD",
                PrimaryProduct = item?.DisplayName,
            };
            await Db.Insertable(audit).ExecuteCommandAsync();
            if (item != null)
            {
                item.EventId = audit.EventId;
                item.LineIndex = 0;
                await Db.Insertable(item).ExecuteCommandAsync();
            }
            return audit.EventId;
        }

        public OperationAuditQueryService CreateQueryService()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "Test");
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
            return new OperationAuditQueryService(Db, Mock.Of<ICurrentUserManageableStoreScopeService>(), accessor);
        }

        public OperationAuditReviewService CreateReviewService()
        {
            var user = new Mock<ICurrentUserService>();
            user.Setup(service => service.GetCurrentUserGuid()).Returns("manager-1");
            user.Setup(service => service.GetCurrentUsername()).Returns("Manager");
            return new OperationAuditReviewService(Db, CreateQueryService(), user.Object);
        }

        public async ValueTask DisposeAsync()
        {
            Db.Dispose();
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_masterConnectionString, $"""
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END
                """);
        }

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }
    }
}
