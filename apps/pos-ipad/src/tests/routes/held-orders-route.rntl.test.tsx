import { beforeEach, expect, jest, test } from "@jest/globals";
import { render, waitFor } from "@testing-library/react-native";

import HeldOrdersRoute from "../../../app/(pos)/held-orders";

import type { SharedHeldOrdersViewPort } from "@/features/held-orders";
import {
  SharedHeldOrderCoordinatorError,
  type SharedHeldOrderTakeResult,
} from "@/features/shared-held-orders/shared-held-order-coordinator";
import type { SharedHeldOrderPendingListItem } from "@/features/shared-held-orders/shared-held-order-network-api";

let mockRuntime: any;
let mockActiveCashier: any;
let mockScreenProps: any;
const mockClearActiveCashier = jest.fn();
const mockCreatePresenter = jest.fn();
const mockAttachSharedOrders = jest.fn();
const mockDestroyPresenter = jest.fn();
const mockListPending = jest.fn(
  async (): Promise<SharedHeldOrderPendingListItem[]> => [],
);
const mockTakeRemoteHold = jest.fn(
  async (holdGuid: string): Promise<SharedHeldOrderTakeResult> => ({
    outcome: "restored",
    claimGuid: "claim-1",
    holdGuid,
  }),
);
const mockRecallLocalPublication = jest.fn(
  async (holdGuid: string): Promise<SharedHeldOrderTakeResult> => ({
    outcome: "restored",
    claimGuid: "claim-1",
    holdGuid,
  }),
);
const mockOwnerRelease = jest.fn(async (holdGuid: string) => ({
  claimGuid: "claim-1",
  holdGuid,
}));
const mockCancelOwnedHold = jest.fn(async (_holdGuid: string) => undefined);
const mockListLocalShareState = jest.fn(async () => [
  { holdId: "local-1", shareState: "Published" as const, blockReason: null },
]);
const mockRequestShare = jest.fn(async (_holdGuid: string) => "requested" as const);
const mockForceRelease = jest.fn(async (holdGuid: string, reason: string) => ({
  ok: true as const,
  code: "force-released" as const,
  holdId: holdGuid,
  reason,
}));
const mockCreateCoordinator = jest.fn(() => ({
  takeRemoteHold: mockTakeRemoteHold,
  recallLocalPublication: mockRecallLocalPublication,
  ownerRelease: mockOwnerRelease,
  cancelOwnedHold: mockCancelOwnedHold,
  forceRelease: mockForceRelease,
  requestShare: mockRequestShare,
}));
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

jest.mock("@/features/held-orders", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    HeldOrdersScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(
        Text,
        { testID: "held-orders-screen" },
        "held orders",
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
    userGuid: "user-1",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
  };
  mockCreatePresenter.mockReturnValue({
    destroy: mockDestroyPresenter,
    attachSharedOrders: mockAttachSharedOrders,
  });
  mockListPending.mockResolvedValue([]);
  mockListLocalShareState.mockResolvedValue([
    { holdId: "local-1", shareState: "Published", blockReason: null },
  ]);
  mockRuntime = readyRuntime();
});

test("挂单路由零参数创建 presenter，返回销售且卸载时销毁", async () => {
  const screen = await render(<HeldOrdersRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("held-orders-screen")).toBeTruthy();
  });
  expect(mockCreatePresenter).toHaveBeenCalledTimes(1);
  expect(mockCreatePresenter).toHaveBeenCalledWith();
  expect(mockCreateCoordinator).toHaveBeenCalledTimes(1);
  expect(mockAttachSharedOrders).toHaveBeenCalledTimes(1);
  const port = mockAttachSharedOrders.mock
    .calls[0]?.[0] as SharedHeldOrdersViewPort;
  expect(port).toBeDefined();
  expect(typeof port.listRemotePending).toBe("function");
  expect(typeof port.listLocalShareState).toBe("function");
  expect(typeof port.takeRemoteHold).toBe("function");
  expect(typeof port.recallLocalPublication).toBe("function");
  expect(typeof port.releaseOwnedClaim).toBe("function");
  expect(typeof port.cancelOwnedHold).toBe("function");
  expect(typeof port.forceRelease).toBe("function");
  expect(typeof port.requestShare).toBe("function");

  mockScreenProps.onBack();
  expect(mockRouterDismissTo).toHaveBeenCalledWith("/sales");

  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("presenter 创建失败时清除收银会话并安全返回登录", async () => {
  mockCreatePresenter.mockImplementationOnce(() => {
    throw new Error("current cashier is invalid");
  });
  const screen = await render(<HeldOrdersRoute />);

  await waitFor(() => {
    expect(mockClearActiveCashier).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("redirect").props.children).toBe("/login");
  });
  expect(mockCreatePresenter).toHaveBeenCalledWith();
});

test("挂单路由把远端列表投影为视图行并把 coordinator 取单结果传给 presenter", async () => {
  mockListPending.mockResolvedValue([
    {
      holdGuid: "remote-1",
      storeCode: "BNE",
      deviceCode: "IPAD-2",
      heldByCashierId: "C2",
      heldByCashierName: "Other Cashier",
      heldAtIso: "2026-07-28T02:00:00.000Z",
      updatedAtIso: "2026-07-28T02:00:00.000Z",
      lineCount: 3,
      totalCents: 3_300,
      discountCents: 0,
      actualCents: 3_300,
      revision: 1,
    },
  ]);
  mockTakeRemoteHold.mockResolvedValue({
    outcome: "restored",
    claimGuid: "claim-1",
    holdGuid: "remote-1",
  });
  const screen = await render(<HeldOrdersRoute />);

  await waitFor(() => {
    expect(screen.getByTestId("held-orders-screen")).toBeTruthy();
  });
  const port = mockAttachSharedOrders.mock
    .calls[0]?.[0] as SharedHeldOrdersViewPort;
  expect(port).toBeDefined();
  await expect(port.listRemotePending()).resolves.toEqual([
    {
      holdGuid: "remote-1",
      deviceCode: "IPAD-2",
      cashierName: "Other Cashier",
      heldAtIso: "2026-07-28T02:00:00.000Z",
      lineCount: 3,
      actualCents: 3_300,
    },
  ]);
  await expect(port.takeRemoteHold("remote-1")).resolves.toEqual({
    holdGuid: "remote-1",
    ok: true,
    outcome: "restored",
  });
  expect(mockTakeRemoteHold).toHaveBeenCalledWith("remote-1");
  await expect(port.recallLocalPublication("remote-1")).resolves.toEqual({
    holdGuid: "remote-1",
    ok: true,
    outcome: "restored",
  });
  expect(mockRecallLocalPublication).toHaveBeenCalledWith("remote-1");
  await expect(port.releaseOwnedClaim?.("remote-1")).resolves.toBe(true);
  expect(mockOwnerRelease).toHaveBeenCalledWith("remote-1");
  mockOwnerRelease.mockRejectedValueOnce(
    new SharedHeldOrderCoordinatorError("NOT_FOUND", "not shared"),
  );
  await expect(port.releaseOwnedClaim?.("legacy-1")).resolves.toBe(false);
  await expect(port.cancelOwnedHold?.("remote-1")).resolves.toBeUndefined();
  expect(mockCancelOwnedHold).toHaveBeenCalledWith("remote-1");
  await expect(port.listLocalShareState?.()).resolves.toEqual([
    { holdId: "local-1", shareState: "Published", blockReason: null },
  ]);
  await expect(port.requestShare?.("local-1")).resolves.toBe("requested");
  expect(mockRequestShare).toHaveBeenCalledWith("local-1");
  await expect(
    port.forceRelease?.({ holdGuid: "remote-1", reason: "duplicate" }),
  ).resolves.toMatchObject({
    ok: true,
    code: "force-released",
    holdId: "remote-1",
  });
  expect(mockForceRelease).toHaveBeenCalledWith("remote-1", "duplicate");

  await screen.unmount();
  expect(mockDestroyPresenter).toHaveBeenCalledTimes(1);
});

test("没有收银员会话时返回登录页", async () => {
  mockActiveCashier = null;
  const screen = await render(<HeldOrdersRoute />);

  expect(screen.getByTestId("redirect").props.children).toBe("/login");
  expect(mockCreatePresenter).not.toHaveBeenCalled();
});

function readyRuntime() {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      heldOrders: { createPresenter: mockCreatePresenter },
      sharedHeldOrders: {
        api: { listPending: mockListPending },
        listLocalShareState: mockListLocalShareState,
        requestShare: mockRequestShare,
        createCoordinator: mockCreateCoordinator,
      },
    },
  };
}
