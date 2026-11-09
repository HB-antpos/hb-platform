using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React;

internal sealed class BrowserExtensionBarcodeLookupRow
{
    public string? LocalSupplierCode { get; set; }
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// 扫码查货号的纯判定逻辑：供应商网站只认自家货号，所以只有「条码 + 当前供应商」命中才给货号。
/// </summary>
internal static class BrowserExtensionBarcodeLookup
{
    internal const int MaxRows = 50;

    /// <summary>
    /// UPC-A（12 位）与 EAN-13 只差前导 0，扫码枪和录入习惯都可能带或不带，两种写法都查。
    /// </summary>
    internal static List<string> BuildBarcodeVariants(string barcode)
    {
        var variants = new List<string> { barcode };
        if (!barcode.All(char.IsAsciiDigit))
        {
            return variants;
        }

        if (barcode.Length == 12)
        {
            variants.Add($"0{barcode}");
        }
        else if (barcode.Length == 13 && barcode[0] == '0')
        {
            variants.Add(barcode[1..]);
        }

        return variants;
    }

    internal static List<string> DistinctSupplierCodes(IEnumerable<BrowserExtensionBarcodeLookupRow> rows) =>
        rows.Select(row => row.LocalSupplierCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static BrowserExtensionBarcodeLookupResultDto Resolve(
        IReadOnlyCollection<BrowserExtensionBarcodeLookupRow> rows,
        string supplierCode,
        string barcode
    )
    {
        var result = new BrowserExtensionBarcodeLookupResultDto
        {
            SupplierCode = supplierCode,
            Barcode = barcode,
        };

        var supplierRows = rows
            .Where(row => string.Equals(row.LocalSupplierCode?.Trim(), supplierCode, StringComparison.OrdinalIgnoreCase))
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemNumber))
            .ToList();
        if (supplierRows.Count == 0)
        {
            // 当前供应商有这个条码但没货号时不算别家商品，同样搜不了，按未找到提示。
            var ownsBarcode = rows.Any(row =>
                string.Equals(row.LocalSupplierCode?.Trim(), supplierCode, StringComparison.OrdinalIgnoreCase)
            );
            result.Status = !ownsBarcode && DistinctSupplierCodes(rows).Count > 0
                ? BrowserExtensionBarcodeLookupStatus.OtherSupplier
                : BrowserExtensionBarcodeLookupStatus.NotFound;
            return result;
        }

        // 同时有启用和停用的商品时只看启用的，避免旧货号干扰；全部停用时仍可搜索。
        var pool = supplierRows.Any(row => row.IsActive)
            ? supplierRows.Where(row => row.IsActive).ToList()
            : supplierRows;
        var candidates = pool
            .GroupBy(row => row.ItemNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new BrowserExtensionBarcodeLookupCandidateDto
            {
                ItemNumber = group.Key,
                ProductName = group.First().ProductName?.Trim() ?? string.Empty,
            })
            .OrderBy(candidate => candidate.ItemNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
        {
            result.Status = BrowserExtensionBarcodeLookupStatus.Matched;
            result.ItemNumber = candidates[0].ItemNumber;
            result.ProductName = candidates[0].ProductName;
            return result;
        }

        result.Status = BrowserExtensionBarcodeLookupStatus.Multiple;
        result.Candidates = candidates;
        return result;
    }
}
