import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Redirect } from "expo-router";
import { StatusBar } from "expo-status-bar";
import {
  type Dispatch,
  type SetStateAction,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import { useTranslation } from "react-i18next";
import { StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { reconcileDeviceSessionRuntime } from "./device-registration-state";
import { serverConnectionPanelCopy } from "./server-connection-copy";
import { ServerConnectionPanel } from "./server-connection-panel";

import {
  HbposApiError,
  resolveHbposDeviceSystem,
  type DeviceActivationPreviewResponse,
} from "@/core/api/hbpos-api";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { parseDeviceActivationCode } from "@/core/security/device-activation-code";
import type { DeviceSessionState } from "@/core/security/device-session";
import { CameraScannerModal } from "@/features/scanner-camera/camera-scanner-modal";
import {
  describeDeviceRegistrationFailure,
  type RegistrationTranslate,
} from "./device-registration-failure";
import { toggleAppLanguage } from "@/i18n";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldStateSurface } from "@/ui/handheld";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type RegistrationOperation = "preview" | "redeem" | null;
type ActivationPreview = Readonly<{
  activationCode: string;
  response: DeviceActivationPreviewResponse;
}>;

export function DeviceRegistrationScreen() {
  const { t } = useTranslation();
  const runtime = usePosRuntime();
  const [session, setSession] = useState<DeviceSessionState | null>(null);
  const [requestError, setRequestError] = useState<string | null>(null);
  const [activationCode, setActivationCode] = useState("");
  const [activationPreview, setActivationPreview] =
    useState<ActivationPreview | null>(null);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [operation, setOperation] = useState<RegistrationOperation>(null);
  const serverOperation = useRef<AbortController | null>(null);
  const deviceSession = runtime.services?.deviceSession;
  const scannerRouter = runtime.services?.scanner.router;
  const serverConnection = runtime.services?.serverConnection;
  const expectedDeviceSystem = resolveHbposDeviceSystem();
  const visibleRegistrationState = registrationState(
    session,
    runtime.state.phase,
  );

  const reconcile = useCallback(
    async (next: DeviceSessionState) => {
      setSession(next);
      await reconcileDeviceSessionRuntime(next, runtime);
    },
    [runtime],
  );

  const poll = useCallback(async () => {
    if (!deviceSession) return;
    try {
      const next = await deviceSession.poll();
      setRequestError(null);
      await reconcile(next);
    } catch (error: unknown) {
      applyRequestFailure(error, runtime, setRequestError, t);
    }
  }, [deviceSession, reconcile, runtime, t]);

  const previewActivation = useCallback(async (rawCode: string) => {
    if (!deviceSession) {
      setRequestError(t("registration.runtimeUnavailable"));
      return;
    }
    const normalized = parseDeviceActivationCode(rawCode);
    if (!normalized) {
      setActivationPreview(null);
      setRequestError(t("registration.activationInvalid"));
      return;
    }

    setRequestError(null);
    setActivationPreview(null);
    setActivationCode(normalized);
    setOperation("preview");
    try {
      const response = await deviceSession.previewActivationCode(normalized);
      const storeCode = response.storeCode?.trim() ?? "";
      const storeName = response.storeName?.trim() ?? "";
      const expiresAtUtc = response.expiresAtUtc?.trim() ?? "";
      if (
        !response.isAllowed ||
        !storeCode ||
        !storeName ||
        !expiresAtUtc ||
        response.deviceSystem !== expectedDeviceSystem
      ) {
        setRequestError(
          response.message ?? t("registration.activationPreviewRejected"),
        );
        return;
      }
      setActivationPreview({
        activationCode: normalized,
        response: { ...response, storeCode, storeName, expiresAtUtc },
      });
    } catch (error: unknown) {
      applyRequestFailure(error, runtime, setRequestError, t);
    } finally {
      setOperation(null);
    }
  }, [deviceSession, expectedDeviceSystem, runtime, t]);

  const redeemActivation = useCallback(async () => {
    if (!deviceSession || !activationPreview) return;
    setRequestError(null);
    setOperation("redeem");
    try {
      const next = await deviceSession.redeemActivationCode({
        activationCode: activationPreview.activationCode,
      });
      if (next.status !== "authorized") {
        setRequestError(
          next.message ?? t("registration.activationRedeemRejected"),
        );
        setActivationPreview(null);
      } else {
        setActivationCode("");
        setActivationPreview(null);
      }
      await reconcile(next);
    } catch (error: unknown) {
      applyRequestFailure(error, runtime, setRequestError, t);
    } finally {
      setOperation(null);
    }
  }, [activationPreview, deviceSession, reconcile, runtime, t]);

  useEffect(() => {
    if (!deviceSession || runtime.state.phase !== "registration-required") {
      return;
    }
    let cancelled = false;
    void deviceSession
      .restorePendingActivationCode()
      .then((restored) => {
        if (!cancelled && restored) setActivationCode(restored);
      })
      .catch((error: unknown) => {
        if (!cancelled) applyRequestFailure(error, runtime, setRequestError, t);
      });
    return () => {
      cancelled = true;
    };
  }, [deviceSession, runtime.state.phase]);

  useEffect(() => {
    if (!cameraVisible || !scannerRouter) return;
    return scannerRouter.acquireContext("device-activation");
  }, [cameraVisible, scannerRouter]);

  useEffect(() => {
    if (runtime.state.phase !== "pending-approval" || !deviceSession) return;
    void poll();
    const timer = setInterval(() => void poll(), 5_000);
    return () => clearInterval(timer);
  }, [deviceSession, poll, runtime.state.phase]);

  useEffect(
    () => () => {
      serverOperation.current?.abort();
    },
    [],
  );

  if (
    runtime.state.phase === "ready" ||
    runtime.state.phase === "ready-offline"
  ) {
    return <Redirect href="/" />;
  }

  const changeActivationCode = (value: string) => {
    setActivationCode(value);
    setActivationPreview(null);
    setRequestError(null);
  };
  const activationBusy = operation !== null;
  const activationReady = parseDeviceActivationCode(activationCode) !== null;
  const locked = runtime.state.phase === "locked";
  const pending = runtime.state.phase === "pending-approval";

  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar style="dark" />
      <PosStatusStrip />
      <View style={styles.languageBar}>
        <PosPressable
          accessibilityLabel={t("registration.languageSwitchLabel")}
          accessibilityRole="button"
          hitSlop={8}
          onPress={() => void toggleAppLanguage()}
          style={({ pressed }) => [
            styles.languageButton,
            pressed && styles.languageButtonPressed,
          ]}
          testID="registration-language-switch"
        >
          <MaterialCommunityIcons color={posColors.ink} name="translate" size={18} />
          <Text style={styles.languageButtonLabel}>
            {t("registration.languageSwitch")}
          </Text>
        </PosPressable>
      </View>

      <HandheldStateSurface slug="device-registration" style={styles.stateSurface}>
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.page}
          keyboardShouldPersistTaps="handled"
          showsVerticalScrollIndicator={false}
        >
          <View style={styles.contextPanel}>
            <Text style={styles.eyebrow}>{t("registration.eyebrow")}</Text>
            <Text style={styles.title}>{t("registration.title")}</Text>
            <Text style={styles.subtitle}>{t("registration.subtitle")}</Text>
            <View style={styles.securityNote}>
              <MaterialCommunityIcons
                color={posColors.green}
                name="shield-lock-outline"
                size={22}
              />
              <Text style={styles.securityCopy}>
                {t("registration.securityNote")}
              </Text>
            </View>
          </View>

          <View style={styles.formPanel}>
            <Text style={styles.formTitle}>
              {locked
                ? t("registration.lockedTitle")
                : pending
                  ? t("registration.pendingTitle")
                  : t("registration.formTitle")}
            </Text>
            <Text style={styles.formHint}>
              {locked
                ? t("registration.lockedHint")
                : pending
                  ? t("registration.pendingHint")
                  : t("registration.formHint")}
            </Text>

            {visibleRegistrationState ? (
              <RegistrationStateCard state={visibleRegistrationState} />
            ) : null}

            {locked ? (
              <View
                accessibilityRole="alert"
                style={styles.pendingCard}
                testID="registration-recovery-readonly"
              >
                <MaterialCommunityIcons
                  color={posColors.red}
                  name="shield-alert-outline"
                  size={30}
                />
                <View style={styles.pendingCopy}>
                  <Text style={styles.pendingCode}>
                    {t("registration.lockedTitle")}
                  </Text>
                  <Text style={styles.pendingStore}>
                    {t("registration.lockedHint")}
                  </Text>
                </View>
              </View>
            ) : pending ? (
              <View style={styles.pendingCard}>
                <MaterialCommunityIcons
                  color={posColors.orange}
                  name="clock-outline"
                  size={30}
                />
                <View style={styles.pendingCopy}>
                  <Text style={styles.pendingCode}>
                    {session?.deviceCode ?? t("registration.pendingDeviceCode")}
                  </Text>
                  <Text style={styles.pendingStore}>
                    {session?.storeCode ?? t("registration.pendingStore")}
                  </Text>
                </View>
              </View>
            ) : (
              <>
                <PosPressable
                  accessibilityRole="button"
                  accessibilityState={{ disabled: activationBusy || !scannerRouter }}
                  disabled={activationBusy || !scannerRouter}
                  onPress={() => setCameraVisible(true)}
                  style={({ pressed }) => [
                    styles.primaryButton,
                    pressed && styles.primaryButtonPressed,
                    (activationBusy || !scannerRouter) && styles.primaryButtonDisabled,
                  ]}
                  testID="registration-scan"
                >
                  <MaterialCommunityIcons color="#FFFFFF" name="qrcode-scan" size={21} />
                  <Text style={styles.primaryButtonLabel}>
                    {t("registration.scan")}
                  </Text>
                </PosPressable>

                <FieldLabel>{t("registration.activationCode")}</FieldLabel>
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("registration.activationCode")}
                  autoCapitalize="characters"
                  autoCorrect={false}
                  editable={!activationBusy}
                  maxLength={96}
                  onChangeText={changeActivationCode}
                  placeholder={t("registration.activationCodePlaceholder")}
                  placeholderTextColor={posColors.mutedInk}
                  secureTextEntry
                  style={styles.activationCodeInput}
                  testID="registration-activation-code"
                  textContentType="oneTimeCode"
                  value={activationCode}
                />
                <Text style={styles.activationCodeHint}>
                  {t("registration.activationCodeHint")}
                </Text>

                {activationPreview ? (
                  <View style={styles.activationPreview}>
                    <MaterialCommunityIcons
                      color={posColors.green}
                      name="store-check-outline"
                      size={26}
                    />
                    <View style={styles.pendingCopy}>
                      <Text style={styles.activationPreviewLabel}>
                        {t("registration.activationPreviewTitle")}
                      </Text>
                      <Text style={styles.activationPreviewStore}>
                        {activationPreview.response.storeName} ·{" "}
                        {activationPreview.response.storeCode}
                      </Text>
                      <Text style={styles.activationPreviewMeta}>
                        {t("registration.activationPlatform", {
                          value: activationPreview.response.deviceSystem,
                        })}
                      </Text>
                      <Text style={styles.activationPreviewMeta}>
                        {activationPreview.response.expiresAtUtc
                          ? t("registration.activationExpires", {
                              value: new Date(
                                activationPreview.response.expiresAtUtc,
                              ).toLocaleString(),
                            })
                          : t("registration.activationExpiryUnavailable")}
                      </Text>
                    </View>
                  </View>
                ) : null}
              </>
            )}

            {requestError ? (
              <View accessibilityRole="alert" style={styles.errorBanner}>
                <Text style={styles.errorBannerText}>{requestError}</Text>
              </View>
            ) : null}

            {locked ? (
              <PosPressable
                accessibilityRole="button"
                onPress={() => void runtime.retry()}
                style={({ pressed }) => [
                  styles.secondaryButton,
                  pressed && styles.secondaryButtonPressed,
                ]}
                testID="registration-recovery-retry"
              >
                <Text style={styles.secondaryButtonLabel}>
                  {t("registration.recoveryRetry")}
                </Text>
              </PosPressable>
            ) : pending ? (
              <PosPressable
                accessibilityRole="button"
                onPress={() => void poll()}
                style={({ pressed }) => [
                  styles.secondaryButton,
                  pressed && styles.secondaryButtonPressed,
                ]}
              >
                <Text style={styles.secondaryButtonLabel}>
                  {t("registration.checkNow")}
                </Text>
              </PosPressable>
            ) : activationPreview ? (
              <PosPressable
                accessibilityRole="button"
                accessibilityState={{ disabled: activationBusy }}
                disabled={activationBusy}
                onPress={() => void redeemActivation()}
                style={({ pressed }) => [
                  styles.primaryButton,
                  pressed && styles.primaryButtonPressed,
                  activationBusy && styles.primaryButtonDisabled,
                ]}
                testID="registration-redeem"
              >
                <Text style={styles.primaryButtonLabel}>
                  {operation === "redeem"
                    ? t("registration.redeeming")
                    : t("registration.confirmActivation")}
                </Text>
              </PosPressable>
            ) : (
              <PosPressable
                accessibilityRole="button"
                accessibilityState={{
                  disabled:
                    activationBusy ||
                    runtime.state.database !== "ready" ||
                    !activationReady,
                }}
                disabled={
                  activationBusy ||
                  runtime.state.database !== "ready" ||
                  !activationReady
                }
                onPress={() => void previewActivation(activationCode)}
                style={({ pressed }) => [
                  styles.secondaryButton,
                  pressed && styles.secondaryButtonPressed,
                  (activationBusy || !activationReady) && styles.primaryButtonDisabled,
                ]}
                testID="registration-preview"
              >
                <Text style={styles.secondaryButtonLabel}>
                  {operation === "preview"
                    ? t("registration.previewing")
                    : t("registration.preview")}
                </Text>
              </PosPressable>
            )}

            {serverConnection && !locked ? (
              <View style={styles.serverConnectionPanel}>
                <ServerConnectionPanel
                  canSave={runtime.state.phase === "registration-required"}
                  copy={serverConnectionPanelCopy(t)}
                  currentAddress={serverConnection.getCurrentApiBaseUrl()}
                  saveAddress={async (address) => {
                    const controller = new AbortController();
                    serverOperation.current?.abort();
                    serverOperation.current = controller;
                    const result = await serverConnection.change(
                      address,
                      controller.signal,
                    );
                    if (result.status !== "completed") throw new Error(result.reason);
                    await runtime.retry();
                  }}
                  testAddress={(address) => {
                    const controller = new AbortController();
                    serverOperation.current?.abort();
                    serverOperation.current = controller;
                    return serverConnection.test(address, controller.signal);
                  }}
                />
              </View>
            ) : null}
          </View>
        </PosKeyboardAwareScrollView>
      </HandheldStateSurface>

      {scannerRouter ? (
        <CameraScannerModal
          context="device-activation"
          onClose={() => setCameraVisible(false)}
          onScan={(value) => void previewActivation(value)}
          scanner={scannerRouter}
          visible={cameraVisible}
        />
      ) : null}
    </SafeAreaView>
  );
}

function RegistrationStateCard({ state }: Readonly<{ state: VisibleRegistrationState }>) {
  const { t } = useTranslation();
  return (
    <HandheldStateSurface slug="registration-states" style={styles.registrationStateSurface}>
      <View
        accessibilityLiveRegion="polite"
        style={[
          styles.registrationState,
          state === "approved" && styles.registrationStateApproved,
          (state === "rejected" || state === "disabled") &&
            styles.registrationStateRejected,
        ]}
        testID={`registration-state-${state}`}
      >
        <MaterialCommunityIcons
          color={
            state === "approved"
              ? posColors.green
              : state === "pending"
                ? posColors.orange
                : posColors.red
          }
          name={
            state === "approved"
              ? "check-circle-outline"
              : state === "pending"
                ? "clock-outline"
                : "alert-circle-outline"
          }
          size={22}
        />
        <View style={styles.registrationStateCopy}>
          <Text style={styles.registrationStateTitle}>
            {t(`registration.state.${state}`)}
          </Text>
          <Text style={styles.registrationStateHint}>
            {t(`registration.state.${state}Hint`)}
          </Text>
        </View>
      </View>
    </HandheldStateSurface>
  );
}

function applyRequestFailure(
  error: unknown,
  runtime: ReturnType<typeof usePosRuntime>,
  setRequestError: Dispatch<SetStateAction<string | null>>,
  t: RegistrationTranslate,
) {
  if (error instanceof HbposApiError) {
    if (error.status === 403) {
      runtime.updateOperationalState({ backend: "rejected", device: "locked" });
    } else if (error.kind === "transport") {
      runtime.updateOperationalState({
        backend: "offline",
        device:
          runtime.state.device === "pending-approval"
            ? "pending-approval"
            : "registration-required",
      });
    }
    setRequestError(describeDeviceRegistrationFailure(error, t));
    return;
  }
  setRequestError(describeDeviceRegistrationFailure(error, t));
}

function FieldLabel({ children }: Readonly<{ children: string }>) {
  return <Text style={styles.fieldLabel}>{children}</Text>;
}

type VisibleRegistrationState = "pending" | "approved" | "rejected" | "disabled";

function registrationState(
  session: DeviceSessionState | null,
  runtimePhase: string,
): VisibleRegistrationState | null {
  switch (session?.status) {
    case "pending-approval":
      return "pending";
    case "authorized":
      return "approved";
    case "denied":
      return "rejected";
    case "disabled":
      return "disabled";
    default:
      return runtimePhase === "pending-approval" ? "pending" : null;
  }
}

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: posColors.canvas },
  stateSurface: { flex: 1 },
  languageBar: {
    minHeight: 48,
    alignItems: "flex-end",
    justifyContent: "center",
    paddingHorizontal: 16,
  },
  languageButton: {
    minWidth: 108,
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    paddingHorizontal: 16,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  languageButtonPressed: { backgroundColor: posColors.blueSoft },
  languageButtonLabel: { color: posColors.ink, fontSize: 13, fontWeight: "800" },
  page: { flexGrow: 1, padding: 16, gap: 16 },
  contextPanel: { width: "100%" },
  eyebrow: { color: posColors.orange, fontSize: 11, fontWeight: "900", letterSpacing: 1.8 },
  title: { marginTop: 8, color: posColors.ink, fontSize: 28, fontWeight: "900", lineHeight: 34 },
  subtitle: { marginTop: 8, color: posColors.mutedInk, fontSize: 14, lineHeight: 20 },
  securityNote: {
    marginTop: 16,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 12,
    borderLeftWidth: 3,
    borderLeftColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  securityCopy: { flex: 1, color: posColors.ink, fontSize: 13, fontWeight: "600", lineHeight: 20 },
  formPanel: {
    width: "100%",
    padding: 16,
    borderRadius: 6,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  formTitle: { color: posColors.ink, fontSize: 20, fontWeight: "900" },
  formHint: { marginTop: 8, marginBottom: 16, color: posColors.mutedInk, fontSize: 14, lineHeight: 21 },
  fieldLabel: { marginTop: 14, marginBottom: 8, color: posColors.ink, fontSize: 13, fontWeight: "800" },
  activationCodeInput: {
    minHeight: 52,
    paddingHorizontal: 14,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  activationCodeHint: { marginTop: 7, color: posColors.mutedInk, fontSize: 12, lineHeight: 18 },
  activationPreview: {
    marginTop: 18,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 12,
    padding: 14,
    borderLeftWidth: 4,
    borderLeftColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  activationPreviewLabel: { color: posColors.green, fontSize: 11, fontWeight: "900", letterSpacing: 1.1 },
  activationPreviewStore: { marginTop: 5, color: posColors.ink, fontSize: 17, fontWeight: "900" },
  activationPreviewMeta: { marginTop: 3, color: posColors.mutedInk, fontSize: 12, lineHeight: 18 },
  errorBanner: { marginTop: 18, padding: 12, backgroundColor: "#FEE4E2" },
  errorBannerText: { color: "#912018", fontSize: 13, fontWeight: "700" },
  primaryButton: {
    minHeight: 52,
    marginTop: 20,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 9,
    backgroundColor: posColors.ink,
  },
  primaryButtonPressed: { opacity: 0.7 },
  primaryButtonDisabled: { opacity: 0.45 },
  primaryButtonLabel: { color: "#FFFFFF", fontSize: 15, fontWeight: "900" },
  secondaryButton: {
    minHeight: 50,
    marginTop: 18,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    borderColor: posColors.ink,
  },
  secondaryButtonPressed: { backgroundColor: posColors.blueSoft },
  secondaryButtonLabel: { color: posColors.ink, fontSize: 14, fontWeight: "800" },
  pendingCard: {
    flexDirection: "row",
    alignItems: "center",
    gap: 14,
    padding: 16,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFF7ED",
  },
  pendingCopy: { flex: 1 },
  pendingCode: { color: posColors.ink, fontSize: 17, fontWeight: "900" },
  pendingStore: { marginTop: 4, color: posColors.mutedInk, fontSize: 13, fontWeight: "700" },
  registrationStateSurface: { marginBottom: 8 },
  registrationState: {
    alignItems: "flex-start",
    backgroundColor: "#FFF7ED",
    borderColor: posColors.orange,
    borderRadius: 6,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    padding: 12,
  },
  registrationStateApproved: { backgroundColor: posColors.greenSoft, borderColor: posColors.green },
  registrationStateRejected: { backgroundColor: posColors.redSoft, borderColor: posColors.red },
  registrationStateCopy: { flex: 1 },
  registrationStateTitle: { color: posColors.ink, fontSize: 14, fontWeight: "900" },
  registrationStateHint: { color: posColors.mutedInk, fontSize: 12, lineHeight: 18, marginTop: 2 },
  serverConnectionPanel: { marginTop: 22 },
});
