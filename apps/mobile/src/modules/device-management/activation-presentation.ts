import { parseUtcTimestamp } from "@/modules/device-management/version-state";

export interface GrantLifetime {
  /** 距过期还剩多少分钟，已过期为 0。 */
  remainingMinutes: number;
  /** 剩余有效期占总有效期的比例，0 到 1，用于进度条。 */
  fraction: number;
  expired: boolean;
}

/**
 * 激活码的剩余有效期。创建时间或过期时间无法解析时返回 null（界面只显示有效期至，不画进度）。
 * 后端时间是 UTC，缺时区后缀时补 Z，避免被当成本地时间而偏移。
 */
export function getGrantLifetime(
  createdAtUtc: string | null | undefined,
  expiresAtUtc: string | null | undefined,
  now: Date = new Date()
): GrantLifetime | null {
  const created = new Date(parseUtcTimestamp(createdAtUtc) ?? "").getTime();
  const expires = new Date(parseUtcTimestamp(expiresAtUtc) ?? "").getTime();
  if (Number.isNaN(created) || Number.isNaN(expires) || expires <= created) return null;
  const remainingMs = Math.max(0, expires - now.getTime());
  return {
    remainingMinutes: Math.ceil(remainingMs / 60000),
    fraction: Math.min(1, remainingMs / (expires - created)),
    expired: remainingMs === 0,
  };
}

/** 把剩余分钟拆成「小时 + 分钟」，用于「剩余 3 小时 12 分钟」。 */
export function splitRemainingMinutes(minutes: number): { hours: number; minutes: number } {
  const safe = Math.max(0, Math.floor(minutes));
  return { hours: Math.floor(safe / 60), minutes: safe % 60 };
}
