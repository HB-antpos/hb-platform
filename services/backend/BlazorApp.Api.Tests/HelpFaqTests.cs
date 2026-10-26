using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Services.HelpFaq;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 使用帮助 FAQ P1：种子导入、公开读服务（SQLite）、控制器匿名元数据与 ETag/304。
/// 迁移协调器的账本 / 诊断码断言在 SchemaMigrationCoordinatorTests，SQL Server 集成在 SchemaMigrationSqlServerIntegrationTests。
/// </summary>
public sealed class HelpFaqTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 1, 2, 3, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly SqlSugarClient _db;

    public HelpFaqTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"help-faq-{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        _db.CodeFirst.InitTables(typeof(HelpFaqGroup), typeof(HelpFaqQuestion));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 临时库清理失败不影响断言结果。
        }
    }

    // ===================== 种子导入 =====================

    [Fact]
    public void 嵌入资源种子_解析出11组43问且字段完整()
    {
        var plan = HelpFaqSeedImporter.Parse(HelpFaqSeedImporter.ReadEmbeddedSeedJson(), Now);

        Assert.Equal(11, plan.Groups.Count);
        Assert.Equal(6, plan.Groups.Count(g => g.Tab == HelpFaqTabs.Pos));
        Assert.Equal(5, plan.Groups.Count(g => g.Tab == HelpFaqTabs.Mobile));
        Assert.Equal(43, plan.Questions.Count);

        var groupGuids = plan.Groups.Select(g => g.GroupGUID).ToHashSet();
        Assert.Equal(11, groupGuids.Count);
        Assert.Equal(43, plan.Questions.Select(q => q.QuestionGUID).Distinct().Count());
        // Code 即锚点：全局唯一。
        Assert.Equal(43, plan.Questions.Select(q => q.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var group in plan.Groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Code));
            Assert.False(string.IsNullOrWhiteSpace(group.TitleZh));
            Assert.False(string.IsNullOrWhiteSpace(group.TitleEn));
            Assert.Equal(HelpFaqImporterActor, group.CreatedBy);
            Assert.Equal(HelpFaqImporterActor, group.UpdatedBy);
            Assert.False(group.IsDeleted);
        }

        foreach (var question in plan.Questions)
        {
            Assert.Contains(question.GroupGUID, groupGuids);
            Assert.False(string.IsNullOrWhiteSpace(question.TitleZh));
            Assert.False(string.IsNullOrWhiteSpace(question.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(question.WhereZh));
            Assert.False(string.IsNullOrWhiteSpace(question.WhereEn));

            // 步骤是 JSON 数组，中英数量一致且非空。
            var stepsZh = HelpFaqRowMapper.ParseStrings(question.StepsZh);
            var stepsEn = HelpFaqRowMapper.ParseStrings(question.StepsEn);
            Assert.NotEmpty(stepsZh);
            Assert.Equal(stepsZh.Count, stepsEn.Count);

            Assert.Equal(HelpFaqStatuses.Published, question.Status);
            Assert.Equal(Now, question.PublishedAt);
            Assert.Equal(1, question.Version);
            Assert.Null(question.DraftJson);
            Assert.Equal(HelpFaqImporterActor, question.CreatedBy);
            Assert.Equal(HelpFaqImporterActor, question.UpdatedBy);
            Assert.False(question.IsDeleted);
        }
    }

    private const string HelpFaqImporterActor = "system-seed";

    [Fact]
    public void 嵌入资源种子_排序为数组下标乘10且按种子顺序()
    {
        var plan = HelpFaqSeedImporter.Parse(HelpFaqSeedImporter.ReadEmbeddedSeedJson(), Now);

        foreach (var tab in HelpFaqTabs.All)
        {
            var tabGroups = plan.Groups.Where(g => g.Tab == tab).ToList();
            Assert.Equal(
                Enumerable.Range(0, tabGroups.Count).Select(i => i * 10),
                tabGroups.Select(g => g.SortOrder)
            );
        }

        foreach (var group in plan.Groups)
        {
            var rows = plan.Questions.Where(q => q.GroupGUID == group.GroupGUID).ToList();
            Assert.Equal(
                Enumerable.Range(0, rows.Count).Select(i => i * 10),
                rows.Select(q => q.SortOrder)
            );
        }
    }

    [Fact]
    public void 嵌入资源与磁盘种子文件内容一致()
    {
        var onDisk = File.ReadAllText(FindRepoFile("services/backend/BlazorApp.Api/Data/SeedData/help-faq-seed.json"));
        var embedded = HelpFaqSeedImporter.ReadEmbeddedSeedJson();

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(onDisk), JsonNode.Parse(embedded)));
    }

    [Fact]
    public void 种子_可选块缺省存NULL_存在则中英两列都写()
    {
        var plan = HelpFaqSeedImporter.Parse(HelpFaqSeedImporter.ReadEmbeddedSeedJson(), Now);

        foreach (var question in plan.Questions)
        {
            Assert.Equal(question.TipsZh is null, question.TipsEn is null);
            Assert.Equal(question.RulesZh is null, question.RulesEn is null);
            Assert.Equal(question.RelatedZh is null, question.RelatedEn is null);
        }

        Assert.Contains(plan.Questions, q => q.TipsZh is null);
        Assert.Contains(plan.Questions, q => q.TipsZh is not null);
        Assert.Contains(plan.Questions, q => q.RelatedZh is not null);
        Assert.Contains(plan.Questions, q => q.Permissions is not null);
        // 中文以可读形式存库，便于直接在库里排查。
        Assert.DoesNotContain("\\u", plan.Questions[0].StepsZh, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":2,"tabs":{"pos":[],"mobile":[]}}""", "version")]
    [InlineData("""{"version":1}""", "tabs")]
    public void 种子_版本或结构不合法时抛异常(string json, string expectedFragment)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(json, Now));
        Assert.Contains(expectedFragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 种子_问题Code跨Tab重复时抛异常()
    {
        var json = SeedJson(
            pos: [Group("g1", Question("same")), Group("g2", Question("other"))],
            mobile: [Group("m1", Question("same"))]
        );
        var exception = Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(json, Now));
        Assert.Contains("same", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 种子_同Tab分组Code重复_步骤中英数量不一致_Code超长都抛异常()
    {
        var duplicateGroup = SeedJson(pos: [Group("g", Question("a")), Group("g", Question("b"))], mobile: []);
        Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(duplicateGroup, Now));

        var mismatchedSteps = SeedJson(
            pos: [Group("g", Question("a", stepsZh: ["一", "二"], stepsEn: ["one"]))],
            mobile: []
        );
        Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(mismatchedSteps, Now));

        var tooLong = SeedJson(pos: [Group("g", Question(new string('x', 97)))], mobile: []);
        Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(tooLong, Now));

        var emptyCode = SeedJson(pos: [Group("g", Question(" "))], mobile: []);
        Assert.Throws<InvalidOperationException>(() => HelpFaqSeedImporter.Parse(emptyCode, Now));
    }

    // ===================== 公开读服务 =====================

    [Fact]
    public async Task 公开服务_种子入库后读出的tabs与种子文件结构等价()
    {
        var seedJson = HelpFaqSeedImporter.ReadEmbeddedSeedJson();
        var plan = HelpFaqSeedImporter.Parse(seedJson, Now);
        await InsertAsync(plan);
        var service = CreateService();

        var snapshot = await service.GetPublishedAsync();

        // 用与线上一致的序列化选项（camelCase + 省略 null + IgnoreCycles）输出整个响应。
        var response = JsonSerializer.SerializeToNode(
            ApiResponse<HelpFaqPublicDto>.OK(snapshot.Data),
            ProductionJsonOptions
        )!;
        var data = response["data"]!.AsObject();

        Assert.Equal(new[] { "tabs", "version" }, data.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.String, data["version"]!.GetValueKind());
        Assert.Equal(snapshot.Data.Version, data["version"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{16}$", snapshot.Data.Version);
        Assert.Equal($"\"{snapshot.Data.Version}\"", snapshot.ETag);

        // tabs 与种子文件深比较（JsonNode.DeepEquals 不关心键顺序），包含「缺省字段不输出」。
        var expectedTabs = JsonNode.Parse(seedJson)!["tabs"]!;
        Assert.True(
            JsonNode.DeepEquals(expectedTabs, data["tabs"]),
            "公开接口 tabs 与种子文件结构不一致。"
        );
    }

    [Fact]
    public async Task 公开服务_只返回已发布且未删除_不泄露草稿()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g1", "分组一", "Group one", 0);
        var published = AddQuestion(group, "published", HelpFaqStatuses.Published, 0);
        // 已发布题带一份待发布草稿：公开接口只能看到线上字段，看不到草稿内容。
        published.DraftJson = """{"id":"published","title":{"zh":"DRAFT-SECRET-TITLE","en":"x"}}""";
        published.TitleZh = "线上标题";
        var neverPublished = AddQuestion(group, "draft-only", HelpFaqStatuses.Draft, 10);
        neverPublished.DraftJson = "DRAFT-SECRET-BODY";
        var deleted = AddQuestion(group, "deleted", HelpFaqStatuses.Published, 20);
        deleted.IsDeleted = true;
        await InsertAsync(group, published, neverPublished, deleted);

        var snapshot = await CreateService().GetPublishedAsync();

        var onlyGroup = Assert.Single(snapshot.Data.Tabs.Pos);
        var onlyQuestion = Assert.Single(onlyGroup.Questions);
        Assert.Equal("published", onlyQuestion.Id);
        Assert.Equal("线上标题", onlyQuestion.Title.Zh);
        Assert.Empty(snapshot.Data.Tabs.Mobile);

        var wholeJson = JsonSerializer.Serialize(snapshot.Data, ProductionJsonOptions);
        Assert.DoesNotContain("DRAFT-SECRET", wholeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("draft-only", wholeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("draftJson", wholeJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 公开DTO_类型上没有任何可输出草稿的属性()
    {
        var dtoTypes = typeof(HelpFaqPublicDto).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(HelpFaqPublicDto).Namespace && t.Name.StartsWith("HelpFaq", StringComparison.Ordinal));

        var propertyNames = dtoTypes.SelectMany(t => t.GetProperties()).Select(p => p.Name).ToList();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Draft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 公开服务_分组与题按SortOrder排序_并列按Code_无已发布题的分组整组省略()
    {
        var late = AddGroup(HelpFaqTabs.Pos, "late", "后", "Late", 20);
        var early = AddGroup(HelpFaqTabs.Pos, "early", "前", "Early", 0);
        var emptyGroup = AddGroup(HelpFaqTabs.Pos, "empty", "空", "Empty", 5);
        var onlyDraft = AddGroup(HelpFaqTabs.Pos, "only-draft", "草稿组", "Draft group", 6);
        var deletedGroup = AddGroup(HelpFaqTabs.Pos, "deleted-group", "已删", "Deleted", 7);
        deletedGroup.IsDeleted = true;
        var tieA = AddGroup(HelpFaqTabs.Pos, "tie-a", "并列A", "Tie A", 30);
        var tieB = AddGroup(HelpFaqTabs.Pos, "tie-b", "并列B", "Tie B", 30);

        var rows = new[]
        {
            AddQuestion(late, "late-1", HelpFaqStatuses.Published, 0),
            AddQuestion(early, "early-second", HelpFaqStatuses.Published, 20),
            AddQuestion(early, "early-first", HelpFaqStatuses.Published, 10),
            AddQuestion(early, "early-tie-b", HelpFaqStatuses.Published, 30),
            AddQuestion(early, "early-tie-a", HelpFaqStatuses.Published, 30),
            AddQuestion(onlyDraft, "d1", HelpFaqStatuses.Draft, 0),
            AddQuestion(deletedGroup, "in-deleted-group", HelpFaqStatuses.Published, 0),
            AddQuestion(tieB, "tb", HelpFaqStatuses.Published, 0),
            AddQuestion(tieA, "ta", HelpFaqStatuses.Published, 0),
        };
        await InsertGroupsAsync(late, early, emptyGroup, onlyDraft, deletedGroup, tieA, tieB);
        await InsertQuestionsAsync(rows);

        var snapshot = await CreateService().GetPublishedAsync();

        Assert.Equal(new[] { "early", "late", "tie-a", "tie-b" }, snapshot.Data.Tabs.Pos.Select(g => g.Id));
        Assert.Equal(
            new[] { "early-first", "early-second", "early-tie-a", "early-tie-b" },
            snapshot.Data.Tabs.Pos[0].Questions.Select(q => q.Id)
        );
    }

    [Fact]
    public async Task 公开服务_只认pos与mobile两个Tab_未知Tab被忽略()
    {
        var pos = AddGroup(HelpFaqTabs.Pos, "p", "收银", "POS", 0);
        var mobile = AddGroup(HelpFaqTabs.Mobile, "m", "移动", "Mobile", 0);
        var alien = AddGroup("web", "w", "网页", "Web", 0);
        await InsertGroupsAsync(pos, mobile, alien);
        await InsertQuestionsAsync(
            AddQuestion(pos, "qp", HelpFaqStatuses.Published, 0),
            AddQuestion(mobile, "qm", HelpFaqStatuses.Published, 0),
            AddQuestion(alien, "qw", HelpFaqStatuses.Published, 0)
        );

        var snapshot = await CreateService().GetPublishedAsync();

        Assert.Equal("qp", Assert.Single(Assert.Single(snapshot.Data.Tabs.Pos).Questions).Id);
        Assert.Equal("qm", Assert.Single(Assert.Single(snapshot.Data.Tabs.Mobile).Questions).Id);
        Assert.DoesNotContain("qw", JsonSerializer.Serialize(snapshot.Data, ProductionJsonOptions), StringComparison.Ordinal);
    }

    [Fact]
    public void 投影_单题JSON损坏只跳过该题_整页不失败()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g", "组", "G", 0);
        var good = AddQuestion(group, "good", HelpFaqStatuses.Published, 0);
        var bad = AddQuestion(group, "bad", HelpFaqStatuses.Published, 10);
        bad.StepsZh = "{not json";
        var skipped = new List<string>();

        var dto = HelpFaqPublicProjector.Build([group], [good, bad], (row, _) => skipped.Add(row.Code));

        Assert.Equal("good", Assert.Single(Assert.Single(dto.Tabs.Pos).Questions).Id);
        Assert.Equal(new[] { "bad" }, skipped);
    }

    [Fact]
    public void 投影_组内全部题损坏时整组省略()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g", "组", "G", 0);
        var bad = AddQuestion(group, "bad", HelpFaqStatuses.Published, 0);
        bad.StepsEn = "oops";

        var dto = HelpFaqPublicProjector.Build([group], [bad]);

        Assert.Empty(dto.Tabs.Pos);
    }

    [Fact]
    public async Task 版本摘要_内容不变则稳定_任何内容或顺序变化都会改变()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g", "组", "G", 0);
        var q1 = AddQuestion(group, "q1", HelpFaqStatuses.Published, 0);
        var q2 = AddQuestion(group, "q2", HelpFaqStatuses.Published, 10);
        await InsertAsync(group, q1, q2);

        var first = await CreateService(new MemoryCache(new MemoryCacheOptions())).GetPublishedAsync();
        var second = await CreateService(new MemoryCache(new MemoryCacheOptions())).GetPublishedAsync();
        Assert.Equal(first.ETag, second.ETag);

        // 改标题 → 版本变化。
        await _db.Updateable<HelpFaqQuestion>().SetColumns(q => q.TitleEn == "changed").Where(q => q.Code == "q1").ExecuteCommandAsync();
        var edited = await CreateService(new MemoryCache(new MemoryCacheOptions())).GetPublishedAsync();
        Assert.NotEqual(first.ETag, edited.ETag);

        // 调换顺序 → 版本变化。
        await _db.Updateable<HelpFaqQuestion>().SetColumns(q => q.SortOrder == 50).Where(q => q.Code == "q1").ExecuteCommandAsync();
        var reordered = await CreateService(new MemoryCache(new MemoryCacheOptions())).GetPublishedAsync();
        Assert.NotEqual(edited.ETag, reordered.ETag);

        // 新题还是草稿 → 对外不可见 → 版本不变（草稿保存不会让公开缓存失效）。
        var draft = AddQuestion(group, "q3", HelpFaqStatuses.Draft, 20);
        await InsertQuestionsAsync(draft);
        var withDraft = await CreateService(new MemoryCache(new MemoryCacheOptions())).GetPublishedAsync();
        Assert.Equal(reordered.ETag, withDraft.ETag);
    }

    [Fact]
    public async Task 公开服务_命中缓存不再查库_Invalidate后立即读到新内容()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g", "组", "G", 0);
        await InsertAsync(group, AddQuestion(group, "q1", HelpFaqStatuses.Published, 0));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);

        var first = await service.GetPublishedAsync();
        Assert.Single(first.Data.Tabs.Pos[0].Questions);

        // 直改库：缓存未失效前仍返回旧快照（同一对象）。
        await InsertQuestionsAsync(AddQuestion(group, "q2", HelpFaqStatuses.Published, 10));
        var cached = await service.GetPublishedAsync();
        Assert.Same(first, cached);

        service.Invalidate();
        var refreshed = await service.GetPublishedAsync();
        Assert.Equal(2, refreshed.Data.Tabs.Pos[0].Questions.Count);
        Assert.NotEqual(first.ETag, refreshed.ETag);
    }

    [Fact]
    public async Task 公开服务_Invalidate对共享IMemoryCache的其它实例同样生效()
    {
        var group = AddGroup(HelpFaqTabs.Pos, "g", "组", "G", 0);
        await InsertAsync(group, AddQuestion(group, "q1", HelpFaqStatuses.Published, 0));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = CreateService(cache);
        var writer = CreateService(cache);
        await reader.GetPublishedAsync();
        await InsertQuestionsAsync(AddQuestion(group, "q2", HelpFaqStatuses.Published, 10));

        // P2 的写服务与公开读服务是不同的 scoped 实例，靠共享缓存键失效。
        writer.Invalidate();

        Assert.Equal(2, (await reader.GetPublishedAsync()).Data.Tabs.Pos[0].Questions.Count);
    }

    [Fact]
    public async Task 公开服务_空库返回空树而不是报错()
    {
        var snapshot = await CreateService().GetPublishedAsync();

        Assert.Empty(snapshot.Data.Tabs.Pos);
        Assert.Empty(snapshot.Data.Tabs.Mobile);
        Assert.False(string.IsNullOrEmpty(snapshot.Data.Version));
    }

    [Fact]
    public void 行映射_可选块往返无损_仅一侧存在时另一侧补空数组()
    {
        var dto = new HelpFaqQuestionDto
        {
            Id = "rt",
            Title = new HelpFaqTextDto { Zh = "标题 <b> & \"引号\"", En = "Title" },
            Where = new HelpFaqTextDto { Zh = "入口", En = "Where" },
            Steps = new HelpFaqListDto { Zh = ["§ 小标题", "点 [k:Pay|付款]"], En = ["§ Heading", "Tap [k:Pay|付款]"] },
            Tips = new HelpFaqListDto { Zh = [], En = [] },
            Permissions = [],
            Related = new HelpFaqRelatedDto
            {
                Zh = [new HelpFaqRelatedItemDto { Title = "相关", Body = "内容" }],
                En = [new HelpFaqRelatedItemDto { Title = "Related", Body = "Body" }],
            },
        };
        var row = new HelpFaqQuestion();
        HelpFaqRowMapper.ApplyContent(row, dto);

        var roundTrip = HelpFaqRowMapper.ToDto(row);

        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(dto, ProductionJsonOptions),
            JsonSerializer.SerializeToNode(roundTrip, ProductionJsonOptions)));
        Assert.Null(roundTrip.Rules);

        // 只有一侧存在时，读取补齐另一侧为空数组。
        var oneSided = new HelpFaqQuestion { StepsZh = "[\"a\"]", StepsEn = "[\"a\"]", TipsZh = "[\"t\"]" };
        var tips = HelpFaqRowMapper.ToDto(oneSided).Tips!;
        Assert.Equal(new[] { "t" }, tips.Zh);
        Assert.Empty(tips.En);
    }

    // ===================== 控制器 =====================

    [Fact]
    public void 控制器_匿名只读且路由固定()
    {
        var type = typeof(HelpFaqPublicController);

        Assert.True(type.IsDefined(typeof(ApiControllerAttribute), inherit: true));
        Assert.Equal("api/support/faq", type.GetCustomAttribute<RouteAttribute>()!.Template);
        // 类级别不得要求登录，也不得要求任何权限策略。
        Assert.Empty(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        var actions = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsDefined(typeof(NonActionAttribute)))
            .ToList();
        var action = Assert.Single(actions);
        Assert.Equal(nameof(HelpFaqPublicController.Get), action.Name);
        Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        var http = Assert.Single(action.GetCustomAttributes<HttpMethodAttribute>());
        Assert.Equal(new[] { "GET" }, http.HttpMethods);
    }

    [Fact]
    public async Task 控制器_返回ApiResponse并带ETag与CacheControl()
    {
        var snapshot = SnapshotOf("v1");
        var controller = CreateController(snapshot);

        var result = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<HelpFaqPublicDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Same(snapshot.Data, body.Data);
        Assert.Equal("\"v1\"", controller.Response.Headers.ETag.ToString());
        Assert.Equal("public, max-age=60", controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("\"v1\"")]
    [InlineData("W/\"v1\"")]
    [InlineData("*")]
    [InlineData("\"old\", \"v1\"")]
    [InlineData("\"old\" , W/\"v1\" , \"other\"")]
    public async Task 控制器_IfNoneMatch命中时返回304无正文且仍带ETag(string ifNoneMatch)
    {
        var controller = CreateController(SnapshotOf("v1"));
        controller.Request.Headers.IfNoneMatch = ifNoneMatch;

        var result = await controller.Get(CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
        Assert.Equal("\"v1\"", controller.Response.Headers.ETag.ToString());
        Assert.Equal("public, max-age=60", controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("\"v0\"")]
    [InlineData("W/\"v0\"")]
    [InlineData("\"v10\"")]
    [InlineData("v1")]
    [InlineData("")]
    public async Task 控制器_IfNoneMatch未命中时返回200完整内容(string ifNoneMatch)
    {
        var controller = CreateController(SnapshotOf("v1"));
        if (ifNoneMatch.Length > 0)
        {
            controller.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var result = await controller.Get(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    // ===================== 辅助 =====================

    private HelpFaqPublicService CreateService(IMemoryCache? cache = null) =>
        new(_db, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<HelpFaqPublicService>.Instance);

    private async Task InsertAsync(HelpFaqSeedPlan plan)
    {
        await _db.Insertable(plan.Groups.ToList()).ExecuteCommandAsync();
        await _db.Insertable(plan.Questions.ToList()).ExecuteCommandAsync();
    }

    private async Task InsertGroupsAsync(params HelpFaqGroup[] groups) =>
        await _db.Insertable(groups.ToList()).ExecuteCommandAsync();

    private async Task InsertQuestionsAsync(params HelpFaqQuestion[] questions) =>
        await _db.Insertable(questions.ToList()).ExecuteCommandAsync();

    private async Task InsertAsync(HelpFaqGroup group, params HelpFaqQuestion[] questions)
    {
        await InsertGroupsAsync(group);
        await InsertQuestionsAsync(questions);
    }

    private static HelpFaqGroup AddGroup(string tab, string code, string zh, string en, int sort) =>
        new()
        {
            Tab = tab,
            Code = code,
            TitleZh = zh,
            TitleEn = en,
            SortOrder = sort,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private static HelpFaqQuestion AddQuestion(HelpFaqGroup group, string code, string status, int sort) =>
        new()
        {
            GroupGUID = group.GroupGUID,
            Code = code,
            TitleZh = $"题 {code}",
            TitleEn = $"Question {code}",
            WhereZh = "入口",
            WhereEn = "Where",
            StepsZh = HelpFaqRowMapper.SerializeStrings(["一步"]),
            StepsEn = HelpFaqRowMapper.SerializeStrings(["One step"]),
            SortOrder = sort,
            Status = status,
            PublishedAt = status == HelpFaqStatuses.Published ? Now : null,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private static HelpFaqPublicSnapshot SnapshotOf(string version) =>
        new(new HelpFaqPublicDto { Version = version }, $"\"{version}\"");

    private static HelpFaqPublicController CreateController(HelpFaqPublicSnapshot snapshot)
    {
        var service = new Mock<IHelpFaqPublicService>(MockBehavior.Strict);
        service
            .Setup(s => s.GetPublishedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        return new HelpFaqPublicController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // 与 Program.cs 中 AddJsonOptions 一致：camelCase + 省略 null + 忽略循环引用。
    private static readonly JsonSerializerOptions ProductionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件 {relativePath}");
    }

    private static string Question(
        string id,
        string[]? stepsZh = null,
        string[]? stepsEn = null
    ) =>
        JsonSerializer.Serialize(new
        {
            id,
            title = new { zh = "标题", en = "Title" },
            where = new { zh = "入口", en = "Where" },
            steps = new { zh = stepsZh ?? ["一步"], en = stepsEn ?? ["One"] },
        });

    private static string Group(string id, string questionJson) =>
        $$$"""{"id":{{{JsonSerializer.Serialize(id)}}},"title":{"zh":"组","en":"Group"},"questions":[{{{questionJson}}}]}""";

    private static string SeedJson(string[] pos, string[] mobile) =>
        $$$"""{"version":1,"tabs":{"pos":[{{{string.Join(",", pos)}}}],"mobile":[{{{string.Join(",", mobile)}}}]}}""";
}
