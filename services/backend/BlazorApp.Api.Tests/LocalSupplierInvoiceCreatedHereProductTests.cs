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
    /// 「本单新品」：明细关联的商品由本进货单新建。主判据是商品变更历史的 Create 记录（来源单据为本单），
    /// 历史之前的数据按「检测为 0 + 已有编码 + 该编码没有任何 Create 记录」兜底。
    /// </summary>
    public sealed class LocalSupplierInvoiceCreatedHereProductTests : IDisposable
    {
        private const string InvoiceGuid = "invoice-created-here";
        private const string OtherInvoiceGuid = "invoice-other";

        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocalSupplierInvoiceCreatedHereProductTests()
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

        [Fact]
        public async Task GetGridDataAsync_本单新品按Create历史计数_历史之前按检测快照兜底()
        {
            await SeedAsync();

            var result = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20 });

            Assert.True(result.Success, result.Message);
            var invoice = Assert.Single(result.Items!, item => item.InvoiceGUID == InvoiceGuid);
            // A1（本单 Create 历史）+ A3（历史之前的快照兜底）；A2 是别的单建的、A4 是旧商品、A6 已删除。
            Assert.Equal(2, invoice.CreatedHereProductDetailCount);
            // 待建新品只剩没有编码的 A5，与本单新品互不重叠。
            Assert.Equal(1, invoice.NewProductDetailCount);
            Assert.Equal(1, Assert.Single(result.Items!, item => item.InvoiceGUID == OtherInvoiceGuid).CreatedHereProductDetailCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetDetails_逐行标记本单新品(bool paged)
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

            var flags = items.ToDictionary(item => item.DetailGUID, item => item.IsCreatedByThisInvoice);
            Assert.Equal(5, flags.Count);
            Assert.True(flags["A1-history-here"]);
            Assert.False(flags["A2-created-by-other"]);
            Assert.True(flags["A3-legacy-snapshot"]);
            Assert.False(flags["A4-existing-product"]);
            Assert.False(flags["A5-pending-new"]);
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
            foreach (var guid in new[] { InvoiceGuid, OtherInvoiceGuid })
            {
                await _db.Insertable(new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = guid,
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    InvoiceNo = guid,
                    OrderDate = new DateTime(2026, 10, 6),
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            static StoreLocalSupplierInvoiceDetails Detail(string guid, string invoice, string? code, int? existing, bool deleted = false) => new()
            {
                DetailGUID = guid,
                InvoiceGUID = invoice,
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = code,
                StoreProductCode = code == null ? null : "S01" + code,
                ExistingProductCount = existing,
                IsDeleted = deleted,
            };
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                // 本单建的商品，之后又被「商品检测」改成了已存在 1：仍应是本单新品。
                Detail("A1-history-here", InvoiceGuid, "P-HERE", 1),
                // 检测时不存在，但商品是另一张单建的：不是本单新品。
                Detail("A2-created-by-other", InvoiceGuid, "P-OTHER", 0),
                // 变更历史出现之前的旧数据：检测为 0 且已关联编码。
                Detail("A3-legacy-snapshot", InvoiceGuid, "P-LEGACY", 0),
                // 本来就存在的商品（只有 Update 历史）。
                Detail("A4-existing-product", InvoiceGuid, "P-OLD", 1),
                // 待建新品：检测为 0、尚未关联编码。
                Detail("A5-pending-new", InvoiceGuid, null, 0),
                // 已删除的明细不计入。
                Detail("A6-deleted", InvoiceGuid, "P-HERE-DELETED", 1, deleted: true),
                // 另一张单：P-OTHER 是它建的。
                Detail("B1-other-here", OtherInvoiceGuid, "P-OTHER", 0),
            }).ExecuteCommandAsync();

            static WarehouseProductChangeHistory History(string code, string action, string? source) => new()
            {
                EventGuid = Guid.NewGuid(),
                ProductCode = code,
                Action = action,
                Source = "LocalSupplierInvoiceHqProductSync",
                SourceReference = source,
                ActorName = "tester",
                ActorType = "User",
                OccurredAtUtc = DateTime.UtcNow,
                ChangesJson = "[]",
            };
            await _db.Insertable(new List<WarehouseProductChangeHistory>
            {
                History("P-HERE", "Create", InvoiceGuid),
                History("P-OTHER", "Create", OtherInvoiceGuid),
                History("P-HERE-DELETED", "Create", InvoiceGuid),
                History("P-OLD", "Update", InvoiceGuid),
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
