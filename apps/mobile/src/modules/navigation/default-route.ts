import { filterLegacyFaceRouteNames, isLegacyFaceRouteName } from "./face-hub";

/**
 * 「人员管理」合并前的三个入口：用户管理 / 角色管理 / 权限管理。路由文件保留为重定向壳（旧版 App、推送、书签仍可能指向它们），
 * 页面自己会重定向到 /users?tab=…；它们不再是工作台入口，也不能因为「不在可见菜单里」被 Shell 纠偏抢先弹回默认页。
 */
export const LEGACY_PEOPLE_ROUTE_NAMES = ["user-admin", "roles", "permissions"] as const;

export function isLegacyPeopleRouteName(routeName: string | undefined): boolean {
  return (LEGACY_PEOPLE_ROUTE_NAMES as readonly string[]).includes(routeName ?? "");
}

export type AppTabPath =
  | "/(shell)/workbench"
  | "/(shell)/home"
  | "/(shell)/orders"
  | "/(shell)/cart"
  | "/(shell)/warehouse"
  | "/(shell)/containers"
  | "/(shell)/warehouse-picking"
  | "/(shell)/domestic-purchase"
  | "/(shell)/local-supplier-invoices"
  | "/(shell)/installment-orders"
  | "/(shell)/advertisements"
  | "/(shell)/promotions"
  | "/(shell)/reports"
  | "/(shell)/traffic-stats"
  | "/(shell)/store-vouchers"
  | "/(shell)/store-cash"
  | "/(shell)/seasonal-cards"
  | "/(shell)/attendance-personal"
  | "/(shell)/attendance-management"
  | "/(shell)/product-query"
  | "/(shell)/product-insights"
  | "/(shell)/price-updates"
  | "/(shell)/pricing-strategies"
  | "/(shell)/warehouse-product-insights"
  | "/(shell)/seasonal-product-insights"
  | "/(shell)/container-new-products"
  | "/(shell)/sales-orders"
  | "/(shell)/users"
  | "/(shell)/user-admin"
  | "/(shell)/cash-register-users"
  | "/(shell)/roles"
  | "/(shell)/permissions"
  | "/(shell)/employee-profile"
  | "/(shell)/employee-profile-review"
  | "/(shell)/face-hub"
  | "/(shell)/face-unwelcome"
  | "/(shell)/face-match-settings"
  | "/(shell)/face-capture-results"
  | "/(shell)/notifications"
  | "/(shell)/legacy-employee-logs"
  | "/(shell)/daily-closes"
  | "/(shell)/device-management"
  | "/(shell)/app-install"
  | "/(shell)/app-downloads"
  | "/(shell)/wpf-versions"
  | "/(shell)/settings";

export const TAB_PATHS: Record<string, AppTabPath> = {
  workbench: "/(shell)/workbench",
  home: "/(shell)/home",
  orders: "/(shell)/orders",
  cart: "/(shell)/cart",
  warehouse: "/(shell)/warehouse",
  // 货柜管理是独立菜单入口；列表页在 containers 栈内，明细页仍由根 Stack 接管。
  containers: "/(shell)/containers",
  "warehouse-picking": "/(shell)/warehouse-picking",
  "domestic-purchase": "/(shell)/domestic-purchase",
  "local-supplier-invoices": "/(shell)/local-supplier-invoices",
  "installment-orders": "/(shell)/installment-orders",
  advertisements: "/(shell)/advertisements",
  promotions: "/(shell)/promotions",
  reports: "/(shell)/reports",
  "traffic-stats": "/(shell)/traffic-stats",
  "store-vouchers": "/(shell)/store-vouchers",
  "store-cash": "/(shell)/store-cash",
  "seasonal-cards": "/(shell)/seasonal-cards",
  "attendance-personal": "/(shell)/attendance-personal",
  "attendance-management": "/(shell)/attendance-management",
  "product-query": "/(shell)/product-query",
  "product-insights": "/(shell)/product-insights",
  "price-updates": "/(shell)/price-updates",
  "pricing-strategies": "/(shell)/pricing-strategies",
  "warehouse-product-insights": "/(shell)/warehouse-product-insights",
  "seasonal-product-insights": "/(shell)/seasonal-product-insights",
  "container-new-products": "/(shell)/container-new-products",
  "sales-orders": "/(shell)/sales-orders",
  users: "/(shell)/users",
  "user-admin": "/(shell)/user-admin",
  "cash-register-users": "/(shell)/cash-register-users",
  roles: "/(shell)/roles",
  permissions: "/(shell)/permissions",
  "employee-profile": "/(shell)/employee-profile",
  "employee-profile-review": "/(shell)/employee-profile-review",
  // 「人脸比对」合并入口：新版 App 菜单里只出现它。下面旧三个路由名保留在路径表里（深链、旧通知、历史书签），页面会重定向到合并页对应页签。
  "face-hub": "/(shell)/face-hub",
  "face-unwelcome": "/(shell)/face-unwelcome",
  "face-match-settings": "/(shell)/face-match-settings",
  "face-capture-results": "/(shell)/face-capture-results",
  notifications: "/(shell)/notifications",
  "legacy-employee-logs": "/(shell)/legacy-employee-logs",
  "daily-closes": "/(shell)/daily-closes",
  "device-management": "/(shell)/device-management",
  "app-install": "/(shell)/app-install",
  "app-downloads": "/(shell)/app-downloads",
  "wpf-versions": "/(shell)/wpf-versions",
  settings: "/(shell)/settings",
};

export const SUPPORTED_TAB_ROUTE_NAMES = new Set(Object.keys(TAB_PATHS));
export const SETTINGS_FALLBACK_ROUTE_NAME = "settings";

const DEVICE_MODE_BLOCKED_ROUTE_NAMES = new Set([
  // 安装页按账号独立权限授权，设备会话没有该权限，接口会直接拒绝。
  "app-install",
  "app-downloads",
  "wpf-versions",
  "attendance-personal",
  "attendance-management",
  "employee-profile-review",
  // 参考照片与命中抓拍是生物识别信息，人脸比对相关接口按账号权限授权，设备会话没有用户账号，设备模式不展示合并入口（及旧三个入口）。
  "face-hub",
  "face-unwelcome",
  "face-match-settings",
  // 抓拍结果含生物识别照片、通知收件箱按用户账号计算，设备会话都没有对应身份，设备模式不展示。
  "face-capture-results",
  "notifications",
  // 设备会话没有用户账号，老收银操作日志按账号可管理分店授权，设备模式不展示入口。
  "legacy-employee-logs",
  // 日结记录同样按账号权限与可查看分店授权，设备会话没有用户账号，设备模式不展示入口。
  "daily-closes",
  // 自动价格策略按账号的 PricingStrategy.View / Edit 授权，设备会话没有用户账号，设备模式不展示入口。
  "pricing-strategies",
  // 收银用户条码按操作人账号的可管理分店授权，设备会话不展示入口。
  "cash-register-users",
  // 设备会话没有用户账号，现金接口按账号权限与可操作分店授权，设备模式不展示入口。
  "store-cash",
  // 季节商品查询按账号独立权限授权，设备会话没有该权限，接口会直接拒绝。
  "seasonal-product-insights",
  "device-management",
  "reports",
  // 客流统计按账号的 FaceMatch.Traffic.View 与可查看门店授权，设备会话没有用户账号，设备模式不展示入口。
  "traffic-stats",
  "user-admin",
  "roles",
  // 权限管理与角色管理同属全局管理，设备会话没有管理员身份，服务层会直接拒绝。
  "permissions",
]);
const LEGACY_ATTENDANCE_ROUTE_NAME = "attendance";
export const SUPPORTED_APP_MENU_ROUTE_NAMES = new Set([
  ...Array.from(SUPPORTED_TAB_ROUTE_NAMES).filter(
    (routeName) => routeName !== "workbench"
  ),
  LEGACY_ATTENDANCE_ROUTE_NAME,
]);

export function expandAttendanceRouteNames(
  routeNames: Iterable<string>,
  includeAttendanceManagement = false
) {
  const expandedRouteNames: string[] = [];
  const pushUnique = (routeName: string) => {
    if (!expandedRouteNames.includes(routeName)) {
      expandedRouteNames.push(routeName);
    }
  };

  Array.from(routeNames).forEach((routeName) => {
    if (routeName === LEGACY_ATTENDANCE_ROUTE_NAME) {
      pushUnique("attendance-personal");
      if (includeAttendanceManagement) {
        pushUnique("attendance-management");
      }
      return;
    }

    pushUnique(routeName);
  });

  return expandedRouteNames;
}

interface AccountTabRouteNamesOptions {
  canCreateOrder?: boolean;
  isWarehouseStaffOnly?: boolean;
}

export function filterAccountTabRouteNames(
  routeNames: Iterable<string>,
  { canCreateOrder = false, isWarehouseStaffOnly = false }: AccountTabRouteNamesOptions = {}
) {
  const orderedRouteNames = Array.from(routeNames);
  if (!isWarehouseStaffOnly || canCreateOrder) {
    return orderedRouteNames;
  }

  // 纯仓库员工只有显式 Orders.Create 才能进入自己的专用购物车。
  return orderedRouteNames.filter((routeName) => routeName !== "cart");
}

interface VisibleTabRouteNamesOptions {
  routeNames: Iterable<string>;
  isDeviceMode?: boolean;
  canViewAttendanceManagement?: boolean;
  canManageAttendance?: boolean;
}

export function getVisibleTabRouteNames({
  routeNames,
  isDeviceMode = false,
  canViewAttendanceManagement,
  canManageAttendance = false,
}: VisibleTabRouteNamesOptions) {
  const orderedRouteNames = expandAttendanceRouteNames(
    routeNames,
    canViewAttendanceManagement ?? canManageAttendance
  );
  // 旧版 App 仍靠后端下发的「抓拍与比对 / 不受欢迎人员名单 / 人脸比对设置」三项；新版统一用 face-hub 一个入口，菜单里去掉旧三项。
  const menuRouteNames = filterLegacyFaceRouteNames(orderedRouteNames);
  const filteredRouteNames = isDeviceMode
    ? menuRouteNames.filter((routeName) => !DEVICE_MODE_BLOCKED_ROUTE_NAMES.has(routeName))
    : menuRouteNames;

  // 工作台和设置属于本地安全壳，不代表任何业务权限；业务入口仍完全来自后端菜单。
  return [
    "workbench",
    ...filteredRouteNames.filter(
      (routeName) => routeName !== "workbench" && routeName !== SETTINGS_FALLBACK_ROUTE_NAME
    ),
    SETTINGS_FALLBACK_ROUTE_NAME,
  ];
}

export function hasVisibleTabRoute(
  routeNames: Iterable<string>,
  routeName: string,
  options?: Omit<VisibleTabRouteNamesOptions, "routeNames">
) {
  return getVisibleTabRouteNames({ routeNames, ...options }).includes(routeName);
}

interface ResolveDefaultTabRouteOptions {
  isDeviceMode: boolean;
  isWarehouseStaffOnly?: boolean;
  routeNames: Iterable<string>;
}

interface ResolveTabRouteCorrectionOptions extends ResolveDefaultTabRouteOptions {
  currentRouteName: string | undefined;
  hasAppliedDefaultRoute: boolean;
}

export function resolveDefaultTabRoute({
  isDeviceMode,
  isWarehouseStaffOnly = false,
  routeNames,
}: ResolveDefaultTabRouteOptions): AppTabPath {
  return resolvePreferredDefaultTabRoute({
    isDeviceMode,
    isWarehouseStaffOnly,
    routeNames,
  }) ?? TAB_PATHS.workbench;
}

export function resolvePreferredDefaultTabRoute({
  isDeviceMode,
  isWarehouseStaffOnly = false,
  routeNames,
}: ResolveDefaultTabRouteOptions): AppTabPath | null {
  const orderedRouteNames = getVisibleTabRouteNames({ routeNames, isDeviceMode });
  if (isDeviceMode && orderedRouteNames.includes("product-query")) {
    return TAB_PATHS["product-query"];
  }

  if (isWarehouseStaffOnly && orderedRouteNames.includes("warehouse")) {
    return TAB_PATHS.warehouse;
  }

  return null;
}

export function resolveTabRouteCorrection({
  currentRouteName,
  hasAppliedDefaultRoute,
  isDeviceMode,
  isWarehouseStaffOnly = false,
  routeNames,
}: ResolveTabRouteCorrectionOptions): AppTabPath | null {
  if (!currentRouteName) {
    return null;
  }

  const orderedRouteNames = getVisibleTabRouteNames({ routeNames, isDeviceMode });
  const visibleRouteNames = new Set(orderedRouteNames);
  const defaultRoute = resolveDefaultTabRoute({
    isDeviceMode,
    isWarehouseStaffOnly,
    routeNames: orderedRouteNames,
  });
  const currentRoute = TAB_PATHS[currentRouteName];

  if (currentRouteName === LEGACY_ATTENDANCE_ROUTE_NAME) {
    return visibleRouteNames.has("attendance-personal")
      ? TAB_PATHS["attendance-personal"]
      : defaultRoute;
  }

  if (!currentRoute) {
    // 非 Tab 栈页面（例如货柜列表/明细）由根 Stack 接管，不能被 Tab 默认页纠偏抢走。
    return null;
  }

  if (isLegacyFaceRouteName(currentRouteName)) {
    // 旧三个人脸比对路由只是跳转壳：页面自己会重定向到 face-hub 对应页签，纠偏交给合并页
    // （没有权限时由合并页的可见性判断退回默认入口）。这里不能按「不在可见集合」直接纠偏，否则会抢在重定向之前把深链弹回默认页。
    return null;
  }

  if (isLegacyPeopleRouteName(currentRouteName)) {
    // 同上：旧的用户 / 角色 / 权限管理路由只是重定向壳，由页面跳到「人员管理」Hub 对应页签；
    // 没有对应权限的账号在 Hub 里会回落到「人员」页签，而不是被纠偏到默认页。
    return null;
  }

  if (!visibleRouteNames.has(currentRouteName)) {
    return defaultRoute;
  }

  if (
    !hasAppliedDefaultRoute
    && (currentRouteName === "home" || currentRouteName === "workbench")
    && currentRoute !== defaultRoute
  ) {
    return defaultRoute;
  }

  return null;
}
