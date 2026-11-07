namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 员工通知批次表新增 SmsSenderId 列（迁移 20261107.001-staff-notification-sms-sender）：
/// 发送时可选短信发件名（默认 HotBargain，另可选 ANTPOS），后台任务逐人发送与「重试失败项」都要读到本批次当初选的发件名，
/// 所以必须落在批次行上。只加一个可空列，历史行为空 = 按默认发件名处理；旧版主 API 不读这一列，回滚镜像不受影响。
/// 回退 = 不需要动库（多一个可空列无害）；真要删列需 ALTER TABLE ... DROP COLUMN 并移除迁移账本行。
/// 错误号接在员工通知那一段（54450–54461）之后：54462 批次表缺失，54463 列签名不兼容。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（新版主 API 查询批次时会选这一列）。
/// </summary>
internal static class StaffNotificationSmsSenderSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.StaffNotificationBatch', N'U') IS NULL
    THROW 54462, N'Staff notification batch table is missing; apply 20261106.001 first.', 1;

IF COL_LENGTH(N'dbo.StaffNotificationBatch', N'SmsSenderId') IS NULL
BEGIN
    ALTER TABLE [dbo].[StaffNotificationBatch] ADD [SmsSenderId] nvarchar(32) NULL;
END;
""";

    // 只读门禁：列必须存在且为可空 nvarchar(32)（max_length 按字节计为 64）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StaffNotificationBatch', N'U') IS NULL
    THROW 54462, N'Staff notification batch table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.StaffNotificationBatch')
      AND c.[name] = N'SmsSenderId'
      AND ty.[name] = N'nvarchar'
      AND c.[max_length] = 64
      AND c.[is_nullable] = 1
)
    THROW 54463, N'Staff notification batch SmsSenderId column signature is incompatible.', 1;
""";
}
