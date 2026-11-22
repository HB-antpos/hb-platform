import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, fireEvent, render } from "@testing-library/react-native";

import { BleScannerCard, BleScannerLoginPanel } from "./ble-scanner-settings";

import { HidScannerRouter } from "@/core/peripherals/scanner";
import type {
  BleScannerScan,
  BleScannerStatus,
} from "@/core/peripherals/scanner/ble/ble-scanner-controller";
import type { SavedBleScanner } from "@/core/peripherals/scanner/ble/ble-scanner-core";
import {
  type BleScannerControllerPort,
  BleScannerService,
} from "@/core/peripherals/scanner/ble/ble-scanner-service";
import { usePosSound } from "@/ui/feedback/pos-sound-context";

jest.mock("@/ui/feedback/pos-sound-context", () => ({ usePosSound: jest.fn() }));

const mockUsePosSound = jest.mocked(usePosSound);

const SAVED: SavedBleScanner = Object.freeze({
  version: 1,
  peripheralId: "D2DA1459-BCE1-0B6C-B7A7-8DE157AA9C2E",
  name: "BarCode Scanner BLE",
  alias: "1 号背夹",
  profileId: "yichip-feea",
  serviceUuid: "0000FEEA-0000-1000-8000-00805F9B34FB",
  notifyUuid: "00002AA1-0000-1000-8000-00805F9B34FB",
  autoReconnect: true,
  savedAt: "2026-11-21T00:00:00.000Z",
});

/** 假控制器：状态可手动推送，扫码由测试直接发出。 */
class FakeController implements BleScannerControllerPort {
  status: BleScannerStatus = {
    state: "ready",
    saved: SAVED,
    batteryLevel: 82,
    rssi: -61,
    lastScanAt: null,
    lastScanValue: "9313559558795",
    attention: false,
    lastErrorCode: null,
  };
  statusListeners = new Set<(status: BleScannerStatus) => void>();
  scanListeners = new Set<(scan: BleScannerScan) => void>();
  forgetCalls = 0;
  async start(): Promise<void> {}
  resume(): void {}
  pause(): void {}
  dispose(): void {}
  getStatus(): BleScannerStatus {
    return this.status;
  }
  subscribeStatus(listener: (status: BleScannerStatus) => void): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }
  subscribeScans(listener: (scan: BleScannerScan) => void): () => void {
    this.scanListeners.add(listener);
    return () => this.scanListeners.delete(listener);
  }
  startDiscovery(): () => void {
    return () => undefined;
  }
  stopDiscovery(): void {}
  async pair(): Promise<void> {}
  async forget(): Promise<void> {
    this.forgetCalls += 1;
  }
  async disconnect(): Promise<void> {}
  async reconnect(): Promise<void> {}
  async setAlias(): Promise<void> {}
  setStatus(patch: Partial<BleScannerStatus>): void {
    this.status = { ...this.status, ...patch };
    this.statusListeners.forEach((listener) => listener(this.status));
  }
  emit(value: string): void {
    const scan: BleScannerScan = {
      kind: "barcode",
      characteristicUuid: SAVED.notifyUuid,
      frame: {
        value,
        rawHex: "39 33 0D",
        packetCount: 3,
        durationMs: 41,
        terminator: "CR",
        receivedAt: Date.now(),
      },
    };
    this.scanListeners.forEach((listener) => listener(scan));
  }
}

beforeEach(() => {
  mockUsePosSound.mockReturnValue({
    buttonSoundEnabled: true,
    play: jest.fn(),
    setButtonSoundEnabled: jest.fn(),
    setSpecialNodeSoundEnabled: jest.fn(),
    specialNodeSoundEnabled: false,
  } as unknown as ReturnType<typeof usePosSound>);
});

function setup() {
  const controller = new FakeController();
  const router = new HidScannerRouter();
  const service = new BleScannerService({ controller, router });
  service.start();
  const routed: string[] = [];
  router.subscribeRouted((event) => routed.push(`${event.context}:${event.source}:${event.value}`));
  return { controller, router, routed, service };
}

test("已连接：显示状态、备注名 + 尾号、电量和最近一次扫码尾 6 位；已知型号显示设置码", async () => {
  const { service, router } = setup();
  const screen = await render(
    <BleScannerCard
      disabled={false}
      locale="zh"
      router={router}
      service={service}
      testDisabled={false}
    />,
  );
  expect(screen.getByText("已连接")).toBeTruthy();
  expect(screen.getByText("1 号背夹 · 9C2E")).toBeTruthy();
  expect(screen.getByText("82%")).toBeTruthy();
  expect(screen.getByText("…558795")).toBeTruthy();
  expect(screen.getByTestId("settings-ble-scanner-codes")).toBeTruthy();
  expect(screen.getByTestId("settings-ble-scanner-disconnect")).toBeTruthy();
});

test("休眠显示灰色提示；未配对只显示「搜索扫码枪」", async () => {
  const { controller, service, router } = setup();
  const screen = await render(
    <BleScannerCard
      disabled={false}
      locale="zh"
      router={router}
      service={service}
      testDisabled={false}
    />,
  );
  await act(async () => {
    controller.setStatus({ state: "sleeping" });
  });
  expect(screen.getByText("休眠中，按扳机唤醒")).toBeTruthy();
  // 休眠是正常状态：仍挂着连接请求，所以给「断开」而不是「重新连接」。
  expect(screen.getByTestId("settings-ble-scanner-disconnect")).toBeTruthy();
  await act(async () => {
    controller.setStatus({ state: "not-paired", saved: null, batteryLevel: null, lastScanValue: null });
  });
  expect(screen.getByTestId("settings-ble-scanner-find")).toBeTruthy();
  expect(screen.queryByTestId("settings-ble-scanner-test")).toBeNull();
  expect(screen.queryByTestId("settings-ble-scanner-codes")).toBeNull();
});

test("测试扫码独占：打开期间 BLE 码只进测试面板，关闭后恢复投给页面", async () => {
  const { controller, service, router, routed } = setup();
  // 模拟销售页正占用 product 场景。
  const releaseSales = router.acquireContext("product");
  const screen = await render(
    <BleScannerCard
      disabled={false}
      locale="zh"
      router={router}
      service={service}
      testDisabled={false}
    />,
  );

  await act(async () => {
    fireEvent.press(screen.getByTestId("settings-ble-scanner-test"));
  });
  expect(screen.getByTestId("settings-ble-scanner-test-panel")).toBeTruthy();

  await act(async () => {
    controller.emit("9313559558795");
  });
  expect(routed).toEqual(["scanner-test:ble:9313559558795"]);
  expect(screen.getByTestId("settings-ble-scanner-test-latest-value").props.children).toBe(
    "9313559558795",
  );
  expect(screen.getByText("13 位 · EAN-13")).toBeTruthy();

  // 键盘模式扫码枪同一个码也进来 → 标记双模式重复。
  await act(async () => {
    router.setCaptureActive(true);
    router.acceptHidText("9313559558795\n");
  });
  expect(screen.getByText("同一个码也从键盘进来了：请关掉其中一种模式")).toBeTruthy();

  // 开通码屏蔽显示。
  await act(async () => {
    controller.emit("HBDEV1-SECRET");
  });
  expect(screen.queryByText("HBDEV1-SECRET")).toBeNull();

  await act(async () => {
    fireEvent.press(screen.getByTestId("settings-ble-scanner-test-end"));
  });
  expect(screen.queryByTestId("settings-ble-scanner-test-panel")).toBeNull();

  await act(async () => {
    controller.emit("VD028");
  });
  expect(routed.at(-1)).toBe("product:ble:VD028");
  releaseSales();
});

test("屏幕设置码：BLE 模式码标「必扫」并画出 Code128", async () => {
  const { service, router } = setup();
  const screen = await render(
    <BleScannerCard
      disabled={false}
      locale="zh"
      router={router}
      service={service}
      testDisabled={false}
    />,
  );
  await act(async () => {
    fireEvent.press(screen.getByTestId("settings-ble-scanner-codes"));
  });
  const bleMode = screen.getByTestId("settings-ble-scanner-code-ble-mode");
  expect(bleMode).toBeTruthy();
  expect(screen.getByText("必扫")).toBeTruthy();
  expect(screen.getByText("扫码枪出厂是键盘模式，先扫「BLE 模式」码。")).toBeTruthy();
  expect(screen.getByLabelText("%%SpecCodeAC")).toBeTruthy();
});

test("登录页面板：登录前就能看到蓝牙扫码枪卡片且配置 / 测试操作不被权限闸禁用；关闭按钮与背板都能关闭", async () => {
  const { service, router } = setup();
  const onClose = jest.fn();
  const screen = await render(
    <BleScannerLoginPanel
      locale="zh"
      onClose={onClose}
      router={router}
      service={service}
    />,
  );

  expect(screen.getByTestId("login-ble-scanner-panel")).toBeTruthy();
  expect(screen.getByTestId("settings-ble-scanner-card")).toBeTruthy();
  expect(screen.getByText("1 号背夹 · 9C2E")).toBeTruthy();
  // 登录前没有收银员权限：测试、替换、断开都必须可点（disabled 为 false）。
  for (const testID of [
    "settings-ble-scanner-test",
    "settings-ble-scanner-replace",
    "settings-ble-scanner-disconnect",
  ]) {
    const button = screen.getByTestId(testID);
    expect(button.props.accessibilityState?.disabled ?? false).toBe(false);
  }

  expect(screen.getByText("关闭")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("login-ble-scanner-close"));
  expect(onClose).toHaveBeenCalledTimes(1);
  await fireEvent.press(screen.getByTestId("login-ble-scanner-backdrop"));
  expect(onClose).toHaveBeenCalledTimes(2);
});

test("登录页面板英文文案：关闭按钮显示 Close", async () => {
  const { service, router } = setup();
  const screen = await render(
    <BleScannerLoginPanel
      locale="en"
      onClose={jest.fn()}
      router={router}
      service={service}
    />,
  );
  expect(screen.getByText("Close")).toBeTruthy();
});
