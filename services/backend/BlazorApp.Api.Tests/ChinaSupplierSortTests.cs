using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 国内供应商列表排序键契约。
/// 前端列头直接把 antd 的 <c>sorter.field</c>（createdAt / updatedAt）当 sortField 发送，
/// 后端必须识别；同时旧键（fgc_createdate / createdate 等）与默认排序行为保持不变。
/// </summary>
public sealed class ChinaSupplierSortTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public ChinaSupplierSortTests()
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
        _db.CodeFirst.InitTables(typeof(ChinaSupplier));

        // 三家供应商的“创建时间顺序”与“更新时间顺序”刻意错开，
        // 这样任何一个排序键被误当成另一个（或落入默认分支）都会被测出来：
        //   创建：S1 < S2 < S3
        //   更新：S2 < S3 < S1
        _db.Insertable(
                new[]
                {
                    Supplier("S1", created: "2026-01-01 10:00:00", modified: "2026-03-01 10:00:00"),
                    Supplier("S2", created: "2026-02-01 10:00:00", modified: "2026-01-15 10:00:00"),
                    Supplier("S3", created: "2026-03-01 10:00:00", modified: "2026-02-10 10:00:00"),
                }
            )
            .ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    [Theory]
    [InlineData("createdAt", "asc", new[] { "S1", "S2", "S3" })]
    [InlineData("createdAt", "desc", new[] { "S3", "S2", "S1" })]
    [InlineData("updatedAt", "asc", new[] { "S2", "S3", "S1" })]
    [InlineData("updatedAt", "desc", new[] { "S1", "S3", "S2" })]
    public async Task 前端列头排序键_按显示的创建更新时间排序(
        string sortField,
        string sortDirection,
        string[] expected
    )
    {
        Assert.Equal(expected, await QueryCodesAsync(sortField, sortDirection));
    }

    [Theory]
    [InlineData("CREATEDAT", "asc", new[] { "S1", "S2", "S3" })]
    [InlineData("UpdatedAt", "desc", new[] { "S1", "S3", "S2" })]
    public async Task 新排序键大小写不敏感(string sortField, string sortDirection, string[] expected)
    {
        Assert.Equal(expected, await QueryCodesAsync(sortField, sortDirection));
    }

    [Theory]
    [InlineData("fgC_CreateDate", "asc", new[] { "S1", "S2", "S3" })]
    [InlineData("createdate", "asc", new[] { "S1", "S2", "S3" })]
    [InlineData("fgc_createdate", "desc", new[] { "S3", "S2", "S1" })]
    public async Task 旧排序键保持兼容(string sortField, string sortDirection, string[] expected)
    {
        Assert.Equal(expected, await QueryCodesAsync(sortField, sortDirection));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "asc")]
    [InlineData("no-such-column", "asc")]
    public async Task 未提供或无法识别的排序键_仍按创建时间倒序(string? sortField, string? sortDirection)
    {
        Assert.Equal(new[] { "S3", "S2", "S1" }, await QueryCodesAsync(sortField, sortDirection));
    }

    private async Task<string[]> QueryCodesAsync(string? sortField, string? sortDirection)
    {
        var result = await CreateService()
            .GetChinaSuppliersAsync(
                new ChinaSupplierQueryDto
                {
                    Page = 1,
                    PageSize = 20,
                    SortField = sortField,
                    SortDirection = sortDirection,
                }
            );

        Assert.True(result.Success, result.Message);
        return result.Data!.Items!.Select(item => item.SupplierCode!).ToArray();
    }

    private static ChinaSupplier Supplier(string code, string created, string modified) =>
        new()
        {
            Guid = $"guid-{code}",
            SupplierCode = code,
            SupplierName = $"供应商{code}",
            Status = 1,
            FGC_CreateDate = created,
            FGC_LastModifyDate = modified,
            IsDeleted = false,
        };

    private ChinaSupplierService CreateService()
    {
        // 列表查询只用到 _context.Db，HBSales 上下文与该路径无关。
        return new ChinaSupplierService(
            CreateSqlSugarContext(_db),
            null!,
            NullLogger<ChinaSupplierService>.Instance
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
