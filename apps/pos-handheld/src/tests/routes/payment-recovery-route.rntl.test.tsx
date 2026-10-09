import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import PaymentRecoveryRoute from "../../../app/payment-recovery";

jest.mock("react-i18next", () => ({ useTranslation: () => ({ i18n: { language: "en" } }) }));

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockPark = jest.fn<() => Promise<void>>();
const mockList = jest.fn<() => Promise<readonly unknown[]>>();
const mockRouterDismissTo = jest.fn();
const mockRouterReplace = jest.fn();
const mockRouterPush = jest.fn();

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    useRouter: () => ({
      dismissTo: mockRouterDismissTo,
      push: mockRouterPush,
      replace: mockRouterReplace,
    }),
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

jest.mock("@/features/payment-recovery/payment-recovery-screen", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } = jest.requireActual<typeof import("react-native")>("react-native");
  return { PaymentRecoveryScreen: (props: unknown) => {
    mockScreenProps = props;
    return React.createElement(Text, { testID: "recovery-screen" }, "recovery");
  }};
});
jest.mock("@/features/payments/runtime/payment-checkout-runtime", () => ({
  PAYMENT_PERMISSION: { view: "Permissions.PosTerminal.Payment.View" },
}));
jest.mock("@/ui/screens/bootstrap-screen", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } = jest.requireActual<typeof import("react-native")>("react-native");
  return { BootstrapScreen: () => React.createElement(Text, { testID: "bootstrap" }, "bootstrap") };
});
beforeEach(() => {
  jest.clearAllMocks();
  mockScreenProps = null;
  mockPark.mockResolvedValue(undefined);
  mockList.mockResolvedValue([]);
  mockActiveCashier = { cashierId: "C1", cashierName: "Cashier", storeCode: "S1", deviceCode: "HH-1",
    permissions: ["Permissions.PosTerminal.Payment.View"], source: "online" };
  mockRuntime = { state: { phase: "ready", backend: "reachable", device: "authorized-online" },
    services: { deviceSession: { getDeviceIdentity: async () => ({ storeCode: "S1", deviceCode: "HH-1" }) },
      payments: { recoveryCenter: { list: mockList, parkCurrent: mockPark,
        recoverOriginalPayment: async () => undefined, submitManualVerification: async () => undefined } } } };
});
test("保存异常并释放购物车成功后才返回下一单", async () => {
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.onBack();
  expect(mockPark).toHaveBeenCalledTimes(1);
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");
});
test("保存失败留在恢复中心，不导航或丢失当前交易", async () => {
  mockPark.mockRejectedValue(new Error("DISK_FULL"));
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.onBack();
  expect(mockRouterDismissTo).not.toHaveBeenCalled();
  expect(mockScreenProps.service.getState().errorCode).toBe("RECOVERY_ACTION_FAILED");
});
test("离线仍允许查看和保留本机异常", async () => {
  mockRuntime.state = { phase: "ready-offline", backend: "offline", device: "authorized-local" };
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
});
test("没有付款查看权限时不创建恢复服务", async () => {
  mockActiveCashier.permissions = [];
  const screen = await render(<PaymentRecoveryRoute />);
  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  expect(mockScreenProps).toBeNull();
});
test("设备作用域变化清除收银会话", async () => {
  mockRuntime.services.deviceSession.getDeviceIdentity = async () => ({ storeCode: "OTHER", deviceCode: "HH-1" });
  await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(mockClearActiveCashier).toHaveBeenCalledTimes(1));
  expect(mockScreenProps).toBeNull();
});
test("服务不可用时显示保留异常提示", async () => {
  delete mockRuntime.services.payments.recoveryCenter;
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.queryByTestId("bootstrap")).toBeNull());
  expect(mockScreenProps).toBeNull();
  expect(mockPark).not.toHaveBeenCalled();
});

test("恢复原单成功后打开付款页", async () => {
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.service.recoverOriginalPayment("record-1");
  expect(mockRouterPush).toHaveBeenCalledWith("/payment");
});

test("首次读取移交当前异常，后续刷新不重复清车", async () => {
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.service.refresh();
  await mockScreenProps.service.refresh();
  expect(mockPark).toHaveBeenCalledTimes(1);
  expect(mockList).toHaveBeenCalledTimes(2);
});
test("首次移交失败可重试且不会先读取假空列表", async () => {
  mockPark.mockRejectedValueOnce(new Error("DISK_FULL"));
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.service.refresh();
  expect(mockList).not.toHaveBeenCalled();
  await mockScreenProps.service.refresh();
  expect(mockPark).toHaveBeenCalledTimes(2);
  expect(mockList).toHaveBeenCalledTimes(1);
});
test("人工已收款补齐订单后留在恢复中心，不打开空付款页", async () => {
  mockRuntime.services.payments.recoveryCenter.recoverOriginalPayment = async () => "completed";
  const screen = await render(<PaymentRecoveryRoute />);
  await waitFor(() => expect(screen.getByTestId("recovery-screen")).toBeTruthy());
  await mockScreenProps.service.recoverOriginalPayment("record-1");
  expect(mockRouterPush).not.toHaveBeenCalled();
});
