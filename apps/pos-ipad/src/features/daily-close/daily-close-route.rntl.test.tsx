import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import DailyCloseRoute from "../../../app/(pos)/daily-close";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
let mockUnavailableProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockGetDeviceIdentity = jest.fn<
  () => Promise<Readonly<{ deviceCode: string; storeCode: string }> | null>
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

jest.mock("@/features/daily-close", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    resolveDailyCloseAccess: (permissions: readonly string[]) => ({
      canView: permissions.includes(
        "Permissions.PosTerminal.DailyClose.View",
      ),
    }),
    resolveDailyCloseRuntimeFactory: (services: any) =>
      services.dailyClose ?? null,
    DailyCloseScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "daily-close-screen" },
        "daily-close",
      );
    },
    DailyCloseUnavailableScreen: (props: unknown) => {
      mockUnavailableProps = props;
      return React.createElement(
        Text,
        { testID: "daily-close-unavailable" },
        "unavailable",
      );
    },
  };
});

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
  jest.clearAllMocks();
  mockScreenProps = null;
  mockUnavailableProps = null;
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Alice",
    deviceCode: "IPAD-1",
    permissions: ["Permissions.PosTerminal.DailyClose.View"],
    source: "online",
    storeCode: "S1",
  };
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-1",
    storeCode: "S1",
  });
  mockCreatePresenter.mockReturnValue({
    destroy: mockDestroyPresenter,
  });
  mockRuntime = readyRuntime();
});

test("复核设备绑定后只以零参数工厂创建 presenter，卸载时销毁", async () => {
  const screen = await render(<DailyCloseRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("daily-close-screen")).toBeTruthy();
  });
  expect(mockGetDeviceIdentity).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("缺 View 权限的直链返回销售页，且不读取设备身份或创建 presenter", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.DailyClose.Save",
  ];
  const screen = await render(<DailyCloseRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockGetDeviceIdentity).not.toHaveBeenCalled();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("设备不匹配清除活动收银员；runtime 未接线显示安全不可用页", async () => {
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-2",
    storeCode: "S2",
  });
  const mismatch = await render(<DailyCloseRoute />);
  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  await mismatch.unmount();

  jest.clearAllMocks();
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-1",
    storeCode: "S1",
  });
  delete mockRuntime.services.dailyClose;
  const unavailable = await render(<DailyCloseRoute />);
  await waitFor(() => {
    expect(unavailable.getByTestId("daily-close-unavailable")).toBeTruthy();
  });
  mockUnavailableProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});

test("活动收银员不存在时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<DailyCloseRoute />);
  expect(screen.getByTestId("redirect").props.children).toBe("/login");
});

function readyRuntime() {
  return {
    state: {
      backend: "reachable",
      device: "authorized-online",
      phase: "ready",
    },
    services: {
      dailyClose: { createPresenter: mockCreatePresenter },
      deviceSession: { getDeviceIdentity: mockGetDeviceIdentity },
    },
  };
}
