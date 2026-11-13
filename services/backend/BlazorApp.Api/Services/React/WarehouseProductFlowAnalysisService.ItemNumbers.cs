using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 批量货号（Excel 粘贴）解析结果：把粘贴清单映射到商品编码，
    /// 供候选列表按粘贴顺序分页、统计未找到的货号。
    /// </summary>
    internal sealed class WarehouseProductFlowItemNumberResolution
    {
        public List<string> ItemNumbers { get; init; } = new();

        /// <summary>商品编码 → 命中清单中最靠前的序号（0 起），用于按粘贴顺序排列。</summary>
        public Dictionary<string, int> RankByCode { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>商品编码 → 该商品命中的清单货号，用于计算「已匹配 / 未找到」。</summary>
        public Dictionary<string, HashSet<string>> TokensByCode { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>按最终筛选结果（叠加分类、供应商、货柜等条件后）统计匹配情况。</summary>
        public WarehouseProductFlowItemNumberMatchDto BuildMatch(IEnumerable<string> filteredCodes)
        {
            var matchedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in filteredCodes)
            {
                if (TokensByCode.TryGetValue(code, out var tokens))
                    matchedTokens.UnionWith(tokens);
            }
            var unmatched = ItemNumbers.Where(item => !matchedTokens.Contains(item)).ToList();
            return new WarehouseProductFlowItemNumberMatchDto
            {
                RequestedCount = ItemNumbers.Count,
                MatchedCount = ItemNumbers.Count - unmatched.Count,
                UnmatchedItemNumbers = unmatched,
            };
        }
    }

    public partial class WarehouseProductFlowAnalysisService
    {
        /// <summary>一次最多 2000 个货号，与前端粘贴弹窗上限一致。</summary>
        internal const int MaxItemNumberFilterCount = 2000;

        /// <summary>去首尾空格、去空值、不区分大小写去重，并保留粘贴顺序。</summary>
        internal static List<string> NormalizeItemNumbers(IEnumerable<string?>? values)
        {
            return NormalizeCodes(values);
        }

        internal static bool IsAnyItemNumberMatchMode(string? mode)
        {
            return string.Equals(mode?.Trim(), "any", StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, int> BuildItemNumberRankMap(List<string> itemNumbers)
        {
            var rankByToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < itemNumbers.Count; index++)
                rankByToken.TryAdd(itemNumbers[index], index);
            return rankByToken;
        }

        /// <summary>
        /// 判断一个商品是否命中批量货号，返回命中的清单序号（取最靠前的），未命中返回 null。
        /// 仅货号模式只比较货号；any 模式额外比较条码和商品编码。
        /// </summary>
        internal static int? FindItemNumberRank(
            string? productCode,
            string? itemNumber,
            string? barcode,
            Dictionary<string, int> rankByToken,
            bool matchAny,
            HashSet<string>? matchedTokens = null
        )
        {
            int? best = null;
            void Check(string? value)
            {
                var token = value?.Trim();
                if (string.IsNullOrEmpty(token) || !rankByToken.TryGetValue(token, out var rank))
                    return;
                matchedTokens?.Add(token);
                if (best == null || rank < best)
                    best = rank;
            }

            Check(itemNumber);
            if (matchAny)
            {
                Check(barcode);
                Check(productCode);
            }
            return best;
        }

        /// <summary>
        /// 把批量货号解析成商品编码。按货号（any 模式加条码、商品编码）分批精确 IN 查询，
        /// 走索引且不生成超长 SQL；结果在内存中再按去空格、不区分大小写复核一次。
        /// </summary>
        private async Task<WarehouseProductFlowItemNumberResolution> ResolveItemNumberFilterAsync(
            List<string> itemNumbers,
            bool matchAny,
            bool hasActiveProductCodeDuplicates
        )
        {
            var resolution = new WarehouseProductFlowItemNumberResolution { ItemNumbers = itemNumbers };
            var rankByToken = BuildItemNumberRankMap(itemNumbers);
            var rows = new List<(string? ProductCode, string? ItemNumber, string? Barcode)>();

            foreach (var batch in BatchCodes(itemNumbers))
            {
                var values = batch;
                var byItemNumber = await BuildCandidateProductQuery(hasActiveProductCodeDuplicates)
                    .Where(product => product.ItemNumber != null && values.Contains(product.ItemNumber))
                    .Select(product => new { product.ProductCode, product.ItemNumber, product.Barcode })
                    .ToListAsync();
                rows.AddRange(byItemNumber.Select(row => (row.ProductCode, row.ItemNumber, row.Barcode)));

                if (!matchAny)
                    continue;

                var byBarcode = await BuildCandidateProductQuery(hasActiveProductCodeDuplicates)
                    .Where(product => product.Barcode != null && values.Contains(product.Barcode))
                    .Select(product => new { product.ProductCode, product.ItemNumber, product.Barcode })
                    .ToListAsync();
                rows.AddRange(byBarcode.Select(row => (row.ProductCode, row.ItemNumber, row.Barcode)));

                var byProductCode = await BuildCandidateProductQuery(hasActiveProductCodeDuplicates)
                    .Where(product => values.Contains(product.ProductCode!))
                    .Select(product => new { product.ProductCode, product.ItemNumber, product.Barcode })
                    .ToListAsync();
                rows.AddRange(byProductCode.Select(row => (row.ProductCode, row.ItemNumber, row.Barcode)));
            }

            foreach (var row in rows)
            {
                var code = row.ProductCode?.Trim();
                if (string.IsNullOrEmpty(code))
                    continue;
                var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rank = FindItemNumberRank(code, row.ItemNumber, row.Barcode, rankByToken, matchAny, tokens);
                if (rank == null)
                    continue;
                if (!resolution.RankByCode.TryGetValue(code, out var existing) || rank < existing)
                    resolution.RankByCode[code] = rank.Value;
                if (!resolution.TokensByCode.TryGetValue(code, out var codeTokens))
                {
                    codeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    resolution.TokensByCode[code] = codeTokens;
                }
                codeTokens.UnionWith(tokens);
            }
            return resolution;
        }
    }
}
