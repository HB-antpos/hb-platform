namespace Hbpos.Contracts.Linkly;

public static class LinklyTimeoutConstants
{
    public static readonly TimeSpan BusinessWait = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(240);

    // CloudBackendAsync 结算启动后等待 Linkly 回调的上限：略长于 POS 的业务等待（180 秒），
    // 超时仍无回调就由服务端把会话收口为“结果未知”，不再无限期占用 POS 和终端。
    public static readonly TimeSpan SettlementCallbackTimeout = TimeSpan.FromMinutes(5);

    // CloudBackendAsync 下，遗留在设备/终端上的会话（仍活动，或已终态但客户端从未确认）超过这个时间没有任何更新，
    // 就认定为陈旧：新付款不再被它挡住，创建新交易会话时由服务端原子地顶替（见 TryCreateSessionSql）。
    // 一条真实在途的交易最长约 BusinessWait（180 秒）+ HttpTimeout（240 秒），10 分钟远大于它，
    // 所以陈旧会话不可能还有人在刷卡；比它新的仍按原样挡住新付款（409），由接管流程处理。
    public static readonly TimeSpan StaleSessionSupersedeAfter = TimeSpan.FromMinutes(10);
}
