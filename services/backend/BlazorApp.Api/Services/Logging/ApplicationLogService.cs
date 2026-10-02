using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Logging
{
    public class ApplicationLogService
    {
        private static readonly HashSet<string> AllowedSourceTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Backend",
            "Web",
            "Mobile",
            "POS",
        };
        private static readonly Regex BearerTokenPattern = new(
            @"\bBearer\s+[^\s,;]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex SensitiveAssignmentPattern = new(
            @"\b(?<key>[A-Za-z][A-Za-z0-9_.-]{0,127})\b\s*[:=]\s*[^\s,;]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex SensitiveQuotedAssignmentPattern = new(
            @"(?<prefix>(?<keyQuote>[""'])(?<key>[^""'\r\n]{1,128})\k<keyQuote>\s*[:=]\s*[""'])(?:Bearer\s+)?[^""']*(?<suffix>[""'])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex SensitiveQuotedUnquotedAssignmentPattern = new(
            @"(?<keyQuote>[""'])(?<key>[^""'\r\n]{1,128})\k<keyQuote>(?<separator>\s*[:=]\s*)(?![""'])[^,}\]\s;]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex JsonStructuredPropertyPattern = new(
            @"([""'])([^""'\r\n]{1,128})\1(\s*[:=]\s*)([\[{])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex PanPattern = new(
            @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex SafeIdentifierPattern = new(
            @"^[A-Za-z0-9][A-Za-z0-9._:/+\-]*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly Regex UrlQueryPattern = new(
            @"(?<url>(?:https?://|/)[^\s?]+)\?[^\s]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );
        private static readonly string[] SensitiveKeyFragments =
        [
            "authorization",
            "bearer",
            "password",
            "pin",
            "apikey",
            "token",
            "secret",
            "credential",
            "cvv",
            "pan",
            "card",
            "cardnumber",
            "voucher",
            "vouchercode",
            "cookie",
            "header",
            "employeebarcode",
            "customeremail",
            "customerphone",
            "customeraddress",
            "customername",
            "requestbody",
            "responsebody",
            "rawrequest",
            "rawresponse",
        ];
        private const string RedactedValue = "[REDACTED]";
        private const string RedactedPropertyKey = "[REDACTED_KEY]";
        private const int MaxJsonSanitizeDepth = 16;
        private const int MaxJsonFragmentDepth = 32;

        private readonly ISqlSugarClient _db;
        private readonly ApplicationLoggingOptions _options;
        private readonly ILogger<ApplicationLogService> _logger;
        private readonly IApplicationLogQueue? _queue;

        public ApplicationLogService(
            ISqlSugarClient db,
            IOptions<ApplicationLoggingOptions> options,
            ILogger<ApplicationLogService> logger,
            IApplicationLogQueue? queue = null
        )
        {
            _db = db;
            _options = options.Value;
            _logger = logger;
            _queue = queue;
        }

        public Task<ApplicationLoggingProjectOptions?> AuthenticateProjectAsync(
            string? projectCode,
            string? apiKey
        )
        {
            if (string.IsNullOrWhiteSpace(projectCode) || string.IsNullOrWhiteSpace(apiKey))
                return Task.FromResult<ApplicationLoggingProjectOptions?>(null);

            var project = FindProject(projectCode);
            if (project == null || !project.Enabled || string.IsNullOrWhiteSpace(project.ApiKeyHash))
                return Task.FromResult<ApplicationLoggingProjectOptions?>(null);

            var incomingHash = ComputeSha256(apiKey);
            var matched = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(incomingHash),
                Encoding.UTF8.GetBytes(project.ApiKeyHash.Trim().ToLowerInvariant())
            );

            return Task.FromResult(matched ? project : null);
        }

        /// <summary>
        /// 项目在配置中存在且已启用。供不走 Key 鉴权的写入通道（如已登录移动端补传诊断）做总开关。
        /// </summary>
        public bool IsProjectEnabled(string? projectCode)
        {
            return FindProject(projectCode)?.Enabled == true;
        }

        public async Task<ApplicationLogIngestResultDto> IngestAsync(
            string projectCode,
            ApplicationLogIngestRequestDto request,
            string? trustedClientIp = null
        )
        {
            if (request.Logs.Count > _options.MaxBatchSize)
                throw new InvalidOperationException($"单次最多写入 {_options.MaxBatchSize} 条日志");

            var project = FindProject(projectCode);
            var itemResults = request
                .Logs.Select(item => new ApplicationLogIngestItemResultDto
                {
                    ClientEventId = item?.ClientEventId,
                })
                .ToList();
            var legacyLogs = new List<(int Index, ApplicationLog Entity)>();
            var idempotentLogs = new List<(int Index, ApplicationLog Entity)>();

            for (var index = 0; index < request.Logs.Count; index++)
            {
                var item = request.Logs[index];
                if (!IsValidIngestItem(item))
                {
                    itemResults[index].Status = "rejected";
                    itemResults[index].ErrorCode = "INVALID_LOG_ITEM";
                    continue;
                }

                var entity = BuildEntity(project, projectCode, item, trustedClientIp);
                if (entity.ClientEventId.HasValue)
                    idempotentLogs.Add((index, entity));
                else
                    legacyLogs.Add((index, entity));
            }

            // 旧客户端没有幂等键，继续保持一次批量写入，避免改变既有吞吐表现。
            if (legacyLogs.Count > 0)
            {
                await _db.Insertable(legacyLogs.Select(item => item.Entity).ToList()).ExecuteCommandAsync();
                foreach (var item in legacyLogs)
                    itemResults[item.Index].Status = "accepted";
            }

            var batchEventIds = new HashSet<Guid>();
            foreach (var item in idempotentLogs)
            {
                var clientEventId = item.Entity.ClientEventId!.Value;
                if (!batchEventIds.Add(clientEventId) || await ClientEventExistsAsync(item.Entity))
                {
                    itemResults[item.Index].Status = "duplicate";
                    continue;
                }

                try
                {
                    await _db.Insertable(item.Entity).ExecuteCommandAsync();
                    itemResults[item.Index].Status = "accepted";
                }
                catch (Exception ex)
                {
                    // 并发请求可能同时通过预检查，唯一索引是最终幂等边界。
                    if (!await ClientEventExistsAsync(item.Entity))
                        throw;

                    _logger.LogDebug(
                        ex,
                        "中心日志并发重复写入已按幂等处理: {ProjectCode}/{ClientEventId}",
                        item.Entity.ProjectCode,
                        clientEventId
                    );
                    itemResults[item.Index].Status = "duplicate";
                }
            }

            return new ApplicationLogIngestResultDto
            {
                AcceptedCount = itemResults.Count(item => item.Status == "accepted"),
                RejectedCount = itemResults.Count(item => item.Status == "rejected"),
                DuplicateCount = itemResults.Count(item => item.Status == "duplicate"),
                Results = itemResults,
            };
        }

        public async Task<PagedResult<ApplicationLogDto>> QueryAsync(ApplicationLogQueryDto query)
        {
            var pageNumber = Math.Max(1, query.PageNumber);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);
            var dbQuery = ApplyQuery(_db.Queryable<ApplicationLog>(), query);

            dbQuery = (query.SortBy ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "level" => IsAsc(query)
                    ? dbQuery.OrderBy(x => x.Level)
                    : dbQuery.OrderByDescending(x => x.Level),
                "projectcode" => IsAsc(query)
                    ? dbQuery.OrderBy(x => x.ProjectCode)
                    : dbQuery.OrderByDescending(x => x.ProjectCode),
                _ => IsAsc(query)
                    ? dbQuery.OrderBy(x => x.TimestampUtc)
                    : dbQuery.OrderByDescending(x => x.TimestampUtc),
            };

            RefAsync<int> total = 0;
            var items = await dbQuery.ToPageListAsync(pageNumber, pageSize, total);

            return new PagedResult<ApplicationLogDto>
            {
                Items = items.Select(ToDto).ToList(),
                Total = total,
                Page = pageNumber,
                PageSize = pageSize,
            };
        }

        public async Task<ApplicationLogDto?> GetAsync(Guid id)
        {
            var entity = await _db.Queryable<ApplicationLog>().FirstAsync(x => x.Id == id);
            return entity == null ? null : ToDto(entity);
        }

        public async Task<ApplicationLogSummaryDto> GetSummaryAsync(ApplicationLogQueryDto query)
        {
            var runtimeSnapshot = _queue?.GetRuntimeSnapshot() ?? new ApplicationLogQueueRuntimeSnapshot();
            return new ApplicationLogSummaryDto
            {
                Total = await ApplyQuery(_db.Queryable<ApplicationLog>(), query).CountAsync(),
                ByProject = await QueryGroupAsync(query, "ProjectCode"),
                ByLevel = await QueryGroupAsync(query, "Level"),
                ByExceptionType = await QueryGroupAsync(query, "ExceptionType"),
                ByRequestPath = await QueryGroupAsync(query, "RequestPath"),
                Status = await GetStatusAsync(),
                Pipeline = new ApplicationLogPipelineRuntimeDto
                {
                    DroppedOldestCount = runtimeSnapshot.DroppedOldestCount,
                    EnqueueFailureCount = runtimeSnapshot.EnqueueFailureCount,
                    FailedFlushBatchCount = runtimeSnapshot.FailedFlushBatchCount,
                    FailedFlushLogCount = runtimeSnapshot.FailedFlushLogCount,
                    LastFailedFlushBatchSize = runtimeSnapshot.LastFailedFlushBatchSize,
                    LastFailedFlushReason = runtimeSnapshot.LastFailedFlushReason,
                },
            };
        }

        private async Task<ApplicationLogStatusDto> GetStatusAsync()
        {
            // 最后接收时间只看服务端 CreatedAt，不能继承当前汇总页的筛选条件。
            var receivedGroups = await _db
                .Queryable<ApplicationLog>()
                .GroupBy(log => log.ProjectCode)
                .Select(log => new ApplicationLogProjectStatusDto
                {
                    ProjectCode = log.ProjectCode,
                    LastReceivedAtUtc = SqlFunc.AggregateMax(log.CreatedAt),
                })
                .ToListAsync();
            var lastReceivedByProject = receivedGroups
                .Where(item => !string.IsNullOrWhiteSpace(item.ProjectCode))
                .GroupBy(item => item.ProjectCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Where(item => item.LastReceivedAtUtc.HasValue)
                        .Select(item => AsUtc(item.LastReceivedAtUtc!.Value))
                        .Cast<DateTime?>()
                        .Max(),
                    StringComparer.OrdinalIgnoreCase
                );

            var configuredProjects = _options.Projects
                .Where(project => NormalizeProjectCode(project.ProjectCode) != null)
                .GroupBy(
                    project => NormalizeProjectCode(project.ProjectCode)!,
                    StringComparer.OrdinalIgnoreCase
                )
                .Select(group => group.First())
                .ToList();
            var defaultProjectCode = NormalizeProjectCode(_options.DefaultProjectCode) ?? string.Empty;
            var defaultProject = configuredProjects.FirstOrDefault(project =>
                string.Equals(
                    NormalizeProjectCode(project.ProjectCode),
                    defaultProjectCode,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            var projects = new List<ApplicationLogProjectStatusDto>
            {
                BuildProjectStatus(
                    defaultProjectCode,
                    defaultProject,
                    isInternal: true,
                    lastReceivedByProject
                ),
            };
            projects.AddRange(
                configuredProjects
                    .Where(project =>
                        !string.Equals(
                            NormalizeProjectCode(project.ProjectCode),
                            defaultProjectCode,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(project =>
                        BuildProjectStatus(
                            NormalizeProjectCode(project.ProjectCode)!,
                            project,
                            isInternal: false,
                            lastReceivedByProject
                        )
                    )
            );

            return new ApplicationLogStatusDto
            {
                BackendCaptureEnabled = _options.Enabled,
                BackendMinimumLevel = _options.MinimumLevel,
                DefaultProjectCode = _options.DefaultProjectCode,
                DefaultEnvironment = _options.DefaultEnvironment,
                ServiceName = _options.ServiceName,
                Projects = projects,
            };
        }

        private ApplicationLogProjectStatusDto BuildProjectStatus(
            string projectCode,
            ApplicationLoggingProjectOptions? project,
            bool isInternal,
            IReadOnlyDictionary<string, DateTime?> lastReceivedByProject
        )
        {
            var enabled = isInternal
                ? _options.Enabled && !string.IsNullOrWhiteSpace(projectCode)
                : project?.Enabled == true;
            bool? credentialConfigured = isInternal
                ? null
                : IsValidSha256Hash(project?.ApiKeyHash);
            var configurationState = !enabled
                ? "Disabled"
                : isInternal || credentialConfigured == true
                    ? "Ready"
                    : "MissingCredential";
            lastReceivedByProject.TryGetValue(projectCode, out var lastReceivedAtUtc);

            return new ApplicationLogProjectStatusDto
            {
                ProjectCode = projectCode,
                DisplayName = string.IsNullOrWhiteSpace(project?.DisplayName)
                    ? projectCode
                    : project.DisplayName.Trim(),
                Mode = isInternal ? "Internal" : "External",
                ExplicitlyConfigured = project != null,
                Enabled = enabled,
                CredentialConfigured = credentialConfigured,
                ConfigurationState = configurationState,
                EffectiveRetentionDays = project?.RetentionDays ?? _options.DefaultRetentionDays,
                LastReceivedAtUtc = lastReceivedAtUtc,
            };
        }

        private static bool IsValidSha256Hash(string? value)
        {
            var hash = value?.Trim();
            return hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
        }

        public async Task<int> CleanupExpiredLogsAsync(DateTime nowUtc)
        {
            var deleted = 0;
            var projects = _options.Projects.Count > 0
                ? _options.Projects
                : new List<ApplicationLoggingProjectOptions>
                {
                    new()
                    {
                        ProjectCode = _options.DefaultProjectCode,
                        RetentionDays = _options.DefaultRetentionDays,
                        Enabled = true,
                    },
                };

            foreach (var project in projects)
            {
                var projectCode = NormalizeProjectCode(project.ProjectCode);
                if (projectCode == null)
                    // 空白项目码不能形成有效清理条件，必须安全跳过。
                    continue;

                var retentionDays = project.RetentionDays ?? _options.DefaultRetentionDays;
                var cutoff = nowUtc.AddDays(-retentionDays);
                deleted += await _db
                    .Deleteable<ApplicationLog>()
                    .Where(x => x.ProjectCode == projectCode && x.CreatedAt < cutoff)
                    .ExecuteCommandAsync();
            }

            return deleted;
        }

        public ApplicationLogIngestItemDto CreateBackendLogItem(
            string category,
            LogLevel level,
            string message,
            Exception? exception,
            HttpContext? httpContext,
            string? eventId = null,
            IReadOnlyDictionary<string, object?>? properties = null
        )
        {
            return new ApplicationLogIngestItemDto
            {
                ProjectCode = _options.DefaultProjectCode,
                Environment = _options.DefaultEnvironment,
                SourceType = _options.DefaultSourceType,
                ServiceName = _options.ServiceName,
                InstanceId = _options.InstanceId,
                Level = level.ToString(),
                Category = category,
                EventId = eventId,
                Message = message,
                TimestampUtc = DateTime.UtcNow,
                TraceId = httpContext?.TraceIdentifier,
                RequestPath = httpContext?.Request.Path.Value,
                RequestMethod = httpContext?.Request.Method,
                StatusCode = httpContext?.Response.StatusCode,
                UserId =
                    httpContext?.User.FindFirst("userId")?.Value
                    ?? httpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                UserName = httpContext?.User.Identity?.Name,
                ClientIp = httpContext?.Connection.RemoteIpAddress?.ToString(),
                ExceptionType = exception?.GetType().Name,
                ExceptionMessage = exception?.Message,
                StackTrace = exception?.ToString(),
                Properties = properties?.ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private ISugarQueryable<ApplicationLog> ApplyQuery(
            ISugarQueryable<ApplicationLog> dbQuery,
            ApplicationLogQueryDto query
        )
        {
            var projectCodes = NormalizeProjectCodes(query);
            if (projectCodes.Count > 0)
                // 多选项目过滤供中心日志页面使用；ProjectCode 单值保留给旧入口。
                dbQuery = dbQuery.Where(x => projectCodes.Contains(x.ProjectCode));
            else if (!string.IsNullOrWhiteSpace(query.ProjectCode))
                dbQuery = dbQuery.Where(x => x.ProjectCode == query.ProjectCode);
            if (!string.IsNullOrWhiteSpace(query.Environment))
                dbQuery = dbQuery.Where(x => x.Environment == query.Environment);
            if (!string.IsNullOrWhiteSpace(query.SourceType))
                dbQuery = dbQuery.Where(x => x.SourceType == query.SourceType);
            if (!string.IsNullOrWhiteSpace(query.Level))
                dbQuery = dbQuery.Where(x => x.Level == query.Level);
            if (!string.IsNullOrWhiteSpace(query.Category))
                dbQuery = dbQuery.Where(x => x.Category != null && x.Category.Contains(query.Category));
            if (!string.IsNullOrWhiteSpace(query.RequestPath))
                dbQuery = dbQuery.Where(x => x.RequestPath != null && x.RequestPath.Contains(query.RequestPath));
            if (!string.IsNullOrWhiteSpace(query.TraceId))
                dbQuery = dbQuery.Where(x => x.TraceId == query.TraceId);
            if (!string.IsNullOrWhiteSpace(query.UserId))
                dbQuery = dbQuery.Where(x => x.UserId == query.UserId);
            if (!string.IsNullOrWhiteSpace(query.UserName))
                dbQuery = dbQuery.Where(x => x.UserName != null && x.UserName.Contains(query.UserName));
            if (!string.IsNullOrWhiteSpace(query.StoreCode))
                dbQuery = dbQuery.Where(x => x.StoreCode == query.StoreCode);
            if (!string.IsNullOrWhiteSpace(query.DeviceCode))
                dbQuery = dbQuery.Where(x => x.DeviceCode == query.DeviceCode);
            if (!string.IsNullOrWhiteSpace(query.AppVersion))
                dbQuery = dbQuery.Where(x => x.AppVersion == query.AppVersion);
            if (!string.IsNullOrWhiteSpace(query.InstanceId))
                dbQuery = dbQuery.Where(x => x.InstanceId == query.InstanceId);
            if (!string.IsNullOrWhiteSpace(query.EventId))
                dbQuery = dbQuery.Where(x => x.EventId == query.EventId);
            if (query.StartUtc.HasValue)
                dbQuery = dbQuery.Where(x => x.TimestampUtc >= query.StartUtc.Value);
            if (query.EndUtc.HasValue)
                // 时间窗统一按 [StartUtc, EndUtc) 处理，方便前端直接传“下一本地日开始时刻”做整日本地统计。
                dbQuery = dbQuery.Where(x => x.TimestampUtc < query.EndUtc.Value);
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                dbQuery = dbQuery.Where(x =>
                    x.Message.Contains(query.Keyword)
                    || (x.ExceptionMessage != null && x.ExceptionMessage.Contains(query.Keyword))
                    || (x.StackTrace != null && x.StackTrace.Contains(query.Keyword))
                );
            }

            return dbQuery;
        }

        private ApplicationLog BuildEntity(
            ApplicationLoggingProjectOptions? project,
            string authenticatedProjectCode,
            ApplicationLogIngestItemDto item,
            string? trustedClientIp = null
        )
        {
            var projectCode = project?.ProjectCode ?? authenticatedProjectCode;
            return new ApplicationLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = item.TimestampUtc.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(item.TimestampUtc, DateTimeKind.Utc)
                    : item.TimestampUtc.ToUniversalTime(),
                ProjectCode = Truncate(projectCode, 80) ?? _options.DefaultProjectCode,
                ProjectName = Truncate(project?.DisplayName ?? projectCode, 120),
                Environment = SanitizeIdentifier(item.Environment, 60) ?? _options.DefaultEnvironment,
                SourceType = SanitizeIdentifier(item.SourceType, 60) ?? _options.DefaultSourceType,
                ServiceName = SanitizeIdentifier(item.ServiceName, 120),
                InstanceId = SanitizeIdentifier(item.InstanceId, 120),
                ClientEventId = item.ClientEventId,
                StoreCode = SanitizeIdentifier(item.StoreCode, 80),
                DeviceCode = SanitizeIdentifier(item.DeviceCode, 120),
                AppVersion = SanitizeIdentifier(item.AppVersion, 60),
                Level = SanitizeIdentifier(item.Level, 30) ?? LogLevel.Information.ToString(),
                Category = SanitizeIdentifier(item.Category, 240),
                EventId = SanitizeIdentifier(item.EventId, 80),
                Message = Truncate(SanitizeText(item.Message), _options.MaxMessageLength) ?? string.Empty,
                ExceptionType = SanitizeIdentifier(item.ExceptionType, 240),
                ExceptionMessage = Truncate(SanitizeText(item.ExceptionMessage), _options.MaxMessageLength),
                StackTrace = Truncate(SanitizeText(item.StackTrace), _options.MaxStackTraceLength),
                RequestPath = Truncate(SanitizeRequestPath(SanitizeText(item.RequestPath)), 500),
                RequestMethod = SanitizeIdentifier(item.RequestMethod, 20),
                StatusCode = item.StatusCode,
                TraceId = SanitizeIdentifier(item.TraceId, 120),
                UserId = SanitizeIdentifier(item.UserId, 120),
                UserName = Truncate(SanitizeText(item.UserName), 120),
                ClientIp = SanitizeIdentifier(trustedClientIp ?? item.ClientIp, 80),
                PropertiesJson = Truncate(SerializeSafeProperties(item.Properties), _options.MaxPropertiesLength),
                CreatedAt = DateTime.UtcNow,
            };
        }

        private async Task<bool> ClientEventExistsAsync(ApplicationLog entity)
        {
            return await _db
                .Queryable<ApplicationLog>()
                .AnyAsync(x =>
                    x.ProjectCode == entity.ProjectCode && x.ClientEventId == entity.ClientEventId
                );
        }

        private static bool IsValidIngestItem(ApplicationLogIngestItemDto? item)
        {
            return item != null
                && !string.IsNullOrWhiteSpace(item.Level)
                && !string.IsNullOrWhiteSpace(item.Message)
                && !string.IsNullOrWhiteSpace(item.Environment)
                && !string.IsNullOrWhiteSpace(item.SourceType)
                && AllowedSourceTypes.Contains(item.SourceType.Trim());
        }

        private ApplicationLoggingProjectOptions? FindProject(string? projectCode)
        {
            var normalizedProjectCode = NormalizeProjectCode(projectCode);
            if (normalizedProjectCode == null)
                return null;

            var project = _options.Projects.FirstOrDefault(item =>
                string.Equals(
                    NormalizeProjectCode(item.ProjectCode),
                    normalizedProjectCode,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (project == null)
                return null;

            // 统一去除配置项目码首尾空白，确保鉴权、写入和状态接口返回同一项目码。
            return new ApplicationLoggingProjectOptions
            {
                ProjectCode = NormalizeProjectCode(project.ProjectCode)!,
                DisplayName = project.DisplayName,
                ApiKeyHash = project.ApiKeyHash,
                Enabled = project.Enabled,
                RetentionDays = project.RetentionDays,
            };
        }

        private static string? NormalizeProjectCode(string? projectCode)
        {
            return string.IsNullOrWhiteSpace(projectCode) ? null : projectCode.Trim();
        }

        private static bool IsAsc(ApplicationLogQueryDto query)
        {
            return string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ApplicationLogGroupCountDto> Group(
            List<ApplicationLog> logs,
            Func<ApplicationLog, string> selector
        )
        {
            return logs
                .GroupBy(selector)
                .Select(group => new ApplicationLogGroupCountDto
                {
                    Name = group.Key,
                    Count = group.Count(),
                })
                .OrderByDescending(item => item.Count)
                .Take(20)
                .ToList();
        }

        private async Task<List<ApplicationLogGroupCountDto>> QueryGroupAsync(
            ApplicationLogQueryDto query,
            string fieldName
        )
        {
            var sqlField = fieldName switch
            {
                "ProjectCode" => nameof(ApplicationLog.ProjectCode),
                "Level" => nameof(ApplicationLog.Level),
                "ExceptionType" => nameof(ApplicationLog.ExceptionType),
                "RequestPath" => nameof(ApplicationLog.RequestPath),
                _ => nameof(ApplicationLog.ProjectCode),
            };
            var dbQuery = ApplyQuery(_db.Queryable<ApplicationLog>(), query);
            var groups = await dbQuery
                .GroupBy(sqlField)
                .Select<ApplicationLogGroupCountDto>(
                    $"{sqlField} AS Name, COUNT(1) AS Count"
                )
                .OrderBy("Count DESC")
                .Take(20)
                .ToListAsync();

            return groups
                .Select(item => new ApplicationLogGroupCountDto
                {
                    Name = string.IsNullOrWhiteSpace(item.Name)
                        ? fieldName switch
                        {
                            "ExceptionType" => "无异常类型",
                            "RequestPath" => "无请求路径",
                            _ => "未设置",
                        }
                        : item.Name,
                    Count = item.Count,
                })
                .ToList();
        }

        private static ApplicationLogDto ToDto(ApplicationLog entity)
        {
            return new ApplicationLogDto
            {
                Id = entity.Id,
                TimestampUtc = AsUtc(entity.TimestampUtc),
                ProjectCode = entity.ProjectCode,
                ProjectName = entity.ProjectName,
                Environment = entity.Environment,
                SourceType = entity.SourceType,
                ServiceName = entity.ServiceName,
                InstanceId = entity.InstanceId,
                ClientEventId = entity.ClientEventId,
                StoreCode = entity.StoreCode,
                DeviceCode = entity.DeviceCode,
                AppVersion = entity.AppVersion,
                Level = entity.Level,
                Category = entity.Category,
                EventId = entity.EventId,
                Message = entity.Message,
                ExceptionType = entity.ExceptionType,
                ExceptionMessage = entity.ExceptionMessage,
                StackTrace = entity.StackTrace,
                RequestPath = entity.RequestPath,
                RequestMethod = entity.RequestMethod,
                StatusCode = entity.StatusCode,
                TraceId = entity.TraceId,
                UserId = entity.UserId,
                UserName = entity.UserName,
                ClientIp = entity.ClientIp,
                PropertiesJson = entity.PropertiesJson,
                CreatedAtUtc = AsUtc(entity.CreatedAt),
            };
        }

        private static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        private static string ComputeSha256(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private static string? SanitizeIdentifier(string? value, int maxLength)
        {
            var sanitized = SanitizeText(value)?.Trim();
            if (
                string.IsNullOrWhiteSpace(sanitized)
                || sanitized.Length > maxLength
                || !SafeIdentifierPattern.IsMatch(sanitized)
            )
                return null;

            return sanitized;
        }

        private static List<string> NormalizeProjectCodes(ApplicationLogQueryDto query)
        {
            return (query.ProjectCodes ?? new List<string>())
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? SerializeSafeProperties(Dictionary<string, object?>? properties)
        {
            if (properties == null || properties.Count == 0)
                return null;

            var safeProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in properties)
            {
                var safeKey = SanitizePropertyKey(item.Key);
                // NFKC 或脱敏后发生键碰撞时采用 last-wins，不能让单条坏日志抛出 500。
                safeProperties[safeKey] = ToJsonSafeValue(item.Value, key: item.Key);
            }
            return JsonSerializer.Serialize(safeProperties);
        }

        private static object? ToJsonSafeValue(object? value, int depth = 0, string? key = null)
        {
            if (IsSensitiveKey(key))
                return RedactedValue;
            if (value == null)
                return null;
            if (depth >= 4)
                return SanitizeText(value.ToString());

            return value switch
            {
                string stringValue => SanitizeText(stringValue),
                bool or char => value,
                byte or sbyte or short or ushort or int or uint or long or ulong => value,
                double doubleValue => double.IsFinite(doubleValue)
                    ? doubleValue
                    : doubleValue.ToString(CultureInfo.InvariantCulture),
                float floatValue => float.IsFinite(floatValue)
                    ? floatValue
                    : floatValue.ToString(CultureInfo.InvariantCulture),
                decimal => value,
                DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString(),
                Enum enumValue => enumValue.ToString(),
                JsonElement jsonElement => ToJsonSafeValueFromElement(jsonElement, depth, key),
                IDictionary dictionary => ToJsonSafeDictionary(dictionary, depth),
                IEnumerable enumerable when value is not string => enumerable
                    .Cast<object?>()
                    .Select(item => ToJsonSafeValue(item, depth + 1))
                    .ToList(),
                _ => SanitizeText(value.ToString()),
            };
        }

        private static Dictionary<string, object?> ToJsonSafeDictionary(
            IDictionary dictionary,
            int depth
        )
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var dictionaryKey in dictionary.Keys.Cast<object?>())
            {
                var originalKey = dictionaryKey?.ToString() ?? string.Empty;
                var safeKey = SanitizePropertyKey(originalKey);
                properties[safeKey] = ToJsonSafeValue(
                    dictionaryKey == null ? null : dictionary[dictionaryKey],
                    depth + 1,
                    originalKey
                );
            }

            return properties;
        }

        private static object? ToJsonSafeValueFromElement(
            JsonElement element,
            int depth,
            string? key = null
        )
        {
            if (IsSensitiveKey(key))
                return RedactedValue;
            if (depth >= 4)
                return SanitizeText(element.ToString());

            return element.ValueKind switch
            {
                JsonValueKind.Object => ToJsonSafeObject(element, depth),
                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Select(item => ToJsonSafeValueFromElement(item, depth + 1))
                    .ToList(),
                JsonValueKind.String => SanitizeText(element.GetString()),
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => SanitizeText(element.ToString()),
            };
        }

        private static Dictionary<string, object?> ToJsonSafeObject(JsonElement element, int depth)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                // JSON 重复键采用确定性的 last-wins，单个客户端坏数据不能打断同批日志。
                properties[SanitizePropertyKey(property.Name)] = ToJsonSafeValueFromElement(
                    property.Value,
                    depth + 1,
                    property.Name
                );
            }

            return properties;
        }

        private static bool IsSensitiveKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string compatibilityNormalized;
            try
            {
                compatibilityNormalized = key.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return true;
            }

            // NFKC 可收敛全宽等兼容字符；仍含非 ASCII 的混合脚本键一律 fail-closed。
            if (compatibilityNormalized.Any(character => character > 0x7F))
                return true;

            var normalized = new string(
                compatibilityNormalized.Where(character =>
                    (character >= 'A' && character <= 'Z')
                    || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                ).ToArray()
            ).ToLowerInvariant();
            // 授权方式只是审计枚举，不是凭据；与自由文本规则保持一致。
            return normalized != "authorizationmode" && SensitiveKeyFragments.Any(normalized.Contains);
        }

        private static string SanitizePropertyKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return RedactedPropertyKey;

            try
            {
                var normalized = key.Normalize(NormalizationForm.FormKC).Trim();
                return normalized.Length is > 0 and <= 128
                    && IsAssignmentKey(normalized)
                    && !IsSensitiveKey(normalized)
                    ? normalized
                    : RedactedPropertyKey;
            }
            catch (ArgumentException)
            {
                return RedactedPropertyKey;
            }
        }

        private static string? SanitizeRequestPath(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var queryIndex = value.IndexOfAny(new[] { '?', '#' });
            return queryIndex < 0 ? value : value[..queryIndex];
        }

        private static string? SanitizeText(string? value)
        {
            return SanitizeText(value, depth: 0);
        }

        private static string? SanitizeText(string? value, int depth)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var structuredValue = depth >= MaxJsonSanitizeDepth
                ? value
                : SanitizeJsonFragments(value, depth);
            return SanitizeFlatText(structuredValue);
        }

        /**
         * 日志可能把 JSON 作为完整字段或嵌进普通文字。敏感父键的值若是对象/数组，
         * 单层正则只会替换首项；这里用结构化递归替换整个值，避免尾项泄漏。
         */
        private static string SanitizeJsonFragments(string value, int depth)
        {
            var firstNonWhitespace = FirstNonWhitespaceIndex(value);
            if (
                firstNonWhitespace >= 0
                && (value[firstNonWhitespace] == '{' || value[firstNonWhitespace] == '[')
                && TrySanitizeJsonFragment(value, depth, out var root)
            )
                return root;

            // 一次扫描仅产出最外层候选，避免每个 `{`/`[` 都重复扫描到文本尾部。
            var result = new StringBuilder(value.Length);
            var copiedUntil = 0;
            foreach (var candidate in ScanJsonFragments(value))
            {
                var end = candidate.End ?? value.Length - 1;
                var fragment = value[candidate.Start..(end + 1)];
                var hasSensitiveParent = candidate.HasSensitivePrefix || HasSensitiveStructuredParent(fragment);
                // 前置 assignment 已确认敏感时优先整值替换；即使 `{...}` 自身可解析也不能绕过父键。
                var replacement = candidate.HasSensitivePrefix
                    ? RedactedValue
                    : candidate.IsTrusted
                        ? TrySanitizeJsonFragment(fragment, depth, out var sanitizedFragment)
                        ? sanitizedFragment
                        : hasSensitiveParent
                            ? RedactedValue
                            : null
                    : hasSensitiveParent
                        ? RedactedValue
                        : null;
                if (replacement == null)
                    continue;

                result.Append(value, copiedUntil, candidate.Start - copiedUntil);
                result.Append(replacement);
                copiedUntil = end + 1;
            }

            return copiedUntil == 0
                ? value
                : result.Append(value, copiedUntil, value.Length - copiedUntil).ToString();
        }

        private static bool HasSensitiveStructuredParent(string value)
        {
            foreach (Match match in JsonStructuredPropertyPattern.Matches(value))
            {
                if (IsSensitiveKey(match.Groups[2].Value))
                    return true;
            }

            return false;
        }

        private static bool HasSensitiveStructuredAssignmentPrefix(string value, int structureStart)
        {
            // 逆向跳过任意空白和分隔符；键本身最多读取 128 字符，空白长度不能绕过。
            var cursor = structureStart - 1;
            while (cursor >= 0 && char.IsWhiteSpace(value[cursor]))
                cursor--;
            if (cursor < 0 || (value[cursor] != ':' && value[cursor] != '='))
                return false;
            cursor--;
            while (cursor >= 0 && char.IsWhiteSpace(value[cursor]))
                cursor--;
            // 已确认 assignment 但没有完整键，不能把结构交给平面回退。
            if (cursor < 0)
                return true;

            var quote = value[cursor];
            if (quote is '"' or '\'')
                return HasSensitiveOrUntrustedQuotedAssignmentKey(value, cursor, quote);

            return HasSensitiveOrUntrustedUnquotedAssignmentKey(value, cursor);
        }

        private static bool HasSensitiveOrUntrustedQuotedAssignmentKey(
            string value,
            int keyEnd,
            char quote
        )
        {
            // 结束引号本身被转义时，不能把它当成一个可信的键结尾。
            if (IsEscapedCharacter(value, keyEnd))
                return true;

            var keyStart = keyEnd - 1;
            while (
                keyStart >= 0
                && keyEnd - keyStart <= 256
                && (value[keyStart] != quote || IsEscapedCharacter(value, keyStart))
            )
            {
                keyStart--;
            }
            if (
                keyStart < 0
                || keyEnd - keyStart > 256
                || !IsTrustedAssignmentBoundary(value, keyStart - 1)
            )
                return true;

            var key = DecodeQuotedAssignmentKey(value[keyStart..(keyEnd + 1)], quote);
            // 引号、转义、外侧边界或解码后的键任一不完整，均不能让结构值回退到平面规则。
            return key == null || !IsAssignmentKey(key) || IsSensitiveKey(key);
        }

        private static bool HasSensitiveOrUntrustedUnquotedAssignmentKey(string value, int keyEnd)
        {
            // `响应片段: {...}` 是常见诊断标签，并非 assignment；仅纯非 ASCII 标签可走此分支。
            // 一旦混入 ASCII 键字符或不可信符号，仍按潜在键 fail-closed。
            if (
                !IsAssignmentKeyCharacter(value[keyEnd])
                && IsPlainTextStructuredLabel(value, keyEnd)
            )
                return false;

            var keyStart = keyEnd;
            var keyLength = 0;
            while (keyStart >= 0 && !IsTrustedAssignmentBoundary(value, keyStart))
            {
                if (keyLength >= 128 || !IsAssignmentKeyCharacter(value[keyStart]))
                    return true;
                keyStart--;
                keyLength++;
            }

            if (keyLength == 0)
                return true;
            var key = value[(keyStart + 1)..(keyEnd + 1)];
            return !IsAssignmentKey(key) || IsSensitiveKey(key);
        }

        private static bool IsPlainTextStructuredLabel(string value, int labelEnd)
        {
            var cursor = labelEnd;
            var sawNonAscii = false;
            var length = 0;
            while (cursor >= 0 && !IsTrustedAssignmentBoundary(value, cursor))
            {
                var character = value[cursor];
                if (
                    length >= 128
                    || IsAssignmentKeyCharacter(character)
                    || character <= 0x1F
                    || "/@#'\"=:".Contains(character)
                )
                    return false;
                sawNonAscii |= character > 0x7F;
                cursor--;
                length++;
            }

            return sawNonAscii;
        }

        private static bool IsAssignmentKey(string value)
        {
            return value.Length > 0 && value.All(IsAssignmentKeyCharacter);
        }

        private static bool IsAssignmentKeyCharacter(char value)
        {
            return value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.'
                or '_'
                or '-';
        }

        private static bool IsTrustedAssignmentBoundary(string value, int index)
        {
            if (index < 0)
                return true;
            var character = value[index];
            return char.IsWhiteSpace(character) || character is ',' or ';' or '|' or '(' or '[' or '{';
        }

        private static bool IsEscapedCharacter(string value, int index)
        {
            var slashCount = 0;
            for (var cursor = index - 1; cursor >= 0 && value[cursor] == '\\'; cursor--)
                slashCount++;
            return slashCount % 2 == 1;
        }

        private static string? DecodeQuotedAssignmentKey(string value, char quote)
        {
            try
            {
                if (
                    value.Length < 2
                    || value[0] != quote
                    || value[^1] != quote
                    || IsEscapedCharacter(value, value.Length - 1)
                )
                    return null;
                if (quote == '"')
                {
                    var parsed = JsonSerializer.Deserialize<string>(value);
                    return parsed is { Length: <= 128 } ? parsed : null;
                }

                var result = new StringBuilder();
                for (var index = 1; index < value.Length - 1; index++)
                {
                    if (value[index] != '\\')
                    {
                        result.Append(value[index]);
                        continue;
                    }

                    if (++index >= value.Length - 1)
                        return null;
                    if (value[index] == 'u')
                    {
                        if (index + 4 >= value.Length - 1)
                            return null;
                        var hex = value.Substring(index + 1, 4);
                        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint))
                            return null;
                        result.Append((char)codePoint);
                        index += 4;
                    }
                    else
                    {
                        var decoded = value[index] switch
                        {
                            '\\' => '\\',
                            '\'' => '\'',
                            '"' => '"',
                            'b' => '\b',
                            'f' => '\f',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => (char?)null,
                        };
                        if (decoded == null)
                            return null;
                        result.Append(decoded.Value);
                    }
                }

                return result.Length <= 128 ? result.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySanitizeJsonFragment(string value, int depth, out string sanitized)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    value,
                    new JsonDocumentOptions { MaxDepth = MaxJsonFragmentDepth }
                );
                sanitized = JsonSerializer.Serialize(ToSanitizedJsonFragmentValue(document.RootElement, depth));
                return true;
            }
            catch
            {
                // 非法 JSON 继续由自由文本规则处理，日志旁路不能因解析失败抛出。
                sanitized = string.Empty;
                return false;
            }
        }

        private static object? ToSanitizedJsonFragmentValue(JsonElement element, int depth)
        {
            // 到达上限时整段替换，既避免深递归，也保证未知深层不泄漏。
            if (depth >= MaxJsonSanitizeDepth)
                return RedactedValue;

            return element.ValueKind switch
            {
                JsonValueKind.Object => ToSanitizedJsonFragmentObject(element, depth),
                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Select(item => ToSanitizedJsonFragmentValue(item, depth + 1))
                    .ToList(),
                JsonValueKind.String => SanitizeText(element.GetString(), depth + 1),
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => SanitizeText(element.ToString(), depth + 1),
            };
        }

        private static Dictionary<string, object?> ToSanitizedJsonFragmentObject(
            JsonElement element,
            int depth
        )
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var safeKey = SanitizePropertyKey(property.Name);
                properties[safeKey] = IsSensitiveKey(property.Name)
                    ? RedactedValue
                    : ToSanitizedJsonFragmentValue(property.Value, depth + 1);
            }

            return properties;
        }

        private static List<JsonFragmentCandidate> ScanJsonFragments(string value)
        {
            var candidates = new List<JsonFragmentCandidate>();
            var closers = new Stack<char>();
            var start = -1;
            var quoted = false;
            var escaped = false;
            var overflowDepth = 0;
            var isTrusted = true;
            var hasSensitivePrefix = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (quoted)
                {
                    if (escaped)
                        escaped = false;
                    else if (character == '\\')
                        escaped = true;
                    else if (character == '"')
                        quoted = false;
                    continue;
                }

                if (character == '"' && closers.Count > 0)
                    quoted = true;
                else if (character is '{' or '[')
                {
                    var isSensitivePrefix = HasSensitiveStructuredAssignmentPrefix(value, index);
                    if (closers.Count == 0)
                    {
                        start = index;
                        hasSensitivePrefix = isSensitivePrefix;
                    }
                    else
                    {
                        hasSensitivePrefix |= isSensitivePrefix;
                    }
                    if (closers.Count >= MaxJsonFragmentDepth)
                    {
                        overflowDepth++;
                        isTrusted = false;
                    }
                    else
                    {
                        closers.Push(character == '{' ? '}' : ']');
                    }
                }
                else if (character is '}' or ']')
                {
                    if (closers.Count == 0)
                        continue;
                    if (overflowDepth > 0)
                    {
                        overflowDepth--;
                        continue;
                    }
                    if (closers.Pop() != character)
                    {
                        // 括号类型错配后不再把后续子对象当独立可信 JSON，防止脱离敏感父键泄漏。
                        candidates.Add(new JsonFragmentCandidate(start, null, IsTrusted: false, hasSensitivePrefix));
                        return candidates;
                    }
                    if (closers.Count == 0)
                    {
                        candidates.Add(new JsonFragmentCandidate(start, index, isTrusted, hasSensitivePrefix));
                        start = -1;
                        isTrusted = true;
                        hasSensitivePrefix = false;
                    }
                }
            }

            if (closers.Count > 0)
                candidates.Add(new JsonFragmentCandidate(start, null, IsTrusted: false, hasSensitivePrefix));
            return candidates;
        }

        private static int FirstNonWhitespaceIndex(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                    return index;
            }

            return -1;
        }

        private static string SanitizeFlatText(string value)
        {
            var sanitized = UrlQueryPattern.Replace(value, "${url}");
            sanitized = BearerTokenPattern.Replace(sanitized, RedactedValue);
            sanitized = SensitiveQuotedAssignmentPattern.Replace(
                sanitized,
                match => ShouldRedactQuotedKey(
                    match.Groups["key"].Value,
                    match.Groups["keyQuote"].Value[0]
                )
                    ? $"{match.Groups["prefix"].Value}{RedactedValue}{match.Groups["suffix"].Value}"
                    : match.Value
            );
            sanitized = SensitiveQuotedUnquotedAssignmentPattern.Replace(
                sanitized,
                match => ShouldRedactQuotedKey(
                    match.Groups["key"].Value,
                    match.Groups["keyQuote"].Value[0]
                )
                    ? $"{match.Groups["keyQuote"].Value}{match.Groups["key"].Value}{match.Groups["keyQuote"].Value}{match.Groups["separator"].Value}{match.Groups["keyQuote"].Value}{RedactedValue}{match.Groups["keyQuote"].Value}"
                    : match.Value
            );
            sanitized = SensitiveAssignmentPattern.Replace(
                sanitized,
                match => IsSensitiveKey(match.Groups["key"].Value)
                    ? $"{match.Groups["key"].Value}={RedactedValue}"
                    : match.Value
            );
            return PanPattern.Replace(sanitized, "[REDACTED_CARD]");
        }

        private static bool ShouldRedactQuotedKey(string key, char quote)
        {
            var decoded = DecodeQuotedAssignmentKey($"{quote}{key}{quote}", quote);
            return decoded == null || !IsAssignmentKey(decoded) || IsSensitiveKey(decoded);
        }

        private readonly record struct JsonFragmentCandidate(
            int Start,
            int? End,
            bool IsTrusted,
            bool HasSensitivePrefix
        );
    }
}
