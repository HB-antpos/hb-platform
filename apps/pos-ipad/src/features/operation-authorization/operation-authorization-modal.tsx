import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import { Modal, StyleSheet, Text, View, type TextInput } from "react-native";

import {
  operationAuthorizationActionCopyKey,
  operationAuthorizationFailureCopyKey,
  operationAuthorizationText,
  resolveOperationAuthorizationLocale,
  type OperationAuthorizationCopyKey,
} from "./operation-authorization-copy";
import type {
  OperationAuthorizationPublicState,
  OperationAuthorizationService,
} from "./operation-authorization-service";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

export const OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET = 44;

export type OperationAuthorizationModalService = Pick<
  OperationAuthorizationService,
  "cancel" | "getState" | "submitSupervisorBarcode" | "subscribe"
>;

export function OperationAuthorizationModal({
  locale: localeInput = "zh",
  service,
}: Readonly<{
  locale?: string;
  service: OperationAuthorizationModalService;
}>) {
  usePosThemeVersion();
  const subscribe = useCallback(
    (notify: () => void) => service.subscribe(() => notify()),
    [service],
  );
  // Service state objects are intentionally ephemeral; cache equal snapshots so
  // useSyncExternalStore sees a stable value between real state transitions.
  const getSnapshot = useMemo(() => {
    let cached = service.getState();
    return () => {
      const next = service.getState();
      if (!samePublicState(cached, next)) cached = next;
      return cached;
    };
  }, [service]);
  const state = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  const locale = resolveOperationAuthorizationLocale(localeInput);
  const t = useCallback(
    (
      key: OperationAuthorizationCopyKey,
      values?: Readonly<Record<string, string | number>>,
    ) => operationAuthorizationText(locale, key, values),
    [locale],
  );
  const inputRef = useRef<TextInput>(null);
  const keyboardRequestedRef = useRef(false);
  const keyboardRefreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const submittingRef = useRef(false);
  const submissionGenerationRef = useRef(0);
  const [barcode, setBarcode] = useState("");
  const [feedbackKey, setFeedbackKey] =
    useState<OperationAuthorizationCopyKey | null>(null);
  const [showSoftInputOnFocus, setShowSoftInputOnFocus] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const awaiting = state.kind === "awaiting-supervisor";
  const verifying = awaiting && (state.verifying || submitting);
  const actionId = awaiting ? state.actionId : null;
  const actionCopyKey = awaiting
    ? operationAuthorizationActionCopyKey(state.action)
    : null;
  let actionLabel = "";
  if (awaiting) actionLabel = actionCopyKey ? t(actionCopyKey) : state.action;

  const focusScanner = useCallback(() => {
    const timer = setTimeout(() => inputRef.current?.focus(), 0);
    return () => clearTimeout(timer);
  }, []);

  const clearKeyboardRefreshTimer = useCallback(() => {
    if (keyboardRefreshTimerRef.current === null) return;
    clearTimeout(keyboardRefreshTimerRef.current);
    keyboardRefreshTimerRef.current = null;
  }, []);

  const resetToScannerMode = useCallback(() => {
    clearKeyboardRefreshTimer();
    keyboardRequestedRef.current = false;
    inputRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    setShowSoftInputOnFocus(false);
  }, [clearKeyboardRefreshTimer]);

  const requestManualKeyboard = useCallback(() => {
    if (verifying) return;
    keyboardRequestedRef.current = true;
    inputRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    if (!showSoftInputOnFocus) {
      setShowSoftInputOnFocus(true);
      return;
    }
    // 软键盘被手动收起但输入仍聚焦时，先切回扫码模式再刷新原生输入视图。
    setShowSoftInputOnFocus(false);
  }, [showSoftInputOnFocus, verifying]);

  useEffect(() => {
    submissionGenerationRef.current += 1;
    resetToScannerMode();
    setBarcode("");
    setFeedbackKey(null);
    submittingRef.current = false;
    setSubmitting(false);
  }, [actionId, resetToScannerMode]);

  useEffect(() => {
    if (!awaiting || verifying) {
      resetToScannerMode();
      return undefined;
    }
    return focusScanner();
  }, [awaiting, focusScanner, resetToScannerMode, verifying]);

  useEffect(() => {
    if (showSoftInputOnFocus || !keyboardRequestedRef.current) return undefined;
    keyboardRefreshTimerRef.current = setTimeout(() => {
      keyboardRefreshTimerRef.current = null;
      setShowSoftInputOnFocus(true);
    }, 0);
    return clearKeyboardRefreshTimer;
  }, [clearKeyboardRefreshTimer, showSoftInputOnFocus]);

  useEffect(() => {
    if (
      !awaiting ||
      verifying ||
      !showSoftInputOnFocus ||
      !keyboardRequestedRef.current
    ) {
      return;
    }
    keyboardRequestedRef.current = false;
    inputRef.current?.setNativeProps({ showSoftInputOnFocus: true });
    inputRef.current?.focus();
  }, [awaiting, showSoftInputOnFocus, verifying]);

  useEffect(() => clearKeyboardRefreshTimer, [clearKeyboardRefreshTimer]);

  const submitBarcode = useCallback(
    async (barcodeInput: string) => {
      const submittedActionId = actionId;
      if (
        !awaiting ||
        !submittedActionId ||
        verifying ||
        submittingRef.current
      ) {
        return;
      }

      resetToScannerMode();
      // 原值只活在本次调用栈内；受控输入与原生输入缓冲在网络请求前立即清空。
      const normalizedBarcode = barcodeInput.replace(/[\r\n].*$/s, "").trim();
      setBarcode("");
      inputRef.current?.clear();
      if (!normalizedBarcode) {
        setFeedbackKey("barcodeRequired");
        focusScanner();
        return;
      }

      const submissionGeneration = submissionGenerationRef.current + 1;
      submissionGenerationRef.current = submissionGeneration;
      submittingRef.current = true;
      setSubmitting(true);
      setFeedbackKey(null);
      try {
        const result = await service.submitSupervisorBarcode(normalizedBarcode);
        const stillCurrent = isCurrentSubmission(
          service,
          submittedActionId,
          submissionGeneration,
          submissionGenerationRef.current,
        );
        if (
          stillCurrent &&
          result.consumed &&
          result.outcome === "denied" &&
          result.reason
        ) {
          setFeedbackKey(operationAuthorizationFailureCopyKey(result.reason));
        } else if (
          stillCurrent &&
          result.consumed &&
          result.outcome !== "authorized" &&
          result.outcome !== "cancelled" &&
          result.outcome !== "duplicate-ignored"
        ) {
          setFeedbackKey("validationFailed");
        }
      } catch {
        if (
          isCurrentSubmission(
            service,
            submittedActionId,
            submissionGeneration,
            submissionGenerationRef.current,
          )
        ) {
          setFeedbackKey("validationFailed");
        }
      } finally {
        if (submissionGenerationRef.current === submissionGeneration) {
          submittingRef.current = false;
          setSubmitting(false);
          const current = service.getState();
          if (
            current.kind === "awaiting-supervisor" &&
            current.actionId === submittedActionId
          ) {
            focusScanner();
          }
        }
      }
    },
    [
      actionId,
      awaiting,
      focusScanner,
      resetToScannerMode,
      service,
      verifying,
    ],
  );

  const handleChangeText = useCallback(
    (value: string) => {
      const terminator = value.search(/[\r\n]/);
      if (terminator >= 0) {
        void submitBarcode(value.slice(0, terminator));
        return;
      }
      setBarcode(value);
    },
    [submitBarcode],
  );

  const cancel = useCallback(() => {
    submissionGenerationRef.current += 1;
    resetToScannerMode();
    submittingRef.current = false;
    setSubmitting(false);
    setBarcode("");
    inputRef.current?.clear();
    setFeedbackKey(null);
    if (actionId) service.cancel(actionId);
  }, [actionId, resetToScannerMode, service]);

  if (!awaiting) return null;

  return (
    <Modal
      animationType="fade"
      onRequestClose={cancel}
      presentationStyle="overFullScreen"
      statusBarTranslucent
      supportedOrientations={["landscape-left", "landscape-right"]}
      transparent
      visible
    >
      <View
        accessibilityViewIsModal
        style={styles.overlay}
        testID="operation-authorization-modal"
      >
        <PosPressable
          accessible={false}
          onPress={cancel}
          sound="navigate"
          style={styles.backdropDismissArea}
          testID="operation-authorization-backdrop"
        />
        {/* 居中弹窗：外层只负责圆角与投影，输入仍在键盘感知滚动容器里。 */}
        <View style={styles.panelFrame}>
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.panelContent}
            showsVerticalScrollIndicator={false}
            style={styles.panel}
            testID="operation-authorization-keyboard-scroll"
          >
            <Text style={styles.eyebrow}>{t("eyebrow")}</Text>
            <Text accessibilityRole="header" style={styles.title}>
              {t("title")}
            </Text>
            <Text style={styles.description}>{t("description")}</Text>
            {/* 黄色条写明在批准哪一项，主管扫码前先看清。 */}
            <View style={styles.requestedAction}>
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.yellowInk}
                name="shield-account-outline"
                size={24}
              />
              <Text numberOfLines={2} style={styles.requestedActionText}>
                {t("requestedAction", { action: actionLabel })}
              </Text>
            </View>

            <View>
              <Text style={styles.inputLabel}>{t("inputLabel")}</Text>
              <View style={styles.inputRow}>
                <View style={styles.inputContainer}>
                  <PosKeyboardAwareTextInput
                    ref={inputRef}
                    accessibilityLabel={t("inputLabel")}
                    autoCapitalize="none"
                    autoComplete="off"
                    autoCorrect={false}
                    autoFocus
                    blurOnSubmit={false}
                    editable={!verifying}
                    onBlur={resetToScannerMode}
                    onChangeText={handleChangeText}
                    onSubmitEditing={(event) =>
                      void submitBarcode(event.nativeEvent.text || barcode)
                    }
                    placeholder={t("inputHint")}
                    placeholderTextColor={posColors.placeholder}
                    returnKeyType="done"
                    secureTextEntry
                    selectTextOnFocus={false}
                    showSoftInputOnFocus={showSoftInputOnFocus}
                    style={[
                      styles.input,
                      // 占位提示用正文字号；有内容后放大遮罩圆点并拉开间距。
                      barcode.length > 0 && styles.inputFilled,
                      verifying && styles.inputDisabled,
                    ]}
                    testID="operation-authorization-barcode"
                    textContentType="none"
                    value={barcode}
                  />
                  <View pointerEvents="none" style={styles.inputIcon}>
                    <MaterialCommunityIcons
                      accessible={false}
                      color={verifying ? posColors.disabledInk : posColors.ink}
                      name="barcode-scan"
                      size={32}
                    />
                  </View>
                </View>
                <PosPressable
                  accessibilityLabel={t("keyboard")}
                  accessibilityRole="button"
                  accessibilityState={{ disabled: verifying }}
                  disabled={verifying}
                  onPress={requestManualKeyboard}
                  sound="navigate"
                  style={({ pressed }) => [
                    styles.keyboardButton,
                    verifying && styles.keyboardButtonDisabled,
                    pressed && !verifying && styles.pressed,
                  ]}
                  testID="operation-authorization-show-keyboard"
                >
                  <MaterialCommunityIcons
                    accessible={false}
                    color={verifying ? posColors.disabledInk : posColors.ink}
                    name="keyboard-outline"
                    size={32}
                  />
                </PosPressable>
              </View>

              {feedbackKey ? (
                <View
                  accessibilityLiveRegion="polite"
                  accessibilityRole="alert"
                  style={styles.feedback}
                  testID="operation-authorization-feedback"
                >
                  <MaterialCommunityIcons
                    accessible={false}
                    color={posColors.red}
                    name="alert-circle-outline"
                    size={24}
                  />
                  <Text style={styles.feedbackText}>{t(feedbackKey)}</Text>
                </View>
              ) : null}
            </View>

            <Text style={styles.privacy}>{t("privacy")}</Text>
            <View style={styles.actions}>
              {/* 核验期间「取消」保持可点，随时可以放弃本次授权。 */}
              <PosButton
                label={t("cancel")}
                onPress={cancel}
                size="large"
                sound="navigate"
                style={styles.cancelButton}
                testID="operation-authorization-cancel"
              />
              <PosButton
                // loading 只负责转圈；无障碍状态沿用原来的 { disabled }。
                accessibilityState={{ disabled: verifying }}
                fill
                icon="shield-check-outline"
                label={t(verifying ? "verifying" : "submit")}
                loading={verifying}
                onPress={() => void submitBarcode(barcode)}
                size="large"
                testID="operation-authorization-submit"
                tone="primary"
              />
            </View>
          </PosKeyboardAwareScrollView>
        </View>
      </View>
    </Modal>
  );
}

function isCurrentSubmission(
  service: OperationAuthorizationModalService,
  actionId: string,
  submissionGeneration: number,
  currentGeneration: number,
): boolean {
  if (submissionGeneration !== currentGeneration) return false;
  const state = service.getState();
  return state.kind === "awaiting-supervisor" && state.actionId === actionId;
}

function samePublicState(
  left: OperationAuthorizationPublicState,
  right: OperationAuthorizationPublicState,
): boolean {
  if (left.kind !== right.kind) return false;
  if (left.kind === "idle" || right.kind === "idle") return true;
  return (
    left.actionId === right.actionId &&
    left.permissionCode === right.permissionCode &&
    left.screen === right.screen &&
    left.action === right.action &&
    left.verifying === right.verifying
  );
}

/** 主管条码输入框与键盘方键的统一高度。 */
const BARCODE_FIELD_HEIGHT = 76;

const styles = createPosStyles((colors) => ({
  overlay: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 32,
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  panelFrame: {
    backgroundColor: colors.canvas,
    borderRadius: 16,
    // 键盘弹出、可用高度不够时整体收缩，由内部滚动容器滚动。
    flexShrink: 1,
    maxWidth: 640,
    shadowColor: colors.shadow,
    shadowOffset: { height: 24, width: 0 },
    shadowOpacity: 0.35,
    shadowRadius: 30,
    width: "100%",
  },
  panel: {
    borderRadius: 16,
    // 弹窗高度跟随内容，不撑满整屏。
    flexGrow: 0,
  },
  panelContent: {
    gap: 14,
    padding: 22,
  },
  eyebrow: {
    color: colors.accent,
    fontSize: 13,
    fontWeight: "900",
    letterSpacing: 2,
  },
  title: { color: colors.ink, fontSize: 26, fontWeight: "900" },
  description: {
    color: colors.mutedInk,
    fontSize: 16,
    fontWeight: "700",
    lineHeight: 23,
  },
  requestedAction: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  requestedActionText: {
    color: colors.yellowInk,
    flex: 1,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },
  inputLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    marginBottom: 5,
  },
  inputRow: {
    flexDirection: "row",
    gap: 10,
  },
  inputContainer: { flex: 1, minWidth: 0 },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 2.5,
    color: colors.ink,
    fontSize: 19,
    fontWeight: "600",
    minHeight: BARCODE_FIELD_HEIGHT,
    paddingLeft: 62,
    paddingRight: 18,
  },
  inputFilled: { fontSize: 26, fontWeight: "900", letterSpacing: 6 },
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
  keyboardButton: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 1.5,
    justifyContent: "center",
    minHeight: BARCODE_FIELD_HEIGHT,
    minWidth: BARCODE_FIELD_HEIGHT,
  },
  keyboardButtonDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  pressed: { opacity: 0.72 },
  feedback: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    marginTop: 10,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  feedbackText: {
    color: colors.red,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  privacy: {
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "600",
    lineHeight: 18,
  },
  actions: {
    flexDirection: "row",
    gap: 12,
    marginTop: 4,
  },
  cancelButton: { width: 180 },
}));
