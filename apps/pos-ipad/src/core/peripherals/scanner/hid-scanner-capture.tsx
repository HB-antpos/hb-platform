import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef } from "react";
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
 * 未带回车的残段停留多久才丢弃。只用于清掉误触按键，不参与正常扫码拼码，
 * 所以取得比扫码枪整码输入时间（通常 < 150ms）宽得多，JS 线程短暂卡顿也不会把一枪拆成两段。
 */
const IDLE_DISCARD_MS = 600;
/** keyPress 回车后等 submitEditing 带回原生全文；没等到才用已收到的文本兜底提交。 */
const ENTER_FALLBACK_MS = 120;
const TERMINATOR_PATTERN = /[\r\n\t]/g;

/**
 * 蓝牙 / USB HID 在 iPadOS 被当作普通键盘。组件仅在这个输入框实际聚焦时接收字符，
 * 没有焦点时通过 status=inactive 显式暴露，绝不声称存在全局捕获能力。
 *
 * 关键：输入框是「非受控」的，JS 从不回写 value。新架构下受控 TextInput 的回写与清空
 * 要带 JS 侧的事件计数，扫码枪敲字比 JS 处理快时计数对不上，原生会静默丢弃这次写入，
 * JS 却以为已清空，两边从此错位，表现为第二枪起条码被截断、或和上一枪残留拼成乱码。
 * 现在以原生回传的文本为准：记住「已消费到哪一段」，每次只取其后新增的部分，
 * 原生清空成功或被丢弃都不会串码。
 */
export const HidScannerCapture = forwardRef<HidScannerCaptureHandle, HidScannerCaptureProps>(
  ({ active, focusRequestKey, onCaptureStatusChange, onHidTextChange, scanner }, ref) => {
    usePosThemeVersion();
    const inputRef = useRef<TextInputInstance>(null);
    const idleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const enterFallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    // 原生输入框最近一次回传的完整文本。
    const seenTextRef = useRef("");
    // 原生文本中已经提交或丢弃过的前缀；原生清空被丢弃时，残留就是这一段。
    const consumedTextRef = useRef("");

    const clearIdleTimer = (): void => {
      if (idleTimerRef.current) {
        clearTimeout(idleTimerRef.current);
        idleTimerRef.current = null;
      }
    };
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
        inputRef.current?.clear();
      }
    };

    const scheduleIdleDiscard = (): void => {
      clearIdleTimer();
      idleTimerRef.current = setTimeout(() => {
        idleTimerRef.current = null;
        // 停顿后仍未回车的残段直接丢弃；同时补一次清空，回收之前被原生丢弃的清空请求。
        consumeNativeText(seenTextRef.current);
      }, IDLE_DISCARD_MS);
    };

    const submitNativeText = (nativeText: string | undefined): void => {
      clearEnterFallbackTimer();
      const text = nativeText ?? seenTextRef.current;
      seenTextRef.current = text;
      const pending = pendingTextOf(text);
      // 先同步记为已消费：同一次回车的 keyPress 与 submitEditing 先后到达时，第二次取到空串不会重放。
      consumeNativeText(text);
      scheduleIdleDiscard();
      if (!pending) return;
      scanner.submitTextInput(pending);
    };

    const updateCaptureState = useCallback((nextFocused: boolean) => {
      scanner.setCaptureActive(active && nextFocused);
      onCaptureStatusChange?.(scanner.getCaptureStatus());
    }, [active, onCaptureStatusChange, scanner]);

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
        clearIdleTimer();
        consumeNativeText(seenTextRef.current);
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
      clearIdleTimer();
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
          // 失焦时丢弃半段输入，避免重新聚焦后与下一枪拼接。
          consumeNativeText(seenTextRef.current);
          updateCaptureState(false);
        }}
        onChangeText={(nextValue) => {
          if (nextValue && nextValue !== seenTextRef.current) {
            onHidTextChange?.();
          }
          seenTextRef.current = nextValue;
          // 收到变化事件就说明原生内容确实变了；不比已消费前缀长，只能是原生已清空后重新输入。
          if (nextValue.length <= consumedTextRef.current.length) {
            consumedTextRef.current = "";
          }
          const pending = pendingTextOf(nextValue);
          // 少数键盘把回车 / Tab 当字符写进文本：提交到最后一个结束符为止，剩下的半段继续等待。
          let lastTerminatorEnd = -1;
          for (const match of pending.matchAll(TERMINATOR_PATTERN)) {
            lastTerminatorEnd = (match.index ?? 0) + match[0].length;
          }
          if (lastTerminatorEnd > 0) {
            consumedTextRef.current += pending.slice(0, lastTerminatorEnd);
            scanner.submitTextInput(pending.slice(0, lastTerminatorEnd));
          }
          scheduleIdleDiscard();
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
        onSubmitEditing={(event) => {
          submitNativeText(event.nativeEvent.text);
        }}
        showSoftInputOnFocus={false}
        style={styles.hiddenInput}
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
