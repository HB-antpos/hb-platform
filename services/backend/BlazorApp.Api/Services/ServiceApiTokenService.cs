using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public sealed class ServiceApiTokenService : IServiceApiTokenService
    {
        private const int TokenBytes = 32;
        private const int TokenPrefixDisplayLength = 18;
        private const int MaxNameLength = 120;
        private const int MaxActorLength = 120;
        private const int MaxIpLength = 64;
        private const string ActiveStatus = "active";
        private const string RevokedStatus = "revoked";
        private const string ExpiredStatus = "expired";
        private static readonly IReadOnlyDictionary<string, string[]> PurposeScopes =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [ServiceApiTokenPurposes.MobileOtaPublisher] =
                    [Permissions.System.ManageAppDownloads],
                [ServiceApiTokenPurposes.PosIpadUpdateDecisionReader] =
                    [ServiceApiScopes.ReadAppUpdateDecisions],
                [ServiceApiTokenPurposes.QualityCiReporter] =
                    [ServiceApiScopes.WritePerformanceMetrics],
                [ServiceApiTokenPurposes.DeploymentAcceptanceReporter] =
                    [ServiceApiScopes.WriteReleaseEvents],
                // 只给 POS API 用：凭它为已登记设备换订货助手令牌，不能读写任何业务数据。
                [ServiceApiTokenPurposes.PosSupplierAssistantTokenIssuer] =
                    [ServiceApiScopes.IssuePosDeviceSupplierAssistantTokens],
            };
        private readonly ISqlSugarClient _db;
        private readonly ILogger<ServiceApiTokenService> _logger;

        public ServiceApiTokenService(SqlSugarContext context, ILogger<ServiceApiTokenService> logger)
        {
            _db = context.Db;
            _logger = logger;
        }

        public async Task<ApiResponse<List<ServiceApiTokenDto>>> ListAsync()
        {
            var items = await _db
                .Queryable<ServiceApiToken>()
                .Where(token => !token.IsDeleted)
                .OrderByDescending(token => token.CreatedAt)
                .ToListAsync();

            return ApiResponse<List<ServiceApiTokenDto>>.OK(items.Select(MapToDto).ToList());
        }

        public async Task<ApiResponse<ServiceApiTokenCreateResponseDto>> CreateAsync(
            ServiceApiTokenCreateRequestDto request,
            string createdBy
        )
        {
            var name = NormalizeName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return ApiResponse<ServiceApiTokenCreateResponseDto>.Error(
                    "Token 名称不能为空",
                    "SERVICE_API_TOKEN_NAME_REQUIRED"
                );
            }

            if (name.Length > MaxNameLength)
            {
                return ApiResponse<ServiceApiTokenCreateResponseDto>.Error(
                    $"Token 名称不能超过 {MaxNameLength} 个字符",
                    "SERVICE_API_TOKEN_NAME_TOO_LONG"
                );
            }

            var purpose = NormalizePurpose(request.Purpose);
            if (!PurposeScopes.TryGetValue(purpose, out var scopes))
            {
                return ApiResponse<ServiceApiTokenCreateResponseDto>.Error(
                    "Token purpose 不受支持",
                    "SERVICE_API_TOKEN_PURPOSE_INVALID"
                );
            }

            var now = DateTime.UtcNow;
            var plaintextToken = GenerateToken();
            var entity = new ServiceApiToken
            {
                Id = Guid.NewGuid(),
                Name = name,
                TokenHash = HashToken(plaintextToken),
                TokenPrefix = plaintextToken[..TokenPrefixDisplayLength],
                Scopes = JsonSerializer.Serialize(scopes),
                CreatedAt = now,
                CreatedBy = NormalizeActor(createdBy),
                UpdatedAt = now,
                UpdatedBy = NormalizeActor(createdBy),
                IsDeleted = false,
            };

            await _db.Insertable(entity).ExecuteCommandAsync();

            return ApiResponse<ServiceApiTokenCreateResponseDto>.OK(
                MapToCreateResponse(entity, plaintextToken),
                "Service API Token 创建成功，请立即保存明文"
            );
        }

        public async Task<ApiResponse<ServiceApiTokenDto>> RevokeAsync(Guid id, string revokedBy)
        {
            var entity = await _db
                .Queryable<ServiceApiToken>()
                .FirstAsync(token => token.Id == id && !token.IsDeleted);
            if (entity == null)
            {
                return ApiResponse<ServiceApiTokenDto>.Error(
                    "Service API Token 不存在",
                    "SERVICE_API_TOKEN_NOT_FOUND"
                );
            }

            if (!entity.RevokedAt.HasValue)
            {
                var actor = NormalizeActor(revokedBy);
                var revokedAt = DateTime.UtcNow;
                await _db
                    .Updateable<ServiceApiToken>()
                    .SetColumns(token => token.RevokedAt == revokedAt)
                    .SetColumns(token => token.RevokedBy == actor)
                    .SetColumns(token => token.UpdatedAt == revokedAt)
                    .SetColumns(token => token.UpdatedBy == actor)
                    .Where(token => token.Id == id && !token.IsDeleted && token.RevokedAt == null)
                    .ExecuteCommandAsync();

                entity =
                    await _db
                        .Queryable<ServiceApiToken>()
                        .FirstAsync(token => token.Id == id && !token.IsDeleted)
                    ?? entity;
            }

            return ApiResponse<ServiceApiTokenDto>.OK(MapToDto(entity), "Service API Token 已撤销");
        }

        public async Task<ServiceApiTokenValidationResult?> ValidateAsync(
            string token,
            string? lastUsedIp
        )
        {
            if (
                string.IsNullOrWhiteSpace(token)
                || !token.StartsWith(
                    ServiceApiTokenAuthenticationDefaults.TokenPrefix,
                    StringComparison.Ordinal
                )
            )
            {
                return null;
            }

            var tokenHash = HashToken(token);
            var entity = await _db
                .Queryable<ServiceApiToken>()
                .FirstAsync(item => item.TokenHash == tokenHash && !item.IsDeleted);
            var now = DateTime.UtcNow;
            if (entity == null || entity.RevokedAt.HasValue || entity.ExpiresAt <= now)
            {
                return null;
            }

            entity.LastUsedAt = now;
            entity.LastUsedIp = NormalizeIp(lastUsedIp);
            entity.UpdatedAt = now;
            entity.UpdatedBy = "ServiceApiToken";

            try
            {
                // 关键位置：这里只写最后使用审计列，并在 WHERE 再检查撤销/过期状态，避免并发撤销被旧实体覆盖。
                var affectedRows = await _db
                    .Updateable<ServiceApiToken>()
                    .SetColumns(token => token.LastUsedAt == entity.LastUsedAt)
                    .SetColumns(token => token.LastUsedIp == entity.LastUsedIp)
                    .SetColumns(token => token.UpdatedAt == entity.UpdatedAt)
                    .SetColumns(token => token.UpdatedBy == entity.UpdatedBy)
                    .Where(token =>
                        token.Id == entity.Id
                        && !token.IsDeleted
                        && token.RevokedAt == null
                        && (token.ExpiresAt == null || token.ExpiresAt > now)
                    )
                    .ExecuteCommandAsync();

                if (affectedRows == 0)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Service API Token 最后使用时间更新失败，TokenId={TokenId}", entity.Id);
                return null;
            }

            return new ServiceApiTokenValidationResult
            {
                Id = entity.Id,
                Name = entity.Name,
                TokenPrefix = entity.TokenPrefix,
                Scopes = ParseScopes(entity.Scopes),
                ExpiresAt = entity.ExpiresAt,
                LastUsedAt = entity.LastUsedAt,
            };
        }

        private static ServiceApiTokenDto MapToDto(ServiceApiToken entity)
        {
            return new ServiceApiTokenDto
            {
                Id = entity.Id,
                Name = entity.Name,
                TokenPrefix = entity.TokenPrefix,
                Scopes = ParseScopes(entity.Scopes),
                Status = ResolveStatus(entity),
                CreatedAt = entity.CreatedAt,
                ExpiresAt = entity.ExpiresAt,
                RevokedAt = entity.RevokedAt,
                LastUsedAt = entity.LastUsedAt,
                LastUsedIp = entity.LastUsedIp,
            };
        }

        private static ServiceApiTokenCreateResponseDto MapToCreateResponse(
            ServiceApiToken entity,
            string plaintextToken
        )
        {
            return new ServiceApiTokenCreateResponseDto
            {
                Id = entity.Id,
                Name = entity.Name,
                TokenPrefix = entity.TokenPrefix,
                Scopes = ParseScopes(entity.Scopes),
                Status = ResolveStatus(entity),
                CreatedAt = entity.CreatedAt,
                ExpiresAt = entity.ExpiresAt,
                RevokedAt = entity.RevokedAt,
                LastUsedAt = entity.LastUsedAt,
                LastUsedIp = entity.LastUsedIp,
                Token = plaintextToken,
            };
        }

        private static string ResolveStatus(ServiceApiToken entity)
        {
            if (entity.RevokedAt.HasValue)
            {
                return RevokedStatus;
            }

            if (entity.ExpiresAt <= DateTime.UtcNow)
            {
                return ExpiredStatus;
            }

            return ActiveStatus;
        }

        private static List<string> ParseScopes(string? scopes)
        {
            if (string.IsNullOrWhiteSpace(scopes))
            {
                return new List<string>();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(scopes);
                if (parsed != null)
                {
                    return parsed.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToList();
                }
            }
            catch (JsonException)
            {
                // 兼容未来手工修复或旧格式：JSON 解析失败时退回到分隔符解析。
            }

            return scopes
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(scope => scope.Trim())
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .ToList();
        }

        private static string GenerateToken()
        {
            Span<byte> bytes = stackalloc byte[TokenBytes];
            RandomNumberGenerator.Fill(bytes);
            var encoded = Convert
                .ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return ServiceApiTokenAuthenticationDefaults.TokenPrefix + encoded;
        }

        private static string HashToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string NormalizeName(string? value) => (value ?? string.Empty).Trim();

        private static string NormalizePurpose(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? ServiceApiTokenPurposes.MobileOtaPublisher
                : value.Trim().ToLowerInvariant();

        private static string NormalizeActor(string? value)
        {
            var actor = string.IsNullOrWhiteSpace(value) ? "System" : value.Trim();
            return actor.Length <= MaxActorLength ? actor : actor[..MaxActorLength];
        }

        private static string? NormalizeIp(string? value)
        {
            var ip = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (ip == null)
            {
                return null;
            }

            return ip.Length <= MaxIpLength ? ip : ip[..MaxIpLength];
        }
    }
}
