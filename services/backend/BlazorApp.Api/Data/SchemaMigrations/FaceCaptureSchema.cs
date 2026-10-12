namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 人脸抓拍接收（独立服务 BlazorApp.FaceIngest P1）：摄像机注册表、抓拍事件表、事件图片表。
/// 主 API 本身不读写这三张表，只负责建表；人脸服务用受限账号（face_ingest_app）直连写入。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；不建 SqlSugar 实体、不进自动建表清单。
/// 时间列一律 UTC datetime2；CameraEventTime 保留摄像机报文里的带偏移时间。错误号区间 52600–52602。
/// </summary>
internal static class FaceCaptureSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.FaceCamera', N'U') IS NULL
    BEGIN
        -- 摄像机注册表：取代 cameras.json 成为正式来源；只存 token 的 SHA-256，不存 token 原文。
        CREATE TABLE [dbo].[FaceCamera]
        (
            [CameraId] nvarchar(64) NOT NULL,
            [StoreCode] nvarchar(32) NOT NULL,
            [TokenSha256] char(64) NOT NULL,
            [Enabled] bit NOT NULL CONSTRAINT [DF_FaceCamera_Enabled] DEFAULT (1),
            [Note] nvarchar(200) NULL,
            [DeviceModel] nvarchar(64) NULL,
            [MacAddress] nvarchar(32) NULL,
            [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_FaceCamera_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_FaceCamera_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [LastSeenAtUtc] datetime2 NULL,
            CONSTRAINT [PK_FaceCamera] PRIMARY KEY CLUSTERED ([CameraId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCamera')
          AND [name] = N'UX_FaceCamera_TokenSha256'
    )
    BEGIN
        -- 一台摄像机一个 token：哈希重复会让鉴权无法区分摄像机，由唯一索引兜底。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_FaceCamera_TokenSha256]
            ON [dbo].[FaceCamera] ([TokenSha256]);
    END;

    IF OBJECT_ID(N'dbo.FaceCaptureEvent', N'U') IS NULL
    BEGIN
        -- 一条推送 = 一行；CaptureId 与人脸服务落盘文件名里的 guid 相同，重处理按它幂等。
        CREATE TABLE [dbo].[FaceCaptureEvent]
        (
            [CaptureId] char(32) NOT NULL,
            [CameraId] nvarchar(64) NOT NULL,
            [StoreCode] nvarchar(32) NOT NULL,
            [ReceivedAtUtc] datetime2 NOT NULL,
            [CameraEventTime] datetimeoffset NULL,
            [EventType] nvarchar(64) NULL,
            [EventState] nvarchar(32) NULL,
            [CameraMac] nvarchar(32) NULL,
            [CameraChannel] int NULL,
            [BodySha256] char(64) NOT NULL,
            [BodyBytes] bigint NOT NULL,
            [IsRetransmission] bit NULL,
            [DuplicateOfCaptureId] char(32) NULL,
            [FaceCount] int NOT NULL CONSTRAINT [DF_FaceCaptureEvent_FaceCount] DEFAULT (0),
            [ParseStatus] tinyint NOT NULL,
            [ParseNote] nvarchar(500) NULL,
            [RawStorage] nvarchar(16) NOT NULL,
            [RawObjectKey] nvarchar(400) NULL,
            [MatchStatus] tinyint NOT NULL CONSTRAINT [DF_FaceCaptureEvent_MatchStatus] DEFAULT (0),
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_FaceCaptureEvent] PRIMARY KEY CLUSTERED ([CaptureId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
          AND [name] = N'IX_FaceCaptureEvent_Store_Received'
    )
    BEGIN
        -- 按门店看最近抓拍。
        CREATE NONCLUSTERED INDEX [IX_FaceCaptureEvent_Store_Received]
            ON [dbo].[FaceCaptureEvent] ([StoreCode], [ReceivedAtUtc] DESC);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
          AND [name] = N'IX_FaceCaptureEvent_Camera_Received'
    )
    BEGIN
        -- 按摄像机看最近抓拍 / 统计流量。
        CREATE NONCLUSTERED INDEX [IX_FaceCaptureEvent_Camera_Received]
            ON [dbo].[FaceCaptureEvent] ([CameraId], [ReceivedAtUtc] DESC);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
          AND [name] = N'IX_FaceCaptureEvent_MatchPending'
    )
    BEGIN
        -- 后续比对阶段按「待比对」取队列；过滤索引只覆盖 MatchStatus=0 的行，已比对的不占空间。
        CREATE NONCLUSTERED INDEX [IX_FaceCaptureEvent_MatchPending]
            ON [dbo].[FaceCaptureEvent] ([MatchStatus], [ReceivedAtUtc])
            WHERE [MatchStatus] = 0;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
          AND [name] = N'IX_FaceCaptureEvent_Camera_BodySha256'
    )
    BEGIN
        -- 重复报文判定（同一摄像机 24 小时内相同请求体哈希）走这条索引。
        CREATE NONCLUSTERED INDEX [IX_FaceCaptureEvent_Camera_BodySha256]
            ON [dbo].[FaceCaptureEvent] ([CameraId], [BodySha256])
            INCLUDE ([ReceivedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.FaceCaptureImage', N'U') IS NULL
    BEGIN
        -- 事件里的每张图一行；ImageId 由 CaptureId + 序号确定性生成，重处理幂等。图片本体在 COS 或服务本地，不进库。
        CREATE TABLE [dbo].[FaceCaptureImage]
        (
            [ImageId] char(32) NOT NULL,
            [CaptureId] char(32) NOT NULL,
            [Kind] tinyint NOT NULL,
            [PartIndex] int NOT NULL,
            [FaceIndex] int NULL,
            [Storage] nvarchar(16) NOT NULL,
            [ObjectKey] nvarchar(400) NOT NULL,
            [ContentType] nvarchar(64) NULL,
            [ByteSize] int NOT NULL,
            [Sha256] char(64) NOT NULL,
            [Width] int NULL,
            [Height] int NULL,
            [RectX] decimal(9, 4) NULL,
            [RectY] decimal(9, 4) NULL,
            [RectW] decimal(9, 4) NULL,
            [RectH] decimal(9, 4) NULL,
            [RectNormalized] bit NULL,
            [FaceScore] decimal(9, 4) NULL,
            [CameraFaceId] nvarchar(64) NULL,
            [AttributesJson] nvarchar(2000) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_FaceCaptureImage] PRIMARY KEY CLUSTERED ([ImageId]),
            CONSTRAINT [FK_FaceCaptureImage_FaceCaptureEvent] FOREIGN KEY ([CaptureId])
                REFERENCES [dbo].[FaceCaptureEvent] ([CaptureId]) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureImage')
          AND [name] = N'IX_FaceCaptureImage_CaptureId'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceCaptureImage_CaptureId]
            ON [dbo].[FaceCaptureImage] ([CaptureId]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键、外键（级联删除）与索引；不兼容结构只能通过显式迁移修复。
    // max_length 按 sys.columns 口径：nvarchar(n) 为 2n、char(n) 为 n、decimal(9,4) 为 5；0 表示不比长度。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.FaceCamera', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceCaptureEvent', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceCaptureImage', N'U') IS NULL
    THROW 52600, N'Face capture tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'FaceCamera', N'CameraId', N'nvarchar', 128, 0),
      (N'FaceCamera', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceCamera', N'TokenSha256', N'char', 64, 0),
      (N'FaceCamera', N'Enabled', N'bit', 0, 0),
      (N'FaceCamera', N'Note', N'nvarchar', 400, 1),
      (N'FaceCamera', N'DeviceModel', N'nvarchar', 128, 1),
      (N'FaceCamera', N'MacAddress', N'nvarchar', 64, 1),
      (N'FaceCamera', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'FaceCamera', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'FaceCamera', N'LastSeenAtUtc', N'datetime2', 0, 1),
      (N'FaceCaptureEvent', N'CaptureId', N'char', 32, 0),
      (N'FaceCaptureEvent', N'CameraId', N'nvarchar', 128, 0),
      (N'FaceCaptureEvent', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceCaptureEvent', N'ReceivedAtUtc', N'datetime2', 0, 0),
      (N'FaceCaptureEvent', N'CameraEventTime', N'datetimeoffset', 0, 1),
      (N'FaceCaptureEvent', N'EventType', N'nvarchar', 128, 1),
      (N'FaceCaptureEvent', N'EventState', N'nvarchar', 64, 1),
      (N'FaceCaptureEvent', N'CameraMac', N'nvarchar', 64, 1),
      (N'FaceCaptureEvent', N'CameraChannel', N'int', 0, 1),
      (N'FaceCaptureEvent', N'BodySha256', N'char', 64, 0),
      (N'FaceCaptureEvent', N'BodyBytes', N'bigint', 0, 0),
      (N'FaceCaptureEvent', N'IsRetransmission', N'bit', 0, 1),
      (N'FaceCaptureEvent', N'DuplicateOfCaptureId', N'char', 32, 1),
      (N'FaceCaptureEvent', N'FaceCount', N'int', 0, 0),
      (N'FaceCaptureEvent', N'ParseStatus', N'tinyint', 0, 0),
      (N'FaceCaptureEvent', N'ParseNote', N'nvarchar', 1000, 1),
      (N'FaceCaptureEvent', N'RawStorage', N'nvarchar', 32, 0),
      (N'FaceCaptureEvent', N'RawObjectKey', N'nvarchar', 800, 1),
      (N'FaceCaptureEvent', N'MatchStatus', N'tinyint', 0, 0),
      (N'FaceCaptureEvent', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'FaceCaptureImage', N'ImageId', N'char', 32, 0),
      (N'FaceCaptureImage', N'CaptureId', N'char', 32, 0),
      (N'FaceCaptureImage', N'Kind', N'tinyint', 0, 0),
      (N'FaceCaptureImage', N'PartIndex', N'int', 0, 0),
      (N'FaceCaptureImage', N'FaceIndex', N'int', 0, 1),
      (N'FaceCaptureImage', N'Storage', N'nvarchar', 32, 0),
      (N'FaceCaptureImage', N'ObjectKey', N'nvarchar', 800, 0),
      (N'FaceCaptureImage', N'ContentType', N'nvarchar', 128, 1),
      (N'FaceCaptureImage', N'ByteSize', N'int', 0, 0),
      (N'FaceCaptureImage', N'Sha256', N'char', 64, 0),
      (N'FaceCaptureImage', N'Width', N'int', 0, 1),
      (N'FaceCaptureImage', N'Height', N'int', 0, 1),
      (N'FaceCaptureImage', N'RectX', N'decimal', 5, 1),
      (N'FaceCaptureImage', N'RectY', N'decimal', 5, 1),
      (N'FaceCaptureImage', N'RectW', N'decimal', 5, 1),
      (N'FaceCaptureImage', N'RectH', N'decimal', 5, 1),
      (N'FaceCaptureImage', N'RectNormalized', N'bit', 0, 1),
      (N'FaceCaptureImage', N'FaceScore', N'decimal', 5, 1),
      (N'FaceCaptureImage', N'CameraFaceId', N'nvarchar', 128, 1),
      (N'FaceCaptureImage', N'AttributesJson', N'nvarchar', 4000, 1),
      (N'FaceCaptureImage', N'CreatedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
       OR (ty.[name] = N'decimal' AND (c.[precision] <> 9 OR c.[scale] <> 4))
)
    THROW 52601, N'Face capture column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceCamera') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceCaptureImage') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCamera')
      AND [name] = N'UX_FaceCamera_TokenSha256'
      AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
      AND [name] = N'IX_FaceCaptureEvent_Store_Received'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
      AND [name] = N'IX_FaceCaptureEvent_Camera_Received'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
      AND [name] = N'IX_FaceCaptureEvent_MatchPending'
      AND [has_filter] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
      AND [name] = N'IX_FaceCaptureEvent_Camera_BodySha256'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceCaptureImage')
      AND [name] = N'IX_FaceCaptureImage_CaptureId'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceCaptureImage')
      AND [referenced_object_id] = OBJECT_ID(N'dbo.FaceCaptureEvent')
      AND [delete_referential_action] = 1
 )
    THROW 52602, N'Face capture key or index signature is incompatible.', 1;
""";
}
