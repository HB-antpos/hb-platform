import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from "react";
import { TextInput, type TextInput as TextInputInstance } from "react-native";

import { HidScannerRouter } from "./hid-scanner";
import type { ScannerCaptureStatus } from "./types";
import { createPosStyles, usePosThemeVersion } from "@/ui/theme";

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
 * 蓝牙 HID 在 iPadOS 被当作普通键盘。组件仅在这个输入框实际聚焦时接收字符，
 * 没有焦点时通过 status=inactive 显式暴露，绝不声称存在全局捕获能力。
 */
export const HidScannerCapture = forwardRef<HidScannerCaptureHandle, HidScannerCaptureProps>(
  ({ active, focusRequestKey, onCaptureStatusChange, onHidTextChange, scanner }, ref) => {
    usePosThemeVersion();
    const inputRef = useRef<TextInputInstance>(null);
    const idleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const [value, setValue] = useState("");
    const valueRef = useRef("");
    const setCapturedValue = (nextValue: string): void => {
      valueRef.current = nextValue;
      setValue(nextValue);
    };
    const submitCapturedValue = (): void => {
      const capturedValue = valueRef.current;
      if (!capturedValue) return;
      // iPad HID 回车可能连续触发 keyPress 与 submitEditing；同步清空可阻止第二次重放。
      valueRef.current = "";
      const result = scanner.submitTextInput(capturedValue);
      setCapturedValue(result.valueToRender);
    };
    const updateCaptureState = useCallback((nextFocused: boolean) => {
      scanner.setCaptureActive(active && nextFocused);
      onCaptureStatusChange?.(scanner.getCaptureStatus());
    }, [active, onCaptureStatusChange, scanner]);

    const scheduleIdleReset = () => {
      if (idleTimerRef.current) {
        clearTimeout(idleTimerRef.current);
      }
      idleTimerRef.current = setTimeout(() => {
        idleTimerRef.current = null;
        if (scanner.resetPartialIfIdle()) {
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
      const focusTimer = setTimeout(() => inputRef.current?.focus(), 0);
      return () => clearTimeout(focusTimer);
    }, [active, updateCaptureState]);

    useEffect(() => {
      if (active) {
        inputRef.current?.focus();
      }
    }, [active, focusRequestKey]);

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
        onBlur={() => updateCaptureState(false)}
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

const styles = createPosStyles((colors) => ({
  hiddenInput: {
    height: 1,
    left: -10_000,
    opacity: 0,
    position: "absolute",
    top: -10_000,
    width: 1,
  },
}));
