import { isIdentitySessionAllowed } from "../identity-admin/user-logic";

interface IdentityAdminNavigationAccess {
  isAuthenticated: boolean;
  sessionKind: string;
  iosReviewOfflineGuardActive: boolean;
  isAdmin: boolean;
  canReadUsers: boolean;
  canReadRoles: boolean;
  menuReady: boolean;
}

/**
 * 合并后的「人员管理」入口路由名（人员 / 角色 / 权限三个页签都在 /users 里）。
 * 旧的 user-admin / roles / permissions 菜单项只保留为重定向壳，不再单独出现在工作台。
 */
export const PEOPLE_ENTRY_ROUTE_NAME = "users";

export function resolveIdentityAdminRouteNames(
  routeNames: Iterable<string>,
  access: IdentityAdminNavigationAccess,
): string[] {
  const routes = Array.from(routeNames);
  const identityRoutes = [
    { name: "user-admin", allowed: isIdentitySessionAllowed(access, access.canReadUsers) },
    { name: "roles", allowed: isIdentitySessionAllowed(access, access.canReadRoles) },
    // 权限管理与 Web 端 /system/permissions 一样只认 Roles.View。
    { name: "permissions", allowed: isIdentitySessionAllowed(access, access.canReadRoles) },
  ];
  const result = routes.filter((name) =>
    identityRoutes.every((route) => route.name !== name || route.allowed),
  );

  // 服务端对管理员开放完整菜单。旧服务尚未注册新路由时，只补齐管理员入口；
  // 普通账号仍服从服务端菜单，加载失败或只有安全壳时也不能推断业务授权。
  const canCompleteAdminMenu = access.isAdmin && access.menuReady
    && routes.some((name) => name !== "settings" && name !== "workbench");
  if (canCompleteAdminMenu) {
    for (const route of identityRoutes) {
      if (route.allowed && !result.includes(route.name)) result.push(route.name);
    }
  }

  // 合并入口兜底：只持有旧入口权限（用户管理 / 角色 / 权限）的账号，服务端菜单里未必有 users（例如只有 Roles.View），
  // 升级后仍要能从工作台进到人员管理。只在上面的会话门禁已通过（account 会话、非设备、非审核）的前提下补齐，
  // 所以设备、审核会话不会因此多出入口；已有 users 时不重复追加。
  const hasIdentityEntry = identityRoutes.some((route) => route.allowed && result.includes(route.name));
  if (hasIdentityEntry && !result.includes(PEOPLE_ENTRY_ROUTE_NAME)) {
    result.push(PEOPLE_ENTRY_ROUTE_NAME);
  }
  return result;
}
