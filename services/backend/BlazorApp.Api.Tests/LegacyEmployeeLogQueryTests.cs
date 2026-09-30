using DbType = System.Data.DbType;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
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
        Assert.Equal("1013", query!.StoreCode);
        Assert.Equal(new[] { "A", "B" }, query.EmployeeIds);
        Assert.Equal(new[] { "删除商品" }, query.Operations);
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.MaxPageSize, query.PageSize);
        Assert.Equal(1, query.PageNumber);
        Assert.False(query.Descending);
        Assert.Equal(DateTimeKind.Unspecified, query.From.Kind);
    }

    [Fact]
    public void BuildList_字符串参数全按varchar传且强制使用分店时间索引()
    {
        var request = ValidRequest();
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
        Assert.Equal(4, CountOccurrences(command.Sql, $"INDEX([{LegacyEmployeeLogSqlServerQuery.IndexName}])"));
        Assert.Equal(
            "%50[%][_]off[[]1]%",
            command.Parameters.Single(parameter => parameter.Name == "@KeywordPattern").Value
        );
    }

    [Fact]
    public void BuildList_操作类型条件只作用于列表不作用于计数()
    {
        var request = ValidRequest();
        request.Operations = ["删除商品"];
        request.Keyword = "xmascard2";
        var command = LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!);

        var statements = command.Sql.Split("OPTION (RECOMPILE);", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, statements.Length); // 四条语句 + 结尾空白
        Assert.DoesNotContain("@Operation0", statements[0]);
        Assert.Contains("@KeywordPattern", statements[0]);
        Assert.Contains("@Operation0", statements[1]);
        Assert.Contains("ORDER BY l.[OperationTime] DESC, l.[Id] DESC", statements[1]);
        // 员工、设备下拉只受分店与时间范围影响。
        Assert.DoesNotContain("@KeywordPattern", statements[2]);
        Assert.DoesNotContain("@KeywordPattern", statements[3]);
    }

    [Fact]
    public async Task QueryAsync_越权分店在访问数据库前返回Forbidden()
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(service => service.CanAccessStoreCodeAsync("1013")).ReturnsAsync(false);
        // 严格模式且不配置任何成员：一旦触碰数据库就抛异常。
        var db = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        var service = new LegacyEmployeeLogQueryService(
            db.Object, scope.Object, NullLogger<LegacyEmployeeLogQueryService>.Instance);

        var result = await service.QueryAsync(ValidRequest());

        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, result.Status);
        scope.Verify(service => service.CanAccessStoreCodeAsync("1013"), Times.Once);
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
