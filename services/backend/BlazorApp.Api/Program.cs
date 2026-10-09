// ===================== 引用和命名空间 =====================
using System.Linq;
using System.Security.Claims;
using System.Text; // 字符串编码
using AutoMapper; // AutoMapper映射服务
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Data; // 数据访问层
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.StoreOrders;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces; // 数据模型
using BlazorApp.Api.Interfaces.React; // React 接口命名空间
using BlazorApp.Api.Mappings; // AutoMapper 映射配置
using BlazorApp.Api.Middleware;
using BlazorApp.Api.Models;
using BlazorApp.Api.Repositories;
using BlazorApp.Api.Repositories.Interfaces;
using BlazorApp.Shared.Security;
using BlazorApp.Api.Services; // 业务服务层
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.Background; // 后台定时服务
using BlazorApp.Api.Services.Logging;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Api.Services.OperationAudits;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Api.Services.Pricing; // 自动定价服务
using BlazorApp.Api.Services.RustDeskCompat;
using BlazorApp.Api.Services.React; // React 专用服务层
using BlazorApp.Api.Utils; // Cookie 配置辅助类
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer; // JWT Bearer认证
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens; // JWT令牌验证

// ===================== 应用程序入口点 =====================
// 创建WebApplicationBuilder实例，读取命令行参数和配置文件
// 这是ASP.NET Core 6+的新式启动方式，替代了传统的Startup.cs
// 显式数据库模式还承载受目标门禁保护的一次性维护命令，所有模式均禁止启动 HTTP 与后台任务。
var schemaCommand = SchemaCommand.Parse(args);
if (schemaCommand.Mode == SchemaCommandMode.Invalid)
{
    Console.Error.WriteLine($"SCHEMA_COMMAND_INVALID: {schemaCommand.Error}");
    Environment.ExitCode = SchemaExitCodes.ConfigurationError;
    return;
}

var builder = WebApplication.CreateBuilder(args);

MapTencentCloudEnvironmentVariables(builder.Configuration);

if (
    schemaCommand.Mode == SchemaCommandMode.Server
    && builder.Configuration.GetValue<bool>("Database:InitializeOnStartup", false)
)
{
    Console.Error.WriteLine(
        "SCHEMA_LEGACY_INITIALIZE_ON_STARTUP_FORBIDDEN: 请关闭 Database:InitializeOnStartup，并改用 --schema=migrate。"
    );
    Environment.ExitCode = SchemaExitCodes.ConfigurationError;
    return;
}

if (schemaCommand.Mode != SchemaCommandMode.Server)
{
    // 显式 schema 命令只构建迁移所需服务，不注册 HTTP、Swagger、HostedService 或后台任务。
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<
        BlazorApp.Api.Services.ICurrentUserService,
        BlazorApp.Api.Services.CurrentUserService
    >();
    builder.Services.AddScoped<SqlSugarContext>();
    builder.Services.AddScoped<POSMSqlSugarContext>();
    builder.Services.AddScoped<SchemaMigrationCoordinator>();
    builder.Services.AddScoped<RemoteMaintenanceSchemaMigrator>();
    builder.Services.AddScoped<RustDeskClientSchemaMigrator>();
    builder.Services.AddScoped<SalesDetailQueryProjectionMaintenanceRunner>();

    WebApplication schemaApp;
    try
    {
        schemaApp = builder.Build();
    }
    catch (Exception)
    {
        Console.Error.WriteLine("SCHEMA_HOST_BUILD_FAILED");
        Environment.ExitCode = SchemaExitCodes.ConfigurationError;
        return;
    }

    var explicitSchemaResult = schemaCommand.Mode is SchemaCommandMode.RemoteMaintenance or SchemaCommandMode.RemoteMaintenanceCheck
        ? await ExecuteRemoteMaintenanceSchemaOperationAsync(schemaApp.Services, schemaCommand.Mode == SchemaCommandMode.RemoteMaintenanceCheck)
        : schemaCommand.Mode is SchemaCommandMode.RustDeskClient or SchemaCommandMode.RustDeskClientCheck
            ? await ExecuteRustDeskClientSchemaOperationAsync(schemaApp.Services, schemaCommand.Mode == SchemaCommandMode.RustDeskClientCheck)
            : schemaCommand.Mode is SchemaCommandMode.SalesDetailProjectionBackfill or SchemaCommandMode.SalesDetailProjectionCheck
                ? await ExecuteSalesDetailProjectionOperationAsync(
                    schemaApp.Services,
                    schemaCommand.Mode == SchemaCommandMode.SalesDetailProjectionCheck)
                : await ExecuteSchemaOperationAsync(schemaApp.Services, schemaCommand.Mode);
    if (!explicitSchemaResult.Success)
    {
        schemaApp.Logger.LogError(
            "数据库 schema 操作失败。模式={Mode}，诊断码={DiagnosticCode}，退出码={ExitCode}",
            schemaCommand.Mode,
            explicitSchemaResult.DiagnosticCode,
            explicitSchemaResult.ExitCode
        );
    }
    else
    {
        schemaApp.Logger.LogInformation(
            "数据库 schema 操作完成。模式={Mode}，诊断码={DiagnosticCode}",
            schemaCommand.Mode,
            explicitSchemaResult.DiagnosticCode
        );
    }

    Environment.ExitCode = explicitSchemaResult.ExitCode;
    // 关键位置：默认控制台日志由后台线程异步写出，只有释放 host（连带 LoggerFactory）才会排空队列；
    // 不释放直接 return，进程退出时上面的诊断日志可能整条丢失。退出码已先写入，释放不改变其语义。
    await schemaApp.DisposeAsync();
    return;
}

// ===================== 服务注册区域 =====================

// --------------------- 基础Web API服务 ---------------------
// 注册MVC控制器服务，启用基于控制器的API端点
builder
    .Services.AddControllers(options =>
    {
        // 仓库改价后把"已通知几家分店"的汇总写入响应头，供前端提示"已发送到通知列表"。
        options.Filters.Add<BlazorApp.Api.Filters.PriceNotificationHeaderFilter>();
    })
    .AddJsonOptions(options =>
    {
        // 使用 camelCase 命名策略（前端 JavaScript 标准）
        options.JsonSerializerOptions.PropertyNamingPolicy = System
            .Text
            .Json
            .JsonNamingPolicy
            .CamelCase;
        options.JsonSerializerOptions.ReferenceHandler = System
            .Text
            .Json
            .Serialization
            .ReferenceHandler
            .IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System
            .Text
            .Json
            .Serialization
            .JsonIgnoreCondition
            .WhenWritingNull;
    })
    .AddMvcOptions(options =>
    {
        options.Filters.Add<BlazorApp.Api.Filters.ApiExceptionFilter>();
    });

// 注册HttpClient服务，供翻译服务等需要HTTP调用的服务使用
builder.Services.AddHttpClient();

// 注册API终结点浏览器，用于自动发现API端点
// 这是Swagger/OpenAPI文档生成的基础服务
builder.Services.AddEndpointsApiExplorer();

// 注册Swagger生成器服务，用于生成API文档和测试界面
// 仅在开发环境中启用，生产环境建议关闭
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc(
        "v1",
        new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "HB Platform API",
            Version = "v1",
            Description = "HB Platform多店铺管理系统API文档",
            Contact = new Microsoft.OpenApi.Models.OpenApiContact
            {
                Name = "HB Platform开发团队",
                Email = "dev@hbplatform.com",
            },
        }
    );

    // 配置JWT认证
    c.AddSecurityDefinition(
        "Bearer",
        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Description =
                "JWT授权(数据将在请求头中进行传输) 参数结构: \"Authorization: Bearer {token}\" 注意：在Swagger中输入时，格式为 'Bearer ' + 空格 + 令牌值，不要包含大括号{}",
            Name = "Authorization",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            BearerFormat = "JWT",
            Scheme = "Bearer",
        }
    );

    c.AddSecurityRequirement(
        new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                },
                new string[] { }
            },
        }
    );

    // 添加XML注释支持
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }

    c.CustomSchemaIds(type => type.FullName);
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

    c.OperationFilter<PDAHeaderOperationFilter>();
});

// 获取HttpContext的服务（用于在服务层读取当前用户）
builder.Services.AddHttpContextAccessor();

// 初始化 Cookie 配置辅助类
CookieOptionsHelper.Initialize(builder.Configuration, builder.Environment);

// 注册内存缓存服务
builder.Services.AddMemoryCache();
var dataProtectionKeysPath = builder.Configuration.GetValue<string>("DataProtection:KeysPath");
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionKeysPath = Path.Combine("App_Data", "DataProtectionKeys");
}

if (!Path.IsPathRooted(dataProtectionKeysPath))
{
    dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, dataProtectionKeysPath);
}

Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName(
        builder.Configuration.GetValue<string>("DataProtection:ApplicationName")
        ?? "HB.AdminPlatform"
    )
    // SMTP 密码需要跨重启/部署解密，key ring 必须落在稳定目录，目录本身不提交到 git。
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
// 远程维护 secret 复用主后端持久化 key ring，仅在代码层使用独立 purpose 隔离。
builder.Services.AddSingleton<BlazorApp.Api.Security.RemoteMaintenanceSecretProtector>(sp =>
    BlazorApp.Api.Security.RemoteMaintenanceDataProtection.CreateProtector(
        sp.GetRequiredService<IDataProtectionProvider>()));

var attendanceQrDataProtectionKeysPath = builder.Configuration.GetValue<string>(
    "AttendanceQrDataProtection:KeysPath");
if (string.IsNullOrWhiteSpace(attendanceQrDataProtectionKeysPath))
{
    // 关键逻辑：生产环境禁止回退到容器内部目录，避免考勤密钥在重建后丢失或与 POS 不一致。
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "生产环境必须配置 AttendanceQrDataProtection:KeysPath。");
    }

    attendanceQrDataProtectionKeysPath = Path.Combine("App_Data", "AttendanceQrDataProtectionKeys");
}

if (!Path.IsPathRooted(attendanceQrDataProtectionKeysPath))
{
    attendanceQrDataProtectionKeysPath = Path.Combine(
        builder.Environment.ContentRootPath,
        attendanceQrDataProtectionKeysPath);
}

// 关键逻辑：考勤二维码使用专用 key ring，避免 POS 获得全局 Data Protection 密钥。
Directory.CreateDirectory(attendanceQrDataProtectionKeysPath);
var attendanceQrDataProtectionProvider =
    BlazorApp.Api.Security.AttendanceQrKeyDataProtection.CreateProvider(
            attendanceQrDataProtectionKeysPath);
builder.Services.AddSingleton(
    BlazorApp.Api.Security.AttendanceQrKeyDataProtection.CreateProtector(
        attendanceQrDataProtectionProvider));
// 关键逻辑：短时打卡凭证复用考勤专用 key ring，但使用独立 purpose 与签码密钥隔离。
builder.Services.AddSingleton(
    BlazorApp.Api.Security.AttendancePunchAuthorizationDataProtection.CreateProtector(
        attendanceQrDataProtectionProvider));

var linklyCredentialDataProtectionKeysPath = builder.Configuration.GetValue<string>(
    "LinklyCloudCredentialDataProtection:KeysPath");
if (string.IsNullOrWhiteSpace(linklyCredentialDataProtectionKeysPath))
{
    // 关键逻辑：Linkly 终端凭据需被 Admin 与 POS API 共同解密，生产不能落入容器临时目录。
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "生产环境必须配置 LinklyCloudCredentialDataProtection:KeysPath。");
    }

    // 非生产回落到与 POS API 相同的用户级目录（两个进程内容根不同，各用 App_Data 会让密钥环永远不一致）。
    linklyCredentialDataProtectionKeysPath =
        BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection.ResolveSharedDevelopmentKeysPath()
        ?? Path.Combine("App_Data", "LinklyCloudCredentialDataProtectionKeys");
}

if (!Path.IsPathRooted(linklyCredentialDataProtectionKeysPath))
{
    linklyCredentialDataProtectionKeysPath = Path.Combine(
        builder.Environment.ContentRootPath,
        linklyCredentialDataProtectionKeysPath);
}

Directory.CreateDirectory(linklyCredentialDataProtectionKeysPath);
var linklyCredentialDataProtectionProvider =
    BlazorApp.Api.Security.LinklyCloudTerminalCredentialDataProtection.CreateProvider(
        linklyCredentialDataProtectionKeysPath);
builder.Services.AddSingleton<ILinklyCloudTerminalCredentialProtector>(
    BlazorApp.Api.Security.LinklyCloudTerminalCredentialDataProtection.CreateProtector(
        linklyCredentialDataProtectionProvider));

builder.Services.Configure<TencentCloudSettings>(builder.Configuration.GetSection("TencentCloud"));
builder.Services.Configure<ApplicationLoggingOptions>(
    builder.Configuration.GetSection("ApplicationLogging")
);
builder.Services.Configure<PerformanceMetricsOptions>(
    builder.Configuration.GetSection("PerformanceMetrics")
);
builder.Services.Configure<SentryReleaseHealthOptions>(
    builder.Configuration.GetSection(SentryReleaseHealthOptions.SectionName)
);
builder.Services.AddSingleton<IApplicationLogQueue, ApplicationLogQueue>();
builder.Services.AddSingleton<ApplicationLogRateLimiter>();
builder.Services.AddSingleton<ILoggerProvider, ApplicationLogLoggerProvider>();
builder.Services.AddHostedService<ApplicationLogBackgroundService>();
builder.Services.AddHostedService<ApplicationLogCleanupService>();
builder.Services.AddHostedService<OperationAuditCleanupBackgroundService>();
builder.Services.AddSingleton<PerformanceMetricBuffer>();
builder.Services.AddSingleton<PerformanceMetricAggregateStore>();
builder.Services.AddSingleton<PerformanceCollectorCoordinator>();
builder.Services.AddSingleton<IPerformanceMetricRecorder>(provider =>
    provider.GetRequiredService<PerformanceMetricBuffer>()
);
builder.Services.AddScoped<PerformanceMetricService>();
builder.Services.AddScoped<PerformanceClientIngestRateLimiter>();
builder.Services.AddHostedService<PerformanceMetricFlushService>();
builder.Services.AddHostedService<AspNetCoreRequestMetricListener>();
builder.Services.AddHostedService<SqlPerformanceAttachmentService>();
builder.Services.AddSingleton<PerformanceOperationalRunQueue>();
builder.Services.AddHostedService<PerformanceOperationalRunWriterService>();
builder.Services.AddHostedService<PerformanceBacklogSamplerService>();
builder.Services.AddHostedService<PerformanceRetentionService>();
builder.Services.AddHttpClient<SentryReleaseHealthClient>();
builder.Services.AddHostedService<SentryReleaseHealthSyncService>();
builder.Services.AddScoped<
    IProductStoreDailyStatisticQueueService,
    ProductStoreDailyStatisticQueueService
>();
builder.Services.AddScoped<IProductStoreDailyStatisticExecutor>(provider =>
    provider.GetRequiredService<SalesStatisticsJobService>()
);
builder.Services.AddScoped<HBSalesRecordStatisticsService>();
builder.Services.AddScoped<ScheduledTaskLogService>();
builder.Services.AddScoped<ScheduledTaskRetryService>();
builder.Services.AddScoped<ScheduledTaskRuntimeControlService>();
builder.Services.AddScoped<ScheduledTaskLeaseService>();
builder.Services.AddScoped<SalesCostBackfillService>();
builder.Services.AddHostedService<SalesCostBackfillWorker>();
builder.Services.AddScoped<SalesStatisticsAlignmentService>();
builder.Services.Configure<ScheduledTaskOptions>(
    builder.Configuration.GetSection("ScheduledTasks")
);
builder.Services.AddHostedService<ProductStoreDailyStatisticRecoveryService>();
builder.Services.AddHostedService<SalesStatisticsOrphanRunningStateRecoveryService>();
builder.Services.AddHostedService<ReportReadConnectionWarmupService>();
builder.Services.AddHostedService<ScheduledTaskService>();

// --------------------- CORS配置 ---------------------
// 🌐 配置跨域资源共享策略，支持 Cookie 认证方案
// ⚠️ 重要：AllowCredentials() 必须与 WithOrigins() 一起使用，不能与 AllowAnyOrigin() 一起使用
// 原因：当使用凭据时，必须明确指定允许的源，不能使用通配符 "*"
var configuredCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
var corsOrigins =
    configuredCorsOrigins?.Length > 0
        ? configuredCorsOrigins
        : new[]
        {
            // 🔧 开发环境默认域名
            "http://localhost:8000", // 前端开发服务器
            "http://localhost:3000", // 备用前端端口
            "http://localhost:5002", // 后端 API 端口
            "https://localhost", // 支持 HTTPS localhost（宝塔面板）
            // 🌐 生产环境域名
            "https://www.dats.com.au",
            "https://www.malmar.com.au",
            "https://www.yatstal.com.au",
            "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
            "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com",
            "http://hotbargain.vip",
        };

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowSpecific",
        policy =>
        {
            // ✅ 配置 CORS 策略
            policy
                .WithOrigins(corsOrigins) // 📍 指定允许的源（域名列表）
                .AllowAnyMethod() // 🔓 允许所有 HTTP 方法（GET, POST, PUT, DELETE 等）
                .AllowAnyHeader() // 🔓 允许所有请求头
                // 跨域场景下浏览器默认读不到自定义响应头，必须显式暴露。
                .WithExposedHeaders(BlazorApp.Api.Filters.PriceNotificationHeaderFilter.HeaderName)
                .AllowCredentials(); // 🍪 允许发送凭据（Cookie、Authorization 头等）
            // ⚠️ 这是 Cookie 认证的关键配置
            // ✅ 前端请求时必须设置 withCredentials: true 或 credentials: 'include'
        }
    );
});

// Mobile 开通、重绑与设备会话交换按可信客户端 IP 独立限流。
builder.Services.AddRateLimiter(MobileDeviceActivationRateLimits.Configure);

// 浏览器扩展一次性授权按父会话限流，匿名兑换按可信客户端 IP 限流。
builder.Services.AddRateLimiter(BrowserExtensionSessionGrantRateLimits.Configure);
builder.Services.AddRateLimiter(RustDeskLoginRateLimits.Configure);
builder.Services.AddRateLimiter(MinorGuardianRateLimits.Configure);
builder.Services.AddRateLimiter(PasswordResetRateLimits.Configure);
builder.Services.AddRateLimiter(BrowserExtensionCaptureRateLimits.Configure);

// --------------------- JWT认证配置 ---------------------
// 🔐 配置JSON Web Token（JWT）身份验证
// JWT是一种无状态的、基于令牌的认证机制，适合前后端分离架构
// 工作原理：用户登录后获得JWT令牌，后续请求携带此令牌进行身份验证

// 📋 从配置文件（appsettings.json）读取JWT相关设置
// 包括：密钥、签发者、受众、过期时间等配置
var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>();

// 🔑 将JWT密钥字符串转换为字节数组，用于令牌签名和验证
// 使用HMAC-SHA256算法进行签名，确保令牌的完整性和真实性
// ⚠️ 安全警告：生产环境中密钥应足够复杂且定期更换
var key = Encoding.UTF8.GetBytes(jwtSettings!.Key);

// 🏗️ 配置ASP.NET Core认证服务，使用JWT Bearer认证方案
// Bearer认证：客户端在HTTP请求头中携带"Bearer {token}"进行认证
var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    // 默认认证保持 JWT，service token 只能在显式声明的自动化端点使用，避免越权访问普通 [Authorize]。
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
});

authenticationBuilder.AddPolicyScheme(
    ServiceApiTokenAuthenticationDefaults.PolicyScheme,
    "Bearer JWT or Service API Token",
    options =>
    {
        options.ForwardDefaultSelector = context =>
            ServiceApiTokenAuthenticationDefaults.RequestHasServiceApiToken(context.Request)
                ? ServiceApiTokenAuthenticationDefaults.AuthenticationScheme
                : JwtBearerDefaults.AuthenticationScheme;
    }
);
authenticationBuilder.AddScheme<AuthenticationSchemeOptions, ServiceApiTokenAuthenticationHandler>(
    ServiceApiTokenAuthenticationDefaults.AuthenticationScheme,
    ConfigureServiceApiTokenAuthentication
);
authenticationBuilder.AddJwtBearer(options =>
    {
        // 🔧 配置JWT令牌验证参数
        // 这些参数决定了如何验证传入的JWT令牌
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, // ✅ 验证JWT签发者（iss声明）
            ValidateAudience = true, // ✅ 验证JWT受众（aud声明）
            ValidateLifetime = true, // ✅ 验证JWT有效期（exp和nbf声明）
            ValidateIssuerSigningKey = true, // ✅ 验证JWT签名密钥
            ValidIssuer = jwtSettings.Issuer, // 🏢 设置有效的签发者
            ValidAudience = jwtSettings.Audience, // 👥 设置有效的受众
            IssuerSigningKey = new SymmetricSecurityKey(key), // 🔑 设置用于验证签名的密钥

            // 🔧 以下配置解决角色和用户名声明映射问题
            // 这是授权系统的关键配置，确保JWT中的角色信息能正确映射到ASP.NET Core的授权系统
            RoleClaimType = System.Security.Claims.ClaimTypes.Role, // 🎭 指定角色声明类型，用于[Authorize(Roles="Admin")]
            NameClaimType = System.Security.Claims.ClaimTypes.Name, // 👤 指定用户名声明类型，用于HttpContext.User.Identity.Name
        };

        // 🍪 从 Cookie 中读取 token（支持 Cookie 认证方案）
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // 优先从 Authorization header 读取 token
                var accessToken = context
                    .Request.Headers["Authorization"]
                    .FirstOrDefault()
                    ?.Split(" ")
                    .Last();

                // 如果 header 中没有 token，尝试从 Cookie 读取
                if (string.IsNullOrEmpty(accessToken))
                {
                    context.Request.Cookies.TryGetValue("access_token", out accessToken);
                }

                // 如果找到 token，设置到 Token 属性
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var identity = principal?.Identities.FirstOrDefault(identity => identity.IsAuthenticated);
                if (principal == null || identity == null)
                {
                    context.Fail("无效的认证主体");
                    return;
                }

                var userGuid =
                    principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst("userId")?.Value
                    ?? principal.FindFirst("userGuid")?.Value
                    ?? principal.FindFirst("uid")?.Value
                    ?? principal.FindFirst(ClaimTypes.Name)?.Value
                    ?? principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

                if (string.IsNullOrWhiteSpace(userGuid))
                {
                    context.Fail("令牌缺少用户标识");
                    return;
                }

                var dbContext = context.HttpContext.RequestServices.GetRequiredService<SqlSugarContext>();
                var authSessionValidator = context.HttpContext.RequestServices
                    .GetRequiredService<IAuthSessionValidator>();

                IReadOnlyList<string> activeRoleNames;
                if (string.Equals(
                        principal.FindFirst("token_use")?.Value,
                        MobileDeviceAccountTokenIssuer.TokenUse,
                        StringComparison.Ordinal))
                {
                    var validation = await authSessionValidator.ValidateMobileDeviceAccessAsync(
                        userGuid,
                        principal,
                        context.HttpContext.RequestAborted);
                    if (!validation.IsValid)
                    {
                        context.Fail("用户、设备绑定或登录会话已失效");
                        return;
                    }
                    // 仅复用本次请求认证时的实时门店快照，避免认证后控制器重复查询且不形成跨请求缓存。
                    context.HttpContext.Items[typeof(AuthMobileDeviceValidationResult)] = validation;
                    activeRoleNames = validation.ActiveRoleNames;
                }
                else
                {
                    var sessionId = principal.FindFirst("sessionId")?.Value;
                    var validation = await authSessionValidator.ValidateWebAccessSessionAsync(
                        userGuid,
                        sessionId,
                        context.HttpContext.RequestAborted);

                    if (!validation.IsValid)
                    {
                        context.Fail("登录会话已失效");
                        return;
                    }

                    activeRoleNames = validation.ActiveRoleNames;
                }

                var staleClaims = identity.Claims
                    .Where(claim =>
                        claim.Type == ClaimTypes.Role
                        || claim.Type == "permission"
                    )
                    .ToList();

                foreach (var staleClaim in staleClaims)
                {
                    identity.RemoveClaim(staleClaim);
                }

                foreach (var roleName in activeRoleNames)
                {
                    identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Role, roleName));
                }
            },
        };
    });

// 🛡️ 注册动态权限授权服务
builder.Services.AddSingleton<
    Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
    BlazorApp.Api.Authorization.PermissionPolicyProvider
>();
builder.Services.AddScoped<
    Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    BlazorApp.Api.Authorization.PermissionAuthorizationHandler
>();

// --------------------- 依赖注入服务注册 ---------------------
// 🏗️ 配置依赖注入容器，注册应用程序所需的各种服务

// 🔑 当前用户服务 - 用于在数据库操作中自动填充审计字段（CreatedBy/UpdatedBy）
builder.Services.AddScoped<
    BlazorApp.Api.Services.ICurrentUserService,
    BlazorApp.Api.Services.CurrentUserService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.ICurrentUserManageableStoreScopeService,
    BlazorApp.Api.Services.CurrentUserManageableStoreScopeService
>();

// 📁 数据库上下文服务 - 使用作用域模式（Scoped）
// 说明：将原先的 Singleton 改为 Scoped，避免多个请求并发复用同一底层连接
// 好处：每个 HTTP 请求内共享一个上下文实例，请求结束自动释放，减少"连接未关闭/正在连接"的并发冲突
builder.Services.AddScoped<SqlSugarContext>(); // 主数据库上下文（每请求一个实例）
builder.Services.AddScoped<HqSqlSugarContext>(); // HQ总部数据库上下文（每请求一个实例）
builder.Services.AddScoped<HBSalesSqlSugarContext>(); // HBSales数据库上下文（每请求一个实例）
builder.Services.AddScoped<POSMSqlSugarContext>(); // POSM数据库上下文（每请求一个实例）
builder.Services.AddScoped<SchemaMigrationCoordinator>();
builder.Services.Configure<BlazorApp.Shared.Options.DeviceActivationOptions>(
    builder.Configuration.GetSection(BlazorApp.Shared.Options.DeviceActivationOptions.SectionName));
builder.Services.Configure<BlazorApp.Shared.Options.MobileDeviceActivationOptions>(
    builder.Configuration.GetSection(
        BlazorApp.Shared.Options.MobileDeviceActivationOptions.SectionName
    )
);
builder.Services.AddScoped<HBSalesRecordSqlSugarContext>(); // HBSalesRecord数据库上下文（每请求一个实例）
builder.Services.AddScoped<OperationAuditQueryService>(sp =>
    new OperationAuditQueryService(
        sp.GetRequiredService<POSMSqlSugarContext>().Db,
        sp.GetRequiredService<ICurrentUserManageableStoreScopeService>(),
        sp.GetRequiredService<IHttpContextAccessor>(),
        // 商品图片在 HBweb 主档里。
        sp.GetRequiredService<SqlSugarContext>().Db,
        sp.GetRequiredService<ILogger<OperationAuditQueryService>>()
    )
);
builder.Services.AddScoped<OperationAuditReviewService>(sp =>
    new OperationAuditReviewService(
        sp.GetRequiredService<POSMSqlSugarContext>().Db,
        sp.GetRequiredService<OperationAuditQueryService>(),
        sp.GetRequiredService<BlazorApp.Api.Services.ICurrentUserService>()
    )
);
builder.Services.AddScoped<OperationAuditRetentionService>(sp =>
    new OperationAuditRetentionService(sp.GetRequiredService<POSMSqlSugarContext>().Db)
);
builder.Services.AddScoped<BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogQueryService>(sp =>
    new BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogQueryService(
        sp.GetRequiredService<POSMSqlSugarContext>().Db,
        sp.GetRequiredService<ICurrentUserManageableStoreScopeService>(),
        sp.GetRequiredService<ILogger<BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogQueryService>>()
    )
);
builder.Services.AddScoped<BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogReviewService>(sp =>
    new BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogReviewService(
        sp.GetRequiredService<POSMSqlSugarContext>().Db,
        sp.GetRequiredService<ICurrentUserManageableStoreScopeService>(),
        sp.GetRequiredService<BlazorApp.Api.Services.ICurrentUserService>()
    )
);
// 员工操作日志异常扫描（老收银 + 新收银）：默认关闭，迁移执行并观察后在配置 LegacyEmployeeLogRisk:Enabled 打开；
// 新收银部分另受 LegacyEmployeeLogRisk:PosAuditEnabled（默认开）控制。
builder.Services.Configure<BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskOptions>(
    builder.Configuration.GetSection(BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskOptions.SectionName));
builder.Services.AddHostedService<BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskScanWorker>();
builder.Services.AddScoped<ILinklySettlementQueryService, LinklySettlementQueryService>();
builder.Services.AddSingleton<ILinklySettlementAmountParser, LinklySettlementAmountParser>();
builder.Services.AddSingleton<LinklySettlementExcelExporter>();
// 卡付款对账异常只读查询：异常表由 Hbpos.Api 建表并写入（订单同步核对 + 后台对账作业），后台只读。
builder.Services.AddScoped<ICardTenderReconciliationQueryService, CardTenderReconciliationQueryService>();
// 日结记录只读查询：日结在 POSM 库（表由 Hbpos.Api 启动时创建，后台不建表），门店名称与时区在 HBweb 门店表。
builder.Services.AddScoped<BlazorApp.Api.Services.DailyCloses.IDailyCloseQueryService>(sp =>
    new BlazorApp.Api.Services.DailyCloses.DailyCloseQueryService(
        sp.GetRequiredService<POSMSqlSugarContext>().Db,
        sp.GetRequiredService<SqlSugarContext>().Db,
        sp.GetRequiredService<ICurrentUserManageableStoreScopeService>(),
        sp.GetRequiredService<IHttpContextAccessor>(),
        sp.GetRequiredService<ILogger<BlazorApp.Api.Services.DailyCloses.DailyCloseQueryService>>()
    )
);
builder.Services.AddScoped<ApplicationLogService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApplicationLoggingOptions>>();
    var logger = sp.GetRequiredService<ILogger<ApplicationLogService>>();
    var queue = sp.GetRequiredService<IApplicationLogQueue>();
    return new ApplicationLogService(context.Db, options, logger, queue);
});
builder.Services.AddScoped(typeof(IRepository<>), typeof(SqlSugarRepository<>));
builder.Services.AddScoped<IStoreRetailPriceRepository, StoreRetailPriceRepository>();

builder.Services.AddScoped<MigrationScripts>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    var logger = sp.GetRequiredService<ILogger<MigrationScripts>>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var currentUser = sp.GetRequiredService<ICurrentUserService>();
    return new MigrationScripts(context.Db, logger, loggerFactory, currentUser);
});

// PostgreSQL数据库上下文（每请求一个实例）
builder.Services.AddScoped<PostgresSqlSugarContext>(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("PostgresConnection")!;
    var logger = provider.GetRequiredService<ILogger<PostgresSqlSugarContext>>();
    return new PostgresSqlSugarContext(connectionString, logger, configuration);
});

// 🔄 数据同步服务
builder.Services.AddScoped<DataSyncService>(); // 数据同步服务
builder.Services.AddScoped<PostgresDataSyncService>(); // PostgreSQL数据同步服务

// --------------------- AutoMapper配置 ---------------------
// 注册AutoMapper服务，自动扫描程序集中所有继承自Profile的类
// AutoMapper可以大大简化数据传输对象和领域模型之间的转换代码
// 例如：UserDto -> User, CreateOrderDto -> Order 等
// 注册AutoMapper服务，自动扫描程序集中所有继承自Profile的类
builder.Services.AddAutoMapper(cfg => { }, typeof(MappingProfile).Assembly);

// 🔧 业务服务层 - 使用作用域模式（Scoped）
// 作用域：每个HTTP请求创建一次实例，请求结束时销毁
// 适合包含状态或需要事务管理的业务服务
builder.Services.AddScoped<IAuthService, AuthService>(); // 认证服务
builder.Services.AddScoped<IAuthSessionValidator, AuthSessionValidator>(); // access token 会话有效性校验
builder.Services.AddScoped<BrowserExtensionSessionGrantService>(); // 网站 Cookie 会话与扩展短期令牌交换
builder.Services.AddScoped<IMobileDeviceActivationService, MobileDeviceActivationService>();
builder.Services.AddScoped<
    IMobileDeviceActivationCodeManagementService,
    MobileDeviceActivationCodeManagementService
>();
builder.Services.AddScoped<IMobileDeviceAccountTokenIssuer, MobileDeviceAccountTokenIssuer>();
builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>(); // 登录公网 IP 解析
builder.Services.AddScoped<IServiceApiTokenService, ServiceApiTokenService>(); // 后台自动化 service API token
builder.Services.AddScoped<IUserService, UserService>(); // 用户管理服务
builder.Services.AddScoped<
    IUserStorePosTerminalPermissionService,
    UserStorePosTerminalPermissionService
>();
builder.Services.AddScoped<IEmployeeProfileService, EmployeeProfileService>(); // 员工个人信息服务
builder.Services.AddScoped<EmployeeMinorComplianceService>(); // 未成年用工档案、家长签署和 HR 审核
builder.Services.AddScoped<MinorEmploymentReminderService>(); // 经理待办只记录提醒，不阻断排班或打卡
builder.Services.AddScoped<IEmployeeMinorDocumentStore, CosEmployeeMinorDocumentStore>();
builder.Services.Configure<MinorEmploymentOptions>(builder.Configuration.GetSection(MinorEmploymentOptions.SectionName));
builder.Services.AddScoped<IMinorGuardianEmailSender, MinorGuardianEmailSender>(); // 监护人签署链接与验证码邮件，复用发票邮箱的公司 SMTP 账号
builder.Services.AddScoped<IAccountPasswordEmailSender, AccountPasswordEmailSender>(); // 员工设置 / 找回密码验证码邮件，同样复用发票邮箱的公司 SMTP 账号
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>(); // 员工邮箱验证码设置 / 找回密码
builder.Services.AddScoped<IAccountEmailChangeService, AccountEmailChangeService>(); // 员工绑定 / 更换账号邮箱（验证码发到新邮箱）
builder.Services.AddHttpClient("minor-employment-documents");
builder.Services.AddScoped<EmployeeProfileSensitiveChangeService>();
builder.Services.AddScoped<EmployeeProfileMediaService>();
builder.Services.AddScoped<EmployeeCashierBarcodeService>();
builder.Services.AddScoped<StoreUserCashierBarcodeService>();
builder.Services.AddHostedService<EmployeeImageUploadCleanupBackgroundService>();
builder.Services.AddScoped<IRoleService, RoleService>(); // 角色管理服务
builder.Services.AddScoped<IStoreService, StoreService>(); // 分店管理服务
builder.Services.AddScoped<StoreSyncService>(); // 分店数据同步服务
builder.Services.AddScoped<BlazorApp.Api.Services.StoreReceiptProfiles.IStoreReceiptProfileService, BlazorApp.Api.Services.StoreReceiptProfiles.StoreReceiptProfileService>(); // 门店小票资料下发（快照 / 设备应用情况）
builder.Services.AddScoped<SeedDataService>(); // 种子数据初始化服务
builder.Services.AddScoped<IDataInitializationService, DataInitializationService>(); // 数据初始化服务
builder.Services.Configure<InvoiceEmailOptions>(builder.Configuration.GetSection("InvoiceEmail"));
builder.Services.Configure<EasWebhookOptions>(builder.Configuration.GetSection("EasWebhook"));
builder.Services.Configure<MobileAppBuildOptions>(
    builder.Configuration.GetSection(MobileAppBuildOptions.SectionName)
);
builder.Services.Configure<AppUpdatePolicyOptions>(
    builder.Configuration.GetSection("AppUpdatePolicy")
);
builder.Services.Configure<BrowserExtensionOptions>(
    builder.Configuration.GetSection(BrowserExtensionOptions.SectionName)
);
builder.Services.Configure<RemoteMaintenanceOptions>(
    builder.Configuration.GetSection(RemoteMaintenanceOptions.SectionName));
builder.Services.AddScoped<RemoteMaintenanceService>();
builder.Services.AddScoped<RemoteMaintenanceSchemaReadiness>();
builder.Services.AddScoped<IRustDeskCompatService, RustDeskCompatService>();
builder.Services.AddScoped<RustDeskCompatSchemaReadiness>();
builder.Services
    .AddOptions<PosHandheldUpdatePolicyOptions>()
    .Bind(builder.Configuration.GetSection("PosHandheldUpdatePolicy"))
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<PosHandheldUpdatePolicyOptions>,
    PosHandheldUpdatePolicyOptionsValidator
>();
builder.Services.AddScoped<IInvoiceEmailSettingsService, InvoiceEmailSettingsService>();
builder.Services.AddScoped<IInvoiceEmailService, InvoiceEmailService>();
builder.Services.AddScoped<PaymentTerminalSettingsService>();
builder.Services.AddScoped<EmergencyLoginGrantService>();
builder.Services.AddScoped<EmergencyLoginKeyManagementService>();
builder.Services.AddHttpClient<TencentCosMobileAppBuildArtifactMirror>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IMobileAppBuildArtifactMirror>(sp =>
    sp.GetRequiredService<TencentCosMobileAppBuildArtifactMirror>()
);
// 商品图片版本号：HEAD 探测 COS 图片修改时间；禁止跳转，防止白名单域名 302 到内网。
builder.Services.AddHttpClient<ProductImageVersionService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IChinaSupplierService, ChinaSupplierService>(); // 国内供应商管理服务
builder.Services.AddScoped<IDomesticSupplierService, DomesticSupplierService>(); // 义乌采购国内供应商服务
builder.Services.AddScoped<IWarehouseCategoryService, WarehouseCategoryService>(); // 仓库分类服务
builder.Services.AddScoped<IProductService, ProductService>(); // 商品服务
builder.Services.AddScoped<IProductStoreSyncService, ProductStoreSyncService>(); // 商品分店同步服务
builder.Services.AddScoped<IWarehouseProductService, WarehouseProductService>(); // 仓库商品服务
builder.Services.AddScoped<ILocationService, LocationService>(); // 位置服务
builder.Services.AddScoped<IProductLocationService, ProductLocationService>(); // 商品位置服务
builder.Services.AddScoped<ICartService, CartService>(); // 购物车服务
builder.Services.AddScoped<IContainerService, ContainerService>(); // 货柜服务（旧版）
builder.Services.AddScoped<IYiwuContainerService, YiwuContainerService>();
builder.Services.AddScoped<ContainerExportService>(); // 义乌货柜服务
builder.Services.AddScoped<ITranslationService, TranslationService>(); // 翻译服务
builder.Services.AddScoped<IYiwuOrderService, YiwuOrderService>(); // 义乌订单服务
builder.Services.AddScoped<IPostgreSqlService, PostgreSqlService>(); // PostgreSQL数据库服务
builder.Services.AddScoped<IProductPrefixCodeService, ProductPrefixCodeService>(); // 商品前缀管理服务
builder.Services.AddScoped<IDomesticProductService, DomesticProductService>(); // 国内商品管理服务
builder.Services.AddScoped<IDomesticSetProductService, DomesticSetProductService>(); // 套装商品管理服务
builder.Services.AddScoped<ItemBarcodeService>(); // 货号条码生成服务
builder.Services.AddScoped<IDomesticProductCreationService, DomesticProductCreationService>(); // 国内商品货号条码批量创建服务
builder.Services.AddScoped<IDomesticSetTemplateService, DomesticSetTemplateService>(); // 国内套装模板服务
builder.Services.AddScoped<IAutoPricingService, AutoPricingService>(); // 自动定价计算服务
builder.Services.AddScoped<IVersionInfoService, VersionInfoService>(); // 版本管理服务
builder.Services.AddHttpClient<IAppleAppStoreLookupClient, AppleAppStoreLookupClient>(client =>
{
    client.BaseAddress = new Uri("https://itunes.apple.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IIosAppStoreReleaseService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new IosAppStoreReleaseService(
        context.Db,
        sp.GetRequiredService<IAppleAppStoreLookupClient>(),
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<AppUpdatePolicyOptions>
        >(),
        sp.GetRequiredService<ILogger<IosAppStoreReleaseService>>()
    );
});
builder.Services.AddScoped<INativeAppUpdatePolicyService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new NativeAppUpdatePolicyService(
        context.Db,
        sp.GetRequiredService<ILogger<NativeAppUpdatePolicyService>>()
    );
});
builder.Services.AddScoped<IMobileAndroidNativeUpdatePolicyService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    // 公开安卓包筛选复用 MobileAppBuildService，与匿名 android-latest 保持同一口径。
    return new MobileAndroidNativeUpdatePolicyService(
        context.Db,
        sp.GetRequiredService<IMobileAppBuildService>(),
        sp.GetRequiredService<ILogger<MobileAndroidNativeUpdatePolicyService>>(),
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<MobileAppBuildOptions>
        >()
    );
});
builder.Services.AddScoped<IAppOtaReleaseService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new AppOtaReleaseService(
        context.Db,
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<EasWebhookOptions>
        >(),
        sp.GetRequiredService<ILogger<AppOtaReleaseService>>()
    );
});
builder.Services.AddScoped<IMobileOtaPolicyService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new MobileOtaPolicyService(
        context.Db,
        sp.GetRequiredService<ILogger<MobileOtaPolicyService>>()
    );
});
builder.Services.AddScoped<IPosIpadOtaPolicyService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new PosIpadOtaPolicyService(
        context.Db,
        sp.GetRequiredService<ILogger<PosIpadOtaPolicyService>>()
    );
});
builder.Services.AddScoped<IPosHandheldUpdatePolicyService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new PosHandheldUpdatePolicyService(
        context.Db,
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<PosHandheldUpdatePolicyOptions>
        >(),
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<EasWebhookOptions>
        >(),
        sp.GetRequiredService<ILogger<PosHandheldUpdatePolicyService>>()
    );
});
builder.Services.AddScoped<IPosHandheldOtaLegacyBackfillService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    return new PosHandheldOtaLegacyBackfillService(
        context.Db,
        sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<PosHandheldUpdatePolicyOptions>
        >(),
        sp.GetRequiredService<ILogger<PosHandheldOtaLegacyBackfillService>>()
    );
});
builder.Services.AddScoped<MobileAppBuildService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EasWebhookOptions>>();
    var logger = sp.GetRequiredService<ILogger<MobileAppBuildService>>();
    return new MobileAppBuildService(context.Db, options, logger);
}); // Expo EAS APK 构建记录服务
builder.Services.AddScoped<IMobileAppBuildService>(sp =>
    sp.GetRequiredService<MobileAppBuildService>()
);
builder.Services.AddScoped<IPosHandheldUpdateDecisionService, PosHandheldUpdateDecisionService>();
builder.Services.AddScoped<IMobileAppBuildMirrorQueue>(sp =>
    sp.GetRequiredService<MobileAppBuildService>()
);
builder.Services.AddHostedService<MobileAppBuildMirrorBackgroundService>();
builder.Services.AddScoped<WpfAppReleaseService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    var posmContext = sp.GetRequiredService<POSMSqlSugarContext>();
    var uploadService = sp.GetRequiredService<TencentCloudUploadService>();
    var logger = sp.GetRequiredService<ILogger<WpfAppReleaseService>>();
    return new WpfAppReleaseService(context.Db, uploadService, logger, posmContext.Db);
}); // WPF 客户端安装包发布与更新策略服务
builder.Services.AddScoped<IWpfAppReleaseService>(sp =>
    sp.GetRequiredService<WpfAppReleaseService>()
);
builder.Services.AddScoped<IWpfAppReleaseTargetingService, WpfAppReleaseTargetingService>();
builder.Services.AddScoped<INavigationService, NavigationService>(); // 动态导航菜单服务

// React 专用：仅限 Product 与 WarehouseProduct 的商品检测/更新/新建服务
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IProductWarehouseReactService,
    BlazorApp.Api.Services.React.ProductWarehouseReactService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IWarehouseProductChangeHistoryService,
    BlazorApp.Api.Services.React.WarehouseProductChangeHistoryService
>();
// 分店价格更新通知：汇总收集器按请求隔离；任务服务挂在上面的审计收口上，覆盖所有仓库改价入口。
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IPriceNotificationSummaryAccessor,
    BlazorApp.Api.Services.React.PriceNotificationSummaryAccessor
>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IStorePriceUpdateTaskService,
    BlazorApp.Api.Services.React.StorePriceUpdateTaskService
>();
builder.Services.AddHostedService<BlazorApp.Api.Services.Background.StorePriceUpdateTaskReconcileWorker>();
// 分店订货端：暂停供货商品的状态查询与“关注恢复订货”。
builder.Services.AddScoped<
    BlazorApp.Api.Features.SupplyNotices.IStoreProductSupplyService,
    BlazorApp.Api.Features.SupplyNotices.StoreProductSupplyService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Features.SupplyNotices.IWarehouseProductSupplyNoticeService,
    BlazorApp.Api.Features.SupplyNotices.WarehouseProductSupplyNoticeService
>();
// 仓库 PDA 订单拣货：拣货记录、一起拣、扫员工码确认拣货人（凭证用应用 DataProtection 密钥环签名）。
builder.Services.AddSingleton<BlazorApp.Api.Features.WarehousePicking.WarehousePickerTicketProtector>();
builder.Services.AddScoped<
    BlazorApp.Api.Features.WarehousePicking.IWarehousePickerService,
    BlazorApp.Api.Features.WarehousePicking.WarehousePickerService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Features.WarehousePicking.IWarehousePickingService,
    BlazorApp.Api.Features.WarehousePicking.WarehousePickingService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Features.WarehousePicking.IWarehousePickingAssignmentService,
    BlazorApp.Api.Features.WarehousePicking.WarehousePickingAssignmentService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IWarehouseRetailPriceChangeService,
    BlazorApp.Api.Services.React.WarehouseRetailPriceChangeService
>();
builder.Services.AddSingleton<IWarehouseProductHqSyncJobService, WarehouseProductHqSyncJobService>();
builder.Services.AddSingleton<
    IWarehouseProductBatchUpdateJobService,
    WarehouseProductBatchUpdateJobService
>();
builder.Services.AddScoped<IDeviceRegistrationService, DeviceRegistrationService>(); // POSM设备注册管理服务
builder.Services.AddScoped<MobileAppDeviceStatusService>(sp =>
{
    var context = sp.GetRequiredService<SqlSugarContext>();
    var deviceRegistrationService = sp.GetRequiredService<IDeviceRegistrationService>();
    var logger = sp.GetRequiredService<ILogger<MobileAppDeviceStatusService>>();
    return new MobileAppDeviceStatusService(context.Db, deviceRegistrationService, logger);
}); // Expo App 设备版本与在线快照服务
builder.Services.AddScoped<UserLoginDeviceAuditService>(); // App 登录设备与定位审计
builder.Services.AddScoped<IProductSyncService, ProductSyncService>(); // 货柜商品同步服务（检测、批量创建、批量更新）
builder.Services.AddScoped<IProductIntegrityService, ProductIntegrityService>(); // 商品数据一致性校验与修复服务
builder.Services.AddScoped<IWarehouseProductBatchService, WarehouseProductBatchService>(); // 仓库商品批量管理服务
builder.Services.AddScoped<TencentCloudUploadService>();

// 🚧 已禁用的服务（如需启用请取消注释）
// builder.Services.AddScoped<IHqBranchService, HqBranchService>(); // 总部分支服务

// 💡 服务生命周期说明：
// - Singleton: 应用启动时创建，应用关闭时销毁（适合无状态、线程安全的服务）
// - Scoped: 每个请求创建，请求结束时销毁（适合有状态的业务服务）
// - Transient: 每次注入时创建新实例（适合轻量级、无状态的工具类）

// ===================== React 专用服务注册（与原有服务解耦） =====================
builder.Services.AddScoped<IContainerReactService, ContainerReactService>();
builder.Services.AddScoped<IContainerNewProductsReactService, ContainerNewProductsReactService>();
builder.Services.AddScoped<IContainerDetailCollaborationService, ContainerDetailCollaborationService>();
builder.Services.AddScoped<IContainerAllocationSalesReportService, ContainerAllocationSalesReportService>();
builder.Services.AddScoped<IWarehouseProductRecordQueryService, WarehouseProductRecordQueryService>();
builder.Services.AddScoped<
    IContainerProductCreationExecutorService,
    ContainerProductCreationExecutorService
>();
builder.Services.AddSingleton<IContainerProductCreationJobService, ContainerProductCreationJobService>();
builder.Services.Configure<ContainerHqSyncOptions>(
    builder.Configuration.GetSection("ContainerHqSync")
);
builder.Services.AddScoped<ContainerHqSyncService>();
builder.Services.AddScoped<IContainerHqSyncService, ContainerHqSyncService>();
builder.Services.AddScoped<IDomesticProductReactService, DomesticProductReactService>();
builder.Services.AddScoped<IProductPrefixCodeReactService, ProductPrefixCodeReactService>();
builder.Services.AddScoped<IProductGradeReactService, ProductGradeReactService>();
builder.Services.AddScoped<IDomesticSupplierReactService, DomesticSupplierReactService>();
builder.Services.AddScoped<ILocalSuppliersReactService, LocalSupplierReactService>();
builder.Services.AddScoped<IWarehouseCategoryReactService, WarehouseCategoryReactService>();
builder.Services.AddScoped<IProductCategoryReactService, ProductCategoryReactService>();
builder.Services.AddScoped<IProductReactService, ProductReactService>(); // Product CRUD和批量操作服务
builder.Services.AddSingleton<
    IProductSupplierImageBatchUpdateJobService,
    ProductSupplierImageBatchUpdateJobService
>();
builder.Services.AddSingleton<IProductStoreSyncJobService, ProductStoreSyncJobService>();
builder.Services.AddSingleton<IProductPushToHqJobService, ProductPushToHqJobService>();
builder.Services.AddSingleton<IStorePriceTransferJobService, StorePriceTransferJobService>();
builder.Services.AddScoped<IProductHqSyncService, ProductHqSyncService>(); // 商品HQ解耦同步服务
builder.Services.Configure<ProductHqSyncOutboxOptions>(
    builder.Configuration.GetSection(ProductHqSyncOutboxOptions.SectionName)
);
builder.Services.AddScoped<IProductHqSyncOutboxQueue, ProductHqSyncOutboxQueue>();
builder.Services.AddScoped<IProductMaintenanceHqProjectionWriter, ProductMaintenanceHqProjectionWriter>();
builder.Services.AddScoped<IProductHqSyncOutboxExecutor, ProductMaintenanceHqSyncOutboxExecutor>();
builder.Services.AddHostedService<ProductHqSyncOutboxWorker>();
builder.Services.AddScoped<IWarehouseStorePriceSyncService, WarehouseStorePriceSyncService>();
builder.Services.AddSingleton<IWarehouseStorePriceSyncJobService, WarehouseStorePriceSyncJobService>();
builder.Services.AddScoped<IProductSetCodeReactService, ProductSetCodeReactService>();
builder.Services.Configure<StoreRetailPriceHqSyncOptions>(
    builder.Configuration.GetSection("StoreRetailPriceHqSync")
);
builder.Services.AddScoped<IStoreRetailPriceHqSyncService, StoreRetailPriceHqSyncService>();
builder.Services.AddScoped<IStoreRetailPriceReactService, StoreRetailPriceReactService>();
builder.Services.AddScoped<IStoreProductPriceReactService, StoreProductPriceReactService>();
builder.Services.AddScoped<IStoreMultiCodePricesReactService, StoreMultiCodePricesReactService>();
builder.Services.AddScoped<IStorePriceTransferService, StorePriceTransferService>();
builder.Services.AddScoped<ICashRegisterUserReactService, CashRegisterUserReactService>();
builder.Services.AddScoped<IStoreUserReactService, StoreUserReactService>();
builder.Services.AddScoped<IDataSyncFullService, DataSyncFullService>();
builder.Services.AddScoped<IDataSyncIncrementalService, DataSyncIncrementalService>();
builder.Services.AddScoped<IDataSyncReactService, DataSyncReactService>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IHqContainerReactService,
    BlazorApp.Api.Services.React.HqContainerReactService
>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.React.IHqProductTranslationReactService,
    BlazorApp.Api.Services.React.HqProductTranslationReactService
>();
builder.Services.AddScoped<ILocalSupplierInvoiceHqSyncService, LocalSupplierInvoiceHqSyncService>();
builder.Services.AddScoped<
    ILocalSupplierInvoiceHqProductSyncService,
    LocalSupplierInvoiceHqProductSyncService
>();
builder.Services.AddScoped<ILocalSupplierInvoiceOcrService, NoopLocalSupplierInvoiceOcrService>();
builder.Services.AddScoped<ILocalSupplierInvoiceImportService, LocalSupplierInvoiceImportService>();
builder.Services.AddSingleton<
    ILocalSupplierInvoiceBatchUpdateJobService,
    LocalSupplierInvoiceBatchUpdateJobService
>();
builder.Services.AddScoped<ILocalSupplierInvoicesReactService, LocalSupplierInvoicesReactService>();
builder.Services.AddScoped<IPricingStrategyReactService, PricingStrategyReactService>();
builder.Services.AddScoped<IPromotionReactService, PromotionReactService>();
builder.Services.AddScoped<IAdvertisementReactService, AdvertisementReactService>();
builder.Services.AddSingleton<IStoreOrderSyncJobService, StoreOrderSyncJobService>();
builder.Services.AddSingleton<IStoreOrderInvoiceEmailJobService, StoreOrderInvoiceEmailJobService>();
builder.Services.AddSingleton<IStoreOrderPasteReplaceJobService, StoreOrderPasteReplaceJobService>();
builder.Services.AddScoped<IStoreOrderInvoiceAttachmentService, StoreOrderInvoiceAttachmentService>();
builder.Services.AddScoped<
    IStoreOrderInvoiceEmailTextTranslationService,
    StoreOrderInvoiceEmailTextTranslationService
>();
builder.Services.AddScoped<IStoreOrderHqSyncService, StoreOrderHqSyncService>();
builder.Services.AddScoped<
    IStoreOrderLocationProductLookupService,
    StoreOrderLocationProductLookupService
>();
builder.Services.AddStoreOrderFeatures();
// 商品进销查询作为独立只读模块注册，复用门店权限与日销售数据。
builder.Services.AddScoped<BlazorApp.Api.Features.ProductInsights.StoreProductInsightQueryService>();
builder.Services.AddScoped<BlazorApp.Api.Features.ProductInsights.SeasonalProductInsightQueryService>();
builder.Services.AddScoped<BlazorApp.Api.Features.ProductInsights.IProductBranchSalesService,
    BlazorApp.Api.Features.ProductInsights.ProductBranchSalesService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddStoreOrderReactFacade();
builder.Services.AddScoped<IBrowserExtensionAccessService, BrowserExtensionAccessService>();
builder.Services.AddScoped<IBrowserExtensionService, BrowserExtensionService>();
builder.Services.AddScoped<ILocalSupplierCategoryCaptureService, LocalSupplierCategoryCaptureService>();
builder.Services.AddScoped<ILocalSupplierCategoryReactService, LocalSupplierCategoryReactService>();
builder.Services.AddScoped<PreorderReactService>();
builder.Services.AddScoped<IPreorderReactService>(provider =>
    provider.GetRequiredService<PreorderReactService>()
);
builder.Services.AddScoped<IPreorderGateService>(provider =>
    provider.GetRequiredService<PreorderReactService>()
);
builder.Services.AddScoped<IStoreProductMaintenanceReactService, StoreProductMaintenanceReactService>();
// 移动端离线商品目录：索引缓存为进程级单例（每店保留多版本供 delta），服务按请求作用域。
builder.Services.AddSingleton<
    BlazorApp.Api.Services.React.OfflineCatalog.IOfflineCatalogIndexCache,
    BlazorApp.Api.Services.React.OfflineCatalog.OfflineCatalogIndexCache
>();
builder.Services.AddScoped<BlazorApp.Api.Services.React.OfflineCatalog.StoreProductOfflineCatalogService>();
builder.Services.AddScoped<IStoreProductOfflineCatalogService>(sp =>
    sp.GetRequiredService<BlazorApp.Api.Services.React.OfflineCatalog.StoreProductOfflineCatalogService>()
);
// 索引构建在独立 scope 中解析，避免后台构建捕获已释放的 HTTP 请求 DbContext。
builder.Services.AddScoped<IOfflineCatalogIndexBuilder>(sp =>
    sp.GetRequiredService<BlazorApp.Api.Services.React.OfflineCatalog.StoreProductOfflineCatalogService>()
);
builder.Services.AddScoped<StoreAccessContextResolver>();
// 促销海报：扫码查询页生成特价 / 多件价 / 新品 / 清仓海报 PDF（经典、现代两种风格）
builder.Services.AddScoped<BlazorApp.Api.Features.PromoPosters.IPromoPosterService, BlazorApp.Api.Features.PromoPosters.PromoPosterService>();
builder.Services.AddScoped<IAustralianPublicHolidayProvider, AustralianPublicHolidayProvider>();
builder.Services.AddScoped<IAttendancePublicHolidaySyncService, AttendancePublicHolidaySyncService>();
builder.Services.AddScoped<IAttendanceReactService, AttendanceReactService>();
builder.Services.AddScoped<ISeasonalCardRemainingReactService, SeasonalCardRemainingReactService>();
builder.Services.AddScoped<ISeasonalCardStatsReactService, SeasonalCardStatsReactService>();
builder.Services.AddScoped<IPDACartToOrderService, PDACartToOrderService>();
builder.Services.AddScoped<IPDAWarehouseOrderService, PDAWarehouseOrderService>();
builder.Services.AddScoped<IPosmSalesOrderReactService, PosmSalesOrderReactService>();
builder.Services.AddScoped<IInstallmentOrderReactService, InstallmentOrderReactService>();
builder.Services.AddScoped<IStoreVoucherReactService, StoreVoucherReactService>();
builder.Services.AddScoped<IDeviceRegistrationReactService, DeviceRegistrationReactService>();
builder.Services.AddScoped<DeviceActivationCodeManagementService>();
builder.Services.AddScoped<IAttendancePosDeviceStatusProvider, AttendancePosDeviceStatusProvider>();
builder.Services.AddScoped<ITaxInvoiceService, TaxInvoiceService>();
builder.Services.AddScoped<ISalesDashboardReactService, SalesDashboardReactService>();
builder.Services.AddScoped<IMonthlyStoreDailySalesReactService, MonthlyStoreDailySalesReactService>();
// 分店现金管理（存银行 / 现金支出 / 现金池）。现金池流入只认日结实点现金：取数口读 POSM_DailyClose
// （收银端日结上传，表由 Hbpos.Api 建立、后台只读）；若需临时停用，换回 NotConnectedCashDailyCloseSource 即可。
builder.Services.AddScoped<BlazorApp.Api.Services.StoreCash.ICashAccessResolver, BlazorApp.Api.Services.StoreCash.CashAccessResolver>();
builder.Services.AddScoped<BlazorApp.Api.Services.StoreCash.ICashDailyCloseSource>(sp =>
    new BlazorApp.Api.Services.StoreCash.PosmCashDailyCloseSource(sp.GetRequiredService<POSMSqlSugarContext>().Db));
builder.Services.AddScoped<BlazorApp.Api.Services.StoreCash.IStoreCashAttachmentService, BlazorApp.Api.Services.StoreCash.StoreCashAttachmentService>();
builder.Services.AddScoped<BlazorApp.Api.Services.StoreCash.IStoreCashService, BlazorApp.Api.Services.StoreCash.StoreCashService>();
builder.Services.AddScoped<ISalesDashboardCacheWarmer, SalesDashboardCacheWarmer>();
builder.Services.AddScoped<IProductMovementReportService, ProductMovementReportService>();
builder.Services.AddScoped<IBatchProductSalesAnalysisService, BatchProductSalesAnalysisService>();
builder.Services.AddHostedService<BatchProductSalesDiscountWorker>();
builder.Services.AddHostedService<ProductMovementReportSnapshotWorker>();
// 移动端中国供应商页签默认视图预热：每个实例各自维护内存缓存，无需租约。
builder.Services.AddHostedService<MobileChinaReportCacheWarmupWorker>();
builder.Services.AddHostedService<ProductStoreDailyColumnstoreMaintenanceWorker>();
builder.Services.AddHostedService<SalesDetailMonthlyProjectionWorker>();
builder.Services.AddHostedService<CompactBoardMonthlyProjectionWorker>();
builder.Services.AddScoped<
    IWarehouseProductFlowAnalysisService,
    WarehouseProductFlowAnalysisService
>();
builder.Services.AddScoped<
    ILocalSupplierProductSalesAnalysisService,
    LocalSupplierProductSalesAnalysisService
>();
builder.Services.AddScoped<
    ILocalSupplierInvoiceSalesAnalysisService,
    LocalSupplierInvoiceSalesAnalysisService
>();
builder.Services.AddScoped<ILocalPurchaseDashboardService, LocalPurchaseDashboardService>();
builder.Services.AddScoped<IHolidayProductReactService, HolidayProductReactService>();
builder.Services.AddScoped<IStoreManagerProductReactService, StoreManagerProductReactService>();
builder.Services.AddScoped<ILocationReactService, LocationReactService>();
builder.Services.AddScoped<
    BlazorApp.Api.Interfaces.IStoreOrderCacheWarmer,
    BlazorApp.Api.Cache.StoreOrderCacheWarmer
>();

builder.Services.AddScoped<SalesStatisticsApplicationCoordinator>();
builder.Services.AddScoped<ISalesStatisticsRefreshOperations>(serviceProvider =>
    serviceProvider.GetRequiredService<SalesStatisticsApplicationCoordinator>()
);
builder.Services.AddScoped<ISalesStatisticsRecalculationExecutor>(serviceProvider =>
    serviceProvider.GetRequiredService<SalesStatisticsApplicationCoordinator>()
);
builder.Services.AddScoped<SalesStatisticsJobService>(serviceProvider =>
    new SalesStatisticsJobService(
        serviceProvider.GetRequiredService<SalesStatisticsApplicationCoordinator>(),
        serviceProvider.GetRequiredService<IServiceScopeFactory>()
    )
);
builder.Services.AddScoped<SalesStatisticsAlignmentBackgroundRecalculateService>();

builder.Services.AddSingleton<
    BlazorApp.Api.Interfaces.React.IOrderNumberGenerator,
    BlazorApp.Api.Services.Common.OrderNumberGeneratorService
>();

// ===================== 应用构建与中间件配置 =====================

// 🏗️ 构建WebApplication实例
// 此时所有服务配置完成，开始构建实际的Web应用程序
var app = builder.Build();

var startupSchemaResult = await ExecuteSchemaOperationAsync(
    app.Services,
    SchemaCommandMode.Check
);
if (!startupSchemaResult.Success)
{
    app.Logger.LogError(
        "数据库 schema 操作失败。模式={Mode}，诊断码={DiagnosticCode}，退出码={ExitCode}",
        schemaCommand.Mode,
        startupSchemaResult.DiagnosticCode,
        startupSchemaResult.ExitCode
    );
    Environment.ExitCode = startupSchemaResult.ExitCode;
    // 与显式 schema 分支相同：host 尚未 Run，不会自动释放，须手动释放以排空控制台日志队列，
    // 否则容器启动失败时 docker logs 可能看不到诊断码。
    await app.DisposeAsync();
    return;
}

app.Logger.LogInformation(
    "数据库 schema 操作完成。模式={Mode}，诊断码={DiagnosticCode}",
    schemaCommand.Mode,
    startupSchemaResult.DiagnosticCode
);

// 📋 在应用构建完成后记录实际启用的 CORS 域名，避免在服务注册阶段提前创建容器。
app.Logger.LogInformation("CORS 允许的域名: {Origins}", string.Join(", ", corsOrigins));

// 📝 初始化缓存键日志记录器
using (var scope = app.Services.CreateScope())
{
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var cacheLogger = loggerFactory.CreateLogger("BlazorApp.Api.Cache.SalesDashboardCacheKeys");
    BlazorApp.Api.Cache.SalesDashboardCacheKeys.SetLogger(cacheLogger);

    var storeOrderCacheLogger = loggerFactory.CreateLogger(
        "BlazorApp.Api.Cache.StoreOrderCacheKeys"
    );
    BlazorApp.Api.Cache.StoreOrderCacheKeys.SetLogger(storeOrderCacheLogger);
}

// --------------------- HTTP请求管道配置 ---------------------
// ⚡ 中间件管道按顺序执行，顺序很重要！
// 请求：客户端 → 中间件1 → 中间件2 → ... → 控制器
// 响应：控制器 → ... → 中间件2 → 中间件1 → 客户端

// 📚 开发环境专用：Swagger API文档中间件
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(); // 启用Swagger JSON端点
    app.UseSwaggerUI(); // 启用Swagger UI界面（通常在 /swagger 路径）
    // 🔗 访问地址：http://localhost:5002/swagger
}

// 🔒 HTTPS重定向中间件（当前已禁用）
// 生产环境建议启用HTTPS重定向以提高安全性
// app.UseHttpsRedirection();

// 🧾 全局请求管道异常日志，覆盖未进入 MVC Filter 的异常。
app.UseMiddleware<ApplicationExceptionLoggingMiddleware>();

// 🌐 CORS中间件：处理跨域请求
// 必须在认证/授权中间件之前，允许预检请求通过
app.UseCors("AllowSpecific");

// 路由已匹配后关闭显式排除或附件下载端点的内建请求指标。
app.UseMiddleware<PerformanceMetricsEndpointExclusionMiddleware>();

// 🔐 认证中间件：验证JWT令牌，设置HttpContext.User
// 必须在授权中间件之前执行
app.UseAuthentication();

// Mobile 会话交换、匿名开通及浏览器扩展授权的独立限流必须在端点映射前启用。
app.UseRateLimiter();

// 浏览器扩展短期 JWT 只允许访问订货助手 API。
app.UseMiddleware<BrowserExtensionTokenScopeMiddleware>();

// 🛡️ 授权中间件：基于用户身份和角色检查访问权限
// 处理[Authorize]特性标记的控制器和方法
app.UseAuthorization();

// 映射控制器路由（启用API端点）
app.MapControllers();

// 角色、权限和用户角色只允许通过明确的一次性维护流程初始化；常规启动保持禁用。
// Console.WriteLine("🌱 开始初始化种子数据...");
// var seedDataService = services.GetRequiredService<SeedDataService>();
// await seedDataService.InitializePermissionSeedsAsync();
// await seedDataService.InitializeAsync();
// Console.WriteLine("🔍 检查角色数据...");
// var dataInitService = services.GetRequiredService<IDataInitializationService>();
// await dataInitService.CheckAndInitializeDataAsync();
// 角色、权限及用户角色自动初始化已禁用，保留数据库现有配置。

// ===================== 缓存预热 =====================
var enableStoreOrderWarmUp = builder.Configuration.GetValue<bool>(
    "Cache:EnableStoreOrderWarmUp",
    false
);
if (enableStoreOrderWarmUp)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var cacheWarmUpDelaySeconds = builder.Configuration.GetValue<int>(
                "Cache:StoreOrderWarmUpDelaySeconds",
                30
            );
            await Task.Delay(TimeSpan.FromSeconds(cacheWarmUpDelaySeconds));
            using (var scope = app.Services.CreateScope())
            {
                var cacheWarmer =
                    scope.ServiceProvider.GetRequiredService<BlazorApp.Api.Interfaces.IStoreOrderCacheWarmer>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("开始预热首页商品列表缓存");
                await cacheWarmer.WarmUpHomePageAsync();
                logger.LogInformation("首页商品列表缓存预热完成");
            }
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "预热首页商品列表缓存失败，但不影响应用启动");
        }
    });
}

/*

// 🚀 应用启动前的数据库优化
// 创建性能优化索引和统计视图
try
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
        await BlazorApp.Api.Data.WarehouseProductIndexCreator.CreateAllIndexesAsync(context.Db);
        await BlazorApp.Api.Data.WarehouseProductIndexCreator.CreateStatisticsViewsAsync(context.Db);
        await BlazorApp.Api.Data.WarehouseProductIndexCreator.AnalyzeDatabasePerformanceAsync(context.Db);
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogWarning(ex, "数据库索引创建过程中出现警告，但不影响应用启动");
}
 */
// 启动Web应用
app.Run();

static async Task<SchemaOperationResult> ExecuteSchemaOperationAsync(
    IServiceProvider services,
    SchemaCommandMode mode
)
{
    using var schemaCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        schemaCancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        using var scope = services.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<SchemaMigrationCoordinator>();
        var result = mode == SchemaCommandMode.Migrate
            ? await coordinator.MigrateAsync(schemaCancellation.Token)
            : await coordinator.CheckAsync(schemaCancellation.Token);

        return schemaCancellation.IsCancellationRequested && result.Success
            ? SchemaOperationResult.Failure(
                SchemaExitCodes.Cancelled,
                SchemaDiagnosticCodes.Cancelled
            )
            : result;
    }
    catch (OperationCanceledException)
    {
        return SchemaOperationResult.Failure(
            SchemaExitCodes.Cancelled,
            SchemaDiagnosticCodes.Cancelled
        );
    }
    catch (Exception)
    {
        return SchemaOperationResult.Failure(
            SchemaExitCodes.DatabaseFailure,
            SchemaDiagnosticCodes.DatabaseFailure
        );
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<SchemaOperationResult> ExecuteSalesDetailProjectionOperationAsync(
    IServiceProvider services,
    bool checkOnly)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<SalesDetailQueryProjectionMaintenanceRunner>()
            .RunAsync(checkOnly, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        return SchemaOperationResult.Failure(
            SchemaExitCodes.Cancelled,
            SchemaDiagnosticCodes.Cancelled);
    }
    catch (Exception)
    {
        return SchemaOperationResult.Failure(
            SchemaExitCodes.DatabaseFailure,
            SchemaDiagnosticCodes.DatabaseFailure);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<SchemaOperationResult> ExecuteRemoteMaintenanceSchemaOperationAsync(
    IServiceProvider services,
    bool checkOnly
)
{
    using var schemaCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        schemaCancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        using var scope = services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<RemoteMaintenanceSchemaMigrator>();
        return checkOnly
            ? await migrator.CheckAsync(schemaCancellation.Token)
            : await migrator.MigrateAsync(schemaCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        return SchemaOperationResult.Failure(SchemaExitCodes.Cancelled, SchemaDiagnosticCodes.Cancelled);
    }
    catch (Exception)
    {
        return SchemaOperationResult.Failure(SchemaExitCodes.DatabaseFailure, SchemaDiagnosticCodes.DatabaseFailure);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<SchemaOperationResult> ExecuteRustDeskClientSchemaOperationAsync(
    IServiceProvider services,
    bool checkOnly
)
{
    using var schemaCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        schemaCancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        using var scope = services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<RustDeskClientSchemaMigrator>();
        return checkOnly
            ? await migrator.CheckAsync(schemaCancellation.Token)
            : await migrator.MigrateAsync(schemaCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        return SchemaOperationResult.Failure(SchemaExitCodes.Cancelled, SchemaDiagnosticCodes.Cancelled);
    }
    catch (Exception)
    {
        return SchemaOperationResult.Failure(SchemaExitCodes.DatabaseFailure, SchemaDiagnosticCodes.DatabaseFailure);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static void ConfigureServiceApiTokenAuthentication(AuthenticationSchemeOptions options)
{
    // service token handler 不需要额外 options，保留显式方法避免链式注册里的 lambda 解析歧义。
}

static void MapTencentCloudEnvironmentVariables(ConfigurationManager configuration)
{
    var envMappings = new Dictionary<string, string>
    {
        ["TENCENT_SECRET_ID"] = "TencentCloud:SecretId",
        ["TENCENT_SECRET_KEY"] = "TencentCloud:SecretKey",
        ["TENCENT_BUCKET_NAME"] = "TencentCloud:BucketName",
        ["TENCENT_REGION"] = "TencentCloud:Region",
        ["TENCENT_IMAGE_BUCKET_NAME"] = "TencentCloud:ImageBucketName",
        ["TENCENT_IMAGE_REGION"] = "TencentCloud:ImageRegion",
    };

    foreach (var (envKey, configKey) in envMappings)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(value))
        {
            configuration[configKey] = value;
        }
    }
}
