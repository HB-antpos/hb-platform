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
