using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>
/// 现金存入 / 取出：记录落库之后，钱箱、打印机、审计任何一步抛异常，都不能被报成「保存失败」。
/// 否则收银员会以为没记上而重录一笔（日结应有现金被多算一次），并且后面的审计也被跳过。
/// </summary>
public sealed class CashMovementViewModelTests
{
    private static readonly PosSessionState Session =
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Confirm_reports_saved_with_warnings_when_drawer_and_printer_throw_after_the_record_is_saved()
    {
        var service = new FakeCashMovementService();
        var audit = new RecordingAuditLogger();
        var statuses = new List<(string Message, bool Succeeded)>();
        var viewModel = CreateViewModel(
            service,
            new FakePrintService { ThrowOnPrint = true },
            () => throw new InvalidOperationException("drawer offline"),
            audit,
            statuses);

        await viewModel.StartCashInCommand.ExecuteAsync(null);
        viewModel.AmountText = "20";
        await viewModel.ConfirmCommand.ExecuteAsync(null);

        // 记录只落库一次，界面不出现「保存失败」，状态栏提示已保存并附上钱箱 / 打印没成功。
        Assert.Equal(1, service.CreateCount);
        Assert.Null(viewModel.ErrorText);
        var status = Assert.Single(statuses);
        Assert.False(status.Succeeded);
        Assert.Contains("cashMovement.status.savedIn", status.Message);
        Assert.Contains("cashMovement.status.drawerFailed", status.Message);
        Assert.Contains("cashMovement.status.printFailed", status.Message);
        Assert.DoesNotContain("saveFailed", status.Message);
        // 善后失败不能吞掉审计：这笔现金进出仍要有一条成功的操作日志。
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(OperationAuditTypes.CashIn, auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
    }

    [Fact]
    public async Task Reprint_reports_failure_in_the_status_bar_instead_of_throwing_when_the_printer_throws()
    {
        var service = new FakeCashMovementService();
        var printService = new FakePrintService();
        var statuses = new List<(string Message, bool Succeeded)>();
        var viewModel = CreateViewModel(service, printService, openCashDrawerAsync: null, auditLogger: null, statuses);
        await viewModel.StartCashInCommand.ExecuteAsync(null);
        viewModel.AmountText = "20";
        await viewModel.ConfirmCommand.ExecuteAsync(null);
        statuses.Clear();
        printService.ThrowOnPrint = true;

        await viewModel.ReprintCommand.ExecuteAsync(Assert.Single(viewModel.Rows));

        var status = Assert.Single(statuses);
        Assert.False(status.Succeeded);
        Assert.Equal("cashMovement.status.printFailed", status.Message);
    }

    private static CashMovementViewModel CreateViewModel(
        FakeCashMovementService service,
        FakePrintService printService,
        Func<Task<ReceiptPrintResult>>? openCashDrawerAsync,
        IOperationAuditLogger? auditLogger,
        List<(string Message, bool Succeeded)> statuses)
    {
        return new CashMovementViewModel(
            service,
            printService,
            () => Session,
            (_, _) => Task.FromResult<ViewModelAuthorizationGrant?>(new ViewModelAuthorizationGrant(null)),
            openCashDrawerAsync,
            loadExpectedCashAsync: null,
            auditLogger,
            key => key,
            (message, succeeded) => statuses.Add((message, succeeded)));
    }

    private sealed class FakeCashMovementService : ICashMovementService
    {
        private readonly List<LocalCashMovement> _movements = [];

        public int CreateCount { get; private set; }

        public DateTime Today => new(2026, 11, 19);

        public Task<CashMovementPeriodSnapshot> GetPeriodAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CashMovementPeriodSnapshot(
                businessDate,
                _movements.ToList(),
                DailyCloseCashMovementTotals.Empty,
                new HashSet<Guid>()));
        }

        public Task<LocalCashMovement> CreateAsync(
            PosSessionState session,
            CashMovementDraft draft,
            CashMovementAuthorizer? authorizer,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            var movement = new LocalCashMovement(
                Guid.NewGuid(),
                session.StoreCode,
                session.DeviceCode,
                CashMovementReasons.NaturalDirection(draft.ReasonCode),
                draft.ReasonCode,
                draft.Amount,
                draft.Note,
                VoidsMovementGuid: null,
                Today,
                new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero),
                session.CashierId,
                session.CashierName,
                AuthorizedById: null,
                AuthorizedByName: null,
                $"CM-{CreateCount:000}");
            _movements.Add(movement);
            return Task.FromResult(movement);
        }

        public Task<LocalCashMovement> VoidAsync(
            PosSessionState session,
            LocalCashMovement original,
            string? note,
            CashMovementAuthorizer? authorizer,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakePrintService : ICashMovementPrintService
    {
        public bool ThrowOnPrint { get; set; }

        public Task<ReceiptPrintResult> PrintAsync(
            LocalCashMovement movement,
            LocalCashMovement? voidedOriginal,
            DailyCloseCashMovementTotals periodTotals,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnPrint)
            {
                throw new InvalidOperationException("printer status read failed");
            }

            return Task.FromResult(new ReceiptPrintResult(true, string.Empty));
        }
    }

    private sealed class RecordingAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }
}
