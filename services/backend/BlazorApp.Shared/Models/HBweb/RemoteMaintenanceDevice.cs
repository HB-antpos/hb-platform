using SqlSugar;

namespace BlazorApp.Shared.Models.HBweb;

/// <summary>远程维护设备最新快照。secret 字段只供服务层读取，禁止映射到列表 DTO。</summary>
[SugarTable("HBweb_RemoteMaintenanceDevice")]
public sealed class RemoteMaintenanceDevice
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; }

    [SugarColumn(IsNullable = false)]
    public int DeviceRegistrationId { get; set; }

    [SugarColumn(Length = 100, IsNullable = false)]
    public string HardwareId { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = false)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 100, IsNullable = false)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ComputerName { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = true)]
    public string? RustdeskId { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? ClientVersion { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? AgentVersion { get; set; }

    /// <summary>
    /// 状态服务心跳最近一次读到的本机 rustdesk.exe 版本（与 commit 登记的 <see cref="ClientVersion"/> 分开存，后者不被心跳覆盖）。
    /// NULL＝还没收到带这一项的心跳；空串＝RustDesk 服务还在但程序文件读不到（被删或被杀毒隔离）。
    /// 列由 --schema=remote-maintenance 补齐，须先于本版本主 API 上线。
    /// </summary>
    [SugarColumn(Length = 80, IsNullable = true)]
    public string? ObservedRustDeskVersion { get; set; }

    [SugarColumn(Length = 20, IsNullable = false)]
    public string ServiceStatus { get; set; } = "notInstalled";

    [SugarColumn(IsNullable = true)]
    public DateTime? LastSeenAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime RegisteredAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public long LastAcceptedSequence { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastAcceptedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? LastOperationId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MonitorTokenHash { get; set; }

    [SugarColumn(Length = 2048, IsNullable = true)]
    public string? CredentialCiphertext { get; set; }

    // SQL Server 的 nvarchar 固定长度最多 4000；加密操作响应使用 max 保存。
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? CommitResponseCiphertext { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool IsDeleted { get; set; }
}
