using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePicking;

public interface IWarehousePickingService
{
    /// <summary>可拣订单列表；pickerUserGuid 为当前拣货人，为空时不算“派给我”的数量，mine 筛选返回空列表。</summary>
    Task<WarehousePickingResult<WarehousePickingOrderListDto>> ListOrdersAsync(
        string? filter,
        string? keyword,
        string? pickerUserGuid = null
    );

    Task<WarehousePickingResult<WarehousePickingOrderResolveDto>> ResolveOrderAsync(string? code);

    Task<WarehousePickingResult<WarehousePickingSheetDto>> JoinAsync(string orderGuid, WarehousePickerContext picker);

    Task<WarehousePickingResult<WarehousePickingSheetDto>> GetSheetAsync(string orderGuid);

    Task<WarehousePickingResult<WarehousePickingProgressDto>> GetProgressAsync(string orderGuid);

    Task<WarehousePickingResult<WarehousePickingLineMutationDto>> AppendRecordAsync(
        string orderGuid,
        WarehousePickingRecordRequestDto request,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingLineMutationDto>> SetLineTotalAsync(
        string orderGuid,
        string detailGuid,
        WarehousePickingSetTotalRequestDto request,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingLineMutationDto>> MarkStockoutAsync(
        string orderGuid,
        string detailGuid,
        int reason,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingLineMutationDto>> ClearStockoutAsync(
        string orderGuid,
        string detailGuid,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>> SetMinOrderQuantityAsync(
        string orderGuid,
        string detailGuid,
        int minOrderQuantity,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingSubmitResultDto>> SubmitAsync(
        string orderGuid,
        WarehousePickerContext picker
    );

    Task<WarehousePickingResult<WarehousePickingCodeLookupDto>> LookupCodeAsync(string? code);
}

/// <summary>
/// 仓库订单拣货：拣货记录只追加，行合计 = 记录求和；提交时把合计写入订单行配货数（AllocQuantity）。
/// 同一订单的所有写入先对拣货会话行加更新锁再执行，多人一起拣时串行化，
/// 保证“先读合计再写记录”（手动改总数、减一个中包、提交）在并发下不丢更新。
/// </summary>
internal sealed class WarehousePickingService(
    SqlSugarContext context,
    IProductWarehouseReactService productWarehouseService,
    ILogger<WarehousePickingService> logger
) : IWarehousePickingService
{
    private const int MaxOrderListItems = 200;
    private const int MaxPickPieces = 100000;
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<WarehousePickingResult<WarehousePickingOrderListDto>> ListOrdersAsync(
        string? filter,
        string? keyword,
        string? pickerUserGuid = null
    )
    {
        var normalizedKeyword = keyword?.Trim();
        var normalizedFilter = (filter ?? "all").Trim();

        // HQ 同步回来的未完结订单都映射为已提交，可拣订单可能很多：计数单独分组，明细只取最新 200 张。
        var statusCounts = await PickableOrderQuery(normalizedKeyword)
            .GroupBy((order, session) => order.FlowStatus)
            .Select((order, session) => new FlowStatusCountRow
            {
                FlowStatus = order.FlowStatus,
                Count = SqlFunc.AggregateCount(order.OrderGUID),
            })
            .ToListAsync();
        var toPickCount = statusCounts
            .Where(row => row.FlowStatus == WarehousePickingRules.FlowStatusSubmitted)
            .Sum(row => row.Count);
        var pickingCount = statusCounts
            .Where(row => row.FlowStatus == WarehousePickingRules.FlowStatusPicking)
            .Sum(row => row.Count);
        var normalizedPicker = string.IsNullOrWhiteSpace(pickerUserGuid) ? null : pickerUserGuid.Trim();
        var counts = new WarehousePickingOrderCountsDto
        {
            All = toPickCount + pickingCount,
            ToPick = toPickCount,
            Picking = pickingCount,
            Mine = normalizedPicker == null
                ? null
                : await PickableOrderQuery(normalizedKeyword)
                    .Where((order, session) => SqlFunc.Subqueryable<WarehouseOrderPickAssignment>()
                        .Where(assignment => assignment.OrderGUID == order.OrderGUID && assignment.PickerUserGuid == normalizedPicker)
                        .Any())
                    .CountAsync(),
        };

        var itemQuery = PickableOrderQuery(normalizedKeyword);
        if (normalizedFilter == "toPick")
        {
            itemQuery = itemQuery.Where((order, session) => order.FlowStatus == WarehousePickingRules.FlowStatusSubmitted);
        }
        else if (normalizedFilter == "picking")
        {
            itemQuery = itemQuery.Where((order, session) => order.FlowStatus == WarehousePickingRules.FlowStatusPicking);
        }
        else if (normalizedFilter == "mine")
        {
            // 派给我的：经理把本单至少一行派给了当前拣货人；认不出拣货人时为空列表。
            var picker = normalizedPicker ?? string.Empty;
            itemQuery = itemQuery.Where((order, session) => SqlFunc.Subqueryable<WarehouseOrderPickAssignment>()
                .Where(assignment => assignment.OrderGUID == order.OrderGUID && assignment.PickerUserGuid == picker)
                .Any());
        }

        // 与 Web 仓库订单列表默认排序一致：下单时间新的在前。
        var page = await itemQuery
            .OrderBy((order, session) => order.OrderDate, OrderByType.Desc)
            .OrderBy((order, session) => order.OrderNo, OrderByType.Desc)
            .Take(MaxOrderListItems)
            .Select((order, session) => new OrderListRow
            {
                OrderGUID = order.OrderGUID,
                OrderNo = order.OrderNo,
                StoreCode = order.StoreCode,
                OrderDate = order.OrderDate,
                FlowStatus = order.FlowStatus,
                SessionStatus = session.Status,
                StoreName = SqlFunc.Subqueryable<Store>()
                    .Where(store => store.StoreCode == order.StoreCode || store.StoreGUID == order.StoreCode)
                    .Select(store => store.StoreName),
                LineCount = SqlFunc.Subqueryable<WareHouseOrderDetails>()
                    .Where(detail => detail.OrderGUID == order.OrderGUID && !detail.IsDeleted)
                    .Count(),
                TotalQuantity = SqlFunc.Subqueryable<WareHouseOrderDetails>()
                    .Where(detail => detail.OrderGUID == order.OrderGUID && !detail.IsDeleted)
                    .Sum(detail => detail.Quantity),
            })
            .ToListAsync();

        var pickingGuids = page
            .Where(row => row.FlowStatus == WarehousePickingRules.FlowStatusPicking)
            .Select(row => row.OrderGUID)
            .ToList();
        var pickedLineCounts = await CountPickedLinesAsync(pickingGuids);
        var pickers = await LoadPickersAsync(pickingGuids);
        var assignees = await WarehousePickingQueries.LoadAssigneesAsync(_db, page.Select(row => row.OrderGUID).ToList());

        var items = page.Select(row => new WarehousePickingOrderListItemDto
        {
            OrderGuid = row.OrderGUID,
            OrderNo = row.OrderNo,
            StoreCode = row.StoreCode,
            StoreName = row.StoreName,
            OrderDate = row.OrderDate,
            FlowStatus = row.FlowStatus ?? 0,
            LineCount = row.LineCount,
            TotalQuantity = row.TotalQuantity ?? 0,
            PickedLineCount = pickedLineCounts.GetValueOrDefault(row.OrderGUID),
            SessionStatus = row.SessionStatus,
            Pickers = pickers.GetValueOrDefault(row.OrderGUID) ?? new List<WarehousePickerRefDto>(),
            Assignees = assignees.GetValueOrDefault(row.OrderGUID) ?? new List<WarehousePickingAssigneeDto>(),
        }).ToList();

        return WarehousePickingResult<WarehousePickingOrderListDto>.Ok(
            new WarehousePickingOrderListDto { Items = items, Counts = counts }
        );
    }

    public async Task<WarehousePickingResult<WarehousePickingOrderResolveDto>> ResolveOrderAsync(string? code)
    {
        var orderNo = WarehousePickingRules.ParseOrderCode(code);
        if (orderNo == null)
        {
            return WarehousePickingResult<WarehousePickingOrderResolveDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "订单号不能为空"
            );
        }

        // 订单号没有数据库唯一约束：同号多单时取仍可拣货的那一张，仍有多张则让用户改用列表选择。
        var candidates = await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && (order.OrderNo == orderNo || order.OrderGUID == orderNo))
            .Select(order => new { order.OrderGUID, order.FlowStatus })
            .ToListAsync();
        var pickable = candidates.Where(order => WarehousePickingRules.IsPickable(order.FlowStatus)).ToList();
        if (candidates.Count == 0)
        {
            return WarehousePickingResult<WarehousePickingOrderResolveDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到该订单"
            );
        }

        if (pickable.Count != 1)
        {
            return WarehousePickingResult<WarehousePickingOrderResolveDto>.Fail(
                409,
                WarehousePickingErrorCodes.OrderNotPickable,
                pickable.Count == 0 ? "该订单当前不能拣货" : "订单号对应多张订单，请在列表中选择"
            );
        }

        return WarehousePickingResult<WarehousePickingOrderResolveDto>.Ok(
            new WarehousePickingOrderResolveDto { OrderGuid = pickable[0].OrderGUID }
        );
    }

    public async Task<WarehousePickingResult<WarehousePickingSheetDto>> JoinAsync(
        string orderGuid,
        WarehousePickerContext picker
    )
    {
        var joinResult = await ExecuteInTransactionAsync(async () =>
        {
            var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
            if (order == null)
            {
                return WarehousePickingResult<bool>.Fail(404, WarehousePickingErrorCodes.OrderNotFound, "找不到该订单");
            }

            if (!WarehousePickingRules.IsPickable(order.FlowStatus))
            {
                return WarehousePickingResult<bool>.Fail(409, WarehousePickingErrorCodes.OrderNotPickable, "该订单当前不能拣货");
            }

            var now = DateTime.UtcNow;
            // 会话行不存在时 UPDLOCK+HOLDLOCK 锁住键范围，并发加入的第二个人等待后看到已建好的会话。
            var session = await SessionQuery(orderGuid, holdRange: true).FirstAsync();
            if (session == null)
            {
                session = new WarehouseOrderPickSession
                {
                    OrderGUID = orderGuid,
                    Status = WarehouseOrderPickSessionStatuses.Picking,
                    StartedAtUtc = now,
                    StartedByUserGuid = picker.UserGuid,
                    StartedByName = picker.Name,
                    UpdatedAtUtc = now,
                };
                await _db.Insertable(session).ExecuteCommandAsync();
            }
            else if (session.Status == WarehouseOrderPickSessionStatuses.Submitted)
            {
                // 已提交的订单只读：返回拣货单让界面展示“已由谁提交”，不再登记参与人。
                return WarehousePickingResult<bool>.Ok(true);
            }

            if (order.FlowStatus == WarehousePickingRules.FlowStatusSubmitted)
            {
                // 与 Web 打印配货单时的开始配货同一语义：比较交换 1→3，已被他人改过则保持现状。
                var startedAt = DateTime.Now;
                var startedBy = picker.Name;
                await _db.Updateable<WareHouseOrder>()
                    .SetColumns(item => new WareHouseOrder
                    {
                        FlowStatus = WarehousePickingRules.FlowStatusPicking,
                        UpdatedAt = startedAt,
                        UpdatedBy = startedBy,
                    })
                    .Where(item =>
                        item.OrderGUID == orderGuid
                        && item.FlowStatus == WarehousePickingRules.FlowStatusSubmitted
                        && !item.IsDeleted
                    )
                    .ExecuteCommandAsync();
            }

            await TouchParticipantAsync(orderGuid, picker, detailGuid: null, now);
            return WarehousePickingResult<bool>.Ok(true);
        });

        if (!joinResult.Success)
        {
            return WarehousePickingResult<WarehousePickingSheetDto>.Fail(
                joinResult.StatusCode,
                joinResult.ErrorCode!,
                joinResult.Message!
            );
        }

        return await GetSheetAsync(orderGuid);
    }

    public async Task<WarehousePickingResult<WarehousePickingSheetDto>> GetSheetAsync(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<WarehousePickingSheetDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到该订单"
            );
        }

        var session = await WarehousePickingQueries.LoadSessionAsync(_db, orderGuid);
        var sheet = await WarehousePickingQueries.BuildSheetAsync(_db, order, session);
        return WarehousePickingResult<WarehousePickingSheetDto>.Ok(sheet);
    }

    public async Task<WarehousePickingResult<WarehousePickingProgressDto>> GetProgressAsync(string orderGuid)
    {
        var session = await WarehousePickingQueries.LoadSessionAsync(_db, orderGuid);
        if (session == null)
        {
            return WarehousePickingResult<WarehousePickingProgressDto>.Fail(
                409,
                WarehousePickingErrorCodes.SessionNotStarted,
                "该订单还没有开始拣货"
            );
        }

        return WarehousePickingResult<WarehousePickingProgressDto>.Ok(
            await WarehousePickingQueries.BuildProgressAsync(_db, orderGuid, session)
        );
    }

    public Task<WarehousePickingResult<WarehousePickingLineMutationDto>> AppendRecordAsync(
        string orderGuid,
        WarehousePickingRecordRequestDto request,
        WarehousePickerContext picker
    )
    {
        var detailGuid = request.DetailGuid?.Trim();
        if (string.IsNullOrEmpty(detailGuid) || request.ClientRequestId == Guid.Empty)
        {
            return Task.FromResult(WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "缺少订单行或请求编号"
            ));
        }

        return ExecuteInTransactionAsync(async () =>
        {
            var gate = await EnterWritableSessionAsync<WarehousePickingLineMutationDto>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var duplicate = await FindDuplicateAsync(orderGuid, request.ClientRequestId);
            if (duplicate != null)
            {
                return duplicate;
            }

            var line = await LoadLineAsync(orderGuid, detailGuid);
            if (line == null)
            {
                return LineNotFound<WarehousePickingLineMutationDto>();
            }

            var current = await WarehousePickingQueries.BuildLineProgressAsync(_db, orderGuid, detailGuid, line.MinOrderQuantity);
            var delta = WarehousePickingRules.ResolveDelta(
                request.Source,
                request.Pieces,
                line.MinOrderQuantity,
                current.PickedTotal
            );
            if (!delta.IsValid)
            {
                return WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                    delta.ErrorCode == WarehousePickingErrorCodes.InvalidRequest ? 400 : 409,
                    delta.ErrorCode!,
                    delta.Message!,
                    new WarehousePickingLineMutationDto { Line = current }
                );
            }

            var isScan = request.Source == WarehouseOrderPickSources.Scan;
            return await AppendAndReportAsync(
                orderGuid,
                line,
                picker,
                delta.Delta!.Value,
                request.Source,
                request.ClientRequestId,
                isScan ? Truncate(request.ScannedCode, 100) : null,
                isScan ? request.MatchedBy : null
            );
        });
    }

    public Task<WarehousePickingResult<WarehousePickingLineMutationDto>> SetLineTotalAsync(
        string orderGuid,
        string detailGuid,
        WarehousePickingSetTotalRequestDto request,
        WarehousePickerContext picker
    )
    {
        var normalizedDetailGuid = detailGuid?.Trim();
        if (string.IsNullOrEmpty(normalizedDetailGuid)
            || request.ClientRequestId == Guid.Empty
            || request.Total < 0
            || request.Total > MaxPickPieces)
        {
            return Task.FromResult(WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "数量必须为 0 到 100000 之间的整数"
            ));
        }

        return ExecuteInTransactionAsync(async () =>
        {
            var gate = await EnterWritableSessionAsync<WarehousePickingLineMutationDto>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var duplicate = await FindDuplicateAsync(orderGuid, request.ClientRequestId);
            if (duplicate != null)
            {
                return duplicate;
            }

            var line = await LoadLineAsync(orderGuid, normalizedDetailGuid);
            if (line == null)
            {
                return LineNotFound<WarehousePickingLineMutationDto>();
            }

            var current = await WarehousePickingQueries.BuildLineProgressAsync(_db, orderGuid, normalizedDetailGuid, line.MinOrderQuantity);
            if (current.PickedTotal != request.ExpectedTotal)
            {
                // 别人刚在同一行扫过：按客户端看到的旧合计写差额会算错，返回最新合计让拣货员确认后重试。
                return WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.PickedTotalChanged,
                    "这一行的已拣数量刚被更新，请确认后重试",
                    new WarehousePickingLineMutationDto { Line = current }
                );
            }

            var delta = request.Total - current.PickedTotal;
            if (delta == 0)
            {
                return WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(
                    new WarehousePickingLineMutationDto { Line = current, AppliedDelta = 0 }
                );
            }

            return await AppendAndReportAsync(
                orderGuid,
                line,
                picker,
                delta,
                WarehouseOrderPickSources.SetTotal,
                request.ClientRequestId,
                scannedCode: null,
                matchedBy: null
            );
        });
    }

    /// <summary>
    /// 标记“货位没货”：已拣的保留，剩余数量记为拣不到的原因；不写拣货记录、不改合计。
    /// 同一行再次标记覆盖原因与标记人；已拣齐（含超拣）的行不需要标记。
    /// </summary>
    public Task<WarehousePickingResult<WarehousePickingLineMutationDto>> MarkStockoutAsync(
        string orderGuid,
        string detailGuid,
        int reason,
        WarehousePickerContext picker
    )
    {
        var normalizedDetailGuid = detailGuid?.Trim();
        if (string.IsNullOrEmpty(normalizedDetailGuid) || !WarehouseOrderPickStockoutReasons.IsKnown(reason))
        {
            return Task.FromResult(WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "缺少订单行或没货原因无效"
            ));
        }

        return ExecuteInTransactionAsync(async () =>
        {
            var gate = await EnterWritableSessionAsync<WarehousePickingLineMutationDto>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var line = await LoadLineAsync(orderGuid, normalizedDetailGuid);
            if (line == null)
            {
                return LineNotFound<WarehousePickingLineMutationDto>();
            }

            var current = await WarehousePickingQueries.BuildLineProgressAsync(_db, orderGuid, normalizedDetailGuid, line.MinOrderQuantity);
            if (current.PickedTotal >= (line.Quantity ?? 0))
            {
                return WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.LineAlreadyComplete,
                    "这一行已经拣齐，不需要标记没货",
                    new WarehousePickingLineMutationDto { Line = current }
                );
            }

            var now = DateTime.UtcNow;
            var stockout = new WarehouseOrderPickStockout
            {
                OrderGUID = orderGuid,
                DetailGUID = line.DetailGUID,
                ProductCode = line.ProductCode ?? string.Empty,
                LocationCode = Truncate(await WarehousePickingQueries.LoadPickLocationTextAsync(_db, line.ProductCode), 200),
                Reason = reason,
                PickedAtMark = current.PickedTotal,
                MarkedByUserGuid = picker.UserGuid,
                MarkedByName = picker.Name,
                MarkedAtUtc = now,
                ClearedAtUtc = null,
                ClearedByName = null,
            };
            // 会话行已加更新锁，同一订单的写入在此串行：先查再插 / 覆盖不会并发撞主键。
            var exists = await _db.Queryable<WarehouseOrderPickStockout>()
                .Where(item => item.OrderGUID == orderGuid && item.DetailGUID == line.DetailGUID)
                .AnyAsync();
            if (exists)
            {
                await _db.Updateable(stockout).ExecuteCommandAsync();
            }
            else
            {
                await _db.Insertable(stockout).ExecuteCommandAsync();
            }

            await TouchParticipantAsync(orderGuid, picker, line.DetailGUID, now);
            logger.LogInformation(
                "拣货标记货位没货: Order={OrderGuid}, Detail={DetailGuid}, Reason={Reason}, Picked={Picked}, Picker={PickerUserGuid}",
                orderGuid,
                line.DetailGUID,
                reason,
                current.PickedTotal,
                picker.UserGuid
            );
            return WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(
                new WarehousePickingLineMutationDto
                {
                    Line = await WarehousePickingQueries.BuildLineProgressAsync(_db, orderGuid, line.DetailGUID, line.MinOrderQuantity),
                }
            );
        });
    }

    /// <summary>撤销“货位没货”标记；没有有效标记时直接返回当前行，重复撤销是幂等的。</summary>
    public Task<WarehousePickingResult<WarehousePickingLineMutationDto>> ClearStockoutAsync(
        string orderGuid,
        string detailGuid,
        WarehousePickerContext picker
    )
    {
        var normalizedDetailGuid = detailGuid?.Trim();
        if (string.IsNullOrEmpty(normalizedDetailGuid))
        {
            return Task.FromResult(WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "缺少订单行"
            ));
        }

        return ExecuteInTransactionAsync(async () =>
        {
            var gate = await EnterWritableSessionAsync<WarehousePickingLineMutationDto>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var line = await LoadLineAsync(orderGuid, normalizedDetailGuid);
            if (line == null)
            {
                return LineNotFound<WarehousePickingLineMutationDto>();
            }

            var now = DateTime.UtcNow;
            await ClearActiveStockoutAsync(orderGuid, line.DetailGUID, picker.Name, now);
            await TouchParticipantAsync(orderGuid, picker, line.DetailGUID, now);
            return WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(
                new WarehousePickingLineMutationDto
                {
                    Line = await WarehousePickingQueries.BuildLineProgressAsync(_db, orderGuid, line.DetailGUID, line.MinOrderQuantity),
                }
            );
        });
    }

    public async Task<WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>> SetMinOrderQuantityAsync(
        string orderGuid,
        string detailGuid,
        int minOrderQuantity,
        WarehousePickerContext picker
    )
    {
        if (minOrderQuantity is <= 0 or > MaxPickPieces)
        {
            return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Fail(
                400,
                WarehousePickingErrorCodes.MinOrderQuantityInvalid,
                "中包数必须为正整数"
            );
        }

        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null || !WarehousePickingRules.IsPickable(order.FlowStatus))
        {
            return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Fail(
                409,
                WarehousePickingErrorCodes.OrderNotPickable,
                "该订单当前不能拣货"
            );
        }

        var line = await LoadLineAsync(orderGuid, detailGuid.Trim());
        if (line == null || string.IsNullOrWhiteSpace(line.ProductCode))
        {
            return LineNotFound<WarehousePickingMinOrderQuantityResultDto>();
        }

        // 中包数同时是分店订货的最小数量与步长：拣货员只能补录缺失值，改已有值须仓库经理或管理员。
        if (WarehousePickingRules.HasMinOrderQuantity(line.MinOrderQuantity) && !picker.CanOverwriteMinOrderQuantity)
        {
            return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Fail(
                409,
                WarehousePickingErrorCodes.MinOrderQuantityAlreadySet,
                "该商品已有中包数，如需修改请联系仓库经理",
                new WarehousePickingMinOrderQuantityResultDto
                {
                    ProductCode = line.ProductCode,
                    MinOrderQuantity = line.MinOrderQuantity!.Value,
                }
            );
        }

        // 复用仓库商品中包数列的窄列 PATCH：写 WarehouseProduct.MinOrderQuantity，
        // 联动有效国内商品中包数并记商品修改历史；它自带事务，不能放进拣货写入事务里。
        var patched = await productWarehouseService.PatchAsync(
            line.ProductCode,
            new WarehouseProductPatchDto { MinOrderQuantity = minOrderQuantity },
            picker.Name
        );
        if (patched == null)
        {
            return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Fail(
                404,
                WarehousePickingErrorCodes.LineNotFound,
                "仓库商品不存在，无法设置中包数"
            );
        }

        if (!patched.Success)
        {
            return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Fail(
                400,
                WarehousePickingErrorCodes.MinOrderQuantityInvalid,
                string.IsNullOrWhiteSpace(patched.Message) ? "设置中包数失败" : patched.Message
            );
        }

        logger.LogInformation(
            "拣货补录中包数: Order={OrderGuid}, Product={ProductCode}, Value={Value}, Picker={PickerUserGuid}",
            orderGuid,
            line.ProductCode,
            minOrderQuantity,
            picker.UserGuid
        );
        return WarehousePickingResult<WarehousePickingMinOrderQuantityResultDto>.Ok(
            new WarehousePickingMinOrderQuantityResultDto
            {
                ProductCode = line.ProductCode,
                MinOrderQuantity = minOrderQuantity,
            }
        );
    }

    public Task<WarehousePickingResult<WarehousePickingSubmitResultDto>> SubmitAsync(
        string orderGuid,
        WarehousePickerContext picker
    )
    {
        return ExecuteInTransactionAsync(async () =>
        {
            var session = await SessionQuery(orderGuid, holdRange: false).FirstAsync();
            if (session == null)
            {
                return WarehousePickingResult<WarehousePickingSubmitResultDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.SessionNotStarted,
                    "该订单还没有开始拣货"
                );
            }

            if (session.Status == WarehouseOrderPickSessionStatuses.Submitted)
            {
                return WarehousePickingResult<WarehousePickingSubmitResultDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.SessionSubmitted,
                    $"该订单已由 {session.SubmittedByName} 提交",
                    new WarehousePickingSubmitResultDto
                    {
                        SubmittedAtUtc = session.SubmittedAtUtc ?? session.UpdatedAtUtc,
                        SubmittedByName = session.SubmittedByName ?? string.Empty,
                    }
                );
            }

            var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
            if (order == null || !WarehousePickingRules.IsPickable(order.FlowStatus))
            {
                return WarehousePickingResult<WarehousePickingSubmitResultDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.OrderNotPickable,
                    "该订单当前不能提交拣货"
                );
            }

            var details = await _db.Queryable<WareHouseOrderDetails>()
                .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
                .ToListAsync();
            var totals = (await WarehousePickingQueries.LoadPickTotalsAsync(_db, orderGuid))
                .GroupBy(total => total.DetailGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(total => total.Quantity), StringComparer.OrdinalIgnoreCase);

            // 订单行批量改配货数经审计拦截器落库的是 UTC；这里显式写 UTC 与拣货人，保持同一口径。
            var now = DateTime.UtcNow;
            foreach (var detail in details)
            {
                // 与订单行批量改配货数同口径：发货金额按配货数，订货金额按订货数，双零行软删除。
                var allocated = (decimal)Math.Max(0, totals.GetValueOrDefault(detail.DetailGUID));
                detail.AllocQuantity = allocated;
                detail.OEMAmount = allocated * (detail.OEMPrice ?? 0);
                detail.ImportAmount = (detail.Quantity ?? 0) * (detail.ImportPrice ?? 0);
                detail.UpdatedAt = now;
                detail.UpdatedBy = picker.Name;
                if (WarehousePickingRules.ShouldSoftDelete(detail.Quantity, allocated))
                {
                    detail.IsDeleted = true;
                }
            }

            if (details.Count > 0)
            {
                // 纯设备会话没有登录名，拦截器会把修改人改成 System；保留显式写入的拣货人。
                using var auditScope = SqlSugarAuditScope.PreserveExplicitAuditFields();
                // 只写配货相关列，不覆盖同一时刻 Web 端对进价、备注等其它列的修改。
                await _db.Updateable(details)
                    .UpdateColumns(detail => new
                    {
                        detail.AllocQuantity,
                        detail.OEMAmount,
                        detail.ImportAmount,
                        detail.UpdatedAt,
                        detail.UpdatedBy,
                        detail.IsDeleted,
                    })
                    .ExecuteCommandAsync();
            }

            await UpdateOrderTotalsAsync(orderGuid);

            var submittedAt = DateTime.UtcNow;
            await _db.Updateable<WarehouseOrderPickSession>()
                .SetColumns(item => new WarehouseOrderPickSession
                {
                    Status = WarehouseOrderPickSessionStatuses.Submitted,
                    SubmittedAtUtc = submittedAt,
                    SubmittedByUserGuid = picker.UserGuid,
                    SubmittedByName = picker.Name,
                    UpdatedAtUtc = submittedAt,
                })
                .Where(item => item.OrderGUID == orderGuid)
                .ExecuteCommandAsync();

            var (shortLines, overLines) = WarehousePickingRules.CountVariances(
                details.Select(detail => (detail.Quantity ?? 0, totals.GetValueOrDefault(detail.DetailGUID)))
            );
            logger.LogInformation(
                "拣货提交: Order={OrderGuid}, Lines={Lines}, Short={Short}, Over={Over}, Picker={PickerUserGuid}",
                orderGuid,
                details.Count,
                shortLines,
                overLines,
                picker.UserGuid
            );
            return WarehousePickingResult<WarehousePickingSubmitResultDto>.Ok(
                new WarehousePickingSubmitResultDto
                {
                    SubmittedAtUtc = submittedAt,
                    SubmittedByName = picker.Name,
                    LineCount = details.Count,
                    ShortLineCount = shortLines,
                    OverLineCount = overLines,
                }
            );
        });
    }

    public async Task<WarehousePickingResult<WarehousePickingCodeLookupDto>> LookupCodeAsync(string? code)
    {
        var normalized = code?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 100)
        {
            return WarehousePickingResult<WarehousePickingCodeLookupDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "条码不能为空"
            );
        }

        var product = await WarehousePickingQueries.LookupProductByCodeAsync(_db, normalized);
        return product == null
            ? WarehousePickingResult<WarehousePickingCodeLookupDto>.Fail(404, WarehousePickingErrorCodes.CodeNotFound, "找不到该条码对应的商品")
            : WarehousePickingResult<WarehousePickingCodeLookupDto>.Ok(product);
    }

    private ISugarQueryable<WareHouseOrder, WarehouseOrderPickSession> PickableOrderQuery(string? keyword)
    {
        var query = _db.Queryable<WareHouseOrder>()
            .LeftJoin<WarehouseOrderPickSession>((order, session) => order.OrderGUID == session.OrderGUID)
            .Where((order, session) =>
                !order.IsDeleted
                && (order.FlowStatus == WarehousePickingRules.FlowStatusSubmitted
                    || order.FlowStatus == WarehousePickingRules.FlowStatusPicking)
                // 已提交拣货的订单等待主管出库，不再出现在拣货列表里。
                && (session.OrderGUID == null || session.Status == WarehouseOrderPickSessionStatuses.Picking)
            );
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where((order, session) =>
                (order.OrderNo != null && order.OrderNo.Contains(keyword))
                || (order.StoreCode != null && order.StoreCode.Contains(keyword))
                || SqlFunc.Subqueryable<Store>()
                    .Where(store =>
                        (store.StoreCode == order.StoreCode || store.StoreGUID == order.StoreCode)
                        && store.StoreName != null
                        && store.StoreName.Contains(keyword)
                    )
                    .Any()
            );
        }

        return query;
    }

    private async Task<Dictionary<string, int>> CountPickedLinesAsync(List<string> orderGuids)
    {
        if (orderGuids.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var details = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID != null && orderGuids.Contains(detail.OrderGUID) && !detail.IsDeleted)
            .Select(detail => new { detail.OrderGUID, detail.DetailGUID, detail.Quantity })
            .ToListAsync();
        var totals = await _db.Queryable<WarehouseOrderPickRecord>()
            .Where(record => orderGuids.Contains(record.OrderGUID))
            .GroupBy(record => record.DetailGUID)
            .Select(record => new { record.DetailGUID, Quantity = SqlFunc.AggregateSum(record.QuantityDelta) })
            .ToListAsync();
        var totalByDetail = totals.ToDictionary(total => total.DetailGUID, total => total.Quantity, StringComparer.OrdinalIgnoreCase);

        return details
            .Where(detail => detail.OrderGUID != null)
            .GroupBy(detail => detail.OrderGUID!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(detail => totalByDetail.GetValueOrDefault(detail.DetailGUID) >= (detail.Quantity ?? 0)
                    && totalByDetail.ContainsKey(detail.DetailGUID)),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private async Task<Dictionary<string, List<WarehousePickerRefDto>>> LoadPickersAsync(List<string> orderGuids)
    {
        if (orderGuids.Count == 0)
        {
            return new Dictionary<string, List<WarehousePickerRefDto>>(StringComparer.OrdinalIgnoreCase);
        }

        var participants = await _db.Queryable<WarehouseOrderPickParticipant>()
            .Where(participant => orderGuids.Contains(participant.OrderGUID))
            .OrderBy(participant => participant.JoinedAtUtc)
            .ToListAsync();
        return participants
            .GroupBy(participant => participant.OrderGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(participant => new WarehousePickerRefDto
                    {
                        PickerUserGuid = participant.PickerUserGuid,
                        PickerName = participant.PickerName,
                    })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );
    }

    /// <summary>写入前的会话门禁：会话必须存在且仍在拣货中；返回 null 表示可以继续写。</summary>
    private async Task<WarehousePickingResult<T>?> EnterWritableSessionAsync<T>(string orderGuid)
    {
        var session = await SessionQuery(orderGuid, holdRange: false).FirstAsync();
        if (session == null)
        {
            return WarehousePickingResult<T>.Fail(409, WarehousePickingErrorCodes.SessionNotStarted, "请先开始拣货");
        }

        if (session.Status == WarehouseOrderPickSessionStatuses.Submitted)
        {
            return WarehousePickingResult<T>.Fail(
                409,
                WarehousePickingErrorCodes.SessionSubmitted,
                $"该订单已由 {session.SubmittedByName} 提交，不能再修改"
            );
        }

        return null;
    }

    /// <summary>同一请求编号重放（网络重试）：不重复计数，直接返回该行当前合计。</summary>
    private async Task<WarehousePickingResult<WarehousePickingLineMutationDto>?> FindDuplicateAsync(
        string orderGuid,
        Guid clientRequestId
    )
    {
        var existing = await _db.Queryable<WarehouseOrderPickRecord>()
            .Where(record => record.ClientRequestId == clientRequestId)
            .Select(record => new { record.OrderGUID, record.DetailGUID, record.QuantityDelta })
            .FirstAsync();
        if (existing == null)
        {
            return null;
        }

        if (!string.Equals(existing.OrderGUID, orderGuid, StringComparison.OrdinalIgnoreCase))
        {
            return WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                409,
                WarehousePickingErrorCodes.InvalidRequest,
                "请求编号已被其它订单使用"
            );
        }

        var line = await LoadLineAsync(orderGuid, existing.DetailGUID);
        var progress = await WarehousePickingQueries.BuildLineProgressAsync(
            _db,
            orderGuid,
            existing.DetailGUID,
            line?.MinOrderQuantity
        );
        return WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(
            new WarehousePickingLineMutationDto
            {
                Line = progress,
                AppliedDelta = existing.QuantityDelta,
                Duplicate = true,
            }
        );
    }

    private async Task<WarehousePickingResult<WarehousePickingLineMutationDto>> AppendAndReportAsync(
        string orderGuid,
        PickLine line,
        WarehousePickerContext picker,
        int delta,
        int source,
        Guid clientRequestId,
        string? scannedCode,
        int? matchedBy
    )
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new WarehouseOrderPickRecord
        {
            // 主键在 API 侧生成 UUIDv7：按时间递增、聚集主键顺序追加（共享模型还要编译 net8.0，不能用 .NET 9 API 做默认值）。
            RecordGUID = Guid.CreateVersion7().ToString("N"),
            OrderGUID = orderGuid,
            DetailGUID = line.DetailGUID,
            ProductCode = line.ProductCode ?? string.Empty,
            QuantityDelta = delta,
            Source = source,
            ScannedCode = scannedCode,
            MatchedBy = matchedBy,
            MinOrderQuantityAtPick = line.MinOrderQuantity,
            PickerUserGuid = picker.UserGuid,
            PickerName = picker.Name,
            AuthUserGuid = picker.AuthUserGuid,
            DeviceCode = picker.DeviceCode,
            ClientRequestId = clientRequestId,
            CreatedAtUtc = now,
        }).ExecuteCommandAsync();
        if (delta > 0)
        {
            // 在别处找到货又拣到了：没货标记自动失效，不需要拣货员再去撤销。
            await ClearActiveStockoutAsync(orderGuid, line.DetailGUID, picker.Name, now);
        }

        await TouchParticipantAsync(orderGuid, picker, line.DetailGUID, now);

        var progress = await WarehousePickingQueries.BuildLineProgressAsync(
            _db,
            orderGuid,
            line.DetailGUID,
            line.MinOrderQuantity
        );
        return WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(
            new WarehousePickingLineMutationDto { Line = progress, AppliedDelta = delta }
        );
    }

    private Task<int> ClearActiveStockoutAsync(string orderGuid, string detailGuid, string clearedByName, DateTime nowUtc)
    {
        return _db.Updateable<WarehouseOrderPickStockout>()
            .SetColumns(item => item.ClearedAtUtc == nowUtc)
            .SetColumns(item => item.ClearedByName == clearedByName)
            .Where(item => item.OrderGUID == orderGuid && item.DetailGUID == detailGuid && item.ClearedAtUtc == null)
            .ExecuteCommandAsync();
    }

    private async Task TouchParticipantAsync(
        string orderGuid,
        WarehousePickerContext picker,
        string? detailGuid,
        DateTime nowUtc
    )
    {
        var pickerName = picker.Name;
        var pickerUserGuid = picker.UserGuid;
        var updateable = _db.Updateable<WarehouseOrderPickParticipant>()
            .SetColumns(item => item.PickerName == pickerName)
            .SetColumns(item => item.LastActiveAtUtc == nowUtc);
        if (detailGuid != null)
        {
            updateable = updateable.SetColumns(item => item.LastDetailGUID == detailGuid);
        }

        var affected = await updateable
            .Where(item => item.OrderGUID == orderGuid && item.PickerUserGuid == pickerUserGuid)
            .ExecuteCommandAsync();
        if (affected == 0)
        {
            await _db.Insertable(new WarehouseOrderPickParticipant
            {
                OrderGUID = orderGuid,
                PickerUserGuid = picker.UserGuid,
                PickerName = picker.Name,
                JoinedAtUtc = nowUtc,
                LastActiveAtUtc = nowUtc,
                LastDetailGUID = detailGuid,
            }).ExecuteCommandAsync();
        }
    }

    /// <summary>重算订单头金额，与订单管理 UpdateOrderTotalAsync 同口径（订单修订时间按毫秒截断）。</summary>
    private async Task UpdateOrderTotalsAsync(string orderGuid)
    {
        var summary = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == orderGuid && !item.IsDeleted)
            .Select(item => new OrderTotalsRow
            {
                TotalAmount = SqlFunc.AggregateSum(item.OEMAmount ?? 0),
                TotalImportAmount = SqlFunc.AggregateSum(item.ImportAmount ?? 0),
            })
            .FirstAsync();
        var revisionAt = DateTimeOffset.FromUnixTimeMilliseconds(
            new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds()
        ).LocalDateTime;
        decimal totalAmount = summary?.TotalAmount ?? 0;
        decimal totalImportAmount = summary?.TotalImportAmount ?? 0;
        await _db.Updateable<WareHouseOrder>()
            .SetColumns(item => new WareHouseOrder
            {
                OEMTotalAmount = totalAmount,
                ImportTotalAmount = totalImportAmount,
                UpdatedAt = revisionAt,
            })
            .Where(item => item.OrderGUID == orderGuid)
            .ExecuteCommandAsync();
    }

    private async Task<PickLine?> LoadLineAsync(string orderGuid, string detailGuid)
    {
        return await _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<WarehouseProduct>((detail, warehouseProduct) =>
                detail.ProductCode == warehouseProduct.ProductCode
            )
            .Where(detail =>
                detail.DetailGUID == detailGuid
                && detail.OrderGUID == orderGuid
                && !detail.IsDeleted
            )
            .Select((detail, warehouseProduct) => new PickLine
            {
                DetailGUID = detail.DetailGUID,
                ProductCode = detail.ProductCode,
                Quantity = detail.Quantity,
                MinOrderQuantity = warehouseProduct.MinOrderQuantity,
            })
            .FirstAsync();
    }

    private ISugarQueryable<WarehouseOrderPickSession> SessionQuery(string orderGuid, bool holdRange)
    {
        var query = _db.Queryable<WarehouseOrderPickSession>().Where(session => session.OrderGUID == orderGuid);
        if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
        {
            return query;
        }

        // SQL Server：更新锁持有到事务结束，同一订单的写入在此串行；不存在的行需 HOLDLOCK 才能锁住键范围。
        return holdRange ? query.With("WITH(UPDLOCK,HOLDLOCK)") : query.With(SqlWith.UpdLock);
    }

    private async Task<WarehousePickingResult<T>> ExecuteInTransactionAsync<T>(
        Func<Task<WarehousePickingResult<T>>> action
    )
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
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static WarehousePickingResult<T> LineNotFound<T>() =>
        WarehousePickingResult<T>.Fail(404, WarehousePickingErrorCodes.LineNotFound, "找不到该订单行");

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class OrderListRow
    {
        public string OrderGUID { get; set; } = string.Empty;
        public string? OrderNo { get; set; }
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public DateTime? OrderDate { get; set; }
        public int? FlowStatus { get; set; }
        public int? SessionStatus { get; set; }
        public int LineCount { get; set; }
        public decimal? TotalQuantity { get; set; }
    }

    private sealed class FlowStatusCountRow
    {
        public int? FlowStatus { get; set; }
        public int Count { get; set; }
    }

    private sealed class OrderTotalsRow
    {
        public decimal TotalAmount { get; set; }
        public decimal TotalImportAmount { get; set; }
    }

    private sealed class PickLine
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string? ProductCode { get; set; }
        public decimal? Quantity { get; set; }
        public int? MinOrderQuantity { get; set; }
    }
}
