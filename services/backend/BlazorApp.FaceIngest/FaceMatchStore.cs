using System.Data;
using Microsoft.Data.SqlClient;

namespace BlazorApp.FaceIngest;

/// <summary>一个整点桶要累加的计数（只有计数，没有任何生物信息）。</summary>
internal sealed record TrafficDelta
{
    public int Capture { get; init; }

    public int StaffMatch { get; init; }

    public int UniqueVisitor { get; init; }

    public int Repeat { get; init; }

    public int LowQuality { get; init; }

    public int ApiFailure { get; init; }

    public bool IsEmpty => Capture == 0 && StaffMatch == 0 && UniqueVisitor == 0 && Repeat == 0 && LowQuality == 0 && ApiFailure == 0;
}

/// <summary>要写入 FaceTrafficBucket 的一次累加；同时可顺带给当日访客库行的 VisitorCount 加数（同一事务）。</summary>
internal sealed record TrafficUpdate(string StoreCode, DateTime BucketStartUtc, TrafficDelta Delta, string? VisitGroupId, int VisitorsAdded);

/// <summary>FaceVisitGroup 一行（新建时 VisitorCount 为 0，之后只增）。</summary>
internal sealed record VisitGroupRecord(string GroupId, string StoreCode, DateOnly BusinessDate, DateTime CreatedAtUtc, DateTime DeleteAfterUtc);

/// <summary>FaceUnwelcomeEntry 中与命中判定有关的列。</summary>
internal sealed record UnwelcomeEntryInfo(string EntryId, string? StoreScope, DateTime ExpiresAtUtc);

/// <summary>FaceMatchTask 的新增行（待复核；NotifiedAtUtc 留空，由主 API 后台任务发提示后写入）。</summary>
internal sealed record MatchTaskRecord(
    string TaskId,
    string StoreCode,
    string CameraId,
    DateTime CapturedAtUtc,
    string EntryId,
    decimal Score,
    string ImageObjectKey,
    DateTime ImageDeleteAtUtc);

/// <summary>评估结果的 Decision 取值（设计 13.3 只写了 tinyint，数值由接收端约定并写进 runbook）。</summary>
internal enum EvaluationDecision : byte
{
    /// <summary>没有命中任何库，且未开启去重（只记分数）。</summary>
    NoMatch = 0,

    /// <summary>员工命中。</summary>
    Staff = 1,

    /// <summary>命中当日访客库（重复抓拍）。</summary>
    RepeatVisitor = 2,

    /// <summary>无命中且开启去重：会被当作新访客。</summary>
    NewVisitor = 3,

    /// <summary>质量不达标，未调用或搜索被判不可用。</summary>
    LowQuality = 4,
}

/// <summary>FaceMatchEvaluation 的新增行。</summary>
internal sealed record EvaluationRecord(string CaptureId, decimal? StaffScore, decimal? VisitScore, EvaluationDecision Decision);

/// <summary>
/// 人脸比对的写库接口（设计第 13.3、14.4 节的授权范围）：FaceTrafficBucket UPSERT、FaceVisitGroup 建行 / 增量、
/// FaceMatchEvaluation 插入、FaceUnwelcomeEntry 只读、FaceMatchTask 插入。抽成接口便于测试用内存假实现。失败一律抛异常。
/// </summary>
internal interface IFaceMatchStore
{
    /// <summary>当日访客库的登记行不存在则插入（幂等；并发插入撞主键视为已存在）。</summary>
    Task EnsureVisitGroupAsync(VisitGroupRecord group, CancellationToken cancellationToken);

    /// <summary>整点桶 UPSERT（并发安全），并在同一事务里给访客库行的 VisitorCount 加数。</summary>
    Task ApplyTrafficAsync(TrafficUpdate update, CancellationToken cancellationToken);

    /// <summary>按腾讯云 PersonId 找名单条目；找不到返回 null。</summary>
    Task<UnwelcomeEntryInfo?> FindUnwelcomeEntryAsync(string tencentPersonId, CancellationToken cancellationToken);

    /// <summary>插入待复核任务（按 TaskId 幂等）。</summary>
    Task InsertMatchTaskAsync(MatchTaskRecord task, CancellationToken cancellationToken);

    /// <summary>插入评估行（按 CaptureId 幂等：已存在时忽略）。</summary>
    Task InsertEvaluationAsync(EvaluationRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// 基于 Microsoft.Data.SqlClient 的实现。受限账号 face_ingest_app 的授权（设计 13.3、14.4）：
/// FaceTrafficBucket SELECT / INSERT / UPDATE；FaceVisitGroup SELECT / INSERT / UPDATE(VisitorCount)；
/// FaceMatchEvaluation 仅 INSERT（所以不能用 IF NOT EXISTS 先查，改成直接插入并吞主键冲突）；
/// FaceUnwelcomeEntry 仅 SELECT；FaceMatchTask SELECT / INSERT。不用 DELETE / MERGE。
/// 所有 SQL 参数化、写成常量便于审查。
/// </summary>
internal sealed class SqlFaceMatchStore : IFaceMatchStore
{
    /// <summary>登记当日访客库：不存在才插入（VisitorCount 从 0 起）。</summary>
    internal const string EnsureVisitGroupSql = """
IF NOT EXISTS (SELECT 1 FROM [dbo].[FaceVisitGroup] WHERE [GroupId] = @GroupId)
INSERT INTO [dbo].[FaceVisitGroup] ([GroupId], [StoreCode], [BusinessDate], [CreatedAtUtc], [DeleteAfterUtc], [VisitorCount])
VALUES (@GroupId, @StoreCode, @BusinessDate, @CreatedAtUtc, @DeleteAfterUtc, 0);
""";

    /// <summary>
    /// 计数桶 UPSERT：先 UPDATE（UPDLOCK + SERIALIZABLE 锁住键范围），没命中行再 INSERT。
    /// 范围锁保证两个并发写者不会同时判定「不存在」而撞主键；累加写成 [列] = [列] + @增量，并发下不丢更新。
    /// 同一事务里顺带给访客库行加数（要么都成功要么都不写）。
    /// </summary>
    internal const string ApplyTrafficSql = """
SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE [dbo].[FaceTrafficBucket] WITH (UPDLOCK, SERIALIZABLE)
SET [CaptureCount] = [CaptureCount] + @CaptureCount,
    [StaffMatchCount] = [StaffMatchCount] + @StaffMatchCount,
    [UniqueVisitorCount] = [UniqueVisitorCount] + @UniqueVisitorCount,
    [RepeatCount] = [RepeatCount] + @RepeatCount,
    [LowQualityCount] = [LowQualityCount] + @LowQualityCount,
    [ApiFailureCount] = [ApiFailureCount] + @ApiFailureCount
WHERE [StoreCode] = @StoreCode AND [BucketStartUtc] = @BucketStartUtc;
IF @@ROWCOUNT = 0
    INSERT INTO [dbo].[FaceTrafficBucket]
        ([StoreCode], [BucketStartUtc], [CaptureCount], [StaffMatchCount], [UniqueVisitorCount], [RepeatCount], [LowQualityCount], [ApiFailureCount])
    VALUES
        (@StoreCode, @BucketStartUtc, @CaptureCount, @StaffMatchCount, @UniqueVisitorCount, @RepeatCount, @LowQualityCount, @ApiFailureCount);
IF @VisitGroupId IS NOT NULL AND @VisitorsAdded > 0
    UPDATE [dbo].[FaceVisitGroup] SET [VisitorCount] = [VisitorCount] + @VisitorsAdded WHERE [GroupId] = @VisitGroupId;
COMMIT TRANSACTION;
""";

    internal const string FindUnwelcomeEntrySql = """
SELECT TOP (1) [EntryId], [StoreScope], [ExpiresAtUtc]
FROM [dbo].[FaceUnwelcomeEntry]
WHERE [TencentPersonId] = @TencentPersonId;
""";

    /// <summary>命中任务：待复核（ReviewStatus 0 = Pending），NotifiedAtUtc 与两位复核人列都留空。</summary>
    internal const string InsertMatchTaskSql = """
IF NOT EXISTS (SELECT 1 FROM [dbo].[FaceMatchTask] WHERE [TaskId] = @TaskId)
INSERT INTO [dbo].[FaceMatchTask]
    ([TaskId], [StoreCode], [CameraId], [CapturedAtUtc], [EntryId], [Score], [ImageObjectKey], [ReviewStatus], [ImageDeleteAtUtc])
VALUES
    (@TaskId, @StoreCode, @CameraId, @CapturedAtUtc, @EntryId, @Score, @ImageObjectKey, 0, @ImageDeleteAtUtc);
""";

    internal const string InsertEvaluationSql = """
INSERT INTO [dbo].[FaceMatchEvaluation] ([CaptureId], [StaffScore], [VisitScore], [Decision])
VALUES (@CaptureId, @StaffScore, @VisitScore, @Decision);
""";

    private const int CommandTimeoutSeconds = 30;
    private const int DeadlockErrorNumber = 1205;
    private const int MaxDeadlockRetries = 3;

    // 主键 / 唯一约束冲突的 SQL Server 错误号。
    private const int PrimaryKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly string _connectionString;

    public SqlFaceMatchStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureVisitGroupAsync(VisitGroupRecord group, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(
                EnsureVisitGroupSql,
                parameters =>
                {
                    parameters.Add(Parameter("@GroupId", SqlDbType.NVarChar, 64, group.GroupId));
                    parameters.Add(Parameter("@StoreCode", SqlDbType.NVarChar, 32, group.StoreCode));
                    parameters.Add(Parameter("@BusinessDate", SqlDbType.Date, 0, group.BusinessDate.ToDateTime(TimeOnly.MinValue)));
                    parameters.Add(Parameter("@CreatedAtUtc", SqlDbType.DateTime2, 0, group.CreatedAtUtc));
                    parameters.Add(Parameter("@DeleteAfterUtc", SqlDbType.DateTime2, 0, group.DeleteAfterUtc));
                },
                cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is PrimaryKeyViolation or UniqueIndexViolation)
        {
            // 中文注释：两个实例同时首次登记同一个库：后到者撞主键，等价于「已存在」。
        }
    }

    public Task ApplyTrafficAsync(TrafficUpdate update, CancellationToken cancellationToken)
        => ExecuteAsync(
            ApplyTrafficSql,
            parameters =>
            {
                parameters.Add(Parameter("@StoreCode", SqlDbType.NVarChar, 32, update.StoreCode));
                parameters.Add(Parameter("@BucketStartUtc", SqlDbType.DateTime2, 0, update.BucketStartUtc));
                parameters.Add(Parameter("@CaptureCount", SqlDbType.Int, 0, update.Delta.Capture));
                parameters.Add(Parameter("@StaffMatchCount", SqlDbType.Int, 0, update.Delta.StaffMatch));
                parameters.Add(Parameter("@UniqueVisitorCount", SqlDbType.Int, 0, update.Delta.UniqueVisitor));
                parameters.Add(Parameter("@RepeatCount", SqlDbType.Int, 0, update.Delta.Repeat));
                parameters.Add(Parameter("@LowQualityCount", SqlDbType.Int, 0, update.Delta.LowQuality));
                parameters.Add(Parameter("@ApiFailureCount", SqlDbType.Int, 0, update.Delta.ApiFailure));
                parameters.Add(Parameter("@VisitGroupId", SqlDbType.NVarChar, 64, update.VisitGroupId));
                parameters.Add(Parameter("@VisitorsAdded", SqlDbType.Int, 0, update.VisitorsAdded));
            },
            cancellationToken);

    public async Task<UnwelcomeEntryInfo?> FindUnwelcomeEntryAsync(string tencentPersonId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(FindUnwelcomeEntrySql, connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add(Parameter("@TencentPersonId", SqlDbType.NVarChar, 64, tencentPersonId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UnwelcomeEntryInfo(
            reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    public Task InsertMatchTaskAsync(MatchTaskRecord task, CancellationToken cancellationToken)
        => ExecuteAsync(
            InsertMatchTaskSql,
            parameters =>
            {
                parameters.Add(Parameter("@TaskId", SqlDbType.Char, 32, task.TaskId));
                parameters.Add(Parameter("@StoreCode", SqlDbType.NVarChar, 32, task.StoreCode));
                parameters.Add(Parameter("@CameraId", SqlDbType.NVarChar, 64, task.CameraId));
                parameters.Add(Parameter("@CapturedAtUtc", SqlDbType.DateTime2, 0, task.CapturedAtUtc));
                parameters.Add(Parameter("@EntryId", SqlDbType.Char, 32, task.EntryId));
                parameters.Add(DecimalParameter("@Score", task.Score));
                parameters.Add(Parameter("@ImageObjectKey", SqlDbType.NVarChar, 400, task.ImageObjectKey));
                parameters.Add(Parameter("@ImageDeleteAtUtc", SqlDbType.DateTime2, 0, task.ImageDeleteAtUtc));
            },
            cancellationToken);

    public async Task InsertEvaluationAsync(EvaluationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(
                InsertEvaluationSql,
                parameters =>
                {
                    parameters.Add(Parameter("@CaptureId", SqlDbType.Char, 32, record.CaptureId));
                    parameters.Add(DecimalParameter("@StaffScore", record.StaffScore));
                    parameters.Add(DecimalParameter("@VisitScore", record.VisitScore));
                    parameters.Add(Parameter("@Decision", SqlDbType.TinyInt, 0, (byte)record.Decision));
                },
                cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is PrimaryKeyViolation or UniqueIndexViolation)
        {
            // 中文注释：评估行按 CaptureId 唯一；重试 / 重处理再插一次撞主键 = 已经记过，忽略。
        }
    }

    /// <summary>执行一条非查询命令；遇到死锁（1205）重试几次，其余异常原样抛出。</summary>
    private async Task ExecuteAsync(string sql, Action<SqlParameterCollection> addParameters, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
                addParameters(command.Parameters);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (SqlException ex) when (ex.Number == DeadlockErrorNumber && attempt < MaxDeadlockRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private static SqlParameter Parameter(string name, SqlDbType type, int size, object? value)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        if (size > 0)
        {
            parameter.Size = size;
        }

        return parameter;
    }

    private static SqlParameter DecimalParameter(string name, decimal? value)
        => new(name, SqlDbType.Decimal) { Precision = 9, Scale = 4, Value = (object?)value ?? DBNull.Value };
}
