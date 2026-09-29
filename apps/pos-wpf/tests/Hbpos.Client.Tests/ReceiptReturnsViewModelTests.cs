using Hbpos.Client.Wpf.Localization;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptReturnsViewModelTests
{
    [Fact]
    public void Dispose_stops_receiving_culture_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { },
            localization: localization);
        var localizedChangeCount = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ReceiptReturnsViewModel.OrderSummaryText) or
                nameof(ReceiptReturnsViewModel.StatusMessage))
            {
                localizedChangeCount++;
            }
        };

        try
        {
            localization.SetCulture(LocalizationService.ChineseCultureName);
            Assert.True(localizedChangeCount > 0);

            viewModel.Dispose();
            viewModel.Dispose();
            var countAfterDispose = localizedChangeCount;
            localization.SetCulture(LocalizationService.DefaultCultureName);

            Assert.Equal(countAfterDispose, localizedChangeCount);
        }
        finally
        {
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Theory]
    [InlineData("HBPOSE1-K1-AA-BB")]
    [InlineData("HBPOSE2-compact-token")]
    public void ScannerBarcode_emergency_token_is_consumed_without_order_lookup(string token)
    {
        var workflow = new FakeReceiptReturnsWorkflowService();
        var viewModel = new ReceiptReturnsViewModel(workflow, CreateSession(), () => { })
        {
            ScanText = "ORDER-EXISTING"
        };

        var processed = viewModel.ProcessScannerBarcode(token, "scanner-device", "raw");

        Assert.True(processed);
        Assert.Equal("ORDER-EXISTING", viewModel.ScanText);
        Assert.Equal(0, workflow.LookupCallCount);
        Assert.Null(workflow.LastOrderQuery);
        Assert.Empty(viewModel.OrderLines);
    }

    [Fact]
    public async Task LookupCommand_shows_busy_state_and_ignores_scanner_until_completion()
    {
        var lookupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupCompletion = new TaskCompletionSource<ReceiptReturnLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupHandler = (_, _) =>
            {
                lookupStarted.TrySetResult(true);
                return lookupCompletion.Task;
            }
        };
        var viewModel = new ReceiptReturnsViewModel(workflow, CreateSession(), () => { })
        {
            ScanText = "ORDER-001"
        };

        var lookupTask = viewModel.LookupCommand.ExecuteAsync(null);
        await lookupStarted.Task;

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Searching for order...", viewModel.StatusMessage);
        Assert.False(viewModel.LookupCommand.CanExecute(null));

        Assert.True(viewModel.ProcessScannerBarcode("ORDER-002", "scanner-device", "raw"));
        Assert.Equal("ORDER-001", viewModel.ScanText);
        Assert.Equal(1, workflow.LookupCallCount);

        lookupCompletion.SetResult(CreateLookupResult());
        await lookupTask;

        Assert.False(viewModel.IsBusy);
        Assert.Single(viewModel.OrderLines);
    }

    [Fact]
    public async Task No_receipt_lookup_stays_single_flight_and_dispose_cancels_without_adding_a_pending_line()
    {
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            NoReceiptLookupHandler = async (_, cancellationToken) =>
            {
                lookupStarted.TrySetResult();
                // 在取消异常里记录，而不是另注册回调：Cancel() 先执行 Task.Delay 的回调，其续延若被空闲线程
                // 立刻执行，using 会在观察回调运行前注销它，导致取消已发生却永远观察不到。
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                return new ReceiptReturnProductLookupResult(null, string.Empty);
            }
        };
        var viewModel = new ReceiptReturnsViewModel(workflow, CreateSession(), () => { })
        {
            IsNoReceiptMode = true,
            ScanText = "930001"
        };

        var lookupTask = viewModel.LookupCommand.ExecuteAsync(null);
        await lookupStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.ProcessScannerBarcode("930002", "scanner-device", "raw"));
        Assert.Equal("930001", viewModel.ScanText);
        Assert.Equal(1, workflow.NoReceiptLookupCallCount);

        viewModel.Dispose();
        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await lookupTask;

        Assert.Empty(viewModel.PendingLines);
    }

    [Fact]
    public async Task No_receipt_lookup_dispose_cancels_real_authorization_prompt_and_releases_pending_slot()
    {
        var requester = CreateCashier();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(requester);
        var authorization = new OperationAuthorizationService(
            new NoopCashierLoginService(),
            cashierContext,
            new NoopOperationAuditLogger());
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            NoReceiptLookupHandler = (_, _) => Task.FromResult(
                new ReceiptReturnProductLookupResult(CreateItem("SKU-LOOKUP", "930001"), "Added no-receipt return item."))
        };
        var session = CreateSession(requester);
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            session,
            () => { },
            cashierSessionContext: cashierContext,
            operationAuthorizationService: authorization)
        {
            IsNoReceiptMode = true,
            ScanText = "930001"
        };

        var lookupTask = viewModel.LookupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => authorization.IsPromptOpen);
        Assert.Equal(Permissions.PosTerminal.Returns.AddNoReceiptItem, authorization.PermissionCode);

        viewModel.Dispose();
        await lookupTask;

        Assert.False(authorization.IsPromptOpen);
        Assert.Empty(authorization.PermissionCode);
        Assert.Equal(0, workflow.NoReceiptLookupCallCount);
        Assert.Empty(viewModel.PendingLines);

        var nextAuthorization = authorization.AuthorizeAsync(
            Permissions.PosTerminal.Returns.AddNoReceiptItem,
            "receipt-returns",
            "next-operation",
            session);
        await WaitUntilAsync(() => authorization.IsPromptOpen);
        authorization.Cancel();
        Assert.Null(await nextAuthorization);
    }

    [Fact]
    public async Task LookupCommand_maps_unexpected_failure_to_visible_status()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupHandler = (_, _) => Task.FromException<ReceiptReturnLookupResult>(
                new InvalidOperationException("Unexpected failure."))
        };
        var viewModel = new ReceiptReturnsViewModel(workflow, CreateSession(), () => { })
        {
            ScanText = "ORDER-001"
        };

        await viewModel.LookupCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.Equal("Order lookup failed. Please retry.", viewModel.StatusMessage);
        Assert.Empty(viewModel.OrderLines);
    }

    [Fact]
    public async Task LookupCommand_clears_previous_return_state_when_next_lookup_fails()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupHandler = (query, _) => query == "ORDER-001"
                ? Task.FromResult(CreateLookupResult())
                : Task.FromException<ReceiptReturnLookupResult>(
                    new InvalidOperationException("Unexpected failure."))
        };
        var viewModel = new ReceiptReturnsViewModel(workflow, CreateSession(), () => { })
        {
            ScanText = "ORDER-001"
        };

        await viewModel.LookupCommand.ExecuteAsync(null);
        viewModel.AddReceiptLineCommand.Execute(viewModel.OrderLines.Single());

        Assert.Single(viewModel.PendingLines);
        Assert.True(viewModel.ConfirmToCartCommand.CanExecute(null));

        viewModel.ScanText = "ORDER-002";
        await viewModel.LookupCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.OrderLines);
        Assert.Empty(viewModel.PendingLines);
        Assert.False(viewModel.ReturnRecordsMayBeStale);
        Assert.Equal("No order loaded", viewModel.OrderSummaryText);
        Assert.Equal("Order lookup failed. Please retry.", viewModel.StatusMessage);
        Assert.False(viewModel.ConfirmToCartCommand.CanExecute(null));
    }

    [Fact]
    public async Task BackCommand_resets_pending_return_state_before_leaving()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupResult = CreateLookupResult()
        };
        var backCalled = false;
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => backCalled = true);

        viewModel.ScanText = "ORDER-001";
        await viewModel.LookupCommand.ExecuteAsync(null);
        viewModel.AddReceiptLineCommand.Execute(viewModel.OrderLines.Single());
        viewModel.IsNoReceiptMode = true;

        viewModel.BackCommand.Execute(null);

        Assert.True(backCalled);
        AssertDefaultState(viewModel);
    }

    [Fact]
    public async Task ConfirmToCart_adds_confirmed_lines_then_resets_pending_return_state()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupResult = CreateLookupResult()
        };
        var backCalled = false;
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => backCalled = true);

        viewModel.ScanText = "ORDER-001";
        await viewModel.LookupCommand.ExecuteAsync(null);
        viewModel.AddReceiptLineCommand.Execute(viewModel.OrderLines.Single());

        viewModel.ConfirmToCartCommand.Execute(null);

        Assert.True(backCalled);
        Assert.Single(workflow.AddedLines);
        AssertDefaultState(viewModel);
    }

    [Fact]
    public void OpenNoReceiptOpenItemDialogCommand_opens_only_in_no_receipt_mode()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        Assert.False(viewModel.OpenNoReceiptOpenItemDialogCommand.CanExecute(null));

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);

        Assert.True(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("Open Item", viewModel.OpenItemDisplayName);
        Assert.Empty(viewModel.OpenItemUnitPriceText);
        Assert.Equal(OpenItemKeyboardTarget.Description, viewModel.OpenItemKeyboardTarget);
        Assert.True(viewModel.IsOpenItemDescriptionKeyboardVisible);
        Assert.False(viewModel.IsOpenItemAmountKeyboardVisible);
    }

    [Fact]
    public void OpenItemKeyboardInputCommand_updates_description_with_letters_space_and_edit_keys()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemKeyboardInputCommand.Execute("Clear");
        viewModel.OpenItemKeyboardInputCommand.Execute("M");
        viewModel.OpenItemKeyboardInputCommand.Execute("I");
        viewModel.OpenItemKeyboardInputCommand.Execute("L");
        viewModel.OpenItemKeyboardInputCommand.Execute("K");
        viewModel.OpenItemKeyboardInputCommand.Execute("Space");
        viewModel.OpenItemKeyboardInputCommand.Execute("A");
        viewModel.OpenItemKeyboardInputCommand.Execute("Back");

        Assert.Equal("MILK ", viewModel.OpenItemDisplayName);
    }

    [Fact]
    public void OpenItemKeyboardInputCommand_updates_amount_with_two_decimal_limit()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.SelectOpenItemAmountKeyboardCommand.Execute(null);
        viewModel.OpenItemKeyboardInputCommand.Execute("1");
        viewModel.OpenItemKeyboardInputCommand.Execute("2");
        viewModel.OpenItemKeyboardInputCommand.Execute(".");
        viewModel.OpenItemKeyboardInputCommand.Execute("3");
        viewModel.OpenItemKeyboardInputCommand.Execute("4");
        viewModel.OpenItemKeyboardInputCommand.Execute("5");

        Assert.Equal("12.34", viewModel.OpenItemUnitPriceText);
        Assert.True(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));

        viewModel.OpenItemKeyboardInputCommand.Execute("Back");
        Assert.Equal("12.3", viewModel.OpenItemUnitPriceText);

        viewModel.OpenItemKeyboardInputCommand.Execute("Clear");
        Assert.Empty(viewModel.OpenItemUnitPriceText);
        Assert.False(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_requires_positive_price()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "0";

        Assert.False(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));

        viewModel.OpenItemUnitPriceText = "12.34";

        Assert.True(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_adds_pending_line_and_closes_dialog()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            OpenItemResult = new ReceiptReturnPendingLineResult(
                CreatePendingOpenItem("Manual Refund", 12.34m),
                "Added no-barcode return item: Manual Refund")
        };
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "12.34";

        viewModel.ConfirmNoReceiptOpenItemCommand.Execute(null);

        var line = Assert.Single(viewModel.PendingLines);
        Assert.Equal("Manual Refund", line.DisplayName);
        Assert.Equal(12.34m, line.UnitPrice);
        Assert.Equal(-12.34m, viewModel.PendingTotal);
        Assert.False(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("Added no-barcode return item: Manual Refund", viewModel.StatusMessage);
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_keeps_dialog_open_when_workflow_rejects()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            OpenItemResult = new ReceiptReturnPendingLineResult(null, "OPENITEM was not found in the local catalog.")
        };
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "12.34";

        viewModel.ConfirmNoReceiptOpenItemCommand.Execute(null);

        Assert.Empty(viewModel.PendingLines);
        Assert.True(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("OPENITEM was not found in the local catalog.", viewModel.StatusMessage);
    }

    private static void AssertDefaultState(ReceiptReturnsViewModel viewModel)
    {
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsNoReceiptMode);
        Assert.Empty(viewModel.OrderLines);
        Assert.Empty(viewModel.PendingLines);
        Assert.False(viewModel.ReturnRecordsMayBeStale);
        Assert.Equal("No order loaded", viewModel.OrderSummaryText);
        Assert.Equal("Scan an order number to start a receipt return.", viewModel.StatusMessage);
    }

    private static PendingReturnLine CreatePendingOpenItem(string displayName, decimal unitPrice)
    {
        return new PendingReturnLine(
            "S001",
            "OPEN-SKU",
            "REF-OPEN",
            displayName,
            "OPENITEM",
            "OPENITEM",
            null,
            1m,
            unitPrice,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            $"noreceipt-open:S001:{Guid.NewGuid():N}",
            null,
            null);
    }

    private static ReceiptReturnLookupResult CreateLookupResult()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        return new ReceiptReturnLookupResult(
            new ReceiptReturnOrder(
                orderGuid,
                "S001",
                "POS-01",
                "Alice",
                DateTimeOffset.UtcNow,
                10m,
                [
                    new ReceiptReturnOrderLine(
                        lineGuid,
                        "SKU-001",
                        "REF-001",
                        "Milk",
                        "690001",
                        "ITEM-001",
                        1m,
                        10m,
                        10m,
                        0m)
                ],
                [],
                []),
            false,
            true,
            "Loaded local order; return records may be stale.");
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C01", "Alice", true, 0);
    }

    private static PosSessionState CreateSession(CashierSessionDto cashier)
    {
        return CreateSession() with
        {
            CashierId = cashier.CashierId,
            CashierName = cashier.CashierName,
            CashierSession = cashier
        };
    }

    private static CashierSessionDto CreateCashier() =>
        new(
            "C01",
            "USER-C01",
            "Alice",
            "S001",
            "POS-01",
            [],
            [],
            ["S001"],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: false);

    private static SellableItemDto CreateItem(string productCode, string lookupCode) =>
        new(
            "S001",
            productCode,
            null,
            "Lookup item",
            lookupCode,
            lookupCode,
            lookupCode,
            1m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            1m,
            DateTimeOffset.UtcNow);

    private sealed class FakeReceiptReturnsWorkflowService : IReceiptReturnsWorkflowService
    {
        public int LookupCallCount { get; private set; }

        public string? LastOrderQuery { get; private set; }

        public ReceiptReturnLookupResult LookupResult { get; init; } = new(null, false, false, "");

        public Func<string, CancellationToken, Task<ReceiptReturnLookupResult>>? LookupHandler { get; init; }

        public Func<string, CancellationToken, Task<ReceiptReturnProductLookupResult>>? NoReceiptLookupHandler { get; init; }

        public int NoReceiptLookupCallCount { get; private set; }

        public List<PendingReturnLine> AddedLines { get; } = [];

        public ReceiptReturnPendingLineResult OpenItemResult { get; init; } = new(null, "");

        public Task<ReceiptReturnLookupResult> LookupOrderAsync(
            PosSessionState session,
            string orderQuery,
            CancellationToken cancellationToken = default)
        {
            LookupCallCount++;
            LastOrderQuery = orderQuery;
            return LookupHandler?.Invoke(orderQuery, cancellationToken)
                ?? Task.FromResult(LookupResult);
        }

        public ReceiptReturnProductLookupResult LookupNoReceiptProduct(
            PosSessionState session,
            string productQuery)
        {
            return new ReceiptReturnProductLookupResult(null, "");
        }

        public Task<ReceiptReturnProductLookupResult> LookupNoReceiptProductAsync(
            PosSessionState session,
            string productQuery,
            CancellationToken cancellationToken = default)
        {
            NoReceiptLookupCallCount++;
            return NoReceiptLookupHandler?.Invoke(productQuery, cancellationToken)
                ?? Task.FromResult(new ReceiptReturnProductLookupResult(null, ""));
        }

        public ReceiptReturnPendingLineResult CreateNoReceiptOpenItem(
            PosSessionState session,
            string displayName,
            decimal unitPrice)
        {
            return OpenItemResult;
        }

        public IReadOnlyList<CartLine> AddReturnLinesToCart(
            IEnumerable<PendingReturnLine> lines,
            IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null)
        {
            AddedLines.AddRange(lines);
            return [];
        }
    }

    private sealed class NoopCashierLoginService : ICashierLoginService
    {
        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CashierLoginResult.Fail("Not configured."));
    }

    private sealed class NoopOperationAuditLogger : IOperationAuditLogger
    {
        public void Record(OperationAuditEventDto auditEvent)
        {
        }
    }
}
