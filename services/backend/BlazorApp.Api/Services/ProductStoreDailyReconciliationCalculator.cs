using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services
{
    internal sealed record ProductStoreDailyBranchRollup(
        string BranchCode,
        decimal TotalAmount,
        int TotalQuantity
    );

    internal sealed record ProductStoreDailyBranchDiagnostic(
        decimal UnmatchedSupplierAmount,
        int UnmatchedSupplierQuantity,
        int UnmatchedSupplierProductCount
    );

    internal sealed record ProductStoreDailyReconciliationResult(
        string Status,
        string? ErrorMessage,
        decimal ProductTotalAmount,
        decimal? StoreTotalAmount,
        decimal? AmountDifference,
        int ProductTotalQuantity,
        int? StoreTotalQuantity,
        int? QuantityDifference
    );

    internal static class ProductStoreDailyReconciliationCalculator
    {
        /// <summary>对账不一致消息的固定前缀；调用方据此区分「数据已算好但对不上」与「写入过程抛出的真异常」。</summary>
        internal const string FailureMessagePrefix = "商品统计与分店营业额统计不一致";

        /// <summary>消息是否来自对账不一致（而非统计写入异常）。</summary>
        internal static bool IsReconciliationFailure(string? errorMessage) =>
            errorMessage != null
            && errorMessage.StartsWith(FailureMessagePrefix, StringComparison.Ordinal);

        private const decimal AmountToleranceRate = 0.001m;
        private const decimal AmountAbsoluteTolerance = 100m;

        public static IReadOnlyList<ProductStoreDailyBranchRollup> ApplyExistingStoreAdjustments(
            IEnumerable<ProductStoreDailyBranchRollup> storeRows,
            IEnumerable<ProductStoreDailyBranchRollup> adjustments
        )
        {
            var adjustmentMap = adjustments
                .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new ProductStoreDailyBranchRollup(
                        group.Key,
                        group.Sum(x => x.TotalAmount),
                        group.Sum(x => x.TotalQuantity)
                    ),
                    StringComparer.OrdinalIgnoreCase
                );

            // 只调整已有分店营业额行，避免单独一笔退货掩盖“分店统计缺失”。
            return storeRows
                .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    adjustmentMap.TryGetValue(group.Key, out var adjustment);
                    return new ProductStoreDailyBranchRollup(
                        group.Key,
                        group.Sum(x => x.TotalAmount) + (adjustment?.TotalAmount ?? 0m),
                        group.Sum(x => x.TotalQuantity) + (adjustment?.TotalQuantity ?? 0)
                    );
                })
                .ToList();
        }

        public static ProductStoreDailyReconciliationResult Calculate(
            DateTime targetDate,
            IEnumerable<ProductStoreDailyBranchRollup> productRows,
            IEnumerable<ProductStoreDailyBranchRollup> storeRows,
            IReadOnlyDictionary<string, ProductStoreDailyBranchDiagnostic>? diagnostics = null
        )
        {
            var productMap = AggregateByBranch(productRows);
            var storeMap = AggregateByBranch(storeRows);

            var productTotalAmount = productMap.Values.Sum(x => x.TotalAmount);
            var productTotalQuantity = productMap.Values.Sum(x => x.TotalQuantity);
            var storeTotalAmount = storeMap.Any()
                ? storeMap.Values.Sum(x => x.TotalAmount)
                : (decimal?)null;
            var storeTotalQuantity = storeMap.Any()
                ? storeMap.Values.Sum(x => x.TotalQuantity)
                : (int?)null;

            if (!productMap.Any() && !storeMap.Any())
            {
                // 双方都没有统计行表示当天确实为零销售；任务已完成，不能重新落回 Pending。
                return new ProductStoreDailyReconciliationResult(
                    SalesStatisticRefreshStatus.Fresh,
                    null,
                    0m,
                    0m,
                    0m,
                    0,
                    0,
                    0
                );
            }

            var branchCodes = productMap.Keys
                .Union(storeMap.Keys)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToList();
            decimal amountDifference = 0m;
            var quantityDifference = 0;
            string? firstFailureMessage = null;

            foreach (var branchCode in branchCodes)
            {
                productMap.TryGetValue(branchCode, out var product);
                storeMap.TryGetValue(branchCode, out var store);

                var productAmount = product?.TotalAmount ?? 0m;
                var storeAmount = store?.TotalAmount ?? 0m;
                var branchAmountDiff = Math.Abs(productAmount - storeAmount);
                amountDifference += branchAmountDiff;
                quantityDifference += Math.Abs((product?.TotalQuantity ?? 0) - (store?.TotalQuantity ?? 0));

                if (firstFailureMessage != null)
                {
                    continue;
                }

                if (store == null)
                {
                    firstFailureMessage =
                        $"{FailureMessagePrefix}: {targetDate:yyyy-MM-dd} {branchCode}, 商品金额 {productAmount}, 分店营业额统计缺失, 金额差 {branchAmountDiff}";
                    continue;
                }

                if (product == null || branchAmountDiff > CalculateAmountTolerance(storeAmount))
                {
                    firstFailureMessage =
                        $"{FailureMessagePrefix}: {targetDate:yyyy-MM-dd} {branchCode}, 商品金额 {productAmount}, 分店营业额 {storeAmount}, 金额差 {branchAmountDiff}{BuildDiagnosticSuffix(branchCode, diagnostics)}";
                }
            }

            var status = firstFailureMessage == null
                ? SalesStatisticRefreshStatus.Fresh
                : SalesStatisticRefreshStatus.Failed;

            return new ProductStoreDailyReconciliationResult(
                status,
                firstFailureMessage,
                productTotalAmount,
                storeTotalAmount,
                amountDifference,
                productTotalQuantity,
                storeTotalQuantity,
                quantityDifference
                );
        }

        private static decimal CalculateAmountTolerance(decimal storeAmount)
        {
            // 金额对账同时允许 0.1% 比例容差和 100 元绝对容差，避免小额差异误判失败。
            return Math.Max(Math.Abs(storeAmount) * AmountToleranceRate, AmountAbsoluteTolerance);
        }

        private static Dictionary<string, ProductStoreDailyBranchRollup> AggregateByBranch(
            IEnumerable<ProductStoreDailyBranchRollup> rows
        )
        {
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                .GroupBy(row => row.BranchCode)
                .ToDictionary(
                    group => group.Key,
                    group => new ProductStoreDailyBranchRollup(
                        group.Key,
                        group.Sum(x => x.TotalAmount),
                        group.Sum(x => x.TotalQuantity)
                    )
                );
        }

        private static string BuildDiagnosticSuffix(
            string branchCode,
            IReadOnlyDictionary<string, ProductStoreDailyBranchDiagnostic>? diagnostics
        )
        {
            if (diagnostics == null || !diagnostics.TryGetValue(branchCode, out var diagnostic))
            {
                return string.Empty;
            }

            return
                $", 未匹配供应商金额 {diagnostic.UnmatchedSupplierAmount}, 未匹配供应商数量 {diagnostic.UnmatchedSupplierQuantity}, 未匹配商品数 {diagnostic.UnmatchedSupplierProductCount}";
        }
    }
}
