/**
 * API 传输入口选择的运行时胶水层：持有进程内唯一的选择器，并把状态落盘到本地存储。
 * 纯逻辑（入口组、退避、错误分类）在 transport-failover.ts，便于脱离 React Native 单测。
 */
import { buildApiBaseUrl } from "@/shared/api/config";
import {
  ApiTransportSelector,
  type PersistedTransportState,
} from "@/shared/api/transport-failover";
import { AppAsyncStorage } from "@/shared/storage/async-storage";

const TRANSPORT_STATE_STORAGE_KEY = "hbweb_api_transport_state";

export const apiTransportSelector = new ApiTransportSelector({
  persist: (state) => {
    // 落盘失败不影响请求：最坏情况是重启后重新判断一次。
    void AppAsyncStorage.setObject(TRANSPORT_STATE_STORAGE_KEY, state).catch(() => undefined);
  },
});

let hydration: Promise<void> | null = null;

/**
 * 在第一次使用选择器之前恢复上次的状态：App 重启后冷却期内直接走备用入口，
 * 避免线路黑洞时每次冷启动的第一个请求都白等 30 秒超时。读取失败则按默认（首选边缘入口）。
 */
export function ensureApiTransportHydrated(): Promise<void> {
  hydration ??= AppAsyncStorage.getObject<PersistedTransportState>(TRANSPORT_STATE_STORAGE_KEY)
    .then((state) => {
      if (state) {
        apiTransportSelector.hydrate(state);
      }
    })
    .catch(() => undefined);
  return hydration;
}

/**
 * 用「身份 host」构造实际发请求的基础地址：经传输选择器映射到当前应使用的入口。
 * 身份 host 仍是设备绑定、多账号保险库等一切比较使用的值，这里只改最终访问的域名。
 * transportHostOverride 供故障转移重试时强制指定备用入口。
 * options.probe=false 用于写请求（登录、刷新令牌、设备换票等 POST）：冷却结束后不拿它们去试探首选入口，
 * 由读请求 / 健康探测先确认首选入口恢复（见 ApiTransportSelector.resolve）。
 */
export function buildTransportApiBaseUrl(
  identityHost: string,
  transportHostOverride?: string,
  options: { probe?: boolean } = {},
) {
  return buildApiBaseUrl(transportHostOverride ?? apiTransportSelector.resolve(identityHost, options));
}

/**
 * 对「身份 host」做一次探测（健康检查、打卡网络校验等）：先试当前入口，不通再试另一个入口；
 * 另一个入口通而当前入口不通，就让选择器改走通的入口（后续业务请求同步切换，避免「探测通、业务不通」的分裂）。
 * attempt 必须自带独立的超时控制：第一次耗尽超时后，第二次不能共用已中止的 AbortController。
 * 没有备用入口时（入口组之外的 host）只试一次，行为与改动前完全一致。
 */
export async function probeWithTransportFallback<T>(
  identityHost: string,
  attempt: (apiBaseUrl: string) => Promise<T>,
  isSuccess: (value: T) => boolean = () => true,
): Promise<T> {
  await ensureApiTransportHydrated();
  const first = apiTransportSelector.resolve(identityHost);
  const fallback = apiTransportSelector.fallbackFor(identityHost, first);
  if (!fallback) {
    return attempt(buildApiBaseUrl(first));
  }

  let firstError: unknown;
  let firstValue: T | undefined;
  let firstThrew = false;
  try {
    const value = await attempt(buildApiBaseUrl(first));
    if (isSuccess(value)) {
      apiTransportSelector.reportSuccess(identityHost, first);
      return value;
    }
    firstValue = value;
  } catch (error) {
    firstThrew = true;
    firstError = error;
  }

  try {
    const value = await attempt(buildApiBaseUrl(fallback));
    if (isSuccess(value)) {
      // 另一个入口通而当前入口不通：这是线路问题。只有当前入口是首选时才会真正切换。
      apiTransportSelector.reportFailure(identityHost, first, "network");
      apiTransportSelector.reportSuccess(identityHost, fallback);
      return value;
    }
  } catch {
    // 两个入口都不通：按第一次的结果返回，不改变入口选择。
  }

  if (firstThrew) {
    throw firstError;
  }
  return firstValue as T;
}
