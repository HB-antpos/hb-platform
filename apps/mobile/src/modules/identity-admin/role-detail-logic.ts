import { isAccessAdminRoleName, isStoreManagerRoleName } from "@/modules/users/access-management";
import {
  isUncertainRoleWrite,
  type PermissionCategoryLike,
  type PermissionDraft,
  type PermissionItemLike,
  type RoleDraft,
} from "./role-logic";

/**
 * 角色详情（资料 / 权限 / 成员 / 菜单）共用的纯逻辑：
 * 改动汇总、权限分类视图（全选 / 仅看已选 / 搜索）、按顺序保存与部分失败的结论。
 * 不依赖 React / 网络，便于单测。
 */

// ───────────────────────── 改动汇总（吸底保存栏） ─────────────────────────

/** 资料改动项数：名称 / 说明 / 状态各算一项。 */
export function countProfileChanges(draft: RoleDraft, baseline: RoleDraft) {
  let count = 0;
  if (draft.roleName.trim() !== baseline.roleName.trim()) count += 1;
  if (draft.description.trim() !== baseline.description.trim()) count += 1;
  if (draft.isActive !== baseline.isActive) count += 1;
  return count;
}

export interface PermissionDelta {
  added: string[];
  removed: string[];
}

/** 权限草稿相对基线的增减（按权限码，去空白、去重）。 */
export function getPermissionDelta(draft: PermissionDraft | null | undefined): PermissionDelta {
  if (!draft) return { added: [], removed: [] };
  const clean = (codes: string[]) => Array.from(new Set(codes.map((code) => code.trim()).filter(Boolean))).sort();
  const selected = clean(draft.selected);
  const baseline = clean(draft.baseline);
  const selectedSet = new Set(selected);
  const baselineSet = new Set(baseline);
  return {
    added: selected.filter((code) => !baselineSet.has(code)),
    removed: baseline.filter((code) => !selectedSet.has(code)),
  };
}

export interface RoleChangeSummary {
  /** 资料改动项数 */
  profileCount: number;
  permissionAdded: number;
  permissionRemoved: number;
  /** 总改动数 = 资料项数 + 权限新增 + 权限移除 */
  total: number;
  hasChanges: boolean;
}

export function summarizeRoleChanges(input: { profileCount: number; permission: PermissionDelta }): RoleChangeSummary {
  const permissionAdded = input.permission.added.length;
  const permissionRemoved = input.permission.removed.length;
  const total = input.profileCount + permissionAdded + permissionRemoved;
  return { profileCount: input.profileCount, permissionAdded, permissionRemoved, total, hasChanges: total > 0 };
}

/** 「+2 −1」；只有一侧时只写一侧（「+1」「−2」），两侧都没有返回空串。 */
export function formatChangeBadge(added: number, removed: number) {
  const parts: string[] = [];
  if (added > 0) parts.push(`+${added}`);
  if (removed > 0) parts.push(`−${removed}`);
  return parts.join(" ");
}

export type SaveBarBlocker = "none" | "noChanges" | "saving" | "frozen" | "invalid" | "readOnly";

/**
 * 吸底保存栏能不能点：
 * - saving：保存进行中；
 * - frozen：上次写入被拒绝或结果不明确，须先刷新核对；
 * - invalid：资料有改动但校验不过（整个保存都不发，避免资料没存、权限却存了）；
 * - readOnly：当前账号对该角色没有任何可写能力。
 */
export function resolveSaveBarState(input: {
  hasChanges: boolean;
  saving: boolean;
  frozen: boolean;
  profileInvalid: boolean;
  canWriteAnything: boolean;
}): { enabled: boolean; blocker: SaveBarBlocker } {
  if (!input.canWriteAnything) return { enabled: false, blocker: "readOnly" };
  if (input.saving) return { enabled: false, blocker: "saving" };
  if (!input.hasChanges) return { enabled: false, blocker: "noChanges" };
  if (input.frozen) return { enabled: false, blocker: "frozen" };
  if (input.profileInvalid) return { enabled: false, blocker: "invalid" };
  return { enabled: true, blocker: "none" };
}

// ───────────────────────── 返回到「人员管理」Hub 的对应页签 ─────────────────────────

type HubRouterLike = {
  canGoBack(): boolean;
  back(): void;
  replace(href: never): void;
};

/**
 * 详情页的返回键：有上一页就正常返回（Hub 页签的选中状态由 Hub 自己保留）；
 * 深链 / 推送直接打开详情、没有上一页时，回到 Hub 对应页签而不是首页。
 */
export function backToHubTab(router: HubRouterLike, tab: "roles" | "permissions") {
  if (router.canGoBack()) router.back();
  else router.replace(`/users?tab=${tab}` as never);
}

// ───────────────────────── 系统角色 / 派生角色 ─────────────────────────

export interface RoleKind {
  /** 超级管理员角色：隐式拥有全部权限，角色名参与权限判定 */
  isSystem: boolean;
  /** 店长角色：成员由「管理」分店派生，后端拒绝手动加减成员（DERIVED_STORE_MANAGER_ROLE） */
  isDerived: boolean;
}

export function classifyRole(roleName: string): RoleKind {
  return { isSystem: isAccessAdminRoleName(roleName), isDerived: isStoreManagerRoleName(roleName) };
}

/** 系统角色与店长角色靠名称被后端识别，改名会让权限判定 / 派生失效，因此名称只读。 */
export function canEditRoleName(kind: RoleKind) {
  return !kind.isSystem && !kind.isDerived;
}

/** 店长角色的成员只能由「管理」分店派生，不允许在这里手动增删。 */
export function canEditRoleMembers(kind: RoleKind) {
  return !kind.isDerived;
}

// ───────────────────────── 权限分类视图 ─────────────────────────

export interface PermissionViewGroup<T extends PermissionItemLike> {
  category: string;
  displayName: string;
  /** 分类内全部权限数（不受搜索 / 仅看已选影响，用于「12 / 18」） */
  total: number;
  /** 分类内已选数 */
  selected: number;
  /** 当前可见的权限（已按搜索与「仅看已选」过滤） */
  permissions: T[];
}

export interface PermissionView<T extends PermissionItemLike> {
  groups: PermissionViewGroup<T>[];
  /** 目录内的权限总数（「已选 46 / 312」的分母） */
  total: number;
  /** 目录内已选数（只数目录里存在的权限码，避免历史遗留码让分子大于分母） */
  selected: number;
}

function matchesKeyword(permission: PermissionItemLike, categoryName: string, keyword: string) {
  return [permission.name, permission.displayName, permission.description ?? "", categoryName]
    .some((value) => value.toLocaleLowerCase().includes(keyword));
}

export function buildPermissionView<C extends PermissionCategoryLike>(
  categories: C[],
  options: { keyword: string; onlySelected: boolean; selected: Iterable<string> },
): PermissionView<C["permissions"][number]> {
  type T = C["permissions"][number];
  const selectedSet = new Set(options.selected);
  const keyword = options.keyword.trim().toLocaleLowerCase();
  const groups: PermissionViewGroup<T>[] = [];
  let total = 0;
  let selected = 0;
  for (const category of categories) {
    const inCategory = category.permissions.filter((permission) => selectedSet.has(permission.name)).length;
    total += category.permissions.length;
    selected += inCategory;
    const visible = category.permissions.filter((permission) =>
      (!options.onlySelected || selectedSet.has(permission.name))
      && (!keyword || matchesKeyword(permission, category.displayName, keyword))) as T[];
    if (visible.length === 0) continue;
    groups.push({
      category: category.category,
      displayName: category.displayName,
      total: category.permissions.length,
      selected: inCategory,
      permissions: visible,
    });
  }
  return { groups, total, selected };
}

/** 本类可见权限是否已全部选中（决定按钮显示「全选本类」还是「清空本类」）。 */
export function isGroupFullySelected(codes: readonly string[], selected: Iterable<string>) {
  if (codes.length === 0) return false;
  const selectedSet = new Set(selected);
  return codes.every((code) => selectedSet.has(code));
}

// ───────────────────────── 按顺序保存与部分失败 ─────────────────────────

/** 吸底保存可能提交的两类写入；成员增删是即时操作，不在其中。 */
export type RoleSaveStepKey = "profile" | "permissions";

export interface RoleSaveStep {
  key: RoleSaveStepKey;
  run: () => Promise<unknown>;
}

export interface RoleSaveOutcome {
  /** 已成功保存的步骤（按执行顺序） */
  completed: RoleSaveStepKey[];
  /** 失败的步骤；全部成功为 null */
  failed: { key: RoleSaveStepKey; error: unknown } | null;
  /** 因前序步骤失败而没有尝试的步骤 */
  notAttempted: RoleSaveStepKey[];
}

/**
 * 严格按顺序提交，任何一步失败立刻停下，后面的步骤不再发请求。
 * 返回已保存 / 失败 / 未尝试三类，界面据此明确告诉用户哪些存了、哪些没存，草稿由调用方保留。
 */
export async function runRoleSaveSequence(steps: readonly RoleSaveStep[]): Promise<RoleSaveOutcome> {
  const completed: RoleSaveStepKey[] = [];
  for (let index = 0; index < steps.length; index += 1) {
    const step = steps[index];
    try {
      await step.run();
      completed.push(step.key);
    } catch (error) {
      return {
        completed,
        failed: { key: step.key, error },
        notAttempted: steps.slice(index + 1).map((item) => item.key),
      };
    }
  }
  return { completed, failed: null, notAttempted: [] };
}

export type RoleWriteFailureKind = "forbidden" | "uncertain" | "rejected";

export interface RoleWriteFailure {
  kind: RoleWriteFailureKind;
  status?: number;
  code?: string;
}

function readString(record: Record<string, unknown> | null, ...keys: string[]) {
  if (!record) return undefined;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return undefined;
}

/**
 * 把写入失败归类：
 * - forbidden：401 / 403，服务器明确拒绝，冻结后续写入；
 * - uncertain：超时 / 5xx / 没有服务端结论，不知道有没有写进去，冻结并要求刷新核对；
 * - rejected：服务端给出确定的业务拒绝（如角色名重复），可以修正后重试。
 */
export function classifyRoleWriteFailure(error: unknown): RoleWriteFailure {
  const candidate = error as { response?: { status?: number; data?: unknown }; status?: number; code?: unknown } | null;
  const status = candidate?.response?.status ?? candidate?.status;
  const payload = candidate?.response?.data && typeof candidate.response.data === "object"
    ? candidate.response.data as Record<string, unknown>
    : null;
  const ownCode = typeof candidate?.code === "string" && candidate.code.trim() ? candidate.code.trim() : undefined;
  const code = readString(payload, "errorCode", "ErrorCode", "code", "Code") ?? ownCode;
  const base = { ...(status !== undefined ? { status } : {}), ...(code ? { code } : {}) };
  if (status === 401 || status === 403) return { kind: "forbidden", ...base };
  if (isUncertainRoleWrite(error)) return { kind: "uncertain", ...base };
  return { kind: "rejected", ...base };
}

export type RoleSaveMessageKey =
  | "savedProfile"
  | "savedPermissions"
  | "savedBoth"
  | "failedProfile"
  | "failedPermissions"
  | "profileFailedPermissionsPending"
  | "profileSavedPermissionsFailed";

export interface RoleSaveReport {
  tone: "success" | "error";
  messageKey: RoleSaveMessageKey;
  /** 失败原因；成功为 null */
  failure: RoleWriteFailure | null;
  /** 已保存 / 未保存 / 未提交的部分，供测试与界面分别展示 */
  saved: RoleSaveStepKey[];
  failedStep: RoleSaveStepKey | null;
  notAttempted: RoleSaveStepKey[];
}

/** 把保存结果翻译成「已保存 / 未保存」的结论，文案由界面按 messageKey 取。 */
export function buildRoleSaveReport(outcome: RoleSaveOutcome): RoleSaveReport {
  const saved = [...outcome.completed];
  if (!outcome.failed) {
    const hasProfile = saved.includes("profile");
    const hasPermissions = saved.includes("permissions");
    const messageKey: RoleSaveMessageKey = hasProfile && hasPermissions ? "savedBoth" : hasProfile ? "savedProfile" : "savedPermissions";
    return { tone: "success", messageKey, failure: null, saved, failedStep: null, notAttempted: [] };
  }
  const failure = classifyRoleWriteFailure(outcome.failed.error);
  const failedStep = outcome.failed.key;
  let messageKey: RoleSaveMessageKey;
  if (failedStep === "profile") {
    messageKey = outcome.notAttempted.includes("permissions") ? "profileFailedPermissionsPending" : "failedProfile";
  } else {
    messageKey = saved.includes("profile") ? "profileSavedPermissionsFailed" : "failedPermissions";
  }
  return { tone: "error", messageKey, failure, saved, failedStep, notAttempted: [...outcome.notAttempted] };
}

/** 失败原因 → 文案键；rejected 里已知的业务码给出更具体的提示。 */
export function resolveFailureReasonKey(failure: RoleWriteFailure): "forbidden" | "uncertainWrite" | "nameExists" | "saveFailed" {
  if (failure.kind === "forbidden") return "forbidden";
  if (failure.kind === "uncertain") return "uncertainWrite";
  if (failure.code === "ROLE_NAME_EXISTS") return "nameExists";
  return "saveFailed";
}
