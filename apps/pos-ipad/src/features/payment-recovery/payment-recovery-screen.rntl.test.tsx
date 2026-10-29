import { beforeEach, expect, jest, test } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";
import { Dimensions, StyleSheet } from "react-native";

import {
  PAYMENT_RECOVERY_MIN_TOUCH_TARGET,
  PaymentRecoveryScreen,
} from "./payment-recovery-screen";
import type {
  ManualPaymentVerificationInput,
  PaymentRecoveryCenterService,
  PaymentRecoveryCenterState,
  PaymentRecoveryRecord,
} from "./payment-recovery-types";

let mockLanguage = "en";
jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
  }),
}));

const pendingRecord: PaymentRecoveryRecord = {
  id: "attempt-1",
  orderGuid: "11854d1a-9c27-4a04-86e1-b7f8f606c688",
  occurredAtIso: "2026-09-10T13:17:49.000Z",
  amountCents: 99,
  status: "result-unknown",
  terminalName: "745710984001 · Lane 1",
  transactionReference: "2609101317495BC4",
  receiptReference: null,
  lines: [{ id: "line-1", name: "OPEN ITEM", quantity: "1", amountCents: 99 }],
  events: [
    {
      id: "event-1",
      occurredAtIso: "2026-09-10T13:17:49.000Z",
      code: "attempt-created",
      source: "system",
    },
  ],
};

test("已有付款待补订单时保留恢复入口，不允许再次人工改写", async () => {
  const { service } = createService({ ...pendingRecord, status: "charged-order-incomplete" });
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  expect(screen.getByTestId("payment-recovery-recover-original")).toBeTruthy();
  expect(screen.queryByTestId("payment-recovery-open-manual")).toBeNull();
});

function createService(
  record: PaymentRecoveryRecord = pendingRecord,
  refundSettlements: PaymentRecoveryCenterState["refundSettlements"] = [],
) {
  let state: PaymentRecoveryCenterState = {
    filter: "pending",
    keyword: "",
    records: [record],
    selectedRecordId: record.id,
    refundSettlements,
    loading: false,
    refreshing: false,
    action: "idle",
    errorCode: null,
  };
  const listeners = new Set<() => void>();
  const submissions: ManualPaymentVerificationInput[] = [];
  const recoveries: string[] = [];
  const refundResolutions: { attemptId: string; note: string }[] = [];
  const service: PaymentRecoveryCenterService = {
    getState: () => state,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setFilter(filter) {
      state = { ...state, filter };
      listeners.forEach((listener) => listener());
    },
    setKeyword(keyword) {
      state = { ...state, keyword };
      listeners.forEach((listener) => listener());
    },
    selectRecord(selectedRecordId) {
      state = { ...state, selectedRecordId };
      listeners.forEach((listener) => listener());
    },
    refresh: jest.fn(async () => undefined),
    recoverOriginalPayment: jest.fn(async (recordId: string) => {
      recoveries.push(recordId);
    }),
    submitManualVerification: jest.fn(async (input: ManualPaymentVerificationInput) => {
      submissions.push(input);
    }),
    checkRefundSettlements: jest.fn(async () => undefined),
    acknowledgeRefundSettlementFailure: jest.fn(async (input: { attemptId: string; note: string }) => {
      refundResolutions.push(input);
    }),
  };
  return { service, submissions, recoveries, refundResolutions };
}

beforeEach(() => {
  mockLanguage = "en";
  Dimensions.set({
    window: { width: 1_024, height: 768, scale: 2, fontScale: 1 },
    screen: { width: 1_024, height: 768, scale: 2, fontScale: 1 },
  });
});

test("恢复中心按设计展示双栏、原支付证据、历史和不阻断下一单入口", async () => {
  const { service, recoveries } = createService();
  const onBack = jest.fn();
  const screen = await render(<PaymentRecoveryScreen onBack={onBack} service={service} />);

  expect(screen.getByText("Payment recovery centre")).toBeTruthy();
  expect(screen.getByTestId("payment-recovery-list-pane")).toBeTruthy();
  expect(screen.getByTestId("payment-recovery-details-pane")).toBeTruthy();
  expect(screen.getAllByText("AU$0.99").length).toBeGreaterThan(0);
  expect(screen.getByText("2609101317495BC4")).toBeTruthy();
  expect(screen.getByText("Card payment attempt saved")).toBeTruthy();
  expect(screen.getByText("Do not charge again. Check the original provider attempt first.")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("payment-recovery-recover-original"));
  await waitFor(() => expect(recoveries).toEqual([pendingRecord.id]));
  await fireEvent.press(screen.getByTestId("payment-recovery-back-to-sale"));
  expect(onBack).toHaveBeenCalledTimes(1);

  for (const testID of [
    "payment-recovery-back-to-sale",
    "payment-recovery-filter-pending",
    "payment-recovery-filter-failed",
    "payment-recovery-filter-resolved",
    "payment-recovery-refresh",
    "payment-recovery-recover-original",
    "payment-recovery-open-manual",
  ]) {
    expect(StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight)
      .toBeGreaterThanOrEqual(PAYMENT_RECOVERY_MIN_TOUCH_TARGET);
  }
});

test("较窄横屏收紧双栏间距并保留可触达的恢复操作", async () => {
  Dimensions.set({
    window: { width: 834, height: 1_194, scale: 2, fontScale: 1 },
    screen: { width: 834, height: 1_194, scale: 2, fontScale: 1 },
  });
  const { service } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  expect(StyleSheet.flatten(screen.getByTestId("payment-recovery-list-pane").props.style))
    .toEqual(expect.objectContaining({ minWidth: 310, width: "42%" }));
  expect(screen.getByTestId("payment-recovery-recover-original")).toBeTruthy();
  expect(screen.getByTestId("payment-recovery-open-manual")).toBeTruthy();
});

test("人工核实默认三项未选且未确认，不需要证据，选好结果并确认后即可提交（已扣款还须等额金额）", async () => {
  const { service, submissions } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));

  for (const finding of ["paid", "unpaid", "uncertain"]) {
    expect(screen.getByTestId(`payment-recovery-finding-${finding}`).props.accessibilityState.selected).toBe(false);
  }
  expect(screen.getByTestId("payment-recovery-manual-confirmation").props.accessibilityState.checked).toBe(false);
  expect(screen.getByTestId("payment-recovery-manual-submit").props.accessibilityState.disabled).toBe(true);
  expect(screen.getByText("Manual verification is kept separate from provider approval.", { exact: false })).toBeTruthy();

  await fireEvent.press(screen.getByTestId("payment-recovery-finding-paid"));
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-amount"), "1.00");
  expect(screen.getByText("The verified amount must match AU$0.99.")).toBeTruthy();
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-amount"), "0.99");
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-note"), "  Checked terminal journal  ");
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));

  const submit = screen.getByTestId("payment-recovery-manual-submit");
  expect(submit.props.accessibilityState.disabled).toBe(false);
  expect(screen.getByText("Verify supervisor and confirm payment")).toBeTruthy();
  await fireEvent.press(submit);

  await waitFor(() => expect(submissions).toEqual([{
    recordId: pendingRecord.id,
    finding: "paid",
    verifiedAmountCents: 99,
    note: "Checked terminal journal",
  }]));
});

test("确认未扣款不提交伪造金额，并显示完整中文主管验证文案", async () => {
  mockLanguage = "zh-CN";
  const { service, submissions } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  expect(screen.getByText("支付恢复中心")).toBeTruthy();
  expect(screen.getByText("禁止再次扣款，请先查询原支付尝试。")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  expect(screen.queryByTestId("payment-recovery-manual-amount")).toBeNull();
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-note"), "核对终端流水未扣款");
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));
  expect(screen.getByText("验证主管并确认未收款")).toBeTruthy();
  expect(screen.getByText("提交后将打开主管身份验证，只有授权成功才会保存处理结果。")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-submit"));
  await waitFor(() => expect(submissions[0]?.verifiedAmountCents).toBeNull());
});

test("提交时隐藏人工面板让主管原生弹窗显示，授权失败后保留草稿重新打开", async () => {
  const { service } = createService();
  let rejectAuthorization: ((reason: Error) => void) | undefined;
  service.submitManualVerification = jest.fn(() => new Promise<void>((_resolve, reject) => {
    rejectAuthorization = reject;
  }));
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-paid"));
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-amount"), "0.99");
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-note"), "Retain this note");
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-submit"));

  await waitFor(() => expect(screen.queryByTestId("payment-recovery-manual-modal")).toBeNull());
  rejectAuthorization?.(new Error("SUPERVISOR_CANCELLED"));
  await waitFor(() => expect(screen.getByTestId("payment-recovery-manual-modal")).toBeTruthy());
  expect(screen.getByTestId("payment-recovery-manual-amount").props.value).toBe("0.99");
  expect(screen.getByTestId("payment-recovery-manual-note").props.value).toBe("Retain this note");
});

test("人工未扣款与明确失败可继续原单，中英文动作准确", async () => {
  for (const language of ["en", "zh"]) {
    mockLanguage = language;
    for (const status of ["manual-unpaid", "payment-failed"] as const) {
      const { service, recoveries } = createService({ ...pendingRecord, status });
      service.setFilter(status === "manual-unpaid" ? "resolved" : "failed");
      const screen = await render(<PaymentRecoveryScreen service={service} />);
      expect(screen.getByText(language === "en" ? "Continue payment for original order" : "继续原订单付款")).toBeTruthy();
      await fireEvent.press(screen.getByTestId("payment-recovery-recover-original"));
      expect(recoveries).toEqual([pendingRecord.id]);
      if (status === "manual-unpaid") expect(screen.queryByTestId("payment-recovery-open-manual")).toBeNull();
      await screen.unmount();
    }
  }
});


test("证据输入已取消：表单里没有证据框，备注选填，不写备注也能提交", async () => {
  const { service, submissions } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  expect(screen.queryByTestId("payment-recovery-manual-evidence")).toBeNull();
  expect(screen.getByText("Verification note (optional)")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));
  const submit = screen.getByTestId("payment-recovery-manual-submit");
  expect(submit.props.accessibilityState.disabled).toBe(false);
  await fireEvent.press(submit);
  await waitFor(() => expect(submissions).toEqual([{
    recordId: pendingRecord.id,
    finding: "unpaid",
    verifiedAmountCents: null,
    note: "",
  }]));
});

test("备注预设：已扣款 2 个、未扣款 3 个、仍无法确认没有；点击追加到备注且不重复", async () => {
  const { service } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  expect(screen.queryByTestId("payment-recovery-manual-presets")).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-recovery-finding-paid"));
  expect(screen.getByText("Terminal shows payment successful")).toBeTruthy();
  expect(screen.getByText("Customer showed payment record")).toBeTruthy();
  expect(screen.queryByTestId("payment-recovery-manual-preset-unpaidTerminalOffline")).toBeNull();
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-preset-paidTerminalSuccess"));
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-preset-paidCustomerRecord"));
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-preset-paidTerminalSuccess"));
  expect(screen.getByTestId("payment-recovery-manual-note").props.value)
    .toBe("Terminal shows payment successful; Customer showed payment record");

  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  for (const id of ["unpaidTerminalFailed", "unpaidTerminalOffline", "unpaidTerminalStuck"]) {
    expect(screen.getByTestId(`payment-recovery-manual-preset-${id}`)).toBeTruthy();
  }
  expect(screen.queryByTestId("payment-recovery-manual-preset-paidTerminalSuccess")).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-recovery-finding-uncertain"));
  expect(screen.queryByTestId("payment-recovery-manual-presets")).toBeNull();
});

test("中文预设：刷卡机显示支付成功 / 顾客提供支付记录；未扣款：显示失败 / 离线 / 卡住了，用中文分号连接", async () => {
  mockLanguage = "zh-CN";
  const { service } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-paid"));
  expect(screen.getByText("刷卡机显示支付成功")).toBeTruthy();
  expect(screen.getByText("顾客提供支付记录")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-preset-paidTerminalSuccess"));
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-preset-paidCustomerRecord"));
  expect(screen.getByTestId("payment-recovery-manual-note").props.value).toBe("刷卡机显示支付成功；顾客提供支付记录");
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  expect(screen.getByText("刷卡机显示失败")).toBeTruthy();
  expect(screen.getByText("刷卡机离线")).toBeTruthy();
  expect(screen.getByText("刷卡机卡住了")).toBeTruthy();
});

test("M34：终端批准不同金额时展示实扣金额，禁用已收款，未收款需确认冲正后才提交", async () => {
  const record: PaymentRecoveryRecord = { ...pendingRecord, amountCents: 1_000, terminalAmountMismatchCents: 1_500 };
  const { service, submissions } = createService(record);
  const screen = await render(<PaymentRecoveryScreen service={service} />);

  expect(screen.getByTestId("payment-recovery-mismatch-warning")).toBeTruthy();
  expect(screen.getByText("Terminal charged")).toBeTruthy();
  expect(screen.getAllByText("AU$15.00").length).toBeGreaterThan(0);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));

  expect(screen.getByTestId("payment-recovery-finding-paid").props.accessibilityState.disabled).toBe(true);
  expect(screen.getByText("Register reversal required")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-note"), "Terminal shows AU$15.00");
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));
  // 缺少“终端已扣款需冲正”确认时不能提交。
  expect(screen.getByTestId("payment-recovery-manual-submit").props.accessibilityState.disabled).toBe(true);
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-terminal-charge"));
  expect(screen.getByTestId("payment-recovery-manual-submit").props.accessibilityState.disabled).toBe(false);
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-submit"));
  await waitFor(() => expect(submissions).toEqual([{
    recordId: pendingRecord.id,
    finding: "unpaid",
    verifiedAmountCents: null,
    note: "Terminal shows AU$15.00",
    terminalChargeAcknowledged: true,
  }]));
});

test("M34：无金额不符的记录不显示实扣警告与冲正确认", async () => {
  const { service } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);

  expect(screen.queryByTestId("payment-recovery-mismatch-warning")).toBeNull();
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  expect(screen.queryByTestId("payment-recovery-manual-terminal-charge")).toBeNull();
  expect(screen.getByTestId("payment-recovery-finding-paid").props.accessibilityState.disabled).toBeFalsy();
});

test("Square 退款结算：只展示结算中与被拒的退款；可立即检查，被拒的须填写退款方式后才能主管结案", async () => {
  const { service, refundResolutions } = createService(pendingRecord, [
    {
      attemptId: "refund-settling",
      returnOrderGuid: "return-order-settling",
      amountCents: 1_250,
      status: "settling",
      squareStatus: "PENDING",
      lastCheckedAtIso: null,
      registeredAtIso: "2026-10-22T01:00:00.000Z",
    },
    {
      attemptId: "refund-rejected",
      returnOrderGuid: "return-order-rejected",
      amountCents: 500,
      status: "rejected",
      squareStatus: "REJECTED",
      lastCheckedAtIso: "2026-10-22T02:00:00.000Z",
      registeredAtIso: "2026-10-22T01:30:00.000Z",
    },
  ]);
  const screen = await render(<PaymentRecoveryScreen service={service} />);

  expect(screen.getByTestId("payment-recovery-refund-settlements")).toBeTruthy();
  expect(screen.getByText("Settling · accepted by Square")).toBeTruthy();
  expect(screen.getByText("Rejected by Square · refund the customer another way")).toBeTruthy();
  expect(screen.getByText("Not checked yet")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("payment-recovery-refund-check"));
  await waitFor(() => expect(service.checkRefundSettlements).toHaveBeenCalledTimes(1));

  // 没填实际退款方式时不能结案。
  await fireEvent.press(screen.getByTestId("payment-recovery-refund-resolve-refund-rejected"));
  expect(service.acknowledgeRefundSettlementFailure).not.toHaveBeenCalled();
  await fireEvent.changeText(screen.getByTestId("payment-recovery-refund-note-refund-rejected"), "  Refunded $5.00 cash  ");
  await fireEvent.press(screen.getByTestId("payment-recovery-refund-resolve-refund-rejected"));
  await waitFor(() => expect(refundResolutions).toEqual([{ attemptId: "refund-rejected", note: "Refunded $5.00 cash" }]));
  // 结算中的退款没有结案入口。
  expect(screen.queryByTestId("payment-recovery-refund-resolve-refund-settling")).toBeNull();
});

test("没有需关注的 Square 退款时不显示结算区块", async () => {
  const { service } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  expect(screen.queryByTestId("payment-recovery-refund-settlements")).toBeNull();
});
