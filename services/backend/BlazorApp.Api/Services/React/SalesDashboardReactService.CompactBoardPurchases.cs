using System.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 仓库销售看板分店栏的「进货」：各分店在进货日期区间内从仓库进的货（区间与销售区间相互独立）。
/// 口径与进货金额看板的仓库订单一致：非草稿、未删除订单的配货数量（AllocQuantity），
/// 日期取出库日期，未出库回退订单日期、再回退创建时间。
/// 商品范围与看板相同：按 POSM 映射（200 → 国内供应商，同一商品多条映射取最小编码，与看板 #cbMap 一致）归到国内供应商，
/// 未映射的商品不计；选中的国内供应商、商品同样收窄进货，选中分店不收窄分店栏自身（与销售联动规则一致）。
/// </summary>
public partial class SalesDashboardReactService
{
    internal sealed class CompactBoardPurchaseRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public string? BranchName { get; set; }
        public decimal Quantity { get; set; }
    }

    private sealed class CompactBoardPurchaseDetailRow
    {
        public string? StoreCode { get; set; }
        public string? ProductCode { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>
    /// 给分店栏补进货量；只有进货、区间内没有销售的分店也补进来（金额、销量为 0），便于看出到货未动销。
    /// SQL Server 且 POSM 同服务器时在数据库里聚合，只回几十行；其他情况（SQLite 测试等）按分店×商品聚合后在内存归属。
    /// </summary>
    private async Task FillCompactSalesBoardPurchasesAsync(
        CompactSalesBoardDto board,
        DateRangeDto purchaseRange,
        HashSet<string>? branchScope,
        string? selectedChinaSupplierCode,
        string? selectedProductCode
    )
    {
        if (branchScope is { Count: 0 })
            return;
        string? Selected(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        var supplier = Selected(selectedChinaSupplierCode);
        var product = Selected(selectedProductCode);
        var rows = UsesCompactBoardDedicatedConnection() && TryGetSameServerPosmDatabase(out var posmDatabase)
            ? await ReadCompactBoardPurchasesOnServerAsync(posmDatabase, purchaseRange, branchScope, supplier, product)
            : await ReadCompactBoardPurchasesPortableAsync(purchaseRange, branchScope, supplier, product);
        MergeCompactBoardPurchases(board.Stores, rows);
    }

    /// <summary>
    /// 把进货量并入分店栏：已有分店填进货量；只有进货的分店追加在有销售的分店之后，按进货量从多到少、再按代码排序。
    /// 分店代码去空格、不区分大小写对齐（与看板分店栏、分店总营业额的对齐方式一致）。
    /// </summary>
    internal static void MergeCompactBoardPurchases(List<CompactSalesBoardStoreDto> stores, IEnumerable<CompactBoardPurchaseRow> rows)
    {
        var purchases = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
            .GroupBy(row => row.BranchCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (
                    Quantity: group.Sum(row => row.Quantity),
                    Name: group.Select(row => row.BranchName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ),
                StringComparer.OrdinalIgnoreCase
            );
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
        {
            var code = store.BranchCode.Trim();
            listed.Add(code);
            store.PurchaseQuantity = purchases.TryGetValue(code, out var purchase) ? purchase.Quantity : 0m;
        }

        stores.AddRange(purchases
            .Where(pair => pair.Value.Quantity != 0m && !listed.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value.Quantity)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new CompactSalesBoardStoreDto
            {
                BranchCode = pair.Key,
                BranchName = string.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key : pair.Value.Name.Trim(),
                PurchaseQuantity = pair.Value.Quantity,
            }));
    }

    private async Task<List<CompactBoardPurchaseRow>> ReadCompactBoardPurchasesOnServerAsync(
        string posmDatabase,
        DateRangeDto range,
        HashSet<string>? branchScope,
        string? supplier,
        string? product
    )
    {
        var scopeCodes = branchScope?.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray();
        await using var connection = await OpenCompactBoardDedicatedConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
        command.CommandText = BuildCompactBoardPurchasesSql(posmDatabase);
        command.Parameters.Add("@cpStart", SqlDbType.DateTime).Value = range.StartDate.Date;
        command.Parameters.Add("@cpEnd", SqlDbType.DateTime).Value = range.EndDate.Date.AddDays(1);
        command.Parameters.Add("@cpAllBranches", SqlDbType.Bit).Value = scopeCodes == null;
        command.Parameters.Add("@cpBranches", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(scopeCodes ?? Array.Empty<string>());
        command.Parameters.Add("@cpSupplier", SqlDbType.NVarChar, 50).Value = (object?)supplier ?? DBNull.Value;
        command.Parameters.Add("@cpProduct", SqlDbType.NVarChar, 50).Value = (object?)product ?? DBNull.Value;

        var rows = new List<CompactBoardPurchaseRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CompactBoardPurchaseRow
            {
                BranchCode = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                BranchName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Quantity = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2)),
            });
        }
        return rows;
    }

    /// <summary>
    /// 数据库端进货聚合：一条语句按分店汇总，只回几十行。日期回退在范围过滤里拆成三支，各日期列的索引都能用
    /// （同进货金额看板）；区间长短差别大（1 天～2 年），用 RECOMPILE 避免沿用短区间的计划。
    /// 排序规则：跨库映射统一转成当前库默认排序规则，与看板 #cbMap 相同。
    /// </summary>
    internal static string BuildCompactBoardPurchasesSql(string posmDatabase)
    {
        var posm = SalesDetailQueryMonthlyProjection.QuoteIdentifier(posmDatabase);
        return $$"""
SET NOCOUNT ON;
SELECT g.[BranchCode], stn.[StoreName], g.[Quantity]
FROM (
    SELECT LTRIM(RTRIM(h.[StoreCode])) [BranchCode], SUM(d.[AllocQuantity]) [Quantity]
    FROM [dbo].[WareHouseOrder] h
    INNER JOIN [dbo].[WareHouseOrderDetails] d ON d.[OrderGUID] = h.[OrderGUID] AND COALESCE(d.[IsDeleted], 0) = 0
    INNER JOIN (
        SELECT LTRIM(RTRIM(m.[ProductCode])) COLLATE DATABASE_DEFAULT [ProductCode],
               MIN(LTRIM(RTRIM(m.[ChinaSupplierCode])) COLLATE DATABASE_DEFAULT) [ChinaSupplierCode]
        FROM {{posm}}.[dbo].[posm_product_supplier_mapping] m
        WHERE m.[LocalSupplierCode] = N'200' AND m.[IsDeleted] = 0 AND m.[ProductCode] IS NOT NULL
          AND m.[ChinaSupplierCode] IS NOT NULL AND LTRIM(RTRIM(m.[ChinaSupplierCode])) <> N''
        GROUP BY LTRIM(RTRIM(m.[ProductCode])) COLLATE DATABASE_DEFAULT
    ) mp ON mp.[ProductCode] = LTRIM(RTRIM(d.[ProductCode]))
    WHERE COALESCE(h.[IsDeleted], 0) = 0
      AND COALESCE(h.[FlowStatus], -1) <> 0
      AND d.[AllocQuantity] <> 0
      AND NULLIF(LTRIM(RTRIM(h.[StoreCode])), N'') IS NOT NULL
      AND (
          (h.[OutboundDate] >= @cpStart AND h.[OutboundDate] < @cpEnd)
          OR (h.[OutboundDate] IS NULL AND h.[OrderDate] >= @cpStart AND h.[OrderDate] < @cpEnd)
          OR (h.[OutboundDate] IS NULL AND h.[OrderDate] IS NULL AND h.[CreatedAt] >= @cpStart AND h.[CreatedAt] < @cpEnd)
      )
      AND (@cpAllBranches = 1
           OR LTRIM(RTRIM(h.[StoreCode])) IN (SELECT CONVERT(nvarchar(50), b.[value]) COLLATE DATABASE_DEFAULT FROM OPENJSON(@cpBranches) b))
      AND (@cpSupplier IS NULL OR mp.[ChinaSupplierCode] = @cpSupplier)
      AND (@cpProduct IS NULL OR mp.[ProductCode] = @cpProduct)
    GROUP BY LTRIM(RTRIM(h.[StoreCode]))
) g
OUTER APPLY (SELECT TOP (1) st.[StoreName] FROM [dbo].[Store] st WHERE st.[StoreCode] = g.[BranchCode]) stn
OPTION (RECOMPILE);
""";
    }

    /// <summary>
    /// 通用路径（SQLite 测试、POSM 不在同一服务器）：数据库里先按分店×商品汇总，再用 POSM 映射在内存归属国内供应商。
    /// </summary>
    private async Task<List<CompactBoardPurchaseRow>> ReadCompactBoardPurchasesPortableAsync(
        DateRangeDto range,
        HashSet<string>? branchScope,
        string? supplier,
        string? product
    )
    {
        var start = range.StartDate.Date;
        var endExclusive = range.EndDate.Date.AddDays(1);
        var details = await _context.Db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((d, h) => d.OrderGUID == h.OrderGUID)
            .Where((d, h) =>
                !d.IsDeleted
                && !h.IsDeleted
                && (h.FlowStatus == null || h.FlowStatus != 0)
                && d.AllocQuantity != null
                && d.AllocQuantity != 0
                && d.ProductCode != null
                && h.StoreCode != null
                && (
                    (h.OutboundDate >= start && h.OutboundDate < endExclusive)
                    || (h.OutboundDate == null && h.OrderDate >= start && h.OrderDate < endExclusive)
                    || (h.OutboundDate == null && h.OrderDate == null && h.CreatedAt >= start && h.CreatedAt < endExclusive)
                ))
            .GroupBy((d, h) => new { h.StoreCode, d.ProductCode })
            .Select((d, h) => new CompactBoardPurchaseDetailRow
            {
                StoreCode = h.StoreCode,
                ProductCode = d.ProductCode,
                Quantity = SqlFunc.AggregateSum(d.AllocQuantity),
            })
            .ToListAsync();
        if (details.Count == 0)
            return new List<CompactBoardPurchaseRow>();

        var productSupplierMap = await GetChinaSupplierProductMapAsync();
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in details)
        {
            var branchCode = row.StoreCode?.Trim();
            var productCode = row.ProductCode?.Trim();
            if (string.IsNullOrEmpty(branchCode) || string.IsNullOrEmpty(productCode))
                continue;
            if (branchScope != null && !branchScope.Contains(branchCode))
                continue;
            if (!productSupplierMap.TryGetValue(productCode, out var chinaSupplier) || string.IsNullOrWhiteSpace(chinaSupplier))
                continue;
            if (supplier != null && !string.Equals(chinaSupplier.Trim(), supplier, StringComparison.OrdinalIgnoreCase))
                continue;
            if (product != null && !string.Equals(productCode, product, StringComparison.OrdinalIgnoreCase))
                continue;
            totals[branchCode] = totals.GetValueOrDefault(branchCode) + (row.Quantity ?? 0m);
        }

        var names = await GetStoreNameMapAsync(totals.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return totals
            .Select(pair => new CompactBoardPurchaseRow
            {
                BranchCode = pair.Key,
                BranchName = names.TryGetValue(pair.Key, out var name) ? name : null,
                Quantity = pair.Value,
            })
            .ToList();
    }
}
