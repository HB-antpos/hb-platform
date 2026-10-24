using System.Windows;
using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 服务器地址面板。登录页、设备注册页与设置页共用。
/// 默认布局（登录页 / 设备注册页）把地址、测试连接 / 保存、入口状态、网络检测与一键修复放在同一张卡里，保持不变；
/// 设置页首屏重设计把操作按钮统一放到页面底栏、网络检测拆到第二栏，所以给它一个开关 <see cref="IsSettingsLayout"/>。
/// </summary>
public partial class ApiServerSettingsPanel : UserControl
{
    /// <summary>
    /// 设置页首屏布局开关（默认 false = 登录页 / 设备注册页的原布局）。
    /// 为真时：隐藏面板内置的「测试连接 / 保存地址」按钮与整块网络检测（对应命令由设置页底栏绑定同一批 ViewModel 命令），
    /// 输入框与填充按钮放大到 44 高，入口状态改为「首选入口 / 当前实际入口」两行加「回退中」芯片。
    /// </summary>
    public static readonly DependencyProperty IsSettingsLayoutProperty = DependencyProperty.Register(
        nameof(IsSettingsLayout),
        typeof(bool),
        typeof(ApiServerSettingsPanel),
        new PropertyMetadata(false));

    public ApiServerSettingsPanel()
    {
        InitializeComponent();
    }

    public bool IsSettingsLayout
    {
        get => (bool)GetValue(IsSettingsLayoutProperty);
        set => SetValue(IsSettingsLayoutProperty, value);
    }
}
