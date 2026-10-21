/**
 * API 传输入口选择与故障转移（纯逻辑，不依赖 React Native，便于单测）。
 *
 * 设计要点：
 * - 「身份 host」（设备绑定、多账号保险库、预设地址、激活流程都按它比较）保持 hotbargain.vip 不变；
 *   边缘入口只是传输层的透明优化：构造请求 URL 的最后一步，才把身份 host 映射成实际访问的入口。
 * - 候选入口：pos.hotbargain.vip（EdgeOne，离澳洲近，首选）→ hotbargain.vip（直连新加坡，备用）。
 *   两者是同一个后端，凭据发往任一入口都属于同一信任域；不在这个组里的 host 只有一个候选，行为完全不变。
 * - 退避：首选失败后冷却 2/4/8/16/30 分钟；冷却结束由下一个请求去试探首选，首选成功则清零。
 * - React Native 的 axios 没有「建连超时」：线路黑洞（SYN 被丢）只会表现为 30 秒后的超时。
 *   但超时也可能只是服务器慢（报表 40~170 秒），所以「超时」只有在该入口最近没成功过时才算线路故障。
 */

/** 走 EdgeOne 的入口，离澳洲近，绕开个别运营商到新加坡的不稳线路。 */
export const EDGE_API_HOST = "pos.hotbargain.vip";
/** 直连新加坡源站的入口，也是设备身份使用的规范 host。 */
export const DIRECT_API_HOST = "hotbargain.vip";

/** 首选在前。 */
const TRANSPORT_GROUP: readonly string[] = [EDGE_API_HOST, DIRECT_API_HOST];
const BACKOFF_MINUTES = [2, 4, 8, 16, 30] as const;
/** 该入口在这个窗口内成功过，则单次超时更像服务器慢，不判定为线路故障。 */
const RECENT_SUCCESS_WINDOW_MS = 60_000;

export type TransportFailureKind = "network" | "timeout" | "edge";

export interface PersistedTransportState {
  activeIndex: number;
  stickyUntil: number;
  backoffStep: number;
}

interface TransportSelectorOptions {
  now?: () => number;
  /** 状态变化时调用，用于落盘；App 重启后冷却期内直接走备用入口，避免每次冷启动都白等 30 秒。 */
  persist?: (state: PersistedTransportState) => void;
}

function normalizeHost(value: string) {
  return value.trim().replace(/\.$/u, "").toLocaleLowerCase("en-US");
}

/** 返回包含自身在内的候选入口：在入口组内是固定的 [边缘, 直连]，否则只有它自己。 */
export function transportCandidates(identityHost: string): string[] {
  const normalized = normalizeHost(identityHost);
  return TRANSPORT_GROUP.includes(normalized) ? [...TRANSPORT_GROUP] : [identityHost];
}

export class ApiTransportSelector {
  private readonly now: () => number;
  private readonly persist?: (state: PersistedTransportState) => void;
  private activeIndex = 0;
  private stickyUntil = 0;
  private backoffStep = 0;
  private readonly lastSuccessAt = new Map<string, number>();

  constructor(options: TransportSelectorOptions = {}) {
    this.now = options.now ?? Date.now;
    this.persist = options.persist;
  }

  /** 恢复上次落盘的状态（来自本地存储，需做基本校验）。 */
  hydrate(state: PersistedTransportState) {
    if (
      !Number.isInteger(state.activeIndex) || state.activeIndex < 0 || state.activeIndex >= TRANSPORT_GROUP.length ||
      !Number.isFinite(state.stickyUntil) ||
      !Number.isInteger(state.backoffStep) || state.backoffStep < 0
    ) {
      return;
    }
    this.activeIndex = state.activeIndex;
    this.stickyUntil = state.stickyUntil;
    this.backoffStep = Math.min(state.backoffStep, BACKOFF_MINUTES.length - 1);
  }

  /**
   * 现在应该使用的传输入口 host。
   * @param options.probe 冷却结束后是否允许这次请求去「试探」首选入口。读请求（GET/HEAD）和健康探测可以试探——
   *   失败了会当场换入口重试；写请求（POST/PUT）失败了不会重放，不能拿来探路，所以继续走已确认可用的入口，
   *   等读请求 / 健康探测确认首选入口恢复后再切回。
   */
  resolve(identityHost: string, options: { probe?: boolean } = {}): string {
    const candidates = transportCandidates(identityHost);
    if (candidates.length === 1) {
      return identityHost;
    }
    if (this.activeIndex !== 0 && this.now() >= this.stickyUntil && options.probe !== false) {
      // 冷却结束：回到首选入口试探。是否真的恢复由 reportSuccess / reportFailure 决定。
      this.activeIndex = 0;
      this.save();
    }
    return candidates[Math.min(this.activeIndex, candidates.length - 1)];
  }

  /** 取一个与 usedHost 不同的备用入口；没有备用时返回 null。 */
  fallbackFor(identityHost: string, usedHost: string): string | null {
    const used = normalizeHost(usedHost);
    return transportCandidates(identityHost).find((candidate) => normalizeHost(candidate) !== used) ?? null;
  }

  /** 上报一次成功。首选入口成功说明线路已恢复，清零退避。 */
  reportSuccess(identityHost: string, usedHost: string) {
    const candidates = transportCandidates(identityHost);
    if (candidates.length === 1) {
      return;
    }
    this.lastSuccessAt.set(normalizeHost(usedHost), this.now());
    if (normalizeHost(usedHost) === normalizeHost(candidates[0]) && (this.activeIndex !== 0 || this.backoffStep !== 0)) {
      this.activeIndex = 0;
      this.backoffStep = 0;
      this.save();
    }
  }

  /**
   * 上报一次失败。仅当失败的是首选入口时才切到备用入口。
   * @returns 本次失败是否让传输入口切到了备用（调用方据此决定要不要立即重试）。
   */
  reportFailure(identityHost: string, usedHost: string, kind: TransportFailureKind): boolean {
    const candidates = transportCandidates(identityHost);
    if (candidates.length === 1 || normalizeHost(usedHost) !== normalizeHost(candidates[0])) {
      return false;
    }
    if (this.activeIndex !== 0 && this.now() < this.stickyUntil) {
      // 已经处于冷却：这是更早派发出去的并发请求迟到的失败。不再升级退避——否则一次故障会被
      // N 个并发请求一次顶满到 30 分钟；但仍告诉调用方「已在用备用入口」，读请求可以换入口重试。
      return true;
    }
    if (kind === "timeout") {
      const lastSuccess = this.lastSuccessAt.get(normalizeHost(usedHost)) ?? 0;
      if (this.now() - lastSuccess <= RECENT_SUCCESS_WINDOW_MS) {
        return false;
      }
    }
    const step = Math.min(this.backoffStep, BACKOFF_MINUTES.length - 1);
    this.activeIndex = 1;
    this.stickyUntil = this.now() + BACKOFF_MINUTES[step] * 60_000;
    this.backoffStep = Math.min(this.backoffStep + 1, BACKOFF_MINUTES.length - 1);
    this.save();
    return true;
  }

  /** 当前是否处于「已切到备用入口」状态。 */
  isUsingFallback(identityHost: string): boolean {
    if (transportCandidates(identityHost).length === 1) {
      return false;
    }
    return this.activeIndex !== 0 && this.now() < this.stickyUntil;
  }

  private save() {
    this.persist?.({
      activeIndex: this.activeIndex,
      stickyUntil: this.stickyUntil,
      backoffStep: this.backoffStep,
    });
  }
}

/**
 * 把 axios 错误归类为传输层失败；返回 null 表示不是线路问题，不触发切换。
 * - 有响应：普通 4xx/5xx 是服务器自己的回答（链路是通的）；只有 EdgeOne 专用的 520~530
 *   （522 连不上源站、524 回源超时等，源站和 ASP.NET 从不返回）才是「边缘出问题」。
 * - 无响应：ERR_NETWORK（域名解析、连接被拒/重置、TLS 失败）→ network；
 *   ECONNABORTED / ETIMEDOUT → timeout（线路黑洞的表现，也可能只是服务器慢，见 reportFailure）。
 * - 用户/代码主动取消不算失败。
 */
export function classifyTransportFailure(input: {
  code?: string;
  status?: number;
  isCancel?: boolean;
}): TransportFailureKind | null {
  if (input.isCancel) {
    return null;
  }
  if (typeof input.status === "number") {
    return input.status >= 520 && input.status <= 530 ? "edge" : null;
  }
  if (input.code === "ERR_NETWORK") {
    return "network";
  }
  if (input.code === "ECONNABORTED" || input.code === "ETIMEDOUT") {
    return "timeout";
  }
  return null;
}

/** 只有读请求允许自动换入口重试：写请求（POST 等）即使连接失败也不重放，杜绝重复提交。 */
export function isTransportRetryableMethod(method?: string | null) {
  const normalized = (method ?? "get").toLowerCase();
  return normalized === "get" || normalized === "head";
}
