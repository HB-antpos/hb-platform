using System.Net;
using BlazorApp.Shared.Security;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudTerminalServiceTests
{
    [Fact]
    public void Management_connection_test_lease_is_non_financial_and_fail_closed()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.TryAcquireConnectionTestLeaseSql;
        Assert.Contains("PairingAttemptId", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PairingLeaseExpiresAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Status] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ClientAcknowledgedAt] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sale", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Refund", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assignment_sql_keeps_replace_atomic_and_checks_target_registration()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.AssignTerminalSql;
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("设备状态] = 1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExpectedSourceRevision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExpectedTargetRevision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE FROM [dbo].[POSM_LinklyCloudDeviceSelection]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_selection_guard_rejects_unknown_acknowledged_sessions()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.UpsertSelectionSql;
        Assert.Contains("Status] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Status] NOT IN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ClientAcknowledgedAt] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Management_gates_share_final_status_set_including_supervisor_resolution()
    {
        // 主管结案写入的终态必须在每条管理闸门里与 Linkly 终态同口径，漏掉一条就会留下永久阻塞的入口。
        var gates = new[]
        {
            SqlSugarLinklyCloudTerminalRepository.AssignTerminalSql,
            SqlSugarLinklyCloudTerminalRepository.TryAcquireConnectionTestLeaseSql,
            SqlSugarLinklyCloudTerminalRepository.UpsertSelectionSql,
            SqlSugarLinklyCloudTerminalRepository.TryBeginPairingSql,
            SqlSugarLinklyCloudTerminalRepository.TryAcquireOperationLeaseSql,
        };

        foreach (var sql in gates)
        {
            var finalSets = System.Text.RegularExpressions.Regex.Matches(sql, @"\[Status\] NOT IN \(([^)]*)\)");
            Assert.NotEmpty(finalSets);
            Assert.All(finalSets, match => Assert.Equal(
                "N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved'",
                match.Groups[1].Value));
        }
    }

    [Fact]
    public void Sql_repository_datetime_parameters_preserve_datetime2_precision()
    {
        var value = new DateTime(2026, 9, 6, 3, 57, 54, 558, DateTimeKind.Utc)
            .AddTicks(8675);
        var sqlSugar = new SqlSugarClient(new ConnectionConfig
        {
            DbType = SqlSugar.DbType.SqlServer,
            ConnectionString = "Server=127.0.0.1,1;Database=NoConnect;Connect Timeout=1"
        });

        var parameter = SqlSugarLinklyCloudTerminalRepository.DateTime2Parameter("@At", value);
        var converted = Assert.IsType<SqlParameter>(
            Assert.Single(sqlSugar.Ado.ToIDbDataParameter(new[] { parameter })));
        var defaultConverted = Assert.IsType<SqlParameter>(
            Assert.Single(sqlSugar.Ado.ToIDbDataParameter(
                new[] { new SugarParameter("@DefaultAt", value) })));

        Assert.Equal(System.Data.DbType.DateTime2, parameter.DbType);
        Assert.Equal(value, parameter.Value);
        Assert.Equal(System.Data.SqlDbType.DateTime2, converted.SqlDbType);
        Assert.Equal(System.Data.SqlDbType.DateTime, defaultConverted.SqlDbType);
        Assert.NotEqual(value, new System.Data.SqlTypes.SqlDateTime(value).Value);
    }

    [Fact]
    public void Sql_repository_readiness_fences_require_current_credential_version()
    {
        Assert.Contains(
            "[CredentialProtectionVersion] = 1",
            SqlSugarLinklyCloudTerminalRepository.UpsertSelectionSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "[CredentialProtectionVersion] = 1",
            SqlSugarLinklyCloudTerminalRepository.TryBeginPairingSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "[CredentialProtectionVersion] = 1",
            SqlSugarLinklyCloudTerminalRepository.TryAcquireOperationLeaseSql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_repository_selection_uses_session_terminal_selection_lock_order()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.UpsertSelectionSql;

        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ClientAcknowledgedAt] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingState] = N'Ready'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("51002", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("51003", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("51004", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[TerminalId] = @TerminalId", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[DeviceCode] <> @DeviceCode", sql, StringComparison.OrdinalIgnoreCase);
        var sessionIndex = sql.IndexOf("POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase);
        var terminalIndex = sql.IndexOf("POSM_LinklyCloudTerminal", StringComparison.OrdinalIgnoreCase);
        var selectionIndex = sql.IndexOf("POSM_LinklyCloudDeviceSelection", StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionIndex >= 0 && terminalIndex > sessionIndex && selectionIndex > terminalIndex);
    }

    [Fact]
    public void Sql_repository_selection_insert_uses_opaque_js_safe_revision()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.UpsertSelectionSql;

        Assert.Contains("@NewRevision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "@TerminalId, 1, @UpdatedAt",
            sql,
            StringComparison.OrdinalIgnoreCase);

        var revisions = Enumerable.Range(0, 16)
            .Select(_ => LinklyCloudSelectionRevision.CreateInitial())
            .ToArray();
        Assert.All(revisions, revision =>
        {
            Assert.InRange(revision, 2, LinklyCloudSelectionRevision.MaxJsSafeRevision);
        });
        Assert.Equal(revisions.Length, revisions.Distinct().Count());
    }

    [Theory]
    [InlineData("SQL Server error 51004")]
    [InlineData("Violation of UNIQUE KEY INDEX 'UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal'.")]
    public void Sql_repository_maps_terminal_assignment_guard_and_unique_index_to_same_domain_error(
        string databaseMessage)
    {
        Assert.True(SqlSugarLinklyCloudTerminalRepository.IsTerminalAssignmentViolation(
            new InvalidOperationException(databaseMessage)));
    }

    [Fact]
    public void Sql_repository_pairing_marker_uses_session_first_serializable_fence()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.TryBeginPairingSql;

        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POSM_LinklyCloudBackendSession", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ClientAcknowledgedAt] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingState] = N'Unknown'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingAttemptId] = @PairingAttemptId", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingLeaseExpiresAt] = @PairingLeaseExpiresAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OUTPUT inserted.[TerminalId]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[UpdatedAt] = @ExpectedUpdatedAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            sql.IndexOf("POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase)
            < sql.IndexOf("UPDATE [dbo].[POSM_LinklyCloudTerminal]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sql_repository_operation_lease_uses_session_terminal_selection_mode_lock_order()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.TryAcquireOperationLeaseSql;

        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ClientAcknowledgedAt] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[UpdatedAt] = @ExpectedTerminalUpdatedAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingAttemptId] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingLeaseExpiresAt] <= @Now", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Revision] = @ExpectedSelectionRevision", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ISNULL(@Mode, N'Legacy') = N'Active'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingAttemptId] = @OperationLeaseId", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingLeaseExpiresAt] = @OperationLeaseExpiresAt", sql, StringComparison.OrdinalIgnoreCase);
        var sessionIndex = sql.IndexOf("POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase);
        var terminalIndex = sql.IndexOf("POSM_LinklyCloudTerminal", StringComparison.OrdinalIgnoreCase);
        var selectionIndex = sql.IndexOf("POSM_LinklyCloudDeviceSelection", StringComparison.OrdinalIgnoreCase);
        var modeIndex = sql.IndexOf("POSM_LinklyCloudConfigurationMode", StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionIndex >= 0 && terminalIndex > sessionIndex && selectionIndex > terminalIndex && modeIndex > selectionIndex);
    }

    [Fact]
    public void Sql_repository_health_snapshot_uses_configuration_version_cas_without_mutating_configuration()
    {
        var sql = SqlSugarLinklyCloudTerminalRepository.TryRecordHealthSql;

        Assert.Contains("SET [LastHealthStatus] = @HealthStatus", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LastHealthAt] = @CheckedAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[UpdatedAt] = @ExpectedTerminalUpdatedAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LastHealthAt] IS NULL OR [LastHealthAt] <= @CheckedAt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET [UpdatedAt]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[PairingState]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Secret]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[PosId]", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordHealthAsync_updates_only_the_matching_configuration_revision()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var original = CreateTerminal(terminalId, "S01", "Production", 1, "Front");
        var repository = new FakeTerminalRepository { Terminals = [original] };
        var service = CreateService(repository);
        var context = new LinklyCloudTerminalPaymentContext(
            original,
            new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 4
            });
        var checkedAt = new DateTime(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(await service.RecordHealthAsync(context, "Healthy", checkedAt, CancellationToken.None));
        var updated = Assert.Single(repository.Terminals);
        Assert.Equal("Healthy", updated.LastHealthStatus);
        Assert.Equal(checkedAt, updated.LastHealthAt);
        Assert.Equal(original.UpdatedAt, updated.UpdatedAt);
        Assert.Equal(original.PairingState, updated.PairingState);
        Assert.Equal(original.Secret, updated.Secret);
        Assert.Equal(original.PosId, updated.PosId);

        Assert.False(await service.RecordHealthAsync(
            context,
            "Unhealthy",
            checkedAt.AddTicks(-1),
            CancellationToken.None));
        var newer = Assert.Single(repository.Terminals);
        Assert.Equal("Healthy", newer.LastHealthStatus);
        Assert.Equal(checkedAt, newer.LastHealthAt);

        repository.Terminals[0] = newer with { UpdatedAt = checkedAt.AddTicks(1) };
        Assert.False(await service.RecordHealthAsync(context, "Unhealthy", checkedAt.AddMinutes(1), CancellationToken.None));
        var drifted = Assert.Single(repository.Terminals);
        Assert.Equal("Healthy", drifted.LastHealthStatus);
        Assert.Equal(checkedAt, drifted.LastHealthAt);
        Assert.Equal(checkedAt.AddTicks(1), drifted.UpdatedAt);
    }

    [Fact]
    public async Task GetTerminalsAsync_returns_claim_scoped_safe_list_and_current_selection()
    {
        var selectedId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var otherId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var repository = new FakeTerminalRepository
        {
            Terminals =
            [
                CreateTerminal(selectedId, "S01", "Production", 1, "Front"),
                CreateTerminal(otherId, "OTHER", "Production", 2, "Other Store")
            ],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = selectedId,
                Revision = 4
            }
        };
        var service = CreateService(repository);

        var response = await service.GetTerminalsAsync("S01", "POS-01", "Production", CancellationToken.None);

        var terminal = Assert.Single(response.Terminals);
        Assert.Equal(selectedId, response.SelectedTerminalId);
        Assert.Equal(4, response.SelectionRevision);
        Assert.Equal("Legacy", response.Mode);
        Assert.Equal("Front", terminal.DisplayName);
        Assert.True(terminal.IsReady);
    }

    [Fact]
    public async Task GetTerminalsAsync_returns_scope_configuration_mode_even_when_list_is_empty()
    {
        var repository = new FakeTerminalRepository { Mode = "Active" };
        var service = CreateService(repository);

        var response = await service.GetTerminalsAsync(
            "S01", "POS-01", "Production", CancellationToken.None);

        Assert.Equal("Active", response.Mode);
        Assert.Empty(response.Terminals);
    }

    [Fact]
    public async Task GetConfigurationModeAsync_normalizes_scope_and_returns_repository_mode()
    {
        var repository = new FakeTerminalRepository { Mode = "Active" };
        var service = CreateService(repository);

        var mode = await service.GetConfigurationModeAsync(
            " sandbox ", " S01 ", CancellationToken.None);

        Assert.Equal("Active", mode);
        Assert.Equal("Sandbox", repository.LastModeEnvironment);
        Assert.Equal("S01", repository.LastModeStoreCode);
    }

    [Fact]
    public async Task SelectTerminalAsync_requires_matching_revision_and_persists_device_scope()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 3
            }
        };
        var service = CreateService(repository);

        await Assert.ThrowsAsync<LinklyCloudTerminalSelectionConflictException>(() =>
            service.SelectTerminalAsync(
                "S01",
                "POS-01",
                new LinklyCloudTerminalSelectionRequest("Production", terminalId, 2),
                "device:POS-01",
                CancellationToken.None));

        var response = await service.SelectTerminalAsync(
            "S01",
            "POS-01",
            new LinklyCloudTerminalSelectionRequest("Production", terminalId, 3),
            "device:POS-01",
            CancellationToken.None);

        Assert.Equal(4, response.Revision);
        Assert.Equal("POS-01", repository.Selection!.DeviceCode);
    }

    [Fact]
    public async Task SelectTerminalAsync_rejects_legacy_credential_version_even_if_state_says_ready()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front") with
            {
                CredentialProtectionVersion =
                    BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection.LegacyPlaintextVersion
            }]
        };
        var service = CreateService(repository);

        await Assert.ThrowsAsync<LinklyCloudTerminalNotReadyException>(() =>
            service.SelectTerminalAsync(
                "S01",
                "POS-01",
                new LinklyCloudTerminalSelectionRequest("Production", terminalId, 0),
                "device:POS-01",
                CancellationToken.None));

        Assert.Null(repository.Selection);
    }

    [Fact]
    public async Task SelectTerminalAsync_rejects_terminal_assigned_to_another_pos()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var terminalRepository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            OtherSelections =
            [
                new LinklyCloudDeviceSelectionRecord
                {
                    Environment = "Production",
                    StoreCode = "S01",
                    DeviceCode = "POS-02",
                    TerminalId = terminalId,
                    Revision = 1
                }
            ]
        };
        var service = CreateService(terminalRepository);

        var exception = await Assert.ThrowsAsync<LinklyCloudTerminalAssignedException>(() =>
            service.SelectTerminalAsync(
                "S01",
                "POS-01",
                new LinklyCloudTerminalSelectionRequest("Production", terminalId, 0),
                "device:POS-01",
                CancellationToken.None));

        Assert.Contains("another POS", exception.Message, StringComparison.Ordinal);
        Assert.Null(terminalRepository.Selection);
    }

    [Fact]
    public async Task PairTerminalAsync_uses_terminal_credentials_and_returns_no_secret()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Sandbox", 1, "Front") with
            {
                Username = "lane-user",
                Password = "lane-password"
            }]
        };
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "paired-secret"));
        var service = CreateService(repository, transport);

        var response = await service.PairTerminalAsync(
            "S01",
            "POS-01",
            terminalId,
            new LinklyCloudBackendPairRequest("Sandbox", "123456"),
            "device:POS-01",
            CancellationToken.None);

        Assert.Equal("lane-user", transport.Username);
        Assert.Equal("lane-password", transport.Password);
        Assert.Equal("Ready", response.PairingState);
        Assert.True(response.IsReady);
        Assert.Equal("paired-secret", repository.Terminals.Single().Secret);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PairTerminalAsync_timeout_marks_terminal_unknown_without_retry()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")]
        };
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.RequestTimeout, null));
        var service = CreateService(repository, transport);

        await Assert.ThrowsAsync<LinklyCloudPairingTimeoutException>(() =>
            service.PairTerminalAsync(
                "S01",
                "POS-01",
                terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(1, transport.CallCount);
        Assert.Equal("Unknown", repository.Terminals.Single().PairingState);
        Assert.NotNull(repository.Terminals.Single().PairingAttemptId);
        Assert.True(repository.Terminals.Single().PairingLeaseExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task PairTerminalAsync_database_lease_blocks_second_service_instance()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")]
        };
        var firstTransport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.RequestTimeout, null));
        var secondTransport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "must-not-run"));
        var firstService = CreateService(repository, firstTransport);
        var secondService = CreateService(repository, secondTransport);

        await Assert.ThrowsAsync<LinklyCloudPairingTimeoutException>(() =>
            firstService.PairTerminalAsync(
                "S01", "POS-01", terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01", CancellationToken.None));
        await Assert.ThrowsAsync<LinklyCloudPairingInProgressException>(() =>
            secondService.PairTerminalAsync(
                "S01", "POS-02", terminalId,
                new LinklyCloudBackendPairRequest("Production", "654321"),
                "device:POS-02", CancellationToken.None));

        Assert.Equal(1, firstTransport.CallCount);
        Assert.Equal(0, secondTransport.CallCount);
    }

    [Fact]
    public async Task Terminal_operation_lease_and_pairing_are_mutually_exclusive()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 1
            }
        };
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "must-not-run"));
        var service = CreateService(repository, transport);
        var context = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None);
        Assert.NotNull(context);

        var lease = await service.AcquireOperationLeaseAsync(
            "Production", "S01", "POS-01", context!, CancellationToken.None);

        await Assert.ThrowsAsync<LinklyCloudPairingInProgressException>(() =>
            service.PairTerminalAsync(
                "S01", "POS-02", terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-02", CancellationToken.None));

        Assert.Equal(0, transport.CallCount);
        Assert.Equal(lease.LeaseId, repository.Terminals.Single().PairingAttemptId);
    }

    [Fact]
    public async Task Pairing_lease_blocks_terminal_operation_lease()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 1
            }
        };
        var service = CreateService(
            repository,
            new FakePairingTransport(new LinklyCloudPairingTransportResponse(HttpStatusCode.RequestTimeout, null)));
        var context = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None);
        Assert.NotNull(context);
        await Assert.ThrowsAsync<LinklyCloudPairingTimeoutException>(() =>
            service.PairTerminalAsync(
                "S01", "POS-01", terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01", CancellationToken.None));

        await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.AcquireOperationLeaseAsync(
                "Production", "S01", "POS-01", context!, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_operation_lease_can_be_reacquired_by_another_service_instance()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 1
            }
        };
        var firstService = CreateService(repository);
        var secondService = CreateService(repository);
        var context = await firstService.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None);
        Assert.NotNull(context);
        var firstLease = await firstService.AcquireOperationLeaseAsync(
            "Production", "S01", "POS-01", context!, CancellationToken.None);
        repository.Terminals[0] = repository.Terminals[0] with
        {
            PairingLeaseExpiresAt = DateTime.UtcNow.AddTicks(-1)
        };

        var secondLease = await secondService.AcquireOperationLeaseAsync(
            "Production", "S01", "POS-01", context!, CancellationToken.None);

        Assert.NotEqual(firstLease.LeaseId, secondLease.LeaseId);
        Assert.Equal(secondLease.LeaseId, repository.Terminals.Single().PairingAttemptId);
    }

    [Fact]
    public async Task PairTerminalAsync_rejects_missing_terminal_credentials_before_upstream_call()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front") with
            {
                Password = " "
            }]
        };
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "paired-secret"));
        var service = CreateService(repository, transport);

        await Assert.ThrowsAsync<LinklyCloudPairingCredentialMissingException>(() =>
            service.PairTerminalAsync(
                "S01",
                "POS-01",
                terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task PairTerminalAsync_rejects_active_or_unacknowledged_session_before_upstream_call()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            PairingBlocked = true,
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")]
        };
        var sessions = new InMemoryLinklyCloudBackendAsyncRepository();
        await sessions.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Production",
            StoreCode = "S01",
            DeviceCode = "POS-02",
            TerminalId = terminalId,
            SessionId = "completed-unacknowledged",
            Status = "Completed",
            IsActive = false,
            ClientAcknowledgedAt = null,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "paired-secret"));
        var service = CreateService(repository, transport, sessions);

        await Assert.ThrowsAsync<LinklyCloudTerminalSessionActiveException>(() =>
            service.PairTerminalAsync(
                "S01",
                "POS-01",
                terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(0, transport.CallCount);
        Assert.Equal("Ready", repository.Terminals.Single().PairingState);
    }

    [Fact]
    public async Task PairTerminalAsync_does_not_overwrite_newer_web_edit_when_upstream_returns()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")]
        };
        var transport = new FakePairingTransport(
            new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "stale-paired-secret"),
            () =>
            {
                var current = repository.Terminals.Single();
                repository.Terminals[0] = current with
                {
                    Password = "new-web-password",
                    Secret = null,
                    PosId = null,
                    PairingState = "NeedsRepair",
                    UpdatedAt = current.UpdatedAt!.Value.AddTicks(1)
                };
            });
        var service = CreateService(repository, transport);

        await Assert.ThrowsAsync<LinklyCloudPairingPersistenceException>(() =>
            service.PairTerminalAsync(
                "S01",
                "POS-01",
                terminalId,
                new LinklyCloudBackendPairRequest("Production", "123456"),
                "device:POS-01",
                CancellationToken.None));

        var saved = repository.Terminals.Single();
        Assert.Equal("new-web-password", saved.Password);
        Assert.Equal("NeedsRepair", saved.PairingState);
        Assert.Null(saved.Secret);
        Assert.Null(saved.PosId);
        Assert.NotNull(saved.PairingAttemptId);
        Assert.True(saved.PairingLeaseExpiresAt > DateTime.UtcNow);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task ResolvePaymentTerminalAsync_active_mode_fails_closed_on_stale_selection()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = LinklyCloudSelectionRevision.CreateInitial()
            }
        };
        var service = CreateService(repository);

        await Assert.ThrowsAsync<LinklyCloudTerminalSelectionConflictException>(() =>
            service.ResolvePaymentTerminalAsync(
                "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None));

        var resolved = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, repository.Selection.Revision, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(terminalId, resolved.Terminal.TerminalId);
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Draft")]
    public async Task ResolvePaymentTerminalAsync_non_active_mode_preserves_legacy_path(string mode)
    {
        var repository = new FakeTerminalRepository { Mode = mode };
        var service = CreateService(repository);

        var resolved = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", null, null, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ConnectionTestAsync_tests_unassigned_ready_terminal_without_selection_or_financial_transport()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")] };
        var transport = new FakeStatusTransport(new LinklyCloudBackendTransportResponse(HttpStatusCode.OK, "{\"Response\":{\"Success\":true,\"ResponseCode\":\"T0\",\"ResponseText\":\"Connected\"}}"));
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(), backendTransport: transport);

        var response = await service.ConnectionTestAsync("S01", "POS-01", terminalId,
            new LinklyCloudTerminalConnectionTestRequest("Production", repository.Terminals[0].UpdatedAt!.Value.Ticks.ToString(), null, 0), CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal("connected", response.Status);
        Assert.Equal(1, transport.StatusCalls);
        // Linkly 的 sessionId 必须是 UUID，审计用途前缀不能进入供应商请求路径。
        var statusRequest = Assert.IsType<LinklyCloudBackendTransportStatusRequest>(transport.LastStatusRequest);
        Assert.True(Guid.TryParseExact(statusRequest.SessionId, "D", out var sessionId));
        Assert.Equal(sessionId.ToString("D"), statusRequest.SessionId);
        Assert.Equal('4', statusRequest.SessionId[14]);
        Assert.Contains(statusRequest.SessionId[19], "89ab");
        Assert.Equal(0, transport.TransactionCalls);
        Assert.Null(repository.Selection);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
    }

    [Fact]
    public async Task ConnectionTestAsync_incomplete_reply_retains_lease_and_blocks_another_test()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")] };
        var transport = new FakeStatusTransport(new LinklyCloudBackendTransportResponse(HttpStatusCode.OK, "{\"ResponseCode\":\"91\"}"));
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(), backendTransport: transport);
        var request = new LinklyCloudTerminalConnectionTestRequest("Production", repository.Terminals[0].UpdatedAt!.Value.Ticks.ToString(), null, 0);

        var failed = await service.ConnectionTestAsync("S01", "POS-01", terminalId, request, CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.Equal("unknown", failed.Status);
        Assert.Equal(0, repository.ReleasedConnectionLeases);

        // M10：结果不明时仍保护终端，但租约缩短为检测类短租约，不再沿用覆盖整次往返的 9 分钟租约。
        Assert.Equal(1, repository.ShortenedLeases);
        var leaseExpiresAt = repository.Terminals[0].PairingLeaseExpiresAt;
        Assert.NotNull(leaseExpiresAt);
        Assert.True(leaseExpiresAt > DateTime.UtcNow);
        Assert.True(leaseExpiresAt <= DateTime.UtcNow + LinklyCloudTerminalService.ProbeAmbiguousLeaseDuration);

        // 租约期间再次检测：专用错误码（Probing）+ 剩余秒数，而不是笼统的“忙/有未确认操作”。
        var blocked = await Assert.ThrowsAsync<LinklyCloudTerminalProbingException>(() =>
            service.ConnectionTestAsync("S01", "POS-01", terminalId, request, CancellationToken.None));
        Assert.InRange(
            blocked.RetryAfterSeconds,
            1,
            (int)LinklyCloudTerminalService.ProbeAmbiguousLeaseDuration.TotalSeconds);
    }

    [Theory]
    [InlineData("{\"Response\":{\"Success\":false,\"ResponseCode\":\"T0\"}}")]
    [InlineData("{\"Response\":{\"Success\":true,\"ResponseCode\":\"05\"}}")]
    public async Task ConnectionTestAsync_requires_both_provider_success_and_success_code(string body)
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")] };
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(),
            backendTransport: new FakeStatusTransport(new LinklyCloudBackendTransportResponse(HttpStatusCode.OK, body)));
        var response = await service.ConnectionTestAsync("S01", "POS-01", terminalId,
            new LinklyCloudTerminalConnectionTestRequest("Production", repository.Terminals[0].UpdatedAt!.Value.Ticks.ToString(), null, 0), CancellationToken.None);
        Assert.False(response.Succeeded);
        Assert.NotEqual("connected", response.Status);
    }

    [Fact]
    public async Task ConnectionTestAsync_credential_failure_returns_needs_repair()
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider { Failure = new LinklyCloudTerminalCredentialUnavailableException() }, backendTransport: new FakeStatusTransport());
        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);
        Assert.Equal("needs-repair", response.Status);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
    }

    [Fact]
    public async Task ConnectionTestAsync_timeout_returns_unknown_and_keeps_lease()
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(), backendTransport: new FakeStatusTransport { Failure = new OperationCanceledException() });
        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);
        Assert.Equal("unknown", response.Status);
        Assert.Equal(0, repository.ReleasedConnectionLeases);
    }

    [Fact]
    public async Task ConnectionTestAsync_rejected_terminal_secret_returns_needs_repair_and_releases_lease()
    {
        // M9：token 端点 401/403（secret 失效）映射为专用异常，连接检测给出 needs-repair，而不是通用错误。
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var transport = new FakeStatusTransport();
        var service = CreateService(
            repository,
            tokenProvider: new FakeTokenProvider { Failure = new LinklyCloudTerminalRepairRequiredException(HttpStatusCode.Unauthorized) },
            backendTransport: transport);

        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);

        Assert.Equal("needs-repair", response.Status);
        Assert.False(response.Succeeded);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
    }

    [Fact]
    public async Task ConnectionTestAsync_token_endpoint_outage_returns_unknown_and_releases_lease()
    {
        // 认证端暂时不可用发生在触碰终端之前：结果为“未确认”，租约立即释放（终端并未处于结果不明状态）。
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var service = CreateService(
            repository,
            tokenProvider: new FakeTokenProvider { Failure = new LinklyCloudBackendTokenUnavailableException(HttpStatusCode.TooManyRequests, "throttled") },
            backendTransport: new FakeStatusTransport());

        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);

        Assert.Equal("unknown", response.Status);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
        Assert.Equal(0, repository.ShortenedLeases);
    }

    [Fact]
    public async Task Ready_terminal_held_by_a_probe_lease_reports_probing_with_capped_remaining_seconds()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 1
            }
        };
        var service = CreateService(repository);
        var context = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None);
        await service.AcquireOperationLeaseAsync("Production", "S01", "POS-01", context!, CancellationToken.None);

        // 请求仍在飞行：租约到期时间是 9 分钟的悲观上界，对外提示的剩余秒数不超过检测类短租约时长。
        var inFlight = await Assert.ThrowsAsync<LinklyCloudTerminalProbingException>(() =>
            service.AcquireOperationLeaseAsync("Production", "S01", "POS-01", context!, CancellationToken.None));
        Assert.Equal(
            (int)LinklyCloudTerminalService.ProbeAmbiguousLeaseDuration.TotalSeconds,
            inFlight.RetryAfterSeconds);
        Assert.IsAssignableFrom<LinklyCloudBackendActiveTransactionException>(inFlight);
    }

    [Fact]
    public async Task ShortenOperationLeaseAsync_shortens_only_the_matching_lease_and_never_extends()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository
        {
            Mode = "Active",
            Terminals = [CreateTerminal(terminalId, "S01", "Production", 1, "Front")],
            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = 1
            }
        };
        var service = CreateService(repository);
        var context = await service.ResolvePaymentTerminalAsync(
            "Production", "S01", "POS-01", terminalId, 1, CancellationToken.None);
        var lease = await service.AcquireOperationLeaseAsync(
            "Production", "S01", "POS-01", context!, CancellationToken.None);
        Assert.True(repository.Terminals.Single().PairingLeaseExpiresAt > DateTime.UtcNow.AddMinutes(8));

        // 他人的租约号：不动。
        await service.ShortenOperationLeaseAsync("Production", "S01", terminalId, Guid.NewGuid(), CancellationToken.None);
        Assert.True(repository.Terminals.Single().PairingLeaseExpiresAt > DateTime.UtcNow.AddMinutes(8));

        await service.ShortenOperationLeaseAsync("Production", "S01", terminalId, lease.LeaseId, CancellationToken.None);
        var shortened = repository.Terminals.Single().PairingLeaseExpiresAt;
        Assert.True(shortened <= DateTime.UtcNow + LinklyCloudTerminalService.ProbeAmbiguousLeaseDuration);
        Assert.Equal(lease.LeaseId, repository.Terminals.Single().PairingAttemptId);

        // 再次缩短不会把到期时间往后延。
        await Task.Delay(20);
        await service.ShortenOperationLeaseAsync("Production", "S01", terminalId, lease.LeaseId, CancellationToken.None);
        Assert.True(repository.Terminals.Single().PairingLeaseExpiresAt <= shortened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionTestAsync_total_budget_cancels_current_phase_without_financial_request(bool statusStarted)
    {
        var id = Guid.NewGuid();
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var clock = new ManualDeadlineTimeProvider();
        var tokenProvider = new FakeTokenProvider { WaitForCancellation = !statusStarted };
        var transport = new FakeStatusTransport { WaitForCancellation = statusStarted };
        var service = CreateService(repository, tokenProvider: tokenProvider, backendTransport: transport, timeProvider: clock);

        var pending = service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);
        Assert.Equal(LinklyTimeoutConstants.BusinessWait, clock.DueTime);
        Assert.Equal(1, clock.TimerCount);
        clock.Expire();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("unknown", result.Status);
        Assert.Equal("Connection test timed out.", result.Message);
        Assert.Equal(statusStarted ? 0 : 1, repository.ReleasedConnectionLeases);
        Assert.Equal(statusStarted ? 1 : 0, transport.StatusCalls);
        Assert.Equal(0, transport.TransactionCalls);
        Assert.Equal(0, repository.HealthWriteAttempts);
        Assert.True(statusStarted ? transport.ReceivedCancellationToken.IsCancellationRequested : tokenProvider.ReceivedCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ConnectionTestAsync_expired_budget_after_token_never_sends_status()
    {
        var id = Guid.NewGuid();
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var clock = new ManualDeadlineTimeProvider();
        var transport = new FakeStatusTransport();
        var service = CreateService(repository,
            tokenProvider: new FakeTokenProvider { BeforeReturn = clock.Expire },
            backendTransport: transport, timeProvider: clock);

        var result = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), CancellationToken.None);

        Assert.Equal("unknown", result.Status);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(0, transport.TransactionCalls);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
        Assert.Equal(0, repository.HealthWriteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionTestAsync_caller_cancellation_propagates_without_becoming_timeout_result(bool statusStarted)
    {
        var id = Guid.NewGuid();
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var clock = new ManualDeadlineTimeProvider();
        var tokenProvider = new FakeTokenProvider { WaitForCancellation = !statusStarted };
        var transport = new FakeStatusTransport { WaitForCancellation = statusStarted };
        var service = CreateService(repository, tokenProvider: tokenProvider, backendTransport: transport, timeProvider: clock);
        using var caller = new CancellationTokenSource();

        var pending = service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(statusStarted ? 0 : 1, repository.ReleasedConnectionLeases);
        Assert.Equal(statusStarted ? 1 : 0, transport.StatusCalls);
        Assert.Equal(0, transport.TransactionCalls);
        Assert.Equal(0, repository.HealthWriteAttempts);
    }

    private static LinklyCloudTerminalConnectionTestRequest TestRequest(FakeTerminalRepository repository) =>
        new("Production", repository.Terminals[0].UpdatedAt!.Value.Ticks.ToString(), null, 0);

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.Accepted, "{}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    [InlineData(HttpStatusCode.OK, "{\"Response\":{\"ResponseCode\":\"T0\"}}")]
    public async Task ConnectionTestAsync_ambiguous_response_keeps_lease_without_writing_health(HttpStatusCode status, string body)
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(),
            backendTransport: new FakeStatusTransport(new LinklyCloudBackendTransportResponse(status, body)));
        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), default);
        Assert.False(response.Succeeded);
        Assert.Equal(0, repository.ReleasedConnectionLeases);
        Assert.Equal(0, repository.HealthWriteAttempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ConnectionTestAsync_explicit_credential_rejection_releases_lease(HttpStatusCode status)
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider(),
            backendTransport: new FakeStatusTransport(new LinklyCloudBackendTransportResponse(status, "{}")));
        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), default);
        Assert.Equal("needs-repair", response.Status);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
        Assert.Equal(0, repository.HealthWriteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionTestAsync_token_failure_releases_lease_before_terminal_request(bool timeout)
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new FakeTerminalRepository { Terminals = [CreateTerminal(id, "S01", "Production", 1, "Front")] };
        var failure = timeout ? (Exception)new OperationCanceledException() : new HttpRequestException("token offline");
        var service = CreateService(repository, tokenProvider: new FakeTokenProvider { Failure = failure },
            backendTransport: new FakeStatusTransport());
        var response = await service.ConnectionTestAsync("S01", "POS-01", id, TestRequest(repository), default);
        Assert.False(response.Succeeded);
        Assert.Equal(1, repository.ReleasedConnectionLeases);
        Assert.Equal(0, repository.HealthWriteAttempts);
    }

    private static LinklyCloudTerminalService CreateService(
        FakeTerminalRepository repository,
        ILinklyCloudPairingTransport? transport = null,
        ILinklyCloudBackendAsyncRepository? sessionRepository = null,
        ILinklyCloudBackendTokenProvider? tokenProvider = null,
        ILinklyCloudBackendAsyncTransport? backendTransport = null,
        TimeProvider? timeProvider = null)
    {
        return new LinklyCloudTerminalService(
            repository,
            sessionRepository ?? new InMemoryLinklyCloudBackendAsyncRepository(),
            transport ?? new FakePairingTransport(
                new LinklyCloudPairingTransportResponse(HttpStatusCode.OK, "paired-secret")),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                ProductionAuthBaseUrl = "https://auth.example/v1/",
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/"
            }),
            tokenProvider,
            backendTransport,
            timeProvider: timeProvider);
    }

    private static LinklyCloudTerminalRecord CreateTerminal(
        Guid terminalId,
        string storeCode,
        string environment,
        int laneNo,
        string displayName)
    {
        return new LinklyCloudTerminalRecord
        {
            TerminalId = terminalId,
            Environment = environment,
            StoreCode = storeCode,
            LaneNo = laneNo,
            DisplayName = displayName,
            Username = "user",
            Password = "password",
            Secret = "secret",
            CredentialProtectionVersion =
                BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection.CurrentVersion,
            PosId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            PairingState = "Ready",
            UpdatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private sealed class FakeTerminalRepository : ILinklyCloudTerminalRepository
    {
        private readonly object _gate = new();

        public List<LinklyCloudTerminalRecord> Terminals { get; set; } = [];

        public LinklyCloudDeviceSelectionRecord? Selection { get; set; }

        public List<LinklyCloudDeviceSelectionRecord> OtherSelections { get; set; } = [];

        public string Mode { get; set; } = "Legacy";

        public string? LastModeEnvironment { get; private set; }

        public string? LastModeStoreCode { get; private set; }

        public bool PairingBlocked { get; set; }

        public List<LinklyCloudAssignableDevice> AssignableDevices { get; set; } = [];

        public int ReleasedConnectionLeases { get; private set; }

        public int HealthWriteAttempts { get; private set; }

        public Task<IReadOnlyList<LinklyCloudTerminalRecord>> ListAsync(
            string environment, string storeCode, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LinklyCloudTerminalRecord>>(Terminals
                .Where(item => item.Environment == environment && item.StoreCode == storeCode)
                .ToArray());
        }

        public Task<LinklyCloudTerminalRecord?> GetAsync(
            string environment, string storeCode, Guid terminalId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Terminals.SingleOrDefault(item =>
                item.Environment == environment && item.StoreCode == storeCode && item.TerminalId == terminalId));
        }

        public Task<LinklyCloudDeviceSelectionRecord?> GetSelectionAsync(
            string environment, string storeCode, string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(Selection is not null &&
                Selection.Environment == environment && Selection.StoreCode == storeCode && Selection.DeviceCode == deviceCode
                ? Selection
                : null);
        }

        public Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid terminalId,
            long? expectedRevision,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            if (OtherSelections.Any(item =>
                    item.Environment == environment &&
                    item.StoreCode == storeCode &&
                    item.TerminalId == terminalId &&
                    item.DeviceCode != deviceCode))
            {
                throw new LinklyCloudTerminalAssignedException();
            }

            var current = Selection;
            if (current is not null && expectedRevision != current.Revision ||
                current is null && expectedRevision is not null and not 0)
            {
                throw new LinklyCloudTerminalSelectionConflictException();
            }

            Selection = new LinklyCloudDeviceSelectionRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                TerminalId = terminalId,
                Revision = (current?.Revision ?? 0) + 1,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
            return Task.FromResult(Selection);
        }

        public Task<string> GetConfigurationModeAsync(
            string environment, string storeCode, CancellationToken cancellationToken)
        {
            LastModeEnvironment = environment;
            LastModeStoreCode = storeCode;
            return Task.FromResult(Mode);
        }

        public Task<IReadOnlyList<LinklyCloudAssignableDevice>> ListAssignableDevicesAsync(
            string environment, string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LinklyCloudAssignableDevice>>(AssignableDevices);

        public Task<bool> TryAcquireConnectionTestLeaseAsync(
            string environment, string storeCode, Guid terminalId, DateTime expectedUpdatedAt,
            Guid leaseId, DateTime leaseExpiresAt, DateTime now, CancellationToken cancellationToken,
            string? expectedAssignedDeviceCode = null, long expectedAssignmentRevision = 0,
            string? operationDeviceCode = null)
        {
            var index = Terminals.FindIndex(item => item.TerminalId == terminalId && item.UpdatedAt == expectedUpdatedAt);
            if (index < 0 || Terminals[index].PairingAttemptId is not null && Terminals[index].PairingLeaseExpiresAt > now)
                return Task.FromResult(false);
            Terminals[index] = Terminals[index] with { PairingAttemptId = leaseId, PairingLeaseExpiresAt = leaseExpiresAt };
            return Task.FromResult(true);
        }

        public int ShortenedLeases { get; private set; }

        public Task ShortenOperationLeaseAsync(
            string environment, string storeCode, Guid terminalId, Guid expectedOperationLeaseId,
            DateTime newExpiresAt, CancellationToken cancellationToken)
        {
            // 与 SQL 一致：只缩短仍属于该租约、且到期时间晚于新值的租约，绝不延长。
            var index = Terminals.FindIndex(item => item.TerminalId == terminalId &&
                item.PairingAttemptId == expectedOperationLeaseId && item.PairingLeaseExpiresAt > newExpiresAt);
            if (index >= 0)
            {
                Terminals[index] = Terminals[index] with { PairingLeaseExpiresAt = newExpiresAt };
                ShortenedLeases++;
            }

            return Task.CompletedTask;
        }

        public Task ReleaseConnectionTestLeaseAsync(
            string environment, string storeCode, Guid terminalId, Guid leaseId, CancellationToken cancellationToken)
        {
            var index = Terminals.FindIndex(item => item.TerminalId == terminalId && item.PairingAttemptId == leaseId);
            if (index >= 0)
            {
                Terminals[index] = Terminals[index] with { PairingAttemptId = null, PairingLeaseExpiresAt = null };
                ReleasedConnectionLeases++;
            }
            return Task.CompletedTask;
        }

        public Task<LinklyCloudTerminalRecord?> TryBeginPairingAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid pairingAttemptId,
            DateTime pairingLeaseExpiresAt,
            DateTime expectedUpdatedAt,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (PairingBlocked)
                {
                    return Task.FromResult<LinklyCloudTerminalRecord?>(null);
                }

                var index = Terminals.FindIndex(item =>
                    item.Environment == environment &&
                    item.StoreCode == storeCode &&
                    item.TerminalId == terminalId &&
                    item.UpdatedAt == expectedUpdatedAt &&
                    (item.PairingAttemptId is null ||
                        item.PairingLeaseExpiresAt is null ||
                        item.PairingLeaseExpiresAt <= updatedAt));
                if (index < 0)
                {
                    return Task.FromResult<LinklyCloudTerminalRecord?>(null);
                }

                var marker = Terminals[index] with
                {
                    PairingState = "Unknown",
                    PairingAttemptId = pairingAttemptId,
                    PairingLeaseExpiresAt = pairingLeaseExpiresAt,
                    UpdatedAt = updatedAt,
                    UpdatedBy = updatedBy
                };
                Terminals[index] = marker;
                return Task.FromResult<LinklyCloudTerminalRecord?>(marker);
            }
        }

        public Task<LinklyCloudTerminalRecord> UpdatePairingAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid expectedPairingAttemptId,
            DateTime expectedUpdatedAt,
            string pairingState,
            string? secret,
            string? posId,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            var index = Terminals.FindIndex(item =>
                item.Environment == environment &&
                item.StoreCode == storeCode &&
                item.TerminalId == terminalId &&
                item.PairingAttemptId == expectedPairingAttemptId &&
                item.UpdatedAt == expectedUpdatedAt);
            if (index < 0)
            {
                throw new LinklyCloudTerminalPairingConflictException();
            }

            var updated = Terminals[index] with
            {
                PairingState = pairingState,
                Secret = secret,
                PosId = posId,
                PairingAttemptId = null,
                PairingLeaseExpiresAt = null,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
            Terminals[index] = updated;
            return Task.FromResult(updated);
        }

        public Task<bool> TryAcquireOperationLeaseAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid terminalId,
            long expectedSelectionRevision,
            DateTime expectedTerminalUpdatedAt,
            Guid operationLeaseId,
            DateTime operationLeaseExpiresAt,
            DateTime now,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var selectionMatches = Selection is not null &&
                    Selection.Environment == environment &&
                    Selection.StoreCode == storeCode &&
                    Selection.DeviceCode == deviceCode &&
                    Selection.TerminalId == terminalId &&
                    Selection.Revision == expectedSelectionRevision;
                var index = Terminals.FindIndex(item =>
                    item.Environment == environment &&
                    item.StoreCode == storeCode &&
                    item.TerminalId == terminalId &&
                    item.UpdatedAt == expectedTerminalUpdatedAt &&
                    item.PairingState == "Ready" &&
                    !string.IsNullOrWhiteSpace(item.Secret) &&
                    !string.IsNullOrWhiteSpace(item.PosId) &&
                    (item.PairingAttemptId is null ||
                        item.PairingLeaseExpiresAt is null ||
                        item.PairingLeaseExpiresAt <= now));
                if (PairingBlocked || !selectionMatches || Mode != "Active" || index < 0)
                {
                    return Task.FromResult(false);
                }

                Terminals[index] = Terminals[index] with
                {
                    PairingAttemptId = operationLeaseId,
                    PairingLeaseExpiresAt = operationLeaseExpiresAt
                };
                return Task.FromResult(true);
            }
        }

        public Task ReleasePairingLeaseAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid expectedPairingAttemptId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var index = Terminals.FindIndex(item =>
                    item.Environment == environment &&
                    item.StoreCode == storeCode &&
                    item.TerminalId == terminalId &&
                    item.PairingAttemptId == expectedPairingAttemptId);
                if (index >= 0)
                {
                    Terminals[index] = Terminals[index] with
                    {
                        PairingAttemptId = null,
                        PairingLeaseExpiresAt = null
                    };
                }
            }

            return Task.CompletedTask;
        }

        public Task ReleaseOperationLeaseAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid expectedOperationLeaseId,
            CancellationToken cancellationToken)
        {
            return ReleasePairingLeaseAsync(
                environment,
                storeCode,
                terminalId,
                expectedOperationLeaseId,
                cancellationToken);
        }

        public Task<bool> TryRecordHealthAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            DateTime expectedTerminalUpdatedAt,
            string healthStatus,
            DateTime checkedAt,
            CancellationToken cancellationToken)
        {
            HealthWriteAttempts++;
            lock (_gate)
            {
                var index = Terminals.FindIndex(item =>
                    item.Environment == environment &&
                    item.StoreCode == storeCode &&
                    item.TerminalId == terminalId &&
                    item.UpdatedAt == expectedTerminalUpdatedAt &&
                    (item.LastHealthAt is null || item.LastHealthAt <= checkedAt));
                if (index < 0)
                {
                    return Task.FromResult(false);
                }

                Terminals[index] = Terminals[index] with
                {
                    LastHealthStatus = healthStatus,
                    LastHealthAt = checkedAt
                };
                return Task.FromResult(true);
            }
        }
    }

    private sealed class FakePairingTransport(
        LinklyCloudPairingTransportResponse response,
        Action? beforeReturn = null)
        : ILinklyCloudPairingTransport
    {
        public string? Username { get; private set; }

        public string? Password { get; private set; }

        public int CallCount { get; private set; }

        public Task<LinklyCloudPairingTransportResponse> PairAsync(
            string authBaseUrl,
            string username,
            string password,
            string pairCode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Username = username;
            Password = password;
            beforeReturn?.Invoke();
            return Task.FromResult(response);
        }
    }

    private sealed class FakeTokenProvider : ILinklyCloudBackendTokenProvider
    {
        public Exception? Failure { get; init; }
        public Action? BeforeReturn { get; init; }
        public bool WaitForCancellation { get; init; }
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public Task<LinklyCloudBackendToken> GetTokenAsync(string environment, string storeCode, string deviceCode, CancellationToken cancellationToken) =>
            Get(cancellationToken);

        public Task<LinklyCloudBackendToken> GetTokenAsync(string environment, string storeCode, string deviceCode, Guid? terminalId, CancellationToken cancellationToken) =>
            Get(cancellationToken);

        private Task<LinklyCloudBackendToken> Get(CancellationToken cancellationToken)
        {
            ReceivedCancellationToken = cancellationToken;
            if (WaitForCancellation) return WaitUntilCanceled<LinklyCloudBackendToken>(cancellationToken);
            if (Failure is not null) return Task.FromException<LinklyCloudBackendToken>(Failure);
            BeforeReturn?.Invoke();
            return Task.FromResult(new LinklyCloudBackendToken("https://rest.example/", "token"));
        }
    }

    private sealed class FakeStatusTransport(LinklyCloudBackendTransportResponse? response = null) : ILinklyCloudBackendAsyncTransport
    {
        public Exception? Failure { get; init; }
        public bool WaitForCancellation { get; init; }
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public int StatusCalls { get; private set; }
        public LinklyCloudBackendTransportStatusRequest? LastStatusRequest { get; private set; }
        public int TransactionCalls { get; private set; }
        public Task<LinklyCloudBackendTransportResponse> SendStatusAsync(LinklyCloudBackendTransportStatusRequest request, CancellationToken cancellationToken)
        {
            StatusCalls++;
            LastStatusRequest = request;
            ReceivedCancellationToken = cancellationToken;
            if (WaitForCancellation) return WaitUntilCanceled<LinklyCloudBackendTransportResponse>(cancellationToken);
            if (Failure is not null) return Task.FromException<LinklyCloudBackendTransportResponse>(Failure);
            return Task.FromResult(response ?? new LinklyCloudBackendTransportResponse(HttpStatusCode.OK, null));
        }
        public Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(LinklyCloudBackendTransportTransactionRequest request, CancellationToken cancellationToken)
        { TransactionCalls++; throw new InvalidOperationException("financial transport must not be called"); }
        public Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(LinklyCloudBackendTransportSessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LinklyCloudBackendTransportResponse> GetTransactionAsync(LinklyCloudBackendTransportSessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LinklyCloudBackendTransportResponse> SendLogonAsync(LinklyCloudBackendTransportSessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LinklyCloudBackendTransportResponse> SendKeyAsync(LinklyCloudBackendTransportSendKeyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<T> WaitUntilCanceled<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation must interrupt the pending request.");
    }

    // 直接触发业务截止时间，避免回归测试真实等待 180 秒。
    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        private TimerCallback? callback;
        private object? callbackState;
        public TimeSpan? DueTime { get; private set; }
        public int TimerCount { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            callbackState = state;
            DueTime = dueTime;
            TimerCount++;
            return new ManualTimer();
        }
        public void Expire() => callback!(callbackState);

        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
