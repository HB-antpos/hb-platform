using BlazorApp.Api.Data;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePacking;

public interface IWarehousePackingService
{
    Task<WarehousePackingResult<WarehousePackingCartonListDto>> ListCartonsAsync(string? filter, string? keyword, string? createdByUserGuid);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> CreateCartonAsync(WarehousePackingCreateCartonRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> GetCartonAsync(string cartonGuid);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> ScanIntoCartonAsync(string cartonGuid, WarehousePackingScanRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SetCartonItemQuantityAsync(string cartonGuid, string productCode, int quantity, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> UpdateCartonHeaderAsync(string cartonGuid, WarehousePackingCartonHeaderRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SealCartonAsync(string cartonGuid, WarehousePackingSealRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> ReopenCartonAsync(string cartonGuid, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> VoidCartonAsync(string cartonGuid, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SetCartonLocationAsync(string cartonGuid, string? locationCode, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletListDto>> ListPalletsAsync(string? filter, string? keyword);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> CreatePalletAsync(WarehousePackingDestinationRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> GetPalletAsync(string palletGuid);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> AddCartonToPalletAsync(string palletGuid, WarehousePackingAddCartonRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> RemoveCartonFromPalletAsync(string palletGuid, string cartonGuid, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> UpdatePalletHeaderAsync(string palletGuid, WarehousePackingPalletHeaderRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> SealPalletAsync(string palletGuid, WarehousePackingSealRequestDto request, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> ReopenPalletAsync(string palletGuid, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> VoidPalletAsync(string palletGuid, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> SetPalletLocationAsync(string palletGuid, string? locationCode, WarehousePackingOperator op);

    Task<WarehousePackingResult<WarehousePackingResolveDto>> ResolveCodeAsync(string? code);

    Task<WarehousePackingResult<WarehousePackingLocateDto>> LocateProductAsync(string? code);

    Task<WarehousePackingResult<List<WarehousePackingEventDto>>> ListEventsAsync(int targetType, string targetGuid);

    Task<WarehousePackingResult<List<WarehousePackingCarrierDto>>> ListCarriersAsync(bool includeInactive);

    Task<WarehousePackingResult<WarehousePackingCarrierDto>> SaveCarrierAsync(string? carrierGuid, WarehousePackingCarrierSaveRequestDto request);

    Task<WarehousePackingResult<List<WarehousePackingOrderOptionDto>>> SearchOrdersAsync(string? keyword);
}

/// <summary>
/// 仓库扫码打包：商品 → 箱 →（可选）托盘。位置挂在箱 / 托盘上，商品位置由箱明细推导。
/// 同一只箱 / 同一个托盘的写入先对该行加更新锁再执行，多台设备同时操作时串行化。
/// 本功能不改仓库库存，也不回写订货单；关联订货单只用于对照配货数量。
/// </summary>
internal sealed partial class WarehousePackingService(
    SqlSugarContext context,
    IConfiguration configuration,
    ILogger<WarehousePackingService> logger,
    TimeProvider? timeProvider = null
) : IWarehousePackingService
{
    internal const string CartonPrefix = "BX";
    internal const string PalletPrefix = "PL";
    private const int MaxListItems = 200;
    private const int MaxPiecesPerItem = 100000;
    private const string DefaultOriginName = "HB WAREHOUSE";
    private const string DefaultTimeZoneId = "Australia/Brisbane";

    private readonly ISqlSugarClient _db = context.Db;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // ---- 箱 ----

    public async Task<WarehousePackingResult<WarehousePackingCartonListDto>> ListCartonsAsync(
        string? filter,
        string? keyword,
        string? createdByUserGuid
    )
    {
        var normalizedFilter = (filter ?? "all").Trim();
        var normalizedKeyword = keyword?.Trim();

        var packingCount = await _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.Status == WarehousePackCartonStatuses.Packing)
            .CountAsync();
        var sealedCount = await _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.Status == WarehousePackCartonStatuses.Sealed)
            .CountAsync();
        var looseSealedCount = await _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.Status == WarehousePackCartonStatuses.Sealed && carton.PalletGUID == null)
            .CountAsync();

        var query = _db.Queryable<WarehousePackCarton>()
            .Where(carton => carton.Status != WarehousePackCartonStatuses.Voided);
        if (normalizedFilter == "packing")
        {
            query = query.Where(carton => carton.Status == WarehousePackCartonStatuses.Packing);
        }
        else if (normalizedFilter == "sealed")
        {
            query = query.Where(carton => carton.Status == WarehousePackCartonStatuses.Sealed);
        }
        else if (normalizedFilter == "loose")
        {
            query = query.Where(carton => carton.Status == WarehousePackCartonStatuses.Sealed && carton.PalletGUID == null);
        }

        if (!string.IsNullOrWhiteSpace(createdByUserGuid))
        {
            var creator = createdByUserGuid.Trim();
            query = query.Where(carton => carton.CreatedByUserGuid == creator);
        }

        if (!string.IsNullOrEmpty(normalizedKeyword))
        {
            // 关键字既可以是箱号片段，也可以是商品的条码 / 货号：后者先换成商品编码，再找装着它的箱。
            var productCodes = (await FindProductsByCodeAsync(normalizedKeyword))
                .Select(product => product.ProductCode)
                .ToList();
            if (productCodes.Count == 0)
            {
                query = query.Where(carton => carton.CartonNo.Contains(normalizedKeyword));
            }
            else
            {
                var cartonGuids = await _db.Queryable<WarehousePackCartonItem>()
                    .Where(item => productCodes.Contains(item.ProductCode))
                    .Select(item => item.CartonGUID)
                    .Distinct()
                    .ToListAsync();
                query = query.Where(carton => carton.CartonNo.Contains(normalizedKeyword) || cartonGuids.Contains(carton.CartonGUID));
            }
        }

        var cartons = await query
            .OrderBy(carton => carton.UpdatedAtUtc, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();

        return WarehousePackingResult<WarehousePackingCartonListDto>.Ok(new WarehousePackingCartonListDto
        {
            Items = await BuildCartonSummariesAsync(cartons),
            PackingCount = packingCount,
            SealedCount = sealedCount,
            LooseSealedCount = looseSealedCount,
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> CreateCartonAsync(
        WarehousePackingCreateCartonRequestDto request,
        WarehousePackingOperator op
    )
    {
        string? orderGuid = null;
        string? orderStoreCode = null;
        if (!string.IsNullOrWhiteSpace(request.OrderGuid))
        {
            var order = await LoadOrderAsync(request.OrderGuid.Trim());
            if (order == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.OrderNotFound, "找不到该订货单");
            }

            orderGuid = order.OrderGUID;
            orderStoreCode = order.StoreCode;
        }

        // 关联了订货单又没单独指定去向时，去向默认就是订货单的分店。
        var destinationRequest = request.DestinationType == WarehousePackDestinationTypes.Unassigned
            && !string.IsNullOrWhiteSpace(orderStoreCode)
            ? new WarehousePackingDestinationRequestDto
            {
                DestinationType = WarehousePackDestinationTypes.Store,
                DestinationStoreCode = orderStoreCode,
            }
            : request;
        var destination = await ResolveDestinationAsync(destinationRequest);
        if (destination.Error != null)
        {
            return CartonFail(400, WarehousePackingErrorCodes.DestinationInvalid, destination.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var now = UtcNow();
            var carton = new WarehousePackCarton
            {
                CartonGUID = NewGuid(),
                CartonNo = await NextNumberAsync(CartonPrefix, 4),
                Status = WarehousePackCartonStatuses.Packing,
                OriginName = ResolveOriginName(),
                DestinationType = destination.Type,
                DestinationStoreCode = destination.StoreCode,
                CarrierGUID = destination.CarrierGuid,
                OrderGUID = orderGuid,
                CreatedAtUtc = now,
                CreatedByUserGuid = op.UserGuid,
                CreatedByName = op.Name,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(carton).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.Created, null, op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> GetCartonAsync(string cartonGuid)
    {
        var carton = await _db.Queryable<WarehousePackCarton>().Where(item => item.CartonGUID == cartonGuid).FirstAsync();
        return carton == null
            ? CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱")
            : WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> ScanIntoCartonAsync(
        string cartonGuid,
        WarehousePackingScanRequestDto request,
        WarehousePackingOperator op
    )
    {
        var requestedQuantity = request.Quantity ?? 1;
        if (requestedQuantity <= 0 || requestedQuantity > MaxPiecesPerItem)
        {
            return CartonFail(400, WarehousePackingErrorCodes.QuantityOutOfRange, "数量必须大于 0");
        }

        string productCode;
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
        {
            // 一码多品时界面已选定商品：只核对商品存在。
            var selected = request.ProductCode.Trim();
            var exists = await _db.Queryable<Product>().Where(item => !item.IsDeleted && item.ProductCode == selected).AnyAsync();
            if (!exists)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CodeNotFound, "找不到该商品");
            }

            productCode = selected;
        }
        else
        {
            var code = request.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                return CartonFail(400, WarehousePackingErrorCodes.InvalidRequest, "条码不能为空");
            }

            var matches = await FindProductsByCodeAsync(code);
            if (matches.Count == 0)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CodeNotFound, "找不到该条码对应的商品");
            }

            if (matches.Count > 1)
            {
                return WarehousePackingResult<WarehousePackingCartonDetailDto>.Fail(
                    409,
                    WarehousePackingErrorCodes.CodeAmbiguous,
                    "这个码对应多个商品，请选择",
                    new WarehousePackingCartonDetailDto { CartonGuid = cartonGuid, Candidates = matches.Select(match => match.ToCandidate()).ToList() }
                );
            }

            productCode = matches[0].ProductCode;
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.Status != WarehousePackCartonStatuses.Packing)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotPacking, "该箱已封箱，不能再加商品；需要先重新开箱");
            }

            var now = UtcNow();
            var existing = await _db.Queryable<WarehousePackCartonItem>()
                .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == productCode)
                .FirstAsync();
            // long 运算避免两数相加溢出后绕过上限。
            var total = (long)(existing?.Quantity ?? 0) + requestedQuantity;
            if (total > MaxPiecesPerItem)
            {
                return CartonFail(400, WarehousePackingErrorCodes.QuantityOutOfRange, "单个商品数量超出上限");
            }

            await UpsertItemAsync(carton, existing, productCode, (int)total, op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, productCode));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SetCartonItemQuantityAsync(
        string cartonGuid,
        string productCode,
        int quantity,
        WarehousePackingOperator op
    )
    {
        if (quantity < 0 || quantity > MaxPiecesPerItem)
        {
            return CartonFail(400, WarehousePackingErrorCodes.QuantityOutOfRange, "数量超出范围");
        }

        var normalizedProductCode = productCode.Trim();
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.Status != WarehousePackCartonStatuses.Packing)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotPacking, "该箱已封箱，不能再改数量；需要先重新开箱");
            }

            var existing = await _db.Queryable<WarehousePackCartonItem>()
                .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == normalizedProductCode)
                .FirstAsync();
            if (existing == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CodeNotFound, "箱里没有这个商品");
            }

            var now = UtcNow();
            if (quantity == 0)
            {
                await _db.Deleteable<WarehousePackCartonItem>()
                    .Where(item => item.CartonGUID == cartonGuid && item.ProductCode == normalizedProductCode)
                    .ExecuteCommandAsync();
                await TouchCartonAsync(carton, now);
            }
            else
            {
                await UpsertItemAsync(carton, existing, normalizedProductCode, quantity, op, now);
            }

            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(
                await BuildCartonDetailAsync(carton, quantity == 0 ? null : normalizedProductCode)
            );
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> UpdateCartonHeaderAsync(
        string cartonGuid,
        WarehousePackingCartonHeaderRequestDto request,
        WarehousePackingOperator op
    )
    {
        var destination = await ResolveDestinationAsync(request);
        if (destination.Error != null)
        {
            return CartonFail(400, WarehousePackingErrorCodes.DestinationInvalid, destination.Error);
        }

        if (!IsValidWeight(request.WeightKg))
        {
            return CartonFail(400, WarehousePackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        string? orderGuid = null;
        if (!string.IsNullOrWhiteSpace(request.OrderGuid))
        {
            var order = await LoadOrderAsync(request.OrderGuid.Trim());
            if (order == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.OrderNotFound, "找不到该订货单");
            }

            orderGuid = order.OrderGUID;
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null || carton.Status == WarehousePackCartonStatuses.Voided)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            var destinationChanged = carton.DestinationType != destination.Type
                || !SameCode(carton.DestinationStoreCode, destination.StoreCode)
                || !SameCode(carton.CarrierGUID, destination.CarrierGuid);
            if (destinationChanged && carton.PalletGUID != null)
            {
                // 上了托盘的箱跟着托盘走：单独改箱的去向会让标签和托盘对不上。
                return CartonFail(409, WarehousePackingErrorCodes.CartonOnPallet, "该箱已在托盘上，去向跟随托盘；请先把箱移出托盘");
            }

            if (!SameCode(carton.OrderGUID, orderGuid) && carton.Status != WarehousePackCartonStatuses.Packing)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotPacking, "已封箱的箱不能更换关联订货单；需要先重新开箱");
            }

            var now = UtcNow();
            carton.DestinationType = destination.Type;
            carton.DestinationStoreCode = destination.StoreCode;
            carton.CarrierGUID = destination.CarrierGuid;
            carton.OrderGUID = orderGuid;
            carton.WeightKg = request.WeightKg;
            carton.Remark = Truncate(request.Remark, 200);
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            if (destinationChanged)
            {
                await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.HeaderChanged, "去向已更改", op, now);
            }

            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SealCartonAsync(
        string cartonGuid,
        WarehousePackingSealRequestDto request,
        WarehousePackingOperator op
    )
    {
        if (!IsValidWeight(request.WeightKg))
        {
            return CartonFail(400, WarehousePackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.Status == WarehousePackCartonStatuses.Sealed)
            {
                // 重复点「封箱」或网络重试：已封箱直接返回，客户端可以照常打印标签。
                return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
            }

            if (carton.Status != WarehousePackCartonStatuses.Packing)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotPacking, "该箱当前不能封箱");
            }

            var hasItems = await _db.Queryable<WarehousePackCartonItem>().Where(item => item.CartonGUID == cartonGuid).AnyAsync();
            if (!hasItems)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonEmpty, "空箱不能封箱");
            }

            var now = UtcNow();
            carton.Status = WarehousePackCartonStatuses.Sealed;
            carton.SealedAtUtc = now;
            carton.SealedByUserGuid = op.UserGuid;
            carton.SealedByName = op.Name;
            if (request.WeightKg != null)
            {
                carton.WeightKg = request.WeightKg;
            }

            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.Sealed, null, op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> ReopenCartonAsync(
        string cartonGuid,
        WarehousePackingOperator op
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.Status != WarehousePackCartonStatuses.Sealed)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotSealed, "只有已封箱的箱可以重新开箱");
            }

            if (carton.PalletGUID != null)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonOnPallet, "该箱已在托盘上，请先把箱移出托盘");
            }

            // 箱号不变：贴在纸箱上的标签继续有效，改动留在操作记录里。
            var now = UtcNow();
            carton.Status = WarehousePackCartonStatuses.Packing;
            carton.SealedAtUtc = null;
            carton.SealedByUserGuid = null;
            carton.SealedByName = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.Reopened, null, op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> VoidCartonAsync(
        string cartonGuid,
        WarehousePackingOperator op
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.Status != WarehousePackCartonStatuses.Packing)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonNotPacking, "只有装箱中的箱可以作废；已封箱的箱请先重新开箱");
            }

            // 作废只改状态，箱号不复用；箱内明细保留，位置查询会排除作废的箱。
            var now = UtcNow();
            carton.Status = WarehousePackCartonStatuses.Voided;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.Voided, null, op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingCartonDetailDto>> SetCartonLocationAsync(
        string cartonGuid,
        string? locationCode,
        WarehousePackingOperator op
    )
    {
        var location = await ResolveLocationAsync(locationCode);
        if (location.Error != null)
        {
            return CartonFail(404, WarehousePackingErrorCodes.LocationNotFound, location.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null || carton.Status == WarehousePackCartonStatuses.Voided)
            {
                return CartonFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.PalletGUID != null)
            {
                return CartonFail(409, WarehousePackingErrorCodes.CartonOnPallet, "该箱已在托盘上，位置跟随托盘");
            }

            var now = UtcNow();
            carton.LocationCode = location.Code;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.LocationChanged, location.Code ?? "清除货位", op, now);
            return WarehousePackingResult<WarehousePackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    private async Task UpsertItemAsync(
        WarehousePackCarton carton,
        WarehousePackCartonItem? existing,
        string productCode,
        int quantity,
        WarehousePackingOperator op,
        DateTime now
    )
    {
        if (existing == null)
        {
            await _db.Insertable(new WarehousePackCartonItem
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

    private async Task TouchCartonAsync(WarehousePackCarton carton, DateTime now)
    {
        carton.UpdatedAtUtc = now;
        await _db.Updateable<WarehousePackCarton>()
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.CartonGUID == carton.CartonGUID)
            .ExecuteCommandAsync();
    }

    private static WarehousePackingResult<WarehousePackingCartonDetailDto> CartonFail(int statusCode, string errorCode, string message) =>
        WarehousePackingResult<WarehousePackingCartonDetailDto>.Fail(statusCode, errorCode, message);
}
