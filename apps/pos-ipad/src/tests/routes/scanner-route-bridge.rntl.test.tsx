import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";

import { HidScannerRouter } from "@/core/peripherals/scanner";
import {
  RouteHidScannerCapture,
  ScannerRouteProvider,
} from "@/ui/scanner/scanner-route-bridge";

let mockRuntime: any;
let mockPathname = "/sales";
let mockIsFocused = true;

jest.mock("expo-router", () => ({
  usePathname: () => mockPathname,
}));

jest.mock("@react-navigation/native", () => ({
  useIsFocused: () => mockIsFocused,
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

beforeEach(() => {
  mockPathname = "/sales";
  mockIsFocused = true;
});

test("同 pathname 的保留路由只让聚焦实例订阅，切焦点后订阅随之转移", async () => {
  const scanner = new HidScannerRouter();
  const subscribeRouted = jest.spyOn(scanner, "subscribeRouted");
  const hiddenOnScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const visibleOnScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => ({ kind: "idle" }),
        subscribe: () => () => undefined,
      },
      scanner: { router: scanner },
    },
  };

  const route = (onScan: typeof hiddenOnScan) => (
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>
  );
  mockIsFocused = false;
  const hiddenScreen = await render(route(hiddenOnScan));
  mockIsFocused = true;
  const visibleScreen = await render(route(visibleOnScan));

  expect(subscribeRouted).toHaveBeenCalledTimes(1);
  scanner.setCaptureActive(true);
  scanner.acceptHidText("VISIBLE-SKU\n");
  expect(hiddenOnScan).not.toHaveBeenCalled();
  expect(visibleOnScan).toHaveBeenCalledWith("VISIBLE-SKU", "hid");

  mockIsFocused = false;
  await visibleScreen.rerender(route(visibleOnScan));
  mockIsFocused = true;
  await hiddenScreen.rerender(route(hiddenOnScan));
  await waitFor(() => {
    expect(subscribeRouted).toHaveBeenCalledTimes(2);
  });
  scanner.setCaptureActive(true);
  scanner.acceptHidText("NEXT-SKU\n");
  expect(hiddenOnScan).toHaveBeenCalledWith("NEXT-SKU", "hid");
  expect(visibleOnScan).toHaveBeenCalledTimes(1);

  await hiddenScreen.unmount();
  await visibleScreen.unmount();
});

test("路由切换和主管弹窗只把完整 HID 条码交给当前 context，并在关闭后恢复销售焦点", async () => {
  const scanner = new HidScannerRouter();
  const listeners = new Set<() => void>();
  let authorizationState: { kind: "awaiting-supervisor" | "idle" } = {
    kind: "idle",
  };
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const onHidTextChange = jest.fn();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => authorizationState,
        subscribe: (listener: () => void) => {
          listeners.add(listener);
          return () => listeners.delete(listener);
        },
      },
      scanner: { router: scanner },
    },
  };

  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onHidTextChange={onHidTextChange}
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>,
  );

  await waitFor(() => {
    scanner.setCaptureActive(true);
    scanner.acceptHidText("SKU-1\n");
    expect(onScan).toHaveBeenLastCalledWith("SKU-1", "hid");
  });

  await act(async () => {
    authorizationState = { kind: "awaiting-supervisor" };
    listeners.forEach((listener) => listener());
  });
  scanner.setCaptureActive(true);
  scanner.acceptHidText("SUPERVISOR-1\n");
  expect(onScan).toHaveBeenCalledTimes(1);

  await act(async () => {
    authorizationState = { kind: "idle" };
    listeners.forEach((listener) => listener());
  });
  await waitFor(() => {
    scanner.setCaptureActive(true);
    scanner.acceptHidText("SKU-2\n");
    expect(onScan).toHaveBeenLastCalledWith("SKU-2", "hid");
  });

  await scanner.startCamera();
  scanner.acceptCameraText("CAMERA-SKU");
  expect(onScan).toHaveBeenLastCalledWith("CAMERA-SKU", "camera");
  await scanner.stopCamera();

  await screen.unmount();
  scanner.acceptHidText("SKU-3\n");
  expect(onScan).toHaveBeenCalledTimes(3);
});

test("同一次 HID 回车同时触发 keyPress 和 submitEditing 时只提交一次条码", async () => {
  const scanner = new HidScannerRouter();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const onHidTextChange = jest.fn();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => ({ kind: "idle" }),
        subscribe: () => () => undefined,
      },
      scanner: { router: scanner },
    },
  };

  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onHidTextChange={onHidTextChange}
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>,
  );

  const hidInput = () => {
    const matches = screen.container.queryAll(
      (instance) => instance.props.caretHidden === true,
      { matchDeepestOnly: true },
    );
    expect(matches).toHaveLength(1);
    return matches[0]!;
  };
  scanner.setCaptureActive(true);
  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  expect(onHidTextChange).toHaveBeenCalledTimes(1);
  await act(async () => {
    hidInput().props.onKeyPress({
      nativeEvent: { key: "Enter" },
    });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001" },
    });
  });

  expect(onScan).toHaveBeenCalledTimes(1);
  expect(onScan).toHaveBeenCalledWith("930000000001", "hid");

  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  await act(async () => {
    hidInput().props.onKeyPress({
      nativeEvent: { key: "Enter" },
    });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001" },
    });
  });
  expect(onScan).toHaveBeenCalledTimes(2);
  await screen.unmount();
});

test("BLE 扫码枪的码只投给聚焦路由，不依赖隐藏输入框焦点；主管授权弹窗优先", async () => {
  const scanner = new HidScannerRouter();
  const listeners = new Set<() => void>();
  let authorizationState: { kind: "awaiting-supervisor" | "idle" } = {
    kind: "idle",
  };
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera" | "ble") => void>();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => authorizationState,
        subscribe: (listener: () => void) => {
          listeners.add(listener);
          return () => listeners.delete(listener);
        },
      },
      scanner: { router: scanner },
    },
  };

  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture context="product" onScan={onScan} path="/sales" />
    </ScannerRouteProvider>,
  );

  // 隐藏输入框没有焦点（captureActive=false）时 BLE 码照样投给当前路由。
  scanner.setCaptureActive(false);
  await waitFor(() => {
    expect(scanner.acceptBleText("BLE-SKU-1")).toBe(true);
    expect(onScan).toHaveBeenLastCalledWith("BLE-SKU-1", "ble");
  });

  await act(async () => {
    authorizationState = { kind: "awaiting-supervisor" };
    listeners.forEach((listener) => listener());
  });
  expect(scanner.acceptBleText("BLE-SUPERVISOR")).toBe(true);
  expect(onScan).toHaveBeenCalledTimes(1);

  await act(async () => {
    authorizationState = { kind: "idle" };
    listeners.forEach((listener) => listener());
  });
  await screen.unmount();
  expect(scanner.acceptBleText("BLE-AFTER-UNMOUNT")).toBe(false);
  expect(onScan).toHaveBeenCalledTimes(1);
});
