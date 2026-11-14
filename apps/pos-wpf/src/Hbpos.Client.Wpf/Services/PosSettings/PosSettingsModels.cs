namespace Hbpos.Client.Wpf.Services;

/// <summary>「业务开关」页上某一项的当前状态。</summary>
/// <param name="Key">设置项键（BlazorApp.Shared.Constants.PosSettingKeys，与服务端同一份清单）。</param>
/// <param name="Value">本机此刻实际生效的值（读自各开关真正的本机存储）。</param>
/// <param name="Source">总部下发值的来源（<c>PosSettingScopes</c> 的 global / store / device，或 <c>PosSettingSources.Default</c>）；没有下发值为 null。</param>
/// <param name="IsManaged">总部下发了值（含「默认」）。未下发的项沿用本机值，不锁定。</param>
/// <param name="IsLocked">已被总部锁定、不能在本机修改：下发了值，且联网探测为在线。离线时自动解锁。</param>
/// <param name="IsOfflineOverride">离线期间在本机临时改过、与总部下发值不同；恢复联网同步后会被总部值覆盖。</param>
public sealed record PosSettingState(
    string Key,
    bool Value,
    string? Source,
    bool IsManaged,
    bool IsLocked,
    bool IsOfflineOverride);

/// <summary>「业务开关」页要显示的整体状态。</summary>
/// <param name="Items">WPF 支持的每个可下发项各一项，顺序同 <c>PosSettingCatalog.All</c>（即「业务开关」页的显示顺序）。</param>
/// <param name="IsOnline">联网探测的结果（与状态栏「在线 / 离线」同一个判断）；尚未探测时按在线。</param>
/// <param name="LastSyncedAtUtc">最近一次成功同步的时间；从未同步过为 null。</param>
public sealed record PosSettingsView(
    IReadOnlyList<PosSettingState> Items,
    bool IsOnline,
    DateTimeOffset? LastSyncedAtUtc)
{
    public static PosSettingsView Empty { get; } = new([], true, null);

    /// <summary>是否有任何一项被总部锁定（用来决定要不要显示锁相关提示）。</summary>
    public bool HasLockedItems => Items.Any(item => item.IsLocked);

    /// <summary>离线中、且至少有一项总部下发的值：页面顶部要显示「离线中，可临时修改」黄条。</summary>
    public bool ShowsOfflineBanner => !IsOnline && Items.Any(item => item.IsManaged);

    public PosSettingState? Find(string key) =>
        Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
}

public enum PosSettingChangeOutcome
{
    /// <summary>已写入本机（离线临时修改，或该项总部没下发）。</summary>
    Applied,

    /// <summary>该项被总部锁定且当前在线，未修改。</summary>
    Locked,

    /// <summary>不认识的键，未修改。</summary>
    UnknownKey,

    /// <summary>写本机存储失败，未修改。</summary>
    Failed
}

public enum PosSettingsSyncOutcome
{
    /// <summary>总部有新设置，已写入本机并应用。</summary>
    Applied,

    /// <summary>总部设置与本机一致，无需更新。</summary>
    UpToDate,

    /// <summary>响应无效（缺指纹、串店等），未改动任何本机设置。</summary>
    Discarded,

    /// <summary>网络、服务端或本机读写失败，下一轮重试。</summary>
    Failed,

    /// <summary>服务端尚未部署该接口（404）：本机沿用各自的值，不锁定任何项。</summary>
    ServerUnsupported,

    /// <summary>设备认证尚未就绪，没有门店可同步。</summary>
    NotReady
}

/// <summary>一轮同步的结果。<see cref="Detail"/> 只放不含设置内容的原因说明。</summary>
public sealed record PosSettingsSyncResult(PosSettingsSyncOutcome Outcome, string? Detail = null);

public interface IPosSettingsService
{
    /// <summary>设置状态变化后触发（同步应用、离线修改、联网状态切换），可能在任意线程，订阅方需自行切回 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 最近一次构建好的状态（内存快照，同步读取，用于判断锁定与做功能开关）。
    /// 在 <see cref="RefreshAsync"/> / 同步 / 修改后更新；启动时在第一次 <see cref="RefreshAsync"/> 完成前是 <see cref="PosSettingsView.Empty"/>。
    /// </summary>
    PosSettingsView Current { get; }

    /// <summary>
    /// 现金存入 / 取出是否开启。读自内存快照；总部没下发过设置时按本机保存的值，默认关。
    /// 单独列出是因为收银页会随它显示或隐藏「钱箱」面板。
    /// </summary>
    bool IsCashMovementEnabled { get; }

    /// <summary>
    /// 从各开关真正的本机存储重新读取一遍并刷新 <see cref="Current"/>。进入「业务开关」页时调用，
    /// 也能发现在别处（例如原来的「付款方式」页）离线改过的值。
    /// </summary>
    Task<PosSettingsView> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 在本机修改一项。被总部锁定且在线时返回 <see cref="PosSettingChangeOutcome.Locked"/>；
    /// 离线时允许修改：记为「离线临时修改」、累计计数并写审计，恢复联网同步后被总部值覆盖。
    /// <paramref name="actor"/> 是当前收银员会话，只用于审计（POS_SETTING_OFFLINE_OVERRIDE）；没有登录收银员时传 null。
    /// </summary>
    Task<PosSettingChangeOutcome> TrySetAsync(
        string key,
        bool value,
        Hbpos.Contracts.Cashiers.CashierSessionDto? actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 立即同步一轮（设置页「立即同步」）。同一时刻只有一个同步在飞：已有同步进行中时直接复用它的结果。
    /// 只在取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<PosSettingsSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 搭 15 秒健康探测节拍调用：设备认证就绪后的第一次立即同步，之后每 4 拍（约 60 秒）一轮，换店后立即同步；
    /// 遇到 404 退避 10 分钟。本拍不需要同步时返回 null。
    /// </summary>
    Task<PosSettingsSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 告知联网探测结果（与状态栏同一个判断）。离线 → 已下发的项解锁；在线 → 重新锁定。
    /// 状态没变时不触发 <see cref="Changed"/>。
    /// </summary>
    void ReportConnectivity(bool isOnline);
}

/// <summary>
/// 一个可下发开关与它「真正的本机存储」之间的桥。下发值通过 <see cref="WriteAsync"/> 写进各自原来的存储，
/// 所以打印、付款、日结等消费方继续读原来的地方，一行都不用改；设置页和同步服务也都以 <see cref="ReadAsync"/> 读到的值为「实际生效值」。
/// </summary>
public interface IPosSettingBinding
{
    string Key { get; }

    Task<bool> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(bool value, CancellationToken cancellationToken = default);
}
