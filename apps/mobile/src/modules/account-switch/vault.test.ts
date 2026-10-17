import assert from "node:assert/strict";
import test from "node:test";
import {
  VAULT_MAX_ENTRIES,
  VAULT_TOKEN_TTL_MS,
  buildEntryFromUser,
  describeEntry,
  findEntry,
  markNeedsRelogin,
  parseEntry,
  pickNextUsableEntry,
  removeEntry,
  sortForDisplay,
  upsertEntry,
  withFreshToken,
  type VaultEntry,
} from "./vault";
import {
  VAULT_INDEX_KEY,
  VAULT_SENTINEL_KEY,
  createVaultStore,
  vaultEntryKey,
  type VaultSecureBackend,
  type VaultSentinelBackend,
} from "./vault-store";

const HOST = "https://api.example.com";
const NOW = 1_800_000_000_000;

function entry(guid: string, overrides: Partial<VaultEntry> = {}): VaultEntry {
  return {
    userGuid: guid,
    username: `user-${guid}`,
    roleNames: ["Order"],
    apiHost: HOST,
    refreshToken: `rt-${guid}`,
    tokenIssuedAt: NOW,
    savedAt: NOW,
    lastUsedAt: NOW,
    ...overrides,
  };
}

test("parseEntry：字段缺失 / 类型不对直接丢弃，合法的规整空白", () => {
  assert.equal(parseEntry(null), null);
  assert.equal(parseEntry({ userGuid: "a" }), null);
  assert.equal(parseEntry({ ...entry("a"), refreshToken: "" }), null);
  assert.equal(parseEntry({ ...entry("a"), tokenIssuedAt: "x" }), null);
  const parsed = parseEntry({ ...entry(" A "), roleNames: ["Order", 3, ""], needsRelogin: "yes" });
  assert.equal(parsed?.userGuid, "A");
  assert.deepEqual(parsed?.roleNames, ["Order"]);
  assert.equal(parsed?.needsRelogin, undefined);
});

test("findEntry / removeEntry：GUID 忽略大小写与空白", () => {
  const list = [entry("AAA-1"), entry("bbb-2")];
  assert.equal(findEntry(list, " aaa-1 ")?.username, "user-AAA-1");
  assert.equal(findEntry(list, ""), undefined);
  assert.deepEqual(removeEntry(list, "BBB-2").map((e) => e.userGuid), ["AAA-1"]);
});

test("upsertEntry：新增 / 更新（保留首次保存时间）/ 满了拒绝而不是挤掉别人", () => {
  let list: VaultEntry[] = [];
  for (let i = 0; i < VAULT_MAX_ENTRIES; i += 1) {
    const result = upsertEntry(list, entry(`g${i}`));
    assert.equal(result.status, "added");
    list = result.entries;
  }
  const full = upsertEntry(list, entry("extra"));
  assert.equal(full.status, "full");
  assert.equal(full.entries.length, VAULT_MAX_ENTRIES);

  const updated = upsertEntry(list, entry("G0", { refreshToken: "new", savedAt: NOW + 5 }));
  assert.equal(updated.status, "updated");
  const g0 = findEntry(updated.entries, "g0");
  assert.equal(g0?.refreshToken, "new");
  assert.equal(g0?.savedAt, NOW);
});

test("describeEntry：需重登 > 换了服务器 > 过期 > 可用", () => {
  assert.equal(describeEntry(entry("a"), NOW, HOST), "ok");
  assert.equal(describeEntry(entry("a"), NOW, `${HOST}/`), "ok");
  assert.equal(describeEntry(entry("a", { needsRelogin: true }), NOW, HOST), "needsRelogin");
  assert.equal(describeEntry(entry("a"), NOW, "https://other.example.com"), "otherHost");
  assert.equal(describeEntry(entry("a"), NOW + VAULT_TOKEN_TTL_MS + 1, HOST), "expired");
  assert.equal(describeEntry(entry("a"), NOW + VAULT_TOKEN_TTL_MS, HOST), "ok");
});

test("withFreshToken 清掉需重登标记；markNeedsRelogin 只动目标", () => {
  const marked = markNeedsRelogin([entry("a"), entry("b")], "A");
  assert.equal(marked[0].needsRelogin, true);
  assert.equal(marked[1].needsRelogin, undefined);
  const fresh = withFreshToken(marked[0], "rt-new", NOW + 10);
  assert.equal(fresh.needsRelogin, undefined);
  assert.equal(fresh.refreshToken, "rt-new");
  assert.equal(fresh.tokenIssuedAt, NOW + 10);
});

test("sortForDisplay 最近使用在前；pickNextUsableEntry 跳过当前、需重登和过期的", () => {
  const list = [
    entry("old", { lastUsedAt: NOW - 3000 }),
    entry("dead", { lastUsedAt: NOW - 1000, needsRelogin: true }),
    entry("cur", { lastUsedAt: NOW }),
    entry("recent", { lastUsedAt: NOW - 2000 }),
  ];
  assert.deepEqual(sortForDisplay(list).map((e) => e.userGuid), ["cur", "dead", "recent", "old"]);
  assert.equal(pickNextUsableEntry(list, "cur", NOW, HOST)?.userGuid, "recent");
  assert.equal(pickNextUsableEntry([entry("cur")], "cur", NOW, HOST), null);
});

test("buildEntryFromUser：取第一个分店名，保留首次保存时间", () => {
  const built = buildEntryFromUser(
    { userGUID: "G-1", username: " kevin ", fullName: "Kevin", roleNames: ["Manager"], storeNames: ["Bankstown", "Wallsend"] },
    "rt",
    HOST,
    NOW,
    { savedAt: NOW - 100 }
  );
  assert.equal(built.userGuid, "G-1");
  assert.equal(built.username, "kevin");
  assert.equal(built.storeLabel, "Bankstown");
  assert.equal(built.savedAt, NOW - 100);
  assert.equal(built.lastUsedAt, NOW);
});

// ───────── 存储层（内存后端）

function memoryBackends() {
  const secure = new Map<string, string>();
  let sentinel = false;
  const secureBackend: VaultSecureBackend = {
    get: async (key) => secure.get(key) ?? null,
    set: async (key, value) => void secure.set(key, value),
    delete: async (key) => void secure.delete(key),
  };
  const sentinelBackend: VaultSentinelBackend = {
    has: async () => sentinel,
    set: async () => void (sentinel = true),
    clear: async () => void (sentinel = false),
  };
  return { secure, secureBackend, sentinelBackend, hasSentinel: () => sentinel };
}

test("vault-store：存取一圈，每个账号独立一个键，索引与哨兵同步", async () => {
  const mem = memoryBackends();
  const store = createVaultStore(mem.secureBackend, mem.sentinelBackend);
  assert.deepEqual(await store.load(), []);

  assert.equal(await store.upsert(entry("AAA")), "added");
  assert.equal(await store.upsert(entry("BBB")), "added");
  assert.ok(mem.secure.has(vaultEntryKey("aaa")));
  assert.deepEqual(JSON.parse(mem.secure.get(VAULT_INDEX_KEY) ?? "[]"), ["aaa", "bbb"]);
  assert.equal(mem.hasSentinel(), true);
  assert.equal((await store.load()).length, 2);

  assert.equal(await store.update("aaa", (e) => ({ ...e, refreshToken: "rt-new" })), true);
  assert.equal(await store.update("zzz", (e) => e), false);
  assert.equal((await store.load()).find((e) => e.userGuid === "AAA")?.refreshToken, "rt-new");

  await store.remove("AAA");
  assert.equal(mem.secure.has(vaultEntryKey("aaa")), false);
  assert.deepEqual((await store.load()).map((e) => e.userGuid), ["BBB"]);

  await store.remove("BBB");
  assert.equal(mem.secure.has(VAULT_INDEX_KEY), false);
  assert.equal(mem.hasSentinel(), false);
});

test("vault-store：有数据却没有哨兵（卸载重装残留）→ 清空而不是恢复", async () => {
  const mem = memoryBackends();
  const store = createVaultStore(mem.secureBackend, mem.sentinelBackend);
  await store.upsert(entry("AAA"));
  await mem.sentinelBackend.clear(); // 模拟 AsyncStorage 被清空
  assert.deepEqual(await store.load(), []);
  assert.equal(mem.secure.size, 0);
});

test("vault-store：单条损坏只丢这一条；满了拒绝新增；clear 清干净", async () => {
  const mem = memoryBackends();
  const store = createVaultStore(mem.secureBackend, mem.sentinelBackend);
  await store.upsert(entry("AAA"));
  await store.upsert(entry("BBB"));
  mem.secure.set(vaultEntryKey("aaa"), "{not json");
  assert.deepEqual((await store.load()).map((e) => e.userGuid), ["BBB"]);

  for (let i = 0; i < VAULT_MAX_ENTRIES; i += 1) await store.upsert(entry(`x${i}`));
  const before = (await store.load()).length;
  assert.equal(await store.upsert(entry("overflow")), "full");
  assert.equal((await store.load()).length, before);

  await store.clear();
  assert.equal(mem.secure.size, 0);
  assert.equal(mem.hasSentinel(), false);
  assert.ok(VAULT_SENTINEL_KEY.length > 0);
});

test("vault-store：并发的读改写串行执行，互不覆盖", async () => {
  const mem = memoryBackends();
  const store = createVaultStore(mem.secureBackend, mem.sentinelBackend);
  await Promise.all([store.upsert(entry("A")), store.upsert(entry("B")), store.upsert(entry("C"))]);
  assert.equal((await store.load()).length, 3);
});
