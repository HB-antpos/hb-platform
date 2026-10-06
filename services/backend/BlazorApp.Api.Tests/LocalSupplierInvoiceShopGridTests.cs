using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalSupplierInvoiceShopGridTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly List<string> _executedSql = new();

    public LocalSupplierInvoiceShopGridTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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
        _db.Aop.OnLogExecuting = (sql, _) => _executedSql.Add(sql);
        _db.CodeFirst.InitTables<
            Store,
            HBLocalSupplier,
            StoreLocalSupplierInvoice,
            StoreLocalSupplierInvoiceDetails
        >();
        SqliteWarehouseProductChangeHistoryTable.Create(_db);
    }

    [Fact]
    public async Task ProductKeyword_DoesNotMatchADetailFromAnotherStore()
    {
        await SeedStoresAndSupplierAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "invoice-s01",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                InvoiceNo = "INV-S01",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-cross-store",
                InvoiceGUID = "invoice-s01",
                StoreCode = "S02",
                SupplierCode = "SUP01",
                ItemNumber = "TARGET-ITEM",
                ProductName = "Cross-store detail",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        _executedSql.Clear();

        var result = await CreateService().GetGridDataAsync(
            KeywordRequest("S01", "TARGET-ITEM"),
            new List<string> { "S01" }
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items!);
        AssertDatabaseExistsQueryWasUsed();
    }

    [Fact]
    public async Task ProductKeyword_LargeMatchingSetStaysScopedAndPagedInDatabase()
    {
        const int allowedStoreInvoiceCount = 1200;
        const int otherStoreInvoiceCount = 120;
        await SeedStoresAndSupplierAsync();
        var invoices = Enumerable.Range(1, allowedStoreInvoiceCount)
            .Select(index => Invoice($"s01-{index:0000}", "S01", $"S01-{index:0000}"))
            .Concat(
                Enumerable.Range(1, otherStoreInvoiceCount)
                    .Select(index => Invoice($"s02-{index:0000}", "S02", $"S02-{index:0000}"))
            )
            .ToList();
        var details = invoices
            .Select((invoice, index) =>
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = $"detail-{index:0000}",
                    InvoiceGUID = invoice.InvoiceGUID,
                    StoreCode = invoice.StoreCode,
                    SupplierCode = invoice.SupplierCode,
                    ItemNumber = $"BULK-MATCH-{index:0000}",
                    ProductName = "Bulk product",
                    IsDeleted = false,
                }
            )
            .ToList();
        foreach (var batch in invoices.Chunk(250))
        {
            await _db.Insertable(batch).ExecuteCommandAsync();
        }
        foreach (var batch in details.Chunk(250))
        {
            await _db.Insertable(batch).ExecuteCommandAsync();
        }
        _executedSql.Clear();

        var result = await CreateService().GetGridDataAsync(
            KeywordRequest("S01", "BULK-MATCH"),
            new List<string> { "S01" }
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(allowedStoreInvoiceCount, result.Total);
        Assert.Equal(20, result.Items!.Count);
        Assert.All(result.Items, item => Assert.Equal("S01", item.StoreCode));
        AssertDatabaseExistsQueryWasUsed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void AssertDatabaseExistsQueryWasUsed()
    {
        var firstSelect = Assert.Single(
            _executedSql.TakeWhile(sql => !sql.Contains("LIMIT 20", StringComparison.OrdinalIgnoreCase))
                .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                .Take(1)
        );
        Assert.Contains("EXISTS", firstSelect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "StoreLocalSupplierInvoiceDetails",
            firstSelect,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private async Task SeedStoresAndSupplierAsync()
    {
        await _db.Insertable(
            new[]
            {
                new Store
                {
                    StoreGUID = "store-1",
                    StoreCode = "S01",
                    StoreName = "Sydney",
                    IsActive = true,
                    IsDeleted = false,
                },
                new Store
                {
                    StoreGUID = "store-2",
                    StoreCode = "S02",
                    StoreName = "Melbourne",
                    IsActive = true,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new HBLocalSupplier
            {
                Guid = "supplier-1",
                LocalSupplierCode = "SUP01",
                Name = "Supplier One",
                Status = 1,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private static StoreLocalSupplierInvoice Invoice(
        string invoiceGuid,
        string storeCode,
        string invoiceNo
    ) =>
        new()
        {
            InvoiceGUID = invoiceGuid,
            StoreCode = storeCode,
            SupplierCode = "SUP01",
            InvoiceNo = invoiceNo,
            OrderDate = new DateTime(2026, 8, 1),
            IsDeleted = false,
        };

    private static GridRequestDto KeywordRequest(string storeCode, string keyword)
    {
        return new GridRequestDto
        {
            StartRow = 0,
            PageSize = 20,
            FilterModel = new Dictionary<string, FilterModelDto>
            {
                ["StoreCode"] = new()
                {
                    FilterType = "text",
                    Type = "equals",
                    Filter = storeCode,
                },
                ["ProductKeyword"] = new()
                {
                    FilterType = "text",
                    Type = "contains",
                    Filter = keyword,
                },
            },
        };
    }

    private LocalSupplierInvoicesReactService CreateService()
    {
        return new LocalSupplierInvoicesReactService(
            CreateSqlSugarContext(_db),
            CreateHqSqlSugarContext(),
            Mock.Of<IMapper>(),
            NullLogger<LocalSupplierInvoicesReactService>.Instance,
            Mock.Of<IAutoPricingService>(),
            WarehouseProductChangeHistoryTestDouble.CreateNoop()
        );
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        dbField.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext()
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HqSqlSugarContext)
        );
        var dbField = typeof(HqSqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        dbField.SetValue(context, Mock.Of<ISqlSugarClient>());
        return context;
    }
}
