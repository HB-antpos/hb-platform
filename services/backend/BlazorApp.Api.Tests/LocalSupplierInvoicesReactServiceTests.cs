using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class LocalSupplierInvoicesReactServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocalSupplierInvoicesReactServiceTests()
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
                typeof(WarehouseProduct),
                typeof(StoreRetailPrice),
                typeof(StoreMultiCodeProduct),
                typeof(ProductSetCode),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails)
            );
            SqliteWarehouseProductChangeHistoryTable.Create(_db);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetDetails_商品类型读取当前主档且不按条码或订单操作推断(bool paged)
        {
            var cases = new (string Code, int? Type, bool Deleted)[]
            {
                ("P-NORMAL", 0, false),
                ("P-SET", 1, false),
                ("P-MULTI", 2, false),
                ("P-NULL", null, false),
                ("P-DELETED", 2, true),
            };
            foreach (var item in cases)
            {
                await _db.Insertable(new Product
                {
                    UUID = item.Code,
                    ProductCode = item.Code,
                    Barcode = "SHARED-BARCODE",
                    ProductType = item.Type,
                    IsDeleted = item.Deleted,
                }).ExecuteCommandAsync();
            }

            var codes = cases.Select(item => item.Code).Concat(new[] { "P-MISSING", null }).ToArray();
            for (var i = 0; i < codes.Length; i++)
            {
                await _db.Insertable(new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = $"type-detail-{i}",
                    InvoiceGUID = "invoice-product-type",
                    ProductCode = codes[i],
                    Barcode = "SHARED-BARCODE",
                    AdditionalBarcodesJson = "[\"OTHER-BARCODE\"]",
                    ActivityType = 2,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            async Task<List<LocalSupplierInvoiceItemDto>> ReadAsync()
            {
                if (paged)
                {
                    var response = await CreateService().GetDetailsGridAsync("invoice-product-type", new GridRequestDto { PageSize = 50 });
                    Assert.True(response.Success, response.Message);
                    return response.Items!;
                }
                var result = await CreateService().GetDetailsAsync("invoice-product-type");
                Assert.True(result.Success, result.Message);
                return result.Data!;
            }

            var details = (await ReadAsync()).ToDictionary(item => item.DetailGUID);
            Assert.Equal(7, details.Count);
            Assert.Equal(0, details["type-detail-0"].ProductType);
            Assert.Equal(1, details["type-detail-1"].ProductType);
            Assert.Equal(2, details["type-detail-2"].ProductType);
            foreach (var i in new[] { 3, 4, 5, 6 })
                Assert.Null(details[$"type-detail-{i}"].ProductType);

            // 再次读取应反映主档最新类型，不在进货单中保存类型快照。
            await _db.Updateable<Product>().SetColumns(item => item.ProductType == 2)
                .Where(item => item.ProductCode == "P-NORMAL").ExecuteCommandAsync();
            Assert.Equal(2, (await ReadAsync()).Single(item => item.DetailGUID == "type-detail-0").ProductType);
        }

        [Fact]
        public async Task UpdateAsync_ChangingStoreAndSupplier_CascadesTrimmedCodesToInvoiceAndDetails()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-update-header", "INV-UPDATE", new DateTime(2026, 7, 19));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-update-header",
                InvoiceGUID = "invoice-update-header",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-2",
                StoreCode = "S02",
                StoreName = "Brisbane Store",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new HBLocalSupplier
            {
                Guid = "supplier-guid-2",
                LocalSupplierCode = "SUP02",
                Name = "Second Supplier",
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateAsync(
                "invoice-update-header",
                new UpdateInvoiceRequest { StoreCode = " S02 ", SupplierCode = " SUP02 " }
            );

            Assert.True(result.Success, result.Message);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == "invoice-update-header");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-update-header");
            Assert.Equal("S02", invoice.StoreCode);
            Assert.Equal("SUP02", invoice.SupplierCode);
            Assert.Equal("S02", detail.StoreCode);
            Assert.Equal("SUP02", detail.SupplierCode);
        }

        [Theory]
        [InlineData("missing-store", "INVALID_STORE")]
        [InlineData("inactive-store", "INVALID_STORE")]
        [InlineData("missing-supplier", "INVALID_SUPPLIER")]
        [InlineData("inactive-supplier", "INVALID_SUPPLIER")]
        public async Task UpdateAsync_InvalidOrInactiveTarget_IsRejectedWithoutChangingData(
            string scenario,
            string expectedErrorCode
        )
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-invalid-header", "INV-INVALID", new DateTime(2026, 7, 19));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-invalid-header",
                InvoiceGUID = "invoice-invalid-header",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            if (scenario == "inactive-store")
            {
                await _db.Insertable(new Store
                {
                    StoreGUID = "store-guid-inactive",
                    StoreCode = "S-INACTIVE",
                    StoreName = "Inactive Store",
                    IsActive = false,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
            if (scenario == "inactive-supplier")
            {
                await _db.Insertable(new HBLocalSupplier
                {
                    Guid = "supplier-guid-inactive",
                    LocalSupplierCode = "SUP-INACTIVE",
                    Name = "Inactive Supplier",
                    Status = 0,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            var targetStoreCode = scenario.EndsWith("store", StringComparison.Ordinal)
                ? scenario == "inactive-store" ? "S-INACTIVE" : "S-MISSING"
                : "S01";
            var targetSupplierCode = scenario.EndsWith("supplier", StringComparison.Ordinal)
                ? scenario == "inactive-supplier" ? "SUP-INACTIVE" : "SUP-MISSING"
                : "SUP01";

            var result = await CreateService().UpdateAsync(
                "invoice-invalid-header",
                new UpdateInvoiceRequest
                {
                    StoreCode = targetStoreCode,
                    SupplierCode = targetSupplierCode,
                    Remarks = "不应保存",
                }
            );

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == "invoice-invalid-header");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-invalid-header");
            Assert.Equal("S01", invoice.StoreCode);
            Assert.Equal("SUP01", invoice.SupplierCode);
            Assert.Null(invoice.Remarks);
            Assert.Equal("S01", detail.StoreCode);
            Assert.Equal("SUP01", detail.SupplierCode);
        }

        [Fact]
        public async Task UpdateAsync_ChangingBusinessKeyToExistingInvoice_ReturnsDuplicateWithoutChangingData()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-existing", "INV-EXISTING", new DateTime(2026, 7, 18));
            await InsertInvoiceAsync("invoice-edit", "INV-EDIT", new DateTime(2026, 7, 19));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-edit",
                InvoiceGUID = "invoice-edit",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateAsync(
                "invoice-edit",
                new UpdateInvoiceRequest
                {
                    InvoiceNo = " INV-EXISTING ",
                    Remarks = "不应保存",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("DUPLICATE_INVOICE", result.ErrorCode);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == "invoice-edit");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-edit");
            Assert.Equal("INV-EDIT", invoice.InvoiceNo);
            Assert.Null(invoice.Remarks);
            Assert.Equal("S01", detail.StoreCode);
            Assert.Equal("SUP01", detail.SupplierCode);
        }

        [Fact]
        public async Task UpdateAsync_ExistingHistoricalDuplicate_AllowsNonBusinessFieldChange()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-duplicate-1", "INV-HISTORY", new DateTime(2023, 7, 28));
            await InsertInvoiceAsync("invoice-duplicate-2", "INV-HISTORY", new DateTime(2023, 8, 1));

            var result = await CreateService().UpdateAsync(
                "invoice-duplicate-2",
                new UpdateInvoiceRequest { Remarks = "保留历史重复单据" }
            );

            Assert.True(result.Success, result.Message);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == "invoice-duplicate-2");
            Assert.Equal("INV-HISTORY", invoice.InvoiceNo);
            Assert.Equal("保留历史重复单据", invoice.Remarks);
        }

        [Fact]
        public async Task UpdateAsync_BlankInvoiceNo_ReturnsValidationErrorWithoutChangingData()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-blank-number", "INV-ORIGINAL", new DateTime(2026, 7, 19));

            var result = await CreateService().UpdateAsync(
                "invoice-blank-number",
                new UpdateInvoiceRequest
                {
                    InvoiceNo = "   ",
                    Remarks = "不应保存",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == "invoice-blank-number");
            Assert.Equal("INV-ORIGINAL", invoice.InvoiceNo);
            Assert.Null(invoice.Remarks);
        }

        [Fact]
        public async Task CreateAsync_TrimsBusinessKeyBeforeDuplicateCheckAndInsert()
        {
            await SeedStoreAndSupplierAsync();

            var result = await CreateService().CreateAsync(
                new CreateInvoiceRequest
                {
                    StoreCode = " S01 ",
                    SupplierCode = " SUP01 ",
                    InvoiceNo = " INV-TRIMMED ",
                    Items = new List<PastedDetailItem>(),
                }
            );

            Assert.True(result.Success, result.Message);
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .FirstAsync(x => x.InvoiceGUID == result.Data);
            Assert.Equal("S01", invoice.StoreCode);
            Assert.Equal("SUP01", invoice.SupplierCode);
            Assert.Equal("INV-TRIMMED", invoice.InvoiceNo);
        }

        [Fact]
        public async Task GetGridDataAsync_DefaultsToOrderDateDescendingAndClampsPageSizeTo20()
        {
            await SeedStoreAndSupplierAsync();
            for (var index = 1; index <= 25; index++)
            {
                await _db.Insertable(new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = $"invoice-{index:00}",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    InvoiceNo = $"INV-{index:00}",
                    OrderDate = new DateTime(2026, 1, index),
                    CreatedAt = new DateTime(2026, 2, 26 - index),
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            var result = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0,
                PageSize = 999,
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(25, result.Total);
            Assert.Equal(20, result.Items?.Count);
            Assert.Equal("INV-25", result.Items?.First().InvoiceNo);
        }

        [Fact]
        public async Task GetGridDataAsync_OrderDateInRangeIncludesTheWholeEndDate()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("before", "INV-1", new DateTime(2026, 1, 1, 23, 0, 0));
            await InsertInvoiceAsync("inside-start", "INV-2", new DateTime(2026, 1, 2, 0, 0, 0));
            await InsertInvoiceAsync("inside-end", "INV-3", new DateTime(2026, 1, 5, 23, 59, 0));
            await InsertInvoiceAsync("after", "INV-4", new DateTime(2026, 1, 6, 0, 0, 0));

            var result = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["orderDate"] = new()
                    {
                        FilterType = "date",
                        Type = "inRange",
                        Filter = "2026-01-02",
                        FilterTo = "2026-01-05",
                    },
                },
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Total);
            Assert.Equal(new[] { "INV-3", "INV-2" }, result.Items?.Select(item => item.InvoiceNo).ToArray());
        }

        [Fact]
        public async Task GetGridDataAsync_创建时间按带时区的时刻区间筛选且结束为开区间()
        {
            await SeedStoreAndSupplierAsync();
            // CreatedAt 存 UTC；悉尼（+10）的 07-09 至 07-10 两天 = UTC [07-08 14:00, 07-10 14:00)。
            var createdAtByInvoice = new Dictionary<string, DateTime>
            {
                ["INV-BEFORE"] = new DateTime(2026, 7, 8, 13, 59, 59, DateTimeKind.Utc),
                ["INV-START"] = new DateTime(2026, 7, 8, 14, 0, 0, DateTimeKind.Utc),
                ["INV-END"] = new DateTime(2026, 7, 10, 13, 59, 59, DateTimeKind.Utc),
                ["INV-AFTER"] = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc),
            };
            foreach (var (invoiceNo, createdAt) in createdAtByInvoice)
            {
                await _db.Insertable(new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = $"guid-{invoiceNo}",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    InvoiceNo = invoiceNo,
                    OrderDate = new DateTime(2026, 7, 1),
                    CreatedAt = createdAt,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            var result = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["createdAt"] = new()
                    {
                        FilterType = "date",
                        Type = "inRange",
                        Filter = "2026-07-09T00:00:00+10:00",
                        FilterTo = "2026-07-11T00:00:00+10:00",
                    },
                },
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(
                new[] { "INV-END", "INV-START" },
                result.Items?.Select(item => item.InvoiceNo).OrderBy(no => no, StringComparer.Ordinal).ToArray()
            );
        }

        [Fact]
        public async Task GetStoreCountsAsync_按分店分组并沿用其他筛选与分店范围()
        {
            await SeedStoreAndSupplierAsync();
            var invoices = new[]
            {
                ("S01", "SUP01", false),
                ("S01", "SUP01", false),
                ("S01", "SUP01", true),
                ("S02", "SUP01", false),
                ("S02", "SUP02", false),
                ("S03", "SUP01", false),
            };
            var index = 0;
            foreach (var (storeCode, supplierCode, isDeleted) in invoices)
            {
                index++;
                await _db.Insertable(new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = $"count-{index}",
                    StoreCode = storeCode,
                    SupplierCode = supplierCode,
                    InvoiceNo = $"INV-COUNT-{index}",
                    OrderDate = new DateTime(2026, 3, index),
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = isDeleted,
                }).ExecuteCommandAsync();
            }
            var request = new GridRequestDto
            {
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["supplierCode"] = new() { FilterType = "text", Type = "equals", Filter = "SUP01" },
                },
            };

            var all = await CreateService().GetStoreCountsAsync(request, null);
            var scoped = await CreateService().GetStoreCountsAsync(request, new List<string> { "S01", "S02" });

            Assert.True(all.Success, all.Message);
            Assert.Equal(
                new[] { ("S01", 2), ("S02", 1), ("S03", 1) },
                all.Data!.OrderBy(row => row.StoreCode).Select(row => (row.StoreCode, row.Count)).ToArray()
            );
            Assert.True(scoped.Success, scoped.Message);
            Assert.Equal(
                new[] { ("S01", 2), ("S02", 1) },
                scoped.Data!.OrderBy(row => row.StoreCode).Select(row => (row.StoreCode, row.Count)).ToArray()
            );
        }

        [Fact]
        public async Task GetGridDataAsync_统计当前页涨跌价商品数量()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-price-up", "INV-UP", new DateTime(2026, 1, 7));
            await InsertInvoiceAsync("invoice-price-flat", "INV-FLAT", new DateTime(2026, 1, 6));

            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-up-1",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 2.00m,
                    PurchasePrice = 2.50m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-up-2",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 1.00m,
                    PurchasePrice = 1.20m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-flat",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 3.00m,
                    PurchasePrice = 3.00m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-down",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 5.00m,
                    PurchasePrice = 4.80m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-zero-last-price",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 0m,
                    PurchasePrice = 1.00m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-null-last-price",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = null,
                    PurchasePrice = 1.00m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-null-purchase-price",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 1.00m,
                    PurchasePrice = null,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-deleted",
                    InvoiceGUID = "invoice-price-up",
                    LastPurchasePrice = 1.00m,
                    PurchasePrice = 9.00m,
                    IsDeleted = true,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-other-invoice-down",
                    InvoiceGUID = "invoice-price-flat",
                    LastPurchasePrice = 4.00m,
                    PurchasePrice = 3.50m,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
            });

            Assert.True(result.Success, result.Message);
            var priceUpInvoice = Assert.Single(result.Items!, item => item.InvoiceGUID == "invoice-price-up");
            var flatInvoice = Assert.Single(result.Items!, item => item.InvoiceGUID == "invoice-price-flat");
            Assert.Equal(2, priceUpInvoice.PriceIncreaseItemCount);
            Assert.Equal(1, priceUpInvoice.PriceDecreaseItemCount);
            Assert.Equal(0, flatInvoice.PriceIncreaseItemCount);
            Assert.Equal(1, flatInvoice.PriceDecreaseItemCount);
        }

        [Fact]
        public async Task GetGridDataAsync_商品检测状态按有效明细聚合并隔离订单()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-empty", "INV-EMPTY", new DateTime(2026, 1, 1));
            await InsertInvoiceAsync("invoice-unchecked", "INV-UNCHECKED", new DateTime(2026, 1, 2));
            await InsertInvoiceAsync("invoice-partial", "INV-PARTIAL", new DateTime(2026, 1, 3));
            await InsertInvoiceAsync("invoice-complete", "INV-COMPLETE", new DateTime(2026, 1, 4));
            await InsertInvoiceAsync("invoice-zero", "INV-ZERO", new DateTime(2026, 1, 5));
            await InsertInvoiceAsync("invoice-deleted-only", "INV-DELETED", new DateTime(2026, 1, 6));
            await InsertInvoiceAsync("invoice-mixed-deleted", "INV-MIXED", new DateTime(2026, 1, 7));
            await InsertInvoiceAsync("invoice-isolated", "INV-ISOLATED", new DateTime(2026, 1, 8));

            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-unchecked", InvoiceGUID = "invoice-unchecked", ExistingProductCount = null, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-partial-checked", InvoiceGUID = "invoice-partial", ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-partial-unchecked", InvoiceGUID = "invoice-partial", ExistingProductCount = null, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-complete-1", InvoiceGUID = "invoice-complete", ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-complete-2", InvoiceGUID = "invoice-complete", ExistingProductCount = 2, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-zero", InvoiceGUID = "invoice-zero", ExistingProductCount = 0, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-deleted-only", InvoiceGUID = "invoice-deleted-only", ExistingProductCount = 1, IsDeleted = true },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-mixed-deleted-valid", InvoiceGUID = "invoice-mixed-deleted", ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-mixed-deleted-null", InvoiceGUID = "invoice-mixed-deleted", ExistingProductCount = null, IsDeleted = true },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-isolated", InvoiceGUID = "invoice-isolated", ExistingProductCount = null, IsDeleted = false },
            }).ExecuteCommandAsync();

            var result = await CreateService().GetGridDataAsync(new GridRequestDto { StartRow = 0, PageSize = 20 });

            Assert.True(result.Success, result.Message);
            var checkedByInvoice = result.Items!.ToDictionary(item => item.InvoiceGUID, item => item.IsProductChecked);
            Assert.False(checkedByInvoice["invoice-empty"]);
            Assert.False(checkedByInvoice["invoice-unchecked"]);
            Assert.False(checkedByInvoice["invoice-partial"]);
            Assert.True(checkedByInvoice["invoice-complete"]);
            Assert.True(checkedByInvoice["invoice-zero"]);
            Assert.False(checkedByInvoice["invoice-deleted-only"]);
            Assert.True(checkedByInvoice["invoice-mixed-deleted"]);
            Assert.False(checkedByInvoice["invoice-isolated"]);

            // 三个明细计数与检测状态同源：只数有效明细，NULL 计未检测，0 计主档不存在。
            var countsByInvoice = result.Items!.ToDictionary(
                item => item.InvoiceGUID,
                item => (item.DetailCount, item.UncheckedDetailCount, item.NewProductDetailCount)
            );
            Assert.Equal((0, 0, 0), countsByInvoice["invoice-empty"]);
            Assert.Equal((1, 1, 0), countsByInvoice["invoice-unchecked"]);
            Assert.Equal((2, 1, 0), countsByInvoice["invoice-partial"]);
            Assert.Equal((2, 0, 0), countsByInvoice["invoice-complete"]);
            Assert.Equal((1, 0, 1), countsByInvoice["invoice-zero"]);
            Assert.Equal((0, 0, 0), countsByInvoice["invoice-deleted-only"]);
            Assert.Equal((1, 0, 0), countsByInvoice["invoice-mixed-deleted"]);
            Assert.Equal((1, 1, 0), countsByInvoice["invoice-isolated"]);

            // 筛选口径必须与列表展示一致：按 true/false 筛出的订单正好是上面展示为是/否的两组。
            async Task<string[]> FilterByCheckedAsync(string value)
            {
                var filtered = await CreateService().GetGridDataAsync(new GridRequestDto
                {
                    StartRow = 0,
                    PageSize = 20,
                    FilterModel = new Dictionary<string, FilterModelDto>
                    {
                        ["isProductChecked"] = new() { FilterType = "text", Type = "equals", Filter = value },
                    },
                });
                Assert.True(filtered.Success, filtered.Message);
                Assert.Equal(filtered.Items!.Count, filtered.Total);
                Assert.All(filtered.Items!, item => Assert.Equal(value == "true", item.IsProductChecked));
                return filtered.Items!.Select(item => item.InvoiceGUID).OrderBy(guid => guid).ToArray();
            }

            Assert.Equal(
                new[] { "invoice-complete", "invoice-mixed-deleted", "invoice-zero" },
                await FilterByCheckedAsync("true")
            );
            Assert.Equal(
                new[] { "invoice-deleted-only", "invoice-empty", "invoice-isolated", "invoice-partial", "invoice-unchecked" },
                await FilterByCheckedAsync("false")
            );

            // 无法解析的值不缩小结果。
            var ignored = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["isProductChecked"] = new() { FilterType = "text", Filter = "maybe" },
                },
            });
            Assert.Equal(result.Total, ignored.Total);
        }

        [Fact]
        public async Task GetGridDataAsync_明细计数区分未检测与主档不存在且忽略已删除明细()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-count-mixed", "INV-COUNT-MIXED", new DateTime(2026, 2, 1));
            await InsertInvoiceAsync("invoice-count-new-only", "INV-COUNT-NEW", new DateTime(2026, 2, 2));
            await InsertInvoiceAsync("invoice-count-none", "INV-COUNT-NONE", new DateTime(2026, 2, 3));

            await _db.Insertable(new[]
            {
                // 混合单：2 行 NULL（未检测）、2 行 0 且无编码（新品）、1 行 0 但已回填编码（已建品，不算新品）、1 行 1、1 行 3；
                // 另有已删除的 NULL/0 不应计入。
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-null-1", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = null, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-null-2", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = null, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-zero-1", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 0, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-zero-2", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 0, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-zero-synced", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 0, ProductCode = "P-SYNCED", IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-one", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 1, ProductCode = "P-ONE", IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-three", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 3, ProductCode = "P-THREE", IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-deleted-null", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = null, IsDeleted = true },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-mixed-deleted-zero", InvoiceGUID = "invoice-count-mixed", ExistingProductCount = 0, IsDeleted = true },
                // 全部已检测但主档都不存在：算已检测，且全部计入主档不存在。
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-new-1", InvoiceGUID = "invoice-count-new-only", ExistingProductCount = 0, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "count-new-2", InvoiceGUID = "invoice-count-new-only", ExistingProductCount = 0, IsDeleted = false },
            }).ExecuteCommandAsync();

            var result = await CreateService().GetGridDataAsync(new GridRequestDto { StartRow = 0, PageSize = 20 });

            Assert.True(result.Success, result.Message);
            var mixed = Assert.Single(result.Items!, item => item.InvoiceGUID == "invoice-count-mixed");
            Assert.Equal(7, mixed.DetailCount);
            Assert.Equal(2, mixed.UncheckedDetailCount);
            // 关键断言：检测为不存在但已回填编码的行不再计入新品。
            Assert.Equal(2, mixed.NewProductDetailCount);
            Assert.False(mixed.IsProductChecked);

            var newOnly = Assert.Single(result.Items!, item => item.InvoiceGUID == "invoice-count-new-only");
            Assert.Equal(2, newOnly.DetailCount);
            Assert.Equal(0, newOnly.UncheckedDetailCount);
            Assert.Equal(2, newOnly.NewProductDetailCount);
            Assert.True(newOnly.IsProductChecked);

            var none = Assert.Single(result.Items!, item => item.InvoiceGUID == "invoice-count-none");
            Assert.Equal(0, none.DetailCount);
            Assert.Equal(0, none.UncheckedDetailCount);
            Assert.Equal(0, none.NewProductDetailCount);
            Assert.False(none.IsProductChecked);
        }

        [Fact]
        public async Task GetGridDataAsync_商品关键词不缩小检测聚合且遵守分页门店范围()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-keyword", "INV-KEYWORD", new DateTime(2026, 1, 10));
            await InsertInvoiceAsync("invoice-page-first", "INV-PAGE-FIRST", new DateTime(2026, 1, 11));
            await InsertInvoiceAsync("invoice-page-second", "INV-PAGE-SECOND", new DateTime(2026, 1, 12));
            for (var index = 1; index <= 21; index++)
            {
                var invoiceGuid = $"invoice-page-extra-{index:00}";
                await InsertInvoiceAsync(invoiceGuid, $"INV-PAGE-EXTRA-{index:00}", new DateTime(2026, 1, 20).AddDays(index));
                await _db.Insertable(new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = $"detail-page-extra-{index:00}",
                    InvoiceGUID = invoiceGuid,
                    ExistingProductCount = 1,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
            await _db.Insertable(new Store { StoreGUID = "store-guid-2", StoreCode = "S02", StoreName = "Melbourne Store", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "invoice-other-store", StoreCode = "S02", SupplierCode = "SUP01", InvoiceNo = "INV-OTHER-STORE",
                OrderDate = new DateTime(2026, 1, 13), CreatedAt = DateTime.UtcNow, IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-keyword-checked", InvoiceGUID = "invoice-keyword", ProductName = "MATCH-KEY", LastPurchasePrice = 1m, PurchasePrice = 2m, ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-keyword-unchecked", InvoiceGUID = "invoice-keyword", ProductName = "OTHER-NAME", LastPurchasePrice = 3m, PurchasePrice = 3m, ExistingProductCount = null, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-page-first", InvoiceGUID = "invoice-page-first", ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-page-second", InvoiceGUID = "invoice-page-second", ExistingProductCount = 1, IsDeleted = false },
                new StoreLocalSupplierInvoiceDetails { DetailGUID = "detail-other-store", InvoiceGUID = "invoice-other-store", ExistingProductCount = 1, IsDeleted = false },
            }).ExecuteCommandAsync();

            var keywordResult = await CreateService().GetGridDataAsync(new GridRequestDto
            {
                StartRow = 0, PageSize = 20,
                FilterModel = new Dictionary<string, FilterModelDto> { ["productKeyword"] = new() { FilterType = "text", Filter = "MATCH-KEY" } },
            });
            var keywordInvoice = Assert.Single(keywordResult.Items!, item => item.InvoiceGUID == "invoice-keyword");
            Assert.False(keywordInvoice.IsProductChecked);
            // 商品关键词只筛选订单，不缩小明细计数：未命中关键词的未检测行仍计入。
            Assert.Equal(2, keywordInvoice.DetailCount);
            Assert.Equal(1, keywordInvoice.UncheckedDetailCount);
            Assert.Equal(0, keywordInvoice.NewProductDetailCount);

            // PageSize=20 是服务允许的最小页长；跳过前一整页，验证当前页各订单的聚合结果。
            var pagedResult = await CreateService().GetGridDataAsync(new GridRequestDto { StartRow = 20, PageSize = 20 }, new List<string> { "S01" });
            Assert.True(pagedResult.Success, pagedResult.Message);
            Assert.Equal(4, pagedResult.Items!.Count);
            Assert.DoesNotContain(pagedResult.Items!, item => item.InvoiceGUID == "invoice-other-store");
            Assert.Equal(24, pagedResult.Total);
            Assert.All(pagedResult.Items!, item =>
                Assert.Equal(item.InvoiceGUID != "invoice-keyword", item.IsProductChecked)
            );
        }

        [Fact]
        public async Task GetGridDataAsync_新增明细未检测且完成检测后更新为已检测()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-recheck", "INV-RECHECK", new DateTime(2026, 1, 14));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-recheck-existing",
                InvoiceGUID = "invoice-recheck",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "EXISTING",
                Barcode = "BAR-EXISTING",
                ExistingProductCount = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var initial = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20 });
            Assert.True(Assert.Single(initial.Items!, item => item.InvoiceGUID == "invoice-recheck").IsProductChecked);

            // 实际批量 Upsert 同时编辑既有货号/条码并新增未检测明细；既有检测结果应保留。
            var upsertResult = await CreateService().BatchUpsertDetailsAsync(
                "invoice-recheck",
                new List<InvoiceDetailUpsertItemDto>
                {
                    new() { DetailGUID = "detail-recheck-existing", ItemNumber = "EXISTING-EDITED", Barcode = "BAR-EXISTING-EDITED" },
                    new() { DetailGUID = null, ItemNumber = "MISSING", Barcode = "BAR-MISSING" },
                },
                "tester"
            );
            Assert.True(upsertResult.Success, upsertResult.Message);

            var afterUpsert = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20 });
            Assert.False(Assert.Single(afterUpsert.Items!, item => item.InvoiceGUID == "invoice-recheck").IsProductChecked);
            var editedDetail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(item => item.DetailGUID == "detail-recheck-existing");
            Assert.Equal("EXISTING-EDITED", editedDetail.ItemNumber);
            Assert.Equal("BAR-EXISTING-EDITED", editedDetail.Barcode);
            Assert.Equal(1, editedDetail.ExistingProductCount);

            var newDetailGuid = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(item => item.InvoiceGUID == "invoice-recheck" && item.ItemNumber == "MISSING")
                .Select(item => item.DetailGUID)
                .FirstAsync();
            var checkResult = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-recheck", DetailGuids = new List<string> { newDetailGuid },
            });
            Assert.True(checkResult.Success, checkResult.Message);
            var after = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20 });
            Assert.True(Assert.Single(after.Items!, item => item.InvoiceGUID == "invoice-recheck").IsProductChecked);
        }

        [Fact]
        public async Task GetDetailsGridAsync_ReturnsPagedDetailsAndClampsPageSizeTo50()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-1", "INV-1", new DateTime(2026, 1, 7));
            for (var index = 1; index <= 55; index++)
            {
                await _db.Insertable(new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = $"detail-{index:00}",
                    InvoiceGUID = "invoice-1",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ProductCode = $"P{index:00}",
                    ItemNumber = $"ITEM-{index:00}",
                    Barcode = $"BAR-{index:00}",
                    ProductName = $"Product {index:00}",
                    PurchasePrice = index,
                    Quantity = index,
                    CreatedAt = new DateTime(2026, 1, 1).AddMinutes(index),
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            var result = await CreateService().GetDetailsGridAsync("invoice-1", new GridRequestDto
            {
                StartRow = 0,
                PageSize = 999,
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(55, result.Total);
            Assert.Equal(50, result.Items?.Count);
            Assert.Equal("detail-55", result.Items?.First().DetailGUID);
        }

        [Fact]
        public async Task GetDetailsGridAsync_按有效上次进货价筛选涨价和减价明细()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-price-change", "INV-PRICE", new DateTime(2026, 1, 8));

            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-up",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = 2.00m,
                    PurchasePrice = 2.50m,
                    CreatedAt = new DateTime(2026, 1, 1, 10, 0, 0),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-down",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = 3.00m,
                    PurchasePrice = 2.80m,
                    CreatedAt = new DateTime(2026, 1, 1, 11, 0, 0),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-flat",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = 4.00m,
                    PurchasePrice = 4.00m,
                    CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-zero-last-price",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = 0m,
                    PurchasePrice = 9.00m,
                    CreatedAt = new DateTime(2026, 1, 1, 13, 0, 0),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-null-last-price",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = null,
                    PurchasePrice = 1.00m,
                    CreatedAt = new DateTime(2026, 1, 1, 14, 0, 0),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-null-purchase-price",
                    InvoiceGUID = "invoice-price-change",
                    LastPurchasePrice = 1.00m,
                    PurchasePrice = null,
                    CreatedAt = new DateTime(2026, 1, 1, 15, 0, 0),
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var upResult = await CreateService().GetDetailsGridAsync("invoice-price-change", new GridRequestDto
            {
                StartRow = 0,
                PageSize = 50,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["priceChange"] = new()
                    {
                        FilterType = "text",
                        Type = "equals",
                        Filter = "up",
                    },
                },
            });
            var downResult = await CreateService().GetDetailsGridAsync("invoice-price-change", new GridRequestDto
            {
                StartRow = 0,
                PageSize = 50,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["priceChange"] = new()
                    {
                        FilterType = "text",
                        Type = "equals",
                        Filter = "down",
                    },
                },
            });

            Assert.True(upResult.Success, upResult.Message);
            Assert.Equal(1, upResult.Total);
            Assert.Equal("detail-up", Assert.Single(upResult.Items!).DetailGUID);
            Assert.True(downResult.Success, downResult.Message);
            Assert.Equal(1, downResult.Total);
            Assert.Equal("detail-down", Assert.Single(downResult.Items!).DetailGUID);
        }

        [Fact]
        public async Task CheckProductsAsync_WhenProductNoLongerMatches_ClearsProductFieldsAndRecalculatesAutoPricingPreview()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check", "INV-CHECK", new DateTime(2026, 1, 8));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-stale",
                InvoiceGUID = "invoice-check",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "MISSING",
                Barcode = "NEW-BARCODE",
                ProductName = "Missing Product",
                Quantity = 1,
                PurchasePrice = 2.00m,
                ProductCode = "OLD-PRODUCT",
                StoreProductCode = "OLD-STORE-PRODUCT",
                LastPurchasePrice = 9.99m,
                AutoPricing = true,
                IsSpecialProduct = true,
                DiscountRate = 0.20m,
                PricingFloatRate = 300m,
                NewAutoRetailPrice = 8.88m,
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                BarcodeMatchCount = 4,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check",
                DetailGuids = new List<string> { "detail-stale" },
            });

            Assert.True(result.Success);

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-stale");
            Assert.Null(detail.ProductCode);
            Assert.Null(detail.StoreProductCode);
            Assert.Equal(9.99m, detail.LastPurchasePrice);
            Assert.True(detail.AutoPricing);
            Assert.Null(detail.IsSpecialProduct);
            Assert.Null(detail.DiscountRate);
            Assert.Equal(250m, detail.PricingFloatRate);
            Assert.Equal(5.00m, detail.NewAutoRetailPrice);
            Assert.Equal(0, detail.ExistingProductCount);
            Assert.Equal(1, detail.BarcodeStatus);
            Assert.Equal(0, detail.BarcodeMatchCount);
            Assert.Equal((int)DetailAction.CreateProduct, detail.ActivityType);
        }

        [Theory]
        [InlineData("BAR-LINK", true)]
        [InlineData("BAR-OTHER", false)]
        public async Task CheckProductsAsync_货号不匹配但已关联商品_仅在商品拥有本行条码时沿用关联(
            string detailBarcode,
            bool expectLinked)
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-linked", "INV-CHECK-LINKED", new DateTime(2026, 1, 8));
            await _db.Insertable(new Product
            {
                UUID = "product-linked",
                ProductCode = "P-LINK",
                ItemNumber = "MASTER-ITEM",
                Barcode = "BAR-LINK",
                ProductName = "Linked Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 4.00m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "store-price-linked",
                StoreCode = "S01",
                ProductCode = "P-LINK",
                StoreProductCode = "S01-P-LINK",
                SupplierCode = "SUP01",
                PurchasePrice = 3.50m,
                StoreRetailPriceValue = 6.50m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-linked",
                InvoiceGUID = "invoice-check-linked",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                // 发票货号与主档货号不同：只能靠手动关联的商品编码识别为已有商品。
                ItemNumber = "INVOICE-ITEM",
                Barcode = detailBarcode,
                ProductName = "Linked Product",
                Quantity = 1,
                PurchasePrice = 3.00m,
                ProductCode = "P-LINK",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-linked",
                DetailGuids = new List<string> { "detail-linked" },
            });

            Assert.True(result.Success, result.Message);
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-linked");
            if (expectLinked)
            {
                Assert.Equal("P-LINK", detail.ProductCode);
                Assert.Equal("S01-P-LINK", detail.StoreProductCode);
                Assert.Equal(1, detail.ExistingProductCount);
                Assert.Equal(1, detail.BarcodeStatus);
                Assert.Equal(3.50m, detail.LastPurchasePrice);
                Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
                Assert.Equal(1, result.Data!.Summary.ProductExists);
            }
            else
            {
                Assert.Null(detail.ProductCode);
                Assert.Null(detail.StoreProductCode);
                Assert.Equal(0, detail.ExistingProductCount);
                Assert.Equal(1, result.Data!.Summary.ProductNotExists);
            }
        }

        [Fact]
        public async Task CheckProductsAsync_排除已执行明细时_已执行行保持不变只检测其余行()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-executed", "INV-CHECK-EXECUTED", new DateTime(2026, 1, 8));
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                new()
                {
                    DetailGUID = "detail-executed",
                    InvoiceGUID = "invoice-check-executed",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "EXEC-1",
                    ProductName = "Executed Product",
                    Quantity = 1,
                    PurchasePrice = 2.00m,
                    ProductCode = "P-EXECUTED",
                    ExistingProductCount = 0,
                    ActivityType = 99,
                    IsDeleted = false,
                },
                new()
                {
                    DetailGUID = "detail-pending",
                    InvoiceGUID = "invoice-check-executed",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "PENDING-1",
                    ProductName = "Pending Product",
                    Quantity = 1,
                    PurchasePrice = 2.00m,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-executed",
                ExcludeExecutedDetails = true,
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.Data!.Summary.Total);
            Assert.Equal("detail-pending", Assert.Single(result.Data.Results).DetailGuid);

            var executed = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-executed");
            Assert.Equal(99, executed.ActivityType);
            Assert.Equal("P-EXECUTED", executed.ProductCode);
            var pending = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-pending");
            Assert.Equal(0, pending.ExistingProductCount);
        }

        [Fact]
        public async Task BatchCheckProductsJobEndpoints_分店范围用户含其他分店的单时拒绝()
        {
            await SeedStoreAndSupplierAsync();
            await SeedSecondActiveStoreAndScopedUserAsync();
            await InsertInvoiceAsync("invoice-batch-s01", "INV-BATCH-S01", new DateTime(2026, 1, 20));
            await InsertInvoiceAsync("invoice-batch-s02", "INV-BATCH-S02", new DateTime(2026, 1, 20));
            await _db.Updateable<StoreLocalSupplierInvoice>()
                .SetColumns(x => x.StoreCode == "S02")
                .Where(x => x.InvoiceGUID == "invoice-batch-s02")
                .ExecuteCommandAsync();
            var jobService = new Mock<ILocalSupplierInvoiceBatchUpdateJobService>(MockBehavior.Strict);
            var controller = CreateControllerWithJobService(
                jobService.Object,
                new Claim(ClaimTypes.Name, "scoped-user"),
                new Claim("userId", "user-scoped")
            );

            var otherStore = await controller.StartBatchCheckProductsJob(
                new StartBatchCheckProductsRequest { InvoiceGuids = ["invoice-batch-s01", "invoice-batch-s02"] },
                CancellationToken.None
            );
            var missing = await controller.StartBatchCheckProductsJob(
                new StartBatchCheckProductsRequest { InvoiceGuids = ["invoice-batch-s01", "invoice-not-exists"] },
                CancellationToken.None
            );

            Assert.IsType<ForbidResult>(otherStore);
            Assert.IsType<ForbidResult>(missing);
            jobService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BatchCheckProductsJobEndpoints_校验数量并把分店编码交给后台服务()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-batch-1", "INV-BATCH-1", new DateTime(2026, 1, 20));
            var jobService = new Mock<ILocalSupplierInvoiceBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartBatchCheckProductsJobAsync(
                    It.Is<IReadOnlyList<string>>(guids => guids.Count == 1 && guids[0] == "invoice-batch-1"),
                    It.Is<IReadOnlyCollection<string>>(codes => codes.Count == 1 && codes.Contains("S01")),
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new LocalSupplierInvoiceBatchCheckProductsJobDto { JobId = "batch-1" });
            var controller = CreateControllerWithJobService(jobService.Object);

            var empty = await controller.StartBatchCheckProductsJob(
                new StartBatchCheckProductsRequest { InvoiceGuids = [" "] },
                CancellationToken.None
            );
            var tooMany = await controller.StartBatchCheckProductsJob(
                new StartBatchCheckProductsRequest
                {
                    InvoiceGuids = Enumerable.Range(0, LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices + 1)
                        .Select(index => $"invoice-{index}")
                        .ToList(),
                },
                CancellationToken.None
            );
            var ok = await controller.StartBatchCheckProductsJob(
                new StartBatchCheckProductsRequest { InvoiceGuids = ["invoice-batch-1", "invoice-batch-1"] },
                CancellationToken.None
            );

            Assert.IsType<BadRequestObjectResult>(empty);
            Assert.IsType<BadRequestObjectResult>(tooMany);
            Assert.IsType<OkObjectResult>(ok);
            jobService.VerifyAll();
        }

        [Fact]
        public async Task CheckProductsAsync_LastPurchasePrice为空时_补充分店上次进货价()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-last-price-empty", "INV-CHECK-LAST-EMPTY", new DateTime(2026, 1, 8));
            await _db.Insertable(new Product
            {
                UUID = "product-last-empty",
                ProductCode = "P-LAST-EMPTY",
                ItemNumber = "LAST-EMPTY",
                Barcode = "BAR-LAST-EMPTY",
                ProductName = "Last Price Empty Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 4.44m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "store-price-last-empty",
                StoreCode = "S01",
                ProductCode = "P-LAST-EMPTY",
                StoreProductCode = "S01-LAST-EMPTY",
                SupplierCode = "SUP01",
                PurchasePrice = 3.33m,
                StoreRetailPriceValue = 6.66m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-last-empty",
                InvoiceGUID = "invoice-check-last-price-empty",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "LAST-EMPTY",
                Barcode = "BAR-LAST-EMPTY",
                ProductName = "Last Price Empty Product",
                PurchasePrice = 5.55m,
                LastPurchasePrice = null,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-last-price-empty",
                DetailGuids = new List<string> { "detail-last-empty" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-last-empty");

            Assert.True(result.Success, result.Message);
            Assert.Equal(3.33m, detail.LastPurchasePrice);
        }

        [Fact]
        public async Task CheckProductsAsync_LastPurchasePrice已有值时_不覆盖()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-last-price-existing", "INV-CHECK-LAST-EXISTING", new DateTime(2026, 1, 8));
            await _db.Insertable(new Product
            {
                UUID = "product-last-existing",
                ProductCode = "P-LAST-EXISTING",
                ItemNumber = "LAST-EXISTING",
                Barcode = "BAR-LAST-EXISTING",
                ProductName = "Last Price Existing Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 4.44m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "store-price-last-existing",
                StoreCode = "S01",
                ProductCode = "P-LAST-EXISTING",
                StoreProductCode = "S01-LAST-EXISTING",
                SupplierCode = "SUP01",
                PurchasePrice = 3.33m,
                StoreRetailPriceValue = 6.66m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-last-existing",
                InvoiceGUID = "invoice-check-last-price-existing",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "LAST-EXISTING",
                Barcode = "BAR-LAST-EXISTING",
                ProductName = "Last Price Existing Product",
                PurchasePrice = 5.55m,
                LastPurchasePrice = 9.99m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-last-price-existing",
                DetailGuids = new List<string> { "detail-last-existing" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-last-existing");

            Assert.True(result.Success, result.Message);
            Assert.Equal(9.99m, detail.LastPurchasePrice);
        }

        [Fact]
        public async Task CheckProductsAsync_分店价缺失时_用商品主档价补空上次进货价()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-last-price-product", "INV-CHECK-LAST-PRODUCT", new DateTime(2026, 1, 8));
            await _db.Insertable(new Product
            {
                UUID = "product-last-product",
                ProductCode = "P-LAST-PRODUCT",
                ItemNumber = "LAST-PRODUCT",
                Barcode = "BAR-LAST-PRODUCT",
                ProductName = "Last Price Product Fallback",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 4.44m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-last-product",
                InvoiceGUID = "invoice-check-last-price-product",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "LAST-PRODUCT",
                Barcode = "BAR-LAST-PRODUCT",
                ProductName = "Last Price Product Fallback",
                PurchasePrice = 5.55m,
                LastPurchasePrice = null,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-last-price-product",
                DetailGuids = new List<string> { "detail-last-product" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-last-product");

            Assert.True(result.Success, result.Message);
            Assert.Equal(4.44m, detail.LastPurchasePrice);
        }

        [Fact]
        public async Task CheckProductsAsync_商品不存在且有进货价_应计算定价浮率和新自动零售价()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-missing-auto", "INV-CHECK-MISSING-AUTO", new DateTime(2026, 1, 16));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-check-missing-auto",
                InvoiceGUID = "invoice-check-missing-auto",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "NEW-AUTO-001",
                Barcode = "9300000000012",
                ProductName = "Missing Auto Product",
                Quantity = 1,
                PurchasePrice = 4.00m,
                Amount = 4.00m,
                AutoPricing = true,
                ExistingProductCount = 0,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-missing-auto",
                DetailGuids = new List<string> { "detail-check-missing-auto" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-check-missing-auto");

            Assert.True(result.Success, result.Message);
            Assert.Equal(0, detail.ExistingProductCount);
            Assert.Equal(250m, detail.PricingFloatRate);
            Assert.Equal(10.00m, detail.NewAutoRetailPrice);
        }

        [Fact]
        public async Task CheckProductsAsync_新商品明细自动定价为空_检测后默认自动()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-new-auto-default", "INV-CHECK-NEW-AUTO-DEFAULT", new DateTime(2026, 1, 18));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-new-auto-default",
                InvoiceGUID = "invoice-check-new-auto-default",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "NEW-AUTO-DEFAULT",
                Barcode = "9300000000014",
                ProductName = "New Auto Default Product",
                Quantity = 1,
                PurchasePrice = 3.00m,
                Amount = 3.00m,
                AutoPricing = null,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-new-auto-default",
                DetailGuids = new List<string> { "detail-new-auto-default" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-new-auto-default");

            Assert.True(result.Success, result.Message);
            Assert.Equal(0, detail.ExistingProductCount);
            Assert.True(detail.AutoPricing);
            Assert.Equal(250m, detail.PricingFloatRate);
            Assert.Equal(7.50m, detail.NewAutoRetailPrice);
        }

        [Fact]
        public async Task CheckProductsAsync_明细已改自动定价_不应被分店价格覆盖为否()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-check-existing-auto", "INV-CHECK-EXISTING-AUTO", new DateTime(2026, 1, 17));
            await _db.Insertable(new Product
            {
                UUID = "product-existing-auto",
                ProductCode = "P-AUTO-001",
                ItemNumber = "AUTO-001",
                Barcode = "9300000000013",
                ProductName = "Existing Auto Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "store-price-existing-auto",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-AUTO-001",
                StoreProductCode = "S01-AUTO-001",
                PurchasePrice = 1.00m,
                StoreRetailPriceValue = 2.00m,
                IsAutoPricing = false,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-existing-auto",
                InvoiceGUID = "invoice-check-existing-auto",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "AUTO-001",
                Barcode = "9300000000013",
                ProductName = "Existing Auto Product",
                Quantity = 1,
                PurchasePrice = 4.00m,
                Amount = 4.00m,
                AutoPricing = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-check-existing-auto",
                DetailGuids = new List<string> { "detail-existing-auto" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-existing-auto");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, detail.ExistingProductCount);
            Assert.True(detail.AutoPricing);
            Assert.Equal(250m, detail.PricingFloatRate);
            Assert.Equal(10.00m, detail.NewAutoRetailPrice);
        }

        [Fact]
        public async Task CheckProductsAsync_UsesIndexedItemNumberLookupForUppercaseItems()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-indexed", "INV-INDEXED", new DateTime(2026, 1, 9));
            await _db.Insertable(new Product
            {
                UUID = "product-indexed",
                ProductCode = "P-INDEXED",
                ItemNumber = "ITEM-INDEXED",
                Barcode = "BAR-INDEXED",
                ProductName = "Indexed Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-indexed",
                InvoiceGUID = "invoice-indexed",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-INDEXED",
                Barcode = "BAR-INDEXED",
                ProductName = "Indexed Product",
                Quantity = 1,
                PurchasePrice = 2.00m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var executedSql = new List<string>();
            _db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-indexed",
            });

            Assert.True(result.Success);
            Assert.DoesNotContain(
                executedSql,
                sql => sql.Contains("UPPER", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("ItemNumber", StringComparison.OrdinalIgnoreCase)
            );
        }

        [Fact]
        public async Task CheckProductsAsync_FallsBackToCaseInsensitiveItemNumberLookup()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-case", "INV-CASE", new DateTime(2026, 1, 9));
            await _db.Insertable(new Product
            {
                UUID = "product-case",
                ProductCode = "P-CASE",
                ItemNumber = "item-case",
                Barcode = "BAR-CASE",
                ProductName = "Case Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-case",
                InvoiceGUID = "invoice-case",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-CASE",
                Barcode = "BAR-CASE",
                ProductName = "Case Product",
                Quantity = 1,
                PurchasePrice = 2.00m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var executedSql = new List<string>();
            _db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-case",
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-case");

            Assert.True(result.Success);
            Assert.Equal("P-CASE", detail.ProductCode);
            Assert.Equal(1, detail.ExistingProductCount);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
            Assert.Contains(
                executedSql,
                sql => sql.Contains("UPPER", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("ItemNumber", StringComparison.OrdinalIgnoreCase)
            );
        }

        [Fact]
        public async Task CheckProductsAsync_主商品匹配且有副条码时_默认添加多码()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-add-multi-check", "INV-ADD-MULTI-CHECK", new DateTime(2026, 1, 9));
            await _db.Insertable(new Product
            {
                UUID = "product-add-multi-check",
                ProductCode = "P-ADD-MULTI-CHECK",
                ItemNumber = "88842",
                Barcode = "191554882676",
                ProductName = "Men Travel Perfume Assorted 35mL",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-add-multi-check",
                InvoiceGUID = "invoice-add-multi-check",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "88842",
                Barcode = "191554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                Quantity = 48,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-add-multi-check",
                DetailGuids = new List<string> { "detail-add-multi-check" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-add-multi-check");

            Assert.True(result.Success, result.Message);
            Assert.Equal("P-ADD-MULTI-CHECK", detail.ProductCode);
            Assert.Equal((int)DetailAction.AddMultiCode, detail.ActivityType);
        }

        [Fact]
        public async Task CheckProductsAsync_副码都已是本商品多码时_默认更新进货价而非添加多码()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-owned-check", "INV-MULTI-OWNED-CHECK", new DateTime(2026, 1, 9));
            await _db.Insertable(new Product
            {
                UUID = "product-multi-owned-check",
                ProductCode = "P-MULTI-OWNED-CHECK",
                ItemNumber = "4303962",
                Barcode = "9310434001674",
                ProductName = "Soft Jellies 240g",
                LocalSupplierCode = "SUP01",
                ProductType = 2,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "set-multi-owned-check",
                ProductCode = "P-MULTI-OWNED-CHECK",
                SetProductCode = "MC-OWNED-CHECK",
                SetItemNumber = "MC-OWNED-CHECK",
                SetBarcode = "9310434005801",
                SetQuantity = 1,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-multi-owned-check",
                    InvoiceGUID = "invoice-multi-owned-check",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "4303962",
                    Barcode = "9310434001674",
                    AdditionalBarcodesJson = JsonSerializer.Serialize(new[] { "9310434005801" }),
                    ProductName = "Soft Jellies 240g",
                    PurchasePrice = 2.5m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    // 对照：同一商品还有一个没加过的副码，仍默认「添加多码」。
                    DetailGUID = "detail-multi-partly-owned-check",
                    InvoiceGUID = "invoice-multi-owned-check",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "4303962",
                    Barcode = "9310434001674",
                    AdditionalBarcodesJson = JsonSerializer.Serialize(new[] { "9310434005801", "9310434005818" }),
                    ProductName = "Soft Jellies 240g",
                    PurchasePrice = 2.5m,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-multi-owned-check",
            });

            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == "invoice-multi-owned-check")
                .ToListAsync();
            Assert.True(result.Success, result.Message);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, details.Single(x => x.DetailGUID == "detail-multi-owned-check").ActivityType);
            Assert.Equal((int)DetailAction.AddMultiCode, details.Single(x => x.DetailGUID == "detail-multi-partly-owned-check").ActivityType);
        }

        [Fact]
        public async Task CheckProductsAsync_有副条码但主条码不属于匹配商品时_等待操作()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-add-multi-mismatch-check", "INV-ADD-MULTI-MISMATCH-CHECK", new DateTime(2026, 1, 9));
            await _db.Insertable(new[]
            {
                new Product
                {
                    UUID = "product-add-multi-mismatch-target",
                    ProductCode = "P-ADD-MULTI-MISMATCH-TARGET",
                    ItemNumber = "88842",
                    Barcode = "191554882676",
                    ProductName = "Men Travel Perfume Assorted 35mL",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
                new Product
                {
                    UUID = "product-add-multi-mismatch-other",
                    ProductCode = "P-ADD-MULTI-MISMATCH-OTHER",
                    ItemNumber = "OTHER-88842",
                    Barcode = "840417950853",
                    ProductName = "Other Product",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-add-multi-mismatch-check",
                InvoiceGUID = "invoice-add-multi-mismatch-check",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "88842",
                Barcode = "840417950853",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                Quantity = 48,
                PurchasePrice = 1.6546m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().CheckProductsAsync(new CheckProductsRequest
            {
                InvoiceGuid = "invoice-add-multi-mismatch-check",
                DetailGuids = new List<string> { "detail-add-multi-mismatch-check" },
            });

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-add-multi-mismatch-check");

            Assert.True(result.Success, result.Message);
            Assert.Equal("P-ADD-MULTI-MISMATCH-TARGET", detail.ProductCode);
            Assert.Equal((int)DetailAction.WaitForOperation, detail.ActivityType);
        }

        [Fact]
        public async Task QueryInChunksParallelAsync_多Chunk查询使用独立连接()
        {
            var service = CreateService();
            var method = typeof(LocalSupplierInvoicesReactService)
                .GetMethod("QueryInChunksParallelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(string), typeof(string));
            var keys = Enumerable.Range(1, 6).Select(index => $"K{index:000}").ToList();
            var clientsByFirstKey = new ConcurrentDictionary<string, ISqlSugarClient>();

            Func<ISqlSugarClient, List<string>, Task<List<string>>> fetch = (db, chunk) =>
            {
                clientsByFirstKey[chunk[0]] = db;
                return Task.FromResult(chunk.ToList());
            };

            var task = (Task<List<string>>)method.Invoke(
                service,
                new object[] { keys, 2, fetch, 3 }
            )!;
            var result = await task;

            Assert.Equal(keys, result);
            Assert.Equal(3, clientsByFirstKey.Count);
            Assert.NotSame(clientsByFirstKey["K001"], clientsByFirstKey["K003"]);
            Assert.NotSame(clientsByFirstKey["K003"], clientsByFirstKey["K005"]);
        }

        [Fact]
        public void BatchExecuteActionsRequestDto_包含确认契约字段()
        {
            Assert.NotNull(typeof(BatchExecuteActionsRequestDto).GetProperty("ExpectedActions"));
            Assert.NotNull(typeof(BatchExecuteActionsRequestDto).GetProperty("ConfirmedCreateProductCount"));
            Assert.NotNull(typeof(BatchExecuteActionsRequestDto).GetProperty("ConfirmedAt"));
            Assert.NotNull(typeof(BatchExecuteActionsRequestDto).GetProperty("NewProductProductTypeSelections"));

            var actionDtoType = typeof(BatchExecuteActionsRequestDto).Assembly.GetType(
                "BlazorApp.Shared.DTOs.BatchExecuteExpectedActionDto"
            );

            Assert.NotNull(actionDtoType);
            Assert.NotNull(actionDtoType!.GetProperty("DetailGuid"));
            Assert.NotNull(actionDtoType.GetProperty("ActivityType"));
            Assert.NotNull(actionDtoType.GetProperty("Action"));

            var productTypeSelectionDtoType = typeof(BatchExecuteActionsRequestDto).Assembly.GetType(
                "BlazorApp.Shared.DTOs.BatchExecuteNewProductProductTypeSelectionDto"
            );
            Assert.NotNull(productTypeSelectionDtoType);
            Assert.NotNull(productTypeSelectionDtoType!.GetProperty("DetailGuid"));
            Assert.NotNull(productTypeSelectionDtoType.GetProperty("ProductType"));
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_RejectsEmptyDetailGuids()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-empty", "INV-EMPTY", new DateTime(2026, 1, 9));

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-empty",
                new List<string>(),
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
        }

        [Fact]
        public void BatchExecuteActionsAsync_必须在获取商品锁后复读并核对完整执行身份()
        {
            var source = File.ReadAllText(ResolveLocalSupplierInvoicesProductExecutionCommandWriterPath());
            var methodStart = source.IndexOf(
                "public async Task<ProductExecutionCommandResult> ExecuteAsync",
                StringComparison.Ordinal
            );
            var methodEnd = source.IndexOf(
                "private async Task ExecuteGroupsAsync",
                methodStart,
                StringComparison.Ordinal
            );
            Assert.True(methodStart >= 0 && methodEnd > methodStart);
            var method = source[methodStart..methodEnd];

            var beginTransaction = method.IndexOf("BeginTranAsync", StringComparison.Ordinal);
            var acquireLock = method.IndexOf(
                "SetChildPurchasePriceMutationLock.Acquire",
                StringComparison.Ordinal
            );
            var lockedData = method.IndexOf("var lockedData", StringComparison.Ordinal);
            var compareIdentity = method.IndexOf("TryValidateLockedData", lockedData, StringComparison.Ordinal);
            var ensureLockCoverage = method.IndexOf("lockScope?.EnsureCovers", StringComparison.Ordinal);
            var validation = method.IndexOf("ValidateLockedDetailsAsync", StringComparison.Ordinal);

            Assert.True(beginTransaction >= 0);
            Assert.True(acquireLock > beginTransaction);
            Assert.True(lockedData > acquireLock);
            Assert.True(compareIdentity > lockedData);
            Assert.True(ensureLockCoverage > compareIdentity);
            Assert.True(validation > ensureLockCoverage);
            var plan = File.ReadAllText(ResolveLocalSupplierInvoicesProductExecutionPlanPath());
            Assert.Contains("detail.ActivityType", plan);
            Assert.Contains("detail.AdditionalBarcodesJson", plan);
            Assert.Contains("detail.PurchasePrice", plan);
            Assert.Contains("detail.RetailPrice", plan);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_锁内动作与用户确认快照不一致时应零写入()
        {
            await SeedExecutablePriceUpdateAsync();
            var beforeProduct = await _db.Queryable<Product>()
                .SingleAsync(product => product.ProductCode == "P001");
            var beforeStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(price => price.ProductCode == "P001");

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price" },
                "tester",
                expectedActions: new List<BatchExecuteExpectedActionDto>
                {
                    new()
                    {
                        DetailGuid = "detail-price",
                        ActivityType = (int)DetailAction.AddMultiCode,
                    },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Contains("确认", result.Message);
            var afterProduct = await _db.Queryable<Product>()
                .SingleAsync(product => product.ProductCode == "P001");
            var afterStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(price => price.ProductCode == "P001");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(item => item.DetailGUID == "detail-price");
            Assert.Equal(beforeProduct.PurchasePrice, afterProduct.PurchasePrice);
            Assert.Equal(beforeStorePrice.PurchasePrice, afterStorePrice.PurchasePrice);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
            Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_确认后同动作执行参数变化时应零写入()
        {
            await SeedExecutablePriceUpdateAsync();
            var confirmedDetail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(item => item.DetailGUID == "detail-price");
            var beforeProduct = await _db.Queryable<Product>()
                .SingleAsync(product => product.ProductCode == "P001");
            var beforeStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(price => price.ProductCode == "P001");
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(detail => detail.PurchasePrice == 99m)
                .Where(detail => detail.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price" },
                "tester",
                expectedActions: new List<BatchExecuteExpectedActionDto>
                {
                    new()
                    {
                        DetailGuid = "detail-price",
                        ActivityType = (int)DetailAction.UpdatePurchasePrice,
                    },
                },
                confirmedDetails: new[] { confirmedDetail }
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Contains("执行参数", result.Message);
            var afterProduct = await _db.Queryable<Product>()
                .SingleAsync(product => product.ProductCode == "P001");
            var afterStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(price => price.ProductCode == "P001");
            var changedDetail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(item => item.DetailGUID == "detail-price");
            Assert.Equal(beforeProduct.PurchasePrice, afterProduct.PurchasePrice);
            Assert.Equal(beforeStorePrice.PurchasePrice, afterStorePrice.PurchasePrice);
            Assert.Equal(99m, changedDetail.PurchasePrice);
            Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActions_确认动作与当前数据库不一致时_拒绝执行()
        {
            await SeedExecutablePriceUpdateAsync();

            var controller = CreateController();
            var actionResult = await controller.BatchExecuteActions(
                "invoice-execute",
                new BatchExecuteActionsRequestDto
                {
                    DetailGuids = new List<string> { "detail-price" },
                    ExpectedActions = new List<BatchExecuteExpectedActionDto>
                    {
                        new()
                        {
                            DetailGuid = "detail-price",
                            ActivityType = (int)DetailAction.AddMultiCode,
                        },
                    },
                    ConfirmedCreateProductCount = 0,
                    ConfirmedAt = DateTime.UtcNow,
                }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.False(ReadAnonymousProperty<bool>(badRequest.Value!, "success"));
            Assert.Equal("VALIDATION_ERROR", ReadAnonymousProperty<string>(badRequest.Value!, "code"));
            Assert.Contains("确认", ReadAnonymousProperty<string>(badRequest.Value!, "message"));
            Assert.NotNull(ReadAnonymousProperty<object>(badRequest.Value!, "details"));
        }

        [Fact]
        public async Task BatchExecuteActions_确认创建商品数量与当前数据库不一致时_拒绝执行()
        {
            await SeedExecutablePriceUpdateAsync();

            var controller = CreateController();
            var actionResult = await controller.BatchExecuteActions(
                "invoice-execute",
                new BatchExecuteActionsRequestDto
                {
                    DetailGuids = new List<string> { "detail-price" },
                    ExpectedActions = new List<BatchExecuteExpectedActionDto>
                    {
                        new()
                        {
                            DetailGuid = "detail-price",
                            ActivityType = (int)DetailAction.UpdatePurchasePrice,
                        },
                    },
                    ConfirmedCreateProductCount = 1,
                    ConfirmedAt = DateTime.UtcNow,
                }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.False(ReadAnonymousProperty<bool>(badRequest.Value!, "success"));
            Assert.Equal("VALIDATION_ERROR", ReadAnonymousProperty<string>(badRequest.Value!, "code"));
            Assert.Contains("创建商品", ReadAnonymousProperty<string>(badRequest.Value!, "message"));
            Assert.NotNull(ReadAnonymousProperty<object>(badRequest.Value!, "details"));
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_UsesSavedActivityTypeAndUpdatesByProductCode()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondExecutablePriceUpdateDetailAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price", "detail-price-2" },
                "tester"
            );

            Assert.True(result.Success);
            Assert.Equal(2, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(0, result.Data?.AddedMultiCodes);

            var product = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "P001");
            var secondProduct = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "P002");
            var price = await _db.Queryable<StoreRetailPrice>().FirstAsync(x => x.ProductCode == "P001");
            var secondPrice = await _db.Queryable<StoreRetailPrice>().FirstAsync(x => x.ProductCode == "P002");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-price");
            var secondDetail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-price-2");
            var multiCodeCount = await _db.Queryable<StoreMultiCodeProduct>().CountAsync();

            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal(6.66m, secondProduct.PurchasePrice);
            Assert.Equal(5.55m, price.PurchasePrice);
            Assert.Equal(6.66m, secondPrice.PurchasePrice);
            Assert.Equal(99, detail.ActivityType);
            Assert.Equal(99, secondDetail.ActivityType);
            Assert.Equal(0, multiCodeCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_更新进货价为0时跳过()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.PurchasePrice == 0m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price" },
                "tester"
            );

            var product = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "P001");
            var price = await _db.Queryable<StoreRetailPrice>().FirstAsync(x => x.ProductCode == "P001");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-price");

            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            Assert.Equal(0, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(1, result.Data?.Skipped);
            Assert.Equal(0, result.Data?.Failed);
            Assert.Equal(1.11m, product.PurchasePrice);
            Assert.Equal(1.11m, price.PurchasePrice);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_更新套装主商品进货价时自动补齐有分店价门店的缺失子项()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedActiveSetRelationsAsync("P001", "AUTO");
            await _db.Insertable(new[]
            {
                new Store { StoreGUID = "store-guid-s02", StoreCode = "S02", StoreName = "Store 2", IsActive = true, IsDeleted = false },
                new Store { StoreGUID = "store-guid-s03", StoreCode = "S03", StoreName = "Store 3", IsActive = true, IsDeleted = false },
            }).ExecuteCommandAsync();
            // S01、S02 都有分店价但没有任何门店子项（生产 1042 缺 G114109 子项即此形态）；
            // S03 没有分店价，补齐不应凭空扩展到该店。
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "SRP-001-S02",
                StoreCode = "S02",
                ProductCode = "P001",
                StoreProductCode = "S02P001",
                SupplierCode = "SUP01",
                PurchasePrice = 1.11m,
                StoreRetailPriceValue = 2.22m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price" },
                "tester"
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
            var relations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == "P001" && x.IsActive && !x.IsDeleted)
                .ToListAsync();
            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            Assert.Equal(1, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal(
                new[] { "S01", "S02" },
                relations.Select(x => x.StoreCode).Distinct().OrderBy(x => x).ToArray()
            );
            Assert.Equal(4, relations.Count);
            Assert.All(relations, relation => Assert.Equal("tester", relation.CreatedBy));
            Assert.All(relations, relation => Assert.True(relation.PurchasePrice > 0m));
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_套装子项无法自动补齐时整单回滚并返回涉及货号()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedActiveSetRelationsAsync("P001", "UNSAFE");
            // 停用墓碑不会被自动复活，补齐失败后严格重算仍报缺子项，整单必须回滚。
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "S01-P001-TOMBSTONE",
                StoreCode = "S01",
                ProductCode = "P001",
                MultiCodeProductCode = "UNSAFE-CHILD-T1",
                StoreMultiCodeProductCode = "S01UNSAFE-CHILD-T1",
                MultiBarcode = "UNSAFE-BAR-T1",
                PurchasePrice = 7.77m,
                MultiCodeRetailPrice = 4m,
                IsActive = false,
                IsDeleted = true,
                UpdatedBy = "历史操作人",
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price" },
                "tester"
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price");
            var activeRelations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == "P001" && x.IsActive && !x.IsDeleted)
                .ToListAsync();
            Assert.False(result.Success);
            Assert.Equal("SET_CHILD_COST_RECALCULATION_INCOMPLETE", result.ErrorCode);
            Assert.Contains("ITEM-OLD（P001）", result.Message);
            var failure = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Equal(1, failure.Failed);
            Assert.Equal(0, failure.UpdatedPurchasePrices);
            Assert.Contains(failure.Errors, error => error.Contains("已停用或软删除", StringComparison.Ordinal));
            Assert.Contains(failure.Errors, error => error.Contains("套装子项成本无法完整重算", StringComparison.Ordinal));
            Assert.Equal(1.11m, product.PurchasePrice);
            Assert.Empty(activeRelations);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_UpdateItemNumber_BatchUpdatesProducts()
        {
            await SeedExecutableItemNumberUpdatesAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-item-update",
                new List<string> { "detail-item-1", "detail-item-2" },
                "tester"
            );

            Assert.True(result.Success);
            Assert.Equal(2, result.Data?.UpdatedItemNumbers);

            var firstProduct = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "PITEM1");
            var secondProduct = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "PITEM2");
            var completedDetails = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => new[] { "detail-item-1", "detail-item-2" }.Contains(x.DetailGUID))
                .ToListAsync();

            Assert.Equal("ITEM-NEW-1", firstProduct.ItemNumber);
            Assert.Equal("ITEM-NEW-2", secondProduct.ItemNumber);
            Assert.All(completedDetails, detail => Assert.Equal(99, detail.ActivityType));

            var grid = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20 });
            Assert.True(Assert.Single(grid.Items!, item => item.InvoiceGUID == "invoice-item-update").IsProductChecked);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_更新货号未关联商品时按条码唯一匹配本供应商商品并回填编码()
        {
            // 生产 10-07 进货单 387250：货号 BEA 345618 与主档 BEA345618 只差空格，检测判主档不存在，
            // 用户直接改成「更新货号」，明细没有商品编码导致整批「未找到商品编码」。
            await SeedUnlinkedItemNumberUpdateAsync(extraProducts: Array.Empty<Product>());

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-item-unlinked",
                new List<string> { "detail-unlinked" },
                "tester"
            );

            Assert.True(result.Success, result.Message + string.Join(";", result.Data?.Errors ?? new()));
            Assert.Equal(1, result.Data?.UpdatedItemNumbers);
            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "PUNLINKED");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>().SingleAsync(x => x.DetailGUID == "detail-unlinked");
            Assert.Equal("BEA 345618", product.ItemNumber);
            Assert.Equal("PUNLINKED", detail.ProductCode);
            Assert.Equal(99, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_更新货号未关联商品且条码匹配本供应商多个商品时提示先选用且零写入()
        {
            await SeedUnlinkedItemNumberUpdateAsync(extraProducts: new[]
            {
                new Product
                {
                    UUID = "UUID-UNLINKED-DUP",
                    ProductCode = "PUNLINKED-DUP",
                    ItemNumber = "BEA345618-B",
                    Barcode = "9320760345618",
                    ProductName = "Duplicate barcode product",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
            });

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-item-unlinked",
                new List<string> { "detail-unlinked" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
            var failure = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Contains(failure.Errors, error => error.Contains("请先「选用」", StringComparison.Ordinal)
                && error.Contains("BEA 345618", StringComparison.Ordinal));
            var products = await _db.Queryable<Product>().Where(x => x.Barcode == "9320760345618").ToListAsync();
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>().SingleAsync(x => x.DetailGUID == "detail-unlinked");
            Assert.DoesNotContain(products, x => x.ItemNumber == "BEA 345618");
            Assert.Null(detail.ProductCode);
            Assert.Equal((int)DetailAction.UpdateItemNumber, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_更新货号未关联商品且条码只匹配别家供应商商品时不解析()
        {
            await SeedUnlinkedItemNumberUpdateAsync(extraProducts: Array.Empty<Product>(), productSupplierCode: "OTHER-SUP");

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-item-unlinked",
                new List<string> { "detail-unlinked" },
                "tester"
            );

            Assert.False(result.Success);
            var failure = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Contains(failure.Errors, error => error.Contains("请先「选用」", StringComparison.Ordinal));
            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "PUNLINKED");
            Assert.Equal("BEA345618", product.ItemNumber);
        }

        private async Task SeedUnlinkedItemNumberUpdateAsync(IEnumerable<Product> extraProducts, string productSupplierCode = "SUP01")
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-item-unlinked", "INV-ITEM-UNLINKED", new DateTime(2026, 10, 7));
            await _db.Insertable(new List<Product>
            {
                new()
                {
                    UUID = "UUID-UNLINKED",
                    ProductCode = "PUNLINKED",
                    ItemNumber = "BEA345618",
                    Barcode = "9320760345618",
                    ProductName = "Kids hair clips",
                    LocalSupplierCode = productSupplierCode,
                    PurchasePrice = 0.88m,
                    RetailPrice = 2.5m,
                    IsDeleted = false,
                },
            }.Concat(extraProducts).ToList()).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-unlinked",
                InvoiceGUID = "invoice-item-unlinked",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "BEA 345618",
                Barcode = "9320760345618",
                ProductName = "Kids hair clips",
                Quantity = 12,
                PurchasePrice = 0.88m,
                ProductCode = null,
                ExistingProductCount = 0,
                BarcodeStatus = 2,
                BarcodeMatchCount = 1,
                ActivityType = (int)DetailAction.UpdateItemNumber,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_商品主档变化记录共享批次和发票来源()
        {
            await SeedExecutableItemNumberUpdatesAsync();
            var history = CreateSnapshotHistoryMock();
            WarehouseProductChangeHistoryContextDto? recordedContext = null;
            history
                .Setup(x =>
                    x.RecordChangesAsync(
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                    WarehouseProductChangeHistoryContextDto,
                    CancellationToken
                >((_, _, context, _) => recordedContext = context)
                .ReturnsAsync(2);

            var result = await CreateService(changeHistoryService: history.Object)
                .BatchExecuteActionsAsync(
                    "invoice-item-update",
                    new List<string> { "detail-item-1", "detail-item-2" },
                    "audit-user"
                );

            Assert.True(result.Success, result.Message);
            Assert.NotNull(recordedContext);
            Assert.Equal("BatchUpdate", recordedContext!.Action);
            Assert.Equal("LocalSupplierInvoice", recordedContext.Source);
            Assert.Equal("invoice-item-update", recordedContext.SourceReference);
            Assert.Equal("audit-user", recordedContext.ActorName);
            Assert.NotNull(recordedContext.BatchGuid);
            history.Verify(
                x =>
                    x.CaptureSnapshotsAsync(
                        It.Is<IEnumerable<string>>(codes =>
                            codes.OrderBy(code => code)
                                .SequenceEqual(new[] { "PITEM1", "PITEM2" })
                        ),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Exactly(2)
            );
            history.Verify(
                x =>
                    x.RecordChangesAsync(
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_历史写入失败回滚商品和明细()
        {
            await SeedExecutableItemNumberUpdatesAsync();
            var history = CreateSnapshotHistoryMock();
            history
                .Setup(x =>
                    x.RecordChangesAsync(
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("history failed"));

            var result = await CreateService(changeHistoryService: history.Object)
                .BatchExecuteActionsAsync(
                    "invoice-item-update",
                    new List<string> { "detail-item-1", "detail-item-2" },
                    "audit-user"
                );

            Assert.False(result.Success);
            var products = await _db.Queryable<Product>()
                .Where(x => new[] { "PITEM1", "PITEM2" }.Contains(x.ProductCode))
                .OrderBy(x => x.ProductCode)
                .ToListAsync();
            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => new[] { "detail-item-1", "detail-item-2" }.Contains(x.DetailGUID))
                .OrderBy(x => x.DetailGUID)
                .ToListAsync();
            Assert.Equal(new[] { "ITEM-OLD-1", "ITEM-OLD-2" }, products.Select(x => x.ItemNumber));
            Assert.All(details, detail =>
                Assert.Equal((int)DetailAction.UpdateItemNumber, detail.ActivityType)
            );
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenAnySelectedDetailIsInvalid_RollsBackAllChanges()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-invalid",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-MISSING",
                Barcode = "BAR-MISSING",
                ProductName = "Invalid Detail",
                PurchasePrice = 3.33m,
                ExistingProductCount = 1,
                BarcodeStatus = 1,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-execute",
                new List<string> { "detail-price", "detail-invalid" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);

            var product = await _db.Queryable<Product>().FirstAsync(x => x.ProductCode == "P001");
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-price");
            Assert.Equal(1.11m, product.PurchasePrice);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenCreateProductItemNumberDiffersOnlyByCaseFromExisting_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-item-case", "INV-CREATE-ITEM-CASE", new DateTime(2026, 1, 11));
            await _db.Insertable(new Product
            {
                UUID = "product-existing-item-case",
                ProductCode = "P-DUP-ITEM",
                ItemNumber = "ITEM-DUP",
                Barcode = "BAR-KEEP",
                ProductName = "Existing Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-item-case",
                InvoiceGUID = "invoice-create-item-case",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "item-dup",
                Barcode = "BAR-NEW",
                ProductName = "Duplicate Item Product",
                PurchasePrice = 2.22m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-item-case",
                new List<string> { "detail-create-item-case" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenCreateProductBarcodeDiffersOnlyByCaseFromExisting_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-barcode-case", "INV-CREATE-BARCODE-CASE", new DateTime(2026, 1, 11));
            await _db.Insertable(new Product
            {
                UUID = "product-existing-barcode-case",
                ProductCode = "P-DUP-BARCODE",
                ItemNumber = "ITEM-KEEP",
                Barcode = "BAR-DUP",
                ProductName = "Existing Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-barcode-case",
                InvoiceGUID = "invoice-create-barcode-case",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-NEW",
                Barcode = "bar-dup",
                ProductName = "Duplicate Barcode Product",
                PurchasePrice = 2.22m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-barcode-case",
                new List<string> { "detail-create-barcode-case" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenCreateProductValuesDifferOnlyByCaseWithinBatch_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-batch-case", "INV-CREATE-BATCH-CASE", new DateTime(2026, 1, 11));
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                new()
                {
                    DetailGUID = "detail-create-batch-case-1",
                    InvoiceGUID = "invoice-create-batch-case",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "ITEM-CASE",
                    Barcode = "BAR-CASE",
                    ProductName = "Batch Duplicate 1",
                    PurchasePrice = 2.22m,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
                new()
                {
                    DetailGUID = "detail-create-batch-case-2",
                    InvoiceGUID = "invoice-create-batch-case",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "item-case",
                    Barcode = "bar-case",
                    ProductName = "Batch Duplicate 2",
                    PurchasePrice = 3.33m,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-batch-case",
                new List<string>
                {
                    "detail-create-batch-case-1",
                    "detail-create-batch-case-2",
                },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(0, await _db.Queryable<Product>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenCreateProductDuplicateExists_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-dup", "INV-CREATE-DUP", new DateTime(2026, 1, 11));
            await _db.Insertable(new Product
            {
                UUID = "product-existing",
                ProductCode = "P-DUP",
                ItemNumber = "ITEM-DUP",
                Barcode = "BAR-DUP",
                ProductName = "Existing Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-dup",
                InvoiceGUID = "invoice-create-dup",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-DUP",
                Barcode = "BAR-DUP-NEW",
                ProductName = "Duplicate Product",
                PurchasePrice = 2.22m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-dup",
                new List<string> { "detail-create-dup" },
                "tester"
            );

            var productCount = await _db.Queryable<Product>().CountAsync();

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(1, productCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_CreateProduct_商品已建好并回填编码时标记已执行不报已存在()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-already", "INV-CREATE-ALREADY", new DateTime(2026, 10, 5));
            // 生产 10-07 实例（388353 · FRA 327935）：商品早由「更新HQ商品」建好并回填编码，明细仍挂着「新建商品」。
            await _db.Insertable(new Product
            {
                UUID = "product-already-created",
                ProductCode = "P-ALREADY",
                ItemNumber = "FRA 327935",
                Barcode = "9320760327935",
                ProductName = "30X30CM ICONIC FRAME BLACK",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 1.2m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "detail-create-already",
                    InvoiceGUID = "invoice-create-already",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ProductCode = "P-ALREADY",
                    ItemNumber = "fra 327935",
                    Barcode = "9320760327935",
                    ProductName = "30X30CM ICONIC FRAME BLACK",
                    PurchasePrice = 1.2m,
                    ExistingProductCount = 0,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    // 对照：同一批里真正的新品照常新建。
                    DetailGUID = "detail-create-fresh",
                    InvoiceGUID = "invoice-create-already",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "FRESH-001",
                    Barcode = "9320760999999",
                    ProductName = "Fresh Product",
                    PurchasePrice = 2.5m,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-already",
                new List<string> { "detail-create-already", "detail-create-fresh" },
                "tester"
            );

            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            Assert.Equal(1, result.Data?.CreatedProducts);
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync(x => x.ItemNumber == "FRA 327935"));
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync(x => x.ItemNumber == "FRESH-001"));
            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == "invoice-create-already")
                .ToListAsync();
            Assert.All(details, x => Assert.Equal(99, x.ActivityType));
            Assert.Equal("P-ALREADY", details.Single(x => x.DetailGUID == "detail-create-already").ProductCode);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_CreateProduct_已关联编码属于别家供应商时仍按已存在报错()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-mismatch", "INV-CREATE-MISMATCH", new DateTime(2026, 10, 5));
            await _db.Insertable(new[]
            {
                new Product
                {
                    UUID = "product-other-supplier",
                    ProductCode = "P-OTHER-SUPPLIER",
                    ItemNumber = "MISMATCH-1",
                    Barcode = "9320760888888",
                    ProductName = "Other Supplier Product",
                    LocalSupplierCode = "SUP02",
                    IsDeleted = false,
                },
                new Product
                {
                    UUID = "product-same-identity",
                    ProductCode = "P-SAME-IDENTITY",
                    ItemNumber = "MISMATCH-1",
                    Barcode = "9320760777777",
                    ProductName = "Same Identity Product",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-mismatch",
                InvoiceGUID = "invoice-create-mismatch",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-OTHER-SUPPLIER",
                ItemNumber = "MISMATCH-1",
                Barcode = "9320760888888",
                ProductName = "Mismatch Product",
                PurchasePrice = 2.2m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-mismatch",
                new List<string> { "detail-create-mismatch" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(2, await _db.Queryable<Product>().CountAsync());
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-create-mismatch");
            Assert.Equal((int)DetailAction.CreateProduct, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenMultiCodeDuplicateDiffersOnlyByCase_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-case", "INV-MULTI-CASE", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-multi-case",
                ProductCode = "P-MULTI-CASE",
                ItemNumber = "ITEM-MULTI",
                Barcode = "BAR-MAIN",
                ProductName = "Multi Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "multi-existing-case",
                StoreCode = "S01",
                // 挂在别的商品上才是冲突；大小写不同也要识别。
                ProductCode = "P-MULTI-CASE-OTHER-OWNER",
                MultiCodeProductCode = "MULTI-CASE-001",
                StoreMultiCodeProductCode = "S01MULTI-CASE-001",
                MultiBarcode = "BAR-MULTI",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-case",
                InvoiceGUID = "invoice-multi-case",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI-CASE",
                ItemNumber = "ITEM-MULTI-ALT",
                Barcode = "bar-multi",
                ProductName = "Multi Product",
                PurchasePrice = 1.23m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER "reject_type2_global_cost_on_insert"
                BEFORE INSERT ON "ProductSetCode"
                WHEN NEW."SetPurchasePrice" IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'Type2 global cost must be empty on insert');
                END;
                """
            );
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER "reject_type2_store_cost_on_insert"
                BEFORE INSERT ON "StoreMultiCodeProduct"
                WHEN NEW."PurchasePrice" IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'Type2 store cost must be empty on insert');
                END;
                """
            );

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-case",
                new List<string> { "detail-multi-case" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(1, await _db.Queryable<StoreMultiCodeProduct>().CountAsync());
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_WhenMultiCodeDuplicateExists_ReturnsValidationError()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-dup", "INV-MULTI-DUP", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-multi",
                ProductCode = "P-MULTI",
                ItemNumber = "ITEM-MULTI",
                Barcode = "BAR-MAIN",
                ProductName = "Multi Product",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            // 已有分店多码挂在别的商品上才是冲突；挂在同一商品上视为已添加（见「已属于同一商品」用例）。
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "multi-existing",
                StoreCode = "S01",
                ProductCode = "P-MULTI-OTHER-OWNER",
                MultiCodeProductCode = "MULTI-001",
                StoreMultiCodeProductCode = "S01MULTI-001",
                MultiBarcode = "BAR-MULTI",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-dup",
                InvoiceGUID = "invoice-multi-dup",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI",
                ItemNumber = "ITEM-MULTI-ALT",
                Barcode = "BAR-MULTI",
                ProductName = "Multi Product",
                PurchasePrice = 1.23m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-dup",
                new List<string> { "detail-multi-dup" },
                "tester"
            );

            var multiCodeCount = await _db.Queryable<StoreMultiCodeProduct>().CountAsync();
            var productSetCodeCount = await _db.Queryable<ProductSetCode>().CountAsync();

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            Assert.Equal(1, multiCodeCount);
            Assert.Equal(0, productSetCodeCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_AddMultiCode_副条码分别写入多码表()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-secondary", "INV-MULTI-SECONDARY", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-multi-secondary",
                ProductCode = "P-MULTI-SECONDARY",
                ItemNumber = "88842",
                Barcode = "191554882676",
                ProductName = "Men Travel Perfume Assorted 35mL",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 1.6546m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-secondary",
                InvoiceGUID = "invoice-multi-secondary",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI-SECONDARY",
                ItemNumber = "88842",
                Barcode = "191554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                PurchasePrice = 1.6546m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-secondary",
                new List<string> { "detail-multi-secondary" },
                "tester"
            );

            var productSetCodes = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == "P-MULTI-SECONDARY")
                .OrderBy(x => x.SetBarcode)
                .ToListAsync();
            var storeMultiCodes = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == "P-MULTI-SECONDARY")
                .OrderBy(x => x.MultiBarcode)
                .ToListAsync();
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-multi-secondary");
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == "P-MULTI-SECONDARY");

            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            Assert.Equal(2, result.Data?.AddedMultiCodes);
            Assert.Equal(2, product.ProductType);
            Assert.Equal(new[] { "191554882669", "191554882690" }, productSetCodes.Select(x => x.SetBarcode).ToArray());
            Assert.Equal(new[] { "191554882669", "191554882690" }, storeMultiCodes.Select(x => x.MultiBarcode).ToArray());
            Assert.Equal(99, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_AddMultiCode_副条码被其他商品占用时回滚()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-secondary-dup", "INV-MULTI-SECONDARY-DUP", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-multi-secondary-dup",
                ProductCode = "P-MULTI-SECONDARY-DUP",
                ItemNumber = "88842",
                Barcode = "191554882676",
                ProductName = "Men Travel Perfume Assorted 35mL",
                LocalSupplierCode = "SUP01",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "multi-existing-secondary",
                StoreCode = "S01",
                ProductCode = "P-MULTI-SECONDARY-OTHER-OWNER",
                MultiCodeProductCode = "MULTI-SECONDARY-001",
                StoreMultiCodeProductCode = "S01MULTI-SECONDARY-001",
                MultiBarcode = "191554882669",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-secondary-dup",
                InvoiceGUID = "invoice-multi-secondary-dup",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI-SECONDARY-DUP",
                ItemNumber = "88842",
                Barcode = "191554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                PurchasePrice = 1.6546m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-secondary-dup",
                new List<string> { "detail-multi-secondary-dup" },
                "tester"
            );

            var multiCodeCount = await _db.Queryable<StoreMultiCodeProduct>().CountAsync();
            var productSetCodeCount = await _db.Queryable<ProductSetCode>().CountAsync();

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            var validationDetails = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Contains(validationDetails.Errors, error => error.Contains("191554882669", StringComparison.Ordinal));
            Assert.Equal(1, multiCodeCount);
            Assert.Equal(0, productSetCodeCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_AddMultiCode_副条码已属于同一商品时跳过已有记录只补缺失分店()
        {
            await SeedStoreAndSupplierAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-multi-second",
                StoreCode = "S02",
                StoreName = "Second Store",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await InsertInvoiceAsync("invoice-multi-same-owner", "INV-MULTI-SAME-OWNER", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-multi-same-owner",
                ProductCode = "P-MULTI-SAME",
                ItemNumber = "4303962",
                Barcode = "9310434001674",
                ProductName = "Soft Jellies 240g",
                LocalSupplierCode = "SUP01",
                ProductType = 2,
                PurchasePrice = 2.5m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            // 生产 10-07 实例：副码 5801 早已是该商品的一品多码，但只在 S01 有分店记录。
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "set-existing-same-owner",
                ProductCode = "P-MULTI-SAME",
                SetProductCode = "MC-EXISTING",
                SetItemNumber = "MC-EXISTING",
                SetBarcode = "9310434005801",
                SetQuantity = 1,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "store-multi-existing-same-owner",
                StoreCode = "S01",
                ProductCode = "P-MULTI-SAME",
                MultiCodeProductCode = "MC-EXISTING",
                StoreMultiCodeProductCode = "S01MC-EXISTING",
                MultiBarcode = "9310434005801",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-same-owner",
                InvoiceGUID = "invoice-multi-same-owner",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI-SAME",
                ItemNumber = "4303962",
                Barcode = "9310434001674",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[] { "9310434005801", "9310434005818" }),
                ProductName = "Soft Jellies 240g",
                PurchasePrice = 2.5m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-same-owner",
                new List<string> { "detail-multi-same-owner" },
                "tester"
            );

            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            var existingSets = await _db.Queryable<ProductSetCode>()
                .Where(x => x.SetBarcode == "9310434005801")
                .ToListAsync();
            var existingStores = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.MultiBarcode == "9310434005801")
                .OrderBy(x => x.StoreCode)
                .ToListAsync();
            var newSets = await _db.Queryable<ProductSetCode>()
                .Where(x => x.SetBarcode == "9310434005818")
                .ToListAsync();
            var newStores = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.MultiBarcode == "9310434005818")
                .ToListAsync();
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-multi-same-owner");

            // 已有关系不重复新建；缺的 S02 补上，并沿用已有关系的多码商品编码。
            Assert.Single(existingSets);
            Assert.Equal(new[] { "S01", "S02" }, existingStores.Select(x => x.StoreCode).ToArray());
            Assert.All(existingStores, x => Assert.Equal("MC-EXISTING", x.MultiCodeProductCode));
            // 同一行里真正新的副码照常写入两张表。
            Assert.Single(newSets);
            Assert.Equal(2, newStores.Count);
            Assert.All(newStores, x => Assert.Equal(newSets[0].SetProductCode, x.MultiCodeProductCode));
            Assert.Equal(99, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_AddMultiCode_副条码主条码不属于商品时校验失败并回滚()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-multi-secondary-mismatch", "INV-MULTI-SECONDARY-MISMATCH", new DateTime(2026, 1, 12));
            await _db.Insertable(new[]
            {
                new Product
                {
                    UUID = "product-multi-secondary-mismatch-target",
                    ProductCode = "P-MULTI-SECONDARY-MISMATCH-TARGET",
                    ItemNumber = "88842",
                    Barcode = "191554882676",
                    ProductName = "Men Travel Perfume Assorted 35mL",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
                new Product
                {
                    UUID = "product-multi-secondary-mismatch-other",
                    ProductCode = "P-MULTI-SECONDARY-MISMATCH-OTHER",
                    ItemNumber = "OTHER-88842",
                    Barcode = "840417950853",
                    ProductName = "Other Product",
                    LocalSupplierCode = "SUP01",
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-multi-secondary-mismatch",
                InvoiceGUID = "invoice-multi-secondary-mismatch",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-MULTI-SECONDARY-MISMATCH-TARGET",
                ItemNumber = "88842",
                Barcode = "840417950853",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                PurchasePrice = 1.6546m,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-multi-secondary-mismatch",
                new List<string> { "detail-multi-secondary-mismatch" },
                "tester"
            );

            var multiCodeCount = await _db.Queryable<StoreMultiCodeProduct>().CountAsync();
            var productSetCodeCount = await _db.Queryable<ProductSetCode>().CountAsync();

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            var validationDetails = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Contains(validationDetails.Errors, error => error.Contains("主条码未匹配当前商品", StringComparison.Ordinal));
            Assert.Equal(0, multiCodeCount);
            Assert.Equal(0, productSetCodeCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_CreateProductWithAdditionalBarcodes_选择多码时写入两张多码表()
        {
            await SeedStoreAndSupplierAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-2",
                StoreCode = "S02",
                StoreName = "Melbourne Store",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await InsertInvoiceAsync("invoice-create-multicode-product", "INV-CREATE-MULTICODE", new DateTime(2026, 1, 12));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-multicode-product",
                InvoiceGUID = "invoice-create-multicode-product",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "88842",
                Barcode = "191554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "191554882690",
                    "191554882669",
                }),
                ProductName = "Men Travel Perfume Assorted 35mL",
                PurchasePrice = 1.6546m,
                RetailPrice = 3.99m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-multicode-product",
                new List<string> { "detail-create-multicode-product" },
                "tester",
                new List<BatchExecuteNewProductProductTypeSelectionDto>
                {
                    new() { DetailGuid = "detail-create-multicode-product", ProductType = 2 },
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ItemNumber == "88842");
            var productSetCodes = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == product.ProductCode)
                .OrderBy(x => x.SetBarcode)
                .ToListAsync();
            var storeMultiCodes = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == product.ProductCode)
                .OrderBy(x => x.StoreCode)
                .OrderBy(x => x.MultiBarcode)
                .ToListAsync();
            var storePriceCount = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == product.ProductCode)
                .CountAsync();
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-create-multicode-product");

            Assert.True(result.Success, $"{result.Code} {result.Message}");
            Assert.Equal(1, result.Data?.CreatedProducts);
            Assert.Equal(2, result.Data?.AddedMultiCodes);
            Assert.Equal(2, product.ProductType);
            Assert.Equal(2, storePriceCount);
            Assert.Equal(new[] { "191554882669", "191554882690" }, productSetCodes.Select(x => x.SetBarcode).ToArray());
            Assert.Equal(4, storeMultiCodes.Count);
            Assert.Equal(new[] { "S01", "S02" }, storeMultiCodes.Select(x => x.StoreCode).Distinct().OrderBy(x => x).ToArray());
            Assert.Equal(99, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_CreateProductWithAdditionalBarcodes_选择套装时仍写入副码关系()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-set-product", "INV-CREATE-SET", new DateTime(2026, 1, 12));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-set-product",
                InvoiceGUID = "invoice-create-set-product",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "SET-88842",
                Barcode = "291554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "291554882690",
                }),
                ProductName = "Set Product With Additional Barcode",
                PurchasePrice = 2.00m,
                RetailPrice = 5.00m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-set-product",
                new List<string> { "detail-create-set-product" },
                "tester",
                new List<BatchExecuteNewProductProductTypeSelectionDto>
                {
                    new() { DetailGuid = "detail-create-set-product", ProductType = 1 },
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ItemNumber == "SET-88842");
            var productSetCodeCount = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == product.ProductCode)
                .CountAsync();
            var storeMultiCodeCount = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == product.ProductCode)
                .CountAsync();

            Assert.True(result.Success, $"{result.Code} {result.Message}");
            Assert.Equal(1, product.ProductType);
            Assert.Equal(1, result.Data?.AddedMultiCodes);
            Assert.Equal(1, productSetCodeCount);
            Assert.Equal(1, storeMultiCodeCount);
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_CreateProductWithAdditionalBarcodes_缺少类型选择时回滚()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-create-missing-type", "INV-CREATE-MISSING-TYPE", new DateTime(2026, 1, 12));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-create-missing-type",
                InvoiceGUID = "invoice-create-missing-type",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "MISSING-TYPE",
                Barcode = "391554882676",
                AdditionalBarcodesJson = JsonSerializer.Serialize(new[]
                {
                    "391554882690",
                }),
                ProductName = "Missing Type Product",
                PurchasePrice = 2.00m,
                RetailPrice = 5.00m,
                ActivityType = (int)DetailAction.CreateProduct,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-create-missing-type",
                new List<string> { "detail-create-missing-type" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.Code);
            var validationDetails = Assert.IsType<BatchExecuteActionsResultDto>(result.Details);
            Assert.Contains(validationDetails.Errors, error => error.Contains("必须选择商品类型", StringComparison.Ordinal));
            Assert.Equal(0, await _db.Queryable<Product>().Where(x => x.ItemNumber == "MISSING-TYPE").CountAsync());
            Assert.Equal(0, await _db.Queryable<ProductSetCode>().CountAsync());
            Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>().CountAsync());
        }

        [Fact]
        public async Task BatchUpdateDetailsAsync_只更新勾选字段并允许False和0持久化()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-batch-edit", "INV-BATCH-EDIT", new DateTime(2026, 1, 13));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-batch-edit",
                InvoiceGUID = "invoice-batch-edit",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductName = "Batch Edit Product",
                Quantity = 3,
                PurchasePrice = 1.50m,
                RetailPrice = 9.99m,
                Amount = 4.50m,
                AutoPricing = true,
                IsSpecialProduct = true,
                DiscountRate = 0.25m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateDetailsAsync(
                "invoice-batch-edit",
                new BatchUpdateInvoiceDetailsRequest
                {
                    Items = new List<InvoiceDetailUpsertItemDto>
                    {
                        new() { DetailGUID = "detail-batch-edit" },
                    },
                    EditFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                        PurchasePrice = 2.00m,
                        UpdateIsAutoPricing = true,
                        IsAutoPricing = false,
                        UpdateIsSpecialProduct = true,
                        IsSpecialProduct = false,
                        UpdateDiscountRate = true,
                        DiscountRate = 0m,
                    },
                },
                "tester"
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-batch-edit");
            var invoice = await _db.Queryable<StoreLocalSupplierInvoice>()
                .SingleAsync(x => x.InvoiceGUID == "invoice-batch-edit");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(2.00m, detail.PurchasePrice);
            Assert.Equal(9.99m, detail.RetailPrice);
            Assert.Equal(6.00m, detail.Amount);
            Assert.False(detail.AutoPricing);
            Assert.False(detail.IsSpecialProduct);
            Assert.Equal(0m, detail.DiscountRate);
            Assert.Equal(6.00m, invoice.TotalAmount);
        }

        [Fact]
        public async Task BatchUpdateDetailsAsync_商品不存在明细_自动定价False改True后刷新保持True()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-batch-edit-missing-product", "INV-BATCH-EDIT-MISSING-PRODUCT", new DateTime(2026, 1, 15));
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-missing-product-auto",
                InvoiceGUID = "invoice-batch-edit-missing-product",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "NEW-001",
                Barcode = "9300000000011",
                ProductName = "Missing Product Auto Pricing",
                Quantity = 1,
                PurchasePrice = 2.00m,
                Amount = 2.00m,
                ExistingProductCount = 0,
                BarcodeStatus = 1,
                BarcodeMatchCount = 1,
                AutoPricing = false,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateDetailsAsync(
                "invoice-batch-edit-missing-product",
                new BatchUpdateInvoiceDetailsRequest
                {
                    Items = new List<InvoiceDetailUpsertItemDto>
                    {
                        new() { DetailGUID = "detail-missing-product-auto" },
                    },
                    EditFields = new UpdateToStorePricesFields
                    {
                        UpdateIsAutoPricing = true,
                        IsAutoPricing = true,
                    },
                },
                "tester"
            );

            var details = await CreateService().GetDetailsAsync("invoice-batch-edit-missing-product");
            var detail = Assert.Single(details.Data!);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(0, detail.ExistingProductCount);
            Assert.True(detail.AutoPricing);
            Assert.Equal(250m, detail.PricingFloatRate);
            Assert.Equal(5.00m, detail.NewAutoRetailPrice);
        }

        [Fact]
        public async Task BatchUpdateDetailsAsync_勾选字段缺少值_返回校验错误且不更新时间戳()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-batch-edit-missing", "INV-BATCH-EDIT-MISSING", new DateTime(2026, 1, 14));
            var originalUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-batch-edit-missing",
                InvoiceGUID = "invoice-batch-edit-missing",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductName = "Batch Edit Missing Value Product",
                Quantity = 3,
                PurchasePrice = 1.50m,
                Amount = 4.50m,
                AutoPricing = true,
                UpdatedAt = originalUpdatedAt,
                UpdatedBy = "seed",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateDetailsAsync(
                "invoice-batch-edit-missing",
                new BatchUpdateInvoiceDetailsRequest
                {
                    Items = new List<InvoiceDetailUpsertItemDto>
                    {
                        new() { DetailGUID = "detail-batch-edit-missing" },
                    },
                    EditFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-batch-edit-missing");

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
            Assert.Equal(1.50m, detail.PurchasePrice);
            Assert.Equal(4.50m, detail.Amount);
            Assert.Equal(originalUpdatedAt, detail.UpdatedAt);
            Assert.Equal("seed", detail.UpdatedBy);
        }

        [Fact]
        public async Task PasteDetailsAsync_新商品默认自动定价为True()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-paste-auto-default", "INV-PASTE-AUTO-DEFAULT", new DateTime(2026, 1, 19));

            var result = await CreateService().PasteDetailsAsync(
                new PasteDetailsRequest
                {
                    InvoiceGuid = "invoice-paste-auto-default",
                    Items = new List<PastedDetailItemDto>
                    {
                        new()
                        {
                            ItemNumber = "PASTE-AUTO-001",
                            Barcode = "9300000000015",
                            ProductName = "Paste Auto Default Product",
                            Quantity = 1,
                            PurchasePrice = 2.00m,
                            RetailPrice = 9.99m,
                        },
                    },
                },
                "tester"
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.InvoiceGUID == "invoice-paste-auto-default");

            Assert.True(result.Success, result.Message);
            Assert.True(detail.AutoPricing);
            Assert.Equal(9.99m, detail.RetailPrice);
        }

        [Fact]
        public async Task PasteDetailsAsync_多条码单元格保存主条码和副条码()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-paste-multi-barcode", "INV-PASTE-MULTI-BARCODE", new DateTime(2026, 1, 20));

            var result = await CreateService().PasteDetailsAsync(
                new PasteDetailsRequest
                {
                    InvoiceGuid = "invoice-paste-multi-barcode",
                    Items = new List<PastedDetailItemDto>
                    {
                        new()
                        {
                            ItemNumber = "88841",
                            Barcode = "191554890459,191554890480,191554890497,191554890473,191554888418",
                            ProductName = "Women Travel Perfume Assorted 35mL",
                            Quantity = 48,
                            PurchasePrice = 1.6546m,
                        },
                    },
                },
                "tester"
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.InvoiceGUID == "invoice-paste-multi-barcode");

            Assert.True(result.Success, result.Message);
            Assert.Equal("191554890459", detail.Barcode);
            Assert.Equal(
                new[]
                {
                    "191554890480",
                    "191554890497",
                    "191554890473",
                    "191554888418",
                },
                JsonSerializer.Deserialize<List<string>>(detail.AdditionalBarcodesJson!)!
            );
            Assert.Equal(79.4208m, detail.Amount);
        }

        [Fact]
        public async Task PasteDetailsAsync_忽略供应商表头行()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-paste-header", "INV-PASTE-HEADER", new DateTime(2026, 1, 21));

            var result = await CreateService().PasteDetailsAsync(
                new PasteDetailsRequest
                {
                    InvoiceGuid = "invoice-paste-header",
                    Items = new List<PastedDetailItemDto>
                    {
                        new()
                        {
                            ItemNumber = "Item No.",
                            Barcode = "Barcode",
                            ProductName = "Description",
                        },
                        new()
                        {
                            ItemNumber = "15085-1xLV5085",
                            Barcode = "840417950853",
                            ProductName = "Women Perfumen New Crystal Absolute",
                            Quantity = 6,
                            PurchasePrice = 2.5000m,
                        },
                    },
                },
                "tester"
            );

            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == "invoice-paste-header")
                .OrderBy(x => x.ItemNumber)
                .ToListAsync();

            Assert.True(result.Success, result.Message);
            Assert.Single(details);
            Assert.Equal("15085-1xLV5085", details[0].ItemNumber);
            Assert.Equal(15.0000m, details[0].Amount);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_更新分店进货价并同步商品主档_不调用Hq商品同步服务()
        {
            await SeedExecutablePriceUpdateAsync();
            var hqSync = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);

            var result = await CreateService(hqSync.Object).UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var product = await _db.Queryable<Product>()
                .FirstAsync(x => x.ProductCode == "P001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal("tester", product.UpdatedBy);
            Assert.Null(typeof(UpdateToStorePricesResultDto).GetProperty("HqSynced"));
            hqSync.Verify(
                x => x.EnsureHqProductsAsync(
                    It.IsAny<string>(),
                    It.IsAny<EnsureHqProductsRequest>(),
                    It.IsAny<string>()
                ),
                Times.Never
            );
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_显式锁等待预算仍使用同一分店更新路径()
        {
            await SeedExecutablePriceUpdateAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = ["detail-price"],
                    TargetStoreCodes = ["S01"],
                    UpdateFields = new UpdateToStorePricesFields { UpdatePurchasePrice = true },
                },
                "tester",
                250
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1, result.Data?.UpdatedPurchasePrices);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_Type1与Type2同键冲突时回滚主成本更新()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Insertable(new[]
            {
                new ProductSetCode
                {
                    SetCodeId = "set-conflict-type1",
                    ProductCode = "P001",
                    SetProductCode = "MC-CONFLICT",
                    SetPurchasePrice = 0.50m,
                    SetRetailPrice = 2m,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                },
                new ProductSetCode
                {
                    SetCodeId = "set-conflict-type2",
                    ProductCode = "P001",
                    SetProductCode = "MC-CONFLICT",
                    SetPurchasePrice = 1.11m,
                    SetRetailPrice = 2m,
                    SetType = 2,
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "store-multi-conflict",
                StoreCode = "S01",
                ProductCode = "P001",
                MultiCodeProductCode = "MC-CONFLICT",
                StoreMultiCodeProductCode = "S01MC-CONFLICT",
                PurchasePrice = 0.75m,
                MultiCodeRetailPrice = 2m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
            var storePrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.UUID == "SRP-001");
            var globalCosts = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == "P001")
                .OrderBy(x => x.SetCodeId)
                .Select(x => x.SetPurchasePrice)
                .ToListAsync();
            var storeCost = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.UUID == "store-multi-conflict")
                .Select(x => x.PurchasePrice)
                .SingleAsync();

            Assert.False(result.Success);
            Assert.Equal("UPDATE_ERROR", result.ErrorCode);
            Assert.Contains("P001", result.Message);
            Assert.Contains("S01", result.Message);
            Assert.Contains("Type1/Type2", result.Message);
            Assert.Equal(1.11m, product.PurchasePrice);
            Assert.Equal(1.11m, storePrice.PurchasePrice);
            Assert.Equal(new decimal?[] { 0.50m, 1.11m }, globalCosts);
            Assert.Equal(0.75m, storeCost);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_目标门店缺失套装投影时自动补齐且不扩展到其他门店()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedActiveSetRelationsAsync("P001", "AUTO");
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "SRP-001-S02",
                StoreCode = "S02",
                ProductCode = "P001",
                StoreProductCode = "S02P001",
                SupplierCode = "SUP01",
                PurchasePrice = 8.88m,
                StoreRetailPriceValue = 9.99m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
            var targetStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.UUID == "SRP-001");
            var targetRelations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P001")
                .OrderBy(x => x.MultiCodeProductCode)
                .ToListAsync();
            var otherStoreRelations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.StoreCode == "S02" && x.ProductCode == "P001")
                .ToListAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal(5.55m, targetStorePrice.PurchasePrice);
            Assert.Equal(2, targetRelations.Count);
            Assert.All(targetRelations, relation => Assert.Equal(5.55m, relation.PurchasePrice));
            Assert.Empty(otherStoreRelations);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_未实际写入的商品不参与套装补齐或重算()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondExecutablePriceUpdateDetailAsync();
            await SeedActiveSetRelationsAsync("P002", "SKIPPED");
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(detail => detail.PurchasePrice == 0m)
                .Where(detail => detail.DetailGUID == "detail-price-2")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price", "detail-price-2" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var skippedProduct = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == "P002");
            var skippedStorePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.UUID == "SRP-002");
            var skippedRelations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P002")
                .ToListAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1, result.Data?.Skipped);
            Assert.Equal(1, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(2.22m, skippedProduct.PurchasePrice);
            Assert.Equal(2.22m, skippedStorePrice.PurchasePrice);
            Assert.Empty(skippedRelations);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_不安全目标组回滚已补齐关系且只报告失败商品()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondExecutablePriceUpdateDetailAsync();
            await SeedActiveSetRelationsAsync("P001", "SAFE");
            await SeedActiveSetRelationsAsync("P002", "UNSAFE");
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "S01-P002-TOMBSTONE",
                StoreCode = "S01",
                ProductCode = "P002",
                MultiCodeProductCode = "UNSAFE-CHILD-T1",
                StoreMultiCodeProductCode = "S01UNSAFE-CHILD-T1",
                MultiBarcode = "UNSAFE-BAR-T1",
                PurchasePrice = 7.77m,
                MultiCodeRetailPrice = 4m,
                IsActive = false,
                IsDeleted = true,
                UpdatedBy = "历史操作人",
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price", "detail-price-2" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var products = await _db.Queryable<Product>()
                .Where(x => new[] { "P001", "P002" }.Contains(x.ProductCode))
                .OrderBy(x => x.ProductCode)
                .ToListAsync();
            var storePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => new[] { "P001", "P002" }.Contains(x.ProductCode))
                .OrderBy(x => x.ProductCode)
                .ToListAsync();
            var safeRelations = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P001")
                .ToListAsync();
            var tombstone = await _db.Queryable<StoreMultiCodeProduct>()
                .SingleAsync(x => x.UUID == "S01-P002-TOMBSTONE");

            Assert.False(result.Success);
            Assert.Equal("UPDATE_ERROR", result.ErrorCode);
            Assert.Contains("P002", result.Message);
            Assert.Contains("S01", result.Message);
            Assert.Contains("停用或软删除", result.Message);
            Assert.DoesNotContain("P001", result.Message);
            Assert.Collection(
                products,
                product => Assert.Equal(1.11m, product.PurchasePrice),
                product => Assert.Equal(2.22m, product.PurchasePrice)
            );
            Assert.Collection(
                storePrices,
                price => Assert.Equal(1.11m, price.PurchasePrice),
                price => Assert.Equal(2.22m, price.PurchasePrice)
            );
            Assert.Empty(safeRelations);
            Assert.False(tombstone.IsActive);
            Assert.True(tombstone.IsDeleted);
            Assert.Equal("历史操作人", tombstone.UpdatedBy);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_零售价为空时_使用新自动零售价更新分店零售价()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.RetailPrice == null)
                .SetColumns(x => x.NewAutoRetailPrice == 6.99m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdateRetailPrice = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");
            var product = await _db.Queryable<Product>()
                .FirstAsync(x => x.ProductCode == "P001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(0, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(6.99m, storePrice.StoreRetailPriceValue);
            Assert.Equal(1.11m, product.PurchasePrice);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_零售价不为空时_优先使用零售价更新分店零售价()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.RetailPrice == 5.55m)
                .SetColumns(x => x.NewAutoRetailPrice == 6.99m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdateRetailPrice = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(5.55m, storePrice.StoreRetailPriceValue);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_大小写不同的重复分店商品只更新一次()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-price-duplicate",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-OLD-DUP",
                ProductName = "Existing Product Duplicate",
                Quantity = 1,
                PurchasePrice = 7.77m,
                ProductCode = "p001",
                StoreProductCode = "S01P001",
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price", "detail-price-duplicate" },
                    TargetStoreCodes = new List<string> { "S01", "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");
            var product = await _db.Queryable<Product>()
                .FirstAsync(x => x.ProductCode == "P001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(7.77m, storePrice.PurchasePrice);
            Assert.Equal(7.77m, product.PurchasePrice);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_数值为0时跳过且不更新时间戳()
        {
            await SeedExecutablePriceUpdateAsync();
            var originalUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(x => x.PurchasePrice == 1.11m)
                .SetColumns(x => x.UpdatedAt == originalUpdatedAt)
                .SetColumns(x => x.UpdatedBy == "seed")
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.PurchasePrice == 0m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");
            var product = await _db.Queryable<Product>()
                .FirstAsync(x => x.ProductCode == "P001");

            Assert.True(result.Success);
            Assert.Equal(0, result.Data?.Updated);
            Assert.Equal(1, result.Data?.Skipped);
            Assert.Equal(0, result.Data?.UpdatedPurchasePrices);
            Assert.Equal(1.11m, storePrice.PurchasePrice);
            Assert.Equal(1.11m, product.PurchasePrice);
            Assert.Equal(originalUpdatedAt, storePrice.UpdatedAt);
            Assert.Equal("seed", storePrice.UpdatedBy);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_布尔False仍然写入()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(x => x.IsAutoPricing == true)
                .SetColumns(x => x.IsSpecialProduct == true)
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.AutoPricing == false)
                .SetColumns(x => x.IsSpecialProduct == false)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdateIsAutoPricing = true,
                        UpdateIsSpecialProduct = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(0, result.Data?.Skipped);
        Assert.False(storePrice.IsAutoPricing);
        Assert.False(storePrice.IsSpecialProduct);
    }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_自动定价为空时按否写入()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(x => x.IsAutoPricing == true)
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.AutoPricing == null)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdateIsAutoPricing = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .FirstAsync(x => x.UUID == "SRP-001");

            Assert.True(result.Success);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(0, result.Data?.Skipped);
            Assert.False(storePrice.IsAutoPricing);
            Assert.DoesNotContain(result.Data?.Errors ?? new List<string>(), error => error.Contains("自动定价为空"));
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_分店价格不存在时按勾选字段新建记录()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Deleteable<StoreRetailPrice>()
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.RetailPrice == 6.66m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                        UpdateRetailPrice = true,
                        UpdateIsAutoPricing = true,
                    },
                },
                "tester"
            );

            var storePrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.StoreCode == "S01" && x.ProductCode == "P001");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Inserted);
            Assert.Equal(0, result.Data?.Updated);
            Assert.Equal(0, result.Data?.Skipped);
            Assert.Equal("S01P001", storePrice.StoreProductCode);
            Assert.Equal("SUP01", storePrice.SupplierCode);
            Assert.Equal(5.55m, storePrice.PurchasePrice);
            Assert.Equal(6.66m, storePrice.StoreRetailPriceValue);
            Assert.False(storePrice.IsAutoPricing);
            Assert.True(storePrice.IsActive);
            Assert.False(storePrice.IsDeleted);
            Assert.Equal("tester", storePrice.CreatedBy);
            Assert.Equal("tester", storePrice.UpdatedBy);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_重复明细缺分店价格时只插入最后一次结果()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Deleteable<StoreRetailPrice>()
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-price-duplicate",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-OLD-DUP",
                ProductName = "Existing Product Duplicate",
                Quantity = 1,
                PurchasePrice = 7.77m,
                ProductCode = "P001",
                StoreProductCode = "S01P001",
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price", "detail-price-duplicate" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var storePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P001")
                .ToListAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Inserted);
            Assert.Equal(0, result.Data?.Updated);
            var storePrice = Assert.Single(storePrices);
            Assert.Equal(7.77m, storePrice.PurchasePrice);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_缺分店价格且无有效字段时不插入并返回跳过原因()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Deleteable<StoreRetailPrice>()
                .Where(x => x.UUID == "SRP-001")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.PurchasePrice == 0m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-execute",
                    DetailGuids = new List<string> { "detail-price" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            var storePriceCount = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P001")
                .CountAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(0, result.Data?.Inserted);
            Assert.Equal(0, result.Data?.Updated);
            Assert.Equal(1, result.Data?.Skipped);
            Assert.Equal(0, storePriceCount);
            Assert.Contains(result.Data?.Errors ?? new List<string>(), error => error.Contains("进货价为空或为0"));
        }

        [Fact]
        public async Task UpdateLastPurchasePricesAsync_选中明细_强制覆盖已有上次进货价()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondExecutablePriceUpdateDetailAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.LastPurchasePrice == 9.99m)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.LastPurchasePrice == 8.88m)
                .Where(x => x.DetailGUID == "detail-price-2")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateLastPurchasePricesAsync(
                "invoice-execute",
                new UpdateLastPurchasePricesRequest
                {
                    DetailGuids = new List<string> { "detail-price" },
                },
                "tester"
            );

            var first = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price");
            var second = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price-2");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Total);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1.11m, first.LastPurchasePrice);
            Assert.Equal(8.88m, second.LastPurchasePrice);
            Assert.Equal("tester", first.UpdatedBy);
        }

        [Fact]
        public async Task UpdateLastPurchasePricesAsync_未传明细_刷新整张单并在分店价缺失时回退商品主档()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondExecutablePriceUpdateDetailAsync();
            await _db.Deleteable<StoreRetailPrice>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "P002")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateLastPurchasePricesAsync(
                "invoice-execute",
                new UpdateLastPurchasePricesRequest(),
                "tester"
            );

            var first = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price");
            var second = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price-2");

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data?.Total);
            Assert.Equal(2, result.Data?.Updated);
            Assert.Equal(1.11m, first.LastPurchasePrice);
            Assert.Equal(2.22m, second.LastPurchasePrice);
        }

        [Fact]
        public async Task UpdateLastPurchasePricesAsync_明细分店为空白时_使用单头分店价()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.StoreCode == "")
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();
            await _db.Updateable<Product>()
                .SetColumns(x => x.PurchasePrice == 9.99m)
                .Where(x => x.ProductCode == "P001")
                .ExecuteCommandAsync();

            var result = await CreateService().UpdateLastPurchasePricesAsync(
                "invoice-execute",
                new UpdateLastPurchasePricesRequest
                {
                    DetailGuids = new List<string> { "detail-price" },
                },
                "tester"
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .SingleAsync(x => x.DetailGUID == "detail-price");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1.11m, detail.LastPurchasePrice);
        }

        [Fact]
        public async Task UpdateLastPurchasePricesAsync_选中明细不存在时_返回跳过原因()
        {
            await SeedExecutablePriceUpdateAsync();

            var result = await CreateService().UpdateLastPurchasePricesAsync(
                "invoice-execute",
                new UpdateLastPurchasePricesRequest
                {
                    DetailGuids = new List<string> { "detail-price", "detail-missing" },
                },
                "tester"
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data?.Total);
            Assert.Equal(1, result.Data?.Updated);
            Assert.Equal(1, result.Data?.Skipped);
            Assert.Contains(result.Data?.Errors ?? new List<string>(), error => error.Contains("明细不存在或不属于当前单据"));
        }

        [Fact]
        public async Task UpdateLastPurchasePricesAsync_缺商品编码或无有效价格时跳过并返回原因()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.ProductCode == null)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                UUID = "UUID-NO-PRICE",
                ProductCode = "P-NO-PRICE",
                ItemNumber = "ITEM-NO-PRICE",
                Barcode = "BAR-NO-PRICE",
                ProductName = "No Price Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 0m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-no-price",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-NO-PRICE",
                Barcode = "BAR-NO-PRICE",
                ProductName = "No Price Product",
                ProductCode = "P-NO-PRICE",
                PurchasePrice = 1m,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateLastPurchasePricesAsync(
                "invoice-execute",
                new UpdateLastPurchasePricesRequest(),
                "tester"
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data?.Total);
            Assert.Equal(0, result.Data?.Updated);
            Assert.Equal(2, result.Data?.Skipped);
            Assert.Contains(result.Data?.Errors ?? new List<string>(), error => error.Contains("未找到商品编码"));
            Assert.Contains(result.Data?.Errors ?? new List<string>(), error => error.Contains("未找到有效上次进货价"));
        }

        [Fact]
        public async Task UpdateDetailActionAsync_WhenDetailAlreadyCompleted_RejectsChange()
        {
            await SeedExecutablePriceUpdateAsync();
            await _db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.ActivityType == 99)
                .Where(x => x.DetailGUID == "detail-price")
                .ExecuteCommandAsync();

            var singleResult = await CreateService().UpdateDetailActionAsync(
                "invoice-execute",
                "detail-price",
                (int)DetailAction.UpdatePurchasePrice
            );
            var batchResult = await CreateService().BatchUpdateDetailActionAsync(
                "invoice-execute",
                new BatchUpdateDetailActionRequest
                {
                    DetailGuids = new List<string> { "detail-price" },
                    Action = (int)DetailAction.UpdatePurchasePrice,
                }
            );

            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .FirstAsync(x => x.DetailGUID == "detail-price");

            Assert.False(singleResult.Success);
            Assert.Equal("VALIDATION_ERROR", singleResult.Code);
            Assert.False(batchResult.Success);
            Assert.Equal("VALIDATION_ERROR", batchResult.Code);
            Assert.Equal(99, detail.ActivityType);
        }

        [Fact]
        public async Task BatchExecuteActions_非全店访问用户_禁止执行会扩散到全店的批量操作()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondActiveStoreAndScopedUserAsync();
            var controller = CreateController(
                new Claim(ClaimTypes.Name, "scoped-user"),
                new Claim("userId", "user-scoped")
            );

            var actionResult = await controller.BatchExecuteActions(
                "invoice-execute",
                new BatchExecuteActionsRequestDto
                {
                    DetailGuids = new List<string> { "detail-price" },
                    ExpectedActions = new List<BatchExecuteExpectedActionDto>
                    {
                        new()
                        {
                            DetailGuid = "detail-price",
                            ActivityType = (int)DetailAction.UpdatePurchasePrice,
                        },
                    },
                    ConfirmedCreateProductCount = 0,
                    ConfirmedAt = DateTime.UtcNow,
                }
            );

            Assert.IsType<ForbidResult>(actionResult);
        }

        [Fact]
        public async Task BatchUpdateDetailAction_非全店访问用户_禁止设置批量执行动作()
        {
            await SeedExecutablePriceUpdateAsync();
            await SeedSecondActiveStoreAndScopedUserAsync();
            var controller = CreateController(
                new Claim(ClaimTypes.Name, "scoped-user"),
                new Claim("userId", "user-scoped")
            );

            var actionResult = await controller.BatchUpdateDetailAction(
                "invoice-execute",
                new BatchUpdateDetailActionRequest
                {
                    DetailGuids = new List<string> { "detail-price" },
                    Action = (int)DetailAction.UpdatePurchasePrice,
                }
            );

            Assert.IsType<ForbidResult>(actionResult);
        }

        [Fact]
        public async Task PasteDetailsJobEndpoints_创建和查询任务委托后台服务()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-job", "INV-JOB", new DateTime(2026, 1, 20));
            var jobService = new Mock<ILocalSupplierInvoiceBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartPasteDetailsJobAsync(
                    It.Is<PasteDetailsRequest>(request =>
                        request.InvoiceGuid == "invoice-job"
                        && request.Mode == "append"
                        && request.Items.Count == 1
                    ),
                    "tester",
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new LocalSupplierInvoicePasteDetailsJobDto
                {
                    JobId = "paste-job-1",
                    InvoiceGuid = "invoice-job",
                    OperationId = "paste|invoice-job",
                    Status = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running,
                });
            jobService
                .Setup(service => service.GetPasteDetailsJobAsync("paste-job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LocalSupplierInvoicePasteDetailsJobDto
                {
                    JobId = "paste-job-1",
                    InvoiceGuid = "invoice-job",
                    OperationId = "paste|invoice-job",
                    Status = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded,
                    Result = new BatchResultDto { Inserted = 1 },
                });

            var controller = CreateControllerWithJobService(jobService.Object);

            var createResult = await controller.StartPasteDetailsJob(
                "invoice-job",
                new PasteDetailsRequest
                {
                    Mode = "append",
                    Items = new List<PastedDetailItemDto> { new() { ItemNumber = "ITEM-1" } },
                },
                CancellationToken.None
            );
            var getResult = await controller.GetPasteDetailsJob(
                "invoice-job",
                "paste-job-1",
                CancellationToken.None
            );

            Assert.IsType<OkObjectResult>(createResult);
            Assert.IsType<OkObjectResult>(getResult);
            jobService.VerifyAll();
        }

        [Fact]
        public async Task UpdateHqProductsJobEndpoints_提交时固化当前审计操作者()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-hq-job", "INV-HQ-JOB", new DateTime(2026, 1, 20));
            var jobService = new Mock<ILocalSupplierInvoiceBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartUpdateHqProductsJobAsync(
                    "invoice-hq-job",
                    It.Is<UpdateHqProductsRequest>(request =>
                        request.DetailGuids.SequenceEqual(new[] { "detail-1" })
                        && request.TargetStoreCodes.SequenceEqual(new[] { "S01" })
                    ),
                    "actor-guid-controller",
                    "后台审计操作员",
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new LocalSupplierInvoiceUpdateHqProductsJobDto
                {
                    JobId = "hq-job-1",
                    InvoiceGuid = "invoice-hq-job",
                    OperationId = "update-hq-products|invoice-hq-job",
                    Status = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running,
                });
            var controller = CreateControllerWithJobService(
                jobService.Object,
                new Claim(ClaimTypes.Name, "后台审计操作员"),
                new Claim("userId", "actor-guid-controller"),
                new Claim(ClaimTypes.Role, "Admin")
            );

            var actionResult = await controller.StartUpdateHqProductsJob(
                "invoice-hq-job",
                new UpdateHqProductsRequest
                {
                    DetailGuids = new List<string> { "detail-1" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields { UpdatePurchasePrice = true },
                },
                CancellationToken.None
            );

            Assert.IsType<OkObjectResult>(actionResult);
            jobService.VerifyAll();
        }

        [Fact]
        public async Task CheckProductsJobEndpoints_创建和查询任务委托后台服务()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-job", "INV-JOB", new DateTime(2026, 1, 20));
            var jobService = new Mock<ILocalSupplierInvoiceBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartCheckProductsJobAsync(
                    It.Is<CheckProductsRequest>(request =>
                        request.InvoiceGuid == "invoice-job"
                        && request.DetailGuids != null
                        && request.DetailGuids.SequenceEqual(new[] { "detail-1" })
                    ),
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new LocalSupplierInvoiceCheckProductsJobDto
                {
                    JobId = "check-job-1",
                    InvoiceGuid = "invoice-job",
                    OperationId = "check-products|invoice-job",
                    Status = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running,
                });
            jobService
                .Setup(service => service.GetCheckProductsJobAsync("check-job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LocalSupplierInvoiceCheckProductsJobDto
                {
                    JobId = "check-job-1",
                    InvoiceGuid = "invoice-job",
                    OperationId = "check-products|invoice-job",
                    Status = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded,
                    Result = new CheckProductsResponseDto
                    {
                        Summary = new CheckProductsSummaryDto { Total = 1 },
                    },
                });

            var controller = CreateControllerWithJobService(jobService.Object);

            var createResult = await controller.StartCheckProductsJob(
                new CheckProductsRequest
                {
                    InvoiceGuid = "invoice-job",
                    DetailGuids = new List<string> { "detail-1" },
                },
                CancellationToken.None
            );
            var getResult = await controller.GetCheckProductsJob(
                "invoice-job",
                "check-job-1",
                CancellationToken.None
            );

            Assert.IsType<OkObjectResult>(createResult);
            Assert.IsType<OkObjectResult>(getResult);
            jobService.VerifyAll();
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
        public async Task BatchExecuteActionsAsync_新增Type2多码成本以父商品重算而非明细成本()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-type2-cost", "INV-TYPE2-COST", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-type2-cost",
                ProductCode = "P-TYPE2-COST",
                ItemNumber = "TYPE2-COST",
                Barcode = "900000000001",
                ProductName = "Type2 Cost Parent",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 0m,
                RetailPrice = 10m,
                ProductType = 0,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-TYPE2-COST",
                ImportPrice = 2m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "store-price-type2-cost",
                StoreCode = "S01",
                ProductCode = "P-TYPE2-COST",
                StoreProductCode = "S01P-TYPE2-COST",
                SupplierCode = "SUP01",
                PurchasePrice = 0m,
                StoreRetailPriceValue = 10m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-type2-cost",
                InvoiceGUID = "invoice-type2-cost",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-TYPE2-COST",
                ItemNumber = "TYPE2-COST",
                Barcode = "900000000002",
                PurchasePrice = 99m,
                RetailPrice = 10m,
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                ActivityType = (int)DetailAction.AddMultiCode,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                "invoice-type2-cost",
                new List<string> { "detail-type2-cost" },
                "tester"
            );

            var global = await _db.Queryable<ProductSetCode>()
                .SingleAsync(x => x.ProductCode == "P-TYPE2-COST");
            var store = await _db.Queryable<StoreMultiCodeProduct>()
                .SingleAsync(x => x.ProductCode == "P-TYPE2-COST" && x.StoreCode == "S01");

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, global.SetType);
            // Product 与门店主成本均为 0 时，必须继续使用 Warehouse.ImportPrice，不能采用明细提交成本。
            Assert.Equal(2m, global.SetPurchasePrice);
            Assert.Equal(2m, store.PurchasePrice);
        }

        [Fact]
        public async Task UpdateDetailsToStorePricesAsync_重算全局与精确门店成本且保留门店差异()
        {
            await SeedStoreAndSupplierAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-type2-s02",
                StoreCode = "S02",
                StoreName = "Store 2",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await InsertInvoiceAsync("invoice-store-cost", "INV-STORE-COST", new DateTime(2026, 1, 12));
            await _db.Insertable(new Product
            {
                UUID = "product-store-cost",
                ProductCode = "P-STORE-COST",
                ItemNumber = "STORE-COST",
                Barcode = "910000000001",
                ProductName = "Store Cost Parent",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 4m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new StoreRetailPrice
                {
                    UUID = "store-price-cost-s01",
                    StoreCode = "S01",
                    ProductCode = "P-STORE-COST",
                    StoreProductCode = "S01P-STORE-COST",
                    SupplierCode = "SUP01",
                    PurchasePrice = 5m,
                    StoreRetailPriceValue = 10m,
                    IsActive = true,
                    IsDeleted = false,
                },
                new StoreRetailPrice
                {
                    UUID = "store-price-cost-s02",
                    StoreCode = "S02",
                    ProductCode = "P-STORE-COST",
                    StoreProductCode = "S02P-STORE-COST",
                    SupplierCode = "SUP01",
                    PurchasePrice = 8m,
                    StoreRetailPriceValue = 10m,
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "set-type2-store-cost",
                ProductCode = "P-STORE-COST",
                SetProductCode = "MC-STORE-COST",
                SetBarcode = "910000000002",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateType2StoreMultiCode("store-multi-cost-s01", "S01", "P-STORE-COST", "MC-STORE-COST", 99m),
                CreateType2StoreMultiCode("store-multi-cost-s02", "S02", "P-STORE-COST", "MC-STORE-COST", 99m),
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-store-cost",
                InvoiceGUID = "invoice-store-cost",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = "P-STORE-COST",
                RetailPrice = 12m,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().UpdateDetailsToStorePricesAsync(
                new UpdateToStorePricesRequest
                {
                    InvoiceGuid = "invoice-store-cost",
                    DetailGuids = new List<string> { "detail-store-cost" },
                    TargetStoreCodes = new List<string> { "S01", "S02" },
                    UpdateFields = new UpdateToStorePricesFields { UpdateRetailPrice = true },
                },
                "tester"
            );

            var global = await _db.Queryable<ProductSetCode>()
                .SingleAsync(x => x.SetCodeId == "set-type2-store-cost");
            var storeCosts = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == "P-STORE-COST")
                .OrderBy(x => x.StoreCode)
                .ToListAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(4m, global.SetPurchasePrice);
            Assert.Equal(
                new[] { 5m, 8m },
                storeCosts.Select(x => x.PurchasePrice.GetValueOrDefault()).ToArray()
            );
        }

        private static StoreMultiCodeProduct CreateType2StoreMultiCode(
            string uuid,
            string storeCode,
            string productCode,
            string multiCodeProductCode,
            decimal purchasePrice
        ) => new()
        {
            UUID = uuid,
            StoreCode = storeCode,
            ProductCode = productCode,
            MultiCodeProductCode = multiCodeProductCode,
            StoreMultiCodeProductCode = storeCode + multiCodeProductCode,
            MultiBarcode = multiCodeProductCode + "-BARCODE",
            PurchasePrice = purchasePrice,
            MultiCodeRetailPrice = 10m,
            IsActive = true,
            IsDeleted = false,
        };

        private async Task SeedActiveSetRelationsAsync(string productCode, string prefix)
        {
            await _db.Insertable(new[]
            {
                new ProductSetCode
                {
                    SetCodeId = $"{prefix}-TYPE1",
                    ProductCode = productCode,
                    SetProductCode = $"{prefix}-CHILD-T1",
                    SetBarcode = $"{prefix}-BAR-T1",
                    SetPurchasePrice = 99m,
                    SetRetailPrice = 4m,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                },
                new ProductSetCode
                {
                    SetCodeId = $"{prefix}-TYPE2",
                    ProductCode = productCode,
                    SetProductCode = $"{prefix}-CHILD-T2",
                    SetBarcode = $"{prefix}-BAR-T2",
                    SetPurchasePrice = 99m,
                    SetRetailPrice = 5m,
                    SetType = 2,
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
        }

        private async Task SeedStoreAndSupplierAsync()
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
        }

        private async Task InsertInvoiceAsync(string invoiceGuid, string invoiceNo, DateTime orderDate)
        {
            await _db.Insertable(new StoreLocalSupplierInvoice
            {
                InvoiceGUID = invoiceGuid,
                StoreCode = "S01",
                SupplierCode = "SUP01",
                InvoiceNo = invoiceNo,
                OrderDate = orderDate,
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedExecutablePriceUpdateAsync()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-execute", "INV-EXEC", new DateTime(2026, 1, 10));
            await _db.Insertable(new Product
            {
                UUID = "UUID-DIFFERENT",
                ProductCode = "P001",
                ItemNumber = "ITEM-OLD",
                Barcode = "BAR-OLD",
                ProductName = "Existing Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 1.11m,
                RetailPrice = 2.22m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "SRP-001",
                StoreCode = "S01",
                ProductCode = "P001",
                StoreProductCode = "S01P001",
                SupplierCode = "SUP01",
                PurchasePrice = 1.11m,
                StoreRetailPriceValue = 2.22m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-price",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-OLD",
                Barcode = "BAR-CONFLICT",
                ProductName = "Existing Product",
                Quantity = 1,
                PurchasePrice = 5.55m,
                ProductCode = "P001",
                StoreProductCode = "S01P001",
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedSecondExecutablePriceUpdateDetailAsync()
        {
            await _db.Insertable(new Product
            {
                UUID = "UUID-DIFFERENT-2",
                ProductCode = "P002",
                ItemNumber = "ITEM-OLD-2",
                Barcode = "BAR-OLD-2",
                ProductName = "Second Existing Product",
                LocalSupplierCode = "SUP01",
                PurchasePrice = 2.22m,
                RetailPrice = 3.33m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = "SRP-002",
                StoreCode = "S01",
                ProductCode = "P002",
                StoreProductCode = "S01P002",
                SupplierCode = "SUP01",
                PurchasePrice = 2.22m,
                StoreRetailPriceValue = 3.33m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "detail-price-2",
                InvoiceGUID = "invoice-execute",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ItemNumber = "ITEM-OLD-2",
                Barcode = "BAR-CONFLICT-2",
                ProductName = "Second Existing Product",
                Quantity = 1,
                PurchasePrice = 6.66m,
                ProductCode = "P002",
                StoreProductCode = "S01P002",
                ExistingProductCount = 1,
                BarcodeStatus = 2,
                ActivityType = (int)DetailAction.UpdatePurchasePrice,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedExecutableItemNumberUpdatesAsync()
        {
            await SeedStoreAndSupplierAsync();
            await InsertInvoiceAsync("invoice-item-update", "INV-ITEM-UPD", new DateTime(2026, 1, 12));
            await _db.Insertable(new List<Product>
            {
                new()
                {
                    UUID = "UUID-ITEM-1",
                    ProductCode = "PITEM1",
                    ItemNumber = "ITEM-OLD-1",
                    Barcode = "BAR-ITEM-1",
                    ProductName = "Item Product 1",
                    LocalSupplierCode = "SUP01",
                    PurchasePrice = 1.11m,
                    RetailPrice = 2.22m,
                    IsDeleted = false,
                },
                new()
                {
                    UUID = "UUID-ITEM-2",
                    ProductCode = "PITEM2",
                    ItemNumber = "ITEM-OLD-2",
                    Barcode = "BAR-ITEM-2",
                    ProductName = "Item Product 2",
                    LocalSupplierCode = "SUP01",
                    PurchasePrice = 1.22m,
                    RetailPrice = 2.44m,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                new()
                {
                    DetailGUID = "detail-item-1",
                    InvoiceGUID = "invoice-item-update",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "ITEM-NEW-1",
                    Barcode = "BAR-ITEM-1",
                    ProductName = "Item Product 1",
                    Quantity = 1,
                    PurchasePrice = 1.11m,
                    ProductCode = "PITEM1",
                    ExistingProductCount = 1,
                    BarcodeStatus = 1,
                    ActivityType = (int)DetailAction.UpdateItemNumber,
                    IsDeleted = false,
                },
                new()
                {
                    DetailGUID = "detail-item-2",
                    InvoiceGUID = "invoice-item-update",
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "ITEM-NEW-2",
                    Barcode = "BAR-ITEM-2",
                    ProductName = "Item Product 2",
                    Quantity = 1,
                    PurchasePrice = 1.22m,
                    ProductCode = "PITEM2",
                    ExistingProductCount = 1,
                    BarcodeStatus = 1,
                    ActivityType = (int)DetailAction.UpdateItemNumber,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
        }

        private LocalSupplierInvoicesReactService CreateService(
            ILocalSupplierInvoiceHqProductSyncService? hqProductSyncService = null,
            IWarehouseProductChangeHistoryService? changeHistoryService = null
        )
        {
            var autoPricing = new Mock<IAutoPricingService>();
            autoPricing.Setup(x => x.GetAllActiveStrategiesAsync())
                .ReturnsAsync(new List<BlazorApp.Shared.Models.HBweb.PricingStrategy>());
            autoPricing.Setup(x => x.FindStrategyForPriceAsync(It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((BlazorApp.Shared.Models.HBweb.PricingStrategy?)null);
            autoPricing.Setup(x => x.CalculateRate(It.IsAny<decimal>(), It.IsAny<BlazorApp.Shared.Models.HBweb.PricingStrategy?>()))
                .Returns(250m);
            autoPricing.Setup(x => x.CalculateRetailPrice(It.IsAny<decimal>(), It.IsAny<BlazorApp.Shared.Models.HBweb.PricingStrategy?>()))
                .Returns<decimal, BlazorApp.Shared.Models.HBweb.PricingStrategy?>((price, _) => price * 2.5m);

            return new LocalSupplierInvoicesReactService(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                autoPricing.Object,
                changeHistoryService ?? WarehouseProductChangeHistoryTestDouble.CreateNoop(),
                hqProductSyncService
            );
        }

        private static Mock<IWarehouseProductChangeHistoryService> CreateSnapshotHistoryMock()
        {
            var history = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
            history
                .Setup(x =>
                    x.CaptureSnapshotsAsync(
                        It.IsAny<IEnumerable<string>>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(
                    (IEnumerable<string> codes, CancellationToken _) =>
                        (IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>)codes
                            .Distinct(StringComparer.Ordinal)
                            .ToDictionary(
                                code => code,
                                code => new WarehouseProductChangeSnapshotDto
                                {
                                    ProductCode = code,
                                },
                                StringComparer.Ordinal
                            )
                );
            return history;
        }

        private ReactLocalSupplierInvoicesController CreateController(params Claim[] claims)
        {
            return CreateControllerCore(null, claims);
        }

        private ReactLocalSupplierInvoicesController CreateControllerWithJobService(
            ILocalSupplierInvoiceBatchUpdateJobService batchUpdateJobService,
            params Claim[] claims
        )
        {
            return CreateControllerCore(batchUpdateJobService, claims);
        }

        private ReactLocalSupplierInvoicesController CreateControllerCore(
            ILocalSupplierInvoiceBatchUpdateJobService? batchUpdateJobService,
            Claim[] claims
        )
        {
            var controller = new ReactLocalSupplierInvoicesController(
                CreateService(),
                CreateSqlSugarContext(_db),
                Mock.Of<ILocalSupplierInvoiceHqSyncService>(),
                Mock.Of<ILocalSupplierInvoiceImportService>(),
                null,
                batchUpdateJobService
            );

            var effectiveClaims = claims.Length > 0
                ? claims
                : new[]
                {
                    new Claim(ClaimTypes.Name, "tester"),
                    new Claim(ClaimTypes.Role, "Admin"),
                };

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            effectiveClaims,
                            "TestAuth"
                        )
                    ),
                },
            };

            return controller;
        }

        private async Task SeedSecondActiveStoreAndScopedUserAsync()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-guid-2",
                StoreCode = "S02",
                StoreName = "Melbourne Store",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new UserStore
            {
                UserStoreGUID = "user-store-scoped-1",
                UserGUID = "user-scoped",
                StoreGUID = "store-guid-1",
                IsPrimary = true,
            }).ExecuteCommandAsync();
        }

        private static T ReadAnonymousProperty<T>(object value, string propertyName)
        {
            var property = value.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException($"未找到属性 {propertyName}");
            return (T)property.GetValue(value)!;
        }

        private static string ResolveLocalSupplierInvoicesProductExecutionCommandWriterPath(
            [CallerFilePath] string testFilePath = ""
        )
        {
            var testDirectory = Path.GetDirectoryName(testFilePath)
                ?? throw new InvalidOperationException("无法解析测试文件目录");
            return Path.GetFullPath(
                Path.Combine(
                    testDirectory,
                    "..",
                    "BlazorApp.Api",
                    "Features",
                    "LocalSupplierInvoices",
                    "ProductExecution",
                    "LocalSupplierInvoicesProductExecutionCommandWriter.cs"
                )
            );
        }

        private static string ResolveLocalSupplierInvoicesProductExecutionPlanPath(
            [CallerFilePath] string testFilePath = ""
        )
        {
            var testDirectory = Path.GetDirectoryName(testFilePath)!;
            return Path.GetFullPath(
                Path.Combine(
                    testDirectory,
                    "..",
                    "BlazorApp.Api",
                    "Features",
                    "LocalSupplierInvoices",
                    "ProductExecution",
                    "LocalSupplierInvoicesProductExecutionPlan.cs"
                )
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
            );
            dbField!.SetValue(context, db);

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
            );
            dbField!.SetValue(context, new Mock<ISqlSugarClient>().Object);

            return context;
        }
    }
}
