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
    IAppVersionProvider? appVersionProvider = null) : IPosRuntimeStatusApiClient
{
    public async Task ReportAsync(
        PosRuntimeStatusReport report,
        CancellationToken cancellationToken = default)
    {
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
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
