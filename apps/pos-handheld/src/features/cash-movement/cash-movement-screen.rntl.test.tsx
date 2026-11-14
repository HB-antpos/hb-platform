import type {
  CashMovementSummary,
  LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";
import { StyleSheet } from "react-native";


import {
  CASH_MOVEMENT_MIN_TOUCH_TARGET,
  CashMovementPresenter,
  CashMovementScreen,
  type CashMovementServicePort,
  type CashMovementSubmitInput,
  type CashMovementTodaySnapshot,
} from "./index";

let mockLanguage = "en";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
  }),
}));

const TODAY = "2026-11-14";
const G1 = "00000000-0000-4000-8000-000000000001";
const G2 = "00000000-0000-4000-8000-000000000002";
const G3 = "00000000-0000-4000-8000-000000000003";

function movement(overrides: Partial<LocalCashMovement> = {}): LocalCashMovement {
  return {
    movementGuid: G1,
    direction: "In",
    reasonCode: "ChangeTopUp",
    amountCents: 5_000,
    note: null,
    voidsMovementGuid: null,
    businessDate: TODAY,
    occurredAtIso: "2026-11-14T02:05:00.000Z",
    cashierId: "c1",
    cashierName: "Alice",
    authorizedById: null,
    authorizedByName: null,
    storeCode: "S1",
    deviceCode: "D1",
    displayNumber: "CM-0001",
    voidedByMovementGuid: null,
    auditEventId: "00000000-0000-4000-8000-0000000000a1",
    uploadState: "pending",
    uploadAttempts: 0,
    uploadErrorCode: null,
    uploadedAtIso: null,
    createdAtIso: "2026-11-14T02:05:00.000Z",
    ...overrides,
  };
}

class FakeService implements CashMovementServicePort {
  public movements: LocalCashMovement[] = [];
  public latestDailyCloseSavedAtIso: string | null = null;
  public submitCalls: CashMovementSubmitInput[] = [];
  public voidCalls: { movementGuid: string; note: string }[] = [];
  public reprintCalls: string[] = [];

  public async listToday(): Promise<CashMovementTodaySnapshot> {
    return {
      businessDate: TODAY,
      businessTimeZone: "Australia/Brisbane",
      movements: this.movements,
      latestDailyCloseSavedAtIso: this.latestDailyCloseSavedAtIso,
    };
  }
  public async summary(): Promise<CashMovementSummary> {
    return {
      cashInCents: 5_000,
      cashOutCents: 1_000,
      byReason: { ChangeTopUp: 5_000, OtherIn: 0, SafeDrop: 1_000, PaidOut: 0 },
    };
  }
  public async submit(input: CashMovementSubmitInput) {
    this.submitCalls.push(input);
    return { kind: "saved" as const, movement: movement(), drawer: "ok" as const, print: "ok" as const };
  }
  public async void(input: { movementGuid: string; note: string }) {
    this.voidCalls.push(input);
    return { kind: "saved" as const, movement: movement(), drawer: "ok" as const, print: "ok" as const };
  }
  public async reprint(guid: string) {
    this.reprintCalls.push(guid);
    return { kind: "printed" as const };
  }
}

async function renderScreen(service = new FakeService()) {
  const presenter = new CashMovementPresenter({ service });
  const onClose = jest.fn();
  const screen = await render(<CashMovementScreen onClose={onClose} presenter={presenter} />);
  await waitFor(() => expect(presenter.getState().kind).toBe("ready"));
  return { screen, presenter, service, onClose };
}

describe("CashMovementScreen", () => {
  it("存入页签只列补零钱与其他存入，默认选第一项；取出页签只列交店长与小额现金支出", async () => {
    const { screen } = await renderScreen();
    expect(screen.getByTestId("cash-movement-reason-ChangeTopUp").props.accessibilityState.checked).toBe(true);
    expect(screen.getByTestId("cash-movement-reason-OtherIn")).toBeTruthy();
    expect(screen.queryByTestId("cash-movement-reason-SafeDrop")).toBeNull();
    expect(screen.getByText("Take change from the safe and put it in the drawer.")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("cash-movement-tab-out"));
    expect(screen.getByTestId("cash-movement-reason-SafeDrop").props.accessibilityState.checked).toBe(true);
    expect(screen.getByTestId("cash-movement-reason-PaidOut")).toBeTruthy();
    expect(screen.queryByTestId("cash-movement-reason-ChangeTopUp")).toBeNull();
    expect(screen.getByText("The cash stays in the store and joins the cash pool after daily close.")).toBeTruthy();
    await screen.unmount();
  });

  it("小键盘与快捷金额更新金额与确认键文案；备注输入框限长 200 且按原因标注必填", async () => {
    const { screen } = await renderScreen();
    for (const key of ["1", "2", "dot", "5", "0", "7"]) {
      await fireEvent.press(screen.getByTestId(`cash-movement-key-${key}`));
    }
    expect(screen.getByTestId("cash-movement-amount").props.children).toBe("$12.50");
    expect(screen.getByText("Confirm cash in $12.50")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("cash-movement-quick-100"));
    expect(screen.getByText("Confirm cash in $100.00")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("cash-movement-key-backspace"));
    expect(screen.getByText("Confirm cash in $10.00")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("cash-movement-key-clear"));
    expect(screen.getByText("Confirm cash in $0.00")).toBeTruthy();

    expect(screen.getByTestId("cash-movement-note").props.maxLength).toBe(200);
    expect(screen.getByText("Note (optional)")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("cash-movement-reason-OtherIn"));
    expect(screen.getByText("Note (required)")).toBeTruthy();
    await screen.unmount();
  });

  it("校验提示：金额为 0、必填备注缺失；通过后提交整数分金额并显示已保存", async () => {
    const { screen, service } = await renderScreen();
    await fireEvent.press(screen.getByTestId("cash-movement-submit"));
    expect(screen.getByText("Enter an amount greater than 0 (up to 2 decimals).")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("cash-movement-reason-OtherIn"));
    await fireEvent.press(screen.getByTestId("cash-movement-quick-20"));
    await fireEvent.press(screen.getByTestId("cash-movement-submit"));
    expect(screen.getByText("This reason needs a note of at least 2 characters.")).toBeTruthy();
    expect(service.submitCalls).toHaveLength(0);

    await fireEvent.changeText(screen.getByTestId("cash-movement-note"), "found coins");
    await fireEvent.press(screen.getByTestId("cash-movement-submit"));
    await waitFor(() => expect(screen.getByText("Saved.")).toBeTruthy());
    expect(service.submitCalls).toEqual([{ reasonCode: "OtherIn", amountCents: 2_000, note: "found coins" }]);
    await screen.unmount();
  });

  it("关键触控目标不小于 48", async () => {
    const { screen } = await renderScreen();
    for (const testID of [
      "cash-movement-close",
      "cash-movement-tab-in",
      "cash-movement-tab-records",
      "cash-movement-key-5",
      "cash-movement-key-backspace",
      "cash-movement-quick-20",
      "cash-movement-submit",
    ]) {
      expect(StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight).toBeGreaterThanOrEqual(
        CASH_MOVEMENT_MIN_TOUCH_TARGET,
      );
    }
    await screen.unmount();
  });

  it("今日记录：最新在上、带符号金额、收银员 / 授权人、备注、上传状态、已冲正；被拒记录有横幅与醒目标记", async () => {
    const service = new FakeService();
    service.movements = [
      movement({ movementGuid: G1, displayNumber: "CM-0001", voidedByMovementGuid: G2 }),
      movement({
        movementGuid: G2,
        displayNumber: "CM-0002",
        direction: "Out",
        voidsMovementGuid: G1,
        occurredAtIso: "2026-11-14T02:30:00.000Z",
        note: "输错金额",
        uploadState: "rejected",
      }),
      movement({
        movementGuid: G3,
        displayNumber: "CM-0003",
        direction: "Out",
        reasonCode: "PaidOut",
        amountCents: 1_250,
        note: "tape",
        occurredAtIso: "2026-11-14T03:00:00.000Z",
        authorizedByName: "Boss",
        uploadState: "synced",
      }),
    ];
    const { screen } = await renderScreen(service);
    await fireEvent.press(screen.getByTestId("cash-movement-tab-records"));
    await waitFor(() => expect(screen.getByTestId("cash-movement-record-" + G3)).toBeTruthy());

    expect(screen.getByTestId("cash-movement-rejected-banner")).toBeTruthy();
    expect(
      screen.getByText("1 cash movements were rejected by the server. Daily close still includes them; contact the back office."),
    ).toBeTruthy();
    expect(screen.getByTestId("cash-movement-summary").props.children).toBe("Cash in $50.00 · Cash out $10.00");
    expect(screen.getByText("Cash out · Paid out")).toBeTruthy();
    expect(screen.getByText("-$12.50")).toBeTruthy();
    expect(screen.getByText("Cashier: Alice / Boss")).toBeTruthy();
    expect(screen.getByText("Note: tape")).toBeTruthy();
    expect(screen.getByText("Uploaded")).toBeTruthy();
    expect(screen.getByText("Rejected")).toBeTruthy();
    expect(screen.getByText("Pending upload")).toBeTruthy();
    expect(screen.getByText("Voided")).toBeTruthy();
    expect(screen.getByText("Voids CM-0001")).toBeTruthy();
    // 冲正记录本身不能再冲正：没有冲正按钮，但可补打。
    expect(screen.queryByTestId("cash-movement-void-" + G2)).toBeNull();
    expect(screen.getByTestId("cash-movement-reprint-" + G2)).toBeTruthy();
    // 最新在上。
    const order = screen.getAllByTestId(/^cash-movement-record-/).map((node) => node.props.testID);
    expect(order).toEqual([`cash-movement-record-${G3}`, `cash-movement-record-${G2}`, `cash-movement-record-${G1}`]);
    await screen.unmount();
  });

  it("没有被拒记录时不显示横幅；空列表有提示", async () => {
    const { screen } = await renderScreen();
    await fireEvent.press(screen.getByTestId("cash-movement-tab-records"));
    await waitFor(() => expect(screen.getByTestId("cash-movement-empty")).toBeTruthy());
    expect(screen.queryByTestId("cash-movement-rejected-banner")).toBeNull();
    await screen.unmount();
  });

  it("冲正：不满足资格的记录按钮禁用并显示原因；满足时填写原因后确认；冲正记录本身没有冲正按钮；补打可用", async () => {
    const service = new FakeService();
    service.movements = [
      movement({ movementGuid: G1, displayNumber: "CM-0001", occurredAtIso: "2026-11-14T02:00:00.000Z" }),
      movement({ movementGuid: G3, displayNumber: "CM-0003", occurredAtIso: "2026-11-14T03:00:00.000Z" }),
    ];
    service.latestDailyCloseSavedAtIso = "2026-11-14T02:30:00.000Z";
    const { screen } = await renderScreen(service);
    await fireEvent.press(screen.getByTestId("cash-movement-tab-records"));
    await waitFor(() => expect(screen.getByTestId("cash-movement-record-" + G1)).toBeTruthy());

    // G1 发生在日结之前 → 不可冲正，并写明原因；G3 在日结之后 → 可冲正。
    expect(screen.getByTestId("cash-movement-void-" + G1).props.accessibilityState.disabled).toBe(true);
    expect(screen.getByTestId("cash-movement-void-reason-" + G1).props.children).toBe(
      "A daily close was saved after this record, so it can no longer be voided.",
    );
    await fireEvent.press(screen.getByTestId("cash-movement-void-" + G1));
    expect(screen.queryByTestId("cash-movement-void-note")).toBeNull();

    expect(screen.getByTestId("cash-movement-void-" + G3).props.accessibilityState.disabled).toBe(false);
    await fireEvent.press(screen.getByTestId("cash-movement-void-" + G3));
    expect(screen.getByTestId("cash-movement-void-note").props.maxLength).toBe(200);
    await fireEvent.changeText(screen.getByTestId("cash-movement-void-note"), "x");
    await fireEvent.press(screen.getByTestId("cash-movement-void-confirm"));
    expect(screen.getByText("Enter a void reason of at least 2 characters.")).toBeTruthy();
    expect(service.voidCalls).toHaveLength(0);
    await fireEvent.changeText(screen.getByTestId("cash-movement-void-note"), "wrong amount");
    await fireEvent.press(screen.getByTestId("cash-movement-void-confirm"));
    await waitFor(() => expect(screen.getByText("Voided.")).toBeTruthy());
    expect(service.voidCalls).toEqual([{ movementGuid: G3, note: "wrong amount" }]);

    await fireEvent.press(screen.getByTestId("cash-movement-reprint-" + G3));
    await waitFor(() => expect(service.reprintCalls).toEqual([G3]));
    await screen.unmount();
  });

  it("中文界面只显示中文文案；关闭按钮触发 onClose", async () => {
    mockLanguage = "zh";
    try {
      const { screen, onClose } = await renderScreen();
      expect(screen.getByText("现金存入 / 取出")).toBeTruthy();
      expect(screen.getByText("补零钱")).toBeTruthy();
      expect(screen.getByText("确认存入 $0.00")).toBeTruthy();
      expect(screen.queryByText("Cash in")).toBeNull();
      await fireEvent.press(screen.getByTestId("cash-movement-tab-out"));
      expect(screen.getByText("交店长 / 入保险柜")).toBeTruthy();
      expect(screen.getByText("确认取出 $0.00")).toBeTruthy();
      await fireEvent.press(screen.getByTestId("cash-movement-submit"));
      expect(screen.getByText("请输入大于 0 的金额（最多两位小数）。")).toBeTruthy();
      await fireEvent.press(screen.getByTestId("cash-movement-close"));
      expect(onClose).toHaveBeenCalledTimes(1);
      await screen.unmount();
    } finally {
      mockLanguage = "en";
    }
  });
});
