import {
  HbposApiError,
  type HbposTransport,
} from "@hb/pos-api-client/transport";

import type {
  CashMovementSyncRequest,
  CashMovementSyncResponse,
} from "./cash-movement-sync-request";

/** 服务端 CashMovementsController 的接口路径（不是 HbposEnvelope 包装，响应与错误体都是裸 JSON）。 */
const CASH_MOVEMENT_SYNC_URL = "/api/v1/cash-movements/sync";
/**
 * 这些状态码的响应体是 { code, message }，要让传输层“正常返回”才能读到 code；
 * 其余状态码按 HbposApiError 的 status 分类即可。
 */
const BODY_CLASSIFIED_STATUSES = Object.freeze([400, 413, 422]);

/**
 * 单条记录被服务端标为 Rejected 时，这些错误码是「暂时的」，客户端要继续重试：
 * - VOIDED_MOVEMENT_NOT_FOUND：冲正比原记录先到（原记录还没上传成功），等原记录到了就能过；
 * - CASH_MOVEMENT_CONCURRENT_UPDATE：服务端并发写入冲突，服务端注释明确写「Retry the upload」
 *   （WPF 把它当永久拒绝是它的缺陷，移动端按服务端语义当可重试）。
 * 其余 Rejected 码（INVALID_AMOUNT、NOTE_REQUIRED、ALREADY_VOIDED、*_CONFLICT 等）都是永久的。
 */
export const CASH_MOVEMENT_RETRYABLE_REJECTION_CODES: ReadonlySet<string> = new Set([
  "VOIDED_MOVEMENT_NOT_FOUND",
  "CASH_MOVEMENT_CONCURRENT_UPDATE",
]);

export type CashMovementItemResult = Readonly<{
  /** 服务端回传的 movementGuid（小写规范化，便于与本地比较）。 */
  movementGuid: string;
  outcome: "synced" | "retry" | "rejected";
  code: string | null;
  message: string | null;
}>;

export type CashMovementSyncResult =
  /** HTTP 200：逐条结果。请求里有、响应里缺的记录由调用方当作重试。 */
  | Readonly<{ kind: "results"; items: readonly CashMovementItemResult[] }>
  /** 5xx、408、429、404（服务端尚未部署接口）、网络异常、响应不可信：整批退避重试。 */
  | Readonly<{ kind: "retry"; code: string; message: string }>
  /** 整批 400 / 413 / 422：请求本身被判定无效，重试不会改变结果，整批永久拒绝。 */
  | Readonly<{ kind: "rejected"; code: string; message: string }>
  /** 401 / 403：设备授权问题，不是这些记录的错。 */
  | Readonly<{ kind: "unauthorized"; code: string; message: string }>;

export interface CashMovementSyncPort {
  sync(request: CashMovementSyncRequest): Promise<CashMovementSyncResult>;
}

/**
 * POST /api/v1/cash-movements/sync。鉴权（设备 Bearer + X-HBPOS-* 头）由 HbposTransport 统一注入。
 * 本类只负责把响应分类成上传服务能直接消费的结果，不抛异常。
 */
export class HbposCashMovementSyncAdapter implements CashMovementSyncPort {
  public constructor(private readonly transport: HbposTransport) {}

  public async sync(request: CashMovementSyncRequest): Promise<CashMovementSyncResult> {
    try {
      const response = await this.transport.request<unknown>({
        method: "POST",
        url: CASH_MOVEMENT_SYNC_URL,
        data: request,
        acceptedStatuses: BODY_CLASSIFIED_STATUSES,
      });
      if (response.status >= 200 && response.status < 300) {
        return classifySuccess(response.data);
      }
      const body = readErrorBody(response.data);
      return classifyHttpStatus(
        response.status,
        body.code,
        body.message ?? `Cash movement sync failed with HTTP ${response.status}.`,
      );
    } catch (error) {
      return classifyThrown(error);
    }
  }
}

function classifySuccess(data: unknown): CashMovementSyncResult {
  const results = isRecord(data)
    ? (data as CashMovementSyncResponse).results
    : undefined;
  if (!Array.isArray(results)) {
    return {
      kind: "retry",
      code: "EMPTY_SYNC_RESPONSE",
      message: "Cash movement sync returned an empty response.",
    };
  }
  const items: CashMovementItemResult[] = [];
  for (const entry of results as readonly unknown[]) {
    if (!isRecord(entry) || typeof entry.movementGuid !== "string") continue;
    const movementGuid = entry.movementGuid.toLowerCase();
    const code = typeof entry.code === "string" && entry.code.length > 0 ? entry.code : null;
    const message =
      typeof entry.message === "string" && entry.message.length > 0 ? entry.message : null;
    const status = typeof entry.status === "string" ? entry.status.toLowerCase() : "";
    if (status === "accepted" || status === "alreadysynced") {
      items.push({ movementGuid, outcome: "synced", code: null, message: null });
    } else if (status === "rejected") {
      items.push({
        movementGuid,
        outcome:
          code !== null && CASH_MOVEMENT_RETRYABLE_REJECTION_CODES.has(code)
            ? "retry"
            : "rejected",
        code: code ?? "REJECTED",
        message,
      });
    } else {
      // 未知状态：不可信，保守重试，而不是永久拒绝。
      items.push({
        movementGuid,
        outcome: "retry",
        code: "SYNC_STATUS_UNKNOWN",
        message: "The server returned an unknown cash movement status.",
      });
    }
  }
  return { kind: "results", items };
}

function classifyThrown(error: unknown): CashMovementSyncResult {
  if (error instanceof HbposApiError) {
    if (error.kind === "http" && error.status !== undefined) {
      return classifyHttpStatus(error.status, error.code, error.message);
    }
    // 无 HTTP 响应（断网、连接拒绝、超时、请求被取消）：保持待传并退避。
    return { kind: "retry", code: "NETWORK", message: error.message };
  }
  // 上传异常必须保持在后台重试，不得影响收银流程。
  return {
    kind: "retry",
    code: "UPLOAD_EXCEPTION",
    message: error instanceof Error ? error.message : "Cash movement sync failed.",
  };
}

function classifyHttpStatus(
  status: number,
  code: string | undefined,
  message: string,
): CashMovementSyncResult {
  const errorCode = code ?? `HTTP_${status}`;
  if (status === 401 || status === 403) {
    return { kind: "unauthorized", code: errorCode, message };
  }
  if (status === 400 || status === 413 || status === 422) {
    // 整批请求被判定无效（版本、客户端类型、批量大小等）：重试不会改变结果，本批全部永久拒绝。
    return { kind: "rejected", code: errorCode, message };
  }
  // 5xx、408、429，以及服务端尚未部署接口时的 404 等：退避重试，不丢数据。
  return { kind: "retry", code: errorCode, message };
}

function readErrorBody(
  data: unknown,
): Readonly<{ code: string | undefined; message: string | undefined }> {
  if (!isRecord(data)) return { code: undefined, message: undefined };
  const code = typeof data.code === "string" ? data.code : data.errorCode;
  return {
    code: typeof code === "string" && code.length > 0 ? code : undefined,
    message:
      typeof data.message === "string" && data.message.length > 0
        ? data.message
        : undefined,
  };
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
