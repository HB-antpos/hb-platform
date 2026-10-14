using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed record WarehouseProductSingleCreationPlan(string UpdatedBy)
{
    internal static WarehouseProductSingleCreationPlan Create(string? updatedBy) =>
        new(ProductWarehouseSliceBase.ResolveUpdatedBy(updatedBy));
}

internal static class WarehouseProductSingleCreationValidator
{
    internal static string? ValidateIdentityPrerequisites(CreateSingleProductRequestDto request)
    {
        if (
            string.IsNullOrWhiteSpace(request.ItemNumber)
            && string.IsNullOrWhiteSpace(request.SupplierCode)
            && !request.SupplierId.HasValue
        )
        {
            return "货号为空时需提供供应商编码以自动生成";
        }
        return null;
    }

    internal static string? ValidatePricing(CreateSingleProductRequestDto request) =>
        request.OEMPrice <= 0
            ? "零售价必须大于0"
            : request.ImportPrice <= 0
                ? "进口价格必须大于0"
                : null;

    /// <summary>
    /// 装箱数、中包数量传了就必须 ≥ 1，与国内商品编辑口径一致；0 或负数没有业务含义，直接拒绝而不是静默忽略。
    /// </summary>
    internal static string? ValidatePackaging(CreateSingleProductRequestDto request) =>
        request.PackingQuantity is < 1
            ? "装箱数必须大于0"
            : request.MiddlePackQuantity is < 1
                ? "中包数量必须大于0"
                : null;

    /// <summary>
    /// 解析分店价格行的分店代码：优先 StoreCode，其次兼容旧的整数 StoreId。
    /// 两者都缺失时返回 null；StoreId 缺省值 0 不是有效分店，不能当作分店代码。
    /// </summary>
    internal static string? ResolveStoreCode(StorePriceDto storePrice)
    {
        var storeCode = storePrice.StoreCode?.Trim();
        if (!string.IsNullOrEmpty(storeCode))
        {
            return storeCode;
        }
        return storePrice.StoreId > 0 ? storePrice.StoreId.ToString() : null;
    }

    /// <summary>
    /// 校验请求里的分店价格：每行必须有有效分店代码且不能重复。
    /// 此前字段名不一致（前端 storeCode、后端 StoreId）会让所有行静默变成分店 0，
    /// 所以这里在写库前就拒绝，避免生成无法归属任何分店的价格记录。
    /// </summary>
    internal static string? ValidateStorePrices(CreateSingleProductRequestDto request)
    {
        if (request.StorePrices is not { Count: > 0 })
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var storePrice in request.StorePrices)
        {
            var storeCode = ResolveStoreCode(storePrice);
            // "0" 是旧缺陷写出的占位值，即使显式传入也视为无效。
            if (string.IsNullOrEmpty(storeCode) || storeCode == "0")
            {
                return "分店价格缺少有效的分店代码";
            }
            if (!seen.Add(storeCode))
            {
                return $"分店价格中分店 {storeCode} 重复";
            }
        }
        return null;
    }
}
