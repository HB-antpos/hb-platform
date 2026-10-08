using System.Security.Cryptography;
using System.Text;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Api.Tests;

public sealed class DeviceServiceTests
{
    [Fact]
    public void FindLatestByHardwareIdAndStoreCodeSql_LocksTargetRangeAndSelectsLatestRecord()
    {
        var sql = SqlSugarDeviceRegistrationRepository.FindLatestByHardwareIdAndStoreCodeSql;

        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[设备硬件识别码] = @HardwareId", sql, StringComparison.Ordinal);
        Assert.Contains("[分店代码] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY [ID] DESC", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindByDeviceCodeSql_KeepsExactFiltersAndOrdersByHardwareThenLatest()
    {
        var sql = SqlSugarDeviceRegistrationRepository.FindByDeviceCodeSql;

        // 过滤条件保持设备号 + 门店精确匹配，硬件码只参与排序，不放宽命中范围。
        Assert.Contains("[系统设备编号] = @DeviceCode", sql, StringComparison.Ordinal);
        Assert.Contains("[分店代码] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE [设备硬件识别码]", sql, StringComparison.Ordinal);
        var orderByIndex = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        Assert.True(orderByIndex > 0);
        Assert.Contains("@HardwareId", sql[orderByIndex..], StringComparison.Ordinal);
        Assert.Contains("[ID] DESC", sql[orderByIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public void IsDeviceCodeTakenForRegistrationSql_LocksStoreDeviceCodeRange()
    {
        var sql = SqlSugarDeviceRegistrationRepository.IsDeviceCodeTakenForRegistrationSql;

        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[分店代码] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("[系统设备编号] = @DeviceCode", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[设备状态]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetRegistrationForReregisterSql_UsesSnapshotConcurrencyConditions()
    {
        var sql = SqlSugarDeviceRegistrationRepository.ResetRegistrationForReregisterSql;

        Assert.Contains("[ID] = @RegistrationId", sql, StringComparison.Ordinal);
        Assert.Contains("[设备硬件识别码] = @HardwareId", sql, StringComparison.Ordinal);
        Assert.Contains("[分店代码] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("[系统设备编号] = @DeviceCode", sql, StringComparison.Ordinal);
        Assert.Contains("[设备状态] = @ExpectedDeviceStatus", sql, StringComparison.Ordinal);
        Assert.Contains("[设备授权码] = @ExpectedAuthorizationCode", sql, StringComparison.Ordinal);
        Assert.Contains("[设备授权码] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("@ExpectedAuthorizationCode IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAllByHardwareIdForRegistrationSql_LocksWholeHardwareRange()
    {
        var sql = SqlSugarDeviceRegistrationRepository.FindAllByHardwareIdForRegistrationSql;

        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[设备硬件识别码] = @HardwareId", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY [ID] DESC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@StoreCode", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CountActiveOrLockedByStoreCodeForRegistrationSql_LocksReviewStoreRange()
    {
        var sql = SqlSugarDeviceRegistrationRepository.CountActiveOrLockedByStoreCodeForRegistrationSql;

        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[POSM_AppReviewGrantConsumptions]", sql, StringComparison.Ordinal);
        Assert.Contains("consumption.[StoreCode] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("[设备状态] IN (1, 2)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [POSM_设备注册信息表] WITH", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RegisterAsync_WhenTargetHasAnonymousReusableStatus_ReusesDeviceAndRefreshesAuthorization(int targetStatus)
    {
        var now = new DateTime(2026, 7, 10, 11, 0, 0);
        var target = new DeviceRegistrationRecord
        {
            Id = 11,
            DeviceCode = "POS_1003_0915",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = targetStatus,
            AuthorizationCode = "AUTH-OLD"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = target,
            RegistrationsForUpdate = [target]
        };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => now,
            utcNowProvider: () => new DateTimeOffset(now));

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1003_0915", response.DeviceCode);
        Assert.Equal("1003", response.StoreCode);
        Assert.Equal("Chermside", response.StoreName);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.CreatedRegistrations);
        var reset = Assert.Single(repository.ResetRequests);
        Assert.Equal(11, reset.RegistrationId);
        Assert.Equal(targetStatus, reset.ExpectedDeviceStatus);
        Assert.Equal("AUTH-OLD", reset.ExpectedAuthorizationCode);
        Assert.NotEqual("AUTH-OLD", reset.AuthorizationCode);
        Assert.Equal(now, reset.ModifiedAt);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetHasNoRegistration_CreatesPendingInsideTransaction()
    {
        var now = new DateTime(2026, 7, 10, 11, 1, 0);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => now,
            utcNowProvider: () => new DateTimeOffset(now));

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1003_1101", response.DeviceCode);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        var created = Assert.Single(repository.CreatedRegistrations);
        Assert.Equal(now, created.CreatedAt);
    }

    [Fact]
    public async Task RegisterAsync_WhenIpadPlatformIsRequested_CreatesIpadOsRegistration()
    {
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync, () => new DateTime(2026, 7, 10, 11, 1, 0));

        await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.Equal("iPadOS", Assert.Single(repository.CreatedRegistrations).DeviceSystem);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewGateAndProvisioningCodeAreValid_AutoApprovesNewIpad()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0);
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => now,
            CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal(1, response.DeviceStatus);
        Assert.NotNull(response.AuthorizationCode);
        var created = Assert.Single(repository.CreatedRegistrations);
        Assert.Equal(1, created.DeviceStatus);
        Assert.Equal("HBPOS_APP_REVIEW", created.CreatedBy);
        Assert.StartsWith("POS_1003_REVIEW_", created.DeviceCode, StringComparison.Ordinal);
        Assert.True(created.DeviceCode.Length <= 50);
        Assert.Contains("auto-approved", created.Remark, StringComparison.Ordinal);
        Assert.Contains("AppReviewGrant:4baf31b5792d49ef8cc2d38b486a28a7", created.Remark, StringComparison.Ordinal);
        Assert.Equal(1, repository.ActiveOrLockedCountCalls);
        var consumption = Assert.Single(repository.AppReviewGrantConsumptions);
        Assert.Equal(Guid.Parse("4baf31b5-792d-49ef-8cc2-d38b486a28a7"), consumption.GrantId);
        Assert.Equal("IPAD-REVIEW-001", consumption.HardwareId);
        Assert.Equal(created.DeviceCode, consumption.DeviceCode);
    }

    [Theory]
    [InlineData("WRONG-REVIEW-DEVICE-CODE")]
    [InlineData("SHORT-CODE")]
    [InlineData(null)]
    public async Task RegisterAsync_WhenReviewProvisioningCodeIsMissingOrInvalid_RejectsWithoutCreatingPending(string? provisioningCode)
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", provisioningCode),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Null(response.AuthorizationCode);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(0, repository.ActiveOrLockedCountCalls);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewGateIsExpired_RejectsWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Equal(0, repository.ActiveOrLockedCountCalls);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewDeviceLimitIsReached_RejectsWithoutCreatingPending()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository { ActiveOrLockedCount = 1 };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-002", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal(1, repository.ActiveOrLockedCountCalls);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewCodeMatchesExistingPendingRegistration_ApprovesExactSnapshot()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var pending = new DeviceRegistrationRecord
        {
            Id = 91,
            DeviceCode = "POS_1003_1200",
            StoreCode = "1003",
            HardwareId = "IPAD-REVIEW-001",
            DeviceStatus = -1,
            AuthorizationCode = "AUTH-PENDING",
            DeviceSystem = "iPadOS"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [pending]
        };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal("POS_1003_1200", response.DeviceCode);
        Assert.NotEqual("AUTH-PENDING", response.AuthorizationCode);
        Assert.Empty(repository.CreatedRegistrations);
        var approval = Assert.Single(repository.AppReviewApprovalRequests);
        Assert.Equal(91, approval.RegistrationId);
        Assert.Equal(-1, approval.ExpectedDeviceStatus);
        Assert.Equal("AUTH-PENDING", approval.ExpectedAuthorizationCode);
        Assert.Equal("iPadOS", approval.DeviceSystem);
        Assert.Single(repository.AppReviewGrantConsumptions);
    }

    [Fact]
    public async Task RegisterForAppReviewAsync_WhenPlatformIsNotIpad_RejectsWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "WINDOWS-001", "Counter", "Windows", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Equal(0, repository.ActiveOrLockedCountCalls);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterForAppReviewAsync_WhenWindowExpiresWhileWaitingForGrantLock_RejectsWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var expiresAtUtc = utcNow.AddSeconds(1);
        var repository = new FakeDeviceRegistrationRepository
        {
            OnAcquireAppReviewGrantLock = () => utcNow = expiresAtUtc
        };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", expiresAtUtc),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.AppReviewApprovalRequests);
        Assert.Empty(repository.AppReviewGrantConsumptions);
    }

    [Fact]
    public async Task RegisterForAppReviewAsync_AcquiresStoreGrantLockBeforeHardwareRangeLock()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal(
            ["app-review-lock", "hardware-range"],
            repository.RegistrationOperationOrder.Take(2));
    }

    [Fact]
    public async Task RegisterForAppReviewAsync_WhenStoreCodeIsLong_PreservesRandomDeviceCodeSuffix()
    {
        const string storeCode = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var options = CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7));
        options.StoreCode = storeCode;
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            (code, _) => Task.FromResult<DeviceStoreInfo?>(new DeviceStoreInfo(code, "Long Store")),
            appReviewOptions: options,
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest(storeCode, "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Matches("^POS_A{6}_REVIEW_[0-9a-f]{32}$", response.DeviceCode);
        Assert.Equal(50, response.DeviceCode.Length);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewGrantWasAlreadyConsumedByAnotherDevice_RejectsWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository
        {
            AppReviewGrantConsumption = new DeviceRegistrationAppReviewGrantConsumption
            {
                GrantId = Guid.Parse("4baf31b5-792d-49ef-8cc2-d38b486a28a7"),
                StoreCode = "1003",
                HardwareId = "IPAD-REVIEW-001",
                DeviceCode = "POS_1003_1200",
                ConsumedAtUtc = utcNow.AddMinutes(-1).UtcDateTime
            }
        };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-002", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenExistingReviewDeviceWasDisabled_DoesNotReenableIt()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var disabled = new DeviceRegistrationRecord
        {
            Id = 92,
            DeviceCode = "POS_1003_1201",
            StoreCode = "1003",
            HardwareId = "IPAD-REVIEW-001",
            DeviceStatus = 0,
            AuthorizationCode = "AUTH-DISABLED",
            DeviceSystem = "iPadOS"
        };
        var repository = new FakeDeviceRegistrationRepository { RegistrationsForUpdate = [disabled] };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Empty(repository.AppReviewApprovalRequests);
        Assert.Empty(repository.ResetRequests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RegisterForAppReviewAsync_WhenApprovedResponseWasLost_RecoversAfterExpiryOrDisable(
        bool enabled,
        bool expired)
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var active = new DeviceRegistrationRecord
        {
            Id = 93,
            DeviceCode = "POS_1003_1202",
            StoreCode = "1003",
            HardwareId = "IPAD-REVIEW-001",
            DeviceStatus = 1,
            AuthorizationCode = "AUTH-RECOVERABLE",
            DeviceSystem = "iPadOS"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [active],
            AppReviewGrantConsumption = new DeviceRegistrationAppReviewGrantConsumption
            {
                GrantId = Guid.Parse("4baf31b5-792d-49ef-8cc2-d38b486a28a7"),
                StoreCode = "1003",
                HardwareId = "IPAD-REVIEW-001",
                DeviceCode = "POS_1003_1202",
                ConsumedAtUtc = utcNow.AddMinutes(-1).UtcDateTime
            }
        };
        var options = CreateReviewOptions(
            "OPEN-REVIEW-DEVICE",
            expired ? utcNow : utcNow.AddDays(7));
        options.Enabled = enabled;
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: options,
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", " iPaDoS ", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal("AUTH-RECOVERABLE", response.AuthorizationCode);
        Assert.Equal("POS_1003_1202", response.DeviceCode);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.AppReviewGrantConsumptions);
    }

    [Fact]
    public async Task RegisterAsync_WhenReviewWindowIsOpen_RequiresDedicatedEndpointWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "IPAD-REVIEW-001", "App Review", "iPadOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterForAppReviewAsync_WhenStoreDoesNotMatch_RejectsWithoutWrites()
    {
        var utcNow = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            appReviewOptions: CreateReviewOptions("OPEN-REVIEW-DEVICE", utcNow.AddDays(7)),
            utcNowProvider: () => utcNow);

        var response = await service.RegisterForAppReviewAsync(
            new DeviceRegisterRequest("1002", "IPAD-REVIEW-001", "App Review", "iPadOS", "OPEN-REVIEW-DEVICE"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(3, response.DeviceStatus);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task VerifyAsync_WhenIpadHardwareIdIsMissing_RejectsTheRequest()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", null, "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Device hardware id is required for iPadOS.", response.Message);
        Assert.Null(response.AuthorizationCode);
    }

    [Fact]
    public async Task VerifyAsync_WhenLegacyWindowsRequestOmitsHardwareId_PreservesExistingCompatibility()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                DeviceSystem = "Windows",
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003"),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal("AUTH-001", response.AuthorizationCode);
        Assert.False(response.ExactIdentityMatched);
    }

    [Fact]
    public async Task VerifyAsync_WhenIpadDeviceCodeIsReused_uses_the_latest_exact_hardware_record()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-OTHER",
                DeviceStatus = 1,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-OTHER"
            },
            DeviceByExactIdentity = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-EXPECTED",
                DeviceStatus = 0,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-EXPECTED"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", "HW-EXPECTED", "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.Equal(0, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.True(response.ExactIdentityMatched);
    }

    [Theory]
    [InlineData("iOS", 1, true)]
    [InlineData("Android", 1, true)]
    [InlineData("iOS", 0, false)]
    [InlineData("Android", 0, false)]
    public async Task VerifyAsync_WhenHandheldExactHardwareMatches_reports_exact_identity_for_reset_recovery(
        string deviceSystem,
        int deviceStatus,
        bool expectedAllowed)
    {
        // 手持 iOS/Android 与 iPadOS 一样按门店+设备码+硬件码精确查询，
        // 必须给出精确身份证明，否则「清除设备注册」中断后启动恢复永远无法确认服务端停用。
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByExactIdentity = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-EXPECTED",
                DeviceStatus = deviceStatus,
                DeviceSystem = deviceSystem,
                AuthorizationCode = "AUTH-EXPECTED"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest(
                "POS_1003_1011",
                "1003",
                "HW-EXPECTED",
                "handheld",
                deviceSystem),
            CancellationToken.None);

        Assert.Equal(deviceStatus, response.DeviceStatus);
        Assert.Equal(expectedAllowed, response.IsAllowed);
        Assert.Equal(expectedAllowed ? "AUTH-EXPECTED" : null, response.AuthorizationCode);
        Assert.True(response.ExactIdentityMatched);
    }

    [Fact]
    public async Task VerifyAsync_WhenWindowsHardwareMatches_never_reports_exact_identity()
    {
        // Windows 只按门店+设备码查询（兼容旧客户端），不是精确硬件身份，不能给出恢复证明。
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 0,
                DeviceSystem = "Windows",
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", "HW-001", "front counter", "Windows"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.False(response.ExactIdentityMatched);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    public async Task VerifyAsync_WhenHandheldRegisteredOnAnotherPlatform_never_reports_exact_identity(
        string deviceSystem)
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByExactIdentity = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-EXPECTED",
                DeviceStatus = 0,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-EXPECTED"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", "HW-EXPECTED", "handheld", deviceSystem),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Device system does not match existing registration.", response.Message);
        Assert.False(response.ExactIdentityMatched);
    }

    [Fact]
    public async Task VerifyAsync_WhenIpadHardwareDoesNotMatch_never_reports_an_exact_identity_match()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-REGISTERED",
                DeviceStatus = 0,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-REGISTERED"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", "HW-OTHER", "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.False(response.ExactIdentityMatched);
    }

    [Fact]
    public async Task VerifyAsync_WhenDatabasePlatformIsUnknown_RejectsInsteadOfFallingBackToWindows()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                DeviceSystem = "watchOS",
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Registered device system is invalid.", response.Message);
        Assert.Null(response.AuthorizationCode);
    }

    [Fact]
    public async Task VerifyAsync_WhenSubmittedPlatformDoesNotMatchRegistration_Rejects()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceByCode = new DeviceRegistrationRecord
            {
                DeviceCode = "POS_1003_1011",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 1,
                DeviceSystem = "iPadOS",
                AuthorizationCode = "AUTH-001"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1011", "1003", "HW-001"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Device system does not match existing registration.", response.Message);
        Assert.Null(response.AuthorizationCode);
        Assert.False(response.ExactIdentityMatched);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RegisterAsync_WhenAnyRegistrationIsActiveOrLocked_RejectsWithoutWrites(int blockingStatus)
    {
        var targetPending = new DeviceRegistrationRecord
        {
            Id = 20,
            DeviceCode = "POS_1003_PENDING",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var blocking = new DeviceRegistrationRecord
        {
            Id = 19,
            DeviceCode = "POS_BLOCKING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = blockingStatus,
            AuthorizationCode = "AUTH-SECRET"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = targetPending,
            RegistrationsForUpdate = [targetPending, blocking]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_BLOCKING", response.DeviceCode);
        Assert.Equal("1002", response.StoreCode);
        Assert.Equal(blockingStatus, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetPendingExists_ReturnsItIdempotentlyWithoutWrites()
    {
        var targetPending = new DeviceRegistrationRecord
        {
            Id = 30,
            DeviceCode = "POS_1003_PENDING",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1,
            AuthorizationCode = "AUTH-PENDING"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = targetPending,
            RegistrationsForUpdate = [targetPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1003_PENDING", response.DeviceCode);
        Assert.Equal("1003", response.StoreCode);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenPendingPlatformDoesNotMatch_RejectsBeforeAnyWrite()
    {
        var targetPending = new DeviceRegistrationRecord
        {
            Id = 31,
            DeviceCode = "POS_1003_IPAD",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1,
            DeviceSystem = "iPadOS"
        };
        var otherPending = new DeviceRegistrationRecord
        {
            Id = 30,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1,
            DeviceSystem = "Windows"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetPending, otherPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Device system does not match existing registration.", response.Message);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenDisabledPlatformDoesNotMatch_DoesNotOverwriteServerPlatform()
    {
        var targetDisabled = new DeviceRegistrationRecord
        {
            Id = 32,
            DeviceCode = "POS_1003_WINDOWS",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = 0,
            DeviceSystem = "Windows"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetDisabled]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Device system does not match existing registration.", response.Message);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenDisabledIpadPlatformMatches_PreservesServerPlatformOnReset()
    {
        var targetDisabled = new DeviceRegistrationRecord
        {
            Id = 33,
            DeviceCode = "POS_1003_IPAD",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = 0,
            DeviceSystem = "iPadOS"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetDisabled]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "iPad counter", "iPadOS"),
            CancellationToken.None);

        Assert.Equal("iPadOS", Assert.Single(repository.ResetRequests).DeviceSystem);
    }

    [Fact]
    public async Task RegisterAsync_WhenLatestPendingBelongsToAnotherStore_DisablesItAndCreatesTargetPending()
    {
        var oldPending = new DeviceRegistrationRecord
        {
            Id = 40,
            DeviceCode = "POS_1002_1011",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = oldPending,
            RegistrationsForUpdate = [oldPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("1003", response.StoreCode);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.Null(response.AuthorizationCode);
        Assert.Equal(1, repository.TransactionCallCount);
        var disabled = Assert.Single(repository.DisabledRequests);
        Assert.Equal("POS_1002_1011", disabled.DeviceCode);
        Assert.Single(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTwoOtherStorePendingsExist_DisablesBothBeforeCreatingTarget()
    {
        var newestPending = new DeviceRegistrationRecord
        {
            Id = 48,
            DeviceCode = "POS_1002_NEWER",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var olderPending = new DeviceRegistrationRecord
        {
            Id = 47,
            DeviceCode = "POS_1004_OLDER",
            StoreCode = "1004",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [newestPending, olderPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal(
            ["POS_1002_NEWER", "POS_1004_OLDER"],
            repository.DisabledRequests.Select(static request => request.DeviceCode));
        Assert.Equal(2, repository.DisabledPendingCount);
        Assert.Empty(repository.ResetRequests);
        Assert.Single(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetDisabledAndTwoOtherStorePendingsExist_DisablesBothBeforeResettingTarget()
    {
        var targetDisabled = new DeviceRegistrationRecord
        {
            Id = 60,
            DeviceCode = "POS_1003_DISABLED",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = 0,
            AuthorizationCode = "AUTH-OLD"
        };
        var newestPending = new DeviceRegistrationRecord
        {
            Id = 59,
            DeviceCode = "POS_1002_NEWER",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var olderPending = new DeviceRegistrationRecord
        {
            Id = 58,
            DeviceCode = "POS_1004_OLDER",
            StoreCode = "1004",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetDisabled, newestPending, olderPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1003_DISABLED", response.DeviceCode);
        Assert.Equal(
            ["POS_1002_NEWER", "POS_1004_OLDER"],
            repository.DisabledRequests.Select(static request => request.DeviceCode));
        Assert.Equal(2, repository.DisabledPendingCount);
        Assert.Single(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetLatestPendingExists_DisablesEveryOtherPendingAndKeepsTarget()
    {
        var targetLatestPending = new DeviceRegistrationRecord
        {
            Id = 70,
            DeviceCode = "POS_1003_LATEST",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var targetOlderPending = new DeviceRegistrationRecord
        {
            Id = 69,
            DeviceCode = "POS_1003_OLDER",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var otherStorePending = new DeviceRegistrationRecord
        {
            Id = 68,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetLatestPending, targetOlderPending, otherStorePending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal("POS_1003_LATEST", response.DeviceCode);
        Assert.Equal(
            ["POS_1003_OLDER", "POS_1002_PENDING"],
            repository.DisabledRequests.Select(static request => request.DeviceCode));
        Assert.Equal(2, repository.DisabledPendingCount);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenAnyPendingDisableMisses_RollsBackAllWritesAndDoesNotCreateOrReset()
    {
        var newestPending = new DeviceRegistrationRecord
        {
            Id = 80,
            DeviceCode = "POS_1002_NEWER",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var olderPending = new DeviceRegistrationRecord
        {
            Id = 79,
            DeviceCode = "POS_1004_OLDER",
            StoreCode = "1004",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [newestPending, olderPending],
            DisablePendingRowsAffectedSequence = [1, 0]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None));

        Assert.Equal(2, repository.DisabledRequests.Count);
        Assert.Equal(0, repository.DisabledPendingCount);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetStatusIsUnknown_RejectsBeforeDisablingAnyPending()
    {
        var targetUnknown = new DeviceRegistrationRecord
        {
            Id = 90,
            DeviceCode = "POS_1003_UNKNOWN",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = 4
        };
        var otherPending = new DeviceRegistrationRecord
        {
            Id = 89,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetUnknown, otherPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal(4, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RegisterAsync_WhenReusableTargetHasEmptyDeviceCode_RejectsBeforeDisablingAnyPending(int targetStatus)
    {
        var targetWithoutCode = new DeviceRegistrationRecord
        {
            Id = 100,
            DeviceCode = string.Empty,
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = targetStatus
        };
        var otherPending = new DeviceRegistrationRecord
        {
            Id = 99,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetWithoutCode, otherPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal(targetStatus, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetPendingHasEmptyDeviceCode_RejectsBeforeDisablingAnyPending()
    {
        var targetPendingWithoutCode = new DeviceRegistrationRecord
        {
            Id = 110,
            DeviceCode = string.Empty,
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var otherPending = new DeviceRegistrationRecord
        {
            Id = 109,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [targetPendingWithoutCode, otherPending]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None);

        Assert.Equal(0, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Empty(repository.DisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task RegisterAsync_WhenTargetResetSnapshotMisses_RollsBackOldPendingDisable()
    {
        var oldPending = new DeviceRegistrationRecord
        {
            Id = 50,
            DeviceCode = "POS_1002_PENDING",
            StoreCode = "1002",
            HardwareId = "HW-001",
            DeviceStatus = -1
        };
        var targetDisabled = new DeviceRegistrationRecord
        {
            Id = 49,
            DeviceCode = "POS_1003_DISABLED",
            StoreCode = "1003",
            HardwareId = "HW-001",
            DeviceStatus = 0,
            AuthorizationCode = "AUTH-OLD"
        };
        var repository = new FakeDeviceRegistrationRepository
        {
            LatestByHardwareId = oldPending,
            RegistrationsForUpdate = [oldPending, targetDisabled],
            ResetRowsAffected = 0
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 2"),
            CancellationToken.None));

        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Single(repository.DisabledRequests);
        Assert.Single(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.True(repository.PendingRegistrationEnabled);
    }

    [Fact]
    public async Task ReregisterAsync_WhenTargetStoreHasDisabledRegistration_ReusesDeviceCodeAndRefreshesRegistration()
    {
        var now = new DateTime(2026, 7, 10, 10, 15, 0);
        var repository = new FakeDeviceRegistrationRepository
        {
            TargetRegistration = new DeviceRegistrationRecord
            {
                Id = 42,
                DeviceCode = "POS_1003_0915",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 0,
                AuthorizationCode = "AUTH-OLD"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync, () => now);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None);

        Assert.Equal("POS_1003_0915", response.DeviceCode);
        Assert.Equal("1003", response.StoreCode);
        Assert.Equal("Chermside", response.StoreName);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.False(response.IsAllowed);
        Assert.Null(response.AuthorizationCode);
        Assert.Single(repository.ActiveDisabledRequests);
        Assert.Single(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);

        var reset = repository.ResetRequests[0];
        Assert.Equal(42, reset.RegistrationId);
        Assert.Equal("HW-001", reset.HardwareId);
        Assert.Equal("1003", reset.StoreCode);
        Assert.Equal("POS_1003_0915", reset.DeviceCode);
        Assert.Equal(0, reset.ExpectedDeviceStatus);
        Assert.Equal("AUTH-OLD", reset.ExpectedAuthorizationCode);
        Assert.NotEmpty(reset.AuthorizationCode);
        Assert.NotEqual("AUTH-OLD", reset.AuthorizationCode);
        Assert.Equal(now, reset.ModifiedAt);
        Assert.Equal("HBPOS_CLIENT", reset.ModifiedBy);
        Assert.Contains("Counter 2", reset.RemarkSuffix, StringComparison.Ordinal);
        Assert.False(repository.CurrentRegistrationEnabled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReregisterAsync_WhenTargetStoreHasRegistrationInAnyOtherStatus_ReusesAndResetsIt(int targetStatus)
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            TargetRegistration = new DeviceRegistrationRecord
            {
                Id = 43,
                DeviceCode = "POS_1003_0916",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = targetStatus,
                AuthorizationCode = "AUTH-OLD"
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync, () => new DateTime(2026, 7, 10, 10, 16, 0));

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None);

        Assert.Equal("POS_1003_0916", response.DeviceCode);
        Assert.Equal(-1, response.DeviceStatus);
        Assert.Single(repository.ResetRequests);
        Assert.Equal(targetStatus, repository.ResetRequests[0].ExpectedDeviceStatus);
        Assert.Equal("AUTH-OLD", repository.ResetRequests[0].ExpectedAuthorizationCode);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ReregisterAsync_WhenTargetAuthorizationCodeIsNullOrEmpty_PreservesExpectedSnapshot(string? authorizationCode)
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            TargetRegistration = new DeviceRegistrationRecord
            {
                Id = 46,
                DeviceCode = "POS_1003_0919",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 2,
                AuthorizationCode = authorizationCode
            }
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None);

        var reset = Assert.Single(repository.ResetRequests);
        Assert.Equal(authorizationCode, reset.ExpectedAuthorizationCode);
    }

    [Fact]
    public async Task ReregisterAsync_WhenTargetStoreHasNoRegistration_CreatesNewPendingRegistration()
    {
        var now = new DateTime(2026, 7, 10, 10, 17, 0);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync, () => now);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None);

        Assert.Equal("POS_1003_1017", response.DeviceCode);
        Assert.Single(repository.ActiveDisabledRequests);
        Assert.Empty(repository.ResetRequests);
        var created = Assert.Single(repository.CreatedRegistrations);
        Assert.Equal("POS_1003_1017", created.DeviceCode);
        Assert.Equal("1003", created.StoreCode);
        Assert.Equal("HW-001", created.HardwareId);
        Assert.Equal(-1, created.DeviceStatus);
        Assert.Equal(now, created.CreatedAt);
    }

    [Fact]
    public async Task ReregisterAsync_InheritsIpadOsPlatformFromAuthenticatedDeviceContext()
    {
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync);

        await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "iPad counter"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001", "iPadOS"),
            CancellationToken.None);

        Assert.Equal("iPadOS", Assert.Single(repository.CreatedRegistrations).DeviceSystem);
    }

    [Fact]
    public async Task ReregisterAsync_WhenAuthenticatedPlatformIsUnknown_RejectsWithoutWrites()
    {
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001", "watchOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Current device system is invalid.", response.Message);
        Assert.Empty(repository.ActiveDisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
    }

    [Fact]
    public async Task ReregisterAsync_WhenCurrentRegistrationCannotBeDisabled_ThrowsBeforeTargetWriteAndRollsBack()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            TargetRegistration = new DeviceRegistrationRecord
            {
                Id = 44,
                DeviceCode = "POS_1003_0917",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 0
            },
            DisableActiveRowsAffected = 0
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None));

        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Single(repository.ActiveDisabledRequests);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.True(repository.CurrentRegistrationEnabled);
    }

    [Fact]
    public async Task ReregisterAsync_WhenTargetRegistrationCannotBeReset_ThrowsAndRollsBackCurrentDisable()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            TargetRegistration = new DeviceRegistrationRecord
            {
                Id = 45,
                DeviceCode = "POS_1003_0918",
                StoreCode = "1003",
                HardwareId = "HW-001",
                DeviceStatus = 0
            },
            ResetRowsAffected = 0
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None));

        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Single(repository.ActiveDisabledRequests);
        Assert.Single(repository.ResetRequests);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.True(repository.CurrentRegistrationEnabled);
    }

    [Fact]
    public async Task ResetRegistrationAsync_DisablesOnlyAuthenticatedCurrentDeviceInsideTransaction()
    {
        var modifiedAt = new DateTime(2026, 8, 18, 12, 30, 0, DateTimeKind.Unspecified);
        var disabledAtUtc = DateTimeOffset.Parse("2026-08-18T02:30:00Z");
        var operationId = Guid.Parse("36d6605c-1e25-4fd1-b345-bec1c4ffad31");
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => modifiedAt,
            utcNowProvider: () => disabledAtUtc);

        var result = await service.ResetRegistrationAsync(
            new DeviceRegistrationResetRequest(operationId),
            new DeviceRegistrationResetContext(
                "POS_1042_0247",
                "1042",
                "INSTALL-1042",
                "CASHIER-HGUID"),
            CancellationToken.None);

        Assert.Equal(operationId, result.OperationId);
        Assert.Equal("POS_1042_0247", result.DeviceCode);
        Assert.Equal("1042", result.StoreCode);
        Assert.Equal(disabledAtUtc.UtcDateTime, result.DisabledAtUtc);
        Assert.Equal(1, repository.TransactionCallCount);
        var reset = Assert.Single(repository.ResetActiveRequests);
        Assert.Equal("INSTALL-1042", reset.HardwareId);
        Assert.Equal("POS_1042_0247", reset.DeviceCode);
        Assert.Equal("1042", reset.StoreCode);
        Assert.Equal("CASHIER-HGUID", reset.ModifiedBy);
        Assert.Equal(modifiedAt, reset.ModifiedAtUtc);
        Assert.Contains(operationId.ToString("D"), reset.RemarkSuffix, StringComparison.Ordinal);
        Assert.DoesNotContain("9900000000001", reset.RemarkSuffix, StringComparison.Ordinal);
        Assert.False(repository.CurrentRegistrationEnabled);
    }

    [Fact]
    public async Task ResetRegistrationAsync_WhenExactEnabledRegistrationChanged_ThrowsAndRollsBack()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DisableActiveRowsAffected = 0
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetRegistrationAsync(
            new DeviceRegistrationResetRequest(Guid.NewGuid()),
            new DeviceRegistrationResetContext(
                "POS_1042_0247",
                "1042",
                "INSTALL-1042",
                "CASHIER-HGUID"),
            CancellationToken.None));

        Assert.Equal(1, repository.TransactionCallCount);
        Assert.Single(repository.ResetActiveRequests);
        Assert.True(repository.CurrentRegistrationEnabled);
    }

    [Fact]
    public async Task UpdateRuntimeStatusAsync_KeepsSameCashierLoginTimeAndClearsEmptyCashier()
    {
        var firstNow = new DateTime(2026, 7, 1, 10, 0, 0);
        var secondNow = new DateTime(2026, 7, 1, 10, 1, 0);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync, () => firstNow);

        var firstResult = await service.UpdateRuntimeStatusAsync(
            "HW-001",
            "POS-001",
            "1002",
            true,
            "CASHIER-1",
            "Alice",
            null,
            CancellationToken.None);
        var secondResult = await new DeviceService(repository, LoadStoreAsync, () => secondNow)
            .UpdateRuntimeStatusAsync(
                "HW-001",
                "POS-001",
                "1002",
                true,
                "CASHIER-1",
                "Alice",
                null,
                CancellationToken.None);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.Equal(firstNow, repository.LastRuntimeStatus!.CashierLoginAt);
        Assert.Equal(secondNow, repository.LastRuntimeStatus.LastHeartbeatAt);
        Assert.Equal("CASHIER-1", repository.LastRuntimeStatus.CashierId);

        var clearResult = await new DeviceService(repository, LoadStoreAsync, () => secondNow.AddMinutes(1))
            .UpdateRuntimeStatusAsync(
                "HW-001",
                "POS-001",
                "1002",
                false,
                null,
                null,
                null,
                CancellationToken.None);

        Assert.True(clearResult);
        Assert.False(repository.LastRuntimeStatus!.IsOnline);
        Assert.Null(repository.LastRuntimeStatus.CashierId);
        Assert.Null(repository.LastRuntimeStatus.CashierName);
        Assert.Null(repository.LastRuntimeStatus.CashierLoginAt);
    }

    [Fact]
    public async Task UpdateRuntimeStatusAsync_StoresTrimmedAppVersionAndKeepsItWhenLaterReportOmitsIt()
    {
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(repository, LoadStoreAsync, () => new DateTime(2026, 10, 8, 10, 0, 0));

        await service.UpdateRuntimeStatusAsync(
            "HW-001", "POS-001", "1002", true, null, null, "  1.0.51  ", CancellationToken.None);
        Assert.Equal("1.0.51", repository.LastRuntimeStatus!.AppVersion);

        // 旧版客户端的心跳不带版本：库里已有的版本必须保留，不能被清空。
        await service.UpdateRuntimeStatusAsync(
            "HW-001", "POS-001", "1002", true, null, null, null, CancellationToken.None);
        Assert.Equal("1.0.51", repository.LastRuntimeStatus!.AppVersion);

        // 升级后上报新版本：覆盖旧值。
        await service.UpdateRuntimeStatusAsync(
            "HW-001", "POS-001", "1002", true, null, null, "1.0.52", CancellationToken.None);
        Assert.Equal("1.0.52", repository.LastRuntimeStatus!.AppVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAppVersion_TreatsBlankAsNotReported(string? value)
    {
        Assert.Null(DeviceService.NormalizeAppVersion(value));
    }

    [Fact]
    public void NormalizeAppVersion_AcceptsFiftyCharactersAndDropsLongerValues()
    {
        var fifty = new string('1', 50);

        Assert.Equal(fifty, DeviceService.NormalizeAppVersion(fifty));
        // 超出列宽的值直接丢弃，不截断成半截版本号。
        Assert.Null(DeviceService.NormalizeAppVersion(fifty + "1"));
    }

    [Fact]
    public async Task RegisterAsync_WhenSameStoreRegistersTwoDevicesInSameMinute_AllocatesDistinctDeviceCodes()
    {
        var now = new DateTime(2026, 7, 10, 11, 1, 0);
        var repository = new FakeDeviceRegistrationRepository();
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => now,
            utcNowProvider: () => new DateTimeOffset(now));

        var first = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-001", "Counter 1"),
            CancellationToken.None);
        var second = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-002", "Counter 2"),
            CancellationToken.None);

        // 同一门店同一 HH:mm 注册的两台设备必须拿到不同设备号，否则 Windows verify 可能命中别人的记录。
        Assert.Equal("POS_1003_1101", first.DeviceCode);
        Assert.Equal("POS_1003_1101_2", second.DeviceCode);
        Assert.Equal(
            ["POS_1003_1101", "POS_1003_1101_2"],
            repository.CreatedRegistrations.Select(item => item.DeviceCode).ToArray());
    }

    [Fact]
    public async Task RegisterAsync_WhenHistoricalDeviceUsedSameHourMinute_SkipsTakenDeviceCode()
    {
        var now = new DateTime(2026, 10, 5, 11, 1, 0);
        var repository = new FakeDeviceRegistrationRepository
        {
            // 另一天、另一平台注册的设备号同样占用该门店的 HH:mm 槽位。
            ExistingDeviceCodes = [("1003", "POS_1003_1101")]
        };
        var service = new DeviceService(
            repository,
            LoadStoreAsync,
            () => now,
            utcNowProvider: () => new DateTimeOffset(now));

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-003", "Counter 3", DeviceSystems.IpadOs),
            CancellationToken.None);

        Assert.Equal("POS_1003_1101_2", response.DeviceCode);
        Assert.Equal("POS_1003_1101_2", Assert.Single(repository.CreatedRegistrations).DeviceCode);
    }

    [Fact]
    public async Task ReregisterAsync_WhenTargetStoreNeedsNewDeviceCode_AvoidsTakenDeviceCode()
    {
        var now = new DateTime(2026, 7, 10, 10, 17, 0);
        var repository = new FakeDeviceRegistrationRepository
        {
            ExistingDeviceCodes = [("1003", "POS_1003_1017"), ("1002", "POS_1002_1017_2")]
        };
        var service = new DeviceService(repository, LoadStoreAsync, () => now);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 2"),
            new DeviceReregisterContext("POS_1002_0800", "1002", "HW-001"),
            CancellationToken.None);

        // 目标门店的同号已被其他设备占用，只按目标门店去重，其他门店的同名号码不影响。
        Assert.Equal("POS_1003_1017_2", response.DeviceCode);
        var created = Assert.Single(repository.CreatedRegistrations);
        Assert.Equal("POS_1003_1017_2", created.DeviceCode);
        Assert.Equal("1003", created.StoreCode);
    }

    [Fact]
    public async Task VerifyAsync_WhenWindowsDeviceCodeHasDuplicateRows_UsesRowMatchingHardwareId()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            // 模拟历史撞号：其他设备的记录更新且物理顺序靠前。
            DeviceRowsByCode =
            [
                new DeviceRegistrationRecord
                {
                    Id = 12,
                    DeviceCode = "POS_1003_1101",
                    StoreCode = "1003",
                    HardwareId = "HW-OTHER",
                    DeviceStatus = 1,
                    DeviceSystem = "Windows",
                    AuthorizationCode = "AUTH-OTHER"
                },
                new DeviceRegistrationRecord
                {
                    Id = 7,
                    DeviceCode = "POS_1003_1101",
                    StoreCode = "1003",
                    HardwareId = "HW-001",
                    DeviceStatus = 1,
                    DeviceSystem = "Windows",
                    AuthorizationCode = "AUTH-001"
                }
            ]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1101", "1003", " hw-001 ", DeviceSystem: DeviceSystems.Windows),
            CancellationToken.None);

        Assert.True(response.IsAllowed);
        Assert.Equal(1, response.DeviceStatus);
        Assert.Equal("AUTH-001", response.AuthorizationCode);
        Assert.Equal("hw-001", repository.LastPreferredHardwareId);
    }

    [Fact]
    public async Task VerifyAsync_WhenLegacyWindowsRequestOmitsHardwareIdForDuplicateRows_UsesLatestRow()
    {
        var repository = new FakeDeviceRegistrationRepository
        {
            DeviceRowsByCode =
            [
                new DeviceRegistrationRecord
                {
                    Id = 7,
                    DeviceCode = "POS_1003_1101",
                    StoreCode = "1003",
                    HardwareId = "HW-001",
                    DeviceStatus = 0,
                    DeviceSystem = "Windows",
                    AuthorizationCode = "AUTH-001"
                },
                new DeviceRegistrationRecord
                {
                    Id = 12,
                    DeviceCode = "POS_1003_1101",
                    StoreCode = "1003",
                    HardwareId = "HW-002",
                    DeviceStatus = 1,
                    DeviceSystem = "Windows",
                    AuthorizationCode = "AUTH-002"
                }
            ]
        };
        var service = new DeviceService(repository, LoadStoreAsync);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1101", "1003"),
            CancellationToken.None);

        // 旧客户端不带硬件码时，确定性地取最新一行，而不是随机行。
        Assert.True(response.IsAllowed);
        Assert.Equal("AUTH-002", response.AuthorizationCode);
    }

    private static Task<DeviceStoreInfo?> LoadStoreAsync(string storeCode, CancellationToken cancellationToken)
    {
        DeviceStoreInfo? store = storeCode switch
        {
            "1002" => new DeviceStoreInfo("1002", "Lutwyche"),
            "1003" => new DeviceStoreInfo("1003", "Chermside"),
            _ => null
        };

        return Task.FromResult(store);
    }

    private static PosIpadAppReviewOptions CreateReviewOptions(string provisioningCode, DateTimeOffset expiresAtUtc)
    {
        return new PosIpadAppReviewOptions
        {
            Enabled = true,
            StoreCode = "1003",
            ExpiresAtUtc = expiresAtUtc,
            MaxActiveDevices = 1,
            GrantId = "4baf31b5-792d-49ef-8cc2-d38b486a28a7",
            RegistrationCodeSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(provisioningCode)))
        };
    }

    private sealed class FakeDeviceRegistrationRepository : IDeviceRegistrationRepository
    {
        public DeviceRegistrationRecord? DeviceByCode { get; init; }

        public DeviceRegistrationRecord? DeviceByExactIdentity { get; init; }

        public IReadOnlyList<DeviceRegistrationRecord> DeviceRowsByCode { get; init; } = [];

        public IReadOnlyList<(string StoreCode, string DeviceCode)> ExistingDeviceCodes { get; init; } = [];

        public string? LastPreferredHardwareId { get; private set; }

        public DeviceRegistrationRecord? LatestByHardwareId { get; init; }

        public DeviceRegistrationRecord? ActiveOrLockedRegistration { get; init; }

        public DeviceRegistrationRecord? TargetRegistration { get; init; }

        public IReadOnlyList<DeviceRegistrationRecord> RegistrationsForUpdate { get; init; } = [];

        public int DisablePendingRowsAffected { get; init; } = 1;

        public IReadOnlyList<int>? DisablePendingRowsAffectedSequence { get; init; }

        public int DisableActiveRowsAffected { get; init; } = 1;

        public int ResetRowsAffected { get; init; } = 1;

        public int AppReviewApprovalRowsAffected { get; init; } = 1;

        public int ActiveOrLockedCount { get; init; }

        public int ActiveOrLockedCountCalls { get; private set; }

        public DeviceRegistrationAppReviewGrantConsumption? AppReviewGrantConsumption { get; init; }

        public Action? OnAcquireAppReviewGrantLock { get; init; }

        public List<DeviceRegistrationDisableRequest> DisabledRequests { get; } = [];

        public List<ActiveDisableSnapshot> ActiveDisabledRequests { get; } = [];

        public List<DeviceRegistrationResetActiveRequest> ResetActiveRequests { get; } = [];

        public List<DeviceRegistrationResetForReregisterRequest> ResetRequests { get; } = [];

        public List<DeviceRegistrationAppReviewApprovalRequest> AppReviewApprovalRequests { get; } = [];

        public List<DeviceRegistrationCreateRequest> CreatedRegistrations { get; } = [];

        public List<DeviceRegistrationAppReviewGrantConsumption> AppReviewGrantConsumptions { get; } = [];

        public List<string> RegistrationOperationOrder { get; } = [];

        public int TransactionCallCount { get; private set; }

        public RuntimeStatusSnapshot? LastRuntimeStatus { get; private set; }

        public bool CurrentRegistrationEnabled { get; private set; } = true;

        public bool PendingRegistrationEnabled { get; private set; } = true;

        public int DisabledPendingCount { get; private set; }

        private int disablePendingCallCount;

        public Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
            string deviceCode,
            string storeCode,
            string? preferredHardwareId,
            CancellationToken cancellationToken)
        {
            LastPreferredHardwareId = preferredHardwareId;
            if (DeviceByCode is not null)
            {
                return Task.FromResult<DeviceRegistrationRecord?>(DeviceByCode);
            }

            // 与仓储 SQL 契约一致：硬件码一致的行优先，其次取 ID 最大的最新行。
            var normalizedHardwareId = (preferredHardwareId ?? string.Empty).Trim();
            var record = DeviceRowsByCode
                .Where(row => string.Equals(row.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => normalizedHardwareId.Length > 0
                    && string.Equals(
                        (row.HardwareId ?? string.Empty).Trim(),
                        normalizedHardwareId,
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenByDescending(row => row.Id)
                .FirstOrDefault();
            return Task.FromResult(record);
        }

        public Task<bool> IsDeviceCodeTakenForRegistrationAsync(
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            var taken = ExistingDeviceCodes.Any(item =>
                    string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
                || CreatedRegistrations.Any(item =>
                    string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(taken);
        }

        public Task<DeviceRegistrationRecord?> FindLatestByDeviceCodeAndHardwareIdAsync(
            string deviceCode,
            string storeCode,
            string hardwareId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(DeviceByExactIdentity);
        }

        public Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
            string hardwareId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(LatestByHardwareId);
        }

        public Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
            string hardwareId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ActiveOrLockedRegistration);
        }

        public Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAndStoreCodeAsync(
            string hardwareId,
            string storeCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TargetRegistration);
        }

        public Task<IReadOnlyList<DeviceRegistrationRecord>> FindAllByHardwareIdForRegistrationAsync(
            string hardwareId,
            CancellationToken cancellationToken)
        {
            RegistrationOperationOrder.Add("hardware-range");
            return Task.FromResult(RegistrationsForUpdate);
        }

        public Task<int> CountActiveOrLockedByStoreCodeForRegistrationAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            ActiveOrLockedCountCalls++;
            return Task.FromResult(ActiveOrLockedCount);
        }

        public Task AcquireAppReviewGrantLockAsync(
            Guid grantId,
            string storeCode,
            CancellationToken cancellationToken)
        {
            RegistrationOperationOrder.Add("app-review-lock");
            OnAcquireAppReviewGrantLock?.Invoke();
            return Task.CompletedTask;
        }

        public Task<DeviceRegistrationAppReviewGrantConsumption?> FindAppReviewGrantConsumptionAsync(
            Guid grantId,
            CancellationToken cancellationToken) => Task.FromResult(AppReviewGrantConsumption);

        public Task<int> ConsumeAppReviewGrantAsync(
            DeviceRegistrationAppReviewGrantConsumption consumption,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken)
        {
            AppReviewGrantConsumptions.Add(consumption);
            return Task.FromResult(1);
        }

        public Task<bool> IsAppReviewDeviceAsync(
            string storeCode,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<int> DisableActiveRegistrationAsync(
            string hardwareId,
            string deviceCode,
            string storeCode,
            string remarkSuffix,
            CancellationToken cancellationToken)
        {
            ActiveDisabledRequests.Add(new ActiveDisableSnapshot(hardwareId, deviceCode, storeCode, remarkSuffix));
            if (DisableActiveRowsAffected == 1)
            {
                CurrentRegistrationEnabled = false;
            }

            return Task.FromResult(DisableActiveRowsAffected);
        }

        public Task<int> ResetActiveRegistrationAsync(
            DeviceRegistrationResetActiveRequest request,
            CancellationToken cancellationToken)
        {
            ResetActiveRequests.Add(request);
            if (DisableActiveRowsAffected == 1)
            {
                CurrentRegistrationEnabled = false;
            }

            return Task.FromResult(DisableActiveRowsAffected);
        }

        public Task<int> ResetRegistrationForReregisterAsync(
            DeviceRegistrationResetForReregisterRequest request,
            CancellationToken cancellationToken)
        {
            ResetRequests.Add(request);
            return Task.FromResult(ResetRowsAffected);
        }

        public Task<int> ApproveRegistrationForAppReviewAsync(
            DeviceRegistrationAppReviewApprovalRequest request,
            CancellationToken cancellationToken)
        {
            AppReviewApprovalRequests.Add(request);
            return Task.FromResult(AppReviewApprovalRowsAffected);
        }

        public Task<int> DisablePendingRegistrationAsync(
            DeviceRegistrationDisableRequest request,
            CancellationToken cancellationToken)
        {
            DisabledRequests.Add(request);
            var rowsAffected = DisablePendingRowsAffectedSequence is not null
                && disablePendingCallCount < DisablePendingRowsAffectedSequence.Count
                    ? DisablePendingRowsAffectedSequence[disablePendingCallCount]
                    : DisablePendingRowsAffected;
            disablePendingCallCount++;
            if (rowsAffected == 1)
            {
                PendingRegistrationEnabled = false;
                DisabledPendingCount++;
            }

            return Task.FromResult(rowsAffected);
        }

        public Task CreateRegistrationAsync(
            DeviceRegistrationCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreatedRegistrations.Add(request);
            return Task.CompletedTask;
        }

        public Task<int> UpdateRuntimeStatusAsync(
            DeviceRuntimeStatusUpdateRequest request,
            CancellationToken cancellationToken)
        {
            var nextCashierId = string.IsNullOrWhiteSpace(request.CashierId) ? null : request.CashierId.Trim();
            var nextCashierName = string.IsNullOrWhiteSpace(request.CashierName) ? null : request.CashierName.Trim();
            var cashierLoginAt = LastRuntimeStatus?.CashierLoginAt;
            if (nextCashierId is null && nextCashierName is null)
            {
                cashierLoginAt = null;
            }
            else if (LastRuntimeStatus?.CashierId != nextCashierId || cashierLoginAt is null)
            {
                cashierLoginAt = request.ReportedAt;
            }

            LastRuntimeStatus = new RuntimeStatusSnapshot(
                request.IsOnline,
                request.ReportedAt,
                nextCashierId,
                nextCashierName,
                cashierLoginAt,
                request.AppVersion ?? LastRuntimeStatus?.AppVersion);
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            TransactionCallCount++;
            var currentRegistrationEnabled = CurrentRegistrationEnabled;
            var pendingRegistrationEnabled = PendingRegistrationEnabled;
            var disabledPendingCount = DisabledPendingCount;
            try
            {
                await action(cancellationToken);
            }
            catch
            {
                CurrentRegistrationEnabled = currentRegistrationEnabled;
                PendingRegistrationEnabled = pendingRegistrationEnabled;
                DisabledPendingCount = disabledPendingCount;
                throw;
            }
        }
    }

    private sealed record ActiveDisableSnapshot(
        string HardwareId,
        string DeviceCode,
        string StoreCode,
        string RemarkSuffix);

    private sealed record RuntimeStatusSnapshot(
        bool IsOnline,
        DateTime LastHeartbeatAt,
        string? CashierId,
        string? CashierName,
        DateTime? CashierLoginAt,
        string? AppVersion = null);
}
