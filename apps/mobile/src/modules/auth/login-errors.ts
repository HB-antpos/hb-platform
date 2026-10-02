import { isAxiosError } from "axios";
import { getCurrentApiHost } from "@/shared/api/config";

export type LoginErrorDescriptor = {
  key: string;
  values?: Record<string, string | number>;
};

type ApiErrorBody = {
  message?: unknown;
  Message?: unknown;
  error?: unknown;
  Error?: unknown;
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? value as Record<string, unknown> : null;
}

function asString(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function getStatus(error: unknown) {
  if (isAxiosError(error)) {
    return error.response?.status;
  }

  const record = asRecord(error);
  const response = asRecord(record?.response);
  return typeof response?.status === "number" ? response.status : undefined;
}

function getCode(error: unknown) {
  if (isAxiosError(error)) {
    return error.code;
  }

  const record = asRecord(error);
  return typeof record?.code === "string" ? record.code : undefined;
}

function getMessage(error: unknown) {
  if (isAxiosError<ApiErrorBody>(error)) {
    return asString(
      error.response?.data?.message ??
        error.response?.data?.Message ??
        error.response?.data?.error ??
        error.response?.data?.Error ??
        error.message
    );
  }

  if (error instanceof Error) {
    return error.message.trim();
  }

  const record = asRecord(error);
  return asString(record?.message ?? record?.Message ?? record?.error ?? record?.Error);
}

function includesAny(message: string, needles: string[]) {
  const normalized = message.toLowerCase();
  return needles.some((needle) => normalized.includes(needle.toLowerCase()));
}

function withOrigin(key: string): LoginErrorDescriptor {
  return {
    key,
    values: { origin: getCurrentApiHost() },
  };
}

export function getFriendlyLoginErrorDescriptor(error: unknown): LoginErrorDescriptor {
  const status = getStatus(error);
  const code = getCode(error);
  const message = getMessage(error);

  if (
    code === "LOCATION_REQUIRED" ||
    includesAny(message, [
      "LOCATION_REQUIRED",
      "登录需要位置信息",
      "登录需要采集当前位置",
      "定位采集时间无效",
      "定位坐标无效",
      "location required",
      "location permission",
    ])
  ) {
    return { key: "errors.locationRequired" };
  }

  if (
    includesAny(message, [
      "停用",
      "禁用",
      "锁定",
      "disabled",
      "locked",
      "inactive",
    ])
  ) {
    return { key: "errors.accountUnavailable" };
  }

  if (
    status === 401 ||
    status === 403 ||
    includesAny(message, [
      "用户名或密码",
      "账号或密码",
      "帳號或密碼",
      "密码错误",
      "密碼錯誤",
      "用户不存在",
      "invalid",
      "unauthorized",
      "password",
      "credential",
    ])
  ) {
    return { key: "errors.invalidCredentials" };
  }

  // 本机步骤（Keychain 读写等）卡住，与网络超时区分：提示重试或重启 App。
  if (code === "LOGIN_STEP_TIMEOUT") {
    return { key: "errors.stepTimeout" };
  }

  if (code === "ECONNABORTED" || code === "ETIMEDOUT" || includesAny(message, ["timeout", "timed out", "超时"])) {
    return withOrigin("errors.timeout");
  }

  if (message === "Network Error" || code === "ERR_NETWORK") {
    return withOrigin("errors.network");
  }

  if (status && status >= 500) {
    return { key: "errors.server" };
  }

  if (status) {
    return { key: "errors.http", values: { status } };
  }

  return { key: "errors.default" };
}

export function getFriendlyDeviceLoginErrorDescriptor(error: unknown): LoginErrorDescriptor {
  const code = getCode(error);
  const message = getMessage(error);

  if (code === "ECONNABORTED" || code === "ETIMEDOUT" || includesAny(message, ["timeout", "timed out", "超时"])) {
    return withOrigin("device.loginTimeout");
  }

  if (message === "Network Error" || code === "ERR_NETWORK") {
    return withOrigin("device.loginNetwork");
  }

  if (
    includesAny(message, [
      "设备未授权",
      "设备授权失败",
      "未授权",
      "auth",
      "unauthorized",
    ])
  ) {
    return { key: "device.loginUnauthorized" };
  }

  if (includesAny(message, ["禁用", "停用", "锁定", "disabled", "locked"])) {
    return { key: "device.loginUnavailable" };
  }

  return { key: "device.loginFailed" };
}
