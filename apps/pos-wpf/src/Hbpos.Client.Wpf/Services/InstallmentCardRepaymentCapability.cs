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

    public static string? ToFamily(CardProcessorKind processor) => processor switch
    {
        CardProcessorKind.Square => InstallmentCardRepaymentProviders.Square,
        CardProcessorKind.Linkly => InstallmentCardRepaymentProviders.Linkly,
        CardProcessorKind.Zeller => InstallmentCardRepaymentProviders.Zeller,
        _ => null
    };
}
