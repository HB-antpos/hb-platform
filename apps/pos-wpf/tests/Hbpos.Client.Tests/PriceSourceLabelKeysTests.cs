using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class PriceSourceLabelKeysTests
{
    [Theory]
    [InlineData("product", PriceSourceLabelKeys.Main)]
    [InlineData("store-retail", PriceSourceLabelKeys.Main)]
    [InlineData("set", PriceSourceLabelKeys.Set)]
    [InlineData("set-follow-main", PriceSourceLabelKeys.Set)]
    [InlineData("set-store-multi-code", PriceSourceLabelKeys.Set)]
    [InlineData("  SET  ", PriceSourceLabelKeys.Set)]
    [InlineData("multi-code", PriceSourceLabelKeys.MultiCode)]
    [InlineData("clearance", PriceSourceLabelKeys.Clearance)]
    public void ResolveKey_maps_known_server_labels_to_localization_keys(string label, string expectedKey)
    {
        Assert.Equal(expectedKey, PriceSourceLabelKeys.ResolveKey(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something-new")]
    public void ResolveKey_returns_null_for_unknown_labels_so_the_raw_text_is_shown(string? label)
    {
        Assert.Null(PriceSourceLabelKeys.ResolveKey(label));
    }
}
