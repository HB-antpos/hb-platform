namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// ABN 规范化与校验。ABN 是 11 位数字，校验算法：第 1 位减 1，按权重 10,1,3,5,7,9,11,13,15,17,19 加权求和，
/// 和能被 89 整除即有效。分店资料里的 ABN 是自由文本（可能带空格 / 分隔符），统一只保留数字再比较。
/// </summary>
public static class MyobAbn
{
    private static readonly int[] Weights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];

    /// <summary>只保留数字；没有数字返回 null。</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>11 位数字且通过校验算法。</summary>
    public static bool IsValid(string? digits)
    {
        if (digits is null || digits.Length != 11 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 11; i++)
        {
            var digit = digits[i] - '0';
            if (i == 0)
            {
                digit -= 1;
            }

            sum += digit * Weights[i];
        }

        return sum % 89 == 0;
    }
}
