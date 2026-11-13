using BlazorApp.Api.Utils;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePacking;

internal sealed partial class WarehousePackingService
{
    // ---- 商品识别 ----

    /// <summary>
    /// 按码找商品：条码 / 货号 / 商品编码精确匹配；无结果时试前导 0 的另一种写法，再试套装码。
    /// 套装码只用来认出母商品，数量仍按每扫一次 1 件计：套装码表混有组合套装的子件、多码和变量套装，
    /// 「套装数量」含义不统一，按它自动加倍会把箱内数量算错。
    /// </summary>
    private async Task<List<ProductMatch>> FindProductsByCodeAsync(string code)
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

        var setCode = await _db.Queryable<ProductSetCode>()
            .Where(item =>
                !item.IsDeleted
                && item.IsActive
                && (item.SetBarcode == code || item.SetItemNumber == code || item.SetProductCode == code)
            )
            .Select(item => item.ProductCode)
            .FirstAsync();
        return string.IsNullOrWhiteSpace(setCode)
            ? new List<ProductMatch>()
            : await LoadProductsAsync(new List<string> { setCode });
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

    // ---- 去向、货位、订货单 ----

    private async Task<DestinationResolution> ResolveDestinationAsync(WarehousePackingDestinationRequestDto request)
    {
        if (!WarehousePackDestinationTypes.IsKnown(request.DestinationType))
        {
            return DestinationResolution.Invalid("去向类型无效");
        }

        if (request.DestinationType == WarehousePackDestinationTypes.Unassigned)
        {
            return new DestinationResolution(null, WarehousePackDestinationTypes.Unassigned, null, null);
        }

        string? storeCode = null;
        if (!string.IsNullOrWhiteSpace(request.DestinationStoreCode))
        {
            var requested = request.DestinationStoreCode.Trim();
            storeCode = await _db.Queryable<Store>()
                .Where(store => !store.IsDeleted && store.StoreCode == requested)
                .Select(store => store.StoreCode)
                .FirstAsync();
            if (string.IsNullOrEmpty(storeCode))
            {
                return DestinationResolution.Invalid("找不到收货分店");
            }
        }

        if (request.DestinationType == WarehousePackDestinationTypes.Store)
        {
            return storeCode == null
                ? DestinationResolution.Invalid("请选择收货分店")
                : new DestinationResolution(null, WarehousePackDestinationTypes.Store, storeCode, null);
        }

        // 运输公司：必须选一家；最终收货分店可以先不填。
        var carrierGuid = request.CarrierGuid?.Trim();
        if (string.IsNullOrEmpty(carrierGuid))
        {
            return DestinationResolution.Invalid("请选择运输公司");
        }

        var carrierExists = await _db.Queryable<WarehousePackCarrier>().Where(carrier => carrier.CarrierGUID == carrierGuid).AnyAsync();
        return carrierExists
            ? new DestinationResolution(null, WarehousePackDestinationTypes.Carrier, storeCode, carrierGuid)
            : DestinationResolution.Invalid("找不到该运输公司");
    }

    /// <summary>货位编码或货位条码都认；传空表示清除暂存货位。</summary>
    private async Task<LocationResolution> ResolveLocationAsync(string? locationCode)
    {
        var code = locationCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return new LocationResolution(null, null);
        }

        var resolved = await _db.Queryable<Location>()
            .Where(location => !location.IsDeleted && (location.LocationCode == code || location.LocationBarcode == code))
            .Select(location => location.LocationCode)
            .FirstAsync();
        return string.IsNullOrEmpty(resolved)
            ? new LocationResolution("找不到该货位", null)
            : new LocationResolution(null, resolved);
    }

    private async Task<WareHouseOrder?> LoadOrderAsync(string orderGuid)
    {
        return await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && order.OrderGUID == orderGuid)
            .FirstAsync();
    }

    // ---- 组装返回 ----

    private async Task<List<WarehousePackingCartonSummaryDto>> BuildCartonSummariesAsync(List<WarehousePackCarton> cartons)
    {
        if (cartons.Count == 0)
        {
            return new List<WarehousePackingCartonSummaryDto>();
        }

        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var itemTotals = (await _db.Queryable<WarehousePackCartonItem>()
                .Where(item => cartonGuids.Contains(item.CartonGUID))
                .Select(item => new { item.CartonGUID, item.Quantity })
                .ToListAsync())
            .GroupBy(item => item.CartonGUID)
            .ToDictionary(group => group.Key, group => (Skus: group.Count(), Pieces: group.Sum(item => item.Quantity)));

        var palletGuids = cartons.Where(carton => carton.PalletGUID != null).Select(carton => carton.PalletGUID!).Distinct().ToList();
        var pallets = palletGuids.Count == 0
            ? new Dictionary<string, WarehousePackPallet>()
            : (await _db.Queryable<WarehousePackPallet>().Where(pallet => palletGuids.Contains(pallet.PalletGUID)).ToListAsync())
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
                // 上了托盘的箱去向跟随托盘：标签和查询都以托盘为准。
                var pallet = carton.PalletGUID == null ? null : pallets.GetValueOrDefault(carton.PalletGUID);
                return pallet == null
                    ? (carton.CartonGUID, carton.DestinationType, carton.DestinationStoreCode, carton.CarrierGUID)
                    : (carton.CartonGUID, pallet.DestinationType, pallet.DestinationStoreCode, pallet.CarrierGUID);
            }).ToList()
        );

        return cartons.Select(carton =>
        {
            var totals = itemTotals.GetValueOrDefault(carton.CartonGUID);
            return new WarehousePackingCartonSummaryDto
            {
                CartonGuid = carton.CartonGUID,
                CartonNo = carton.CartonNo,
                Status = carton.Status,
                OriginName = carton.OriginName,
                Destination = destinations[carton.CartonGUID],
                SkuCount = totals.Skus,
                PieceCount = totals.Pieces,
                PalletGuid = carton.PalletGUID,
                PalletNo = carton.PalletGUID == null ? null : pallets.GetValueOrDefault(carton.PalletGUID)?.PalletNo,
                LocationCode = carton.LocationCode,
                WeightKg = carton.WeightKg,
                OrderGuid = carton.OrderGUID,
                OrderNo = carton.OrderGUID == null ? null : orderNos.GetValueOrDefault(carton.OrderGUID),
                CreatedByName = carton.CreatedByName,
                CreatedAtUtc = carton.CreatedAtUtc,
                SealedByName = carton.SealedByName,
                SealedAtUtc = carton.SealedAtUtc,
                UpdatedAtUtc = carton.UpdatedAtUtc,
            };
        }).ToList();
    }

    private async Task<WarehousePackingCartonDetailDto> BuildCartonDetailAsync(WarehousePackCarton carton, string? changedProductCode)
    {
        var summary = (await BuildCartonSummariesAsync(new List<WarehousePackCarton> { carton }))[0];
        var items = await _db.Queryable<WarehousePackCartonItem>()
            .Where(item => item.CartonGUID == carton.CartonGUID)
            .ToListAsync();
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

            var siblingGuids = await _db.Queryable<WarehousePackCarton>()
                .Where(item =>
                    item.OrderGUID == orderGuid
                    && item.CartonGUID != carton.CartonGUID
                    && item.Status != WarehousePackCartonStatuses.Voided
                )
                .Select(item => item.CartonGUID)
                .ToListAsync();
            if (siblingGuids.Count > 0)
            {
                packedElsewhere = (await _db.Queryable<WarehousePackCartonItem>()
                        .Where(item => siblingGuids.Contains(item.CartonGUID) && productCodes.Contains(item.ProductCode))
                        .Select(item => new { item.ProductCode, item.Quantity })
                        .ToListAsync())
                    .GroupBy(item => item.ProductCode)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
            }
        }

        return new WarehousePackingCartonDetailDto
        {
            CartonGuid = summary.CartonGuid,
            CartonNo = summary.CartonNo,
            Status = summary.Status,
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
            UpdatedAtUtc = summary.UpdatedAtUtc,
            Remark = carton.Remark,
            ChangedProductCode = changedProductCode,
            Items = items
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.ProductCode, StringComparer.Ordinal)
                .Select(item =>
                {
                    var product = products.GetValueOrDefault(item.ProductCode);
                    return new WarehousePackingCartonItemDto
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

    private async Task<List<WarehousePackingPalletSummaryDto>> BuildPalletSummariesAsync(List<WarehousePackPallet> pallets)
    {
        if (pallets.Count == 0)
        {
            return new List<WarehousePackingPalletSummaryDto>();
        }

        var palletGuids = pallets.Select(pallet => pallet.PalletGUID).ToList();
        var cartons = await _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.PalletGUID != null && palletGuids.Contains(carton.PalletGUID))
            .Select(carton => new { carton.CartonGUID, carton.PalletGUID })
            .ToListAsync();
        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var items = cartonGuids.Count == 0
            ? new List<WarehousePackCartonItem>()
            : await _db.Queryable<WarehousePackCartonItem>().Where(item => cartonGuids.Contains(item.CartonGUID)).ToListAsync();
        var cartonPallet = cartons.ToDictionary(carton => carton.CartonGUID, carton => carton.PalletGUID!);
        var destinations = await BuildDestinationsAsync(
            pallets.Select(pallet => (pallet.PalletGUID, pallet.DestinationType, pallet.DestinationStoreCode, pallet.CarrierGUID)).ToList()
        );

        return pallets.Select(pallet =>
        {
            var palletItems = items.Where(item => cartonPallet[item.CartonGUID] == pallet.PalletGUID).ToList();
            return new WarehousePackingPalletSummaryDto
            {
                PalletGuid = pallet.PalletGUID,
                PalletNo = pallet.PalletNo,
                Status = pallet.Status,
                OriginName = pallet.OriginName,
                Destination = destinations[pallet.PalletGUID],
                ConsignmentNo = pallet.ConsignmentNo,
                LocationCode = pallet.LocationCode,
                WeightKg = pallet.WeightKg,
                CartonCount = cartons.Count(carton => carton.PalletGUID == pallet.PalletGUID),
                // 品种数按托盘去重：同一货号分装在几只箱里只算一个品种。
                SkuCount = palletItems.Select(item => item.ProductCode).Distinct().Count(),
                PieceCount = palletItems.Sum(item => item.Quantity),
                CreatedByName = pallet.CreatedByName,
                CreatedAtUtc = pallet.CreatedAtUtc,
                SealedByName = pallet.SealedByName,
                SealedAtUtc = pallet.SealedAtUtc,
                UpdatedAtUtc = pallet.UpdatedAtUtc,
            };
        }).ToList();
    }

    private async Task<WarehousePackingPalletDetailDto> BuildPalletDetailAsync(WarehousePackPallet pallet)
    {
        var summary = (await BuildPalletSummariesAsync(new List<WarehousePackPallet> { pallet }))[0];
        var cartons = await _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.PalletGUID == pallet.PalletGUID)
            .OrderBy(carton => carton.UpdatedAtUtc, OrderByType.Desc)
            .ToListAsync();
        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var items = cartonGuids.Count == 0
            ? new List<WarehousePackCartonItem>()
            : await _db.Queryable<WarehousePackCartonItem>().Where(item => cartonGuids.Contains(item.CartonGUID)).ToListAsync();
        var products = (await LoadProductsAsync(items.Select(item => item.ProductCode).Distinct().ToList()))
            .ToDictionary(product => product.ProductCode);

        return new WarehousePackingPalletDetailDto
        {
            PalletGuid = summary.PalletGuid,
            PalletNo = summary.PalletNo,
            Status = summary.Status,
            OriginName = summary.OriginName,
            Destination = summary.Destination,
            ConsignmentNo = summary.ConsignmentNo,
            LocationCode = summary.LocationCode,
            WeightKg = summary.WeightKg,
            CartonCount = summary.CartonCount,
            SkuCount = summary.SkuCount,
            PieceCount = summary.PieceCount,
            CreatedByName = summary.CreatedByName,
            CreatedAtUtc = summary.CreatedAtUtc,
            SealedByName = summary.SealedByName,
            SealedAtUtc = summary.SealedAtUtc,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            Remark = pallet.Remark,
            Cartons = await BuildCartonSummariesAsync(cartons),
            Products = items
                .GroupBy(item => item.ProductCode)
                .Select(group =>
                {
                    var product = products.GetValueOrDefault(group.Key);
                    return new WarehousePackingPalletProductDto
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

    private async Task<Dictionary<string, WarehousePackingDestinationDto>> BuildDestinationsAsync(
        List<(string Key, int Type, string? StoreCode, string? CarrierGuid)> sources
    )
    {
        var storeNames = await LoadStoreNamesAsync(sources.Select(source => source.StoreCode));
        var carrierGuids = sources.Where(source => source.CarrierGuid != null).Select(source => source.CarrierGuid!).Distinct().ToList();
        var carrierNames = carrierGuids.Count == 0
            ? new Dictionary<string, string>()
            : (await _db.Queryable<WarehousePackCarrier>()
                    .Where(carrier => carrierGuids.Contains(carrier.CarrierGUID))
                    .Select(carrier => new { carrier.CarrierGUID, carrier.Name })
                    .ToListAsync())
                .ToDictionary(carrier => carrier.CarrierGUID, carrier => carrier.Name);

        return sources.ToDictionary(
            source => source.Key,
            source => new WarehousePackingDestinationDto
            {
                DestinationType = source.Type,
                StoreCode = source.StoreCode,
                StoreName = source.StoreCode == null ? null : storeNames.GetValueOrDefault(source.StoreCode),
                CarrierGuid = source.CarrierGuid,
                CarrierName = source.CarrierGuid == null ? null : carrierNames.GetValueOrDefault(source.CarrierGuid),
            }
        );
    }

    private async Task<Dictionary<string, string>> LoadStoreNamesAsync(IEnumerable<string?> storeCodes)
    {
        var codes = storeCodes.Where(code => !string.IsNullOrEmpty(code)).Select(code => code!).Distinct().ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var stores = await _db.Queryable<Store>()
            .Where(store => codes.Contains(store.StoreCode))
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
        {
            names[store.StoreCode] = store.StoreName;
        }

        return names;
    }

    private async Task<Dictionary<string, string?>> LoadPalletLocationsAsync(List<WarehousePackCarton> cartons)
    {
        var palletGuids = cartons.Where(carton => carton.PalletGUID != null).Select(carton => carton.PalletGUID!).Distinct().ToList();
        if (palletGuids.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        return (await _db.Queryable<WarehousePackPallet>()
                .Where(pallet => palletGuids.Contains(pallet.PalletGUID))
                .Select(pallet => new { pallet.PalletGUID, pallet.LocationCode })
                .ToListAsync())
            .ToDictionary(pallet => pallet.PalletGUID, pallet => pallet.LocationCode);
    }

    // ---- 加锁、发号、事务 ----

    private async Task<WarehousePackCarton?> LockCartonAsync(string cartonGuid)
    {
        var query = _db.Queryable<WarehousePackCarton>().Where(carton => carton.CartonGUID == cartonGuid);
        // SQL Server：更新锁持有到事务结束，同一只箱的写入在此串行；SQLite 测试库整库串行，不需要提示。
        return await (IsSqlServer ? query.With(SqlWith.UpdLock) : query).FirstAsync();
    }

    private async Task<WarehousePackPallet?> LockPalletAsync(string palletGuid)
    {
        var query = _db.Queryable<WarehousePackPallet>().Where(pallet => pallet.PalletGUID == palletGuid);
        return await (IsSqlServer ? query.With(SqlWith.UpdLock) : query).FirstAsync();
    }

    /// <summary>
    /// 发号：前缀 + 仓库本地年月日 + 当日流水。必须在事务里调用——
    /// 行不存在时 UPDLOCK+HOLDLOCK 锁住键范围，两台设备同时新建当天第一只箱也不会重号。
    /// </summary>
    private async Task<string> NextNumberAsync(string prefix, int digits)
    {
        var key = prefix + LocalNow().ToString("yyMMdd");
        var query = _db.Queryable<WarehousePackSequence>().Where(sequence => sequence.SequenceKey == key);
        var row = await (IsSqlServer ? query.With("WITH(UPDLOCK,HOLDLOCK)") : query).FirstAsync();
        int next;
        if (row == null)
        {
            next = 1;
            await _db.Insertable(new WarehousePackSequence { SequenceKey = key, LastValue = next }).ExecuteCommandAsync();
        }
        else
        {
            next = row.LastValue + 1;
            row.LastValue = next;
            await _db.Updateable(row).ExecuteCommandAsync();
        }

        return key + next.ToString("D" + digits);
    }

    private async Task AddEventAsync(int targetType, string targetGuid, int action, string? detail, WarehousePackingOperator op, DateTime now)
    {
        await _db.Insertable(new WarehousePackEvent
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

    private async Task<WarehousePackingResult<T>> ExecuteInTransactionAsync<T>(Func<Task<WarehousePackingResult<T>>> action)
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
            logger.LogError(exception, "仓库打包事务失败");
            throw;
        }
    }

    private bool IsSqlServer => _db.CurrentConnectionConfig.DbType == DbType.SqlServer;

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    /// <summary>箱号 / 托盘号里的日期按仓库当地时间，避免早班建的箱编到前一天。</summary>
    private DateTime LocalNow()
    {
        var zoneId = configuration["WarehousePacking:TimeZoneId"];
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

    /// <summary>标签上的发出地（FROM）：取配置 WarehousePacking:OriginName，建箱 / 建托盘时写成快照。</summary>
    private string ResolveOriginName()
    {
        return Truncate(configuration["WarehousePacking:OriginName"], 100) ?? DefaultOriginName;
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

        public WarehousePackingProductCandidateDto ToCandidate() => new()
        {
            ProductCode = ProductCode,
            ItemNumber = ItemNumber,
            ProductName = ProductName,
            Barcode = Barcode,
            ProductImage = ProductImage,
        };
    }

    private sealed record DestinationResolution(string? Error, int Type, string? StoreCode, string? CarrierGuid)
    {
        public static DestinationResolution Invalid(string error) => new(error, WarehousePackDestinationTypes.Unassigned, null, null);
    }

    private sealed record LocationResolution(string? Error, string? Code);
}
