using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ConfirmationDialogServiceTests
{
    [Theory]
    [InlineData("exit", "Exit", "Are you sure you want to exit the POS application?", "Exit", true)]
    [InlineData("reset", "Reset test sales data", "Delete all test sales data? This action cannot be undone.", "Reset", true)]
    [InlineData("full-first-payment", "Confirm lay-by order", "The first lay-by payment covers the full order amount. Continue creating a lay-by order?", "Confirm", false)]
    [InlineData("pickup", "Confirm pickup", "This lay-by order is fully paid. Confirm customer pickup now?", "Confirm", false)]
    [InlineData("linkly-settlement", "Confirm Linkly settlement", "Submit the Linkly settlement for 8/3/2026 to the card terminal and print the bank receipt?", "Settle & Print", true)]
    [InlineData("held-order-delete", "Delete held sale?", "This permanently removes the local hold. A shared hold is cancelled online first.", "Delete held sale", true)]
    public async Task Confirmation_scenarios_publish_expected_english_content(
        string scenario,
        string expectedTitle,
        string expectedMessage,
        string expectedConfirmButton,
        bool expectedDestructive)
    {
        var service = new WpfConfirmationDialogService(new LocalizationService());
        var presenter = Assert.IsAssignableFrom<IConfirmationDialogPresenter>(service);

        var resultTask = OpenAsync(service, scenario);

        Assert.True(presenter.IsOpen);
        Assert.Equal(expectedTitle, presenter.TitleText);
        Assert.Equal(expectedMessage, presenter.MessageText);
        Assert.Equal(expectedConfirmButton, presenter.ConfirmButtonText);
        Assert.Equal("Cancel", presenter.CancelButtonText);
        Assert.Equal(expectedDestructive, presenter.IsDestructive);

        presenter.CancelCommand.Execute(null);
        Assert.False(await resultTask);
    }

    [Fact]
    public async Task Confirm_and_cancel_complete_once_with_expected_result()
    {
        var service = new WpfConfirmationDialogService(new LocalizationService());
        var presenter = (IConfirmationDialogPresenter)service;

        var confirmedTask = service.ConfirmExitApplicationAsync();
        presenter.ConfirmCommand.Execute(null);
        presenter.ConfirmCommand.Execute(null);

        Assert.True(await confirmedTask);
        Assert.False(presenter.IsOpen);

        var cancelledTask = service.ConfirmResetTestSalesDataAsync();
        presenter.CancelCommand.Execute(null);
        presenter.CancelCommand.Execute(null);

        Assert.False(await cancelledTask);
        Assert.False(presenter.IsOpen);
    }

    [Fact]
    public async Task Concurrent_confirmation_fails_closed_without_replacing_open_content()
    {
        var service = new WpfConfirmationDialogService(new LocalizationService());
        var presenter = (IConfirmationDialogPresenter)service;
        var firstTask = service.ConfirmExitApplicationAsync();

        var secondResult = await service.ConfirmResetTestSalesDataAsync();

        Assert.False(secondResult);
        Assert.Equal("Exit", presenter.TitleText);
        Assert.Equal("Are you sure you want to exit the POS application?", presenter.MessageText);
        presenter.CancelCommand.Execute(null);
        Assert.False(await firstTask);
    }

    [Fact]
    public async Task Open_confirmation_refreshes_when_culture_changes()
    {
        var localization = new LocalizationService();
        var service = new WpfConfirmationDialogService(localization);
        var presenter = (IConfirmationDialogPresenter)service;
        var resultTask = service.ConfirmExitApplicationAsync();

        localization.SetCulture("zh-CN");

        Assert.Equal("退出软件", presenter.TitleText);
        Assert.Equal("确定要退出收银软件吗？", presenter.MessageText);
        Assert.Equal("退出软件", presenter.ConfirmButtonText);
        Assert.Equal("取消", presenter.CancelButtonText);
        presenter.CancelCommand.Execute(null);
        Assert.False(await resultTask);
    }

    [Fact]
    public async Task Date_range_reupload_confirmation_formats_count_batches_and_localizes_open_dialog()
    {
        var localization = new LocalizationService();
        var service = new WpfConfirmationDialogService(localization);
        var presenter = (IConfirmationDialogPresenter)service;

        var resultTask = service.ConfirmOrderDateRangeReuploadAsync(
            501,
            2,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 2));

        Assert.Equal("Confirm date-range reupload", presenter.TitleText);
        Assert.Contains("501", presenter.MessageText);
        Assert.Contains("2 batch", presenter.MessageText);

        localization.SetCulture("zh-CN");

        Assert.Equal("确认重传日期范围", presenter.TitleText);
        Assert.Contains("501", presenter.MessageText);
        Assert.Contains("2 批", presenter.MessageText);
        presenter.CancelCommand.Execute(null);
        Assert.False(await resultTask);
    }

    [Fact]
    public async Task Dispose_stops_receiving_culture_changes()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var service = new WpfConfirmationDialogService(localization);
        var resultTask = service.ConfirmExitApplicationAsync();
        var changeCount = 0;
        service.PropertyChanged += (_, _) => changeCount++;

        try
        {
            service.Dispose();
            service.Dispose();
            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal(0, changeCount);
            service.CancelCommand.Execute(null);
            Assert.False(await resultTask);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    private static Task<bool> OpenAsync(IConfirmationDialogService service, string scenario) =>
        scenario switch
        {
            "exit" => service.ConfirmExitApplicationAsync(),
            "reset" => service.ConfirmResetTestSalesDataAsync(),
            "full-first-payment" => service.ConfirmInstallmentFullFirstPaymentAsync(),
            "pickup" => service.ConfirmInstallmentPickupAfterPaidOffAsync(),
            "linkly-settlement" => service.ConfirmLinklySettlementAsync(new DateTime(2026, 8, 3)),
            "held-order-delete" => service.ConfirmHeldOrderCancellationAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
}
