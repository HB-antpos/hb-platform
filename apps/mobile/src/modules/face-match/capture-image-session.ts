// 抓拍详情图片会话（敏感生物识别信息）：一次「抓拍详情」对应一个会话，只有两个槽位——人脸小图与背景图。
//
// 约束（与 image-session.ts 同一套，由 capture-image-session.test / source-contract.test 守住）：
// 1. 只在用户打开详情后才请求，列表没有缩略图、不预加载；
// 2. 图片只以 base64 data URI 的形式存在于会话状态里（组件 state），不写磁盘缓存、不进 react-query 缓存、不进日志；
// 3. 关闭 / 切换 / 卸载 / 应用退到后台时调用 dispose()：中止在途请求并清空全部状态引用，此后不再回调。
import { classifyReviewImageError } from "./errors";
import type { ReviewImageState } from "./image-session";

export type CaptureImageSlot = "face" | "scene";

export type CaptureImageStates = ReadonlyMap<CaptureImageSlot, ReviewImageState>;

export interface CaptureImageSession {
  /** 并行读取存在编号的槽位；没有编号的槽位不请求、也不出现在状态里。 */
  start: () => Promise<void>;
  /** 关闭 / 切换 / 卸载：中止在途请求，清空状态，此后不再回调。 */
  dispose: () => void;
  getStates: () => CaptureImageStates;
}

export interface CaptureImageSessionOptions {
  /** 槽位 → 读取函数（返回 data URI）；传 null 表示该槽位没有图片编号。 */
  fetchers: Record<CaptureImageSlot, ((signal: AbortSignal) => Promise<string>) | null>;
  onChange: (states: CaptureImageStates) => void;
}

export function createCaptureImageSession(options: CaptureImageSessionOptions): CaptureImageSession {
  const controller = new AbortController();
  let disposed = false;
  let states = new Map<CaptureImageSlot, ReviewImageState>();

  function update(slot: CaptureImageSlot, state: ReviewImageState) {
    if (disposed) return;
    states = new Map(states).set(slot, state);
    options.onChange(states);
  }

  async function load(slot: CaptureImageSlot) {
    const fetcher = options.fetchers[slot];
    if (disposed || !fetcher) return;
    update(slot, { status: "loading" });
    try {
      const uri = await fetcher(controller.signal);
      // 关闭后才返回的响应直接丢弃（不进入状态）。
      if (disposed) return;
      update(slot, { status: "loaded", uri });
    } catch (error) {
      if (disposed || controller.signal.aborted) return;
      update(slot, { status: "error", kind: classifyReviewImageError(error) });
    }
  }

  return {
    start: async () => {
      await Promise.all([load("face"), load("scene")]);
    },
    getStates: () => states,
    dispose() {
      if (disposed) return;
      disposed = true;
      controller.abort();
      states = new Map();
    },
  };
}
