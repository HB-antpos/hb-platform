using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「查看国内供应商名称」权限：没有它的账号，接口返回里的国内供应商名称、店铺号、联系方式一律为空（只留代码），
/// 也不能按名称搜索 / 排序。测试值一律用 ASCII：默认 JSON 编码会把中文转成 \uXXXX，直接比对字符串会失真。
/// </summary>
public sealed class DomesticSupplierInfoMaskingTests
{
    private const string Code = "HB196";
    private const string Name = "Yiwu Test Crafts Factory";
    private const string ShopNumber = "A1-1234";
    private const string Contact = "Zhang San";
    private const string Phone = "13800000000";

    // ===== 转换器与作用域 =====

    [Fact]
    public void JsonConverter_作用域外原样输出_作用域内输出空_且不修改对象()
    {
        var dto = NewSupplierDto();

        var raw = JsonSerializer.Serialize(dto);
        string masked;
        using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: true))
        {
            masked = JsonSerializer.Serialize(dto);
        }
        var afterScope = JsonSerializer.Serialize(dto);

        Assert.Contains(Name, raw);
        Assert.Contains(Code, masked);
        foreach (var secret in new[] { Name, ShopNumber, Contact, Phone, "a@example.com", "storefront.jpg", "private remark" })
        {
            Assert.DoesNotContain(secret, masked);
        }
        Assert.Contains(Name, afterScope);
        // 缓存里被多个账号共用的对象不能被改写。
        Assert.Equal(Name, dto.SupplierName);
        Assert.Equal(Phone, dto.Phone);
        // 读取方向原样。
        Assert.Equal(Name, JsonSerializer.Deserialize<ChinaSupplierDto>(raw)!.SupplierName);
        // 作用域内不写入 null 值的全局设置下，被遮掉的字段照样输出 null 而不是原值。
        using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: true))
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            var camel = JsonSerializer.Serialize(dto, options);
            Assert.DoesNotContain(Name, camel);
            Assert.Contains("\"supplierCode\":\"HB196\"", camel);
        }
    }

    [Fact]
    public void BeginMaskingScope_false_不遮罩_嵌套后恢复外层状态()
    {
        var dto = NewSupplierDto();
        using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: true))
        {
            using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: false))
            {
                Assert.Contains(Name, JsonSerializer.Serialize(dto));
            }
            Assert.True(DomesticSupplierInfoMasking.IsMaskingActive);
        }
        Assert.False(DomesticSupplierInfoMasking.IsMaskingActive);
    }

    /// <summary>
    /// 反射门禁：共享程序集里所有名为 DomesticSupplierName / ChinaSupplierName 的字符串属性都必须挂遮罩转换器。
    /// 以后新增返回字段只要沿用这两个名字，漏挂就会在这里失败。
    /// </summary>
    [Fact]
    public void 所有国内供应商名称命名的属性_都挂了遮罩转换器()
    {
        var offenders = typeof(ChinaSupplierDto).Assembly
            .GetTypes()
            .SelectMany(type => type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            ))
            .Where(property =>
                property.PropertyType == typeof(string)
                && property.Name is "DomesticSupplierName" or "ChinaSupplierName"
            )
            .Where(property => !HasMaskingConverter(property))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "以下属性缺少 DomesticSupplierInfoJsonConverter：" + string.Join(", ", offenders));
    }

    /// <summary>名字不带前缀、但数据来源是国内供应商的返回字段：逐个点名，防止被改回去。</summary>
    [Fact]
    public void 已知的国内供应商敏感字段_逐个确认已挂遮罩转换器()
    {
        string[] supplierFields =
        {
            "SupplierName", "ShopNumber", "ContactPerson", "Phone", "Email", "StorefrontPhoto", "Remarks",
        };
        (Type Type, string[] Properties)[] expected =
        {
            (typeof(ChinaSupplierDto), supplierFields),
            (typeof(DomesticSupplierDto), supplierFields),
            (typeof(ChinaSupplier), supplierFields),
            (typeof(DomesticProductBatchDto), new[] { "SupplierName" }),
            (typeof(DomesticProductBatchDetailDto), new[] { "SupplierName" }),
            (typeof(DomesticProductDto), new[] { "SupplierName" }),
            (typeof(BatchProductDetectionResultDto), new[] { "SupplierName" }),
            (typeof(DomesticSetProductDto), new[] { "SupplierName" }),
            (typeof(ProductDto), new[] { "DomesticSupplierName" }),
            (typeof(ProductGradeDto), new[] { "SupplierName" }),
            (typeof(ProductPrefixCodeDto), new[] { "SupplierName" }),
            (typeof(DomesticProductNotInWarehouseDto), new[] { "SupplierName" }),
            (typeof(WarehouseMobileProductDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductLabelPrintDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductReactListDto), new[] { "SupplierName", "DomesticSupplierName" }),
            (typeof(ChinaSupplierSalesRankDto), new[] { "SupplierName" }),
            (typeof(ChinaSupplierStoreSalesDto), new[] { "SupplierName" }),
            (typeof(ChinaSupplierStoreSalesDetailDto), new[] { "SupplierName" }),
            (typeof(CompactSalesBoardChinaSupplierDto), new[] { "SupplierName" }),
            (typeof(CompactSalesBoardProductDto), new[] { "ChinaSupplierName" }),
            (typeof(StoreOrderProductDto), new[] { "DomesticSupplierName" }),
            (typeof(StoreOrderImportPriceVarianceItemDto), new[] { "SupplierName" }),
            (typeof(StoreOrderImportPriceVarianceSupplierSummaryDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductBatchDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductFlowProductDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductFlowContainerDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductFlowCandidateDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductFlowSupplierOptionDto), new[] { "Name" }),
            (typeof(WarehouseProductInsightProductDto), new[] { "SupplierName" }),
            (typeof(WarehouseProductListDto), new[] { "DomesticSupplierName" }),
        };

        foreach (var (type, properties) in expected)
        {
            foreach (var name in properties)
            {
                var property = type.GetProperty(name);
                Assert.True(property != null, $"{type.Name}.{name} 不存在");
                Assert.True(HasMaskingConverter(property!), $"{type.Name}.{name} 缺少 DomesticSupplierInfoJsonConverter");
            }
        }

        // 供应商代码永远不遮罩。
        Assert.False(HasMaskingConverter(typeof(ChinaSupplierDto).GetProperty("SupplierCode")!));
        Assert.False(HasMaskingConverter(typeof(ChinaSupplier).GetProperty("SupplierCode")!));
    }

    [Fact]
    public void 国内供应商引用列表_遮罩时只输出代码_澳洲供应商列表不受影响()
    {
        var options = new ProductSalesAnalysisOptionsDto
        {
            AustralianSuppliers = { new ProductSalesSupplierRefDto { Code = "240", Name = "Aussie Supplier" } },
            ChinaSuppliers = { new ProductSalesSupplierRefDto { Code = Code, Name = Name } },
        };
        var row = new ProductSalesProductRowDto
        {
            AustralianSuppliers = { new ProductSalesSupplierRefDto { Code = "240", Name = "Aussie Supplier" } },
            ChinaSuppliers = { new ProductSalesSupplierRefDto { Code = Code, Name = Name } },
        };

        var raw = JsonSerializer.Serialize(options);
        string maskedOptions;
        string maskedRow;
        using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: true))
        {
            maskedOptions = JsonSerializer.Serialize(options);
            maskedRow = JsonSerializer.Serialize(row);
        }

        Assert.Contains(Name, raw);
        foreach (var masked in new[] { maskedOptions, maskedRow })
        {
            Assert.DoesNotContain(Name, masked);
            Assert.Contains(Code, masked);
            Assert.Contains("Aussie Supplier", masked);
        }
        // 输出的是临时副本，原列表里的对象不变。
        Assert.Equal(Name, options.ChinaSuppliers[0].Name);
        var roundTrip = JsonSerializer.Deserialize<ProductSalesAnalysisOptionsDto>(raw)!;
        Assert.Equal(Name, roundTrip.ChinaSuppliers.Single().Name);
        Assert.Equal("Aussie Supplier", roundTrip.AustralianSuppliers.Single().Name);
    }

    // ===== 全局过滤器 =====

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 结果过滤_按权限决定响应序列化是否遮罩(bool hasPermission)
    {
        var httpContext = CreateHttpContext(CreateServices(hasPermission, out _), "user-1");
        var payload = new { success = true, data = NewSupplierDto() };
        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new OkObjectResult(payload),
            controller: new object()
        );
        string? serialized = null;

        await new DomesticSupplierInfoMaskingFilter().OnResultExecutionAsync(
            context,
            () =>
            {
                // 模拟输出格式化器在 next() 中序列化响应。
                serialized = JsonSerializer.Serialize(payload);
                return Task.FromResult(
                    new ResultExecutedContext(context, context.Filters, context.Result, context.Controller)
                );
            }
        );

        Assert.NotNull(serialized);
        Assert.Contains(Code, serialized);
        Assert.Equal(hasPermission, serialized!.Contains(Name));
        Assert.Equal(hasPermission, serialized.Contains(Phone));
        // 过滤器结束后作用域恢复，不影响后续的内部序列化。
        Assert.False(DomesticSupplierInfoMasking.IsMaskingActive);
    }

    [Fact]
    public async Task 结果过滤_JsonResult同样遮罩_文件结果不查权限()
    {
        var services = CreateServices(hasPermission: false, out var authorization);
        var payload = NewSupplierDto();
        var jsonContext = new ResultExecutingContext(
            new ActionContext(CreateHttpContext(services, "user-json"), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new JsonResult(payload),
            controller: new object()
        );
        string? serialized = null;
        await new DomesticSupplierInfoMaskingFilter().OnResultExecutionAsync(
            jsonContext,
            () =>
            {
                serialized = JsonSerializer.Serialize(payload);
                return Task.FromResult(
                    new ResultExecutedContext(jsonContext, jsonContext.Filters, jsonContext.Result, jsonContext.Controller)
                );
            }
        );
        Assert.DoesNotContain(Name, serialized);

        authorization.Invocations.Clear();
        var fileContext = new ResultExecutingContext(
            new ActionContext(CreateHttpContext(services, "user-file"), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new FileContentResult(Array.Empty<byte>(), "application/octet-stream"),
            controller: new object()
        );
        var maskingDuringFile = true;
        await new DomesticSupplierInfoMaskingFilter().OnResultExecutionAsync(
            fileContext,
            () =>
            {
                maskingDuringFile = DomesticSupplierInfoMasking.IsMaskingActive;
                return Task.FromResult(
                    new ResultExecutedContext(fileContext, fileContext.Filters, fileContext.Result, fileContext.Controller)
                );
            }
        );
        Assert.False(maskingDuringFile);
        Assert.Empty(authorization.Invocations);
    }

    /// <summary>
    /// 接口方法执行期间只关「按名称搜索」，绝不能开遮罩：服务层在这期间把对象序列化进缓存、
    /// 同步给 HQ 或记审计时必须拿到原值，否则空名称会被写出去。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 动作过滤_只控制名称搜索开关_执行期间不遮罩(bool hasPermission)
    {
        var httpContext = CreateHttpContext(CreateServices(hasPermission, out _), "user-2");
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object()
        );
        bool? searchAllowedInside = null;
        bool? exportVisibleInside = null;
        string? serializedInside = null;

        await new DomesticSupplierInfoMaskingFilter().OnActionExecutionAsync(
            context,
            () =>
            {
                searchAllowedInside = DomesticSupplierInfoMasking.IsNameSearchAllowed;
                exportVisibleInside = DomesticSupplierInfoMasking.IsNameVisibleInCurrentAction;
                serializedInside = JsonSerializer.Serialize(NewSupplierDto());
                return Task.FromResult(
                    new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())
                );
            }
        );

        Assert.Equal(hasPermission, searchAllowedInside);
        Assert.Equal(hasPermission, exportVisibleInside);
        Assert.Contains(Name, serializedInside);
        // 过滤器结束后恢复：请求之外恒允许。
        Assert.True(DomesticSupplierInfoMasking.IsNameSearchAllowed);
    }

    [Fact]
    public async Task 权限结果_同一请求只算一次_同一账号跨请求走缓存_不同账号各算各的()
    {
        var services = CreateServices(hasPermission: true, out var authorization);

        var first = CreateHttpContext(services, "cache-user-a");
        Assert.True(await first.CanViewDomesticSupplierNameAsync());
        Assert.True(await first.CanViewDomesticSupplierNameAsync());
        var second = CreateHttpContext(services, "cache-user-a");
        Assert.True(await second.CanViewDomesticSupplierNameAsync());
        authorization.Verify(
            service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                Permissions.DomesticPurchase.ViewSupplierName
            ),
            Times.Once
        );

        var other = CreateHttpContext(services, "cache-user-b");
        Assert.True(await other.CanViewDomesticSupplierNameAsync());
        authorization.Verify(
            service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                Permissions.DomesticPurchase.ViewSupplierName
            ),
            Times.Exactly(2)
        );
        Assert.Equal(TimeSpan.FromSeconds(60), DomesticSupplierInfoVisibilityExtensions.CacheDuration);
    }

    [Fact]
    public async Task 权限结果_未登录或拿不到授权服务时按无权限处理()
    {
        Assert.False(await ((HttpContext?)null).CanViewDomesticSupplierNameAsync());
        Assert.False(await new DefaultHttpContext().CanViewDomesticSupplierNameAsync());

        // 未登录（含设备头放行的 PDA）：即使授权服务会放行也不查，直接按无权限。
        var services = CreateServices(hasPermission: true, out var authorization);
        var anonymous = new DefaultHttpContext { RequestServices = services };
        Assert.False(await anonymous.CanViewDomesticSupplierNameAsync());
        Assert.Empty(authorization.Invocations);
    }

    // ===== 名称搜索开关 =====

    [Fact]
    public void GuardNameKeyword_请求之外原样_无权限时换成匹配不到的占位关键词()
    {
        Assert.Equal("Yiwu", DomesticSupplierInfoMasking.GuardNameKeyword("Yiwu"));
        using (DomesticSupplierInfoMasking.BeginQueryScope(canView: true))
        {
            Assert.Equal("Yiwu", DomesticSupplierInfoMasking.GuardNameKeyword("Yiwu"));
        }
        using (DomesticSupplierInfoMasking.BeginQueryScope(canView: false))
        {
            Assert.Equal(
                DomesticSupplierInfoMasking.BlockedNameKeyword,
                DomesticSupplierInfoMasking.GuardNameKeyword("Yiwu")
            );
            Assert.False(DomesticSupplierInfoMasking.IsNameSearchAllowed);
            // 查询作用域不等于遮罩作用域。
            Assert.False(DomesticSupplierInfoMasking.IsMaskingActive);
        }
        Assert.True(DomesticSupplierInfoMasking.IsNameSearchAllowed);
        // 占位关键词全小写（部分查询会先把关键词转小写再比对）且不含 LIKE 通配符。
        Assert.Equal(
            DomesticSupplierInfoMasking.BlockedNameKeyword,
            DomesticSupplierInfoMasking.BlockedNameKeyword.ToLowerInvariant()
        );
        Assert.DoesNotContain('%', DomesticSupplierInfoMasking.BlockedNameKeyword);
        Assert.DoesNotContain('_', DomesticSupplierInfoMasking.BlockedNameKeyword);
        Assert.DoesNotContain('[', DomesticSupplierInfoMasking.BlockedNameKeyword);
    }

    [Fact]
    public async Task 供应商列表_无名称权限时只能按代码搜索_按名称排序退回按代码排()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        var db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        try
        {
            db.CodeFirst.InitTables(typeof(ChinaSupplier));
            // 名称顺序与代码顺序刻意相反：按名称排是 HB300, HB200, HB100；按代码排是 HB100, HB200, HB300。
            db.Insertable(
                    new[]
                    {
                        SupplierEntity("HB100", "Zeta Crafts", "Wang"),
                        SupplierEntity("HB200", "Mid Toys", "Li"),
                        SupplierEntity("HB300", "Alpha Gifts", "Zhao"),
                    }
                )
                .ExecuteCommand();
            var service = new ChinaSupplierService(
                CreateSqlSugarContext(db),
                null!,
                NullLogger<ChinaSupplierService>.Instance
            );

            async Task<string[]> QueryAsync(string? search, string? sortField)
            {
                var result = await service.GetChinaSuppliersAsync(
                    new ChinaSupplierQueryDto
                    {
                        Page = 1,
                        PageSize = 20,
                        Search = search,
                        SortField = sortField,
                        SortDirection = "asc",
                    }
                );
                Assert.True(result.Success, result.Message);
                return result.Data!.Items!.Select(item => item.SupplierCode!).ToArray();
            }

            // 有权限（或请求之外）：名称、联系人都能搜，按名称排序生效。
            Assert.Equal(new[] { "HB300" }, await QueryAsync("Alpha", null));
            Assert.Equal(new[] { "HB200" }, await QueryAsync("Li", null));
            Assert.Equal(new[] { "HB300", "HB200", "HB100" }, await QueryAsync(null, "supplierName"));

            using (DomesticSupplierInfoMasking.BeginQueryScope(canView: false))
            {
                Assert.Empty(await QueryAsync("Alpha", null));
                Assert.Empty(await QueryAsync("Li", null));
                Assert.Equal(new[] { "HB300" }, await QueryAsync("HB3", null));
                Assert.Equal(new[] { "HB100", "HB200", "HB300" }, await QueryAsync(null, "supplierName"));
                Assert.Equal(new[] { "HB100", "HB200", "HB300" }, await QueryAsync(null, "contactPerson"));
            }
        }
        finally
        {
            db.Dispose();
            connection.Dispose();
            SqliteConnection.ClearAllPools();
            SqliteTempFileCleanup.DeleteIfExists(dbPath);
        }
    }

    // ===== 打不了属性标记的出口 =====

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 供应商下拉接口_匿名对象按权限手工处理(bool hasPermission)
    {
        var supplierService = new Mock<IDomesticSupplierReactService>();
        supplierService
            .Setup(service => service.GetActiveSupplierListAsync())
            .ReturnsAsync(
                new List<DomesticSupplierDto>
                {
                    new()
                    {
                        SupplierCode = Code,
                        SupplierName = Name,
                        ShopNumber = ShopNumber,
                        ContactPerson = Contact,
                        Phone = Phone,
                    },
                }
            );
#pragma warning disable CS0618 // 控制器已标记 Obsolete，但线上仍在用，必须覆盖。
        var controller = new ReactSuppliersController(
            supplierService.Object,
            NullLogger<ReactSuppliersController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(CreateServices(hasPermission, out _), "user-3"),
            },
        };
#pragma warning restore CS0618

        var result = Assert.IsType<OkObjectResult>(await controller.GetActiveSupplierList());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains(Code, json);
        foreach (var secret in new[] { Name, ShopNumber, Contact, Phone })
        {
            Assert.Equal(hasPermission, json.Contains(secret));
        }
    }

    [Fact]
    public async Task 修改国内供应商_无名称权限时整单拒绝_不调用服务()
    {
        var supplierService = new Mock<IChinaSupplierService>();
        var controller = new ChinaSuppliersController(
            supplierService.Object,
            NullLogger<ChinaSuppliersController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(CreateServices(hasPermission: false, out _), "user-4"),
            },
        };

        var result = Assert.IsType<ObjectResult>(
            await controller.UpdateChinaSupplier("guid-1", new UpdateChinaSupplierDto { SupplierCode = Code })
        );

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        supplierService.Verify(
            service => service.UpdateChinaSupplierAsync(It.IsAny<string>(), It.IsAny<UpdateChinaSupplierDto>()),
            Times.Never
        );
    }

    [Fact]
    public void 共用DTO的去名称副本_不改动原对象()
    {
        var row = new SalesDetailRowDto { Code = Code, Name = Name, Revenue = 12.5m, Quantity = 3 };
        var section = new SalesDetailSectionResultDto
        {
            Rows = { row },
            Total = 1,
            Summary = new SalesDetailRowDto { Code = "TOTAL", Name = "Total" },
            OrderCountNote = "note",
        };

        var masked = section.WithoutRowNames();

        Assert.NotSame(section, masked);
        Assert.Equal(string.Empty, masked.Rows.Single().Name);
        Assert.Equal(Code, masked.Rows.Single().Code);
        Assert.Equal(12.5m, masked.Rows.Single().Revenue);
        Assert.Equal(3, masked.Rows.Single().Quantity);
        Assert.Equal(1, masked.Total);
        Assert.Same(section.Summary, masked.Summary);
        Assert.Equal("note", masked.OrderCountNote);
        Assert.Equal(Name, row.Name);
        Assert.Equal(Name, section.Rows.Single().Name);

        var supplierRow = new StoreSupplierSalesDto
        {
            BranchCode = "1004",
            SupplierCode = Code,
            SupplierName = Name,
            TotalAmount = 99m,
        };
        var maskedSupplierRow = supplierRow.WithSupplierCodeAsName();
        Assert.Equal(Code, maskedSupplierRow.SupplierName);
        Assert.Equal(99m, maskedSupplierRow.TotalAmount);
        Assert.Equal("1004", maskedSupplierRow.BranchCode);
        Assert.Equal(Name, supplierRow.SupplierName);
    }

    // ===== 权限登记 =====

    [Fact]
    public void 新权限码_只进仓库管理角色模板_管理国内供应商自动具备_其余国内采购权限不自动具备()
    {
        const string code = Permissions.DomesticPurchase.ViewSupplierName;
        Assert.Equal("DomesticPurchase.ViewSupplierName", code);

        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == code);
        Assert.Equal("查看国内供应商名称", seed.Name);
        Assert.Equal("国内采购", seed.Category);

        // 默认只对仓库管理员开放（管理员由权限处理器自动放行，不进模板）。
        var grantedRoles = PermissionSeedData.RolePermissionTemplates
            .Where(template => template.PermissionCodes.Contains(code))
            .Select(template => template.RoleName)
            .ToArray();
        Assert.Equal(new[] { "WarehouseManager" }, grantedRoles);

        // 别名方向：持有「管理国内供应商」即满足本权限；反向不成立，其余国内采购权限也不自动具备。
        Assert.Contains(
            Permissions.DomesticPurchase.ManageSuppliers,
            Permissions.GetEquivalentPermissionCodes(code)
        );
        Assert.DoesNotContain(
            Permissions.DomesticPurchase.ManageProducts,
            Permissions.GetEquivalentPermissionCodes(code)
        );
        Assert.DoesNotContain(
            Permissions.DomesticPurchase.ManagePrefixCodes,
            Permissions.GetEquivalentPermissionCodes(code)
        );
        Assert.DoesNotContain(
            code,
            Permissions.GetEquivalentPermissionCodes(Permissions.DomesticPurchase.ManageSuppliers)
        );
        Assert.Contains(
            code,
            Permissions.ExpandPermissionCodes(new[] { Permissions.DomesticPurchase.ManageSuppliers })
        );
        Assert.DoesNotContain(
            code,
            Permissions.ExpandPermissionCodes(
                new[]
                {
                    Permissions.DomesticPurchase.ManageProducts,
                    Permissions.DomesticPurchase.ManagePrefixCodes,
                }
            )
        );

        Assert.Equal(new[] { "web", "mobile" }, PermissionSurfaceCatalog.ResolveNames(code));
    }

    [Fact]
    public async Task 入库脚本_与种子的权限码名称描述一致_只授予仓库管理角色()
    {
        var sql = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/Migrations/20261105_AddDomesticSupplierNamePermission.sql"
            )
        );
        var seed = Assert.Single(
            PermissionSeedData.AllPermissions,
            item => item.Code == Permissions.DomesticPurchase.ViewSupplierName
        );

        Assert.Contains($"N'{seed.Code}'", sql);
        Assert.Contains($"N'{seed.Name}'", sql);
        Assert.Contains($"N'{seed.Description}'", sql);
        Assert.Contains($"N'{seed.Category}'", sql);
        Assert.Contains("INSERT INTO @RoleNames (RoleName) VALUES (N'WarehouseManager');", sql);
        // 角色名只插入这一次：不授予仓库员工、店长、员工、订货员等其他角色。
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "INSERT INTO @RoleNames"));
        // 只登记与授予，不改、不删已有数据。
        Assert.DoesNotContain("DELETE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ===== 工具 =====

    private static ChinaSupplierDto NewSupplierDto() =>
        new()
        {
            Guid = "guid-1",
            SupplierCode = Code,
            SupplierName = Name,
            ShopNumber = ShopNumber,
            ContactPerson = Contact,
            Phone = Phone,
            Email = "a@example.com",
            StorefrontPhoto = "storefront.jpg",
            Remarks = "private remark",
        };

    private static ChinaSupplier SupplierEntity(string code, string name, string contact) =>
        new()
        {
            Guid = $"guid-{code}",
            SupplierCode = code,
            SupplierName = name,
            ContactPerson = contact,
            Status = 1,
            FGC_CreateDate = "2026-01-01 10:00:00",
            FGC_LastModifyDate = "2026-01-01 10:00:00",
            IsDeleted = false,
        };

    private static bool HasMaskingConverter(PropertyInfo property) =>
        property.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType
        == typeof(DomesticSupplierInfoJsonConverter);

    private static ServiceProvider CreateServices(bool hasPermission, out Mock<IAuthorizationService> authorization)
    {
        authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                Permissions.DomesticPurchase.ViewSupplierName
            ))
            .ReturnsAsync(hasPermission ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return new ServiceCollection()
            .AddSingleton(authorization.Object)
            .AddMemoryCache()
            .BuildServiceProvider();
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services, string userId) =>
        new()
        {
            RequestServices = services,
            User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth")
            ),
        };

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static string FindRepoRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
