using System.ComponentModel;
using BlazorApp.Shared.DTOs;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class DailyCloseViewModelTests
{
    [Fact]
    public void Constructor_defaults_to_today_and_builds_all_denominations()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());

        Assert.Equal(DateTime.Today, viewModel.SelectedDate);
        Assert.Equal(11, viewModel.Denominations.Count);
        Assert.Equal("$100", viewModel.Denominations.First().Label);
        Assert.Equal("5c", viewModel.Denominations.Last().Label);
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.False(viewModel.SaveAndPrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyDenominationCommand_replaces_count_and_clears_keypad_buffer()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var denomination = viewModel.Denominations.Single(item => item.Label == "$50");
        await OpenNewDailyCloseDraftAsync(viewModel);

        viewModel.OpenCashCountDialogCommand.Execute(denomination);
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        Assert.Equal(12, denomination.Count);
        Assert.Equal(600m, denomination.Subtotal);
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.Equal(600m, viewModel.NoteSubtotal);
        Assert.Equal(600m, viewModel.CountedCashAmount);
    }

    [Fact]
    public async Task OpenCashCountDialogCommand_prefills_current_count_and_first_digit_replaces_it()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var denomination = viewModel.Denominations.Single(item => item.Label == "$50");
        await OpenNewDailyCloseDraftAsync(viewModel);
        denomination.Count = 24;

        viewModel.OpenCashCountDialogCommand.Execute(denomination);

        Assert.True(viewModel.IsCashCountDialogOpen);
        Assert.Same(denomination, viewModel.SelectedCashDenomination);
        Assert.True(denomination.IsSelected);
        Assert.Equal("24", viewModel.KeypadBuffer);
        Assert.Equal(24, viewModel.CashCountDialogQuantity);
        Assert.Equal(1200m, viewModel.CashCountDialogSubtotal);
        Assert.True(viewModel.KeypadInputCommand.CanExecute("3"));

        viewModel.KeypadInputCommand.Execute("3");

        Assert.Equal("3", viewModel.KeypadBuffer);
        Assert.Equal(3, viewModel.CashCountDialogQuantity);
        Assert.Equal(150m, viewModel.CashCountDialogSubtotal);
        Assert.Equal(24, denomination.Count);
    }

    [Fact]
    public async Task CancelCashCountDialogCommand_discards_pending_input_and_preserves_count()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var denomination = viewModel.Denominations.Single(item => item.Label == "$20");
        await OpenNewDailyCloseDraftAsync(viewModel);
        denomination.Count = 2;

        viewModel.OpenCashCountDialogCommand.Execute(denomination);
        viewModel.KeypadInputCommand.Execute("9");
        viewModel.CancelCashCountDialogCommand.Execute(null);

        Assert.False(viewModel.IsCashCountDialogOpen);
        Assert.Null(viewModel.SelectedCashDenomination);
        Assert.False(denomination.IsSelected);
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.Equal(2, denomination.Count);
        Assert.Equal(40m, denomination.Subtotal);
    }

    [Fact]
    public async Task ApplyDenominationCommand_from_dialog_updates_count_totals_and_closes_dialog()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var denomination = viewModel.Denominations.Single(item => item.Label == "$10");
        await OpenNewDailyCloseDraftAsync(viewModel);

        viewModel.OpenCashCountDialogCommand.Execute(denomination);
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        Assert.Equal(12, denomination.Count);
        Assert.Equal(120m, denomination.Subtotal);
        Assert.Equal(120m, viewModel.NoteSubtotal);
        Assert.Equal(120m, viewModel.CountedCashAmount);
        Assert.False(viewModel.IsCashCountDialogOpen);
        Assert.Null(viewModel.SelectedCashDenomination);
        Assert.False(denomination.IsSelected);
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
    }

    [Fact]
    public async Task Cash_count_commands_reject_closed_dialog_wrong_target_and_more_than_nine_digits()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var selected = viewModel.Denominations.Single(item => item.Label == "$20");
        var other = viewModel.Denominations.Single(item => item.Label == "$10");

        viewModel.KeypadInputCommand.Execute("7");
        viewModel.ApplyDenominationCommand.Execute(selected);

        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.Equal(0, selected.Count);

        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.OpenCashCountDialogCommand.Execute(selected);
        foreach (var digit in "1234567890")
        {
            viewModel.KeypadInputCommand.Execute(digit.ToString());
        }

        viewModel.ApplyDenominationCommand.Execute(other);

        Assert.Equal("123456789", viewModel.KeypadBuffer);
        Assert.Equal(123456789, viewModel.CashCountDialogQuantity);
        Assert.Equal(0, selected.Count);
        Assert.Equal(0, other.Count);
        Assert.True(viewModel.IsCashCountDialogOpen);
    }

    [Fact]
    public async Task RefreshSummaryCommand_loads_report_payments_and_archives()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());

        await viewModel.RefreshSummaryCommand.ExecuteAsync(null);

        Assert.Equal(DateTime.Today, service.LastRequestedDate);
        Assert.Equal(145.35m, viewModel.ExpectedCashAmount);
        Assert.Equal(980.50m, viewModel.GrossAmount);
        Assert.Equal(955.20m, viewModel.NetAmount);
        Assert.Equal(18, viewModel.TransactionCount);
        Assert.Equal(25.30m, viewModel.RefundAmount);
        Assert.Equal(2m, viewModel.ReturnQuantity);
        Assert.Collection(
            viewModel.PaymentSummaries,
            item =>
            {
                Assert.Equal("Cash", item.Label);
                Assert.Equal(145.35m, item.NetAmount);
                Assert.Equal(6, item.TransactionCount);
            },
            item =>
            {
                Assert.Equal("Card", item.Label);
                Assert.Equal(809.85m, item.NetAmount);
                Assert.Equal(12, item.TransactionCount);
            },
            item =>
            {
                Assert.Equal("Voucher", item.Label);
                Assert.Equal(0m, item.NetAmount);
            });
        Assert.Single(viewModel.Archives);
        Assert.NotNull(viewModel.SelectedArchive);
    }

    [Fact]
    public async Task SaveAndPrintCommand_saves_prints_clears_cash_and_returns_to_pos()
    {
        var service = new FakeDailyCloseService();
        var printService = new FakeDailyClosePrintService();
        var returnedToPos = false;
        var viewModel = new DailyCloseViewModel(
            service,
            printService,
            CreateSession(),
            returnToPos: () => returnedToPos = true);
        var note = viewModel.Denominations.Single(item => item.Label == "$20");

        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.OpenCashCountDialogCommand.Execute(note);
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(DateTime.Today, service.LastSavedDate);
        Assert.NotNull(service.LastSavedCashCounts);
        Assert.Contains(service.LastSavedCashCounts!, item => item.Label == "$20" && item.Quantity == 3);
        Assert.Equal(1, printService.PrintCallCount);
        Assert.True(returnedToPos);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.False(viewModel.IsCashCountDialogOpen);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.Equal(0m, viewModel.CountedCashAmount);
        Assert.Equal("Daily close saved and sent to printer.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAndPrintCommand_print_failure_clears_cash_and_opens_saved_archive_history()
    {
        var service = new FakeDailyCloseService();
        var printService = new FakeDailyClosePrintService
        {
            PrintResult = new ReceiptPrintResult(false, "paper out")
        };
        var returnedToPos = false;
        var viewModel = new DailyCloseViewModel(
            service,
            printService,
            CreateSession(),
            returnToPos: () => returnedToPos = true);
        var note = viewModel.Denominations.Single(item => item.Label == "$50");

        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.OpenCashCountDialogCommand.Execute(note);
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.False(returnedToPos);
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.Equal(service.LastSavedArchive?.DailyCloseGuid, viewModel.SelectedArchive?.DailyCloseGuid);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.Equal("Daily close saved, but printing failed: paper out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAndPrintCommand_print_exception_uses_print_failure_flow()
    {
        var service = new FakeDailyCloseService();
        var printService = new FakeDailyClosePrintService
        {
            PrintException = new InvalidOperationException("printer offline")
        };
        var returnedToPos = false;
        var viewModel = new DailyCloseViewModel(
            service,
            printService,
            CreateSession(),
            returnToPos: () => returnedToPos = true);

        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.OpenCashCountDialogCommand.Execute(viewModel.Denominations.First());
        viewModel.KeypadInputCommand.Execute("4");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.False(returnedToPos);
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.Equal(service.LastSavedArchive?.DailyCloseGuid, viewModel.SelectedArchive?.DailyCloseGuid);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal("Daily close saved, but printing failed: printer offline", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAndPrintCommand_save_failure_preserves_cash_workspace_and_draft()
    {
        var service = new FakeDailyCloseService
        {
            SaveException = new InvalidOperationException("save failed")
        };
        var printService = new FakeDailyClosePrintService();
        var returnedToPos = false;
        var viewModel = new DailyCloseViewModel(
            service,
            printService,
            CreateSession(),
            returnToPos: () => returnedToPos = true);
        var note = viewModel.Denominations.Single(item => item.Label == "$10");

        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.OpenCashCountDialogCommand.Execute(note);
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplyDenominationCommand.Execute(viewModel.SelectedCashDenomination);

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.False(returnedToPos);
        Assert.Equal(0, printService.PrintCallCount);
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.Equal(2, note.Count);
        Assert.Equal(20m, viewModel.CountedCashAmount);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.False(viewModel.CanChangeBusinessDate);
        Assert.True(viewModel.SaveAndPrintCommand.CanExecute(null));
        Assert.Equal("save failed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadAsync_defaults_to_history_without_loading_a_cash_summary()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession())
        {
            SelectedTabIndex = 1
        };

        await viewModel.LoadAsync();

        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.Equal(0, service.LoadReportCallCount);
        Assert.Equal(1, service.GetArchivesCallCount);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
    }

    [Fact]
    public async Task Create_new_daily_close_refreshes_latest_summary_then_opens_an_unsaved_draft()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());
        var staleCount = viewModel.Denominations.Single(item => item.Label == "$100");
        staleCount.Count = 7;

        await viewModel.CreateOrResumeDailyCloseCommand.ExecuteAsync(null);

        Assert.Equal(1, service.LoadReportCallCount);
        Assert.Equal(1, service.GetArchivesCallCount);
        Assert.Equal(0, service.SaveCallCount);
        Assert.Equal(0, staleCount.Count);
        Assert.Equal(145.35m, viewModel.ExpectedCashAmount);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.False(viewModel.CanChangeBusinessDate);
        Assert.True(viewModel.SaveAndPrintCommand.CanExecute(null));
        Assert.Equal(0, viewModel.SelectedTabIndex);
    }

    [Fact]
    public async Task Create_new_daily_close_failure_stays_on_history_without_a_draft()
    {
        var service = new FakeDailyCloseService
        {
            LoadReportException = new InvalidOperationException("summary unavailable")
        };
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());
        viewModel.Denominations.First().Count = 4;

        await viewModel.CreateOrResumeDailyCloseCommand.ExecuteAsync(null);

        Assert.Equal(1, service.LoadReportCallCount);
        Assert.Equal(0, service.GetArchivesCallCount);
        Assert.Equal(0, service.SaveCallCount);
        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(0m, viewModel.ExpectedCashAmount);
        Assert.Equal("summary unavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Close_and_continue_daily_close_preserve_counts_without_refreshing_summary()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$20");
        note.Count = 3;
        viewModel.OpenCashCountDialogCommand.Execute(note);
        viewModel.KeypadInputCommand.Execute("9");
        var reportLoads = service.LoadReportCallCount;

        viewModel.CloseCashCountWorkspaceCommand.Execute(null);

        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.False(viewModel.IsCashCountDialogOpen);
        Assert.Equal(3, note.Count);
        Assert.False(viewModel.CanChangeBusinessDate);

        await viewModel.CreateOrResumeDailyCloseCommand.ExecuteAsync(null);

        Assert.Equal(reportLoads, service.LoadReportCallCount);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(3, note.Count);
    }

    [Fact]
    public async Task LoadAsync_preserves_same_session_draft_across_return_to_pos()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var coin = viewModel.Denominations.Single(item => item.Label == "50c");
        coin.Count = 5;
        viewModel.CloseCashCountWorkspaceCommand.Execute(null);
        var reportLoads = service.LoadReportCallCount;

        await viewModel.LoadAsync();

        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(5, coin.Count);
        Assert.Equal(reportLoads, service.LoadReportCallCount);

        await viewModel.CreateOrResumeDailyCloseCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(5, coin.Count);
        Assert.Equal(reportLoads, service.LoadReportCallCount);
    }

    [Fact]
    public async Task LoadAsync_resets_stale_business_date_to_today_when_no_draft()
    {
        // 模拟客户端隔夜不重启：页面实例保留着前一天的营业日期。
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession())
        {
            SelectedDate = DateTime.Today.AddDays(-1)
        };

        await viewModel.LoadAsync();

        Assert.Equal(DateTime.Today, viewModel.SelectedDate);
        Assert.Equal(DateTime.Today, service.LastArchivesRequestedDate);

        await OpenNewDailyCloseDraftAsync(viewModel);
        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(DateTime.Today, service.LastRequestedDate);
        Assert.Equal(DateTime.Today, service.LastSavedDate);
    }

    [Fact]
    public async Task LoadAsync_keeps_business_date_of_preserved_draft()
    {
        var service = new FakeDailyCloseService();
        var yesterday = DateTime.Today.AddDays(-1);
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession())
        {
            SelectedDate = yesterday
        };
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$50");
        note.Count = 2;
        viewModel.CloseCashCountWorkspaceCommand.Execute(null);

        await viewModel.LoadAsync();

        Assert.Equal(yesterday, viewModel.SelectedDate);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.Equal(2, note.Count);
    }

    [Fact]
    public async Task LoadAsync_selects_newest_archive_after_save_returned_to_pos()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(
            service,
            new FakeDailyClosePrintService(),
            CreateSession(),
            returnToPos: () => { });
        await viewModel.LoadAsync();
        var previousArchiveGuid = viewModel.SelectedArchive!.DailyCloseGuid;

        await OpenNewDailyCloseDraftAsync(viewModel);
        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);
        await viewModel.LoadAsync();

        Assert.NotEqual(previousArchiveGuid, viewModel.SelectedArchive!.DailyCloseGuid);
        Assert.Equal(service.LastSavedArchive!.DailyCloseGuid, viewModel.SelectedArchive.DailyCloseGuid);
    }

    [Fact]
    public async Task Online_and_pending_sync_changes_do_not_discard_daily_close_draft()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$10");
        note.Count = 2;

        viewModel.Session = viewModel.Session with
        {
            IsOnline = false,
            PendingSyncCount = 12
        };

        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(2, note.Count);
        Assert.False(viewModel.CanChangeBusinessDate);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("terminal")]
    [InlineData("cashier")]
    public async Task Store_terminal_or_cashier_change_discards_daily_close_draft(string identityPart)
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.Denominations.First().Count = 2;

        viewModel.Session = identityPart switch
        {
            "store" => viewModel.Session with { StoreCode = "S002" },
            "terminal" => viewModel.Session with { DeviceCode = "POS-02" },
            "cashier" => viewModel.Session with { CashierId = "C002" },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart))
        };

        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(0m, viewModel.ExpectedCashAmount);
        Assert.False(viewModel.SaveAndPrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_business_date_discards_draft_and_unlocks_date_selection()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        viewModel.Denominations.First().Count = 2;
        Assert.False(viewModel.CanChangeBusinessDate);

        viewModel.SelectedDate = DateTime.Today.AddDays(-1);

        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(0m, viewModel.ExpectedCashAmount);
    }

    [Fact]
    public async Task Discard_confirmation_cancel_preserves_then_confirm_clears_draft()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$50");
        note.Count = 2;
        viewModel.OpenCashCountDialogCommand.Execute(note);

        viewModel.RequestDiscardDailyCloseDraftCommand.Execute(null);

        Assert.False(viewModel.IsCashCountDialogOpen);
        Assert.True(viewModel.IsDiscardDailyCloseDraftConfirmationOpen);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);

        viewModel.CancelDiscardDailyCloseDraftCommand.Execute(null);

        Assert.False(viewModel.IsDiscardDailyCloseDraftConfirmationOpen);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.Equal(2, note.Count);

        viewModel.RequestDiscardDailyCloseDraftCommand.Execute(null);
        viewModel.ConfirmDiscardDailyCloseDraftCommand.Execute(null);

        Assert.False(viewModel.IsDiscardDailyCloseDraftConfirmationOpen);
        Assert.False(viewModel.HasDailyCloseDraft);
        Assert.False(viewModel.IsCashCountWorkspaceOpen);
        Assert.True(viewModel.CanChangeBusinessDate);
        Assert.All(viewModel.Denominations, item => Assert.Equal(0, item.Count));
        Assert.Equal(0m, viewModel.ExpectedCashAmount);
        Assert.False(viewModel.SaveAndPrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_authorization_denial_preserves_workspace_draft_and_counts()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(
            service,
            new FakeDailyClosePrintService(),
            CreateSession(),
            operationAuthorizationService: new DenyingOperationAuthorizationService());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$20");
        note.Count = 3;

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(0, service.SaveCallCount);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(3, note.Count);
        Assert.False(viewModel.CanChangeBusinessDate);
    }

    [Fact]
    public async Task Save_caller_cancellation_preserves_workspace_draft_and_counts()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());
        await OpenNewDailyCloseDraftAsync(viewModel);
        var note = viewModel.Denominations.Single(item => item.Label == "$20");
        note.Count = 3;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.SaveAndPrintAsync(cancellation.Token));

        Assert.Equal(1, service.SaveCallCount);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
        Assert.Equal(3, note.Count);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeBusinessDate);
    }

    [Fact]
    public async Task LoadHistoryCommand_selects_archive_and_builds_preview_cash_detail()
    {
        var printService = new FakeDailyClosePrintService();
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), printService, CreateSession());

        await viewModel.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Archives);
        Assert.NotNull(viewModel.SelectedArchive);
        Assert.Equal(5, viewModel.SelectedArchiveNoteCounts.Count);
        Assert.Equal(6, viewModel.SelectedArchiveCoinCounts.Count);
        Assert.Contains(viewModel.SelectedArchiveNoteCounts, count => count.Label == "$100" && count.Quantity == 0);
        Assert.Contains(viewModel.SelectedArchiveCoinCounts, count => count.Label == "5c" && count.Quantity == 0);
        Assert.Contains(viewModel.ArchivePreviewRows, row => row.Text == "==== DAILY CLOSE REPRINT ====");
        Assert.Equal(1, printService.BuildDocumentCallCount);
    }

    [Fact]
    public async Task ReprintSelectedArchiveCommand_prints_selected_archive_as_reprint()
    {
        var printService = new FakeDailyClosePrintService();
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), printService, CreateSession());

        await viewModel.LoadHistoryCommand.ExecuteAsync(null);
        await viewModel.ReprintSelectedArchiveCommand.ExecuteAsync(null);

        Assert.Equal(1, printService.PrintCallCount);
        Assert.Equal(ReceiptPrintReason.Reprint, printService.LastPrintReason);
        Assert.Equal(viewModel.SelectedArchive!.Archive, printService.LastPrintedArchive);
        Assert.Equal("Daily close archive sent to printer.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Save_and_reprint_record_daily_close_operation_audits()
    {
        var logger = new RecordingOperationAuditLogger();
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(
            service,
            new FakeDailyClosePrintService(),
            CreateSession(),
            operationAuditLogger: logger);

        await OpenNewDailyCloseDraftAsync(viewModel);
        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);
        await viewModel.LoadHistoryCommand.ExecuteAsync(null);
        await viewModel.ReprintSelectedArchiveCommand.ExecuteAsync(null);

        var savedArchive = Assert.IsType<DailyCloseArchive>(service.LastSavedArchive);
        Assert.Collection(
            logger.Events,
            auditEvent =>
            {
                Assert.Equal("DAILY_CLOSE_SAVE", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
                // 日结审计必须带系统应有现金、实点现金与长短款，后台按“金额变化”列即可筛查差异。
                Assert.Equal(savedArchive.DailyCloseGuid.ToString("D"), auditEvent.OrderGuid);
                Assert.Equal("Cash", auditEvent.PaymentMethod);
                Assert.Equal(145.35m, auditEvent.BeforeActual);
                Assert.Equal(savedArchive.CountedCashAmount, auditEvent.AfterActual);
                Assert.Equal(savedArchive.CashDifference, auditEvent.AmountDelta);
                Assert.NotEqual(0m, auditEvent.AmountDelta);
            },
            auditEvent =>
            {
                Assert.Equal("DAILY_CLOSE_REPRINT", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
            });
    }

    [Fact]
    public async Task Linkly_settlement_and_reprint_commands_use_persisted_record_without_resubmitting()
    {
        var settlementService = new FakeLinklySettlementService();
        var logger = new RecordingOperationAuditLogger();
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            operationAuditLogger: logger,
            linklySettlementService: settlementService,
            confirmLinklySettlementAsync: _ => Task.FromResult(true));

        await viewModel.LoadAsync();
        await viewModel.SettleAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(1, settlementService.SettleCallCount);
        Assert.Single(viewModel.Settlements);
        Assert.NotNull(viewModel.SelectedSettlement);
        Assert.Equal(LocalLinklySettlementStatus.Succeeded, viewModel.SelectedSettlement!.Status);
        Assert.Equal("Settlement saved and sent to the POS printer.", viewModel.StatusMessage);

        await viewModel.ReprintSelectedSettlementCommand.ExecuteAsync(null);

        Assert.Equal(1, settlementService.SettleCallCount);
        Assert.Equal(1, settlementService.ReprintCallCount);
        Assert.Equal("Linkly settlement receipt sent to the POS printer.", viewModel.StatusMessage);
        Assert.Collection(
            logger.Events,
            auditEvent =>
            {
                Assert.Equal("LINKLY_SETTLEMENT", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
            },
            auditEvent =>
            {
                Assert.Equal("LINKLY_SETTLEMENT_REPRINT", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
            });
    }

    [Fact]
    public async Task Linkly_settlement_blocked_by_a_pending_record_uses_a_consistent_audit_reason()
    {
        var logger = new RecordingOperationAuditLogger();
        var settlementService = new FakeLinklySettlementService(LocalLinklySettlementStatus.Pending);
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            operationAuditLogger: logger,
            linklySettlementService: settlementService,
            confirmLinklySettlementAsync: _ => Task.FromResult(true));

        await viewModel.LoadAsync();
        await viewModel.SettleAndPrintCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("LINKLY_SETTLEMENT", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("SETTLEMENT_BLOCKED", auditEvent.ReasonCode);
    }

    [Fact]
    public async Task Linkly_settlement_cancelled_at_confirmation_does_not_submit()
    {
        var settlementService = new FakeLinklySettlementService();
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            linklySettlementService: settlementService,
            confirmLinklySettlementAsync: _ => Task.FromResult(false));

        await viewModel.LoadAsync();
        await viewModel.SettleAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(0, settlementService.SettleCallCount);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Linkly_settlement_command_handles_non_caller_timeout_without_escaping_execute()
    {
        var settlementService = new FakeLinklySettlementService
        {
            SettleException = new TaskCanceledException("settlement lookup timed out")
        };
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            linklySettlementService: settlementService,
            confirmLinklySettlementAsync: _ => Task.FromResult(true));

        await viewModel.LoadAsync();
        await viewModel.SettleAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(1, settlementService.SettleCallCount);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("settlement lookup timed out", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Linkly_settlement_command_propagates_caller_cancellation()
    {
        var settlementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settlementService = new FakeLinklySettlementService
        {
            WaitForCancellation = true,
            SettlementStarted = settlementStarted
        };
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            linklySettlementService: settlementService,
            confirmLinklySettlementAsync: _ => Task.FromResult(true));

        await viewModel.LoadAsync();
        var execution = viewModel.SettleAndPrintCommand.ExecuteAsync(null);
        await settlementStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.SettleAndPrintCommand.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Linkly_settlement_command_stays_enabled_when_an_unknown_record_exists_for_safe_backend_recovery()
    {
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            linklySettlementService: new FakeLinklySettlementService(LocalLinklySettlementStatus.Unknown));

        await viewModel.LoadAsync();

        Assert.Single(viewModel.Settlements);
        Assert.True(viewModel.SettleAndPrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Linkly_local_unknown_settlement_requires_a_second_supervisor_confirmation_before_resolution()
    {
        var settlementService = new FakeLinklySettlementService(
            LocalLinklySettlementStatus.Unknown,
            LinklyConnectionMode.LocalIp.ToString());
        var logger = new RecordingOperationAuditLogger();
        var viewModel = new DailyCloseViewModel(
            new FakeDailyCloseService(),
            new FakeDailyClosePrintService(),
            CreateSession(),
            operationAuditLogger: logger,
            linklySettlementService: settlementService);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsSettlementManualResolutionVisible);
        viewModel.PrepareSettlementManualResolutionCommand.Execute(
            LocalLinklySettlementManualResolution.ConfirmedSucceeded);
        Assert.True(viewModel.IsSettlementManualResolutionConfirmationVisible);
        Assert.Equal(0, settlementService.ResolveCallCount);

        await viewModel.ConfirmSettlementManualResolutionCommand.ExecuteAsync(null);

        Assert.Equal(1, settlementService.ResolveCallCount);
        Assert.Equal(0, settlementService.SettleCallCount);
        Assert.False(viewModel.IsSettlementManualResolutionVisible);
        Assert.Equal(LocalLinklySettlementStatus.Succeeded, viewModel.SelectedSettlement!.Status);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("LINKLY_SETTLEMENT", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("ConfirmedSucceeded", auditEvent.ReasonCode);
    }

    private static async Task OpenNewDailyCloseDraftAsync(DailyCloseViewModel viewModel)
    {
        await viewModel.CreateOrResumeDailyCloseCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasDailyCloseDraft);
        Assert.True(viewModel.IsCashCountWorkspaceOpen);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private sealed class FakeDailyCloseService : IDailyCloseService
    {
        private readonly List<DailyCloseArchive> _archives = [];

        public FakeDailyCloseService()
        {
            _archives.Add(CreateArchive(140m, -1.25m, new DateTimeOffset(2026, 5, 27, 17, 35, 0, TimeSpan.Zero)));
        }

        public IReadOnlyList<CashDenomination> Denominations => DailyCloseService.AustralianDenominations;

        public DateTime LastRequestedDate { get; private set; }

        public DateTime LastArchivesRequestedDate { get; private set; }

        public DateTime LastSavedDate { get; private set; }

        public IReadOnlyList<CashDenominationCount>? LastSavedCashCounts { get; private set; }

        public DailyCloseArchive? LastSavedArchive { get; private set; }

        public int LoadReportCallCount { get; private set; }

        public int GetArchivesCallCount { get; private set; }

        public int SaveCallCount { get; private set; }

        public Exception? LoadReportException { get; init; }

        public Exception? SaveException { get; init; }

        public Task<DailyCloseReport> LoadReportAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            LoadReportCallCount++;
            LastRequestedDate = businessDate;
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadReportException is not null)
            {
                throw LoadReportException;
            }

            return Task.FromResult(CreateReport());
        }

        public Task<DailyCloseArchive> SaveAsync(
            PosSessionState session,
            DateTime businessDate,
            IReadOnlyList<CashDenominationCount> cashCounts,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveException is not null)
            {
                throw SaveException;
            }

            LastSavedDate = businessDate;
            LastSavedCashCounts = cashCounts;
            var counted = cashCounts.Sum(count => count.Amount);
            var archive = CreateArchive(counted, counted - CreateReport().SystemCashAmount, new DateTimeOffset(2026, 5, 28, 18, 0, 0, TimeSpan.Zero));
            LastSavedArchive = archive;
            _archives.Insert(0, archive);
            return Task.FromResult(archive);
        }

        public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            GetArchivesCallCount++;
            LastArchivesRequestedDate = businessDate;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DailyCloseArchive>>(_archives.ToArray());
        }

        private static DailyCloseReport CreateReport()
        {
            return new DailyCloseReport(
                DateTime.Today,
                new DateTimeOffset(DateTime.Today),
                new DateTimeOffset(DateTime.Today.AddDays(1)),
                "S001",
                "POS-01",
                "C001",
                "Alice",
                18,
                [
                    new DailyClosePaymentSummary(PaymentMethodKind.Cash, 170.65m, 25.30m, 145.35m, 6),
                    new DailyClosePaymentSummary(PaymentMethodKind.Card, 809.85m, 0m, 809.85m, 12),
                    new DailyClosePaymentSummary(PaymentMethodKind.Voucher, 0m, 0m, 0m, 0)
                ],
                25.30m,
                2m);
        }

        private static DailyCloseArchive CreateArchive(decimal countedCashAmount, decimal cashDifference, DateTimeOffset savedAt)
        {
            return new DailyCloseArchive(
                Guid.NewGuid(),
                CreateReport(),
                DailyCloseService.AustralianDenominations
                    .Select(denomination => new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0))
                    .ToArray(),
                savedAt,
                countedCashAmount,
                0m,
                countedCashAmount,
                cashDifference);
        }
    }

    private sealed class FakeDailyClosePrintService : IDailyClosePrintService
    {
        public int BuildDocumentCallCount { get; private set; }

        public int PrintCallCount { get; private set; }

        public DailyCloseArchive? LastPrintedArchive { get; private set; }

        public ReceiptPrintReason? LastPrintReason { get; private set; }

        public ReceiptPrintResult PrintResult { get; init; } = new(true, "printed");

        public Exception? PrintException { get; init; }

        public Task<ReceiptPrintDocument> BuildDocumentAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            BuildDocumentCallCount++;
            return Task.FromResult(DailyCloseTextFormatter.Build(archive, ReceiptPrinterSettings.Default, reason));
        }

        public Task<ReceiptPrintResult> PrintAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            PrintCallCount++;
            LastPrintedArchive = archive;
            LastPrintReason = reason;
            return PrintException is not null
                ? Task.FromException<ReceiptPrintResult>(PrintException)
                : Task.FromResult(PrintResult);
        }
    }

    private sealed class FakeLinklySettlementService : ILinklySettlementService
    {
        private readonly List<LocalLinklySettlementRecord> _settlements = [];

        public FakeLinklySettlementService(
            LocalLinklySettlementStatus? initialStatus = null,
            string connectionMode = "CloudBackendAsync")
        {
            if (initialStatus is { } status)
            {
                _settlements.Add(new LocalLinklySettlementRecord(
                    Guid.NewGuid(),
                    "S001",
                    "POS-01",
                    DateTime.Today,
                    connectionMode,
                    "Production",
                    "unresolved-settlement-001",
                    status,
                    ResponseCode: null,
                    ResponseText: null,
                    SettlementData: null,
                    ReceiptTexts: [],
                    DateTimeOffset.UtcNow,
                    CompletedAt: null,
                    FirstPrintedAt: null,
                    LastPrintedAt: null,
                    PrintCount: 0,
                    LastPrintError: null));
            }
        }

        public int SettleCallCount { get; private set; }

        public int ReprintCallCount { get; private set; }

        public int ResolveCallCount { get; private set; }

        public Exception? SettleException { get; init; }

        public bool WaitForCancellation { get; init; }

        public TaskCompletionSource<bool>? SettlementStarted { get; init; }

        public async Task<LinklySettlementExecutionResult> SettleAndPrintAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            SettleCallCount++;
            SettlementStarted?.TrySetResult(true);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (SettleException is not null)
            {
                throw SettleException;
            }

            if (_settlements.FirstOrDefault(item => item.Status == LocalLinklySettlementStatus.Pending) is { } pending)
            {
                return new LinklySettlementExecutionResult(pending, PrintResult: null);
            }

            var settlement = new LocalLinklySettlementRecord(
                Guid.NewGuid(),
                session.StoreCode,
                session.DeviceCode,
                businessDate,
                "LocalIp",
                "Production",
                "settlement-001",
                LocalLinklySettlementStatus.Succeeded,
                "00",
                "Approved",
                "Totals: 3",
                ["SETTLEMENT RECEIPT"],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                1,
                LastPrintError: null);
            _settlements.Insert(0, settlement);
            return new LinklySettlementExecutionResult(settlement, new ReceiptPrintResult(true, "printed"));
        }

        public Task<ReceiptPrintResult> ReprintAsync(
            LocalLinklySettlementRecord settlement,
            CancellationToken cancellationToken = default)
        {
            ReprintCallCount++;
            return Task.FromResult(new ReceiptPrintResult(true, "printed"));
        }

        public Task<IReadOnlyList<LocalLinklySettlementRecord>> GetHistoryAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalLinklySettlementRecord>>(
                _settlements.Where(item => item.BusinessDate == businessDate.Date).ToArray());
        }

        public Task<LinklySettlementManualResolutionResult> ResolveUncertainAsync(
            PosSessionState session,
            LocalLinklySettlementRecord settlement,
            LocalLinklySettlementManualResolution resolution,
            CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            var index = _settlements.FindIndex(item => item.SettlementGuid == settlement.SettlementGuid);
            var current = _settlements[index];
            var updated = current with
            {
                Status = resolution == LocalLinklySettlementManualResolution.ConfirmedSucceeded
                    ? LocalLinklySettlementStatus.Succeeded
                    : LocalLinklySettlementStatus.Failed,
                ProviderSubmissionState = resolution == LocalLinklySettlementManualResolution.ConfirmedNotSubmitted
                    ? ProviderSubmissionState.NotSubmitted
                    : ProviderSubmissionState.Submitted,
                CompletedAt = DateTimeOffset.UtcNow
            };
            _settlements[index] = updated;
            return Task.FromResult(new LinklySettlementManualResolutionResult(true, updated, "resolved"));
        }
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private sealed class DenyingOperationAuthorizationService : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public event EventHandler? StatusChanged { add { } remove { } }

        public string ScannerPageId => "daily-close-test";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OperationAuthorizationScope?>(null);
        }

        public bool ProcessScannerBarcode(string barcode) => false;

        public void Cancel()
        {
        }

        public void RevokeAll()
        {
        }
    }
}
