import type { IdentityUserDetail } from "@/modules/identity-admin/types";
import type { StoreUserProfile } from "@/modules/users/types";

/**
 * 详情页统一展示模型：把店长版（store-users profile，status 为数字）与管理员版
 * （identity-admin 用户详情，isActive 为布尔）适配成同一形状，页面只认这一份。
 */
export interface PersonView {
  userGuid: string;
  username: string;
  fullName?: string;
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
  email?: string;
  phone?: string;
  active: boolean;
  roleNames: string[];
  mustChangePassword?: boolean;
  /**
   * 是否允许登录。店长版取自 store-users 资料（缺省按允许）；管理员版取自 GET /Users/guid/{guid} 的 loginEnabled，
   * 旧后端 / 未查询时为 undefined：界面不标「不能登录」，也不展示管理员版的开关。
   */
  loginEnabled?: boolean;
  employmentType?: string;
  lastLoginAt?: string;
  lastLoginIp?: string;
  createdAt?: string;
  updatedAt?: string;
}

export function personFromStoreProfile(profile: StoreUserProfile): PersonView {
  return {
    userGuid: profile.userGUID,
    username: profile.username,
    fullName: profile.fullName,
    givenName: profile.givenName,
    middleName: profile.middleName,
    familyName: profile.familyName,
    nickname: profile.nickname,
    email: profile.email,
    phone: profile.phone,
    active: profile.status === 1,
    roleNames: profile.roleNames,
    mustChangePassword: profile.mustChangePassword,
    loginEnabled: profile.loginEnabled,
    employmentType: profile.employmentType,
    lastLoginAt: profile.lastLoginTime,
    lastLoginIp: profile.lastLoginIp,
    createdAt: profile.createdAt,
    updatedAt: profile.updatedAt,
  };
}

export function personFromIdentityUser(user: IdentityUserDetail): PersonView {
  return {
    userGuid: user.userGUID,
    username: user.username,
    fullName: user.fullName,
    givenName: user.givenName,
    middleName: user.middleName,
    familyName: user.familyName,
    nickname: user.nickname,
    email: user.email,
    phone: user.phone,
    active: user.isActive,
    roleNames: user.roleNames,
    loginEnabled: user.loginEnabled,
    lastLoginAt: user.lastLoginAt,
    lastLoginIp: user.lastLoginIp,
    createdAt: user.createdAt,
    updatedAt: user.updatedAt,
  };
}

// ───────────────────────── 路由参数 ─────────────────────────

type RawParam = string | string[] | undefined;

export function firstRouteParam(value: RawParam): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value;
  const trimmed = raw?.trim();
  return trimmed ? trimmed : undefined;
}

export interface DetailRouteParams {
  userGuid: string;
  storeCode?: string;
  /** 以下旧参数只作首屏预填（标题 / 状态占位），绝不参与任何权限判断。 */
  prefill: {
    userName?: string;
    username?: string;
    active?: boolean;
    roleNames: string[];
  };
}

export function parseDetailRouteParams(params: {
  userGuid?: RawParam;
  storeCode?: RawParam;
  userName?: RawParam;
  username?: RawParam;
  targetStatus?: RawParam;
  roleNames?: RawParam;
}): DetailRouteParams {
  const status = firstRouteParam(params.targetStatus);
  const roleValues = Array.isArray(params.roleNames) ? params.roleNames : params.roleNames ? [params.roleNames] : [];
  return {
    userGuid: firstRouteParam(params.userGuid) ?? "",
    storeCode: firstRouteParam(params.storeCode),
    prefill: {
      userName: firstRouteParam(params.userName),
      username: firstRouteParam(params.username),
      active: status === undefined ? undefined : status === "1",
      roleNames: roleValues.flatMap((item) => item.split("|")).map((item) => item.trim()).filter(Boolean),
    },
  };
}

/** 数据到达前的占位头部：只有名字 / 账号，没有任何可操作的信息。 */
export function buildPrefillTitle(prefill: DetailRouteParams["prefill"]): { name: string; username: string } | null {
  const name = prefill.userName ?? prefill.username;
  if (!name) return null;
  return { name, username: prefill.username ?? "" };
}
