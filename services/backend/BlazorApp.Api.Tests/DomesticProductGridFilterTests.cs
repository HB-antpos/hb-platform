using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 国内商品列表（POST grid）的筛选与分页契约：
/// 无论叠加多少筛选条件，返回的 total 必须等于“逐页翻完实际拿到的条数”，
/// 且列筛选（供应商/类型/状态）对列表本身也必须生效。
/// </summary>
public sealed class DomesticProductGridFilterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public DomesticProductGridFilterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        // grid 对套装商品（ProductType > 0）会再查一次套装明细数量，故需要 DomesticSetProduct 表
        _db.CodeFirst.InitTables(typeof(DomesticProduct), typeof(DomesticSetProduct), typeof(ChinaSupplier));
        SeedData();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    [Fact]
    public async Task 无筛选_total等于未删除商品总数()
    {
        var (total, codes) = await FetchAllPagesAsync(new GridRequestDto());

        Assert.Equal(6, total);
        Assert.Equal(6, codes.Count);
        // 软删除的商品不计入
        Assert.DoesNotContain("A5-DELETED", codes);
    }

    [Fact]
    public async Task 供应商编码筛选_total只统计该供应商()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["supplierCode"] = TextFilter("contains", "SUP-A"),
                },
            }
        );

        Assert.Equal(4, total);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, codes.OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task 商品类型筛选_total只统计该类型()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["productType"] = SetFilter("0"),
                },
            }
        );

        Assert.Equal(3, total);
        Assert.Equal(new[] { "A1", "A2", "B1" }, codes.OrderBy(code => code).ToArray());
    }

    [Theory]
    [InlineData("true", new[] { "A1", "A3", "B1" })]
    [InlineData("false", new[] { "A2", "A4", "B2" })]
    public async Task 启用状态筛选_列表与total都按状态过滤(string isActive, string[] expected)
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["isActive"] = SetFilter(isActive),
                },
            }
        );

        Assert.Equal(expected.Length, total);
        Assert.Equal(expected, codes.OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task 启用状态同时勾选两个值_等同不筛选()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["isActive"] = SetFilter("true", "false"),
                },
            }
        );

        Assert.Equal(6, total);
        Assert.Equal(6, codes.Count);
    }

    [Fact]
    public async Task 关键词叠加供应商筛选_total与翻页条数一致()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                GlobalSearch = "杯子",
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["supplierCode"] = TextFilter("contains", "SUP-A"),
                },
            }
        );

        // “杯子”共 A1/A3/B1 三个，叠加 SUP-A 后只剩 A1/A3
        Assert.Equal(2, total);
        Assert.Equal(new[] { "A1", "A3" }, codes.OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task 供应商_类型_状态_关键词全部叠加_total与翻页条数一致()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                GlobalSearch = "红色",
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["supplierCode"] = TextFilter("contains", "SUP-A"),
                    ["productType"] = SetFilter("0"),
                    ["isActive"] = SetFilter("true"),
                },
            }
        );

        Assert.Equal(1, total);
        Assert.Equal(new[] { "A1" }, codes.ToArray());
    }

    [Fact]
    public async Task 筛选无结果_total为0且不返回任何行()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["supplierCode"] = TextFilter("contains", "SUP-NOT-EXIST"),
                },
            }
        );

        Assert.Equal(0, total);
        Assert.Empty(codes);
    }

    [Fact]
    public async Task 排序不影响total()
    {
        var (total, codes) = await FetchAllPagesAsync(
            new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["productType"] = SetFilter("0"),
                },
                SortModel = new List<SortModelDto> { new() { ColId = "name", Sort = "asc" } },
            }
        );

        Assert.Equal(3, total);
        Assert.Equal(3, codes.Count);
    }

    /// <summary>
    /// 以每页 2 条逐页翻完，返回“服务端声明的 total”与“实际翻出的商品编码”。
    /// 同时断言每一页返回的 total 一致，且翻页过程中没有重复行。
    /// </summary>
    private async Task<(int Total, List<string> Codes)> FetchAllPagesAsync(GridRequestDto request)
    {
        const int pageSize = 2;
        var service = CreateService();
        var codes = new List<string>();
        int? reportedTotal = null;

        for (var startRow = 0; startRow < 100; startRow += pageSize)
        {
            request.StartRow = startRow;
            request.EndRow = startRow + pageSize;
            request.PageSize = pageSize;

            var response = await service.GetGridDataAsync(request);
            Assert.True(response.Success, response.Message);
            reportedTotal ??= response.Total;
            Assert.Equal(reportedTotal, response.Total);

            if (response.Items == null || response.Items.Count == 0)
                break;

            codes.AddRange(response.Items.Select(item => item.ProductCode));
        }

        Assert.NotNull(reportedTotal);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        return (reportedTotal!.Value, codes);
    }

    private static FilterModelDto TextFilter(string type, string value) =>
        new()
        {
            FilterType = "text",
            Type = type,
            Filter = value,
        };

    private static FilterModelDto SetFilter(params string[] values) =>
        new() { FilterType = "set", Values = values.ToList() };

    private void SeedData()
    {
        _db.Insertable(
                new[]
                {
                    new ChinaSupplier { Guid = "g-a", SupplierCode = "SUP-A", SupplierName = "供应商甲", IsDeleted = false },
                    new ChinaSupplier { Guid = "g-b", SupplierCode = "SUP-B", SupplierName = "供应商乙", IsDeleted = false },
                }
            )
            .ExecuteCommand();

        _db.Insertable(
                new[]
                {
                    Product("A1", "SUP-A", "红色杯子", type: 0, active: true),
                    Product("A2", "SUP-A", "红色盘子", type: 0, active: false),
                    Product("A3", "SUP-A", "蓝色杯子", type: 1, active: true),
                    Product("A4", "SUP-A", "绿色碗", type: 2, active: false),
                    Product("B1", "SUP-B", "金色杯子", type: 0, active: true),
                    Product("B2", "SUP-B", "黄色碗", type: 1, active: false),
                    // 软删除商品：无论怎样筛选都不应出现在 total 与列表里
                    Product("A5-DELETED", "SUP-A", "红色杯子（已删除）", type: 0, active: true, deleted: true),
                }
            )
            .ExecuteCommand();
    }

    private static DomesticProduct Product(
        string code,
        string supplierCode,
        string name,
        int type,
        bool active,
        bool deleted = false
    ) =>
        new()
        {
            ProductCode = code,
            SupplierCode = supplierCode,
            ProductName = name,
            HBProductNo = $"HB-{code}",
            Barcode = $"BC-{code}",
            ProductType = type,
            IsActive = active,
            IsDeleted = deleted,
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 1, 1),
        };

    private DomesticProductReactService CreateService()
    {
        // 只有 grid 查询路径会被调用，它只依赖 _context.Db；
        // 其余协作者与该路径无关，用 null/Mock 占位即可。
        return new DomesticProductReactService(
            CreateSqlSugarContext(_db),
            null!,
            Mock.Of<AutoMapper.IMapper>(),
            NullLogger<DomesticProductReactService>.Instance,
            null!,
            null!,
            Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>()
        );
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
