using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
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
    public sealed class LocationReactServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocationReactServiceTests()
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
            });

            _db.CodeFirst.InitTables(
                typeof(Location),
                typeof(ProductLocation),
                typeof(Product)
            );
        }

        [Fact]
        public void StatusFilters_SQLServer方言不得生成空串比较以免排除状态码0()
        {
            // SQLite 里 0 <> '' 为真，行为测试抓不到；线上 SQL Server 会把 '' 转成 0，
            // 写成 Status.HasValue && ... 时 SqlSugar 生成的 [Status]<>'' 会把「禁用(0)」排除。
            var db = new SqlSugarClient(
                new ConnectionConfig
                {
                    DbType = DbType.SqlServer,
                    ConnectionString = "Server=127.0.0.1;Database=x;User Id=x;Password=x;",
                    IsAutoCloseConnection = true,
                }
            );

            var equalsResult = LocationReactService
                .ApplyStatusEqualsFilter(db.Queryable<Location>(), 0)
                .ToSql();
            var equalsSql = equalsResult.Key;
            var inSql = LocationReactService
                .ApplyNullableIntFilter(db.Queryable<Location>(), "status", new[] { "0", "1" })
                .ToSql()
                .Key;

            Assert.Contains("[Status] = @Status0", equalsSql);
            Assert.Equal(0, Assert.Single(equalsResult.Value).Value);
            Assert.DoesNotContain("<>''", equalsSql);
            Assert.Contains("[Status] IN (0,1)", inSql);
            Assert.DoesNotContain("<>''", inSql);
        }

        [Fact]
        public async Task LookupAsync_StillReturnsLocationWhenKeywordMatchesLocationCode()
        {
            await SeedBoundProductLocationAsync();
            var service = CreateService();

            var result = await service.LookupAsync("B-03-04");

            var item = Assert.Single(result);
            Assert.Equal("loc-001", item.LocationGuid);
            Assert.Equal("B-03-04-07", item.LocationCode);
            Assert.Equal(1, item.ProductCount);
        }

        [Fact]
        public async Task LookupAsync_ReturnsBoundLocationWhenKeywordMatchesProductItemNumber()
        {
            await SeedBoundProductLocationAsync();
            var service = CreateService();

            var result = await service.LookupAsync("HB-246-PP-053");

            var item = Assert.Single(result);
            Assert.Equal("loc-001", item.LocationGuid);
            Assert.Equal("B-03-04-07", item.LocationCode);
            Assert.Equal(1, item.ProductCount);
        }

        [Fact]
        public async Task LookupAsync_ReturnsBoundLocationWhenKeywordMatchesProductBarcode()
        {
            await SeedBoundProductLocationAsync();
            var service = CreateService();

            var result = await service.LookupAsync("9525812461024");

            var item = Assert.Single(result);
            Assert.Equal("loc-001", item.LocationGuid);
            Assert.Equal("B-03-04-07", item.LocationCode);
            Assert.Equal(1, item.ProductCount);
        }

        [Fact]
        public async Task LookupAsync_ReturnsDistinctLocationsBeforeTakingProductMatches()
        {
            await SeedDuplicateProductMatchesAcrossLocationsAsync();
            var service = CreateService();

            var result = await service.LookupAsync("MATCH-ITEM");

            Assert.Equal(2, result.Count);
            Assert.Contains(result, item => item.LocationGuid == "loc-many");
            Assert.Contains(result, item => item.LocationGuid == "loc-other");
        }

        [Fact]
        public async Task GetPagedListAsync_SortsByLocationCodeInRequestedDirection()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var ascending = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "LocationCode",
                SortDirection = "asc",
            });
            var descending = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "LocationCode",
                SortDirection = "desc",
            });

            Assert.Equal(new[] { "A-01-01-01", "B-01-01-01", "C-01-01-01" }, ascending.Items.Select(item => item.LocationCode));
            Assert.Equal(new[] { "C-01-01-01", "B-01-01-01", "A-01-01-01" }, descending.Items.Select(item => item.LocationCode));
        }

        [Fact]
        public async Task GetPagedListAsync_SortsByUpdatedByAndUpdatedAt()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var byUpdater = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "UpdatedBy",
                SortDirection = "asc",
            });
            var byUpdatedAt = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "UpdatedAt",
                SortDirection = "desc",
            });

            Assert.Equal(new[] { "A-01-01-01", "B-01-01-01", "C-01-01-01" }, byUpdater.Items.Select(item => item.LocationCode));
            Assert.Equal(new[] { "C-01-01-01", "B-01-01-01", "A-01-01-01" }, byUpdatedAt.Items.Select(item => item.LocationCode));
        }

        [Fact]
        public async Task GetPagedListAsync_FiltersUsedStatusWithDatabaseSubquery()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var used = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = true,
            });
            var unused = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = false,
            });

            Assert.Equal(new[] { "B-01-01-01" }, used.Items.Select(item => item.LocationCode));
            Assert.Equal(new[] { "A-01-01-01", "C-01-01-01" }, unused.Items.Select(item => item.LocationCode));
        }

        [Fact]
        public async Task GetPagedListAsync_SortsByUsageStatus()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var usageDescending = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "Usage",
                SortDirection = "desc",
            });

            Assert.Equal("B-01-01-01", usageDescending.Items.First().LocationCode);
            Assert.Single(usageDescending.Items.First().Products);
        }

        [Fact]
        public async Task GetPagedListAsync_UsageIgnoresDeletedProducts()
        {
            await SeedPagedLocationsAsync();
            await SeedDeletedProductOnlyLocationAsync();
            var service = CreateService();

            var used = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = true,
                SortBy = "LocationCode",
                SortDirection = "asc",
            });
            var unused = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = false,
                SortBy = "LocationCode",
                SortDirection = "asc",
            });
            var usageDescending = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = "Usage",
                SortDirection = "desc",
            });

            Assert.Equal(new[] { "B-01-01-01" }, used.Items.Select(item => item.LocationCode));
            Assert.Contains(unused.Items, item => item.LocationCode == "D-01-01-01");
            Assert.DoesNotContain(usageDescending.Items.Take(1), item => item.LocationCode == "D-01-01-01");
        }

        [Fact]
        public async Task GetPagedListAsync_BatchLoadsProductsForCurrentPage()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var page = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 2,
                SortBy = "LocationCode",
                SortDirection = "asc",
            });

            Assert.Equal(3, page.Total);
            Assert.Equal(2, page.Items.Count);
            Assert.Empty(page.Items[0].Products);
            var boundLocation = Assert.Single(page.Items[1].Products);
            Assert.Equal("P-PAGED-001", boundLocation.ProductCode);
            Assert.Equal("HB-PAGED-001", boundLocation.ItemNumber);
            Assert.Equal("9320000000011", boundLocation.Barcode);
            Assert.Equal("Paged product", boundLocation.ProductName);
            Assert.Equal(12, boundLocation.MiddlePackageQuantity);
        }

        [Fact]
        public async Task GetPagedListAsync_ProductColumnFiltersApplyBeforePaging()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var page = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 1,
                SortBy = "LocationCode",
                SortDirection = "asc",
                Filters = new Dictionary<string, string[]>
                {
                    ["productItemNumber"] = new[] { "__filter:eq:HB-PAGED-001" },
                },
            });

            Assert.Equal(1, page.Total);
            var item = Assert.Single(page.Items);
            Assert.Equal("B-01-01-01", item.LocationCode);
            Assert.Equal("HB-PAGED-001", Assert.Single(item.Products).ItemNumber);
        }

        [Fact]
        public async Task GetPagedListAsync_ProductColumnFiltersIgnoreDeletedProductLinks()
        {
            await SeedBoundProductLocationAsync();
            var service = CreateService();

            var byDeletedProduct = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                Filters = new Dictionary<string, string[]>
                {
                    ["productBarcode"] = new[] { "__filter:eq:DELETED-BARCODE" },
                },
            });
            var byDeletedLink = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                Filters = new Dictionary<string, string[]>
                {
                    ["productBarcode"] = new[] { "__filter:eq:DELETED-LINK-BARCODE" },
                },
            });

            Assert.Equal(0, byDeletedProduct.Total);
            Assert.Empty(byDeletedProduct.Items);
            Assert.Equal(0, byDeletedLink.Total);
            Assert.Empty(byDeletedLink.Items);
        }

        [Fact]
        public async Task GetPagedListAsync_LocationAndProductColumnFiltersUseAndSemantics()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var matching = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationCode"] = new[] { "__filter:starts:B-" },
                    ["productName"] = new[] { "__filter:contains:Paged" },
                },
            });
            var mismatching = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationCode"] = new[] { "__filter:starts:A-" },
                    ["productName"] = new[] { "__filter:contains:Paged" },
                },
            });

            Assert.Equal(new[] { "B-01-01-01" }, matching.Items.Select(item => item.LocationCode));
            Assert.Equal(0, mismatching.Total);
            Assert.Empty(mismatching.Items);
        }

        [Fact]
        public async Task GetPagedListAsync_LocationTypeColumnFilterUsesStorageValue()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var page = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationType"] = new[] { "2" },
                },
            });

            Assert.Equal(new[] { "A-01-01-01" }, page.Items.Select(item => item.LocationCode));
        }

        [Fact]
        public async Task GetPagedListAsync_UsageAndProductColumnFiltersCombine()
        {
            await SeedPagedLocationsAsync();
            var service = CreateService();

            var used = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = true,
                Filters = new Dictionary<string, string[]>
                {
                    ["productBarcode"] = new[] { "__filter:starts:932" },
                },
            });
            var unused = await service.GetPagedListAsync(new LocationReactFilterDto
            {
                PageNumber = 1,
                PageSize = 10,
                IsUsed = false,
                Filters = new Dictionary<string, string[]>
                {
                    ["productBarcode"] = new[] { "__filter:starts:932" },
                },
            });

            Assert.Equal(new[] { "B-01-01-01" }, used.Items.Select(item => item.LocationCode));
            Assert.Equal(0, unused.Total);
            Assert.Empty(unused.Items);
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

        private async Task SeedBoundProductLocationAsync()
        {
            await _db.Insertable(new Location
            {
                LocationGuid = "loc-001",
                LocationCode = "B-03-04-07",
                LocationBarcode = "LOC-B030407",
                LocationType = 1,
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-uuid-001",
                ProductCode = "P001",
                ProductName = "Glitter Pom Pom 25MM",
                ItemNumber = "HB-246-PP-053",
                Barcode = "9525812461024",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-001",
                ProductCode = "P001",
                LocationGuid = "loc-001",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-uuid-deleted",
                ProductCode = "P-DELETED",
                ProductName = "Deleted product",
                ItemNumber = "HB-DELETED",
                Barcode = "DELETED-BARCODE",
                IsActive = false,
                IsDeleted = true,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-deleted",
                ProductCode = "P-DELETED",
                LocationGuid = "loc-001",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-uuid-deleted-link",
                ProductCode = "P-DELETED-LINK",
                ProductName = "Deleted link product",
                ItemNumber = "HB-DELETED-LINK",
                Barcode = "DELETED-LINK-BARCODE",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-deleted-link",
                ProductCode = "P-DELETED-LINK",
                LocationGuid = "loc-001",
                IsDeleted = true,
            }).ExecuteCommandAsync();
        }

        private async Task SeedDuplicateProductMatchesAcrossLocationsAsync()
        {
            await _db.Insertable(new[]
            {
                new Location
                {
                    LocationGuid = "loc-many",
                    LocationCode = "A-01-01-01",
                    LocationBarcode = "LOC-MANY",
                    LocationType = 1,
                    Status = 1,
                    IsDeleted = false,
                },
                new Location
                {
                    LocationGuid = "loc-other",
                    LocationCode = "B-01-01-01",
                    LocationBarcode = "LOC-OTHER",
                    LocationType = 1,
                    Status = 1,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            for (var index = 0; index < 21; index += 1)
            {
                var productCode = $"P-MANY-{index:00}";
                await _db.Insertable(new Product
                {
                    UUID = $"product-uuid-many-{index:00}",
                    ProductCode = productCode,
                    ProductName = $"Many match {index:00}",
                    ItemNumber = $"MATCH-ITEM-{index:00}",
                    Barcode = $"MATCH-BAR-{index:00}",
                    IsActive = true,
                    IsDeleted = false,
                }).ExecuteCommandAsync();

                await _db.Insertable(new ProductLocation
                {
                    Guid = $"product-location-many-{index:00}",
                    ProductCode = productCode,
                    LocationGuid = "loc-many",
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            await _db.Insertable(new Product
            {
                UUID = "product-uuid-other",
                ProductCode = "P-OTHER",
                ProductName = "Other match",
                ItemNumber = "MATCH-ITEM-OTHER",
                Barcode = "MATCH-BAR-OTHER",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-other",
                ProductCode = "P-OTHER",
                LocationGuid = "loc-other",
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedPagedLocationsAsync()
        {
            await _db.Insertable(new[]
            {
                new Location
                {
                    LocationGuid = "loc-page-b",
                    LocationCode = "B-01-01-01",
                    LocationBarcode = "LOC-B",
                    LocationType = 1,
                    Status = 1,
                    UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedBy = "bravo",
                    IsDeleted = false,
                },
                new Location
                {
                    LocationGuid = "loc-page-a",
                    LocationCode = "A-01-01-01",
                    LocationBarcode = "LOC-A",
                    LocationType = 2,
                    Status = 0,
                    UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedBy = "alpha",
                    IsDeleted = false,
                },
                new Location
                {
                    LocationGuid = "loc-page-c",
                    LocationCode = "C-01-01-01",
                    LocationBarcode = "LOC-C",
                    LocationType = 1,
                    Status = 1,
                    UpdatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedBy = "charlie",
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-page-001",
                ProductCode = "P-PAGED-001",
                ProductName = "Paged product",
                ItemNumber = "HB-PAGED-001",
                Barcode = "9320000000011",
                MiddlePackageQuantity = 12,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-page-001",
                ProductCode = "P-PAGED-001",
                LocationGuid = "loc-page-b",
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedDeletedProductOnlyLocationAsync()
        {
            await _db.Insertable(new Location
            {
                LocationGuid = "loc-deleted-product-only",
                LocationCode = "D-01-01-01",
                LocationBarcode = "LOC-D",
                LocationType = 1,
                Status = 1,
                UpdatedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                UpdatedBy = "deleted-product",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-page-deleted-only",
                ProductCode = "P-PAGED-DELETED",
                ProductName = "Deleted only product",
                ItemNumber = "HB-PAGED-DELETED",
                Barcode = "9320000000099",
                IsActive = false,
                IsDeleted = true,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-page-deleted-only",
                ProductCode = "P-PAGED-DELETED",
                LocationGuid = "loc-deleted-product-only",
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private LocationReactService CreateService()
        {
            return new LocationReactService(
                CreateSqlSugarContext(_db),
                Mock.Of<ICurrentUserService>(),
                NullLogger<LocationReactService>.Instance
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
    }
}
