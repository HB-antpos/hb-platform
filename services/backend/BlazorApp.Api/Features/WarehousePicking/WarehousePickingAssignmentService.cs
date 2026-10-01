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

    /// <summary>订单列表“拣货分配”列：多张订单的负责人与各自品种数（一次最多 200 张）。</summary>
    Task<WarehousePickingResult<Dictionary<string, List<WarehousePickingAssigneeDto>>>> ListSummariesAsync(
        IEnumerable<string> orderGuids
    );

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

    /// <summary>员工扫分单领取：该段还没人领时写到扫码人名下；已被别人领取时不改，只如实返回负责人。</summary>
    Task<WarehousePickingResult<WarehousePickingSlipResolveDto>> ClaimSlipAsync(string? code, WarehousePickerContext picker);

    /// <summary>经理改某一段的负责人（或释放为待领取）；不改分配版本，已打印的分单仍有效。</summary>
    Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> SetSegmentPickerAsync(
        string orderGuid,
        int segmentNo,
        string? pickerUserGuid
    );
}

/// <summary>
/// 拣货分配：经理定份数（可顺带指定员工），系统按 M 型走位顺序把订单行按品种数切成首尾相接的连续段；
/// 没指定员工的段打印分单后由员工扫码领取。
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
                assignment.PickerUserGuid != null
                && !order.IsDeleted
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
        var countByPicker = activeCounts.Where(row => row.PickerUserGuid != null).ToDictionary(
            row => row.PickerUserGuid!,
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

    public async Task<WarehousePickingResult<Dictionary<string, List<WarehousePickingAssigneeDto>>>> ListSummariesAsync(
        IEnumerable<string> orderGuids
    )
    {
        var guids = orderGuids
            .Select(guid => guid?.Trim())
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (guids.Count > 200)
        {
            return WarehousePickingResult<Dictionary<string, List<WarehousePickingAssigneeDto>>>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "一次最多查询 200 张订单"
            );
        }

        return WarehousePickingResult<Dictionary<string, List<WarehousePickingAssigneeDto>>>.Ok(
            await WarehousePickingQueries.LoadAssigneesAsync(_db, guids)
        );
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

        var pickers = await ResolveSegmentPickersAsync<WarehousePickingAssignmentPreviewDto>(
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
                $"各段品种数之和必须等于订单品种数 {sortedLines.Count}"
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

        var pickers = await ResolveSegmentPickersAsync<WarehousePickingAssignmentSummaryDto>(inputs.Select(item => item.PickerUserGuid));
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
                        PickerUserGuid = picker?.UserGuid,
                        PickerName = picker?.Name,
                        // 段号按经理排的顺序；空段已在入口过滤，编号连续。
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
            string.Join(",", pickers.Pickers.Select(picker => picker?.UserGuid ?? "待领取")),
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

        // 指定员工时按员工人数分段；只给份数时每段都待扫码领取。
        var segmentInputs = request.PickerUserGuids.Count > 0
            ? request.PickerUserGuids.Select(guid => (string?)guid).ToList()
            : Enumerable.Repeat<string?>(null, Math.Max(0, request.SegmentCount ?? 0)).ToList();
        var pickers = await ResolveSegmentPickersAsync<WarehousePickingBatchAssignResultDto>(segmentInputs);
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
                        PickerUserGuid = item.Picker?.UserGuid,
                        PickerName = item.Picker?.Name,
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
            string.Join(",", pickers.Pickers.Select(picker => picker?.UserGuid ?? "待领取")),
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

    /// <summary>负责人按段号排列，附各段分单条码、路线与拣货进度。</summary>
    private async Task<WarehousePickingAssignmentSummaryDto> BuildSummaryAsync(string orderGuid)
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        var sortedLines = WarehousePickingRoute.SortByRoute(await WarehousePickingQueries.LoadRouteLinesAsync(_db, orderGuid));
        var assignments = await WarehousePickingQueries.LoadAssignmentsAsync(_db, orderGuid);
        var pickTotals = await WarehousePickingQueries.LoadPickTotalsAsync(_db, orderGuid);
        var pickedByDetail = pickTotals
            .GroupBy(total => total.DetailGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(total => total.Quantity), StringComparer.OrdinalIgnoreCase);
        var stockouts = await WarehousePickingQueries.LoadActiveStockoutsAsync(_db, orderGuid);
        var lastActive = (await WarehousePickingQueries.LoadParticipantsAsync(_db, orderGuid))
            .ToDictionary(participant => participant.PickerUserGuid, participant => participant.LastActiveAtUtc, StringComparer.OrdinalIgnoreCase);
        var assignees = assignments.Values
            .GroupBy(item => item.SegmentNo)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var first = group.First();
                // 段内的行按走位顺序取，起止货位与打印分单一致。
                var lines = sortedLines
                    .Where(line => assignments.TryGetValue(line.DetailGuid, out var assignment) && assignment.SegmentNo == group.Key)
                    .ToList();
                var located = lines.Where(line => WarehousePickingRoute.HasLocation(line.LocationCode)).ToList();
                return new WarehousePickingAssigneeDto
                {
                    PickerUserGuid = first.PickerUserGuid,
                    PickerName = first.PickerName,
                    SegmentNo = group.Key,
                    LineCount = group.Count(),
                    SlipCode = string.IsNullOrWhiteSpace(order?.OrderNo)
                        ? null
                        : WarehousePickingRules.FormatSlipCode(order.OrderNo, group.Key, first.AssignmentVersion),
                    FirstLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[0].LocationCode),
                    LastLocation = located.Count == 0 ? null : WarehousePickingRoute.PrimaryLocation(located[^1].LocationCode),
                    Pieces = lines.Sum(line => line.OrderedQuantity),
                    PickedPieces = lines.Sum(line => Math.Max(0, pickedByDetail.GetValueOrDefault(line.DetailGuid))),
                    CompletedLineCount = lines.Count(line => pickedByDetail.GetValueOrDefault(line.DetailGuid) >= line.OrderedQuantity),
                    StockoutLineCount = lines.Count(line =>
                        stockouts.ContainsKey(line.DetailGuid)
                        && pickedByDetail.GetValueOrDefault(line.DetailGuid) < line.OrderedQuantity),
                    LastActiveAtUtc = first.PickerUserGuid != null && lastActive.TryGetValue(first.PickerUserGuid, out var activeAt)
                        ? activeAt
                        : null,
                    Helpers = BuildHelpers(lines, first.PickerUserGuid, pickTotals),
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
            Lines = assignments.Values
                .Select(item => new WarehousePickingAssignmentLineDto { DetailGuid = item.DetailGUID, SegmentNo = item.SegmentNo })
                .ToList(),
        };
    }

    /// <summary>
    /// 帮拣的人：负责人以外在这段里净拣过货的人，各算拣过几个品种。帮忙不改负责人，只在卡片上标出来；
    /// 待领取的段没有负责人，拣过的人都算帮拣。
    /// </summary>
    private static List<WarehousePickingHelperDto> BuildHelpers(
        IReadOnlyCollection<WarehouseRouteLine> segmentLines,
        string? assigneeUserGuid,
        IEnumerable<WarehousePickingQueries.PickTotalRow> pickTotals
    )
    {
        var segmentDetails = segmentLines.Select(line => line.DetailGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pickTotals
            .Where(total =>
                segmentDetails.Contains(total.DetailGUID)
                && total.Quantity > 0
                && !string.Equals(total.PickerUserGuid, assigneeUserGuid, StringComparison.OrdinalIgnoreCase))
            .GroupBy(total => total.PickerUserGuid, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WarehousePickingHelperDto
            {
                PickerName = group.Select(total => total.PickerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty,
                LineCount = group.Select(total => total.DetailGUID).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            })
            .OrderByDescending(helper => helper.LineCount)
            .ThenBy(helper => helper.PickerName, StringComparer.Ordinal)
            .ToList();
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

    public async Task<WarehousePickingResult<WarehousePickingSlipResolveDto>> ClaimSlipAsync(
        string? code,
        WarehousePickerContext picker
    )
    {
        var resolved = await ResolveSlipAsync(code);
        if (!resolved.Success || resolved.Data!.PickerUserGuid != null)
        {
            // 已有人负责（含扫码人自己）：只做引导，不改负责人，如实返回。
            if (resolved.Success)
            {
                resolved.Data!.ClaimedByMe = string.Equals(resolved.Data.PickerUserGuid, picker.UserGuid, StringComparison.OrdinalIgnoreCase);
            }

            return resolved;
        }

        var slip = resolved.Data;
        var version = WarehousePickingRules.ParseSlipCode(code)!.Value.Version;
        var pickerUserGuid = picker.UserGuid;
        var pickerName = picker.Name;
        // 锁住订单行后只更新“同版本、同段、仍无人领取”的行：两人同时扫同一张分单时只有一个人领到。
        var claimed = await ExecuteInTransactionAsync(async () =>
        {
            await LockOrderRowAsync(slip.OrderGuid);
            var affected = await _db.Updateable<WarehouseOrderPickAssignment>()
                .SetColumns(item => item.PickerUserGuid == pickerUserGuid)
                .SetColumns(item => item.PickerName == pickerName)
                .Where(item =>
                    item.OrderGUID == slip.OrderGuid
                    && item.SegmentNo == slip.SegmentNo
                    && item.AssignmentVersion == version
                    && item.PickerUserGuid == null
                )
                .ExecuteCommandAsync();
            return WarehousePickingResult<int>.Ok(affected);
        });

        var after = await ResolveSlipAsync(code);
        if (after.Success)
        {
            after.Data!.ClaimedByMe = string.Equals(after.Data.PickerUserGuid, picker.UserGuid, StringComparison.OrdinalIgnoreCase);
            after.Data.ClaimedNow = claimed.Data > 0;
            if (after.Data.ClaimedNow)
            {
                logger.LogInformation(
                    "拣货分单领取: Order={OrderGuid}, Segment={SegmentNo}, Picker={PickerUserGuid}",
                    slip.OrderGuid,
                    slip.SegmentNo,
                    picker.UserGuid
                );
            }
        }

        return after;
    }

    public async Task<WarehousePickingResult<WarehousePickingAssignmentSummaryDto>> SetSegmentPickerAsync(
        string orderGuid,
        int segmentNo,
        string? pickerUserGuid
    )
    {
        WarehousePickerEligibility? eligibility = null;
        if (!string.IsNullOrWhiteSpace(pickerUserGuid))
        {
            eligibility = await pickerService.GetEligibilityAsync(pickerUserGuid.Trim());
            if (eligibility is not { IsAllowed: true })
            {
                return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(
                    400,
                    WarehousePickingErrorCodes.AssignPickerInvalid,
                    "该员工已不能拣货，请刷新员工列表"
                );
            }
        }

        var result = await ExecuteInTransactionAsync(async () =>
        {
            var gate = await LockAssignableOrderAsync<bool>(orderGuid);
            if (gate != null)
            {
                return gate;
            }

            var newGuid = eligibility?.UserGuid;
            var newName = eligibility?.Name;
            // 只改负责人不改版本：已打印的分单仍然有效，领错了的人换成对的人即可。
            var affected = await _db.Updateable<WarehouseOrderPickAssignment>()
                .SetColumns(item => item.PickerUserGuid == newGuid)
                .SetColumns(item => item.PickerName == newName)
                .Where(item => item.OrderGUID == orderGuid && item.SegmentNo == segmentNo)
                .ExecuteCommandAsync();
            return affected == 0
                ? WarehousePickingResult<bool>.Fail(404, WarehousePickingErrorCodes.InvalidRequest, "找不到该分段")
                : WarehousePickingResult<bool>.Ok(true);
        });
        if (!result.Success)
        {
            return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Fail(result.StatusCode, result.ErrorCode!, result.Message!);
        }

        return WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(await BuildSummaryAsync(orderGuid));
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
                    .Select(item => item.First.PickerName ?? $"第{item.SegmentNo}段待领取")
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
        WarehousePickerEligibility? picker,
        List<WarehouseRouteLine> lines
    )
    {
        var located = lines.Where(line => WarehousePickingRoute.HasLocation(line.LocationCode)).ToList();
        return new WarehousePickingAssignmentSegmentDto
        {
            PickerUserGuid = picker?.UserGuid,
            PickerName = picker?.Name,
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

    /// <summary>
    /// 各段负责人：1–10 段，顺序即分段顺序；员工可留空（待扫分单领取），指定的员工不能重复且必须仍可拣货。
    /// </summary>
    private async Task<(List<WarehousePickerEligibility?> Pickers, WarehousePickingResult<T>? Failure)> ResolveSegmentPickersAsync<T>(
        IEnumerable<string?> pickerUserGuids
    )
    {
        var guids = pickerUserGuids.Select(guid => string.IsNullOrWhiteSpace(guid) ? null : guid.Trim()).ToList();
        var named = guids.Where(guid => guid != null).Select(guid => guid!).ToList();
        if (guids.Count is 0 or > WarehousePickingRoute.MaxPickersPerOrder
            || named.Distinct(StringComparer.OrdinalIgnoreCase).Count() != named.Count)
        {
            return (new List<WarehousePickerEligibility?>(), WarehousePickingResult<T>.Fail(
                400,
                WarehousePickingErrorCodes.AssignPickerInvalid,
                $"请分成 1 到 {WarehousePickingRoute.MaxPickersPerOrder} 份，指定的员工不能重复"
            ));
        }

        var pickers = new List<WarehousePickerEligibility?>();
        foreach (var guid in guids)
        {
            if (guid == null)
            {
                pickers.Add(null);
                continue;
            }

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
        await LockOrderRowAsync(orderGuid);
        return await CheckAssignableAsync<T>(orderGuid);
    }

    private async Task LockOrderRowAsync(string orderGuid)
    {
        var query = _db.Queryable<WareHouseOrder>().Where(order => order.OrderGUID == orderGuid);
        if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            query = query.With(SqlWith.UpdLock);
        }

        await query.Select(order => order.OrderGUID).FirstAsync();
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
        public string? PickerUserGuid { get; set; }
        public int OrderCount { get; set; }
    }
}
