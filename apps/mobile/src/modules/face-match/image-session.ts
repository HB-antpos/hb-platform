// 复核图片会话（敏感生物识别信息）：一次「复核弹框」对应一个会话。
//
// 约束（由 image-session.test / source-contract.test 守住）：
// 1. 只在用户打开复核弹框后才请求：先现场抓拍，再第一张参考照片；其余参考照片在切换时才取，没有预加载列表或缩略图；
// 2. 图片只以 base64 data URI 的形式存在于会话状态里（组件 state），不写磁盘缓存、不进 react-query 缓存、不进日志；
// 3. 关闭 / 切换 / 卸载 / 应用退到后台时调用 dispose()：中止在途请求并清空全部状态引用，此后不再回调。
import { classifyReviewImageError, isSharedImageFailure, type ReviewImageErrorKind } from "./errors";

export const CAPTURE_SLOT = "capture";

export function referenceSlot(index: number): string {
  return `reference-${index}`;
}

export type ReviewImageState =
  | { status: "loading" }
  | { status: "loaded"; uri: string }
  | { status: "error"; kind: ReviewImageErrorKind };

export type ReviewImageStates = ReadonlyMap<string, ReviewImageState>;

export interface ReviewImageSession {
  /** 开始加载：先现场抓拍，再第一张参考照片。 */
  start: () => Promise<void>;
  /** 按需加载某张参考照片；已加载 / 加载中的不重复请求。 */
  loadReference: (index: number) => Promise<void>;
  /** 关闭 / 切换 / 卸载：中止在途请求，清空状态，此后不再回调。 */
  dispose: () => void;
  getStates: () => ReviewImageStates;
}

export interface ReviewImageSessionOptions {
  referenceCount: number;
  /** 取抓拍图，返回 data URI。 */
  fetchCapture: (signal: AbortSignal) => Promise<string>;
  /** 取第 index 张参考照片，返回 data URI。 */
  fetchReference: (index: number, signal: AbortSignal) => Promise<string>;
  onChange: (states: ReviewImageStates) => void;
}

/**
 * 遇到未配置 / 无权限 / 登录过期这类对其余图片同样成立的失败，后续图片直接标记同样的失败，不再逐张重试；
 * 单张被清理 / 不存在不算共享失败（其余图片可能还在）。
 */
export function createReviewImageSession(options: ReviewImageSessionOptions): ReviewImageSession {
  const controller = new AbortController();
  let disposed = false;
  let states = new Map<string, ReviewImageState>();
  let sharedFailure: ReviewImageErrorKind | null = null;
  const requested = new Set<string>();

  function update(slot: string, state: ReviewImageState) {
    if (disposed) return;
    states = new Map(states).set(slot, state);
    options.onChange(states);
  }

  async function load(slot: string, fetcher: (signal: AbortSignal) => Promise<string>) {
    if (disposed || requested.has(slot)) return;
    requested.add(slot);
    if (sharedFailure) {
      update(slot, { status: "error", kind: sharedFailure });
      return;
    }
    update(slot, { status: "loading" });
    try {
      const uri = await fetcher(controller.signal);
      // 关闭后才返回的响应直接丢弃（不进入状态）。
      if (disposed) return;
      update(slot, { status: "loaded", uri });
    } catch (error) {
      if (disposed || controller.signal.aborted) return;
      const kind = classifyReviewImageError(error);
      if (isSharedImageFailure(kind)) sharedFailure = kind;
      update(slot, { status: "error", kind });
    }
  }

  function loadReference(index: number) {
    if (!Number.isInteger(index) || index < 0 || index >= options.referenceCount) return Promise.resolve();
    return load(referenceSlot(index), (signal) => options.fetchReference(index, signal));
  }

  async function start() {
    await load(CAPTURE_SLOT, options.fetchCapture);
    if (options.referenceCount > 0) await loadReference(0);
  }

  return {
    start,
    loadReference,
    getStates: () => states,
    dispose() {
      if (disposed) return;
      disposed = true;
      controller.abort();
      states = new Map();
    },
  };
}
