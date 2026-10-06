using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Controllers;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class InstallmentServiceTests
{
    [Theory]
    [InlineData(nameof(InstallmentOrderEntity.PickedUpAt))]
    [InlineData(nameof(InstallmentOrderEntity.CancellationKind))]
    [InlineData(nameof(InstallmentOrderEntity.CancelledAt))]
    public void Installment_order_lifecycle_columns_are_nullable(string propertyName)
    {
        var property = typeof(InstallmentOrderEntity).GetProperty(propertyName);

        Assert.NotNull(property);
        var column = property!.GetCustomAttribute<SugarColumn>();
        Assert.NotNull(column);
        Assert.True(column!.IsNullable);
    }

    [Fact]
    public void Installment_card_transactions_use_cross_database_unbounded_column_type()
    {
        var property = typeof(InstallmentPaymentEntity).GetProperty(
            nameof(InstallmentPaymentEntity.CardTransactionsJson));

        Assert.NotNull(property);
        var column = property!.GetCustomAttribute<SugarColumn>();
        Assert.NotNull(column);
        Assert.Equal(StaticConfig.CodeFirst_BigString, column!.ColumnDataType);
        Assert.True(column.IsNullable);
    }

    [Fact]
    public void Installment_order_schema_repair_is_owned_by_startup_initializer()
    {
        var initializerType = typeof(SqlSugarInstallmentRepository).Assembly.GetType(
            "Hbpos.Api.Services.SqlSugarInstallmentSchemaInitializer");

        Assert.NotNull(initializerType);
        var field = initializerType!.GetField(
            "EnsureNullableLifecycleColumnsSql",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var sql = Assert.IsType<string>(field!.GetRawConstantValue());
        Assert.Contains("OBJECT_ID(N'[dbo].[InstallmentOrder]', N'U')", sql);
        Assert.Contains("sys.columns", sql);
        Assert.Contains("[is_nullable] = 0", sql);
        Assert.Contains("ALTER COLUMN [PickedUpAt] DATETIME2 NULL", sql);
        Assert.Contains("ALTER COLUMN [CancellationKind] INT NULL", sql);
        Assert.Contains("ALTER COLUMN [CancelledAt] DATETIME2 NULL", sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[InstallmentPayment]', N'U')", sql);
        Assert.Contains("[name] = N'CardTransactionsJson'", sql);
        Assert.Contains("[max_length] <> -1", sql);
        Assert.Contains("ALTER COLUMN [CardTransactionsJson] NVARCHAR(MAX) NULL", sql);
    }

    [Fact]
    public async Task Sql_repository_request_path_does_not_run_code_first_or_DDL()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var statements = fixture.CaptureSql();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);

        _ = await repository.GetDetailsAsync(Guid.NewGuid(), CancellationToken.None);

        AssertNoSchemaSql(statements);
    }

    [Fact]
    public void Sql_repository_guid_filters_do_not_format_guid_inside_query_expression()
    {
        var source = ReadInstallmentServiceSource();
        var forbiddenSnippets = new[]
        {
            "x.PaymentGuid == payment.PaymentGuid.ToString(\"D\")",
            "x.PaymentGuid == refund.PaymentGuid.ToString(\"D\")",
            "x.InstallmentGuid == installmentGuid.ToString(\"D\")",
            "x.PaymentGuid == paymentGuid.ToString(\"D\")"
        };

        foreach (var snippet in forbiddenSnippets)
        {
            Assert.DoesNotContain(snippet, source);
        }
    }

    [Fact]
    public void Sql_server_guid_filter_uses_string_constant_without_format_function()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        var paymentGuidText = Guid.Parse("7e9464fc-a8b3-41b2-9645-6ee21a31a5e9").ToString("D");

        var sql = db.Queryable<InstallmentPaymentEntity>()
            .Where(x => x.PaymentGuid == paymentGuidText)
            .ToSqlString();

        Assert.DoesNotContain("FORMAT(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sql_repository_create_allows_order_without_pickup_or_cancellation_info()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(
            repository,
            new FakeReservationService(),
            new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z")));

        var response = await service.CreateAsync(
            CreateRequest(totalAmount: 60m, downPaymentAmount: 20m),
            CancellationToken.None);
        var stored = await repository.GetDetailsAsync(response.InstallmentGuid, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Null(stored!.PickupInfo);
        Assert.Null(stored.CancellationInfo);
        Assert.Equal(InstallmentStatus.Active, stored.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-05-21T10:00:00Z"), stored.UpdatedAt);
    }

    [Fact]
    public async Task Create_rejects_down_payment_below_minimum()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 100m, downPaymentAmount: 19.99m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));

        Assert.Contains("at least $20", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_rejects_installment_total_below_minimum()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 49.99m, downPaymentAmount: 20m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));

        Assert.Contains("$50", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_returns_existing_installment_idempotently()
    {
        var service = CreateService();
        var request = CreateRequest();

        await service.CreateAsync(request, CancellationToken.None);
        var duplicate = await service.CreateAsync(request, CancellationToken.None);

        Assert.True(duplicate.AlreadyExists);
        Assert.Equal("AlreadyExists", duplicate.Message);
    }

    [Fact]
    public async Task Append_payment_records_once_and_marks_paid_off()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 60m, downPaymentAmount: 20m);
        var created = await service.CreateAsync(request, CancellationToken.None);
        var paymentGuid = Guid.NewGuid();

        var response = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, paymentGuid, amount: 40m),
            CancellationToken.None);
        var duplicate = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, paymentGuid, amount: 40m),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.PaidOff, response.Status);
        Assert.Equal(60m, response.PaidAmount);
        Assert.Equal(0m, response.BalanceAmount);
        Assert.True(duplicate.AlreadyRecorded);
        Assert.Equal(60m, duplicate.PaidAmount);
    }

    [Fact]
    public async Task Append_payment_is_idempotent_by_idempotency_key()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);
        var idempotencyKey = "INSTALLMENT-1:PAY-2";
        var firstPaymentGuid = Guid.NewGuid();

        await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, firstPaymentGuid, amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);
        var duplicate = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, Guid.NewGuid(), amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        Assert.True(duplicate.AlreadyRecorded);
        Assert.Equal(firstPaymentGuid, duplicate.PaymentGuid);
        Assert.Equal(30m, duplicate.PaidAmount);
    }

    [Fact]
    public async Task Append_payment_allows_same_idempotency_key_on_different_installments()
    {
        var service = CreateService();
        var first = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);
        var second = await service.CreateAsync(CreateRequest(totalAmount: 70m, downPaymentAmount: 20m), CancellationToken.None);
        var idempotencyKey = "SHARED-PAYMENT-KEY";
        var firstPaymentGuid = Guid.NewGuid();
        var secondPaymentGuid = Guid.NewGuid();

        await service.AppendPaymentAsync(
            CreatePayment(first.InstallmentGuid, firstPaymentGuid, amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        var response = await service.AppendPaymentAsync(
            CreatePayment(second.InstallmentGuid, secondPaymentGuid, amount: 15m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        Assert.False(response.AlreadyRecorded);
        Assert.Equal(second.InstallmentGuid, response.InstallmentGuid);
        Assert.Equal(secondPaymentGuid, response.PaymentGuid);
        Assert.Equal(35m, response.PaidAmount);
    }

    [Fact]
    public async Task Append_payment_rejects_device_scope_mismatch()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AppendPaymentAsync(
                CreatePayment(created.InstallmentGuid, Guid.NewGuid(), amount: 10m) with { StoreCode = "S02" },
                CancellationToken.None));

        Assert.Contains("this store", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_rejects_store_scope_mismatch()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid) with { StoreCode = "S02" }, CancellationToken.None));

        Assert.Contains("this store", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_rejects_same_device_idempotency_key_without_operation_guid()
    {
        var service = CreateService();
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 50m, downPaymentAmount: 50m),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(
                CreatePickup(created.InstallmentGuid) with
                {
                    IdempotencyKey = $"{created.InstallmentGuid:D}:pickup"
                },
                CancellationToken.None));

        Assert.Contains("provided together", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_allows_enabled_same_store_cross_device_with_operation_identity()
    {
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(),
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { PickupEnabled = true }));
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 50m, downPaymentAmount: 50m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();

        var response = await service.ConfirmPickupAsync(
            CreatePickup(created.InstallmentGuid) with
            {
                DeviceCode = "POS02",
                CashierId = "C02",
                CashierName = "Cashier Two",
                OperationGuid = operationGuid,
                IdempotencyKey = operationGuid.ToString("D")
            },
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.PickedUp, response.Status);
        Assert.Equal("Cashier Two", response.Details.PickupInfo?.PickedUpBy);
    }

    [Fact]
    public async Task Confirm_pickup_retry_reuses_operation_when_only_client_timestamp_changes()
    {
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(),
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { PickupEnabled = true }));
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 50m, downPaymentAmount: 50m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();
        var firstRequest = CreatePickup(created.InstallmentGuid) with
        {
            DeviceCode = "POS02",
            OperationGuid = operationGuid,
            IdempotencyKey = $"{created.InstallmentGuid:D}:pickup"
        };

        var first = await service.ConfirmPickupAsync(firstRequest, CancellationToken.None);
        var replay = await service.ConfirmPickupAsync(
            firstRequest with { ConfirmedAt = firstRequest.ConfirmedAt.AddMinutes(5) },
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.PickedUp, first.Status);
        Assert.True(replay.AlreadyConfirmed);
    }

    [Fact]
    public async Task Cross_device_pickup_fails_closed_when_switch_is_disabled()
    {
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(),
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { PickupEnabled = false }));
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 50m, downPaymentAmount: 50m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(
                CreatePickup(created.InstallmentGuid) with
                {
                    DeviceCode = "POS02",
                    OperationGuid = operationGuid,
                    IdempotencyKey = operationGuid.ToString("D")
                },
                CancellationToken.None));

        Assert.Contains("cross-device", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cross_device_pickup_terminal_replay_ignores_recovery_cashier_and_bypasses_disabled_switch_only_for_same_operation()
    {
        var repository = new InMemoryInstallmentRepository();
        var enabled = new InstallmentService(
            repository,
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { PickupEnabled = true }));
        var created = await enabled.CreateAsync(
            CreateRequest(totalAmount: 50m, downPaymentAmount: 50m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();
        var firstRequest = CreatePickup(created.InstallmentGuid) with
        {
            DeviceCode = "POS02",
            CashierId = "C02",
            CashierName = "Cashier Two",
            Note = "Collected",
            OperationGuid = operationGuid,
            IdempotencyKey = operationGuid.ToString("D")
        };
        await enabled.ConfirmPickupAsync(firstRequest, CancellationToken.None);

        var disabled = new InstallmentService(
            repository,
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { PickupEnabled = false }));
        var replay = await disabled.ConfirmPickupAsync(
            firstRequest with { CashierId = "C03", CashierName = "Cashier Three" },
            CancellationToken.None);

        Assert.True(replay.AlreadyConfirmed);
        Assert.Equal("Cashier Two", replay.Details.PickupInfo?.PickedUpBy);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabled.ConfirmPickupAsync(
                firstRequest with
                {
                    OperationGuid = Guid.NewGuid(),
                    IdempotencyKey = Guid.NewGuid().ToString("D")
                },
                CancellationToken.None));
        Assert.Contains("idempotency", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_rejects_line_total_mismatch()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 100m, downPaymentAmount: 20m) with
        {
            Lines =
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Tea",
                    "9300001",
                    1m,
                    99m,
                    0m,
                    99m)
            ]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));

        Assert.Contains("Line total", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_requires_paid_off_installment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None));

        Assert.Contains("paid off", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_is_idempotent_after_success()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);
        var first = await service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None);

        var second = await service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None);

        Assert.Equal(InstallmentStatus.PickedUp, first.Status);
        Assert.True(second.AlreadyConfirmed);
    }

    [Fact]
    public async Task Query_history_matches_trimmed_keyword_against_summary_fields()
    {
        var service = CreateService();
        var aliceRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.NewGuid(),
            CustomerName = "Alice Zhang",
            CustomerPhone = "0400111222"
        };
        var bobRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.NewGuid(),
            CustomerName = "Bob Li",
            CustomerPhone = "0499888777"
        };

        var alice = await service.CreateAsync(aliceRequest, CancellationToken.None);
        await service.CreateAsync(bobRequest, CancellationToken.None);

        var byName = await service.QueryAsync(
            new InstallmentHistoryQueryRequest(" S01 ", Keyword: "  Alice  "),
            CancellationToken.None);
        var byNumber = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: $"  {alice.InstallmentNumber}  "),
            CancellationToken.None);

        Assert.Equal(alice.InstallmentGuid, Assert.Single(byName.Orders).InstallmentGuid);
        Assert.Equal(alice.InstallmentGuid, Assert.Single(byNumber.Orders).InstallmentGuid);
    }

    [Theory]
    [InlineData("SKU-TARGET")]
    [InlineData("ITEM-TARGET")]
    [InlineData("930000000001")]
    public async Task Sql_repository_history_matches_item_number_and_barcode(string keyword)
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        var targetRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Lines =
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-TARGET",
                    null,
                    "Tea",
                    "930000000001",
                    1m,
                    100m,
                    0m,
                    100m,
                    "ITEM-TARGET")
            ]
        };
        var otherRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.Parse("ffffffff-1111-2222-3333-444444444444"),
            Lines =
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-OTHER",
                    null,
                    "Coffee",
                    "930000000002",
                    1m,
                    100m,
                    0m,
                    100m,
                    "ITEM-OTHER")
            ]
        };

        var target = await service.CreateAsync(targetRequest, CancellationToken.None);
        await service.CreateAsync(otherRequest, CancellationToken.None);

        var response = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: keyword),
            CancellationToken.None);

        Assert.Equal(target.InstallmentGuid, Assert.Single(response.Orders).InstallmentGuid);
    }

    [Fact]
    public async Task Sql_repository_history_does_not_partially_match_product_identifiers()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        await service.CreateAsync(
            CreateRequest() with
            {
                InstallmentGuid = Guid.NewGuid(),
                CustomerName = "Exact identifier customer",
                Lines =
                [
                    new InstallmentLineDto(
                        Guid.NewGuid(),
                        "SKU-TARGET-10",
                        null,
                        "Tea",
                        "930000000010",
                        1m,
                        100m,
                        0m,
                        100m,
                        "ITEM-TARGET-10")
                ]
            },
            CancellationToken.None);

        var response = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: "ITEM-TARGET"),
            CancellationToken.None);

        Assert.Empty(response.Orders);
    }

    [Theory]
    [InlineData("_")]
    [InlineData("%")]
    [InlineData("[")]
    public async Task Sql_repository_history_treats_special_identifier_characters_as_literals(string keyword)
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        var targetGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        await service.CreateAsync(
            CreateRequest() with
            {
                InstallmentGuid = targetGuid,
                Lines =
                [
                    new InstallmentLineDto(
                        Guid.NewGuid(),
                        "SKU-SPECIAL",
                        null,
                        "Special Tea",
                        "BAR-SPECIAL",
                        1m,
                        100m,
                        0m,
                        100m,
                        keyword)
                ]
            },
            CancellationToken.None);
        await service.CreateAsync(
            CreateRequest() with
            {
                InstallmentGuid = Guid.Parse("ffffffff-1111-2222-3333-444444444444"),
                Lines =
                [
                    new InstallmentLineDto(
                        Guid.NewGuid(),
                        "SKU-OTHER",
                        null,
                        "Other Tea",
                        "BAR-OTHER",
                        1m,
                        100m,
                        0m,
                        100m,
                        "X")
                ]
            },
            CancellationToken.None);

        var response = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: keyword),
            CancellationToken.None);

        Assert.Equal(targetGuid, Assert.Single(response.Orders).InstallmentGuid);
    }

    [Fact]
    public async Task Sql_repository_keyword_pagination_filters_and_pages_in_the_database()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var db = fixture.DbContext.PosmDb;
        const int orderCount = 1_205;
        var createdAt = DateTime.Parse("2026-08-25T10:00:00Z").ToUniversalTime();
        var orders = Enumerable.Range(0, orderCount)
            .Select(index => new InstallmentOrderEntity
            {
                InstallmentGuid = Guid.Parse($"00000000-0000-0000-0000-{index:D12}").ToString("D"),
                InstallmentNumber = $"IO-DEEP-{index:D4}",
                StoreCode = "S01",
                DeviceCode = "POS01",
                CashierId = "C01",
                CashierName = "Cashier",
                CustomerName = "Customer",
                CustomerPhone = "0400000000",
                TotalAmount = 100m,
                DownPaymentAmount = 20m,
                PaidAmount = 20m,
                BalanceAmount = 80m,
                Status = (int)InstallmentStatus.Active,
                CreatedAt = createdAt.AddSeconds(-index),
                UpdatedAt = createdAt.AddSeconds(-index)
            })
            .ToList();
        var lines = orders.Select(order => new InstallmentOrderLineEntity
        {
            InstallmentLineGuid = Guid.NewGuid().ToString("D"),
            InstallmentGuid = order.InstallmentGuid,
            ProductCode = "SKU-DEEP",
            DisplayName = "Tea",
            LookupCode = "930000000001",
            ItemNumber = "ITEM-DEEP",
            Quantity = 1m,
            UnitPrice = 100m,
            ActualAmount = 100m
        }).ToList();
        foreach (var batch in orders.Chunk(500))
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }

        foreach (var batch in lines.Chunk(500))
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }

        var statements = fixture.CaptureSql();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);

        var response = await repository.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: "ITEM-DEEP", Take: 5, Skip: 1_000),
            CancellationToken.None);

        var selects = statements
            .Where(statement => statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(selects);
        Assert.Contains("EXISTS", selects[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" IN (", selects[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, response.Orders.Count);
        Assert.Equal("IO-DEEP-1000", response.Orders[0].InstallmentNumber);
    }

    [Fact]
    public void Sql_server_keyword_pagination_translates_to_exists_without_guid_in_list()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        const string keyword = "ITEM-DEEP";

        var sql = db.Queryable<InstallmentOrderEntity>()
            .Where(order => order.StoreCode == "S01")
            .Where(order =>
                SqlFunc.CharIndexNew(order.InstallmentNumber, keyword) > 0 ||
                SqlFunc.Subqueryable<InstallmentOrderLineEntity>()
                    .Where(line => line.InstallmentGuid == order.InstallmentGuid &&
                        (line.ItemNumber == keyword ||
                         line.LookupCode == keyword ||
                         line.ProductCode == keyword))
                    .Any())
            .OrderByDescending(order => order.UpdatedAt)
            .OrderByDescending(order => order.InstallmentGuid)
            .Skip(2_200)
            .Take(100)
            .ToSql();

        Assert.Contains("EXISTS", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" IN (", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" LIKE ", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.True(sql.Key.Length < 5_000, $"SQL Server 查询文本异常膨胀到 {sql.Key.Length} 个字符。");
    }

    [Fact]
    public async Task Sql_repository_history_summary_preserves_cancellation_kind()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        await service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None);

        var response = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: created.InstallmentNumber),
            CancellationToken.None);

        var summary = Assert.Single(response.Orders);
        Assert.Equal(InstallmentCancellationKind.VoidCancel, summary.CancellationKind);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Sql_repository_history_finds_item_number_within_two_seconds_at_scale()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var db = fixture.DbContext.PosmDb;
        var updatedAt = DateTime.Parse("2026-08-25T10:00:00Z").ToUniversalTime();
        const int orderCount = 10_000;
        var targetGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D");
        var orders = Enumerable.Range(1, orderCount)
            .Select(index => new InstallmentOrderEntity
            {
                InstallmentGuid = Guid.Parse($"00000000-0000-0000-0000-{index:D12}").ToString("D"),
                InstallmentNumber = $"IO-PERF-{index:D6}",
                StoreCode = "S01",
                DeviceCode = "POS01",
                CashierId = "C01",
                CashierName = "Cashier",
                CustomerName = "Customer",
                CustomerPhone = "0400000000",
                TotalAmount = 100m,
                DownPaymentAmount = 20m,
                PaidAmount = 20m,
                BalanceAmount = 80m,
                Status = (int)InstallmentStatus.Active,
                CreatedAt = updatedAt.AddSeconds(-index),
                UpdatedAt = updatedAt.AddSeconds(-index)
            })
            .Append(new InstallmentOrderEntity
            {
                InstallmentGuid = targetGuid,
                InstallmentNumber = "IO-PERF-TARGET",
                StoreCode = "S01",
                DeviceCode = "POS01",
                CashierId = "C01",
                CashierName = "Cashier",
                CustomerName = "Target Customer",
                CustomerPhone = "0499999999",
                TotalAmount = 100m,
                DownPaymentAmount = 20m,
                PaidAmount = 20m,
                BalanceAmount = 80m,
                Status = (int)InstallmentStatus.Active,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            })
            .ToList();
        var lines = orders.Select((order, index) => new InstallmentOrderLineEntity
        {
            InstallmentLineGuid = Guid.NewGuid().ToString("D"),
            InstallmentGuid = order.InstallmentGuid,
            ProductCode = $"SKU-{index:D6}",
            DisplayName = "Tea",
            LookupCode = $"9301{index:D8}",
            ItemNumber = order.InstallmentGuid == targetGuid ? "ITEM-TARGET" : $"ITEM-{index:D6}",
            Quantity = 1m,
            UnitPrice = 100m,
            ActualAmount = 100m
        }).ToList();
        foreach (var batch in orders.Chunk(500))
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }

        foreach (var batch in lines.Chunk(500))
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }

        await db.Ado.ExecuteCommandAsync(
            "CREATE INDEX IX_Test_InstallmentOrder_UpdatedScope ON InstallmentOrder(StoreCode, UpdatedAt DESC, InstallmentGuid DESC);");
        await db.Ado.ExecuteCommandAsync(
            "CREATE INDEX IX_Test_InstallmentOrderLine_ItemNumber ON InstallmentOrderLine(ItemNumber, InstallmentGuid);");
        await db.Ado.ExecuteCommandAsync(
            "CREATE INDEX IX_Test_InstallmentOrderLine_Barcode ON InstallmentOrderLine(LookupCode, InstallmentGuid);");
        await db.Ado.ExecuteCommandAsync(
            "CREATE INDEX IX_Test_InstallmentOrderLine_ProductCode ON InstallmentOrderLine(ProductCode, InstallmentGuid);");
        var stopwatch = Stopwatch.StartNew();

        var response = await repository.QueryAsync(
            new InstallmentHistoryQueryRequest(
                "S01",
                Keyword: "ITEM-TARGET",
                Take: 100,
                UpdatedFrom: new DateTimeOffset(updatedAt.AddDays(-1), TimeSpan.Zero),
                UpdatedTo: new DateTimeOffset(updatedAt.AddDays(1), TimeSpan.Zero),
                OrderByUpdatedAt: true),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(targetGuid, Assert.Single(response.Orders).InstallmentGuid.ToString("D"));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"分期按货号查询耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 2 秒预算。");
    }

    [Fact]
    public async Task Sql_repository_history_filters_and_orders_by_updated_at_when_requested()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        var recentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var oldGuid = Guid.Parse("ffffffff-1111-2222-3333-444444444444");
        var recentGuidText = recentGuid.ToString("D");
        var oldGuidText = oldGuid.ToString("D");
        var now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");

        await service.CreateAsync(CreateRequest() with { InstallmentGuid = recentGuid }, CancellationToken.None);
        await service.CreateAsync(CreateRequest() with { InstallmentGuid = oldGuid }, CancellationToken.None);
        await fixture.DbContext.PosmDb.Updateable<InstallmentOrderEntity>()
            .SetColumns(entity => entity.UpdatedAt == now.UtcDateTime)
            .Where(entity => entity.InstallmentGuid == recentGuidText)
            .ExecuteCommandAsync();
        await fixture.DbContext.PosmDb.Updateable<InstallmentOrderEntity>()
            .SetColumns(entity => entity.UpdatedAt == now.AddDays(-2).UtcDateTime)
            .Where(entity => entity.InstallmentGuid == oldGuidText)
            .ExecuteCommandAsync();
        var persistedRecent = await fixture.DbContext.PosmDb.Queryable<InstallmentOrderEntity>()
            .FirstAsync(entity => entity.InstallmentGuid == recentGuidText);
        Assert.NotNull(persistedRecent);
        Assert.Equal(now.UtcDateTime, persistedRecent!.UpdatedAt);

        var response = await service.QueryAsync(
            new InstallmentHistoryQueryRequest(
                "S01",
                UpdatedFrom: now.AddHours(-1),
                UpdatedTo: now.AddHours(1),
                OrderByUpdatedAt: true),
            CancellationToken.None);

        Assert.Equal(recentGuid, Assert.Single(response.Orders).InstallmentGuid);
    }

    [Fact]
    public void History_query_request_defaults_skip_to_zero()
    {
        var request = new InstallmentHistoryQueryRequest("S01");

        Assert.Equal(0, request.Skip);
    }

    [Fact]
    public async Task History_defaults_skip_to_zero_and_preserves_legacy_take_semantics()
    {
        var historyService = new CapturingInstallmentHistoryService();
        var controller = new InstallmentsController(null!, historyService);

        await controller.History(
            "S01",
            null,
            null,
            null,
            null,
            null,
            0,
            CancellationToken.None);

        Assert.NotNull(historyService.LastQuery);
        Assert.Equal(0, historyService.LastQuery!.Skip);
        Assert.Equal(100, historyService.LastQuery.Take);
    }

    [Fact]
    public async Task History_forwards_updated_range_and_sort_mode()
    {
        var historyService = new CapturingInstallmentHistoryService();
        var controller = new InstallmentsController(null!, historyService);
        var updatedFrom = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var updatedTo = DateTimeOffset.Parse("2026-08-25T23:59:59Z");

        await controller.History(
            "S01",
            "POS-02",
            null,
            null,
            "ITEM-TARGET",
            null,
            100,
            CancellationToken.None,
            updatedFrom: updatedFrom,
            updatedTo: updatedTo,
            orderByUpdatedAt: true);

        Assert.NotNull(historyService.LastQuery);
        Assert.Equal(updatedFrom, historyService.LastQuery!.UpdatedFrom);
        Assert.Equal(updatedTo, historyService.LastQuery.UpdatedTo);
        Assert.True(historyService.LastQuery.OrderByUpdatedAt);
    }

    [Fact]
    public async Task History_rejects_negative_skip()
    {
        var controller = new InstallmentsController(null!, null!);

        var result = await controller.History(
            "S01",
            null,
            null,
            null,
            null,
            null,
            100,
            CancellationToken.None,
            skip: -1);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<InstallmentHistoryQueryResponse>>(badRequest.Value);
        Assert.Equal("INSTALLMENT_HISTORY_SKIP_INVALID", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Sql_repository_history_pages_use_stable_created_at_and_guid_order()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var repository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var service = new InstallmentService(repository, new FakeReservationService());
        var createdAt = DateTimeOffset.Parse("2026-05-21T10:00:00Z");
        var installments = new[]
        {
            (Guid: Guid.Parse("00000000-0000-0000-0000-000000000001"), CreatedAt: createdAt),
            (Guid: Guid.Parse("00000000-0000-0000-0000-000000000004"), CreatedAt: createdAt.AddMinutes(-1)),
            (Guid: Guid.Parse("00000000-0000-0000-0000-000000000002"), CreatedAt: createdAt),
            (Guid: Guid.Parse("00000000-0000-0000-0000-000000000003"), CreatedAt: createdAt)
        };
        foreach (var installment in installments)
        {
            await service.CreateAsync(
                CreateRequest() with
                {
                    InstallmentGuid = installment.Guid,
                    CreatedAt = installment.CreatedAt
                },
                CancellationToken.None);
        }

        var firstPage = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Take: 2),
            CancellationToken.None);
        var secondPage = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Take: 2, Skip: 2),
            CancellationToken.None);

        var expected = installments
            .OrderByDescending(installment => installment.CreatedAt)
            .ThenByDescending(installment => installment.Guid.ToString("D"), StringComparer.Ordinal)
            .Select(installment => installment.Guid)
            .ToArray();
        Assert.Equal(expected[..2], firstPage.Orders.Select(order => order.InstallmentGuid));
        Assert.Equal(expected[2..], secondPage.Orders.Select(order => order.InstallmentGuid));
        Assert.Empty(firstPage.Orders.Select(order => order.InstallmentGuid)
            .Intersect(secondPage.Orders.Select(order => order.InstallmentGuid)));
    }

    [Fact]
    public async Task Query_history_clamps_take_and_preserves_skip()
    {
        var repository = new InMemoryInstallmentRepository();
        var service = new InstallmentService(repository, new FakeReservationService());

        await service.QueryAsync(
            new InstallmentHistoryQueryRequest(" S01 ", Take: 500, Skip: 7),
            CancellationToken.None);

        Assert.NotNull(repository.LastHistoryQuery);
        Assert.Equal("S01", repository.LastHistoryQuery!.StoreCode);
        Assert.Equal(200, repository.LastHistoryQuery.Take);
        Assert.Equal(7, repository.LastHistoryQuery.Skip);
    }

    [Fact]
    public async Task Voucher_payment_requires_valid_reservation()
    {
        var reservation = new FakeReservationService();
        var service = CreateService(reservation);
        var request = CreateRequest(
            totalAmount: 50m,
            downPaymentAmount: 20m,
            method: PaymentMethodKind.Voucher,
            reference: "VOUCHER-1",
            reservationToken: "missing-token");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_with_voucher_payment_redeems_voucher_and_consumes_reservation()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 30m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 20m, 30m, CancellationToken.None);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);
        var statements = fixture.CaptureSql();

        var response = await service.CreateAsync(
            CreateRequest(
                totalAmount: 50m,
                downPaymentAmount: 20m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);
        AssertNoSchemaSql(statements);

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(20m, response.PaidAmount);
        Assert.NotNull(voucher);
        Assert.Equal(10m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
        Assert.Null(await reservationService.GetAsync(reservation.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Append_with_voucher_payment_redeems_voucher_and_consumes_reservation()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 50m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 60m, downPaymentAmount: 20m),
            CancellationToken.None);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 30m, 50m, CancellationToken.None);
        var statements = fixture.CaptureSql();

        var response = await service.AppendPaymentAsync(
            CreatePayment(
                created.InstallmentGuid,
                Guid.NewGuid(),
                amount: 30m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);
        AssertNoSchemaSql(statements);

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(50m, response.PaidAmount);
        Assert.Equal(10m, response.BalanceAmount);
        Assert.NotNull(voucher);
        Assert.Equal(20m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
        Assert.Null(await reservationService.GetAsync(reservation.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Append_with_voucher_payment_rejects_reused_reservation_token()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 50m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 60m, downPaymentAmount: 20m),
            CancellationToken.None);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 15m, 50m, CancellationToken.None);

        await service.AppendPaymentAsync(
            CreatePayment(
                created.InstallmentGuid,
                Guid.NewGuid(),
                amount: 15m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AppendPaymentAsync(
                CreatePayment(
                    created.InstallmentGuid,
                    Guid.NewGuid(),
                    amount: 15m,
                    method: PaymentMethodKind.Voucher,
                    reference: "V001",
                    reservationToken: reservation.Token),
                CancellationToken.None));
        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Contains("Voucher reservation token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(35m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
    }

    [Fact]
    public async Task Cancel_with_refund_marks_active_installment_cancelled()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var response = await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
            CancellationToken.None);
        var duplicate = await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND-2")]),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.NotNull(response.Details.CancellationInfo);
        Assert.Equal(InstallmentCancellationKind.RefundCancel, response.Details.CancellationInfo!.Kind);
        Assert.Equal(0m, response.Details.PaidAmount);
        Assert.True(duplicate.AlreadyCancelled);
    }

    [Fact]
    public async Task Void_marks_active_installment_cancelled_without_refund_payment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var response = await service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None);
        var duplicate = await service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.NotNull(response.Details.CancellationInfo);
        Assert.Equal(InstallmentCancellationKind.VoidCancel, response.Details.CancellationInfo!.Kind);
        Assert.Equal(20m, response.Details.PaidAmount);
        Assert.Single(response.Details.Payments);
        Assert.True(duplicate.AlreadyVoided);
    }

    [Fact]
    public async Task Void_allows_legacy_same_device_request_with_idempotency_key_only()
    {
        var service = CreateService();
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 80m, downPaymentAmount: 20m),
            CancellationToken.None);

        var response = await service.VoidAsync(
            CreateVoid(created.InstallmentGuid) with
            {
                OperationGuid = Guid.Empty,
                IdempotencyKey = $"{created.InstallmentGuid:D}:void"
            },
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.Equal(InstallmentCancellationKind.VoidCancel, response.Details.CancellationInfo?.Kind);
    }

    [Fact]
    public async Task Void_rejects_incomplete_lifecycle_identity_outside_legacy_same_device_key_only()
    {
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(),
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { VoidEnabled = true }));
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 80m, downPaymentAmount: 20m),
            CancellationToken.None);

        var crossDeviceKeyOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(
                CreateVoid(created.InstallmentGuid) with
                {
                    DeviceCode = "POS02",
                    IdempotencyKey = $"{created.InstallmentGuid:D}:void"
                },
                CancellationToken.None));
        var sameDeviceGuidOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(
                CreateVoid(created.InstallmentGuid) with { OperationGuid = Guid.NewGuid() },
                CancellationToken.None));

        Assert.Contains("provided together", crossDeviceKeyOnly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provided together", sameDeviceGuidOnly.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Void_allows_enabled_same_store_cross_device_with_operation_identity()
    {
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(),
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { VoidEnabled = true }));
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 80m, downPaymentAmount: 20m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();

        var response = await service.VoidAsync(
            CreateVoid(created.InstallmentGuid) with
            {
                DeviceCode = "POS02",
                CashierId = "C02",
                CashierName = "Cashier Two",
                OperationGuid = operationGuid,
                IdempotencyKey = operationGuid.ToString("D")
            },
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.Equal("Cashier Two", response.Details.CancellationInfo?.CancelledBy);
    }

    [Fact]
    public async Task Cross_device_void_terminal_replay_ignores_recovery_cashier_and_bypasses_disabled_switch_only_for_same_operation()
    {
        var repository = new InMemoryInstallmentRepository();
        var enabled = new InstallmentService(
            repository,
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { VoidEnabled = true }));
        var created = await enabled.CreateAsync(
            CreateRequest(totalAmount: 80m, downPaymentAmount: 20m),
            CancellationToken.None);
        var operationGuid = Guid.NewGuid();
        var firstRequest = CreateVoid(created.InstallmentGuid) with
        {
            DeviceCode = "POS02",
            CashierId = "C02",
            CashierName = "Cashier Two",
            OperationGuid = operationGuid,
            IdempotencyKey = operationGuid.ToString("D")
        };
        await enabled.VoidAsync(firstRequest, CancellationToken.None);

        var disabled = new InstallmentService(
            repository,
            new FakeReservationService(),
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions { VoidEnabled = false }));
        var replay = await disabled.VoidAsync(
            firstRequest with { CashierId = "C03", CashierName = "Cashier Three" },
            CancellationToken.None);

        Assert.True(replay.AlreadyVoided);
        Assert.Equal("Cashier Two", replay.Details.CancellationInfo?.CancelledBy);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabled.VoidAsync(
                firstRequest with
                {
                    OperationGuid = Guid.NewGuid(),
                    IdempotencyKey = Guid.NewGuid().ToString("D")
                },
                CancellationToken.None));
        Assert.Contains("idempotency", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_with_refund_allows_paid_off_installment_before_pickup()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);
        Assert.Equal(InstallmentStatus.PaidOff, created.Status);

        // 已付清仍须按方式全额原路退，部分退款不能把付清单取消。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(
                CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
                CancellationToken.None));

        var response = await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 50m, "CASH-REFUND")]),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.Equal(InstallmentCancellationKind.RefundCancel, response.Details.CancellationInfo!.Kind);
        Assert.Equal(0m, response.Details.PaidAmount);
    }

    [Fact]
    public async Task Cancel_with_voucher_mode_requires_voucher_refund_for_the_full_paid_total()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);
        var voucherMode = CreateCancel(
            created.InstallmentGuid,
            [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 50m, "CASH-REFUND")]) with
        {
            RefundMode = InstallmentCancelRefundMode.Voucher
        };

        // 退代金券模式下现金退款不被接受。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(voucherMode, CancellationToken.None));

        var response = await service.CancelAsync(
            voucherMode with
            {
                Refunds = [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 50m, "VOUCHER_REFUND:RF001")]
            },
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.Contains(response.Details.Payments, payment => payment.Method == PaymentMethodKind.Voucher && payment.Amount == -50m);
    }

    [Fact]
    public async Task Void_still_rejects_paid_off_installment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);

        // 作废不退款会让客户损失全款，付清后只允许取消退款。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_with_refund_rejects_picked_up_installment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 50m, downPaymentAmount: 50m), CancellationToken.None);
        await service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(
                CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 50m, "CASH-REFUND")]),
                CancellationToken.None));

        Assert.Contains("picked up", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_then_void_returns_conflict()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);
        await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None));

        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallmentService CreateService(FakeReservationService? reservation = null)
    {
        return new InstallmentService(new InMemoryInstallmentRepository(), reservation ?? new FakeReservationService());
    }

    private static string ReadInstallmentServiceSource([CallerFilePath] string testFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("Cannot resolve test source directory.");
        var sourcePath = Path.GetFullPath(Path.Combine(
            testsDirectory,
            "..",
            "..",
            "src",
            "Hbpos.Api",
            "Services",
            "InstallmentService.cs"));

        return File.ReadAllText(sourcePath);
    }

    private static void AssertNoSchemaSql(IEnumerable<string> statements)
    {
        Assert.NotEmpty(statements);
        Assert.DoesNotContain(statements, sql =>
            sql.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("PRAGMA table_info", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase));
    }

    private static InstallmentCreateRequest CreateRequest(
        decimal totalAmount = 100m,
        decimal downPaymentAmount = 20m,
        PaymentMethodKind method = PaymentMethodKind.Cash,
        string? reference = null,
        string? reservationToken = null)
    {
        return new InstallmentCreateRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            totalAmount,
            downPaymentAmount,
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Tea",
                    "9300001",
                    1m,
                    totalAmount,
                    0m,
                    totalAmount)
            ],
            new InstallmentPaymentCommandDto(Guid.NewGuid(), method, downPaymentAmount, reference, reservationToken),
            "Alice",
            "0400000000");
    }

    private static InstallmentAppendPaymentRequest CreatePayment(
        Guid installmentGuid,
        Guid paymentGuid,
        decimal amount,
        PaymentMethodKind method = PaymentMethodKind.Cash,
        string? idempotencyKey = null,
        string? reference = null,
        string? reservationToken = null)
    {
        return new InstallmentAppendPaymentRequest(
            installmentGuid,
            paymentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            amount,
            method,
            reference,
            reservationToken,
            IdempotencyKey: idempotencyKey);
    }

    private static StoreVoucher CreateVoucher(decimal remainingAmount)
    {
        return new StoreVoucher
        {
            ID = 1,
            StoreCode = "S01",
            VoucherCode = "V001",
            VoucherType = 3,
            Amount = remainingAmount,
            RemainingAmount = remainingAmount,
            Status = "1",
            ExpiredDate = DateTime.UtcNow.AddDays(3),
            DiscountRate = 0m,
            IsDelete = false
        };
    }

    private static InstallmentConfirmPickupRequest CreatePickup(Guid installmentGuid)
    {
        return new InstallmentConfirmPickupRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T10:00:00Z"));
    }

    private static InstallmentCancelRequest CreateCancel(
        Guid installmentGuid,
        IReadOnlyList<InstallmentRefundPaymentCommandDto> refunds)
    {
        return new InstallmentCancelRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T11:00:00Z"),
            refunds,
            "Customer cancelled");
    }

    private static InstallmentVoidRequest CreateVoid(Guid installmentGuid)
    {
        return new InstallmentVoidRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T11:30:00Z"),
            "Void without refund");
    }

    private sealed class InMemoryInstallmentRepository(HbposSqlSugarContext? dbContext = null) : IInstallmentRepository
    {
        private readonly Dictionary<Guid, InstallmentDetailsDto> details = [];
        private readonly Dictionary<Guid, Guid> paymentIndex = [];
        private readonly Dictionary<Guid, InstallmentLifecycleOperationFacts> pickupOperations = [];
        private readonly Dictionary<Guid, InstallmentLifecycleOperationFacts> voidOperations = [];

        public InstallmentHistoryQueryRequest? LastHistoryQuery { get; private set; }

        public async Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
        {
            if (dbContext is not null)
            {
                await RedeemVoucherPaymentsAsync(details.StoreCode, details.CashierId, details.Payments, cancellationToken);
            }

            this.details[details.InstallmentGuid] = details;
            foreach (var payment in details.Payments)
            {
                paymentIndex[payment.PaymentGuid] = details.InstallmentGuid;
            }
        }

        public async Task<InstallmentDetailsDto> AppendPaymentAsync(
            Guid installmentGuid,
            InstallmentPaymentDto payment,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid];
            if (!paymentIndex.ContainsKey(payment.PaymentGuid))
            {
                if (dbContext is not null && payment.Method == PaymentMethodKind.Voucher)
                {
                    await RedeemVoucherPaymentsAsync(current.StoreCode, payment.CashierId, [payment], cancellationToken);
                }

                paymentIndex[payment.PaymentGuid] = installmentGuid;
                var paidAmount = current.PaidAmount + payment.Amount;
                var balanceAmount = Math.Max(0m, current.TotalAmount - paidAmount);
                current = current with
                {
                    PaidAmount = paidAmount,
                    BalanceAmount = balanceAmount,
                    Status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active,
                    Payments = current.Payments.Concat([payment]).ToList()
                };
                details[installmentGuid] = current;
            }

            return current;
        }

        private async Task RedeemVoucherPaymentsAsync(
            string storeCode,
            string cashierId,
            IReadOnlyList<InstallmentPaymentDto> payments,
            CancellationToken cancellationToken)
        {
            var voucherPayments = payments
                .Where(payment => payment.Method == PaymentMethodKind.Voucher)
                .ToList();
            if (voucherPayments.Count == 0 || dbContext is null)
            {
                return;
            }

            await dbContext.PosmDb.Ado.BeginTranAsync();
            try
            {
                foreach (var payment in voucherPayments)
                {
                    await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                        dbContext.PosmDb,
                        payment.ReservationToken ?? string.Empty,
                        storeCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        payment.PaymentGuid.ToString("D"),
                        payment.RecordedAt,
                        cancellationToken);
                    await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                        dbContext.PosmDb,
                        storeCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        cashierId,
                        cancellationToken);
                }

                await dbContext.PosmDb.Ado.CommitTranAsync();
            }
            catch
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
                throw;
            }
        }

        public Task<InstallmentDetailsDto> ConfirmPickupAsync(
            Guid installmentGuid,
            DateTimeOffset pickedUpAt,
            string pickedUpBy,
            string? note,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid] with
            {
                Status = InstallmentStatus.PickedUp,
                PickupInfo = new InstallmentPickupInfoDto(pickedUpAt, pickedUpBy, note)
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> ConfirmPickupIdempotentAsync(
            Guid installmentGuid,
            DateTimeOffset pickedUpAt,
            string pickedUpBy,
            string? note,
            InstallmentLifecycleOperationFacts operation,
            CancellationToken cancellationToken)
        {
            if (pickupOperations.TryGetValue(installmentGuid, out var existing))
            {
                EnsureLifecycleReplayMatches(existing, operation, "pickup");
                return Task.FromResult(details[installmentGuid]);
            }

            pickupOperations[installmentGuid] = operation;
            return ConfirmPickupAsync(installmentGuid, pickedUpAt, pickedUpBy, note, cancellationToken);
        }

        public Task<InstallmentDetailsDto> CancelWithRefundAsync(
            Guid installmentGuid,
            IReadOnlyList<InstallmentPaymentDto> refunds,
            InstallmentCancellationInfoDto cancellationInfo,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid];
            foreach (var refund in refunds)
            {
                paymentIndex[refund.PaymentGuid] = installmentGuid;
            }

            var payments = current.Payments.Concat(refunds).ToList();
            var paidAmount = payments.Where(payment => payment.Status == InstallmentPaymentStatus.Recorded).Sum(payment => payment.Amount);
            current = current with
            {
                Status = InstallmentStatus.Cancelled,
                PaidAmount = paidAmount,
                BalanceAmount = 0m,
                Payments = payments,
                CancellationInfo = cancellationInfo
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> VoidAsync(
            Guid installmentGuid,
            InstallmentCancellationInfoDto cancellationInfo,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid] with
            {
                Status = InstallmentStatus.Cancelled,
                CancellationInfo = cancellationInfo
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> VoidIdempotentAsync(
            Guid installmentGuid,
            InstallmentCancellationInfoDto cancellationInfo,
            InstallmentLifecycleOperationFacts operation,
            CancellationToken cancellationToken)
        {
            if (voidOperations.TryGetValue(installmentGuid, out var existing))
            {
                EnsureLifecycleReplayMatches(existing, operation, "void");
                return Task.FromResult(details[installmentGuid]);
            }

            voidOperations[installmentGuid] = operation;
            return VoidAsync(installmentGuid, cancellationInfo, cancellationToken);
        }

        private static void EnsureLifecycleReplayMatches(
            InstallmentLifecycleOperationFacts existing,
            InstallmentLifecycleOperationFacts recovery,
            string action)
        {
            if (existing.OperationGuid != recovery.OperationGuid ||
                !string.Equals(existing.IdempotencyKey, recovery.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint, recovery.Fingerprint, StringComparison.Ordinal) ||
                !string.Equals(existing.ExecutingDeviceCode, recovery.ExecutingDeviceCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Installment {action} idempotency facts conflict with the existing operation.");
            }
        }

        public Task<InstallmentPaymentLookup?> FindPaymentAsync(Guid paymentGuid, CancellationToken cancellationToken)
        {
            if (!paymentIndex.TryGetValue(paymentGuid, out var installmentGuid))
            {
                return Task.FromResult<InstallmentPaymentLookup?>(null);
            }

            var payment = details[installmentGuid].Payments.Single(x => x.PaymentGuid == paymentGuid);
            return Task.FromResult<InstallmentPaymentLookup?>(new InstallmentPaymentLookup(installmentGuid, payment));
        }

        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
            Guid installmentGuid,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            var match = details.Values
                .Where(order => order.InstallmentGuid == installmentGuid)
                .SelectMany(order => order.Payments.Select(payment => new { order.InstallmentGuid, Payment = payment }))
                .FirstOrDefault(x => string.Equals(x.Payment.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            return Task.FromResult(match is null
                ? null
                : new InstallmentPaymentLookup(match.InstallmentGuid, match.Payment));
        }

        public Task<InstallmentHistoryQueryResponse> QueryAsync(
            InstallmentHistoryQueryRequest request,
            CancellationToken cancellationToken)
        {
            LastHistoryQuery = request;
            var query = details.Values
                .Where(order => string.Equals(order.StoreCode, request.StoreCode, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(request.DeviceCode))
            {
                query = query.Where(order => string.Equals(order.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase));
            }

            if (request.CreatedFrom is not null)
            {
                query = query.Where(order => order.CreatedAt >= request.CreatedFrom.Value);
            }

            if (request.CreatedTo is not null)
            {
                query = query.Where(order => order.CreatedAt <= request.CreatedTo.Value);
            }

            if (request.Status is not null)
            {
                query = query.Where(order => order.Status == request.Status.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                query = query.Where(order =>
                    order.InstallmentGuid.ToString("D").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.InstallmentNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            var orders = query
                .OrderByDescending(order => order.CreatedAt)
                .ThenByDescending(order => order.InstallmentGuid)
                .Skip(request.Skip)
                .Take(Math.Clamp(request.Take, 1, 200))
                .Select(order => new InstallmentSummaryDto(
                    order.InstallmentGuid,
                    order.InstallmentNumber,
                    order.StoreCode,
                    order.DeviceCode,
                    order.CashierName,
                    order.CustomerName,
                    order.CustomerPhone,
                    order.CreatedAt,
                    order.TotalAmount,
                    order.DownPaymentAmount,
                    order.PaidAmount,
                    order.BalanceAmount,
                    order.Status,
                    order.CreatedAt))
                .ToList();
            return Task.FromResult(new InstallmentHistoryQueryResponse(orders));
        }

        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken)
        {
            details.TryGetValue(installmentGuid, out var value);
            return Task.FromResult(value);
        }
    }

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        private readonly Dictionary<string, StoreVoucherReservation> reservations = [];

        public void Add(StoreVoucherReservation reservation)
        {
            reservations[reservation.Token] = reservation;
        }

        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            reservations.TryGetValue(token, out var reservation);
            return Task.FromResult(reservation);
        }

        public Task<StoreVoucherReservation> ReserveAsync(
            string storeCode,
            string voucherCode,
            decimal requestedAmount,
            decimal currentRemainingAmount,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<StoreVoucherReservation> ClaimAsync(
            string token,
            string storeCode,
            string voucherCode,
            decimal amount,
            string? consumedByReference,
            CancellationToken cancellationToken)
        {
            if (!reservations.TryGetValue(token, out var reservation) ||
                !string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase) ||
                reservation.LockedAmount < amount)
            {
                throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
            }

            reservations.Remove(token);
            return Task.FromResult(reservation);
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            reservations.Remove(token);
            return Task.CompletedTask;
        }

        public Task<bool> ReleaseAsync(
            string token,
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(reservations.Remove(token));
        }
    }

    private sealed class CapturingInstallmentHistoryService : IInstallmentHistoryService
    {
        public InstallmentHistoryQueryRequest? LastQuery { get; private set; }

        public Task<InstallmentHistoryQueryResponse> QueryAsync(
            InstallmentHistoryQueryRequest request,
            CancellationToken cancellationToken)
        {
            LastQuery = request;
            return Task.FromResult(new InstallmentHistoryQueryResponse([]));
        }

        public Task<InstallmentDetailsDto?> GetDetailsAsync(
            Guid installmentGuid,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<InstallmentDetailsDto?>(null);
        }
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class InstallmentSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-voucher-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private InstallmentSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<
                StoreVoucher,
                StoreVoucherReservationEntity,
                InstallmentOrderEntity,
                InstallmentOrderLineEntity,
                InstallmentPaymentEntity>();
            client.CodeFirst.InitTables<
                InstallmentRepaymentClaimEntity,
                InstallmentCancelClaimEntity>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public List<string> CaptureSql()
        {
            var statements = new List<string>();
            client.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);
            return statements;
        }

        public static Task<InstallmentSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new InstallmentSqliteFixture());
        }

        public Task SeedVoucherAsync(StoreVoucher voucher)
        {
            return client.Insertable(voucher).ExecuteCommandAsync();
        }

        public async Task<StoreVoucher?> GetVoucherAsync(string voucherCode)
        {
            return await client.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == voucherCode)
                .FirstAsync();
        }

        public async Task<StoreVoucherReservationEntity?> GetReservationEntityAsync(string token)
        {
            return await client.Queryable<StoreVoucherReservationEntity>()
                .Where(x => x.Token == token)
                .FirstAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
