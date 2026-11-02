using System.Globalization;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

// MYOB EmployeePayrollDetails「Wage 节」只读探针（临时接口，核对完字段后整体删除）：
// 总方案只在文档里写过 EmployeePayrollDetails.Wage.HourlyRate，从没对过真实响应。要「从 MYOB 取 base rate」，
// 先确认真实响应里时薪放在哪个字段、年薪制员工长什么样，再写正式解析，避免重蹈「字段名没核对」的覆辙。
//
//   GET /api/react/v1/myob/employee-payroll/wage-probe?connectionGuid=&employeeDisplayId=   Myob.Connection.Manage
//
// 隐私约束（都在这里硬性实现，不靠自觉）：
// 1. 只读：只发 GET Contact/EmployeePayrollDetails，不写 MYOB，不写我方数据库；
// 2. 响应只含「字段路径、值类型、出现次数」和时薪的汇总统计（条数 / 为 0 的条数 / 最小 / 中位 / 最大），
//    以及 PayBasis / PayFrequency 这类枚举值的计数；不含任何姓名、UID、年薪或个人金额；
// 3. 唯一的个人值：请求里显式给了 employeeDisplayId 时，返回这一张卡的 PayBasis / PayFrequency / HourlyRate，
//    用来和 MYOB 界面里同一员工的「Hourly Rate」对一眼；年薪永远不返回。

public static class MyobWageProbeErrorCodes
{
    public const string InvalidRequest = "MYOB_WAGE_PROBE_INVALID_REQUEST";
}

/// <summary>读 MYOB 原始 JSON 的客户端（只给探针用，放在独立接口里，不影响 <see cref="IMyobApiClient"/> 的实现者）。</summary>
public interface IMyobRawReadClient
{
    /// <summary>GET /{cf_uri}/Contact/EmployeePayrollDetails 全量翻页，原样返回每条 JSON。</summary>
    Task<IReadOnlyList<JsonElement>> GetEmployeePayrollDetailsRawAsync(
        string businessId, string accessToken, CancellationToken cancellationToken);
}

/// <summary>一个字段路径的统计：数组元素用 [] 表示，如 Wage.WageCategories[].Name。</summary>
public sealed class MyobWageProbeFieldDto
{
    public string Path { get; set; } = string.Empty;

    /// <summary>出现过的 JSON 类型（Number / String / Object / Array / True / False / Null）。</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>有多少张卡出现了这个路径。</summary>
    public int Present { get; set; }

    /// <summary>其中值不是 null 的卡数。</summary>
    public int NonNull { get; set; }
}

/// <summary>名为 HourlyRate 的数字字段的汇总统计（不含个人值）。</summary>
public sealed class MyobWageProbeRateStatsDto
{
    public string Path { get; set; } = string.Empty;
    public int Count { get; set; }
    public int ZeroCount { get; set; }
    public decimal? Min { get; set; }
    public decimal? Median { get; set; }
    public decimal? Max { get; set; }
}

/// <summary>请求里显式指定的那一张卡的时薪相关值。</summary>
public sealed class MyobWageProbeCardDto
{
    public string DisplayId { get; set; } = string.Empty;

    /// <summary>名册里按 Employee ID 找到、且 EmployeePayrollDetails 里也有这张卡。</summary>
    public bool Found { get; set; }

    public string? PayBasis { get; set; }
    public string? PayFrequency { get; set; }
    public decimal? HourlyRate { get; set; }
}

public sealed class MyobWageProbeDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public int CardCount { get; set; }

    /// <summary>有 Wage 对象的卡数。</summary>
    public int CardsWithWage { get; set; }

    public List<MyobWageProbeFieldDto> Fields { get; set; } = [];

    /// <summary>PayBasis / PayFrequency 的取值与卡数（路径 → 值 → 卡数）。</summary>
    public Dictionary<string, Dictionary<string, int>> EnumValues { get; set; } = [];

    public List<MyobWageProbeRateStatsDto> RateStats { get; set; } = [];
    public MyobWageProbeCardDto? Card { get; set; }

    public string Note { get; set; } =
        "只读探针：只含字段路径、类型与计数、时薪汇总统计；不含姓名、UID、年薪。字段核对完后整个接口删除。";
}

public interface IMyobWageProbeService
{
    Task<ApiResponse<MyobWageProbeDto>> RunAsync(
        string connectionGuid, string? employeeDisplayId, CancellationToken cancellationToken);
}

internal sealed class MyobWageProbeService : IMyobWageProbeService
{
    private readonly ISqlSugarClient _db;
    private readonly IMyobRawReadClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMyobEmployeeDirectory _directory;

    public MyobWageProbeService(
        SqlSugarContext context,
        IMyobRawReadClient client,
        IMyobTokenProvider tokens,
        IMyobEmployeeDirectory directory)
        : this(context.Db, client, tokens, directory)
    {
    }

    internal MyobWageProbeService(
        ISqlSugarClient db,
        IMyobRawReadClient client,
        IMyobTokenProvider tokens,
        IMyobEmployeeDirectory directory)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _directory = directory;
    }

    public async Task<ApiResponse<MyobWageProbeDto>> RunAsync(
        string connectionGuid, string? employeeDisplayId, CancellationToken cancellationToken)
    {
        var guid = connectionGuid?.Trim() ?? string.Empty;
        if (guid.Length == 0)
        {
            return ApiResponse<MyobWageProbeDto>.Error("缺少 connectionGuid", MyobWageProbeErrorCodes.InvalidRequest);
        }

        var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == guid);
        if (connection is null)
        {
            return ApiResponse<MyobWageProbeDto>.Error("MYOB 连接不存在", MyobErrorCodes.ConnectionNotFound);
        }

        if (connection.Status != MyobConnectionStatuses.Active)
        {
            return ApiResponse<MyobWageProbeDto>.Error("MYOB 连接未启用", MyobErrorCodes.ConnectionNotActive);
        }

        IReadOnlyList<JsonElement> cards;
        try
        {
            var token = await _tokens.GetAccessTokenAsync(guid, cancellationToken);
            cards = await _client.GetEmployeePayrollDetailsRawAsync(connection.BusinessId, token, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobWageProbeDto>.Error(ex.Message, ex.Code);
        }

        var result = MyobWageProbeAnalyzer.Analyze(cards);
        result.ConnectionGuid = connection.ConnectionGuid;
        result.ConnectionName = connection.BusinessName;

        var displayId = employeeDisplayId?.Trim();
        if (!string.IsNullOrEmpty(displayId))
        {
            try
            {
                var directory = await _directory.GetAsync(guid, false, cancellationToken);
                var uid = directory.Cards
                    .FirstOrDefault(card => string.Equals(card.Dto.DisplayId, displayId, StringComparison.OrdinalIgnoreCase))
                    ?.Dto.Uid;
                result.Card = MyobWageProbeAnalyzer.PickCard(cards, uid, displayId);
            }
            catch (MyobApiException ex)
            {
                return ApiResponse<MyobWageProbeDto>.Error(ex.Message, ex.Code);
            }
        }

        return ApiResponse<MyobWageProbeDto>.OK(result);
    }
}

/// <summary>把一批 EmployeePayrollDetails 原始 JSON 汇总成「字段结构 + 时薪统计」（纯函数，无 IO）。</summary>
internal static class MyobWageProbeAnalyzer
{
    private const int MaxDepth = 6;
    private const int MaxPaths = 300;
    private const int MaxEnumValues = 12;

    internal static MyobWageProbeDto Analyze(IReadOnlyList<JsonElement> cards)
    {
        var present = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonNull = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var enums = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var rates = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        var withWage = 0;

        foreach (var card in cards)
        {
            if (card.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (HasObject(card, "Wage"))
            {
                withWage++;
            }

            // 同一张卡里数组会让同一路径出现多次：路径出现次数、非空次数按「每卡一次」算。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenNonNull = new HashSet<string>(StringComparer.Ordinal);
            Walk(card, string.Empty, 0, (path, value) =>
            {
                if (!kinds.ContainsKey(path) && kinds.Count >= MaxPaths)
                {
                    return;
                }

                if (!kinds.TryGetValue(path, out var set))
                {
                    kinds[path] = set = [];
                }

                set.Add(value.ValueKind.ToString());
                seen.Add(path);
                if (value.ValueKind != JsonValueKind.Null)
                {
                    seenNonNull.Add(path);
                }

                var leaf = Leaf(path);
                if (value.ValueKind == JsonValueKind.String && IsEnumLeaf(leaf))
                {
                    var text = Clip(value.GetString());
                    if (!enums.TryGetValue(path, out var counts))
                    {
                        enums[path] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    }

                    if (counts.ContainsKey(text) || counts.Count < MaxEnumValues)
                    {
                        counts[text] = counts.GetValueOrDefault(text) + 1;
                    }
                }
                else if (IsRateLeaf(leaf) && TryNumber(value, out var number))
                {
                    if (!rates.TryGetValue(path, out var list))
                    {
                        rates[path] = list = [];
                    }

                    list.Add(number);
                }
            });

            foreach (var path in seen)
            {
                present[path] = present.GetValueOrDefault(path) + 1;
            }

            foreach (var path in seenNonNull)
            {
                nonNull[path] = nonNull.GetValueOrDefault(path) + 1;
            }
        }

        var dto = new MyobWageProbeDto
        {
            CardCount = cards.Count,
            CardsWithWage = withWage,
            Fields = kinds
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new MyobWageProbeFieldDto
                {
                    Path = item.Key,
                    Kinds = item.Value.ToList(),
                    Present = present.GetValueOrDefault(item.Key),
                    NonNull = nonNull.GetValueOrDefault(item.Key),
                })
                .ToList(),
            EnumValues = enums
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value),
            RateStats = rates
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => ToStats(item.Key, item.Value))
                .ToList(),
        };
        return dto;
    }

    /// <summary>按员工 UID 取那一张卡的 Wage.PayBasis / PayFrequency / HourlyRate；找不到返回 Found=false。</summary>
    internal static MyobWageProbeCardDto PickCard(IReadOnlyList<JsonElement> cards, string? employeeUid, string displayId)
    {
        var result = new MyobWageProbeCardDto { DisplayId = displayId };
        if (string.IsNullOrWhiteSpace(employeeUid))
        {
            return result;
        }

        foreach (var card in cards)
        {
            if (card.ValueKind != JsonValueKind.Object
                || !TryGet(card, "Employee", out var employee)
                || employee.ValueKind != JsonValueKind.Object
                || !TryGet(employee, "UID", out var uid)
                || uid.ValueKind != JsonValueKind.String
                || !string.Equals(uid.GetString(), employeeUid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Found = true;
            if (TryGet(card, "Wage", out var wage) && wage.ValueKind == JsonValueKind.Object)
            {
                result.PayBasis = TryGet(wage, "PayBasis", out var basis) && basis.ValueKind == JsonValueKind.String
                    ? Clip(basis.GetString()) : null;
                result.PayFrequency = TryGet(wage, "PayFrequency", out var frequency) && frequency.ValueKind == JsonValueKind.String
                    ? Clip(frequency.GetString()) : null;
                result.HourlyRate = TryGet(wage, "HourlyRate", out var rate) && TryNumber(rate, out var number) ? number : null;
            }

            break;
        }

        return result;
    }

    private static void Walk(JsonElement element, string path, int depth, Action<string, JsonElement> visit)
    {
        if (path.Length > 0)
        {
            visit(path, element);
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, visit);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[]", depth + 1, visit);
                }

                break;
        }
    }

    private static MyobWageProbeRateStatsDto ToStats(string path, List<decimal> values)
    {
        var sorted = values.OrderBy(item => item).ToList();
        return new MyobWageProbeRateStatsDto
        {
            Path = path,
            Count = sorted.Count,
            ZeroCount = sorted.Count(item => item == 0m),
            Min = sorted.Count == 0 ? null : sorted[0],
            Median = sorted.Count == 0 ? null : sorted[sorted.Count / 2],
            Max = sorted.Count == 0 ? null : sorted[^1],
        };
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasObject(JsonElement card, string name) =>
        TryGet(card, name, out var value) && value.ValueKind == JsonValueKind.Object;

    // MYOB 个别数字字段会以带引号的字符串出现，所以字符串也尝试按数字读。
    private static bool TryNumber(JsonElement value, out decimal number)
    {
        number = 0m;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }

    private static string Leaf(string path)
    {
        var dot = path.LastIndexOf('.');
        var leaf = dot < 0 ? path : path[(dot + 1)..];
        return leaf.TrimEnd('[', ']');
    }

    private static bool IsEnumLeaf(string leaf) =>
        leaf.Equals("PayBasis", StringComparison.OrdinalIgnoreCase)
        || leaf.Equals("PayFrequency", StringComparison.OrdinalIgnoreCase);

    private static bool IsRateLeaf(string leaf) => leaf.Equals("HourlyRate", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= 40 ? text : text[..40];
    }
}
