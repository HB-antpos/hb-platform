import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
  act,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import {
  SYNC_HISTORY_CHECKBOX_SIZE,
  SYNC_HISTORY_FILTERS_WIDTH,
  SYNC_HISTORY_MIN_TOUCH_TARGET,
  SyncHistoryPresenter,
  SyncHistoryScreen,
  syncHistoryText,
  type LocalSyncHistoryOrder,
  type LocalSyncHistoryPage,
  type LocalSyncHistoryPageQuery,
  type LocalSyncHistoryPort,
} from "./index";

import { posColors } from "@/ui/theme";

const SYNC_HISTORY_ALL_PERMISSIONS = [
  "Permissions.PosTerminal.Audit.View",
  "Permissions.PosTerminal.History.View",
  "Permissions.PosTerminal.System.Sync",
];

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
    // 页面外壳自带的顶栏会调用 t()，这里原样返回键名即可。
    t: (key: string) => key,
  }),
}));

jest.mock("@/ui/feedback", () => ({
  usePosSound: () => ({
    buttonSoundEnabled: false,
    play: jest.fn(),
    setButtonSoundEnabled: jest.fn(),
    setSpecialNodeSoundEnabled: jest.fn(),
    specialNodeSoundEnabled: false,
  }),
}));

jest.mock("@react-native-community/datetimepicker");

function order(
  overrides: Partial<LocalSyncHistoryOrder> = {},
): LocalSyncHistoryOrder {
  return {
    actualAmountCents: 1_100,
    deviceCode: "IPAD-1",
    discountCents: 100,
    localSequence: 100,
    orderGuid: "order-100",
    outbox: {
      attemptCount: 2,
      lastErrorCode: "SYNC_NETWORK",
      nextAttemptAtIso: "2026-07-28T10:12:12.000Z",
      state: "pending",
    },
    soldAtIso: "2026-07-28T10:11:12.000Z",
    state: "PendingSync",
    storeCode: "BNE",
    tenders: [{ amountCents: 1_100, method: "cash" }],
    totalCents: 1_200,
    ...overrides,
  };
}

class ScreenHistoryPort implements LocalSyncHistoryPort {
  public readonly queries: LocalSyncHistoryPageQuery[] = [];
  public readonly restoreCalls: string[][] = [];
  public pageError = false;
  public listHold: Promise<void> | null = null;

  public constructor(public orders: LocalSyncHistoryOrder[]) {}

  public async listLocalSyncHistory(
    query: LocalSyncHistoryPageQuery,
  ): Promise<LocalSyncHistoryPage> {
    this.queries.push(query);
    await this.listHold;
    if (this.pageError) throw new Error("secret database path");
    const filtered = this.orders
      .filter(
        (candidate) =>
          query.beforeLocalSequence === null ||
          candidate.localSequence < query.beforeLocalSequence,
      )
      .filter(
        (candidate) =>
          !query.filters.dateFromIso ||
          candidate.soldAtIso >= query.filters.dateFromIso,
      )
      .filter(
        (candidate) =>
          !query.filters.dateToIso ||
          candidate.soldAtIso <= query.filters.dateToIso,
      )
      .filter(
        (candidate) =>
          !query.filters.states.length ||
          query.filters.states.includes(candidate.state),
      )
      .sort((left, right) => right.localSequence - left.localSequence);
    const page = filtered.slice(0, query.limit);
    return {
      nextBeforeLocalSequence:
        page.length === query.limit && filtered.length > page.length
          ? (page.at(-1)?.localSequence ?? null)
          : null,
      orders: page,
      pendingCount: this.orders.filter(
        (candidate) => candidate.outbox?.state === "pending",
      ).length,
    };
  }

  public async getLocalSyncHistorySupportSnapshot(query: {
    filters: LocalSyncHistoryPageQuery["filters"];
    limit: number;
  }) {
    const filtered = this.orders
      .filter(
        (candidate) =>
          !query.filters.dateFromIso ||
          candidate.soldAtIso >= query.filters.dateFromIso,
      )
      .filter(
        (candidate) =>
          !query.filters.dateToIso ||
          candidate.soldAtIso <= query.filters.dateToIso,
      )
      .filter(
        (candidate) =>
          !query.filters.states.length ||
          query.filters.states.includes(candidate.state),
      )
      .sort((left, right) => right.localSequence - left.localSequence);
    return {
      orders: filtered.slice(0, query.limit),
      totalMatchingCount: filtered.length,
    };
  }

  public async restoreExistingOrderOutboxToPending(
    orderGuids: readonly string[],
  ) {
    this.restoreCalls.push([...orderGuids]);
    return {
      restoredOrderGuids: [...orderGuids],
      skippedOrderGuids: [],
    };
  }

  public async getSupportContext() {
    return {
      appId: "hb-pos-ipad",
      appVersion: "2.11.0",
      deviceCode: "IPAD-1",
      storeCode: "BNE",
    };
  }
}

const mountedPresenters: SyncHistoryPresenter[] = [];

/** 取一个节点下全部文字（含嵌套 Text）。 */
function textContent(node: { children: readonly unknown[] }): string {
  return node.children
    .map((child) =>
      typeof child === "string"
        ? child
        : textContent(child as { children: readonly unknown[] }),
    )
    .join("");
}

function screenPresenter(
  port: LocalSyncHistoryPort,
  pageSize = 50,
  permissionCodes: readonly string[] = SYNC_HISTORY_ALL_PERMISSIONS,
): SyncHistoryPresenter {
  const presenter = new SyncHistoryPresenter({
    pageSize,
    permissionCodes,
    port,
  });
  mountedPresenters.push(presenter);
  return presenter;
}

afterEach(() => {
  for (const presenter of mountedPresenters.splice(0)) presenter.destroy();
  jest.restoreAllMocks();
});

async function chooseDate(
  screen: Awaited<ReturnType<typeof render>>,
  testID: string,
  date: Date,
) {
  await fireEvent.press(screen.getByTestId(testID));
  await fireEvent(
    screen.getByTestId(`${testID}-picker`),
    "change",
    { type: "set" },
    date,
  );
  await fireEvent.press(screen.getByTestId(`${testID}-confirm`));
}

describe("SyncHistoryScreen", () => {
  it("历史小数数量待同步时显示人工核对和勿重复收款提示", async () => {
    const pending = order({
      outbox: { attemptCount: 1, lastErrorCode: "ORDER_SYNC_QUANTITY_UNSUPPORTED", nextAttemptAtIso: null, state: "pending" },
    });
    const screen = await render(<SyncHistoryScreen onExport={jest.fn<(serializedJson: string) => void>()} presenter={screenPresenter(new ScreenHistoryPort([pending]))} />);
    await screen.findByTestId("sync-history-row-order-100");
    expect(screen.getByText(/quantity review.*Do not charge again/)).toBeTruthy();
  });

  it("筛选与恢复操作提供短高度滚动回退，列表工作区允许收缩", async () => {
    const presenter = screenPresenter(new ScreenHistoryPort([order()]));
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    const filters = screen.getByTestId("sync-history-filters-scroll");
    expect(filters.props.showsVerticalScrollIndicator).toBe(true);
    expect(
      StyleSheet.flatten(
        screen.getByTestId("sync-history-workspace").props.style,
      ).minHeight,
    ).toBe(0);
    expect(screen.getByTestId("sync-history-retransmit-range")).toBeTruthy();
    expect(screen.getByTestId("sync-history-export")).toBeTruthy();
  });

  it("左栏只管筛选：7 个状态大按钮高 52、点了立即查，「应用筛选」固定在栏底", async () => {
    const port = new ScreenHistoryPort([order()]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    const chipIds = [
      "sync-history-filter-all",
      "sync-history-filter-CompletedLocal",
      "sync-history-filter-PendingSync",
      "sync-history-filter-Syncing",
      "sync-history-filter-Synced",
      "sync-history-filter-Blocked403",
      "sync-history-filter-Rejected",
    ];
    for (const testID of chipIds) {
      const chip = screen.getByTestId(testID);
      expect(
        StyleSheet.flatten(chip.props.style).minHeight,
      ).toBeGreaterThanOrEqual(52);
      expect(chip.props.accessibilityRole).toBe("checkbox");
    }
    expect(
      screen.getByTestId("sync-history-filter-all").props.accessibilityState
        .checked,
    ).toBe(true);
    expect(screen.getByText("Status (select any)")).toBeTruthy();

    // 状态可多选，点一下就按新状态查询，不需要再点「应用筛选」。
    await fireEvent.press(screen.getByTestId("sync-history-filter-PendingSync"));
    await fireEvent.press(screen.getByTestId("sync-history-filter-Rejected"));
    await waitFor(() => {
      expect(port.queries.at(-1)?.filters.states).toEqual([
        "PendingSync",
        "Rejected",
      ]);
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("sync-history-filter-PendingSync").props
          .accessibilityState.checked,
      ).toBe(true);
    });
    expect(
      screen.getByTestId("sync-history-filter-all").props.accessibilityState
        .checked,
    ).toBe(false);

    // 筛选栏固定 300 宽；「应用筛选」在滚动区之外，矮屏也始终可见。
    const filtersScroll = screen.getByTestId("sync-history-filters-scroll");
    const filtersPane = filtersScroll.parent;
    expect(StyleSheet.flatten(filtersPane?.props.style).width).toBe(
      SYNC_HISTORY_FILTERS_WIDTH,
    );
    const apply = screen.getByTestId("sync-history-apply-filters");
    expect(apply.parent).toBe(filtersPane);
    expect(
      StyleSheet.flatten(apply.props.style).minHeight,
    ).toBeGreaterThanOrEqual(56);
    for (const testID of ["sync-history-date-from", "sync-history-date-to"]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(56);
    }
  });

  it("补传已选、按日期范围补传、支持导出固定在列表下方一排，旁边显示已选数量", async () => {
    const presenter = screenPresenter(
      new ScreenHistoryPort([
        order({ localSequence: 102, orderGuid: "first" }),
        order({ localSequence: 101, orderGuid: "second" }),
      ]),
    );
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-first");

    const bar = screen.getByTestId("sync-history-action-bar");
    expect(StyleSheet.flatten(bar.props.style).flexDirection).toBe("row");
    for (const testID of [
      "sync-history-retransmit-selected",
      "sync-history-retransmit-range",
      "sync-history-export",
    ]) {
      const button = screen.getByTestId(testID);
      expect(button.parent).toBe(bar);
      expect(
        StyleSheet.flatten(button.props.style).minHeight,
      ).toBeGreaterThanOrEqual(64);
    }
    // 补传已选是主按钮，占满剩余宽度。
    expect(
      StyleSheet.flatten(
        screen.getByTestId("sync-history-retransmit-selected").props.style,
      ).flex,
    ).toBe(1);
    expect(
      StyleSheet.flatten(
        screen.getByTestId("sync-history-load-more").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(56);

    const selectionText = () =>
      textContent(screen.getByTestId("sync-history-selection-count"));
    expect(selectionText()).toBe("0 selected");
    await fireEvent.press(screen.getByTestId("sync-history-row-first"));
    await fireEvent.press(screen.getByTestId("sync-history-row-second"));
    await waitFor(() => {
      expect(selectionText()).toBe("2 selected");
    });
  });

  it("行内勾选框 34×34、整行可点；不能补传的行置灰并在行内写原因", async () => {
    const presenter = screenPresenter(
      new ScreenHistoryPort([
        order({ localSequence: 102, orderGuid: "eligible" }),
        order({
          localSequence: 101,
          orderGuid: "blocked",
          outbox: {
            attemptCount: 3,
            lastErrorCode: "SYNC_403",
            nextAttemptAtIso: null,
            state: "blocked403",
          },
          state: "Blocked403",
        }),
      ]),
    );
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-eligible");

    const checkbox = StyleSheet.flatten(
      screen.getByTestId("sync-history-select-eligible").props.style,
    );
    expect(checkbox.width).toBe(SYNC_HISTORY_CHECKBOX_SIZE);
    expect(checkbox.height).toBe(SYNC_HISTORY_CHECKBOX_SIZE);
    expect(SYNC_HISTORY_CHECKBOX_SIZE).toBe(34);

    const eligibleRow = screen.getByTestId("sync-history-row-eligible");
    const blockedRow = screen.getByTestId("sync-history-row-blocked");
    expect(
      StyleSheet.flatten(eligibleRow.props.style).minHeight,
    ).toBeGreaterThanOrEqual(84);
    expect(StyleSheet.flatten(eligibleRow.props.style).backgroundColor).toBe(
      posColors.surface,
    );
    expect(StyleSheet.flatten(blockedRow.props.style).backgroundColor).toBe(
      posColors.surfaceAlt,
    );
    expect(
      StyleSheet.flatten(
        screen.getByTestId("sync-history-select-blocked").props.style,
      ).backgroundColor,
    ).toBe(posColors.disabledSurface);
    // 原因与补传队列信息都写在行内。
    expect(
      textContent(screen.getByTestId("sync-history-gate-blocked")),
    ).toBe("Re-authenticate before retransmitting");
    expect(screen.getByText("Outbox: Blocked · 3 attempts")).toBeTruthy();
    expect(
      textContent(screen.getByTestId("sync-history-error-blocked")),
    ).toBe("Safe error: SYNC_403");

    // 点行本身就能勾选，勾上后勾选框变成主操作色实底。
    await fireEvent.press(eligibleRow);
    await waitFor(() => {
      expect(
        StyleSheet.flatten(
          screen.getByTestId("sync-history-select-eligible").props.style,
        ).backgroundColor,
      ).toBe(posColors.accent);
    });
  });

  it("空日期保持不限筛选，选择与清除都不暴露文本输入", async () => {
    const port = new ScreenHistoryPort([order()]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    expect(screen.getAllByText("Any date")).toHaveLength(2);
    expect(
      screen.getByTestId("sync-history-date-from").props.onChangeText,
    ).toBeUndefined();
    expect(
      screen.getByTestId("sync-history-date-to").props.onChangeText,
    ).toBeUndefined();

    await fireEvent.press(screen.getByTestId("sync-history-apply-filters"));
    await waitFor(() => {
      expect(port.queries.at(-1)?.filters).toMatchObject({
        dateFromIso: null,
        dateToIso: null,
      });
    });

    await chooseDate(
      screen,
      "sync-history-date-from",
      new Date(2026, 6, 27, 12),
    );
    await chooseDate(
      screen,
      "sync-history-date-to",
      new Date(2026, 6, 28, 12),
    );
    expect(
      screen.getByTestId("sync-history-retransmit-range").props
        .accessibilityState.disabled,
    ).toBe(false);

    await fireEvent.press(screen.getByTestId("sync-history-date-from"));
    await fireEvent.press(
      screen.getByTestId("sync-history-date-from-clear"),
    );
    expect(
      screen.getByTestId("sync-history-retransmit-range").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(screen.getByText("Any date")).toBeTruthy();
  });

  it("筛选与日期重传共用注入的门店时区业务日 UTC 边界", async () => {
    const port = new ScreenHistoryPort([
      order({
        orderGuid: "brisbane-midnight",
        soldAtIso: "2026-07-27T14:00:00.000Z",
      }),
    ]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        businessTimeZone="Australia/Brisbane"
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-brisbane-midnight");

    await chooseDate(
      screen,
      "sync-history-date-from",
      new Date(2026, 6, 28, 12),
    );
    await chooseDate(
      screen,
      "sync-history-date-to",
      new Date(2026, 6, 28, 12),
    );
    await fireEvent.press(screen.getByTestId("sync-history-apply-filters"));

    await waitFor(() => {
      expect(port.queries.at(-1)?.filters).toMatchObject({
        dateFromIso: "2026-07-27T14:00:00.000Z",
        dateToIso: "2026-07-28T13:59:59.999Z",
      });
    });

    const retransmitQueryStart = port.queries.length;
    await fireEvent.press(screen.getByTestId("sync-history-retransmit-range"));
    await waitFor(() => {
      expect(port.restoreCalls).toEqual([["brisbane-midnight"]]);
    });
    expect(port.queries.slice(retransmitQueryStart)).not.toHaveLength(0);
    for (const query of port.queries.slice(retransmitQueryStart)) {
      expect(query.filters).toMatchObject({
        dateFromIso: "2026-07-27T14:00:00.000Z",
        dateToIso: "2026-07-28T13:59:59.999Z",
      });
    }
  });

  it("非法门店时区不会退化成无日期筛选或触发日期重传", async () => {
    const port = new ScreenHistoryPort([order()]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        businessTimeZone="Australia/Not_A_Zone"
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-order-100");
    const initialQueryCount = port.queries.length;

    await chooseDate(
      screen,
      "sync-history-date-from",
      new Date(2026, 6, 28, 12),
    );
    await chooseDate(
      screen,
      "sync-history-date-to",
      new Date(2026, 6, 28, 12),
    );
    await fireEvent.press(screen.getByTestId("sync-history-apply-filters"));

    await waitFor(() => {
      expect(presenter.getState()).toMatchObject({
        errorCode: "invalid-date-range",
        kind: "failed",
      });
    });
    expect(port.queries).toHaveLength(initialQueryCount);
    expect(presenter.getState().filters).toMatchObject({
      dateFromIso: "invalid-business-date-range",
      dateToIso: "invalid-business-date-range",
    });

    await fireEvent.press(screen.getByTestId("sync-history-retransmit-range"));
    await waitFor(() => {
      expect(presenter.getState().lastRetransmit).toMatchObject({
        errorCode: "invalid-date-range",
        kind: "failed",
      });
    });
    expect(port.queries).toHaveLength(initialQueryCount);
    expect(port.restoreCalls).toEqual([]);
  });

  it("显示加载态、待同步数与 localSequence 降序列表，并保持 44pt 触控目标", async () => {
    let release!: () => void;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const port = new ScreenHistoryPort([
      order({ localSequence: 99, orderGuid: "order-99" }),
      order({ localSequence: 101, orderGuid: "order-101" }),
    ]);
    port.listHold = hold;
    const presenter = screenPresenter(port);
    const onExport = jest.fn<(serializedJson: string) => void>();
    const screen = await render(
      <SyncHistoryScreen onExport={onExport} presenter={presenter} />,
    );

    expect(screen.getByTestId("sync-history-loading")).toBeTruthy();
    const refreshStyle = StyleSheet.flatten(
      screen.getByTestId("sync-history-refresh").props.style,
    );
    expect(refreshStyle.minHeight).toBeGreaterThanOrEqual(
      SYNC_HISTORY_MIN_TOUCH_TARGET,
    );

    await act(async () => {
      release();
      await hold;
    });

    await waitFor(() => {
      expect(screen.getByText("Local #101")).toBeTruthy();
      expect(screen.getByText("Local #99")).toBeTruthy();
      expect(screen.getByText("2 orders")).toBeTruthy();
    });
    expect(
      presenter.getState().rows.map((row) => row.localSequence),
    ).toEqual([101, 99]);
  });

  it("选择订单后只把 eligible pending outbox 提交给补传 Port", async () => {
    const port = new ScreenHistoryPort([
      order({ localSequence: 102, orderGuid: "eligible" }),
      order({
        localSequence: 101,
        orderGuid: "synced",
        outbox: {
          attemptCount: 1,
          lastErrorCode: null,
          nextAttemptAtIso: null,
          state: "succeeded",
        },
        state: "Synced",
      }),
    ]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-eligible");

    await fireEvent.press(screen.getByTestId("sync-history-row-eligible"));
    expect(
      screen.getByTestId("sync-history-row-eligible").props
        .accessibilityState,
    ).toMatchObject({ checked: true, disabled: false });
    await fireEvent.press(
      screen.getByTestId("sync-history-retransmit-selected"),
    );

    await waitFor(() => {
      expect(port.restoreCalls).toEqual([["eligible"]]);
      expect(
        screen.getByTestId("sync-history-retransmit-result"),
      ).toBeTruthy();
    });
  });

  it("Blocked403 和 Rejected 显示真实处置门禁，且不可选择或发起无效补传", async () => {
    const port = new ScreenHistoryPort([
      order({
        localSequence: 102,
        orderGuid: "blocked",
        outbox: {
          attemptCount: 3,
          lastErrorCode: "SYNC_403",
          nextAttemptAtIso: null,
          state: "blocked403",
        },
        state: "Blocked403",
      }),
      order({
        localSequence: 101,
        orderGuid: "rejected",
        outbox: {
          attemptCount: 2,
          lastErrorCode: "BUSINESS_REJECTED",
          nextAttemptAtIso: null,
          state: "rejected",
        },
        state: "Rejected",
      }),
    ]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-row-blocked");

    expect(screen.getByText("Re-authenticate before retransmitting")).toBeTruthy();
    expect(screen.getByText("Back-office handling required")).toBeTruthy();
    expect(
      screen.getByTestId("sync-history-row-blocked").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("sync-history-row-rejected").props
        .accessibilityState.disabled,
    ).toBe(true);
    await fireEvent.press(screen.getByTestId("sync-history-row-blocked"));
    await fireEvent.press(screen.getByTestId("sync-history-row-rejected"));
    expect(presenter.getState().selectedOrderGuids).toEqual([]);
    expect(
      screen.getByTestId("sync-history-retransmit-selected").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(port.restoreCalls).toEqual([]);
    expect(screen.queryByText(/refund/i)).toBeNull();
    expect("requestRefund" in presenter).toBe(false);
  });

  it("筛选竞态中旧请求晚到也不会覆盖当前筛选的 UI", async () => {
    let releaseOld!: () => void;
    const oldHold = new Promise<void>((resolve) => {
      releaseOld = resolve;
    });
    let firstQuery = true;
    const port = new ScreenHistoryPort([
      order({
        localSequence: 102,
        orderGuid: "old-pending",
        state: "PendingSync",
      }),
      order({
        localSequence: 101,
        orderGuid: "current-synced",
        outbox: {
          attemptCount: 1,
          lastErrorCode: null,
          nextAttemptAtIso: null,
          state: "succeeded",
        },
        state: "Synced",
      }),
    ]);
    const ordinaryList = port.listLocalSyncHistory.bind(port);
    port.listLocalSyncHistory = async (query) => {
      if (firstQuery) {
        firstQuery = false;
        port.queries.push(query);
        await oldHold;
        return {
          nextBeforeLocalSequence: null,
          orders: [order({ localSequence: 102, orderGuid: "old-pending" })],
          pendingCount: 1,
        };
      }
      return ordinaryList(query);
    };
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    expect(screen.getByTestId("sync-history-loading")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("sync-history-filter-Synced"));
    await screen.findByTestId("sync-history-row-current-synced");
    await act(async () => {
      releaseOld();
      await oldHold;
    });

    await waitFor(() => {
      expect(
        screen.getByTestId("sync-history-row-current-synced"),
      ).toBeTruthy();
      expect(screen.queryByTestId("sync-history-row-old-pending")).toBeNull();
    });
    expect(presenter.getState().filters.states).toEqual(["Synced"]);
  });

  it("支持导出只把 presenter 生成的白名单 JSON 交给回调", async () => {
    const sensitiveOrder = {
      ...order({
        outbox: {
          attemptCount: 2,
          lastErrorCode: "PAN-4111111111111111",
          nextAttemptAtIso: null,
          state: "pending" as const,
        },
        tenders: [{ amountCents: 1_100, method: "card" as const }],
      }),
      authorizationCode: "AUTH-SECRET",
      customerPhone: "0400000000",
      receiptBytes: "SECRET-RECEIPT",
      reservationToken: "VOUCHER-SECRET",
    } as LocalSyncHistoryOrder;
    const port = new ScreenHistoryPort([sensitiveOrder]);
    const presenter = screenPresenter(port);
    const onExport = jest.fn(async (_serializedJson: string) => undefined);
    const screen = await render(
      <SyncHistoryScreen onExport={onExport} presenter={presenter} />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    await fireEvent.press(screen.getByTestId("sync-history-export"));

    await waitFor(() => {
      expect(onExport).toHaveBeenCalledTimes(1);
      expect(screen.getByTestId("sync-history-export-success")).toBeTruthy();
    });
    const serializedJson = onExport.mock.calls[0]?.[0] ?? "";
    expect(serializedJson).toContain("hb-pos-sync-history-v1");
    expect(serializedJson).toContain('"orderGuid":"order-0001"');
    expect(serializedJson).toContain('"soldAtUtcDate":"2026-07-28"');
    expect(serializedJson).not.toMatch(
      /AUTH-SECRET|0400000000|SECRET-RECEIPT|VOUCHER-SECRET|4111111111111111|order-100|IPAD-1|BNE|10:11:12|soldAtIso/,
    );
  });

  it("没有 Audit.View 时导出入口不可调用，但 System.Sync 手动重传仍可用", async () => {
    const port = new ScreenHistoryPort([order()]);
    const presenter = screenPresenter(port, 50, [
      "Permissions.PosTerminal.History.View",
      "Permissions.PosTerminal.System.Sync",
    ]);
    const onExport = jest.fn(async (_serializedJson: string) => undefined);
    const screen = await render(
      <SyncHistoryScreen onExport={onExport} presenter={presenter} />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    const exportButton = screen.getByTestId("sync-history-export");
    expect(exportButton.props.accessibilityState).toMatchObject({
      disabled: true,
    });
    await fireEvent.press(exportButton);
    expect(onExport).not.toHaveBeenCalled();

    await fireEvent.press(screen.getByTestId("sync-history-row-order-100"));
    await fireEvent.press(
      screen.getByTestId("sync-history-retransmit-selected"),
    );
    await waitFor(() => {
      expect(port.restoreCalls).toEqual([["order-100"]]);
    });
  });

  it("导出失败只显示固定提示，不回显异常或导出正文", async () => {
    const port = new ScreenHistoryPort([order()]);
    const presenter = screenPresenter(port);
    const onExport = jest.fn(async () => {
      throw new Error("AUTH-SECRET from share sheet");
    });
    const screen = await render(
      <SyncHistoryScreen onExport={onExport} presenter={presenter} />,
    );
    await screen.findByTestId("sync-history-row-order-100");

    await fireEvent.press(screen.getByTestId("sync-history-export"));

    await waitFor(() => {
      expect(screen.getByTestId("sync-history-export-failed")).toBeTruthy();
    });
    expect(screen.queryByText(/AUTH-SECRET/)).toBeNull();
    expect(screen.getByText(/No diagnostic content was shown/)).toBeTruthy();
  });

  it("空态与失败态保持只读，并允许安全刷新", async () => {
    const port = new ScreenHistoryPort([]);
    const presenter = screenPresenter(port);
    const screen = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    await screen.findByTestId("sync-history-empty");

    port.pageError = true;
    await fireEvent.press(screen.getByTestId("sync-history-refresh"));
    await screen.findByTestId("sync-history-failed");

    expect(screen.getByText(/encrypted ledger was not changed/i)).toBeTruthy();
    expect(screen.queryByText(/refund/i)).toBeNull();
    expect(syncHistoryText("zh", "title")).toBe("本地同步历史");
  });

  it("仅在注入 onBack 时显示 44pt 返回销售入口", async () => {
    const presenter = screenPresenter(new ScreenHistoryPort([]));
    const onBack = jest.fn<() => void>();
    const screen = await render(
      <SyncHistoryScreen
        onBack={onBack}
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenter}
      />,
    );
    const back = screen.getByTestId("sync-history-back");
    const backStyle = StyleSheet.flatten(back.props.style);

    expect(backStyle.minHeight).toBeGreaterThanOrEqual(
      SYNC_HISTORY_MIN_TOUCH_TARGET,
    );
    await fireEvent.press(back);
    expect(onBack).toHaveBeenCalledTimes(1);

    await screen.unmount();
    const presenterWithoutBack = screenPresenter(new ScreenHistoryPort([]));
    const screenWithoutBack = await render(
      <SyncHistoryScreen
        onExport={jest.fn<(serializedJson: string) => void>()}
        presenter={presenterWithoutBack}
      />,
    );
    expect(
      screenWithoutBack.queryByTestId("sync-history-back"),
    ).toBeNull();
  });
});
