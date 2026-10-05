/**
 * 更新身份的纯函数集合：完整 runtime 与启动失败恢复通道共用，
 * 本文件不得 import SQLite、收银或支付模块。
 */
export function readCurrentUpdateGroupId(manifest: unknown): string | null {
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    return null;
  }
  const record = manifest as Record<string, unknown>;
  const metadata =
    record.metadata &&
    typeof record.metadata === "object" &&
    !Array.isArray(record.metadata)
      ? (record.metadata as Record<string, unknown>)
      : null;
  const candidate =
    metadata?.updateGroupId ??
    metadata?.updateGroup ??
    record.updateGroupId ??
    null;
  if (typeof candidate !== "string") return null;
  const normalized = candidate.trim().toLowerCase();
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(
    normalized,
  )
    ? normalized
    : null;
}
