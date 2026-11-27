import { expect, jest, test } from "@jest/globals";
import { act, fireEvent, render, waitFor } from "@testing-library/react-native";
import { ScrollView, StyleSheet, TextInput } from "react-native";

import {
  CashierLoginController,
  type CashierLoginRuntime,
  type CashierLoginStorePort,
} from "./cashier-login-controller";
import { CashierLoginScreen } from "./cashier-login-screen";

import { HbposApiError } from "@/core/api/hbpos-api";
import type { PosCashierSummary } from "@/core/runtime/production-pos-service-composition";
import { posColors } from "@/ui/theme";

let mockStatusStripProps: any;

// mock 记录图标 props，便于断言输入区按钮使用的图标名（如清除按钮 close）。
const mockMaterialCommunityIcons = jest.fn((_props: unknown) => null);

jest.mock("@expo/vector-icons", () => ({
  MaterialCommunityIcons: (props: unknown) => {
    mockMaterialCommunityIcons(props);
    return null;
  },
}));

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: (props: unknown) => {
    mockStatusStripProps = props;
    return null;
  },
}));

function runtime(
  signIn: (barcode: string) => Promise<PosCashierSummary>,
): CashierLoginRuntime {
  return {
    state: { phase: "ready", device: "authorized-online" },
    services: { cashierSession: { signIn } },
  };
}

function cashier(): PosCashierSummary {
  return {
    cashierId: "C1",
    cashierName: "Cashier",
    deviceCode: "IPAD-1",
    permissions: [],
    source: "online",
    storeCode: "S1",
    userGuid: null,
  };
}

function store(): CashierLoginStorePort {
  return { clearActiveCashier: jest.fn(), setActiveCashier: jest.fn() };
}

test("登录页以弱提示单行显示 Runtime 和 OTA 版本且不可交互", async () => {
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
      versionInfo={{ runtimeVersion: "0.2.0", otaVersion: "a1b2c3d4" }}
    />,
  );

  const versionInfo = screen.getByTestId("cashier-login-version-info");
  expect(versionInfo.props.children).toBe("Runtime: 0.2.0 · OTA: a1b2c3d4");
  expect(versionInfo.props.numberOfLines).toBe(1);
  expect(versionInfo.props.pointerEvents).toBe("none");
  expect(versionInfo.props.selectable).toBe(false);
  expect(versionInfo.props.accessible).toBeUndefined();
  expect(StyleSheet.flatten(versionInfo.props.style).color).toBe(
    posColors.mutedInk,
  );
  expect(StyleSheet.flatten(versionInfo.props.style)).not.toHaveProperty(
    "position",
    "absolute",
  );
  const pageStyle = StyleSheet.flatten(
    screen.getByTestId("cashier-login-page").props.style,
  );
  expect(pageStyle).toMatchObject({ flexDirection: "column", padding: 16 });
});

test("在线、离线、错误与锁定状态使用真实 runtime 和用户操作进入对应 surface", async () => {
  const online = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );
  expect(online.getByTestId("handheld-state-online-login")).toBeTruthy();
  expect(online.queryByText("HB")).toBeNull();
  await online.unmount();

  const offlineRuntime: CashierLoginRuntime = {
    state: { phase: "ready-offline", device: "authorized-local" },
    services: { cashierSession: { signIn: async () => cashier() } },
  };
  const offline = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      onSuccess={jest.fn()}
      runtime={offlineRuntime}
    />,
  );
  expect(offline.getByTestId("handheld-state-offline-login")).toBeTruthy();

  await fireEvent.press(offline.getByTestId("cashier-login-submit"));
  expect(await offline.findByTestId("cashier-login-error")).toBeTruthy();
  expect(offline.getByTestId("handheld-state-offline-login")).toBeTruthy();
  await offline.unmount();

  const locked = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      onSuccess={jest.fn()}
      runtime={{
        state: { phase: "locked", device: "locked" },
        services: null,
      }}
    />,
  );
  expect(locked.getByTestId("handheld-state-offline-login")).toBeTruthy();
  expect(locked.getByTestId("cashier-login-submit").props.accessibilityState)
    .toEqual({ disabled: true });
});

test("登录屏把当前语言和切换回调传给状态条", async () => {
  const onSwitchLanguage = jest.fn();
  mockStatusStripProps = null;
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="en-AU"
      onSuccess={jest.fn()}
      onSwitchLanguage={onSwitchLanguage}
      runtime={runtime(async () => cashier())}
    />,
  );

  expect(mockStatusStripProps).toMatchObject({
    language: "en",
    onSwitchLanguage,
  });
  mockStatusStripProps.onSwitchLanguage();
  expect(onSwitchLanguage).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("手动输入和 HID 回车都经同一安全 controller 成功后才进入 sales", async () => {
  const onSuccess = jest.fn();
  const signIn = jest.fn(async () => cashier());
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={onSuccess}
      runtime={runtime(signIn)}
    />,
  );
  const input = screen.getByTestId("cashier-login-barcode");
  expect(input.props.autoFocus).toBe(true);
  expect(input.props.showSoftInputOnFocus).toBe(false);
  await act(async () => {
    fireEvent.changeText(input, " HID-123 ");
  });
  await act(async () => {
    fireEvent(input, "submitEditing", { nativeEvent: { text: " HID-123 " } });
  });

  await waitFor(() => {
    expect(signIn.mock.calls[0]).toEqual(["HID-123"]);
    expect(signIn).toHaveBeenCalledTimes(1);
    expect(onSuccess).toHaveBeenCalledTimes(1);
  });
});

test("收银员条码默认隐藏且显示按钮不改变输入值或软键盘状态", async () => {
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );

  const input = screen.getByTestId("cashier-login-barcode");
  const visibilityButton = screen.getByTestId(
    "cashier-login-toggle-barcode-visibility",
  );
  expect(input.props.secureTextEntry).toBe(true);
  expect(input.props.showSoftInputOnFocus).toBe(false);
  expect(visibilityButton.props.accessibilityRole).toBe("button");
  expect(visibilityButton.props.accessibilityLabel).toBe("显示收银员条码");
  expect(visibilityButton.props.accessibilityState).toEqual({ disabled: false });
  expect(
    StyleSheet.flatten(visibilityButton.props.style),
  ).toMatchObject({ minHeight: 48, minWidth: 48 });

  const textInputPrototype = (
    TextInput as unknown as { prototype: { clear: jest.Mock } }
  ).prototype;
  textInputPrototype.clear.mockClear();
  await fireEvent.changeText(input, "HID-SECRET");
  await fireEvent.press(visibilityButton);
  // 非受控输入：切换显示只改 secureTextEntry，不回写、不清空原生内容。
  expect(screen.getByTestId("cashier-login-barcode").props.value).toBeUndefined();
  expect(textInputPrototype.clear).not.toHaveBeenCalled();
  expect(screen.getByTestId("cashier-login-barcode").props).toMatchObject({
    secureTextEntry: false,
    showSoftInputOnFocus: false,
  });
  expect(
    screen.getByTestId("cashier-login-toggle-barcode-visibility").props
      .accessibilityLabel,
  ).toBe("隐藏收银员条码");

  await fireEvent.press(
    screen.getByTestId("cashier-login-toggle-barcode-visibility"),
  );
  expect(screen.getByTestId("cashier-login-barcode").props).toMatchObject({
    secureTextEntry: true,
    showSoftInputOnFocus: false,
  });
  expect(textInputPrototype.clear).not.toHaveBeenCalled();
});

test("扫码默认不弹软键盘，常驻键盘按钮可手动开启且失焦后复位", async () => {
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );

  expect(screen.getByText("默认使用扫码枪；手动输入请点“键盘”。")).toBeTruthy();
  const keyboardButton = screen.getByTestId("cashier-login-show-keyboard");
  expect(keyboardButton.props.accessibilityRole).toBe("button");
  expect(keyboardButton.props.accessibilityLabel).toBe("键盘");
  expect(
    StyleSheet.flatten(keyboardButton.props.style).minHeight,
  ).toBeGreaterThanOrEqual(48);

  expect(
    screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
  ).toBe(false);
  await act(async () => {
    fireEvent.press(keyboardButton);
  });
  expect(
    screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
  ).toBe(true);

  jest.useFakeTimers();
  try {
    await act(async () => {
      fireEvent.press(keyboardButton);
    });
    expect(
      screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
    ).toBe(false);
    await act(() => {
      jest.runOnlyPendingTimers();
    });
    expect(
      screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
    ).toBe(true);
  } finally {
    jest.useRealTimers();
  }

  await act(async () => {
    fireEvent(screen.getByTestId("cashier-login-barcode"), "blur");
  });
  expect(
    screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
  ).toBe(false);
});

test("手动软键盘聚焦时滚动表单揭示条码输入，HID 聚焦保持静默", async () => {
  const revealInput = jest
    .spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    )
    .mockImplementation(() => undefined);
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );

  try {
    expect(screen.getByTestId("cashier-login-keyboard-scroll").props).toMatchObject(
      {
        automaticallyAdjustKeyboardInsets: true,
        keyboardDismissMode: "interactive",
        keyboardShouldPersistTaps: "handled",
      },
    );
    await fireEvent(screen.getByTestId("cashier-login-barcode"), "focus", {
      target: 201,
    });
    expect(revealInput).not.toHaveBeenCalled();

    await fireEvent.press(screen.getByTestId("cashier-login-show-keyboard"));
    await waitFor(() => {
      expect(revealInput).toHaveBeenCalledTimes(1);
      expect(revealInput).toHaveBeenCalledWith(201, 16, true);
    });
  } finally {
    revealInput.mockRestore();
    await screen.unmount();
  }
});

test("可见条码输入接管 HID 焦点，失焦和卸载都会释放", async () => {
  const onManualInputFocusChange = jest.fn();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onManualInputFocusChange={onManualInputFocusChange}
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );
  const input = screen.getByTestId("cashier-login-barcode");

  expect(input.props.autoFocus).toBe(true);
  await act(async () => {
    fireEvent(input, "focus");
  });
  expect(onManualInputFocusChange.mock.calls).toEqual([[true]]);

  await act(async () => {
    fireEvent(input, "blur");
  });
  expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);

  await act(async () => {
    fireEvent(input, "focus");
  });
  await screen.unmount();
  expect(onManualInputFocusChange.mock.calls).toEqual([
    [true],
    [false],
    [true],
    [false],
  ]);
});

test("HID Enter 登录待定时保持可见输入所有权并拒绝重复提交", async () => {
  let resolveSignIn: ((value: PosCashierSummary) => void) | undefined;
  const signIn = jest.fn(
    () =>
      new Promise<PosCashierSummary>((resolve) => {
        resolveSignIn = resolve;
      }),
  );
  const onManualInputFocusChange = jest.fn();
  const onSuccess = jest.fn();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onManualInputFocusChange={onManualInputFocusChange}
      onSuccess={onSuccess}
      runtime={runtime(signIn)}
    />,
  );
  const input = screen.getByTestId("cashier-login-barcode");

  expect(input.props.submitBehavior).toBe("submit");
  await act(async () => {
    input.props.onFocus();
    input.props.onChangeText(" HID-PENDING ");
  });
  const activeInput = screen.getByTestId("cashier-login-barcode");
  await act(async () => {
    activeInput.props.onSubmitEditing();
    await Promise.resolve();
  });
  expect(signIn).toHaveBeenCalledTimes(1);
  expect(
    screen.getByTestId("cashier-login-toggle-barcode-visibility").props
      .accessibilityState,
  ).toEqual({ disabled: true });

  await act(async () => {
    activeInput.props.onSubmitEditing();
    activeInput.props.onBlur();
    expect(signIn).toHaveBeenCalledTimes(1);
    expect(onManualInputFocusChange.mock.calls).toEqual([[true]]);

    resolveSignIn?.(cashier());
    await Promise.resolve();
  });
  expect(onSuccess).toHaveBeenCalledTimes(1);
});

test("登录失败在输入恢复 editable 后重新取得扫码焦点", async () => {
  let rejectSignIn: ((reason: Error) => void) | undefined;
  const signIn = jest.fn(
    () =>
      new Promise<PosCashierSummary>((_resolve, reject) => {
        rejectSignIn = reject;
      }),
  );
  const onManualInputFocusChange = jest.fn();
  const textInputPrototype = (
    TextInput as unknown as {
      prototype: {
        focus: jest.Mock;
        setNativeProps: jest.Mock;
      };
    }
  ).prototype;
  textInputPrototype.focus.mockClear();
  textInputPrototype.setNativeProps.mockClear();
  let editableWhenFocusRequested: boolean | undefined;
  textInputPrototype.focus.mockImplementationOnce(function (this: {
    props: { editable?: boolean };
  }) {
    editableWhenFocusRequested = this.props.editable;
  });
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onManualInputFocusChange={onManualInputFocusChange}
      onSuccess={jest.fn()}
      runtime={runtime(signIn)}
    />,
  );
  const input = screen.getByTestId("cashier-login-barcode");

  await act(async () => {
    input.props.onFocus();
    input.props.onChangeText(" HID-REJECT ");
  });
  const activeInput = screen.getByTestId("cashier-login-barcode");
  await act(async () => {
    activeInput.props.onSubmitEditing();
    await Promise.resolve();
  });
  expect(signIn).toHaveBeenCalledTimes(1);
  expect(screen.getByTestId("cashier-login-barcode").props.editable).toBe(
    false,
  );

  await act(async () => {
    screen.getByTestId("cashier-login-barcode").props.onBlur();
  });
  expect(onManualInputFocusChange.mock.calls).toEqual([[true]]);

  await act(async () => {
    rejectSignIn?.(new Error("rejected"));
    await Promise.resolve();
    await Promise.resolve();
  });

  await waitFor(() => {
    expect(textInputPrototype.focus).toHaveBeenCalledTimes(1);
  });
  expect(editableWhenFocusRequested).toBe(true);
  expect(textInputPrototype.setNativeProps).toHaveBeenCalledWith({
    showSoftInputOnFocus: false,
  });
  expect(
    screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
  ).toBe(false);
  expect(onManualInputFocusChange.mock.calls).toEqual([[true]]);
});

test("登录提交后清空已扫描内容，失败重试不会与下一条码拼接", async () => {
  const signIn = jest.fn(async () => {
    throw new Error("rejected");
  });
  const textInputPrototype = (
    TextInput as unknown as { prototype: { clear: jest.Mock } }
  ).prototype;
  textInputPrototype.clear.mockClear();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(signIn)}
    />,
  );

  await fireEvent.changeText(
    screen.getByTestId("cashier-login-barcode"),
    "HID-FIRST",
  );
  await fireEvent.press(
    screen.getByTestId("cashier-login-toggle-barcode-visibility"),
  );
  expect(screen.getByTestId("cashier-login-barcode").props.secureTextEntry).toBe(
    false,
  );
  await act(async () => {
    screen.getByTestId("cashier-login-barcode").props.onSubmitEditing();
  });
  await waitFor(() => {
    expect(signIn).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("cashier-login-error")).toBeTruthy();
  });
  // 提交时请求原生清空；测试环境的 clear() 是空实现，正好模拟「清空被原生丢弃」。
  expect(textInputPrototype.clear).toHaveBeenCalled();
  expect(screen.getByTestId("cashier-login-barcode").props.secureTextEntry).toBe(
    true,
  );

  await act(async () => {
    // 原生仍留着上一枪，下一枪接在后面回传；只取新增部分，不拼接。
    fireEvent.changeText(
      screen.getByTestId("cashier-login-barcode"),
      "HID-FIRSTHID-SECOND",
    );
  });
  await act(async () => {
    screen.getByTestId("cashier-login-barcode").props.onSubmitEditing();
  });
  await waitFor(() => {
    expect(signIn.mock.calls).toEqual([["HID-FIRST"], ["HID-SECOND"]]);
  });
  await screen.unmount();
});

test("紧急二维码时钟回拨显示专用安全提示且不进入 sales", async () => {
  const onSuccess = jest.fn();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={onSuccess}
      runtime={runtime(async () => {
        throw new HbposApiError("rejected", {
          kind: "envelope",
          code: "EMERGENCY_CLOCK_ROLLBACK",
        });
      })}
    />,
  );
  await act(async () => {
    fireEvent.changeText(
      screen.getByTestId("cashier-login-barcode"),
      "HBPOSE2-signed",
    );
  });
  await act(async () => {
    fireEvent.press(screen.getByTestId("cashier-login-submit"));
  });

  await waitFor(() => {
    expect(screen.getByText(/系统时间早于可信时间/)).toBeTruthy();
    expect(onSuccess).not.toHaveBeenCalled();
  });
});

test("明确拒绝显示错误且不进入 sales", async () => {
  const onSuccess = jest.fn();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="en"
      onSuccess={onSuccess}
      runtime={runtime(async () => {
        throw new Error("rejected");
      })}
    />,
  );
  expect(
    screen.getByText('Scanner ready; tap "Keyboard" for manual entry.'),
  ).toBeTruthy();
  expect(
    screen.getByTestId("cashier-login-toggle-barcode-visibility").props
      .accessibilityLabel,
  ).toBe("Show cashier barcode");
  await act(async () => {
    fireEvent.press(screen.getByTestId("cashier-login-show-keyboard"));
  });
  expect(
    screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
  ).toBe(true);
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "NOPE");
  });
  await act(async () => {
    fireEvent.press(screen.getByTestId("cashier-login-submit"));
  });

  await waitFor(() => {
    expect(screen.getByText(/not accepted/)).toBeTruthy();
    expect(
      screen.getByTestId("cashier-login-barcode").props.showSoftInputOnFocus,
    ).toBe(false);
    expect(onSuccess).not.toHaveBeenCalled();
  });
});

test("输入内容后显示清除按钮，点击清除全部内容", async () => {
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );

  // 空输入时清除按钮不渲染
  expect(screen.queryByTestId("cashier-login-clear-barcode")).toBeNull();

  await fireEvent.changeText(
    screen.getByTestId("cashier-login-barcode"),
    "2947858456543",
  );
  const clearButton = screen.getByTestId("cashier-login-clear-barcode");
  expect(clearButton.props.accessibilityLabel).toBe("清除收银员条码");
  expect(clearButton.props.accessibilityRole).toBe("button");

  // 点击清除：请求原生清空输入框
  const textInputPrototype = (
    TextInput as unknown as { prototype: { clear: jest.Mock } }
  ).prototype;
  textInputPrototype.clear.mockClear();
  await fireEvent.press(clearButton);
  expect(textInputPrototype.clear).toHaveBeenCalledTimes(1);

  // 清空后清除按钮消失
  expect(screen.queryByTestId("cashier-login-clear-barcode")).toBeNull();
});

test("清除被原生丢弃时，残留内容不会带进下一次扫码登录", async () => {
  const signIn = jest.fn(async () => cashier());
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(signIn)}
    />,
  );
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "TYPO");
  });
  await act(async () => {
    fireEvent.press(screen.getByTestId("cashier-login-clear-barcode"));
  });
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "TYPO1042701");
  });
  await act(async () => {
    fireEvent(screen.getByTestId("cashier-login-barcode"), "submitEditing", {
      nativeEvent: { text: "TYPO1042701" },
    });
  });
  await waitFor(() => expect(signIn.mock.calls).toEqual([["1042701"]]));
  await screen.unmount();
});

test("扫码器回车以原生回传全文为准：JS 镜像还没追上最后几位也不会提交残码", async () => {
  const signIn = jest.fn(async () => cashier());
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(signIn)}
    />,
  );
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "10427");
  });
  await act(async () => {
    fireEvent(screen.getByTestId("cashier-login-barcode"), "submitEditing", {
      nativeEvent: { text: "1042701" },
    });
  });
  await waitFor(() => expect(signIn.mock.calls).toEqual([["1042701"]]));
  await screen.unmount();
});

test("扫码登录被拒时回调脱敏诊断：截断能从与上一枪的关系看出来，且不含条码内容", async () => {
  const signIn = jest.fn(async () => {
    throw new HbposApiError("rejected", {
      kind: "http",
      status: 401,
      code: "CASHIER_LOGIN_FAILED",
    });
  });
  const onLoginDiagnostic = jest.fn();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onLoginDiagnostic={onLoginDiagnostic}
      onSuccess={jest.fn()}
      runtime={runtime(signIn)}
    />,
  );
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "1042701");
  });
  await act(async () => {
    fireEvent(screen.getByTestId("cashier-login-barcode"), "submitEditing", {
      nativeEvent: { text: "1042701" },
    });
  });
  await waitFor(() => expect(onLoginDiagnostic).toHaveBeenCalledTimes(1));
  await act(async () => {
    fireEvent.changeText(screen.getByTestId("cashier-login-barcode"), "10427011042");
  });
  await act(async () => {
    fireEvent(screen.getByTestId("cashier-login-barcode"), "submitEditing", {
      nativeEvent: { text: "10427011042" },
    });
  });
  await waitFor(() => expect(onLoginDiagnostic).toHaveBeenCalledTimes(2));
  expect(signIn.mock.calls).toEqual([["1042701"], ["1042"]]);
  expect(onLoginDiagnostic.mock.calls[0]?.[0]).toMatchObject({
    outcome: "rejected",
    trigger: "enter",
    submittedLength: 7,
    errorCode: "http:401:CASHIER_LOGIN_FAILED",
  });
  expect(onLoginDiagnostic.mock.calls[1]?.[0]).toMatchObject({
    submittedLength: 4,
    relationToPrevious: "prefix-of-previous",
    trace: expect.objectContaining({ nativeTextLength: 11, staleNativePrefixLength: 7 }),
  });
  for (const [diagnostic] of onLoginDiagnostic.mock.calls) {
    expect(JSON.stringify(diagnostic)).not.toMatch(/1042/u);
  }
  await screen.unmount();
});

test("清除按钮使用 close（×）图标，与键盘/可见性按钮同排渲染", async () => {
  mockMaterialCommunityIcons.mockClear();
  const screen = await render(
    <CashierLoginScreen
      controller={new CashierLoginController(store())}
      language="zh"
      onSuccess={jest.fn()}
      runtime={runtime(async () => cashier())}
    />,
  );

  await fireEvent.changeText(
    screen.getByTestId("cashier-login-barcode"),
    "2947858456543",
  );
  expect(screen.getByTestId("cashier-login-clear-barcode")).toBeTruthy();

  // 清除按钮图标为 close（×）
  const closeIconCall = mockMaterialCommunityIcons.mock.calls
    .map((call) => call[0] as Readonly<{ name?: string }>)
    .find((props) => props.name === "close");
  expect(closeIconCall).toBeTruthy();

  // 三个按钮与输入框同排（inputRow 内），在输入框右侧外面而非绝对定位覆盖框内
  const input = screen.getByTestId("cashier-login-barcode");
  const keyboard = screen.getByTestId("cashier-login-show-keyboard");
  const visibility = screen.getByTestId("cashier-login-toggle-barcode-visibility");
  expect(input).toBeTruthy();
  expect(keyboard).toBeTruthy();
  expect(visibility).toBeTruthy();
});
