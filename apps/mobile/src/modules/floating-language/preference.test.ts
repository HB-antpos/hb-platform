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

test("悬浮按钮默认关闭；只有明确存了 true 才开启", async () => {
  const {
    FLOATING_LANGUAGE_STORAGE_KEY,
    hydrateFloatingLanguagePreference,
    normalizeFloatingLanguagePreference,
    resetFloatingLanguagePreferenceForTests,
    useFloatingLanguageStore,
  } = await import("./preference");

  assert.equal(normalizeFloatingLanguagePreference(null).enabled, false);
  assert.equal(normalizeFloatingLanguagePreference({ enabled: "true" }).enabled, false);
  assert.equal(normalizeFloatingLanguagePreference({ enabled: true }).enabled, true);

  store.clear();
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, false);
  assert.equal(useFloatingLanguageStore.getState().hydrated, true);

  store.set(FLOATING_LANGUAGE_STORAGE_KEY, JSON.stringify({ enabled: true, position: { side: "left", yRatio: 0.3 } }));
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, true);
  assert.deepEqual(useFloatingLanguageStore.getState().position, { side: "left", yRatio: 0.3 });

  // 读盘失败：保持默认关闭，不抛错、不阻塞。
  failReads = true;
  resetFloatingLanguagePreferenceForTests();
  await hydrateFloatingLanguagePreference();
  assert.equal(useFloatingLanguageStore.getState().enabled, false);
  assert.equal(useFloatingLanguageStore.getState().hydrated, true);
  failReads = false;
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
