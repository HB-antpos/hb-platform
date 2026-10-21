/**
 * POS API 传输入口选择与故障转移（纯逻辑，不依赖 React Native / Expo，便于单测）。
 *
 * 设计要点：
 * - 「身份」与「传输入口」分离：设备开通码分区、考勤安全分区、更新缓存、图片路径等仍按配置里的
 *   规范地址（https://hotbargain.vip/pos-api）计算，保持稳定；pos.hotbargain.vip 只是同一后端的
 *   第二个传输入口，构造请求 URL 的最后一步才决定实际访问哪个。
 * - 候选入口：pos.hotbargain.vip（EdgeOne，离澳洲近，首选）→ hotbargain.vip（直连新加坡，备用）。
 *   两者回源到同一个后端，凭据发往任一入口都属于同一信任域；不在这个组里的地址（本地开发、
 *   局域网、其他服务器）完全不参与，行为与改动前一致。
 * - 退避：首选失败后冷却 2/4/8/16/30 分钟；冷却结束由下一个请求去试探首选，首选成功则清零。
 * - React Native 的 axios / fetch 没有「建连超时」：线路黑洞（SYN 被丢）只会在请求超时后才表现为失败。
 *   但超时也可能只是服务器慢，所以「超时」只有在该入口最近 60 秒没成功过时才算线路故障。
 * - 状态只保存在内存里：进程重启后重新从首选入口开始；首个失败的请求（或健康探测）会触发切换。
 */

/** 走 EdgeOne 的入口，离澳洲近，绕开个别运营商到新加坡的不稳线路。 */
export const EDGE_API_HOST = "pos.hotbargain.vip";
/** 直连新加坡源站的入口，也是设备身份使用的规范 host。 */
export const DIRECT_API_HOST = "hotbargain.vip";

/** 首选在前。 */
const TRANSPORT_GROUP: readonly string[] = [EDGE_API_HOST, DIRECT_API_HOST];
/** 只有这个固定路径下的 HTTPS 默认端口地址才属于入口组。 */
const API_PATH_PREFIX = "/pos-api";
const BACKOFF_MINUTES = [2, 4, 8, 16, 30] as const;
/** 该入口在这个窗口内成功过，则单次超时更像服务器慢，不判定为线路故障。 */
const RECENT_SUCCESS_WINDOW_MS = 60_000;

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

export class ApiTransportSelector {
  private readonly now: () => number;
  private activeIndex = 0;
  private stickyUntil = 0;
  private backoffStep = 0;
  private readonly lastSuccessAt = new Map<string, number>();

  constructor(options: Readonly<{ now?: () => number }> = {}) {
    this.now = options.now ?? Date.now;
  }

  /**
   * 现在应该使用的传输入口 host。
   * @param options.probe 冷却结束后是否允许这次请求去「试探」首选入口。
   *   读请求（GET/HEAD）和健康探测可以试探——失败了会当场换入口重试；
   *   写请求（支付、同步等）不能当探路的：它们失败了不会重放，所以继续走已确认可用的入口，
   *   等读请求 / 健康探测确认首选入口恢复后再切回。
   */
  resolve(options: Readonly<{ probe?: boolean }> = {}): string {
    if (this.activeIndex !== 0 && this.now() >= this.stickyUntil && options.probe !== false) {
      // 冷却结束：回到首选入口试探。是否真的恢复由 reportSuccess / reportFailure 决定。
      this.activeIndex = 0;
    }
    return TRANSPORT_GROUP[this.activeIndex] ?? EDGE_API_HOST;
  }

  /** 取一个与 usedHost 不同的备用入口。 */
  fallbackFor(usedHost: string): string {
    const used = normalizeHost(usedHost);
    return TRANSPORT_GROUP.find((candidate) => candidate !== used) ?? DIRECT_API_HOST;
  }

  /** 上报一次成功。首选入口成功说明线路已恢复，清零退避。 */
  reportSuccess(usedHost: string): void {
    const used = normalizeHost(usedHost);
    if (!TRANSPORT_GROUP.includes(used)) {
      return;
    }
    this.lastSuccessAt.set(used, this.now());
    if (used === EDGE_API_HOST && (this.activeIndex !== 0 || this.backoffStep !== 0)) {
      this.activeIndex = 0;
      this.backoffStep = 0;
    }
  }

  /**
   * 上报一次失败。仅当失败的是首选入口时才切到备用入口。
   * @returns 本次失败是否让传输入口切到了备用（调用方据此决定要不要立即重试）。
   */
  reportFailure(usedHost: string, kind: TransportFailureKind): boolean {
    const used = normalizeHost(usedHost);
    if (used !== EDGE_API_HOST) {
      return false;
    }
    if (this.activeIndex !== 0 && this.now() < this.stickyUntil) {
      // 已经处于冷却：这是更早派发出去的并发请求迟到的失败。不再升级退避——否则一次故障会被
      // N 个并发请求一次顶满到 30 分钟；但仍告诉调用方「已在用备用入口」，读请求可以换入口重试。
      return true;
    }
    if (kind === "timeout") {
      const lastSuccess = this.lastSuccessAt.get(used) ?? 0;
      if (this.now() - lastSuccess <= RECENT_SUCCESS_WINDOW_MS) {
        return false;
      }
    }
    const step = Math.min(this.backoffStep, BACKOFF_MINUTES.length - 1);
    this.activeIndex = 1;
    this.stickyUntil = this.now() + (BACKOFF_MINUTES[step] ?? 30) * 60_000;
    this.backoffStep = Math.min(this.backoffStep + 1, BACKOFF_MINUTES.length - 1);
    return true;
  }

  /** 当前是否处于「已切到备用入口」状态。 */
  isUsingFallback(): boolean {
    return this.activeIndex !== 0 && this.now() < this.stickyUntil;
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
 * 给只读探测（健康检查）用的 fetch 包装：入口组内的地址先走当前入口，
 * 失败（异常、非 2xx）则换另一个入口再试一次；备用入口成功就让后续请求也改走备用。
 * - 调用方自己的取消 / 超时（signal.aborted）不再重试，只上报「超时」交给 60 秒规则判断；
 * - 两个入口都不通：原样返回 / 抛出第一次的结果，不改变入口选择；
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

    // 两个入口都不通：保持第一次的结果，不改变入口选择。
    if (firstFailed) {
      throw firstError;
    }
    return firstResponse as TResponse;
  };
}
