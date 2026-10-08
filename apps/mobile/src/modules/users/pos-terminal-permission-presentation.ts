import {
  buildGrantedPosPermissionCodes,
  hasPrivilegedPosPermissionRole,
} from "./pos-terminal-permissions";
import type { PosTerminalPermissionOption, StoreUserListItem } from "./types";

/**
 * 员工 POS 授权页的展示层纯逻辑：前端分组、模板、高风险、三态、改动对比、筛选与折扣矩阵。
 * 这里只依据权限 code 推导展示结构，不依赖后端返回的 group 中文字符串；
 * 保存边界仍由 assignablePermissions 白名单决定（见 pos-terminal-permissions.ts）。
 */

export type PosPermissionDisplayGroupKey =
  | "sales"
  | "discount"
  | "payment"
  | "returns"
  | "history"
  | "dailyClose"
  | "installments"
  | "drawerReceipt"
  | "other";

export const POS_PERMISSION_DISPLAY_GROUP_ORDER: readonly PosPermissionDisplayGroupKey[] = [
  "sales",
  "discount",
  "payment",
  "returns",
  "history",
  "dailyClose",
  "installments",
  "drawerReceipt",
  "other",
];

export type PosDiscountScope = "line" | "order";
export type PosDiscountLevel = "10" | "20" | "30" | "40" | "50" | "manual";

export const POS_DISCOUNT_SCOPES: readonly PosDiscountScope[] = ["line", "order"];
export const POS_DISCOUNT_LEVELS: readonly PosDiscountLevel[] = [
  "10",
  "20",
  "30",
  "40",
  "50",
  "manual",
];

const CODE_PREFIX = "Permissions.PosTerminal.";

function posCode(moduleAndAction: string) {
  return `${CODE_PREFIX}${moduleAndAction}`;
}

/** 高风险权限：可直接让收银金额变小或绕开小票核对的操作。 */
export const POS_HIGH_RISK_PERMISSION_CODES: readonly string[] = [
  posCode("Sales.ChangePrice"),
  posCode("Sales.LineManualDiscount"),
  posCode("Sales.OrderManualDiscount"),
  posCode("Sales.LineQuickDiscount40Percent"),
  posCode("Sales.OrderQuickDiscount40Percent"),
  posCode("Sales.LineQuickDiscount50Percent"),
  posCode("Sales.OrderQuickDiscount50Percent"),
  posCode("Returns.AddNoReceiptItem"),
  posCode("CashDrawer.Open"),
];

const HIGH_RISK_KEYS = new Set(
  POS_HIGH_RISK_PERMISSION_CODES.map((code) => parsePosPermissionCode(code)?.key ?? "")
);

/** 组内展示顺序沿用后端 PosTerminalBusinessPermissionCodes 的业务顺序，入口（View）永远在最前。 */
const CANONICAL_ACTION_ORDER = [
  "sales.view",
  "sales.additem",
  "sales.addopenitem",
  "sales.removeline",
  "sales.changequantity",
  "sales.changeprice",
  "sales.clearcart",
  "sales.holdorder",
  "sales.recallorder",
  "payment.view",
  "payment.takecash",
  "payment.takecard",
  "payment.takevoucher",
  "payment.removetender",
  "payment.confirm",
  "returns.view",
  "returns.addreceiptline",
  "returns.addnoreceiptitem",
  "returns.confirm",
  "history.view",
  "history.recall",
  "history.reprint",
  "dailyclose.view",
  "dailyclose.save",
  "dailyclose.reprint",
  "installments.view",
  "installments.create",
  "installments.addrepayment",
  "installments.cancel",
  "installments.confirmpickup",
  "installments.amendlines",
  "cashdrawer.open",
  "receipt.printlast",
];
const CANONICAL_ACTION_RANK = new Map(
  CANONICAL_ACTION_ORDER.map((key, index) => [key, index])
);

export type PosPermissionPresetKey = "basic" | "senior" | "all" | "none";

export const POS_PERMISSION_PRESET_ORDER: readonly PosPermissionPresetKey[] = [
  "basic",
  "senior",
  "all",
  "none",
];

/** 基础收银：能卖、能收款、能查历史单和补打小票；不含改价、清空购物车。 */
const BASIC_CASHIER_CODES = [
  posCode("Sales.View"),
  posCode("Sales.AddItem"),
  posCode("Sales.AddOpenItem"),
  posCode("Sales.ChangeQuantity"),
  posCode("Sales.RemoveLine"),
  posCode("Sales.HoldOrder"),
  posCode("Sales.RecallOrder"),
  posCode("Payment.View"),
  posCode("Payment.TakeCash"),
  posCode("Payment.TakeCard"),
  posCode("Payment.TakeVoucher"),
  posCode("Payment.RemoveTender"),
  posCode("Payment.Confirm"),
  posCode("History.View"),
  posCode("History.Recall"),
  posCode("History.Reprint"),
  posCode("Receipt.PrintLast"),
];

/** 资深收银：基础收银 + 有小票退货 + 九折/八折快捷折扣 + 日结。 */
const SENIOR_CASHIER_CODES = [
  ...BASIC_CASHIER_CODES,
  posCode("Returns.View"),
  posCode("Returns.AddReceiptLine"),
  posCode("Returns.Confirm"),
  posCode("Sales.LineQuickDiscount10Percent"),
  posCode("Sales.LineQuickDiscount20Percent"),
  posCode("Sales.OrderQuickDiscount10Percent"),
  posCode("Sales.OrderQuickDiscount20Percent"),
  posCode("DailyClose.View"),
  posCode("DailyClose.Save"),
  posCode("DailyClose.Reprint"),
];

export interface ParsedPosPermissionCode {
  /** 小写的「模块.动作」，用于不区分大小写的比较（后端按 OrdinalIgnoreCase 处理）。 */
  key: string;
  module: string;
  action: string;
}

export function parsePosPermissionCode(code: string): ParsedPosPermissionCode | null {
  // 兼容带或不带 "Permissions." 前缀的写法，只取 PosTerminal 后的两段。
  const match = /(?:^|\.)PosTerminal\.([A-Za-z]+)\.([A-Za-z0-9]+)$/i.exec(code.trim());
  if (!match) return null;
  const moduleName = match[1].toLowerCase();
  const action = match[2].toLowerCase();
  return { key: `${moduleName}.${action}`, module: moduleName, action };
}

export function getPosDiscountCell(
  code: string
): { scope: PosDiscountScope; level: PosDiscountLevel } | null {
  const parsed = parsePosPermissionCode(code);
  if (!parsed || parsed.module !== "sales") return null;
  const manual = /^(line|order)manualdiscount$/.exec(parsed.action);
  if (manual) return { scope: manual[1] as PosDiscountScope, level: "manual" };
  const quick = /^(line|order)quickdiscount(10|20|30|40|50)percent$/.exec(parsed.action);
  if (quick) {
    return { scope: quick[1] as PosDiscountScope, level: quick[2] as PosDiscountLevel };
  }
  return null;
}

export function getPosPermissionDisplayGroupKey(code: string): PosPermissionDisplayGroupKey {
  const parsed = parsePosPermissionCode(code);
  if (!parsed) return "other";
  if (getPosDiscountCell(code)) return "discount";

  switch (parsed.module) {
    case "sales":
      return "sales";
    case "payment":
      return "payment";
    case "returns":
      return "returns";
    case "history":
      return "history";
    case "dailyclose":
      return "dailyClose";
    case "installments":
      return "installments";
    case "cashdrawer":
    case "receipt":
      return "drawerReceipt";
    default:
      return "other";
  }
}

export function isHighRiskPosPermission(code: string) {
  const parsed = parsePosPermissionCode(code);
  return Boolean(parsed && HIGH_RISK_KEYS.has(parsed.key));
}

/** *.View 是 POS 上进入对应页面的入口权限。 */
export function isPosPermissionEntry(code: string) {
  return parsePosPermissionCode(code)?.action === "view";
}

export interface PosPermissionDisplayItem {
  code: string;
  name: string;
  description: string;
  groupKey: PosPermissionDisplayGroupKey;
  isEntry: boolean;
  highRisk: boolean;
  discount: { scope: PosDiscountScope; level: PosDiscountLevel } | null;
}

export interface PosPermissionDisplayGroup {
  key: PosPermissionDisplayGroupKey;
  items: PosPermissionDisplayItem[];
  /** 本组入口权限（*.View）；折扣、钱箱与小票、其他组没有入口。 */
  entryCode: string | null;
}

function getItemRank(item: PosPermissionDisplayItem, backendIndex: number) {
  if (item.isEntry) return -1;
  if (item.discount) {
    // 折扣按「单行 → 整单」「九折 → 手工」排列，与矩阵一致。
    return (
      POS_DISCOUNT_SCOPES.indexOf(item.discount.scope) * POS_DISCOUNT_LEVELS.length +
      POS_DISCOUNT_LEVELS.indexOf(item.discount.level)
    );
  }
  const key = parsePosPermissionCode(item.code)?.key ?? "";
  const rank = CANONICAL_ACTION_RANK.get(key);
  // 未登记的新权限排在已知权限之后，并保留后端返回顺序。
  return rank ?? CANONICAL_ACTION_ORDER.length + backendIndex;
}

export function buildPosPermissionDisplayGroups(
  assignablePermissions: PosTerminalPermissionOption[]
): PosPermissionDisplayGroup[] {
  const seen = new Set<string>();
  const buckets = new Map<
    PosPermissionDisplayGroupKey,
    { item: PosPermissionDisplayItem; rank: number }[]
  >();

  assignablePermissions.forEach((permission, backendIndex) => {
    if (seen.has(permission.code)) return;
    seen.add(permission.code);
    const groupKey = getPosPermissionDisplayGroupKey(permission.code);
    const item: PosPermissionDisplayItem = {
      code: permission.code,
      name: permission.name,
      description: permission.description,
      groupKey,
      // 只有真正的页面组才有入口；未知模块的 View 不当作入口，避免误提示。
      isEntry: groupKey !== "other" && groupKey !== "discount" && isPosPermissionEntry(permission.code),
      highRisk: isHighRiskPosPermission(permission.code),
      discount: getPosDiscountCell(permission.code),
    };
    const bucket = buckets.get(groupKey) ?? [];
    bucket.push({ item, rank: getItemRank(item, backendIndex) });
    buckets.set(groupKey, bucket);
  });

  return POS_PERMISSION_DISPLAY_GROUP_ORDER.flatMap((key) => {
    const bucket = buckets.get(key);
    if (!bucket?.length) return [];
    const items = [...bucket].sort((left, right) => left.rank - right.rank).map(({ item }) => item);
    return [{ key, items, entryCode: items.find((item) => item.isEntry)?.code ?? null }];
  });
}

export type PosPermissionSelectionState = "all" | "partial" | "none";

export function getPosPermissionSelectionState(
  codes: string[],
  selectedCodeSet: ReadonlySet<string>
): PosPermissionSelectionState {
  const selectedCount = codes.filter((code) => selectedCodeSet.has(code)).length;
  if (codes.length === 0 || selectedCount === 0) return "none";
  return selectedCount === codes.length ? "all" : "partial";
}

/** 组头三态勾选框：全选时点击清空本组，其余情况点击全选本组。 */
export function getNextPosPermissionGroupChecked(state: PosPermissionSelectionState) {
  return state !== "all";
}

/** 入口没开、组内却有其他项开着：这些项在 POS 上用不到，只提示不改勾选。 */
export function hasPosPermissionEntryGap(
  group: PosPermissionDisplayGroup,
  selectedCodeSet: ReadonlySet<string>
) {
  if (!group.entryCode || selectedCodeSet.has(group.entryCode)) return false;
  return group.items.some((item) => !item.isEntry && selectedCodeSet.has(item.code));
}

export interface PosPermissionChange {
  item: PosPermissionDisplayItem;
  kind: "added" | "removed";
}

export interface PosPermissionChanges {
  added: PosPermissionDisplayItem[];
  removed: PosPermissionDisplayItem[];
  /** 按页面分组顺序排列的全部改动，确认弹层直接渲染。 */
  ordered: PosPermissionChange[];
  changedCodes: Set<string>;
  addedHighRiskCount: number;
}

export function computePosPermissionChanges(
  groups: PosPermissionDisplayGroup[],
  baselineCodes: string[],
  selectedCodes: string[]
): PosPermissionChanges {
  const baseline = new Set(baselineCodes);
  const selected = new Set(selectedCodes);
  const added: PosPermissionDisplayItem[] = [];
  const removed: PosPermissionDisplayItem[] = [];
  const ordered: PosPermissionChange[] = [];

  // 只比较可分配项；基线与草稿都已按白名单过滤，未知 code 不会产生改动。
  groups.forEach((group) => {
    group.items.forEach((item) => {
      const before = baseline.has(item.code);
      const after = selected.has(item.code);
      if (before === after) return;
      if (after) {
        added.push(item);
        ordered.push({ item, kind: "added" });
      } else {
        removed.push(item);
        ordered.push({ item, kind: "removed" });
      }
    });
  });

  return {
    added,
    removed,
    ordered,
    changedCodes: new Set(ordered.map((change) => change.item.code)),
    addedHighRiskCount: added.filter((item) => item.highRisk).length,
  };
}

/** 模板一律与 assignablePermissions 取交集：店长可分配的范围可能比完整清单小。 */
export function buildPosPermissionPresetCodes(
  preset: PosPermissionPresetKey,
  assignablePermissions: PosTerminalPermissionOption[]
): string[] {
  switch (preset) {
    case "none":
      return [];
    case "all":
      return buildGrantedPosPermissionCodes(
        assignablePermissions.map((permission) => permission.code),
        assignablePermissions
      );
    case "basic":
      return intersectByKey(BASIC_CASHIER_CODES, assignablePermissions);
    case "senior":
      return intersectByKey(SENIOR_CASHIER_CODES, assignablePermissions);
  }
}

function intersectByKey(templateCodes: string[], assignablePermissions: PosTerminalPermissionOption[]) {
  const templateKeys = new Set(
    templateCodes.map((code) => parsePosPermissionCode(code)?.key ?? code)
  );
  // 返回后端给的原始 code，保证保存时与白名单逐字一致。
  return Array.from(
    new Set(
      assignablePermissions
        .filter((permission) => {
          const key = parsePosPermissionCode(permission.code)?.key;
          return Boolean(key && templateKeys.has(key));
        })
        .map((permission) => permission.code)
    )
  );
}

function sameCodeSet(left: string[], right: string[]) {
  const leftSet = new Set(left);
  const rightSet = new Set(right);
  return leftSet.size === rightSet.size && [...leftSet].every((code) => rightSet.has(code));
}

/** 草稿与哪些模板完全一致（可分配范围受限时可能同时命中多个）。 */
export function getMatchingPosPermissionPresets(
  selectedCodes: string[],
  assignablePermissions: PosTerminalPermissionOption[]
): PosPermissionPresetKey[] {
  return POS_PERMISSION_PRESET_ORDER.filter((preset) =>
    sameCodeSet(selectedCodes, buildPosPermissionPresetCodes(preset, assignablePermissions))
  );
}

export type PosPermissionListFilter = "all" | "enabled" | "highRisk" | "changed";

export interface PosPermissionFilterInput {
  query: string;
  filter: PosPermissionListFilter;
  selectedCodeSet: ReadonlySet<string>;
  changedCodeSet: ReadonlySet<string>;
  /** 前端分组名（已本地化），搜索分组名时整组命中。 */
  getGroupLabel: (key: PosPermissionDisplayGroupKey) => string;
}

function normalizeSearchText(value: string) {
  return value.trim().toLocaleLowerCase();
}

function matchesListFilter(
  item: PosPermissionDisplayItem,
  filter: PosPermissionListFilter,
  selectedCodeSet: ReadonlySet<string>,
  changedCodeSet: ReadonlySet<string>
) {
  switch (filter) {
    case "enabled":
      return selectedCodeSet.has(item.code);
    case "highRisk":
      return item.highRisk;
    case "changed":
      return changedCodeSet.has(item.code);
    default:
      return true;
  }
}

export function filterPosPermissionGroups(
  groups: PosPermissionDisplayGroup[],
  { query, filter, selectedCodeSet, changedCodeSet, getGroupLabel }: PosPermissionFilterInput
): PosPermissionDisplayGroup[] {
  const normalizedQuery = normalizeSearchText(query);

  return groups.flatMap((group) => {
    const groupMatchesQuery =
      normalizedQuery.length > 0 &&
      normalizeSearchText(getGroupLabel(group.key)).includes(normalizedQuery);
    const items = group.items.filter((item) => {
      if (!matchesListFilter(item, filter, selectedCodeSet, changedCodeSet)) return false;
      if (!normalizedQuery || groupMatchesQuery) return true;
      return normalizeSearchText(item.name).includes(normalizedQuery);
    });
    return items.length ? [{ ...group, items }] : [];
  });
}

export function isPosPermissionFilterActive(query: string, filter: PosPermissionListFilter) {
  return normalizeSearchText(query).length > 0 || filter !== "all";
}

/** 默认展开：有本次改动或部分勾选的组；其余折叠，减少首屏滚动。 */
export function getDefaultExpandedPosPermissionGroups(
  groups: PosPermissionDisplayGroup[],
  selectedCodeSet: ReadonlySet<string>,
  changedCodeSet: ReadonlySet<string>
): Set<PosPermissionDisplayGroupKey> {
  return new Set(
    groups
      .filter((group) => {
        const codes = group.items.map((item) => item.code);
        return (
          codes.some((code) => changedCodeSet.has(code)) ||
          getPosPermissionSelectionState(codes, selectedCodeSet) === "partial"
        );
      })
      .map((group) => group.key)
  );
}

export interface PosPermissionGroupSummary {
  selectedCount: number;
  total: number;
  /** 前几个已开权限名，用作组头副标题；为空时显示「未开放」。 */
  previewNames: string[];
  remainingCount: number;
}

export function getPosPermissionGroupSummary(
  group: PosPermissionDisplayGroup,
  selectedCodeSet: ReadonlySet<string>,
  maxPreview = 3
): PosPermissionGroupSummary {
  const selectedItems = group.items.filter((item) => selectedCodeSet.has(item.code));
  const previewNames = selectedItems.slice(0, maxPreview).map((item) => item.name);
  return {
    selectedCount: selectedItems.length,
    total: group.items.length,
    previewNames,
    remainingCount: selectedItems.length - previewNames.length,
  };
}

export interface PosPermissionStats {
  enabledCount: number;
  total: number;
  highRiskTotal: number;
  highRiskEnabledCount: number;
  segments: { key: PosPermissionDisplayGroupKey; total: number; selected: number }[];
}

export function getPosPermissionStats(
  groups: PosPermissionDisplayGroup[],
  selectedCodeSet: ReadonlySet<string>
): PosPermissionStats {
  const items = groups.flatMap((group) => group.items);
  return {
    enabledCount: items.filter((item) => selectedCodeSet.has(item.code)).length,
    total: items.length,
    highRiskTotal: items.filter((item) => item.highRisk).length,
    highRiskEnabledCount: items.filter(
      (item) => item.highRisk && selectedCodeSet.has(item.code)
    ).length,
    // 分段进度条：每段宽度按组内权限数，填充比例按已开数。
    segments: groups.map((group) => ({
      key: group.key,
      total: group.items.length,
      selected: group.items.filter((item) => selectedCodeSet.has(item.code)).length,
    })),
  };
}

export interface PosDiscountMatrixRow {
  scope: PosDiscountScope;
  cells: { level: PosDiscountLevel; item: PosPermissionDisplayItem }[];
}

/** 折扣组两行矩阵；只放实际可见的格子，缺失档位（无权分配或被筛掉）不占位。 */
export function buildPosDiscountMatrix(items: PosPermissionDisplayItem[]): PosDiscountMatrixRow[] {
  return POS_DISCOUNT_SCOPES.flatMap((scope) => {
    const cells = POS_DISCOUNT_LEVELS.flatMap((level) => {
      const item = items.find(
        (candidate) => candidate.discount?.scope === scope && candidate.discount.level === level
      );
      return item ? [{ level, item }] : [];
    });
    return cells.length ? [{ scope, cells }] : [];
  });
}


function normalizeIdentity(value?: string | null) {
  return value?.trim().toLowerCase() ?? "";
}

export interface PosPermissionCopyCandidateInput {
  targetUserGuid: string;
  actorUserGuid?: string | null;
  query?: string;
}

/**
 * 复制同事的候选人：与 getPosPermissionEntryState 的资格规则一致——
 * 只列启用账号、排除目标员工本人与操作者本人、排除高权限角色（这些人的 POS 授权本页也不可管理）。
 */
export function filterPosPermissionCopyCandidates(
  users: StoreUserListItem[],
  { targetUserGuid, actorUserGuid, query = "" }: PosPermissionCopyCandidateInput
): StoreUserListItem[] {
  const target = normalizeIdentity(targetUserGuid);
  const actor = normalizeIdentity(actorUserGuid);
  const normalizedQuery = normalizeSearchText(query);
  const seen = new Set<string>();

  return users.filter((user) => {
    const guid = normalizeIdentity(user.userGUID);
    if (!guid || seen.has(guid)) return false;
    seen.add(guid);
    if (guid === target || (actor && guid === actor)) return false;
    if (user.status !== 1) return false;
    if (hasPrivilegedPosPermissionRole(user.roleNames)) return false;
    if (!normalizedQuery) return true;
    return [user.fullName, user.username]
      .filter((value): value is string => Boolean(value))
      .some((value) => normalizeSearchText(value).includes(normalizedQuery));
  });
}

export function getPosPermissionCopyCandidateName(user: StoreUserListItem) {
  return user.fullName?.trim() || user.username;
}

export function getPosPermissionInitials(value: string) {
  const words = value.trim().split(/\s+/).filter(Boolean);
  if (!words.length) return "?";
  return words.length === 1
    ? words[0].slice(0, 2).toUpperCase()
    : `${words[0][0]}${words[1][0]}`.toUpperCase();
}
