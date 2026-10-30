using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 历史页底栏分页的运行时契约（对应设计图）：分页控件在底栏最右侧，首页“上一页”置灰、末页“下一页”置灰，
/// 分期页签不显示。设置 HBPOS_HISTORY_PAGER_SCREENSHOTS 可导出整页截图用于和设计图对照。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class TransactionHistoryPagerRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private const int Width = 1366;
    private const int Height = 768;

    [Fact]
    public Task Pager_sits_at_the_right_end_of_the_footer_and_follows_page_state() => host.RunAsync(async _ =>
    {
        var remote = new PagedRemoteHistory(totalOrders: 120);
        var viewModel = new TransactionHistoryViewModel(
            new EmptyReceiptQuery(),
            null,
            remote,
            new PosSessionState("HB POS", "1002", "Main Branch", "Terminal 04", "C001", "Alice", false, 0));
        var view = new TransactionHistoryView();
        PaymentViewRuntimeStaTestHost.IsolateRadioGroups(view);
        view.DataContext = viewModel;
        try
        {
            viewModel.IsOnlineSourceSelected = true;
            await viewModel.LoadAsync();
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);

            var pager = Assert.IsType<StackPanel>(view.FindName("HistoryPager"));
            Assert.Equal(Visibility.Visible, pager.Visibility);
            var previous = FindButton(view, "TransactionHistoryPreviousPage");
            var next = FindButton(view, "TransactionHistoryNextPage");
            var label = FindText(view, "TransactionHistoryPageLabel");

            Assert.Equal(40, previous.ActualHeight);
            Assert.Equal(40, next.ActualHeight);
            Assert.Equal("Page 1", label.Text);
            Assert.False(previous.IsEnabled);
            Assert.True(next.IsEnabled);

            // 设计图：分页控件是底栏最右侧的一组，上一页 / 页码 / 下一页从左到右。
            var footer = (FrameworkElement)pager.Parent;
            var pagerRight = pager.TransformToAncestor(footer).TransformBounds(
                new Rect(0, 0, pager.ActualWidth, pager.ActualHeight)).Right;
            Assert.True(footer.ActualWidth - pagerRight < 1, $"pager right gap {footer.ActualWidth - pagerRight}");
            Assert.True(
                previous.TranslatePoint(default, pager).X < label.TranslatePoint(default, pager).X &&
                label.TranslatePoint(default, pager).X < next.TranslatePoint(default, pager).X);
            Save(view, "page1");

            await viewModel.NextPageCommand.ExecuteAsync(null);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.Equal("Page 2", label.Text);
            Assert.True(previous.IsEnabled);
            Assert.True(next.IsEnabled);

            await viewModel.NextPageCommand.ExecuteAsync(null);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.Equal("Page 3", label.Text);
            Assert.True(previous.IsEnabled);
            Assert.False(next.IsEnabled);
            Save(view, "page3");

            viewModel.IsInstallmentSourceSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.Equal(Visibility.Collapsed, pager.Visibility);
        }
        finally
        {
            view.DataContext = null;
        }
    });

    private static Button FindButton(DependencyObject root, string automationId) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(root)
            .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == automationId);

    private static TextBlock FindText(DependencyObject root, string automationId) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(root)
            .Single(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == automationId);

    private static void Save(FrameworkElement view, string name)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_HISTORY_PAGER_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, $"history-pager-{name}.png"));
        encoder.Save(file);
    }

    private sealed class EmptyReceiptQuery : IReceiptQueryService
    {
        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);
    }

    private sealed class PagedRemoteHistory(int totalOrders) : IRemoteOrderHistoryService
    {
        private readonly List<RemoteOrderHistorySummary> _orders = Enumerable.Range(0, totalOrders)
            .Select(index => new RemoteOrderHistorySummary(
                Guid.NewGuid(),
                "1002",
                "POS_1013_0239",
                "Xiaoman Cao",
                DateTimeOffset.Now.AddMinutes(-index),
                10m + index,
                0m,
                10m + index,
                1 + index % 7,
                index % 2 == 0 ? "Card" : "Cash",
                "Completed"))
            .ToList();

        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteOrderHistoryResult(_orders.Skip(query.Skip).Take(query.Take).ToList()));

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderReturnContextDto?>(null);

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
