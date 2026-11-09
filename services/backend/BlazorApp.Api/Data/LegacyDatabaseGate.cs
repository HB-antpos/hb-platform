using Microsoft.Extensions.Configuration;

namespace BlazorApp.Api.Data;

/// <summary>
/// 旧库总开关：HQ（HOT_HQ_CLOUD）与 HBSales（HOT_CPT_CLOUD、HOT_POS_CLOUD）。
/// 关闭后三个旧库上下文都指向本机不可用地址，任何漏网的调用只会立刻失败，不会连到旧服务器。
/// </summary>
public static class LegacyDatabaseGate
{
    /// <summary>
    /// 关闭时使用的连接串。端口 1 没有 SQL Server 监听，1 秒内失败，不产生任何对外网络连接。
    /// </summary>
    internal const string DisabledConnectionString =
        "Server=127.0.0.1,1;Database=legacy_disconnected;User Id=disabled;Password=disabled;"
        + "Encrypt=False;Connect Timeout=1;ConnectRetryCount=0";

    private static volatile bool _hqEnabled = true;
    private static volatile bool _hbSalesEnabled = true;

    // 单元测试并行运行，覆盖值只在当前异步流内生效，不能改全局状态。
    private static readonly AsyncLocal<GateOverride?> TestOverride = new();

    public static bool HqEnabled => TestOverride.Value?.HqEnabled ?? _hqEnabled;

    public static bool HBSalesEnabled => TestOverride.Value?.HBSalesEnabled ?? _hbSalesEnabled;

    /// <summary>
    /// 应用启动时读取配置。未配置时按“已断开”处理：恢复旧库必须显式写 true。
    /// </summary>
    public static void Configure(IConfiguration configuration)
    {
        (_hqEnabled, _hbSalesEnabled) = ReadSwitches(configuration);
    }

    internal static (bool HqEnabled, bool HBSalesEnabled) ReadSwitches(IConfiguration configuration) => (
        configuration.GetValue("LegacyDatabases:HqEnabled", false),
        configuration.GetValue("LegacyDatabases:HBSalesEnabled", false)
    );

    internal static IDisposable OverrideForTests(bool hqEnabled, bool hbSalesEnabled)
    {
        var previous = TestOverride.Value;
        TestOverride.Value = new GateOverride(hqEnabled, hbSalesEnabled);
        return new OverrideScope(previous);
    }

    private sealed record GateOverride(bool HqEnabled, bool HBSalesEnabled);

    private sealed class OverrideScope(GateOverride? previous) : IDisposable
    {
        public void Dispose() => TestOverride.Value = previous;
    }
}
