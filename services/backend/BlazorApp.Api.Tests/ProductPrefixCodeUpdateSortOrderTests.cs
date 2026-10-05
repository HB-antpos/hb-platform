using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Mappings.Profiles;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 前缀管理 PUT：请求里没带 SortOrder（null）时必须保持库里原值，
/// 显式给出的值（含 0）才覆盖。否则任何不涉及排序的编辑都会把排序悄悄重置。
/// </summary>
public sealed class ProductPrefixCodeUpdateSortOrderTests : IDisposable
{
    private const string PrefixCode = "prefix-001";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly IMapper _mapper;

    public ProductPrefixCodeUpdateSortOrderTests()
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
        _db.CodeFirst.InitTables(typeof(ProductPrefixCode), typeof(ChinaSupplier));

        // 使用真实的映射配置：缺陷出在 AutoMapper 的“null 也覆盖”默认行为上
        _mapper = new MapperConfiguration(
            cfg => cfg.AddProfile<ProductPrefixCodeMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

        _db.Insertable(
                new ChinaSupplier { Guid = "g-1", SupplierCode = "SUP-1", SupplierName = "供应商一", IsDeleted = false }
            )
            .ExecuteCommand();
        _db.Insertable(
                new ProductPrefixCode
                {
                    PrefixCode = PrefixCode,
                    SupplierCode = "SUP-1",
                    PrefixName = "AB",
                    PrefixDescription = "原说明",
                    IsActive = true,
                    SortOrder = 7,
                    IsDeleted = false,
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

    [Fact]
    public async Task 未提供SortOrder_保持库里原值()
    {
        var result = await UpdateAsync(new UpdateProductPrefixCodeDto
        {
            PrefixName = "AB",
            PrefixDescription = "新说明",
            IsActive = true,
            SortOrder = null,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(7, result.Data!.SortOrder);
        Assert.Equal(7, (await LoadAsync()).SortOrder);
        // 其余字段照常更新，证明不是整次更新被跳过
        Assert.Equal("新说明", (await LoadAsync()).PrefixDescription);
    }

    [Fact]
    public async Task 显式提供0_按请求覆盖为0()
    {
        var result = await UpdateAsync(new UpdateProductPrefixCodeDto
        {
            PrefixName = "AB",
            IsActive = true,
            SortOrder = 0,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, (await LoadAsync()).SortOrder);
    }

    [Fact]
    public async Task 显式提供新值_按请求覆盖()
    {
        var result = await UpdateAsync(new UpdateProductPrefixCodeDto
        {
            PrefixName = "AB",
            IsActive = true,
            SortOrder = 42,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(42, (await LoadAsync()).SortOrder);
    }

    [Fact]
    public async Task 原值为空且未提供SortOrder_仍为空()
    {
        await _db.Updateable<ProductPrefixCode>()
            .SetColumns(p => p.SortOrder == null)
            .Where(p => p.PrefixCode == PrefixCode)
            .ExecuteCommandAsync();

        var result = await UpdateAsync(new UpdateProductPrefixCodeDto
        {
            PrefixName = "AB",
            IsActive = true,
        });

        Assert.True(result.Success, result.Message);
        Assert.Null((await LoadAsync()).SortOrder);
    }

    /// <summary>
    /// 映射层本身的契约：旧版（非 React）前缀服务共用这份映射，同样受益。
    /// </summary>
    [Fact]
    public void 更新映射_SortOrder为空时不覆盖目标原值()
    {
        var entity = new ProductPrefixCode { PrefixName = "AB", SortOrder = 7 };

        _mapper.Map(new UpdateProductPrefixCodeDto { PrefixName = "CD", SortOrder = null }, entity);

        Assert.Equal("CD", entity.PrefixName);
        Assert.Equal(7, entity.SortOrder);
    }

    private async Task<ApiResponse<ProductPrefixCodeDto>> UpdateAsync(
        UpdateProductPrefixCodeDto dto
    )
    {
        var service = new ProductPrefixCodeReactService(
            CreateSqlSugarContext(_db),
            _mapper,
            NullLogger<ProductPrefixCodeReactService>.Instance
        );
        return await service.UpdateProductPrefixCodeAsync(PrefixCode, dto);
    }

    private Task<ProductPrefixCode> LoadAsync() =>
        _db.Queryable<ProductPrefixCode>().Where(p => p.PrefixCode == PrefixCode).FirstAsync();

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
