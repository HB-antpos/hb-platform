namespace BlazorApp.Api.Features.StoreOrders.Common;

/// <summary>
/// 订单明细保存层使用的「分店订单退货」权限判定。独立成窄接口，是为了让行命令存储不依赖整套访问策略，
/// 未注册时（旧构造路径、测试）按「无权限」处理，只会更严，不会放开退货录入。
/// </summary>
internal interface IStoreOrderReturnPermission
{
    Task<bool> CanRecordReturnsAsync();
}

internal sealed class StoreOrderReturnPermission(IStoreOrderAccessPolicy accessPolicy)
    : IStoreOrderReturnPermission
{
    public Task<bool> CanRecordReturnsAsync() => accessPolicy.CanRecordStoreOrderReturnsAsync();
}
