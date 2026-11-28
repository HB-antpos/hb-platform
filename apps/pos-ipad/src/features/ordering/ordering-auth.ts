import { ORDERING_WEB_ORIGIN } from "./ordering-config";

/**
 * 订货模式的员工登录（hotbargain.vip 网页 Cookie 会话）。
 *
 * 中文注释：
 * 1. 用网站同一个 Cookie 会话登录接口（/api/Auth/session/login）。App 原生请求写下的 Cookie
 *    与网页窗口共用（iOS：系统 Cookie 存储 + sharedCookiesEnabled；安卓：同一个 CookieManager），
 *    所以登录后打开 /shop 就是已登录状态，不用在网页里再输一次密码。
 * 2. 登录后续期由网页自己做；App 原生层之后不再用这份 Cookie 发请求，免得两边各自续期把会话搞失效。
 * 3. 密码只在这一次请求里用，不落盘、不进网页。
 */
export type OrderingUser = Readonly<{
  userGuid: string;
  username: string;
  displayName: string;
}>;

export type OrderingAuthErrorCode = "INVALID_CREDENTIALS" | "NOT_ALLOWED" | "NETWORK" | "SERVER";

export class OrderingAuthError extends Error {
  public constructor(
    public readonly code: OrderingAuthErrorCode,
    /** 后端给的原话（如「用户名或密码错误」「账号已停用」），只在账号密码类错误时带。 */
    public readonly serverMessage: string | null = null,
  ) {
    super(code);
    this.name = "OrderingAuthError";
  }
}

export type OrderingFetch = (
  input: string,
  init: Readonly<{
    method: "GET" | "POST";
    headers: Record<string, string>;
    body?: string;
    credentials: "include";
  }>,
) => Promise<Readonly<{ status: number; ok: boolean; json(): Promise<unknown> }>>;

export const defaultOrderingFetch: OrderingFetch = (input, init) => fetch(input, init);

/** 与后端 BrowserExtensionAccessService.AdminRoles 一致：管理员不需要单独的订货权限。 */
const ADMIN_ROLES = new Set(["admin", "管理员", "superadmin", "超级管理员"]);
/** Permissions.OrderFront.View 的值就是 "OrderFront"。 */
const ORDER_FRONT_PERMISSION = "OrderFront";

type Envelope = Readonly<{ success?: unknown; data?: unknown; message?: unknown }>;

async function readEnvelope(response: Readonly<{ json(): Promise<unknown> }>): Promise<Envelope> {
  try {
    const body = await response.json();
    return body && typeof body === "object" ? (body as Envelope) : {};
  } catch {
    return {};
  }
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

/** 账号能不能进订货模式：管理员，或者有订货前台权限。后端各接口仍会再校验一遍。 */
export function canUseOrdering(user: Readonly<{ permissions?: unknown; roleNames?: unknown }>): boolean {
  if (asStringArray(user.roleNames).some((role) => ADMIN_ROLES.has(role.trim().toLowerCase()))) return true;
  return asStringArray(user.permissions).includes(ORDER_FRONT_PERMISSION);
}

function toOrderingUser(data: unknown): OrderingUser | null {
  if (!data || typeof data !== "object") return null;
  const record = data as Record<string, unknown>;
  // 后端序列化名是 userGUID；兼容 userGuid 写法（与员工移动端一致）。
  const rawGuid = typeof record.userGUID === "string" ? record.userGUID : record.userGuid;
  const userGuid = typeof rawGuid === "string" ? rawGuid.trim() : "";
  const username = typeof record.username === "string" ? record.username.trim() : "";
  if (!userGuid || !username) return null;
  const pick = (key: string) => (typeof record[key] === "string" ? (record[key] as string).trim() : "");
  return Object.freeze({
    userGuid,
    username,
    displayName: pick("nickname") || pick("fullName") || username,
  });
}

const JSON_HEADERS = Object.freeze({ Accept: "application/json", "Content-Type": "application/json" });

/**
 * 员工账号登录订货模式。成功返回员工信息；账号没有订货权限时会顺手退出刚建的会话再报错。
 */
export async function signInOrdering(
  credentials: Readonly<{ username: string; password: string }>,
  fetchImpl: OrderingFetch = defaultOrderingFetch,
): Promise<OrderingUser> {
  let loginResponse;
  try {
    loginResponse = await fetchImpl(`${ORDERING_WEB_ORIGIN}/api/Auth/session/login`, {
      method: "POST",
      headers: { ...JSON_HEADERS },
      // 必须声明 passwordFormat: "raw"：后端缺省按旧客户端的 clientSha256 处理，
      // PBKDF2 新哈希的账号会一律判「用户名或密码错误」，还会计入登录失败限流（与 Web / 员工移动端一致）。
      body: JSON.stringify({
        username: credentials.username.trim(),
        password: credentials.password,
        passwordFormat: "raw",
      }),
      credentials: "include",
    });
  } catch {
    throw new OrderingAuthError("NETWORK");
  }
  const login = await readEnvelope(loginResponse);
  if (login.success !== true) {
    if (loginResponse.status >= 500) throw new OrderingAuthError("SERVER");
    throw new OrderingAuthError(
      "INVALID_CREDENTIALS",
      typeof login.message === "string" && login.message.trim() ? login.message.trim() : null,
    );
  }

  let currentResponse;
  try {
    currentResponse = await fetchImpl(`${ORDERING_WEB_ORIGIN}/api/Auth/current`, {
      method: "GET",
      headers: { Accept: "application/json" },
      credentials: "include",
    });
  } catch {
    await signOutOrderingNative(fetchImpl);
    throw new OrderingAuthError("NETWORK");
  }
  const current = await readEnvelope(currentResponse);
  const user = current.success === true ? toOrderingUser(current.data) : null;
  if (!user) {
    await signOutOrderingNative(fetchImpl);
    throw new OrderingAuthError("SERVER");
  }
  const data = current.data as Record<string, unknown>;
  if (!canUseOrdering({ permissions: data.permissions, roleNames: data.roleNames })) {
    await signOutOrderingNative(fetchImpl);
    throw new OrderingAuthError("NOT_ALLOWED");
  }
  return user;
}

/**
 * 原生层兜底退出：作废 Cookie 里的会话并让服务器清 Cookie。失败不抛错（尽力而为）。
 * 正常退出优先在网页窗口里做（网页那份 Cookie 才是最新的），见 ordering-web-bridge.ts。
 */
export async function signOutOrderingNative(fetchImpl: OrderingFetch = defaultOrderingFetch): Promise<void> {
  try {
    await fetchImpl(`${ORDERING_WEB_ORIGIN}/api/Auth/session/logout`, {
      method: "POST",
      headers: { ...JSON_HEADERS },
      body: "{}",
      credentials: "include",
    });
  } catch {
    // 作废失败只意味着会话自然过期前还在服务器上；本机已回到首页，没有入口能继续用。
  }
}
