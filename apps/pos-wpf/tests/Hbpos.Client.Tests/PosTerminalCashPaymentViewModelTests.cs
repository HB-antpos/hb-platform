using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Hbpos.Contracts.Promotions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class PosTerminalCashPaymentViewModelTests
{

    [Fact]
    public async Task Approved_card_persistence_log_failure_preserves_visible_recovery_and_does_not_escape()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PERSIST-LOG", "Persistence log failure", "930PERSISTLOG", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Card, 10m, "APPROVED"),
            ThrowOnComplete = new CardPaymentPersistenceUnknownException(Guid.NewGuid(), "Recover approved order",
                new PersistenceRecoveryLogProbeException())
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, openCardRecoveryCenter: () => { });
        void FailAuditLog(string line)
        {
            if (line.Contains(nameof(PersistenceRecoveryLogProbeException), StringComparison.Ordinal))
                throw new IOException("audit sink unavailable");
        }
        ConsoleLog.LineWritten += FailAuditLog;
        try
        {
            Assert.Null(await Record.ExceptionAsync(() => viewModel.SelectCardCommand.ExecuteAsync(null)));
            Assert.True(viewModel.IsCardPaymentRecoveryRequired);
            Assert.True(viewModel.IsPaymentInteractionLocked);
            Assert.True(viewModel.OpenCardRecoveryCenterCommand.CanExecute(null));
            Assert.Single(viewModel.PaymentTenders);
        }
        finally
        {
            ConsoleLog.LineWritten -= FailAuditLog;
        }
    }

    private sealed class PersistenceRecoveryLogProbeException : IOException;

    [Fact]
    public async Task Pos_terminal_card_recovery_status_opens_center_and_displays_queue_count()
    {
        var opened = false;
        var localization = new LocalizationService();
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: localization,
            onOpenCardRecoveryCenterAsync: () =>
            {
                opened = true;
                return Task.CompletedTask;
            })
        {
            CardRecoveryOpenCount = 2
        };

        await viewModel.OpenCardRecoveryCenterCommand.ExecuteAsync(null);

        Assert.True(opened);
        Assert.True(viewModel.HasOpenCardRecoveryAttempts);
        Assert.Contains("2", viewModel.CardRecoveryOpenText, StringComparison.Ordinal);
        Assert.Equal("Card transaction recovery", viewModel.CardRecoveryCenterText);
        var automationName = viewModel.CardRecoveryCenterAutomationName;
        Assert.Contains("Card transaction recovery", automationName, StringComparison.Ordinal);
        Assert.Contains(viewModel.CardRecoveryOpenText, automationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pos_terminal_card_recovery_center_stays_available_without_open_attempts()
    {
        var opened = false;
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: localization,
            onOpenCardRecoveryCenterAsync: () =>
            {
                opened = true;
                return Task.CompletedTask;
            })
        {
            CardRecoveryOpenCount = 0
        };

        await viewModel.OpenCardRecoveryCenterCommand.ExecuteAsync(null);

        Assert.True(opened);
        Assert.False(viewModel.HasOpenCardRecoveryAttempts);
        var title = viewModel.CardRecoveryCenterText;
        var automationName = viewModel.CardRecoveryCenterAutomationName;
        Assert.Equal("Card transaction recovery", title);
        Assert.Equal(title, automationName);
        Assert.DoesNotContain("0", title, StringComparison.Ordinal);
        Assert.DoesNotContain("0", automationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pos_terminal_card_recovery_center_text_and_automation_name_refresh_with_language()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: localization)
        {
            CardRecoveryOpenCount = 2
        };
        var propertyChanges = new List<string?>();
        viewModel.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);

        try
        {
            var englishTitle = viewModel.CardRecoveryCenterText;
            var englishAutomationName = viewModel.CardRecoveryCenterAutomationName;
            Assert.Equal("Card transaction recovery", englishTitle);
            Assert.Contains("2 card transactions need attention", englishAutomationName, StringComparison.Ordinal);

            propertyChanges.Clear();
            viewModel.CardRecoveryOpenCount = 0;
            Assert.Contains(nameof(viewModel.HasOpenCardRecoveryAttempts), propertyChanges);
            Assert.Contains(nameof(viewModel.CardRecoveryOpenText), propertyChanges);
            Assert.Contains(nameof(viewModel.CardRecoveryCenterAutomationName), propertyChanges);

            propertyChanges.Clear();
            viewModel.CardRecoveryOpenCount = 2;
            Assert.Contains(nameof(viewModel.HasOpenCardRecoveryAttempts), propertyChanges);
            Assert.Contains(nameof(viewModel.CardRecoveryOpenText), propertyChanges);
            Assert.Contains(nameof(viewModel.CardRecoveryCenterAutomationName), propertyChanges);

            propertyChanges.Clear();
            await localization.SetCultureAsync(LocalizationService.ChineseCultureName);

            var chineseTitle = viewModel.CardRecoveryCenterText;
            var chineseAutomationName = viewModel.CardRecoveryCenterAutomationName;
            Assert.Equal("卡交易异常中心", chineseTitle);
            Assert.Contains(chineseTitle, chineseAutomationName, StringComparison.Ordinal);
            Assert.Contains("2 笔卡交易待处理", chineseAutomationName, StringComparison.Ordinal);
            Assert.Contains("CardRecoveryCenterText", propertyChanges);
            Assert.Contains("CardRecoveryOpenText", propertyChanges);
            Assert.Contains("CardRecoveryCenterAutomationName", propertyChanges);
        }
        finally
        {
            viewModel.Dispose();
            await localization.SetCultureAsync(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public async Task Pos_terminal_open_history_command_invokes_navigation_callback()
    {
        var opened = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onOpenHistoryAsync: () =>
            {
                opened = true;
                return Task.CompletedTask;
            });

        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.True(opened);
    }

    [Fact]
    public async Task Pos_terminal_page_navigation_commands_invoke_callbacks()
    {
        var settingsOpened = false;
        var customerDisplayOpened = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onOpenSettingsAsync: () =>
            {
                settingsOpened = true;
                return Task.CompletedTask;
            },
            onOpenCustomerDisplay: () => customerDisplayOpened = true);

        await viewModel.OpenSettingsCommand.ExecuteAsync(null);
        viewModel.OpenCustomerDisplayCommand.Execute(null);

        Assert.True(settingsOpened);
        Assert.True(customerDisplayOpened);
    }

    [Fact]
    public async Task Pos_terminal_open_cash_drawer_command_invokes_callback_and_shows_status()
    {
        var opened = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: new LocalizationService(),
            onOpenCashDrawerAsync: () =>
            {
                opened = true;
                return Task.FromResult(new ReceiptPrintResult(true, "Cash drawer opened."));
            });

        await viewModel.OpenCashDrawerCommand.ExecuteAsync(null);

        Assert.True(opened);
        Assert.Equal("Cash drawer opened.", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Success, viewModel.StatusFeedbackKind);
    }

    [Fact]
    public async Task Pos_terminal_open_cash_drawer_command_reports_failure_and_feedback()
    {
        var feedback = new FakeUserFeedbackService();
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback,
            onOpenCashDrawerAsync: () => Task.FromResult(new ReceiptPrintResult(false, "drawer offline")));

        await viewModel.OpenCashDrawerCommand.ExecuteAsync(null);

        Assert.Equal("drawer offline", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Contains(UserFeedbackCue.OperationError, feedback.Cues);
    }

    [Fact]
    public async Task Pos_terminal_exit_application_command_invokes_callback()
    {
        var exited = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onExitApplicationAsync: () =>
            {
                exited = true;
                return Task.CompletedTask;
            });

        await viewModel.ExitApplicationCommand.ExecuteAsync(null);

        Assert.True(exited);
    }

    [Fact]
    public async Task Pos_terminal_lock_cashier_command_invokes_callback()
    {
        var locked = false;
        var localization = new LocalizationService();
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: localization,
            onLockCashierAsync: () =>
            {
                locked = true;
                return Task.CompletedTask;
            });

        try
        {
            await viewModel.LockCashierCommand.ExecuteAsync(null);

            Assert.True(locked);
            Assert.Equal("Log Out", viewModel.LockCashierText);

            await localization.SetCultureAsync("zh-CN");
            Assert.Equal("登出", viewModel.LockCashierText);
        }
        finally
        {
            await localization.SetCultureAsync(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public async Task Pos_terminal_scans_exact_barcode_into_cart()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-101", "Sparkling Water", "930101", PriceSourceKind.StoreRetailPrice, 2.5m, itemNumber: "ITEM-101", productImage: "https://images.example/sparkling-water.jpg")]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930101";
        await ExecuteManualScanAsync(viewModel);

        Assert.Empty(viewModel.ScanText);
        Assert.Equal(2.5m, viewModel.ActualAmount);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Sparkling Water", line.DisplayName);
        Assert.Equal("ITEM-101", line.ItemNumber);
        Assert.Equal("https://images.example/sparkling-water.jpg", line.ProductImage);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal("StoreRetailPrice", line.PriceSourceLabel);
    }

    [Fact]
    public async Task Pos_terminal_scan_command_maps_workflow_result_to_ui_state()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var matchedItem = CreateItem("SKU-101A", "Workflow Match", "930101A", PriceSourceKind.StoreRetailPrice, 2.2m);
        index.ReplaceAll([matchedItem]);
        var workflow = new FakePosTerminalWorkflowService
        {
            ProcessScanResult = new PosTerminalWorkflowResult
            {
                StatusKey = "pos.status.multipleMatches",
                StatusArgs = [1],
                Matches = [matchedItem],
                SelectedItem = matchedItem,
                MatchesPopupOpen = true,
                TouchKeyboardOpen = false
            }
        };
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            workflowService: workflow)
        {
            ScanText = "930101A",
            IsTouchKeyboardOpen = true
        };

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal("930101A", workflow.LastProcessScanText);
        Assert.True(workflow.LastProcessScanPreferExactLookup is false);
        var match = Assert.Single(viewModel.Matches);
        Assert.Equal("Workflow Match", match.DisplayName);
        Assert.Same(match, viewModel.SelectedItem);
        Assert.True(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
        Assert.Equal("pos.status.multipleMatches", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Catalog_index_update_refreshes_only_an_open_nonempty_search()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-CATALOG-1", "Catalog Tea", "930CAT1", PriceSourceKind.StoreRetailPrice, 3m),
            CreateItem("SKU-CATALOG-2", "Catalog Coffee", "930CAT2", PriceSourceKind.StoreRetailPrice, 4m)
        ]);
        var workflow = new FakePosTerminalWorkflowService();
        using var viewModel = new PosTerminalViewModel(
            index,
            new PosCartService(),
            Session,
            onOpenPayment: null,
            workflowService: workflow)
        {
            ScanText = "tea",
            IsMatchesPopupOpen = true
        };

        workflow.RaiseCatalogIndexUpdated();

        await WaitUntilAsync(() => viewModel.Matches.Count == 1);
        Assert.Equal("Catalog Tea", Assert.Single(viewModel.Matches).DisplayName);

        viewModel.IsMatchesPopupOpen = false;
        index.ReplaceAll([CreateItem("SKU-CATALOG-3", "Catalog Matcha", "930CAT3", PriceSourceKind.StoreRetailPrice, 5m)]);
        workflow.RaiseCatalogIndexUpdated();

        Assert.Equal("Catalog Tea", Assert.Single(viewModel.Matches).DisplayName);
    }

    [Fact]
    public async Task Pos_terminal_open_empty_match_panel_shows_only_first_eight_items()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(1, 10).Select(index =>
            CreateItem($"SKU-EMPTY-{index}", $"Catalog {index:D2}", $"930EMPTY{index:D2}", PriceSourceKind.StoreRetailPrice, index)));
        using var viewModel = new PosTerminalViewModel(index, new PosCartService(), Session, onOpenPayment: null);

        viewModel.IsMatchesPopupOpen = true;

        await WaitUntilAsync(() => viewModel.Matches.Count == 8);
        Assert.Equal(8, viewModel.Matches.Count);
    }

    [Fact]
    public async Task Pos_terminal_reopening_empty_match_panel_replaces_stale_matches_with_first_eight_items()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(1, 10).Select(index =>
            CreateItem($"SKU-REOPEN-{index}", $"Reopen Catalog {index:D2}", $"930REOPEN{index:D2}", PriceSourceKind.StoreRetailPrice, index)));
        var staleItem = CreateItem("SKU-STALE", "Stale Match", "930STALE", PriceSourceKind.StoreRetailPrice, 1m);
        using var viewModel = new PosTerminalViewModel(index, new PosCartService(), Session, onOpenPayment: null);
        viewModel.LoadMatches([staleItem]);
        viewModel.ScanText = "stale";

        viewModel.ClearSearchCommand.Execute(null);
        viewModel.IsMatchesPopupOpen = true;

        await WaitUntilAsync(() => viewModel.Matches.Count == 8 && viewModel.Matches.All(item => item.DisplayName != staleItem.DisplayName));
        Assert.Equal(8, viewModel.Matches.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Pos_terminal_catalog_match_refresh_keeps_ui_enqueue_under_50ms_for_100k_items()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(0, 100_000).Select(index =>
            CreateItem($"SKU-PERF-{index}", $"Catalog Item {index:D6}", $"930PERF{index:D6}", PriceSourceKind.StoreRetailPrice, 1m)));
        using var viewModel = new PosTerminalViewModel(index, new PosCartService(), Session, onOpenPayment: null)
        {
            IsMatchesPopupOpen = true
        };

        var stopwatch = Stopwatch.StartNew();
        viewModel.ScanText = "Catalog Item 099999";
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50), $"UI refresh enqueue took {stopwatch.ElapsedMilliseconds}ms.");
        await WaitUntilAsync(() => viewModel.Matches.Count == 1 && viewModel.Matches[0].DisplayName == "Catalog Item 099999");
    }

    // 以下两条原先用 Task.Delay(10) 打点、断言最大间隔 < 100ms 的"心跳"：测试里没有 Dispatcher，
    // 打点测到的是 xUnit 同步上下文与线程池的调度争用（CI 并行时可达 160ms 误报），而搜索若退回同步执行，
    // 打点要等搜索结束才开始，反而测不出回归。改为闸门阻塞搜索，断言调用方返回时搜索仍未完成。
    [Fact]
    public async Task Pos_terminal_catalog_match_refresh_does_not_block_ui_heartbeat_when_search_is_slow()
    {
        var item = CreateItem("SKU-SLOW", "Slow Catalog", "930SLOW", PriceSourceKind.StoreRetailPrice, 1m);
        var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSearch = new ManualResetEventSlim(false);
        var searchFinished = 0;
        using var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            searchCatalogMatches: (_, _, _) =>
            {
                searchStarted.TrySetResult();
                // 超时只为同步回归时不挂死：那时 setter 会等满超时才返回，下面的断言必然失败。
                releaseSearch.Wait(AsyncTestWaitSupport.DefaultTimeout);
                Volatile.Write(ref searchFinished, 1);
                return [item];
            })
        {
            IsMatchesPopupOpen = true
        };

        try
        {
            viewModel.ScanText = "slow";
            Assert.Equal(0, Volatile.Read(ref searchFinished));

            await searchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.Equal(0, Volatile.Read(ref searchFinished));
            Assert.DoesNotContain(viewModel.Matches, match => match.DisplayName == item.DisplayName);
        }
        finally
        {
            releaseSearch.Set();
        }

        await WaitUntilAsync(() => viewModel.Matches.Count == 1 && viewModel.Matches[0].DisplayName == item.DisplayName);
    }

    [Fact]
    public async Task Pos_terminal_manual_search_keeps_heartbeat_running_and_never_adds_a_stale_result()
    {
        var index = new LocalSellableItemIndex();
        var secondItem = CreateItem("SKU-SECOND", "Second Catalog", "930SECOND", PriceSourceKind.StoreRetailPrice, 1m);
        index.ReplaceAll(Enumerable.Range(0, 99_999)
            .Select(itemIndex => CreateItem(
                $"SKU-FIRST-{itemIndex}",
                $"First Catalog {itemIndex:D6}",
                $"930FIRST{itemIndex:D6}",
                PriceSourceKind.StoreRetailPrice,
                1m))
            .Append(secondItem));
        var firstSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstSearch = new ManualResetEventSlim(false);
        var firstSearchResumed = 0;
        index.SearchProgressForTests = (query, progress) =>
        {
            if (query == "first" && progress == 0)
            {
                firstSearchStarted.TrySetResult();
                releaseFirstSearch.Wait(AsyncTestWaitSupport.DefaultTimeout);
                Volatile.Write(ref firstSearchResumed, 1);
            }
        };
        using var viewModel = new PosTerminalViewModel(index, new PosCartService(), Session, onOpenPayment: null)
        {
            ScanText = "first"
        };

        try
        {
            viewModel.NumberInputCommand.Execute("Enter");
            Assert.Equal(0, Volatile.Read(ref firstSearchResumed));

            await firstSearchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.Equal(0, Volatile.Read(ref firstSearchResumed));

            // 第一次搜索仍被闸门挡住时发起第二次，再放行第一次：迟到的旧结果不得进购物车。
            viewModel.ScanText = "second";
            viewModel.NumberInputCommand.Execute("Enter");
            releaseFirstSearch.Set();
            await WaitUntilAsync(() => viewModel.CartLines.Count == 1);

            Assert.Equal(secondItem.ProductCode, Assert.Single(viewModel.CartLines).ProductCode);
        }
        finally
        {
            releaseFirstSearch.Set();
            index.SearchProgressForTests = null;
        }
    }

    [Fact]
    public async Task Pos_terminal_default_catalog_search_cancels_old_real_index_work_before_applying_new_result()
    {
        var index = new LocalSellableItemIndex();
        var newItem = CreateItem("SKU-NEW", "New Result", "930NEW", PriceSourceKind.StoreRetailPrice, 1m);
        index.ReplaceAll(
            Enumerable.Range(0, 99_999)
                .Select(index => CreateItem($"SKU-OLD-{index}", $"Old Result {index:D6}", $"930OLD{index:D6}", PriceSourceKind.StoreRetailPrice, 1m))
                .Append(newItem));
        var oldSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueOldSearch = new ManualResetEventSlim(false);
        var oldSearchProgress = -1;
        index.SearchProgressForTests = (query, progress) =>
        {
            if (!string.Equals(query, "old", StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref oldSearchProgress, progress);
            if (progress == 0)
            {
                oldSearchStarted.TrySetResult();
                // 等新输入确实取消旧查询后再继续，避免依赖 CI 线程能在固定延时内获调度。
                continueOldSearch.Wait();
            }
        };
        using var viewModel = new PosTerminalViewModel(
            index,
            new PosCartService(),
            Session,
            onOpenPayment: null)
        {
            IsMatchesPopupOpen = true
        };

        try
        {
            viewModel.ScanText = "old";
            await oldSearchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var oldCancellation = Assert.IsType<CancellationTokenSource>(typeof(PosTerminalViewModel)
                .GetField("_matchesRefreshCts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(viewModel));
            viewModel.ScanText = "new result";
            await WaitUntilAsync(() => oldCancellation.IsCancellationRequested);
        }
        finally
        {
            // 断言失败也释放真实索引线程，防止污染同一分组的后续测试。
            continueOldSearch.Set();
        }
        await WaitUntilAsync(() => viewModel.Matches.Count == 1 && viewModel.Matches[0].DisplayName == newItem.DisplayName);

        Assert.InRange(Volatile.Read(ref oldSearchProgress), 0, 255);
        Assert.Equal(newItem.DisplayName, Assert.Single(viewModel.Matches).DisplayName);
        index.SearchProgressForTests = null;
    }

    [Fact]
    public async Task Pos_terminal_dispose_cancels_inflight_catalog_match_refresh()
    {
        var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            searchCatalogMatches: (_, _, cancellationToken) =>
            {
                searchStarted.TrySetResult();
                cancellationToken.WaitHandle.WaitOne();
                cancellationObserved.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            })
        {
            IsMatchesPopupOpen = true
        };

        viewModel.ScanText = "dispose";
        await searchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.Dispose();

        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Empty(viewModel.Matches);
    }

    [Fact]
    public void Pos_terminal_dispose_is_idempotent_and_stops_culture_updates()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            localization: localization);
        var changeCount = 0;
        viewModel.PropertyChanged += (_, _) => changeCount++;

        try
        {
            viewModel.Dispose();
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal(0, changeCount);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public void Pos_terminal_open_payment_obeys_workflow_guard_result()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-101B", "Guarded Match", "930101B", PriceSourceKind.StoreRetailPrice, 3.3m));
        var deniedWorkflow = new FakePosTerminalWorkflowService
        {
            GuardPaymentResult = new PosTerminalWorkflowResult
            {
                StatusKey = "cart.status.zeroPriceItem",
                PaymentAllowed = false
            }
        };
        var deniedViewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            workflowService: deniedWorkflow);
        deniedViewModel.RefreshCart();

        var deniedRaised = false;
        deniedViewModel.PaymentRequested += (_, _) => deniedRaised = true;

        deniedViewModel.OpenPaymentCommand.Execute(null);

        Assert.False(deniedRaised);
        Assert.Equal("cart.status.zeroPriceItem", deniedViewModel.StatusMessage);

        var allowedWorkflow = new FakePosTerminalWorkflowService
        {
            GuardPaymentResult = new PosTerminalWorkflowResult { PaymentAllowed = true }
        };
        var allowedViewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            workflowService: allowedWorkflow);
        allowedViewModel.RefreshCart();

        var allowedRaised = false;
        allowedViewModel.PaymentRequested += (_, _) => allowedRaised = true;

        allowedViewModel.OpenPaymentCommand.Execute(null);

        Assert.True(allowedRaised);
    }

    [Fact]
    public async Task Pos_terminal_open_payment_waits_for_pending_promotion_evaluation()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-PAY", "Promo Payment Tea", "930PROMO-PAY", PriceSourceKind.StoreRetailPrice, 10m)]);
        var evaluationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionService = new ScriptedPromotionEvaluationService();
        promotionService.EnqueueSync(lines => []);
        promotionService.EnqueueAsync(async lines =>
        {
            await evaluationGate.Task;
            return [new PromotionLineDiscount(Assert.Single(lines), 5m)];
        });

        var openedPayment = false;
        var discountWhenOpenedPayment = -1m;
        var actualAmountWhenOpenedPayment = -1m;
        PosTerminalViewModel? viewModel = null;
        viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () =>
            {
                openedPayment = true;
                discountWhenOpenedPayment = Assert.Single(viewModel!.CartLines).DiscountAmount;
                actualAmountWhenOpenedPayment = viewModel.ActualAmount;
            },
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO-PAY";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 1);

        viewModel.ScanText = "930PROMO-PAY";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 2);

        viewModel.OpenPaymentCommand.Execute(null);

        Assert.False(openedPayment);

        evaluationGate.SetResult();

        await WaitUntilAsync(() => openedPayment);
        Assert.Equal(5m, discountWhenOpenedPayment);
        Assert.Equal(15m, actualAmountWhenOpenedPayment);
    }

    [Fact]
    public async Task Pos_terminal_hold_order_waits_for_pending_promotion_evaluation()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-HOLD", "Promo Hold Tea", "930PROMO-HOLD", PriceSourceKind.StoreRetailPrice, 10m)]);
        var evaluationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionService = new ScriptedPromotionEvaluationService();
        promotionService.EnqueueSync(lines => []);
        promotionService.EnqueueAsync(async lines =>
        {
            await evaluationGate.Task;
            return [new PromotionLineDiscount(Assert.Single(lines), 5m)];
        });

        var heldOrder = false;
        var discountWhenHeldOrder = -1m;
        var actualAmountWhenHeldOrder = -1m;
        PosTerminalViewModel? viewModel = null;
        viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            onHoldOrderAsync: () =>
            {
                heldOrder = true;
                discountWhenHeldOrder = Assert.Single(viewModel!.CartLines).DiscountAmount;
                actualAmountWhenHeldOrder = viewModel.ActualAmount;
                return Task.CompletedTask;
            },
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO-HOLD";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 1);

        viewModel.ScanText = "930PROMO-HOLD";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 2);

        var holdTask = viewModel.HoldOrderCommand.ExecuteAsync(null);

        Assert.False(heldOrder);

        evaluationGate.SetResult();

        await holdTask;
        Assert.True(heldOrder);
        Assert.Equal(5m, discountWhenHeldOrder);
        Assert.Equal(15m, actualAmountWhenHeldOrder);
    }

    [Fact]
    public async Task Pos_terminal_selects_the_latest_added_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-121", "First Item", "930121", PriceSourceKind.StoreRetailPrice, 1.5m),
            CreateItem("SKU-122", "Second Item", "930122", PriceSourceKind.StoreRetailPrice, 2.5m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930121";
        await ExecuteManualScanAsync(viewModel);
        var firstLine = Assert.Single(viewModel.CartLines);

        viewModel.ScanText = "930122";
        await ExecuteManualScanAsync(viewModel);

        var secondLine = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930122");
        Assert.NotSame(firstLine, secondLine);
        Assert.Same(secondLine, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_reveals_cart_line_added_outside_pos_view_model()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-131", "Special Item", "930131", PriceSourceKind.StoreRetailPrice, 3.5m));
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null);

        viewModel.RevealCartLine(line);

        Assert.Contains(line, viewModel.CartLines);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_keeps_touch_keyboard_and_main_keypad_buffers_separate()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.NumberInputCommand.Execute("A");
        viewModel.NumberInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("9");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");

        Assert.Equal("A1", viewModel.ScanText);
        Assert.Equal("9.5", viewModel.KeypadBuffer);

        viewModel.KeypadInputCommand.Execute("Clear");

        Assert.Equal("A1", viewModel.ScanText);
        Assert.Empty(viewModel.KeypadBuffer);
    }

    [Fact]
    public void Pos_terminal_open_item_command_requires_positive_keypad_amount()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        Assert.False(viewModel.AddOpenItemCommand.CanExecute(null));

        viewModel.KeypadInputCommand.Execute("0");

        Assert.False(viewModel.AddOpenItemCommand.CanExecute(null));

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");

        Assert.True(viewModel.AddOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void Pos_terminal_open_item_adds_openitem_with_keypad_price_without_merging()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");
        viewModel.AddOpenItemCommand.Execute(null);
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.All(viewModel.CartLines, line =>
        {
            Assert.Equal(CartLineKind.OpenItem, line.Kind);
            Assert.Equal("OPENITEM", line.LookupCodeNormalized);
            Assert.Equal(1m, line.Quantity);
        });
        Assert.Equal(12.34m, viewModel.CartLines[0].UnitPrice);
        Assert.Equal(5m, viewModel.CartLines[1].UnitPrice);
        Assert.Same(viewModel.CartLines[1], viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_open_item_does_not_add_when_openitem_lookup_is_missing_or_duplicated()
    {
        var cart = new PosCartService();
        var missingIndex = new LocalSellableItemIndex();
        var missingViewModel = new PosTerminalViewModel(
            missingIndex,
            cart,
            Session,
            onOpenPayment: null);
        missingViewModel.KeypadInputCommand.Execute("1");

        missingViewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(cart.Lines);
        Assert.Equal("pos.status.noLocalMatch", missingViewModel.StatusMessage);

        var duplicateIndex = new LocalSellableItemIndex();
        duplicateIndex.ReplaceAll([
            CreateItem("OPEN-SKU-1", "Open Item 1", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m),
            CreateItem("OPEN-SKU-2", "Open Item 2", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)
        ]);
        var duplicateViewModel = new PosTerminalViewModel(
            duplicateIndex,
            cart,
            Session,
            onOpenPayment: null);
        duplicateViewModel.KeypadInputCommand.Execute("1");

        duplicateViewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(cart.Lines);
        Assert.Equal("pos.status.multipleMatches", duplicateViewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_touch_keyboard_enter_closes_keyboard_after_search()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-109", "Match Tea", "930109", PriceSourceKind.StoreRetailPrice, 4.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsTouchKeyboardOpen = true,
            ScanText = "930109",
        };

        viewModel.NumberInputCommand.Execute("Enter");
        await WaitUntilAsync(() => !viewModel.IsTouchKeyboardOpen && string.IsNullOrEmpty(viewModel.ScanText) && viewModel.CartLines.Count == 1);

        Assert.False(viewModel.IsTouchKeyboardOpen);
        Assert.Empty(viewModel.ScanText);
        Assert.Single(viewModel.CartLines);
    }

    [Fact]
    public void Pos_terminal_touch_keyboard_typing_keeps_keyboard_open()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsTouchKeyboardOpen = true,
        };

        viewModel.NumberInputCommand.Execute("A");
        viewModel.NumberInputCommand.Execute("1");

        Assert.True(viewModel.IsTouchKeyboardOpen);
        Assert.Equal("A1", viewModel.ScanText);
    }

    [Fact]
    public void Pos_terminal_adds_item_from_raw_scanner_event()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        index.ReplaceAll([CreateItem("SKU-110", "Scanner Apple", "930110", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930110");

        Assert.Empty(viewModel.ScanText);
        Assert.Equal(1.8m, viewModel.ActualAmount);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Scanner Apple", line.DisplayName);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.Equal("Scan 930110: Added Scanner Apple", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_uses_exact_lookup_without_keyword_search()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll([CreateItem("SKU-113", "Scanner Keyword Match", "930113", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("Keyword");

        Assert.Empty(viewModel.CartLines);
        Assert.Empty(viewModel.Matches);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.Equal("Keyword", viewModel.ScanText);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_duplicate_exact_lookup_requires_selection()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        index.ReplaceAll(
        [
            CreateItem("SKU-114", "Scanner Apple Small", "930114", PriceSourceKind.StoreRetailPrice, 1.8m),
            CreateItem("SKU-115", "Scanner Apple Large", "930114", PriceSourceKind.StoreRetailPrice, 2.8m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930114");

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(2, viewModel.Matches.Count);
        Assert.True(viewModel.IsMatchesPopupOpen);
        Assert.Equal("930114", viewModel.ScanText);
        Assert.Equal("Scan 930114: Found 2 items. Select one to add.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_scan_success_plays_success_feedback_and_marks_success_status()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-114A", "Feedback Apple", "930114A", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback)
        {
            ScanText = "930114A"
        };

        await ExecuteManualScanAsync(viewModel);

        Assert.Equal(StatusFeedbackKind.Success, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanAdded], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_keyboard_fallback_scanner_shows_barcode_and_no_match_result()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localization = new LocalizationService();
        var feedback = new FakeUserFeedbackService();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            userFeedbackService: feedback);

        viewModel.ProcessScannerBarcode("930999", "keyboard-focus-fallback", "keyboard-fallback");

        Assert.Empty(viewModel.CartLines);
        Assert.Equal("930999", viewModel.ScanText);
        Assert.Equal("Scan 930999: No local item found. Checkout can continue offline.", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanNoMatch], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_multiple_match_plays_warning_feedback_once()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll(
        [
            CreateItem("SKU-114B", "Feedback Apple Small", "930114B", PriceSourceKind.StoreRetailPrice, 1.8m),
            CreateItem("SKU-114C", "Feedback Apple Large", "930114B", PriceSourceKind.StoreRetailPrice, 2.8m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            userFeedbackService: feedback,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930114B");

        Assert.Equal(StatusFeedbackKind.Warning, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanMultipleMatches], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_uses_lookup_code_when_metadata_barcode_is_shared()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll(
        [
            CreateItem("SKU-117", "Base Barcode Item", "9525812460346", PriceSourceKind.StoreRetailPrice, 1.8m, productBarcode: "9525812460346"),
            CreateItem("SKU-118", "Variant Metadata Item", "HB246-GJ-013", PriceSourceKind.StoreRetailPrice, 2.8m, productBarcode: "9525812460346")
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner)
        {
            IsTouchKeyboardOpen = true
        };
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("9525812460346");

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Base Barcode Item", line.DisplayName);
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public void Pos_terminal_select_match_adds_item_and_closes_popup_input_and_keyboard()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var first = CreateItem("SKU-119", "Scanner Apple Small", "930119", PriceSourceKind.StoreRetailPrice, 1.8m);
        var second = CreateItem("SKU-120", "Scanner Apple Large", "930119", PriceSourceKind.StoreRetailPrice, 2.8m);
        index.ReplaceAll([first, second]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsMatchesPopupOpen = true,
            IsTouchKeyboardOpen = true,
            ScanText = "930119"
        };

        viewModel.SelectMatchCommand.Execute(second);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Scanner Apple Large", line.DisplayName);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public void Pos_terminal_repeated_scan_keeps_same_cart_line_and_updates_quantity()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll([CreateItem("SKU-116", "Scanner Pear", "930116", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);
        var firstScanAt = DateTimeOffset.Now;

        scanner.Emit("930116", firstScanAt);
        var firstLine = Assert.Single(viewModel.CartLines);
        scanner.Emit("930116", firstScanAt.AddMilliseconds(100));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Same(firstLine, line);
        Assert.Equal(2m, line.Quantity);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(4m, viewModel.ActualAmount);
    }

    [Fact]
    public void Pos_terminal_non_consecutive_repeated_scan_creates_new_cart_line_and_then_merges_tail()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll(
        [
            CreateItem("SKU-116A", "Scanner Apple", "930116A", PriceSourceKind.StoreRetailPrice, 2m),
            CreateItem("SKU-116B", "Scanner Banana", "930116B", PriceSourceKind.StoreRetailPrice, 3m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);
        var firstScanAt = DateTimeOffset.Now;

        scanner.Emit("930116A", firstScanAt);
        scanner.Emit("930116B", firstScanAt.AddMilliseconds(100));
        scanner.Emit("930116A", firstScanAt.AddMilliseconds(200));
        scanner.Emit("930116A", firstScanAt.AddMilliseconds(300));

        Assert.Equal(3, viewModel.CartLines.Count);
        Assert.Equal("930116A", viewModel.CartLines[0].LookupCodeNormalized);
        Assert.Equal(1m, viewModel.CartLines[0].Quantity);
        Assert.Equal("930116B", viewModel.CartLines[1].LookupCodeNormalized);
        Assert.Equal(1m, viewModel.CartLines[1].Quantity);
        Assert.Equal("930116A", viewModel.CartLines[2].LookupCodeNormalized);
        Assert.Equal(2m, viewModel.CartLines[2].Quantity);
        Assert.Same(viewModel.CartLines[2], viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_keypad_caps_decimal_input_at_two_places()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");

        Assert.Equal("1.23", viewModel.KeypadBuffer);
    }

    [Fact]
    public void Pos_terminal_keypad_quick_decimal_buttons_replace_decimal_places()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute("QuickHalf");

        Assert.Equal("12.50", viewModel.KeypadBuffer);

        viewModel.KeypadInputCommand.Execute("QuickNinetyNine");

        Assert.Equal("12.99", viewModel.KeypadBuffer);
    }

    [Fact]
    public async Task Pos_terminal_keypad_can_modify_selected_line_quantity_and_price()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-129", "Adjustable Tea", "930129", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930129";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);

        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ModifySelectedLinePriceCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(3m, line.UnitPrice);
        Assert.Equal(6m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public async Task Pos_terminal_keypad_rejects_decimal_quantity_updates()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-136", "Integer Tea", "930136", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930136";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("2.5", viewModel.KeypadBuffer);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(2m, viewModel.ActualAmount);
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_keypad_can_apply_selected_line_discount_amount_and_percent()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-130", "Discount Tea", "930130", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930130";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(8m, viewModel.ActualAmount);

        viewModel.KeypadInputCommand.Execute("8");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(0.85m, line.DiscountAmount);
        Assert.Equal("-8.5%", line.DiscountRateText);
        Assert.Equal(9.15m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public async Task Pos_terminal_adding_item_recalculates_promotions_and_applies_discount()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-ADD", "Promo Add Tea", "930PROMO1", PriceSourceKind.StoreRetailPrice, 10m)]);
        var promotionRepository = new FakeLocalPromotionRepository();
        promotionRepository.SeedStore(
            Session.StoreCode,
            [CreatePromotionRule("PROMO-ADD", ["SKU-PROMO-ADD"], applyQuantity: 2, fixedPrice: 15m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: new PromotionEvaluationService(promotionRepository));

        viewModel.ScanText = "930PROMO1";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO1";
        await ExecuteManualScanAsync(viewModel);

        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => line.DiscountAmount == 5m);

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(15m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_quantity_and_price_changes_clear_stale_promotion_discount_and_recalculate()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-EDIT", "Promo Edit Tea", "930PROMO2", PriceSourceKind.StoreRetailPrice, 10m)]);
        var promotionRepository = new FakeLocalPromotionRepository();
        promotionRepository.SeedStore(
            Session.StoreCode,
            [CreatePromotionRule("PROMO-EDIT", ["SKU-PROMO-EDIT"], applyQuantity: 2, fixedPrice: 15m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: new PromotionEvaluationService(promotionRepository));

        viewModel.ScanText = "930PROMO2";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO2";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => line.DiscountAmount == 5m);

        viewModel.SelectedCartLine = line;
        viewModel.KeypadBuffer = "1";
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);
        await WaitUntilAsync(() => line.Quantity == 1m && line.DiscountAmount == 0m);

        viewModel.KeypadBuffer = "2";
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);
        await WaitUntilAsync(() => line.Quantity == 2m && line.DiscountAmount == 5m);

        viewModel.KeypadBuffer = "12";
        viewModel.ModifySelectedLinePriceCommand.Execute(null);
        await WaitUntilAsync(() => line.UnitPrice == 12m && line.DiscountAmount == 9m);

        Assert.Equal(15m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_manual_discount_recalculates_other_lines_without_reusing_manual_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-PROMO-MANUAL-A", "Promo Manual Tea A", "930PROMO3A", PriceSourceKind.StoreRetailPrice, 10m),
            CreateItem("SKU-PROMO-MANUAL-B", "Promo Manual Tea B", "930PROMO3B", PriceSourceKind.StoreRetailPrice, 10m)
        ]);
        var promotionRepository = new FakeLocalPromotionRepository();
        promotionRepository.SeedStore(
            Session.StoreCode,
            [
                CreatePromotionRule("PROMO-MANUAL-A", ["SKU-PROMO-MANUAL-A"], applyQuantity: 2, fixedPrice: 15m),
                CreatePromotionRule("PROMO-MANUAL-B", ["SKU-PROMO-MANUAL-B"], applyQuantity: 2, fixedPrice: 15m)
            ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: new PromotionEvaluationService(promotionRepository));

        viewModel.ScanText = "930PROMO3A";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO3A";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO3B";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO3B";
        await ExecuteManualScanAsync(viewModel);

        var lineA = Assert.Single(viewModel.CartLines, item => item.LookupCode == "930PROMO3A");
        var lineB = Assert.Single(viewModel.CartLines, item => item.LookupCode == "930PROMO3B");
        await WaitUntilAsync(() => lineA.DiscountAmount == 5m && lineB.DiscountAmount == 5m);

        viewModel.SelectedCartLine = lineA;
        viewModel.KeypadBuffer = "1";
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);
        await WaitUntilAsync(() => lineA.DiscountAmount == 1m && lineB.DiscountAmount == 5m);

        Assert.Equal(34m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_promotion_evaluation_failure_keeps_existing_promotion_discount_and_sets_localized_warning_status()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var openedPayment = false;
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        index.ReplaceAll([CreateItem("SKU-PROMO-FAIL", "Promo Fail Tea", "930PROMO4", PriceSourceKind.StoreRetailPrice, 10m)]);
        var promotionService = new ScriptedPromotionEvaluationService();
        promotionService.EnqueueSync(lines => []);
        promotionService.EnqueueSync(lines => [new PromotionLineDiscount(Assert.Single(lines), 5m)]);
        promotionService.EnqueueThrow(new InvalidOperationException("promotion cache unavailable"));
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => openedPayment = true,
            localization: localization,
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO4";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO4";
        await ExecuteManualScanAsync(viewModel);

        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => line.DiscountAmount == 5m);

        viewModel.ScanText = "930PROMO4";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => viewModel.StatusMessage == localization.T("pos.status.promotionFallback"));

        Assert.Equal(3m, line.Quantity);
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(10m, line.UnitPrice);
        Assert.Equal(25m, viewModel.ActualAmount);
        Assert.Equal(StatusFeedbackKind.Warning, viewModel.StatusFeedbackKind);

        viewModel.OpenPaymentCommand.Execute(null);

        Assert.True(openedPayment);
    }

    [Fact]
    public async Task Pos_terminal_promotion_budget_exceeded_clears_old_promotion_warns_and_still_opens_payment()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var openedPayment = false;
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        index.ReplaceAll([CreateItem(
            "SKU-PROMO-BUDGET",
            "Promo Budget Tea",
            "930PROMO-BUDGET",
            PriceSourceKind.StoreRetailPrice,
            10m)]);
        var promotionService = new ScriptedPromotionEvaluationService();
        promotionService.EnqueueSync(lines => []);
        promotionService.EnqueueSync(lines => [new PromotionLineDiscount(Assert.Single(lines), 5m)]);
        promotionService.EnqueueThrow(new PromotionComputationBudgetExceededException(
            "PROMO-HUGE",
            "rule",
            PromotionComputationBudget.MaxWorkUnitsPerRule + 1,
            PromotionComputationBudget.MaxWorkUnitsPerRule));
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => openedPayment = true,
            localization: localization,
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO-BUDGET";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930PROMO-BUDGET";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => line.DiscountAmount == 5m);

        viewModel.ScanText = "930PROMO-BUDGET";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() =>
            viewModel.StatusMessage == localization.T("pos.status.promotionBudgetExceeded"));

        Assert.Equal(3m, line.Quantity);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.None, line.DiscountSource);
        Assert.Equal(30m, viewModel.ActualAmount);
        Assert.Equal(StatusFeedbackKind.Warning, viewModel.StatusFeedbackKind);

        viewModel.OpenPaymentCommand.Execute(null);

        Assert.True(openedPayment);
    }

    [Fact]
    public async Task Pos_terminal_promotion_evaluation_does_not_recurse_when_discount_application_raises_cart_changed()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-COUNT", "Promo Count Tea", "930PROMO5", PriceSourceKind.StoreRetailPrice, 10m)]);
        var promotionService = new CountingPromotionEvaluationService();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO5";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 1);

        viewModel.ScanText = "930PROMO5";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => promotionService.CallCount == 2 && line.DiscountAmount == 5m);
        await Task.Delay(100);

        Assert.Equal(2, promotionService.CallCount);
    }

    [Fact]
    public async Task Pos_terminal_promotion_evaluation_requeues_latest_cart_change_while_previous_evaluation_is_still_running()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-PROMO-QUEUE", "Promo Queue Tea", "930PROMO6", PriceSourceKind.StoreRetailPrice, 10m)]);
        var firstEvaluationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionService = new ScriptedPromotionEvaluationService();
        promotionService.EnqueueAsync(async lines =>
        {
            await firstEvaluationGate.Task;
            return [];
        });
        promotionService.EnqueueSync(lines => [new PromotionLineDiscount(Assert.Single(lines), 5m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: promotionService);

        viewModel.ScanText = "930PROMO6";
        await ExecuteManualScanAsync(viewModel);
        await WaitUntilAsync(() => promotionService.CallCount == 1);

        viewModel.ScanText = "930PROMO6";
        await ExecuteManualScanAsync(viewModel);
        await Task.Delay(50);
        Assert.Equal(1, promotionService.CallCount);

        firstEvaluationGate.SetResult();

        var line = Assert.Single(viewModel.CartLines);
        await WaitUntilAsync(() => promotionService.CallCount == 2 && line.DiscountAmount == 5m);

        Assert.Equal([1m, 2m], promotionService.SnapshotQuantities);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(15m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_keypad_commands_ignore_missing_selection_or_invalid_input()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-131", "Guarded Tea", "930131", PriceSourceKind.StoreRetailPrice, 5m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("2", viewModel.KeypadBuffer);
        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);

        viewModel.ScanText = "930131";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.KeypadInputCommand.Execute("Clear");

        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(5m, viewModel.ActualAmount);
        Assert.Equal("pos.status.invalidKeypadValue", viewModel.StatusMessage);
        Assert.Equal(
            [UserFeedbackCue.OperationError, UserFeedbackCue.ScanAdded, UserFeedbackCue.OperationError],
            feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_repeated_same_error_still_pulses_and_replays_feedback()
    {
        var feedback = new FakeUserFeedbackService();
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(2, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.OperationError, UserFeedbackCue.OperationError], feedback.Cues);
    }

    [Fact]
    public async Task Pos_terminal_non_error_line_update_does_not_play_extra_feedback()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-129A", "Quiet Tea", "930129A", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback)
        {
            ScanText = "930129A"
        };

        await ExecuteManualScanAsync(viewModel);
        feedback.Cues.Clear();

        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Empty(feedback.Cues);
        Assert.Equal(StatusFeedbackKind.Neutral, viewModel.StatusFeedbackKind);
    }

    [Fact]
    public async Task Pos_terminal_keypad_commands_show_friendly_messages_for_zero_quantity_and_invalid_discounts()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-134", "Friendly Tea", "930134", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930134";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("0");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("0", viewModel.KeypadBuffer);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal("pos.status.quantityMustBePositive", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Equal("11", viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal("pos.status.discountAmountTooHigh", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.Equal("101", viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal("pos.status.discountPercentOutOfRange", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_whole_order_discount_shows_friendly_messages_for_invalid_values()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-135", "Order Friendly Tea", "930135", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930135";
        await ExecuteManualScanAsync(viewModel);

        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("11", viewModel.KeypadBuffer);
        Assert.Equal(0m, viewModel.DiscountAmount);
        Assert.Equal("pos.status.discountAmountTooHigh", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("101", viewModel.KeypadBuffer);
        Assert.Equal(0m, viewModel.DiscountAmount);
        Assert.Equal("pos.status.discountPercentOutOfRange", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_whole_order_toggle_applies_one_time_order_discount()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-132", "Small Tea", "930132", PriceSourceKind.StoreRetailPrice, 10m),
            CreateItem("SKU-133", "Large Tea", "930133", PriceSourceKind.StoreRetailPrice, 30m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930132";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930133";
        await ExecuteManualScanAsync(viewModel);

        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        var first = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930132");
        var second = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930133");
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(4m, viewModel.DiscountAmount);
        Assert.Equal(36m, viewModel.ActualAmount);
        Assert.Equal("pos.status.orderDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_blocks_payment_until_zero_price_line_is_fixed()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        var openedPayment = false;
        index.ReplaceAll([CreateItem("SKU-140", "Zero Price Tea", "930140", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => openedPayment = true,
            userFeedbackService: feedback);

        viewModel.ScanText = "930140";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.OpenPaymentCommand.Execute(null);

        Assert.False(openedPayment);
        Assert.True(line.HasZeroUnitPrice);
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(2, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanAdded, UserFeedbackCue.OperationError], feedback.Cues);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLinePriceCommand.Execute(null);
        viewModel.OpenPaymentCommand.Execute(null);

        Assert.True(openedPayment);
        Assert.False(line.HasZeroUnitPrice);
    }

    [Fact]
    public async Task Pos_terminal_quick_discount_applies_percent_to_selected_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-136", "Quick Discount Tea", "930136", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930136";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.KeypadInputCommand.Execute("9");

        viewModel.ApplyQuickDiscountPercentCommand.Execute("20");

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(8m, viewModel.ActualAmount);
        Assert.Equal("pos.status.lineDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_quick_discount_applies_percent_to_whole_order_and_turns_mode_off()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-137", "Quick Small Tea", "930137", PriceSourceKind.StoreRetailPrice, 10m),
            CreateItem("SKU-138", "Quick Large Tea", "930138", PriceSourceKind.StoreRetailPrice, 30m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930137";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930138";
        await ExecuteManualScanAsync(viewModel);
        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("9");

        viewModel.ApplyQuickDiscountPercentCommand.Execute("50");

        var first = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930137");
        var second = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930138");
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(5m, first.DiscountAmount);
        Assert.Equal(15m, second.DiscountAmount);
        Assert.Equal(20m, viewModel.ActualAmount);
        Assert.Equal("pos.status.orderDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_quick_discount_requires_selection_or_cart()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ApplyQuickDiscountPercentCommand.Execute("10");

        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);

        viewModel.IsWholeOrderOperation = true;
        viewModel.ApplyQuickDiscountPercentCommand.Execute("10");

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_remove_line_command_removes_entire_cart_line_and_recalculates_total()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-111", "Apples", "930111", PriceSourceKind.StoreRetailPrice, 2m),
            CreateItem("SKU-112", "Bananas", "930112", PriceSourceKind.StoreRetailPrice, 3m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930111";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930111";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930112";
        await ExecuteManualScanAsync(viewModel);

        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.Equal(7m, viewModel.ActualAmount);

        var appleLine = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930111");
        Assert.Equal(2m, appleLine.Quantity);
        viewModel.RemoveLineCommand.Execute(appleLine);

        var remaining = Assert.Single(viewModel.CartLines);
        Assert.Equal("Bananas", remaining.DisplayName);
        Assert.Equal(3m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_line_quantity_commands_update_totals_selection_and_counts()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-123", "Touch Apples", "930123", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930123";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);

        Assert.Equal(1m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.False(viewModel.DecreaseLineCommand.CanExecute(line));

        viewModel.IncreaseLineCommand.Execute(line);

        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(2m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.Equal(4m, viewModel.ActualAmount);
        Assert.True(viewModel.DecreaseLineCommand.CanExecute(line));

        viewModel.DecreaseLineCommand.Execute(line);

        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(1m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.Equal(2m, viewModel.ActualAmount);
        Assert.False(viewModel.DecreaseLineCommand.CanExecute(line));

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(viewModel.DecreaseLineCommand)
            .ExecuteAsync(line);

        Assert.Same(line, Assert.Single(viewModel.CartLines));
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(1m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.Equal(2m, viewModel.ActualAmount);

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(viewModel.RemoveLineCommand)
            .ExecuteAsync(line);

        Assert.Empty(viewModel.CartLines);
        Assert.Null(viewModel.SelectedCartLine);
        Assert.Equal(0m, viewModel.CartItemQuantity);
        Assert.Equal(0, viewModel.CartSkuCount);
        Assert.Equal(0m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_cart_operations_write_diagnostic_logs()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var logs = new ConcurrentQueue<string>();
        var paymentOpenCount = 0;
        index.ReplaceAll([CreateItem("SKU-129", "Logged Apples", "930129", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => paymentOpenCount++);

        using var logCapture = CaptureClientLog(logs);
        viewModel.ScanText = "930129";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.IncreaseLineCommand.Execute(line);
        viewModel.OpenPaymentCommand.Execute(null);

        Assert.Equal(1, paymentOpenCount);
        Assert.True(HasLog(logs, "[CartPerf]"));
        Assert.True(HasLog(logs, "operation=cart-changed"));
        Assert.True(HasLog(logs, "operation=scan-auto-add"));
        Assert.True(HasLog(logs, "operation=increase-line"));
        Assert.True(HasLog(logs, "operation=open-payment"));
        Assert.True(HasLog(logs, "syncCartElapsedMs="));
        Assert.True(HasLog(logs, "stateRefreshElapsedMs="));
        Assert.True(HasLog(logs, "totalElapsedMs="));
    }

    [Fact]
    public async Task Pos_terminal_counts_quantity_and_sku_lines_separately()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-124", "Repeated Item", "930124", PriceSourceKind.StoreRetailPrice, 2m),
            CreateItem("SKU-125", "Second Item", "930125", PriceSourceKind.StoreRetailPrice, 3m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930124";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930124";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930125";
        await ExecuteManualScanAsync(viewModel);

        Assert.Equal(3m, viewModel.CartItemQuantity);
        Assert.Equal(2, viewModel.CartSkuCount);
    }

    [Fact]
    public void Pos_terminal_clear_search_command_clears_input_and_closes_popups()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            ScanText = "apple",
            IsMatchesPopupOpen = true,
            IsTouchKeyboardOpen = true
        };

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public async Task Pos_terminal_keeps_local_add_when_remote_lookup_fails()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-104", "Local Coffee", "930104", PriceSourceKind.StoreRetailPrice, 6.5m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930104";
        await ExecuteManualScanAsync(viewModel);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);

        remoteLookup.SetException(new InvalidOperationException("remote unavailable"));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup failed"));

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);
        Assert.DoesNotContain("Remote lookup failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pos_terminal_remote_deleted_does_not_remove_matching_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-105", "Retired Snack", "930105", PriceSourceKind.StoreRetailPrice, 4.2m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task,
            reloadCatalogAsync: _ =>
            {
                index.ReplaceAll([]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
            });

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930105";
        await ExecuteManualScanAsync(viewModel);

        Assert.Single(viewModel.CartLines);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930105", Found: false, Item: null, DeletedCount: 1));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup deleted local cache only"));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Retired Snack", line.DisplayName);
        Assert.Single(cart.Lines);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.True(string.IsNullOrEmpty(viewModel.ScanText));
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_updates_cart_only_when_identity_matches()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem(
            "SKU-126",
            "Local Juice",
            "930126",
            PriceSourceKind.ProductBase,
            3m,
            referenceCode: "REF-126");
        var remoteItem = CreateItem(
            "SKU-126",
            "Remote Juice",
            "930126",
            PriceSourceKind.StoreRetailPrice,
            3.8m,
            referenceCode: "REF-126",
            productImage: "https://images.example/juice.jpg");
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([localItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        viewModel.ScanText = "930126";
        await ExecuteManualScanAsync(viewModel);
        viewModel.ScanText = "930126";
        await ExecuteManualScanAsync(viewModel);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(2m, line.Quantity);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930126", Found: true, Item: remoteItem, DeletedCount: 0));
        await WaitUntilAsync(() => viewModel.CartLines.Single().DisplayName == "Remote Juice");

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Remote Juice", line.DisplayName);
        Assert.Equal(3.8m, line.UnitPrice);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("https://images.example/juice.jpg", line.ProductImage);
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_ignores_cart_update_when_identity_differs()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem(
            "SKU-127",
            "Local Snack",
            "930127",
            PriceSourceKind.ProductBase,
            2.5m,
            referenceCode: "REF-127");
        var remoteItem = CreateItem(
            "SKU-OTHER",
            "Remote Snack",
            "930127",
            PriceSourceKind.StoreRetailPrice,
            3.2m,
            referenceCode: "REF-127");
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([localItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930127";
        await ExecuteManualScanAsync(viewModel);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930127", Found: true, Item: remoteItem, DeletedCount: 0));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup ignored for cart"));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Snack", line.DisplayName);
        Assert.Equal("SKU-127", line.ProductCode);
        Assert.Equal(2.5m, line.UnitPrice);
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_timeout_keeps_cart_unchanged()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-128", "Timeout Tea", "930128", PriceSourceKind.StoreRetailPrice, 5.5m);
        var logs = new ConcurrentQueue<string>();
        var timeProvider = new FakeTimeProvider();
        var remoteLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([item]);
        // 显式构造 workflow service 以注入 FakeTimeProvider；ViewModel 其余行为与默认构造一致。
        var workflowService = new PosTerminalWorkflowService(
            index,
            cart,
            remoteLookupRefreshAsync: async (_, _, cancellationToken) =>
            {
                remoteLookupStarted.TrySetResult();
                // 只有超时令牌能结束这次等待：证明购物车不会被"成功结果"改写。
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RemoteLookupRefreshResult("S001", "930128", Found: false, Item: null, DeletedCount: 1);
            },
            timeProvider: timeProvider);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            workflowService: workflowService);

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930128";
        await ExecuteManualScanAsync(viewModel);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Timeout Tea", line.DisplayName);

        // 远程查询回调被调用时，超时 CTS 已在 FakeTimeProvider 上注册，此时推进虚拟时间必然触发超时。
        await WaitUntilAsync(() => remoteLookupStarted.Task.IsCompleted, diagnostics: () => DescribeCapturedLogs(logs));
        timeProvider.Advance(PosTerminalWorkflowService.RemoteLookupTimeout);

        await WaitUntilAsync(() => HasLog(logs, "remote lookup timeout"), diagnostics: () => DescribeCapturedLogs(logs));

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Timeout Tea", line.DisplayName);
        Assert.Equal(5.5m, viewModel.ActualAmount);
        Assert.DoesNotContain("timeout", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pos_terminal_sync_command_refreshes_matches_and_index()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var oldItem = CreateItem("SKU-106", "Old Tea", "930106", PriceSourceKind.ProductBase, 3m);
        var syncedItem = CreateItem("SKU-107", "Synced Tea", "930107", PriceSourceKind.StoreRetailPrice, 3.5m);
        var syncToken = CancellationToken.None;
        index.ReplaceAll([oldItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            syncCatalogAsync: cancellationToken =>
            {
                syncToken = cancellationToken;
                index.ReplaceAll([syncedItem]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([syncedItem]);
            });
        viewModel.LoadMatches([oldItem]);

        await viewModel.SyncCommand.ExecuteAsync(null);

        var indexedItem = Assert.Single(index.Search("930107"));
        Assert.Equal("Synced Tea", indexedItem.DisplayName);
        Assert.Contains("completed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(syncToken.CanBeCanceled);
    }

    [Fact]
    public async Task Pos_terminal_reset_catalog_command_uses_reset_callback()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var oldItem = CreateItem("SKU-108", "Old Reset Tea", "930108", PriceSourceKind.ProductBase, 3m);
        var resetItem = CreateItem("SKU-109", "Reset Tea", "930109", PriceSourceKind.StoreRetailPrice, 4.5m);
        var syncCalled = false;
        var resetCalled = false;
        var resetToken = CancellationToken.None;
        index.ReplaceAll([oldItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            syncCatalogAsync: _ =>
            {
                syncCalled = true;
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([oldItem]);
            },
            resetCatalogAsync: cancellationToken =>
            {
                resetCalled = true;
                resetToken = cancellationToken;
                index.ReplaceAll([resetItem]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([resetItem]);
            });
        viewModel.LoadMatches([oldItem]);

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        Assert.False(syncCalled);
        Assert.True(resetCalled);
        var indexedItem = Assert.Single(index.Search("930109"));
        Assert.Equal("Reset Tea", indexedItem.DisplayName);
        Assert.Contains("reset completed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(resetToken.CanBeCanceled);
    }

    [Fact]
    public async Task Pos_terminal_sync_command_cancel_stops_running_catalog_download()
    {
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedToken = CancellationToken.None;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            syncCatalogAsync: async cancellationToken =>
            {
                receivedToken = cancellationToken;
                using var registration = cancellationToken.Register(cancellationObserved.SetResult);
                downloadStarted.SetResult();
                await Task.WhenAny(cancellationObserved.Task, releaseDownload.Task);
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            });

        var execution = viewModel.SyncCommand.ExecuteAsync(null);
        await downloadStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        viewModel.SyncCommand.Cancel();
        var wasCancellationRequested = receivedToken.IsCancellationRequested;
        releaseDownload.TrySetResult();
        await execution.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(receivedToken.CanBeCanceled);
        Assert.True(wasCancellationRequested);
        Assert.Equal("pos.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_sync_command_cancel_stops_online_refresh_before_download()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCalled = false;
        var receivedToken = CancellationToken.None;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            syncCatalogAsync: _ =>
            {
                downloadCalled = true;
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
            },
            refreshOnlineAsync: async cancellationToken =>
            {
                receivedToken = cancellationToken;
                using var registration = cancellationToken.Register(cancellationObserved.SetResult);
                refreshStarted.SetResult();
                await Task.WhenAny(cancellationObserved.Task, releaseRefresh.Task);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            });

        var execution = viewModel.SyncCommand.ExecuteAsync(null);
        await refreshStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        viewModel.SyncCommand.Cancel();
        var wasCancellationRequested = receivedToken.IsCancellationRequested;
        releaseRefresh.TrySetResult();
        await execution.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(receivedToken.CanBeCanceled);
        Assert.True(wasCancellationRequested);
        Assert.False(downloadCalled);
        Assert.Equal("pos.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_recalculates_change_from_cash_tenders()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-102", "Orange Juice", "930102", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "10";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(2.2m, viewModel.ChangeDue);
        // 澳币现金最小面额 0.05，顾客给整钞后超收是常态。超收时剩余应收必须是 0：
        // 若同屏在找零 2.20 旁边再显示"还差 2.20"，收银员可能据此重复收款。
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_keeps_positive_remaining_amount_when_cash_is_insufficient()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-112", "Partial Cash Juice", "930112", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        // 未收够时剩余应收仍要显示真实差额，且不产生找零。
        Assert.Equal(2.8m, viewModel.RemainingAmount);
        Assert.Equal(0m, viewModel.ChangeDue);
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_does_not_show_confirm_payment_from_keyboard_input_only()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-109", "Keyboard Cash Tea", "930109", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "10";

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.False(viewModel.IsConfirmPaymentVisible);
    }

    [Fact]
    public async Task Payment_page_refresh_cart_recalculates_change_after_cart_update()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.ProductBase, 7m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);
        viewModel.TenderAmountText = "10";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        cart.UpdateLineFromRemote(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.StoreRetailPrice, 8m));
        viewModel.RefreshCart();

        Assert.Equal(2m, viewModel.ChangeDue);
        Assert.Equal(8m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Cash_payment_blocks_zero_price_cart_without_saving_or_clearing()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-141", "Zero Payment Tea", "930141", PriceSourceKind.StoreRetailPrice, 0m));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "0"
        };

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Null(orders.LastOrder);
        Assert.Single(cart.Lines);
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Cash_payment_blocks_non_integer_quantity_cart_without_saving_or_clearing()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-142", "Fractional Tea", "930142", PriceSourceKind.StoreRetailPrice, 5m));
        SetUnsafeQuantity(line, 1.5m);
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "10"
        };

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Null(orders.LastOrder);
        Assert.Single(cart.Lines);
        Assert.Equal(1.5m, line.Quantity);
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_adds_negative_cash_tender_and_allows_confirmation()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-1",
            null,
            "Refund Tea",
            "930142R",
            "ITEM-REFUND-1",
            null,
            1m,
            7.82m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository()),
            Session);

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.True(viewModel.IsRefundMode);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
        Assert.Equal(7.82m, viewModel.RemainingAmount);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(-7.80m, tender.Amount);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(orders.LastOrder);
        Assert.All(orders.LastOrder!.Payments, payment => Assert.True(payment.Amount < 0m));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Refund_mode_adds_negative_voucher_tender_without_source_voucher_code()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-VOUCHER",
            null,
            "Refund Voucher Tea",
            "930142V",
            "ITEM-REFUND-VOUCHER",
            null,
            1m,
            8.5m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-VOUCHER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository()),
            Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.Equal(string.Empty, viewModel.VoucherCodeText);
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Voucher, tender.Method);
        Assert.Equal(-8.5m, tender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", tender.Reference);
    }

    [Fact]
    public async Task Refund_mode_voucher_button_adds_tender_without_opening_code_entry_dialog()
    {
        var cart = CreateRefundVoucherCart();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository()),
            Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.True(viewModel.OpenVoucherEntryCommand.CanExecute(null));

        // 退款的代金券由系统结算时新发，点按钮不应再弹出“扫码或输入代金券号”，而是直接加退券付款项。
        await viewModel.OpenVoucherEntryCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVoucherEntryDialogOpen);
        Assert.Equal(string.Empty, viewModel.VoucherEntryText);
        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Voucher, tender.Method);
        Assert.Equal(-8.5m, tender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", tender.Reference);
    }

    [Fact]
    public async Task Refund_mode_offline_voucher_entry_button_shows_unavailable_without_opening_dialog()
    {
        var cart = CreateRefundVoucherCart();
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, -8.5m, "VOUCHER_REFUND_PENDING")
        };
        var viewModel = new PaymentViewModel(cart, workflow, OfflineSession, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(OfflineSession);

        await viewModel.OpenVoucherEntryCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVoucherEntryDialogOpen);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_of_voucher_paid_goods_requires_voucher_share_before_cash()
    {
        var originalOrder = Guid.NewGuid();
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VOUCHER-FUNDED",
            null,
            "Voucher Funded Tea",
            "930142F",
            "ITEM-VOUCHER-FUNDED",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-VOUCHER-FUNDED",
            originalOrder,
            Guid.NewGuid()));
        // 原单现金 20 + 代金券 10：退 10 元商品须至少 3.34 退代金券。
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Cash, 20m, 0m, 20m, null, OriginalOrderGuid: originalOrder),
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Voucher, 10m, 0m, 10m, null, OriginalOrderGuid: originalOrder)
        ]);
        // 状态文案带金额占位符，需注入真实本地化；未注入时 T() 只回显键名，金额无从断言。
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository()),
            Session,
            localization: localization,
            paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.Contains("3.34", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        var voucherTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Voucher, voucherTender.Method);
        Assert.Equal(-3.34m, voucherTender.Amount);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var cashTender = Assert.Single(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Cash);
        Assert.Equal(-6.65m, cashTender.Amount);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Recovered_square_refund_policy_survives_page_reentry_and_clears_with_the_cart()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-ALTERNATIVE",
            null,
            "Alternative Refund Tea",
            "930142A",
            "ITEM-REFUND-ALTERNATIVE",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-ALTERNATIVE",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(
                PaymentMethodKind.Card,
                10m,
                0m,
                10m,
                "SQ:original-card-refund")
        ]);
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        viewModel.PrepareForEntry(Session);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        var cardCanExecuteChanged = 0;
        viewModel.SelectCardCommand.CanExecuteChanged += (_, _) => cardCanExecuteChanged++;

        viewModel.SetAlternativeRefundMethodRequired(true);

        Assert.True(cardCanExecuteChanged > 0);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.Equal("payment.refund.status.alternativeMethodRequired", viewModel.StatusMessage);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.refund.status.alternativeMethodRequired", viewModel.StatusMessage);

        viewModel.PrepareForEntry(Session);

        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.Equal("payment.refund.status.alternativeMethodRequired", viewModel.StatusMessage);

        cart.Clear();
        viewModel.RefreshCart();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-NEXT",
            null,
            "Next Refund Tea",
            "930142B",
            "ITEM-REFUND-NEXT",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-NEXT",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(
                PaymentMethodKind.Card,
                10m,
                0m,
                10m,
                "SQ:next-original-card-refund")
        ]);
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.refund.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_offline_voucher_button_shows_unavailable_without_adding_tender()
    {
        var cart = CreateRefundVoucherCart();
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, -8.5m, "VOUCHER_REFUND_PENDING")
        };
        var viewModel = new PaymentViewModel(cart, workflow, OfflineSession, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(OfflineSession);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_offline_voucher_tender_blocks_back_with_offline_status()
    {
        var cart = CreateRefundVoucherCart();
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, -8.5m, "VOUCHER_REFUND_PENDING")
        };
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(cart, workflow, Session, onBackToPos: () => returnedToPos = true, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(Session);
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.Session = OfflineSession;

        viewModel.BackToPosCommand.Execute(null);

        Assert.False(returnedToPos);
        Assert.Single(viewModel.PaymentTenders);
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_offline_voucher_tender_blocks_completion_without_calling_workflow()
    {
        var cart = CreateRefundVoucherCart();
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, -8.5m, "VOUCHER_REFUND_PENDING")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForEntry(Session);
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.Session = OfflineSession;
        var addTenderCallsBeforeConfirm = workflow.AddTenderCallCount;

        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(addTenderCallsBeforeConfirm, workflow.AddTenderCallCount);
        Assert.Equal(0, workflow.CompletePaymentCallCount);
        Assert.Single(viewModel.PaymentTenders);
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_session_change_to_offline_refreshes_commands_and_uses_voucher_guard()
    {
        var cart = CreateRefundVoucherCart();
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, -8.5m, "VOUCHER_REFUND_PENDING")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        var selectVoucherCanExecuteChanged = 0;
        var confirmCanExecuteChanged = 0;
        viewModel.SelectVoucherCommand.CanExecuteChanged += (_, _) => selectVoucherCanExecuteChanged++;
        viewModel.ConfirmPaymentCommand.CanExecuteChanged += (_, _) => confirmCanExecuteChanged++;

        viewModel.PrepareForEntry(Session);
        viewModel.Session = OfflineSession;

        Assert.True(selectVoucherCanExecuteChanged > 0);
        Assert.True(confirmCanExecuteChanged > 0);

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_card_button_uses_each_original_card_capacity()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD",
            null,
            "Refund Card Tea",
            "930142C",
            "ITEM-REFUND-CARD",
            null,
            1m,
            12m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 5m, 0m, 5m, "SQ:card-1"),
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 7m, 0m, 7m, "SQ:card-2")
        ]);
        var cardTerminal = new ApprovedCardTerminalClient("CARD-REFUND");
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository(),
                cardTerminalClient: cardTerminal),
            Session);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        viewModel.PrepareForEntry(Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var firstTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(-5m, firstTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(firstTender.Reference, out var firstOriginal));
        Assert.Equal("SQ:card-1", firstOriginal);
        Assert.Equal(["SQ:card-1"], cardTerminal.RefundOriginalReferences);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Null(completed);
        Assert.Null(orders.LastOrder);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.NotNull(completed);
        Assert.Same(orders.LastOrder, completed!.Order);
        Assert.Empty(cart.Lines);
        Assert.Equal(-12m, completed.Order.ActualAmount);
        Assert.Equal([-5m, -7m], completed.Order.Payments.Select(payment => payment.Amount).ToArray());
        Assert.All(completed.Order.Payments, payment => Assert.Equal(PaymentMethodKind.Card, payment.Method));
        Assert.Equal(["SQ:card-1", "SQ:card-2"], cardTerminal.RefundOriginalReferences);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refund_mode_card_button_rebuilds_linkly_cloud_reference_from_original_rfn()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-LINKLY",
            null,
            "Refund Linkly Tea",
            "930142L",
            "ITEM-REFUND-LINKLY",
            null,
            1m,
            8m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-LINKLY",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(
                PaymentMethodKind.Card,
                8m,
                0m,
                8m,
                "ANZCLOUD:TXN-CLOUD-1",
                [
                    new CardTransactionDto(
                        "ANZ",
                        "TXN-CLOUD-1",
                        "123456",
                        "VISA",
                        4,
                        "****1234",
                        "MID",
                        "00",
                        "APPROVED",
                        "42",
                        null,
                        8m,
                        null,
                        RefundReference: "RFN-ORIGINAL")
                ])
        ]);
        var cardTerminal = new ApprovedCardTerminalClient("CARD-REFUND");
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository(),
                cardTerminalClient: cardTerminal),
            Session);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        viewModel.PrepareForEntry(Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.NotNull(completed);
        var payment = Assert.Single(completed!.Order.Payments);
        Assert.Equal(-8m, payment.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference));
        Assert.Equal("ANZCLOUD:TXN-CLOUD-1:RFN-ORIGINAL", originalReference);
        Assert.Equal(["ANZCLOUD:TXN-CLOUD-1:RFN-ORIGINAL"], cardTerminal.RefundOriginalReferences);
        Assert.Same(orders.LastOrder, completed.Order);
    }

    [Fact]
    public async Task Refund_mode_card_button_limits_original_card_to_that_orders_return_amount()
    {
        var originalOrderA = Guid.NewGuid();
        var originalOrderB = Guid.NewGuid();
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD-A",
            null,
            "Refund Card Tea A",
            "930142CA",
            "ITEM-REFUND-CARD-A",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD-A",
            originalOrderA,
            Guid.NewGuid()));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD-B",
            null,
            "Refund Card Tea B",
            "930142CB",
            "ITEM-REFUND-CARD-B",
            null,
            1m,
            90m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD-B",
            originalOrderB,
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 100m, 0m, 100m, "SQ:card-a", OriginalOrderGuid: originalOrderA),
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 90m, 0m, 90m, "SQ:card-b", OriginalOrderGuid: originalOrderB)
        ]);
        var cardTerminal = new ApprovedCardTerminalClient("CARD-REFUND");
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository(),
                cardTerminalClient: cardTerminal),
            Session);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        viewModel.PrepareForEntry(Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var firstTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(-10m, firstTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(firstTender.Reference, out var firstOriginal));
        Assert.Equal("SQ:card-a", firstOriginal);
        Assert.Null(completed);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.NotNull(completed);
        Assert.Same(orders.LastOrder, completed!.Order);
        Assert.Equal([-10m, -90m], completed.Order.Payments.Select(payment => payment.Amount).ToArray());
        Assert.True(CardRefundReference.TryGetOriginalReference(completed.Order.Payments[1].Reference, out var secondOriginal));
        Assert.Equal("SQ:card-b", secondOriginal);
        Assert.Equal(["SQ:card-a", "SQ:card-b"], cardTerminal.RefundOriginalReferences);
    }

    [Fact]
    public async Task Zero_settlement_mode_completes_without_tenders_and_saves_empty_payments()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-ZERO-1", "Zero Tea", "930ZERO1", PriceSourceKind.StoreRetailPrice, 5m));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-ZERO-RET",
            null,
            "Zero Return Tea",
            "930ZERO2",
            "ITEM-ZERO-RET",
            null,
            1m,
            5m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-ZERO-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository()),
            Session);

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.ZeroSettlement, viewModel.PaymentMode);
        Assert.True(viewModel.IsZeroSettlementMode);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Empty(viewModel.PaymentTenders);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(orders.LastOrder);
        Assert.Empty(orders.LastOrder!.Payments);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Cash_payment_confirmation_saves_order_snapshot_and_refreshes_pending_sync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-cash-vm-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var cart = new PosCartService();
            cart.AddItem(CreateItem("SKU-103", "Whole Milk", "930103", PriceSourceKind.StoreClearancePrice, 4.4m));
            var viewModel = new PaymentViewModel(cart, new CashCheckoutService(), orders, syncQueue, Session);
            PaymentCompletedEventArgs? completed = null;
            viewModel.PaymentCompleted += (_, args) => completed = args;

            await schema.InitializeAsync();
            viewModel.TenderAmountText = "5";
            await viewModel.SelectCashCommand.ExecuteAsync(null);
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

            Assert.NotNull(completed);
            Assert.Equal(0.6m, completed.ChangeAmount);
            Assert.Equal(4.4m, completed.Order.ActualAmount);
            Assert.Empty(cart.Lines);
            Assert.Equal(1, viewModel.PendingSyncCount);
            Assert.Equal(1, await syncQueue.CountPendingAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Payment_page_blocks_card_overpay_but_allows_cash_change()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-143", "Card And Cash Tea", "930143", PriceSourceKind.StoreRetailPrice, 7.8m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-143"));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        viewModel.TenderAmountText = "10";
        viewModel.SelectedPaymentMethod = PaymentMethodKind.Card;

        Assert.False(viewModel.SelectCardCommand.CanExecute(null));

        viewModel.TenderAmountText = "10";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_card_button_auto_completes_when_default_amount_fully_pays_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146", "Default Card Tea", "930146", PriceSourceKind.StoreRetailPrice, 7.83m));
        var orders = new InMemoryOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-146"));
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        Assert.Equal(string.Empty, viewModel.TenderAmountText);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(completed);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Empty(cart.Lines);
        Assert.NotNull(orders.LastOrder);
        var payment = Assert.Single(completed!.Order.Payments);
        Assert.Equal(PaymentMethodKind.Card, payment.Method);
        Assert.Equal(7.83m, payment.Amount);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
    }

    [Fact]
    public async Task Payment_page_card_button_auto_completes_signature_receipt_fallback_approval()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146SIG", "Signature Card Tea", "930146SIG", PriceSourceKind.StoreRetailPrice, 10.08m));
        var cardTransactions = new[]
        {
            new CardTransactionDto(
                "ANZ",
                "260601120016",
                null,
                null,
                null,
                null,
                null,
                "08",
                "APPROVE WITH SIG",
                null,
                null,
                10.08m,
                "----------------------\n*** MERCHANT COPY ***\nTOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:")
        };
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:260601120016:signature-receipt-fallback-session-1:Sandbox",
            "ANZ Linkly Cloud",
            10.08m,
            cardTransactions,
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            SessionId: "signature-receipt-fallback-session-1",
            TxnRef: "260601120016",
            ResponseCode: "08",
            ResponseText: "APPROVE WITH SIG");
        var orders = new InMemoryOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-SIGNATURE", authorization));
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(completed);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Empty(cart.Lines);
        Assert.NotNull(orders.LastOrder);
        var payment = Assert.Single(completed!.Order.Payments);
        Assert.Equal(PaymentMethodKind.Card, payment.Method);
        Assert.Equal(10.08m, payment.Amount);
        var transaction = Assert.Single(payment.CardTransactions!);
        Assert.Equal("08", transaction.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", transaction.ResponseText);
        Assert.Contains("PLEASE SIGN:", transaction.ReceiptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payment_page_card_auto_completion_reauthorizes_after_grant_revoked_during_terminal_wait()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146AUTH", "Authorized Card Tea", "930146AUTH", PriceSourceKind.StoreRetailPrice, 10m));
        var completedOrder = CreateCompletedOrder(actualAmount: 10m);
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CompletePaymentResult = new CashPaymentWorkflowResult(completedOrder, 10m, 0m, 0, Session)
        };
        var authorization = new RevocableOperationAuthorizationService();
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuthorizationService: authorization);
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        var payment = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        authorization.RevokeAll();
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-REAUTHORIZED"),
            "payment.status.cardTenderAdded"));
        await payment;

        Assert.Equal(
            [
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm,
                Permissions.PosTerminal.Payment.Confirm
            ],
            authorization.RequestedPermissions);
        Assert.Equal(1, workflow.CompletePaymentCallCount);
        Assert.NotNull(completed);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Payment_page_auto_completion_blocks_manual_confirmation_while_completion_is_pending()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146RACE", "Single Completion Tea", "930146RACE", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-SINGLE-COMPLETION"),
            CompletePaymentResult = new CashPaymentWorkflowResult(CreateCompletedOrder(10m), 10m, 0m, 0, Session),
            CompletePaymentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CompletePaymentRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var automaticCompletion = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.CompletePaymentStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(viewModel.IsPaymentInteractionLocked);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(1, workflow.CompletePaymentCallCount);

        workflow.CompletePaymentRelease.SetResult();
        await automaticCompletion;

        Assert.Equal(1, workflow.CompletePaymentCallCount);
        Assert.False(viewModel.IsPaymentInteractionLocked);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_completion_propagates_fatal_workflow_exception_and_keeps_lock(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal completion failure")
            : new StackOverflowException("fatal completion failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146FATAL-COMPLETE", "Fatal Completion Tea", "930146FATAL-COMPLETE", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-FATAL-COMPLETE"),
            ThrowOnComplete = fatal
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var thrown = await Record.ExceptionAsync(() => viewModel.SelectCardCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Equal(1, workflow.CompletePaymentCallCount);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_card_tender_propagates_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal card tender failure")
            : new StackOverflowException("fatal card tender failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146FATAL-CARD", "Fatal Card Tender Tea", "930146FATAL-CARD", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-FATAL-TENDER"),
            AddTenderException = fatal
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var thrown = await Record.ExceptionAsync(() => viewModel.SelectCardCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Equal(1, workflow.AddTenderCallCount);
    }

    [Fact]
    public async Task Payment_page_card_button_blocks_partial_tender_until_remaining_is_paid()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146A", "Partial Card Tea", "930146A", PriceSourceKind.StoreRetailPrice, 7.83m));
        var workflow = new FakeCashPaymentWorkflowService();
        var completed = false;
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(cart, workflow, Session, onBackToPos: () => returnedToPos = true)
        {
            TenderAmountText = "5"
        };
        viewModel.PaymentCompleted += (_, _) => completed = true;

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Equal(7.83m, viewModel.RemainingAmount);
        Assert.False(completed);
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardMustBeFinalTender", viewModel.StatusMessage);

        viewModel.BackToPosCommand.Execute(null);

        Assert.True(returnedToPos);
    }

    [Fact]
    public void Payment_page_blocks_removing_recovered_card_attempt_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146B", "Recovered Card Tea", "930146B", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var recoveredTender = new PaymentTender(
            PaymentMethodKind.Card,
            5m,
            "CARD-APPROVED",
            IdempotencyKey: $"CARD_ATTEMPT:{Guid.NewGuid():N}");

        viewModel.RestoreRecoveredPaymentTenders([recoveredTender], "Recovered");

        Assert.False(viewModel.RemoveTenderCommand.CanExecute(recoveredTender));
        viewModel.RemoveTenderCommand.Execute(recoveredTender);
        Assert.Same(recoveredTender, Assert.Single(viewModel.PaymentTenders));
    }

    [Fact]
    public void Payment_page_recovery_projection_preserves_complete_partial_tender_snapshot_and_blocks_card()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146PROJ", "Recovered Partial Tea", "930146PROJ", PriceSourceKind.StoreRetailPrice, 10m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session);
        var tenders = new PaymentTender[]
        {
            new(PaymentMethodKind.Cash, 2m, "CASH-RECOVERED"),
            new(PaymentMethodKind.Voucher, 3m, "VOUCHER-RECOVERED"),
            new(PaymentMethodKind.Card, 5m, "CARD-RECOVERED", IdempotencyKey: "CARD_ATTEMPT:partial")
        };

        Assert.True(viewModel.TryApplyRecoveredPaymentProjection(true, tenders, "Complete the remaining refund methods."));

        Assert.Equal(tenders, viewModel.PaymentTenders);
        Assert.Equal("Complete the remaining refund methods.", viewModel.StatusMessage);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_recovery_projection_rolls_back_after_property_notification_failure()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146PROP", "Projection Property Tea", "930146PROP", PriceSourceKind.StoreRetailPrice, 10m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session);
        var originalTender = new PaymentTender(PaymentMethodKind.Cash, 1m, "ORIGINAL");
        var replacementTender = new PaymentTender(PaymentMethodKind.Card, 5m, "REPLACEMENT");
        viewModel.TryApplyRecoveredPaymentProjection(false, [originalTender], "Original status");
        var originalStatus = viewModel.StatusMessage;
        var failNotification = true;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (failNotification && args.PropertyName == nameof(PaymentViewModel.StatusMessage))
            {
                throw new InvalidOperationException("status subscriber failed");
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            viewModel.TryApplyRecoveredPaymentProjection(true, [replacementTender], "Replacement status"));

        Assert.Equal([originalTender], viewModel.PaymentTenders);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        failNotification = false;
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_recovery_projection_rolls_back_after_collection_notification_failure()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146COLL", "Projection Collection Tea", "930146COLL", PriceSourceKind.StoreRetailPrice, 10m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session);
        var originalTender = new PaymentTender(PaymentMethodKind.Voucher, 1m, "ORIGINAL-VOUCHER");
        var replacementTender = new PaymentTender(PaymentMethodKind.Cash, 2m, "REPLACEMENT-CASH");
        viewModel.TryApplyRecoveredPaymentProjection(false, [originalTender], "Original status");
        var originalStatus = viewModel.StatusMessage;
        viewModel.PaymentTenders.CollectionChanged += (_, _) =>
            throw new InvalidOperationException("tender collection subscriber failed");

        Assert.Throws<InvalidOperationException>(() =>
            viewModel.TryApplyRecoveredPaymentProjection(true, [replacementTender], "Replacement status"));

        Assert.Equal([originalTender], viewModel.PaymentTenders);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("so")]
    public void Payment_page_recovery_projection_propagates_fatal_notification(string failureKind)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146FATAL", "Projection Fatal Tea", "930146FATAL", PriceSourceKind.StoreRetailPrice, 10m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session);
        Exception fatal = failureKind == "oom"
            ? new OutOfMemoryException("fatal status subscriber")
            : new StackOverflowException("fatal status subscriber");
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PaymentViewModel.StatusMessage))
            {
                throw fatal;
            }
        };

        var thrown = Record.Exception(() =>
            viewModel.TryApplyRecoveredPaymentProjection(
                false,
                [new PaymentTender(PaymentMethodKind.Cash, 1m, "FATAL")],
                "Fatal status"));

        Assert.Same(fatal, thrown);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("sqlite-busy")]
    public async Task Approved_card_order_persistence_failure_keeps_lock_voucher_and_card_tender(string failureKind)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(
            "SKU-146C",
            "Approved Persistence Tea",
            "930146C",
            PriceSourceKind.StoreRetailPrice,
            10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, 2m, "VOUCHER-146C")
        };
        var openRecoveryCenterCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            openCardRecoveryCenter: () => openRecoveryCenterCalls++, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        viewModel.TenderAmountText = "2";
        viewModel.VoucherCodeText = "VOUCHER-146C";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucherTender = Assert.Single(viewModel.PaymentTenders);

        Exception persistenceFailure = failureKind == "io"
            ? new IOException("order save failed")
            : new SqliteException("database is locked", 5);
        workflow.TenderToAdd = new PaymentTender(PaymentMethodKind.Card, 8m, "CARD-APPROVED-146C");
        workflow.ThrowOnComplete = new CardPaymentPersistenceUnknownException(
            Guid.NewGuid(),
            "The card was approved, but POS could not safely save the order.",
            persistenceFailure);
        viewModel.TenderAmountText = "8";

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.IsCardPaymentRecoveryRequired);
        Assert.Null(viewModel.CardPaymentErrorOverlay);
        Assert.Equal(((CardPaymentPersistenceUnknownException)workflow.ThrowOnComplete).OrderGuid,
            viewModel.CreateCardPaymentHandoffRequest().RecoveryOrderGuid);
        Assert.True(viewModel.OpenCardRecoveryCenterCommand.CanExecute(null));
        viewModel.OpenCardRecoveryCenterCommand.Execute(null);
        Assert.Equal(1, openRecoveryCenterCalls);
        Assert.Equal("The card was approved, but POS could not safely save the order.", viewModel.StatusMessage);
        Assert.Null(completed);
        Assert.Single(cart.Lines);
        Assert.Equal(2, viewModel.PaymentTenders.Count);
        Assert.Contains(voucherTender, viewModel.PaymentTenders);
        var cardTender = Assert.Single(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Card);
        Assert.Empty(workflow.ReleasedVoucherTenders);
        Assert.False(viewModel.RemoveTenderCommand.CanExecute(cardTender));

        await viewModel.RemoveTenderCommand.ExecuteAsync(cardTender);
        var addTenderCallsAfterFailure = workflow.AddTenderCallCount;
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Contains(cardTender, viewModel.PaymentTenders);
        Assert.Contains(voucherTender, viewModel.PaymentTenders);
        Assert.Empty(workflow.ReleasedVoucherTenders);
        Assert.Equal(addTenderCallsAfterFailure, workflow.AddTenderCallCount);
        Assert.Equal(1, workflow.CompletePaymentCallCount);
    }

    [Fact]
    public void Payment_page_prepare_for_entry_keeps_current_tender_empty()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-147", "Blank Tender Tea", "930147", PriceSourceKind.StoreRetailPrice, 7.83m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session)
        {
            TenderAmountText = "3"
        };

        viewModel.PrepareForEntry(Session);

        Assert.Equal(string.Empty, viewModel.TenderAmountText);
        Assert.Equal(7.83m, viewModel.RemainingAmount);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_back_to_pos_command_invokes_callback()
    {
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            new FakeCashPaymentWorkflowService(),
            Session,
            onBackToPos: () => returnedToPos = true);

        viewModel.BackToPosCommand.Execute(null);

        Assert.True(returnedToPos);
    }

    [Fact]
    public async Task Payment_page_late_approved_card_after_cancellation_cannot_be_removed_or_leave_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-148", "Back Card Tea", "930148", PriceSourceKind.StoreRetailPrice, 7.83m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => returnedToPos = true);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        Assert.False(viewModel.BackToPosCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.BackToPosCommand.CanExecute(null));

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 7.83m, "CARD-LATE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.True(viewModel.BackToPosCommand.CanExecute(null));
        viewModel.BackToPosCommand.Execute(null);
        Assert.False(returnedToPos);
        Assert.Equal("payment.status.removeTendersBeforeBack", viewModel.StatusMessage);

        var approvedCardTender = Assert.Single(viewModel.PaymentTenders);
        Assert.False(viewModel.RemoveTenderCommand.CanExecute(approvedCardTender));
        viewModel.RemoveTenderCommand.Execute(approvedCardTender);
        viewModel.BackToPosCommand.Execute(null);
        Assert.False(returnedToPos);
        Assert.Same(approvedCardTender, Assert.Single(viewModel.PaymentTenders));
    }

    [Fact]
    public async Task Payment_page_cash_button_uses_australian_cash_rounding_when_amount_is_empty()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-147", "Rounded Cash Tea", "930147", PriceSourceKind.StoreRetailPrice, 7.83m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(7.85m, tender.Amount);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.Equal(0m, viewModel.ChangeDue);
    }

    [Fact]
    public async Task Payment_page_cash_button_adds_typed_amount_and_clears_input()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-148", "Typed Cash Tea", "930148", PriceSourceKind.StoreRetailPrice, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "10"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(10m, Assert.Single(viewModel.PaymentTenders).Amount);
        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_card_button_rejects_typed_overpay()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-149", "Card Overpay Tea", "930149", PriceSourceKind.StoreRetailPrice, 7.8m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-149"));
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "10"
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.cardExceedsRemaining", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_button_does_not_treat_cash_rounding_shortfall_as_paid()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-151", "Card Boundary Tea", "930151", PriceSourceKind.StoreRetailPrice, 7.82m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-151"));
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "7.80"
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(7.82m, viewModel.RemainingAmount);
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardMustBeFinalTender", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(10.49, "3", "7.49")]
    [InlineData(10.99, "3", "7.99")]
    [InlineData(10.01, "3", "7.01")]
    public async Task Payment_page_cash_then_card_pays_exact_remainder_when_total_is_not_multiple_of_five_cents(
        double total,
        string cashText,
        string cardText)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-152M", "Mixed Pay Tea", "930152M", PriceSourceKind.StoreRetailPrice, (decimal)total));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-152M"));
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = cashText
        };
        var completed = false;
        viewModel.PaymentCompleted += (_, _) => completed = true;
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        viewModel.TenderAmountText = cardText;
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotEqual("payment.status.cardMustBeFinalTender", viewModel.StatusMessage);
        Assert.NotEqual("payment.status.cardExceedsRemaining", viewModel.StatusMessage);
        Assert.True(completed, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_quick_cash_options_add_cash_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-150", "Quick Cash Tea", "930150", PriceSourceKind.StoreRetailPrice, 4.4m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);
        Assert.Equal([100m, 50m, 20m, 10m, 5m], viewModel.QuickCashAmounts.Select(option => option.Amount));
        var quickFive = Assert.Single(viewModel.QuickCashAmounts.Where(option => option.Amount == 5m));

        await viewModel.QuickCashCommand.ExecuteAsync(quickFive);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(5m, tender.Amount);
        Assert.Equal(0.6m, viewModel.ChangeDue);
        Assert.Equal("$5", quickFive.Label);
    }

    [Fact]
    public async Task Payment_page_allows_only_one_tender_per_payment_method_and_reenables_after_remove()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-152", "Single Method Tea", "930152", PriceSourceKind.StoreRetailPrice, 15m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "5"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var cashTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, cashTender.Method);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Single(viewModel.PaymentTenders);

        viewModel.RemoveTenderCommand.Execute(cashTender);

        Assert.Empty(viewModel.PaymentTenders);
        viewModel.TenderAmountText = "5";
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_blocks_voucher_after_cash_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153", "Mixed Single Method Tea", "930153", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Cash, 0m)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal("payment.status.paymentMethodOrder", viewModel.StatusMessage);
        Assert.Equal(1, workflow.AddTenderCallCount);
    }

    [Fact]
    public async Task Payment_page_allows_voucher_then_cash_then_final_card_and_completes_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153A", "Ordered Tender Tea", "930153A", PriceSourceKind.StoreRetailPrice, 20m));
        var completedOrder = CreateCompletedOrder(actualAmount: 20m);
        var workflow = new FakeCashPaymentWorkflowService
        {
            CompletePaymentResult = new CashPaymentWorkflowResult(completedOrder, 20m, 0m, 0, Session)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        PaymentCompletedEventArgs? completed = null;
        viewModel.PaymentCompleted += (_, args) => completed = args;

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153A";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "10";
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(completed);
        Assert.Equal(3, workflow.AddTenderCallCount);
        Assert.Equal(1, workflow.CompletePaymentCallCount);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Empty(cart.Lines);
        Assert.Equal("payment.status.completed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_releases_voucher_when_final_card_declines()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153B", "Declined Ordered Tender Tea", "930153B", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153B";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "DECLINED"));
        viewModel.TenderAmountText = "10";
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.DoesNotContain(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Cash);
        var released = Assert.Single(workflow.ReleasedVoucherTenders);
        Assert.Equal(PaymentMethodKind.Voucher, released.Method);
        Assert.Equal("payment.status.voucherReleased", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_releases_voucher_when_tender_is_removed()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153E", "Removed Voucher Tea", "930153E", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153E";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucherTender = Assert.Single(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);

        viewModel.RemoveTenderCommand.Execute(voucherTender);

        Assert.DoesNotContain(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Same(voucherTender, Assert.Single(workflow.ReleasedVoucherTenders));
        Assert.Equal("payment.status.voucherReleased", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_keeps_voucher_when_remove_release_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153F", "Remove Release Failed Tea", "930153F", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            ReleaseVoucherTenderResult = false
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153F";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucherTender = Assert.Single(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);

        viewModel.RemoveTenderCommand.Execute(voucherTender);

        Assert.Same(voucherTender, Assert.Single(viewModel.PaymentTenders));
        Assert.Same(voucherTender, Assert.Single(workflow.ReleasedVoucherTenders));
        Assert.Equal("payment.status.voucherReleaseFailed", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_voucher_remove_propagates_fatal_release_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal manual voucher release")
            : new StackOverflowException("fatal manual voucher release");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153F-FATAL", "Fatal Voucher Remove Tea", "930153FFATAL", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153F-FATAL";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucherTender = Assert.Single(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        workflow.ThrowOnReleaseVoucherTender = fatal;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => viewModel.RemoveTenderCommand.ExecuteAsync(voucherTender));

        Assert.Same(fatal, thrown);
        Assert.Same(voucherTender, Assert.Single(viewModel.PaymentTenders));
    }

    [Fact]
    public async Task Payment_page_keeps_voucher_when_release_after_card_decline_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153D", "Release Failed Ordered Tender Tea", "930153D", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            ReleaseVoucherTenderResult = false
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153D";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "DECLINED"));
        viewModel.TenderAmountText = "10";
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Cash);
        Assert.Equal("payment.status.voucherReleaseFailed", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_card_decline_voucher_release_propagates_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal automatic voucher release")
            : new StackOverflowException("fatal automatic voucher release");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153D-FATAL", "Fatal Decline Release Tea", "930153DFATAL", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153D-FATAL";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        workflow.ThrowOnReleaseVoucherTender = fatal;
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail("payment.status.cardDeclined", "DECLINED"));
        viewModel.TenderAmountText = "10";

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => viewModel.SelectCardCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Cash);
    }

    [Fact]
    public async Task Payment_page_keeps_voucher_when_final_card_result_is_unknown()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153C", "Unknown Ordered Tender Tea", "930153C", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153C";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "Result unknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        viewModel.TenderAmountText = "10";
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Empty(workflow.ReleasedVoucherTenders);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Equal("Result unknown", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_unknown_card_error_does_not_lock_the_next_new_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153C-OLD", "Unknown Old Order Tea", "930153COLD", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "The previous card result requires supervisor reconciliation.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsPaymentInteractionLocked);

        cart.Clear();
        cart.AddItem(CreateItem("SKU-153C-NEW", "Next Order Tea", "930153CNEW", PriceSourceKind.StoreRetailPrice, 12m));
        viewModel.PrepareForEntry(Session);

        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.True(viewModel.BackToPosCommand.CanExecute(null));
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public void Payment_page_alternative_refund_policy_does_not_block_the_next_new_sale()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-ALT-OLD",
            null,
            "Previous Alternative Refund Tea",
            "930ALTOLD",
            "ITEM-ALT-OLD",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-ALT-OLD",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        viewModel.SetAlternativeRefundMethodRequired(true);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));

        // 模拟收银员放弃旧退款、回到 POS 后直接建立下一笔销售；中间没有付款页 RefreshCart。
        cart.Clear();
        cart.AddItem(CreateItem(
            "SKU-ALT-NEW",
            "Next Sale Tea",
            "930ALTNEW",
            PriceSourceKind.StoreRetailPrice,
            12m));
        viewModel.PrepareForEntry(Session);

        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.True(viewModel.BackToPosCommand.CanExecute(null));
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public void Payment_page_hides_cancel_entry_and_does_not_raise_navigation_cancel_during_regular_entry()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153", "Regular Tea", "930153", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.False(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.IsCancelPaymentVisible);
    }

    [Fact]
    public async Task Payment_page_locks_interactions_while_card_payment_is_pending()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-154", "Pending Card Tea", "930154", PriceSourceKind.StoreRetailPrice, 15m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        Assert.True(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.IsCancelPaymentVisible);
        Assert.False(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.False(viewModel.QuickCashCommand.CanExecute(viewModel.QuickCashAmounts[0]));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardProcessing", viewModel.StatusMessage);

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-154"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.Single(viewModel.PaymentTenders);
    }

    [Fact]
    public async Task Payment_page_shutdown_cancels_active_card_and_keeps_interactions_locked()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SHUTDOWN", "Shutdown Card Tea", "930SHUTDOWN", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        _ = viewModel.BeginShutdown();
        _ = viewModel.BeginShutdown();
        await paymentTask;

        Assert.True(workflow.LastAddTenderCancellationToken.IsCancellationRequested);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsPaymentInteractionEnabled);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.Empty(viewModel.PaymentTenders);
    }

    [Fact]
    public async Task Payment_page_shutdown_and_dispose_do_not_block_on_cancellation_callback()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SHUTDOWN-BLOCK", "Shutdown Card Tea", "930SHUTDOWNBLOCK", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = workflow.LastAddTenderCancellationToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            try
            {
                Thread.Sleep(100);
            }
            finally
            {
                callbackCompleted.TrySetResult();
            }
        });

        var beginStartedAt = Stopwatch.GetTimestamp();
        _ = viewModel.BeginShutdown();
        Assert.True(Stopwatch.GetElapsedTime(beginStartedAt) < TimeSpan.FromSeconds(1));
        await callbackStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var disposeStartedAt = Stopwatch.GetTimestamp();
        viewModel.Dispose();
        Assert.True(Stopwatch.GetElapsedTime(disposeStartedAt) < TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsPaymentInteractionLocked);

        await callbackCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await paymentTask;
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_shutdown_cancellation_task_propagates_fatal_callback_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal shutdown cancellation callback")
            : new StackOverflowException("fatal shutdown cancellation callback");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SHUTDOWN-FATAL", "Fatal Shutdown Tea", "930SHUTDOWNFATAL", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        using var registration = workflow.LastAddTenderCancellationToken.Register(() => throw fatal);
        var shutdownCancellationTask = viewModel.BeginShutdown();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => shutdownCancellationTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));

        Assert.Same(fatal, thrown);
        Assert.True(workflow.LastAddTenderCancellationToken.IsCancellationRequested);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        await paymentTask;
    }

    [Fact]
    public async Task Payment_page_dispose_keeps_active_card_token_until_workflow_finally()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SHUTDOWN-LIFETIME", "Shutdown Card Tea", "930SHUTDOWNLIFETIME", PriceSourceKind.StoreRetailPrice, 10m));
        var tokenWasUsableAfterWorkflowRelease = false;
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true,
            AfterAddTenderResult = cancellationToken =>
            {
                using var registration = cancellationToken.Register(static () => { });
                tokenWasUsableAfterWorkflowRelease = true;
            }
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        var cardSession = typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var activeProperty = cardSession.GetType().GetProperty(
            "ActiveCardPaymentCts",
            BindingFlags.Instance | BindingFlags.Public)!;
        var activeCancellation = Assert.IsType<CancellationTokenSource>(
            activeProperty.GetValue(cardSession));

        _ = viewModel.BeginShutdown();
        var shutdownCancellationTask = Assert.IsAssignableFrom<Task>(
            cardSession.GetType()
                .GetField("_shutdownCancellationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(cardSession));
        await shutdownCancellationTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(activeCancellation.IsCancellationRequested);
        Assert.False(paymentTask.IsCompleted);

        viewModel.Dispose();

        Assert.Same(activeCancellation, activeProperty.GetValue(cardSession));
        using (activeCancellation.Token.Register(static () => { }))
        {
        }
        Assert.False(paymentTask.IsCompleted);

        workflow.AddTenderResult.SetResult(
            PaymentTenderAttemptResult.Fail("payment.status.cardCancelled"));
        await paymentTask;

        Assert.True(tokenWasUsableAfterWorkflowRelease);
        Assert.Null(workflow.AfterAddTenderResultException);
        Assert.Null(activeProperty.GetValue(cardSession));
        Assert.Throws<ObjectDisposedException>(
            () => activeCancellation.Token.Register(static () => { }));
        Assert.Null(Record.Exception(viewModel.Dispose));
    }

    [Fact]
    public async Task Payment_page_manual_card_cancel_restores_interactions_without_adding_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-155", "Cancelled Card Tea", "930155", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_keeps_late_card_approval_when_second_cancel_is_not_requested()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-156", "Late Approval Tea", "930156", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);

        Assert.True(viewModel.IsCancelPaymentVisible);
        Assert.False(viewModel.CancelCommand.CanExecute(null));

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-LATE-APPROVED"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.Equal("payment.status.cardTenderAdded", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_second_cancel_cannot_discard_late_card_approval()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-157", "Late Card Tea", "930157", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.IsCancelPaymentVisible);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-LATE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.Equal("payment.status.cardTenderAdded", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_ignores_late_card_approval_from_previous_entry()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-161", "Stale Card Tea", "930161", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        var nextSession = Session with { DeviceCode = "POS-02" };
        viewModel.PrepareForEntry(nextSession);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-STALE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(nextSession, viewModel.Session);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_session_notification_failure_still_invalidates_previous_card_task()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-161-NOTIFY", "Stale Notification Tea", "930161NOTIFY", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PaymentViewModel.Session))
            {
                throw new InvalidOperationException("payment session subscriber failed");
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            viewModel.PrepareForEntry(Session with { DeviceCode = "POS-02" }));
        var cancellationRequested = workflow.LastAddTenderCancellationToken.IsCancellationRequested;
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-STALE-NOTIFICATION"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Equal("payment session subscriber failed", exception.Message);
        Assert.True(cancellationRequested);
        Assert.Empty(viewModel.PaymentTenders);
    }

    [Fact]
    public void Payment_page_prepare_for_entry_keeps_unknown_result_lock_for_same_cart_transaction()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-RECOVERY-LOCK", "Recovery Lock Tea", "930RECOVERYLOCK", PriceSourceKind.StoreRetailPrice, 10m));
        var viewModel = new PaymentViewModel(cart, new FakeCashPaymentWorkflowService(), Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.SetCurrentCardRecoveryRequired(true, "Previous card result is unknown.");
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.False(viewModel.BackToPosCommand.CanExecute(null));
        Assert.Equal("Previous card result is unknown.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_unresolved_recovery_overlay_survives_return_from_recovery_center()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-RECOVERY-NAV", "Recovery Navigation Tea", "930RECOVERYNAV", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Previous card result is unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var openCenterCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            openCardRecoveryCenter: () => openCenterCalls++);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, openCenterCalls);
        Assert.True(viewModel.CardPaymentErrorOverlay?.IsOpen);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_clearing_unknown_does_not_close_fallback_or_regular_error_overlay()
    {
        var fallbackCoordinator = new LinklyFallbackPromptCoordinator();
        var fallbackViewModel = new PaymentViewModel(
            new PosCartService(),
            new FakeCashPaymentWorkflowService(),
            Session,
            linklyFallbackPromptCoordinator: fallbackCoordinator);
        var promptTask = fallbackCoordinator.ConfirmFallbackAsync(
            new LinklyFallbackPromptRequest(
                LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionMode.CloudDirectSync,
                "Backend API is offline.",
                [LinklyConnectionMode.CloudBackendAsync.ToString()]));
        await WaitUntilAsync(() => fallbackViewModel.CardPaymentErrorOverlay is { IsOpen: true });

        fallbackViewModel.SetCurrentCardRecoveryRequired(false);

        Assert.Equal(
            CardPaymentErrorOverlayPrimaryActionKind.ConfirmFallback,
            fallbackViewModel.CardPaymentErrorOverlay?.PrimaryActionKind);
        Assert.True(fallbackViewModel.CardPaymentErrorOverlay?.IsOpen);
        fallbackViewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);
        Assert.False(await promptTask);

        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.cloud.communicationFailed",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.CloudCommunicationFailed,
                PreserveStatus: true)));
        var regularViewModel = new PaymentViewModel(new PosCartService(), workflow, Session);
        await regularViewModel.SelectCardCommand.ExecuteAsync(null);

        var regularOverlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(regularViewModel.CardPaymentErrorOverlay);
        regularViewModel.SetCurrentCardRecoveryRequired(false);

        Assert.Same(regularOverlay, regularViewModel.CardPaymentErrorOverlay);
        Assert.True(regularOverlay.IsOpen);
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_after_second_cancel_allows_new_tender_before_late_card_result()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-162", "Reentry Tea", "930162", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-LATE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_stale_card_cancel_after_reentry_does_not_release_current_voucher()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-162V", "Reentry Voucher Tea", "930162V", PriceSourceKind.StoreRetailPrice, 10m));
        var staleCardResult = new TaskCompletionSource<PaymentTenderAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = staleCardResult,
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.PrepareForEntry(Session);
        workflow.AddTenderStarted = null;
        workflow.AddTenderResult = null;
        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-REENTRY";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        staleCardResult.SetException(new OperationCanceledException());
        await paymentTask;

        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.Empty(workflow.ReleasedVoucherTenders);
    }

    [Fact]
    public async Task Payment_page_manual_card_cancel_shows_unconfirmed_cancel_outcome()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-159", "Unconfirmed Cancel Tea", "930159", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "ANZ Linkly cancellation outcome could not be confirmed."));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("ANZ Linkly cancellation outcome could not be confirmed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_terminal_cancel_result_does_not_add_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160", "Terminal Cancel Tea", "930160", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "CANCELLED (C0)",
            CardResult: new CardPaymentResultDisposition(CardPaymentTerminalOutcome.Cancelled)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_linkly_backend_cancel_result_allows_next_card_payment_without_recovery_prompt()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160L", "Linkly Cancel Tea", "930160L", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var recoverCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () =>
            {
                recoverCallCount++;
                return Task.FromResult(false);
            });

        var cancelledPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.cancelled",
            StatusMessage: "ANZ Linkly Cloud transaction was cancelled.",
            CardResult: new CardPaymentResultDisposition(CardPaymentTerminalOutcome.Cancelled)));
        await cancelledPaymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.Null(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);

        workflow.AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-AFTER-CANCEL"),
            "payment.status.cardTenderAdded"));
        await retryPaymentTask;

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.Equal("CARD-AFTER-CANCEL", tender.Reference);
        Assert.Equal(0, recoverCallCount);
        Assert.False(viewModel.CardPaymentErrorOverlay?.IsOpen == true);
        Assert.Equal("payment.status.cardTenderAdded", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_virtual_linkly_cancel_result_allows_next_card_payment_without_recovery_prompt()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160V", "Virtual Linkly Cancel Tea", "930160V", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var recoverCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () =>
            {
                recoverCallCount++;
                return Task.FromResult(false);
            });

        var cancelledPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "CANCELLED (CN)",
            CardResult: new CardPaymentResultDisposition(CardPaymentTerminalOutcome.Cancelled)));
        await cancelledPaymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.Null(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);

        workflow.AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-AFTER-VIRTUAL-CANCEL"),
            "payment.status.cardTenderAdded"));
        await retryPaymentTask;

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.Equal("CARD-AFTER-VIRTUAL-CANCEL", tender.Reference);
        Assert.Equal(0, recoverCallCount);
        Assert.False(viewModel.CardPaymentErrorOverlay?.IsOpen == true);
        Assert.Equal("payment.status.cardTenderAdded", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_square_card_cancel_result_uses_cancelled_status()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160A", "Square Terminal Cancel Tea", "930160A", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "Square checkout was canceled.",
            CardResult: new CardPaymentResultDisposition(CardPaymentTerminalOutcome.Cancelled)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("payment.card.squareCanceled", "Square checkout was canceled.")]
    [InlineData("payment.card.squareCanceledBuyer", "Square checkout was canceled by the buyer. Ask the customer to try again or choose another payment method.")]
    [InlineData("payment.card.squareCanceledSeller", "Square checkout was canceled. Please start the card payment again.")]
    public async Task Payment_page_square_cancel_status_keeps_friendly_message_without_overlay(
        string statusKey,
        string statusMessage)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160SQ-CANCEL", "Square Friendly Cancel Tea", "930160SQ", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            statusKey,
            StatusMessage: statusMessage,
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.Cancelled,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.CardPaymentErrorOverlay is null or { IsOpen: false });
        Assert.Equal(statusMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_square_timeout_status_opens_timeout_overlay()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160SQ-TIMEOUT", "Square Timeout Tea", "930160ST", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.squareTimedOut",
            StatusMessage: "Square checkout timed out before the customer completed payment.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.TimedOut,
                CardPaymentErrorKind.Timeout,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Equal("payment.card.error.overlay.timeout.title", viewModel.CardPaymentErrorOverlay.TitleKey);
        Assert.Equal("Square checkout timed out before the customer completed payment.", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("payment.card.squareTerminalOffline", "Square terminal is offline. Check the terminal network and try again.")]
    [InlineData("payment.card.squareTerminalNotPickedUp", "Square terminal did not pick up the checkout. Check that the terminal is online, then try again.")]
    public async Task Payment_page_square_terminal_connectivity_status_opens_square_overlay(
        string statusKey,
        string statusMessage)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160SQ-OFFLINE", "Square Offline Tea", "930160SO", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            statusKey,
            StatusMessage: statusMessage,
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.SquareCommunicationFailed,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Equal("payment.card.error.overlay.squareCommunicationFailed.title", viewModel.CardPaymentErrorOverlay.TitleKey);
        Assert.Equal(statusMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_failure_result_restores_buttons_without_adding_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160B", "Declined Card Tea", "930160B", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "DECLINED (55)",
            IsTerminalDecline: true,
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.CardDeclined,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Equal(
            "payment.card.error.overlay.cardDeclined.title",
            viewModel.CardPaymentErrorOverlay.TitleKey);
        Assert.Contains(
            "DECLINED (55)",
            viewModel.CardPaymentErrorOverlay.DisplayMessage,
            StringComparison.Ordinal);
        Assert.Equal("DECLINED (55)", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_local_card_decline_status_does_not_show_bank_decline_overlay()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160C", "Local Declined Card Tea", "930160C", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "Original card payment reference is required."));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.CardPaymentErrorOverlay is null or { IsOpen: false });
        Assert.Equal("Original card payment reference is required.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_timeout_exception_restores_interactions_without_adding_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-156", "Timed Out Card Tea", "930156", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderException = new OperationCanceledException()
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardTimedOut", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_timeout_failure_result_uses_timeout_status()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158", "Timeout Result Card Tea", "930158", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "ANZ Linkly transaction timed out.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.TimedOut,
                CardPaymentErrorKind.Timeout)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.True(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardTimedOut", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_result_unknown_without_persisted_handoff_opens_recovery_center()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158U", "Unknown Result Card Tea", "930158U", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is unknown. Recover the previous transaction before charging again.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var recoverCallCount = 0;
        var openCenterCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () =>
            {
                recoverCallCount++;
                return Task.FromResult(true);
            },
            openCardRecoveryCenter: () => openCenterCalls++, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsCancelPaymentVisible);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.True(viewModel.CardPaymentErrorOverlay.HasPrimaryAction);
        Assert.Equal(
            CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious,
            viewModel.CardPaymentErrorOverlay.PrimaryActionKind);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.openRecoveryCenter",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(0, recoverCallCount);
        Assert.Equal(1, openCenterCalls);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_card_result_unknown_does_not_invoke_legacy_recovery_callback()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V", "Unresolved Recovery Tea", "930158V", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is still unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var recoverCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () =>
            {
                recoverCallCount++;
                return Task.FromResult(true);
            });

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(0, recoverCallCount);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);
        Assert.False(viewModel.CardPaymentErrorOverlay.IsOpen);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_null_handoff_qualification_exposes_enabled_recovery_center_action()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V2", "Unqualified Recovery Tea", "930158V2", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000157");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is still unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000157")));
        var prepareCalls = 0;
        var openCenterCalls = 0;
        var handoffCalls = 0;
        var qualificationCompleted = false;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () => Task.FromResult(true),
            prepareCardPaymentHandoffAsync: _ =>
            {
                prepareCalls++;
                qualificationCompleted = true;
                return Task.FromResult<CardPaymentHandoffCandidate?>(null);
            },
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return Task.FromResult(true);
            },
            openCardRecoveryCenter: () => openCenterCalls++);
        var notificationStates = new List<(bool HasAction, string TextKey, bool CanExecute)>();
        viewModel.CardPaymentErrorPrimaryActionCommand.CanExecuteChanged += (_, _) =>
        {
            if (qualificationCompleted && viewModel.CardPaymentErrorOverlay is { } overlay)
            {
                notificationStates.Add((
                    overlay.HasPrimaryAction,
                    overlay.PrimaryButtonTextKey,
                    viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null)));
            }
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.openRecoveryCenter",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
        Assert.Equal(1, prepareCalls);
        Assert.NotEmpty(notificationStates);
        Assert.All(notificationStates, state =>
        {
            Assert.True(state.HasAction);
            Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", state.TextKey);
            Assert.True(state.CanExecute);
        });

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, openCenterCalls);
        Assert.Equal(0, handoffCalls);
    }

    [Fact]
    public async Task Payment_page_matching_persisted_handoff_enables_primary_action_immediately_after_overlay()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V3", "Qualified Recovery Tea", "930158V3", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000158")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000158")));
        var candidate = new CardPaymentHandoffCandidate(
            CardProcessorKind.Linkly,
            Guid.Parse("10000000-0000-0000-0000-000000000158"));
        CardPaymentHandoffRequest? preparedRequest = null;
        var qualificationCompleted = false;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: request =>
            {
                preparedRequest = request;
                qualificationCompleted = true;
                return Task.FromResult<CardPaymentHandoffCandidate?>(candidate);
            },
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true));
        var notificationStates = new List<(bool HasAction, string TextKey, bool CanExecute)>();
        viewModel.CardPaymentErrorPrimaryActionCommand.CanExecuteChanged += (_, _) =>
        {
            if (qualificationCompleted && viewModel.CardPaymentErrorOverlay is { } overlay)
            {
                notificationStates.Add((
                    overlay.HasPrimaryAction,
                    overlay.PrimaryButtonTextKey,
                    viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null)));
            }
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.handoff",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.NotEmpty(notificationStates);
        Assert.All(notificationStates, state =>
        {
            Assert.True(state.HasAction);
            Assert.Equal("payment.card.error.overlay.activeSession.handoff", state.TextKey);
            Assert.True(state.CanExecute);
        });
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Linkly, candidate.AttemptGuid),
            preparedRequest?.RecoveryAttemptKey);
        Assert.Equal(
            Guid.Parse("20000000-0000-0000-0000-000000000158"),
            preparedRequest?.RecoveryOrderGuid);

        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);

        Assert.False(viewModel.CardPaymentErrorOverlay.IsOpen);
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_matching_persisted_handoff_keeps_action_disabled_until_qualification_finishes()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V-PENDING", "Pending Qualification Tea", "930158VPENDING", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000162");
        var orderGuid = Guid.Parse("20000000-0000-0000-0000-000000000162");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: orderGuid));
        var qualificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualificationResult = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var handoffCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ =>
            {
                qualificationStarted.SetResult(true);
                return qualificationResult.Task;
            },
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return Task.FromResult(true);
            },
            openCardRecoveryCenter: () => { });

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await qualificationStarted.Task;

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.False(viewModel.CardPaymentErrorOverlay.HasPrimaryAction);
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(0, handoffCalls);

        qualificationResult.SetResult(candidate);
        await paymentTask;

        Assert.True(viewModel.CardPaymentErrorOverlay.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.handoff",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, handoffCalls);
    }

    [Fact]
    public async Task Payment_page_closed_pending_handoff_overlay_ignores_late_candidate()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-QUAL-CLOSE-CANDIDATE", "Closed Candidate Tea", "930QUALCLOSECANDIDATE", PriceSourceKind.StoreRetailPrice, 10m));
        var existingTender = new PaymentTender(PaymentMethodKind.Cash, 2m);
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000174");
        var orderGuid = Guid.Parse("20000000-0000-0000-0000-000000000174");
        var recoveryKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid);
        var qualificationRequests = 0;
        var qualificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualificationResult = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCenterCalls = 0;
        var handoffCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            prepareCardPaymentHandoffAsync: _ =>
            {
                qualificationStarted.TrySetResult(true);
                // 关闭后的入口会重新查询；旧请求的迟到结果不得成为新请求的结果。
                if (++qualificationRequests > 1)
                {
                    return Task.FromResult<CardPaymentHandoffCandidate?>(null);
                }
                return qualificationResult.Task;
            },
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return Task.FromResult(true);
            },
            openCardRecoveryCenter: () => openCenterCalls++);
        viewModel.PaymentTenders.Add(existingTender);
        var cardSession = Assert.IsType<CardPaymentSession>(typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel));
        var handlingTask = cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Closed candidate qualification is pending.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: recoveryKey,
            RecoveryOrderGuid: orderGuid));
        await qualificationStarted.Task;
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);
        var originalStatus = viewModel.StatusMessage;

        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);

        Assert.False(overlay.IsOpen);
        Assert.True(overlay.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.openRecoveryCenter",
            overlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, openCenterCalls);
        Assert.Equal(0, handoffCalls);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(recoveryKey, cardSession.RecoveryAttemptKey);
        Assert.Equal(orderGuid, cardSession.RecoveryOrderGuid);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
        Assert.Equal(existingTender, Assert.Single(viewModel.PaymentTenders));

        qualificationResult.SetResult(new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid));
        await handlingTask;

        Assert.False(overlay.IsOpen);
        Assert.True(overlay.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", overlay.PrimaryButtonTextKey);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(recoveryKey, cardSession.RecoveryAttemptKey);
        Assert.Equal(orderGuid, cardSession.RecoveryOrderGuid);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
        Assert.Equal(existingTender, Assert.Single(viewModel.PaymentTenders));
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.Equal(1, openCenterCalls);
        Assert.Equal(0, handoffCalls);
    }

    [Fact]
    public async Task Payment_page_closed_pending_handoff_overlay_ignores_late_qualification_exception()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-QUAL-CLOSE-ERROR", "Closed Error Tea", "930QUALCLOSEERROR", PriceSourceKind.StoreRetailPrice, 10m));
        var existingTender = new PaymentTender(PaymentMethodKind.Cash, 3m);
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000175");
        var orderGuid = Guid.Parse("20000000-0000-0000-0000-000000000175");
        var recoveryKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid);
        var qualificationRequests = 0;
        var qualificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualificationResult = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCenterCalls = 0;
        var handoffCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            prepareCardPaymentHandoffAsync: _ =>
            {
                qualificationStarted.TrySetResult(true);
                // 关闭后的入口会重新查询；旧请求的迟到异常只属于旧一代资格。
                if (++qualificationRequests > 1)
                {
                    return Task.FromResult<CardPaymentHandoffCandidate?>(null);
                }
                return qualificationResult.Task;
            },
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return Task.FromResult(true);
            },
            openCardRecoveryCenter: () => openCenterCalls++);
        viewModel.PaymentTenders.Add(existingTender);
        var cardSession = Assert.IsType<CardPaymentSession>(typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel));
        var handlingTask = cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Closed error qualification is pending.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: recoveryKey,
            RecoveryOrderGuid: orderGuid));
        await qualificationStarted.Task;
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);
        var originalStatus = viewModel.StatusMessage;

        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);

        Assert.False(overlay.IsOpen);
        Assert.True(overlay.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.openRecoveryCenter",
            overlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, openCenterCalls);
        Assert.Equal(0, handoffCalls);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(recoveryKey, cardSession.RecoveryAttemptKey);
        Assert.Equal(orderGuid, cardSession.RecoveryOrderGuid);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
        Assert.Equal(existingTender, Assert.Single(viewModel.PaymentTenders));

        viewModel.SetStatus("payment.status.ready", "Status changed after the overlay closed.");
        var statusAfterClose = viewModel.StatusMessage;
        qualificationResult.SetException(new IOException("simulated late qualification failure"));
        await handlingTask;

        Assert.False(overlay.IsOpen);
        Assert.True(overlay.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", overlay.PrimaryButtonTextKey);
        Assert.Equal(statusAfterClose, viewModel.StatusMessage);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(recoveryKey, cardSession.RecoveryAttemptKey);
        Assert.Equal(orderGuid, cardSession.RecoveryOrderGuid);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
        Assert.Equal(existingTender, Assert.Single(viewModel.PaymentTenders));
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.Equal(1, openCenterCalls);
        Assert.Equal(0, handoffCalls);
    }

    [Fact]
    public async Task Payment_page_late_handoff_qualification_does_not_revive_cleared_unknown_state()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-QUAL-CLEARED", "Cleared Qualification Tea", "930QUALCLEARED", PriceSourceKind.StoreRetailPrice, 10m));
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000170");
        var orderGuid = Guid.Parse("20000000-0000-0000-0000-000000000170");
        var qualificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualificationResult = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            prepareCardPaymentHandoffAsync: _ =>
            {
                qualificationStarted.TrySetResult(true);
                return qualificationResult.Task;
            },
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true),
            openCardRecoveryCenter: () => { });
        var cardSession = Assert.IsType<CardPaymentSession>(typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel));
        var handlingTask = cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Qualification A is pending.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: orderGuid));
        await qualificationStarted.Task;
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);

        viewModel.SetCurrentCardRecoveryRequired(false, "Payment is ready again.");
        var clearedStatus = viewModel.StatusMessage;
        qualificationResult.SetResult(new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid));
        await handlingTask;

        Assert.False(overlay.IsOpen);
        Assert.False(overlay.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", overlay.PrimaryButtonTextKey);
        Assert.Equal(clearedStatus, viewModel.StatusMessage);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_older_handoff_qualification_cannot_overwrite_new_pending_generation()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-QUAL-GENERATION", "Generation Qualification Tea", "930QUALGEN", PriceSourceKind.StoreRetailPrice, 10m));
        var attemptA = Guid.Parse("10000000-0000-0000-0000-000000000171");
        var attemptB = Guid.Parse("10000000-0000-0000-0000-000000000172");
        var orderA = Guid.Parse("20000000-0000-0000-0000-000000000171");
        var orderB = Guid.Parse("20000000-0000-0000-0000-000000000172");
        var startedA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultA = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultB = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            prepareCardPaymentHandoffAsync: request =>
            {
                if (request.RecoveryAttemptKey is { AttemptGuid: var requestAttemptGuid } &&
                    requestAttemptGuid == attemptA)
                {
                    startedA.TrySetResult(true);
                    return resultA.Task;
                }

                startedB.TrySetResult(true);
                return resultB.Task;
            },
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true),
            openCardRecoveryCenter: () => { });
        var cardSession = Assert.IsType<CardPaymentSession>(typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel));
        var handlingA = cardSession.TryHandleFailedResultAsync(CreateUnknownResult(attemptA, orderA, "Qualification A is pending."));
        await startedA.Task;
        var overlayA = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);

        var handlingB = cardSession.TryHandleFailedResultAsync(CreateUnknownResult(attemptB, orderB, "Qualification B is pending."));
        await startedB.Task;
        var overlayB = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);
        Assert.NotSame(overlayA, overlayB);

        resultA.SetResult(new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptA));
        await handlingA;

        Assert.True(overlayB.IsOpen);
        Assert.False(overlayB.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", overlayB.PrimaryButtonTextKey);
        Assert.Equal("Qualification B is pending.", viewModel.StatusMessage);
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        resultB.SetResult(new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptB));
        await handlingB;

        Assert.True(overlayB.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.handoff", overlayB.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        static PaymentTenderAttemptResult CreateUnknownResult(Guid attemptGuid, Guid orderGuid, string message)
        {
            return new PaymentTenderAttemptResult(
                false,
                "payment.card.resultUnknown",
                StatusMessage: message,
                CardResult: new CardPaymentResultDisposition(
                    CardPaymentTerminalOutcome.ResultUnknown,
                    CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                    PreserveStatus: true),
                RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
                RecoveryOrderGuid: orderGuid);
        }
    }

    [Fact]
    public async Task Payment_page_shutdown_invalidates_pending_handoff_qualification()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-QUAL-SHUTDOWN", "Shutdown Qualification Tea", "930QUALSHUTDOWN", PriceSourceKind.StoreRetailPrice, 10m));
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000173");
        var qualificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualificationResult = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            prepareCardPaymentHandoffAsync: _ =>
            {
                qualificationStarted.TrySetResult(true);
                return qualificationResult.Task;
            },
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true),
            openCardRecoveryCenter: () => { });
        var cardSession = Assert.IsType<CardPaymentSession>(typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel));
        var handlingTask = cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Shutdown qualification is pending.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000173")));
        await qualificationStarted.Task;
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(viewModel.CardPaymentErrorOverlay);

        viewModel.BeginShutdown();
        qualificationResult.SetResult(new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid));
        await handlingTask;

        Assert.True(overlay.IsOpen);
        Assert.False(overlay.HasPrimaryAction);
        Assert.Equal("payment.card.error.overlay.activeSession.openRecoveryCenter", overlay.PrimaryButtonTextKey);
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_exposes_open_recovery_center_navigation_contract()
    {
        var opened = false;
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            new FakeCashPaymentWorkflowService(),
            Session,
            openCardRecoveryCenter: () => opened = true);

        Assert.True(viewModel.OpenCardRecoveryCenterCommand.CanExecute(null));
        viewModel.OpenCardRecoveryCenterCommand.Execute(null);

        Assert.True(opened);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Payment_page_open_recovery_after_closing_overlay_requires_successful_handoff(
        bool handoffSucceeds,
        bool usePrimaryAction)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("RECOVERY-ENTRY", "Recovery Entry Tea", "930ENTRY", PriceSourceKind.StoreRetailPrice, 10m));
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, Guid.NewGuid());
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: attemptKey,
            RecoveryOrderGuid: Guid.NewGuid()));
        var navigation = new List<string>();
        var candidate = new CardPaymentHandoffCandidate(attemptKey.Processor, attemptKey.AttemptGuid);
        using var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => navigation.Add("pos"),
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(handoffSucceeds),
            openCardRecoveryCenter: () => navigation.Add(cart.IsEmpty ? "recovery-empty" : "recovery-occupied"));

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);

        await (usePrimaryAction
            ? viewModel.CardPaymentErrorPrimaryActionCommand
            : viewModel.OpenCardRecoveryCenterCommand).ExecuteAsync(null);

        if (handoffSucceeds)
        {
            Assert.Equal(new[] { "pos", "recovery-empty" }, navigation);
            Assert.Empty(cart.Lines);
            Assert.False(viewModel.IsCardPaymentRecoveryRequired);
        }
        else
        {
            Assert.Empty(navigation);
            Assert.Single(cart.Lines);
            Assert.True(viewModel.IsCardPaymentRecoveryRequired);
            Assert.True(viewModel.IsPaymentInteractionLocked);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Payment_page_open_recovery_serializes_requalification_and_handoff(bool delayQualification)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("RECOVERY-ENTRY-WAIT", "Recovery Entry Tea", "930ENTRYWAIT", PriceSourceKind.StoreRetailPrice, 10m));
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, Guid.NewGuid());
        var candidate = new CardPaymentHandoffCandidate(attemptKey.Processor, attemptKey.AttemptGuid);
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: attemptKey,
            RecoveryOrderGuid: Guid.NewGuid()));
        var qualification = new TaskCompletionSource<CardPaymentHandoffCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareCalls = 0;
        var handoffCalls = 0;
        var centerCalls = 0;
        using var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => ++prepareCalls > 1 && delayQualification
                ? qualification.Task
                : Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return delayQualification ? Task.FromResult(true) : handoff.Task;
            },
            openCardRecoveryCenter: () => centerCalls++);
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);

        var opening = viewModel.OpenCardRecoveryCenterCommand.ExecuteAsync(null);
        Assert.False(viewModel.OpenCardRecoveryCenterCommand.CanExecute(null));
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        // 两个入口即便在检查与执行间交错，也不得重复移交或提前打开中心。
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        await viewModel.OpenCardRecoveryCenterCommand.ExecuteAsync(null);
        Assert.Equal(0, centerCalls);

        qualification.TrySetResult(candidate);
        handoff.TrySetResult(true);
        await opening.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(2, prepareCalls);
        Assert.Equal(1, handoffCalls);
        Assert.Equal(1, centerCalls);
        Assert.Empty(cart.Lines);
        Assert.False(viewModel.IsCardPaymentRecoveryRequired);
    }

    [Fact]
    public async Task Payment_page_confirmed_handoff_clears_only_current_ui_and_navigates_to_pos()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V4", "Handoff Recovery Tea", "930158V4", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000159")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000159")));
        var candidate = new CardPaymentHandoffCandidate(
            CardProcessorKind.Linkly,
            Guid.Parse("10000000-0000-0000-0000-000000000159"));
        CardPaymentHandoffCandidate? handedOffCandidate = null;
        var returnedToPos = false;
        var legacyRecoveryCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => returnedToPos = true,
            recoverPreviousCardTransactionAsync: () =>
            {
                legacyRecoveryCalls++;
                return Task.FromResult(true);
            },
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (value, _) =>
            {
                handedOffCandidate = value;
                return Task.FromResult(true);
            }, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 2m));
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(candidate, handedOffCandidate);
        Assert.Equal(0, legacyRecoveryCalls);
        Assert.True(returnedToPos);
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.CartLines);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Null(viewModel.CardPaymentErrorOverlay);
        Assert.False(viewModel.IsPaymentInteractionLocked);

        cart.AddItem(CreateItem("SKU-158V5", "New Order Tea", "930158V5", PriceSourceKind.StoreRetailPrice, 12m));
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_confirmed_handoff_collection_notification_failure_still_unlocks_next_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V5-NOTIFY", "Handoff Notification Tea", "930158V5N", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000164");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000164")));
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => returnedToPos = true,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true), paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 2m));
        var throwOnce = true;
        viewModel.PaymentTenders.CollectionChanged += (_, _) =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("simulated tender projection listener failure");
            }
        };

        var thrown = await Record.ExceptionAsync(
            () => viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null));

        Assert.Null(thrown);
        Assert.True(returnedToPos);
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Null(viewModel.CardPaymentErrorOverlay);
        Assert.False(viewModel.IsPaymentInteractionLocked);

        cart.AddItem(CreateItem("SKU-158V5-NEXT", "Next Order Tea", "930158V5NEXT", PriceSourceKind.StoreRetailPrice, 12m));
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_confirmed_handoff_collection_notification_propagates_fatal_and_retains_recovery(
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal tender projection listener failure")
            : new StackOverflowException("fatal tender projection listener failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V5-FATAL", "Fatal Handoff Notification Tea", "930158V5F", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000165");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000165")));
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true));
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 2m));
        viewModel.PaymentTenders.CollectionChanged += (_, _) => throw fatal;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
    }

    [Fact]
    public async Task Payment_page_handoff_qualification_failure_keeps_current_order_locked()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V6", "Qualification Failure Tea", "930158V6", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is still unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000160")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000160")));
        var returnedToPos = false;
        var openCenterCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => returnedToPos = true,
            prepareCardPaymentHandoffAsync: _ =>
                Task.FromException<CardPaymentHandoffCandidate?>(new IOException("simulated persistence query failure")),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true),
            openCardRecoveryCenter: () => openCenterCalls++);

        var exception = await Record.ExceptionAsync(() => viewModel.SelectCardCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.False(returnedToPos);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.HasPrimaryAction);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.openRecoveryCenter",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(1, openCenterCalls);
        Assert.False(returnedToPos);
    }

    [Fact]
    public async Task Payment_page_unqualified_recovery_without_center_navigation_hides_primary_action()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V6B", "No Navigation Tea", "930158V6B", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000161")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000161")));
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(null),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true));

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.False(viewModel.CardPaymentErrorOverlay!.HasPrimaryAction);
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_handoff_qualification_propagates_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal handoff qualification failure")
            : new StackOverflowException("fatal handoff qualification failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V6-FATAL", "Fatal Qualification Tea", "930158V6FATAL", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is still unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000161")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000161")));
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ =>
                Task.FromException<CardPaymentHandoffCandidate?>(fatal),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(true));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => viewModel.SelectCardCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_handoff_execution_propagates_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal handoff execution failure")
            : new StackOverflowException("fatal handoff execution failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V7-FATAL", "Fatal Handoff Tea", "930158V7FATAL", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000162");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: "The Linkly card result is still unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000162")));
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromException<bool>(fatal));

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        Assert.True(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
    }

    [Fact]
    public async Task Payment_page_handoff_command_notification_cannot_mask_fatal_exception()
    {
        var fatal = new OutOfMemoryException("fatal handoff must remain authoritative");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V8-FATAL", "Fatal Notification Tea", "930158V8FATAL", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.Parse("10000000-0000-0000-0000-000000000163");
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000163")));
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromException<bool>(fatal));

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.CardPaymentErrorPrimaryActionCommand.CanExecuteChanged += (_, _) =>
            throw new InvalidOperationException("command notification must not replace fatal handoff failure");
        var cardSession = typeof(PaymentViewModel)
            .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var executionTask = Assert.IsAssignableFrom<Task>(
            cardSession.GetType()
                .GetMethod("ExecuteErrorPrimaryActionAsync", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(cardSession, null));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => executionTask);

        Assert.Same(fatal, thrown);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Payment_page_stale_handoff_candidate_rejection_keeps_cart_and_original_unknown_status()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158V7", "Stale Candidate Tea", "930158V7", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        const string unknownMessage = "The persisted attempt no longer matches this payment.";
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.resultUnknown",
            StatusMessage: unknownMessage,
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("10000000-0000-0000-0000-000000000160")),
            RecoveryOrderGuid: Guid.Parse("20000000-0000-0000-0000-000000000160")));
        var candidate = new CardPaymentHandoffCandidate(
            CardProcessorKind.Linkly,
            Guid.Parse("10000000-0000-0000-0000-000000000160"));
        var returnedToPos = false;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            onBackToPos: () => returnedToPos = true,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) => Task.FromResult(false));

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.False(returnedToPos);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.Equal(unknownMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_shutdown_disables_handoff_without_clearing_unknown_state()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158Y", "Shutdown Recovery Tea", "930158Y", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var attemptGuid = Guid.NewGuid();
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attemptGuid),
            RecoveryOrderGuid: Guid.NewGuid()));
        var candidate = new CardPaymentHandoffCandidate(CardProcessorKind.Linkly, attemptGuid);
        var handoffCalls = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            prepareCardPaymentHandoffAsync: _ => Task.FromResult<CardPaymentHandoffCandidate?>(candidate),
            handoffCardPaymentAsync: (_, _) =>
            {
                handoffCalls++;
                return Task.FromResult(true);
            });

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        _ = viewModel.BeginShutdown();
        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.CardPaymentErrorPrimaryActionCommand.CanExecute(null));
        Assert.Equal(0, handoffCalls);
    }

    [Fact]
    public async Task Payment_page_ignores_second_card_submit_while_first_is_running()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158D", "Double Tap Card Tea", "930158D", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var firstPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;
        workflow.AddTenderStarted = null;

        var secondPaymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Equal(1, workflow.AddTenderCallCount);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-158D"),
            "payment.status.cardTenderAdded"));
        await Task.WhenAll(firstPaymentTask, secondPaymentTask);
        Assert.Equal(1, workflow.AddTenderCallCount);
        Assert.Single(viewModel.PaymentTenders);
    }

    [Fact]
    public async Task Payment_page_card_terminal_error_overlay_uses_status_key_for_localized_messages()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158A", "Localized Error Tea", "930158A", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.cloud.communicationFailed",
            StatusMessage: "Linkly Cloud 通讯失败。",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.CloudCommunicationFailed,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Equal(
            "payment.card.error.overlay.cloudCommunicationFailed.title",
            viewModel.CardPaymentErrorOverlay.TitleKey);
    }

    [Fact]
    public async Task Payment_page_active_linkly_session_error_hides_unqualified_recovery_action()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158B", "Active Session Tea", "930158B", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "linkly.backend.activeSessionRequiresRecovery",
            StatusMessage: "Current terminal already has an unfinished card transaction.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var recoverCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () =>
            {
                recoverCallCount++;
                return Task.FromResult(true);
            });

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.True(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.title",
            viewModel.CardPaymentErrorOverlay.TitleKey);
        Assert.False(viewModel.CardPaymentErrorOverlay.HasPrimaryAction);

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(0, recoverCallCount);
        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
    }

    [Fact]
    public async Task Payment_page_active_linkly_session_message_fallback_hides_unqualified_recovery_action()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158C", "Active Session Message Tea", "930158C", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(new PaymentTenderAttemptResult(
            false,
            "payment.status.cardDeclined",
            StatusMessage: "Current terminal already has an unfinished card transaction. Recover the previous transaction.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true)));
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            recoverPreviousCardTransactionAsync: () => Task.FromResult(true));

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CardPaymentErrorOverlay);
        Assert.Equal(
            "payment.card.error.overlay.activeSession.title",
            viewModel.CardPaymentErrorOverlay!.TitleKey);
        Assert.False(viewModel.CardPaymentErrorOverlay.HasPrimaryAction);
    }

    [Fact]
    public async Task Payment_page_linkly_fallback_prompt_primary_action_confirms_fallback()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158F", "Fallback Prompt Tea", "930158F", PriceSourceKind.StoreRetailPrice, 10m));
        var coordinator = new LinklyFallbackPromptCoordinator();
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            linklyFallbackPromptCoordinator: coordinator);
        var promptTask = coordinator.ConfirmFallbackAsync(
            new LinklyFallbackPromptRequest(
                LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionMode.CloudDirectSync,
                "Backend API is offline.",
                [LinklyConnectionMode.CloudBackendAsync.ToString()]));

        await WaitUntilAsync(() => viewModel.CardPaymentErrorOverlay is { IsOpen: true });

        Assert.False(promptTask.IsCompleted);
        Assert.Equal(
            "payment.card.error.overlay.fallback.title",
            viewModel.CardPaymentErrorOverlay!.TitleKey);
        Assert.Equal(
            "payment.card.error.overlay.fallback.use",
            viewModel.CardPaymentErrorOverlay.PrimaryButtonTextKey);
        Assert.Equal(
            "payment.card.error.overlay.fallback.cancel",
            viewModel.CardPaymentErrorOverlay.ButtonTextKey);

        await viewModel.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);

        Assert.True(await promptTask);
        Assert.False(viewModel.CardPaymentErrorOverlay.IsOpen);
    }

    [Fact]
    public async Task Payment_page_linkly_fallback_prompt_close_cancels_fallback()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158G", "Fallback Cancel Tea", "930158G", PriceSourceKind.StoreRetailPrice, 10m));
        var coordinator = new LinklyFallbackPromptCoordinator();
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            linklyFallbackPromptCoordinator: coordinator);
        var promptTask = coordinator.ConfirmFallbackAsync(
            new LinklyFallbackPromptRequest(
                LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionMode.CloudDirectSync,
                "Backend API is offline.",
                [LinklyConnectionMode.CloudBackendAsync.ToString()]));

        await WaitUntilAsync(() => viewModel.CardPaymentErrorOverlay is { IsOpen: true });

        viewModel.CloseCardPaymentErrorOverlayCommand.Execute(null);

        Assert.False(await promptTask);
        Assert.False(viewModel.CardPaymentErrorOverlay!.IsOpen);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_requires_voucher_code_before_adding_voucher()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-144", "Voucher Tea", "930144", PriceSourceKind.StoreRetailPrice, 5m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.SelectedPaymentMethod = PaymentMethodKind.Voucher;

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.voucherCodeRequired", viewModel.StatusMessage);

        viewModel.VoucherCodeText = "ABC123";

        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_voucher_dialog_accepts_touch_keyboard_input_and_adds_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-144B", "Voucher Dialog Tea", "930144B", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Cash, 0m)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.OpenVoucherEntryCommand.Execute(null);
        viewModel.VoucherEntryKeyCommand.Execute("X");
        viewModel.VoucherEntryKeyCommand.Execute("Clear");
        foreach (var key in new[] { "A", "B", "C", "-", "1", "2", "4", "Back", "3" })
        {
            viewModel.VoucherEntryKeyCommand.Execute(key);
        }

        await viewModel.ConfirmVoucherEntryCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Voucher, tender.Method);
        Assert.Equal("ABC-123", tender.Reference);
        Assert.Equal(string.Empty, viewModel.VoucherCodeText);
        Assert.Equal(string.Empty, viewModel.VoucherEntryText);
        Assert.False(viewModel.IsVoucherEntryDialogOpen);
        Assert.Equal(1, workflow.AddTenderCallCount);
    }

    [Fact]
    public async Task Payment_page_voucher_dialog_accepts_scan_routed_from_keyboard_fallback_without_auto_confirm()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-144S", "Voucher Scan Tea", "930144S", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Cash, 0m)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        // 弹窗没开时付款页不接这次扫码，由主窗口提示先点「代金券」。
        Assert.False(viewModel.TryAcceptScannedVoucherCode("hb-voucher-77"));
        Assert.True(viewModel.CanScanVoucherAfterOpeningEntry);

        viewModel.TenderAmountText = "5";
        viewModel.OpenVoucherEntryCommand.Execute(null);
        viewModel.VoucherEntryKeyCommand.Execute("9");

        Assert.True(viewModel.TryAcceptScannedVoucherCode("  hb-voucher-77 "));

        // 与扫码直接进输入框一致：整体替换、转大写，不自动确认。
        Assert.Equal("HB-VOUCHER-77", viewModel.VoucherEntryText);
        Assert.True(viewModel.IsVoucherEntryDialogOpen);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(0, workflow.AddTenderCallCount);

        await viewModel.ConfirmVoucherEntryCommand.ExecuteAsync(null);

        Assert.Equal("HB-VOUCHER-77", Assert.Single(viewModel.PaymentTenders).Reference);
    }

    [Fact]
    public async Task Payment_page_voucher_dialog_requires_code_before_confirming()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-144C", "Voucher Dialog Required Tea", "930144C", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.OpenVoucherEntryCommand.Execute(null);
        await viewModel.ConfirmVoucherEntryCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.voucherCodeRequired", viewModel.StatusMessage);
        Assert.True(viewModel.IsVoucherEntryDialogOpen);
        Assert.Equal(0, workflow.AddTenderCallCount);
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_resets_tenders_and_pending_voucher_retry_state()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-145", "Retry Voucher Tea", "930145", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "ABC123";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        workflow.ThrowOnComplete = new PaymentUploadFailedException(Guid.NewGuid(), 5m, 0m, "upload failed");
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal("upload failed", viewModel.StatusMessage);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        var updatedSession = Session with { PendingSyncCount = 3 };
        viewModel.PrepareForEntry(updatedSession);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(string.Empty, viewModel.VoucherCodeText);
        Assert.Equal(PaymentMethodKind.Cash, viewModel.SelectedPaymentMethod);
        Assert.Equal(updatedSession.PendingSyncCount, viewModel.PendingSyncCount);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_page_voucher_upload_retry_propagates_fatal_exception_and_keeps_retry_lock(
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal voucher upload retry")
            : new StackOverflowException("fatal voucher upload retry");
        var orderGuid = Guid.NewGuid();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-145-FATAL", "Fatal Retry Voucher Tea", "930145FATAL", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            ThrowOnComplete = new PaymentUploadFailedException(orderGuid, 5m, 0m, "upload failed")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "ABC123";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        workflow.ThrowOnRetryVoucherUpload = fatal;

        var thrown = await Record.ExceptionAsync(() => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.Equal(orderGuid, workflow.LastRetryVoucherUploadOrderGuid);
        Assert.False(cart.IsEmpty);
        Assert.Single(viewModel.PaymentTenders);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_installment_toggle_can_be_closed_before_confirm_and_prepare_resets()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-TOGGLE", "Installment Tea", "939001", PriceSourceKind.StoreRetailPrice, 55m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService());

        viewModel.IsInstallmentPaymentEnabled = true;

        Assert.True(viewModel.IsInstallmentPaymentEnabled);
        Assert.False(viewModel.IsInstallmentSwitchLocked);

        viewModel.IsInstallmentPaymentEnabled = false;

        Assert.False(viewModel.IsInstallmentPaymentEnabled);

        viewModel.IsInstallmentPaymentEnabled = true;
        viewModel.PrepareForEntry(Session);

        Assert.False(viewModel.IsInstallmentPaymentEnabled);
        Assert.False(viewModel.IsInstallmentSwitchLocked);
    }

    [Fact]
    public void Payment_page_installment_customer_dialog_confirms_touch_keyboard_edits()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CUSTOMER-DIALOG", "Customer Dialog Tea", "939007", PriceSourceKind.StoreRetailPrice, 55m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService())
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222"
        };

        viewModel.OpenInstallmentCustomerDialogCommand.Execute(null);

        Assert.True(viewModel.IsInstallmentCustomerDialogOpen);
        Assert.Equal("Alice", viewModel.InstallmentCustomerDraftName);
        Assert.Equal("0400111222", viewModel.InstallmentCustomerDraftPhone);

        viewModel.SelectInstallmentCustomerFieldCommand.Execute("Name");
        viewModel.InstallmentCustomerKeyCommand.Execute("Clear");
        foreach (var key in new[] { "B", "O", "B", "Space", "2" })
        {
            viewModel.InstallmentCustomerKeyCommand.Execute(key);
        }

        viewModel.SelectInstallmentCustomerFieldCommand.Execute("Phone");
        viewModel.InstallmentCustomerKeyCommand.Execute("Clear");
        foreach (var key in new[] { "0", "4", "+", "1", "Back", "2" })
        {
            viewModel.InstallmentCustomerKeyCommand.Execute(key);
        }

        viewModel.ConfirmInstallmentCustomerDialogCommand.Execute(null);

        Assert.False(viewModel.IsInstallmentCustomerDialogOpen);
        Assert.Equal("BOB 2", viewModel.InstallmentCustomerName);
        Assert.Equal("04+2", viewModel.InstallmentCustomerPhone);
    }

    [Fact]
    public void Payment_page_installment_customer_dialog_cancel_keeps_original_values()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CUSTOMER-CANCEL", "Customer Cancel Tea", "939008", PriceSourceKind.StoreRetailPrice, 55m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService())
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222"
        };

        viewModel.OpenInstallmentCustomerDialogCommand.Execute(null);
        viewModel.InstallmentCustomerDraftName = "Bob";
        viewModel.InstallmentCustomerDraftPhone = "0400333444";

        viewModel.CancelInstallmentCustomerDialogCommand.Execute(null);

        Assert.False(viewModel.IsInstallmentCustomerDialogOpen);
        Assert.Equal("Alice", viewModel.InstallmentCustomerName);
        Assert.Equal("0400111222", viewModel.InstallmentCustomerPhone);
    }

    [Fact]
    public async Task Payment_page_new_installment_requires_customer_details()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CUSTOMER", "Customer Tea", "939002", PriceSourceKind.StoreRetailPrice, 55m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService())
        {
            IsInstallmentPaymentEnabled = true,
            TenderAmountText = "20"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));

        viewModel.InstallmentCustomerName = "Alice";
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));

        viewModel.InstallmentCustomerPhone = "0400111222";
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_new_installment_rejects_first_payment_below_minimum()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-MIN-DOWN", "Minimum Down Tea", "939004", PriceSourceKind.StoreRetailPrice, 80m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService())
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "19.99"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_new_installment_rejects_order_total_below_minimum()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-MIN-TOTAL", "Minimum Total Tea", "939005", PriceSourceKind.StoreRetailPrice, 49.99m));
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: new FakeInstallmentOrderService())
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "20"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_new_installment_creates_order_with_partial_first_payment_without_completing_regular_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CREATE", "Partial Installment Tea", "939003", PriceSourceKind.StoreRetailPrice, 80m));
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var completed = false;
        InstallmentOrderSummary? createdOrder = null;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            installmentOrderService: installmentService,
            onInstallmentOrderCreatedAsync: order =>
            {
                createdOrder = order;
                return Task.CompletedTask;
            })
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "20"
        };
        viewModel.PaymentCompleted += (_, _) => completed = true;

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.False(completed);
        Assert.Equal(0, workflow.CompletePaymentCallCount);
        Assert.NotNull(installmentService.LastCreateRequest);
        Assert.Equal("Alice", installmentService.LastCreateRequest!.CustomerName);
        Assert.Equal("0400111222", installmentService.LastCreateRequest.CustomerPhone);
        Assert.Equal(20m, installmentService.LastCreateRequest.DownPaymentAmount);
        Assert.NotNull(createdOrder);
        Assert.Equal("IO-NEW", createdOrder!.OrderNumber);
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.PaymentTenders);
    }

    [Theory]
    [InlineData(false, "oom")]
    [InlineData(false, "stack")]
    [InlineData(true, "oom")]
    [InlineData(true, "stack")]
    public async Task Payment_page_installment_submission_propagates_fatal_exception_and_keeps_lock(
        bool isRepayment,
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal installment submission")
            : new StackOverflowException("fatal installment submission");
        var cart = new PosCartService();
        var installmentService = new FakeInstallmentOrderService
        {
            ThrowOnCreateOrder = isRepayment ? null : fatal,
            ThrowOnAddRepayment = isRepayment ? fatal : null
        };
        var viewModel = new PaymentViewModel(
            cart,
            new FakeCashPaymentWorkflowService(),
            Session,
            installmentOrderService: installmentService);

        if (isRepayment)
        {
            viewModel.PrepareForInstallmentRepayment(
                Session,
                CreateInstallmentOrder("IO-FATAL", "Alice", "0400111222", paidAmount: 30m, outstandingAmount: 90m));
            viewModel.TenderAmountText = "30";
        }
        else
        {
            cart.AddItem(CreateItem("SKU-INST-FATAL", "Fatal Installment Tea", "939003FATAL", PriceSourceKind.StoreRetailPrice, 80m));
            viewModel.IsInstallmentPaymentEnabled = true;
            viewModel.InstallmentCustomerName = "Alice";
            viewModel.InstallmentCustomerPhone = "0400111222";
            viewModel.TenderAmountText = "20";
        }

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        var thrown = await Record.ExceptionAsync(() => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));

        Assert.Same(fatal, thrown);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.Single(viewModel.PaymentTenders);
        if (!isRepayment)
        {
            Assert.Single(cart.Lines);
        }
    }

    [Fact]
    public async Task Payment_page_new_installment_full_cash_payment_cancel_keeps_tender_and_cart()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CASH-FULL-CANCEL", "Full Cash Cancel Tea", "939010", PriceSourceKind.StoreRetailPrice, 80m));
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var confirmCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            installmentOrderService: installmentService,
            confirmInstallmentFullFirstPaymentAsync: () =>
            {
                confirmCallCount++;
                return Task.FromResult(false);
            })
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "100"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmCallCount);
        Assert.Null(installmentService.LastCreateRequest);
        Assert.Equal(0, workflow.CompletePaymentCallCount);
        Assert.Single(cart.Lines);
        Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(20m, viewModel.ChangeDue);
        Assert.Equal("payment.installment.status.fullFirstPaymentCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_new_installment_full_payment_without_confirmation_delegate_continues()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-FULL-NO-PROMPT", "Full No Prompt Tea", "939011", PriceSourceKind.StoreRetailPrice, 80m));
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            installmentOrderService: installmentService)
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "80"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(installmentService.LastCreateRequest);
        Assert.Equal(80m, installmentService.LastCreateRequest!.DownPaymentAmount);
    }

    [Fact]
    public async Task Payment_page_new_installment_allows_cash_overpay_with_change_and_applies_order_amount()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-CASH-OVERPAY", "Cash Overpay Installment Tea", "939009", PriceSourceKind.StoreRetailPrice, 80m));
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var confirmCallCount = 0;
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            installmentOrderService: installmentService,
            confirmInstallmentFullFirstPaymentAsync: () =>
            {
                confirmCallCount++;
                return Task.FromResult(true);
            })
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "100"
        };

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(20m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmCallCount);
        Assert.Equal(0, workflow.CompletePaymentCallCount);
        Assert.NotNull(installmentService.LastCreateRequest);
        Assert.Equal(80m, installmentService.LastCreateRequest!.DownPaymentAmount);
        Assert.Equal(80m, installmentService.LastCreateRequest.DownPayment.Amount);
    }

    [Fact]
    public async Task Payment_page_new_installment_splits_voucher_reference_for_create_request()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INST-VOUCHER", "Voucher Installment Tea", "939006", PriceSourceKind.StoreRetailPrice, 80m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, 20m, "VOUCHER:VIP001:LOCK-001")
        };
        var installmentService = new FakeInstallmentOrderService();
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            installmentOrderService: installmentService, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)))
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Alice",
            InstallmentCustomerPhone = "0400111222",
            TenderAmountText = "20",
            VoucherCodeText = "VIP001"
        };

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(installmentService.LastCreateRequest);
        Assert.Equal("VIP001", installmentService.LastCreateRequest!.DownPayment.Reference);
        Assert.Equal("LOCK-001", installmentService.LastCreateRequest.DownPayment.ReservationToken);
    }

    [Fact]
    public async Task Payment_page_disabled_card_cannot_use_another_installment_draft_to_bypass_settings()
    {
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService { ThrowOnAddRepayment = new IOException("submission unavailable") };
        var settings = new MutablePaymentMethodSettingsService();
        using var viewModel = new PaymentViewModel(new PosCartService(), workflow, Session,
            installmentOrderService: installmentService, paymentMethodSettingsService: settings);
        var previousOrder = CreateInstallmentOrder("IO-PREVIOUS-DRAFT", "Bob", "0400222333", paidAmount: 30m, outstandingAmount: 90m);
        viewModel.PrepareForInstallmentRepayment(Session, previousOrder);
        viewModel.TenderAmountText = "30";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<IOException>(() => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));
        var previousRequest = Assert.IsType<InstallmentOrderRepaymentRequest>(installmentService.LastRepaymentRequest);

        // 上一笔保留的幂等身份，不能让另一张订单的新卡付款绕过关闭开关。
        var nextOrder = CreateInstallmentOrder("IO-NEXT-DRAFT", "Alice", "0400111222", paidAmount: 40m, outstandingAmount: 80m);
        viewModel.PrepareForInstallmentRepayment(Session, nextOrder);
        viewModel.TenderAmountText = "20";
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        Assert.Equal(PaymentMethodKind.Card, Assert.Single(viewModel.PaymentTenders).Method);
        await settings.SaveAsync(new(UseManualCard: true));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Same(previousRequest, installmentService.LastRepaymentRequest);
        Assert.Equal(0, workflow.AddTenderCallCount);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_records_payment_without_regular_checkout()
    {
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var order = CreateInstallmentOrder("IO-20260703-0001", "Bob", "0400222333", paidAmount: 30m, outstandingAmount: 90m);
        var createdCallbackCount = 0;
        InstallmentOrderSummary? callbackOrder = null;
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            workflow,
            Session,
            installmentOrderService: installmentService,
            onInstallmentOrderCreatedAsync: completedOrder =>
            {
                createdCallbackCount++;
                callbackOrder = completedOrder;
                return Task.CompletedTask;
            });

        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "30";

        Assert.True(viewModel.IsInstallmentPaymentEnabled);
        Assert.True(viewModel.IsInstallmentSwitchLocked);
        Assert.False(viewModel.CanEditInstallmentCustomer);

        viewModel.OpenInstallmentCustomerDialogCommand.Execute(null);
        Assert.False(viewModel.IsInstallmentCustomerDialogOpen);

        viewModel.IsInstallmentPaymentEnabled = false;
        Assert.True(viewModel.IsInstallmentPaymentEnabled);

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.Equal(0, workflow.CompletePaymentCallCount);
        Assert.NotNull(installmentService.LastRepaymentRequest);
        Assert.Equal(order.OrderId, installmentService.LastRepaymentRequest!.InstallmentGuid);
        Assert.Equal(30m, installmentService.LastRepaymentRequest.Payment.Amount);
        Assert.Equal(1, createdCallbackCount);
        Assert.Equal(order.OrderId, callbackOrder!.OrderId);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_allows_cash_overpay_with_change_and_applies_outstanding_amount()
    {
        var workflow = new FakeCashPaymentWorkflowService();
        var installmentService = new FakeInstallmentOrderService();
        var order = CreateInstallmentOrder("IO-20260703-0004", "Bob", "0400222333", paidAmount: 30m, outstandingAmount: 48m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            workflow,
            Session,
            installmentOrderService: installmentService,
            confirmInstallmentFullFirstPaymentAsync: () => throw new InvalidOperationException("Repayments must not show first-payment confirmation."));

        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "50";

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(2m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.NotNull(installmentService.LastRepaymentRequest);
        Assert.Equal(order.OrderId, installmentService.LastRepaymentRequest!.InstallmentGuid);
        Assert.Equal(48m, installmentService.LastRepaymentRequest.Payment.Amount);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_defers_voucher_provider_until_claimed_operation()
    {
        var workflow = new FakeCashPaymentWorkflowService
        {
            TenderToAdd = new PaymentTender(PaymentMethodKind.Voucher, 30m, "VOUCHER:VIP002:LOCK-002")
        };
        var installmentService = new FakeInstallmentOrderService();
        var order = CreateInstallmentOrder("IO-20260703-0003", "Bob", "0400222333", paidAmount: 30m, outstandingAmount: 90m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            workflow,
            Session,
            installmentOrderService: installmentService, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));

        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "30";
        viewModel.VoucherCodeText = "VIP002";

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.NotNull(installmentService.LastRepaymentRequest);
        Assert.Equal("VIP002", installmentService.LastRepaymentRequest!.Payment.Reference);
        Assert.True(string.IsNullOrWhiteSpace(installmentService.LastRepaymentRequest.Payment.ReservationToken));
    }

    [Fact]
    public async Task Payment_page_installment_repayment_defers_card_terminal_until_claimed_operation()
    {
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderException = new InvalidOperationException("Card provider must not run while selecting a repayment tender.")
        };
        var installmentService = new FakeInstallmentOrderService();
        var order = CreateInstallmentOrder("IO-20260703-0005", "Bob", "0400222333", paidAmount: 30m, outstandingAmount: 90m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            workflow,
            Session,
            installmentOrderService: installmentService);

        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "30";

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.AddTenderCallCount);
        Assert.NotNull(installmentService.LastRepaymentRequest);
        Assert.Equal(PaymentMethodKind.Card, installmentService.LastRepaymentRequest!.Payment.Method);
        Assert.Null(installmentService.LastRepaymentRequest.Payment.CardTransactions);
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static PosSessionState OfflineSession => Session with { IsOnline = false };

    private static LocalOrder CreateCompletedOrder(decimal actualAmount)
    {
        return new LocalOrder(
            Guid.NewGuid(),
            Session.StoreCode,
            Session.DeviceCode,
            Session.CashierId,
            Session.CashierName,
            DateTimeOffset.UtcNow,
            actualAmount,
            0m,
            actualAmount,
            [],
            []);
    }

    private static PosCartService CreateRefundVoucherCart(decimal amount = 8.5m)
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-VOUCHER",
            null,
            "Refund Voucher Tea",
            "930142V",
            "ITEM-REFUND-VOUCHER",
            null,
            1m,
            amount,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-VOUCHER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        return cart;
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price,
        string? referenceCode = null,
        string? itemNumber = null,
        string? productBarcode = null,
        string? productImage = null)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: productBarcode ?? barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }

    private static InstallmentOrderSummary CreateInstallmentOrder(
        string orderNumber,
        string customerName,
        string phone,
        decimal paidAmount,
        decimal outstandingAmount)
    {
        return new InstallmentOrderSummary(
            Guid.NewGuid(),
            orderNumber,
            customerName,
            phone,
            paidAmount + outstandingAmount,
            20m,
            paidAmount,
            outstandingAmount,
            0,
            outstandingAmount > 0m,
            outstandingAmount == 0m,
            outstandingAmount > 0m,
            outstandingAmount > 0m,
            outstandingAmount > 0m ? "待补款" : "待提货",
            "POS-01",
            DateTimeOffset.Now);
    }

    private static void SetUnsafeQuantity(CartLine line, decimal quantity)
    {
        var field = typeof(CartLine).GetField("_quantity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(line, quantity);
    }

    private static Task ExecuteManualScanAsync(PosTerminalViewModel viewModel)
    {
        return viewModel.ScanCommand.ExecuteAsync(null);
    }

    private static PromotionRuleDto CreatePromotionRule(
        string id,
        IReadOnlyList<string> productCodes,
        int applyQuantity,
        decimal fixedPrice)
    {
        var asOf = DateTimeOffset.UtcNow;
        return new PromotionRuleDto(
            id,
            $"Rule {id}",
            asOf.AddDays(-1).UtcDateTime,
            asOf.AddDays(1).UtcDateTime,
            false,
            10,
            applyQuantity,
            fixedPrice,
            null,
            productCodes.Select(productCode => new PromotionRuleProductDto(productCode, 1)).ToArray());
    }

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private static bool HasLog(ConcurrentQueue<string> lines, string text)
    {
        return lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }

    private sealed class InMemoryOrderRepository : ILocalOrderRepository
    {
        public LocalOrder? LastOrder { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            LastOrder = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(LastOrder?.OrderGuid == orderGuid ? LastOrder : null);
        }
    }

    private sealed class InMemorySyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(1, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class FakeLocalPromotionRepository : ILocalPromotionRepository
    {
        private readonly Dictionary<string, IReadOnlyList<PromotionRuleDto>> _rulesByStore = new(StringComparer.OrdinalIgnoreCase);

        public void SeedStore(string storeCode, IReadOnlyList<PromotionRuleDto> rules)
        {
            _rulesByStore[storeCode] = rules;
        }

        public Task ReplaceStoreRulesAsync(
            string storeCode,
            PromotionRulesResponse response,
            CancellationToken cancellationToken = default)
        {
            _rulesByStore[storeCode] = response.Rules;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            if (!_rulesByStore.TryGetValue(storeCode, out var rules))
            {
                return Task.FromResult<IReadOnlyList<PromotionRuleDto>>([]);
            }

            var activeRules = rules
                .Where(rule => rule.EffectiveStart <= asOf.UtcDateTime && rule.EffectiveEnd >= asOf.UtcDateTime)
                .ToArray();
            return Task.FromResult<IReadOnlyList<PromotionRuleDto>>(activeRules);
        }
    }

    private sealed class CountingPromotionEvaluationService : IPromotionEvaluationService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<PromotionLineDiscount>> EvaluateAsync(
            IReadOnlyList<CartLine> lines,
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (lines.Count == 1 && lines[0].Quantity >= 2m)
            {
                return Task.FromResult<IReadOnlyList<PromotionLineDiscount>>([new PromotionLineDiscount(lines[0], 5m)]);
            }

            return Task.FromResult<IReadOnlyList<PromotionLineDiscount>>([]);
        }
    }

    private sealed class ScriptedPromotionEvaluationService : IPromotionEvaluationService
    {
        private readonly Queue<Func<IReadOnlyList<CartLine>, Task<IReadOnlyList<PromotionLineDiscount>>>> _steps = new();

        public int CallCount { get; private set; }

        public List<decimal> SnapshotQuantities { get; } = [];

        public void EnqueueSync(Func<IReadOnlyList<CartLine>, IReadOnlyList<PromotionLineDiscount>> step)
        {
            _steps.Enqueue(lines => Task.FromResult(step(lines)));
        }

        public void EnqueueAsync(Func<IReadOnlyList<CartLine>, Task<IReadOnlyList<PromotionLineDiscount>>> step)
        {
            _steps.Enqueue(step);
        }

        public void EnqueueThrow(Exception exception)
        {
            _steps.Enqueue(_ => Task.FromException<IReadOnlyList<PromotionLineDiscount>>(exception));
        }

        public async Task<IReadOnlyList<PromotionLineDiscount>> EvaluateAsync(
            IReadOnlyList<CartLine> lines,
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SnapshotQuantities.Add(lines.Sum(line => line.Quantity));

            if (_steps.Count == 0)
            {
                return [];
            }

            return await _steps.Dequeue()(lines);
        }
    }

    private sealed class FakePosTerminalWorkflowService : IPosTerminalWorkflowService
    {
        public event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

        public PosTerminalWorkflowResult ProcessScanResult { get; set; } = new();

        public PosTerminalWorkflowResult AddSelectedItemResult { get; set; } = new();

        public PosTerminalWorkflowResult AddOpenItemResult { get; set; } = new();

        public PosTerminalWorkflowResult RemoveLineResult { get; set; } = new();

        public PosTerminalWorkflowResult IncreaseLineResult { get; set; } = new();

        public PosTerminalWorkflowResult DecreaseLineResult { get; set; } = new();

        public PosTerminalWorkflowResult ModifySelectedLineQuantityResult { get; set; } = new();

        public PosTerminalWorkflowResult ModifySelectedLinePriceResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplySelectedLineDiscountAmountResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplySelectedLineDiscountPercentResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplyQuickDiscountPercentResult { get; set; } = new();

        public PosTerminalWorkflowResult ClearCartResult { get; set; } = new();

        public PosTerminalWorkflowResult GuardPaymentResult { get; set; } = new();

        public string? LastProcessScanText { get; private set; }

        public bool? LastProcessScanPreferExactLookup { get; private set; }

        public PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source, string? traceId = null)
        {
            LastProcessScanText = scanText;
            LastProcessScanPreferExactLookup = preferExactLookup;
            return ProcessScanResult;
        }

        public Task<PosTerminalWorkflowResult> ProcessScanAsync(
            PosSessionState session,
            string scanText,
            bool preferExactLookup,
            string source,
            string? traceId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ProcessScan(session, scanText, preferExactLookup, source, traceId));
        }

        public PosTerminalWorkflowResult AddSelectedItem(PosSessionState session, SellableItemDto item, bool clearScanText, bool closeMatchesPopup, string operation)
        {
            return AddSelectedItemResult;
        }

        public PosTerminalWorkflowResult AddOpenItem(PosSessionState session, string keypadBuffer)
        {
            return AddOpenItemResult;
        }

        public PosTerminalWorkflowResult RemoveLine(CartLine? line)
        {
            return RemoveLineResult;
        }

        public PosTerminalWorkflowResult IncreaseLine(CartLine? line)
        {
            return IncreaseLineResult;
        }

        public PosTerminalWorkflowResult DecreaseLine(CartLine? line)
        {
            return DecreaseLineResult;
        }

        public PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer)
        {
            return ModifySelectedLineQuantityResult;
        }

        public PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer)
        {
            return ModifySelectedLinePriceResult;
        }

        public PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
        {
            return ApplySelectedLineDiscountAmountResult;
        }

        public PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
        {
            return ApplySelectedLineDiscountPercentResult;
        }

        public PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation)
        {
            return ApplyQuickDiscountPercentResult;
        }

        public PosTerminalWorkflowResult ClearCart()
        {
            return ClearCartResult;
        }

        public PosTerminalWorkflowResult GuardPayment()
        {
            return GuardPaymentResult;
        }

        public void RaiseCatalogReloaded(IReadOnlyList<SellableItemDto> catalogItems)
        {
            CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs(catalogItems));
        }

        public void RaiseCatalogIndexUpdated()
        {
            CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs());
        }
    }

    private sealed class RevocableOperationAuthorizationService : IOperationAuthorizationService
    {
        private readonly List<OperationAuthorizationScope> _issuedScopes = [];

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public string ScannerPageId => "OperationAuthorizationTest";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public List<string> RequestedPermissions { get; } = [];

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            RequestedPermissions.Add(permissionCode);
            var requestingSession = new CashierSessionDto(
                session.CashierId,
                session.CashierId,
                session.CashierName,
                session.StoreCode,
                session.DeviceCode,
                [],
                [],
                [session.StoreCode],
                false,
                false,
                false);
            var scope = new OperationAuthorizationScope(requestingSession, permissionCode, screen, action);
            _issuedScopes.Add(scope);
            return Task.FromResult<OperationAuthorizationScope?>(scope);
        }

        public bool ProcessScannerBarcode(string barcode) => false;

        public void Cancel()
        {
        }

        public void RevokeAll()
        {
            foreach (var scope in _issuedScopes)
            {
                scope.Dispose();
            }

            _issuedScopes.Clear();
        }
    }

    private sealed class FakeCashPaymentWorkflowService : ICashPaymentWorkflowService
    {
        public PaymentTender TenderToAdd { get; set; } = new(PaymentMethodKind.Voucher, 5m, "ABC123");

        public Exception? ThrowOnComplete { get; set; }

        public CashPaymentWorkflowResult? CompletePaymentResult { get; set; }

        public Exception? ThrowOnRetryVoucherUpload { get; set; }

        public Guid? LastRetryVoucherUploadOrderGuid { get; private set; }

        public TaskCompletionSource? AddTenderStarted { get; set; }

        public TaskCompletionSource<PaymentTenderAttemptResult>? AddTenderResult { get; set; }

        public Exception? AddTenderException { get; set; }

        public bool IgnoreCancellation { get; set; }

        public Action<CancellationToken>? AfterAddTenderResult { get; set; }

        public Exception? AfterAddTenderResultException { get; private set; }

        public int AddTenderCallCount { get; private set; }

        public CancellationToken LastAddTenderCancellationToken { get; private set; }

        public int CompletePaymentCallCount { get; private set; }

        public TaskCompletionSource? CompletePaymentStarted { get; set; }

        public TaskCompletionSource? CompletePaymentRelease { get; set; }

        public List<PaymentTender> ReleasedVoucherTenders { get; } = [];

        public bool ReleaseVoucherTenderResult { get; set; } = true;

        public Exception? ThrowOnReleaseVoucherTender { get; set; }

        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
        {
            return decimal.TryParse(amountTenderedText, out tenderedAmount);
        }

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount)
        {
            return TryParseTenderedAmount(amountTenderedText, out var tenderedAmount)
                ? decimal.Round(tenderedAmount - actualAmount, 2, MidpointRounding.AwayFromZero)
                : 0m;
        }

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders)
        {
            return tenders.Sum(tender => tender.Amount);
        }

        public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
        {
            if (actualAmount < 0m)
            {
                return actualAmount - tenders.Sum(tender => tender.Amount);
            }

            return Math.Max(0m, actualAmount - CalculateTenderedAmount(tenders));
        }

        public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount)
        {
            return Math.Max(0m, CalculateTenderedAmount(tenders) - actualAmount);
        }

        public async Task<PaymentTenderAttemptResult> AddTenderAsync(
            PaymentMethodKind method,
            PosSessionState session,
            decimal actualAmount,
            IReadOnlyList<PaymentTender> currentTenders,
            string? amountText,
            string? referenceText = null,
            CancellationToken cancellationToken = default,
            PosCartSnapshot? cartSnapshot = null)
        {
            AddTenderCallCount++;
            LastAddTenderCancellationToken = cancellationToken;
            AddTenderStarted?.SetResult();

            if (AddTenderException is not null)
            {
                throw AddTenderException;
            }

            if (AddTenderResult is not null)
            {
                var result = IgnoreCancellation
                    ? await AddTenderResult.Task
                    : await AddTenderResult.Task.WaitAsync(cancellationToken);
                try
                {
                    AfterAddTenderResult?.Invoke(cancellationToken);
                }
                catch (Exception ex)
                {
                    AfterAddTenderResultException = ex;
                    throw;
                }

                return result;
            }

            var tender = TenderToAdd.Method == method
                ? TenderToAdd
                : new PaymentTender(method, decimal.TryParse(amountText, out var amount) ? amount : 5m, referenceText);

            return PaymentTenderAttemptResult.Success(tender, "payment.status.tenderAdded");
        }

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default)
        {
            CompletePaymentCallCount++;
            CompletePaymentStarted?.TrySetResult();
            if (CompletePaymentRelease is not null)
            {
                await CompletePaymentRelease.Task;
            }

            if (ThrowOnComplete is not null)
            {
                throw ThrowOnComplete;
            }

            if (CompletePaymentResult is not null)
            {
                cart.Clear();
                return CompletePaymentResult;
            }

            throw new NotSupportedException();
        }

        public Task<bool> ReleaseVoucherTenderAsync(
            PaymentTender tender,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            ReleasedVoucherTenders.Add(tender);
            if (ThrowOnReleaseVoucherTender is not null)
            {
                throw ThrowOnReleaseVoucherTender;
            }

            return Task.FromResult(ReleaseVoucherTenderResult);
        }

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default)
        {
            LastRetryVoucherUploadOrderGuid = orderGuid;
            if (ThrowOnRetryVoucherUpload is not null)
            {
                throw ThrowOnRetryVoucherUpload;
            }

            if (CompletePaymentResult is not null)
            {
                return Task.FromResult(CompletePaymentResult);
            }

            throw new NotSupportedException();
        }
    }

    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public Exception? ThrowOnCreateOrder { get; init; }

        public Exception? ThrowOnAddRepayment { get; init; }

        public InstallmentOrderCreateRequest? LastCreateRequest { get; private set; }

        public InstallmentOrderRepaymentRequest? LastRepaymentRequest { get; private set; }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
        }

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalInstallmentOrder?>(null);
        }

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            if (ThrowOnCreateOrder is not null)
            {
                throw ThrowOnCreateOrder;
            }

            return Task.FromResult(new InstallmentOrderCreateResult(
                true,
                "分期单已创建。",
                CreateInstallmentOrder("IO-NEW", request.CustomerName, request.CustomerPhone, request.DownPaymentAmount, request.CartSnapshot.ActualAmount - request.DownPaymentAmount)));
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            LastRepaymentRequest = request;
            if (ThrowOnAddRepayment is not null)
            {
                throw ThrowOnAddRepayment;
            }

            return Task.FromResult(new InstallmentOrderActionResult(true, "补款已记录。"));
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];
        private string? _activePageId;

        public bool IsActive { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            _handlers.Remove(pageId);
        }

        public void SetActivePage(string? pageId)
        {
            _activePageId = pageId;
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public void ClearPendingInput()
        {
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        public void Emit(string barcode, DateTimeOffset? scannedAt = null)
        {
            if (_activePageId is not null && _handlers.TryGetValue(_activePageId, out var handler))
            {
                handler(new RawBarcodeScannedEventArgs(barcode, "scanner-device", scannedAt ?? DateTimeOffset.Now));
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeUserFeedbackService : IUserFeedbackService
    {
        public List<UserFeedbackCue> Cues { get; } = [];

        public void Play(UserFeedbackCue cue)
        {
            Cues.Add(cue);
        }
    }

    private sealed class ApprovedCardTerminalClient(
        string reference,
        PaymentAuthorizationResult? authorization = null) : ICardTerminalClient
    {
        public List<string?> RefundOriginalReferences { get; } = [];

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(authorization ?? new PaymentAuthorizationResult(true, reference));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            RefundOriginalReferences.Add(originalReference);
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }
}
