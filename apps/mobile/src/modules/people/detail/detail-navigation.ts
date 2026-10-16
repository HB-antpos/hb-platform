/**
 * 详情页发出的跨页路由与参数（契约见人员管理合并规格 2b）。
 * 全部是纯函数：页面里统一写
 *   router.push(route as unknown as Parameters<typeof router.push>[0])
 * （项目里 expo-router 没有生成强类型路由）。
 */

export interface DetailRouteTarget {
  userGuid: string;
  storeCode?: string;
  /** 以下只是给下游页面做首屏预填，下游页面必须自己按 userGuid 拉取目标，不得用它们做权限判断。 */
  userName?: string;
  username?: string;
  active?: boolean;
  roleNames?: readonly string[];
}

export type AccessSection = "stores" | "roles" | "permissions" | "menu";

export interface RoutePush {
  pathname: string;
  params: Record<string, string>;
}

function prefillParams(target: DetailRouteTarget): Record<string, string> {
  const params: Record<string, string> = {};
  if (target.storeCode) params.storeCode = target.storeCode;
  if (target.userName) params.userName = target.userName;
  if (target.username) params.username = target.username;
  if (target.active !== undefined) params.targetStatus = target.active ? "1" : "0";
  if (target.roleNames?.length) params.roleNames = target.roleNames.join("|");
  return params;
}

export function buildDetailRoute(target: DetailRouteTarget): RoutePush {
  return { pathname: "/users/[userGuid]", params: { userGuid: target.userGuid, ...prefillParams(target) } };
}

export function buildEditRoute(target: DetailRouteTarget): RoutePush {
  return {
    pathname: "/users/[userGuid]/edit",
    params: { userGuid: target.userGuid, ...(target.storeCode ? { storeCode: target.storeCode } : {}) },
  };
}

export function buildAccessRoute(target: DetailRouteTarget, options: { section?: AccessSection; copy?: boolean } = {}): RoutePush {
  return {
    pathname: "/users/[userGuid]/access",
    params: {
      userGuid: target.userGuid,
      ...prefillParams(target),
      section: options.section ?? "stores",
      ...(options.copy ? { copy: "1" } : {}),
    },
  };
}

/** 收银权限：storeCode 必填；storeGuid / storeName 能解析到就一并带上，降低下游页面再查一遍的成本。 */
export function buildPosRoute(
  target: DetailRouteTarget & { storeCode: string },
  store: { storeGuid?: string; storeName?: string } = {},
): RoutePush {
  return {
    pathname: "/users/[userGuid]/pos-terminal-permissions",
    params: {
      userGuid: target.userGuid,
      ...prefillParams(target),
      ...(store.storeGuid ? { storeGuid: store.storeGuid } : {}),
      ...(store.storeName ? { storeName: store.storeName } : {}),
    },
  };
}

export function buildLoginRecordsRoute(userGuid: string): RoutePush {
  return { pathname: "/users/[userGuid]/login-records", params: { userGuid } };
}

/**
 * 旧路由壳 → 新路由：把旧页面收到的全部参数原样透传到 /users/[userGuid]（storeCode 与预填参数不丢），
 * userGuid 为 "new" 时转去新增页。
 */
export function buildLegacyRedirect(
  params: Record<string, string | string[] | undefined>,
): RoutePush {
  const passthrough: Record<string, string> = {};
  for (const [key, value] of Object.entries(params)) {
    const first = Array.isArray(value) ? value[0] : value;
    if (key !== "userGuid" && first !== undefined && first !== "") passthrough[key] = first;
  }
  const userGuid = (Array.isArray(params.userGuid) ? params.userGuid[0] : params.userGuid)?.trim() ?? "";
  if (userGuid === "new") {
    return { pathname: "/users/new", params: passthrough };
  }
  return { pathname: "/users/[userGuid]", params: { userGuid, ...passthrough } };
}
