import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";

import LocalHistoryRoute from "../../../app/(pos)/local-history";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockRouterDismissTo = jest.fn();
const mockRouterReplace = jest.fn();

let mockBleScanOptions: any;
// 扫码路由桥依赖真实导航容器；路由测试只关心页面把码交给了谁，所以替换成可调用的 mock。
jest.mock("@/ui/scanner/scanner-route-bridge", () => ({
  RouteHidScannerCapture: () => null,
  useRouteBleScan: (options: unknown) => {
    mockBleScanOptions = options;
  },
}));

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useRouter: () => ({
      dismissTo: mockRouterDismissTo,
      replace: mockRouterReplace,
    }),
  };
});

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/features/cashier-login", () => ({
  isActiveCashierBoundToDevice: (
    cashier: Readonly<{ storeCode: string; deviceCode: string }>,
    identity: Readonly<{ storeCode: string; deviceCode: string }>,
  ) =>
    cashier.storeCode === identity.storeCode &&
    cashier.deviceCode === identity.deviceCode,
  resolveProtectedSalesRouteGate: (
    runtime: Readonly<{ phase: string; device: string }>,
    cashier: unknown,
  ) => {
    if (
      !["ready", "ready-offline"].includes(runtime.phase) ||
      !["authorized-local", "authorized-online"].includes(runtime.device)
    ) {
      return "redirect-index";
    }
    return cashier ? "check-device-identity" : "redirect-login";
  },
  useCashierLoginStore: (selector: (state: unknown) => unknown) =>
    selector({
      activeCashier: mockActiveCashier,
      clearActiveCashier: mockClearActiveCashier,
    }),
}));

jest.mock("@/features/local-history", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    hasLocalHistoryViewPermission: (permissions: readonly string[]) =>
      permissions.includes("Permissions.PosTerminal.History.View"),
    resolveLocalHistoryPresenterFactory: (services: any) =>
      services?.localHistory ?? null,
    LocalHistoryScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "local-history-screen" },
        "local history",
      );
    },
    LocalHistoryUnavailableScreen: ({ onBack }: { onBack(): void }) =>
      React.createElement(
        Text,
        { onPress: onBack, testID: "local-history-unavailable" },
        "unavailable",
      ),
  };
});

jest.mock("@/ui/screens/pos-route-loading-screen", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosRouteLoadingScreen: ({ active }: { active: string }) =>
      React.createElement(Text, { testID: "route-loading" }, active),
  };
});

beforeEach(() => {
  jest.clearAllMocks();
  mockScreenProps = null;
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: ["Permissions.PosTerminal.History.View"],
    source: "online",
  };
  mockCreatePresenter.mockReturnValue({
    destroy: mockDestroyPresenter,
  });
  mockRuntime = readyRuntime();
});

test("复核当前设备后创建本机历史 presenter，返回销售并在卸载时销毁", async () => {
  const screen = await render(<LocalHistoryRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("local-history-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledWith();

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  expect(mockRouterReplace).not.toHaveBeenCalled();
  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("离线 runtime 仍可进入本机历史", async () => {
  mockRuntime = readyRuntime({
    phase: "ready-offline",
    backend: "offline",
    device: "authorized-local",
  });
  const screen = await render(<LocalHistoryRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("local-history-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledWith();
});

test("缺少历史查看权限时返回销售且不创建 presenter", async () => {
  mockActiveCashier = { ...mockActiveCashier, permissions: [] };
  const screen = await render(<LocalHistoryRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("设备绑定不一致时清除收银会话", async () => {
  mockRuntime = readyRuntime(
    {},
    { storeCode: "S2", deviceCode: "IPAD-2" },
  );
  const screen = await render(<LocalHistoryRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(screen.getByTestId("route-loading")).toBeTruthy();
});

test("runtime 未接线时显示受控不可用状态", async () => {
  mockRuntime = readyRuntime({}, undefined, false);
  const screen = await render(<LocalHistoryRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("local-history-unavailable")).toBeTruthy();
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

function readyRuntime(
  state: Partial<{
    phase: string;
    backend: string;
    device: string;
  }> = {},
  identity: Readonly<{ storeCode: string; deviceCode: string }> | undefined = {
    storeCode: "S1",
    deviceCode: "IPAD-1",
  },
  includeLocalHistory = true,
) {
  return {
    state: {
      phase: state.phase ?? "ready",
      backend: state.backend ?? "reachable",
      device: state.device ?? "authorized-online",
    },
    services: {
      deviceSession: {
        async getDeviceIdentity() {
          return identity;
        },
      },
      ...(includeLocalHistory
        ? { localHistory: { createPresenter: mockCreatePresenter } }
        : {}),
    },
  };
}

test("BLE 扫码枪：扫到的码以递增 id 交给历史屏幕作为关键字，同一个码连扫两次也会再次触发", async () => {
  mockBleScanOptions = undefined;
  const screen = await render(<LocalHistoryRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("local-history-screen")).toBeTruthy();
  });

  expect(mockBleScanOptions.path).toBe("/local-history");
  expect(mockBleScanOptions.enabled).toBe(true);
  expect(mockScreenProps.scannedKeyword).toBeNull();
  await act(async () => {
    mockBleScanOptions.onScan("6E0F13AB");
  });
  expect(mockScreenProps.scannedKeyword).toEqual({ id: 1, value: "6E0F13AB" });
  await act(async () => {
    mockBleScanOptions.onScan("6E0F13AB");
  });
  expect(mockScreenProps.scannedKeyword).toEqual({ id: 2, value: "6E0F13AB" });
  await screen.unmount();
});
