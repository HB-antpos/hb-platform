using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class SpecialProductsView : UserControl
{
    private SpecialProductsViewModel? _subscribedViewModel;

    public SpecialProductsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 页面会被缓存复用：卸载时退订，重新加载时按当前 DataContext 再订阅。
        Loaded += (_, _) => Subscribe(DataContext as SpecialProductsViewModel);
        Unloaded += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Subscribe(e.NewValue as SpecialProductsViewModel);
    }

    private void Subscribe(SpecialProductsViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        Unsubscribe();
        if (viewModel is not null)
        {
            _subscribedViewModel = viewModel;
            viewModel.FocusProductRequested += OnFocusProductRequested;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.FocusProductRequested -= OnFocusProductRequested;
            _subscribedViewModel = null;
        }
    }

    /// <summary>
    /// 新增、改价、排序后把目标卡片滚到可见区域。卡片容器在集合刷新后才生成，
    /// 因此延后到布局完成再定位；找不到卡片（被筛选掉等）时静默忽略。
    /// </summary>
    private void OnFocusProductRequested(object? sender, string productCode)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                var card = _subscribedViewModel?.DisplayedCards.FirstOrDefault(item =>
                    string.Equals(item.ProductCode?.Trim(), productCode?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (card is null)
                {
                    return;
                }

                (SpecialProductGrid.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement)?.BringIntoView();
            }));
    }
}
