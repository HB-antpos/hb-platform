import assert from "node:assert/strict";
import Module from "node:module";
import test from "node:test";

// AsyncStorage 用内存替身：只验证本机偏好 / 最近访问的读写口径。
const store = new Map<string, string>();
let failReads = false;
const filename = require.resolve("@react-native-async-storage/async-storage");
const mocked = new Module(filename);
mocked.filename = filename;
mocked.loaded = true;
mocked.exports = {
  __esModule: true,
  default: {
    getItem: async (key: string) => {
      if (failReads) throw new Error("storage down");
      return store.get(key) ?? null;
    },
    setItem: async (key: string, value: string) => {
      store.set(key, value);
    },
    removeItem: async (key: string) => {
      store.delete(key);
    },
  },
};
require.cache[filename] = mocked;

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

test("悬浮按钮默认开启；只有用户明确关过（存了 false）才保持关闭", async () => {
  const {
    FLOATING_LANGUAGE_STORAGE_KEY,
    hydrateFloatingLanguagePreference,
    normalizeFloatingLanguagePreference,
    resetFloatingLanguagePreferenceForTests,
    useFloatingLanguageStore,
  } = await import("./preference");

  // 没存过、存坏了：都按默认开启。
  assert.equal(normalizeFloatingLanguagePreference(null).enabled, true);
  assert.equal(normalizeFloatingLanguagePreference({}).enabled, true);
  assert.equal(normalizeFloatingLanguagePreference({ enabled: "false" }).enabled, true);
  assert.equal(normalizeFloatingLanguagePreference({ enabled: true }).enabled, true);
  assert.equal(normalizeFloatingLanguagePreference({ enabled: false }).enabled, false);

  // 全新安装：开启，读盘完成后才显示。
  store.clear();
  resetFloatingLanguagePreferenceForTests();
  assert.equal(useFloatingLanguageStore.getState().hydrated, false);
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, true);
  assert.equal(useFloatingLanguageStore.getState().hydrated, true);

  // 用户之前关过：升级后仍然关闭，位置也保留。
  store.set(
    FLOATING_LANGUAGE_STORAGE_KEY,
    JSON.stringify({ enabled: false, position: { side: "left", yRatio: 0.3 } })
  );
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, false);
  assert.deepEqual(useFloatingLanguageStore.getState().position, { side: "left", yRatio: 0.3 });

  // 读盘失败：保持默认开启，不抛错、不阻塞。
  failReads = true;
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, true);
  assert.equal(useFloatingLanguageStore.getState().hydrated, true);
  failReads = false;
});

test("读盘完成前用户先关闭：以用户操作为准，存储里的位置保留，合并后一起落盘", async () => {
  const {
    FLOATING_LANGUAGE_STORAGE_KEY,
    hydrateFloatingLanguagePreference,
    resetFloatingLanguagePreferenceForTests,
    useFloatingLanguageStore,
  } = await import("./preference");
  store.clear();
  store.set(FLOATING_LANGUAGE_STORAGE_KEY, JSON.stringify({ position: { side: "left", yRatio: 0.4 } }));
  resetFloatingLanguagePreferenceForTests();
  const pending = hydrateFloatingLanguagePreference();
  useFloatingLanguageStore.getState().setEnabled(false);
  await pending;
  await flush();
  assert.equal(useFloatingLanguageStore.getState().enabled, false);
  assert.deepEqual(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}"), {
    enabled: false,
    position: { side: "left", yRatio: 0.4 },
    longPressMs: 2000,
  });
});

test("读盘完成前用户先打开：开关以用户操作为准，存储里的位置照样保留，合并后一起落盘", async () => {
  const {
    FLOATING_LANGUAGE_STORAGE_KEY,
    hydrateFloatingLanguagePreference,
    resetFloatingLanguagePreferenceForTests,
    useFloatingLanguageStore,
  } = await import("./preference");
  store.clear();
  store.set(
    FLOATING_LANGUAGE_STORAGE_KEY,
    JSON.stringify({ enabled: false, position: { side: "left", yRatio: 0.3 } })
  );
  resetFloatingLanguagePreferenceForTests();
  const pending = hydrateFloatingLanguagePreference();
  useFloatingLanguageStore.getState().setEnabled(true);
  // 读盘完成前不写盘：存储里还是原值，不会被默认位置盖掉。
  assert.deepEqual(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}").position, { side: "left", yRatio: 0.3 });
  await pending;
  await flush();
  assert.equal(useFloatingLanguageStore.getState().enabled, true);
  assert.deepEqual(useFloatingLanguageStore.getState().position, { side: "left", yRatio: 0.3 });
  assert.deepEqual(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}"), {
    enabled: true,
    position: { side: "left", yRatio: 0.3 },
    longPressMs: 2000,
  });

  // 读盘完成后的修改立即落盘。
  useFloatingLanguageStore.getState().setPosition({ side: "right", yRatio: 0.8 });
  await flush();
  assert.deepEqual(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}").position, { side: "right", yRatio: 0.8 });
});

test("最近访问按账号隔离：换人不显示上一个人的记录", async () => {
  const {
    QUICK_JUMP_RECENT_STORAGE_KEY,
    loadRecentRoutesForScope,
    normalizeStoredRecent,
    resetQuickJumpRecentForTests,
    useQuickJumpRecentStore,
  } = await import("./recent-routes");

  assert.deepEqual(normalizeStoredRecent({ scope: "account:a", routes: ["x", "x", 3, "y"] }, "account:a"), ["x", "y"]);
  assert.deepEqual(normalizeStoredRecent({ scope: "account:a", routes: ["x"] }, "account:b"), []);

  store.clear();
  resetQuickJumpRecentForTests();
  useQuickJumpRecentStore.getState().replace("account:a", ["reports", "users"]);
  await flush();
  assert.deepEqual(JSON.parse(store.get(QUICK_JUMP_RECENT_STORAGE_KEY) ?? "{}"), {
    scope: "account:a",
    routes: ["reports", "users"],
  });

  resetQuickJumpRecentForTests();
  await loadRecentRoutesForScope("account:b");
  assert.deepEqual(useQuickJumpRecentStore.getState(), {
    ...useQuickJumpRecentStore.getState(),
    scope: "account:b",
    routes: [],
  });

  resetQuickJumpRecentForTests();
  await loadRecentRoutesForScope("account:a");
  assert.deepEqual(useQuickJumpRecentStore.getState().routes, ["reports", "users"]);
});

test("按住时长：默认 2 秒、设置后落盘、重启读回；读盘前改的以用户为准且不丢位置", async () => {
  const {
    FLOATING_LANGUAGE_STORAGE_KEY,
    hydrateFloatingLanguagePreference,
    normalizeFloatingLanguagePreference,
    resetFloatingLanguagePreferenceForTests,
    useFloatingLanguageStore,
  } = await import("./preference");

  assert.equal(normalizeFloatingLanguagePreference(null).longPressMs, 2000);
  assert.equal(normalizeFloatingLanguagePreference({ longPressMs: "x" }).longPressMs, 2000);
  assert.equal(normalizeFloatingLanguagePreference({ longPressMs: 1000 }).longPressMs, 1000);

  // 读盘完成后设置：立即落盘，写入的值已归一化。
  store.clear();
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().longPressMs, 2000);
  useFloatingLanguageStore.getState().setLongPressMs(3000);
  await flush();
  assert.equal(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}").longPressMs, 3000);
  useFloatingLanguageStore.getState().setLongPressMs(99999);
  assert.equal(useFloatingLanguageStore.getState().longPressMs, 3000);
  useFloatingLanguageStore.getState().setLongPressMs(10);
  assert.equal(useFloatingLanguageStore.getState().longPressMs, 1000);

  // 「重启」：从存储读回。
  useFloatingLanguageStore.getState().setLongPressMs(1500);
  await flush();
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().longPressMs, 1500);

  // 读盘前先改时长：以用户为准，存储里的开关与位置保留。
  store.clear();
  store.set(
    FLOATING_LANGUAGE_STORAGE_KEY,
    JSON.stringify({ enabled: false, position: { side: "left", yRatio: 0.2 }, longPressMs: 3000 })
  );
  resetFloatingLanguagePreferenceForTests();
  const pending = hydrateFloatingLanguagePreference();
  useFloatingLanguageStore.getState().setLongPressMs(1000);
  await pending;
  await flush();
  assert.deepEqual(JSON.parse(store.get(FLOATING_LANGUAGE_STORAGE_KEY) ?? "{}"), {
    enabled: false,
    position: { side: "left", yRatio: 0.2 },
    longPressMs: 1000,
  });
});
