using System.Windows;
using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class DeviceRegistrationView : UserControl
{
    public DeviceRegistrationView()
    {
        InitializeComponent();
    }

    private void DiagnoseNetworkClick(object sender, RoutedEventArgs e)
    {
        // 命令已经开始检测；把页面上方的服务器地址面板滚动到可见位置，检测结果就显示在那里。
        ApiServerPanel.BringIntoView();
    }
}
