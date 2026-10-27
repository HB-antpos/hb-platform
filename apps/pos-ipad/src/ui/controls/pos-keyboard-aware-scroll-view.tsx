import {
  createContext,
  forwardRef,
  useCallback,
  useContext,
  useEffect,
  useRef,
} from "react";
import {
  ScrollView,
  type FocusEvent,
  type ScrollViewProps,
  type TextInput,
} from "react-native";

import {
  PosTextInput,
  type PosTextInputProps,
} from "@/ui/controls/pos-text-input";

const DEFAULT_KEYBOARD_REVEAL_OFFSET = 16;

type KeyboardRevealTarget = Parameters<
  ScrollView["scrollResponderScrollNativeHandleToKeyboard"]
>[0];

type RevealFocusedInput = (target: KeyboardRevealTarget) => void;

const KeyboardRevealContext = createContext<RevealFocusedInput | null>(null);

export type PosKeyboardAwareScrollViewProps = ScrollViewProps &
  Readonly<{ keyboardRevealOffset?: number }>;

export const PosKeyboardAwareScrollView = forwardRef<
  ScrollView,
  PosKeyboardAwareScrollViewProps
>(function PosKeyboardAwareScrollView(
  { keyboardRevealOffset = DEFAULT_KEYBOARD_REVEAL_OFFSET, ...props },
  forwardedRef,
) {
  const scrollViewRef = useRef<ScrollView>(null);

  const setScrollViewRef = useCallback(
    (instance: ScrollView | null) => {
      scrollViewRef.current = instance;
      if (typeof forwardedRef === "function") {
        forwardedRef(instance);
      } else if (forwardedRef) {
        forwardedRef.current = instance;
      }
    },
    [forwardedRef],
  );

  const revealFocusedInput = useCallback(
    (target: KeyboardRevealTarget) => {
      // 系统 inset 先腾出键盘空间，再把真实焦点精确滚到键盘上沿。
      scrollViewRef.current?.scrollResponderScrollNativeHandleToKeyboard(
        target,
        keyboardRevealOffset,
        true,
      );
    },
    [keyboardRevealOffset],
  );

  return (
    <KeyboardRevealContext.Provider value={revealFocusedInput}>
      <ScrollView
        {...props}
        automaticallyAdjustKeyboardInsets
        keyboardDismissMode="interactive"
        keyboardShouldPersistTaps="handled"
        ref={setScrollViewRef}
      />
    </KeyboardRevealContext.Provider>
  );
});

PosKeyboardAwareScrollView.displayName = "PosKeyboardAwareScrollView";

export type PosKeyboardAwareTextInputProps = PosTextInputProps &
  Readonly<{
    /**
     * 启用扫码无回车自动提交（等效回车）。
     * 自动提交以无参形式调用 onSubmitEditing，回调不得依赖 event 参数。
     */
    autoSubmitOnScanIdle?: boolean;
    /**
     * 启用慢速/整串注入的 HID 扫码自动提交（等效回车）：
     * 覆盖字符间隔 60~250ms 的慢速设备与单次整串注入（DataWedge 等）。
     * 该模式与熟练手动输入（间隔 150~250ms）同区间不可区分，停顿 ≥300ms 会误提交；
     * 仅登录页等可重试场景启用（默认关闭，销售/退货/搜索页面保持原快速节奏判定）。
     * 自动提交同样以无参形式调用 onSubmitEditing。
     */
    mediumSpeedHidSubmit?: boolean;
  }>;

// 扫码节奏判定：相邻字符间隔小于该值视为扫码器连发；停止该时长后自动提交（等效回车）。
// 连续 3 个快速间隔（第 4 个字符起）才判定为扫码节奏，降低零星输入的误提交概率。
const SCAN_CHARACTER_GAP_MS = 60;
const SCAN_AUTO_SUBMIT_MS = 180;
const SCAN_RAPID_STREAK_MIN = 3;
// 慢速 HID 注入支持：部分扫码设备（蓝牙 HID/DataWedge 注入）字符间隔大于快速阈值，
// 无法依赖 onKeyPress（注入不经硬件键盘 key 事件），改为按注入特征判定——
// 逐字符间隔 ≤ 中速阈值且累计达到最小长度，或一次跳变注入 ≥2 字符（整串注入），
// 停顿后等效回车提交。
// 已知权衡：熟练手动输入（间隔 150~250ms）与慢速 HID 同区间不可区分，停顿 ≥300ms 会
// 误提交；该模式经 mediumSpeedHidSubmit 显式启用（当前仅登录页，submit 失败可重试），
// 销售/退货/搜索页面保持原快速节奏判定，不扩大误提交窗口。
const HID_MEDIUM_GAP_MS = 250; // 中速注入的相邻字符间隔上限
const HID_MEDIUM_STREAK_MIN = 3; // 中速注入至少连续 3 个快速间隔
const HID_AUTO_SUBMIT_MS = 300; // HID 输入停止该时长后自动提交（等效回车）
const HID_MIN_CHARS = 6; // HID 自动提交的最小字符数，防零星输入误提交

export const PosKeyboardAwareTextInput = forwardRef<
  TextInput,
  PosKeyboardAwareTextInputProps
>(function PosKeyboardAwareTextInput(
  {
    autoSubmitOnScanIdle = false,
    mediumSpeedHidSubmit = false,
    onBlur,
    onChangeText,
    onFocus,
    onSubmitEditing,
    showSoftInputOnFocus,
    ...props
  },
  ref,
) {
  const revealFocusedInput = useContext(KeyboardRevealContext);
  const focusedTargetRef = useRef<KeyboardRevealTarget | null>(null);
  const lastChangeAtRef = useRef(0);
  const rapidStreakRef = useRef(0);
  const mediumStreakRef = useRef(0);
  const prevValueRef = useRef("");
  const scanIdleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // 定时器回调触发提交时读取最新 onSubmitEditing，避免闭包过期。
  // 扫码自动提交等效回车，业务回调不依赖事件参数，故按无参函数包装。
  const onSubmitEditingRef = useRef<(() => void) | undefined>(undefined);
  onSubmitEditingRef.current =
    onSubmitEditing as unknown as (() => void) | undefined;

  const handleChangeText = useCallback(
    (value: string) => {
      const nowMs = Date.now();
      const gap =
        lastChangeAtRef.current === 0
          ? Number.POSITIVE_INFINITY
          : nowMs - lastChangeAtRef.current;
      lastChangeAtRef.current = nowMs;
      // 跳变基准：受控组件以父组件最新 value 为准（blur 重聚焦后显示值仍在，
      // 续输不应误判为整串注入）；非受控输入用内部 prevValueRef。
      const previousValue =
        typeof props.value === "string" ? props.value : prevValueRef.current;
      // 一次跳变注入 ≥2 字符（整串注入/粘贴）：逐字符手动输入不会出现。
      const jumpedByMany = value.length - previousValue.length > 1;
      prevValueRef.current = value;
      const cancelPendingSubmit = () => {
        if (scanIdleTimerRef.current) {
          clearTimeout(scanIdleTimerRef.current);
          scanIdleTimerRef.current = null;
        }
      };
      const scheduleSubmit = (delayMs: number) => {
        cancelPendingSubmit();
        scanIdleTimerRef.current = setTimeout(() => {
          scanIdleTimerRef.current = null;
          onSubmitEditingRef.current?.();
        }, delayMs);
      };
      cancelPendingSubmit();
      // 连续快速输入计数：间隔大或清空输入即中断，至少连续 3 个快速间隔
      // （第 4 个字符起）才视为扫码节奏；手动键盘输入间隔大，不会进入该分支。
      rapidStreakRef.current =
        autoSubmitOnScanIdle && value && gap <= SCAN_CHARACTER_GAP_MS
          ? rapidStreakRef.current + 1
          : 0;
      // 中速注入计数：间隔放宽到中速阈值（覆盖慢速 HID 扫码设备）。
      mediumStreakRef.current =
        mediumSpeedHidSubmit && value && gap <= HID_MEDIUM_GAP_MS
          ? mediumStreakRef.current + 1
          : 0;
      if (
        autoSubmitOnScanIdle &&
        value &&
        gap <= SCAN_CHARACTER_GAP_MS &&
        rapidStreakRef.current >= SCAN_RAPID_STREAK_MIN
      ) {
        scheduleSubmit(SCAN_AUTO_SUBMIT_MS);
      } else if (
        // 慢速 HID / 整串注入：快速节奏（≤60ms）不满足时，只要长度足够且
        // （逐字符间隔在中速阈值内累计 3 次，或一次跳变注入 ≥2 字符），
        // 停顿后即等效回车提交。仅 mediumSpeedHidSubmit 显式启用（登录页等
        // 可重试场景），销售/退货/搜索页面不扩大误提交窗口。
        mediumSpeedHidSubmit &&
        value &&
        value.length >= HID_MIN_CHARS &&
        (mediumStreakRef.current >= HID_MEDIUM_STREAK_MIN || jumpedByMany)
      ) {
        scheduleSubmit(HID_AUTO_SUBMIT_MS);
      }
      onChangeText?.(value);
    },
    [autoSubmitOnScanIdle, mediumSpeedHidSubmit, onChangeText, props.value],
  );

  // 手动提交（回车）时取消挂起的扫码自动提交，避免带回车扫码器双提交；
  // 业务回调仍按原签名接收事件参数。
  const handleSubmitEditing = useCallback<
    NonNullable<PosTextInputProps["onSubmitEditing"]>
  >(
    (event) => {
      if (scanIdleTimerRef.current) {
        clearTimeout(scanIdleTimerRef.current);
        scanIdleTimerRef.current = null;
      }
      rapidStreakRef.current = 0;
      mediumStreakRef.current = 0;
      prevValueRef.current = "";
      onSubmitEditing?.(event);
    },
    [onSubmitEditing],
  );

  const handleFocus = useCallback(
    (event: FocusEvent) => {
      focusedTargetRef.current = event?.target ?? null;
      if (showSoftInputOnFocus !== false && focusedTargetRef.current != null) {
        revealFocusedInput?.(focusedTargetRef.current);
      }
      onFocus?.(event);
    },
    [onFocus, revealFocusedInput, showSoftInputOnFocus],
  );

  const handleBlur = useCallback(
    (event: FocusEvent) => {
      focusedTargetRef.current = null;
      // 失焦后不再自动提交，防止后台弹窗期间误触发；
      // 同时重置节奏计数与时间戳，避免重聚焦后延续上次扫码节奏。
      if (scanIdleTimerRef.current) {
        clearTimeout(scanIdleTimerRef.current);
        scanIdleTimerRef.current = null;
      }
      rapidStreakRef.current = 0;
      mediumStreakRef.current = 0;
      lastChangeAtRef.current = 0;
      prevValueRef.current = "";
      onBlur?.(event);
    },
    [onBlur],
  );

  useEffect(() => {
    if (showSoftInputOnFocus !== true || focusedTargetRef.current == null) {
      return;
    }
    // HID 输入已聚焦时切换软键盘不会再次触发 focus，需主动揭示当前输入。
    revealFocusedInput?.(focusedTargetRef.current);
  }, [revealFocusedInput, showSoftInputOnFocus]);

  useEffect(() => {
    // 受控 value 被外部清空（如清除按钮 setBarcode("")）时，取消挂起的自动提交
    // 并重置节奏计数，避免残留定时器在清空后触发空值提交。
    if (props.value === "") {
      if (scanIdleTimerRef.current) {
        clearTimeout(scanIdleTimerRef.current);
        scanIdleTimerRef.current = null;
      }
      rapidStreakRef.current = 0;
      mediumStreakRef.current = 0;
    }
  }, [props.value]);

  useEffect(
    () => () => {
      // 卸载时清理扫码自动提交定时器。
      if (scanIdleTimerRef.current) {
        clearTimeout(scanIdleTimerRef.current);
        scanIdleTimerRef.current = null;
      }
    },
    [],
  );

  return (
    <PosTextInput
      {...props}
      onBlur={handleBlur}
      onChangeText={handleChangeText}
      onFocus={handleFocus}
      onSubmitEditing={handleSubmitEditing}
      ref={ref}
      showSoftInputOnFocus={showSoftInputOnFocus}
    />
  );
});

PosKeyboardAwareTextInput.displayName = "PosKeyboardAwareTextInput";
