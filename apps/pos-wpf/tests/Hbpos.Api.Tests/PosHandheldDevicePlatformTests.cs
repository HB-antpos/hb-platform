using System.Security.Claims;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class PosHandheldDevicePlatformTests
{
    [Theory]
    [InlineData("iOS", "iOS")]
    [InlineData("Android", "Android")]
    [InlineData(" iPadOS ", "iPadOS")]
    [InlineData("IPADOS", "iPadOS")]
    [InlineData(" windows ", "Windows")]
    [InlineData(null, "Windows")]
    public void DeviceSystems_normalizes_canonical_handheld_and_legacy_platforms(
        string? input,
        string expected)
    {
        var accepted = DeviceSystems.TryNormalize(input, out var normalized);

        Assert.True(accepted);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(" ios ")]
    [InlineData("ios")]
    [InlineData("IOS")]
    [InlineData(" android ")]
    [InlineData("android")]
    [InlineData("ANDROID")]
    public void DeviceSystems_rejects_noncanonical_handheld_platform_values(string input)
    {
        var accepted = DeviceSystems.TryNormalize(input, out var normalized);

        Assert.False(accepted);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    [InlineData("iPadOS")]
    public void Device_authorization_requires_exact_hardware_for_every_mobile_platform(
        string deviceSystem)
    {
        Assert.False(DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(
            deviceSystem,
            "HW-001",
            null));
        Assert.False(DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(
            deviceSystem,
            "HW-001",
            "HW-OTHER"));
        Assert.True(DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(
            deviceSystem,
            "HW-001",
            "hw-001"));
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    public async Task Register_creates_canonical_handheld_registration(string deviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository();
        var service = CreateService(repository);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-SHARED", "Handheld", deviceSystem),
            CancellationToken.None);

        Assert.Equal(-1, response.DeviceStatus);
        var created = Assert.Single(repository.CreatedRegistrations);
        Assert.Equal(deviceSystem, created.DeviceSystem);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    public async Task Verify_rejects_missing_or_mismatched_handheld_hardware(string deviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository
        {
            DeviceByCode = Registration(
                id: 1,
                storeCode: "1003",
                hardwareId: "HW-001",
                deviceSystem: deviceSystem,
                status: 1,
                deviceCode: "POS_1003_1000")
        };
        var service = CreateService(repository);

        var missing = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1000", "1003", null, "Handheld", deviceSystem),
            CancellationToken.None);
        var mismatch = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1000", "1003", "HW-OTHER", "Handheld", deviceSystem),
            CancellationToken.None);
        var exact = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1000", "1003", "hw-001", "Handheld", deviceSystem),
            CancellationToken.None);

        Assert.False(missing.IsAllowed);
        Assert.Contains("required", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(mismatch.IsAllowed);
        Assert.Equal("Device hardware id does not match.", mismatch.Message);
        Assert.True(exact.IsAllowed);
    }

    [Fact]
    public async Task Register_same_hardware_on_different_platform_is_blocked_globally()
    {
        var android = Registration(
            id: 20,
            storeCode: "1003",
            hardwareId: "HW-SHARED",
            deviceSystem: "Android",
            status: 1,
            deviceCode: "POS_1003_ANDROID");
        var repository = new RecordingDeviceRegistrationRepository
        {
            RegistrationsForUpdate = [android]
        };
        var service = CreateService(repository);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-SHARED", "iPhone", "iOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal(1, response.DeviceStatus);
        Assert.Equal("Device hardware is already registered and cannot be registered anonymously.", response.Message);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.DisabledPlatforms);
    }

    [Fact]
    public async Task Register_unknown_requested_platform_fails_closed_without_writes()
    {
        var repository = new RecordingDeviceRegistrationRepository();
        var service = CreateService(repository);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-SHARED", "Watch", "watchOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("deviceSystem is invalid", response.Message);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.DisabledPlatforms);
    }

    [Theory]
    [InlineData(" ios ")]
    [InlineData("ios")]
    [InlineData("IOS")]
    [InlineData(" android ")]
    [InlineData("android")]
    [InlineData("ANDROID")]
    public async Task Register_rejects_noncanonical_handheld_platform_without_writes(
        string deviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository();
        var service = CreateService(repository);

        var response = await service.RegisterAsync(
            new DeviceRegisterRequest("1003", "HW-SHARED", "Handheld", deviceSystem),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("deviceSystem is invalid", response.Message);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.DisabledPlatforms);
    }

    [Theory]
    [InlineData(" ios ")]
    [InlineData("ios")]
    [InlineData("IOS")]
    [InlineData(" android ")]
    [InlineData("android")]
    [InlineData("ANDROID")]
    public async Task Verify_rejects_noncanonical_handheld_request_platform_without_writes(
        string deviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository
        {
            DeviceByCode = Registration(1, "1003", "HW-001", "iOS", 1, "POS_1003_1000")
        };
        var service = CreateService(repository);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1000", "1003", "HW-001", "Handheld", deviceSystem),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("deviceSystem is invalid", response.Message);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.DisabledPlatforms);
    }

    [Theory]
    [InlineData(" ios ")]
    [InlineData("ios")]
    [InlineData("IOS")]
    [InlineData(" android ")]
    [InlineData("android")]
    [InlineData("ANDROID")]
    public async Task Verify_rejects_noncanonical_handheld_platform_from_registered_record(
        string registeredDeviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository
        {
            DeviceByCode = Registration(
                1,
                "1003",
                "HW-001",
                registeredDeviceSystem,
                1,
                "POS_1003_1000")
        };
        var service = CreateService(repository);

        var response = await service.VerifyAsync(
            new DeviceVerifyRequest("POS_1003_1000", "1003", "HW-001", "Handheld", "iOS"),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Registered device system is invalid.", response.Message);
    }

    [Fact]
    public async Task Reregister_inherits_authenticated_platform_using_existing_target_and_disable_contract()
    {
        var androidTarget = Registration(
            id: 30,
            storeCode: "1003",
            hardwareId: "HW-SHARED",
            deviceSystem: "Android",
            status: 0,
            deviceCode: "POS_1003_ANDROID");
        var repository = new RecordingDeviceRegistrationRepository
        {
            TargetRegistration = androidTarget
        };
        var service = CreateService(repository);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-SHARED", "iPhone"),
            new DeviceReregisterContext(
                "POS_1002_IOS",
                "1002",
                "HW-SHARED",
                "iOS"),
            CancellationToken.None);

        Assert.Equal(-1, response.DeviceStatus);
        Assert.Equal("POS_1003_ANDROID", response.DeviceCode);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Equal("iOS", Assert.Single(repository.ResetRequests).DeviceSystem);
        Assert.Equal([string.Empty], repository.DisabledPlatforms);
    }

    [Theory]
    [InlineData(" ios ")]
    [InlineData("ios")]
    [InlineData("IOS")]
    [InlineData(" android ")]
    [InlineData("android")]
    [InlineData("ANDROID")]
    public async Task Reregister_rejects_noncanonical_handheld_platform_from_authenticated_context_without_writes(
        string deviceSystem)
    {
        var repository = new RecordingDeviceRegistrationRepository
        {
            TargetRegistration = Registration(30, "1003", "HW-SHARED", "Android", 0, "POS_1003_ANDROID")
        };
        var service = CreateService(repository);

        var response = await service.ReregisterAsync(
            new DeviceReregisterRequest("1003", "HW-SHARED", "Handheld"),
            new DeviceReregisterContext("POS_1002_HANDHELD", "1002", "HW-SHARED", deviceSystem),
            CancellationToken.None);

        Assert.False(response.IsAllowed);
        Assert.Equal("Current device system is invalid.", response.Message);
        Assert.Empty(repository.CreatedRegistrations);
        Assert.Empty(repository.ResetRequests);
        Assert.Empty(repository.DisabledPlatforms);
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    public async Task Reregister_controller_inherits_platform_from_authenticated_claim(
        string deviceSystem)
    {
        var service = new RecordingDeviceService();
        var controller = new DevicesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS_1002_HANDHELD"),
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "1002"),
                        new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001"),
                        new Claim(DeviceAuthConstants.DeviceSystemClaim, deviceSystem)
                    ], DeviceAuthConstants.Scheme))
                }
            }
        };

        var result = await controller.Reregister(
            new DeviceReregisterRequest("1003", "HW-001", "Handheld"),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(deviceSystem, service.Context?.DeviceSystem);
    }

    private static DeviceService CreateService(RecordingDeviceRegistrationRepository repository) =>
        new(repository, LoadStoreAsync, () => new DateTime(2026, 8, 10, 12, 0, 0));

    private static Task<DeviceStoreInfo?> LoadStoreAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        DeviceStoreInfo? store = storeCode switch
        {
            "1002" => new DeviceStoreInfo("1002", "Lutwyche"),
            "1003" => new DeviceStoreInfo("1003", "Chermside"),
            _ => null
        };
        return Task.FromResult(store);
    }

    private static DeviceRegistrationRecord Registration(
        int id,
        string storeCode,
        string hardwareId,
        string deviceSystem,
        int status,
        string deviceCode) =>
        new()
        {
            Id = id,
            StoreCode = storeCode,
            HardwareId = hardwareId,
            DeviceSystem = deviceSystem,
            DeviceStatus = status,
            DeviceCode = deviceCode,
            AuthorizationCode = "AUTH-001"
        };

    private sealed class RecordingDeviceRegistrationRepository : IDeviceRegistrationRepository
    {
        public DeviceRegistrationRecord? DeviceByCode { get; init; }

        public DeviceRegistrationRecord? TargetRegistration { get; init; }

        public IReadOnlyList<DeviceRegistrationRecord> RegistrationsForUpdate { get; init; } = [];

        public List<DeviceRegistrationCreateRequest> CreatedRegistrations { get; } = [];

        public List<DeviceRegistrationResetForReregisterRequest> ResetRequests { get; } = [];

        public List<string> DisabledPlatforms { get; } = [];

        public Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
            string hardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DeviceRegistrationRecord?>(null);

        public Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
            string deviceCode,
            string storeCode,
            string? preferredHardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult(DeviceByCode);

        public Task<bool> IsDeviceCodeTakenForRegistrationAsync(
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<DeviceRegistrationRecord?> FindLatestByDeviceCodeAndHardwareIdAsync(
            string deviceCode,
            string storeCode,
            string hardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult(DeviceByCode);

        public Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
            string hardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DeviceRegistrationRecord?>(null);

        public Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAndStoreCodeAsync(
            string hardwareId,
            string storeCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(TargetRegistration);

        public Task<IReadOnlyList<DeviceRegistrationRecord>> FindAllByHardwareIdForRegistrationAsync(
            string hardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult(RegistrationsForUpdate);

        public Task<int> CountActiveOrLockedByStoreCodeForRegistrationAsync(
            string storeCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task AcquireAppReviewGrantLockAsync(
            Guid grantId,
            string storeCode,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeviceRegistrationAppReviewGrantConsumption?> FindAppReviewGrantConsumptionAsync(
            Guid grantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DeviceRegistrationAppReviewGrantConsumption?>(null);

        public Task<int> ConsumeAppReviewGrantAsync(
            DeviceRegistrationAppReviewGrantConsumption consumption,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<bool> IsAppReviewDeviceAsync(
            string storeCode,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<int> DisablePendingRegistrationAsync(
            DeviceRegistrationDisableRequest request,
            CancellationToken cancellationToken)
        {
            DisabledPlatforms.Add(string.Empty);
            return Task.FromResult(1);
        }

        public Task<int> DisableActiveRegistrationAsync(
            string hardwareId,
            string deviceCode,
            string storeCode,
            string remarkSuffix,
            CancellationToken cancellationToken)
        {
            DisabledPlatforms.Add(string.Empty);
            return Task.FromResult(1);
        }

        public Task<int> ResetRegistrationForReregisterAsync(
            DeviceRegistrationResetForReregisterRequest request,
            CancellationToken cancellationToken)
        {
            ResetRequests.Add(request);
            return Task.FromResult(1);
        }

        public Task<int> ApproveRegistrationForAppReviewAsync(
            DeviceRegistrationAppReviewApprovalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(1);

        public Task CreateRegistrationAsync(
            DeviceRegistrationCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreatedRegistrations.Add(request);
            return Task.CompletedTask;
        }

        public Task<int> UpdateRuntimeStatusAsync(
            DeviceRuntimeStatusUpdateRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(1);

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken) =>
            await action(cancellationToken);
    }

    private sealed class RecordingDeviceService : IDeviceService
    {
        public DeviceReregisterContext? Context { get; private set; }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeviceRegisterResponse> RegisterForAppReviewAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            DeviceReregisterContext currentDevice,
            CancellationToken cancellationToken)
        {
            Context = currentDevice;
            return Task.FromResult(new DeviceReregisterResponse(
                "POS_1003_HANDHELD",
                "1003",
                "Chermside",
                -1,
                false));
        }

        public Task<bool> UpdateRuntimeStatusAsync(
            string hardwareId,
            string deviceCode,
            string storeCode,
            bool isOnline,
            string? cashierId,
            string? cashierName,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
