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

import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldActionButton } from "@/ui/handheld/handheld-actions";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import { HandheldSection } from "@/ui/handheld/handheld-layout";
import { posColors } from "@/ui/theme";

export const OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET = 48;

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
    [actionId, awaiting, focusScanner, resetToScannerMode, service, verifying],
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
      supportedOrientations={["portrait"]}
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
          style={styles.modalDismissArea}
          testID="operation-authorization-backdrop"
        />
        <HandheldStateSurface
          slug="operation-authorization"
          style={styles.stateSurface}
        >
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.panelContent}
            showsVerticalScrollIndicator={false}
            style={styles.panel}
            testID="operation-authorization-keyboard-scroll"
          >
            <View style={styles.accent} />
            <Text style={styles.eyebrow}>{t("eyebrow")}</Text>
            <Text style={styles.title}>{t("title")}</Text>
            <Text style={styles.description}>{t("description")}</Text>

            <HandheldSection title={t("inputLabel")}>
              <Text numberOfLines={2} style={styles.requestedAction}>
                {t("requestedAction", { action: actionLabel })}
              </Text>
              <View style={styles.inputRow}>
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
                  returnKeyType="done"
                  secureTextEntry
                  selectTextOnFocus={false}
                  showSoftInputOnFocus={showSoftInputOnFocus}
                  style={[styles.input, verifying && styles.inputDisabled]}
                  testID="operation-authorization-barcode"
                  textContentType="none"
                  value={barcode}
                />
                <PosPressable
                  accessibilityLabel={t("keyboard")}
                  accessibilityRole="button"
                  accessibilityState={{ disabled: verifying }}
                  disabled={verifying}
                  onPress={requestManualKeyboard}
                  sound="navigate"
                  style={({ pressed }) => [
                    styles.keyboardButton,
                    (pressed || verifying) && styles.buttonPressed,
                  ]}
                  testID="operation-authorization-show-keyboard"
                >
                  <Text style={styles.keyboardLabel}>{t("keyboard")}</Text>
                </PosPressable>
              </View>

              {feedbackKey ? (
                <View
                  accessibilityLiveRegion="polite"
                  accessibilityRole="alert"
                  style={styles.feedback}
                  testID="operation-authorization-feedback"
                >
                  <Text style={styles.feedbackText}>{t(feedbackKey)}</Text>
                </View>
              ) : null}
            </HandheldSection>

            <Text style={styles.privacy}>{t("privacy")}</Text>
            <View style={styles.actions}>
              <HandheldActionButton
                disabled={verifying}
                label={t(verifying ? "verifying" : "submit")}
                onPress={() => void submitBarcode(barcode)}
                testID="operation-authorization-submit"
              />
              <HandheldActionButton
                label={t("cancel")}
                onPress={cancel}
                sound="navigate"
                testID="operation-authorization-cancel"
                variant="secondary"
              />
            </View>
          </PosKeyboardAwareScrollView>
        </HandheldStateSurface>
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

const styles = StyleSheet.create({
  overlay: {
    alignItems: "center",
    backgroundColor: "rgba(16, 37, 58, 0.74)",
    flex: 1,
    justifyContent: "center",
    padding: 16,
  },
  modalDismissArea: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  stateSurface: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxHeight: "96%",
    maxWidth: 520,
    overflow: "hidden",
    width: "100%",
  },
  panel: {
    backgroundColor: posColors.surface,
    width: "100%",
  },
  panelContent: {
    gap: 16,
    padding: 16,
  },
  accent: {
    backgroundColor: posColors.orange,
    height: 5,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  eyebrow: {
    color: posColors.orange,
    fontSize: 13,
    fontWeight: "900",
    letterSpacing: 1.4,
    marginBottom: 10,
  },
  title: { color: posColors.ink, fontSize: 29, fontWeight: "800" },
  description: {
    color: posColors.mutedInk,
    fontSize: 17,
    lineHeight: 25,
    marginTop: 10,
  },
  requestedAction: {
    backgroundColor: posColors.blueSoft,
    color: posColors.blue,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  input: {
    backgroundColor: "#FFFDF8",
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    flex: 1,
    fontSize: 22,
    minHeight: 56,
    paddingHorizontal: 16,
  },
  inputRow: {
    alignItems: "stretch",
    flexDirection: "column",
    gap: 10,
  },
  inputDisabled: { backgroundColor: "#F1F0EC", color: posColors.mutedInk },
  keyboardButton: {
    alignItems: "center",
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.border,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: OPERATION_AUTHORIZATION_MIN_TOUCH_TARGET,
    paddingHorizontal: 16,
  },
  keyboardLabel: {
    color: posColors.blue,
    fontSize: 15,
    fontWeight: "800",
  },
  feedback: {
    backgroundColor: posColors.redSoft,
    borderLeftColor: posColors.red,
    borderLeftWidth: 3,
    marginTop: 14,
    padding: 12,
  },
  feedbackText: {
    color: posColors.red,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  privacy: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 16,
  },
  actions: {
    flexDirection: "column",
    gap: 12,
  },
  buttonPressed: { opacity: 0.62 },
});
