using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

public interface IPackingService
{
    // 箱
    Task<PackingResult<PackingCartonListDto>> ListCartonsAsync(PackingOperator op, string? ownerCode, string? filter, string? keyword, bool mine);

    Task<PackingResult<PackingCartonDetailDto>> CreateCartonAsync(PackingOperator op, PackingCreateCartonRequestDto request);

    Task<PackingResult<PackingCartonDetailDto>> GetCartonAsync(PackingOperator op, string cartonGuid);

    Task<PackingResult<PackingCartonDetailDto>> ScanIntoCartonAsync(PackingOperator op, string cartonGuid, PackingScanRequestDto request);

    Task<PackingResult<PackingCartonDetailDto>> SetCartonItemQuantityAsync(PackingOperator op, string cartonGuid, string productCode, int quantity);

    Task<PackingResult<PackingCartonDetailDto>> UpdateCartonHeaderAsync(PackingOperator op, string cartonGuid, PackingCartonHeaderRequestDto request);

    Task<PackingResult<PackingCartonDetailDto>> SealCartonAsync(PackingOperator op, string cartonGuid, PackingSealRequestDto request);

    Task<PackingResult<PackingCartonDetailDto>> ReopenCartonAsync(PackingOperator op, string cartonGuid);

    Task<PackingResult<PackingCartonDetailDto>> VoidCartonAsync(PackingOperator op, string cartonGuid);

    Task<PackingResult<PackingCartonDetailDto>> SetCartonLocationAsync(PackingOperator op, string cartonGuid, string? locationCode);

    Task<PackingResult<PackingCartonDetailDto>> DispatchCartonAsync(PackingOperator op, string cartonGuid);

    Task<PackingResult<PackingCartonDetailDto>> ReceiveCartonAsync(PackingOperator op, string cartonGuid, PackingReceiveCartonRequestDto request);

    // 托盘
    Task<PackingResult<PackingPalletListDto>> ListPalletsAsync(PackingOperator op, string? ownerCode, string? filter, string? keyword);

    Task<PackingResult<PackingPalletDetailDto>> CreatePalletAsync(PackingOperator op, PackingCreatePalletRequestDto request);

    Task<PackingResult<PackingPalletDetailDto>> GetPalletAsync(PackingOperator op, string palletGuid);

    Task<PackingResult<PackingPalletDetailDto>> AddCartonToPalletAsync(PackingOperator op, string palletGuid, PackingAddCartonRequestDto request);

    Task<PackingResult<PackingPalletDetailDto>> RemoveCartonFromPalletAsync(PackingOperator op, string palletGuid, string cartonGuid);

    Task<PackingResult<PackingPalletDetailDto>> UpdatePalletHeaderAsync(PackingOperator op, string palletGuid, PackingPalletHeaderRequestDto request);

    Task<PackingResult<PackingPalletDetailDto>> SealPalletAsync(PackingOperator op, string palletGuid, PackingSealRequestDto request);

    Task<PackingResult<PackingPalletDetailDto>> ReopenPalletAsync(PackingOperator op, string palletGuid);

    Task<PackingResult<PackingPalletDetailDto>> VoidPalletAsync(PackingOperator op, string palletGuid);

    Task<PackingResult<PackingPalletDetailDto>> SetPalletLocationAsync(PackingOperator op, string palletGuid, string? locationCode);

    Task<PackingResult<PackingPalletDetailDto>> DispatchPalletAsync(PackingOperator op, string palletGuid);

    Task<PackingResult<PackingPalletDetailDto>> ReceivePalletAsync(PackingOperator op, string palletGuid, PackingReceivePalletRequestDto request);

    // 签收
    Task<PackingResult<PackingIncomingDto>> ListIncomingAsync(PackingOperator op, string? ownerCode);

    Task<PackingResult<PackingReceiveLookupDto>> LookupForReceiveAsync(PackingOperator op, string? code);

    // 识别、查询、主数据
    Task<PackingResult<PackingResolveDto>> ResolveCodeAsync(PackingOperator op, string? code);

    Task<PackingResult<PackingLocateDto>> LocateProductAsync(PackingOperator op, string? ownerCode, string? code, bool allStores);

    Task<PackingResult<List<PackingEventDto>>> ListEventsAsync(PackingOperator op, int targetType, string targetGuid);

    Task<PackingResult<List<PackingCarrierDto>>> ListCarriersAsync(bool includeInactive);

    Task<PackingResult<PackingCarrierDto>> SaveCarrierAsync(PackingOperator op, string? carrierGuid, PackingCarrierSaveRequestDto request);

    Task<PackingResult<List<PackingLocationDto>>> ListLocationsAsync(PackingOperator op, string? ownerCode, bool includeInactive);

    Task<PackingResult<PackingLocationDto>> SaveLocationAsync(PackingOperator op, string? locationGuid, PackingLocationSaveRequestDto request);

    Task<PackingResult<List<PackingOwnerDto>>> ListDestinationsAsync(PackingOperator op, string? ownerCode);

    Task<PackingResult<List<PackingOrderOptionDto>>> SearchOrdersAsync(PackingOperator op, string? keyword);
}

/// <summary>
/// 扫码打包：商品 → 箱 →（可选）托盘。每只箱 / 托盘有所属地点（分店或总仓），位置挂在箱 / 托盘上，商品位置由箱明细推导。
/// 店间调货：发出方「发出」后箱变成在路上（所属地点仍是发出方），接收方扫码「签收」后所属地点才换成接收方。
/// 同一只箱 / 同一个托盘的写入先对该行加更新锁再执行，多台设备同时操作时串行化。
/// 本功能不改库存，也不回写订货单。
/// </summary>
internal sealed partial class PackingService(
    SqlSugarContext context,
    IConfiguration configuration,
    ILogger<PackingService> logger,
    TimeProvider? timeProvider = null
) : IPackingService
{
    internal const string CartonPrefix = "BX";
    internal const string PalletPrefix = "PL";
    internal const string LocationPrefix = "LC";
    private const int MaxListItems = 200;
    private const int MaxPiecesPerItem = 100000;
    private const string DefaultTimeZoneId = "Australia/Brisbane";

    private readonly ISqlSugarClient _db = context.Db;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // ---- 箱列表与新建 ----

    public async Task<PackingResult<PackingCartonListDto>> ListCartonsAsync(
        PackingOperator op,
        string? ownerCode,
        string? filter,
        string? keyword,
        bool mine
    )
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingCartonListDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var code = owner.Code!;
        var normalizedFilter = (filter ?? "all").Trim();
        var normalizedKeyword = keyword?.Trim();

        // 本地点能看到的箱：自己持有的，加上自己发出、已经不在手上（在路上或对方已签收）的。
        var packingCount = await _db.Queryable<PackCarton>()
            .Where(carton => carton.OwnerCode == code && carton.Status == PackCartonStatuses.Packing)
            .CountAsync();
        var storedCount = await _db.Queryable<PackCarton>()
            .Where(carton => carton.OwnerCode == code && carton.Status == PackCartonStatuses.Sealed)
            .CountAsync();
        var outgoingCount = await _db.Queryable<PackCarton>()
            .Where(carton =>
                carton.OriginCode == code
                && carton.Status != PackCartonStatuses.Voided
                && (carton.Status == PackCartonStatuses.InTransit || carton.OwnerCode != code)
            )
            .CountAsync();

        var query = _db.Queryable<PackCarton>()
            .Where(carton =>
                carton.Status != PackCartonStatuses.Voided
                && (carton.OwnerCode == code
                    || (carton.OriginCode == code
                        && (carton.Status == PackCartonStatuses.InTransit || carton.OwnerCode != code)))
            );
        if (normalizedFilter == "packing")
        {
            query = query.Where(carton => carton.OwnerCode == code && carton.Status == PackCartonStatuses.Packing);
        }
        else if (normalizedFilter == "stored")
        {
            query = query.Where(carton => carton.OwnerCode == code && carton.Status == PackCartonStatuses.Sealed);
        }
        else if (normalizedFilter == "outgoing")
        {
            query = query.Where(carton =>
                carton.OriginCode == code
                && (carton.Status == PackCartonStatuses.InTransit || carton.OwnerCode != code)
            );
        }

        if (mine)
        {
            var creator = op.UserGuid;
            query = query.Where(carton => carton.CreatedByUserGuid == creator);
        }

        if (!string.IsNullOrEmpty(normalizedKeyword))
        {
            // 关键字既可以是箱号片段，也可以是商品的条码 / 货号：后者先换成商品编码，再找装着它的箱。
            var productCodes = (await FindProductsByCodeAsync(normalizedKeyword, code)).Select(product => product.ProductCode).ToList();
            if (productCodes.Count == 0)
            {
                query = query.Where(carton => carton.CartonNo.Contains(normalizedKeyword));
            }
            else
            {
                var cartonGuids = await _db.Queryable<PackCartonItem>()
                    .Where(item => productCodes.Contains(item.ProductCode))
                    .Select(item => item.CartonGUID)
                    .Distinct()
                    .ToListAsync();
                query = query.Where(carton => carton.CartonNo.Contains(normalizedKeyword) || cartonGuids.Contains(carton.CartonGUID));
            }
        }

        var cartons = await query.OrderBy(carton => carton.UpdatedAtUtc, OrderByType.Desc).Take(MaxListItems).ToListAsync();
        return PackingResult<PackingCartonListDto>.Ok(new PackingCartonListDto
        {
            Items = await BuildCartonSummariesAsync(cartons),
            PackingCount = packingCount,
            StoredCount = storedCount,
            OutgoingCount = outgoingCount,
            IncomingCount = await CountIncomingAsync(code),
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> CreateCartonAsync(PackingOperator op, PackingCreateCartonRequestDto request)
    {
        var owner = RequireOwner(op, request.OwnerCode);
        if (owner.Error != null)
        {
            return CartonFail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var ownerCode = owner.Code!;
        string? orderGuid = null;
        string? orderStoreCode = null;
        if (!string.IsNullOrWhiteSpace(request.OrderGuid))
        {
            // 按订货单装箱是总仓的事（二期边拣边装）；分店一期不用。
            if (!PackOwners.IsWarehouse(ownerCode))
            {
                return CartonFail(400, PackingErrorCodes.InvalidRequest, "只有总仓可以按订货单装箱");
            }

            var order = await LoadOrderAsync(request.OrderGuid.Trim());
            if (order == null)
            {
                return CartonFail(404, PackingErrorCodes.OrderNotFound, "找不到该订货单");
            }

            orderGuid = order.OrderGUID;
            orderStoreCode = order.StoreCode;
        }

        // 关联了订货单又没单独指定去向时，去向默认就是订货单的分店。
        var destinationRequest = request.DestinationType == PackDestinationTypes.Unassigned && !string.IsNullOrWhiteSpace(orderStoreCode)
            ? new PackingDestinationRequestDto { DestinationType = PackDestinationTypes.Store, DestinationCode = orderStoreCode, CarrierGuid = request.CarrierGuid }
            : request;
        var destination = await ResolveDestinationAsync(ownerCode, destinationRequest);
        if (destination.Error != null)
        {
            return CartonFail(400, PackingErrorCodes.DestinationInvalid, destination.Error);
        }

        var originName = await OwnerNameAsync(ownerCode);
        return await ExecuteInTransactionAsync(async () =>
        {
            var now = UtcNow();
            var carton = new PackCarton
            {
                CartonGUID = NewGuid(),
                CartonNo = await NextNumberAsync(CartonPrefix, 4, ownerCode),
                Status = PackCartonStatuses.Packing,
                OwnerCode = ownerCode,
                OriginCode = ownerCode,
                OriginName = originName,
                DestinationType = destination.Type,
                DestinationCode = destination.Code,
                CarrierGUID = destination.CarrierGuid,
                OrderGUID = orderGuid,
                CreatedAtUtc = now,
                CreatedByUserGuid = op.UserGuid,
                CreatedByName = op.Name,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Created, null, op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> GetCartonAsync(PackingOperator op, string cartonGuid)
    {
        var carton = await _db.Queryable<PackCarton>().Where(item => item.CartonGUID == cartonGuid).FirstAsync();
        if (carton == null || !await CanViewCartonAsync(op, carton))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
    }

    // ---- 装箱 ----

    public async Task<PackingResult<PackingCartonDetailDto>> ScanIntoCartonAsync(
        PackingOperator op,
        string cartonGuid,
        PackingScanRequestDto request
    )
    {
        var requestedQuantity = request.Quantity ?? 1;
        if (requestedQuantity <= 0 || requestedQuantity > MaxPiecesPerItem)
        {
            return CartonFail(400, PackingErrorCodes.QuantityOutOfRange, "数量必须大于 0");
        }

        var ownerCode = await _db.Queryable<PackCarton>()
            .Where(item => item.CartonGUID == cartonGuid)
            .Select(item => item.OwnerCode)
            .FirstAsync();
        if (string.IsNullOrEmpty(ownerCode) || !op.Has(ownerCode))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        string productCode;
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
        {
            // 一码多品时界面已选定商品：只核对商品存在。
            var selected = request.ProductCode.Trim();
            var exists = await _db.Queryable<Product>().Where(item => !item.IsDeleted && item.ProductCode == selected).AnyAsync();
            if (!exists)
            {
                return CartonFail(404, PackingErrorCodes.CodeNotFound, "找不到该商品");
            }

            productCode = selected;
        }
        else
        {
            var code = request.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                return CartonFail(400, PackingErrorCodes.InvalidRequest, "条码不能为空");
            }

            var matches = await FindProductsByCodeAsync(code, ownerCode);
            if (matches.Count == 0)
            {
                return CartonFail(404, PackingErrorCodes.CodeNotFound, "找不到该条码对应的商品");
            }

            if (matches.Count > 1)
            {
                return PackingResult<PackingCartonDetailDto>.Fail(
                    409,
                    PackingErrorCodes.CodeAmbiguous,
                    "这个码对应多个商品，请选择",
                    new PackingCartonDetailDto { CartonGuid = cartonGuid, Candidates = matches.Select(match => match.ToCandidate()).ToList() }
                );
            }

            productCode = matches[0].ProductCode;
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: true, "该箱已封箱，不能再加商品；需要先重新开箱");
            if (denied != null)
            {
                return denied;
            }

            var now = UtcNow();
            var existing = await _db.Queryable<PackCartonItem>()
                .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == productCode)
                .FirstAsync();
            // long 运算避免两数相加溢出后绕过上限。
            var total = (long)(existing?.Quantity ?? 0) + requestedQuantity;
            if (total > MaxPiecesPerItem)
            {
                return CartonFail(400, PackingErrorCodes.QuantityOutOfRange, "单个商品数量超出上限");
            }

            await UpsertItemAsync(carton!, existing, productCode, (int)total, op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton!, productCode));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> SetCartonItemQuantityAsync(
        PackingOperator op,
        string cartonGuid,
        string productCode,
        int quantity
    )
    {
        if (quantity < 0 || quantity > MaxPiecesPerItem)
        {
            return CartonFail(400, PackingErrorCodes.QuantityOutOfRange, "数量超出范围");
        }

        var normalizedProductCode = productCode.Trim();
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: true, "该箱已封箱，不能再改数量；需要先重新开箱");
            if (denied != null)
            {
                return denied;
            }

            var existing = await _db.Queryable<PackCartonItem>()
                .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == normalizedProductCode)
                .FirstAsync();
            if (existing == null)
            {
                return CartonFail(404, PackingErrorCodes.CodeNotFound, "箱里没有这个商品");
            }

            var now = UtcNow();
            if (quantity == 0)
            {
                await _db.Deleteable<PackCartonItem>()
                    .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == normalizedProductCode)
                    .ExecuteCommandAsync();
                await TouchCartonAsync(carton!, now);
            }
            else
            {
                await UpsertItemAsync(carton!, existing, normalizedProductCode, quantity, op, now);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(
                await BuildCartonDetailAsync(carton!, quantity == 0 ? null : normalizedProductCode)
            );
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> UpdateCartonHeaderAsync(
        PackingOperator op,
        string cartonGuid,
        PackingCartonHeaderRequestDto request
    )
    {
        var ownerCode = await _db.Queryable<PackCarton>()
            .Where(item => item.CartonGUID == cartonGuid)
            .Select(item => item.OwnerCode)
            .FirstAsync();
        if (string.IsNullOrEmpty(ownerCode) || !op.Has(ownerCode))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        var destination = await ResolveDestinationAsync(ownerCode, request);
        if (destination.Error != null)
        {
            return CartonFail(400, PackingErrorCodes.DestinationInvalid, destination.Error);
        }

        if (!IsValidWeight(request.WeightKg))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        string? orderGuid = null;
        if (!string.IsNullOrWhiteSpace(request.OrderGuid))
        {
            var order = await LoadOrderAsync(request.OrderGuid.Trim());
            if (order == null)
            {
                return CartonFail(404, PackingErrorCodes.OrderNotFound, "找不到该订货单");
            }

            orderGuid = order.OrderGUID;
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: false, null);
            if (denied != null)
            {
                return denied;
            }

            var destinationChanged = carton!.DestinationType != destination.Type
                || !SameCode(carton.DestinationCode, destination.Code)
                || !SameCode(carton.CarrierGUID, destination.CarrierGuid);
            if (destinationChanged && carton.PalletGUID != null)
            {
                // 上了托盘的箱跟着托盘走：单独改箱的去向会让标签和托盘对不上。
                return CartonFail(409, PackingErrorCodes.CartonOnPallet, "该箱已在托盘上，去向跟随托盘；请先把箱移出托盘");
            }

            if (!SameCode(carton.OrderGUID, orderGuid) && carton.Status != PackCartonStatuses.Packing)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotPacking, "已封箱的箱不能更换关联订货单；需要先重新开箱");
            }

            var now = UtcNow();
            carton.DestinationType = destination.Type;
            carton.DestinationCode = destination.Code;
            carton.CarrierGUID = destination.CarrierGuid;
            carton.OrderGUID = orderGuid;
            carton.WeightKg = request.WeightKg;
            carton.Remark = Truncate(request.Remark, 200);
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            if (destinationChanged)
            {
                await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.HeaderChanged, "去向已更改", op, now);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> SealCartonAsync(
        PackingOperator op,
        string cartonGuid,
        PackingSealRequestDto request
    )
    {
        if (!IsValidWeight(request.WeightKg))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton != null && op.Has(carton.OwnerCode) && carton.Status == PackCartonStatuses.Sealed)
            {
                // 重复点「封箱」或网络重试：已封箱直接返回，客户端可以照常打印标签。
                return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
            }

            var denied = CheckEditable(op, carton, requirePacking: true, "该箱当前不能封箱");
            if (denied != null)
            {
                return denied;
            }

            var hasItems = await _db.Queryable<PackCartonItem>().Where(item => item.CartonGUID == cartonGuid).AnyAsync();
            if (!hasItems)
            {
                return CartonFail(409, PackingErrorCodes.CartonEmpty, "空箱不能封箱");
            }

            var now = UtcNow();
            carton!.Status = PackCartonStatuses.Sealed;
            carton.SealedAtUtc = now;
            carton.SealedByUserGuid = op.UserGuid;
            carton.SealedByName = op.Name;
            if (request.WeightKg != null)
            {
                carton.WeightKg = request.WeightKg;
            }

            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Sealed, null, op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> ReopenCartonAsync(PackingOperator op, string cartonGuid)
    {
        if (!op.IsManager)
        {
            return CartonFail(403, PackingErrorCodes.NotAllowed, "只有店长或仓库经理可以重新开箱");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: false, null);
            if (denied != null)
            {
                return denied;
            }

            if (carton!.Status != PackCartonStatuses.Sealed)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotSealed, "只有已封箱的箱可以重新开箱");
            }

            if (carton.PalletGUID != null)
            {
                return CartonFail(409, PackingErrorCodes.CartonOnPallet, "该箱已在托盘上，请先把箱移出托盘");
            }

            // 箱号不变：贴在纸箱上的标签继续有效，改动留在操作记录里。
            var now = UtcNow();
            carton.Status = PackCartonStatuses.Packing;
            carton.SealedAtUtc = null;
            carton.SealedByUserGuid = null;
            carton.SealedByName = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Reopened, null, op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> VoidCartonAsync(PackingOperator op, string cartonGuid)
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: true, "只有装箱中的箱可以作废；已封箱的箱请先重新开箱");
            if (denied != null)
            {
                return denied;
            }

            // 作废只改状态，箱号不复用；箱内明细保留，位置查询会排除作废的箱。
            var now = UtcNow();
            carton!.Status = PackCartonStatuses.Voided;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Voided, null, op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> SetCartonLocationAsync(PackingOperator op, string cartonGuid, string? locationCode)
    {
        var ownerCode = await _db.Queryable<PackCarton>()
            .Where(item => item.CartonGUID == cartonGuid)
            .Select(item => item.OwnerCode)
            .FirstAsync();
        if (string.IsNullOrEmpty(ownerCode) || !op.Has(ownerCode))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        var location = await ResolveLocationAsync(ownerCode, locationCode);
        if (location.Error != null)
        {
            return CartonFail(404, PackingErrorCodes.LocationNotFound, location.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: false, null);
            if (denied != null)
            {
                return denied;
            }

            if (carton!.Status == PackCartonStatuses.Voided)
            {
                return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.PalletGUID != null)
            {
                return CartonFail(409, PackingErrorCodes.CartonOnPallet, "该箱已在托盘上，位置跟随托盘");
            }

            var now = UtcNow();
            carton.LocationCode = location.Code;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.LocationChanged, location.Code ?? "清除位置", op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    private async Task UpsertItemAsync(
        PackCarton carton,
        PackCartonItem? existing,
        string productCode,
        int quantity,
        PackingOperator op,
        DateTime now
    )
    {
        if (existing == null)
        {
            await _db.Insertable(new PackCartonItem
            {
                CartonGUID = carton.CartonGUID,
                ProductCode = productCode,
                Quantity = quantity,
                UpdatedAtUtc = now,
                UpdatedByName = op.Name,
            }).ExecuteCommandAsync();
        }
        else
        {
            existing.Quantity = quantity;
            existing.UpdatedAtUtc = now;
            existing.UpdatedByName = op.Name;
            await _db.Updateable(existing).ExecuteCommandAsync();
        }

        await TouchCartonAsync(carton, now);
    }

    private async Task TouchCartonAsync(PackCarton carton, DateTime now)
    {
        carton.UpdatedAtUtc = now;
        await _db.Updateable<PackCarton>()
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.CartonGUID == carton.CartonGUID)
            .ExecuteCommandAsync();
    }
}
