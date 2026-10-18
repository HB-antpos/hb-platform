import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import {
  humanizePermissionCode,
  localizeAccessPermission,
  localizeAccessPermissionCategory,
  localizeAccessRoleDescription,
  localizeAccessRoleName,
  localizePosTerminalPermissionOptions,
} from "./access-permission-presentation";

const currentDir = new URL(".", import.meta.url).pathname;
const en = JSON.parse(
  readFileSync(
    resolve(currentDir, "../../locales/en/accessPermissions.json"),
    "utf8",
  ),
);
const zh = JSON.parse(
  readFileSync(
    resolve(currentDir, "../../locales/zh/accessPermissions.json"),
    "utf8",
  ),
);
const enUserManagement = JSON.parse(
  readFileSync(
    resolve(currentDir, "../../locales/en/screens/userManagement.json"),
    "utf8",
  ),
);
const zhUserManagement = JSON.parse(
  readFileSync(
    resolve(currentDir, "../../locales/zh/screens/userManagement.json"),
    "utf8",
  ),
);

assert.equal(
  localizeAccessPermissionCategory("货柜管理", "en"),
  "Container management",
);
assert.equal(localizeAccessPermissionCategory("货柜管理", "zh"), "货柜管理");
assert.deepEqual(
  localizeAccessPermission(
    {
      name: "Container.Edit",
      displayName: "编辑货柜",
      description: "页面 /warehouse/containers - 编辑货柜",
      category: "货柜管理",
      isSystemPermission: false,
    },
    "en",
  ),
  {
    name: "Edit containers",
    description: "Edit container records.",
  },
);
assert.deepEqual(
  localizeAccessPermission(
    {
      name: "CustomStock.ApproveLateTransfer",
      displayName: "审批延迟调拨",
      description: "自定义中文说明",
      category: "自定义",
      isSystemPermission: false,
    },
    "en",
  ),
  {
    name: "Approve late transfer",
    description: "Allows access to Approve late transfer.",
  },
  "英文界面遇到未知中文后端字段时必须使用稳定 code，而不是继续显示中文",
);
assert.equal(
  localizeAccessPermission(
    {
      name: "CustomStock.ApproveLateTransfer",
      displayName: "审批延迟调拨",
      description: "自定义中文说明",
      category: "自定义",
      isSystemPermission: false,
    },
    "zh",
  ).name,
  "审批延迟调拨",
);
assert.equal(
  humanizePermissionCode("Permissions.PosTerminal.Sales.AddOpenItem"),
  "Add open item",
);
assert.equal(humanizePermissionCode("Stores.View"), "View stores");
assert.equal(localizeAccessRoleName("StoreManager", "zh"), "店长");
assert.equal(localizeAccessRoleName("店长", "en"), "Store manager");
assert.equal(localizeAccessRoleName("StoreStaff", "zh"), "店员");
assert.equal(
  localizeAccessRoleDescription("StoreManager", "店长角色说明", "en"),
  "Store manager role.",
);

const permissionConstantsSource = readFileSync(
  resolve(
    currentDir,
    "../../../../../services/backend/BlazorApp.Shared/Constants/Permissions.cs",
  ),
  "utf8",
);
const permissionCodeBySymbol = new Map<string, string>();
const classStack: Array<string | null> = [];
let pendingClass: string | null = null;
const permissionTokenPattern =
  /public\s+static\s+class\s+(\w+)|public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"|[{}]/g;
for (const token of permissionConstantsSource.matchAll(
  permissionTokenPattern,
)) {
  if (token[1]) {
    pendingClass = token[1];
  } else if (token[0] === "{") {
    classStack.push(pendingClass);
    pendingClass = null;
  } else if (token[0] === "}") {
    classStack.pop();
  } else if (token[2]) {
    permissionCodeBySymbol.set(
      [
        ...classStack.filter((item): item is string => Boolean(item)),
        token[2],
      ].join("."),
      token[3],
    );
  }
}
const canonicalPermissionCodes = Array.from(
  permissionConstantsSource.matchAll(
    /public const string\s+\w+\s*=\s*"([^"]+)"/g,
  ),
  (match) => match[1],
);
assert.ok(canonicalPermissionCodes.length > 100);
canonicalPermissionCodes.forEach((code) => {
  const presentation = localizeAccessPermission(
    {
      name: code,
      displayName: "后端中文名称",
      description: "后端中文说明",
      category: "系统管理",
      isSystemPermission: true,
    },
    "en",
  );
  assert.doesNotMatch(presentation.name, /[\u3400-\u9fff]/u, code);
  assert.doesNotMatch(presentation.description, /[\u3400-\u9fff]/u, code);
});

const permissionSeedSource = readFileSync(
  resolve(
    currentDir,
    "../../../../../services/backend/BlazorApp.Shared/Constants/PermissionSeedData.cs",
  ),
  "utf8",
);
const seededPermissionCodes = Array.from(
  permissionSeedSource.matchAll(/new\(\s*(Permissions(?:\.\w+)+)\s*,/g),
  (match) => {
    const code = permissionCodeBySymbol.get(match[1]);
    assert.ok(code, `无法解析权限常量 ${match[1]}`);
    return code;
  },
);
const uniqueSeededPermissionCodes = [...new Set(seededPermissionCodes)].sort();
assert.equal(
  uniqueSeededPermissionCodes.length,
  seededPermissionCodes.length,
  "权限 seed 不得包含重复 code",
);
assert.deepEqual(
  Object.keys(en.permissions).sort(),
  uniqueSeededPermissionCodes,
  "英文权限资源必须与 seed 完全一致",
);
assert.deepEqual(
  Object.keys(zh.permissions).sort(),
  uniqueSeededPermissionCodes,
  "中文权限资源必须与 seed 完全一致",
);
seededPermissionCodes.forEach((code) => {
  assert.ok(en.permissions[code], `英文资源缺少权限 ${code}`);
  assert.ok(zh.permissions[code], `中文资源缺少权限 ${code}`);
});
const seededCategories = new Set(
  Array.from(
    permissionSeedSource.matchAll(
      /new\(Permissions\.[^,]+,\s*"[^"]+",\s*"([^"]+)"/g,
    ),
    (match) => match[1],
  ),
);
// 「订单管理」已改名「分店订货」，「价格管理」随死权限一并移除。
assert.equal(seededCategories.size, 36);
seededCategories.forEach((category) => {
  assert.notEqual(
    localizeAccessPermissionCategory(category, "en"),
    "Other permissions",
  );
});

assert.deepEqual(
  Object.keys(en.categories).sort(),
  Object.keys(zh.categories).sort(),
);
assert.deepEqual(
  Object.keys(en.permissions).sort(),
  Object.keys(zh.permissions).sort(),
);
assert.deepEqual(Object.keys(en.roles).sort(), Object.keys(zh.roles).sort());
assert.equal(
  enUserManagement.accessManagement.identity.roleCount_one,
  "{{count}} role assigned",
);
assert.equal(
  zhUserManagement.accessManagement.permissions.implicit,
  "管理员默认拥有",
);

// 收银权限页：后端只给中文名，英文界面按权限码取英文；中文界面原样；未登记的码不露中文。
const posOptions = [
  { code: "Permissions.PosTerminal.Returns.View", name: "查看退货页", group: "退货", description: "允许进入退货页" },
  { code: "Permissions.PosTerminal.Returns.AddNoReceiptItem", name: "添加无小票退货商品", group: "退货", description: "允许无小票退货" },
  { code: "Permissions.PosTerminal.Future.DoThing", name: "未来权限", group: "其他", description: "后端新加的权限" },
];
const enPos = localizePosTerminalPermissionOptions(posOptions, "en");
assert.equal(enPos[0].name, "Open returns screen");
assert.equal(enPos[1].name, "Return items without a receipt");
assert.equal(enPos[0].code, posOptions[0].code, "权限码不能被改写，保存仍按码提交");
assert.ok(enPos.every((option) => !/[\u3400-\u9fff]/u.test(`${option.name}${option.description}`)), "英文界面不得出现中文权限名或说明");
assert.deepEqual(localizePosTerminalPermissionOptions(posOptions, "zh"), posOptions, "中文界面保持后端原文");

console.log("access permission presentation tests passed");
