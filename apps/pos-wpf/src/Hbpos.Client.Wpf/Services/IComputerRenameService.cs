namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 本机电脑名的当前状态。
/// <see cref="PendingName"/> 不为空 = 电脑名已改但还没重启：Environment.MachineName 仍是旧名，硬件码要重启后才会变。
/// </summary>
public sealed record ComputerRenameState(string CurrentName, string? PendingName, bool IsDomainJoined);

/// <summary>改本机电脑名并重启。抽成接口是为了让页面逻辑能在没有 Windows 的环境里测试。</summary>
public interface IComputerRenameService
{
    /// <summary>读取当前电脑名、是否已改名待重启、是否加入了域。不修改任何设置。</summary>
    ComputerRenameState GetState();

    /// <summary>
    /// 以管理员身份改电脑名（Windows 会弹授权窗口），<b>不</b>重启。
    /// 用户在授权窗口拒绝时返回 false；其他失败抛异常。
    /// </summary>
    Task<bool> RenameAsync(string newName, CancellationToken cancellationToken);

    /// <summary>延迟几秒后重启本机，让新电脑名生效。</summary>
    void Restart();
}
