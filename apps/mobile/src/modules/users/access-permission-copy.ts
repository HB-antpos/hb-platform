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
    return [user.fullName, user.username, user.email]
      .filter((value): value is string => Boolean(value))
      .some((value) => normalizeSearchText(value).includes(normalizedQuery));
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
