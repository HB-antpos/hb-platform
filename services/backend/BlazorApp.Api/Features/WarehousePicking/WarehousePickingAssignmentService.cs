using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePicking;

/// <summary>派单操作人：登录的仓库经理 / 管理员 / 有订货管理权限的账号。</summary>
public sealed record WarehousePickingAssigner(string UserGuid, string Name);

public interface IWarehousePickingAssignmentService
{
    Task<WarehousePickingResult<List<WarehousePickingPickerCandidateDto>>> ListCandidatesAsync();

    Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> GetAsync(string orderGuid);

    Task<WarehousePickingResult<WarehousePickingAssignmentPreviewDto>> PreviewAsync(
        string orderGuid,
        WarehousePickingAssignmentPreviewRequestDto request
    );

    Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> SaveAsync(
        string orderGuid,
        WarehousePickingAssignmentSaveRequestDto request,
        WarehousePickingAssigner assigner
    );

    Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> ClearAsync(string orderGuid);

    Task<WarehousePickingResult<WarehousePickingBatchAssignResultDto>> AssignEvenlyAsync(
        WarehousePickingBatchAssignRequestDto request,
        WarehousePickingAssigner assigner
    );

    /// <summary>PDA 扫分单条码：解析出订单与段；重新分配或撤销后的旧分单返回 SLIP_STALE。</summary>
    Task<WarehousePickingResult<WarehousePickingSlipResolveDto>> ResolveSlipAsync(string? code);

    /// <summary>分单拣货单打印数据：每段一页，行按走位顺序；segmentNo 为空时返回全部段。</summary>
    Task<WarehousePickingResult<WarehousePickingSlipsDto>> GetSlipsAsync(string orderGuid, int? segmentNo);
}

/// <summary>
/// 拣货分配：经理选员工，系统按 M 型走位顺序把订单行按品种数切成首尾相接的连续段，每人一段。
/// 预览与保存分开：保存提交的是预览出来的逐行归属，经理看到什么就存什么；服务端只校验，不重算。
/// 分配只做引导，拣货写入不看分配；同一订单的保存先对订单行加更新锁，两位经理同时派单不会撞主键。
/// </summary>
internal sealed class WarehousePickingAssignmentService(
    SqlSugarContext context,
    IWarehousePickerService pickerService,
    ILogger<WarehousePickingAssignmentService> logger
) : IWarehousePickingAssignmentService
{
    private const int MaxBatchOrders = 50;
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<WarehousePickingResult<List<WarehousePickingPickerCandidateDto>>> ListCandidatesAsync()
    {
        var eligible = await pickerService.ListEligibleAsync();
        var activeCounts = await _db.Queryable<WarehouseOrderPickAssignment>()
            .InnerJoin<WareHouseOrder>((assignment, order) => assignment.OrderGUID == order.OrderGUID)
            .LeftJoin<WarehouseOrderPickSession>((assignment, order, session) => assignment.OrderGUID == session.OrderGUID)
            .Where((assignment, order, session) =>
                !order.IsDeleted
                && (order.FlowStatus == WarehousePickingRules.FlowStatusSubmitted
                    || order.FlowStatus == WarehousePickingRules.FlowStatusPicking)
                && (session.OrderGUID == null || session.Status == WarehouseOrderPickSessionStatuses.Picking)
            )
            .GroupBy((assignment, order, session) => assignment.PickerUserGuid)
            .Select((assignment, order, session) => new ActiveCountRow
            {
                PickerUserGuid = assignment.PickerUserGuid,
                OrderCount = SqlFunc.AggregateDistinctCount(assignment.OrderGUID),
            })
            .ToListAsync();
        var countByPicker = activeCounts.ToDictionary(
            row => row.PickerUserGuid,
            row => row.OrderCount,
            StringComparer.OrdinalIgnoreCase
        );

        return WarehousePickingResult<List<WarehousePickingPickerCandidateDto>>.Ok(
            eligible
                .Select(item => new WarehousePickingPickerCandidateDto
                {
                    PickerUserGuid = item.UserGuid,
                    PickerName = item.Name,
                    RoleLabel = item.RoleLabel,
                    ActiveOrderCount = countByPicker.GetValueOrDefault(item.UserGuid),
                })
                .ToList()
        );
    }

    public async Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> GetAsync(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到该订单"
            );
        }

        return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(await BuildSummaryAsync(orderGuid));
    }

    public async Task<WarehousePickingResult<WarehousePickingAssignmentPreviewDto>> PreviewAsync(
        string orderGuid,
        WarehousePickingAssignmentPreviewRequestDto request
    )
    {
        var gate = await CheckAssignableAsync<WarehousePickingAssignmentPreviewDto>(orderGuid);
        if (gate != null)
        {
            return gate;
        }

        var pickers = await ResolvePickersAsync<WarehousePickingAssignmentPreviewDto>(
            request.Pickers.Select(item => item.PickerUserGuid)
        );
        if (pickers.Failure != null)
        {
            return pickers.Failure;
        }

        var sortedLines = WarehousePickingRoute.SortByRoute(await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid));
        var requestedCounts = request.Pickers.Select(item => item.LineCount).ToList();
        List<int> counts;
        if (requestedCounts.All(count => count == null))
        {
            counts = WarehousePickingRoute.SplitEvenly(sortedLines.Count, pickers.Pickers.Count);
        }
        else if (requestedCounts.All(count => count is >= 0) && requestedCounts.Sum(count => count!.Value) == sortedLines.Count)
        {
            counts = requestedCounts.Select(count => count!.Value).ToList();
        }
        else
        {
            return WarehousePickingResult<WarehousePickingAssignmentPreviewDto>.Fail(
                400,
                WarehousePickingErrorCodes.AssignCountsInvalid,
                $"各员工的品种数之和必须等于订单品种数 {sortedLines.Count}"
            );
        }

        var segments = WarehousePickingRoute.SplitContiguous(sortedLines, counts);
        return WarehousePickingResult<WarehousePickingAssignmentPreviewDto>.Ok(new WarehousePickingAssignmentPreviewDto
        {
            LineCount = sortedLines.Count,
            Pieces = sortedLines.Sum(line => line.OrderedQuantity),
            UnlocatedLineCount = sortedLines.Count(line => !WarehousePickingRoute.HasLocation(line.LocationCode)),
            IrregularLineCount = sortedLines.Count(IsIrregular),
            Segments = pickers.Pickers
                .Select((picker, index) => ToSegment(picker, segments[index]))
                .ToList(),
        });
    }

    public async Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> SaveAsync(
        string orderGuid,
        WarehousePickingAssignmentSaveRequestDto request,
        WarehousePickingAssigner assigner
    )
    {
        var inputs = request.Assignments.Where(item => item.DetailGuids.Count > 0).ToList();
        if (inputs.Count == 0)
        {
            return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(
                400,
                WarehousePickingErrorCodes.AssignLinesInvalid,
                "没有要分配的订单行；取消分配请用撤销"
            );
        }

        var pickers = await ResolvePickersAsync<WarehousePickingAssignmentSummaryDto>(inputs.Select(item => item.PickerUserGuid));
        if (pickers.Failure != null)
        {
            return pickers.Failure;
        }

        var result = await ExecuteInTransactionAsync(async () =>
        {
            var gate = await LockAssignableOrderAsync<WarehousePickingAssignmentSummaryDto>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var orderDetailGuids = (await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid))
                .Select(line => line.DetailGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<WarehouseOrderPickAssignment>();
            var now = DateTime.UtcNow;
            var version = await NextVersionAsync(orderGuid, now);
            for (var index = 0; index < inputs.Count; index++)
            {
                var picker = pickers.Pickers[index];
                foreach (var raw in inputs[index].DetailGuids)
                {
                    var detailGuid = raw?.Trim() ?? string.Empty;
                    // 行必须属于本单且只能派给一个人：预览之后订单行被删或同一行出现两次都拒绝，请经理重新预览。
                    if (!orderDetailGuids.Contains(detailGuid) || !seen.Add(detailGuid))
                    {
                        return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(
                            409,
                            WarehousePickingErrorCodes.AssignLinesInvalid,
                            "订单明细已变化，请重新预览后再保存"
                        );
                    }

                    rows.Add(new WarehouseOrderPickAssignment
                    {
                        OrderGUID = orderGuid,
                        DetailGUID = detailGuid,
                        PickerUserGuid = picker.UserGuid,
                        PickerName = picker.Name,
                        // 段号按经理选择的员工顺序；空段已在入口过滤，编号连续。
                        SegmentNo = index + 1,
                        AssignmentVersion = version,
                        AssignedByUserGuid = assigner.UserGuid,
                        AssignedByName = assigner.Name,
                        AssignedAtUtc = now,
                    });
                }
            }

            await ReplaceAssignmentsAsync(orderGuid, rows);
            return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(new WarehousePickingAssignmentSummaryDto());
        });
        if (!result.Success)
        {
            return result;
        }

        logger.LogInformation(
            "拣货分配: Order={OrderGuid}, Pickers={Pickers}, By={AssignerUserGuid}",
            orderGuid,
            string.Join(",", pickers.Pickers.Select(picker => picker.UserGuid)),
            assigner.UserGuid
        );
        return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(await BuildSummaryAsync(orderGuid));
    }

    public async Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> ClearAsync(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到该订单"
            );
        }

        // 撤销不限订单状态：已提交的单子留着分配也没用，允许清掉；重复撤销是幂等的。
        await _db.Deleteable<WarehouseOrderPickAssignment>()
            .Where(assignment => assignment.OrderGUID == orderGuid)
            .ExecuteCommandAsync();
        return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(await BuildSummaryAsync(orderGuid));
    }

    public async Task<WarehousePickingResult<WarehousePickingBatchAssignResultDto>> AssignEvenlyAsync(
        WarehousePickingBatchAssignRequestDto request,
        WarehousePickingAssigner assigner
    )
    {
        var orderGuids = request.OrderGuids
            .Select(guid => guid?.Trim())
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (orderGuids.Count is 0 or > MaxBatchOrders)
        {
            return WarehousePickingResult<WarehousePickingBatchAssignResultDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                $"一次最多分配 {MaxBatchOrders} 张订单"
            );
        }

        var pickers = await ResolvePickersAsync<WarehousePickingBatchAssignResultDto>(request.PickerUserGuids);
        if (pickers.Failure != null)
        {
            return pickers.Failure;
        }

        // 每张单各自一个事务：一张失败（已提交、被删）不影响其它单，逐张返回结果。
        var result = new WarehousePickingBatchAssignResultDto();
        foreach (var orderGuid in orderGuids)
        {
            var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
            var saved = await ExecuteInTransactionAsync(async () =>
            {
                var gate = await LockAssignableOrderAsync<bool>(orderGuid);
                if (gate != null)
                {
                    return gate;
                }

                var sortedLines = WarehousePickingRoute.SortByRoute(await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid));
                var segments = WarehousePickingRoute.SplitContiguous(
                    sortedLines,
                    WarehousePickingRoute.SplitEvenly(sortedLines.Count, pickers.Pickers.Count)
                );
                var now = DateTime.UtcNow;
                var version = await NextVersionAsync(orderGuid, now);
                // 品种比人少时会有空段：跳过空段，段号保持连续。
                var rows = segments
                    .Select((segment, index) => (Segment: segment, Picker: pickers.Pickers[index]))
                    .Where(item => item.Segment.Count > 0)
                    .SelectMany((item, segmentIndex) => item.Segment.Select(line => new WarehouseOrderPickAssignment
                    {
                        OrderGUID = orderGuid,
                        DetailGUID = line.DetailGuid,
                        PickerUserGuid = item.Picker.UserGuid,
                        PickerName = item.Picker.Name,
                        SegmentNo = segmentIndex + 1,
                        AssignmentVersion = version,
                        AssignedByUserGuid = assigner.UserGuid,
                        AssignedByName = assigner.Name,
                        AssignedAtUtc = now,
                    }))
                    .ToList();
                await ReplaceAssignmentsAsync(orderGuid, rows);
                return WarehousePickingResult<bool>.Ok(true);
            });
            result.Items.Add(new WarehousePickingBatchAssignItemDto
            {
                OrderGuid = orderGuid,
                OrderNo = order?.OrderNo,
                Success = saved.Success,
                ErrorCode = saved.ErrorCode,
                Message = saved.Message,
            });
        }

        logger.LogInformation(
            "拣货批量分配: Orders={Orders}, Succeeded={Succeeded}, Pickers={Pickers}, By={AssignerUserGuid}",
            orderGuids.Count,
            result.Items.Count(item => item.Success),
            string.Join(",", pickers.Pickers.Select(picker => picker.UserGuid)),
            assigner.UserGuid
        );
        return WarehousePickingResult<WarehousePickingBatchAssignResultDto>.Ok(result);
    }

    private async Task ReplaceAssignmentsAsync(string orderGuid, List<WarehouseOrderPickAssignment> rows)
    {
        // 限定本单：先删本单旧分配再整单写入新分配。
        await _db.Deleteable<WarehouseOrderPickAssignment>()
            .Where(assignment => assignment.OrderGUID == orderGuid)
            .ExecuteCommandAsync();
        if (rows.Count > 0)
        {
            await _db.Insertable(rows).ExecuteCommandAsync();
        }
    }

    /// <summary>负责人按段号排列，附各段分单条码。</summary>
    private async Task<WarehousePickingAssignmentSummaryDto> BuildSummaryAsync(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        var sortedLines = WarehousePickingRoute.SortByRoute(await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid));
        var assignments = await WarehousePickingQueries.LoadAssignmentsAsync(_db, orderGuid);
        var assignees = assignments.Values
            .GroupBy(item => item.SegmentNo)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var first = group.First();
                return new WarehousePickingAssigneeDto
                {
                    PickerUserGuid = first.PickerUserGuid,
                    PickerName = first.PickerName,
                    SegmentNo = group.Key,
                    LineCount = group.Count(),
                    SlipCode = string.IsNullOrWhiteSpace(order?.OrderNo)
                        ? null
                        : WarehousePickingRules.FormatSlipCode(order.OrderNo, group.Key, first.AssignmentVersion),
                };
            })
            .ToList();

        var latest = assignments.Values.OrderByDescending(item => item.AssignedAtUtc).FirstOrDefault();
        return new WarehousePickingAssignmentSummaryDto
        {
            OrderGuid = orderGuid,
            LineCount = sortedLines.Count,
            UnassignedLineCount = sortedLines.Count(line => !assignments.ContainsKey(line.DetailGuid)),
            Assignees = assignees,
            AssignedByName = latest?.AssignedByName,
            AssignedAtUtc = latest?.AssignedAtUtc,
        };
    }

    public async Task<WarehousePickingResult<WarehousePickingSlipResolveDto>> ResolveSlipAsync(string? code)
    {
        var slip = WarehousePickingRules.ParseSlipCode(code);
        if (slip == null)
        {
            return WarehousePickingResult<WarehousePickingSlipResolveDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "不是分单拣货单条码"
            );
        }

        var (orderNo, segmentNo, version) = slip.Value;
        // 订单号没有唯一约束：同号多单时取仍可拣货、且确实有这个分单版本的那一张。
        var candidates = await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && order.OrderNo == orderNo)
            .Select(order => new { order.OrderGUID, order.OrderNo, order.FlowStatus })
            .ToListAsync();
        if (candidates.Count == 0)
        {
            return WarehousePickingResult<WarehousePickingSlipResolveDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到分单对应的订单"
            );
        }

        var pickable = candidates.Where(order => WarehousePickingRules.IsPickable(order.FlowStatus)).ToList();
        if (pickable.Count == 0)
        {
            return WarehousePickingResult<WarehousePickingSlipResolveDto>.Fail(
                409,
                WarehousePickingErrorCodes.OrderNotPickable,
                "该订单当前不能拣货"
            );
        }

        var pickableGuids = pickable.Select(order => order.OrderGUID).ToList();
        var rows = await _db.Queryable<WarehouseOrderPickAssignment>()
            .Where(item => pickableGuids.Contains(item.OrderGUID) && item.AssignmentVersion == version)
            .ToListAsync();
        var segmentRows = rows.Where(item => item.SegmentNo == segmentNo).ToList();
        var orderGuids = segmentRows.Select(item => item.OrderGUID).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (orderGuids.Count != 1)
        {
            return WarehousePickingResult<WarehousePickingSlipResolveDto>.Fail(
                409,
                WarehousePickingErrorCodes.SlipStale,
                "这张分单已重新分配或撤销，请找仓库经理要新打印的分单"
            );
        }

        var orderGuid = orderGuids[0];
        var first = segmentRows[0];
        return WarehousePickingResult<WarehousePickingSlipResolveDto>.Ok(new WarehousePickingSlipResolveDto
        {
            OrderGuid = orderGuid,
            OrderNo = pickable.First(order => string.Equals(order.OrderGUID, orderGuid, StringComparison.OrdinalIgnoreCase)).OrderNo,
            SegmentNo = segmentNo,
            SegmentCount = rows
                .Where(item => string.Equals(item.OrderGUID, orderGuid, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SegmentNo)
                .Distinct()
                .Count(),
            PickerUserGuid = first.PickerUserGuid,
            PickerName = first.PickerName,
            LineCount = segmentRows.Count,
        });
    }

    public async Task<WarehousePickingResult<WarehousePickingSlipsDto>> GetSlipsAsync(string orderGuid, int? segmentNo)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<WarehousePickingSlipsDto>.Fail(
                404,
                WarehousePickingErrorCodes.OrderNotFound,
                "找不到该订单"
            );
        }

        var assignments = await WarehousePickingQueries.LoadAssignmentsAsync(_db, orderGuid);
        if (assignments.Count == 0 || string.IsNullOrWhiteSpace(order.OrderNo))
        {
            return WarehousePickingResult<WarehousePickingSlipsDto>.Fail(
                409,
                WarehousePickingErrorCodes.AssignLinesInvalid,
                "该订单还没有分配拣货"
            );
        }

        var sortedLines = WarehousePickingRoute.SortByRoute(await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid));
        var segments = assignments.Values
            .GroupBy(item => item.SegmentNo)
            .OrderBy(group => group.Key)
            .Select(group => (SegmentNo: group.Key, First: group.First()))
            .ToList();
        var slips = new List<WarehousePickingSlipDto>();
        foreach (var (number, first) in segments)
        {
            if (segmentNo.HasValue && segmentNo.Value != number)
            {
                continue;
            }

            var lines = sortedLines
                .Where(line => assignments.TryGetValue(line.DetailGuid, out var assignment) && assignment.SegmentNo == number)
                .ToList();
            var located = lines.Where(line => WarehousePickingRoute.HasLocation(line.LocationCode)).ToList();
            slips.Add(new WarehousePickingSlipDto
            {
                SegmentNo = number,
                SegmentCount = segments.Count,
                SlipCode = WarehousePickingRules.FormatSlipCode(order.OrderNo, number, first.AssignmentVersion),
                PickerUserGuid = first.PickerUserGuid,
                PickerName = first.PickerName,
                LineCount = lines.Count,
                Pieces = lines.Sum(line => line.OrderedQuantity),
                FirstLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[0].LocationCode),
                LastLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[^1].LocationCode),
                OtherPickerNames = segments
                    .Where(item => item.SegmentNo != number)
                    .Select(item => item.First.PickerName)
                    .ToList(),
                Lines = lines
                    .Select(line =>
                    {
                        var parsed = WarehousePickingRoute.ParseLocationCode(line.LocationCode);
                        return new WarehousePickingSlipLineDto
                        {
                            DetailGuid = line.DetailGuid,
                            LocationCode = line.LocationCode,
                            Zone = parsed?.Zone,
                            RowLabel = parsed?.RowLabel,
                            ItemNumber = line.ItemNumber,
                            ProductName = line.ProductName,
                            Barcode = line.Barcode,
                            OrderedQuantity = line.OrderedQuantity,
                            MinOrderQuantity = line.MinOrderQuantity,
                        };
                    })
                    .ToList(),
            });
        }

        if (slips.Count == 0)
        {
            return WarehousePickingResult<WarehousePickingSlipsDto>.Fail(
                404,
                WarehousePickingErrorCodes.InvalidRequest,
                "找不到该分段"
            );
        }

        var latest = assignments.Values.OrderByDescending(item => item.AssignedAtUtc).First();
        return WarehousePickingResult<WarehousePickingSlipsDto>.Ok(new WarehousePickingSlipsDto
        {
            OrderGuid = orderGuid,
            OrderNo = order.OrderNo,
            StoreCode = order.StoreCode,
            StoreName = order.StoreName,
            OrderDate = order.OrderDate,
            AssignedByName = latest.AssignedByName,
            AssignedAtUtc = latest.AssignedAtUtc,
            Slips = slips,
        });
    }

    /// <summary>本单下一次分配的版本号（在订单行更新锁内调用，保证递增）。</summary>
    private async Task<int> NextVersionAsync(string orderGuid, DateTime nowUtc)
    {
        // 空表时取到 0（值类型默认值），避免 MAX 在空集上返回 NULL 的转换问题。
        var previous = await _db.Queryable<WarehouseOrderPickAssignment>()
            .Where(item => item.OrderGUID == orderGuid)
            .OrderBy(item => item.AssignmentVersion, OrderByType.Desc)
            .Select(item => item.AssignmentVersion)
            .FirstAsync();
        return WarehousePickingRules.NextAssignmentVersion(previous, nowUtc);
    }

    private static WarehousePickingAssignmentSegmentDto ToSegment(
        WarehousePickerEligibility picker,
        List<WarehouseRouteLine> lines
    )
    {
        var located = lines.Where(line => WarehousePickingRoute.HasLocation(line.LocationCode)).ToList();
        return new WarehousePickingAssignmentSegmentDto
        {
            PickerUserGuid = picker.UserGuid,
            PickerName = picker.Name,
            LineCount = lines.Count,
            Pieces = lines.Sum(line => line.OrderedQuantity),
            FirstLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[0].LocationCode),
            LastLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[^1].LocationCode),
            UnlocatedLineCount = lines.Count - located.Count,
            IrregularLineCount = lines.Count(IsIrregular),
            DetailGuids = lines.Select(line => line.DetailGuid).ToList(),
        };
    }

    private static bool IsIrregular(WarehouseRouteLine line) =>
        WarehousePickingRoute.HasLocation(line.LocationCode)
        && WarehousePickingRoute.ParseLocationCode(line.LocationCode) == null;

    /// <summary>员工去重、1–10 人、都必须仍可拣货；顺序即分段顺序。</summary>
    private async Task<(List<WarehousePickerEligibility> Pickers, WarehousePickingResult<T>? Failure)> ResolvePickersAsync<T>(
        IEnumerable<string?> pickerUserGuids
    )
    {
        var guids = pickerUserGuids.Select(guid => guid?.Trim() ?? string.Empty).ToList();
        if (guids.Count is 0 or > WarehousePickingRoute.MaxPickersPerOrder
            || guids.Any(string.IsNullOrEmpty)
            || guids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != guids.Count)
        {
            return (new List<WarehousePickerEligibility>(), WarehousePickingResult<T>.Fail(
                400,
                WarehousePickingErrorCodes.AssignPickerInvalid,
                $"请选择 1 到 {WarehousePickingRoute.MaxPickersPerOrder} 位不重复的员工"
            ));
        }

        var pickers = new List<WarehousePickerEligibility>();
        foreach (var guid in guids)
        {
            var eligibility = await pickerService.GetEligibilityAsync(guid);
            if (eligibility is not { IsAllowed: true })
            {
                return (pickers, WarehousePickingResult<T>.Fail(
                    400,
                    WarehousePickingErrorCodes.AssignPickerInvalid,
                    "所选员工中有人已不能拣货，请刷新员工列表"
                ));
            }

            pickers.Add(eligibility);
        }

        return (pickers, null);
    }

    /// <summary>只有已提交或配货中、且拣货未提交的订单可以派单。</summary>
    private async Task<WarehousePickingResult<T>?> CheckAssignableAsync<T>(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<T>.Fail(404, WarehousePickingErrorCodes.OrderNotFound, "找不到该订单");
        }

        if (!WarehousePickingRules.IsPickable(order.FlowStatus))
        {
            return WarehousePickingResult<T>.Fail(409, WarehousePickingErrorCodes.OrderNotPickable, "该订单当前不能拣货");
        }

        var session = await WarehousePickingQueries.LoadSessionAsync(_db, orderGuid);
        if (session?.Status == WarehouseOrderPickSessionStatuses.Submitted)
        {
            return WarehousePickingResult<T>.Fail(
                409,
                WarehousePickingErrorCodes.SessionSubmitted,
                $"该订单已由 {session.SubmittedByName} 提交拣货，不能再分配"
            );
        }

        return null;
    }

    /// <summary>事务内先锁订单行（SQL Server 更新锁持有到事务结束），同一订单的派单串行，再复核可派状态。</summary>
    private async Task<WarehousePickingResult<T>?> LockAssignableOrderAsync<T>(string orderGuid)
    {
        var query = _db.Queryable<WareHouseOrder>().Where(order => order.OrderGUID == orderGuid);
        if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            query = query.With(SqlWith.UpdLock);
        }

        await query.Select(order => order.OrderGUID).FirstAsync();
        return await CheckAssignableAsync<T>(orderGuid);
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

    private sealed class ActiveCountRow
    {
        public string PickerUserGuid { get; set; } = string.Empty;
        public int OrderCount { get; set; }
    }
}
