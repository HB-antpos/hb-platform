import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render } from "@testing-library/react-native";
import { createRef } from "react";
import { ScrollView, TextInput } from "react-native";

import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "./pos-keyboard-aware-scroll-view";

describe("PosKeyboardAwareScrollView", () => {
  it("固定系统键盘滚动策略并完整转发 ScrollView 属性与 ref", async () => {
    const ref = createRef<ScrollView>();
    const screen = await render(
      <PosKeyboardAwareScrollView
        accessibilityLabel="Keyboard aware form"
        automaticallyAdjustKeyboardInsets={false}
        contentContainerStyle={{ padding: 24 }}
        keyboardDismissMode="none"
        keyboardShouldPersistTaps="always"
        ref={ref}
        testID="keyboard-aware-scroll"
      />,
    );

    const scroll = screen.getByTestId("keyboard-aware-scroll");
    expect(scroll.props.accessibilityLabel).toBe("Keyboard aware form");
    expect(scroll.props.contentContainerStyle).toEqual({ padding: 24 });
    expect(scroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
    expect(scroll.props.keyboardDismissMode).toBe("interactive");
    expect(scroll.props.keyboardShouldPersistTaps).toBe("handled");
    expect(ref.current).not.toBeNull();
  });

  it("系统键盘输入聚焦时按默认间距滚到键盘上方并组合调用 onFocus", async () => {
    const revealFocusedInput = jest.spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    );
    const onFocus = jest.fn();
    const screen = await render(
      <PosKeyboardAwareScrollView testID="keyboard-aware-scroll">
        <PosKeyboardAwareTextInput
          accessibilityHint="Type a value"
          onFocus={onFocus}
          placeholder="Value"
          testID="keyboard-aware-input"
        />
      </PosKeyboardAwareScrollView>,
    );
    const focusEvent = { target: 202 };

    await fireEvent(
      screen.getByTestId("keyboard-aware-input"),
      "focus",
      focusEvent,
    );

    expect(revealFocusedInput).toHaveBeenCalledTimes(1);
    expect(revealFocusedInput).toHaveBeenCalledWith(202, 16, true);
    expect(onFocus).toHaveBeenCalledTimes(1);
    expect(onFocus).toHaveBeenCalledWith(focusEvent);
    expect(
      screen.getByTestId("keyboard-aware-input").props.accessibilityHint,
    ).toBe("Type a value");
    expect(screen.getByTestId("keyboard-aware-input").props.placeholder).toBe(
      "Value",
    );

    revealFocusedInput.mockRestore();
  });

  it("允许页面覆盖焦点滚动间距", async () => {
    const revealFocusedInput = jest.spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    );
    const screen = await render(
      <PosKeyboardAwareScrollView keyboardRevealOffset={32}>
        <PosKeyboardAwareTextInput testID="custom-offset-input" />
      </PosKeyboardAwareScrollView>,
    );

    await fireEvent(screen.getByTestId("custom-offset-input"), "focus", {
      target: 303,
    });

    expect(revealFocusedInput).toHaveBeenCalledWith(303, 32, true);
    revealFocusedInput.mockRestore();
  });

  it("禁用系统键盘时不主动滚动，但仍转发输入属性、ref 与 onFocus", async () => {
    const revealFocusedInput = jest.spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    );
    const ref = createRef<TextInput>();
    const onChangeText = jest.fn();
    const onFocus = jest.fn();
    const screen = await render(
      <PosKeyboardAwareScrollView>
        <PosKeyboardAwareTextInput
          onChangeText={onChangeText}
          onFocus={onFocus}
          ref={ref}
          showSoftInputOnFocus={false}
          testID="hid-safe-input"
          value=""
        />
      </PosKeyboardAwareScrollView>,
    );
    const input = screen.getByTestId("hid-safe-input");
    const focusEvent = { target: 404 };

    await fireEvent(input, "focus", focusEvent);
    await fireEvent.changeText(input, "HID-001");

    expect(revealFocusedInput).not.toHaveBeenCalled();
    expect(onFocus).toHaveBeenCalledWith(focusEvent);
    expect(onChangeText).toHaveBeenCalledWith("HID-001");
    expect(input.props.showSoftInputOnFocus).toBe(false);
    expect(ref.current).not.toBeNull();

    revealFocusedInput.mockRestore();
  });

  it("已聚焦输入切换为系统键盘模式时主动揭示当前输入", async () => {
    const revealFocusedInput = jest.spyOn(
      ScrollView.prototype,
      "scrollResponderScrollNativeHandleToKeyboard",
    );
    const screen = await render(
      <PosKeyboardAwareScrollView>
        <PosKeyboardAwareTextInput
          showSoftInputOnFocus={false}
          testID="manual-keyboard-input"
        />
      </PosKeyboardAwareScrollView>,
    );

    await fireEvent(screen.getByTestId("manual-keyboard-input"), "focus", {
      target: 606,
    });
    expect(revealFocusedInput).not.toHaveBeenCalled();

    await screen.rerender(
      <PosKeyboardAwareScrollView>
        <PosKeyboardAwareTextInput
          showSoftInputOnFocus
          testID="manual-keyboard-input"
        />
      </PosKeyboardAwareScrollView>,
    );

    expect(revealFocusedInput).toHaveBeenCalledTimes(1);
    expect(revealFocusedInput).toHaveBeenCalledWith(606, 16, true);
    revealFocusedInput.mockRestore();
  });

  it("脱离 ScrollView provider 仍保留输入行为且不会崩溃", async () => {
    const ref = createRef<TextInput>();
    const onChangeText = jest.fn();
    const onFocus = jest.fn();
    const screen = await render(
      <PosKeyboardAwareTextInput
        onChangeText={onChangeText}
        onFocus={onFocus}
        ref={ref}
        testID="standalone-input"
        value=""
      />,
    );
    const input = screen.getByTestId("standalone-input");
    const focusEvent = { target: 505 };

    await expect(
      fireEvent(input, "focus", focusEvent),
    ).resolves.toBeUndefined();
    await fireEvent.changeText(input, "Standalone");

    expect(onFocus).toHaveBeenCalledWith(focusEvent);
    expect(onChangeText).toHaveBeenCalledWith("Standalone");
    expect(ref.current).not.toBeNull();
  });
});

describe("PosKeyboardAwareTextInput 扫码自动提交（对齐手持）", () => {
  // 注意：自动提交的定时器触发后，react-test-renderer 的后续 render 会失效，
  // 因此“触发提交”的场景必须放在本 describe 的最后。
  it("未启用时不触发；手动输入节奏（字符间隔大）也不触发", async () => {
    const plain = jest.fn();
    const manual = jest.fn();
    const screen = await render(
      <>
        <PosKeyboardAwareTextInput
          onChangeText={() => undefined}
          onSubmitEditing={plain}
          testID="plain-input"
        />
        <PosKeyboardAwareTextInput
          autoSubmitOnScanIdle
          onChangeText={() => undefined}
          onSubmitEditing={manual}
          testID="manual-input"
        />
      </>,
    );

    const plainInput = screen.getByTestId("plain-input");
    await fireEvent.changeText(plainInput, "1");
    await fireEvent.changeText(plainInput, "12");
    await fireEvent.changeText(plainInput, "123");
    await fireEvent.changeText(plainInput, "1234");
    const manualInput = screen.getByTestId("manual-input");
    await fireEvent.changeText(manualInput, "9");
    await new Promise((resolve) => setTimeout(resolve, 150));
    await fireEvent.changeText(manualInput, "98");
    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(plain).not.toHaveBeenCalled();
    expect(manual).not.toHaveBeenCalled();
  });

  it("扫码枪快速连发且不带回车时，停顿后自动提交一次（等效回车）", async () => {
    const onSubmitEditing = jest.fn();
    const screen = await render(
      <PosKeyboardAwareTextInput
        autoSubmitOnScanIdle
        onChangeText={() => undefined}
        onSubmitEditing={onSubmitEditing}
        testID="scan-input"
      />,
    );

    const input = screen.getByTestId("scan-input");
    for (const value of ["S", "ST", "STA", "STAF", "STAFF"]) {
      await fireEvent.changeText(input, value);
    }
    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(onSubmitEditing).toHaveBeenCalledTimes(1);
  });
});
