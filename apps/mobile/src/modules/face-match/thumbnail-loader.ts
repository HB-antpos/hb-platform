// 抓拍列表头像缩略图的加载器（敏感生物识别信息，纯逻辑，可在 Node 下测试）。
//
// 约束（与 image-session.ts / capture-image-session.ts 同一套，由 thumbnail-loader.test / source-contract.test 守住）：
// 1. 只加载「现在需要」的项（屏幕可见 + 前后少量预取，由调用方通过 request 声明）；一次只跑一个批请求，一批最多 30 项；
// 2. 结果只放内存（本加载器的 Map 与调用方 state / ref），不写磁盘缓存、不进 react-query、不进日志；
// 3. clear()（隐藏 / 切后台）与 dispose()（卸载 / 离开页面）立即中止在途请求、清空全部引用；之后迟到的响应一律丢弃；
// 4. 已知 ok / none / gone / denied 的项不再重复请求；批请求失败后暂停自动请求，只在 retryFailed()（下拉刷新）时重试，
//    避免弱网下边滚边失败、每一屏都重发一次（服务端每个批请求写一条审计）。
import { THUMBNAIL_BATCH_MAX } from "./constants";
import type { FaceThumbnailKind, FaceThumbnailResult } from "./types";

export interface ThumbnailTarget {
  kind: FaceThumbnailKind;
  /** capture = 抓拍编号；hit = 命中任务编号。 */
  id: string;
}

/** 一项在界面上的状态；没有记录（undefined）= 还没人要过它。 */
export type ThumbnailState =
  /** 排队或在途：显示灰色骨架。 */
  | { status: "loading" }
  | { status: "ok"; uri: string }
  | { status: "none" }
  | { status: "gone" }
  | { status: "denied" }
  /** 批请求失败：显示占位，等下拉刷新重试。 */
  | { status: "failed" };

export type ThumbnailStates = ReadonlyMap<string, ThumbnailState>;

export function thumbnailKey(target: ThumbnailTarget): string {
  return `${target.kind}:${target.id}`;
}

/**
 * base64 → 可显示的 data URI：只认字符集合法且文件头是 JPEG（/9j/）或 PNG 的内容，其余当作没有图
 * （与整图读取一样「识别不出来的字节一律不当图片显示」）。服务端承诺的是 JPEG，PNG 只是兼容。
 */
export function thumbnailDataUri(base64: string | null | undefined): string | null {
  if (typeof base64 !== "string" || base64 === "") return null;
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(base64)) return null;
  if (base64.startsWith("/9j/")) return `data:image/jpeg;base64,${base64}`;
  if (base64.startsWith("iVBORw0KGgo")) return `data:image/png;base64,${base64}`;
  return null;
}

export interface ThumbnailTimers {
  setTimeout: (callback: () => void, ms: number) => unknown;
  clearTimeout: (handle: unknown) => void;
}

export interface ThumbnailLoaderOptions {
  /** 发一个批请求（调用方保证 signal 中止时抛错或不再返回有用的结果）。 */
  fetchBatch: (items: ThumbnailTarget[], signal: AbortSignal) => Promise<readonly FaceThumbnailResult[]>;
  /** 状态变化回调；每次给一个新的 Map 快照。dispose 之后不再回调。 */
  onChange: (states: ThumbnailStates) => void;
  /** 一批最多几项，默认且不超过 30。 */
  batchMax?: number;
  /**
   * 空闲时第一次 request 之后等多久再发批请求（毫秒，默认 120）：让快速滚动期间的多次「可见项变化」合并成一个批请求，
   * 少写审计；0 = 立即发。已有批在途时不等待（上一批结束后立刻接着发）。
   */
  debounceMs?: number;
  timers?: ThumbnailTimers;
}

export interface ThumbnailLoader {
  /**
   * 声明「现在需要这些项」（按优先级排序：可见的在前、预取的在后）。它替换上一次的需求：
   * 还在排队、没发出去的旧项会被丢弃（不再需要就不请求）；已知 / 在途 / 失败的项不重复请求。
   */
  request: (targets: readonly ThumbnailTarget[]) => void;
  /** 下拉刷新：清掉「失败」标记并立即重新请求当前需要的项。 */
  retryFailed: () => void;
  /** 隐藏 / 切后台：中止在途请求，清空全部缩略图与需求；之后可以再 request。 */
  clear: () => void;
  /** 卸载 / 离开页面：clear 并且不再回调。 */
  dispose: () => void;
  getStates: () => ThumbnailStates;
}

function toState(result: FaceThumbnailResult | undefined): ThumbnailState {
  // 服务端没回这一项：按「没有图」处理，避免无限重试。
  if (!result) return { status: "none" };
  if (result.status === "ok") {
    const uri = thumbnailDataUri(result.imageBase64);
    return uri ? { status: "ok", uri } : { status: "none" };
  }
  return { status: result.status };
}

export function createThumbnailLoader(options: ThumbnailLoaderOptions): ThumbnailLoader {
  const batchMax = Math.min(THUMBNAIL_BATCH_MAX, Math.max(1, Math.trunc(options.batchMax ?? THUMBNAIL_BATCH_MAX)));
  const debounceMs = Math.max(0, options.debounceMs ?? 120);
  const timers: ThumbnailTimers = options.timers ?? {
    setTimeout: (callback, ms) => setTimeout(callback, ms),
    clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  };

  let states = new Map<string, ThumbnailState>();
  /** 最近一次 request 声明的需求（保序、去重），retryFailed 用它重新排队。 */
  let wanted: ThumbnailTarget[] = [];
  /** 已登记 loading、但还没发出去的项（保序）。 */
  let queue: ThumbnailTarget[] = [];
  /** 当前在途批里的项。 */
  const inflight = new Set<string>();
  let controller: AbortController | null = null;
  /** 每次 clear / dispose 加一：旧代的响应回来时发现代数变了就丢弃。 */
  let generation = 0;
  let disposed = false;
  let running = false;
  /** 批请求失败后暂停自动请求，直到 retryFailed。 */
  let suspended = false;
  let timer: unknown = null;

  const emit = () => {
    if (!disposed) options.onChange(states);
  };

  function clearTimer() {
    if (timer !== null) {
      timers.clearTimeout(timer);
      timer = null;
    }
  }

  async function pump(): Promise<void> {
    if (disposed || running || queue.length === 0) return;
    const batch = queue.splice(0, batchMax);
    const batchGeneration = generation;
    controller ??= new AbortController();
    const batchController = controller;
    running = true;
    for (const target of batch) inflight.add(thumbnailKey(target));
    try {
      const results = await options.fetchBatch(batch, batchController.signal);
      // 清空 / 销毁之后才回来的响应直接丢弃（不进入状态）。
      if (disposed || batchGeneration !== generation) return;
      const byKey = new Map(results.map((result) => [`${result.kind}:${result.id}`, result]));
      const next = new Map(states);
      for (const target of batch) {
        const key = thumbnailKey(target);
        inflight.delete(key);
        next.set(key, toState(byKey.get(key)));
      }
      states = next;
      running = false;
      emit();
    } catch {
      if (disposed || batchGeneration !== generation || batchController.signal.aborted) return;
      // 失败：这一批与还在排队的项都标失败，并暂停自动请求，等下拉刷新重试。
      const next = new Map(states);
      for (const target of batch) {
        inflight.delete(thumbnailKey(target));
        next.set(thumbnailKey(target), { status: "failed" });
      }
      for (const target of queue) next.set(thumbnailKey(target), { status: "failed" });
      queue = [];
      suspended = true;
      states = next;
      running = false;
      emit();
      return;
    }
    // 上一批结束，接着发下一批（一次只跑一个批请求）。
    void pump();
  }

  function schedule(immediate: boolean) {
    if (disposed || running || queue.length === 0) return;
    if (immediate || debounceMs === 0) {
      clearTimer();
      void pump();
      return;
    }
    // 只在没有等待中的定时器时才安排：连续滚动时最长等 debounceMs，不会被无限推迟。
    if (timer === null) {
      timer = timers.setTimeout(() => {
        timer = null;
        void pump();
      }, debounceMs);
    }
  }

  function applyWanted(targets: readonly ThumbnailTarget[], mode: { immediate: boolean; changed: boolean }) {
    const unique: ThumbnailTarget[] = [];
    const seen = new Set<string>();
    for (const target of targets) {
      const key = thumbnailKey(target);
      if (seen.has(key)) continue;
      seen.add(key);
      unique.push(target);
    }
    wanted = unique;

    const next = new Map(states);
    let changed = mode.changed;
    // 不再需要、且还没发出去的排队项：撤销 loading 标记（回到「没人要过」）。
    for (const [key, state] of states) {
      if (state.status === "loading" && !inflight.has(key) && !seen.has(key)) {
        next.delete(key);
        changed = true;
      }
    }
    const nextQueue: ThumbnailTarget[] = [];
    for (const target of unique) {
      const key = thumbnailKey(target);
      const state = states.get(key);
      if (state?.status === "loading") {
        // 在途的不重复发；仍在排队的保留（顺序以最新需求为准）。
        if (!inflight.has(key)) nextQueue.push(target);
        continue;
      }
      if (state) continue; // ok / none / gone / denied / failed：不重复请求
      if (suspended) {
        next.set(key, { status: "failed" });
        changed = true;
        continue;
      }
      next.set(key, { status: "loading" });
      nextQueue.push(target);
      changed = true;
    }
    queue = nextQueue;
    if (changed) {
      states = next;
      emit();
    }
    schedule(mode.immediate);
  }

  function reset() {
    generation += 1;
    controller?.abort();
    controller = null;
    clearTimer();
    states = new Map();
    wanted = [];
    queue = [];
    inflight.clear();
    running = false;
    suspended = false;
  }

  return {
    request(targets) {
      if (disposed) return;
      applyWanted(targets, { immediate: false, changed: false });
    },
    retryFailed() {
      if (disposed) return;
      suspended = false;
      const next = new Map(states);
      let removed = false;
      for (const [key, state] of states) {
        if (state.status === "failed") {
          next.delete(key);
          removed = true;
        }
      }
      states = next;
      applyWanted(wanted, { immediate: true, changed: removed });
    },
    clear() {
      if (disposed) return;
      reset();
      emit();
    },
    dispose() {
      if (disposed) return;
      reset();
      disposed = true;
    },
    getStates: () => states,
  };
}
