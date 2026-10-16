// 人员列表的统一展示模型：店长的「本店店员」（store-users）与管理员的「全局用户」（Users/optimized）
// 数据结构不同（status 数字 vs isActive 布尔、分页方式不同），不能直接合并成一张表，
// 这里用适配层把两边都转成同一个 PersonListItem，列表行共用同一个 PersonListRow。
// 纯函数、不依赖 React Native，可在 Node 下测试；所有文案由调用方通过 formatters / ctx 传入（已本地化）。
import type { IdentityUser } from "../identity-admin/types";
import { isAccessAdminRoleName, isStoreManagerRoleName } from "../users/access-management";
import { hasDeliverableEmail } from "../users/staff-email-username";
import type { StoreUserListItem } from "../users/types";
import { resolvePersonName } from "./person-name";
import type { PeopleTone } from "./ui";

/** 卡片上只给「例外」的标签；正常账号不挂任何标签。 */
export type PersonTagKey = "pendingFirstLogin" | "cannotLogin" | "noEmail" | "disabled";

export interface PersonListBadge {
  label: string;
  tone: PeopleTone;
}

export interface PersonListItem {
  /** userGUID，列表 key。 */
  key: string;
  /** 主标题（姓名；缺省回落到全名 / 账号）。 */
  name: string;
  /** 昵称小字，与主标题相同或为空时为 null。 */
  nickname: string | null;
  /** 头像字母。 */
  initials: string;
  /** 停用账号：头像置灰、状态点变灰。 */
  inactive: boolean;
  /** 一行元信息：@账号 · …（已本地化、已拼好）。 */
  meta: string;
  tags: PersonTagKey[];
  /** 名字后面的角色徽标（管理员列表用）。 */
  badge: PersonListBadge | null;
  isSelf: boolean;
}

// ───────────────────────── 通用小工具 ─────────────────────────

/** 澳洲手机号 04xxxxxxxx 展示成「04xx xxx xxx」，其余原样（只做展示，不改数据）。 */
export function formatPhoneForList(phone: string | null | undefined): string {
  const text = (phone ?? "").trim();
  if (!text) return "";
  const digits = text.replace(/[\s-]/g, "");
  if (/^04\d{8}$/.test(digits)) {
    return `${digits.slice(0, 4)} ${digits.slice(4, 7)} ${digits.slice(7)}`;
  }
  return text;
}

function joinMeta(parts: (string | null | undefined)[]): string {
  return parts.map((part) => (part ?? "").trim()).filter(Boolean).join(" · ");
}

// ───────────────────────── 店员（store-users）适配 ─────────────────────────

/**
 * 店员卡片上的例外标签。
 * - 已停用：只给「已停用」（停用账号本来就不能登录，不再叠加其它标签）；
 * - 启用中：待首次登录（还没把初始 / 重置密码改成自己的）、不能登录（loginEnabled === false）、
 *   无邮箱（占位邮箱收不到验证码，找不回密码）。
 */
export function resolveStaffExceptionTags(user: Pick<StoreUserListItem, "status" | "mustChangePassword" | "loginEnabled" | "email">): PersonTagKey[] {
  if (user.status !== 1) return ["disabled"];
  const tags: PersonTagKey[] = [];
  if (user.mustChangePassword === true) tags.push("pendingFirstLogin");
  if (user.loginEnabled === false) tags.push("cannotLogin");
  if (!hasDeliverableEmail(user.email)) tags.push("noEmail");
  return tags;
}

export interface StaffPersonContext {
  /** 本人行：不提供任何操作，也不挂例外标签。 */
  isSelf: boolean;
  /** 「全部已分配分店」模式下带上所属分店；单店模式传 null。 */
  storeLabel: string | null;
  /** 非本人 = 员工类型（全职 / 兼职 / 临时工）；本人 = 角色（如店长）。由调用方本地化。 */
  typeLabel: string;
}

export function buildStaffPersonItem(user: StoreUserListItem, ctx: StaffPersonContext): PersonListItem {
  const name = resolvePersonName(user);
  return {
    key: user.userGUID,
    name: name.primary,
    nickname: name.nickname,
    initials: name.initials,
    inactive: user.status !== 1,
    meta: joinMeta([`@${user.username}`, ctx.storeLabel, ctx.typeLabel, formatPhoneForList(user.phone)]),
    tags: ctx.isSelf ? [] : resolveStaffExceptionTags(user),
    badge: null,
    isSelf: ctx.isSelf,
  };
}

// ───────────────────────── 全局用户（Users/optimized）适配 ─────────────────────────

const WAREHOUSE_MANAGER_ROLE_NAMES = new Set(["warehousemanager", "warehouse", "仓库经理", "仓库管理员", "warehouseadmin"]);

function normalizeRoleName(roleName: string) {
  return roleName.trim().toLowerCase();
}

export function isWarehouseManagerRoleName(roleName: string) {
  return WAREHOUSE_MANAGER_ROLE_NAMES.has(normalizeRoleName(roleName));
}

/** 角色标签颜色：管理员紫、店长蓝、仓库经理橙，其余（店员、订货员…）灰，一眼分清高权限账号。 */
export function resolveRoleTone(roleName: string): PeopleTone {
  if (isAccessAdminRoleName(roleName)) return "violet";
  if (isStoreManagerRoleName(roleName)) return "blue";
  if (isWarehouseManagerRoleName(roleName)) return "amber";
  return "gray";
}

const ROLE_TONE_PRIORITY: Record<PeopleTone, number> = {
  violet: 3,
  blue: 2,
  amber: 1,
  gray: 0,
  green: 0,
  red: 0,
};

/** 一个账号可能有多个角色，名字旁只放权限最高的那个（同级取列表里第一个）。 */
export function pickPrimaryRole(roleNames: readonly string[]): { roleName: string; tone: PeopleTone } | null {
  let best: { roleName: string; tone: PeopleTone } | null = null;
  for (const raw of roleNames) {
    const roleName = raw.trim();
    if (!roleName) continue;
    const tone = resolveRoleTone(roleName);
    if (!best || ROLE_TONE_PRIORITY[tone] > ROLE_TONE_PRIORITY[best.tone]) {
      best = { roleName, tone };
    }
  }
  return best;
}

/** 管理员列表「分店」那一段的语义（由调用方本地化成「全部分店」「Bankstown 管理 +2」…）。 */
export type UserStoreSummary =
  | { scope: "all" }
  | { scope: "none" }
  | { scope: "stores"; firstName: string; relation: "manage" | "view" | null; extraCount: number };

export function summarizeUserStores(user: Pick<IdentityUser, "roleNames" | "stores" | "storeNames">): UserStoreSummary {
  // 管理员可见全部分店，不按分店关联列出。
  if (user.roleNames.some(isAccessAdminRoleName)) return { scope: "all" };

  const linked = (user.stores ?? []).filter((store) => store.storeName.trim());
  if (linked.length) {
    // 「管理」的分店排在前面：一个人管 A、只看 B 时，卡片上优先体现「管 A」。
    const ordered = [...linked.filter((store) => store.isPrimary), ...linked.filter((store) => !store.isPrimary)];
    const first = ordered[0];
    return {
      scope: "stores",
      firstName: first.storeName.trim(),
      relation: first.isPrimary ? "manage" : "view",
      extraCount: ordered.length - 1,
    };
  }

  const names = (user.storeNames ?? []).map((name) => name.trim()).filter(Boolean);
  if (names.length) {
    // 旧数据只有分店名、没有管理 / 查看关系。
    return { scope: "stores", firstName: names[0], relation: null, extraCount: names.length - 1 };
  }
  return { scope: "none" };
}

// ───────────────────────── 最近登录（相对时间） ─────────────────────────

export type RelativeLogin =
  | { unit: "justNow" }
  | { unit: "minutes"; count: number }
  | { unit: "hours"; count: number }
  | { unit: "yesterday" }
  | { unit: "days"; count: number }
  | { unit: "months"; count: number }
  | { unit: "years"; count: number };

/** 后端时间字段按 UTC 解释；缺少时区标记的字符串补 Z，避免被当成设备本地时间（与报表模块同一口径）。 */
export function parseBackendUtc(value: string | null | undefined): number | null {
  const text = (value ?? "").trim();
  if (!text) return null;
  const hasZone = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(text);
  const timestamp = Date.parse(hasZone ? text : `${text}Z`);
  return Number.isFinite(timestamp) ? timestamp : null;
}

/** 从没登录过（或时间无法解析）返回 null；时钟略快于服务端（负差值）按「刚刚」处理。 */
export function resolveRelativeLogin(value: string | null | undefined, now: number): RelativeLogin | null {
  const timestamp = parseBackendUtc(value);
  if (timestamp === null) return null;
  const minutes = Math.max(0, Math.floor((now - timestamp) / 60_000));
  if (minutes < 1) return { unit: "justNow" };
  if (minutes < 60) return { unit: "minutes", count: minutes };
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return { unit: "hours", count: hours };
  const days = Math.floor(hours / 24);
  if (days < 2) return { unit: "yesterday" };
  if (days < 60) return { unit: "days", count: days };
  if (days < 365) return { unit: "months", count: Math.floor(days / 30) };
  return { unit: "years", count: Math.floor(days / 365) };
}

export interface IdentityPersonFormatters {
  roleName: (roleName: string) => string;
  stores: (summary: UserStoreSummary) => string;
  /** null = 从未登录。 */
  lastLogin: (login: RelativeLogin | null) => string;
}

export function resolveIdentityExceptionTags(user: Pick<IdentityUser, "isActive" | "email">): PersonTagKey[] {
  if (!user.isActive) return ["disabled"];
  return hasDeliverableEmail(user.email) ? [] : ["noEmail"];
}

export function buildIdentityPersonItem(
  user: IdentityUser,
  formatters: IdentityPersonFormatters,
  now: number,
): PersonListItem {
  const name = resolvePersonName(user);
  const primaryRole = pickPrimaryRole(user.roleNames);
  return {
    key: user.userGUID,
    name: name.primary,
    nickname: name.nickname,
    initials: name.initials,
    inactive: !user.isActive,
    meta: joinMeta([
      `@${user.username}`,
      formatters.stores(summarizeUserStores(user)),
      formatters.lastLogin(resolveRelativeLogin(user.lastLoginAt, now)),
    ]),
    tags: resolveIdentityExceptionTags(user),
    badge: primaryRole ? { label: formatters.roleName(primaryRole.roleName), tone: primaryRole.tone } : null,
    isSelf: false,
  };
}
