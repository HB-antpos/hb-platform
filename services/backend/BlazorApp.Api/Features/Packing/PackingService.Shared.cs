using BlazorApp.Api.Utils;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

internal static class PackingConfig
{
    private const string DefaultWarehouseName = "HB WAREHOUSE";

    /// <summary>总仓在标签 FROM / TO 上显示的名称，取配置 Packing:WarehouseName。</summary>
    public static string WarehouseName(IConfiguration configuration)
    {
        var value = configuration["Packing:WarehouseName"]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return DefaultWarehouseName;
        }

        return value.Length <= 100 ? value : value[..100];
    }
}

internal sealed partial class PackingService
{
    // ---- 所属地点、权限判断 ----

    /// <summary>
    /// 请求里的所属地点必须是账号能操作的地点。没传时，账号只有一个地点就用它，
    /// 有多个（店长管多家店、管理员）则必须显式指定，避免默认操作了别的店。
    /// </summary>
    private static OwnerChoice RequireOwner(PackingOperator op, string? ownerCode)
    {
        var requested = ownerCode?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            return op.Owners.Count == 1
                ? OwnerChoice.Of(op.Owners[0].Code)
                : OwnerChoice.Fail(400, PackingErrorCodes.OwnerRequired, "请先选择分店或总仓");
        }

        var match = op.Owners.FirstOrDefault(owner => string.Equals(owner.Code, requested, StringComparison.OrdinalIgnoreCase));
        return match == null
            ? OwnerChoice.Fail(403, PackingErrorCodes.OwnerNotAllowed, "你不能操作这个分店")
            : OwnerChoice.Of(match.Code);
    }

    /// <summary>
    /// 能不能看到这只箱：它属于你的地点、是你的地点发出的、或是发给你的地点的。
    /// 发给你的箱必须看得到，否则收货方扫不开在路上的箱，没法签收。
    /// </summary>
    private async Task<bool> CanViewCartonAsync(PackingOperator op, PackCarton carton)
    {
        if (op.Has(carton.OwnerCode) || op.Has(carton.OriginCode) || op.Has(carton.DestinationCode))
        {
            return true;
        }

        if (carton.PalletGUID == null)
        {
            return false;
        }

        // 上了托盘、托盘还没发出的箱：去向跟随托盘。
        var pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == carton.PalletGUID).FirstAsync();
        return pallet != null && op.Has(pallet.DestinationCode);
    }

    private static bool CanViewPallet(PackingOperator op, PackPallet pallet) =>
        op.Has(pallet.OwnerCode) || op.Has(pallet.OriginCode) || op.Has(pallet.DestinationCode);

    /// <summary>
    /// 改箱前的统一校验：箱必须存在、属于你的地点、没有作废、没有发出。
    /// 不属于你地点的箱按「找不到」处理，不暴露它存在。
    /// </summary>
    private static PackingResult<PackingCartonDetailDto>? CheckEditable(
        PackingOperator op,
        PackCarton? carton,
        bool requirePacking,
        string? notPackingMessage
    )
    {
        if (carton == null || carton.Status == PackCartonStatuses.Voided || !op.Has(carton.OwnerCode))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        if (carton.Status == PackCartonStatuses.InTransit)
        {
            return CartonFail(409, PackingErrorCodes.CartonInTransit, "这只箱已经发出，不能再修改");
        }

        if (requirePacking && carton.Status != PackCartonStatuses.Packing)
        {
            return CartonFail(409, PackingErrorCodes.CartonNotPacking, notPackingMessage ?? "该箱已封箱");
        }

        return null;
    }

    private static PackingResult<PackingPalletDetailDto>? CheckPalletEditable(
        PackingOperator op,
        PackPallet? pallet,
        bool requireBuilding,
        string? notBuildingMessage
    )
    {
        if (pallet == null || pallet.Status == PackPalletStatuses.Voided || !op.Has(pallet.OwnerCode))
        {
            return PalletFail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘");
        }

        if (pallet.Status == PackPalletStatuses.InTransit)
        {
            return PalletFail(409, PackingErrorCodes.PalletInTransit, "这个托盘已经发出，不能再修改");
        }

        if (requireBuilding && pallet.Status != PackPalletStatuses.Building)
        {
            return PalletFail(409, PackingErrorCodes.PalletNotBuilding, notBuildingMessage ?? "该托盘已封托");
        }

        return null;
    }

    private static PackingResult<PackingCartonDetailDto> CartonFail(int statusCode, string errorCode, string message) =>
        PackingResult<PackingCartonDetailDto>.Fail(statusCode, errorCode, message);

    private static PackingResult<PackingPalletDetailDto> PalletFail(int statusCode, string errorCode, string message) =>
        PackingResult<PackingPalletDetailDto>.Fail(statusCode, errorCode, message);

    // ---- 商品识别 ----

    /// <summary>
    /// 按码找商品：条码 / 货号 / 商品编码精确匹配；无结果时试前导 0 的另一种写法，再试套装码，最后试本店多码。
    /// 套装码、多码只用来认出母商品，数量仍按每扫一次 1 件计：套装码表混有组合套装的子件、多码和变量套装，
    /// 「套装数量」含义不统一，按它自动加倍会把箱内数量算错。
    /// </summary>
    private async Task<List<ProductMatch>> FindProductsByCodeAsync(string code, string ownerCode)
    {
        var matches = await FindProductsByExactCodeAsync(code);
        if (matches.Count > 0)
        {
            return matches;
        }

        // iOS 相机会把 0 开头的 EAN-13 去成 12 位：精确无结果才回退前导 0 的另一种写法。
        var variant = BarcodeVariants.GetLeadingZeroVariant(code);
        if (variant != null)
        {
            matches = await FindProductsByExactCodeAsync(variant);
            if (matches.Count > 0)
            {
                return matches;
            }
        }

        var setParent = await _db.Queryable<ProductSetCode>()
            .Where(item =>
                !item.IsDeleted
                && item.IsActive
                && (item.SetBarcode == code || item.SetItemNumber == code || item.SetProductCode == code)
            )
            .Select(item => item.ProductCode)
            .FirstAsync();
        if (!string.IsNullOrWhiteSpace(setParent))
        {
            return await LoadProductsAsync(new List<string> { setParent });
        }

        if (PackOwners.IsWarehouse(ownerCode))
        {
            return new List<ProductMatch>();
        }

        // 分店的多码是各店自己设的：只认本店的。
        var multiParent = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item =>
                !item.IsDeleted
                && item.StoreCode == ownerCode
                && item.ProductCode != null
                && (item.MultiBarcode == code || item.MultiCodeProductCode == code || item.StoreMultiCodeProductCode == code)
            )
            .Select(item => item.ProductCode)
            .FirstAsync();
        return string.IsNullOrWhiteSpace(multiParent)
            ? new List<ProductMatch>()
            : await LoadProductsAsync(new List<string> { multiParent });
    }

    private async Task<List<ProductMatch>> FindProductsByExactCodeAsync(string code)
    {
        return await _db.Queryable<Product>()
            .Where(item => !item.IsDeleted && (item.Barcode == code || item.ItemNumber == code || item.ProductCode == code))
            .Take(10)
            .Select(item => new ProductMatch
            {
                ProductCode = item.ProductCode ?? string.Empty,
                ItemNumber = item.ItemNumber,
                ProductName = item.ProductName,
                Barcode = item.Barcode,
                ProductImage = item.ProductImage,
                MiddlePackageQuantity = item.MiddlePackageQuantity,
            })
            .ToListAsync();
    }

    private async Task<List<ProductMatch>> LoadProductsAsync(List<string> productCodes)
    {
        if (productCodes.Count == 0)
        {
            return new List<ProductMatch>();
        }

        return await _db.Queryable<Product>()
            .Where(item => item.ProductCode != null && productCodes.Contains(item.ProductCode))
            .Select(item => new ProductMatch
            {
                ProductCode = item.ProductCode ?? string.Empty,
                ItemNumber = item.ItemNumber,
                ProductName = item.ProductName,
                Barcode = item.Barcode,
                ProductImage = item.ProductImage,
                MiddlePackageQuantity = item.MiddlePackageQuantity,
            })
            .ToListAsync();
    }

    // ---- 去向、位置、订货单 ----

    /// <summary>
    /// 校验去向：其他分店必须是启用的分店且不是自己；总仓不能发给总仓自己；
    /// 「怎么送」（运输公司）只对要发出的去向有意义，本地存放 / 未指定时清空。
    /// </summary>
    private async Task<DestinationResolution> ResolveDestinationAsync(string ownerCode, PackingDestinationRequestDto request)
    {
        if (!PackDestinationTypes.IsKnown(request.DestinationType))
        {
            return DestinationResolution.Invalid("去向类型无效");
        }

        if (!PackDestinationTypes.IsShippable(request.DestinationType))
        {
            return new DestinationResolution(null, request.DestinationType, null, null);
        }

        string destinationCode;
        if (request.DestinationType == PackDestinationTypes.Warehouse)
        {
            if (PackOwners.IsWarehouse(ownerCode))
            {
                return DestinationResolution.Invalid("总仓不能发给总仓");
            }

            destinationCode = PackOwners.Warehouse;
        }
        else
        {
            var requested = request.DestinationCode?.Trim();
            if (string.IsNullOrEmpty(requested))
            {
                return DestinationResolution.Invalid("请选择收货分店");
            }

            if (SameCode(requested, ownerCode))
            {
                return DestinationResolution.Invalid("不能发给自己，本店存放请选「本店存放」");
            }

            var storeCode = await _db.Queryable<Store>()
                .Where(store => !store.IsDeleted && store.IsActive && store.StoreCode == requested)
                .Select(store => store.StoreCode)
                .FirstAsync();
            if (string.IsNullOrEmpty(storeCode))
            {
                return DestinationResolution.Invalid("找不到收货分店");
            }

            destinationCode = storeCode;
        }

        var carrierGuid = request.CarrierGuid?.Trim();
        if (!string.IsNullOrEmpty(carrierGuid))
        {
            var carrierExists = await _db.Queryable<PackCarrier>().Where(carrier => carrier.CarrierGUID == carrierGuid).AnyAsync();
            if (!carrierExists)
            {
                return DestinationResolution.Invalid("找不到该运输公司");
            }
        }
        else
        {
            carrierGuid = null;
        }

        return new DestinationResolution(null, request.DestinationType, destinationCode, carrierGuid);
    }

    /// <summary>
    /// 存放位置：分店取本店自己维护的位置（名称或位置条码都认），总仓取现有货位（编码或货位条码）。
    /// 传空表示清除。返回的是位置名称，箱 / 托盘上记名称，位置改名后历史记录仍显示当时的名称。
    /// </summary>
    private async Task<LocationResolution> ResolveLocationAsync(string ownerCode, string? locationCode)
    {
        var code = locationCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return new LocationResolution(null, null);
        }

        string? resolved;
        if (PackOwners.IsWarehouse(ownerCode))
        {
            resolved = await _db.Queryable<Location>()
                .Where(location => !location.IsDeleted && (location.LocationCode == code || location.LocationBarcode == code))
                .Select(location => location.LocationCode)
                .FirstAsync();
        }
        else
        {
            resolved = await _db.Queryable<PackLocation>()
                .Where(location => location.OwnerCode == ownerCode && location.IsActive && (location.Name == code || location.Barcode == code))
                .Select(location => location.Name)
                .FirstAsync();
        }

        return string.IsNullOrEmpty(resolved)
            ? new LocationResolution("找不到该存放位置", null)
            : new LocationResolution(null, resolved);
    }

    private async Task<WareHouseOrder?> LoadOrderAsync(string orderGuid)
    {
        return await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && order.OrderGUID == orderGuid)
            .FirstAsync();
    }

    // ---- 组装返回 ----

    private async Task<List<PackingCartonSummaryDto>> BuildCartonSummariesAsync(List<PackCarton> cartons)
    {
        if (cartons.Count == 0)
        {
            return new List<PackingCartonSummaryDto>();
        }

        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var itemTotals = (await _db.Queryable<PackCartonItem>()
                .Where(item => cartonGuids.Contains(item.CartonGUID))
                .Select(item => new { item.CartonGUID, item.Quantity })
                .ToListAsync())
            .GroupBy(item => item.CartonGUID)
            .ToDictionary(group => group.Key, group => (Skus: group.Count(), Pieces: group.Sum(item => item.Quantity)));

        var palletGuids = cartons.Where(carton => carton.PalletGUID != null).Select(carton => carton.PalletGUID!).Distinct().ToList();
        var pallets = palletGuids.Count == 0
            ? new Dictionary<string, PackPallet>()
            : (await _db.Queryable<PackPallet>().Where(pallet => palletGuids.Contains(pallet.PalletGUID)).ToListAsync())
                .ToDictionary(pallet => pallet.PalletGUID);

        var orderGuids = cartons.Where(carton => carton.OrderGUID != null).Select(carton => carton.OrderGUID!).Distinct().ToList();
        var orderNos = orderGuids.Count == 0
            ? new Dictionary<string, string?>()
            : (await _db.Queryable<WareHouseOrder>()
                    .Where(order => orderGuids.Contains(order.OrderGUID))
                    .Select(order => new { order.OrderGUID, order.OrderNo })
                    .ToListAsync())
                .ToDictionary(order => order.OrderGUID, order => order.OrderNo);

        var destinations = await BuildDestinationsAsync(
            cartons.Select(carton =>
            {
                // 上了托盘、托盘还没发出的箱去向跟随托盘；托盘发出时会把去向写进每只箱，之后以箱自己的为准。
                var pallet = carton.PalletGUID == null ? null : PalletOf(pallets, carton.PalletGUID);
                return pallet is { Status: PackPalletStatuses.Building or PackPalletStatuses.Sealed }
                    ? (carton.CartonGUID, pallet.DestinationType, pallet.DestinationCode, pallet.CarrierGUID)
                    : (carton.CartonGUID, carton.DestinationType, carton.DestinationCode, carton.CarrierGUID);
            }).ToList()
        );
        var ownerNames = await LoadOwnerNamesAsync(cartons.Select(carton => carton.OwnerCode));

        return cartons.Select(carton =>
        {
            var totals = itemTotals.GetValueOrDefault(carton.CartonGUID);
            return new PackingCartonSummaryDto
            {
                CartonGuid = carton.CartonGUID,
                CartonNo = carton.CartonNo,
                Status = carton.Status,
                OwnerCode = carton.OwnerCode,
                OwnerName = ownerNames.GetValueOrDefault(carton.OwnerCode) ?? carton.OwnerCode,
                OriginCode = carton.OriginCode,
                OriginName = carton.OriginName,
                Destination = destinations[carton.CartonGUID],
                SkuCount = totals.Skus,
                PieceCount = totals.Pieces,
                PalletGuid = carton.PalletGUID,
                PalletNo = carton.PalletGUID == null ? null : PalletOf(pallets, carton.PalletGUID)?.PalletNo,
                LocationCode = carton.LocationCode,
                WeightKg = carton.WeightKg,
                OrderGuid = carton.OrderGUID,
                OrderNo = carton.OrderGUID == null ? null : orderNos.GetValueOrDefault(carton.OrderGUID),
                CreatedByName = carton.CreatedByName,
                CreatedAtUtc = carton.CreatedAtUtc,
                SealedByName = carton.SealedByName,
                SealedAtUtc = carton.SealedAtUtc,
                DispatchedByName = carton.DispatchedByName,
                DispatchedAtUtc = carton.DispatchedAtUtc,
                ReceivedByName = carton.ReceivedByName,
                ReceivedAtUtc = carton.ReceivedAtUtc,
                UpdatedAtUtc = carton.UpdatedAtUtc,
            };
        }).ToList();
    }

    private static PackPallet? PalletOf(Dictionary<string, PackPallet> pallets, string guid) => pallets.GetValueOrDefault(guid);

    private async Task<PackingCartonDetailDto> BuildCartonDetailAsync(PackCarton carton, string? changedProductCode)
    {
        var summary = (await BuildCartonSummariesAsync(new List<PackCarton> { carton }))[0];
        var items = await _db.Queryable<PackCartonItem>().Where(item => item.CartonGUID == carton.CartonGUID).ToListAsync();
        var productCodes = items.Select(item => item.ProductCode).ToList();
        var products = (await LoadProductsAsync(productCodes)).ToDictionary(product => product.ProductCode);
        var packingQuantities = productCodes.Count == 0
            ? new Dictionary<string, int?>()
            : (await _db.Queryable<WarehouseProduct>()
                    .Where(item => productCodes.Contains(item.ProductCode))
                    .Select(item => new { item.ProductCode, item.PackingQuantity })
                    .ToListAsync())
                .ToDictionary(item => item.ProductCode, item => item.PackingQuantity);

        // 关联订货单时对照配货数量：配货数为空（还没配货）时退回订货数；退货行（负数）不参与。
        var orderQuantities = new Dictionary<string, decimal>();
        var packedElsewhere = new Dictionary<string, int>();
        if (carton.OrderGUID != null && productCodes.Count > 0)
        {
            var orderGuid = carton.OrderGUID;
            var details = await _db.Queryable<WareHouseOrderDetails>()
                .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted && detail.ProductCode != null)
                .Select(detail => new { detail.ProductCode, detail.Quantity, detail.AllocQuantity })
                .ToListAsync();
            orderQuantities = details
                .Where(detail => (detail.AllocQuantity ?? detail.Quantity ?? 0) > 0)
                .GroupBy(detail => detail.ProductCode!)
                .ToDictionary(group => group.Key, group => group.Sum(detail => detail.AllocQuantity ?? detail.Quantity ?? 0));

            var siblingGuids = await _db.Queryable<PackCarton>()
                .Where(item => item.OrderGUID == orderGuid && item.CartonGUID != carton.CartonGUID && item.Status != PackCartonStatuses.Voided)
                .Select(item => item.CartonGUID)
                .ToListAsync();
            if (siblingGuids.Count > 0)
            {
                packedElsewhere = (await _db.Queryable<PackCartonItem>()
                        .Where(item => siblingGuids.Contains(item.CartonGUID) && productCodes.Contains(item.ProductCode))
                        .Select(item => new { item.ProductCode, item.Quantity })
                        .ToListAsync())
                    .GroupBy(item => item.ProductCode)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
            }
        }

        return new PackingCartonDetailDto
        {
            CartonGuid = summary.CartonGuid,
            CartonNo = summary.CartonNo,
            Status = summary.Status,
            OwnerCode = summary.OwnerCode,
            OwnerName = summary.OwnerName,
            OriginCode = summary.OriginCode,
            OriginName = summary.OriginName,
            Destination = summary.Destination,
            SkuCount = summary.SkuCount,
            PieceCount = summary.PieceCount,
            PalletGuid = summary.PalletGuid,
            PalletNo = summary.PalletNo,
            LocationCode = summary.LocationCode,
            WeightKg = summary.WeightKg,
            OrderGuid = summary.OrderGuid,
            OrderNo = summary.OrderNo,
            CreatedByName = summary.CreatedByName,
            CreatedAtUtc = summary.CreatedAtUtc,
            SealedByName = summary.SealedByName,
            SealedAtUtc = summary.SealedAtUtc,
            DispatchedByName = summary.DispatchedByName,
            DispatchedAtUtc = summary.DispatchedAtUtc,
            ReceivedByName = summary.ReceivedByName,
            ReceivedAtUtc = summary.ReceivedAtUtc,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            Remark = carton.Remark,
            ChangedProductCode = changedProductCode,
            Items = items
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.ProductCode, StringComparer.Ordinal)
                .Select(item =>
                {
                    var product = products.GetValueOrDefault(item.ProductCode);
                    return new PackingCartonItemDto
                    {
                        ProductCode = item.ProductCode,
                        ItemNumber = product?.ItemNumber,
                        ProductName = product?.ProductName,
                        Barcode = product?.Barcode,
                        ProductImage = product?.ProductImage,
                        Quantity = item.Quantity,
                        MiddlePackageQuantity = product?.MiddlePackageQuantity,
                        PackingQuantity = packingQuantities.GetValueOrDefault(item.ProductCode),
                        OrderQuantity = orderQuantities.TryGetValue(item.ProductCode, out var orderQuantity) ? orderQuantity : null,
                        PackedInOtherCartons = packedElsewhere.GetValueOrDefault(item.ProductCode),
                        UpdatedAtUtc = item.UpdatedAtUtc,
                    };
                })
                .ToList(),
        };
    }

    private async Task<List<PackingPalletSummaryDto>> BuildPalletSummariesAsync(List<PackPallet> pallets)
    {
        if (pallets.Count == 0)
        {
            return new List<PackingPalletSummaryDto>();
        }

        var palletGuids = pallets.Select(pallet => pallet.PalletGUID).ToList();
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.PalletGUID != null && palletGuids.Contains(carton.PalletGUID))
            .Select(carton => new { carton.CartonGUID, carton.PalletGUID, carton.Status })
            .ToListAsync();
        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var items = cartonGuids.Count == 0
            ? new List<PackCartonItem>()
            : await _db.Queryable<PackCartonItem>().Where(item => cartonGuids.Contains(item.CartonGUID)).ToListAsync();
        var cartonPallet = cartons.ToDictionary(carton => carton.CartonGUID, carton => carton.PalletGUID!);
        var destinations = await BuildDestinationsAsync(
            pallets.Select(pallet => (pallet.PalletGUID, pallet.DestinationType, pallet.DestinationCode, pallet.CarrierGUID)).ToList()
        );
        var ownerNames = await LoadOwnerNamesAsync(pallets.Select(pallet => pallet.OwnerCode));

        return pallets.Select(pallet =>
        {
            var palletItems = items.Where(item => cartonPallet[item.CartonGUID] == pallet.PalletGUID).ToList();
            var own = cartons.Where(carton => carton.PalletGUID == pallet.PalletGUID).ToList();
            return new PackingPalletSummaryDto
            {
                PalletGuid = pallet.PalletGUID,
                PalletNo = pallet.PalletNo,
                Status = pallet.Status,
                OwnerCode = pallet.OwnerCode,
                OwnerName = ownerNames.GetValueOrDefault(pallet.OwnerCode) ?? pallet.OwnerCode,
                OriginCode = pallet.OriginCode,
                OriginName = pallet.OriginName,
                Destination = destinations[pallet.PalletGUID],
                ConsignmentNo = pallet.ConsignmentNo,
                LocationCode = pallet.LocationCode,
                WeightKg = pallet.WeightKg,
                CartonCount = own.Count,
                InTransitCartonCount = own.Count(carton => carton.Status == PackCartonStatuses.InTransit),
                // 品种数按托盘去重：同一货号分装在几只箱里只算一个品种。
                SkuCount = palletItems.Select(item => item.ProductCode).Distinct().Count(),
                PieceCount = palletItems.Sum(item => item.Quantity),
                CreatedByName = pallet.CreatedByName,
                CreatedAtUtc = pallet.CreatedAtUtc,
                SealedByName = pallet.SealedByName,
                SealedAtUtc = pallet.SealedAtUtc,
                DispatchedByName = pallet.DispatchedByName,
                DispatchedAtUtc = pallet.DispatchedAtUtc,
                ReceivedByName = pallet.ReceivedByName,
                ReceivedAtUtc = pallet.ReceivedAtUtc,
                UpdatedAtUtc = pallet.UpdatedAtUtc,
            };
        }).ToList();
    }

    private async Task<PackingPalletDetailDto> BuildPalletDetailAsync(PackPallet pallet)
    {
        var summary = (await BuildPalletSummariesAsync(new List<PackPallet> { pallet }))[0];
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.PalletGUID == pallet.PalletGUID)
            .OrderBy(carton => carton.UpdatedAtUtc, OrderByType.Desc)
            .ToListAsync();
        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var items = cartonGuids.Count == 0
            ? new List<PackCartonItem>()
            : await _db.Queryable<PackCartonItem>().Where(item => cartonGuids.Contains(item.CartonGUID)).ToListAsync();
        var products = (await LoadProductsAsync(items.Select(item => item.ProductCode).Distinct().ToList()))
            .ToDictionary(product => product.ProductCode);

        return new PackingPalletDetailDto
        {
            PalletGuid = summary.PalletGuid,
            PalletNo = summary.PalletNo,
            Status = summary.Status,
            OwnerCode = summary.OwnerCode,
            OwnerName = summary.OwnerName,
            OriginCode = summary.OriginCode,
            OriginName = summary.OriginName,
            Destination = summary.Destination,
            ConsignmentNo = summary.ConsignmentNo,
            LocationCode = summary.LocationCode,
            WeightKg = summary.WeightKg,
            CartonCount = summary.CartonCount,
            InTransitCartonCount = summary.InTransitCartonCount,
            SkuCount = summary.SkuCount,
            PieceCount = summary.PieceCount,
            CreatedByName = summary.CreatedByName,
            CreatedAtUtc = summary.CreatedAtUtc,
            SealedByName = summary.SealedByName,
            SealedAtUtc = summary.SealedAtUtc,
            DispatchedByName = summary.DispatchedByName,
            DispatchedAtUtc = summary.DispatchedAtUtc,
            ReceivedByName = summary.ReceivedByName,
            ReceivedAtUtc = summary.ReceivedAtUtc,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            Remark = pallet.Remark,
            Cartons = await BuildCartonSummariesAsync(cartons),
            Products = items
                .GroupBy(item => item.ProductCode)
                .Select(group =>
                {
                    var product = products.GetValueOrDefault(group.Key);
                    return new PackingPalletProductDto
                    {
                        ProductCode = group.Key,
                        ItemNumber = product?.ItemNumber,
                        ProductName = product?.ProductName,
                        Barcode = product?.Barcode,
                        Quantity = group.Sum(item => item.Quantity),
                        CartonCount = group.Count(),
                    };
                })
                .OrderBy(product => product.ItemNumber ?? product.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private async Task<Dictionary<string, PackingDestinationDto>> BuildDestinationsAsync(
        List<(string Key, int Type, string? Code, string? CarrierGuid)> sources
    )
    {
        var names = await LoadOwnerNamesAsync(sources.Select(source => source.Code));
        var carrierNames = await LoadCarrierNamesAsync(sources.Select(source => source.CarrierGuid));
        return sources.ToDictionary(
            source => source.Key,
            source => new PackingDestinationDto
            {
                DestinationType = source.Type,
                Code = source.Code,
                Name = source.Code == null ? null : names.GetValueOrDefault(source.Code) ?? source.Code,
                CarrierGuid = source.CarrierGuid,
                CarrierName = source.CarrierGuid == null ? null : carrierNames.GetValueOrDefault(source.CarrierGuid),
            }
        );
    }

    private async Task<Dictionary<string, string>> LoadCarrierNamesAsync(IEnumerable<string?> carrierGuids)
    {
        var guids = carrierGuids.Where(guid => !string.IsNullOrEmpty(guid)).Select(guid => guid!).Distinct().ToList();
        if (guids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        return (await _db.Queryable<PackCarrier>()
                .Where(carrier => guids.Contains(carrier.CarrierGUID))
                .Select(carrier => new { carrier.CarrierGUID, carrier.Name })
                .ToListAsync())
            .ToDictionary(carrier => carrier.CarrierGUID, carrier => carrier.Name);
    }

    /// <summary>分店编码 → 分店名；总仓编码 → 配置的总仓名称。</summary>
    private async Task<Dictionary<string, string>> LoadOwnerNamesAsync(IEnumerable<string?> ownerCodes)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var codes = ownerCodes.Where(code => !string.IsNullOrEmpty(code)).Select(code => code!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
        {
            return names;
        }

        if (codes.Any(PackOwners.IsWarehouse))
        {
            names[PackOwners.Warehouse] = PackingConfig.WarehouseName(configuration);
        }

        var storeCodes = codes.Where(code => !PackOwners.IsWarehouse(code)).ToList();
        if (storeCodes.Count > 0)
        {
            var stores = await _db.Queryable<Store>()
                .Where(store => storeCodes.Contains(store.StoreCode))
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync();
            foreach (var store in stores)
            {
                names[store.StoreCode] = store.StoreName;
            }
        }

        return names;
    }

    private async Task<string> OwnerNameAsync(string ownerCode)
    {
        var names = await LoadOwnerNamesAsync(new[] { ownerCode });
        return Truncate(names.GetValueOrDefault(ownerCode) ?? ownerCode, 100) ?? ownerCode;
    }

    private async Task<Dictionary<string, string?>> LoadPalletLocationsAsync(List<PackCarton> cartons)
    {
        var palletGuids = cartons.Where(carton => carton.PalletGUID != null).Select(carton => carton.PalletGUID!).Distinct().ToList();
        if (palletGuids.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        return (await _db.Queryable<PackPallet>()
                .Where(pallet => palletGuids.Contains(pallet.PalletGUID))
                .Select(pallet => new { pallet.PalletGUID, pallet.LocationCode })
                .ToListAsync())
            .ToDictionary(pallet => pallet.PalletGUID, pallet => pallet.LocationCode);
    }

    // ---- 加锁、发号、事务 ----

    private async Task<PackCarton?> LockCartonAsync(string cartonGuid)
    {
        var query = _db.Queryable<PackCarton>().Where(carton => carton.CartonGUID == cartonGuid);
        // SQL Server：更新锁持有到事务结束，同一只箱的写入在此串行；SQLite 测试库整库串行，不需要提示。
        return await (IsSqlServer ? query.With(SqlWith.UpdLock) : query).FirstAsync();
    }

    private async Task<PackPallet?> LockPalletAsync(string palletGuid)
    {
        var query = _db.Queryable<PackPallet>().Where(pallet => pallet.PalletGUID == palletGuid);
        return await (IsSqlServer ? query.With(SqlWith.UpdLock) : query).FirstAsync();
    }

    /// <summary>
    /// 发号：前缀 + 所属地点当地年月日 + 当日流水（位置标签不带日期，一直累加）。必须在事务里调用——
    /// 行不存在时 UPDLOCK+HOLDLOCK 锁住键范围，两台设备同时新建当天第一只箱也不会重号。
    /// </summary>
    private async Task<string> NextNumberAsync(string prefix, int digits, string ownerCode, bool includeDate = true)
    {
        var key = includeDate ? prefix + (await LocalNowAsync(ownerCode)).ToString("yyMMdd") : prefix;
        var query = _db.Queryable<PackSequence>().Where(sequence => sequence.SequenceKey == key);
        var row = await (IsSqlServer ? query.With("WITH(UPDLOCK,HOLDLOCK)") : query).FirstAsync();
        int next;
        if (row == null)
        {
            next = 1;
            await _db.Insertable(new PackSequence { SequenceKey = key, LastValue = next }).ExecuteCommandAsync();
        }
        else
        {
            next = row.LastValue + 1;
            row.LastValue = next;
            await _db.Updateable(row).ExecuteCommandAsync();
        }

        return key + next.ToString("D" + digits);
    }

    private async Task AddEventAsync(int targetType, string targetGuid, int action, string? detail, PackingOperator op, DateTime now)
    {
        await _db.Insertable(new PackEvent
        {
            EventGUID = NewGuid(),
            TargetType = targetType,
            TargetGUID = targetGuid,
            Action = action,
            Detail = Truncate(detail, 400),
            OperatorUserGuid = op.UserGuid,
            OperatorName = op.Name,
            CreatedAtUtc = now,
        }).ExecuteCommandAsync();
    }

    private async Task<PackingResult<T>> ExecuteInTransactionAsync<T>(Func<Task<PackingResult<T>>> action)
    {
        await _db.Ado.BeginTranAsync();
        try
        {
            var result = await action();
            if (result.Success)
            {
                await _db.Ado.CommitTranAsync();
            }
            else
            {
                await _db.Ado.RollbackTranAsync();
            }

            return result;
        }
        catch (Exception exception)
        {
            await _db.Ado.RollbackTranAsync();
            logger.LogError(exception, "打包事务失败");
            throw;
        }
    }

    private bool IsSqlServer => _db.CurrentConnectionConfig.DbType == DbType.SqlServer;

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    /// <summary>编号里的日期按所属地点的当地时间（分店取自己的时区，总仓取配置），避免早班建的箱编到前一天。</summary>
    private async Task<DateTime> LocalNowAsync(string ownerCode)
    {
        string? zoneId = null;
        if (!PackOwners.IsWarehouse(ownerCode))
        {
            zoneId = await _db.Queryable<Store>()
                .Where(store => store.StoreCode == ownerCode)
                .Select(store => store.TimeZoneId)
                .FirstAsync();
        }

        zoneId = string.IsNullOrWhiteSpace(zoneId) ? configuration["Packing:TimeZoneId"] : zoneId;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? DefaultTimeZoneId : zoneId.Trim());
            return TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).DateTime;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning(exception, "打包时区配置无效，改用 UTC: {ZoneId}", zoneId);
            return UtcNow();
        }
    }

    private static string NewGuid() => Guid.CreateVersion7().ToString("N");

    private static bool IsValidWeight(decimal? weightKg) => weightKg is null or (>= 0 and < 10000);

    private static bool SameCode(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class ProductMatch
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public int? MiddlePackageQuantity { get; set; }

        public PackingProductCandidateDto ToCandidate() => new()
        {
            ProductCode = ProductCode,
            ItemNumber = ItemNumber,
            ProductName = ProductName,
            Barcode = Barcode,
            ProductImage = ProductImage,
        };
    }

    private sealed record OwnerChoice(string? Error, int StatusCode, string? ErrorCode, string? Code)
    {
        public static OwnerChoice Of(string code) => new(null, 200, null, code);

        public static OwnerChoice Fail(int statusCode, string errorCode, string message) => new(message, statusCode, errorCode, null);
    }

    private sealed record DestinationResolution(string? Error, int Type, string? Code, string? CarrierGuid)
    {
        public static DestinationResolution Invalid(string error) => new(error, PackDestinationTypes.Unassigned, null, null);
    }

    private sealed record LocationResolution(string? Error, string? Code);
}
