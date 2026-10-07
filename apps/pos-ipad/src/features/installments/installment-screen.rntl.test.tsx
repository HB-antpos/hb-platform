import { beforeEach, expect, jest, test } from "@jest/globals";
import {
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { Dimensions, StyleSheet } from "react-native";

import type { InstallmentDetails } from "./installment-models";
import type { InstallmentPresenterState } from "./installment-presenter";
import {
  INSTALLMENTS_MIN_TOUCH_TARGET,
  InstallmentScreen,
  InstallmentsUnavailableScreen,
  installmentLayoutForWidth,
  type InstallmentScreenPresenter,
} from "./installment-screen";

let mockLanguage = "en";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
  }),
}));

jest.mock("@react-native-community/datetimepicker");

beforeEach(() => {
  jest.clearAllMocks();
  mockLanguage = "en";
  setWindowWidth(1_024);
});

test("宽屏采用 43/57 双栏和 76pt 顶栏，主要触控区不少于 44pt", async () => {
  expect(installmentLayoutForWidth(1_024)).toEqual({
    compact: false,
    listFlex: 0.43,
    detailsFlex: 0.57,
    pagePadding: 14,
    workspaceGap: 12,
  });
  expect(installmentLayoutForWidth(899).compact).toBe(true);

  const onBack = jest.fn();
  const { presenter, spies } = createPresenter({
    createDraft: draft(),
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const screen = await render(
    <InstallmentScreen
      onBack={onBack}
      onStartCreate={() => true}
      onStartRepayment={() => true}
      presenter={presenter}
    />,
  );

  await waitFor(() => expect(spies.load).toHaveBeenCalledTimes(1));
  expect(screen.getByTestId("installments-list")).toBeTruthy();
  expect(screen.getByTestId("installments-details-pane")).toBeTruthy();
  expect(
    StyleSheet.flatten(screen.getByTestId("installments-history-pane").props.style)
      .flex,
  ).toBe(0.43);
  expect(
    StyleSheet.flatten(screen.getByTestId("installments-details-pane").props.style)
      .flex,
  ).toBe(0.57);
  const headerStyle = StyleSheet.flatten(
    screen.getByTestId("installments-header").props.style,
  );
  expect(headerStyle.minHeight).toBe(76);
  expect(headerStyle.maxHeight).toBe(80);

  for (const testID of [
    "installments-back",
    "installments-refresh",
    "installments-primary-action",
    "installments-search-submit",
  ]) {
    expect(StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight).toBe(
      INSTALLMENTS_MIN_TOUCH_TARGET,
    );
  }
  await fireEvent.press(screen.getByTestId("installments-back"));
  await fireEvent.press(screen.getByTestId("installments-refresh"));
  expect(onBack).toHaveBeenCalledTimes(1);
  expect(spies.load).toHaveBeenCalledTimes(2);
  await screen.unmount();
});

test("搜索显式提交，状态、日期和设备范围立即调用异步筛选并支持加载更多", async () => {
  const { presenter, spies } = createPresenter({
    hasMore: true,
    orders: [summary()],
  });
  const screen = await render(<InstallmentScreen presenter={presenter} />);
  await waitFor(() => expect(spies.load).toHaveBeenCalledTimes(1));
  spies.load.mockClear();

  await fireEvent.changeText(screen.getByTestId("installments-search"), "Bob");
  expect(spies.setSearchQuery).toHaveBeenCalledWith("Bob");
  expect(spies.load).not.toHaveBeenCalled();
  await fireEvent.press(screen.getByTestId("installments-search-submit"));
  expect(spies.load).toHaveBeenCalledTimes(1);

  await fireEvent.press(screen.getByTestId("installments-filter-Active"));
  await fireEvent.press(screen.getByTestId("installments-date-today"));
  await fireEvent.press(screen.getByTestId("installments-scope-device"));
  expect(spies.setStatusFilter).toHaveBeenCalledWith("Active");
  expect(spies.setDateFilter).toHaveBeenCalledWith({
    preset: "today",
    fromDate: null,
    toDate: null,
  });
  expect(spies.setDeviceScope).toHaveBeenCalledWith("device");

  await fireEvent.press(screen.getByTestId("installments-date-custom"));
  expect(screen.getByTestId("installments-custom-dates")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installments-date-apply"));
  expect(spies.setDateFilter).toHaveBeenLastCalledWith({
    preset: "custom",
    fromDate: null,
    toDate: null,
  });

  expect(screen.getByTestId("installments-result-count")).toHaveTextContent(
    "1 item",
  );
  await fireEvent.press(screen.getByTestId("installments-load-more"));
  expect(spies.loadMore).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("离线时完全隐藏历史和详情，只显示恢复连接与重试", async () => {
  const { presenter, spies } = createPresenter({
    details: details("Active"),
    online: false,
    orders: [summary()],
    selectedGuid: GUID,
  });
  const screen = await render(<InstallmentScreen presenter={presenter} />);

  expect(screen.getByTestId("installments-offline-state")).toBeTruthy();
  expect(screen.queryByTestId("installments-list")).toBeNull();
  expect(screen.queryByTestId("installments-details-pane")).toBeNull();
  expect(screen.queryByText("IP-0001")).toBeNull();
  expect(spies.load).not.toHaveBeenCalled();
  await fireEvent.press(screen.getByTestId("installments-offline-retry"));
  expect(spies.load).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("首次加载、首次空、筛选空、列表失败和权限失败均有独立状态", async () => {
  const cases = [
    [
      { kind: "loading" as const },
      "installments-history-loading",
    ],
    [
      { kind: "ready" as const },
      "installments-history-empty-initial",
    ],
    [
      { kind: "ready" as const, statusFilter: "Active" as const },
      "installments-history-empty-filtered",
    ],
    [
      { kind: "failed" as const, statusCode: "history-failed" as const },
      "installments-history-failed",
    ],
    [
      { kind: "unauthorized" as const },
      "installments-history-unauthorized",
    ],
  ] as const;

  for (const [overrides, testID] of cases) {
    const { presenter } = createPresenter(overrides);
    const screen = await render(<InstallmentScreen presenter={presenter} />);
    expect(screen.getByTestId(testID)).toBeTruthy();
    await screen.unmount();
  }

  const failed = createPresenter({
    kind: "failed",
    statusCode: "service-unavailable",
  });
  const failedScreen = await render(
    <InstallmentScreen presenter={failed.presenter} />,
  );
  await waitFor(() => expect(failed.spies.load).toHaveBeenCalled());
  failed.spies.load.mockClear();
  await fireEvent.press(
    failedScreen.getByTestId("installments-history-failed-action"),
  );
  expect(failed.spies.load).toHaveBeenCalledTimes(1);
  expect(
    failedScreen.getByTestId("installments-status-service-unavailable"),
  ).toBeTruthy();
  await failedScreen.unmount();
});

test("详情加载和服务失败可重试，键盘避让合同保持启用", async () => {
  const loading = createPresenter({
    detailsLoading: true,
    orders: [summary()],
    selectedGuid: GUID,
  });
  const loadingScreen = await render(
    <InstallmentScreen presenter={loading.presenter} />,
  );
  expect(
    loadingScreen.getByTestId("installments-details-loading"),
  ).toBeTruthy();
  expect(
    loadingScreen.getByTestId("installment-history-search-keyboard-scroll")
      .props.automaticallyAdjustKeyboardInsets,
  ).toBe(true);
  await loadingScreen.unmount();

  const failed = createPresenter({
    orders: [summary()],
    selectedGuid: GUID,
    statusCode: "details-failed",
  });
  const failedScreen = await render(
    <InstallmentScreen presenter={failed.presenter} />,
  );
  expect(failedScreen.getByTestId("installments-details-failed")).toBeTruthy();
  await fireEvent.press(
    failedScreen.getByTestId("installments-details-failed-action"),
  );
  expect(failed.spies.retryDetails).toHaveBeenCalledTimes(1);
  await failedScreen.unmount();

  const ready = createPresenter({
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const readyScreen = await render(
    <InstallmentScreen
      onStartRepayment={() => true}
      presenter={ready.presenter}
    />,
  );
  const detailsScroll = readyScreen.getByTestId("installment-details");
  expect(detailsScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
  expect(detailsScroll.props.keyboardDismissMode).toBe("interactive");
  expect(detailsScroll.props.keyboardShouldPersistTaps).toBe("handled");
  await readyScreen.unmount();
});

test("详情只呈现业务白名单字段，不显示付款 reference、授权码或 token", async () => {
  const { presenter } = createPresenter({
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const screen = await render(
    <InstallmentScreen onStartRepayment={() => true} presenter={presenter} />,
  );

  for (const text of [
    "P1",
    "930001",
    "R1",
    "I1",
    "$100.00",
    "$5.00",
    "$95.00",
    "CASHIER-2",
    "IPAD-2",
    "Visa · •••• 4242",
    "Friday",
  ]) {
    expect(screen.getAllByText(text).length).toBeGreaterThan(0);
  }
  expect(screen.queryByText("PAYMENT-REF-SECRET")).toBeNull();
  expect(screen.queryByText("AUTH-SECRET")).toBeNull();
  expect(screen.queryByText("TOKEN-SECRET")).toBeNull();
  await screen.unmount();
});

test("Active 主操作走统一支付，失败明确提示；退款取消和作废收进更多操作并二次确认", async () => {
  const onStartRepayment = jest.fn((_installmentGuid: string) => false);
  const { presenter, spies } = createPresenter({
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const screen = await render(
    <InstallmentScreen
      onStartRepayment={onStartRepayment}
      presenter={presenter}
    />,
  );

  expect(screen.queryByTestId("installment-cancel-reason")).toBeNull();
  await fireEvent.press(screen.getByTestId("installment-continue-to-payment"));
  expect(onStartRepayment).toHaveBeenCalledWith(GUID);
  expect(screen.getByTestId("installments-navigation-failed")).toBeTruthy();

  await fireEvent.press(screen.getByTestId("installment-more-actions"));
  await fireEvent.press(screen.getByTestId("installment-more-cancel"));
  expect(screen.getByTestId("installment-cancel-reason")).toBeTruthy();
  expect(screen.queryByTestId("installment-void-reason")).toBeNull();
  await fireEvent.changeText(
    screen.getByTestId("installment-cancel-reason"),
    "Customer request",
  );
  await fireEvent.press(screen.getByTestId("installment-cancel-refund"));
  expect(spies.cancelWithRefund).not.toHaveBeenCalled();
  expect(screen.getByTestId("installment-confirm-cancel")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installment-confirm-operation-submit"));
  expect(spies.setCancelReason).toHaveBeenCalledWith("Customer request");
  expect(spies.cancelWithRefund).toHaveBeenCalledTimes(1);

  await fireEvent.press(screen.getByTestId("installment-danger-back"));
  await fireEvent.press(screen.getByTestId("installment-more-void"));
  expect(screen.getByTestId("installment-void-reason")).toBeTruthy();
  expect(screen.queryByTestId("installment-cancel-reason")).toBeNull();
  await fireEvent.press(screen.getByTestId("installment-void"));
  await fireEvent.press(screen.getByTestId("installment-confirm-operation-submit"));
  expect(spies.voidSelected).toHaveBeenCalledTimes(1);
  await screen.unmount();
}, 15_000);

test("PaidOff 取货要求二次确认；无权限和恢复状态显示原因并禁用", async () => {
  const denied = createPresenter({
    access: {
      canAddRepayment: true,
      canCancel: true,
      canConfirmPickup: false,
      canCreate: true,
      canView: true,
    },
    details: details("PaidOff"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const deniedScreen = await render(
    <InstallmentScreen presenter={denied.presenter} />,
  );
  expect(
    deniedScreen.getByTestId("installment-confirm-pickup").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    deniedScreen.getByTestId("installment-action-blocked-permission"),
  ).toBeTruthy();
  await deniedScreen.unmount();

  const allowed = createPresenter({
    details: details("PaidOff"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const allowedScreen = await render(
    <InstallmentScreen presenter={allowed.presenter} />,
  );
  await fireEvent.changeText(
    allowedScreen.getByTestId("installment-pickup-note"),
    "ID checked",
  );
  await fireEvent.press(allowedScreen.getByTestId("installment-confirm-pickup"));
  expect(allowed.spies.confirmPickup).not.toHaveBeenCalled();
  await fireEvent.press(
    allowedScreen.getByTestId("installment-confirm-operation-submit"),
  );
  expect(allowed.spies.confirmPickup).toHaveBeenCalledTimes(1);
  await allowedScreen.unmount();

  const recovery = createPresenter({
    details: details("Active"),
    orders: [summary()],
    recoveryRequired: true,
    selectedGuid: GUID,
    statusCode: "payment-recovery-required",
  });
  const recoveryScreen = await render(
    <InstallmentScreen
      onStartRepayment={() => true}
      presenter={recovery.presenter}
    />,
  );
  expect(
    recoveryScreen.getByTestId("installment-continue-to-payment").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    recoveryScreen.getByTestId("installment-action-blocked-recovery"),
  ).toBeTruthy();
  await fireEvent.press(
    recoveryScreen.getByTestId("installments-recover-blocking-action"),
  );
  expect(recovery.spies.recoverBlocking).toHaveBeenCalledTimes(1);
  await recoveryScreen.unmount();
});

test("PaidOff 可在更多操作里退款取消（不提供作废），并选择原路退或全部退代金券", async () => {
  const { presenter, spies } = createPresenter(
    {
      details: details("PaidOff"),
      orders: [summary()],
      selectedGuid: GUID,
    },
    false,
    true,
    false,
    { selectedDetailsVoidable: false },
  );
  const screen = await render(<InstallmentScreen presenter={presenter} />);

  expect(screen.getByTestId("installment-confirm-pickup")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installment-more-actions"));
  expect(screen.getByTestId("installment-more-cancel")).toBeTruthy();
  expect(screen.queryByTestId("installment-more-void")).toBeNull();
  await fireEvent.press(screen.getByTestId("installment-more-cancel"));
  expect(screen.getByTestId("installment-cancel-refund-mode")).toBeTruthy();
  expect(
    screen.getByTestId("installment-cancel-refund-mode-original-route").props
      .accessibilityState.selected,
  ).toBe(true);
  await fireEvent.press(
    screen.getByTestId("installment-cancel-refund-mode-voucher"),
  );
  expect(spies.setCancelRefundMode).toHaveBeenCalledWith("voucher");
  await fireEvent.press(screen.getByTestId("installment-cancel-refund"));
  expect(screen.getByTestId("installment-confirm-cancel")).toBeTruthy();
  expect(
    screen.getByText(/including a fully paid balance, will be refunded through the original route/u),
  ).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installment-confirm-operation-submit"));
  expect(spies.cancelWithRefund).toHaveBeenCalledTimes(1);
  await screen.unmount();
}, 15_000);

test("含刷卡原付款时原路退不可选，提示原因并以退代金券确认", async () => {
  const { presenter, spies } = createPresenter(
    {
      details: details("Active"),
      orders: [summary()],
      selectedGuid: GUID,
    },
    false,
    true,
    true,
    {
      selectedDetailsCancelRefundMode: "voucher",
      selectedDetailsOriginalRouteRefundable: false,
    },
  );
  const screen = await render(
    <InstallmentScreen onStartRepayment={() => true} presenter={presenter} />,
  );

  await fireEvent.press(screen.getByTestId("installment-more-actions"));
  await fireEvent.press(screen.getByTestId("installment-more-cancel"));
  const originalRoute = screen.getByTestId(
    "installment-cancel-refund-mode-original-route",
  );
  expect(originalRoute.props.accessibilityState.disabled).toBe(true);
  expect(
    screen.getByTestId("installment-cancel-refund-mode-voucher").props
      .accessibilityState.selected,
  ).toBe(true);
  expect(
    screen.getByText(/card payments, which cannot be refunded through the original route/u),
  ).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installment-cancel-refund"));
  expect(
    screen.getByText(/refunded in full as new refund vouchers/u),
  ).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installment-confirm-operation-submit"));
  expect(spies.cancelWithRefund).toHaveBeenCalledTimes(1);
  await screen.unmount();
}, 15_000);

test("busy 与 online-required 明确说明阻塞原因并禁用主操作", async () => {
  const busy = createPresenter({
    busy: true,
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
    statusCode: "online-required",
  });
  const screen = await render(
    <InstallmentScreen
      onStartRepayment={() => true}
      presenter={busy.presenter}
    />,
  );

  expect(
    screen.getByTestId("installment-continue-to-payment").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(screen.getByTestId("installment-action-blocked-busy")).toBeTruthy();
  expect(screen.getByTestId("installments-status-online-required")).toBeTruthy();
  await screen.unmount();
});

test("窄屏列表优先，选中后详情接管并可返回列表", async () => {
  setWindowWidth(800);
  const { presenter, spies } = createPresenter({
    details: details("Active"),
    orders: [summary()],
    selectedGuid: GUID,
  });
  const screen = await render(
    <InstallmentScreen onStartRepayment={() => true} presenter={presenter} />,
  );

  expect(screen.getByTestId("installments-list")).toBeTruthy();
  expect(screen.queryByTestId("installments-details-pane")).toBeNull();
  await fireEvent.press(screen.getByTestId(`installment-row-${GUID}`));
  expect(spies.select).toHaveBeenCalledWith(GUID);
  expect(screen.queryByTestId("installments-list")).toBeNull();
  expect(screen.getByTestId("installments-details-pane")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installments-details-back"));
  expect(screen.getByTestId("installments-list")).toBeTruthy();
  await screen.unmount();
});

test("主操作有购物车时启动新建，准备失败提示；空购物车时返回销售添加商品", async () => {
  const onBack = jest.fn();
  const onStartCreate = jest.fn(() => false);
  const withDraft = createPresenter({ createDraft: draft() });
  const draftScreen = await render(
    <InstallmentScreen
      onBack={onBack}
      onStartCreate={onStartCreate}
      presenter={withDraft.presenter}
    />,
  );
  await fireEvent.press(draftScreen.getByTestId("installments-primary-action"));
  expect(onStartCreate).toHaveBeenCalledTimes(1);
  expect(draftScreen.getByTestId("installments-navigation-failed")).toBeTruthy();
  await draftScreen.unmount();

  const empty = createPresenter({ createDraft: null });
  const emptyScreen = await render(
    <InstallmentScreen onBack={onBack} presenter={empty.presenter} />,
  );
  expect(emptyScreen.getByText("Add items in sales")).toBeTruthy();
  await fireEvent.press(emptyScreen.getByTestId("installments-primary-action"));
  expect(onBack).toHaveBeenCalledTimes(1);
  await emptyScreen.unmount();
});

test("已取货和已取消只显示完成信息，不显示动作区", async () => {
  for (const status of ["PickedUp", "Cancelled"] as const) {
    const { presenter } = createPresenter({
      details: details(status),
      orders: [summary()],
      selectedGuid: GUID,
    });
    const screen = await render(<InstallmentScreen presenter={presenter} />);
    expect(screen.queryByTestId("installment-action-dock")).toBeNull();
    expect(
      screen.getAllByText(
        status === "PickedUp" ? "Picked up" : "Refunded and cancelled",
      ).length,
    ).toBeGreaterThan(0);
    if (status === "PickedUp") {
      expect(screen.getByText("Handle with care")).toBeTruthy();
    }
    await screen.unmount();
  }
});

test("已完成与已取消详情仍可在标题工具区重打，按钮满足 44pt 与无障碍合同", async () => {
  for (const status of ["PickedUp", "Cancelled"] as const) {
    const { presenter, spies } = createPresenter(
      {
        details: details(status),
        orders: [summary()],
        selectedGuid: GUID,
      },
      true,
    );
    const screen = await render(<InstallmentScreen presenter={presenter} />);
    const button = screen.getByTestId("installment-reprint");

    expect(StyleSheet.flatten(button.props.style).minHeight).toBe(
      INSTALLMENTS_MIN_TOUCH_TARGET,
    );
    expect(button.props.accessibilityLabel).toBe(
      "Reprint receipt for IP-0001",
    );
    expect(button.props.accessibilityHint).toBe(
      "Prints another copy of the existing installment receipt.",
    );
    expect(screen.queryByTestId("installment-action-dock")).toBeNull();
    await fireEvent.press(button);
    expect(spies.reprintSelected).toHaveBeenCalledTimes(1);
    await screen.unmount();
  }
});

test("打印中禁用重打，成功与失败反馈通过详情 live region 播报", async () => {
  const submitting = createPresenter(
    {
      details: details("Active"),
      orders: [summary()],
      reprint: { kind: "submitting", installmentGuid: GUID },
      selectedGuid: GUID,
    },
    true,
  );
  const submittingScreen = await render(
    <InstallmentScreen presenter={submitting.presenter} />,
  );
  const submittingButton = submittingScreen.getByTestId("installment-reprint");
  expect(submittingButton.props.accessibilityState.disabled).toBe(true);
  expect(submittingButton).toHaveTextContent("Printing…");
  await fireEvent.press(submittingButton);
  expect(submitting.spies.reprintSelected).not.toHaveBeenCalled();
  await submittingScreen.unmount();

  for (const [reprint, testID, message] of [
    [
      { kind: "succeeded" as const, installmentGuid: GUID },
      "installment-reprint-succeeded",
      "Receipt reprinted.",
    ],
    [
      { kind: "failed" as const, installmentGuid: GUID },
      "installment-reprint-failed",
      "Receipt could not be reprinted. Check the printer and try again.",
    ],
  ] as const) {
    const result = createPresenter(
      {
        details: details("Active"),
        orders: [summary()],
        reprint,
        selectedGuid: GUID,
      },
      true,
    );
    const screen = await render(
      <InstallmentScreen presenter={result.presenter} />,
    );
    const feedback = screen.getByTestId(testID);
    expect(feedback).toHaveTextContent(message);
    expect(feedback.props.accessibilityLiveRegion).toBe("polite");
    expect(
      screen.getByTestId("installment-reprint").props.accessibilityState.disabled,
    ).toBe(false);
    await screen.unmount();
  }
});

test("打印中禁用新建、续付、危险操作与取货入口", async () => {
  const onStartCreate = jest.fn(() => true);
  const onStartRepayment = jest.fn((_installmentGuid: string) => true);
  const active = createPresenter(
    {
      createDraft: draft(),
      details: details("Active"),
      orders: [summary()],
      reprint: { kind: "submitting", installmentGuid: GUID },
      selectedGuid: GUID,
    },
    true,
  );
  const activeScreen = await render(
    <InstallmentScreen
      onStartCreate={onStartCreate}
      onStartRepayment={onStartRepayment}
      presenter={active.presenter}
    />,
  );

  expect(
    activeScreen.getByTestId("installments-primary-action").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    activeScreen.getByTestId("installment-continue-to-payment").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    activeScreen.getByTestId("installment-more-actions").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    activeScreen.getByTestId("installment-action-blocked-busy"),
  ).toBeTruthy();
  await fireEvent.press(
    activeScreen.getByTestId("installment-continue-to-payment"),
  );
  await fireEvent.press(activeScreen.getByTestId("installments-primary-action"));
  expect(onStartRepayment).not.toHaveBeenCalled();
  expect(onStartCreate).not.toHaveBeenCalled();
  await activeScreen.unmount();

  const paidOff = createPresenter(
    {
      details: details("PaidOff"),
      orders: [summary()],
      reprint: { kind: "submitting", installmentGuid: GUID },
      selectedGuid: GUID,
    },
    true,
  );
  const paidOffScreen = await render(
    <InstallmentScreen presenter={paidOff.presenter} />,
  );

  expect(
    paidOffScreen.getByTestId("installment-confirm-pickup").props
      .accessibilityState.disabled,
  ).toBe(true);
  expect(
    paidOffScreen.getByTestId("installment-pickup-note").props.editable,
  ).toBe(false);
  expect(
    paidOffScreen.getByTestId("installment-action-blocked-busy"),
  ).toBeTruthy();
  await paidOffScreen.unmount();
});

test("同店跨终端 capability 启用后只展示继续付款与原设备动作说明", async () => {
  const onStartRepayment = jest.fn((_installmentGuid: string) => true);
  const crossDevice = createPresenter(
    {
      details: { ...details("Active"), deviceCode: "IPAD-2" },
      orders: [{ ...summary(), deviceCode: "IPAD-2" }],
      selectedGuid: GUID,
    },
    false,
    false,
    true,
  );
  const screen = await render(
    <InstallmentScreen
      onStartRepayment={onStartRepayment}
      presenter={crossDevice.presenter}
    />,
  );

  expect(screen.getAllByText("IPAD-2").length).toBeGreaterThan(0);
  expect(screen.queryByTestId("installment-reprint")).toBeNull();
  expect(screen.getByTestId("installment-action-dock")).toBeTruthy();
  expect(screen.getByTestId("installment-continue-to-payment")).toBeTruthy();
  expect(screen.queryByTestId("installment-more-actions")).toBeNull();
  expect(screen.getByTestId("installment-cross-device-notice").props.children).toBe(
    "Created on another device. You can continue payment here; cancellation, void, pickup and receipt reprint remain on the original device.",
  );

  await fireEvent.press(screen.getByTestId("installment-continue-to-payment"));
  expect(onStartRepayment).toHaveBeenCalledWith(GUID);
  await screen.unmount();

  mockLanguage = "zh-CN";
  setWindowWidth(820);
  const chinese = await render(
    <InstallmentScreen
      onStartRepayment={() => true}
      presenter={crossDevice.presenter}
    />,
  );
  await fireEvent.press(
    chinese.getByTestId(`installment-row-${GUID}`),
  );
  expect(chinese.getByTestId("installment-cross-device-notice").props.children).toBe(
    "此分期单创建于另一台设备；本机可继续付款，取消、作废、提货和重打小票仍须回到原设备操作。",
  );
  await chinese.unmount();
});

test("同店跨终端高风险动作只展示服务端逐项放行的入口", async () => {
  const active = createPresenter(
    {
      details: { ...details("Active"), deviceCode: "IPAD-2" },
      orders: [{ ...summary(), deviceCode: "IPAD-2" }],
      selectedGuid: GUID,
    },
    false,
    false,
    false,
    {
      selectedDetailsCancelRefundable: true,
      selectedDetailsVoidable: false,
    },
  );
  const screen = await render(
    <InstallmentScreen presenter={active.presenter} />,
  );

  await fireEvent.press(screen.getByTestId("installment-more-actions"));
  expect(screen.getByTestId("installment-more-cancel")).toBeTruthy();
  expect(screen.queryByTestId("installment-more-void")).toBeNull();
  await screen.unmount();

  const paidOff = createPresenter(
    {
      details: { ...details("PaidOff"), deviceCode: "IPAD-2" },
      orders: [{ ...summary(), deviceCode: "IPAD-2" }],
      selectedGuid: GUID,
    },
    false,
    false,
    false,
    { selectedDetailsPickupConfirmable: true },
  );
  const pickupScreen = await render(
    <InstallmentScreen presenter={paidOff.presenter} />,
  );
  expect(pickupScreen.getByTestId("installment-confirm-pickup")).toBeTruthy();
  await pickupScreen.unmount();
});

test("跨终端 capability 关闭或分期已付清时不呈现续付及原设备高风险动作", async () => {
  for (const scenario of [
    { status: "Active" as const, repayable: false },
    { status: "PaidOff" as const, repayable: false },
  ]) {
    const readonly = createPresenter(
      {
        details: { ...details(scenario.status), deviceCode: "IPAD-2" },
        orders: [{ ...summary(), deviceCode: "IPAD-2" }],
        selectedGuid: GUID,
      },
      false,
      false,
      scenario.repayable,
    );
    const screen = await render(
      <InstallmentScreen
        onStartRepayment={() => true}
        presenter={readonly.presenter}
      />,
    );

    expect(screen.queryByTestId("installment-reprint")).toBeNull();
    expect(screen.queryByTestId("installment-action-dock")).toBeNull();
    expect(screen.queryByTestId("installment-continue-to-payment")).toBeNull();
    expect(screen.queryByTestId("installment-confirm-pickup")).toBeNull();
    await screen.unmount();
  }
});

test("详情错误只在详情区呈现，不再重复顶栏状态横幅", async () => {
  for (const statusCode of [
    "details-failed",
    "service-unavailable",
    "details-unavailable",
  ] as const) {
    const { presenter } = createPresenter({
      orders: [summary()],
      selectedGuid: GUID,
      statusCode,
    });
    const screen = await render(<InstallmentScreen presenter={presenter} />);

    expect(
      screen.queryByTestId(`installments-status-${statusCode}`),
    ).toBeNull();
    expect(
      screen.getByTestId(
        statusCode === "details-unavailable"
          ? "installments-details-unavailable"
          : "installments-details-failed",
      ),
    ).toBeTruthy();
    await screen.unmount();
  }
});

test("运行时未接线页安全返回", async () => {
  const onBack = jest.fn();
  const screen = await render(
    <InstallmentsUnavailableScreen onBack={onBack} />,
  );
  expect(screen.getByTestId("installments-runtime-unavailable")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("installments-unavailable-back"));
  expect(onBack).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

function createPresenter(
  overrides: Partial<InstallmentPresenterState>,
  canReprint = false,
  selectedDetailsWritable = true,
  selectedDetailsRepayable = selectedDetailsWritable,
  actionCapabilities: Partial<
    InstallmentScreenPresenter["capabilities"]
  > = {},
) {
  const state: InstallmentPresenterState = {
    access: {
      canAddRepayment: true,
      canCancel: true,
      canConfirmPickup: true,
      canCreate: true,
      canView: true,
    },
    busy: false,
    cancelReason: "",
    cancelRefundMode: "original-route",
    createDownPayment: "20.00",
    createDraft: null,
    createNote: "",
    createPaymentMethod: "cash",
    createVoucherReference: "",
    customerName: "",
    customerPhone: "",
    dateFilter: { preset: "all", fromDate: null, toDate: null },
    details: null,
    detailsLoading: false,
    deviceScope: "store",
    hasMore: false,
    kind: "ready",
    loadingMore: false,
    online: true,
    orders: [],
    pane: "history",
    pickupNote: "",
    query: "",
    recoveryRequired: false,
    reprint: { kind: "idle" },
    repaymentAmount: "",
    repaymentMethod: "cash",
    repaymentVoucherReference: "",
    selectedGuid: null,
    statusCode: null,
    statusFilter: null,
    voidReason: "",
    ...overrides,
  };
  const spies = {
    cancelWithRefund: jest.fn(async () => undefined),
    confirmPickup: jest.fn(async () => undefined),
    load: jest.fn(async () => undefined),
    loadMore: jest.fn(async () => undefined),
    recoverBlocking: jest.fn(async () => undefined),
    reprintSelected: jest.fn(async () => undefined),
    retryDetails: jest.fn(async () => undefined),
    select: jest.fn(async (_installmentGuid: string) => undefined),
    setCancelReason: jest.fn((_value: string) => undefined),
    setCancelRefundMode: jest.fn(
      (_value: InstallmentPresenterState["cancelRefundMode"]) => undefined,
    ),
    setDateFilter: jest.fn(async (_value: InstallmentPresenterState["dateFilter"]) => undefined),
    setDeviceScope: jest.fn(async (_value: InstallmentPresenterState["deviceScope"]) => undefined),
    setPickupNote: jest.fn((_value: string) => undefined),
    setSearchQuery: jest.fn((_value: string) => undefined),
    setStatusFilter: jest.fn(async (_value: InstallmentPresenterState["statusFilter"]) => undefined),
    setVoidReason: jest.fn((_value: string) => undefined),
    subscribe: jest.fn((_listener: () => void) => () => undefined),
    voidSelected: jest.fn(async () => undefined),
  };
  const presenter: InstallmentScreenPresenter = {
    ...spies,
    capabilities: {
      reprint: canReprint,
      selectedDetailsCancelRefundable:
        actionCapabilities.selectedDetailsCancelRefundable ??
        selectedDetailsWritable,
      selectedDetailsCancelRefundMode:
        actionCapabilities.selectedDetailsCancelRefundMode ??
        "original-route",
      selectedDetailsOriginalRouteRefundable:
        actionCapabilities.selectedDetailsOriginalRouteRefundable ?? true,
      selectedDetailsPickupConfirmable:
        actionCapabilities.selectedDetailsPickupConfirmable ??
        selectedDetailsWritable,
      selectedDetailsRepayable,
      selectedDetailsVoidable:
        actionCapabilities.selectedDetailsVoidable ??
        selectedDetailsWritable,
      selectedDetailsWritable,
    },
    getState: () => state,
  };
  return { presenter, spies };
}

function setWindowWidth(width: number): void {
  Dimensions.set({
    window: { width, height: 768, scale: 2, fontScale: 1 },
    screen: { width, height: 768, scale: 2, fontScale: 1 },
  });
}

const GUID = "10000000-0000-4000-8000-000000000001";

function draft() {
  return {
    revision: 1,
    totalCents: 10_000,
    lines: [
      {
        lineKey: "L1",
        displayName: "Tea",
        quantity: "1",
        actualAmountCents: 10_000,
      },
    ],
  };
}

function summary() {
  return {
    installmentGuid: GUID,
    installmentNumber: "IP-0001",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierName: "Alice",
    customerName: "Bob",
    customerPhone: "0400000000",
    createdAtIso: "2026-07-27T01:02:03.000Z",
    totalCents: 10_000,
    downPaymentCents: 2_000,
    paidCents: 2_000,
    balanceCents: 8_000,
    status: "Active" as const,
    updatedAtIso: "2026-07-27T02:03:04.000Z",
  };
}

function details(
  status: "Active" | "PaidOff" | "PickedUp" | "Cancelled",
): InstallmentDetails {
  const balanceCents = status === "Active" ? 500 : 0;
  return {
    ...summary(),
    status,
    paidCents: 10_000 - balanceCents,
    balanceCents,
    cashierId: "C1",
    minimumDownPaymentCents: 2_000,
    lines: [
      {
        installmentLineGuid: "30000000-0000-4000-8000-000000000001",
        productCode: "P1",
        referenceCode: "R1",
        displayName: "Tea",
        lookupCode: "930001",
        quantity: "1",
        unitPriceCents: 10_000,
        discountCents: 500,
        actualAmountCents: 9_500,
        itemNumber: "I1",
      },
    ],
    payments: [
      {
        paymentGuid: "PAY-GUID-NOT-DISPLAYED",
        method: "card",
        amountCents: 9_500,
        status: "Recorded",
        recordedAtIso: "2026-07-27T02:03:04.000Z",
        cashierId: "CASHIER-2",
        deviceCode: "IPAD-2",
        cardType: "Visa",
        maskedCardNumber: "•••• 4242",
        reference: "PAYMENT-REF-SECRET",
        authorizationCode: "AUTH-SECRET",
        token: "TOKEN-SECRET",
      } as InstallmentDetails["payments"][number],
    ],
    pickupInfo:
      status === "PickedUp"
        ? {
            pickedUpAtIso: "2026-07-28T01:00:00.000Z",
            pickedUpBy: "Alice",
            note: "Handle with care",
          }
        : null,
    cancellationInfo:
      status === "Cancelled"
        ? {
            kind: "RefundCancel",
            cancelledAtIso: "2026-07-28T01:00:00.000Z",
            cancelledBy: "Manager",
            reason: "Customer request",
          }
        : null,
    note: "Friday",
  };
}
