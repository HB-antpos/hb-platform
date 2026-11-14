import type { LocalCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";
import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor, within } from "@testing-library/react-native";
import { StyleSheet } from "react-native";


import {
  CASH_MOVEMENT_MIN_TOUCH_TARGET,
  CashMovementPresenter,
  CashMovementScreen,
  type CashMovementRuntime,
  type CashMovementWriteResult,
} from "./index";

let mockLanguage = "en";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
    t: (key: string) => key,
  }),
}));

function movement(overrides: Partial<LocalCashMovement> = {}): LocalCashMovement {
  return {
    movementGuid: "00000000-0000-4000-8000-000000000001",
    direction: "In",
    reasonCode: "ChangeTopUp",
    amountCents: 5_000,
    note: null,
    voidsMovementGuid: null,
    businessDate: "2026-11-14",
    occurredAtIso: "2026-11-14T01:00:00.000Z",
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
    createdAtIso: "2026-11-14T01:00:00.000Z",
    ...overrides,
  };
}

function setup(movements: readonly LocalCashMovement[] = []) {
  const submit = jest.fn<CashMovementRuntime["submit"]>(async () => ({
    kind: "saved",
    movement: movement({ displayNumber: "CM-0009" }),
    drawer: "opened",
    print: "printed",
  }));
  const voidMovement = jest.fn<CashMovementRuntime["voidMovement"]>(async () => ({
    kind: "saved",
    movement: movement({ displayNumber: "CM-0010", direction: "Out" }),
    drawer: "opened",
    print: "printed",
  }));
  const reprint = jest.fn<CashMovementRuntime["reprint"]>(async () => ({ kind: "printed" }));
  const state = { movements, latestDailyCloseSavedAtIso: null as string | null };
  const runtime: CashMovementRuntime = {
    listToday: async () => ({
      businessDate: "2026-11-14",
      businessTimeZone: "Australia/Brisbane",
      movements: state.movements,
      latestDailyCloseSavedAtIso: state.latestDailyCloseSavedAtIso,
      cashInCents: 5_000,
      cashOutCents: 1_250,
    }),
    submit,
    voidMovement,
    reprint,
  };
  let counter = 0;
  const presenter = new CashMovementPresenter({
    runtime,
    createId: () => `10000000-0000-4000-8000-${String(++counter).padStart(12, "0")}`,
  });
  return { presenter, submit, voidMovement, reprint, state };
}

async function press(screen: Awaited<ReturnType<typeof render>>, testID: string) {
  await fireEvent.press(screen.getByTestId(testID));
}

describe("CashMovementScreen", () => {
  it("存入页：两个原因（默认补零钱）、金额小键盘与快捷键、确认键随金额变化，所有触控目标至少 44pt", async () => {
    mockLanguage = "en";
    const { presenter, submit } = setup();
    const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
    await waitFor(() => {
      expect(screen.getByText("Today: in $50.00 · out $12.50")).toBeTruthy();
    });

    expect(screen.getByTestId("cash-movement-reason-ChangeTopUp")).toBeTruthy();
    expect(screen.getByTestId("cash-movement-reason-OtherIn")).toBeTruthy();
    expect(screen.queryByTestId("cash-movement-reason-SafeDrop")).toBeNull();
    expect(screen.getByTestId("cash-movement-reason-ChangeTopUp").props.accessibilityState.checked).toBe(true);
    expect(screen.getByText("Take change from the safe and put it in the drawer.")).toBeTruthy();
    expect(screen.getByTestId("cash-movement-confirm").props.accessibilityState?.disabled ?? false).toBe(false);
    expect(screen.getByText("Confirm cash in")).toBeTruthy();

    const tooSmall: string[] = [];
    for (const testID of [
      "cash-movement-key-7",
      "cash-movement-key-decimal",
      "cash-movement-key-backspace",
      "cash-movement-key-clear",
      "cash-movement-quick-20",
      "cash-movement-confirm",
      "cash-movement-close",
    ]) {
      const minHeight = StyleSheet.flatten(screen.getByTestId(testID).props.style)?.minHeight;
      if (typeof minHeight !== "number" || minHeight < CASH_MOVEMENT_MIN_TOUCH_TARGET) {
        tooSmall.push(`${testID}:${String(minHeight)}`);
      }
    }
    expect(tooSmall).toEqual([]);

    await press(screen, "cash-movement-key-1");
    await press(screen, "cash-movement-key-2");
    await press(screen, "cash-movement-key-decimal");
    await press(screen, "cash-movement-key-5");
    expect(screen.getByTestId("cash-movement-amount").props.children).toBe("$12.5");
    await press(screen, "cash-movement-quick-100");
    expect(screen.getByTestId("cash-movement-amount").props.children).toBe("$100");
    expect(screen.getByText("Confirm cash in $100.00")).toBeTruthy();
    for (const value of [20, 50, 100, 200, 500]) {
      expect(screen.getByTestId(`cash-movement-quick-${value}`)).toBeTruthy();
    }
    await press(screen, "cash-movement-key-backspace");
    expect(screen.getByTestId("cash-movement-amount").props.children).toBe("$10");

    await press(screen, "cash-movement-confirm");
    await waitFor(() => {
      expect(submit).toHaveBeenCalledTimes(1);
    });
    expect(submit.mock.calls[0]![0]).toMatchObject({
      reasonCode: "ChangeTopUp",
      amountCents: 1_000,
      note: null,
    });
    await waitFor(() => {
      expect(screen.getByTestId("cash-movement-status")).toBeTruthy();
    });
    expect(within(screen.getByTestId("cash-movement-status")).getByText("Saved CM-0009.")).toBeTruthy();
    await screen.unmount();
  });

  it("备注输入框限长 200；其他存入的备注是必填提示，校验失败时显示中英文提示且不调用运行时", async () => {
    mockLanguage = "zh";
    try {
      const { presenter, submit } = setup();
      const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
      expect(screen.getByTestId("cash-movement-note").props.maxLength).toBe(200);
      expect(screen.getByTestId("cash-movement-note").props.placeholder).toBe("备注（选填）");

      await press(screen, "cash-movement-reason-OtherIn");
      expect(screen.getByTestId("cash-movement-note").props.placeholder).toBe("必须写备注（至少 2 个字）");
      expect(screen.getByText("必须写原因")).toBeTruthy();
      // 先不输金额点确认：金额提示。
      await press(screen, "cash-movement-confirm");
      expect(screen.getByTestId("cash-movement-validation").props.children).toBe("请输入大于 0 的金额（最多两位小数）");
      await press(screen, "cash-movement-key-5");
      await press(screen, "cash-movement-confirm");
      expect(screen.getByTestId("cash-movement-validation").props.children).toBe("这个原因必须写备注，至少 2 个字");
      expect(submit).not.toHaveBeenCalled();
      await fireEvent.changeText(screen.getByTestId("cash-movement-note"), "备用金");
      expect(screen.queryByTestId("cash-movement-validation")).toBeNull();
      expect(screen.getByText("确认存入 $5.00")).toBeTruthy();
      await screen.unmount();
    } finally {
      mockLanguage = "en";
    }
  });

  it("取出页只列交店长与小额现金支出；确认键文案为「确认取出」", async () => {
    mockLanguage = "zh";
    try {
      const { presenter, submit } = setup();
      const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
      await press(screen, "cash-movement-tab-out");
      expect(screen.queryByTestId("cash-movement-reason-OtherIn")).toBeNull();
      expect(screen.getByTestId("cash-movement-reason-SafeDrop").props.accessibilityState.checked).toBe(true);
      expect(screen.getByText("钱还在店里，日结后入现金池")).toBeTruthy();
      await press(screen, "cash-movement-reason-PaidOut");
      expect(screen.getByText("会自动生成后台「现金支出」")).toBeTruthy();
      await press(screen, "cash-movement-key-3");
      await fireEvent.changeText(screen.getByTestId("cash-movement-note"), "买牛奶");
      expect(screen.getByText("确认取出 $3.00")).toBeTruthy();
      await press(screen, "cash-movement-confirm");
      await waitFor(() => {
        expect(submit).toHaveBeenCalledTimes(1);
      });
      expect(submit.mock.calls[0]![0]).toMatchObject({ reasonCode: "PaidOut", amountCents: 300, note: "买牛奶" });
      await screen.unmount();
    } finally {
      mockLanguage = "en";
    }
  });

  it("保存成功但钱箱 / 打印失败：显示已记录并追加两条警告", async () => {
    mockLanguage = "en";
    const { presenter, submit } = setup();
    submit.mockResolvedValueOnce({
      kind: "saved",
      movement: movement({ displayNumber: "CM-0005" }),
      drawer: "failed",
      print: "failed",
    } satisfies CashMovementWriteResult);
    const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
    await press(screen, "cash-movement-key-5");
    await press(screen, "cash-movement-confirm");
    await waitFor(() => {
      expect(screen.getByTestId("cash-movement-warning-drawer")).toBeTruthy();
    });
    expect(screen.getByTestId("cash-movement-warning-print")).toBeTruthy();
    expect(screen.getByText("Saved CM-0005.")).toBeTruthy();
    await screen.unmount();
  });

  it("今日记录：最新在上，显示方向 / 带符号金额 / 收银员与授权人 / 上传状态；被拒时顶部有醒目横幅", async () => {
    mockLanguage = "zh";
    try {
      const rejected = movement({
        movementGuid: "00000000-0000-4000-8000-0000000000a1",
        displayNumber: "CM-0001",
        direction: "Out",
        reasonCode: "PaidOut",
        amountCents: 1_250,
        note: "milk",
        uploadState: "rejected",
        authorizedById: "boss",
        authorizedByName: "Boss",
        occurredAtIso: "2026-11-14T01:00:00.000Z",
      });
      const synced = movement({
        movementGuid: "00000000-0000-4000-8000-0000000000a2",
        displayNumber: "CM-0002",
        uploadState: "synced",
        occurredAtIso: "2026-11-14T02:00:00.000Z",
      });
      const { presenter } = setup([rejected, synced]);
      const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
      await press(screen, "cash-movement-tab-records");
      await waitFor(() => {
        expect(screen.getByTestId(`cash-movement-row-${synced.movementGuid}`)).toBeTruthy();
      });
      expect(screen.getByTestId("cash-movement-rejected-banner")).toBeTruthy();
      expect(
        screen.getByText("有 1 笔现金进出被服务端拒绝；日结仍包含它们，请联系后台"),
      ).toBeTruthy();
      const rows = screen.getAllByTestId(/^cash-movement-row-/u);
      expect(rows[0]!.props.testID).toBe(`cash-movement-row-${synced.movementGuid}`);
      expect(rows[1]!.props.testID).toBe(`cash-movement-row-${rejected.movementGuid}`);
      expect(screen.getByTestId(`cash-movement-amount-${rejected.movementGuid}`).props.children).toBe("−$12.50");
      expect(screen.getByTestId(`cash-movement-amount-${synced.movementGuid}`).props.children).toBe("+$50.00");
      expect(screen.getByTestId(`cash-movement-upload-${rejected.movementGuid}`).props.children).toBe("被拒");
      expect(screen.getByTestId(`cash-movement-upload-${synced.movementGuid}`).props.children).toBe("已上传");
      expect(within(screen.getByTestId(`cash-movement-row-${rejected.movementGuid}`)).getByText("Alice / Boss")).toBeTruthy();
      expect(within(screen.getByTestId(`cash-movement-row-${rejected.movementGuid}`)).getByText("小额现金支出")).toBeTruthy();
      await screen.unmount();
    } finally {
      mockLanguage = "en";
    }
  });

  it("没有被拒记录时不显示横幅；没有记录显示空状态；读取失败可重新读取", async () => {
    mockLanguage = "en";
    const { presenter } = setup([]);
    const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
    await press(screen, "cash-movement-tab-records");
    await waitFor(() => {
      expect(screen.getByTestId("cash-movement-empty").props.children).toBe("No cash movements today.");
    });
    expect(screen.queryByTestId("cash-movement-rejected-banner")).toBeNull();
    await screen.unmount();
  });

  it("冲正：不满足资格时按钮禁用并显示原因；满足时先填原因（≥2 字）再确认；已冲正的原记录显示标记", async () => {
    mockLanguage = "en";
    const original = movement({
      movementGuid: "00000000-0000-4000-8000-0000000000b1",
      displayNumber: "CM-0001",
      occurredAtIso: "2026-11-14T04:00:00.000Z",
    });
    const voidedOriginal = movement({
      movementGuid: "00000000-0000-4000-8000-0000000000b2",
      displayNumber: "CM-0002",
      occurredAtIso: "2026-11-14T05:00:00.000Z",
      voidedByMovementGuid: "00000000-0000-4000-8000-0000000000b3",
    });
    const voidRecord = movement({
      movementGuid: "00000000-0000-4000-8000-0000000000b3",
      displayNumber: "CM-0003",
      direction: "Out",
      voidsMovementGuid: voidedOriginal.movementGuid,
      occurredAtIso: "2026-11-14T05:10:00.000Z",
    });
    const { presenter, voidMovement, state } = setup([original, voidedOriginal, voidRecord]);
    // 日结在 03:00 保存过：CM-0001（04:00）之后才发生，不受影响。
    state.latestDailyCloseSavedAtIso = "2026-11-14T03:00:00.000Z";
    const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
    await press(screen, "cash-movement-tab-records");
    await waitFor(() => {
      expect(screen.getByTestId(`cash-movement-row-${original.movementGuid}`)).toBeTruthy();
    });
    expect(screen.getByTestId(`cash-movement-voided-${voidedOriginal.movementGuid}`)).toBeTruthy();
    expect(screen.getByTestId(`cash-movement-void-${voidedOriginal.movementGuid}`).props.accessibilityState.disabled).toBe(true);
    expect(screen.getByTestId(`cash-movement-void-reason-${voidedOriginal.movementGuid}`).props.children).toBe("Already voided.");
    expect(screen.getByTestId(`cash-movement-void-reason-${voidRecord.movementGuid}`).props.children).toBe("A void record cannot be voided.");
    expect(screen.queryByTestId(`cash-movement-void-reason-${original.movementGuid}`)).toBeNull();

    await press(screen, `cash-movement-void-${original.movementGuid}`);
    expect(screen.getByTestId("cash-movement-void-panel")).toBeTruthy();
    await press(screen, "cash-movement-void-confirm");
    expect(screen.getByTestId("cash-movement-void-validation").props.children).toBe(
      "Write the reason for the void (at least 2 characters).",
    );
    expect(voidMovement).not.toHaveBeenCalled();
    await fireEvent.changeText(screen.getByTestId("cash-movement-void-note"), "wrong amount");
    await press(screen, "cash-movement-void-confirm");
    await waitFor(() => {
      expect(voidMovement).toHaveBeenCalledTimes(1);
    });
    expect(voidMovement.mock.calls[0]![0]).toMatchObject({
      originalMovementGuid: original.movementGuid,
      note: "wrong amount",
    });
    await waitFor(() => {
      expect(screen.queryByTestId("cash-movement-void-panel")).toBeNull();
    });
    expect(screen.getByText("Void saved CM-0010.")).toBeTruthy();
    await screen.unmount();
  });

  it("其后已保存日结的记录：冲正禁用并说明原因；补打按钮调用运行时", async () => {
    mockLanguage = "en";
    const early = movement({
      movementGuid: "00000000-0000-4000-8000-0000000000c1",
      occurredAtIso: "2026-11-14T01:00:00.000Z",
    });
    const { presenter, reprint, state } = setup([early]);
    state.latestDailyCloseSavedAtIso = "2026-11-14T03:00:00.000Z";
    const screen = await render(<CashMovementScreen onClose={jest.fn()} presenter={presenter} />);
    await press(screen, "cash-movement-tab-records");
    await waitFor(() => {
      expect(screen.getByTestId(`cash-movement-row-${early.movementGuid}`)).toBeTruthy();
    });
    expect(screen.getByTestId(`cash-movement-void-${early.movementGuid}`).props.accessibilityState.disabled).toBe(true);
    expect(screen.getByTestId(`cash-movement-void-reason-${early.movementGuid}`).props.children).toBe(
      "Daily close was saved after this record, so it cannot be voided.",
    );
    await press(screen, `cash-movement-reprint-${early.movementGuid}`);
    await waitFor(() => {
      expect(reprint).toHaveBeenCalledWith(early.movementGuid);
    });
    await waitFor(() => {
      expect(screen.getByText("The slip was sent to the printer.")).toBeTruthy();
    });
    await screen.unmount();
  });

  it("visible=false 时不渲染内容（主管授权期间临时隐藏），恢复后状态仍在", async () => {
    mockLanguage = "en";
    const { presenter } = setup();
    const screen = await render(
      <CashMovementScreen onClose={jest.fn()} presenter={presenter} visible={false} />,
    );
    expect(screen.queryByTestId("cash-movement-screen")).toBeNull();
    await screen.rerender(<CashMovementScreen onClose={jest.fn()} presenter={presenter} visible />);
    expect(screen.getByTestId("cash-movement-screen")).toBeTruthy();
    await screen.unmount();
  });
});
