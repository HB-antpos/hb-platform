import type { components } from "@hb/pos-api-client/openapi";
import type { LocalCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";
import {
  CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE,
  CASH_MOVEMENT_UPLOAD_MAXIMUM_REQUEST_BYTES,
} from "@hb/pos-domain/core/contracts/cash-movement-upload";

import {
  centsToAmount,
  normalizeDailyCloseAppVersion,
  type DailyCloseSyncClientKind,
} from "./daily-close-sync-request";

export type CashMovementSyncRequest = components["schemas"]["CashMovementSyncRequest"];
export type CashMovementSyncItem = components["schemas"]["CashMovementSync"];
export type CashMovementSyncResponse = components["schemas"]["CashMovementSyncResponse"];

/** 与日结相同：手持与 iPad 各自固定的 clientKind。 */
export type CashMovementSyncClientKind = DailyCloseSyncClientKind;

/** 当前唯一受支持的 schemaVersion（服务端 CashMovementContractConstants.SchemaVersion）。 */
export const CASH_MOVEMENT_SYNC_SCHEMA_VERSION = 1;

/**
 * 本地记录无法映射成上传项（数据本身有问题）。重试不会改变结果：上传服务把这条记录永久标记为 rejected，且不发请求。
 */
export class CashMovementSyncMappingError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "CashMovementSyncMappingError";
  }
}

/**
 * 本地记录 → 上传项。不修正任何数据：金额由整数分换算成元（centsToAmount 全程整数运算，不引入浮点误差），
 * 发生时刻统一输出 UTC ISO（服务端按 DateTimeOffset 读入，幂等比较用 ticks，所以同一时刻必须输出同一字符串）。
 */
export function mapCashMovementToSyncItem(
  movement: LocalCashMovement,
): CashMovementSyncItem {
  const occurredMs = Date.parse(movement.occurredAtIso);
  if (!Number.isFinite(occurredMs)) {
    throw new CashMovementSyncMappingError(
      "CASH_MOVEMENT_OCCURRED_AT_INVALID",
      "Cash movement occurred-at timestamp is invalid.",
    );
  }
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(movement.businessDate)) {
    throw new CashMovementSyncMappingError(
      "CASH_MOVEMENT_BUSINESS_DATE_INVALID",
      "Cash movement business date is invalid.",
    );
  }
  let amount: number;
  try {
    amount = centsToAmount(movement.amountCents);
  } catch {
    throw new CashMovementSyncMappingError(
      "CASH_MOVEMENT_AMOUNT_UNSAFE",
      "Cash movement amount cannot be represented exactly.",
    );
  }
  return {
    movementGuid: movement.movementGuid,
    direction: movement.direction,
    reasonCode: movement.reasonCode,
    amount,
    note: movement.note,
    voidsMovementGuid: movement.voidsMovementGuid,
    businessDate: movement.businessDate,
    occurredAt: new Date(occurredMs).toISOString(),
    cashierId: movement.cashierId,
    cashierName: movement.cashierName,
    authorizedById: movement.authorizedById,
    authorizedByName: movement.authorizedByName,
    displayNumber: movement.displayNumber,
  };
}

export type CashMovementSyncRequestOptions = Readonly<{
  storeCode: string;
  deviceCode: string;
  clientKind: CashMovementSyncClientKind;
  appVersion?: string | null | undefined;
}>;

export function buildCashMovementSyncRequest(
  items: readonly CashMovementSyncItem[],
  options: CashMovementSyncRequestOptions,
): CashMovementSyncRequest {
  return {
    schemaVersion: CASH_MOVEMENT_SYNC_SCHEMA_VERSION,
    storeCode: options.storeCode,
    deviceCode: options.deviceCode,
    clientKind: options.clientKind,
    appVersion: normalizeDailyCloseAppVersion(options.appVersion),
    movements: [...items],
  };
}

export function cashMovementSyncRequestBytes(request: CashMovementSyncRequest): number {
  return new TextEncoder().encode(JSON.stringify(request)).byteLength;
}

/**
 * 在不拆乱顺序的前提下，取出最长的、能放进一个请求的前缀：条数 <= 100，请求体 <= 256KB（服务端 Controller 限制）。
 * 至少返回 1 条（单条记录远小于上限；真超限时由服务端 413 永久拒绝）。
 */
export function fitCashMovementBatch(
  items: readonly CashMovementSyncItem[],
  options: CashMovementSyncRequestOptions,
  limits: Readonly<{ maximumCount?: number; maximumBytes?: number }> = {},
): number {
  const maximumCount = Math.min(
    limits.maximumCount ?? CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE,
    CASH_MOVEMENT_UPLOAD_MAXIMUM_BATCH_SIZE,
  );
  const maximumBytes = limits.maximumBytes ?? CASH_MOVEMENT_UPLOAD_MAXIMUM_REQUEST_BYTES;
  let count = 0;
  while (count < items.length && count < maximumCount) {
    const candidate = buildCashMovementSyncRequest(items.slice(0, count + 1), options);
    if (count > 0 && cashMovementSyncRequestBytes(candidate) > maximumBytes) break;
    count += 1;
  }
  return count;
}
