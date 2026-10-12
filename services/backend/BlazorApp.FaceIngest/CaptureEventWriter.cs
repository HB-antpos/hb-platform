using System.Data;
using Microsoft.Data.SqlClient;

namespace BlazorApp.FaceIngest;

/// <summary>FaceCaptureEvent 一行（字段长度已在处理器里截断到列宽）。</summary>
internal sealed record CaptureEventRecord
{
    public required string CaptureId { get; init; }

    public required string CameraId { get; init; }

    public required string StoreCode { get; init; }

    /// <summary>服务端 UTC 接收时间（Kind=Utc）。</summary>
    public required DateTime ReceivedAtUtc { get; init; }

    public DateTimeOffset? CameraEventTime { get; init; }

    public string? EventType { get; init; }

    public string? EventState { get; init; }

    public string? CameraMac { get; init; }

    public int? CameraChannel { get; init; }

    public required string BodySha256 { get; init; }

    public required long BodyBytes { get; init; }

    public bool? IsRetransmission { get; init; }

    public required int FaceCount { get; init; }

    public required CaptureParseStatus ParseStatus { get; init; }

    public string? ParseNote { get; init; }

    public required string RawStorage { get; init; }

    public string? RawObjectKey { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}

/// <summary>FaceCaptureImage 一行。</summary>
internal sealed record CaptureImageRecord
{
    public required string ImageId { get; init; }

    public required string CaptureId { get; init; }

    public required CaptureImageKind Kind { get; init; }

    public required int PartIndex { get; init; }

    public int? FaceIndex { get; init; }

    public required string Storage { get; init; }

    public required string ObjectKey { get; init; }

    public string? ContentType { get; init; }

    public required int ByteSize { get; init; }

    public required string Sha256 { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public decimal? RectX { get; init; }

    public decimal? RectY { get; init; }

    public decimal? RectW { get; init; }

    public decimal? RectH { get; init; }

    public bool? RectNormalized { get; init; }

    public decimal? FaceScore { get; init; }

    public string? CameraFaceId { get; init; }

    public string? AttributesJson { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}

/// <summary>写库结果：事件行是否本次新插入、新插入的图片行数、判定出的重复来源（本次或已有行里的值）。</summary>
internal sealed record CaptureWriteOutcome(bool EventInserted, int ImagesInserted, string? DuplicateOfCaptureId);

/// <summary>
/// 事件入库。抽成接口便于处理器测试用内存假实现；SqlClient 实现见 <see cref="SqlCaptureEventWriter"/>。
/// </summary>
internal interface ICaptureEventWriter
{
    /// <summary>
    /// 同一事务内：按 CaptureId 幂等写事件行（已存在则不动）、判定 DuplicateOfCaptureId、按 ImageId 幂等写图片行。
    /// 失败抛异常（处理器按退避重试）。
    /// </summary>
    Task<CaptureWriteOutcome> WriteAsync(
        CaptureEventRecord captureEvent,
        IReadOnlyList<CaptureImageRecord> images,
        CancellationToken cancellationToken);

    /// <summary>更新摄像机最近一次推送时间（只前进不后退）。在事务外调用，失败不影响该条处理结果。</summary>
    Task TouchCameraLastSeenAsync(string cameraId, DateTime seenAtUtc, CancellationToken cancellationToken);
}

/// <summary>
/// 基于 Microsoft.Data.SqlClient 的写入实现（受限账号 face_ingest_app：FaceCaptureEvent / FaceCaptureImage 只有 SELECT + INSERT，
/// FaceCamera 只有 SELECT + UPDATE(LastSeenAtUtc)）。所有 SQL 参数化、写成常量便于审查；不用 MERGE / DELETE。
/// </summary>
internal sealed class SqlCaptureEventWriter : ICaptureEventWriter
{
    /// <summary>重复报文判定窗口：同一摄像机 24 小时内相同请求体哈希。</summary>
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);

    /// <summary>事件行是否已存在（加更新锁 + 范围锁，同一 CaptureId 的并发写入会串行化）。</summary>
    internal const string SelectExistingEventSql = """
SELECT [DuplicateOfCaptureId]
FROM [dbo].[FaceCaptureEvent] WITH (UPDLOCK, HOLDLOCK)
WHERE [CaptureId] = @CaptureId;
""";

    /// <summary>重复来源：窗口内（接收时间不晚于本条）最早到达的同摄像机同哈希事件。</summary>
    internal const string FindDuplicateSql = """
SELECT TOP (1) [CaptureId]
FROM [dbo].[FaceCaptureEvent]
WHERE [CameraId] = @CameraId
  AND [BodySha256] = @BodySha256
  AND [CaptureId] <> @CaptureId
  AND [ReceivedAtUtc] >= @WindowStartUtc
  AND [ReceivedAtUtc] <= @ReceivedAtUtc
ORDER BY [ReceivedAtUtc] ASC, [CaptureId] ASC;
""";

    internal const string InsertEventSql = """
IF NOT EXISTS (SELECT 1 FROM [dbo].[FaceCaptureEvent] WHERE [CaptureId] = @CaptureId)
INSERT INTO [dbo].[FaceCaptureEvent]
    ([CaptureId], [CameraId], [StoreCode], [ReceivedAtUtc], [CameraEventTime], [EventType], [EventState],
     [CameraMac], [CameraChannel], [BodySha256], [BodyBytes], [IsRetransmission], [DuplicateOfCaptureId],
     [FaceCount], [ParseStatus], [ParseNote], [RawStorage], [RawObjectKey], [MatchStatus], [CreatedAtUtc])
VALUES
    (@CaptureId, @CameraId, @StoreCode, @ReceivedAtUtc, @CameraEventTime, @EventType, @EventState,
     @CameraMac, @CameraChannel, @BodySha256, @BodyBytes, @IsRetransmission, @DuplicateOfCaptureId,
     @FaceCount, @ParseStatus, @ParseNote, @RawStorage, @RawObjectKey, 0, @CreatedAtUtc);
""";

    internal const string InsertImageSql = """
IF NOT EXISTS (SELECT 1 FROM [dbo].[FaceCaptureImage] WHERE [ImageId] = @ImageId)
INSERT INTO [dbo].[FaceCaptureImage]
    ([ImageId], [CaptureId], [Kind], [PartIndex], [FaceIndex], [Storage], [ObjectKey], [ContentType],
     [ByteSize], [Sha256], [Width], [Height], [RectX], [RectY], [RectW], [RectH], [RectNormalized],
     [FaceScore], [CameraFaceId], [AttributesJson], [CreatedAtUtc])
VALUES
    (@ImageId, @CaptureId, @Kind, @PartIndex, @FaceIndex, @Storage, @ObjectKey, @ContentType,
     @ByteSize, @Sha256, @Width, @Height, @RectX, @RectY, @RectW, @RectH, @RectNormalized,
     @FaceScore, @CameraFaceId, @AttributesJson, @CreatedAtUtc);
""";

    internal const string TouchLastSeenSql = """
UPDATE [dbo].[FaceCamera]
SET [LastSeenAtUtc] = @SeenAtUtc
WHERE [CameraId] = @CameraId
  AND ([LastSeenAtUtc] IS NULL OR [LastSeenAtUtc] < @SeenAtUtc);
""";

    private const int CommandTimeoutSeconds = 30;

    private readonly string _connectionString;

    public SqlCaptureEventWriter(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<CaptureWriteOutcome> WriteAsync(
        CaptureEventRecord captureEvent,
        IReadOnlyList<CaptureImageRecord> images,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var (exists, existingDuplicateOf) = await ReadExistingAsync(connection, transaction, captureEvent.CaptureId, cancellationToken);
            var eventInserted = false;
            var duplicateOf = existingDuplicateOf;
            if (!exists)
            {
                duplicateOf = await FindDuplicateAsync(connection, transaction, captureEvent, cancellationToken);
                await using var insert = CreateCommand(connection, transaction, InsertEventSql);
                AddEventParameters(insert, captureEvent, duplicateOf);
                eventInserted = await insert.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            var imagesInserted = 0;
            foreach (var image in images)
            {
                await using var insertImage = CreateCommand(connection, transaction, InsertImageSql);
                AddImageParameters(insertImage, image);
                imagesInserted += await insertImage.ExecuteNonQueryAsync(cancellationToken) > 0 ? 1 : 0;
            }

            await transaction.CommitAsync(cancellationToken);
            return new CaptureWriteOutcome(eventInserted, imagesInserted, duplicateOf);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackError) when (rollbackError is InvalidOperationException or SqlException)
            {
                // 连接已断开时事务已由服务器回滚，忽略。
            }

            throw;
        }
    }

    public async Task TouchCameraLastSeenAsync(string cameraId, DateTime seenAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, TouchLastSeenSql);
        command.Parameters.Add(Parameter("@CameraId", SqlDbType.NVarChar, 64, cameraId));
        command.Parameters.Add(Parameter("@SeenAtUtc", SqlDbType.DateTime2, 0, seenAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(bool Exists, string? DuplicateOf)> ReadExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string captureId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectExistingEventSql);
        command.Parameters.Add(Parameter("@CaptureId", SqlDbType.Char, 32, captureId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, null);
        }

        return (true, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    private static async Task<string?> FindDuplicateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CaptureEventRecord captureEvent,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, FindDuplicateSql);
        command.Parameters.Add(Parameter("@CameraId", SqlDbType.NVarChar, 64, captureEvent.CameraId));
        command.Parameters.Add(Parameter("@BodySha256", SqlDbType.Char, 64, captureEvent.BodySha256));
        command.Parameters.Add(Parameter("@CaptureId", SqlDbType.Char, 32, captureEvent.CaptureId));
        command.Parameters.Add(Parameter("@WindowStartUtc", SqlDbType.DateTime2, 0, captureEvent.ReceivedAtUtc - DuplicateWindow));
        command.Parameters.Add(Parameter("@ReceivedAtUtc", SqlDbType.DateTime2, 0, captureEvent.ReceivedAtUtc));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string id ? id : null;
    }

    private static void AddEventParameters(SqlCommand command, CaptureEventRecord e, string? duplicateOf)
    {
        var p = command.Parameters;
        p.Add(Parameter("@CaptureId", SqlDbType.Char, 32, e.CaptureId));
        p.Add(Parameter("@CameraId", SqlDbType.NVarChar, 64, e.CameraId));
        p.Add(Parameter("@StoreCode", SqlDbType.NVarChar, 32, e.StoreCode));
        p.Add(Parameter("@ReceivedAtUtc", SqlDbType.DateTime2, 0, e.ReceivedAtUtc));
        p.Add(Parameter("@CameraEventTime", SqlDbType.DateTimeOffset, 0, e.CameraEventTime));
        p.Add(Parameter("@EventType", SqlDbType.NVarChar, 64, e.EventType));
        p.Add(Parameter("@EventState", SqlDbType.NVarChar, 32, e.EventState));
        p.Add(Parameter("@CameraMac", SqlDbType.NVarChar, 32, e.CameraMac));
        p.Add(Parameter("@CameraChannel", SqlDbType.Int, 0, e.CameraChannel));
        p.Add(Parameter("@BodySha256", SqlDbType.Char, 64, e.BodySha256));
        p.Add(Parameter("@BodyBytes", SqlDbType.BigInt, 0, e.BodyBytes));
        p.Add(Parameter("@IsRetransmission", SqlDbType.Bit, 0, e.IsRetransmission));
        p.Add(Parameter("@DuplicateOfCaptureId", SqlDbType.Char, 32, duplicateOf));
        p.Add(Parameter("@FaceCount", SqlDbType.Int, 0, e.FaceCount));
        p.Add(Parameter("@ParseStatus", SqlDbType.TinyInt, 0, (byte)e.ParseStatus));
        p.Add(Parameter("@ParseNote", SqlDbType.NVarChar, 500, e.ParseNote));
        p.Add(Parameter("@RawStorage", SqlDbType.NVarChar, 16, e.RawStorage));
        p.Add(Parameter("@RawObjectKey", SqlDbType.NVarChar, 400, e.RawObjectKey));
        p.Add(Parameter("@CreatedAtUtc", SqlDbType.DateTime2, 0, e.CreatedAtUtc));
    }

    private static void AddImageParameters(SqlCommand command, CaptureImageRecord i)
    {
        var p = command.Parameters;
        p.Add(Parameter("@ImageId", SqlDbType.Char, 32, i.ImageId));
        p.Add(Parameter("@CaptureId", SqlDbType.Char, 32, i.CaptureId));
        p.Add(Parameter("@Kind", SqlDbType.TinyInt, 0, (byte)i.Kind));
        p.Add(Parameter("@PartIndex", SqlDbType.Int, 0, i.PartIndex));
        p.Add(Parameter("@FaceIndex", SqlDbType.Int, 0, i.FaceIndex));
        p.Add(Parameter("@Storage", SqlDbType.NVarChar, 16, i.Storage));
        p.Add(Parameter("@ObjectKey", SqlDbType.NVarChar, 400, i.ObjectKey));
        p.Add(Parameter("@ContentType", SqlDbType.NVarChar, 64, i.ContentType));
        p.Add(Parameter("@ByteSize", SqlDbType.Int, 0, i.ByteSize));
        p.Add(Parameter("@Sha256", SqlDbType.Char, 64, i.Sha256));
        p.Add(Parameter("@Width", SqlDbType.Int, 0, i.Width));
        p.Add(Parameter("@Height", SqlDbType.Int, 0, i.Height));
        p.Add(DecimalParameter("@RectX", i.RectX));
        p.Add(DecimalParameter("@RectY", i.RectY));
        p.Add(DecimalParameter("@RectW", i.RectW));
        p.Add(DecimalParameter("@RectH", i.RectH));
        p.Add(Parameter("@RectNormalized", SqlDbType.Bit, 0, i.RectNormalized));
        p.Add(DecimalParameter("@FaceScore", i.FaceScore));
        p.Add(Parameter("@CameraFaceId", SqlDbType.NVarChar, 64, i.CameraFaceId));
        p.Add(Parameter("@AttributesJson", SqlDbType.NVarChar, 2000, i.AttributesJson));
        p.Add(Parameter("@CreatedAtUtc", SqlDbType.DateTime2, 0, i.CreatedAtUtc));
    }

    private static SqlCommand CreateCommand(SqlConnection connection, SqlTransaction? transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

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
