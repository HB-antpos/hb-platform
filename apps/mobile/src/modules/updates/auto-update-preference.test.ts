import assert from "node:assert/strict";
import Module from "node:module";

async function run() {
  // 只验证「自动更新」偏好的读写与门禁逻辑，AsyncStorage 用内存替身。
  const store = new Map<string, string>();
  let failReads = false;
  const writes: [string, string][] = [];
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
        writes.push([key, value]);
        store.set(key, value);
      },
      removeItem: async (key: string) => {
        store.delete(key);
      },
    },
  };
  require.cache[filename] = mocked;

  const {
    AUTO_UPDATE_STORAGE_KEY,
    hydrateAutoUpdatePreference,
    isAutoUpdateAllowed,
    parseAutoUpdateEnabled,
    resetAutoUpdatePreferenceForTest,
    useAutoUpdatePreferenceStore,
  } = await import("./auto-update-preference");

  // 解析：只有明确存了 "0" 才是关；没存过、存坏了都按默认开启。
  assert.equal(parseAutoUpdateEnabled(null), true);
  assert.equal(parseAutoUpdateEnabled(undefined), true);
  assert.equal(parseAutoUpdateEnabled("1"), true);
  assert.equal(parseAutoUpdateEnabled("garbage"), true);
  assert.equal(parseAutoUpdateEnabled("0"), false);

  // 门禁：读盘完成前一律按「关」，避免已关闭的用户在冷启动被默认值触发可选更新。
  assert.equal(isAutoUpdateAllowed({ hydrated: false, enabled: true }), false);
  assert.equal(isAutoUpdateAllowed({ hydrated: true, enabled: true }), true);
  assert.equal(isAutoUpdateAllowed({ hydrated: true, enabled: false }), false);

  // 没存过：读盘后默认开启。
  resetAutoUpdatePreferenceForTest();
  await hydrateAutoUpdatePreference();
  assert.deepEqual(
    { enabled: useAutoUpdatePreferenceStore.getState().enabled, hydrated: useAutoUpdatePreferenceStore.getState().hydrated },
    { enabled: true, hydrated: true },
  );

  // 关闭会落盘；冷启动读回仍是关闭。
  useAutoUpdatePreferenceStore.getState().setEnabled(false);
  assert.deepEqual(writes.at(-1), [AUTO_UPDATE_STORAGE_KEY, "0"]);
  resetAutoUpdatePreferenceForTest();
  await hydrateAutoUpdatePreference();
  assert.equal(useAutoUpdatePreferenceStore.getState().enabled, false);
  assert.equal(isAutoUpdateAllowed(useAutoUpdatePreferenceStore.getState()), false);

  // 重新打开会落盘。
  useAutoUpdatePreferenceStore.getState().setEnabled(true);
  assert.deepEqual(writes.at(-1), [AUTO_UPDATE_STORAGE_KEY, "1"]);

  // 读盘失败：按默认开启，且 hydrated 仍会置位（不能卡住更新通道）。
  store.set(AUTO_UPDATE_STORAGE_KEY, "0");
  failReads = true;
  resetAutoUpdatePreferenceForTest();
  await hydrateAutoUpdatePreference();
  assert.equal(useAutoUpdatePreferenceStore.getState().hydrated, true);
  assert.equal(useAutoUpdatePreferenceStore.getState().enabled, true);
  failReads = false;

  // 读盘完成前用户已经点过开关：以用户这次的选择为准，不被旧值覆盖，并在读盘后落盘。
  store.set(AUTO_UPDATE_STORAGE_KEY, "1");
  resetAutoUpdatePreferenceForTest();
  const pending = hydrateAutoUpdatePreference();
  useAutoUpdatePreferenceStore.getState().setEnabled(false);
  await pending;
  assert.equal(useAutoUpdatePreferenceStore.getState().enabled, false);
  assert.equal(useAutoUpdatePreferenceStore.getState().hydrated, true);
  assert.equal(store.get(AUTO_UPDATE_STORAGE_KEY), "0");

  console.log("auto-update-preference tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
