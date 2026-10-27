using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 设置页的分类页面容器（卡片）：顶部可选二级页签条（<see cref="SubTabs"/>）、中间正文（<see cref="ContentControl.Content"/>，
/// 外包 ScrollViewer，仅作为内容超出时的兜底）、底部可选固定操作条（<see cref="Footer"/>，永不随正文滚动）。
/// 设计目标是 1366×632 视口下正文放得下、不出现滚动条；SubTabs / Footer 为 null 时对应行整体折叠。
/// 外观与模板见 Themes/PosTheme.xaml（样式键 SettingsPageHostStyle，并注册了隐式样式）。
/// </summary>
public sealed class SettingsPageHost : ContentControl
{
    public static readonly DependencyProperty SubTabsProperty = DependencyProperty.Register(
        nameof(SubTabs),
        typeof(object),
        typeof(SettingsPageHost),
        new FrameworkPropertyMetadata(null, OnSubTabsChanged));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer),
        typeof(object),
        typeof(SettingsPageHost),
        new FrameworkPropertyMetadata(null, OnFooterChanged));

    private static readonly DependencyPropertyKey HasSubTabsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasSubTabs),
        typeof(bool),
        typeof(SettingsPageHost),
        new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey HasFooterPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasFooter),
        typeof(bool),
        typeof(SettingsPageHost),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasSubTabsProperty = HasSubTabsPropertyKey.DependencyProperty;

    public static readonly DependencyProperty HasFooterProperty = HasFooterPropertyKey.DependencyProperty;

    private ScrollViewer? _bodyScrollViewer;

    static SettingsPageHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsPageHost),
            new FrameworkPropertyMetadata(typeof(SettingsPageHost)));
    }

    /// <summary>页面顶部的二级页签条（可选）。通常放一个分段条（Border + RadioButton 组）。</summary>
    public object? SubTabs
    {
        get => GetValue(SubTabsProperty);
        set => SetValue(SubTabsProperty, value);
    }

    /// <summary>页面底部固定操作条（可选）。推荐放一个 <see cref="SettingsFooterBar"/>。</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>是否设置了二级页签条；模板据此显示 / 折叠页签行。</summary>
    public bool HasSubTabs => (bool)GetValue(HasSubTabsProperty);

    /// <summary>是否设置了底部操作条；模板据此显示 / 折叠底栏行。</summary>
    public bool HasFooter => (bool)GetValue(HasFooterProperty);

    /// <summary>
    /// 正文外层的兜底 ScrollViewer（模板应用后才有值）。
    /// 视口测试用 <c>ScrollableHeight == 0</c> 断言「正文放得下、无需滚动」。
    /// </summary>
    public ScrollViewer? BodyScrollViewer => _bodyScrollViewer;

    /// <summary>
    /// SubTabs / Footer 不是 Content，需手动登记为逻辑子元素，
    /// 这样 DataContext 继承与 ElementName 绑定才与放在 Content 里的元素表现一致（同 HeaderedContentControl.Header）。
    /// </summary>
    protected override IEnumerator LogicalChildren
    {
        get
        {
            var children = new List<object>();
            var baseChildren = base.LogicalChildren;
            while (baseChildren.MoveNext())
            {
                children.Add(baseChildren.Current);
            }

            if (SubTabs is not null)
            {
                children.Add(SubTabs);
            }

            if (Footer is not null)
            {
                children.Add(Footer);
            }

            return children.GetEnumerator();
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bodyScrollViewer = GetTemplateChild("PART_BodyScrollViewer") as ScrollViewer;
    }

    private static void OnSubTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (SettingsPageHost)d;
        host.UntrackLogicalChild(e.OldValue);
        host.TrackLogicalChild(e.NewValue);
        host.SetValue(HasSubTabsPropertyKey, e.NewValue is not null);
    }

    private static void OnFooterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (SettingsPageHost)d;
        host.UntrackLogicalChild(e.OldValue);
        host.TrackLogicalChild(e.NewValue);
        host.SetValue(HasFooterPropertyKey, e.NewValue is not null);
    }

    private void TrackLogicalChild(object? child)
    {
        if (child is not null)
        {
            base.AddLogicalChild(child);
        }
    }

    private void UntrackLogicalChild(object? child)
    {
        if (child is not null)
        {
            base.RemoveLogicalChild(child);
        }
    }
}
