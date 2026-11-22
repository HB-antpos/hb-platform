/**
 * BLE 扫码枪连接控制器（纯 TS，蓝牙库通过 BleScannerTransport 注入）。
 *
 * 与 ble-scanner-core.ts 一样，本文件在 iPad POS 与移动端各有一份逐字相同的拷贝，
 * 由 apps/mobile/src/modules/scanner/ble/ble-shared-sync.test.ts 校验一致。
 *
 * 职责：搜索、连接、选数据通道（已知型号直连，未知型号嗅探学习）、订阅拼帧、
 * 读电量、断开后重连（挂起式请求 + 前台唤醒扫描）、休眠判断。不负责把码投给页面——那是各端路由的事。
 */
import {
  BLE_BATTERY_LEVEL_UUID,
  BLE_BATTERY_SERVICE_UUID,
  BleScanFrameAssembler,
  type BleScanFrame,
  type BleScanKind,
  classifyBleScanValue,
  findBleScannerProfile,
  looksLikeBleScanner,
  matchBleScannerProfile,
  normalizeBleUuid,
  parseBatteryLevel,
  type SavedBleScanner,
} from "./ble-scanner-core";

/* ------------------------------------------------------------------ */
/* 蓝牙传输层接口                                                         */
/* ------------------------------------------------------------------ */

export type BleAdapterState = "on" | "off" | "unauthorized" | "unsupported" | "unknown";

export type BleDiscoveredDevice = Readonly<{
  id: string;
  name: string | null;
  rssi: number | null;
  serviceUuids: readonly string[];
}>;

export type BleCharacteristicInfo = Readonly<{
  serviceUuid: string;
  uuid: string;
  notifiable: boolean;
  readable: boolean;
}>;

export interface BleScannerTransport {
  getState(): Promise<BleAdapterState>;
  onStateChange(listener: (state: BleAdapterState) => void): () => void;
  /** 开始扫描附近设备；返回停止函数。 */
  startScan(
    onDevice: (device: BleDiscoveredDevice) => void,
    onError: (error: Error) => void,
  ): () => void;
  /**
   * 连接。**不设超时**：iOS 的 connect 本身不超时。
   * pending=true（断开后挂起等设备回来）：Android 用 autoConnect=true，由系统在设备重新广播时接上；
   * pending=false（用户主动连接 / 刚扫到广播）：Android 直连，比 autoConnect 快得多。
   */
  connect(id: string, options: Readonly<{ pending: boolean }>): Promise<void>;
  cancelConnection(id: string): Promise<void>;
  onDisconnected(id: string, listener: (error: Error | null) => void): () => void;
  discover(id: string): Promise<readonly BleCharacteristicInfo[]>;
  monitor(
    id: string,
    serviceUuid: string,
    characteristicUuid: string,
    onValue: (bytes: Uint8Array) => void,
    onError: (error: Error) => void,
  ): () => void;
  read(id: string, serviceUuid: string, characteristicUuid: string): Promise<Uint8Array>;
  readRssi(id: string): Promise<number | null>;
}

export interface BleScannerStorage {
  load(): Promise<SavedBleScanner | null>;
  save(value: SavedBleScanner | null): Promise<void>;
}

/* ------------------------------------------------------------------ */
/* 状态                                                                 */
/* ------------------------------------------------------------------ */

export type BleScannerConnectionState =
  | "unsupported"
  | "bluetooth-off"
  | "unauthorized"
  /** 没有保存的扫码枪。 */
  | "not-paired"
  /** 已保存但关了自动重连，或用户手动断开。 */
  | "disconnected"
  | "connecting"
  /** 未知型号：已订阅全部 notify 特征，等第一次扫码学会数据通道。 */
  | "learning"
  | "ready"
  /** 长时间没扫码后断开：扫码枪正常休眠，按扳机即可唤醒，不算异常。 */
  | "sleeping"
  | "reconnecting";

export type BleScannerStatus = Readonly<{
  state: BleScannerConnectionState;
  saved: SavedBleScanner | null;
  batteryLevel: number | null;
  rssi: number | null;
  lastScanAt: number | null;
  lastScanValue: string | null;
  /** 需要人处理（重连太久仍未连上 / 电量低）。休眠不算。 */
  attention: boolean;
  lastErrorCode: string | null;
}>;

export type BleScannerScan = Readonly<{
  frame: BleScanFrame;
  kind: BleScanKind;
  characteristicUuid: string;
}>;

export type BleScannerControllerOptions = Readonly<{
  transport: BleScannerTransport;
  storage: BleScannerStorage;
  now?: () => number;
  /** 多久没扫码后断开就算休眠。背夹出厂 1 分钟，实测这把约 5 分钟。 */
  sleepAfterMs?: number;
  /** reconnecting 持续多久仍未连上才标「需处理」。 */
  attentionAfterMs?: number;
  batteryPollMs?: number;
  lowBatteryLevel?: number;
  setTimer?: (callback: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
  setRepeatingTimer?: (callback: () => void, ms: number) => unknown;
  clearRepeatingTimer?: (handle: unknown) => void;
  /** 唤醒扫描每轮扫多久。 */
  wakeScanWindowMs?: number;
  /** 两轮唤醒扫描之间停多久。 */
  wakeScanIntervalMs?: number;
  /** 直连（用户点重连 / 扫到广播）多久没连上就放弃，回到休眠 / 重连中继续唤醒扫描。 */
  directConnectTimeoutMs?: number;
}>;

export const BLE_SLEEP_AFTER_MS = 50_000;
export const BLE_ATTENTION_AFTER_MS = 60_000;
export const BLE_BATTERY_POLL_MS = 5 * 60_000;
export const BLE_LOW_BATTERY_LEVEL = 20;
/**
 * 唤醒扫描：休眠 / 重连中且 App 在前台时，每 8 秒扫 3 秒，扫到本机扫码枪的广播就重新连。
 * 一轮 8 秒是为了避开 Android「30 秒内最多开始 5 次扫描」的系统限制（超了会静默扫不到）。
 */
export const BLE_WAKE_SCAN_WINDOW_MS = 3_000;
export const BLE_WAKE_SCAN_INTERVAL_MS = 5_000;
/** 直连看门狗：iOS 的 connect 不超时，扫码枪睡着时点「重新连接」不能一直卡在「连接中」。 */
export const BLE_DIRECT_CONNECT_TIMEOUT_MS = 15_000;

type Session = {
  id: string;
  generation: number;
  cleanups: (() => void)[];
  assemblers: BleScanFrameAssembler[];
  /** 链路已建立（onConnected 已开始）：唤醒扫描不得再拆这条会话。 */
  linked: boolean;
};

export class BleScannerController {
  private readonly transport: BleScannerTransport;
  private readonly storage: BleScannerStorage;
  private readonly now: () => number;
  private readonly sleepAfterMs: number;
  private readonly attentionAfterMs: number;
  private readonly batteryPollMs: number;
  private readonly lowBatteryLevel: number;
  private readonly setTimer: (callback: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private readonly setRepeatingTimer: (callback: () => void, ms: number) => unknown;
  private readonly clearRepeatingTimer: (handle: unknown) => void;
  private readonly statusListeners = new Set<(status: BleScannerStatus) => void>();
  private readonly scanListeners = new Set<(scan: BleScannerScan) => void>();
  private status: BleScannerStatus;
  private adapterState: BleAdapterState = "unknown";
  private started = false;
  private session: Session | null = null;
  private generation = 0;
  private lastActivityAt: number | null = null;
  private reconnectingSince: number | null = null;
  private attentionTimer: unknown = null;
  private batteryTimer: unknown = null;
  private stopAdapterListener: (() => void) | null = null;
  /** 是否已收到过蓝牙状态事件（收到后以事件为准）。 */
  private adapterEventReceived = false;
  private adapterRetryCount = 0;
  private adapterRetryTimer: unknown = null;
  /** 设置页「搜索扫码枪」的停止函数；非 null 表示搜索进行中。 */
  private stopScan: (() => void) | null = null;
  private readonly wakeScanWindowMs: number;
  private readonly wakeScanIntervalMs: number;
  private readonly directConnectTimeoutMs: number;
  /** App 是否在前台。只有前台才做唤醒扫描（后台扫描耗电，iOS 也没声明蓝牙后台模式）。 */
  private foreground = true;
  /** 唤醒扫描循环是否在运行（扫描中或等下一轮）。 */
  private wakeLoopActive = false;
  private wakeTimer: unknown = null;
  private stopWakeScan: (() => void) | null = null;
  /** 排队执行蓝牙操作，避免搜索、连接、断开交错。 */
  private queue: Promise<unknown> = Promise.resolve();

  constructor(options: BleScannerControllerOptions) {
    this.transport = options.transport;
    this.storage = options.storage;
    this.now = options.now ?? Date.now;
    this.sleepAfterMs = options.sleepAfterMs ?? BLE_SLEEP_AFTER_MS;
    this.attentionAfterMs = options.attentionAfterMs ?? BLE_ATTENTION_AFTER_MS;
    this.batteryPollMs = options.batteryPollMs ?? BLE_BATTERY_POLL_MS;
    this.lowBatteryLevel = options.lowBatteryLevel ?? BLE_LOW_BATTERY_LEVEL;
    this.wakeScanWindowMs = options.wakeScanWindowMs ?? BLE_WAKE_SCAN_WINDOW_MS;
    this.wakeScanIntervalMs = options.wakeScanIntervalMs ?? BLE_WAKE_SCAN_INTERVAL_MS;
    this.directConnectTimeoutMs = options.directConnectTimeoutMs ?? BLE_DIRECT_CONNECT_TIMEOUT_MS;
    this.setTimer = options.setTimer ?? ((callback, ms) => setTimeout(callback, ms));
    this.clearTimer =
      options.clearTimer ?? ((handle) => clearTimeout(handle as ReturnType<typeof setTimeout>));
    this.setRepeatingTimer =
      options.setRepeatingTimer ?? ((callback, ms) => setInterval(callback, ms));
    this.clearRepeatingTimer =
      options.clearRepeatingTimer ??
      ((handle) => clearInterval(handle as ReturnType<typeof setInterval>));
    this.status = Object.freeze({
      state: "not-paired",
      saved: null,
      batteryLevel: null,
      rssi: null,
      lastScanAt: null,
      lastScanValue: null,
      attention: false,
      lastErrorCode: null,
    });
  }

  getStatus(): BleScannerStatus {
    return this.status;
  }

  subscribeStatus(listener: (status: BleScannerStatus) => void): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }

  /** 收到的每一个完整码（含设置码、设备回复，由调用方按 kind 决定去向）。 */
  subscribeScans(listener: (scan: BleScannerScan) => void): () => void {
    this.scanListeners.add(listener);
    return () => this.scanListeners.delete(listener);
  }

  /** App 启动：读保存的扫码枪，蓝牙可用就挂起连接。可重复调用。 */
  async start(): Promise<void> {
    if (this.started) return;
    this.started = true;
    this.stopAdapterListener = this.transport.onStateChange((state) => {
      // 真机实测（2026-11-21 iPad Pro）：状态事件可能比 getState() 的结果先到。
      // 一旦收到过事件，就以事件为准，后到的 getState() 旧值（常是 unknown）不得覆盖它。
      this.adapterEventReceived = true;
      this.handleAdapterState(state);
    });
    const [adapterState, saved] = await Promise.all([
      this.transport.getState().catch((): BleAdapterState => "unknown"),
      this.storage.load().catch(() => null),
    ]);
    // 读存储期间用户可能已经配对了新扫码枪：内存里已有的配置比启动时读到的旧值新，不能被覆盖。
    if (!this.status.saved && saved) this.update({ saved });
    this.handleAdapterState(this.adapterEventReceived ? this.adapterState : adapterState);
  }

  /**
   * App 回到前台。休眠 / 重连中：立即补一轮唤醒扫描，**不**再对同一设备叠加连接请求
   * （iOS ble-plx 对同一设备重复 connect 会把前一个请求连带取消，新请求可能永远等不到回调）。
   * 没有挂着的请求（如之前被 stop 断开过）才重新发起连接。
   */
  resume(): void {
    this.foreground = true;
    const saved = this.status.saved;
    if (!this.started || !saved?.autoReconnect || this.adapterState !== "on") return;
    const state = this.status.state;
    if (state === "ready" || state === "connecting" || state === "learning") return;
    if ((state === "sleeping" || state === "reconnecting") && this.session) {
      this.syncWakeScan({ immediate: true });
      return;
    }
    void this.enqueue(() => this.connectSaved(true));
  }

  /** App 进后台：停止唤醒扫描（挂着的连接请求保留，回前台再继续扫）。 */
  pause(): void {
    this.foreground = false;
    this.syncWakeScan();
  }

  dispose(): void {
    this.started = false;
    this.stopWakeLoop();
    this.stopDiscovery();
    this.stopAdapterListener?.();
    this.stopAdapterListener = null;
    this.teardownSession();
    this.clearAttentionTimer();
    if (this.adapterRetryTimer !== null) {
      this.clearTimer(this.adapterRetryTimer);
      this.adapterRetryTimer = null;
    }
  }

  /* ---------------- 搜索 ---------------- */

  startDiscovery(
    onDevices: (devices: readonly BleDiscoveredDevice[]) => void,
    options: Readonly<{ excludeIds?: readonly string[] }> = {},
  ): () => void {
    this.stopDiscovery();
    // ble-plx 同一时刻只能有一个扫描，且停止扫描是全局的：先让唤醒扫描彻底退出，再开始搜索。
    this.stopWakeLoop();
    const exclude = new Set(options.excludeIds ?? []);
    const found = new Map<string, BleDiscoveredDevice>();
    const emit = () => {
      const devices = [...found.values()].sort(compareDiscovered);
      onDevices(devices);
    };
    const stop = this.transport.startScan(
      (device) => {
        if (exclude.has(device.id)) return;
        if (!device.name && device.serviceUuids.length === 0) return;
        found.set(device.id, device);
        emit();
      },
      (error) => {
        this.update({ lastErrorCode: errorCode(error, "scan-failed") });
      },
    );
    this.stopScan = stop;
    return () => {
      if (this.stopScan === stop) this.stopDiscovery();
    };
  }

  stopDiscovery(): void {
    const stop = this.stopScan;
    this.stopScan = null;
    stop?.();
    // 搜索结束：需要的话恢复唤醒扫描（下一轮按间隔开始）。
    if (stop) this.syncWakeScan();
  }

  /* ---------------- 配对 / 断开 ---------------- */

  /** 连接搜索列表里的设备并保存为本机扫码枪（替换已保存的那把）。 */
  pair(device: BleDiscoveredDevice): Promise<void> {
    this.stopDiscovery();
    return this.enqueue(async () => {
      const previous = this.status.saved;
      if (previous && previous.peripheralId !== device.id) {
        this.teardownSession();
        await this.transport.cancelConnection(previous.peripheralId).catch(() => undefined);
      }
      const profile = matchBleScannerProfile(device.serviceUuids, device.name);
      const saved: SavedBleScanner = Object.freeze({
        version: 1,
        peripheralId: device.id,
        name: device.name ?? "",
        alias: previous?.peripheralId === device.id ? previous.alias : null,
        profileId: profile?.id ?? null,
        serviceUuid: profile?.serviceUuid ?? "",
        notifyUuid: profile?.notifyUuid ?? "",
        autoReconnect: true,
        savedAt: new Date(this.now()).toISOString(),
      });
      // 未知型号先存空通道，嗅探学会后再补；空通道不会通过 parseSavedBleScanner，
      // 所以学会之前不落盘，避免存下不可用的配置。
      this.update({ saved, lastErrorCode: null });
      if (saved.serviceUuid) await this.storage.save(saved);
      await this.connectWhenAdapterReady();
    });
  }

  /** 忘记这把扫码枪：断开、清存储。 */
  forget(): Promise<void> {
    return this.enqueue(async () => {
      const saved = this.status.saved;
      this.teardownSession();
      this.clearAttentionTimer();
      if (saved) await this.transport.cancelConnection(saved.peripheralId).catch(() => undefined);
      await this.storage.save(null);
      this.update({
        saved: null,
        state: this.baseStateWithoutDevice(),
        batteryLevel: null,
        rssi: null,
        lastScanAt: null,
        lastScanValue: null,
        lastErrorCode: null,
      });
    });
  }

  /** 手动断开：保留配置，但本次不再自动重连，直到用户点「重新连接」。 */
  disconnect(): Promise<void> {
    return this.enqueue(async () => {
      const saved = this.status.saved;
      if (!saved) return;
      this.teardownSession();
      this.clearAttentionTimer();
      await this.transport.cancelConnection(saved.peripheralId).catch(() => undefined);
      this.update({ state: "disconnected" });
    });
  }

  /** 用户点「重新连接」：先取消挂着的旧请求 / 旧链路，再重新连，保证同一设备只有一个连接请求。 */
  reconnect(): Promise<void> {
    return this.enqueue(async () => {
      const session = this.session;
      if (session) {
        // 先作废会话：旧请求被取消时的失败回调不会再触发重连。
        this.teardownSession();
        await this.transport.cancelConnection(session.id).catch(() => undefined);
      }
      await this.connectWhenAdapterReady();
    });
  }

  setAlias(alias: string | null): Promise<void> {
    return this.enqueue(async () => {
      const saved = this.status.saved;
      if (!saved) return;
      const next = Object.freeze({ ...saved, alias });
      this.update({ saved: next });
      if (next.serviceUuid) await this.storage.save(next);
    });
  }

  setAutoReconnect(autoReconnect: boolean): Promise<void> {
    return this.enqueue(async () => {
      const saved = this.status.saved;
      if (!saved) return;
      const next = Object.freeze({ ...saved, autoReconnect });
      this.update({ saved: next });
      if (next.serviceUuid) await this.storage.save(next);
    });
  }

  /* ---------------- 内部：连接流程 ---------------- */

  /**
   * 用户主动配对 / 重新连接时：本地记录的蓝牙状态不是 on 就先重新问一次，
   * 不能因为启动时拿到的 unknown 而「只保存不连接」（真机上表现为保存后仍显示未连接）。
   */
  private async connectWhenAdapterReady(): Promise<void> {
    if (this.adapterState !== "on") {
      const fresh = await this.transport.getState().catch((): BleAdapterState => "unknown");
      if (fresh !== "unknown") this.adapterState = fresh;
    }
    if (this.adapterState === "on") {
      await this.connectSaved(false);
      return;
    }
    // 仍未就绪：显示对应状态；之后的状态事件（on）会自动挂起连接。
    this.handleAdapterState(this.adapterState);
  }

  private handleAdapterState(state: BleAdapterState): void {
    this.adapterState = state;
    if (state === "unknown") {
      // unknown 是 iOS 蓝牙管理器初始化中的过渡态，不是故障：不拆会话，等下一个状态事件；
      // 同时补问几次，防止事件已经错过、状态永远停在 unknown。
      if (!this.session) {
        this.update({ state: this.status.saved ? "connecting" : "not-paired" });
      }
      this.scheduleAdapterStateRetry();
      return;
    }
    this.adapterRetryCount = 0;
    if (state === "on") {
      if (!this.status.saved) {
        this.update({ state: "not-paired" });
        return;
      }
      if (this.session) return;
      if (this.status.saved.autoReconnect) {
        void this.enqueue(() => this.connectSaved(true));
      } else {
        this.update({ state: "disconnected" });
      }
      return;
    }
    // 蓝牙关闭 / 未授权：丢掉会话，等恢复后再连。
    this.teardownSession();
    this.update({
      state:
        state === "off"
          ? "bluetooth-off"
          : state === "unauthorized"
            ? "unauthorized"
            : state === "unsupported"
              ? "unsupported"
              : this.status.saved
                ? "disconnected"
                : "not-paired",
    });
  }

  /** 蓝牙状态停在 unknown 时每秒补问一次，最多 5 次。 */
  private scheduleAdapterStateRetry(): void {
    if (!this.started || this.adapterRetryTimer !== null || this.adapterRetryCount >= 5) return;
    this.adapterRetryCount += 1;
    this.adapterRetryTimer = this.setTimer(() => {
      this.adapterRetryTimer = null;
      if (!this.started || this.adapterState !== "unknown") return;
      void this.transport.getState().then(
        (state) => {
          if (this.started && this.adapterState === "unknown") this.handleAdapterState(state);
        },
        () => this.scheduleAdapterStateRetry(),
      );
    }, 1000);
  }

  /**
   * 发起连接。pending=true：断开后挂起等设备回来（状态保持休眠 / 重连中）；
   * pending=false：用户主动连接或刚扫到广播，显示「连接中」并直连。
   */
  private async connectSaved(pending: boolean): Promise<void> {
    const saved = this.status.saved;
    if (!saved || this.adapterState !== "on") return;
    this.teardownSession();
    const session: Session = {
      id: saved.peripheralId,
      generation: ++this.generation,
      cleanups: [],
      assemblers: [],
      linked: false,
    };
    this.session = session;
    if (!pending || this.status.state === "not-paired" || this.status.state === "disconnected") {
      this.update({ state: "connecting" });
    }
    if (!pending) {
      const watchdog = this.setTimer(() => {
        void this.enqueue(() => this.abandonDirectConnect(session));
      }, this.directConnectTimeoutMs);
      session.cleanups.push(() => this.clearTimer(watchdog));
    }
    const connecting = this.transport.connect(saved.peripheralId, { pending });
    // 挂起的连接可能很久才完成；不 await 在队列里，避免堵住后续操作。
    void connecting.then(
      () => this.onConnected(session),
      (error: unknown) => {
        if (!this.isCurrent(session)) return;
        this.update({ lastErrorCode: errorCode(error, "connect-failed") });
        this.onLinkLost(session);
      },
    );
  }

  private async onConnected(session: Session): Promise<void> {
    if (!this.isCurrent(session)) return;
    session.linked = true;
    // 链路已建立：唤醒扫描立即停（不等 discover 完成、状态变成 ready）。
    this.syncWakeScan();
    this.reconnectingSince = null;
    this.clearAttentionTimer();
    session.cleanups.push(
      this.transport.onDisconnected(session.id, () => this.onLinkLost(session)),
    );
    let characteristics: readonly BleCharacteristicInfo[];
    try {
      characteristics = await this.transport.discover(session.id);
    } catch (error) {
      if (!this.isCurrent(session)) return;
      this.update({ lastErrorCode: errorCode(error, "discover-failed") });
      this.onLinkLost(session);
      return;
    }
    if (!this.isCurrent(session)) return;
    const saved = this.status.saved;
    if (!saved) return;
    const channel = this.pickChannel(saved, characteristics);
    if (channel.kind === "none") {
      this.update({ state: "reconnecting", lastErrorCode: "no-notify-characteristic" });
      return;
    }
    for (const candidate of channel.candidates) {
      this.subscribe(session, candidate, channel.kind === "learning");
    }
    this.lastActivityAt = this.now();
    this.update({
      state: channel.kind === "learning" ? "learning" : "ready",
      lastErrorCode: null,
    });
    const battery = characteristics.find(
      (item) =>
        normalizeBleUuid(item.serviceUuid) === BLE_BATTERY_SERVICE_UUID &&
        normalizeBleUuid(item.uuid) === BLE_BATTERY_LEVEL_UUID &&
        item.readable,
    );
    if (battery) {
      // 背夹的电量特征订阅会失败（没有 CCCD），统一定时读。
      void this.readBattery(session);
      const handle = this.setRepeatingTimer(() => {
        void this.readBattery(session);
      }, this.batteryPollMs);
      session.cleanups.push(() => this.clearRepeatingTimer(handle));
    }
    void this.transport.readRssi(session.id).then(
      (rssi) => {
        if (this.isCurrent(session)) this.update({ rssi });
      },
      () => undefined,
    );
  }

  private pickChannel(
    saved: SavedBleScanner,
    characteristics: readonly BleCharacteristicInfo[],
  ):
    | Readonly<{ kind: "known"; candidates: readonly BleCharacteristicInfo[] }>
    | Readonly<{ kind: "learning"; candidates: readonly BleCharacteristicInfo[] }>
    | Readonly<{ kind: "none" }> {
    if (saved.serviceUuid && saved.notifyUuid) {
      const known = characteristics.find(
        (item) =>
          normalizeBleUuid(item.serviceUuid) === saved.serviceUuid &&
          normalizeBleUuid(item.uuid) === saved.notifyUuid &&
          item.notifiable,
      );
      if (known) return { kind: "known", candidates: [known] };
    }
    // 设备连上后才看到服务的，再按服务识别一次型号。
    const profile =
      findBleScannerProfile(saved.profileId) ??
      matchBleScannerProfile(
        characteristics.map((item) => item.serviceUuid),
        saved.name,
      );
    if (profile) {
      const byProfile = characteristics.find(
        (item) =>
          normalizeBleUuid(item.serviceUuid) === profile.serviceUuid &&
          normalizeBleUuid(item.uuid) === profile.notifyUuid &&
          item.notifiable,
      );
      if (byProfile) return { kind: "known", candidates: [byProfile] };
    }
    const candidates = characteristics.filter(
      (item) =>
        item.notifiable &&
        // 电池通道不可能是条码。
        normalizeBleUuid(item.serviceUuid) !== BLE_BATTERY_SERVICE_UUID,
    );
    return candidates.length > 0 ? { kind: "learning", candidates } : { kind: "none" };
  }

  private subscribe(session: Session, characteristic: BleCharacteristicInfo, learning: boolean): void {
    const assembler = new BleScanFrameAssembler({
      now: this.now,
      onFrame: (frame) => {
        if (!this.isCurrent(session)) return;
        void this.handleFrame(session, characteristic, frame, learning);
      },
    });
    session.assemblers.push(assembler);
    const stop = this.transport.monitor(
      session.id,
      characteristic.serviceUuid,
      characteristic.uuid,
      (bytes) => {
        if (!this.isCurrent(session)) return;
        assembler.push(bytes, this.now());
      },
      (error) => {
        // 个别特征订阅失败（如没有 CCCD）不影响其他候选。
        if (!this.isCurrent(session)) return;
        if (!learning) this.update({ lastErrorCode: errorCode(error, "monitor-failed") });
      },
    );
    session.cleanups.push(stop);
  }

  private async handleFrame(
    session: Session,
    characteristic: BleCharacteristicInfo,
    frame: BleScanFrame,
    learning: boolean,
  ): Promise<void> {
    const kind = classifyBleScanValue(frame.value);
    if (learning && this.status.state !== "learning") {
      // 已学会数据通道：其余候选特征的数据一律忽略（本次会话结束后不再订阅它们）。
      const learned = this.status.saved?.notifyUuid;
      if (learned !== normalizeBleUuid(characteristic.uuid)) return;
    }
    this.lastActivityAt = frame.receivedAt;
    if (learning && this.status.state === "learning") {
      // 第一次收到像条码的数据的那个特征就是数据通道，写进配置，下次直接订阅它。
      const saved = this.status.saved;
      if (saved) {
        const next: SavedBleScanner = Object.freeze({
          ...saved,
          serviceUuid: normalizeBleUuid(characteristic.serviceUuid),
          notifyUuid: normalizeBleUuid(characteristic.uuid),
        });
        this.update({ saved: next, state: "ready" });
        await this.storage.save(next).catch(() => undefined);
      }
    }
    this.update({
      lastScanAt: frame.receivedAt,
      lastScanValue: kind === "barcode" ? frame.value : this.status.lastScanValue,
    });
    const scan: BleScannerScan = Object.freeze({
      frame,
      kind,
      characteristicUuid: normalizeBleUuid(characteristic.uuid),
    });
    this.scanListeners.forEach((listener) => listener(scan));
  }

  private async readBattery(session: Session): Promise<void> {
    try {
      const bytes = await this.transport.read(
        session.id,
        BLE_BATTERY_SERVICE_UUID,
        BLE_BATTERY_LEVEL_UUID,
      );
      if (!this.isCurrent(session)) return;
      const level = parseBatteryLevel(bytes);
      if (level !== null) this.update({ batteryLevel: level });
    } catch {
      // 读电量失败不影响扫码。
    }
  }

  /** 直连超时：先作废会话再取消请求（取消引起的失败回调不再触发重连），然后按断开处理。 */
  private async abandonDirectConnect(session: Session): Promise<void> {
    if (!this.isCurrent(session) || session.linked) return;
    this.teardownSession();
    await this.transport.cancelConnection(session.id).catch(() => undefined);
    if (this.session) return; // 取消期间已有新的连接流程接手
    this.update({ lastErrorCode: "connect-timeout" });
    this.enterReconnectWait();
  }

  /** 链路断开：判断是休眠还是异常，然后挂起新的连接请求。 */
  private onLinkLost(session: Session): void {
    if (!this.isCurrent(session)) return;
    this.teardownSession();
    this.enterReconnectWait();
  }

  /** 会话已拆除后：进入休眠 / 重连中，挂起新的连接请求并启用唤醒扫描。 */
  private enterReconnectWait(): void {
    const saved = this.status.saved;
    if (!saved || this.adapterState !== "on") return;
    if (!saved.autoReconnect) {
      this.update({ state: "disconnected" });
      return;
    }
    const idleFor = this.lastActivityAt === null ? 0 : this.now() - this.lastActivityAt;
    const sleeping = idleFor >= this.sleepAfterMs;
    this.update({ state: sleeping ? "sleeping" : "reconnecting" });
    if (!sleeping) this.startAttentionTimer();
    // 不排队：断开回调可能发生在队列任务内部。
    // 挂起请求是系统级的兜底（部分设备 / 系统版本上它能自己接上）；
    // 真正可靠的路径是下面的唤醒扫描：扫到广播后取消它、重新直连。
    void this.connectSaved(true);
  }

  /* ---------------- 内部：唤醒扫描 ---------------- */

  /** 现在是否应该做唤醒扫描。 */
  private shouldWakeScan(): boolean {
    const saved = this.status.saved;
    const state = this.status.state;
    return (
      this.started &&
      this.foreground &&
      this.adapterState === "on" &&
      Boolean(saved?.autoReconnect) &&
      (state === "sleeping" || state === "reconnecting") &&
      // 与设置页搜索互斥：搜索期间不扫。
      this.stopScan === null &&
      // 链路已建立（正在 discover）时不扫，免得拆掉刚连上的会话。
      !this.session?.linked
    );
  }

  /**
   * 按当前条件启停唤醒扫描循环。状态、前台、搜索、会话变化后都调用一次。
   * immediate：回前台时立即扫一轮，不等间隔。
   */
  private syncWakeScan(options: Readonly<{ immediate?: boolean }> = {}): void {
    if (!this.shouldWakeScan()) {
      this.stopWakeLoop();
      return;
    }
    if (options.immediate) {
      if (this.stopWakeScan) return; // 正在扫，不用重开
      this.clearWakeTimer();
      this.wakeLoopActive = true;
      this.runWakeScanWindow();
      return;
    }
    if (this.wakeLoopActive) return;
    this.wakeLoopActive = true;
    // 刚断开时扫码枪多半刚睡着，不急着扫：等一个间隔再开始第一轮。
    this.scheduleNextWakeScan();
  }

  private scheduleNextWakeScan(): void {
    this.clearWakeTimer();
    this.wakeTimer = this.setTimer(() => {
      this.wakeTimer = null;
      if (!this.wakeLoopActive) return;
      if (!this.shouldWakeScan()) {
        this.stopWakeLoop();
        return;
      }
      this.runWakeScanWindow();
    }, this.wakeScanIntervalMs);
  }

  /** 扫一轮：只认已保存的 peripheralId；扫到就停扫并排队重连，扫不到窗口结束后等下一轮。 */
  private runWakeScanWindow(): void {
    const saved = this.status.saved;
    if (!saved) return;
    const targetId = saved.peripheralId;
    let finished = false;
    let stop: (() => void) | undefined;
    const finish = () => {
      if (finished) return;
      finished = true;
      this.endWakeScanWindow(stop);
    };
    stop = this.transport.startScan(
      (device) => {
        if (finished || device.id !== targetId) return;
        finish();
        this.wakeLoopActive = false;
        this.clearWakeTimer();
        void this.enqueue(() => this.reconnectAfterWake(targetId));
      },
      () => {
        // 扫描出错（权限、蓝牙忙等）：结束本轮，按间隔再试。
        if (finished) return;
        finish();
        if (this.wakeLoopActive) this.scheduleNextWakeScan();
      },
    );
    if (finished) {
      // 回调同步触发时 stop 还没赋值，这里补停一次。
      this.endWakeScanWindow(stop);
      return;
    }
    this.stopWakeScan = finish;
    this.clearWakeTimer();
    this.wakeTimer = this.setTimer(() => {
      this.wakeTimer = null;
      finish();
      if (this.wakeLoopActive) this.scheduleNextWakeScan();
    }, this.wakeScanWindowMs);
  }

  private endWakeScanWindow(stop: (() => void) | undefined): void {
    if (this.stopWakeScan) this.stopWakeScan = null;
    // 搜索已经接管了扫描时不能再调停止（ble-plx 的停止是全局的，会把搜索也停掉）。
    if (this.stopScan === null) stop?.();
  }

  private stopWakeLoop(): void {
    this.wakeLoopActive = false;
    this.clearWakeTimer();
    const stop = this.stopWakeScan;
    this.stopWakeScan = null;
    stop?.();
  }

  private clearWakeTimer(): void {
    if (this.wakeTimer !== null) {
      this.clearTimer(this.wakeTimer);
      this.wakeTimer = null;
    }
  }

  /**
   * 扫到了本机扫码枪的广播（它醒着）：先作废并取消挂着的旧请求，再直连。
   * 保证同一设备同时只有一个连接请求。
   */
  private async reconnectAfterWake(id: string): Promise<void> {
    const saved = this.status.saved;
    const state = this.status.state;
    if (!this.started || !saved || saved.peripheralId !== id || this.adapterState !== "on") return;
    if (state !== "sleeping" && state !== "reconnecting") return;
    if (this.session?.linked) return;
    // 先切到「连接中」：唤醒扫描据此不会再启动；再作废旧会话，旧请求被取消时的失败回调就不会再触发重连。
    this.update({ state: "connecting" });
    this.teardownSession();
    await this.transport.cancelConnection(id).catch(() => undefined);
    if (this.session || this.status.saved?.peripheralId !== id || this.status.state !== "connecting") return;
    await this.connectSaved(false);
  }

  private startAttentionTimer(): void {
    if (this.reconnectingSince === null) this.reconnectingSince = this.now();
    this.clearAttentionTimer();
    this.attentionTimer = this.setTimer(() => {
      this.attentionTimer = null;
      if (this.status.state === "reconnecting") this.update({});
    }, this.attentionAfterMs);
  }

  private clearAttentionTimer(): void {
    if (this.attentionTimer !== null) {
      this.clearTimer(this.attentionTimer);
      this.attentionTimer = null;
    }
  }

  private teardownSession(): void {
    const session = this.session;
    this.session = null;
    if (!session) return;
    for (const cleanup of session.cleanups.splice(0)) {
      try {
        cleanup();
      } catch {
        // 清理失败不影响后续流程。
      }
    }
    session.assemblers.forEach((assembler) => assembler.dispose());
  }

  private isCurrent(session: Session): boolean {
    return this.session === session && session.generation === this.generation;
  }

  private baseStateWithoutDevice(): BleScannerConnectionState {
    if (this.adapterState === "off") return "bluetooth-off";
    if (this.adapterState === "unauthorized") return "unauthorized";
    if (this.adapterState === "unsupported") return "unsupported";
    return "not-paired";
  }

  private enqueue<T>(task: () => Promise<T>): Promise<T> {
    const run = this.queue.then(task, task);
    this.queue = run.catch(() => undefined);
    return run;
  }

  private update(patch: Partial<Omit<BleScannerStatus, "attention">>): void {
    const next = { ...this.status, ...patch };
    const reconnectingTooLong =
      next.state === "reconnecting" &&
      this.reconnectingSince !== null &&
      this.now() - this.reconnectingSince >= this.attentionAfterMs;
    if (next.state !== "reconnecting") this.reconnectingSince = null;
    const lowBattery =
      next.batteryLevel !== null &&
      next.batteryLevel < this.lowBatteryLevel &&
      next.saved !== null;
    const status: BleScannerStatus = Object.freeze({
      ...next,
      attention: reconnectingTooLong || lowBattery,
    });
    if (shallowEqualStatus(status, this.status)) return;
    this.status = status;
    this.statusListeners.forEach((listener) => listener(status));
    // 进入 / 离开休眠、重连中时启停唤醒扫描。
    this.syncWakeScan();
  }
}

function shallowEqualStatus(left: BleScannerStatus, right: BleScannerStatus): boolean {
  return (
    left.state === right.state &&
    left.saved === right.saved &&
    left.batteryLevel === right.batteryLevel &&
    left.rssi === right.rssi &&
    left.lastScanAt === right.lastScanAt &&
    left.lastScanValue === right.lastScanValue &&
    left.attention === right.attention &&
    left.lastErrorCode === right.lastErrorCode
  );
}

function compareDiscovered(left: BleDiscoveredDevice, right: BleDiscoveredDevice): number {
  const leftScanner = looksLikeBleScanner(left.serviceUuids, left.name) ? 0 : 1;
  const rightScanner = looksLikeBleScanner(right.serviceUuids, right.name) ? 0 : 1;
  if (leftScanner !== rightScanner) return leftScanner - rightScanner;
  return (right.rssi ?? -999) - (left.rssi ?? -999);
}

function errorCode(error: unknown, fallback: string): string {
  if (error && typeof error === "object" && "code" in error) {
    const code = (error as { code: unknown }).code;
    if (typeof code === "string" || typeof code === "number") return `${fallback}:${code}`;
  }
  return fallback;
}
