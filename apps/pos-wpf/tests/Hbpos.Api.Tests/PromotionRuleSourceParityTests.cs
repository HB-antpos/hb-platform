using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

// WPF 收银端有两套促销规则来源（C3 任务对照用的特征化测试，钉住「当前」服务端行为）：
//   旧源 = CatalogService.GetPromotionRulesAsync（目录同步 /catalog/promotions，PosCartService 旧引擎用，iPad 也用它）；
//   新源 = PromotionRuleService.GetRulesAsync（/promotions/rules，PromotionEvaluationService 新引擎用）。
// 同一份库数据下两个来源返回的规则集合并不相同；这些差异在用户拍板「以哪个来源为准」之前不要改。
// 生产现配置（1 条生效促销、绑定 1 家店）下两个来源返回同一条规则，差异只在下面这些扩展情形出现。
public sealed class PromotionRuleSourceParityTests
{
    [Fact]
    public async Task Catalog_source_and_promotion_rules_source_return_different_rule_sets_for_the_same_database()
    {
        await using var fixture = new PromotionSourceFixture();
        await fixture.SeedStoreAsync("S01");
        var now = DateTime.UtcNow;

        // 绑定本店 / 绑定多店（含本店）：两个来源都应下发。
        await fixture.SeedPromotionAsync("BOUND-S01", now, priority: 5, isExclusive: true, stores: ["S01"], products: [("A", 1)]);
        await fixture.SeedPromotionAsync("MULTI-STORE", now, priority: 6, stores: ["S01", "S02"], products: [("A", 1)]);
        // 绑定别的店 / 已停用 / 已过期：两个来源都不应下发。
        await fixture.SeedPromotionAsync("OTHER-STORE", now, priority: 3, stores: ["S02"], products: [("A", 1)]);
        await fixture.SeedPromotionAsync("DISABLED", now, priority: 3, stores: ["S01"], products: [("A", 1)], isEnabled: false);
        await fixture.SeedPromotionAsync("EXPIRED", now, priority: 3, stores: ["S01"], products: [("A", 1)], startDays: -5, endDays: -1);
        // 只有新源会下发的扩展情形：
        //  1) 没有任何门店绑定 = 后台口径的「总部促销，对所有门店可见」；旧源要求必须有本店绑定，不下发。
        await fixture.SeedPromotionAsync("HQ-UNBOUND", now, priority: 4, stores: [], products: [("A", 1)]);
        //  2) 固定价为负：旧源在服务端过滤 FixedPrice >= 0；新源不过滤，WPF 新引擎也不检查，会把整组折成 0 元。
        await fixture.SeedPromotionAsync("NEG-FIXED", now, priority: 2, stores: ["S01"], products: [("A", 1)], fixedPrice: -1m);
        //  3) ApplyQuantity <= 0、无商品明细：旧源在服务端过滤；新源照发，但两套 WPF 引擎都会忽略它们（无实际影响）。
        await fixture.SeedPromotionAsync("ZERO-QTY", now, priority: 1, stores: ["S01"], products: [("A", 1)], applyQuantity: 0);
        await fixture.SeedPromotionAsync("NO-PRODUCTS", now, priority: 0, stores: ["S01"], products: []);
        //  4) UnitWeight 为 0：旧源下发时归一为 1；新源原样下发（WPF 新引擎再把 <= 0 当 1，实际等价）。
        await fixture.SeedPromotionAsync("WEIGHT-ZERO", now, priority: -1, stores: ["S01"], products: [("A", 0)]);

        var catalogRules = (await fixture.CreateCatalogService().GetPromotionRulesAsync("S01", CancellationToken.None))!.Promotions;
        var promotionRules = (await new PromotionRuleService(fixture.DbContext).GetRulesAsync("S01", null, CancellationToken.None)).Rules;

        // 旧源：只含「有本店绑定」且 FixedPrice >= 0、ApplyQuantity > 0、有商品明细的规则；互斥规则排在前面。
        Assert.Equal(
            ["BOUND-S01", "MULTI-STORE", "WEIGHT-ZERO"],
            catalogRules.Select(rule => rule.PromotionId).ToArray());
        Assert.Equal(1, Assert.Single(catalogRules, rule => rule.PromotionId == "WEIGHT-ZERO").Products[0].UnitWeight);

        // 新源：多出 HQ-UNBOUND（总部）、NEG-FIXED、ZERO-QTY、NO-PRODUCTS；按 Priority 降序。
        Assert.Equal(
            ["MULTI-STORE", "BOUND-S01", "HQ-UNBOUND", "NEG-FIXED", "ZERO-QTY", "NO-PRODUCTS", "WEIGHT-ZERO"],
            promotionRules.Select(rule => rule.Id).ToArray());
        Assert.Equal(0, Assert.Single(promotionRules, rule => rule.Id == "WEIGHT-ZERO").Products[0].UnitWeight);
        Assert.Equal(-1m, Assert.Single(promotionRules, rule => rule.Id == "NEG-FIXED").FixedPrice);
    }

    [Fact]
    public async Task Production_shaped_database_returns_the_same_single_rule_from_both_sources()
    {
        // 与生产同构：只有 1 条生效促销、互斥、只绑 S01 一家店。两个来源都只下发这一条，字段一致。
        await using var fixture = new PromotionSourceFixture();
        await fixture.SeedStoreAsync("S01");
        await fixture.SeedStoreAsync("S02");
        var now = DateTime.UtcNow;
        await fixture.SeedPromotionAsync("PROMO-PROD", now, priority: 1, isExclusive: true, stores: ["S01"], products: [("SKU-A", 1)], applyQuantity: 3, fixedPrice: 25m);

        var catalogService = fixture.CreateCatalogService();
        var ruleService = new PromotionRuleService(fixture.DbContext);

        var catalogRule = Assert.Single((await catalogService.GetPromotionRulesAsync("S01", CancellationToken.None))!.Promotions);
        var promotionRule = Assert.Single((await ruleService.GetRulesAsync("S01", null, CancellationToken.None)).Rules);
        Assert.Equal(catalogRule.PromotionId, promotionRule.Id);
        Assert.Equal(catalogRule.IsExclusive, promotionRule.IsExclusive);
        Assert.Equal(catalogRule.Priority, promotionRule.Priority);
        Assert.Equal(catalogRule.ApplyQuantity, promotionRule.ApplyQuantity);
        Assert.Equal(catalogRule.FixedPrice, promotionRule.FixedPrice);
        Assert.Equal(catalogRule.MaxApplicationsPerOrder, promotionRule.MaxApplicationsPerOrder);
        Assert.Equal(
            catalogRule.Products.Select(product => (product.ProductCode, product.UnitWeight)).ToArray(),
            promotionRule.Products.Select(product => (product.ProductCode, product.UnitWeight)).ToArray());
        Assert.Equal(catalogRule.EffectiveStart.UtcDateTime, promotionRule.EffectiveStart, TimeSpan.FromMilliseconds(1));
        Assert.Equal(catalogRule.EffectiveEnd.UtcDateTime, promotionRule.EffectiveEnd, TimeSpan.FromMilliseconds(1));

        // 其它门店 S02：两个来源都没有规则。
        Assert.Empty((await catalogService.GetPromotionRulesAsync("S02", CancellationToken.None))!.Promotions);
        Assert.Empty((await ruleService.GetRulesAsync("S02", null, CancellationToken.None)).Rules);
    }

    private sealed class PromotionSourceFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-promotion-source-parity-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        public PromotionSourceFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<Store, Promotion, PromotionProduct, PromotionStore>();

            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            foreach (var propertyName in new[] { nameof(HbposSqlSugarContext.MainDb), nameof(HbposSqlSugarContext.PosmDb) })
            {
                var backingField = typeof(HbposSqlSugarContext).GetField(
                    $"<{propertyName}>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(backingField);
                backingField!.SetValue(context, client);
            }

            DbContext = context;
        }

        public HbposSqlSugarContext DbContext { get; }

        public CatalogService CreateCatalogService()
        {
            return new CatalogService(DbContext, new PriceIndexBuilder(), new CatalogIndexCache());
        }

        public async Task SeedStoreAsync(string storeCode)
        {
            await client.Insertable(new Store
            {
                StoreGUID = $"STORE-{storeCode}",
                StoreCode = storeCode,
                StoreName = $"Store {storeCode}",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }).ExecuteCommandAsync();
        }

        public async Task SeedPromotionAsync(
            string id,
            DateTime now,
            int priority,
            string[] stores,
            (string ProductCode, int UnitWeight)[] products,
            bool isExclusive = false,
            bool isEnabled = true,
            int applyQuantity = 2,
            decimal fixedPrice = 15m,
            int startDays = -1,
            int endDays = 1)
        {
            await client.Insertable(new Promotion
            {
                Id = id,
                Name = id,
                Description = id,
                EffectiveStart = now.AddDays(startDays),
                EffectiveEnd = now.AddDays(endDays),
                IsEnabled = isEnabled,
                IsExclusive = isExclusive,
                Priority = priority,
                ApplyQuantity = applyQuantity,
                FixedPrice = fixedPrice,
                MaxApplicationsPerOrder = null
            }).ExecuteCommandAsync();

            foreach (var store in stores)
            {
                await client.Insertable(new PromotionStore
                {
                    Id = $"{id}-{store}",
                    PromotionId = id,
                    StoreCode = store
                }).ExecuteCommandAsync();
            }

            foreach (var (productCode, unitWeight) in products)
            {
                await client.Insertable(new PromotionProduct
                {
                    Id = $"{id}-{productCode}",
                    PromotionId = id,
                    ProductCode = productCode,
                    UnitWeight = unitWeight
                }).ExecuteCommandAsync();
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
    }
}
