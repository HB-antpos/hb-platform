using AutoMapper;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using BlazorApp.Shared.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public class DeviceRegistrationControllerTests
    {
        [Fact]
        public async Task RegisterDevice_ReturnsExistingEnabledDevice_WhenAlreadyRegistered()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x =>
                    x.RegisterDeviceAsync(
                        "hbmobile-existing",
                        "Mobile",
                        "Android",
                        "S001"
                    )
                )
                .ReturnsAsync(
                    new POSM_设备注册信息表
                    {
                        ID = 12,
                        设备硬件识别码 = "hbmobile-existing",
                        系统设备编号 = "SYS-001",
                        设备授权码 = "AUTH-001",
                        设备状态 = 1,
                        设备类型 = "Mobile",
                        设备系统 = "Android",
                        分店代码 = "S001",
                    }
                );

            var mapper = new Mock<IMapper>();
            mapper
                .Setup(x => x.Map<DeviceRegistrationResponseDto>(It.IsAny<POSM_设备注册信息表>()))
                .Returns(
                    (POSM_设备注册信息表 device) =>
                        new DeviceRegistrationResponseDto
                        {
                            DeviceId = device.ID,
                            SystemDeviceNumber = device.系统设备编号 ?? string.Empty,
                            AuthCode = device.设备授权码 ?? string.Empty,
                            Status = device.设备状态,
                            StatusDescription = "启用",
                        }
                );
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                mapper.Object,
                Mock.Of<IStoreService>()
            );

            var result = await controller.RegisterDevice(
                new DeviceRegistrationRequestDto
                {
                    HardwareId = "hbmobile-existing",
                    DeviceType = "Mobile",
                    DeviceSystem = "Android",
                    StoreCode = "S001",
                }
            );

            var ok = Assert.IsType<OkObjectResult>(result);
            var dataProperty = ok.Value!.GetType().GetProperty("data");
            var data = Assert.IsType<DeviceRegistrationResponseDto>(
                dataProperty!.GetValue(ok.Value)
            );
            Assert.Equal("AUTH-001", data.AuthCode);
            Assert.Equal(1, data.Status);
        }

        [Fact]
        public async Task RegisterDevice_MobileActivationRequired_ReturnsActivationRequiredWithoutWrites()
        {
            var service = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>(),
                mobileDeviceActivationOptions: Options.Create(new MobileDeviceActivationOptions
                {
                    EnforceForNewRegistrations = true,
                }));

            var result = await controller.RegisterDevice(new DeviceRegistrationRequestDto
            {
                HardwareId = "hbmobile-new",
                DeviceType = "Mobile",
                DeviceSystem = "Android",
                StoreCode = "S001",
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.False((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
            Assert.Equal(
                "ACTIVATION_CODE_REQUIRED",
                ok.Value.GetType().GetProperty("reasonCode")!.GetValue(ok.Value));
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RegisterDevice_PosLegacyDisabled_ReturnsActivationRequiredWithoutWrites()
        {
            var service = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
            var options = Options.Create(new DeviceActivationOptions
            {
                LegacyRegistrationEnabled = new LegacyRegistrationOptions
                {
                    Windows = false,
                },
            });
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>(),
                deviceActivationOptions: options);

            var result = await controller.RegisterDevice(new DeviceRegistrationRequestDto
            {
                HardwareId = "POS-HW-1",
                DeviceType = "POS",
                DeviceSystem = "Windows",
                StoreCode = "S001",
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.False((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
            Assert.Equal(
                "ACTIVATION_CODE_REQUIRED",
                ok.Value.GetType().GetProperty("reasonCode")!.GetValue(ok.Value));
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public void LegacyRegistrationGate_TreatsAndroidTabletAsSeparateSwitch()
        {
            // 安卓平板与 iPadOS / 手持 Android 各自独立开关，关闭其一不影响另一方。
            var tabletOff = new DeviceActivationOptions
            {
                LegacyRegistrationEnabled = new LegacyRegistrationOptions { AndroidTablet = false },
            };
            var ipadAndAndroidOff = new DeviceActivationOptions
            {
                LegacyRegistrationEnabled = new LegacyRegistrationOptions { IpadOS = false, Android = false },
            };

            Assert.True(DeviceActivationOptions.TryNormalizeDeviceSystem(" AndroidTablet ", out var normalized));
            Assert.Equal("AndroidTablet", normalized);
            Assert.False(tabletOff.IsLegacyRegistrationEnabled("AndroidTablet"));
            Assert.True(tabletOff.IsLegacyRegistrationEnabled("iPadOS"));
            Assert.True(tabletOff.IsLegacyRegistrationEnabled("Android"));
            Assert.True(ipadAndAndroidOff.IsLegacyRegistrationEnabled("AndroidTablet"));
            Assert.False(ipadAndAndroidOff.IsLegacyRegistrationEnabled("iPadOS"));
            Assert.False(ipadAndAndroidOff.IsLegacyRegistrationEnabled("Android"));
        }

        [Fact]
        public async Task RegisterDevice_NonPosDeviceIsNotAffectedByLegacyPosGate()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service.Setup(item => item.RegisterDeviceAsync("PDA-HW-1", "PDA", "Windows", "S001"))
                .ReturnsAsync(new POSM_设备注册信息表
                {
                    设备硬件识别码 = "PDA-HW-1",
                    设备类型 = "PDA",
                    设备系统 = "Windows",
                    分店代码 = "S001",
                    系统设备编号 = "PDA_S001_1000",
                    设备授权码 = "AUTH",
                    设备状态 = -1,
                });
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>(),
                deviceActivationOptions: Options.Create(new DeviceActivationOptions
                {
                    LegacyRegistrationEnabled = new LegacyRegistrationOptions { Windows = false },
                }));

            await controller.RegisterDevice(new DeviceRegistrationRequestDto
            {
                HardwareId = "PDA-HW-1",
                DeviceType = "PDA",
                DeviceSystem = "Windows",
                StoreCode = "S001",
            });

            service.Verify(
                item => item.RegisterDeviceAsync("PDA-HW-1", "PDA", "Windows", "S001"),
                Times.Once);
        }

        [Fact]
        public async Task UnbindDevice_ReturnsOk_WhenDeviceAuthMatches()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x => x.UnbindDeviceAsync("hbmobile-existing", "AUTH-001", "DeviceSelfService"))
                .ReturnsAsync(true);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );

            var result = await controller.UnbindDevice(
                new DeviceUnbindRequestDto
                {
                    HardwareId = "hbmobile-existing",
                    AuthCode = "AUTH-001",
                }
            );

            var ok = Assert.IsType<OkObjectResult>(result);
            var successProperty = ok.Value!.GetType().GetProperty("success");
            Assert.True((bool)successProperty!.GetValue(ok.Value)!);
            service.Verify(
                x => x.UnbindDeviceAsync("hbmobile-existing", "AUTH-001", "DeviceSelfService"),
                Times.Once
            );
        }

        [Fact]
        public async Task UnbindDevice_ReturnsBadRequest_WhenDeviceAuthDoesNotMatch()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x => x.UnbindDeviceAsync("hbmobile-existing", "WRONG", "DeviceSelfService"))
                .ReturnsAsync(false);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );

            var result = await controller.UnbindDevice(
                new DeviceUnbindRequestDto
                {
                    HardwareId = "hbmobile-existing",
                    AuthCode = "WRONG",
                }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var successProperty = badRequest.Value!.GetType().GetProperty("success");
            Assert.False((bool)successProperty!.GetValue(badRequest.Value)!);
        }

        [Fact]
        public async Task GetDeviceByHardwareId_IncludesStoreName_WhenDeviceHasStoreCode()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x => x.ValidateDeviceAuthCodeAsync("hbmobile-existing", "AUTH-001"))
                .ReturnsAsync(true);
            service
                .Setup(x => x.GetDeviceByHardwareIdAsync("hbmobile-existing"))
                .ReturnsAsync(
                    new POSM_设备注册信息表
                    {
                        ID = 12,
                        设备硬件识别码 = "hbmobile-existing",
                        系统设备编号 = "SYS-001",
                        设备授权码 = "AUTH-001",
                        设备状态 = 1,
                        设备类型 = "Mobile",
                        设备系统 = "Android",
                        分店代码 = "1004",
                    }
                );

            var mapper = new Mock<IMapper>();
            mapper
                .Setup(x => x.Map<DeviceDataDto>(It.IsAny<POSM_设备注册信息表>()))
                .Returns(
                    (POSM_设备注册信息表 device) =>
                        new DeviceDataDto
                        {
                            Id = device.ID,
                            HardwareId = device.设备硬件识别码 ?? string.Empty,
                            SystemDeviceNumber = device.系统设备编号 ?? string.Empty,
                            AuthCode = device.设备授权码 ?? string.Empty,
                            Status = device.设备状态,
                            DeviceType = device.设备类型 ?? string.Empty,
                            DeviceSystem = device.设备系统 ?? string.Empty,
                            StoreCode = device.分店代码,
                        }
                );
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(x => x.GetStoreByCodeAsync("1004"))
                .ReturnsAsync(
                    ApiResponse<StoreDto>.OK(
                        new StoreDto
                        {
                            StoreCode = "1004",
                            StoreName = "Sunnybank",
                        }
                    )
                );
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                mapper.Object,
                storeService.Object
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            controller.Request.Headers["X-Device-Id"] = "hbmobile-existing";
            controller.Request.Headers["X-Auth-Code"] = "AUTH-001";

            var result = await controller.GetDeviceByHardwareId("hbmobile-existing");

            var ok = Assert.IsType<OkObjectResult>(result);
            var dataProperty = ok.Value!.GetType().GetProperty("data");
            var data = Assert.IsType<DeviceDataDto>(dataProperty!.GetValue(ok.Value));
            Assert.Equal("1004", data.StoreCode);
            Assert.Equal("Sunnybank", data.StoreName);
            Assert.Equal(string.Empty, data.AuthCode);
        }

        [Fact]
        public async Task GetDeviceByHardwareId_ReturnsUnauthorized_WhenDeviceHeadersAreMissing()
        {
            var service = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();

            var result = await controller.GetDeviceByHardwareId("hbmobile-existing");

            Assert.IsType<UnauthorizedObjectResult>(result);
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task GetDeviceByHardwareId_ReturnsUnauthorized_WhenHeaderHardwareIdDoesNotMatchRoute()
        {
            var service = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            controller.Request.Headers["X-Device-Id"] = "another-device";
            controller.Request.Headers["X-Auth-Code"] = "AUTH-001";

            var result = await controller.GetDeviceByHardwareId("hbmobile-existing");

            Assert.IsType<UnauthorizedObjectResult>(result);
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task GetDeviceByHardwareId_ReturnsUnauthorized_WhenDeviceAuthIsInvalid()
        {
            var service = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
            service
                .Setup(x => x.ValidateDeviceAuthCodeAsync("hbmobile-existing", "WRONG"))
                .ReturnsAsync(false);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            controller.Request.Headers["X-Device-Id"] = "hbmobile-existing";
            controller.Request.Headers["X-Auth-Code"] = "WRONG";

            var result = await controller.GetDeviceByHardwareId("hbmobile-existing");

            Assert.IsType<UnauthorizedObjectResult>(result);
            service.Verify(
                x => x.ValidateDeviceAuthCodeAsync("hbmobile-existing", "WRONG"),
                Times.Once
            );
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ReportRuntimeStatus_UpdatesAuthorizedDeviceOnly()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x => x.ValidateDeviceAuthCodeAsync("HW-001", "AUTH-001"))
                .ReturnsAsync(true);
            service
                .Setup(x => x.UpdateRuntimeStatusAsync("HW-001", true, "CASHIER-1", "Alice"))
                .ReturnsAsync(true);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            controller.Request.Headers.Authorization = "Bearer AUTH-001";
            controller.Request.Headers["X-HBPOS-Hardware-Id"] = "HW-001";

            var result = await controller.ReportRuntimeStatus(
                new DeviceRuntimeStatusUpdateDto
                {
                    IsOnline = true,
                    CurrentCashierId = "CASHIER-1",
                    CurrentCashierName = "Alice",
                }
            );

            var ok = Assert.IsType<OkObjectResult>(result);
            var successProperty = ok.Value!.GetType().GetProperty("success");
            Assert.True((bool)successProperty!.GetValue(ok.Value)!);
            service.Verify(
                x => x.UpdateRuntimeStatusAsync("HW-001", true, "CASHIER-1", "Alice"),
                Times.Once
            );
        }

        [Fact]
        public async Task ReportRuntimeStatus_ReturnsUnauthorized_WhenDeviceAuthIsInvalid()
        {
            var service = new Mock<IDeviceRegistrationService>();
            service
                .Setup(x => x.ValidateDeviceAuthCodeAsync("HW-001", "WRONG"))
                .ReturnsAsync(false);
            var controller = new DeviceRegistrationController(
                service.Object,
                Mock.Of<ILogger<DeviceRegistrationController>>(),
                Mock.Of<IMapper>(),
                Mock.Of<IStoreService>()
            );
            controller.ControllerContext.HttpContext = new DefaultHttpContext();
            controller.Request.Headers.Authorization = "Bearer WRONG";
            controller.Request.Headers["X-HBPOS-Hardware-Id"] = "HW-001";

            var result = await controller.ReportRuntimeStatus(
                new DeviceRuntimeStatusUpdateDto
                {
                    IsOnline = true,
                    CurrentCashierId = "CASHIER-1",
                    CurrentCashierName = "Alice",
                }
            );

            Assert.IsType<UnauthorizedObjectResult>(result);
            service.Verify(
                x => x.UpdateRuntimeStatusAsync(
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>()),
                Times.Never
            );
        }
    }

    public sealed class DeviceRegistrationServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public DeviceRegistrationServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });

            _db.CodeFirst.InitTables<POSM_设备注册信息表>();
        }

        [Fact]
        public void Constructor_MissingMobileActivationDependency_FailsClosed()
        {
            var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(POSMSqlSugarContext)
            );

            Assert.Throws<ArgumentNullException>(() => new DeviceRegistrationService(
                context,
                NullLogger<DeviceRegistrationService>.Instance,
                null!,
                null
            ));
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_ExistingAuthorizationCode_RemainsValid()
        {
            const string hardwareId = "hbmobile-legacy";
            await _db.Insertable(CreateDevice(
                hardwareId,
                "PDA_1004_1429",
                "1004")).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    "AUTH-001",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));

            var isValid = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateDeviceAuthCodeAsync(
                hardwareId,
                "AUTH-001");

            Assert.True(isValid);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_ActiveMobileBindingCredential_IsValid()
        {
            const string hardwareId = "hbmobile-bound";
            const string credential = "mobile-device-account-credential";
            var device = CreateDevice(hardwareId, "MOB_1004_ABC12345", "1004");
            device.设备授权码 = "SERVER-INTERNAL-AUTH-CODE";
            await _db.Insertable(device).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    credential,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, true));

            var isValid = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateDeviceAuthCodeAsync(
                hardwareId,
                credential);

            Assert.True(isValid);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_MobileBindingCredential_FailsClosedWhenBridgeRejects()
        {
            const string hardwareId = "hbmobile-bound-rejected";
            const string credential = "mobile-device-account-credential";
            var device = CreateDevice(hardwareId, "MOB_1004_DEF67890", "1004");
            device.设备授权码 = "SERVER-INTERNAL-AUTH-CODE";
            await _db.Insertable(device).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, false));
            var service = CreateService(
                mobileDeviceActivationService: activationService.Object);

            Assert.False(await service.ValidateDeviceAuthCodeAsync(hardwareId, "wrong-credential"));
            Assert.False(await service.ValidateDeviceAuthCodeAsync(hardwareId, credential));
            activationService.Verify(
                service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            activationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_ActiveMobileBinding_NeverAcceptsInternalAuthorizationCode()
        {
            const string hardwareId = "hbmobile-bound-internal-code";
            const string internalAuthCode = "SERVER-INTERNAL-AUTH-CODE";
            var device = CreateDevice(hardwareId, "MOB_1004_13572468", "1004");
            device.设备授权码 = internalAuthCode;
            await _db.Insertable(device).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    internalAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, false));

            var isValid = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateDeviceAuthCodeAsync(hardwareId, internalAuthCode);

            Assert.False(isValid);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_BindingHistoryOwnsHardwareBeforeLegacyRecordLookup()
        {
            const string hardwareId = "shared-cross-type-hardware";
            const string internalAuthCode = "POS-INTERNAL-AUTH-CODE";
            var legacyDevice = CreateDevice(
                hardwareId,
                "POS_1004_87654321",
                "1004",
                "Windows");
            legacyDevice.设备类型 = "POS";
            legacyDevice.设备授权码 = internalAuthCode;
            await _db.Insertable(legacyDevice).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    internalAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, false));

            var isValid = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateDeviceAuthCodeAsync(hardwareId, internalAuthCode);

            Assert.False(isValid);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateDeviceAuthCodeAsync_DuplicateSystemNumberAliasFailsClosed()
        {
            const string systemDeviceNumber = "SHARED-SYSTEM-ALIAS";
            const string legacyHardwareId = "legacy-alias-hardware";
            const string internalAuthCode = "LEGACY-INTERNAL-AUTH-CODE";
            var legacyDevice = CreateDevice(
                legacyHardwareId,
                systemDeviceNumber,
                "1004",
                "Windows");
            legacyDevice.设备类型 = "POS";
            legacyDevice.设备授权码 = internalAuthCode;
            await _db.Insertable(legacyDevice).ExecuteCommandAsync();
            await _db.Insertable(CreateDevice(
                "revoked-bound-alias-hardware",
                systemDeviceNumber,
                "1004")).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    systemDeviceNumber,
                    internalAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    legacyHardwareId,
                    internalAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));

            var isValid = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateDeviceAuthCodeAsync(systemDeviceNumber, internalAuthCode);

            Assert.False(isValid);
        }

        [Fact]
        public async Task ValidateAndUpdateDeviceAuthCodeAsync_ActiveMobileBinding_NeverReturnsInternalAuthorizationCode()
        {
            const string hardwareId = "hbmobile-bound-no-code-replay";
            const string internalAuthCode = "SERVER-INTERNAL-AUTH-CODE";
            var device = CreateDevice(hardwareId, "MOB_1004_24681357", "1004");
            device.设备授权码 = internalAuthCode;
            await _db.Insertable(device).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    "wrong-credential",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, false));

            var result = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateAndUpdateDeviceAuthCodeAsync(hardwareId, "wrong-credential");

            Assert.False(result.IsValid);
            Assert.Null(result.NewAuthCode);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateAndUpdateDeviceAuthCodeAsync_RevokedBindingHistoryNeverReturnsLegacyCode()
        {
            const string hardwareId = "revoked-binding-cross-type-hardware";
            const string internalAuthCode = "POS-INTERNAL-AUTH-CODE";
            var legacyDevice = CreateDevice(
                hardwareId,
                "POS_1004_99887766",
                "1004",
                "Windows");
            legacyDevice.设备类型 = "POS";
            legacyDevice.设备授权码 = internalAuthCode;
            await _db.Insertable(legacyDevice).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    internalAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, false));

            var result = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateAndUpdateDeviceAuthCodeAsync(hardwareId, internalAuthCode);

            Assert.False(result.IsValid);
            Assert.Null(result.NewAuthCode);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateAndUpdateDeviceAuthCodeAsync_ExactHardwareWinsOverAnotherDeviceAlias()
        {
            const string requestedHardwareId = "exact-hardware-priority";
            const string exactAuthCode = "EXACT-HARDWARE-AUTH-CODE";
            var aliasDevice = CreateDevice(
                "different-hardware",
                requestedHardwareId,
                "1004",
                "Windows");
            aliasDevice.设备类型 = "POS";
            aliasDevice.设备授权码 = "ALIAS-AUTH-CODE";
            await _db.Insertable(aliasDevice).ExecuteCommandAsync();
            var exactDevice = CreateDevice(
                requestedHardwareId,
                "POS_1004_EXACT000",
                "1004",
                "Windows");
            exactDevice.设备类型 = "POS";
            exactDevice.设备授权码 = exactAuthCode;
            await _db.Insertable(exactDevice).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    requestedHardwareId,
                    exactAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    "different-hardware",
                    exactAuthCode,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));

            var result = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateAndUpdateDeviceAuthCodeAsync(requestedHardwareId, exactAuthCode);

            Assert.True(result.IsValid);
            Assert.Null(result.NewAuthCode);
        }

        [Fact]
        public async Task ValidateAndUpdateDeviceAuthCodeAsync_ActiveMobileBinding_AcceptsOnlyDynamicCredential()
        {
            const string hardwareId = "hbmobile-bound-valid-credential";
            const string credential = "mobile-device-account-credential";
            var device = CreateDevice(hardwareId, "MOB_1004_11223344", "1004");
            device.设备授权码 = "SERVER-INTERNAL-AUTH-CODE";
            await _db.Insertable(device).ExecuteCommandAsync();
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    hardwareId,
                    credential,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(true, true));

            var result = await CreateService(
                mobileDeviceActivationService: activationService.Object
            ).ValidateAndUpdateDeviceAuthCodeAsync(hardwareId, credential);

            Assert.True(result.IsValid);
            Assert.Null(result.NewAuthCode);
            activationService.VerifyAll();
        }

        [Fact]
        public async Task ValidateAndUpdateDeviceAuthCodeAsync_PosDevice_PreservesLatestAuthorizationCodeRecovery()
        {
            const string hardwareId = "pos-device-existing";
            var device = CreateDevice(hardwareId, "POS_1004_12345678", "1004", "Windows");
            device.设备类型 = "POS";
            device.设备授权码 = "POS-AUTH-001";
            await _db.Insertable(device).ExecuteCommandAsync();

            var result = await CreateService()
                .ValidateAndUpdateDeviceAuthCodeAsync(hardwareId, "OUTDATED-POS-CODE");

            Assert.True(result.IsValid);
            Assert.Equal("POS-AUTH-001", result.NewAuthCode);
        }

        [Fact]
        public async Task UnbindDeviceAsync_MarksDeviceUnregisteredAndClearsAuthCode()
        {
            await _db.Insertable(new POSM_设备注册信息表
            {
                设备硬件识别码 = "hbmobile-existing",
                系统设备编号 = "SYS-001",
                设备授权码 = "AUTH-001",
                设备状态 = (int)DeviceStatus.启用,
                设备类型 = "Mobile",
                设备系统 = "Android",
                分店代码 = "S001",
                创建时间 = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.UnbindDeviceAsync(
                "hbmobile-existing",
                "AUTH-001",
                "DeviceSelfService"
            );

            Assert.True(result);
            var device = await _db.Queryable<POSM_设备注册信息表>()
                .FirstAsync(item => item.设备硬件识别码 == "hbmobile-existing");
            Assert.Equal((int)DeviceStatus.未注册, device.设备状态);
            Assert.Equal(string.Empty, device.设备授权码);
            Assert.Equal("DeviceSelfService", device.最后修改人);
            Assert.NotNull(device.最后修改时间);
        }

        [Fact]
        public async Task UnbindDeviceAsync_DoesNotChangeDevice_WhenAuthCodeDoesNotMatch()
        {
            await _db.Insertable(new POSM_设备注册信息表
            {
                设备硬件识别码 = "hbmobile-existing",
                系统设备编号 = "SYS-001",
                设备授权码 = "AUTH-001",
                设备状态 = (int)DeviceStatus.启用,
                设备类型 = "Mobile",
                设备系统 = "Android",
                分店代码 = "S001",
                创建时间 = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.UnbindDeviceAsync(
                "hbmobile-existing",
                "WRONG",
                "DeviceSelfService"
            );

            Assert.False(result);
            var device = await _db.Queryable<POSM_设备注册信息表>()
                .FirstAsync(item => item.设备硬件识别码 == "hbmobile-existing");
            Assert.Equal((int)DeviceStatus.启用, device.设备状态);
            Assert.Equal("AUTH-001", device.设备授权码);
            Assert.Null(device.最后修改人);
        }

        [Fact]
        public async Task GetDevicesPagedAsync_FiltersByDeviceSystem()
        {
            await _db.Insertable(new[]
            {
                CreateDevice("hbmobile-android", "PDA_1004_1430", "1004", "Android"),
                CreateDevice("hbmobile-ios", "PDA_1004_1431", "1004", "iOS"),
                CreateDevice("hbmobile-windows", "PDA_1004_1432", "1004", "Windows"),
            }).ExecuteCommandAsync();
            var service = CreateService();

            var (devices, total) = await service.GetDevicesPagedAsync(
                page: 1,
                pageSize: 20,
                deviceSystem: "iOS"
            );

            var device = Assert.Single(devices);
            Assert.Equal(1, total);
            Assert.Equal("hbmobile-ios", device.设备硬件识别码);
            Assert.Equal("iOS", device.设备系统);
        }

        [Fact]
        public async Task GetDevicesPagedAsync_WindowsFilterIncludesLegacyBlankSystems()
        {
            await _db.Insertable(new[]
            {
                CreateDevice("hbmobile-windows", "PDA_1004_1432", "1004", "Windows"),
                CreateDevice("hbmobile-legacy-empty", "PDA_1004_1433", "1004", ""),
                CreateDevice("hbmobile-legacy-blank", "PDA_1004_1434", "1004", " "),
                CreateDevice("hbmobile-ipados", "PDA_1004_1435", "1004", "iPadOS"),
                CreateDevice("hbmobile-windows-spaced", "PDA_1004_1436", "1004", " Windows "),
            }).ExecuteCommandAsync();

            var (devices, total) = await CreateService().GetDevicesPagedAsync(
                page: 1,
                pageSize: 20,
                deviceSystem: "Windows"
            );

            Assert.Equal(4, total);
            Assert.Equal(
                new[]
                {
                    "hbmobile-legacy-blank",
                    "hbmobile-legacy-empty",
                    "hbmobile-windows",
                    "hbmobile-windows-spaced",
                },
                devices.Select(device => device.设备硬件识别码).OrderBy(value => value)
            );
        }

        [Fact]
        public async Task GetDevicesPagedAsync_IpadOsAndOtherFiltersUsePlatformCategories()
        {
            await _db.Insertable(new[]
            {
                CreateDevice("hbmobile-windows", "PDA_1004_1432", "1004", "Windows"),
                CreateDevice("hbmobile-ipados", "PDA_1004_1433", "1004", "iPadOS"),
                CreateDevice("hbmobile-android", "PDA_1004_1434", "1004", "Android"),
                CreateDevice("hbmobile-unknown", "PDA_1004_1435", "1004", "VisionOS"),
                CreateDevice("hbmobile-legacy-blank", "PDA_1004_1436", "1004", ""),
                CreateDevice("hbmobile-ipados-spaced", "PDA_1004_1437", "1004", " iPadOS "),
                CreateDevice("hbmobile-windows-spaced", "PDA_1004_1438", "1004", " Windows "),
            }).ExecuteCommandAsync();
            var service = CreateService();

            var (ipadDevices, ipadTotal) = await service.GetDevicesPagedAsync(
                page: 1,
                pageSize: 20,
                deviceSystem: "iPadOS"
            );
            var (otherDevices, otherTotal) = await service.GetDevicesPagedAsync(
                page: 1,
                pageSize: 20,
                deviceSystem: "Other"
            );
            var (unknownDevices, unknownTotal) = await service.GetDevicesPagedAsync(
                page: 1,
                pageSize: 20,
                deviceSystem: "VisionOS"
            );

            Assert.Equal(2, ipadTotal);
            Assert.Equal(
                new[] { "hbmobile-ipados", "hbmobile-ipados-spaced" },
                ipadDevices.Select(device => device.设备硬件识别码).OrderBy(value => value)
            );
            Assert.Equal(2, otherTotal);
            Assert.Equal(
                new[] { "Android", "VisionOS" },
                otherDevices.Select(device => device.设备系统).OrderBy(value => value)
            );
            Assert.Equal(1, unknownTotal);
            Assert.Equal("VisionOS", Assert.Single(unknownDevices).设备系统);
        }

        [Fact]
        public async Task UpdateRuntimeStatusAsync_KeepsCashierLoginTimeForSameCashierAndClearsWhenEmpty()
        {
            await _db.Insertable(new POSM_设备注册信息表
            {
                设备硬件识别码 = "HW-001",
                系统设备编号 = "POS-001",
                设备授权码 = "AUTH-001",
                设备状态 = (int)DeviceStatus.启用,
                设备类型 = "POS",
                设备系统 = "Windows",
                分店代码 = "S001",
                创建时间 = DateTime.UtcNow,
                最后修改时间 = new DateTime(2026, 1, 1, 9, 0, 0),
            }).ExecuteCommandAsync();
            var firstNow = new DateTime(2026, 7, 1, 10, 0, 0);
            var secondNow = new DateTime(2026, 7, 1, 10, 1, 0);

            var firstResult = await CreateService(firstNow).UpdateRuntimeStatusAsync(
                "HW-001",
                true,
                "CASHIER-1",
                "Alice"
            );
            var secondResult = await CreateService(secondNow).UpdateRuntimeStatusAsync(
                "HW-001",
                true,
                "CASHIER-1",
                "Alice"
            );

            var device = await _db.Queryable<POSM_设备注册信息表>()
                .FirstAsync(item => item.设备硬件识别码 == "HW-001");
            Assert.True(firstResult);
            Assert.True(secondResult);
            Assert.True(device.是否在线);
            Assert.Equal(secondNow, device.最后心跳时间);
            Assert.Equal("CASHIER-1", device.当前收银员ID);
            Assert.Equal("Alice", device.当前收银员姓名);
            Assert.Equal(firstNow, device.收银员登录时间);
            Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), device.最后修改时间);

            var clearResult = await CreateService(secondNow.AddMinutes(1)).UpdateRuntimeStatusAsync(
                "HW-001",
                false,
                null,
                null
            );

            device = await _db.Queryable<POSM_设备注册信息表>()
                .FirstAsync(item => item.设备硬件识别码 == "HW-001");
            Assert.True(clearResult);
            Assert.False(device.是否在线);
            Assert.Null(device.当前收银员ID);
            Assert.Null(device.当前收银员姓名);
            Assert.Null(device.收银员登录时间);
        }

        [Fact]
        public async Task RegisterDeviceAsync_GeneratesPdaStoreTimeNumber_ForNewDevice()
        {
            var service = CreateService(new DateTime(2026, 1, 1, 14, 30, 0));

            var device = await service.RegisterDeviceAsync(
                "hbmobile-new",
                "Mobile",
                "Android",
                "1004"
            );

            Assert.Equal("PDA_1004_1430", device.系统设备编号);
        }

        [Fact]
        public async Task RegisterDeviceAsync_UsesNextMinute_WhenSameStoreTimeAlreadyExists()
        {
            await InsertDeviceAsync("hbmobile-existing", "PDA_1004_1430", "1004");
            var service = CreateService(new DateTime(2026, 1, 1, 14, 30, 0));

            var device = await service.RegisterDeviceAsync(
                "hbmobile-new",
                "Mobile",
                "Android",
                "1004"
            );

            Assert.Equal("PDA_1004_1431", device.系统设备编号);
        }

        [Fact]
        public async Task RegisterDeviceAsync_AllowsSameMinute_ForDifferentStores()
        {
            await InsertDeviceAsync("hbmobile-existing", "PDA_1005_1430", "1005");
            var service = CreateService(new DateTime(2026, 1, 1, 14, 30, 0));

            var device = await service.RegisterDeviceAsync(
                "hbmobile-new",
                "Mobile",
                "Android",
                "1004"
            );

            Assert.Equal("PDA_1004_1430", device.系统设备编号);
        }

        [Fact]
        public async Task RegisterDeviceAsync_Throws_WhenAllStoreMinutesAreUsed()
        {
            var rows = Enumerable.Range(0, 1440)
                .Select(offset =>
                {
                    var hhmm = new DateTime(2026, 1, 1).AddMinutes(offset).ToString("HHmm");
                    return CreateDevice($"hbmobile-existing-{hhmm}", $"PDA_1004_{hhmm}", "1004");
                })
                .ToList();
            await _db.Insertable(rows).ExecuteCommandAsync();
            var service = CreateService(new DateTime(2026, 1, 1, 14, 30, 0));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RegisterDeviceAsync("hbmobile-new", "Mobile", "Android", "1004")
            );

            Assert.Contains("HHMM", ex.Message);
            Assert.Contains("1004", ex.Message);
        }

        [Fact]
        public async Task RegisterDeviceAsync_Throws_WhenStoreCodeIsMissing()
        {
            var service = CreateService(new DateTime(2026, 1, 1, 14, 30, 0));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RegisterDeviceAsync("hbmobile-new", "Mobile", "Android", " ")
            );

            Assert.Contains("分店代码", ex.Message);
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private async Task InsertDeviceAsync(
            string hardwareId,
            string systemDeviceNumber,
            string storeCode
        )
        {
            await _db.Insertable(CreateDevice(hardwareId, systemDeviceNumber, storeCode))
                .ExecuteCommandAsync();
        }

        private static POSM_设备注册信息表 CreateDevice(
            string hardwareId,
            string systemDeviceNumber,
            string storeCode,
            string deviceSystem = "Android"
        )
        {
            return new POSM_设备注册信息表
            {
                设备硬件识别码 = hardwareId,
                系统设备编号 = systemDeviceNumber,
                设备授权码 = "AUTH-001",
                设备状态 = (int)DeviceStatus.启用,
                设备类型 = "Mobile",
                设备系统 = deviceSystem,
                分店代码 = storeCode,
                创建时间 = DateTime.UtcNow,
            };
        }

        private DeviceRegistrationService CreateService(
            DateTime? now = null,
            IMobileDeviceActivationService? mobileDeviceActivationService = null)
        {
            var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(POSMSqlSugarContext)
            );
            var dbField = typeof(POSMSqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, _db);

            return new DeviceRegistrationService(
                context,
                NullLogger<DeviceRegistrationService>.Instance,
                mobileDeviceActivationService ?? CreateLegacyMobileActivationService(),
                now.HasValue ? () => now.Value : null
            );
        }

        private static IMobileDeviceActivationService CreateLegacyMobileActivationService()
        {
            var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
            activationService
                .Setup(service => service.ValidateBoundDeviceCredentialAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileDeviceCredentialValidationResult(false, false));
            return activationService.Object;
        }
    }
}
