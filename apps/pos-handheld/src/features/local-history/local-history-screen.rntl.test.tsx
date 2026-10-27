import { beforeEach, expect, jest, test } from "@jest/globals";
import {
  fireEvent,
  render,
} from "@testing-library/react-native";
import { Dimensions, StyleSheet } from "react-native";

import {
  localHistoryText,
  resolveLocalHistoryLocale,
} from "./local-history-copy";
import { LOCAL_HISTORY_KEYWORD_MAX_LENGTH } from "./local-history-domain";
import type {
  LocalHistoryDetailsState,
  LocalHistoryPresenterState,
  LocalHistoryReceiptPreviewState,
  LocalHistoryReprintState,
} from "./local-history-presenter";
import {
  LOCAL_HISTORY_MIN_TOUCH_TARGET,
  LocalHistoryScreen,
  LocalHistoryUnavailableScreen,
  localHistoryLayoutForWidth,
  type LocalHistoryScreenPresenter,
} from "./local-history-screen";

import { posColors } from "@/ui/theme";

let mockLanguage = "en";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: {
      language: mockLanguage,
      resolvedLanguage: mockLanguage,
    },
  }),
}));

jest.mock("@react-native-community/datetimepicker");

const orderGuid = "10000000-0000-4000-8000-000000000042";
const anotherOrderGuid = "10000000-0000-4000-8000-000000000043";

const readyState = {
  kind: "ready",
  filters: {
    soldFromIso: "2026-07-26T14:00:00.000Z",
    soldToIso: "2026-07-27T13:59:59.999Z",
    keyword: null,
  },
  businessTimeZone: "Australia/Brisbane",
  rows: [
    {
      orderGuid,
      localSequence: 42,
      soldAtIso: "2026-07-27T01:02:03.000Z",
      cashierName: "Alice",
      state: "PendingSync",
      totalCents: 1_234,
      discountCents: 34,
      actualAmountCents: 1_200,
      lineCount: 1,
      paymentSummary: "Card",
    },
  ],
  selectedOrderGuid: orderGuid,
  details: {
    kind: "ready",
    orderGuid,
    value: {
      orderGuid,
      localSequence: 42,
      soldAtIso: "2026-07-27T01:02:03.000Z",
      cashierName: "Alice",
      state: "PendingSync",
      totalCents: 1_234,
      discountCents: 34,
      actualAmountCents: 1_200,
      lines: [
        {
          lineId: "line-1",
          productCode: "P1",
          itemNumber: "I1",
          lookupCode: "930001",
          displayName: "Tea",
          quantity: "1",
          unitPriceCents: 1_234,
          discountCents: 34,
          actualAmountCents: 1_200,
          kind: "sale",
        },
      ],
      tenders: [
        {
          method: "card",
          amountCents: 1_200,
        },
      ],
    },
  },
  receiptPreview: { kind: "idle" },
  reprint: { kind: "idle" },
  loadingMore: false,
  hasMore: true,
  nextCursor: 41,
  errorCode: null,
} as const satisfies LocalHistoryPresenterState;

const pageStateCases = [
  {
    kind: "idle",
    testID: "local-history-loading",
    message: "Loading local history…",
  },
  {
    kind: "loading",
    testID: "local-history-loading",
    message: "Loading local history…",
  },
  {
    kind: "empty",
    testID: "local-history-empty",
    message: "No local orders match these filters.",
  },
  {
    kind: "failed",
    testID: "local-history-failed",
    message: "Local history could not be loaded.",
  },
  {
    kind: "unauthorized",
    testID: "local-history-unauthorized",
    message: "You do not have permission to view local history.",
  },
] as const;

const detailsStateCases = [
  [
    { kind: "idle" },
    "Select an order to inspect its items and payments.",
  ],
  [
    { kind: "loading", orderGuid },
    "Loading local history…",
  ],
  [
    { kind: "not-found", orderGuid },
    "This local order is no longer available.",
  ],
  [
    {
      kind: "failed",
      orderGuid,
      errorCode: "local-history-details-failed",
    },
    "Order details could not be loaded.",
  ],
] as const satisfies readonly (
  readonly [LocalHistoryDetailsState, string]
)[];

const receiptPreviewStateCases = [
  [
    { kind: "loading", orderGuid },
    "Loading receipt preview…",
  ],
  [
    { kind: "not-found", orderGuid },
    "This receipt preview is no longer available.",
  ],
  [
    {
      kind: "failed",
      orderGuid,
      errorCode: "local-history-receipt-preview-failed",
    },
    "Receipt preview could not be loaded.",
  ],
] as const satisfies readonly (
  readonly [LocalHistoryReceiptPreviewState, string]
)[];

const reprintResultCases = [
  [
    { kind: "succeeded", orderGuid },
    "Reprint sent to the configured terminal printer.",
  ],
  [
    {
      kind: "failed",
      orderGuid,
      errorCode: "local-history-reprint-failed",
    },
    "Receipt reprint could not be completed. Order details are unchanged.",
  ],
] as const satisfies readonly (
  readonly [LocalHistoryReprintState, string]
)[];

function createPresenter(
  state: LocalHistoryPresenterState = readyState,
  canReprint = true,
) {
  return {
    capabilities: {
      refund: false as const,
      recall: false as const,
      reprint: canReprint,
    },
    getState: jest.fn(() => state),
    subscribe: jest.fn((_listener: () => void) => () => undefined),
    setFilters: jest.fn((_filters: LocalHistoryPresenterState["filters"]) => {
      return undefined;
    }),
    refresh: jest.fn(async () => undefined),
    selectOrder: jest.fn(async (_selectedOrderGuid: string) => undefined),
    loadMore: jest.fn(async () => undefined),
    reprintSelected: jest.fn(async () => undefined),
  } satisfies LocalHistoryScreenPresenter;
}

async function openOrderDetail(
  screen: Awaited<ReturnType<typeof render>>,
  selectedOrderGuid = orderGuid,
) {
  await fireEvent.press(
    screen.getByTestId(`local-history-order-${selectedOrderGuid}`),
  );
  expect(screen.getByTestId("handheld-state-local-history-detail")).toBeTruthy();
  expect(screen.getByTestId("local-history-details")).toBeTruthy();
}

beforeEach(() => {
  jest.clearAllMocks();
  mockLanguage = "en";
  Dimensions.set({
    window: { width: 1_024, height: 768, scale: 2, fontScale: 1 },
    screen: { width: 1_024, height: 768, scale: 2, fontScale: 1 },
  });
});

test("390px 手持端从本地列表进入独立详情并提供 48px 返回", async () => {
  Dimensions.set({
    window: { width: 390, height: 844, scale: 3, fontScale: 1 },
    screen: { width: 390, height: 844, scale: 3, fontScale: 1 },
  });
  const presenter = createPresenter();
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  expect(screen.getByTestId("handheld-state-local-history-list")).toBeTruthy();
  expect(screen.queryByTestId("handheld-state-local-history-detail")).toBeNull();

  await fireEvent.press(screen.getByTestId(`local-history-order-${orderGuid}`));

  expect(screen.getByTestId("handheld-state-local-history-detail")).toBeTruthy();
  expect(screen.queryByTestId("handheld-state-local-history-list")).toBeNull();
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-detail-back").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(48);
  expect(presenter.selectOrder).toHaveBeenCalledWith(orderGuid);
});

test("本机历史以单列列表进入详情，完整保留业务信息和 48px 操作", async () => {
  const presenter = createPresenter();
  const onBack = jest.fn();
  const screen = await render(
    <LocalHistoryScreen
      onBack={onBack}
      presenter={presenter}
    />,
  );

  expect(screen.getByTestId("local-history-list")).toBeTruthy();
  expect(screen.queryByTestId("local-history-details")).toBeNull();
  const keyboardScroll = screen.getByTestId(
    "local-history-filters-keyboard-scroll",
  );
  expect(keyboardScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
  expect(keyboardScroll.props.keyboardDismissMode).toBe("interactive");
  expect(keyboardScroll.props.keyboardShouldPersistTaps).toBe("handled");
  expect(screen.getAllByText("Card").length).toBeGreaterThan(0);
  expect(screen.getAllByText("Pending sync").length).toBeGreaterThan(0);
  const order = screen.getByTestId(`local-history-order-${orderGuid}`);
  for (const expected of [
    "Local #42",
    "Alice",
    "Card",
    "Pending sync",
    "2026",
  ]) {
    expect(order.props.accessibilityLabel).toContain(expected);
  }
  expect(
    StyleSheet.flatten(screen.getAllByText("Pending sync")[0]?.props.style)
      .color,
  ).toBe(posColors.ink);
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-workspace").props.style),
  ).toEqual(
    expect.objectContaining({
      flexDirection: "column",
      gap: 14,
      padding: 14,
    }),
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-list-pane").props.style)
      .minWidth,
  ).toBe(0);

  await fireEvent.press(screen.getByTestId("local-history-back"));
  await fireEvent.press(screen.getByTestId("local-history-refresh"));
  for (const testID of [
    "local-history-back",
    "local-history-refresh",
    "local-history-apply-filters",
    "local-history-load-more",
  ]) {
    const style = StyleSheet.flatten(screen.getByTestId(testID).props.style);
    expect(style.minHeight).toBeGreaterThanOrEqual(
      LOCAL_HISTORY_MIN_TOUCH_TARGET,
    );
  }

  await fireEvent.press(order);
  expect(screen.getByTestId("local-history-details")).toBeTruthy();
  expect(screen.getByText("Tea")).toBeTruthy();
  expect(screen.getByTestId("local-history-detail-back")).toBeTruthy();
  for (const testID of [
    "local-history-detail-back",
    "local-history-reprint",
  ]) {
    expect(
      StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
    ).toBeGreaterThanOrEqual(LOCAL_HISTORY_MIN_TOUCH_TARGET);
  }
  await fireEvent.press(screen.getByTestId("local-history-reprint"));

  expect(onBack).toHaveBeenCalledTimes(1);
  expect(presenter.refresh).toHaveBeenCalled();
  expect(presenter.reprintSelected).toHaveBeenCalledTimes(1);
});

test("430px 竖屏详情保留双标签和 48px 触控区", async () => {
  Dimensions.set({
    window: { width: 430, height: 932, scale: 3, fontScale: 1 },
    screen: { width: 430, height: 932, scale: 3, fontScale: 1 },
  });
  const screen = await render(
    <LocalHistoryScreen presenter={createPresenter()} />,
  );

  expect(screen.getByTestId("local-history-list")).toBeTruthy();
  await fireEvent.press(screen.getByTestId(`local-history-order-${orderGuid}`));
  expect(screen.getByTestId("local-history-details")).toBeTruthy();
  for (const testID of [
    "local-history-details-tab",
    "local-history-receipt-preview-tab",
  ]) {
    const style = StyleSheet.flatten(screen.getByTestId(testID).props.style);
    expect(style.minHeight).toBeGreaterThanOrEqual(
      LOCAL_HISTORY_MIN_TOUCH_TARGET,
    );
  }
});

test("日期与订单或商品关键字按 presenter 业务时区边界提交", async () => {
  const presenter = createPresenter();
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("local-history-keyword"),
    "  930001  ",
  );
  await fireEvent.press(screen.getByTestId("local-history-date-from"));
  await fireEvent(
    screen.getByTestId("local-history-date-from-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 26, 12),
  );
  await fireEvent.press(
    screen.getByTestId("local-history-date-from-confirm"),
  );
  await fireEvent.press(screen.getByTestId("local-history-date-to"));
  await fireEvent(
    screen.getByTestId("local-history-date-to-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 28, 12),
  );
  await fireEvent.press(
    screen.getByTestId("local-history-date-to-confirm"),
  );
  await fireEvent.press(
    screen.getByTestId("local-history-apply-filters"),
  );

  expect(presenter.setFilters).toHaveBeenLastCalledWith({
    soldFromIso: "2026-07-25T14:00:00.000Z",
    soldToIso: "2026-07-28T13:59:59.999Z",
    keyword: "930001",
  });

  await fireEvent.press(screen.getByTestId("local-history-date-from"));
  await fireEvent(
    screen.getByTestId("local-history-date-from-picker"),
    "change",
    { type: "set" },
    new Date(2026, 6, 29, 12),
  );
  await fireEvent.press(
    screen.getByTestId("local-history-date-from-confirm"),
  );
  presenter.setFilters.mockClear();
  await fireEvent.press(
    screen.getByTestId("local-history-apply-filters"),
  );

  expect(presenter.setFilters).not.toHaveBeenCalled();
  expect(screen.getByTestId("local-history-date-invalid")).toBeTruthy();
});

test("快捷日期范围按分店营业时区取今天 / 昨天 / 近 7 天并立即查询（对齐 WPF #608）", async () => {
  // 只伪造 Date：2026-07-27T05:00Z = 布里斯班 07-27 15:00。
  jest.useFakeTimers({
    now: new Date("2026-07-27T05:00:00.000Z"),
    doNotFake: [
      "setTimeout", "clearTimeout", "setInterval", "clearInterval",
      "setImmediate", "clearImmediate", "queueMicrotask", "nextTick",
      "requestAnimationFrame", "cancelAnimationFrame", "performance",
    ],
  });
  try {
    const presenter = createPresenter();
    const screen = await render(<LocalHistoryScreen presenter={presenter} />);

    await fireEvent.press(screen.getByTestId("local-history-quick-today"));
    expect(presenter.setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({
        soldFromIso: "2026-07-26T14:00:00.000Z",
        soldToIso: "2026-07-27T13:59:59.999Z",
      }),
    );
    await fireEvent.press(screen.getByTestId("local-history-quick-yesterday"));
    expect(presenter.setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({
        soldFromIso: "2026-07-25T14:00:00.000Z",
        soldToIso: "2026-07-26T13:59:59.999Z",
      }),
    );
    await fireEvent.press(screen.getByTestId("local-history-quick-last7days"));
    expect(presenter.setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({
        soldFromIso: "2026-07-20T14:00:00.000Z",
        soldToIso: "2026-07-27T13:59:59.999Z",
      }),
    );
    expect(presenter.refresh).toHaveBeenCalled();
  } finally {
    jest.useRealTimers();
  }
});

test("关键词限制为查询边界长度，筛选同步拒绝时页面受控提示", async () => {
  const presenter = createPresenter();
  presenter.setFilters.mockImplementation(() => {
    throw new TypeError("invalid local history filters");
  });
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );
  const input = screen.getByTestId("local-history-keyword");

  expect(input.props.maxLength).toBe(LOCAL_HISTORY_KEYWORD_MAX_LENGTH);
  await fireEvent.changeText(
    input,
    "x".repeat(LOCAL_HISTORY_KEYWORD_MAX_LENGTH + 1),
  );
  await fireEvent.press(
    screen.getByTestId("local-history-apply-filters"),
  );

  expect(screen.getByTestId("local-history-query-invalid")).toBeTruthy();
});

test("订单列表和详情时间统一使用门店业务时区", async () => {
  const soldAtIso = "2026-07-27T01:02:03.000Z";
  const businessTimeZone = "Pacific/Honolulu";
  const state = {
    ...readyState,
    businessTimeZone,
    rows: [{ ...readyState.rows[0], soldAtIso }],
    details: {
      ...readyState.details,
      value: { ...readyState.details.value, soldAtIso },
    },
  } as const satisfies LocalHistoryPresenterState;
  const screen = await render(
    <LocalHistoryScreen presenter={createPresenter(state)} />,
  );
  const expected = new Intl.DateTimeFormat("en-AU", {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: businessTimeZone,
  }).format(new Date(soldAtIso));

  expect(
    screen.getByTestId(`local-history-order-${orderGuid}`).props
      .accessibilityLabel,
  ).toContain(expected);
  await openOrderDetail(screen);
  expect(screen.getAllByText(new RegExp(expected, "u")).length).toBeGreaterThan(
    0,
  );
});

test("选择订单与加载更多只调用 presenter 的窄接口", async () => {
  const presenter = createPresenter();
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  await fireEvent.press(screen.getByTestId("local-history-load-more"));
  await openOrderDetail(screen);

  expect(presenter.selectOrder).toHaveBeenCalledWith(orderGuid);
  expect(presenter.loadMore).toHaveBeenCalledTimes(1);
});

test("单列详情可切换小票预览，返回列表选择另一单后仍保留重打门禁", async () => {
  let state = {
    ...readyState,
    rows: [
      ...readyState.rows,
      {
        ...readyState.rows[0],
        orderGuid: anotherOrderGuid,
        localSequence: 43,
      },
    ],
    receiptPreview: {
      kind: "ready",
      orderGuid,
      document: {
        paper: "58mm",
        lines: [
          { kind: "text", text: "HOT BARGAIN", align: "center", bold: true },
          { kind: "separator", text: "--------------------------------", align: "left", bold: false },
          { kind: "barcode", value: "HB-ORDER-42" },
          { kind: "qr", value: orderGuid },
        ],
      },
    },
  } as LocalHistoryPresenterState;
  const listeners = new Set<() => void>();
  const presenter = {
    ...createPresenter(),
    getState: jest.fn(() => state),
    subscribe: jest.fn((listener: () => void) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }),
    selectOrder: jest.fn(async (nextOrderGuid: string) => {
      if (nextOrderGuid === orderGuid) return;
      state = {
        ...state,
        selectedOrderGuid: nextOrderGuid,
        receiptPreview: { kind: "loading", orderGuid: nextOrderGuid },
      };
      listeners.forEach((listener) => listener());
    }),
  } satisfies LocalHistoryScreenPresenter;
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  await openOrderDetail(screen);
  expect(
    screen.getByTestId("local-history-details-tab").props
      .accessibilityState.selected,
  ).toBe(true);
  expect(screen.queryByTestId("local-history-receipt-paper")).toBeNull();

  const previewTab = screen.getByTestId("local-history-receipt-preview-tab");
  expect(
    StyleSheet.flatten(previewTab.props.style).minHeight,
  ).toBeGreaterThanOrEqual(LOCAL_HISTORY_MIN_TOUCH_TARGET);
  await fireEvent.press(previewTab);

  expect(
    screen.getByTestId("local-history-receipt-paper"),
  ).toBeTruthy();
  expect(screen.getByText("HOT BARGAIN")).toBeTruthy();
  expect(
    screen.getByTestId("local-history-receipt-barcode-2").props
      .accessibilityLabel,
  ).toContain("HB-ORDER-42");
  expect(screen.getByTestId("local-history-reprint")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("local-history-detail-back"));
  await openOrderDetail(screen, anotherOrderGuid);

  expect(presenter.selectOrder).toHaveBeenCalledWith(anotherOrderGuid);
  expect(
    screen.getByTestId("local-history-details-tab").props
      .accessibilityState.selected,
  ).toBe(true);
  await fireEvent.press(
    screen.getByTestId("local-history-receipt-preview-tab"),
  );
  expect(screen.getByText("Loading receipt preview…")).toBeTruthy();
  expect(screen.getByTestId("local-history-reprint")).toBeTruthy();
});

test("320–430px 小屏保持单列、紧凑间距和可收缩全宽面板", async () => {
  for (const width of [320, 390, 430]) {
    expect(localHistoryLayoutForWidth(width)).toEqual({
      compact: true,
      workspaceGap: 8,
      workspacePadding: 0,
      filterWidth: 136,
    });
  }
  expect(localHistoryLayoutForWidth(1_024)).toEqual({
    compact: false,
    workspaceGap: 14,
    workspacePadding: 14,
    filterWidth: 150,
  });

  Dimensions.set({
    window: { width: 320, height: 568, scale: 2, fontScale: 1 },
    screen: { width: 320, height: 568, scale: 2, fontScale: 1 },
  });
  const screen = await render(
    <LocalHistoryScreen presenter={createPresenter()} />,
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-workspace").props.style),
  ).toEqual(
    expect.objectContaining({
      flexDirection: "column",
      gap: 8,
      padding: 0,
    }),
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-list-pane").props.style),
  ).toEqual(expect.objectContaining({ minWidth: 0, width: "100%" }));

  await openOrderDetail(screen);
  expect(
    StyleSheet.flatten(screen.getByTestId("local-history-details").props.style),
  ).toEqual(expect.objectContaining({ minWidth: 0, width: "100%" }));
});

test("紧凑布局 helper 保留 8px 节奏", () => {
  expect(localHistoryLayoutForWidth(760)).toEqual({
    compact: true,
    workspaceGap: 8,
    workspacePadding: 0,
    filterWidth: 136,
  });
});

test.each(pageStateCases)(
  "页面状态 $kind 显示明确提示",
  async ({ kind, message, testID }) => {
    const presenter = createPresenter({
      ...readyState,
      kind,
      rows: [],
      selectedOrderGuid: null,
      details: { kind: "idle" },
      reprint: { kind: "idle" },
      loadingMore: false,
      hasMore: false,
      nextCursor: null,
      errorCode:
        kind === "failed" ? "local-history-load-failed" : null,
    });
    const screen = await render(
      <LocalHistoryScreen presenter={presenter} />,
    );

    expect(screen.getByTestId(testID)).toBeTruthy();
    expect(screen.getByText(message)).toBeTruthy();
  },
);

test.each(detailsStateCases)(
  "详情状态 %j 显示明确提示",
  async (details, message) => {
    const presenter = createPresenter({
      ...readyState,
      details,
    });
    const screen = await render(
      <LocalHistoryScreen presenter={presenter} />,
    );

    await openOrderDetail(screen);
    expect(screen.getByText(message)).toBeTruthy();
  },
);

test.each(receiptPreviewStateCases)(
  "小票预览状态 %j 不改变订单详情",
  async (receiptPreview, message) => {
    const screen = await render(
      <LocalHistoryScreen
        presenter={createPresenter({ ...readyState, receiptPreview })}
      />,
    );

    await openOrderDetail(screen);
    await fireEvent.press(
      screen.getByTestId("local-history-receipt-preview-tab"),
    );
    expect(screen.getByText(message)).toBeTruthy();

    await fireEvent.press(screen.getByTestId("local-history-details-tab"));
    expect(screen.getByText("Tea")).toBeTruthy();
  },
);

test("分页加载中禁用按钮，无后续页时移除 footer", async () => {
  const loadingPresenter = createPresenter({
    ...readyState,
    loadingMore: true,
  });
  const loadingScreen = await render(
    <LocalHistoryScreen presenter={loadingPresenter} />,
  );
  const loadMore = loadingScreen.getByTestId("local-history-load-more");

  expect(loadMore.props.accessibilityState.disabled).toBe(true);
  expect(loadingScreen.getByText("Loading…")).toBeTruthy();
  await fireEvent.press(loadMore);
  expect(loadingPresenter.loadMore).not.toHaveBeenCalled();
  await loadingScreen.unmount();

  const completePresenter = createPresenter({
    ...readyState,
    hasMore: false,
    nextCursor: null,
  });
  const completeScreen = await render(
    <LocalHistoryScreen presenter={completePresenter} />,
  );
  expect(
    completeScreen.queryByTestId("local-history-load-more"),
  ).toBeNull();
});

test("重打提交中禁用按钮，并渲染成功或失败结果", async () => {
  const submittingPresenter = createPresenter({
    ...readyState,
    reprint: { kind: "submitting", orderGuid },
  });
  const submittingScreen = await render(
    <LocalHistoryScreen presenter={submittingPresenter} />,
  );
  await openOrderDetail(submittingScreen);
  const reprint = submittingScreen.getByTestId("local-history-reprint");

  expect(reprint.props.accessibilityState.disabled).toBe(true);
  expect(submittingScreen.getByText("Reprinting…")).toBeTruthy();
  await fireEvent.press(reprint);
  expect(submittingPresenter.reprintSelected).not.toHaveBeenCalled();
  await submittingScreen.unmount();

  for (const [reprintState, message] of reprintResultCases) {
    const presenter = createPresenter({
      ...readyState,
      reprint: reprintState,
    });
    const screen = await render(
      <LocalHistoryScreen presenter={presenter} />,
    );
    await openOrderDetail(screen);
    expect(screen.getByText(message)).toBeTruthy();
    await screen.unmount();
  }
});

test("无重打能力时隐藏重打入口", async () => {
  const presenter = createPresenter(readyState, false);
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  await openOrderDetail(screen);
  expect(screen.queryByTestId("local-history-reprint")).toBeNull();
});

test("runtime 未接线时显示受控不可用页并提供 44pt 返回", async () => {
  const onBack = jest.fn();
  const screen = await render(
    <LocalHistoryUnavailableScreen onBack={onBack} />,
  );

  expect(screen.getByTestId("local-history-unavailable")).toBeTruthy();
  const back = screen.getByTestId("local-history-unavailable-back");
  expect(
    StyleSheet.flatten(back.props.style).minHeight,
  ).toBeGreaterThanOrEqual(LOCAL_HISTORY_MIN_TOUCH_TARGET);

  await fireEvent.press(back);
  expect(onBack).toHaveBeenCalledTimes(1);
});

test("中英文文案跟随 locale，不在同一页面混排", () => {
  expect(resolveLocalHistoryLocale("zh-CN")).toBe("zh");
  expect(resolveLocalHistoryLocale("en-AU")).toBe("en");
  expect(localHistoryText("zh", "title")).toBe("本机销售历史");
  expect(localHistoryText("en", "title")).toBe("Local sales history");
});

test("中文页面本地化付款摘要与订单状态", async () => {
  mockLanguage = "zh";
  const presenter = createPresenter({
    ...readyState,
    rows: [
      {
        ...readyState.rows[0],
        paymentSummary: "Cash, Card",
      },
    ],
  });
  const screen = await render(
    <LocalHistoryScreen presenter={presenter} />,
  );

  expect(screen.getByText("本机销售历史")).toBeTruthy();
  expect(screen.getByText("现金、银行卡")).toBeTruthy();
  expect(screen.getAllByText("待同步").length).toBeGreaterThan(0);
  expect(screen.queryByText("Cash, Card")).toBeNull();
  const order = screen.getByTestId(`local-history-order-${orderGuid}`);
  expect(order.props.accessibilityLabel).toContain("本机 #42");
  expect(order.props.accessibilityLabel).toContain("现金、银行卡");
  expect(order.props.accessibilityLabel).toContain("待同步");
});
