import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";
import { create } from "zustand";

import AttendanceAuditRoute from "../../../app/(pos)/attendance-audit";

// 用真实 zustand store 模拟 connectivity：setState 能驱动 React 重渲染。
type MockShellState = Readonly<{ connectivity: string }>;
const mockShellStore = create<MockShellState>()(() => ({
  connectivity: "online",
}));

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
let mockUnavailableProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockSetOnline = jest.fn();
const mockGetDeviceIdentity = jest.fn<
  () => Promise<Readonly<{
    deviceCode: string;
    storeCode: string;
  }> | null>
>();
const mockRouterDismissTo = jest.fn();

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

jest.mock("@/ui/shell/pos-shell-store", () => ({
  usePosShellStore: (selector: (state: MockShellState) => unknown) =>
    mockShellStore(selector),
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

jest.mock("@/features/attendance-audit", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    resolveAttendanceAuditRuntimeFactory: (services: any) =>
      services.attendanceAudit ?? null,
    AttendanceAuditScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "attendance-audit-screen" },
        "attendance-audit",
      );
    },
    AttendanceAuditUnavailableScreen: (props: unknown) => {
      mockUnavailableProps = props;
      return React.createElement(
        Text,
        { testID: "attendance-audit-runtime-unavailable" },
        "unavailable",
      );
    },
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
  mockUnavailableProps = null;
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    deviceCode: "IPAD-1",
    permissions: ["Permissions.PosTerminal.Audit.View"],
    source: "online",
    storeCode: "S1",
  };
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-1",
    storeCode: "S1",
  });
  mockCreatePresenter.mockReturnValue({
    destroy: mockDestroyPresenter,
    setOnline: mockSetOnline,
  });
  mockRuntime = readyRuntime();
  mockShellStore.setState({ connectivity: "online" });
});

test("复核设备绑定后零参数创建 presenter，并同步后端在线状态", async () => {
  const screen = await render(<AttendanceAuditRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("attendance-audit-screen")).toBeTruthy();
  });
  expect(mockGetDeviceIdentity).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(mockSetOnline).toHaveBeenCalledWith(true);

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("网络离线时仍可显示已登记二维码和本机审计", async () => {
  mockShellStore.setState({ connectivity: "offline" });
  const screen = await render(<AttendanceAuditRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("attendance-audit-screen")).toBeTruthy();
  });
  expect(mockSetOnline).toHaveBeenCalledWith(false);
  await screen.unmount();
});

test("缺少 Audit.View 不阻止考勤页面，由 screen 单独保护审计区", async () => {
  mockActiveCashier.permissions = [];
  const screen = await render(<AttendanceAuditRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("attendance-audit-screen")).toBeTruthy();
  });
  expect(mockGetDeviceIdentity).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("设备绑定不一致时清除活动收银员且不创建 presenter", async () => {
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-2",
    storeCode: "S2",
  });
  const screen = await render(<AttendanceAuditRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(screen.getByTestId("route-loading")).toBeTruthy();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  await screen.unmount();
});

test("安全 runtime 未接线时失败关闭并提供返回销售", async () => {
  delete mockRuntime.services.attendanceAudit;
  const screen = await render(<AttendanceAuditRoute />);

  await waitFor(() => {
    expect(
      screen.getByTestId("attendance-audit-runtime-unavailable"),
    ).toBeTruthy();
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  mockUnavailableProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
});

test("没有活动收银员时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<AttendanceAuditRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
  await screen.unmount();
});

function readyRuntime(
  state: Partial<{
    backend: string;
    device: string;
    phase: string;
  }> = {},
) {
  return {
    services: {
      attendanceAudit: { createPresenter: mockCreatePresenter },
      deviceSession: { getDeviceIdentity: mockGetDeviceIdentity },
    },
    state: {
      backend: "reachable",
      device: "authorized-online",
      phase: "ready",
      ...state,
    },
  };
}

test("网络恢复后就地调用 presenter.setOnline 翻转在线状态", async () => {
  mockShellStore.setState({ connectivity: "offline" });
  const screen = await render(<AttendanceAuditRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("attendance-audit-screen")).toBeTruthy();
  });
  expect(mockSetOnline).toHaveBeenCalledWith(false);
  mockSetOnline.mockClear();

  await act(async () => {
    mockShellStore.setState({ connectivity: "online" });
    await new Promise((resolve) => setImmediate(resolve));
  });
  await waitFor(() => {
    expect(mockSetOnline).toHaveBeenCalledWith(true);
  });
  await screen.unmount();
});
