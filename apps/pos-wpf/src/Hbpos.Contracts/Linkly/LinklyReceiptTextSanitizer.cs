using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hbpos.Contracts.Linkly;

public static class LinklyReceiptTextSanitizer
{
    private const int SettlementRecordLength = 69;

    private static readonly Regex FullPanRegex = new(
        @"(?<!\d)(?:\d[ \t\u00A0.\-]*){11,18}\d(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReferenceValueRegex = new(
        @"\b(?:TXN\s*REF|RRN|RETRIEVAL\s*REF(?:ERENCE)?|STAN|TRACE(?:\s*NO)?|INVOICE(?:\s*NO)?|INV\s*NO|BATCH(?:\s*(?:NO|NUMBER))?)\b\s*[:#=\-]?\s*(?<value>(?:\d[ \t\u00A0.\-]*){11,18}\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PureBusinessReferenceValueRegex = new(
        @"^\s*(?:\d[ \t\u00A0.\-]*){11,18}\d\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MaskedPanRegex = new(
        @"(?<![\dA-Za-z])(?:[\dXx*][ \t\u00A0.\-]*){0,15}(?:[Xx*][ \t\u00A0.\-]*){2,18}\d{4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CardFieldRegex = new(
        @"(?<label>\b(?:card\s*number|masked\s*card\s*number|pan|account\s*number)\b\s*[:=]\s*)(?<value>[^\r\n,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveTextFieldRegex = new(
        @"(?im)^\s*(?:track\s*2|track\s*data|encrypted\s*track|cvv|(?:(?:access|refresh|bearer|authorization)[ \t]+token|[\w\-]*token)|authorization)\b\s*[:=].*(?:\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveInlineFieldRegex = new(
        @"(?i)(?<![\w-])(?:(?:access|refresh|bearer|authorization)[ \t]+token|[\w-]*token|authorization|track[ \t]*2|track[ \t]*data|encrypted[ \t]*track|cvv\d*|cvc\d*)\b[ \t]*[:=][ \t]*[^,;\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var withoutSensitiveFields = SensitiveTextFieldRegex.Replace(text, string.Empty);
        var withoutInlineSensitiveFields = SensitiveInlineFieldRegex.Replace(withoutSensitiveFields, string.Empty);
        var withCanonicalCardFields = CardFieldRegex.Replace(withoutInlineSensitiveFields, match =>
        {
            var canonical = SanitizeCardNumber(match.Groups["value"].Value);
            return canonical is null ? string.Empty : match.Groups["label"].Value + canonical;
        });
        var referenceValues = ReferenceValueRegex.Matches(withCanonicalCardFields)
            .Cast<Match>()
            .Select(match => match.Groups["value"])
            .Where(group => group.Success)
            .ToArray();

        var masked = FullPanRegex.Replace(withCanonicalCardFields, match =>
        {
            if (referenceValues.Any(reference =>
                    match.Index == reference.Index && match.Length == reference.Length))
            {
                // 保留所有已标记的业务参考号；同行其他 PAN 仍必须脱敏。
                return match.Value;
            }

            return SanitizeCardNumber(match.Value) ?? string.Empty;
        });

        return MaskedPanRegex.Replace(masked, match => SanitizeCardNumber(match.Value) ?? string.Empty);
    }

    /// <summary>
    /// 将原始或已掩码的卡号统一收敛为仅保留后四位的安全展示值。
    /// </summary>
    public static string? SanitizeCardNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        var hasMask = value.Any(character => character is '*' or 'X' or 'x');
        return digits.Length == 4 ||
               digits.Length is >= 12 and <= 19 ||
               hasMask && digits.Length >= 4
            ? "****" + digits[^4..]
            : null;
    }

    /// <summary>
    /// 结算扩展数据可能是 JSON 或普通文本；两种形式都不得保留完整 PAN 或敏感认证材料。
    /// </summary>
    public static string? SanitizeSettlementData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        // Linkly 官方结算数据是无分隔符定长格式，普通 PAN 正则会把相邻金额和笔数组合误判为卡号。
        // 按结构重建才能只保留金额/笔数字段，同时继续脱敏卡名和可选文本尾段。
        if (TrySanitizeOfficialFixedWidthSettlement(value, out var fixedWidthSettlement))
        {
            return fixedWidthSettlement;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteSanitizedJsonElement(document.RootElement, writer, propertyName: null);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return Sanitize(value);
        }
    }

    /// <summary>
    /// 仅用于受控结构化日志；返回值始终是 ****1234 形式，无法可靠取得后四位时返回 null。
    /// </summary>
    public static string? FindSanitizedCardNumber(JsonElement payload)
    {
        return FindSanitizedCardNumberCore(payload, propertyName: null);
    }

    public static IReadOnlyList<string> SanitizeReceipts(IEnumerable<string>? receiptTexts)
    {
        return (receiptTexts ?? [])
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => string.Join(
                Environment.NewLine,
                text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(Sanitize)))
            .ToArray();
    }

    private static void WriteSanitizedJsonElement(
        JsonElement element,
        Utf8JsonWriter writer,
        string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var normalizedName = NormalizePropertyName(property.Name);
                    if (IsRemovedProperty(normalizedName))
                    {
                        continue;
                    }

                    if (IsCardNumberProperty(normalizedName))
                    {
                        var cardNumber = SanitizeCardNumber(ReadScalarValue(property.Value));
                        if (cardNumber is not null)
                        {
                            writer.WriteString(property.Name, cardNumber);
                        }

                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitizedJsonElement(property.Value, writer, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitizedJsonElement(item, writer, propertyName: null);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SanitizeJsonStringValue(
                    element.GetString(),
                    NormalizePropertyName(propertyName)));
                break;
            case JsonValueKind.Number:
                // 普通数值可能是 batch、merchant 或 reference，不得仅凭长度误判成卡号。
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static string? FindSanitizedCardNumberCore(JsonElement element, string? propertyName)
    {
        var normalizedName = NormalizePropertyName(propertyName);
        if (IsCardNumberProperty(normalizedName))
        {
            return SanitizeCardNumber(ReadScalarValue(element));
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .Select(property => FindSanitizedCardNumberCore(property.Value, property.Name))
                .FirstOrDefault(value => value is not null),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => FindSanitizedCardNumberCore(item, propertyName: null))
                .FirstOrDefault(value => value is not null),
            _ => null
        };
    }

    private static string? ReadScalarValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    private static string SanitizeJsonStringValue(string? value, string propertyName)
    {
        if (IsBusinessReferenceProperty(propertyName) &&
            PureBusinessReferenceValueRegex.IsMatch(value ?? string.Empty))
        {
            return value ?? string.Empty;
        }

        var trimmed = value?.TrimStart();
        if (!string.IsNullOrEmpty(trimmed) && trimmed[0] is '{' or '[')
        {
            return SanitizeSettlementData(value) ?? string.Empty;
        }

        if (propertyName == "settlementdata")
        {
            return SanitizeSettlementData(value) ?? string.Empty;
        }

        return Sanitize(value);
    }

    private static bool TrySanitizeOfficialFixedWidthSettlement(
        string value,
        out string sanitized)
    {
        sanitized = string.Empty;
        if (value.Length < 12 + 3 + SettlementRecordLength
            || !TryReadUnsigned(value.AsSpan(0, 9), out var cardCount)
            || !TryReadUnsigned(value.AsSpan(9, 3), out var cardDataLength)
            || cardCount > 999 / SettlementRecordLength
            || cardDataLength != cardCount * SettlementRecordLength)
        {
            return false;
        }

        var offset = 12;
        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, 12);
        for (var index = 0; index < cardCount; index++)
        {
            if (value.Length - offset < SettlementRecordLength
                || !TrySanitizeSettlementRecord(
                    value.AsSpan(offset, SettlementRecordLength),
                    total: false,
                    out var record))
            {
                return false;
            }

            builder.Append(record);
            offset += SettlementRecordLength;
        }

        if (value.Length - offset < 3 + SettlementRecordLength
            || !TryReadUnsigned(value.AsSpan(offset, 3), out var totalLength)
            || totalLength != SettlementRecordLength
            || !TrySanitizeSettlementRecord(
                value.AsSpan(offset + 3, SettlementRecordLength),
                total: true,
                out var totalRecord))
        {
            return false;
        }

        builder.Append(value, offset, 3);
        builder.Append(totalRecord);
        offset += 3 + SettlementRecordLength;
        if (offset == value.Length)
        {
            sanitized = builder.ToString();
            return true;
        }

        if (value.Length - offset < 3
            || !TryReadUnsigned(value.AsSpan(offset, 3), out var tailLength)
            || tailLength != value.Length - offset - 3)
        {
            return false;
        }

        var tail = value[(offset + 3)..];
        var sanitizedTail = Sanitize(tail);
        builder.Append(sanitizedTail.Length.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(sanitizedTail);
        sanitized = builder.ToString();
        return true;
    }

    private static bool TrySanitizeSettlementRecord(
        ReadOnlySpan<char> record,
        bool total,
        out string sanitized)
    {
        sanitized = string.Empty;
        if (record.Length != SettlementRecordLength)
        {
            return false;
        }

        var name = record[..20].Trim().ToString();
        // 真实终端的汇总行名称是 TOTALS（官方文档写作 TOTAL），符号位非负时是空格；
        // 两者不兼容会让整段定长数据落入通用 PAN 正则，被误掩码成 ****dddd 而无法还原金额。
        if (name.Length == 0
            || (name.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TOTALS", StringComparison.OrdinalIgnoreCase)) != total
            || !AllDigits(record.Slice(20, 36))
            || record[56] is not ('+' or '-' or ' ')
            || !AllDigits(record.Slice(57, 12)))
        {
            return false;
        }

        var sanitizedName = Sanitize(name).Trim();
        if (string.IsNullOrWhiteSpace(sanitizedName)
            || sanitizedName.Length > 20
            || sanitizedName.Any(char.IsControl))
        {
            sanitizedName = "[REDACTED]";
        }

        sanitized = sanitizedName.PadRight(20) + record[20..].ToString();
        return true;
    }

    private static bool TryReadUnsigned(ReadOnlySpan<char> value, out int parsed)
    {
        parsed = 0;
        if (!AllDigits(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            parsed = checked(parsed * 10 + character - '0');
        }

        return true;
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePropertyName(string? propertyName)
    {
        return string.IsNullOrWhiteSpace(propertyName)
            ? string.Empty
            : new string(propertyName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsCardNumberProperty(string propertyName)
    {
        return propertyName is "cardnumber" or "maskedcardnumber" or "pan" or "accountnumber";
    }

    private static bool IsRemovedProperty(string propertyName)
    {
        return propertyName is "track2" or "trackdata" or "encryptedtrack" or "cvv" or "authorization" ||
            propertyName.StartsWith("cvv", StringComparison.Ordinal) ||
            propertyName.Contains("authorization", StringComparison.Ordinal) ||
            propertyName.Contains("token", StringComparison.Ordinal);
    }

    private static bool IsBusinessReferenceProperty(string propertyName)
    {
        return propertyName is
            "txnref" or
            "rrn" or
            "retrievalreference" or
            "retrievalref" or
            "stan" or
            "trace" or
            "traceno" or
            "invoice" or
            "invoiceno" or
            "invno" or
            "batch" or
            "batchnumber";
    }
}
