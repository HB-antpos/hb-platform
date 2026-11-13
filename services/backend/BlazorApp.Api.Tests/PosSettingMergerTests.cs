using BlazorApp.Shared.Constants;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>收银端设置下发的三层合并与指纹：优先级、缺省值、按端过滤、指纹稳定性。</summary>
public sealed class PosSettingMergerTests
{
    private static Dictionary<string, bool> Layer(params (string Key, bool Value)[] items) =>
        items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    [Fact]
    public void Merge_设备优先于门店_门店优先于全局()
    {
        var global = Layer((PosSettingKeys.ManualCard, false), (PosSettingKeys.Voucher, false), (PosSettingKeys.OrderList, false));
        var store = Layer((PosSettingKeys.ManualCard, true), (PosSettingKeys.Voucher, true));
        var device = Layer((PosSettingKeys.Voucher, false));

        var merged = PosSettingMerger.Merge(global, store, device);

        Assert.Equal(new PosSettingEffective(true, PosSettingScopes.Store), merged[PosSettingKeys.ManualCard]);
        Assert.Equal(new PosSettingEffective(false, PosSettingScopes.Device), merged[PosSettingKeys.Voucher]);
        Assert.Equal(new PosSettingEffective(false, PosSettingScopes.Global), merged[PosSettingKeys.OrderList]);
    }

    [Fact]
    public void Merge_三层都没设置时沿用本机值_只有现金进出有缺省关()
    {
        var merged = PosSettingMerger.Merge(null, null, null);

        // 现金存入 / 取出是新功能，本机没有对应设置：缺省按「关」。
        Assert.Equal(new PosSettingEffective(false, PosSettingSources.Default), merged[PosSettingKeys.CashMovementEnabled]);
        // 其余项没设置 = 收银机沿用本机自己的值，绝不能被服务端当成「关」覆盖掉。
        foreach (var definition in PosSettingCatalog.All.Where(item => item.Key != PosSettingKeys.CashMovementEnabled))
        {
            Assert.Equal(new PosSettingEffective(null, PosSettingSources.None), merged[definition.Key]);
        }

        Assert.Equal([PosSettingKeys.CashMovementEnabled], PosSettingMerger.Managed(merged).Keys);
    }

    [Fact]
    public void Merge_按端过滤_WPF专属项不下发给iPad和手持()
    {
        var global = Layer((PosSettingKeys.NoSaleRescan, true), (PosSettingKeys.ManualCard, true), (PosSettingKeys.Voucher, true));

        var ipad = PosSettingMerger.Merge(global, null, null, PosSettingClientKinds.Ipad);
        var handheld = PosSettingMerger.Merge(global, null, null, PosSettingClientKinds.Handheld);
        var other = PosSettingMerger.Merge(global, null, null, PosSettingClientKinds.Other);

        Assert.DoesNotContain(PosSettingKeys.NoSaleRescan, ipad.Keys);
        Assert.Contains(PosSettingKeys.Voucher, ipad.Keys);
        Assert.DoesNotContain(PosSettingKeys.Voucher, handheld.Keys);
        Assert.Contains(PosSettingKeys.ManualCard, handheld.Keys);
        Assert.Empty(other);
    }

    [Fact]
    public void Merge_忽略清单里没有的键()
    {
        var merged = PosSettingMerger.Merge(Layer(("removed.setting", true)), null, null);

        Assert.DoesNotContain("removed.setting", merged.Keys);
    }

    [Fact]
    public void ComputeHash_与插入顺序无关_值或来源变化都会改变指纹()
    {
        var a = PosSettingMerger.Managed(PosSettingMerger.Merge(
            Layer((PosSettingKeys.ManualCard, true), (PosSettingKeys.Voucher, false)), null, null));
        var b = PosSettingMerger.Managed(PosSettingMerger.Merge(
            Layer((PosSettingKeys.Voucher, false), (PosSettingKeys.ManualCard, true)), null, null));
        var valueChanged = PosSettingMerger.Managed(PosSettingMerger.Merge(
            Layer((PosSettingKeys.ManualCard, false), (PosSettingKeys.Voucher, false)), null, null));
        // 同一个值，从全局变成门店设置：收银机设置页上的来源标签要跟着变，指纹必须不同。
        var sourceChanged = PosSettingMerger.Managed(PosSettingMerger.Merge(
            null, Layer((PosSettingKeys.ManualCard, true), (PosSettingKeys.Voucher, false)), null));

        var hash = PosSettingMerger.ComputeHash(a);
        Assert.Equal(hash, PosSettingMerger.ComputeHash(b));
        Assert.NotEqual(hash, PosSettingMerger.ComputeHash(valueChanged));
        Assert.NotEqual(hash, PosSettingMerger.ComputeHash(sourceChanged));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ComputeHash_没有任何已管理项时是固定值()
    {
        var empty = PosSettingMerger.ComputeHash(new Dictionary<string, PosSettingEffective>());

        // 空文本的 SHA-256。
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty);
    }

    [Theory]
    [InlineData("Windows", "wpf")]
    [InlineData("windows ", "wpf")]
    [InlineData("iPadOS", "ipad")]
    [InlineData("iOS", "ipad")]
    [InlineData("Android", "handheld")]
    [InlineData("Linux", "other")]
    [InlineData(null, "other")]
    public void ClientKindFromDeviceSystem_按设备系统推导端类型(string? system, string expected)
    {
        Assert.Equal(expected, PosSettingMerger.ClientKindFromDeviceSystem(system));
    }

    [Fact]
    public void Catalog_键唯一且现金进出是唯一的新增项()
    {
        var keys = PosSettingCatalog.All.Select(item => item.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal([PosSettingKeys.CashMovementEnabled], PosSettingCatalog.All.Where(item => item.IsNew).Select(item => item.Key));
        Assert.All(PosSettingCatalog.All, item => Assert.NotEmpty(item.ClientKinds));
    }
}
