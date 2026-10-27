using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>修订版里一款商品的对比结果。Existing 为空 = 新增；Row 为空 = 下架。</summary>
public sealed class SupplierPreorderRevisionDiff
{
    public string Kind { get; init; } = SupplierPreorderRevisionRules.Unchanged;
    public SupplierPreorderFormItem? Existing { get; init; }
    public SupplierPreorderNormalizedRow? Row { get; init; }
    public List<string> Changes { get; init; } = new();
}

/// <summary>
/// 修订版对比与应用（方案 3.2，纯函数）：按供应商货号对齐（去空格、不分大小写）；同一货号在表里出现多次时按出现顺序一一对应。
/// 已下的箱数挂在商品行（ItemGuid）上，所以对齐上的商品箱数自然保留；新表里没有的商品标为下架，不删数据。
/// 只记「最近一次修订」的改动用于高亮，没变的商品清掉旧高亮。
/// </summary>
public static class SupplierPreorderRevisionRules
{
    public const string Added = "Added";
    public const string Removed = "Removed";
    public const string Restored = "Restored";
    public const string Changed = "Changed";
    public const string Unchanged = "Unchanged";

    public const string ChangePrice = "Price";
    public const string ChangeCarton = "Carton";
    public const string ChangeName = "Name";
    public const string ChangeBarcode = "Barcode";
    public const string ChangeImage = "Image";
    public const string ChangeCbm = "Cbm";
    public const string ChangeCategory = "Category";

    internal static string CodeKey(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// 对比：existing 是这张订单表的全部商品（含已下架的），rows 是修订版里能导入的行（按原表顺序）。
    /// 返回顺序：先按新表顺序列出对齐 / 新增的，再列下架的（按原顺序）。之前就已下架、这次也没出现的不返回。
    /// keepCodes：修订版里有这个货号、但这一行不完整（缺进价、箱规等，不能导入）——对应商品保持原样，不当作下架。
    /// </summary>
    public static List<SupplierPreorderRevisionDiff> Diff(
        IReadOnlyList<SupplierPreorderFormItem> existing,
        IReadOnlyList<SupplierPreorderNormalizedRow> rows,
        IEnumerable<string>? keepCodes = null)
    {
        var keep = (keepCodes ?? Enumerable.Empty<string>()).Select(CodeKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        // 同货号多行：先对上架中的，再对已下架的，各自按原顺序
        var pool = existing
            .GroupBy(i => CodeKey(i.SupplierItemCode))
            .ToDictionary(g => g.Key, g => new Queue<SupplierPreorderFormItem>(g.OrderByDescending(i => i.IsActive).ThenBy(i => i.SortOrder)));
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SupplierPreorderRevisionDiff>(rows.Count);
        foreach (var row in rows)
        {
            if (pool.TryGetValue(CodeKey(row.SupplierItemCode), out var queue) && queue.Count > 0)
            {
                var item = queue.Dequeue();
                used.Add(item.ItemGuid);
                var changes = CompareFields(item, row);
                var kind = !item.IsActive ? Restored : changes.Count > 0 ? Changed : Unchanged;
                result.Add(new SupplierPreorderRevisionDiff { Kind = kind, Existing = item, Row = row, Changes = changes });
            }
            else
            {
                result.Add(new SupplierPreorderRevisionDiff { Kind = Added, Row = row });
            }
        }

        result.AddRange(existing
            .Where(i => i.IsActive && !used.Contains(i.ItemGuid) && !keep.Contains(CodeKey(i.SupplierItemCode)))
            .OrderBy(i => i.SortOrder)
            .Select(i => new SupplierPreorderRevisionDiff { Kind = Removed, Existing = i }));
        return result;
    }

    /// <summary>比较会影响下单或展示的字段。价格按 4 位小数比较；文本去首尾空格后区分大小写比较（改大小写也算改名）。</summary>
    public static List<string> CompareFields(SupplierPreorderFormItem item, SupplierPreorderNormalizedRow row)
    {
        var changes = new List<string>();
        if (Math.Round(item.Price, 4) != Math.Round(row.Price, 4))
        {
            changes.Add(ChangePrice);
        }

        if (item.CartonQty != row.CartonQty)
        {
            changes.Add(ChangeCarton);
        }

        if (!TextEquals(item.Name, row.Name))
        {
            changes.Add(ChangeName);
        }

        if (!TextEquals(item.Barcode, row.Barcode))
        {
            changes.Add(ChangeBarcode);
        }

        if (!TextEquals(item.ImageSourceUrl, row.ImageSourceUrl))
        {
            changes.Add(ChangeImage);
        }

        if ((item.CartonCbm ?? 0m) != (row.CartonCbm ?? 0m))
        {
            changes.Add(ChangeCbm);
        }

        if (!TextEquals(item.Category, row.Category))
        {
            changes.Add(ChangeCategory);
        }

        return changes;
    }

    private static bool TextEquals(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);

    /// <summary>把修订版的一行写回已有商品（对齐上的 / 重新上架的），并记下最近一次修订的改动。</summary>
    public static void ApplyRow(SupplierPreorderFormItem item, SupplierPreorderNormalizedRow row, SupplierPreorderRevisionDiff diff, int revision, int sortOrder, bool keepSourceRowNo)
    {
        var changes = diff.Changes.ToList();
        if (diff.Kind == Restored)
        {
            changes.Insert(0, Restored);
        }

        item.PrevPrice = changes.Contains(ChangePrice) ? item.Price : null;
        item.PrevCartonQty = changes.Contains(ChangeCarton) ? item.CartonQty : null;
        if (changes.Contains(ChangeImage))
        {
            // 换了图：之前转存的 COS 图作废，后台重新转存
            item.ImageCosUrl = null;
            item.ImageCopyStatus = null;
            item.ImageCopyAttempts = 0;
            item.ImageCopyError = null;
            item.ImageCopiedAtUtc = null;
        }

        item.Seq = row.Seq;
        item.SourceRowNo = keepSourceRowNo ? row.RowNo : 0;
        item.SortOrder = sortOrder;
        item.Barcode = row.Barcode;
        item.Name = row.Name;
        item.Unit = row.Unit;
        item.CartonQty = row.CartonQty;
        item.CartonCbm = row.CartonCbm;
        item.Price = row.Price;
        item.Category = row.Category;
        item.PrevYearItemCode = row.PrevYearItemCode ?? item.PrevYearItemCode;
        item.ImageSourceUrl = row.ImageSourceUrl;
        item.Notes = row.Notes;
        item.IsActive = true;
        item.RemovedAtUtc = null;
        if (changes.Count > 0)
        {
            item.RevisionChange = string.Join(",", changes);
            item.RevisionNo = revision;
        }
        else
        {
            // 这次没变：清掉以前修订的高亮
            item.RevisionChange = null;
            item.PrevPrice = null;
            item.PrevCartonQty = null;
        }
    }

    public static void MarkRemoved(SupplierPreorderFormItem item, int revision, int sortOrder, DateTime nowUtc)
    {
        item.IsActive = false;
        item.RemovedAtUtc = nowUtc;
        item.RevisionChange = Removed;
        item.RevisionNo = revision;
        item.PrevPrice = null;
        item.PrevCartonQty = null;
        item.SourceRowNo = 0;
        item.SortOrder = sortOrder;
    }

    /// <summary>最近一次修订的改动列表（逗号分隔存储）。</summary>
    public static List<string> ParseChanges(string? revisionChange) =>
        string.IsNullOrWhiteSpace(revisionChange)
            ? new List<string>()
            : revisionChange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
