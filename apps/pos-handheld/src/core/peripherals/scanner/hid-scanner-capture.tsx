import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef } from "react";
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

/** keyPress 回车后等 submitEditing 带回原生全文；没等到才用已收到的文本兜底提交。 */
const ENTER_FALLBACK_MS = 120;

/**
 * 蓝牙 HID 在手持系统中被当作普通键盘。组件仅在这个输入框实际聚焦时接收字符，
 * 没有焦点时通过 status=inactive 显式暴露，绝不声称存在全局捕获能力。
 *
 * 关键：输入框是「非受控」的，JS 从不回写 value。新架构下受控 TextInput 的回写与清空
 * 要带 JS 侧的事件计数，扫码器注入比 JS 处理快时计数对不上，原生会静默丢弃这次写入，
 * JS 却以为已清空，两边从此错位，表现为第二枪起条码被截断、或和上一枪残留拼成乱码。
 * 现在以原生回传的文本为准：记住「已消费到哪一段」，只把其后新增的部分交给路由器，
 * 原生清空成功或被丢弃都不会串码。未带回车的扫码器仍按停顿自动提交（flushPartialIfIdle）。
 *
 * 安卓上 JS 清空成功时不回传文本变化事件，DataWedge 整段写入时单看文本分不清「清空成功后写入一个
 * 恰好以上一枪开头的新码」和「清空被丢弃后追加」；清空成功时光标回到 0 并派发选区事件，
 * 所以请求清空后、下一次文本变化前收到选区 (0,0)，就确认原生已空，已消费前缀作废。
 */
export const HidScannerCapture = forwardRef<HidScannerCaptureHandle, HidScannerCaptureProps>(
  ({ active, focusRequestKey, onCaptureStatusChange, onHidTextChange, scanner }, ref) => {
    const inputRef = useRef<TextInputInstance>(null);
    const idleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const focusTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const enterFallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    // 原生输入框最近一次回传的完整文本。
    const seenTextRef = useRef("");
    // 原生文本中已经提交或丢弃过的前缀；原生清空被丢弃时，残留就是这一段。
    const consumedTextRef = useRef("");
    // 已请求原生清空、还没收到下一次文本变化：此时的选区 (0,0) 才算「清空成功」的确认。
    const awaitingClearConfirmationRef = useRef(false);

    const clearEnterFallbackTimer = (): void => {
      if (enterFallbackTimerRef.current) {
        clearTimeout(enterFallbackTimerRef.current);
        enterFallbackTimerRef.current = null;
      }
    };

    /** 当前原生文本里尚未消费的部分；原生已被清空（不再以已消费前缀开头）时整段都算新输入。 */
    const pendingTextOf = (nativeText: string): string => {
      const consumed = consumedTextRef.current;
      if (!nativeText.startsWith(consumed)) {
        consumedTextRef.current = "";
        return nativeText;
      }
      return nativeText.slice(consumed.length);
    };

    /** 把原生现有文本整体记为已消费，并尽力清空原生输入框（被原生丢弃也无妨，下次按差量取）。 */
    const consumeNativeText = (nativeText: string): void => {
      consumedTextRef.current = nativeText;
      if (nativeText) {
        awaitingClearConfirmationRef.current = true;
        inputRef.current?.clear();
      }
    };

    const submitNativeText = (nativeText: string | undefined): void => {
      clearEnterFallbackTimer();
      const text = nativeText ?? seenTextRef.current;
      seenTextRef.current = text;
      const pending = pendingTextOf(text);
      // 先同步记为已消费：同一次回车的 keyPress 与 submitEditing 先后到达时，第二次取到空串不会重放。
      consumeNativeText(text);
      if (!pending) return;
      scanner.submitTextInput(pending);
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
          consumeNativeText(seenTextRef.current);
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
        clearEnterFallbackTimer();
        consumeNativeText(seenTextRef.current);
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
      clearEnterFallbackTimer();
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
          // 失焦时路由器会丢弃半段输入；原生残留同步记为已消费，避免重新聚焦后与下一枪拼接。
          consumeNativeText(seenTextRef.current);
          updateCaptureState(false);
          scheduleFocus();
        }}
        onChangeText={(nextValue) => {
          if (nextValue && nextValue !== seenTextRef.current) {
            onHidTextChange?.();
          }
          seenTextRef.current = nextValue;
          // 清空后的第一次文本变化已到：之后的选区事件只是光标随输入移动，不再代表清空结果。
          awaitingClearConfirmationRef.current = false;
          // 收到变化事件就说明原生内容确实变了；不比已消费前缀长，只能是原生已清空后重新输入。
          if (nextValue.length <= consumedTextRef.current.length) {
            consumedTextRef.current = "";
          }
          // 路由器只看「未消费部分」，它内部再按前缀差量逐字符拼码、遇结束符提交。
          const result = scanner.acceptTextInputValue(pendingTextOf(nextValue));
          if (result.emitted || result.overflowed) {
            // 文本里带了回车 / Tab 已提交，或超长已丢弃：整段原生文本都算处理过。
            consumeNativeText(nextValue);
          }
          scheduleIdleReset();
        }}
        onFocus={() => updateCaptureState(true)}
        onKeyPress={(event) => {
          if (event.nativeEvent.key !== "Enter") return;
          // keyPress 不带原生全文；正常情况下紧跟着的 submitEditing 会带全文提交并取消兜底。
          clearEnterFallbackTimer();
          enterFallbackTimerRef.current = setTimeout(() => {
            enterFallbackTimerRef.current = null;
            submitNativeText(undefined);
          }, ENTER_FALLBACK_MS);
        }}
        onSelectionChange={(event) => {
          const selection = event?.nativeEvent?.selection;
          if (
            !awaitingClearConfirmationRef.current ||
            selection?.start !== 0 ||
            selection?.end !== 0
          ) {
            return;
          }
          // 安卓 JS 清空成功只派发选区事件（光标回到 0）：确认原生已空，下一枪整段都是新码。
          awaitingClearConfirmationRef.current = false;
          consumedTextRef.current = "";
          seenTextRef.current = "";
        }}
        onSubmitEditing={(event) => {
          submitNativeText(event?.nativeEvent?.text);
        }}
        showSoftInputOnFocus={false}
        style={styles.hiddenInput}
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
