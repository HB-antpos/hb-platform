using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Tests;

[Collection(ShutdownTimingTestCollection.Name)]
public sealed class RemoteMaintenanceServiceTests
{
    [Theory]
    [InlineData(RemoteMaintenanceSetupError.ComponentsMissing, "componentsMissing")]
    [InlineData(RemoteMaintenanceSetupError.InstallationLocationInvalid, "installationLocationInvalid")]
    [InlineData(RemoteMaintenanceSetupError.InstallationPermissionsInvalid, "installationPermissionsInvalid")]
    public async Task Invalid_install_environment_stops_before_prepare_download_and_uac(RemoteMaintenanceSetupError error, string message)
    {
        using var fixture = new Fixture();
        fixture.Launcher.ValidationFailure = new RemoteMaintenanceSetupException(error);
        fixture.Launcher.InstallWritesId = true;
        var stages = new List<RemoteMaintenanceStage>();

        var result = await fixture.Service.InstallAsync(fixture.Session, progress: new RecordingProgress(stages));

        Assert.Empty(fixture.Api.PreparedOperations);
        Assert.Equal(0, fixture.Downloader.VerifiedCount);
        Assert.Empty(fixture.Launcher.Stages);
        Assert.Equal(new[] { RemoteMaintenanceStage.Preparing }, stages);
        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result." + message, result.Message);
    }

    [Theory]
    [InlineData(RemoteMaintenanceSetupError.ComponentsMissing, "componentsMissing")]
    [InlineData(RemoteMaintenanceSetupError.InstallationLocationInvalid, "installationLocationInvalid")]
    [InlineData(RemoteMaintenanceSetupError.InstallationPermissionsInvalid, "installationPermissionsInvalid")]
    public async Task Elevated_precheck_failure_keeps_specific_install_guidance(RemoteMaintenanceSetupError error, string message)
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallExitCode = (int)error;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result." + message, result.Message);
        Assert.Empty(fixture.Api.CommittedOperations);
        var cleanup = Assert.Single(fixture.Launcher.FailClosedRequests);
        Assert.False(cleanup.RustDeskInstalled);
        Assert.False(cleanup.StatusAgentInstalled);
    }

    [Theory]
    [InlineData(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, "existingRustDeskUnmanaged", false)]
    [InlineData(RemoteMaintenanceSetupError.RustDeskSetupIncomplete, "rustDeskSetupIncomplete", true)]
    [InlineData(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, "rustDeskConfigurationFailed", true)]
    public async Task Elevated_install_step_failure_shows_actionable_guidance(
        RemoteMaintenanceSetupError error, string message, bool rustDeskTouched)
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallExitCode = (int)error;
        fixture.Launcher.InstallRustDeskInstalled = rustDeskTouched;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result." + message, result.Message);
        Assert.Empty(fixture.Api.CommittedOperations);
        // 外部 RustDesk 由预检查拒绝，不能被清理步骤停掉；本次触及的才需要停用。
        var cleanup = Assert.Single(fixture.Launcher.FailClosedRequests);
        Assert.Equal(rustDeskTouched, cleanup.RustDeskInstalled);
    }

    [Fact]
    public async Task Fresh_install_reads_id_written_by_elevated_helper_before_commit()
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallWritesId = true;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.configured", result.Message);
        Assert.Equal("rustdesk-123", fixture.Api.CommittedRustdeskIds.Single());
        Assert.Equal(new[] { "install", "configure" }, fixture.Launcher.Stages);
        Assert.Equal(1, fixture.Launcher.ValidationCount);
    }

    [Fact]
    public async Task Prepared_retry_reuses_operation_and_password_and_does_not_reinstall_twice()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var password = "same-password-for-retry";
        await fixture.WriteStateAsync(fixture.State(operationId, RemoteMaintenanceOperationState.Prepared,
            rustdeskId: string.Empty, protectedMonitorToken: null));
        fixture.Launcher.InstallWritesId = true;

        var first = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(first.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.configured", first.Message);
        Assert.Equal(operationId, fixture.Api.PreparedOperations.Single());
        Assert.Equal(password, fixture.Api.CommittedPasswords.Single());
        Assert.Equal(1, fixture.Launcher.Stages.Count(x => x == "install"));
        Assert.Equal(operationId, fixture.Api.CommittedOperations.Single());
    }

    [Fact]
    public async Task Installed_pending_commit_retries_commit_without_reinstall()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        await fixture.WriteStateAsync(fixture.State(operationId, RemoteMaintenanceOperationState.InstalledPendingCommit,
            rustdeskId: "rustdesk-123", protectedMonitorToken: null));

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.restored", result.Message);
        Assert.Empty(fixture.Launcher.Stages.Where(x => x == "install"));
        Assert.Equal(new[] { "configure" }, fixture.Launcher.Stages);
        Assert.Equal(operationId, fixture.Api.CommittedOperations.Single());
        Assert.Equal(0, fixture.Launcher.ValidationCount);
    }

    [Theory]
    [InlineData(RemoteMaintenanceOperationState.InstalledPendingCommit)]
    [InlineData(RemoteMaintenanceOperationState.Committed)]
    public async Task Registered_operation_with_rustdesk_uninstalled_reinstalls_with_same_operation_and_password(
        RemoteMaintenanceOperationState state)
    {
        // 门店卸载 RustDesk 后再点安装：只重跑 configure 会永远失败，必须完整重装；
        // 沿用原操作号才能通过中心的幂等 prepare/commit。
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        await fixture.WriteStateAsync(fixture.State(operationId, state, "rustdesk-123",
            state == RemoteMaintenanceOperationState.Committed ? fixture.Protect("monitor-token") : null));
        fixture.Installer.ServiceStatus = "notInstalled";
        fixture.Launcher.InstallWritesId = true;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.configured", result.Message);
        Assert.Equal(new[] { "install", "configure" }, fixture.Launcher.Stages);
        Assert.Equal(2, fixture.Downloader.VerifiedCount);
        Assert.Equal(operationId, fixture.Api.PreparedOperations.Single());
        Assert.Equal(operationId, fixture.Api.CommittedOperations.Single());
        Assert.Equal("same-password-for-retry", fixture.Api.CommittedPasswords.Single());
    }

    [Fact]
    public async Task Committed_recovery_reconfigures_same_operation_and_token()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        await fixture.WriteStateAsync(fixture.State(operationId, RemoteMaintenanceOperationState.Committed,
            rustdeskId: "rustdesk-123", protectedMonitorToken: fixture.Protect("monitor-token")));

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.serviceRestored", result.Message);
        Assert.Equal(new[] { "configure" }, fixture.Launcher.Stages);
        Assert.Equal(operationId, fixture.Launcher.OperationIds.Single());
        Assert.Equal(0, fixture.Launcher.ValidationCount);
    }

    [Fact]
    public async Task Uac_cancellation_requests_fail_closed_cleanup()
    {
        using var fixture = new Fixture(cancelInstall: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.InstallAsync(fixture.Session));

        Assert.Contains("fail-closed", fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Helper_rejection_does_not_request_cleanup_for_preexisting_services()
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallExitCode = 4;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.installationFailed", result.Message);
        var cleanup = Assert.Single(fixture.Launcher.FailClosedRequests);
        Assert.False(cleanup.RustDeskInstalled);
        Assert.False(cleanup.StatusAgentInstalled);
    }

    [Fact]
    public async Task Partial_install_failure_keeps_cleanup_for_components_already_touched()
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallExitCode = 4;
        fixture.Launcher.InstallRustDeskInstalled = true;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.installationFailed", result.Message);
        var cleanup = Assert.Single(fixture.Launcher.FailClosedRequests);
        Assert.True(cleanup.RustDeskInstalled);
        Assert.False(cleanup.StatusAgentInstalled);
    }

    [Fact]
    public async Task Download_success_is_reported_only_after_both_files_verify_and_before_uac()
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallWritesId = true;
        var stages = new List<RemoteMaintenanceStage>();
        fixture.Downloader.BeforeDownload = artifact =>
        {
            Assert.DoesNotContain(RemoteMaintenanceStage.DownloadedInstalling, stages);
            Assert.Empty(fixture.Launcher.Stages);
            Assert.Equal(artifact.FileName == "rustdesk.exe"
                ? RemoteMaintenanceStage.DownloadingRustDesk : RemoteMaintenanceStage.DownloadingStatusAgent, stages.Last());
        };
        fixture.Launcher.BeforeStage = stage =>
        {
            if (stage == "install")
            {
                Assert.Equal(2, fixture.Downloader.VerifiedCount);
                Assert.Equal(RemoteMaintenanceStage.DownloadedInstalling, stages.Last());
            }
        };

        var result = await fixture.Service.InstallAsync(fixture.Session, progress: new RecordingProgress(stages));

        Assert.True(result.Succeeded);
        Assert.Equal(new[]
        {
            RemoteMaintenanceStage.Preparing, RemoteMaintenanceStage.DownloadingRustDesk,
            RemoteMaintenanceStage.DownloadingStatusAgent, RemoteMaintenanceStage.DownloadedInstalling,
            RemoteMaintenanceStage.Registering, RemoteMaintenanceStage.Configuring
        }, stages);
    }

    [Theory]
    [InlineData("rustdesk.exe", false)]
    [InlineData("agent.exe", false)]
    [InlineData("agent.exe", true)]
    public async Task Failed_download_never_reports_success_or_launches_uac(string fileName, bool verificationFailure)
    {
        using var fixture = new Fixture();
        var stages = new List<RemoteMaintenanceStage>();
        fixture.Downloader.BeforeDownload = artifact =>
        {
            if (artifact.FileName != fileName) return;
            if (verificationFailure) throw new InvalidDataException("private download details");
            throw new HttpRequestException("private download details");
        };

        var result = await fixture.Service.InstallAsync(fixture.Session, progress: new RecordingProgress(stages));

        Assert.False(result.Succeeded);
        Assert.Equal(verificationFailure ? "settings.remoteMaintenance.result.downloadVerificationFailed"
            : "settings.remoteMaintenance.result.downloadFailed", result.Message);
        Assert.DoesNotContain(RemoteMaintenanceStage.DownloadedInstalling, stages);
        Assert.Empty(fixture.Launcher.Stages);
        Assert.Empty(fixture.Api.CommittedOperations);
    }

    [Fact]
    public async Task Download_timeout_is_reported_as_download_failure()
    {
        using var fixture = new Fixture();
        fixture.Downloader.BeforeDownload = _ => throw new TaskCanceledException("HTTP timeout");

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.downloadFailed", result.Message);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Helper_launch_failure_distinguishes_uac_cancellation_from_missing_components(bool canceled)
    {
        using var fixture = new Fixture();
        fixture.Launcher.BeforeStage = stage =>
        {
            if (stage != "install") return;
            if (canceled) throw new System.ComponentModel.Win32Exception(1223);
            throw new FileNotFoundException("private helper path");
        };

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal(canceled ? "settings.remoteMaintenance.result.uacCanceled"
            : "settings.remoteMaintenance.result.componentsMissing", result.Message);
        Assert.Empty(fixture.Api.CommittedOperations);
    }

    [Theory]
    [InlineData(RemoteMaintenanceOperationState.InstalledPendingCommit)]
    [InlineData(RemoteMaintenanceOperationState.Committed)]
    public async Task Recovery_reports_its_stage_without_claiming_a_new_download(RemoteMaintenanceOperationState state)
    {
        using var fixture = new Fixture();
        var stages = new List<RemoteMaintenanceStage>();
        await fixture.WriteStateAsync(fixture.State(Guid.NewGuid(), state, "rustdesk-123",
            state == RemoteMaintenanceOperationState.Committed ? fixture.Protect("monitor-token") : null));

        var result = await fixture.Service.InstallAsync(fixture.Session, progress: new RecordingProgress(stages));

        Assert.True(result.Succeeded);
        Assert.Equal(RemoteMaintenanceStage.Configuring, stages.Last());
        Assert.DoesNotContain(RemoteMaintenanceStage.DownloadedInstalling, stages);
        Assert.Equal(0, fixture.Downloader.VerifiedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_timeout_reports_registration_failure_and_preserves_cleanup(bool recovering)
    {
        using var fixture = new Fixture();
        fixture.Launcher.InstallWritesId = true;
        fixture.Launcher.InstallRustDeskInstalled = true;
        fixture.Launcher.InstallStatusAgentInstalled = true;
        fixture.Api.CommitFailure = new TaskCanceledException("HTTP timeout");
        if (recovering)
        {
            await fixture.WriteStateAsync(fixture.State(Guid.NewGuid(),
                RemoteMaintenanceOperationState.InstalledPendingCommit, "rustdesk-123", null));
        }

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.registrationFailed", result.Message);
        Assert.DoesNotContain("configure", fixture.Launcher.Stages);
        if (!recovering)
        {
            var cleanup = Assert.Single(fixture.Launcher.FailClosedRequests);
            Assert.True(cleanup.RustDeskInstalled);
            Assert.True(cleanup.StatusAgentInstalled);
        }
        else
        {
            Assert.Empty(fixture.Launcher.Stages);
        }
    }

    [Theory]
    [InlineData(503, "REMOTE_MAINTENANCE_DISABLED", "serverDisabled")]
    [InlineData(503, "REMOTE_MAINTENANCE_NOT_READY", "serverNotReady")]
    [InlineData(400, "REMOTE_MAINTENANCE_GATEWAY_INVALID", "serverConfigurationInvalid")]
    [InlineData(401, "REMOTE_MAINTENANCE_INTERNAL_AUTH_REQUIRED", "serverAuthorizationFailed")]
    [InlineData(503, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", "serverUnavailable")]
    [InlineData(404, null, "endpointUnavailable")]
    [InlineData(405, null, "endpointUnavailable")]
    [InlineData(401, "DEVICE_AUTH_INVALID", "authorizationExpired")]
    [InlineData(403, null, "authorizationExpired")]
    [InlineData(503, "unknown-private-error", "preparationFailed")]
    public async Task Preparation_failure_identifies_the_action_without_exposing_server_details(
        int statusCode, string? code, string expectedResult)
    {
        using var fixture = new Fixture();
        fixture.Api.PrepareFailure = new RemoteMaintenanceApiException("private server details", statusCode, code);

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result." + expectedResult, result.Message);
        Assert.Equal(0, fixture.Downloader.VerifiedCount);
        Assert.Empty(fixture.Launcher.Stages);
        Assert.Empty(fixture.Api.CommittedOperations);
    }

    [Theory]
    [InlineData(false, "connectionFailed")]
    [InlineData(true, "preparationTimedOut")]
    public async Task Preparation_transport_failure_provides_a_retry_action(bool timedOut, string expectedResult)
    {
        using var fixture = new Fixture();
        fixture.Api.PrepareFailure = timedOut
            ? new TaskCanceledException("private timeout details")
            : new HttpRequestException("private connection details");

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result." + expectedResult, result.Message);
        Assert.Empty(fixture.Launcher.Stages);
    }

    private static readonly string TestPublicKey = Convert.ToBase64String(new byte[32]);
    private static readonly RemoteMaintenanceConfig LegacyServer = new("hotbargain.vip:21116", "hotbargain.vip:21117", TestPublicKey);
    private static readonly RemoteMaintenanceConfig DedicatedServer = new("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", TestPublicKey);

    // 已提交且配置为历史地址的设备；中心返回的 deviceId 必须与 journal 一致，对账才会信任响应。
    private static async Task<RemoteMaintenanceJournalState> WriteCommittedAsync(Fixture fixture, RemoteMaintenanceConfig config)
    {
        var state = fixture.State(Guid.NewGuid(), RemoteMaintenanceOperationState.Committed,
            rustdeskId: "rustdesk-123", protectedMonitorToken: fixture.Protect("monitor-token")) with { Config = config };
        await fixture.WriteStateAsync(state);
        fixture.Api.DeviceId = state.DeviceId;
        return state;
    }

    [Fact]
    public async Task Reconcile_switches_to_the_server_the_center_asks_for()
    {
        using var fixture = new Fixture();
        var state = await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Reconciled, result);
        Assert.Equal(new[] { "reconcile" }, fixture.Launcher.Stages);
        Assert.Equal(state.OperationId, fixture.Launcher.OperationIds.Single());
        var journal = await fixture.ReadStateAsync();
        Assert.Equal(DedicatedServer, journal!.Config);
        Assert.Null(journal.PendingConfig);
    }

    [Fact]
    public async Task Reconcile_does_nothing_when_local_config_already_matches_the_center()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, DedicatedServer);
        fixture.Api.PrepareConfig = DedicatedServer;

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.UpToDate, result);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Reconcile_rolls_back_to_the_legacy_server_when_the_center_switches_back()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, DedicatedServer);
        fixture.Api.PrepareConfig = LegacyServer;

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Reconciled, result);
        Assert.Equal(LegacyServer, (await fixture.ReadStateAsync())!.Config);
    }

    [Theory]
    [InlineData("evil.example.com:21116", "evil.example.com:21117")]
    [InlineData("rd.hotbargain.vip:21116", "hotbargain.vip:21117")]
    [InlineData("rd.hotbargain.vip:22", "rd.hotbargain.vip:21117")]
    [InlineData("rd.hotbargain.vip.evil.com:21116", "rd.hotbargain.vip.evil.com:21117")]
    public async Task Reconcile_refuses_servers_outside_the_company_allow_list(string idServer, string relayServer)
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = new RemoteMaintenanceConfig(idServer, relayServer, TestPublicKey);

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Rejected, result);
        Assert.Empty(fixture.Launcher.Stages);
        var journal = await fixture.ReadStateAsync();
        Assert.Equal(LegacyServer, journal!.Config);
        Assert.Null(journal.PendingConfig);
    }

    [Fact]
    public async Task Reconcile_refuses_a_public_key_change()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        fixture.Api.PrepareConfig = DedicatedServer with { PublicKey = otherKey };

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Rejected, result);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Reconcile_ignores_a_response_for_another_device()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;
        fixture.Api.DeviceId = Guid.NewGuid();

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, result);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Reconcile_leaves_uninstalled_devices_to_the_full_reinstall_path()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Installer.ServiceStatus = "notInstalled";
        fixture.Api.PrepareConfig = DedicatedServer;

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.NotApplicable, result);
        Assert.Empty(fixture.Api.PreparedOperations);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Theory]
    [InlineData(RemoteMaintenanceOperationState.Prepared)]
    [InlineData(RemoteMaintenanceOperationState.InstalledPendingCommit)]
    public async Task Reconcile_only_applies_to_committed_operations(RemoteMaintenanceOperationState state)
    {
        using var fixture = new Fixture();
        await fixture.WriteStateAsync(fixture.State(Guid.NewGuid(), state, "rustdesk-123", null) with { Config = LegacyServer });
        fixture.Api.PrepareConfig = DedicatedServer;

        Assert.Equal(RemoteMaintenanceReconcileResult.NotApplicable, await fixture.Service.ReconcileAsync());
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Reconcile_with_no_journal_is_not_applicable()
    {
        using var fixture = new Fixture();

        Assert.Equal(RemoteMaintenanceReconcileResult.NotApplicable, await fixture.Service.ReconcileAsync());
        Assert.Empty(fixture.Api.PreparedOperations);
    }

    [Fact]
    public async Task Reconcile_offline_defers_quietly_without_counting_a_failure()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareFailure = new HttpRequestException("offline");

        var result = await fixture.Service.ReconcileAsync();

        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, result);
        Assert.Empty(fixture.Launcher.Stages);
        Assert.Equal(0, (await fixture.ReadStateAsync())!.ReconcileFailureCount);
    }

    [Fact]
    public async Task Reconcile_stops_prompting_for_uac_after_repeated_failures_but_manual_repair_still_tries()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;
        fixture.Launcher.ReconcileFailure = new System.ComponentModel.Win32Exception(1223);

        for (var attempt = 1; attempt <= RemoteMaintenanceService.MaxAutomaticReconcileFailures; attempt++)
        {
            Assert.Equal(RemoteMaintenanceReconcileResult.Failed, await fixture.Service.ReconcileAsync());
            Assert.Equal(attempt, (await fixture.ReadStateAsync())!.ReconcileFailureCount);
        }

        // 达到上限后自动对账不再弹 UAC（仍会问中心，这样中心撤回时才能解除锁定）。
        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, await fixture.Service.ReconcileAsync());
        Assert.Equal(RemoteMaintenanceService.MaxAutomaticReconcileFailures, fixture.Launcher.Stages.Count(x => x == "reconcile"));

        // 设置页手动修复仍会再试一次；这次成功后失败计数清零、配置已切换。
        fixture.Launcher.ReconcileFailure = null;
        var repair = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(repair.Succeeded);
        Assert.Equal(RemoteMaintenanceService.MaxAutomaticReconcileFailures + 1, fixture.Launcher.Stages.Count(x => x == "reconcile"));
        var journal = await fixture.ReadStateAsync();
        Assert.Equal(DedicatedServer, journal!.Config);
        Assert.Equal(0, journal.ReconcileFailureCount);
    }

    [Fact]
    public async Task Reconcile_failure_from_the_helper_keeps_the_old_config_and_retries_next_start()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;
        fixture.Launcher.ReconcileExitCode = 13;

        Assert.Equal(RemoteMaintenanceReconcileResult.Failed, await fixture.Service.ReconcileAsync());

        var journal = await fixture.ReadStateAsync();
        Assert.Equal(LegacyServer, journal!.Config);
        Assert.Equal(1, journal.ReconcileFailureCount);

        fixture.Launcher.ReconcileExitCode = 0;
        Assert.Equal(RemoteMaintenanceReconcileResult.Reconciled, await fixture.Service.ReconcileAsync());
        Assert.Equal(DedicatedServer, (await fixture.ReadStateAsync())!.Config);
    }

    [Fact]
    public async Task Manual_repair_reconciles_the_server_before_restoring_the_status_service()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "reconcile", "configure" }, fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Reconcile_stops_waiting_when_the_uac_prompt_is_ignored_and_releases_the_gate()
    {
        using var fixture = new Fixture(reconcileHelperTimeout: TimeSpan.FromMilliseconds(100));
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;
        fixture.Launcher.HangReconcile = true;

        Assert.Equal(RemoteMaintenanceReconcileResult.Failed, await fixture.Service.ReconcileAsync());
        Assert.Equal(1, (await fixture.ReadStateAsync())!.ReconcileFailureCount);

        // 闸门已释放，之后用户点了授权即可成功。
        fixture.Launcher.HangReconcile = false;
        Assert.Equal(RemoteMaintenanceReconcileResult.Reconciled, await fixture.Service.ReconcileAsync());
    }

    [Fact]
    public async Task Reconcile_yields_to_a_running_install_instead_of_queueing()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource();
        fixture.Api.PrepareBlocker = release.Task;
        var install = fixture.Service.InstallAsync(fixture.Session);
        await AsyncTestWaitSupport.WaitUntilAsync(() => fixture.Api.PreparedOperations.Count == 1);

        // 安装占着操作闸门：对账立即让路，不排队，也不去问中心。
        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, await fixture.Service.ReconcileAsync());
        Assert.Single(fixture.Api.PreparedOperations);

        fixture.Api.PrepareFailure = new HttpRequestException("stop the install");
        release.SetResult();
        await install;
    }

    [Fact]
    public async Task Reconcile_clears_leftover_pending_config_and_failure_count_once_it_matches()
    {
        using var fixture = new Fixture();
        var state = await WriteCommittedAsync(fixture, DedicatedServer);
        await fixture.WriteStateAsync(state with { PendingConfig = DedicatedServer, ReconcileFailureCount = 3 });
        fixture.Api.PrepareConfig = DedicatedServer;

        Assert.Equal(RemoteMaintenanceReconcileResult.UpToDate, await fixture.Service.ReconcileAsync());

        var journal = await fixture.ReadStateAsync();
        Assert.Null(journal!.PendingConfig);
        Assert.Equal(0, journal.ReconcileFailureCount);
    }

    [Fact]
    public async Task Reconcile_failure_lock_is_cleared_when_the_center_reverts_and_does_not_block_a_later_switch()
    {
        using var fixture = new Fixture();
        var state = await WriteCommittedAsync(fixture, LegacyServer);
        await fixture.WriteStateAsync(state with { ReconcileFailureCount = RemoteMaintenanceService.MaxAutomaticReconcileFailures });

        // 已锁定且中心要求切换：不弹 UAC。
        fixture.Api.PrepareConfig = DedicatedServer;
        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, await fixture.Service.ReconcileAsync());
        Assert.Empty(fixture.Launcher.Stages);

        // 中心撤回到本机现状：计数被清零，解除锁定。
        fixture.Api.PrepareConfig = LegacyServer;
        Assert.Equal(RemoteMaintenanceReconcileResult.UpToDate, await fixture.Service.ReconcileAsync());
        Assert.Equal(0, (await fixture.ReadStateAsync())!.ReconcileFailureCount);

        // 之后中心再次要求切换，可以自动对账。
        fixture.Api.PrepareConfig = DedicatedServer;
        Assert.Equal(RemoteMaintenanceReconcileResult.Reconciled, await fixture.Service.ReconcileAsync());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Reconcile_gateway_auth_rejections_are_not_counted_as_failures(int statusCode)
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareFailure = new RemoteMaintenanceApiException("denied", statusCode);

        Assert.Equal(RemoteMaintenanceReconcileResult.Deferred, await fixture.Service.ReconcileAsync());
        Assert.Equal(0, (await fixture.ReadStateAsync())!.ReconcileFailureCount);
        Assert.Empty(fixture.Launcher.Stages);
    }

    [Fact]
    public async Task Manual_repair_tells_the_user_when_the_server_address_could_not_be_updated()
    {
        using var fixture = new Fixture();
        await WriteCommittedAsync(fixture, LegacyServer);
        fixture.Api.PrepareConfig = DedicatedServer;
        fixture.Launcher.ReconcileExitCode = 13;

        var result = await fixture.Service.InstallAsync(fixture.Session);

        // 状态服务照常恢复，但不能让用户以为一切就绪。
        Assert.True(result.Succeeded);
        Assert.Equal("settings.remoteMaintenance.result.serviceRestoredServerPending", result.Message);
        Assert.Equal(new[] { "reconcile", "configure" }, fixture.Launcher.Stages);
    }

    private sealed class RecordingProgress(List<RemoteMaintenanceStage> stages) : IProgress<RemoteMaintenanceStage>
    {
        public void Report(RemoteMaintenanceStage value) => stages.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-remote-test-" + Guid.NewGuid().ToString("N"));
        private readonly PrefixProtector _protector = new();
        private readonly RemoteMaintenanceJournal _journal;
        public Fixture(bool cancelInstall = false, TimeSpan? reconcileHelperTimeout = null)
        {
            Directory.CreateDirectory(_root);
            _journal = new RemoteMaintenanceJournal(Path.Combine(_root, "journal.json"), _protector);
            Api = new FakeApi();
            Downloader = new FakeDownloader();
            Launcher = new FakeLauncher(_journal);
            Launcher.CancelInstall = cancelInstall;
            Installer = new FakeInstaller();
            Service = new RemoteMaintenanceService(Api, Downloader, Installer, Launcher, _journal, _protector)
            {
                ReconcileHelperTimeout = reconcileHelperTimeout ?? TimeSpan.FromMinutes(5)
            };
        }

        public FakeApi Api { get; }
        public FakeDownloader Downloader { get; }
        public FakeLauncher Launcher { get; }
        public FakeInstaller Installer { get; }
        public RemoteMaintenanceService Service { get; }
        public PosSessionState Session => new("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0);

        public string Protect(string value) => _protector.Protect(value);

        public RemoteMaintenanceJournalState State(Guid operationId, RemoteMaintenanceOperationState state,
            string rustdeskId, string? protectedMonitorToken)
        {
            string? rustDeskPath = null;
            string? agentPath = null;
            string? dataDirectory = null;
            if (state == RemoteMaintenanceOperationState.InstalledPendingCommit)
            {
                dataDirectory = Path.Combine(_root, "data");
                Directory.CreateDirectory(dataDirectory);
                rustDeskPath = Path.Combine(dataDirectory, "rustdesk.exe");
                agentPath = Path.Combine(dataDirectory, "agent.exe");
                File.WriteAllBytes(rustDeskPath, [1]);
                File.WriteAllBytes(agentPath, [1]);
            }

            return new(
                operationId, Guid.NewGuid(), state, rustdeskId, "1.4.9", _protector.Protect("same-password-for-retry"),
                protectedMonitorToken, state == RemoteMaintenanceOperationState.Committed ? "https://example.test/heartbeat" : null,
                DateTimeOffset.UtcNow, Manifest.Config, rustDeskPath, agentPath, dataDirectory, Manifest.Artifacts);
        }

        public Task WriteStateAsync(RemoteMaintenanceJournalState state) => _journal.WriteAsync(state);
        public Task<RemoteMaintenanceJournalState?> ReadStateAsync() => _journal.ReadAsync();

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static class Manifest
        {
            public static RemoteMaintenanceConfig Config => new("https://id.example.test", "https://relay.example.test", "public-key");
            public static RemoteMaintenanceArtifactManifest Artifacts => new(
                new("1.4.9", "rustdesk.exe", "/rustdesk", "00".PadLeft(64, '0'), 1),
                new("1.0.0", "agent.exe", "/agent", "11".PadLeft(64, '0'), 1));
        }

        private sealed class PrefixProtector : IRemoteMaintenanceSecretProtector
        {
            public string Protect(string plaintext) => "protected:" + plaintext;
            public string? Unprotect(string protectedValue) => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue[10..] : null;
        }

        public sealed class FakeApi : IRemoteMaintenanceApiClient
        {
            public Exception? PrepareFailure { get; set; }
            public Exception? CommitFailure { get; set; }
            // 对账用例需要中心返回指定的服务器配置，且 deviceId 与 journal 一致。
            public RemoteMaintenanceConfig? PrepareConfig { get; set; }
            public Guid? DeviceId { get; set; }
            // 让 prepare 停在中心请求上，用来制造“安装正占着操作闸门”的并发场景。
            public Task? PrepareBlocker { get; set; }
            public List<Guid> PreparedOperations { get; } = [];
            public List<Guid> CommittedOperations { get; } = [];
            public List<string> CommittedPasswords { get; } = [];
            public List<string> CommittedRustdeskIds { get; } = [];
            public async Task<RemoteMaintenancePrepareResponse> PrepareAsync(RemoteMaintenancePrepareRequest request, CancellationToken cancellationToken = default)
            {
                PreparedOperations.Add(request.OperationId);
                if (PrepareBlocker is not null) await PrepareBlocker;
                if (PrepareFailure is not null) throw PrepareFailure;
                return new RemoteMaintenancePrepareResponse(request.OperationId, DeviceId ?? Guid.NewGuid(), PrepareConfig ?? Manifest.Config, Manifest.Artifacts);
            }
            public Task<Stream> DownloadArtifactAsync(string downloadUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<RemoteMaintenanceCommitResponse> CommitAsync(RemoteMaintenanceCommitRequest request, CancellationToken cancellationToken = default)
            {
                CommittedOperations.Add(request.OperationId);
                CommittedPasswords.Add(request.Password);
                CommittedRustdeskIds.Add(request.RustdeskId);
                if (CommitFailure is not null) throw CommitFailure;
                return Task.FromResult(new RemoteMaintenanceCommitResponse(Guid.NewGuid(), "monitor-token", "https://example.test/heartbeat"));
            }
        }

        public sealed class FakeDownloader : IRemoteMaintenanceArtifactDownloader
        {
            public Action<RemoteMaintenanceArtifact>? BeforeDownload { get; set; }
            public int VerifiedCount { get; private set; }
            public Task<string> DownloadAndVerifyAsync(RemoteMaintenanceArtifact artifact, string destinationDirectory, CancellationToken cancellationToken = default)
            {
                BeforeDownload?.Invoke(artifact);
                Directory.CreateDirectory(destinationDirectory);
                var path = Path.Combine(destinationDirectory, artifact.FileName);
                File.WriteAllBytes(path, [1]);
                VerifiedCount++;
                return Task.FromResult(path);
            }
        }

        public sealed class FakeInstaller : IRemoteMaintenanceInstaller
        {
            public Task<RemoteMaintenanceInstallationResult> InstallAsync(RemoteMaintenanceInstallationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task FailClosedAsync(RemoteMaintenanceInstallationResult installation, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ConfigureStatusAgentAsync(RemoteMaintenancePrepareResponse prepare, RemoteMaintenanceCommitResponse commit, string rustdeskId, string clientVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<string?> GetRustdeskIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("unused");
            public string ServiceStatus { get; set; } = "stopped";
            public Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemoteMaintenanceStatus(false, "", "", ServiceStatus));
        }

        public sealed class FakeLauncher(RemoteMaintenanceJournal journal) : IRemoteMaintenanceUacHelperLauncher
        {
            public Exception? ValidationFailure { get; set; }
            public int ValidationCount { get; private set; }
            public void ValidateInstallation(string helperPath)
            {
                ValidationCount++;
                if (ValidationFailure is not null) throw ValidationFailure;
            }

            public Action<string>? BeforeStage { get; set; }
            public List<string> Stages { get; } = [];
            public List<Guid> OperationIds { get; } = [];
            public List<RemoteMaintenanceInstallationResult> FailClosedRequests { get; } = [];
            // reconcile 阶段：抛出异常（如 UAC 被拒）或返回非零；成功时模拟 helper 把 PendingConfig 并入 Config。
            public Exception? ReconcileFailure { get; set; }
            // 模拟 UAC 弹窗无人理会：helper 一直不返回，只有等待超时才会结束。
            public bool HangReconcile { get; set; }
            public int ReconcileExitCode { get; set; }
            public bool InstallWritesId { get; set; }
            public bool CancelInstall { get; set; }
            public int InstallExitCode { get; set; }
            public bool InstallRustDeskInstalled { get; set; }
            public bool InstallStatusAgentInstalled { get; set; }
            public async Task<int> RunAsync(string helperPath, string journalPath, Guid operationId, string stage, CancellationToken cancellationToken = default)
            {
                Stages.Add(stage);
                OperationIds.Add(operationId);
                BeforeStage?.Invoke(stage);
                if (stage == "install" && CancelInstall) throw new OperationCanceledException(cancellationToken);
                if (stage == "fail-closed")
                {
                    var state = await journal.ReadAsync(CancellationToken.None) ?? throw new InvalidOperationException();
                    FailClosedRequests.Add(new RemoteMaintenanceInstallationResult(
                        state.RustDeskInstalled,
                        state.StatusAgentInstalled,
                        state.RustdeskId,
                        state.ClientVersion,
                        state.DataDirectory ?? string.Empty));
                    return 0;
                }
                if (stage == "reconcile")
                {
                    if (ReconcileFailure is not null) throw ReconcileFailure;
                    if (HangReconcile) await Task.Delay(Timeout.Infinite, cancellationToken);
                    if (ReconcileExitCode != 0) return ReconcileExitCode;
                    var current = await journal.ReadAsync(cancellationToken) ?? throw new InvalidOperationException();
                    await journal.WriteAsync(current with { Config = current.PendingConfig, PendingConfig = null, ReconcileFailureCount = 0 }, cancellationToken);
                    return 0;
                }
                if (stage == "install" && InstallWritesId)
                {
                    var state = await journal.ReadAsync(cancellationToken) ?? throw new InvalidOperationException();
                    await journal.WriteAsync(state with
                    {
                        RustdeskId = "rustdesk-123",
                        RustDeskInstalled = InstallRustDeskInstalled,
                        StatusAgentInstalled = InstallStatusAgentInstalled
                    }, cancellationToken);
                }
                if (stage == "install" && (InstallRustDeskInstalled || InstallStatusAgentInstalled))
                {
                    var state = await journal.ReadAsync(cancellationToken) ?? throw new InvalidOperationException();
                    await journal.WriteAsync(state with
                    {
                        RustDeskInstalled = InstallRustDeskInstalled,
                        StatusAgentInstalled = InstallStatusAgentInstalled
                    }, cancellationToken);
                }
                return stage == "install" ? InstallExitCode : 0;
            }
        }
    }
}
