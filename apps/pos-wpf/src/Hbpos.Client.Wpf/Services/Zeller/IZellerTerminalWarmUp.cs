namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// 预热 Zeller 刷卡机的 SDK 连接：在没人收款的空闲时间提前做完「新建 Terminal 之后第一次调用」的初始化，
/// 让下一笔扣款不用再付这笔冷启动的代价。
/// </summary>
/// <remarks>
/// 中文注释：预热必须让位于真实调用。SDK 同一时刻只能进一个调用，预热占着门会把扣款拖慢，等于拆东墙补西墙，
/// 所以实现要保证：门被占着就不预热；预热进行中真实调用到达时，预热立刻放弃（它不涉及钱，放弃没有副作用）。
/// 最坏情况和没有预热一样（新建 Terminal 后再调用），不会更慢。
/// </remarks>
public interface IZellerTerminalWarmUp
{
    /// <summary>
    /// 请求一次后台预热，立即返回，从不抛出。已经有可用的 Terminal、门被占着、或有真实调用在排队时什么都不做。
    /// </summary>
    void RequestWarmUp();
}
