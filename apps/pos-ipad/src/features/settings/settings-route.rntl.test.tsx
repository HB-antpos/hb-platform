import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import SettingsRoute from "../../../app/settings";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
let mockUnavailableProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroy = jest.fn();
const mockGetDeviceIdentity = jest.fn<
  () => Promise<Readonly<{
    storeCode: string;
    deviceCode: string;
  }> | null>
>();
const mockRouterDismissTo = jest.fn();
const mockRouterPush = jest.fn();

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useRouter: () => ({ dismissTo: mockRouterDismissTo, push: mockRouterPush }),
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

jest.mock("@/features/settings", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    resolveSettingsAccess: (permissions: readonly string[]) => ({
      canView: permissions.includes(
        "Permissions.PosTerminal.Settings.View",
      ),
    }),
    resolveSettingsRuntimeFactory: (services: any) =>
      services.settings ?? null,
    SettingsScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "settings-route-screen" },
        "settings",
      );
    },
    SettingsUnavailableScreen: (props: unknown) => {
      mockUnavailableProps = props;
      return React.createElement(
        Text,
        { testID: "settings-route-unavailable" },
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
    cashierName: "Cashier",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: ["Permissions.PosTerminal.Settings.View"],
    source: "online",
  };
  mockCreatePresenter.mockReturnValue({ destroy: mockDestroy });
  mockGetDeviceIdentity.mockResolvedValue({
    storeCode: "S1",
    deviceCode: "IPAD-1",
  });
  mockRuntime = readyRuntime({
    createPresenter: mockCreatePresenter,
  });
});

test("复核设备身份后只调用零参数 Settings 工厂", async () => {
  const screen = await render(<SettingsRoute />);

  await waitFor(() =>
    expect(screen.getByTestId("settings-route-screen")).toBeTruthy(),
  );
  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  expect(mockScreenProps.onOpenSyncHistory).toBeUndefined();
});

test("只有同步记录 View 权限时设置页才提供同步记录处理入口", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.Settings.View",
    "Permissions.PosTerminal.History.View",
  ];
  const screen = await render(<SettingsRoute />);

  await waitFor(() =>
    expect(screen.getByTestId("settings-route-screen")).toBeTruthy(),
  );
  mockScreenProps.onOpenSyncHistory();
  expect(mockRouterPush).toHaveBeenCalledWith("/sync-history");
});

test("没有 View 权限时直链返回销售页且不读取设备身份", async () => {
  mockActiveCashier.permissions = [];
  const screen = await render(<SettingsRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockGetDeviceIdentity).not.toHaveBeenCalled();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("运行时未接线时显示受控不可用页且不伪造 presenter", async () => {
  mockRuntime = readyRuntime(null);
  const screen = await render(<SettingsRoute />);

  await waitFor(() =>
    expect(
      screen.getByTestId("settings-route-unavailable"),
    ).toBeTruthy(),
  );
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  mockUnavailableProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});

test("设备绑定不一致时清除活动收银员并保持 bootstrap", async () => {
  mockGetDeviceIdentity.mockResolvedValue({
    storeCode: "S2",
    deviceCode: "IPAD-2",
  });
  const screen = await render(<SettingsRoute />);

  await waitFor(() =>
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1),
  );
  expect(screen.getByTestId("bootstrap")).toBeTruthy();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

function readyRuntime(
  settings: Readonly<{
    createPresenter(): unknown;
  }> | null,
) {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      deviceSession: { getDeviceIdentity: mockGetDeviceIdentity },
      ...(settings ? { settings } : {}),
    },
  };
}
