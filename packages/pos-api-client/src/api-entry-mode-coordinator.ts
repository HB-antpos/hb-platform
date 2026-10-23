/**
 * POS API「首选入口」的接收与本机记忆（手持 / iPad 共用，纯 TS，无原生依赖）。
 *
 * 后台按分店设置首选入口，经 `GET api/v1/stores/current/receipt-profile/sync` 响应顶层的
 * `apiEntryMode`（"edge" | "aws" | "direct"，null / 缺失 = 后台未设置）下发。本协调器负责：
 * 1. apply：每次 sync 成功后（无论小票资料 changed 与否）调用——合法值或 null/缺失交给共享选择器，
 *    未知取值保持当前设置不变；取值变化时写入本机存储；
 * 2. restore：App 冷启动早期读取本机记住的值喂给选择器，让第一批请求就走上次的首选入口；
 *    之后被服务端值覆盖；从未收到过则保持默认 edge。
 *
 * 持久化只是体验优化：任何读写失败都被吞掉（只记一条日志），绝不影响同步与收银。
 * 存储介质由两个 App 注入：生产接线传 AsyncStorage（getItem / setItem / removeItem），
 * 测试传内存假实现；本文件不依赖任何原生模块。
 */
import {
  parseApiEntryMode,
  sharedApiTransportSelector,
  type ApiEntryMode,
  type ApiTransportSelector,
} from "./transport-failover";

/** 本机存储端口：write(null) 表示「后台未设置」（实现可直接删除该键）。 */
export interface ApiEntryModeStoragePort {
  read(): Promise<string | null>;
  write(mode: ApiEntryMode | null): Promise<void>;
}

/** 本机保存首选入口的键（AsyncStorage 按 App 沙盒隔离，两个 App 可共用同一个键名；带版本后缀便于以后换格式）。 */
export const API_ENTRY_MODE_STORAGE_KEY = "hbpos.api-entry-mode.v1";

/** AsyncStorage 的最小形状（@react-native-async-storage/async-storage 的默认导出满足它）。 */
export interface AsyncKeyValueStorage {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

/** 把 AsyncStorage 形状的键值存储适配成存储端口：write(null) = 后台未设置，直接删除该键。 */
export function createAsyncStorageApiEntryModeStorage(
  storage: AsyncKeyValueStorage,
  key: string = API_ENTRY_MODE_STORAGE_KEY,
): ApiEntryModeStoragePort {
  return {
    read: () => storage.getItem(key),
    write: (mode) => (mode === null ? storage.removeItem(key) : storage.setItem(key, mode)),
  };
}

export type ApiEntryModeCoordinatorOptions = Readonly<{
  storage: ApiEntryModeStoragePort;
  selector?: ApiTransportSelector;
  /** 日志旁路，只含取值与失败原因等非敏感标量；缺省时静默。 */
  log?: (message: string, properties: Readonly<Record<string, string>>) => void;
}>;

export type ApiEntryModeApplyOutcome = "applied" | "ignored";

export interface ApiEntryModeCoordinator {
  /** 冷启动：读取本机记住的值；已收到过服务端值则不覆盖。永不 reject。 */
  restore(): Promise<void>;
  /**
   * 应用服务端下发的取值（原样传入响应里的 apiEntryMode 字段）。同步生效，持久化在后台进行。
   * 未知取值返回 "ignored" 且不改变任何状态。永不抛错。
   */
  apply(raw: unknown): ApiEntryModeApplyOutcome;
  /** 等待已排队的本机写入结束（测试与优雅关闭用）。永不 reject。 */
  flush(): Promise<void>;
}

export function createApiEntryModeCoordinator(
  options: ApiEntryModeCoordinatorOptions,
): ApiEntryModeCoordinator {
  const selector = options.selector ?? sharedApiTransportSelector;
  const { storage } = options;
  /** 本机已持久化的值：undefined = 尚不确定（需要写入）；null = 已记为「未设置」。 */
  let persisted: ApiEntryMode | null | undefined;
  /** 是否已收到过服务端的有效取值；之后 restore 不得再覆盖。 */
  let serverApplied = false;
  let writeQueue: Promise<void> = Promise.resolve();

  const log = (message: string, properties: Readonly<Record<string, string>>) => {
    try {
      options.log?.(message, properties);
    } catch {
      // 日志旁路故障不能影响同步。
    }
  };

  const persist = (value: ApiEntryMode | null) => {
    if (persisted === value) {
      return;
    }
    // 先乐观记下，写失败再回退成「不确定」，让下一轮同步重试；串行排队避免乱序覆盖。
    persisted = value;
    writeQueue = writeQueue
      .then(() => storage.write(value))
      .catch(() => {
        if (persisted === value) {
          persisted = undefined;
        }
        log("POS API entry mode could not be saved.", { mode: value ?? "unset" });
      });
  };

  return {
    async restore() {
      try {
        const stored = await storage.read();
        if (serverApplied) {
          return;
        }
        if (stored === null) {
          persisted = null;
          return;
        }
        const mode = parseApiEntryMode(stored);
        // 本机存了无法识别的值（版本回退、损坏）：当作没有，等服务端下发。
        if (mode === undefined || stored !== mode) {
          return;
        }
        persisted = mode;
        selector.setPreferredMode(mode);
      } catch {
        log("POS API entry mode could not be restored.", {});
      }
    },

    apply(raw) {
      try {
        const mode = parseApiEntryMode(raw);
        if (mode === undefined) {
          // 未知取值（可能是更新的服务端）：保持当前设置不变，也不写入本机。
          log("POS API entry mode ignored.", { value: String(raw).slice(0, 32) });
          return "ignored";
        }
        serverApplied = true;
        selector.setPreferredMode(mode);
        // null / 缺失记为「未设置」，与显式 edge 区分，方便后续排查「后台到底有没有设置」。
        persist(raw === null || raw === undefined ? null : mode);
        return "applied";
      } catch {
        return "ignored";
      }
    },

    async flush() {
      await writeQueue;
    },
  };
}
