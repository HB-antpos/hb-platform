using System.Xml.Linq;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

public sealed class NetworkDiagnosticsViewModelTests
{
    private static NetworkMeasurements Healthy() => new()
    {
        AdapterName = "Ethernet",
        Gateway = "192.168.1.1",
        SystemDnsMs = 30,
        SystemDnsAddresses = ["203.0.113.20"],
        PublicDnsMs = 38,
        PublicDnsAddresses = ["203.0.113.20"],
        Ipv4ConnectMs = 46,
        DirectReplyMs = 310,
        SystemProxyReplyMs = 330,
        Reply = ReplyOutcome.Online,
        ClockSkew = TimeSpan.FromSeconds(2)
    };

    private static NetworkDiagnosisReport ReportOf(NetworkMeasurements m) => new(
        "https://hotbargain.vip/pos-api/",
        new DateTimeOffset(2026, 10, 17, 20, 8, 21, TimeSpan.FromHours(11)),
        TimeSpan.FromSeconds(9.4),
        NetworkDiagnosisRules.BuildSteps(m),
        NetworkDiagnosisRules.Evaluate(m),
        m,
        "Windows 10");

    private static string SettingsResource(string file) => Path.Combine(
        FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Resources", file);

    [Fact]
    public async Task Diagnosing_lists_every_step_with_a_readable_finding_and_the_matching_fix()
    {
        var h = new Harness(ReportOf(Healthy() with { SystemDnsMs = 4200 }));

        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.True(h.Vm.HasResult);
        Assert.Equal(10, h.Vm.Steps.Count);
        Assert.Equal("Link to the router", h.Vm.Steps[1].Label);
        Assert.Equal("skipped", h.Vm.Steps[1].StatusKey);
        Assert.Equal("System DNS", h.Vm.Steps[2].Label);
        Assert.Equal("4.2 s", h.Vm.Steps[2].TimeText);
        Assert.Equal("slow", h.Vm.Steps[2].StatusKey);
        Assert.Equal("Connect over IPv6", h.Vm.Steps[5].Label);
        Assert.Equal("skipped", h.Vm.Steps[5].StatusKey);
        Assert.Equal("Connect to a reference server", h.Vm.Steps[6].Label);
        Assert.Equal("This machine's DNS server is slow", h.Vm.FindingTitle);
        Assert.Equal("warn", h.Vm.FindingSeverity);
        Assert.True(h.Vm.HasFix);
        Assert.Equal("Fix DNS (use 1.1.1.1 and 8.8.8.8)", h.Vm.FixButtonText);
        Assert.Equal("Finished in 9.4 s", h.Vm.SummaryText);
        Assert.False(h.Vm.IsRunning);
    }

    [Fact]
    public async Task A_healthy_network_shows_a_success_card_and_no_fix_button()
    {
        var h = new Harness(ReportOf(Healthy()));

        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal("ok", h.Vm.FindingSeverity);
        Assert.Equal("No problem found", h.Vm.FindingTitle);
        Assert.False(h.Vm.HasFix);
        Assert.False(h.Vm.FixCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_the_fix_only_shows_what_will_change_and_changes_nothing_yet()
    {
        var h = new Harness(ReportOf(Healthy() with { SystemDnsMs = 4200 }));
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        h.Vm.FixCommand.Execute(null);

        Assert.True(h.Vm.IsConfirming);
        Assert.Equal("DNS on Ethernet", h.Vm.PlanTarget);
        Assert.Equal("192.168.1.1 (automatic)", h.Vm.PlanBefore);
        Assert.Equal("1.1.1.1, 8.8.8.8", h.Vm.PlanAfter);
        Assert.Contains("Windows will ask for permission", h.Vm.PlanNote);
        Assert.Empty(h.Fixes.Applied);
        Assert.Equal(0, h.Fixes.ApplyCalls);

        h.Vm.CancelFixCommand.Execute(null);
        Assert.False(h.Vm.IsConfirming);
        Assert.Equal(0, h.Fixes.ApplyCalls);
    }

    [Fact]
    public async Task Confirming_applies_the_fix_then_rechecks_and_reports_the_result_with_an_undo_option()
    {
        var h = new Harness(
            ReportOf(Healthy() with { SystemDnsMs = 4200 }),
            ReportOf(Healthy()));
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);
        h.Vm.FixCommand.Execute(null);

        await h.Vm.ConfirmFixCommand.ExecuteAsync(null);

        Assert.Equal(1, h.Fixes.ApplyCalls);
        Assert.Equal(2, h.Diagnostics.RunCalls);
        Assert.False(h.Vm.IsConfirming);
        Assert.Equal("ok", h.Vm.ResultSeverity);
        Assert.StartsWith("Changed. The check passes now", h.Vm.ResultMessage);
        Assert.Equal("ok", h.Vm.FindingSeverity);
        Assert.True(h.Vm.CanUndo);
        Assert.Contains("DNS on Ethernet: automatic → 1.1.1.1, 8.8.8.8", h.Vm.AppliedSummary);
    }

    [Fact]
    public async Task A_change_that_does_not_help_says_so_instead_of_claiming_success()
    {
        var h = new Harness(
            ReportOf(Healthy() with { SystemDnsMs = 4200 }),
            ReportOf(Healthy() with { Reply = ReplyOutcome.Unreachable }));
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);
        h.Vm.FixCommand.Execute(null);

        await h.Vm.ConfirmFixCommand.ExecuteAsync(null);

        Assert.Equal("warn", h.Vm.ResultSeverity);
        Assert.StartsWith("Changed, but the check still finds a problem", h.Vm.ResultMessage);
    }

    [Fact]
    public async Task Declining_the_windows_permission_prompt_leaves_everything_unchanged()
    {
        var h = new Harness(ReportOf(Healthy() with { SystemDnsMs = 4200 }));
        h.Fixes.ApplyResult = NetworkFixResult.UserCancelled;
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);
        h.Vm.FixCommand.Execute(null);

        await h.Vm.ConfirmFixCommand.ExecuteAsync(null);

        Assert.Equal("Nothing was changed: the permission was not granted.", h.Vm.ResultMessage);
        Assert.Equal("warn", h.Vm.ResultSeverity);
        Assert.False(h.Vm.CanUndo);
        Assert.Equal(1, h.Diagnostics.RunCalls);
    }

    [Fact]
    public async Task A_failed_change_shows_the_reason()
    {
        var h = new Harness(ReportOf(Healthy() with { SystemDnsMs = 4200 }));
        h.Fixes.ApplyResult = NetworkFixResult.Failure("exit code 1");
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);
        h.Vm.FixCommand.Execute(null);

        await h.Vm.ConfirmFixCommand.ExecuteAsync(null);

        Assert.Equal("The change could not be applied. exit code 1", h.Vm.ResultMessage);
    }

    [Fact]
    public async Task Undo_restores_and_clears_the_list_of_changes()
    {
        var h = new Harness(
            ReportOf(Healthy() with { SystemDnsMs = 4200 }),
            ReportOf(Healthy()));
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);
        h.Vm.FixCommand.Execute(null);
        await h.Vm.ConfirmFixCommand.ExecuteAsync(null);

        await h.Vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, h.Fixes.UndoCalls);
        Assert.Equal("The changes were undone.", h.Vm.ResultMessage);
        Assert.False(h.Vm.CanUndo);
        Assert.Equal(string.Empty, h.Vm.AppliedSummary);
    }

    [Fact]
    public async Task Copy_report_puts_the_full_report_on_the_clipboard_and_survives_a_busy_clipboard()
    {
        var h = new Harness(ReportOf(Healthy() with { SystemDnsMs = 4200 }));
        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        h.Vm.CopyReportCommand.Execute(null);

        Assert.Contains("HB POS network report", h.Clipboard.Single());
        Assert.Contains("DnsSlow", h.Clipboard.Single());
        Assert.Equal("Report copied.", h.Vm.CopyResultMessage);

        h.ClipboardThrows = true;
        h.Vm.CopyReportCommand.Execute(null);
        Assert.Equal("Could not copy. Try again.", h.Vm.CopyResultMessage);
    }

    [Fact]
    public async Task An_invalid_address_is_reported_without_running_the_check()
    {
        var h = new Harness(ReportOf(Healthy())) { AddressText = "not a url" };

        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(0, h.Diagnostics.RunCalls);
        Assert.False(h.Vm.HasResult);
        Assert.False(string.IsNullOrWhiteSpace(h.Vm.ResultMessage));
    }

    [Fact]
    public async Task A_crash_inside_the_check_shows_a_message_instead_of_a_result()
    {
        var h = new Harness(ReportOf(Healthy())) { Diagnostics = { Throw = new InvalidOperationException("boom") } };

        await h.Vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.False(h.Vm.HasResult);
        Assert.Equal("The network check could not run. Try again.", h.Vm.ResultMessage);
        Assert.False(h.Vm.IsRunning);
    }

    [Fact]
    public void Every_step_finding_and_fix_has_text_in_both_languages()
    {
        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<DiagnosticStepId>().Select(id => $"settings.network.step.{Camel(id)}"));
        foreach (var kind in Enum.GetValues<DiagnosisKind>())
        {
            keys.Add($"settings.network.finding.{Camel(kind)}.title");
            keys.Add($"settings.network.finding.{Camel(kind)}.message");
        }

        keys.AddRange(Enum.GetValues<NetworkFixKind>().Select(kind => $"settings.network.fix.{Camel(kind)}"));
        keys.AddRange(
        [
            "settings.network.diagnose", "settings.network.diagnose.hint", "settings.network.title", "settings.network.running",
            "settings.network.finishedIn", "settings.network.error", "settings.network.ok", "settings.network.skipped",
            "settings.network.fixUnavailable", "settings.network.confirm.title", "settings.network.confirm.now",
            "settings.network.confirm.change", "settings.network.confirm.admin", "settings.network.confirm.currentUser",
            "settings.network.confirm.apply", "settings.network.fixed.passed", "settings.network.fixed.stillFailing",
            "settings.network.fixed.cancelled", "settings.network.fixed.failed", "settings.network.undone",
            "settings.network.undo", "settings.network.applied.title", "settings.network.copy",
            "settings.network.copied", "settings.network.copyFailed"
        ]);

        foreach (var file in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var values = XDocument.Load(SettingsResource(file))
                .Descendants("data")
                .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value);
            foreach (var key in keys)
            {
                Assert.True(values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{file} is missing {key}");
            }
        }
    }

    [Fact]
    public void The_server_address_panel_shows_the_network_check_and_binds_every_command()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Controls", "ApiServerSettingsPanel.xaml"));

        foreach (var binding in new[]
                 {
                     "Network.DiagnoseCommand", "FixCommand", "ConfirmFixCommand", "CancelFixCommand",
                     "UndoCommand", "CopyReportCommand", "HasNetworkDiagnostics", "IsConfirming"
                 })
        {
            Assert.Contains(binding, xaml);
        }
    }

    private static string Camel(Enum value) => char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps", "pos-wpf")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root.");
    }

    private sealed class Harness
    {
        public Harness(params NetworkDiagnosisReport[] reports)
        {
            Diagnostics = new FakeDiagnostics(reports);
            Fixes = new FakeFixes();
            Vm = new NetworkDiagnosticsViewModel(
                Diagnostics,
                Fixes,
                new LocalizationService(),
                () => AddressText,
                text =>
                {
                    if (ClipboardThrows)
                    {
                        throw new InvalidOperationException("clipboard busy");
                    }

                    Clipboard.Add(text);
                });
        }

        public string AddressText { get; set; } = "https://hotbargain.vip/pos-api/";

        public bool ClipboardThrows { get; set; }

        public List<string> Clipboard { get; } = [];

        public FakeDiagnostics Diagnostics { get; }

        public FakeFixes Fixes { get; }

        public NetworkDiagnosticsViewModel Vm { get; }
    }

    private sealed class FakeDiagnostics(NetworkDiagnosisReport[] reports) : INetworkDiagnosticsService
    {
        public int RunCalls { get; private set; }

        public Exception? Throw { get; set; }

        public Task<NetworkDiagnosisReport> RunAsync(Uri apiBaseAddress, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            var report = reports[Math.Min(RunCalls, reports.Length - 1)];
            RunCalls++;
            return Task.FromResult(report);
        }
    }

    private sealed class FakeFixes : INetworkFixService
    {
        public Dictionary<NetworkFixKind, NetworkFixRecord> Applied { get; } = [];

        public int ApplyCalls { get; private set; }

        public int UndoCalls { get; private set; }

        public NetworkFixResult ApplyResult { get; set; } = NetworkFixResult.Done;

        public NetworkFixPlan? Plan(NetworkFixKind kind) => new(
            kind,
            kind == NetworkFixKind.PublicDns ? "DNS on Ethernet" : "Proxy: automatic detection",
            kind == NetworkFixKind.PublicDns ? "192.168.1.1 (automatic)" : "on",
            kind == NetworkFixKind.PublicDns ? "1.1.1.1, 8.8.8.8" : "off",
            kind == NetworkFixKind.PublicDns,
            12,
            "Ethernet",
            ["192.168.1.1"],
            true,
            null);

        public Task<NetworkFixResult> ApplyAsync(NetworkFixPlan plan, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (ApplyResult.Succeeded)
            {
                Applied[plan.Kind] = new NetworkFixRecord(
                    plan.Kind,
                    plan.AdapterIndex,
                    plan.AdapterName,
                    plan.PreviousServers,
                    plan.PreviousAutomatic,
                    plan.PreviousAutoDetect,
                    new DateTimeOffset(2026, 10, 17, 20, 31, 0, TimeSpan.FromHours(11)));
            }

            return Task.FromResult(ApplyResult);
        }

        public NetworkFixRecord? GetApplied(NetworkFixKind kind) => Applied.GetValueOrDefault(kind);

        public Task<NetworkFixResult> UndoAsync(NetworkFixKind kind, CancellationToken cancellationToken = default)
        {
            UndoCalls++;
            Applied.Remove(kind);
            return Task.FromResult(NetworkFixResult.Done);
        }
    }
}
