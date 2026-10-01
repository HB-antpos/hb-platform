using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.DataSync.Common;

/// <summary>
/// HQ 同步写 WarehouseProduct 时保留本地中包数（MinOrderQuantity）。
/// 中包数由仓库在本地维护（Web 仓库商品“中包数”列、拣货时补录），HQ 的 H最小订货量
/// 只在本地为空或 ≤0 时补位；本地一旦有正数，任何同步路径（清表重建、整行更新、批量更新）都不再覆盖。
/// </summary>
internal static class WarehouseMinOrderQuantitySyncGuard
{
    /// <summary>
    /// 读取本地所有正数中包数。键为去空格后的商品编码，大小写不敏感。
    /// 同步路径本来就会一次性读全表编码，调用方应尽量复用那次读取（见 <see cref="BuildLocalValues"/>）。
    /// </summary>
    internal static async Task<Dictionary<string, int>> LoadLocalValuesAsync(ISqlSugarClient db)
    {
        var rows = await db.Queryable<WarehouseProduct>()
            .Where(item => item.MinOrderQuantity > 0)
            .Select(item => new LocalMinOrderQuantityRow
            {
                ProductCode = item.ProductCode,
                MinOrderQuantity = item.MinOrderQuantity,
            })
            .ToListAsync();
        return BuildLocalValues(rows);
    }

    internal static Dictionary<string, int> BuildLocalValues(
        IEnumerable<LocalMinOrderQuantityRow> rows
    )
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = row.ProductCode?.Trim();
            if (string.IsNullOrEmpty(code) || row.MinOrderQuantity is not > 0)
            {
                continue;
            }

            // 历史数据里同一编码可能大小写或空格不同地出现多次，保留第一条正数即可。
            values.TryAdd(code, row.MinOrderQuantity.Value);
        }

        return values;
    }

    /// <summary>
    /// 把本地正数中包数回填到即将写入的 HQ 映射结果上，返回实际被本地值改写的行数（用于日志）。
    /// </summary>
    internal static int Apply(
        IEnumerable<WarehouseProduct> incoming,
        IReadOnlyDictionary<string, int> localValues
    )
    {
        if (localValues.Count == 0)
        {
            return 0;
        }

        var preserved = 0;
        foreach (var item in incoming)
        {
            var code = item.ProductCode?.Trim();
            if (string.IsNullOrEmpty(code) || !localValues.TryGetValue(code, out var localValue))
            {
                continue;
            }

            if (item.MinOrderQuantity != localValue)
            {
                preserved++;
            }

            item.MinOrderQuantity = localValue;
        }

        return preserved;
    }

    internal sealed class LocalMinOrderQuantityRow
    {
        public string? ProductCode { get; set; }

        public int? MinOrderQuantity { get; set; }
    }
}
