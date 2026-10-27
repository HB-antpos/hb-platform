/**
 * POS API 传输入口选择与故障转移（纯逻辑，不依赖 React Native / Expo，便于单测）。
 *
 * 设计要点：
 * - 「身份」与「传输入口」分离：设备开通码分区、考勤安全分区、更新缓存、图片路径等仍按配置里的
 *   规范地址（https://hotbargain.vip/pos-api）计算，保持稳定；下面三个入口只是同一后端的传输入口，
 *   构造请求 URL 的最后一步才决定实际访问哪个。
 * - 三个入口（都回源到同一个新加坡后端，凭据发往任一入口都属于同一信任域）：
 *     edge   pos.hotbargain.vip      腾讯 EdgeOne，离澳洲近（默认首选）
 *     aws    pos-syd.hotbargain.vip  AWS 悉尼边缘机，与腾讯完全独立的网络
 *     direct hotbargain.vip          直连新加坡源站，也是设备身份使用的规范 host
 *   不在这个组里的地址（本地开发、局域网、其他服务器）完全不参与，行为与改动前一致。
 * - 首选入口由后台按分店设置（经 receipt-profile/sync 的 apiEntryMode 下发），客户端用
 *   setPreferredMode 接收；候选顺序 = 首选 + 其余按固定优先级 aws → edge → direct（见 buildEntryOrder）。
 * - 退避：每个入口各自记冷却，失败后冷却 2/4/8/16/30 分钟；请求取「顺序中第一个不在冷却的入口」，
 *   冷却结束后由读请求 / 健康探测去试探，成功则清零。
 * - React Native 的 axios / fetch 没有「建连超时」：线路黑洞（SYN 被丢）只会在请求超时后才表现为失败。
 *   但超时也可能只是服务器慢，所以「超时」只有在该入口最近 60 秒没成功过时才算线路故障。
 * - 状态只保存在内存里；首选入口的取值由调用方另行持久化（见 api-entry-mode-coordinator.ts）。
 */

/** 走 EdgeOne 的入口，离澳洲近，绕开个别运营商到新加坡的不稳线路。 */
export const EDGE_API_HOST = "pos.hotbargain.vip";
/** 走 AWS 悉尼边缘机的入口，与腾讯线路相互独立。 */
export const AWS_API_HOST = "pos-syd.hotbargain.vip";
/** 直连新加坡源站的入口，也是设备身份使用的规范 host。 */
export const DIRECT_API_HOST = "hotbargain.vip";

/** 后台可选的首选入口代号（与服务端 receipt-profile/sync 的 apiEntryMode 取值一致）。 */
export type ApiEntryMode = "edge" | "aws" | "direct";
export const API_ENTRY_MODES: readonly ApiEntryMode[] = ["edge", "aws", "direct"];
/** 后台未设置（null / 缺失）时的默认首选。 */
export const DEFAULT_API_ENTRY_MODE: ApiEntryMode = "edge";

/** 入口代号 → 入口 host。 */
export const API_ENTRY_HOSTS: Readonly<Record<ApiEntryMode, string>> = Object.freeze({
  edge: EDGE_API_HOST,
  aws: AWS_API_HOST,
  direct: DIRECT_API_HOST,
});

/** 非首选入口的固定备用优先级：AWS 基本不额外花钱且绕开腾讯线路，EdgeOne 按请求计费放后面，直连最后。 */
const FALLBACK_PRIORITY: readonly ApiEntryMode[] = ["aws", "edge", "direct"];

/** 入口组内全部入口 host（用于 URL 守卫）。 */
const TRANSPORT_GROUP: readonly string[] = [EDGE_API_HOST, AWS_API_HOST, DIRECT_API_HOST];
/** 只有这个固定路径下的 HTTPS 默认端口地址才属于入口组。 */
const API_PATH_PREFIX = "/pos-api";
const BACKOFF_MINUTES = [2, 4, 8, 16, 30] as const;
/** 该入口在这个窗口内成功过，则单次超时更像服务器慢，不判定为线路故障。 */
const RECENT_SUCCESS_WINDOW_MS = 60_000;

/** 是否为合法的入口代号（严格匹配，不做大小写 / 空白容错）。 */
export function isApiEntryMode(value: unknown): value is ApiEntryMode {
  return value === "edge" || value === "aws" || value === "direct";
}

/**
 * 解析服务端下发的 apiEntryMode：
 * - 合法值 → 该值；null / undefined（后台未设置或旧服务端缺字段）→ 默认 edge；
 * - 其他任何值（未知字符串、非字符串）→ undefined，调用方必须保持当前设置不变。
 */
export function parseApiEntryMode(value: unknown): ApiEntryMode | undefined {
  if (value === null || value === undefined) {
    return DEFAULT_API_ENTRY_MODE;
  }
  return isApiEntryMode(value) ? value : undefined;
}

/** 入口 host → 入口代号；不在组内返回 null。 */
export function modeForHost(host: string): ApiEntryMode | null {
  const normalized = normalizeHost(host);
  return API_ENTRY_MODES.find((mode) => API_ENTRY_HOSTS[mode] === normalized) ?? null;
}

/**
 * 入口顺序规则（纯函数）：首选在前，其余按固定优先级 aws → edge → direct 排列（去掉首选）。
 *   edge   → [edge, aws, direct]
 *   aws    → [aws, edge, direct]
 *   direct → [direct, aws, edge]
 */
export function buildEntryOrder(preferred: ApiEntryMode): readonly ApiEntryMode[] {
  return [preferred, ...FALLBACK_PRIORITY.filter((mode) => mode !== preferred)];
}

export type TransportFailureKind = "network" | "timeout" | "edge";

function normalizeHost(value: string): string {
  return value.trim().replace(/\.$/u, "").toLocaleLowerCase("en-US");
}

/**
 * 判断一个 URL 是否属于入口组：https + 默认端口 + 入口组内的 host + /pos-api 路径。
 * 命中则返回把 host 换成指定入口后的 URL 生成函数；不命中返回 null。
 */
export function parseEntryUrl(
  value: string,
): Readonly<{ withHost(host: string): string }> | null {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (
    url.protocol !== "https:" ||
    url.port !== "" ||
    url.username !== "" ||
    url.password !== "" ||
    !TRANSPORT_GROUP.includes(normalizeHost(url.hostname)) ||
    !(url.pathname === API_PATH_PREFIX || url.pathname.startsWith(`${API_PATH_PREFIX}/`))
  ) {
    return null;
  }
  return Object.freeze({
    withHost(host: string): string {
      const next = new URL(url.toString());
      next.hostname = host;
      return next.toString();
    },
  });
}

/** 该地址是否属于入口组（才启用故障转移）。 */
export function isFailoverBaseUrl(value: string): boolean {
  return parseEntryUrl(value) !== null;
}

/** 入口组内全部入口的 origin（用于放宽「请求 origin 必须等于配置地址」的校验）。 */
export function entryOrigins(value: string): string[] {
  const entry = parseEntryUrl(value);
  if (!entry) {
    return [];
  }
  return TRANSPORT_GROUP.map((host) => new URL(entry.withHost(host)).origin);
}

/** 把入口组内的地址改写成指定入口；不在组内则原样返回。 */
export function rewriteEntryHost(value: string, host: string): string {
  return parseEntryUrl(value)?.withHost(host) ?? value;
}

/** 单个入口的健康状态。 */
type EntryState = {
  /** 冷却截止时间（毫秒时间戳）；0 = 没有未确认恢复的失败。 */
  cooldownUntil: number;
  /** 下一次失败使用的退避阶梯下标。 */
  backoffStep: number;
  /** 最近一次成功时间；从未成功为 undefined。 */
  lastSuccessAt: number | undefined;
};

function createEntryStates(): Record<ApiEntryMode, EntryState> {
  return {
    edge: { cooldownUntil: 0, backoffStep: 0, lastSuccessAt: undefined },
    aws: { cooldownUntil: 0, backoffStep: 0, lastSuccessAt: undefined },
    direct: { cooldownUntil: 0, backoffStep: 0, lastSuccessAt: undefined },
  };
}

export class ApiTransportSelector {
  private readonly now: () => number;
  private preferred: ApiEntryMode = DEFAULT_API_ENTRY_MODE;
  private states = createEntryStates();

  constructor(options: Readonly<{ now?: () => number }> = {}) {
    this.now = options.now ?? Date.now;
  }

  /** 当前首选入口（后台按分店设置；从未收到过则是默认 edge）。 */
  getPreferredMode(): ApiEntryMode {
    return this.preferred;
  }

  /**
   * 设置首选入口。null 表示后台未设置，按默认 edge；非法取值（未知字符串等）一律忽略、保持当前设置。
   * 值真正发生变化时清零各入口的冷却与退避：新首选应立刻被下一个读请求采用，
   * 而不是被旧首选遗留的冷却状态拖住。
   * @returns 是否发生了变化。
   */
  setPreferredMode(mode: ApiEntryMode | null): boolean {
    const next = parseApiEntryMode(mode);
    if (next === undefined || next === this.preferred) {
      return false;
    }
    this.preferred = next;
    // 保留 lastSuccessAt：它只用于「超时是否算线路故障」的 60 秒判断，与首选无关。
    for (const state of Object.values(this.states)) {
      state.cooldownUntil = 0;
      state.backoffStep = 0;
    }
    return true;
  }

  /** 当前候选顺序（首选在前）。 */
  getEntryOrder(): readonly ApiEntryMode[] {
    return buildEntryOrder(this.preferred);
  }

  /**
   * 现在应该使用的传输入口 host（本方法无副作用）。
   * 取「顺序中第一个不在冷却的入口」；全部在冷却则取冷却最早结束的（并列按顺序靠前者）。
   * @param options.probe 冷却结束后是否允许这次请求去「试探」曾经失败过的入口。
   *   读请求（GET/HEAD）和健康探测可以试探——失败了会当场换入口重试；
   *   写请求（支付、同步等）不能当探路的：它们失败了不会重放，所以只走从未失败（或已确认恢复）的入口，
   *   等读请求 / 健康探测确认曾失败的入口恢复后再切回。
   */
  resolve(options: Readonly<{ probe?: boolean }> = {}): string {
    const order = this.getEntryOrder();
    const now = this.now();
    const probe = options.probe !== false;
    const eligible = order.find((mode) => {
      const { cooldownUntil } = this.states[mode];
      return cooldownUntil === 0 || (probe && now >= cooldownUntil);
    });
    if (eligible) {
      return API_ENTRY_HOSTS[eligible];
    }
    // 都在冷却（或写请求面对的全是「待试探」入口）：取冷却最早结束的，并列按顺序靠前。
    let best = order[0] ?? DEFAULT_API_ENTRY_MODE;
    for (const mode of order) {
      if (this.states[mode].cooldownUntil < this.states[best].cooldownUntil) {
        best = mode;
      }
    }
    return API_ENTRY_HOSTS[best];
  }

  /** 当前实际在用的入口代号（不触发试探，用于界面展示）。 */
  getActiveMode(): ApiEntryMode {
    return modeForHost(this.resolve({ probe: false })) ?? this.preferred;
  }

  /**
   * 取 usedHost 之后「顺序中的下一个」入口（循环取，必不等于 usedHost）。
   * 优先跳过仍在冷却的入口——刚被判定故障的入口不值得当场再试；全都在冷却则取纯顺序的下一个。
   * usedHost 不在组内时返回顺序中的第一个。
   */
  fallbackFor(usedHost: string): string {
    const order = this.getEntryOrder();
    const usedMode = modeForHost(usedHost);
    const start = usedMode ? order.indexOf(usedMode) : -1;
    const others: ApiEntryMode[] = [];
    for (let step = 1; step <= order.length; step += 1) {
      const mode = order[(start + step) % order.length];
      if (mode && mode !== usedMode) {
        others.push(mode);
      }
    }
    const now = this.now();
    const next = others.find((mode) => now >= this.states[mode].cooldownUntil) ?? others[0];
    return API_ENTRY_HOSTS[next ?? DEFAULT_API_ENTRY_MODE];
  }

  /** 上报一次成功：该入口已确认可用，清零它自己的冷却与退避。 */
  reportSuccess(usedHost: string): void {
    const mode = modeForHost(usedHost);
    if (!mode) {
      return;
    }
    const state = this.states[mode];
    state.lastSuccessAt = this.now();
    state.cooldownUntil = 0;
    state.backoffStep = 0;
  }

  /**
   * 上报一次失败：失败的入口进入冷却（无论它是首选还是备用）。
   * @returns 本次失败后是否应当换入口（调用方据此决定读请求要不要立即换下一个入口重试）。
   */
  reportFailure(usedHost: string, kind: TransportFailureKind): boolean {
    const mode = modeForHost(usedHost);
    if (!mode) {
      return false;
    }
    const state = this.states[mode];
    const now = this.now();
    if (now < state.cooldownUntil) {
      // 已经处于冷却：这是更早派发出去的并发请求迟到的失败。不再升级退避——否则一次故障会被
      // N 个并发请求一次顶满到 30 分钟；但仍告诉调用方「该入口已判定故障」，读请求可以换入口重试。
      return true;
    }
    if (kind === "timeout" && state.lastSuccessAt !== undefined
      && now - state.lastSuccessAt <= RECENT_SUCCESS_WINDOW_MS) {
      // 刚成功过：单次超时更像服务器慢（报表、目录同步），不算线路故障。
      return false;
    }
    const step = Math.min(state.backoffStep, BACKOFF_MINUTES.length - 1);
    state.cooldownUntil = now + (BACKOFF_MINUTES[step] ?? 30) * 60_000;
    state.backoffStep = Math.min(state.backoffStep + 1, BACKOFF_MINUTES.length - 1);
    return true;
  }

  /** 首选入口当前是否处于冷却，即流量已经落到备用入口（回退中）。 */
  isUsingFallback(): boolean {
    const preferredState = this.states[this.preferred];
    return this.now() < preferredState.cooldownUntil;
  }
}

/** 进程内共享的选择器：同一 App 的所有 transport 与健康探测共用一份状态。 */
export const sharedApiTransportSelector = new ApiTransportSelector();

/**
 * 把请求失败归类为传输层失败；返回 null 表示不是线路问题，不触发切换。
 * - 有响应：普通 4xx/5xx 是服务器自己的回答（链路是通的）；只有 EdgeOne 专用的 520~530
 *   （522 连不上源站、524 回源超时等，源站和 ASP.NET 从不返回）才是「边缘出问题」。
 * - 无响应：ERR_NETWORK（域名解析、连接被拒/重置、TLS 失败）→ network；
 *   ECONNABORTED / ETIMEDOUT → timeout（线路黑洞的表现，也可能只是服务器慢，见 reportFailure）。
 * - 用户/代码主动取消不算失败。
 */
export function classifyTransportFailure(input: Readonly<{
  code?: string;
  status?: number;
  isCancel?: boolean;
}>): TransportFailureKind | null {
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

/** 只有读请求允许自动换入口重试：写请求（POST/PUT）即使连接失败也不重放，杜绝重复提交。 */
export function isTransportRetryableMethod(method: string | null | undefined): boolean {
  const normalized = (method ?? "get").toLowerCase();
  return normalized === "get" || normalized === "head";
}

type FetchLikeResponse = Readonly<{ ok?: boolean; status?: number }>;

/**
 * 给只读探测（健康检查）用的 fetch 包装：入口组内的地址先走当前入口（顺序中第一个不在冷却的），
 * 失败（异常、非 2xx）则换顺序中的下一个入口再试一次，最多再换一个——探测是「首选失败 → 换下一个」，
 * 不把三个入口全试完（每个入口失败都可能等满超时，会把探测拖得很久）；备用入口成功就让后续请求也改走备用。
 * - 调用方自己的取消 / 超时（signal.aborted）不再重试，只上报「超时」交给 60 秒规则判断；
 * - 两次都不通：原样返回 / 抛出第一次的结果，不改变入口选择（真实请求自己会逐个入口上报并冷却）；
 * - 不在入口组内的地址直接透传，行为不变。
 */
export function createFailoverFetch<
  TInit extends Readonly<{ signal?: AbortSignal | null | undefined }>,
  TResponse extends FetchLikeResponse,
>(
  fetcher: (url: string, init: TInit) => Promise<TResponse>,
  selector: ApiTransportSelector = sharedApiTransportSelector,
): (url: string, init: TInit) => Promise<TResponse> {
  return async (url, init) => {
    const entry = parseEntryUrl(url);
    if (!entry) {
      return fetcher(url, init);
    }
    const first = selector.resolve();
    const second = selector.fallbackFor(first);

    let firstResponse: TResponse | undefined;
    let firstError: unknown;
    let firstFailed = false;
    try {
      const response = await fetcher(entry.withHost(first), init);
      if (response.ok === true) {
        selector.reportSuccess(first);
        return response;
      }
      firstResponse = response;
    } catch (error) {
      if (init.signal?.aborted) {
        // 调用方取消或超时：线路是否有问题交给 60 秒规则判断，本次不再重试。
        selector.reportFailure(first, "timeout");
        throw error;
      }
      firstError = error;
      firstFailed = true;
    }

    try {
      const response = await fetcher(entry.withHost(second), init);
      if (response.ok === true) {
        // 另一个入口通了而第一个没通：判定第一个入口有问题。
        const kind: TransportFailureKind = firstFailed
          ? "network"
          : classifyTransportFailure({
              ...(typeof firstResponse?.status === "number" ? { status: firstResponse.status } : {}),
            }) ?? "edge";
        selector.reportFailure(first, kind);
        selector.reportSuccess(second);
        return response;
      }
    } catch (error) {
      if (init.signal?.aborted) {
        throw error;
      }
    }

    // 两次都不通：保持第一次的结果，不改变入口选择。
    if (firstFailed) {
      throw firstError;
    }
    return firstResponse as TResponse;
  };
}
