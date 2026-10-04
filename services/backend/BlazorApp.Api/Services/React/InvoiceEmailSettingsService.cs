using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 发票邮件 SMTP 配置读写服务。
    /// </summary>
    public class InvoiceEmailSettingsService : IInvoiceEmailSettingsService
    {
        private const string DataProtectionPurpose = "Hbweb.InvoiceEmail.SmtpPassword.v1";
        private const string DataProtectionPayloadPrefix = "CfDJ8";

        private readonly SqlSugarContext _context;
        private readonly InvoiceEmailOptions _fallbackOptions;
        private readonly IDataProtector _protector;
        private readonly ILogger<InvoiceEmailSettingsService> _logger;

        public InvoiceEmailSettingsService(
            SqlSugarContext context,
            IOptions<InvoiceEmailOptions> fallbackOptions,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<InvoiceEmailSettingsService> logger
        )
        {
            _context = context;
            _fallbackOptions = fallbackOptions.Value ?? new InvoiceEmailOptions();
            _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
            _logger = logger;
        }

        public async Task<ApiResponse<InvoiceEmailSettingsDto>> GetSettingsAsync(
            CancellationToken cancellationToken = default
        )
        {
            var models = await QueryModelsAsync();
            var dto = models.Count == 0
                ? ToDto(_fallbackOptions)
                : ToDto(models);

            return ApiResponse<InvoiceEmailSettingsDto>.OK(dto, "查询成功");
        }

        public async Task<ApiResponse<InvoiceEmailSettingsDto>> UpdateSettingsAsync(
            UpdateInvoiceEmailSettingsDto request,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            var validationError = ValidateSettingsRequest(request);
            if (validationError != null)
            {
                return ApiResponse<InvoiceEmailSettingsDto>.Error(
                    validationError.Value.Message,
                    validationError.Value.Code
                );
            }

            var existingModels = await QueryModelsAsync();
            var existingById = existingModels.ToDictionary(
                item => item.Id,
                StringComparer.OrdinalIgnoreCase
            );
            var incomingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<InvoiceEmailConfiguration>();
            var now = DateTime.UtcNow;

            try
            {
                foreach (var account in request.Accounts)
                {
                    var accountId = NormalizeOptional(account.Id) ?? CreateAccountId();
                    if (!incomingIds.Add(accountId))
                    {
                        return ApiResponse<InvoiceEmailSettingsDto>.Error(
                            "发件邮箱账号 ID 不能重复",
                            "INVOICE_EMAIL_ACCOUNT_ID_DUPLICATED"
                        );
                    }

                    existingById.TryGetValue(accountId, out var existing);
                    var model = existing ?? new InvoiceEmailConfiguration
                    {
                        Id = accountId,
                        CreatedAt = now,
                        CreatedBy = NormalizeOptional(updatedBy),
                    };

                    model.Name = account.Name.Trim();
                    model.Host = account.Host.Trim();
                    model.Port = account.Port;
                    model.UseSsl = account.UseSsl;
                    model.CheckCertificateRevocation = account.CheckCertificateRevocation;
                    model.Username = NormalizeOptional(account.Username);
                    // 密码留空只保留同一个账号的旧存储密码；首次从 appsettings fallback 落库时才迁移默认账号密码。
                    model.EncryptedPassword = ResolveStoredPassword(
                        existing,
                        account,
                        existingModels.Count == 0 && IsDefaultAccountId(accountId)
                    );
                    model.FromEmail = account.FromEmail.Trim();
                    model.FromName = NormalizeOptional(account.FromName);
                    model.MaxAttachmentBytes = account.MaxAttachmentBytes;
                    model.IsDefault = account.IsDefault;
                    model.UpdatedAtUtc = now;
                    model.UpdatedAt = now;
                    model.UpdatedBy = NormalizeOptional(updatedBy);
                    models.Add(model);
                }
            }
            catch (InvoiceEmailPasswordDecryptException)
            {
                return ApiResponse<InvoiceEmailSettingsDto>.Error(
                    "发票邮件 SMTP 密码解密失败，请重新输入 SMTP 密码后保存发票邮箱配置",
                    "INVOICE_EMAIL_PASSWORD_DECRYPT_FAILED"
                );
            }

            var transactionResult = await _context.Db.Ado.UseTranAsync(async () =>
            {
                var removedModels = existingModels
                    .Where(item => !incomingIds.Contains(item.Id))
                    .ToList();

                foreach (var removedModel in removedModels)
                {
                    // 保存列表即为最终账号集合，前端删除的账号在这里物理删除，避免旧账号继续被发送链路选中。
                    await _context.Db.Deleteable<InvoiceEmailConfiguration>()
                        .Where(item => item.Id == removedModel.Id)
                        .ExecuteCommandAsync();
                }

                foreach (var model in models)
                {
                    if (existingById.ContainsKey(model.Id))
                    {
                        await _context.Db.Updateable(model).ExecuteCommandAsync();
                    }
                    else
                    {
                        await _context.Db.Insertable(model).ExecuteCommandAsync();
                    }
                }
            });

            if (!transactionResult.IsSuccess)
            {
                _logger.LogError(transactionResult.ErrorException, "保存发票邮件账号配置失败");
                return ApiResponse<InvoiceEmailSettingsDto>.Error(
                    "发票邮件配置保存失败",
                    "INVOICE_EMAIL_SETTINGS_SAVE_FAILED"
                );
            }

            return ApiResponse<InvoiceEmailSettingsDto>.OK(ToDto(models), "发票邮件配置已更新");
        }

        public async Task<InvoiceEmailOptions> GetEffectiveOptionsAsync(
            CancellationToken cancellationToken = default
        )
        {
            var models = await QueryModelsAsync();
            if (models.Count == 0)
            {
                return CloneOptions(_fallbackOptions);
            }

            var defaultModels = models.Where(item => item.IsDefault).ToList();
            if (defaultModels.Count != 1)
            {
                throw new InvoiceEmailDefaultAccountException("发票邮件默认发件账号配置异常");
            }

            // 未指定发件账号时只认默认账号；异常默认标记会显式报错，避免从错误 SMTP 账号发出发票。
            return ToOptions(defaultModels.Single());
        }

        public async Task<InvoiceEmailOptions> GetAccountOptionsAsync(
            string? accountId,
            CancellationToken cancellationToken = default
        )
        {
            var normalizedId = NormalizeOptional(accountId);
            if (normalizedId == null)
            {
                return await GetEffectiveOptionsAsync(cancellationToken);
            }

            var model = await QueryModelAsync(normalizedId);
            if (model != null)
            {
                return ToOptions(model);
            }

            // 尚未落库时页面只展示 appsettings fallback 的 default 账号，允许按该 ID 发送。
            if (IsDefaultAccountId(normalizedId) && !await HasSavedAccountsAsync())
            {
                return CloneOptions(_fallbackOptions);
            }

            // 指定账号已被删除或 ID 错误时显式失败，不能悄悄改用默认账号，避免用错发件人。
            throw new InvoiceEmailAccountNotFoundException($"发件邮箱账号不存在：{normalizedId}");
        }

        public async Task<List<InvoiceEmailSenderAccountDto>> GetSenderAccountsAsync(
            CancellationToken cancellationToken = default
        )
        {
            var models = await QueryModelsAsync();
            if (models.Count == 0)
            {
                // 未落库且 appsettings 也没配发件邮箱时返回空列表，前端按默认账号发送并由发送链路报未配置。
                return string.IsNullOrWhiteSpace(_fallbackOptions.FromEmail)
                    ? new List<InvoiceEmailSenderAccountDto>()
                    : new List<InvoiceEmailSenderAccountDto>
                    {
                        new()
                        {
                            Id = InvoiceEmailConfiguration.DefaultId,
                            Name = "默认发件账号",
                            FromEmail = _fallbackOptions.FromEmail!,
                            FromName = _fallbackOptions.FromName,
                            IsDefault = true,
                        },
                    };
            }

            return models
                .Select(model => new InvoiceEmailSenderAccountDto
                {
                    Id = model.Id,
                    Name = string.IsNullOrWhiteSpace(model.Name) ? model.FromEmail : model.Name,
                    FromEmail = model.FromEmail,
                    FromName = model.FromName,
                    IsDefault = model.IsDefault,
                })
                .ToList();
        }

        public async Task<InvoiceEmailOptions> BuildTransientOptionsAsync(
            TestInvoiceEmailSettingsDto request,
            CancellationToken cancellationToken = default
        )
        {
            var requestPassword = NormalizeOptional(request.Password);
            var password = request.ClearPassword
                ? null
                : requestPassword;
            if (!request.ClearPassword && password == null)
            {
                var accountId = NormalizeOptional(request.Id);
                var existing = accountId == null
                    ? null
                    : await QueryModelAsync(accountId);

                if (existing != null)
                {
                    // 测试已有账号且密码留空时复用该账号存储密码，不回退到其他默认账号密码。
                    password = ReadStoredPassword(existing.EncryptedPassword);
                }
                else if (IsDefaultAccountId(accountId) && !await HasSavedAccountsAsync())
                {
                    // 数据库尚未落库时，页面显示的是 appsettings fallback 默认账号，测试邮件沿用该密码。
                    password = _fallbackOptions.Password;
                }
            }

            return new InvoiceEmailOptions
            {
                Host = request.Host.Trim(),
                Port = request.Port,
                UseSsl = request.UseSsl,
                CheckCertificateRevocation = request.CheckCertificateRevocation,
                Username = NormalizeOptional(request.Username),
                Password = password,
                FromEmail = request.FromEmail.Trim(),
                FromName = NormalizeOptional(request.FromName),
                MaxAttachmentBytes = request.MaxAttachmentBytes,
            };
        }

        private async Task<List<InvoiceEmailConfiguration>> QueryModelsAsync()
        {
            var models = await _context.Db.Queryable<InvoiceEmailConfiguration>()
                .ToListAsync();

            return models
                .OrderByDescending(item => item.IsDefault)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToList();
        }

        private async Task<InvoiceEmailConfiguration?> QueryModelAsync(string id)
        {
            return await _context.Db.Queryable<InvoiceEmailConfiguration>()
                .Where(item => item.Id == id)
                .FirstAsync();
        }

        private string? ResolveStoredPassword(
            InvoiceEmailConfiguration? existing,
            UpdateInvoiceEmailAccountDto request,
            bool allowFallbackPassword = false
        )
        {
            if (request.ClearPassword)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                if (!string.IsNullOrWhiteSpace(existing?.EncryptedPassword))
                {
                    // 密码留空表示沿用同库里已有的密码；明文保存时不再绑定本机 DataProtection key。
                    return existing.EncryptedPassword;
                }

                if (allowFallbackPassword && !string.IsNullOrWhiteSpace(_fallbackOptions.Password))
                {
                    return _fallbackOptions.Password;
                }

                return null;
            }

            return request.Password;
        }

        private async Task<bool> HasSavedAccountsAsync()
        {
            return await _context.Db.Queryable<InvoiceEmailConfiguration>().CountAsync() > 0;
        }

        private string? ReadStoredPassword(string? storedPassword)
        {
            if (string.IsNullOrWhiteSpace(storedPassword))
            {
                return null;
            }

            // 兼容旧版 DataProtection 密文；新版按用户要求直接明文读取数据库密码。
            if (!storedPassword.StartsWith(DataProtectionPayloadPrefix, StringComparison.Ordinal))
            {
                return storedPassword;
            }

            try
            {
                return _protector.Unprotect(storedPassword);
            }
            catch
            {
                // 旧密文解不开时按明文处理，避免 CfDJ8 前缀的真实明文密码被误判。
                return storedPassword;
            }
        }

        private static InvoiceEmailSettingsDto ToDto(InvoiceEmailOptions options) => new()
        {
            Accounts = new List<InvoiceEmailAccountDto>
            {
                new()
                {
                    Id = InvoiceEmailConfiguration.DefaultId,
                    Name = "默认发件账号",
                    Host = options.Host,
                    Port = options.Port,
                    UseSsl = options.UseSsl,
                    CheckCertificateRevocation = options.CheckCertificateRevocation,
                    Username = options.Username,
                    HasPassword = !string.IsNullOrWhiteSpace(options.Password),
                    FromEmail = options.FromEmail,
                    FromName = options.FromName,
                    MaxAttachmentBytes = options.MaxAttachmentBytes,
                    IsDefault = true,
                },
            },
        };

        private static InvoiceEmailSettingsDto ToDto(IEnumerable<InvoiceEmailConfiguration> models) => new()
        {
            Accounts = models
                .OrderByDescending(item => item.IsDefault)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .Select(ToDto)
                .ToList(),
        };

        private static InvoiceEmailAccountDto ToDto(InvoiceEmailConfiguration model) => new()
        {
            Id = model.Id,
            Name = string.IsNullOrWhiteSpace(model.Name) ? model.FromEmail : model.Name,
            Host = model.Host,
            Port = model.Port,
            UseSsl = model.UseSsl,
            CheckCertificateRevocation = model.CheckCertificateRevocation,
            Username = model.Username,
            HasPassword = !string.IsNullOrWhiteSpace(model.EncryptedPassword),
            FromEmail = model.FromEmail,
            FromName = model.FromName,
            MaxAttachmentBytes = model.MaxAttachmentBytes,
            IsDefault = model.IsDefault,
            UpdatedAtUtc = model.UpdatedAtUtc,
            UpdatedBy = model.UpdatedBy,
        };

        private InvoiceEmailOptions ToOptions(InvoiceEmailConfiguration model) => new()
        {
            Host = model.Host,
            Port = model.Port,
            UseSsl = model.UseSsl,
            CheckCertificateRevocation = model.CheckCertificateRevocation,
            Username = model.Username,
            Password = ReadStoredPassword(model.EncryptedPassword),
            FromEmail = model.FromEmail,
            FromName = model.FromName,
            MaxAttachmentBytes = model.MaxAttachmentBytes,
        };

        private static InvoiceEmailOptions CloneOptions(InvoiceEmailOptions options) => new()
        {
            Host = options.Host,
            Port = options.Port,
            UseSsl = options.UseSsl,
            CheckCertificateRevocation = options.CheckCertificateRevocation,
            Username = options.Username,
            Password = options.Password,
            FromEmail = options.FromEmail,
            FromName = options.FromName,
            MaxAttachmentBytes = options.MaxAttachmentBytes,
        };

        private static string? NormalizeOptional(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private static string CreateAccountId() => Guid.NewGuid().ToString("N");

        private static bool IsDefaultAccountId(string? accountId)
        {
            return string.Equals(
                accountId,
                InvoiceEmailConfiguration.DefaultId,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static (string Message, string Code)? ValidateSettingsRequest(
            UpdateInvoiceEmailSettingsDto request
        )
        {
            if (request.Accounts == null || request.Accounts.Count == 0)
            {
                return ("至少需要配置一个发件邮箱账号", "INVOICE_EMAIL_ACCOUNT_REQUIRED");
            }

            if (request.Accounts.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            {
                return ("发件账号名称不能为空", "INVOICE_EMAIL_ACCOUNT_NAME_REQUIRED");
            }

            var defaultCount = request.Accounts.Count(item => item.IsDefault);
            if (defaultCount != 1)
            {
                return ("必须且只能设置一个默认发件邮箱账号", "INVOICE_EMAIL_DEFAULT_ACCOUNT_REQUIRED");
            }

            return null;
        }
    }

    public sealed class InvoiceEmailPasswordDecryptException : Exception
    {
        public InvoiceEmailPasswordDecryptException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class InvoiceEmailAccountNotFoundException : Exception
    {
        public InvoiceEmailAccountNotFoundException(string message)
            : base(message)
        {
        }
    }

    public sealed class InvoiceEmailDefaultAccountException : Exception
    {
        public InvoiceEmailDefaultAccountException(string message)
            : base(message)
        {
        }
    }
}
