// 账号保险库的纯逻辑（不依赖 React / 存储，便于单测）。
//
// 保险库只放「用户勾选了保存」的个人账号的休眠凭据（刷新令牌）；当前正在用的账号仍然走原来的活动槽位
// （hbweb_access_token / hbweb_refresh_token / hbweb_user），代码几乎不用动。
// 不存访问令牌（只有 15 分钟）、不存密码。

/** 最多保存几个账号。 */
export const VAULT_MAX_ENTRIES = 5;

/** 刷新令牌服务端有效期 7 天；留 1 小时余量，临近过期就直接提示「需重新登录」，不去白跑一次请求。 */
export const VAULT_TOKEN_TTL_MS = 7 * 24 * 60 * 60 * 1000 - 60 * 60 * 1000;

export interface VaultEntry {
  userGuid: string;
  username: string;
  fullName?: string;
  roleNames: string[];
  /** 展示用的分店名，如「Bankstown」。 */
  storeLabel?: string;
  /** 令牌是在哪个服务器签发的：不同服务器的账号不混用。 */
  apiHost: string;
  refreshToken: string;
  /** 刷新令牌签发（或最近一次轮转）的时间，毫秒。 */
  tokenIssuedAt: number;
  savedAt: number;
  lastUsedAt: number;
  /** 服务器明确拒绝过这个令牌（登录无效 / 账号被限制 / 已过期）：必须重新输入密码。 */
  needsRelogin?: boolean;
}

/** 用户标识比较一律忽略大小写与首尾空白（后端返回的 GUID 大小写不稳定）。 */
export function normalizeGuid(guid: string | null | undefined): string {
  return (guid ?? "").trim().toLowerCase();
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** 从存储里读回一条记录：字段缺失 / 类型不对就丢弃（宁可让用户重登，也不拿残缺凭据去请求）。 */
export function parseEntry(raw: unknown): VaultEntry | null {
  if (!raw || typeof raw !== "object") return null;
  const value = raw as Record<string, unknown>;
  if (
    !isNonEmptyString(value.userGuid) ||
    !isNonEmptyString(value.username) ||
    !isNonEmptyString(value.refreshToken) ||
    !isNonEmptyString(value.apiHost) ||
    !isFiniteNumber(value.tokenIssuedAt) ||
    !isFiniteNumber(value.savedAt) ||
    !isFiniteNumber(value.lastUsedAt)
  ) {
    return null;
  }
  return {
    userGuid: value.userGuid.trim(),
    username: value.username.trim(),
    fullName: isNonEmptyString(value.fullName) ? value.fullName.trim() : undefined,
    roleNames: Array.isArray(value.roleNames) ? value.roleNames.filter(isNonEmptyString) : [],
    storeLabel: isNonEmptyString(value.storeLabel) ? value.storeLabel.trim() : undefined,
    apiHost: value.apiHost.trim(),
    refreshToken: value.refreshToken,
    tokenIssuedAt: value.tokenIssuedAt,
    savedAt: value.savedAt,
    lastUsedAt: value.lastUsedAt,
    needsRelogin: value.needsRelogin === true ? true : undefined,
  };
}

export function findEntry(entries: readonly VaultEntry[], userGuid: string | null | undefined): VaultEntry | undefined {
  const target = normalizeGuid(userGuid);
  if (!target) return undefined;
  return entries.find((entry) => normalizeGuid(entry.userGuid) === target);
}

export type UpsertResult =
  | { status: "added" | "updated"; entries: VaultEntry[] }
  | { status: "full"; entries: VaultEntry[] };

/** 新增或更新一条；已满且是新账号时拒绝（不悄悄挤掉别人）。 */
export function upsertEntry(entries: readonly VaultEntry[], entry: VaultEntry): UpsertResult {
  const existing = findEntry(entries, entry.userGuid);
  if (existing) {
    return {
      status: "updated",
      entries: entries.map((item) => (item === existing ? { ...entry, savedAt: existing.savedAt } : item)),
    };
  }
  if (entries.length >= VAULT_MAX_ENTRIES) {
    return { status: "full", entries: [...entries] };
  }
  return { status: "added", entries: [...entries, entry] };
}

export function removeEntry(entries: readonly VaultEntry[], userGuid: string): VaultEntry[] {
  const target = normalizeGuid(userGuid);
  return entries.filter((entry) => normalizeGuid(entry.userGuid) !== target);
}

export function markNeedsRelogin(entries: readonly VaultEntry[], userGuid: string): VaultEntry[] {
  const target = normalizeGuid(userGuid);
  return entries.map((entry) => (normalizeGuid(entry.userGuid) === target ? { ...entry, needsRelogin: true } : entry));
}

/** 刷新成功后换上新令牌：清掉「需重新登录」，更新签发与使用时间。 */
export function withFreshToken(entry: VaultEntry, refreshToken: string, now: number): VaultEntry {
  return { ...entry, refreshToken, tokenIssuedAt: now, lastUsedAt: now, needsRelogin: undefined };
}

export type EntryState = "ok" | "expired" | "needsRelogin" | "otherHost";

/** 这条凭据现在能不能直接用来切换（不联网判断）。 */
export function describeEntry(entry: VaultEntry, now: number, currentApiHost: string): EntryState {
  if (entry.needsRelogin) return "needsRelogin";
  if (normalizeHost(entry.apiHost) !== normalizeHost(currentApiHost)) return "otherHost";
  if (now - entry.tokenIssuedAt > VAULT_TOKEN_TTL_MS) return "expired";
  return "ok";
}

/** 两个 API 主机是否相同（忽略大小写与结尾斜杠）。 */
export function sameApiHost(a: string, b: string): boolean {
  return normalizeHost(a) === normalizeHost(b);
}

function normalizeHost(host: string): string {
  return host.trim().toLowerCase().replace(/\/+$/, "");
}

/** 切换列表的显示顺序：最近用过的在前。 */
export function sortForDisplay(entries: readonly VaultEntry[]): VaultEntry[] {
  return [...entries].sort((a, b) => b.lastUsedAt - a.lastUsedAt);
}

/** 「退出当前账号」后自动切到谁：最近用过且还能直接用的那个。 */
export function pickNextUsableEntry(
  entries: readonly VaultEntry[],
  excludeGuid: string | null | undefined,
  now: number,
  currentApiHost: string
): VaultEntry | null {
  const excluded = normalizeGuid(excludeGuid);
  const candidates = sortForDisplay(entries).filter(
    (entry) => normalizeGuid(entry.userGuid) !== excluded && describeEntry(entry, now, currentApiHost) === "ok"
  );
  return candidates[0] ?? null;
}

export interface EntrySourceUser {
  userGUID?: string;
  userGuid?: string;
  username: string;
  fullName?: string;
  roleNames?: string[];
  storeNames?: string[];
}

/** 由当前登录用户构造保险库条目（令牌由调用方给）。 */
export function buildEntryFromUser(
  user: EntrySourceUser,
  refreshToken: string,
  apiHost: string,
  now: number,
  previous?: Pick<VaultEntry, "savedAt"> | null
): VaultEntry {
  return {
    userGuid: (user.userGUID || user.userGuid || "").trim(),
    username: user.username.trim(),
    fullName: user.fullName?.trim() || undefined,
    roleNames: (user.roleNames ?? []).filter(isNonEmptyString),
    storeLabel: user.storeNames?.find(isNonEmptyString)?.trim(),
    apiHost: apiHost.trim(),
    refreshToken,
    tokenIssuedAt: now,
    savedAt: previous?.savedAt ?? now,
    lastUsedAt: now,
  };
}
