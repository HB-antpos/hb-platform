using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hbpos.Contracts.Catalog;

/// <summary>
/// 客户端复算目录分页摘要（全量页 v2、增量页 v1）。编码必须与 Hbpos.Api 的 CatalogSellableIndex
/// 以及 iPad/手持端的 hbpos-catalog-remote.ts 逐字节一致：字段按 {十进制UTF-16长度}:{内容}| 首尾相接，
/// 服务端测试 CatalogPageChecksumParityTests 锁定两端输出相同。
/// </summary>
public static class CatalogPageChecksums
{
    public const string SellablePageV2Prefix = "sha256-catalog-page-v2:";
    public const string DeltaPageV1Prefix = "sha256-catalog-delta-page-v1:";
    private const string SellablePageV2AlgorithmMarker = "HBPOS-CATALOG-PAGE-CHECKSUM-V2";
    private const string DeltaPageV1AlgorithmMarker = "HBPOS-CATALOG-DELTA-PAGE-CHECKSUM-V1";

    public static string ComputeSellablePageV2(IReadOnlyList<CatalogLookupItemDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, SellablePageV2AlgorithmMarker);
        AppendCanonical(hash, FormatBinary64((double)items.Count));

        foreach (var item in items)
        {
            AppendCanonical(hash, item.StoreCode);
            AppendCanonical(hash, item.ProductCode);
            AppendCanonical(hash, item.ReferenceCode ?? string.Empty);
            AppendCanonical(hash, item.DisplayName);
            AppendCanonical(hash, item.LookupCode);
            AppendCanonical(hash, item.LookupCodeNormalized);
            AppendCanonical(hash, item.ItemNumber ?? string.Empty);
            AppendCanonical(hash, item.Barcode ?? string.Empty);
            AppendCanonical(hash, FormatBinary64(item.RetailPrice));
            AppendCanonical(hash, FormatBinary64((double)(int)item.PriceSource));
            AppendCanonical(hash, item.PriceSourceLabel);
            AppendCanonical(hash, FormatBinary64(item.QuantityFactor));
            AppendCanonical(hash, FormatTimestamp(item.UpdatedAt));
            AppendCanonical(hash, item.ProductImage ?? string.Empty);
            AppendCanonical(hash, item.DiscountRate.HasValue ? FormatBinary64(item.DiscountRate.Value) : string.Empty);
            AppendCanonical(hash, item.IsSpecialProduct ? "1" : "0");
        }

        return string.Concat(
            SellablePageV2Prefix,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// 增量页把 upsert 与 delete 按 LookupCodeNormalized 的序数顺序归并后编码；
    /// 服务端每个 key 最多一个操作，所以归并顺序唯一，漏掉任何一条 delete 都会校验失败。
    /// </summary>
    public static string ComputeDeltaPageV1(
        string baseCatalogVersion,
        string targetCatalogVersion,
        IReadOnlyList<CatalogLookupItemDto> upsertedItems,
        IReadOnlyList<DeletedLookupDto> deletedLookups)
    {
        ArgumentNullException.ThrowIfNull(baseCatalogVersion);
        ArgumentNullException.ThrowIfNull(targetCatalogVersion);
        ArgumentNullException.ThrowIfNull(upsertedItems);
        ArgumentNullException.ThrowIfNull(deletedLookups);

        var operations = upsertedItems
            .Select(item => (Key: item.LookupCodeNormalized, Item: (CatalogLookupItemDto?)item, Deleted: (DeletedLookupDto?)null))
            .Concat(deletedLookups.Select(deleted => (Key: deleted.LookupCodeNormalized, Item: (CatalogLookupItemDto?)null, Deleted: (DeletedLookupDto?)deleted)))
            .OrderBy(operation => operation.Key, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        AppendCanonical(builder, DeltaPageV1AlgorithmMarker);
        AppendCanonical(builder, baseCatalogVersion);
        AppendCanonical(builder, targetCatalogVersion);
        AppendCanonical(builder, operations.Length.ToString(CultureInfo.InvariantCulture));

        foreach (var operation in operations)
        {
            if (operation.Item is { } item)
            {
                AppendCanonical(builder, "U");
                AppendCanonical(builder, item.StoreCode);
                AppendCanonical(builder, item.ProductCode);
                AppendCanonical(builder, item.ReferenceCode ?? string.Empty);
                AppendCanonical(builder, item.DisplayName);
                AppendCanonical(builder, item.LookupCode);
                AppendCanonical(builder, item.LookupCodeNormalized);
                AppendCanonical(builder, item.ItemNumber ?? string.Empty);
                AppendCanonical(builder, item.Barcode ?? string.Empty);
                AppendCanonical(builder, FormatNumberV1(item.RetailPrice));
                AppendCanonical(builder, ((int)item.PriceSource).ToString(CultureInfo.InvariantCulture));
                AppendCanonical(builder, item.PriceSourceLabel);
                AppendCanonical(builder, FormatNumberV1(item.QuantityFactor));
                AppendCanonical(builder, FormatTimestamp(item.UpdatedAt));
                AppendCanonical(builder, item.ProductImage ?? string.Empty);
                AppendCanonical(builder, item.DiscountRate.HasValue ? FormatNumberV1(item.DiscountRate.Value) : string.Empty);
                AppendCanonical(builder, item.IsSpecialProduct ? "1" : "0");
                continue;
            }

            var deleted = operation.Deleted!;
            AppendCanonical(builder, "D");
            AppendCanonical(builder, deleted.StoreCode);
            AppendCanonical(builder, deleted.LookupCode);
            AppendCanonical(builder, deleted.LookupCodeNormalized);
            AppendCanonical(builder, FormatTimestamp(deleted.DeletedAt));
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return string.Concat(
            DeltaPageV1Prefix,
            Convert.ToHexString(hashBytes).ToLowerInvariant());
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static void AppendCanonical(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture)));
        hash.AppendData(":"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData("|"u8);
    }

    private static string FormatBinary64(decimal value)
    {
        // decimal 先转成 JSON 会输出的十进制文本，再按客户端 JSON.parse 看到的 double 编码。
        return FormatBinary64(double.Parse(
            value.ToString("0.#############################", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture));
    }

    private static string FormatBinary64(double value)
    {
        if (value == 0d)
        {
            // 负零归一为正零，与服务端一致。
            value = 0d;
        }

        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// v1 数值编码：取 JavaScript 能观察到的 double，再展开成不带指数的十进制文本。
    /// </summary>
    private static string FormatNumberV1(decimal value)
    {
        var javascriptNumber = double.Parse(
            value.ToString("0.#############################", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
        var text = javascriptNumber.ToString("R", CultureInfo.InvariantCulture);
        if (text is "-0")
        {
            return "0";
        }

        var exponentSeparator = text.IndexOfAny(['e', 'E']);
        if (exponentSeparator < 0)
        {
            return text;
        }

        var mantissa = text[..exponentSeparator];
        var exponent = int.Parse(text[(exponentSeparator + 1)..], CultureInfo.InvariantCulture);
        var isNegative = mantissa.StartsWith("-", StringComparison.Ordinal);
        var unsignedMantissa = isNegative ? mantissa[1..] : mantissa;
        var decimalSeparator = unsignedMantissa.IndexOf('.');
        var whole = decimalSeparator < 0 ? unsignedMantissa : unsignedMantissa[..decimalSeparator];
        var fraction = decimalSeparator < 0 ? string.Empty : unsignedMantissa[(decimalSeparator + 1)..];
        var digits = string.Concat(whole, fraction);
        var decimalIndex = whole.Length + exponent;
        string expanded;

        if (decimalIndex <= 0)
        {
            expanded = string.Concat("0.", new string('0', -decimalIndex), digits);
        }
        else if (decimalIndex >= digits.Length)
        {
            expanded = string.Concat(digits, new string('0', decimalIndex - digits.Length));
        }
        else
        {
            expanded = string.Concat(digits[..decimalIndex], ".", digits[decimalIndex..]);
        }

        return isNegative ? string.Concat("-", expanded) : expanded;
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
