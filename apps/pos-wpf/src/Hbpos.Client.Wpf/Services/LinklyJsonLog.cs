using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

internal enum LinklyLogLevel
{
    Information,
    Warning,
    Error
}

internal static class LinklyJsonLog
{
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "accessToken",
        "authorization",
        "secret",
        "password",
        "pairCode",
        "track2",
        "trackData",
        "cvv",
        "cvc",
        "pan",
        "cardNumber",
        "maskedCardNumber"
    };

    private static readonly HashSet<string> BusinessReferencePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "txnRef",
        "rrn",
        "retrievalReference",
        "stan",
        "trace",
        "invoice",
        "invoiceNumber",
        "batch",
        "batchNumber",
        "refundReference"
    };

    private static readonly Regex CardNumberTextRegex = new(
        @"(?i)\b(?:card\s*(?:number|no)?|pan)\s*[:=]\s*(?<value>(?:\d[ \t\u00A0.\-]*){11,18}\d)",
        RegexOptions.Compiled);
    private static readonly Regex PotentialPanRegex = new(
        @"(?<![A-Za-z0-9])(?:\d[ \t\u00A0.\-]*){12,18}\d(?![A-Za-z0-9])",
        RegexOptions.Compiled);
    private static readonly Regex TrackDataRegex = new(
        @"(?i)\b(?:track\s*2|trackdata)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled);
    private static readonly Regex CredentialTextRegex = new(
        @"(?i)\b(?:bearer\s+)?(?:token|secret|password|pair\s*code|authorization)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled);
    private static readonly Regex CvvTextRegex = new(
        @"(?i)\b(?:cvv|cvc)\s*[:=]\s*\d+",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Build(
        string source,
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        int? httpStatus = null,
        bool? success = null,
        string? reason = null,
        long? elapsedMs = null,
        object? request = null,
        object? response = null,
        object? details = null)
    {
        // 在统一 JSON 外形前递归脱敏请求、响应和详情，诊断日志不得保留认证材料或完整卡号。
        return JsonSerializer.Serialize(
            new LinklyLogEvent(
                source,
                operation,
                phase,
                direction,
                environment?.ToString(),
                sessionId,
                httpStatus,
                success,
                reason,
                elapsedMs,
                SanitizeForLog(request),
                SanitizeForLog(response),
                SanitizeForLog(details)),
            JsonOptions);
    }

    public static void Write(
        string category,
        string source,
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        HttpStatusCode? httpStatus = null,
        bool? success = null,
        string? reason = null,
        long? elapsedMs = null,
        object? request = null,
        object? response = null,
        object? details = null,
        Exception? exception = null,
        LinklyLogLevel? level = null)
    {
        var statusCode = httpStatus.HasValue ? (int)httpStatus.Value : (int?)null;
        var message = Build(
            source,
            operation,
            phase,
            direction,
            environment,
            sessionId,
            statusCode,
            success,
            reason,
            elapsedMs,
            request,
            response,
            details);
        // sessionId 进 TraceId、HTTP 状态进 StatusCode，中心日志才能按会话串联并按级别筛出支付/结算失败。
        var context = new ApplicationLogContext(
            TraceId: string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>
            {
                ["source"] = source,
                ["operation"] = operation,
                ["phase"] = phase,
                ["elapsedMs"] = elapsedMs
            });
        switch (level ?? ResolveLevel(phase, success))
        {
            case LinklyLogLevel.Error:
                ConsoleLog.WriteError(category, message, context, exception);
                break;
            case LinklyLogLevel.Warning:
                ConsoleLog.WriteWarning(category, message, context, exception);
                break;
            default:
                ConsoleLog.WriteInformation(category, message, context);
                break;
        }
    }

    private static readonly HashSet<string> FailurePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed",
        "unknown",
        "error",
        "timeout",
        "rejected"
    };

    /// <summary>
    /// 未显式指定级别时：明确失败或结果未知升为 Warning，其余保持 Information，避免正常轮询快照刷成告警。
    /// </summary>
    internal static LinklyLogLevel ResolveLevel(string? phase, bool? success)
    {
        return success == false || (!string.IsNullOrWhiteSpace(phase) && FailurePhases.Contains(phase))
            ? LinklyLogLevel.Warning
            : LinklyLogLevel.Information;
    }

    public static void WriteMessage(string category, string source, string message)
    {
        var operation = InferOperation(message);
        var phase = InferPhase(message);
        Write(
            category,
            source,
            operation,
            phase,
            details: new
            {
                message
            });
    }

    public static object? SanitizeForLog(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return SanitizeElement(JsonSerializer.SerializeToElement(value, JsonOptions), propertyName: null);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return SanitizeText(value.ToString() ?? string.Empty);
        }
    }

    private static object? SanitizeElement(JsonElement value, string? propertyName)
    {
        if (!string.IsNullOrWhiteSpace(propertyName) && SensitivePropertyNames.Contains(propertyName))
        {
            return IsCardNumberProperty(propertyName)
                ? MaskCardNumberForLog(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
                : "<redacted>";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => SanitizeElement(property.Value, property.Name),
                StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(item => SanitizeElement(item, propertyName: null))
                .ToArray(),
            JsonValueKind.String => SanitizeText(value.GetString() ?? string.Empty, propertyName),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsCardNumberProperty(string propertyName)
    {
        return string.Equals(propertyName, "pan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "cardNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "maskedCardNumber", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeText(string value, string? propertyName = null)
    {
        var preserveBusinessReference = !string.IsNullOrWhiteSpace(propertyName) &&
            BusinessReferencePropertyNames.Contains(propertyName) &&
            IsSingleBusinessReferenceScalar(value);
        // 结构化的单一业务引用由字段名证明用途；复合文本仍完整走敏感信息清理。
        var sanitizedReceiptText = preserveBusinessReference
            ? value
            : LinklyReceiptTextSanitizer.Sanitize(value);

        var withoutCredentials = CredentialTextRegex.Replace(sanitizedReceiptText, "<redacted>");
        var withoutTrackData = TrackDataRegex.Replace(withoutCredentials, "Track2=<redacted>");
        var withoutCvv = CvvTextRegex.Replace(withoutTrackData, "CVV=<redacted>");
        var withMaskedLabels = CardNumberTextRegex.Replace(
            withoutCvv,
            match => $"CardNumber={MaskCardNumberForLog(match.Groups["value"].Value)}");
        return preserveBusinessReference
            ? withMaskedLabels
            : PotentialPanRegex.Replace(
                withMaskedLabels,
                match => IsLikelyPan(match.Value) ? MaskCardNumberForLog(match.Value) : match.Value);
    }

    public static string MaskCardNumberForLog(string? value)
    {
        return LinklyReceiptTextSanitizer.SanitizeCardNumber(value) ?? "<redacted>";
    }

    private static bool IsSingleBusinessReferenceScalar(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '/' or '-'))
        {
            return true;
        }

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return digits.Length is >= 12 and <= 19 &&
            trimmed.All(character => char.IsDigit(character) || character is ' ' or '\t' or '\u00A0' or '.' or '-');
    }

    private static bool IsLikelyPan(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubleNext = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index] - '0';
            if (doubleNext)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleNext = !doubleNext;
        }

        return sum % 10 == 0;
    }

    private static string InferOperation(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "linkly";
        }

        var trimmed = message.Trim();
        var index = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return index <= 0 ? trimmed : trimmed[..index];
    }

    private static string InferPhase(string message)
    {
        if (message.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return "blocked";
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (message.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "succeeded";
        }

        if (message.Contains("response", StringComparison.OrdinalIgnoreCase))
        {
            return "response";
        }

        if (message.Contains("request", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            return "request";
        }

        return "event";
    }

    private sealed record LinklyLogEvent(
        string Source,
        string Operation,
        string Phase,
        string? Direction,
        string? Environment,
        string? SessionId,
        int? HttpStatus,
        bool? Success,
        string? Reason,
        long? ElapsedMs,
        object? Request,
        object? Response,
        object? Details);
}
