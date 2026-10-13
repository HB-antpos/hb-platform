using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Containers;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 货柜「到仓库日期」共用规则的纯逻辑测试：窗口常量、有效日期、窗口判定、悉尼本地今天、展示排序。
/// </summary>
public sealed class ContainerComingSoonWarehouseArrivalLogicTests
{
    private static readonly DateTime Today = new(2026, 10, 14);

    [Fact]
    public void 窗口常量集中在一处_过去7天_未来21天_上界为开区间()
    {
        Assert.Equal(7, ContainerWarehouseArrival.PastDays);
        Assert.Equal(21, ContainerWarehouseArrival.FutureDays);

        var (from, toExclusive) = ContainerWarehouseArrival.BuildWindow(Today.AddHours(15));

        Assert.Equal(Today.AddDays(-7), from);
        Assert.Equal(Today.AddDays(22), toExclusive);
        // 移动端沿用的同名入口必须与共用实现逐值一致
        Assert.Equal((from, toExclusive), ContainerNewProductsReactService.BuildWindow(Today));
    }

    [Theory]
    [InlineData(-8, false)]
    [InlineData(-7, true)]
    [InlineData(0, true)]
    [InlineData(21, true)]
    [InlineData(22, false)]
    public void IsInWindow_只有实际日期_边界为减7含_加21含_加22不含_减8不含(int offsetDays, bool expected)
    {
        var (from, toExclusive) = ContainerWarehouseArrival.BuildWindow(Today);

        Assert.Equal(expected, ContainerWarehouseArrival.IsInWindow(Today.AddDays(offsetDays), null, from, toExclusive));
        // 同样的日期当作预计日期（在途）时规则相同
        Assert.Equal(expected, ContainerWarehouseArrival.IsInWindow(null, Today.AddDays(offsetDays), from, toExclusive));
    }

    [Fact]
    public void IsInWindow_带时间部分的边界日_加21当天23点含_加22当天零点不含_减7当天零点含_减8当天23点不含()
    {
        var (from, toExclusive) = ContainerWarehouseArrival.BuildWindow(Today);

        Assert.True(ContainerWarehouseArrival.IsInWindow(Today.AddDays(21).AddHours(23).AddMinutes(59), null, from, toExclusive));
        Assert.False(ContainerWarehouseArrival.IsInWindow(Today.AddDays(22), null, from, toExclusive));
        Assert.True(ContainerWarehouseArrival.IsInWindow(Today.AddDays(-7), null, from, toExclusive));
        Assert.False(ContainerWarehouseArrival.IsInWindow(Today.AddDays(-8).AddHours(23).AddMinutes(59), null, from, toExclusive));
    }

    [Fact]
    public void IsInWindow_实际日期优先于预计日期_两者都为空不出现()
    {
        var (from, toExclusive) = ContainerWarehouseArrival.BuildWindow(Today);

        // 实际日期在窗口外，预计日期在窗口内：以实际日期为准，不出现
        Assert.False(ContainerWarehouseArrival.IsInWindow(Today.AddDays(-9), Today.AddDays(3), from, toExclusive));
        // 实际日期在窗口内，预计日期在窗口外：出现
        Assert.True(ContainerWarehouseArrival.IsInWindow(Today.AddDays(2), Today.AddDays(-30), from, toExclusive));
        Assert.False(ContainerWarehouseArrival.IsInWindow(null, null, from, toExclusive));
    }

    [Fact]
    public void ResolveDate_实际优先_只取日期部分_两者为空返回null()
    {
        var actual = new DateTime(2026, 10, 15, 15, 30, 0);
        var estimated = new DateTime(2026, 10, 1, 8, 0, 0);

        Assert.Equal(new DateTime(2026, 10, 15), ContainerWarehouseArrival.ResolveDate(actual, estimated));
        Assert.Equal(new DateTime(2026, 10, 1), ContainerWarehouseArrival.ResolveDate(null, estimated));
        Assert.Equal(new DateTime(2026, 10, 15), ContainerWarehouseArrival.ResolveDate(actual, null));
        Assert.Null(ContainerWarehouseArrival.ResolveDate(null, null));
    }

    [Fact]
    public void GetSydneyToday_按悉尼日历日算_不是UTC日期也不是服务器本地日期()
    {
        // 夏令时（AEDT，UTC+11）：UTC 10-13 14:30 已是悉尼 10-14 01:30
        Assert.Equal(new DateTime(2026, 10, 14), ContainerWarehouseArrival.GetSydneyToday(new DateTime(2026, 10, 13, 14, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 13), ContainerWarehouseArrival.GetSydneyToday(new DateTime(2026, 10, 13, 12, 59, 0, DateTimeKind.Utc)));
        // 标准时间（AEST，UTC+10）：UTC 7-1 14:30 是悉尼 7-2 00:30
        Assert.Equal(new DateTime(2026, 7, 2), ContainerWarehouseArrival.GetSydneyToday(new DateTime(2026, 7, 1, 14, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void GetLocalToday_QLD用布里斯班时区_悉尼与NSW口径同一个函数()
    {
        // UTC 10-13 13:30：悉尼（UTC+11）已是 10-14 00:30，布里斯班（UTC+10，无夏令时）还是 10-13 23:30
        var nowUtc = new DateTime(2026, 10, 13, 13, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 14), ContainerWarehouseArrival.GetLocalToday("NSW", nowUtc));
        Assert.Equal(new DateTime(2026, 10, 13), ContainerWarehouseArrival.GetLocalToday("QLD", nowUtc));
        Assert.Equal(ContainerWarehouseArrival.GetLocalToday("NSW", nowUtc), ContainerWarehouseArrival.GetSydneyToday(nowUtc));
    }

    [Fact]
    public void OrderForDisplay_按到仓库日期升序_同日按货柜编号再按HGUID_无日期的排最后()
    {
        var input = new List<ContainerMainDto>
        {
            Dto("G-4", "B-NO-DATE", null, null),
            Dto("G-3", "A-OTHER-DAY", null, Today.AddDays(5)),
            // 实际日期优先：预计日期很早也不影响排序，按实际日期 T+2 排
            Dto("G-2", "Z-ACTUAL", Today.AddDays(2), Today.AddDays(-30)),
            // 同为 T+5：货柜编号相同时按 HGUID（序号比较，大写在小写前）
            Dto("g-5", "A-OTHER-DAY", null, Today.AddDays(5).AddHours(9)),
            Dto("G-1", "A-FIRST", Today.AddDays(-7), null),
        };

        var ordered = ContainerWarehouseArrival.OrderForDisplay(input);

        Assert.Equal(new[] { "G-1", "G-2", "G-3", "g-5", "G-4" }, ordered.Select(x => x.HGUID));
    }

    [Fact]
    public void OrderForDisplay_输入顺序不同结果相同_稳定排序()
    {
        var items = new List<ContainerMainDto>
        {
            Dto("G-1", "N2", Today.AddDays(1), null),
            Dto("G-2", "N1", Today.AddDays(1), null),
            Dto("G-3", "N1", Today.AddDays(1), null),
            Dto("G-4", null, Today.AddDays(1), null),
            Dto("G-5", "N0", null, Today.AddDays(1)),
        };
        var expected = ContainerWarehouseArrival.OrderForDisplay(items).Select(x => x.HGUID).ToList();

        // 空货柜编号按空串排在同日最前
        Assert.Equal(new[] { "G-4", "G-5", "G-2", "G-3", "G-1" }, expected);
        var reversed = Enumerable.Reverse(items).ToList();
        Assert.Equal(expected, ContainerWarehouseArrival.OrderForDisplay(reversed).Select(x => x.HGUID));
        var rotated = items.Skip(2).Concat(items.Take(2)).ToList();
        Assert.Equal(expected, ContainerWarehouseArrival.OrderForDisplay(rotated).Select(x => x.HGUID));
    }

    [Fact]
    public void OrderForDisplay_按HGUID去重_空HGUID丢弃_同一HGUID保留第一条()
    {
        var input = new List<ContainerMainDto>
        {
            Dto("G-1", "FIRST", Today.AddDays(1), null),
            Dto("G-1", "DUPLICATE", Today.AddDays(1), null),
            Dto("", "EMPTY", Today.AddDays(1), null),
            Dto(null, "NULL", Today.AddDays(1), null),
            Dto("G-2", "OTHER", Today.AddDays(2), null),
        };

        var ordered = ContainerWarehouseArrival.OrderForDisplay(input);

        Assert.Equal(new[] { "G-1", "G-2" }, ordered.Select(x => x.HGUID));
        Assert.Equal("FIRST", ordered[0].货柜编号);
    }

    private static ContainerMainDto Dto(string? hguid, string? number, DateTime? actual, DateTime? estimated) =>
        new() { HGUID = hguid, 货柜编号 = number, 实际到货日期 = actual, 预计到岸日期 = estimated };
}

/// <summary>
/// Web 即将上新的货柜选取（<see cref="ContainerReactService.GetComingSoonSummariesAsync"/>）与移动端 HB新品页 warehouse 口径的 SQLite 集成测试，
/// 重点证明两处在同一份数据上选出的货柜集合完全一致。
/// </summary>
public sealed class ContainerComingSoonWarehouseWindowTests : IDisposable
{
    // 固定「现在」：UTC 03:00 = 悉尼 14:00（夏令时），悉尼今天 = 2026-10-14
    private static readonly DateTime NowUtc = new(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 10, 14);

    private readonly string path = Path.Combine(Path.GetTempPath(), $"container-coming-soon-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient database;

    public ContainerComingSoonWarehouseWindowTests()
    {
        database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = false,
        });
        database.CodeFirst.InitTables(typeof(Store), typeof(UserStore), typeof(Container), typeof(ContainerDetail),
            typeof(DomesticProduct), typeof(WarehouseProduct), typeof(Product), typeof(StoreRetailPrice));
        // 移动端新品查询用商品修改历史判定「本柜新品」；SQLite 下 long 自增主键需手写建表
        database.Ado.ExecuteCommand("CREATE TABLE WarehouseProductChangeHistory (Id INTEGER PRIMARY KEY, EventGuid TEXT NOT NULL, ProductCode TEXT NOT NULL, Action TEXT NOT NULL, Source TEXT NOT NULL, SourceReference TEXT NULL, BatchGuid TEXT NULL, ActorUserGuid TEXT NULL, ActorName TEXT NOT NULL, ActorType TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, ChangesJson TEXT NOT NULL)");
    }

    // 一批覆盖各种边界的货柜夹具：名称 → (实际到货日期, 预计到岸日期, 是否软删, 状态)，期望是否出现在窗口 [T-7, T+22)
    private static readonly (string Code, DateTime? Actual, DateTime? Estimated, bool Deleted, int? Status, bool Expected)[] Fixture =
    [
        ("C-ACT-M8", Today.AddDays(-8), null, false, 2, false),
        ("C-ACT-M7", Today.AddDays(-7), null, false, 2, true),
        ("C-ACT-M7-LATE", Today.AddDays(-7).AddHours(23).AddMinutes(59), null, false, 2, true),
        ("C-ACT-M8-LATE", Today.AddDays(-8).AddHours(23).AddMinutes(59), null, false, 2, false),
        ("C-ACT-TODAY", Today, null, false, 2, true),
        ("C-ACT-P21", Today.AddDays(21), null, false, 2, true),
        ("C-ACT-P21-LATE", Today.AddDays(21).AddHours(15).AddMinutes(30), null, false, 2, true),
        ("C-ACT-P22", Today.AddDays(22), null, false, 2, false),
        // 实际日期优先于预计日期：实际在窗口外则预计日期不能把它救回来，反之预计日期不能把它挤出去
        ("C-ACT-OUT-EST-IN", Today.AddDays(-9), Today.AddDays(3), false, 2, false),
        ("C-ACT-IN-EST-OUT", Today.AddDays(2), Today.AddDays(-30), false, 2, true),
        // 只有预计日期（在途）
        ("C-EST-M8", null, Today.AddDays(-8), false, 1, false),
        ("C-EST-M7", null, Today.AddDays(-7), false, 1, true),
        ("C-EST-IN", null, Today.AddDays(5), false, 1, true),
        ("C-EST-P21", null, Today.AddDays(21), false, 1, true),
        ("C-EST-P22", null, Today.AddDays(22), false, 1, false),
        ("C-EST-FAR", null, Today.AddDays(30), false, 1, false),
        // 两个日期都为空：没有到仓库日期，不出现
        ("C-NO-DATE", null, null, false, 1, false),
        // 软删除不出现；状态为空的货柜与移动端一样照常出现（状态不参与到仓库日期口径）
        ("C-DELETED", Today.AddDays(1), null, true, 2, false),
        ("C-STATUS-NULL", Today.AddDays(3), null, false, null, true),
    ];

    private void SeedFixture()
    {
        foreach (var (code, actual, estimated, deleted, status, _) in Fixture)
        {
            database.Insertable(new Container
            {
                ContainerCode = code,
                ContainerNumber = $"NO-{code}",
                ActualArrivalDate = actual,
                EstimatedArrivalDate = estimated,
                IsDeleted = deleted,
                Status = status,
            }).ExecuteCommand();
            // 每个货柜一条商品明细：移动端按商品出条目，没有明细的货柜在移动端本来就不会有条目
            database.Insertable(new ContainerDetail { DetailCode = $"D-{code}", ContainerCode = code, ProductCode = $"P-{code}" }).ExecuteCommand();
        }
    }

    private static string[] ExpectedCodes() =>
        Fixture.Where(x => x.Expected).Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Web摘要_窗口边界与实际日期优先_只含到仓库日期在窗口内且未删除的货柜()
    {
        SeedFixture();

        var result = await CreateService().GetComingSoonSummariesAsync(Today);

        Assert.Equal(ExpectedCodes(), result.Select(x => x.HGUID!).OrderBy(x => x, StringComparer.Ordinal));
        // 映射沿用既有 ContainerMainDto：关键字段与货柜主表一致，Details 不预加载（恒为空）
        var edge = result.Single(x => x.HGUID == "C-ACT-IN-EST-OUT");
        Assert.Equal("NO-C-ACT-IN-EST-OUT", edge.货柜编号);
        Assert.Equal(Today.AddDays(2), edge.实际到货日期);
        Assert.Equal(Today.AddDays(-30), edge.预计到岸日期);
        Assert.All(result, x => Assert.Empty(x.Details));
    }

    [Fact]
    public async Task Web摘要_按到仓库日期升序_同日按货柜编号稳定排序()
    {
        SeedFixture();
        // 与 C-ACT-TODAY 同一天（预计日期），货柜编号更小，排在它前面
        database.Insertable(new Container { ContainerCode = "C-SAME-DAY-A", ContainerNumber = "AAA", EstimatedArrivalDate = Today }).ExecuteCommand();

        var result = await CreateService().GetComingSoonSummariesAsync(Today);

        var codes = result.Select(x => x.HGUID!).ToList();
        Assert.Equal(
            new[]
            {
                "C-ACT-M7", "C-ACT-M7-LATE", "C-EST-M7", // T-7（同日按货柜编号 NO-C-ACT-M7 < NO-C-ACT-M7-LATE < NO-C-EST-M7）
                "C-SAME-DAY-A", "C-ACT-TODAY", // T：货柜编号 AAA < NO-C-ACT-TODAY
                "C-ACT-IN-EST-OUT", "C-STATUS-NULL", "C-EST-IN", "C-ACT-P21", "C-ACT-P21-LATE", "C-EST-P21",
            },
            codes);
    }

    [Fact]
    public async Task Web摘要_超过100个货柜时不截断()
    {
        // 旧实现两段查询各 PageSize=100，超过就会静默丢货柜；新实现一次取全窗口
        for (var index = 0; index < 130; index++)
        {
            database.Insertable(new Container
            {
                ContainerCode = $"C-BULK-{index:D3}",
                ContainerNumber = $"BULK-{index:D3}",
                ActualArrivalDate = index % 2 == 0 ? Today.AddDays(index % 22 - 7) : null,
                EstimatedArrivalDate = index % 2 == 1 ? Today.AddDays(index % 22 - 7) : null,
            }).ExecuteCommand();
        }

        var result = await CreateService().GetComingSoonSummariesAsync(Today);

        Assert.Equal(130, result.Count);
        Assert.Equal(130, result.Select(x => x.HGUID).Distinct().Count());
    }

    [Fact]
    public async Task Web摘要_SQL条件与IsInWindow纯逻辑判定在所有日期组合上一致()
    {
        var offsets = new int?[] { null, -30, -9, -8, -7, -1, 0, 1, 20, 21, 22, 23, 40 };
        var rows = new List<(string Code, DateTime? Actual, DateTime? Estimated)>();
        foreach (var actualOffset in offsets)
        {
            foreach (var estimatedOffset in offsets)
            {
                var code = $"C-GRID-{actualOffset?.ToString() ?? "N"}-{estimatedOffset?.ToString() ?? "N"}";
                rows.Add((code, actualOffset is { } a ? Today.AddDays(a) : null, estimatedOffset is { } e ? Today.AddDays(e) : null));
            }
        }
        foreach (var (code, actual, estimated) in rows)
        {
            database.Insertable(new Container { ContainerCode = code, ContainerNumber = code, ActualArrivalDate = actual, EstimatedArrivalDate = estimated }).ExecuteCommand();
        }
        var (from, toExclusive) = ContainerWarehouseArrival.BuildWindow(Today);
        var expected = rows
            .Where(x => ContainerWarehouseArrival.IsInWindow(x.Actual, x.Estimated, from, toExclusive))
            .Select(x => x.Code)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var result = await CreateService().GetComingSoonSummariesAsync(Today);

        Assert.NotEmpty(expected);
        Assert.True(expected.Length < rows.Count);
        Assert.Equal(expected, result.Select(x => x.HGUID!).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Web摘要与移动端warehouse口径_同一份数据选出的货柜集合完全一致()
    {
        SeedFixture();
        var store = new Store { StoreGUID = "store-c", StoreCode = "S-C", StoreName = "S", Address = "Sydney NSW 2000" };
        database.Insertable(store).ExecuteCommand();

        // 移动端：NSW 门店在同一个 nowUtc 下的本地今天 = 悉尼今天；Web：同一个悉尼今天
        var mobile = await ContainerNewProductsReactService.QueryForStoreAsync(
            database, store, "S-C", includeExisting: true, NowUtc, CancellationToken.None,
            ContainerNewProductsWindowBasis.WarehouseArrival);
        var web = await CreateService().GetComingSoonSummariesAsync(ContainerWarehouseArrival.GetSydneyToday(NowUtc));

        Assert.Equal(DateOnly.FromDateTime(Today), mobile.LocalToday);
        var mobileCodes = mobile.Items.Select(x => x.ContainerCode).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var webCodes = web.Select(x => x.HGUID!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(mobileCodes);
        Assert.Equal(ExpectedCodes(), mobileCodes);
        Assert.Equal(mobileCodes, webCodes);
        // 到仓库日期的取法也一致：移动端条目上的到仓库日期 = Web 摘要按同一函数算出的日期
        foreach (var item in mobile.Items)
        {
            var dto = web.Single(x => x.HGUID == item.ContainerCode);
            Assert.Equal(
                DateOnly.FromDateTime(ContainerWarehouseArrival.ResolveDate(dto.实际到货日期, dto.预计到岸日期)!.Value),
                item.WarehouseArrivalDate);
        }
    }

    [Fact]
    public async Task 历史兼容入口GetComingSoonContainers_货柜选取与摘要接口一致_不再有8周范围()
    {
        // 该入口用「现在」的悉尼今天，夹具跟着真实时钟走；窗口外的 T+30（旧实现 56 天内会出现）和 T-8 都不应出现
        var today = ContainerWarehouseArrival.GetSydneyToday(DateTime.UtcNow);
        var rows = new (string Code, DateTime? Actual, DateTime? Estimated)[]
        {
            ("L-IN-ACT", today.AddDays(-3), null),
            ("L-IN-EST", null, today.AddDays(10)),
            ("L-OUT-FAR", null, today.AddDays(30)),
            ("L-OUT-OLD", today.AddDays(-8), null),
            ("L-ACT-OVER-EST", today.AddDays(-9), today.AddDays(2)),
        };
        foreach (var (code, actual, estimated) in rows)
        {
            database.Insertable(new Container { ContainerCode = code, ContainerNumber = code, ActualArrivalDate = actual, EstimatedArrivalDate = estimated, Status = 0 }).ExecuteCommand();
            database.Insertable(new ContainerDetail { DetailCode = $"D-{code}", ContainerCode = code, ProductCode = $"P-{code}" }).ExecuteCommand();
        }
        var service = CreateService();

        var legacy = await service.GetComingSoonContainersAsync();
        var summaries = await service.GetComingSoonSummariesAsync(today);

        Assert.Equal(new[] { "L-IN-ACT", "L-IN-EST" }, legacy.Select(x => x.货柜编码));
        Assert.Equal(summaries.Select(x => x.HGUID), legacy.Select(x => x.货柜编码));
        Assert.All(legacy, x => Assert.Single(x.商品列表));
    }

    [Fact]
    public async Task 悉尼与UTC日期错位时窗口按悉尼今天算()
    {
        // UTC 10-13 14:30 已是悉尼 10-14：基准日按悉尼算，T-7 = 10-07 才是下界
        var sydneyToday = ContainerWarehouseArrival.GetSydneyToday(new DateTime(2026, 10, 13, 14, 30, 0, DateTimeKind.Utc));
        database.Insertable(new Container { ContainerCode = "C-EDGE-IN", ContainerNumber = "IN", ActualArrivalDate = new DateTime(2026, 10, 7) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-EDGE-OUT", ContainerNumber = "OUT", ActualArrivalDate = new DateTime(2026, 10, 6) }).ExecuteCommand();

        var result = await CreateService().GetComingSoonSummariesAsync(sydneyToday);

        Assert.Equal(new DateTime(2026, 10, 14), sydneyToday);
        Assert.Equal(new[] { "C-EDGE-IN" }, result.Select(x => x.HGUID));
    }

    private ContainerReactService CreateService()
    {
        var mapper = new MapperConfiguration(
            cfg => cfg.AddProfile<ContainerMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();
        // 摘要与历史入口只用本地库；HQ / HBSales 上下文不会被访问，用未初始化对象占位即可
        return new ContainerReactService(
            CreateSqlSugarContext(database),
            (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext)),
            (HBSalesSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HBSalesSqlSugarContext)),
            new ConfigurationBuilder().Build(),
            mapper,
            NullLogger<ContainerReactService>.Instance,
            Mock.Of<IContainerHqSyncService>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>()
        );
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        database.Dispose();
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try { File.Delete(file); } catch { /* 临时库清理失败不影响测试结论 */ }
        }
    }
}
