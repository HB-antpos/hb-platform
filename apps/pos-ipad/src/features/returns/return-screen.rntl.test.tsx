import { expect, jest, test } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import type {
  NoReceiptReturnItem,
  ReceiptReturnContext,
} from "@hb/pos-domain/features/returns/return-domain";
import { ReturnPresenter } from "@hb/pos-domain/features/returns/return-presenter";
import {
  RETURN_MIN_TOUCH_TARGET,
  ReturnScreen,
  parsePositiveAudCents,
} from "./return-screen";
import {
  ReturnWorkflow,
  type ReturnExecutionCommand,
  type ReturnExecutionPort,
} from "@hb/pos-domain/features/returns/return-workflow";

import {
  PosNavigationContext,
  type PosNavigationContextValue,
} from "@/ui/shell/pos-navigation-context";

// 页面外壳的顶栏与侧栏通过 t() 取全局文案，这里原样返回键名即可。
jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
    t: (key: string) => key,
  }),
}));

test("小票退货覆盖查询、数量、容量选择、Unknown 恢复和成功页", async () => {
  expect(RETURN_MIN_TOUCH_TARGET).toBe(44);
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution);
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );
  const keyboardScroll = screen.getByTestId("return-editor-keyboard-scroll");
  expect(keyboardScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
  expect(keyboardScroll.props.keyboardDismissMode).toBe("interactive");
  expect(keyboardScroll.props.keyboardShouldPersistTaps).toBe("handled");

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );

  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );
  expect(
    screen.getByTestId("return-confirm").props.accessibilityState.disabled,
  ).toBe(false);
  await fireEvent.press(screen.getByTestId("return-method-card"));
  await fireEvent.press(screen.getByTestId("return-confirm"));

  await waitFor(() =>
    expect(screen.getByTestId("return-unknown")).toBeTruthy(),
  );
  expect(screen.queryByTestId("return-confirm")).toBeNull();
  expect(screen.queryByTestId("return-method-cash")).toBeNull();
  expect(execution.executeCalls).toHaveLength(1);
  expect(execution.executeCalls[0]?.plan.allocations[0]?.method).toBe("card");

  await fireEvent.press(screen.getByTestId("return-unknown-action"));
  await waitFor(() =>
    expect(screen.getByTestId("return-success")).toBeTruthy(),
  );
  expect(execution.recoverCalls).toBe(1);
});

test("路由提供 orderRef 时只自动执行一次既有小票查询", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution);
  const loadReceipt = jest.spyOn(presenter, "loadReceipt");
  const screen = await render(
    <ReturnScreen
      initialReceiptQuery="10000000-0000-4000-8000-000000000001"
      locale="en"
      presenter={presenter}
    />,
  );

  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  expect(loadReceipt).toHaveBeenCalledTimes(1);
  expect(loadReceipt).toHaveBeenCalledWith(
    "10000000-0000-4000-8000-000000000001",
  );
});

test("无小票 OPENITEM 走在线主管路径并提供中英文一致的 44pt 操作", async () => {
  const execution = new ScreenExecution();
  let authorizationCalls = 0;
  const presenter = createScreenPresenter(execution, {
    authorize: async () => {
      authorizationCalls += 1;
      return { authorizationKey: "supervisor-grant-screen" };
    },
  });
  const screen = await render(
    <ReturnScreen locale="zh" presenter={presenter} />,
  );

  await fireEvent.press(screen.getByTestId("return-mode-no-receipt"));
  await fireEvent.changeText(
    screen.getByTestId("return-open-item-name"),
    "散装商品",
  );
  await fireEvent.changeText(
    screen.getByTestId("return-open-item-amount"),
    "4.50",
  );
  await fireEvent.press(screen.getByTestId("return-open-item-add"));

  await waitFor(() =>
    expect(screen.getByText("散装商品")).toBeTruthy(),
  );
  expect(authorizationCalls).toBe(1);
  expect(screen.getByText("数量 1")).toBeTruthy();
  expect(
    screen.getByTestId("return-confirm").props.accessibilityState.disabled,
  ).toBe(false);
  expect(screen.queryByTestId("return-method-installment")).toBeNull();
  await fireEvent.press(screen.getByTestId("return-method-voucher"));
  expect(presenter.getState().preferredMethod).toBe("voucher");
});

test("OPENITEM 金额只接受正数且最多两位小数", () => {
  expect(parsePositiveAudCents("4.5")).toBe(450);
  expect(parsePositiveAudCents("4.50")).toBe(450);
  expect(parsePositiveAudCents("0")).toBeNull();
  expect(parsePositiveAudCents("-1")).toBeNull();
  expect(parsePositiveAudCents("1.234")).toBeNull();
});

test("支付边界等待页隐藏确认和退款方式，避免重复操作", async () => {
  const pending = deferred<{
    status: "completed";
    returnOrderGuid: string;
  }>();
  const execution = new ScreenExecution();
  execution.executeImpl = async () => pending.promise;
  const presenter = createScreenPresenter(execution);
  await presenter.loadReceipt("HB-1001");
  presenter.incrementLine("return-line-1");
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(screen.getByTestId("return-waiting")).toBeTruthy();
  expect(screen.queryByTestId("return-confirm")).toBeNull();
  expect(screen.queryByTestId("return-method-card")).toBeNull();

  pending.resolve({
    status: "completed",
    returnOrderGuid: "return-order-waiting",
  });
  await waitFor(() =>
    expect(screen.getByTestId("return-success")).toBeTruthy(),
  );
  expect(execution.executeCalls).toHaveLength(1);
});

test("刷卡订单退款方式开放现金/代金券代替，默认仍按原支付方式退回", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: {
      ...screenReceiptContext(),
      tenderCapacities: [
        {
          capacityId: "card-capacity",
          originalOrderGuid: "order-a",
          method: "card",
          remainingCents: 2_000,
          offlineCashProof: null,
        },
      ],
    },
  });
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );

  // 代替选项：刷卡订单上同时出现现金、礼券按钮。
  expect(screen.getByTestId("return-method-cash")).toBeTruthy();
  expect(screen.getByTestId("return-method-card")).toBeTruthy();
  expect(screen.getByTestId("return-method-voucher")).toBeTruthy();
  // 默认未选择时显示按原支付方式退回的提示。
  expect(screen.getByText(/default to the original tender/i)).toBeTruthy();

  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );
  // 不点选代替方式：仍按原支付方式（银行卡）退回。
  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(execution.executeCalls).toHaveLength(1);
  expect(
    execution.executeCalls[0]?.plan.allocations[0]?.method,
  ).toBe("card");
  expect(
    execution.executeCalls[0]?.plan.allocations[0]?.originalCapacityId,
  ).toBe("card-capacity");
});

test("手工刷卡订单现金/礼券兜底：不显示原卡退回，默认现金且绑定原卡额度", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: {
      ...screenReceiptContext(),
      tenderCapacities: [
        {
          capacityId: "manual-card-capacity",
          originalOrderGuid: "order-a",
          method: "card",
          remainingCents: 2_000,
          offlineCashProof: null,
          substituteOnly: true,
        },
      ],
    },
  });
  const screen = await render(
    <ReturnScreen locale="zh" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  // 查单成功后默认现金兜底，而不是按原支付方式（手工刷卡无法原路退回）。
  expect(presenter.getState().preferredMethod).toBe("cash");
  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );

  expect(screen.getByTestId("return-method-cash")).toBeTruthy();
  expect(screen.queryByTestId("return-method-card")).toBeNull();
  expect(screen.getByTestId("return-method-voucher")).toBeTruthy();
  expect(
    screen.getByTestId("return-capacity-card").props.children,
  ).toContain("仅可现金或礼券退款");

  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(execution.executeCalls).toHaveLength(1);
  expect(
    execution.executeCalls[0]?.plan.allocations.map((allocation) => [
      allocation.method,
      allocation.originalCapacityId,
    ]),
  ).toEqual([["cash", "manual-card-capacity"]]);
});

test("刷卡订单选现金代替：整单按现金退款且仍绑定原卡容量", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: {
      ...screenReceiptContext(),
      tenderCapacities: [
        {
          capacityId: "card-capacity",
          originalOrderGuid: "order-a",
          method: "card",
          remainingCents: 2_000,
          offlineCashProof: null,
        },
      ],
    },
  });
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );

  // 选择现金代替刷卡退款。
  await fireEvent.press(screen.getByTestId("return-method-cash"));
  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(execution.executeCalls).toHaveLength(1);
  expect(
    execution.executeCalls[0]?.plan.allocations[0]?.method,
  ).toBe("cash");
  // 代替退款仍绑定原卡容量，防止超额退款。
  expect(
    execution.executeCalls[0]?.plan.allocations[0]?.originalCapacityId,
  ).toBe("card-capacity");
});

test("代金券买的商品：提示至少须退代金券的金额，计划先退代金券其余按原支付", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: voucherFundedReceiptContext(),
  });
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );

  // 退 $10.00 × 代金券占比 7/20 = $3.50 须退代金券。
  expect(presenter.getState().requiredVoucherRefundCents).toBe(350);
  expect(
    screen.getByTestId("return-voucher-funded-notice").props.children,
  ).toMatch(/cannot be refunded as cash or card: at least .*3\.50 must be refunded as a voucher/u);

  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(execution.executeCalls).toHaveLength(1);
  expect(
    execution.executeCalls[0]?.plan.allocations.map((allocation) => [
      allocation.method,
      allocation.signedAmountCents,
      allocation.originalCapacityId,
    ]),
  ).toEqual([
    ["voucher", -350, "voucher-capacity"],
    ["card", -650, "card-capacity"],
  ]);
});

test("代金券买的商品离线时说明退款券须联网签发；中文提示与英文对应", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: voucherFundedReceiptContext(),
    online: false,
  });
  const screen = await render(
    <ReturnScreen locale="zh" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );

  expect(
    screen.getByTestId("return-voucher-funded-notice").props.children,
  ).toMatch(/^代金券买的商品不能退现金或退卡：至少 .*3\.50 须退代金券。$/u);
  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(execution.executeCalls).toHaveLength(0);
  await waitFor(() =>
    expect(screen.getByText("代金券买的商品须退代金券，退款券只能联网签发，请联网后再退。")).toBeTruthy(),
  );
});

test("原单无代金券付款时不显示代金券提示", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: {
      ...screenReceiptContext(),
      voucherFundedBasis: { voucherOriginalCents: 0, paidOriginalCents: 2_000 },
    },
  });
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );
  await fireEvent.press(
    screen.getByTestId("return-increase-return-line-1"),
  );

  expect(presenter.getState().requiredVoucherRefundCents).toBe(0);
  expect(screen.queryByTestId("return-voucher-funded-notice")).toBeNull();
});

test("新布局：模式切换在页头分段、商品行与 −/＋、退款方式大块、带金额的确认键", async () => {
  const execution = new ScreenExecution();
  const presenter = createScreenPresenter(execution, {
    receiptContext: {
      ...screenReceiptContext(),
      tenderCapacities: [
        {
          capacityId: "card-capacity",
          originalOrderGuid: "order-a",
          method: "card",
          remainingCents: 2_000,
          offlineCashProof: null,
        },
      ],
    },
  });
  const beginNoReceipt = jest.spyOn(presenter, "beginNoReceipt");
  const reset = jest.spyOn(presenter, "reset");
  const screen = await render(
    <ReturnScreen locale="en" presenter={presenter} />,
  );

  // 模式切换是单选分段：当前模式 checked。
  expect(
    screen.getByTestId("return-mode-receipt").props.accessibilityState.checked,
  ).toBe(true);
  expect(
    screen.getByTestId("return-mode-no-receipt").props.accessibilityState
      .checked,
  ).toBe(false);
  // 未选商品时确认键不带金额且禁用。
  const confirm = screen.getByTestId("return-confirm");
  expect(confirm.props.accessibilityLabel).toBe("Confirm refund");
  expect(confirm.props.accessibilityState.disabled).toBe(true);
  expect(StyleSheet.flatten(confirm.props.style).minHeight).toBe(88);
  expect(
    StyleSheet.flatten(screen.getByTestId("return-order-search").props.style)
      .height,
  ).toBe(60);

  await fireEvent.changeText(
    screen.getByTestId("return-order-query"),
    "HB-1001",
  );
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() =>
    expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy(),
  );

  expect(
    StyleSheet.flatten(screen.getByTestId("return-row-return-line-1").props.style)
      .minHeight,
  ).toBeGreaterThanOrEqual(92);
  for (const id of ["return-decrease-return-line-1", "return-increase-return-line-1"]) {
    expect(StyleSheet.flatten(screen.getByTestId(id).props.style)).toMatchObject({
      height: 52,
      width: 52,
    });
  }
  expect(screen.getByTestId("return-quantity-return-line-1").props.children).toBe(
    "0 of 2",
  );
  // 一件都没选时不能再减。
  expect(
    screen.getByTestId("return-decrease-return-line-1").props.accessibilityState
      .disabled,
  ).toBe(true);

  // 退款方式是 68 高的大块单选；只有可原路退回的原支付方式带「原支付方式」标记。
  const card = screen.getByTestId("return-method-card");
  expect(StyleSheet.flatten(card.props.style).height).toBe(68);
  expect(card.props.accessibilityRole).toBe("radio");
  expect(card.props.accessibilityState.checked).toBe(false);
  expect(card.props.accessibilityLabel).toBe("Card · Original tender");
  expect(screen.getByTestId("return-method-cash").props.accessibilityLabel).toBe(
    "Cash",
  );
  expect(screen.getAllByText("Original tender")).toHaveLength(1);

  await fireEvent.press(screen.getByTestId("return-increase-return-line-1"));
  expect(screen.getByTestId("return-quantity-return-line-1").props.children).toBe(
    "1 of 2",
  );
  expect(screen.getByTestId("return-confirm").props.accessibilityLabel).toMatch(
    /^Confirm refund · \$10\.00$/u,
  );
  await fireEvent.press(screen.getByTestId("return-method-cash"));
  expect(
    screen.getByTestId("return-method-cash").props.accessibilityState.checked,
  ).toBe(true);

  // 切到无小票：三个输入与两个添加按钮按同一尺寸排，且无原支付标记。
  await fireEvent.press(screen.getByTestId("return-mode-no-receipt"));
  expect(beginNoReceipt).toHaveBeenCalledTimes(1);
  expect(screen.getByTestId("return-product-query")).toBeTruthy();
  expect(screen.getByTestId("return-open-item-name")).toBeTruthy();
  expect(screen.getByTestId("return-open-item-amount")).toBeTruthy();
  for (const id of ["return-product-add", "return-open-item-add"]) {
    expect(StyleSheet.flatten(screen.getByTestId(id).props.style).height).toBe(60);
  }
  expect(screen.queryByText("Original tender")).toBeNull();

  // 切回小票退货沿用原来的 reset。
  await fireEvent.press(screen.getByTestId("return-mode-receipt"));
  expect(reset).toHaveBeenCalledTimes(1);
  expect(screen.getByTestId("return-order-query")).toBeTruthy();

  // 与改版前的页签一致：再点一次当前模式 = 清空并重新开始这种退货。
  await fireEvent.press(screen.getByTestId("return-mode-receipt"));
  expect(reset).toHaveBeenCalledTimes(2);
  await fireEvent.press(screen.getByTestId("return-mode-no-receipt"));
  await fireEvent.press(screen.getByTestId("return-mode-no-receipt"));
  expect(beginNoReceipt).toHaveBeenCalledTimes(3);
});

test("退款进行中与结果未知时锁住侧栏跳转；选商品阶段侧栏可用", async () => {
  const pending = deferred<{ status: "unknown"; recoveryKey: string | null }>();
  const execution = new ScreenExecution();
  execution.executeImpl = async () => pending.promise;
  const presenter = createScreenPresenter(execution);
  await presenter.loadReceipt("HB-1001");
  presenter.incrementLine("return-line-1");
  const openSales = jest.fn();
  const navigation: PosNavigationContextValue = {
    appVersionLabel: null,
    language: "en",
    model: {
      entries: [{ id: "returns", label: "Returns", onPress: jest.fn() }],
      onOpenSales: openSales,
      pinned: ["returns"],
    },
    openSales,
    switchLanguage: jest.fn(),
  };
  const screen = await render(
    <PosNavigationContext.Provider value={navigation}>
      <ReturnScreen locale="en" presenter={presenter} />
    </PosNavigationContext.Provider>,
  );

  expect(screen.getByTestId("pos-rail-sales").props.accessibilityState.disabled).toBe(
    false,
  );
  expect(
    screen.getByTestId("pos-rail-returns").props.accessibilityState.selected,
  ).toBe(true);

  await fireEvent.press(screen.getByTestId("return-confirm"));
  expect(screen.getByTestId("return-waiting")).toBeTruthy();
  expect(screen.getByTestId("pos-rail-sales").props.accessibilityState.disabled).toBe(
    true,
  );
  expect(screen.getByTestId("pos-rail-more").props.accessibilityState.disabled).toBe(
    true,
  );
  await fireEvent.press(screen.getByTestId("pos-rail-sales"));
  expect(openSales).not.toHaveBeenCalled();

  pending.resolve({ status: "unknown", recoveryKey: "private-recovery-key" });
  await waitFor(() =>
    expect(screen.getByTestId("return-unknown")).toBeTruthy(),
  );
  expect(screen.getByTestId("pos-rail-sales").props.accessibilityState.disabled).toBe(
    true,
  );
  expect(
    StyleSheet.flatten(screen.getByTestId("return-unknown-action").props.style)
      .minHeight,
  ).toBe(72);
});

function voucherFundedReceiptContext(): ReceiptReturnContext {
  return {
    ...screenReceiptContext(),
    tenderCapacities: [
      {
        capacityId: "card-capacity",
        originalOrderGuid: "order-a",
        method: "card",
        remainingCents: 1_300,
        offlineCashProof: null,
      },
      {
        capacityId: "voucher-capacity",
        originalOrderGuid: "order-a",
        method: "voucher",
        remainingCents: 700,
        offlineCashProof: null,
      },
    ],
    voucherFundedBasis: { voucherOriginalCents: 700, paidOriginalCents: 2_000 },
  };
}

function createScreenPresenter(
  execution: ScreenExecution,
  options: Readonly<{
    authorize?(): Promise<{ authorizationKey: string }>;
    receiptContext?: ReceiptReturnContext;
    online?: boolean;
  }> = {},
): ReturnPresenter {
  const workflow = new ReturnWorkflow({
    lookup: {
      lookupReceipt: async () =>
        options.receiptContext ?? screenReceiptContext(),
      lookupNoReceiptProduct: async () => noReceiptItem(),
      createNoReceiptOpenItem: async (input) => ({
        ...noReceiptItem(),
        sourceKind: "no-receipt-open-item",
        selectionKey: "open-item-line",
        returnSourceKey: "noreceipt-open:BNE:1",
        lookupCode: "OPENITEM",
        displayName: input.displayName,
        unitRefundCents: input.unitRefundCents,
      }),
    },
    connectivity: { isOnline: async () => options.online ?? true },
    supervisorAuthorization: {
      authorizeNoReceiptReturn:
        options.authorize ??
        (async () => ({ authorizationKey: "supervisor-grant-default" })),
    },
    sessionGuard: {
      captureLease: () => "lease-1",
      assertActive: () => undefined,
    },
    execution,
    createActionId: () => "return-action-1",
  });
  return new ReturnPresenter(workflow);
}

class ScreenExecution implements ReturnExecutionPort {
  public readonly executeCalls: ReturnExecutionCommand[] = [];
  public recoverCalls = 0;
  public executeImpl: (
    command: ReturnExecutionCommand,
  ) => Promise<
    | { status: "completed"; returnOrderGuid: string }
    | { status: "unknown"; recoveryKey: string | null }
  > = async () => ({
    status: "unknown",
    recoveryKey: "private-recovery-key",
  });

  public async execute(
    command: ReturnExecutionCommand,
  ): Promise<
    | { status: "completed"; returnOrderGuid: string }
    | { status: "unknown"; recoveryKey: string | null }
  > {
    this.executeCalls.push(command);
    return this.executeImpl(command);
  }

  public async recover(): Promise<{
    status: "completed";
    returnOrderGuid: string;
  }> {
    this.recoverCalls += 1;
    return {
      status: "completed",
      returnOrderGuid: "return-order-1",
    };
  }
}

function screenReceiptContext(): ReceiptReturnContext {
  return {
    originalOrderGuid: "order-a",
    receiptLabel: "HB-1001",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [
      {
        selectionKey: "detail-a",
        originalOrderGuid: "order-a",
        originalOrderDetailGuid: "detail-a",
        returnSourceKey: "return:order-a:detail-a",
        productCode: "P-1",
        itemNumber: "1001",
        lookupCode: "1001",
        displayName: "Blue cup",
        availableQuantity: 2,
        unitRefundCents: 1_000,
        remainingAmountCents: 2_000,
        syncProvenance: {
          referenceCode: "RECEIPT-REF",
          priceSource: 0,
        },
      },
    ],
    tenderCapacities: [
      {
        capacityId: "cash-capacity",
        originalOrderGuid: "order-a",
        method: "cash",
        remainingCents: 2_000,
        offlineCashProof: {
          evidenceId: "cash-proof",
          capacityId: "cash-capacity",
          originalOrderGuid: "order-a",
          remainingCents: 2_000,
        },
      },
      {
        capacityId: "card-capacity",
        originalOrderGuid: "order-a",
        method: "card",
        remainingCents: 2_000,
        offlineCashProof: null,
      },
    ],
  };
}

function noReceiptItem(): NoReceiptReturnItem {
  return {
    sourceKind: "no-receipt-product",
    selectionKey: "no-receipt-line",
    returnSourceKey: "noreceipt:BNE:1",
    productCode: "P-2",
    itemNumber: "2002",
    lookupCode: "9320001",
    displayName: "No receipt product",
    unitRefundCents: 500,
    syncProvenance: {
      referenceCode: "CATALOG-REF",
      priceSource: 1,
    },
  };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}
