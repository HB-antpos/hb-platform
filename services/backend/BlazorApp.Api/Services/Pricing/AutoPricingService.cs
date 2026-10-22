using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Services.Pricing
{
    public class AutoPricingService : IAutoPricingService
    {
        private readonly SqlSugarContext _context;

        public AutoPricingService(SqlSugarContext context)
        {
            _context = context;
        }

        /// <summary>
        /// 查找适用的定价策略
        /// 优先级：供应商 > 分店 > 全局
        /// </summary>
        public async Task<PricingStrategy?> FindStrategyAsync(
            string? supplierCode,
            string? storeCode
        )
        {
            var strategies = await GetAllActiveStrategiesAsync();
            return FindScopedStrategy(strategies, supplierCode, storeCode);
        }

        /// <summary>
        /// 先按进货价过滤，再按供应商和分店的实际目标范围选择策略。
        /// </summary>
        public async Task<PricingStrategy?> FindStrategyForPriceAsync(
            decimal purchasePrice,
            string? supplierCode,
            string? storeCode
        )
        {
            var strategies = await GetAllActiveStrategiesAsync();
            var matched = strategies
                .Where(s => s.Details?.Any(d =>
                    purchasePrice >= d.MinPrice && purchasePrice <= d.MaxPrice
                ) ?? false)
                .ToList();

            return FindScopedStrategy(matched, supplierCode, storeCode);
        }

        /// <summary>
        /// 计算倍率（rate）
        /// </summary>
        public decimal CalculateRate(decimal purchasePrice, PricingStrategy? strategy)
        {
            return CalculateTheoreticalRetail(purchasePrice, strategy) / purchasePrice;
        }

        private static decimal CalculateTheoreticalRetail(decimal purchasePrice, PricingStrategy? strategy)
            => ResolveTheoreticalRetail(purchasePrice, strategy).Theoretical;

        private static (decimal Theoretical, PricingStrategyDetail? Rule) ResolveTheoreticalRetail(
            decimal purchasePrice, PricingStrategy? strategy)
        {
            if (purchasePrice < 0.1m)
                throw new ArgumentException("进货价必须至少为 0.10，才能满足尾数及 1.5～5 成率限制");
            // 交界归属：区间按下限排序后取第一个命中者，所以进货价恰好等于交界值时属于上一个区间（上限含、下限不含）。
            var rule = strategy?.Details?.OrderBy(d => d.MinPrice).FirstOrDefault(d =>
                purchasePrice >= d.MinPrice && purchasePrice <= d.MaxPrice);
            if (rule == null)
            {
                if (strategy != null) throw new ArgumentException("当前成本不在此定价策略范围内");
                return (purchasePrice * 2.5m, null);
            }
            if (PricingCurveMath.IsCurve(rule.Algorithm))
                PricingCurveMath.Validate(strategy!.Details, allowLegacyZero: true, enforceIntervalLimit: false);
            return (PricingCurveMath.TheoreticalRetail(purchasePrice, rule), rule);
        }

        /// <summary>
        /// 计算建议零售价，先按合法尾数及倍率限幅，再将 0.99/1.99 归整为 1/2。
        /// 固定成率（Fixed）区间例外：直接取 进货价 × 成率 并四舍五入到分，不做尾数调整。
        /// </summary>
        public decimal CalculateRetailPrice(decimal purchasePrice, PricingStrategy? strategy)
        {
            var (theoretical, rule) = ResolveTheoreticalRetail(purchasePrice, strategy);
            if (rule != null && PricingCurveMath.IsFixed(rule.Algorithm))
                return decimal.Round(theoretical, 2, MidpointRounding.AwayFromZero);
            return PricingCurveMath.AdjustTail(purchasePrice, theoretical);
        }

        /// <summary>
        /// 便捷方法：直接计算
        /// </summary>
        public async Task<decimal> GetAutoRetailPriceAsync(
            decimal purchasePrice,
            string? supplierCode,
            string? storeCode
        )
        {
            var strategy = await FindStrategyForPriceAsync(purchasePrice, supplierCode, storeCode);
            if (strategy == null)
                return purchasePrice;
            return CalculateRetailPrice(purchasePrice, strategy);
        }

        /// <summary>
        /// 获取所有启用的定价策略（包含明细和目标），用于批量计算时预加载避免N+1查询
        /// </summary>
        public async Task<List<PricingStrategy>> GetAllActiveStrategiesAsync()
        {
            var flatRows = await _context
                .PricingStrategyDb.AsQueryable()
                .LeftJoin<PricingStrategyDetail>((s, d) => s.Id == d.StrategyId)
                .LeftJoin<PricingStrategyTarget>((s, d, t) => s.Id == t.StrategyId)
                .Where((s, d, t) => s.IsEnabled)
                .Select(
                    (s, d, t) =>
                        new
                        {
                            StrategyId = s.Id,
                            StrategyName = s.Name,
                            StrategyLevel = s.Level,
                            StrategyTargetCode = s.TargetCode,
                            StrategyPriority = s.Priority,
                            DetailId = d.Id,
                            DetailMinPrice = d.MinPrice,
                            DetailMaxPrice = d.MaxPrice,
                            DetailStartRate = d.StartRate,
                            DetailEndRate = d.EndRate,
                            DetailAlgorithm = d.Algorithm,
                            DetailStartRetailPrice = d.StartRetailPrice,
                            DetailEndRetailPrice = d.EndRetailPrice,
                            DetailCurveBend = d.CurveBend,
                            TargetId = t.Id,
                            TargetType = t.TargetType,
                            TargetCode = t.TargetCode,
                        }
                )
                .ToListAsync();

            var strategies = flatRows
                .GroupBy(r => new
                {
                    r.StrategyId,
                    r.StrategyName,
                    r.StrategyLevel,
                    r.StrategyTargetCode,
                    r.StrategyPriority,
                })
                .Select(g => new PricingStrategy
                {
                    Id = g.Key.StrategyId,
                    Name = g.Key.StrategyName,
                    Level = g.Key.StrategyLevel,
                    TargetCode = g.Key.StrategyTargetCode,
                    Priority = g.Key.StrategyPriority,
                    Details = g.Where(x => x.DetailId != null)
                        .GroupBy(x => x.DetailId)
                        .Select(gg => new PricingStrategyDetail
                        {
                            Id = gg.Key!,
                            StrategyId = g.Key.StrategyId,
                            MinPrice = gg.First().DetailMinPrice,
                            MaxPrice = gg.First().DetailMaxPrice,
                            StartRate = gg.First().DetailStartRate,
                            EndRate = gg.First().DetailEndRate,
                            Algorithm = gg.First().DetailAlgorithm,
                            StartRetailPrice = gg.First().DetailStartRetailPrice,
                            EndRetailPrice = gg.First().DetailEndRetailPrice,
                            CurveBend = gg.First().DetailCurveBend,
                        })
                        .ToList(),
                    Targets = g.Where(x => x.TargetId != null)
                        .GroupBy(x => x.TargetId)
                        .Select(gg => new PricingStrategyTarget
                        {
                            Id = gg.Key!,
                            StrategyId = g.Key.StrategyId,
                            TargetType = gg.First().TargetType,
                            TargetCode = gg.First().TargetCode,
                        })
                        .ToList(),
                })
                .ToList();

            // 旧单目标数据仅在内存中补齐 Targets，供手机和进货单按同一范围分组，不写回数据库。
            // 已有新 Targets 时以新配置为准，避免陈旧的 TargetCode 扩大适用范围。
            foreach (var strategy in strategies)
            {
                if (strategy.Targets.Count == 0
                    && strategy.Level is "Supplier" or "Store"
                    && !string.IsNullOrWhiteSpace(strategy.TargetCode))
                {
                    strategy.Targets.Add(new PricingStrategyTarget
                    {
                        Id = $"legacy:{strategy.Id}",
                        StrategyId = strategy.Id,
                        TargetType = strategy.Level,
                        TargetCode = strategy.TargetCode,
                    });
                }
            }

            return strategies;
        }

        /// <summary>
        /// 在内存中查找最佳定价策略（用于批量计算时避免N+1查询）
        /// 优先级：供应商+分店 > 供应商 > 分店 > 全局
        /// </summary>
        public PricingStrategy? FindBestStrategyForPrice(
            decimal purchasePrice,
            List<PricingStrategy> supplierStrategies,
            List<PricingStrategy> storeStrategies,
            List<PricingStrategy> globalStrategies
        )
        {
            List<PricingStrategy> MatchByPrice(List<PricingStrategy> list)
            {
                return list
                    .Where(s =>
                        s.Details?.Any(d =>
                            purchasePrice >= d.MinPrice && purchasePrice <= d.MaxPrice
                        ) ?? false
                    )
                    .ToList();
            }

            return SelectScopedStrategy(
                MatchByPrice(supplierStrategies),
                MatchByPrice(storeStrategies),
                MatchByPrice(globalStrategies)
            );
        }

        private static PricingStrategy? FindScopedStrategy(
            List<PricingStrategy> strategies,
            string? supplierCode,
            string? storeCode
        )
        {
            return SelectScopedStrategy(
                strategies.Where(s => MatchesTarget(s, "Supplier", supplierCode)).ToList(),
                strategies.Where(s => MatchesTarget(s, "Store", storeCode)).ToList(),
                strategies.Where(s => s.Level == "Global" || s.Targets == null || s.Targets.Count == 0).ToList()
            );
        }

        private static PricingStrategy? SelectScopedStrategy(
            List<PricingStrategy> supplierStrategies,
            List<PricingStrategy> storeStrategies,
            List<PricingStrategy> globalStrategies
        )
        {
            // 同类目标允许命中任意一个；同时配置供应商和分店时，两类目标必须都命中同一策略。
            var matchedStoreIds = storeStrategies.Select(s => s.Id).ToHashSet();
            var bothStrategy = supplierStrategies
                .Where(s => HasTarget(s, "Store") && matchedStoreIds.Contains(s.Id))
                .OrderByDescending(s => s.Priority)
                .FirstOrDefault();
            if (bothStrategy != null)
                return bothStrategy;

            // 单类回退不能重新捞回另一类目标不匹配的混合策略。
            var supplierStrategy = supplierStrategies
                .Where(s => !HasTarget(s, "Store"))
                .OrderByDescending(s => s.Priority)
                .FirstOrDefault();
            if (supplierStrategy != null)
                return supplierStrategy;

            var storeStrategy = storeStrategies
                .Where(s => !HasTarget(s, "Supplier"))
                .OrderByDescending(s => s.Priority)
                .FirstOrDefault();
            if (storeStrategy != null)
                return storeStrategy;

            // 级别为全局或旧数据没有目标行，也不能绕过其已经配置的范围限制。
            return globalStrategies
                .Where(s => !HasTarget(s, "Supplier") && !HasTarget(s, "Store"))
                .OrderByDescending(s => s.Priority)
                .FirstOrDefault();
        }

        private static bool HasTarget(PricingStrategy strategy, string targetType)
        {
            if (strategy.Targets?.Count > 0)
                return strategy.Targets.Any(t => t.TargetType == targetType);

            // 兼容尚未迁移到多目标表的旧策略，避免把它们误当作全局策略。
            return strategy.Level == targetType && !string.IsNullOrWhiteSpace(strategy.TargetCode);
        }

        private static bool MatchesTarget(PricingStrategy strategy, string targetType, string? targetCode)
        {
            if (string.IsNullOrWhiteSpace(targetCode))
                return false;

            if (strategy.Targets?.Count > 0)
                return strategy.Targets.Any(t => t.TargetType == targetType && t.TargetCode == targetCode);

            return strategy.Level == targetType && strategy.TargetCode == targetCode;
        }
    }
}
