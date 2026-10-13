import { unwrapApiEnvelope } from "@/shared/api/api-envelope";

// 班中定位“批量上传”的纯类型与纯函数（请求构造、响应校验）。
// 不引入 apiClient / 原生模块，便于在 Node 下做单元测试；真正发请求的函数在 location-sample-batch-api.ts。

export type AttendanceLocationSampleEventType = "ShiftSample" | "ShiftHeartbeat";

/** 后端单批上限（契约：samples 1～200 个）。 */
export const LOCATION_SAMPLE_BATCH_MAX = 200;

/** 本地队列里的一个定位点：同时携带上传所需的门店/设备信息与所属用户。 */
export interface QueuedLocationSample {
  /** 客户端生成的 GUID（≤50 字符），后端据此幂等去重。 */
  clientSampleId: string;
  /** 采集该点时登录的用户；flush 时只上传属于当前登录用户的点。 */
  ownerUserId: string;
  storeCode: string;
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  eventType: AttendanceLocationSampleEventType;
  locationLatitude: number;
  locationLongitude: number;
  locationAccuracy?: number;
  isMocked?: boolean;
  /** 采集时刻（ISO UTC，形如 2026-10-13T05:12:34.567Z）。 */
  locationCapturedAtUtc: string;
}

/** 批量请求体里的单个样本（契约字段，locationPermissionStatus 固定 granted）。 */
export interface AttendanceLocationSampleBatchItem {
  clientSampleId: string;
  eventType: AttendanceLocationSampleEventType;
  locationLatitude: number;
  locationLongitude: number;
  locationAccuracy?: number;
  isMocked?: boolean;
  locationPermissionStatus: "granted";
  locationCapturedAtUtc: string;
}

export interface AttendanceLocationSampleBatchRequest {
  storeCode: string;
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  samples: AttendanceLocationSampleBatchItem[];
}

export interface AttendanceLocationSampleRejection {
  clientSampleId: string;
  errorCode: string;
  message: string;
}

export interface AttendanceLocationSampleBatchResult {
  accepted: string[];
  duplicates: string[];
  rejected: AttendanceLocationSampleRejection[];
}

/** 响应格式不符合契约：调用方必须保留整批重试，不能删除任何点。 */
export class LocationSampleBatchResponseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LocationSampleBatchResponseError";
  }
}

function trimmedOrUndefined(value: string | undefined) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

/** 同一批请求共用的“分组键”：门店与设备信息相同的点才能放进同一个请求体。 */
export function locationSampleGroupKey(sample: QueuedLocationSample) {
  return [
    sample.storeCode,
    sample.hardwareId ?? "",
    sample.systemDeviceNumber ?? "",
    sample.deviceSystem ?? "",
  ].join("\u0001");
}

/**
 * 把同一分组的队列点构造成契约请求体。
 * 调用方保证 samples 非空、数量 ≤ 200 且分组键一致；这里按采集时间升序输出。
 */
export function buildLocationSampleBatchRequest(
  samples: readonly QueuedLocationSample[],
): AttendanceLocationSampleBatchRequest {
  if (samples.length === 0 || samples.length > LOCATION_SAMPLE_BATCH_MAX) {
    throw new RangeError(
      `定位批量请求的样本数必须在 1~${LOCATION_SAMPLE_BATCH_MAX} 之间，当前 ${samples.length}`,
    );
  }
  const head = samples[0];
  const key = locationSampleGroupKey(head);
  const ordered = samples
    .map((sample, index) => ({ sample, index }))
    .sort((left, right) => {
      const delta = Date.parse(left.sample.locationCapturedAtUtc)
        - Date.parse(right.sample.locationCapturedAtUtc);
      return Number.isFinite(delta) && delta !== 0 ? delta : left.index - right.index;
    })
    .map((entry) => entry.sample);

  return {
    storeCode: head.storeCode,
    ...(trimmedOrUndefined(head.hardwareId) ? { hardwareId: trimmedOrUndefined(head.hardwareId) } : {}),
    ...(trimmedOrUndefined(head.systemDeviceNumber)
      ? { systemDeviceNumber: trimmedOrUndefined(head.systemDeviceNumber) }
      : {}),
    ...(trimmedOrUndefined(head.deviceSystem) ? { deviceSystem: trimmedOrUndefined(head.deviceSystem) } : {}),
    samples: ordered.map((sample) => {
      if (locationSampleGroupKey(sample) !== key) {
        throw new Error("定位批量请求不能混入门店或设备信息不同的样本");
      }
      return {
        clientSampleId: sample.clientSampleId,
        eventType: sample.eventType,
        locationLatitude: sample.locationLatitude,
        locationLongitude: sample.locationLongitude,
        ...(typeof sample.locationAccuracy === "number" ? { locationAccuracy: sample.locationAccuracy } : {}),
        ...(typeof sample.isMocked === "boolean" ? { isMocked: sample.isMocked } : {}),
        locationPermissionStatus: "granted" as const,
        locationCapturedAtUtc: sample.locationCapturedAtUtc,
      };
    }),
  };
}

function pickField(record: Record<string, unknown>, camel: string) {
  // 后端默认 camelCase；兼容 PascalCase，避免序列化配置差异导致整批无法确认。
  const pascal = camel.charAt(0).toUpperCase() + camel.slice(1);
  return camel in record ? record[camel] : record[pascal];
}

function normalizeIdList(value: unknown, field: string): string[] {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string")) {
    throw new LocationSampleBatchResponseError(`批量定位响应的 ${field} 必须是字符串数组`);
  }
  return value as string[];
}

/**
 * 校验并规范化批量响应。
 * - 容器必须是对象，accepted / duplicates / rejected 至少出现一个，出现的必须是数组；
 * - 任何格式异常都抛 LocationSampleBatchResponseError（调用方据此保留整批重试）。
 */
export function normalizeLocationSampleBatchResult(raw: unknown): AttendanceLocationSampleBatchResult {
  const body = unwrapApiEnvelope<unknown>(raw);
  if (typeof body !== "object" || body === null || Array.isArray(body)) {
    throw new LocationSampleBatchResponseError("批量定位响应不是对象");
  }
  const record = body as Record<string, unknown>;
  const acceptedRaw = pickField(record, "accepted");
  const duplicatesRaw = pickField(record, "duplicates");
  const rejectedRaw = pickField(record, "rejected");
  if (
    !Array.isArray(acceptedRaw)
    && !Array.isArray(duplicatesRaw)
    && !Array.isArray(rejectedRaw)
  ) {
    // 三个字段一个数组都没有：多半是网关/代理返回的非业务体，绝不能当成“全部成功”。
    throw new LocationSampleBatchResponseError("批量定位响应缺少 accepted/duplicates/rejected");
  }

  let rejected: AttendanceLocationSampleRejection[] = [];
  if (rejectedRaw !== undefined && rejectedRaw !== null) {
    if (!Array.isArray(rejectedRaw)) {
      throw new LocationSampleBatchResponseError("批量定位响应的 rejected 必须是数组");
    }
    rejected = rejectedRaw.map((item) => {
      if (typeof item !== "object" || item === null) {
        throw new LocationSampleBatchResponseError("批量定位响应的 rejected 条目必须是对象");
      }
      const entry = item as Record<string, unknown>;
      const clientSampleId = pickField(entry, "clientSampleId");
      if (typeof clientSampleId !== "string" || !clientSampleId) {
        throw new LocationSampleBatchResponseError("批量定位响应的 rejected 条目缺少 clientSampleId");
      }
      const errorCode = pickField(entry, "errorCode");
      const message = pickField(entry, "message");
      return {
        clientSampleId,
        errorCode: typeof errorCode === "string" ? errorCode : "",
        message: typeof message === "string" ? message : "",
      };
    });
  }

  return {
    accepted: normalizeIdList(acceptedRaw, "accepted"),
    duplicates: normalizeIdList(duplicatesRaw, "duplicates"),
    rejected,
  };
}

/** 契约语义：accepted / duplicates / rejected 里的点全部可以从本地队列删除。 */
export function collectAcknowledgedSampleIds(result: AttendanceLocationSampleBatchResult): string[] {
  return [
    ...result.accepted,
    ...result.duplicates,
    ...result.rejected.map((entry) => entry.clientSampleId),
  ];
}
