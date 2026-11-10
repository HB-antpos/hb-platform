import { expect, jest, test } from "@jest/globals";
import { act, fireEvent, render, waitFor } from "@testing-library/react-native";
import { ScrollView, StyleSheet } from "react-native";

import {
  OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET,
  OperationAuthorizationModal,
} from "./operation-authorization-modal";
import {
  OperationAuthorizationService,
  type OperationAuthorizationResult,
} from "./operation-authorization-service";

import type { CashierSessionDto } from "@/core/api/hbpos-api";
import type { CashierLoginResult } from "@/core/security/cashier-authentication";
import { posColors } from "@/ui/theme";

const PERMISSION = "Permissions.PosTerminal.Sales.ChangePrice";

function supervisor(
  permissionCodes: readonly string[] = [PERMISSION],
): CashierSessionDto {
  return {
    authorizationExpiresAtUtc: "2026-07-28T07:00:00.000Z",
    authorizationToken: "must-never-render",
    cashierId: "SUPERVISOR",
    cashierName: "Supervisor",
    deviceCode: "IPAD-1",
    isEmergencyOverride: false,
    permissionCodes: [...permissionCodes],
    storeCode: "STORE-1",
    userGuid: "supervisor-guid",
  };
}

function createHarness(
  login: (barcode: string) => Promise<CashierLoginResult> = async () => ({
    session: supervisor(),
    source: "online",
  }),
) {
  const submittedBarcodes: string[] = [];
  const service = new OperationAuthorizationService({
    audit: { append: async () => {} },
    cashierAuthentication: {
      login: async (input) => {
        submittedBarcodes.push(input.userBarcode);
        return login(input.userBarcode);
      },
    },
    createId: () => "00000000-0000-4000-8000-000000000001",
    nowIso: () => "2026-07-28T06:00:00.000Z",
  });
  service.activateRequestingCashier({
    cashierId: "REQUESTER",
    cashierName: "Requester",
    deviceCode: "IPAD-1",
    permissions: [],
    storeCode: "STORE-1",
    userGuid: "requester-guid",
  });
  return { service, submittedBarcodes };
}

function startAuthorization(
  service: OperationAuthorizationService,
  actionId = "00000000-0000-4000-8000-000000000101",
  action = "change-price",
): Promise<OperationAuthorizationResult<string>> {
  return service.authorizeAndRun(
    {
      action,
      actionId,
      permissionCode: PERMISSION,
      screen: "PosTerminal",
    },
    () => "completed",
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, reject, resolve };
}

test("仅在待授权时显示，中英双语且输入与按钮满足安全和触控约束", async () => {
  const { service } = createHarness();
  const screen = await render(
    <OperationAuthorizationModal locale="zh-CN" service={service} />,
  );
  expect(screen.queryByTestId("operation-authorization-modal")).toBeNull();

  let authorization!: Promise<OperationAuthorizationResult<string>>;
  await act(async () => {
    authorization = startAuthorization(service);
    await Promise.resolve();
  });

  expect(screen.toJSON()).toMatchObject({
    props: {
      supportedOrientations: ["landscape-left", "landscape-right"],
    },
    type: "Modal",
  });
  expect(screen.getByText("此操作需要主管批准")).toBeTruthy();
  expect(screen.getByText("申请操作：change-price")).toBeTruthy();
  const input = screen.getByTestId("operation-authorization-barcode");
  expect(input.props.secureTextEntry).toBe(true);
  expect(input.props.showSoftInputOnFocus).toBe(false);
  expect(input.props.autoComplete).toBe("off");
  expect(input.props.autoFocus).toBe(true);
  expect(StyleSheet.flatten(input.props.style).minHeight).toBeGreaterThanOrEqual(
    OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET,
  );
  const keyboardButton = screen.getByTestId(
    "operation-authorization-show-keyboard",
  );
  expect(keyboardButton.props.accessibilityRole).toBe("button");
  expect(keyboardButton.props.accessibilityLabel).toBe("键盘");
  expect(
    StyleSheet.flatten(keyboardButton.props.style).minHeight,
  ).toBeGreaterThanOrEqual(OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET);
  for (const testID of [
    "operation-authorization-cancel",
    "operation-authorization-submit",
  ]) {
    expect(
      StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
    ).toBeGreaterThanOrEqual(OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET);
  }

  await screen.rerender(
    <OperationAuthorizationModal locale="en-AU" service={service} />,
  );
  expect(screen.getByText("Approval required")).toBeTruthy();
  expect(screen.getByText("Requested action: change-price")).toBeTruthy();
  // 新布局：键盘入口是只有图标的方键，名称由无障碍标签提供。
  expect(screen.queryByText("Keyboard")).toBeNull();
  expect(
    screen.getByTestId("operation-authorization-show-keyboard").props
      .accessibilityLabel,
  ).toBe("Keyboard");

  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-cancel"));
    await Promise.resolve();
  });
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
});

test.each([
  ["zh-CN", "payment-recovery-paid", "申请操作：人工确认刷卡已收款"],
  ["zh-CN", "payment-recovery-unpaid", "申请操作：人工确认刷卡未扣款"],
  ["zh-CN", "payment-recovery-uncertain", "申请操作：保留刷卡待核实状态"],
  ["en-AU", "payment-recovery-paid", "Requested action: Manually confirm card payment received"],
  ["en-AU", "payment-recovery-unpaid", "Requested action: Manually confirm no card payment"],
  ["en-AU", "payment-recovery-uncertain", "Requested action: Keep card payment awaiting verification"],
] as const)("支付恢复动作 %s / %s 显示本地化名称", async (locale, action, expected) => {
  const { service } = createHarness();
  const authorization = startAuthorization(service, undefined, action);
  const screen = await render(
    <OperationAuthorizationModal locale={locale} service={service} />,
  );

  expect(screen.getByText(expected)).toBeTruthy();
  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-cancel"));
    await Promise.resolve();
  });
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
});

test("扫码输入默认静默软键盘，仅按钮手动开启，失焦与重复请求均安全复位", async () => {
  const { service } = createHarness();
  const authorization = startAuthorization(service);
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );

  expect(
    screen.getByTestId("operation-authorization-barcode").props
      .showSoftInputOnFocus,
  ).toBe(false);

  jest.useFakeTimers();
  try {
    await fireEvent.press(
      screen.getByTestId("operation-authorization-show-keyboard"),
    );
    expect(
      screen.getByTestId("operation-authorization-barcode").props
        .showSoftInputOnFocus,
    ).toBe(true);

    // 已在手动键盘模式时再次请求，仍强制完成 false -> true 的输入视图刷新。
    await fireEvent.press(
      screen.getByTestId("operation-authorization-show-keyboard"),
    );
    expect(
      screen.getByTestId("operation-authorization-barcode").props
        .showSoftInputOnFocus,
    ).toBe(false);
    await act(() => {
      jest.runOnlyPendingTimers();
    });
    expect(
      screen.getByTestId("operation-authorization-barcode").props
        .showSoftInputOnFocus,
    ).toBe(true);

    await fireEvent(
      screen.getByTestId("operation-authorization-barcode"),
      "blur",
    );
    expect(
      screen.getByTestId("operation-authorization-barcode").props
        .showSoftInputOnFocus,
    ).toBe(false);
  } finally {
    jest.useRealTimers();
  }

  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-cancel"));
    await Promise.resolve();
  });
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
});

test("主管手动键盘聚焦时由弹窗内滚动容器揭示输入，HID 聚焦保持静默", async () => {
  const revealInput = jest
    .spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    )
    .mockImplementation(() => undefined);
  const { service } = createHarness();
  const authorization = startAuthorization(service);
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );

  try {
    expect(
      screen.getByTestId("operation-authorization-keyboard-scroll").props,
    ).toMatchObject({
      automaticallyAdjustKeyboardInsets: true,
      keyboardDismissMode: "interactive",
      keyboardShouldPersistTaps: "handled",
    });
    await fireEvent(
      screen.getByTestId("operation-authorization-barcode"),
      "focus",
      { target: 301 },
    );
    expect(revealInput).not.toHaveBeenCalled();

    await fireEvent.press(
      screen.getByTestId("operation-authorization-show-keyboard"),
    );
    await waitFor(() => {
      expect(revealInput).toHaveBeenCalledTimes(1);
      expect(revealInput).toHaveBeenCalledWith(301, 16, true);
    });
  } finally {
    await fireEvent.press(
      screen.getByTestId("operation-authorization-cancel"),
    );
    await expect(authorization).resolves.toEqual({
      authorized: false,
      reason: "CANCELLED",
    });
    revealInput.mockRestore();
    await screen.unmount();
  }
});

test("HID CR/LF 只提交一次、立即清空遮罩输入，核验期间仍可取消", async () => {
  const login = deferred<CashierLoginResult>();
  const { service, submittedBarcodes } = createHarness(() => login.promise);
  const authorization = startAuthorization(service);
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );
  const input = screen.getByTestId("operation-authorization-barcode");

  await act(async () => {
    fireEvent.changeText(input, " SUP-007\r");
    await Promise.resolve();
  });

  await waitFor(() => expect(submittedBarcodes).toEqual(["SUP-007"]));
  expect(screen.getByTestId("operation-authorization-barcode").props.value).toBe(
    "",
  );
  expect(
    screen.getByTestId("operation-authorization-submit").props
      .accessibilityState,
  ).toEqual({ disabled: true });
  expect(
    screen.getByTestId("operation-authorization-barcode").props.editable,
  ).toBe(false);
  expect(
    screen.getByTestId("operation-authorization-barcode").props
      .showSoftInputOnFocus,
  ).toBe(false);
  expect(
    screen.getByTestId("operation-authorization-show-keyboard").props
      .accessibilityState,
  ).toEqual({ disabled: true });

  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-submit"));
    fireEvent.press(screen.getByTestId("operation-authorization-cancel"));
    await Promise.resolve();
  });
  await waitFor(() =>
    expect(screen.queryByTestId("operation-authorization-modal")).toBeNull(),
  );
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
  expect(submittedBarcodes).toEqual(["SUP-007"]);

  login.resolve({ session: supervisor(), source: "online" });
  await act(async () => {
    await Promise.resolve();
  });
});

test("拒绝后保留弹窗并恢复扫码，重试成功且界面不泄漏条码或票据", async () => {
  let attempt = 0;
  const { service, submittedBarcodes } = createHarness(async () => {
    attempt += 1;
    return {
      session: supervisor(attempt === 1 ? [] : [PERMISSION]),
      source: "online",
    };
  });
  const authorization = startAuthorization(service);
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );

  await act(async () => {
    fireEvent.changeText(
      screen.getByTestId("operation-authorization-barcode"),
      "LEAK-ME\n",
    );
    await Promise.resolve();
  });

  await waitFor(() =>
    expect(screen.getByTestId("operation-authorization-feedback")).toBeTruthy(),
  );
  expect(screen.getByText("该主管没有批准此操作的权限。")).toBeTruthy();
  expect(screen.getByTestId("operation-authorization-modal")).toBeTruthy();
  expect(
    screen.getByTestId("operation-authorization-barcode").props.editable,
  ).toBe(true);
  expect(
    screen.getByTestId("operation-authorization-barcode").props.value,
  ).toBe("");
  expect(
    screen.getByTestId("operation-authorization-barcode").props
      .showSoftInputOnFocus,
  ).toBe(false);
  const serialized = JSON.stringify(screen.toJSON());
  expect(serialized).not.toContain("LEAK-ME");
  expect(serialized).not.toContain("must-never-render");

  await act(async () => {
    fireEvent.changeText(
      screen.getByTestId("operation-authorization-barcode"),
      "APPROVED\r\n",
    );
    await Promise.resolve();
  });
  await waitFor(() =>
    expect(screen.queryByTestId("operation-authorization-modal")).toBeNull(),
  );
  await expect(authorization).resolves.toEqual({
    authorized: true,
    value: "completed",
  });
  expect(submittedBarcodes).toEqual(["LEAK-ME", "APPROVED"]);
});

test("主管授权弹窗点击面板外遮罩取消", async () => {
  const { service } = createHarness();
  const authorization = startAuthorization(service);
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );

  expect(screen.getByTestId("operation-authorization-modal")).toBeTruthy();
  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-backdrop"));
    await Promise.resolve();
  });
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
});

test("弹窗按新布局放大：条码框与键盘方键 76、取消 180×72、核验主管为 72 高主按钮", async () => {
  const login = deferred<CashierLoginResult>();
  const { service } = createHarness(() => login.promise);
  const authorization = startAuthorization(
    service,
    undefined,
    "payment-recovery-paid",
  );
  const screen = await render(
    <OperationAuthorizationModal locale="zh" service={service} />,
  );

  expect(screen.getByText("申请操作：人工确认刷卡已收款")).toBeTruthy();
  expect(
    screen.getByText("条码会被遮罩，仅用于本次授权，并在提交后立即清除。"),
  ).toBeTruthy();
  const input = screen.getByTestId("operation-authorization-barcode");
  expect(input.props.secureTextEntry).toBe(true);
  expect(StyleSheet.flatten(input.props.style)).toMatchObject({
    minHeight: 76,
  });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("operation-authorization-show-keyboard").props.style,
    ),
  ).toMatchObject({ minHeight: 76, minWidth: 76 });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("operation-authorization-cancel").props.style,
    ),
  ).toMatchObject({ minHeight: 72, width: 180 });
  const submit = screen.getByTestId("operation-authorization-submit");
  expect(StyleSheet.flatten(submit.props.style)).toMatchObject({
    backgroundColor: posColors.accent,
    minHeight: 72,
  });
  expect(submit.props.accessibilityLabel).toBe("核验主管");

  // 核验期间主按钮显示「正在核验…」并禁用，「取消」仍可点。
  await act(async () => {
    fireEvent.changeText(input, "SUP-NEW-LAYOUT\r");
    await Promise.resolve();
  });
  await waitFor(() =>
    expect(
      screen.getByTestId("operation-authorization-submit").props
        .accessibilityLabel,
    ).toBe("正在核验…"),
  );
  expect(
    screen.getByTestId("operation-authorization-cancel").props
      .accessibilityState,
  ).toEqual({ disabled: false });

  await act(async () => {
    fireEvent.press(screen.getByTestId("operation-authorization-cancel"));
    await Promise.resolve();
  });
  await expect(authorization).resolves.toEqual({
    authorized: false,
    reason: "CANCELLED",
  });
  login.resolve({ session: supervisor(), source: "online" });
  await act(async () => {
    await Promise.resolve();
  });
});
