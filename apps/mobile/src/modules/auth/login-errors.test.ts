import {
  getFriendlyDeviceLoginErrorDescriptor,
  getFriendlyLoginErrorDescriptor,
} from "./login-errors";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

assertEqual(
  getFriendlyLoginErrorDescriptor(new Error("用户名或密码错误")).key,
  "errors.invalidCredentials",
  "wrapped backend credential message stays friendly"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(new Error("账号已停用")).key,
  "errors.accountUnavailable",
  "disabled account gets admin-contact guidance"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(new Error("登录需要位置信息")).key,
  "errors.locationRequired",
  "backend location requirement stays actionable"
);

assertEqual(
  getFriendlyLoginErrorDescriptor({ message: "Network Error" }).key,
  "errors.network",
  "network failure gets connection guidance"
);

assertEqual(
  getFriendlyDeviceLoginErrorDescriptor(new Error("设备未授权，请重新绑定")).key,
  "device.loginUnauthorized",
  "device auth failure gets device-specific guidance"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(
    Object.assign(new Error("LOGIN_STEP_TIMEOUT: saveAccessToken 超过 8000ms 未完成"), { code: "LOGIN_STEP_TIMEOUT" }),
  ).key,
  "errors.stepTimeout",
  "local login step timeout is not reported as a server/network timeout"
);

// 「重试」按钮只给网络 / 超时 / 服务器繁忙这类可能自愈的失败；密码错误等业务失败重试也没用。
assertEqual(
  getFriendlyLoginErrorDescriptor({ message: "Network Error" }).retryable,
  true,
  "network failure offers retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor({ code: "ECONNABORTED", message: "timeout of 30000ms exceeded" }).retryable,
  true,
  "axios timeout offers retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor({ response: { status: 503 }, message: "Request failed with status code 503" }).key,
  "errors.server",
  "5xx maps to server busy"
);

assertEqual(
  getFriendlyLoginErrorDescriptor({ response: { status: 503 }, message: "Request failed with status code 503" }).retryable,
  true,
  "server busy offers retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(
    Object.assign(new Error("LOGIN_STEP_TIMEOUT: loginApi 超过 35000ms 未完成"), { code: "LOGIN_STEP_TIMEOUT" }),
  ).retryable,
  true,
  "local login step timeout offers retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(new Error("用户名或密码错误")).retryable,
  undefined,
  "wrong password does not offer retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor(new Error("账号已停用")).retryable,
  undefined,
  "disabled account does not offer retry"
);

assertEqual(
  getFriendlyLoginErrorDescriptor({ response: { status: 400 }, message: "Request failed with status code 400" }).retryable,
  undefined,
  "other HTTP errors do not offer retry"
);

assertEqual(
  getFriendlyDeviceLoginErrorDescriptor({ message: "Network Error" }).retryable,
  true,
  "device login network failure offers retry"
);

assertEqual(
  getFriendlyDeviceLoginErrorDescriptor(new Error("设备未授权，请重新绑定")).retryable,
  undefined,
  "device auth failure does not offer retry"
);
