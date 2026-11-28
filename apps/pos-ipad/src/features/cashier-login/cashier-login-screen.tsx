import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { Image, Keyboard, Text, View, type TextInput } from "react-native";

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
import type { AttendanceQrDisplaySource } from "@/features/attendance-audit/attendance-audit-runtime";
import {
  AttendanceQrLoginCard,
  useAttendanceQrState,
} from "@/features/attendance-audit/attendance-qr-sales-panels";
import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

type LoginCopy = Readonly<{
  eyebrow: string;
  title: string;
  subtitle: string;
  scanTitle: string;
  inputLabel: string;
  inputHint: string;
  keyboard: string;
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
  networkCheck: string;
  bleScanner: string;
  backToEntry: string;
}>;

const copy: Record<"en" | "zh", LoginCopy> = {
  en: {
    eyebrow: "CASHIER SIGN IN",
    title: "Ready for the next sale",
    subtitle:
      "Scan your cashier barcode or enter it manually. This terminal only accepts approved staff for its assigned store.",
    scanTitle: "Scan your cashier barcode",
    inputLabel: "Cashier barcode",
    inputHint: "Scan cashier barcode",
    keyboard: "Keyboard",
    showBarcode: "Show cashier barcode",
    hideBarcode: "Hide cashier barcode",
    manualEntryHint: "Scanner ready; tap the keyboard icon for manual entry.",
    submit: "Sign in to checkout",
    submitting: "Checking authorization…",
    offline: "Offline sign-in uses this iPad's encrypted cashier cache.",
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
      "The emergency signing key is unavailable. Connect this iPad and refresh its security keys.",
    emergencyInvalid: "This emergency sign-in QR is invalid.",
    networkCheck: "Network check",
    bleScanner: "Bluetooth scanner",
    backToEntry: "Back to home",
  },
  zh: {
    eyebrow: "收银员登录",
    title: "准备开始下一笔收银",
    subtitle:
      "请扫描收银员条码或手动输入。此终端只允许已获本门店授权的员工进入收银。",
    scanTitle: "请扫描收银员条码",
    inputLabel: "收银员条码",
    inputHint: "扫描收银员条码",
    keyboard: "键盘",
    showBarcode: "显示收银员条码",
    hideBarcode: "隐藏收银员条码",
    manualEntryHint: "默认使用扫码枪；手动输入请点键盘图标。",
    submit: "进入收银",
    submitting: "正在核验授权…",
    offline: "离线登录只使用本 iPad 加密保存的收银员缓存。",
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
    networkCheck: "网络检测",
    bleScanner: "蓝牙扫码枪",
    backToEntry: "返回首页",
  },
};

export type CashierLoginVersionInfo = Readonly<{
  appVersion: string;
  buildNumber: string;
  runtimeVersion: string;
  otaVersion: string;
}>;

const DEFAULT_VERSION_INFO: CashierLoginVersionInfo = Object.freeze({
  appVersion: "unknown",
  buildNumber: "unknown",
  runtimeVersion: "unknown",
  otaVersion: "embedded",
});

export function CashierLoginScreen({
  attendanceQr = null,
  controller,
  language = "zh",
  onLoginDiagnostic,
  onBackToEntry,
  onManualInputFocusChange,
  onOpenBleScanner,
  onOpenNetworkCheck,
  onSwitchLanguage,
  onSuccess,
  runtime,
  versionInfo = DEFAULT_VERSION_INFO,
}: Readonly<{
  /**
   * 员工打卡二维码（路由在登录页聚焦时创建、失焦时销毁）。不传或为 null 时左栏保持原来的品牌介绍；
   * 设备还没授权 / 终端未就绪时也不显示，避免在没有设备身份时展示考勤区。
   */
  attendanceQr?: AttendanceQrDisplaySource | null;
  controller?: CashierLoginController;
  language?: string;
  /** 扫码登录被拒、或原生与 JS 输入错位后才成功时回调脱敏诊断（不含条码内容），由路由写进中心日志。 */
  onLoginDiagnostic?(diagnostic: CashierLoginDiagnostic): void;
  onManualInputFocusChange?(focused: boolean): void;
  /** 回到首页（「收银 / 订货」两个入口）；不传则不显示按钮。 */
  onBackToEntry?(): void;
  /** 打开蓝牙扫码枪面板（配对 / 连接 / 测试）；登录前就能用。原生包不带 BLE 服务时不传，按钮不显示。 */
  onOpenBleScanner?(): void;
  /** 打开只读网络检测页；登录前就能用，不受终端就绪状态限制。 */
  onOpenNetworkCheck?(): void;
  onSwitchLanguage?: () => void;
  onSuccess(): void;
  runtime: CashierLoginRuntime;
  versionInfo?: CashierLoginVersionInfo;
}>) {
  usePosThemeVersion();
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
  // 要带 JS 侧事件计数，USB 扫码枪敲字比 JS 处理快时计数对不上，原生会静默丢弃这次写入，
  // JS 却以为已清空，下一枪就和残留拼成错码（服务端表现为「收银员条码无效」）。
  // 现在以原生回传文本为准：记住已提交过的原生前缀，每次只取其后新增的部分（同 #1400 隐藏扫码框）。
  // barcode 只是「原生文本中未提交部分」的显示镜像，用于样式与诊断，不再作为提交来源。
  const [barcode, setBarcode] = useState("");
  const nativeTextRef = useRef("");
  const consumedTextRef = useRef("");
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
  const blocked = !isCashierLoginRuntimeReady(runtime);
  const attendanceQrState = useAttendanceQrState(blocked ? null : attendanceQr);

  const clearKeyboardRefreshTimer = useCallback((): void => {
    if (keyboardRefreshTimerRef.current === null) return;
    clearTimeout(keyboardRefreshTimerRef.current);
    keyboardRefreshTimerRef.current = null;
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
    consumedTextRef.current = fullNativeText;
    inputTraceRef.current = { firstAt: 0, lastAt: 0, maxGapMs: 0, changes: 0 };
    if (fullNativeText) barcodeRef.current?.clear();
    setBarcode("");
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

  const inputLocked = submitting || blocked;

  return (
    <PosScreenFrame
      language={locale}
      {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      // 登录前不允许跳去其他页面，这一页不显示左侧栏。
      rail="none"
    >
      <View style={styles.page} testID="cashier-login-page">
        {/* 左侧深色品牌区：Logo、眉标、标题、说明；安全提示和版本信息压在底部。 */}
        <View style={styles.brandPanel}>
          {attendanceQrState ? (
            // 有考勤码：品牌区收成一行放在顶上，腾出位置给「员工打卡」大码；
            // 右侧收银员登录表单不受影响。
            <>
              <View style={styles.brandHeaderCompact}>
                <Image
                  accessibilityIgnoresInvertColors
                  accessible={false}
                  resizeMode="contain"
                  source={require("../../../assets/icon.png")}
                  style={styles.brandMarkCompact}
                />
                <View style={styles.brandHeaderCopy}>
                  <Text style={styles.eyebrowCompact}>{text.eyebrow}</Text>
                  <Text numberOfLines={1} style={styles.titleCompact}>
                    {text.title}
                  </Text>
                </View>
              </View>
              <AttendanceQrLoginCard
                language={locale}
                state={attendanceQrState}
              />
            </>
          ) : (
            <>
              <Image
                accessibilityIgnoresInvertColors
                accessible={false}
                resizeMode="contain"
                source={require("../../../assets/icon.png")}
                style={styles.brandMark}
              />
              <Text style={styles.eyebrow}>{text.eyebrow}</Text>
              <Text style={styles.title}>{text.title}</Text>
              <Text style={styles.subtitle}>{text.subtitle}</Text>
            </>
          )}
          <View style={styles.brandSpacer} />
          <View style={styles.securityNote}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.onInverse}
              name="shield-lock-outline"
              size={20}
              style={styles.securityIcon}
            />
            <View style={styles.securityCopy}>
              <Text style={styles.securityText}>{text.security}</Text>
              <Text style={styles.securityText}>{text.offline}</Text>
            </View>
          </View>
          <Text
            numberOfLines={1}
            pointerEvents="none"
            selectable={false}
            style={styles.versionInfo}
            testID="cashier-login-version-info"
          >
            {`Version ${versionInfo.appVersion} · Build ${versionInfo.buildNumber} · Runtime: ${versionInfo.runtimeVersion} · OTA: ${versionInfo.otaVersion}`}
          </Text>
        </View>

        {/* 右侧表单：条码输入留在键盘感知滚动容器内，手动键盘弹出时能把输入框滚出来。 */}
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.formPanelContent}
          showsVerticalScrollIndicator={false}
          style={styles.formPanel}
          testID="cashier-login-keyboard-scroll"
        >
          <View style={styles.scanPrompt}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.accent}
              name="barcode-scan"
              size={84}
            />
            <Text accessibilityRole="header" style={styles.scanTitle}>
              {text.scanTitle}
            </Text>
            <Text style={styles.scanHint}>{text.manualEntryHint}</Text>
          </View>

          <View>
            <Text style={styles.inputLabel}>{text.inputLabel}</Text>
            <View style={styles.inputRow}>
              <View style={styles.inputContainer}>
                <PosKeyboardAwareTextInput
                  ref={barcodeRef}
                  accessibilityLabel={text.inputLabel}
                  autoFocus
                  autoCapitalize="characters"
                  // 蓝牙扫码枪不带回车时按扫码节奏自动提交（等效回车，对齐手持）；手动输入间隔大，不触发。
                  // 不启用 mediumSpeedHidSubmit：iPad 蓝牙 HID 连发很快，且软键盘手输不应被慢速判定误提交。
                  autoSubmitOnScanIdle
                  autoCorrect={false}
                  editable={!inputLocked}
                  onBlur={handleBarcodeBlur}
                  onChangeText={handleBarcodeChange}
                  onFocus={notifyManualInputFocused}
                  // 有事件 = 真实回车（USB 枪回车后缀 / 键盘），取原生全文；无事件 = 扫码节奏停顿自动提交。
                  onSubmitEditing={(event) =>
                    void submit(
                      event?.nativeEvent ? "enter" : "scan-idle",
                      event?.nativeEvent?.text,
                    )
                  }
                  placeholder={text.inputHint}
                  placeholderTextColor={posColors.placeholder}
                  returnKeyType="go"
                  secureTextEntry={!barcodeVisible}
                  showSoftInputOnFocus={showSoftInputOnFocus}
                  style={[
                    styles.input,
                    // 占位提示用正文字号；有内容后放大，遮罩圆点再拉开间距便于数位数。
                    barcode.length > 0 && styles.inputFilled,
                    barcode.length > 0 && !barcodeVisible && styles.inputMasked,
                    inputLocked && styles.inputDisabled,
                  ]}
                  submitBehavior="submit"
                  testID="cashier-login-barcode"
                  // 不传 value：非受控，见 nativeTextRef 处注释。
                />
                <View pointerEvents="none" style={styles.inputIcon}>
                  <MaterialCommunityIcons
                    accessible={false}
                    color={inputLocked ? posColors.disabledInk : posColors.ink}
                    name="account-key-outline"
                    size={32}
                  />
                </View>
              </View>
              <PosPressable
                accessibilityLabel={
                  barcodeVisible ? text.hideBarcode : text.showBarcode
                }
                accessibilityRole="button"
                accessibilityState={{ disabled: inputLocked }}
                disabled={inputLocked}
                onPress={() => setBarcodeVisible((visible) => !visible)}
                sound="navigate"
                style={({ pressed }) => [
                  styles.squareKey,
                  inputLocked && styles.squareKeyDisabled,
                  pressed && !inputLocked && styles.pressed,
                ]}
                testID="cashier-login-toggle-barcode-visibility"
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={inputLocked ? posColors.disabledInk : posColors.ink}
                  name={barcodeVisible ? "eye-off-outline" : "eye-outline"}
                  size={32}
                />
              </PosPressable>
              <PosPressable
                accessibilityLabel={text.keyboard}
                accessibilityRole="button"
                accessibilityState={{ disabled: inputLocked }}
                disabled={inputLocked}
                onPress={requestManualKeyboard}
                sound="navigate"
                style={({ pressed }) => [
                  styles.squareKey,
                  inputLocked && styles.squareKeyDisabled,
                  pressed && !inputLocked && styles.pressed,
                ]}
                testID="cashier-login-show-keyboard"
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={inputLocked ? posColors.disabledInk : posColors.ink}
                  name="keyboard-outline"
                  size={32}
                  testID="cashier-login-keyboard-icon"
                />
              </PosPressable>
            </View>
            {/* 出错提示紧跟在输入框下方，扫码后不用移开视线。 */}
            {error ? (
              <View
                accessibilityRole="alert"
                style={styles.errorBanner}
                testID="cashier-login-error"
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.red}
                  name="alert-circle-outline"
                  size={24}
                />
                <Text style={styles.errorText}>{error}</Text>
              </View>
            ) : null}
          </View>

          <PosButton
            disabled={blocked}
            icon="login"
            label={submitting ? text.submitting : text.submit}
            loading={submitting}
            onPress={() => void submit("button")}
            size="primary"
            testID="cashier-login-submit"
            tone="primary"
          />
          {onOpenNetworkCheck ? (
            <PosButton
              disabled={submitting}
              icon="access-point-network"
              label={text.networkCheck}
              onPress={onOpenNetworkCheck}
              size="medium"
              sound="navigate"
              style={styles.networkCheckButton}
              testID="cashier-login-network-check"
              tone="quiet"
            />
          ) : null}
          {onOpenBleScanner ? (
            <PosButton
              disabled={submitting}
              icon="barcode-scan"
              label={text.bleScanner}
              onPress={onOpenBleScanner}
              size="medium"
              sound="navigate"
              style={styles.networkCheckButton}
              testID="cashier-login-ble-scanner"
              tone="quiet"
            />
          ) : null}
          {onBackToEntry ? (
            <PosButton
              disabled={submitting}
              icon="home-outline"
              label={text.backToEntry}
              onPress={onBackToEntry}
              size="medium"
              sound="navigate"
              style={styles.networkCheckButton}
              testID="cashier-login-back-to-entry"
              tone="quiet"
            />
          ) : null}
        </PosKeyboardAwareScrollView>
      </View>
    </PosScreenFrame>
  );
}

/** 设备已授权且终端就绪（含离线就绪）、未被服务端锁定：登录页才显示考勤码，路由才创建它。 */
export function isCashierLoginRuntimeReady(
  runtime: CashierLoginRuntime,
): boolean {
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

/** 条码输入框与右侧方键的统一高度。 */
const BARCODE_FIELD_HEIGHT = 76;

const styles = createPosStyles((colors) => ({
  page: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  brandPanel: {
    backgroundColor: colors.inverseSurface,
    borderRadius: 14,
    flexBasis: 440,
    flexGrow: 0,
    flexShrink: 1,
    gap: 14,
    paddingHorizontal: 32,
    paddingVertical: 34,
  },
  brandMark: {
    borderRadius: 18,
    height: 76,
    width: 76,
  },
  eyebrow: {
    color: colors.onInverse,
    fontSize: 14,
    fontWeight: "900",
    letterSpacing: 2,
    opacity: 0.72,
  },
  title: {
    color: colors.onInverse,
    fontSize: 34,
    fontWeight: "900",
    lineHeight: 42,
  },
  subtitle: {
    color: colors.onInverse,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 23,
    opacity: 0.82,
  },
  brandHeaderCompact: {
    alignItems: "center",
    borderBottomColor: "rgba(255,255,255,0.12)",
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    paddingBottom: 14,
  },
  brandHeaderCopy: { flex: 1, minWidth: 0 },
  brandMarkCompact: {
    borderRadius: 12,
    height: 46,
    width: 46,
  },
  eyebrowCompact: {
    color: colors.onInverse,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 2,
    opacity: 0.7,
  },
  titleCompact: {
    color: colors.onInverse,
    fontSize: 18,
    fontWeight: "900",
    marginTop: 2,
  },
  brandSpacer: { flex: 1, minHeight: 8 },
  securityNote: {
    alignItems: "flex-start",
    backgroundColor: colors.chromeChip,
    borderRadius: 10,
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  securityIcon: { opacity: 0.82 },
  securityCopy: { flex: 1, minWidth: 0 },
  securityText: {
    color: colors.onInverse,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 20,
    opacity: 0.82,
  },
  versionInfo: {
    color: colors.onInverse,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    opacity: 0.6,
  },
  formPanel: {
    flex: 1,
    minWidth: 0,
  },
  formPanelContent: {
    flexGrow: 1,
    gap: 14,
    justifyContent: "center",
    paddingHorizontal: 56,
    paddingVertical: 12,
  },
  scanPrompt: { alignItems: "center" },
  scanTitle: {
    color: colors.ink,
    fontSize: 26,
    fontWeight: "900",
    marginTop: 4,
    textAlign: "center",
  },
  scanHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    marginTop: 6,
    textAlign: "center",
  },
  inputLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    marginBottom: 5,
  },
  inputRow: { flexDirection: "row", gap: 10 },
  inputContainer: { flex: 1, minWidth: 0 },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 2.5,
    color: colors.ink,
    fontSize: 19,
    fontWeight: "600",
    height: BARCODE_FIELD_HEIGHT,
    paddingLeft: 62,
    paddingRight: 18,
  },
  inputFilled: { fontSize: 26, fontWeight: "900" },
  inputMasked: { letterSpacing: 6 },
  inputDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
    color: colors.disabledInk,
  },
  inputIcon: {
    bottom: 0,
    justifyContent: "center",
    left: 18,
    position: "absolute",
    top: 0,
  },
  squareKey: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 1.5,
    justifyContent: "center",
    minHeight: BARCODE_FIELD_HEIGHT,
    minWidth: BARCODE_FIELD_HEIGHT,
  },
  squareKeyDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  pressed: { opacity: 0.72 },
  errorBanner: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    marginTop: 10,
    minHeight: posTouch.small,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  errorText: {
    color: colors.red,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  networkCheckButton: {
    alignSelf: "center",
    minWidth: 240,
  },
}));
