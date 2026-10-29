using System.Globalization;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobEmployeeSyncService
{
    Task<ApiResponse<MyobSyncFieldStatsDto>> GetFieldStatsAsync(
        MyobSyncScopeRequest request, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobSyncPreviewDto>> PreviewAsync(
        MyobSyncPreviewRequest request, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobSyncApplyResultDto>> ApplyAsync(
        MyobSyncApplyRequest request, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<List<MyobSyncBatchDto>>> ListBatchesAsync(
        string connectionGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobSyncUndoResultDto>> UndoAsync(
        string batchGuid, string actorUserGuid, CancellationToken cancellationToken);
}

/// <summary>撤销快照里的一项：写入前（Before）与写入后（After）的字段值字典，键见 <see cref="MyobEmployeeSyncRules.CurrentValues"/>。</summary>
internal sealed record SyncSnapshotItem(
    string UserGuid,
    string Field,
    Dictionary<string, string?> Before,
    Dictionary<string, string?> After);

/// <summary>
/// 从 MYOB 同步资料到我方（设计：预览 → 勾选 → 写入 → 可撤销）。
/// 只读 MYOB；只写我方员工资料，并且只写「已绑定且 MYOB 卡仍存在」的员工。
/// 写入一律经 <see cref="IEmployeeProfileService.UpsertAdminAsync"/>（与管理员手改资料同一条路径：敏感资料锁、版本号、待审申请作废都沿用），
/// 姓名走 <see cref="UserNameParts"/>（与用户管理同一规则）。
/// 安全约束：①前端只传字段键和哈希，值由服务端重新从 MYOB 取；②写入前比对「我方现值 / MYOB 值」哈希，预览之后变了就不写；
/// ③每次成功写入留快照，7 天内可整批撤销（只还原本次写入、且之后没被改过的字段）。
/// </summary>
public sealed class MyobEmployeeSyncService : IMyobEmployeeSyncService
{
    internal const int UndoDays = 7;
    private const int InChunk = 500;
    private const int MaxBatchList = 30;

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private readonly ISqlSugarClient _db;
    private readonly IMyobEmployeeDirectory _directory;
    private readonly IMyobEmployeePaymentSource _payments;
    private readonly IEmployeeProfileService _profiles;
    private readonly Func<string, Task<bool>> _canEditProfiles;
    private readonly TimeProvider _time;

    public MyobEmployeeSyncService(
        SqlSugarContext context,
        IMyobEmployeeDirectory directory,
        IMyobEmployeePaymentSource payments,
        IEmployeeProfileService profiles,
        IRoleService roles)
        : this(
            context.Db,
            directory,
            payments,
            profiles,
            async userGuid => (await roles.UserHasPermissionAsync(userGuid, Permissions.EmployeeProfiles.Edit)).Data == true)
    {
    }

    internal MyobEmployeeSyncService(
        ISqlSugarClient db,
        IMyobEmployeeDirectory directory,
        IMyobEmployeePaymentSource payments,
        IEmployeeProfileService profiles,
        Func<string, Task<bool>> canEditProfiles,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _directory = directory;
        _payments = payments;
        _profiles = profiles;
        _canEditProfiles = canEditProfiles;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 第 1 步：字段统计 ─────────────────────────

    public async Task<ApiResponse<MyobSyncFieldStatsDto>> GetFieldStatsAsync(
        MyobSyncScopeRequest request, string actorUserGuid, CancellationToken cancellationToken)
    {
        var load = await LoadContextAsync(request.ConnectionGuid, request.UserGuids, request.Refresh, needBank: true, cancellationToken);
        if (load.Error is not null)
        {
            return ApiResponse<MyobSyncFieldStatsDto>.Error(load.Error.Value.Message, load.Error.Value.Code);
        }

        var ctx = load.Context!;
        var stats = MyobSyncFields.All.ToDictionary(
            field => field,
            field => new MyobSyncFieldStatDto
            {
                Field = field,
                Sensitive = MyobSyncFields.IsSensitive(field),
                DefaultSelected = MyobSyncFields.IsDefaultSelected(field),
            });
        var missing = 0;
        foreach (var bound in ctx.Bound)
        {
            if (!ctx.Cards.TryGetValue(bound.MyobEmployeeUid, out var card))
            {
                missing++;
                continue;
            }

            var myob = ctx.MyobValuesOf(card, bound.MyobEmployeeUid);
            var our = ctx.Ours[bound.UserGuid];
            // 「MYOB 有值」＝拿一个全空的我方资料去比，能产生差异就说明 MYOB 有值。
            var empty = ctx.EmptyOur(bound.UserGuid);
            foreach (var field in MyobSyncFields.All)
            {
                if (MyobEmployeeSyncRules.ComputeField(field, empty, myob) is null)
                {
                    continue;
                }

                stats[field].MyobHasValue++;
                var diff = MyobEmployeeSyncRules.ComputeField(field, our, myob);
                if (diff is null)
                {
                    continue;
                }

                if (diff.Kind == MyobSyncChangeKinds.Fill)
                {
                    stats[field].Fillable++;
                }
                else
                {
                    stats[field].Differs++;
                }
            }
        }

        return ApiResponse<MyobSyncFieldStatsDto>.OK(new MyobSyncFieldStatsDto
        {
            ConnectionGuid = request.ConnectionGuid,
            FetchedAt = ctx.FetchedAt,
            BoundCount = ctx.Bound.Count,
            MissingCardCount = missing,
            Fields = MyobSyncFields.All.Select(field => stats[field]).ToList(),
            CanWrite = await _canEditProfiles(actorUserGuid),
            UnavailableFields = ctx.UnavailableFields.ToList(),
        });
    }

    // ───────────────────────── 第 2 步：预览 ─────────────────────────

    public async Task<ApiResponse<MyobSyncPreviewDto>> PreviewAsync(
        MyobSyncPreviewRequest request, string actorUserGuid, CancellationToken cancellationToken)
    {
        if (!await _canEditProfiles(actorUserGuid))
        {
            return ApiResponse<MyobSyncPreviewDto>.Error("没有修改员工资料的权限", MyobSyncErrorCodes.NoPermission);
        }

        var fields = NormalizeFields(request.Fields);
        if (fields.Count == 0)
        {
            return ApiResponse<MyobSyncPreviewDto>.Error("请至少选择一个字段", MyobSyncErrorCodes.NoFields);
        }

        var mode = request.Mode == MyobSyncModes.Overwrite ? MyobSyncModes.Overwrite : MyobSyncModes.FillEmpty;
        var load = await LoadContextAsync(
            request.ConnectionGuid, request.UserGuids, request.Refresh, needBank: fields.Contains(MyobSyncFields.Bank), cancellationToken);
        if (load.Error is not null)
        {
            return ApiResponse<MyobSyncPreviewDto>.Error(load.Error.Value.Message, load.Error.Value.Code);
        }

        var ctx = load.Context!;
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(_time.GetUtcNow().UtcDateTime.Date);
        var summary = new MyobSyncPreviewSummaryDto();
        var employees = new List<MyobSyncEmployeeDto>();
        foreach (var bound in ctx.Bound)
        {
            if (!ctx.Cards.TryGetValue(bound.MyobEmployeeUid, out var card))
            {
                summary.SkippedMissingCard++;
                continue;
            }

            var our = ctx.Ours[bound.UserGuid];
            var myob = ctx.MyobValuesOf(card, bound.MyobEmployeeUid);
            var changes = MyobEmployeeSyncRules.Compute(our, myob)
                .Where(diff => fields.Contains(diff.Field))
                .Select(diff => ToChangeDto(diff, mode))
                .ToList();
            if (changes.Count == 0)
            {
                summary.SkippedNoDiff++;
                continue;
            }

            employees.Add(new MyobSyncEmployeeDto
            {
                UserGuid = bound.UserGuid,
                UserName = our.DisplayName,
                StoreLabels = workforce.StoreLabelsOf(bound.UserGuid, request.ConnectionGuid),
                EmployeeType = our.EmployeeType?.ToString(),
                HasPendingSensitiveRequest = our.HasPendingSensitiveRequest && changes.Any(change => change.Sensitive),
                SensitiveRevision = our.SensitiveRevision,
                Changes = changes,
            });
        }

        employees = employees.OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase).ToList();
        summary.EmployeeCount = employees.Count;
        summary.ChangeCount = employees.Sum(item => item.Changes.Count);
        summary.DefaultCheckedCount = employees.Sum(item => item.Changes.Count(change => change.DefaultChecked));
        summary.OverwriteCount = employees.Sum(item => item.Changes.Count(change => change.Kind == MyobSyncChangeKinds.Overwrite));
        summary.SensitiveCount = employees.Sum(item => item.Changes.Count(change => change.Sensitive));
        summary.PendingSensitiveRequestCount = employees.Count(item => item.HasPendingSensitiveRequest);

        return ApiResponse<MyobSyncPreviewDto>.OK(new MyobSyncPreviewDto
        {
            ConnectionGuid = request.ConnectionGuid,
            FetchedAt = ctx.FetchedAt,
            Mode = mode,
            Fields = fields.ToList(),
            Summary = summary,
            Employees = employees,
            UnavailableFields = ctx.UnavailableFields.Where(fields.Contains).ToList(),
        });
    }

    private static MyobSyncChangeDto ToChangeDto(SyncDiff diff, string mode) => new()
    {
        Field = diff.Field,
        Sensitive = MyobSyncFields.IsSensitive(diff.Field),
        OurDisplay = diff.OurDisplay,
        MyobDisplay = diff.MyobDisplay,
        Kind = diff.Kind,
        DefaultChecked = diff.Kind == MyobSyncChangeKinds.Fill || mode == MyobSyncModes.Overwrite,
        Note = diff.Note,
        OurHash = MyobEmployeeSyncRules.Hash(diff.Field, "our", diff.OurNorm),
        MyobHash = MyobEmployeeSyncRules.Hash(diff.Field, "myob", diff.MyobNorm),
    };

    // ───────────────────────── 写入 ─────────────────────────

    public async Task<ApiResponse<MyobSyncApplyResultDto>> ApplyAsync(
        MyobSyncApplyRequest request, string actorUserGuid, CancellationToken cancellationToken)
    {
        if (!await _canEditProfiles(actorUserGuid))
        {
            return ApiResponse<MyobSyncApplyResultDto>.Error("没有修改员工资料的权限", MyobSyncErrorCodes.NoPermission);
        }

        var items = request.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.UserGuid) && item.Changes.Count > 0)
            .GroupBy(item => item.UserGuid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (items.Count == 0)
        {
            return ApiResponse<MyobSyncApplyResultDto>.Error("没有要写入的变更", MyobSyncErrorCodes.NoFields);
        }

        var needBank = items.Any(item => item.Changes.Any(change => change.Field == MyobSyncFields.Bank));
        var load = await LoadContextAsync(
            request.ConnectionGuid, items.Select(item => item.UserGuid).ToList(), refresh: false, needBank, cancellationToken);
        if (load.Error is not null)
        {
            return ApiResponse<MyobSyncApplyResultDto>.Error(load.Error.Value.Message, load.Error.Value.Code);
        }

        var ctx = load.Context!;
        var result = new MyobSyncApplyResultDto();
        var snapshot = new List<SyncSnapshotItem>();
        var boundByUser = ctx.Bound.ToDictionary(item => item.UserGuid, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedFields = item.Changes.Select(change => change.Field).Distinct().ToList();
            var name = ctx.Ours.TryGetValue(item.UserGuid, out var ourForName) ? ourForName.DisplayName : item.UserGuid;

            if (!boundByUser.TryGetValue(item.UserGuid, out var bound) || !ctx.Ours.TryGetValue(item.UserGuid, out var our))
            {
                result.Failed.Add(Fail(item.UserGuid, name, requestedFields, MyobSyncErrorCodes.NotBound, "该员工已不再绑定本公司的 MYOB 员工卡"));
                continue;
            }

            if (!ctx.Cards.TryGetValue(bound.MyobEmployeeUid, out var card))
            {
                result.Failed.Add(Fail(item.UserGuid, name, requestedFields, MyobSyncErrorCodes.CardMissing, "MYOB 里已找不到这张员工卡"));
                continue;
            }

            var current = MyobEmployeeSyncRules.Compute(our, ctx.MyobValuesOf(card, bound.MyobEmployeeUid))
                .ToDictionary(diff => diff.Field);
            var accepted = new List<SyncDiff>();
            var rejected = new Dictionary<string, (string Code, string Message)>();
            foreach (var change in item.Changes.GroupBy(change => change.Field).Select(group => group.First()))
            {
                if (!MyobSyncFields.All.Contains(change.Field))
                {
                    rejected[change.Field] = (MyobSyncErrorCodes.WriteFailed, "不支持的字段");
                }
                else if (ctx.UnavailableFields.Contains(change.Field))
                {
                    rejected[change.Field] = (MyobSyncErrorCodes.WriteFailed, "MYOB 数据暂时读取不到，请稍后重试");
                }
                else if (!current.TryGetValue(change.Field, out var diff)
                    || diff.OurHash(change.Field) != change.OurHash
                    || diff.MyobHash(change.Field) != change.MyobHash)
                {
                    rejected[change.Field] = (MyobSyncErrorCodes.ValueChanged, "预览之后这一项已经变化（我方或 MYOB 的值不同了），请重新预览");
                }
                else
                {
                    accepted.Add(diff);
                }
            }

            // 敏感字段：预览之后敏感资料版本变了就一律不写；有待审申请而用户没确认作废，也不写。
            if (accepted.Any(diff => MyobSyncFields.IsSensitive(diff.Field)))
            {
                if (our.SensitiveRevision != item.ExpectedSensitiveRevision)
                {
                    RejectSensitive(accepted, rejected, MyobSyncErrorCodes.VersionConflict, "敏感资料在预览之后被别人修改了，请重新预览");
                }
                else if (our.HasPendingSensitiveRequest && !request.ConfirmSupersedePendingSensitiveRequests)
                {
                    RejectSensitive(accepted, rejected, MyobSyncErrorCodes.PendingRequestNeedsConfirm, "该员工有待审的敏感资料申请，需要确认作废后才能写入");
                }
            }

            if (accepted.Count > 0)
            {
                await WriteEmployeeAsync(item, our, accepted, rejected, snapshot, request.ConfirmSupersedePendingSensitiveRequests);
            }

            var written = accepted.Where(diff => !rejected.ContainsKey(diff.Field)).Select(diff => diff.Field).ToList();
            if (written.Count > 0)
            {
                result.Written.Add(new MyobSyncWrittenDto { UserGuid = item.UserGuid, UserName = name, Fields = written });
                result.WrittenChangeCount += written.Count;
                result.WrittenSensitiveCount += written.Count(MyobSyncFields.IsSensitive);
            }

            foreach (var group in rejected.GroupBy(pair => pair.Value.Code))
            {
                result.Failed.Add(Fail(
                    item.UserGuid, name, group.Select(pair => pair.Key).ToList(), group.Key, group.First().Value.Message));
            }
        }

        result.WrittenEmployeeCount = result.Written.Count;
        if (snapshot.Count > 0)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var batch = new MyobEmployeeSyncBatch
            {
                ConnectionGuid = request.ConnectionGuid,
                ActorUserGuid = actorUserGuid,
                CreatedAt = now,
                EmployeeCount = result.WrittenEmployeeCount,
                ChangeCount = result.WrittenChangeCount,
                SensitiveCount = result.WrittenSensitiveCount,
                ChangesJson = JsonSerializer.Serialize(snapshot, SnapshotJson),
            };
            await _db.Insertable(batch).ExecuteCommandAsync();
            result.BatchGuid = batch.BatchGuid;
            result.UndoDeadline = now.AddDays(UndoDays);
        }

        return ApiResponse<MyobSyncApplyResultDto>.OK(result);
    }

    private static void RejectSensitive(
        List<SyncDiff> accepted,
        Dictionary<string, (string Code, string Message)> rejected,
        string code,
        string message)
    {
        foreach (var diff in accepted.Where(item => MyobSyncFields.IsSensitive(item.Field)).ToList())
        {
            rejected[diff.Field] = (code, message);
        }
    }

    /// <summary>
    /// 写一个员工：资料类字段合并成一次 UpsertAdminAsync（读现值 → 叠加改动 → 整份保存，因为该接口是「未传即清空」语义），
    /// 姓名单独走用户表。成功的字段追加到快照；失败的字段记进 rejected。
    /// </summary>
    private async Task WriteEmployeeAsync(
        MyobSyncApplyEmployeeItem item,
        SyncOurValues our,
        List<SyncDiff> accepted,
        Dictionary<string, (string Code, string Message)> rejected,
        List<SyncSnapshotItem> snapshot,
        bool confirmSupersede)
    {
        var profileDiffs = accepted
            .Where(diff => diff.Field != MyobSyncFields.Name && !rejected.ContainsKey(diff.Field))
            .ToList();
        if (profileDiffs.Count > 0)
        {
            var failure = await WriteProfileFieldsAsync(
                item.UserGuid, profileDiffs.ToDictionary(diff => diff.Field, diff => diff.After),
                item.ExpectedSensitiveRevision, confirmSupersede);
            if (failure is not null)
            {
                foreach (var diff in profileDiffs)
                {
                    rejected[diff.Field] = failure.Value;
                }
            }
        }

        var nameDiff = accepted.FirstOrDefault(diff => diff.Field == MyobSyncFields.Name && !rejected.ContainsKey(diff.Field));
        if (nameDiff is not null)
        {
            var error = await WriteNameAsync(item.UserGuid, nameDiff.After);
            if (error is not null)
            {
                rejected[MyobSyncFields.Name] = (MyobSyncErrorCodes.WriteFailed, error);
            }
        }

        foreach (var diff in accepted.Where(diff => !rejected.ContainsKey(diff.Field)))
        {
            snapshot.Add(new SyncSnapshotItem(
                item.UserGuid, diff.Field, MyobEmployeeSyncRules.CurrentValues(diff.Field, our), new Dictionary<string, string?>(diff.After)));
        }
    }

    /// <summary>把若干资料字段（字段键 → 目标值字典）写入员工资料。成功返回 null，失败返回（错误码, 文案）。</summary>
    private async Task<(string Code, string Message)?> WriteProfileFieldsAsync(
        string userGuid,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> values,
        int expectedSensitiveRevision,
        bool confirmSupersede)
    {
        var detail = await _profiles.GetAdminDetailAsync(userGuid);
        if (!detail.Success || detail.Data is null)
        {
            return (MyobSyncErrorCodes.WriteFailed, detail.Message ?? "读取员工资料失败");
        }

        var dto = BuildUpsertDto(detail.Data, expectedSensitiveRevision, confirmSupersede);
        foreach (var (field, map) in values)
        {
            ApplyValues(dto, field, map);
        }

        var response = await _profiles.UpsertAdminAsync(userGuid, dto);
        if (response.Success)
        {
            return null;
        }

        return response.ErrorCode switch
        {
            EmployeeProfileSensitiveChangeService.VersionConflictCode =>
                (MyobSyncErrorCodes.VersionConflict, "敏感资料在预览之后被别人修改了，请重新预览"),
            EmployeeProfileService.PendingChangeConfirmationRequiredCode =>
                (MyobSyncErrorCodes.PendingRequestNeedsConfirm, "该员工有待审的敏感资料申请，需要确认作废后才能写入"),
            _ => (MyobSyncErrorCodes.WriteFailed, response.Message ?? "写入员工资料失败"),
        };
    }

    /// <summary>用员工现有资料构造「整份保存」DTO：没改动的字段原样带回，避免被「未传即清空」冲掉。</summary>
    private static EmployeeProfileUpsertDto BuildUpsertDto(
        EmployeeProfileDetailDto detail, int expectedSensitiveRevision, bool confirmSupersede) => new()
    {
        UserGUID = detail.UserGUID,
        Phone = detail.Phone,
        BankBsb = detail.BankBsb,
        BankAccountNumber = detail.BankAccountNumber,
        SuperannuationCompanyName = detail.SuperannuationCompanyName,
        SuperannuationCompanyCode = detail.SuperannuationCompanyCode,
        SuperannuationAccountNumber = detail.SuperannuationAccountNumber,
        Birthday = detail.Birthday,
        Gender = detail.Gender,
        EmploymentType = detail.EmploymentType,
        AvatarUrl = detail.AvatarUrl,
        IdentityId = detail.IdentityId,
        IdentityType = detail.IdentityType,
        IdentityPhotoUrl = detail.IdentityPhotoUrl,
        Address = detail.Address,
        ExpectedSensitiveRevision = expectedSensitiveRevision,
        ConfirmSupersedePendingSensitiveChangeRequest = confirmSupersede ? true : null,
    };

    /// <summary>把一个字段的目标值写进 DTO（写入与撤销共用；值为 null 表示清空该字段）。</summary>
    private static void ApplyValues(EmployeeProfileUpsertDto dto, string field, IReadOnlyDictionary<string, string?> map)
    {
        string? Get(string key) => map.TryGetValue(key, out var value) ? value : null;

        switch (field)
        {
            case MyobSyncFields.Phone:
                dto.Phone = Get("phone");
                break;
            case MyobSyncFields.Email:
                // 邮箱是登录与找回密码的渠道，不允许被清空；撤销到空值时保持不动。
                if (Get("email") is { } email)
                {
                    dto.Email = email;
                }

                break;
            case MyobSyncFields.Birthday:
                dto.Birthday = DateTime.TryParseExact(
                    Get("birthday"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : null;
                break;
            case MyobSyncFields.Gender:
                dto.Gender = Get("gender");
                break;
            case MyobSyncFields.Address:
                dto.Address = Get("address");
                break;
            case MyobSyncFields.EmploymentType:
                dto.EmploymentType = Get("employmentType");
                break;
            case MyobSyncFields.Bank:
                dto.BankBsb = Get("bsb");
                dto.BankAccountNumber = Get("account");
                break;
            case MyobSyncFields.Superannuation:
                // 基金代码 MYOB 读不到，保持我方原值；只写基金名称与会员号。
                dto.SuperannuationCompanyName = Get("name");
                dto.SuperannuationAccountNumber = Get("account");
                break;
        }
    }

    /// <summary>
    /// 姓名写入用户表：名 / 中间名 / 姓三段（null=清空）由 <see cref="UserNameParts"/> 合成 FullName。
    /// 旧值只有 FullName、没有三段时（撤销到旧数据），退回只写 FullName 的旧规则。
    /// </summary>
    private async Task<string?> WriteNameAsync(string userGuid, IReadOnlyDictionary<string, string?> map)
    {
        var user = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (user is null)
        {
            return "用户不存在";
        }

        string? Get(string key) => map.TryGetValue(key, out var value) ? value : null;
        var hasParts = Get("given") is not null || Get("middle") is not null || Get("family") is not null;
        var input = hasParts
            ? new UserNameInput(Get("full"), Get("given") ?? string.Empty, Get("middle") ?? string.Empty, Get("family") ?? string.Empty, null)
            : new UserNameInput(Get("full"), null, null, null, null);
        var error = UserNameParts.ApplyOnUpdate(user, input);
        if (error is not null)
        {
            return error;
        }

        user.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.Updateable(user).ExecuteCommandAsync();
        return null;
    }

    private static MyobSyncFailureDto Fail(string userGuid, string userName, List<string> fields, string code, string message) => new()
    {
        UserGuid = userGuid,
        UserName = userName,
        Fields = fields,
        Code = code,
        Message = message,
    };

    // ───────────────────────── 同步记录与撤销 ─────────────────────────

    public async Task<ApiResponse<List<MyobSyncBatchDto>>> ListBatchesAsync(
        string connectionGuid, CancellationToken cancellationToken)
    {
        var batches = await _db.Queryable<MyobEmployeeSyncBatch>()
            .Where(item => item.ConnectionGuid == connectionGuid)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .Take(MaxBatchList)
            .ToListAsync();
        var names = await LoadUserNamesAsync(
            batches.SelectMany(item => new[] { item.ActorUserGuid, item.UndoneByUserGuid })
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Select(guid => guid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
        var now = _time.GetUtcNow().UtcDateTime;
        var list = batches.Select(batch =>
        {
            var deadline = batch.CreatedAt.AddDays(UndoDays);
            var status = batch.UndoneAt is not null
                ? MyobSyncBatchStatuses.Undone
                : now > deadline ? MyobSyncBatchStatuses.Expired : MyobSyncBatchStatuses.Active;
            return new MyobSyncBatchDto
            {
                BatchGuid = batch.BatchGuid,
                CreatedAt = batch.CreatedAt,
                ActorName = batch.ActorUserGuid is not null && names.TryGetValue(batch.ActorUserGuid, out var actor) ? actor : null,
                EmployeeCount = batch.EmployeeCount,
                ChangeCount = batch.ChangeCount,
                SensitiveCount = batch.SensitiveCount,
                Status = status,
                CanUndo = status == MyobSyncBatchStatuses.Active,
                UndoDeadline = deadline,
                UndoneAt = batch.UndoneAt,
                UndoneByName = batch.UndoneByUserGuid is not null && names.TryGetValue(batch.UndoneByUserGuid, out var undoer) ? undoer : null,
            };
        }).ToList();
        return ApiResponse<List<MyobSyncBatchDto>>.OK(list);
    }

    public async Task<ApiResponse<MyobSyncUndoResultDto>> UndoAsync(
        string batchGuid, string actorUserGuid, CancellationToken cancellationToken)
    {
        if (!await _canEditProfiles(actorUserGuid))
        {
            return ApiResponse<MyobSyncUndoResultDto>.Error("没有修改员工资料的权限", MyobSyncErrorCodes.NoPermission);
        }

        var batch = await _db.Queryable<MyobEmployeeSyncBatch>().FirstAsync(item => item.BatchGuid == batchGuid);
        if (batch is null)
        {
            return ApiResponse<MyobSyncUndoResultDto>.Error("同步记录不存在", MyobSyncErrorCodes.BatchNotFound);
        }

        if (batch.UndoneAt is not null)
        {
            return ApiResponse<MyobSyncUndoResultDto>.Error("这次同步已经撤销过了", MyobSyncErrorCodes.BatchAlreadyUndone);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (now > batch.CreatedAt.AddDays(UndoDays))
        {
            return ApiResponse<MyobSyncUndoResultDto>.Error($"超过 {UndoDays} 天，不能再撤销", MyobSyncErrorCodes.BatchExpired);
        }

        var items = JsonSerializer.Deserialize<List<SyncSnapshotItem>>(batch.ChangesJson, SnapshotJson) ?? [];
        var ours = await LoadOursAsync(items.Select(item => item.UserGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var result = new MyobSyncUndoResultDto();

        foreach (var group in items.GroupBy(item => item.UserGuid, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userName = ours.TryGetValue(group.Key, out var our) ? our.DisplayName : group.Key;
            var skipped = new List<string>();
            if (our is null)
            {
                result.SkippedChanged.Add(new MyobSyncWrittenDto { UserGuid = group.Key, UserName = userName, Fields = group.Select(item => item.Field).ToList() });
                continue;
            }

            var restorable = new List<SyncSnapshotItem>();
            foreach (var item in group)
            {
                // 写入之后又被人改过 → 不还原；有待审的敏感资料申请时也不动敏感字段（还原会让那份申请作废）。
                if (!MyobEmployeeSyncRules.SameValues(MyobEmployeeSyncRules.CurrentValues(item.Field, our), item.After)
                    || (MyobSyncFields.IsSensitive(item.Field) && our.HasPendingSensitiveRequest))
                {
                    skipped.Add(item.Field);
                }
                else
                {
                    restorable.Add(item);
                }
            }

            var profileItems = restorable.Where(item => item.Field != MyobSyncFields.Name).ToList();
            var restoredFields = new List<string>();
            if (profileItems.Count > 0)
            {
                var failure = await WriteProfileFieldsAsync(
                    group.Key,
                    profileItems.ToDictionary(item => item.Field, item => (IReadOnlyDictionary<string, string?>)item.Before),
                    our.SensitiveRevision,
                    confirmSupersede: false);
                if (failure is null)
                {
                    restoredFields.AddRange(profileItems.Select(item => item.Field));
                }
                else
                {
                    skipped.AddRange(profileItems.Select(item => item.Field));
                }
            }

            var nameItem = restorable.FirstOrDefault(item => item.Field == MyobSyncFields.Name);
            if (nameItem is not null)
            {
                if (await WriteNameAsync(group.Key, nameItem.Before) is null)
                {
                    restoredFields.Add(MyobSyncFields.Name);
                }
                else
                {
                    skipped.Add(MyobSyncFields.Name);
                }
            }

            if (restoredFields.Count > 0)
            {
                result.RestoredEmployeeCount++;
                result.RestoredChangeCount += restoredFields.Count;
            }

            if (skipped.Count > 0)
            {
                result.SkippedChanged.Add(new MyobSyncWrittenDto { UserGuid = group.Key, UserName = userName, Fields = skipped });
            }
        }

        // 无论有没有跳过的项，这一批都标记为已撤销（不能反复撤销）。
        batch.UndoneAt = now;
        batch.UndoneByUserGuid = actorUserGuid;
        await _db.Updateable(batch)
            .UpdateColumns(item => new { item.UndoneAt, item.UndoneByUserGuid })
            .ExecuteCommandAsync();
        return ApiResponse<MyobSyncUndoResultDto>.OK(result);
    }

    // ───────────────────────── 读取上下文 ─────────────────────────

    private sealed record BoundEmployee(string UserGuid, string MyobEmployeeUid);

    private sealed class SyncContext
    {
        public DateTime FetchedAt { get; init; }
        public List<BoundEmployee> Bound { get; init; } = [];
        public Dictionary<string, MyobDirectoryCard> Cards { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SyncOurValues> Ours { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, MyobBankAccountRecord>? Banks { get; init; }
        public HashSet<string> UnavailableFields { get; init; } = [];

        public SyncMyobValues MyobValuesOf(MyobDirectoryCard card, string uid)
        {
            MyobBankAccountRecord? bank = null;
            Banks?.TryGetValue(uid, out bank);
            return new SyncMyobValues(
                card.Match.IsIndividual,
                card.Match.FirstName,
                card.Match.LastName,
                card.Match.Emails,
                card.Match.Phones,
                card.Match.DateOfBirth,
                card.Extras?.Gender,
                card.Extras?.Address,
                card.Dto.EmploymentStatus,
                card.Dto.HasAnnualEntitlement || card.Dto.HasSickEntitlement,
                bank?.Bsb,
                bank?.AccountNumber,
                card.Extras?.SuperFundName,
                card.Extras?.SuperMembershipNumber);
        }

        public SyncOurValues EmptyOur(string userGuid) => new(
            userGuid, string.Empty, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 0, false);
    }

    private async Task<(SyncContext? Context, (string Code, string Message)? Error)> LoadContextAsync(
        string connectionGuid,
        IReadOnlyCollection<string>? userGuids,
        bool refresh,
        bool needBank,
        CancellationToken cancellationToken)
    {
        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return (null, ("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive));
        }

        MyobEmployeeDirectoryData directory;
        try
        {
            directory = await _directory.GetAsync(connectionGuid, refresh, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return (null, (ex.Message, ex.Code));
        }

        // 银行账户单独拉取：失败只让「银行」字段不可用，不影响其它字段。
        IReadOnlyDictionary<string, MyobBankAccountRecord>? banks = null;
        var unavailable = new HashSet<string>();
        if (needBank)
        {
            try
            {
                banks = await _payments.GetAsync(connectionGuid, refresh, cancellationToken);
            }
            catch (MyobApiException)
            {
                unavailable.Add(MyobSyncFields.Bank);
            }
        }

        var scope = userGuids is { Count: > 0 }
            ? userGuids.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var links = await _db.Queryable<EmployeeMyobLink>()
            .Where(item => item.ConnectionGuid == connectionGuid && item.UnboundAt == null)
            .ToListAsync();
        var bound = links
            .Where(link => scope is null || scope.Contains(link.UserGuid))
            .Select(link => new BoundEmployee(link.UserGuid, link.MyobEmployeeUid))
            .ToList();

        var ours = await LoadOursAsync(bound.Select(item => item.UserGuid).ToList());
        // 我方已删除 / 不存在的员工不参与同步。
        bound = bound.Where(item => ours.ContainsKey(item.UserGuid)).ToList();

        return (new SyncContext
        {
            FetchedAt = directory.FetchedAt,
            Bound = bound,
            Cards = directory.Cards.ToDictionary(card => card.Dto.Uid, StringComparer.OrdinalIgnoreCase),
            Ours = ours,
            Banks = banks,
            UnavailableFields = unavailable,
        }, null);
    }

    /// <summary>读出一批员工的我方当前资料（用户 + 资料 + 未成年合规生日 + 待审敏感申请）。</summary>
    private async Task<Dictionary<string, SyncOurValues>> LoadOursAsync(IReadOnlyList<string> userGuids)
    {
        var result = new Dictionary<string, SyncOurValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(InChunk))
        {
            var guids = chunk.ToList();
            var users = await _db.Queryable<User>()
                .Where(item => !item.IsDeleted && guids.Contains(item.UserGUID))
                .ToListAsync();
            var profiles = (await _db.Queryable<EmployeeProfile>()
                    .Where(item => !item.IsDeleted && guids.Contains(item.UserGUID))
                    .ToListAsync())
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var minorBirthdays = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var minor in await _db.Queryable<EmployeeMinorCompliance>()
                         .Where(item => !item.IsDeleted && guids.Contains(item.UserGUID))
                         .ToListAsync())
            {
                if (minor.DateOfBirth.HasValue)
                {
                    minorBirthdays[minor.UserGUID] = minor.DateOfBirth.Value;
                }
            }

            var pending = (await _db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .Where(item => item.Status == EmployeeProfileSensitiveChangeStatus.Pending && guids.Contains(item.UserGUID))
                    .ToListAsync())
                .Select(item => item.UserGUID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var user in users)
            {
                profiles.TryGetValue(user.UserGUID, out var profile);
                var birthday = profile?.Birthday ?? (minorBirthdays.TryGetValue(user.UserGUID, out var dob) ? dob : null);
                var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
                result[user.UserGUID] = new SyncOurValues(
                    user.UserGUID,
                    displayName,
                    user.FullName,
                    user.GivenName,
                    user.MiddleName,
                    user.FamilyName,
                    user.Email,
                    profile?.Phone,
                    birthday,
                    profile?.Gender,
                    profile?.Address,
                    profile?.EmployeeType,
                    profile?.BankBSB,
                    profile?.BankACC,
                    profile?.SuperannuationCompanyName,
                    profile?.SuperannuationAccount,
                    profile?.SensitiveRevision ?? 0,
                    pending.Contains(user.UserGUID));
            }
        }

        return result;
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

    /// <summary>只保留认识的字段键并按固定顺序排列；去重。</summary>
    private static HashSet<string> NormalizeFields(IEnumerable<string>? fields) =>
        MyobSyncFields.All.Where(field => fields?.Contains(field) == true).ToHashSet();
}

internal static class SyncDiffHashExtensions
{
    internal static string OurHash(this SyncDiff diff, string field) =>
        MyobEmployeeSyncRules.Hash(field, "our", diff.OurNorm);

    internal static string MyobHash(this SyncDiff diff, string field) =>
        MyobEmployeeSyncRules.Hash(field, "myob", diff.MyobNorm);
}
