namespace Hbpos.Contracts.Linkly;

public static class LinklyTimeoutConstants
{
    public static readonly TimeSpan BusinessWait = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(240);

    // CloudBackendAsync 结算启动后等待 Linkly 回调的上限：略长于 POS 的业务等待（180 秒），
    // 超时仍无回调就由服务端把会话收口为“结果未知”，不再无限期占用 POS 和终端。
    public static readonly TimeSpan SettlementCallbackTimeout = TimeSpan.FromMinutes(5);
}
