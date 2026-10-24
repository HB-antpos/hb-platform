using System.Net;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>参考商品候选：我方商品 + 进价 + 历史窗口内（全部分店）的销量，用于打分与弹窗展示。</summary>
public sealed class SupplierPreorderRefCandidate
{
    public string ProductCode { get; init; } = string.Empty;
    public string? ItemNumber { get; init; }
    public string? Barcode { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal? PurchasePrice { get; init; }
    public string? LocalSupplierCode { get; init; }
    public string? ProductImage { get; init; }
    /// <summary>历史窗口内全部分店净销量。</summary>
    public decimal SalesQty { get; init; }
    /// <summary>历史窗口内全部分店进货量（总仓配货 + 供应商进货单）。</summary>
    public decimal PurchaseQty { get; init; }
}

/// <summary>一次打分：Eligible = 过了两个硬门槛（名称 ≥ 0.5、进价差 ≤ 30%）；Score 只在 Eligible 时有意义。</summary>
public sealed record SupplierPreorderRefScore(decimal Score, decimal NameScore, decimal PriceScore, bool Eligible);

public sealed record SupplierPreorderRefPick(SupplierPreorderRefCandidate Candidate, SupplierPreorderRefScore Score);

/// <summary>
/// 参考商品打分（方案 5.1，10-23 用生产数据核对过的口径，见 5.2）：
/// 名称相似度占 70%（先去掉颜色、Asst/Pack 这类词和尺寸数字，再用 Dice 系数比较词的重合），进价接近占 30%（1 − 差幅 / 30%）。
/// 硬门槛：名称 ≥ 0.5 且进价差 &lt; 30%，不满足的直接排除，不靠另一项凑分；我方没有进价时进价项按中性 0.5 计。
/// 得分 ≥ 0.6 自动带上（系统推荐）。不用箱规：我方商品表的箱规（MiddlePackageQuantity）实测全空。
/// </summary>
public static class SupplierPreorderRefMatcher
{
    public const decimal AutoThreshold = 0.6m;
    public const decimal NameThreshold = 0.5m;
    public const decimal MaxPriceDiff = 0.3m;
    public const decimal NameWeight = 0.7m;
    public const decimal PriceWeight = 0.3m;

    private static readonly HashSet<string> Colors = new(
        "red green gold silver white black blue pink purple champagne rose copper bronze clear natural burgundy navy teal ivory cream grey gray multi multicolour multicolor iridescent pastel brown orange yellow aqua mint warm cool"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> StopWords = new(
        "asst assorted asstd of the and with w/ x pc pcs pk pack set kids adult adults new deluxe"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    // 尺寸、件数：10cm、2.5 m、6pk、12 pcs
    private static readonly Regex SizeUnit = new(@"\d+(\.\d+)?\s*(cm|mm|m|in|inch|pk|pc|pcs|l|ml|g|kg)\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Pk 6、6 Pk、30x40cm
    private static readonly Regex PackAndDims = new(@"\bpk\s*\d+\b|\b\d+\s*pk\b|\b\d+x\d+\w*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NonLetters = new(@"[^a-z ]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>名称分词：小写、HTML 实体还原、去尺寸/件数、只留字母，再去颜色词和停用词、去单字母。</summary>
    public static HashSet<string> Tokenize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var text = WebUtility.HtmlDecode(name).ToLowerInvariant().Replace('&', ' ');
        text = SizeUnit.Replace(text, " ");
        text = PackAndDims.Replace(text, " ");
        text = NonLetters.Replace(text, " ");
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !Colors.Contains(t) && !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Dice 系数：2|A∩B| / (|A| + |B|)。</summary>
    public static decimal Dice(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0m;
        }

        var common = a.Count(b.Contains);
        return Math.Round(2m * common / (a.Count + b.Count), 4);
    }

    /// <summary>进价接近程度：1 − 差幅 / 30%（差幅以我方进价为分母），≤ 0 视为超出门槛；任一方没有进价时为中性 0.5。</summary>
    public static decimal PriceScore(decimal itemPrice, decimal? candidatePrice)
    {
        if (candidatePrice is not > 0 || itemPrice <= 0)
        {
            return 0.5m;
        }

        var diff = Math.Abs(itemPrice - candidatePrice.Value) / candidatePrice.Value;
        return Math.Round(Math.Max(0m, 1m - diff / MaxPriceDiff), 4);
    }

    public static SupplierPreorderRefScore Score(IReadOnlySet<string> itemTokens, decimal itemPrice, IReadOnlySet<string> candidateTokens, decimal? candidatePrice)
    {
        var name = Dice(itemTokens, candidateTokens);
        var price = PriceScore(itemPrice, candidatePrice);
        if (name < NameThreshold || price <= 0m)
        {
            return new SupplierPreorderRefScore(0m, name, price, false);
        }

        return new SupplierPreorderRefScore(Math.Round(NameWeight * name + PriceWeight * price, 3), name, price, true);
    }

    /// <summary>预先给候选分好词并建倒排索引：订单表上千款 × 候选几千个时，只和至少有一个相同词的候选比较。</summary>
    public sealed class CandidateIndex
    {
        private readonly List<(SupplierPreorderRefCandidate Candidate, HashSet<string> Tokens)> _entries;
        private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);

        public CandidateIndex(IEnumerable<SupplierPreorderRefCandidate> candidates)
        {
            _entries = candidates.Select(c => (c, Tokenize(c.Name))).ToList();
            for (var i = 0; i < _entries.Count; i++)
            {
                foreach (var token in _entries[i].Tokens)
                {
                    if (!_byToken.TryGetValue(token, out var list))
                    {
                        _byToken[token] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }
        }

        public int Count => _entries.Count;

        /// <summary>得分最高的前 N 个（只含过了门槛的）；同分时销量高的在前，再按商品编码。</summary>
        public List<SupplierPreorderRefPick> Top(string itemName, decimal itemPrice, int limit, ISet<string>? exclude = null)
        {
            var tokens = Tokenize(itemName);
            var ids = new HashSet<int>();
            foreach (var token in tokens)
            {
                if (_byToken.TryGetValue(token, out var list))
                {
                    ids.UnionWith(list);
                }
            }

            return ids
                .Select(i => _entries[i])
                .Where(e => exclude is null || !exclude.Contains(e.Candidate.ProductCode))
                .Select(e => new SupplierPreorderRefPick(e.Candidate, Score(tokens, itemPrice, e.Tokens, e.Candidate.PurchasePrice)))
                .Where(p => p.Score.Eligible)
                .OrderByDescending(p => p.Score.Score)
                .ThenByDescending(p => p.Candidate.SalesQty)
                .ThenBy(p => p.Candidate.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        /// <summary>系统推荐：最高分 ≥ 0.6 才自动带上，否则不带。</summary>
        public SupplierPreorderRefPick? Recommend(string itemName, decimal itemPrice, ISet<string>? exclude = null)
        {
            var best = Top(itemName, itemPrice, 1, exclude).FirstOrDefault();
            return best is not null && best.Score.Score >= AutoThreshold ? best : null;
        }
    }

    /// <summary>手工搜索出的商品也给个分（不过门槛也显示，只是不推荐），方便采购判断像不像。</summary>
    public static SupplierPreorderRefScore ScoreCandidate(string itemName, decimal itemPrice, SupplierPreorderRefCandidate candidate) =>
        Score(Tokenize(itemName), itemPrice, Tokenize(candidate.Name), candidate.PurchasePrice);
}
