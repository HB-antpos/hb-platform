using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 贺卡剩余填报的「当前生效」口径，填报端（预填、覆盖判定）与后台统计共用同一套规则：
    /// - 有供应商的数据按批次整组生效：同一 分店 + 年份 + 节日 + 供应商 取提交时间最新的一批，旧批次整批作废；
    /// - 批量填报上线前的历史行没有供应商、没有批次号，只能按价格目录项各自取最新一条，归入「未指定供应商」。
    /// 只做内存计算（数据量为 分店 × 供应商 × 4 个价格），便于 SQLite 测试覆盖。
    /// </summary>
    internal static class SeasonalCardBatchResolver
    {
        public const string UnassignedSupplierName = "未指定供应商";

        /// <summary>rows 须为同一分店、同一年份、同一节日的未删除提交行。</summary>
        public static List<SeasonalCardRemainingSubmission> ResolveEffectiveLines(
            IEnumerable<SeasonalCardRemainingSubmission> rows
        )
        {
            var result = new List<SeasonalCardRemainingSubmission>();
            foreach (var supplierGroup in rows.GroupBy(row => SupplierKey(row.LocalSupplierCode), StringComparer.OrdinalIgnoreCase))
            {
                if (supplierGroup.Key.Length == 0)
                {
                    // 历史单条记录：同一价格目录项只认最新一条。
                    result.AddRange(
                        supplierGroup
                            .GroupBy(row => row.CatalogGuid, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group
                                .OrderByDescending(row => row.SubmittedAt)
                                .ThenByDescending(row => row.SubmissionGuid, StringComparer.Ordinal)
                                .First())
                    );
                    continue;
                }

                var latestBatch = GroupIntoBatches(supplierGroup).First();
                result.AddRange(latestBatch);
            }

            return result;
        }

        /// <summary>
        /// 同一供应商（或同一组历史行）下的最新批次；没有任何行时为 null。
        /// </summary>
        public static List<SeasonalCardRemainingSubmission>? FindLatestBatch(
            IEnumerable<SeasonalCardRemainingSubmission> rows
        )
        {
            var batches = GroupIntoBatches(rows);
            return batches.Count == 0 ? null : batches[0];
        }

        /// <summary>按批次分组（历史行一行一批），按提交时间倒序。</summary>
        public static List<List<SeasonalCardRemainingSubmission>> GroupIntoBatches(
            IEnumerable<SeasonalCardRemainingSubmission> rows
        )
        {
            return rows
                .GroupBy(BatchKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToList())
                .OrderByDescending(batch => batch.Max(row => row.SubmittedAt))
                .ThenByDescending(batch => BatchKey(batch[0]), StringComparer.Ordinal)
                .ToList();
        }

        public static string BatchKey(SeasonalCardRemainingSubmission row) =>
            string.IsNullOrWhiteSpace(row.BatchGuid) ? row.SubmissionGuid : row.BatchGuid;

        public static string SupplierKey(string? localSupplierCode) =>
            string.IsNullOrWhiteSpace(localSupplierCode) ? string.Empty : localSupplierCode.Trim();

        public static decimal LineAmount(SeasonalCardRemainingSubmission row) =>
            row.RemainingQuantity * row.UnitPrice;

        public static SeasonalCardBatchDto ToBatchDto(
            IReadOnlyCollection<SeasonalCardRemainingSubmission> lines,
            string? storeName,
            bool isCurrent
        )
        {
            var ordered = lines
                .OrderBy(row => row.PriceOption)
                .ThenBy(row => row.CatalogGuid, StringComparer.Ordinal)
                .ToList();
            var latest = lines
                .OrderByDescending(row => row.SubmittedAt)
                .ThenByDescending(row => row.SubmissionGuid, StringComparer.Ordinal)
                .First();

            return new SeasonalCardBatchDto
            {
                BatchGuid = string.IsNullOrWhiteSpace(latest.BatchGuid) ? null : latest.BatchGuid,
                StoreCode = latest.StoreCode,
                StoreName = storeName,
                SeasonYear = latest.SeasonYear,
                CardType = latest.CardType,
                CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(latest.CardType),
                LocalSupplierCode = string.IsNullOrWhiteSpace(latest.LocalSupplierCode) ? null : latest.LocalSupplierCode,
                SupplierName = string.IsNullOrWhiteSpace(latest.LocalSupplierCode)
                    ? UnassignedSupplierName
                    : latest.SupplierName,
                Remark = ordered.Select(row => row.Remark).FirstOrDefault(remark => !string.IsNullOrWhiteSpace(remark)),
                SubmittedByName = latest.SubmittedByName,
                SubmittedAt = latest.SubmittedAt,
                TotalQuantity = ordered.Sum(row => row.RemainingQuantity),
                TotalAmount = ordered.Sum(LineAmount),
                IsCurrent = isCurrent,
                Lines = ordered
                    .Select(row => new SeasonalCardBatchLineDto
                    {
                        SubmissionGuid = row.SubmissionGuid,
                        CatalogGuid = row.CatalogGuid,
                        PriceOption = row.PriceOption,
                        PriceLabel = row.PriceLabel,
                        UnitPrice = row.UnitPrice,
                        RemainingQuantity = row.RemainingQuantity,
                    })
                    .ToList(),
            };
        }
    }
}
