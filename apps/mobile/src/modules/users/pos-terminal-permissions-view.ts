import {
  POS_DISCOUNT_LEVELS,
  POS_DISCOUNT_SCOPES,
  getPosPermissionGroupSummary,
  getPosPermissionStats,
  parsePosPermissionCode,
  type PosDiscountLevel,
  type PosDiscountScope,
  type PosPermissionChanges,
  type PosPermissionDisplayGroup,
  type PosPermissionDisplayGroupKey,
  type PosPermissionDisplayItem,
  type PosPermissionListFilter,
} from "./pos-terminal-permission-presentation";

/**
 * 「收银权限」页的展示视图模型（纯函数）：概要统计、筛选芯片、改动汇总、
 * 高风险文案、折扣矩阵取值。页面只负责把这里算好的结构配上本地化文案渲染出来，
 * 这样「数字对不对 / 哪些格子红 / 警示条写什么」都能脱离 React 直接测试。
 * 这里不改变任何权限规则：分组、高风险定义、保存白名单仍由 pos-terminal-permission-presentation.ts
 * 与 pos-terminal-permissions.ts 决定。
 */

export type PosPermissionMode = "override" | "inherited";

/** 后端 mode 字段不区分大小写；除 override 外一律按「角色默认」处理。 */
export function normalizePosPermissionMode(mode?: string | null): PosPermissionMode {
  return mode?.trim().toLowerCase() === "override" ? "override" : "inherited";
}

// ───────────────────────── 概要统计 ─────────────────────────

export interface PosPermissionOverview {
  mode: PosPermissionMode;
  enabledCount: number;
  total: number;
  /** 已开启的高风险权限数（概要卡右侧红色数字）。 */
  highRiskEnabledCount: number;
  /** 可分配的高风险权限总数（筛选芯片「高风险 N」）。 */
  highRiskTotal: number;
  /** 本次草稿相对基线的改动项数（开启 + 关闭）。 */
  changedCount: number;
  /** 本次新开启的高风险项数；只有「开」才算风险，关掉高风险权限不提示。 */
  addedHighRiskCount: number;
}

export function buildPosPermissionOverview(input: {
  groups: PosPermissionDisplayGroup[];
  selectedCodeSet: ReadonlySet<string>;
  changes: PosPermissionChanges;
  mode?: string | null;
}): PosPermissionOverview {
  const stats = getPosPermissionStats(input.groups, input.selectedCodeSet);
  return {
    mode: normalizePosPermissionMode(input.mode),
    enabledCount: stats.enabledCount,
    total: stats.total,
    highRiskEnabledCount: stats.highRiskEnabledCount,
    highRiskTotal: stats.highRiskTotal,
    changedCount: input.changes.ordered.length,
    addedHighRiskCount: input.changes.addedHighRiskCount,
  };
}

// ───────────────────────── 筛选芯片 ─────────────────────────

export interface PosPermissionFilterChip {
  key: PosPermissionListFilter;
  /** 「全部」不带计数。 */
  count?: number;
}

/** 顺序固定：全部 | 已开 N | 高风险 N | 本次改动 N。 */
export function buildPosPermissionFilterChips(
  overview: Pick<PosPermissionOverview, "enabledCount" | "highRiskTotal" | "changedCount">
): PosPermissionFilterChip[] {
  return [
    { key: "all" },
    { key: "enabled", count: overview.enabledCount },
    { key: "highRisk", count: overview.highRiskTotal },
    { key: "changed", count: overview.changedCount },
  ];
}

export type PosPermissionEmptyKind = "noResults" | "noEnabled" | "noChanges";

/** 列表被筛空时的提示：有搜索词一律「没有符合条件」，否则按当前筛选给更具体的话。 */
export function getPosPermissionEmptyKind(
  query: string,
  filter: PosPermissionListFilter
): PosPermissionEmptyKind {
  if (query.trim().length > 0) return "noResults";
  if (filter === "enabled") return "noEnabled";
  if (filter === "changed") return "noChanges";
  return "noResults";
}

// ───────────────────────── 名称 / 改动汇总 ─────────────────────────

/** 页面传入已本地化的取词函数，纯函数本身不依赖 i18n。 */
export interface PosPermissionLabelResolver {
  group: (key: PosPermissionDisplayGroupKey) => string;
  /** 折扣项的展示名，如「整单六折」「单行手工折扣」。 */
  discountItem: (scope: PosDiscountScope, level: PosDiscountLevel) => string;
}

/** 折扣项用「范围 + 档位」生成的统一名称；其余项沿用后端返回的权限名。 */
export function getPosPermissionItemLabel(
  item: PosPermissionDisplayItem,
  labels: Pick<PosPermissionLabelResolver, "discountItem">
): string {
  return item.discount ? labels.discountItem(item.discount.scope, item.discount.level) : item.name;
}

export interface PosPermissionChangeRow {
  code: string;
  kind: "added" | "removed";
  name: string;
  groupKey: PosPermissionDisplayGroupKey;
  groupLabel: string;
  highRisk: boolean;
}

/**
 * 确认弹层的行：「开启」整体排在「关闭」前面（设计稿要求分开列），
 * 各自内部沿用页面分组顺序。
 */
export function buildPosPermissionChangeRows(
  changes: PosPermissionChanges,
  labels: PosPermissionLabelResolver
): PosPermissionChangeRow[] {
  const toRow = (item: PosPermissionDisplayItem, kind: "added" | "removed"): PosPermissionChangeRow => ({
    code: item.code,
    kind,
    name: getPosPermissionItemLabel(item, labels),
    groupKey: item.groupKey,
    groupLabel: labels.group(item.groupKey),
    highRisk: item.highRisk,
  });
  return [
    ...changes.added.map((item) => toRow(item, "added")),
    ...changes.removed.map((item) => toRow(item, "removed")),
  ];
}

export type PosPermissionFooterKind = "idle" | "plain" | "withHighRisk";

/** 吸底栏左侧文案类型：没改动 / 「N 项改动」 / 「N 项改动（含 M 项高风险）」。 */
export function getPosPermissionFooterKind(
  overview: Pick<PosPermissionOverview, "changedCount" | "addedHighRiskCount">
): PosPermissionFooterKind {
  if (overview.changedCount === 0) return "idle";
  return overview.addedHighRiskCount > 0 ? "withHighRisk" : "plain";
}

// ───────────────────────── 高风险文案 ─────────────────────────

export type PosHighRiskImpactKind =
  | "changePrice"
  | "manualDiscountLine"
  | "manualDiscountOrder"
  | "quickDiscountLine"
  | "quickDiscountOrder"
  | "noReceiptReturn"
  | "openCashDrawer";

export interface PosHighRiskImpact {
  kind: PosHighRiskImpactKind;
  /** 快捷折扣才有：用来拼「直接给整单打六折」。 */
  level?: PosDiscountLevel;
}

/**
 * 某一项权限开启后「对收银的实际影响」归类；后端新增的未知权限返回 null，
 * 文案层会退回到只列权限名的通用说法，不会编造影响。
 */
export function getPosHighRiskImpact(item: PosPermissionDisplayItem): PosHighRiskImpact | null {
  if (item.discount) {
    const { scope, level } = item.discount;
    if (level === "manual") {
      return { kind: scope === "line" ? "manualDiscountLine" : "manualDiscountOrder" };
    }
    return { kind: scope === "line" ? "quickDiscountLine" : "quickDiscountOrder", level };
  }
  switch (parsePosPermissionCode(item.code)?.key) {
    case "sales.changeprice":
      return { kind: "changePrice" };
    case "returns.addnoreceiptitem":
      return { kind: "noReceiptReturn" };
    case "cashdrawer.open":
      return { kind: "openCashDrawer" };
    default:
      return null;
  }
}

export interface PosHighRiskWarning {
  count: number;
  /** 本次新开启的高风险权限名，按页面分组顺序。 */
  names: string[];
  /** 去重后的影响说明；可能少于 names（未知权限没有影响归类）。 */
  impacts: PosHighRiskImpact[];
}

/** 确认弹层顶部红色警示条的数据；本次没有新开高风险权限时返回 null（不显示警示条）。 */
export function buildPosHighRiskWarning(
  changes: PosPermissionChanges,
  labels: Pick<PosPermissionLabelResolver, "discountItem">
): PosHighRiskWarning | null {
  const items = changes.added.filter((item) => item.highRisk);
  if (items.length === 0) return null;

  const impacts: PosHighRiskImpact[] = [];
  const seen = new Set<string>();
  items.forEach((item) => {
    const impact = getPosHighRiskImpact(item);
    if (!impact) return;
    const key = `${impact.kind}:${impact.level ?? ""}`;
    if (seen.has(key)) return;
    seen.add(key);
    impacts.push(impact);
  });

  return {
    count: items.length,
    names: items.map((item) => getPosPermissionItemLabel(item, labels)),
    impacts,
  };
}

/** 分组上的「含高风险」红标签：本组有已开启的高风险权限才亮，一个都没开就不打扰。 */
export function groupHasEnabledHighRisk(
  group: PosPermissionDisplayGroup,
  selectedCodeSet: ReadonlySet<string>
): boolean {
  return group.items.some((item) => item.highRisk && selectedCodeSet.has(item.code));
}

// ───────────────────────── 折扣矩阵 ─────────────────────────

export interface PosDiscountGridCell {
  item: PosPermissionDisplayItem;
  checked: boolean;
  changed: boolean;
}

export interface PosDiscountGridColumn {
  level: PosDiscountLevel;
  /** 本列任一可见格子是高风险，列头标红。 */
  highRisk: boolean;
}

export interface PosDiscountGridRow {
  scope: PosDiscountScope;
  /** 与 columns 一一对齐；这一格没有对应权限（无权分配或被筛掉）时为 null，占位不勾选。 */
  cells: (PosDiscountGridCell | null)[];
}

export interface PosDiscountGrid {
  columns: PosDiscountGridColumn[];
  rows: PosDiscountGridRow[];
}

/**
 * 「单行 / 整单 × 九折…手工」矩阵。列取实际出现的档位，行取实际出现的范围；
 * 与 buildPosDiscountMatrix 不同，缺格用 null 占位，保证表头与每行的格子严格对齐。
 * 每格的 item.code 就是回写用的权限 code：点击即 togglePosPermissionCode(selected, cell.item.code)。
 */
export function buildPosDiscountGrid(
  items: PosPermissionDisplayItem[],
  selectedCodeSet: ReadonlySet<string>,
  changedCodeSet: ReadonlySet<string>
): PosDiscountGrid {
  const discountItems = items.filter((item) => item.discount);
  const columns: PosDiscountGridColumn[] = POS_DISCOUNT_LEVELS.flatMap((level) => {
    const inColumn = discountItems.filter((item) => item.discount?.level === level);
    return inColumn.length ? [{ level, highRisk: inColumn.some((item) => item.highRisk) }] : [];
  });

  const rows: PosDiscountGridRow[] = POS_DISCOUNT_SCOPES.flatMap((scope) => {
    const cells = columns.map((column) => {
      const item = discountItems.find(
        (candidate) => candidate.discount?.scope === scope && candidate.discount.level === column.level
      );
      return item
        ? { item, checked: selectedCodeSet.has(item.code), changed: changedCodeSet.has(item.code) }
        : null;
    });
    return cells.some((cell) => cell !== null) ? [{ scope, cells }] : [];
  });

  return { columns, rows };
}

// ───────────────────────── 分组摘要 ─────────────────────────

export interface PosDiscountSummaryPart {
  scope: PosDiscountScope;
  /** 已开启的数字档位（九折…五折），按档位顺序。 */
  levels: PosDiscountLevel[];
  /** 是否开启了手工折扣。 */
  manual: boolean;
}

export type PosGroupSummary =
  | { kind: "empty" }
  | { kind: "names"; names: string[]; selectedCount: number; remainingCount: number }
  | { kind: "discount"; parts: PosDiscountSummaryPart[] };

/**
 * 分组行第二行摘要：折扣组按「单行九/八/七折、整单九/八/六折」归并，
 * 其余组列前 3 个已开权限名并带「等 N 项」。必须传完整分组，摘要不随筛选变化。
 */
export function buildPosGroupSummary(
  group: PosPermissionDisplayGroup,
  selectedCodeSet: ReadonlySet<string>
): PosGroupSummary {
  const summary = getPosPermissionGroupSummary(group, selectedCodeSet);
  if (summary.selectedCount === 0) return { kind: "empty" };

  if (group.key === "discount") {
    const parts = POS_DISCOUNT_SCOPES.flatMap((scope) => {
      const enabled = group.items.filter(
        (item) => item.discount?.scope === scope && selectedCodeSet.has(item.code)
      );
      if (enabled.length === 0) return [];
      const levels = POS_DISCOUNT_LEVELS.filter(
        (level) => level !== "manual" && enabled.some((item) => item.discount?.level === level)
      );
      const manual = enabled.some((item) => item.discount?.level === "manual");
      return [{ scope, levels, manual }];
    });
    // 折扣组理论上只含折扣项；万一都不是（后端异常数据），退回通用的权限名摘要。
    if (parts.length > 0) return { kind: "discount", parts };
  }

  return {
    kind: "names",
    names: summary.previewNames,
    selectedCount: summary.selectedCount,
    remainingCount: summary.remainingCount,
  };
}

// ───────────────────────── 页头 ─────────────────────────

/** 副标题里的分店：「店名 编号」；店名里已含编号（或没有编号）就不重复拼。 */
export function buildPosStoreLabel(storeName: string, storeCode?: string | null): string {
  const name = storeName.trim();
  const code = storeCode?.trim() ?? "";
  if (!code) return name;
  if (!name) return code;
  return name.toLowerCase().includes(code.toLowerCase()) ? name : `${name} ${code}`;
}

// ───────────────────────── 路由参数 → 分店 ─────────────────────────

export interface PosStoreTarget {
  storeGuid: string;
  storeCode: string;
  storeName: string;
}

interface SessionStoreLike {
  storeGUID?: string | null;
  storeCode?: string | null;
  storeName?: string | null;
}

function normalizeStoreKey(value?: string | null) {
  return value?.trim().toLowerCase() ?? "";
}

/**
 * 解析路由里的分店：接口按 storeGuid 调用，「复制同事」按 storeCode 列同事。
 * 入口（详情页、授权页分店行）可能只带其中一个，所以缺的那个用当前登录会话的分店列表补齐；
 * 路由里明确给出的值始终优先，补不到就保持为空（页面会按「参数不完整」处理，不会猜）。
 */
export function resolvePosPermissionStoreTarget(
  params: { storeGuid?: string | null; storeCode?: string | null; storeName?: string | null },
  sessionStores: readonly SessionStoreLike[]
): PosStoreTarget {
  const storeGuid = params.storeGuid?.trim() ?? "";
  const storeCode = params.storeCode?.trim() ?? "";
  const storeName = params.storeName?.trim() ?? "";

  // guid 与 code 都给全了就不用查会话分店。
  const match =
    storeGuid && storeCode
      ? null
      : (sessionStores.find((store) =>
          storeGuid
            ? normalizeStoreKey(store.storeGUID) === normalizeStoreKey(storeGuid)
            : storeCode
              ? normalizeStoreKey(store.storeCode) === normalizeStoreKey(storeCode)
              : false
        ) ?? null);

  return {
    storeGuid: storeGuid || match?.storeGUID?.trim() || "",
    storeCode: storeCode || match?.storeCode?.trim() || "",
    storeName: storeName || match?.storeName?.trim() || "",
  };
}
