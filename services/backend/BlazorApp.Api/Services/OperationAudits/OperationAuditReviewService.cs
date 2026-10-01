using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.OperationAudits;

/// <summary>
/// 新收银异常操作核查，规则与老收银一致：只对命中有效异常规则的事件开放，分店范围与查看一致；
/// 当前结论一行一条（乐观并发版本号），每次标记、改判、撤销都追加一条历史流水。
/// 用条件更新（WHERE Version = 期望值）实现并发控制，SQL Server 与测试用的 SQLite 行为一致。
/// </summary>
public sealed class OperationAuditReviewService
{
    public const int MaxNoteLength = 500;

    private readonly ISqlSugarClient _db;
    private readonly OperationAuditQueryService _queryService;
    private readonly ICurrentUserService _currentUser;

    public OperationAuditReviewService(
        ISqlSugarClient db,
        OperationAuditQueryService queryService,
        ICurrentUserService currentUser
    )
    {
        _db = db;
        _queryService = queryService;
        _currentUser = currentUser;
    }

    public async Task<LegacyEmployeeLogResult<OperationAuditReviewDto>> ReviewAsync(
        OperationAuditReviewRequestDto request,
        DateTime? utcNow = null
    )
    {
        if (request.EventId is not { } eventId || eventId == Guid.Empty)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Invalid("事件编号无效");
        }
        byte? result = request.Result?.Trim() switch
        {
            "normal" => OperationAuditQueryService.ReviewNormal,
            "followUp" => OperationAuditQueryService.ReviewFollowUp,
            "revoked" => OperationAuditQueryService.ReviewRevoked,
            _ => null,
        };
        if (result is not { } resultCode)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Invalid("核查结论只能是 normal、followUp 或 revoked");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Invalid($"核查备注最长 {MaxNoteLength} 个字符");
        }
        var userId = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Forbidden();
        }
        userId = userId.Length > 50 ? userId[..50] : userId;
        var userName = _currentUser.GetCurrentUsername() ?? string.Empty;
        userName = userName.Length > 100 ? userName[..100] : userName;

        var (status, target) = await _queryService.FindAccessibleAsync(eventId);
        if (status == OperationAuditDetailAccessStatus.NotFound || target == null)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.NotFound();
        }
        if (status == OperationAuditDetailAccessStatus.Forbidden)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Forbidden();
        }
        // 撤销不要求仍有命中（规则口径变化后也能撤销旧结论）；标记新结论必须有有效命中。
        if (resultCode != OperationAuditQueryService.ReviewRevoked
            && !await _db.Queryable<PosOperationAuditFlag>().AnyAsync(flag => flag.EventId == eventId && flag.RetractedAtUtc == null))
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Invalid("该记录没有命中异常规则，无需核查");
        }

        var now = utcNow ?? DateTime.UtcNow;
        var outcome = await WriteAsync(target, resultCode, note, request.ExpectedVersion, userId, userName, now);
        if (outcome == WriteOutcome.RevokeUnreviewed)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Invalid("该记录尚未核查，无需撤销");
        }
        if (outcome == WriteOutcome.Conflict)
        {
            return LegacyEmployeeLogResult<OperationAuditReviewDto>.Conflict();
        }
        var current = await _db.Queryable<PosOperationAuditReview>().FirstAsync(review => review.EventId == eventId);
        // 撤销后 Result = 0，接口仍返回版本号，便于前端继续改判。
        return LegacyEmployeeLogResult<OperationAuditReviewDto>.Ok(OperationAuditQueryService.MapReview(current) ?? new OperationAuditReviewDto
        {
            Result = "revoked",
            Note = current?.Note,
            ReviewedByName = current?.ReviewedByName ?? userName,
            ReviewedAtUtc = DateTime.SpecifyKind(current?.ReviewedAtUtc ?? now, DateTimeKind.Utc),
            Version = current?.Version ?? 0,
        });
    }

    private enum WriteOutcome
    {
        Ok,
        Conflict,
        RevokeUnreviewed,
    }

    private async Task<WriteOutcome> WriteAsync(
        PosOperationAudit target,
        byte result,
        string? note,
        int? expectedVersion,
        string userId,
        string userName,
        DateTime nowUtc
    )
    {
        var current = await _db.Queryable<PosOperationAuditReview>().FirstAsync(review => review.EventId == target.EventId);
        if (current == null && result == OperationAuditQueryService.ReviewRevoked)
        {
            return WriteOutcome.RevokeUnreviewed;
        }
        if (current == null ? (expectedVersion ?? 0) != 0 : expectedVersion != current.Version)
        {
            return WriteOutcome.Conflict;
        }

        try
        {
            await _db.Ado.BeginTranAsync();
            if (current == null)
            {
                // 并发首次核查时主键冲突，落到下面的 catch 按冲突处理。
                await _db.Insertable(new PosOperationAuditReview
                {
                    EventId = target.EventId,
                    StoreCode = target.StoreCode,
                    OccurredAtUtc = target.OccurredAtUtc,
                    Result = result,
                    Note = note,
                    ReviewedByUserId = userId,
                    ReviewedByName = userName,
                    ReviewedAtUtc = nowUtc,
                    Version = 1,
                }).ExecuteCommandAsync();
            }
            else
            {
                var expected = current.Version;
                var updated = await _db.Updateable<PosOperationAuditReview>()
                    .SetColumns(review => new PosOperationAuditReview
                    {
                        Result = result,
                        Note = note,
                        ReviewedByUserId = userId,
                        ReviewedByName = userName,
                        ReviewedAtUtc = nowUtc,
                        Version = expected + 1,
                    })
                    .Where(review => review.EventId == target.EventId && review.Version == expected)
                    .ExecuteCommandAsync();
                if (updated != 1)
                {
                    await _db.Ado.RollbackTranAsync();
                    return WriteOutcome.Conflict;
                }
            }
            await _db.Insertable(new PosOperationAuditReviewHistory
            {
                EventId = target.EventId,
                Result = result,
                Note = note,
                ActorUserId = userId,
                ActorName = userName,
                CreatedAtUtc = nowUtc,
            }).ExecuteCommandAsync();
            await _db.Ado.CommitTranAsync();
            return WriteOutcome.Ok;
        }
        catch (Exception) when (current == null)
        {
            await _db.Ado.RollbackTranAsync();
            return WriteOutcome.Conflict;
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }
}
