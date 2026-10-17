// 「角色」「权限」页签（列表）的纯逻辑：角色卡片的标签与图标、权限列表的分类芯片与分组行。
// 不依赖 React Native，可在 Node 下测试。详情页的逻辑仍在 role-logic.ts / permission-logic.ts。
import { resolveRowPosition, type ListRowPosition } from "../people/list-position";
import type { PeopleTone } from "../people/ui";
import { isAccessAdminRoleName, isStoreManagerRoleName } from "../users/access-management";
import { isWarehouseManagerRoleName } from "../people/person-list-item";
import type { IdentityRole } from "./types";
import type { PermissionCategoryGroup, PermissionListItem } from "./permission-logic";

// ───────────────────────── 角色 ─────────────────────────

/** 角色卡片上的标签：系统（管理员，隐式拥有全部权限）/ 派生（店长，由「管理」分店自动决定）/ 停用。 */
export type RoleListTagKey = "system" | "derived" | "disabled";

export function resolveRoleListTags(roleName: string, isActive: boolean): RoleListTagKey[] {
  const tags: RoleListTagKey[] = [];
  if (isAccessAdminRoleName(roleName)) tags.push("system");
  // 店长不能手动加减：成员由「管理」分店派生（PR #840 的 planStoreManagerGrant 引导逻辑）。
  if (isStoreManagerRoleName(roleName)) tags.push("derived");
  if (!isActive) tags.push("disabled");
  return tags;
}

const ORDER_ROLE_NAMES = new Set(["order", "订货员"]);

/** 角色图标与底色：管理员紫盾牌、店长蓝分支、仓库经理橙仓库、订货员绿购物车，其余灰。 */
export function resolveRoleVisual(roleName: string): { icon: string; tone: PeopleTone } {
  if (isAccessAdminRoleName(roleName)) return { icon: "shield-outline", tone: "violet" };
  if (isStoreManagerRoleName(roleName)) return { icon: "source-branch", tone: "blue" };
  if (isWarehouseManagerRoleName(roleName)) return { icon: "warehouse", tone: "amber" };
  if (ORDER_ROLE_NAMES.has(roleName.trim().toLowerCase())) return { icon: "cart-outline", tone: "green" };
  return { icon: "account-outline", tone: "gray" };
}

/** 角色分页累加（下拉加载更多）：按 roleGUID 去重并保持首次出现的顺序。 */
export function flattenRolePages(pages: readonly { items: readonly IdentityRole[] }[]): IdentityRole[] {
  const seen = new Set<string>();
  const result: IdentityRole[] = [];
  for (const page of pages) {
    for (const role of page.items) {
      if (seen.has(role.roleGUID)) continue;
      seen.add(role.roleGUID);
      result.push(role);
    }
  }
  return result;
}

// ───────────────────────── 权限 ─────────────────────────

export interface PermissionCategoryChip {
  /** 空串 = 全部。 */
  key: string;
  label: string;
  count: number;
}

/** 分类芯片：第一个是「全部 N」，其余按分类带数量（分类顺序沿用 groupPermissionsByCategory）。 */
export function buildPermissionCategoryChips(
  groups: readonly PermissionCategoryGroup[],
  totalCount: number,
  allLabel: string,
): PermissionCategoryChip[] {
  return [
    { key: "", label: allLabel, count: totalCount },
    ...groups.map((group) => ({ key: group.key, label: group.displayName, count: group.items.length })),
  ];
}

export type PermissionListRowPosition = ListRowPosition;

export type PermissionListRow =
  | { kind: "header"; key: string; title: string; count: number }
  | { kind: "item"; key: string; item: PermissionListItem; position: PermissionListRowPosition };

/** 分组展平成可虚拟化的行：每个分类一个标题行 + 若干权限行（同一分类的权限共用一张卡片）。 */
export function buildPermissionListRows(groups: readonly PermissionCategoryGroup[]): PermissionListRow[] {
  const rows: PermissionListRow[] = [];
  for (const group of groups) {
    if (!group.items.length) continue;
    rows.push({ kind: "header", key: `header:${group.key}`, title: group.displayName, count: group.items.length });
    group.items.forEach((item, index) => {
      rows.push({ kind: "item", key: `item:${item.code}`, item, position: resolveRowPosition(index, group.items.length) });
    });
  }
  return rows;
}

/** 权限被几个角色使用；角色数没取到（接口失败）时返回 null，界面不显示这一列。 */
export function resolvePermissionRoleCount(roleCounts: Readonly<Record<string, number>> | undefined, code: string): number | null {
  if (!roleCounts) return null;
  return roleCounts[code] ?? 0;
}
