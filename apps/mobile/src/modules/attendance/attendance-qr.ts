import type {
  AttendanceMealDeclaration,
  AttendancePunch,
  AttendancePunchMutationResult,
  AttendancePunchPayload,
  AttendancePunchVerificationPayload,
  AttendancePunchVerificationState,
  AttendanceQrResolveResult,
} from "@/modules/attendance/types";

const TOKEN_PREFIX = "HBATE1";
const MAX_TOKEN_LENGTH = 600;
const KEY_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const BASE64_URL_PATTERN = /^[A-Za-z0-9_-]+$/;
const CONTROL_CHARACTER_PATTERN = /[\u0000-\u001F\u007F-\u009F\u200B-\u200D\uFEFF]/g;
const TOKEN_CANDIDATE_PATTERN = /HBATE1\.[A-Za-z0-9_-]{1,64}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/;
const MAX_CIPHERTEXT_BYTES = 327;

function getBase64UrlDecodedLength(value: string) {
  if (!BASE64_URL_PATTERN.test(value) || value.length % 4 === 1) {
    return -1;
  }
  return Math.floor(value.length * 6 / 8);
}

function normalizeAttendanceMutationWorkDate(value: string) {
  const match = value.match(
    /^(\d{4})-(\d{2})-(\d{2})(?:T00:00:00(?:\.\d{1,7})?)?$/,
  );
  if (!match) return "";

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const parsed = new Date(Date.UTC(year, month - 1, day));
  return parsed.getUTCFullYear() === year
    && parsed.getUTCMonth() === month - 1
    && parsed.getUTCDate() === day
    ? `${match[1]}-${match[2]}-${match[3]}`
    : "";
}

export function validateAttendanceQrToken(token: string) {
  if (!token || token.length > MAX_TOKEN_LENGTH) {
    throw new Error("ATTENDANCE_QR_FORMAT_INVALID");
  }

  const parts = token.split(".");
  const nonceLength = getBase64UrlDecodedLength(parts[2] ?? "");
  const ciphertextLength = getBase64UrlDecodedLength(parts[3] ?? "");
  const tagLength = getBase64UrlDecodedLength(parts[4] ?? "");
  if (
    parts.length !== 5 ||
    parts[0] !== TOKEN_PREFIX ||
    !KEY_ID_PATTERN.test(parts[1]) ||
    nonceLength !== 12 ||
    ciphertextLength < 1 ||
    ciphertextLength > MAX_CIPHERTEXT_BYTES ||
    tagLength !== 16
  ) {
    throw new Error("ATTENDANCE_QR_FORMAT_INVALID");
  }
  // 客户端只确认传输格式，门店、设备和有效期全部交给后端解密验证。
  return token;
}

export function normalizeAttendanceQrTokenInput(rawToken: string) {
  const cleaned = rawToken.trim().replace(CONTROL_CHARACTER_PATTERN, "");
  const candidate = cleaned.match(TOKEN_CANDIDATE_PATTERN)?.[0] ?? cleaned;
  // 关键逻辑：只清理扫描输入噪声，令牌结构和身份有效性仍由严格校验与后端解密负责。
  return candidate.trim();
}

function readResolveString(
  raw: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
) {
  const value = raw[camelKey] ?? raw[pascalKey];
  return typeof value === "string" ? value.trim() : "";
}

export function normalizeAttendanceQrResolveResult(
  payload: unknown,
): AttendanceQrResolveResult {
  const raw = payload && typeof payload === "object" && !Array.isArray(payload)
    ? payload as Record<string, unknown>
    : {};
  const storeCode = readResolveString(raw, "storeCode", "StoreCode");
  const deviceCode = readResolveString(raw, "deviceCode", "DeviceCode");
  const expiresAtUtc = readResolveString(raw, "expiresAtUtc", "ExpiresAtUtc");
  const punchAuthorizationToken = readResolveString(
    raw,
    "punchAuthorizationToken",
    "PunchAuthorizationToken",
  );
  const punchAuthorizationExpiresAtUtc = readResolveString(
    raw,
    "punchAuthorizationExpiresAtUtc",
    "PunchAuthorizationExpiresAtUtc",
  );
  const storeName = readResolveString(raw, "storeName", "StoreName");
  const hasPunchAuthorization = Boolean(
    punchAuthorizationToken || punchAuthorizationExpiresAtUtc,
  );
  if (
    !storeCode
    || !deviceCode
    || !expiresAtUtc
    || Number.isNaN(Date.parse(expiresAtUtc))
    || (hasPunchAuthorization && (
      !punchAuthorizationToken
      || !punchAuthorizationExpiresAtUtc
      || Number.isNaN(Date.parse(punchAuthorizationExpiresAtUtc))
    ))
  ) {
    throw Object.assign(new Error("ATTENDANCE_QR_FORMAT_INVALID"), {
      code: "ATTENDANCE_QR_FORMAT_INVALID",
    });
  }
  return {
    storeCode,
    deviceCode,
    expiresAtUtc,
    ...(punchAuthorizationToken && punchAuthorizationExpiresAtUtc
      ? { punchAuthorizationToken, punchAuthorizationExpiresAtUtc }
      : {}),
    ...(storeName ? { storeName } : {}),
  };
}

export function normalizeAttendancePunchMutationResult(
  payload: unknown,
  normalized: AttendancePunch,
): AttendancePunchMutationResult {
  const raw = payload && typeof payload === "object" && !Array.isArray(payload)
    ? payload as Record<string, unknown>
    : {};
  const punchGuid = readResolveString(raw, "punchGuid", "PunchGuid");
  const punchType = readResolveString(raw, "punchType", "PunchType");
  const storeCode = readResolveString(raw, "storeCode", "StoreCode");
  const serverTimeUtc = readResolveString(raw, "serverTimeUtc", "ServerTimeUtc");
  const workDate = normalizeAttendanceMutationWorkDate(
    readResolveString(raw, "workDate", "WorkDate"),
  );
  const storeTimeZone = readResolveString(raw, "storeTimeZone", "StoreTimeZone");
  if (
    !punchGuid
    || (punchType !== "ClockIn" && punchType !== "ClockOut")
    || !storeCode
    || !serverTimeUtc
    || !workDate
    || !storeTimeZone
  ) {
    throw Object.assign(new Error("ATTENDANCE_PUNCH_RESPONSE_INVALID"), {
      code: "ATTENDANCE_PUNCH_RESPONSE_INVALID",
    });
  }
  // 历史查询继续宽松兼容；mutation 的 tracking 权威字段必须来自明确的服务端响应。
  return {
    ...normalized,
    punchGuid,
    punchType,
    storeCode,
    serverTimeUtc,
    workDate,
    storeTimeZone,
  };
}

export function resolveAttendanceQrStore<T extends { storeCode: string }>(
  resolved: { storeCode: string },
  stores: T[],
) {
  const store = stores.find((item) => item.storeCode === resolved.storeCode);
  if (!store) {
    throw new Error("ATTENDANCE_STORE_FORBIDDEN");
  }
  return store;
}

export function buildAttendanceQrPunchPayload(
  qrToken: string,
  punchAuthorizationToken: string | undefined,
  verification: AttendancePunchVerificationPayload,
  mealDeclaration?: AttendanceMealDeclaration,
): AttendancePunchPayload {
  return {
    qrToken,
    ...(punchAuthorizationToken ? { punchAuthorizationToken } : {}),
    locationLatitude: verification.locationLatitude,
    locationLongitude: verification.locationLongitude,
    locationAccuracy: verification.locationAccuracy,
    locationCapturedAtUtc: verification.locationCapturedAtUtc,
    // 扫码前问过「吃饭休息了吗」才带；没问过（上班、或不缺休息）时保持原 payload。
    ...(mealDeclaration ? { mealDeclaration } : {}),
  };
}

export function canOpenAttendanceQrScanner(options: {
  isLoading: boolean;
  isPunching: boolean;
  isToday: boolean;
  hasAuthorizedStores: boolean;
}) {
  return !options.isLoading
    && !options.isPunching
    && options.isToday
    && options.hasAuthorizedStores;
}

export function canSubmitAttendanceQrPunch(options: {
  hasCurrentLocation: boolean;
  isOnline: boolean;
  hasBackgroundLocationPermission: boolean;
}) {
  return options.hasCurrentLocation
    && options.isOnline
    && options.hasBackgroundLocationPermission;
}

export function shouldEnableAttendanceQrScanning(options: {
  isVisible: boolean;
  isSubmitting: boolean;
  isPaused: boolean;
}) {
  return options.isVisible && !options.isSubmitting && !options.isPaused;
}

export function getAttendanceTrackingAction(punchType: string) {
  if (punchType === "ClockIn") return "start";
  if (punchType === "ClockOut") return "stop";
  return "none";
}

export function requiresBackgroundPermissionBeforePunch(punchType: string) {
  return punchType === "ClockIn";
}

type AttendanceQrPunchPreparation =
  | {
      status: "ready";
      verification: AttendancePunchVerificationState;
      backgroundLocationAllowed: boolean;
    }
  | { status: "stale" | "backgroundRequired" | "gpsRequired" | "networkRequired" };

export async function prepareAttendanceQrPunch(
  expectedPunchType: string,
  options: {
    isActive: () => boolean;
    ensureBackgroundPermission: () => Promise<boolean>;
    refreshVerification: () => Promise<AttendancePunchVerificationState>;
  },
): Promise<AttendanceQrPunchPreparation> {
  let backgroundLocationAllowed = false;
  if (requiresBackgroundPermissionBeforePunch(expectedPunchType)) {
    backgroundLocationAllowed = await options.ensureBackgroundPermission();
    if (!options.isActive()) return { status: "stale" };
    if (!backgroundLocationAllowed) return { status: "backgroundRequired" };
  }

  // 权限交互可能耗时，提交前必须重新采集最新 GPS 与网络状态。
  const verification = await options.refreshVerification();
  if (!options.isActive()) return { status: "stale" };
  if (verification.location.status !== "available") return { status: "gpsRequired" };
  if (verification.network.status !== "available") return { status: "networkRequired" };
  return { status: "ready", verification, backgroundLocationAllowed };
}

export async function applyAttendanceTrackingLifecycle(
  result: { punchType: string; storeCode?: string },
  fallbackStoreCode: string,
  handlers: {
    start: (storeCode: string) => Promise<void>;
    stop: () => Promise<void>;
  },
) {
  const action = getAttendanceTrackingAction(result.punchType);
  if (action === "start") {
    await handlers.start(result.storeCode || fallbackStoreCode);
  } else if (action === "stop") {
    await handlers.stop();
  }
}

const ATTENDANCE_ERROR_KEYS: Record<string, string> = {
  ATTENDANCE_QR_DECRYPT_FAILED: "messages.qrDecryptFailed",
  ATTENDANCE_PUNCH_RESPONSE_INVALID: "messages.punchFailed",
  ATTENDANCE_QR_KEY_DECRYPT_FAILED: "messages.qrDecryptFailed",
  ATTENDANCE_QR_FORMAT_INVALID: "messages.qrFormatInvalid",
  ATTENDANCE_QR_PAYLOAD_INVALID: "messages.qrFormatInvalid",
  ATTENDANCE_QR_SIGNATURE_INVALID: "messages.qrInvalidSignature",
  ATTENDANCE_QR_AUTH_INVALID: "messages.qrInvalidSignature",
  ATTENDANCE_QR_DEVICE_MISMATCH: "messages.qrDeviceMismatch",
  ATTENDANCE_QR_KEY_INVALID: "messages.qrKeyInvalid",
  ATTENDANCE_QR_KEY_UNKNOWN: "messages.qrKeyUnknown",
  ATTENDANCE_QR_KEY_REVOKED: "messages.qrKeyRevoked",
  ATTENDANCE_QR_NOT_ACTIVE: "messages.qrNotActive",
  ATTENDANCE_QR_EXPIRED: "messages.qrExpired",
  ATTENDANCE_PUNCH_AUTHORIZATION_INVALID: "messages.qrPunchAuthorizationInvalid",
  ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED: "messages.qrPunchAuthorizationExpired",
  POS_DEVICE_DISABLED: "messages.qrDeviceDisabled",
  ATTENDANCE_STORE_FORBIDDEN: "messages.qrStoreForbidden",
  STORE_ACCESS_DENIED: "messages.qrStoreForbidden",
  FORBIDDEN_STORE: "messages.qrStoreForbidden",
  LOCATION_REQUIRED: "messages.qrGpsRequired",
  LOCATION_PERMISSION_REQUIRED: "messages.qrGpsRequired",
  NETWORK_ERROR: "messages.qrNetworkRequired",
  NETWORK_UNAVAILABLE: "messages.qrNetworkRequired",
  ERR_NETWORK: "messages.qrNetworkRequired",
  ECONNABORTED: "messages.qrNetworkRequired",
  DAY_COMPLETE: "messages.qrDayComplete",
  QR_REQUIRED: "messages.qrRequired",
  // 当班开始/结束休息的错误码。
  NO_OPEN_SEGMENT: "messages.mealNoOpenSegment",
  NO_OPEN_MEAL_BREAK: "messages.mealNoOpenBreak",
  MEAL_BREAK_ALREADY_OPEN: "messages.mealBreakAlreadyOpen",
};

export function getAttendancePunchErrorKey(errorCode?: string) {
  return errorCode ? ATTENDANCE_ERROR_KEYS[errorCode] : undefined;
}

export function getAttendancePunchErrorCode(error: unknown) {
  if (!error || typeof error !== "object") {
    return undefined;
  }

  const readPayloadCode = (payload: unknown, depth = 0): string | undefined => {
    if (!payload || typeof payload !== "object" || depth > 3) return undefined;
    const raw = payload as Record<string, unknown>;
    for (const key of ["errorCode", "ErrorCode", "code", "Code"]) {
      const value = raw[key];
      if (typeof value === "string" && value.trim()) return value.trim();
    }
    return readPayloadCode(raw.data ?? raw.Data, depth + 1);
  };

  const raw = error as Record<string, unknown>;
  const response = raw.response;
  if (response && typeof response === "object") {
    const responseCode = readPayloadCode((response as Record<string, unknown>).data);
    if (responseCode) return responseCode;
  }
  return typeof raw.code === "string" ? raw.code : undefined;
}
