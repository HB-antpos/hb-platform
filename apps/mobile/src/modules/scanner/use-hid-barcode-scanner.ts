import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppState } from "react-native";
import type { NativeSyntheticEvent, TextInputSubmitEditingEventData, TextInput } from "react-native";
import { getItemAsync, setItemAsync } from "expo-secure-store";
import {
  createHidBarcodeKeyBuffer,
  extractNewHidBarcodeSegment,
  normalizeHidBarcode,
  type HidBarcodeKeyEvent,
} from "./hid-barcode-buffer";
import { createHiddenInputFocusController, type HiddenInputFocusController } from "./hid-hidden-input-focus";
import { acquireHidNativeListenerLease } from "./hid-native-listener-lifecycle";
import {
  getHidScannerForceTextInput,
  setHidScannerForceTextInput,
  setHidScannerForceTextInputIfUnset,
  subscribeHidScannerMode,
} from "./hid-scanner-mode-state";
import { createHidScannerEnabledGate } from "./hid-scanner-enabled-gate";

interface UseHidBarcodeScannerOptions {
  enabled?: boolean;
  idleMs?: number;
  minLength?: number;
  /**
   * 只用隐藏 TextInput 接收扫码，不订阅原生按键监听。
   * 原生监听视图挂在 Activity 根视图上，Modal（底部弹层）是独立窗口收不到按键，弹层内必须用这个模式。
   */
  textInputOnly?: boolean;
  onScan: (barcode: string) => void | Promise<void>;
}

const STORAGE_KEY = "hid_scanner_force_text_input";
const DUPLICATE_TEXT_INPUT_SUPPRESS_MS = 100;

let nativeModuleRef: any = null;

function getNativeModule() {
  if (nativeModuleRef !== null) {
    return nativeModuleRef;
  }
  try {
    const { requireNativeModule } = require("expo-modules-core") as typeof import("expo-modules-core");
    nativeModuleRef = requireNativeModule("ExpoKeyEvent");
  } catch {
    nativeModuleRef = undefined;
  }
  return nativeModuleRef;
}

async function loadPersistedMode(): Promise<boolean | null> {
  const currentMode = getHidScannerForceTextInput();
  if (currentMode !== null) {
    return currentMode;
  }
  try {
    const value = await getItemAsync(STORAGE_KEY);
    if (value === "true") {
      setHidScannerForceTextInputIfUnset(true);
      return true;
    }
    if (value === "false") {
      setHidScannerForceTextInputIfUnset(false);
      return false;
    }
  } catch {
    // ignore
  }
  return null;
}

async function persistForceTextInput(value: boolean) {
  setHidScannerForceTextInput(value);
  try {
    await setItemAsync(STORAGE_KEY, value ? "true" : "false");
  } catch {
    // ignore
  }
}

export function getHidScannerAvailability() {
  return getNativeModule() != null;
}

export function useHidBarcodeScanner({
  enabled = true,
  idleMs = 50,
  minLength = 3,
  textInputOnly = false,
  onScan,
}: UseHidBarcodeScannerOptions) {
  const onScanRef = useRef(onScan);
  onScanRef.current = onScan;

  const [textInputValue, setTextInputValue] = useState("");
  const textInputTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const textInputRef = useRef<TextInput>(null);
  const focusTargetRef = useRef<() => void>(() => {});
  const focusControllerRef = useRef<HiddenInputFocusController | null>(null);
  const enabledGateRef = useRef(createHidScannerEnabledGate(enabled));
  enabledGateRef.current.setEnabled(enabled);

  const [forceTextInput, setForceTextInput] = useState<boolean>(() => getHidScannerForceTextInput() === true);
  const lastSubmittedRef = useRef("");
  const lastSubmittedTimeRef = useRef(0);
  const lastSubmittedRawValueRef = useRef("");

  const switchToTextInputMode = useCallback(() => {
    if (getHidScannerForceTextInput() === true) {
      return;
    }
    console.log("[HID-Scanner] scanner uses unmapped keyCodes, switching to TextInput mode");
    void persistForceTextInput(true);
  }, []);

  const nativeKeyBuffer = useMemo(() => createHidBarcodeKeyBuffer({
    idleMs,
    minLength,
    onBarcode: (barcode) => {
      if (enabledGateRef.current.isEnabled()) {
        onScanRef.current?.(barcode);
      }
    },
    onFallbackToTextInput: switchToTextInputMode,
  }), [idleMs, minLength, switchToTextInputMode]);

  // 隐藏输入框只在扫码场景抢焦点，避免覆盖用户正在编辑的可见输入框。
  focusTargetRef.current = () => {
    if (enabled) {
      textInputRef.current?.focus();
    }
  };

  if (!focusControllerRef.current) {
    focusControllerRef.current = createHiddenInputFocusController(() => focusTargetRef.current());
  }

  const submitBarcode = useCallback(
    (rawValue: string) => {
      if (!enabledGateRef.current.isEnabled()) {
        lastSubmittedRawValueRef.current = "";
        return;
      }
      const normalizedRawValue = normalizeHidBarcode(rawValue);
      const barcode = extractNewHidBarcodeSegment(normalizedRawValue, lastSubmittedRawValueRef.current);
      if (!barcode || barcode.length < minLength) {
        return;
      }

      const now = Date.now();
      if (barcode === lastSubmittedRef.current && now - lastSubmittedTimeRef.current < DUPLICATE_TEXT_INPUT_SUPPRESS_MS) {
        lastSubmittedRawValueRef.current = normalizedRawValue;
        return;
      }
      lastSubmittedRef.current = barcode;
      lastSubmittedTimeRef.current = now;
      lastSubmittedRawValueRef.current = normalizedRawValue;

      onScanRef.current?.(barcode);
    },
    [minLength],
  );

  const handleTextInputChange = useCallback(
    (nextValue: string) => {
      if (!enabledGateRef.current.isEnabled()) {
        if (textInputTimerRef.current) {
          clearTimeout(textInputTimerRef.current);
          textInputTimerRef.current = null;
        }
        setTextInputValue("");
        return;
      }

      setTextInputValue(nextValue);
      if (textInputTimerRef.current) {
        clearTimeout(textInputTimerRef.current);
      }
      // 隐藏 TextInput 模式兼容不能提供 character 的扫码枪，仍按输入停顿自动提交。
      const canSubmit = enabledGateRef.current.captureSubmission();
      textInputTimerRef.current = setTimeout(() => {
        textInputTimerRef.current = null;
        if (canSubmit()) {
          submitBarcode(nextValue);
        }
        setTextInputValue("");
      }, idleMs);
    },
    [idleMs, submitBarcode],
  );

  const handleTextInputSubmit = useCallback(
    (event: NativeSyntheticEvent<TextInputSubmitEditingEventData>) => {
      if (textInputTimerRef.current) {
        clearTimeout(textInputTimerRef.current);
        textInputTimerRef.current = null;
      }
      if (!enabledGateRef.current.isEnabled()) {
        setTextInputValue("");
        lastSubmittedRawValueRef.current = "";
        return;
      }
      const value = event.nativeEvent.text || textInputValue;
      submitBarcode(value);
      setTextInputValue("");
    },
    [submitBarcode, textInputValue],
  );

  const focusHiddenInput = useCallback(() => {
    focusControllerRef.current?.focusIfAllowed();
  }, []);

  const pauseHiddenInputFocus = useCallback(() => {
    focusControllerRef.current?.pauseHiddenInputFocus();
  }, []);

  const resumeHiddenInputFocus = useCallback((options?: { refocus?: boolean }) => {
    focusControllerRef.current?.resumeHiddenInputFocus(options);
  }, []);

  useEffect(() => {
    return subscribeHidScannerMode(setForceTextInput);
  }, []);

  useEffect(() => {
    void loadPersistedMode();
  }, []);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const interval = setInterval(() => {
      focusHiddenInput();
    }, 3000);

    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        setTimeout(() => focusHiddenInput(), 100);
      }
    });

    return () => {
      clearInterval(interval);
      subscription.remove();
    };
  }, [enabled, focusHiddenInput]);

  useEffect(() => {
    if ((forceTextInput || textInputOnly) && enabled) {
      setTimeout(() => focusHiddenInput(), 50);
    }
  }, [forceTextInput, textInputOnly, enabled, focusHiddenInput]);

  useEffect(() => {
    if (enabled) {
      return;
    }

    if (textInputTimerRef.current) {
      clearTimeout(textInputTimerRef.current);
      textInputTimerRef.current = null;
    }
    setTextInputValue("");
    lastSubmittedRawValueRef.current = "";
    lastSubmittedRef.current = "";
    lastSubmittedTimeRef.current = 0;
    textInputRef.current?.blur();
    nativeKeyBuffer.dispose();
  }, [enabled, nativeKeyBuffer]);

  useEffect(() => {
    return () => {
      if (textInputTimerRef.current) {
        clearTimeout(textInputTimerRef.current);
      }
      nativeKeyBuffer.dispose();
    };
  }, [nativeKeyBuffer]);

  useEffect(() => {
    const mod = getNativeModule();
    if (!mod) {
      return;
    }

    const onKeyPress = (event: HidBarcodeKeyEvent) => {
      if (!enabled) {
        return;
      }

      nativeKeyBuffer.handleKeyPress(event);
    };

    const lease = acquireHidNativeListenerLease({
      module: mod,
      enabled,
      nativeMode: !forceTextInput && !textInputOnly,
      onKeyPress,
    });

    return () => lease.release();
  }, [forceTextInput, textInputOnly, enabled, nativeKeyBuffer]);

  if (getNativeModule() && !forceTextInput && !textInputOnly) {
    return {
      mode: "native" as const,
      textInputProps: null,
      focusHiddenInput: null,
      pauseHiddenInputFocus,
      resumeHiddenInputFocus,
    };
  }

  return {
    mode: "textInput" as const,
    textInputProps: {
      ref: textInputRef,
      value: textInputValue,
      onChangeText: handleTextInputChange,
      onSubmitEditing: handleTextInputSubmit,
      autoCapitalize: "none" as const,
      autoCorrect: false,
      editable: enabled,
      blurOnSubmit: false,
      showSoftInputOnFocus: false,
      caretHidden: true,
      contextMenuHidden: true,
    },
    focusHiddenInput,
    pauseHiddenInputFocus,
    resumeHiddenInputFocus,
  };
}
