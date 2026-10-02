using System.Data.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using SqlSugar;
using Q = BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogSqlServerQuery;
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs;

/// <summary>
/// 异常操作核查：只对命中有效异常规则的记录开放；分店范围与查看一致。
/// 当前结论一行一条（乐观并发版本号），每次标记、改判、撤销都追加一条历史流水，审计不丢。
/// </summary>
public sealed class LegacyEmployeeLogReviewService
{
    public const int MaxNoteLength = 500;

    private readonly ISqlSugarClient _posmDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly ICurrentUserService _currentUser;

    public LegacyEmployeeLogReviewService(
        ISqlSugarClient posmDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ICurrentUserService currentUser
    )
    {
        _posmDb = posmDb;
        _storeScopeService = storeScopeService;
        _currentUser = currentUser;
    }

    public async Task<LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>> ReviewAsync(
        LegacyEmployeeLogReviewRequestDto request,
        CancellationToken cancellationToken = default
    )
    {
        var logId = request.LogId?.Trim();
        if (string.IsNullOrEmpty(logId) || logId.Length > 255)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Invalid("日志编号无效");
        }
        byte? result = request.Result?.Trim() switch
        {
            "normal" => Q.ReviewNormal,
            "followUp" => Q.ReviewNeedsFollowUp,
            "revoked" => Q.ReviewRevoked,
            _ => null,
        };
        if (result is not { } resultCode)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Invalid("核查结论只能是 normal、followUp 或 revoked");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Invalid($"核查备注最长 {MaxNoteLength} 个字符");
        }
        var userId = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Forbidden();
        }
        var userName = _currentUser.GetCurrentUsername();
        userName = userName.Length > 100 ? userName[..100] : userName;

        var connection = GetSqlServerConnection();
        var target = (await Q.ExecuteItemsAsync(connection, Q.BuildTarget(logId), cancellationToken)).FirstOrDefault();
        if (target == null)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.NotFound();
        }
        // 核查与查看同一分店口径：店长可核查全部关联分店的日志（另需 Review 权限）。
        var scope = await _storeScopeService.GetAssignedStoreScopeAsync();
        if (string.IsNullOrWhiteSpace(target.StoreCode)
            || !scope.IsAllowed
            || !scope.CanAccessStoreCode(target.StoreCode.Trim()))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Forbidden();
        }
        // 撤销不要求仍有命中（规则口径变化后也能撤销旧结论）；标记新结论必须有有效命中。
        if (resultCode != Q.ReviewRevoked && !await HasActiveFlagAsync(connection, target.Id, cancellationToken))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Invalid("该记录没有命中异常规则，无需核查");
        }

        var command = BuildReview(target, resultCode, note, request.ExpectedVersion, userId, userName, DateTime.UtcNow);
        var (status, review) = await Q.WithReaderAsync(connection, command, async reader =>
        {
            var code = await reader.ReadAsync(cancellationToken) ? Convert.ToInt32(reader.GetValue(0)) : 1;
            await Q.NextResultAsync(reader, cancellationToken);
            var current = await reader.ReadAsync(cancellationToken) ? Q.ReadReview(reader, 0) : null;
            return (code, current);
        }, cancellationToken);
        return status switch
        {
            0 when review != null => LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Ok(review),
            2 => LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Invalid("该记录尚未核查，无需撤销"),
            _ => LegacyEmployeeLogResult<LegacyEmployeeLogReviewDto>.Conflict(),
        };
    }

    /// <summary>
    /// 一个事务内：带更新锁读当前版本 → 版本相符才写（首次核查期望版本为空或 0）→ 追加历史。
    /// 返回状态：0 成功、1 版本冲突、2 撤销一条从未核查的记录。
    /// </summary>
    internal static LegacyEmployeeLogSqlCommand BuildReview(
        LegacyEmployeeLogItemDto target,
        byte result,
        string? note,
        int? expectedVersion,
        string userId,
        string userName,
        DateTime nowUtc
    )
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@LogId", target.Id, DbType.AnsiString, 255),
            new("@StoreCode", target.StoreCode, DbType.AnsiString, 200),
            new("@OperationTime", target.OperationTime, DbType.DateTime),
            new("@Result", result, DbType.Byte),
            new("@Note", note, DbType.String, 500),
            new("@Expected", expectedVersion, DbType.Int32),
            new("@UserId", userId, DbType.String, 50),
            new("@UserName", userName, DbType.String, 100),
            new("@Now", nowUtc, DbType.DateTime2),
        };
        const string sql = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            DECLARE @Current int, @Status int = 0;
            BEGIN TRANSACTION;
            SELECT @Current = r.[Version] FROM [dbo].[LegacyEmployeeLogReviews] AS r WITH (UPDLOCK, HOLDLOCK) WHERE r.[LogId] = @LogId;
            IF @Current IS NULL
            BEGIN
                IF @Result = 0 SET @Status = 2;
                ELSE IF ISNULL(@Expected, 0) <> 0 SET @Status = 1;
                ELSE
                    INSERT INTO [dbo].[LegacyEmployeeLogReviews]
                        ([LogId], [StoreCode], [OperationTime], [Result], [Note], [ReviewedByUserId], [ReviewedByName], [ReviewedAtUtc], [Version])
                    VALUES (@LogId, @StoreCode, @OperationTime, @Result, @Note, @UserId, @UserName, @Now, 1);
            END
            ELSE IF @Expected IS NULL OR @Expected <> @Current
                SET @Status = 1;
            ELSE
                UPDATE [dbo].[LegacyEmployeeLogReviews]
                SET [Result] = @Result, [Note] = @Note, [ReviewedByUserId] = @UserId, [ReviewedByName] = @UserName,
                    [ReviewedAtUtc] = @Now, [Version] = @Current + 1
                WHERE [LogId] = @LogId;
            IF @Status = 0
                INSERT INTO [dbo].[LegacyEmployeeLogReviewHistory] ([LogId], [Result], [Note], [ActorUserId], [ActorName], [CreatedAtUtc])
                VALUES (@LogId, @Result, @Note, @UserId, @UserName, @Now);
            COMMIT TRANSACTION;
            SELECT @Status;
            SELECT r.[Result], r.[Note], r.[ReviewedByName], r.[ReviewedAtUtc], r.[Version]
            FROM [dbo].[LegacyEmployeeLogReviews] AS r WHERE r.[LogId] = @LogId;
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    private static async Task<bool> HasActiveFlagAsync(DbConnection connection, string logId, CancellationToken cancellationToken)
    {
        var command = new LegacyEmployeeLogSqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogFlags] WITH (NOLOCK) WHERE [LogId] = @LogId AND [RetractedAtUtc] IS NULL) THEN 1 ELSE 0 END;",
            [new LegacyEmployeeLogSqlParameter("@LogId", logId, DbType.AnsiString, 255)]
        );
        return await Q.WithReaderAsync(connection, command, async reader =>
            await reader.ReadAsync(cancellationToken) && Convert.ToInt32(reader.GetValue(0)) == 1, cancellationToken);
    }

    private DbConnection GetSqlServerConnection()
    {
        if (_posmDb.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer)
        {
            throw new NotSupportedException("老系统操作日志只支持 SQL Server 上的 POSM 库。");
        }
        return (DbConnection)_posmDb.Ado.Connection;
    }
}
