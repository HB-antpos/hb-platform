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
    [NotifyPropertyChangedFor(nameof(HasCashier))]
    [NotifyPropertyChangedFor(nameof(CashierInitial))]
    private string _cashierFirstName = string.Empty;

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

    // 当前广告位：false = 空闲全屏（播横版 + 通用），true = 收银时右侧（播竖版 + 通用）。
    private bool _isCheckoutSlot;

    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    public ObservableCollection<CustomerDisplayLine> Lines { get; } = [];

    public string TotalToPayLabel => "customer.totalToPay";

    public string ReadyForPaymentLabel => "customer.readyForPayment";

    public string InsertOrTapLabel => "customer.insertOrTap";

    public string SubtotalLabel => "Subtotal";

    public string TaxLabel => "Tax";

    public string SavingsLabel => "Savings";

    public bool HasSavings => SavingsAmount > 0m;

    public bool HasCashier => CashierFirstName.Length > 0;

    public string CashierInitial => HasCashier
        ? char.ConvertFromUtf32(char.ConvertToUtf32(CashierFirstName, 0)).ToUpperInvariant()
        : string.Empty;

    public void ApplyCashier(PosSessionState session)
    {
        // 紧急授权会话的收银员名是固定的 EMERGENCY，不是真人，客显只显示终端号。
        var isEmergencyOverride = session.CashierSession?.IsEmergencyOverride == true;
        CashierFirstName = isEmergencyOverride ? string.Empty : ResolveCashierFirstName(session.CashierName);
    }

    internal static string ResolveCashierFirstName(string? cashierName)
    {
        // 客显面向顾客，只露名不露姓：取全名按空白切分后的第一个词。
        var trimmed = cashierName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var separatorIndex = trimmed.IndexOfAny([' ', '\t', '　']);
        var firstName = separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];

        // 没填姓名的账号会回退成用户名；用户名若是邮箱就不展示，避免把邮箱露给顾客。
        if (firstName.Contains('@'))
        {
            return string.Empty;
        }

        // 用户名多为小写（如 lily），首字母大写后再展示。
        return char.ToUpperInvariant(firstName[0]) + firstName[1..];
    }

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

        // 购物车由空变有（空闲全屏 → 收银右侧）或反过来时广告位换了，正在播的广告不适合新位置就立即换一条。
        var isCheckoutSlot = materialized.Count > 0;
        if (isCheckoutSlot != _isCheckoutSlot)
        {
            _isCheckoutSlot = isCheckoutSlot;
            if (CurrentAdvertisement is not null && !IsEligibleForCurrentSlot(CurrentAdvertisement))
            {
                AdvanceAdvertisement();
            }
        }
    }

    public void LoadAdvertisements(IEnumerable<AdvertisementPlaybackItemDto> advertisements)
    {
        var now = UtcNow();
        _advertisements.Clear();
        _advertisements.AddRange(advertisements.Where(advertisement => IsPlayableAdvertisement(advertisement, now)));
        IsAdvertisementAvailable = _advertisements.Count > 0;
        // 从当前广告位可播的第一条开始（没有适合当前位置的广告时退回全部）。
        _currentAdvertisementIndex = IsAdvertisementAvailable ? FindNextEligibleIndex(-1) : -1;
        CurrentAdvertisement = _currentAdvertisementIndex >= 0 ? _advertisements[_currentAdvertisementIndex] : null;
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

        // 以当前广告在列表中的位置为起点往后找；当前广告已被剔除（过期/失败）时，沿用上次记下的位置继续轮转。
        var anchorIndex = _currentAdvertisementIndex;
        if (CurrentAdvertisement is not null)
        {
            var currentIndex = _advertisements.FindIndex(advertisement =>
                EqualityComparer<AdvertisementPlaybackItemDto>.Default.Equals(advertisement, CurrentAdvertisement));
            if (currentIndex >= 0)
            {
                anchorIndex = currentIndex;
            }
        }

        _currentAdvertisementIndex = FindNextEligibleIndex(anchorIndex);
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

    /// <summary>
    /// 从 <paramref name="anchorIndex"/> 之后（循环）找第一条适合当前广告位的广告；列表非空时一定有结果。
    /// </summary>
    private int FindNextEligibleIndex(int anchorIndex)
    {
        var count = _advertisements.Count;
        var start = ((anchorIndex % count) + count) % count;
        for (var offset = 1; offset <= count; offset++)
        {
            var index = (start + offset) % count;
            if (IsEligibleForCurrentSlot(_advertisements[index]))
            {
                return index;
            }
        }

        return (start + 1) % count;
    }

    private bool IsEligibleForCurrentSlot(AdvertisementPlaybackItemDto advertisement)
    {
        // 当前位置一条匹配的都没有时退回全部，保证广告位不空着。
        return MatchesSlot(advertisement, _isCheckoutSlot)
            || !_advertisements.Any(item => MatchesSlot(item, _isCheckoutSlot));
    }

    internal static bool MatchesSlot(AdvertisementPlaybackItemDto advertisement, bool isCheckoutSlot)
    {
        // 横版只在空闲全屏播、竖版只在收银右侧播；通用、空值或未知值（旧服务端、旧缓存）两处都播。
        var orientation = advertisement.Orientation?.Trim();
        if (string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase))
        {
            return !isCheckoutSlot;
        }

        if (string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase))
        {
            return isCheckoutSlot;
        }

        return true;
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
