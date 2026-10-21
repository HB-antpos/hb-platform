using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>「一键改电脑名」面板：取名 → 页内二次确认 → 授权后改名 → 提示重启；每一步的失败都要能回到可重试的状态。</summary>
public sealed class ComputerRenameViewModelTests
{
    private static ComputerRenameViewModel Create(FakeRenameService service, string? storeCode = "2010") =>
        new(service, (_, fallback) => fallback, () => storeCode);

    [Fact]
    public void Panel_is_hidden_until_a_hardware_conflict_is_reported()
    {
        var viewModel = Create(new FakeRenameService());

        Assert.False(viewModel.IsVisible);
        Assert.Equal(ComputerRenameStep.Hidden, viewModel.Step);
        Assert.False(viewModel.CanRunPrimary);
    }

    [Fact]
    public void Hardware_conflict_offers_the_rename_without_touching_the_system()
    {
        var service = new FakeRenameService();
        var viewModel = Create(service);

        viewModel.ShowForHardwareConflict();

        Assert.True(viewModel.IsVisible);
        Assert.Equal(ComputerRenameStep.Offer, viewModel.Step);
        Assert.True(viewModel.CanRunPrimary);
        Assert.False(viewModel.IsCancelVisible);
        Assert.Contains("hardware ID", viewModel.MessageText);
        Assert.Contains("The other computer is not changed", viewModel.MessageText);
        Assert.Empty(service.RenamedTo);
        Assert.Equal(0, service.RestartCalls);
    }

    [Fact]
    public async Task Offer_then_confirm_step_shows_old_and_new_names_and_can_be_cancelled()
    {
        var viewModel = Create(new FakeRenameService { State = new("BF-202508261629", null, false) });
        viewModel.ShowForHardwareConflict();

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ComputerRenameStep.Confirm, viewModel.Step);
        Assert.StartsWith("HB-2010-", viewModel.ProposedName);
        Assert.True(ComputerNamePolicy.IsValid(viewModel.ProposedName));
        Assert.Contains("BF-202508261629", viewModel.MessageText);
        Assert.Contains(viewModel.ProposedName, viewModel.MessageText);
        Assert.Contains("administrator", viewModel.MessageText);
        Assert.True(viewModel.IsCancelVisible);
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(ComputerRenameStep.Offer, viewModel.Step);
        Assert.False(viewModel.IsCancelVisible);
    }

    [Fact]
    public async Task Confirming_renames_to_the_proposed_name_and_waits_for_restart()
    {
        var service = new FakeRenameService();
        var viewModel = Create(service);
        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);
        var proposed = viewModel.ProposedName;

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(new[] { proposed }, service.RenamedTo);
        Assert.Equal(ComputerRenameStep.RestartPending, viewModel.Step);
        Assert.Contains(proposed, viewModel.MessageText);
        Assert.Equal("Restart now", viewModel.PrimaryButtonText);
        Assert.False(viewModel.IsBusy);
        // 改名后不会自动重启：必须由用户点「立即重启」。
        Assert.Equal(0, service.RestartCalls);
    }

    [Fact]
    public async Task Declining_the_windows_authorization_changes_nothing_and_allows_trying_again()
    {
        var service = new FakeRenameService { RenameResult = false };
        var viewModel = Create(service);
        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ComputerRenameStep.Offer, viewModel.Step);
        Assert.Equal("The rename was cancelled. Nothing was changed.", viewModel.Notice);
        Assert.True(viewModel.HasNotice);
        Assert.True(viewModel.CanRunPrimary);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task A_failed_rename_reports_the_reason_and_allows_trying_again()
    {
        var service = new FakeRenameService { RenameException = new InvalidOperationException("script exit code 1") };
        var viewModel = Create(service);
        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ComputerRenameStep.Offer, viewModel.Step);
        Assert.Equal("Could not rename the computer: script exit code 1", viewModel.Notice);
        Assert.True(viewModel.CanRunPrimary);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Restart_now_restarts_once_and_blocks_further_clicks()
    {
        var service = new FakeRenameService();
        var viewModel = Create(service);
        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);
        await viewModel.PrimaryCommand.ExecuteAsync(null);

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(1, service.RestartCalls);
        Assert.True(viewModel.IsRestartRequested);
        Assert.False(viewModel.CanRunPrimary);
        Assert.Contains("Restarting shortly", viewModel.Notice);
        Assert.False(viewModel.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_restart_is_reported_and_can_be_retried()
    {
        var service = new FakeRenameService { RestartException = new InvalidOperationException("no permission") };
        var viewModel = Create(service);
        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);
        await viewModel.PrimaryCommand.ExecuteAsync(null);

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("Could not restart the computer: no permission", viewModel.Notice);
        Assert.False(viewModel.IsRestartRequested);
        Assert.True(viewModel.CanRunPrimary);
    }

    [Fact]
    public void A_rename_that_is_still_waiting_for_restart_is_not_offered_again()
    {
        var service = new FakeRenameService { State = new("BF-202508261629", "HB-2010-K7M2", false) };
        var viewModel = Create(service);

        viewModel.ShowForHardwareConflict();

        Assert.Equal(ComputerRenameStep.RestartPending, viewModel.Step);
        Assert.Equal("HB-2010-K7M2", viewModel.ProposedName);
        Assert.Contains("HB-2010-K7M2", viewModel.MessageText);
        Assert.Equal("Restart now", viewModel.PrimaryButtonText);
        Assert.True(viewModel.CanRunPrimary);
    }

    [Fact]
    public void A_domain_joined_computer_gets_an_explanation_and_no_buttons()
    {
        var service = new FakeRenameService { State = new("PC01", null, true) };
        var viewModel = Create(service);

        viewModel.ShowForHardwareConflict();

        Assert.Equal(ComputerRenameStep.Unavailable, viewModel.Step);
        Assert.True(viewModel.IsVisible);
        Assert.False(viewModel.HasActions);
        Assert.False(viewModel.CanRunPrimary);
        Assert.Contains("domain", viewModel.MessageText);
    }

    [Fact]
    public void Failing_to_read_the_computer_state_still_offers_the_rename()
    {
        var service = new FakeRenameService { StateException = new UnauthorizedAccessException("registry") };
        var viewModel = Create(service);

        viewModel.ShowForHardwareConflict();

        Assert.Equal(ComputerRenameStep.Offer, viewModel.Step);
        Assert.True(viewModel.CanRunPrimary);
    }

    [Fact]
    public async Task Without_a_store_code_the_new_name_has_no_store_part()
    {
        var viewModel = Create(new FakeRenameService(), storeCode: null);
        viewModel.ShowForHardwareConflict();

        await viewModel.PrimaryCommand.ExecuteAsync(null);

        Assert.Matches("^HB-[2-9A-Z]{4}$", viewModel.ProposedName);
    }

    [Fact]
    public async Task Reset_hides_the_panel_except_while_a_restart_is_pending()
    {
        var viewModel = Create(new FakeRenameService());
        viewModel.ShowForHardwareConflict();

        viewModel.Reset();
        Assert.False(viewModel.IsVisible);

        viewModel.ShowForHardwareConflict();
        await viewModel.PrimaryCommand.ExecuteAsync(null);
        await viewModel.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(ComputerRenameStep.RestartPending, viewModel.Step);

        // 又点了一次激活得到别的错误：「请重启」的提示不能丢，重启之前改名状态一直有效。
        viewModel.Reset();

        Assert.Equal(ComputerRenameStep.RestartPending, viewModel.Step);
        Assert.True(viewModel.IsVisible);
    }

    [Fact]
    public void Texts_come_from_the_translation_function()
    {
        var service = new FakeRenameService();
        var viewModel = new ComputerRenameViewModel(
            service,
            (key, _) => $"[{key}]",
            () => "2010");

        viewModel.ShowForHardwareConflict();

        Assert.Equal("[deviceActivation.rename.title]", viewModel.TitleText);
        Assert.Equal("[deviceActivation.rename.offer]", viewModel.MessageText);
        Assert.Equal("[deviceActivation.rename.button.rename]", viewModel.PrimaryButtonText);
        Assert.Equal("[deviceActivation.rename.button.cancel]", viewModel.CancelButtonText);
    }

    private sealed class FakeRenameService : IComputerRenameService
    {
        public ComputerRenameState State { get; set; } = new("OLD-PC", null, false);

        public Exception? StateException { get; set; }

        public bool RenameResult { get; set; } = true;

        public Exception? RenameException { get; set; }

        public Exception? RestartException { get; set; }

        public List<string> RenamedTo { get; } = [];

        public int RestartCalls { get; private set; }

        public ComputerRenameState GetState() => StateException is null ? State : throw StateException;

        public Task<bool> RenameAsync(string newName, CancellationToken cancellationToken)
        {
            if (RenameException is not null)
            {
                throw RenameException;
            }

            RenamedTo.Add(newName);
            return Task.FromResult(RenameResult);
        }

        public void Restart()
        {
            if (RestartException is not null)
            {
                throw RestartException;
            }

            RestartCalls++;
        }
    }
}
