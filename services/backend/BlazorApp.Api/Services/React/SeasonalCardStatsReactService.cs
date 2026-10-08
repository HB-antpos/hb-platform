using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 后台「节日贺卡填报统计」。口径：
    /// - 应填报分店 = 启用中的分店，排除测试店与仓库（配置 SeasonalCards:StatsExcludedStoreCodes，缺省 1006 仓库、1042 测试店）；
    /// - 每个 分店 + 年份 + 节日 + 供应商 以最新一批为当前生效值（见 SeasonalCardBatchResolver）；
    /// - 「已填报」只看该分店在这个年份 + 节日下有没有任一生效数据，供应商 / 价格筛选只影响数量，
    ///   否则不卖某供应商贺卡的分店会被误判为未填报。
    /// 权限由控制器的 SeasonalCards.Remaining.ViewAllStores 策略把关（管理员隐含），这里不再按分店收口。
    /// </summary>
    public class SeasonalCardStatsReactService : ISeasonalCardStatsReactService
    {
        internal static readonly IReadOnlyList<string> DefaultExcludedStoreCodes = new[] { "1006", "1042" };

        private readonly ISqlSugarClient _db;
        private readonly HashSet<string> _excludedStoreCodes;

        public SeasonalCardStatsReactService(SqlSugarContext context, IConfiguration configuration)
        {
            _db = context.Db;
            var configured = configuration
                .GetSection("SeasonalCards:StatsExcludedStoreCodes")
                .Get<string[]>();
            _excludedStoreCodes = (configured ?? DefaultExcludedStoreCodes.ToArray())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ApiResponse<SeasonalCardStatsSummaryDto>> GetSummaryAsync(
            SeasonalCardStatsQueryDto query
        )
        {
            var validation = ValidateYearAndType(query.SeasonYear, query.CardType);
            if (validation != null)
            {
                return ApiResponse<SeasonalCardStatsSummaryDto>.Error(validation.Value.Message, validation.Value.Code);
            }

            var activeStores = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && item.IsActive)
                .OrderBy(item => item.StoreCode)
                .Select(item => new SeasonalCardStatsStoreRefDto
                {
                    StoreCode = item.StoreCode,
                    StoreName = item.StoreName,
                })
                .ToListAsync();
            var excludedStores = activeStores
                .Where(item => _excludedStoreCodes.Contains(item.StoreCode))
                .ToList();
            var requestedStoreCodes = (query.StoreCodes ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stores = activeStores
                .Where(item => !_excludedStoreCodes.Contains(item.StoreCode))
                .Where(item => requestedStoreCodes.Count == 0 || requestedStoreCodes.Contains(item.StoreCode))
                .ToList();
            var storeCodes = stores.Select(item => item.StoreCode).ToList();

            var rows = storeCodes.Count == 0
                ? new List<SeasonalCardRemainingSubmission>()
                : await _db.Queryable<SeasonalCardRemainingSubmission>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.SeasonYear == query.SeasonYear
                        && item.CardType == query.CardType
                        && storeCodes.Contains(item.StoreCode)
                    )
                    .ToListAsync();
            var rowsByStore = rows.ToLookup(item => item.StoreCode, StringComparer.OrdinalIgnoreCase);
            var supplierFilter = SeasonalCardBatchResolver.SupplierKey(query.LocalSupplierCode);
            var priceOptions = Enum.GetValues<SeasonalCardPriceOptionType>();

            var storeRows = new List<SeasonalCardStatsStoreRowDto>();
            var countedLines = new List<SeasonalCardRemainingSubmission>();
            foreach (var store in stores)
            {
                var effective = SeasonalCardBatchResolver.ResolveEffectiveLines(rowsByStore[store.StoreCode]);
                var filtered = effective
                    .Where(row =>
                        supplierFilter.Length == 0
                        || string.Equals(
                            SeasonalCardBatchResolver.SupplierKey(row.LocalSupplierCode),
                            supplierFilter,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Where(row => !query.PriceOption.HasValue || row.PriceOption == query.PriceOption.Value)
                    .ToList();
                countedLines.AddRange(filtered);
                var latest = effective
                    .OrderByDescending(row => row.SubmittedAt)
                    .FirstOrDefault();

                storeRows.Add(new SeasonalCardStatsStoreRowDto
                {
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    IsFilled = effective.Count > 0,
                    Prices = BuildPriceQuantities(filtered, priceOptions),
                    TotalQuantity = filtered.Sum(row => row.RemainingQuantity),
                    TotalAmount = filtered.Sum(SeasonalCardBatchResolver.LineAmount),
                    Suppliers = filtered
                        .GroupBy(row => SeasonalCardBatchResolver.SupplierKey(row.LocalSupplierCode), StringComparer.OrdinalIgnoreCase)
                        .Select(group => ToSupplierRef(group.Key, group.First().SupplierName))
                        .OrderBy(item => item.SupplierName, StringComparer.CurrentCulture)
                        .ToList(),
                    LastSubmittedAt = latest?.SubmittedAt,
                    LastSubmittedByName = latest?.SubmittedByName,
                });
            }

            var filledCount = storeRows.Count(item => item.IsFilled);
            return ApiResponse<SeasonalCardStatsSummaryDto>.OK(new SeasonalCardStatsSummaryDto
            {
                SeasonYear = query.SeasonYear,
                CardType = query.CardType,
                CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(query.CardType),
                StoreCount = storeRows.Count,
                FilledStoreCount = filledCount,
                UnfilledStoreCount = storeRows.Count - filledCount,
                TotalQuantity = countedLines.Sum(row => row.RemainingQuantity),
                TotalAmount = countedLines.Sum(SeasonalCardBatchResolver.LineAmount),
                Stores = storeRows,
                PriceTotals = BuildPriceQuantities(countedLines, priceOptions),
                SupplierTotals = countedLines
                    .GroupBy(row => SeasonalCardBatchResolver.SupplierKey(row.LocalSupplierCode), StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var supplier = ToSupplierRef(group.Key, group.First().SupplierName);
                        return new SeasonalCardStatsSupplierTotalDto
                        {
                            LocalSupplierCode = supplier.LocalSupplierCode,
                            SupplierName = supplier.SupplierName,
                            StoreCount = group
                                .Select(row => row.StoreCode)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count(),
                            Quantity = group.Sum(row => row.RemainingQuantity),
                            Amount = group.Sum(SeasonalCardBatchResolver.LineAmount),
                        };
                    })
                    .OrderByDescending(item => item.Quantity)
                    .ThenBy(item => item.SupplierName, StringComparer.CurrentCulture)
                    .ToList(),
                UnfilledStores = storeRows
                    .Where(item => !item.IsFilled)
                    .Select(item => new SeasonalCardStatsStoreRefDto
                    {
                        StoreCode = item.StoreCode,
                        StoreName = item.StoreName,
                    })
                    .ToList(),
                ExcludedStores = excludedStores,
            });
        }

        public async Task<ApiResponse<SeasonalCardStatsStoreDetailDto>> GetStoreDetailAsync(
            string storeCode,
            SeasonalCardStatsStoreDetailQueryDto query
        )
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<SeasonalCardStatsStoreDetailDto>.Error("分店代码不能为空", "STORE_CODE_REQUIRED");
            }

            var validation = ValidateYearAndType(query.SeasonYear, query.CardType);
            if (validation != null)
            {
                return ApiResponse<SeasonalCardStatsStoreDetailDto>.Error(validation.Value.Message, validation.Value.Code);
            }

            var code = storeCode.Trim();
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => !item.IsDeleted && item.StoreCode == code);
            if (store == null)
            {
                return ApiResponse<SeasonalCardStatsStoreDetailDto>.Error("分店不存在", "STORE_NOT_FOUND");
            }

            var previousYear = query.SeasonYear - 1;
            var rows = await _db.Queryable<SeasonalCardRemainingSubmission>()
                .Where(item =>
                    !item.IsDeleted
                    && item.StoreCode == code
                    && item.CardType == query.CardType
                    && (item.SeasonYear == query.SeasonYear || item.SeasonYear == previousYear)
                )
                .ToListAsync();
            var currentYearRows = rows.Where(item => item.SeasonYear == query.SeasonYear).ToList();
            var effective = SeasonalCardBatchResolver.ResolveEffectiveLines(currentYearRows);
            var effectiveGuids = effective
                .Select(row => row.SubmissionGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousEffective = SeasonalCardBatchResolver.ResolveEffectiveLines(
                rows.Where(item => item.SeasonYear == previousYear)
            );

            return ApiResponse<SeasonalCardStatsStoreDetailDto>.OK(new SeasonalCardStatsStoreDetailDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                SeasonYear = query.SeasonYear,
                CardType = query.CardType,
                CardTypeName = SeasonalCardCatalogSeedData.GetCardTypeName(query.CardType),
                IsFilled = effective.Count > 0,
                TotalQuantity = effective.Sum(row => row.RemainingQuantity),
                TotalAmount = effective.Sum(SeasonalCardBatchResolver.LineAmount),
                CurrentBatches = effective
                    .GroupBy(row => SeasonalCardBatchResolver.SupplierKey(row.LocalSupplierCode), StringComparer.OrdinalIgnoreCase)
                    .Select(group => SeasonalCardBatchResolver.ToBatchDto(group.ToList(), store.StoreName, true))
                    .OrderBy(item => item.SupplierName, StringComparer.CurrentCulture)
                    .ToList(),
                // 历史行一行一批；某批只要还有一行生效就标为当前生效。
                History = SeasonalCardBatchResolver.GroupIntoBatches(currentYearRows)
                    .Select(batch => SeasonalCardBatchResolver.ToBatchDto(
                        batch,
                        store.StoreName,
                        batch.Any(row => effectiveGuids.Contains(row.SubmissionGuid))
                    ))
                    .ToList(),
                PreviousYearTotalQuantity = previousEffective.Count == 0
                    ? null
                    : previousEffective.Sum(row => row.RemainingQuantity),
            });
        }

        private static (string Message, string Code)? ValidateYearAndType(int seasonYear, SeasonalCardType cardType)
        {
            if (seasonYear <= 0)
            {
                return ("季节年份必须大于 0", "INVALID_SEASON_YEAR");
            }

            if (!Enum.IsDefined(cardType))
            {
                return ("节日无效", "INVALID_CARD_TYPE");
            }

            return null;
        }

        private static List<SeasonalCardStatsPriceQuantityDto> BuildPriceQuantities(
            IReadOnlyCollection<SeasonalCardRemainingSubmission> lines,
            IEnumerable<SeasonalCardPriceOptionType> priceOptions
        ) =>
            priceOptions
                .Select(option =>
                {
                    var optionLines = lines.Where(row => row.PriceOption == option).ToList();
                    return new SeasonalCardStatsPriceQuantityDto
                    {
                        PriceOption = option,
                        PriceLabel = SeasonalCardCatalogSeedData.GetPriceOptionName(option),
                        Quantity = optionLines.Sum(row => row.RemainingQuantity),
                        Amount = optionLines.Sum(SeasonalCardBatchResolver.LineAmount),
                    };
                })
                .ToList();

        private static SeasonalCardStatsSupplierRefDto ToSupplierRef(string supplierKey, string? supplierName) =>
            supplierKey.Length == 0
                ? new SeasonalCardStatsSupplierRefDto
                {
                    LocalSupplierCode = null,
                    SupplierName = SeasonalCardBatchResolver.UnassignedSupplierName,
                }
                : new SeasonalCardStatsSupplierRefDto
                {
                    LocalSupplierCode = supplierKey,
                    SupplierName = string.IsNullOrWhiteSpace(supplierName) ? supplierKey : supplierName,
                };
    }
}
