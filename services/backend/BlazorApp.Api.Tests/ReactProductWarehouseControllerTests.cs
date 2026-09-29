using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public class ReactProductWarehouseControllerTests
    {
        [Fact]
        public void SyncFromHq_仅允许Admin调用()
        {
            var method = typeof(ReactProductWarehouseController).GetMethod(
                nameof(ReactProductWarehouseController.SyncFromHq)
            );

            var authorizeAttribute = Assert.Single(
                method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            );

            Assert.Equal("Admin", ((AuthorizeAttribute)authorizeAttribute).Roles);
        }

        [Fact]
        public void StartSyncFromHqJob_仅允许Admin调用()
        {
            var method = typeof(ReactProductWarehouseController).GetMethod(
                nameof(ReactProductWarehouseController.StartSyncFromHqJob)
            );

            var authorizeAttribute = Assert.Single(
                method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            );

            Assert.Equal("Admin", ((AuthorizeAttribute)authorizeAttribute).Roles);
        }

        [Fact]
        public void GetSyncFromHqJob_仅允许Admin调用()
        {
            var method = typeof(ReactProductWarehouseController).GetMethod(
                nameof(ReactProductWarehouseController.GetSyncFromHqJob)
            );

            var authorizeAttribute = Assert.Single(
                method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            );

            Assert.Equal("Admin", ((AuthorizeAttribute)authorizeAttribute).Roles);
        }

        [Fact]
        public void BatchUpdate_仅允许Admin和WarehouseManager调用()
        {
            var authorize = GetSingleAuthorizeAttribute(
                nameof(ReactProductWarehouseController.BatchUpdate)
            );

            Assert.Equal("Admin,WarehouseManager", authorize.Roles);
        }

        [Theory]
        [InlineData(nameof(ReactProductWarehouseController.StartBatchUpdateJob))]
        [InlineData(nameof(ReactProductWarehouseController.GetBatchUpdateJob))]
        public void BatchUpdateJob_仅允许Admin和WarehouseManager调用(string methodName)
        {
            var authorize = GetSingleAuthorizeAttribute(methodName);

            Assert.Equal("Admin,WarehouseManager", authorize.Roles);
        }

        [Fact]
        public async Task Table_服务抛异常时_记录日志前先设置500且包含失败阶段()
        {
            var timings = new WarehouseProductTableTimingSnapshot(
                CandidateMs: 1,
                CountMs: 2,
                PageMs: 3,
                LocationMs: 4,
                RowsMs: 5,
                MapMs: 6,
                TotalMs: 21
            );
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.GetAntdTableDataAsync(It.IsAny<ReactTableRequestDto>()))
                .ThrowsAsync(
                    new WarehouseProductTableQueryException(
                        "page",
                        timings,
                        new TimeoutException("query timeout")
                    )
                );

            var statusCodeWhenLogged = 0;
            string? loggedMessage = null;
            ReactProductWarehouseController? controller = null;
            var logger = new Mock<ILogger<ReactProductWarehouseController>>();
            logger
                .Setup(x =>
                    x.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((_, _) => true),
                        It.IsAny<Exception?>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                    )
                )
                .Callback(
                    new InvocationAction(invocation =>
                    {
                        statusCodeWhenLogged = controller!.HttpContext.Response.StatusCode;
                        loggedMessage = invocation.Arguments[2]?.ToString();
                    })
                );
            controller = CreateController(serviceMock.Object, logger: logger.Object);

            var result = await controller.Table(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 100,
                GlobalSearch = "DO-NOT-LOG-ME-123",
            });

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
            Assert.Equal(StatusCodes.Status500InternalServerError, statusCodeWhenLogged);
            Assert.Contains("stage=page", loggedMessage);
            Assert.Contains("candidateMs=1", loggedMessage);
            Assert.DoesNotContain("DO-NOT-LOG-ME-123", loggedMessage);
        }

        [Fact]
        public async Task SyncFromHq_成功时_返回统一响应()
        {
            var expected = new SyncResult
            {
                IsSuccess = true,
                Message = "仓库商品同步成功",
                AddedCount = 3,
            };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.SyncFromHqAsync(
                    It.IsAny<string?>(),
                    It.Is<string?>(name => name == "测试用户")
                ))
                .ReturnsAsync(expected);

            var controller = CreateController(serviceMock.Object);

            var result = await controller.SyncFromHq();

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<ApiResponse<SyncResult>>(ok.Value);
            Assert.True(payload.Success);
            Assert.Equal("仓库商品同步成功", payload.Message);
            Assert.Same(expected, payload.Data);
        }

        [Fact]
        public async Task SyncFromHq_服务抛异常时_返回500统一错误响应()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.SyncFromHqAsync(
                    It.IsAny<string?>(),
                    It.Is<string?>(name => name == "测试用户")
                ))
                .ThrowsAsync(new Exception("boom"));

            var controller = CreateController(serviceMock.Object);

            var result = await controller.SyncFromHq();

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, objectResult.StatusCode);

            var payload = Assert.IsType<ApiResponse<SyncResult>>(objectResult.Value);
            Assert.False(payload.Success);
            Assert.Equal("仓库商品同步异常", payload.Message);
            Assert.Equal("INTERNAL_ERROR", payload.ErrorCode);
        }

        [Fact]
        public async Task StartSyncFromHqJob_空OperationId_返回400()
        {
            var controller = CreateController(Mock.Of<IProductWarehouseReactService>());

            var result = await controller.StartSyncFromHqJob(
                new WarehouseProductHqSyncJobRequestDto { OperationId = "" },
                CancellationToken.None
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("operationId 不能为空", badRequest.Value?.ToString());
        }

        [Fact]
        public async Task StartSyncFromHqJob_成功时_返回后台任务()
        {
            var expected = new WarehouseProductHqSyncJobDto
            {
                JobId = "warehouse-job-1",
                Status = WarehouseProductHqSyncJobStatusConstants.Running,
                CreatedAt = new DateTime(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc),
            };
            var jobServiceMock = new Mock<IWarehouseProductHqSyncJobService>();
            jobServiceMock
            .Setup(service =>
                    service.StartJobAsync(
                        It.Is<WarehouseProductHqSyncJobRequestDto>(request =>
                            request.OperationId == "warehouse-sync"
                        ),
                        "warehouse-user-guid",
                        "仓库管理员",
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(expected);

            var currentUserService = new Mock<ICurrentUserService>(MockBehavior.Strict);
            currentUserService.Setup(service => service.GetCurrentUserGuid()).Returns("warehouse-user-guid");
            currentUserService.Setup(service => service.GetCurrentUsername()).Returns("仓库管理员");

            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                jobService: jobServiceMock.Object,
                currentUserService: currentUserService.Object
            );

            var result = await controller.StartSyncFromHqJob(
                new WarehouseProductHqSyncJobRequestDto { OperationId = "warehouse-sync" },
                CancellationToken.None
            );

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("仓库商品同步任务已提交", ok.Value?.ToString());
            var data = ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value);
            var job = Assert.IsType<WarehouseProductHqSyncJobDto>(data);
            Assert.Equal("warehouse-job-1", job.JobId);
        }

        [Fact]
        public async Task GetSyncFromHqJob_不存在时_返回404()
        {
            var jobServiceMock = new Mock<IWarehouseProductHqSyncJobService>();
            jobServiceMock
                .Setup(service => service.GetJobAsync("missing", It.IsAny<CancellationToken>()))
                .ReturnsAsync((WarehouseProductHqSyncJobDto?)null);

            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                jobService: jobServiceMock.Object
            );

            var result = await controller.GetSyncFromHqJob("missing", CancellationToken.None);

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Contains("同步任务不存在或已过期", notFound.Value?.ToString());
        }

        [Fact]
        public void PatchMobileProduct_允许WarehouseStaff更新业务字段()
        {
            var authorizeAttribute = GetSingleAuthorizeAttribute(
                nameof(ReactProductWarehouseController.PatchMobileProduct)
            );

            Assert.Equal("Admin,WarehouseManager,WarehouseStaff", authorizeAttribute.Roles);
        }

        [Fact]
        public void GetMobileImageUploadSignature_允许WarehouseStaff更新图片()
        {
            var authorizeAttribute = GetSingleAuthorizeAttribute(
                nameof(ReactProductWarehouseController.GetMobileImageUploadSignature)
            );

            Assert.Equal("Admin,WarehouseManager,WarehouseStaff", authorizeAttribute.Roles);
        }

        [Fact]
        public async Task PatchMobileProduct_WhenWarehouseStaff_CallsPatchService()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service =>
                    service.PatchMobileProductAsync(
                        "HB313-129",
                        It.Is<WarehouseMobileProductPatchDto>(dto =>
                            dto.StockQuantity == 141
                            && dto.RetailPrice == 12.99m
                            && dto.SyncStoreRetailPrices == true
                        ),
                        "仓库操作员"
                    )
                )
                .ReturnsAsync(
                    new WarehouseMobileProductDto
                    {
                        ProductCode = "HB313-129",
                        StockQuantity = 141,
                        RetailPrice = 12.99m,
                    }
                );

            var controller = CreateController(
                serviceMock.Object,
                roles: new[] { "WarehouseStaff" },
                username: "仓库操作员"
            );

            var result = await controller.PatchMobileProduct(
                "HB313-129",
                new WarehouseMobileProductPatchDto
                {
                    StockQuantity = 141,
                    RetailPrice = 12.99m,
                    SyncStoreRetailPrices = true,
                }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service =>
                    service.PatchMobileProductAsync(
                        "HB313-129",
                        It.Is<WarehouseMobileProductPatchDto>(dto =>
                            dto.StockQuantity == 141
                            && dto.RetailPrice == 12.99m
                            && dto.SyncStoreRetailPrices == true
                        ),
                        "仓库操作员"
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task PatchMobileProduct_WhenWarehouseIsActiveProvided_PassesWarehouseIsActiveToService()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service =>
                    service.PatchMobileProductAsync(
                        "HB313-130",
                        It.Is<WarehouseMobileProductPatchDto>(dto =>
                            dto.WarehouseIsActive == false
                            && dto.IsActive == null
                            && dto.StockQuantity == 25
                        ),
                        "仓库操作员"
                    )
                )
                .ReturnsAsync(
                    new WarehouseMobileProductDto
                    {
                        ProductCode = "HB313-130",
                        WarehouseIsActive = false,
                        IsActive = false,
                        StockQuantity = 25,
                    }
                );

            var controller = CreateController(
                serviceMock.Object,
                roles: new[] { "WarehouseStaff" },
                username: "仓库操作员"
            );

            var result = await controller.PatchMobileProduct(
                "HB313-130",
                new WarehouseMobileProductPatchDto
                {
                    WarehouseIsActive = false,
                    StockQuantity = 25,
                }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service =>
                    service.PatchMobileProductAsync(
                        "HB313-130",
                        It.Is<WarehouseMobileProductPatchDto>(dto =>
                            dto.WarehouseIsActive == false
                            && dto.IsActive == null
                            && dto.StockQuantity == 25
                        ),
                        "仓库操作员"
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task SetMobileProductLocation_WhenLocationIsInvalid_ReturnsBadRequest()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.SetMobileProductLocationAsync("P001", "LOC-404"))
                .ThrowsAsync(new InvalidOperationException("货位不存在"));

            var uploadService = new TencentCloudUploadService(
                Options.Create(new TencentCloudSettings()),
                Mock.Of<ILogger<TencentCloudUploadService>>(),
                new System.Net.Http.HttpClient()
            );

            var controller = CreateController(serviceMock.Object, uploadService);

            var result = await controller.SetMobileProductLocation(
                "P001",
                new SetWarehouseProductLocationDto { LocationGuid = "LOC-404" }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = badRequest.Value?.ToString() ?? string.Empty;
            Assert.Contains("货位不存在", payload);
        }

        [Fact]
        public void SetMobileProductLocation_允许设备授权进入方法内校验()
        {
            var method = typeof(ReactProductWarehouseController).GetMethod(
                nameof(ReactProductWarehouseController.SetMobileProductLocation)
            );

            Assert.Single(method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false));
            Assert.Empty(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
        }

        [Fact]
        public async Task SetMobileProductLocation_WhenDeviceAuthorized_CallsBindService()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.SetMobileProductLocationAsync("HB313-129", "A-00-00-01"))
                .ReturnsAsync(
                    new WarehouseMobileProductDto
                    {
                        ProductCode = "HB313-129",
                        LocationGuid = "A-00-00-01",
                        LocationCode = "A-00-00-01",
                    }
                );

            var device = new POSM_设备注册信息表 { 设备硬件识别码 = "device-1", 设备状态 = 1 };
            var deviceServiceMock = new Mock<IDeviceRegistrationService>();
            deviceServiceMock
                .Setup(service => service.ValidateDeviceAuthCodeAsync("device-1", "auth-1"))
                .ReturnsAsync(true);
            deviceServiceMock
                .Setup(service => service.GetDeviceByHardwareIdAsync("device-1"))
                .ReturnsAsync(device);

            var mapperMock = new Mock<IMapper>();
            mapperMock
                .Setup(mapper => mapper.Map<DeviceDataDto>(device))
                .Returns(new DeviceDataDto { HardwareId = "device-1", Status = 1 });

            var controller = CreateController(
                serviceMock.Object,
                deviceService: deviceServiceMock.Object,
                mapper: mapperMock.Object,
                roles: null
            );
            controller.Request.Headers["X-Device-Id"] = "device-1";
            controller.Request.Headers["X-Auth-Code"] = "auth-1";

            var result = await controller.SetMobileProductLocation(
                "HB313-129",
                new SetWarehouseProductLocationDto { LocationGuid = "A-00-00-01" }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.SetMobileProductLocationAsync("HB313-129", "A-00-00-01"),
                Times.Once
            );
        }

        [Fact]
        public async Task SetMobileProductLocation_WhenWarehouseStaff_CallsBindService()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.SetMobileProductLocationAsync("HB313-129", "A-00-00-01"))
                .ReturnsAsync(
                    new WarehouseMobileProductDto
                    {
                        ProductCode = "HB313-129",
                        LocationGuid = "A-00-00-01",
                        LocationCode = "A-00-00-01",
                    }
                );

            var controller = CreateController(serviceMock.Object, roles: new[] { "WarehouseStaff" });

            var result = await controller.SetMobileProductLocation(
                "HB313-129",
                new SetWarehouseProductLocationDto { LocationGuid = "A-00-00-01" }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.SetMobileProductLocationAsync("HB313-129", "A-00-00-01"),
                Times.Once
            );
        }

        [Fact]
        public async Task BatchUpdate_使用ClaimTypesName透传当前用户名给服务()
        {
            var items = new List<UpdateItemDto> { new() { ProductCode = "P001" } };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.BatchUpdateAsync(items, "批量更新人"))
                .ReturnsAsync(new BatchOperationResultDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "批量更新人");
            Assert.Equal("批量更新人", controller.User.FindFirstValue(ClaimTypes.Name));

            var result = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest { Items = items }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.BatchUpdateAsync(items, "批量更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task BatchUpdate_套装成本锁冲突时返回409及逐项失败详情()
        {
            var items = new List<UpdateItemDto>
            {
                new() { ProductCode = "P-BUSY-1" },
                new() { ItemNumber = "ITEM-BUSY-2" },
            };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.BatchUpdateAsync(items, "测试用户"))
                .ThrowsAsync(new SetChildPurchasePriceLockException("test", -1));
            var controller = CreateController(serviceMock.Object);

            var actionResult = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest { Items = items }
            );

            var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
            var payload = conflict.Value!;
            Assert.Equal(
                SetChildPurchasePriceMutationLock.BusyErrorCode,
                payload.GetType().GetProperty("errorCode")!.GetValue(payload)
            );
            var data = payload.GetType().GetProperty("data")!.GetValue(payload)!;
            Assert.Equal(0, data.GetType().GetProperty("successCount")!.GetValue(data));
            Assert.Equal(2, data.GetType().GetProperty("failedCount")!.GetValue(data));
            var failureDetails = Assert.IsAssignableFrom<IEnumerable<BatchOperationFailureDto>>(
                data.GetType().GetProperty("failureDetails")!.GetValue(data)
            );
            Assert.All(
                failureDetails,
                detail =>
                    Assert.Equal(
                        SetChildPurchasePriceMutationLock.BusyErrorCode,
                        detail.ErrorCode
                    )
            );
        }

        [Fact]
        public async Task BatchUpdate_图片本地成功但HQ失败时仍返回200和分级结果()
        {
            var items = new List<UpdateItemDto> { new() { ProductCode = "P-IMAGE-HQ" } };
            var serviceMock = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
            serviceMock
                .Setup(service =>
                    service.BatchUpdateAsync(
                        items,
                        "仓库经理A",
                        It.Is<WarehouseProductBatchUpdateOptionsDto>(options =>
                            options.GenerateImageUrls
                            && options.SyncImageToHq
                            && options.ImageBaseUrl == "https://images.example.com/catalog/"
                        )
                    )
                )
                .ReturnsAsync(
                    new WarehouseProductBatchUpdateResultDto
                    {
                        Success = true,
                        SuccessCount = 1,
                        ImageUpdatedCount = 1,
                        ImageUpdates = new List<ProductHqImageUpdateItemDto>
                        {
                            new()
                            {
                                ProductCode = "P-IMAGE-HQ",
                                ImageUrl = "https://images.example.com/catalog/ITEM-1.jpg",
                            },
                        },
                    }
                );
            var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
            hqSync
                .Setup(service =>
                    service.SyncProductImagesAsync(
                        It.Is<IReadOnlyCollection<ProductHqImageUpdateItemDto>>(updates =>
                            updates.Count == 1
                            && updates.Single().ProductCode == "P-IMAGE-HQ"
                        ),
                        "仓库经理A",
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(
                    new ProductHqImageSyncResultDto
                    {
                        Requested = true,
                        Success = false,
                        FailedCount = 1,
                        ErrorCode = "HQ_IMAGE_SYNC_ITEM_ERRORS",
                        Errors = new List<string> { "HQ 商品不存在: P-IMAGE-HQ" },
                    }
                );
            var controller = CreateController(
                serviceMock.Object,
                username: "仓库经理A",
                productHqSyncService: hqSync.Object
            );

            var actionResult = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = items,
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog///",
                    SyncImageToHq = true,
                }
            );

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var payload = ok.Value!;
            Assert.True((bool)payload.GetType().GetProperty("success")!.GetValue(payload)!);
            Assert.Equal(
                "本地更新完成，HQ 图片同步存在失败",
                payload.GetType().GetProperty("message")!.GetValue(payload)
            );
            var hqPayload = payload.GetType().GetProperty("hqImageSync")!.GetValue(payload)!;
            Assert.False((bool)hqPayload.GetType().GetProperty("Success")!.GetValue(hqPayload)!);
            Assert.Equal(
                "HQ_IMAGE_SYNC_ITEM_ERRORS",
                hqPayload.GetType().GetProperty("ErrorCode")!.GetValue(hqPayload)
            );
            serviceMock.VerifyAll();
            hqSync.VerifyAll();
        }

        [Fact]
        public async Task BatchUpdate_同步HQ但未启用图片生成时返回400且不调用服务()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
            var controller = CreateController(serviceMock.Object, username: "仓库经理A");

            var actionResult = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = new List<UpdateItemDto> { new() { ProductCode = "P-IMAGE-HQ" } },
                    SyncImageToHq = true,
                }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Contains(
                "必须启用图片地址生成",
                badRequest.Value!.GetType().GetProperty("message")!.GetValue(badRequest.Value)!.ToString()
            );
            serviceMock.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BatchUpdate_本地事务失败时不调用HQ图片同步()
        {
            var items = new List<UpdateItemDto> { new() { ProductCode = "P-IMAGE-LOCAL-FAIL" } };
            var serviceMock = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
            serviceMock
                .Setup(service =>
                    service.BatchUpdateAsync(
                        items,
                        "仓库经理A",
                        It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
                    )
                )
                .ReturnsAsync(
                    new WarehouseProductBatchUpdateResultDto
                    {
                        Success = false,
                        Message = "批量更新失败: history insert failed",
                    }
                );
            var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
            var controller = CreateController(
                serviceMock.Object,
                username: "仓库经理A",
                productHqSyncService: hqSync.Object
            );

            var actionResult = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = items,
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/",
                    SyncImageToHq = true,
                }
            );

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var payload = ok.Value!;
            Assert.False((bool)payload.GetType().GetProperty("success")!.GetValue(payload)!);
            Assert.Equal(
                "批量更新失败: history insert failed",
                payload.GetType().GetProperty("message")!.GetValue(payload)
            );
            var hqPayload = payload.GetType().GetProperty("hqImageSync")!.GetValue(payload)!;
            Assert.Equal(
                "HQ_IMAGE_SYNC_LOCAL_UPDATE_FAILED",
                hqPayload.GetType().GetProperty("ErrorCode")!.GetValue(hqPayload)
            );
            serviceMock.VerifyAll();
            hqSync.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task StartBatchUpdateJob_传递完整请求与Claim用户名给后台任务()
        {
            var jobService = new Mock<IWarehouseProductBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartJobAsync(
                    It.Is<WarehouseProductBatchUpdateJobRequestDto>(request =>
                        request.Items.Count == 1
                        && request.Items[0].ProductCode == "P-JOB-1"
                        && request.GenerateImageUrls
                        && request.SyncImageToHq
                        && request.ImageBaseUrl == "https://images.example.com/catalog/"
                    ),
                    "后台操作员",
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new WarehouseProductBatchUpdateJobDto
                {
                    JobId = "batch-job-1",
                    OperationId = "warehouse-product-batch-update:test",
                    Status = WarehouseProductBatchUpdateJobStatusConstants.Queued,
                    CreatedAt = DateTime.UtcNow,
                });
            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                batchUpdateJobService: jobService.Object,
                username: "后台操作员"
            );

            var actionResult = await controller.StartBatchUpdateJob(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = [new UpdateItemDto { ProductCode = "P-JOB-1" }],
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/",
                    SyncImageToHq = true,
                },
                CancellationToken.None
            );

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var payload = ok.Value!;
            var data = payload.GetType().GetProperty("data")!.GetValue(payload)!;
            Assert.Equal("batch-job-1", data.GetType().GetProperty("JobId")!.GetValue(data));
            jobService.VerifyAll();
        }

        [Fact]
        public async Task BatchUpdate_带供货说明时走带选项重载并透传说明()
        {
            // 未开图片生成时旧逻辑走无选项重载；带了下架说明必须改走带选项重载，否则说明会被丢弃。
            var items = new List<UpdateItemDto> { new() { ProductCode = "P-DELIST", IsActive = false } };
            var serviceMock = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
            serviceMock
                .Setup(service =>
                    service.BatchUpdateAsync(
                        items,
                        "仓库经理A",
                        It.Is<WarehouseProductBatchUpdateOptionsDto>(options =>
                            !options.GenerateImageUrls
                            && options.SupplyNotice != null
                            && options.SupplyNotice.SupplyPlan == "Discontinued"
                            && options.SupplyNotice.StoreFacingNote == "厂家停产"
                        )
                    )
                )
                .ReturnsAsync(new WarehouseProductBatchUpdateResultDto { Success = true, SuccessCount = 1 });
            var controller = CreateController(serviceMock.Object, username: "仓库经理A");

            var actionResult = await controller.BatchUpdate(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = items,
                    SupplyNotice = new WarehouseProductSupplyNoticeInputDto
                    {
                        SupplyPlan = "Discontinued",
                        StoreFacingNote = "厂家停产",
                    },
                }
            );

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
            serviceMock.VerifyAll();
        }

        [Fact]
        public async Task StartBatchUpdateJob_透传供货说明给后台任务()
        {
            var jobService = new Mock<IWarehouseProductBatchUpdateJobService>(MockBehavior.Strict);
            jobService
                .Setup(service => service.StartJobAsync(
                    It.Is<WarehouseProductBatchUpdateJobRequestDto>(request =>
                        request.Items.Count == 1
                        && request.Items[0].IsActive == false
                        && request.SupplyNotice != null
                        && request.SupplyNotice.SupplyPlan == "WillRestock"
                        && request.SupplyNotice.ExpectedPrecision == "Month"
                        && request.SupplyNotice.ExpectedFrom == new DateOnly(2099, 3, 1)
                    ),
                    "后台操作员",
                    It.IsAny<CancellationToken>()
                ))
                .ReturnsAsync(new WarehouseProductBatchUpdateJobDto
                {
                    JobId = "batch-job-notice",
                    OperationId = "warehouse-product-batch-update:notice",
                    Status = WarehouseProductBatchUpdateJobStatusConstants.Queued,
                    CreatedAt = DateTime.UtcNow,
                });
            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                batchUpdateJobService: jobService.Object,
                username: "后台操作员"
            );

            var actionResult = await controller.StartBatchUpdateJob(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = [new UpdateItemDto { ProductCode = "P-JOB-NOTICE", IsActive = false }],
                    SupplyNotice = new WarehouseProductSupplyNoticeInputDto
                    {
                        SupplyPlan = "WillRestock",
                        ExpectedPrecision = "Month",
                        ExpectedFrom = new DateOnly(2099, 3, 1),
                    },
                },
                CancellationToken.None
            );

            Assert.IsType<OkObjectResult>(actionResult);
            jobService.VerifyAll();
        }

        [Fact]
        public async Task GetBatchUpdateJob_任务不存在时返回404()
        {
            var jobService = new Mock<IWarehouseProductBatchUpdateJobService>();
            jobService
                .Setup(service => service.GetJobAsync("missing-job", It.IsAny<CancellationToken>()))
                .ReturnsAsync((WarehouseProductBatchUpdateJobDto?)null);
            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                batchUpdateJobService: jobService.Object
            );

            var actionResult = await controller.GetBatchUpdateJob(
                "missing-job",
                CancellationToken.None
            );

            var notFound = Assert.IsType<NotFoundObjectResult>(actionResult);
            Assert.Contains("已过期或服务已重启", notFound.Value!.ToString());
        }

        [Fact]
        public async Task StartBatchUpdateJob_队列已满时返回429()
        {
            var jobService = new Mock<IWarehouseProductBatchUpdateJobService>();
            jobService
                .Setup(service => service.StartJobAsync(
                    It.IsAny<WarehouseProductBatchUpdateJobRequestDto>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ))
                .ThrowsAsync(new WarehouseProductBatchUpdateQueueFullException());
            var controller = CreateController(
                Mock.Of<IProductWarehouseReactService>(),
                batchUpdateJobService: jobService.Object
            );

            var actionResult = await controller.StartBatchUpdateJob(
                new ReactProductWarehouseController.BatchUpdateRequest
                {
                    Items = [new UpdateItemDto { ProductCode = "P-JOB-2" }],
                },
                CancellationToken.None
            );

            var tooManyRequests = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status429TooManyRequests, tooManyRequests.StatusCode);
            Assert.Contains("队列已满", tooManyRequests.Value!.ToString());
        }

        [Fact]
        public async Task BatchCreate_传递当前用户名给服务()
        {
            var items = new List<CreateItemDto> { new() };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.BatchCreateAsync(items, true, "商品更新人"))
                .ReturnsAsync(new BatchOperationResultDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.BatchCreate(
                new ReactProductWarehouseController.BatchCreateRequest { Items = items }
            );

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.BatchCreateAsync(items, true, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task CreateSingle_传递当前用户名给服务()
        {
            var request = new CreateSingleProductRequestDto { ProductCode = "P001" };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.CreateSingleProductAsync(request, "商品更新人"))
                .ReturnsAsync(new CreateSingleProductResponseDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.CreateSingle(request);

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.CreateSingleProductAsync(request, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task ImportFromDomestic_传递当前用户名给服务()
        {
            var request = new ImportFromDomesticRequestDto { ProductCodes = new List<string> { "P001" } };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.ImportFromDomesticAsync(request, "商品更新人"))
                .ReturnsAsync(new ImportFromDomesticResponseDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.ImportFromDomestic(request);

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.ImportFromDomesticAsync(request, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task ImportNonHotbargain_传递当前用户名给服务()
        {
            var request = new ImportNonHotbargainRequestDto
            {
                ProductCodes = new List<string> { "P001" },
            };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.ImportNonHotbargainProductsAsync(request, "商品更新人"))
                .ReturnsAsync(new ImportFromDomesticResponseDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.ImportNonHotbargain(request);

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.ImportNonHotbargainProductsAsync(request, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task FullUpdate_传递当前用户名给服务()
        {
            var dto = new WarehouseProductFullUpdateDto { ProductName = "测试商品" };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.FullUpdateAsync("P001", dto, "商品更新人"))
                .ReturnsAsync(new WarehouseProductFullUpdateResultDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.FullUpdate("P001", dto);

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.FullUpdateAsync("P001", dto, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public async Task BatchToggleActive_传递当前用户名给服务()
        {
            var request = new BatchToggleWarehouseProductsActiveRequestDto
            {
                ProductCodes = new List<string> { "P001" },
                IsActive = true,
            };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.BatchToggleActiveAsync(request, "商品更新人"))
                .ReturnsAsync(new BatchToggleWarehouseProductsActiveResultDto { Success = true });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.BatchToggleActive(request);

            Assert.IsType<OkObjectResult>(result);
            serviceMock.Verify(
                service => service.BatchToggleActiveAsync(request, "商品更新人"),
                Times.Once
            );
        }

        [Fact]
        public void Patch_仅允许Admin和WarehouseManager()
        {
            var authorizeAttribute = GetSingleAuthorizeAttribute(
                nameof(ReactProductWarehouseController.Patch)
            );

            Assert.Equal("Admin,WarehouseManager", authorizeAttribute.Roles);
        }

        [Fact]
        public async Task Patch_成功时_传递当前用户名给服务()
        {
            var dto = new WarehouseProductPatchDto { ImportPrice = 5.55m };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.PatchAsync("P001", dto, "商品更新人"))
                .ReturnsAsync(new WarehouseProductPatchResultDto { Success = true, Message = "保存成功" });

            var controller = CreateController(serviceMock.Object, username: "商品更新人");

            var result = await controller.Patch("P001", dto);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("保存成功", ok.Value?.ToString());
            serviceMock.Verify(service => service.PatchAsync("P001", dto, "商品更新人"), Times.Once);
        }

        [Fact]
        public async Task Patch_套装成本锁冲突时返回409及Busy错误码()
        {
            var dto = new WarehouseProductPatchDto { ImportPrice = 5.55m };
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service => service.PatchAsync("P001", dto, "测试用户"))
                .ThrowsAsync(new SetChildPurchasePriceLockException("test", -1));
            var controller = CreateController(serviceMock.Object);

            var actionResult = await controller.Patch("P001", dto);

            var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
            Assert.Equal(
                SetChildPurchasePriceMutationLock.BusyErrorCode,
                conflict.Value!.GetType().GetProperty("errorCode")!.GetValue(conflict.Value)
            );
        }

        [Fact]
        public async Task Patch_无字段_返回400()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            var controller = CreateController(serviceMock.Object);

            var result = await controller.Patch("P001", new WarehouseProductPatchDto());

            Assert.IsType<BadRequestObjectResult>(result);
            serviceMock.Verify(
                service =>
                    service.PatchAsync(
                        It.IsAny<string>(),
                        It.IsAny<WarehouseProductPatchDto>(),
                        It.IsAny<string>()
                    ),
                Times.Never
            );
        }

        [Fact]
        public async Task Patch_多字段_返回400()
        {
            var controller = CreateController(Mock.Of<IProductWarehouseReactService>());

            var result = await controller.Patch(
                "P001",
                new WarehouseProductPatchDto { DomesticPrice = 1m, ImportPrice = 2m }
            );

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Patch_负值_返回400()
        {
            var controller = CreateController(Mock.Of<IProductWarehouseReactService>());

            var result = await controller.Patch(
                "P001",
                new WarehouseProductPatchDto { MinOrderQuantity = -1 }
            );

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Patch_商品不存在_返回404()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service =>
                    service.PatchAsync(
                        "P001",
                        It.IsAny<WarehouseProductPatchDto>(),
                        "测试用户"
                    )
                )
                .ReturnsAsync((WarehouseProductPatchResultDto?)null);
            var controller = CreateController(serviceMock.Object);

            var result = await controller.Patch(
                "P001",
                new WarehouseProductPatchDto { OEMPrice = 1m }
            );

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Patch_服务抛InvalidOperationException_返回500()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service =>
                    service.PatchAsync(
                        "P001",
                        It.IsAny<WarehouseProductPatchDto>(),
                        It.IsAny<string>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("参数无效"));
            var controller = CreateController(serviceMock.Object);

            var result = await controller.Patch(
                "P001",
                new WarehouseProductPatchDto { ImportPrice = 1m }
            );

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, objectResult.StatusCode);
        }

        [Fact]
        public async Task Patch_服务异常_返回500()
        {
            var serviceMock = new Mock<IProductWarehouseReactService>();
            serviceMock
                .Setup(service =>
                    service.PatchAsync(
                        "P001",
                        It.IsAny<WarehouseProductPatchDto>(),
                        It.IsAny<string>()
                    )
                )
                .ThrowsAsync(new Exception("boom"));
            var controller = CreateController(serviceMock.Object);

            var result = await controller.Patch(
                "P001",
                new WarehouseProductPatchDto { ImportPrice = 1m }
            );

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, objectResult.StatusCode);
        }

        private static AuthorizeAttribute GetSingleAuthorizeAttribute(string methodName)
        {
            var method = typeof(ReactProductWarehouseController).GetMethod(methodName);
            var authorizeAttribute = Assert.Single(
                method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            );

            return Assert.IsType<AuthorizeAttribute>(authorizeAttribute);
        }

        private static ReactProductWarehouseController CreateController(
            IProductWarehouseReactService service,
            TencentCloudUploadService? uploadService = null,
            IWarehouseProductHqSyncJobService? jobService = null,
            IWarehouseProductBatchUpdateJobService? batchUpdateJobService = null,
            IDeviceRegistrationService? deviceService = null,
            IMapper? mapper = null,
            string[]? roles = null,
            string username = "测试用户",
            ILogger<ReactProductWarehouseController>? logger = null,
            IWarehouseProductChangeHistoryService? changeHistoryService = null,
            ICurrentUserService? currentUserService = null,
            IProductHqSyncService? productHqSyncService = null
        )
        {
            roles ??= new[] { "Admin" };
            var httpContext = new DefaultHttpContext();
            if (roles.Length > 0)
            {
                var claims = roles.Select(role => new Claim(ClaimTypes.Role, role))
                    .Append(new Claim(ClaimTypes.Name, username));
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            var controller = new ReactProductWarehouseController(
                service,
                jobService ?? Mock.Of<IWarehouseProductHqSyncJobService>(),
                batchUpdateJobService ?? Mock.Of<IWarehouseProductBatchUpdateJobService>(),
                logger ?? Mock.Of<ILogger<ReactProductWarehouseController>>(),
                deviceService ?? Mock.Of<IDeviceRegistrationService>(),
                mapper ?? Mock.Of<IMapper>(),
                uploadService ?? CreateUploadService(),
                changeHistoryService ?? Mock.Of<IWarehouseProductChangeHistoryService>(),
                currentUserService ?? Mock.Of<ICurrentUserService>(service =>
                    service.GetCurrentUsername() == username
                    && service.GetCurrentUserGuid() == string.Empty
                ),
                productHqSyncService ?? Mock.Of<IProductHqSyncService>()
            );
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private static TencentCloudUploadService CreateUploadService()
        {
            return new TencentCloudUploadService(
                Options.Create(new TencentCloudSettings()),
                Mock.Of<ILogger<TencentCloudUploadService>>(),
                new System.Net.Http.HttpClient()
            );
        }
    }
}
