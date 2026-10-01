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

  public constructor(
    message: string,
    details: Readonly<{
      kind: HbposApiErrorKind;
      status?: number;
      code?: string;
      networkCode?: string;
    }>,
  ) {
    super(message);
    this.name = "HbposApiError";
    this.kind = details.kind;
    this.status = details.status;
    this.code = details.code;
    this.networkCode = details.networkCode;
  }
}

export function unwrapHbposEnvelope<T>(envelope: HbposEnvelope<T>): T {
  if (envelope.success !== true || envelope.data === undefined) {
    const code = envelope.errorCode ?? undefined;
    throw new HbposApiError(
      envelope.message ?? "Hbpos API request was rejected.",
      code ? { kind: "envelope", code } : { kind: "envelope" },
    );
  }
  return envelope.data;
}
