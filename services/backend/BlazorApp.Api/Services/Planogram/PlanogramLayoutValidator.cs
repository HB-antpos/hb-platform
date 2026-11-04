using System.Globalization;

namespace BlazorApp.Api.Services.Planogram;

// ---------------------------------------------------------------------------------------------------------------
// 校验器的输入模型：只含几何 / 统计需要的字段，不依赖数据库实体，纯函数便于和前端共用同一份夹具测试。
// 单位一律 mm；孔距 / 首孔边距 / 挂钩尺寸是 decimal（允许 25.4 这类英寸换算值）。
// ---------------------------------------------------------------------------------------------------------------

/// <summary>货架型号里与几何有关的字段。板面宽 = 节宽 − 立柱宽，板面高 = 总高 − 底板面离地高。</summary>
public sealed record LayoutShelfInput(
    int BayWidthMm,
    int TotalHeightMm,
    int UprightWidthMm,
    int BaseTopHeightMm,
    decimal HolePitchXMm,
    decimal HolePitchYMm,
    decimal FirstHoleLeftMm,
    decimal FirstHoleTopMm,
    int HoleCols,
    int HoleRows)
{
    public decimal BoardWidthMm => BayWidthMm - UprightWidthMm;

    public decimal BoardHeightMm => TotalHeightMm - BaseTopHeightMm;
}

/// <summary>挂钩型号里与几何 / 承重有关的字段。</summary>
public sealed record LayoutHookInput(
    bool HasPriceTagArm,
    decimal ArmLengthMm,
    decimal DropMm,
    decimal TipReserveMm,
    decimal TagWidthMm,
    decimal TagHeightMm,
    decimal TagBottomAboveArmMm,
    int HoleSpanX,
    int HoleSpanY,
    decimal? LoadKg);

/// <summary>商品陈列参数；<c>RetailPrice</c> 取自 Product.RetailPrice（统计零售总值用）。</summary>
public sealed record LayoutSpecInput(
    int WidthMm,
    int HeightMm,
    int DepthMm,
    int HangHoleOffsetMm,
    int? UnitWeightG,
    IReadOnlyDictionary<string, int>? HookQtyOverrides,
    decimal RetailPrice);

/// <summary>一个挂钩条目（一钩一行）。</summary>
public sealed record LayoutItemInput(
    string Guid,
    int BayIndex,
    string ProductCode,
    string HookModelGuid,
    int HoleCol,
    int HoleRow,
    int? PerHookQty);

/// <summary>商品间距：左右 / 上下（mm）。</summary>
public sealed record LayoutGaps(int XMm, int YMm);

/// <summary>校验结果：冲突列表 + 统计。</summary>
public sealed class PlanogramLayoutResult
{
    public List<ConflictDto> Conflicts { get; init; } = new();
    public PlanogramStatsDto Stats { get; init; } = new();

    /// <summary>冲突条数过多时只返回前 <see cref="PlanogramLayoutValidator.MaxConflictsReturned"/> 条；统计里的冲突条目数仍是完整的。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// 陈列方案几何校验器（纯函数）。规则逐条对应
/// docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 B.1，前端 engine/geometry.ts 必须与它逐条一致，
/// 两端共用 apps/web/src/pages/PosAdmin/Planogram/engine/fixtures/layout-cases.json 跑测试防止口径漂移。
///
/// 坐标系：每一节（bay）独立，原点是该节「板面」左上角，x 向右、y 向下；各节之间不互通。
/// 浮点比较容差 0.001 mm：「a &lt; b」写成「a &lt; b − 容差」，「a &gt; b」写成「a &gt; b + 容差」，所以恰好等于间距不算冲突。
///
/// 参与范围（和前端夹具一致）：
/// - BAY_OUT_OF_RANGE 只报告，不阻止该条目继续按自己的节号参与后续判定；
/// - 缺参数 / 缺挂钩型号的条目不参与任何后续判定（含承重与统计里的件数 / 金额 / 占板）；
/// - HOLE_OUT_OF_RANGE 的条目不再参与「几何判定」（占孔冲突、板边、重叠），但承重（OVERLOAD）与统计仍照常计算。
/// </summary>
public static class PlanogramLayoutValidator
{
    /// <summary>浮点比较容差（mm）。</summary>
    internal const decimal Tolerance = 0.001m;

    /// <summary>单次返回的冲突条数上限：防止几千个条目叠在同一位置时成对冲突撑爆内存与响应体。</summary>
    internal const int MaxConflictsReturned = 20000;

    /// <summary>建议每钩挂数 = ⌊(臂长 − 臂端预留) ÷ 包装厚⌋；臂太短或厚度未填时为 0。</summary>
    public static int SuggestPerHookQty(LayoutHookInput hook, LayoutSpecInput spec)
    {
        if (spec.DepthMm <= 0)
        {
            return 0;
        }

        var usable = hook.ArmLengthMm - hook.TipReserveMm;
        return usable <= 0 ? 0 : (int)Math.Floor(usable / spec.DepthMm);
    }

    /// <summary>
    /// 每钩挂数：条目上的值 &gt; 规格里按挂钩型号的覆盖 &gt; 建议值。条目值与覆盖值小于 1 视为没填。
    /// </summary>
    public static int ResolvePerHookQty(LayoutItemInput item, LayoutHookInput hook, LayoutSpecInput spec)
    {
        if (item.PerHookQty is >= 1)
        {
            return item.PerHookQty.Value;
        }

        if (spec.HookQtyOverrides is not null
            && spec.HookQtyOverrides.TryGetValue(item.HookModelGuid, out var overrideQty)
            && overrideQty >= 1)
        {
            return overrideQty;
        }

        return SuggestPerHookQty(hook, spec);
    }

    public static PlanogramLayoutResult Validate(
        LayoutShelfInput shelf,
        IReadOnlyDictionary<string, LayoutHookInput> hooks,
        IReadOnlyDictionary<string, LayoutSpecInput> specs,
        IReadOnlyList<LayoutItemInput> items,
        int bayCount,
        LayoutGaps gaps)
    {
        var boardW = shelf.BoardWidthMm;
        var boardH = shelf.BoardHeightMm;
        var gapX = (decimal)gaps.XMm;
        var gapY = (decimal)gaps.YMm;
        var sink = new ConflictSink(items);

        // ① BAY_OUT_OF_RANGE：节号不在 1..bayCount。只报告，不排除。
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].BayIndex < 1 || items[i].BayIndex > bayCount)
            {
                sink.Add(PlanogramConflictCodes.BayOutOfRange, PlanogramSeverity.Error, i, null,
                    $"节号 {items[i].BayIndex} 超出方案的 1～{bayCount} 节");
            }
        }

        // ② 缺商品参数 / 缺挂钩型号（停用的型号仍在字典里，不算缺失）。缺失的条目不再参与后续判定。
        var resolved = new List<Placed>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var hasSpec = specs.TryGetValue(item.ProductCode, out var spec);
            var hasHook = hooks.TryGetValue(item.HookModelGuid, out var hook);
            if (!hasSpec)
            {
                sink.Add(PlanogramConflictCodes.ProductSpecMissing, PlanogramSeverity.Error, i, null,
                    $"商品 {item.ProductCode} 还没有陈列参数");
            }

            if (!hasHook)
            {
                sink.Add(PlanogramConflictCodes.HookModelMissing, PlanogramSeverity.Error, i, null,
                    "挂钩型号不存在");
            }

            if (hasSpec && hasHook)
            {
                resolved.Add(new Placed(i, item, hook!, spec!));
            }
        }

        // ③ HOLE_OUT_OF_RANGE：锚点孔位或占孔区间超出孔阵。该条目不再参与几何判定。
        var geometric = new List<Placed>(resolved.Count);
        foreach (var placed in resolved)
        {
            var item = placed.Item;
            var hook = placed.Hook;
            if (item.HoleCol < 1
                || item.HoleRow < 1
                || item.HoleCol + hook.HoleSpanX - 1 > shelf.HoleCols
                || item.HoleRow + hook.HoleSpanY - 1 > shelf.HoleRows)
            {
                sink.Add(PlanogramConflictCodes.HoleOutOfRange, PlanogramSeverity.Error, placed.Index, null,
                    $"孔位 第 {item.HoleCol} 列 / 第 {item.HoleRow} 行（占 {hook.HoleSpanX}×{hook.HoleSpanY} 孔）超出孔阵 {shelf.HoleCols}×{shelf.HoleRows}");
                continue;
            }

            placed.CalculateGeometry(shelf);
            geometric.Add(placed);
        }

        // ④ HOOK_HOLE_CLASH：同一节内两个条目占孔区间在行、列上都相交。成对报，每个条目各一条。
        foreach (var (a, b) in FindClashPairs(geometric))
        {
            var message = "与另一个挂钩占用了同一个孔";
            sink.Add(PlanogramConflictCodes.HookHoleClash, PlanogramSeverity.Error, a.Index, b.Index, message);
            sink.Add(PlanogramConflictCodes.HookHoleClash, PlanogramSeverity.Error, b.Index, a.Index, message);
        }

        // ⑤ OUT_OF_BOARD：矩形越过板面左 / 右 / 顶 / 底。
        var outOfBoard = new HashSet<int>();
        foreach (var placed in geometric)
        {
            if (placed.Left < -Tolerance
                || placed.Right > boardW + Tolerance
                || placed.Top < -Tolerance
                || placed.Bottom > boardH + Tolerance)
            {
                outOfBoard.Add(placed.Index);
                sink.Add(PlanogramConflictCodes.OutOfBoard, PlanogramSeverity.Error, placed.Index, null,
                    "商品（含价签）超出了板面范围");
            }
        }

        // ⑥ EDGE_GAP：未越界，但与板面边缘的间隙小于对应间距。
        foreach (var placed in geometric)
        {
            if (outOfBoard.Contains(placed.Index))
            {
                continue;
            }

            if (placed.Left < gapX - Tolerance
                || boardW - placed.Right < gapX - Tolerance
                || placed.Top < gapY - Tolerance
                || boardH - placed.Bottom < gapY - Tolerance)
            {
                sink.Add(PlanogramConflictCodes.EdgeGap, PlanogramSeverity.Error, placed.Index, null,
                    $"离板面边缘的间隙小于间距（左右 {Format(gapX)} mm、上下 {Format(gapY)} mm）");
            }
        }

        // ⑦ OVERLAP：同一节内两矩形各向外扩半个间距后相交面积 > 0（即净间隙 < 间距）。成对报。
        foreach (var (a, b) in FindOverlapPairs(geometric, gapX, gapY))
        {
            var message = $"与另一个商品的间距不足（要求左右 {Format(gapX)} mm、上下 {Format(gapY)} mm）";
            sink.Add(PlanogramConflictCodes.Overlap, PlanogramSeverity.Error, a.Index, b.Index, message);
            sink.Add(PlanogramConflictCodes.Overlap, PlanogramSeverity.Error, b.Index, a.Index, message);
        }

        // ⑧ OVERLOAD（warn）：每钩挂数 × 单件重 > 承重。承重不是几何判定，只要参数与型号都在就判。
        foreach (var placed in resolved)
        {
            var loadKg = placed.Hook.LoadKg;
            var unitWeightG = placed.Spec.UnitWeightG;
            if (loadKg is null || unitWeightG is null)
            {
                continue;
            }

            var qty = ResolvePerHookQty(placed.Item, placed.Hook, placed.Spec);
            var totalG = (decimal)qty * unitWeightG.Value;
            if (totalG > loadKg.Value * 1000m)
            {
                sink.Add(PlanogramConflictCodes.Overload, PlanogramSeverity.Warn, placed.Index, null,
                    $"每钩 {qty} 件共 {Format(totalG / 1000m)} kg，超过挂钩承重 {Format(loadKg.Value)} kg");
            }
        }

        return new PlanogramLayoutResult
        {
            Conflicts = sink.Conflicts,
            Stats = ComputeStats(items, resolved, bayCount, boardW, boardH, sink.ErrorItemCount),
            Truncated = sink.Truncated,
        };
    }

    // ---------------------------------------------------------------- 统计

    /// <summary>
    /// 统计口径（附录 B.1 第 6 条）：itemCount / skuCount 覆盖全部条目；
    /// unitCount / retailValue / fillPercent 只统计参数与型号都在的条目（缺失的算不出件数与尺寸）。
    /// </summary>
    private static PlanogramStatsDto ComputeStats(
        IReadOnlyList<LayoutItemInput> items,
        List<Placed> resolved,
        int bayCount,
        decimal boardW,
        decimal boardH,
        int conflictItemCount)
    {
        var unitCount = 0;
        var retailValue = 0m;
        var productArea = 0m;
        foreach (var placed in resolved)
        {
            var qty = ResolvePerHookQty(placed.Item, placed.Hook, placed.Spec);
            unitCount += qty;
            retailValue += qty * placed.Spec.RetailPrice;
            productArea += (decimal)placed.Spec.WidthMm * placed.Spec.HeightMm;
        }

        var boardArea = boardW * boardH * bayCount;
        var fillPercent = boardArea > 0
            ? (int)Math.Round(productArea / boardArea * 100m, MidpointRounding.AwayFromZero)
            : 0;

        return new PlanogramStatsDto
        {
            ItemCount = items.Count,
            SkuCount = items.Select(item => item.ProductCode).Distinct(StringComparer.Ordinal).Count(),
            UnitCount = unitCount,
            RetailValue = Math.Round(retailValue, 2, MidpointRounding.AwayFromZero),
            FillPercent = fillPercent,
            ConflictCount = conflictItemCount,
        };
    }

    // ---------------------------------------------------------------- 成对判定（按节分组 + 扫描线，避免 O(n²)）

    /// <summary>占孔区间相交的条目对（同节）。按「左端列」排序后扫描，列区间不再相交即停止。</summary>
    private static List<(Placed A, Placed B)> FindClashPairs(List<Placed> geometric)
    {
        var pairs = new List<(Placed, Placed)>();
        foreach (var group in geometric.GroupBy(placed => placed.Item.BayIndex))
        {
            var sorted = group.OrderBy(placed => placed.ColFrom).ThenBy(placed => placed.Index).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                for (var j = i + 1; j < sorted.Count && sorted[j].ColFrom <= sorted[i].ColTo; j++)
                {
                    if (Math.Max(sorted[i].RowFrom, sorted[j].RowFrom) <= Math.Min(sorted[i].RowTo, sorted[j].RowTo))
                    {
                        pairs.Add(Ordered(sorted[i], sorted[j]));
                    }
                }
            }
        }

        return SortPairs(pairs);
    }

    /// <summary>
    /// 外扩后相交的条目对（同节）：横向相交长度 = min(右) − max(左) + gapX，纵向同理，两个方向都 &gt; 容差才算相交。
    /// 按左边缘排序后扫描：后一个条目的左边缘 ≥ 前一个右边缘 + gapX 时横向不可能再相交，直接停止。
    /// </summary>
    private static List<(Placed A, Placed B)> FindOverlapPairs(List<Placed> geometric, decimal gapX, decimal gapY)
    {
        var pairs = new List<(Placed, Placed)>();
        foreach (var group in geometric.GroupBy(placed => placed.Item.BayIndex))
        {
            var sorted = group.OrderBy(placed => placed.Left).ThenBy(placed => placed.Index).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                for (var j = i + 1; j < sorted.Count && sorted[j].Left < sorted[i].Right + gapX - Tolerance; j++)
                {
                    var overlapX = Math.Min(sorted[i].Right, sorted[j].Right) - Math.Max(sorted[i].Left, sorted[j].Left) + gapX;
                    var overlapY = Math.Min(sorted[i].Bottom, sorted[j].Bottom) - Math.Max(sorted[i].Top, sorted[j].Top) + gapY;
                    if (overlapX > Tolerance && overlapY > Tolerance)
                    {
                        pairs.Add(Ordered(sorted[i], sorted[j]));
                    }
                }
            }
        }

        return SortPairs(pairs);
    }

    private static (Placed, Placed) Ordered(Placed a, Placed b) => a.Index <= b.Index ? (a, b) : (b, a);

    /// <summary>成对结果按输入顺序排序，保证输出稳定（便于测试与前端对照）。</summary>
    private static List<(Placed A, Placed B)> SortPairs(List<(Placed, Placed)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Index).ThenBy(pair => pair.Item2.Index).ToList();

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- 内部类型

    /// <summary>参数与挂钩型号都存在的条目 + 它的占孔区间与占位矩形（几何有效时才计算矩形）。</summary>
    private sealed class Placed
    {
        public Placed(int index, LayoutItemInput item, LayoutHookInput hook, LayoutSpecInput spec)
        {
            Index = index;
            Item = item;
            Hook = hook;
            Spec = spec;
            ColFrom = item.HoleCol;
            ColTo = item.HoleCol + hook.HoleSpanX - 1;
            RowFrom = item.HoleRow;
            RowTo = item.HoleRow + hook.HoleSpanY - 1;
        }

        public int Index { get; }
        public LayoutItemInput Item { get; }
        public LayoutHookInput Hook { get; }
        public LayoutSpecInput Spec { get; }
        public int ColFrom { get; }
        public int ColTo { get; }
        public int RowFrom { get; }
        public int RowTo { get; }
        public decimal Left { get; private set; }
        public decimal Right { get; private set; }
        public decimal Top { get; private set; }
        public decimal Bottom { get; private set; }

        /// <summary>
        /// 占位矩形（B.1 第 1～4 条）：
        /// 孔心 x = 首孔边距 + (col−1)×孔距X，y = 首孔边距 + (row−1)×孔距Y；
        /// 挂钩中心 xc = x + (占孔X − 1)×孔距X÷2（双钩居于两孔之间）；
        /// 有效宽 = max(商品宽, 带价签臂时的价签宽)；商品顶 = y + 钩高 − 吊孔偏移，商品底 = 商品顶 + 商品高；
        /// 带价签臂时，顶边取「价签顶 = y + 钩高 − 价签底边高于挂货臂 − 价签高」与商品顶中较高（数值较小）者。
        /// </summary>
        public void CalculateGeometry(LayoutShelfInput shelf)
        {
            var holeX = shelf.FirstHoleLeftMm + (Item.HoleCol - 1) * shelf.HolePitchXMm;
            var holeY = shelf.FirstHoleTopMm + (Item.HoleRow - 1) * shelf.HolePitchYMm;
            var centerX = holeX + (Hook.HoleSpanX - 1) * shelf.HolePitchXMm / 2m;
            var effectiveWidth = Math.Max(Spec.WidthMm, Hook.HasPriceTagArm ? Hook.TagWidthMm : 0m);
            Left = centerX - effectiveWidth / 2m;
            Right = centerX + effectiveWidth / 2m;

            var productTop = holeY + Hook.DropMm - Spec.HangHoleOffsetMm;
            Bottom = productTop + Spec.HeightMm;
            Top = productTop;
            if (Hook.HasPriceTagArm)
            {
                var tagTop = holeY + Hook.DropMm - Hook.TagBottomAboveArmMm - Hook.TagHeightMm;
                Top = Math.Min(productTop, tagTop);
            }
        }
    }

    /// <summary>收集冲突，同时按条目下标记录「有 error 级冲突的条目」，用于 conflictCount 去重计数。</summary>
    private sealed class ConflictSink
    {
        private readonly IReadOnlyList<LayoutItemInput> _items;
        private readonly HashSet<int> _errorItems = new();

        public ConflictSink(IReadOnlyList<LayoutItemInput> items)
        {
            _items = items;
        }

        public List<ConflictDto> Conflicts { get; } = new();

        public bool Truncated { get; private set; }

        public int ErrorItemCount => _errorItems.Count;

        public void Add(string code, string severity, int itemIndex, int? otherIndex, string message)
        {
            if (severity == PlanogramSeverity.Error)
            {
                _errorItems.Add(itemIndex);
            }

            if (Conflicts.Count >= MaxConflictsReturned)
            {
                Truncated = true;
                return;
            }

            Conflicts.Add(new ConflictDto
            {
                ItemGuid = _items[itemIndex].Guid,
                OtherItemGuid = otherIndex is { } other ? _items[other].Guid : null,
                Code = code,
                Severity = severity,
                Message = message,
            });
        }
    }
}
