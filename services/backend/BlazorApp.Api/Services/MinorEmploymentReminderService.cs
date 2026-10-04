using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

public sealed class MinorEmploymentReminderService(
    SqlSugarContext context, ICurrentUserService currentUser, ICurrentUserManageableStoreScopeService storeScope)
{
    /// <summary>由排班/打卡完成后调用；调用方须捕获提醒留档故障，不能回滚真实考勤或阻断发布。</summary>
    public static async Task RecordAsync(ISqlSugarClient db, string userGuid, string storeCode, string? scheduleGuid,
        string trigger, MinorEmploymentComplianceEvaluationDto evaluation, string actor)
    {
        var phase = trigger.Contains("punch", StringComparison.Ordinal) || trigger.Contains("actual", StringComparison.Ordinal) ? "actual" : "planned";
        var currentFingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in evaluation.Findings.DistinctBy(x => (x.RuleId, x.WorkDate)))
        {
            var identity = $"{userGuid}|{storeCode}|{scheduleGuid}|{phase}|{finding.RuleId}|{finding.WorkDate:yyyy-MM-dd}";
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            currentFingerprints.Add(fingerprint);
            var row = await db.Queryable<EmployeeMinorReminder>().FirstAsync(x => x.Fingerprint == fingerprint && !x.IsDeleted);
            if (row is not null)
            {
                // 重复刷新保留经理的知悉/升级状态；最新计算事实仍继续显示，确认不等于风险解除。
                await db.Updateable<EmployeeMinorReminder>().SetColumns(x => new EmployeeMinorReminder {
                    Message = finding.Message, ActualMinutes = finding.ActualMinutes, LimitMinutes = finding.LimitMinutes,
                    Severity = finding.Severity, UpdatedAt = DateTime.UtcNow,
                }).Where(x => x.Id == row.Id).ExecuteCommandAsync();
                // 状态由经理动作独立更新；不能用查询时的旧状态覆盖同时发生的知悉/升级。
                if (row.Status == "resolved")
                {
                    var reopened = await db.Updateable<EmployeeMinorReminder>()
                        .SetColumns(x => new EmployeeMinorReminder { Status = "open", Revision = x.Revision + 1 })
                        .Where(x => x.Id == row.Id && x.Revision == row.Revision && x.Status == "resolved").ExecuteCommandAsync();
                    if (reopened == 1)
                        await db.Insertable(new EmployeeMinorReminderEvent { ReminderId = row.Id, Action = "reopened", Actor = actor }).ExecuteCommandAsync();
                }
                continue;
            }
            row = new EmployeeMinorReminder {
                Fingerprint = fingerprint, UserGUID = userGuid, StoreCode = storeCode, ScheduleGuid = scheduleGuid,
                RuleId = finding.RuleId, Severity = finding.Severity, RuleCategory = finding.RuleCategory, Message = finding.Message,
                SourceUrl = finding.SourceUrl, WorkDate = finding.WorkDate, ActualMinutes = finding.ActualMinutes,
                LimitMinutes = finding.LimitMinutes, Trigger = trigger, CreatedBy = actor,
            };
            try { row.Id = await db.Insertable(row).ExecuteReturnIdentityAsync(); }
            catch
            {
                // 并发评估由唯一指纹去重；数据库真实故障必须交回调用方记录。
                if (await db.Queryable<EmployeeMinorReminder>().AnyAsync(x => x.Fingerprint == fingerprint)) continue;
                throw;
            }
            await db.Insertable(new EmployeeMinorReminderEvent { ReminderId = row.Id, Action = "detected", Actor = actor }).ExecuteCommandAsync();
        }
        // 仅结清本次明确重新评估的具体班次。打卡的无班次评估不能误清除其他日期的待办。
        if (!string.IsNullOrWhiteSpace(scheduleGuid))
        {
            var previous = await db.Queryable<EmployeeMinorReminder>().Where(x => !x.IsDeleted && x.UserGUID == userGuid && x.StoreCode == storeCode && x.ScheduleGuid == scheduleGuid && x.Status != "resolved").ToListAsync();
            foreach (var old in previous.Where(x => (x.Trigger.Contains("punch", StringComparison.Ordinal) || x.Trigger.Contains("actual", StringComparison.Ordinal) ? "actual" : "planned") == phase && !currentFingerprints.Contains(x.Fingerprint)))
            {
                var changed = await db.Updateable<EmployeeMinorReminder>().SetColumns(x => new EmployeeMinorReminder { Status = "resolved", Revision = x.Revision + 1, UpdatedAt = DateTime.UtcNow })
                    .Where(x => x.Id == old.Id && x.Revision == old.Revision).ExecuteCommandAsync();
                if (changed == 1) await db.Insertable(new EmployeeMinorReminderEvent { ReminderId = old.Id, Action = "resolved", Actor = actor, Comment = "本次班次重新计算已无此项提醒" }).ExecuteCommandAsync();
            }
        }
    }

    public async Task<ApiResponse<PagedResult<EmployeeMinorReminderDto>>> ListAsync(string? storeCode, string? status, int page, int pageSize)
    {
        var scope = await storeScope.GetScopeAsync();
        if (!scope.IsAllowed || !scope.IsAuthenticated) return ApiResponse<PagedResult<EmployeeMinorReminderDto>>.Error("无权查看门店提醒", "FORBIDDEN");
        if (!string.IsNullOrWhiteSpace(storeCode) && !scope.CanAccessStoreCode(storeCode)) return ApiResponse<PagedResult<EmployeeMinorReminderDto>>.Error("无权查看该门店提醒", "FORBIDDEN");
        var q = context.Db.Queryable<EmployeeMinorReminder>().Where(x => !x.IsDeleted);
        if (!scope.IsAdmin) q = q.Where(x => scope.StoreCodes.Contains(x.StoreCode));
        if (!string.IsNullOrWhiteSpace(storeCode)) q = q.Where(x => x.StoreCode == storeCode);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var rows = await q.OrderBy(x => x.CreatedAt, OrderByType.Desc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var userIds = rows.Select(r => r.UserGUID).Distinct().ToArray();
        var names = userIds.Length == 0 ? new List<User>() : await context.Db.Queryable<User>().Where(x => userIds.Contains(x.UserGUID) && !x.IsDeleted).ToListAsync();
        var result = rows.Select(row => Map(row, names.FirstOrDefault(x => x.UserGUID == row.UserGUID)?.FullName)).ToList();
        return ApiResponse<PagedResult<EmployeeMinorReminderDto>>.OK(new() { Items = result, Page = page, PageSize = pageSize, Total = total });
    }

    public async Task<ApiResponse<EmployeeMinorReminderDto>> ActAsync(int id, EmployeeMinorReminderActionDto dto)
    {
        if (dto.Action is not ("acknowledge" or "escalate")) return ApiResponse<EmployeeMinorReminderDto>.Error("提醒操作无效", "INVALID_ACTION");
        var scope = await storeScope.GetScopeAsync();
        var row = await context.Db.Queryable<EmployeeMinorReminder>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null || !scope.IsAllowed || !scope.IsAuthenticated || !scope.CanAccessStoreCode(row.StoreCode)) return ApiResponse<EmployeeMinorReminderDto>.Error("提醒不存在或无权访问", "FORBIDDEN");
        if (row.Revision != dto.ExpectedRevision) return ApiResponse<EmployeeMinorReminderDto>.Error("提醒状态已变化，请刷新", "REMINDER_VERSION_CONFLICT");
        if (row.Status == "resolved") return ApiResponse<EmployeeMinorReminderDto>.Error("该提醒已解除，请刷新", "REMINDER_RESOLVED");
        var actor = currentUser.GetCurrentUsername();
        await context.Db.Ado.BeginTranAsync();
        try
        {
            var changed = await context.Db.Updateable<EmployeeMinorReminder>().SetColumns(x => new EmployeeMinorReminder {
                Status = dto.Action == "escalate" ? "escalated" : "acknowledged", ActionActor = actor,
                ActionComment = dto.Comment, ActionAtUtc = DateTime.UtcNow, Revision = x.Revision + 1,
            }).Where(x => x.Id == id && x.Revision == dto.ExpectedRevision).ExecuteCommandAsync();
            if (changed != 1) { await context.Db.Ado.RollbackTranAsync(); return ApiResponse<EmployeeMinorReminderDto>.Error("提醒状态已变化，请刷新", "REMINDER_VERSION_CONFLICT"); }
            await context.Db.Insertable(new EmployeeMinorReminderEvent { ReminderId = id, Action = dto.Action, Actor = actor, Comment = dto.Comment }).ExecuteCommandAsync();
            await context.Db.Ado.CommitTranAsync();
        }
        catch { await context.Db.Ado.RollbackTranAsync(); throw; }
        row = await context.Db.Queryable<EmployeeMinorReminder>().InSingleAsync(id);
        return ApiResponse<EmployeeMinorReminderDto>.OK(Map(row, null), dto.Action == "escalate" ? "已标记为需要上级跟进" : "已记录知悉，风险提醒继续保留");
    }

    private static EmployeeMinorReminderDto Map(EmployeeMinorReminder row, string? name) => new() {
        Id = row.Id, UserGUID = row.UserGUID, EmployeeName = name, StoreCode = row.StoreCode, ScheduleGuid = row.ScheduleGuid,
        RuleId = row.RuleId, Severity = row.Severity, RuleCategory = row.RuleCategory, Message = row.Message, SourceUrl = row.SourceUrl,
        WorkDate = row.WorkDate, ActualMinutes = row.ActualMinutes, LimitMinutes = row.LimitMinutes, Trigger = row.Trigger,
        Status = row.Status, Revision = row.Revision, CreatedAt = row.CreatedAt, ActionActor = row.ActionActor,
        ActionComment = row.ActionComment, ActionAtUtc = row.ActionAtUtc,
    };
}
