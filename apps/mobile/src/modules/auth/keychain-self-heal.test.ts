import assert from "node:assert/strict";
import {
  ensureKeychainHealthy,
  isKeychainSuspectLoginFailure,
  type KeychainSelfHealDeps,
} from "./keychain-self-heal";
import { LoginStepTimeoutError } from "./login-diagnostics";

function makeDeps(overrides: Partial<KeychainSelfHealDeps> & { dirty?: boolean } = {}) {
  const calls: string[] = [];
  const { dirty = false, ...rest } = overrides;
  const deps: KeychainSelfHealDeps = {
    isDirty: async () => dirty,
    markDirty: async () => { calls.push("markDirty"); },
    clearDirty: async () => { calls.push("clearDirty"); },
    probeReadWrite: async () => { calls.push("probe"); },
    listResidue: async () => { calls.push("residue"); return []; },
    forceClearAccountSession: async () => { calls.push("forceClear"); },
    ...rest,
  };
  return { deps, calls };
}

async function main() {
  // 健康且无标记：只自检，不清理
  const healthy = makeDeps();
  assert.equal(await ensureKeychainHealthy(healthy.deps), "healthy");
  assert.ok(!healthy.calls.includes("forceClear"));

  // 健康但有标记（上次在 Keychain 步骤失败）：强制清理后复检通过并清标记
  const dirty = makeDeps({ dirty: true });
  assert.equal(await ensureKeychainHealthy(dirty.deps), "repaired");
  assert.ok(dirty.calls.includes("forceClear"));
  assert.ok(dirty.calls.includes("clearDirty"));

  // 自检失败，清理后恢复：repaired
  let probeCount = 0;
  const flaky = makeDeps({
    probeReadWrite: async () => { if (probeCount++ === 0) throw new Error("User interaction is not allowed"); },
  });
  assert.equal(await ensureKeychainHealthy(flaky.deps), "repaired");
  assert.ok(flaky.calls.includes("forceClear"));

  // 读取账号项就抛错（项已损坏）：清理后读取正常即修复
  let listCount = 0;
  const unreadable = makeDeps({
    listResidue: async () => { if (listCount++ === 0) throw new Error("decrypt failed"); return []; },
  });
  assert.equal(await ensureKeychainHealthy(unreadable.deps), "repaired");

  // 清理后仍写不进去：unrepaired 并保留标记；不抛错（不能拦登录）
  const broken = makeDeps({ probeReadWrite: async () => { throw new Error("keychain down"); } });
  assert.equal(await ensureKeychainHealthy(broken.deps), "unrepaired");
  assert.ok(broken.calls.includes("markDirty"));
  assert.ok(!broken.calls.includes("clearDirty"));

  // 仅有旧会话残留、读写正常、无标记：登录会直接覆盖，算健康，不清理
  const leftover = makeDeps({ listResidue: async () => ["hbweb_access_token"] });
  assert.equal(await ensureKeychainHealthy(leftover.deps), "healthy");
  assert.ok(!leftover.calls.includes("forceClear"));

  // 有标记要求强制清理，但清理后仍有残留（删不掉）：unrepaired
  const residue = makeDeps({ dirty: true, listResidue: async () => ["hbweb_access_token"] });
  assert.equal(await ensureKeychainHealthy(residue.deps), "unrepaired");
  assert.ok(residue.calls.includes("markDirty"));

  // 读标记失败当作没有标记，清理动作抛错也不外溢
  const noisy = makeDeps({
    isDirty: async () => { throw new Error("asyncstorage down"); },
    forceClearAccountSession: async () => { throw new Error("delete failed"); },
    probeReadWrite: async () => { throw new Error("keychain down"); },
  });
  assert.equal(await ensureKeychainHealthy(noisy.deps), "unrepaired");

  // 失败位置判定：只有 Keychain 写入步骤才算嫌疑
  assert.equal(isKeychainSuspectLoginFailure("loginApi", new Error("x")), true, "loginApi 之后 = 存 accessToken 出错");
  assert.equal(isKeychainSuspectLoginFailure("saveAccessToken", new Error("x")), true);
  assert.equal(isKeychainSuspectLoginFailure("currentUser", new Error("x")), true, "currentUser 之后 = 存用户出错");
  assert.equal(isKeychainSuspectLoginFailure("saveRefreshToken", new Error("Network Error")), false, "下一步是联网取用户");
  assert.equal(isKeychainSuspectLoginFailure("localClear", new Error("bad password")), false, "登录接口失败（含密码错误）");
  assert.equal(isKeychainSuspectLoginFailure("saveUser", new Error("x")), false, "sessionMarker 写 AsyncStorage");
  assert.equal(isKeychainSuspectLoginFailure("sessionMarker", new Error("menu")), false);
  assert.equal(isKeychainSuspectLoginFailure(undefined, new Error("x")), false);
  // 超时直接用超时的阶段，不看最后完成的阶段
  assert.equal(isKeychainSuspectLoginFailure("localClear", new LoginStepTimeoutError("saveAccessToken", 8000)), true);
  assert.equal(isKeychainSuspectLoginFailure("loginApi", new LoginStepTimeoutError("currentUser", 35000)), false);

  console.log("keychain-self-heal tests passed");
}

void main();
