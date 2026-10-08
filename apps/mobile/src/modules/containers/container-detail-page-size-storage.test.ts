import assert from "node:assert/strict";
import Module from "node:module";

async function run() {
  // 只验证每页条数的记忆逻辑，AsyncStorage 用内存替身。
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
    CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY,
    peekRememberedContainerDetailPageSize,
    readRememberedContainerDetailPageSize,
    rememberContainerDetailPageSize,
    resetRememberedContainerDetailPageSizeCache,
  } = await import("./container-detail-page-size-storage");

  assert.equal(CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY, "containers.detail.pageSize.v1");

  // 冷启动：没存过 -> 默认 50；peek 在读之前为 null
  assert.equal(peekRememberedContainerDetailPageSize(), null);
  assert.equal(await readRememberedContainerDetailPageSize(), 50);
  assert.equal(peekRememberedContainerDetailPageSize(), 50);

  // 记住：同步更新缓存并写入存储
  rememberContainerDetailPageSize(200);
  assert.equal(peekRememberedContainerDetailPageSize(), 200);
  await Promise.resolve();
  assert.deepEqual(writes.at(-1), [CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY, "200"]);

  // 冷启动读到已存的合法值
  resetRememberedContainerDetailPageSizeCache();
  assert.equal(await readRememberedContainerDetailPageSize(), 200);

  // 旧版本/被篡改的值（30、abc）回到默认
  for (const bad of ["30", "abc", ""]) {
    store.set(CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY, bad);
    resetRememberedContainerDetailPageSizeCache();
    assert.equal(await readRememberedContainerDetailPageSize(), 50, `非法值 ${JSON.stringify(bad)} 回到默认`);
  }

  // 读取失败只是不记住
  failReads = true;
  resetRememberedContainerDetailPageSizeCache();
  assert.equal(await readRememberedContainerDetailPageSize(), 50);
  failReads = false;

  // 读取期间用户已改过选择：以最新选择为准
  store.set(CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY, "100");
  resetRememberedContainerDetailPageSizeCache();
  const pendingRead = readRememberedContainerDetailPageSize();
  rememberContainerDetailPageSize(500);
  assert.equal(await pendingRead, 500);
  assert.equal(peekRememberedContainerDetailPageSize(), 500);

  console.log("containers/container-detail-page-size-storage.test.ts: ok");
}

void run();
