using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class CardRefundProcessorRoutingTests
{
    [Theory]
    [InlineData("ANZ:P29R5YNFN4XMNN50")]
    [InlineData("  anz:P29R5YNFN4XMNN50 ")]
    [InlineData("ANZCLOUD:P29R5YNFN4XMNN50:RFN001")]
    [InlineData("ANZBACKEND:P29R5YNFN4XMNN50:session=abc:environment=Production")]
    public void Square_register_routes_linkly_original_to_linkly(string originalReference)
    {
        var settings = CreateSettings(CardProcessorKind.Square);

        var routed = CardRefundProcessorRouting.Resolve(settings, originalReference);

        Assert.Equal(CardProcessorKind.Linkly, routed.Processor);
        // 中文注释：只换通道，其余设置（环境、Linkly 连接参数、Square 配置）原样保留。
        Assert.Equal(settings with { Processor = CardProcessorKind.Linkly }, routed);
    }

    [Theory]
    [InlineData("SQ:payment-1")]
    [InlineData("MANUAL:1b72eb95fdc944e9b7c80b4d557322e5")]
    [InlineData("267836")]
    [InlineData("")]
    [InlineData(null)]
    public void Square_register_keeps_square_for_other_references(string? originalReference)
    {
        var settings = CreateSettings(CardProcessorKind.Square);

        Assert.Same(settings, CardRefundProcessorRouting.Resolve(settings, originalReference));
    }

    [Theory]
    [InlineData(CardProcessorKind.Linkly, "SQ:payment-1")]
    [InlineData(CardProcessorKind.Linkly, "ANZ:P29R5YNFN4XMNN50")]
    [InlineData(CardProcessorKind.None, "ANZ:P29R5YNFN4XMNN50")]
    [InlineData(CardProcessorKind.None, "SQ:payment-1")]
    public void Other_registers_keep_current_processor(CardProcessorKind processor, string originalReference)
    {
        var settings = CreateSettings(processor);

        Assert.Same(settings, CardRefundProcessorRouting.Resolve(settings, originalReference));
    }

    [Theory]
    [InlineData("ANZ:X", true)]
    [InlineData("ANZCLOUD:X:Y", true)]
    [InlineData("ANZBACKEND:X", true)]
    [InlineData("ANZX:Y", false)]
    [InlineData("SQ:X", false)]
    [InlineData("CARD_REFUND|refund=a|original=b", false)]
    public void Recognizes_linkly_reference_prefixes(string reference, bool expected)
    {
        Assert.Equal(expected, CardRefundProcessorRouting.IsLinklyReference(reference));
    }

    private static CardTerminalSettings CreateSettings(CardProcessorKind processor) => new(
        processor,
        CardTerminalEnvironment.Production,
        "127.0.0.1",
        2011,
        SquareAccessToken: null,
        SquareLocationId: "LOCATION-1",
        SquareDeviceId: "device:DEVICE-1",
        SquareApiBaseUrl: "https://connect.squareup.com/",
        TerminalTimeout: TimeSpan.FromSeconds(90));
}
