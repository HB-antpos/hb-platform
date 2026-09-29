using System;
using System.Collections.Generic;
using System.Linq;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class StoreMultiCodePricesReactService : IStoreMultiCodePricesReactService
    {
        private readonly SqlSugarContext _context;
        private readonly ILogger<StoreMultiCodePricesReactService> _logger;

        public StoreMultiCodePricesReactService(
            SqlSugarContext context,
            ILogger<StoreMultiCodePricesReactService> logger
        )
        {
            _context = context;
            _logger = logger;
        }

        public async Task<GridResponseDto<StoreMultiCodePriceListDto>> GetGridDataAsync(
            GridRequestDto request
        )
        {
            try
            {
                var db = _context.Db;
                var query = db.Queryable<StoreMultiCodeProduct>()
                    .LeftJoin<Product>((mc, prod) => mc.ProductCode == prod.ProductCode)
                    .LeftJoin<Store>((mc, prod, st) => mc.StoreCode == st.StoreCode)
                    .Where((mc, prod, st) => mc.IsDeleted == false);

                if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
                {
                    var keyword = request.GlobalSearch.Trim();
                    var longEnough = keyword.Length >= 2;
                    query = query.Where(
                        (mc, prod, st) =>
                            (
                                mc.StoreCode != null
                                && (
                                    longEnough
                                        ? mc.StoreCode.StartsWith(keyword)
                                        : mc.StoreCode.Contains(keyword)
                                )
                            )
                            || (st.StoreName != null && st.StoreName.Contains(keyword))
                            || (
                                mc.ProductCode != null
                                && (
                                    longEnough
                                        ? mc.ProductCode.StartsWith(keyword)
                                        : mc.ProductCode.Contains(keyword)
                                )
                            )
                            || (prod.ProductName != null && prod.ProductName.Contains(keyword))
                            || (
                                prod.ItemNumber != null
                                && (
                                    longEnough
                                        ? prod.ItemNumber.StartsWith(keyword)
                                        : prod.ItemNumber.Contains(keyword)
                                )
                            )
                            || (mc.MultiBarcode != null && mc.MultiBarcode.Contains(keyword))
                    );
                }

                if (request.FilterModel != null && request.FilterModel.Any())
                {
                    foreach (var kv in request.FilterModel)
                    {
                        var col = kv.Key;
                        var f = kv.Value;
                        if (f == null || f.FilterType == null)
                            continue;
                        var type = f.FilterType.ToLower();
                        if (type == "text" && f.Filter != null)
                        {
                            var v = f.Filter?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(v))
                                continue;
                            var op = (f.Type ?? "contains").ToLower();
                            switch (col)
                            {
                                case "storeCode":
                                    query = ApplyText(query, op, v, x => x.StoreCode);
                                    break;
                                case "storeName":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (mc, prod, st) => st.StoreName == v
                                        ),
                                        "notequal" => query.Where(
                                            (mc, prod, st) => st.StoreName != v
                                        ),
                                        "contains" => query.Where(
                                            (mc, prod, st) =>
                                                st.StoreName != null && st.StoreName.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (mc, prod, st) =>
                                                st.StoreName == null || !st.StoreName.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (mc, prod, st) =>
                                                st.StoreName != null && st.StoreName.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (mc, prod, st) =>
                                                st.StoreName != null && st.StoreName.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (mc, prod, st) => string.IsNullOrEmpty(st.StoreName)
                                        ),
                                        "notblank" => query.Where(
                                            (mc, prod, st) => !string.IsNullOrEmpty(st.StoreName)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "productCode":
                                    query = ApplyText(query, op, v, x => x.ProductCode);
                                    break;
                                case "productName":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (mc, prod, st) => prod.ProductName == v
                                        ),
                                        "notequal" => query.Where(
                                            (mc, prod, st) => prod.ProductName != v
                                        ),
                                        "contains" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ProductName == null
                                                || !prod.ProductName.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (mc, prod, st) => string.IsNullOrEmpty(prod.ProductName)
                                        ),
                                        "notblank" => query.Where(
                                            (mc, prod, st) =>
                                                !string.IsNullOrEmpty(prod.ProductName)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "itemNumber":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (mc, prod, st) => prod.ItemNumber == v
                                        ),
                                        "notequal" => query.Where(
                                            (mc, prod, st) => prod.ItemNumber != v
                                        ),
                                        "contains" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ItemNumber == null
                                                || !prod.ItemNumber.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (mc, prod, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (mc, prod, st) => string.IsNullOrEmpty(prod.ItemNumber)
                                        ),
                                        "notblank" => query.Where(
                                            (mc, prod, st) => !string.IsNullOrEmpty(prod.ItemNumber)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "multiBarcode":
                                    query = ApplyText(query, op, v, x => x.MultiBarcode);
                                    break;
                            }
                        }
                        else if (type == "number" && f.Filter != null)
                        {
                            var sv = f.Filter?.ToString()?.Trim();
                            if (decimal.TryParse(sv, out var dv))
                            {
                                var op = f.Type?.ToLower();
                                switch (col)
                                {
                                    case "purchasePrice":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.PurchasePrice,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                    case "multiCodeRetailPrice":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.MultiCodeRetailPrice,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                    case "discountRate":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.DiscountRate,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                }
                            }
                        }
                        else if (type == "set" && f.Values != null && f.Values.Any())
                        {
                            switch (col)
                            {
                                case "isActive":
                                    var boolsA = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (mc, prod, st) => boolsA.Contains(mc.IsActive)
                                    );
                                    break;
                                case "isAutoPricing":
                                    var boolsB = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (mc, prod, st) => boolsB.Contains(mc.IsAutoPricing)
                                    );
                                    break;
                                case "isSpecialProduct":
                                    var boolsC = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (mc, prod, st) => boolsC.Contains(prod.IsSpecialProduct)
                                    );
                                    break;
                            }
                        }
                    }
                }

                if (request.SortModel != null && request.SortModel.Any())
                {
                    var s = request.SortModel.First();
                    var asc = s.Sort.ToLower() == "asc";
                    query = s.ColId switch
                    {
                        "storeCode" => query.OrderBy(
                            (mc, prod, st) => mc.StoreCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "storeName" => query.OrderBy(
                            (mc, prod, st) => st.StoreName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "productCode" => query.OrderBy(
                            (mc, prod, st) => mc.ProductCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "productName" => query.OrderBy(
                            (mc, prod, st) => prod.ProductName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "itemNumber" => query.OrderBy(
                            (mc, prod, st) => prod.ItemNumber,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "multiBarcode" => query.OrderBy(
                            (mc, prod, st) => mc.MultiBarcode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "purchasePrice" => query.OrderBy(
                            (mc, prod, st) => mc.PurchasePrice,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "multiCodeRetailPrice" => query.OrderBy(
                            (mc, prod, st) => mc.MultiCodeRetailPrice,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "discountRate" => query.OrderBy(
                            (mc, prod, st) => mc.DiscountRate,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isSpecialProduct" => query.OrderBy(
                            (mc, prod, st) => prod.IsSpecialProduct,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isAutoPricing" => query.OrderBy(
                            (mc, prod, st) => mc.IsAutoPricing,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isActive" => query.OrderBy(
                            (mc, prod, st) => mc.IsActive,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "updatedAt" => query.OrderBy(
                            (mc, prod, st) => mc.UpdatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        _ => query.OrderBy((mc, prod, st) => mc.CreatedAt, OrderByType.Desc),
                    };
                }
                else
                {
                    query = query.OrderBy((mc, prod, st) => mc.CreatedAt, OrderByType.Desc);
                }

                var total = await query.CountAsync();
                var items = await query
                    .Select(
                        (mc, prod, st) =>
                            new StoreMultiCodePriceListDto
                            {
                                UUID = mc.UUID,
                                StoreCode = mc.StoreCode,
                                StoreName = st.StoreName,
                                ProductCode = mc.ProductCode,
                                ProductName = prod.ProductName,
                                ProductImage = prod.ProductImage,
                                ItemNumber = prod.ItemNumber,
                                MultiBarcode = mc.MultiBarcode,
                                PurchasePrice = mc.PurchasePrice,
                                MultiCodeRetailPrice = mc.MultiCodeRetailPrice,
                                DiscountRate = mc.DiscountRate,
                                IsActive = mc.IsActive,
                                IsAutoPricing = mc.IsAutoPricing,
                                UpdatedBy = mc.UpdatedBy,
                                UpdatedAt = mc.UpdatedAt,
                                IsSpecialProduct = prod.IsSpecialProduct,
                            }
                    )
                    .Skip(request.StartRow)
                    .Take(request.PageSize)
                    .ToListAsync();

                return GridResponseDto<StoreMultiCodePriceListDto>.OK(items, total);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StoreMultiCodePrice Grid 查询失败");
                return GridResponseDto<StoreMultiCodePriceListDto>.Error("查询失败");
            }
        }

        public async Task<ApiResponse<BatchResultDtoMC>> BatchUpsertAsync(
            List<StoreMultiCodePriceUpsertItemDto> items,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                var now = DateTime.UtcNow;
                var insertList = new List<StoreMultiCodeProduct>();
                var updateList = new List<StoreMultiCodeProduct>();
                var errors = new List<string>();
                var uuids = items
                    .Select(it => it.UUID)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!.Trim())
                    .Distinct()
                    .ToList();
                var storeCodes = items
                    .Select(it => it.StoreCode)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct()
                    .ToList();
                var productCodes = items
                    .Select(it => it.ProductCode)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim())
                    .Distinct()
                    .ToList();
                var multiCodeProductCodes = items
                    .Select(it => it.MultiCodeProductCode)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim())
                    .Distinct()
                    .ToList();
                var requestedFullKeys = items
                    .Where(it =>
                        !string.IsNullOrWhiteSpace(it.StoreCode)
                        && !string.IsNullOrWhiteSpace(it.ProductCode)
                        && !string.IsNullOrWhiteSpace(it.MultiCodeProductCode)
                    )
                    .Select(it =>
                        $"{it.StoreCode!.Trim()}|{it.ProductCode!.Trim()}|{it.MultiCodeProductCode!.Trim()}"
                    )
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requestedMainKeys = items
                    .Where(it =>
                        !string.IsNullOrWhiteSpace(it.StoreCode)
                        && !string.IsNullOrWhiteSpace(it.ProductCode)
                        && string.IsNullOrWhiteSpace(it.MultiCodeProductCode)
                    )
                    .Select(it => $"{it.StoreCode!.Trim()}|{it.ProductCode!.Trim()}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // 事务外只解析 UUID 当前归属，用于一次性确定稳定产品锁集合。
                var preflightUuidRows = uuids.Count == 0
                    ? new List<StoreMultiCodeProduct>()
                    : await db.Queryable<StoreMultiCodeProduct>()
                        .Where(x => x.UUID != null && uuids.Contains(x.UUID) && !x.IsDeleted)
                        .Select(x => new StoreMultiCodeProduct
                        {
                            UUID = x.UUID,
                            ProductCode = x.ProductCode,
                        })
                        .ToListAsync();
                var preflightByUuid = preflightUuidRows
                    .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                    .ToDictionary(x => x.UUID!);
                var lockProductCodes = productCodes
                    .Concat(
                        preflightUuidRows
                            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                            .Select(x => x.ProductCode!)
                    )
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lockProductCodes.Count == 0)
                {
                    return ApiResponse<BatchResultDtoMC>.Error(
                        "批量保存缺少可锁定的父商品编码",
                        "VALIDATION_ERROR"
                    );
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        lockProductCodes
                    );
                    var existing = uuids.Count == 0
                        ? new List<StoreMultiCodeProduct>()
                        : await db.Queryable<StoreMultiCodeProduct>()
                            .Where(x => x.UUID != null && uuids.Contains(x.UUID) && !x.IsDeleted)
                            .ToListAsync();
                    if (storeCodes.Any())
                    {
                        var keyQuery = db.Queryable<StoreMultiCodeProduct>()
                            .Where(x =>
                                x.StoreCode != null
                                && storeCodes.Contains(x.StoreCode)
                                && !x.IsDeleted
                            );
                        // 仅对非空列表使用 Contains，避免 SqlSugar 解析空列表时索引越界
                        if (productCodes.Any() && multiCodeProductCodes.Any())
                            keyQuery = keyQuery.Where(x =>
                                (x.ProductCode != null && productCodes.Contains(x.ProductCode))
                                || (
                                    x.MultiCodeProductCode != null
                                    && multiCodeProductCodes.Contains(x.MultiCodeProductCode)
                                )
                            );
                        else if (productCodes.Any())
                            keyQuery = keyQuery.Where(x =>
                                x.ProductCode != null && productCodes.Contains(x.ProductCode)
                            );
                        else if (multiCodeProductCodes.Any())
                            keyQuery = keyQuery.Where(x =>
                                x.MultiCodeProductCode != null
                                && multiCodeProductCodes.Contains(x.MultiCodeProductCode)
                            );
                        var keyRows = await keyQuery.ToListAsync();
                        existing.AddRange(
                            keyRows.Where(row =>
                                requestedFullKeys.Contains(
                                    $"{row.StoreCode}|{row.ProductCode}|{row.MultiCodeProductCode}"
                                )
                                || requestedMainKeys.Contains(
                                    $"{row.StoreCode}|{row.ProductCode}"
                                )
                            )
                        );
                    }
                    existing = existing
                        .GroupBy(x =>
                            !string.IsNullOrWhiteSpace(x.UUID)
                                ? $"UUID:{x.UUID}"
                                : $"KEY:{x.StoreCode}|{x.ProductCode}|{x.MultiCodeProductCode}"
                        )
                        .Select(group => group.First())
                        .ToList();
                    foreach (var uuid in uuids)
                    {
                        preflightByUuid.TryGetValue(uuid, out var expected);
                        var current = existing.FirstOrDefault(x => x.UUID == uuid);
                        if (
                            (expected == null) != (current == null)
                            || (
                                expected != null
                                && current != null
                                && !string.Equals(
                                    expected.ProductCode?.Trim(),
                                    current.ProductCode?.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        )
                        {
                            throw new InvalidOperationException(
                                $"多码记录 {uuid} 在等待业务锁期间归属发生变化，请重试"
                            );
                        }
                    }
                    var candidateProductCodes = productCodes
                        .Concat(existing
                            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                            .Select(x => x.ProductCode!))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    lockScope.EnsureCovers(db, candidateProductCodes);
                    var candidateChildCodes = multiCodeProductCodes
                        .Concat(existing
                            .Where(x => !string.IsNullOrWhiteSpace(x.MultiCodeProductCode))
                            .Select(x => x.MultiCodeProductCode!))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var activeSetChildren = candidateProductCodes.Count == 0
                        || candidateChildCodes.Count == 0
                        ? new List<ProductSetCode>()
                        : await db.Queryable<ProductSetCode>()
                            .Where(x =>
                                candidateProductCodes.Contains(x.ProductCode)
                                && x.SetProductCode != null
                                && candidateChildCodes.Contains(x.SetProductCode)
                                && (x.SetType == 1 || x.SetType == 2)
                                && x.IsActive
                                && !x.IsDeleted
                            )
                            .ToListAsync();
                    var activeSetChildKeys = activeSetChildren
                        .Select(x => $"{x.ProductCode}\u0001{x.SetProductCode}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var byUuid = existing
                        .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                        .ToDictionary(x => x.UUID);
                    var byKeyMain = existing.GroupBy(x => $"{x.StoreCode}|{x.ProductCode}").ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).First());
                    var byKeyMulti = existing
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.ProductCode)
                            && !string.IsNullOrWhiteSpace(x.MultiCodeProductCode)
                        )
                        .GroupBy(x =>
                            $"{x.StoreCode}|{x.ProductCode}|{x.MultiCodeProductCode}"
                        )
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).First());
                    foreach (var it in items)
                    {
                        try
                        {
                            StoreMultiCodeProduct? entity = null;
                            if (
                                !string.IsNullOrWhiteSpace(it.UUID)
                                && byUuid.TryGetValue(it.UUID!, out var foundByUuid)
                            )
                                entity = foundByUuid;
                            if (entity == null)
                            {
                                var sc = it.StoreCode?.Trim();
                                if (string.IsNullOrWhiteSpace(sc))
                                    throw new Exception("缺少关键键值(storeCode)");
                                if (!string.IsNullOrWhiteSpace(it.MultiCodeProductCode))
                                {
                                    var pc = it.ProductCode?.Trim();
                                    if (string.IsNullOrWhiteSpace(pc))
                                        throw new Exception("按子项编码匹配时缺少父商品编码(productCode)");
                                    var keyMulti = $"{sc}|{pc}|{it.MultiCodeProductCode!.Trim()}";
                                    if (byKeyMulti.TryGetValue(keyMulti, out var foundByKey))
                                        entity = foundByKey;
                                }
                                if (
                                    entity == null
                                    && string.IsNullOrWhiteSpace(it.MultiCodeProductCode)
                                    && !string.IsNullOrWhiteSpace(it.ProductCode)
                                )
                                {
                                    var pc = it.ProductCode?.Trim();
                                    var keyMain = $"{sc}|{pc}";
                                    if (byKeyMain.TryGetValue(keyMain, out var foundByKey))
                                        entity = foundByKey;
                                }
                            }
                            var targetProductCode = entity?.ProductCode ?? it.ProductCode;
                            var targetChildCode = entity?.MultiCodeProductCode ?? it.MultiCodeProductCode;
                            var isSetChild = !string.IsNullOrWhiteSpace(targetProductCode)
                                && !string.IsNullOrWhiteSpace(targetChildCode)
                                && activeSetChildKeys.Contains(
                                    $"{targetProductCode}\u0001{targetChildCode}"
                                );
                            if (entity == null)
                            {
                                var pc = it.ProductCode?.Trim();
                                if (string.IsNullOrWhiteSpace(pc))
                                    throw new Exception("缺少关键键值(storeCode/productCode)");
                                entity = new StoreMultiCodeProduct
                                {
                                    UUID = UuidHelper.GenerateUuid7(),
                                    StoreCode = it.StoreCode,
                                    ProductCode = it.ProductCode,
                                    MultiCodeProductCode = it.MultiCodeProductCode,
                                    StoreMultiCodeProductCode = !string.IsNullOrWhiteSpace(it.MultiCodeProductCode) ? it.StoreCode + it.MultiCodeProductCode : it.StoreCode + it.MultiCodeRetailPrice,
                                    PurchasePrice = isSetChild ? null : it.PurchasePrice,
                                    MultiCodeRetailPrice = it.MultiCodeRetailPrice,
                                    DiscountRate = it.DiscountRate,
                                    IsActive = it.IsActive ?? true,
                                    IsAutoPricing = it.IsAutoPricing ?? false,
                                    CreatedAt = now,
                                    UpdatedAt = now,
                                    CreatedBy = updatedBy,
                                    UpdatedBy = updatedBy,
                                    IsDeleted = false,
                                };
                                insertList.Add(entity);
                                var sk = $"{entity.StoreCode}|{entity.ProductCode}";
                                var skm = !string.IsNullOrWhiteSpace(entity.MultiCodeProductCode)
                                    ? $"{entity.StoreCode}|{entity.ProductCode}|{entity.MultiCodeProductCode}"
                                    : null;
                                if (!byKeyMain.ContainsKey(sk))
                                    byKeyMain[sk] = entity;
                                if (skm != null && !byKeyMulti.ContainsKey(skm))
                                    byKeyMulti[skm] = entity;
                            }
                            else
                            {
                                if (string.IsNullOrWhiteSpace(entity.StoreMultiCodeProductCode))
                                    entity.StoreMultiCodeProductCode = UuidHelper.GenerateUuid7();
                                if (it.PurchasePrice.HasValue && !isSetChild)
                                    entity.PurchasePrice = it.PurchasePrice;
                                if (it.MultiCodeRetailPrice.HasValue)
                                    entity.MultiCodeRetailPrice = it.MultiCodeRetailPrice;
                                if (it.DiscountRate.HasValue)
                                    entity.DiscountRate = it.DiscountRate;
                                if (it.IsActive.HasValue)
                                    entity.IsActive = it.IsActive.Value;
                                if (it.IsAutoPricing.HasValue)
                                    entity.IsAutoPricing = it.IsAutoPricing.Value;
                                entity.UpdatedAt = now;
                                entity.UpdatedBy = updatedBy;
                                updateList.Add(entity);
                            }
                        }
                        catch (Exception exItem)
                        {
                            errors.Add(exItem.Message);
                        }
                    }

                    if (insertList.Any())
                        await db.Insertable(insertList).ExecuteCommandAsync();
                    if (updateList.Any())
                        await db.Updateable(updateList)
                            .UpdateColumns(x => new
                            {
                                x.StoreMultiCodeProductCode,
                                x.PurchasePrice,
                                x.MultiCodeRetailPrice,
                                x.DiscountRate,
                                x.IsActive,
                                x.IsAutoPricing,
                                x.UpdatedAt,
                                x.UpdatedBy,
                            })
                            .ExecuteCommandAsync();

                    var affectedRows = insertList
                        .Concat(updateList)
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.ProductCode)
                            && !string.IsNullOrWhiteSpace(x.StoreCode)
                        )
                        .ToList();
                    if (affectedRows.Count > 0)
                    {
                        // 普通多码不受影响；统一服务只会重算匹配 Type1/Type2 的套装投影。
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoreGroupsLockedAsync(
                            lockScope,
                            affectedRows.Select(x => (x.StoreCode, x.ProductCode)),
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }
                    await db.Ado.CommitTranAsync();

                    var result = new BatchResultDtoMC
                    {
                        Inserted = insertList.Count,
                        Updated = updateList.Count,
                        Failed = errors.Count,
                        Errors = errors,
                    };
                    return ApiResponse<BatchResultDtoMC>.OK(result, "批量保存成功");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "StoreMultiCode 批量保存事务失败");
                    if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                    {
                        return ApiResponse<BatchResultDtoMC>.Error(
                            conflict!.Message,
                            SetChildPurchasePriceMutationLock.BusyErrorCode
                        );
                    }
                    return ApiResponse<BatchResultDtoMC>.Error(
                        "批量保存失败",
                        "BATCH_UPSERT_ERROR"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StoreMultiCode 批量保存失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    return ApiResponse<BatchResultDtoMC>.Error(
                        conflict!.Message,
                        SetChildPurchasePriceMutationLock.BusyErrorCode
                    );
                }
                return ApiResponse<BatchResultDtoMC>.Error("批量保存失败", "BATCH_UPSERT_ERROR");
            }
        }

        public async Task<ApiResponse<bool>> BatchUpdateSpecialFlagAsync(
            List<string> productCodes,
            bool isSpecial,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                await db.Ado.BeginTranAsync();
                try
                {
                    var count = await db.Updateable<Product>()
                        .SetColumns(x => x.IsSpecialProduct == isSpecial)
                        .SetColumns(x => x.UpdatedBy == updatedBy)
                        .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                        .Where(x => x.ProductCode != null && productCodes.Contains(x.ProductCode))
                        .ExecuteCommandAsync();
                    await db.Ado.CommitTranAsync();
                    return ApiResponse<bool>.OK(true, "已更新 " + count + " 条产品特殊标记");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "StoreMultiCode 批量更新特殊产品标记失败");
                    return ApiResponse<bool>.Error("批量更新失败", "BATCH_UPDATE_SPECIAL_ERROR");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StoreMultiCode 批量更新特殊产品标记失败");
                return ApiResponse<bool>.Error("批量更新失败", "BATCH_UPDATE_SPECIAL_ERROR");
            }
        }

        public async Task<ApiResponse<List<StoreMultiCodePriceListDto>>> GetListByUuidsAsync(
            List<string> uuids
        )
        {
            try
            {
                var db = _context.Db;
                var items = await db.Queryable<StoreMultiCodeProduct>()
                    .LeftJoin<Product>((mc, prod) => mc.ProductCode == prod.ProductCode)
                    .LeftJoin<Store>((mc, prod, st) => mc.StoreCode == st.StoreCode)
                    .Where((mc, prod, st) => mc.UUID != null && uuids.Contains(mc.UUID) && mc.IsDeleted == false)
                    .Select(
                        (mc, prod, st) =>
                            new StoreMultiCodePriceListDto
                            {
                                UUID = mc.UUID,
                                StoreCode = mc.StoreCode,
                                StoreName = st.StoreName,
                                ProductCode = mc.ProductCode,
                                ProductName = prod.ProductName,
                                ProductImage = prod.ProductImage,
                                ItemNumber = prod.ItemNumber,
                                MultiBarcode = mc.MultiBarcode,
                                PurchasePrice = mc.PurchasePrice,
                                MultiCodeRetailPrice = mc.MultiCodeRetailPrice,
                                DiscountRate = mc.DiscountRate,
                                IsActive = mc.IsActive,
                                IsAutoPricing = mc.IsAutoPricing,
                                UpdatedBy = mc.UpdatedBy,
                                UpdatedAt = mc.UpdatedAt,
                                IsSpecialProduct = prod.IsSpecialProduct,
                            }
                    )
                    .ToListAsync();

                return ApiResponse<List<StoreMultiCodePriceListDto>>.OK(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按UUID批量获取多码价格列表失败");
                return ApiResponse<List<StoreMultiCodePriceListDto>>.Error(
                    "查询失败",
                    "BATCH_BY_UUIDS_ERROR"
                );
            }
        }

        private ISugarQueryable<StoreMultiCodeProduct, Product, Store> ApplyText(
            ISugarQueryable<StoreMultiCodeProduct, Product, Store> query,
            string operation,
            string value,
            System.Linq.Expressions.Expression<System.Func<StoreMultiCodeProduct, string?>> selector
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreMultiCodeProduct),
                "mc"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            return operation switch
            {
                "equals" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.Equal(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "notequal" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.NotEqual(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "contains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "notcontains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.OrElse(
                            System.Linq.Expressions.Expression.Equal(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Not(
                                System.Linq.Expressions.Expression.Call(
                                    member,
                                    typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                    System.Linq.Expressions.Expression.Constant(value)
                                )
                            )
                        ),
                        newParam
                    )
                ),
                "startswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "endswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "blank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.Call(
                            typeof(string),
                            "IsNullOrEmpty",
                            null,
                            member
                        ),
                        newParam
                    )
                ),
                "notblank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreMultiCodeProduct,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.Not(
                            System.Linq.Expressions.Expression.Call(
                                typeof(string),
                                "IsNullOrEmpty",
                                null,
                                member
                            )
                        ),
                        newParam
                    )
                ),
                _ => query,
            };
        }

        private ISugarQueryable<StoreMultiCodeProduct, Product, Store> ApplyNumber(
            ISugarQueryable<StoreMultiCodeProduct, Product, Store> query,
            string? operation,
            System.Linq.Expressions.Expression<System.Func<
                StoreMultiCodeProduct,
                decimal?
            >> selector,
            decimal value,
            object? filterTo
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreMultiCodeProduct),
                "mc"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            var constantValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value),
                typeof(decimal?)
            );
            System.Linq.Expressions.Expression? condition = operation switch
            {
                "equals" => System.Linq.Expressions.Expression.Equal(member, constantValue),
                "notequal" => System.Linq.Expressions.Expression.NotEqual(member, constantValue),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, constantValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(
                    member,
                    constantValue
                ),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    constantValue
                ),
                "inrange" => filterTo != null
                && decimal.TryParse(filterTo.ToString(), out var toValue)
                    ? System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(
                            member,
                            constantValue
                        ),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(toValue),
                                typeof(decimal?)
                            )
                        )
                    )
                    : null,
                _ => null,
            };
            if (condition == null)
                return query;
            var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<
                StoreMultiCodeProduct,
                bool
            >>(condition, newParam);
            return query.Where(lambda);
        }

        public async Task<ApiResponse<BatchResultDtoMC>> UpsertForActiveStoresAsync(
            List<StoreMultiCodePriceUpsertForActiveStoresItemDto> items,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                var stores = await db.Queryable<Store>()
                    .Where(s => s.IsActive == true && s.IsDeleted == false)
                    .Select(s => s.StoreCode)
                    .ToListAsync();
                if (stores == null)
                    stores = new List<string>();
                var productCodes = items
                    .Select(it => it.ProductCode?.Trim())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 分店多码行必须挂在具体子码上：只展开父商品下启用的套装(1)/多码(2)子码。
                // 过去只带父商品编码，会按「分店+父商品」只改最近一行，匹配不到时还插入
                // MultiCodeProductCode 为空、编码为「分店+零售价」的垃圾行；无子码的普通商品在此直接跳过。
                var childCodes = productCodes.Count == 0 || stores.Count == 0
                    ? new List<ProductSetCode>()
                    : await db.Queryable<ProductSetCode>()
                        .Where(x =>
                            productCodes.Contains(x.ProductCode)
                            && x.SetProductCode != null
                            && (x.SetType == 1 || x.SetType == 2)
                            && x.IsActive
                            && !x.IsDeleted
                        )
                        .ToListAsync();
                if (childCodes.Count == 0)
                {
                    // 全是普通商品时没有可写的分店多码；返回成功，避免调用方把前面已完成的仓库/分店价更新报成失败。
                    return ApiResponse<BatchResultDtoMC>.OK(new BatchResultDtoMC(), "没有需要同步的分店多码");
                }

                var childProductCodes = childCodes
                    .Select(x => x.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var existingChildKeys = (await db.Queryable<StoreMultiCodeProduct>()
                        .Where(x =>
                            x.ProductCode != null
                            && childProductCodes.Contains(x.ProductCode)
                            && x.MultiCodeProductCode != null
                            && !x.IsDeleted
                        )
                        .Select(x => new { x.StoreCode, x.ProductCode, x.MultiCodeProductCode })
                        .ToListAsync())
                    .Select(x => $"{x.StoreCode}|{x.ProductCode}|{x.MultiCodeProductCode}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var childrenByProduct = childCodes
                    .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var upserts = new List<StoreMultiCodePriceUpsertItemDto>();
                foreach (var it in items)
                {
                    var productCode = it.ProductCode?.Trim();
                    if (string.IsNullOrWhiteSpace(productCode) || !childrenByProduct.TryGetValue(productCode, out var children))
                        continue;
                    foreach (var child in children)
                    {
                        var childRetailPrice = child.SetRetailPrice > 0 ? child.SetRetailPrice : null;
                        foreach (var sc in stores)
                        {
                            var rowExists = existingChildKeys.Contains($"{sc}|{child.ProductCode}|{child.SetProductCode}");
                            upserts.Add(
                                new StoreMultiCodePriceUpsertItemDto
                                {
                                    StoreCode = sc,
                                    ProductCode = child.ProductCode,
                                    MultiCodeProductCode = child.SetProductCode,
                                    // 套装/多码子项进货价由 BatchUpsertAsync 内的成本重算统一写回，这里仅作触发。
                                    PurchasePrice = it.PurchasePrice,
                                    // 子码零售价只取子码自己的零售价，与建档口径一致；调用方传来的父商品零售价只表示
                                    // 「需要同步零售价」。分店缺行时补齐也用子码价，避免成本重算因零售价为空而整批失败。
                                    MultiCodeRetailPrice = it.MultiCodeRetailPrice.HasValue || !rowExists ? childRetailPrice : null,
                                    DiscountRate = it.DiscountRate,
                                    IsActive = it.IsActive ?? true,
                                    IsAutoPricing = it.IsAutoPricing ?? false,
                                }
                            );
                        }
                    }
                }
                return await BatchUpsertAsync(upserts, updatedBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启用分店批量上架多码失败");
                return ApiResponse<BatchResultDtoMC>.Error(
                    "批量保存失败",
                    "UPSERT_ACTIVE_STORES_ERROR"
                );
            }
        }

        private sealed class ParamReplaceVisitor : System.Linq.Expressions.ExpressionVisitor
        {
            private readonly System.Linq.Expressions.ParameterExpression _source;
            private readonly System.Linq.Expressions.ParameterExpression _target;

            public ParamReplaceVisitor(
                System.Linq.Expressions.ParameterExpression source,
                System.Linq.Expressions.ParameterExpression target
            )
            {
                _source = source;
                _target = target;
            }

            protected override System.Linq.Expressions.Expression VisitParameter(
                System.Linq.Expressions.ParameterExpression node
            )
            {
                return node == _source ? _target : base.VisitParameter(node);
            }
        }
    }
}
