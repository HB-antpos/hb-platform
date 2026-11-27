import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  ActivityIndicator,
  Keyboard,
  StyleSheet,
  Text,
  View,
  type NativeSyntheticEvent,
  type ScrollView,
  type TextInput,
  type TextInputSelectionChangeEventData,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  CashierLoginController,
  CashierLoginError,
  type CashierLoginRuntime,
} from "./cashier-login-controller";
import {
  buildCashierLoginDiagnostic,
  type CashierLoginDiagnostic,
  type CashierLoginTrigger,
} from "./cashier-login-diagnostics";
import { useCashierLoginStore } from "./cashier-login-store";

import { HbposApiError } from "@/core/api/hbpos-api";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type LoginCopy = Readonly<{
  eyebrow: string;
  title: string;
  subtitle: string;
  inputLabel: string;
  inputHint: string;
  keyboard: string;
  clearBarcode: string;
  showBarcode: string;
  hideBarcode: string;
  manualEntryHint: string;
  submit: string;
  submitting: string;
  offline: string;
  security: string;
  runtimeNotReady: string;
  locked: string;
  barcodeRequired: string;
  rejected: string;
  emergencyClockRollback: string;
  emergencyExpired: string;
  emergencyWrongStore: string;
  emergencyKeyUnknown: string;
  emergencyInvalid: string;
}>;

const copy: Record<"en" | "zh", LoginCopy> = {
  en: {
    eyebrow: "CASHIER SIGN IN",
    title: "Ready for the next sale",
    subtitle:
      "Scan your cashier barcode or enter it manually. This terminal only accepts approved staff for its assigned store.",
    inputLabel: "Cashier barcode",
    inputHint: "Scan cashier barcode",
    keyboard: "Keyboard",
    clearBarcode: "Clear cashier barcode",
    showBarcode: "Show cashier barcode",
    hideBarcode: "Hide cashier barcode",
    manualEntryHint: 'Scanner ready; tap "Keyboard" for manual entry.',
    submit: "Sign in to checkout",
    submitting: "Checking authorization…",
    offline:
      "Offline sign-in uses this handheld terminal's encrypted cashier cache.",
    security:
      "Your authorization remains in the device keychain and is never shown here.",
    runtimeNotReady:
      "This POS terminal is not ready. Complete device approval or retry startup.",
    locked: "This POS terminal has been locked by server policy.",
    barcodeRequired: "Enter or scan a cashier barcode.",
    rejected:
      "Cashier sign-in was not accepted. Check the barcode or contact a manager.",
    emergencyClockRollback:
      "The system clock is earlier than trusted time. Reconnect and synchronize before retrying emergency sign-in.",
    emergencyExpired:
      "This emergency sign-in QR has expired or is not active yet.",
    emergencyWrongStore: "This emergency sign-in QR belongs to another store.",
    emergencyKeyUnknown:
      "The emergency signing key is unavailable. Connect this handheld terminal and refresh its security keys.",
    emergencyInvalid: "This emergency sign-in QR is invalid.",
  },
  zh: {
    eyebrow: "收银员登录",
    title: "准备开始下一笔收银",
    subtitle:
      "请扫描收银员条码或手动输入。此终端只允许已获本门店授权的员工进入收银。",
    inputLabel: "收银员条码",
    inputHint: "扫描收银员条码",
    keyboard: "键盘",
    clearBarcode: "清除收银员条码",
    showBarcode: "显示收银员条码",
    hideBarcode: "隐藏收银员条码",
    manualEntryHint: "默认使用扫码枪；手动输入请点“键盘”。",
    submit: "进入收银",
    submitting: "正在核验授权…",
    offline: "离线登录只使用本手持终端加密保存的收银员缓存。",
    security: "授权票据只保存在设备钥匙串中，不会显示在此页面。",
    runtimeNotReady: "POS 终端尚未就绪，请完成设备审批或重试启动。",
    locked: "此 POS 终端已被服务端策略锁定。",
    barcodeRequired: "请输入或扫描收银员条码。",
    rejected: "收银员登录未获批准，请检查条码或联系管理员。",
    emergencyClockRollback:
      "系统时间早于可信时间，请联网校时后再重试紧急登录。",
    emergencyExpired: "紧急登录二维码已过期或尚未生效。",
    emergencyWrongStore: "紧急登录二维码不属于当前门店。",
    emergencyKeyUnknown: "紧急登录签名密钥不可用，请联网刷新安全密钥。",
    emergencyInvalid: "紧急登录二维码无效。",
  },
};

export type CashierLoginVersionInfo = Readonly<{
  runtimeVersion: string;
  otaVersion: string;
}>;

const DEFAULT_VERSION_INFO: CashierLoginVersionInfo = Object.freeze({
  runtimeVersion: "unknown",
  otaVersion: "embedded",
});

export function CashierLoginScreen({
  controller,
  language = "zh",
  onLoginDiagnostic,
  onManualInputFocusChange,
  onSwitchLanguage,
  onSuccess,
  runtime,
  versionInfo = DEFAULT_VERSION_INFO,
}: Readonly<{
  controller?: CashierLoginController;
  language?: string;
  /** 扫码登录被拒、或原生与 JS 输入错位后才成功时回调脱敏诊断（不含条码内容），由路由写进中心日志。 */
  onLoginDiagnostic?(diagnostic: CashierLoginDiagnostic): void;
  onManualInputFocusChange?(focused: boolean): void;
  onSwitchLanguage?: () => void;
  onSuccess(): void;
  runtime: CashierLoginRuntime;
  versionInfo?: CashierLoginVersionInfo;
}>) {
  const store = useCashierLoginStore;
  const defaultController = useMemo(
    () => new CashierLoginController(store.getState()),
    [store],
  );
  const activeController = controller ?? defaultController;
  const barcodeRef = useRef<TextInput>(null);
  const keyboardRequestedRef = useRef(false);
  const manualInputActiveRef = useRef(false);
  const manualInputFocusChangeRef = useRef(onManualInputFocusChange);
  manualInputFocusChangeRef.current = onManualInputFocusChange;
  const submittingRef = useRef(false);
  const keyboardRefreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  // 关键：条码输入框是「非受控」的，JS 从不回写 value。RN 新架构下受控 TextInput 的回写 / 清空
  // 要带 JS 侧事件计数，扫码器注入比 JS 处理快时计数对不上，原生会静默丢弃这次写入，
  // JS 却以为已清空，下一枪就和残留拼成错码（服务端表现为「收银员条码无效」）。
  // 现在以原生回传文本为准：记住已提交过的原生前缀，每次只取其后新增的部分。
  // barcode 只是「原生文本中未提交部分」的显示镜像，用于清除按钮与诊断，不再作为提交来源。
  const [barcode, setBarcode] = useState("");
  const nativeTextRef = useRef("");
  const consumedTextRef = useRef("");
  // 已请求原生清空、还没收到下一次文本变化：此时的选区 (0,0) 才算「清空成功」的确认
  // （安卓 JS 清空成功不回传文本事件，只派发选区事件）。
  const awaitingClearConfirmationRef = useRef(false);
  const previousSubmittedRef = useRef<string | null>(null);
  const inputTraceRef = useRef({ firstAt: 0, lastAt: 0, maxGapMs: 0, changes: 0 });
  const onLoginDiagnosticRef = useRef(onLoginDiagnostic);
  onLoginDiagnosticRef.current = onLoginDiagnostic;
  const [barcodeVisible, setBarcodeVisible] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [restoreScannerFocusAfterFailure, setRestoreScannerFocusAfterFailure] =
    useState(false);
  const [showSoftInputOnFocus, setShowSoftInputOnFocus] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const locale = language.toLowerCase().startsWith("zh") ? "zh" : "en";
  const text = copy[locale];
  const blocked = !isReady(runtime);
  const loginSurface =
    runtime.state.phase === "ready" &&
    runtime.state.device === "authorized-online" &&
    error === null
      ? "online-login"
      : "offline-login";

  const clearKeyboardRefreshTimer = useCallback((): void => {
    if (keyboardRefreshTimerRef.current === null) return;
    clearTimeout(keyboardRefreshTimerRef.current);
    keyboardRefreshTimerRef.current = null;
  }, []);

  const keyboardScrollRef = useRef<ScrollView>(null);

  // 中文注释：Android adjustResize 下 RN 不会在键盘弹出时自动滚动到焦点；
  // 键盘弹出后显式滚动输入框到键盘上沿，保证手动输入时输入框可见。
  useEffect(() => {
    const keyboardDidShowSubscription = Keyboard.addListener(
      "keyboardDidShow",
      () => {
        const input = barcodeRef.current;
        if (input == null) return;
        keyboardScrollRef.current?.scrollResponderScrollNativeHandleToKeyboard(
          input,
          16,
          true,
        );
      },
    );
    return () => {
      keyboardDidShowSubscription.remove();
    };
  }, []);

  const resetToScannerMode = useCallback((): void => {
    clearKeyboardRefreshTimer();
    keyboardRequestedRef.current = false;
    setShowSoftInputOnFocus(false);
  }, [clearKeyboardRefreshTimer]);

  const requestManualKeyboard = (): void => {
    keyboardRequestedRef.current = true;
    if (!showSoftInputOnFocus) {
      setShowSoftInputOnFocus(true);
      return;
    }
    // 键盘被手动收起但输入仍聚焦时，先回到扫码模式再刷新原生输入视图。
    setShowSoftInputOnFocus(false);
  };

  const notifyManualInputFocused = (): void => {
    if (manualInputActiveRef.current) return;
    manualInputActiveRef.current = true;
    manualInputFocusChangeRef.current?.(true);
  };

  const handleBarcodeBlur = (): void => {
    resetToScannerMode();
    // 提交期间 editable 变化也可能触发 blur；此时可见输入仍拥有扫码焦点。
    if (submittingRef.current) return;
    if (!manualInputActiveRef.current) return;
    manualInputActiveRef.current = false;
    manualInputFocusChangeRef.current?.(false);
  };

  useEffect(() => {
    if (showSoftInputOnFocus || !keyboardRequestedRef.current) return;
    keyboardRefreshTimerRef.current = setTimeout(() => {
      keyboardRefreshTimerRef.current = null;
      setShowSoftInputOnFocus(true);
    }, 0);
    return clearKeyboardRefreshTimer;
  }, [clearKeyboardRefreshTimer, showSoftInputOnFocus]);

  useEffect(() => {
    if (!showSoftInputOnFocus || !keyboardRequestedRef.current) return;
    keyboardRequestedRef.current = false;
    barcodeRef.current?.focus();
  }, [showSoftInputOnFocus]);

  useEffect(
    () => () => {
      clearKeyboardRefreshTimer();
      if (!manualInputActiveRef.current) return;
      manualInputActiveRef.current = false;
      manualInputFocusChangeRef.current?.(false);
    },
    [clearKeyboardRefreshTimer],
  );

  useEffect(() => {
    if (!restoreScannerFocusAfterFailure || submitting || blocked) {
      return;
    }
    setRestoreScannerFocusAfterFailure(false);
    resetToScannerMode();
    // 核验期间输入框不可编辑，提交时的 clear() 可能没生效；恢复可编辑后若原生里仍只有已提交内容，补一次清空。
    if (
      nativeTextRef.current.length > 0 &&
      nativeTextRef.current === consumedTextRef.current
    ) {
      barcodeRef.current?.clear();
    }
    barcodeRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    barcodeRef.current?.focus();
  }, [
    blocked,
    resetToScannerMode,
    restoreScannerFocusAfterFailure,
    submitting,
  ]);

  /** 原生文本里尚未提交的部分；原生已被清空（不再以已提交前缀开头）时整段都算新输入。 */
  const pendingTextOf = (nativeText: string): string => {
    const consumed = consumedTextRef.current;
    if (!nativeText.startsWith(consumed)) {
      consumedTextRef.current = "";
      return nativeText;
    }
    return nativeText.slice(consumed.length);
  };

  const handleBarcodeChange = (nextValue: string): void => {
    // 清空后的第一次文本变化已到：之后的选区事件只是光标随输入移动，不再代表清空结果。
    awaitingClearConfirmationRef.current = false;
    // 收到变化事件说明原生内容确实变了；不比已提交前缀长，只能是原生已清空后重新输入。
    if (nextValue.length <= consumedTextRef.current.length) {
      consumedTextRef.current = "";
    }
    nativeTextRef.current = nextValue;
    const pending = pendingTextOf(nextValue);
    // 记录本次输入节奏，供诊断判断「一枪是否被拆成多段送达」。
    const now = Date.now();
    const trace = inputTraceRef.current;
    if (pending.length === 0 || trace.changes === 0) {
      inputTraceRef.current = { firstAt: now, lastAt: now, maxGapMs: 0, changes: 1 };
    } else {
      inputTraceRef.current = {
        firstAt: trace.firstAt,
        lastAt: now,
        maxGapMs: Math.max(trace.maxGapMs, now - trace.lastAt),
        changes: trace.changes + 1,
      };
    }
    setBarcode(pending);
  };

  /** 把原生现有文本整体记为已提交并尽力清空；被原生丢弃也无妨，下次按差量取。 */
  const consumeNativeText = (): void => {
    consumedTextRef.current = nativeTextRef.current;
    inputTraceRef.current = { firstAt: 0, lastAt: 0, maxGapMs: 0, changes: 0 };
    if (nativeTextRef.current) {
      awaitingClearConfirmationRef.current = true;
      barcodeRef.current?.clear();
    }
    setBarcode("");
  };

  /** 请求清空后、下一次输入前光标回到 (0,0)：原生清空确实生效，已提交前缀作废，下一枪整段都是新码。 */
  const handleBarcodeSelectionChange = (
    event: NativeSyntheticEvent<TextInputSelectionChangeEventData>,
  ): void => {
    const selection = event?.nativeEvent?.selection;
    if (
      !awaitingClearConfirmationRef.current ||
      selection?.start !== 0 ||
      selection?.end !== 0
    ) {
      return;
    }
    awaitingClearConfirmationRef.current = false;
    consumedTextRef.current = "";
    nativeTextRef.current = "";
  };

  const submit = async (
    trigger: CashierLoginTrigger,
    nativeText?: string,
  ) => {
    if (submittingRef.current || blocked) {
      setError(blocked ? readinessError(runtime, text) : error);
      return;
    }
    setError(null);
    // 回车以 onSubmitEditing 带回的原生全文为准；按钮 / 停顿自动提交没有事件，用最近一次原生回传文本。
    const fullNativeText = nativeText ?? nativeTextRef.current;
    nativeTextRef.current = fullNativeText;
    const submittedBarcode = pendingTextOf(fullNativeText);
    const trace = inputTraceRef.current;
    const inputTrace = {
      nativeTextLength: fullNativeText.length,
      staleNativePrefixLength: fullNativeText.length - submittedBarcode.length,
      jsMirrorMatched: barcode === submittedBarcode,
      inputDurationMs: trace.changes > 0 ? Math.max(0, Date.now() - trace.firstAt) : 0,
      maxCharacterGapMs: trace.maxGapMs,
      changeEvents: trace.changes,
    };
    // 先同步记为已提交：同一次回车重复到达时第二次取到空串；再尽力清空原生输入框，
    // 被原生丢弃也无妨，下一枪按差量只取新增部分，不会和这次拼接。
    consumeNativeText();
    setBarcodeVisible(false);
    const previousSubmitted = previousSubmittedRef.current;
    previousSubmittedRef.current = submittedBarcode.trim();
    submittingRef.current = true;
    setSubmitting(true);
    try {
      await activeController.login(submittedBarcode, runtime);
      if (inputTrace.staleNativePrefixLength > 0 || !inputTrace.jsMirrorMatched) {
        // 成功但原生与 JS 已错位：说明本次修复兜住了旧问题，留一条记录便于现场核实。
        onLoginDiagnosticRef.current?.(
          buildCashierLoginDiagnostic({
            outcome: "accepted-after-mismatch",
            trigger,
            submitted: submittedBarcode,
            previousSubmitted,
            error: null,
            trace: inputTrace,
          }),
        );
      }
      onSuccess();
    } catch (nextError: unknown) {
      // 空框点按钮只是操作提示，不是扫码问题，不写诊断。
      const emptyButtonPress =
        trigger === "button" &&
        nextError instanceof CashierLoginError &&
        nextError.code === "BARCODE_REQUIRED";
      if (!emptyButtonPress) onLoginDiagnosticRef.current?.(
        buildCashierLoginDiagnostic({
          outcome: "rejected",
          trigger,
          submitted: submittedBarcode,
          previousSubmitted,
          error: nextError,
          trace: inputTrace,
        }),
      );
      setError(errorText(nextError, text));
      setRestoreScannerFocusAfterFailure(true);
      Keyboard.dismiss();
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  return (
    <SafeAreaView style={styles.safeArea}>
      <PosStatusStrip
        language={locale}
        {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      />
      <HandheldStateSurface slug={loginSurface} style={styles.stateSurface}>
        <View style={styles.page} testID="cashier-login-page">
          <View style={styles.contextPanel}>
            <Text style={styles.eyebrow}>{text.eyebrow}</Text>
            <Text style={styles.title}>{text.title}</Text>
            <Text style={styles.subtitle}>{text.subtitle}</Text>
            <View style={styles.securityNote}>
              <MaterialCommunityIcons
                color={posColors.green}
                name="shield-check-outline"
                size={22}
              />
              <Text style={styles.securityText}>{text.security}</Text>
            </View>
          </View>

          <PosKeyboardAwareScrollView
            ref={keyboardScrollRef}
            contentContainerStyle={styles.formPanelContent}
            showsVerticalScrollIndicator={false}
            style={styles.formPanel}
            testID="cashier-login-keyboard-scroll"
          >
            <View style={styles.formHeader}>
              <MaterialCommunityIcons
                color={posColors.orange}
                name="barcode-scan"
                size={26}
              />
              <View style={styles.formHeaderCopy}>
                <Text style={styles.formTitle}>{text.inputLabel}</Text>
                <Text style={styles.formHint}>{text.manualEntryHint}</Text>
              </View>
            </View>
            <View style={styles.inputRow}>
              <View style={styles.inputContainer}>
              <PosKeyboardAwareTextInput
                ref={barcodeRef}
                accessibilityLabel={text.inputLabel}
                autoFocus
                autoCapitalize="characters"
                // 扫码器未带回车时按扫码节奏自动提交（等效回车），手动输入不触发。
                autoSubmitOnScanIdle
                // 登录页同时接受慢速/整串注入的 HID 设备（无回车自动提交，失败可重试）。
                mediumSpeedHidSubmit
                autoCorrect={false}
                editable={!submitting && !blocked}
                onBlur={handleBarcodeBlur}
                onChangeText={handleBarcodeChange}
                onFocus={notifyManualInputFocused}
                onSelectionChange={handleBarcodeSelectionChange}
                // 有事件 = 真实回车（扫码器回车后缀 / 键盘），取原生全文；无事件 = 扫码节奏停顿自动提交。
                onSubmitEditing={(event) =>
                  void submit(
                    event?.nativeEvent ? "enter" : "scan-idle",
                    event?.nativeEvent?.text,
                  )
                }
                placeholder={text.inputHint}
                returnKeyType="go"
                secureTextEntry={!barcodeVisible}
                showSoftInputOnFocus={showSoftInputOnFocus}
                style={[
                  styles.input,
                  (submitting || blocked) && styles.inputDisabled,
                ]}
                submitBehavior="submit"
                testID="cashier-login-barcode"
                // 不传 value：非受控，见 nativeTextRef 处注释。
              />
              </View>
              {barcode.length > 0 ? (
                <PosPressable
                  accessibilityLabel={text.clearBarcode}
                  accessibilityRole="button"
                  accessibilityState={{ disabled: submitting || blocked }}
                  disabled={submitting || blocked}
                  onPress={() => {
                    // 非受控：直接清空原生输入框，并把已有内容记为已处理，清空被丢弃也不会带进下一次。
                    consumeNativeText();
                    // 清除后保持输入焦点，便于立即重扫或手动输入。
                    barcodeRef.current?.focus();
                  }}
                  sound="navigate"
                  style={({ pressed }) => [
                    styles.clearButton,
                    (pressed || submitting || blocked) &&
                      styles.visibilityButtonPressed,
                  ]}
                  testID="cashier-login-clear-barcode"
                >
                  <MaterialCommunityIcons
                    color={
                      submitting || blocked
                        ? posColors.mutedInk
                        : posColors.blue
                    }
                    name="close"
                    size={24}
                  />
                </PosPressable>
              ) : null}
              <PosPressable
                accessibilityLabel={text.keyboard}
                accessibilityRole="button"
                accessibilityState={{ disabled: submitting || blocked }}
                disabled={submitting || blocked}
                onPress={requestManualKeyboard}
                sound="navigate"
                style={({ pressed }) => [
                  styles.keyboardIconButton,
                  (pressed || submitting || blocked) &&
                    styles.visibilityButtonPressed,
                ]}
                testID="cashier-login-show-keyboard"
              >
                <MaterialCommunityIcons
                  color={
                    submitting || blocked ? posColors.mutedInk : posColors.blue
                  }
                  name="keyboard-outline"
                  size={23}
                />
              </PosPressable>
              <PosPressable
                accessibilityLabel={
                  barcodeVisible ? text.hideBarcode : text.showBarcode
                }
                accessibilityRole="button"
                accessibilityState={{ disabled: submitting || blocked }}
                disabled={submitting || blocked}
                onPress={() => setBarcodeVisible((visible) => !visible)}
                sound="navigate"
                style={({ pressed }) => [
                  styles.visibilityButton,
                  (pressed || submitting || blocked) &&
                    styles.visibilityButtonPressed,
                ]}
                testID="cashier-login-toggle-barcode-visibility"
              >
                <MaterialCommunityIcons
                  color={
                    submitting || blocked ? posColors.mutedInk : posColors.blue
                  }
                  name={barcodeVisible ? "eye-off-outline" : "eye-outline"}
                  size={23}
                />
              </PosPressable>
            </View>
            {error ? (
            <View
              accessibilityRole="alert"
              style={styles.errorBanner}
              testID="cashier-login-error"
            >
              <Text style={styles.errorText}>{error}</Text>
            </View>
            ) : null}
            <PosPressable
            accessibilityRole="button"
            accessibilityState={{ disabled: submitting || blocked }}
            disabled={submitting || blocked}
            onPress={() => void submit("button")}
            style={({ pressed }) => [
              styles.submit,
              (pressed || submitting || blocked) && styles.submitPressed,
            ]}
            testID="cashier-login-submit"
          >
            {submitting ? (
              <ActivityIndicator color="#FFFFFF" />
            ) : (
              <MaterialCommunityIcons color="#FFFFFF" name="login" size={22} />
            )}
            <Text style={styles.submitLabel}>
              {submitting ? text.submitting : text.submit}
            </Text>
            </PosPressable>
            <View style={styles.offlineNote}>
              <MaterialCommunityIcons
                color={posColors.blue}
                name="database-lock-outline"
                size={20}
              />
              <Text style={styles.offlineText}>{text.offline}</Text>
            </View>
          </PosKeyboardAwareScrollView>
          <Text
            numberOfLines={1}
            pointerEvents="none"
            selectable={false}
            style={styles.versionInfo}
            testID="cashier-login-version-info"
          >
            {`Runtime: ${versionInfo.runtimeVersion} · OTA: ${versionInfo.otaVersion}`}
          </Text>
        </View>
      </HandheldStateSurface>
    </SafeAreaView>
  );
}

function isReady(runtime: CashierLoginRuntime): boolean {
  return (
    (runtime.state.phase === "ready" ||
      runtime.state.phase === "ready-offline") &&
    runtime.state.device !== "locked" &&
    runtime.services !== null
  );
}

function readinessError(runtime: CashierLoginRuntime, text: LoginCopy): string {
  return runtime.state.phase === "locked" || runtime.state.device === "locked"
    ? text.locked
    : text.runtimeNotReady;
}

function errorText(error: unknown, text: LoginCopy): string {
  if (error instanceof CashierLoginError) {
    switch (error.code) {
      case "BARCODE_REQUIRED":
        return text.barcodeRequired;
      case "DEVICE_LOCKED":
        return text.locked;
      case "RUNTIME_NOT_READY":
        return text.runtimeNotReady;
    }
  }
  if (error instanceof HbposApiError) {
    switch (error.code) {
      case "EMERGENCY_CLOCK_ROLLBACK":
        return text.emergencyClockRollback;
      case "EMERGENCY_TOKEN_EXPIRED":
      case "EMERGENCY_TOKEN_NOT_ACTIVE":
        return text.emergencyExpired;
      case "EMERGENCY_TOKEN_WRONG_STORE":
        return text.emergencyWrongStore;
      case "EMERGENCY_TOKEN_KEY_UNKNOWN":
        return text.emergencyKeyUnknown;
      default:
        if (error.code?.startsWith("EMERGENCY_")) {
          return text.emergencyInvalid;
        }
    }
  }
  return text.rejected;
}

const styles = StyleSheet.create({
  safeArea: { backgroundColor: posColors.canvas, flex: 1 },
  stateSurface: { flex: 1 },
  page: {
    flex: 1,
    flexDirection: "column",
    gap: 16,
    padding: 16,
  },
  contextPanel: {
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    paddingBottom: 16,
  },
  eyebrow: {
    color: posColors.orange,
    fontSize: 12,
    fontWeight: "800",
    letterSpacing: 1.5,
    marginBottom: 8,
  },
  title: {
    color: posColors.ink,
    fontSize: 26,
    fontWeight: "800",
    letterSpacing: -0.5,
    lineHeight: 32,
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 8,
  },
  securityNote: {
    alignItems: "flex-start",
    borderLeftColor: posColors.orange,
    borderLeftWidth: 3,
    flexDirection: "row",
    gap: 12,
    marginTop: 12,
    paddingLeft: 12,
  },
  securityText: {
    color: posColors.mutedInk,
    flex: 1,
    fontSize: 12,
    lineHeight: 18,
  },
  formPanel: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flex: 1,
    width: "100%",
  },
  formPanelContent: { flexGrow: 1, padding: 16 },
  formHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 14,
    marginBottom: 16,
  },
  formHeaderCopy: { flex: 1, minWidth: 0 },
  formTitle: { color: posColors.ink, fontSize: 20, fontWeight: "800" },
  formHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
    marginTop: 3,
  },
  inputRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
  },
  inputContainer: { flex: 1, minWidth: 0 },
  input: {
    backgroundColor: "#FFFDF8",
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 20,
    minHeight: 56,
    paddingHorizontal: 14,
  },
  inputDisabled: { backgroundColor: "#F1F0EC", color: posColors.mutedInk },
  // 输入框右侧外面的图标按钮（inputRow 内并排）；触摸区 48px
  visibilityButton: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 48,
    minWidth: 48,
  },
  clearButton: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 48,
    minWidth: 48,
  },
  keyboardIconButton: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 48,
    minWidth: 48,
  },
  visibilityButtonPressed: { opacity: 0.62 },
  submit: {
    alignItems: "center",
    backgroundColor: posColors.orange,
    flexDirection: "row",
    gap: 10,
    justifyContent: "center",
    marginTop: 16,
    minHeight: 48,
    paddingHorizontal: 20,
  },
  submitPressed: { opacity: 0.62 },
  submitLabel: { color: "#FFFFFF", fontSize: 18, fontWeight: "800" },
  errorBanner: {
    backgroundColor: posColors.redSoft,
    borderLeftColor: posColors.red,
    borderLeftWidth: 3,
    marginTop: 16,
    padding: 13,
  },
  errorText: { color: posColors.red, fontSize: 15, fontWeight: "700" },
  offlineNote: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 9,
    marginTop: 16,
  },
  offlineText: {
    color: posColors.mutedInk,
    flex: 1,
    fontSize: 14,
    lineHeight: 20,
  },
  versionInfo: {
    color: posColors.mutedInk,
    fontSize: 12,
    textAlign: "right",
  },
});
