import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from "react";
import { AppState, StyleSheet, TextInput, type TextInput as TextInputInstance } from "react-native";

import { HidScannerRouter } from "./hid-scanner";
import type { ScannerCaptureStatus } from "./types";

export type HidScannerCaptureHandle = {
  focus(): void;
  blur(): void;
  getStatus(): ScannerCaptureStatus;
};

export type HidScannerCaptureProps = {
  scanner: HidScannerRouter;
  active: boolean;
  focusRequestKey?: string | number;
  onCaptureStatusChange?: (status: ScannerCaptureStatus) => void;
  onHidTextChange?: () => void;
};

/**
 * 蓝牙 HID 在手持系统中被当作普通键盘。组件仅在这个输入框实际聚焦时接收字符，
 * 没有焦点时通过 status=inactive 显式暴露，绝不声称存在全局捕获能力。
 */
export const HidScannerCapture = forwardRef<HidScannerCaptureHandle, HidScannerCaptureProps>(
  ({ active, focusRequestKey, onCaptureStatusChange, onHidTextChange, scanner }, ref) => {
    const inputRef = useRef<TextInputInstance>(null);
    const idleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const focusTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const [value, setValue] = useState("");
    const valueRef = useRef("");
    const setCapturedValue = (nextValue: string): void => {
      valueRef.current = nextValue;
      setValue(nextValue);
    };
    const submitCapturedValue = (): void => {
      const capturedValue = valueRef.current;
      if (!capturedValue) return;
      // HID 回车可能连续触发 keyPress 与 submitEditing；同步清空可阻止第二次重放。
      valueRef.current = "";
      const result = scanner.submitTextInput(capturedValue);
      setCapturedValue(result.valueToRender);
    };
    const updateCaptureState = useCallback((nextFocused: boolean) => {
      scanner.setCaptureActive(active && nextFocused);
      onCaptureStatusChange?.(scanner.getCaptureStatus());
    }, [active, onCaptureStatusChange, scanner]);

    const scheduleFocus = useCallback((resetWindowFocus = false) => {
      if (focusTimerRef.current) clearTimeout(focusTimerRef.current);
      if (!active) return;
      // 等原生焦点交接完成；手动输入框已接管时不抢焦点。
      focusTimerRef.current = setTimeout(() => {
        focusTimerRef.current = null;
        if (AppState.currentState === "background" || AppState.currentState === "inactive") return;
        const input = inputRef.current;
        const focusedInput = TextInput.State.currentlyFocusedInput();
        if (!input || (focusedInput && focusedInput !== input)) return;
        // Android 弹窗关闭后 JS 可能仍缓存旧焦点，重新连接原生编辑器供 DataWedge 输出。
        if (resetWindowFocus && focusedInput === input) input.blur();
        input.focus();
      }, 50);
    }, [active]);

    const scheduleIdleReset = () => {
      if (idleTimerRef.current) {
        clearTimeout(idleTimerRef.current);
      }
      idleTimerRef.current = setTimeout(() => {
        idleTimerRef.current = null;
        // 扫码器未带回车时，停顿即自动提交（等效回车）；带回车时回车已先行提交。
        if (scanner.flushPartialIfIdle()) {
          setCapturedValue("");
        }
      }, 85);
    };

    useImperativeHandle(ref, () => ({
      focus() {
        if (active) {
          inputRef.current?.focus();
        }
      },
      blur() {
        inputRef.current?.blur();
      },
      getStatus: () => scanner.getCaptureStatus(),
    }), [active, scanner]);

    useEffect(() => {
      if (!active) {
        inputRef.current?.blur();
        setCapturedValue("");
        updateCaptureState(false);
        return;
      }
      scheduleFocus();
      const focusSubscription = AppState.addEventListener("focus", () => scheduleFocus(true));
      const stateSubscription = AppState.addEventListener("change", (state) => {
        if (state === "active") scheduleFocus(true);
      });
      return () => {
        if (focusTimerRef.current) clearTimeout(focusTimerRef.current);
        focusSubscription.remove();
        stateSubscription.remove();
      };
    }, [active, focusRequestKey, scheduleFocus, updateCaptureState]);

    useEffect(() => () => {
      if (idleTimerRef.current) {
        clearTimeout(idleTimerRef.current);
      }
      scanner.setCaptureActive(false);
    }, [scanner]);

    return (
      <TextInput
        ref={inputRef}
        accessible={false}
        autoCapitalize="none"
        autoCorrect={false}
        blurOnSubmit={false}
        caretHidden
        contextMenuHidden
        editable={active}
        importantForAutofill="no"
        keyboardType="default"
        onBlur={() => {
          updateCaptureState(false);
          scheduleFocus();
        }}
        onChangeText={(nextValue) => {
          if (nextValue && nextValue !== valueRef.current) {
            onHidTextChange?.();
          }
          const result = scanner.acceptTextInputValue(nextValue);
          setCapturedValue(result.valueToRender);
          scheduleIdleReset();
        }}
        onFocus={() => updateCaptureState(true)}
        onKeyPress={(event) => {
          if (event.nativeEvent.key === "Enter") {
            submitCapturedValue();
          }
        }}
        onSubmitEditing={submitCapturedValue}
        showSoftInputOnFocus={false}
        style={styles.hiddenInput}
        value={value}
      />
    );
  },
);

HidScannerCapture.displayName = "HidScannerCapture";

const styles = StyleSheet.create({
  hiddenInput: {
    height: 1,
    left: -10_000,
    opacity: 0,
    position: "absolute",
    top: -10_000,
    width: 1,
  },
});
