import assert from "node:assert/strict";
import test from "node:test";
import { VAULT_MAX_ENTRIES, findEntry, type EntrySourceUser, type VaultEntry } from "./vault";
import { createVaultStore, type VaultSecureBackend, type VaultSentinelBackend } from "./vault-store";
import {
  addAccount,
  forgetSavedAccount,
  saveCurrentAccount,
  signOutCurrentAccount,
  switchToAccount,
  type CurrentSnapshot,
  type SwitchDeps,
} from "./switch-flow";

const HOST = "https://api.example.com";
const NOW = 1_800_000_000_000;

interface TestUser extends EntrySourceUser {
  userGUID: string;
}

function user(guid: string, name = guid): TestUser {
  return { userGUID: guid, username: name, roleNames: ["Order"], storeNames: ["Bankstown"] };
}

function httpError(status: number) {
  return Object.assign(new Error(`http ${status}`), { response: { status } });
}

interface Harness {
  deps: SwitchDeps<TestUser>;
  events: string[];
  vault: ReturnType<typeof createVaultStore>;
  state: {
    current: CurrentSnapshot<TestUser> | null;
    onShift: boolean;
    pending: boolean;
    refreshImpl: (rt: string) => Promise<{ accessToken: string; refreshToken: string }>;
    fetchUserImpl: (access: string) => Promise<TestUser>;
    loginImpl: (u: string, p: string) => Promise<{ accessToken: string; refreshToken: string; user: TestUser }>;
    activateFails: boolean;
    activated: { accessToken: string; refreshToken: string; guid: string }[];
    revoked: string[];
    gateEnds: boolean[];
  };
}

function makeHarness(options: { current?: string; saved?: VaultEntry[] } = {}): Harness {
  const secure = new Map<string, string>();
  let sentinel = false;
  const secureBackend: VaultSecureBackend = {
    get: async (k) => secure.get(k) ?? null,
    set: async (k, v) => void secure.set(k, v),
    delete: async (k) => void secure.delete(k),
  };
  const sentinelBackend: VaultSentinelBackend = {
    has: async () => sentinel,
    set: async () => void (sentinel = true),
    clear: async () => void (sentinel = false),
  };
  const vault = createVaultStore(secureBackend, sentinelBackend);
  const events: string[] = [];
  const state: Harness["state"] = {
    current: options.current
      ? { user: user(options.current), refreshToken: `rt-active-${options.current}`, sessionKind: "account" }
      : null,
    onShift: false,
    pending: false,
    refreshImpl: async (rt) => ({ accessToken: `at-from-${rt}`, refreshToken: `${rt}-rotated` }),
    fetchUserImpl: async (access) => user(access.replace(/^at-from-rt-/, "")),
    loginImpl: async () => {
      throw new Error("not configured");
    },
    activateFails: false,
    activated: [],
    revoked: [],
    gateEnds: [],
  };
  const deps: SwitchDeps<TestUser> = {
    now: () => NOW,
    apiHost: () => HOST,
    vault,
    readCurrent: async () => state.current,
    isOnShift: async () => state.onShift,
    hasPendingWork: () => state.pending,
    refreshWithToken: (rt) => {
      events.push(`refresh:${rt}`);
      return state.refreshImpl(rt);
    },
    fetchUserWithToken: (access) => state.fetchUserImpl(access),
    loginWithCredentials: (u, p) => state.loginImpl(u, p),
    settleTokenRefresh: async () => void events.push("settleRefresh"),
    revokeSession: async (rt) => void state.revoked.push(rt),
    notifyLeaving: async () => void events.push("notifyLeaving"),
    activate: async (session) => {
      events.push("activate");
      if (state.activateFails) throw new Error("activate failed");
      state.activated.push({ accessToken: session.accessToken, refreshToken: session.refreshToken, guid: session.user.userGUID });
      state.current = { user: session.user, refreshToken: session.refreshToken, sessionKind: "account" };
    },
    fullLogout: async () => {
      events.push("fullLogout");
      state.current = null;
    },
    gate: {
      begin: () => void events.push("gate.begin"),
      end: ({ sessionChanged }) => {
        events.push(`gate.end:${sessionChanged}`);
        state.gateEnds.push(sessionChanged);
      },
    },
  };
  return { deps, events, vault, state };
}

function saved(guid: string, overrides: Partial<VaultEntry> = {}): VaultEntry {
  return {
    userGuid: guid,
    username: guid,
    roleNames: ["Order"],
    apiHost: HOST,
    refreshToken: `rt-${guid}`,
    tokenIssuedAt: NOW,
    savedAt: NOW,
    lastUsedAt: NOW - 1000,
    ...overrides,
  };
}

async function seed(h: Harness, entries: VaultEntry[]) {
  for (const entry of entries) await h.vault.upsert(entry);
}

test("切换：成功路径——目标新令牌先落盘，当前账号（已保存）回写最新令牌，再激活", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A", { refreshToken: "rt-A-stale" }), saved("B")]);
  const result = await switchToAccount(h.deps, "B");
  assert.deepEqual(result, { ok: true });

  assert.deepEqual(h.state.activated, [{ accessToken: "at-from-rt-B", refreshToken: "rt-B-rotated", guid: "B" }]);
  const entries = await h.vault.load();
  // 当前账号 A：保险库里换成活动槽位里最新的令牌
  assert.equal(findEntry(entries, "A")?.refreshToken, "rt-active-A");
  // 目标 B：保险库里是轮转后的新令牌，并且标记为刚用过
  assert.equal(findEntry(entries, "B")?.refreshToken, "rt-B-rotated");
  assert.equal(findEntry(entries, "B")?.lastUsedAt, NOW);
  // 顺序：刷新目标 → 通知推送（闸门还开着，推送注销要走 apiClient）→ 关闸门 → 等在途刷新 → 激活 → 放闸门（会话已变）
  assert.deepEqual(h.events, [
    "refresh:rt-B",
    "notifyLeaving",
    "gate.begin",
    "settleRefresh",
    "activate",
    "gate.end:true",
  ]);
  // 已保存的旧账号不吊销
  assert.deepEqual(h.state.revoked, []);
});

test("切换：班中 / 有未完成操作 / 非个人账号会话 → 直接拦截，什么都不碰", async () => {
  const onShift = makeHarness({ current: "A" });
  await seed(onShift, [saved("B")]);
  onShift.state.onShift = true;
  assert.deepEqual(await switchToAccount(onShift.deps, "B"), { ok: false, reason: "onShift" });
  assert.deepEqual(onShift.events, []);

  const busy = makeHarness({ current: "A" });
  await seed(busy, [saved("B")]);
  busy.state.pending = true;
  assert.deepEqual(await switchToAccount(busy.deps, "B"), { ok: false, reason: "busy" });

  const device = makeHarness({ current: "A" });
  await seed(device, [saved("B")]);
  device.state.current = { user: user("A"), refreshToken: "rt", sessionKind: "device" };
  assert.deepEqual(await switchToAccount(device.deps, "B"), { ok: false, reason: "notAccountSession" });
});

test("切换：目标不在列表 / 就是当前账号 / 已标记需重登", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B", { needsRelogin: true })]);
  assert.deepEqual(await switchToAccount(h.deps, "nobody"), { ok: false, reason: "unknownAccount" });
  assert.deepEqual(await switchToAccount(h.deps, "a"), { ok: false, reason: "sameAccount" });
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "needsRelogin" });
  assert.deepEqual(h.events, []);
});

test("切换：服务器拒绝令牌 → 标记需重登并放闸门，当前账号原封不动", async () => {
  for (const status of [401, 403, 404]) {
    const h = makeHarness({ current: "A" });
    await seed(h, [saved("B")]);
    h.state.refreshImpl = async () => {
      throw httpError(status);
    };
    assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "needsRelogin" });
    assert.equal(findEntry(await h.vault.load(), "B")?.needsRelogin, true);
    assert.deepEqual(h.state.gateEnds, []);
    assert.equal(h.state.activated.length, 0);
    assert.equal(h.state.current?.user.userGUID, "A");
  }
});

test("切换：网络 / 5xx 错误 → 不标需重登（令牌没丢，可重试）", async () => {
  for (const error of [new Error("Network Error"), httpError(503)]) {
    const h = makeHarness({ current: "A" });
    await seed(h, [saved("B")]);
    h.state.refreshImpl = async () => {
      throw error;
    };
    assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "networkError" });
    assert.equal(findEntry(await h.vault.load(), "B")?.needsRelogin, undefined);
    assert.deepEqual(h.state.gateEnds, []);
  }
});

test("切换：刷新成功但核对身份时断网 → 新令牌已落盘，下次重试能用", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("B")]);
  h.state.fetchUserImpl = async () => {
    throw new Error("Network Error");
  };
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "networkError" });
  assert.equal(findEntry(await h.vault.load(), "B")?.refreshToken, "rt-B-rotated");
  assert.equal(findEntry(await h.vault.load(), "B")?.needsRelogin, undefined);
  assert.equal(h.state.activated.length, 0);
});

test("切换：令牌对应的人不是保存的那个 → 拒绝并标需重登，不激活", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("B")]);
  h.state.fetchUserImpl = async () => user("SOMEONE-ELSE");
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "identityMismatch" });
  assert.equal(findEntry(await h.vault.load(), "B")?.needsRelogin, true);
  assert.equal(h.state.activated.length, 0);
});

test("切换：当前账号未保存 + 选「不保存」→ 切走后吊销它的会话，不入库", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("B")]);
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: true });
  assert.deepEqual(h.state.revoked, ["rt-active-A"]);
  assert.equal(findEntry(await h.vault.load(), "A"), undefined);
});

test("切换：当前账号未保存 + 「保存并切换」→ 入库（用活动槽位令牌），不吊销", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("B")]);
  assert.deepEqual(await switchToAccount(h.deps, "B", { saveCurrent: true }), { ok: true });
  assert.equal(findEntry(await h.vault.load(), "A")?.refreshToken, "rt-active-A");
  assert.deepEqual(h.state.revoked, []);
});

test("切换：「保存并切换」但列表已满 → 在动任何东西之前拒绝", async () => {
  const h = makeHarness({ current: "A" });
  const others = Array.from({ length: VAULT_MAX_ENTRIES }, (_, i) => saved(`S${i}`));
  await seed(h, others);
  assert.deepEqual(await switchToAccount(h.deps, "S0", { saveCurrent: true }), { ok: false, reason: "vaultFull" });
  assert.deepEqual(h.events, []);
});

test("切换：激活失败 → 报告失败并放闸门（会话已被清场）", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B")]);
  h.state.activateFails = true;
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "activationFailed" });
  assert.deepEqual(h.state.gateEnds, [true]);
  assert.deepEqual(h.state.revoked, []);
});

test("添加账号：默认不保存——登录成功切过去，列表里没有它；未保存的旧账号被吊销", async () => {
  const h = makeHarness({ current: "A" });
  h.state.loginImpl = async () => ({ accessToken: "at-N", refreshToken: "rt-N", user: user("N") });
  const result = await addAccount(h.deps, { username: "n", password: "pw", save: false });
  assert.deepEqual(result, { ok: true });
  assert.equal(h.state.current?.user.userGUID, "N");
  assert.equal(findEntry(await h.vault.load(), "N"), undefined);
  assert.deepEqual(h.state.revoked, ["rt-active-A"]);
});

test("添加账号：勾选保存 → 新账号入库；当前账号已保存则保持保存", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A")]);
  h.state.loginImpl = async () => ({ accessToken: "at-N", refreshToken: "rt-N", user: user("N") });
  assert.deepEqual(await addAccount(h.deps, { username: "n", password: "pw", save: true }), { ok: true });
  const entries = await h.vault.load();
  assert.equal(findEntry(entries, "N")?.refreshToken, "rt-N");
  assert.equal(findEntry(entries, "A")?.refreshToken, "rt-active-A");
  assert.deepEqual(h.state.revoked, []);
});

test("添加账号：登录失败（密码错）→ 当前账号一点没动，错误原样带回", async () => {
  const h = makeHarness({ current: "A" });
  const failure = httpError(401);
  h.state.loginImpl = async () => {
    throw failure;
  };
  const result = await addAccount(h.deps, { username: "n", password: "bad", save: true });
  assert.equal(result.ok, false);
  assert.equal((result as { reason: string }).reason, "loginFailed");
  assert.equal((result as { error: unknown }).error, failure);
  assert.equal(h.state.current?.user.userGUID, "A");
  assert.deepEqual(h.state.gateEnds, []);
  assert.deepEqual(h.events.filter((e) => e === "activate"), []);
});

test("添加账号：登录的就是当前账号 → 吊销多出来的会话，不切换", async () => {
  const h = makeHarness({ current: "A" });
  h.state.loginImpl = async () => ({ accessToken: "at-A2", refreshToken: "rt-A2", user: user("a") });
  assert.deepEqual(await addAccount(h.deps, { username: "a", password: "pw", save: true }), {
    ok: false,
    reason: "sameAccount",
  });
  assert.deepEqual(h.state.revoked, ["rt-A2"]);
});

test("添加账号：列表放不下新账号 → 吊销新会话，不切换", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, Array.from({ length: VAULT_MAX_ENTRIES }, (_, i) => saved(`S${i}`)));
  h.state.loginImpl = async () => ({ accessToken: "at-N", refreshToken: "rt-N", user: user("N") });
  assert.deepEqual(await addAccount(h.deps, { username: "n", password: "pw", save: true }), {
    ok: false,
    reason: "vaultFull",
  });
  assert.deepEqual(h.state.revoked, ["rt-N"]);
  assert.equal(h.state.current?.user.userGUID, "A");
});

test("添加账号：班中直接拦截，不发登录请求", async () => {
  const h = makeHarness({ current: "A" });
  h.state.onShift = true;
  let called = false;
  h.state.loginImpl = async () => {
    called = true;
    throw new Error("should not run");
  };
  assert.deepEqual(await addAccount(h.deps, { username: "n", password: "pw", save: true }), {
    ok: false,
    reason: "onShift",
  });
  assert.equal(called, false);
});

test("保存当前账号 / 移除已保存账号", async () => {
  const h = makeHarness({ current: "A" });
  assert.deepEqual(await saveCurrentAccount(h.deps), { ok: true });
  assert.equal(findEntry(await h.vault.load(), "A")?.refreshToken, "rt-active-A");

  await seed(h, [saved("B")]);
  await forgetSavedAccount(h.deps, "b");
  assert.equal(findEntry(await h.vault.load(), "B"), undefined);
  assert.deepEqual(h.state.revoked, ["rt-B"]);

  const full = makeHarness({ current: "A" });
  await seed(full, Array.from({ length: VAULT_MAX_ENTRIES }, (_, i) => saved(`S${i}`)));
  assert.deepEqual(await saveCurrentAccount(full.deps), { ok: false, reason: "vaultFull" });
});

test("退出当前账号：还有可用的已保存账号 → 移除当前并切过去，吊销旧会话", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B", { lastUsedAt: NOW - 10 })]);
  const result = await signOutCurrentAccount(h.deps);
  assert.equal(result.ok, true);
  assert.equal((result as { switchedTo: VaultEntry | null }).switchedTo?.userGuid, "B");
  assert.equal(h.state.current?.user.userGUID, "B");
  assert.equal(findEntry(await h.vault.load(), "A"), undefined);
  assert.deepEqual(h.state.revoked, ["rt-active-A"]);
  assert.ok(!h.events.includes("fullLogout"));
});

test("退出当前账号：没有可用的下一个账号 → 完整退出回登录页", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B", { needsRelogin: true })]);
  const result = await signOutCurrentAccount(h.deps);
  assert.deepEqual(result, { ok: true, switchedTo: null });
  assert.ok(h.events.includes("fullLogout"));
  assert.equal(findEntry(await h.vault.load(), "A"), undefined);
});

test("退出当前账号：下一个账号起不来 → 照样退出当前账号，不把人卡住", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B")]);
  h.state.refreshImpl = async () => {
    throw httpError(401);
  };
  const result = await signOutCurrentAccount(h.deps);
  assert.deepEqual(result, { ok: true, switchedTo: null });
  assert.ok(h.events.includes("fullLogout"));
  assert.equal(findEntry(await h.vault.load(), "B")?.needsRelogin, true);
  assert.equal(findEntry(await h.vault.load(), "A"), undefined);
});

test("退出当前账号：有下一个账号但班中 → 拦截，当前账号不退出", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B")]);
  h.state.onShift = true;
  assert.deepEqual(await signOutCurrentAccount(h.deps), { ok: false, reason: "onShift" });
  assert.equal(h.state.current?.user.userGUID, "A");
  assert.equal(findEntry(await h.vault.load(), "A")?.userGuid, "A");
});

test("切换：关闸门后发现当前会话已变（被登出 / 换人）→ 放闸门、不激活、不写保险库", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B")]);
  const original = h.deps.settleTokenRefresh;
  h.deps.settleTokenRefresh = async () => {
    await original();
    h.state.current = null; // 等刷新的间隙，用户被登出了
  };
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "notAccountSession" });
  assert.equal(h.state.activated.length, 0);
  assert.deepEqual(h.state.gateEnds, [false]);
});

test("切换：等在途刷新后读到更新的令牌 → 回写的是最新那份", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A", { refreshToken: "rt-A-stale" }), saved("B")]);
  const original = h.deps.settleTokenRefresh;
  h.deps.settleTokenRefresh = async () => {
    await original();
    if (h.state.current) h.state.current = { ...h.state.current, refreshToken: "rt-A-after-refresh" };
  };
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: true });
  assert.equal(findEntry(await h.vault.load(), "A")?.refreshToken, "rt-A-after-refresh");
});

test("切换：激活前本机存储读写抛错 → 闸门必须放开（否则整个 App 请求挂死），会话未变", async () => {
  const h = makeHarness({ current: "A" });
  await seed(h, [saved("A"), saved("B")]);
  // 回写当前账号令牌时 Keychain 抛错（如锁屏）
  const originalUpdate = h.deps.vault.update;
  let calls = 0;
  h.deps.vault.update = async (...args) => {
    calls += 1;
    // 第 1 次是「目标新令牌落盘」（放行），第 2 次才是闸门内的「回写当前账号」
    if (calls === 2) throw new Error("keychain locked");
    return originalUpdate(...args);
  };
  assert.deepEqual(await switchToAccount(h.deps, "B"), { ok: false, reason: "storageError" });
  assert.deepEqual(h.state.gateEnds, [false]);
  assert.equal(h.state.activated.length, 0);
  assert.equal(h.state.current?.user.userGUID, "A");
});

test("移除已保存账号：只向签发它的服务器吊销（改过服务器地址就不发）", async () => {
  const same = makeHarness({ current: "A" });
  await seed(same, [saved("B")]);
  await forgetSavedAccount(same.deps, "B");
  assert.deepEqual(same.state.revoked, ["rt-B"]);

  const other = makeHarness({ current: "A" });
  await seed(other, [saved("B", { apiHost: "http://192.168.1.9:5002/api" })]);
  await forgetSavedAccount(other.deps, "B");
  assert.deepEqual(other.state.revoked, []);
  assert.equal(findEntry(await other.vault.load(), "B"), undefined);
});
