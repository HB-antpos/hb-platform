using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobTimesheetPushService
{
    Task<ApiResponse<MyobPushResultDto>> PushAsync(
        MyobPushRequestDto request, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobUndoResultDto>> UndoBatchAsync(
        string batchGuid, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<List<MyobPushBatchSummaryDto>>> ListBatchesAsync(
        string connectionGuid, string? periodStart, CancellationToken cancellationToken);

    Task<ApiResponse<MyobPushBatchDetailDto>> GetBatchAsync(string batchGuid, CancellationToken cancellationToken);
}

/// <summary>
/// Timesheet 推送与撤销（方案第 4.2、4.5 节）。**会写 MYOB 的生产数据**，所以：
/// - 总闸 Myob__TimesheetPushEnabled 默认关，灰度白名单按公司文件（空 = 全部禁止）；服务端每次重新规划（与预览同一段代码），不信任客户端的任何工时数字；
/// - 每个员工顺序处理（天然满足 MYOB 限流）：**先落库 Running 持锁**（过滤唯一索引保证同一周期同时只有一个），再读现状（也是撤销的「推送前快照」）、
///   判定、PUT、回读校验，最后把同一行更新成结局。落库失败就不推；
/// - 落库之后的 PUT / 回读 / 收尾**不再响应调用方取消**（用户关页签不能让 MYOB 写了一半而记录卡在 Running）；
/// - PUT 结果不确定（超时 / 5xx）时先回读再定结局：已等于目标算成功，等于推送前算失败，其余记 Mismatch——不丢可撤销的快照；
/// - 实测 PUT 是整体覆盖该员工该区间，所以「已有未发薪内容」必须由人**按人**勾选覆盖；有已发薪（Processed）的一律不碰；
/// - 撤销 = 把推送前的快照 PUT 回去（原来没有则 PUT 空 Lines）；内容被人改过 / 已发薪 / 有更新批次时拒绝，不覆盖；
/// - 日志只记接口、连接 GUID、批次 GUID、人数与状态码，不含姓名、MYOB 响应体或令牌。
/// </summary>
internal sealed class MyobTimesheetPushService : IMyobTimesheetPushService
{
    private const int MaxBatchRows = 2000;
    private const int InChunk = 400;
    private const int ErrorCodeLength = 80;
    private static readonly TimeSpan StaleRunning = TimeSpan.FromMinutes(10);

    private readonly ISqlSugarClient _db;
    private readonly IMyobTimesheetPlanSource _plans;
    private readonly ICurrentUserManageableStoreScopeService _scope;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly MyobOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MyobTimesheetPushService>? _logger;

    public MyobTimesheetPushService(
        SqlSugarContext context,
        IMyobTimesheetPlanSource plans,
        ICurrentUserManageableStoreScopeService scope,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IOptions<MyobOptions> options,
        ILogger<MyobTimesheetPushService> logger)
        : this(context.Db, plans, scope, client, tokens, options, null, logger)
    {
    }

    internal MyobTimesheetPushService(
        ISqlSugarClient db,
        IMyobTimesheetPlanSource plans,
        ICurrentUserManageableStoreScopeService scope,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IOptions<MyobOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<MyobTimesheetPushService>? logger = null)
    {
        _db = db;
        _plans = plans;
        _scope = scope;
        _client = client;
        _tokens = tokens;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    // ───────────────────────── 推送 ─────────────────────────

    private sealed record PushContext(
        MyobTimesheetComputation Computation,
        string BatchGuid,
        string ActorUserGuid,
        string Token,
        HashSet<string> OverwriteUsers,
        HashSet<string> Acknowledged);

    /// <summary>单个员工的处理结果；StopAll = 令牌 / 授权类失败或周期被占用，本块剩余员工不再处理。</summary>
    private sealed record OneResult(MyobPushUserResultDto Result, bool StopAll = false);

    public async Task<ApiResponse<MyobPushResultDto>> PushAsync(
        MyobPushRequestDto request, string actorUserGuid, CancellationToken cancellationToken)
    {
        request ??= new MyobPushRequestDto();
        var connectionGuid = request.ConnectionGuid?.Trim() ?? string.Empty;
        if (connectionGuid.Length == 0 || string.IsNullOrWhiteSpace(request.PeriodStart))
        {
            return ApiResponse<MyobPushResultDto>.Error("缺少公司文件或工资周期", MyobPushErrorCodes.InvalidRequest);
        }

        var userGuids = (request.UserGuids ?? [])
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (userGuids.Count == 0 || userGuids.Count > MyobTimesheetPushRules.MaxUsersPerChunk)
        {
            return ApiResponse<MyobPushResultDto>.Error(
                $"每次推送 1–{MyobTimesheetPushRules.MaxUsersPerChunk} 位员工，更多请分块调用", MyobPushErrorCodes.InvalidRequest);
        }

        // ① 总闸 + 灰度白名单（权限由控制器的策略保证）
        if (!MyobTimesheetPushGate.IsAllowed(_options, connectionGuid))
        {
            return ApiResponse<MyobPushResultDto>.Error("Timesheet 推送未开启，或该公司文件不在灰度名单里", MyobPushErrorCodes.Disabled);
        }

        // ② 与预览同一段规划代码；分店范围在里面校验
        var computed = await _plans.ComputeAsync(connectionGuid, request.PeriodStart, false, cancellationToken);
        if (!computed.Success || computed.Data is null)
        {
            return ApiResponse<MyobPushResultDto>.Error(computed.Message, computed.ErrorCode);
        }

        var computation = computed.Data;
        // ③ 不推未结束的周期
        if (computation.End.Date >= computation.Today.Date)
        {
            return ApiResponse<MyobPushResultDto>.Error("这个工资周期还没有结束，不能推送", MyobPushErrorCodes.PeriodNotEnded);
        }

        // ④ 批次号：客户端可带上第一块返回的批次号；已存在的批次必须是同一公司文件、同一周期，不能把别的周期混进来
        string batchGuid;
        if (string.IsNullOrWhiteSpace(request.BatchGuid))
        {
            batchGuid = Guid.NewGuid().ToString();
        }
        else if (Guid.TryParse(request.BatchGuid.Trim(), out _))
        {
            batchGuid = request.BatchGuid.Trim();
            var existing = await _db.Queryable<MyobTimesheetPush>().Where(item => item.BatchGuid == batchGuid).ToListAsync();
            if (existing.Any(item => !item.ConnectionGuid.Equals(connectionGuid, StringComparison.OrdinalIgnoreCase)
                                     || item.PeriodStart != computation.Start))
            {
                return ApiResponse<MyobPushResultDto>.Error("批次号和这次的公司文件 / 工资周期不一致", MyobPushErrorCodes.InvalidRequest);
            }
        }
        else
        {
            return ApiResponse<MyobPushResultDto>.Error("批次号格式不正确", MyobPushErrorCodes.InvalidRequest);
        }

        // ⑤ 并发预检：同一 (公司文件, 周期) 同时只允许一个推送在跑；超过 10 分钟的 Running 视为中断（真正的保险是过滤唯一索引）
        await ReleaseStaleRunningAsync(connectionGuid, computation.Start);
        if (await HasRunningAsync(connectionGuid, computation.Start))
        {
            return ApiResponse<MyobPushResultDto>.Error("这个周期正在推送中，请稍后再试", MyobPushErrorCodes.InProgress);
        }

        string token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobPushResultDto>.Error(ex.Message, MyobPushErrorCodes.AuthFailed);
        }

        var context = new PushContext(
            computation,
            batchGuid,
            actorUserGuid ?? string.Empty,
            token,
            (request.OverwriteUserGuids ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (request.AcknowledgedHintCodes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var result = new MyobPushResultDto
        {
            BatchGuid = batchGuid,
            ConnectionGuid = connectionGuid,
            PeriodStart = MyobTimesheetPushRules.FormatDate(computation.Start),
        };
        string? stopCode = null;
        foreach (var userGuid in userGuids)
        {
            if (stopCode is not null)
            {
                result.Results.Add(new MyobPushUserResultDto
                {
                    UserGuid = userGuid,
                    UserName = NameOf(computation, userGuid),
                    Status = MyobPushStatuses.Failed,
                    Code = stopCode,
                    Message = stopCode == MyobPushErrorCodes.InProgress
                        ? "这个周期正在推送中，本块已停止，请稍后再试"
                        : "前面的员工遇到授权问题，本块已停止；请重新授权后重试",
                });
                continue;
            }

            var one = await PushOneAsync(context, userGuid, cancellationToken);
            result.Results.Add(one.Result);
            if (one.StopAll)
            {
                stopCode = one.Result.Code == MyobPushErrorCodes.InProgress ? MyobPushErrorCodes.InProgress : MyobPushErrorCodes.AuthFailed;
            }
        }

        _logger?.LogInformation(
            "MYOB timesheet push chunk finished: connection={ConnectionGuid} batch={BatchGuid} users={Count} pushed={Pushed} failed={Failed}",
            connectionGuid,
            batchGuid,
            result.Results.Count,
            result.Results.Count(item => item.Status == MyobPushStatuses.Pushed),
            result.Results.Count(item => item.Status is MyobPushStatuses.Failed or MyobPushStatuses.Mismatch));
        return ApiResponse<MyobPushResultDto>.OK(result);
    }

    private async Task<OneResult> PushOneAsync(PushContext context, string userGuid, CancellationToken cancellationToken)
    {
        var computation = context.Computation;
        var connection = computation.Connection;
        var row = computation.Dto.Rows.FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase));
        var result = new MyobPushUserResultDto { UserGuid = userGuid, UserName = row?.UserName ?? userGuid };

        // ⑥ 重新校验拦截项 / 提示项：任何一项不满足只拒绝该员工，不拖累同块其他员工
        if (row is null)
        {
            return Rejected(result, MyobPushErrorCodes.NotReady, "预览里没有这位员工，请刷新预览后再试");
        }

        if (row.Status != MyobPreviewStatuses.Ready)
        {
            var code = row.Blockers.FirstOrDefault()?.Code ?? MyobPushErrorCodes.NotReady;
            var message = row.Blockers.Count > 0 ? string.Join("；", row.Blockers.Select(item => item.Message)) : "该员工本期没有要推送的转账工时";
            return Rejected(result, code, message);
        }

        var missingHints = row.HintCodes.Where(code => !context.Acknowledged.Contains(code)).ToList();
        if (missingHints.Count > 0)
        {
            return Rejected(result, MyobPushErrorCodes.HintNotAcknowledged, "有提示项还没有勾选「已知晓」：" + string.Join("、", missingHints));
        }

        if (!computation.Plans.TryGetValue(userGuid, out var plan) || !computation.Links.TryGetValue(userGuid, out var link))
        {
            return Rejected(result, MyobPushErrorCodes.NotReady, "找不到该员工的推送计划或员工卡绑定");
        }

        var (planned, unmapped) = MyobTimesheetPushRules.BuildPlannedCells(plan, computation.MappingByKey);
        if (unmapped.Count > 0)
        {
            return Rejected(result, MyobBlockerCodes.CategoryNotMapped, "要推送的工时里有还没映射 MYOB 工资类别的项目");
        }

        if (planned.Count == 0)
        {
            return Rejected(result, MyobPushErrorCodes.NotReady, "该员工本期没有要推送的转账工时");
        }

        var target = MyobTimesheetPushRules.ToTarget(planned);
        var hash = MyobTimesheetPushRules.ComputeRequestHash(
            connection.ConnectionGuid, link.MyobEmployeeUid, computation.Start, computation.End, planned);
        result.PushedHours = MyobTimesheetPushRules.TotalHours(target);

        // ⑦ 先落库 Running 持锁（过滤唯一索引拦并发），之后读到的现状才是「推送前快照」：写不进去就不推
        var record = new MyobTimesheetPush
        {
            BatchGuid = context.BatchGuid,
            ConnectionGuid = connection.ConnectionGuid,
            PeriodStart = computation.Start,
            Weeks = computation.Weeks,
            UserGuid = userGuid,
            MyobEmployeeUid = link.MyobEmployeeUid,
            Status = MyobPushStatuses.Running,
            RequestHash = hash,
            PushedMinutes = planned.Sum(cell => cell.Minutes),
            ActorUserGuid = context.ActorUserGuid,
            StartedAt = Now(),
        };
        try
        {
            await _db.Insertable(record).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            // 过滤唯一索引拦下了并发的第二个 Running，或库写入失败：都不推。
            var inProgress = await HasRunningAsync(connection.ConnectionGuid, computation.Start);
            result.Status = MyobPushStatuses.Failed;
            result.Code = inProgress ? MyobPushErrorCodes.InProgress : MyobPushErrorCodes.RecordFailed;
            result.Message = inProgress ? "这个周期正在推送中，请稍后再试" : "推送记录写入失败，没有写入 MYOB";
            return new OneResult(result, inProgress);
        }

        IReadOnlyList<MyobTimesheetLineRecord> current;
        List<MyobTimesheetCell> before;
        try
        {
            // ⑧ 读 MYOB 现状（锁已持有）。到 PUT 之前都还没写 MYOB，取消 / 失败只需释放锁。
            try
            {
                current = await _client.GetTimesheetLinesAsync(
                    connection.BusinessId, context.Token, link.MyobEmployeeUid, computation.Start, computation.End, cancellationToken);
            }
            catch (MyobApiException ex)
            {
                var status = (int?)ex.StatusCode;
                await FinishAsync(record, MyobPushStatuses.Failed, MyobPushErrorCodes.ReadFailed, status);
                result.Status = MyobPushStatuses.Failed;
                result.Code = MyobPushErrorCodes.ReadFailed;
                result.Message = "读取 MYOB 现有工时失败，没有写入";
                return new OneResult(result, IsAuthStatus(status));
            }

            before = MyobTimesheetPushRules.Normalize(current);
            var hasExtras = MyobTimesheetPushRules.HasExtras(current);
            if (MyobTimesheetPushRules.HasProcessed(current))
            {
                record.BeforeJson = MyobTimesheetPushRules.ToJson(before);
                await FinishAsync(record, MyobPushStatuses.SkippedProcessed, null, null);
                result.Status = MyobPushStatuses.SkippedProcessed;
                result.Message = "MYOB 里该周期已有已发薪的记录，不会改动";
                return new OneResult(result);
            }

            if (MyobTimesheetPushRules.SameCells(before, target) && !hasExtras)
            {
                record.BeforeJson = MyobTimesheetPushRules.ToJson(before);
                record.AfterJson = MyobTimesheetPushRules.ToJson(target);
                await FinishAsync(record, MyobPushStatuses.UpToDate, null, null);
                result.Status = MyobPushStatuses.UpToDate;
                result.Message = "MYOB 里已经是这份工时，没有重复写入";
                return new OneResult(result);
            }

            if ((before.Count > 0 || hasExtras) && !context.OverwriteUsers.Contains(userGuid))
            {
                record.BeforeJson = MyobTimesheetPushRules.ToJson(before);
                await FinishAsync(record, MyobPushStatuses.SkippedHasEntries, null, null);
                result.Status = MyobPushStatuses.SkippedHasEntries;
                result.Message = hasExtras
                    ? "MYOB 里该周期已有带 Job / Notes 的工时，未勾选覆盖，没有写入（覆盖后撤销也还原不了这两项）"
                    : "MYOB 里该周期已有未发薪的工时，未勾选覆盖，没有写入";
                return new OneResult(result);
            }

            // PUT 之前把快照写进记录：快照落不了库就不推。
            record.BeforeJson = MyobTimesheetPushRules.ToJson(before);
            record.AfterJson = MyobTimesheetPushRules.ToJson(target);
            try
            {
                await _db.Updateable(record).ExecuteCommandAsync();
            }
            catch (Exception)
            {
                await FinishAsync(record, MyobPushStatuses.Failed, MyobPushErrorCodes.RecordFailed, null);
                result.Status = MyobPushStatuses.Failed;
                result.Code = MyobPushErrorCodes.RecordFailed;
                result.Message = "推送前快照写入失败，没有写入 MYOB";
                return new OneResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(record, MyobPushStatuses.Failed, "CANCELED", null);
            throw;
        }

        // ⑨ 从 PUT 起不再响应调用方取消：MYOB 一旦可能已写入，必须走完回读与收尾。
        var detached = CancellationToken.None;
        var put = await PutWithRetryAsync(
            connection.BusinessId, context.Token, link.MyobEmployeeUid, computation.Start, computation.End,
            MyobTimesheetPushRules.ToPutLines(target), detached);

        var putCode = put.Success ? null : put.ErrorName ?? (put.StatusCode == 0 ? "NETWORK_ERROR" : $"HTTP_{put.StatusCode}");
        var uncertain = !put.Success && (put.StatusCode == 0 || put.StatusCode >= 500);
        if (!put.Success && !uncertain)
        {
            // 4xx：MYOB 明确拒绝，没写入。
            await FinishAsync(record, MyobPushStatuses.Failed, putCode, put.StatusCode);
            result.Status = MyobPushStatuses.Failed;
            result.Code = putCode;
            result.Message = "MYOB 拒绝了这次写入，可以稍后重试";
            return new OneResult(result, IsAuthStatus(put.StatusCode));
        }

        // ⑩ 回读校验（PUT 成功，或结果不确定时用它判断到底写没写进去）
        List<MyobTimesheetCell> after;
        try
        {
            after = MyobTimesheetPushRules.Normalize(await _client.GetTimesheetLinesAsync(
                connection.BusinessId, context.Token, link.MyobEmployeeUid, computation.Start, computation.End, detached));
        }
        catch (MyobApiException ex)
        {
            // 写没写进去都无从确认：记 Mismatch（可撤销、需人工核对），不记 Failed，免得丢掉快照。
            await FinishAsync(record, MyobPushStatuses.Mismatch, MyobPushErrorCodes.ReadbackFailed, (int?)ex.StatusCode);
            result.Status = MyobPushStatuses.Mismatch;
            result.Code = MyobPushErrorCodes.ReadbackFailed;
            result.Message = put.Success ? "已写入，但回读校验失败，请在 MYOB 里核对" : "写入结果不确定且回读失败，请在 MYOB 里核对";
            return new OneResult(result, IsAuthStatus((int?)ex.StatusCode));
        }

        if (MyobTimesheetPushRules.SameCells(after, target))
        {
            // 含「PUT 超时但其实已生效」的情况：按成功收尾，快照保留，可撤销。
            await FinishAsync(record, MyobPushStatuses.Pushed, null, null);
            result.Status = MyobPushStatuses.Pushed;
            return new OneResult(result);
        }

        if (uncertain && MyobTimesheetPushRules.SameCells(after, before))
        {
            // 不确定的 PUT，回读仍是推送前的内容：确实没写进去。
            await FinishAsync(record, MyobPushStatuses.Failed, putCode, put.StatusCode == 0 ? null : put.StatusCode);
            result.Status = MyobPushStatuses.Failed;
            result.Code = putCode;
            result.Message = "MYOB 没有响应这次写入，内容保持原样，可以稍后重试";
            return new OneResult(result);
        }

        record.AfterJson = MyobTimesheetPushRules.ToJson(after);
        await FinishAsync(record, MyobPushStatuses.Mismatch, MyobPushErrorCodes.ReadbackMismatch, null);
        result.Status = MyobPushStatuses.Mismatch;
        result.Code = MyobPushErrorCodes.ReadbackMismatch;
        result.Message = "写入后回读的内容与预期不一致，请在 MYOB 里核对";
        return new OneResult(result);
    }

    // ───────────────────────── 撤销 ─────────────────────────

    public async Task<ApiResponse<MyobUndoResultDto>> UndoBatchAsync(
        string batchGuid, string actorUserGuid, CancellationToken cancellationToken)
    {
        var rows = await _db.Queryable<MyobTimesheetPush>().Where(item => item.BatchGuid == batchGuid).ToListAsync();
        if (rows.Count == 0)
        {
            return ApiResponse<MyobUndoResultDto>.Error("找不到这个推送批次", MyobPushErrorCodes.BatchNotFound);
        }

        var connectionGuid = rows[0].ConnectionGuid;
        if (!MyobTimesheetPushGate.IsAllowed(_options, connectionGuid))
        {
            return ApiResponse<MyobUndoResultDto>.Error("Timesheet 推送未开启，或该公司文件不在灰度名单里", MyobPushErrorCodes.Disabled);
        }

        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobUndoResultDto>.Error(scope.Message, MyobTimesheetErrorCodes.Forbidden);
        }

        if (!CanSeeBatch(scope, rows))
        {
            return ApiResponse<MyobUndoResultDto>.Error("只能撤销自己推送的批次", MyobTimesheetErrorCodes.Forbidden);
        }

        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return ApiResponse<MyobUndoResultDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        // 同一批次里同一员工可能有多条写入记录（分块重发）：最新的先撤，才不会被同批更旧/更新的记录互相挡住。
        var undoable = rows
            .Where(item => MyobPushStatuses.WroteToMyob.Contains(item.Status))
            .OrderByDescending(item => item.StartedAt)
            .ToList();
        if (undoable.Count == 0)
        {
            return ApiResponse<MyobUndoResultDto>.Error("这个批次没有可以撤销的写入", MyobPushErrorCodes.NothingToUndo);
        }

        foreach (var period in undoable.Select(item => item.PeriodStart).Distinct())
        {
            await ReleaseStaleRunningAsync(connectionGuid, period);
            if (await HasRunningAsync(connectionGuid, period))
            {
                return ApiResponse<MyobUndoResultDto>.Error("这个周期正在推送中，请稍后再试", MyobPushErrorCodes.InProgress);
            }
        }

        string token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobUndoResultDto>.Error(ex.Message, MyobPushErrorCodes.AuthFailed);
        }

        var names = await LoadUserNamesAsync(undoable.Select(item => item.UserGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var response = new MyobUndoResultDto { BatchGuid = batchGuid };
        var stopped = false;
        foreach (var record in undoable)
        {
            var result = new MyobPushUserResultDto
            {
                UserGuid = record.UserGuid,
                UserName = names.GetValueOrDefault(record.UserGuid) ?? record.UserGuid,
                PushedHours = MyobTimesheetPushRules.FromJson(record.AfterJson) is { } written
                    ? MyobTimesheetPushRules.TotalHours(written)
                    : 0m,
            };
            if (stopped)
            {
                result.Status = MyobPushStatuses.Failed;
                result.Code = MyobPushErrorCodes.AuthFailed;
                result.Message = "前面的员工遇到授权问题，本次撤销已停止";
                response.Results.Add(result);
                continue;
            }

            // 每条记录按自己的公司文件 / 周期 / 周数处理，不信任批次里第一条的值。
            var start = record.PeriodStart;
            var end = start.AddDays(record.Weeks * 7 - 1);
            stopped = await UndoOneAsync(connection, token, record, start, end, actorUserGuid, result, cancellationToken);
            response.Results.Add(result);
        }

        _logger?.LogInformation(
            "MYOB timesheet push undo finished: connection={ConnectionGuid} batch={BatchGuid} users={Count} undone={Undone}",
            connectionGuid, batchGuid, response.Results.Count, response.Results.Count(item => item.Status == MyobPushStatuses.Undone));
        return ApiResponse<MyobUndoResultDto>.OK(response);
    }

    /// <summary>返回 true = 授权类失败，剩余员工不再处理。</summary>
    private async Task<bool> UndoOneAsync(
        MyobConnection connection,
        string token,
        MyobTimesheetPush record,
        DateTime start,
        DateTime end,
        string actorUserGuid,
        MyobPushUserResultDto result,
        CancellationToken cancellationToken)
    {
        // 1) 该员工在该周期有更新的写入：撤销本批会覆盖掉后来的重推。UpToDate 没有改动 MYOB，不算。
        var hasNewer = await _db.Queryable<MyobTimesheetPush>()
            .AnyAsync(item => item.ConnectionGuid == record.ConnectionGuid
                              && item.PeriodStart == record.PeriodStart
                              && item.UserGuid == record.UserGuid
                              && item.StartedAt > record.StartedAt
                              && (item.Status == MyobPushStatuses.Pushed || item.Status == MyobPushStatuses.Mismatch));
        if (hasNewer)
        {
            return UndoRefused(result, MyobPushStatuses.UndoBlockedNewer, "该员工在这个周期有更新的推送批次，请先撤销更新的批次");
        }

        var before = MyobTimesheetPushRules.FromJson(record.BeforeJson);
        var after = MyobTimesheetPushRules.FromJson(record.AfterJson);
        if (before is null || after is null)
        {
            return UndoRefused(result, MyobPushStatuses.UndoConflict, "推送前的快照不可用，无法自动撤销，请在 MYOB 里手工处理");
        }

        // 2) 读现状：已发薪不改；已经是推送前的内容（上次撤销写成功但没来得及记账）就直接记账；
        //    和本批写入的不一样（有人在 MYOB 里手改过）不覆盖。
        IReadOnlyList<MyobTimesheetLineRecord> current;
        try
        {
            current = await _client.GetTimesheetLinesAsync(
                connection.BusinessId, token, record.MyobEmployeeUid, start, end, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            result.Status = MyobPushStatuses.Failed;
            result.Code = MyobPushErrorCodes.ReadFailed;
            result.Message = "读取 MYOB 现有工时失败，没有撤销";
            return IsAuthStatus((int?)ex.StatusCode);
        }

        if (MyobTimesheetPushRules.HasProcessed(current))
        {
            await NoteAsync(record, "已发薪，撤销被拒绝");
            return UndoRefused(result, MyobPushStatuses.UndoBlockedProcessed, "MYOB 里该员工已发薪，不会改动；请财务在 MYOB 里反向处理");
        }

        var currentCells = MyobTimesheetPushRules.Normalize(current);
        if (MyobTimesheetPushRules.SameCells(currentCells, before))
        {
            await MarkUndoneAsync(record, actorUserGuid);
            result.Status = MyobPushStatuses.Undone;
            result.Message = "MYOB 里已经是推送前的内容";
            return false;
        }

        if (!MyobTimesheetPushRules.SameCells(currentCells, after))
        {
            await NoteAsync(record, "MYOB 内容已被改动，撤销被拒绝");
            return UndoRefused(result, MyobPushStatuses.UndoConflict, "MYOB 里的内容和本批写入的不一样（可能有人手改过），没有覆盖，请人工处理");
        }

        // 3) 把推送前的快照 PUT 回去（原来没有 = 空 Lines，实测可清空）。从这里起不再响应调用方取消。
        var detached = CancellationToken.None;
        var put = await PutWithRetryAsync(
            connection.BusinessId, token, record.MyobEmployeeUid, start, end, MyobTimesheetPushRules.ToPutLines(before), detached);
        if (!put.Success)
        {
            result.Status = MyobPushStatuses.Failed;
            result.Code = put.ErrorName ?? (put.StatusCode == 0 ? "NETWORK_ERROR" : $"HTTP_{put.StatusCode}");
            result.Message = "MYOB 拒绝了撤销，可以稍后重试";
            await NoteAsync(record, "撤销失败：" + result.Code);
            return IsAuthStatus(put.StatusCode);
        }

        // 4) 回读确认已恢复
        try
        {
            var restored = MyobTimesheetPushRules.Normalize(await _client.GetTimesheetLinesAsync(
                connection.BusinessId, token, record.MyobEmployeeUid, start, end, detached));
            if (MyobTimesheetPushRules.SameCells(restored, before))
            {
                await MarkUndoneAsync(record, actorUserGuid);
                result.Status = MyobPushStatuses.Undone;
                return false;
            }
        }
        catch (MyobApiException ex)
        {
            await NoteAsync(record, "撤销后回读失败，请在 MYOB 里核对");
            result.Status = MyobPushStatuses.Mismatch;
            result.Code = MyobPushErrorCodes.ReadbackFailed;
            result.Message = "已撤销，但回读校验失败，请在 MYOB 里核对";
            return IsAuthStatus((int?)ex.StatusCode);
        }

        await NoteAsync(record, "撤销后回读与推送前不一致，请在 MYOB 里核对");
        result.Status = MyobPushStatuses.Mismatch;
        result.Code = MyobPushErrorCodes.ReadbackMismatch;
        result.Message = "撤销后回读的内容与推送前不一致，请在 MYOB 里核对";
        return false;
    }

    // ───────────────────────── 批次查询 ─────────────────────────

    public async Task<ApiResponse<List<MyobPushBatchSummaryDto>>> ListBatchesAsync(
        string connectionGuid, string? periodStart, CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<List<MyobPushBatchSummaryDto>>.Error(scope.Message, MyobTimesheetErrorCodes.Forbidden);
        }

        var query = _db.Queryable<MyobTimesheetPush>().Where(item => item.ConnectionGuid == connectionGuid);
        if (!string.IsNullOrWhiteSpace(periodStart))
        {
            if (!DateTime.TryParseExact(periodStart.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return ApiResponse<List<MyobPushBatchSummaryDto>>.Error("工资周期格式不正确", MyobTimesheetErrorCodes.InvalidPeriod);
            }

            var day = parsed.Date;
            query = query.Where(item => item.PeriodStart == day);
        }

        var rows = await query.OrderBy(item => item.StartedAt, OrderByType.Desc).Take(MaxBatchRows).ToListAsync();
        var batches = rows
            .GroupBy(item => item.BatchGuid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToList())
            .Where(group => CanSeeBatch(scope, group))
            .ToList();
        var names = await LoadUserNamesAsync(batches.SelectMany(group => group).Select(item => item.ActorUserGuid)
            .Where(guid => !string.IsNullOrWhiteSpace(guid)).Select(guid => guid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var list = batches
            .Select(group => Summarize(group, names))
            .OrderByDescending(item => item.StartedAt)
            .ToList();
        return ApiResponse<List<MyobPushBatchSummaryDto>>.OK(list);
    }

    public async Task<ApiResponse<MyobPushBatchDetailDto>> GetBatchAsync(string batchGuid, CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobPushBatchDetailDto>.Error(scope.Message, MyobTimesheetErrorCodes.Forbidden);
        }

        var rows = await _db.Queryable<MyobTimesheetPush>().Where(item => item.BatchGuid == batchGuid).ToListAsync();
        if (rows.Count == 0)
        {
            return ApiResponse<MyobPushBatchDetailDto>.Error("找不到这个推送批次", MyobPushErrorCodes.BatchNotFound);
        }

        if (!CanSeeBatch(scope, rows))
        {
            return ApiResponse<MyobPushBatchDetailDto>.Error("只能查看自己推送的批次", MyobTimesheetErrorCodes.Forbidden);
        }

        var userGuids = rows.Select(item => item.UserGuid)
            .Concat(rows.Select(item => item.ActorUserGuid ?? string.Empty))
            .Where(guid => guid.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = await LoadUserNamesAsync(userGuids);
        return ApiResponse<MyobPushBatchDetailDto>.OK(new MyobPushBatchDetailDto
        {
            Summary = Summarize(rows, names),
            ConnectionGuid = rows[0].ConnectionGuid,
            PeriodStart = MyobTimesheetPushRules.FormatDate(rows[0].PeriodStart),
            Items = rows.OrderBy(item => item.StartedAt).Select(item => new MyobPushBatchItemDto
            {
                UserGuid = item.UserGuid,
                UserName = names.GetValueOrDefault(item.UserGuid) ?? item.UserGuid,
                Status = item.Status,
                ErrorCode = item.ErrorCode,
                ErrorStatus = item.ErrorStatus,
                PushedHours = MyobTimesheetPushRules.FromJson(item.AfterJson) is { } cells ? MyobTimesheetPushRules.TotalHours(cells) : 0m,
                StartedAt = item.StartedAt,
                FinishedAt = item.FinishedAt,
                UndoneAt = item.UndoneAt,
            }).ToList(),
        });
    }

    private static MyobPushBatchSummaryDto Summarize(IReadOnlyList<MyobTimesheetPush> rows, IReadOnlyDictionary<string, string> names)
    {
        var first = rows.OrderBy(item => item.StartedAt).First();
        return new MyobPushBatchSummaryDto
        {
            BatchGuid = first.BatchGuid,
            StartedAt = first.StartedAt,
            ActorUserGuid = first.ActorUserGuid,
            ActorName = first.ActorUserGuid is not null && names.TryGetValue(first.ActorUserGuid, out var actor) ? actor : null,
            Total = rows.Count,
            Pushed = rows.Count(item => item.Status == MyobPushStatuses.Pushed),
            UpToDate = rows.Count(item => item.Status == MyobPushStatuses.UpToDate),
            Skipped = rows.Count(item => item.Status is MyobPushStatuses.SkippedProcessed or MyobPushStatuses.SkippedHasEntries),
            Failed = rows.Count(item => item.Status is MyobPushStatuses.Failed or MyobPushStatuses.Running),
            Mismatch = rows.Count(item => item.Status == MyobPushStatuses.Mismatch),
            Undone = rows.Count(item => item.Status == MyobPushStatuses.Undone),
            Undoable = rows.Count(item => MyobPushStatuses.WroteToMyob.Contains(item.Status)),
        };
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static bool IsAuthStatus(int? status) => status is 401 or 403;

    /// <summary>
    /// 管理员可以看 / 撤销所有批次；其他账号只能处理自己推送的批次——批次是在推送者当时的分店范围内算出来的，
    /// 别人的批次可能含他管不到的分店员工（姓名与工时），不能因为拿到批次号就看到或撤销。
    /// </summary>
    private static bool CanSeeBatch(CurrentUserManageableStoreScope scope, IReadOnlyList<MyobTimesheetPush> rows) =>
        scope.IsAdmin
        || rows.All(item => !string.IsNullOrEmpty(scope.UserGuid)
                            && string.Equals(item.ActorUserGuid, scope.UserGuid, StringComparison.OrdinalIgnoreCase));

    private static string NameOf(MyobTimesheetComputation computation, string userGuid) =>
        computation.Dto.Rows.FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase))?.UserName ?? userGuid;

    private static string? Trunc(string? value) =>
        value is { Length: > ErrorCodeLength } ? value[..ErrorCodeLength] : value;

    private static OneResult Rejected(MyobPushUserResultDto result, string code, string message)
    {
        result.Status = MyobPushStatuses.Blocked;
        result.Code = code;
        result.Message = message;
        return new OneResult(result);
    }

    private static bool UndoRefused(MyobPushUserResultDto result, string status, string message)
    {
        result.Status = status;
        result.Message = message;
        return false;
    }

    private async Task<bool> HasRunningAsync(string connectionGuid, DateTime periodStart) =>
        await _db.Queryable<MyobTimesheetPush>()
            .AnyAsync(item => item.ConnectionGuid == connectionGuid && item.PeriodStart == periodStart && item.Status == MyobPushStatuses.Running);

    /// <summary>
    /// 上一次推送被中断（进程重启等）遗留的 Running 记录：超过 10 分钟放行下一次。中断时 MYOB 里是否已写入不确定，
    /// 所以标成 Mismatch（可撤销、需人工核对），而不是 Failed——Failed 会让它的推送前快照够不着撤销。
    /// </summary>
    private async Task ReleaseStaleRunningAsync(string connectionGuid, DateTime periodStart)
    {
        var cutoff = Now() - StaleRunning;
        var stale = await _db.Queryable<MyobTimesheetPush>()
            .Where(item => item.ConnectionGuid == connectionGuid && item.PeriodStart == periodStart
                           && item.Status == MyobPushStatuses.Running && item.StartedAt < cutoff)
            .ToListAsync();
        foreach (var item in stale)
        {
            item.Status = item.BeforeJson is null ? MyobPushStatuses.Failed : MyobPushStatuses.Mismatch;
            item.ErrorCode = MyobPushErrorCodes.Stale;
            item.FinishedAt = Now();
            await _db.Updateable(item).ExecuteCommandAsync();
        }
    }

    /// <summary>把 Running 记录收成最终结局。更新失败只记告警：不改写已经发生的 MYOB 结果；Running 超时后会被放行。</summary>
    private async Task FinishAsync(MyobTimesheetPush record, string status, string? errorCode, int? errorStatus)
    {
        record.Status = status;
        record.ErrorCode = Trunc(errorCode);
        record.ErrorStatus = errorStatus;
        record.FinishedAt = Now();
        try
        {
            await _db.Updateable(record).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            _logger?.LogWarning("MYOB timesheet push record update failed: push={PushGuid} status={Status}", record.PushGuid, status);
        }
    }

    private async Task MarkUndoneAsync(MyobTimesheetPush record, string actorUserGuid)
    {
        record.Status = MyobPushStatuses.Undone;
        record.UndoneAt = Now();
        record.UndoneByUserGuid = actorUserGuid;
        record.UndoNote = null;
        try
        {
            await _db.Updateable(record).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            // MYOB 已经恢复；记账失败时下一次撤销会发现「已经是推送前的内容」并补记。
            _logger?.LogWarning("MYOB timesheet push undo record update failed: push={PushGuid}", record.PushGuid);
        }
    }

    private async Task NoteAsync(MyobTimesheetPush record, string note)
    {
        record.UndoNote = note.Length > 200 ? note[..200] : note;
        try
        {
            await _db.Updateable(record).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            _logger?.LogWarning("MYOB timesheet push note update failed: push={PushGuid}", record.PushGuid);
        }
    }

    /// <summary>PUT；5xx 与网络错误重试 1 次（整体覆盖，幂等），4xx 不重试。网络层最终失败返回状态码 0。</summary>
    private async Task<MyobTimesheetPutResult> PutWithRetryAsync(
        string businessId,
        string token,
        string employeeUid,
        DateTime start,
        DateTime end,
        IReadOnlyList<MyobTimesheetPutLine> lines,
        CancellationToken cancellationToken)
    {
        MyobTimesheetPutResult? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                last = await _client.PutTimesheetAsync(businessId, token, employeeUid, start, end, lines, cancellationToken);
                if (last.StatusCode < 500)
                {
                    return last;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or MyobApiException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                last = new MyobTimesheetPutResult(ex is MyobApiException api ? (int?)api.StatusCode ?? 0 : 0, null);
                if (last.StatusCode is >= 400 and < 500)
                {
                    return last;
                }
            }
        }

        return last ?? new MyobTimesheetPutResult(0, null);
    }

    private async Task<Dictionary<string, string>> LoadUserNamesAsync(IReadOnlyList<string> userGuids)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(InChunk))
        {
            var guids = chunk.ToList();
            foreach (var user in await _db.Queryable<User>().Where(item => guids.Contains(item.UserGUID)).ToListAsync())
            {
                names[user.UserGUID] = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
            }
        }

        return names;
    }
}
