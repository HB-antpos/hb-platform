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

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 进货单明细回填「本单分店当前的商品自动定价状态」（StoreRetailPrice.IsAutoPricing），
    /// 与明细自身的 AutoPricing（执行时写入的定价方案）互不影响。
    /// </summary>
    public sealed class LocalSupplierInvoiceStoreAutoPricingStatusTests : IDisposable
    {
        private const string InvoiceGuid = "invoice-store-auto-pricing";

        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocalSupplierInvoiceStoreAutoPricingStatusTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            });

            _db.CodeFirst.InitTables(
                typeof(Store),
                typeof(UserStore),
                typeof(HBLocalSupplier),
                typeof(Product),
                typeof(StoreRetailPrice),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails)
            );
            SqliteWarehouseProductChangeHistoryTable.Create(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetDetails_回填本单分店当前自动定价状态(bool paged)
        {
            await SeedAsync();

            List<LocalSupplierInvoiceItemDto> items;
            if (paged)
            {
                var response = await CreateService().GetDetailsGridAsync(InvoiceGuid, new GridRequestDto { PageSize = 50 });
                Assert.True(response.Success, response.Message);
                items = response.Items!;
            }
            else
            {
                var response = await CreateService().GetDetailsAsync(InvoiceGuid);
                Assert.True(response.Success, response.Message);
                items = response.Data!;
            }

            var byGuid = items.ToDictionary(item => item.DetailGUID);
            Assert.Equal(6, byGuid.Count);
            Assert.True(byGuid["D-auto"].StoreIsAutoPricing);
            Assert.False(byGuid["D-manual"].StoreIsAutoPricing);
            // 本店没有分店价格记录（只有别的分店有）：状态为空，不能借用别店的值。
            Assert.Null(byGuid["D-other-store-only"].StoreIsAutoPricing);
            // 本店唯一的记录已删除：按没有记录处理。
            Assert.Null(byGuid["D-deleted"].StoreIsAutoPricing);
            // 未关联商品编码。
            Assert.Null(byGuid["D-no-product"].StoreIsAutoPricing);
            // 同一分店商品有多条有效记录：取最近更新的那条。
            Assert.True(byGuid["D-duplicate"].StoreIsAutoPricing);

            // 明细自身的定价方案不受分店状态影响。
            Assert.True(byGuid["D-manual"].AutoPricing);
            Assert.False(byGuid["D-auto"].AutoPricing);
        }

        private async Task SeedAsync()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-1",
                StoreCode = "S01",
                StoreName = "Sydney Store",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new HBLocalSupplier
            {
                Guid = "supplier-guid-1",
                LocalSupplierCode = "SUP01",
                Name = "Local Supplier",
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoice
            {
                InvoiceGUID = InvoiceGuid,
                StoreCode = "S01",
                SupplierCode = "SUP01",
                InvoiceNo = InvoiceGuid,
                OrderDate = new DateTime(2026, 10, 23),
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            static StoreLocalSupplierInvoiceDetails Detail(string guid, string? code, bool autoPricing) => new()
            {
                DetailGUID = guid,
                InvoiceGUID = InvoiceGuid,
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = code,
                AutoPricing = autoPricing,
                IsDeleted = false,
            };
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                Detail("D-auto", "P-AUTO", autoPricing: false),
                Detail("D-manual", "P-MANUAL", autoPricing: true),
                Detail("D-other-store-only", "P-OTHER", autoPricing: true),
                Detail("D-deleted", "P-DELETED", autoPricing: true),
                Detail("D-no-product", null, autoPricing: true),
                Detail("D-duplicate", "P-DUP", autoPricing: true),
            }).ExecuteCommandAsync();

            var older = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var newer = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            static StoreRetailPrice Price(string store, string code, bool auto, DateTime updatedAt, bool deleted = false) => new()
            {
                UUID = Guid.NewGuid().ToString("N"),
                StoreCode = store,
                ProductCode = code,
                IsAutoPricing = auto,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
                IsDeleted = deleted,
            };
            await _db.Insertable(new List<StoreRetailPrice>
            {
                Price("S01", "P-AUTO", true, older),
                Price("S01", "P-MANUAL", false, older),
                Price("S02", "P-OTHER", true, older),
                Price("S01", "P-DELETED", true, older, deleted: true),
                Price("S01", "P-DUP", false, older),
                Price("S01", "P-DUP", true, newer),
            }).ExecuteCommandAsync();
        }

        private LocalSupplierInvoicesReactService CreateService()
        {
            return new LocalSupplierInvoicesReactService(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                Mock.Of<IAutoPricingService>(),
                WarehouseProductChangeHistoryTestDouble.CreateNoop(),
                null
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

        private static HqSqlSugarContext CreateHqSqlSugarContext()
        {
            var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
            typeof(HqSqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, new Mock<ISqlSugarClient>().Object);
            return context;
        }
    }
}
