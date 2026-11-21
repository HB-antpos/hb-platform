import { afterEach, expect, jest, test } from "@jest/globals";
import { act, cleanup, render } from "@testing-library/react-native";

import { BleScannerStatusBridge } from "./ble-scanner-status-bridge";
import { usePosShellStore } from "./pos-shell-store";

import type { BleScannerStatus } from "@/core/peripherals/scanner/ble/ble-scanner-controller";

let mockRuntime: any;

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

function status(overrides: Partial<BleScannerStatus> = {}): BleScannerStatus {
  return {
    state: "ready",
    saved: null,
    batteryLevel: 80,
    rssi: -55,
    lastScanAt: null,
    lastScanValue: null,
    attention: false,
    lastErrorCode: null,
    ...overrides,
  };
}

/** 假 BLE 服务：getStatus 给初值，subscribeStatus 记录监听器，测试里手动推送。 */
function fakeBle(initial: BleScannerStatus) {
  let listener: ((next: BleScannerStatus) => void) | null = null;
  const unsubscribe = jest.fn(() => {
    listener = null;
  });
  return {
    getStatus: jest.fn(() => initial),
    subscribeStatus: jest.fn((next: (value: BleScannerStatus) => void) => {
      listener = next;
      return unsubscribe;
    }),
    push: (next: BleScannerStatus) => listener?.(next),
    unsubscribe,
  };
}

afterEach(() => {
  cleanup();
  mockRuntime = null;
  usePosShellStore.getState().reset();
});

test("挂载时先读一次初值（subscribeStatus 不会立即回调），之后跟随推送更新", async () => {
  const ble = fakeBle(status({ state: "connecting", batteryLevel: null }));
  mockRuntime = { services: { scanner: { ble } } };

  await render(<BleScannerStatusBridge />);
  expect(usePosShellStore.getState().bleScanner).toEqual({
    state: "connecting",
    batteryLevel: null,
    attention: false,
  });

  await act(async () => {
    ble.push(status({ state: "ready", batteryLevel: 90 }));
  });
  expect(usePosShellStore.getState().bleScanner).toEqual({
    state: "ready",
    batteryLevel: 90,
    attention: false,
  });
});

test("只有 RSSI / 最近扫码时间变化时不重写 store，避免顶栏无意义重渲染", async () => {
  const ble = fakeBle(status());
  mockRuntime = { services: { scanner: { ble } } };
  await render(<BleScannerStatusBridge />);

  const before = usePosShellStore.getState().bleScanner;
  await act(async () => {
    ble.push(status({ rssi: -80, lastScanAt: 123, lastScanValue: "9300000000000" }));
  });
  expect(usePosShellStore.getState().bleScanner).toBe(before);
});

test("没有 BLE 服务（旧原生包）时状态保持 null；卸载时退订并清空", async () => {
  mockRuntime = { services: { scanner: { ble: null } } };
  const none = await render(<BleScannerStatusBridge />);
  expect(usePosShellStore.getState().bleScanner).toBeNull();
  await none.unmount();

  const ble = fakeBle(status());
  mockRuntime = { services: { scanner: { ble } } };
  const screen = await render(<BleScannerStatusBridge />);
  expect(usePosShellStore.getState().bleScanner).not.toBeNull();
  await screen.unmount();
  expect(ble.unsubscribe).toHaveBeenCalledTimes(1);
  expect(usePosShellStore.getState().bleScanner).toBeNull();
});
