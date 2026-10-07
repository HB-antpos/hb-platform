import type { DailyCloseArchive } from "./daily-close";

/**
 * 日结记录上传（手持 / iPad → Hbpos.Api `POST /api/v1/daily-closes/sync`）的本地投递合同。
 * 语义与 WPF 的 LocalDailyCloses 上传状态完全一致：
 * pending →(认领)→ uploading →(成功)→ synced；失败回 pending 并带退避；永久拒绝 rejected；
 * 非 GUID 的遗留 closeId 无法上传，标记 skipped。
 */
export const DAILY_CLOSE_UPLOAD_STATES = Object.freeze([
  "pending",
  "uploading",
  "synced",
  "rejected",
  "skipped",
] as const);

export type DailyCloseUploadState =
  (typeof DAILY_CLOSE_UPLOAD_STATES)[number];

/** 上传租约：进程中途退出遗留的 uploading 超过该时长就回收为 pending（与 WPF 一致，2 分钟）。 */
export const DAILY_CLOSE_UPLOAD_LEASE_SECONDS = 120;

/** 只上传当前设备授权范围内的日结；其它范围的行保持 pending 不动。 */
export type DailyCloseUploadScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

/** 认领成功后的租约：attemptCount 是认领后的累计尝试次数，用于计算退避。 */
export type DailyCloseUploadLease = Readonly<{
  closeId: string;
  attemptCount: number;
}>;

/**
 * 日结上传 outbox 的本地仓储。所有时间都由调用方以 UTC ISO 毫秒串传入，
 * 保证 SQL 里按字符串比较“是否到期”与按时间先后比较结果一致。
 */
export interface DailyCloseUploadRepositoryPort {
  /**
   * 回收过期租约：last_attempt 不晚于 staleBefore 的 uploading 退回 pending 并立即按 nextAttemptAtIso 排队。
   * 返回被回收的行数。
   */
  recoverExpiredUploading(
    input: Readonly<{ staleBeforeIso: string; nextAttemptAtIso: string }>,
  ): Promise<number>;

  /** 取到期待传的 closeId；只返回 store/device 与 scope 完全一致且已归档的行。 */
  listDue(
    scope: DailyCloseUploadScope,
    limit: number,
    nowIso: string,
  ): Promise<readonly string[]>;

  /**
   * 租约认领（CAS）：只有仍是 pending、已到期、范围一致的行才会被改成 uploading 并累加尝试次数；
   * 并发认领同一条记录时只有一个调用方拿到租约，其余返回 null。
   */
  tryClaim(
    closeId: string,
    scope: DailyCloseUploadScope,
    attemptedAtIso: string,
  ): Promise<DailyCloseUploadLease | null>;

  /** 读出完整存档（含 11 档面额）用于构造上传请求；记录不存在或未归档时返回 null。 */
  readArchive(closeId: string): Promise<DailyCloseArchive | null>;

  /** 服务端已确认收下。不限定 uploading：即使租约刚好被回收也应记为已同步，避免重复上传。 */
  markSucceeded(closeId: string, uploadedAtIso: string): Promise<void>;

  /** 失败后退回 pending，保留已累加的尝试次数并设置下次到期时间；只有持有租约（uploading）的一方才能改。 */
  markPending(
    closeId: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /**
   * 设备授权问题（401/403）：退回 pending 并撤销本次认领累加的尝试次数。
   * 这不是这条记录自己的失败，不能烧掉它的尝试次数；nextAttemptAtIso 是短冷却，避免调度器 0ms 自旋。
   */
  releaseWithoutAttempt(
    closeId: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /** 数据被服务端判定无效或冲突：永久拒绝，不再重试。 */
  markRejected(
    closeId: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /** 无法上传（例如非 GUID 的遗留 closeId）：标记 skipped，不发请求。 */
  markSkipped(
    closeId: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /**
   * 供调度器定时唤醒：当前范围内最早的“带退避时间”的 pending 到期时间与 uploading 租约到期时间。
   * 没有 next 时间的 pending（立即到期）不在此列，它们由保存/启动/前台/联网的显式触发处理。
   */
  nextReadyAtIso(scope: DailyCloseUploadScope): Promise<string | null>;
}

const closeIdGuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

/**
 * 服务端 dailyCloseGuid 是 Guid：只接受 8-4-4-4-12 十六进制形态（不限版本位）。
 * M17 从 M6 迁入的遗留日结 closeId 可能是任意文本，不满足时不能上传。
 */
export function isDailyCloseUploadGuid(value: string): boolean {
  return closeIdGuidPattern.test(value);
}
