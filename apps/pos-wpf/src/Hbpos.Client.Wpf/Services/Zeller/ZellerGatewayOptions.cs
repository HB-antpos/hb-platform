namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>网关的超时设置。默认值来自 2026-11-05 的真机实测，做成可配置是因为它们只是兜底的初始值。</summary>
public sealed record ZellerGatewayOptions
{
    /// <summary>
    /// 扣款或退款从发出起的最长等待。实测无人值守下不刷卡约 30 秒就返回超时，断网后最长 72 秒返回错误，
    /// 所以 150 秒还没结果就说明 SDK 不对劲。
    /// </summary>
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(150);

    /// <summary>收到「处理中」事件之后的最长等待。实测处理中到出结果通常 3–18 秒。</summary>
    public TimeSpan AfterProcessingTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 请求取消之后等 SDK 返回的时间。实测正常取消约 1 秒就返回，所以 3 秒还没返回就放弃这次调用。
    /// </summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>按 reference 查询的最长等待。实测查询约 0.2–3 秒；断网时 SDK 自己约 28 秒后报错。</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>检查配对（不弹界面）的最长等待。实测 0.4–2.2 秒。</summary>
    public TimeSpan InitialiseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 作废 Terminal 之后，等这么久再在空闲时重新预热。放弃一笔调用后，客户端常常马上要用刷卡机核实结果，
    /// 收银员也可能马上重试：让它们先走，它们会自己新建 Terminal，那时就不用预热了，避免白白建了又丢。
    /// </summary>
    public TimeSpan RewarmDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        Require(RewarmDelay, nameof(RewarmDelay));
        Require(TransactionTimeout, nameof(TransactionTimeout));
        Require(AfterProcessingTimeout, nameof(AfterProcessingTimeout));
        Require(CancelGrace, nameof(CancelGrace));
        Require(QueryTimeout, nameof(QueryTimeout));
        Require(InitialiseTimeout, nameof(InitialiseTimeout));
    }

    private static void Require(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "The timeout must be positive.");
        }
    }
}
