using System.Text.RegularExpressions;

namespace BlazorApp.Api.Features.WarehousePicking;

/// <summary>货位编码「区-排-列-层」解析结果；列、层可省略时为 0；RowLabel 保留排号原文（如 "03"）。</summary>
internal sealed record WarehouseParsedLocation(string Zone, int Row, string RowLabel, int Bay, int Level);

/// <summary>参与走位排序的订单行：货位为拣货单同口径的配货位文本（多个以逗号连接）。</summary>
internal sealed record WarehouseRouteLine(
    string DetailGuid,
    string? LocationCode,
    string? ItemNumber,
    string ProductCode,
    decimal OrderedQuantity,
    string? ProductName = null,
    string? Barcode = null,
    int? MinOrderQuantity = null
);

/// <summary>
/// 拣货走位顺序与按品种数分段。排序口径与移动端 pick-math.ts 的 M 型一致：
/// 先区、再排，排内列号从小到大、同列按层；编码不规范的在规范货位之后按编码自然排序；未绑定货位的最后，按货号兜底。
/// 两端各写一份，靠同一组测试用例对齐。
/// </summary>
internal static partial class WarehousePickingRoute
{
    public const int MaxPickersPerOrder = 10;

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex ZonePattern();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex("[0-9]+|[^0-9]+")]
    private static partial Regex NaturalChunkPattern();

    /// <summary>一行可能绑定多个配货位（按编码排序后以逗号连接）：走位按第一个算。</summary>
    public static string PrimaryLocation(string? locationCode) =>
        locationCode?.Split(',')[0].Trim() ?? string.Empty;

    public static bool HasLocation(string? locationCode) => PrimaryLocation(locationCode).Length > 0;

    public static WarehouseParsedLocation? ParseLocationCode(string? raw)
    {
        var code = PrimaryLocation(raw);
        if (code.Length == 0)
        {
            return null;
        }

        var parts = code.Split('-').Select(part => part.Trim()).ToArray();
        if (parts.Length is < 2 or > 4
            || !ZonePattern().IsMatch(parts[0])
            || parts.Skip(1).Any(part => !NumberPattern().IsMatch(part)))
        {
            return null;
        }

        // 编号超出 int 范围的按不规范处理，不让解析抛异常。
        var numbers = new int[3];
        for (var index = 1; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out numbers[index - 1]))
            {
                return null;
            }
        }

        return new WarehouseParsedLocation(parts[0].ToUpperInvariant(), numbers[0], parts[1], numbers[1], numbers[2]);
    }

    public static List<WarehouseRouteLine> SortByRoute(IEnumerable<WarehouseRouteLine> lines)
    {
        var list = lines.ToList();
        list.Sort(CompareByRoute);
        return list;
    }

    private static int CompareByRoute(WarehouseRouteLine a, WarehouseRouteLine b)
    {
        var locationA = PrimaryLocation(a.LocationCode);
        var locationB = PrimaryLocation(b.LocationCode);
        if ((locationA.Length == 0) != (locationB.Length == 0))
        {
            return locationA.Length > 0 ? -1 : 1;
        }

        if (locationA.Length > 0)
        {
            var parsedA = ParseLocationCode(locationA);
            var parsedB = ParseLocationCode(locationB);
            if ((parsedA == null) != (parsedB == null))
            {
                return parsedA != null ? -1 : 1;
            }

            if (parsedA != null && parsedB != null)
            {
                var byRoute = CompareNatural(parsedA.Zone, parsedB.Zone);
                if (byRoute == 0) byRoute = parsedA.Row.CompareTo(parsedB.Row);
                if (byRoute == 0) byRoute = parsedA.Bay.CompareTo(parsedB.Bay);
                if (byRoute == 0) byRoute = parsedA.Level.CompareTo(parsedB.Level);
                if (byRoute != 0)
                {
                    return byRoute;
                }
            }

            var byLocation = CompareNatural(locationA, locationB);
            if (byLocation != 0)
            {
                return byLocation;
            }
        }

        var byItem = CompareNatural(
            string.IsNullOrEmpty(a.ItemNumber) ? a.ProductCode : a.ItemNumber,
            string.IsNullOrEmpty(b.ItemNumber) ? b.ProductCode : b.ItemNumber
        );
        // 货号也相同（重复商品）时按明细号定序，保证预览与保存两次计算结果一致。
        return byItem != 0 ? byItem : string.CompareOrdinal(a.DetailGuid, b.DetailGuid);
    }

    /// <summary>自然排序：数字段按数值比较（A-2 在 A-10 前），其余不区分大小写。</summary>
    internal static int CompareNatural(string? left, string? right)
    {
        var chunksA = NaturalChunkPattern().Matches(left ?? string.Empty);
        var chunksB = NaturalChunkPattern().Matches(right ?? string.Empty);
        for (var index = 0; index < Math.Min(chunksA.Count, chunksB.Count); index++)
        {
            var chunkA = chunksA[index].Value;
            var chunkB = chunksB[index].Value;
            int result;
            if (char.IsDigit(chunkA[0]) && char.IsDigit(chunkB[0]))
            {
                var trimmedA = chunkA.TrimStart('0');
                var trimmedB = chunkB.TrimStart('0');
                result = trimmedA.Length != trimmedB.Length
                    ? trimmedA.Length.CompareTo(trimmedB.Length)
                    : string.CompareOrdinal(trimmedA, trimmedB);
            }
            else
            {
                result = string.Compare(chunkA, chunkB, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return chunksA.Count.CompareTo(chunksB.Count);
    }

    /// <summary>按品种数平均分：除不尽的余数从排在前面的人开始每人多一个。</summary>
    public static List<int> SplitEvenly(int lineCount, int pickerCount)
    {
        if (pickerCount <= 0)
        {
            return new List<int>();
        }

        var baseCount = lineCount / pickerCount;
        var remainder = lineCount % pickerCount;
        return Enumerable.Range(0, pickerCount)
            .Select(index => baseCount + (index < remainder ? 1 : 0))
            .ToList();
    }

    /// <summary>
    /// 按走位顺序把行切成首尾相接的连续段，每人一段，保证每人走的是一条不重叠的路线。
    /// counts 之和必须等于行数（调用方已校验）。
    /// </summary>
    public static List<List<WarehouseRouteLine>> SplitContiguous(
        IReadOnlyList<WarehouseRouteLine> sortedLines,
        IReadOnlyList<int> counts
    )
    {
        var segments = new List<List<WarehouseRouteLine>>(counts.Count);
        var offset = 0;
        foreach (var count in counts)
        {
            segments.Add(sortedLines.Skip(offset).Take(count).ToList());
            offset += count;
        }

        return segments;
    }
}
