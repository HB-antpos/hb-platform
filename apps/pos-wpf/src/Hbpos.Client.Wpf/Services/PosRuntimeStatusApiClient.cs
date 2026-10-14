using System.Net.Http;
using System.Net.Http.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed record PosRuntimeStatusReport(
    bool IsOnline,
    string? CashierId,
    string? CashierName);

public interface IPosRuntimeStatusApiClient
{
    Task ReportAsync(
        PosRuntimeStatusReport report,
        CancellationToken cancellationToken = default);
}

public sealed class PosRuntimeStatusApiClient(
    HttpClient httpClient,
    IAppVersionProvider? appVersionProvider = null,
    IRuntimeResourceSampler? resourceSampler = null) : IPosRuntimeStatusApiClient
{
    public async Task ReportAsync(
        PosRuntimeStatusReport report,
        CancellationToken cancellationToken = default)
    {
        // 中文注释：采样失败或未注册采样器时三项都为 null，服务端保留库里上次的值，不会被清空。
        var resources = resourceSampler?.Capture();
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/devices/runtime-status",
            new
            {
                isOnline = report.IsOnline,
                currentCashierId = report.CashierId,
                currentCashierName = report.CashierName,
                // 中文注释：版本号是进程内常量，随每次心跳一起上报，后台设备列表据此显示 WPF 当前版本。
                // 未注册版本提供器时不带该字段，服务端会保留库里上次上报的版本。
                appVersion = appVersionProvider?.CurrentVersion,
                // 中文注释：开机时长与本进程内存/句柄数随心跳上报，后台据此找出长期不重启、资源被拖满的收银机。
                systemUptimeSeconds = resources?.SystemUptimeSeconds,
                processWorkingSetMb = resources?.ProcessWorkingSetMb,
                processHandleCount = resources?.ProcessHandleCount,
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
