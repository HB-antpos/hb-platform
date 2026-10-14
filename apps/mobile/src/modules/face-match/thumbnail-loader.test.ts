import assert from "node:assert/strict";
import {
  createThumbnailLoader,
  thumbnailDataUri,
  thumbnailKey,
  type ThumbnailLoaderOptions,
  type ThumbnailState,
  type ThumbnailTarget,
  type ThumbnailTimers,
} from "./thumbnail-loader";
import type { FaceThumbnailResult } from "./types";

const JPEG_B64 = "/9j/4AAQSkZJRg==";
const PNG_B64 = "iVBORw0KGgoAAAANSUhEUg==";

const cap = (id: string): ThumbnailTarget => ({ kind: "capture", id });
const hit = (id: string): ThumbnailTarget => ({ kind: "hit", id });
const ok = (target: ThumbnailTarget, imageBase64 = JPEG_B64): FaceThumbnailResult => ({ ...target, status: "ok", imageBase64 });
const other = (target: ThumbnailTarget, status: "none" | "gone" | "denied"): FaceThumbnailResult => ({ ...target, status, imageBase64: null });

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** 手动推进的定时器：只验证「合并」逻辑，不依赖真实时间。 */
function fakeTimers() {
  const pending = new Map<number, () => void>();
  let next = 1;
  const timers: ThumbnailTimers = {
    setTimeout: (callback) => {
      const handle = next++;
      pending.set(handle, callback);
      return handle;
    },
    clearTimeout: (handle) => {
      pending.delete(handle as number);
    },
  };
  return {
    timers,
    pendingCount: () => pending.size,
    flush: () => {
      const callbacks = [...pending.values()];
      pending.clear();
      for (const callback of callbacks) callback();
    },
  };
}

/** 等所有已排队的微任务（含串联的批请求）跑完。 */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/** 创建加载器 + 记录每个批请求；reply 决定每批的返回。 */
function setup(reply: (items: ThumbnailTarget[], signal: AbortSignal) => Promise<FaceThumbnailResult[]>, extra: Partial<ThumbnailLoaderOptions> = {}) {
  const batches: ThumbnailTarget[][] = [];
  const signals: AbortSignal[] = [];
  let inflight = 0;
  let maxInflight = 0;
  const changes: ReadonlyMap<string, ThumbnailState>[] = [];
  const loader = createThumbnailLoader({
    debounceMs: 0,
    fetchBatch: async (items, signal) => {
      batches.push(items);
      signals.push(signal);
      inflight += 1;
      maxInflight = Math.max(maxInflight, inflight);
      try {
        return await reply(items, signal);
      } finally {
        inflight -= 1;
      }
    },
    onChange: (states) => changes.push(states),
    ...extra,
  });
  return { loader, batches, signals, changes, maxInflight: () => maxInflight };
}

const echoOk = async (items: ThumbnailTarget[]) => items.map((item) => ok(item));

async function run() {
  // ───────── 键与 data URI ─────────
  assert.equal(thumbnailKey(cap("a1")), "capture:a1");
  assert.equal(thumbnailKey(hit("t1")), "hit:t1");
  assert.equal(thumbnailDataUri(JPEG_B64), `data:image/jpeg;base64,${JPEG_B64}`);
  assert.equal(thumbnailDataUri(PNG_B64), `data:image/png;base64,${PNG_B64}`);
  assert.equal(thumbnailDataUri(null), null);
  assert.equal(thumbnailDataUri(""), null);
  assert.equal(thumbnailDataUri("R0lGODlhAQABAAAAACw="), null, "不是 JPEG / PNG 文件头的内容不当图片显示（GIF）");
  assert.equal(thumbnailDataUri("/9j/ not base64!"), null, "字符集不合法");
  assert.equal(thumbnailDataUri("/9j/4AAQ\n=="), null, "夹带换行 / 注入内容不接受");
  assert.equal(thumbnailDataUri("<svg onload=alert(1)>"), null);

  // ───────── 分批：一批最多 30 项，一次只跑一个批请求，顺序是需求的优先级 ─────────
  {
    const targets = Array.from({ length: 70 }, (_, index) => cap(`c${index}`));
    const { loader, batches, maxInflight } = setup(echoOk);
    loader.request(targets);
    await settle();
    assert.deepEqual(batches.map((batch) => batch.length), [30, 30, 10]);
    assert.equal(maxInflight(), 1, "任何时刻最多一个批请求在途");
    assert.deepEqual(batches[0].map((item) => item.id), targets.slice(0, 30).map((item) => item.id), "先发排在前面（可见）的项");
    assert.equal(loader.getStates().size, 70);
    assert.ok([...loader.getStates().values()].every((state) => state.status === "ok"));
    loader.dispose();
  }
  {
    // 自定义批量上限不能超过 30，也不能小于 1
    const { loader, batches } = setup(echoOk, { batchMax: 999 });
    loader.request(Array.from({ length: 31 }, (_, index) => cap(`c${index}`)));
    await settle();
    assert.deepEqual(batches.map((batch) => batch.length), [30, 1], "上限被夹到 30");
    loader.dispose();
    const small = setup(echoOk, { batchMax: 0 });
    small.loader.request([cap("a"), cap("b")]);
    await settle();
    assert.deepEqual(small.batches.map((batch) => batch.length), [1, 1], "上限至少为 1");
    small.loader.dispose();
  }

  // ───────── 去重：同一次需求里的重复项只请求一次；已知项不重复请求 ─────────
  {
    const { loader, batches } = setup(echoOk);
    loader.request([cap("a"), hit("a"), cap("a"), hit("a")]);
    await settle();
    assert.deepEqual(batches, [[cap("a"), hit("a")]], "kind + id 才是唯一键，重复项去掉");
    loader.request([cap("a"), hit("a")]);
    loader.request([cap("a")]);
    await settle();
    assert.equal(batches.length, 1, "已经取到的项不再请求");
    loader.request([cap("a"), cap("b")]);
    await settle();
    assert.deepEqual(batches[1], [cap("b")], "只请求新增的项");
    loader.dispose();
  }

  // ───────── none / gone / denied 与缺失项都不再重复请求；状态按结果归并 ─────────
  {
    const { loader, batches } = setup(async () => [other(cap("n"), "none"), other(cap("g"), "gone"), other(hit("d"), "denied"), ok(cap("good"))]);
    loader.request([cap("n"), cap("g"), hit("d"), cap("good"), cap("missing")]);
    await settle();
    const states = loader.getStates();
    assert.deepEqual(states.get("capture:n"), { status: "none" });
    assert.deepEqual(states.get("capture:g"), { status: "gone" });
    assert.deepEqual(states.get("hit:d"), { status: "denied" });
    assert.deepEqual(states.get("capture:good"), { status: "ok", uri: `data:image/jpeg;base64,${JPEG_B64}` });
    assert.deepEqual(states.get("capture:missing"), { status: "none" }, "服务端没回的项按没有图处理，避免无限重试");
    loader.request([cap("n"), cap("g"), hit("d"), cap("good"), cap("missing")]);
    await settle();
    assert.equal(batches.length, 1, "已知状态的项不再发请求");
    loader.dispose();
  }
  {
    // ok 但内容不是合法图片（文件头不对）→ none，不把脏数据交给 Image
    const { loader } = setup(async (items) => items.map((item) => ({ ...item, status: "ok" as const, imageBase64: "R0lGODlhAQABAAAAACw=" })));
    loader.request([cap("x")]);
    await settle();
    assert.deepEqual(loader.getStates().get("capture:x"), { status: "none" });
    loader.dispose();
  }

  // ───────── 声明新需求会撤掉还没发出去的旧排队项（滚走了就不取） ─────────
  {
    const first = deferred<FaceThumbnailResult[]>();
    const calls: ThumbnailTarget[][] = [];
    const { loader, changes } = setup((items) => {
      calls.push(items);
      return calls.length === 1 ? first.promise : echoOk(items);
    });
    const all = Array.from({ length: 40 }, (_, index) => cap(`c${index}`));
    loader.request(all); // 第一批 c0..c29 在途，c30..c39 排队
    assert.equal(loader.getStates().get("capture:c35")?.status, "loading");
    loader.request([...all.slice(35, 40), all[0]]); // 用户滚到了下面：只要 c35..c39（c0 已在途）
    assert.equal(loader.getStates().has("capture:c30"), false, "不再需要的排队项撤销 loading 标记");
    assert.equal(loader.getStates().get("capture:c0")?.status, "loading", "在途项保持 loading");
    first.resolve(all.slice(0, 30).map((item) => ok(item)));
    await settle();
    assert.deepEqual(calls[1].map((item) => item.id), ["c35", "c36", "c37", "c38", "c39"], "第二批只含最新需求里的项");
    assert.equal(calls.length, 2);
    assert.equal(loader.getStates().get("capture:c0")?.status, "ok", "在途批的结果照常保存");
    assert.equal(loader.getStates().has("capture:c31"), false, "被撤销的项没有被请求");
    assert.ok(changes.length > 0);
    loader.dispose();
  }

  // ───────── 防抖：快速滚动期间多次声明合并成一批（少写审计） ─────────
  {
    const clock = fakeTimers();
    const { loader, batches } = setup(echoOk, { debounceMs: 120, timers: clock.timers });
    loader.request([cap("a"), cap("b"), cap("c")]);
    loader.request([cap("b"), cap("c"), cap("d")]);
    loader.request([cap("c"), cap("d"), cap("e")]);
    assert.equal(batches.length, 0, "防抖期内不发请求");
    assert.equal(clock.pendingCount(), 1, "只安排一个定时器，不会被连续滚动无限推迟");
    clock.flush();
    await settle();
    assert.deepEqual(batches, [[cap("c"), cap("d"), cap("e")]], "合并成一个批请求，只含最新需求");
    loader.dispose();
    assert.equal(clock.pendingCount(), 0);
  }
  {
    // 有批在途时，新需求不等防抖，上一批结束立刻接着发
    const clock = fakeTimers();
    const gate = deferred<FaceThumbnailResult[]>();
    let call = 0;
    const { loader, batches } = setup(
      (items) => {
        call += 1;
        return call === 1 ? gate.promise : echoOk(items);
      },
      { debounceMs: 120, timers: clock.timers },
    );
    loader.request([cap("a")]);
    clock.flush();
    await settle();
    assert.equal(batches.length, 1);
    loader.request([cap("b")]);
    assert.equal(clock.pendingCount(), 0, "有批在途时不再安排防抖定时器");
    gate.resolve([ok(cap("a"))]);
    await settle();
    assert.deepEqual(batches[1], [cap("b")]);
    loader.dispose();
  }

  // ───────── 失败：这一批与排队项标失败并暂停自动请求，只在 retryFailed 时重试 ─────────
  {
    let failing = true;
    const { loader, batches } = setup(async (items) => {
      if (failing) throw new Error("network down");
      return items.map((item) => ok(item));
    });
    const targets = Array.from({ length: 35 }, (_, index) => cap(`c${index}`));
    loader.request(targets);
    await settle();
    assert.equal(batches.length, 1, "失败后不继续发排队的批");
    assert.ok(targets.every((target) => loader.getStates().get(thumbnailKey(target))?.status === "failed"), "这一批与排队项都标失败");
    // 继续滚动、声明新项：不发请求，直接标失败（避免弱网下每滚一屏就发一次）
    loader.request([cap("later")]);
    await settle();
    assert.equal(batches.length, 1);
    assert.deepEqual(loader.getStates().get("capture:later"), { status: "failed" });
    // 下拉刷新：清掉失败标记，立即按最近一次需求重试（需求是 later）
    failing = false;
    loader.retryFailed();
    await settle();
    assert.equal(batches.length, 2);
    assert.deepEqual(batches[1], [cap("later")], "只重试最近一次需求里的项");
    assert.equal(loader.getStates().get("capture:later")?.status, "ok");
    assert.equal(loader.getStates().has("capture:c0"), false, "不在当前需求里的失败项也清掉了标记：之后滚到它时按新项正常请求");
    loader.request([cap("c0")]);
    await settle();
    assert.equal(loader.getStates().get("capture:c0")?.status, "ok");
    assert.equal(batches.length, 3);
    loader.dispose();
  }
  {
    // 没有 retryFailed 就不会自己重试：失败标记一直保留，再次声明同一项也不发请求
    const { loader, batches } = setup(async () => {
      throw new Error("down");
    });
    loader.request([cap("a")]);
    await settle();
    loader.request([cap("a")]);
    loader.request([cap("a"), cap("b")]);
    await settle();
    assert.equal(batches.length, 1);
    assert.equal(loader.getStates().get("capture:a")?.status, "failed");
    loader.dispose();
  }

  // ───────── clear：中止在途请求，迟到的响应被丢弃；清空后可以重新请求 ─────────
  {
    const gate = deferred<FaceThumbnailResult[]>();
    let call = 0;
    const { loader, signals, batches, changes } = setup((items) => {
      call += 1;
      return call === 1 ? gate.promise : echoOk(items);
    });
    loader.request([cap("a"), cap("b")]);
    await settle();
    assert.equal(signals[0].aborted, false);
    loader.clear();
    assert.equal(signals[0].aborted, true, "clear 立即中止在途请求");
    assert.equal(loader.getStates().size, 0, "clear 立即清空全部缩略图");
    assert.equal(changes.at(-1)?.size, 0, "清空会通知界面");
    gate.resolve([ok(cap("a")), ok(cap("b"))]);
    await settle();
    assert.equal(loader.getStates().size, 0, "迟到的响应被丢弃，不会把图片写回状态");
    // 之后可以再次请求（隐藏后重新显示），用新的 AbortSignal
    loader.request([cap("a")]);
    await settle();
    assert.equal(batches.length, 2);
    assert.equal(signals[1].aborted, false);
    assert.equal(loader.getStates().get("capture:a")?.status, "ok");
    loader.dispose();
  }
  {
    // clear 之后迟到的「失败」也不能把新一代的状态弄脏
    const gate = deferred<FaceThumbnailResult[]>();
    let call = 0;
    const { loader } = setup((items) => {
      call += 1;
      return call === 1 ? gate.promise : echoOk(items);
    });
    loader.request([cap("a")]);
    await settle();
    loader.clear();
    loader.request([cap("b")]);
    await settle();
    gate.reject(new Error("aborted late"));
    await settle();
    assert.equal(loader.getStates().get("capture:b")?.status, "ok");
    assert.equal(loader.getStates().has("capture:a"), false);
    loader.request([cap("c")]);
    await settle();
    assert.equal(loader.getStates().get("capture:c")?.status, "ok", "旧代的失败不会让新一代进入暂停状态");
    loader.dispose();
  }

  // ───────── dispose：销毁后迟到的响应丢弃、不再回调、不再发请求 ─────────
  {
    const gate = deferred<FaceThumbnailResult[]>();
    const { loader, signals, batches, changes } = setup(() => gate.promise);
    loader.request([cap("a")]);
    await settle();
    const before = changes.length;
    loader.dispose();
    assert.equal(signals[0].aborted, true, "dispose 中止在途请求");
    gate.resolve([ok(cap("a"))]);
    await settle();
    assert.equal(changes.length, before, "dispose 之后不再回调");
    assert.equal(loader.getStates().size, 0, "销毁后不持有任何图片");
    loader.request([cap("b")]);
    loader.retryFailed();
    loader.clear();
    await settle();
    assert.equal(batches.length, 1, "销毁后再 request 不发请求");
    assert.equal(changes.length, before);
    loader.dispose(); // 重复销毁无副作用
  }
  {
    // dispose 会取消还没触发的防抖定时器
    const clock = fakeTimers();
    const { loader, batches } = setup(echoOk, { debounceMs: 50, timers: clock.timers });
    loader.request([cap("a")]);
    assert.equal(clock.pendingCount(), 1);
    loader.dispose();
    assert.equal(clock.pendingCount(), 0);
    clock.flush();
    await settle();
    assert.equal(batches.length, 0);
  }

  // ───────── onChange 给的是不可变快照：之后的变化不会改动旧快照 ─────────
  {
    const { loader, changes } = setup(echoOk);
    loader.request([cap("a")]);
    await settle();
    const loadingSnapshot = changes[0];
    assert.equal(loadingSnapshot.get("capture:a")?.status, "loading");
    assert.equal(changes.at(-1)?.get("capture:a")?.status, "ok");
    assert.notEqual(changes[0], changes.at(-1));
    loader.dispose();
  }
}

run().then(
  () => console.log("thumbnail-loader.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
