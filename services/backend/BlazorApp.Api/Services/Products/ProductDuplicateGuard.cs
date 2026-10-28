using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Products;

/// <summary>已占用某个条码的存活商品摘要，用于提示用户「选用已有商品」。</summary>
internal sealed record ProductBarcodeOwner(
    string ProductCode,
    string? ItemNumber,
    string? ProductName,
    string? LocalSupplierCode,
    string Source
);

/// <summary>新建被查重拦截：带错误码与「已有商品」清单，由入口映射成 409 / 失败响应，供客户端提示用户选用已有商品。</summary>
internal sealed class ProductDuplicateException : Exception
{
    public ProductDuplicateException(string errorCode, string message, object? details = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
    }

    public string ErrorCode { get; }
    public object? Details { get; }
}

/// <summary>
/// 新建商品前的条码/货号查重，所有新建入口共用同一口径。
/// 条码「占用」= 存活商品的主条码 + 存活的分店多码 + 存活的「一品多码」关系（ProductSetCode.SetType=2）；
/// 已软删商品不占用条码。占用时不允许新建，由调用方提示用户改为选用已有商品，不提供「仍然创建」。
/// 背景：同货号同条码的商品只出现在不同供应商之间（2026-10-28 全库 90/90 组两条档案供应商都不同），
/// 以前各入口只按「同供应商」查重，跨供应商的同条码商品会被静默建成第二条。
/// </summary>
internal static class ProductDuplicateGuard
{
    public const string BarcodeExistsErrorCode = "BARCODE_EXISTS";
    public const string ItemNumberExistsErrorCode = "ITEM_NUMBER_EXISTS";

    public const string SourceMainBarcode = "MainBarcode";
    public const string SourceStoreMultiCode = "StoreMultiCode";
    public const string SourceProductSetCode = "ProductSetCode";

    // ProductSetCode.SetType：2 = 一品多码（跟随主条码价）；其余为套装子项，其条码属于子项商品本身，不算「别人占用」。
    private const int SingleProductMultiCodeSetType = 2;
    private const int ChunkSize = 200;
    private const int MaxOwnersInMessage = 3;

    public static string? NormalizeBarcode(string? barcode) =>
        string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();

    /// <summary>
    /// 批量查条码占用者，返回「条码（忽略大小写）→ 占用商品列表」；没人占用的条码不在结果里。
    /// <paramref name="excludeProductCode"/> 用于「改商品自己的条码」之类场景，排除该商品自身。
    /// </summary>
    public static async Task<Dictionary<string, List<ProductBarcodeOwner>>> FindBarcodeOwnersAsync(
        ISqlSugarClient db,
        IEnumerable<string?> barcodes,
        string? excludeProductCode = null
    )
    {
        var result = new Dictionary<string, List<ProductBarcodeOwner>>(StringComparer.OrdinalIgnoreCase);
        var normalized = barcodes
            .Select(NormalizeBarcode)
            .Where(barcode => barcode != null)
            .Select(barcode => barcode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
            return result;

        var exclude = excludeProductCode?.Trim();
        foreach (var chunk in normalized.Chunk(ChunkSize))
        {
            // 原值与大写各查一遍：生产库排序规则大小写不敏感，SQLite 测试库区分大小写；内存里再按大小写不敏感归并。
            var variants = chunk
                .SelectMany(barcode => new[] { barcode, barcode.ToUpperInvariant() })
                .Distinct()
                .ToList();

            var mainProducts = await db.Queryable<Product>()
                .Where(product => product.IsDeleted == false
                    && product.Barcode != null
                    && variants.Contains(product.Barcode))
                .ToListAsync();
            foreach (var product in mainProducts)
            {
                if (string.IsNullOrWhiteSpace(product.ProductCode) || string.IsNullOrWhiteSpace(product.Barcode))
                    continue;
                AddOwner(result, product.Barcode, ToOwner(product, SourceMainBarcode), exclude);
            }

            var multiCodes = await db.Queryable<StoreMultiCodeProduct>()
                .Where(item => item.IsDeleted == false
                    && item.MultiBarcode != null
                    && variants.Contains(item.MultiBarcode))
                .ToListAsync();
            var setCodes = await db.Queryable<ProductSetCode>()
                .Where(item => item.IsDeleted == false
                    && item.SetType == SingleProductMultiCodeSetType
                    && item.SetBarcode != null
                    && variants.Contains(item.SetBarcode))
                .ToListAsync();

            // 多码/一品多码挂在某个主档商品上，占用者是该主档；主档已删除则不算占用。
            var ownerCodes = multiCodes.Select(item => item.ProductCode)
                .Concat(setCodes.Select(item => item.ProductCode))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ownerCodes.Count == 0)
                continue;

            var ownerProducts = (await db.Queryable<Product>()
                    .Where(product => product.IsDeleted == false && ownerCodes.Contains(product.ProductCode!))
                    .ToListAsync())
                .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                .GroupBy(product => product.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var item in multiCodes)
            {
                if (item.MultiBarcode == null || item.ProductCode == null) continue;
                if (ownerProducts.TryGetValue(item.ProductCode.Trim(), out var owner))
                    AddOwner(result, item.MultiBarcode, ToOwner(owner, SourceStoreMultiCode), exclude);
            }
            foreach (var item in setCodes)
            {
                if (item.SetBarcode == null || item.ProductCode == null) continue;
                if (ownerProducts.TryGetValue(item.ProductCode.Trim(), out var owner))
                    AddOwner(result, item.SetBarcode, ToOwner(owner, SourceProductSetCode), exclude);
            }
        }

        return result;
    }

    /// <summary>同一供应商下是否已有存活的同货号商品（不同供应商允许相同货号）。</summary>
    public static async Task<bool> HasSupplierItemNumberAsync(
        ISqlSugarClient db,
        string? supplierCode,
        string? itemNumber
    )
    {
        var item = string.IsNullOrWhiteSpace(itemNumber) ? null : itemNumber.Trim();
        if (item == null || string.IsNullOrWhiteSpace(supplierCode))
            return false;

        return await db.Queryable<Product>().AnyAsync(product =>
            product.IsDeleted == false
            && product.LocalSupplierCode == supplierCode
            && product.ItemNumber == item);
    }

    /// <summary>
    /// 创建事务内给本次要写入的条码加事务级互斥锁，保证「查重 + 插入」对同一条码是串行的。
    /// 必须在已开启的事务里、查重之前调用；锁按条码排序获取，避免多条码批次互相死锁。仅 SQL Server 生效。
    /// </summary>
    public static async Task AcquireBarcodeCreateLocksAsync(ISqlSugarClient db, IEnumerable<string?> barcodes)
    {
        if (db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            return;

        var ordered = barcodes
            .Select(NormalizeBarcode)
            .Where(barcode => barcode != null)
            .Select(barcode => barcode!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(barcode => barcode, StringComparer.Ordinal)
            .ToList();
        foreach (var barcode in ordered)
        {
            await db.Ado.ExecuteCommandAsync("""
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 10000;
IF @result < 0 THROW 51064, '获取商品条码创建锁失败', 1;
""", new SugarParameter("@resource", $"ProductBarcodeCreate:{barcode}"));
        }
    }

    /// <summary>
    /// 同一供应商下同货号的并发创建串行化；资源名与 <c>ReactProductsController.CreateWithPrices</c> 保持一致，
    /// 两个创建入口互相也会排队。必须在已开启的事务里、查重之前调用。仅 SQL Server 生效。
    /// </summary>
    public static async Task AcquireSupplierItemNumberCreateLockAsync(
        ISqlSugarClient db,
        string? supplierCode,
        string? itemNumber
    )
    {
        var item = string.IsNullOrWhiteSpace(itemNumber) ? null : itemNumber.Trim();
        if (item == null || db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            return;

        await db.Ado.ExecuteCommandAsync("""
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 10000;
IF @result < 0 THROW 51063, '获取本地商品货号创建锁失败', 1;
""", new SugarParameter("@resource", $"LocalProductItemNumberCreate:{supplierCode}:{item}"));
    }

    public static string DescribeOwner(ProductBarcodeOwner owner) =>
        $"货号 {Display(owner.ItemNumber)} · {Display(owner.ProductName)}（供应商 {Display(owner.LocalSupplierCode)}）";

    public static string BuildBarcodeConflictMessage(string barcode, IReadOnlyList<ProductBarcodeOwner> owners)
    {
        var shown = string.Join("；", owners.Take(MaxOwnersInMessage).Select(DescribeOwner));
        var more = owners.Count > MaxOwnersInMessage ? $" 等 {owners.Count} 个商品" : string.Empty;
        return $"条码 {barcode} 已属于商品 {shown}{more}，请选用已有商品，不能重复新建";
    }

    /// <summary>409 响应里的 existingProducts：客户端据此弹出「选用已有商品」。</summary>
    public static object[] ToResponsePayload(IEnumerable<ProductBarcodeOwner> owners) =>
        owners
            .Select(owner => (object)new
            {
                productCode = owner.ProductCode,
                itemNumber = owner.ItemNumber,
                productName = owner.ProductName,
                localSupplierCode = owner.LocalSupplierCode,
                source = owner.Source,
            })
            .ToArray();

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();

    private static ProductBarcodeOwner ToOwner(Product product, string source) =>
        new(product.ProductCode!.Trim(), product.ItemNumber, product.ProductName, product.LocalSupplierCode, source);

    private static void AddOwner(
        Dictionary<string, List<ProductBarcodeOwner>> result,
        string barcode,
        ProductBarcodeOwner owner,
        string? excludeProductCode
    )
    {
        if (excludeProductCode != null
            && string.Equals(owner.ProductCode, excludeProductCode, StringComparison.OrdinalIgnoreCase))
            return;

        var key = barcode.Trim();
        if (!result.TryGetValue(key, out var owners))
            result[key] = owners = new List<ProductBarcodeOwner>();
        // 同一商品既是主条码又有多码时只记一次，主条码来源优先（先加入的就是主条码）。
        if (owners.Any(existing => string.Equals(existing.ProductCode, owner.ProductCode, StringComparison.OrdinalIgnoreCase)))
            return;
        owners.Add(owner);
    }
}
