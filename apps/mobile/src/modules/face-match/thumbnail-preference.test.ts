import assert from "node:assert/strict";
import Module from "node:module";

async function run() {
  // 只验证「缩略图是否显示」偏好的记忆逻辑，AsyncStorage 用内存替身。
  const store = new Map<string, string>();
  let failReads = false;
  let failWrites = false;
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
        if (failWrites) throw new Error("disk full");
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
    THUMBNAILS_VISIBLE_STORAGE_KEY,
    parseThumbnailsVisible,
    peekThumbnailsVisible,
    readThumbnailsVisible,
    rememberThumbnailsVisible,
    resetThumbnailsVisibleCache,
  } = await import("./thumbnail-preference");

  assert.equal(THUMBNAILS_VISIBLE_STORAGE_KEY, "faceMatch.thumbnails.visible");

  // 只有明确存了 "0" 才是隐藏
  assert.equal(parseThumbnailsVisible("0"), false);
  assert.equal(parseThumbnailsVisible("1"), true);
  assert.equal(parseThumbnailsVisible(null), true);
  assert.equal(parseThumbnailsVisible(undefined), true);
  assert.equal(parseThumbnailsVisible("false"), true, "存坏了的值按默认显示");

  // 冷启动：没存过 -> 默认显示；peek 在读之前为 null（页面据此等读完再决定要不要加载缩略图）
  assert.equal(peekThumbnailsVisible(), null);
  assert.equal(await readThumbnailsVisible(), true);
  assert.equal(peekThumbnailsVisible(), true);

  // 点「眼睛」隐藏：同步更新缓存并写入存储
  rememberThumbnailsVisible(false);
  assert.equal(peekThumbnailsVisible(), false);
  await Promise.resolve();
  assert.deepEqual(writes.at(-1), [THUMBNAILS_VISIBLE_STORAGE_KEY, "0"]);

  // 冷启动读到已存的选择
  resetThumbnailsVisibleCache();
  assert.equal(await readThumbnailsVisible(), false);
  rememberThumbnailsVisible(true);
  await Promise.resolve();
  assert.deepEqual(writes.at(-1), [THUMBNAILS_VISIBLE_STORAGE_KEY, "1"]);
  resetThumbnailsVisibleCache();
  assert.equal(await readThumbnailsVisible(), true);

  // 偏好里只会有这一个键、这两种值，不含任何人脸数据
  assert.ok(writes.every(([key, value]) => key === THUMBNAILS_VISIBLE_STORAGE_KEY && (value === "0" || value === "1")));

  // 读取失败只是不记住（按默认显示）
  failReads = true;
  resetThumbnailsVisibleCache();
  assert.equal(await readThumbnailsVisible(), true);
  failReads = false;

  // 写入失败不抛错，当前会话仍按用户的选择
  failWrites = true;
  rememberThumbnailsVisible(false);
  await Promise.resolve();
  assert.equal(peekThumbnailsVisible(), false);
  failWrites = false;

  // 读取期间用户已点过眼睛：以最新选择为准，不被旧值覆盖
  store.set(THUMBNAILS_VISIBLE_STORAGE_KEY, "1");
  resetThumbnailsVisibleCache();
  const pendingRead = readThumbnailsVisible();
  rememberThumbnailsVisible(false);
  assert.equal(await pendingRead, false);
  assert.equal(peekThumbnailsVisible(), false);

  console.log("thumbnail-preference.test.ts: ok");
}

void run();
