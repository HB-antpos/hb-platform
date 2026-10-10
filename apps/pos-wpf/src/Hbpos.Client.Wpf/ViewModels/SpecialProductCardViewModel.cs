using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 特殊商品页单张卡片的显示状态：在 <see cref="SellableItemDto"/> 之上叠加
/// 购物车数量角标、编辑模式选中 / 移除确认、刚加入高亮和排序位置。
/// 商品本身仍是单一数据源，刷新列表时按商品编码复用同一张卡，避免整页闪烁。
/// </summary>
public sealed partial class SpecialProductCardViewModel : ObservableObject
{
    [ObservableProperty]
    private SellableItemDto _item;

    /// <summary>购物车里该商品的销售数量；0 表示不在购物车中。</summary>
    [ObservableProperty]
    private decimal _cartQuantity;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>编辑模式下点「移除」后卡片内先确认，误触不会直接删除。</summary>
    [ObservableProperty]
    private bool _isRemoveConfirming;

    [ObservableProperty]
    private bool _isJustAdded;

    /// <summary>该商品在特殊商品列表中的 1 基排序位置。</summary>
    [ObservableProperty]
    private int _position;

    public SpecialProductCardViewModel(SellableItemDto item)
    {
        _item = item;
    }

    public string ProductCode => Item.ProductCode;

    public string DisplayName => Item.DisplayName;

    public string? ItemNumber => Item.ItemNumber;

    public string LookupCode => Item.LookupCode;

    public string? ProductImage => Item.ProductImage;

    public decimal RetailPrice => Item.RetailPrice;

    public bool IsInCart => CartQuantity > 0m;

    public string CartQuantityText => string.Format(
        CultureInfo.InvariantCulture,
        "×{0:0.##}",
        CartQuantity);

    partial void OnItemChanged(SellableItemDto value)
    {
        OnPropertyChanged(nameof(ProductCode));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ItemNumber));
        OnPropertyChanged(nameof(LookupCode));
        OnPropertyChanged(nameof(ProductImage));
        OnPropertyChanged(nameof(RetailPrice));
    }

    partial void OnCartQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(IsInCart));
        OnPropertyChanged(nameof(CartQuantityText));
    }
}

/// <summary>右侧购物车摘要里的一行。</summary>
public sealed record SpecialCartSummaryLine(string DisplayName, string QuantityText, string AmountText);
