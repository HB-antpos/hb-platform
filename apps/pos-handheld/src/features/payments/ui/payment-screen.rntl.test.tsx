import { expect, jest, test } from "@jest/globals";
import {
  act,
  fireEvent,
  render,
  waitFor,
  within,
} from "@testing-library/react-native";
import { Dimensions, StyleSheet } from "react-native";

import {
  PaymentPresenter,
  type PaymentConfirmOptions,
  type PaymentPresenterState,
  type PaymentRecoverOptions,
  type PaymentScreenPresenter,
  type PaymentUiMethod,
} from "./payment-presenter";
import {
  PAYMENT_MIN_TOUCH_TARGET,
  PaymentScreen,
  formatAud,
  type PaymentReceiptPrintOutcome,
} from "./payment-screen";

import type {
  Money,
  PaymentProvider,
} from "@/core/contracts";
import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import { InstallmentCheckoutPresenter } from "@/features/installments/installment-checkout-presenter";
import type { InstallmentDetails } from "@/features/installments/installment-models";
import {
  InstallmentWorkflowError,
  type InstallmentWorkflowPort,
} from "@/features/installments/installment-presenter";
import type {
  LinklyOperatorPublicResult,
  LinklyOperatorRuntimePort,
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
import {
  PAYMENT_PERMISSION,
  type PaymentCheckoutPublicSnapshot,
  type PaymentCheckoutRuntimePort,
} from "@/features/payments/runtime/payment-checkout-runtime";
import type {
  PaymentProviderAvailability,
} from "@/features/payments/runtime/payment-provider-registry";
import { installmentCreatePaymentEntry } from "@/features/payments/ui/unified-payment-entry";

let mockPrintStateWrite: ((value: unknown) => void) | null = null;

jest.mock("react", () => {
  const actual = jest.requireActual<typeof import("react")>("react");
  return {
    ...actual,
    useState: ((initialState: unknown) => {
      const [value, setValue] = actual.useState<unknown>(initialState);
      if (initialState !== "idle" || !mockPrintStateWrite) {
        return [value, setValue];
      }
      return [
        value,
        (nextValue: unknown) => {
          mockPrintStateWrite?.(nextValue);
          setValue(nextValue);
        },
      ];
    }) as typeof actual.useState,
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

jest.mock("@/ui/feedback", () => ({
  usePosSound: () => ({
    buttonSoundEnabled: false,
    play: jest.fn(),
    setButtonSoundEnabled: jest.fn(),
    setSpecialNodeSoundEnabled: jest.fn(),
    specialNodeSoundEnabled: false,
  }),
}));

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: () => null,
}));

async function openPaymentEntry(
  screen: Awaited<ReturnType<typeof render>>,
  method: PaymentUiMethod = "cash",
): Promise<void> {
  await fireEvent.press(screen.getByTestId(`payment-method-${method}`));
}

test("点击付款方式后才打开金额输入弹窗", async () => {
  const { presenter, spies } = createUiPresenter({
    selectedMethod: "cash",
    total: aud(1_000),
    remaining: aud(1_000),
  });
  const screen = await render(
    <PaymentScreen locale="zh" presenter={presenter} showStatusStrip={false} />,
  );

  expect(screen.queryByTestId("payment-entry-modal")).toBeNull();
  expect(screen.queryByTestId("payment-entry-pane")).toBeNull();
  expect(screen.queryByTestId("payment-amount")).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-method-cash"));

  expect(spies.selectMethod).toHaveBeenCalledWith("cash");
  expect(screen.getByTestId("payment-entry-modal")).toBeTruthy();
  expect(screen.getByTestId("payment-entry-pane")).toBeTruthy();
  expect(screen.getByTestId("payment-amount").props.value).toBe("10.00");

  await act(async () => {
    screen.getByTestId("payment-entry-native-modal").props.onRequestClose();
  });
  expect(screen.queryByTestId("payment-entry-modal")).toBeNull();

  await openPaymentEntry(screen, "cash");
  await fireEvent.press(screen.getByTestId("payment-entry-cancel"));
  expect(screen.queryByTestId("payment-entry-modal")).toBeNull();
  expect(spies.cancel).not.toHaveBeenCalled();
});

test("提交失败时保留金额弹窗并宣告字段错误", async () => {
  const harness = createUiPresenter({
    selectedMethod: "cash",
    total: aud(1_000),
    remaining: aud(1_000),
  });
  harness.spies.submitSelected.mockImplementation(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      fieldIssue: "amount-required",
    });
    return false;
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  await openPaymentEntry(screen, "cash");
  await fireEvent.press(screen.getByTestId("payment-submit"));

  await waitFor(() =>
    expect(screen.getByTestId("payment-entry-modal")).toBeTruthy(),
  );
  expect(screen.getByTestId("payment-field-error").props.accessibilityRole).toBe(
    "alert",
  );
  expect(screen.getByTestId("payment-field-error")).toHaveTextContent(
    "请输入付款金额。",
  );
});

test("付款内容滚动区采用系统键盘避让且金额输入仍禁用软键盘", async () => {
  const { presenter } = createUiPresenter({
    phase: "ready",
    providers: [
      providerAvailability("square"),
      providerAvailability("linkly-cloud"),
      providerAvailability("voucher"),
    ],
    selectedMethod: "cash",
    orderGuid: "order-ui-1",
    total: aud(1_000),
    remaining: aud(1_000),
  });
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByTestId("payment-content-scroll").props).toMatchObject({
    automaticallyAdjustKeyboardInsets: true,
    keyboardDismissMode: "interactive",
    keyboardShouldPersistTaps: "handled",
  });
  await openPaymentEntry(screen, "cash");
  const entryScroll = screen.getByTestId("payment-entry-scroll");
  expect(entryScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
  expect(entryScroll.props.keyboardDismissMode).toBe("interactive");
  expect(entryScroll.props.keyboardShouldPersistTaps).toBe("handled");
  expect(screen.getByTestId("payment-amount").props.showSoftInputOnFocus).toBe(
    false,
  );
});

test("Linkly Cloud 多终端允许预选忙碌终端，未配对与未知结果期间禁用", async () => {
  const harness = createUiPresenter({
    selectedMethod: "linkly-cloud",
    linklyTerminals: {
      kind: "ready",
      environment: "Sandbox",
      snapshot: {
        environment: "Sandbox",
        mode: "Active",
        selectedTerminalId: "terminal-1",
        selectionRevision: 5,
        terminals: [
          linklyTerminal("terminal-1", 1, "Front"),
          linklyTerminal("terminal-2", 2, "Returns"),
          { ...linklyTerminal("terminal-busy", 3, "Busy"), isBusy: true },
          {
            ...linklyTerminal("terminal-unpaired", 4, "Unpaired"),
            pairingState: "Unpaired",
            isReady: false,
          },
        ],
      },
      errorCode: null,
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByTestId("payment-linkly-terminal-selector")).toBeTruthy();
  expect(screen.getByText("Front · Lane 1")).toBeTruthy();
  expect(
    screen.getByTestId("payment-linkly-terminal-terminal-2").props
      .accessibilityState.disabled,
  ).toBe(false);
  expect(
    screen.getByTestId("payment-linkly-terminal-terminal-busy").props
      .accessibilityState.disabled,
  ).toBe(false);
  expect(
    screen.getByTestId("payment-linkly-terminal-terminal-unpaired").props
      .accessibilityState.disabled,
  ).toBe(true);

  await fireEvent.press(
    screen.getByTestId("payment-linkly-terminal-terminal-2"),
  );
  expect(harness.spies.selectLinklyTerminal).toHaveBeenCalledWith("terminal-2");
  await fireEvent.press(
    screen.getByTestId("payment-linkly-terminal-terminal-busy"),
  );
  expect(harness.spies.selectLinklyTerminal).toHaveBeenCalledWith(
    "terminal-busy",
  );

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      phase: "unknown",
      runtimeStatus: "unknown",
    });
  });
  expect(
    screen.getByTestId("payment-linkly-terminal-terminal-2").props
      .accessibilityState.disabled,
  ).toBe(true);
});

test("Linkly Cloud 零终端阻止支付，单终端只显示当前终端而隐藏切换器", async () => {
  const zero = createUiPresenter({
    selectedMethod: "linkly-cloud",
    linklyTerminals: {
      kind: "ready",
      environment: "Sandbox",
      snapshot: {
        environment: "Sandbox",
        mode: "Active",
        selectedTerminalId: null,
        selectionRevision: 1,
        terminals: [],
      },
      errorCode: null,
    },
  });
  const zeroScreen = await render(
    <PaymentScreen locale="zh" presenter={zero.presenter} showStatusStrip={false} />,
  );
  expect(zeroScreen.getByTestId("payment-linkly-terminal-empty")).toHaveTextContent(
    "没有可用的 Linkly 云端刷卡机。",
  );
  expect(
    zeroScreen.getByTestId("payment-method-linkly-cloud").props
      .accessibilityState.disabled,
  ).toBe(true);
  await zeroScreen.unmount();

  const single = createUiPresenter({
    linklyTerminals: {
      kind: "ready",
      environment: "Sandbox",
      snapshot: {
        environment: "Sandbox",
        mode: "Active",
        selectedTerminalId: "terminal-1",
        selectionRevision: 2,
        terminals: [linklyTerminal("terminal-1", 1, "Front")],
      },
      errorCode: null,
    },
  });
  const singleScreen = await render(
    <PaymentScreen locale="en" presenter={single.presenter} showStatusStrip={false} />,
  );
  expect(singleScreen.queryByTestId("payment-linkly-terminal-selector")).toBeNull();
  expect(singleScreen.getByText("Front · Lane 1")).toBeTruthy();
});

test("手持付款页保持 48px 触控，礼券输入只安全遮罩且成功后不回显", async () => {
  expect(PAYMENT_MIN_TOUCH_TARGET).toBe(48);
  const runtime = new ScreenPaymentRuntime();
  runtime.startImpl = async () =>
    snapshot({
      status: "completed",
      remaining: aud(0),
      provider: "voucher",
      attemptId: "attempt-voucher-ui",
      tenders: [
        {
          tenderGuid: "tender-voucher-ui",
          method: "voucher",
          amount: aud(1_000),
          reversible: true,
        },
      ],
    });
  const onComplete = jest.fn();
  const presenter = screenPresenter(runtime);
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onComplete={onComplete}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-status-ready")).toBeTruthy(),
  );
  const voucherMethod = screen.getByTestId("payment-method-voucher");
  expect(
    StyleSheet.flatten(voucherMethod.props.style).minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  await fireEvent.press(voucherMethod);
  await fireEvent.press(screen.getByTestId("payment-key-1"));
  expect(screen.getByTestId("payment-amount").props.value).toBe("1");

  const voucherInput = screen.getByTestId("payment-voucher-code");
  expect(voucherInput.props.secureTextEntry).toBe(true);
  await fireEvent.changeText(voucherInput, "VOUCHER-UI-SECRET");
  expect(screen.queryByText("VOUCHER-UI-SECRET")).toBeNull();
  expect(screen.getByTestId("payment-voucher-captured")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("payment-submit"));
  await waitFor(() =>
    expect(screen.getByTestId("payment-status-success")).toBeTruthy(),
  );
  expect(runtime.startCalls).toHaveLength(1);
  expect(
    JSON.stringify(presenter.getState()).includes("VOUCHER-UI-SECRET"),
  ).toBe(false);
  expect(screen.queryByTestId("payment-voucher-code")).toBeNull();
  await fireEvent.press(screen.getByTestId("payment-complete"));
  expect(onComplete).toHaveBeenCalledWith("order-ui-1");
});

test("支付成功切换为独立结算页，显示现金找零与小票动作并隐藏付款控件", async () => {
  const { presenter } = createUiPresenter({
    phase: "success",
    providers: [
      {
        provider: "square",
        available: false,
        blocker: "SQUARE_CONFIGURATION_MISSING",
      },
      providerAvailability("linkly-cloud"),
      providerAvailability("voucher"),
    ],
    selectedMethod: "cash",
    orderGuid: "order-ui-1",
    total: aud(1_000),
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "cash-success",
        method: "cash",
        amount: aud(1_000),
        reversible: true,
      },
    ],
    allowedActions: actions(),
    checkout: {
      flow: "regular",
      lines: [
        {
          lineKey: "open-item-success",
          displayName: "OPEN ITEM",
          quantity: "1",
          actualAmountCents: 1_000,
        },
      ],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_500,
        appliedCents: 1_000,
        changeCents: 500,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
    },
  });
  const onComplete = jest.fn();
  const onPrintReceipt = jest.fn<
    (orderGuid: string) => Promise<PaymentReceiptPrintOutcome>
  >(async () => "completed");
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onComplete={onComplete}
      onPrintReceipt={onPrintReceipt}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByTestId("payment-success-layout")).toBeTruthy();
  expect(screen.getByText("支付完成")).toBeTruthy();
  expect(screen.getAllByText("order-ui-1")).toHaveLength(2);
  expect(screen.getByText(formatAud(1_500, "zh"))).toBeTruthy();
  expect(screen.getByText(formatAud(500, "zh"))).toBeTruthy();
  expect(screen.getByText("订单已安全保存，并进入同步队列。")).toBeTruthy();
  expect(screen.getByTestId("payment-success-receipt-preview")).toBeTruthy();
  expect(screen.getByText("OPEN ITEM")).toBeTruthy();
  expect(screen.getByText("× 1")).toBeTruthy();
  expect(screen.getByText("打印小票")).toBeTruthy();
  expect(screen.getByText("开始下一单")).toBeTruthy();

  expect(screen.queryByTestId("payment-entry-pane")).toBeNull();
  expect(screen.queryByTestId("payment-summary")).toBeNull();
  expect(screen.queryByTestId("payment-method-cash")).toBeNull();
  expect(screen.queryByTestId("payment-method-square")).toBeNull();
  expect(screen.queryByTestId("payment-method-linkly-cloud")).toBeNull();
  expect(screen.queryByTestId("payment-method-voucher")).toBeNull();
  expect(screen.queryByTestId("payment-provider-blockers")).toBeNull();
  expect(screen.queryByTestId("payment-remove-cash-success")).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-success-print"));
  expect(onPrintReceipt).toHaveBeenCalledWith("order-ui-1");
  await fireEvent.press(screen.getByTestId("payment-complete"));
  expect(onComplete).toHaveBeenCalledWith("order-ui-1");
  await screen.unmount();
});

test("320×568 现金成功结算卡纵向收缩，长找零金额保持单行并可缩放", async () => {
  setPaymentWindowSize(320, 568);
  const { presenter } = createUiPresenter({
    phase: "success",
    orderGuid: "order-compact-cash-success",
    selectedMethod: "cash",
    total: aud(123_456_789),
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "cash-compact-success",
        method: "cash",
        amount: aud(123_456_789),
        reversible: true,
      },
    ],
    checkout: {
      flow: "regular",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 987_654_321,
        appliedCents: 123_456_789,
        changeCents: 864_197_532,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
    },
  });

  try {
    const screen = await render(
      <PaymentScreen locale="en" presenter={presenter} showStatusStrip={false} />,
    );
    const settlement = screen.getByTestId("payment-success-settlement");
    const settlementStyle = StyleSheet.flatten(settlement.props.style);
    const [, change] = settlement.props.children;
    const changeStyle = StyleSheet.flatten(change.props.style);
    const changeAmount = screen.getByTestId("payment-success-change");

    expect(settlementStyle).toMatchObject({
      alignItems: "stretch",
      flexDirection: "column",
    });
    expect(changeStyle).toMatchObject({
      borderLeftWidth: 0,
      minWidth: 0,
      width: "100%",
    });
    expect(changeAmount.props).toMatchObject({
      adjustsFontSizeToFit: true,
      minimumFontScale: 0.65,
      numberOfLines: 1,
    });
    expect(changeAmount.props.children).toBe(formatAud(864_197_532, "en"));
    await screen.unmount();

    setPaymentWindowSize(390, 844);
    const roomyScreen = await render(
      <PaymentScreen locale="en" presenter={presenter} showStatusStrip={false} />,
    );
    const roomySettlement = roomyScreen.getByTestId("payment-success-settlement");
    const roomySettlementStyle = StyleSheet.flatten(roomySettlement.props.style);
    const [, roomyChange] = roomySettlement.props.children;
    const roomyChangeStyle = StyleSheet.flatten(roomyChange.props.style);

    expect(roomySettlementStyle).toMatchObject({
      alignItems: "center",
      flexDirection: "row",
    });
    expect(roomyChangeStyle).toMatchObject({
      borderLeftWidth: 1,
      minWidth: 180,
    });
    await roomyScreen.unmount();
  } finally {
    setPaymentWindowSize(390, 844);
  }
});

test("手动打印在结果返回前防重复，并依次显示完成、未知和失败结果", async () => {
  const { presenter } = createUiPresenter({
    phase: "success",
    orderGuid: "order-print-state",
    total: aud(1_000),
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "cash-print-state",
        method: "cash",
        amount: aud(1_000),
        reversible: true,
      },
    ],
    allowedActions: actions(),
  });
  const pending = createDeferred<"completed">();
  const onPrintReceipt = jest.fn<
    (orderGuid: string) => Promise<PaymentReceiptPrintOutcome>
  >(() => pending.promise);
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onPrintReceipt={onPrintReceipt}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  const print = screen.getByTestId("payment-success-print");
  await fireEvent.press(print);
  await fireEvent.press(print);
  expect(onPrintReceipt).toHaveBeenCalledTimes(1);
  expect(onPrintReceipt).toHaveBeenCalledWith("order-print-state");
  expect(print.props.accessibilityState).toEqual({ disabled: true });
  expect(screen.getByText("正在打印小票…")).toBeTruthy();

  pending.resolve("completed");
  await waitFor(() =>
    expect(screen.getByText("小票打印完成。")).toBeTruthy(),
  );
  expect(
    screen.getByTestId("payment-success-print").props.accessibilityState,
  ).toEqual({ disabled: false });

  onPrintReceipt.mockResolvedValueOnce("unknown");
  await fireEvent.press(screen.getByTestId("payment-success-print"));
  await waitFor(() =>
    expect(
      screen.getByText("打印结果未知，请先检查打印机再决定是否重试。"),
    ).toBeTruthy(),
  );

  onPrintReceipt.mockResolvedValueOnce("failed");
  await fireEvent.press(screen.getByTestId("payment-success-print"));
  await waitFor(() =>
    expect(
      screen.getByText("小票打印失败，请检查打印机后重试。"),
    ).toBeTruthy(),
  );
  expect(onPrintReceipt).toHaveBeenCalledTimes(3);
  await screen.unmount();
});

test("pending 打印在成功页卸载后不得再写 React 状态", async () => {
  const { presenter } = createUiPresenter({
    phase: "success",
    orderGuid: "order-print-unmount",
    total: aud(1_000),
    remaining: aud(0),
    allowedActions: actions(),
  });
  const pending = createDeferred<PaymentReceiptPrintOutcome>();
  const onPrintReceipt = jest.fn(() => pending.promise);
  const printStateWrites = jest.fn<(value: unknown) => void>();
  mockPrintStateWrite = printStateWrites;

  try {
    const screen = await render(
      <PaymentScreen
        locale="zh"
        onPrintReceipt={onPrintReceipt}
        presenter={presenter}
        showStatusStrip={false}
      />,
    );
    await fireEvent.press(screen.getByTestId("payment-success-print"));
    expect(printStateWrites).toHaveBeenCalledWith("printing");
    printStateWrites.mockClear();

    await screen.unmount();
    await act(async () => {
      pending.resolve("completed");
      await pending.promise;
    });
    expect(printStateWrites).not.toHaveBeenCalled();
  } finally {
    mockPrintStateWrite = null;
  }
});

test("pending 打印切换订单时清空状态并忽略旧订单迟到结果", async () => {
  const first = createUiPresenter({
    phase: "success",
    orderGuid: "order-print-first",
    total: aud(1_000),
    remaining: aud(0),
    allowedActions: actions(),
  });
  const second = createUiPresenter({
    phase: "success",
    orderGuid: "order-print-second",
    total: aud(2_000),
    remaining: aud(0),
    allowedActions: actions(),
  });
  const pending = createDeferred<PaymentReceiptPrintOutcome>();
  const onPrintReceipt = jest.fn(() => pending.promise);
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onPrintReceipt={onPrintReceipt}
      presenter={first.presenter}
      showStatusStrip={false}
    />,
  );

  await fireEvent.press(screen.getByTestId("payment-success-print"));
  expect(screen.getByText("正在打印小票…")).toBeTruthy();
  await screen.rerender(
    <PaymentScreen
      locale="zh"
      onPrintReceipt={onPrintReceipt}
      presenter={second.presenter}
      showStatusStrip={false}
    />,
  );
  await waitFor(() => {
    expect(screen.getAllByText("order-print-second")).toHaveLength(2);
    expect(screen.queryByTestId("payment-success-print-status")).toBeNull();
  });

  await act(async () => {
    pending.resolve("completed");
    await pending.promise;
  });
  expect(screen.queryByText("小票打印完成。")).toBeNull();
  expect(
    screen.getByTestId("payment-success-print").props.accessibilityState,
  ).toEqual({ disabled: false });
  await screen.unmount();
});

test("Linkly 支付成功仍保留回执确认与终端确认入口", async () => {
  const { presenter, spies } = createUiPresenter({
    phase: "success",
    selectedMethod: "linkly-cloud",
    orderGuid: "order-linkly-success",
    total: aud(1_000),
    remaining: aud(0),
    tenders: [
      {
        tenderGuid: "card-linkly-success",
        method: "card",
        amount: aud(1_000),
        reversible: true,
      },
    ],
    attemptId: "attempt-linkly-success",
    provider: "linkly-cloud",
    runtimeStatus: "completed",
    allowedActions: actions(),
    linkly: {
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByTestId("payment-linkly-controls")).toBeTruthy();
  expect(
    screen.getByTestId("payment-success-print").props.accessibilityState,
  ).toEqual({ disabled: true });
  await fireEvent.press(
    screen.getByTestId("payment-linkly-receipt-printed"),
  );
  expect(spies.markLinklyReceiptPrinted).toHaveBeenCalledTimes(1);
  await fireEvent.press(screen.getByTestId("payment-linkly-acknowledge"));
  expect(spies.acknowledgeLinkly).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("Unknown 隐藏新付款和 Linkly 按键，只允许恢复同一 attempt", async () => {
  const runtime = new ScreenPaymentRuntime();
  runtime.recovery = snapshot({
    status: "unknown",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-unknown",
    errorCode: "PAYMENT_STATUS_UNKNOWN",
    allowedActions: actions({ recover: true }),
  });
  runtime.recoverImpl = async () =>
    snapshot({
      status: "completed",
      remaining: aud(0),
      provider: "linkly-cloud",
      attemptId: "attempt-linkly-unknown",
      tenders: [
        {
          tenderGuid: "tender-linkly-ui",
          method: "card",
          amount: aud(1_000),
          reversible: true,
        },
      ],
    });
  const linkly = new ScreenLinklyOperator();
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={screenPresenter(runtime, linkly)}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-status-unknown")).toBeTruthy(),
  );
  expect(screen.queryByTestId("payment-entry-form")).toBeNull();
  expect(screen.queryByTestId("payment-submit")).toBeNull();
  expect(screen.queryByTestId("payment-linkly-yes")).toBeNull();
  expect(screen.queryByTestId("payment-cancel")).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-recover"));
  await waitFor(() =>
    expect(screen.getByTestId("payment-status-success")).toBeTruthy(),
  );
  expect(runtime.recoverInputs).toEqual([
    {
      orderGuid: "order-ui-1",
      attemptId: "attempt-linkly-unknown",
    },
  ]);
  expect(linkly.sendCalls).toHaveLength(0);
});

test("Blocked 礼券撤券显示主管作废入口，作废后回到可继续收款的支付页", async () => {
  class VoidingScreenRuntime extends ScreenPaymentRuntime {
    public readonly voidInputs: unknown[] = [];

    public async voidBlockedTenderReversal(input: {
      orderGuid: string;
      tenderGuid: string;
    }): Promise<PaymentCheckoutPublicSnapshot> {
      this.voidInputs.push(input);
      return snapshot();
    }
  }
  const runtime = new VoidingScreenRuntime();
  const blocked = snapshot({
    status: "recovery-required",
    remaining: aud(500),
    errorCode: "TENDER_REVERSAL_BLOCKED",
    tenders: [
      {
        tenderGuid: "voucher-ui-1",
        method: "voucher",
        amount: aud(500),
        reversible: false,
      },
    ],
    tenderReversalRecovery: {
      tenderGuid: "voucher-ui-1",
      status: "blocked",
      voidAvailable: true,
    },
    allowedActions: actions(),
  });
  runtime.recovery = blocked;
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={screenPresenter(runtime, new ScreenLinklyOperator())}
      showStatusStrip={false}
    />,
  );

  const button = await waitFor(() =>
    screen.getByTestId("payment-void-blocked-tender-reversal"),
  );
  expect(screen.getByText("主管授权作废礼券付款")).toBeTruthy();
  expect(screen.queryByTestId("payment-recover")).toBeNull();
  await fireEvent.press(button);
  await waitFor(() =>
    expect(
      screen.queryByTestId("payment-void-blocked-tender-reversal"),
    ).toBeNull(),
  );
  expect(runtime.voidInputs).toEqual([
    { orderGuid: "order-ui-1", tenderGuid: "voucher-ui-1" },
  ]);
  expect(screen.getByTestId("payment-cancel")).toBeTruthy();
});

test("部分付款显示脱敏 tender、余额与可控 reversal，不把银行卡归因到 provider reference", async () => {
  const runtime = new ScreenPaymentRuntime();
  runtime.recovery = snapshot({
    status: "partial",
    remaining: aud(400),
    provider: "square",
    attemptId: "attempt-square-approved",
    tenders: [
      {
        tenderGuid: "public-tender-guid",
        method: "card",
        amount: aud(600),
        reversible: true,
      },
    ],
    allowedActions: actions({
      start: true,
      changeProvider: true,
      addCash: true,
      removeTender: true,
    }),
  });
  runtime.removeImpl = async () =>
    snapshot({
      status: "draft-prepared",
      remaining: aud(1_000),
      allowedActions: actions({
        start: true,
        changeProvider: true,
        cancel: true,
        addCash: true,
      }),
    });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={screenPresenter(runtime)}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-status-partial")).toBeTruthy(),
  );
  expect(screen.getByText("银行卡")).toBeTruthy();
  expect(screen.getByText(formatAud(400, "zh"))).toBeTruthy();
  expect(screen.getByTestId("payment-method-square").props.accessibilityState)
    .toEqual({ disabled: true, selected: false });
  expect(screen.getByTestId("payment-method-linkly-cloud").props.accessibilityState)
    .toEqual({ disabled: true, selected: false });

  await fireEvent.press(
    screen.getByTestId("payment-remove-public-tender-guid"),
  );
  await waitFor(() =>
    expect(runtime.removeInputs).toHaveLength(1),
  );
  expect(runtime.removeInputs[0]).toEqual({
    orderGuid: "order-ui-1",
    actionId: "ui-action-1",
    tenderGuid: "public-tender-guid",
  });
});

test("Linkly Pending 仅渲染枚举安全键，点击只传 attemptId 和 key", async () => {
  const runtime = new ScreenPaymentRuntime();
  runtime.recovery = snapshot({
    status: "pending",
    provider: "linkly-cloud",
    attemptId: "attempt-linkly-pending",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const linkly = new ScreenLinklyOperator();
  linkly.sendImpl = async (attemptId) => ({
    attemptId,
    status: "in-progress",
    errorCode: null,
    allowedKeys: ["yes", "no"],
  });
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={screenPresenter(runtime, linkly)}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-linkly-controls")).toBeTruthy(),
  );
  expect(screen.getByTestId("payment-linkly-display-text")).toHaveTextContent(
    "PRESENT CARD",
  );
  expect(screen.queryByTestId("payment-linkly-cancel")).toBeNull();
  expect(screen.getByTestId("payment-linkly-yes")).toBeTruthy();
  expect(screen.queryByTestId("payment-linkly-authorise")).toBeNull();
  await fireEvent.press(screen.getByTestId("payment-linkly-yes"));
  await waitFor(() => expect(linkly.sendCalls).toHaveLength(1));
  expect(linkly.sendCalls[0]).toEqual({
    attemptId: "attempt-linkly-pending",
    key: "yes",
  });
  expect(screen.queryByTestId("payment-linkly-authorise")).toBeNull();
});

test("Square Pending 按 WPF 节奏自动恢复且卸载后停止轮询", async () => {
  jest.useFakeTimers();
  const createdAtIso = "2026-08-09T00:00:00.000Z";
  jest.setSystemTime(new Date(createdAtIso));
  const { presenter, spies } = createUiPresenter({
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: "order-square-auto-recovery",
    attemptId: "attempt-square-auto-recovery",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  try {
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1_999);
    });
    expect(spies.recover).not.toHaveBeenCalled();

    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(spies.recover).toHaveBeenCalledTimes(1);
    expect(spies.recover).toHaveBeenCalledWith({
      background: true,
      deadlineAtMs: Date.parse(createdAtIso) + 90_000,
      signal: expect.any(AbortSignal),
    });
    await act(async () => {
      await jest.advanceTimersByTimeAsync(4_000);
    });
    expect(spies.recover).toHaveBeenCalledTimes(3);
    const quickSignals = spies.recover.mock.calls.map(
      ([options]) => options?.signal,
    );
    expect(new Set(quickSignals).size).toBe(3);

    await screen.unmount();
    await jest.advanceTimersByTimeAsync(10_000);
    expect(spies.recover).toHaveBeenCalledTimes(3);
  } finally {
    jest.useRealTimers();
  }
});

test("Square 后台恢复期间明确提示并禁用恢复与取消", async () => {
  const square = createUiPresenter({
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: "order-square-recovery-ui",
    attemptId: "attempt-square-recovery-ui",
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={square.presenter}
      showStatusStrip={false}
    />,
  );

  await act(async () => {
    square.publish({
      ...square.presenter.getState(),
      recoveryInFlight: true,
    });
  });

  expect(screen.getByTestId("payment-recovery-in-flight")).toBeTruthy();
  expect(screen.getByText("正在自动恢复当前支付…")).toBeTruthy();
  expect(screen.getByTestId("payment-recover").props.accessibilityState).toEqual(
    { disabled: true },
  );
  expect(screen.getByTestId("payment-cancel").props.accessibilityState).toEqual(
    { disabled: true },
  );

  await fireEvent.press(screen.getByTestId("payment-recover"));
  await fireEvent.press(screen.getByTestId("payment-cancel"));
  expect(square.spies.recover).not.toHaveBeenCalled();
  expect(square.spies.cancel).not.toHaveBeenCalled();
  await screen.unmount();
});

test("现金续付确认中显示中英文专属状态并禁用重复确认", async () => {
  const state: Partial<PaymentPresenterState> = {
    phase: "cash-confirming",
    busy: true,
    selectedMethod: "cash",
    total: aud(1_000),
    remaining: aud(0),
    allowedActions: actions(),
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "confirming",
    },
  };
  const english = createUiPresenter(state);
  const englishScreen = await render(
    <PaymentScreen
      locale="en"
      presenter={english.presenter}
      showStatusStrip={false}
    />,
  );
  expect(englishScreen.getByTestId("payment-status-cash-confirming")).toBeTruthy();
  expect(englishScreen.getByText("Confirming cash repayment")).toBeTruthy();
  expect(englishScreen.getByText("Saving the confirmed cash receipt. Keep this screen open and do not tap again.")).toBeTruthy();
  expect(englishScreen.getByTestId("payment-confirm").props.accessibilityState).toEqual({
    disabled: true,
  });
  expect(englishScreen.getByText("Confirm cash received")).toBeTruthy();
  await englishScreen.unmount();

  const chinese = createUiPresenter(state);
  const chineseScreen = await render(
    <PaymentScreen
      locale="zh"
      presenter={chinese.presenter}
      showStatusStrip={false}
    />,
  );
  expect(chineseScreen.getByText("正在确认现金续付")).toBeTruthy();
  expect(chineseScreen.getByText("正在保存已确认的现金收款，请保持页面打开，不要重复点击。")).toBeTruthy();
  await chineseScreen.unmount();
});

test("Square 一秒恢复仍锚定 createdAt 的固定两秒 cadence", async () => {
  jest.useFakeTimers();
  const createdAtIso = "2026-08-09T00:30:00.000Z";
  const createdAtMs = Date.parse(createdAtIso);
  jest.setSystemTime(new Date(createdAtIso));
  const square = createUiPresenter({
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: "order-square-one-second-recovery",
    attemptId: "attempt-square-one-second-recovery",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const startedAt: number[] = [];
  let active = 0;
  let maximumActive = 0;
  square.spies.recover.mockImplementation(async () => {
    startedAt.push(Date.now() - createdAtMs);
    active += 1;
    maximumActive = Math.max(maximumActive, active);
    await new Promise<void>((resolve) => setTimeout(resolve, 1_000));
    active -= 1;
    return false;
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={square.presenter}
      showStatusStrip={false}
    />,
  );

  try {
    await act(async () => {
      await jest.advanceTimersByTimeAsync(7_000);
    });
    expect(startedAt.slice(0, 3)).toEqual([2_000, 4_000, 6_000]);
    expect(maximumActive).toBe(1);
  } finally {
    await screen.unmount();
    jest.useRealTimers();
  }
});

test("Square 慢恢复在 90 秒后不再发起新请求；Linkly Pending 在 2 秒后恢复同一 attempt", async () => {
  jest.useFakeTimers();
  const createdAtIso = "2026-08-09T01:00:00.000Z";
  const createdAtMs = Date.parse(createdAtIso);
  jest.setSystemTime(new Date(createdAtIso));
  const square = createUiPresenter({
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: "order-square-bounded-recovery",
    attemptId: "attempt-square-bounded-recovery",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const startedAt: number[] = [];
  const recoverySignals: AbortSignal[] = [];
  let active = 0;
  let maximumActive = 0;
  square.spies.recover.mockImplementation(async (options) => {
    startedAt.push(Date.now() - createdAtMs);
    if (options?.signal) recoverySignals.push(options.signal);
    active += 1;
    maximumActive = Math.max(maximumActive, active);
    await new Promise<void>((resolve) => setTimeout(resolve, 5_000));
    active -= 1;
    return false;
  });
  const squareScreen = await render(
    <PaymentScreen
      locale="zh"
      presenter={square.presenter}
      showStatusStrip={false}
    />,
  );

  try {
    await act(async () => {
      await jest.advanceTimersByTimeAsync(100_000);
    });
    const callsAtDeadline = square.spies.recover.mock.calls.length;
    expect(callsAtDeadline).toBeGreaterThan(1);
    expect(callsAtDeadline).toBeLessThan(45);
    expect(maximumActive).toBe(1);
    expect(startedAt.slice(0, 3)).toEqual([2_000, 8_000, 14_000]);
    expect(startedAt.every((value) => value % 2_000 === 0)).toBe(true);
    expect(startedAt.every((value) => value < 90_000)).toBe(true);
    expect(new Set(recoverySignals).size).toBe(callsAtDeadline);
    expect(recoverySignals.at(-1)?.aborted).toBe(true);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(100_000);
    });
    expect(square.spies.recover).toHaveBeenCalledTimes(callsAtDeadline);
    await squareScreen.unmount();

    jest.setSystemTime(new Date(createdAtMs));
    const linkly = createUiPresenter({
      phase: "pending",
      provider: "linkly-cloud",
      runtimeStatus: "pending",
      orderGuid: "order-linkly-auto-recovery",
      attemptId: "attempt-linkly-auto-recovery",
      attemptCreatedAtIso: createdAtIso,
      allowedActions: actions({ recover: true, cancel: true }),
    });
    const linklyScreen = await render(
      <PaymentScreen
        locale="zh"
        presenter={linkly.presenter}
        showStatusStrip={false}
      />,
    );
    await act(async () => {
      await jest.advanceTimersByTimeAsync(2_000);
    });
    expect(linkly.spies.recover).toHaveBeenCalledWith({
      background: true,
      deadlineAtMs: createdAtMs + 180_000,
      signal: expect.any(AbortSignal),
    });
    await linklyScreen.unmount();
  } finally {
    jest.useRealTimers();
  }
});

test("同一 Square attempt 重挂载不会重置 90 秒恢复窗口", async () => {
  jest.useFakeTimers();
  const createdAtIso = "2026-08-09T02:00:00.000Z";
  jest.setSystemTime(new Date(Date.parse(createdAtIso) + 87_000));
  const pendingState: Partial<PaymentPresenterState> = {
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: "order-square-remount-deadline",
    attemptId: "attempt-square-remount-deadline",
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  };
  const first = createUiPresenter(pendingState);
  const firstScreen = await render(
    <PaymentScreen
      locale="zh"
      presenter={first.presenter}
      showStatusStrip={false}
    />,
  );

  try {
    await act(async () => {
      await jest.advanceTimersByTimeAsync(999);
    });
    expect(first.spies.recover).not.toHaveBeenCalled();
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1);
    });
    expect(first.spies.recover).toHaveBeenCalledTimes(1);
    await firstScreen.unmount();

    const remounted = createUiPresenter(pendingState);
    const remountedScreen = await render(
      <PaymentScreen
        locale="zh"
        presenter={remounted.presenter}
        showStatusStrip={false}
      />,
    );
    await act(async () => {
      await jest.advanceTimersByTimeAsync(2_000);
    });
    expect(remounted.spies.recover).not.toHaveBeenCalled();
    await remountedScreen.unmount();
  } finally {
    jest.useRealTimers();
  }
});

test.each([
  { label: "null", createdAtIso: null },
  { label: "非 canonical", createdAtIso: "2026-08-09T03:00:00Z" },
  { label: "未来", createdAtIso: "2026-08-09T03:00:00.001Z" },
  { label: "已过期", createdAtIso: "2026-08-09T02:58:30.000Z" },
])("Square createdAt $label 时自动恢复 fail closed，手动恢复仍可用", async ({
  createdAtIso,
}) => {
  jest.useFakeTimers();
  jest.setSystemTime(new Date("2026-08-09T03:00:00.000Z"));
  const square = createUiPresenter({
    phase: "pending",
    provider: "square",
    runtimeStatus: "pending",
    orderGuid: `order-square-invalid-${String(createdAtIso)}`,
    attemptId: `attempt-square-invalid-${String(createdAtIso)}`,
    attemptCreatedAtIso: createdAtIso,
    allowedActions: actions({ recover: true, cancel: true }),
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={square.presenter}
      showStatusStrip={false}
    />,
  );

  try {
    await act(async () => {
      await jest.advanceTimersByTimeAsync(10_000);
    });
    expect(square.spies.recover).not.toHaveBeenCalled();

    await fireEvent.press(screen.getByTestId("payment-recover"));
    expect(square.spies.recover).toHaveBeenCalledTimes(1);
    expect(square.spies.recover).toHaveBeenCalledWith();
  } finally {
    await screen.unmount();
    jest.useRealTimers();
  }
});

test.each(["unmount", "attempt", "provider", "phase", "presenter"] as const)(
  "Square 后台恢复在 %s 生命周期变化时只 abort 自己的 controller",
  async (transition) => {
    jest.useFakeTimers();
    const createdAtIso = new Date("2026-08-09T04:00:00.000Z").toISOString();
    jest.setSystemTime(new Date(createdAtIso));
    const square = createUiPresenter({
      phase: "pending",
      provider: "square",
      runtimeStatus: "pending",
      orderGuid: `order-square-abort-${transition}`,
      attemptId: `attempt-square-abort-${transition}`,
      attemptCreatedAtIso: createdAtIso,
      allowedActions: actions({ recover: true, cancel: true }),
    });
    let ownedSignal: AbortSignal | null = null;
    square.spies.recover.mockImplementation(async (options) => {
      ownedSignal = options?.signal ?? null;
      return new Promise<boolean>((resolve) => {
        ownedSignal?.addEventListener("abort", () => resolve(false), {
          once: true,
        });
      });
    });
    const screen = await render(
      <PaymentScreen
        locale="zh"
        presenter={square.presenter}
        showStatusStrip={false}
      />,
    );

    try {
      await act(async () => {
        await jest.advanceTimersByTimeAsync(2_000);
      });
      const signalAfterTick = ownedSignal as AbortSignal | null;
      expect(signalAfterTick).toBeTruthy();
      expect(signalAfterTick?.aborted).toBe(false);

      if (transition === "unmount") {
        await screen.unmount();
      } else if (transition === "presenter") {
        const replacement = createUiPresenter();
        await screen.rerender(
          <PaymentScreen
            locale="zh"
            presenter={replacement.presenter}
            showStatusStrip={false}
          />,
        );
      } else {
        await act(async () => {
          square.publish({
            ...square.presenter.getState(),
            ...(transition === "attempt"
              ? { attemptId: `attempt-square-abort-${transition}-next` }
              : transition === "provider"
                ? { provider: "linkly-cloud" as const }
                : { phase: "unknown" as const }),
          });
        });
      }

      expect(signalAfterTick?.aborted).toBe(true);
    } finally {
      if (transition !== "unmount") await screen.unmount();
      jest.useRealTimers();
    }
  },
);

test.each(
  [
    { height: 768, label: "1024×768", width: 1024 },
    { height: 810, label: "1080×810", width: 1080 },
    { height: 834, label: "1194×834", width: 1194 },
    { height: 1024, label: "1366×1024", width: 1366 },
  ].flatMap((viewport) =>
    (
      ["regular", "installment-create", "installment-repayment"] as const
    ).map((flow) => ({
      ...viewport,
      flow,
      label: `${viewport.label} ${flow}`,
    })),
  ),
)("$label 仍为单列手持滚动流且通过弹窗提供付款动作", async ({
  flow,
  height,
  width,
}) => {
  setPaymentWindowSize(width, height);
  const installment = flow !== "regular";
  const { presenter } = createUiPresenter({
    selectedMethod: "cash",
    orderGuid: installment ? "order-installment-ui" : null,
    checkout: {
      flow,
      lines: Array.from({ length: 12 }, (_, index) => ({
        lineKey: `line-layout-${index}`,
        displayName: `Layout item ${index}`,
        quantity: "1",
        actualAmountCents: 100,
      })),
      installmentCustomer: installment
        ? {
            name: "Bob",
            phone: "0400000000",
            editable: flow === "installment-create",
            editorOpen: false,
            draftName: "Bob",
            draftPhone: "0400000000",
            installmentNumber:
              flow === "installment-repayment" ? "IP-0001" : null,
          }
        : null,
      cash: {
        tenderedCents: 0,
        appliedCents: 0,
        changeCents: 0,
      },
      canConfirm: installment,
      fullInstallmentConfirmationRequired: false,
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await openPaymentEntry(screen, "cash");
  expect(screen.getByTestId("payment-content-scroll").props.scrollEnabled).toBe(
    true,
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-workspace").props.style),
  ).toMatchObject({
    flex: 0,
    flexDirection: "column",
    minHeight: 0,
    overflow: "visible",
  });
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-context-pane").props.style)
      .flex,
  ).toBe(0);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-entry-pane").props.style)
      .flex,
  ).toBe(0);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-summary").props.style).flex,
  ).toBe(0);

  expect(screen.getByTestId("payment-context-lines").props.scrollEnabled).toBe(
    true,
  );
  expect(screen.getByTestId("payment-entry-scroll").props.scrollEnabled).toBe(
    true,
  );
  expect(screen.getByTestId("payment-summary-scroll").props.scrollEnabled).toBe(
    false,
  );

  const entryPane = screen.getByTestId("payment-entry-pane");
  const entryActions = screen.getByTestId("payment-entry-actions");
  expect(entryActions.parent).toBe(entryPane);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-entry-cancel").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-submit").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);

  if (installment) {
    const summary = screen.getByTestId("payment-summary");
    const summaryFooter = screen.getByTestId("payment-summary-footer");
    expect(summaryFooter.parent).toBe(summary);
    expect(
      StyleSheet.flatten(screen.getByTestId("payment-confirm").props.style)
        .minHeight,
    ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  } else {
    expect(screen.queryByTestId("payment-summary-footer")).toBeNull();
  }

  await screen.unmount();
});

test.each([
  { height: 768, label: "1024×768", width: 1024 },
  { height: 810, label: "1080×810", width: 1080 },
  { height: 834, label: "1194×834", width: 1194 },
])("$label 新建分期客户编辑独立避让键盘且操作保持 48px", async ({
  height,
  width,
}) => {
  setPaymentWindowSize(width, height);
  const { presenter } = createUiPresenter({
    orderGuid: "order-installment-customer-layout",
    checkout: {
      flow: "installment-create",
      lines: Array.from({ length: 12 }, (_, index) => ({
        lineKey: `line-customer-layout-${index}`,
        displayName: `Customer layout item ${index}`,
        quantity: "1",
        actualAmountCents: 100,
      })),
      installmentCustomer: {
        name: "Bob",
        phone: "0400000000",
        editable: true,
        editorOpen: false,
        draftName: "Bob",
        draftPhone: "0400000000",
        installmentNumber: null,
      },
      cash: {
        tenderedCents: 0,
        appliedCents: 0,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await fireEvent.press(screen.getByTestId("payment-customer-edit"));

  expect(screen.getByTestId("payment-content-scroll").props.scrollEnabled).toBe(
    true,
  );
  const customerScroll = screen.getByTestId("payment-customer-scroll");
  expect(customerScroll.props).toMatchObject({
    automaticallyAdjustKeyboardInsets: true,
    keyboardDismissMode: "interactive",
    keyboardShouldPersistTaps: "handled",
    nestedScrollEnabled: true,
    scrollEnabled: true,
  });
  expect(StyleSheet.flatten(customerScroll.props.style)).toMatchObject({
    flexShrink: 1,
    minHeight: 0,
  });
  expect(screen.getByTestId("payment-context-lines").props.scrollEnabled).toBe(
    true,
  );
  for (const action of [
    "payment-customer-cancel",
    "payment-customer-save",
  ]) {
    expect(
      StyleSheet.flatten(screen.getByTestId(action).props.style).minHeight,
    ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  }

  await screen.unmount();
});

test("任意视口都保持单列付款流，并在金额弹窗提供五个现金快捷金额", async () => {
  setPaymentWindowSize(1366, 1024);
  const { presenter, spies } = createUiPresenter({
    selectedMethod: "cash",
  });
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );
  await openPaymentEntry(screen, "cash");

  expect(
    StyleSheet.flatten(screen.getByTestId("payment-workspace").props.style)
      .flexDirection,
  ).toBe("column");
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-context-pane").props.style)
      .minWidth,
  ).toBe(0);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-entry-pane").props.style)
      .flex,
  ).toBe(0);
  expect(screen.getByTestId("handheld-state-cash-payment")).toBeTruthy();
  expect(screen.getByTestId("payment-keypad")).toBeTruthy();
  for (const key of [
    "1",
    "2",
    "3",
    "4",
    "5",
    "6",
    "7",
    "8",
    "9",
    "decimal",
    "0",
    "backspace",
  ]) {
    expect(screen.getByTestId(`payment-key-${key}`)).toBeTruthy();
  }
  expect(screen.getByTestId("payment-amount").props.showSoftInputOnFocus)
    .toBe(false);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-key-1").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-cash-quick").props.style)
      .flexWrap,
  ).toBe("nowrap");

  const banknoteColors = {
    5: "#E7C5DD",
    10: "#B9DCEB",
    20: "#EDB5AA",
    50: "#F4DB7F",
    100: "#B9D8B4",
  } as const;
  for (const amount of [5, 10, 20, 50, 100] as const) {
    expect(
      screen.getByTestId(`payment-cash-quick-${amount}`),
    ).toBeTruthy();
    expect(
      StyleSheet.flatten(
        screen.getByTestId(`payment-cash-quick-${amount}`).props.style,
      ).backgroundColor,
    ).toBe(banknoteColors[amount]);
  }
  expect(screen.queryByText(/exacta?/i)).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-key-1"));
  expect(spies.setAmountText).toHaveBeenCalledWith("1");
  await fireEvent.press(screen.getByTestId("payment-cash-quick-50"));
  expect(spies.setAmountText).toHaveBeenCalledWith("50.00");
  await screen.unmount();
  setPaymentWindowSize(750, 1334);
});

test("宽屏模拟仍使用手持单列密度，数字键盘和支付动作保持可达", async () => {
  setPaymentWindowSize(1194, 834);
  const { presenter } = createUiPresenter({
    selectedMethod: "cash",
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );
  await openPaymentEntry(screen, "cash");

  expect(
    StyleSheet.flatten(screen.getByTestId("payment-workspace").props.style)
      .flexDirection,
  ).toBe("column");
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-key-1").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  expect(
    StyleSheet.flatten(screen.getByTestId("payment-cash-quick").props.style)
      .flexWrap,
  ).toBe("nowrap");
  expect(
    StyleSheet.flatten(
      screen.getByTestId("payment-cash-quick-5").props.style,
    ).minWidth,
  ).toBe(PAYMENT_MIN_TOUCH_TARGET);
  expect(
    StyleSheet.flatten(
      screen.getByTestId("payment-cash-quick-5").props.style,
    ).flexBasis,
  ).toBe(0);
  expect(screen.getByTestId("payment-submit")).toBeTruthy();

  await screen.unmount();
  setPaymentWindowSize(750, 1334);
});

test("窄屏支付页改为单列且分期开关保持可达", async () => {
  setPaymentWindowSize(750, 1334);
  const { presenter } = createUiPresenter();
  const screen = await render(
    <PaymentScreen
      installmentModeControl={{
        enabled: false,
        locked: false,
        issue: null,
        onToggle: jest.fn(),
      }}
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );
  await openPaymentEntry(screen, "square");

  expect(
    StyleSheet.flatten(screen.getByTestId("payment-workspace").props.style)
      .flexDirection,
  ).toBe("column");
  expect(
    StyleSheet.flatten(
      screen.getByTestId("payment-context-pane").props.style,
    ).maxHeight,
  ).toBe(320);
  expect(screen.getByTestId("payment-content-scroll").props).toMatchObject({
    automaticallyAdjustKeyboardInsets: true,
    keyboardDismissMode: "interactive",
    keyboardShouldPersistTaps: "handled",
    scrollEnabled: true,
  });
  expect(screen.getByTestId("payment-entry-scroll").props.scrollEnabled).toBe(
    true,
  );
  expect(screen.getByTestId("payment-summary-scroll").props.scrollEnabled).toBe(
    false,
  );
  expect(screen.getByTestId("payment-installment-toggle")).toBeTruthy();
  await screen.unmount();
});

test("五个设计状态只由真实 presenter 状态与付款方式驱动", async () => {
  const { presenter, publish } = createUiPresenter({
    phase: "ready",
    selectedMethod: "square",
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByTestId("handheld-state-payment-method")).toBeTruthy();

  await act(async () => {
    publish({ ...presenter.getState(), selectedMethod: "cash" });
  });
  expect(screen.getByTestId("handheld-state-cash-payment")).toBeTruthy();

  await act(async () => {
    publish({
      ...presenter.getState(),
      phase: "submitting",
      busy: true,
      provider: "square",
      selectedMethod: "square",
    });
  });
  expect(screen.getByTestId("handheld-state-card-processing")).toBeTruthy();

  await act(async () => {
    publish({
      ...presenter.getState(),
      phase: "success",
      busy: false,
      orderGuid: "order-state-surface",
      remaining: aud(0),
    });
  });
  expect(screen.getByTestId("handheld-state-payment-success")).toBeTruthy();

  await act(async () => {
    publish({
      ...presenter.getState(),
      phase: "declined",
      orderGuid: "order-state-surface",
    });
  });
  expect(screen.getByTestId("handheld-state-payment-failure")).toBeTruthy();
  await screen.unmount();
});

test("无耐久支付事实的初始化失败仍允许返回收银", async () => {
  const { presenter } = createUiPresenter({
    phase: "recovery-required",
    runtimeErrorCode: "ONLINE_REQUIRED",
    allowedActions: actions(),
  });
  const onBack = jest.fn();
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onBack={onBack}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  const back = screen.getByTestId("payment-back");
  expect(back.props.accessibilityState).toEqual({ disabled: false });
  await fireEvent.press(back);
  expect(onBack).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("Square 终端取消已耐久关闭后允许返回收银", async () => {
  const { presenter } = createUiPresenter({
    phase: "cancelled",
    orderGuid: "order-square-terminal-cancelled",
    attemptId: null,
    attemptCreatedAtIso: null,
    provider: null,
    runtimeStatus: "cancelled",
    allowedActions: actions(),
  });
  const onBack = jest.fn();
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onBack={onBack}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  const back = screen.getByTestId("payment-back");
  expect(back.props.accessibilityState).toEqual({ disabled: false });
  await fireEvent.press(back);
  expect(onBack).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("分期现金 fence 建立后禁用返回且 press 不离开支付页", async () => {
  for (const cashRepaymentStatus of ["ready", "confirming"] as const) {
    const { presenter } = createUiPresenter({
      phase:
        cashRepaymentStatus === "ready"
          ? "cash-collection-ready"
          : "cash-confirming",
      busy: false,
      selectedMethod: "cash",
      checkout: {
        flow: "installment-repayment",
        lines: [],
        installmentCustomer: null,
        cash: {
          tenderedCents: 1_000,
          appliedCents: 1_000,
          changeCents: 0,
        },
        canConfirm: cashRepaymentStatus === "ready",
        fullInstallmentConfirmationRequired: false,
        cashRepaymentStatus,
      },
    });
    const onBack = jest.fn();
    const screen = await render(
      <PaymentScreen
        locale="zh"
        onBack={onBack}
        presenter={presenter}
        showStatusStrip={false}
      />,
    );

    const back = screen.getByTestId("payment-back");
    expect(back.props.accessibilityState).toMatchObject({ disabled: true });
    await fireEvent.press(back);
    expect(onBack).not.toHaveBeenCalled();
    await screen.unmount();
  }
});

test("分期不可逆现金 tender 即使状态字段丢失也禁止返回", async () => {
  const { presenter } = createUiPresenter({
    phase: "recovery-required",
    selectedMethod: "cash",
    tenders: [{
      tenderGuid: "durable-cash-fence",
      method: "cash",
      amount: aud(1_000),
      reversible: false,
      provider: null,
    }],
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "idle",
    },
  });
  const onBack = jest.fn();
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onBack={onBack}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  const back = screen.getByTestId("payment-back");
  expect(back.props.accessibilityState).toMatchObject({ disabled: true });
  await fireEvent.press(back);
  expect(onBack).not.toHaveBeenCalled();
  await screen.unmount();
});

test("尚未收现取消续付先显示中文主管确认 Modal，放弃时零调用", async () => {
  const harness = createUiPresenter({
    phase: "cash-collection-ready",
    selectedMethod: "cash",
    tenders: [{
      tenderGuid: "prepared-cash-cancel-zh",
      method: "cash",
      amount: aud(1_000),
      reversible: false,
      provider: null,
    }],
    allowedActions: actions({ cancel: true }),
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "ready",
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  const cancelPrepared = screen.getByTestId("payment-cancel-prepared-cash");
  expect(screen.getByText("尚未收现，取消续付")).toBeTruthy();
  expect(
    StyleSheet.flatten(cancelPrepared.props.style).minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  await fireEvent.press(cancelPrepared);
  expect(harness.spies.cancel).not.toHaveBeenCalled();
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeTruthy();
  expect(screen.getByText("确认本次现金尚未收取")).toBeTruthy();
  expect(
    screen.getByText(
      "此操作需要主管授权。仅在核对钱箱并确认本次续付现金尚未收取时继续。若现金已收取或无法确定，请返回并由主管恢复原操作。",
    ),
  ).toBeTruthy();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: true,
    });
  });
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-dismiss").props
      .accessibilityState,
  ).toMatchObject({ disabled: true });
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirm").props
      .accessibilityState,
  ).toMatchObject({ disabled: true });
  await fireEvent.press(
    screen.getByTestId("payment-cancel-prepared-cash-confirm"),
  );
  expect(harness.spies.cancel).not.toHaveBeenCalled();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: false,
    });
  });

  await fireEvent.press(
    screen.getByTestId("payment-cancel-prepared-cash-dismiss"),
  );
  expect(harness.spies.cancel).not.toHaveBeenCalled();
  expect(harness.spies.removeTender).not.toHaveBeenCalled();
  expect(
    screen.queryByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeNull();
  await screen.unmount();
});

test("尚未收现取消续付英文 Modal 确认后只调用 presenter cancel 一次", async () => {
  const harness = createUiPresenter({
    phase: "cash-collection-ready",
    selectedMethod: "cash",
    tenders: [{
      tenderGuid: "prepared-cash-cancel-en",
      method: "cash",
      amount: aud(1_000),
      reversible: false,
      provider: null,
    }],
    allowedActions: actions({ cancel: true }),
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "ready",
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.getByText("Cash not received — cancel repayment")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-cancel-prepared-cash"));
  expect(screen.getByText("Confirm cash was not received")).toBeTruthy();
  expect(
    screen.getByText(
      "This action requires supervisor authorization. Only continue after checking the cash drawer and confirming that no cash was collected for this repayment. If cash was collected or you are unsure, go back and ask a supervisor to recover the existing operation.",
    ),
  ).toBeTruthy();
  const confirm = screen.getByTestId("payment-cancel-prepared-cash-confirm");
  expect(
    StyleSheet.flatten(confirm.props.style).minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  await fireEvent.press(confirm);
  expect(harness.spies.cancel).toHaveBeenCalledTimes(1);
  expect(
    screen.queryByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeNull();
  await screen.unmount();
});

test("取消 Prepared 现金失败后仅保留同一取消重试，且继续禁止返回", async () => {
  const harness = createUiPresenter({
    phase: "recovery-required",
    runtimeErrorCode: "INSTALLMENT_CASH_CANCELLATION_FAILED",
    selectedMethod: "cash",
    tenders: [{
      tenderGuid: "prepared-cash-cancel-failed",
      method: "cash",
      amount: aud(1_000),
      reversible: false,
      provider: null,
    }],
    allowedActions: actions({ cancel: true }),
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "idle",
    },
  });
  const onBack = jest.fn();
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onBack={onBack}
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.queryByTestId("payment-recover")).toBeNull();
  expect(screen.getByTestId("payment-cancel-prepared-cash")).toBeTruthy();
  expect(screen.queryByTestId("payment-confirm-cash-recovery")).toBeNull();
  const back = screen.getByTestId("payment-back");
  expect(back.props.accessibilityState).toMatchObject({ disabled: true });
  await fireEvent.press(back);
  expect(onBack).not.toHaveBeenCalled();
  await fireEvent.press(screen.getByTestId("payment-cancel-prepared-cash"));
  expect(harness.spies.cancel).not.toHaveBeenCalled();
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeTruthy();
  await screen.unmount();
});

test("普通支付 cancel 保持直接调用且不显示现金续付 Modal", async () => {
  const harness = createUiPresenter({
    phase: "pending",
    orderGuid: "ordinary-order-cancel",
    attemptId: "ordinary-attempt-cancel",
    allowedActions: actions({ cancel: true }),
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  expect(screen.queryByTestId("payment-cancel-prepared-cash")).toBeNull();
  await fireEvent.press(screen.getByTestId("payment-cancel"));
  expect(harness.spies.cancel).toHaveBeenCalledTimes(1);
  expect(
    screen.queryByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeNull();
  await screen.unmount();
});

test("成功状态由 React commit 后通知 presenter 且重复 render 不重复通知", async () => {
  const harness = createUiPresenter();
  const recordSuccessRendered = jest.fn();
  Object.assign(harness.presenter, { recordSuccessRendered });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );
  expect(recordSuccessRendered).not.toHaveBeenCalled();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      phase: "success",
      orderGuid: "installment-success-operation",
    });
  });
  expect(screen.getByTestId("payment-status-success")).toBeTruthy();
  expect(recordSuccessRendered).toHaveBeenCalledTimes(1);

  await screen.rerender(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );
  expect(recordSuccessRendered).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("成功指标 hook 抛错不影响成功页面", async () => {
  const harness = createUiPresenter({
    phase: "success",
    orderGuid: "installment-success-metric-failure",
  });
  Object.assign(harness.presenter, {
    recordSuccessRendered: () => {
      throw new Error("metrics unavailable");
    },
  });

  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );
  expect(screen.getByTestId("payment-status-success")).toBeTruthy();
  await screen.unmount();
});

test("分期开关保持 48px、switch 语义和稳定失败提示", async () => {
  const { presenter } = createUiPresenter();
  const onToggle = jest.fn();
  const screen = await render(
    <PaymentScreen
      installmentModeControl={{
        enabled: false,
        locked: false,
        issue: "unavailable",
        onToggle,
      }}
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  const toggle = screen.getByTestId("payment-installment-toggle");
  expect(toggle.props.accessibilityRole).toBe("switch");
  expect(toggle.props.accessibilityState).toEqual({
    checked: false,
    disabled: false,
  });
  expect(
    StyleSheet.flatten(toggle.props.style).minHeight,
  ).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
  expect(
    screen.getByText(
      "Installment mode is unavailable. Return to sale and try again.",
    ),
  ).toBeTruthy();

  await fireEvent.press(toggle);
  expect(onToggle).toHaveBeenCalledWith(true);

  await screen.rerender(
    <PaymentScreen
      installmentModeControl={{
        enabled: true,
        locked: true,
        issue: null,
        onToggle,
      }}
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );
  const lockedToggle = screen.getByTestId("payment-installment-toggle");
  expect(lockedToggle.props.accessibilityState).toEqual({
    checked: true,
    disabled: true,
  });
  await fireEvent.press(lockedToggle);
  expect(onToggle).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("分期现金超付显示入账与找零，并可确认付款", async () => {
  const { presenter, spies } = createUiPresenter(
    {
      checkout: {
        flow: "installment-create",
        lines: [
          {
            lineKey: "line-ui-1",
            displayName: "Tea",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
        installmentCustomer: {
          name: "Bob",
          phone: "0400000000",
          editable: true,
          editorOpen: false,
          draftName: "Bob",
          draftPhone: "0400000000",
          installmentNumber: null,
        },
        cash: {
          tenderedCents: 0,
          appliedCents: 0,
          changeCents: 0,
        },
        canConfirm: false,
        fullInstallmentConfirmationRequired: false,
      },
      selectedMethod: "cash",
      total: aud(5_000),
      remaining: aud(5_000),
    },
    (state) => ({
      ...state,
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "cash-ui-1",
          method: "cash",
          amount: aud(5_000),
          reversible: true,
        },
      ],
      allowedActions: actions(),
      checkout: {
        ...state.checkout,
        cash: {
          tenderedCents: 6_000,
          appliedCents: 5_000,
          changeCents: 1_000,
        },
        canConfirm: true,
        fullInstallmentConfirmationRequired: true,
      },
    }),
  );
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await openPaymentEntry(screen, "cash");
  await fireEvent.changeText(screen.getByTestId("payment-amount"), "60.00");
  await fireEvent.press(screen.getByTestId("payment-submit"));

  await waitFor(() =>
    expect(screen.getByTestId("payment-cash-applied")).toHaveTextContent(
      formatAud(5_000, "en"),
    ),
  );
  expect(screen.getByTestId("payment-change")).toHaveTextContent(
    formatAud(1_000, "en"),
  );
  await fireEvent.press(screen.getByTestId("payment-confirm"));
  expect(
    screen.getByTestId("payment-full-installment-confirmation"),
  ).toBeTruthy();
  expect(spies.confirm).not.toHaveBeenCalled();

  await fireEvent.press(
    screen.getByTestId("payment-full-installment-cancel"),
  );
  expect(
    screen.queryByTestId("payment-full-installment-confirmation"),
  ).toBeNull();
  expect(screen.getByTestId("payment-remove-cash-ui-1")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("payment-confirm"));
  await fireEvent.press(
    screen.getByTestId("payment-full-installment-confirm"),
  );
  expect(spies.confirm).toHaveBeenCalledWith({
    acknowledgeFullInstallmentPayment: true,
  });
  await screen.unmount();
});

test("新建分期顾客可编辑，续付顾客只读且已选 provider 冻结", async () => {
  const create = createUiPresenter({
    checkout: {
      flow: "installment-create",
      lines: [],
      installmentCustomer: {
        name: "Bob",
        phone: "0400000000",
        editable: true,
        editorOpen: false,
        draftName: "Bob",
        draftPhone: "0400000000",
        installmentNumber: null,
      },
      cash: {
        tenderedCents: 0,
        appliedCents: 0,
        changeCents: 0,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
    },
  });
  const createScreen = await render(
    <PaymentScreen
      locale="en"
      presenter={create.presenter}
      showStatusStrip={false}
    />,
  );

  await fireEvent.press(createScreen.getByTestId("payment-customer-edit"));
  await fireEvent.changeText(
    createScreen.getByTestId("payment-customer-name"),
    "Alice",
  );
  await fireEvent.changeText(
    createScreen.getByTestId("payment-customer-phone"),
    "0411111111",
  );
  await fireEvent.press(createScreen.getByTestId("payment-customer-save"));
  expect(create.spies.setInstallmentCustomerDraftName).toHaveBeenCalledWith(
    "Alice",
  );
  expect(create.spies.setInstallmentCustomerDraftPhone).toHaveBeenCalledWith(
    "0411111111",
  );
  expect(create.spies.saveInstallmentCustomer).toHaveBeenCalledTimes(1);
  await createScreen.unmount();

  const repayment = createUiPresenter({
    allowedActions: actions(),
    selectedMethod: "linkly-cloud",
    provider: "linkly-cloud",
    tenders: [
      {
        tenderGuid: "card-ui-1",
        method: "card",
        amount: aud(1_000),
        reversible: false,
        provider: "linkly-cloud",
      },
    ],
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: {
        name: "Bob",
        phone: "0400000000",
        editable: false,
        editorOpen: false,
        draftName: "Bob",
        draftPhone: "0400000000",
        installmentNumber: "IP-0001",
      },
      cash: {
        tenderedCents: 0,
        appliedCents: 0,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
    },
  });
  const repaymentScreen = await render(
    <PaymentScreen
      locale="en"
      presenter={repayment.presenter}
      showStatusStrip={false}
    />,
  );

  expect(repaymentScreen.queryByTestId("payment-customer-edit")).toBeNull();
  expect(repaymentScreen.queryByTestId("payment-customer-name")).toBeNull();
  expect(
    repaymentScreen.getByTestId("payment-method-square").props
      .accessibilityState,
  ).toEqual({ disabled: true, selected: false });
  expect(
    repaymentScreen.getByTestId("payment-method-linkly-cloud").props
      .accessibilityState,
  ).toEqual({ disabled: true, selected: true });
  await fireEvent.press(
    repaymentScreen.getByTestId("payment-method-square"),
  );
  expect(repayment.spies.selectMethod).not.toHaveBeenCalled();
  await repaymentScreen.unmount();
});

test("真实分期 presenter 点击客户编辑仍保留实例上下文", async () => {
  const unusedWorkflowOperation = async (): Promise<never> => {
    throw new Error("本测试不应执行分期写操作");
  };
  const workflow: InstallmentWorkflowPort = {
    listPaymentProviderAvailability: async () => [
      providerAvailability("square"),
      providerAvailability("linkly-cloud"),
      providerAvailability("voucher"),
    ],
    list: async () => [],
    getDetails: async () => null,
    recoverBlocking: unusedWorkflowOperation,
    create: unusedWorkflowOperation,
    addRepayment: unusedWorkflowOperation,
    cancelWithRefund: unusedWorkflowOperation,
    void: unusedWorkflowOperation,
    confirmPickup: unusedWorkflowOperation,
  };
  const presenter = new InstallmentCheckoutPresenter({
    entry: installmentCreatePaymentEntry({
      checkoutIntentId: "11111111-1111-4111-8111-111111111111",
      expectedCartRevision: 7,
    }),
    createDrafts: {
      getSnapshot: () => ({
        revision: 7,
        totalCents: 5_000,
        lines: [
          {
            lineKey: "line-real-presenter",
            displayName: "真实 presenter 商品",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
      }),
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: [
      INSTALLMENTS_CREATE_PERMISSION,
      PAYMENT_PERMISSION.view,
      PAYMENT_PERMISSION.confirm,
      PAYMENT_PERMISSION.takeCash,
    ],
    workflow,
    createTenderId: () => "tender-real-presenter",
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-customer-edit")).toBeTruthy(),
  );
  await openPaymentEntry(screen, "cash");
  await fireEvent.changeText(
    screen.getByTestId("payment-amount"),
    "50.00",
  );
  await fireEvent.press(screen.getByTestId("payment-submit"));
  await waitFor(() =>
    expect(screen.getByTestId("payment-confirm")).toBeTruthy(),
  );
  await fireEvent.press(screen.getByTestId("payment-confirm"));
  await waitFor(() =>
    expect(
      screen.getByText("请填写分期顾客姓名和联系电话。"),
    ).toBeTruthy(),
  );
  expect(
    screen.queryByTestId("payment-full-installment-confirmation"),
  ).toBeNull();

  await fireEvent.press(screen.getByTestId("payment-customer-edit"));
  expect(screen.getByTestId("payment-customer-editor")).toBeTruthy();
  await fireEvent.changeText(
    screen.getByTestId("payment-customer-name"),
    "顾客乙",
  );
  await fireEvent.changeText(
    screen.getByTestId("payment-customer-phone"),
    "0412345678",
  );
  await fireEvent.press(screen.getByTestId("payment-customer-save"));
  expect(screen.getByText("顾客乙")).toBeTruthy();
  expect(screen.getByText("0412345678")).toBeTruthy();
  await screen.unmount();
});

test("真实分期恢复 presenter 可从 Prepared 状态确认原现金 operation", async () => {
  let confirmCalls = 0;
  const unusedWorkflowOperation = async (): Promise<never> => {
    throw new Error("本测试不应执行其他分期写操作");
  };
  const workflow: InstallmentWorkflowPort = {
    listPaymentProviderAvailability: async () => [],
    list: async () => [],
    getDetails: async () => null,
    recoverBlocking: async () => {
      throw new InstallmentWorkflowError(
        "cash-confirmation-required",
        "check drawer",
      );
    },
    inspectPreparedCashRepayment: async () => ({
      installmentGuid: "22222222-2222-4222-8222-222222222222",
      operationHash: "sha256:recovered-operation",
      amountCents: 1_000,
      path: "recovery",
    }),
    confirmPreparedCashRepayment: async () => {
      confirmCalls += 1;
      return {
        installmentGuid: "22222222-2222-4222-8222-222222222222",
        totalCents: 5_000,
        balanceCents: 2_000,
      } as InstallmentDetails;
    },
    cancelPreparedCashRepayment: async () => undefined,
    create: unusedWorkflowOperation,
    addRepayment: unusedWorkflowOperation,
    cancelWithRefund: unusedWorkflowOperation,
    void: unusedWorkflowOperation,
    confirmPickup: unusedWorkflowOperation,
  };
  const presenter = new InstallmentCheckoutPresenter({
    entry: null,
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: [
      INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
      PAYMENT_PERMISSION.view,
      PAYMENT_PERMISSION.confirm,
      PAYMENT_PERMISSION.takeCash,
    ],
    workflow,
    createTenderId: () => "recovered-cash-tender",
  });
  const screen = await render(
    <PaymentScreen locale="zh" presenter={presenter} showStatusStrip={false} />,
  );

  await waitFor(() => expect(screen.getByTestId("payment-recover")).toBeTruthy());
  await fireEvent.press(screen.getByTestId("payment-recover"));
  await waitFor(() =>
    expect(screen.getByTestId("payment-confirm-cash-recovery")).toBeTruthy(),
  );
  expect(screen.queryByTestId("payment-cancel-prepared-cash")).toBeNull();
  expect(screen.getByText("确认已收现金")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-confirm-cash-recovery"));
  await waitFor(() => expect(confirmCalls).toBe(1));
  expect(screen.getByTestId("payment-status-success")).toBeTruthy();
  await screen.unmount();
});

test.each([
  ["Unknown+Prepared", "payment-recovery-required"],
  ["ProviderPending+Prepared", "cash-confirmation-required"],
] as const)(
  "%s 切换主管或重启后只显示显式取消 Modal",
  async (scenario, recoveryErrorCode) => {
    let cancelCalls = 0;
    let inspectCancellableCalls = 0;
    const onBack = jest.fn();
    const unusedWorkflowOperation = async (): Promise<never> => {
      throw new Error("本测试不应执行其他分期写操作");
    };
    const workflow: InstallmentWorkflowPort = {
      listPaymentProviderAvailability: async () => [],
      list: async () => [],
      getDetails: async () => null,
      recoverBlocking: async () => {
        throw new InstallmentWorkflowError(
          recoveryErrorCode,
          scenario,
        );
      },
      inspectPreparedCashRepayment: async () => null,
      inspectCancellablePreparedCashRepayment: async () => {
        inspectCancellableCalls += 1;
        return {
          installmentGuid: "22222222-2222-4222-8222-222222222222",
          operationHash: `sha256:${scenario}`,
          amountCents: 1_000,
          path: "recovery",
        };
      },
      cancelPreparedCashRepayment: async () => {
        cancelCalls += 1;
      },
      create: unusedWorkflowOperation,
      addRepayment: unusedWorkflowOperation,
      cancelWithRefund: unusedWorkflowOperation,
      void: unusedWorkflowOperation,
      confirmPickup: unusedWorkflowOperation,
    };
    const presenter = new InstallmentCheckoutPresenter({
      entry: null,
      createDrafts: {
        getSnapshot: () => null,
        subscribe: () => () => undefined,
      },
      initialOnline: true,
      permissions: [
        INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
        INSTALLMENTS_CANCEL_PERMISSION,
        PAYMENT_PERMISSION.view,
        PAYMENT_PERMISSION.confirm,
        PAYMENT_PERMISSION.takeCash,
      ],
      workflow,
      createTenderId: () => `${scenario}-cancel-tender`,
    });
    const screen = await render(
      <PaymentScreen
        locale="zh"
        onBack={onBack}
        presenter={presenter}
        showStatusStrip={false}
      />,
    );

    await waitFor(() =>
      expect(screen.getByTestId("payment-recover")).toBeTruthy(),
    );
    await fireEvent.press(screen.getByTestId("payment-recover"));
    await waitFor(() =>
      expect(
        screen.getByTestId("payment-cancel-prepared-cash"),
      ).toBeTruthy(),
    );
    expect(inspectCancellableCalls).toBe(1);
    expect(screen.queryByTestId("payment-recover")).toBeNull();
    expect(
      screen.queryByTestId("payment-confirm-cash-recovery"),
    ).toBeNull();
    expect(screen.queryByTestId("payment-confirm")).toBeNull();
    const back = screen.getByTestId("payment-back");
    expect(back.props.accessibilityState).toMatchObject({ disabled: true });
    await fireEvent.press(back);
    expect(onBack).not.toHaveBeenCalled();

    await fireEvent.press(
      screen.getByTestId("payment-cancel-prepared-cash"),
    );
    expect(cancelCalls).toBe(0);
    expect(
      screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
    ).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("payment-cancel-prepared-cash-dismiss"),
    );
    expect(cancelCalls).toBe(0);

    await fireEvent.press(
      screen.getByTestId("payment-cancel-prepared-cash"),
    );
    await fireEvent.press(
      screen.getByTestId("payment-cancel-prepared-cash-confirm"),
    );
    await waitFor(() => expect(cancelCalls).toBe(1));
    expect(
      screen.queryByTestId("payment-cancel-prepared-cash-confirmation"),
    ).toBeNull();
    expect(screen.queryByTestId("payment-status-success")).toBeNull();
    await screen.unmount();
  },
);

test("无 Cancel 权限时不可确认 Prepared 保持恢复阻断且不显示取消", async () => {
  let inspectCancellableCalls = 0;
  const onBack = jest.fn();
  const unusedWorkflowOperation = async (): Promise<never> => {
    throw new Error("本测试不应执行其他分期写操作");
  };
  const workflow: InstallmentWorkflowPort = {
    listPaymentProviderAvailability: async () => [],
    list: async () => [],
    getDetails: async () => null,
    recoverBlocking: async () => {
      throw new InstallmentWorkflowError(
        "payment-recovery-required",
        "unknown prepared claim",
      );
    },
    inspectCancellablePreparedCashRepayment: async () => {
      inspectCancellableCalls += 1;
      return {
        installmentGuid: "22222222-2222-4222-8222-222222222222",
        operationHash: "sha256:unauthorized-cancellable",
        amountCents: 1_000,
        path: "recovery",
      };
    },
    cancelPreparedCashRepayment: async () => undefined,
    create: unusedWorkflowOperation,
    addRepayment: unusedWorkflowOperation,
    cancelWithRefund: unusedWorkflowOperation,
    void: unusedWorkflowOperation,
    confirmPickup: unusedWorkflowOperation,
  };
  const presenter = new InstallmentCheckoutPresenter({
    entry: null,
    createDrafts: {
      getSnapshot: () => null,
      subscribe: () => () => undefined,
    },
    initialOnline: true,
    permissions: [
      INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
      PAYMENT_PERMISSION.view,
      PAYMENT_PERMISSION.confirm,
      PAYMENT_PERMISSION.takeCash,
    ],
    workflow,
    createTenderId: () => "unauthorized-cancellable-tender",
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      onBack={onBack}
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("payment-recover")).toBeTruthy(),
  );
  await fireEvent.press(screen.getByTestId("payment-recover"));
  await waitFor(() =>
    expect(screen.getByText("支付恢复尚未完成。")).toBeTruthy(),
  );
  expect(inspectCancellableCalls).toBe(0);
  expect(screen.queryByTestId("payment-cancel-prepared-cash")).toBeNull();
  expect(screen.queryByTestId("payment-confirm-cash-recovery")).toBeNull();
  const back = screen.getByTestId("payment-back");
  expect(back.props.accessibilityState).toMatchObject({ disabled: true });
  await fireEvent.press(back);
  expect(onBack).not.toHaveBeenCalled();
  await screen.unmount();
});

test("付款金额弹窗 busy 时遮罩不可关闭，恢复后遮罩关闭", async () => {
  const harness = createUiPresenter({
    selectedMethod: "cash",
    total: aud(1_000),
    remaining: aud(1_000),
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  await openPaymentEntry(screen, "cash");
  expect(screen.getByTestId("payment-entry-modal")).toBeTruthy();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: true,
    });
  });
  await act(async () => {
    screen.getByTestId("payment-entry-native-modal").props.onRequestClose();
  });
  expect(screen.getByTestId("payment-entry-modal")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-entry-backdrop"));
  expect(screen.getByTestId("payment-entry-modal")).toBeTruthy();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: false,
    });
  });
  await fireEvent.press(screen.getByTestId("payment-entry-backdrop"));
  expect(screen.queryByTestId("payment-entry-modal")).toBeNull();
  expect(harness.spies.cancel).not.toHaveBeenCalled();

  await screen.unmount();
});

test("尚未收现取消续付确认弹窗 busy 时遮罩不可关闭", async () => {
  const harness = createUiPresenter({
    phase: "cash-collection-ready",
    selectedMethod: "cash",
    tenders: [
      {
        tenderGuid: "prepared-cash-cancel-zh",
        method: "cash",
        amount: aud(1_000),
        reversible: false,
        provider: null,
      },
    ],
    allowedActions: actions({ cancel: true }),
    checkout: {
      flow: "installment-repayment",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 1_000,
        appliedCents: 1_000,
        changeCents: 0,
      },
      canConfirm: true,
      fullInstallmentConfirmationRequired: false,
      cashRepaymentStatus: "ready",
    },
  });
  const screen = await render(
    <PaymentScreen
      locale="zh"
      presenter={harness.presenter}
      showStatusStrip={false}
    />,
  );

  await fireEvent.press(screen.getByTestId("payment-cancel-prepared-cash"));
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeTruthy();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: true,
    });
  });
  await act(async () => {
    screen
      .getByTestId("payment-cancel-prepared-cash-native-modal")
      .props.onRequestClose();
  });
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeTruthy();
  await fireEvent.press(
    screen.getByTestId("payment-cancel-prepared-cash-backdrop"),
  );
  expect(
    screen.getByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeTruthy();
  expect(harness.spies.cancel).not.toHaveBeenCalled();

  await act(async () => {
    harness.publish({
      ...harness.presenter.getState(),
      busy: false,
    });
  });
  await fireEvent.press(
    screen.getByTestId("payment-cancel-prepared-cash-backdrop"),
  );
  expect(
    screen.queryByTestId("payment-cancel-prepared-cash-confirmation"),
  ).toBeNull();
  expect(harness.spies.cancel).not.toHaveBeenCalled();

  await screen.unmount();
});

test("分期全额付款确认弹窗点击面板外遮罩关闭且不确认", async () => {
  const { presenter, spies } = createUiPresenter(
    {
      checkout: {
        flow: "installment-create",
        lines: [
          {
            lineKey: "line-ui-1",
            displayName: "Tea",
            quantity: "1",
            actualAmountCents: 5_000,
          },
        ],
        installmentCustomer: {
          name: "Bob",
          phone: "0400000000",
          editable: true,
          editorOpen: false,
          draftName: "Bob",
          draftPhone: "0400000000",
          installmentNumber: null,
        },
        cash: {
          tenderedCents: 0,
          appliedCents: 0,
          changeCents: 0,
        },
        canConfirm: false,
        fullInstallmentConfirmationRequired: false,
      },
      selectedMethod: "cash",
      total: aud(5_000),
      remaining: aud(5_000),
    },
    (state) => ({
      ...state,
      remaining: aud(0),
      tenders: [
        {
          tenderGuid: "cash-ui-1",
          method: "cash",
          amount: aud(5_000),
          reversible: true,
        },
      ],
      allowedActions: actions(),
      checkout: {
        ...state.checkout,
        cash: {
          tenderedCents: 6_000,
          appliedCents: 5_000,
          changeCents: 1_000,
        },
        canConfirm: true,
        fullInstallmentConfirmationRequired: true,
      },
    }),
  );
  const screen = await render(
    <PaymentScreen
      locale="en"
      presenter={presenter}
      showStatusStrip={false}
    />,
  );

  await openPaymentEntry(screen, "cash");
  await fireEvent.changeText(screen.getByTestId("payment-amount"), "60.00");
  await fireEvent.press(screen.getByTestId("payment-submit"));

  await waitFor(() =>
    expect(screen.getByTestId("payment-cash-applied")).toHaveTextContent(
      formatAud(5_000, "en"),
    ),
  );
  await fireEvent.press(screen.getByTestId("payment-confirm"));
  expect(
    screen.getByTestId("payment-full-installment-confirmation"),
  ).toBeTruthy();
  expect(spies.confirm).not.toHaveBeenCalled();

  await fireEvent.press(
    screen.getByTestId("payment-full-installment-backdrop"),
  );
  expect(
    screen.queryByTestId("payment-full-installment-confirmation"),
  ).toBeNull();
  expect(spies.confirm).not.toHaveBeenCalled();

  await screen.unmount();
});

test.each(["en", "zh"] as const)("%s 手动刷卡固定余额并要求确认", async (locale) => {
  setPaymentWindowSize(locale === "en" ? 320 : 360, locale === "en" ? 568 : 592);
  try {
    const { presenter, spies } = createUiPresenter({
      providers: [providerAvailability("manual-card"), providerAvailability("voucher")],
      selectedMethod: "cash",
    });
    const screen = await render(<PaymentScreen locale={locale} presenter={presenter} showStatusStrip={false} />);
    expect(screen.queryByTestId("payment-method-square")).toBeNull();
    expect(screen.queryByTestId("payment-method-linkly-cloud")).toBeNull();
    await openPaymentEntry(screen, "manual-card");
    expect(StyleSheet.flatten(screen.getByTestId("payment-entry-pane").props.style)).toMatchObject({ height: "90%" });
    expect(screen.getByTestId("payment-entry-scroll")).not.toContainElement(screen.getByTestId("payment-submit"));
    expect(screen.getByTestId("payment-amount").props.editable).toBe(false);
    expect(screen.getByTestId("payment-amount").props.value).toBe("10.00");
    expect(screen.queryByTestId("payment-keypad")).toBeNull();
    expect(screen.getByTestId("payment-submit").props.accessibilityState.disabled).toBe(true);
    await fireEvent.press(screen.getByTestId("payment-submit"));
    expect(spies.submitSelected).not.toHaveBeenCalled();
    const checkbox = screen.getByTestId("payment-manual-card-check");
    expect(checkbox.props.accessibilityRole).toBe("checkbox");
    expect(StyleSheet.flatten(checkbox.props.style).minHeight).toBeGreaterThanOrEqual(PAYMENT_MIN_TOUCH_TARGET);
    await fireEvent.press(checkbox);
    expect(presenter.getState().manualCardConfirmed).toBe(true);
    expect(screen.getByTestId("payment-submit").props.accessibilityState.disabled).toBe(false);
    await fireEvent.press(screen.getByTestId("payment-submit"));
    expect(spies.submitSelected).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("payment-entry-modal")).toBeNull();
    await screen.unmount();
  } finally {
    setPaymentWindowSize(390, 844);
  }
});

test("手动刷卡关闭时隐藏入口且不把关闭开关当作配置错误", async () => {
  const { presenter } = createUiPresenter({
    providers: [providerAvailability("square"), {
      provider: "manual-card", available: false, blocker: "MANUAL_CARD_CONFIGURATION_DISABLED",
    }],
  });
  const screen = await render(<PaymentScreen locale="zh" presenter={presenter} showStatusStrip={false} />);
  expect(screen.getByTestId("payment-method-square")).toBeTruthy();
  expect(screen.queryByTestId("payment-method-manual-card")).toBeNull();
  expect(screen.queryByTestId("payment-provider-blockers")).toBeNull();
  await screen.unmount();
});

test("手动刷卡退出重进和剩余金额改变必须重新勾选", async () => {
  const harness = createUiPresenter({ providers: [providerAvailability("manual-card")], selectedMethod: "cash" });
  const screen = await render(<PaymentScreen locale="zh" presenter={harness.presenter} showStatusStrip={false} />);
  await openPaymentEntry(screen, "manual-card");
  await fireEvent.press(screen.getByTestId("payment-manual-card-check"));
  await fireEvent.press(screen.getByTestId("payment-entry-cancel"));
  await openPaymentEntry(screen, "manual-card");
  expect(harness.presenter.getState().manualCardConfirmed).toBe(false);
  await fireEvent.press(screen.getByTestId("payment-manual-card-check"));
  await act(async () => harness.publish({ ...harness.presenter.getState(), remaining: aud(650) }));
  expect(harness.presenter.getState().manualCardConfirmed).toBe(false);
  expect(screen.getByTestId("payment-amount").props.value).toBe("6.50");
  expect(screen.getByTestId("payment-submit").props.accessibilityState.disabled).toBe(true);
  await screen.unmount();
});

test("分期流程隐藏手动刷卡入口", async () => {
  const harness = createUiPresenter({ providers: [providerAvailability("manual-card")] });
  harness.publish({ ...harness.presenter.getState(), checkout: { ...harness.presenter.getState().checkout, flow: "installment-repayment" } });
  const screen = await render(<PaymentScreen locale="zh" presenter={harness.presenter} showStatusStrip={false} />);
  expect(screen.queryByTestId("payment-method-manual-card")).toBeNull();
  await screen.unmount();
});

function createUiPresenter(
  override: Partial<PaymentPresenterState> = {},
  applySubmittedState?: (
    state: PaymentPresenterState,
  ) => PaymentPresenterState,
) {
  let state: PaymentPresenterState = {
    phase: "ready",
    busy: false,
    recoveryInFlight: false,
    initialized: true,
    providers: [
      providerAvailability("square"),
      providerAvailability("linkly-cloud"),
      providerAvailability("voucher"),
    ],
    // 此夹具代表已通过 Payment.TakeCash 权限校验的收银员。
    cashAvailable: true,
    selectedMethod: "square",
    amountText: "10.00",
    voucherCaptured: false,
    sensitiveInputRevision: 0,
    fieldIssue: null,
    runtimeErrorCode: null,
    orderGuid: null,
    total: aud(1_000),
    remaining: aud(1_000),
    tenders: [],
    attemptId: null,
    attemptCreatedAtIso: null,
    provider: null,
    runtimeStatus: null,
    allowedActions: actions({
      start: true,
      changeProvider: true,
      cancel: true,
      addCash: true,
    }),
    tenderReversalRecovery: null,
    checkout: {
      flow: "regular",
      lines: [],
      installmentCustomer: null,
      cash: {
        tenderedCents: 0,
        appliedCents: 0,
        changeCents: 0,
      },
      canConfirm: false,
      fullInstallmentConfirmationRequired: false,
    },
    linkly: {
      status: null,
      errorCode: null,
      allowedKeys: [],
    },
    ...override,
  };
  const listeners = new Set<() => void>();
  const publish = (next: PaymentPresenterState) => {
    state = next;
    listeners.forEach((listener) => listener());
  };
  const patchCustomer = (
    update: (
      customer: NonNullable<
        PaymentPresenterState["checkout"]["installmentCustomer"]
      >,
    ) => NonNullable<
      PaymentPresenterState["checkout"]["installmentCustomer"]
    >,
  ) => {
    const customer = state.checkout.installmentCustomer;
    if (!customer) return;
    publish({
      ...state,
      checkout: {
        ...state.checkout,
        installmentCustomer: update(customer),
      },
    });
  };
  const spies = {
    selectMethod: jest.fn((method: PaymentUiMethod) => {
      publish({ ...state, selectedMethod: method, manualCardConfirmed: false });
      return true;
    }),
    selectLinklyTerminal: jest.fn(async (terminalId: string) => {
      const resource = state.linklyTerminals;
      if (!resource?.snapshot) return false;
      publish({
        ...state,
        linklyTerminals: {
          ...resource,
          snapshot: {
            ...resource.snapshot,
            selectedTerminalId: terminalId,
            selectionRevision: resource.snapshot.selectionRevision + 1,
          },
        },
      });
      return true;
    }),
    setAmountText: jest.fn((value: string) => {
      publish({ ...state, amountText: value });
    }),
    setVoucherCode: jest.fn((_value: string) => undefined),
    setManualCardConfirmed: jest.fn((confirmed: boolean) => {
      publish({ ...state, manualCardConfirmed: confirmed });
    }),
    dismissError: jest.fn(() => undefined),
    submitSelected: jest.fn(async () => {
      if (applySubmittedState) publish(applySubmittedState(state));
      return true;
    }),
    recover: jest.fn(async (_options?: PaymentRecoverOptions) => true),
    cancel: jest.fn(async () => true),
    removeTender: jest.fn(async (_tenderGuid: string) => true),
    sendLinklyKey: jest.fn(async (_key: LinklySafeOperatorKey) => true),
    markLinklyReceiptPrinted: jest.fn(async () => true),
    acknowledgeLinkly: jest.fn(async () => true),
    confirm: jest.fn(async (_options?: PaymentConfirmOptions) => true),
    openInstallmentCustomerEditor: jest.fn(() => {
      patchCustomer((customer) => ({
        ...customer,
        editorOpen: customer.editable,
      }));
    }),
    setInstallmentCustomerDraftName: jest.fn((value: string) => {
      patchCustomer((customer) => ({ ...customer, draftName: value }));
    }),
    setInstallmentCustomerDraftPhone: jest.fn((value: string) => {
      patchCustomer((customer) => ({ ...customer, draftPhone: value }));
    }),
    saveInstallmentCustomer: jest.fn(() => {
      patchCustomer((customer) => ({
        ...customer,
        name: customer.draftName,
        phone: customer.draftPhone,
        editorOpen: false,
      }));
    }),
    cancelInstallmentCustomerEditor: jest.fn(() => {
      patchCustomer((customer) => ({ ...customer, editorOpen: false }));
    }),
  };
  const presenter: PaymentScreenPresenter = {
    getState: () => state,
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    initialize: async () => true,
    destroy: () => listeners.clear(),
    ...spies,
  };
  return { presenter, publish, spies };
}

function linklyTerminal(
  terminalId: string,
  laneNo: number,
  displayName: string,
) {
  return {
    terminalId,
    laneNo,
    displayName,
    pairingState: "Ready" as const,
    isBusy: false,
    isReady: true,
    lastHealthStatus: "Ready",
    lastHealthAt: null,
  };
}

class ScreenPaymentRuntime implements PaymentCheckoutRuntimePort {
  public recovery: PaymentCheckoutPublicSnapshot | null = null;
  public readonly startCalls: unknown[] = [];
  public readonly recoverInputs: unknown[] = [];
  public readonly removeInputs: unknown[] = [];
  public startImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "pending" });
  public recoverImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "pending" });
  public removeImpl: () => Promise<PaymentCheckoutPublicSnapshot> =
    async () => snapshot({ status: "partial" });

  public listProviderAvailability(): readonly PaymentProviderAvailability[] {
    return [
      providerAvailability("square"),
      providerAvailability("linkly-cloud"),
      providerAvailability("voucher"),
    ];
  }

  public async read(): Promise<PaymentCheckoutPublicSnapshot> {
    return snapshot();
  }

  public async findRecoveryRequired(): Promise<PaymentCheckoutPublicSnapshot | null> {
    return this.recovery;
  }

  public async resumeCurrent(): Promise<PaymentCheckoutPublicSnapshot | null> {
    return snapshot({ status: "pending" });
  }

  public start(
    input: Parameters<PaymentCheckoutRuntimePort["start"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.startCalls.push(input);
    return this.startImpl();
  }

  public recover(
    input: Parameters<PaymentCheckoutRuntimePort["recover"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.recoverInputs.push(input);
    return this.recoverImpl();
  }

  public async cancel(): Promise<PaymentCheckoutPublicSnapshot> {
    return snapshot({ status: "cancelled" });
  }

  public async abandonPrepared(): Promise<PaymentCheckoutPublicSnapshot> {
    return snapshot({ status: "cancelled" });
  }

  public async addCash(): Promise<PaymentCheckoutPublicSnapshot> {
    return snapshot({ status: "partial" });
  }

  public removeTender(
    input: Parameters<PaymentCheckoutRuntimePort["removeTender"]>[0],
  ): Promise<PaymentCheckoutPublicSnapshot> {
    this.removeInputs.push(input);
    return this.removeImpl();
  }
}

class ScreenLinklyOperator implements LinklyOperatorRuntimePort {
  public readonly sendCalls: {
    attemptId: string;
    key: LinklySafeOperatorKey;
  }[] = [];
  public sendImpl: (
    attemptId: string,
  ) => Promise<LinklyOperatorPublicResult> = async (attemptId) => ({
    attemptId,
    status: "in-progress",
    errorCode: null,
    allowedKeys: ["yes"],
  });

  public sendKey(input: {
    attemptId: string;
    key: LinklySafeOperatorKey;
  }): Promise<LinklyOperatorPublicResult> {
    this.sendCalls.push(input);
    return this.sendImpl(input.attemptId);
  }

  public async read(input: { attemptId: string; signal?: AbortSignal; deadlineAtMs?: number }): Promise<LinklyOperatorPublicResult> {
    return {
      attemptId: input.attemptId,
      status: "in-progress",
      errorCode: null,
      allowedKeys: ["yes"],
      interaction: {
        displayText: "PRESENT CARD",
        displayLines: ["TAP CARD"],
        inputType: null,
        graphicCode: null,
        recoveryAction: null,
      },
    };
  }

  public async markReceiptPrinted(
    attemptId: string,
  ): Promise<LinklyOperatorPublicResult> {
    return {
      attemptId,
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    };
  }

  public async acknowledge(
    attemptId: string,
  ): Promise<LinklyOperatorPublicResult> {
    return {
      attemptId,
      status: "completed",
      errorCode: null,
      allowedKeys: [],
    };
  }
}

function screenPresenter(
  runtime: ScreenPaymentRuntime,
  linklyOperator?: LinklyOperatorRuntimePort,
): PaymentPresenter {
  let action = 0;
  return new PaymentPresenter({
    runtime,
    ...(linklyOperator ? { linklyOperator } : {}),
    entry: {
      checkoutIntentId: "checkout-ui-1",
      expectedCartRevision: 3,
      total: aud(1_000),
    },
    createActionId: () => `ui-action-${++action}`,
  });
}

function snapshot(
  override: Partial<PaymentCheckoutPublicSnapshot> = {},
): PaymentCheckoutPublicSnapshot {
  return {
    orderGuid: "order-ui-1",
    total: aud(1_000),
    remaining: aud(1_000),
    tenders: [],
    attemptId: null,
    attemptCreatedAtIso: null,
    provider: null,
    status: "draft-prepared",
    errorCode: null,
    allowedActions: actions({
      start: true,
      changeProvider: true,
      cancel: true,
      addCash: true,
    }),
    ...override,
  };
}

function actions(
  override: Partial<PaymentCheckoutPublicSnapshot["allowedActions"]> = {},
): PaymentCheckoutPublicSnapshot["allowedActions"] {
  return {
    start: false,
    changeProvider: false,
    recover: false,
    cancel: false,
    addCash: false,
    removeTender: false,
    ...override,
  };
}

function providerAvailability(
  provider: PaymentProvider,
): PaymentProviderAvailability {
  return {
    provider,
    available: true,
    blocker: null,
  };
}

function aud(cents: number): Money {
  return { currency: "AUD", cents };
}

function setPaymentWindowSize(width: number, height: number): void {
  const metrics = {
    width,
    height,
    scale: 2,
    fontScale: 1,
  };
  Dimensions.set({ window: metrics, screen: metrics });
}

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, reject, resolve };
}


test.each(["zh", "en"] as const)("Linkly 双 OK/Cancel 旗标仅显示一个合并按键（%s）", async (locale) => {
  const { presenter, spies } = createUiPresenter({
    phase: "pending", provider: "linkly-cloud", selectedMethod: "linkly-cloud",
    orderGuid: "order-key-zero", attemptId: "attempt-key-zero", runtimeStatus: "pending",
    linkly: { status: "in-progress", errorCode: null, allowedKeys: ["ok", "cancel"] },
  });
  const screen = await render(<PaymentScreen locale={locale} presenter={presenter} showStatusStrip={false} />);
  expect(screen.queryByTestId("payment-linkly-cancel")).toBeNull();
  expect(screen.getByTestId("payment-linkly-ok")).toHaveTextContent(locale === "zh" ? "确认 / 取消" : "OK / Cancel");
  await fireEvent.press(screen.getByTestId("payment-linkly-ok"));
  expect(spies.sendLinklyKey).toHaveBeenCalledTimes(1);
  expect(spies.sendLinklyKey).toHaveBeenCalledWith("ok");
});


test("新收款被活动退货拦截后，显示原退货恢复提示并允许返回", async () => {
  const onBack = jest.fn();
  const { presenter } = createUiPresenter({
    phase: "submitting", busy: false, orderGuid: null, attemptId: null,
    runtimeErrorCode: "RETURN_RECOVERY_REQUIRED",
  });
  const screen = await render(<PaymentScreen locale="zh" onBack={onBack} presenter={presenter} showStatusStrip={false} />);
  expect(within(screen.getByTestId("payment-runtime-error")).getByText("请返回退货页面恢复原退货，再开始新的收款。")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("payment-back"));
  expect(onBack).toHaveBeenCalledTimes(1);
});
