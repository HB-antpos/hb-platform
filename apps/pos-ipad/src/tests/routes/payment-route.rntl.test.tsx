import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";

import PaymentRoute from "../../../app/payment";

import type { PosAuthorizedFulfilmentActionResult } from "@/core/runtime/production-pos-service-composition";
import { takePendingSalesScan } from "@/ui/scanner/pending-scan-handoff";

let mockRuntime: any;
let mockActiveCashier: any;
let mockParams: any;
let mockPaymentScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreateRegularPresenter = jest.fn();
const mockCreateInstallmentPresenter = jest.fn();
const mockPrepareInstallmentCreate = jest.fn();
const mockRegularRecoveryRequired = jest.fn<() => Promise<boolean>>();
const mockInstallmentRecoveryRequired =
  jest.fn<() => Promise<boolean>>();
const mockDestroyRegularPresenter = jest.fn();
const mockDestroyInstallmentPresenter = jest.fn();
const mockRouterDismissTo = jest.fn();
const mockReprintCurrentReceipt = jest.fn<
  (orderGuid: string) => Promise<PosAuthorizedFulfilmentActionResult>
>();
let mockRegularPresenters: any[];

let mockBleScanOptions: any;
let mockHidCaptureProps: any;
// 扫码路由桥依赖真实导航容器；路由测试只关心页面把码交给了谁，所以替换成可调用的 mock。
jest.mock("@/ui/scanner/scanner-route-bridge", () => ({
  RouteHidScannerCapture: (props: unknown) => {
    mockHidCaptureProps = props;
    return null;
  },
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
    // Expo Router 每次 render 都会 Object.fromEntries；返回新对象以锁住 effect 稳定性。
    useLocalSearchParams: () =>
      Object.fromEntries(
        Object.entries(mockParams).map(([key, value]) => [
          key,
          Array.isArray(value) ? [...value] : value,
        ]),
      ),
    useRouter: () => ({ dismissTo: mockRouterDismissTo }),
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
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
    }),
}));

jest.mock("@/features/installments", () => ({
  INSTALLMENTS_CREATE_PERMISSION:
    "Permissions.PosTerminal.Installments.Create",
  resolveInstallmentsRuntimeFactory: (services: any) => {
    const candidate = services.installments;
    return candidate &&
      typeof candidate.prepareCreateCheckout === "function" &&
      typeof candidate.createCheckoutPresenter === "function" &&
      typeof candidate.hasRecoveryRequired === "function"
      ? candidate
      : null;
  },
}));

jest.mock("@/features/payments/ui", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  const actual =
    jest.requireActual<typeof import("@/features/payments/ui")>(
      "@/features/payments/ui",
    );
  return {
    ...actual,
    resolvePaymentLocale: () => "en",
    PaymentScreen: (props: unknown) => {
      mockPaymentScreenProps = props;
      return React.createElement(
        Text,
        { testID: "payment-screen" },
        "payment",
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
  mockParams = {
    checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
    revision: "7",
    totalCents: "1250",
  };
  mockPaymentScreenProps = null;
  mockActiveCashier = {
    cashierId: "C1",
    cashierName: "Cashier",
    userGuid: "user-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
  };
  mockRegularRecoveryRequired.mockResolvedValue(false);
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  mockReprintCurrentReceipt.mockResolvedValue({
    state: "Printed",
    errorCode: null,
  });
  mockPrepareInstallmentCreate.mockReturnValue({
    kind: "installment-create",
    checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
    expectedCartRevision: 7,
  });
  mockRegularPresenters = [];
  mockCreateRegularPresenter.mockImplementation(() => {
    const presenter = createMockPresenter(mockDestroyRegularPresenter);
    mockRegularPresenters.push(presenter);
    return presenter;
  });
  mockCreateInstallmentPresenter.mockImplementation(() =>
    createMockPresenter(mockDestroyInstallmentPresenter),
  );
  mockRuntime = readyRuntime();
});

test("普通交易经统一 facade 只传最小 checkout entry", async () => {
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockRegularRecoveryRequired).toHaveBeenCalledTimes(1);
  expect(mockInstallmentRecoveryRequired).toHaveBeenCalledTimes(1);
  expect(mockCreateRegularPresenter).toHaveBeenCalledWith({
    kind: "regular",
    checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
    expectedCartRevision: 7,
    total: { currency: "AUD", cents: 1_250 },
    lines: [],
  });
  expect(mockCreateRegularPresenter).toHaveBeenCalledTimes(1);
  expect(mockRuntime.services.payments.attempts).toBeUndefined();
  expect(mockRuntime.services.payments.providers).toBeUndefined();

  mockPaymentScreenProps.onBack();
  mockPaymentScreenProps.onComplete("order-not-used-by-route");
  expect(mockRouterDismissTo).toHaveBeenNthCalledWith(1, "/sales");
  expect(mockRouterDismissTo).toHaveBeenNthCalledWith(2, "/sales");
});

test.each([
  ["Printed", "completed"],
  ["Ambiguous", "unknown"],
  ["recovery-required", "unknown"],
  ["Completed", "failed"],
  ["Unknown", "failed"],
  ["Failed", "failed"],
] as const)("成功页打印把 %s 映射为 %s 并传入精确订单号", async (state, expected) => {
  mockReprintCurrentReceipt.mockResolvedValueOnce({
    state,
    errorCode: state === "Printed" ? null : "PRINT_RESULT",
  });
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(mockPaymentScreenProps.onPrintReceipt).toEqual(
      expect.any(Function),
    );
  });
  await expect(
    mockPaymentScreenProps.onPrintReceipt("order-guid-exact"),
  ).resolves.toBe(expected);
  expect(mockReprintCurrentReceipt).toHaveBeenCalledTimes(1);
  expect(mockReprintCurrentReceipt).toHaveBeenCalledWith("order-guid-exact");
  await screen.unmount();
});

test("运行时缺少当前订单重打回调时成功页保持打印禁用", async () => {
  delete mockRuntime.services.fulfilment.reprintCurrentReceipt;
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockPaymentScreenProps.onPrintReceipt).toBeUndefined();
  expect(mockReprintCurrentReceipt).not.toHaveBeenCalled();
  await screen.unmount();
});

test("新建分期和续付参数分别创建对应统一支付 presenter", async () => {
  mockParams = {
    flow: "installment-create",
    checkoutIntentId: "223e4567-e89b-42d3-a456-426614174000",
    revision: "8",
  };
  const create = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(create.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateInstallmentPresenter).toHaveBeenCalledWith({
    kind: "installment-create",
    checkoutIntentId: "223e4567-e89b-42d3-a456-426614174000",
    expectedCartRevision: 8,
  });
  await create.unmount();

  jest.clearAllMocks();
  mockRegularRecoveryRequired.mockResolvedValue(false);
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  mockCreateInstallmentPresenter.mockImplementation(() =>
    createMockPresenter(mockDestroyInstallmentPresenter),
  );
  mockParams = {
    flow: "installment-repayment",
    installmentGuid: "323e4567-e89b-42d3-a456-426614174000",
  };
  const repayment = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(repayment.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateInstallmentPresenter).toHaveBeenCalledWith({
    kind: "installment-repayment",
    installmentGuid: "323e4567-e89b-42d3-a456-426614174000",
  });
  await repayment.unmount();
});

test("两账本同时阻塞时固定优先恢复普通支付", async () => {
  mockRegularRecoveryRequired.mockResolvedValue(true);
  mockInstallmentRecoveryRequired.mockResolvedValue(true);
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateRegularPresenter).toHaveBeenCalledWith(null);
  expect(mockCreateInstallmentPresenter).not.toHaveBeenCalled();
});

test("只有分期阻塞时恢复分期账本，无恢复且无参数则返回销售页", async () => {
  mockParams = {};
  mockInstallmentRecoveryRequired.mockResolvedValue(true);
  const recovery = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(recovery.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateInstallmentPresenter).toHaveBeenCalledWith(null);
  expect(mockCreateRegularPresenter).not.toHaveBeenCalled();
  await recovery.unmount();

  jest.clearAllMocks();
  mockRegularRecoveryRequired.mockResolvedValue(false);
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  const none = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(none.getByTestId("redirect").props.children).toBe("/sales");
  });
  expect(mockCreateRegularPresenter).not.toHaveBeenCalled();
  expect(mockCreateInstallmentPresenter).not.toHaveBeenCalled();
});

test.each([
  [
    "未知参数",
    {
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "7",
      totalCents: "1250",
      injected: "true",
    },
  ],
  [
    "数组参数",
    {
      flow: ["regular", "installment-create"],
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "7",
      totalCents: "1250",
    },
  ],
  [
    "不安全整数",
    {
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      revision: "9007199254740992",
      totalCents: "1250",
    },
  ],
  [
    "非 UUID",
    {
      flow: "installment-repayment",
      installmentGuid: "../../payment",
    },
  ],
])("严格拒绝%s并且不读取任何恢复账本", async (_label, params) => {
  mockParams = params;
  const screen = await render(<PaymentRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockRegularRecoveryRequired).not.toHaveBeenCalled();
  expect(mockInstallmentRecoveryRequired).not.toHaveBeenCalled();
  expect(mockCreateRegularPresenter).not.toHaveBeenCalled();
  expect(mockCreateInstallmentPresenter).not.toHaveBeenCalled();
});

test("两条 capability 独立降级，不因另一账本缺失禁用可用入口", async () => {
  delete mockRuntime.services.installments;
  const regular = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(regular.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateRegularPresenter).toHaveBeenCalledTimes(1);
  await regular.unmount();

  jest.clearAllMocks();
  mockParams = {
    flow: "installment-create",
    checkoutIntentId: "223e4567-e89b-42d3-a456-426614174000",
    revision: "8",
  };
  mockRuntime = readyRuntime();
  mockRuntime.services.payments = {
    status: "unavailable",
    blockers: ["PAYMENTS_DISABLED"],
  };
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  const installment = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(installment.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockCreateInstallmentPresenter).toHaveBeenCalledTimes(1);
  await installment.unmount();
});

test("普通支付可切换为分期并恢复一份新的普通 presenter", async () => {
  grantInstallmentModePermissions();
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
      issue: null,
    });
  });

  await act(async () => {
    mockPaymentScreenProps.installmentModeControl.onToggle(true);
  });
  await waitFor(() => {
    expect(mockCreateInstallmentPresenter).toHaveBeenCalledWith({
      kind: "installment-create",
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      expectedCartRevision: 7,
    });
    expect(mockDestroyRegularPresenter).toHaveBeenCalledTimes(1);
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: true,
      locked: false,
      issue: null,
    });
  });

  await act(async () => {
    mockPaymentScreenProps.installmentModeControl.onToggle(false);
  });
  await waitFor(() => {
    expect(mockDestroyInstallmentPresenter).toHaveBeenCalledTimes(1);
    expect(mockCreateRegularPresenter).toHaveBeenCalledTimes(2);
    expect(mockCreateRegularPresenter).toHaveBeenLastCalledWith({
      kind: "regular",
      checkoutIntentId: "123e4567-e89b-42d3-a456-426614174000",
      expectedCartRevision: 7,
      total: { currency: "AUD", cents: 1_250 },
      lines: [],
    });
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
      issue: null,
    });
  });

  await screen.unmount();
});

test.each([
  ["缺少分期新建权限", ["Permissions.PosTerminal.Payment.View", "Permissions.PosTerminal.Payment.Confirm"]],
  ["缺少付款查看权限", ["Permissions.PosTerminal.Installments.Create", "Permissions.PosTerminal.Payment.Confirm"]],
  ["缺少付款确认权限", ["Permissions.PosTerminal.Installments.Create", "Permissions.PosTerminal.Payment.View"]],
])("普通支付%s时不显示分期开关", async (_label, permissions) => {
  mockActiveCashier.permissions = permissions;
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockPaymentScreenProps.installmentModeControl).toBeUndefined();
  await screen.unmount();
});

test("分期运行时不可用时普通支付不显示分期开关", async () => {
  grantInstallmentModePermissions();
  delete mockRuntime.services.installments;
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockPaymentScreenProps.installmentModeControl).toBeUndefined();
  await screen.unmount();
});

test("分期还款和无普通 fallback 的分期新建均保持开启且锁定", async () => {
  mockParams = {
    flow: "installment-repayment",
    installmentGuid: "323e4567-e89b-42d3-a456-426614174000",
  };
  const repayment = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: true,
      locked: true,
      issue: null,
    });
  });
  await repayment.unmount();

  jest.clearAllMocks();
  mockParams = {
    flow: "installment-create",
    checkoutIntentId: "223e4567-e89b-42d3-a456-426614174000",
    revision: "8",
  };
  mockRegularRecoveryRequired.mockResolvedValue(false);
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  mockPrepareInstallmentCreate.mockReturnValue({
    kind: "installment-create",
    checkoutIntentId: "223e4567-e89b-42d3-a456-426614174000",
    expectedCartRevision: 8,
  });
  mockCreateRegularPresenter.mockImplementation(() => createMockPresenter(mockDestroyRegularPresenter));
  mockCreateInstallmentPresenter.mockImplementation(() => createMockPresenter(mockDestroyInstallmentPresenter));
  const create = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: true,
      locked: true,
      issue: null,
    });
  });
  await create.unmount();
});

test("恢复支付隐藏分期开关，进入分期失败仍停在普通支付并返回稳定问题", async () => {
  grantInstallmentModePermissions();
  mockRegularRecoveryRequired.mockResolvedValue(true);
  const recovery = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(recovery.getByTestId("payment-screen")).toBeTruthy();
  });
  expect(mockPaymentScreenProps.installmentModeControl).toBeUndefined();
  await recovery.unmount();

  jest.clearAllMocks();
  mockRegularRecoveryRequired.mockResolvedValue(false);
  mockInstallmentRecoveryRequired.mockResolvedValue(false);
  mockPrepareInstallmentCreate.mockImplementation(() => {
    throw new Error("installment runtime unavailable");
  });
  mockCreateRegularPresenter.mockImplementation(() => createMockPresenter(mockDestroyRegularPresenter));
  mockCreateInstallmentPresenter.mockImplementation(() => createMockPresenter(mockDestroyInstallmentPresenter));
  const screen = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
      issue: null,
    });
  });

  await act(async () => {
    mockPaymentScreenProps.installmentModeControl.onToggle(true);
  });
  await waitFor(() => {
    expect(mockCreateInstallmentPresenter).not.toHaveBeenCalled();
    expect(mockDestroyRegularPresenter).not.toHaveBeenCalled();
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
      issue: "unavailable",
    });
  });
  await screen.unmount();
});

test("支付方尝试、恢复或提交阶段锁定分期开关", async () => {
  grantInstallmentModePermissions();
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
    });
  });

  for (const state of [
    { attemptId: "attempt-1", phase: "awaiting-terminal" },
    {
      allowedActions: { recover: true },
      attemptId: null,
      phase: "recovery-required",
    },
    {
      allowedActions: { recover: false },
      attemptId: null,
      busy: true,
      phase: "submitting",
    },
  ]) {
    await act(async () => {
      mockRegularPresenters[0].setState(state);
    });
    await waitFor(() => {
      expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
        enabled: false,
        locked: true,
      });
    });
  }
  await act(async () => {
    mockPaymentScreenProps.installmentModeControl.onToggle(true);
  });
  expect(mockCreateInstallmentPresenter).not.toHaveBeenCalled();

  await act(async () => {
    mockRegularPresenters[0].setState({
      initialized: true,
      allowedActions: { recover: false },
      attemptId: null,
      orderGuid: null,
      busy: false,
      phase: "recovery-required",
    });
  });
  await waitFor(() => {
    expect(mockPaymentScreenProps.installmentModeControl).toMatchObject({
      enabled: false,
      locked: false,
    });
  });

  await screen.unmount();
});

test("未登录直链返回登录，且不会读取支付运行时", async () => {
  mockActiveCashier = null;
  const screen = await render(<PaymentRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
  expect(mockRegularRecoveryRequired).not.toHaveBeenCalled();
  expect(mockInstallmentRecoveryRequired).not.toHaveBeenCalled();
  expect(mockCreateRegularPresenter).not.toHaveBeenCalled();
});

test("请求的账本不可用时 fail closed 回销售页", async () => {
  mockRuntime.services.payments = {
    status: "unavailable",
    blockers: ["PAYMENTS_DISABLED"],
  };
  const screen = await render(<PaymentRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  });
  expect(mockCreateRegularPresenter).not.toHaveBeenCalled();
});

function readyRuntime() {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      payments: {
        status: "available",
        createPresenter: mockCreateRegularPresenter,
        hasRecoveryRequired: mockRegularRecoveryRequired,
      },
      installments: {
        prepareCreateCheckout: mockPrepareInstallmentCreate,
        createCheckoutPresenter: mockCreateInstallmentPresenter,
        hasRecoveryRequired: mockInstallmentRecoveryRequired,
      },
      fulfilment: {
        reprintCurrentReceipt: mockReprintCurrentReceipt,
      },
    },
  };
}

function grantInstallmentModePermissions(): void {
  mockActiveCashier.permissions = [
    "Permissions.PosTerminal.Installments.Create",
    "Permissions.PosTerminal.Payment.View",
    "Permissions.PosTerminal.Payment.Confirm",
  ];
}

function createMockPresenter(destroy: jest.Mock): any {
  let state = {
    phase: "ready",
    busy: false,
    initialized: true,
    attemptId: null,
    orderGuid: null,
    allowedActions: { recover: false },
  };
  const listeners = new Set<() => void>();
  return {
    destroy,
    getState: () => state,
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setState: (patch: Record<string, unknown>) => {
      state = { ...state, ...patch };
      listeners.forEach((listener) => listener());
    },
  };
}

test("付款成功页扫到商品码：交给收银页自动加购并直接回收银页；支付进行中不启用扫码捕获", async () => {
  takePendingSalesScan();
  const screen = await render(<PaymentRoute />);
  await waitFor(() => {
    expect(screen.getByTestId("payment-screen")).toBeTruthy();
  });

  // 支付进行中有金额 / 礼券输入框，隐藏的扫码输入框不能抢焦点。
  expect(mockHidCaptureProps.path).toBe("/payment");
  expect(mockHidCaptureProps.context).toBe("product");
  expect(mockHidCaptureProps.enabled).toBe(false);

  await act(async () => {
    mockRegularPresenters[0].setState({ phase: "success", orderGuid: "order-1" });
  });
  expect(mockHidCaptureProps.enabled).toBe(true);

  const onScan = mockHidCaptureProps.onScan;
  onScan("9313559558795", "ble");
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
  expect(takePendingSalesScan()).toMatchObject({
    value: "9313559558795",
    source: "ble",
  });
  // 回调引用在重渲染间保持稳定，否则每次渲染都会释放 / 重新申请扫码租约。
  await act(async () => {
    mockRegularPresenters[0].setState({ busy: false });
  });
  expect(mockHidCaptureProps.onScan).toBe(onScan);
  await screen.unmount();
});
