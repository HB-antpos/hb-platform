using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.Planogram;

/// <summary>收集字段校验错误，最后一次性返回，避免前端一个一个改。</summary>
internal sealed class PlanogramFieldErrors
{
    private readonly List<PlanogramFieldError> _items = new();

    public bool Any => _items.Count > 0;

    public void Add(string field, string message) => _items.Add(new PlanogramFieldError(field, message));

    /// <summary>必填文本：去空白后不能为空且不超长。</summary>
    public string RequiredText(string field, string label, string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            Add(field, $"{label}不能为空");
        }
        else if (text.Length > maxLength)
        {
            Add(field, $"{label}不能超过 {maxLength} 个字符");
        }

        return text;
    }

    /// <summary>可选文本：空白视为空，超长报错。</summary>
    public string? OptionalText(string field, string label, string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            Add(field, $"{label}不能超过 {maxLength} 个字符");
        }

        return text;
    }

    /// <summary>整数范围 [min, max]。</summary>
    public void IntRange(string field, string label, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            Add(field, $"{label}必须在 {min}～{max} 之间");
        }
    }

    /// <summary>
    /// 小数范围 [min, max]，同时把值四舍五入到 2 位小数（库里是 decimal(6,2)）。
    /// 返回四舍五入后的值，供调用方写入。
    /// </summary>
    public decimal DecimalRange(string field, string label, decimal value, decimal min, decimal max)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded < min || rounded > max)
        {
            Add(field, $"{label}必须在 {min:0.##}～{max:0.##} 之间");
        }

        return rounded;
    }

    /// <summary>
    /// 换一套字段命名后返回新的收集器：导入文件的字段名与保存请求不同（如 <c>shelfModelCode</c> / <c>plan.bayCount</c>），
    /// 复用保存校验逻辑的同时让错误字段指向导入文件里的真实位置。
    /// </summary>
    public PlanogramFieldErrors Remap(Func<string, string> map)
    {
        var mapped = new PlanogramFieldErrors();
        foreach (var item in _items)
        {
            mapped.Add(map(item.Field), item.Message);
        }

        return mapped;
    }

    public ApiResponse<T> ToFailure<T>()
    {
        var message = "输入不合法：" + string.Join("；", _items.Select(item => item.Message));
        return ApiResponse<T>.Error(message, PlanogramErrorCodes.Validation, _items.ToList());
    }
}
