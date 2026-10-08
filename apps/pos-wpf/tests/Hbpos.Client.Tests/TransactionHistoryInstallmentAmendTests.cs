using System.ComponentModel;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using InstallmentPaymentDto = Hbpos.Contracts.Installments.InstallmentPaymentDto;

namespace Hbpos.Client.Tests;

/// <summary>历史页订单明细弹窗里“修改商品”的视图模型测试：入口矩阵、编辑 / 取消 / 关闭语义、授权保存与失败处理。</summary>
public sealed class TransactionHistoryInstallmentAmendTests
{
    private static readonly Guid OrderGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid LineGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    // 取“几分钟前”而不是几小时前：历史页默认只查今天，临近零点时不能把测试订单筛掉。
    private static readonly DateTimeOffset T0 = DateTimeOffset.Now.AddMinutes(-10);

    [Theory]
    [InlineData(InstallmentStatus.Active, true, false, false, true)]
    [InlineData(InstallmentStatus.PaidOff, true, false, false, true)]
    [InlineData(InstallmentStatus.Cancelled, true, false, false, false)]
    [InlineData(InstallmentStatus.PickedUp, true, false, false, false)]
    [InlineData(InstallmentStatus.Active, false, false, false, false)]
    [InlineData(InstallmentStatus.Active, true, true, false, false)]
    [InlineData(InstallmentStatus.Active, true, false, true, false)]
    public async Task Edit_button_visibility_follows_status_connectivity_and_recovery_locks(
        InstallmentStatus status,
        bool isOnline,
        bool orderIsLocked,
        bool lockStateUnknown,
        bool expectedVisible)
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions
        {
            Status = status,
            IsOnline = isOnline,
            OrderIsLocked = orderIsLocked,
            LockStateUnknown = lockStateUnknown
        });

        Assert.Equal(expectedVisible, fixture.ViewModel.IsEditInstallmentLinesVisible);
        Assert.Equal(expectedVisible, fixture.ViewModel.BeginEditInstallmentLinesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_button_is_hidden_for_regular_orders()
    {
        var service = new FakeInstallmentOrderService();
        using var viewModel = new TransactionHistoryViewModel(
            new EmptyReceiptQueryService(),
            null,
            null,
            Session(),
            installmentOrderService: service);
        var regular = new HistoryOrderListItem(
            Guid.NewGuid(),
            TransactionHistorySource.LocalOrders,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.Now,
            10m,
            0m,
            10m,
            1,
            "Cash",
            "Paid");
        viewModel.Orders.Add(regular);
        viewModel.SelectedOrder = regular;
        await WaitUntilAsync(() => !viewModel.IsReceiptPreviewLoading);

        Assert.False(viewModel.IsEditInstallmentLinesVisible);
        Assert.False(viewModel.BeginEditInstallmentLinesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Begin_edit_checks_server_support_then_enters_edit_mode_with_a_seeded_editor()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        Assert.True(viewModel.IsContinueInstallmentPaymentVisible);

        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Service.CapabilityCheckCount);
        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsInstallmentLinesReadOnlyMode);
        var editor = Assert.IsType<InstallmentLinesEditor>(viewModel.InstallmentEditor);
        Assert.Single(editor.Rows);
        Assert.Equal(30m, editor.PaidAmount);
        Assert.Equal(120m, editor.OriginalTotal);
        // 编辑期间不能跳去收款，也不能再次点进编辑。
        Assert.False(viewModel.IsContinueInstallmentPaymentVisible);
        Assert.False(viewModel.IsEditInstallmentLinesVisible);
        Assert.False(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
        Assert.True(viewModel.CancelInstallmentLinesEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Begin_edit_stays_read_only_with_a_notice_when_the_server_does_not_support_it()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { AmendSupported = false });

        await fixture.ViewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.IsEditingInstallmentLines);
        Assert.Null(fixture.ViewModel.InstallmentEditor);
        Assert.True(fixture.ViewModel.HasInstallmentEditorNotice);
        Assert.True(fixture.ViewModel.IsInstallmentEditorNoticeError);
        Assert.Contains("does not support", fixture.ViewModel.InstallmentEditorNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Begin_edit_is_abandoned_when_the_capability_check_throws()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { CapabilityException = new HttpRequestException("offline") });

        await fixture.ViewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.IsEditingInstallmentLines);
        Assert.True(fixture.ViewModel.IsInstallmentEditorNoticeError);
    }

    [Fact]
    public async Task Close_exits_clean_edit_mode_first_then_closes_the_dialog()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        viewModel.CloseOrderDetailsCommand.Execute(null);

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.True(viewModel.IsOrderDetailsOpen);

        viewModel.CloseOrderDetailsCommand.Execute(null);

        Assert.False(viewModel.IsOrderDetailsOpen);
    }

    [Fact]
    public async Task Close_with_unsaved_changes_keeps_editing_and_asks_for_an_explicit_cancel()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);

        viewModel.CloseOrderDetailsCommand.Execute(null);

        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.Contains("unsaved", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);

        viewModel.CancelInstallmentLinesEditCommand.Execute(null);

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.Null(viewModel.InstallmentEditor);
        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.Equal(string.Empty, viewModel.InstallmentEditorMessage);
    }

    [Fact]
    public async Task Closing_the_dialog_by_any_other_path_discards_the_edit_session()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);

        viewModel.IsOrderDetailsOpen = false;

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.Null(viewModel.InstallmentEditor);
    }

    [Fact]
    public async Task Selecting_another_order_discards_the_edit_session()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { ExtraOrderCount = 1 });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEditingInstallmentLines);

        viewModel.SelectedOrder = viewModel.Orders.First(order => order.OrderGuid != OrderGuid);

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.Null(viewModel.InstallmentEditor);
    }

    [Fact]
    public async Task Save_is_only_enabled_when_the_editor_has_valid_changes()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        var row = viewModel.InstallmentEditor!.Rows[0];
        var canExecuteChanged = 0;
        viewModel.SaveInstallmentLinesCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        Assert.False(viewModel.SaveInstallmentLinesCommand.CanExecute(null));

        row.IncrementCommand.Execute(null);
        Assert.True(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);

        // 新总额 20 < 已付 30（且低于 50 下限）：不可保存，并给出红色提示。
        row.UnitPriceText = "10";
        row.QuantityText = "2";
        Assert.False(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
        Assert.Contains("cannot be lower than", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hint_announces_paid_off_for_an_order_whose_paid_amount_reaches_the_new_total()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Paid = 80m });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        viewModel.InstallmentEditor!.Rows[0].UnitPriceText = "80";

        Assert.True(viewModel.InstallmentEditor.WillBePaidOff);
        Assert.Contains("paid off", viewModel.InstallmentEditorHint, StringComparison.Ordinal);
        Assert.True(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_authorizes_with_the_amend_permission_submits_baseline_and_refreshes_the_list_row()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        var originalRow = viewModel.SelectedOrder!;
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        var authorization = Assert.Single(fixture.Authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.Installments.AmendLines, authorization.Permission);
        Assert.Equal("transaction-history", authorization.Screen);
        Assert.Equal("amend-installment-lines", authorization.Action);

        var call = Assert.Single(fixture.Service.AmendCalls);
        Assert.Equal(T0, call.Baseline.UpdatedAt);
        Assert.Equal(2m, Assert.Single(call.Lines).Quantity);
        Assert.Equal(240m, call.Lines.Sum(line => line.ActualAmount));

        // 编辑态结束，弹窗仍打开，详情已按服务端最新订单重载。
        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsSavingInstallmentLines);
        Assert.True(viewModel.IsOrderDetailsOpen);
        Assert.Equal(2m, Assert.Single(viewModel.OrderDetailLinesForTests).Quantity);
        Assert.Equal(210m, viewModel.OrderDetailsOutstandingAmount);
        Assert.Equal(30m, viewModel.OrderDetailsPaidAmount);

        // 列表行已换成新对象，金额 / 状态随之更新，且仍是当前选中行。
        var row = Assert.Single(viewModel.Orders);
        Assert.NotSame(originalRow, row);
        Assert.Same(row, viewModel.SelectedOrder);
        Assert.Equal(240m, row.TotalAmount);
        Assert.Equal(210m, row.InstallmentOrder!.OutstandingAmount);
        Assert.True(row.CanContinueInstallmentPayment);

        Assert.True(viewModel.HasInstallmentEditorNotice);
        Assert.False(viewModel.IsInstallmentEditorNoticeError);
        Assert.Contains("210.00", viewModel.InstallmentEditorNotice, StringComparison.Ordinal);
        Assert.Equal(viewModel.InstallmentEditorNotice, viewModel.StatusMessage);

        var audit = Assert.Single(fixture.Audit.Events, auditEvent => auditEvent.OperationType == OperationAuditTypes.InstallmentLinesAmend);
        Assert.Equal("Succeeded", audit.Outcome);
        Assert.Equal(OrderGuid.ToString("D"), audit.OrderGuid);
        Assert.Equal(120m, audit.BeforeActual);
        Assert.Equal(240m, audit.AfterActual);
        Assert.Equal(120m, audit.AmountDelta);
        var item = Assert.Single(audit.Items);
        Assert.Equal(1m, item.BeforeQuantity);
        Assert.Equal(2m, item.AfterQuantity);
    }

    [Fact]
    public async Task Save_that_pays_the_order_off_shows_the_pickup_ready_message_and_enables_pickup()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Paid = 80m });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].UnitPriceText = "80";

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Contains("ready for pickup", viewModel.InstallmentEditorNotice, StringComparison.Ordinal);
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanConfirmInstallmentPickup);
        Assert.False(row.CanContinueInstallmentPayment);
        Assert.False(viewModel.IsContinueInstallmentPaymentVisible);
        Assert.Equal(InstallmentStatus.PaidOff, fixture.Service.Store[OrderGuid].Status);
    }

    [Fact]
    public async Task Save_without_authorization_never_calls_the_service_and_keeps_editing()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        fixture.Authorization.Deny = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Service.AmendCalls);
        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsSavingInstallmentLines);
        Assert.True(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_without_the_permission_is_denied_audited_and_explained_in_the_editor()
    {
        var cashier = new CashierSessionDto(
            "C001",
            "user-1",
            "Alice",
            "S001",
            "POS-01",
            [],
            [Permissions.PosTerminal.Installments.View],
            ["S001"],
            false,
            false,
            false);
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Cashier = cashier, UseAuthorizationService = false });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Service.AmendCalls);
        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.Contains("没有修改分期商品权限", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
        var audit = Assert.Single(fixture.Audit.Events);
        Assert.Equal(OperationAuditTypes.InstallmentLinesAmend, audit.OperationType);
        Assert.Equal("Denied", audit.Outcome);
        Assert.Equal("PERMISSION_DENIED", audit.ReasonCode);
    }

    [Fact]
    public async Task Save_is_abandoned_when_the_order_changes_while_waiting_for_authorization()
    {
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { ExtraOrderCount = 1 });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Authorization.BeforeReturn = () =>
            viewModel.SelectedOrder = viewModel.Orders.First(order => order.OrderGuid != OrderGuid);

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Service.AmendCalls);
        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsSavingInstallmentLines);
    }

    [Fact]
    public async Task Save_is_abandoned_when_connectivity_is_lost_while_waiting_for_authorization()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Authorization.BeforeReturn = () => viewModel.Session = viewModel.Session with { IsOnline = false };

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Service.AmendCalls);
        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.Contains("try again", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_save_keeps_editing_with_a_localized_message_and_audits_the_failure()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Service.AmendHandler = (_, _) => InstallmentAmendLinesResult.Create(
            InstallmentAmendLinesOutcome.Rejected,
            "server said no",
            InstallmentAmendLinesErrorCodes.TotalBelowPaid);

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsSavingInstallmentLines);
        Assert.Contains("cannot be lower than the paid amount", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
        var audit = Assert.Single(fixture.Audit.Events);
        Assert.Equal("Failed", audit.Outcome);
        Assert.Equal(InstallmentAmendLinesErrorCodes.TotalBelowPaid, audit.ReasonCode);
        // 失败后允许继续改，改动会清掉旧错误并重新启用保存。
        viewModel.InstallmentEditor.Rows[0].IncrementCommand.Execute(null);
        Assert.DoesNotContain("cannot be lower than the paid amount", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.SaveInstallmentLinesCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(InstallmentAmendLinesOutcome.OnlineRequired, "online connection")]
    [InlineData(InstallmentAmendLinesOutcome.Unknown, "could not be confirmed")]
    [InlineData(InstallmentAmendLinesOutcome.Failed, "Save failed")]
    public async Task Non_success_outcomes_keep_editing_and_explain_what_happened(
        InstallmentAmendLinesOutcome outcome,
        string expectedFragment)
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Service.AmendHandler = (_, _) => InstallmentAmendLinesResult.Create(outcome, "raw message");

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.Contains(expectedFragment, viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
        Assert.Equal("Failed", Assert.Single(fixture.Audit.Events).Outcome);
    }

    [Fact]
    public async Task Service_exception_keeps_editing_and_is_reported_in_the_editor()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Service.AmendException = new InvalidOperationException("boom");

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEditingInstallmentLines);
        Assert.False(viewModel.IsSavingInstallmentLines);
        Assert.Contains("boom", viewModel.InstallmentEditorMessage, StringComparison.Ordinal);
        Assert.Equal("Failed", Assert.Single(fixture.Audit.Events).Outcome);
    }

    [Fact]
    public async Task Stale_result_leaves_edit_mode_refreshes_the_order_and_asks_to_edit_again()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        // 别的设备已把订单改成 5 件：服务层刷新快照并返回 Stale。
        fixture.Service.AmendHandler = (service, _) =>
        {
            var changed = service.Rebuild(OrderGuid, [Line(5m, 120m)], T0.AddMinutes(30));
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Stale,
                "stale",
                InstallmentAmendLinesErrorCodes.Stale,
                changed,
                FakeInstallmentOrderService.ToSummary(changed));
        };

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.True(viewModel.IsInstallmentEditorNoticeError);
        Assert.Contains("another device", viewModel.InstallmentEditorNotice, StringComparison.Ordinal);
        Assert.Equal(5m, Assert.Single(viewModel.OrderDetailLinesForTests).Quantity);
        Assert.Equal(600m, Assert.Single(viewModel.Orders).TotalAmount);
        Assert.Equal("Failed", Assert.Single(fixture.Audit.Events).Outcome);
        // 可以基于最新订单重新进入编辑。
        Assert.True(viewModel.IsEditInstallmentLinesVisible);
    }

    [Fact]
    public async Task Status_no_longer_allowed_leaves_edit_mode_and_reflects_the_new_status()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        viewModel.InstallmentEditor!.Rows[0].IncrementCommand.Execute(null);
        fixture.Service.AmendHandler = (service, _) =>
        {
            var cancelled = service.Rebuild(OrderGuid, [Line(1m, 120m)], T0.AddMinutes(5), InstallmentStatus.Cancelled);
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Rejected,
                "status",
                InstallmentAmendLinesErrorCodes.StatusNotAllowed,
                cancelled,
                FakeInstallmentOrderService.ToSummary(cancelled));
        };

        await viewModel.SaveInstallmentLinesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditingInstallmentLines);
        Assert.True(viewModel.IsInstallmentEditorNoticeError);
        Assert.False(viewModel.IsEditInstallmentLinesVisible);
    }

    [Fact]
    public async Task Search_adds_a_single_exact_match_directly_and_lists_candidates_otherwise()
    {
        var search = new FakeProductSearch(query => query switch
        {
            "930009" => new InstallmentLineProductSearchResult([Item("P9", "930009", "Green Tea")], IsExactMatch: true),
            "tea" => new InstallmentLineProductSearchResult(
                [Item("P9", "930009", "Green Tea"), Item("P8", "930008", "Black Tea")],
                IsExactMatch: false),
            "oolong" => new InstallmentLineProductSearchResult([Item("P7", "930007", "Oolong")], IsExactMatch: false),
            _ => InstallmentLineProductSearchResult.Empty
        });
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Search = search });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        var editor = viewModel.InstallmentEditor!;

        // 1) 编码精确且唯一：直接加行，输入框清空。
        viewModel.InstallmentLineSearchText = "930009";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.Rows.Count);
        Assert.Equal("930009", editor.Rows[1].LookupCode);
        Assert.Equal(string.Empty, viewModel.InstallmentLineSearchText);
        Assert.False(viewModel.HasInstallmentLineSearchResults);
        Assert.Equal("S001", search.Calls[0].StoreCode);

        // 2) 名称模糊：出候选，点选后才加入。
        viewModel.InstallmentLineSearchText = "tea";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.InstallmentLineSearchResults.Count);
        Assert.True(viewModel.HasInstallmentLineSearchResults);
        Assert.Equal(2, editor.Rows.Count);
        viewModel.AddInstallmentLineProductCommand.Execute(viewModel.InstallmentLineSearchResults[1]);
        Assert.Equal(3, editor.Rows.Count);
        Assert.False(viewModel.HasInstallmentLineSearchResults);
        Assert.Equal(string.Empty, viewModel.InstallmentLineSearchText);

        // 3) 模糊且只有一条也不自动加入。
        viewModel.InstallmentLineSearchText = "oolong";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);
        Assert.Single(viewModel.InstallmentLineSearchResults);
        Assert.Equal(3, editor.Rows.Count);

        // 4) 没有结果：提示未找到。
        viewModel.InstallmentLineSearchText = "nothing";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasInstallmentLineSearchResults);
        Assert.Contains("No matching product", viewModel.InstallmentLineSearchMessage, StringComparison.Ordinal);

        // 清空输入关闭提示。
        viewModel.InstallmentLineSearchText = string.Empty;
        Assert.Equal(string.Empty, viewModel.InstallmentLineSearchMessage);
    }

    [Fact]
    public async Task Search_failure_is_reported_without_breaking_the_editor()
    {
        var search = new FakeProductSearch(_ => throw new InvalidOperationException("index offline"));
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Search = search });
        var viewModel = fixture.ViewModel;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        viewModel.InstallmentLineSearchText = "tea";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);

        Assert.Contains("index offline", viewModel.InstallmentLineSearchMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsSearchingInstallmentProducts);
        Assert.True(viewModel.IsEditingInstallmentLines);
    }

    [Fact]
    public async Task Search_and_add_are_ignored_outside_edit_mode()
    {
        var search = new FakeProductSearch(_ => new InstallmentLineProductSearchResult([Item("P9", "930009", "Tea")], true));
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Search = search });
        var viewModel = fixture.ViewModel;

        viewModel.InstallmentLineSearchText = "930009";
        await viewModel.SearchInstallmentLineProductsCommand.ExecuteAsync(null);
        viewModel.AddInstallmentLineProductCommand.Execute(Item("P9", "930009", "Tea"));

        Assert.Empty(search.Calls);
        Assert.Null(viewModel.InstallmentEditor);
    }

    [Fact]
    public async Task Scanning_while_editing_adds_the_product_instead_of_changing_the_history_search()
    {
        var search = new FakeProductSearch(query => query == "930009"
            ? new InstallmentLineProductSearchResult([Item("P9", "930009", "Green Tea")], true)
            : InstallmentLineProductSearchResult.Empty);
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Search = search });
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);
        var historySearchBefore = viewModel.SearchText;

        var consumed = viewModel.ProcessScannerBarcode("930009", "device", "test");

        Assert.True(consumed);
        await WaitUntilAsync(() => viewModel.InstallmentEditor!.Rows.Count == 2);
        Assert.Equal(historySearchBefore, viewModel.SearchText);
        Assert.Equal("930009", viewModel.InstallmentEditor!.Rows[1].LookupCode);
    }

    [Fact]
    public async Task Scanning_with_the_dialog_open_but_not_editing_is_still_swallowed()
    {
        var search = new FakeProductSearch(_ => InstallmentLineProductSearchResult.Empty);
        using var fixture = await Fixture.CreateAsync(new FixtureOptions { Search = search });
        var viewModel = fixture.ViewModel;
        viewModel.IsOrderDetailsOpen = true;
        var historySearchBefore = viewModel.SearchText;

        var consumed = viewModel.ProcessScannerBarcode("930009", "device", "test");

        Assert.True(consumed);
        Assert.Equal(historySearchBefore, viewModel.SearchText);
        Assert.Empty(search.Calls);
    }

    [Fact]
    public async Task Editing_flags_notify_the_view_for_every_dependent_property()
    {
        using var fixture = await Fixture.CreateAsync();
        var viewModel = fixture.ViewModel;
        var raised = new HashSet<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        await viewModel.BeginEditInstallmentLinesCommand.ExecuteAsync(null);

        Assert.Contains(nameof(TransactionHistoryViewModel.IsEditingInstallmentLines), raised);
        Assert.Contains(nameof(TransactionHistoryViewModel.IsInstallmentLinesReadOnlyMode), raised);
        Assert.Contains(nameof(TransactionHistoryViewModel.IsEditInstallmentLinesVisible), raised);
        Assert.Contains(nameof(TransactionHistoryViewModel.IsContinueInstallmentPaymentVisible), raised);
        Assert.Contains(nameof(TransactionHistoryViewModel.InstallmentEditor), raised);
    }

    private static PosSessionState Session(bool isOnline = true, CashierSessionDto? cashier = null) =>
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", isOnline, 0, cashier);

    private static InstallmentLineDto Line(decimal quantity, decimal unitPrice) => new(
        LineGuid,
        "SKU-001",
        null,
        "Premium Rice Cooker",
        "690001",
        quantity,
        unitPrice,
        0m,
        InstallmentAmendRules.CalculateActualAmount(quantity, unitPrice, 0m),
        "ITEM-001");

    private static SellableItemDto Item(string productCode, string lookupCode, string name) => new(
        "S001",
        productCode,
        null,
        name,
        lookupCode,
        $"ITEM-{productCode}",
        lookupCode,
        10m,
        PriceSourceKind.ProductBase,
        "Product",
        1m,
        DateTimeOffset.UtcNow);

    private sealed class FixtureOptions
    {
        public InstallmentStatus Status { get; init; } = InstallmentStatus.Active;

        public bool IsOnline { get; init; } = true;

        public bool OrderIsLocked { get; init; }

        public bool LockStateUnknown { get; init; }

        public bool AmendSupported { get; init; } = true;

        public Exception? CapabilityException { get; init; }

        public decimal Paid { get; init; } = 30m;

        public int ExtraOrderCount { get; init; }

        public IInstallmentLineProductSearch? Search { get; init; }

        public CashierSessionDto? Cashier { get; init; }

        public bool UseAuthorizationService { get; init; } = true;
    }

    /// <summary>已加载好分期来源与首单详情的视图模型 + 假服务。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(
            TransactionHistoryViewModel viewModel,
            FakeInstallmentOrderService service,
            RecordingAuthorizationService authorization,
            RecordingAuditLogger audit)
        {
            ViewModel = viewModel;
            Service = service;
            Authorization = authorization;
            Audit = audit;
        }

        public TransactionHistoryViewModel ViewModel { get; }

        public FakeInstallmentOrderService Service { get; }

        public RecordingAuthorizationService Authorization { get; }

        public RecordingAuditLogger Audit { get; }

        public static async Task<Fixture> CreateAsync(FixtureOptions? options = null)
        {
            options ??= new FixtureOptions();
            var service = new FakeInstallmentOrderService
            {
                AmendSupported = options.AmendSupported,
                CapabilityException = options.CapabilityException
            };
            var status = options.Status;
            var paid = status == InstallmentStatus.PaidOff ? 120m : options.Paid;
            service.Add(OrderGuid, [Line(1m, 120m)], T0, status, paid);
            for (var index = 0; index < options.ExtraOrderCount; index++)
            {
                service.Add(Guid.NewGuid(), [Line(1m, 100m)], T0.AddMinutes(-index - 1), InstallmentStatus.Active, 30m);
            }

            if (options.OrderIsLocked)
            {
                service.LockedInstallments = new HashSet<Guid> { OrderGuid };
            }

            if (options.LockStateUnknown)
            {
                service.LockedInstallmentsException = new InvalidOperationException("lock lookup failed");
            }

            var authorization = new RecordingAuthorizationService();
            var audit = new RecordingAuditLogger();
            var viewModel = new TransactionHistoryViewModel(
                new EmptyReceiptQueryService(),
                null,
                null,
                Session(options.IsOnline, options.Cashier),
                installmentOrderService: service,
                continueInstallmentPaymentAsync: _ => Task.CompletedTask,
                operationAuditLogger: audit,
                operationAuthorizationService: options.UseAuthorizationService ? authorization : null,
                enforcePermissionsWhenNoCashier: options.Cashier is not null,
                installmentLineProductSearch: options.Search);

            viewModel.IsInstallmentSourceSelected = true;
            await viewModel.LoadAsync();
            // 列表选中首行（按 UpdatedAt 倒序，主订单最新）；等详情加载稳定后再断言。
            await WaitUntilAsync(() =>
                viewModel.SelectedOrder?.OrderGuid == OrderGuid &&
                viewModel.SelectedReceipt is not null &&
                !viewModel.IsReceiptPreviewLoading);
            return new Fixture(viewModel, service, authorization, audit);
        }

        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class EmptyReceiptQueryService : IReceiptQueryService
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

    private sealed class RecordingAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent) => Events.Add(auditEvent);
    }

    private sealed class FakeProductSearch(Func<string, InstallmentLineProductSearchResult> handler) : IInstallmentLineProductSearch
    {
        public List<(string StoreCode, string Query)> Calls { get; } = [];

        public Task<InstallmentLineProductSearchResult> SearchAsync(
            string storeCode,
            string query,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((storeCode, query));
            try
            {
                return Task.FromResult(handler(query));
            }
            catch (Exception ex)
            {
                return Task.FromException<InstallmentLineProductSearchResult>(ex);
            }
        }
    }

    private sealed class RecordingAuthorizationService : IOperationAuthorizationService
    {
        public List<(string Permission, string Screen, string Action)> Requests { get; } = [];

        public bool Deny { get; set; }

        public Action? BeforeReturn { get; set; }

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

        public string ScannerPageId => "installment-amend-authorization-test";

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
            Requests.Add((permissionCode, screen, action));
            BeforeReturn?.Invoke();
            if (Deny)
            {
                return Task.FromResult<OperationAuthorizationScope?>(null);
            }

            var supervisor = new CashierSessionDto(
                "S100",
                "user-s",
                "Supervisor",
                "S001",
                "POS-01",
                [],
                [permissionCode],
                ["S001"],
                false,
                false,
                false);
            return Task.FromResult<OperationAuthorizationScope?>(
                new OperationAuthorizationScope(supervisor, permissionCode, screen, action));
        }

        public bool ProcessScannerBarcode(string barcode) => false;

        public void Cancel()
        {
        }

        public void RevokeAll()
        {
        }
    }

    /// <summary>
    /// 内存版分期服务：保存订单状态，GetOrderDetailsAsync 总返回当前版本，
    /// 默认的 AmendLinesAsync 会真实改写订单（与服务端“整体替换商品行并重算状态”一致）。
    /// </summary>
    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public Dictionary<Guid, LocalInstallmentOrder> Store { get; } = [];

        public IReadOnlySet<Guid> LockedInstallments { get; set; } = new HashSet<Guid>();

        public Exception? LockedInstallmentsException { get; set; }

        public bool AmendSupported { get; init; } = true;

        public Exception? CapabilityException { get; init; }

        public int CapabilityCheckCount { get; private set; }

        public List<(PosSessionState Session, LocalInstallmentOrder Baseline, IReadOnlyList<InstallmentLineDto> Lines)> AmendCalls { get; } = [];

        public Func<FakeInstallmentOrderService, IReadOnlyList<InstallmentLineDto>, InstallmentAmendLinesResult>? AmendHandler { get; set; }

        public Exception? AmendException { get; set; }

        public void Add(Guid guid, IReadOnlyList<InstallmentLineDto> lines, DateTimeOffset updatedAt, InstallmentStatus status, decimal paid)
        {
            var total = InstallmentAmendRules.CalculateTotal(lines);
            Store[guid] = new LocalInstallmentOrder(
                guid,
                guid,
                $"IO-{guid.ToString("N")[..6].ToUpperInvariant()}",
                "S001",
                "POS-01",
                "C001",
                "Alice",
                "张三",
                "0400111222",
                updatedAt.AddHours(-1),
                updatedAt,
                total,
                20m,
                paid,
                paid,
                InstallmentAmendRules.CalculateBalance(total, paid),
                status,
                lines,
                [
                    new InstallmentPaymentDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Cash,
                        paid,
                        null,
                        InstallmentPaymentStatus.Recorded,
                        updatedAt.AddHours(-1),
                        "C001",
                        "POS-01")
                ],
                null);
        }

        public LocalInstallmentOrder Rebuild(
            Guid guid,
            IReadOnlyList<InstallmentLineDto> lines,
            DateTimeOffset updatedAt,
            InstallmentStatus? status = null)
        {
            var current = Store[guid];
            var total = InstallmentAmendRules.CalculateTotal(lines);
            var rebuilt = current with
            {
                Lines = lines,
                TotalAmount = total,
                BalanceAmount = InstallmentAmendRules.CalculateBalance(total, current.PaidAmount),
                Status = status ?? InstallmentAmendRules.ResolveStatus(total, current.PaidAmount),
                UpdatedAt = updatedAt
            };
            Store[guid] = rebuilt;
            return rebuilt;
        }

        public static InstallmentOrderSummary ToSummary(LocalInstallmentOrder order) => new(
            order.InstallmentGuid,
            order.InstallmentNumber,
            order.CustomerName,
            order.CustomerPhone,
            order.TotalAmount,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            0,
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            order.Status == InstallmentStatus.PaidOff,
            order.Status is InstallmentStatus.Active or InstallmentStatus.PaidOff,
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            order.Status.ToString(),
            order.DeviceCode,
            order.UpdatedAt);

        public Task<InstallmentAmendLinesResult> AmendLinesAsync(
            PosSessionState session,
            LocalInstallmentOrder baseline,
            IReadOnlyList<InstallmentLineDto> lines,
            CancellationToken cancellationToken = default)
        {
            AmendCalls.Add((session, baseline, lines));
            if (AmendException is not null)
            {
                return Task.FromException<InstallmentAmendLinesResult>(AmendException);
            }

            if (AmendHandler is not null)
            {
                return Task.FromResult(AmendHandler(this, lines));
            }

            var updated = Rebuild(baseline.InstallmentGuid, lines, baseline.UpdatedAt.AddMinutes(1));
            return Task.FromResult(InstallmentAmendLinesResult.Success(updated, ToSummary(updated)));
        }

        public Task<bool> IsAmendLinesSupportedAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            CapabilityCheckCount++;
            return CapabilityException is not null
                ? Task.FromException<bool>(CapabilityException)
                : Task.FromResult(AmendSupported);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> QueryHistoryAsync(
            PosSessionState session,
            InstallmentHistorySearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>(
                Store.Values.OrderByDescending(order => order.UpdatedAt).Select(ToSummary).ToList());

        public Task<LocalInstallmentOrder?> GetOrderDetailsAsync(
            PosSessionState session,
            Guid installmentGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.GetValueOrDefault(installmentGuid));

        public Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
            LockedInstallmentsException is null
                ? Task.FromResult(LockedInstallments)
                : Task.FromException<IReadOnlySet<Guid>>(LockedInstallmentsException);

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>(Store.Values.Select(ToSummary).ToList());

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default) =>
            GetOrdersAsync(session, cancellationToken);

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.GetValueOrDefault(installmentGuid));

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
