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
 * 3. 本机手动覆盖（设置页「收银入口」）：用户可在这台设备上手动指定线路，优先级高于后台下发值。
 *    手动值单独存放（独立 key，见 API_ENTRY_OVERRIDE_STORAGE_KEY），绝不与后台值混存——否则后台下一次
 *    同步会把它覆盖掉。存在覆盖时，后台下发值只记下来用于展示（「后台指定为 X」），不交给选择器；
 *    清除覆盖（选「自动」）后立即回到后台值（后台未设置则默认 edge）。
 *
 * 持久化只是体验优化：任何读写失败都被吞掉（只记一条日志），绝不影响同步与收银。
 * 存储介质由两个 App 注入：生产接线传 AsyncStorage（getItem / setItem / removeItem），
 * 测试传内存假实现；本文件不依赖任何原生模块。
 */
import {
  isApiEntryMode,
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

/** 本机手动覆盖的存储键：与后台下发值的键分开存放，后台同步永远不会读写它。 */
export const API_ENTRY_OVERRIDE_STORAGE_KEY = "hbpos.api-entry-override.v1";

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
  /** 本机手动覆盖的存储端口（独立 key）；不传则手动覆盖只在本次运行内有效，不落盘。 */
  overrideStorage?: ApiEntryModeStoragePort;
  selector?: ApiTransportSelector;
  /** 日志旁路，只含取值与失败原因等非敏感标量；缺省时静默。 */
  log?: (message: string, properties: Readonly<Record<string, string>>) => void;
}>;

export type ApiEntryModeApplyOutcome = "applied" | "ignored";

/** 设置页展示用的快照（纯数据）。 */
export type ApiEntrySettingsSnapshot = Readonly<{
  /** 本机手动指定的线路；null = 自动（跟随后台）。 */
  manualMode: ApiEntryMode | null;
  /** 后台为本店指定的线路；null = 后台未指定（或尚未收到）。 */
  backendMode: ApiEntryMode | null;
  /** 当前生效的首选线路（= 选择器的首选：手动 > 后台 > 默认 edge）。 */
  preferredMode: ApiEntryMode;
  /** 此刻实际在用的线路（首选处于冷却时会是备用）。 */
  activeMode: ApiEntryMode;
  /** 首选线路当前不通，流量已落到备用。 */
  usingFallback: boolean;
  /** 首选的来源。 */
  source: "manual" | "backend" | "default";
  /** 最近一次保存手动设置失败：本次运行仍然有效，但重启后会丢失。 */
  manualSaveFailed: boolean;
}>;

/** 设置页使用的最小控制面（协调器的子集，便于界面测试注入假实现）。 */
export interface ApiEntrySettingsController {
  getSnapshot(): ApiEntrySettingsSnapshot;
  /** 状态（含后台值 / 手动覆盖）变化时通知；返回取消订阅。 */
  subscribe(listener: () => void): () => void;
  /**
   * 手动指定线路；null = 恢复自动（清除本机覆盖）。同步生效，持久化在后台进行。
   * 非法取值返回 "ignored" 且不改变任何状态。永不抛错。
   */
  setManualMode(mode: ApiEntryMode | null): ApiEntryModeApplyOutcome;
}

export interface ApiEntryModeCoordinator extends ApiEntrySettingsController {
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
  const { storage, overrideStorage } = options;
  /** 本机已持久化的后台值：undefined = 尚不确定（需要写入）；null = 已记为「未设置」。 */
  let persisted: ApiEntryMode | null | undefined;
  /** 是否已收到过服务端的有效取值；之后 restore 不得再覆盖。 */
  let serverApplied = false;
  /** 后台为本店指定的线路（仅用于展示与「清除覆盖后回退」）；null = 未指定 / 尚未收到。 */
  let backendMode: ApiEntryMode | null = null;
  /** 本机手动覆盖；null = 自动（跟随后台）。 */
  let manualMode: ApiEntryMode | null = null;
  /** 本机已持久化的手动覆盖：undefined = 尚不确定（需要写入）。 */
  let persistedManual: ApiEntryMode | null | undefined;
  /** 本次运行内用户是否已动过手动设置；之后 restore 读到的旧值不得覆盖它。 */
  let manualTouched = false;
  let manualSaveFailed = false;
  let writeQueue: Promise<void> = Promise.resolve();
  const listeners = new Set<() => void>();

  const log = (message: string, properties: Readonly<Record<string, string>>) => {
    try {
      options.log?.(message, properties);
    } catch {
      // 日志旁路故障不能影响同步。
    }
  };

  const notify = () => {
    for (const listener of [...listeners]) {
      try {
        listener();
      } catch {
        // 界面订阅者出错不能影响同步与收银。
      }
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

  const persistManual = (value: ApiEntryMode | null) => {
    if (!overrideStorage || persistedManual === value) {
      return;
    }
    persistedManual = value;
    writeQueue = writeQueue
      .then(async () => {
        await overrideStorage.write(value);
        // 只有最新一次写入成功才清除失败标记，避免旧写入的结果盖掉新的失败。
        if (persistedManual === value) {
          manualSaveFailed = false;
          notify();
        }
      })
      .catch(() => {
        if (persistedManual === value) {
          persistedManual = undefined;
          manualSaveFailed = true;
          notify();
        }
        log("POS API entry override could not be saved.", { mode: value ?? "auto" });
      });
  };

  /** 把「手动 > 后台 > 默认」的结果交给选择器。 */
  const syncSelector = () => {
    selector.setPreferredMode(manualMode ?? backendMode);
  };

  const restoreBackend = async () => {
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
    backendMode = mode;
  };

  const restoreManual = async () => {
    if (!overrideStorage) {
      return;
    }
    const stored = await overrideStorage.read();
    if (manualTouched) {
      return;
    }
    if (stored === null) {
      persistedManual = null;
      return;
    }
    // 无法识别的值（版本回退、损坏）：当作「自动」，不报错。
    if (!isApiEntryMode(stored)) {
      return;
    }
    persistedManual = stored;
    manualMode = stored;
  };

  return {
    async restore() {
      // 两个键互不依赖：任何一个读失败都只记日志，另一个照常生效。
      await restoreBackend().catch(() => {
        log("POS API entry mode could not be restored.", {});
      });
      await restoreManual().catch(() => {
        log("POS API entry override could not be restored.", {});
      });
      // 与旧行为一致：本机什么都没记过 / 记的值不可识别时，不碰选择器当前设置。
      if (manualMode !== null || backendMode !== null) {
        syncSelector();
      }
      notify();
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
        // 后台值始终记下（用于展示与清除覆盖后的回退），但存在本机手动覆盖时不交给选择器。
        backendMode = raw === null || raw === undefined ? null : mode;
        syncSelector();
        // null / 缺失记为「未设置」，与显式 edge 区分，方便后续排查「后台到底有没有设置」。
        persist(backendMode);
        notify();
        return "applied";
      } catch {
        return "ignored";
      }
    },

    setManualMode(mode) {
      try {
        if (mode !== null && !isApiEntryMode(mode)) {
          return "ignored";
        }
        manualTouched = true;
        manualMode = mode;
        syncSelector();
        persistManual(mode);
        notify();
        return "applied";
      } catch {
        return "ignored";
      }
    },

    getSnapshot() {
      const preferredMode = selector.getPreferredMode();
      return {
        manualMode,
        backendMode,
        preferredMode,
        activeMode: selector.getActiveMode(),
        usingFallback: selector.isUsingFallback(),
        source: manualMode !== null ? "manual" : backendMode !== null ? "backend" : "default",
        manualSaveFailed,
      };
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    async flush() {
      await writeQueue;
    },
  };
}
