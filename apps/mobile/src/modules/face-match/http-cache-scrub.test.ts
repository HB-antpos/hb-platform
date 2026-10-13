import assert from "node:assert/strict";
import {
  CLOCK_SLACK_AFTER_MS,
  CLOCK_SLACK_BEFORE_MS,
  FINAL_SCRUB_DELAY_MS,
  SCRUB_DELAYS_MS,
  createImageCacheScrubber,
  scrubCachedImageBytes,
  type CacheFsPort,
} from "./http-cache-scrub";

const ROOT = "file:///var/app/Library/Caches/";
const JPEG = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46]);
const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const JSON_BODY = Buffer.from('{"ok":true}');

interface FakeFile {
  content: Buffer;
  modifiedAtMs: number;
}

/** 内存里的假文件系统：路径 → 文件；目录靠路径前缀推断。 */
function createFakeFs(files: Record<string, FakeFile>, options: { cacheDirectory?: string | null; failList?: boolean } = {}) {
  const store = new Map(Object.entries(files));
  const calls = { readHead: [] as string[], deleted: [] as string[] };
  const port: CacheFsPort = {
    getCacheDirectory: async () => (options.cacheDirectory === undefined ? ROOT : options.cacheDirectory),
    listDirectory: async (uri) => {
      if (options.failList) throw new Error("list failed");
      const prefix = uri.endsWith("/") ? uri : `${uri}/`;
      const names = new Set<string>();
      for (const path of store.keys()) {
        if (path.startsWith(prefix)) names.add(path.slice(prefix.length).split("/")[0]);
      }
      if (names.size === 0) throw new Error("no such directory");
      return [...names];
    },
    getFileInfo: async (uri) => {
      const file = store.get(uri);
      if (!file) {
        // 目录
        const prefix = `${uri}/`;
        const isDirectory = [...store.keys()].some((path) => path.startsWith(prefix));
        return isDirectory ? { exists: true, isDirectory: true } : { exists: false };
      }
      return { exists: true, isDirectory: false, modifiedAtMs: file.modifiedAtMs };
    },
    readHeadBase64: async (uri, length) => {
      calls.readHead.push(uri);
      const file = store.get(uri);
      if (!file) throw new Error("missing");
      return file.content.subarray(0, length).toString("base64");
    },
    deleteFile: async (uri) => {
      calls.deleted.push(uri);
      store.delete(uri);
    },
  };
  return { port, store, calls };
}

/** 手动推进的假定时器。 */
function createFakeTimers(startMs = 1_000_000) {
  let now = startMs;
  let nextId = 1;
  const queue = new Map<number, { due: number; callback: () => void }>();
  return {
    timers: {
      setTimeout: (callback: () => void, ms: number) => {
        const id = nextId++;
        queue.set(id, { due: now + ms, callback });
        return id;
      },
      clearTimeout: (handle: unknown) => {
        queue.delete(handle as number);
      },
      now: () => now,
    },
    pending: () => queue.size,
    /** 推进时间并依次触发到期的回调，随后让异步清理跑完。 */
    async advance(ms: number) {
      const target = now + ms;
      for (;;) {
        const due = [...queue.entries()].filter(([, entry]) => entry.due <= target).sort((a, b) => a[1].due - b[1].due)[0];
        if (!due) break;
        queue.delete(due[0]);
        now = Math.max(now, due[1].due);
        due[1].callback();
        await flush();
      }
      now = target;
    },
    setNow(ms: number) {
      now = ms;
    },
  };
}

async function flush() {
  for (let i = 0; i < 5; i += 1) await new Promise((resolve) => setImmediate(resolve));
}

const dataDir = `${ROOT}com.hbweb.expo/fsCachedData`;

async function run() {
  const T = 5_000_000;
  const window = { startedAtMs: T, endedAtMs: T + 1_000 };

  // ───────── scrubCachedImageBytes：只删时间窗内、文件头是 JPEG / PNG 的缓存数据文件 ─────────
  {
    const fake = createFakeFs({
      [`${dataDir}/AAA`]: { content: JPEG, modifiedAtMs: T + 200 }, // 窗内 JPEG：删
      [`${dataDir}/BBB`]: { content: PNG, modifiedAtMs: T + 900 }, // 窗内 PNG：删
      [`${dataDir}/CCC`]: { content: JSON_BODY, modifiedAtMs: T + 300 }, // 窗内但不是图片：留
      [`${dataDir}/DDD`]: { content: JPEG, modifiedAtMs: T - CLOCK_SLACK_BEFORE_MS - 1 }, // 早于窗口：留
      [`${dataDir}/EEE`]: { content: JPEG, modifiedAtMs: T + 1_000 + CLOCK_SLACK_AFTER_MS + 1 }, // 晚于窗口：留
      [`${dataDir}/FFF`]: { content: JPEG, modifiedAtMs: T - CLOCK_SLACK_BEFORE_MS }, // 恰在容差边界：删
      [`${ROOT}com.hbweb.expo/Cache.db`]: { content: JPEG, modifiedAtMs: T + 200 }, // 不在 fsCachedData：留
      [`${ROOT}ImagePicker/photo.jpg`]: { content: JPEG, modifiedAtMs: T + 200 }, // 别的缓存目录：留
    });
    const deleted = await scrubCachedImageBytes(fake.port, [window]);
    assert.equal(deleted, 3);
    assert.deepEqual([...fake.calls.deleted].sort(), [`${dataDir}/AAA`, `${dataDir}/BBB`, `${dataDir}/FFF`]);
    assert.ok(fake.store.has(`${dataDir}/CCC`) && fake.store.has(`${dataDir}/DDD`) && fake.store.has(`${dataDir}/EEE`));
    assert.ok(fake.store.has(`${ROOT}com.hbweb.expo/Cache.db`) && fake.store.has(`${ROOT}ImagePicker/photo.jpg`));
    // 窗外的文件不会被读取内容（只读窗内文件的文件头）
    assert.ok(!fake.calls.readHead.includes(`${dataDir}/DDD`) && !fake.calls.readHead.includes(`${dataDir}/EEE`));
  }

  // ───────── 多个时间窗：任一命中即可 ─────────
  {
    const fake = createFakeFs({
      [`${dataDir}/A`]: { content: JPEG, modifiedAtMs: T + 100 },
      [`${dataDir}/B`]: { content: JPEG, modifiedAtMs: T + 60_000 },
    });
    const deleted = await scrubCachedImageBytes(fake.port, [window, { startedAtMs: T + 59_000, endedAtMs: T + 60_500 }]);
    assert.equal(deleted, 2);
  }

  // ───────── 没有时间窗 / 没有缓存目录 / 列目录失败 / 单个文件出错：都不抛错 ─────────
  {
    const fake = createFakeFs({ [`${dataDir}/A`]: { content: JPEG, modifiedAtMs: T } });
    assert.equal(await scrubCachedImageBytes(fake.port, []), 0);
    assert.equal(fake.store.size, 1);
    assert.equal(await scrubCachedImageBytes(createFakeFs({}, { cacheDirectory: null }).port, [window]), 0);
    assert.equal(await scrubCachedImageBytes(createFakeFs({ [`${dataDir}/A`]: { content: JPEG, modifiedAtMs: T } }, { failList: true }).port, [window]), 0);

    const flaky = createFakeFs({
      [`${dataDir}/A`]: { content: JPEG, modifiedAtMs: T },
      [`${dataDir}/B`]: { content: JPEG, modifiedAtMs: T },
    });
    const originalRead = flaky.port.readHeadBase64;
    flaky.port.readHeadBase64 = async (uri, length) => {
      if (uri.endsWith("/A")) throw new Error("read failed");
      return originalRead(uri, length);
    };
    assert.equal(await scrubCachedImageBytes(flaky.port, [window]), 1);
    assert.ok(flaky.store.has(`${dataDir}/A`) && !flaky.store.has(`${dataDir}/B`));
  }

  // ───────── scrubber.wrap：成功后按 0.5 / 2 / 6 秒安排清理；失败原样抛出且不登记 ─────────
  {
    const clock = createFakeTimers(T);
    const fake = createFakeFs({});
    const scrubber = createImageCacheScrubber(fake.port, clock.timers);

    const uri = await scrubber.wrap(async () => {
      clock.setNow(T + 400);
      return "data:image/jpeg;base64,AAAA";
    });
    assert.equal(uri, "data:image/jpeg;base64,AAAA", "wrap 原样返回图片，不改内容");
    assert.equal(scrubber.pendingCount(), 1);
    assert.equal(clock.pending(), SCRUB_DELAYS_MS.length);

    // 系统在响应结束后异步写缓存：0.5 秒时文件还没出现，2 秒时才出现
    await clock.advance(600);
    fake.store.set(`${dataDir}/LATE`, { content: JPEG, modifiedAtMs: clock.timers.now() });
    await clock.advance(1_500);
    assert.ok(!fake.store.has(`${dataDir}/LATE`), "第二次扫描应删掉晚写入的文件");

    await assert.rejects(
      scrubber.wrap(async () => {
        throw new Error("boom");
      }),
      /boom/,
    );
    assert.equal(scrubber.pendingCount(), 1, "失败的读取不登记");
  }

  // ───────── scrubber.release：取消未触发的定时器、立即扫一遍、稍后补扫、清空登记 ─────────
  {
    const clock = createFakeTimers(T);
    const fake = createFakeFs({ [`${dataDir}/EARLY`]: { content: JPEG, modifiedAtMs: T + 50 } });
    const scrubber = createImageCacheScrubber(fake.port, clock.timers);
    await scrubber.wrap(async () => "data:image/jpeg;base64,AAAA");
    assert.equal(clock.pending(), SCRUB_DELAYS_MS.length);

    await scrubber.release();
    assert.ok(!fake.store.has(`${dataDir}/EARLY`), "release 立即扫描");
    assert.equal(scrubber.pendingCount(), 0, "release 后不再持有登记");
    assert.equal(clock.pending(), 1, "原有定时器被取消，只剩一次补扫");

    // 弹框刚打开就关掉：缓存是之后才写进去的，补扫要能删到
    fake.store.set(`${dataDir}/AFTER_CLOSE`, { content: PNG, modifiedAtMs: clock.timers.now() + 100 });
    await clock.advance(FINAL_SCRUB_DELAY_MS + 10);
    assert.ok(!fake.store.has(`${dataDir}/AFTER_CLOSE`));
    assert.equal(clock.pending(), 0);

    // 重复 release 是空操作
    await scrubber.release();
    assert.equal(clock.pending(), 0);
  }

  // ───────── release 后才返回的响应：不登记，只补一次清理 ─────────
  {
    const clock = createFakeTimers(T);
    const fake = createFakeFs({});
    const scrubber = createImageCacheScrubber(fake.port, clock.timers);
    let finish!: (value: string) => void;
    const inflight = scrubber.wrap(() => new Promise<string>((resolve) => (finish = resolve)));
    await scrubber.release();
    fake.store.set(`${dataDir}/STRAGGLER`, { content: JPEG, modifiedAtMs: clock.timers.now() + 20 });
    finish("data:image/jpeg;base64,AAAA");
    await inflight;
    assert.equal(scrubber.pendingCount(), 0);
    await clock.advance(FINAL_SCRUB_DELAY_MS + 10);
    assert.ok(!fake.store.has(`${dataDir}/STRAGGLER`));
  }

  // ───────── 没读到任何图片就关闭：不做任何文件操作 ─────────
  {
    const clock = createFakeTimers(T);
    const fake = createFakeFs({ [`${dataDir}/X`]: { content: JPEG, modifiedAtMs: T } });
    const scrubber = createImageCacheScrubber(fake.port, clock.timers);
    await scrubber.release();
    assert.equal(clock.pending(), 0);
    assert.equal(fake.calls.readHead.length, 0);
    assert.equal(fake.store.size, 1);
  }
}

run().then(
  () => console.log("http-cache-scrub.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
