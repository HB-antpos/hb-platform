using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

/// <summary>商品分店日统计的纯领域规则；不访问数据库或事务。</summary>
internal static class SalesStatisticsProductStoreDailyDomainRules
{
    internal static string ResolveStatisticSupplierCode(
        string? mappedSupplierCode,
        string? detailSupplierCode)
    {
        var supplierCode = SalesStatisticsCodeRules.Normalize(mappedSupplierCode);
        if (!string.IsNullOrWhiteSpace(supplierCode))
            return supplierCode;

        supplierCode = SalesStatisticsCodeRules.Normalize(detailSupplierCode);
        return !string.IsNullOrWhiteSpace(supplierCode)
            ? supplierCode
            : SalesStatisticsCodeRules.UnknownSupplierCode;
    }

    /// <summary>已支付但没有商品明细的订单（只有表头）；按订单号去重后用于计算对账扣除项。</summary>
    internal readonly record struct HeaderOnlyOrder(string? OrderGuid, string? BranchCode, string? DeviceCode);

    /// <summary>
    /// 「无明细订单」的分店营业额扣除项（负数，按分店汇总）。
    /// POSM 偶有已支付订单只有表头、明细没有上传：分店营业额按订单支付金额计入，商品统计却无法把它归属到任何商品，
    /// 对账差额恰好等于这些订单的支付金额。差额一旦超过容差，当天商品统计与供应商统计会永久 Failed，
    /// 区间包含这一天的商品报告整份打不开。这类订单本来就不可能归属商品，所以对账前先从分店营业额里扣掉；
    /// 扣除后仍对不上的差额才是真异常，照常失败。
    /// 金额用支付金额，与商品统计「按订单支付金额分摊」的口径一致；查不到支付记录的订单不扣
    /// （对账口径是按支付计，查不到本来就算 0）；数量不扣（分店数量口径不参与失败判定）。
    /// </summary>
    internal static List<ProductStoreDailyBranchRollup> BuildHeaderOnlyOrderDeductions(
        IEnumerable<HeaderOnlyOrder> orders,
        IReadOnlyDictionary<string, string> deviceBranchMap,
        IReadOnlyDictionary<string, decimal> paymentAmounts) =>
        orders
            .Where(order => !string.IsNullOrWhiteSpace(order.OrderGuid))
            .GroupBy(order => SalesStatisticsCodeRules.Normalize(order.OrderGuid), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(first.BranchCode, first.DeviceCode, deviceBranchMap),
                    PaymentAmount = paymentAmounts.TryGetValue(group.Key, out var amount) ? amount : 0m,
                };
            })
            .Where(order => !string.IsNullOrWhiteSpace(order.BranchCode) && order.PaymentAmount != 0m)
            .GroupBy(order => order.BranchCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductStoreDailyBranchRollup(group.Key, -group.Sum(order => order.PaymentAmount), 0))
            .ToList();

    internal static Dictionary<string, decimal> BuildOrderAmountMap(IEnumerable<OrderAmountRow> rows) =>
        rows
            .Where(row => !string.IsNullOrWhiteSpace(row.OrderGuid))
            .GroupBy(row => row.OrderGuid!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.Amount),
                StringComparer.OrdinalIgnoreCase
            );

    internal static decimal ResolveStatisticAmount(
        string? orderGuid,
        decimal detailAmount,
        Dictionary<string, decimal> paymentAmounts,
        Dictionary<string, decimal> detailAmounts)
    {
        var key = SalesStatisticsCodeRules.Normalize(orderGuid);
        if (string.IsNullOrWhiteSpace(key)
            || !paymentAmounts.TryGetValue(key, out var paymentAmount)
            || !detailAmounts.TryGetValue(key, out var detailTotal)
            || detailTotal == 0m)
        {
            // 无支付记录时必须按支付口径计 0，不能回退明细金额掩盖对账异常。
            return 0m;
        }

        return paymentAmount * detailAmount / detailTotal;
    }

    internal static decimal? ResolveUnitCost(
        string branchCode,
        string supplierCode,
        string productCode,
        Dictionary<string, decimal?> storeCostMap,
        Dictionary<string, decimal?> productCostMap,
        Dictionary<string, decimal?> warehouseCostMap,
        out string costSource)
    {
        if (storeCostMap.TryGetValue(
                $"{branchCode}|{supplierCode}|{productCode}",
                out var storeCost)
            && storeCost is > 0)
        {
            costSource = "StoreRetailPrice";
            return storeCost;
        }

        if (productCostMap.TryGetValue(productCode, out var productCost)
            && productCost is > 0)
        {
            costSource = "ProductPurchasePrice";
            return productCost;
        }

        if (warehouseCostMap.TryGetValue(productCode, out var warehouseCost)
            && warehouseCost is > 0)
        {
            costSource = "WarehouseImportPrice";
            return warehouseCost;
        }

        costSource = "Missing";
        return null;
    }

    /// <summary>
    /// 解析普通商品成本。当前分店的正数价格优先；当前分店缺口时，只有其它有效
    /// 分店在相同商品、供应商和计价单位上全部给出同一个正数，才允许可信回退。
    /// </summary>
    internal static decimal? ResolveUnitCost(
        string branchCode,
        string supplierCode,
        string productCode,
        IReadOnlyList<StoreCostRow> storeCosts,
        string? pricingUnit,
        Dictionary<string, decimal?> productCostMap,
        Dictionary<string, decimal?> warehouseCostMap,
        out string costSource)
    {
        var normalizedBranch = Normalize(branchCode);
        var normalizedSupplier = Normalize(supplierCode);
        var normalizedProduct = Normalize(productCode);
        var normalizedUnit = Normalize(pricingUnit);
        var identityCandidates = storeCosts
            .Where(row => string.Equals(Normalize(row.ProductCode), normalizedProduct, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(row.SupplierCode), normalizedSupplier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currentStore = identityCandidates
            .Where(row => string.Equals(Normalize(row.StoreCode), normalizedBranch, StringComparison.OrdinalIgnoreCase)
                && IsPricingUnitMatch(row.PricingUnit, normalizedUnit))
            .ToList();
        if (TryResolveSinglePositivePrice(currentStore, out var currentPrice))
        {
            costSource = "StoreRetailPrice";
            return currentPrice;
        }

        // 其它门店必须整体一致；任一已存在的非正数/冲突值都会使回退失去可信性。
        var otherStores = identityCandidates
            .Where(row => !string.Equals(Normalize(row.StoreCode), normalizedBranch, StringComparison.OrdinalIgnoreCase))
            .Where(row => row.IsActive)
            .ToList();
        var currentPositivePrices = currentStore.Where(row => row.PurchasePrice is > 0)
            .Select(row => row.PurchasePrice!.Value).Distinct().ToList();
        var crossStoreFailure = string.Empty;
        if (otherStores.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(normalizedUnit)
                || otherStores.Any(row => !IsKnownPricingUnit(row)))
                crossStoreFailure = "MissingStoreCostUnit";
            else if (otherStores.Any(row => !string.Equals(
                         Normalize(row.PricingUnit), normalizedUnit, StringComparison.OrdinalIgnoreCase)))
                crossStoreFailure = "StoreRetailPriceUnitConflict";
            else if (otherStores.Any(row => row.PurchasePrice is not > 0)
                || otherStores.Select(row => row.PurchasePrice!.Value).Distinct().Count() > 1)
                crossStoreFailure = "StoreRetailPriceConflict";
        }
        if (productCostMap.TryGetValue(normalizedProduct, out var productCost)
            && productCost is > 0)
        {
            costSource = "ProductPurchasePrice";
            return productCost;
        }

        if (warehouseCostMap.TryGetValue(normalizedProduct, out var warehouseCost)
            && warehouseCost is > 0)
        {
            costSource = "WarehouseImportPrice";
            return warehouseCost;
        }

        // 跨门店一致回退是最后的补洞手段，不能盖过商品主档或仓库已有成本。
        if (string.IsNullOrWhiteSpace(crossStoreFailure)
            && currentPositivePrices.Count == 0
            && otherStores.Count > 0
            && otherStores.All(row => row.PurchasePrice is > 0)
            && otherStores.Select(row => row.PurchasePrice!.Value).Distinct().Count() == 1)
        {
            costSource = "StoreRetailPriceFallback";
            return otherStores[0].PurchasePrice;
        }

        costSource = !string.IsNullOrWhiteSpace(crossStoreFailure)
            ? crossStoreFailure
            : currentPositivePrices.Count > 1
                ? "StoreRetailPriceConflict"
                : "Missing";
        return null;
    }

    private static bool IsPricingUnitMatch(string? candidateUnit, string expectedUnit)
    {
        var candidate = Normalize(candidateUnit);
        // StoreRetailPrice 目前没有单位列，空值代表商品主档的隐含计价单位。
        return string.IsNullOrWhiteSpace(expectedUnit)
            || string.IsNullOrWhiteSpace(candidate)
            || string.Equals(candidate, expectedUnit, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownPricingUnit(StoreCostRow row) =>
        row.PricingUnitKnown || !string.IsNullOrWhiteSpace(row.PricingUnit);

    private static bool TryResolveSinglePositivePrice(
        IReadOnlyList<StoreCostRow> rows,
        out decimal? price)
    {
        var positive = rows.Where(row => row.PurchasePrice is > 0)
            .Select(row => row.PurchasePrice!.Value)
            .Distinct()
            .ToList();
        price = positive.Count == 1 ? positive[0] : null;
        return price.HasValue;
    }

    internal static bool IsExplicitOpenItem(ProductStoreDailySourceRow row) =>
        IsOpenItemCode(row.ProductCode)
        || IsOpenItemCode(row.Barcode)
        || IsOpenItemCode(row.PriceLookupCode);

    private static bool IsOpenItemCode(string? value) =>
        string.Equals(Normalize(value), "OPENITEM", StringComparison.OrdinalIgnoreCase);

    internal static decimal? ResolveOriginalUnitPrice(ProductStoreDailySourceRow row)
    {
        // 客户端把 LookupCode 写入明细 Barcode；这里只接受明确 OPENITEM，绝不按名称猜测。
        if (!row.OriginalSaleCostEvidence)
            return null;
        var direct = row.IsHBSalesSource ? row.HBSalesUnitPrice : row.Price;
        var subtotal = row.IsHBSalesSource ? row.HBSalesOriginalAmount : row.Subtotal;
        // 纯构造快照可能只填公共字段；查询入口仍优先使用来源专属字段。
        direct ??= row.OriginalUnitPrice;
        subtotal ??= row.OriginalSubtotal;
        var originalQuantity = Math.Abs(row.OriginalSaleQuantity ?? 0m);

        if (direct is > 0)
        {
            // 若来源同时带有原价小计和原销售数量，则核验 Price 是未折扣原价；允许
            // 一分钱以内的来源舍入误差。缺少小计时，正数 Price 本身仍是权威原价。
            if (subtotal is > 0 && originalQuantity > 0m
                && Math.Abs(subtotal.Value - direct.Value * originalQuantity) > 0.01m)
                return null;
            return direct;
        }

        if (subtotal is > 0 && originalQuantity > 0m)
            return subtotal.Value / originalQuantity;
        return null;
    }

    internal sealed record ProductStoreDailyCostResolution(
        decimal? UnitCost,
        decimal? TotalCost,
        string CostSource);

    internal static ProductStoreDailyCostResolution ResolveCost(
        IReadOnlyList<ProductStoreDailySourceRow> rows,
        string branchCode,
        string supplierCode,
        string productCode,
        IReadOnlyList<StoreCostRow> storeCosts,
        Dictionary<string, decimal?> productCostMap,
        Dictionary<string, decimal?> warehouseCostMap,
        string? pricingUnit)
    {
        var explicitOpenItemRows = rows.Where(IsExplicitOpenItem).ToList();
        if (explicitOpenItemRows.Count > 0 && explicitOpenItemRows.Count < rows.Count)
        {
            // 同一商品分组中只有部分明细被 OPENITEM 证据确认，不能把其余普通身份
            // 强行套用开放商品规则，也不能回退到普通商品成本。
            return new ProductStoreDailyCostResolution(null, null, "IdentityConflict");
        }

        if (explicitOpenItemRows.Count > 0)
        {
            if (rows.Any(row => row.IsHBSalesSource
                && (row.DocumentType?.Trim() is "3" or "4")
                && !row.OriginalSaleCostEvidence))
            {
                return new ProductStoreDailyCostResolution(null, null, "OpenItemMissingOriginalSale");
            }
            var pricedRows = rows.Select(row =>
            {
                var unitPrice = ResolveOriginalUnitPrice(row);
                var lineCost = unitPrice.HasValue
                    ? unitPrice.Value / 2.5m * row.Quantity
                    : (decimal?)null;
                return (UnitPrice: unitPrice, LineCost: lineCost);
            }).ToList();
            if (rows.Any(HasOriginalPriceConflict))
            {
                return new ProductStoreDailyCostResolution(null, null, "OpenItemPriceConflict");
            }
            var prices = pricedRows.Where(item => item.UnitPrice is > 0)
                .Select(item => item.UnitPrice!.Value / 2.5m)
                .Distinct()
                .ToList();
            // 无价行使总成本也不再可证明；保留 OpenItem 来源，禁止普通成本回退。
            var totalCost = pricedRows.All(item => item.LineCost.HasValue)
                ? pricedRows.Sum(item => item.LineCost!.Value)
                : (decimal?)null;
            return new ProductStoreDailyCostResolution(
                prices.Count == 1 && totalCost.HasValue ? prices[0] : null,
                totalCost,
                totalCost.HasValue ? "OpenItem" : "OpenItemMissingPrice");
        }

        var sourcePricingUnits = rows
            .Select(row => Normalize(row.PricingUnit))
            .Where(unit => !string.IsNullOrWhiteSpace(unit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourcePricingUnits.Count > 1)
        {
            // 同一统计行混入多个来源计价单位时，不能用第一行单位掩盖身份冲突。
            return new ProductStoreDailyCostResolution(null, null, "StoreRetailPriceUnitConflict");
        }

        var unitCost = ResolveUnitCost(
            branchCode,
            supplierCode,
            productCode,
            storeCosts,
            pricingUnit,
            productCostMap,
            warehouseCostMap,
            out var costSource);
        return new ProductStoreDailyCostResolution(
            unitCost,
            // 既有统计实体按整数销量落库；成本必须沿用同一截断口径，不能用原始 decimal
            // 数量造成 TotalCost 与 TotalQuantity 不一致。
            unitCost.HasValue ? unitCost.Value * (int)rows.Sum(row => row.Quantity) : null,
            costSource);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool HasOriginalPriceConflict(ProductStoreDailySourceRow row)
    {
        var direct = row.IsHBSalesSource ? row.HBSalesUnitPrice : row.Price;
        var subtotal = row.IsHBSalesSource ? row.HBSalesOriginalAmount : row.Subtotal;
        direct ??= row.OriginalUnitPrice;
        subtotal ??= row.OriginalSubtotal;
        var originalQuantity = Math.Abs(row.OriginalSaleQuantity ?? 0m);
        return direct is > 0
            && subtotal is > 0
            && originalQuantity > 0m
            && Math.Abs(subtotal.Value - direct.Value * originalQuantity) > 0.01m;
    }

    internal static string SelectDeterministicProductCode(IEnumerable<string> candidates) =>
        candidates
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(code => code, StringComparer.Ordinal)
            .First();

    internal static string? ResolveUniqueCanonicalProductCode(IEnumerable<string> candidates)
    {
        var unique = candidates
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return unique.Count == 1 ? unique[0] : null;
    }

    internal static DateTime? GetLatestSourceTime(params DateTime?[] timestamps)
    {
        var values = timestamps
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    internal static DateTime? GetLatestSourceTime(IEnumerable<ProductStoreDailySourceRow> rows) =>
        GetLatestSourceTime(rows
            .SelectMany(row => new[] { row.OrderLastUploadTime, row.DetailLastUploadTime })
            .ToArray());

    internal static DateTime? GetHBSalesSourceWatermark(
        IEnumerable<ProductStoreDailySourceRow> hbSalesRows)
    {
        // 四列必须分别取 MAX；LastModify 有值时不能遮蔽 Create。
        return GetLatestSourceTime(
            GetLatestSourceTime(hbSalesRows.Select(row => row.HBSalesMainLastModifiedAt).ToArray()),
            GetLatestSourceTime(hbSalesRows.Select(row => row.HBSalesMainCreatedAt).ToArray()),
            GetLatestSourceTime(hbSalesRows.Select(row => row.HBSalesDetailLastModifiedAt).ToArray()),
            GetLatestSourceTime(hbSalesRows.Select(row => row.HBSalesDetailCreatedAt).ToArray())
        );
    }

    internal static DateTime? GetPosmSnapshotWatermark(Posm2025DailySnapshot snapshot)
    {
        var values = snapshot.OrderRows.Select(row => row.LastUploadTime)
            .Concat(snapshot.DetailRows.Select(row =>
                GetLatestSourceTime(row.OrderLastUploadTime, row.DetailLastUploadTime)))
            .Concat(snapshot.PaymentRows.Select(row => row.LastUploadTime))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    internal static HBSales2025DailySnapshotSignature CreateHBSales2025DailySnapshotSignature(
        DateTime date,
        IEnumerable<ProductStoreDailySourceRow> rows)
    {
        var dayRows = rows.Where(row => row.Date.Date == date.Date).ToList();
        var mainLastModifiedAt = dayRows.Max(row => row.HBSalesMainLastModifiedAt);
        var mainCreatedAt = dayRows.Max(row => row.HBSalesMainCreatedAt);
        var detailLastModifiedAt = dayRows.Max(row => row.HBSalesDetailLastModifiedAt);
        var detailCreatedAt = dayRows.Max(row => row.HBSalesDetailCreatedAt);
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in dayRows.OrderBy(row => row.HBSalesOrderNumber, StringComparer.Ordinal)
                     .ThenBy(row => row.DetailGuid, StringComparer.Ordinal)
                     .ThenBy(row => row.BranchCode, StringComparer.Ordinal)
                     .ThenBy(row => row.ProductCode, StringComparer.Ordinal))
        {
            AppendSignatureValue(checksum, row.HBSalesOrderNumber);
            AppendSignatureValue(checksum, row.DetailGuid);
            AppendSignatureValue(checksum, row.Date);
            AppendSignatureValue(checksum, row.BranchCode);
            AppendSignatureValue(checksum, row.ProductCode);
            AppendSignatureValue(checksum, row.ItemNumber);
            AppendSignatureValue(checksum, row.Barcode);
            AppendSignatureValue(checksum, row.HBSalesUnitPrice);
            AppendSignatureValue(checksum, row.HBSalesOriginalAmount);
            AppendSignatureValue(checksum, row.OriginalSaleQuantity);
            AppendSignatureValue(checksum, row.OriginalHBSalesOrderNumber);
            AppendSignatureValue(checksum, row.HBSalesReturnCode);
            AppendSignatureValue(checksum, row.PricingUnit);
            AppendSignatureValue(checksum, row.SupplierCode);
            AppendSignatureValue(checksum, row.ProductName);
            AppendSignatureValue(checksum, row.Quantity);
            AppendSignatureValue(checksum, row.ActualAmount);
            AppendSignatureValue(checksum, row.DocumentType);
            AppendSignatureValue(checksum, row.HBSalesMainLastModifiedAt);
            AppendSignatureValue(checksum, row.HBSalesMainCreatedAt);
            AppendSignatureValue(checksum, row.HBSalesDetailLastModifiedAt);
            AppendSignatureValue(checksum, row.HBSalesDetailCreatedAt);
        }

        return new HBSales2025DailySnapshotSignature(
            date.Date,
            dayRows.Count,
            mainLastModifiedAt,
            mainCreatedAt,
            detailLastModifiedAt,
            detailCreatedAt,
            Convert.ToHexString(checksum.GetHashAndReset())
        );
    }

    internal static void AppendSignatureValue(IncrementalHash checksum, object? value)
    {
        var text = value switch
        {
            null => "<null>",
            DateTime dateTime => dateTime.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            decimal decimalValue => decimalValue.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
        };
        var bytes = Encoding.UTF8.GetBytes(text);
        checksum.AppendData(BitConverter.GetBytes(bytes.Length));
        checksum.AppendData(bytes);
    }

    internal static Posm2025DailySnapshotSignature CreatePosm2025DailySnapshotSignature(
        DateTime date,
        IEnumerable<StoreStatisticOrderRow> orders,
        IEnumerable<ProductStoreDailySourceRow> details,
        IEnumerable<StoreStatisticPaymentRow> payments,
        IEnumerable<ProductStoreDailySourceRow> salesReturns)
    {
        var orderRows = orders.OrderBy(row => row.OrderGuid, StringComparer.Ordinal).ToList();
        var detailRows = details.OrderBy(row => row.DetailGuid, StringComparer.Ordinal).ToList();
        var paymentRows = payments.OrderBy(row => row.PaymentGuid, StringComparer.Ordinal).ToList();
        var returnRows = salesReturns.OrderBy(row => row.DetailGuid, StringComparer.Ordinal).ToList();
        return new Posm2025DailySnapshotSignature(
            date.Date,
            CreatePosmTableSignature(orderRows, row => row.UpdatedAt, row => row.CreatedAt, row =>
                [row.OrderGuid, row.OrderTime, row.BranchCode, row.DeviceCode, row.Status,
                    row.LastUploadTime, row.CreatedAt, row.UpdatedAt]),
            CreatePosmTableSignature(detailRows, row => row.SourceUpdatedAt, row => row.SourceCreatedAt, row =>
                [row.OrderGuid, row.DetailGuid, row.ProductCode, row.SupplierCode, row.ProductName,
                    row.Barcode, row.Price, row.Subtotal, row.OriginalSaleQuantity,
                    row.OriginalSaleCostEvidence,
                    row.Quantity, row.ActualAmount,
                    row.DetailLastUploadTime,
                    row.SourceCreatedAt, row.SourceUpdatedAt]),
            CreatePosmTableSignature(paymentRows, row => row.UpdatedAt, row => row.CreatedAt, row =>
                [row.PaymentGuid, row.OrderGuid, row.Amount, row.LastUploadTime,
                    row.CreatedAt, row.UpdatedAt]),
            CreatePosmTableSignature(returnRows, row => row.SourceUpdatedAt, row => row.SourceCreatedAt, row =>
                [row.OrderGuid, row.DetailGuid, row.ProductCode, row.Quantity, row.ActualAmount,
                    row.Price, row.Subtotal, row.OriginalSaleQuantity,
                    row.OriginalSaleCostEvidence, row.Barcode,
                    row.SourceCreatedAt, row.SourceUpdatedAt])
        );
    }

    internal static Posm2025DailyTableSignature CreatePosmTableSignature<T>(
        IReadOnlyList<T> rows,
        Func<T, DateTime?> lastModifiedSelector,
        Func<T, DateTime?> createdSelector,
        Func<T, object?[]> valuesSelector)
    {
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            foreach (var value in valuesSelector(row))
                AppendSignatureValue(checksum, value);
        }

        return new Posm2025DailyTableSignature(
            rows.Count,
            rows.Max(lastModifiedSelector),
            rows.Max(createdSelector),
            Convert.ToHexString(checksum.GetHashAndReset())
        );
    }

    internal static List<HBSalesStoreAggregateRow> BuildHBSalesStoreAggregates(
        IReadOnlyList<ProductStoreDailySourceRow> hbSalesRows)
    {
        return hbSalesRows
            .Where(row => row.IsHBSalesSource)
            .Select(row => new
            {
                Row = row,
                BranchCode = SalesStatisticsCodeRules.Normalize(row.BranchCode),
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
            .GroupBy(row => row.BranchCode)
            .Select(group => new HBSalesStoreAggregateRow
            {
                BranchCode = group.Key,
                TotalAmount = group.Sum(row => row.Row.ActualAmount),
                TotalQuantity = group.Sum(row => row.Row.Quantity),
                OrderCount = group
                    .Select(row => row.Row.HBSalesOrderNumber)
                    .Where(orderNumber => orderNumber != null)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            })
            .ToList();
    }
}
