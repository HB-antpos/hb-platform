import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";
import { create } from "zustand";

import SpecialProductsRoute from "../../../app/(pos)/special-products";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
let mockUnavailableProps: any;
type MockSpecialProductsFeedbackEvent = Readonly<{ kind: string }>;
const mockSpecialProductsFeedbackSubscription: {
  listener?: (event: MockSpecialProductsFeedbackEvent) => void;
} = {};
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockSetOnline = jest.fn();
const mockLoad = jest.fn(async () => undefined);
const mockUnsubscribeSpecialProductsFeedback = jest.fn();
const mockSubscribeSpecialProductsFeedback = jest.fn<
  (
    listener: (event: MockSpecialProductsFeedbackEvent) => void,
  ) => () => void
>();
const mockPlaySound = jest.fn();
const mockGetDeviceIdentity = jest.fn<
  () => Promise<Readonly<{
    deviceCode: string;
    storeCode: string;
  }> | null>
>();
const mockRouterDismissTo = jest.fn();
// 模块级单例：声明在 jest.mock 之前（仓库既有模式），工厂引用无 TDZ 风险；
// router 引用稳定，避免重渲染触发 SoundBridge 重订阅
const mockRouter = { dismissTo: mockRouterDismissTo };

// 用真实 zustand store 模拟 connectivity：setState 能驱动 React 重渲染。
type MockShellState = Readonly<{ connectivity: string }>;
const mockShellStore = create<MockShellState>()(() => ({
  connectivity: "online",
}));

jest.mock("@/ui/shell/pos-shell-store", () => ({
  usePosShellStore: (selector: (state: MockShellState) => unknown) =>
    mockShellStore(selector),
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: mockPlaySound }),
}));

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useRouter: () => mockRouter,
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

jest.mock("@/features/special-products", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    resolveSpecialProductsAccess: (permissions: readonly string[]) => ({
      canAddToCart: permissions.includes(
        "Permissions.PosTerminal.SpecialProducts.AddToCart",
      ),
      canManage: permissions.includes(
        "Permissions.PosTerminal.SpecialProducts.Manage",
      ),
      canView: permissions.includes(
        "Permissions.PosTerminal.SpecialProducts.View",
      ),
    }),
    resolveSpecialProductsRuntimeFactory: (services: any) =>
      services.specialProducts ?? null,
    SpecialProductsScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "special-products-screen" },
        "special-products",
      );
    },
    SpecialProductsUnavailableScreen: (props: unknown) => {
      mockUnavailableProps = props;
      return React.createElement(
        Text,
        { testID: "special-products-runtime-unavailable" },
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
  delete mockSpecialProductsFeedbackSubscription.listener;
  mockSubscribeSpecialProductsFeedback.mockImplementation((listener) => {
    mockSpecialProductsFeedbackSubscription.listener = listener;
    return mockUnsubscribeSpecialProductsFeedback;
  });
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    deviceCode: "IPAD-1",
    permissions: ["Permissions.PosTerminal.SpecialProducts.View"],
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
    load: mockLoad,
    subscribeFeedback: mockSubscribeSpecialProductsFeedback,
  });
  mockShellStore.setState({ connectivity: "online" });
  mockRuntime = readyRuntime();
});

test("复核设备身份后零参数创建受限 presenter，并同步在线状态", async () => {
  const screen = await render(<SpecialProductsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("special-products-screen")).toBeTruthy();
  });
  expect(mockGetDeviceIdentity).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(mockSetOnline).toHaveBeenCalledWith(true);

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("特殊商品反馈逐项映射声音 cue，并在路由卸载时解除订阅", async () => {
  const screen = await render(<SpecialProductsRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("special-products-screen")).toBeTruthy();
  });
  expect(mockSubscribeSpecialProductsFeedback).toHaveBeenCalledTimes(1);

  const cases = [
    ["query-found", "query-found"],
    ["query-empty", "query-empty"],
    ["query-error", "query-error"],
    ["added", "cart-added"],
    ["incremented", "cart-incremented"],
    ["failed-blocked", "cart-failed-blocked"],
  ] as const;
  for (const [kind] of cases) {
    mockSpecialProductsFeedbackSubscription.listener?.({ kind });
  }

  expect(mockPlaySound.mock.calls).toEqual(
    cases.map(([, cue]) => [cue]),
  );
  // 加购成功（added）自动返回收银页，一次性守卫拦截连点重复跳转
  expect(mockRouterDismissTo).toHaveBeenCalledTimes(1);
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
  expect(mockUnsubscribeSpecialProductsFeedback).toHaveBeenCalledTimes(1);
});

test("网络离线时仍可进入本地列表，并把管理能力切为离线", async () => {
  mockShellStore.setState({ connectivity: "offline" });
  const screen = await render(<SpecialProductsRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("special-products-screen")).toBeTruthy();
  });
  expect(mockSetOnline).toHaveBeenCalledWith(false);
});

test("网络从离线恢复为在线时自动刷新并恢复下载可用", async () => {
  mockShellStore.setState({ connectivity: "offline" });
  const screen = await render(<SpecialProductsRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("special-products-screen")).toBeTruthy();
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
    // 关键：恢复后自动调用 load() 刷新列表，无需用户手动操作。
    expect(mockLoad).toHaveBeenCalledTimes(1);
  });
  await screen.unmount();
});

test("缺少 View 权限的直链访问返回销售页且不读取设备身份", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.SpecialProducts.Manage",
  ];
  const screen = await render(<SpecialProductsRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockGetDeviceIdentity).not.toHaveBeenCalled();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("设备绑定不一致时清除活动收银员且不创建 presenter", async () => {
  mockGetDeviceIdentity.mockResolvedValue({
    deviceCode: "IPAD-2",
    storeCode: "S2",
  });
  const screen = await render(<SpecialProductsRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
  });
  expect(screen.getByTestId("bootstrap")).toBeTruthy();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("runtime 尚未接线时显示安全不可用页并可返回销售", async () => {
  delete mockRuntime.services.specialProducts;
  const screen = await render(<SpecialProductsRoute />);

  await waitFor(() => {
    expect(
      screen.getByTestId("special-products-runtime-unavailable"),
    ).toBeTruthy();
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  mockUnavailableProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});

test("身份请求在页面销毁后返回时不得创建 presenter", async () => {
  const identity = deferred<{
    deviceCode: string;
    storeCode: string;
  } | null>();
  mockGetDeviceIdentity.mockReturnValue(identity.promise);
  const screen = await render(<SpecialProductsRoute />);
  await screen.unmount();

  identity.resolve({ deviceCode: "IPAD-1", storeCode: "S1" });
  await identity.promise;
  await Promise.resolve();

  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("没有活动收银员时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<SpecialProductsRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
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
      deviceSession: { getDeviceIdentity: mockGetDeviceIdentity },
      specialProducts: { createPresenter: mockCreatePresenter },
    },
    state: {
      backend: "reachable",
      device: "authorized-online",
      phase: "ready",
      ...state,
    },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
