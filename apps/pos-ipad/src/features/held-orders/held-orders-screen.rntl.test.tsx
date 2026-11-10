import { afterEach, beforeEach, expect, jest, test } from "@jest/globals";
import {
  act,
  cleanup,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import {
  AppState,
  StyleSheet,
  type AppStateEvent,
  type AppStateStatus,
} from "react-native";

import {
  HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS,
  HELD_ORDERS_MIN_TOUCH_TARGET,
  HeldOrdersScreen,
} from "./held-orders-screen";

// 页面外壳的顶栏通过 t() 取全局文案，这里原样返回键名即可。
jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
    t: (key: string) => key,
  }),
}));

let appStateListener: ((state: AppStateStatus) => void) | null = null;

beforeEach(() => {
  appStateListener = null;
  jest.spyOn(AppState, "addEventListener").mockImplementation(
    (eventName: AppStateEvent, handler: (state: AppStateStatus) => void) => {
      if (eventName === "change") {
        appStateListener = handler;
      }
      return { remove: jest.fn() } as never;
    },
  );
});

afterEach(async () => {
  jest.useRealTimers();
  jest.restoreAllMocks();
  await cleanup();
});

function localSummary(overrides: Record<string, unknown> = {}) {
  return {
    holdId: "hold-1",
    localSequence: 8,
    scope: { storeCode: "BNE", deviceCode: "IPAD-1" },
    heldBy: { cashierId: "C1", cashierName: "Cashier" },
    status: "Pending",
    itemCount: 2,
    subtotalCents: 1_200,
    discountCents: 0,
    actualAmountCents: 1_200,
    heldAtIso: "2026-07-28T01:00:00.000Z",
    recallingAtIso: null,
    ...overrides,
  };
}

function viewRow(overrides: Record<string, unknown> = {}) {
  return {
    holdId: "hold-1",
    local: localSummary(),
    remote: null,
    status: "local-pending",
    blockReason: null,
    ...overrides,
  };
}

function swipeEvent(
  pageX: number,
  startPageX: number,
  pageY = 80,
  startPageY = 80,
  timeStamp = 200,
) {
  return {
    nativeEvent: {
      changedTouches: [{ pageX, pageY }],
      identifier: 0,
      touches: [{ pageX, pageY }],
    },
    touchHistory: {
      indexOfSingleActiveTouch: 0,
      mostRecentTimeStamp: timeStamp,
      numberActiveTouches: 1,
      touchBank: [
        {
          currentPageX: pageX,
          currentPageY: pageY,
          currentTimeStamp: timeStamp,
          previousPageX: startPageX,
          previousPageY: startPageY,
          previousTimeStamp: timeStamp - 100,
          startPageX,
          startPageY,
          startTimeStamp: timeStamp - 100,
          touchActive: true,
        },
      ],
    },
  };
}

function createPresenter(overrides: Record<string, unknown> = {}) {
  const listeners = new Set<() => void>();
  const state: any = overrides.state ?? {
    kind: "ready",
    rows: [],
    busy: false,
    lastAction: null,
    refreshError: null,
    sharedEnabled: false,
    dateFilter: "today",
  };
  let timer: ReturnType<typeof setInterval> | null = null;
  const presenter: any = {
    state,
    getState: () => state,
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    notify: () => {
      for (const listener of [...listeners]) listener();
    },
    refresh: jest.fn(async () => undefined),
    hold: jest.fn(async () => ({ ok: true, code: "held" })),
    recall: jest.fn(async () => ({ ok: true, code: "recalled" })),
    recover: jest.fn(async () => ({ ok: true, code: "recovered" })),
    release: jest.fn(async () => ({ ok: true, code: "released" })),
    delete: jest.fn(async () => ({ ok: true, code: "deleted" })),
    setDateFilter: jest.fn(),
    takeRemote: jest.fn(async () => ({ ok: true, code: "recalled" })),
    recallLocalShared: jest.fn(async () => ({ ok: true, code: "recalled" })),
    requestShare: jest.fn(async () => ({ ok: true, outcome: "requested" })),
    setSourceTab: jest.fn(),
    forceRelease: jest.fn(async () => ({ ok: true, code: "force-released" })),
    supportsForceRelease: () => false,
    startAutoRefresh: jest.fn((intervalMs: number) => {
      if (timer) return;
      timer = setInterval(() => {
        void presenter.refresh();
      }, intervalMs);
    }),
    stopAutoRefresh: jest.fn(() => {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    }),
    ...overrides,
  };
  return presenter;
}

test("挂单列表默认选中今天，并可切换查看全部挂单", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      dateFilter: "today",
      rows: [viewRow()],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  // 筛选改为大分段（单选语义），选中态用 checked 表达。
  expect(
    screen.getByTestId("held-orders-filter-today").props.accessibilityState
      .checked,
  ).toBe(true);
  expect(
    screen.getByTestId("held-orders-filter-all").props.accessibilityState
      .checked,
  ).toBe(false);

  await fireEvent.press(screen.getByTestId("held-orders-filter-all"));

  expect(presenter.setDateFilter).toHaveBeenCalledWith("all");
});

test("挂单列表默认本机分栏，可切换非本机；未请求行显示共享且 Blocked 评估仍可 recall", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      shareBusyHoldIds: [],
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      dateFilter: "today",
      sourceTab: "local",
      rows: [
        viewRow({
          holdId: "share-me",
          shareState: "NeedsEvaluation",
          shareRequestedAtIso: null,
        }),
        viewRow({
          holdId: "blocked-local",
          local: localSummary({ holdId: "blocked-local" }),
          status: "blocked",
          shareState: "Blocked",
          blockReason: "SHARED_CART_INVALID",
        }),
      ],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  expect(screen.getByTestId("held-orders-source-local").props.accessibilityState.checked).toBe(true);
  expect(screen.getByTestId("held-order-share-share-me")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("held-order-share-share-me"));
  expect(presenter.requestShare).toHaveBeenCalledWith("share-me");
  await fireEvent.press(screen.getByTestId("held-order-action-blocked-local"));
  expect(presenter.recall).toHaveBeenCalledWith("blocked-local");

  await fireEvent.press(screen.getByTestId("held-orders-source-other"));
  expect(presenter.setSourceTab).toHaveBeenCalledWith("other");
});

test("横屏挂单列表提供 44pt 操作、显式恢复入口和不含商品详情的行", async () => {
  expect(HELD_ORDERS_MIN_TOUCH_TARGET).toBe(44);
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: false,
      rows: [
        viewRow({
          holdId: "recover-1",
          local: localSummary({
            holdId: "recover-1",
            status: "Recalling",
            recallingAtIso: "2026-07-28T01:01:00.000Z",
          }),
          status: "claiming-here",
        }),
      ],
    },
  });
  const onBack = jest.fn();
  const screen = await render(
    <HeldOrdersScreen onBack={onBack} presenter={presenter} />,
  );
  await waitFor(() => expect(presenter.refresh).toHaveBeenCalledTimes(1));
  expect(screen.getByTestId("held-order-row-recover-1")).toBeTruthy();
  expect(screen.queryByText("Product 1")).toBeNull();
  await fireEvent.press(screen.getByTestId("held-order-action-recover-1"));
  expect(presenter.recover).toHaveBeenCalledWith("recover-1");
  await waitFor(() => expect(onBack).toHaveBeenCalledTimes(1));
  expect(screen.getByTestId("held-orders-hold").props.accessibilityState.disabled).toBe(false);
});

test("合并列表按 HoldGuid 去重并显示本地/共享/远端/阻断徽标与远端来源", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [
        viewRow({
          holdId: "shared-1",
          local: localSummary({ holdId: "shared-1" }),
          remote: {
            holdGuid: "shared-1",
            deviceCode: "IPAD-1",
            cashierName: "Cashier",
            heldAtIso: "2026-07-28T01:00:00.000Z",
            lineCount: 2,
            actualCents: 1_200,
          },
          status: "published-shareable",
        }),
        viewRow({
          holdId: "remote-2",
          local: null,
          remote: {
            holdGuid: "remote-2",
            deviceCode: "IPAD-2",
            cashierName: "Other Cashier",
            heldAtIso: "2026-07-28T02:00:00.000Z",
            lineCount: 3,
            actualCents: 3_300,
          },
          status: "remote-pending",
        }),
        viewRow({
          holdId: "local-3",
          local: localSummary({ holdId: "local-3" }),
          status: "local-pending-publish",
        }),
        viewRow({
          holdId: "blocked-4",
          local: localSummary({ holdId: "blocked-4" }),
          status: "blocked",
          blockReason: "LEGACY_PAYLOAD_CORRUPTED",
        }),
      ],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  expect(screen.getByText("Shared")).toBeTruthy();
  expect(screen.getByText("Remote · available")).toBeTruthy();
  expect(screen.getByText("Device IPAD-2 · Other Cashier")).toBeTruthy();
  expect(screen.getByText("Awaiting share")).toBeTruthy();
  expect(screen.getByText("Cannot share")).toBeTruthy();
  expect(screen.getByTestId("held-order-blocked-reason-blocked-4")).toBeTruthy();
  expect(
    screen.getByText("Legacy hold data could not be read losslessly."),
  ).toBeTruthy();
});

test("远端挂单通过本机在线取单，成功返回收银", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [
        viewRow({
          holdId: "remote-2",
          local: null,
          remote: {
            holdGuid: "remote-2",
            deviceCode: "IPAD-2",
            cashierName: "Other Cashier",
            heldAtIso: "2026-07-28T02:00:00.000Z",
            lineCount: 3,
            actualCents: 3_300,
          },
          status: "remote-pending",
        }),
      ],
    },
  });
  const onBack = jest.fn();
  const screen = await render(
    <HeldOrdersScreen onBack={onBack} presenter={presenter} />,
  );
  await fireEvent.press(screen.getByTestId("held-order-action-remote-2"));
  expect(presenter.takeRemote).toHaveBeenCalledWith("remote-2");
  await waitFor(() => expect(onBack).toHaveBeenCalledTimes(1));
});

test("共享购物车恢复失败明确提示 Active claim 已保留", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      refreshError: null,
      sharedEnabled: true,
      rows: [],
      lastAction: {
        ok: false,
        code: "shared-restore-failed",
        holdId: "remote-2",
      },
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);
  expect(
    screen.getByText(
      "Cart restore failed; the active shared claim was kept. Use Recover sale before continuing.",
    ),
  ).toBeTruthy();
});

test("本机可共享副本通过 OfflineOrigin durable claim 取回，不走旧 recall", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [
        viewRow({
          holdId: "shared-local",
          local: localSummary({ holdId: "shared-local" }),
          status: "published-shareable",
        }),
      ],
    },
  });
  const onBack = jest.fn();
  const screen = await render(
    <HeldOrdersScreen onBack={onBack} presenter={presenter} />,
  );

  await fireEvent.press(screen.getByTestId("held-order-action-shared-local"));

  expect(presenter.recallLocalShared).toHaveBeenCalledWith("shared-local");
  expect(presenter.recall).not.toHaveBeenCalled();
  await waitFor(() => expect(onBack).toHaveBeenCalledTimes(1));
});

test("删除手势仅认领横向左滑，不抢占右滑、纵向滚动或 busy 行", async () => {
  const makeReadyState = (busy: boolean) => ({
    kind: "ready",
    busy,
    lastAction: null,
    refreshError: null,
    sharedEnabled: true,
    rows: [
      viewRow({ holdId: "local-1", local: localSummary({ holdId: "local-1" }) }),
    ],
  });
  const screen = await render(
    <HeldOrdersScreen
      presenter={createPresenter({ state: makeReadyState(false) })}
    />,
  );
  const swipeShell = screen.getByTestId("held-order-swipe-local-1");
  const shouldClaim = (
    shell: typeof swipeShell,
    startPageX: number,
    pageX: number,
    startPageY = 80,
    pageY = 80,
  ) => {
    const start = swipeEvent(
      startPageX,
      startPageX,
      startPageY,
      startPageY,
      100,
    );
    const move = swipeEvent(pageX, startPageX, pageY, startPageY, 200);
    shell.props.onStartShouldSetResponderCapture(start);
    shell.props.onMoveShouldSetResponderCapture(move);
    return shell.props.onMoveShouldSetResponder(move);
  };

  expect(shouldClaim(swipeShell, 220, 110)).toBe(true);
  expect(shouldClaim(swipeShell, 110, 220)).toBe(false);
  expect(shouldClaim(swipeShell, 220, 210, 80, 150)).toBe(false);

  const busyScreen = await render(
    <HeldOrdersScreen
      presenter={createPresenter({ state: makeReadyState(true) })}
    />,
  );
  expect(
    shouldClaim(
      busyScreen.getByTestId("held-order-swipe-local-1"),
      220,
      110,
    ),
  ).toBe(false);
});

test("左滑本机非恢复挂单露出删除，远端与恢复中挂单不可删除，并要求显式二次确认", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [
        viewRow({ holdId: "local-1", local: localSummary({ holdId: "local-1" }) }),
        viewRow({
          holdId: "remote-2",
          local: null,
          remote: {
            holdGuid: "remote-2",
            deviceCode: "IPAD-2",
            cashierName: "Other Cashier",
            heldAtIso: "2026-07-28T02:00:00.000Z",
            lineCount: 1,
            actualCents: 500,
          },
          status: "remote-pending",
        }),
        viewRow({
          holdId: "claim-3",
          local: localSummary({ holdId: "claim-3", status: "Recalling" }),
          status: "claiming-here",
        }),
      ],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  // 左滑之前滑出的删除键不存在；远端与恢复中的挂单既没有行尾删除键也不能左滑。
  expect(screen.queryByTestId("held-order-swipe-delete-local-1")).toBeNull();
  expect(screen.queryByTestId("held-order-delete-remote-2")).toBeNull();
  expect(screen.queryByTestId("held-order-delete-claim-3")).toBeNull();
  expect(screen.queryByTestId("held-order-swipe-remote-2")).toBeNull();
  expect(screen.queryByTestId("held-order-swipe-claim-3")).toBeNull();

  const swipeShell = screen.getByTestId("held-order-swipe-local-1");
  await act(async () => {
    swipeShell.props.onResponderGrant(swipeEvent(220, 220));
    swipeShell.props.onResponderMove(swipeEvent(110, 220));
    swipeShell.props.onResponderRelease(swipeEvent(110, 220));
  });

  await fireEvent.press(screen.getByTestId("held-order-swipe-delete-local-1"));
  expect(screen.getByTestId("held-orders-delete-panel")).toBeTruthy();
  expect(presenter.delete).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("held-orders-delete-cancel"));
  expect(screen.queryByTestId("held-orders-delete-panel")).toBeNull();

  const reopenedSwipeShell = screen.getByTestId("held-order-swipe-local-1");
  await act(async () => {
    reopenedSwipeShell.props.onResponderGrant(swipeEvent(220, 220));
    reopenedSwipeShell.props.onResponderMove(swipeEvent(110, 220));
    reopenedSwipeShell.props.onResponderRelease(swipeEvent(110, 220));
  });
  await fireEvent.press(screen.getByTestId("held-order-swipe-delete-local-1"));
  await fireEvent.press(screen.getByTestId("held-orders-delete-confirm"));
  expect(presenter.delete).toHaveBeenCalledWith("local-1");
  expect(screen.queryByTestId("held-orders-delete-panel")).toBeNull();
});

test("行尾红色删除键直接可见（64×64），不必左滑；点击先出确认面板，确认后才删除", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [
        viewRow({ holdId: "local-1", local: localSummary({ holdId: "local-1" }) }),
        viewRow({
          holdId: "claim-3",
          local: localSummary({ holdId: "claim-3", status: "Recalling" }),
          status: "claiming-here",
        }),
      ],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  const deleteButton = screen.getByTestId("held-order-delete-local-1");
  expect(deleteButton.props.accessibilityLabel).toBe("Delete");
  expect(StyleSheet.flatten(deleteButton.props.style)).toMatchObject({
    height: 64,
    width: 64,
  });
  // 恢复中的挂单不能删除，行尾不出现删除键。
  expect(screen.queryByTestId("held-order-delete-claim-3")).toBeNull();

  await fireEvent.press(deleteButton);
  expect(screen.getByTestId("held-orders-delete-panel")).toBeTruthy();
  expect(presenter.delete).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("held-orders-delete-cancel"));
  expect(screen.queryByTestId("held-orders-delete-panel")).toBeNull();
  expect(presenter.delete).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("held-order-delete-local-1"));
  await fireEvent.press(screen.getByTestId("held-orders-delete-confirm"));
  expect(presenter.delete).toHaveBeenCalledWith("local-1");
});

test("新布局尺寸：行高不低于 100，行主按钮约 190×64，忙碌时行尾删除键禁用", async () => {
  const makeState = (busy: boolean) => ({
    kind: "ready",
    busy,
    lastAction: null,
    refreshError: null,
    sharedEnabled: false,
    rows: [
      viewRow({ holdId: "local-1", local: localSummary({ holdId: "local-1" }) }),
    ],
  });
  const screen = await render(
    <HeldOrdersScreen presenter={createPresenter({ state: makeState(false) })} />,
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("held-order-row-local-1").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(100);
  const action = StyleSheet.flatten(
    screen.getByTestId("held-order-action-local-1").props.style,
  );
  expect(action.minHeight).toBe(64);
  expect(action.minWidth).toBe(190);
  // 未开启共享时不显示「本机 / 非本机」分段。
  expect(screen.queryByTestId("held-orders-source-local")).toBeNull();
  expect(screen.getByTestId("held-orders-filter-today")).toBeTruthy();

  const busyPresenter = createPresenter({ state: makeState(true) });
  const busyScreen = await render(<HeldOrdersScreen presenter={busyPresenter} />);
  const busyDelete = busyScreen.getByTestId("held-order-delete-local-1");
  expect(busyDelete.props.accessibilityState.disabled).toBe(true);
  await fireEvent.press(busyDelete);
  expect(busyScreen.queryByTestId("held-orders-delete-panel")).toBeNull();
});

test("删除未完成的阻断行只允许重试删除，不再允许取回到购物车", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      dateFilter: "all",
      rows: [
        viewRow({
          holdId: "delete-pending-1",
          local: localSummary({ holdId: "delete-pending-1" }),
          status: "blocked",
          blockReason: "LOCAL_DELETE_PENDING",
        }),
      ],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);

  expect(screen.queryByTestId("held-order-action-delete-pending-1")).toBeNull();
  expect(screen.getByTestId("held-order-swipe-delete-pending-1")).toBeTruthy();
  expect(presenter.recall).not.toHaveBeenCalled();
  expect(presenter.recallLocalShared).not.toHaveBeenCalled();
});

test("共享同步失败保留本地行并显示非阻塞错误", async () => {
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: "SHARED_HELD_ORDERS_SYNC_FAILED",
      remoteRefreshing: true,
      sharedEnabled: true,
      sourceTab: "other",
      rows: [viewRow({ holdId: "local-1" })],
    },
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);
  expect(screen.getByTestId("held-orders-remote-loading-indicator")).toBeTruthy();
  expect(screen.getByTestId("held-orders-refresh-error")).toBeTruthy();
  expect(screen.getByTestId("held-order-row-local-1")).toBeTruthy();
});

test("页面可见时每 10 秒刷新，进入后台停表，回到前台恢复，卸载停表", async () => {
  jest.useFakeTimers();
  const presenter = createPresenter();
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);
  await act(async () => {});
  expect(presenter.refresh).toHaveBeenCalledTimes(1);

  await act(async () => {
    jest.advanceTimersByTime(HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS);
  });
  expect(presenter.refresh).toHaveBeenCalledTimes(2);

  await act(async () => {
    appStateListener?.("background");
  });
  await act(async () => {
    jest.advanceTimersByTime(HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS * 3);
  });
  expect(presenter.stopAutoRefresh).toHaveBeenCalled();
  expect(presenter.refresh).toHaveBeenCalledTimes(2);

  await act(async () => {
    appStateListener?.("active");
  });
  await act(async () => {
    jest.advanceTimersByTime(HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS);
  });
  expect(presenter.refresh).toHaveBeenCalledTimes(3);

  await act(async () => {
    await screen.unmount();
  });
  expect(presenter.stopAutoRefresh).toHaveBeenCalledTimes(2);
});

test("强制释放入口未接线时不出现", async () => {
  const claimingRow = viewRow({
    holdId: "claim-1",
    local: localSummary({
      holdId: "claim-1",
      status: "Recalling",
      recallingAtIso: "2026-07-28T01:01:00.000Z",
    }),
    status: "claiming-here",
  });
  const base = {
    kind: "ready",
    busy: false,
    lastAction: null,
    refreshError: null,
    sharedEnabled: true,
    rows: [claimingRow],
  };

  const screen = await render(
    <HeldOrdersScreen
      presenter={createPresenter({ state: base, supportsForceRelease: () => false })}
    />,
  );
  expect(screen.queryByTestId("held-order-force-release-claim-1")).toBeNull();
});

test("强制释放接线后显示入口并要求非空原因", async () => {
  const claimingRow = viewRow({
    holdId: "claim-1",
    local: localSummary({
      holdId: "claim-1",
      status: "Recalling",
      recallingAtIso: "2026-07-28T01:01:00.000Z",
    }),
    status: "claiming-here",
  });
  const presenter = createPresenter({
    state: {
      kind: "ready",
      busy: false,
      lastAction: null,
      refreshError: null,
      sharedEnabled: true,
      rows: [claimingRow],
    },
    supportsForceRelease: () => true,
  });
  const screen = await render(<HeldOrdersScreen presenter={presenter} />);
  await fireEvent.press(screen.getByTestId("held-order-force-release-claim-1"));
  expect(screen.getByTestId("held-orders-force-release-panel")).toBeTruthy();
  expect(
    screen.getByTestId("held-orders-force-release-panel").props
      .automaticallyAdjustKeyboardInsets,
  ).toBe(true);

  const confirm = screen.getByTestId("held-orders-force-release-confirm");
  expect(confirm.props.accessibilityState.disabled).toBe(true);
  await fireEvent.changeText(
    screen.getByTestId("held-orders-force-release-reason"),
    "  duplicate claim  ",
  );
  expect(confirm.props.accessibilityState.disabled).toBe(false);
  await fireEvent.press(confirm);
  expect(presenter.forceRelease).toHaveBeenCalledWith("claim-1", "duplicate claim");
  expect(screen.queryByTestId("held-orders-force-release-panel")).toBeNull();
});
