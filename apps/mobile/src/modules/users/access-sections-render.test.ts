import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 统一授权页四个分段 + 复制权限弹层的真实渲染测试：
// 在 test-renderer 里挂载真实组件（react-native / paper 换成同名宿主桩），点击、切换并断言回调与可见文案，
// 钉住「店长没有管理档」「店长行不可勾选只引导」「继承来的开关置灰」「收银权限不在直接权限里」「复制确认逐人列出」等。
const zh = JSON.parse(
  readFileSync(resolve(__dirname, "../../locales/zh/screens/peopleAccess.json"), "utf8"),
) as Record<string, unknown>;

function resolveKey(key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      zh,
    );
}

function lookup(key: string, options?: Record<string, unknown>): string {
  if (key.startsWith("common:")) return key;
  let value = resolveKey(key);
  // 中文只有 other 复数形式；带 count 的键先找 _other。
  if (options && typeof options.count === "number") {
    const plural = resolveKey(`${key}_other`);
    if (typeof plural === "string") value = plural;
  }
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
  });
  mockModule("react-native-paper", {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    Switch: host("Switch"),
    Searchbar: host("Searchbar"),
    IconButton: host("IconButton"),
    ActivityIndicator: host("ActivityIndicator"),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });

  const { createRoot } = await import("test-renderer");
  const { AccessStoresSection } = await import("./access-stores-section");
  const { AccessRolesSection } = await import("./access-roles-section");
  const { AccessPermissionsSection } = await import("./access-permissions-section");
  const { AccessMenuSection } = await import("./access-menu-section");

  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);
  const texts = () => all("Text").map(textOf);
  const render = (element: React.ReactElement) => act(() => root.render(element));
  const byLabel = (type: string, label: string) =>
    all(type).find((node) => node.props.accessibilityLabel === label);

  try {
    // ───────────── 分店分段 ─────────────
    const stores = [
      { storeGUID: "g-b", storeCode: "1008", storeName: "Bankstown" },
      { storeGUID: "g-l", storeCode: "1012", storeName: "Liverpool" },
      { storeGUID: "g-p", storeCode: "1003", storeName: "Parramatta" },
      { storeGUID: "g-c", storeCode: "1015", storeName: "Campbelltown" },
      { storeGUID: "g-w", storeCode: "WH01", storeName: "仓库", isActive: false },
    ];
    const storeEvents: string[] = [];
    const renderStores = (
      overrides: Partial<React.ComponentProps<typeof AccessStoresSection>> = {},
    ) =>
      render(
        React.createElement(AccessStoresSection, {
          stores,
          // 草稿：Bankstown 管理、Liverpool 查看、Parramatta 新分配（未保存）。
          assignments: [
            { storeGUID: "g-b", isPrimary: true },
            { storeGUID: "g-l", isPrimary: false },
            { storeGUID: "g-p", isPrimary: false },
          ],
          baselineAssignments: [
            { storeGUID: "g-b", isPrimary: true },
            { storeGUID: "g-l", isPrimary: false },
          ],
          editable: true,
          canGrantManage: true,
          canManagePos: true,
          isAdmin: true,
          onChangeState: (guid: string, state: string) => storeEvents.push(`${guid}:${state}`),
          onOpenPos: (store: { storeCode: string }) => storeEvents.push(`pos:${store.storeCode}`),
          ...overrides,
        }),
      );

    await renderStores();
    assert.equal(all("Searchbar").length, 1, "5 家分店显示搜索框");
    const labels = (guid: string) =>
      all("Pressable")
        .filter((node) => String(node.props.accessibilityLabel ?? "").startsWith(guid))
        .map((node) => String(node.props.accessibilityLabel));
    assert.deepEqual(labels("Bankstown"), ["Bankstown 未分配", "Bankstown 查看", "Bankstown 管理"], "管理员看到三档");
    const bankstownManage = byLabel("Pressable", "Bankstown 管理")!;
    assert.equal(bankstownManage.props.accessibilityState.selected, true, "Bankstown 当前是「管理」");
    await act(() => byLabel("Pressable", "Liverpool 管理")!.props.onPress());
    await act(() => byLabel("Pressable", "Parramatta 未分配")!.props.onPress());
    await act(() => byLabel("Pressable", "Liverpool 查看")!.props.onPress());
    assert.deepEqual(storeEvents, ["g-l:manage", "g-p:unassigned"], "点已选中的档不重复触发，其余触发 onChangeState");
    // 收银权限入口：已保存的分店可点；刚在草稿里分配的先保存；未分配的没有入口。
    assert.ok(texts().includes("收银权限 ›"));
    assert.equal(all("Pressable").filter((n) => String(n.props.accessibilityLabel).startsWith("收银权限 ")).length, 2, "只有已保存且已分配的 2 家分店有入口");
    assert.ok(texts().includes("保存后可设置收银权限"), "草稿里新分配的分店要先保存");
    await act(() => byLabel("Pressable", "收银权限 Liverpool")!.props.onPress());
    assert.equal(storeEvents.at(-1), "pos:1012");
    assert.ok(texts().includes("1008"), "分店行显示编码");
    assert.ok(texts().includes(`WH01 · ${lookup("stores.disabledStore")}`), "已停用的分店带标注");
    assert.ok(texts().includes(lookup("stores.managerNotice")), "管理员看到店长派生说明");

    // 店长：只有两档；目标已是「管理」的分店整行只读；没有收银权限资格时没有入口。
    await renderStores({
      stores: stores.slice(0, 2),
      canGrantManage: false,
      isAdmin: false,
      canManagePos: false,
      assignments: [{ storeGUID: "g-b", isPrimary: true }, { storeGUID: "g-l", isPrimary: false }],
    });
    assert.equal(all("Searchbar").length, 0, "分店少时不显示搜索框");
    assert.deepEqual(labels("Liverpool"), ["Liverpool 未分配", "Liverpool 查看"], "店长看不到「管理」档");
    assert.equal(byLabel("Pressable", "Liverpool 查看")!.props.disabled, false);
    assert.ok(
      ["Bankstown 未分配", "Bankstown 查看", "Bankstown 管理"].every((label) => byLabel("Pressable", label)!.props.disabled === true),
      "目标已是「管理」时店长不能把它降级",
    );
    assert.ok(!texts().some((text) => text.includes("收银权限 ›")), "没有收银权限资格不显示入口");
    assert.ok(texts().includes(lookup("stores.scopeNotice")), "店长看到范围说明");

    // 搜索：按名称或编码过滤。
    await renderStores();
    await act(() => all("Searchbar")[0].props.onChangeText("campbell"));
    assert.deepEqual(
      all("Text").map(textOf).filter((text) => ["Bankstown", "Liverpool", "Campbelltown"].includes(text)),
      ["Campbelltown"],
    );
    await act(() => all("Searchbar")[0].props.onChangeText("zzz"));
    assert.ok(texts().includes(lookup("stores.noMatch")));

    // 授权筛选：全部 / 管理 / 查看 / 未分配，带台数；与搜索叠加；选中后改档不让行消失。
    await act(() => all("Searchbar")[0].props.onChangeText(""));
    const storeNames = () =>
      all("Text").map(textOf).filter((text) => ["Bankstown", "Liverpool", "Parramatta", "Campbelltown", "仓库"].includes(text));
    const chipLabels = () =>
      all("Pressable")
        .filter((node) => String(node.props.testID ?? "").startsWith("access-store-filter-"))
        .map((node) => String(node.props.accessibilityLabel));
    assert.deepEqual(chipLabels(), ["全部 5", "管理 1", "查看 2", "未分配 2"], "筛选芯片显示各档台数（按当前草稿）");
    const chip = (key: string) => all("Pressable").find((node) => node.props.testID === `access-store-filter-${key}`)!;
    await act(() => chip("view").props.onPress());
    assert.equal(chip("view").props.accessibilityState.selected, true);
    assert.deepEqual(storeNames(), ["Liverpool", "Parramatta"], "只看「查看」分店");
    await act(() => chip("manage").props.onPress());
    assert.deepEqual(storeNames(), ["Bankstown"], "只看「管理」分店");
    await act(() => chip("unassigned").props.onPress());
    assert.deepEqual(storeNames(), ["Campbelltown", "仓库"], "只看未分配分店（含已停用）");
    await act(() => all("Searchbar")[0].props.onChangeText("camp"));
    assert.deepEqual(storeNames(), ["Campbelltown"], "筛选与搜索叠加");
    // 草稿变化：Campbelltown 改成查看。台数实时更新，但「未分配」筛选里这一行仍在（直到重新点芯片）。
    await renderStores({
      assignments: [
        { storeGUID: "g-b", isPrimary: true },
        { storeGUID: "g-l", isPrimary: false },
        { storeGUID: "g-p", isPrimary: false },
        { storeGUID: "g-c", isPrimary: false },
      ],
    });
    assert.deepEqual(chipLabels(), ["全部 5", "管理 1", "查看 3", "未分配 1"], "改档后台数实时更新");
    assert.deepEqual(storeNames(), ["Campbelltown"], "改档后行不会立刻消失");
    await act(() => chip("unassigned").props.onPress());
    assert.deepEqual(storeNames(), [], "重新点芯片按最新分配取");
    await act(() => all("Searchbar")[0].props.onChangeText(""));
    await act(() => chip("all").props.onPress());
    assert.equal(storeNames().length, 5, "全部");
    // 店长（没有管理档、也没有管理分店）不显示「管理」芯片。
    await renderStores({ canGrantManage: false, isAdmin: false, assignments: [{ storeGUID: "g-l", isPrimary: false }] });
    assert.deepEqual(chipLabels(), ["全部 5", "查看 1", "未分配 4"], "店长看不到「管理」筛选");

    // ───────────── 角色分段 ─────────────
    const role = (name: string, id: string, description?: string) => ({ roleGUID: id, roleName: name, description });
    const roles = [
      role("StoreManager", "r-m"),
      role("StoreStaff", "r-s"),
      role("Finance", "r-f", "报表、现金管理、日结"),
      role("Admin", "r-a"),
    ];
    const roleEvents: string[] = [];
    const renderRoles = (overrides: Partial<React.ComponentProps<typeof AccessRolesSection>> = {}) =>
      render(
        React.createElement(AccessRolesSection, {
          roles,
          selectedRoleGuids: ["r-s"],
          hasManagedStoreAssignment: false,
          isAdmin: true,
          rolesEditable: true,
          storesEditable: true,
          canGrantStoreManagement: true,
          busy: false,
          onToggleRole: (guid: string) => roleEvents.push(`toggle:${guid}`),
          onGuideManagerGrant: () => roleEvents.push("guide"),
          ...overrides,
        }),
      );
    const rows = () => all("Pressable").filter((node) => node.props.accessibilityRole === "checkbox");

    await renderRoles();
    assert.equal(rows().length, 4);
    assert.equal(all("Searchbar").length, 0, "角色少时不显示搜索框");
    const managerRow = rows().find((node) => node.props.accessibilityLabel === "店长")!;
    assert.equal(managerRow.props.accessibilityState.checked, false);
    assert.equal(managerRow.props.disabled, false, "管理员点店长行是引导，所以可按");
    assert.ok(textOf(managerRow).includes(lookup("roles.derived")), "店长行带「由管理分店派生」标签");
    assert.ok(textOf(managerRow).includes(lookup("roles.derivedHintOff")));
    await act(() => managerRow.props.onPress());
    assert.deepEqual(roleEvents, ["guide"], "点店长行走引导，不会勾选");
    const adminRow = rows().find((node) => node.props.accessibilityLabel === "管理员")!;
    assert.ok(textOf(adminRow).includes(lookup("roles.highPrivilege")), "高权限角色带红色标签");
    await act(() => rows().find((node) => node.props.accessibilityLabel === "Finance")!.props.onPress());
    assert.deepEqual(roleEvents, ["guide", "toggle:r-f"]);

    // 有管理分店：店长行显示为已派生（已勾），仍不可勾选。
    await renderRoles({ hasManagedStoreAssignment: true });
    const derivedRow = rows().find((node) => node.props.accessibilityLabel === "店长")!;
    assert.equal(derivedRow.props.accessibilityState.checked, true);
    assert.ok(textOf(derivedRow).includes(lookup("roles.derivedHintOn")));

    // 店长只看到「店员」一行：可改；不显示搜索框；有范围说明；店长行即使出现也保持锁定。
    await renderRoles({ roles: [roles[1]], isAdmin: false, canGrantStoreManagement: false });
    assert.equal(rows().length, 1);
    assert.equal(all("Searchbar").length, 0, "店长只有一行，不显示搜索框");
    assert.ok(texts().includes(lookup("roles.managerLimit")));
    await act(() => rows()[0].props.onPress());
    assert.equal(roleEvents.at(-1), "toggle:r-s");
    await renderRoles({ roles: [roles[0]], isAdmin: false, canGrantStoreManagement: false });
    assert.equal(rows()[0].props.disabled, true, "非管理员的店长行保持锁定");
    await act(() => rows()[0].props.onPress());
    assert.equal(roleEvents.filter((event) => event === "guide").length, 1);

    // 角色很多时才出现搜索框。
    await renderRoles({
      roles: Array.from({ length: 7 }, (_, index) => role(`Role${index}`, `r-${index}`)),
    });
    assert.equal(all("Searchbar").length, 1);

    // ───────────── 直接权限分段 ─────────────
    const permissionEvents: string[] = [];
    const permission = (name: string, displayName: string, category: string) => ({
      name,
      displayName,
      category,
      isSystemPermission: false,
    });
    const categories = [
      {
        // 后端的分类名本身就是中文（权限本地化表也是按中文分类名建的）。
        category: "报表",
        displayName: "报表",
        permissions: [
          permission("Reports.Revenue.View", "营业额报表", "报表"),
          permission("Cash.Manage", "现金管理", "报表"),
          permission("Reports.Hours.View", "员工工时报表", "报表"),
        ],
      },
      {
        category: "员工",
        displayName: "员工",
        permissions: [permission("Staff.ReviewProfile", "员工资料审核", "员工")],
      },
    ];
    // 收银（POS）权限不在主分类里，单独成「收银权限 · 账号默认值」分组（账号级，对所有分店生效）。
    const posCategories = [
      {
        category: "收银",
        displayName: "收银",
        permissions: [permission("PosTerminal.Sales.AddItem", "收银加商品", "收银")],
      },
    ];
    const draft = {
      baselineCodes: ["Cash.Manage"],
      selectedCodes: ["Cash.Manage", "Staff.ReviewProfile"],
      inheritedPermissionCodes: ["Reports.Revenue.View"],
    };
    const renderPermissions = (overrides: Partial<React.ComponentProps<typeof AccessPermissionsSection>> = {}) =>
      render(
        React.createElement(AccessPermissionsSection, {
          categories,
          posCategories,
          draft,
          counts: { effective: 3, inherited: 1, direct: 2 },
          implicitAll: false,
          isAdmin: true,
          inheritedSourceMap: new Map([["Reports.Revenue.View", ["店长"]]]),
          editable: true,
          busy: false,
          onToggle: (code: string, checked: boolean) => permissionEvents.push(`${code}:${checked}`),
          onCopy: (direction: string) => permissionEvents.push(`copy:${direction}`),
          ...overrides,
        }),
      );
    await renderPermissions();
    const switchOf = (label: string) => byLabel("Switch", label)!;
    assert.ok(texts().includes("有效") && texts().includes("角色继承") && texts().includes("直接授权"));
    assert.deepEqual(
      all("Text").map(textOf).filter((text) => ["3", "1", "2"].includes(text)),
      ["3", "1", "2"],
      "顶部三个数字：有效 / 继承 / 直接",
    );
    // 默认展开有直接授权的分类（报表、员工都有），分类标题带「已开放 / 总数」。
    assert.ok(texts().includes("报表 · 2 / 3"), "分类标题 报表 · 2 / 3");
    assert.ok(texts().includes("员工 · 1 / 1"));
    assert.ok(texts().includes("继承自 店长"), "继承来的显示来源角色");
    assert.ok(texts().includes("直接授权"));
    assert.ok(texts().includes("未开放"));
    assert.equal(switchOf("营业额报表").props.value, true);
    assert.equal(switchOf("营业额报表").props.disabled, true, "继承来的开关置灰，要去角色里改");
    assert.equal(switchOf("员工工时报表").props.value, false);
    assert.equal(switchOf("员工工时报表").props.disabled, false);
    await act(() => switchOf("员工工时报表").props.onValueChange(true));
    await act(() => switchOf("现金管理").props.onValueChange(false));
    assert.deepEqual(permissionEvents, ["Reports.Hours.View:true", "Cash.Manage:false"]);
    // 收银默认值：默认折叠（没有直接授予的收银码），展开后能开关，说明里指向分店下的「收银权限」页。
    const posTitle = lookup("permissions.posDefaults.titleWithCount", { enabled: 0, total: 1 });
    assert.ok(texts().includes(posTitle), "底部有「收银权限 · 账号默认值」分组");
    assert.ok(!byLabel("Switch", "收银加商品"), "默认折叠，不渲染收银默认值的行");
    await act(() => byLabel("Pressable", lookup("permissions.posDefaults.title"))!.props.onPress());
    assert.ok(texts().includes(lookup("permissions.posDefaults.notice")), "展开后说明：账号级默认值，按分店调整去「收银权限」");
    assert.equal(switchOf("收银加商品").props.value, false);
    await act(() => switchOf("收银加商品").props.onValueChange(true));
    assert.deepEqual(permissionEvents.slice(-1), ["PosTerminal.Sales.AddItem:true"], "账号级收银默认值开关可用");
    permissionEvents.pop();
    // 复制入口。
    await act(() => byLabel("Button", "复制给他人")!.props.onPress());
    await act(() => byLabel("Button", "从同事复制")!.props.onPress());
    assert.deepEqual(permissionEvents.slice(-2), ["copy:toOthers", "copy:fromColleague"]);
    // 搜索按名称 / 权限码命中，并自动展开。
    await act(() => all("Searchbar")[0].props.onChangeText("审核"));
    assert.ok(texts().includes("员工 · 1 / 1"));
    assert.ok(!texts().some((text) => text.startsWith("报表")), "没命中的分类整体隐藏");
    await act(() => all("Searchbar")[0].props.onChangeText("nothing-here"));
    assert.ok(texts().includes(lookup("permissions.noMatch")));
    await act(() => all("Searchbar")[0].props.onChangeText(""));
    // 折叠：点分类标题收起 / 展开。
    await renderPermissions();
    await act(() => byLabel("Pressable", "报表 · 2 / 3")!.props.onPress());
    assert.ok(!byLabel("Switch", "营业额报表"), "收起后不再渲染该分类的行");
    await act(() => byLabel("Pressable", "报表 · 2 / 3")!.props.onPress());
    assert.ok(byLabel("Switch", "营业额报表"));
    // 管理员目标：默认拥有全部，开关全锁定，不提供复制。
    await renderPermissions({ implicitAll: true });
    assert.equal(switchOf("员工工时报表").props.disabled, true);
    assert.equal(switchOf("员工工时报表").props.value, true);
    assert.equal(byLabel("Button", "复制给他人"), undefined, "管理员默认拥有全部权限时不显示复制入口");
    assert.ok(texts().includes("管理员默认拥有"));
    // 店长：显示授权范围说明；保存中 / 只读时全部不可改。
    await renderPermissions({ isAdmin: false });
    assert.ok(texts().includes(lookup("permissions.managerLimit")));
    await renderPermissions({ busy: true });
    assert.equal(switchOf("员工工时报表").props.disabled, true);
    await renderPermissions({ editable: false });
    assert.equal(switchOf("员工工时报表").props.disabled, true);

    // ───────────── 菜单分段（管理员） ─────────────
    const menuEvents: string[] = [];
    const item = (key: string, title: string, over: Record<string, unknown> = {}) => ({
      key,
      title,
      permissionCodes: [`${key}.View`],
      visible: false,
      locked: false,
      fixed: false,
      requireAdmin: false,
      inherited: false,
      direct: false,
      ...over,
    });
    const menuItems = [
      item("settings", "我的", { visible: true, locked: true, fixed: true }),
      item("reports", "报表", { visible: true, locked: true, inherited: true }),
      item("device-management", "设备管理", { visible: true, direct: true }),
      item("cart", "购物车"),
    ];
    const renderMenu = (readOnly = false) =>
      render(
        React.createElement(AccessMenuSection, {
          model: {
            items: menuItems,
            sections: {
              bottom: [menuItems[0]],
              store: [menuItems[3]],
              operations: [menuItems[2]],
              reports: [menuItems[1]],
            },
            openCount: 3,
          },
          readOnly,
          onToggle: (menuItem: { key: string }, visible: boolean) => menuEvents.push(`${menuItem.key}:${visible}`),
        }),
      );
    await renderMenu();
    assert.ok(texts().includes(lookup("menu.sections.bottom")) && texts().includes(lookup("menu.sections.reports")));
    assert.ok(texts().includes(lookup("menu.fixed")) && texts().includes(lookup("menu.inherited")) && texts().includes(lookup("menu.direct")) && texts().includes(lookup("menu.hidden")));
    assert.equal(byLabel("Switch", "我的")!.props.disabled, true, "固定入口不能改");
    assert.equal(byLabel("Switch", "报表")!.props.disabled, true, "角色继承的菜单要去角色里改");
    await act(() => byLabel("Switch", "购物车")!.props.onValueChange(true));
    await act(() => byLabel("Switch", "设备管理")!.props.onValueChange(false));
    assert.deepEqual(menuEvents, ["cart:true", "device-management:false"]);
    await renderMenu(true);
    assert.equal(byLabel("Switch", "购物车")!.props.disabled, true, "只读时菜单开关全锁定");
  } finally {
    await act(() => root.unmount());
  }
}

run()
  .then(() => {
    console.log("access-sections-render.test.ts: ok");
  })
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
