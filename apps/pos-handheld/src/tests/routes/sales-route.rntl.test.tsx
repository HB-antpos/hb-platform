import { afterEach, beforeEach, expect, jest, test } from "@jest/globals";
import { act, fireEvent, render, waitFor } from "@testing-library/react-native";

import SalesRoute from "../../../app/sales";

let mockRuntime: any;
let mockActiveCashier: any;
let mockSalesScreenProps: any;
let mockRouteCaptureProps: any;
let mockCameraScannerProps: any;
let mockUpdateGate: any;
let mockUpdatePolicy: any;
let mockSalesRouteFocusEffect: (() => void) | null;
let mockPresenterState: any;
let mockPresenterListener: (() => void) | null;
type MockSalesFeedbackEvent = Readonly<{
  kind: string;
  timingId?: string;
}>;
const mockSalesFeedbackSubscription: {
  listener?: (event: MockSalesFeedbackEvent) => void;
} = {};
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockUnsubscribeSalesFeedback = jest.fn();
const mockSubscribeSalesFeedback =
  jest.fn<(listener: (event: MockSalesFeedbackEvent) => void) => () => void>();
const mockUnsubscribePresenter = jest.fn();
const mockSubscribePresenter = jest.fn<(listener: () => void) => () => void>();
const mockMarkSalesFirstFrameCommitted = jest.fn();
const mockMarkSalesInteractive = jest.fn();
const mockBusinessStartupFail = jest.fn();
const mockPlaySound = jest.fn();
const mockExpectSound = jest.fn();
const mockNoteHidCharacter = jest.fn();
const mockRouterPush = jest.fn();
const mockRouterReplace = jest.fn();
const mockHasRecoveryRequired = jest.fn<() => Promise<boolean>>();
const mockInstallmentHasRecoveryRequired = jest.fn<() => Promise<boolean>>();
const mockRandomUUID = jest.fn();
const mockAddLookupCode = jest.fn<() => Promise<boolean>>();
const mockAddScannedLookupCode =
  jest.fn<(barcode: string, source: "hid" | "camera") => Promise<boolean>>();
const mockPrepareOnlineCheckout = jest.fn<() => Promise<any>>();
const mockReleasePreparedCheckout = jest.fn();
const mockCatalogFindExact = jest.fn<(lookupCode: string) => Promise<any>>();
const mockReprintReceipt = jest.fn<() => Promise<any>>();
const mockOpenCashDrawer = jest.fn<() => Promise<any>>();
const mockPerformSelectedUpdate = jest.fn<() => Promise<any>>();
const mockRecordApplicationLog = jest.fn();
const mockSetQuery = jest.fn();
const mockToggleAppLanguage = jest.fn<() => Promise<"en" | "zh">>();
const mockReadSalesToolbarOrder = jest.fn<() => string[] | null>();
const mockSaveSalesToolbarOrder =
  jest.fn<(order: readonly string[]) => Promise<void>>();
const mockReconcileSalesToolbarOrder =
  jest.fn<(order: readonly string[] | null | undefined) => string[]>();

const DEFAULT_TOOLBAR_ORDER = ["held-orders", "hold", "language", "lock"];

// Settings 设备换店也引用独立相机入口；本路由测试只验证销售扫码，避免加载原生模块。
jest.mock("@/features/scanner-camera/camera-scanner-modal", () => ({
  CameraScannerModal: () => null,
}));

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useFocusEffect: (effect: () => void) => {
      React.useEffect(() => {
        mockSalesRouteFocusEffect = effect;
        return () => {
          if (mockSalesRouteFocusEffect === effect) {
            mockSalesRouteFocusEffect = null;
          }
        };
      }, [effect]);
    },
    useRouter: () => ({ push: mockRouterPush, replace: mockRouterReplace }),
  };
});

jest.mock("expo-crypto", () => ({
  randomUUID: () => mockRandomUUID(),
}));

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.mock("@/i18n", () => ({
  toggleAppLanguage: () => mockToggleAppLanguage(),
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: mockPlaySound }),
}));

jest.mock("@/features/sales/runtime/scan-timing", () => ({
  scanTiming: {
    expectSound: (timingId: string | undefined, cue: string) =>
      mockExpectSound(timingId, cue),
    noteHidCharacter: () => mockNoteHidCharacter(),
  },
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/core/performance/business-startup-clock", () => ({
  businessStartupClock: {
    markSalesFirstFrameCommitted: () => mockMarkSalesFirstFrameCommitted(),
    markSalesInteractive: () => mockMarkSalesInteractive(),
    fail: () => mockBusinessStartupFail(),
  },
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
    }),
}));

jest.mock("@/features/sales/ui", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    resolveSalesLocale: () => "en",
    SalesScreen: (props: unknown) => {
      mockSalesScreenProps = props;
      return React.createElement(Text, { testID: "sales-screen" }, "sales");
    },
  };
});

jest.mock("@/features/sales/ui/sales-toolbar-order", () => ({
  reconcileSalesToolbarOrder: (order: readonly string[] | null | undefined) =>
    mockReconcileSalesToolbarOrder(order),
}));

jest.mock("@/features/scanner-camera", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    CameraScannerModal: (props: unknown) => {
      mockCameraScannerProps = props;
      return (props as { visible: boolean }).visible
        ? React.createElement(
            Text,
            { testID: "camera-scanner-modal-mock" },
            "camera",
          )
        : null;
    },
  };
});

jest.mock("@/ui/preferences/terminal-ui-preferences", () => ({
  readSalesToolbarOrder: () => mockReadSalesToolbarOrder(),
  saveSalesToolbarOrder: (order: readonly string[]) =>
    mockSaveSalesToolbarOrder(order),
}));

jest.mock("@/ui/scanner/scanner-route-bridge", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    RouteHidScannerCapture: (props: unknown) => {
      mockRouteCaptureProps = props;
      return React.createElement(
        Text,
        { testID: "hid-scanner-capture-mock" },
        String((props as { enabled?: boolean }).enabled ?? true),
      );
    },
  };
});

jest.mock("@/features/catalog/maintenance", () => ({
  canDownloadCatalog: (permissions: readonly string[]) =>
    permissions.includes("Permissions.PosTerminal.Settings.CatalogDownload"),
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

jest.mock("@/ui/shell/status-strip", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosStatusStrip: () =>
      React.createElement(Text, { testID: "status-strip" }, "status"),
  };
});

beforeEach(() => {
  jest.clearAllMocks();
  mockSalesScreenProps = null;
  mockRouteCaptureProps = null;
  mockCameraScannerProps = null;
  mockSalesRouteFocusEffect = null;
  mockPresenterListener = null;
  delete mockSalesFeedbackSubscription.listener;
  mockSubscribeSalesFeedback.mockImplementation((listener) => {
    mockSalesFeedbackSubscription.listener = listener;
    return mockUnsubscribeSalesFeedback;
  });
  mockSubscribePresenter.mockImplementation((listener) => {
    mockPresenterListener = listener;
    return mockUnsubscribePresenter;
  });
  mockHasRecoveryRequired.mockResolvedValue(false);
  mockInstallmentHasRecoveryRequired.mockResolvedValue(false);
  mockAddLookupCode.mockResolvedValue(true);
  mockPrepareOnlineCheckout.mockResolvedValue({
    revision: 7,
    lines: [{ lineId: "line-1" }],
    actualAmount: { currency: "AUD", cents: 1_250 },
  });
  mockCatalogFindExact.mockResolvedValue(null);
  mockReprintReceipt.mockResolvedValue({
    state: "Printed",
    errorCode: null,
  });
  mockOpenCashDrawer.mockResolvedValue({
    state: "Completed",
    errorCode: null,
  });
  mockPerformSelectedUpdate.mockResolvedValue({
    action: "open-app-store",
    url: "https://apps.apple.com/au/app/hb-pos/id123456789",
  });
  mockRandomUUID.mockReturnValue("123e4567-e89b-42d3-a456-426614174000");
  mockToggleAppLanguage.mockResolvedValue("zh");
  mockReadSalesToolbarOrder.mockReturnValue(null);
  mockSaveSalesToolbarOrder.mockResolvedValue(undefined);
  mockReconcileSalesToolbarOrder.mockImplementation((order) =>
    order ? [...order] : [...DEFAULT_TOOLBAR_ORDER],
  );
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    userGuid: "user-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [
      "Permissions.PosTerminal.CashDrawer.Open",
      "Permissions.PosTerminal.History.View",
      "Permissions.PosTerminal.SpecialProducts.View",
      "Permissions.PosTerminal.DailyClose.View",
      "Permissions.PosTerminal.Installments.View",
      "Permissions.PosTerminal.Settings.View",
    ],
    source: "online",
  };
  mockPresenterState = {
    phase: "selling",
    cart: {
      revision: 7,
      lines: [{ lineId: "line-1" }],
      actualAmount: { currency: "AUD", cents: 1_250 },
    },
    capabilities: {
      catalog: true,
      cartEditing: true,
    },
  };
  mockCreatePresenter.mockReturnValue({
    addLookupCode: mockAddLookupCode,
    addScannedLookupCode: mockAddScannedLookupCode,
    destroy: mockDestroyPresenter,
    getState: () => mockPresenterState,
    prepareOnlineCheckout: mockPrepareOnlineCheckout,
    releasePreparedCheckout: mockReleasePreparedCheckout,
    setQuery: mockSetQuery,
    subscribe: mockSubscribePresenter,
    subscribeFeedback: mockSubscribeSalesFeedback,
  });
  mockUpdateGate = {
    state: "enabled",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  };
  mockUpdatePolicy = {
    enabled: true,
    appStoreId: null,
    bundleIdentifier: null,
    distribution: null,
    downloadUrl: null,
    fileSize: null,
    latestBuild: null,
    latestVersion: null,
    minimumSupportedVersion: null,
    packageName: null,
    platform: "iOS",
    policyVersion: "none",
    releaseMessage: null,
    required: false,
    sha256: null,
    signingCertificateSha256: null,
    state: "none",
  };
  mockRuntime = readyRuntime();
});

afterEach(() => {
  jest.useRealTimers();
});

test("销售路由零参数创建生产 presenter，并在卸载时销毁", async () => {
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();

  mockSalesScreenProps.onOpenHeldOrders();
  expect(mockRouterPush).toHaveBeenCalledWith("/held-orders");
  mockSalesScreenProps.onOpenDailyClose();
  expect(mockRouterPush).toHaveBeenCalledWith("/daily-close");
  mockSalesScreenProps.onOpenReturns();
  expect(mockRouterPush).toHaveBeenCalledWith("/returns");
  mockSalesScreenProps.onOpenRemoteHistory();
  expect(mockRouterPush).toHaveBeenCalledWith("/remote-history");
  mockSalesScreenProps.onOpenLocalHistory();
  expect(mockRouterPush).toHaveBeenCalledWith("/local-history");
  mockSalesScreenProps.onOpenSpecialProducts();
  expect(mockRouterPush).toHaveBeenCalledWith("/special-products");
  mockSalesScreenProps.onOpenInstallments();
  expect(mockRouterPush).toHaveBeenCalledWith("/installments");
  mockSalesScreenProps.onOpenSettings();
  expect(mockRouterPush).toHaveBeenCalledWith("/settings");
  mockSalesScreenProps.onOpenSyncHistory();
  expect(mockRouterPush).toHaveBeenCalledWith("/sync-history");
  mockSalesScreenProps.onOpenPayment({
    revision: 7,
    lines: [{ lineId: "line-1" }],
    actualAmount: { currency: "AUD", cents: 1_250 },
  });
  expect(mockPrepareOnlineCheckout).not.toHaveBeenCalled();
  expect(mockRouterPush).toHaveBeenCalledWith({
    pathname: "/payment",
    params: {
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "7",
      totalCents: "1250",
    },
  });
  expect(mockSalesScreenProps.onOpenCatalogMaintenance).toBeUndefined();
  expect(mockSalesScreenProps.newTransactionGate).toEqual(mockUpdateGate);
  mockSalesScreenProps.onSwitchLanguage();
  expect(mockToggleAppLanguage).toHaveBeenCalledTimes(1);

  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("销售首帧提交后仍等待目录与销售录入实际可交互", async () => {
  mockPresenterState = {
    ...mockPresenterState,
    capabilities: {
      ...mockPresenterState.capabilities,
      catalog: false,
    },
  };
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
    expect(mockMarkSalesFirstFrameCommitted).toHaveBeenCalledTimes(1);
  });
  expect(mockMarkSalesInteractive).not.toHaveBeenCalled();

  await act(async () => {
    mockPresenterState = {
      ...mockPresenterState,
      capabilities: {
        ...mockPresenterState.capabilities,
        catalog: true,
      },
    };
    mockPresenterListener?.();
    await Promise.resolve();
  });

  expect(mockMarkSalesInteractive).toHaveBeenCalledTimes(1);
});

test("更新服务尚未注入时使用默认允许的新交易门禁", async () => {
  delete mockRuntime.services.appUpdates;

  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockSalesScreenProps.newTransactionGate).toEqual({
    state: "unchecked",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });
});

test("销售反馈逐项映射声音 cue，并在路由卸载时解除订阅", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockSubscribeSalesFeedback).toHaveBeenCalledTimes(1);

  const cases = [
    ["query-found", "query-found"],
    ["query-empty", "query-empty"],
    ["query-error", "query-error"],
    ["added", "cart-added"],
    ["incremented", "cart-incremented"],
    ["not-found", "cart-not-found"],
    ["failed-blocked", "cart-failed-blocked"],
  ] as const;
  for (const [kind] of cases) {
    mockSalesFeedbackSubscription.listener?.({ kind });
  }

  expect(mockPlaySound.mock.calls).toEqual(cases.map(([, cue]) => [cue]));
  mockSalesFeedbackSubscription.listener?.({
    kind: "added",
    timingId: "scan-1",
  });
  expect(mockExpectSound).toHaveBeenCalledWith("scan-1", "cart-added");
  await screen.unmount();
  expect(mockUnsubscribeSalesFeedback).toHaveBeenCalledTimes(1);
});

test("本机历史与分期入口分别使用 History.View 和 Installments.View", async () => {
  mockActiveCashier = {
    ...mockActiveCashier,
    permissions: ["Permissions.PosTerminal.History.View"],
  };
  const historyOnly = await render(<SalesRoute />);
  await waitFor(() => {
    expect(historyOnly.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockSalesScreenProps.onOpenRemoteHistory).toBeInstanceOf(Function);
  expect(mockSalesScreenProps.onOpenLocalHistory).toBeInstanceOf(Function);
  expect(mockSalesScreenProps.onOpenInstallments).toBeUndefined();
  await historyOnly.unmount();

  mockActiveCashier = {
    ...mockActiveCashier,
    permissions: ["Permissions.PosTerminal.Installments.View"],
  };
  const installmentsOnly = await render(<SalesRoute />);
  await waitFor(() => {
    expect(installmentsOnly.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockSalesScreenProps.onOpenRemoteHistory).toBeUndefined();
  expect(mockSalesScreenProps.onOpenLocalHistory).toBeUndefined();
  expect(mockSalesScreenProps.onOpenInstallments).toBeInstanceOf(Function);
  await installmentsOnly.unmount();
});

test("销售功能按钮只调用受保护履约 facade，并完整映射终态", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  const cases = [
    ["Printed", "completed"],
    ["Completed", "completed"],
    ["not-found", "not-found"],
    ["denied", "denied"],
    ["not-retryable", "unavailable"],
    ["Ambiguous", "unknown"],
    ["Unknown", "unknown"],
    ["recovery-required", "unknown"],
    ["Failed", "failed"],
  ] as const;
  for (const [state, kind] of cases) {
    mockReprintReceipt.mockResolvedValueOnce({
      state,
      errorCode: state === "denied" ? "PERMISSION_DENIED" : null,
    });
    assertUtilityResult(await mockSalesScreenProps.onReprintReceipt(), kind);
  }
  assertUtilityResult(
    await mockSalesScreenProps.onOpenCashDrawer(),
    "completed",
  );
  expect(mockReprintReceipt).toHaveBeenCalledTimes(cases.length);
  expect(mockOpenCashDrawer).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("购物车图片接受目录复核后的相对地址和外部 HTTPS 地址", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  const identity = {
    productCode: "P-1",
    lookupCode: "930000000001",
  };
  mockCatalogFindExact.mockResolvedValue({
    ...identity,
    productImage: "/media/products/milk.png",
  });

  await expect(
    mockSalesScreenProps.resolveCartProductImage(identity),
  ).resolves.toBe("https://pos.example.test/media/products/milk.png");
  expect(mockCatalogFindExact).toHaveBeenLastCalledWith(identity.lookupCode);

  mockCatalogFindExact.mockResolvedValue({
    ...identity,
    productImage: "https://cdn.example.test/milk.png",
  });
  await expect(
    mockSalesScreenProps.resolveCartProductImage(identity),
  ).resolves.toBe("https://cdn.example.test/milk.png");

  mockCatalogFindExact.mockResolvedValue({
    ...identity,
    productImage: "https://user:password@pos.example.test/milk.png",
  });
  await expect(
    mockSalesScreenProps.resolveCartProductImage(identity),
  ).resolves.toBeNull();

  mockCatalogFindExact.mockResolvedValue({
    ...identity,
    productCode: "P-OTHER",
    productImage: "/media/products/other.png",
  });
  await expect(
    mockSalesScreenProps.resolveCartProductImage(identity),
  ).resolves.toBeNull();

  mockCatalogFindExact.mockClear();
  await expect(
    mockSalesScreenProps.resolveCartProductImage({
      productCode: " ",
      lookupCode: identity.lookupCode,
    }),
  ).resolves.toBeNull();
  expect(mockCatalogFindExact).not.toHaveBeenCalled();
  await screen.unmount();
});

test("一码多商品时按商品编码在同码候选中取图，非胜出商品行也能显示自己的图片", async () => {
  const findExactCandidates = jest.fn(async (_lookupCode: string) => [
    {
      productCode: "P-FLY",
      lookupCode: "6405090401470",
      productImage: "/media/products/fly.png",
    },
    {
      productCode: "P-FLOWER",
      lookupCode: "6405090401470",
      productImage: "/media/products/flower.png",
    },
  ]);
  mockRuntime.services.catalog.findExactCandidates = findExactCandidates;
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  await expect(
    mockSalesScreenProps.resolveCartProductImage({
      productCode: "P-FLOWER",
      lookupCode: "6405090401470",
    }),
  ).resolves.toBe("https://pos.example.test/media/products/flower.png");
  await expect(
    mockSalesScreenProps.resolveCartProductImage({
      productCode: "P-GONE",
      lookupCode: "6405090401470",
    }),
  ).resolves.toBeNull();
  expect(findExactCandidates).toHaveBeenLastCalledWith("6405090401470");
  expect(mockCatalogFindExact).not.toHaveBeenCalled();
  await screen.unmount();
});

test("销售页只结算一次，路由直接使用已核验快照且不重复调用 Presenter", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  mockSalesScreenProps.onOpenPayment({
    revision: 9,
    lines: [{ lineId: "line-latest" }],
    actualAmount: { currency: "AUD", cents: 1_875 },
  });

  expect(mockPrepareOnlineCheckout).not.toHaveBeenCalled();
  expect(mockRouterPush).toHaveBeenCalledWith({
    pathname: "/payment",
    params: {
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "9",
      totalCents: "1875",
    },
  });
  await screen.unmount();
});

test("从统一支付页返回收银时释放已核验 checkout，使商品录入恢复可用", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  mockSalesScreenProps.onOpenPayment({
    revision: 9,
    lines: [{ lineId: "line-square" }],
    actualAmount: { currency: "AUD", cents: 1_875 },
  });
  expect(mockRouterPush).toHaveBeenCalledWith({
    pathname: "/payment",
    params: {
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "9",
      totalCents: "1875",
    },
  });
  expect(mockReleasePreparedCheckout).not.toHaveBeenCalled();
  expect(mockSalesRouteFocusEffect).not.toBeNull();

  await act(async () => {
    mockSalesRouteFocusEffect?.();
    await Promise.resolve();
  });

  expect(mockReleasePreparedCheckout).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("已核验快照失效时释放结账租约且不进入支付页", async () => {
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  mockSalesScreenProps.onOpenPayment({
    revision: 9,
    lines: [],
    actualAmount: { currency: "AUD", cents: 0 },
  });

  expect(mockReleasePreparedCheckout).toHaveBeenCalledTimes(1);
  expect(mockRouterPush).not.toHaveBeenCalled();
  await screen.unmount();
});

test("扫码回调不等待后台在线查询，并把实际 HID/相机来源传给 Presenter", async () => {
  mockAddScannedLookupCode.mockImplementation(
    () => new Promise<boolean>(() => undefined),
  );
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });

  mockRouteCaptureProps.onHidTextChange();
  expect(mockNoteHidCharacter).toHaveBeenCalledTimes(1);

  const firstResult = mockRouteCaptureProps.onScan("930000000001");
  const secondResult = mockRouteCaptureProps.onScan("930000000002", "camera");

  expect(firstResult).toBeUndefined();
  expect(secondResult).toBeUndefined();
  expect(mockSetQuery).not.toHaveBeenCalled();
  expect(mockAddScannedLookupCode.mock.calls).toEqual([
    ["930000000001", "hid"],
    ["930000000002", "camera"],
  ]);
  await screen.unmount();
});

test("销售工具栏同步读取已保存顺序，先更新页面再异步持久化", async () => {
  const storedOrder = ["lock", "language", "hold"];
  const updatedOrder = ["language", "hold", "lock"];
  mockReadSalesToolbarOrder.mockReturnValue(storedOrder);
  mockReconcileSalesToolbarOrder.mockImplementation((order) =>
    order ? [...order] : [...DEFAULT_TOOLBAR_ORDER],
  );
  mockSaveSalesToolbarOrder.mockRejectedValueOnce(
    new Error("terminal preferences unavailable"),
  );

  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockReadSalesToolbarOrder).toHaveBeenCalledTimes(1);
  expect(mockReconcileSalesToolbarOrder).toHaveBeenCalledWith(storedOrder);
  expect(mockSalesScreenProps.toolbarOrder).toEqual(storedOrder);

  await act(async () => {
    mockSalesScreenProps.onToolbarOrderChange(updatedOrder);
    await Promise.resolve();
  });

  expect(mockSaveSalesToolbarOrder).toHaveBeenCalledWith(updatedOrder);
  expect(mockSalesScreenProps.toolbarOrder).toEqual(updatedOrder);
  expect(mockReconcileSalesToolbarOrder).toHaveBeenLastCalledWith(updatedOrder);

  await screen.unmount();
});

test("销售路由把相机作为 HID 备用入口，打开期间停用 HID 并保留相机来源", async () => {
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockSalesScreenProps.onOpenCameraScanner).toBeInstanceOf(Function);
  expect(mockRouteCaptureProps.enabled).toBe(true);
  expect(screen.queryByTestId("camera-scanner-modal-mock")).toBeNull();

  await act(async () => {
    mockSalesScreenProps.onOpenCameraScanner();
  });
  expect(screen.getByTestId("camera-scanner-modal-mock")).toBeTruthy();
  expect(mockRouteCaptureProps.enabled).toBe(false);
  expect(mockCameraScannerProps.context).toBe("product");
  expect(mockCameraScannerProps.scanner).toBe(
    mockRuntime.services.scanner.router,
  );

  mockCameraScannerProps.onScan("930000000099");
  expect(mockAddScannedLookupCode).toHaveBeenLastCalledWith(
    "930000000099",
    "camera",
  );
  await act(async () => {
    mockCameraScannerProps.onClose();
  });
  expect(screen.queryByTestId("camera-scanner-modal-mock")).toBeNull();
  expect(mockRouteCaptureProps.enabled).toBe(true);
});

test("手动输入失焦后延后恢复 HID，焦点交接会取消恢复", async () => {
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  jest.useFakeTimers();
  try {
    expect(mockRouteCaptureProps.enabled).toBe(true);

    await act(async () => {
      mockSalesScreenProps.onManualInputFocusChange(true);
    });
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockSalesScreenProps.onManualInputFocusChange(false);
    });
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockSalesScreenProps.onManualInputFocusChange(true);
      jest.runOnlyPendingTimers();
    });
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockSalesScreenProps.onManualInputFocusChange(false);
      jest.runOnlyPendingTimers();
    });
    expect(mockRouteCaptureProps.enabled).toBe(true);
  } finally {
    jest.useRealTimers();
  }
});

test("销售路由卸载时清理待恢复的 HID 定时器", async () => {
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  jest.useFakeTimers();
  const clearTimeoutSpy = jest.spyOn(global, "clearTimeout");
  try {
    await act(async () => {
      mockSalesScreenProps.onManualInputFocusChange(true);
      mockSalesScreenProps.onManualInputFocusChange(false);
    });

    await screen.unmount();
    expect(clearTimeoutSpy).toHaveBeenCalledTimes(1);

    await act(async () => {
      jest.runOnlyPendingTimers();
    });
  } finally {
    clearTimeoutSpy.mockRestore();
    jest.useRealTimers();
  }
});

test("只有冻结权限摘要包含目录下载权限时才暴露维护入口", async () => {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.Settings.CatalogDownload",
  ];
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  mockSalesScreenProps.onOpenCatalogMaintenance();
  expect(mockRouterPush).toHaveBeenCalledWith("/catalog-maintenance");
  expect(mockSalesScreenProps.onOpenRemoteHistory).toBeUndefined();
  expect(mockSalesScreenProps.onOpenLocalHistory).toBeUndefined();
  expect(mockSalesScreenProps.onOpenSpecialProducts).toBeUndefined();
  expect(mockSalesScreenProps.onOpenInstallments).toBeUndefined();
  expect(mockSalesScreenProps.onOpenSettings).toBeUndefined();
});

test("presenter 初始化失败保留活动收银员，展示可重试诊断并在重试后进入销售", async () => {
  mockCreatePresenter.mockImplementationOnce(() => {
    throw new Error("current cashier is invalid");
  });
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("sales-bootstrap-failed-presenter")).toBeTruthy();
  });
  expect(mockClearActiveCashier).not.toHaveBeenCalled();
  expect(mockRecordApplicationLog).toHaveBeenCalledWith(
    expect.objectContaining({
      category: "sales.bootstrap",
      error: expect.any(Error),
      properties: { stage: "presenter" },
    }),
  );
  expect(screen.getByTestId("status-strip")).toBeTruthy();
  expect(screen.queryByTestId("redirect")).toBeNull();
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(mockBusinessStartupFail).toHaveBeenCalledTimes(1);

  await act(async () => {
    fireEvent.press(screen.getByTestId("sales-bootstrap-retry"));
  });
  await waitFor(() => {
    expect(screen.getByTestId("sales-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledTimes(2);
});

test("普通支付恢复检查异常保留会话并显示对应的可重试诊断", async () => {
  mockHasRecoveryRequired.mockRejectedValueOnce(
    new Error("payment recovery storage unavailable"),
  );
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(
      screen.getByTestId("sales-bootstrap-failed-payment-recovery"),
    ).toBeTruthy();
  });
  expect(mockClearActiveCashier).not.toHaveBeenCalled();
  expect(mockRecordApplicationLog).toHaveBeenCalledWith(
    expect.objectContaining({
      category: "sales.bootstrap",
      error: expect.any(Error),
      properties: { stage: "payment-recovery" },
    }),
  );
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  expect(screen.queryByTestId("redirect")).toBeNull();
});

test("分期恢复检查异常保留会话并显示对应的可重试诊断", async () => {
  mockInstallmentHasRecoveryRequired.mockRejectedValueOnce(
    new Error("installment recovery storage unavailable"),
  );
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(
      screen.getByTestId("sales-bootstrap-failed-installment-recovery"),
    ).toBeTruthy();
  });
  expect(mockClearActiveCashier).not.toHaveBeenCalled();
  expect(mockRecordApplicationLog).toHaveBeenCalledWith(
    expect.objectContaining({
      category: "sales.bootstrap",
      error: expect.any(Error),
      properties: { stage: "installment-recovery" },
    }),
  );
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  expect(screen.queryByTestId("redirect")).toBeNull();
});

test("恢复检查等待期间会话失效时立即回登录，迟到异常不再覆盖路由状态", async () => {
  let rejectRecovery!: (reason: unknown) => void;
  mockHasRecoveryRequired.mockImplementationOnce(
    () =>
      new Promise<boolean>((_resolve, reject) => {
        rejectRecovery = reject;
      }),
  );
  const screen = await render(<SalesRoute />);
  await waitFor(() => {
    expect(mockHasRecoveryRequired).toHaveBeenCalledTimes(1);
  });

  mockActiveCashier = null;
  await screen.rerender(<SalesRoute />);
  expect(screen.getByTestId("redirect").props.children).toBe("/login");

  await act(async () => {
    rejectRecovery(new Error("late recovery failure"));
    await Promise.resolve();
  });
  expect(mockRecordApplicationLog).not.toHaveBeenCalled();
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("销售直链没有收银员会话时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<SalesRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

test("检测到冷恢复时先转到支付页，不创建销售 presenter", async () => {
  mockUpdateGate = {
    state: "force-update",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  };
  mockHasRecoveryRequired.mockResolvedValue(true);
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(mockRouterReplace).toHaveBeenCalledWith("/payment");
  });
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  expect(mockInstallmentHasRecoveryRequired).not.toHaveBeenCalled();
  expect(screen.getByTestId("bootstrap")).toBeTruthy();
});

test("销售页强制升级入口只触发 orchestrator，不在 route 内旁路打开 App Store", async () => {
  mockUpdateGate = {
    state: "force-update",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  };
  mockUpdatePolicy = {
    ...mockUpdatePolicy,
    appStoreId: "123456789",
    bundleIdentifier: "com.hotbargain.pos.handheld",
    distribution: "app-store",
    downloadUrl: "https://apps.apple.com/au/app/hb-pos/id123456789",
    latestBuild: "110",
    latestVersion: "1.1.0",
    policyVersion: "ios-required-110",
    required: true,
    state: "required",
  };
  await render(<SalesRoute />);
  await waitFor(() => {
    expect(mockSalesScreenProps).not.toBeNull();
  });

  await act(async () => {
    mockSalesScreenProps.onOpenRequiredUpdate();
    await Promise.resolve();
  });
  expect(mockPerformSelectedUpdate).toHaveBeenCalledTimes(1);
});

test("普通支付稳定后检测到分期恢复时转到统一支付页", async () => {
  mockInstallmentHasRecoveryRequired.mockResolvedValue(true);
  const screen = await render(<SalesRoute />);

  await waitFor(() => {
    expect(mockRouterReplace).toHaveBeenCalledWith("/payment");
  });
  expect(mockHasRecoveryRequired).toHaveBeenCalledTimes(1);
  expect(mockInstallmentHasRecoveryRequired).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).not.toHaveBeenCalled();
  expect(screen.getByTestId("bootstrap")).toBeTruthy();
});

function readyRuntime() {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      apiBaseUrl: "https://pos.example.test/api",
      catalog: {
        findExact: mockCatalogFindExact,
      },
      fulfilment: {
        reprint: {
          status: "available",
          execute: mockReprintReceipt,
        },
        openCashDrawer: {
          status: "available",
          execute: mockOpenCashDrawer,
        },
      },
      sales: { createPresenter: mockCreatePresenter },
      payments: {
        status: "available",
        createPresenter: jest.fn(),
        hasRecoveryRequired: mockHasRecoveryRequired,
      },
      installments: {
        createPresenter: jest.fn(),
        prepareCreateCheckout: jest.fn(),
        createCheckoutPresenter: jest.fn(),
        hasRecoveryRequired: mockInstallmentHasRecoveryRequired,
      },
      scanner: {
        router: {
          acceptCameraText: jest.fn(),
          acquireContext: jest.fn(() => () => undefined),
          startCamera: jest.fn(),
          stopCamera: jest.fn(),
        },
      },
      applicationLog: {
        record: mockRecordApplicationLog,
      },
      appUpdates: {
        getGate: () => mockUpdateGate,
        getPolicy: () => mockUpdatePolicy,
        performSelectedUpdate: mockPerformSelectedUpdate,
        subscribe: (listener: (gate: unknown) => void) => {
          listener(mockUpdateGate);
          return () => undefined;
        },
      },
    },
  };
}

function assertUtilityResult(
  result: Readonly<{ kind: string }>,
  expectedKind: string,
): void {
  expect(result).toEqual({ kind: expectedKind });
}
