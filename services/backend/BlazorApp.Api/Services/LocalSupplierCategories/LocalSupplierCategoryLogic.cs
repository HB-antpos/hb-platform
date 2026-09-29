using System.Text;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

/// <summary>
/// 供应商分类的共享常量。供应商 200 是 Hot Bargain 自营，其供应商分类即仓库分类，不参与网站采集。
/// </summary>
public static class LocalSupplierCategoryConstants
{
    public const string HotBargainSupplierCode = "200";
    public const int MaxPathDepth = 8;
    public const int MaxExternalKeyLength = 400;
    public const int MaxFullPathLength = 1000;
    public const string PathSeparator = " > ";

    /// <summary>
    /// 与商品写入口径一致：空供应商码视为 200（见 ProductReactService.NormalizeLocalSupplierCode）。
    /// </summary>
    public static bool IsHotBargain(string? supplierCode) =>
        string.IsNullOrWhiteSpace(supplierCode)
        || string.Equals(supplierCode.Trim(), HotBargainSupplierCode, StringComparison.OrdinalIgnoreCase);
}

public static class LocalSupplierCategorySources
{
    public const string Website = "website";
    public const string Manual = "manual";
    public const string Warehouse = "warehouse";
}

/// <summary>
/// 分类外部键归一化：与扩展端 category-path.js 的 normalizeCategoryKey 同一规则，服务端再做一次兜底。
/// </summary>
public static partial class LocalSupplierCategoryKeyNormalizer
{
    /// <summary>
    /// 归一化站点分类键；非法（空、首页、超长）时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    public static string Normalize(string? rawKey, IReadOnlyCollection<string>? keepQueryParams = null)
    {
        var value = rawKey?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException("分类键不能为空。", nameof(rawKey));
        }

        // 允许传入完整 URL：只取路径与查询串。
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
        {
            value = absolute.PathAndQuery;
        }

        var hashIndex = value.IndexOf('#');
        if (hashIndex >= 0)
        {
            value = value[..hashIndex];
        }

        var queryIndex = value.IndexOf('?');
        var path = queryIndex >= 0 ? value[..queryIndex] : value;
        var query = queryIndex >= 0 ? value[(queryIndex + 1)..] : string.Empty;

        path = SafeUnescape(path).ToLowerInvariant();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }
        path = DuplicateSlashRegex().Replace(path, "/");
        // 剥离 WooCommerce 风格分页尾段，翻页不应产生新分类。
        path = PagePathSuffixRegex().Replace(path, string.Empty);
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }
        if (path.Length == 0)
        {
            path = "/";
        }

        var keptQuery = BuildKeptQuery(query, keepQueryParams);
        var normalized = keptQuery.Length > 0 ? $"{path}?{keptQuery}" : path;

        if (normalized == "/")
        {
            throw new ArgumentException("分类键不能是站点首页。", nameof(rawKey));
        }
        if (normalized.Length > LocalSupplierCategoryConstants.MaxExternalKeyLength)
        {
            throw new ArgumentException("分类键过长。", nameof(rawKey));
        }

        return normalized;
    }

    private static string BuildKeptQuery(string query, IReadOnlyCollection<string>? keepQueryParams)
    {
        if (string.IsNullOrEmpty(query) || keepQueryParams == null || keepQueryParams.Count == 0)
        {
            return string.Empty;
        }

        var allowed = new HashSet<string>(keepQueryParams, StringComparer.OrdinalIgnoreCase);
        var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=');
            var name = SafeUnescape(equalsIndex >= 0 ? part[..equalsIndex] : part).Trim();
            if (name.Length == 0 || !allowed.Contains(name))
            {
                continue;
            }

            var paramValue = equalsIndex >= 0 ? SafeUnescape(part[(equalsIndex + 1)..]) : string.Empty;
            // 同名参数只保留首个，保证键稳定。
            pairs.TryAdd(name.ToLowerInvariant(), paramValue.Trim().ToLowerInvariant());
        }

        return string.Join('&', pairs.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    [GeneratedRegex("/{2,}")]
    private static partial Regex DuplicateSlashRegex();

    [GeneratedRegex(@"/page/\d+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PagePathSuffixRegex();
}

/// <summary>
/// 促销/横切分类判定：只支持 * 通配（其余字符按字面量），与扩展端 matchUrlPattern 同一安全约束。
/// </summary>
public static partial class LocalSupplierCategoryPromotionRule
{
    /// <summary>
    /// 默认促销模式。不用 *sale*，避免误伤 wholesale 等正常分类。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = new[]
    {
        "clearance*",
        "*-clearance",
        "sale",
        "sale-*",
        "*-sale",
        "on-sale*",
        "specials*",
        "special-offers*",
        "new",
        "new-arrivals*",
        "new-in*",
        "new-releases*",
        "whats-new*",
        "what-s-new*",
        "new-products*",
        "best-sellers*",
        "bestsellers*",
        "shop-by-*",
        "gift-ideas*",
        "trending*",
        "promotions*",
        "deals*",
        "hot-deals*",
    };

    public const int MaxPatternCount = 50;
    public const int MaxPatternLength = 100;

    /// <summary>
    /// 合并默认模式与供应商配置的附加模式；非法模式（超长、含非允许字符）被丢弃。
    /// </summary>
    public static IReadOnlyList<string> Combine(IEnumerable<string>? extraPatterns)
    {
        var result = new List<string>(DefaultPatterns);
        foreach (var raw in extraPatterns ?? Array.Empty<string>())
        {
            var pattern = raw?.Trim().ToLowerInvariant();
            if (IsSafePattern(pattern) && !result.Contains(pattern!))
            {
                result.Add(pattern!);
            }
        }

        return result;
    }

    public static bool IsSafePattern(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern)
        && pattern.Length <= MaxPatternLength
        && SafePatternRegex().IsMatch(pattern);

    public static bool IsPromotional(string? name, string? externalKey, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        foreach (var candidate in BuildCandidates(name, externalKey))
        {
            foreach (var pattern in patterns)
            {
                if (GlobMatches(pattern, candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 候选值：键的每一段、完整键（去首斜杠与查询串）、名称 slug。
    /// </summary>
    internal static IEnumerable<string> BuildCandidates(string? name, string? externalKey)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(externalKey))
        {
            var path = externalKey.Split('?')[0].Trim('/').ToLowerInvariant();
            if (path.Length > 0)
            {
                candidates.Add(Slugify(path.Replace('/', '-')));
                candidates.AddRange(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Slugify));
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            candidates.Add(Slugify(name));
        }

        return candidates.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal);
    }

    internal static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousDash = false;
            }
            else if (ch == '\'')
            {
                // what's new -> whats-new
                continue;
            }
            else if (!previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    internal static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    [GeneratedRegex(@"^[a-z0-9*_/ -]+$")]
    private static partial Regex SafePatternRegex();
}

/// <summary>
/// 自动归类候选：某货号出现过的一个分类。
/// </summary>
public sealed record LocalSupplierCategoryCandidate(
    string CategoryGuid,
    int Depth,
    bool IsPromotional,
    bool IsActive,
    bool IsDeleted,
    DateTime LastSeenAt
);

/// <summary>
/// 自动归类规则：同一货号出现在多个分类时，取最深（最具体）的非促销分类；并列取最近看到的，再按 GUID 定序保证确定性。
/// </summary>
public static class LocalSupplierCategoryResolver
{
    public static string? Resolve(IEnumerable<LocalSupplierCategoryCandidate> candidates) =>
        candidates
            .Where(candidate => !candidate.IsPromotional && candidate.IsActive && !candidate.IsDeleted)
            .OrderByDescending(candidate => candidate.Depth)
            .ThenByDescending(candidate => candidate.LastSeenAt)
            .ThenBy(candidate => candidate.CategoryGuid, StringComparer.Ordinal)
            .Select(candidate => candidate.CategoryGuid)
            .FirstOrDefault();

    /// <summary>
    /// 拼接完整路径；超长时从根侧截断并保留叶子，保证列表始终能看到最具体的分类名。
    /// </summary>
    public static string BuildFullPath(IReadOnlyList<string> names)
    {
        var joined = string.Join(LocalSupplierCategoryConstants.PathSeparator, names);
        if (joined.Length <= LocalSupplierCategoryConstants.MaxFullPathLength)
        {
            return joined;
        }

        return "…" + joined[^(LocalSupplierCategoryConstants.MaxFullPathLength - 1)..];
    }

    /// <summary>
    /// 分类名清洗：折叠空白、去掉尾部商品计数 "(123)"、截断到 200 字符。
    /// </summary>
    public static string NormalizeName(string? name)
    {
        var value = Regex.Replace(name ?? string.Empty, @"\s+", " ").Trim();
        value = Regex.Replace(value, @"\s*\(\d+\)$", string.Empty).Trim();
        return value.Length <= 200 ? value : value[..200];
    }
}
