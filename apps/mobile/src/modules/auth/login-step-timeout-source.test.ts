import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 2026-10-02 生产事故：admin 登录成功后 App 停在本机步骤 68 秒，只能重启。
// 账号登录的每个本机/联网步骤都必须带超时，并记录阶段供下次登录补传。
const currentDir = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(currentDir, "../../store/auth-store.ts"), "utf8");
const login = source.slice(source.indexOf("  async login(payload)"), source.indexOf("  async loginDeviceAccount()"));

for (const step of [
  "SecureStorage.setToken(tokenRes.accessToken)",
  "SecureStorage.setRefreshToken(tokenRes.refreshToken)",
  "getCurrentUserApi()",
  "SecureStorage.setUser(user)",
  'setAuthSessionMarker("account")',
  "SecureStorage.clearAll()",
]) {
  // 取最后一次出现：审核账号分支（本地离线认证）里的同名调用不在此约束内。
  const index = login.lastIndexOf(step);
  if (index < 0) throw new Error(`login() 缺少步骤 ${step}`);
  const before = login.slice(Math.max(0, index - 60), index);
  if (!before.includes("withLoginStepTimeout(")) {
    throw new Error(`login() 中 ${step} 必须包在 withLoginStepTimeout 里`);
  }
}
for (const marker of ['startLoginTrace(', 'trace?.finish("success")', "void flushLoginDiagnostics()"]) {
  if (!login.includes(marker)) throw new Error(`login() 缺少登录诊断记录 ${marker}`);
}

console.log("login-step-timeout-source.test.ts: ok");
