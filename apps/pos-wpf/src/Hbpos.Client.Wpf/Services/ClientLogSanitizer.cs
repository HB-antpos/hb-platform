using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hbpos.Client.Wpf.Services;

internal static partial class ClientLogSanitizer
{
    private const string Redacted = "[REDACTED]";
    private const int MaximumDepth = 8;
    private const int MaximumArrayItems = 1_000;
    private const int MaximumTextLength = 8_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "bearer", "token", "accesstoken", "refreshtoken", "password", "pin",
        "secret", "apikey", "credential", "authorizationcode", "pan", "cardnumber", "cvv",
        "vouchercode", "employeebarcode", "customeremail", "customerphone", "customeraddress",
        "requestbody", "responsebody", "rawrequest", "rawresponse", "headers", "cookies", "setcookie"
    };
    private static readonly HashSet<string> IdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "productcode", "itemnumber", "referencecode", "lookupcode", "cashierid", "userguid",
        "userid", "storecode", "devicecode", "instanceid", "orderguid", "receiptnumber",
        "correlationid", "traceid", "clienteventid", "eventid"
    };

    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions);
        SanitizeNode(node, 0, null);
        return node?.ToJsonString(JsonOptions) ?? "null";
    }

    private static void SanitizeNode(JsonNode? node, int depth, string? propertyName)
    {
        if (node is null)
        {
            return;
        }

        if (depth >= MaximumDepth)
        {
            node.ReplaceWith(JsonValue.Create("[TRUNCATED]"));
            return;
        }

        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSensitiveKey(property.Key))
                {
                    jsonObject[property.Key] = Redacted;
                    continue;
                }

                SanitizeNode(property.Value, depth + 1, property.Key);
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            while (!string.Equals(propertyName, "items", StringComparison.OrdinalIgnoreCase) &&
                   jsonArray.Count > MaximumArrayItems)
            {
                jsonArray.RemoveAt(jsonArray.Count - 1);
            }

            foreach (var item in jsonArray)
            {
                SanitizeNode(item, depth + 1, propertyName);
            }

            return;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            node.ReplaceWith(JsonValue.Create(SanitizeText(
                text,
                propertyName is not null && IdentifierKeys.Contains(NormalizeKey(propertyName)))));
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = NormalizeKey(key);
        return SensitiveKeys.Contains(normalized) ||
               normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("customer", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string key) => KeySeparatorRegex().Replace(key, string.Empty);

    private static string SanitizeText(string value, bool preserveNumericIdentifier)
    {
        var result = UrlQueryRegex().Replace(value, static match =>
        {
            var url = match.Value;
            var queryIndex = url.IndexOf('?');
            return queryIndex < 0 ? url : url[..queryIndex];
        });
        result = RelativeUrlQueryRegex().Replace(result, static match =>
        {
            var path = match.Value;
            var queryIndex = path.IndexOf('?');
            return queryIndex < 0 ? path : path[..queryIndex];
        });
        result = BearerRegex().Replace(result, "$1[REDACTED]");
        result = InlineSecretRegex().Replace(result, "$1[REDACTED]");
        if (!preserveNumericIdentifier)
        {
            result = PanRegex().Replace(result, "[REDACTED_CARD]");
        }

        return result.Length <= MaximumTextLength ? result : result[..MaximumTextLength];
    }

    [GeneratedRegex("[_\\-.\\s]", RegexOptions.CultureInvariant)]
    private static partial Regex KeySeparatorRegex();

    [GeneratedRegex("https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])/[^\\s\\\"'<>?]*\\?[^\\s\\\"'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeUrlQueryRegex();

    [GeneratedRegex("(?i)(Bearer\\s+)[^\\s,;\\\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:authorization|password|pin|secret|api[_-]?key|token|credential|authorization[_-]?code|pan|cvv|voucher(?:[_-]?code)?|employee[_-]?barcode)\\s*[:=]\\s*)[^\\s,;\\\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSecretRegex();

    [GeneratedRegex("(?<!\\d)(?:\\d[ -]?){13,19}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PanRegex();
}
