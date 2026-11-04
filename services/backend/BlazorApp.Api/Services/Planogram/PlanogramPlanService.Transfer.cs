using System.Text.RegularExpressions;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Planogram;

/// <summary>
/// 批 3（附录 C）：方案 JSON 导出 / 导入。文件里用货架 / 挂钩型号编码和商品货号（不带 guid），便于跨环境搬运。
/// 导入只做「按编码解析 + 换成 guid」，之后整个走保存路径的 <see cref="PrepareAsync"/> 校验与冲突复算，不另写一套规则。
/// </summary>
public sealed partial class PlanogramPlanService
{
    // 校验错误里明细字段名 items[3].hookModelGuid → 导入文件里的真实位置 items[3].hookModelCode。
    private static readonly Regex ItemHookGuidField = new(@"^(items\[\d+\])\.hookModelGuid$", RegexOptions.Compiled);

    // ================================================================ 导出

    public async Task<ApiResponse<PlanogramExportDto>> ExportAsync(string guid, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramExportDto>();
        }

        var shelf = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == header.ShelfModelGuid);
        if (shelf is null)
        {
            return ApiResponse<PlanogramExportDto>.Error("方案引用的货架型号不存在，无法导出", PlanogramErrorCodes.NotFound);
        }

        var items = await LoadItemsAsync(header.Guid);
        var hookRows = await QueryChunkedAsync(items.Select(item => item.HookModelGuid).ToList(),
            async chunk => await _db.Queryable<PlanogramHookModel>().Where(hook => chunk.Contains(hook.Guid)).ToListAsync());
        var hookCodes = hookRows.ToDictionary(hook => hook.Guid, hook => hook.Code, StringComparer.OrdinalIgnoreCase);

        var exported = new List<PlanogramExportItemDto>(items.Count);
        foreach (var item in items)
        {
            if (!hookCodes.TryGetValue(item.HookModelGuid, out var hookCode))
            {
                // 挂钩型号被引用时不允许删除，走到这里只可能是直连库造成的脏数据：明确报错，不导出残缺文件。
                return ApiResponse<PlanogramExportDto>.Error(
                    $"方案引用的挂钩型号「{item.HookModelGuid}」不存在，无法导出", PlanogramErrorCodes.NotFound);
            }

            exported.Add(new PlanogramExportItemDto
            {
                BayIndex = item.BayIndex,
                ProductCode = item.ProductCode,
                HookModelCode = hookCode,
                HoleCol = item.HoleCol,
                HoleRow = item.HoleRow,
                PerHookQty = item.PerHookQty,
            });
        }

        return ApiResponse<PlanogramExportDto>.OK(new PlanogramExportDto
        {
            FormatVersion = PlanogramExportFormat.CurrentVersion,
            ExportedAt = _time.GetUtcNow().UtcDateTime,
            Plan = new PlanogramExportPlanDto
            {
                Name = header.Name,
                Notes = header.Notes,
                BayCount = header.BayCount,
                GapXMm = header.GapXMm,
                GapYMm = header.GapYMm,
            },
            ShelfModelCode = shelf.Code,
            Items = exported,
        });
    }

    // ================================================================ 导入

    public async Task<ApiResponse<PlanogramDetailDto>> ImportAsync(
        PlanogramImportRequest input, string actor, CancellationToken cancellationToken)
    {
        // 1) 文件格式：版本不认识就不再往下看（别的字段含义可能已经变了）。
        var errors = new PlanogramFieldErrors();
        if (input.FormatVersion != PlanogramExportFormat.CurrentVersion)
        {
            errors.Add(
                "formatVersion",
                $"不认识的导出格式版本 {input.FormatVersion}，当前只支持 {PlanogramExportFormat.CurrentVersion}");
            return errors.ToFailure<PlanogramDetailDto>();
        }

        var planPart = input.Plan;
        if (planPart is null)
        {
            errors.Add("plan", "缺少 plan（方案头）");
        }

        var fileItems = input.Items;
        if (fileItems is null)
        {
            errors.Add("items", "缺少 items（没有明细请传空数组）");
        }
        else if (fileItems.Count > MaxItems)
        {
            errors.Add("items", $"一个方案最多 {MaxItems} 条明细，导入文件有 {fileItems.Count} 条");
        }

        var shelfCode = (input.ShelfModelCode ?? string.Empty).Trim();
        if (shelfCode.Length == 0)
        {
            errors.Add("shelfModelCode", "货架型号编码不能为空");
        }

        if (errors.Any || planPart is null || fileItems is null)
        {
            return errors.ToFailure<PlanogramDetailDto>();
        }

        // 2) 按编码解析货架 / 挂钩型号与商品。编码不区分大小写（SQL Server 默认排序规则如此，SQLite 测试库在这里统一）。
        //    解析不到的不一个一个报，凑齐（去重）后一次返回，用户能一次补齐环境里缺的型号 / 商品。
        var unresolved = new List<PlanogramImportUnresolvedDto>();

        var shelves = await _db.Queryable<PlanogramShelfModel>().ToListAsync();
        var shelf = shelves.FirstOrDefault(model => string.Equals(model.Code, shelfCode, StringComparison.OrdinalIgnoreCase));
        if (shelf is null)
        {
            unresolved.Add(new PlanogramImportUnresolvedDto(PlanogramImportKinds.Shelf, shelfCode));
        }

        var hooks = await _db.Queryable<PlanogramHookModel>().ToListAsync();
        var hooksByCode = new Dictionary<string, PlanogramHookModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var hook in hooks)
        {
            hooksByCode.TryAdd(hook.Code, hook);
        }

        var hookCodesInFile = fileItems
            .Select(item => (item?.HookModelCode ?? string.Empty).Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var code in hookCodesInFile.Where(code => !hooksByCode.ContainsKey(code)))
        {
            unresolved.Add(new PlanogramImportUnresolvedDto(PlanogramImportKinds.Hook, code));
        }

        var productCodesInFile = fileItems
            .Select(item => (item?.ProductCode ?? string.Empty).Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var foundProducts = await QueryChunkedAsync(
            productCodesInFile,
            async chunk => await _db.Queryable<Product>()
                .Where(product => chunk.Contains(product.ProductCode!))
                .Select(product => product.ProductCode!)
                .ToListAsync());
        var foundProductSet = new HashSet<string>(foundProducts, StringComparer.OrdinalIgnoreCase);
        foreach (var code in productCodesInFile.Where(code => !foundProductSet.Contains(code)))
        {
            unresolved.Add(new PlanogramImportUnresolvedDto(PlanogramImportKinds.Product, code));
        }

        if (unresolved.Count > 0)
        {
            return ApiResponse<PlanogramDetailDto>.Error(
                $"导入文件里有 {unresolved.Count} 个编码在本环境找不到（货架 / 挂钩型号 / 商品），请先建立或修正后再导入",
                PlanogramErrorCodes.ImportUnresolved,
                unresolved);
        }

        // 3) 换成 guid 的保存请求，交给保存路径同一套校验（节数 / 间距 / 货架启用 / 明细结构 / 商品与挂钩存在 / 上限）。
        //    明细 guid 留空由服务端生成；sortOrder 取文件里的先后顺序。
        var overrideName = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name;
        var request = new PlanogramSaveRequest
        {
            Name = overrideName ?? planPart.Name,
            Notes = planPart.Notes,
            ShelfModelGuid = shelf!.Guid,
            BayCount = planPart.BayCount,
            GapXMm = planPart.GapXMm ?? DefaultGapMm,
            GapYMm = planPart.GapYMm ?? DefaultGapMm,
            Items = fileItems.Select((item, index) =>
            {
                item ??= new PlanogramExportItemDto();
                var hookCode = (item.HookModelCode ?? string.Empty).Trim();
                return new PlanogramItemDto
                {
                    Guid = string.Empty,
                    BayIndex = item.BayIndex,
                    Face = 0,
                    ItemType = 1,
                    ProductCode = item.ProductCode ?? string.Empty,
                    HookModelGuid = hooksByCode.TryGetValue(hookCode, out var hook) ? hook.Guid : string.Empty,
                    HoleCol = item.HoleCol,
                    HoleRow = item.HoleRow,
                    PerHookQty = item.PerHookQty,
                    SortOrder = index,
                };
            }).ToList(),
        };

        var planGuid = UuidHelper.GenerateUuid7();
        var outcome = await PrepareAsync(request, planGuid, currentShelfGuid: null, requireName: true);
        if (outcome.Errors.Any || outcome.Plan is null)
        {
            return outcome.Errors.Remap(field => ToImportFileField(field, overrideName is not null)).ToFailure<PlanogramDetailDto>();
        }

        // 4) 复算冲突后新建草稿方案（version=1，不带任何发布信息）。
        var plan = outcome.Plan;
        var lookups = await LoadLookupsAsync(plan.Items.Select(item => item.ProductCode), plan.Items.Select(item => item.HookModelGuid));
        var layout = Evaluate(plan, lookups);

        var now = _time.GetUtcNow().UtcDateTime;
        var header = new PlanogramHeader
        {
            Guid = planGuid,
            Name = plan.Name,
            Notes = plan.Notes,
            GapXMm = plan.GapXMm,
            GapYMm = plan.GapYMm,
            ShelfModelGuid = plan.Shelf.Guid,
            BayCount = plan.BayCount,
            Status = PlanogramStatus.Draft,
            Version = 1,
            ConflictCount = layout.Stats.ConflictCount,
            LastValidatedAt = now,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        var rows = plan.Items.Select(item => ToEntity(planGuid, item, lookups)).ToList();
        await InsertNewPlanAsync(header, rows);

        var created = await GetAsync(planGuid, cancellationToken);
        return created.Success
            ? ApiResponse<PlanogramDetailDto>.OK(created.Data!, "已导入")
            : created;
    }

    /// <summary>保存请求的字段名 → 导入文件里的字段位置（让前端能指到文件里出错的地方）。</summary>
    private static string ToImportFileField(string field, bool nameFromOverride)
    {
        switch (field)
        {
            case "name":
                return nameFromOverride ? "name" : "plan.name";
            case "notes":
            case "bayCount":
            case "gapXMm":
            case "gapYMm":
                return "plan." + field;
            case "shelfModelGuid":
                return "shelfModelCode";
        }

        var hook = ItemHookGuidField.Match(field);
        return hook.Success ? hook.Groups[1].Value + ".hookModelCode" : field;
    }
}
