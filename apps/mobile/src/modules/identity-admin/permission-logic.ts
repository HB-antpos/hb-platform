export const PERMISSION_ADMIN_PERMISSIONS = {
  view: "Roles.View",
  manage: "Roles.ManagePermissions",
} as const;

/** 与 Web 端「批量生成操作」保持同一组动作；服务端按 `${code}.${action}` 生成代码。 */
export const PERMISSION_ACTION_OPTIONS = ["Create", "View", "Edit", "Delete"] as const;
export type PermissionActionOption = (typeof PERMISSION_ACTION_OPTIONS)[number];

// 服务端 CreatePermissionAsync 为批量生成的名称追加的中文后缀，预览必须与实际落库结果一致。
const ACTION_NAME_SUFFIX: Record<string, string> = {
  Create: "创建",
  View: "查看",
  Edit: "编辑",
  Delete: "删除",
};

export interface PermissionCapabilityContext {
  isDeviceMode: boolean;
  isAdmin: boolean;
  hasPermission: (permission: string) => boolean;
}

export interface PermissionCapabilities {
  canView: boolean;
  canManage: boolean;
}

export function getPermissionCapabilities(context: PermissionCapabilityContext): PermissionCapabilities {
  if (context.isDeviceMode) return { canView: false, canManage: false };
  const allowed = (code: string) => context.isAdmin || context.hasPermission(code);
  return {
    canView: allowed(PERMISSION_ADMIN_PERMISSIONS.view),
    // RoleService 的权限写接口（创建/删除/分配角色）都有管理员二次校验，非管理员即使持有权限码也会被拒绝。
    canManage: context.isAdmin && allowed(PERMISSION_ADMIN_PERMISSIONS.manage),
  };
}

export interface CatalogPermissionLike {
  name: string;
  displayName: string;
  description?: string;
  category: string;
  isSystemPermission: boolean;
  createdAt?: string;
  createdBy?: string;
  updatedAt?: string;
  updatedBy?: string;
}

export interface CatalogCategoryLike {
  category: string;
  displayName: string;
  description?: string;
  permissions: CatalogPermissionLike[];
}

export interface SysPermissionLike {
  id: string;
  code: string;
  name: string;
  category: string;
  description?: string;
}

export interface PermissionListItem {
  id: string;
  code: string;
  name: string;
  /** 分类的原始键（catalog 的 category 字段），用于筛选与本地化查表 */
  categoryKey: string;
  /** 分类展示名（catalog 的 displayName，缺失时回退到 categoryKey） */
  categoryName: string;
  description?: string;
  /** 是否为代码内置的系统权限（不可删除） */
  isSystem: boolean;
  /** 只有非系统且已落库的权限才允许删除，与 Web 端 deletable 判定一致 */
  deletable: boolean;
  createdAt?: string;
  createdBy?: string;
  updatedAt?: string;
  updatedBy?: string;
}

/**
 * 合并「权限目录」与「数据库权限表」两个数据源。
 * 目录是权威定义（含代码内置权限），数据库表补充自定义权限和删除资格；移植自 Web 端 buildPermissionTableItems。
 */
export function buildPermissionListItems(
  categories: CatalogCategoryLike[],
  sysPermissions: SysPermissionLike[],
): PermissionListItem[] {
  const sysByCode = new Map(sysPermissions.map((item) => [item.code.trim().toLocaleLowerCase(), item]));
  const items = new Map<string, PermissionListItem>();

  for (const category of categories) {
    const categoryKey = (category.category || category.displayName).trim();
    for (const permission of category.permissions) {
      const code = permission.name.trim();
      if (!code) continue;
      const sysPermission = sysByCode.get(code.toLocaleLowerCase());
      items.set(code.toLocaleLowerCase(), {
        id: sysPermission?.id ?? code,
        code,
        name: permission.displayName || sysPermission?.name || code,
        categoryKey,
        categoryName: category.displayName || categoryKey,
        ...(permission.description || sysPermission?.description ? { description: permission.description || sysPermission?.description } : {}),
        isSystem: permission.isSystemPermission,
        deletable: !permission.isSystemPermission && Boolean(sysPermission),
        ...(permission.createdAt ? { createdAt: permission.createdAt } : {}),
        ...(permission.createdBy ? { createdBy: permission.createdBy } : {}),
        ...(permission.updatedAt ? { updatedAt: permission.updatedAt } : {}),
        ...(permission.updatedBy ? { updatedBy: permission.updatedBy } : {}),
      });
    }
  }

  for (const permission of sysPermissions) {
    const code = permission.code.trim();
    if (!code || items.has(code.toLocaleLowerCase())) continue;
    const categoryKey = permission.category.trim();
    items.set(code.toLocaleLowerCase(), {
      id: permission.id,
      code,
      name: permission.name || code,
      categoryKey,
      categoryName: categoryKey,
      ...(permission.description ? { description: permission.description } : {}),
      isSystem: false,
      deletable: true,
    });
  }

  return Array.from(items.values());
}

export interface PermissionCategoryGroup {
  key: string;
  displayName: string;
  items: PermissionListItem[];
  /** 分类内没有任何系统权限时视为自定义分类 */
  isCustom: boolean;
}

export function groupPermissionsByCategory(items: PermissionListItem[]): PermissionCategoryGroup[] {
  const groups = new Map<string, PermissionCategoryGroup>();
  for (const item of items) {
    const key = item.categoryKey || item.categoryName;
    const group = groups.get(key) ?? { key, displayName: item.categoryName || key, items: [], isCustom: true };
    group.items.push(item);
    if (item.isSystem) group.isCustom = false;
    groups.set(key, group);
  }
  return Array.from(groups.values())
    .map((group) => ({ ...group, items: [...group.items].sort((a, b) => a.name.localeCompare(b.name)) }))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
}

export function filterPermissionListItems(
  items: PermissionListItem[],
  { keyword = "", categoryKey = "" }: { keyword?: string; categoryKey?: string },
) {
  const normalized = keyword.trim().toLocaleLowerCase();
  return items.filter((item) => {
    if (categoryKey && item.categoryKey !== categoryKey) return false;
    if (!normalized) return true;
    return [item.code, item.name, item.description ?? "", item.categoryName]
      .some((value) => value.toLocaleLowerCase().includes(normalized));
  });
}

export function findPermissionListItem(items: PermissionListItem[], code: string) {
  const normalized = code.trim().toLocaleLowerCase();
  return items.find((item) => item.code.toLocaleLowerCase() === normalized) ?? null;
}

export interface PermissionDraft {
  code: string;
  name: string;
  category: string;
  description: string;
  actions: string[];
}

export interface PermissionDraftErrors {
  code?: "required" | "tooLong" | "invalid" | "exists";
  name?: "required" | "tooLong";
  category?: "required" | "tooLong";
  description?: "tooLong";
}

export function createEmptyPermissionDraft(): PermissionDraft {
  return { code: "", name: "", category: "", description: "", actions: [] };
}

// 允许字母、数字、下划线与点号分段，例如 StoreEvents.View；避免空格或斜杠导致路由参数无法编码。
const PERMISSION_CODE_PATTERN = /^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$/;

export function validatePermissionDraft(draft: PermissionDraft, existingCodes: Iterable<string> = []): PermissionDraftErrors {
  const errors: PermissionDraftErrors = {};
  const code = draft.code.trim();
  const name = draft.name.trim();
  const category = draft.category.trim();
  if (!code) errors.code = "required";
  else if (code.length > 100) errors.code = "tooLong";
  else if (!PERMISSION_CODE_PATTERN.test(code)) errors.code = "invalid";
  else {
    const existing = new Set(Array.from(existingCodes, (value) => value.trim().toLocaleLowerCase()));
    const generated = previewGeneratedPermissions(draft).map((item) => item.code.toLocaleLowerCase());
    if (generated.some((value) => existing.has(value))) errors.code = "exists";
  }
  if (!name) errors.name = "required";
  else if (name.length > 100) errors.name = "tooLong";
  if (!category) errors.category = "required";
  else if (category.length > 50) errors.category = "tooLong";
  if (draft.description.trim().length > 500) errors.description = "tooLong";
  return errors;
}

export function togglePermissionAction(draft: PermissionDraft, action: string): PermissionDraft {
  const actions = new Set(draft.actions);
  if (actions.has(action)) actions.delete(action);
  else actions.add(action);
  // 保持与选项定义相同的顺序，预览与服务端生成顺序一致。
  return { ...draft, actions: PERMISSION_ACTION_OPTIONS.filter((option) => actions.has(option)) };
}

export interface GeneratedPermissionPreview {
  code: string;
  name: string;
}

/** 预览本次提交将创建的权限；勾选动作时按服务端规则展开，否则只创建一条。 */
export function previewGeneratedPermissions(draft: PermissionDraft): GeneratedPermissionPreview[] {
  const code = draft.code.trim();
  const name = draft.name.trim();
  if (!code) return [];
  if (draft.actions.length === 0) return [{ code, name: name || code }];
  return draft.actions.map((action) => ({
    code: `${code}.${action}`,
    name: `${name || code} - ${ACTION_NAME_SUFFIX[action] ?? action}`,
  }));
}

export function toCreatePermissionInput(draft: PermissionDraft) {
  return {
    code: draft.code.trim(),
    name: draft.name.trim(),
    category: draft.category.trim(),
    description: draft.description.trim() || undefined,
    actions: draft.actions.length ? [...draft.actions] : undefined,
  };
}

/** 创建后读回核验：所有预期代码都必须出现在最新权限列表中。 */
export function isPermissionCreationVerified(draft: PermissionDraft, latestCodes: Iterable<string>) {
  const latest = new Set(Array.from(latestCodes, (value) => value.trim().toLocaleLowerCase()));
  return previewGeneratedPermissions(draft).every((item) => latest.has(item.code.toLocaleLowerCase()));
}

export interface RoleAssignmentDraft {
  selected: string[];
  baseline: string[];
}

function normalizeGuids(values: Iterable<string>) {
  return Array.from(new Set(Array.from(values).map((value) => value.trim()).filter(Boolean))).sort();
}

export function createRoleAssignmentDraft(roleGuids: Iterable<string>): RoleAssignmentDraft {
  const normalized = normalizeGuids(roleGuids);
  return { selected: normalized, baseline: normalized };
}

export function toggleRoleAssignment(draft: RoleAssignmentDraft, roleGuid: string): RoleAssignmentDraft {
  const selected = new Set(draft.selected);
  if (selected.has(roleGuid)) selected.delete(roleGuid);
  else selected.add(roleGuid);
  return { ...draft, selected: normalizeGuids(selected) };
}

export function getRoleAssignmentDelta(draft: RoleAssignmentDraft) {
  const baseline = new Set(draft.baseline);
  const selected = new Set(draft.selected);
  return {
    added: draft.selected.filter((guid) => !baseline.has(guid)),
    removed: draft.baseline.filter((guid) => !selected.has(guid)),
  };
}

export function isRoleAssignmentDirty(draft: RoleAssignmentDraft) {
  const delta = getRoleAssignmentDelta(draft);
  return delta.added.length > 0 || delta.removed.length > 0;
}

export function areRoleGuidsEqual(left: Iterable<string>, right: Iterable<string>) {
  const a = normalizeGuids(left);
  const b = normalizeGuids(right);
  return a.length === b.length && a.every((guid, index) => guid === b[index]);
}

/**
 * 权限直接授权用户的增量读回核验。
 * 服务端按增量写入，其他管理员可能同时调整了未涉及的用户，所以只核对本次新增已存在、本次移除已消失，
 * 不要求整份名单与草稿完全一致。GUID 按不区分大小写比较，与服务端 SQL Server 排序规则一致。
 */
export function isPermissionUserDeltaApplied(
  delta: { added: string[]; removed: string[] },
  verifiedUserGuids: Iterable<string>,
) {
  const verified = new Set(Array.from(verifiedUserGuids, (guid) => guid.trim().toLocaleLowerCase()));
  return delta.added.every((guid) => verified.has(guid.trim().toLocaleLowerCase()))
    && delta.removed.every((guid) => !verified.has(guid.trim().toLocaleLowerCase()));
}

/** 用户展示名：有姓名时「姓名（用户名）」，否则仅用户名。 */
export function formatPermissionUserName(user: { username: string; fullName?: string }) {
  const fullName = user.fullName?.trim();
  return fullName && fullName !== user.username ? `${fullName}（${user.username}）` : user.username;
}

/** 超级管理员角色隐式拥有全部权限，服务端分配时会直接跳过，因此在分配弹层中只读展示。 */
export function isImplicitAllRoleName(roleName: string, superAdminRoleNames: Iterable<string> = ["Admin", "管理员"]) {
  const normalized = roleName.trim().toLocaleLowerCase();
  return Array.from(superAdminRoleNames).some((name) => name.trim().toLocaleLowerCase() === normalized);
}

export interface AssignableRoleLike {
  roleGUID: string;
  roleName: string;
  description?: string;
}

/**
 * 「分配角色」弹层的可见行：
 * - 「仅看已选」保留已勾选的角色，以及被锁定（隐式拥有全部权限）的角色，因为它们本来就算已选；
 * - 搜索匹配角色名、本地化后的显示名、说明，大小写不敏感。
 */
export function buildAssignRoleRows<T extends AssignableRoleLike>(
  roles: readonly T[],
  options: {
    keyword: string;
    onlySelected: boolean;
    selectedGuids: Iterable<string>;
    isLocked: (role: T) => boolean;
    localizedName: (role: T) => string;
  },
): T[] {
  const selected = new Set(options.selectedGuids);
  const keyword = options.keyword.trim().toLocaleLowerCase();
  return roles.filter((role) => {
    if (options.onlySelected && !options.isLocked(role) && !selected.has(role.roleGUID)) return false;
    if (!keyword) return true;
    return [role.roleName, options.localizedName(role), role.description ?? ""]
      .some((value) => value.toLocaleLowerCase().includes(keyword));
  });
}

/** 「已选 4 / 12」：锁定角色算已选；已选只数列表里真实存在的角色，避免分子大于分母。 */
export function countAssignedRoles<T extends AssignableRoleLike>(
  roles: readonly T[],
  selectedGuids: Iterable<string>,
  isLocked: (role: T) => boolean,
) {
  const selected = new Set(selectedGuids);
  return {
    selected: roles.filter((role) => isLocked(role) || selected.has(role.roleGUID)).length,
    total: roles.length,
  };
}

/** 从权限列表项推导「分类候选」供新建权限选择；同时保留原始 key 便于新分类判断。 */
export function listPermissionCategoryOptions(items: PermissionListItem[]) {
  const seen = new Map<string, string>();
  for (const item of items) {
    const key = item.categoryKey || item.categoryName;
    if (key && !seen.has(key)) seen.set(key, item.categoryName || key);
  }
  return Array.from(seen.entries())
    .map(([key, label]) => ({ key, label }))
    .sort((a, b) => a.label.localeCompare(b.label));
}
