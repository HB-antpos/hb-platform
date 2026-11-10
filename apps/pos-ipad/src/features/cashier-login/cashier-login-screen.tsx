import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  type FormEvent,
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
import { useCashierLoginStore } from "./cashier-login-store";

import { HbposApiError } from "@/core/api/hbpos-api";
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
  controller,
  language = "zh",
  onManualInputFocusChange,
  onOpenNetworkCheck,
  onSwitchLanguage,
  onSuccess,
  runtime,
  versionInfo = DEFAULT_VERSION_INFO,
}: Readonly<{
  controller?: CashierLoginController;
  language?: string;
  onManualInputFocusChange?(focused: boolean): void;
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
  const [barcode, setBarcode] = useState("");
  const [barcodeVisible, setBarcodeVisible] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [restoreScannerFocusAfterFailure, setRestoreScannerFocusAfterFailure] =
    useState(false);
  const [showSoftInputOnFocus, setShowSoftInputOnFocus] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const locale = language.toLowerCase().startsWith("zh") ? "zh" : "en";
  const text = copy[locale];
  const blocked = !isReady(runtime);

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
    barcodeRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    barcodeRef.current?.focus();
  }, [
    blocked,
    resetToScannerMode,
    restoreScannerFocusAfterFailure,
    submitting,
  ]);

  const submit = async (event?: FormEvent) => {
    event?.preventDefault();
    if (submittingRef.current || blocked) {
      setError(blocked ? readinessError(runtime, text) : error);
      return;
    }
    setError(null);
    const submittedBarcode = barcode;
    // 扫码提交后立即清空可见输入，登录失败复焦时下一次 HID 从空值开始。
    setBarcode("");
    setBarcodeVisible(false);
    submittingRef.current = true;
    setSubmitting(true);
    try {
      await activeController.login(submittedBarcode, runtime);
      onSuccess();
    } catch (nextError: unknown) {
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
                  onChangeText={setBarcode}
                  onFocus={notifyManualInputFocused}
                  onSubmitEditing={() => void submit()}
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
                  value={barcode}
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
            onPress={() => void submit()}
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
        </PosKeyboardAwareScrollView>
      </View>
    </PosScreenFrame>
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
