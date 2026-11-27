import { beforeEach, expect, jest, test } from "@jest/globals";
import {
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import ReturnsRoute from "../../../app/(pos)/returns";

import type { PosNavModel } from "@/ui/shell/pos-app-rail";
import {
  PosNavigationContext,
  type PosNavigationContextValue,
} from "@/ui/shell/pos-navigation-context";
import { PosShellChromeStore, PosShellHost } from "@/ui/shell/pos-shell-host";

let mockRuntime: any;
let mockActiveCashier: any;
let mockReturnScreenProps: any;
let mockSearchParams: Record<string, string | string[]>;
const mockClearActiveCashier = jest.fn();
const mockSignOut = jest.fn();
const mockCreatePresenter = jest.fn<() => Promise<any>>();
const mockDestroyPresenter = jest.fn();
const mockRouterDismissTo = jest.fn();

let mockBleScanOptions: any;
// 扫码路由桥依赖真实导航容器；路由测试只关心页面把码交给了谁，所以替换成可调用的 mock。
jest.mock("@/ui/scanner/scanner-route-bridge", () => ({
  RouteHidScannerCapture: () => null,
  useRouteBleScan: (options: unknown) => {
    mockBleScanOptions = options;
  },
}));

// 外壳的侧栏 / 顶栏换成假组件：这里只验证出错页向常驻外壳登记了什么。
jest.mock("@/ui/shell/pos-app-rail", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosAppRail: (props: { active: string }) => (
      <MockText testID="mock-rail">{`rail:${props.active}`}</MockText>
    ),
  };
});
jest.mock("@/ui/shell/status-strip", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return { PosStatusStrip: () => <MockText testID="mock-status-strip" /> };
});

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useRouter: () => ({ dismissTo: mockRouterDismissTo }),
    useLocalSearchParams: () => mockSearchParams,
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
  }),
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/features/cashier-login", () => ({
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
      signOut: mockSignOut,
    }),
}));

jest.mock("@/features/returns", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    RETURN_MIN_TOUCH_TARGET: 44,
    ReturnScreen: (props: unknown) => {
      mockReturnScreenProps = props;
      return React.createElement(
        Text,
        { testID: "return-screen" },
        "returns",
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
  mockReturnScreenProps = null;
  mockSearchParams = {};
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    userGuid: "user-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
  };
  mockCreatePresenter.mockResolvedValue({
    destroy: mockDestroyPresenter,
  });
  mockRuntime = readyRuntime();
});

test("available 时保持稳定加载占位，异步创建窄 presenter 并在返回或卸载时销毁", async () => {
  const pending = deferred<{ destroy(): void }>();
  mockCreatePresenter.mockReturnValueOnce(pending.promise);
  const screen = await render(<ReturnsRoute />);

  // 占位提前高亮「退货」：外壳左侧栏不会在 presenter 建好前消失。
  expect(screen.getByTestId("route-loading").props.children).toBe("returns");
  pending.resolve({ destroy: mockDestroyPresenter });
  await waitFor(() => {
    expect(screen.getByTestId("return-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(Object.keys(mockReturnScreenProps).sort()).toEqual([
    "onBack",
    "presenter",
  ]);

  mockReturnScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");

  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("在常驻外壳里退货出错页仍登记左侧栏并高亮退货，侧栏不会消失", async () => {
  mockRuntime = readyRuntime({
    status: "unavailable",
    reason: "SUPERVISOR_AUTHENTICATION_MISSING",
  });
  const model: PosNavModel = { entries: [], pinned: [] };
  const navigation: PosNavigationContextValue = {
    appVersionLabel: null,
    language: "zh",
    model,
    openSales: () => undefined,
    switchLanguage: () => undefined,
  };
  const store = new PosShellChromeStore();
  const screen = await render(
    <PosNavigationContext.Provider value={navigation}>
      <PosShellHost store={store}>
        <ReturnsRoute />
      </PosShellHost>
    </PosNavigationContext.Provider>,
  );

  await waitFor(() => {
    expect(screen.getByTestId("returns-route-error")).toBeTruthy();
  });
  expect(store.getSnapshot()).toMatchObject({ active: "returns", model });
  expect(screen.getByTestId("mock-rail")).toHaveTextContent("rail:returns");
  await screen.unmount();
});

test("returns unavailable 时显示可恢复错误并允许返回销售", async () => {
  mockRuntime = readyRuntime({
    status: "unavailable",
    reason: "SUPERVISOR_AUTHENTICATION_MISSING",
  });
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("returns-route-error")).toBeTruthy();
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  expect(screen.queryByTestId("returns-route-retry")).toBeNull();

  const back = screen.getByTestId("returns-route-back");
  expect(StyleSheet.flatten(back.props.style).minHeight).toBeGreaterThanOrEqual(
    44,
  );
  await fireEvent.press(back);
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});

test("orderRef 路由参数只作为既有小票查询预载值传给 ReturnScreen", async () => {
  mockSearchParams = {
    orderRef: "10000000-0000-4000-8000-000000000001",
  };
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("return-screen")).toBeTruthy();
  });
  expect(mockReturnScreenProps.initialReceiptQuery).toBe(
    "10000000-0000-4000-8000-000000000001",
  );
});

test("普通创建失败可原地重试，失败期间不清空收银员", async () => {
  mockCreatePresenter
    .mockRejectedValueOnce(new Error("temporary return setup failure"))
    .mockResolvedValueOnce({ destroy: mockDestroyPresenter });
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("returns-route-error")).toBeTruthy();
  });
  expect(mockClearActiveCashier).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("returns-route-retry"));
  await waitFor(() => {
    expect(screen.getByTestId("return-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledTimes(2);
});

test("presenter 工厂同步抛错也进入可重试状态，不使 route 崩溃", async () => {
  mockCreatePresenter.mockImplementationOnce(() => {
    throw new Error("synchronous return setup failure");
  });
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("returns-route-error")).toBeTruthy();
  });
  expect(screen.getByTestId("returns-route-retry")).toBeTruthy();
  expect(mockClearActiveCashier).not.toHaveBeenCalled();
});

test("Returns.View 明确拒绝时退回既有销售页，不清空 cashier 或 signOut", async () => {
  mockCreatePresenter.mockRejectedValueOnce({
    code: "RETURN_VIEW_FORBIDDEN",
  });
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  });
  expect(mockClearActiveCashier).not.toHaveBeenCalled();
  expect(mockSignOut).not.toHaveBeenCalled();
  expect(screen.queryByTestId("returns-route-error")).toBeNull();
});

test("route 只向 ReturnScreen 传 presenter/back，不读取或泄露私有依赖", async () => {
  const privateAccess = jest.fn();
  const returns = {
    status: "available",
    createPresenter: mockCreatePresenter,
    hasRecoveryRequired: jest.fn(async () => false),
  };
  for (const key of [
    "database",
    "ledger",
    "authorization",
    "provider",
    "protectedRecoveryKey",
  ]) {
    Object.defineProperty(returns, key, {
      configurable: true,
      get: privateAccess,
    });
  }
  mockRuntime = readyRuntime(returns);
  const screen = await render(<ReturnsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("return-screen")).toBeTruthy();
  });
  expect(privateAccess).not.toHaveBeenCalled();
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(mockReturnScreenProps).toEqual({
    onBack: expect.any(Function),
    presenter: expect.objectContaining({
      destroy: mockDestroyPresenter,
    }),
  });
});

function readyRuntime(
  returns: Readonly<Record<string, unknown>> = {
    status: "available",
    createPresenter: mockCreatePresenter,
    hasRecoveryRequired: jest.fn(async () => false),
  },
) {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: { returns },
  };
}

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T): void;
}> {
  let resolvePromise: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    resolvePromise = resolve;
  });
  return {
    promise,
    resolve: resolvePromise,
  };
}

function bleReturnPresenter(state: Record<string, unknown>) {
  return {
    destroy: mockDestroyPresenter,
    getState: () => ({ busy: false, phase: "search", mode: "receipt", ...state }),
    loadReceipt: jest.fn(async (_query: string) => true),
    addNoReceiptProduct: jest.fn(async (_query: string) => true),
  };
}

test("BLE 扫码枪：有小票模式扫小票条码直接查单，无小票模式扫商品码直接加行", async () => {
  for (const [mode, expectLoad] of [["receipt", true], ["no-receipt", false]] as const) {
    const presenter = bleReturnPresenter({ mode });
    mockCreatePresenter.mockResolvedValueOnce(presenter);
    mockBleScanOptions = undefined;
    const screen = await render(<ReturnsRoute />);
    await waitFor(() => {
      expect(screen.getByTestId("return-screen")).toBeTruthy();
    });

    expect(mockBleScanOptions.path).toBe("/returns");
    expect(mockBleScanOptions.enabled).toBe(true);
    mockBleScanOptions.onScan("6E0F13AB");
    expect(presenter.loadReceipt).toHaveBeenCalledTimes(expectLoad ? 1 : 0);
    expect(presenter.addNoReceiptProduct).toHaveBeenCalledTimes(expectLoad ? 0 : 1);
    expect(
      (expectLoad ? presenter.loadReceipt : presenter.addNoReceiptProduct).mock.calls[0]?.[0],
    ).toBe("6E0F13AB");
    await screen.unmount();
  }
});

test("BLE 扫码枪：提交中 / 结果页 / 处理中的退货页不处理扫码，避免打断正在进行的退款", async () => {
  for (const state of [
    { busy: true },
    { phase: "submitting" },
    { phase: "unknown" },
    { phase: "success" },
    { phase: "failed" },
  ]) {
    const presenter = bleReturnPresenter(state);
    mockCreatePresenter.mockResolvedValueOnce(presenter);
    const screen = await render(<ReturnsRoute />);
    await waitFor(() => {
      expect(screen.getByTestId("return-screen")).toBeTruthy();
    });

    mockBleScanOptions.onScan("6E0F13AB");
    expect(presenter.loadReceipt).not.toHaveBeenCalled();
    expect(presenter.addNoReceiptProduct).not.toHaveBeenCalled();
    await screen.unmount();
  }
});
