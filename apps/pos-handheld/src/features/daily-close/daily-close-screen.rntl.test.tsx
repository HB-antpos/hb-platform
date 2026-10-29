import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";
import {
  Keyboard,
  Platform,
  ScrollView,
  StyleSheet,
  TextInput,
} from "react-native";

import {
  DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED,
  DAILY_CLOSE_KEYBOARD_AVOIDING_BEHAVIOR,
  DAILY_CLOSE_MIN_TOUCH_TARGET,
  DailyCloseScreen,
  resolveDailyCloseAccess,
  type DailyCloseScreenPresenter,
  type DailyCloseState,
} from "./index";

import type { DailyCloseArchive, DailyCloseSummary } from "@/core/contracts";

let mockLanguage = "en";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
  }),
}));

jest.mock("@react-native-community/datetimepicker");

describe("DailyCloseScreen", () => {
  it("点钞与汇总提交是两个单列状态且关键操作至少 48px", async () => {
    const presenter = new ScreenPresenter();
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    expect(screen.getByTestId("handheld-state-daily-close-count")).toBeTruthy();
    expect(screen.queryByTestId("handheld-state-daily-close-summary")).toBeNull();
    expect(screen.queryByTestId("daily-close-save")).toBeNull();

    await fireEvent.press(screen.getByTestId("daily-close-review-summary"));

    expect(screen.getByTestId("handheld-state-daily-close-summary")).toBeTruthy();
    for (const testID of ["daily-close-summary-back", "daily-close-save"]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(48);
    }
  });

  it("英文界面只显示英文日结文案", async () => {
    mockLanguage = "en";
    const presenter = new ScreenPresenter();
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    expect(screen.getByText("Daily close")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("daily-close-review-summary"));
    expect(screen.getByText("Cash")).toBeTruthy();
    expect(screen.queryByText("日结")).toBeNull();
    expect(screen.queryByText("日结 / Daily close")).toBeNull();
    await screen.unmount();
  });

  it("中文界面只显示中文日结文案", async () => {
    mockLanguage = "zh";
    try {
      const presenter = new ScreenPresenter();
      const screen = await render(<DailyCloseScreen presenter={presenter} />);

      expect(screen.getByText("日结")).toBeTruthy();
      await fireEvent.press(screen.getByTestId("daily-close-review-summary"));
      expect(screen.getByText("现金")).toBeTruthy();
      expect(screen.queryByText("Daily close")).toBeNull();
      expect(screen.queryByText("日结 / Daily close")).toBeNull();
      await screen.unmount();
    } finally {
      mockLanguage = "en";
    }
  });

  it("单列点钞显示 11 种面额并把所有触控目标保持为至少 48px", async () => {
    const presenter = new ScreenPresenter();
    const onBack = jest.fn();
    const screen = await render(
      <DailyCloseScreen onBack={onBack} presenter={presenter} />,
    );

    expect(screen.getByText("Daily close")).toBeTruthy();
    expect(
      StyleSheet.flatten(
        screen.getByTestId("daily-close-workspace").props.style,
      ).flexDirection,
    ).toBe("column");
    expect(screen.getAllByTestId(/^daily-close-count-/)).toHaveLength(11);
    for (const testID of [
      "daily-close-back",
      "daily-close-refresh",
      "daily-close-show-count",
      "daily-close-show-history",
      "daily-close-review-summary",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(DAILY_CLOSE_MIN_TOUCH_TARGET);
    }

    await fireEvent.changeText(
      screen.getByTestId("daily-close-count-10000"),
      "2",
    );
    expect(presenter.counts).toEqual([
      { denominationCents: 10_000, quantity: 2 },
    ]);
    expect(screen.getByTestId("daily-close-count-10000").props.value).toBe("2");
    expect(presenter.countedCashCents).toBe(20_000);
    await fireEvent.press(screen.getByTestId("daily-close-review-summary"));
    expect(
      StyleSheet.flatten(screen.getByTestId("daily-close-save").props.style)
        .minHeight,
    ).toBeGreaterThanOrEqual(DAILY_CLOSE_MIN_TOUCH_TARGET);
    await fireEvent.press(screen.getByTestId("daily-close-save"));
    expect(presenter.saveCalls).toBe(1);
    await fireEvent.press(screen.getByTestId("daily-close-back"));
    expect(onBack).toHaveBeenCalledTimes(1);
    await screen.unmount();
  });

  it("切换点钞和历史时保留 Presenter 上下文，并允许面额输入唤起系统键盘", async () => {
    const presenter = new ScreenPresenter();
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    await waitFor(() => {
      expect(
        screen.getByTestId("daily-close-show-count").props.accessibilityState
          .selected,
      ).toBe(true);
    });
    expect(
      screen.getByTestId("daily-close-count-10000").props.showSoftInputOnFocus,
    ).toBe(true);
    expect(
      screen.getByText(
        /The numeric keyboard opens automatically for counting; the focused field stays visible above it/,
      ),
    ).toBeTruthy();

    expect(() =>
      fireEvent.press(screen.getByTestId("daily-close-show-history")),
    ).not.toThrow();
    await waitFor(() => {
      expect(
        screen.getByTestId("daily-close-show-history").props.accessibilityState
          .selected,
      ).toBe(true);
    });
    expect(
      screen.getByTestId("daily-close-show-count").props.accessibilityState
        .selected,
    ).toBe(false);
    expect(screen.queryByTestId("daily-close-count-10000")).toBeNull();

    expect(() =>
      fireEvent.press(screen.getByTestId("daily-close-show-count")),
    ).not.toThrow();
    await waitFor(() => {
      expect(
        screen.getByTestId("daily-close-show-count").props.accessibilityState
          .selected,
      ).toBe(true);
    });
    expect(
      screen.getByTestId("daily-close-count-10000").props.showSoftInputOnFocus,
    ).toBe(true);
    await screen.unmount();
  });

  it("首次进入自动请求 $100 键盘，History 收起键盘且再次 Count 重新聚焦", async () => {
    const presenter = new ScreenPresenter();
    const focusInput = jest.spyOn(TextInput.prototype, "focus");
    const dismissKeyboard = jest.spyOn(Keyboard, "dismiss");
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    const firstCountInput = screen.getByTestId("daily-close-count-10000");
    expect(firstCountInput.props.autoFocus).toBe(true);
    expect(firstCountInput.props.showSoftInputOnFocus).toBe(true);
    expect(screen.getByTestId("daily-close-count-5000").props.autoFocus).toBe(
      false,
    );

    focusInput.mockClear();
    dismissKeyboard.mockClear();
    await fireEvent.press(screen.getByTestId("daily-close-show-history"));
    expect(dismissKeyboard).toHaveBeenCalledTimes(1);
    expect(focusInput).not.toHaveBeenCalled();

    await fireEvent.press(screen.getByTestId("daily-close-show-count"));
    expect(focusInput).toHaveBeenCalledTimes(1);
    expect(
      screen.getByTestId("daily-close-count-10000").props.showSoftInputOnFocus,
    ).toBe(true);

    await screen.unmount();
    focusInput.mockRestore();
    dismissKeyboard.mockRestore();
  });

  it("营业日打开日期弹层并关闭键盘，只有点钞输入需要滚动到可见区域", async () => {
    const presenter = new ScreenPresenter();
    const revealFocusedInput = jest.spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    );
    const dismissKeyboard = jest.spyOn(Keyboard, "dismiss");
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    expect(DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED).toBe(Platform.OS !== "ios");
    expect(DAILY_CLOSE_KEYBOARD_AVOIDING_BEHAVIOR).toBe("height");
    if (Platform.OS === "ios") {
      expect(DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED).toBe(false);
    }
    expect(
      StyleSheet.flatten(
        screen.getByTestId("daily-close-keyboard-avoider").props.style,
      ).flex,
    ).toBe(1);

    const summaryScroll = screen.getByTestId("daily-close-summary-scroll");
    expect(summaryScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
    expect(summaryScroll.props.keyboardDismissMode).toBe("interactive");
    expect(summaryScroll.props.keyboardShouldPersistTaps).toBe("handled");

    dismissKeyboard.mockClear();
    await fireEvent.press(screen.getByTestId("daily-close-business-date"));
    expect(dismissKeyboard).toHaveBeenCalledTimes(1);
    expect(
      screen.getByTestId("daily-close-business-date-modal"),
    ).toBeTruthy();
    expect(
      screen.getByTestId("daily-close-business-date").props.onChangeText,
    ).toBeUndefined();

    await fireEvent(screen.getByTestId("daily-close-count-10000"), "focus", {
      target: 202,
    });
    expect(revealFocusedInput).toHaveBeenCalledTimes(1);
    expect(revealFocusedInput).toHaveBeenCalledWith(202, 16, true);

    await screen.unmount();
    revealFocusedInput.mockRestore();
    dismissKeyboard.mockRestore();
  });

  it("选择营业日后仍由刷新提交并重新加载汇总", async () => {
    const presenter = new ScreenPresenter();
    const screen = await render(<DailyCloseScreen presenter={presenter} />);
    await waitFor(() => expect(presenter.loadCalls).toBe(1));

    await fireEvent.press(screen.getByTestId("daily-close-business-date"));
    await fireEvent(
      screen.getByTestId("daily-close-business-date-picker"),
      "change",
      { type: "set" },
      new Date(2026, 6, 27, 12),
    );
    await fireEvent.press(
      screen.getByTestId("daily-close-business-date-confirm"),
    );
    expect(presenter.businessDates).toEqual([]);

    await fireEvent.press(screen.getByTestId("daily-close-refresh"));
    expect(presenter.businessDates).toEqual(["2026-07-27"]);
    expect(presenter.loadCalls).toBe(2);
  });

  it("仅 View 权限隐藏保存和补打，但仍显示汇总与历史归档", async () => {
    const presenter = new ScreenPresenter({
      permissions: ["Permissions.PosTerminal.DailyClose.View"],
    });
    const screen = await render(<DailyCloseScreen presenter={presenter} />);

    expect(screen.getByText("Cash")).toBeTruthy();
    expect(screen.getByText(/\$12\.00.*-\$2\.00.*\$10\.00/)).toBeTruthy();
    // 汇总里单独显示现金进位，应有现金在其后。
    expect(screen.getByText("Cash rounding")).toBeTruthy();
    expect(screen.getByText("Expected cash")).toBeTruthy();
    expect(screen.queryByTestId("daily-close-save")).toBeNull();
    await fireEvent.press(screen.getByTestId("daily-close-show-history"));
    expect(screen.getByTestId("daily-close-history-close-old")).toBeTruthy();
    expect(screen.queryByTestId("daily-close-reprint")).toBeNull();
    await screen.unmount();
  });
});

class ScreenPresenter implements DailyCloseScreenPresenter {
  public readonly businessDates: string[] = [];
  public readonly counts: {
    denominationCents: number;
    quantity: number;
  }[] = [];
  public loadCalls = 0;
  public saveCalls = 0;
  private readonly listeners = new Set<() => void>();
  private state: DailyCloseState;

  public constructor(
    options: Partial<{ permissions: readonly string[] }> = {},
  ) {
    const selectedArchive = archive();
    const summary = closeSummary();
    this.state = {
      access: resolveDailyCloseAccess(
        options.permissions ?? [
          "Permissions.PosTerminal.DailyClose.View",
          "Permissions.PosTerminal.DailyClose.Save",
          "Permissions.PosTerminal.DailyClose.Reprint",
        ],
      ),
      activePane: "count",
      archives: [selectedArchive],
      businessDate: "2026-07-28",
      busy: false,
      coinsSubtotalCents: 0,
      countedCashCents: 0,
      counts: selectedArchive.denominations.map((entry) => ({
        ...entry,
        quantity: 0,
        subtotalCents: 0,
      })),
      kind: "ready",
      notesSubtotalCents: 0,
      selectedArchive,
      statusCode: null,
      summary,
      varianceCents: -summary.expectedCashCents,
    };
  }

  public readonly getState = () => this.state;
  public get countedCashCents() {
    return this.state.countedCashCents;
  }
  public readonly subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };
  public async load() {
    this.loadCalls += 1;
  }
  public destroy() {}
  public setBusinessDate(value: string) {
    this.businessDates.push(value);
    this.patch({ businessDate: value });
    return true;
  }
  public setCount(denominationCents: number, quantity: number) {
    this.counts.push({ denominationCents, quantity });
    const counts = this.state.counts.map((count) =>
      count.denominationCents === denominationCents
        ? {
            ...count,
            quantity,
            subtotalCents: count.denominationCents * quantity,
          }
        : count,
    );
    const countedCashCents = counts.reduce(
      (total, count) => total + count.subtotalCents,
      0,
    );
    this.patch({
      coinsSubtotalCents: counts
        .filter((count) => count.denominationCents < 100)
        .reduce((total, count) => total + count.subtotalCents, 0),
      countedCashCents,
      counts,
      notesSubtotalCents: counts
        .filter((count) => count.denominationCents >= 100)
        .reduce((total, count) => total + count.subtotalCents, 0),
      varianceCents: countedCashCents - this.state.summary!.expectedCashCents,
    });
    return true;
  }
  public async saveAndPrint() {
    this.saveCalls += 1;
  }
  public showCount() {
    this.patch({ activePane: "count" });
  }
  public showHistory() {
    this.patch({ activePane: "history" });
  }
  public selectArchive(closeId: string) {
    if (this.state.selectedArchive?.closeId === closeId) return;
  }
  public async reprintSelected() {}

  private patch(patch: Partial<DailyCloseState>) {
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
}

function closeSummary(): DailyCloseSummary {
  return {
    businessDate: "2026-07-28",
    periodFromIso: "2026-07-27T14:00:00.000Z",
    periodToIso: "2026-07-28T14:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    orderCount: 4,
    returnQuantity: "2",
    tenders: [
      {
        method: "cash",
        salesCents: 1_200,
        refundCents: -200,
        netCents: 1_000,
      },
      {
        method: "card",
        salesCents: 2_000,
        refundCents: 0,
        netCents: 2_000,
      },
      {
        method: "voucher",
        salesCents: 500,
        refundCents: -100,
        netCents: 400,
      },
    ],
    cashRoundingCents: 0,
    expectedCashCents: 1_000,
  };
}

function archive(): DailyCloseArchive {
  return {
    ...closeSummary(),
    closeId: "close-old",
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedAtIso: "2026-07-28T08:00:00.000Z",
    denominations: [
      10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5,
    ].map((denominationCents) => ({
      denominationCents:
        denominationCents as DailyCloseArchive["denominations"][number]["denominationCents"],
      quantity: 0,
      subtotalCents: 0,
    })),
    notesSubtotalCents: 0,
    coinsSubtotalCents: 0,
    countedCashCents: 0,
    varianceCents: -1_000,
  };
}
