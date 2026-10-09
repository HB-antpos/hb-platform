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

async function openDetails(screen: Awaited<ReturnType<typeof render>>, recordId = "attempt-1") {
  await fireEvent.press(screen.getByTestId(`payment-recovery-record-${recordId}`));
  await waitFor(() => expect(screen.getByTestId("payment-recovery-details-pane")).toBeTruthy());
}

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
  await openDetails(screen);
  expect(screen.getByTestId("payment-recovery-recover-original")).toBeTruthy();
  expect(screen.queryByTestId("payment-recovery-open-manual")).toBeNull();
});

function createService(record: PaymentRecoveryRecord = pendingRecord) {
  let state: PaymentRecoveryCenterState = {
    filter: "pending",
    keyword: "",
    records: [record],
    selectedRecordId: record.id,
    loading: false,
    refreshing: false,
    action: "idle",
    errorCode: null,
  };
  const listeners = new Set<() => void>();
  const submissions: ManualPaymentVerificationInput[] = [];
  const recoveries: string[] = [];
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
  };
  return { service, submissions, recoveries };
}

beforeEach(() => {
  mockLanguage = "en";
  Dimensions.set({
    window: { width: 390, height: 844, scale: 3, fontScale: 1 },
    screen: { width: 390, height: 844, scale: 3, fontScale: 1 },
  });
});

test("手持恢复中心单栏展示：先列表，点开后看原支付证据、历史和不阻断下一单入口", async () => {
  const { service, recoveries } = createService();
  const onBack = jest.fn();
  const screen = await render(<PaymentRecoveryScreen onBack={onBack} service={service} />);

  expect(screen.getByText("Payment recovery centre")).toBeTruthy();
  expect(screen.getByTestId("payment-recovery-list-pane")).toBeTruthy();
  expect(screen.queryByTestId("payment-recovery-details-pane")).toBeNull();

  await openDetails(screen);
  expect(screen.queryByTestId("payment-recovery-list-pane")).toBeNull();
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
    "payment-recovery-back-to-list",
    "payment-recovery-recover-original",
    "payment-recovery-open-manual",
  ]) {
    expect(StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight)
      .toBeGreaterThanOrEqual(PAYMENT_RECOVERY_MIN_TOUCH_TARGET);
  }
  expect(PAYMENT_RECOVERY_MIN_TOUCH_TARGET).toBe(48);

  await fireEvent.press(screen.getByTestId("payment-recovery-back-to-list"));
  expect(screen.getByTestId("payment-recovery-list-pane")).toBeTruthy();
  for (const testID of [
    "payment-recovery-filter-pending",
    "payment-recovery-filter-failed",
    "payment-recovery-filter-resolved",
    "payment-recovery-refresh",
  ]) {
    expect(StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight)
      .toBeGreaterThanOrEqual(PAYMENT_RECOVERY_MIN_TOUCH_TARGET);
  }
});

test("人工核实默认三项未选且未确认，完整有效证据和等额金额后才允许提交", async () => {
  const { service, submissions } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  await openDetails(screen);
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
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-evidence"), "  RECEIPT-77  ");
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
    evidenceReference: "RECEIPT-77",
    note: "Checked terminal journal",
  }]));
});

test("确认未扣款不提交伪造金额，并显示完整中文主管验证文案", async () => {
  mockLanguage = "zh-CN";
  const { service, submissions } = createService();
  const screen = await render(<PaymentRecoveryScreen service={service} />);
  expect(screen.getByText("支付恢复中心")).toBeTruthy();
  await openDetails(screen);
  expect(screen.getByText("禁止再次扣款，请先查询原支付尝试。")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-unpaid"));
  expect(screen.queryByTestId("payment-recovery-manual-amount")).toBeNull();
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-evidence"), "REF-UNPAID");
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
  await openDetails(screen);
  await fireEvent.press(screen.getByTestId("payment-recovery-open-manual"));
  await fireEvent.press(screen.getByTestId("payment-recovery-finding-paid"));
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-amount"), "0.99");
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-evidence"), "RECEIPT-DRAFT");
  await fireEvent.changeText(screen.getByTestId("payment-recovery-manual-note"), "Retain this note");
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-confirmation"));
  await fireEvent.press(screen.getByTestId("payment-recovery-manual-submit"));

  await waitFor(() => expect(screen.queryByTestId("payment-recovery-manual-modal")).toBeNull());
  rejectAuthorization?.(new Error("SUPERVISOR_CANCELLED"));
  await waitFor(() => expect(screen.getByTestId("payment-recovery-manual-modal")).toBeTruthy());
  expect(screen.getByTestId("payment-recovery-manual-amount").props.value).toBe("0.99");
  expect(screen.getByTestId("payment-recovery-manual-evidence").props.value).toBe("RECEIPT-DRAFT");
  expect(screen.getByTestId("payment-recovery-manual-note").props.value).toBe("Retain this note");
});

test("人工未扣款与明确失败可继续原单，中英文动作准确", async () => {
  for (const language of ["en", "zh"]) {
    mockLanguage = language;
    for (const status of ["manual-unpaid", "payment-failed"] as const) {
      const { service, recoveries } = createService({ ...pendingRecord, status });
      service.setFilter(status === "manual-unpaid" ? "resolved" : "failed");
      const screen = await render(<PaymentRecoveryScreen service={service} />);
      await openDetails(screen);
      expect(screen.getByText(language === "en" ? "Continue payment for original order" : "继续原订单付款")).toBeTruthy();
      await fireEvent.press(screen.getByTestId("payment-recovery-recover-original"));
      expect(recoveries).toEqual([pendingRecord.id]);
      if (status === "manual-unpaid") expect(screen.queryByTestId("payment-recovery-open-manual")).toBeNull();
      await screen.unmount();
    }
  }
});
