import {
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api/hbpos-api";
import type { components } from "@hb/pos-api-client/openapi";

type GeneratedResponse =
  components["schemas"]["AdvertisementPlaybackResponse"];
type GeneratedItem =
  components["schemas"]["AdvertisementPlaybackItemDto"];

const MAXIMUM_ADVERTISEMENT_BYTES = 200 * 1024 * 1024;

/**
 * 广告版式：landscape 只在空闲全屏位播，portrait 只在收银右侧位播，any 两处都播。
 */
export type CustomerDisplayAdvertisementOrientation =
  | "landscape"
  | "portrait"
  | "any";

export type CustomerDisplayAdvertisementItem = Readonly<{
  id: string;
  kind: "image" | "video";
  remoteUrl: string;
  objectKey: string;
  originalFileName: string;
  contentType: string;
  fileSize: number;
  effectiveStartIso: string;
  effectiveEndIso: string;
  sortOrder: number;
  /** 缺省（旧缓存项、旧调用方）按 any 处理。 */
  orientation?: CustomerDisplayAdvertisementOrientation;
}>;

export type CustomerDisplayAdvertisementResponse = Readonly<{
  storeCode: string;
  generatedAtIso: string;
  items: readonly CustomerDisplayAdvertisementItem[];
}>;

export interface CustomerDisplayAdvertisementRemotePort {
  getActive(
    storeCode: string,
  ): Promise<CustomerDisplayAdvertisementResponse>;
}

export class HbposAdvertisementApi
  implements CustomerDisplayAdvertisementRemotePort
{
  public constructor(private readonly transport: HbposTransport) {}

  public async getActive(
    requestedStoreCode: string,
  ): Promise<CustomerDisplayAdvertisementResponse> {
    const storeCode = requestIdentity(requestedStoreCode, "storeCode");
    const response = await this.transport.request<
      HbposEnvelope<GeneratedResponse>
    >({
      method: "GET",
      url: "/api/v1/advertisements/active",
      params: { storeCode, take: 20 },
    });
    const payload = unwrapHbposEnvelope(response.data);
    const responseStoreCode = responseIdentity(
      payload.storeCode,
      "storeCode",
    );
    if (responseStoreCode !== storeCode) {
      throw invalidResponse("storeCode");
    }
    const generatedAtIso = responseTimestamp(
      payload.generatedAt,
      "generatedAt",
    );
    if (!Array.isArray(payload.items)) {
      throw invalidResponse("items");
    }
    const items = payload.items.map(normalizeItem);
    return Object.freeze({
      storeCode,
      generatedAtIso,
      items: Object.freeze(items),
    });
  }
}

function normalizeItem(
  item: GeneratedItem,
): CustomerDisplayAdvertisementItem {
  const id = responseIdentity(item.id, "item.id");
  const kind = responseKind(item.mediaType);
  const remoteUrl = responseRemoteUrl(item.mediaUrl);
  const fileSize = responsePositiveInteger(
    item.fileSize,
    "item.fileSize",
  );
  if (fileSize > MAXIMUM_ADVERTISEMENT_BYTES) {
    throw invalidResponse("item.fileSize");
  }
  const effectiveStartIso = responseTimestamp(
    item.effectiveStart,
    "item.effectiveStart",
  );
  const effectiveEndIso = responseTimestamp(
    item.effectiveEnd,
    "item.effectiveEnd",
  );
  if (
    Date.parse(effectiveStartIso) > Date.parse(effectiveEndIso)
  ) {
    throw invalidResponse("item.effectiveEnd");
  }
  const contentType = responseText(
    item.contentType,
    "item.contentType",
    128,
  ).toLowerCase();
  if (
    (kind === "image" && !contentType.startsWith("image/")) ||
    (kind === "video" && !contentType.startsWith("video/"))
  ) {
    throw invalidResponse("item.contentType");
  }
  return Object.freeze({
    id,
    kind,
    remoteUrl,
    objectKey: responseText(
      item.objectKey,
      "item.objectKey",
      1_024,
    ),
    originalFileName: responseText(
      item.originalFileName,
      "item.originalFileName",
      512,
    ),
    contentType,
    fileSize,
    effectiveStartIso,
    effectiveEndIso,
    sortOrder: responseNonNegativeInteger(
      item.sortOrder,
      "item.sortOrder",
    ),
    orientation: responseOrientation(item.orientation),
  });
}

/**
 * 版式字段宽松解析：trim+小写后只接受 landscape/portrait，
 * 空值、未知值与非字符串（旧服务端）一律归为 any，不能让它拖垮整批广告。
 */
function responseOrientation(
  value: unknown,
): CustomerDisplayAdvertisementOrientation {
  if (typeof value !== "string") return "any";
  const normalized = value.trim().toLowerCase();
  return normalized === "landscape" || normalized === "portrait"
    ? normalized
    : "any";
}

function responseKind(value: unknown): "image" | "video" {
  if (value === "image" || value === "video") return value;
  throw invalidResponse("item.mediaType");
}

function responseRemoteUrl(value: unknown): string {
  const raw = responseText(value, "item.mediaUrl", 2_048);
  let parsed: URL;
  try {
    parsed = new URL(raw);
  } catch {
    throw invalidResponse("item.mediaUrl");
  }
  const loopbackHttp =
    parsed.protocol === "http:" && isLoopbackHost(parsed.hostname);
  if (
    (parsed.protocol !== "https:" && !loopbackHttp) ||
    parsed.username ||
    parsed.password
  ) {
    throw invalidResponse("item.mediaUrl");
  }
  return parsed.toString();
}

function isLoopbackHost(hostname: string): boolean {
  const normalized = hostname.toLowerCase();
  return (
    normalized === "localhost" ||
    normalized === "127.0.0.1" ||
    normalized === "[::1]" ||
    normalized === "::1"
  );
}

function requestIdentity(value: unknown, field: string): string {
  if (typeof value !== "string") throw invalidRequest(field);
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw invalidRequest(field);
  }
  return normalized;
}

function responseIdentity(value: unknown, field: string): string {
  return responseText(value, field, 128);
}

function responseText(
  value: unknown,
  field: string,
  maximum: number,
): string {
  if (typeof value !== "string") throw invalidResponse(field);
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maximum ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw invalidResponse(field);
  }
  return normalized;
}

function responseTimestamp(value: unknown, field: string): string {
  const normalized = responseText(value, field, 64);
  const timestamp = Date.parse(normalized);
  if (!Number.isFinite(timestamp)) throw invalidResponse(field);
  return new Date(timestamp).toISOString();
}

function responsePositiveInteger(value: unknown, field: string): number {
  if (!Number.isSafeInteger(value) || (value as number) <= 0) {
    throw invalidResponse(field);
  }
  return value as number;
}

function responseNonNegativeInteger(
  value: unknown,
  field: string,
): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw invalidResponse(field);
  }
  return value as number;
}

function invalidRequest(field: string): TypeError {
  return new TypeError(`Advertisement request ${field} is invalid.`);
}

function invalidResponse(field: string): TypeError {
  return new TypeError(`Advertisement response ${field} is invalid.`);
}
