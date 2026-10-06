using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Features.LocalSupplierInvoices;

/// <summary>商品审核的只读数据边界；所有 SQL 查询都集中在此处。</summary>
internal sealed class LocalSupplierInvoicesProductReviewStore
{
    private readonly SqlSugarContext _context;
    private readonly ILogger _logger;

    public LocalSupplierInvoicesProductReviewStore(LocalSupplierInvoicesDependencies dependencies)
    {
        _context = dependencies.Context;
        _logger = dependencies.Logger;
    }

        public async Task<ApiResponse<List<SupplierItemDetectResult>>> DetectSupplierItemAsync(
            DetectSupplierItemRequest dto
        )
        {
            try
            {
                var db = _context.Db;
                var inputItems = dto.Items ?? new List<DetectSupplierItem>();
                var itemNumbers = inputItems.Select(x => x?.ItemNumber?.Trim()).ToList();

                var validItemNumbers = itemNumbers
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .Distinct()
                    .ToList();

                var products = new List<ProductDetectProjection>();
                if (validItemNumbers.Count > 0)
                {
                    products = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        ProductDetectProjection,
                        string
                    >(
                        validItemNumbers,
                        500,
                        async chunk =>
                            await db.Queryable<Product>()
                                .Where(p =>
                                    p.LocalSupplierCode == dto.SupplierCode
                                    && p.ItemNumber != null
                                    && chunk.Contains(p.ItemNumber)
                                    && p.IsDeleted == false
                                )
                                .Select(p => new ProductDetectProjection
                                {
                                    ItemNumber = p.ItemNumber!,
                                    ProductCode = p.ProductCode,
                                    ProductName = p.ProductName,
                                    ProductImage = p.ProductImage,
                                })
                                .ToListAsync()
                    );
                }

                var prodByItem = products.ToDictionary(x => x.ItemNumber, x => x);

                var productCodes = products
                    .Select(x => x.ProductCode)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .Distinct()
                    .ToList();

                var priceByCode = new Dictionary<string, PriceDetectProjection>();
                if (productCodes.Count > 0)
                {
                    var prices = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        PriceDetectProjection,
                        string
                    >(
                        productCodes,
                        500,
                        async chunk =>
                            await db.Queryable<StoreRetailPrice>()
                                .Where(x =>
                                    x.StoreCode == dto.StoreCode
                                    && x.ProductCode != null
                                    && chunk.Contains(x.ProductCode)
                                    && x.IsDeleted == false
                                )
                                .Select(x => new PriceDetectProjection
                                {
                                    ProductCode = x.ProductCode!,
                                    PurchasePrice = x.PurchasePrice,
                                    Retail = x.StoreRetailPriceValue,
                                    StoreProductCode = x.StoreProductCode,
                                })
                                .ToListAsync()
                    );
                    priceByCode = prices.ToDictionary(x => x.ProductCode, x => x);
                }

                var results = new List<SupplierItemDetectResult>(inputItems.Count);
                foreach (var it in inputItems)
                {
                    var itemNumber = it?.ItemNumber?.Trim();
                    if (string.IsNullOrWhiteSpace(itemNumber))
                    {
                        results.Add(
                            new SupplierItemDetectResult { Exists = false, Error = "货号为空" }
                        );
                        continue;
                    }

                    if (!prodByItem.TryGetValue(itemNumber, out var prod))
                    {
                        results.Add(new SupplierItemDetectResult { Exists = false });
                        continue;
                    }

                    PriceDetectProjection? price = null;
                    if (!string.IsNullOrWhiteSpace(prod.ProductCode))
                    {
                        priceByCode.TryGetValue(prod.ProductCode!, out price);
                    }

                    results.Add(
                        new SupplierItemDetectResult
                        {
                            Exists = true,
                            ProductImage = prod.ProductImage,
                            ProductCode = prod.ProductCode,
                            StoreProductCode = price?.StoreProductCode,
                            ProductName = prod.ProductName,
                            CurrentPurchasePrice = price?.PurchasePrice,
                            CurrentRetailPrice = price?.Retail,
                        }
                    );
                }

                return ApiResponse<List<SupplierItemDetectResult>>.OK(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "供应商+货号检测失败");
                return ApiResponse<List<SupplierItemDetectResult>>.Error(
                    "检测失败",
                    "DETECT_ERROR"
                );
            }
        }

        public async Task<ApiResponse<List<BarcodeDetectResult>>> DetectBarcodeAsync(
            DetectBarcodeRequest dto
        )
        {
            try
            {
                var db = _context.Db;
                var inputItems = dto.Items ?? new List<DetectBarcodeItem>();
                var inputBarcodes = inputItems.Select(x => x?.Barcode?.Trim()).ToList();
                var validBarcodes = inputBarcodes
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => b!)
                    .Distinct()
                    .ToList();

                var productByBarcode =
                    new Dictionary<string, List<(string code, string name, string? image)>>();
                if (validBarcodes.Count > 0)
                {
                    var prods = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        BarcodeProductProjection,
                        string
                    >(
                        validBarcodes,
                        500,
                        async chunk =>
                            await db.Queryable<Product>()
                                .Where(p =>
                                    p.IsDeleted == false
                                    && p.Barcode != null
                                    && chunk.Contains(p.Barcode)
                                )
                                .Select(p => new BarcodeProductProjection
                                {
                                    Barcode = p.Barcode!,
                                    ProductCode = p.ProductCode,
                                    ProductName = p.ProductName,
                                    ProductImage = p.ProductImage,
                                })
                                .ToListAsync()
                    );
                    foreach (var p in prods)
                    {
                        var key = p.Barcode;
                        if (!productByBarcode.TryGetValue(key, out var list))
                        {
                            list = new List<(string code, string name, string? image)>();
                            productByBarcode[key] = list;
                        }
                        if (!string.IsNullOrWhiteSpace(p.ProductCode))
                            list.Add(
                                (p.ProductCode!, p.ProductName ?? string.Empty, p.ProductImage)
                            );
                    }

                    var mprods = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        MultiCodeProductProjection,
                        string
                    >(
                        validBarcodes,
                        500,
                        async chunk =>
                            await db.Queryable<StoreMultiCodeProduct>()
                                .LeftJoin<Product>((m, p) => m.ProductCode == p.ProductCode)
                                .Where(
                                    (m, p) =>
                                        m.StoreCode == dto.StoreCode
                                        && m.MultiBarcode != null
                                        && chunk.Contains(m.MultiBarcode)
                                        && m.IsDeleted == false
                                )
                                .Select(
                                    (m, p) =>
                                        new MultiCodeProductProjection
                                        {
                                            MultiBarcode = m.MultiBarcode!,
                                            ProductCode = m.ProductCode,
                                            Name = p.ProductName,
                                            Image = p.ProductImage,
                                        }
                                )
                                .ToListAsync()
                    );
                    foreach (var mp in mprods)
                    {
                        var key = mp.MultiBarcode;
                        if (!productByBarcode.TryGetValue(key, out var list))
                        {
                            list = new List<(string code, string name, string? image)>();
                            productByBarcode[key] = list;
                        }
                        if (!string.IsNullOrWhiteSpace(mp.ProductCode))
                            list.Add((mp.ProductCode!, mp.Name ?? string.Empty, mp.Image));
                    }
                }

                var results = new List<BarcodeDetectResult>(inputItems.Count);
                // 预先汇总所有条码对应的产品码，批量查询分店商品编码，避免循环内重复查询
                var allCodes = productByBarcode
                    .Values.SelectMany(list => list.Select(x => x.code))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .Distinct()
                    .ToList();
                var spByCode = new Dictionary<string, List<string>>();
                if (allCodes.Count > 0)
                {
                    var allStoreProductCodes = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        StoreProductCodeProjection,
                        string
                    >(
                        allCodes,
                        500,
                        async chunk =>
                            await db.Queryable<StoreRetailPrice>()
                                .Where(x =>
                                    x.StoreCode == dto.StoreCode
                                    && x.ProductCode != null
                                    && chunk.Contains(x.ProductCode)
                                    && x.IsDeleted == false
                                )
                                .Select(x => new StoreProductCodeProjection
                                {
                                    ProductCode = x.ProductCode!,
                                    StoreProductCode = x.StoreProductCode,
                                })
                                .ToListAsync()
                    );
                    foreach (var row in allStoreProductCodes)
                    {
                        if (
                            string.IsNullOrWhiteSpace(row.ProductCode)
                            || string.IsNullOrWhiteSpace(row.StoreProductCode)
                        )
                            continue;
                        if (!spByCode.TryGetValue(row.ProductCode, out var list))
                        {
                            list = new List<string>();
                            spByCode[row.ProductCode] = list;
                        }
                        if (!list.Contains(row.StoreProductCode!))
                        {
                            list.Add(row.StoreProductCode!);
                        }
                    }
                }
                foreach (var it in inputItems)
                {
                    var barcode = it?.Barcode?.Trim();
                    if (string.IsNullOrWhiteSpace(barcode))
                    {
                        results.Add(
                            new BarcodeDetectResult
                            {
                                Matched = false,
                                MatchCount = 0,
                                OverTwo = false,
                                Error = "条码为空",
                            }
                        );
                        continue;
                    }
                    var pairs = productByBarcode.TryGetValue(barcode, out var list)
                        ? list
                        : new List<(string code, string name, string? image)>();
                    var codes = pairs
                        .Select(x => x.code)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct()
                        .ToList();
                    var names = pairs
                        .Select(x => x.name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct()
                        .ToList();
                    var firstImg = pairs
                        .Select(x => x.image)
                        .FirstOrDefault(img => !string.IsNullOrWhiteSpace(img));
                    var count = codes.Count;
                    // 关联 StoreRetailPrice 获取分店商品编码（使用预先批量查询的映射）
                    List<string>? storeProductCodes =
                        codes.Count > 0
                            ? codes
                                .SelectMany(c =>
                                    spByCode.TryGetValue(c, out var list)
                                        ? list
                                        : new List<string>()
                                )
                                .Where(sp => !string.IsNullOrWhiteSpace(sp))
                                .Distinct()
                                .ToList()
                            : null;
                    results.Add(
                        new BarcodeDetectResult
                        {
                            Matched = count > 0,
                            MatchCount = count,
                            OverTwo = count > 2,
                            ProductCodes = codes,
                            StoreProductCodes = storeProductCodes,
                            ProductNames = names,
                            FirstProductImage = firstImg,
                        }
                    );
                }

                return ApiResponse<List<BarcodeDetectResult>>.OK(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "条码检测失败");
                return ApiResponse<List<BarcodeDetectResult>>.Error("检测失败", "DETECT_ERROR");
            }
        }

    public async Task<LocalSupplierInvoicesProductReviewData?> LoadAsync(CheckProductsRequest request)
    {
        var db = _context.Db;
        var header = await db.Queryable<StoreLocalSupplierInvoice>()
            .FirstAsync(x => x.InvoiceGUID == request.InvoiceGuid && x.IsDeleted == false);
        if (header == null)
            return null;

        var detailsQuery = db.Queryable<StoreLocalSupplierInvoiceDetails>()
            .Where(x => x.InvoiceGUID == request.InvoiceGuid && x.IsDeleted == false);
        if (request.DetailGuids is { Count: > 0 })
            detailsQuery = detailsQuery.Where(x => request.DetailGuids.Contains(x.DetailGUID));
        // 关键位置：批量检测不能把已执行行（99）的操作类型覆盖回默认建议，否则「已执行」进度丢失。
        if (request.ExcludeExecutedDetails)
            detailsQuery = detailsQuery.Where(x => x.ActivityType == null || x.ActivityType != 99);
        var details = await detailsQuery.ToListAsync();

        var itemNumbers = details.Select(x => x.ItemNumber?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var barcodes = details.Select(x => x.Barcode?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .Distinct().ToList();
        var products = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        if (itemNumbers.Count > 0)
        {
            var exact = await QueryInChunksParallelAsync<Product, string>(itemNumbers, 200, (queryDb, chunk) =>
                queryDb.Queryable<Product>().Where(p => p.LocalSupplierCode == header.SupplierCode
                    && p.ItemNumber != null && chunk.Contains(p.ItemNumber) && p.IsDeleted == false).ToListAsync());
            foreach (var product in exact.Where(x => !string.IsNullOrWhiteSpace(x.ItemNumber)))
                products[product.ItemNumber!] = product;

            var missing = itemNumbers.Where(x => !products.ContainsKey(x)).ToList();
            if (missing.Count > 0)
            {
                var fallback = await QueryInChunksParallelAsync<Product, string>(missing, 200, (queryDb, chunk) =>
                {
                    var upperChunk = chunk.Select(x => x.ToUpper()).ToList();
                    return queryDb.Queryable<Product>().Where(p => p.LocalSupplierCode == header.SupplierCode
                        && p.ItemNumber != null && upperChunk.Contains(p.ItemNumber.ToUpper()) && p.IsDeleted == false).ToListAsync();
                });
                foreach (var product in fallback.Where(x => !string.IsNullOrWhiteSpace(x.ItemNumber)))
                    products[product.ItemNumber!] = product;
            }
        }

        var productCodes = products.Values.Select(x => x.ProductCode).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!).Distinct().ToList();
        var storePrices = new Dictionary<string, StoreRetailPrice>();
        if (productCodes.Count > 0)
        {
            var rows = await QueryInChunksParallelAsync<StoreRetailPrice, string>(productCodes, 200, (queryDb, chunk) =>
                queryDb.Queryable<StoreRetailPrice>().Where(x => x.StoreCode == header.StoreCode
                    && x.ProductCode != null && chunk.Contains(x.ProductCode) && x.IsDeleted == false).ToListAsync());
            foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)))
                storePrices[row.ProductCode!] = row;
        }

        var barcodeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productCodesByBarcode = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (barcodes.Count > 0)
        {
            var barcodeProducts = await QueryInChunksParallelAsync<Product, string>(barcodes, 200, (queryDb, chunk) =>
                queryDb.Queryable<Product>().Where(p => p.IsDeleted == false && p.Barcode != null && chunk.Contains(p.Barcode)).ToListAsync());
            foreach (var product in barcodeProducts)
                AddBarcodeMatch(barcodeCounts, productCodesByBarcode, product.Barcode, product.ProductCode);

            var multiCodes = await QueryInChunksParallelAsync<StoreMultiCodeProduct, string>(barcodes, 200, (queryDb, chunk) =>
                queryDb.Queryable<StoreMultiCodeProduct>().Where(x => x.StoreCode == header.StoreCode
                    && x.MultiBarcode != null && chunk.Contains(x.MultiBarcode) && x.IsDeleted == false).ToListAsync());
            foreach (var multiCode in multiCodes)
                AddBarcodeMatch(barcodeCounts, productCodesByBarcode, multiCode.MultiBarcode, multiCode.ProductCode);
        }

        return new LocalSupplierInvoicesProductReviewData(
            header,
            details,
            products,
            storePrices,
            barcodeCounts,
            productCodesByBarcode
        );
    }

    private static void AddBarcodeMatch(
        Dictionary<string, int> counts,
        Dictionary<string, HashSet<string>> productCodesByBarcode,
        string? barcode,
        string? productCode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return;
        var normalized = barcode.Trim();
        counts[normalized] = counts.TryGetValue(normalized, out var count) ? count + 1 : 1;
        LocalSupplierInvoicesBarcodeRules.AddBarcodeProductCode(productCodesByBarcode, normalized, productCode);
    }

    internal async Task<List<T>> QueryInChunksParallelAsync<T, TKey>(
        IReadOnlyList<TKey> keys,
        int chunkSize,
        Func<ISqlSugarClient, List<TKey>, Task<List<T>>> fetch,
        int maxConcurrency = 5)
    {
        if (keys == null || keys.Count == 0)
            return new List<T>();
        var chunks = Enumerable.Range(0, (keys.Count + chunkSize - 1) / chunkSize)
            .Select(index => keys.Skip(index * chunkSize).Take(chunkSize).ToList()).ToList();
        if (chunks.Count == 1)
            return await fetch(_context.Db, chunks[0]) ?? new List<T>();

        var result = new List<T>[chunks.Count];
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = chunks.Select((chunk, index) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                // 并发分块使用独立连接，避免 SqlSugar 查询初始化共享状态。
                using var queryDb = _context.CreateConcurrentQueryConnection();
                result[index] = await fetch(queryDb, chunk) ?? new List<T>();
            }
            finally { semaphore.Release(); }
        })).ToArray();
        await Task.WhenAll(tasks);
        return result.Where(x => x != null).SelectMany(x => x).ToList();
    }
}

internal sealed record LocalSupplierInvoicesProductReviewData(
    StoreLocalSupplierInvoice Header,
    List<StoreLocalSupplierInvoiceDetails> Details,
    Dictionary<string, Product> ProductsByItemNumber,
    Dictionary<string, StoreRetailPrice> StorePricesByProductCode,
    Dictionary<string, int> BarcodeMatchCounts,
    Dictionary<string, HashSet<string>> ProductCodesByBarcode);

internal sealed class BarcodeProductProjection
{
    public required string Barcode { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImage { get; set; }
}

internal sealed class MultiCodeProductProjection
{
    public required string MultiBarcode { get; set; }
    public string? ProductCode { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
}

internal sealed class StoreProductCodeProjection
{
    public required string ProductCode { get; set; }
    public string? StoreProductCode { get; set; }
}
