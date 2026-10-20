using DbType = System.Data.DbType;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LegacyEmployeeLogQueryTests
{
    private static LegacyEmployeeLogQueryDto ValidRequest() => new()
    {
        StoreCode = " 1013 ",
        From = new DateTime(2026, 9, 30),
        To = new DateTime(2026, 10, 1),
    };

    [Fact]
    public void Normalize_分店与时间范围必填()
    {
        var noStore = ValidRequest();
        noStore.StoreCode = "  ";
        Assert.Equal("请选择分店", LegacyEmployeeLogSqlServerQuery.Normalize(noStore).Error);

        var noRange = ValidRequest();
        noRange.To = null;
        Assert.Equal("请选择操作时间范围", LegacyEmployeeLogSqlServerQuery.Normalize(noRange).Error);

        var reversed = ValidRequest();
        reversed.To = reversed.From;
        Assert.NotNull(LegacyEmployeeLogSqlServerQuery.Normalize(reversed).Error);
    }

    [Fact]
    public void Normalize_时间范围最长31天()
    {
        var request = ValidRequest();
        request.To = request.From!.Value.AddDays(31);
        Assert.Null(LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);

        request.To = request.From!.Value.AddDays(31).AddSeconds(1);
        Assert.Equal("时间范围最长 31 天", LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
    }

    [Fact]
    public void Normalize_超长筛选值直接拒绝而不是丢弃放宽条件()
    {
        var request = ValidRequest();
        request.EmployeeIds = ["A", new string('x', 51)];
        Assert.NotNull(LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);

        request = ValidRequest();
        request.Operations = Enumerable.Range(0, 65).Select(index => $"op{index}").ToList();
        Assert.NotNull(LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);

        request = ValidRequest();
        request.Keyword = new string('k', 101);
        Assert.NotNull(LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
    }

    [Fact]
    public void Normalize_裁剪去重并限制分页()
    {
        var request = ValidRequest();
        request.EmployeeIds = [" A ", "a", "", "B"];
        request.Operations = ["删除商品", " 删除商品 "];
        request.PageSize = 5000;
        request.PageNumber = -3;
        request.SortOrder = "ASC";

        var (query, error) = LegacyEmployeeLogSqlServerQuery.Normalize(request);

        Assert.Null(error);
        Assert.Equal(new[] { "1013" }, query!.StoreCodes);
        Assert.Equal(new[] { "A", "B" }, query.EmployeeIds);
        Assert.Equal(new[] { "删除商品" }, query.Operations);
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.MaxPageSize, query.PageSize);
        Assert.Equal(1, query.PageNumber);
        Assert.False(query.Descending);
        Assert.Equal(DateTimeKind.Unspecified, query.From.Kind);
    }

    [Fact]
    public void Normalize_分店可多选并与单店旧参数合并去重()
    {
        var request = ValidRequest();
        request.StoreCodes = ["1022", " 1013", "1022", ""];
        Assert.Equal(new[] { "1022", "1013" }, LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!.StoreCodes);

        request = ValidRequest();
        request.StoreCode = null;
        request.StoreCodes = [" "];
        Assert.Equal("请选择分店", LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);

        request.StoreCodes = Enumerable.Range(0, LegacyEmployeeLogSqlServerQuery.MaxStoreFilters + 1).Select(index => $"S{index}").ToList();
        Assert.NotNull(LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
    }

    [Fact]
    public void BuildList_字符串参数全按varchar传且强制使用分店时间索引()
    {
        var request = ValidRequest();
        request.StoreCodes = ["1022"];
        request.DeviceCode = "POS_1013_1047";
        request.EmployeeIds = ["19915D4B-569E-4A66-A9CF-D4CB43BEA7E0"];
        request.Operations = ["删除商品", "开钱箱"];
        request.Keyword = "50%_off[1]";
        var query = LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!;

        var command = LegacyEmployeeLogSqlServerQuery.BuildList(query);

        // nvarchar 参数会让 varchar 列发生隐式转换，索引只能退化为扫描。
        Assert.All(
            command.Parameters.Where(parameter => parameter.Value is string),
            parameter => Assert.Equal(DbType.AnsiString, parameter.DbType)
        );
        Assert.All(
            command.Parameters.Where(parameter => parameter.Value is DateTime),
            parameter => Assert.Equal(DbType.DateTime, parameter.DbType)
        );
        // 关键字路径：守卫计数、命中落表、员工选项、设备选项四处都经由分店时间索引。
        Assert.Equal(4, CountOccurrences(command.Sql, $"INDEX([{LegacyEmployeeLogSqlServerQuery.IndexName}])"));
        Assert.Contains("l.[StoreCode] IN (@Store0, @Store1)", command.Sql);
        Assert.Equal(
            "%50[%][_]off[[]1]%",
            command.Parameters.Single(parameter => parameter.Name == "@KeywordPattern").Value
        );
    }

    [Fact]
    public void BuildList_无关键字时先按索引分页再回表且操作类型只作用于列表()
    {
        var request = ValidRequest();
        request.Operations = ["删除商品"];
        var command = LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!);

        var statements = command.Sql.Split("OPTION (RECOMPILE);", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, statements.Length); // 四条语句 + 结尾空白
        Assert.DoesNotContain("@Operation0", statements[0]);
        // 分页只在索引里取时间和主键排序，排好后再按主键回表取详情（多店时直接带详情排序要 30 秒）。
        Assert.Contains("WITH p AS", statements[1]);
        Assert.Contains("SELECT l.[Id], l.[OperationTime]", statements[1]);
        Assert.Contains("@Operation0", statements[1]);
        Assert.Contains("JOIN [dbo].[EmployeeLogs] AS x WITH (NOLOCK) ON x.[Id] = p.[Id]", statements[1]);
        Assert.Contains("ORDER BY p.[OperationTime] DESC, p.[Id] DESC", statements[1]);
        Assert.DoesNotContain("@ScanLimit", command.Sql);
    }

    [Fact]
    public void BuildList_关键字先守卫扫描行数再落临时表共用()
    {
        var request = ValidRequest();
        request.Operations = ["删除商品"];
        request.Keyword = "xmascard2";
        var command = LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!, 1234);

        Assert.Equal(1234L, command.Parameters.Single(parameter => parameter.Name == "@ScanLimit").Value);
        var guard = command.Sql.IndexOf("IF @ScanRows > @ScanLimit RETURN;", StringComparison.Ordinal);
        var insert = command.Sql.IndexOf("INSERT INTO #hb_legacy_log_hits", StringComparison.Ordinal);
        Assert.True(guard > 0 && insert > guard, "守卫必须在逐行回表的 LIKE 之前");
        // 守卫计数不带关键字（只数要扫描的范围），LIKE 只在落表时做一次。
        Assert.DoesNotContain("@KeywordPattern", command.Sql[..guard]);
        Assert.Equal(1, CountOccurrences(command.Sql, "LIKE @KeywordPattern"));
        // 操作类型条件只作用于分页，计数保留全部类型。
        var countStatement = command.Sql[insert..command.Sql.IndexOf("WITH p AS", StringComparison.Ordinal)];
        Assert.DoesNotContain("@Operation0", countStatement);
        Assert.Contains("WHERE h.[Operation] IN (@Operation0)", command.Sql);
        Assert.Contains("COLLATE DATABASE_DEFAULT", command.Sql);
        // 变量赋值式计数在生产上要 11 秒以上，必须经由表变量。
        Assert.Contains("INSERT INTO @ScanCount", command.Sql);
        Assert.DoesNotContain("SELECT @ScanRows = COUNT_BIG", command.Sql);
    }

    [Fact]
    public async Task QueryAsync_越权分店在访问数据库前返回Forbidden()
    {
        // 严格模式只配置查看口径：服务若仍走可管理分店（主分店）口径会直接抛异常。
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        scope.Setup(service => service.GetAssignedStoreScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsStoreManager = true,
            StoreCodes = ["1022"],
        });
        // 严格模式且不配置任何成员：一旦触碰数据库就抛异常。
        var db = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        var service = new LegacyEmployeeLogQueryService(
            db.Object, scope.Object, NullLogger<LegacyEmployeeLogQueryService>.Instance);

        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, (await service.QueryAsync(ValidRequest())).Status);

        // 多选里只要有一家越权就整次拒绝，不静默剔除。
        var mixed = ValidRequest();
        mixed.StoreCodes = ["1022"];
        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, (await service.QueryAsync(mixed)).Status);
    }

    [Fact]
    public async Task QueryAsync_条件无效时不检查权限也不访问数据库()
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        var db = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        var service = new LegacyEmployeeLogQueryService(
            db.Object, scope.Object, NullLogger<LegacyEmployeeLogQueryService>.Instance);
        var request = ValidRequest();
        request.StoreCode = null;

        var result = await service.QueryAsync(request);

        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, result.Status);
        Assert.Equal("请选择分店", result.Message);
    }

    [Fact]
    public void BuildEmployeeSummary_设备选项只受分店和时间约束_不带设备条件_剔除空设备码()
    {
        var request = ValidRequest();
        request.DeviceCode = "POS_1013_1047";
        var command = LegacyEmployeeLogSqlServerQuery.BuildEmployeeSummary(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!);

        // 设备选项是最后一条语句：走分店时间索引、按设备分组，且不含设备条件。
        var devices = command.Sql[command.Sql.LastIndexOf("SELECT l.[DeviceCode], COUNT_BIG(*)", StringComparison.Ordinal)..];
        Assert.Contains("GROUP BY l.[DeviceCode]", devices);
        Assert.Contains(LegacyEmployeeLogSqlServerQuery.IndexName, devices);
        Assert.Contains("l.[DeviceCode] IS NOT NULL AND l.[DeviceCode] <> ''", devices);
        Assert.DoesNotContain("@DeviceCode", devices);
        // 员工汇总本身仍按设备条件过滤。
        Assert.Contains("@DeviceCode", command.Sql);
    }

    [Fact]
    public void BuildBadgeSummary_只读标记表_按记录去重_待核查与危险口径()
    {
        var from = new DateTime(2026, 10, 13, 9, 0, 0);
        var command = LegacyEmployeeLogSqlServerQuery.BuildBadgeSummary(["1022", "1013"], from);

        // 不碰 EmployeeLogs 大表，只读规则标记表与核查结论表。
        Assert.DoesNotContain("[EmployeeLogs]", command.Sql);
        Assert.Contains("[dbo].[LegacyEmployeeLogFlags] AS f WITH (NOLOCK)", command.Sql);
        Assert.Contains("f.[StoreCode] IN (@Store0, @Store1)", command.Sql);
        Assert.Contains("f.[OperationTime] >= @From", command.Sql);
        Assert.Contains("f.[RetractedAtUtc] IS NULL", command.Sql);
        // 待核查：没有「确认正常(1) / 需跟进(2)」结论；撤销(0)视同待核查。
        Assert.Contains("NOT EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogReviews]", command.Sql);
        Assert.Contains("r.[Result] IN (1, 2)", command.Sql);
        // 同一条记录命中多条规则只计一次。
        Assert.Equal(1, CountOccurrences(command.Sql, "COUNT(DISTINCT p.[LogId])"));
        Assert.Equal(1, CountOccurrences(command.Sql, "THEN p.[LogId] END)"));
        // 危险清单全部作为 varchar 参数传入，窗口起点按 datetime 传。
        Assert.Equal(
            LegacyEmployeeLogRiskCatalog.DangerOperations.Count,
            command.Parameters.Count(parameter => parameter.Name.StartsWith("@Danger", StringComparison.Ordinal)));
        Assert.All(
            command.Parameters.Where(parameter => parameter.Value is string),
            parameter => Assert.Equal(DbType.AnsiString, parameter.DbType));
        Assert.Equal(from, command.Parameters.Single(parameter => parameter.Name == "@From").Value);
    }

    [Fact]
    public void BuildBadgeSummary_管理员不限分店()
    {
        var command = LegacyEmployeeLogSqlServerQuery.BuildBadgeSummary(null, new DateTime(2026, 10, 13));

        Assert.DoesNotContain("StoreCode", command.Sql);
        Assert.DoesNotContain(command.Parameters, parameter => parameter.Name.StartsWith("@Store", StringComparison.Ordinal));
    }

    [Fact]
    public void 角标辅助_天数默认7并限制在1到31_缓存键只看范围与天数()
    {
        Assert.Equal(7, EmployeeLogBadgeSupport.NormalizeDays(null));
        Assert.Equal(1, EmployeeLogBadgeSupport.NormalizeDays(0));
        Assert.Equal(1, EmployeeLogBadgeSupport.NormalizeDays(-5));
        Assert.Equal(31, EmployeeLogBadgeSupport.NormalizeDays(100));
        Assert.Equal(14, EmployeeLogBadgeSupport.NormalizeDays(14));

        // 分店顺序不影响键；管理员与具体分店互不相同；天数不同键不同。
        var a = EmployeeLogBadgeSupport.BuildCacheKey("k", false, ["1022", "1013"], 7);
        Assert.Equal(a, EmployeeLogBadgeSupport.BuildCacheKey("k", false, ["1013", "1022"], 7));
        Assert.NotEqual(a, EmployeeLogBadgeSupport.BuildCacheKey("k", false, ["1013"], 7));
        Assert.NotEqual(a, EmployeeLogBadgeSupport.BuildCacheKey("k", false, ["1022", "1013"], 8));
        Assert.NotEqual(EmployeeLogBadgeSupport.BuildCacheKey("k", true, [], 7), EmployeeLogBadgeSupport.BuildCacheKey("k", false, [], 7));
        // 分店很多时键长度有上限。
        var many = Enumerable.Range(0, 200).Select(index => $"S{index:000}");
        Assert.True(EmployeeLogBadgeSupport.BuildCacheKey("k", false, many, 7).Length < 100);
    }

    [Fact]
    public void 角标辅助_窗口起点是悉尼墙钟时间回退指定天数()
    {
        // 2026-10-20 00:00 UTC = 悉尼夏令时（UTC+11）11:00。
        var start = EmployeeLogBadgeSupport.WallClockWindowStart(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), 7);

        Assert.Equal(new DateTime(2026, 10, 13, 11, 0, 0), start);
        Assert.Equal(DateTimeKind.Unspecified, start.Kind);
    }

    [Fact]
    public async Task GetBadgeSummaryAsync_无权限或无可见分店时返回零且不访问数据库()
    {
        // 严格模式且不配置任何成员：一旦触碰数据库就抛异常。
        var db = new Mock<ISqlSugarClient>(MockBehavior.Strict);

        foreach (var scope in new[]
        {
            new CurrentUserManageableStoreScope { IsAllowed = false },
            new CurrentUserManageableStoreScope { IsAllowed = true, IsStoreManager = true, StoreCodes = [" ", ""] },
        })
        {
            var scopeService = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
            scopeService.Setup(service => service.GetAssignedStoreScopeAsync()).ReturnsAsync(scope);
            var service = new LegacyEmployeeLogQueryService(
                db.Object, scopeService.Object, NullLogger<LegacyEmployeeLogQueryService>.Instance);

            var badge = await service.GetBadgeSummaryAsync();

            Assert.Equal((0, 0), (badge.AbnormalPending, badge.DangerPending));
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
