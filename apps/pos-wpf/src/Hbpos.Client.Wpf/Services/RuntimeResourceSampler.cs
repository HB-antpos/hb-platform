using System.Diagnostics;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 随心跳上报的机器/进程资源快照；任何一项取不到都返回 null，服务端据此保留库里上次的值。
/// </summary>
public sealed record RuntimeResourceSnapshot(
    long? SystemUptimeSeconds,
    int? ProcessWorkingSetMb,
    int? ProcessHandleCount);

public interface IRuntimeResourceSampler
{
    RuntimeResourceSnapshot Capture();
}

/// <summary>
/// 采样 Windows 开机时长与当前 WPF 进程的内存、句柄数，用来在后台发现「很久没重启、资源被拖满」的收银机。
/// </summary>
public sealed class RuntimeResourceSampler : IRuntimeResourceSampler
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    public RuntimeResourceSnapshot Capture()
    {
        long? uptimeSeconds = null;
        int? workingSetMb = null;
        int? handleCount = null;

        try
        {
            // 中文注释：TickCount64 是内核自开机起的毫秒数。开启「快速启动」时点「关机」并不会重置内核，
            // 所以它反映的正是资源（桌面堆、句柄、提交内存）累积的真实时长，只有「重启」才会清零。
            uptimeSeconds = Math.Max(0L, Environment.TickCount64 / 1000L);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // 取不到就不报，不影响心跳本身。
        }

        try
        {
            // 中文注释：每次新建 Process 对象才能拿到最新的 WorkingSet64 / HandleCount；用完立即释放，避免心跳本身泄漏句柄。
            using var process = Process.GetCurrentProcess();
            workingSetMb = (int)Math.Min(int.MaxValue, process.WorkingSet64 / BytesPerMegabyte);
            handleCount = process.HandleCount;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            // 取不到就不报，不影响心跳本身。
        }

        return new RuntimeResourceSnapshot(uptimeSeconds, workingSetMb, handleCount);
    }
}
