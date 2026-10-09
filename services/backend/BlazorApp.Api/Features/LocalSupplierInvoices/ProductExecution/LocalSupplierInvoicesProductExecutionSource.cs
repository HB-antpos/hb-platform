using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>批量执行的唯一 SQL 读取入口，锁内复读也必须经过这里。</summary>
    internal sealed class LocalSupplierInvoicesProductExecutionSource
    {
        private readonly SqlSugarContext _context;

        public LocalSupplierInvoicesProductExecutionSource(SqlSugarContext context) => _context = context;

        public async Task<ProductExecutionSourceData> LoadInitialAsync(ProductExecutionRequest request) =>
            await LoadAsync(request.InvoiceGuid, request.SelectedDetailGuids, includeProductItemNumbers: false);

        public async Task<ProductExecutionSourceData> ReadLockedAsync(ProductExecutionRequest request) =>
            await LoadAsync(request.InvoiceGuid, request.SelectedDetailGuids, includeProductItemNumbers: true);

        public async Task<bool> ProductExistsByCodeAsync(string productCode) =>
            await _context.Db.Queryable<Product>()
                .AnyAsync(product => product.ProductCode == productCode && product.IsDeleted == false);

        /// <summary>
        /// 把前端传入的分店编码对照「POS 启用分店」（Store.IsActive 且未删除）解析成库内标准写法（忽略大小写）。
        /// 启用分店只有几十家，直接整表读取后在内存比对，避免 IN 查询受数据库排序规则大小写影响。
        /// </summary>
        public async Task<(List<string> Resolved, List<string> Invalid)> ResolvePosEnabledStoreCodesAsync(
            IReadOnlyCollection<string> storeCodes
        )
        {
            if (storeCodes.Count == 0) return (new List<string>(), new List<string>());
            var enabled = (await _context.Db.Queryable<Store>()
                    .Where(store => store.IsActive && store.IsDeleted == false)
                    .Select(store => store.StoreCode)
                    .ToListAsync())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .GroupBy(code => code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Trim(), StringComparer.OrdinalIgnoreCase);
            var resolved = new List<string>();
            var invalid = new List<string>();
            foreach (var code in storeCodes)
            {
                if (enabled.TryGetValue(code, out var canonical)) resolved.Add(canonical);
                else invalid.Add(code);
            }
            return (resolved, invalid);
        }

        public async Task<bool> StorePriceExistsAsync(string storeCode, string productCode) =>
            await _context.Db.Queryable<StoreRetailPrice>()
                .AnyAsync(price =>
                    price.StoreCode == storeCode
                    && price.ProductCode == productCode
                    && price.IsDeleted == false
                );

        public async Task<bool> HasProductBarcodeAsync(string normalizedBarcode) =>
            await _context.Db.Queryable<Product>().AnyAsync(product =>
                product.IsDeleted == false
                && product.Barcode != null
                && SqlFunc.ToUpper(product.Barcode) == normalizedBarcode
            );

        public async Task<bool> HasStoreMultiCodeBarcodeAsync(string normalizedBarcode) =>
            await _context.Db.Queryable<StoreMultiCodeProduct>().AnyAsync(item =>
                item.IsDeleted == false
                && item.MultiBarcode != null
                && SqlFunc.ToUpper(item.MultiBarcode) == normalizedBarcode
            );

        public async Task<bool> HasProductSetBarcodeAsync(string normalizedBarcode) =>
            await _context.Db.Queryable<ProductSetCode>().AnyAsync(item =>
                item.IsDeleted == false
                && item.SetBarcode != null
                && SqlFunc.ToUpper(item.SetBarcode) == normalizedBarcode
            );

        /// <summary>
        /// 「新建商品」行里商品其实已经建好的明细：已关联商品编码、该商品未删除、供应商与本单一致，且货号或条码对得上。
        /// 历史上「更新HQ商品」会隐式新建并回填编码却不标记已执行（生产 10-07 统计 57 万行），这些行再次新建会撞「已存在」。
        /// </summary>
        public async Task<HashSet<string>> FindAlreadyCreatedProductDetailGuidsAsync(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details,
            string? supplierCode
        )
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = details
                .Where(detail => detail.ActivityType == (int)DetailAction.CreateProduct
                    && !string.IsNullOrWhiteSpace(detail.ProductCode))
                .ToList();
            if (candidates.Count == 0 || string.IsNullOrWhiteSpace(supplierCode))
                return result;

            var productCodes = candidates.Select(detail => detail.ProductCode!.Trim()).Distinct().ToList();
            var products = await _context.Db.Queryable<Product>()
                .Where(product => product.IsDeleted == false && productCodes.Contains(product.ProductCode))
                .Select(product => new { product.ProductCode, product.LocalSupplierCode, product.ItemNumber, product.Barcode })
                .ToListAsync();
            var productsByCode = products
                .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                .GroupBy(product => product.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            static bool SameText(string? left, string? right) =>
                !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

            foreach (var detail in candidates)
            {
                if (!productsByCode.TryGetValue(detail.ProductCode!.Trim(), out var product))
                    continue;
                if (!SameText(product.LocalSupplierCode, supplierCode))
                    continue;
                if (SameText(product.ItemNumber, detail.ItemNumber) || SameText(product.Barcode, detail.Barcode))
                    result.Add(detail.DetailGUID);
            }
            return result;
        }

        /// <summary>
        /// 「更新货号」但未关联商品编码的明细，按主条码在本单供应商的商品里唯一解析商品编码（明细 GUID → 商品编码）。
        /// 典型场景：单据货号与主档只差空格（BEA 345618 ↔ BEA345618），检测判为主档不存在，用户直接改成「更新货号」。
        /// 只认同供应商、未删除、主条码相同且去重后恰好一个商品；0 个或多个都不解析，交给校验提示先「选用」。
        /// </summary>
        public async Task<Dictionary<string, string>> ResolveItemNumberUpdateProductCodesAsync(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details,
            string? supplierCode
        )
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = details
                .Where(detail => detail.ActivityType == (int)DetailAction.UpdateItemNumber
                    && string.IsNullOrWhiteSpace(detail.ProductCode)
                    && NormalizeCaseInsensitive(detail.Barcode) != null)
                .ToList();
            if (candidates.Count == 0 || string.IsNullOrWhiteSpace(supplierCode))
                return result;

            // 原值做 IN（生产库排序规则大小写不敏感），内存里再按大写归并，兼顾 SQLite 测试库。
            var barcodes = candidates
                .SelectMany(detail => new[] { detail.Barcode!.Trim(), detail.Barcode!.Trim().ToUpperInvariant() })
                .Distinct()
                .ToList();
            var products = await _context.Db.Queryable<Product>()
                .Where(product => product.IsDeleted == false
                    && product.LocalSupplierCode == supplierCode
                    && product.ProductCode != null
                    && barcodes.Contains(product.Barcode))
                .Select(product => new { product.ProductCode, product.Barcode })
                .ToListAsync();
            var codesByBarcode = products
                .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode) && !string.IsNullOrWhiteSpace(product.Barcode))
                .GroupBy(product => product.Barcode!.Trim().ToUpperInvariant())
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(product => product.ProductCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                );

            foreach (var detail in candidates)
            {
                if (codesByBarcode.TryGetValue(NormalizeCaseInsensitive(detail.Barcode)!, out var codes) && codes.Count == 1)
                    result[detail.DetailGUID] = codes[0];
            }
            return result;
        }

        /// <summary>分店多码里该条码是否已挂在别的商品上；挂在同一商品上视为已添加，不算冲突。</summary>
        public async Task<bool> HasStoreMultiCodeBarcodeOnOtherProductAsync(string normalizedBarcode, string productCode) =>
            await _context.Db.Queryable<StoreMultiCodeProduct>().AnyAsync(item =>
                item.IsDeleted == false
                && item.MultiBarcode != null
                && SqlFunc.ToUpper(item.MultiBarcode) == normalizedBarcode
                && item.ProductCode != productCode
            );

        /// <summary>多码关系里该条码是否被别的商品或非「一品多码」关系占用；同一商品的一品多码视为已添加。</summary>
        public async Task<bool> HasProductSetBarcodeConflictAsync(string normalizedBarcode, string productCode) =>
            await _context.Db.Queryable<ProductSetCode>().AnyAsync(item =>
                item.IsDeleted == false
                && item.SetBarcode != null
                && SqlFunc.ToUpper(item.SetBarcode) == normalizedBarcode
                && (item.ProductCode != productCode || item.SetType != 2)
            );

        public async Task<bool> HasSupplierProductIdentityAsync(
            string? supplierCode,
            string? normalizedItemNumber,
            string? normalizedBarcode
        ) =>
            await _context.Db.Queryable<Product>().AnyAsync(product =>
                product.IsDeleted == false
                && product.LocalSupplierCode == supplierCode
                && (
                    (normalizedItemNumber != null && SqlFunc.ToUpper(product.ItemNumber) == normalizedItemNumber)
                    || (normalizedBarcode != null && SqlFunc.ToUpper(product.Barcode) == normalizedBarcode)
                )
            );

        public async Task<bool> BarcodeBelongsToProductAsync(
            string? barcode,
            string? productCode,
            string? storeCode
        )
        {
            var normalizedBarcode = NormalizeCaseInsensitive(barcode);
            var normalizedProductCode = productCode?.Trim();
            if (normalizedBarcode == null || string.IsNullOrWhiteSpace(normalizedProductCode))
                return false;

            var productMatch = await _context.Db.Queryable<Product>().AnyAsync(product =>
                product.IsDeleted == false
                && product.ProductCode == normalizedProductCode
                && product.Barcode != null
                && SqlFunc.ToUpper(product.Barcode) == normalizedBarcode
            );
            if (productMatch)
                return productMatch;

            var normalizedStoreCode = storeCode?.Trim();
            return await _context.Db.Queryable<StoreMultiCodeProduct>().AnyAsync(multiCode =>
                multiCode.IsDeleted == false
                && (normalizedStoreCode == null || multiCode.StoreCode == normalizedStoreCode)
                && multiCode.ProductCode == normalizedProductCode
                && multiCode.MultiBarcode != null
                && SqlFunc.ToUpper(multiCode.MultiBarcode) == normalizedBarcode
            );
        }

        private async Task<ProductExecutionSourceData> LoadAsync(
            string invoiceGuid,
            List<string> detailGuids,
            bool includeProductItemNumbers
        )
        {
            var db = _context.Db;
            var header = await db.Queryable<StoreLocalSupplierInvoice>()
                .Where(invoice => invoice.InvoiceGUID == invoiceGuid && invoice.IsDeleted == false)
                .FirstAsync();
            if (header == null)
                return new ProductExecutionSourceData(null, new(), new(StringComparer.OrdinalIgnoreCase));

            var details = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(detail =>
                    detail.InvoiceGUID == invoiceGuid
                    && detailGuids.Contains(detail.DetailGUID)
                    && detail.IsDeleted == false
                )
                .ToListAsync();
            var itemNumbers = includeProductItemNumbers
                ? await LoadProductItemNumbersAsync(details)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new ProductExecutionSourceData(header, details, itemNumbers);
        }

        private async Task<Dictionary<string, string>> LoadProductItemNumbersAsync(
            IEnumerable<StoreLocalSupplierInvoiceDetails> details
        )
        {
            var productCodes = LocalSupplierInvoicesProductExecutionPlan.NormalizeProductCodes(details);
            if (productCodes.Count == 0)
                return new(StringComparer.OrdinalIgnoreCase);

            var products = await _context.Db.Queryable<Product>()
                .Where(product =>
                    product.ProductCode != null
                    && productCodes.Contains(product.ProductCode)
                    && product.IsDeleted == false
                )
                .Select(product => new { product.ProductCode, product.ItemNumber })
                .ToListAsync();
            return products
                .Where(product =>
                    !string.IsNullOrWhiteSpace(product.ProductCode)
                    && !string.IsNullOrWhiteSpace(product.ItemNumber)
                )
                .ToDictionary(product => product.ProductCode!, product => product.ItemNumber!, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读取本次改动商品中仍有有效套装/多码关系的主商品成本，供重算前补齐门店子项。
        /// 口径与套装重算一致：主档进货价为正优先，否则回退仓库进口价；成本不为正的商品不返回，
        /// 交由随后的严格重算按原规则报错，避免补齐门禁比原流程更严。
        /// </summary>
        public async Task<Dictionary<string, decimal>> LoadSetParentPurchasePricesAsync(
            IReadOnlyCollection<string> productCodes
        )
        {
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (productCodes.Count == 0)
                return prices;

            var codes = productCodes.ToList();
            var parentCodes = (
                await _context.Db.Queryable<ProductSetCode>()
                    .Where(row =>
                        codes.Contains(row.ProductCode)
                        && (row.SetType == 1 || row.SetType == 2)
                        && row.IsActive
                        && row.IsDeleted == false
                    )
                    .Select(row => row.ProductCode)
                    .Distinct()
                    .ToListAsync()
            )
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (parentCodes.Count == 0)
                return prices;

            var products = await _context.Db.Queryable<Product>()
                .Where(product =>
                    product.ProductCode != null
                    && parentCodes.Contains(product.ProductCode)
                    && product.IsDeleted == false
                )
                .ToListAsync();
            foreach (var product in products)
            {
                if (product.PurchasePrice.GetValueOrDefault() > 0m)
                    prices.TryAdd(product.ProductCode!.Trim(), product.PurchasePrice!.Value);
            }

            var warehouseCodes = parentCodes.Where(code => !prices.ContainsKey(code)).ToList();
            if (warehouseCodes.Count == 0)
                return prices;
            var warehouses = await _context.Db.Queryable<WarehouseProduct>()
                .Where(row => warehouseCodes.Contains(row.ProductCode) && row.IsDeleted == false)
                .ToListAsync();
            foreach (var warehouse in warehouses)
            {
                if (warehouse.ImportPrice.GetValueOrDefault() > 0m)
                    prices.TryAdd(warehouse.ProductCode.Trim(), warehouse.ImportPrice!.Value);
            }

            return prices;
        }

        private static string? NormalizeCaseInsensitive(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    internal sealed record ProductExecutionSourceData(
        StoreLocalSupplierInvoice? Header,
        List<StoreLocalSupplierInvoiceDetails> Details,
        Dictionary<string, string> ProductItemNumbers
    );
}
