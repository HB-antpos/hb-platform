import { buildPersonSearchText } from "@/modules/people/person-name";
import type { StoreUserListItem } from "./types";
import { hasPrivilegedAccessRole } from "./access-management";
import type {
  CopyPermissionDirection,
  CopyPermissionMode,
  CopyUserDirectPermissionsInput,
  UserDirectPermissionCopyResult,
  UserDirectPermissionCopyTarget,
} from "./access-management-types";

/** GUID 比较统一忽略大小写与首尾空白（后端可能回大写 GUID）。 */
export function normalizeCopyGuid(value?: string | null) {
  return (value ?? "").trim().toLowerCase();
}

function normalizeSearchText(value: string) {
  return value.trim().toLowerCase();
}

export interface PermissionCopyCandidateInput {
  /** 权限页当前正在编辑的员工。 */
  currentEmployeeGuid: string;
  /** 登录的操作者本人（可能是店长）。 */
  actorUserGuid?: string | null;
  query?: string;
}

/**
 * 复制权限的候选人：同店在职店员。
 * 排除：当前员工自己、操作者本人、已停用、店长/管理员/仓库经理等高权限角色，以及重复行。
 * 注意：「权限与对方完全相同」无法在列表阶段得知（列表不含直接权限），由预览结果的“无变化”判定。
 */
export function filterPermissionCopyCandidates(
  users: StoreUserListItem[],
  { currentEmployeeGuid, actorUserGuid, query = "" }: PermissionCopyCandidateInput,
): StoreUserListItem[] {
  const current = normalizeCopyGuid(currentEmployeeGuid);
  const actor = normalizeCopyGuid(actorUserGuid);
  const normalizedQuery = normalizeSearchText(query);
  const seen = new Set<string>();

  return users.filter((user) => {
    const guid = normalizeCopyGuid(user.userGUID);
    if (!guid || seen.has(guid)) return false;
    seen.add(guid);
    if (guid === current || (actor && guid === actor)) return false;
    if (user.status !== 1) return false;
    if (user.isStoreManager === true) return false;
    if (hasPrivilegedAccessRole(user.roleNames ?? [])) return false;
    if (!normalizedQuery) return true;
    // 姓名各段、昵称、账号统一走 buildPersonSearchText；邮箱不属于姓名，单独拼进来。
    return `${buildPersonSearchText(user)} ${normalizeSearchText(user.email ?? "")}`.includes(
      normalizedQuery,
    );
  });
}

/** 「复制给他人」可多选；「从同事复制」只能选一个同事。 */
export function togglePermissionCopySelection(
  selectedGuids: string[],
  userGuid: string,
  direction: CopyPermissionDirection,
) {
  const guid = userGuid.trim();
  if (!guid) return selectedGuids;
  const exists = selectedGuids.some(
    (item) => normalizeCopyGuid(item) === normalizeCopyGuid(guid),
  );
  if (direction === "fromColleague") return exists ? [] : [guid];
  return exists
    ? selectedGuids.filter(
        (item) => normalizeCopyGuid(item) !== normalizeCopyGuid(guid),
      )
    : [...selectedGuids, guid];
}

/**
 * 按方向生成请求：
 * toOthers 来源=当前员工、目标=所选多人；fromColleague 来源=所选同事、目标=当前员工。
 * 选择为空时返回 null，调用方据此禁用「下一步」。
 */
export function buildPermissionCopyInput(
  direction: CopyPermissionDirection,
  currentEmployeeGuid: string,
  selectedGuids: string[],
  mode: CopyPermissionMode,
): CopyUserDirectPermissionsInput | null {
  const current = currentEmployeeGuid.trim();
  const selected = Array.from(
    new Set(selectedGuids.map((guid) => guid.trim()).filter(Boolean)),
  ).filter((guid) => normalizeCopyGuid(guid) !== normalizeCopyGuid(current));
  if (!current || !selected.length) return null;

  if (direction === "fromColleague") {
    return { sourceUserGuid: selected[0], targetUserGuids: [current], mode };
  }
  return { sourceUserGuid: current, targetUserGuids: selected, mode };
}

export interface PermissionCopyPreviewSummary {
  sourceCount: number;
  skippedCount: number;
  /** 有实际变化的目标。 */
  changedTargets: UserDirectPermissionCopyTarget[];
  totalAdded: number;
  totalRemoved: number;
  /** 没有任何目标发生变化时，不允许再提交。 */
  hasChanges: boolean;
}

export function summarizePermissionCopyPreview(
  result: UserDirectPermissionCopyResult,
): PermissionCopyPreviewSummary {
  const changedTargets = result.targets.filter(
    (target) => target.added.length > 0 || target.removed.length > 0,
  );
  return {
    sourceCount: result.sourceDirectPermissionCodes.length,
    skippedCount: result.skippedPermissionCodes.length,
    changedTargets,
    totalAdded: result.targets.reduce((sum, item) => sum + item.added.length, 0),
    totalRemoved: result.targets.reduce(
      (sum, item) => sum + item.removed.length,
      0,
    ),
    hasChanges: changedTargets.length > 0,
  };
}

/**
 * 预览与当前选择必须一致才能提交：来源、方式、目标集合都对得上，
 * 防止用户在预览后改了选择却用旧预览去确认。
 */
export function isPermissionCopyPreviewCurrent(
  preview: UserDirectPermissionCopyResult | null,
  input: CopyUserDirectPermissionsInput | null,
) {
  if (!preview || !input) return false;
  if (normalizeCopyGuid(preview.sourceUserGuid) !== normalizeCopyGuid(input.sourceUserGuid)) {
    return false;
  }
  if (preview.mode !== input.mode) return false;
  const previewTargets = new Set(preview.targets.map((item) => normalizeCopyGuid(item.userGuid)));
  const inputTargets = new Set(input.targetUserGuids.map(normalizeCopyGuid));
  return (
    previewTargets.size === inputTargets.size &&
    Array.from(inputTargets).every((guid) => previewTargets.has(guid))
  );
}

/** 展示用权限名：已加载目录里按 code（忽略大小写）找名称，找不到直接显示 code。 */
export function createPermissionNameResolver(
  entries: { code: string; name: string }[],
) {
  const byCode = new Map(
    entries.map((entry) => [entry.code.trim().toLowerCase(), entry.name]),
  );
  return (code: string) => byCode.get(code.trim().toLowerCase()) || code;
}

/** 目标展示名：优先用已知名称映射，找不到退回 GUID，避免出现空白行。 */
export function resolveCopyTargetName(
  nameByGuid: Map<string, string>,
  userGuid: string,
) {
  return nameByGuid.get(normalizeCopyGuid(userGuid)) || userGuid;
}

// ───────────────────────── 确认页：逐人列出会发生什么 ─────────────────────────

export interface CopyConfirmRow {
  userGuid: string;
  name: string;
  /** 将增加的权限展示名。 */
  added: string[];
  /** 「覆盖」方式下将被移除的权限展示名。 */
  removed: string[];
  /** 已有且保持不变的数量（用于「已有全部 N 项，不变」）。 */
  unchangedCount: number;
  noChange: boolean;
}

/**
 * 确认页的逐人行：有变化的排前面（同组保持原顺序），没变化的沉到最后。
 * 数量标签（+3 / −2）与明细都从预览结果直接算，不另行猜测。
 */
export function buildCopyConfirmRows(
  preview: UserDirectPermissionCopyResult,
  resolveName: (userGuid: string) => string,
  resolvePermissionName: (code: string) => string,
): CopyConfirmRow[] {
  const rows = preview.targets.map<CopyConfirmRow>((target) => ({
    userGuid: target.userGuid,
    name: resolveName(target.userGuid),
    added: target.added.map(resolvePermissionName),
    removed: target.removed.map(resolvePermissionName),
    unchangedCount: target.unchangedCount,
    noChange: target.added.length === 0 && target.removed.length === 0,
  }));
  return [...rows.filter((row) => !row.noChange), ...rows.filter((row) => row.noChange)];
}

/** 被跳过的权限（操作者本人不能分配）的展示名；是整个操作共用的，不分目标。 */
export function listCopySkippedPermissionNames(
  preview: UserDirectPermissionCopyResult,
  resolvePermissionName: (code: string) => string,
) {
  return preview.skippedPermissionCodes.map(resolvePermissionName);
}

// ───────────────────────── 候选同事的分店范围 ─────────────────────────

export interface CopyStoreOption {
  storeCode: string;
  storeName: string;
}

export interface CopyStoreFilter {
  options: CopyStoreOption[];
  /** 打开弹层时默认选中的分店；空串表示「全部分店」（只有管理员可用）。 */
  initialStoreCode: string;
  /** 是否允许查看全部分店的同事；只有管理员可以。 */
  allowAll: boolean;
}

/**
 * 复制权限的候选同事按分店列出。
 * - 店长：只能在自己管理的分店里选，默认取路由带入的分店，没有就取第一个管理分店；
 * - 管理员：没有 storeCode 时（从详情进入）默认取目标员工所属分店，再不行就是「全部分店」，
 *   并且可以在弹层里切换分店 / 全部分店。
 */
export function resolveCopyStoreFilter(input: {
  isAdmin: boolean;
  /** 已按操作者收窄过的分店（店长=自己管理的分店，管理员=全部分店目录）。 */
  storeOptions: CopyStoreOption[];
  /** 目标员工已分配的分店编码。 */
  targetStoreCodes: string[];
  /** 路由带入的分店编码，可能为空。 */
  routeStoreCode: string;
}): CopyStoreFilter {
  const route = input.routeStoreCode.trim();
  const options = input.storeOptions.map((item) => ({ ...item }));
  const find = (code: string) =>
    options.find((item) => item.storeCode.toLowerCase() === code.toLowerCase());

  // 管理员的分店目录可能还没加载出来：路由带入的分店仍要能选。
  if (input.isAdmin && route && !find(route)) {
    options.push({ storeCode: route, storeName: route });
  }

  const preferred = [route, ...input.targetStoreCodes.map((code) => code.trim())].filter(Boolean);
  const hit = preferred.map(find).find((item) => Boolean(item));
  return {
    options,
    initialStoreCode: hit?.storeCode ?? (input.isAdmin ? "" : (options[0]?.storeCode ?? "")),
    allowAll: input.isAdmin,
  };
}
