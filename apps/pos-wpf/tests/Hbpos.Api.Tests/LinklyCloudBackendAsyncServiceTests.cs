using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

    public sealed class LinklyCloudBackendAsyncServiceTests
    {
    [Fact]
    public void Sql_repository_create_session_uses_serializable_lock_hints()
    {
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Revision] = @SelectionRevision", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[UpdatedAt] = @TerminalUpdatedAt", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[TerminalId] = @TerminalId", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingState] = N'Ready'", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[CredentialProtectionVersion] = 1", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingAttemptId] IS NULL", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PairingLeaseExpiresAt] <= @UpdatedAt", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LTRIM(RTRIM([Secret]))", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LTRIM(RTRIM([PosId]))", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        var sql = SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql;
        var sessionIndex = sql.IndexOf("POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase);
        var terminalIndex = sql.IndexOf("POSM_LinklyCloudTerminal", StringComparison.OrdinalIgnoreCase);
        var selectionIndex = sql.IndexOf("POSM_LinklyCloudDeviceSelection", StringComparison.OrdinalIgnoreCase);
        var modeIndex = sql.IndexOf("POSM_LinklyCloudConfigurationMode", StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionIndex >= 0 && terminalIndex > sessionIndex && selectionIndex > terminalIndex && modeIndex > selectionIndex);
        Assert.Contains("THEN target.[TxnRef]", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COALESCE(target.[RequestRfn], @RequestRfn)", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repository_stale_write_preserves_persisted_request_evidence_and_txn_ref()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var stored = new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "request-evidence-race",
            Status = "Pending",
            TxnRef = "260101010101ABCD",
            RequestTxnType = "P",
            RequestAmountCents = 99,
            RequestRfn = "CUSTOM-RFN-99",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await repository.UpsertSessionAsync(stored, CancellationToken.None);
        var stale = new LinklyCloudBackendSessionRecord
        {
            Environment = stored.Environment,
            StoreCode = stored.StoreCode,
            DeviceCode = stored.DeviceCode,
            SessionId = stored.SessionId,
            Status = "Completed",
            TxnRef = "****ABCD",
            TransactionSuccess = true,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await repository.UpsertSessionAsync(stale, CancellationToken.None);
        var persisted = await repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", stored.SessionId, CancellationToken.None);

        Assert.Equal(stored.TxnRef, persisted?.TxnRef);
        Assert.Equal("P", persisted?.RequestTxnType);
        Assert.Equal(99, persisted?.RequestAmountCents);
        Assert.Equal("CUSTOM-RFN-99", persisted?.RequestRfn);
    }

    [Fact]
    public void Legacy_pairing_and_credential_writes_are_fenced_by_the_configuration_mode_row()
    {
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO [dbo].[POSM_LinklyCloudConfigurationMode]", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51005", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51007", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LegacyPairingAttemptId]", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LegacyPairingLeaseExpiresAt]", SqlSugarLinklyCloudBackendTerminalCredentialRepository.AcquireLegacyPairingLeaseSql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LegacyPairingAttemptId] = @AttemptId", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@ExistingLeaseExpiresAt <= @Now", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51005", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51006", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LegacyPairingAttemptId] = NULL", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT TOP 1", SqlSugarLinklyCloudBackendTerminalCredentialRepository.CompleteLegacyPairingSql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("POSM_LinklyCloudConfigurationMode", SqlSugarLinklyCloudBackendTerminalCredentialRepository.UpsertSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", SqlSugarLinklyCloudBackendTerminalCredentialRepository.UpsertSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51005", SqlSugarLinklyCloudBackendTerminalCredentialRepository.UpsertSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("logon")]
    public async Task Active_terminal_test_is_blocked_by_physical_terminal_session_before_upstream_call(string operation)
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-A",
            TerminalId = terminalId,
            SessionId = "active-on-terminal",
            Status = "Pending",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.OK, "{}");
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            repository);
        var service = CreateService(
            transport,
            repository: repository,
            terminalService: terminalService);

        if (operation == "status")
        {
            await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
                service.RunStatusTestAsync("S01", "POS-B", "Sandbox", terminalId, 9, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
                service.RunLogonTestAsync("S01", "POS-B", "Sandbox", terminalId, 9, CancellationToken.None));
        }

        Assert.Null(transport.LastStatus);
        Assert.Null(transport.LastLogon);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task Active_status_test_releases_only_unambiguous_http_response_leases(
        HttpStatusCode statusCode,
        bool leaseMustRemain)
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            leaseState: leaseState);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(statusCode, "{}");
        var service = CreateService(transport, terminalService: terminalService);

        await service.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.Equal(leaseMustRemain, leaseState.LeaseId is not null);
        Assert.Equal(terminalId, transport.LastStatus?.TerminalId);
        Assert.Equal(leaseMustRemain ? 0 : 1, terminalService.HealthWriteAttempts.Count);
    }

    [Theory]
    [InlineData("T0", "APPROVED", "Healthy")]
    [InlineData("05", "DECLINED", "Unhealthy")]
    public async Task Active_status_test_records_the_last_definitive_physical_terminal_health(
        string responseCode,
        string responseText,
        string expectedHealth)
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                $$"""
                {
                  "Response": {
                    "Success": {{(expectedHealth == "Healthy").ToString().ToLowerInvariant()}},
                    "ResponseCode": "{{responseCode}}",
                    "ResponseText": "{{responseText}}"
                  }
                }
                """),
            terminalService: terminalService);

        await service.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        var health = Assert.Single(terminalService.HealthWriteAttempts);
        Assert.Equal(expectedHealth, health.Status);
        Assert.Equal(terminalId, health.TerminalId);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), health.ExpectedUpdatedAt);
    }

    [Fact]
    public async Task Active_status_test_ignores_unknown_terminal_outcomes_for_last_health()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                terminalTestException: new HttpRequestException("network")),
            terminalService: terminalService);

        var response = await service.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.RequestTimeout, response.HttpStatus);
        Assert.Empty(terminalService.HealthWriteAttempts);
    }

    [Fact]
    public async Task Active_status_test_health_cas_conflict_does_not_change_completed_test_result()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9)
        {
            RecordHealthResult = false
        };
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                """{"Response":{"Success":true,"ResponseCode":"T0","ResponseText":"APPROVED"}}"""),
            terminalService: terminalService);

        var response = await service.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Single(terminalService.HealthWriteAttempts);
    }

    [Fact]
    public async Task Active_logon_test_binds_transport_gate_to_physical_terminal_and_releases_success_lease()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            leaseState: leaseState);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.OK,
            """{"Response":{"Success":true,"ResponseCode":"00","ResponseText":"APPROVED"}}""");
        var service = CreateService(transport, terminalService: terminalService);

        await service.RunLogonTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.Equal(terminalId, transport.LastLogon?.TerminalId);
        Assert.Null(leaseState.LeaseId);
        Assert.Equal("Healthy", Assert.Single(terminalService.HealthWriteAttempts).Status);
    }

    [Fact]
    public async Task Active_logon_test_records_unhealthy_for_a_definitive_failure()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                """{"Response":{"ResponseCode":"01","ResponseText":"DECLINED"}}"""),
            terminalService: terminalService);

        var response = await service.RunLogonTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("Unhealthy", Assert.Single(terminalService.HealthWriteAttempts).Status);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("cancel")]
    public async Task Active_status_test_preserves_lease_after_ambiguous_transport_exception(string failure)
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            leaseState: leaseState);
        Exception exception = failure == "network"
            ? new HttpRequestException("network")
            : new TaskCanceledException("timeout");
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.OK, terminalTestException: exception),
            terminalService: terminalService);

        var response = await service.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.RequestTimeout, response.HttpStatus);
        Assert.NotNull(leaseState.LeaseId);
    }

    [Fact]
    public async Task Active_status_test_releases_lease_when_token_fails_before_terminal_request()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            leaseState: leaseState);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.OK, "{}");
        var service = CreateService(
            transport,
            new ThrowingLinklyCloudBackendTokenProvider("token failed"),
            terminalService: terminalService);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.RunStatusTestAsync(
                "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None));

        Assert.Null(leaseState.LeaseId);
        Assert.Null(transport.LastStatus);
    }

    [Fact]
    public async Task Ambiguous_status_lease_blocks_second_service_instance_until_expiry()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var firstTerminalService = new FixedLinklyCloudTerminalService(
            terminalId, "Front Counter", 9, leaseState: leaseState);
        var secondTerminalService = new FixedLinklyCloudTerminalService(
            terminalId, "Front Counter", 9, leaseState: leaseState);
        var firstService = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                terminalTestException: new HttpRequestException("network")),
            terminalService: firstTerminalService);
        var secondTransport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.OK, "{}");
        var secondService = CreateService(secondTransport, terminalService: secondTerminalService);

        await firstService.RunStatusTestAsync(
            "S01", "POS-01", "Sandbox", terminalId, 9, CancellationToken.None);
        await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            secondService.RunStatusTestAsync(
                "S01", "POS-02", "Sandbox", terminalId, 9, CancellationToken.None));
        Assert.Null(secondTransport.LastStatus);

        leaseState.ExpiresAt = DateTime.UtcNow.AddTicks(-1);
        await secondService.RunStatusTestAsync(
            "S01", "POS-02", "Sandbox", terminalId, 9, CancellationToken.None);

        Assert.NotNull(secondTransport.LastStatus);
        Assert.Null(leaseState.LeaseId);
    }

    [Fact]
    public async Task Active_terminal_operation_lease_blocks_transaction_transport_creation()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var leaseState = new TerminalOperationLeaseState();
        var repository = new TerminalLeaseAwareBackendRepository(leaseState);
        var terminalService = new FixedLinklyCloudTerminalService(
            terminalId,
            "Front Counter",
            9,
            repository,
            leaseState);
        var context = await terminalService.ResolvePaymentTerminalAsync(
            "Sandbox", "S01", "POS-01", terminalId, 9, CancellationToken.None);
        Assert.NotNull(context);
        _ = await terminalService.AcquireOperationLeaseAsync(
            "Sandbox", "S01", "POS-01", context!, CancellationToken.None);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(
            transport,
            repository: repository,
            terminalService: terminalService);

        await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                new LinklyCloudBackendTransactionRequest("Sandbox", "P", 1000, null, terminalId, 9),
                CancellationToken.None));

        Assert.Null(transport.LastTransaction);
    }

    [Fact]
    public async Task StartTransactionAsync_active_mode_binds_session_and_token_to_selected_terminal()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            tokenProvider,
            repository: repository,
            terminalService: terminalService);

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox", "P", 1000, null, terminalId, 9),
            CancellationToken.None);

        Assert.Equal(terminalId, response.TerminalId);
        Assert.Equal("Front Counter", response.TerminalDisplayName);
        Assert.Contains(terminalId, tokenProvider.TerminalCalls);
        var active = await repository.GetActiveSessionByTerminalAsync(
            "Sandbox", "S01", terminalId, CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal("POS-01", active!.DeviceCode);
    }

    [Fact]
    public async Task StartSettlementAsync_active_mode_binds_session_and_token_to_selected_terminal()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(
            transport,
            tokenProvider,
            repository: repository,
            terminalService: new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9));

        var response = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox", terminalId, 9),
            CancellationToken.None);

        Assert.Equal("Settlement", response.OperationType);
        Assert.Equal(terminalId, response.TerminalId);
        Assert.Equal("Front Counter", response.TerminalDisplayName);
        Assert.Contains(terminalId, tokenProvider.TerminalCalls);
        Assert.Equal(terminalId, transport.LastSettlement?.TerminalId);
    }

    [Fact]
    public async Task Active_mode_transaction_path_binds_every_transport_request_to_the_physical_terminal()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(
            transport,
            tokenProvider,
            terminalService: new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9));

        // Token 已按实体终端取；同一会话的每个 REST 请求也必须带上实体终端，才能与 Token 落进同一个闸门。
        var started = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest("Sandbox", "P", 1000, null, terminalId, 9),
            CancellationToken.None);
        await service.GetStatusAsync("S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);
        await service.RecoverAsync(
            "S01", "POS-01", started.SessionId, new LinklyCloudBackendRecoverRequest("Sandbox"), CancellationToken.None);
        await service.SendKeyAsync(
            "S01", "POS-01", started.SessionId, new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null), CancellationToken.None);

        Assert.All(tokenProvider.TerminalCalls, call => Assert.Equal(terminalId, call));
        Assert.Equal(terminalId, transport.LastTransaction?.TerminalId);
        Assert.Equal(terminalId, transport.LastGetTransaction?.TerminalId);
        Assert.Equal(terminalId, transport.LastRecover?.TerminalId);
        Assert.Equal(terminalId, transport.LastSendKey?.TerminalId);
    }

    [Fact]
    public async Task GetActiveSessionAsync_uses_non_sensitive_terminal_metadata()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            TerminalId = terminalId,
            SessionId = "active-terminal-session",
            Status = "Pending",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository,
            terminalService: new FixedLinklyCloudTerminalService(
                terminalId,
                "Front Counter",
                9,
                failCredentialMaterialization: true));

        var response = await service.GetActiveSessionAsync(
            "S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(terminalId, response!.TerminalId);
        Assert.Equal("Front Counter", response.TerminalDisplayName);
    }

    [Fact]
    public async Task AcknowledgeSessionAsync_returns_after_persisting_when_credentials_cannot_materialize()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            TerminalId = terminalId,
            SessionId = "completed-terminal-session",
            Status = "Completed",
            TransactionSuccess = true,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository,
            terminalService: new FixedLinklyCloudTerminalService(
                terminalId,
                "Front Counter",
                9,
                failCredentialMaterialization: true));

        var response = await service.AcknowledgeSessionAsync(
            "S01", "POS-01", "Sandbox", "completed-terminal-session", supervisorResolved: false, CancellationToken.None);
        var persisted = await repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", "completed-terminal-session", CancellationToken.None);

        Assert.NotNull(response.ClientAcknowledgedAt);
        Assert.NotNull(persisted?.ClientAcknowledgedAt);
        Assert.Equal("Front Counter", response.TerminalDisplayName);
    }

    [Fact]
    public async Task MarkReceiptPrintedAsync_returns_after_persisting_when_credentials_cannot_materialize()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            TerminalId = terminalId,
            SessionId = "receipt-terminal-session",
            Status = "Completed",
            ReceiptText = "MERCHANT RECEIPT",
            TransactionSuccess = true,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository,
            terminalService: new FixedLinklyCloudTerminalService(
                terminalId,
                "Front Counter",
                9,
                failCredentialMaterialization: true));

        var response = await service.MarkReceiptPrintedAsync(
            "S01",
            "POS-01",
            "receipt-terminal-session",
            new LinklyCloudBackendMarkReceiptPrintedRequest("Sandbox"),
            CancellationToken.None);
        var persisted = await repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", "receipt-terminal-session", CancellationToken.None);

        Assert.NotNull(response.ReceiptPrintedAt);
        Assert.NotNull(persisted?.ReceiptPrintedAt);
        Assert.Equal("Front Counter", response.TerminalDisplayName);
    }

    [Fact]
    public async Task StartTransactionAsync_blocks_second_pos_using_same_selected_terminal()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var terminalService = new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository,
            terminalService: terminalService);
        var request = new LinklyCloudBackendTransactionRequest(
            "Sandbox", "P", 1000, null, terminalId, 9);

        await service.StartTransactionAsync("S01", "POS-01", request, CancellationToken.None);

        await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.StartTransactionAsync("S01", "POS-02", request, CancellationToken.None));
    }

    [Fact]
    public async Task RecoverAsync_uses_terminal_bound_to_existing_session()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(
            transport,
            tokenProvider,
            repository: repository,
            terminalService: new FixedLinklyCloudTerminalService(terminalId, "Front Counter", 9));
        var started = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox", "P", 1000, null, terminalId, 9),
            CancellationToken.None);

        var recovered = await service.RecoverAsync(
            "S01",
            "POS-01",
            started.SessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal(terminalId, recovered.TerminalId);
        Assert.Equal([terminalId, terminalId], tokenProvider.TerminalCalls);
        Assert.Equal(started.SessionId, transport.LastRecover?.SessionId);
    }

    [Fact]
    public async Task StartSettlementAsync_creates_settlement_session_and_uses_shared_active_terminal_lock()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        var response = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal("Settlement", response.OperationType);
        Assert.Equal("Pending", response.Status);
        Assert.Null(response.TxnRef);
        Assert.Equal(response.SessionId, transport.LastSettlement?.SessionId);
        Assert.NotNull(transport.LastSettlement?.Notification);
        await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.StartTransactionAsync("S01", "POS-01", CreateTransactionRequest(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StartSettlementAsync_closes_not_submitted_session_when_authorization_is_rejected(
        HttpStatusCode statusCode)
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(statusCode),
            repository: repository);

        var settlement = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal("NotSubmitted", settlement.Status);
        Assert.False(settlement.OperationSuccess);
        Assert.Null(settlement.RecoveryAction);
        Assert.Equal((int)statusCode, settlement.LastHttpStatus);
        Assert.Contains(((int)statusCode).ToString(), settlement.ResponseText, StringComparison.Ordinal);
        Assert.Null(await repository.GetActiveSessionAsync(
            "Sandbox", "S01", "POS-01", CancellationToken.None));

        var transaction = await service.StartTransactionAsync(
            "S01", "POS-01", CreateTransactionRequest(), CancellationToken.None);
        Assert.Equal("TokenRefreshRequired", transaction.Status);
        Assert.Equal("RefreshToken", transaction.RecoveryAction);
    }

    [Fact]
    public async Task Settlement_notification_and_receipt_are_persisted_in_any_order_without_transaction_refresh()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        var started = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);

        await service.ReceiveNotificationAsync(
            "Sandbox",
            started.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            JsonDocument.Parse("""{ "Type": "S", "ReceiptText": ["SETTLEMENT", "TOTALS"] }""").RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            started.SessionId,
            "settlement",
            "Bearer sandbox-notify",
            JsonDocument.Parse("""{ "Response": { "Success": true, "ResponseCode": "00", "ResponseText": "SETTLED", "SettlementData": "{\"Batch\":\"1\"}" } }""").RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            started.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            JsonDocument.Parse("""{ "Type": "S", "ReceiptText": ["SETTLEMENT", "TOTALS"] }""").RootElement,
            CancellationToken.None);

        var response = await service.GetSettlementStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.True(response.OperationSuccess);
        Assert.Equal("00", response.ResponseCode);
        Assert.Equal("{\"Batch\":\"1\"}", response.SettlementData);
        Assert.Equal(
            [
                "SETTLEMENT" + Environment.NewLine + "TOTALS",
                "SETTLEMENT" + Environment.NewLine + "TOTALS"
            ],
            response.SettlementReceiptTexts);
        Assert.Null(transport.LastGetTransaction);
        Assert.Equal(3, response.Notifications.Count);
    }

    [Fact]
    public async Task Settlement_callback_never_returns_raw_card_or_track_data()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);
        using var completed = JsonDocument.Parse(
            """
            {
              "Response": {
                "Success": true,
                "ResponseCode": "00",
                "ResponseText": "CARD 4111111111111111",
                "SettlementData": "{\"CardNumber\":\"4111111111111111\",\"Track2\":\"4111111111111111=2512\",\"TxnRef\":\"123456789012\"}"
              }
            }
            """);

        await service.ReceiveNotificationAsync(
            "Sandbox",
            started.SessionId,
            "settlement",
            "Bearer sandbox-notify",
            completed.RootElement,
            CancellationToken.None);

        var response = await service.GetSettlementStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("CARD ****1111", response!.ResponseText);
        Assert.DoesNotContain("4111111111111111", response.Notifications.Single().PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Track2", response.Notifications.Single().PayloadJson, StringComparison.Ordinal);
        using var settlementData = JsonDocument.Parse(response.SettlementData!);
        Assert.Equal("****1111", settlementData.RootElement.GetProperty("CardNumber").GetString());
        Assert.False(settlementData.RootElement.TryGetProperty("Track2", out _));
        Assert.Equal("123456789012", settlementData.RootElement.GetProperty("TxnRef").GetString());
    }

    [Fact]
    public async Task Settlement_concurrent_receipts_and_final_preserve_all_receipts_and_final_status()
    {
        var repository = new CoordinatedNotificationRepository(expectedNotificationReads: 3);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository);
        var started = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);
        using var firstReceipt = JsonDocument.Parse("""{ "Type": "S", "ReceiptText": ["FIRST RECEIPT", "TOTAL 1"] }""");
        using var secondReceipt = JsonDocument.Parse("""{ "Type": "S", "ReceiptText": ["SECOND RECEIPT", "TOTAL 2"] }""");
        using var final = JsonDocument.Parse("""{ "Response": { "Success": true, "ResponseCode": "00", "ResponseText": "SETTLED", "SettlementData": "{\"Batch\":\"2\"}" } }""");

        await Task.WhenAll(
            service.ReceiveNotificationAsync(
                "Sandbox", started.SessionId, "receipt", "Bearer sandbox-notify", firstReceipt.RootElement, CancellationToken.None),
            service.ReceiveNotificationAsync(
                "Sandbox", started.SessionId, "receipt", "Bearer sandbox-notify", secondReceipt.RootElement, CancellationToken.None),
            service.ReceiveNotificationAsync(
                "Sandbox", started.SessionId, "settlement", "Bearer sandbox-notify", final.RootElement, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var response = await service.GetSettlementStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.True(response.OperationSuccess);
        Assert.Equal("00", response.ResponseCode);
        Assert.Equal("{\"Batch\":\"2\"}", response.SettlementData);
        Assert.Equal(2, response.SettlementReceiptTexts?.Count);
        Assert.Contains("FIRST RECEIPT" + Environment.NewLine + "TOTAL 1", response.SettlementReceiptTexts!);
        Assert.Contains("SECOND RECEIPT" + Environment.NewLine + "TOTAL 2", response.SettlementReceiptTexts!);
        Assert.Equal(3, response.Notifications.Count);
    }

    [Fact]
    public async Task Http_transport_posts_async_settlement_with_required_fields()
    {
        var handler = new LinklyTransportHttpMessageHandler("{ }");
        using var client = new HttpClient(handler);
        var transport = new HttpLinklyCloudBackendAsyncTransport(client);

        await transport.StartSettlementAsync(
            new LinklyCloudBackendTransportSettlementRequest(
                "Sandbox",
                "https://rest.example/v1/",
                "access-token",
                "session-1",
                new LinklyCloudBackendNotificationRequest("https://public.example/notification", "Bearer callback-token"),
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.Equal("/v1/sessions/session-1/settlement", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("async=true", handler.LastRequest?.RequestUri?.Query.TrimStart('?'));
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var request = body.RootElement.GetProperty("Request");
        Assert.Equal("00", request.GetProperty("Merchant").GetString());
        Assert.Equal("00", request.GetProperty("Application").GetString());
        Assert.Equal("S", request.GetProperty("SettlementType").GetString());
        Assert.Equal("0", request.GetProperty("ReceiptAutoPrint").GetString());
        Assert.Equal("0", request.GetProperty("CutReceipt").GetString());
        Assert.Equal("https://public.example/notification", body.RootElement.GetProperty("Notification").GetProperty("Uri").GetString());
    }

    [Fact]
    public async Task Http_transport_returns_settlement_authorization_failure_without_retry()
    {
        var handler = new LinklyTransportHttpMessageHandler("{ }", HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        var transport = new HttpLinklyCloudBackendAsyncTransport(client);

        var response = await transport.StartSettlementAsync(
            new LinklyCloudBackendTransportSettlementRequest(
                "Sandbox",
                "https://rest.example/v1/",
                "access-token",
                "session-1",
                new LinklyCloudBackendNotificationRequest("https://public.example/notification", "Bearer callback-token"),
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("/v1/sessions/session-1/settlement", handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Resumable_queries_are_isolated_by_operation_type()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted), repository: repository);
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "transaction-session",
            Status = "Completed",
            OperationType = "Transaction",
            TransactionSuccess = true,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "settlement-session",
            Status = "Completed",
            OperationType = "Settlement",
            OperationSuccess = true,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1)
        }, CancellationToken.None);

        var transaction = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        var settlement = await service.GetResumableSettlementSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Equal("transaction-session", transaction?.SessionId);
        Assert.Equal("settlement-session", settlement?.SessionId);
    }

    [Fact]
    public async Task StartTransactionAsync_limits_txn_ref_and_locks_active_terminal_session()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        var request = CreateTransactionRequest();

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            request,
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.NotNull(response.TxnRef);
        Assert.True(response.TxnRef!.Length <= 16);
        Assert.Equal(response.TxnRef, transport.LastTransaction?.TxnRef);
        Assert.Equal("https://server-rest.example/POS-01/", transport.LastTransaction?.RestBaseUrl);
        Assert.Equal("server-token-POS-01", transport.LastTransaction?.AccessToken);
        Assert.Equal([("Sandbox", "S01", "POS-01")], tokenProvider.Calls);
        Assert.NotNull(transport.LastNotification);
        Assert.Equal("Bearer sandbox-notify", transport.LastNotification!.AuthorizationHeader);
        Assert.Equal(
            $"https://public.example/callback/api/v1/linkly/cloud-notifications/Sandbox/{response.SessionId}/{{{{type}}}}",
            transport.LastNotification.Uri);
        Assert.Equal(202, response.LastHttpStatus);

        var activeException = await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                request,
                CancellationToken.None));
        Assert.Equal(response.SessionId, activeException.ActiveSessionId);

        var secondTerminal = await service.StartTransactionAsync(
            "S01",
            "POS-02",
            request,
            CancellationToken.None);
        Assert.True(secondTerminal.TxnRef?.Length <= 16);
        Assert.Equal("https://server-rest.example/POS-02/", transport.LastTransaction?.RestBaseUrl);
        Assert.Equal("server-token-POS-02", transport.LastTransaction?.AccessToken);
        Assert.Equal(
            [("Sandbox", "S01", "POS-01"), ("Sandbox", "S01", "POS-02")],
            tokenProvider.Calls);
    }

    [Fact]
    public async Task StartTransactionAsync_retries_when_txn_ref_collides_without_active_session()
    {
        var repository = new TxnRefCollisionOnceRepository();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, new CapturingLinklyCloudBackendTokenProvider(), repository: repository);

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal(2, repository.TryCreateCallCount);
        Assert.NotNull(transport.LastTransaction);
    }

    [Fact]
    public async Task StartTransactionAsync_adds_purchase_rfn_from_backend_txn_ref()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        var purchaseAnalysisData = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            transport.LastTransaction?.PurchaseAnalysisData);
        Assert.Equal(response.TxnRef, purchaseAnalysisData["RFN"]);
        Assert.Equal("000001000", purchaseAnalysisData["AMT"]);
        Assert.Equal("0000", purchaseAnalysisData["PCM"]);
    }

    [Fact]
    public async Task StartTransactionAsync_derives_txn_ref_from_pos_attempt_so_pos_can_recover_before_session_binding()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        var attemptGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // POS 在发请求前已用同一算法把该引用落到本地 attempt；API 必须派生出完全相同的值，
        // 断电后本地记录才能按 TxnRef 找回这笔会话。字面量与 POS 端的已知向量测试一致，钉住两端算法不漂移。
        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest() with { AttemptGuid = attemptGuid },
            CancellationToken.None);

        Assert.Equal("PMND6WYVF2GBVY6G", response.TxnRef);
        Assert.Equal(LinklyAttemptTxnRef.Create('P', attemptGuid), response.TxnRef);
        Assert.Equal("PMND6WYVF2GBVY6G", transport.LastTransaction?.TxnRef);
        var purchaseAnalysisData = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            transport.LastTransaction?.PurchaseAnalysisData);
        Assert.Equal("PMND6WYVF2GBVY6G", purchaseAnalysisData["RFN"]);
        var resumable = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        Assert.Equal("PMND6WYVF2GBVY6G", resumable?.TxnRef);
    }

    [Fact]
    public async Task StartTransactionAsync_derives_refund_txn_ref_with_refund_prefix_and_keeps_original_rfn()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox",
                "R",
                1000,
                new Dictionary<string, string> { ["RFN"] = "PMND6WYVF2GBVY6G" },
                AttemptGuid: Guid.Parse("00000000-0000-0000-0000-000000000001")),
            CancellationToken.None);

        // 退款引用带 R 前缀，与同一 attempt 身份的销售引用不会相同；PAD 里的 RFN 仍指向原交易，不能被派生值覆盖。
        Assert.Equal("RB7VFXNSJNPBZ6QX", response.TxnRef);
        Assert.Equal("RB7VFXNSJNPBZ6QX", transport.LastTransaction?.TxnRef);
        var purchaseAnalysisData = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            transport.LastTransaction?.PurchaseAnalysisData);
        Assert.Equal("PMND6WYVF2GBVY6G", purchaseAnalysisData["RFN"]);
    }

    [Theory]
    [InlineData("P", "00000000-0000-0000-0000-000000000000")]
    [InlineData("X", "00000000-0000-0000-0000-000000000001")]
    public async Task StartTransactionAsync_rejects_unusable_attempt_identity_before_creating_session(
        string txnType,
        string attemptGuid)
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest() with { TxnType = txnType, AttemptGuid = Guid.Parse(attemptGuid) },
                CancellationToken.None));

        Assert.Null(transport.LastTransaction);
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task StartTransactionAsync_never_replaces_attempt_txn_ref_when_the_attempt_was_already_submitted()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        var request = CreateTransactionRequest() with { AttemptGuid = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var first = await service.StartTransactionAsync("S01", "POS-01", request, CancellationToken.None);
        await service.AcknowledgeSessionAsync("S01", "POS-01", "Sandbox", first.SessionId, supervisorResolved: false, CancellationToken.None);

        // API 随机生成的引用碰撞时可以换号重试；由 attempt 派生的引用一旦换号，POS 本地记录就再也对不上这笔会话，
        // 而且同一个 attempt 再次提交本身就意味着可能重复扣款。
        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync("S01", "POS-01", request, CancellationToken.None));

        Assert.Equal(first.SessionId, transport.LastTransaction?.SessionId);
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("08")]
    [InlineData("11")]
    public async Task Completed_notification_uses_persisted_request_evidence_for_card_transaction(
        string responseCode)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox",
                "P",
                99,
                new Dictionary<string, string> { ["RFN"] = "CUSTOM-RFN-99" }),
            CancellationToken.None);
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = started.TxnRef,
                TxnType = "P",
                AmtPurchase = 99,
                ResponseCode = responseCode,
                ResponseText = "APPROVED",
                PurchaseAnalysisData = new { }
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox",
            started.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            notification.RootElement,
            CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.NotNull(recovered?.CardTransaction);
        Assert.Equal(started.TxnRef, recovered!.CardTransaction!.TxnRef);
        Assert.Equal("CUSTOM-RFN-99", recovered.CardTransaction.Rfn);
        Assert.Equal(99, recovered.CardTransaction.AmountCents);
    }

    [Fact]
    public async Task Refund_persists_custom_rfn_before_sending_and_never_replaces_it_with_new_txn_ref()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, repository: repository);

        var started = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox",
                "R",
                250,
                new Dictionary<string, string> { ["rfn"] = "ORIGINAL-RFN-250" }),
            CancellationToken.None);

        var persisted = await repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", started.SessionId, CancellationToken.None);
        Assert.Equal("R", persisted?.RequestTxnType);
        Assert.Equal(250, persisted?.RequestAmountCents);
        Assert.Equal("ORIGINAL-RFN-250", persisted?.RequestRfn);
        Assert.Equal("ORIGINAL-RFN-250", transport.LastTransaction?.PurchaseAnalysisData?["rfn"]);
        Assert.Equal(persisted?.RequestRfn, transport.LastTransaction?.PurchaseAnalysisData?["rfn"]);
        Assert.NotEqual(started.TxnRef, persisted?.RequestRfn);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("txn-type")]
    [InlineData("txn-ref")]
    public async Task Completed_notification_rejects_mismatched_request_evidence(string mismatch)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateTransactionRequest(), CancellationToken.None);
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = mismatch == "txn-ref" ? "260101010101FFFF" : started.TxnRef,
                TxnType = mismatch == "txn-type" ? "R" : "P",
                AmtPurchase = mismatch == "amount" ? 1001 : 1000,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
        Assert.DoesNotContain(recovered?.Notifications ?? [], notification =>
            string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("RFN WITH SPACE", 99L)]
    [InlineData("RFN-OVER-LIMIT", 1_000_000_000L)]
    public async Task Completed_notification_rejects_request_evidence_outside_public_card_contract(
        string rfn,
        long amount)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox",
                "P",
                amount,
                new Dictionary<string, string> { ["RFN"] = rfn }),
            CancellationToken.None);
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = started.TxnRef,
                TxnType = "P",
                AmtPurchase = amount,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
    }

    [Fact]
    public async Task Completed_notification_requires_explicit_approval_response_code()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateTransactionRequest(), CancellationToken.None);
        var payload = new Dictionary<string, object?>
        {
            ["Success"] = true,
            ["TxnRef"] = started.TxnRef,
            ["TxnType"] = "P",
            ["AmtPurchase"] = 1000,
            ["ResponseText"] = "APPROVED"
        };
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new { Response = payload }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
    }

    [Fact]
    public async Task Persisted_evidence_allows_later_notification_text_when_session_text_is_absent()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "nullable-response-text",
            Status = "Completed",
            TxnRef = "2609101317495BC4",
            RequestTxnType = "P",
            RequestAmountCents = 99,
            RequestRfn = "RFN-99",
            TransactionSuccess = true,
            ResponseCode = "00",
            ResponseText = null,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "nullable-response-text",
            Type = "transaction",
            PayloadJson = """{"Response":{"Success":true,"TxnRef":"2609101317495BC4","TxnType":"P","AmtPurchase":-99,"ResponseCode":"00","ResponseText":"APPROVED"}}""",
            ReceivedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository);

        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", "nullable-response-text", CancellationToken.None);

        Assert.Equal("2609101317495BC4", recovered?.CardTransaction?.TxnRef);
        Assert.Null(recovered?.CardTransaction?.ResponseText);
    }

    [Fact]
    public async Task Legacy_completed_session_without_request_evidence_remains_without_card_transaction()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "legacy-completed",
            Status = "Completed",
            TxnRef = "260101010101ABCD",
            TransactionSuccess = true,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "legacy-completed",
            Type = "transaction",
            PayloadJson = """{"Response":{"Success":true,"TxnRef":"****ABCD","TxnType":"P","AmtPurchase":99,"ResponseCode":"00","ResponseText":"APPROVED"}}""",
            ReceivedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository);

        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", "legacy-completed", CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
    }

    [Theory]
    [InlineData("2601010101011234")]
    [InlineData("2609101317495BC4")]
    public async Task Legacy_completed_session_keeps_exact_complete_official_evidence_compatible(string txnRef)
    {
        var recovered = await GetLegacyCompletedSessionAsync(
            txnRef,
            JsonSerializer.Serialize(new
            {
                Response = new
                {
                    Success = true,
                    TxnRef = txnRef,
                    TxnType = "P",
                    AmtPurchase = 99,
                    ResponseCode = "00",
                    ResponseText = "APPROVED",
                    PurchaseAnalysisData = new { RFN = "RFN-LEGACY-99" }
                }
            }));

        Assert.Equal(txnRef, recovered?.CardTransaction?.TxnRef);
        Assert.Equal("RFN-LEGACY-99", recovered?.CardTransaction?.Rfn);
        Assert.Equal(99, recovered?.CardTransaction?.AmountCents);
    }

    [Fact]
    public async Task Legacy_completed_session_rejects_masked_txn_ref_even_with_other_complete_fields()
    {
        var recovered = await GetLegacyCompletedSessionAsync(
            "2609101317495BC4",
            """{"Response":{"Success":true,"TxnRef":"****7495BC4","TxnType":"P","AmtPurchase":99,"ResponseCode":"00","ResponseText":"APPROVED","PurchaseAnalysisData":{"RFN":"RFN-LEGACY-99"}}}""");

        Assert.Null(recovered?.CardTransaction);
    }

    [Fact]
    public async Task Legacy_completed_session_rejects_masked_rfn()
    {
        var recovered = await GetLegacyCompletedSessionAsync(
            "2609101317495BC4",
            """{"Response":{"Success":true,"TxnRef":"2609101317495BC4","TxnType":"P","AmtPurchase":99,"ResponseCode":"00","ResponseText":"APPROVED","PurchaseAnalysisData":{"RFN":"****1234"}}}""");

        Assert.Null(recovered?.CardTransaction);
    }

    [Fact]
    public async Task Partially_persisted_request_evidence_never_falls_back_to_legacy_notification()
    {
        var recovered = await GetLegacyCompletedSessionAsync(
            "2601010101011234",
            """{"Response":{"Success":true,"TxnRef":"2601010101011234","TxnType":"P","AmtPurchase":99,"ResponseCode":"00","ResponseText":"APPROVED","PurchaseAnalysisData":{"RFN":"RFN-LEGACY-99"}}}""",
            requestTxnType: "P");

        Assert.Null(recovered?.CardTransaction);
    }

    private static async Task<LinklyCloudBackendSessionResponse?> GetLegacyCompletedSessionAsync(
        string txnRef,
        string notificationJson,
        string? requestTxnType = null)
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "legacy-complete-evidence",
            Status = "Completed",
            TxnRef = txnRef,
            RequestTxnType = requestTxnType,
            TransactionSuccess = true,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "legacy-complete-evidence",
            Type = "transaction",
            PayloadJson = notificationJson,
            ReceivedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            repository: repository);
        return await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", "legacy-complete-evidence", CancellationToken.None);
    }

    [Fact]
    public async Task Official_get_rejects_masked_txn_ref_even_when_the_suffix_matches()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK);
        var service = CreateService(transport);
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateTransactionRequest(), CancellationToken.None);
        var originalTxnRef = started.TxnRef!;
        transport.GetTransactionBody = JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = "****FFFF",
                TxnType = "P",
                AmtPurchase = 1000,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            }
        });

        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);
        var persisted = await service.Repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", started.SessionId, CancellationToken.None);

        Assert.Equal(originalTxnRef, recovered?.TxnRef);
        Assert.Equal(originalTxnRef, persisted?.TxnRef);
        Assert.Null(recovered?.CardTransaction);
        Assert.DoesNotContain(recovered?.Notifications ?? [], notification =>
            string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("260921123456AB12")]
    [InlineData("2609211234560A1F")]
    [InlineData("2609211234561234")]
    public async Task Official_get_alone_verifies_card_transaction_when_the_webhook_never_arrived(string txnRef)
    {
        // API 生成的交易引用是 12 位时间戳加 4 位十六进制。后 4 位只要含字母（约 85%），脱敏器就会把前面
        // 那串数字当成卡号掩掉；GET 刷新写入的通知若不保留身份字段，就永远对不上会话的交易引用。
        // webhook 没送达时 GET 是唯一的官方证据，这时也必须能核验出金额，否则实时收款只能报"结果未知"。
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "get-only-session",
            Status = "Pending",
            OperationType = "Transaction",
            TxnRef = txnRef,
            RequestTxnType = "P",
            RequestAmountCents = 1000,
            RequestRfn = txnRef,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: JsonSerializer.Serialize(new
            {
                Response = new
                {
                    Success = true,
                    TxnRef = txnRef,
                    TxnType = "P",
                    AmtPurchase = 1000,
                    ResponseCode = "00",
                    ResponseText = "APPROVED",
                    Pan = "4111111111111111",
                    PurchaseAnalysisData = new { RFN = txnRef }
                }
            }));
        var service = CreateService(transport, repository: repository);

        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", "get-only-session", CancellationToken.None);

        Assert.Equal("Completed", recovered?.Status);
        Assert.Equal(txnRef, recovered?.CardTransaction?.TxnRef);
        Assert.Equal(1000, recovered?.CardTransaction?.AmountCents);
        // 保留的只是交易引用与 RFN 两项身份字段，卡号等敏感内容仍必须脱敏后才落库和返回。
        var stored = await repository.GetNotificationsAsync(
            "Sandbox", "S01", "POS-01", "get-only-session", CancellationToken.None);
        Assert.DoesNotContain(stored, notification =>
            notification.PayloadJson.Contains("4111111111111111", StringComparison.Ordinal));
        Assert.DoesNotContain(recovered?.Notifications ?? [], notification =>
            notification.PayloadJson.Contains("4111111111111111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completed_notification_rejects_wrong_refund_reference()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateTransactionRequest(), CancellationToken.None);
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = started.TxnRef,
                TxnType = "P",
                AmtPurchase = 1000,
                ResponseCode = "00",
                ResponseText = "APPROVED",
                PurchaseAnalysisData = new { RFN = "WRONG-RFN" }
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
        Assert.DoesNotContain(recovered?.Notifications ?? [], item =>
            string.Equals(item.Type, "transaction", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://public.example/callback/")]
    [InlineData("https://localhost/callback/")]
    [InlineData("https://127.0.0.1/callback/")]
    [InlineData("https://10.1.2.3/callback/")]
    [InlineData("https://172.16.2.3/callback/")]
    [InlineData("https://192.168.2.3/callback/")]
    [InlineData("https://169.254.2.3/callback/")]
    [InlineData("https://pos.local/callback/")]
    [InlineData("https://terminal.internal/callback/")]
    [InlineData("https://store.lan/callback/")]
    [InlineData("https://192.0.0.8/callback/")]
    [InlineData("https://192.0.2.10/callback/")]
    [InlineData("https://198.18.0.1/callback/")]
    [InlineData("https://198.51.100.10/callback/")]
    [InlineData("https://203.0.113.10/callback/")]
    [InlineData("https://[::1]/callback/")]
    [InlineData("https://[fc00::1]/callback/")]
    [InlineData("https://[fe80::1]/callback/")]
    public async Task StartTransactionAsync_requires_configured_https_public_notification_url(
        string? publicNotificationBaseUrl)
    {
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            tokenProvider,
            publicNotificationBaseUrl);

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest(),
                CancellationToken.None));

        Assert.Equal("Linkly Cloud public notification base URL must be configured as an HTTPS URL.", exception.Message);
        Assert.Empty(tokenProvider.Calls);
    }

    [Fact]
    public async Task StartTransactionAsync_requires_notification_bearer_before_creating_pending_session()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(
            transport,
            tokenProvider,
            repository: repository,
            sandboxNotificationBearer: "");

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest(),
                CancellationToken.None));

        Assert.Equal("Linkly Cloud notification bearer is not configured.", exception.Message);
        Assert.Empty(tokenProvider.Calls);
        Assert.Null(transport.LastTransaction);
        Assert.Null(await repository.GetActiveSessionAsync("Sandbox", "S01", "POS-01", CancellationToken.None));
    }

    [Fact]
    public void Sql_repository_upsert_does_not_overwrite_completed_with_non_completed()
    {
        Assert.Contains("target.[Status] IN ('Completed', 'Cancelled')", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Status NOT IN ('Completed', 'Cancelled')", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, "Pending", "Retry")]
    // 429 是限流，语义即“稍后重试”。此前它落到兜底分支被写成 Failed + IsActive=false，
    // 该会话再也不向 Linkly 核对真实结果，已批准的交易会被报成失败并引发重刷。
    [InlineData(HttpStatusCode.TooManyRequests, "Pending", "Retry")]
    [InlineData(HttpStatusCode.InternalServerError, "Pending", "Retry")]
    [InlineData((HttpStatusCode)599, "Pending", "Retry")]
    [InlineData(HttpStatusCode.Unauthorized, "TokenRefreshRequired", "RefreshToken")]
    [InlineData(HttpStatusCode.NotFound, "NotSubmitted", "RetryNewSession")]
    public async Task RecoverAsync_maps_linkly_status_codes_to_recoverable_outcomes(
        HttpStatusCode statusCode,
        string expectedStatus,
        string expectedRecoveryAction)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(statusCode);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-1",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.RecoverAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(expectedRecoveryAction, response.RecoveryAction);
        Assert.Equal(sessionId, response.SessionId);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Fact]
    public async Task RecoverAsync_treats_202_as_short_pending_without_recovery_increment()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-202",
            RecoveryCount = 3,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.RecoverAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.RecoveryAction);
        Assert.Equal(3, response.RecoveryCount);
        Assert.Equal(202, response.LastHttpStatus);
    }

    [Fact]
    public async Task RecoverAsync_maps_operator_cancelled_response_to_cancelled_status()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.OK,
            """
            {
              "Response": {
                "Success": false,
                "TxnRef": "TXN-CANCELLED",
                "ResponseCode": "TM",
                "ResponseText": "Operator Cancelled"
              }
            }
            """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.RecoverAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal(LinklyCloudBackendStatusConstants.StatusCancelled, response.Status);
        Assert.False(response.TransactionSuccess);
        Assert.Equal("TM", response.ResponseCode);
        Assert.Equal("Operator Cancelled", response.ResponseText);

        var persisted = await service.Repository.GetSessionAsync(
            "Sandbox",
            "S01",
            "POS-01",
            sessionId,
            CancellationToken.None);
        Assert.Equal(LinklyCloudBackendStatusConstants.StatusCancelled, persisted?.Status);
        Assert.False(persisted?.IsActive);
    }

    [Fact]
    public async Task GetStatusAsync_refreshes_open_session_from_official_get_transaction_success()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": true,
                    "TxnRef": "TXN-OFFICIAL",
                    "ResponseCode": "08",
                    "ResponseText": "APPROVE WITH SIG",
                    "AmtPurchase": 1008,
                    "PurchaseAnalysisData": { "RFN": "RFN-OFFICIAL" }
                  }
                }
                """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.True(response.TransactionSuccess);
        Assert.Equal("TXN-OFFICIAL", response.TxnRef);
        Assert.Equal("08", response.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", response.ResponseText);
        Assert.Equal(200, response.LastHttpStatus);
        Assert.Equal(sessionId, transport.LastGetTransaction?.SessionId);
        var notification = Assert.Single(response.Notifications, item => item.Type == "transaction");
        using var payload = JsonDocument.Parse(notification.PayloadJson);
        var officialResponse = payload.RootElement.GetProperty("Response");
        Assert.Equal(1008, officialResponse.GetProperty("AmtPurchase").GetInt32());
        Assert.Equal("RFN-OFFICIAL", officialResponse.GetProperty("PurchaseAnalysisData").GetProperty("RFN").GetString());
    }

    [Fact]
    public async Task GetStatusAsync_refreshes_completed_session_with_missing_transaction_success_from_official_get_transaction()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": true,
                    "TxnRef": "TXN-OFFICIAL",
                    "ResponseCode": "00",
                    "ResponseText": "APPROVED"
                  }
                }
                """);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Completed",
            TxnRef = "TXN-LOCAL",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            TransactionSuccess = null,
            IsActive = false,
            LastHttpStatus = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response!.TransactionSuccess);
        Assert.Equal(sessionId, transport.LastGetTransaction?.SessionId);
    }

    [Fact]
    public async Task GetStatusAsync_keeps_completed_session_completed_when_missing_success_refresh_is_not_final()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.Accepted);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Completed",
            TxnRef = "TXN-LOCAL",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            TransactionSuccess = null,
            IsActive = false,
            LastHttpStatus = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.Null(response.TransactionSuccess);
        Assert.Equal("00", response.ResponseCode);
        Assert.Equal(202, response.LastHttpStatus);
    }

    [Fact]
    public async Task GetStatusAsync_refreshes_open_session_from_official_get_transaction_decline()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": false,
                    "TxnRef": "TXN-DECLINED",
                    "ResponseCode": "50",
                    "ResponseText": "SYSTEM ERROR"
                  }
                }
                """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.False(response.TransactionSuccess);
        Assert.Equal("TXN-DECLINED", response.TxnRef);
        Assert.Equal("50", response.ResponseCode);
        Assert.Equal("SYSTEM ERROR", response.ResponseText);
        Assert.Equal(200, response.LastHttpStatus);
    }

    [Fact]
    public async Task GetStatusAsync_maps_apphub_cancelled_response_to_cancelled_status()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": false,
                    "TxnRef": "TXN-APPHUB-CANCELLED",
                    "ResponseCode": "HD",
                    "ResponseText": "Txn Cancelled"
                  }
                }
                """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(LinklyCloudBackendStatusConstants.StatusCancelled, response!.Status);
        Assert.False(response.TransactionSuccess);
        Assert.Equal("TXN-APPHUB-CANCELLED", response.TxnRef);
        Assert.Equal("HD", response.ResponseCode);
        Assert.Equal("Txn Cancelled", response.ResponseText);
        Assert.Equal(200, response.LastHttpStatus);
    }

    [Fact]
    public async Task GetStatusAsync_keeps_decline_completed_not_cancelled()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": false,
                    "TxnRef": "TXN-DECLINED",
                    "ResponseCode": "50",
                    "ResponseText": "SYSTEM ERROR"
                  }
                }
                """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.False(response.TransactionSuccess);
        Assert.Equal("50", response.ResponseCode);
        Assert.Equal("SYSTEM ERROR", response.ResponseText);
    }

    [Fact]
    public async Task GetStatusAsync_keeps_operator_timeout_completed_not_cancelled()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: HttpStatusCode.OK,
            getTransactionBody: """
                {
                  "Response": {
                    "Success": false,
                    "TxnRef": "TXN-TIMEOUT",
                    "ResponseCode": "TI",
                    "ResponseText": "Operator Timeout"
                  }
                }
                """);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Completed", response!.Status);
        Assert.False(response.TransactionSuccess);
        Assert.Equal("TI", response.ResponseCode);
        Assert.Equal("Operator Timeout", response.ResponseText);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetStatusAsync_keeps_open_session_checking_when_official_get_transaction_is_not_final(HttpStatusCode statusCode)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.Accepted,
            getTransactionStatusCode: statusCode,
            getTransactionBody: null);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Pending", response!.Status);
        Assert.Null(response.TransactionSuccess);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task StartTransactionAsync_counts_transport_recovery_failures(HttpStatusCode statusCode)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(statusCode));

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Retry", response.RecoveryAction);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Fact]
    public async Task StartTransactionAsync_keeps_202_as_plain_pending_without_recovery_count()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.RecoveryAction);
        Assert.Equal(0, response.RecoveryCount);
        Assert.Equal(202, response.LastHttpStatus);
    }

    [Fact]
    public async Task GetActiveSessionAsync_returns_current_claim_scoped_active_session()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var active = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        var response = await service.GetActiveSessionAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(active.SessionId, response!.SessionId);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.Null(await service.GetActiveSessionAsync("S01", "POS-02", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveSessionAsync_returns_active_settlement_with_its_operation_type()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartSettlementAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendSettlementRequest("Sandbox"),
            CancellationToken.None);

        var active = await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(active);
        Assert.Equal(started.SessionId, active!.SessionId);
        Assert.Equal("Settlement", active.OperationType);
    }

    [Fact]
    public async Task GetActiveSessionAsync_ignores_completed_unacknowledged_session_but_resumable_returns_it()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "completed-unacknowledged",
            Status = "Completed",
            TxnRef = "TXN-COMPLETE",
            TransactionSuccess = true,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        var resumable = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Null(response);
        Assert.NotNull(resumable);
        Assert.Equal("completed-unacknowledged", resumable!.SessionId);
        Assert.Equal("Completed", resumable.Status);
        Assert.Null(resumable.ClientAcknowledgedAt);
    }

    [Fact]
    public async Task GetResumableSessionAsync_returns_active_session_before_completed_unacknowledged()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "completed-session",
            Status = "Completed",
            TxnRef = "TXN-COMPLETE",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        }, CancellationToken.None);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "active-session",
            Status = "Pending",
            TxnRef = "TXN-ACTIVE",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("active-session", response!.SessionId);
        Assert.Equal("Pending", response.Status);
    }

    [Fact]
    public async Task GetResumableSessionAsync_returns_latest_completed_unacknowledged_when_no_active_session()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "older-completed",
            Status = "Completed",
            TxnRef = "TXN-OLD",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        }, CancellationToken.None);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "latest-completed",
            Status = "Completed",
            TxnRef = "TXN-LATEST",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("latest-completed", response!.SessionId);
        Assert.Equal("Completed", response.Status);
        Assert.Null(response.ClientAcknowledgedAt);
    }

    [Theory]
    [InlineData("Failed", "OPERATOR TIMEOUT")]
    [InlineData("NotSubmitted", "Linkly Cloud returned HTTP 400.")]
    public async Task GetResumableSessionAsync_returns_latest_final_unacknowledged_when_no_active_session(
        string status,
        string responseText)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "older-completed",
            Status = "Completed",
            TxnRef = "TXN-OLD",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ClientAcknowledgedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
        }, CancellationToken.None);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "latest-final",
            Status = status,
            TxnRef = "TXN-FINAL",
            ResponseCode = status == "Failed" ? "TM" : null,
            ResponseText = responseText,
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("latest-final", response!.SessionId);
        Assert.Equal(status, response.Status);
        Assert.Equal(responseText, response.ResponseText);
        Assert.Null(response.ClientAcknowledgedAt);
    }

    [Fact]
    public async Task GetResumableSessionAsync_returns_unacknowledged_cancelled_session_once()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "cancelled-session",
            Status = LinklyCloudBackendStatusConstants.StatusCancelled,
            TxnRef = "TXN-CANCELLED",
            TransactionSuccess = false,
            ResponseCode = "TM",
            ResponseText = "Operator Cancelled",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("cancelled-session", response!.SessionId);
        Assert.Equal(LinklyCloudBackendStatusConstants.StatusCancelled, response.Status);
        Assert.Null(response.ClientAcknowledgedAt);
    }

    [Fact]
    public async Task GetResumableSessionAsync_does_not_return_acknowledged_cancelled_session()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "cancelled-session",
            Status = LinklyCloudBackendStatusConstants.StatusCancelled,
            TxnRef = "TXN-CANCELLED",
            TransactionSuccess = false,
            ResponseCode = "TM",
            ResponseText = "Operator Cancelled",
            IsActive = false,
            ClientAcknowledgedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task AcknowledgeSessionAsync_marks_session_and_removes_it_from_resumable_fallback()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "completed-session",
            Status = "Completed",
            TxnRef = "TXN-COMPLETE",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var acknowledged = await service.AcknowledgeSessionAsync(
            "S01",
            "POS-01",
            "Sandbox",
            "completed-session",
            supervisorResolved: false,
            CancellationToken.None);

        Assert.NotNull(acknowledged.ClientAcknowledgedAt);
        Assert.Equal("completed-session", acknowledged.SessionId);
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task AcknowledgeSessionAsync_clears_pending_active_session_for_manual_recovery()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "pending-session",
            Status = "Pending",
            TxnRef = "TXN-PENDING",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var acknowledged = await service.AcknowledgeSessionAsync(
            "S01",
            "POS-01",
            "Sandbox",
            "pending-session",
            supervisorResolved: false,
            CancellationToken.None);

        var persisted = await service.Repository.GetSessionAsync(
            "Sandbox",
            "S01",
            "POS-01",
            "pending-session",
            CancellationToken.None);

        Assert.NotNull(acknowledged.ClientAcknowledgedAt);
        Assert.NotNull(persisted);
        Assert.False(persisted.IsActive);
        // 普通 ack 不能把结果未知的会话说成已结束，终端管理闸门继续按未知处理。
        Assert.Equal("Pending", persisted.Status);
        Assert.Null(await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Theory]
    [InlineData("Pending", true, "SupervisorResolved")]
    [InlineData("TokenRefreshRequired", true, "SupervisorResolved")]
    [InlineData("Completed", false, "Completed")]
    [InlineData("Cancelled", false, "Cancelled")]
    [InlineData("Failed", false, "Failed")]
    [InlineData("NotSubmitted", false, "NotSubmitted")]
    public async Task AcknowledgeSessionAsync_supervisor_resolution_only_replaces_non_final_status(
        string status,
        bool isActive,
        string expectedStatus)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "supervisor-session",
            Status = status,
            TxnRef = "TXN-SUPERVISOR",
            IsActive = isActive,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var acknowledged = await service.AcknowledgeSessionAsync(
            "S01",
            "POS-01",
            "Sandbox",
            "supervisor-session",
            supervisorResolved: true,
            CancellationToken.None);
        var persisted = await service.Repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", "supervisor-session", CancellationToken.None);

        Assert.Equal(expectedStatus, acknowledged.Status);
        Assert.NotNull(persisted);
        Assert.Equal(expectedStatus, persisted.Status);
        Assert.False(persisted.IsActive);
        Assert.NotNull(persisted.ClientAcknowledgedAt);
    }

    [Fact]
    public async Task Late_linkly_final_result_replaces_supervisor_resolved_marker_and_stays_final()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "late-final-session",
            Status = "Pending",
            TxnRef = "TXN-LATE",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await service.AcknowledgeSessionAsync(
            "S01", "POS-01", "Sandbox", "late-final-session", supervisorResolved: true, CancellationToken.None);

        await service.ReceiveNotificationAsync(
            "Sandbox",
            "late-final-session",
            "transaction",
            "Bearer sandbox-notify",
            JsonDocument.Parse("""{ "Response": { "Success": false, "ResponseCode": "05", "ResponseText": "DECLINED", "TxnRef": "TXN-LATE" } }""").RootElement,
            CancellationToken.None);
        var persisted = await service.Repository.GetSessionAsync(
            "Sandbox", "S01", "POS-01", "late-final-session", CancellationToken.None);

        // Linkly 的真实终态优先于主管结案标记；两者都是终态，不会重新挡住付款或终端管理。
        Assert.NotNull(persisted);
        Assert.Equal("Completed", persisted.Status);
        Assert.Equal("05", persisted.ResponseCode);
        Assert.False(persisted.IsActive);
        Assert.NotNull(persisted.ClientAcknowledgedAt);
    }

    [Fact]
    public async Task Recovery_service_operations_write_certification_evidence_json()
    {
        var logger = new RecordingLogger<LinklyCloudBackendAsyncService>();
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(
                HttpStatusCode.OK,
                """
                {
                  "Response": {
                    "Success": true,
                    "TxnRef": "TXN-RECOVER",
                    "ResponseCode": "00",
                    "ResponseText": "APPROVED"
                  }
                }
                """),
            logger: logger);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "recover-session",
            Status = "Pending",
            TxnRef = "TXN-RECOVER",
            ReceiptText = "MERCHANT RECEIPT\nAPPROVED",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await service.Repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "recover-session",
            Type = "receipt",
            PayloadJson = """{ "Response": { "ReceiptText": ["MERCHANT RECEIPT", "APPROVED"] } }""",
            ReceivedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        await service.GetStatusAsync("S01", "POS-01", "Sandbox", "recover-session", CancellationToken.None);
        await service.RecoverAsync(
            "S01",
            "POS-01",
            "recover-session",
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);
        await service.AcknowledgeSessionAsync("S01", "POS-01", "Sandbox", "recover-session", supervisorResolved: false, CancellationToken.None);
        await service.MarkReceiptPrintedAsync(
            "S01",
            "POS-01",
            "recover-session",
            new LinklyCloudBackendMarkReceiptPrintedRequest("Sandbox"),
            CancellationToken.None);

        AssertServiceEvidenceLog(logger.Lines, "resumable session", "request", "GET", "4.1.2", expectRequestJson: false, expectResponseJson: false);
        AssertServiceEvidenceLog(logger.Lines, "resumable session", "response", "GET", "4.1.2", expectRequestJson: false, expectResponseJson: true);
        AssertServiceEvidenceLog(logger.Lines, "status", "request", "GET", "3.1.3/4.1.2", expectRequestJson: false, expectResponseJson: false);
        AssertServiceEvidenceLog(logger.Lines, "status", "response", "GET", "3.1.3/4.1.2", expectRequestJson: false, expectResponseJson: true);
        AssertServiceEvidenceLog(logger.Lines, "recover", "request", "POST", "3.1.3/4.1.2", expectRequestJson: true, expectResponseJson: false);
        AssertServiceEvidenceLog(logger.Lines, "recover", "response", "POST", "3.1.3/4.1.2", expectRequestJson: false, expectResponseJson: true);
        AssertServiceEvidenceLog(logger.Lines, "acknowledge", "request", "POST", "4.1.2", expectRequestJson: false, expectResponseJson: false);
        AssertServiceEvidenceLog(logger.Lines, "acknowledge", "response", "POST", "4.1.2", expectRequestJson: false, expectResponseJson: true);
        AssertServiceEvidenceLog(logger.Lines, "receipt/printed", "request", "POST", "4.1.3", expectRequestJson: true, expectResponseJson: false);
        AssertServiceEvidenceLog(logger.Lines, "receipt/printed", "response", "POST", "4.1.3", expectRequestJson: false, expectResponseJson: true);
        using var resumableLog = FindLinklyLog(logger.Lines, "resumable session", "response");
        using var resumableResponseJson = JsonDocument.Parse(resumableLog
            .RootElement
            .GetProperty("details")
            .GetProperty("data")
            .GetProperty("responseJson")
            .GetString()!);
        var loggedReceiptText = resumableResponseJson.RootElement.GetProperty("receiptText");
        Assert.True(loggedReceiptText.GetProperty("hasValue").GetBoolean());
        var loggedPayloadJson = resumableResponseJson.RootElement
            .GetProperty("notifications")[0]
            .GetProperty("payloadJson");
        Assert.True(loggedPayloadJson.GetProperty("hasValue").GetBoolean());
        Assert.DoesNotContain("MERCHANT RECEIPT", logger.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("\"ReceiptText\"", logger.Lines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AcknowledgeSessionAsync_throws_when_session_is_missing()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));

        await Assert.ThrowsAsync<LinklyCloudBackendSessionNotFoundException>(() =>
            service.AcknowledgeSessionAsync(
                "S01",
                "POS-01",
                "Sandbox",
                "missing-session",
                supervisorResolved: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task SendKeyAsync_uses_server_token_scope_for_existing_session()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-1",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
            CancellationToken.None);

        Assert.Equal("https://server-rest.example/POS-01/", transport.LastSendKey?.RestBaseUrl);
        Assert.Equal("server-token-POS-01", transport.LastSendKey?.AccessToken);
        Assert.Equal([("Sandbox", "S01", "POS-01")], tokenProvider.Calls);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    [InlineData("3", "3")]
    [InlineData("OK", "0")]
    [InlineData("CANCEL", "0")]
    [InlineData("YES", "1")]
    [InlineData("NO", "2")]
    [InlineData("AUTH", "3")]
    [InlineData(" auth ", "3")]
    public async Task SendKeyAsync_normalizes_supported_linkly_keys_to_official_numeric_enum(
        string key,
        string expectedKey)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", key, null),
            CancellationToken.None);

        Assert.Equal(expectedKey, transport.LastSendKey?.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("OKAY")]
    public async Task SendKeyAsync_rejects_keys_outside_official_linkly_enum(string? key)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.SendKeyAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendSendKeyRequest("Sandbox", key!, null),
                CancellationToken.None));

        Assert.Equal("key must be one of 0, 1, 2, or 3.", exception.Message);
        Assert.Empty(tokenProvider.Calls);
        Assert.Null(transport.LastSendKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendKeyAsync_marks_408_and_5xx_as_recovery_retry(HttpStatusCode statusCode)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(statusCode);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-SENDKEY",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Retry", response.RecoveryAction);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Fact]
    public async Task SendKeyAsync_keeps_pending_active_when_linkly_returns_400()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.BadRequest);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var response = await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
            CancellationToken.None);
        var active = await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.ResponseText);
        Assert.Null(response.RecoveryAction);
        Assert.Equal(0, response.RecoveryCount);
        Assert.Equal(400, response.LastHttpStatus);
        Assert.NotNull(active);
        Assert.Equal(sessionId, active!.SessionId);
    }

    [Theory]
    [InlineData("start", HttpStatusCode.Accepted)]
    [InlineData("recover", HttpStatusCode.RequestTimeout)]
    [InlineData("sendkey", HttpStatusCode.InternalServerError)]
    public async Task Transport_stale_responses_do_not_rollback_completed_sessions(
        string operation,
        HttpStatusCode staleStatusCode)
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var transport = new CompletingBeforeResponseTransport(repository, staleStatusCode);
        var service = CreateService(transport, repository: repository);
        var sessionId = Guid.NewGuid().ToString("D");

        if (!string.Equals(operation, "start", StringComparison.Ordinal))
        {
            await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                SessionId = sessionId,
                Status = "Pending",
                TxnRef = "TXN-PENDING",
                IsActive = true,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }

        var response = operation switch
        {
            "start" => await service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest(),
                CancellationToken.None),
            "recover" => await service.RecoverAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendRecoverRequest("Sandbox"),
                CancellationToken.None),
            "sendkey" => await service.SendKeyAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
                CancellationToken.None),
            _ => throw new InvalidOperationException(operation)
        };

        var persisted = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            response.SessionId,
            CancellationToken.None);

        Assert.Equal("Completed", response.Status);
        Assert.Equal("Completed", persisted?.Status);
        Assert.Equal("00", persisted?.ResponseCode);
        var blockingSession = await repository.GetResumableSessionAsync("Sandbox", "S01", "POS-01", CancellationToken.None);
        Assert.NotNull(blockingSession);
        Assert.Equal(response.SessionId, blockingSession!.SessionId);
        Assert.False(blockingSession.IsActive);
    }

    [Fact]
    public async Task StartTransactionAsync_returns_clear_error_when_terminal_secret_is_missing()
    {
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            new ThrowingLinklyCloudBackendTokenProvider(
                "Linkly Cloud terminal secret is not configured for this terminal."));

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None));

        Assert.Equal("Linkly Cloud terminal secret is not configured for this terminal.", exception.Message);
    }

    [Fact]
    public async Task TokenProvider_uses_environment_store_device_terminal_secret_and_pos_id()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            },
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-02",
                Secret = "secret-pos-02",
                PosId = "22222222-2222-4222-8222-222222222222"
            });
        var handler = new CapturingTokenHttpMessageHandler();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            new HttpClient(handler),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }));

        var pos01Token = await provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None);
        var pos02Token = await provider.GetTokenAsync("Sandbox", "S01", "POS-02", CancellationToken.None);

        Assert.Equal("https://rest.sandbox.example/v1/", pos01Token.RestBaseUrl);
        Assert.Equal("token-for-111111111111", pos01Token.AccessToken);
        Assert.Equal("token-for-222222222222", pos02Token.AccessToken);
        Assert.Equal(
            [("S01", "Sandbox"), ("S01", "Sandbox")],
            credentialRepository.Calls);
        Assert.Equal(
            [("Sandbox", "S01", "POS-01"), ("Sandbox", "S01", "POS-02")],
            terminalRepository.Calls);
        Assert.Equal("secret-pos-01", handler.RequestBodies[0].RootElement.GetProperty("secret").GetString());
        Assert.Equal("11111111-1111-4111-8111-111111111111", handler.RequestBodies[0].RootElement.GetProperty("posId").GetString());
        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", handler.RequestBodies[0].RootElement.GetProperty("posVendorId").GetString());
        Assert.Equal("secret-pos-02", handler.RequestBodies[1].RootElement.GetProperty("secret").GetString());
        Assert.Equal("22222222-2222-4222-8222-222222222222", handler.RequestBodies[1].RootElement.GetProperty("posId").GetString());
        Assert.Equal(new bool?[] { null, null }, handler.ConnectionCloseHeaders);
    }

    [Fact]
    public async Task TokenProvider_terminal_overload_uses_physical_terminal_credentials_only()
    {
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var credentialRepository = new CapturingCredentialRepository(null);
        var legacyTerminalRepository = new CapturingTerminalCredentialRepository();
        var cloudTerminalRepository = new FixedCloudTerminalRepository(new LinklyCloudTerminalRecord
        {
            TerminalId = terminalId,
            Environment = "Sandbox",
            StoreCode = "S01",
            LaneNo = 1,
            DisplayName = "Front",
            Username = "lane-user",
            Password = "lane-password",
            Secret = "physical-terminal-secret",
            PosId = "11111111-1111-4111-8111-111111111111",
            PairingState = "Ready"
        });
        var handler = new CapturingTokenHttpMessageHandler();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            legacyTerminalRepository,
            new HttpClient(handler),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }),
            cloudTerminalRepository: cloudTerminalRepository);

        var token = await provider.GetTokenAsync(
            "Sandbox", "S01", "POS-99", terminalId, CancellationToken.None);

        Assert.Equal("token-for-111111111111", token.AccessToken);
        Assert.Empty(credentialRepository.Calls);
        Assert.Empty(legacyTerminalRepository.Calls);
        var body = Assert.Single(handler.RequestBodies).RootElement;
        Assert.Equal("physical-terminal-secret", body.GetProperty("secret").GetString());
        Assert.Equal("11111111-1111-4111-8111-111111111111", body.GetProperty("posId").GetString());
    }

    [Fact]
    public async Task TokenProvider_active_mode_without_terminal_id_fails_closed_before_legacy_lookup()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "legacy-user",
            Password = "legacy-password",
            UpdatedAt = DateTime.UtcNow
        });
        var legacyTerminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "legacy-secret",
                PosId = "11111111-1111-4111-8111-111111111111"
            });
        var cloudTerminalRepository = new FixedCloudTerminalRepository(new LinklyCloudTerminalRecord
        {
            TerminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Environment = "Sandbox",
            StoreCode = "S01"
        });
        var handler = new CapturingTokenHttpMessageHandler();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            legacyTerminalRepository,
            new HttpClient(handler),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }),
            cloudTerminalRepository: cloudTerminalRepository);

        var exception = await Assert.ThrowsAsync<LinklyCloudTerminalSelectionConflictException>(() =>
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None));

        Assert.Equal("Active Linkly Cloud multi-terminal mode requires terminalId.", exception.Message);
        Assert.Empty(credentialRepository.Calls);
        Assert.Empty(legacyTerminalRepository.Calls);
        Assert.Empty(handler.RequestBodies);
        Assert.Equal(1, cloudTerminalRepository.ModeCalls);
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Draft")]
    public async Task TokenProvider_non_active_mode_keeps_legacy_credentials_compatible(string mode)
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "legacy-user",
            Password = "legacy-password",
            UpdatedAt = DateTime.UtcNow
        });
        var legacyTerminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "legacy-secret",
                PosId = "11111111-1111-4111-8111-111111111111"
            });
        var cloudTerminalRepository = new FixedCloudTerminalRepository(
            new LinklyCloudTerminalRecord
            {
                TerminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                Environment = "Sandbox",
                StoreCode = "S01"
            },
            mode);
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            legacyTerminalRepository,
            new HttpClient(new CapturingTokenHttpMessageHandler()),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }),
            cloudTerminalRepository: cloudTerminalRepository);

        var token = await provider.GetTokenAsync(
            "Sandbox", "S01", "POS-01", CancellationToken.None);

        Assert.Equal("token-for-111111111111", token.AccessToken);
        Assert.Single(credentialRepository.Calls);
        Assert.Single(legacyTerminalRepository.Calls);
        Assert.Equal(1, cloudTerminalRepository.ModeCalls);
    }

    [Fact]
    public async Task TokenProvider_uses_fixed_production_pos_vendor_id()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Production",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            });
        var handler = new CapturingTokenHttpMessageHandler();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            new HttpClient(handler),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                ProductionAuthBaseUrl = "https://auth.example/v1/",
                ProductionRestBaseUrl = "https://rest.example/v1/",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }));

        await provider.GetTokenAsync("Production", "S01", "POS-01", CancellationToken.None);

        var body = Assert.Single(handler.RequestBodies).RootElement;
        Assert.Equal("secret-pos-01", body.GetProperty("secret").GetString());
        Assert.Equal("HBPOS", body.GetProperty("posName").GetString());
        Assert.Equal("2026.5.1", body.GetProperty("posVersion").GetString());
        Assert.Equal("11111111-1111-4111-8111-111111111111", body.GetProperty("posId").GetString());
        Assert.Equal(LinklyCloudIdentityConstants.ProductionPosVendorId, body.GetProperty("posVendorId").GetString());
    }

    [Fact]
    public async Task Same_linkly_terminal_outbound_requests_share_a_maximum_concurrency_of_two()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        using var handler = new ConcurrentLinklyHttpMessageHandler();
        using var tokenHttpClient = new HttpClient(handler, disposeHandler: false);
        using var transportHttpClient = new HttpClient(handler, disposeHandler: false);
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            tokenHttpClient,
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }));
        var transport = new HttpLinklyCloudBackendAsyncTransport(transportHttpClient);

        await Task.WhenAll(
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None),
            transport.GetTransactionAsync(
                new LinklyCloudBackendTransportSessionRequest(
                    "Sandbox",
                    "https://rest.sandbox.example/v1/",
                    "access-token",
                    "session-1",
                    "S01",
                    "POS-01"),
                CancellationToken.None),
            transport.GetTransactionAsync(
                new LinklyCloudBackendTransportSessionRequest(
                    "Sandbox",
                    "https://rest.sandbox.example/v1/",
                    "access-token",
                    "session-2",
                    "S01",
                    "POS-01"),
                CancellationToken.None));

        Assert.True(
            handler.MaxConcurrency <= 2,
            $"Linkly 出站 HTTP 请求峰值并发为 {handler.MaxConcurrency}，必须不超过 2。");
    }

    [Fact]
    public async Task Different_linkly_terminals_do_not_share_the_same_two_request_limit()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            },
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-02",
                Secret = "secret-pos-02",
                PosId = "22222222-2222-4222-8222-222222222222"
            });
        using var handler = new ConcurrentLinklyHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            httpClient,
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }));

        await Task.WhenAll(
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None),
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None),
            provider.GetTokenAsync("Sandbox", "S01", "POS-02", CancellationToken.None),
            provider.GetTokenAsync("Sandbox", "S01", "POS-02", CancellationToken.None));

        Assert.Equal(4, handler.MaxConcurrency);
    }

    [Fact]
    public async Task Same_physical_terminal_shares_two_request_limit_across_token_and_transaction_operations()
    {
        // 闸门是进程级静态字典：用本用例独有的门店与终端，避免与其他用例共用信号量。
        var terminalId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        const string storeCode = "S-GATE";
        var cloudTerminalRepository = new FixedCloudTerminalRepository(new LinklyCloudTerminalRecord
        {
            TerminalId = terminalId,
            Environment = "Sandbox",
            StoreCode = storeCode,
            LaneNo = 1,
            DisplayName = "Front",
            Username = "lane-user",
            Password = "lane-password",
            Secret = "physical-terminal-secret",
            PosId = "11111111-1111-4111-8111-111111111111",
            PairingState = "Ready"
        });
        using var handler = new ConcurrentLinklyHttpMessageHandler();
        using var tokenHttpClient = new HttpClient(handler, disposeHandler: false);
        using var transportHttpClient = new HttpClient(handler, disposeHandler: false);
        var provider = new HttpLinklyCloudBackendTokenProvider(
            new CapturingCredentialRepository(null),
            new CapturingTerminalCredentialRepository(),
            tokenHttpClient,
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }),
            cloudTerminalRepository: cloudTerminalRepository);
        var transport = new HttpLinklyCloudBackendAsyncTransport(transportHttpClient);
        const string restBaseUrl = "https://rest.sandbox.example/v1/";
        var notification = new LinklyCloudBackendNotificationRequest("https://pos.example/notify/{{type}}", "Bearer notify");

        // 同一台实体刷卡机：Token、发起交易、按键、状态查询合计在途不得超过 2。
        await Task.WhenAll(
            provider.GetTokenAsync("Sandbox", storeCode, "POS-01", terminalId, CancellationToken.None),
            transport.StartTransactionAsync(
                new LinklyCloudBackendTransportTransactionRequest(
                    "Sandbox", restBaseUrl, "access-token", "session-start", "P", 1000, "TXN-GATE", null,
                    notification, storeCode, "POS-01", terminalId),
                CancellationToken.None),
            transport.SendKeyAsync(
                new LinklyCloudBackendTransportSendKeyRequest(
                    "Sandbox", restBaseUrl, "access-token", "session-start", "0", null, storeCode, "POS-01", terminalId),
                CancellationToken.None),
            transport.GetTransactionAsync(
                new LinklyCloudBackendTransportSessionRequest(
                    "Sandbox", restBaseUrl, "access-token", "session-start", storeCode, "POS-01", terminalId),
                CancellationToken.None));

        Assert.True(
            handler.MaxConcurrency <= 2,
            $"同一实体终端的 Linkly 出站请求峰值并发为 {handler.MaxConcurrency}，必须不超过 2。");
    }

    [Fact]
    public async Task TokenProvider_logs_linkly_token_request_and_response_json()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        var logger = new RecordingLogger<HttpLinklyCloudBackendTokenProvider>();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            new HttpClient(new CapturingTokenHttpMessageHandler()),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }),
            logger);

        await provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None);

        using var requestLog = FindLinklyLog(logger.Lines, "token", "request");
        Assert.Equal("api-backend-token-provider", requestLog.RootElement.GetProperty("source").GetString());
        Assert.Equal("https://auth.sandbox.example/v1/tokens/cloudpos", requestLog.RootElement.GetProperty("details").GetProperty("url").GetString());
        Assert.True(requestLog.RootElement.GetProperty("request").GetProperty("secret").GetProperty("hasValue").GetBoolean());
        Assert.Equal(13, requestLog.RootElement.GetProperty("request").GetProperty("secret").GetProperty("length").GetInt32());
        Assert.Equal("11111111-1111-4111-8111-111111111111", requestLog.RootElement.GetProperty("request").GetProperty("posId").GetString());
        using var responseLog = FindLinklyLog(logger.Lines, "token", "response");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.True(responseLog.RootElement.GetProperty("response").GetProperty("token").GetProperty("hasValue").GetBoolean());
        Assert.DoesNotContain("secret-pos-01", logger.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("token-for-111111111111", logger.Lines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Transport_logs_linkly_sandbox_rest_request_and_response_json()
    {
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(
            """
            {
              "SessionId": "session-1",
              "Response": {
                "TxnRef": "260601120001",
                "ResponseCode": "00"
              }
            }
            """);
        var transport = new HttpLinklyCloudBackendAsyncTransport(
            new HttpClient(handler),
            logger);

        await transport.StartTransactionAsync(
            new LinklyCloudBackendTransportTransactionRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "session-1",
                "P",
                1234,
                "260601120001",
                new Dictionary<string, string> { ["OPR"] = "C001|Cashier" },
                new LinklyCloudBackendNotificationRequest(
                    "https://public.example/api/v1/linkly/cloud-notifications/Sandbox/session-1/transaction",
                    "Bearer callback-secret"),
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Null(handler.LastRequest!.Headers.ConnectionClose);
        using var requestLog = FindLinklyLog(logger.Lines, "transaction", "request");
        Assert.Equal("api-backend-transport", requestLog.RootElement.GetProperty("source").GetString());
        Assert.Equal("POST", requestLog.RootElement.GetProperty("details").GetProperty("method").GetString());
        Assert.Equal("https://rest.sandbox.example/v1/sessions/session-1/transaction?async=true", requestLog.RootElement.GetProperty("details").GetProperty("url").GetString());
        Assert.Equal("260601120001", requestLog.RootElement.GetProperty("details").GetProperty("txnRef").GetString());
        Assert.Equal("260601120001", requestLog.RootElement.GetProperty("request").GetProperty("Request").GetProperty("TxnRef").GetString());
        var notification = requestLog.RootElement
            .GetProperty("request")
            .GetProperty("Notification");
        Assert.False(notification.TryGetProperty("AuthorizationHeader", out _));
        Assert.DoesNotContain("callback-secret", logger.Lines, StringComparer.Ordinal);
        using var responseLog = FindLinklyLog(logger.Lines, "transaction", "response");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal("session-1", responseLog.RootElement.GetProperty("response").GetProperty("SessionId").GetString());
    }

    [Fact]
    public async Task Transport_logs_sanitize_settlement_response_card_and_sensitive_fields()
    {
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(
            """
            {
              "Response": {
                "ResponseText": "CARD 4111111111111111",
                "CardNumber": "4111111111111111",
                "Track2": "4111111111111111=2512",
                "CVV": "123",
                "refreshToken": "do-not-log",
                "RRN": "123456789012"
              }
            }
            """);
        var transport = new HttpLinklyCloudBackendAsyncTransport(new HttpClient(handler), logger);

        await transport.StartSettlementAsync(
            new LinklyCloudBackendTransportSettlementRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "session-1",
                new LinklyCloudBackendNotificationRequest(
                    "https://public.example/notification",
                    "Bearer callback-secret"),
                "S01",
                "POS-01"),
            CancellationToken.None);

        using var responseLog = FindLinklyLog(logger.Lines, "settlement", "response");
        var response = responseLog.RootElement.GetProperty("response").GetProperty("Response");
        Assert.Equal("****1111", response.GetProperty("CardNumber").GetString());
        Assert.Equal("123456789012", response.GetProperty("RRN").GetString());
        Assert.False(response.TryGetProperty("Track2", out _));
        Assert.False(response.TryGetProperty("CVV", out _));
        Assert.False(response.TryGetProperty("refreshToken", out _));
        Assert.Equal(
            "CARD ****1111",
            responseLog.RootElement.GetProperty("details").GetProperty("responseText").GetString());
        Assert.DoesNotContain("4111111111111111", logger.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("do-not-log", logger.Lines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Transport_logs_sanitize_non_json_settlement_response_text()
    {
        const string responseText =
            "SETTLEMENT OK; refreshToken=do-not-log; Track2=4111111111111111=2512; CVV=123; CardNumber=4111111111111111; RRN=123456789012";
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(responseText);
        var transport = new HttpLinklyCloudBackendAsyncTransport(new HttpClient(handler), logger);

        await transport.StartSettlementAsync(
            new LinklyCloudBackendTransportSettlementRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "session-1",
                new LinklyCloudBackendNotificationRequest(
                    "https://public.example/notification",
                    "Bearer callback-secret"),
                "S01",
                "POS-01"),
            CancellationToken.None);

        using var responseLog = FindLinklyLog(logger.Lines, "settlement", "response");
        var loggedResponseJson = responseLog.RootElement
            .GetProperty("details")
            .GetProperty("responseJson")
            .GetString()!;
        Assert.Contains("SETTLEMENT OK", loggedResponseJson, StringComparison.Ordinal);
        Assert.Contains("****1111", loggedResponseJson, StringComparison.Ordinal);
        Assert.Contains("RRN=123456789012", loggedResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", loggedResponseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Track2", loggedResponseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CVV", loggedResponseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-log", loggedResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", loggedResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_status_test_posts_status_without_txn_ref_and_logs_evidence()
    {
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(
            """
            {
              "SessionId": "status-session-1",
              "Response": {
                "Success": false,
                "TxnRef": "LAST-TXN-1",
                "Date": "050626",
                "Time": "143000",
                "ResponseCode": "05",
                "ResponseText": "DECLINED"
              }
            }
            """);
        var transport = new HttpLinklyCloudBackendAsyncTransport(new HttpClient(handler), logger);

        await transport.SendStatusAsync(
            new LinklyCloudBackendTransportStatusRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "status-session-1",
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://rest.sandbox.example/v1/sessions/status-session-1/status?async=false",
            handler.LastRequest.RequestUri!.AbsoluteUri);
        var requestJson = handler.LastRequestBody!;
        using var requestBody = JsonDocument.Parse(requestJson);
        var request = requestBody.RootElement.GetProperty("Request");
        Assert.Equal("00", request.GetProperty("Merchant").GetString());
        Assert.Equal("00", request.GetProperty("Application").GetString());
        Assert.Equal("0", request.GetProperty("StatusType").GetString());
        Assert.False(request.TryGetProperty("TxnRef", out _));
        Assert.DoesNotContain("TxnRef", requestJson, StringComparison.Ordinal);

        using var requestLog = FindLinklyLog(logger.Lines, "transaction-status-test", "request");
        var requestDetails = requestLog.RootElement.GetProperty("details");
        Assert.Equal("POST", requestDetails.GetProperty("method").GetString());
        Assert.Equal("https://rest.sandbox.example/v1/sessions/status-session-1/status?async=false", requestDetails.GetProperty("url").GetString());
        Assert.Equal("status-session-1", requestDetails.GetProperty("transactionReference").GetString());
        Assert.Equal(JsonValueKind.Null, requestDetails.GetProperty("txnRef").ValueKind);
        Assert.False(requestDetails.GetProperty("txnRefSpecified").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(requestDetails.GetProperty("timestamp").GetString()));
        Assert.Contains("\"StatusType\":\"0\"", requestDetails.GetProperty("requestJson").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TxnRef", requestDetails.GetProperty("requestJson").GetString(), StringComparison.Ordinal);

        using var responseLog = FindLinklyLog(logger.Lines, "transaction-status-test", "response");
        var responseDetails = responseLog.RootElement.GetProperty("details");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        using var responseBody = JsonDocument.Parse(responseDetails.GetProperty("responseJson").GetString()!);
        Assert.Equal("05", responseBody.RootElement.GetProperty("Response").GetProperty("ResponseCode").GetString());
        Assert.Equal("LAST-TXN-1", responseDetails.GetProperty("responseTxnRef").GetString());
        Assert.Equal("050626", responseDetails.GetProperty("responseDate").GetString());
        Assert.Equal("143000", responseDetails.GetProperty("responseTime").GetString());
        Assert.Equal("05", responseDetails.GetProperty("responseCode").GetString());
        Assert.Equal("DECLINED", responseDetails.GetProperty("responseText").GetString());
    }

    [Fact]
    public async Task Transport_logon_test_posts_logon_request_and_logs_evidence()
    {
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(
            """
            {
              "SessionId": "logon-session-1",
              "ResponseType": "logon",
              "Response": {
                "Success": true,
                "ResponseCode": "00",
                "ResponseText": "APPROVED",
                "Catid": "12345678",
                "Caid": "123456789012345",
                "PinPadVersion": "1.8.6.0"
              }
            }
            """);
        var transport = new HttpLinklyCloudBackendAsyncTransport(new HttpClient(handler), logger);

        await transport.SendLogonAsync(
            new LinklyCloudBackendTransportSessionRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "logon-session-1",
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://rest.sandbox.example/v1/sessions/logon-session-1/logon?async=false",
            handler.LastRequest.RequestUri!.AbsoluteUri);
        var requestJson = handler.LastRequestBody!;
        using var requestBody = JsonDocument.Parse(requestJson);
        var request = requestBody.RootElement.GetProperty("Request");
        Assert.Equal("00", request.GetProperty("Merchant").GetString());
        Assert.Equal(" ", request.GetProperty("LogonType").GetString());
        Assert.Equal("00", request.GetProperty("Application").GetString());
        Assert.Equal("0", request.GetProperty("ReceiptAutoPrint").GetString());
        Assert.Equal("0", request.GetProperty("CutReceipt").GetString());
        Assert.False(request.TryGetProperty("TxnRef", out _));

        using var requestLog = FindLinklyLog(logger.Lines, "logon-test", "request");
        var requestDetails = requestLog.RootElement.GetProperty("details");
        Assert.Equal("POST", requestDetails.GetProperty("method").GetString());
        Assert.Equal("https://rest.sandbox.example/v1/sessions/logon-session-1/logon?async=false", requestDetails.GetProperty("url").GetString());
        Assert.Equal("logon-session-1", requestDetails.GetProperty("transactionReference").GetString());
        Assert.Equal(JsonValueKind.Null, requestDetails.GetProperty("txnRef").ValueKind);
        Assert.False(requestDetails.GetProperty("txnRefSpecified").GetBoolean());
        Assert.Contains("\"LogonType\":\" \"", requestDetails.GetProperty("requestJson").GetString(), StringComparison.Ordinal);

        using var responseLog = FindLinklyLog(logger.Lines, "logon-test", "response");
        var responseDetails = responseLog.RootElement.GetProperty("details");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal("00", responseDetails.GetProperty("responseCode").GetString());
        Assert.Equal("APPROVED", responseDetails.GetProperty("responseText").GetString());
    }

    [Fact]
    public async Task Transport_sendkey_posts_cancel_key_synchronously()
    {
        var logger = new RecordingLogger<HttpLinklyCloudBackendAsyncTransport>();
        var handler = new LinklyTransportHttpMessageHandler(
            """
            {
              "SessionId": "sendkey-session-1",
              "ResponseType": "sendkey",
              "Response": null
            }
            """);
        var transport = new HttpLinklyCloudBackendAsyncTransport(new HttpClient(handler), logger);

        await transport.SendKeyAsync(
            new LinklyCloudBackendTransportSendKeyRequest(
                "Sandbox",
                "https://rest.sandbox.example/v1/",
                "access-token",
                "sendkey-session-1",
                "0",
                null,
                "S01",
                "POS-01"),
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://rest.sandbox.example/v1/sessions/sendkey-session-1/sendkey?async=false",
            handler.LastRequest.RequestUri!.AbsoluteUri);
        using var requestBody = JsonDocument.Parse(handler.LastRequestBody!);
        var request = requestBody.RootElement.GetProperty("Request");
        Assert.Equal("0", request.GetProperty("Key").GetString());
        Assert.False(request.TryGetProperty("Data", out _));

        using var requestLog = FindLinklyLog(logger.Lines, "sendkey", "request");
        var requestDetails = requestLog.RootElement.GetProperty("details");
        Assert.Equal("https://rest.sandbox.example/v1/sessions/sendkey-session-1/sendkey?async=false", requestDetails.GetProperty("url").GetString());
        Assert.Contains("\"Key\":\"0\"", requestDetails.GetProperty("requestJson").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenProvider_returns_clear_error_when_store_credential_is_missing()
    {
        var provider = new HttpLinklyCloudBackendTokenProvider(
            new CapturingCredentialRepository(null),
            new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            }),
            new HttpClient(new CapturingTokenHttpMessageHandler()),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
            }));

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None));

        Assert.Equal("Linkly Cloud credential is not configured for this store and environment.", exception.Message);
    }

    [Fact]
    public async Task GetHealthAsync_reports_ready_when_store_terminal_bearer_and_public_callback_are_configured()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            credentialRepository: credentialRepository,
            terminalCredentialRepository: terminalRepository);

        var response = await service.GetHealthAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.True(response.IsReady);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.All(response.Checks, check => Assert.True(check.IsReady));
        Assert.Contains(response.Checks, check => check.Code == "STORE_CREDENTIAL");
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_SECRET");
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_POS_ID");
        Assert.Contains(response.Checks, check => check.Code == "NOTIFICATION_BEARER");
        Assert.Contains(response.Checks, check => check.Code == "PUBLIC_CALLBACK_URL");
        Assert.Equal([("S01", "Sandbox")], credentialRepository.Calls);
        Assert.Equal([("Sandbox", "S01", "POS-01")], terminalRepository.Calls);
    }

    [Fact]
    public async Task RunStatusTestAsync_returns_failed_result_for_declined_linkly_response()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.OK,
            """
            {
              "Response": {
                "Success": false,
                "TxnRef": "LAST-TXN-1",
                "Date": "050626",
                "Time": "143000",
                "ResponseCode": "05",
                "ResponseText": "DECLINED"
              }
            }
            """);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);

        var response = await service.RunStatusTestAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.Equal(200, response.HttpStatus);
        Assert.Equal("05", response.ResponseCode);
        Assert.Equal("DECLINED", response.ResponseText);
        Assert.Equal("LAST-TXN-1", response.ResponseTxnRef);
        Assert.Equal("050626", response.ResponseDate);
        Assert.Equal("143000", response.ResponseTime);
        Assert.Equal("DECLINED", response.Message);
        Assert.Equal("Sandbox", transport.LastStatus?.Environment);
        Assert.Equal("server-token-POS-01", transport.LastStatus?.AccessToken);
        Assert.Equal(("Sandbox", "S01", "POS-01"), Assert.Single(tokenProvider.Calls));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"true\"", null)]
    public async Task Status_test_exposes_only_explicit_boolean_logged_on(string value, bool? expected)
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.OK,
            "{\"Response\":{\"Success\":true,\"ResponseCode\":\"T0\",\"LoggedOn\":" + value + "}}");
        var response = await CreateService(transport, new CapturingLinklyCloudBackendTokenProvider())
            .RunStatusTestAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.True(json.RootElement.TryGetProperty("loggedOn", out var actual));
        Assert.Equal(expected, actual.ValueKind == JsonValueKind.Null ? (bool?)null : actual.GetBoolean());
    }

    [Fact]
    public async Task RunStatusTestAsync_returns_success_for_terminal_status_ok_response()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.OK,
            """
            {
              "Response": {
                "Success": true,
                "TxnRef": "LAST-TXN-1",
                "Date": "050626",
                "Time": "143000",
                "ResponseCode": "T0",
                "ResponseText": "APPROVED"
              }
            }
            """);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);

        var response = await service.RunStatusTestAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal("T0", response.ResponseCode);
        Assert.Equal("APPROVED", response.ResponseText);
        Assert.Equal("ANZ Linkly Cloud transaction status test succeeded.", response.Message);
        Assert.Equal(200, response.HttpStatus);
        Assert.Equal(("Sandbox", "S01", "POS-01"), Assert.Single(tokenProvider.Calls));
    }

    [Fact]
    public async Task RunStatusTestAsync_falls_back_to_latest_resumable_txn_ref_when_status_response_omits_txn_ref()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(
            HttpStatusCode.OK,
            """
            {
              "Response": {
                "Success": true,
                "ResponseCode": "T0",
                "ResponseText": "APPROVED"
              }
            }
            """);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = "last-success",
            Status = "Completed",
            TxnRef = "LAST-TXN-44",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.RunStatusTestAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal("LAST-TXN-44", response.ResponseTxnRef);
        Assert.Equal(("Sandbox", "S01", "POS-01"), Assert.Single(tokenProvider.Calls));
    }

    [Fact]
    public void StatusTestResponse_serializes_txn_ref_alias_with_response_txn_ref()
    {
        var response = new LinklyCloudBackendStatusTestResponse(
            "Sandbox",
            "S01",
            "POS-01",
            "status-session",
            DateTimeOffset.Parse("2026-06-29T00:00:00+00:00"),
            200,
            true,
            "T0",
            "APPROVED",
            "LAST-TXN-44",
            null,
            null,
            "ANZ Linkly Cloud transaction status test succeeded.");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var root = document.RootElement;
        Assert.Equal("LAST-TXN-44", root.GetProperty("responseTxnRef").GetString());
        Assert.Equal("LAST-TXN-44", root.GetProperty("txnRef").GetString());
    }

    [Fact]
    public async Task GetHealthAsync_reports_not_ready_without_calling_active_session_when_configuration_is_incomplete()
    {
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            publicNotificationBaseUrl: "https://localhost/callback/",
            credentialRepository: new CapturingCredentialRepository(null),
            terminalCredentialRepository: new CapturingTerminalCredentialRepository());

        var response = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.False(response.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "STORE_CREDENTIAL" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_SECRET" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_POS_ID" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "PUBLIC_CALLBACK_URL" && !check.IsReady);
    }

    [Fact]
    public async Task UpsertTerminalCredentialAsync_updates_same_claim_scope_and_health_becomes_ready()
    {
        var credentialRepository = new CapturingCredentialRepository(null);
        var terminalRepository = new CapturingTerminalCredentialRepository();
        var credentialService = new LinklyCloudCredentialService(credentialRepository);
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            credentialRepository: credentialRepository,
            terminalCredentialRepository: terminalRepository);

        var before = await service.GetHealthAsync(" S01 ", " POS-01 ", "Sandbox", CancellationToken.None);
        Assert.False(before.IsReady);

        await credentialService.UpsertAsync(
            " S01 ",
            new LinklyCloudCredentialUpsertRequest(" sandbox ", " merchant-user ", " merchant-password "),
            "device:POS-01",
            CancellationToken.None);
        var response = await service.UpsertTerminalCredentialAsync(
            " S01 ",
            " POS-01 ",
            new LinklyCloudBackendTerminalCredentialUpsertRequest(
                " sandbox ",
                " secret-pos-01 ",
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            "device:POS-01",
            CancellationToken.None);
        var updated = await service.UpsertTerminalCredentialAsync(
            "S01",
            "POS-01",
            new LinklyCloudBackendTerminalCredentialUpsertRequest(
                "Sandbox",
                "secret-pos-01-updated",
                "bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"),
            "device:POS-01",
            CancellationToken.None);
        var health = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.True(response.HasSecret);
        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", response.PosId);
        Assert.True(updated.HasSecret);
        Assert.Equal("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb", updated.PosId);
        Assert.True(health.IsReady);
        Assert.All(health.Checks, check => Assert.True(check.IsReady));
        Assert.Single(terminalRepository.Credentials);
        Assert.Equal("secret-pos-01-updated", terminalRepository.Credentials.Single().Secret);
    }

    [Fact]
    public async Task TerminalCredentialAndHealth_write_backend_pairing_json_without_secret_plaintext()
    {
        var logger = new RecordingLogger<LinklyCloudBackendAsyncService>();
        var terminalRepository = new CapturingTerminalCredentialRepository();
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            terminalCredentialRepository: terminalRepository,
            logger: logger);

        await service.UpsertTerminalCredentialAsync(
            " S01 ",
            " POS-01 ",
            new LinklyCloudBackendTerminalCredentialUpsertRequest(
                " sandbox ",
                "secret-pos-01",
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            "device:POS-01",
            CancellationToken.None);
        await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        using var requestLog = FindLinklyLog(logger.Lines, "terminal-credential", "request");
        Assert.Equal("api-backend-service", requestLog.RootElement.GetProperty("source").GetString());
        Assert.Equal("Sandbox", requestLog.RootElement.GetProperty("environment").GetString());
        Assert.Equal("S01", requestLog.RootElement.GetProperty("details").GetProperty("storeCode").GetString());
        Assert.Equal("POS-01", requestLog.RootElement.GetProperty("details").GetProperty("deviceCode").GetString());
        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", requestLog.RootElement.GetProperty("request").GetProperty("posId").GetString());
        var secretMetadata = requestLog.RootElement.GetProperty("request").GetProperty("secret");
        Assert.Equal(13, secretMetadata.GetProperty("secretLength").GetInt32());
        Assert.Equal(
            new[] { "hasSecret", "secretLength" },
            secretMetadata.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.False(secretMetadata.TryGetProperty("secretPreview", out _));
        Assert.DoesNotContain("secret-pos-01", logger.Lines, StringComparer.Ordinal);

        using var successLog = FindLinklyLog(logger.Lines, "terminal-credential", "succeeded");
        Assert.True(successLog.RootElement.GetProperty("success").GetBoolean());
        Assert.True(successLog.RootElement.GetProperty("response").GetProperty("hasSecret").GetBoolean());

        using var healthLog = FindLinklyLog(logger.Lines, "backend-health", "response");
        Assert.True(healthLog.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(healthLog.RootElement.GetProperty("response").GetProperty("failedChecks").EnumerateArray());
    }

    [Theory]
    [InlineData("", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "secret is required.")]
    [InlineData("secret-pos-01", "", "posId is required.")]
    [InlineData("secret-pos-01", "11111111-1111-1111-1111-111111111111", "posId must be UUID v4.")]
    public async Task UpsertTerminalCredentialAsync_rejects_invalid_input_without_exposing_secret(
        string secret,
        string posId,
        string expectedMessage)
    {
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            terminalCredentialRepository: new CapturingTerminalCredentialRepository());

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.UpsertTerminalCredentialAsync(
                "S01",
                "POS-01",
                new LinklyCloudBackendTerminalCredentialUpsertRequest("Sandbox", secret, posId),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain("secret-pos-01", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationAsync_validates_environment_bearer_and_keeps_status_scoped()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        using var unknownDisplay = JsonDocument.Parse("""{ "Response": { "DisplayText": ["IGNORED"] } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            "unknown-session",
            "display",
            "Bearer sandbox-notify",
            unknownDisplay.RootElement,
            CancellationToken.None);
        Assert.Null(await service.GetStatusAsync("S01", "POS-01", "Sandbox", "unknown-session", CancellationToken.None));

        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var display = JsonDocument.Parse("""{ "sessionId": "ignored", "responseType": "display", "Response": { "DisplayText": ["PRESENT CARD"] } }""");
        using var receipt = JsonDocument.Parse("""{ "Response": { "ReceiptText": ["MERCHANT RECEIPT", "APPROVED"] } }""");

        await Assert.ThrowsAsync<LinklyCloudBackendNotificationUnauthorizedException>(() =>
            service.ReceiveNotificationAsync(
                "Sandbox",
                session.SessionId,
                "display",
                "Bearer wrong",
                display.RootElement,
                CancellationToken.None));

        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            display.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            display.RootElement,
            CancellationToken.None);

        using var approved = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-APPROVED", "ResponseCode": "00", "ResponseText": "APPROVED" } }""");
        using var staleDeclined = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-DECLINED", "ResponseCode": "05", "ResponseText": "DECLINED" } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            staleDeclined.RootElement,
            CancellationToken.None);

        var scopedStatus = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            session.SessionId,
            CancellationToken.None);
        Assert.NotNull(scopedStatus);
        Assert.Equal("Completed", scopedStatus!.Status);
        Assert.Equal(session.TxnRef, scopedStatus.TxnRef);
        Assert.Equal("00", scopedStatus.ResponseCode);
        Assert.Equal("PRESENT CARD", scopedStatus.DisplayText);
        Assert.Contains("MERCHANT RECEIPT", scopedStatus.ReceiptText, StringComparison.Ordinal);
        // 新证据会话只向客户端暴露通过完整请求证据校验的 transaction 通知，避免客户端回退历史通知误判成功。
        Assert.Equal(2, scopedStatus.Notifications.Count);
        Assert.Equal("display", scopedStatus.Notifications[0].Type);
        Assert.Null(scopedStatus.ReceiptPrintedAt);
        Assert.Null(await service.GetStatusAsync("S01", "POS-02", "Sandbox", session.SessionId, CancellationToken.None));
        Assert.Null(await service.GetStatusAsync("S01", "POS-01", "Production", session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task NotificationAsync_fills_missing_transaction_success_for_completed_session_without_changing_txn_ref()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var sessionId = Guid.NewGuid().ToString("D");
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Completed",
            TxnRef = "TXN-LOCAL",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            TransactionSuccess = null,
            IsActive = false,
            LastHttpStatus = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        using var payload = JsonDocument.Parse("""{ "Response": { "Success": true, "TxnRef": "TXN-OFFICIAL", "ResponseCode": "00", "ResponseText": "APPROVED" } }""");

        await service.ReceiveNotificationAsync(
            "Sandbox",
            sessionId,
            "transaction",
            "Bearer sandbox-notify",
            payload.RootElement,
            CancellationToken.None);

        var status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.True(status!.TransactionSuccess);
        Assert.Equal("TXN-LOCAL", status.TxnRef);
        Assert.Equal("00", status.ResponseCode);
    }

    [Fact]
    public async Task NotificationAsync_does_not_fill_missing_transaction_success_from_sparse_conflicting_payload()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var sessionId = Guid.NewGuid().ToString("D");
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Completed",
            TxnRef = "TXN-LOCAL",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            TransactionSuccess = null,
            IsActive = false,
            LastHttpStatus = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        using var payload = JsonDocument.Parse("""{ "Response": { "Success": false } }""");

        await service.ReceiveNotificationAsync(
            "Sandbox",
            sessionId,
            "transaction",
            "Bearer sandbox-notify",
            payload.RootElement,
            CancellationToken.None);

        var status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", sessionId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Null(status!.TransactionSuccess);
        Assert.Equal("00", status.ResponseCode);
    }

    [Fact]
    public async Task Display_notifications_persist_metadata_for_status_and_active_and_replace_current_prompt()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var firstDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["PRESENT CARD"],
                "DisplayLines": ["PRESENT CARD", "TOTAL $10.00"],
                "CancelKeyFlag": "0",
                "OKKeyFlag": "1",
                "AcceptYesKeyFlag": "1",
                "DeclineNoKeyFlag": "0",
                "AuthoriseKeyFlag": "3",
                "InputType": "2",
                "GraphicCode": "7"
              }
            }
            """);

        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            firstDisplay.RootElement,
            CancellationToken.None);

        var status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        var active = await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(status);
        Assert.NotNull(active);
        Assert.Equal("PRESENT CARD", status!.DisplayText);
        Assert.False(status.CancelKeyFlag);
        Assert.True(status.OKKeyFlag);
        Assert.True(status.AcceptYesKeyFlag);
        Assert.False(status.DeclineNoKeyFlag);
        Assert.True(status.AuthoriseKeyFlag);
        Assert.Equal("2", status.InputType);
        Assert.Equal("7", status.GraphicCode);
        Assert.Equal(["PRESENT CARD", "TOTAL $10.00"], status.DisplayLines);
        Assert.Equal(status.DisplayLines, active!.DisplayLines);
        Assert.Equal(status.OKKeyFlag, active.OKKeyFlag);

        using var nextDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["ENTER PIN"],
                "DisplayLines": ["ENTER PIN"],
                "DeclineNoKeyFlag": "1",
                "InputType": "1"
              }
            }
            """);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            nextDisplay.RootElement,
            CancellationToken.None);

        status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("ENTER PIN", status!.DisplayText);
        Assert.Equal(["ENTER PIN"], status.DisplayLines);
        Assert.False(status.CancelKeyFlag);
        Assert.False(status.OKKeyFlag);
        Assert.False(status.AcceptYesKeyFlag);
        Assert.True(status.DeclineNoKeyFlag);
        Assert.False(status.AuthoriseKeyFlag);
        Assert.Equal("1", status.InputType);
        Assert.Null(status.GraphicCode);

        using var cancelDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["CANCEL AVAILABLE"],
                "CancelKeyFlag": "1"
              }
            }
            """);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            cancelDisplay.RootElement,
            CancellationToken.None);

        status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("CANCEL AVAILABLE", status!.DisplayText);
        Assert.True(status.CancelKeyFlag);
        Assert.False(status.OKKeyFlag);
        Assert.False(status.AcceptYesKeyFlag);
        Assert.False(status.DeclineNoKeyFlag);
        Assert.False(status.AuthoriseKeyFlag);

        using var approved = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-APPROVED", "ResponseCode": "00", "ResponseText": "APPROVED" } }""");
        using var staleDisplay = JsonDocument.Parse("""{ "Response": { "DisplayText": ["OLD PROMPT"], "OKKeyFlag": "1" } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            staleDisplay.RootElement,
            CancellationToken.None);

        status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        Assert.Equal("Completed", status?.Status);
        Assert.Equal("00", status?.ResponseCode);
        var blockingSession = await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        Assert.NotNull(blockingSession);
        Assert.Equal(session.SessionId, blockingSession!.SessionId);
        Assert.Equal("Completed", blockingSession.Status);
    }

    [Fact]
    public async Task MarkReceiptPrintedAsync_sets_printed_time_after_receipt_arrives()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var receipt = JsonDocument.Parse("""{ "Response": { "ReceiptText": ["MERCHANT RECEIPT", "APPROVED"] } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);

        var beforeMark = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            session.SessionId,
            CancellationToken.None);
        Assert.NotNull(beforeMark);
        Assert.Contains("MERCHANT RECEIPT", beforeMark!.ReceiptText, StringComparison.Ordinal);
        Assert.Null(beforeMark.ReceiptPrintedAt);

        var afterMark = await service.MarkReceiptPrintedAsync(
            "S01",
            "POS-01",
            session.SessionId,
            new LinklyCloudBackendMarkReceiptPrintedRequest("Sandbox"),
            CancellationToken.None);

        Assert.NotNull(afterMark.ReceiptPrintedAt);
        Assert.Contains("MERCHANT RECEIPT", afterMark.ReceiptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkReceiptPrintedAsync_preserves_signature_confirmation_display_after_signature_receipt_prints()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var processingDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["PROCESSING", "PLEASE WAIT"],
                "CancelKeyFlag": false,
                "AcceptYesKeyFlag": false,
                "DeclineNoKeyFlag": false,
                "AuthoriseKeyFlag": false,
                "OKKeyFlag": false,
                "InputType": "0",
                "GraphicCode": "0"
              }
            }
            """);
        using var signatureReceipt = JsonDocument.Parse("""
            {
              "Response": {
                "ReceiptText": [
                  "LINE2",
                  "CREDIT ACCOUNT",
                  "PURCHASE AUD $10.08",
                  "APPROVE WITH SIG - 08",
                  "PLEASE SIGN:"
                ]
              }
            }
            """);
        using var signatureDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["SIGNATURE OK?"],
                "CancelKeyFlag": false,
                "AcceptYesKeyFlag": true,
                "DeclineNoKeyFlag": true,
                "AuthoriseKeyFlag": false,
                "OKKeyFlag": false,
                "InputType": "0",
                "GraphicCode": "1"
              }
            }
            """);

        await service.ReceiveNotificationAsync("Sandbox", session.SessionId, "display", "Bearer sandbox-notify", processingDisplay.RootElement, CancellationToken.None);
        await service.ReceiveNotificationAsync("Sandbox", session.SessionId, "receipt", "Bearer sandbox-notify", signatureReceipt.RootElement, CancellationToken.None);
        await service.ReceiveNotificationAsync("Sandbox", session.SessionId, "display", "Bearer sandbox-notify", signatureDisplay.RootElement, CancellationToken.None);

        var afterMark = await service.MarkReceiptPrintedAsync(
            "S01",
            "POS-01",
            session.SessionId,
            new LinklyCloudBackendMarkReceiptPrintedRequest("Sandbox"),
            CancellationToken.None);

        Assert.NotNull(afterMark.ReceiptPrintedAt);
        Assert.Equal("SIGNATURE OK?", afterMark.DisplayText);
        Assert.True(afterMark.AcceptYesKeyFlag);
        Assert.True(afterMark.DeclineNoKeyFlag);
        Assert.False(afterMark.CancelKeyFlag);
        Assert.Contains("APPROVE WITH SIG - 08", afterMark.ReceiptText, StringComparison.Ordinal);
    }

    private static LinklyCloudBackendTransactionRequest CreateRefundRequest(string? rfn, long amount = 250)
    {
        return new LinklyCloudBackendTransactionRequest(
            "Sandbox",
            "R",
            amount,
            rfn is null ? null : new Dictionary<string, string> { ["RFN"] = rfn });
    }

    private static async Task SeedPurchaseAsync(
        ILinklyCloudBackendAsyncRepository repository,
        string txnRef,
        long amountCents,
        string status = "Completed",
        bool? success = true,
        string storeCode = "S01")
    {
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = storeCode,
            DeviceCode = "POS-01",
            SessionId = Guid.NewGuid().ToString("D"),
            Status = status,
            TxnRef = txnRef,
            RequestTxnType = "P",
            RequestAmountCents = amountCents,
            RequestRfn = txnRef,
            TransactionSuccess = success,
            ResponseCode = "00",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    private static async Task SeedRefundAsync(
        ILinklyCloudBackendAsyncRepository repository,
        string rfn,
        long amountCents,
        string status,
        bool? success,
        bool isActive = false)
    {
        await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = Guid.NewGuid().ToString("D"),
            Status = status,
            TxnRef = "R" + Guid.NewGuid().ToString("N")[..15].ToUpperInvariant(),
            RequestTxnType = "R",
            RequestAmountCents = amountCents,
            RequestRfn = rfn,
            TransactionSuccess = success,
            ResponseCode = success == true ? "00" : null,
            IsActive = isActive,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, "PMND6WYVF2GBVY6G", true)]
    [InlineData(true, "PMND6WYVF2GBVY6G", true)]
    [InlineData(true, "SOMEONE-ELSES-RFN", false)]
    public async Task Approved_refund_callback_is_verified_without_requiring_an_echoed_rfn(
        bool echoRfn,
        string echoedRfn,
        bool expectEvidence)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateRefundRequest("PMND6WYVF2GBVY6G"), CancellationToken.None);
        // 生产通知的 PurchaseAnalysisData 不含 RFN：退款证据靠发送前持久化的 RequestRfn 加 TxnType/金额/TxnRef/响应码核验。
        object purchaseAnalysisData = echoRfn ? new { RFN = echoedRfn } : new { };
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = started.TxnRef,
                TxnType = "R",
                AmtPurchase = 250,
                ResponseCode = "00",
                ResponseText = "APPROVED",
                PurchaseAnalysisData = purchaseAnalysisData
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        if (!expectEvidence)
        {
            // 回显了 RFN 就必须与持久化值一致，否则仍判为无法核验。
            Assert.Null(recovered?.CardTransaction);
            Assert.DoesNotContain(recovered?.Notifications ?? [], item =>
                string.Equals(item.Type, "transaction", StringComparison.OrdinalIgnoreCase));
            return;
        }

        Assert.NotNull(recovered?.CardTransaction);
        Assert.Equal(started.TxnRef, recovered!.CardTransaction!.TxnRef);
        Assert.Equal("PMND6WYVF2GBVY6G", recovered.CardTransaction.Rfn);
        Assert.Equal(250, recovered.CardTransaction.AmountCents);
        Assert.Contains(recovered.Notifications ?? [], item =>
            string.Equals(item.Type, "transaction", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("txn-type")]
    [InlineData("amount")]
    [InlineData("txn-ref")]
    public async Task Refund_callback_without_rfn_still_rejects_other_mismatched_evidence(string mismatch)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateRefundRequest("PMND6WYVF2GBVY6G"), CancellationToken.None);
        using var notification = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Response = new
            {
                Success = true,
                TxnRef = mismatch == "txn-ref" ? "260101010101FFFF" : started.TxnRef,
                TxnType = mismatch == "txn-type" ? "P" : "R",
                AmtPurchase = mismatch == "amount" ? 251 : 250,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            }
        }));

        await service.ReceiveNotificationAsync(
            "Sandbox", started.SessionId, "transaction", "Bearer sandbox-notify",
            notification.RootElement, CancellationToken.None);
        var recovered = await service.GetStatusAsync(
            "S01", "POS-01", "Sandbox", started.SessionId, CancellationToken.None);

        Assert.Null(recovered?.CardTransaction);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("C")]
    [InlineData("p r")]
    public async Task StartTransactionAsync_rejects_txn_types_other_than_purchase_and_refund(string txnType)
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest() with { TxnType = txnType },
                CancellationToken.None));

        Assert.Null(transport.LastTransaction);
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task StartTransactionAsync_normalizes_lowercase_refund_type()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        await service.StartTransactionAsync(
            "S01", "POS-01", CreateRefundRequest("ORIGINAL-RFN") with { TxnType = " r " }, CancellationToken.None);

        Assert.Equal("R", transport.LastTransaction?.TxnType);
    }

    [Theory]
    [InlineData(null, 250L)]
    [InlineData("RFN WITH SPACE", 250L)]
    [InlineData("ORIGINAL-RFN", 0L)]
    [InlineData("ORIGINAL-RFN", 1_000_000_000L)]
    [InlineData("ORIGINAL-RFN", long.MinValue)]
    public async Task Refund_requires_a_safe_rfn_and_a_bounded_non_zero_amount(string? rfn, long amount)
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01", "POS-01", CreateRefundRequest(rfn, amount), CancellationToken.None));

        Assert.Null(transport.LastTransaction);
        Assert.Null(await service.GetResumableSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Theory]
    [InlineData("Completed", false)]
    [InlineData("Pending", null)]
    [InlineData("Failed", false)]
    public async Task Refund_is_rejected_when_the_original_purchase_was_not_approved(string status, bool? success)
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await SeedPurchaseAsync(repository, "2610090900000001", 1000, status, success);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, repository: repository);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01", "POS-01", CreateRefundRequest("2610090900000001", 500), CancellationToken.None));

        Assert.Null(transport.LastTransaction);
    }

    [Fact]
    public async Task Refund_is_rejected_when_the_rfn_points_at_another_refund()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await SeedPurchaseAsync(repository, "2610090900000001", 1000);
        await SeedRefundAsync(repository, "2610090900000001", 100, "Completed", true);
        var refundTxnRef = (await repository.GetRefundSessionsByReferenceAsync(
            "Sandbox", "S01", "2610090900000001", CancellationToken.None)).Single().TxnRef!;
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, repository: repository);

        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01", "POS-01", CreateRefundRequest(refundTxnRef, 50), CancellationToken.None));

        Assert.Null(transport.LastTransaction);
    }

    [Fact]
    public async Task Refund_cannot_exceed_original_amount_across_the_same_rfn()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await SeedPurchaseAsync(repository, "2610090900000001", 1000);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, repository: repository);

        // 单笔超额直接拒绝。
        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01", "POS-01", CreateRefundRequest("2610090900000001", 1001), CancellationToken.None));
        Assert.Null(transport.LastTransaction);

        // 累计额度：已批准的 600 + 在途的 300 占用额度，只剩 100。
        await SeedRefundAsync(repository, "2610090900000001", 600, "Completed", true);
        await SeedRefundAsync(repository, "2610090900000001", 300, "Pending", null, isActive: true);
        await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01", "POS-01", CreateRefundRequest("2610090900000001", 101), CancellationToken.None));
        Assert.Null(transport.LastTransaction);
    }

    [Fact]
    public async Task Refund_within_remaining_capacity_is_allowed_and_failed_refunds_do_not_consume_it()
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        await SeedPurchaseAsync(repository, "2610090900000001", 1000);
        await SeedRefundAsync(repository, "2610090900000001", 600, "Completed", true);
        // 失败、未提交、取消、主管结案的退款不占额度。
        await SeedRefundAsync(repository, "2610090900000001", 900, "Completed", false);
        await SeedRefundAsync(repository, "2610090900000001", 900, "Failed", null);
        await SeedRefundAsync(repository, "2610090900000001", 900, "NotSubmitted", null);
        await SeedRefundAsync(repository, "2610090900000001", 900, "Cancelled", null);
        await SeedRefundAsync(repository, "2610090900000001", 900, "SupervisorResolved", null);
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, repository: repository);

        var started = await service.StartTransactionAsync(
            "S01", "POS-01", CreateRefundRequest("2610090900000001", -400), CancellationToken.None);

        Assert.NotNull(transport.LastTransaction);
        Assert.Equal(started.SessionId, transport.LastTransaction!.SessionId);
    }

    [Fact]
    public async Task Refund_of_an_original_purchase_not_in_cloud_backend_sessions_is_not_blocked()
    {
        // 直连模式或迁移前的历史销售查不到会话：保持可退，避免历史小票无法原路退款（缺口写在 PR 描述里）。
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);

        await service.StartTransactionAsync(
            "S01", "POS-01", CreateRefundRequest("LEGACY-DIRECT-RFN", 777), CancellationToken.None);

        Assert.NotNull(transport.LastTransaction);
    }

    private static TestableLinklyCloudBackendAsyncService CreateService(
        ILinklyCloudBackendAsyncTransport transport,
        ILinklyCloudBackendTokenProvider? tokenProvider = null,
        string? publicNotificationBaseUrl = "https://public.example/callback/",
        ILinklyCloudBackendAsyncRepository? repository = null,
        ILinklyCloudCredentialRepository? credentialRepository = null,
        ILinklyCloudBackendTerminalCredentialRepository? terminalCredentialRepository = null,
        string? sandboxNotificationBearer = "sandbox-notify",
        ILogger<LinklyCloudBackendAsyncService>? logger = null,
        ILinklyCloudTerminalService? terminalService = null)
    {
        repository ??= new InMemoryLinklyCloudBackendAsyncRepository();
        credentialRepository ??= new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        terminalCredentialRepository ??= new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        return new TestableLinklyCloudBackendAsyncService(
            repository,
            transport,
            tokenProvider ?? new CapturingLinklyCloudBackendTokenProvider(),
            credentialRepository,
            terminalCredentialRepository,
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                ProductionNotificationBearer = "production-notify",
                SandboxNotificationBearer = sandboxNotificationBearer,
                PublicNotificationBaseUrl = publicNotificationBaseUrl
            }),
            logger,
            terminalService);
    }

    private static LinklyCloudBackendTransactionRequest CreateTransactionRequest()
    {
        return new LinklyCloudBackendTransactionRequest(
            "Sandbox",
            "P",
            1000,
            null);
    }

    private static Task SeedPendingSessionAsync(
        ILinklyCloudBackendAsyncRepository repository,
        string sessionId)
    {
        return repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-SENDKEY",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    private sealed class TestableLinklyCloudBackendAsyncService(
        ILinklyCloudBackendAsyncRepository repository,
        ILinklyCloudBackendAsyncTransport transport,
        ILinklyCloudBackendTokenProvider tokenProvider,
        ILinklyCloudCredentialRepository credentialRepository,
        ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
        IOptions<LinklyCloudBackendAsyncOptions> options,
        ILogger<LinklyCloudBackendAsyncService>? logger = null,
        ILinklyCloudTerminalService? terminalService = null)
        : LinklyCloudBackendAsyncService(repository, transport, tokenProvider, credentialRepository, terminalCredentialRepository, options, logger, terminalService)
    {
        public ILinklyCloudBackendAsyncRepository Repository { get; } = repository;
    }

    private sealed class CapturingLinklyCloudBackendAsyncTransport(
        HttpStatusCode responseStatusCode,
        string? responseBody = null,
        HttpStatusCode? getTransactionStatusCode = null,
        string? getTransactionBody = null,
        Exception? terminalTestException = null) : ILinklyCloudBackendAsyncTransport
    {
        public string? GetTransactionBody { get; set; } = getTransactionBody;

        public LinklyCloudBackendNotificationRequest? LastNotification { get; private set; }

        public LinklyCloudBackendTransportTransactionRequest? LastTransaction { get; private set; }

        public LinklyCloudBackendTransportSettlementRequest? LastSettlement { get; private set; }

        public LinklyCloudBackendTransportSessionRequest? LastRecover { get; private set; }

        public LinklyCloudBackendTransportSessionRequest? LastLogon { get; private set; }

        public LinklyCloudBackendTransportStatusRequest? LastStatus { get; private set; }

        public LinklyCloudBackendTransportSendKeyRequest? LastSendKey { get; private set; }

        public LinklyCloudBackendTransportSessionRequest? LastGetTransaction { get; private set; }

        public Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
            LinklyCloudBackendTransportTransactionRequest request,
            CancellationToken cancellationToken)
        {
            LastTransaction = request;
            LastNotification = request.Notification;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> StartSettlementAsync(
            LinklyCloudBackendTransportSettlementRequest request,
            CancellationToken cancellationToken)
        {
            LastSettlement = request;
            LastNotification = request.Notification;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            LastRecover = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> GetTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            LastGetTransaction = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(
                getTransactionStatusCode ?? responseStatusCode,
                GetTransactionBody ?? responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            if (terminalTestException is not null)
            {
                throw terminalTestException;
            }

            LastLogon = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
            LinklyCloudBackendTransportStatusRequest request,
            CancellationToken cancellationToken)
        {
            if (terminalTestException is not null)
            {
                throw terminalTestException;
            }

            LastStatus = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }

        public Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
            LinklyCloudBackendTransportSendKeyRequest request,
            CancellationToken cancellationToken)
        {
            LastSendKey = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, responseBody));
        }
    }

    private sealed class CompletingBeforeResponseTransport(
        ILinklyCloudBackendAsyncRepository repository,
        HttpStatusCode responseStatusCode) : ILinklyCloudBackendAsyncTransport
    {
        public async Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
            LinklyCloudBackendTransportTransactionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> GetTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
            LinklyCloudBackendTransportStatusRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
            LinklyCloudBackendTransportSendKeyRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        private async Task CompleteAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            var current = await repository.GetSessionAsync(environment, "S01", "POS-01", sessionId, cancellationToken);
            Assert.NotNull(current);
            current!.Status = "Completed";
            current.ResponseCode = "00";
            current.ResponseText = "APPROVED";
            current.RecoveryAction = null;
            current.IsActive = false;
            current.LastHttpStatus = 200;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await repository.UpsertSessionAsync(current, cancellationToken);
        }
    }

    private sealed class TxnRefCollisionOnceRepository : ILinklyCloudBackendAsyncRepository
    {
        private readonly InMemoryLinklyCloudBackendAsyncRepository _inner = new();
        private bool _collided;

        public int TryCreateCallCount { get; private set; }

        public Task<bool> TryCreateSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken)
        {
            TryCreateCallCount++;
            if (!_collided)
            {
                _collided = true;
                return Task.FromResult(false);
            }

            return _inner.TryCreateSessionAsync(session, cancellationToken);
        }

        public Task UpsertSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken)
        {
            return _inner.UpsertSessionAsync(session, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetSessionByEnvironmentSessionIdAsync(environment, sessionId, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            return _inner.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            return _inner.GetResumableSessionAsync(environment, storeCode, deviceCode, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            DateTimeOffset acknowledgedAt,
            bool supervisorResolved,
            CancellationToken cancellationToken)
        {
            return _inner.AcknowledgeSessionAsync(environment, storeCode, deviceCode, sessionId, acknowledgedAt, supervisorResolved, cancellationToken);
        }

        public Task AddNotificationAsync(
            LinklyCloudBackendNotificationRecord notification,
            CancellationToken cancellationToken)
        {
            return _inner.AddNotificationAsync(notification, cancellationToken);
        }

        public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetNotificationsAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
        }
    }

    private sealed class CapturingLinklyCloudBackendTokenProvider : ILinklyCloudBackendTokenProvider
    {
        public List<(string Environment, string StoreCode, string DeviceCode)> Calls { get; } = [];

        public List<Guid> TerminalCalls { get; } = [];

        public Task<LinklyCloudBackendToken> GetTokenAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((environment, storeCode, deviceCode));
            return Task.FromResult(new LinklyCloudBackendToken(
                $"https://server-rest.example/{deviceCode}/",
                $"server-token-{deviceCode}"));
        }

        public Task<LinklyCloudBackendToken> GetTokenAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid? terminalId,
            CancellationToken cancellationToken)
        {
            if (terminalId.HasValue)
            {
                TerminalCalls.Add(terminalId.Value);
            }

            return GetTokenAsync(environment, storeCode, deviceCode, cancellationToken);
        }
    }

    private sealed class FixedLinklyCloudTerminalService(
        Guid terminalId,
        string displayName,
        long revision,
        ILinklyCloudBackendAsyncRepository? sessionRepository = null,
        TerminalOperationLeaseState? leaseState = null,
        bool failCredentialMaterialization = false) : ILinklyCloudTerminalService
    {
        private readonly ILinklyCloudBackendAsyncRepository _sessionRepository =
            sessionRepository ?? new InMemoryLinklyCloudBackendAsyncRepository();
        private readonly TerminalOperationLeaseState _leaseState = leaseState ?? new TerminalOperationLeaseState();
        private readonly LinklyCloudTerminalPaymentContext _context = new(
            new LinklyCloudTerminalRecord
            {
                TerminalId = terminalId,
                Environment = "Sandbox",
                StoreCode = "S01",
                LaneNo = 1,
                DisplayName = displayName,
                Username = "lane-user",
                Password = "lane-password",
                Secret = "terminal-secret",
                CredentialProtectionVersion =
                    BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection.CurrentVersion,
                PosId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PairingState = "Ready",
                UpdatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
            },
            new LinklyCloudDeviceSelectionRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                TerminalId = terminalId,
                Revision = revision
            });

        public List<HealthWriteAttempt> HealthWriteAttempts { get; } = [];

        public bool RecordHealthResult { get; init; } = true;

        public Task<LinklyCloudTerminalPaymentContext?> ResolvePaymentTerminalAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid? requestedTerminalId,
            long? selectionRevision,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<LinklyCloudTerminalPaymentContext?>(_context);
        }

        public Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
            string storeCode, string deviceCode, string environment, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetConfigurationModeAsync(
            string environment, string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult("Active");

        public Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
            string storeCode, string deviceCode, LinklyCloudTerminalSelectionRequest request, string? updatedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
            string storeCode, string deviceCode, Guid requestedTerminalId, LinklyCloudBackendPairRequest request, string? updatedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LinklyCloudTerminalRecord?> GetTerminalAsync(
            string environment,
            string storeCode,
            Guid requestedTerminalId,
            CancellationToken cancellationToken)
        {
            if (failCredentialMaterialization)
            {
                throw new LinklyCloudTerminalCredentialUnavailableException();
            }

            return Task.FromResult<LinklyCloudTerminalRecord?>(_context.Terminal with
            {
                PairingAttemptId = _leaseState.LeaseId,
                PairingLeaseExpiresAt = _leaseState.ExpiresAt
            });
        }

        public Task<string?> GetTerminalDisplayNameAsync(
            string environment,
            string storeCode,
            Guid requestedTerminalId,
            CancellationToken cancellationToken) => Task.FromResult<string?>(displayName);

        public async Task<LinklyCloudTerminalOperationLease> AcquireOperationLeaseAsync(
            string environment,
            string storeCode,
            string deviceCode,
            LinklyCloudTerminalPaymentContext terminalContext,
            CancellationToken cancellationToken)
        {
            var blockingSession = await _sessionRepository.GetBlockingSessionByTerminalAsync(
                environment,
                storeCode,
                terminalContext.Terminal.TerminalId,
                cancellationToken);
            if (blockingSession is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(blockingSession.SessionId);
            }

            lock (_leaseState.Gate)
            {
                var now = DateTime.UtcNow;
                if (_leaseState.LeaseId is not null && _leaseState.ExpiresAt > now)
                {
                    throw new LinklyCloudBackendActiveTransactionException(null);
                }

                _leaseState.LeaseId = Guid.NewGuid();
                _leaseState.ExpiresAt = now.AddMinutes(9);
                return new LinklyCloudTerminalOperationLease(
                    _leaseState.LeaseId.Value,
                    terminalContext.Terminal.TerminalId,
                    _leaseState.ExpiresAt.Value);
            }
        }

        public Task ReleaseOperationLeaseAsync(
            string environment,
            string storeCode,
            Guid requestedTerminalId,
            Guid leaseId,
            CancellationToken cancellationToken)
        {
            lock (_leaseState.Gate)
            {
                if (_leaseState.LeaseId == leaseId)
                {
                    _leaseState.LeaseId = null;
                    _leaseState.ExpiresAt = null;
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> RecordHealthAsync(
            LinklyCloudTerminalPaymentContext terminalContext,
            string healthStatus,
            DateTime checkedAt,
            CancellationToken cancellationToken)
        {
            HealthWriteAttempts.Add(new HealthWriteAttempt(
                terminalContext.Terminal.TerminalId,
                terminalContext.Terminal.UpdatedAt ?? throw new InvalidOperationException(),
                healthStatus,
                checkedAt));
            return Task.FromResult(RecordHealthResult);
        }
    }

    private sealed class TerminalOperationLeaseState
    {
        public object Gate { get; } = new();

        public Guid? LeaseId { get; set; }

        public DateTime? ExpiresAt { get; set; }
    }

    private sealed record HealthWriteAttempt(
        Guid TerminalId,
        DateTime ExpectedUpdatedAt,
        string Status,
        DateTime CheckedAt);

    private sealed class TerminalLeaseAwareBackendRepository(TerminalOperationLeaseState leaseState)
        : ILinklyCloudBackendAsyncRepository
    {
        private readonly InMemoryLinklyCloudBackendAsyncRepository _inner = new();

        public Task<bool> TryCreateSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken)
        {
            lock (leaseState.Gate)
            {
                if (leaseState.LeaseId is not null && leaseState.ExpiresAt > DateTime.UtcNow)
                {
                    return Task.FromResult(false);
                }
            }

            return _inner.TryCreateSessionAsync(session, cancellationToken);
        }

        public Task UpsertSessionAsync(LinklyCloudBackendSessionRecord session, CancellationToken cancellationToken) =>
            _inner.UpsertSessionAsync(session, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
            string environment, string storeCode, string deviceCode, string sessionId, CancellationToken cancellationToken) =>
            _inner.GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
            string environment, string sessionId, CancellationToken cancellationToken) =>
            _inner.GetSessionByEnvironmentSessionIdAsync(environment, sessionId, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
            string environment, string storeCode, string deviceCode, CancellationToken cancellationToken) =>
            _inner.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionByTerminalAsync(
            string environment, string storeCode, Guid terminalId, CancellationToken cancellationToken) =>
            _inner.GetActiveSessionByTerminalAsync(environment, storeCode, terminalId, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetBlockingSessionByTerminalAsync(
            string environment, string storeCode, Guid terminalId, CancellationToken cancellationToken) =>
            _inner.GetBlockingSessionByTerminalAsync(environment, storeCode, terminalId, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
            string environment, string storeCode, string deviceCode, CancellationToken cancellationToken) =>
            _inner.GetResumableSessionAsync(environment, storeCode, deviceCode, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            DateTimeOffset acknowledgedAt,
            bool supervisorResolved,
            CancellationToken cancellationToken) =>
            _inner.AcknowledgeSessionAsync(
                environment, storeCode, deviceCode, sessionId, acknowledgedAt, supervisorResolved, cancellationToken);

        public Task AddNotificationAsync(
            LinklyCloudBackendNotificationRecord notification,
            CancellationToken cancellationToken) =>
            _inner.AddNotificationAsync(notification, cancellationToken);

        public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken) =>
            _inner.GetNotificationsAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
    }

    private sealed class ThrowingLinklyCloudBackendTokenProvider(string message) : ILinklyCloudBackendTokenProvider
    {
        public Task<LinklyCloudBackendToken> GetTokenAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            throw new LinklyCloudBackendValidationException(message);
        }
    }

    private sealed class CapturingCredentialRepository(
        LinklyCloudCredentialRecord? credential) : ILinklyCloudCredentialRepository
    {
        private LinklyCloudCredentialRecord? _credential = credential;

        public List<(string StoreCode, string Environment)> Calls { get; } = [];

        public List<(string StoreCode, string Environment)> UpsertCalls { get; } = [];

        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            Calls.Add((storeCode, environment));
            return Task.FromResult(_credential is not null &&
                string.Equals(_credential.StoreCode, storeCode, StringComparison.Ordinal) &&
                string.Equals(_credential.Environment, environment, StringComparison.Ordinal)
                    ? _credential
                    : null);
        }

        public Task<LinklyCloudCredentialRecord> UpsertAsync(
            string storeCode,
            string environment,
            string username,
            string password,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            UpsertCalls.Add((storeCode, environment));
            _credential = new LinklyCloudCredentialRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Username = username,
                Password = password,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
            return Task.FromResult(_credential);
        }
    }

    private sealed class FixedCloudTerminalRepository(
        LinklyCloudTerminalRecord terminal,
        string mode = "Active")
        : ILinklyCloudTerminalRepository
    {
        public int ModeCalls { get; private set; }

        public Task<IReadOnlyList<LinklyCloudTerminalRecord>> ListAsync(
            string environment, string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LinklyCloudTerminalRecord>>([terminal]);

        public Task<LinklyCloudTerminalRecord?> GetAsync(
            string environment, string storeCode, Guid terminalId, CancellationToken cancellationToken) =>
            Task.FromResult<LinklyCloudTerminalRecord?>(
                terminal.Environment == environment &&
                terminal.StoreCode == storeCode &&
                terminal.TerminalId == terminalId
                    ? terminal
                    : null);

        public Task<LinklyCloudDeviceSelectionRecord?> GetSelectionAsync(
            string environment, string storeCode, string deviceCode, CancellationToken cancellationToken) =>
            Task.FromResult<LinklyCloudDeviceSelectionRecord?>(null);

        public Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid terminalId,
            long? expectedRevision,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetConfigurationModeAsync(
            string environment, string storeCode, CancellationToken cancellationToken)
        {
            ModeCalls++;
            return Task.FromResult(mode);
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
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleasePairingLeaseAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid expectedPairingAttemptId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseOperationLeaseAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            Guid expectedOperationLeaseId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryRecordHealthAsync(
            string environment,
            string storeCode,
            Guid terminalId,
            DateTime expectedTerminalUpdatedAt,
            string healthStatus,
            DateTime checkedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingTerminalCredentialRepository(
        params LinklyCloudBackendTerminalCredentialRecord[] credentials) : ILinklyCloudBackendTerminalCredentialRepository
    {
        private readonly List<LinklyCloudBackendTerminalCredentialRecord> _credentials = credentials.ToList();

        public List<(string Environment, string StoreCode, string DeviceCode)> Calls { get; } = [];

        public IReadOnlyList<LinklyCloudBackendTerminalCredentialRecord> Credentials => _credentials;

        public Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((environment, storeCode, deviceCode));
            return Task.FromResult(_credentials.SingleOrDefault(credential =>
                string.Equals(credential.Environment, environment, StringComparison.Ordinal) &&
                string.Equals(credential.StoreCode, storeCode, StringComparison.Ordinal) &&
                string.Equals(credential.DeviceCode, deviceCode, StringComparison.Ordinal)));
        }

        public Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string secret,
            string posId,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            var existing = _credentials.SingleOrDefault(credential =>
                string.Equals(credential.Environment, environment, StringComparison.Ordinal) &&
                string.Equals(credential.StoreCode, storeCode, StringComparison.Ordinal) &&
                string.Equals(credential.DeviceCode, deviceCode, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new LinklyCloudBackendTerminalCredentialRecord
                {
                    Environment = environment,
                    StoreCode = storeCode,
                    DeviceCode = deviceCode
                };
                _credentials.Add(existing);
            }

            existing.Secret = secret;
            existing.PosId = posId;
            existing.UpdatedAt = updatedAt;
            existing.UpdatedBy = updatedBy;
            return Task.FromResult(existing);
        }

        public Task AcquireLegacyPairingLeaseAsync(
            string environment, string storeCode, Guid attemptId, DateTime leaseExpiresAt,
            DateTime now, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseLegacyPairingLeaseAsync(
            string environment, string storeCode, Guid attemptId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
            string environment, string storeCode, string deviceCode, Guid attemptId, DateTime now,
            string secret, string posId, string? updatedBy, CancellationToken cancellationToken) =>
            UpsertAsync(environment, storeCode, deviceCode, secret, posId, now, updatedBy, cancellationToken);
    }

    private sealed class CapturingTokenHttpMessageHandler : HttpMessageHandler
    {
        public List<JsonDocument> RequestBodies { get; } = [];
        public List<bool?> ConnectionCloseHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ConnectionCloseHeaders.Add(request.Headers.ConnectionClose);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(JsonDocument.Parse(body));
            var posId = RequestBodies[^1].RootElement.GetProperty("posId").GetString() ?? string.Empty;
            var suffix = posId.Split('-').Last();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    token = $"token-for-{suffix}",
                    expirySeconds = 300
                })
            };
        }
    }

    private sealed class ConcurrentLinklyHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private int _currentConcurrency;

        public int MaxConcurrency { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _currentConcurrency++;
                MaxConcurrency = Math.Max(MaxConcurrency, _currentConcurrency);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                var isTokenRequest = request.RequestUri?.AbsolutePath.EndsWith(
                    "/tokens/cloudpos",
                    StringComparison.Ordinal) == true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = isTokenRequest
                        ? JsonContent.Create(new { token = "access-token", expirySeconds = 300 })
                        : JsonContent.Create(new { SessionId = "session", Response = (object?)null })
                };
            }
            finally
            {
                lock (_sync)
                {
                    _currentConcurrency--;
                }
            }
        }
    }

    private sealed class CoordinatedNotificationRepository(
        int expectedNotificationReads) : ILinklyCloudBackendAsyncRepository
    {
        private readonly InMemoryLinklyCloudBackendAsyncRepository _inner = new();
        private readonly TaskCompletionSource<bool> _allSnapshotsRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _notificationReads;

        public Task<bool> TryCreateSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken) => _inner.TryCreateSessionAsync(session, cancellationToken);

        public Task UpsertSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken) => _inner.UpsertSessionAsync(session, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken) =>
            _inner.GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);

        public async Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            var snapshot = await _inner.GetSessionByEnvironmentSessionIdAsync(
                environment, sessionId, cancellationToken);
            if (Interlocked.Increment(ref _notificationReads) == expectedNotificationReads)
            {
                _allSnapshotsRead.TrySetResult(true);
            }

            await _allSnapshotsRead.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken) =>
            _inner.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken) =>
            _inner.GetResumableSessionAsync(environment, storeCode, deviceCode, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> GetResumableSettlementSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken) =>
            _inner.GetResumableSettlementSessionAsync(environment, storeCode, deviceCode, cancellationToken);

        public Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            DateTimeOffset acknowledgedAt,
            bool supervisorResolved,
            CancellationToken cancellationToken) =>
            _inner.AcknowledgeSessionAsync(
                environment, storeCode, deviceCode, sessionId, acknowledgedAt, supervisorResolved, cancellationToken);

        public Task AddNotificationAsync(
            LinklyCloudBackendNotificationRecord notification,
            CancellationToken cancellationToken) => _inner.AddNotificationAsync(notification, cancellationToken);

        public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken) =>
            _inner.GetNotificationsAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
    }

    private sealed class LinklyTransportHttpMessageHandler(
        string responseJson,
        HttpStatusCode responseStatusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(responseStatusCode)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static JsonDocument FindLinklyLog(
        IReadOnlyList<string> lines,
        string operation,
        string phase)
    {
        foreach (var line in lines)
        {
            var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
            if (jsonStart < 0)
            {
                continue;
            }

            var document = JsonDocument.Parse(line[jsonStart..]);
            if (string.Equals(document.RootElement.GetProperty("operation").GetString(), operation, StringComparison.Ordinal) &&
                string.Equals(document.RootElement.GetProperty("phase").GetString(), phase, StringComparison.Ordinal))
            {
                return document;
            }

            document.Dispose();
        }

        throw new Xunit.Sdk.XunitException($"Expected Linkly JSON log operation={operation} phase={phase}.");
    }

    private static void AssertServiceEvidenceLog(
        IReadOnlyList<string> lines,
        string operation,
        string phase,
        string method,
        string certCase,
        bool expectRequestJson,
        bool expectResponseJson)
    {
        using var log = FindLinklyLog(lines, operation, phase);
        Assert.Equal("api-backend-service", log.RootElement.GetProperty("source").GetString());
        var details = log.RootElement.GetProperty("details");
        Assert.False(string.IsNullOrWhiteSpace(details.GetProperty("timestamp").GetString()));
        var data = details.GetProperty("data");
        Assert.Equal(certCase, data.GetProperty("certCase").GetString());
        Assert.Equal(method, data.GetProperty("method").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("url").GetString()));
        if (phase == "response")
        {
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("transactionReference").GetString()));
        }

        Assert.Equal(
            expectRequestJson,
            data.TryGetProperty("requestJson", out var requestJson) &&
                requestJson.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(requestJson.GetString()));
        Assert.Equal(
            expectResponseJson,
            data.TryGetProperty("responseJson", out var responseJson) &&
                responseJson.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(responseJson.GetString()));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}
