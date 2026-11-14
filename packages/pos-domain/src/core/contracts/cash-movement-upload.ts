import type { LocalCashMovement } from "./cash-movement";

/**
 * 现金进出上传（手持 / iPad → Hbpos.Api `POST /api/v1/cash-movements/sync`）的本地投递合同。
 * 状态机照日结上传（M50）：pending →(认领)→ uploading（租约 120s）→ synced / rejected；
 * 重试类失败回 pending 并带退避（5s 起、300s 封顶）。没有 skipped：movementGuid 本地生成，必为 UUID。
 */
export const CASH_MOVEMENT_UPLOAD_LEASE_SECONDS = 120;

/** 单批上限：与服务端 MaximumBatchSize 一致；请求体另受 256KB 限制（Controller RequestSizeLimit）。 */
export const CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE = 100;
export const CASH_MOVEMENT_UPLOAD_MAXIMUM_REQUEST_BYTES = 256 * 1024;

/** 只上传当前设备授权范围内的记录；其它范围的行保持 pending 不动。 */
export type CashMovementUploadScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

/** 认领成功后的一条记录：attemptCount 是认领后的累计尝试次数，用于计算退避。 */
export type ClaimedCashMovement = Readonly<{
  movement: LocalCashMovement;
  attemptCount: number;
}>;

export type CashMovementClaimResult = Readonly<{
  /** 按 occurred_at 升序（同时刻按创建顺序）认领到的记录，已带租约。 */
  claimed: readonly ClaimedCashMovement[];
  /** 因原记录已被永久拒绝而本地直接标记 rejected 的冲正记录数（不会发请求）。 */
  rejectedLocally: number;
}>;

export interface CashMovementUploadRepositoryPort {
  /** 回收过期租约：lease_expires_at 不晚于 nowIso 的 uploading 退回 pending 并按 nextAttemptAtIso 排队。 */
  recoverExpiredUploading(
    input: Readonly<{ nowIso: string; nextAttemptAtIso: string }>,
  ): Promise<number>;

  /**
   * 认领到期待传的记录（CAS + 租约）。只认本设备范围；按 occurred_at 升序；
   * 冲正记录要等原记录已 synced 或在本批里排在它前面才会被认领（服务端要求原记录先到）；
   * 原记录已 rejected 的冲正记录直接本地 rejected。
   */
  claimDueBatch(
    scope: CashMovementUploadScope,
    limit: number,
    nowIso: string,
  ): Promise<CashMovementClaimResult>;

  /** 服务端已确认收下（Accepted / AlreadySynced）。不限定 uploading：避免租约刚好被回收后重复上传。 */
  markSynced(movementGuids: readonly string[], uploadedAtIso: string): Promise<void>;

  /** 重试类失败：退回 pending，保留已累加的尝试次数并设置下次到期时间；只有持有租约的一方才能改。 */
  markRetry(
    movementGuid: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /** 设备授权问题（401/403）或本批未发送：退回 pending 并撤销本次认领累加的尝试次数。 */
  releaseWithoutAttempt(
    movementGuids: readonly string[],
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /** 永久拒绝，不再重试。 */
  markRejected(
    movementGuid: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void>;

  /** 带退避时间的 pending 与 uploading 租约的最早到期时间，仅用于调度器定时唤醒。 */
  nextReadyAtIso(scope: CashMovementUploadScope): Promise<string | null>;
}
