using System.Runtime.CompilerServices;
using BlazorApp.Api.Services;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesStatisticsProductStoreDailyArchitectureContractTests
{
    private const string FacadePath =
        "services/backend/BlazorApp.Api/Services/Hangfire/SalesStatisticsJobService.cs";
    private const string SalesStatisticsFeaturePath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics";
    private const string CompatibilityAdapterPath =
        "services/backend/BlazorApp.Api/Services/Hangfire/SalesStatisticsCompatibilityAdapter.cs";
    private const string ContractsPath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/Common/SalesStatisticsContracts.cs";
    private const string CompatibilityContractsPath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/Common/SalesStatisticsCompatibilityContracts.cs";
    private const string RefreshSlicePath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/SalesStatisticsProductStoreDailyRefreshSlice.cs";
    private const string SupportSlicePath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/SalesStatisticsProductStoreDailySupportSlice.cs";
    private const string StateSlicePath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/SalesStatisticsProductStoreDailyStateSlice.cs";
    private const string SupplierStoreSlicePath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/Supplier/SalesStatisticsSupplierStoreSlice.cs";
    private const string SourceReaderPath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/Infrastructure/SalesStatisticsProductStoreDailySourceReader.cs";
    private const string BuilderPath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/Domain/SalesStatisticsProductStoreDailyBuilder.cs";
    private const string CommandWriterPath =
        "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/Infrastructure/SalesStatisticsProductStoreDailyCommandWriter.cs";

    [Fact]
    public void 销售统计切片不得依赖兼容门面或应用协调器()
    {
        var featureRoot = Path.Combine(FindRepoRoot(), SalesStatisticsFeaturePath);
        string[] forbiddenDependencies =
        [
            "SalesStatisticsJobService",
            "ISalesStatisticsOperations",
            "SalesStatisticsApplicationCoordinator",
        ];
        var violations = Directory
            .EnumerateFiles(featureRoot, "*Slice.cs", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = Path.GetRelativePath(FindRepoRoot(), path),
                Dependencies = forbiddenDependencies
                    .Where(File.ReadAllText(path).Contains)
                    .ToArray(),
            })
            .Where(item => item.Dependencies.Length > 0)
            .Select(item => $"{item.Path}: {string.Join(", ", item.Dependencies)}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Slice 不得通过门面或应用协调器回调形成依赖环：\n" + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 兼容门面必须只委派且不得承载映射事务或ORM()
    {
        var facade = ReadRepoFile(FacadePath);
        string[] forbiddenTokens =
        [
            ": ISalesStatisticsOperations",
            ", ISalesStatisticsOperations",
            ".GroupBy(",
            ".Select(",
            ".Queryable<",
            ".Fastest<",
            ".Insertable(",
            ".Updateable(",
            ".Deleteable<",
            "BeginTran",
            "CommitTran",
            "RollbackTran",
        ];
        var violations = forbiddenTokens.Where(facade.Contains).ToArray();

        Assert.True(
            violations.Length == 0,
            "兼容门面仍承载 operations、DTO 映射、事务或 ORM：\n" + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 兼容结果类型必须归属销售统计模块而不是门面文件()
    {
        var facade = ReadRepoFile(FacadePath);
        Assert.DoesNotContain("class BatchStatisticsUpdateResult", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("class FullRefreshRangeExecutionResult", facade, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class ProductStoreDailyRecalculationSubmitResult",
            facade,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("class DateRange", facade, StringComparison.Ordinal);

        var contracts = ReadRepoFile(CompatibilityContractsPath);
        Assert.Contains("class BatchStatisticsUpdateResult", contracts, StringComparison.Ordinal);
        Assert.Contains("class FullRefreshRangeExecutionResult", contracts, StringComparison.Ordinal);
        Assert.Contains(
            "class ProductStoreDailyRecalculationSubmitResult",
            contracts,
            StringComparison.Ordinal
        );
        Assert.Contains("class DateRange", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void 兼容快照必须共享冻结源并使用按需只读视图()
    {
        var adapterPath = Path.Combine(FindRepoRoot(), CompatibilityAdapterPath);
        Assert.True(File.Exists(adapterPath), $"缺少独立兼容适配器：{CompatibilityAdapterPath}");

        var adapter = File.ReadAllText(adapterPath);
        var contracts = ReadRepoFile(ContractsPath);
        Assert.Contains("MappedReadOnlyList<", adapter, StringComparison.Ordinal);
        Assert.Contains("Array.AsReadOnly", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Select(ToLegacyProductStoreDailySourceRow).ToList()",
            adapter,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            ".Select(ToLegacyProductStoreDailySourceRow).ToArray()",
            adapter,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            ".Select(ToCanonicalProductStoreDailySourceRow).ToList()",
            adapter,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            ".Select(ToCanonicalProductStoreDailySourceRow).ToArray()",
            adapter,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void 兼容适配器不得反向引用门面静态owner()
    {
        var adapter = ReadRepoFile(CompatibilityAdapterPath);
        string[] forbiddenOwnerReferences =
        [
            "SalesStatisticsJobService",
            "using Legacy =",
            "Legacy.",
        ];
        var violations = forbiddenOwnerReferences.Where(adapter.Contains).ToArray();

        Assert.True(
            violations.Length == 0,
            "兼容适配器仍通过 legacy nested DTO 反向引用 façade owner：\n"
                + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 销售统计切片构造依赖图不得成环()
    {
        var sliceTypes = typeof(SalesStatisticsJobService).Assembly
            .GetTypes()
            .Where(type =>
                type.Namespace == "BlazorApp.Api.Services"
                && type.Name.StartsWith("SalesStatistics", StringComparison.Ordinal)
                && type.Name.EndsWith("Slice", StringComparison.Ordinal)
                && !type.IsAbstract
            )
            .ToHashSet();
        var dependencies = sliceTypes.ToDictionary(
            type => type,
            type => type
                .GetConstructors(
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                )
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .Where(sliceTypes.Contains)
                .Distinct()
                .ToArray()
        );
        var visiting = new HashSet<Type>();
        var visited = new HashSet<Type>();
        var path = new Stack<Type>();

        foreach (var type in sliceTypes)
            Visit(type);

        void Visit(Type type)
        {
            if (visited.Contains(type))
                return;
            if (!visiting.Add(type))
            {
                var cycle = path
                    .Reverse()
                    .SkipWhile(item => item != type)
                    .Append(type)
                    .Select(item => item.Name);
                Assert.Fail("销售统计切片存在构造依赖环：" + string.Join(" -> ", cycle));
            }

            path.Push(type);
            foreach (var dependency in dependencies[type])
                Visit(dependency);
            path.Pop();
            visiting.Remove(type);
            visited.Add(type);
        }
    }

    [Fact]
    public void 销售统计源码不得用using_static隐藏跨切片调用方向()
    {
        var featureRoot = Path.Combine(FindRepoRoot(), SalesStatisticsFeaturePath);
        var violations = Directory
            .EnumerateFiles(featureRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new
                {
                    Path = Path.GetRelativePath(FindRepoRoot(), path),
                    Line = index + 1,
                    Text = line.Trim(),
                }))
            .Where(item => item.Text.StartsWith(
                "using static BlazorApp.Api.Services.SalesStatistics",
                StringComparison.Ordinal
            ))
            .Select(item => $"{item.Path}:{item.Line}: {item.Text}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "SalesStatistics 不得以 using static 隐藏切片间调用边：\n" + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 销售统计已知反向调用必须迁到窄组件()
    {
        var forbiddenDependencies = new Dictionary<string, string[]>
        {
            [SupplierStoreSlicePath] = ["SalesStatisticsDailyHourlySlice"],
            [SupportSlicePath] = ["SalesStatisticsProductStoreDailyRefreshSlice"],
            [StateSlicePath] = ["SalesStatisticsProductStoreDailyRefreshSlice"],
            [RefreshSlicePath] = ["SalesStatisticsProductStoreDailyEntrySlice"],
            [SourceReaderPath] = ["SalesStatisticsProductStoreDailyRefreshSlice"],
            [BuilderPath] =
            [
                "SalesStatisticsProductStoreDailyRefreshSlice",
                "SalesStatisticsProductStoreDailySupportSlice",
                "SalesStatisticsProductStoreDailyStateSlice",
                "ProductStoreDaily.Infrastructure",
            ],
            [CommandWriterPath] = ["SalesStatisticsDailyHourlySlice"],
        };
        var violations = forbiddenDependencies
            .SelectMany(rule => rule.Value
                .Where(ReadRepoFile(rule.Key).Contains)
                .Select(dependency => $"{rule.Key}: {dependency}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "SalesStatistics 仍存在已确认的反向调用边：\n" + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 销售统计切片源码不得存在双向显式依赖()
    {
        var featureRoot = Path.Combine(FindRepoRoot(), SalesStatisticsFeaturePath);
        var sliceFiles = Directory
            .EnumerateFiles(featureRoot, "*Slice.cs", SearchOption.AllDirectories)
            .ToArray();
        var sliceNames = sliceFiles.ToDictionary(
            path => path,
            path => Path.GetFileNameWithoutExtension(path)
        );
        var dependencies = sliceFiles.ToDictionary(
            path => path,
            path => sliceFiles
                .Where(candidate => candidate != path)
                .Where(candidate => File.ReadAllText(path).Contains(
                    sliceNames[candidate],
                    StringComparison.Ordinal
                ))
                .ToHashSet()
        );
        var reciprocalPairs = sliceFiles
            .SelectMany(path => dependencies[path]
                .Where(dependency => dependencies[dependency].Contains(path))
                .Select(dependency => new[] { sliceNames[path], sliceNames[dependency] }
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()))
            .DistinctBy(pair => string.Join("|", pair))
            .Select(pair => string.Join(" <-> ", pair))
            .ToArray();

        Assert.True(
            reciprocalPairs.Length == 0,
            "SalesStatistics 仍存在跨切片双向显式依赖：\n" + string.Join("\n", reciprocalPairs)
        );
    }

    [Fact]
    public void 商品分店日统计主用例必须只负责编排()
    {
        var body = ExtractMethodBody(
            ReadRepoFile(RefreshSlicePath),
            "UpdateProductStoreDailyStatisticsWithContext"
        );
        var lines = body.Count(character => character == '\n') + 1;
        string[] forbiddenTokens =
        [
            ".Queryable<",
            ".Fastest<",
            ".Deleteable<",
            "BeginTran",
            "CommitTran",
            "RollbackTran",
            ".GroupBy(",
            "ResolveStatisticAmount(",
            "ResolveUnitCost(",
            "new ProductStoreDailySalesStatistic",
        ];
        var violations = forbiddenTokens.Where(body.Contains).ToArray();

        Assert.True(lines <= 180, $"主用例编排方法共 {lines} 行，必须不超过 180 行。");
        Assert.True(
            violations.Length == 0,
            "主用例仍混合查询、事务、业务规则或实体映射：\n" + string.Join("\n", violations)
        );
    }

    [Fact]
    public void 商品分店日统计必须具备来源领域和事务写入边界()
    {
        string[] requiredPaths =
        [
            SourceReaderPath,
            BuilderPath,
            CommandWriterPath,
        ];
        var missing = requiredPaths
            .Where(path => !File.Exists(Path.Combine(FindRepoRoot(), path)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "商品分店日统计缺少明确的来源、领域或事务写入边界：\n" + string.Join("\n", missing)
        );
    }

    [Fact]
    public void 商品分店日统计边界必须各自保持职责()
    {
        var sourceReader = ReadRepoFile(SourceReaderPath);
        var builder = ReadRepoFile(BuilderPath);
        var commandWriter = ReadRepoFile(CommandWriterPath);

        Assert.Contains(".Queryable<", sourceReader);
        Assert.DoesNotContain(".Queryable<", builder);
        Assert.DoesNotContain(".Fastest<", builder);
        Assert.DoesNotContain(".Deleteable<", builder);
        Assert.DoesNotContain("BeginTran", builder);
        Assert.DoesNotContain("CommitTran", builder);
        Assert.DoesNotContain("RollbackTran", builder);
        Assert.Equal(1, CountOccurrences(commandWriter, "BeginTranAsync"));
        Assert.Equal(1, CountOccurrences(commandWriter, "CommitTranAsync"));
        Assert.Equal(1, CountOccurrences(commandWriter, "RollbackTranAsync"));
    }

    /// <summary>
    /// 金额/数量统计必须按净额并入退款单（Status=3），统一走 <see cref="SalesOrderStatusRules.NetSalesStatuses"/>。
    /// 仍写死「Status == 1 || Status == 4」的只允许是数订单/客数、补充退货、已知的手动导入入口；
    /// 新增写死处会让该统计回到毛额，与日结小票对不上，须在此登记理由。
    /// </summary>
    [Fact]
    public void 写死订单状态1或4的查询只允许订单数_补充退货_手动导入()
    {
        var apiRoot = Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api");
        var allowed = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // 订单数/客数：退款单不算一单
            ["Features/SalesStatistics/DailyHourly/SalesStatisticsHourlyRefresher.cs"] = 1,
            ["Features/SalesStatistics/DailyHourly/SalesStatisticsDailyHourlySlice.cs"] = 2,
            ["Features/SalesStatistics/ProductStoreDaily/SalesStatisticsProductStoreDailySupportSlice.cs"] = 1,
            // 补充退货（sales_return_record）与退款单自带的负数明细 GUID 不重合，并入 3 会重复冲减；
            // 另含 2025 快照的订单行（订单数口径）
            ["Features/SalesStatistics/ProductStoreDaily/Infrastructure/SalesStatisticsProductStoreDailySourceQueries.cs"] = 2,
            ["Services/React/BatchProductSalesDiscountSnapshotSourceReader.cs"] = 3,
            // 手动「导入 2025 全年」入口：自带一套按明细金额的口径且订单数按单号计，未并入退款单
            ["Services/HBSalesRecordStatisticsService.cs"] = 1,
        };
        var pattern = new System.Text.RegularExpressions.Regex(
            @"Status == 1 \|\| \w+\.Status == 4");
        var actual = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (
                Path: Path.GetRelativePath(apiRoot, path).Replace('\\', '/'),
                Count: pattern.Matches(File.ReadAllText(path)).Count))
            .Where(item => item.Count > 0)
            .ToDictionary(item => item.Path, item => item.Count, StringComparer.Ordinal);

        Assert.Equal(allowed.OrderBy(x => x.Key), actual.OrderBy(x => x.Key));

        // 折扣事实聚合的原生 SQL：补充退货只认 1/4，销售明细与支付分母认 1/3/4。
        var factReader = File.ReadAllText(Path.Combine(apiRoot, "Services/React/BatchProductSalesAnalysisFactReader.cs"));
        Assert.Equal(1, CountOccurrences(factReader, "[Status] IN (1, 4)"));
        Assert.Equal(2, CountOccurrences(factReader, "[Status] IN (1, 3, 4)"));
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"未找到方法 {methodName}");
        var openBrace = source.IndexOf('{', methodIndex);
        Assert.True(openBrace >= 0, $"方法 {methodName} 没有方法体");

        var depth = 0;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;
        var verbatimString = false;
        var escaped = false;
        for (var index = openBrace; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                    inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (inString)
            {
                if (verbatimString && current == '"' && next == '"')
                {
                    index++;
                    continue;
                }
                if ((!verbatimString && escaped) || current == '\\')
                {
                    escaped = !escaped;
                    continue;
                }
                if (current == '"' && !escaped)
                    inString = false;
                escaped = false;
                continue;
            }
            if (inChar)
            {
                if (escaped)
                    escaped = false;
                else if (current == '\\')
                    escaped = true;
                else if (current == '\'')
                    inChar = false;
                continue;
            }
            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                verbatimString = index > 0 && source[index - 1] == '@';
                escaped = false;
                continue;
            }
            if (current == '\'')
            {
                inChar = true;
                escaped = false;
                continue;
            }
            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
                return source.Substring(openBrace, index - openBrace + 1);
        }

        throw new InvalidOperationException($"方法 {methodName} 的方法体未闭合。");
    }

    private static string FindRepoRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位仓库根目录。");
    }
}
