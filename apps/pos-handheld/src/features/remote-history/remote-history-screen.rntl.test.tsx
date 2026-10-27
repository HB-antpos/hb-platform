import { beforeEach, expect, jest, test } from "@jest/globals";
import {
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import {
  REMOTE_HISTORY_VIEW_PERMISSION,
  REMOTE_HISTORY_REPRINT_PERMISSION,
  RemoteHistoryPresenter,
  type RemoteHistoryReprintPort,
} from "@hb/pos-domain/features/remote-history/remote-history-presenter";
import {
  REMOTE_HISTORY_MIN_TOUCH_TARGET,
  RemoteHistoryScreen,
} from "./remote-history-screen";

import type { RemoteOrderHistoryPort } from "@hb/pos-domain/core/contracts/remote-history";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.mock("@react-native-community/datetimepicker");

const port: RemoteOrderHistoryPort = {
  async list() {
    return [
      {
        orderGuid: "10000000-0000-4000-8000-000000000001",
        storeCode: "S1",
        deviceCode: "IPAD-1",
        cashierName: "Alice",
        soldAtIso: "2026-07-27T01:02:03.000Z",
        totalCents: 1234,
        discountCents: 34,
        actualAmountCents: 1200,
        lineCount: 1,
        paymentSummary: "Card",
        statusLabel: "Synced",
      },
    ];
  },
  async getDetails() {
    return {
      orderGuid: "10000000-0000-4000-8000-000000000001",
      storeCode: "S1",
      deviceCode: "IPAD-1",
      cashierName: "Alice",
      soldAtIso: "2026-07-27T01:02:03.000Z",
      totalCents: 1234,
      discountCents: 34,
      actualAmountCents: 1200,
      lines: [
        {
          orderLineGuid: "20000000-0000-4000-8000-000000000001",
          productCode: "P1",
          referenceCode: null,
          displayName: "Tea",
          lookupCode: "930001",
          itemNumber: "I1",
          quantity: "1",
          unitPriceCents: 1234,
          discountCents: 34,
          actualAmountCents: 1200,
          kind: "sale",
        },
      ],
      payments: [
        {
          paymentGuid: "30000000-0000-4000-8000-000000000001",
          method: "card",
          amountCents: 1200,
          displayReference: null,
          cardType: "VISA",
          maskedCardNumber: "**** 1234",
        },
      ],
    };
  },
};

beforeEach(() => {
  jest.clearAllMocks();
});

test("远程历史在手机宽度以列表进入独立详情并明确返回", async () => {
  const presenter = new RemoteHistoryPresenter({
    port,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: true,
    permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const screen = await render(
    <RemoteHistoryScreen presenter={presenter} />,
  );
  const row = await screen.findByTestId(
    "remote-history-order-10000000-0000-4000-8000-000000000001",
  );

  expect(screen.getByTestId("handheld-state-remote-history-list")).toBeTruthy();
  expect(screen.queryByTestId("handheld-state-remote-history-detail")).toBeNull();
  await fireEvent.press(row);

  expect(screen.getByTestId("handheld-state-remote-history-detail")).toBeTruthy();
  expect(screen.queryByTestId("handheld-state-remote-history-list")).toBeNull();
  expect(
    StyleSheet.flatten(screen.getByTestId("remote-history-detail-back").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(48);
});

test("远程列表与独立详情可读，且只呈现真实授权的操作", async () => {
  const presenter = new RemoteHistoryPresenter({
    port,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: true,
    permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const screen = await render(
    <RemoteHistoryScreen onBack={jest.fn()} presenter={presenter} />,
  );

  const row = await screen.findByTestId(
    "remote-history-order-10000000-0000-4000-8000-000000000001",
  );
  expect(screen.getByTestId("remote-history-readonly-note")).toBeTruthy();
  const keyboardScroll = screen.getByTestId(
    "remote-history-filters-keyboard-scroll",
  );
  expect(keyboardScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
  expect(keyboardScroll.props.keyboardDismissMode).toBe("interactive");
  expect(keyboardScroll.props.keyboardShouldPersistTaps).toBe("handled");

  for (const testID of [
    "remote-history-back",
    "remote-history-refresh",
    "remote-history-apply-filters",
  ]) {
    const style = StyleSheet.flatten(screen.getByTestId(testID).props.style);
    expect(style.minHeight).toBeGreaterThanOrEqual(
      REMOTE_HISTORY_MIN_TOUCH_TARGET,
    );
  }

  await fireEvent.press(row);
  await waitFor(() => expect(screen.getByText("Tea")).toBeTruthy());
  expect(screen.getByText("VISA · **** 1234")).toBeTruthy();
  expect(screen.queryByTestId("remote-history-refund")).toBeNull();
  expect(screen.queryByTestId("remote-history-recall")).toBeNull();
  expect(screen.queryByTestId("remote-history-reprint")).toBeNull();
});

test("筛选只提交日期、终端和关键词，可信门店仍由 presenter 固定", async () => {
  const presenter = new RemoteHistoryPresenter({
    port,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: true,
    permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const setFilters = jest.spyOn(presenter, "setFilters");
  const screen = await render(<RemoteHistoryScreen presenter={presenter} />);
  await waitFor(() => expect(screen.getByTestId("remote-history-list")).toBeTruthy());

  await fireEvent.changeText(
    screen.getByTestId("remote-history-device"),
    "",
  );
  await fireEvent.changeText(
    screen.getByTestId("remote-history-keyword"),
    "930001",
  );
  await fireEvent.press(
    screen.getByTestId("remote-history-apply-filters"),
  );

  expect(setFilters).toHaveBeenLastCalledWith(
    expect.objectContaining({
      deviceCode: null,
      keyword: "930001",
    }),
  );
});

test("快捷日期范围按设备本地日历日取今天 / 昨天 / 近 7 天并立即查询（对齐 WPF #608）", async () => {
  jest.useFakeTimers({
    now: new Date(2026, 6, 27, 12, 0, 0),
    doNotFake: [
      "setTimeout", "clearTimeout", "setInterval", "clearInterval",
      "setImmediate", "clearImmediate", "queueMicrotask", "nextTick",
      "requestAnimationFrame", "cancelAnimationFrame", "performance",
    ],
  });
  try {
    const presenter = new RemoteHistoryPresenter({
      port,
      trustedStoreCode: "S1",
      currentDeviceCode: "IPAD-1",
      online: true,
      permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
      now: () => new Date(2026, 6, 27, 12, 0, 0),
    });
    const setFilters = jest.spyOn(presenter, "setFilters");
    const screen = await render(<RemoteHistoryScreen presenter={presenter} />);
    await waitFor(() => expect(screen.getByTestId("remote-history-list")).toBeTruthy());

    const startOf = (day: number) => new Date(2026, 6, day, 0, 0, 0, 0).toISOString();
    const endOf = (day: number) => new Date(2026, 6, day, 23, 59, 59, 999).toISOString();
    await fireEvent.press(screen.getByTestId("remote-history-quick-yesterday"));
    expect(setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({ soldFromIso: startOf(26), soldToIso: endOf(26) }),
    );
    await fireEvent.press(screen.getByTestId("remote-history-quick-last7days"));
    expect(setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({ soldFromIso: startOf(21), soldToIso: endOf(27) }),
    );
    await fireEvent.press(screen.getByTestId("remote-history-quick-today"));
    expect(setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({ soldFromIso: startOf(27), soldToIso: endOf(27) }),
    );
  } finally {
    jest.useRealTimers();
  }
});

test("日期只能通过弹层选择，并按本地自然日边界提交", async () => {
  const presenter = new RemoteHistoryPresenter({
    port,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: true,
    permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const setFilters = jest.spyOn(presenter, "setFilters");
  const screen = await render(<RemoteHistoryScreen presenter={presenter} />);
  await screen.findByTestId("remote-history-list");

  expect(
    screen.getByTestId("remote-history-date-from").props.onChangeText,
  ).toBeUndefined();
  expect(
    screen.getByTestId("remote-history-date-to").props.onChangeText,
  ).toBeUndefined();

  await fireEvent.press(screen.getByTestId("remote-history-date-from"));
  await fireEvent(
    screen.getByTestId("remote-history-date-from-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 26, 12),
  );
  await fireEvent.press(
    screen.getByTestId("remote-history-date-from-confirm"),
  );
  await fireEvent.press(screen.getByTestId("remote-history-date-to"));
  await fireEvent(
    screen.getByTestId("remote-history-date-to-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 28, 12),
  );
  await fireEvent.press(
    screen.getByTestId("remote-history-date-to-confirm"),
  );
  await fireEvent.press(
    screen.getByTestId("remote-history-apply-filters"),
  );

  expect(setFilters).toHaveBeenLastCalledWith(
    expect.objectContaining({
      soldFromIso: new Date(2026, 6, 26, 0, 0, 0, 0).toISOString(),
      soldToIso: new Date(2026, 6, 28, 23, 59, 59, 999).toISOString(),
    }),
  );

  await fireEvent.press(screen.getByTestId("remote-history-date-from"));
  await fireEvent(
    screen.getByTestId("remote-history-date-from-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 29, 12),
  );
  await fireEvent.press(
    screen.getByTestId("remote-history-date-from-confirm"),
  );
  setFilters.mockClear();
  await fireEvent.press(
    screen.getByTestId("remote-history-apply-filters"),
  );

  expect(setFilters).not.toHaveBeenCalled();
  expect(screen.getByTestId("remote-history-date-invalid")).toBeTruthy();
});

test("离线状态明确说明远程历史只在线可用", async () => {
  const presenter = new RemoteHistoryPresenter({
    port,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: false,
    permissionCodes: [REMOTE_HISTORY_VIEW_PERMISSION],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const screen = await render(<RemoteHistoryScreen presenter={presenter} />);

  await waitFor(() => {
    expect(screen.getByTestId("remote-history-offline")).toBeTruthy();
  });
  expect(screen.getByText(/online only/i)).toBeTruthy();
});

test("普通完成订单可从可信详情进入既有退款流程，重打权限仍独立", async () => {
  const reprintPort: RemoteHistoryReprintPort = {
    canReprint: () => true,
    reprintExistingOrder: jest.fn(async () => undefined),
  };
  const presenter = new RemoteHistoryPresenter({
    port,
    reprintPort,
    trustedStoreCode: "S1",
    currentDeviceCode: "IPAD-1",
    online: true,
    permissionCodes: [
      REMOTE_HISTORY_VIEW_PERMISSION,
      REMOTE_HISTORY_REPRINT_PERMISSION,
    ],
    now: () => new Date("2026-07-27T05:00:00Z"),
  });
  const onRefund = jest.fn();
  const screen = await render(
    <RemoteHistoryScreen onRefund={onRefund} presenter={presenter} />,
  );

  await fireEvent.press(
    await screen.findByTestId(
      "remote-history-order-10000000-0000-4000-8000-000000000001",
    ),
  );
  const reprint = await screen.findByTestId("remote-history-reprint");
  await fireEvent.press(reprint);

  expect(reprintPort.reprintExistingOrder).toHaveBeenCalledWith(
    "10000000-0000-4000-8000-000000000001",
  );
  await fireEvent.press(screen.getByTestId("remote-history-refund"));
  expect(onRefund).toHaveBeenCalledWith(
    "10000000-0000-4000-8000-000000000001",
  );
});
