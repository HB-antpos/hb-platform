import { afterEach, beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";
import { create } from "zustand";

import InstallmentsRoute from "../../../app/installments";

let mockRuntime: any;
let mockActiveCashier: any;
let mockSetOnline: ReturnType<typeof jest.fn>;
let mockLoad: ReturnType<typeof jest.fn>;
let mockDestroy: ReturnType<typeof jest.fn>;
let mockGetDeviceIdentity: ReturnType<typeof jest.fn>;
let mockRouterDismissTo: ReturnType<typeof jest.fn>;
let mockSetSearchQuery: ReturnType<typeof jest.fn>;
let mockPane: "history" | "create";
// 稳定单例：避免每次渲染返回新 factory 导致路由 effect 无限重建 presenter。
let mockRuntimeFactory: unknown;
let mockClearActiveCashier: ReturnType<typeof jest.fn>;

const MOCK_PRESENTER = () => ({
  setOnline: mockSetOnline,
  load: mockLoad,
  destroy: mockDestroy,
  getState: () => ({ pane: mockPane }),
  setSearchQuery: mockSetSearchQuery,
});

// 用真实 zustand store 模拟 connectivity，使 setState 能驱动 React 重渲染。
type MockShellState = Readonly<{ connectivity: string }>;
const mockShellStore = create<MockShellState>()(() => ({
  connectivity: "online",
}));

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
    useRouter: () => ({ dismissTo: mockRouterDismissTo }),
  };
});

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/features/cashier-login", () => ({
  useCashierLoginStore: (selector: (state: unknown) => unknown) =>
    selector({
      activeCashier: mockActiveCashier,
      clearActiveCashier: mockClearActiveCashier,
    }),
  isActiveCashierBoundToDevice: async () => true,
  resolveProtectedSalesRouteGate: () => "check-device-identity",
}));

jest.mock("@/features/installments", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    InstallmentScreen: () =>
      React.createElement(
        Text,
        { testID: "installment-screen" },
        "installment-screen",
      ),
    InstallmentsUnavailableScreen: () =>
      React.createElement(
        Text,
        { testID: "installments-unavailable" },
        "unavailable",
      ),
    resolveInstallmentsAccess: () => ({
      canAddRepayment: true,
      canCancel: true,
      canConfirmPickup: true,
      canCreate: true,
      canView: true,
    }),
    resolveInstallmentsRuntimeFactory: () => mockRuntimeFactory,
  };
});

jest.mock("@/features/payments/ui", () => ({
  installmentRepaymentPaymentEntry: () => {
    throw new Error("not used");
  },
}));

jest.mock("@/ui/shell/pos-shell-store", () => ({
  usePosShellStore: (selector: (state: MockShellState) => unknown) =>
    mockShellStore(selector),
}));

jest.mock("@/ui/screens/bootstrap-screen", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    BootstrapScreen: () =>
      React.createElement(Text, { testID: "bootstrap" }, "bootstrap"),
  };
});

beforeEach(() => {
  mockShellStore.setState({ connectivity: "online" });
  mockSetOnline = jest.fn();
  mockLoad = jest.fn(async () => undefined);
  mockSetSearchQuery = jest.fn();
  mockPane = "history";
  mockDestroy = jest.fn();
  mockGetDeviceIdentity = jest.fn(async () => ({
    storeCode: "1003",
    deviceCode: "IPAD-01",
  }));
  mockRouterDismissTo = jest.fn();
  mockClearActiveCashier = jest.fn();
  mockActiveCashier = { name: "Cashier A" };
  mockRuntimeFactory = {
    createPresenter: MOCK_PRESENTER,
    prepareCreateCheckout: () => {
      throw new Error("not used");
    },
  };
  mockRuntime = {
    state: { backend: "offline" },
    services: {
      deviceSession: { getDeviceIdentity: mockGetDeviceIdentity },
    },
  };
});

afterEach(() => {
  jest.restoreAllMocks();
});

test("后端离线时分期页进入离线状态，不自动加载", async () => {
  mockShellStore.setState({ connectivity: "offline" });
  await act(async () => {
    await render(<InstallmentsRoute />);
    await new Promise((resolve) => setImmediate(resolve));
  });

  expect(mockSetOnline).toHaveBeenCalledWith(false);
  expect(mockLoad).not.toHaveBeenCalled();
});

test("网络从离线恢复为在线时只同步 presenter 状态，刷新由 Screen 负责", async () => {
  // 进入分期页时后端已离线。
  mockShellStore.setState({ connectivity: "offline" });
  await act(async () => {
    await render(<InstallmentsRoute />);
    await new Promise((resolve) => setImmediate(resolve));
  });
  expect(mockSetOnline).toHaveBeenCalledWith(false);
  mockLoad.mockClear();

  // 后端恢复：NetworkStatusBridge 探测成功 → connectivity 翻转为 online。
  await act(async () => {
    mockShellStore.setState({ connectivity: "online" });
    await new Promise((resolve) => setImmediate(resolve));
  });

  await waitFor(() => {
    expect(mockSetOnline).toHaveBeenCalledWith(true);
    // 路由不直接刷新，避免与 InstallmentScreen 的恢复 effect 重复 load。
    expect(mockLoad).not.toHaveBeenCalled();
  });
});

test("在线期间 connectivity 保持 online 不重复触发加载", async () => {
  await act(async () => {
    await render(<InstallmentsRoute />);
    await new Promise((resolve) => setImmediate(resolve));
  });
  await act(async () => {
    mockShellStore.setState({ connectivity: "online" });
    await new Promise((resolve) => setImmediate(resolve));
  });

  expect(mockSetOnline).toHaveBeenCalledWith(true);
  expect(mockLoad).not.toHaveBeenCalled();
});

test("BLE 扫码枪：分期历史面板扫小票条码直接按单号搜索；新建分期面板不处理", async () => {
  mockBleScanOptions = undefined;
  mockRuntime.state = { backend: "online" };
  await act(async () => {
    await render(<InstallmentsRoute />);
    await new Promise((resolve) => setImmediate(resolve));
  });
  mockLoad.mockClear();

  expect(mockBleScanOptions.path).toBe("/installments");
  expect(mockBleScanOptions.enabled).toBe(true);
  await act(async () => {
    mockBleScanOptions.onScan("3D2E1F00");
  });
  expect(mockSetSearchQuery).toHaveBeenCalledWith("3D2E1F00");
  expect(mockLoad).toHaveBeenCalledTimes(1);

  mockSetSearchQuery.mockClear();
  mockLoad.mockClear();
  mockPane = "create";
  await act(async () => {
    mockBleScanOptions.onScan("3D2E1F00");
  });
  expect(mockSetSearchQuery).not.toHaveBeenCalled();
  expect(mockLoad).not.toHaveBeenCalled();
});
