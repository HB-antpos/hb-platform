using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.StaffNotifications;

internal interface IStaffNotificationTemplateService
{
    Task<IReadOnlyList<StaffNotificationTemplateContent>> ListAsync();

    Task<StaffNotificationTemplateContent?> GetAsync(string templateKey);

    Task<ApiResponse<StaffNotificationTemplateDto>> CreateAsync(StaffNotificationTemplateSaveDto dto, string actor);

    Task<ApiResponse<StaffNotificationTemplateDto>> UpdateAsync(string templateKey, StaffNotificationTemplateSaveDto dto, string actor);

    /// <summary>系统模板恢复默认文案（删掉数据库里的改动行）。</summary>
    Task<ApiResponse<StaffNotificationTemplateDto>> ResetAsync(string templateKey, string actor);
}

/// <summary>
/// 模板读写。系统模板的默认文案在代码里（<see cref="StaffNotificationTemplateDefaults"/>），数据库里有一行才表示被改过；
/// 自建模板只存数据库。校验规则集中在 <see cref="StaffNotificationTemplateValidator"/>，保存与前端提示共用同一套。
/// </summary>
internal sealed class StaffNotificationTemplateService : IStaffNotificationTemplateService
{
    public const string TemplateInvalidCode = "TEMPLATE_INVALID";
    public const string TemplateNotFoundCode = "TEMPLATE_NOT_FOUND";
    public const string TemplateNameDuplicateCode = "TEMPLATE_NAME_DUPLICATE";
    public const string TemplateLimitCode = "TEMPLATE_LIMIT_REACHED";

    private readonly SqlSugarContext _context;

    public StaffNotificationTemplateService(SqlSugarContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<StaffNotificationTemplateContent>> ListAsync()
    {
        var rows = await _context.Db.Queryable<StaffNotificationTemplate>().ToListAsync();
        var result = new List<StaffNotificationTemplateContent>
        {
            MergeSystem(rows.FirstOrDefault(row => row.TemplateKey == StaffNotificationTemplateDefaults.AccountActivatedKey)),
        };
        result.AddRange(rows
            .Where(row => row.TemplateKey != StaffNotificationTemplateDefaults.AccountActivatedKey)
            .OrderBy(row => row.CreatedAtUtc)
            .Select(FromRow));
        return result;
    }

    public async Task<StaffNotificationTemplateContent?> GetAsync(string templateKey)
    {
        if (string.IsNullOrWhiteSpace(templateKey)) return null;
        var key = templateKey.Trim();
        var row = await _context.Db.Queryable<StaffNotificationTemplate>().FirstAsync(item => item.TemplateKey == key);
        if (key == StaffNotificationTemplateDefaults.AccountActivatedKey) return MergeSystem(row);
        return row is null ? null : FromRow(row);
    }

    public async Task<ApiResponse<StaffNotificationTemplateDto>> CreateAsync(StaffNotificationTemplateSaveDto dto, string actor)
    {
        var errors = Validate(dto, issuesPassword: false);
        if (errors.Count > 0) return Invalid(errors);

        var existing = await ListAsync();
        if (existing.Count >= StaffNotificationRules.MaxTemplates)
        {
            return ApiResponse<StaffNotificationTemplateDto>.Error($"模板最多 {StaffNotificationRules.MaxTemplates} 个，请先停用不用的模板", TemplateLimitCode);
        }

        var name = dto.Name.Trim();
        if (IsDuplicateName(existing, name, exceptKey: null))
        {
            return ApiResponse<StaffNotificationTemplateDto>.Error("已有同名模板，请换一个名称", TemplateNameDuplicateCode);
        }

        var now = DateTime.UtcNow;
        var row = new StaffNotificationTemplate
        {
            TemplateKey = Guid.NewGuid().ToString("N"),
            Name = name,
            Kind = StaffNotificationTemplateKinds.General,
            IsEnabled = dto.IsEnabled,
            EmailSubject = dto.EmailSubject.Trim(),
            EmailBody = NormalizeBody(dto.EmailBody),
            SmsBody = NormalizeBody(dto.SmsBody),
            CreatedBy = actor,
            CreatedAtUtc = now,
            UpdatedBy = actor,
            UpdatedAtUtc = now,
        };
        await _context.Db.Insertable(row).ExecuteCommandAsync();
        return ApiResponse<StaffNotificationTemplateDto>.OK(ToDto(FromRow(row)), "模板已创建");
    }

    public async Task<ApiResponse<StaffNotificationTemplateDto>> UpdateAsync(string templateKey, StaffNotificationTemplateSaveDto dto, string actor)
    {
        var current = await GetAsync(templateKey);
        if (current is null) return ApiResponse<StaffNotificationTemplateDto>.Error("模板不存在", TemplateNotFoundCode);

        var errors = Validate(dto, current.IssuesPassword);
        if (errors.Count > 0) return Invalid(errors);

        var db = _context.Db;
        var now = DateTime.UtcNow;
        var key = current.Key;

        // 系统模板的名称固定（发送记录和权限逻辑都按它识别）；自建模板才允许改名，且不能与其他模板重名。
        string name;
        if (current.IsSystem)
        {
            name = current.Name;
        }
        else
        {
            name = dto.Name.Trim();
            if (IsDuplicateName(await ListAsync(), name, exceptKey: key))
            {
                return ApiResponse<StaffNotificationTemplateDto>.Error("已有同名模板，请换一个名称", TemplateNameDuplicateCode);
            }
        }

        var row = await db.Queryable<StaffNotificationTemplate>().FirstAsync(item => item.TemplateKey == key);
        if (row is null)
        {
            // 系统模板第一次被改：把改动后的完整内容存成一行。
            row = new StaffNotificationTemplate
            {
                TemplateKey = key,
                Name = name,
                Kind = current.Kind,
                CreatedBy = actor,
                CreatedAtUtc = now,
            };
            Apply(row, name, dto, actor, now);
            await db.Insertable(row).ExecuteCommandAsync();
        }
        else
        {
            Apply(row, name, dto, actor, now);
            await db.Updateable(row).ExecuteCommandAsync();
        }

        return ApiResponse<StaffNotificationTemplateDto>.OK(ToDto(current.IsSystem ? MergeSystem(row) : FromRow(row)), "模板已保存");
    }

    public async Task<ApiResponse<StaffNotificationTemplateDto>> ResetAsync(string templateKey, string actor)
    {
        var current = await GetAsync(templateKey);
        if (current is null) return ApiResponse<StaffNotificationTemplateDto>.Error("模板不存在", TemplateNotFoundCode);
        if (!current.IsSystem) return ApiResponse<StaffNotificationTemplateDto>.Error("只有系统模板可以恢复默认文案", TemplateInvalidCode);

        var key = current.Key;
        await _context.Db.Deleteable<StaffNotificationTemplate>().Where(item => item.TemplateKey == key).ExecuteCommandAsync();
        return ApiResponse<StaffNotificationTemplateDto>.OK(ToDto(MergeSystem(null)), "已恢复默认文案");
    }

    private static void Apply(StaffNotificationTemplate row, string name, StaffNotificationTemplateSaveDto dto, string actor, DateTime now)
    {
        row.Name = name;
        row.IsEnabled = dto.IsEnabled;
        row.EmailSubject = dto.EmailSubject.Trim();
        row.EmailBody = NormalizeBody(dto.EmailBody);
        row.SmsBody = NormalizeBody(dto.SmsBody);
        row.UpdatedBy = actor;
        row.UpdatedAtUtc = now;
    }

    private static IReadOnlyList<string> Validate(StaffNotificationTemplateSaveDto dto, bool issuesPassword) =>
        StaffNotificationTemplateValidator.Validate(
            dto?.Name ?? string.Empty,
            dto?.EmailSubject?.Trim() ?? string.Empty,
            NormalizeBody(dto?.EmailBody),
            NormalizeBody(dto?.SmsBody),
            issuesPassword
        );

    private static ApiResponse<StaffNotificationTemplateDto> Invalid(IReadOnlyList<string> errors) =>
        ApiResponse<StaffNotificationTemplateDto>.Error(string.Join("；", errors), TemplateInvalidCode);

    // 统一换行符并去掉首尾空白，避免 Windows 浏览器提交的 \r\n 让字符数与校验对不上。
    private static string NormalizeBody(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Trim();

    private static bool IsDuplicateName(IEnumerable<StaffNotificationTemplateContent> all, string name, string? exceptKey) =>
        all.Any(item => item.Key != exceptKey && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private static StaffNotificationTemplateContent MergeSystem(StaffNotificationTemplate? row)
    {
        var defaults = StaffNotificationTemplateDefaults.AccountActivated;
        return row is null
            ? defaults
            : defaults with
            {
                IsEnabled = row.IsEnabled,
                EmailSubject = row.EmailSubject,
                EmailBody = row.EmailBody,
                SmsBody = row.SmsBody,
                IsCustomized = true,
                UpdatedAtUtc = row.UpdatedAtUtc,
            };
    }

    private static StaffNotificationTemplateContent FromRow(StaffNotificationTemplate row) => new(
        row.TemplateKey,
        row.Name,
        row.Kind,
        string.Empty,
        row.IsEnabled,
        row.EmailSubject,
        row.EmailBody,
        row.SmsBody,
        IsCustomized: row.Kind == StaffNotificationTemplateKinds.System,
        row.UpdatedAtUtc
    );

    internal static StaffNotificationTemplateDto ToDto(StaffNotificationTemplateContent content) => new()
    {
        Key = content.Key,
        Name = content.Name,
        Kind = content.Kind,
        Description = content.Description,
        IsEnabled = content.IsEnabled,
        RequiresEmail = content.RequiresEmail,
        IssuesPassword = content.IssuesPassword,
        EmailSubject = content.EmailSubject,
        EmailBody = content.EmailBody,
        SmsBody = content.SmsBody,
        IsCustomized = content.IsCustomized,
        UpdatedAtUtc = content.UpdatedAtUtc,
    };
}
