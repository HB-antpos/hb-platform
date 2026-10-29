import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  type FormEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  ActivityIndicator,
  Image,
  Keyboard,
  StyleSheet,
  Text,
  View,
  type TextInput,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  CashierLoginController,
  CashierLoginError,
  type CashierLoginRuntime,
} from "./cashier-login-controller";
import { useCashierLoginStore } from "./cashier-login-store";

import { HbposApiError } from "@/core/api/hbpos-api";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type LoginCopy = Readonly<{
  eyebrow: string;
  title: string;
  subtitle: string;
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

  return (
    <SafeAreaView style={styles.safeArea}>
      <PosStatusStrip
        language={locale}
        {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      />
      <View style={styles.page} testID="cashier-login-page">
        <View style={styles.contextPanel}>
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
          <View style={styles.securityNote}>
            <MaterialCommunityIcons
              color={posColors.green}
              name="shield-check-outline"
              size={26}
            />
            <Text style={styles.securityText}>{text.security}</Text>
          </View>
        </View>

        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.formPanelContent}
          showsVerticalScrollIndicator={false}
          style={styles.formPanel}
          testID="cashier-login-keyboard-scroll"
        >
          <View style={styles.formHeader}>
            <MaterialCommunityIcons
              color={posColors.orange}
              name="barcode-scan"
              size={30}
            />
            <View>
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
                // 蓝牙扫码枪不带回车时按扫码节奏自动提交（等效回车，对齐手持）；手动输入间隔大，不触发。
                // 不启用 mediumSpeedHidSubmit：iPad 蓝牙 HID 连发很快，且软键盘手输不应被慢速判定误提交。
                autoSubmitOnScanIdle
                autoCorrect={false}
                editable={!submitting && !blocked}
                onBlur={handleBarcodeBlur}
                onChangeText={setBarcode}
                onFocus={notifyManualInputFocused}
                onSubmitEditing={() => void submit()}
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
                value={barcode}
              />
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
            <PosPressable
              accessibilityLabel={text.keyboard}
              accessibilityRole="button"
              accessibilityState={{ disabled: submitting || blocked }}
              disabled={submitting || blocked}
              onPress={requestManualKeyboard}
              sound="navigate"
              style={({ pressed }) => [
                styles.keyboardButton,
                (pressed || submitting || blocked) &&
                  styles.keyboardButtonPressed,
              ]}
              testID="cashier-login-show-keyboard"
            >
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.blue}
                name="keyboard-outline"
                size={26}
                testID="cashier-login-keyboard-icon"
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
            onPress={() => void submit()}
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
          {onOpenNetworkCheck ? (
            <PosPressable
              accessibilityLabel={text.networkCheck}
              accessibilityRole="button"
              accessibilityState={{ disabled: submitting }}
              disabled={submitting}
              onPress={onOpenNetworkCheck}
              sound="navigate"
              style={({ pressed }) => [
                styles.networkCheckButton,
                (pressed || submitting) && styles.networkCheckButtonPressed,
              ]}
              testID="cashier-login-network-check"
            >
              <MaterialCommunityIcons
                color={posColors.blue}
                name="access-point-network"
                size={20}
              />
              <Text style={styles.networkCheckLabel}>{text.networkCheck}</Text>
            </PosPressable>
          ) : null}
        </PosKeyboardAwareScrollView>
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
  page: {
    flex: 1,
    flexDirection: "row",
    gap: 28,
    padding: 34,
  },
  contextPanel: {
    backgroundColor: posColors.ink,
    flex: 1.12,
    justifyContent: "center",
    padding: 48,
  },
  brandMark: {
    borderRadius: 12,
    height: 52,
    marginBottom: 30,
    width: 52,
  },
  eyebrow: {
    color: "#F9C1AD",
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 1.5,
    marginBottom: 14,
  },
  title: {
    color: "#FFFFFF",
    fontSize: 39,
    fontWeight: "800",
    letterSpacing: -0.5,
    lineHeight: 47,
    maxWidth: 480,
  },
  subtitle: {
    color: "#D8E1E8",
    fontSize: 18,
    lineHeight: 27,
    marginTop: 18,
    maxWidth: 520,
  },
  securityNote: {
    alignItems: "flex-start",
    borderLeftColor: posColors.orange,
    borderLeftWidth: 3,
    flexDirection: "row",
    gap: 12,
    marginTop: 42,
    paddingLeft: 15,
  },
  securityText: { color: "#E3EEF2", flex: 1, fontSize: 15, lineHeight: 22 },
  formPanel: {
    alignSelf: "center",
    backgroundColor: posColors.surface,
    flex: 0.88,
    maxWidth: 560,
    minHeight: 430,
    width: "100%",
  },
  formPanelContent: { flexGrow: 1, padding: 48 },
  formHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 14,
    marginBottom: 32,
  },
  formTitle: { color: posColors.ink, fontSize: 25, fontWeight: "800" },
  formHint: { color: posColors.mutedInk, fontSize: 15, marginTop: 3 },
  inputRow: { flexDirection: "row", gap: 12 },
  inputContainer: { flex: 1, minWidth: 0, position: "relative" },
  input: {
    backgroundColor: "#FFFDF8",
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 22,
    minHeight: 62,
    paddingHorizontal: 18,
    paddingRight: 62,
  },
  inputDisabled: { backgroundColor: "#F1F0EC", color: posColors.mutedInk },
  visibilityButton: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 44,
    minWidth: 44,
    position: "absolute",
    right: 8,
    top: 9,
  },
  visibilityButtonPressed: { opacity: 0.62 },
  keyboardButton: {
    alignItems: "center",
    backgroundColor: "#FFFFFF",
    borderColor: posColors.blue,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 62,
    minWidth: 62,
  },
  keyboardButtonPressed: { opacity: 0.62 },
  submit: {
    alignItems: "center",
    backgroundColor: posColors.orange,
    flexDirection: "row",
    gap: 10,
    justifyContent: "center",
    marginTop: 20,
    minHeight: 58,
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
    marginTop: 30,
  },
  offlineText: {
    color: posColors.mutedInk,
    flex: 1,
    fontSize: 14,
    lineHeight: 20,
  },
  networkCheckButton: {
    alignItems: "center",
    alignSelf: "flex-start",
    flexDirection: "row",
    gap: 8,
    marginTop: 18,
    minHeight: 44,
  },
  networkCheckButtonPressed: { opacity: 0.62 },
  networkCheckLabel: { color: posColors.blue, fontSize: 15, fontWeight: "800" },
  versionInfo: {
    bottom: 20,
    color: posColors.mutedInk,
    fontSize: 12,
    position: "absolute",
    right: 84,
    textAlign: "right",
  },
});
