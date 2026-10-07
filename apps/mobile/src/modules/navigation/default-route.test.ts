import {
  getVisibleTabRouteNames,
  expandAttendanceRouteNames,
  filterAccountTabRouteNames,
  resolveDefaultTabRoute,
  resolvePreferredDefaultTabRoute,
  resolveTabRouteCorrection,
  TAB_PATHS,
} from "./default-route";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

const warehouseManagerWithoutMenu = {
  roleNames: ["WarehouseManager"],
  permissions: [],
  appMenu: [] as string[],
};

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: true,
    routeNames: ["home", "orders", "product-query", "device-management", "settings"],
  }),
  "/(shell)/product-query",
  "device-bound login defaults to product query"
);

assertEqual(
  resolvePreferredDefaultTabRoute({
    isDeviceMode: true,
    routeNames: ["workbench", "settings"],
  }),
  null,
  "设备菜单恢复前不得把本地安全壳误认为设备首选业务入口"
);

assertEqual(
  resolvePreferredDefaultTabRoute({
    isDeviceMode: true,
    routeNames: ["workbench", "product-query", "settings"],
  }),
  "/(shell)/product-query",
  "设备菜单恢复后必须重新识别扫码查询首选入口"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    isWarehouseStaffOnly: true,
    routeNames: ["workbench", "warehouse", "settings"],
  }),
  "/(shell)/warehouse",
  "纯仓库员工有仓库权限时必须默认进入仓库"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    isWarehouseStaffOnly: true,
    routeNames: ["workbench", "settings"],
  }),
  "/(shell)/workbench",
  "纯仓库员工没有仓库入口时回退到工作台，而不是推断仓库权限"
);

assertEqual(
  TAB_PATHS.workbench,
  "/(shell)/workbench",
  "工作台必须注册为本地固定 Tab 路径"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: warehouseManagerWithoutMenu.appMenu,
  }),
  "/(shell)/workbench",
  "账号会话即使无业务菜单也必须进入 fail-closed 工作台"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: ["home", "attendance", "settings"],
  }),
  "/(shell)/workbench",
  "账号登录默认进入工作台"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: ["home", "attendance-personal", "attendance-management", "settings"],
  }),
  "/(shell)/workbench",
  "管理员菜单也不得把账号登录默认页改为打卡"
);

assertEqual(
  expandAttendanceRouteNames(["home", "attendance", "settings"], false).join(","),
  "home,attendance-personal,settings",
  "legacy attendance expands to personal attendance for normal users"
);

assertEqual(
  expandAttendanceRouteNames(["home", "attendance", "settings"], true).join(","),
  "home,attendance-personal,attendance-management,settings",
  "legacy attendance expands to personal and management attendance for managers"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["home", "attendance", "employee-profile-review", "device-management", "reports", "settings"],
    isDeviceMode: true,
    canViewAttendanceManagement: true,
  }).join(","),
  "workbench,home,settings",
  "设备模式必须保留工作台和我的安全壳，同时隐藏个人打卡、考勤管理、敏感资料、设备管理和报表"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["product-query", "attendance-personal"],
    isDeviceMode: true,
  }).join(","),
  "workbench,product-query,settings",
  "设备模式收到显式个人考勤菜单时也必须保留工作台和我的安全壳，并隐藏个人考勤"
);

assertEqual(
  TAB_PATHS["employee-profile-review"],
  "/(shell)/employee-profile-review",
  "employee profile review route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS["product-insights"],
  "/(shell)/product-insights",
  "商品进销查询必须注册为商品查询详情可返回的 Shell 子页"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["product-query", "product-insights"],
    isDeviceMode: true,
  }).join(","),
  "workbench,product-query,product-insights,settings",
  "设备模式拥有商品查询权限时必须保留商品进销查询入口"
);

assertEqual(
  TAB_PATHS["user-admin"],
  "/(shell)/user-admin",
  "全局用户管理入口必须注册为独立路由，不能复用门店员工入口"
);

assertEqual(
  TAB_PATHS.roles,
  "/(shell)/roles",
  "全局角色管理入口必须注册为独立路由"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["user-admin", "roles", "users", "settings"],
    isDeviceMode: true,
  }).join(","),
  "workbench,users,settings",
  "设备模式必须隐藏全局用户与角色管理，但保留既有门店员工流程"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "employee-profile-review",
    hasAppliedDefaultRoute: true,
    isDeviceMode: true,
    routeNames: ["employee-profile-review", "settings"],
  }),
  "/(shell)/workbench",
  "设备模式离开敏感资料审核后必须回到工作台安全壳"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["home", "attendance", "settings"],
    isDeviceMode: false,
    canViewAttendanceManagement: true,
  }).join(","),
  "workbench,home,attendance-personal,attendance-management,settings",
  "账号模式必须保留工作台安全壳，并展开 legacy 考勤到管理入口"
);

assertEqual(
  filterAccountTabRouteNames(["home", "orders", "cart", "settings"], {
    canCreateOrder: true,
    isWarehouseStaffOnly: true,
  }).join(","),
  "home,orders,cart,settings",
  "pure WarehouseStaff with Orders.Create keeps cart for dedicated warehouse cart"
);

assertEqual(
  filterAccountTabRouteNames(["home", "orders", "cart", "settings"], {
    canCreateOrder: false,
    isWarehouseStaffOnly: true,
  }).join(","),
  "home,orders,settings",
  "pure WarehouseStaff without Orders.Create removes cart even when app menu returns it"
);

assertEqual(
  filterAccountTabRouteNames(["home", "orders", "cart", "settings"], {
    isWarehouseStaffOnly: false,
  }).join(","),
  "home,orders,cart,settings",
  "normal account menu keeps cart visible"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: ["settings"],
  }),
  "/(shell)/workbench",
  "没有可见业务项的账号会话仍进入 fail-closed 工作台"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: ["home", "settings"],
  }),
  "/(shell)/workbench",
  "工作台是账号模式本地固定入口，不依赖后端菜单返回"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: true,
    routeNames: ["device-management", "reports", "settings"],
  }),
  "/(shell)/workbench",
  "设备模式没有扫码权限时不得回退到设备管理或报表，必须进入工作台安全壳"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: false,
    routeNames: ["device-management", "settings"],
  }),
  "/(shell)/workbench",
  "账号会话不得因菜单排序回退到设备管理页"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "device-management",
    hasAppliedDefaultRoute: false,
    isDeviceMode: true,
    routeNames: ["device-management", "settings"],
  }),
  "/(shell)/workbench",
  "设备模式离开设备管理后必须回到工作台安全壳"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "reports",
    hasAppliedDefaultRoute: false,
    isDeviceMode: true,
    routeNames: ["reports", "settings"],
  }),
  "/(shell)/workbench",
  "设备模式离开报表后必须回到工作台安全壳"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "attendance-personal",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "settings"],
  }),
  "/(shell)/workbench",
  "账号没有个人考勤菜单时直达打卡必须回到工作台"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "reports",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "settings"],
  }),
  "/(shell)/workbench",
  "账号没有报表菜单时直达报表必须回到工作台"
);

assertEqual(
  resolveDefaultTabRoute({
    isDeviceMode: true,
    routeNames: [],
  }),
  "/(shell)/workbench",
  "空菜单必须进入工作台安全壳，由工作台展示 fail-closed 状态"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "home",
    hasAppliedDefaultRoute: false,
    isDeviceMode: false,
    routeNames: ["home", "attendance", "settings"],
  }),
  "/(shell)/workbench",
  "启动时旧 home 地址必须校正到工作台"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "attendance",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "attendance", "settings"],
  }),
  "/(shell)/attendance-personal",
  "旧考勤路径必须固定重定向到个人考勤，而不是当前默认工作台"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "containers",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["containers", "attendance-personal", "settings"],
  }),
  null,
  "后端下发 containers 时货柜列表不能被默认页纠偏抢走"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "containers",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["warehouse", "attendance-personal", "settings"],
  }),
  "/(shell)/workbench",
  "货柜管理已是独立菜单入口，后端未下发 containers 时必须纠偏回工作台"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "container-guid-001",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["warehouse", "attendance-personal", "settings"],
  }),
  null,
  "root stack container detail is not redirected by tab default correction"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "home",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "attendance", "settings"],
  }),
  null,
  "manual home navigation is allowed after startup default was applied"
);

assertEqual(
  TAB_PATHS["local-supplier-invoices"],
  "/(shell)/local-supplier-invoices",
  "local supplier invoices route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS["installment-orders"],
  "/(shell)/installment-orders",
  "installment orders route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS.advertisements,
  "/(shell)/advertisements",
  "advertisements route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS.promotions,
  "/(shell)/promotions",
  "promotions route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS.reports,
  "/(shell)/reports",
  "reports route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS["store-vouchers"],
  "/(shell)/store-vouchers",
  "store vouchers route is registered as a valid tab path"
);

assertEqual(
  TAB_PATHS["store-cash"],
  "/(shell)/store-cash",
  "store cash route is registered as a valid tab path"
);

// 现金入口按账号权限与可操作分店授权：设备会话必须隐藏，账号会话照后端菜单显示。
assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["product-query", "store-cash"],
    isDeviceMode: true,
  }).join(","),
  "workbench,product-query,settings",
  "设备模式收到显式现金菜单时也必须隐藏现金入口"
);

assertEqual(
  getVisibleTabRouteNames({
    routeNames: ["store-vouchers", "store-cash"],
    isDeviceMode: false,
  }).join(","),
  "workbench,store-vouchers,store-cash,settings",
  "账号会话必须保留后端下发的现金入口"
);

assertEqual(
  TAB_PATHS["seasonal-cards"],
  "/(shell)/seasonal-cards",
  "seasonal cards route is registered as a valid tab path"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "local-supplier-invoices",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "local-supplier-invoices", "settings"],
  }),
  null,
  "local supplier invoices route is allowed when app menu exposes it"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "installment-orders",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "installment-orders", "settings"],
  }),
  null,
  "installment orders route is allowed when app menu exposes it"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "advertisements",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "advertisements", "settings"],
  }),
  null,
  "advertisements route is allowed when app menu exposes it"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "promotions",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "promotions", "settings"],
  }),
  null,
  "promotions route is allowed when app menu exposes it"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "reports",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "reports", "settings"],
  }),
  null,
  "reports route is allowed when app menu exposes it for account sessions"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "store-vouchers",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "store-vouchers", "settings"],
  }),
  null,
  "store vouchers route is allowed when app menu exposes it"
);

assertEqual(
  resolveTabRouteCorrection({
    currentRouteName: "seasonal-cards",
    hasAppliedDefaultRoute: true,
    isDeviceMode: false,
    routeNames: ["home", "seasonal-cards", "settings"],
  }),
  null,
  "seasonal cards route is allowed when app menu exposes it"
);
