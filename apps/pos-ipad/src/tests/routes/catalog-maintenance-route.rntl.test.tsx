import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import CatalogMaintenanceRoute from "../../../app/(pos)/catalog-maintenance";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockDownloadAndActivate = jest.fn();
const mockGetCurrentCatalog = jest.fn();
const mockCatalogRefresh = {};
const mockInitializeCatalogMaintenance = jest.fn();
const mockGetDeviceIdentity = jest.fn<
  () => Promise<Readonly<{
    storeCode: string;
    deviceCode: string;
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

jest.mock("@/features/catalog/maintenance", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    canDownloadCatalog: (permissions: readonly string[]) =>
      permissions.includes(
        "Permissions.PosTerminal.Settings.CatalogDownload",
      ),
    CatalogMaintenancePresenter: class {
      public readonly options: unknown;

      public constructor(options: unknown) {
        this.options = options;
      }

      public destroy() {}

      public initialize() {
        mockInitializeCatalogMaintenance();
        return Promise.resolve();
      }
    },
    CatalogMaintenanceScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "catalog-maintenance-screen" },
        "catalog",
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
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
  };
  mockGetDeviceIdentity.mockResolvedValue({
    storeCode: "S1",
    deviceCode: "IPAD-1",
  });
  mockRuntime = readyRuntime({
    getDeviceIdentity: mockGetDeviceIdentity,
  });
});

test("具备目录下载权限并复核设备身份后绑定固定门店 Port", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.Settings.CatalogDownload",
  ];
  const screen = await render(<CatalogMaintenanceRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("catalog-maintenance-screen")).toBeTruthy();
  });

  const presenter = mockScreenProps.presenter as {
    options: {
      authenticatedStoreCode: string;
      coordinator: object;
      port: {
        getCurrentCatalog: typeof mockGetCurrentCatalog;
        downloadAndActivate: typeof mockDownloadAndActivate;
      };
    };
  };
  expect(presenter.options).toEqual({
    authenticatedStoreCode: "S1",
    coordinator: mockCatalogRefresh,
    port: {
      getCurrentCatalog: mockGetCurrentCatalog,
      downloadAndActivate: mockDownloadAndActivate,
    },
  });
  expect(mockInitializeCatalogMaintenance).toHaveBeenCalledTimes(1);
  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});

test("零权限收银员直链访问时返回销售页且不创建维护 presenter", async () => {
  const screen = await render(<CatalogMaintenanceRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockScreenProps).toBeNull();
  expect(mockGetDeviceIdentity).not.toHaveBeenCalled();
});

test("设备绑定不一致时清除会话且绝不暴露目录维护屏", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.Settings.CatalogDownload",
  ];
  mockRuntime = readyRuntime({
    async getDeviceIdentity() {
      return { storeCode: "S2", deviceCode: "IPAD-2" };
    },
  });
  const screen = await render(<CatalogMaintenanceRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(screen.getByTestId("bootstrap")).toBeTruthy();
  expect(mockScreenProps).toBeNull();
});

test("没有收银员会话时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<CatalogMaintenanceRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
});

function readyRuntime(
  deviceSession: Readonly<{
    getDeviceIdentity(): Promise<Readonly<{
      storeCode: string;
      deviceCode: string;
    }> | null>;
  }>,
) {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      deviceSession,
      catalogRefresh: mockCatalogRefresh,
      catalog: {
        getCurrentCatalog: mockGetCurrentCatalog,
        downloadAndActivate: mockDownloadAndActivate,
      },
    },
  };
}
