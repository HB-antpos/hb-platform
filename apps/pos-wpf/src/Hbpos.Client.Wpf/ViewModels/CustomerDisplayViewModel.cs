using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class CustomerDisplayViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _taxAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavings))]
    private decimal _savingsAmount;

    [ObservableProperty]
    private decimal _totalToPay;

    [ObservableProperty]
    private decimal _totalItemQuantity;

    [ObservableProperty]
    private int _skuCount;

    [ObservableProperty]
    private string _terminalName = "Terminal 01";

    [ObservableProperty]
    private bool _isReadyForPayment;

    [ObservableProperty]
    private AdvertisementPlaybackItemDto? _currentAdvertisement;

    [ObservableProperty]
    private bool _isAdvertisementAvailable;

    [ObservableProperty]
    private bool _isIdleAdvertisementVisible;

    private readonly List<AdvertisementPlaybackItemDto> _advertisements = [];
    private int _currentAdvertisementIndex = -1;

    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    public ObservableCollection<CustomerDisplayLine> Lines { get; } = [];

    public string TotalToPayLabel => "customer.totalToPay";

    public string ReadyForPaymentLabel => "customer.readyForPayment";

    public string InsertOrTapLabel => "customer.insertOrTap";

    public string SubtotalLabel => "Subtotal";

    public string TaxLabel => "Tax";

    public string SavingsLabel => "Savings";

    public bool HasSavings => SavingsAmount > 0m;

    public string CurrentAdvertisementTitle => CurrentAdvertisement?.Title ?? string.Empty;

    public string CurrentAdvertisementDescription => CurrentAdvertisement?.Description ?? string.Empty;

    public string? CurrentAdvertisementMediaUrl => CurrentAdvertisement?.MediaUrl;

    public bool IsCurrentAdvertisementImage =>
        CurrentAdvertisement is not null
        && string.Equals(CurrentAdvertisement.MediaType, "image", StringComparison.OrdinalIgnoreCase);

    public bool IsCurrentAdvertisementVideo =>
        CurrentAdvertisement is not null
        && string.Equals(CurrentAdvertisement.MediaType, "video", StringComparison.OrdinalIgnoreCase);

    public void LoadLines(IEnumerable<CustomerDisplayLine> lines, decimal subtotal, decimal savingsAmount)
    {
        var materialized = lines.ToList();
        // 退货行金额为负数，折扣上限只取销售行原价，避免混合购物车截断合法折扣。
        var maximumSavingsAmount = decimal.Round(
            materialized.Sum(line => Math.Max(0m, line.GrossAmount)),
            2,
            MidpointRounding.AwayFromZero);
        var normalizedSavingsAmount = decimal.Round(savingsAmount, 2, MidpointRounding.AwayFromZero);
        var effectiveSavingsAmount = Math.Clamp(normalizedSavingsAmount, 0m, maximumSavingsAmount);

        Lines.ReplaceWith(materialized);
        Subtotal = subtotal;
        SavingsAmount = effectiveSavingsAmount;
        TotalToPay = subtotal - effectiveSavingsAmount;
        // 客显 GST 是含税应付额里的税额组成部分，不能再加回应付金额。
        TaxAmount = decimal.Round(TotalToPay / 11m, 2, MidpointRounding.AwayFromZero);
        TotalItemQuantity = materialized.Sum(line => line.Quantity);
        SkuCount = materialized.Count;
        IsReadyForPayment = TotalToPay > 0m;
        RefreshIdleAdvertisementVisibility();
    }

    public void LoadAdvertisements(IEnumerable<AdvertisementPlaybackItemDto> advertisements)
    {
        var now = UtcNow();
        _advertisements.Clear();
        _advertisements.AddRange(advertisements.Where(advertisement => IsPlayableAdvertisement(advertisement, now)));
        IsAdvertisementAvailable = _advertisements.Count > 0;
        _currentAdvertisementIndex = IsAdvertisementAvailable ? 0 : -1;
        CurrentAdvertisement = IsAdvertisementAvailable ? _advertisements[0] : null;
        RefreshIdleAdvertisementVisibility();
    }

    public void AdvanceAdvertisement()
    {
        RemoveExpiredAdvertisements();
        if (_advertisements.Count == 0)
        {
            ClearAdvertisements();
            return;
        }

        if (CurrentAdvertisement is not null)
        {
            _currentAdvertisementIndex = _advertisements.FindIndex(advertisement =>
                EqualityComparer<AdvertisementPlaybackItemDto>.Default.Equals(advertisement, CurrentAdvertisement));
        }

        _currentAdvertisementIndex = (_currentAdvertisementIndex + 1) % _advertisements.Count;
        var nextAdvertisement = _advertisements[_currentAdvertisementIndex];

        // 只有一条广告时也要触发属性变更，让播放层重新开始下一轮。
        if (EqualityComparer<AdvertisementPlaybackItemDto?>.Default.Equals(CurrentAdvertisement, nextAdvertisement))
        {
            CurrentAdvertisement = null;
        }

        CurrentAdvertisement = nextAdvertisement;
    }

    public void SkipCurrentAdvertisement()
    {
        if (_advertisements.Count == 0)
        {
            ClearAdvertisements();
            return;
        }

        var currentIndex = _currentAdvertisementIndex;
        if (currentIndex < 0 || currentIndex >= _advertisements.Count)
        {
            currentIndex = 0;
        }

        // 播放失败的素材直接移出当前轮播，避免客显在坏素材上反复打转。
        _advertisements.RemoveAt(currentIndex);
        if (_advertisements.Count == 0)
        {
            ClearAdvertisements();
            return;
        }

        _currentAdvertisementIndex = currentIndex - 1;
        IsAdvertisementAvailable = true;
        AdvanceAdvertisement();
    }

    partial void OnCurrentAdvertisementChanged(AdvertisementPlaybackItemDto? value)
    {
        OnPropertyChanged(nameof(CurrentAdvertisementTitle));
        OnPropertyChanged(nameof(CurrentAdvertisementDescription));
        OnPropertyChanged(nameof(CurrentAdvertisementMediaUrl));
        OnPropertyChanged(nameof(IsCurrentAdvertisementImage));
        OnPropertyChanged(nameof(IsCurrentAdvertisementVideo));
    }

    partial void OnIsAdvertisementAvailableChanged(bool value)
    {
        RefreshIdleAdvertisementVisibility();
    }

    private void RefreshIdleAdvertisementVisibility()
    {
        IsIdleAdvertisementVisible = IsAdvertisementAvailable && Lines.Count == 0;
    }

    private void RemoveExpiredAdvertisements()
    {
        var now = UtcNow();
        // 切换广告前先剔除过期素材，当前片段可播完，但不会进入下一轮。
        _advertisements.RemoveAll(advertisement => !IsPlayableAdvertisement(advertisement, now));
    }

    private void ClearAdvertisements()
    {
        _currentAdvertisementIndex = -1;
        CurrentAdvertisement = null;
        IsAdvertisementAvailable = false;
        RefreshIdleAdvertisementVisibility();
    }

    private static bool IsPlayableAdvertisement(AdvertisementPlaybackItemDto advertisement, DateTimeOffset now)
    {
        return !string.IsNullOrWhiteSpace(advertisement.MediaUrl)
            && advertisement.EffectiveStart <= now
            && advertisement.EffectiveEnd >= now
            && (string.Equals(advertisement.MediaType, "image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(advertisement.MediaType, "video", StringComparison.OrdinalIgnoreCase));
    }
}
