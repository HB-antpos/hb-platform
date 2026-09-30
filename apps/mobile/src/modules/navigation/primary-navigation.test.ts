import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  buildPrimaryNavigation,
  resolveMeTabLabel,
  resolvePrimaryNavigationAction,
  shouldHidePrimaryTabBar,
} from "./primary-navigation";
import {
  areAllWorkbenchSectionsCollapsed,
  buildWorkbenchSections,
  toggleAllWorkbenchSections,
  toggleWorkbenchSectionCollapsed,
} from "./workbench";

const localeRoot = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../../locales"
);
const zhWorkbench = JSON.parse(
  readFileSync(resolve(localeRoot, "zh/screens/workbench.json"), "utf8")
);
const enWorkbench = JSON.parse(
  readFileSync(resolve(localeRoot, "en/screens/workbench.json"), "utf8")
);
const zhOrders = JSON.parse(
  readFileSync(resolve(localeRoot, "zh/screens/orders.json"), "utf8")
);
const enOrders = JSON.parse(
  readFileSync(resolve(localeRoot, "en/screens/orders.json"), "utf8")
);

assert.equal(zhWorkbench.routes.orders, "HB订单", "中文工作台必须显示 HB订单");
assert.equal(enWorkbench.routes.orders, "HB Orders", "英文工作台必须显示 HB Orders");
assert.equal(zhWorkbench.routes.containerNewProducts, "HB新品", "中文工作台必须显示 HB新品");
assert.equal(enWorkbench.routes.containerNewProducts, "HB new arrivals", "英文工作台必须显示 HB new arrivals");
assert.equal(zhWorkbench.routes.appInstall, "App 安装", "中文工作台必须显示 App 安装");
assert.equal(enWorkbench.routes.appInstall, "Install app", "英文工作台必须显示 Install app");
assert.equal(zhOrders.title, "订单列表", "中文订单业务页标题不得随工作台入口改名");
assert.equal(enOrders.title, "Orders", "英文订单业务页标题不得随工作台入口改名");

function compactPrimaryItems(
  activeRouteName: string | undefined,
  visibleRouteNames: string[],
  isDeviceMode = false
) {
  return buildPrimaryNavigation({
    activeRouteName,
    visibleRouteNames,
    isDeviceMode,
  }).map((item) => ({
    key: item.key,
    targetRouteName: item.targetRouteName,
    labelKey: item.labelKey,
    icon: item.icon,
    active: item.active,
  }));
}

const fullMenu = [
  "home",
  "orders",
  "cart",
  "product-query",
  "warehouse",
  "domestic-purchase",
  "local-supplier-invoices",
  "advertisements",
  "promotions",
  "reports",
  "attendance-personal",
  "attendance-management",
  "users",
  "employee-profile-review",
  "device-management",
  "employee-profile",
  "settings",
];

const accountPrimaryItems = compactPrimaryItems("workbench", fullMenu);
assert.deepEqual(
  accountPrimaryItems.map(({ icon: _icon, ...item }) => item),
  [
    {
      key: "workbench",
      targetRouteName: "workbench",
      labelKey: "tabs.workbench",
      active: true,
    },
    {
      key: "scan",
      targetRouteName: "product-query",
      labelKey: "tabs.scan",
      active: false,
    },
    {
      key: "attendance",
      targetRouteName: "attendance-personal",
      labelKey: "tabs.checkIn",
      active: false,
    },
    {
      key: "reports",
      targetRouteName: "reports",
      labelKey: "tabs.reports",
      active: false,
    },
    {
      key: "me",
      targetRouteName: "settings",
      labelKey: "tabs.me",
      active: false,
    },
  ],
  "全权限账号必须按工作台、扫码查询、打卡、报表、我的顺序显示五项"
);
assert.equal(
  accountPrimaryItems.every((item) => typeof item.icon === "string" && item.icon.length > 0),
  true,
  "五个一级入口必须都声明图标"
);
assert.match(
  accountPrimaryItems[2]?.icon ?? "",
  /clock/,
  "打卡入口必须使用时钟语义图标"
);
assert.equal(
  accountPrimaryItems[3]?.icon,
  "chart-box-outline",
  "报表入口必须使用清晰的报表语义图标"
);

assert.equal(
  compactPrimaryItems("orders", fullMenu)[0]?.active,
  true,
  "订单等业务页必须回归工作台上下文"
);
assert.equal(
  compactPrimaryItems("product-query", fullMenu)[1]?.active,
  true,
  "商品查询页必须高亮扫码查询"
);
assert.equal(
  compactPrimaryItems("promo-poster-editor", fullMenu)[1]?.active,
  true,
  "促销海报编辑页从扫码查询进入，必须继续高亮扫码查询"
);
assert.equal(
  compactPrimaryItems("promo-poster-queue", fullMenu)[1]?.active,
  true,
  "待打印海报页从扫码查询进入，必须继续高亮扫码查询"
);
assert.equal(
  resolvePrimaryNavigationAction(
    "promo-poster-queue",
    buildPrimaryNavigation({ activeRouteName: "promo-poster-queue", visibleRouteNames: fullMenu })[1]!
  ),
  "dismiss-to",
  "在海报子页点扫码查询必须弹回扫码页，而不是再推一个新页面"
);
assert.equal(
  compactPrimaryItems("product-insights", [...fullMenu, "product-insights"])[0]?.active,
  true,
  "商品进销查询作为商品功能子页必须回归工作台上下文，不能新增一级导航"
);
assert.equal(
  compactPrimaryItems("warehouse-product-insights", [
    ...fullMenu,
    "warehouse-product-insights",
  ])[0]?.active,
  true,
  "仓库商品进销查询同样从工作台进入，不能新增一级导航"
);
assert.equal(
  compactPrimaryItems("sales-orders", [...fullMenu, "sales-orders"])[0]?.active,
  true,
  "销售订单查询从工作台进入，不能新增一级导航"
);
assert.equal(
  compactPrimaryItems("warehouse-picking", [...fullMenu, "warehouse-picking"])[0]?.active,
  true,
  "订单拣货从工作台进入，不能新增一级导航"
);
assert.equal(shouldHidePrimaryTabBar("/warehouse-picking"), true, "拣货入口是 PDA 全屏作业，不显示全局底栏");
assert.equal(shouldHidePrimaryTabBar("/warehouse-picking/abc123/finish"), true, "拣货子页同样不显示全局底栏");
assert.equal(shouldHidePrimaryTabBar("/warehouse"), false, "仓库页等其它页面仍显示全局底栏");
assert.equal(shouldHidePrimaryTabBar("/warehouse-pickingx"), false, "只按完整路径段匹配，避免误伤相似路由");
assert.equal(
  compactPrimaryItems("pos-operation-logs", [...fullMenu, "pos-operation-logs"])[0]?.active,
  true,
  "员工操作日志从工作台进入，不能新增一级导航"
);
assert.equal(
  compactPrimaryItems("attendance-management", fullMenu)[2]?.active,
  true,
  "考勤管理页也必须高亮打卡，而不是改变打卡的个人考勤目标"
);
assert.equal(
  compactPrimaryItems("reports", fullMenu)[3]?.active,
  true,
  "报表中心必须高亮独立的报表入口"
);
assert.equal(
  compactPrimaryItems("employee-profile", fullMenu)[4]?.active,
  true,
  "个人资料页必须高亮我的"
);

const primaryItemsForNavigationActions = buildPrimaryNavigation({
  activeRouteName: "workbench",
  visibleRouteNames: fullMenu,
});
assert.equal(
  primaryItemsForNavigationActions.every((item) => !("locked" in item)),
  true,
  "可见一级导航不得再携带锁定展示状态"
);

function primaryItem(key: "workbench" | "scan" | "attendance" | "reports" | "me") {
  const item = primaryItemsForNavigationActions.find((candidate) => candidate.key === key);
  assert.ok(item, `必须存在 ${key} 一级入口`);
  return item;
}

assert.equal(
  resolvePrimaryNavigationAction("workbench", primaryItem("workbench")),
  "none",
  "已在工作台根页时重复点击不得产生重复导航"
);
assert.equal(
  resolvePrimaryNavigationAction("orders", primaryItem("workbench")),
  "dismiss-to",
  "工作台二级业务页点击工作台必须清理子栈并回到工作台根页"
);
assert.equal(
  resolvePrimaryNavigationAction("reports", primaryItem("workbench")),
  "dismiss-to",
  "报表页点击工作台必须清理报表栈并回到工作台根页"
);
assert.equal(
  resolvePrimaryNavigationAction(
    "attendance-management",
    buildPrimaryNavigation({
      activeRouteName: "attendance-management",
      visibleRouteNames: fullMenu,
    }).find((item) => item.key === "attendance")!
  ),
  "dismiss-to",
  "考勤管理作为打卡上下文子页时必须返回个人考勤根页"
);
assert.equal(
  resolvePrimaryNavigationAction(
    "employee-profile",
    buildPrimaryNavigation({
      activeRouteName: "employee-profile",
      visibleRouteNames: fullMenu,
    }).find((item) => item.key === "me")!
  ),
  "dismiss-to",
  "个人资料作为我的上下文子页时必须返回设置根页"
);
assert.equal(
  resolvePrimaryNavigationAction("orders", primaryItem("scan")),
  "navigate",
  "跨一级入口时必须导航到目标首页，而不能错误地清理当前业务栈"
);
assert.equal(
  resolvePrimaryNavigationAction("orders", primaryItem("reports")),
  "navigate",
  "业务页进入报表必须导航到现有报表根页"
);
assert.equal(
  resolvePrimaryNavigationAction(
    "reports",
    buildPrimaryNavigation({
      activeRouteName: "reports",
      visibleRouteNames: fullMenu,
    }).find((item) => item.key === "reports")!
  ),
  "none",
  "已在报表根页时重复点击不得产生重复导航"
);

const devicePrimaryItems = compactPrimaryItems(
  "product-query",
  [...fullMenu, "attendance-personal"],
  true
);
assert.deepEqual(
  devicePrimaryItems.map((item) => item.key),
  ["workbench", "scan", "me"],
  "设备模式即使收到个人考勤和报表菜单，也必须完全隐藏两个入口"
);
assert.equal(
  devicePrimaryItems.find((item) => item.key === "scan")?.active,
  true,
  "设备模式扫码查询必须保持可用且高亮"
);

const attendanceUnavailableItems = compactPrimaryItems(
  "workbench",
  fullMenu.filter((routeName) => routeName !== "attendance-personal")
);
assert.deepEqual(
  attendanceUnavailableItems.map((item) => item.key),
  ["workbench", "scan", "reports", "me"],
  "只有考勤管理权限但没有个人考勤菜单时，不得显示个人打卡入口"
);

const attendanceManagementOnlyItems = compactPrimaryItems(
  "attendance-management",
  ["workbench", "attendance-management", "settings"]
);
assert.deepEqual(
  attendanceManagementOnlyItems.map((item) => ({
    key: item.key,
    active: item.active,
  })),
  [
    { key: "workbench", active: true },
    { key: "me", active: false },
  ],
  "个人打卡主入口不可见时，合法考勤管理页必须回落到工作台上下文"
);

const reportsUnavailableItems = compactPrimaryItems(
  "workbench",
  fullMenu.filter((routeName) => routeName !== "reports")
);
assert.deepEqual(
  reportsUnavailableItems.map((item) => item.key),
  ["workbench", "scan", "attendance", "me"],
  "没有报表菜单时必须完全隐藏报表入口"
);

const orderAccountItems = compactPrimaryItems("workbench", [
  "home",
  "orders",
  "cart",
  "product-query",
  "local-supplier-invoices",
  "settings",
]);
assert.deepEqual(
  orderAccountItems.map((item) => item.key),
  ["workbench", "scan", "me"],
  "无打卡与报表权限的账号只能显示工作台、扫码查询和我的"
);

assert.deepEqual(
  compactPrimaryItems("workbench", ["home", "settings"]).map((item) => item.key),
  ["workbench", "me"],
  "没有任何可用业务主入口时只显示两个本地安全壳"
);

const sparseSections = buildWorkbenchSections([
  "orders",
  "warehouse",
  "reports",
  "users",
  "user-admin",
  "roles",
]);
assert.deepEqual(
  sparseSections.map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    { key: "product-purchasing", itemRouteNames: ["orders"] },
    { key: "warehouse-purchase", itemRouteNames: ["warehouse"] },
    { key: "operations-reports", itemRouteNames: ["reports"] },
    { key: "people-management", itemRouteNames: ["users", "user-admin", "roles"] },
  ],
  "工作台仅按显式可见路由显示各类业务入口"
);

const salesAndSupplierInvoiceSections = buildWorkbenchSections([
  "local-supplier-invoices",
  "warehouse",
  "orders",
]);
assert.deepEqual(
  salesAndSupplierInvoiceSections.map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "product-purchasing",
      itemRouteNames: ["orders", "local-supplier-invoices"],
    },
    { key: "warehouse-purchase", itemRouteNames: ["warehouse"] },
  ],
  "供应商发票必须归入商品进货并紧跟 HB订单，仓库与采购不得再包含该入口"
);
assert.deepEqual(
  buildWorkbenchSections(["container-new-products"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "product-purchasing", itemRouteNames: ["container-new-products"] }],
  "HB新品必须在工作台商品进货分区显示"
);
assert.deepEqual(
  buildWorkbenchSections(["app-install", "app-downloads"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "people-management", itemRouteNames: ["app-install", "app-downloads"] }],
  "App 安装必须在人员与管理分区、紧挨版本管理之前显示"
);
assert.deepEqual(
  buildWorkbenchSections(["product-query", "product-insights"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "product-sales", itemRouteNames: ["product-query", "product-insights"] }],
  "商品查询与商品进销查询必须同属商品销售，并且只依赖后端显式菜单"
);
assert.deepEqual(
  buildWorkbenchSections(["orders", "sales-orders"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    { key: "product-purchasing", itemRouteNames: ["orders"] },
    { key: "product-sales", itemRouteNames: ["sales-orders"] },
  ],
  "HB订单归入商品进货、销售订单归入商品销售，同样只依赖后端显式菜单"
);
assert.equal(
  buildWorkbenchSections(["orders"]).some((section) =>
    section.items.some((item) => item.routeName === "sales-orders")
  ),
  false,
  "后端菜单未显式下发 sales-orders 时工作台不得自行显示销售订单入口"
);
assert.deepEqual(
  buildWorkbenchSections(["warehouse", "warehouse-product-insights"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "warehouse-purchase",
      itemRouteNames: ["warehouse", "warehouse-product-insights"],
    },
  ],
  "仓库商品进销查询必须归入仓库与采购并紧跟仓库入口，同样只依赖后端显式菜单"
);
assert.deepEqual(
  buildWorkbenchSections(["warehouse", "warehouse-picking", "warehouse-product-insights"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "warehouse-purchase",
      itemRouteNames: ["warehouse", "warehouse-picking", "warehouse-product-insights"],
    },
  ],
  "订单拣货归入仓库与采购并紧跟仓库入口，只依赖后端显式菜单"
);
assert.deepEqual(
  buildWorkbenchSections(["warehouse", "containers", "warehouse-picking"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "warehouse-purchase",
      itemRouteNames: ["warehouse", "containers", "warehouse-picking"],
    },
  ],
  "货柜管理必须作为独立入口归入仓库与采购，并紧跟商品和货位管理"
);
assert.deepEqual(
  buildWorkbenchSections(["containers"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "warehouse-purchase", itemRouteNames: ["containers"] }],
  "只有 Container.View 的账号也要在仓库与采购分区看到货柜管理"
);
assert.deepEqual(
  buildWorkbenchSections(["reports", "pos-operation-logs"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "operations-reports",
      itemRouteNames: ["reports", "pos-operation-logs"],
    },
  ],
  "员工操作日志必须归入运营与报表并紧跟报表中心，只依赖后端显式菜单"
);
assert.deepEqual(
  buildWorkbenchSections(["local-supplier-invoices"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "product-purchasing",
      itemRouteNames: ["local-supplier-invoices"],
    },
  ],
  "只有供应商发票权限时必须只生成商品进货分组"
);
assert.deepEqual(
  buildWorkbenchSections([
    "product-query",
    "product-insights",
    "seasonal-product-insights",
    "container-new-products",
    "price-updates",
    "home",
    "cart",
    "orders",
    "sales-orders",
    "local-supplier-invoices",
    "installment-orders",
    "store-vouchers",
    "seasonal-cards",
  ]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [
    {
      key: "product-purchasing",
      itemRouteNames: ["container-new-products", "home", "cart", "orders", "local-supplier-invoices"],
    },
    {
      key: "product-sales",
      itemRouteNames: [
        "product-query",
        "product-insights",
        "seasonal-product-insights",
        "price-updates",
        "sales-orders",
        "installment-orders",
        "store-vouchers",
        "seasonal-cards",
      ],
    },
  ],
  "原销售与商品分组拆为商品进货在前、商品销售在后，入口不得遗漏或重复"
);

const visibleSectionRoutes = sparseSections.flatMap((section) =>
  section.items.map((item) => item.routeName)
);
assert.deepEqual(
  visibleSectionRoutes,
  ["orders", "warehouse", "reports", "users", "user-admin", "roles"],
  "工作台不得从角色或默认配置推断未授权入口"
);
assert.deepEqual(
  buildWorkbenchSections([]),
  [],
  "空菜单必须 fail-closed，不得泄露任何工作台业务入口"
);
assert.deepEqual(
  buildWorkbenchSections(["task-center", "unknown-route"]),
  [],
  "任务中心和未知路由不得成为工作台入口"
);

assert.equal(
  resolveMeTabLabel({ fullName: "张伟", username: "zhangwei", fallbackLabel: "我的" }),
  "张伟",
  "底栏「我的」优先显示姓名"
);
assert.equal(
  resolveMeTabLabel({ fullName: "  ", username: "zhangwei", fallbackLabel: "我的" }),
  "zhangwei",
  "姓名为空白时回落到用户名"
);
assert.equal(
  resolveMeTabLabel({ fallbackLabel: "我的" }),
  "我的",
  "未登录时保持「我的」"
);
assert.equal(
  resolveMeTabLabel({
    fullName: "张伟",
    username: "zhangwei",
    isDeviceMode: true,
    fallbackLabel: "我的",
  }),
  "我的",
  "设备模式没有个人账号，不显示残留的用户名"
);

// 工作台分组折叠：默认全部展开；单组切换互不影响；全部折叠/展开只作用于当前可见分组。
{
  const sections = buildWorkbenchSections(["orders", "product-query", "warehouse"]);
  const none = new Set<string>();
  assert.equal(areAllWorkbenchSectionsCollapsed(sections, none), false, "默认空集合即全部展开");
  assert.equal(areAllWorkbenchSectionsCollapsed([], none), false, "没有分组时不得显示为已全部折叠");

  const oneCollapsed = toggleWorkbenchSectionCollapsed(none, "product-sales");
  assert.deepEqual([...oneCollapsed], ["product-sales"]);
  assert.equal(none.size, 0, "切换分组不得修改原集合");
  assert.deepEqual(
    [...toggleWorkbenchSectionCollapsed(oneCollapsed, "product-sales")],
    [],
    "再次点按同一分组恢复展开"
  );

  const allCollapsed = toggleAllWorkbenchSections(sections, oneCollapsed);
  assert.deepEqual(
    [...allCollapsed].sort(),
    ["product-purchasing", "product-sales", "warehouse-purchase"],
    "部分折叠时点全部折叠，折叠全部可见分组"
  );
  assert.equal(areAllWorkbenchSectionsCollapsed(sections, allCollapsed), true);
  assert.deepEqual([...toggleAllWorkbenchSections(sections, allCollapsed)], [], "已全部折叠时点全部展开清空");

  assert.equal(
    areAllWorkbenchSectionsCollapsed(sections, new Set(["product-purchasing", "product-sales", "people-management"])),
    false,
    "失效分组键不得让可见分组被判定为全部折叠"
  );
}

{
  const workbenchScreenSource = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), "../workbench/workbench-screen.tsx"),
    "utf8"
  );
  assert.match(
    workbenchScreenSource,
    /useState<ReadonlySet<string>>\(\s*\(\) => new Set\(\)\s*\)/,
    "工作台分组必须默认全部展开"
  );
  assert.match(
    workbenchScreenSource,
    /accessibilityState=\{\{ expanded \}\}[\s\S]*toggleWorkbenchSectionCollapsed\(current, section\.key\)/,
    "分组标题必须可点按折叠，并向读屏暴露展开状态"
  );
  assert.match(
    workbenchScreenSource,
    /sections\.length > 1 \?[\s\S]*toggleAllWorkbenchSections\(sections, current\)/,
    "多个分组时必须提供全部折叠/展开"
  );
  assert.match(
    workbenchScreenSource,
    /\{expanded \? section\.items\.map/,
    "折叠的分组不得渲染其功能入口"
  );
}
