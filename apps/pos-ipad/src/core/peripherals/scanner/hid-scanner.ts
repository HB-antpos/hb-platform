import type {
  ScanEvent,
  ScannerContext,
  ScannerPort,
  ScanSource,
} from "../../contracts/scanner";

import { CrossSourceScanDeduper } from "./ble/ble-scanner-core";
import type {
  RoutedScanEvent,
  ScannerCaptureContext,
  ScannerCaptureStatus,
  ScannerScanCategory,
  TextInputCaptureResult,
} from "./types";

export type HidScannerRouterOptions = {
  idleMs?: number;
  maxLength?: number;
  now?: () => number;
  onDiagnostic?: (diagnostic: "idle-reset" | "max-length") => void;
};

const DEFAULT_IDLE_MS = 80;
const DEFAULT_MAX_LENGTH = 256;

/**
 * iPad 只可在当前 TextInput 焦点接收 HID 键盘输入。本路由器不尝试安装全局键盘 hook，
 * 上下文、弹窗和失焦都会清空半段数据，避免条码跨界拼接。
 */
export class HidScannerRouter implements ScannerPort {
  private readonly idleMs: number;
  private readonly maxLength: number;
  private readonly now: () => number;
  private readonly onDiagnostic?: HidScannerRouterOptions["onDiagnostic"];
  private readonly scannerListeners = new Set<(event: ScanEvent) => void>();
  private readonly routedListeners = new Set<(event: RoutedScanEvent) => void>();
  private readonly contextStack: {
    context: ScannerCaptureContext;
    leaseId: number | null;
    /** 只由 captureDeviceActivation 登记：BLE 注册码的私有接收器。 */
    activationSink?: (raw: string) => void;
  }[] = [{ context: "product", leaseId: null }];
  private nextContextLeaseId = 1;
  private buffer = "";
  private textInputValue = "";
  private lastInputAt: number | null = null;
  private captureActive = false;
  private cameraActive = false;
  /** 同一个码 300ms 内从不同来源（hid / camera / ble）各来一次只投递一次。 */
  private readonly crossSourceDeduper = new CrossSourceScanDeduper();

  constructor(options: HidScannerRouterOptions = {}) {
    this.idleMs = options.idleMs ?? DEFAULT_IDLE_MS;
    this.maxLength = options.maxLength ?? DEFAULT_MAX_LENGTH;
    this.now = options.now ?? Date.now;
    this.onDiagnostic = options.onDiagnostic;
  }

  setContext(context: ScannerContext): void {
    this.replaceContext(context);
  }

  setCaptureContext(context: ScannerCaptureContext): void {
    this.replaceContext(context);
  }

  pushContext(context: ScannerCaptureContext): void {
    this.resetPartial();
    this.contextStack.push({ context, leaseId: null });
  }

  popContext(): void {
    if (this.contextStack.length > 1) {
      this.contextStack.pop();
    }
    this.resetPartial();
  }

  /**
   * 路由和全局弹窗的 effect 清理顺序并不保证后进先出。
   * lease 按自身身份释放，避免页面卸载时误弹出仍在显示的授权弹窗 context。
   */
  acquireContext(context: ScannerCaptureContext): () => void {
    const leaseId = this.nextContextLeaseId;
    this.nextContextLeaseId += 1;
    this.resetPartial();
    this.contextStack.push({ context, leaseId });
    let released = false;
    return () => {
      if (released) return;
      released = true;
      const index = this.contextStack.findIndex(
        (entry) => entry.leaseId === leaseId,
      );
      if (index >= 0) {
        this.contextStack.splice(index, 1);
        this.resetPartial();
      }
    };
  }

  /**
   * 设备注册页专用：申请 device-activation 租约，并登记一个私有接收器收 BLE 扫码枪的注册码。
   *
   * 中文注释：
   * 1. 注册码是设备秘密，emit() 在 device-activation 场景下故意不广播给任何订阅者；
   *    BLE 扫码因此只能像相机弹窗的直接回调一样，经这里交给持有租约的注册页本身。
   * 2. 只有栈顶正是本租约时才投递；相机弹窗、扫码测试等后申请的租约压在上面时一律让给它们。
   * 3. 交出 BLE 原文（只去回车换行，不做 Unicode trim），由注册码解析器先拒非 ASCII 再规范化。
   */
  captureDeviceActivation(onValue: (raw: string) => void): () => void {
    const release = this.acquireContext("device-activation");
    const entry = this.contextStack.at(-1);
    if (entry) entry.activationSink = onValue;
    return () => {
      if (entry) delete entry.activationSink;
      release();
    };
  }

  setCaptureActive(active: boolean): void {
    if (this.captureActive === active) {
      return;
    }
    this.captureActive = active;
    this.resetPartial();
  }

  getCaptureStatus(): ScannerCaptureStatus {
    if (!this.captureActive) {
      return "inactive";
    }
    return this.cameraActive ? "camera" : "capturing";
  }

  acceptHidText(fragment: string, receivedAt = this.now()): TextInputCaptureResult {
    if (!this.captureActive) {
      return { valueToRender: "", emitted: false, overflowed: false };
    }
    this.resetIfStale(receivedAt);
    return this.appendFragment(fragment, "hid", receivedAt);
  }

  acceptTextInputValue(nextValue: string, receivedAt = this.now()): TextInputCaptureResult {
    if (!this.captureActive) {
      this.textInputValue = "";
      return { valueToRender: "", emitted: false, overflowed: false };
    }
    this.resetIfStale(receivedAt);
    const fragment = nextValue.startsWith(this.textInputValue)
      ? nextValue.slice(this.textInputValue.length)
      : nextValue;
    this.textInputValue = nextValue;
    const result = this.appendFragment(fragment, "hid", receivedAt);
    if (result.emitted || result.overflowed) {
      this.textInputValue = "";
    }
    return { ...result, valueToRender: this.textInputValue };
  }

  submitTextInput(value: string, receivedAt = this.now()): TextInputCaptureResult {
    const result = this.acceptTextInputValue(value, receivedAt);
    if (!this.captureActive || result.emitted || result.overflowed) {
      return result;
    }
    const emitted = this.flush("hid", receivedAt);
    this.textInputValue = "";
    return { valueToRender: "", emitted, overflowed: false };
  }

  resetPartialIfIdle(receivedAt = this.now()): boolean {
    if (!this.buffer || this.lastInputAt === null || receivedAt - this.lastInputAt <= this.idleMs) {
      return false;
    }
    this.resetPartial();
    this.onDiagnostic?.("idle-reset");
    return true;
  }

  async startCamera(): Promise<void> {
    this.cameraActive = true;
    this.resetPartial();
  }

  async stopCamera(): Promise<void> {
    this.cameraActive = false;
  }

  acceptCameraText(value: string, receivedAt = this.now()): boolean {
    if (!this.cameraActive) {
      return false;
    }
    const normalized = normalizeScanValue(value);
    if (!normalized) {
      return false;
    }
    return this.emit(normalized, "camera", receivedAt);
  }

  /**
   * BLE 数据模式扫码枪交来的整码（已由控制器拼帧、分类为 barcode）。
   * 与 HID 不同，它不依赖隐藏输入框焦点，所以必须靠「场景租约」判断有没有页面在收：
   * 只有栈顶是某个页面 / 弹窗 acquireContext 拿到的租约时才投递；
   * 栈顶是没人占用的默认 product（付款页、普通表单等）时返回 false，由调用方轻提示后丢弃。
   * HBDEV1- 开通码隔离、device-activation 静默与 HID / 相机共用 emit。
   */
  acceptBleText(value: string, receivedAt = this.now()): boolean {
    const top = this.contextStack.at(-1);
    if (!top || top.leaseId === null) {
      return false;
    }
    if (top.context === "device-activation" && top.activationSink) {
      // 注册页持有租约：原文直接交给它，不进 emit / 任何共享订阅。
      const raw = value.replace(/[\r\n]/g, "");
      if (!raw.trim()) return false;
      top.activationSink(raw);
      return true;
    }
    const normalized = normalizeScanValue(value);
    if (!normalized) {
      return false;
    }
    return this.emit(normalized, "ble", receivedAt);
  }

  subscribe(listener: (event: ScanEvent) => void): () => void {
    this.scannerListeners.add(listener);
    return () => this.scannerListeners.delete(listener);
  }

  subscribeRouted(listener: (event: RoutedScanEvent) => void): () => void {
    this.routedListeners.add(listener);
    return () => this.routedListeners.delete(listener);
  }

  private replaceContext(context: ScannerCaptureContext): void {
    this.contextStack.splice(0, this.contextStack.length, {
      context,
      leaseId: null,
    });
    this.resetPartial();
  }

  private appendFragment(
    fragment: string,
    source: "hid",
    receivedAt: number,
  ): TextInputCaptureResult {
    let emitted = false;
    let overflowed = false;
    for (const character of fragment) {
      if (isTerminator(character)) {
        emitted = this.flush(source, receivedAt) || emitted;
        continue;
      }
      if (!isPrintable(character)) {
        continue;
      }
      if (this.buffer.length >= this.maxLength) {
        // 本片段余下内容一概丢弃；下次输入从全新缓冲开始，禁止残码污染。
        this.resetPartial();
        this.onDiagnostic?.("max-length");
        overflowed = true;
        break;
      }
      this.buffer += character;
      this.lastInputAt = receivedAt;
    }
    return { valueToRender: this.textInputValue, emitted, overflowed };
  }

  private flush(source: "hid", receivedAt: number): boolean {
    const value = normalizeScanValue(this.buffer);
    this.resetPartial();
    if (!value) {
      return false;
    }
    return this.emit(value, source, receivedAt);
  }

  private resetIfStale(receivedAt: number): void {
    this.resetPartialIfIdle(receivedAt);
  }

  private resetPartial(): void {
    this.buffer = "";
    this.textInputValue = "";
    this.lastInputAt = null;
  }

  private emit(value: string, source: ScanSource, receivedAt: number): boolean {
    const context = this.currentContext();
    if (context === "device-activation") {
      // 开通码只交给相机弹窗的直接回调或当前输入框，禁止进入任何共享扫码订阅。
      return true;
    }
    if (hasDeviceActivationPrefix(value)) {
      // 即使未进入专用上下文，也绝不能把疑似设备秘密当商品或员工码广播。
      return false;
    }
    if (
      context !== "scanner-test" &&
      !this.crossSourceDeduper.accept(value, source, receivedAt)
    ) {
      // 同码已从另一来源投递过（例如同一把枪键盘模式和 BLE 模式都开着）：
      // 视为已处理，返回 true，避免相机等调用方把它当成「未被接收」。
      // 扫码测试不去重：测试面板要把「两种模式同时开着」的重复如实显示出来。
      return true;
    }
    const event: RoutedScanEvent = {
      value,
      source,
      context,
      category: categoryForContext(context),
      receivedAtIso: new Date(receivedAt).toISOString(),
    };
    this.routedListeners.forEach((listener) => listener(event));
    // 紧急 QR 与扫码测试只走 routed 订阅，不广播给通用 ScannerPort 订阅者。
    if (context !== "emergency-qr" && context !== "scanner-test") {
      const scannerEvent: ScanEvent = {
        value: event.value,
        source: event.source,
        context,
        receivedAtIso: event.receivedAtIso,
      };
      this.scannerListeners.forEach((listener) => listener(scannerEvent));
    }
    return true;
  }

  private currentContext(): ScannerCaptureContext {
    return this.contextStack.at(-1)?.context ?? "product";
  }
}

export function normalizeScanValue(value: string): string {
  return value.replace(/[\r\n]/g, "").trim();
}

/** 测试面板等只读展示用：疑似设备开通码（HBDEV1-）要屏蔽显示。 */
export function isDeviceActivationScanValue(value: string): boolean {
  return hasDeviceActivationPrefix(value);
}

function hasDeviceActivationPrefix(value: string): boolean {
  const expected = "HBDEV1-";
  let matched = 0;
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code > 0x7f) return false;
    if (code === 0x20 || (code >= 0x09 && code <= 0x0d)) {
      continue;
    }
    const normalized =
      code >= 0x61 && code <= 0x7a
        ? String.fromCharCode(code - 0x20)
        : character;
    if (normalized !== expected[matched]) return false;
    matched += 1;
    if (matched === expected.length) return true;
  }
  return false;
}

function isTerminator(character: string): boolean {
  return character === "\r" || character === "\n" || character === "\t";
}

function isPrintable(character: string): boolean {
  return character >= " " && character !== "\u007f";
}

function categoryForContext(context: ScannerCaptureContext): ScannerScanCategory {
  switch (context) {
    case "cashier-login":
      return "cashier-code";
    case "supervisor-authorization":
      return "supervisor-code";
    case "dialog":
      return "dialog-code";
    case "emergency-qr":
      return "emergency-qr";
    case "device-activation":
      return "device-activation";
    case "scanner-test":
      return "scanner-test";
    case "product":
    case "product-search":
      return "product-code";
  }
}
