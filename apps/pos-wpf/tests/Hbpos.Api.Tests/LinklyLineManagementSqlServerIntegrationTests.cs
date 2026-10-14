using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class LinklyLineSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "LINKLY_LINE_SQLSERVER_TEST_CONNECTION";

    public LinklyLineSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = "未配置隔离 SQL Server，跳过 Linkly 线路真实事务测试。";
    }
}

// 每个用例创建独立数据库；不允许借用生产表，也不依赖用例执行顺序。
[Trait("Category", "SQL")]
public sealed class LinklyLineManagementSqlServerIntegrationTests : IAsyncLifetime
{
    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;
    private readonly DateTime version = new(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);
    private readonly Guid lineA = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private readonly Guid lineB = Guid.Parse("00000002-0000-0000-0000-000000000002");
    private readonly Guid lineC = Guid.Parse("00000003-0000-0000-0000-000000000003");

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(LinklyLineSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured)) return;
        database = $"HbLinklyLines_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await ExecuteAsync(SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);
        await ExecuteAsync("""
            CREATE TABLE [dbo].[POSM_设备注册信息表] (
                [ID] int IDENTITY(1,1) NOT NULL,
                [系统设备编号] nvarchar(64) NOT NULL PRIMARY KEY,
                [分店代码] nvarchar(32) NOT NULL,
                [设备类型] nvarchar(32) NOT NULL,
                [设备系统] nvarchar(32) NULL,
                [设备状态] int NOT NULL,
                [是否允许交易] bit NOT NULL);
            INSERT INTO [dbo].[POSM_设备注册信息表]
                ([系统设备编号],[分店代码],[设备类型],[设备系统],[设备状态],[是否允许交易]) VALUES
                (N'POS-A',N'S001',N'POS',N'Windows',1,1),
                (N'POS-B',N'S001',N'POS',N'iOS',1,1),
                (N'POS-C',N'S001',N'POS',N'iOS',1,1),
                (N'POS-DISABLED',N'S001',N'POS',N'iOS',2,1),
                (N'POS-NOT-ALLOWED',N'S001',N'POS',N'iOS',1,0),
                (N'PDA-A',N'S001',N'PDA',N'Android',1,1),
                (N'OTHER-STORE',N'S002',N'POS',N'Windows',1,1);
            INSERT INTO [dbo].[POSM_LinklyCloudConfigurationMode]
                ([Environment],[StoreCode],[Mode]) VALUES (N'Production',N'S001',N'Active');
            """);
        await SeedLineAsync(lineA, 1);
        await SeedLineAsync(lineB, 2);
        await SeedLineAsync(lineC, 3);
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null) return;
        // 名称只来自本用例生成的 GUID，销毁范围严格限于本用例数据库。
        await ExecuteAtAsync(masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [LinklyLineSqlServerFact]
    public async Task Caller_can_transfer_another_pos_line_and_atomically_replace_target()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        await SeedSelectionAsync("POS-C", lineC, 301);
        await AssignAsync("POS-A", lineB, "POS-B", 201, "POS-C", lineC, 301);
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Null(await SelectionAsync("POS-B"));
        var target = (await SelectionAsync("POS-C"))!;
        Assert.Equal(lineB, target.TerminalId);
        Assert.True(target.Revision > 301);
        Assert.Null(await OwnerAsync(lineC));
        var refreshed = await Repository().ListAsync("Production", "S001", default);
        Assert.Equal("Healthy", refreshed.Single(item => item.TerminalId == lineA).LastHealthStatus);
        Assert.All(refreshed.Where(item => item.TerminalId != lineA), item =>
        {
            Assert.Null(item.LastHealthStatus);
            Assert.Null(item.LastHealthAt);
            Assert.True(item.UpdatedAt > version);
        });
        await AssertPairingPreservedAsync();
    }

    [LinklyLineSqlServerFact]
    public async Task Local_switch_to_unassigned_line_releases_previous_line()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await AssignAsync("POS-A", lineB, null, 0, "POS-A", lineA, 101);
        Assert.Equal(lineB, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Null(await OwnerAsync(lineA));
        await AssertPairingPreservedAsync();
    }

    [LinklyLineSqlServerFact]
    public async Task Public_directory_uses_target_device_revision_and_zero_after_unbinding()
    {
        await SeedSelectionAsync("POS-A", lineA, 900000);
        await SeedSelectionAsync("POS-B", lineB, 10);
        var pairedPosId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        await ExecuteAsync("UPDATE [dbo].[POSM_LinklyCloudTerminal] SET [PosId]=@PosId WHERE [TerminalId]=@Line;",
            new SqlParameter("@PosId", pairedPosId), new SqlParameter("@Line", lineA));
        var service = new LinklyCloudTerminalService(
            Repository(), new SqlSugarLinklyCloudBackendAsyncRepository(CreateContext()),
            new UnusedPairingTransport(), Options.Create(new LinklyCloudBackendAsyncOptions()));
        var before = await service.GetTerminalsAsync("S001", "POS-C", "Production", default);
        var source = before.Terminals.Single(item => item.TerminalId == lineA);

        // 修订号属于设备选择；跨设备转绑可以变小，解绑后公开目录必须返回零。
        var transferred = await service.AssignTerminalAsync("S001", "POS-C", lineA,
            new LinklyCloudTerminalAssignmentRequest("Production", source.TerminalVersion!,
                "POS-A", 900000, "POS-B", lineB, 10), "TEST", default);
        var assigned = transferred.Terminals.Single(item => item.TerminalId == lineA);
        Assert.Equal("POS-B", assigned.AssignedDeviceCode);
        Assert.Equal(11, assigned.AssignmentRevision);
        Assert.NotEqual(source.TerminalVersion, assigned.TerminalVersion);
        Assert.Null(assigned.LastHealthStatus);
        var releasedOwner = transferred.Devices!.Single(item => item.DeviceCode == "POS-A");
        Assert.Null(releasedOwner.SelectedTerminalId);
        Assert.Equal(0, releasedOwner.SelectionRevision);
        var displaced = transferred.Terminals.Single(item => item.TerminalId == lineB);
        Assert.Null(displaced.AssignedDeviceCode);
        Assert.Equal(0, displaced.AssignmentRevision);
        Assert.Null(displaced.LastHealthStatus);

        var unbound = await service.AssignTerminalAsync("S001", "POS-C", lineA,
            new LinklyCloudTerminalAssignmentRequest("Production", assigned.TerminalVersion!,
                "POS-B", 11, null, null, 0), "TEST", default);
        var releasedLine = unbound.Terminals.Single(item => item.TerminalId == lineA);
        Assert.Null(releasedLine.AssignedDeviceCode);
        Assert.Equal(0, releasedLine.AssignmentRevision);
        Assert.NotEqual(assigned.TerminalVersion, releasedLine.TerminalVersion);
        var releasedTarget = unbound.Devices!.Single(item => item.DeviceCode == "POS-B");
        Assert.Null(releasedTarget.SelectedTerminalId);
        Assert.Equal(0, releasedTarget.SelectionRevision);
        var unchangedPairing = (await Repository().GetAsync("Production", "S001", lineA, default))!;
        Assert.Equal("Ready", unchangedPairing.PairingState);
        Assert.Equal("test-secret", unchangedPairing.Secret);
        Assert.Equal(pairedPosId, unchangedPairing.PosId);
    }

    [LinklyLineSqlServerFact]
    public async Task Unbind_only_releases_requested_line_and_can_repeat_from_new_snapshot()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        await AssignAsync("POS-A", lineB, "POS-B", 201, null, null, 0);
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Null(await SelectionAsync("POS-B"));
        var newVersion = (await Repository().GetAsync("Production", "S001", lineB, default))!.UpdatedAt!.Value;
        await AssignAsync("POS-A", lineB, null, 0, null, null, 0, newVersion);
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        await AssertPairingPreservedAsync();
    }

    [LinklyLineSqlServerFact]
    public async Task Stale_source_or_target_version_leaves_all_bindings_unchanged()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-C", lineA, "POS-A", 100, "POS-B", lineB, 201));
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", lineB, 200));
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", lineB, 201, version.AddTicks(-1)));
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Equal(101, (await SelectionAsync("POS-A"))!.Revision);
        Assert.Equal(lineB, (await SelectionAsync("POS-B"))!.TerminalId);
        Assert.Equal(201, (await SelectionAsync("POS-B"))!.Revision);
    }

    [LinklyLineSqlServerFact]
    public async Task Target_must_be_available_pos_in_same_store()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        foreach (var target in new[] { "OTHER-STORE", "POS-DISABLED", "POS-NOT-ALLOWED", "PDA-A", "MISSING" })
        {
            await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-A", lineA, "POS-A", 101, target, null, 0));
            Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        }
    }

    [LinklyLineSqlServerFact]
    public async Task Historical_owner_stays_visible_but_cannot_be_a_new_target()
    {
        await SeedSelectionAsync("DELETED-POS", lineA, 101);
        await SeedSelectionAsync("POS-DISABLED", lineB, 201);
        await ExecuteAsync("UPDATE [dbo].[POSM_设备注册信息表] SET [设备状态]=0 WHERE [系统设备编号]=N'POS-DISABLED';");
        var devices = await Repository().ListAssignableDevicesAsync("Production", "S001", default);
        foreach (var owner in new[] { "DELETED-POS", "POS-DISABLED" })
        {
            var historical = Assert.Single(devices, item => item.DeviceCode == owner);
            Assert.False(historical.IsAvailable);
            Assert.NotNull(historical.SelectedTerminalId);
            Assert.True(historical.SelectionRevision > 0);
        }
        await AssignAsync("POS-C", lineA, "DELETED-POS", 101, "POS-A", null, 0);
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Null(await SelectionAsync("DELETED-POS"));
    }

    [LinklyLineSqlServerFact]
    public async Task Source_owner_session_without_terminal_id_blocks_transfer()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSessionAsync("POS-A", null, "Unknown", true);
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", null, 0));
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Null(await SelectionAsync("POS-B"));
    }

    [LinklyLineSqlServerFact]
    public async Task Target_old_line_lease_and_unknown_acknowledged_session_block_replace()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        var lease = Guid.NewGuid();
        Assert.True(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineB,
            version, lease, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default, "POS-B", 201));
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-A", lineA, "POS-A", 101, "POS-B", lineB, 201));
        await Repository().ReleaseConnectionTestLeaseAsync("Production", "S001", lineB, lease, default);
        await SeedSessionAsync("HISTORICAL-POS", lineB, "Unknown", true);
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-A", lineA, "POS-A", 101, "POS-B", lineB, 201));
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Equal(lineB, (await SelectionAsync("POS-B"))!.TerminalId);
    }

    [LinklyLineSqlServerFact]
    public async Task Concurrent_claims_have_one_winner_and_no_partial_release()
    {
        var attempts = await Task.WhenAll(
            CaptureAsync(() => AssignAsync("POS-A", lineA, null, 0, "POS-A", null, 0)),
            CaptureAsync(() => AssignAsync("POS-B", lineA, null, 0, "POS-B", null, 0)));
        Assert.Single(attempts, success => success);
        var owners = await Repository().ListAssignableDevicesAsync("Production", "S001", default);
        Assert.Single(owners, item => item.SelectedTerminalId == lineA);
        await AssertPairingPreservedAsync();
    }

    [LinklyLineSqlServerFact]
    public async Task Opposite_transfers_terminate_and_commit_only_one_complete_result()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        var attempts = await Task.WhenAll(
            CaptureAsync(() => AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", lineB, 201)),
            CaptureAsync(() => AssignAsync("POS-C", lineB, "POS-B", 201, "POS-A", lineA, 101)));
        Assert.Single(attempts, success => success);
        var a = await SelectionAsync("POS-A");
        var b = await SelectionAsync("POS-B");
        Assert.True((a is null && b?.TerminalId == lineA) || (b is null && a?.TerminalId == lineB));
    }

    [LinklyLineSqlServerFact]
    public async Task Unassigned_line_management_lease_blocks_assignment_without_changing_pairing()
    {
        var lease = Guid.NewGuid();
        Assert.True(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineA,
            version, lease, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default));
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-A", lineA, null, 0, "POS-A", null, 0));
        Assert.Null(await SelectionAsync("POS-A"));
        await Repository().ReleaseConnectionTestLeaseAsync("Production", "S001", lineA, lease, default);
        await AssignAsync("POS-A", lineA, null, 0, "POS-A", null, 0);
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        await AssertPairingPreservedAsync();
    }

    [LinklyLineSqlServerFact]
    public async Task Legacy_local_selection_cannot_bypass_old_or_new_line_management_lease()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        foreach (var terminal in new[] { lineA, lineB })
        {
            var lease = Guid.NewGuid();
            Assert.True(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", terminal,
                version, lease, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default,
                terminal == lineA ? "POS-A" : null, terminal == lineA ? 101 : 0));
            await Assert.ThrowsAnyAsync<Exception>(() => Repository().UpsertSelectionAsync(
                "Production", "S001", "POS-A", lineB, 101, DateTime.UtcNow, "TEST-OPERATOR", default));
            Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
            Assert.Equal(101, (await SelectionAsync("POS-A"))!.Revision);
            await Repository().ReleaseConnectionTestLeaseAsync("Production", "S001", terminal, lease, default);
        }
        await Repository().UpsertSelectionAsync("Production", "S001", "POS-A", lineB, 101,
            DateTime.UtcNow, "TEST-OPERATOR", default);
        Assert.Equal(lineB, (await SelectionAsync("POS-A"))!.TerminalId);
    }

    [LinklyLineSqlServerFact]
    public async Task Opposite_legacy_switches_return_conflicts_without_deadlock_or_partial_changes()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        var attempts = await Task.WhenAll(
            CaptureAsync(() => Repository().UpsertSelectionAsync("Production", "S001", "POS-A",
                lineB, 101, DateTime.UtcNow, "TEST", default)),
            CaptureAsync(() => Repository().UpsertSelectionAsync("Production", "S001", "POS-B",
                lineA, 201, DateTime.UtcNow, "TEST", default)));
        Assert.All(attempts, success => Assert.False(success));
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        Assert.Equal(101, (await SelectionAsync("POS-A"))!.Revision);
        Assert.Equal(lineB, (await SelectionAsync("POS-B"))!.TerminalId);
        Assert.Equal(201, (await SelectionAsync("POS-B"))!.Revision);
    }

    [LinklyLineSqlServerFact]
    public async Task Connection_test_lease_rechecks_owner_after_target_line_is_displaced()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSelectionAsync("POS-B", lineB, 201);
        await AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", lineB, 201);
        var lease = Guid.NewGuid();
        var currentVersion = (await Repository().GetAsync("Production", "S001", lineB, default))!.UpdatedAt!.Value;
        Assert.False(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineB,
            currentVersion, lease, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default, "POS-B", 201));
        Assert.Null((await Repository().GetAsync("Production", "S001", lineB, default))!.PairingAttemptId);
        Assert.Null(await OwnerAsync(lineB));
    }

    [LinklyLineSqlServerFact]
    public async Task Not_ready_line_rejects_binding_but_still_allows_unbinding()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await ExecuteAsync("UPDATE [dbo].[POSM_LinklyCloudTerminal] SET [PairingState]=N'NeedsRepair' WHERE [TerminalId]=@Line;", new SqlParameter("@Line", lineA));
        await Assert.ThrowsAsync<LinklyCloudTerminalNotReadyException>(() =>
            AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", null, 0));
        Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
        await AssignAsync("POS-C", lineA, "POS-A", 101, null, null, 0);
        Assert.Null(await SelectionAsync("POS-A"));
    }

    [LinklyLineSqlServerFact]
    public async Task Supervisor_resolution_ack_releases_management_gates_but_plain_ack_keeps_them_blocked()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        const string sessionId = "supervisor-resolution-session";
        await ExecuteAsync("""
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
                ([Environment],[StoreCode],[DeviceCode],[TerminalId],[SessionId],[Status],[IsActive],[ClientAcknowledgedAt])
            VALUES (N'Production',N'S001',N'POS-A',@Line,@Session,N'Pending',1,NULL);
            """, new("@Line", lineA), new("@Session", sessionId));

        // 普通 ack（2.5-7 的现场）：付款闸门已放行，但结果仍未知，终端管理必须继续阻塞。
        var plain = await BackendRepository().AcknowledgeSessionAsync(
            "Production", "S001", "POS-A", sessionId, DateTimeOffset.UtcNow, supervisorResolved: false, default);
        Assert.Equal("Pending", plain!.Status);
        Assert.False(plain.IsActive);
        Assert.False(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineA,
            version, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default, "POS-A", 101));
        Assert.Null(await Repository().TryBeginPairingAsync("Production", "S001", lineA, Guid.NewGuid(),
            DateTime.UtcNow.AddMinutes(2), version, DateTime.UtcNow, "TEST-OPERATOR", default));
        await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync("POS-A", lineB, null, 0, "POS-A", lineA, 101));

        // 主管结案：非终态写成可审计的 SupervisorResolved，各管理闸门按已结束放行。
        var resolved = await BackendRepository().AcknowledgeSessionAsync(
            "Production", "S001", "POS-A", sessionId, DateTimeOffset.UtcNow, supervisorResolved: true, default);
        Assert.Equal("SupervisorResolved", resolved!.Status);
        Assert.False(resolved.IsActive);
        var lease = Guid.NewGuid();
        Assert.True(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineA,
            version, lease, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, default, "POS-A", 101));
        await Repository().ReleaseConnectionTestLeaseAsync("Production", "S001", lineA, lease, default);
        await AssignAsync("POS-A", lineB, null, 0, "POS-A", lineA, 101,
            (await Repository().GetAsync("Production", "S001", lineB, default))!.UpdatedAt);
        Assert.Equal(lineB, (await SelectionAsync("POS-A"))!.TerminalId);
        var lineAVersion = (await Repository().GetAsync("Production", "S001", lineA, default))!.UpdatedAt!.Value;
        Assert.NotNull(await Repository().TryBeginPairingAsync("Production", "S001", lineA, Guid.NewGuid(),
            DateTime.UtcNow.AddMinutes(2), lineAVersion, DateTime.UtcNow, "TEST-OPERATOR", default));
    }

    [LinklyLineSqlServerFact]
    public async Task Supervisor_resolution_ack_keeps_linkly_final_status()
    {
        const string sessionId = "supervisor-resolution-final-session";
        await ExecuteAsync("""
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
                ([Environment],[StoreCode],[DeviceCode],[TerminalId],[SessionId],[Status],[IsActive],[ClientAcknowledgedAt])
            VALUES (N'Production',N'S001',N'POS-A',@Line,@Session,N'Failed',0,NULL);
            """, new("@Line", lineA), new("@Session", sessionId));

        var resolved = await BackendRepository().AcknowledgeSessionAsync(
            "Production", "S001", "POS-A", sessionId, DateTimeOffset.UtcNow, supervisorResolved: true, default);

        Assert.Equal("Failed", resolved!.Status);
        Assert.NotNull(resolved.ClientAcknowledgedAt);
    }

    [LinklyLineSqlServerFact]
    public async Task Connection_test_rejects_owner_unknown_session_without_terminal_id()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedSessionAsync("POS-A", null, "Unknown", true);
        Assert.False(await Repository().TryAcquireConnectionTestLeaseAsync("Production", "S001", lineA,
            version, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow, default, "POS-A", 101));
        Assert.Null((await Repository().GetAsync("Production", "S001", lineA, default))!.PairingAttemptId);
    }

    [LinklyLineSqlServerFact]
    public async Task Unassigned_line_connection_test_rejects_caller_sessions_on_another_line()
    {
        var blockingCases = new[]
        {
            (Status: "Completed", Acknowledged: true, IsActive: true),
            (Status: "Unknown", Acknowledged: true, IsActive: false),
            (Status: "Completed", Acknowledged: false, IsActive: false),
        };

        foreach (var item in blockingCases)
        {
            await SeedSessionAsync("POS-A", lineB, item.Status, item.Acknowledged, item.IsActive);
            Assert.False(await Repository().TryAcquireConnectionTestLeaseAsync(
                "Production", "S001", lineA, version, Guid.NewGuid(),
                DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow, default,
                expectedAssignedDeviceCode: null, expectedAssignmentRevision: 0,
                operationDeviceCode: "POS-A"));
            Assert.Null((await Repository().GetAsync("Production", "S001", lineA, default))!.PairingAttemptId);
            await ExecuteAsync("DELETE FROM [dbo].[POSM_LinklyCloudBackendSession];");
        }
    }

    [LinklyLineSqlServerFact]
    public Task Assignment_racing_session_creation_has_only_one_winner() => RaceWithSessionCreationAsync(false);

    [LinklyLineSqlServerFact]
    public Task Legacy_switch_racing_session_creation_has_only_one_winner() => RaceWithSessionCreationAsync(true);

    private async Task RaceWithSessionCreationAsync(bool legacy)
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        var sessionRepository = new SqlSugarLinklyCloudBackendAsyncRepository(CreateContext());
        var createSession = sessionRepository.TryCreateSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Production", StoreCode = "S001", DeviceCode = "POS-A",
            TerminalId = lineA, TerminalUpdatedAt = version, SelectionRevision = 101,
            SessionId = Guid.NewGuid().ToString("N"), Status = "Created", IsActive = true,
            UpdatedAt = DateTime.UtcNow,
        }, default);
        var assignment = CaptureAsync(() => legacy
            ? Repository().UpsertSelectionAsync("Production", "S001", "POS-A", lineB,
                101, DateTime.UtcNow, "TEST", default)
            : AssignAsync("POS-C", lineA, "POS-A", 101, "POS-B", null, 0));
        var results = await Task.WhenAll(createSession, assignment);
        Assert.Single(results, success => success);
        if (results[0])
        {
            Assert.Equal(lineA, (await SelectionAsync("POS-A"))!.TerminalId);
            Assert.Equal(101, (await SelectionAsync("POS-A"))!.Revision);
        }
        else
        {
            Assert.Equal(legacy ? lineB : lineA, (await SelectionAsync(legacy ? "POS-A" : "POS-B"))!.TerminalId);
        }
    }

    // ---- 业务要求：遗留的陈旧会话不能挡住下一单付款（真实 SQL：原子顶替 + 并发只有一个赢家） ----

    [LinklyLineSqlServerFact]
    public async Task Stale_active_session_is_superseded_in_the_same_transaction_as_the_new_session_insert()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("stale-active", "POS-A", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromMinutes(30));
        var before = await ReadSessionAsync("stale-active");

        var created = await BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default);

        Assert.True(created);
        var closed = await ReadSessionAsync("stale-active");
        Assert.Equal("SupervisorResolved", closed.Status);
        Assert.False(closed.IsActive);
        Assert.NotNull(closed.AcknowledgedAt);
        // 不改 UpdatedAt：孤儿对账按它判断年龄。
        Assert.Equal(before.UpdatedAt, closed.UpdatedAt);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [IsActive] = 1"));
    }

    [LinklyLineSqlServerFact]
    public async Task Fresh_active_session_still_blocks_a_new_transaction_session()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("fresh-active", "POS-A", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromMinutes(2));

        var created = await BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default);

        Assert.False(created);
        var untouched = await ReadSessionAsync("fresh-active");
        Assert.Equal("Pending", untouched.Status);
        Assert.True(untouched.IsActive);
        Assert.Null(untouched.AcknowledgedAt);
    }

    [LinklyLineSqlServerFact]
    public async Task Stale_unacknowledged_final_session_only_gets_an_acknowledgement_and_keeps_its_evidence()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync(
            "stale-approved", "POS-A", lineA, "Completed", isActive: false, acknowledged: false,
            TimeSpan.FromDays(12), txnRef: "TXN-APPROVED", transactionSuccess: true);
        var before = await ReadSessionAsync("stale-approved");

        var created = await BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default);

        Assert.True(created);
        var kept = await ReadSessionAsync("stale-approved");
        Assert.Equal("Completed", kept.Status);
        Assert.True(kept.TransactionSuccess);
        Assert.NotNull(kept.AcknowledgedAt);
        Assert.Equal(before.UpdatedAt, kept.UpdatedAt);
    }

    [LinklyLineSqlServerFact]
    public async Task Stale_session_of_another_device_on_the_same_line_is_superseded()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("stale-other", "POS-B", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromHours(3));

        var created = await BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default);

        Assert.True(created);
        var closed = await ReadSessionAsync("stale-other");
        Assert.Equal("SupervisorResolved", closed.Status);
        Assert.False(closed.IsActive);
    }

    [LinklyLineSqlServerFact]
    public async Task Settlement_session_creation_does_not_supersede_a_stale_active_session()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("stale-active", "POS-A", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromMinutes(30));

        var created = await BackendRepository().TryCreateSessionAsync(
            NewTransactionSession("POS-A", operationType: "Settlement"), default);

        Assert.False(created);
        var untouched = await ReadSessionAsync("stale-active");
        Assert.Equal("Pending", untouched.Status);
        Assert.True(untouched.IsActive);
    }

    [LinklyLineSqlServerFact]
    public async Task Txn_ref_collision_leaves_the_stale_session_untouched()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("stale-active", "POS-A", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromMinutes(30));
        // 另一台设备上已存在同一个 attempt 的 TxnRef（已确认的失败，不占用线路）。
        await SeedAgedSessionAsync(
            "same-attempt-earlier", "POS-C", null, "Failed", isActive: false, acknowledged: true,
            TimeSpan.FromMinutes(20), txnRef: "P000000000000777");

        var created = await BackendRepository().TryCreateSessionAsync(
            NewTransactionSession("POS-A", txnRef: "P000000000000777"), default);

        // 其它校验没过就不能先顶替：整个事务一个写入都不留下。
        Assert.False(created);
        var untouched = await ReadSessionAsync("stale-active");
        Assert.Equal("Pending", untouched.Status);
        Assert.True(untouched.IsActive);
        Assert.Null(untouched.AcknowledgedAt);
    }

    [LinklyLineSqlServerFact]
    public async Task Concurrent_transaction_creates_over_a_stale_session_have_only_one_winner()
    {
        await SeedSelectionAsync("POS-A", lineA, 101);
        await SeedAgedSessionAsync("stale-active", "POS-A", lineA, "Pending", isActive: true, acknowledged: false, TimeSpan.FromMinutes(30));

        var results = await Task.WhenAll(
            BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default),
            BackendRepository().TryCreateSessionAsync(NewTransactionSession("POS-A"), default));

        // 第二个事务在 SERIALIZABLE 下等到第一个提交，随后看到的是一条新鲜的活动会话，只能被挡住。
        Assert.Single(results, success => success);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [IsActive] = 1"));
        Assert.Equal("SupervisorResolved", (await ReadSessionAsync("stale-active")).Status);
    }

    private LinklyCloudBackendSessionRecord NewTransactionSession(
        string device, string operationType = "Transaction", string? txnRef = null) => new()
    {
        Environment = "Production", StoreCode = "S001", DeviceCode = device,
        TerminalId = lineA, TerminalUpdatedAt = version, SelectionRevision = 101,
        SessionId = Guid.NewGuid().ToString("D"), Status = "Pending", OperationType = operationType,
        TxnRef = txnRef, IsActive = true, UpdatedAt = DateTime.UtcNow,
    };

    private Task SeedAgedSessionAsync(
        string sessionId, string device, Guid? line, string status, bool isActive, bool acknowledged,
        TimeSpan age, string? txnRef = null, bool? transactionSuccess = null) => ExecuteAsync("""
        INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
            ([Environment],[StoreCode],[DeviceCode],[TerminalId],[SessionId],[Status],[TxnRef],
             [TransactionSuccess],[IsActive],[ClientAcknowledgedAt],[UpdatedAt])
        VALUES (N'Production',N'S001',@Device,@Line,@Session,@Status,@TxnRef,@Success,@IsActive,@Ack,@UpdatedAt);
        """, new("@Device", device), new("@Line", (object?)line ?? DBNull.Value),
        new("@Session", sessionId), new("@Status", status), new("@TxnRef", (object?)txnRef ?? DBNull.Value),
        new("@Success", (object?)transactionSuccess ?? DBNull.Value), new("@IsActive", isActive),
        acknowledged
            ? At("@Ack", DateTime.UtcNow)
            : new SqlParameter("@Ack", System.Data.SqlDbType.DateTime2) { Value = DBNull.Value },
        At("@UpdatedAt", DateTime.UtcNow - age));

    private async Task<(string Status, bool IsActive, DateTime? AcknowledgedAt, DateTime UpdatedAt, bool? TransactionSuccess)>
        ReadSessionAsync(string sessionId)
    {
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT [Status],[IsActive],[ClientAcknowledgedAt],[UpdatedAt],[TransactionSuccess]
            FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [SessionId] = @Session;
            """, sql);
        command.Parameters.AddWithValue("@Session", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"会话 {sessionId} 不存在");
        return (
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetBoolean(4));
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private Task AssignAsync(string caller, Guid line, string? owner, long sourceRevision,
        string? target, Guid? targetLine, long targetRevision, DateTime? expectedVersion = null) =>
        Repository().AssignTerminalAsync("Production", "S001", caller, line, expectedVersion ?? version,
            owner, sourceRevision, target, targetLine, targetRevision, DateTime.UtcNow, "TEST-OPERATOR", default);

    private Task<LinklyCloudDeviceSelectionRecord?> SelectionAsync(string device) =>
        Repository().GetSelectionAsync("Production", "S001", device, default);

    private async Task<string?> OwnerAsync(Guid line) =>
        (await Repository().ListAssignableDevicesAsync("Production", "S001", default))
            .FirstOrDefault(item => item.SelectedTerminalId == line)?.DeviceCode;

    private async Task AssertPairingPreservedAsync()
    {
        var lines = await Repository().ListAsync("Production", "S001", default);
        Assert.Equal(3, lines.Count);
        foreach (var line in lines)
        {
            var actual = (await Repository().GetAsync("Production", "S001", line.TerminalId, default))!;
            Assert.Equal("Ready", actual.PairingState);
            Assert.Equal("test-secret", actual.Secret);
            Assert.Equal("test-pos-id", actual.PosId);
        }
    }

    private SqlSugarLinklyCloudTerminalRepository Repository()
        => new(CreateContext(), new TestProtector());

    private SqlSugarLinklyCloudBackendAsyncRepository BackendRepository()
        => new(CreateContext());

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "20",
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private Task SeedLineAsync(Guid id, int lane) => ExecuteAsync("""
        INSERT INTO [dbo].[POSM_LinklyCloudTerminal]
            ([TerminalId],[Environment],[StoreCode],[LaneNo],[DisplayName],[Username],[Password],
             [Secret],[PosId],[PairingState],[CreatedAt],[UpdatedAt],[LastHealthStatus],[LastHealthAt])
        VALUES (@Id,N'Production',N'S001',@Lane,@Name,@Name,N'test-password',N'test-secret',
                N'test-pos-id',N'Ready',@At,@At,N'Healthy',@At);
        """, new("@Id", id), new("@Lane", lane), new("@Name", $"Test line {lane}"), At("@At", version));

    private Task SeedSelectionAsync(string device, Guid line, long revision) => ExecuteAsync("""
        INSERT INTO [dbo].[POSM_LinklyCloudDeviceSelection]
            ([Environment],[StoreCode],[DeviceCode],[TerminalId],[Revision])
        VALUES (N'Production',N'S001',@Device,@Line,@Revision);
        """, new("@Device", device), new("@Line", line), new("@Revision", revision));

    private Task SeedSessionAsync(
        string device, Guid? line, string status, bool acknowledged, bool isActive = false) => ExecuteAsync("""
        INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
            ([Environment],[StoreCode],[DeviceCode],[TerminalId],[SessionId],[Status],[IsActive],[ClientAcknowledgedAt])
        VALUES (N'Production',N'S001',@Device,@Line,@Session,@Status,@IsActive,@Ack);
        """, new("@Device", device), new("@Line", (object?)line ?? DBNull.Value),
        new("@Session", Guid.NewGuid().ToString()), new("@Status", status),
        new("@IsActive", isActive),
        new("@Ack", acknowledged ? DateTime.UtcNow : DBNull.Value));

    private Task ExecuteAsync(string sql, params SqlParameter[] parameters) => ExecuteAtAsync(connection, sql, parameters);

    private static async Task ExecuteAtAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlParameter At(string name, DateTime value) => new(name, System.Data.SqlDbType.DateTime2) { Value = value };

    private static async Task<bool> CaptureAsync(Func<Task> action)
    {
        try { await action(); return true; }
        // 竞争失败必须是可恢复的绑定冲突，死锁、超时和 SQL 错误不能被误算作通过。
        catch (LinklyCloudTerminalSelectionConflictException) { return false; }
        catch (LinklyCloudTerminalAssignedException) { return false; }
    }

    private sealed class TestProtector : ILinklyCloudTerminalCredentialProtector
    {
        public string ProtectPassword(string value) => value;
        public string UnprotectPassword(string value) => value;
        public string ProtectSecret(string value) => value;
        public string UnprotectSecret(string value) => value;
    }

    private sealed class UnusedPairingTransport : ILinklyCloudPairingTransport
    {
        public Task<LinklyCloudPairingTransportResponse> PairAsync(string authBaseUrl,
            string username, string password, string pairCode, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("线路分配不能调用配对服务。");
    }
}
