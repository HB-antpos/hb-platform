import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import SyncHistoryRoute from "../../../app/(pos)/sync-history";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockRouterDismissTo = jest.fn();
const mockFileCreate = jest.fn();
const mockFileWrite = jest.fn();
const mockFileDelete = jest.fn();
const mockFileConstructor = jest.fn();
const mockShareAsync =
  jest.fn<(uri: string, options: unknown) => Promise<void>>();
const mockSharingAvailable = jest.fn<() => Promise<boolean>>();
let mockFileExists = false;

jest.mock("expo-file-system", () => ({
  File: class {
    public readonly uri = "file:///cache/hb-pos-sync-support.json";

    public constructor(directory: unknown, fileName: string) {
      mockFileConstructor(directory, fileName);
    }

    public get exists() {
      return mockFileExists;
    }

    public create(options: unknown) {
      mockFileExists = true;
      mockFileCreate(options);
    }

    public write(value: string) {
      mockFileWrite(value);
    }

    public delete() {
      mockFileExists = false;
      mockFileDelete();
    }
  },
  Paths: { cache: "file:///cache/" },
}));

jest.mock("expo-sharing", () => ({
  isAvailableAsync: () => mockSharingAvailable(),
  shareAsync: (uri: string, options: unknown) => mockShareAsync(uri, options),
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

jest.mock("@/features/sync-history", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    SyncHistoryScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "sync-history-screen" },
        "history",
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
  mockFileExists = false;
  mockSharingAvailable.mockResolvedValue(true);
  mockShareAsync.mockResolvedValue(undefined);
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
  };
  mockCreatePresenter.mockReturnValue({
    destroy: mockDestroyPresenter,
  });
  mockRuntime = readyRuntime({
    async getDeviceIdentity() {
      return { storeCode: "S1", deviceCode: "IPAD-1" };
    },
  });
});

test("设备身份复核后创建同步历史 presenter，返回销售且卸载时销毁", async () => {
  const screen = await render(<SyncHistoryRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sync-history-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith(
    mockActiveCashier.permissions,
  );

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");

  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("支持导出只向缓存写固定安全文件名，以文件分享并在成功后删除", async () => {
  const screen = await render(<SyncHistoryRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sync-history-screen")).toBeTruthy();
  });

  await mockScreenProps.onExport('{"format":"hb-pos-sync-history-v1"}');
  expect(mockFileConstructor).toHaveBeenCalledWith(
    "file:///cache/",
    "hb-pos-sync-support.json",
  );
  expect(mockFileCreate).toHaveBeenCalledWith({
    intermediates: true,
    overwrite: true,
  });
  expect(mockFileWrite).toHaveBeenCalledWith(
    '{"format":"hb-pos-sync-history-v1"}',
  );
  expect(mockShareAsync).toHaveBeenCalledWith(
    "file:///cache/hb-pos-sync-support.json",
    {
      UTI: "public.json",
      dialogTitle: "HB POS support export",
      mimeType: "application/json",
    },
  );
  expect(mockFileDelete).toHaveBeenCalledTimes(1);
  expect(mockFileExists).toBe(false);
});

test("系统分享失败时 finally 仍删除临时支持导出文件", async () => {
  mockShareAsync.mockRejectedValueOnce(new Error("share failed"));
  const screen = await render(<SyncHistoryRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sync-history-screen")).toBeTruthy();
  });

  await expect(
    mockScreenProps.onExport('{"format":"hb-pos-sync-history-v1"}'),
  ).rejects.toThrow("share failed");
  expect(mockFileDelete).toHaveBeenCalledTimes(1);
  expect(mockFileExists).toBe(false);
});

test("设备绑定不一致时清除收银会话且不创建历史 presenter", async () => {
  mockRuntime = readyRuntime({
    async getDeviceIdentity() {
      return { storeCode: "S2", deviceCode: "IPAD-2" };
    },
  });
  const screen = await render(<SyncHistoryRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(screen.getByTestId("route-loading")).toBeTruthy();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("没有收银员会话时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<SyncHistoryRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
  expect(mockCreatePresenter).not.toHaveBeenCalled();
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
      syncHistory: { createPresenter: mockCreatePresenter },
    },
  };
}
