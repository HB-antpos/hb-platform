import type { IdentityRole } from "./types";
import type { PermissionCategoryGroup, PermissionListItem } from "./permission-logic";
import {
  buildPermissionCategoryChips,
  buildPermissionListRows,
  flattenRolePages,
  resolvePermissionRoleCount,
  resolveRoleListTags,
  resolveRoleVisual,
} from "./role-permission-list-logic";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// ── 角色标签 ──
assertEqual(resolveRoleListTags("Admin", true), ["system"], "管理员：系统");
assertEqual(resolveRoleListTags("超级管理员", true), ["system"], "中文超级管理员：系统");
assertEqual(resolveRoleListTags("StoreManager", true), ["derived"], "店长：派生");
assertEqual(resolveRoleListTags("店长", true), ["derived"], "中文店长：派生");
assertEqual(resolveRoleListTags("StoreStaff", true), [], "店员：无标签");
assertEqual(resolveRoleListTags("财务", false), ["disabled"], "停用角色：停用");
assertEqual(resolveRoleListTags("StoreManager", false), ["derived", "disabled"], "派生且停用：两个标签");

// ── 角色图标 ──
assertEqual(resolveRoleVisual("Admin"), { icon: "shield-outline", tone: "violet" }, "管理员");
assertEqual(resolveRoleVisual("StoreManager"), { icon: "source-branch", tone: "blue" }, "店长");
assertEqual(resolveRoleVisual("仓库经理"), { icon: "warehouse", tone: "amber" }, "仓库经理");
assertEqual(resolveRoleVisual("订货员"), { icon: "cart-outline", tone: "green" }, "订货员");
assertEqual(resolveRoleVisual("Order"), { icon: "cart-outline", tone: "green" }, "Order");
assertEqual(resolveRoleVisual("StoreStaff"), { icon: "account-outline", tone: "gray" }, "店员");

// ── 角色分页累加去重 ──
function role(id: string, userCount = 0): IdentityRole {
  return { roleGUID: id, roleName: id, isActive: true, createdAt: "", updatedAt: "", userCount };
}
assertEqual(
  flattenRolePages([{ items: [role("a"), role("b")] }, { items: [role("b"), role("c")] }]).map((item) => item.roleGUID),
  ["a", "b", "c"],
  "跨页重复的角色只留一条",
);

// ── 权限分类芯片 ──
function perm(code: string, categoryKey: string, isSystem = true): PermissionListItem {
  return { id: code, code, name: code, categoryKey, categoryName: categoryKey, isSystem, deletable: !isSystem };
}
const groups: PermissionCategoryGroup[] = [
  { key: "Reports", displayName: "报表", isCustom: false, items: [perm("Reports.Revenue.View", "Reports"), perm("Reports.ProductSales.View", "Reports"), perm("Reports.StaffHours.View", "Reports")] },
  { key: "Users", displayName: "用户", isCustom: true, items: [perm("Users.Export", "Users", false)] },
  { key: "Empty", displayName: "空分类", isCustom: true, items: [] },
];
assertEqual(
  buildPermissionCategoryChips(groups, 4, "全部"),
  [
    { key: "", label: "全部", count: 4 },
    { key: "Reports", label: "报表", count: 3 },
    { key: "Users", label: "用户", count: 1 },
    { key: "Empty", label: "空分类", count: 0 },
  ],
  "第一个是「全部」，其余按分类带数量",
);

// ── 权限分组行 ──
const rows = buildPermissionListRows(groups);
assertEqual(
  rows.map((row) => (row.kind === "header" ? `H:${row.title}:${row.count}` : `${row.item.code}:${row.position}`)),
  [
    "H:报表:3",
    "Reports.Revenue.View:first",
    "Reports.ProductSales.View:middle",
    "Reports.StaffHours.View:last",
    "H:用户:1",
    "Users.Export:only",
  ],
  "每个分类一个标题行 + 同卡片的权限行；空分类不输出",
);
assertEqual(buildPermissionListRows([]), [], "没有分组没有行");

// ── 角色数 ──
assertEqual(resolvePermissionRoleCount(undefined, "A"), null, "角色数没取到不显示");
assertEqual(resolvePermissionRoleCount({ A: 4 }, "A"), 4, "有角色数");
assertEqual(resolvePermissionRoleCount({ A: 4 }, "B"), 0, "没被任何角色使用");

console.log("role-permission-list-logic.test.ts: ok");
