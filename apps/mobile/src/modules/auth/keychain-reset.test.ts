import assert from "node:assert/strict";
import { resetLocalKeychainSession, type KeychainResetDeps } from "./keychain-reset";

function makeDeps(overrides: Partial<KeychainResetDeps> = {}) {
  const calls: string[] = [];
  const deps: KeychainResetDeps = {
    clearLocalSession: async () => { calls.push("clear"); },
    listResidue: async () => { calls.push("residue"); return []; },
    probeReadWrite: async () => { calls.push("probe"); },
    ...overrides,
  };
  return { deps, calls };
}

async function main() {
  // 正常：先清理、再核对残留、最后读写自检，三步顺序固定
  const ok = makeDeps();
  assert.deepEqual(await resetLocalKeychainSession(ok.deps), { status: "ok" });
  assert.deepEqual(ok.calls, ["clear", "residue", "probe"]);

  // 清理本身抛错：直接 failed，不再继续核对
  const failed = makeDeps({ clearLocalSession: async () => { throw new Error("boom"); } });
  assert.deepEqual(await resetLocalKeychainSession(failed.deps), { status: "failed", message: "boom" });
  assert.deepEqual(failed.calls, []);

  // 仍有残留：返回残留键名，且不做写入自检（避免掩盖残留）
  const residual = makeDeps({ listResidue: async () => ["hbweb_access_token"] });
  assert.deepEqual(await resetLocalKeychainSession(residual.deps), {
    status: "residual",
    items: ["hbweb_access_token"],
  });

  // 自检失败：Keychain 写入异常
  const probe = makeDeps({ probeReadWrite: async () => { throw new Error("User interaction is not allowed"); } });
  assert.deepEqual(await resetLocalKeychainSession(probe.deps), {
    status: "probeFailed",
    message: "User interaction is not allowed",
  });

  // 核对残留时读 Keychain 抛错，同样视为 Keychain 异常
  const readFail = makeDeps({ listResidue: async () => { throw new Error("read failed"); } });
  assert.deepEqual(await resetLocalKeychainSession(readFail.deps), {
    status: "probeFailed",
    message: "read failed",
  });

  console.log("keychain-reset tests passed");
}

void main();
