using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.SupplyNotices;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;
using SetChildPurchasePriceMutationLock = BlazorApp.Api.Services.ProductCosts.ProductCostMutationLock;
using SetChildPurchasePriceService = BlazorApp.Api.Services.ProductCosts.ProductCostRecalculationService;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed class ProductWarehouseMobileSlice
    : ProductWarehouseSliceBase,
      IProductWarehouseMobileSlice
{
    internal ProductWarehouseMobileSlice(ProductWarehouseSliceContext context)
        : base(context) { }

    public async Task<List<WarehouseMobileProductDto>> LookupMobileProductsAsync(string keyword)
    {
        var trimmed = keyword?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new List<WarehouseMobileProductDto>();
        }

        var joined = _context
            .Db.Queryable<WarehouseProduct>()
            .LeftJoin<Product>((w, p) => p.ProductCode == w.ProductCode && !p.IsDeleted)
            .LeftJoin<DomesticProduct>((w, p, dp) => dp.ProductCode == w.ProductCode && !dp.IsDeleted)
            .LeftJoin<ChinaSupplier>((w, p, dp, s) => dp.SupplierCode == s.SupplierCode && !s.IsDeleted)
            .LeftJoin<ProductLocation>((w, p, dp, s, pl) => pl.ProductCode == w.ProductCode && !pl.IsDeleted)
            .LeftJoin<Location>((w, p, dp, s, pl, l) => l.LocationGuid == pl.LocationGuid && !l.IsDeleted)
            .LeftJoin<ProductGrade>((w, p, dp, s, pl, l, pg) => pg.ProductCode == w.ProductCode && !pg.IsDeleted)
            .Where((w, p, dp, s, pl, l, pg) => !w.IsDeleted);

        if (_context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            // 原宽 OR 横跨 7 表，叠加 ORDER BY 货号 + TOP 50 的行目标，优化器会沿货号逐行联接并求值 9 个
            // CI 排序 LIKE，生产平均 2.2 秒、46 万逻辑读。改为先按来源表 UNION 候选编码、取货号前 50 个商品，
            // 外层联接只落在这些商品上；同规模压测逻辑读 21 万 → 0.7~1.4 万，CPU 10 秒级 → 0.3~0.5 秒。
            joined = joined.Where(
                MobileLookupSqlServerFilter,
                new
                {
                    mobileLookupPattern = BuildSqlServerContainsPattern(
                        trimmed.ToUpperInvariant()
                    ),
                }
            );
        }
        else
        {
            var lowered = trimmed.ToLower();
            joined = joined.Where((w, p, dp, s, pl, l, pg) =>
                (w.ProductCode != null && w.ProductCode.ToLower().Contains(lowered))
                || (p.ProductName != null && p.ProductName.ToLower().Contains(lowered))
                || (p.ItemNumber != null && p.ItemNumber.ToLower().Contains(lowered))
                || (p.Barcode != null && p.Barcode.ToLower().Contains(lowered))
                || (p.LocalSupplierCode != null && p.LocalSupplierCode.ToLower().Contains(lowered))
                || (s.SupplierName != null && s.SupplierName.ToLower().Contains(lowered))
                || (s.SupplierCode != null && s.SupplierCode.ToLower().Contains(lowered))
                || (l.LocationCode != null && l.LocationCode.ToLower().Contains(lowered))
                || (l.LocationBarcode != null && l.LocationBarcode.ToLower().Contains(lowered))
            );
        }

        var rows = await joined
            .OrderBy((w, p, dp, s, pl, l, pg) => p.ItemNumber)
            .Select((w, p, dp, s, pl, l, pg) => new
            {
                w.ProductCode,
                ProductName = p.ProductName,
                p.ItemNumber,
                p.Barcode,
                ProductImage = SqlFunc.IsNullOrEmpty(p.ProductImage) ? dp.ProductImage : p.ProductImage,
                p.ProductType,
                p.LocalSupplierCode,
                SupplierCode = dp.SupplierCode,
                SupplierName = s.SupplierName,
                Grade = pg.Grade,
                w.IsActive,
                p.PurchasePrice,
                p.RetailPrice,
                w.DomesticPrice,
                w.OEMPrice,
                w.ImportPrice,
                w.StockQuantity,
                MiddlePackageQuantity = p.MiddlePackageQuantity,
                PackingQuantity = SqlFunc.IsNull(w.PackingQuantity, dp.PackingQuantity),
                Volume = SqlFunc.IsNull(w.Volume, dp.UnitVolume),
                l.LocationGuid,
                l.LocationCode,
                l.LocationBarcode,
                UpdatedAt = SqlFunc.IsNull(p.UpdatedAt, w.UpdatedAt),
            })
            .Take(50)
            .ToListAsync();

        return rows
            .GroupBy(row => row.ProductCode)
            .Select(group => group.First())
            .Select(row => new WarehouseMobileProductDto
            {
                ProductCode = row.ProductCode,
                ProductName = row.ProductName ?? string.Empty,
                ItemNumber = row.ItemNumber,
                Barcode = row.Barcode,
                ProductImage = row.ProductImage,
                ProductType = row.ProductType,
                ProductTypeLabel = GetProductTypeLabel(row.ProductType),
                LocalSupplierCode = row.LocalSupplierCode,
                SupplierCode = row.SupplierCode,
                SupplierName = row.SupplierName,
                Grade = row.Grade,
                // 兼容新旧字段，移动端新字段与旧字段始终返回同值。
                WarehouseIsActive = row.IsActive,
                IsActive = row.IsActive,
                PurchasePrice = row.PurchasePrice,
                RetailPrice = row.RetailPrice,
                DomesticPrice = row.DomesticPrice,
                OEMPrice = row.OEMPrice,
                ImportPrice = row.ImportPrice,
                StockQuantity = row.StockQuantity,
                MiddlePackageQuantity = row.MiddlePackageQuantity,
                PackingQuantity = row.PackingQuantity,
                Volume = row.Volume,
                LocationGuid = row.LocationGuid,
                LocationCode = row.LocationCode,
                LocationBarcode = row.LocationBarcode,
                UpdatedAt = row.UpdatedAt,
            })
            .ToList();
    }

    internal const string MobileLookupPatternParameter = "@mobileLookupPattern";

    /// <summary>
    /// SQL Server 移动端查询过滤，接在 7 表联接之后，依赖 lambda 别名 [w]/[p]/[s]/[l]。
    /// 内层：四类来源（仓库编码、商品名称/货号/条码/本地供应商、国内供应商、货位）各自 UNION 出 varchar 候选编码，
    /// 再按货号取前 50 个商品；TOP 50 行必然落在货号前 50 个命中商品内，所以结果与原宽 OR 一致。
    /// 外层：保留 9 列谓词逐行复核，多货位商品仍只返回命中的那条货位行。
    /// 判别列写成字面量 [IsDeleted] = 0，才能命中 IsDeleted=0 过滤索引（参数化会让过滤索引失效）。
    /// </summary>
    internal static readonly string MobileLookupSqlServerFilter =
        "([w].[ProductCode] IN ("
        + "SELECT TOP 50 [mc].[ProductCode] FROM ("
        + "SELECT [mw].[ProductCode] FROM [WarehouseProduct] [mw]"
        + " WHERE [mw].[IsDeleted] = 0 AND " + SqlServerContainsMatch("[mw].[ProductCode]")
        + " UNION SELECT CAST([mp].[ProductCode] AS varchar(255)) FROM [Product] [mp]"
        + " WHERE [mp].[IsDeleted] = 0 AND [mp].[ProductCode] IS NOT NULL AND ("
        + SqlServerContainsMatch("[mp].[ProductName]")
        + " OR " + SqlServerContainsMatch("[mp].[ItemNumber]")
        + " OR " + SqlServerContainsMatch("[mp].[Barcode]")
        + " OR " + SqlServerContainsMatch("[mp].[LocalSupplierCode]") + ")"
        + " UNION SELECT CAST([mdp].[ProductCode] AS varchar(255)) FROM [DomesticProduct] [mdp]"
        + " INNER JOIN [ChinaSupplier] [ms] ON [ms].[SupplierCode] = [mdp].[SupplierCode] AND [ms].[IsDeleted] = 0"
        + " WHERE [mdp].[IsDeleted] = 0 AND ("
        + SqlServerContainsMatch("[ms].[SupplierName]")
        + " OR " + SqlServerContainsMatch("[ms].[SupplierCode]") + ")"
        + " UNION SELECT [mpl].[ProductCode] FROM [ProductLocation] [mpl]"
        + " INNER JOIN [Location] [ml] ON [ml].[LocationGuid] = [mpl].[LocationGuid] AND [ml].[IsDeleted] = 0"
        + " WHERE [mpl].[IsDeleted] = 0 AND ("
        + SqlServerContainsMatch("[ml].[LocationCode]")
        + " OR " + SqlServerContainsMatch("[ml].[LocationBarcode]") + ")"
        + ") [mc]([ProductCode])"
        // 候选已统一为 varchar，与仓库编码直接比较不转换索引列；与 Product 比较时转换落在候选侧。
        + " INNER JOIN [WarehouseProduct] [mcw] ON [mcw].[ProductCode] = [mc].[ProductCode] AND [mcw].[IsDeleted] = 0"
        + " LEFT JOIN [Product] [mcp] ON [mcp].[ProductCode] = [mc].[ProductCode] AND [mcp].[IsDeleted] = 0"
        + " ORDER BY [mcp].[ItemNumber])"
        + " AND (" + SqlServerContainsMatch("[w].[ProductCode]")
        + " OR " + SqlServerContainsMatch("[p].[ProductName]")
        + " OR " + SqlServerContainsMatch("[p].[ItemNumber]")
        + " OR " + SqlServerContainsMatch("[p].[Barcode]")
        + " OR " + SqlServerContainsMatch("[p].[LocalSupplierCode]")
        + " OR " + SqlServerContainsMatch("[s].[SupplierName]")
        + " OR " + SqlServerContainsMatch("[s].[SupplierCode]")
        + " OR " + SqlServerContainsMatch("[l].[LocationCode]")
        + " OR " + SqlServerContainsMatch("[l].[LocationBarcode]") + "))";

    /// <summary>
    /// UPPER + BIN2 逐字节比较替代 CI 排序规则 LIKE，大小写不敏感由 UPPER 保证。
    /// 先转 nvarchar 再 COLLATE：varchar 列直接 COLLATE 到 Latin1 会按 1252 代码页重解释，中文永远匹配不到。
    /// </summary>
    private static string SqlServerContainsMatch(string column) =>
        "UPPER(CAST(" + column + " AS nvarchar(4000))) COLLATE Latin1_General_100_BIN2 LIKE "
        + MobileLookupPatternParameter;

    /// <summary>把用户输入里的 LIKE 通配符按方括号转义后包成包含匹配，输入须已转大写。</summary>
    internal static string BuildSqlServerContainsPattern(string upperKeyword)
    {
        var escaped = upperKeyword
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }

    public async Task<WarehouseMobileProductDto?> GetMobileProductAsync(string productCode)
    {
        var trimmed = productCode?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return await FindMobileProductByCodeAsync(trimmed);
    }

    public Task<WarehouseMobileProductDto?> PatchMobileProductAsync(
        string productCode,
        WarehouseMobileProductPatchDto dto
    )
    {
        return PatchMobileProductAsync(productCode, dto, SystemUpdatedBy);
    }

    public async Task<WarehouseMobileProductDto?> PatchMobileProductAsync(
        string productCode,
        WarehouseMobileProductPatchDto dto,
        string? updatedBy
    )
    {
        var product = await _context
            .Db.Queryable<Product>()
            .Where(p => p.ProductCode == productCode && !p.IsDeleted)
            .FirstAsync();
        var warehouseProduct = await _context
            .Db.Queryable<WarehouseProduct>()
            .Where(w => w.ProductCode == productCode && !w.IsDeleted)
            .FirstAsync();

        if (product == null || warehouseProduct == null)
        {
            return null;
        }

        var effectiveUpdatedBy = ResolveUpdatedBy(updatedBy);

        var domesticProduct = await _context
            .Db.Queryable<DomesticProduct>()
            .Where(dp => dp.ProductCode == productCode && !dp.IsDeleted)
            .FirstAsync();
        var productGrade = await _context
            .Db.Queryable<ProductGrade>()
            .Where(pg => pg.ProductCode == productCode && !pg.IsDeleted)
            .FirstAsync();
        var shouldInsertProductGrade = false;

        var now = DateTime.UtcNow;
        var shouldUpdateProduct = false;
        var shouldUpdateWarehouseProduct = false;
        var shouldUpdateDomesticProduct = false;
        var shouldUpdateProductGrade = false;
        // 仅更新仓库商品状态；优先新字段，旧字段仅作兼容回退。
        var warehouseIsActive = dto.WarehouseIsActive ?? dto.IsActive;
        // 供货说明只在下架时有意义；录入有误直接拒绝，此时尚未开事务。
        NormalizedSupplyNotice? supplyNotice = null;
        if (warehouseIsActive == false && dto.SupplyNotice != null)
        {
            var (normalizedNotice, noticeError) = WarehouseProductSupplyNoticeRules.Normalize(
                dto.SupplyNotice
            );
            if (noticeError != null)
            {
                throw new ArgumentException(noticeError);
            }
            supplyNotice = normalizedNotice;
        }
        if (warehouseIsActive.HasValue)
        {
            warehouseProduct.IsActive = warehouseIsActive.Value;
            shouldUpdateWarehouseProduct = true;
        }

        var syncedPurchasePrice = ResolveLinkedPrice(
            dto.PurchasePrice,
            dto.ImportPrice,
            "进货价",
            "进口价"
        );
        var syncedRetailPrice = ResolveLinkedPrice(
            dto.RetailPrice,
            dto.OEMPrice,
            "零售价",
            "RRP"
        );
        var shouldSyncStorePurchasePrice = syncedPurchasePrice.HasValue;
        // 不传 = 同步（统一默认勾选）；旧版 App 会显式传 false，行为保持「不动分店价」。
        var priceSyncPolicy = WarehousePriceSyncPolicy.From(dto.SyncStoreRetailPrices, dto.GenerateTasks);
        var shouldSyncStoreRetailPrice = priceSyncPolicy.SyncStoreRetailPrices
            && syncedRetailPrice.HasValue;

        if (syncedPurchasePrice.HasValue)
        {
            product.PurchasePrice = syncedPurchasePrice;
            shouldUpdateProduct = true;
            warehouseProduct.ImportPrice = syncedPurchasePrice;
            shouldUpdateWarehouseProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.ImportPrice = syncedPurchasePrice;
                shouldUpdateDomesticProduct = true;
            }
        }
        if (syncedRetailPrice.HasValue)
        {
            product.RetailPrice = syncedRetailPrice;
            shouldUpdateProduct = true;
            warehouseProduct.OEMPrice = syncedRetailPrice;
            shouldUpdateWarehouseProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.OEMPrice = syncedRetailPrice;
                shouldUpdateDomesticProduct = true;
            }
        }
        if (dto.MiddlePackageQuantity.HasValue)
        {
            product.MiddlePackageQuantity = dto.MiddlePackageQuantity;
            shouldUpdateProduct = true;
        }
        if (dto.ProductImage != null)
        {
            product.ProductImage = dto.ProductImage;
            shouldUpdateProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.ProductImage = dto.ProductImage;
                shouldUpdateDomesticProduct = true;
            }
        }

        if (dto.DomesticPrice.HasValue)
        {
            warehouseProduct.DomesticPrice = dto.DomesticPrice;
            shouldUpdateWarehouseProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.DomesticPrice = dto.DomesticPrice;
                shouldUpdateDomesticProduct = true;
            }
        }
        if (dto.StockQuantity.HasValue)
        {
            warehouseProduct.StockQuantity = dto.StockQuantity;
            shouldUpdateWarehouseProduct = true;
        }
        if (dto.PackingQuantity.HasValue)
        {
            warehouseProduct.PackingQuantity = dto.PackingQuantity;
            shouldUpdateWarehouseProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.PackingQuantity = dto.PackingQuantity;
                shouldUpdateDomesticProduct = true;
            }
        }
        if (dto.Volume.HasValue)
        {
            warehouseProduct.Volume = dto.Volume;
            shouldUpdateWarehouseProduct = true;
            if (domesticProduct != null)
            {
                domesticProduct.UnitVolume = dto.Volume;
                shouldUpdateDomesticProduct = true;
            }
        }
        if (dto.MiddlePackageQuantity.HasValue && domesticProduct != null)
        {
            domesticProduct.MiddlePackQuantity = dto.MiddlePackageQuantity;
            shouldUpdateDomesticProduct = true;
        }

        if (dto.Grade != null)
        {
            var normalizedGrade = dto.Grade.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalizedGrade))
            {
                if (productGrade != null)
                {
                    productGrade.IsDeleted = true;
                    productGrade.UpdatedAt = now;
                    shouldUpdateProductGrade = true;
                }
            }
            else if (productGrade != null)
            {
                productGrade.Grade = normalizedGrade;
                productGrade.UpdatedAt = now;
                shouldUpdateProductGrade = true;
            }
            else
            {
                productGrade = new ProductGrade
                {
                    ProductCode = productCode,
                    Grade = normalizedGrade,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                shouldInsertProductGrade = true;
                shouldUpdateProductGrade = true;
            }
        }

        if (shouldUpdateProduct)
        {
            product.UpdatedAt = now;
        }
        // 任一移动端可编辑字段发生变化时，都刷新仓库商品审计信息。
        // 即使实际字段位于 Product、DomesticProduct 或 ProductGrade，列表也从 WarehouseProduct 展示更新人。
        if (shouldUpdateProduct || shouldUpdateWarehouseProduct || shouldUpdateDomesticProduct || shouldUpdateProductGrade)
        {
            warehouseProduct.UpdatedAt = now;
            warehouseProduct.UpdatedBy = effectiveUpdatedBy;
            shouldUpdateWarehouseProduct = true;
        }
        if (domesticProduct != null && shouldUpdateDomesticProduct)
        {
            domesticProduct.UpdatedAt = now;
        }

        await _context.Db.Ado.BeginTranAsync();
        try
        {
            var setChildPurchasePriceLock =
                await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                    _context.Db,
                    new[] { productCode }
                );
            // 与批量、完整编辑和表格 PATCH 统一先锁 WarehouseProduct，避免跨表反向等待。
            var lockedWarehouseProduct = await WithWarehouseProductUpdateLock(
                    _context
                        .Db.Queryable<WarehouseProduct>()
                        .Where(w => w.ProductCode == productCode && !w.IsDeleted)
                )
                .FirstAsync();
            if (lockedWarehouseProduct == null)
            {
                await _context.Db.Ado.RollbackTranAsync();
                return null;
            }
            var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                new[] { productCode }
            );

            if (shouldUpdateProduct)
            {
                // 仅回写移动端本次涉及的 Product 列，避免覆盖并发修改的基础商品字段。
                var productUpdate = _context
                    .Db.Updateable<Product>()
                    .SetColumns(p => p.UpdatedAt == now)
                    .SetColumns(p => p.UpdatedBy == effectiveUpdatedBy)
                    .Where(p => p.UUID == product.UUID && !p.IsDeleted);
                if (syncedPurchasePrice.HasValue)
                {
                    productUpdate = productUpdate.SetColumns(p =>
                        p.PurchasePrice == syncedPurchasePrice.Value
                    );
                }
                if (syncedRetailPrice.HasValue)
                {
                    productUpdate = productUpdate.SetColumns(p =>
                        p.RetailPrice == syncedRetailPrice.Value
                    );
                }
                if (dto.MiddlePackageQuantity.HasValue)
                {
                    productUpdate = productUpdate.SetColumns(p =>
                        p.MiddlePackageQuantity == dto.MiddlePackageQuantity.Value
                    );
                }
                if (dto.ProductImage != null)
                {
                    productUpdate = productUpdate.SetColumns(p => p.ProductImage == dto.ProductImage);
                }
                await productUpdate.ExecuteCommandAsync();
            }
            if (shouldUpdateWarehouseProduct)
            {
                // 移动端只写本次请求涉及的仓库字段，避免读后全实体回写覆盖并发库存或价格。
                var warehouseUpdate = _context
                    .Db.Updateable<WarehouseProduct>()
                    .SetColumns(w => w.UpdatedAt == now)
                    .SetColumns(w => w.UpdatedBy == effectiveUpdatedBy)
                    .Where(w => w.ProductCode == productCode && !w.IsDeleted);
                if (warehouseIsActive.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.IsActive == warehouseIsActive.Value
                    );
                }
                if (syncedPurchasePrice.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.ImportPrice == syncedPurchasePrice.Value
                    );
                }
                if (syncedRetailPrice.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.OEMPrice == syncedRetailPrice.Value
                    );
                }
                if (dto.DomesticPrice.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.DomesticPrice == dto.DomesticPrice.Value
                    );
                }
                if (dto.StockQuantity.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.StockQuantity == dto.StockQuantity.Value
                    );
                }
                if (dto.PackingQuantity.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w =>
                        w.PackingQuantity == dto.PackingQuantity.Value
                    );
                }
                if (dto.Volume.HasValue)
                {
                    warehouseUpdate = warehouseUpdate.SetColumns(w => w.Volume == dto.Volume.Value);
                }
                await warehouseUpdate.ExecuteCommandAsync();

                if (warehouseIsActive.HasValue)
                {
                    // 同一事务：下架登记供货说明，上架关闭说明。
                    await WarehouseProductSupplyNoticeWriter.ApplyStatusChangeAsync(
                        _context.Db,
                        new[] { productCode },
                        warehouseIsActive.Value,
                        supplyNotice,
                        effectiveUpdatedBy,
                        source: "MobileWarehouse",
                        DateTime.UtcNow
                    );
                }
            }
            if (domesticProduct != null && shouldUpdateDomesticProduct)
            {
                // 国内商品同样只写本次 DTO 驱动的联动列，避免全实体更新覆盖并发包装数据。
                var domesticProductUpdate = _context
                    .Db.Updateable<DomesticProduct>()
                    .SetColumns(dp => dp.UpdatedAt == now)
                    .SetColumns(dp => dp.UpdatedBy == effectiveUpdatedBy)
                    .Where(dp => dp.ProductCode == domesticProduct.ProductCode && !dp.IsDeleted);
                if (syncedPurchasePrice.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.ImportPrice == syncedPurchasePrice.Value
                    );
                }
                if (syncedRetailPrice.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.OEMPrice == syncedRetailPrice.Value
                    );
                }
                if (dto.ProductImage != null)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.ProductImage == dto.ProductImage
                    );
                }
                if (dto.DomesticPrice.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.DomesticPrice == dto.DomesticPrice.Value
                    );
                }
                if (dto.PackingQuantity.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.PackingQuantity == dto.PackingQuantity.Value
                    );
                }
                if (dto.Volume.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.UnitVolume == dto.Volume.Value
                    );
                }
                if (dto.MiddlePackageQuantity.HasValue)
                {
                    domesticProductUpdate = domesticProductUpdate.SetColumns(dp =>
                        dp.MiddlePackQuantity == dto.MiddlePackageQuantity.Value
                    );
                }
                await domesticProductUpdate.ExecuteCommandAsync();
            }
            if (productGrade != null && shouldUpdateProductGrade)
            {
                if (shouldInsertProductGrade)
                {
                    await _context.Db.Insertable(productGrade).ExecuteCommandAsync();
                }
                else
                {
                    await _context.Db.Updateable(productGrade).ExecuteCommandAsync();
                }
            }
            if (shouldSyncStorePurchasePrice || shouldSyncStoreRetailPrice)
            {
                product = await _context
                    .Db.Queryable<Product>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();
                if (product == null)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    return null;
                }
            }
            if (shouldSyncStorePurchasePrice)
            {
                await UpsertActiveStoreRetailPricesAsync(
                    product,
                    purchasePrice: syncedPurchasePrice,
                    retailPrice: null,
                    now,
                    MobileWarehousePricePatchUpdatedBy
                );
            }
            if (shouldSyncStoreRetailPrice)
            {
                await UpsertActiveStoreRetailPricesAsync(
                    product,
                    purchasePrice: null,
                    retailPrice: syncedRetailPrice,
                    now,
                    MobileWarehousePricePatchUpdatedBy,
                    priceSyncPolicy
                );
            }

            if (syncedPurchasePrice.HasValue)
            {
                var recalculation = await new SetChildPurchasePriceService(
                    _context.Db
                ).RecalculateLockedAsync(
                    setChildPurchasePriceLock,
                    new[] { productCode },
                    storeCodes: null,
                    updatedBy: effectiveUpdatedBy
                );
                EnsureSetChildPurchasePriceRecalculated(
                    recalculation,
                    "移动端更新后的套装子项成本重算不完整"
                );
            }

            await RecordProductChangeHistoryAsync(
                beforeSnapshots,
                new[] { productCode },
                action: "Patch",
                source: "MobileWarehouse",
                actorName: effectiveUpdatedBy,
                skipPriceTasks: !priceSyncPolicy.GenerateTasks
            );

            await _context.Db.Ado.CommitTranAsync();
        }
        catch
        {
            await _context.Db.Ado.RollbackTranAsync();
            throw;
        }

        return await GetMobileProductAsync(productCode);
    }

    private static decimal? ResolveLinkedPrice(
        decimal? masterPrice,
        decimal? warehousePrice,
        string masterLabel,
        string warehouseLabel
    )
    {
        if (masterPrice.HasValue && warehousePrice.HasValue && masterPrice.Value != warehousePrice.Value)
        {
            throw new InvalidOperationException($"{masterLabel}和{warehouseLabel}不一致");
        }

        return masterPrice ?? warehousePrice;
    }

    public async Task<WarehouseMobileProductDto?> SetMobileProductLocationAsync(
        string productCode,
        string? locationGuid
    )
    {
        var trimmedProductCode = productCode?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedProductCode))
        {
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 绑定失败：商品编码为空 ProductCode={productCode}, LocationGuid={locationGuid}"
            );
            return null;
        }

        Console.WriteLine(
            $"[ProductWarehouseReactService.SetMobileProductLocation] 开始绑定 ProductCode={trimmedProductCode}, LocationGuid={locationGuid}"
        );

        var warehouseProduct = await _context
            .Db.Queryable<WarehouseProduct>()
            .Where(w => w.ProductCode == trimmedProductCode && !w.IsDeleted)
            .FirstAsync();
        var product = await _context
            .Db.Queryable<Product>()
            .Where(p => p.ProductCode == trimmedProductCode && !p.IsDeleted)
            .FirstAsync();
        if (warehouseProduct == null || product == null)
        {
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 绑定失败：商品不存在 ProductCode={trimmedProductCode}, WarehouseProductExists={warehouseProduct != null}, ProductExists={product != null}"
            );
            return null;
        }

        Console.WriteLine(
            $"[ProductWarehouseReactService.SetMobileProductLocation] 命中商品 ProductCode={trimmedProductCode}, ItemNumber={product.ItemNumber}, Barcode={product.Barcode}"
        );

        var trimmedLocationGuid = locationGuid?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedLocationGuid))
        {
            var location = await _context
                .Db.Queryable<Location>()
                .Where(l => l.LocationGuid == trimmedLocationGuid && !l.IsDeleted)
                .FirstAsync();
            if (location == null)
            {
                Console.WriteLine(
                    $"[ProductWarehouseReactService.SetMobileProductLocation] 绑定失败：货位不存在 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}"
                );
                throw new InvalidOperationException("货位不存在");
            }

            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 命中货位 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}, LocationCode={location.LocationCode}, LocationBarcode={location.LocationBarcode}, LocationType={location.LocationType}"
            );

            var existingSameBinding = await _context
                .Db.Queryable<ProductLocation>()
                .AnyAsync(pl =>
                    !pl.IsDeleted
                    && pl.ProductCode == trimmedProductCode
                    && pl.LocationGuid == trimmedLocationGuid
                );
            if (existingSameBinding)
            {
                Console.WriteLine(
                    $"[ProductWarehouseReactService.SetMobileProductLocation] 已存在相同绑定，直接返回 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}, LocationCode={location.LocationCode}"
                );
                return await GetMobileProductAsync(trimmedProductCode);
            }

            if (location.LocationType != 2)
            {
                var existingLocationProduct = await _context
                    .Db.Queryable<ProductLocation>()
                    .Where(pl =>
                        !pl.IsDeleted
                        && pl.LocationGuid == trimmedLocationGuid
                        && pl.ProductCode != trimmedProductCode
                    )
                    .FirstAsync();
                if (existingLocationProduct != null)
                {
                    Console.WriteLine(
                        $"[ProductWarehouseReactService.SetMobileProductLocation] 绑定失败：配货位已有商品 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}, LocationCode={location.LocationCode}, ExistingProductCode={existingLocationProduct.ProductCode}"
                    );
                    throw new InvalidOperationException("该配货位已有商品，不能继续绑定");
                }

                var existingPickingLocation = await _context
                    .Db.Queryable<ProductLocation, Location>((pl, l) => new JoinQueryInfos(
                        JoinType.Inner,
                        pl.LocationGuid == l.LocationGuid
                    ))
                    .Where((pl, l) =>
                        !pl.IsDeleted
                        && !l.IsDeleted
                        && pl.ProductCode == trimmedProductCode
                        && l.LocationType != 2
                    )
                    .Select((pl, l) => new { pl.LocationGuid, l.LocationCode })
                    .FirstAsync();
                if (existingPickingLocation != null)
                {
                    Console.WriteLine(
                        $"[ProductWarehouseReactService.SetMobileProductLocation] 商品已有配货位，将移动到新货位 ProductCode={trimmedProductCode}, ExistingLocationGuid={existingPickingLocation.LocationGuid}, ExistingLocationCode={existingPickingLocation.LocationCode}, NewLocationGuid={trimmedLocationGuid}, NewLocationCode={location.LocationCode}"
                    );
                }
            }
        }
        else
        {
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 准备清空商品货位 ProductCode={trimmedProductCode}"
            );
        }

        await _context.Db.Ado.BeginTranAsync();
        try
        {
            var deletedCount = await _context
                .Db.Deleteable<ProductLocation>()
                .Where(pl => pl.ProductCode == trimmedProductCode)
                .ExecuteCommandAsync();
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 删除旧绑定 ProductCode={trimmedProductCode}, DeletedCount={deletedCount}"
            );

            if (!string.IsNullOrWhiteSpace(trimmedLocationGuid))
            {
                await _context
                    .Db.Insertable(new ProductLocation
                    {
                        Guid = Guid.NewGuid().ToString(),
                        ProductCode = trimmedProductCode,
                        LocationGuid = trimmedLocationGuid,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    })
                    .ExecuteCommandAsync();
                Console.WriteLine(
                    $"[ProductWarehouseReactService.SetMobileProductLocation] 插入新绑定 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}"
                );
            }

            await _context.Db.Ado.CommitTranAsync();
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 事务提交成功 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}"
            );
        }
        catch (Exception ex)
        {
            await _context.Db.Ado.RollbackTranAsync();
            Console.WriteLine(
                $"[ProductWarehouseReactService.SetMobileProductLocation] 事务回滚 ProductCode={trimmedProductCode}, LocationGuid={trimmedLocationGuid}, Error={ex}"
            );
            throw;
        }

        return await GetMobileProductAsync(trimmedProductCode);
    }

    private async Task<WarehouseMobileProductDto?> FindMobileProductByCodeAsync(string productCode)
    {
        var row = await _context
            .Db.Queryable<WarehouseProduct>()
            .LeftJoin<Product>((w, p) => p.ProductCode == w.ProductCode && !p.IsDeleted)
            .LeftJoin<DomesticProduct>(
                (w, p, dp) => dp.ProductCode == w.ProductCode && !dp.IsDeleted
            )
            .LeftJoin<ChinaSupplier>(
                (w, p, dp, s) => dp.SupplierCode == s.SupplierCode && !s.IsDeleted
            )
            .LeftJoin<ProductLocation>(
                (w, p, dp, s, pl) => pl.ProductCode == w.ProductCode && !pl.IsDeleted
            )
            .LeftJoin<Location>(
                (w, p, dp, s, pl, l) => l.LocationGuid == pl.LocationGuid && !l.IsDeleted
            )
            .LeftJoin<ProductGrade>(
                (w, p, dp, s, pl, l, pg) => pg.ProductCode == w.ProductCode && !pg.IsDeleted
            )
            .Where((w, p, dp, s, pl, l, pg) =>
                !w.IsDeleted && w.ProductCode == productCode
            )
            .Select((w, p, dp, s, pl, l, pg) => new WarehouseMobileProductDto
            {
                ProductCode = w.ProductCode,
                ProductName = p.ProductName ?? string.Empty,
                ItemNumber = p.ItemNumber,
                Barcode = p.Barcode,
                ProductImage = SqlFunc.IsNullOrEmpty(p.ProductImage) ? dp.ProductImage : p.ProductImage,
                ProductType = p.ProductType,
                ProductTypeLabel = p.ProductType == 1
                    ? "套装商品"
                    : p.ProductType == 2
                        ? "多码商品"
                        : "普通商品",
                LocalSupplierCode = p.LocalSupplierCode,
                SupplierCode = dp.SupplierCode,
                SupplierName = s.SupplierName,
                Grade = pg.Grade,
                // 兼容新旧字段，移动端新字段与旧字段始终返回同值。
                WarehouseIsActive = w.IsActive,
                IsActive = w.IsActive,
                PurchasePrice = p.PurchasePrice,
                RetailPrice = p.RetailPrice,
                DomesticPrice = w.DomesticPrice,
                OEMPrice = w.OEMPrice,
                ImportPrice = w.ImportPrice,
                StockQuantity = w.StockQuantity,
                MiddlePackageQuantity = p.MiddlePackageQuantity,
                PackingQuantity = SqlFunc.IsNull(w.PackingQuantity, dp.PackingQuantity),
                Volume = SqlFunc.IsNull(w.Volume, dp.UnitVolume),
                LocationGuid = l.LocationGuid,
                LocationCode = l.LocationCode,
                LocationBarcode = l.LocationBarcode,
                UpdatedAt = SqlFunc.IsNull(p.UpdatedAt, w.UpdatedAt),
            })
            .FirstAsync();

        return row;
    }

    public async Task<WarehouseProductLabelPrintDto?> GetMobileProductPrintPayloadAsync(string productCode)
    {
        var item = await GetMobileProductAsync(productCode);
        if (item == null)
        {
            return null;
        }

        return new WarehouseProductLabelPrintDto
        {
            ProductCode = item.ProductCode,
            ProductName = item.ProductName,
            ItemNumber = item.ItemNumber,
            Barcode = item.Barcode,
            SupplierName = item.SupplierName,
            RetailPrice = item.RetailPrice,
            DomesticPrice = item.DomesticPrice,
            OEMPrice = item.OEMPrice,
            ImportPrice = item.ImportPrice,
            MiddlePackageQuantity = item.MiddlePackageQuantity,
            LocationCode = item.LocationCode,
            LocationBarcode = item.LocationBarcode,
        };
    }

    public async Task<WarehouseLocationLabelPrintDto?> GetMobileLocationPrintPayloadAsync(string productCode)
    {
        var item = await GetMobileProductAsync(productCode);
        if (item == null || string.IsNullOrWhiteSpace(item.LocationGuid))
        {
            return null;
        }

        var productCount = await _context
            .Db.Queryable<ProductLocation>()
            .Where(pl => !pl.IsDeleted && pl.LocationGuid == item.LocationGuid)
            .CountAsync();

        return new WarehouseLocationLabelPrintDto
        {
            LocationGuid = item.LocationGuid,
            LocationCode = item.LocationCode,
            LocationBarcode = item.LocationBarcode,
            ItemNumber = item.ItemNumber,
            ProductName = item.ProductName,
            MiddlePackageQuantity = item.MiddlePackageQuantity,
            ProductCount = productCount,
        };
    }

    private static string GetProductTypeLabel(int? productType)
    {
        return productType switch
        {
            0 => "普通",
            1 => "套装",
            2 => "多码",
            _ => "未知",
        };
    }

    /// <summary>
    /// 保留旧实现的内部转换入口，避免反射调用兼容性变化。
    /// </summary>
    private int? SafeConvertToInt(decimal? value)
    {
        if (value == null)
            return null;
        return (int)value.Value;
    }

    /// <summary>
    /// 保留旧实现约定：1 = true，其他值 = false。
    /// </summary>
    private bool ConvertToBool(int? value)
    {
        return value == 1;
    }
}
