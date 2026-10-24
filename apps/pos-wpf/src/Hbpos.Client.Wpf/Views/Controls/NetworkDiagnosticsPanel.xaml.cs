using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 设置页「设备注册」第二栏的网络检测卡（步骤区固定高度内部滚动 + 固定状态区），DataContext 为 NetworkDiagnosticsViewModel。
/// 「检测网络」「复制报告」按钮在设置页底栏，本控件不含。
/// </summary>
public partial class NetworkDiagnosticsPanel : UserControl
{
    public NetworkDiagnosticsPanel()
    {
        InitializeComponent();
    }
}
