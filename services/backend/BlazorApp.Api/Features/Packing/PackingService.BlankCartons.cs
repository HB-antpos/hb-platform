using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

/// <summary>
/// 空箱标（批量预先创建、预先打印标签的箱）。
/// 空箱标仍是 Status=1（装箱中）的箱，靠 PackCarton.BatchGUID / ActivatedAtUtc 标记：
/// 「未启用」= BatchGUID 不为空且 ActivatedAtUtc 为空（此时没有商品、没有订货单），不计入装箱中数量，列表里单独成类。
/// 启用 = 第一次装入商品、关联订货单（拣货端 attach 或改表头）或显式 activate。封箱规则不变（空箱仍 409 CARTON_EMPTY）。
/// 本功能不改库存。
/// </summary>
internal sealed partial class PackingService
{
    /// <summary>一次批量创建的张数上限。</summary>
    internal const int MaxBatchCartons = 100;

    /// <summary>一个地点「未启用的空箱标」数量上限（现有 + 本次 &gt; 上限就拒绝）。</summary>
    internal const int BlankLimit = 300;

    /// <summary>批量回报打印 / 批量作废一次最多处理的箱数。</summary>
    internal const int MaxBatchGuids = 100;

    private const int DefaultBatchListLimit = 20;
    private const int MaxBatchListLimit = 50;

    /// <summary>多行插入的分块大小：SQL Server 一条语句最多 2100 个参数，PackCarton 约 30 列，按 50 行分块留足余量。</summary>
    private const int InsertChunkSize = 50;

    /// <summary>未启用的空箱标：属于某批次且还没启用。</summary>
    internal static bool IsUnactivatedBlank(PackCarton carton) => carton.BatchGUID != null && carton.ActivatedAtUtc == null;

    private async Task<int> CountUnactivatedBlankAsync(string ownerCode)
    {
        return await _db.Queryable<PackCarton>()
            .Where(carton =>
                carton.OwnerCode == ownerCode
                && carton.Status == PackCartonStatuses.Packing
                && carton.BatchGUID != null
                && carton.ActivatedAtUtc == null
            )
            .CountAsync();
    }

    // ---- 批量创建 ----

    public async Task<PackingResult<PackingBatchCreateDto>> CreateCartonBatchAsync(PackingOperator op, PackingCreateBatchRequestDto request)
    {
        var owner = RequireOwner(op, request.OwnerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingBatchCreateDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var ownerCode = owner.Code!;
        if (request.ClientRequestId == Guid.Empty)
        {
            return PackingResult<PackingBatchCreateDto>.Fail(400, PackingErrorCodes.InvalidRequest, "缺少请求编号");
        }

        // 同一个请求编号重放（网络重试 / 重复点击）：先于张数、额度与去向校验，直接返回已有批次。
        var replay = await TryReplayBatchAsync(op, ownerCode, request.ClientRequestId);
        if (replay != null)
        {
            return replay;
        }

        if (request.Count < 1 || request.Count > MaxBatchCartons)
        {
            return PackingResult<PackingBatchCreateDto>.Fail(400, PackingErrorCodes.QuantityOutOfRange, $"一次最多创建 {MaxBatchCartons} 张空箱标");
        }

        var destination = await ResolveDestinationAsync(ownerCode, request);
        if (destination.Error != null)
        {
            return PackingResult<PackingBatchCreateDto>.Fail(400, PackingErrorCodes.DestinationInvalid, destination.Error);
        }

        var originName = await OwnerNameAsync(ownerCode);
        try
        {
            return await ExecuteInTransactionAsync(async () =>
            {
                // 先取号：对发号行加锁后，下面的额度检查与建箱对所有并发的批量创建串行，额度不会被同时越过。
                var numbers = await NextNumberRangeAsync(CartonPrefix, 4, ownerCode, request.Count);
                var existing = await CountUnactivatedBlankAsync(ownerCode);
                if (existing + request.Count > BlankLimit)
                {
                    // 失败结果会让事务回滚，刚取的号一并撤销，不留空洞。
                    return PackingResult<PackingBatchCreateDto>.Fail(
                        409,
                        PackingErrorCodes.BlankLimitExceeded,
                        $"未启用的空箱标最多 {BlankLimit} 张，现有 {existing} 张，不能再创建 {request.Count} 张",
                        new PackingBatchCreateDto { Existing = existing, Limit = BlankLimit }
                    );
                }

                var now = UtcNow();
                var batch = new PackCartonBatch
                {
                    BatchGUID = NewGuid(),
                    OwnerCode = ownerCode,
                    Count = request.Count,
                    FirstCartonNo = numbers[0],
                    LastCartonNo = numbers[^1],
                    DestinationType = destination.Type,
                    DestinationCode = destination.Code,
                    CarrierGUID = destination.CarrierGuid,
                    ClientRequestId = request.ClientRequestId,
                    CreatedAtUtc = now,
                    CreatedByUserGuid = op.UserGuid,
                    CreatedByName = op.Name,
                };
                await _db.Insertable(batch).ExecuteCommandAsync();

                // 空箱标不带订货单；去向可以先不定（装箱或发出时再定）。
                var cartons = numbers.Select((number, index) => new PackCarton
                {
                    CartonGUID = NewGuid(),
                    CartonNo = number,
                    Status = PackCartonStatuses.Packing,
                    OwnerCode = ownerCode,
                    OriginCode = ownerCode,
                    OriginName = originName,
                    DestinationType = destination.Type,
                    DestinationCode = destination.Code,
                    CarrierGUID = destination.CarrierGuid,
                    CreatedAtUtc = now,
                    CreatedByUserGuid = op.UserGuid,
                    CreatedByName = op.Name,
                    UpdatedAtUtc = now,
                    BatchGUID = batch.BatchGUID,
                    BatchSeq = index + 1,
                }).ToList();
                var events = cartons.Select(carton => new PackEvent
                {
                    EventGUID = NewGuid(),
                    TargetType = PackTargetTypes.Carton,
                    TargetGUID = carton.CartonGUID,
                    Action = PackEventActions.Created,
                    Detail = $"批量创建空箱标（第 {carton.BatchSeq} / {request.Count} 张）",
                    OperatorUserGuid = op.UserGuid,
                    OperatorName = op.Name,
                    CreatedAtUtc = now,
                }).ToList();
                foreach (var chunk in cartons.Chunk(InsertChunkSize))
                {
                    await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
                }

                foreach (var chunk in events.Chunk(InsertChunkSize))
                {
                    await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
                }

                return PackingResult<PackingBatchCreateDto>.Ok(await BuildBatchCreateAsync(batch, cartons, duplicate: false));
            });
        }
        catch (Exception exception) when (!_db.Ado.IsAnyTran())
        {
            // 两个相同 clientRequestId 的请求同时到：后到的会在 ClientRequestId 唯一索引上失败（事务已回滚）。
            // 此时先到的批次已提交，当作重放返回；查不到才是真错误，原样抛出。
            var concurrent = await TryReplayBatchAsync(op, ownerCode, request.ClientRequestId);
            if (concurrent != null)
            {
                logger.LogInformation(exception, "批量空箱标请求编号并发重复，按重放处理: {ClientRequestId}", request.ClientRequestId);
                return concurrent;
            }

            throw;
        }
    }

    /// <summary>按请求编号找已有批次；找到且属于本次请求的地点就当重放返回，属于别的地点说明编号被占用。</summary>
    private async Task<PackingResult<PackingBatchCreateDto>?> TryReplayBatchAsync(PackingOperator op, string ownerCode, Guid clientRequestId)
    {
        var batch = await _db.Queryable<PackCartonBatch>().Where(item => item.ClientRequestId == clientRequestId).FirstAsync();
        if (batch == null)
        {
            return null;
        }

        if (!SameCode(batch.OwnerCode, ownerCode) || !op.Has(batch.OwnerCode))
        {
            return PackingResult<PackingBatchCreateDto>.Fail(409, PackingErrorCodes.InvalidRequest, "这个请求编号已被使用");
        }

        var cartons = await LoadBatchCartonsAsync(batch.BatchGUID);
        return PackingResult<PackingBatchCreateDto>.Ok(await BuildBatchCreateAsync(batch, cartons, duplicate: true));
    }

    private async Task<List<PackCarton>> LoadBatchCartonsAsync(string batchGuid)
    {
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.BatchGUID == batchGuid && carton.Status != PackCartonStatuses.Voided)
            .ToListAsync();
        return cartons.OrderBy(carton => carton.BatchSeq ?? int.MaxValue).ThenBy(carton => carton.CartonNo, StringComparer.Ordinal).ToList();
    }

    private async Task<PackingBatchCreateDto> BuildBatchCreateAsync(PackCartonBatch batch, List<PackCarton> cartons, bool duplicate)
    {
        return new PackingBatchCreateDto
        {
            BatchGuid = batch.BatchGUID,
            Duplicate = duplicate,
            Count = batch.Count,
            FirstCartonNo = batch.FirstCartonNo,
            LastCartonNo = batch.LastCartonNo,
            CreatedAtUtc = batch.CreatedAtUtc,
            Cartons = await BuildCartonSummariesAsync(cartons),
        };
    }

    // ---- 批次列表 / 详情 ----

    public async Task<PackingResult<PackingBatchListDto>> ListBatchesAsync(PackingOperator op, string? ownerCode, int? limit)
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingBatchListDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var code = owner.Code!;
        var take = Math.Clamp(limit ?? DefaultBatchListLimit, 1, MaxBatchListLimit);
        var batches = await _db.Queryable<PackCartonBatch>()
            .Where(batch => batch.OwnerCode == code)
            .OrderBy(batch => batch.CreatedAtUtc, OrderByType.Desc)
            .Take(take)
            .ToListAsync();
        return PackingResult<PackingBatchListDto>.Ok(new PackingBatchListDto { Batches = await BuildBatchSummariesAsync(batches) });
    }

    public async Task<PackingResult<PackingBatchDetailDto>> GetBatchAsync(PackingOperator op, string batchGuid)
    {
        var batch = await _db.Queryable<PackCartonBatch>().Where(item => item.BatchGUID == batchGuid).FirstAsync();
        if (batch == null || !op.Has(batch.OwnerCode))
        {
            // 不属于你地点的批次按「找不到」处理，不暴露它存在。
            return PackingResult<PackingBatchDetailDto>.Fail(404, PackingErrorCodes.BatchNotFound, "找不到该批次");
        }

        var cartons = await LoadBatchCartonsAsync(batch.BatchGUID);
        return PackingResult<PackingBatchDetailDto>.Ok(new PackingBatchDetailDto
        {
            Batch = (await BuildBatchSummariesAsync(new List<PackCartonBatch> { batch }))[0],
            Cartons = await BuildCartonSummariesAsync(cartons),
        });
    }

    /// <summary>批次汇总：已打印 / 未启用只算非作废的箱，作废的单独计。</summary>
    private async Task<List<PackingBatchSummaryDto>> BuildBatchSummariesAsync(List<PackCartonBatch> batches)
    {
        if (batches.Count == 0)
        {
            return new List<PackingBatchSummaryDto>();
        }

        var batchGuids = batches.Select(batch => batch.BatchGUID).ToList();
        var rows = await _db.Queryable<PackCarton>()
            .Where(carton => carton.BatchGUID != null && batchGuids.Contains(carton.BatchGUID))
            .Select(carton => new { carton.BatchGUID, carton.Status, carton.ActivatedAtUtc, carton.LabelPrintCount })
            .ToListAsync();
        var byBatch = rows.ToLookup(row => row.BatchGUID!);
        return batches.Select(batch =>
        {
            var cartons = byBatch[batch.BatchGUID].ToList();
            var alive = cartons.Where(carton => carton.Status != PackCartonStatuses.Voided).ToList();
            return new PackingBatchSummaryDto
            {
                BatchGuid = batch.BatchGUID,
                Count = batch.Count,
                FirstCartonNo = batch.FirstCartonNo,
                LastCartonNo = batch.LastCartonNo,
                CreatedAtUtc = batch.CreatedAtUtc,
                CreatedByName = batch.CreatedByName,
                PrintedCount = alive.Count(carton => (carton.LabelPrintCount ?? 0) > 0),
                UnactivatedCount = alive.Count(carton => carton.ActivatedAtUtc == null),
                VoidedCount = cartons.Count - alive.Count,
            };
        }).ToList();
    }

    // ---- 回报标签已打印 ----

    public async Task<PackingResult<PackingLabelsPrintedDto>> MarkLabelsPrintedAsync(PackingOperator op, IReadOnlyList<string>? cartonGuids)
    {
        var guids = NormalizeGuidList(cartonGuids);
        if (guids == null)
        {
            return PackingResult<PackingLabelsPrintedDto>.Fail(400, PackingErrorCodes.InvalidRequest, $"请选择 1–{MaxBatchGuids} 只箱");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var result = new PackingLabelsPrintedDto();
            var now = UtcNow();
            // 按编号排序逐只加锁：多台设备同时回报同一批箱也不会互相等死。
            foreach (var guid in guids.OrderBy(guid => guid, StringComparer.Ordinal))
            {
                var carton = await LockCartonAsync(guid);
                if (carton == null || !op.Has(carton.OwnerCode))
                {
                    result.Skipped.Add(new PackingSkippedCartonDto { CartonGuid = guid, Reason = PackingErrorCodes.SkipNotFound });
                    continue;
                }

                if (carton.Status == PackCartonStatuses.Voided)
                {
                    result.Skipped.Add(new PackingSkippedCartonDto { CartonGuid = guid, Reason = PackingErrorCodes.SkipVoided });
                    continue;
                }

                // 打印次数 +1；不动 UpdatedAtUtc，免得「回报已打印」把列表顺序刷乱。
                var count = (carton.LabelPrintCount ?? 0) + 1;
                await _db.Updateable<PackCarton>()
                    .SetColumns(item => new PackCarton { LabelPrintCount = count, LabelPrintedAtUtc = now })
                    .Where(item => item.CartonGUID == carton.CartonGUID)
                    .ExecuteCommandAsync();
                result.Updated++;
            }

            return PackingResult<PackingLabelsPrintedDto>.Ok(result);
        });
    }

    // ---- 启用 ----

    public async Task<PackingResult<PackingCartonDetailDto>> ActivateCartonAsync(PackingOperator op, string cartonGuid)
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null || !op.Has(carton.OwnerCode))
            {
                return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            // 已封箱、已发出、已作废的箱都不是「装箱中」，不能启用。
            if (carton.Status != PackCartonStatuses.Packing)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotPacking, "该箱当前不是装箱中，不能启用");
            }

            // 普通箱（没有批次）、已启用的空箱标：幂等，直接返回当前详情。
            if (IsUnactivatedBlank(carton))
            {
                var now = UtcNow();
                carton.ActivatedAtUtc = now;
                carton.UpdatedAtUtc = now;
                await _db.Updateable(carton).ExecuteCommandAsync();
                await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.HeaderChanged, "启用空箱标", op, now);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    // ---- 作废未用空箱标 ----

    public async Task<PackingResult<PackingVoidBlankDto>> VoidBlankCartonsAsync(PackingOperator op, IReadOnlyList<string>? cartonGuids)
    {
        var guids = NormalizeGuidList(cartonGuids);
        if (guids == null)
        {
            return PackingResult<PackingVoidBlankDto>.Fail(400, PackingErrorCodes.InvalidRequest, $"请选择 1–{MaxBatchGuids} 只箱");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var result = new PackingVoidBlankDto();
            foreach (var guid in guids.OrderBy(guid => guid, StringComparer.Ordinal))
            {
                var carton = await LockCartonAsync(guid);
                if (carton == null || !op.Has(carton.OwnerCode) || carton.Status == PackCartonStatuses.Voided)
                {
                    result.Skipped.Add(new PackingSkippedCartonDto { CartonGuid = guid, Reason = PackingErrorCodes.SkipNotFound });
                    continue;
                }

                // 只作废「未启用的空箱标」：有批次、没启用、装箱中、没有商品。普通箱、已启用 / 已封 / 已发出的箱一律跳过。
                var blank = IsUnactivatedBlank(carton)
                    && carton.Status == PackCartonStatuses.Packing
                    && !await _db.Queryable<PackCartonItem>().Where(item => item.CartonGUID == carton.CartonGUID).AnyAsync();
                if (!blank)
                {
                    result.Skipped.Add(new PackingSkippedCartonDto { CartonGuid = guid, Reason = PackingErrorCodes.SkipNotBlank });
                    continue;
                }

                var now = UtcNow();
                carton.Status = PackCartonStatuses.Voided;
                carton.UpdatedAtUtc = now;
                await _db.Updateable(carton).ExecuteCommandAsync();
                await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Voided, "作废未用空箱标", op, now);
                result.Voided.Add(guid);
            }

            return PackingResult<PackingVoidBlankDto>.Ok(result);
        });
    }

    // ---- 拣货端关联空箱标到订货单 ----

    public async Task<PackingResult<PackingCartonDetailDto>> AttachBlankCartonToOrderAsync(PackingOperator op, string orderGuid, string? cartonRef)
    {
        var reference = cartonRef?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "缺少箱号");
        }

        // 空箱标都在总仓：这条路径只给总仓拣货人用。
        if (!op.Has(PackOwners.Warehouse))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        var order = string.IsNullOrWhiteSpace(orderGuid) ? null : await LoadOrderAsync(orderGuid.Trim());
        if (order == null)
        {
            return CartonFail(404, PackingErrorCodes.OrderNotFound, "找不到该订货单");
        }

        // 订货单没有单独指定去向时默认发往订货单的分店（与按订货单建箱同一规则、同一套校验）。
        PackingDestinationRequestDto? defaultDestination = string.IsNullOrWhiteSpace(order.StoreCode)
            ? null
            : new PackingDestinationRequestDto { DestinationType = PackDestinationTypes.Store, DestinationCode = order.StoreCode };
        var resolvedDefault = defaultDestination == null ? null : await ResolveDestinationAsync(PackOwners.Warehouse, defaultDestination);

        // 箱号（BX…，大写存储）或箱 GUID（小写十六进制存储）：忽略大小写。
        var byNo = reference.ToUpperInvariant();
        var byGuid = reference.ToLowerInvariant();
        var candidateGuid = await _db.Queryable<PackCarton>()
            .Where(carton => carton.CartonNo == byNo || carton.CartonGUID == byGuid)
            .Select(carton => carton.CartonGUID)
            .FirstAsync();
        if (string.IsNullOrEmpty(candidateGuid))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(candidateGuid);
            // 不存在、别的地点持有、已作废：统一同一句话，不暴露箱的存在。
            if (carton == null || !PackOwners.IsWarehouse(carton.OwnerCode) || carton.Status == PackCartonStatuses.Voided)
            {
                return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            // 必须是「未启用、没关联订单、没商品、装箱中」的空箱标；已启用 / 已关联别的订单 / 已封 / 在途都拒绝。
            var hasItems = await _db.Queryable<PackCartonItem>().Where(item => item.CartonGUID == carton.CartonGUID).AnyAsync();
            if (!IsUnactivatedBlank(carton)
                || carton.Status != PackCartonStatuses.Packing
                || carton.OrderGUID != null
                || hasItems)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotBlank, "这只箱已启用或已被使用，不是空箱标");
            }

            var now = UtcNow();
            if (carton.DestinationType == PackDestinationTypes.Unassigned && resolvedDefault != null)
            {
                if (resolvedDefault.Error != null)
                {
                    return CartonFail(400, PackingErrorCodes.DestinationInvalid, resolvedDefault.Error);
                }

                carton.DestinationType = resolvedDefault.Type;
                carton.DestinationCode = resolvedDefault.Code;
                carton.CarrierGUID = resolvedDefault.CarrierGuid;
            }

            carton.OrderGUID = order.OrderGUID;
            carton.ActivatedAtUtc = now;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(
                PackTargetTypes.Carton,
                carton.CartonGUID,
                PackEventActions.HeaderChanged,
                $"空箱标关联订货单 {Truncate(order.OrderNo, 40) ?? order.OrderGUID}",
                op,
                now
            );
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    /// <summary>清洗 GUID 列表：去空白、去重；空列表或超过上限返回 null（调用方按 400 处理）。</summary>
    private static List<string>? NormalizeGuidList(IReadOnlyList<string>? cartonGuids)
    {
        var guids = (cartonGuids ?? Array.Empty<string>())
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return guids.Count is >= 1 and <= MaxBatchGuids ? guids : null;
    }
}
