import {
  HbposApiError,
  type HbposTransport,
} from "@hb/pos-api-client/transport";

import type { DailyCloseSyncRequest } from "./daily-close-sync-request";

/** 服务端 DailyClosesController 的接口路径（不是 HbposEnvelope 包装，响应与错误体都是裸 JSON）。 */
const DAILY_CLOSE_SYNC_URL = "/api/v1/daily-closes/sync";
/** 服务端并发更新冲突是暂时的，重试即可；其它 409（范围冲突、内容冲突）是永久性的。 */
const CONCURRENT_UPDATE_CODE = "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE";
/**
 * 这些状态码的响应体是 { code, message }，要让传输层“正常返回”才能读到 code
 * （传输层对错误响应只认信封里的 errorCode）；其余状态码按 HbposApiError 的 status 分类即可。
 */
const BODY_CLASSIFIED_STATUSES = Object.freeze([400, 409, 413, 422]);

export type DailyCloseSyncResult =
  | Readonly<{
      kind: "synced";
      /** Accepted / AlreadySynced / ReplacedPlaceholder 都表示服务端已持有这条日结。 */
      outcome: "accepted" | "already-synced" | "replaced-placeholder";
    }>
  /** 5xx、408、429、404（服务端尚未部署接口）、网络异常、并发更新冲突：退避重试。 */
  | Readonly<{ kind: "retry"; code: string; message: string }>
  /** 400、非并发 409、413、422：数据被判定无效或冲突，永久拒绝。 */
  | Readonly<{ kind: "rejected"; code: string; message: string }>
  /** 401 / 403：设备授权问题，不是这条记录的错。 */
  | Readonly<{ kind: "unauthorized"; code: string; message: string }>;

export interface DailyCloseSyncPort {
  sync(request: DailyCloseSyncRequest): Promise<DailyCloseSyncResult>;
}

/**
 * POST /api/v1/daily-closes/sync。鉴权（设备 Bearer + X-HBPOS-* 头）统一由 HbposTransport 注入，
 * 与订单、审计上传共用同一套设备凭据。本类只负责把响应分类成上传服务能直接消费的结果，不抛异常。
 */
export class HbposDailyCloseSyncAdapter implements DailyCloseSyncPort {
  public constructor(private readonly transport: HbposTransport) {}

  public async sync(
    request: DailyCloseSyncRequest,
  ): Promise<DailyCloseSyncResult> {
    try {
      const response = await this.transport.request<unknown>({
        method: "POST",
        url: DAILY_CLOSE_SYNC_URL,
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
        body.message ?? `Daily close sync failed with HTTP ${response.status}.`,
      );
    } catch (error) {
      return classifyThrown(error);
    }
  }
}

function classifySuccess(data: unknown): DailyCloseSyncResult {
  if (!isRecord(data)) {
    return {
      kind: "retry",
      code: "EMPTY_SYNC_RESPONSE",
      message: "Daily close sync returned an empty response.",
    };
  }
  if (data.accepted === true) return { kind: "synced", outcome: "accepted" };
  if (data.alreadySynced === true) {
    return { kind: "synced", outcome: "already-synced" };
  }
  if (data.replacedPlaceholder === true) {
    return { kind: "synced", outcome: "replaced-placeholder" };
  }
  // 200 但没有任何“已收下”标志：响应不可信（服务端契约里 Accepted 恒为 true），
  // 保守地退避重试，而不是把记录永久标成拒绝。
  return {
    kind: "retry",
    code: "SYNC_NOT_ACCEPTED",
    message: "The server did not accept the daily close sync request.",
  };
}

function classifyThrown(error: unknown): DailyCloseSyncResult {
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
    message: error instanceof Error ? error.message : "Daily close sync failed.",
  };
}

function classifyHttpStatus(
  status: number,
  code: string | undefined,
  message: string,
): DailyCloseSyncResult {
  const errorCode = code ?? `HTTP_${status}`;
  if (status === 401 || status === 403) {
    // 设备授权问题：调用方保持 pending、撤销本次尝试计数并中断本批次。
    return { kind: "unauthorized", code: errorCode, message };
  }
  if (status === 409) {
    return code === CONCURRENT_UPDATE_CODE
      ? { kind: "retry", code: errorCode, message }
      : { kind: "rejected", code: errorCode, message };
  }
  if (status === 400 || status === 413 || status === 422) {
    // 数据本身被服务端判定无效：重试不会改变结果，永久拒绝并记录原因。
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
