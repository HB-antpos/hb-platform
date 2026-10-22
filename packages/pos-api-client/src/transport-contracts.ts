export type HbposEnvelope<T> = Readonly<{
  success?: boolean;
  data?: T;
  errorCode?: string | null;
  message?: string | null;
}>;

export type HbposTransportRequest = Readonly<{
  method: "GET" | "POST" | "PUT";
  url: string;
  data?: unknown;
  params?: Readonly<Record<string, string | number | boolean | undefined>>;
  headers?: Readonly<Record<string, string>>;
  signal?: AbortSignal;
  timeoutMs?: number;
  acceptedStatuses?: readonly number[];
  authenticationFailurePolicy?: "default" | "suppress-unauthorized";
  /** 默认按 JSON 解析；"arraybuffer" 用于目录文件这类二进制下载，data 为原始字节。 */
  responseType?: "json" | "arraybuffer";
}>;

export type HbposTransportResponse<T> = Readonly<{
  status: number;
  data: T;
  headers?: Readonly<Record<string, string>>;
}>;

export interface HbposTransport {
  request<T>(request: HbposTransportRequest): Promise<HbposTransportResponse<T>>;
}

export type HbposApiErrorKind = "transport" | "http" | "envelope";

export class HbposApiError extends Error {
  public readonly kind: HbposApiErrorKind;
  public readonly status: number | undefined;
  public readonly code: string | undefined;
  public readonly networkCode: string | undefined;
  /**
   * 收到 HTTP 响应时，正文是否为 POS API 的 ApiResult 信封（带布尔 success 字段的 JSON 对象）。
   * false 表示响应来自网关/WAF/Wi-Fi 认证页等（HTML、空 200、非信封 JSON），不能当作服务端业务结论；
   * undefined 表示本地构造的错误或无 HTTP 响应，无从判断。
   */
  public readonly apiEnvelope: boolean | undefined;

  public constructor(
    message: string,
    details: Readonly<{
      kind: HbposApiErrorKind;
      status?: number;
      code?: string;
      networkCode?: string;
      apiEnvelope?: boolean;
    }>,
  ) {
    super(message);
    this.name = "HbposApiError";
    this.kind = details.kind;
    this.status = details.status;
    this.code = details.code;
    this.networkCode = details.networkCode;
    this.apiEnvelope = details.apiEnvelope;
  }
}

/** 与 WPF DeviceApiClient.IsApiResultEnvelope 同口径：只有带布尔 success 字段的 JSON 对象才是 ApiResult 信封。 */
export function isHbposApiEnvelope(value: unknown): value is HbposEnvelope<unknown> {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    typeof (value as Readonly<{ success?: unknown }>).success === "boolean"
  );
}

/**
 * 响应确实到达过某个 HTTP 端点，但正文不是 POS API 的 ApiResult 信封（网关 HTML 错误页、EdgeOne/WAF 拦截页、
 * Wi-Fi 认证页、空 200 等）。这类响应无法证明服务端给出了业务结论，调用方不得据此持久化拒绝或清除离线能力。
 */
// 刻意返回 boolean 而非类型谓词：放在 || 链中时，类型谓词会在 false 分支把 HbposApiError 整个排除掉。
export function isHbposNonApiResponse(error: unknown): boolean {
  return error instanceof HbposApiError && error.apiEnvelope === false;
}

export function unwrapHbposEnvelope<T>(envelope: HbposEnvelope<T>): T {
  // 2xx 正文可能是网关 HTML 字符串、null 或认证页 JSON：不是信封时仍抛 envelope 类错误（保持各调用方既有语义），
  // 但标记 apiEnvelope=false，供启动设备门禁、收银员登录等按“服务不可用”处理；也避免 null 正文抛出 TypeError。
  if (!isHbposApiEnvelope(envelope)) {
    throw new HbposApiError("Hbpos API response is not an ApiResult envelope.", {
      kind: "envelope",
      apiEnvelope: false,
    });
  }
  if (envelope.success !== true || envelope.data === undefined) {
    const code = envelope.errorCode ?? undefined;
    throw new HbposApiError(
      envelope.message ?? "Hbpos API request was rejected.",
      code
        ? { kind: "envelope", code, apiEnvelope: true }
        : { kind: "envelope", apiEnvelope: true },
    );
  }
  return envelope.data;
}
