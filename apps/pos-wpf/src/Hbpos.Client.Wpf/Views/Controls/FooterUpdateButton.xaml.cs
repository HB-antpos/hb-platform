using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>底部状态栏里的「最新版本」更新按钮；全部展示状态来自 <c>AppUpdateState</c>（DataContext）。</summary>
public partial class FooterUpdateButton : UserControl
{
    public FooterUpdateButton()
    {
        InitializeComponent();
    }
}
