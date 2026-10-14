using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class PromotionRuleServiceTests
{
    [Fact]
    public async Task GetRulesAsync_FiltersStoreScopeAndMapsProducts()
    {
        await using var fixture = await PromotionRuleSqliteFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-06-13T10:00:00Z");

        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-HQ", "Headquarters Bundle", now.UtcDateTime.AddDays(-2), now.UtcDateTime.AddDays(2), priority: 5),
            products:
            [
                new PromotionProduct
                {
                    Id = "PROMO-HQ-P01",
                    PromotionId = "PROMO-HQ",
                    ProductCode = "SKU-HQ-1",
                    UnitWeight = 2
                }
            ]);
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-STORE", "Store Bundle", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1), priority: 9),
            stores:
            [
                new PromotionStore
                {
                    Id = "PROMO-STORE-S01",
                    PromotionId = "PROMO-STORE",
                    StoreCode = "S01"
                }
            ],
            products:
            [
                new PromotionProduct
                {
                    Id = "PROMO-STORE-P01",
                    PromotionId = "PROMO-STORE",
                    ProductCode = "SKU-STORE-1",
                    UnitWeight = 1
                },
                new PromotionProduct
                {
                    Id = "PROMO-STORE-P02",
                    PromotionId = "PROMO-STORE",
                    ProductCode = "SKU-STORE-2",
                    UnitWeight = 3
                }
            ]);
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-OTHER", "Other Store Bundle", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1)),
            stores:
            [
                new PromotionStore
                {
                    Id = "PROMO-OTHER-S02",
                    PromotionId = "PROMO-OTHER",
                    StoreCode = "S02"
                }
            ]);
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-DISABLED", "Disabled Bundle", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1), isEnabled: false));
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-EXPIRED", "Expired Bundle", now.UtcDateTime.AddDays(-3), now.UtcDateTime.AddSeconds(-1)));

        var service = new PromotionRuleService(fixture.DbContext, new MutableFakeTimeProvider(now));

        var response = await service.GetRulesAsync(" S01 ", now, CancellationToken.None);

        Assert.Equal("S01", response.StoreCode);
        Assert.Equal(now, response.GeneratedAt);
        // 没有任何门店绑定的 PROMO-HQ 哪家都不生效，不下发。
        Assert.Equal(["PROMO-STORE"], response.Rules.Select(rule => rule.Id).ToArray());
        Assert.DoesNotContain(response.Rules, rule => rule.Id == "PROMO-HQ");

        var storeRule = Assert.Single(response.Rules, rule => rule.Id == "PROMO-STORE");
        Assert.Equal(2, storeRule.Products.Count);
        Assert.Equal(("SKU-STORE-1", 1), (storeRule.Products[0].ProductCode, storeRule.Products[0].UnitWeight));
        Assert.Equal(("SKU-STORE-2", 3), (storeRule.Products[1].ProductCode, storeRule.Products[1].UnitWeight));
    }

    [Fact]
    public async Task GetRulesAsync_ExcludesSoftDeletedPromotionsStoresAndProducts()
    {
        await using var fixture = await PromotionRuleSqliteFixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-06-13T10:00:00Z");
        var deletedPromotion = CreatePromotion("PROMO-DELETED", "Deleted Promotion", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1));
        deletedPromotion.IsDeleted = true;

        await fixture.SeedPromotionAsync(deletedPromotion);
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-DELETED-STORE", "Deleted Store Scope", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1)),
            stores:
            [
                new PromotionStore
                {
                    Id = "PROMO-DELETED-STORE-S01",
                    PromotionId = "PROMO-DELETED-STORE",
                    StoreCode = "S01",
                    IsDeleted = true
                },
                new PromotionStore
                {
                    Id = "PROMO-DELETED-STORE-S02",
                    PromotionId = "PROMO-DELETED-STORE",
                    StoreCode = "S02"
                }
            ]);
        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-HQ-FROM-DELETED-STORE", "Headquarters From Deleted Store", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1)),
            stores:
            [
                new PromotionStore
                {
                    Id = "PROMO-HQ-FROM-DELETED-STORE-S01",
                    PromotionId = "PROMO-HQ-FROM-DELETED-STORE",
                    StoreCode = "S01",
                    IsDeleted = true
                }
            ],
            products:
            [
                new PromotionProduct
                {
                    Id = "PROMO-HQ-FROM-DELETED-STORE-P01",
                    PromotionId = "PROMO-HQ-FROM-DELETED-STORE",
                    ProductCode = "SKU-ACTIVE",
                    UnitWeight = 1
                },
                new PromotionProduct
                {
                    Id = "PROMO-HQ-FROM-DELETED-STORE-P02",
                    PromotionId = "PROMO-HQ-FROM-DELETED-STORE",
                    ProductCode = "SKU-DELETED",
                    UnitWeight = 9,
                    IsDeleted = true
                }
            ]);

        await fixture.SeedPromotionAsync(
            CreatePromotion("PROMO-BOUND-WITH-DELETED-PRODUCT", "Bound With Deleted Product", now.UtcDateTime.AddDays(-1), now.UtcDateTime.AddDays(1)),
            stores:
            [
                new PromotionStore
                {
                    Id = "PROMO-BOUND-WITH-DELETED-PRODUCT-S01",
                    PromotionId = "PROMO-BOUND-WITH-DELETED-PRODUCT",
                    StoreCode = "S01"
                }
            ],
            products:
            [
                new PromotionProduct
                {
                    Id = "PROMO-BOUND-WITH-DELETED-PRODUCT-P01",
                    PromotionId = "PROMO-BOUND-WITH-DELETED-PRODUCT",
                    ProductCode = "SKU-ACTIVE",
                    UnitWeight = 1
                },
                new PromotionProduct
                {
                    Id = "PROMO-BOUND-WITH-DELETED-PRODUCT-P02",
                    PromotionId = "PROMO-BOUND-WITH-DELETED-PRODUCT",
                    ProductCode = "SKU-DELETED",
                    UnitWeight = 9,
                    IsDeleted = true
                }
            ]);

        var service = new PromotionRuleService(fixture.DbContext, new MutableFakeTimeProvider(now));

        var response = await service.GetRulesAsync("S01", now, CancellationToken.None);

        Assert.DoesNotContain(response.Rules, rule => rule.Id == "PROMO-DELETED");
        Assert.DoesNotContain(response.Rules, rule => rule.Id == "PROMO-DELETED-STORE");

        // 唯一的本店绑定已软删除 = 没有有效绑定，不能退化成「总部促销」。
        Assert.DoesNotContain(response.Rules, rule => rule.Id == "PROMO-HQ-FROM-DELETED-STORE");

        var boundRule = Assert.Single(response.Rules, rule => rule.Id == "PROMO-BOUND-WITH-DELETED-PRODUCT");
        var product = Assert.Single(boundRule.Products);
        Assert.Equal("SKU-ACTIVE", product.ProductCode);
    }

    private static Promotion CreatePromotion(
        string id,
        string name,
        DateTime effectiveStart,
        DateTime effectiveEnd,
        bool isEnabled = true,
        bool isExclusive = true,
        int priority = 0,
        int applyQuantity = 2,
        decimal fixedPrice = 9.9m,
        int? maxApplicationsPerOrder = 1)
    {
        return new Promotion
        {
            Id = id,
            Name = name,
            Description = $"{name} description",
            EffectiveStart = effectiveStart,
            EffectiveEnd = effectiveEnd,
            IsEnabled = isEnabled,
            IsExclusive = isExclusive,
            Priority = priority,
            ApplyQuantity = applyQuantity,
            FixedPrice = fixedPrice,
            MaxApplicationsPerOrder = maxApplicationsPerOrder
        };
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PromotionRuleSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-promotion-rules-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private PromotionRuleSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });

            client.CodeFirst.InitTables<Promotion, PromotionProduct, PromotionStore>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public static Task<PromotionRuleSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new PromotionRuleSqliteFixture());
        }

        public async Task SeedPromotionAsync(
            Promotion promotion,
            IReadOnlyList<PromotionStore>? stores = null,
            IReadOnlyList<PromotionProduct>? products = null)
        {
            // 直接构造 MainDb 数据，验证门店范围和商品权重映射。
            await client.Insertable(promotion).ExecuteCommandAsync();

            if (stores is { Count: > 0 })
            {
                await client.Insertable(stores.ToArray()).ExecuteCommandAsync();
            }

            if (products is { Count: > 0 })
            {
                await client.Insertable(products.ToArray()).ExecuteCommandAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient mainDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), mainDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), mainDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
