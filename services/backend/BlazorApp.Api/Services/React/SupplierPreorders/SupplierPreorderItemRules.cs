using System.Globalization;
using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>按列映射规范化后的一行订单表商品（纯数据，便于单测）。</summary>
public sealed class SupplierPreorderNormalizedRow
{
    public int RowNo { get; set; }
    public int Seq { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public int CartonQty { get; set; }
    public decimal? CartonCbm { get; set; }
    public decimal Price { get; set; }
    public string? Category { get; set; }
    public string? PrevYearItemCode { get; set; }
    public string? ImageSourceUrl { get; set; }
    public string? Notes { get; set; }
    /// <summary>不能导入（缺货号/名称/价格/箱规）。</summary>
    public bool Invalid { get; set; }
    public List<string> Issues { get; } = new();
}

/// <summary>参与匹配的我方商品（只取需要的列）。</summary>
public sealed class SupplierPreorderProductCandidate
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal? PurchasePrice { get; set; }
    public string? ProductImage { get; set; }
    public string? LocalSupplierCode { get; set; }
    public bool IsActive { get; set; }
}

public sealed class SupplierPreorderMatchResult
{
    public string MatchMethod { get; set; } = SupplierPreorderMatchMethods.None;
    public SupplierPreorderProductCandidate? Product { get; set; }
    /// <summary>已匹配但需要提示的情况（条码对上货号不同、表内货号重复等）。</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>订单表行规范化与商品匹配规则（纯函数）。</summary>
public static class SupplierPreorderItemRules
{
    /// <summary>
    /// 按映射取值并校验。货号和名称都为空的行（如 KFC 表头下面的合计 0 行）直接跳过，不算「不能导入」。
    /// </summary>
    public static List<SupplierPreorderNormalizedRow> Normalize(
        IReadOnlyList<SupplierPreorderParsedRowDto> rows,
        SupplierPreorderColumnMappingDto mapping
    )
    {
        var result = new List<SupplierPreorderNormalizedRow>(rows.Count);
        var order = 0;
        foreach (var row in rows)
        {
            string? Cell(int? index) =>
                index is int i && i >= 0 && i < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[i])
                    ? row.Cells[i]!.Trim()
                    : null;

            var code = Cell(mapping.ItemCode);
            var name = Cell(mapping.Name);
            if (code is null && name is null)
            {
                continue;
            }

            order++;
            var item = new SupplierPreorderNormalizedRow
            {
                RowNo = row.RowNo,
                SupplierItemCode = Truncate(code, 100) ?? string.Empty,
                Name = Truncate(name is null ? null : Regex.Replace(name, @"\s+", " "), 300) ?? string.Empty,
                Unit = Truncate(Cell(mapping.Unit), 30),
                Category = Truncate(Cell(mapping.Category), 100),
                PrevYearItemCode = NormalizePrevYearCode(Cell(mapping.PrevYearItemCode)),
                Notes = Truncate(Cell(mapping.Notes), 500),
            };

            item.Seq = TryParseInt(Cell(mapping.Seq), out var seq) && seq > 0 ? seq : order;

            var barcode = Cell(mapping.Barcode);
            if (barcode is not null)
            {
                if (Regex.IsMatch(barcode, @"^\d+(\.\d+)?E\+\d+$", RegexOptions.IgnoreCase))
                {
                    item.Issues.Add("条码被 Excel 显示成科学计数法，已忽略");
                }
                else
                {
                    item.Barcode = Truncate(barcode.Replace(" ", string.Empty), 50);
                }
            }

            var image = Cell(mapping.Image);
            if (image is not null && (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                item.ImageSourceUrl = Truncate(image, 500);
            }

            if (code is null)
            {
                item.Invalid = true;
                item.Issues.Add("缺少货号");
            }

            if (name is null)
            {
                item.Invalid = true;
                item.Issues.Add("缺少名称");
            }

            // 主列为空时按行退回备用列（Dats 灯饰等行只填了 BULK PRICE / Outer）
            var priceText = Cell(mapping.Price) ?? Cell(mapping.PriceFallback);
            var cartonText = Cell(mapping.CartonQty) ?? Cell(mapping.CartonQtyFallback);
            if (TryParseDecimal(priceText, out var price) && price > 0)
            {
                item.Price = Math.Round(price, 4);
            }
            else
            {
                item.Invalid = true;
                item.Issues.Add("进价无效");
            }

            if (TryParseDecimal(cartonText, out var ctn) && ctn > 0 && ctn == Math.Floor(ctn) && ctn <= 100000)
            {
                item.CartonQty = (int)ctn;
            }
            else
            {
                item.Invalid = true;
                item.Issues.Add("箱规无效（必须是正整数）");
            }

            if (TryParseDecimal(Cell(mapping.CartonCbm), out var cbm) && cbm >= 0 && cbm < 1000)
            {
                item.CartonCbm = Math.Round(cbm, 4);
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// 匹配顺序：条码 → 货号（Product.ItemNumber）→ 去年货号。同一条码对上多个商品时优先同供应商、启用的、货号一致的。
    /// 条码对上但货号不同（如 XB4393 ↔ XB4393W）、货号对上但条码不同都算「有提示」，不阻止导入。
    /// </summary>
    public static List<SupplierPreorderMatchResult> Match(
        IReadOnlyList<SupplierPreorderNormalizedRow> rows,
        IReadOnlyList<SupplierPreorderProductCandidate> products,
        string? localSupplierCode
    )
    {
        var byBarcode = products
            .Where(p => !string.IsNullOrWhiteSpace(p.Barcode))
            .GroupBy(p => p.Barcode!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var byItem = products
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemNumber))
            .GroupBy(p => p.ItemNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var codeFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var results = new List<SupplierPreorderMatchResult>(rows.Count);
        foreach (var row in rows)
        {
            var result = new SupplierPreorderMatchResult();
            results.Add(result);
            if (row.Invalid)
            {
                continue;
            }

            if (codeFirstRow.TryGetValue(row.SupplierItemCode, out var firstRow))
            {
                result.Warnings.Add($"表内货号重复（与第 {firstRow} 行相同）");
            }
            else
            {
                codeFirstRow[row.SupplierItemCode] = row.RowNo;
            }

            SupplierPreorderProductCandidate Pick(List<SupplierPreorderProductCandidate> list) => list
                .OrderByDescending(p => !string.IsNullOrEmpty(localSupplierCode) && p.LocalSupplierCode == localSupplierCode)
                .ThenByDescending(p => p.IsActive)
                .ThenByDescending(p => string.Equals(p.ItemNumber?.Trim(), row.SupplierItemCode, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.ProductCode, StringComparer.Ordinal)
                .First();

            if (row.Barcode is not null && byBarcode.TryGetValue(row.Barcode, out var barcodeHits))
            {
                result.Product = Pick(barcodeHits);
                result.MatchMethod = SupplierPreorderMatchMethods.Barcode;
                var ourItem = result.Product.ItemNumber?.Trim();
                if (!string.IsNullOrEmpty(ourItem) && !string.Equals(ourItem, row.SupplierItemCode, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"条码对上的商品货号是 {ourItem}");
                }

                continue;
            }

            if (byItem.TryGetValue(row.SupplierItemCode, out var itemHits))
            {
                result.Product = Pick(itemHits);
                result.MatchMethod = SupplierPreorderMatchMethods.ItemNumber;
                var ourBarcode = result.Product.Barcode?.Trim();
                if (row.Barcode is not null && !string.IsNullOrEmpty(ourBarcode) && ourBarcode != row.Barcode)
                {
                    result.Warnings.Add($"货号对上但条码不同（我方 {ourBarcode}）");
                }

                continue;
            }

            if (row.PrevYearItemCode is not null && byItem.TryGetValue(row.PrevYearItemCode, out var prevHits))
            {
                result.Product = Pick(prevHits);
                result.MatchMethod = SupplierPreorderMatchMethods.PrevYearItemCode;
            }
        }

        return results;
    }

    /// <summary>进价变动比例（相对商品表进价）；商品表没有进价时为 null。</summary>
    public static decimal? PriceChangeRate(decimal price, decimal? lastPurchasePrice) =>
        lastPurchasePrice is > 0 ? Math.Round((price - lastPurchasePrice.Value) / lastPurchasePrice.Value, 4) : null;

    /// <summary>Dats 新品的去年货号写「NEW」，不是货号。</summary>
    private static string? NormalizePrevYearCode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Equals("NEW", StringComparison.OrdinalIgnoreCase) || value == "-" ? null : Truncate(value, 100);
    }

    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Replace("$", string.Empty).Replace(",", string.Empty).Replace("AUD", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        return TryParseDecimal(text, out var number) && number == Math.Floor(number) && number < int.MaxValue && (value = (int)number) == number;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
