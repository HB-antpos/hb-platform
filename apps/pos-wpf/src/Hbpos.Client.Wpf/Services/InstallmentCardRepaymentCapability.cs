using Hbpos.Contracts.Installments;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 服务端是否允许用本机当前的刷卡通道做分期续付。新服务端按通道下发 <c>CardRepaymentProviders</c>；
/// 旧服务端没有这个字段时只能看 <c>CardRepaymentSupported</c>（旧服务端恒为 false）。
/// </summary>
public static class InstallmentCardRepaymentCapability
{
    public static bool IsSupported(InstallmentRepaymentCapabilitiesResponse capabilities, CardProcessorKind processor)
    {
        var family = ToFamily(processor);
        if (family is null)
        {
            return false;
        }

        return capabilities.CardRepaymentProviders is { } providers
            ? providers.Any(provider => string.Equals(
                InstallmentCardRepaymentProviders.NormalizeFamily(provider),
                family,
                StringComparison.Ordinal))
            : capabilities.CardRepaymentSupported;
    }

    /// <summary>手动刷卡不依赖本机刷卡机配置，只看服务端是否放开；旧服务端没有通道列表时一律不支持。</summary>
    public static bool IsManualSupported(InstallmentRepaymentCapabilitiesResponse capabilities) =>
        capabilities.CardRepaymentProviders?.Any(provider => string.Equals(
            InstallmentCardRepaymentProviders.NormalizeFamily(provider),
            InstallmentCardRepaymentProviders.Manual,
            StringComparison.Ordinal)) == true;

    public static string? ToFamily(CardProcessorKind processor) => processor switch
    {
        CardProcessorKind.Square => InstallmentCardRepaymentProviders.Square,
        CardProcessorKind.Linkly => InstallmentCardRepaymentProviders.Linkly,
        CardProcessorKind.Zeller => InstallmentCardRepaymentProviders.Zeller,
        _ => null
    };
}
