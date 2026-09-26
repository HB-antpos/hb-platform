namespace Hbpos.RemoteMaintenance.Setup;

// 固定故障码同时用于普通用户预检和提权 helper 退出码，避免向界面透传异常原文。
public enum RemoteMaintenanceSetupError
{
    InstallationLocationInvalid = 10,
    ComponentsMissing = 11,
    InstallationPermissionsInvalid = 12,
    // 以下为提权安装分步故障码：泛化的退出码 4 无法告诉门店下一步该做什么。
    // 本机已有未由公司配置接管的 RustDesk（含此前安装失败的残留），需先卸载。
    ExistingRustDeskUnmanaged = 13,
    // RustDesk 安装程序未在限定时间内完成，或安装后服务无法建立/启动。
    RustDeskSetupIncomplete = 14,
    // RustDesk 已安装，但公司服务器、无人值守密码或 ID 未能写入/读回。
    RustDeskConfigurationFailed = 15
}

public sealed class RemoteMaintenanceSetupException(RemoteMaintenanceSetupError error)
    : Exception("远程维护安装环境检查未通过。")
{
    public RemoteMaintenanceSetupError Error { get; } = error;
}
