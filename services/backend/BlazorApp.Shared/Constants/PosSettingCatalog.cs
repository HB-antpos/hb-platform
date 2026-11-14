namespace BlazorApp.Shared.Constants;

/// <summary>收银端设置下发的三个层级：越具体越优先（设备 → 门店 → 全局）。</summary>
public static class PosSettingScopes
{
    public const string Global = "global";
    public const string Store = "store";
    public const string Device = "device";

    /// <summary>全局层没有 ScopeKey，库里固定存这个值，方便主键包含 ScopeKey。</summary>
    public const string GlobalScopeKey = "*";

    public static bool IsValid(string? scope) => scope is Global or Store or Device;
}

/// <summary>端类型，与设备回执的 ClientKind 同一份取值（wpf / ipad / handheld）。</summary>
public static class PosSettingClientKinds
{
    public const string Wpf = "wpf";
    public const string Ipad = "ipad";
    public const string Handheld = "handheld";
    public const string Other = "other";
}

/// <summary>
/// 生效值的来源：global / store / device 是对应层级设置的；default 是三层都没设置、但该项没有本机对应设置，
/// 由服务端给定的缺省值（目前只有「现金存入 / 取出」，默认关）；none 表示三层都没设置、收银机沿用本机自己的值。
/// </summary>
public static class PosSettingSources
{
    public const string Default = "default";
    public const string None = "none";
}

/// <summary>可下发的设置项键。键一经发布不能改名：收银端、服务端、后台三处都按这个字符串识别。</summary>
public static class PosSettingKeys
{
    public const string CashMovementEnabled = "cashMovement.enabled";
    public const string NoSaleRescan = "cashDrawer.noSaleRescan";
    public const string ReturnsRescan = "returns.rescan";
    public const string BankReceipt = "receipt.bankReceipt";
    public const string ManualCard = "payment.manualCard";
    public const string Voucher = "payment.voucher";
    public const string OrderList = "payment.orderList";
    public const string AutoRestart = "maintenance.autoRestart";
    public const string ZellerUnmatchedRefund = "zeller.unmatchedRefund";
}

/// <summary>一个可下发的设置项。DefaultWhenUnset 为 null 表示三层都没设置时收银机沿用本机值。</summary>
public sealed record PosSettingDefinition(
    string Key,
    IReadOnlyList<string> ClientKinds,
    bool? DefaultWhenUnset,
    bool IsNew);

/// <summary>
/// 可下发设置清单（权威来源）。只放业务开关，不放打印机端口、刷卡机地址、扫码枪灵敏度这类绑定具体机器的硬件参数。
/// 新增一项：这里加一行，后台页面、手机端、收银端各自补中英文文案即可，服务端合并与下发逻辑不用改。
/// </summary>
public static class PosSettingCatalog
{
    private static readonly string[] AllClients =
        [PosSettingClientKinds.Wpf, PosSettingClientKinds.Ipad, PosSettingClientKinds.Handheld];

    private static readonly string[] WpfOnly = [PosSettingClientKinds.Wpf];
    private static readonly string[] WpfAndIpad = [PosSettingClientKinds.Wpf, PosSettingClientKinds.Ipad];

    public static IReadOnlyList<PosSettingDefinition> All { get; } =
    [
        // 新功能：本机没有对应设置，三层都没设置时按「关」处理，由后台按店开启。
        new(PosSettingKeys.CashMovementEnabled, AllClients, false, true),
        new(PosSettingKeys.NoSaleRescan, WpfOnly, null, false),
        new(PosSettingKeys.ReturnsRescan, WpfOnly, null, false),
        new(PosSettingKeys.BankReceipt, WpfOnly, null, false),
        new(PosSettingKeys.ManualCard, AllClients, null, false),
        new(PosSettingKeys.Voucher, WpfAndIpad, null, false),
        new(PosSettingKeys.OrderList, WpfOnly, null, false),
        new(PosSettingKeys.AutoRestart, WpfOnly, null, false),
        new(PosSettingKeys.ZellerUnmatchedRefund, WpfOnly, null, false),
    ];

    private static readonly Dictionary<string, PosSettingDefinition> ByKey =
        All.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static bool IsKnownKey(string? key) => key is not null && ByKey.ContainsKey(key);

    public static PosSettingDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>该设置项是否适用于这个端；未知端（other）一个都不适用。</summary>
    public static bool IsSupportedBy(string key, string clientKind) =>
        ByKey.TryGetValue(key, out var definition)
        && definition.ClientKinds.Contains(clientKind, StringComparer.Ordinal);
}
