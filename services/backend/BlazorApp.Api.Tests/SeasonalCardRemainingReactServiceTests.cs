using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SeasonalCardRemainingReactServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public SeasonalCardRemainingReactServiceTests()
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
            typeof(User),
            typeof(Store),
            typeof(UserStore),
            typeof(SeasonalCardCatalog),
            typeof(SeasonalCardRemainingSubmission),
            typeof(HBLocalSupplier)
        );
    }

    [Fact]
    public async Task CreateSubmissionAsync_WhenCatalogUsesFixedPriceAndRequestCarriesCustomUnitPrice_ReturnsErrorAndDoesNotInsert()
    {
        await SeedStoreScopeAsync();
        await SeedCatalogAsync(
            "catalog-fixed-2",
            SeasonalCardType.Christmas,
            "$2",
            false,
            2m,
            2
        );
        var service = CreateService("manager-user", "manager", "StoreManager");

        var result = await service.CreateSubmissionAsync(new CreateSeasonalCardRemainingSubmissionDto
        {
            StoreCode = "BRI",
            CatalogGuid = "catalog-fixed-2",
            SeasonYear = 2026,
            RemainingQuantity = 7,
            CustomUnitPrice = 9.99m,
            Remark = "fixed",
        });

        Assert.False(result.Success);
        Assert.Equal("FIXED_PRICE_OVERRIDE_NOT_ALLOWED", result.ErrorCode);

        Assert.Equal(0, await _db.Queryable<SeasonalCardRemainingSubmission>().CountAsync());
    }

    [Fact]
    public async Task CreateSubmissionAsync_WhenCatalogUsesOtherPrice_RequiresPositiveCustomUnitPrice()
    {
        await SeedStoreScopeAsync();
        await SeedCatalogAsync(
            "catalog-other",
            SeasonalCardType.Easter,
            "其他",
            true,
            null,
            4
        );
        var service = CreateService("manager-user", "manager", "StoreManager");

        var invalidResult = await service.CreateSubmissionAsync(
            new CreateSeasonalCardRemainingSubmissionDto
            {
                StoreCode = "BRI",
                CatalogGuid = "catalog-other",
                SeasonYear = 2026,
                RemainingQuantity = 3,
            }
        );

        Assert.False(invalidResult.Success);
        Assert.Equal("CUSTOM_PRICE_REQUIRED", invalidResult.ErrorCode);

        var validResult = await service.CreateSubmissionAsync(
            new CreateSeasonalCardRemainingSubmissionDto
            {
                StoreCode = "BRI",
                CatalogGuid = "catalog-other",
                SeasonYear = 2026,
                RemainingQuantity = 3,
                CustomUnitPrice = 4.5m,
            }
        );

        Assert.True(validResult.Success);
        Assert.Equal(4.5m, validResult.Data!.UnitPrice);
    }

    [Fact]
    public async Task CreateSubmissionAsync_WhenOtherPriceRoundsToZero_ReturnsErrorAndDoesNotInsert()
    {
        await SeedStoreScopeAsync();
        await SeedCatalogAsync(
            "catalog-other",
            SeasonalCardType.ValentinesDay,
            "其他",
            true,
            null,
            4
        );
        var service = CreateService("manager-user", "manager", "StoreManager");

        var result = await service.CreateSubmissionAsync(
            new CreateSeasonalCardRemainingSubmissionDto
            {
                StoreCode = "BRI",
                CatalogGuid = "catalog-other",
                SeasonYear = 2026,
                RemainingQuantity = 3,
                CustomUnitPrice = 0.001m,
            }
        );

        Assert.False(result.Success);
        Assert.Equal("CUSTOM_PRICE_REQUIRED", result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<SeasonalCardRemainingSubmission>().CountAsync());
    }

    [Fact]
    public async Task GetSubmissionsAsync_WhenStoreManagerRequestsUnmanagedStore_ReturnsForbidden()
    {
        await SeedStoreScopeAsync();
        await SeedCatalogAsync(
            "catalog-fixed-1",
            SeasonalCardType.FathersDay,
            "$1",
            false,
            1m,
            1
        );
        await SeedSubmissionAsync(
            "submission-other",
            "OTHER",
            "catalog-fixed-1",
            SeasonalCardType.FathersDay,
            "$1",
            1m
        );
        var service = CreateService("manager-user", "manager", "StoreManager");

        var result = await service.GetSubmissionsAsync(new SeasonalCardRemainingSubmissionQueryDto
        {
            StoreCode = "OTHER",
            PageNumber = 1,
            PageSize = 20,
        });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
    }

    [Fact]
    public async Task CreateBatchAsync_首次整组提交_四行共用批次号并由服务端写入供应商名称快照()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        var service = CreateService("manager-user", "manager", "StoreManager");

        var result = await service.CreateBatchAsync(BatchRequest(
            SeasonalCardType.Christmas, "SUP-A", null, 120, 80, 36, 12, otherPrice: 4.5m));

        Assert.True(result.Success, result.Message);
        var rows = await _db.Queryable<SeasonalCardRemainingSubmission>().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Single(rows.Select(row => row.BatchGuid).Distinct());
        Assert.All(rows, row =>
        {
            Assert.Equal("SUP-A", row.LocalSupplierCode);
            Assert.Equal("Supplier A", row.SupplierName);
            Assert.Equal(2026, row.SeasonYear);
        });
        Assert.Equal(248, result.Data!.TotalQuantity);
        Assert.Equal(120m + 160m + 108m + 54m, result.Data.TotalAmount);
        Assert.Equal(result.Data.BatchGuid, rows[0].BatchGuid);
    }

    [Fact]
    public async Task CreateBatchAsync_价格项缺失或重复_整批拒绝不写入()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        var service = CreateService("manager-user", "manager", "StoreManager");

        var missing = BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 1, 2, 3, 0);
        missing.Items.RemoveAt(3);
        var duplicated = BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 1, 2, 3, 0);
        duplicated.Items[3] = new SeasonalCardBatchItemDto { CatalogGuid = duplicated.Items[0].CatalogGuid, RemainingQuantity = 1 };

        Assert.Equal("BATCH_ITEMS_MISMATCH", (await service.CreateBatchAsync(missing)).ErrorCode);
        Assert.Equal("BATCH_ITEMS_MISMATCH", (await service.CreateBatchAsync(duplicated)).ErrorCode);
        Assert.Equal(0, await _db.Queryable<SeasonalCardRemainingSubmission>().CountAsync());
    }

    [Fact]
    public async Task CreateBatchAsync_供应商停用或不存在_拒绝()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-OFF", "Disabled", status: 0);
        var service = CreateService("manager-user", "manager", "StoreManager");

        Assert.Equal(
            "SUPPLIER_NOT_FOUND",
            (await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-OFF", null, 1, 0, 0, 0))).ErrorCode
        );
        Assert.Equal(
            "SUPPLIER_NOT_FOUND",
            (await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-NONE", null, 1, 0, 0, 0))).ErrorCode
        );
    }

    [Fact]
    public async Task CreateBatchAsync_其他价格数量为零可不填单价_有数量必须填单价()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Easter);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        var service = CreateService("manager-user", "manager", "StoreManager");

        var needsPrice = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Easter, "SUP-A", null, 1, 2, 3, 5));
        Assert.Equal("CUSTOM_PRICE_REQUIRED", needsPrice.ErrorCode);

        var zeroOther = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Easter, "SUP-A", null, 1, 2, 3, 0));
        Assert.True(zeroOther.Success, zeroOther.Message);
        var other = await _db.Queryable<SeasonalCardRemainingSubmission>()
            .FirstAsync(row => row.PriceOption == SeasonalCardPriceOptionType.Other);
        Assert.Equal(0, other.RemainingQuantity);
        Assert.Equal(0m, other.UnitPrice);
    }

    [Fact]
    public async Task CreateBatchAsync_覆盖须带上当前批次号_过期返回最新批次_相同数量拒绝重复提交()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        var service = CreateService("manager-user", "manager", "StoreManager");
        var first = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 150, 96, 40, 0));
        Assert.True(first.Success, first.Message);
        var firstBatch = first.Data!.BatchGuid;

        // 没带上次批次号（客户端以为没填过）：判定为已被更新，并把当前批次带回去。
        var stale = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 120, 80, 36, 0));
        Assert.Equal("SEASONAL_CARD_STALE", stale.ErrorCode);
        var staleDetails = Assert.IsType<SeasonalCardBatchDto>(stale.Details);
        Assert.Equal(firstBatch, staleDetails.BatchGuid);

        // 数量与上次完全相同：不生成新记录。
        var unchanged = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-A", firstBatch, 150, 96, 40, 0));
        Assert.Equal("SEASONAL_CARD_NO_CHANGES", unchanged.ErrorCode);
        Assert.Equal(4, await _db.Queryable<SeasonalCardRemainingSubmission>().CountAsync());

        // 正常覆盖：新批次生效，旧批次保留为历史。
        var overwrite = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-A", firstBatch, 120, 80, 36, 0));
        Assert.True(overwrite.Success, overwrite.Message);
        Assert.Equal("已覆盖上次填报", overwrite.Message);
        Assert.Equal(8, await _db.Queryable<SeasonalCardRemainingSubmission>().CountAsync());

        var overview = await service.GetOverviewAsync(new SeasonalCardOverviewQueryDto
        {
            StoreCode = "BRI",
            SeasonYear = 2026,
            LocalSupplierCode = "SUP-A",
        });
        Assert.True(overview.Success, overview.Message);
        var christmas = overview.Data!.Holidays.Single(item => item.CardType == SeasonalCardType.Christmas);
        Assert.Equal(overwrite.Data!.BatchGuid, christmas.CurrentBatch!.BatchGuid);
        Assert.Equal(236, christmas.CurrentBatch.TotalQuantity);
        Assert.Null(overview.Data.Holidays.Single(item => item.CardType == SeasonalCardType.Easter).CurrentBatch);
        Assert.Equal(5, overview.Data.Holidays.Count);
    }

    [Fact]
    public async Task GetOverviewAsync_不同供应商的批次互不影响()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        await SeedSupplierAsync("SUP-B", "Supplier B");
        var service = CreateService("manager-user", "manager", "StoreManager");
        Assert.True((await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 10, 0, 0, 0))).Success);

        // 供应商 B 没填过：首次提交不需要批次号，也不受 A 的批次影响。
        var supplierB = await service.CreateBatchAsync(BatchRequest(SeasonalCardType.Christmas, "SUP-B", null, 5, 0, 0, 0));
        Assert.True(supplierB.Success, supplierB.Message);

        var overviewB = await service.GetOverviewAsync(new SeasonalCardOverviewQueryDto
        {
            StoreCode = "BRI",
            SeasonYear = 2026,
            LocalSupplierCode = "SUP-B",
        });
        Assert.Equal(5, overviewB.Data!.Holidays.Single(item => item.CardType == SeasonalCardType.Christmas).CurrentBatch!.TotalQuantity);
        Assert.Equal("Supplier B", overviewB.Data.SupplierName);
    }

    [Fact]
    public async Task CreateBatchAsync_店长提交非主分店_拒绝()
    {
        await SeedStoreScopeAsync();
        await SeedFullCatalogAsync(SeasonalCardType.Christmas);
        await SeedSupplierAsync("SUP-A", "Supplier A");
        var service = CreateService("manager-user", "manager", "StoreManager");
        var request = BatchRequest(SeasonalCardType.Christmas, "SUP-A", null, 1, 0, 0, 0);
        request.StoreCode = "OTHER";

        Assert.Equal("FORBIDDEN_STORE", (await service.CreateBatchAsync(request)).ErrorCode);
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

    private SeasonalCardRemainingReactService CreateService(
        string userGuid,
        string username,
        params string[] roles
    )
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(CreateClaims(userGuid, username, roles), "TestAuth")
                ),
            },
        };
        var currentUserService = new CurrentUserService(httpContextAccessor);
        var context = CreateSqlSugarContext(_db);
        var scopeService = new CurrentUserManageableStoreScopeService(
            context,
            currentUserService,
            httpContextAccessor
        );

        return new SeasonalCardRemainingReactService(
            context,
            currentUserService,
            scopeService,
            NullLogger<SeasonalCardRemainingReactService>.Instance
        );
    }

    private async Task SeedStoreScopeAsync()
    {
        await _db.Insertable(
            new[]
            {
                new Store
                {
                    StoreGUID = "store-bri",
                    StoreCode = "BRI",
                    StoreName = "Brisbane",
                    CreatedAt = DateTime.UtcNow,
                },
                new Store
                {
                    StoreGUID = "store-other",
                    StoreCode = "OTHER",
                    StoreName = "Other",
                    CreatedAt = DateTime.UtcNow,
                },
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new[]
            {
                new User
                {
                    UserGUID = "manager-user",
                    Username = "manager",
                    Email = "manager@example.com",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                },
                new User
                {
                    UserGUID = "admin-user",
                    Username = "admin",
                    Email = "admin@example.com",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                },
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "manager-store-bri",
            UserGUID = "manager-user",
            StoreGUID = "store-bri",
            IsPrimary = true,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedCatalogAsync(
        string catalogGuid,
        SeasonalCardType cardType,
        string priceLabel,
        bool allowsCustomUnitPrice,
        decimal? fixedUnitPrice,
        int sortOrder,
        SeasonalCardPriceOptionType priceOption = SeasonalCardPriceOptionType.FixedOneDollar
    )
    {
        await _db.Insertable(new SeasonalCardCatalog
        {
            CatalogGuid = catalogGuid,
            CatalogCode = $"{cardType}-{priceLabel}",
            CardType = cardType,
            PriceOption = priceOption,
            PriceLabel = priceLabel,
            AllowsCustomUnitPrice = allowsCustomUnitPrice,
            FixedUnitPrice = fixedUnitPrice,
            SortOrder = sortOrder,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedFullCatalogAsync(SeasonalCardType cardType)
    {
        await SeedCatalogAsync($"{cardType}-1", cardType, "$1", false, 1m, 1, SeasonalCardPriceOptionType.FixedOneDollar);
        await SeedCatalogAsync($"{cardType}-2", cardType, "$2", false, 2m, 2, SeasonalCardPriceOptionType.FixedTwoDollars);
        await SeedCatalogAsync($"{cardType}-3", cardType, "$3", false, 3m, 3, SeasonalCardPriceOptionType.FixedThreeDollars);
        await SeedCatalogAsync($"{cardType}-other", cardType, "其他", true, null, 4, SeasonalCardPriceOptionType.Other);
    }

    private async Task SeedSupplierAsync(string code, string name, int status = 1)
    {
        await _db.Insertable(new HBLocalSupplier
        {
            Guid = Guid.NewGuid().ToString(),
            LocalSupplierCode = code,
            Name = name,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private static CreateSeasonalCardRemainingBatchDto BatchRequest(
        SeasonalCardType cardType,
        string supplierCode,
        string? expectedBatchGuid,
        int one,
        int two,
        int three,
        int other,
        decimal? otherPrice = null
    ) => new()
    {
        StoreCode = "BRI",
        SeasonYear = 2026,
        CardType = cardType,
        LocalSupplierCode = supplierCode,
        ExpectedPreviousBatchGuid = expectedBatchGuid,
        Items = new List<SeasonalCardBatchItemDto>
        {
            new() { CatalogGuid = $"{cardType}-1", RemainingQuantity = one },
            new() { CatalogGuid = $"{cardType}-2", RemainingQuantity = two },
            new() { CatalogGuid = $"{cardType}-3", RemainingQuantity = three },
            new() { CatalogGuid = $"{cardType}-other", RemainingQuantity = other, CustomUnitPrice = otherPrice },
        },
    };

    private async Task SeedSubmissionAsync(
        string submissionGuid,
        string storeCode,
        string catalogGuid,
        SeasonalCardType cardType,
        string priceLabel,
        decimal unitPrice
    )
    {
        await _db.Insertable(new SeasonalCardRemainingSubmission
        {
            SubmissionGuid = submissionGuid,
            StoreCode = storeCode,
            CatalogGuid = catalogGuid,
            CardType = cardType,
            PriceLabel = priceLabel,
            UnitPrice = unitPrice,
            SeasonYear = 2026,
            RemainingQuantity = 1,
            SubmittedAt = DateTime.UtcNow,
            SubmittedByUserGuid = "manager-user",
            SubmittedByName = "manager",
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
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

    private static IEnumerable<Claim> CreateClaims(
        string userGuid,
        string username,
        IEnumerable<string> roles
    )
    {
        yield return new Claim("userGuid", userGuid);
        yield return new Claim("userId", userGuid);
        yield return new Claim(ClaimTypes.NameIdentifier, userGuid);
        yield return new Claim(ClaimTypes.Name, username);

        foreach (var role in roles)
        {
            yield return new Claim(ClaimTypes.Role, role);
        }
    }
}
