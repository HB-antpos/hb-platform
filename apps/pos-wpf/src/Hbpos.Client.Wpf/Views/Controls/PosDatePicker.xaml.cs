using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 触屏日期选择器：整块日期按钮点开大格子月历，可选前后一天按钮。
/// 日期文字、月份标题、星期行都按界面语言（LocalizationResourceProvider）生成，不读线程区域性，
/// 切换语言后立即刷新；不允许未来日期时，未来格子、后一天和下月按钮都不可点。
/// </summary>
public partial class PosDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate),
        typeof(DateTime?),
        typeof(PosDatePicker),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnDisplayPropertyChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(PosDatePicker),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ShowDayStepButtonsProperty = DependencyProperty.Register(
        nameof(ShowDayStepButtons),
        typeof(bool),
        typeof(PosDatePicker),
        new PropertyMetadata(false, OnDisplayPropertyChanged));

    public static readonly DependencyProperty AllowFutureDatesProperty = DependencyProperty.Register(
        nameof(AllowFutureDates),
        typeof(bool),
        typeof(PosDatePicker),
        new PropertyMetadata(false, OnDisplayPropertyChanged));

    public static readonly DependencyProperty IncludeDayOfWeekProperty = DependencyProperty.Register(
        nameof(IncludeDayOfWeek),
        typeof(bool),
        typeof(PosDatePicker),
        new PropertyMetadata(true, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ShowDropDownGlyphProperty = DependencyProperty.Register(
        nameof(ShowDropDownGlyph),
        typeof(bool),
        typeof(PosDatePicker),
        new PropertyMetadata(true, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ShowCalendarIconProperty = DependencyProperty.Register(
        nameof(ShowCalendarIcon),
        typeof(bool),
        typeof(PosDatePicker),
        new PropertyMetadata(true, OnDisplayPropertyChanged));

    public static readonly DependencyProperty StepButtonWidthProperty = DependencyProperty.Register(
        nameof(StepButtonWidth),
        typeof(double),
        typeof(PosDatePicker),
        new PropertyMetadata(48d));

    public static readonly DependencyProperty ButtonHeightProperty = DependencyProperty.Register(
        nameof(ButtonHeight),
        typeof(double),
        typeof(PosDatePicker),
        new PropertyMetadata(48d));

    public static readonly DependencyProperty DateButtonMinWidthProperty = DependencyProperty.Register(
        nameof(DateButtonMinWidth),
        typeof(double),
        typeof(PosDatePicker),
        new PropertyMetadata(220d));

    public static readonly DependencyProperty DateFontSizeProperty = DependencyProperty.Register(
        nameof(DateFontSize),
        typeof(double),
        typeof(PosDatePicker),
        new PropertyMetadata(16d));

    private DateTime _displayMonth = DateTime.Today;
    private bool _isSubscribedToCulture;

    public PosDatePicker()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshDisplay();
    }

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>日期上方的小标签（如「营业日期」）；为空时不占行。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool ShowDayStepButtons
    {
        get => (bool)GetValue(ShowDayStepButtonsProperty);
        set => SetValue(ShowDayStepButtonsProperty, value);
    }

    public bool AllowFutureDates
    {
        get => (bool)GetValue(AllowFutureDatesProperty);
        set => SetValue(AllowFutureDatesProperty, value);
    }

    public bool IncludeDayOfWeek
    {
        get => (bool)GetValue(IncludeDayOfWeekProperty);
        set => SetValue(IncludeDayOfWeekProperty, value);
    }

    /// <summary>日期右侧的下拉箭头；筛选栏等窄处可隐藏以省宽度。</summary>
    public bool ShowDropDownGlyph
    {
        get => (bool)GetValue(ShowDropDownGlyphProperty);
        set => SetValue(ShowDropDownGlyphProperty, value);
    }

    /// <summary>日期按钮左侧的日历图标；顶栏等窄处有前后一天箭头时可隐藏以省宽度。</summary>
    public bool ShowCalendarIcon
    {
        get => (bool)GetValue(ShowCalendarIconProperty);
        set => SetValue(ShowCalendarIconProperty, value);
    }

    /// <summary>前后一天按钮的宽度，触屏下不要小于 44。</summary>
    public double StepButtonWidth
    {
        get => (double)GetValue(StepButtonWidthProperty);
        set => SetValue(StepButtonWidthProperty, value);
    }

    public double ButtonHeight
    {
        get => (double)GetValue(ButtonHeightProperty);
        set => SetValue(ButtonHeightProperty, value);
    }

    public double DateButtonMinWidth
    {
        get => (double)GetValue(DateButtonMinWidthProperty);
        set => SetValue(DateButtonMinWidthProperty, value);
    }

    public double DateFontSize
    {
        get => (double)GetValue(DateFontSizeProperty);
        set => SetValue(DateFontSizeProperty, value);
    }

    private static void OnDisplayPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PosDatePicker)d).RefreshDisplay();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribedToCulture)
        {
            // 提供者是全局单例：只在控件挂到界面期间订阅，卸载时退订，避免页面实例被单例长期引用。
            LocalizationResourceProvider.Instance.PropertyChanged += OnLocalizationChanged;
            _isSubscribedToCulture = true;
        }

        RefreshDisplay();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribedToCulture)
        {
            LocalizationResourceProvider.Instance.PropertyChanged -= OnLocalizationChanged;
            _isSubscribedToCulture = false;
        }

        CalendarPopup.IsOpen = false;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationResourceProvider.CurrentCulture))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RefreshDisplay();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(RefreshDisplay));
        }
    }

    private void RefreshDisplay()
    {
        var culture = LocalizationResourceProvider.Instance.CurrentCulture;
        var today = DateTime.Today;

        DateText.Text = SelectedDate is { } date
            ? PosDatePickerCalendar.FormatDate(date, culture, IncludeDayOfWeek)
            : string.Empty;
        LabelText.Visibility = string.IsNullOrWhiteSpace(Label) ? Visibility.Collapsed : Visibility.Visible;
        DropDownGlyph.Visibility = ShowDropDownGlyph ? Visibility.Visible : Visibility.Collapsed;
        CalendarIcon.Visibility = ShowCalendarIcon ? Visibility.Visible : Visibility.Collapsed;

        var stepVisibility = ShowDayStepButtons ? Visibility.Visible : Visibility.Collapsed;
        PreviousDayButton.Visibility = stepVisibility;
        NextDayButton.Visibility = stepVisibility;
        NextDayButton.IsEnabled = PosDatePickerCalendar.IsSelectable(
            (SelectedDate ?? today).Date.AddDays(1),
            today,
            AllowFutureDates);

        if (CalendarPopup.IsOpen)
        {
            RefreshCalendar();
        }
    }

    private void RefreshCalendar()
    {
        var culture = LocalizationResourceProvider.Instance.CurrentCulture;
        var today = DateTime.Today;

        MonthTitleText.Text = PosDatePickerCalendar.FormatMonthTitle(_displayMonth, culture);
        DayNameItems.ItemsSource = PosDatePickerCalendar.GetDayNames(culture);
        DayItems.ItemsSource = PosDatePickerCalendar.BuildDayCells(
            _displayMonth,
            SelectedDate,
            today,
            AllowFutureDates,
            culture);
        NextMonthButton.IsEnabled = PosDatePickerCalendar.CanShowNextMonth(_displayMonth, today, AllowFutureDates);
    }

    private void SelectDate(DateTime date)
    {
        // 跨午夜后「今天」会变：每次选择都按当前时刻再判一次，未来日期一律不接受。
        if (!PosDatePickerCalendar.IsSelectable(date, DateTime.Today, AllowFutureDates))
        {
            return;
        }

        // SetCurrentValue 保留调用方的双向绑定，把新日期写回 ViewModel。
        SetCurrentValue(SelectedDateProperty, date.Date);
        CalendarPopup.IsOpen = false;
    }

    private void DateButtonClick(object sender, RoutedEventArgs e)
    {
        var anchor = SelectedDate ?? DateTime.Today;
        _displayMonth = new DateTime(anchor.Year, anchor.Month, 1);
        CalendarPopup.IsOpen = true;
        RefreshCalendar();
    }

    private void PreviousDayButtonClick(object sender, RoutedEventArgs e)
    {
        SelectDate((SelectedDate ?? DateTime.Today).Date.AddDays(-1));
    }

    private void NextDayButtonClick(object sender, RoutedEventArgs e)
    {
        SelectDate((SelectedDate ?? DateTime.Today).Date.AddDays(1));
    }

    private void PreviousMonthButtonClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RefreshCalendar();
    }

    private void NextMonthButtonClick(object sender, RoutedEventArgs e)
    {
        if (!PosDatePickerCalendar.CanShowNextMonth(_displayMonth, DateTime.Today, AllowFutureDates))
        {
            return;
        }

        _displayMonth = _displayMonth.AddMonths(1);
        RefreshCalendar();
    }

    private void DayButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateTime date })
        {
            SelectDate(date);
        }
    }

    private void YesterdayButtonClick(object sender, RoutedEventArgs e)
    {
        SelectDate(DateTime.Today.AddDays(-1));
    }

    private void TodayButtonClick(object sender, RoutedEventArgs e)
    {
        SelectDate(DateTime.Today);
    }
}
