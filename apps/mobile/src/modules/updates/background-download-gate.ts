/**
 * 后台下载闸门：可选的安装包 / OTA 下载要等登录完成并空闲一段时间后才开始，
 * 不和开机、登录抢带宽（10-07 实测 TC26 下载 124MB 安装包用了约 5 分钟，期间登录明显变慢）。
 * 强制更新不经过这个闸门，照常立即下载。
 *
 * 根布局用 bind() 接上登录状态之前（测试、其它入口），闸门视为打开，保持原有「立即下载」行为，
 * 避免某处漏接导致可选更新永远不下载。
 */

/** 登录（或恢复会话）满这么久才开始后台下载。 */
export const BACKGROUND_DOWNLOAD_IDLE_MS = 30_000;

/** 后台安装包下载最多占用的带宽比例，交给原生下载器限速；旧原生包会忽略这个参数。 */
export const BACKGROUND_DOWNLOAD_BANDWIDTH_SHARE = 0.5;

type Timer = ReturnType<typeof setTimeout>;

export interface BackgroundDownloadGateDeps {
  now: () => number;
  setTimer: (callback: () => void, delayMs: number) => Timer;
  clearTimer: (timer: Timer) => void;
  idleMs?: number;
}

export interface BackgroundDownloadGate {
  /** 接上登录状态（传入当前是否已登录）；之后按「登录满 idleMs」判定。 */
  bind(isAuthenticated: boolean): void;
  setAuthenticated(isAuthenticated: boolean): void;
  /** 断开登录状态，回到「视为打开」。 */
  unbind(): void;
  isOpen(): boolean;
  /** 闸门打开（或解绑）时通知，供被挡下的下载重试。 */
  subscribe(listener: () => void): () => void;
}

export function createBackgroundDownloadGate(deps: BackgroundDownloadGateDeps): BackgroundDownloadGate {
  const idleMs = deps.idleMs ?? BACKGROUND_DOWNLOAD_IDLE_MS;
  const listeners = new Set<() => void>();
  let bound = false;
  let authenticatedSince: number | null = null;
  let timer: Timer | null = null;

  const notify = () => {
    for (const listener of [...listeners]) {
      try {
        listener();
      } catch {
        // 某个订阅者出错不能影响其它更新通道。
      }
    }
  };
  const clearPending = () => {
    if (timer !== null) {
      deps.clearTimer(timer);
      timer = null;
    }
  };

  function setAuthenticated(isAuthenticated: boolean) {
    if (!bound) return;
    if (!isAuthenticated) {
      authenticatedSince = null;
      clearPending();
      return;
    }
    // 已在计时或已打开时重复上报登录（如刷新令牌）不重新计时。
    if (authenticatedSince !== null) return;
    authenticatedSince = deps.now();
    clearPending();
    timer = deps.setTimer(() => {
      timer = null;
      notify();
    }, idleMs);
  }

  return {
    bind(isAuthenticated) {
      bound = true;
      authenticatedSince = null;
      clearPending();
      setAuthenticated(isAuthenticated);
    },
    setAuthenticated,
    unbind() {
      bound = false;
      authenticatedSince = null;
      clearPending();
      notify();
    },
    isOpen() {
      if (!bound) return true;
      return authenticatedSince !== null && deps.now() - authenticatedSince >= idleMs;
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}

export const backgroundDownloadGate = createBackgroundDownloadGate({
  now: () => Date.now(),
  setTimer: (callback, delayMs) => setTimeout(callback, delayMs),
  clearTimer: (timer) => clearTimeout(timer),
});
