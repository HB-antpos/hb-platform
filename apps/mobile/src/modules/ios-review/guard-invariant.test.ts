import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { shouldReleaseIosReviewGuard } from "./guard-invariant";

const moduleDir = dirname(fileURLToPath(import.meta.url));

const base = { isAuthenticated: true, sessionKind: "account", iosReviewOfflineGuardActive: true };

async function run() {
  // 本次缺陷：退出 → 换账号登录后，真实账号会话已认证，守卫却粘在真上 → 必须释放。
  assert.equal(shouldReleaseIosReviewGuard(base, false), true);
  for (const kind of ["account", "deviceAccount"]) {
    assert.equal(shouldReleaseIosReviewGuard({ ...base, sessionKind: kind }, false), true, `${kind} 已认证应释放`);
  }

  // 守卫本来就是假：无事可做，也避免无谓的 setState。
  assert.equal(shouldReleaseIosReviewGuard({ ...base, iosReviewOfflineGuardActive: false }, false), false);

  // 审核合规：审核会话无论哪种形式都不能释放（离线审核必须保持 fail-closed）。
  assert.equal(shouldReleaseIosReviewGuard({ ...base, sessionKind: "iosReview" }, false), false);
  assert.equal(shouldReleaseIosReviewGuard(base, true), false, "审核 marker 仍在时不能释放");

  // 未认证阶段（登录页、设备会话尚未校验）的守卫是设计内的 pre-auth fail-closed，不能动。
  assert.equal(shouldReleaseIosReviewGuard({ ...base, isAuthenticated: false }, false), false);
  assert.equal(
    shouldReleaseIosReviewGuard({ ...base, isAuthenticated: false, sessionKind: "device" }, false),
    false,
  );

  // 接线：auth-store 必须订阅自身状态并调用该判定，释放时只改守卫这一个字段。
  const authStore = await readFile(resolve(moduleDir, "../../store/auth-store.ts"), "utf8");
  assert.match(authStore, /from "@\/modules\/ios-review\/guard-invariant"/);
  assert.match(
    authStore,
    /useAuthStore\.subscribe\(\(state\)\s*=>\s*\{\s*if \(shouldReleaseIosReviewGuard\(state, isIosReviewSessionActive\(\)\)\)\s*\{\s*useAuthStore\.setState\(\{ iosReviewOfflineGuardActive: false \}\);/,
  );

  console.log("guard-invariant.test.ts: ok");
}

void run();
