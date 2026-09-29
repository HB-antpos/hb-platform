export type AppTabPath =
  | "/(shell)/workbench"
  | "/(shell)/home"
  | "/(shell)/orders"
  | "/(shell)/cart"
  | "/(shell)/warehouse"
  | "/(shell)/warehouse-picking"
  | "/(shell)/domestic-purchase"
  | "/(shell)/local-supplier-invoices"
  | "/(shell)/installment-orders"
  | "/(shell)/advertisements"
  | "/(shell)/promotions"
  | "/(shell)/reports"
  | "/(shell)/store-vouchers"
  | "/(shell)/seasonal-cards"
  | "/(shell)/attendance-personal"
  | "/(shell)/attendance-management"
  | "/(shell)/product-query"
  | "/(shell)/product-insights"
  | "/(shell)/price-updates"
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
  | "/(shell)/pos-operation-logs"
  | "/(shell)/device-management"
  | "/(shell)/app-downloads"
  | "/(shell)/wpf-versions"
  | "/(shell)/settings";

export const TAB_PATHS: Record<string, AppTabPath> = {
  workbench: "/(shell)/workbench",
  home: "/(shell)/home",
  orders: "/(shell)/orders",
  cart: "/(shell)/cart",
  warehouse: "/(shell)/warehouse",
  "warehouse-picking": "/(shell)/warehouse-picking",
  "domestic-purchase": "/(shell)/domestic-purchase",
  "local-supplier-invoices": "/(shell)/local-supplier-invoices",
  "installment-orders": "/(shell)/installment-orders",
  advertisements: "/(shell)/advertisements",
  promotions: "/(shell)/promotions",
  reports: "/(shell)/reports",
  "store-vouchers": "/(shell)/store-vouchers",
  "seasonal-cards": "/(shell)/seasonal-cards",
  "attendance-personal": "/(shell)/attendance-personal",
  "attendance-management": "/(shell)/attendance-management",
  "product-query": "/(shell)/product-query",
  "product-insights": "/(shell)/product-insights",
  "price-updates": "/(shell)/price-updates",
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
  "pos-operation-logs": "/(shell)/pos-operation-logs",
  "device-management": "/(shell)/device-management",
  "app-downloads": "/(shell)/app-downloads",
  "wpf-versions": "/(shell)/wpf-versions",
  settings: "/(shell)/settings",
};

export const SUPPORTED_TAB_ROUTE_NAMES = new Set(Object.keys(TAB_PATHS));
export const SETTINGS_FALLBACK_ROUTE_NAME = "settings";

const DEVICE_MODE_BLOCKED_ROUTE_NAMES = new Set([
  "app-downloads",
  "wpf-versions",
  "attendance-personal",
  "attendance-management",
  "employee-profile-review",
  // 设备会话没有店长/管理员角色，后端审计查询会直接拒绝，设备模式不展示入口。
  "pos-operation-logs",
  // 收银用户条码按操作人账号的可管理分店授权，设备会话不展示入口。
  "cash-register-users",
  // 季节商品查询按账号独立权限授权，设备会话没有该权限，接口会直接拒绝。
  "seasonal-product-insights",
  "device-management",
  "reports",
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
  const filteredRouteNames = isDeviceMode
    ? orderedRouteNames.filter((routeName) => !DEVICE_MODE_BLOCKED_ROUTE_NAMES.has(routeName))
    : orderedRouteNames;

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
