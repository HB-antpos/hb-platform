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
import {
  Image,
  Keyboard,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { reconcileDeviceSessionRuntime } from "./device-registration-state";
import { serverConnectionPanelCopy } from "./server-connection-copy";
import { ServerConnectionPanel } from "./server-connection-panel";

import {
  HbposApiError,
  type DeviceActivationPreviewResponse,
  type DeviceRegistrationStore,
} from "@/core/api/hbpos-api";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  normalizeDeviceActivationCode,
  parseDeviceActivationCode,
} from "@/core/security/device-activation-code";
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
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

type StoreLoadState = "idle" | "loading" | "ready" | "failed";
type RegistrationOperation = "preview" | "redeem" | "app-review" | null;
type RegistrationKind = "unresolved" | "activation" | "app-review";
type ActivationPreview = Readonly<{
  activationCode: string;
  response: DeviceActivationPreviewResponse;
}>;

export function DeviceRegistrationScreen({
  onOpenNetworkCheck,
}: Readonly<{
  /** 开通 / 待审批时也能打开只读网络检测，排查连不上服务器的问题。 */
  onOpenNetworkCheck?: () => void;
}> = {}) {
  const { t } = useTranslation();
  const runtime = usePosRuntime();
  const [session, setSession] = useState<DeviceSessionState | null>(null);
  const [requestError, setRequestError] = useState<string | null>(null);
  const [registrationCode, setRegistrationCode] = useState("");
  const [activationPreview, setActivationPreview] =
    useState<ActivationPreview | null>(null);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [operation, setOperation] = useState<RegistrationOperation>(null);
  const [registrationKind, setRegistrationKind] =
    useState<RegistrationKind>("unresolved");
  const [stores, setStores] = useState<readonly DeviceRegistrationStore[]>([]);
  const [storeLoadState, setStoreLoadState] =
    useState<StoreLoadState>("idle");
  const [storeLoadError, setStoreLoadError] = useState<string | null>(null);
  const [selectedStoreCode, setSelectedStoreCode] = useState("");
  const [pickerVisible, setPickerVisible] = useState(false);
  const serverOperation = useRef<AbortController | null>(null);
  const previewRequestId = useRef(0);
  const deviceSession = runtime.services?.deviceSession;
  const scannerRouter = runtime.services?.scanner.router;
  const serverConnection = runtime.services?.serverConnection;
  const selectedStore =
    stores.find((store) => store.storeCode === selectedStoreCode) ?? null;

  const reconcile = useCallback(
    async (next: DeviceSessionState) => {
      setSession(next);
      await reconcileDeviceSessionRuntime(next, runtime);
    },
    [runtime],
  );

  const loadStores = useCallback(async () => {
    if (!deviceSession) {
      setStoreLoadState("failed");
      setStoreLoadError(null);
      return;
    }

    setStoreLoadState("loading");
    setStoreLoadError(null);
    try {
      const nextStores = await deviceSession.listRegistrationStores();
      setStores(nextStores);
      setSelectedStoreCode((current) =>
        nextStores.some((store) => store.storeCode === current)
          ? current
          : "",
      );
      setStoreLoadState("ready");
    } catch (error: unknown) {
      setStores([]);
      setStoreLoadState("failed");
      setStoreLoadError(error instanceof Error ? error.message : null);
    }
  }, [deviceSession]);

  useEffect(() => {
    if (
      registrationKind !== "app-review" ||
      runtime.state.phase === "pending-approval" ||
      runtime.state.phase === "locked"
    ) {
      return;
    }
    void loadStores();
  }, [loadStores, registrationKind, runtime.state.phase]);

  useEffect(() => {
    if (
      !deviceSession ||
      runtime.state.phase !== "registration-required"
    ) {
      return;
    }
    let cancelled = false;
    void deviceSession
      .restorePendingActivationCode()
      .then((restored) => {
        if (!cancelled && restored) setRegistrationCode(restored);
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

  useEffect(() => {
    if (
      runtime.state.phase !== "pending-approval" ||
      !deviceSession
    ) {
      return;
    }

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

  const previewActivation = useCallback(async (rawCode: string) => {
    const requestId = ++previewRequestId.current;
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
    setRegistrationKind("activation");
    setRegistrationCode(normalized);
    setOperation("preview");
    try {
      const response = await deviceSession.previewActivationCode(normalized);
      if (requestId !== previewRequestId.current) return;
      const storeCode = response.storeCode?.trim() ?? "";
      const storeName = response.storeName?.trim() ?? "";
      const expiresAtUtc = response.expiresAtUtc?.trim() ?? "";
      const platformMatches = response.deviceSystem === "iPadOS";
      if (
        !response.isAllowed ||
        !storeCode ||
        !storeName ||
        !expiresAtUtc ||
        !platformMatches
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
      if (requestId === previewRequestId.current) {
        applyRequestFailure(error, runtime, setRequestError, t);
      }
    } finally {
      if (requestId === previewRequestId.current) setOperation(null);
    }
  }, [deviceSession, runtime, t]);

  const identifyRegistrationCode = useCallback((rawCode: string) => {
    Keyboard.dismiss();
    const trimmed = rawCode.trim();
    if (!trimmed) {
      setRequestError(t("registration.codeRequired"));
      return;
    }

    previewRequestId.current += 1;
    setRegistrationCode(rawCode);
    setActivationPreview(null);
    setSelectedStoreCode("");
    setPickerVisible(false);
    setRequestError(null);

    // HBDEV1 是保留前缀：即使格式损坏也只能走严格开通码校验，禁止降级到 App Review。
    if (normalizeDeviceActivationCode(trimmed).startsWith("HBDEV1")) {
      setRegistrationKind("activation");
      void previewActivation(rawCode);
      return;
    }

    setRegistrationCode(rawCode);
    setRegistrationKind("app-review");
  }, [previewActivation, t]);

  if (
    runtime.state.phase === "ready" ||
    runtime.state.phase === "ready-offline"
  ) {
    return <Redirect href="/" />;
  }

  const redeemActivation = async () => {
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
        setRegistrationCode("");
        setActivationPreview(null);
        setRegistrationKind("unresolved");
      }
      await reconcile(next);
    } catch (error: unknown) {
      applyRequestFailure(error, runtime, setRequestError, t);
    } finally {
      setOperation(null);
    }
  };

  const submitAppReview = async () => {
    if (!deviceSession || !selectedStore) {
      setRequestError(t("registration.storeRequired"));
      return;
    }
    const provisioningCode = registrationCode.trim();
    if (!provisioningCode) {
      setRequestError(t("registration.appReviewCodeRequired"));
      return;
    }
    setRequestError(null);
    setOperation("app-review");
    try {
      const next = await deviceSession.registerAppReview({
        storeCode: selectedStore.storeCode,
        provisioningCode,
      });
      await reconcile(next);
    } catch (error: unknown) {
      applyRequestFailure(error, runtime, setRequestError, t);
    } finally {
      setRegistrationCode("");
      setRegistrationKind("unresolved");
      setSelectedStoreCode("");
      setPickerVisible(false);
      setOperation(null);
    }
  };

  const changeRegistrationCode = (value: string) => {
    previewRequestId.current += 1;
    setRegistrationCode(value);
    setRegistrationKind("unresolved");
    setActivationPreview(null);
    setSelectedStoreCode("");
    setPickerVisible(false);
    setRequestError(null);
  };

  const registrationBusy = operation !== null;
  const registrationReady = registrationCode.trim().length > 0;

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
          <MaterialCommunityIcons
            color={posColors.ink}
            name="translate"
            size={18}
          />
          <Text style={styles.languageButtonLabel}>
            {t("registration.languageSwitch")}
          </Text>
        </PosPressable>
      </View>
      <View style={styles.page}>
        <View style={styles.contextPanel}>
          <Image
            accessibilityIgnoresInvertColors
            accessible={false}
            resizeMode="contain"
            source={require("../../../assets/icon.png")}
            style={styles.brandMark}
          />
          <Text style={styles.eyebrow}>{t("registration.eyebrow")}</Text>
          <Text style={styles.title}>{t("registration.title")}</Text>
          <Text style={styles.subtitle}>{t("registration.subtitle")}</Text>

          <View style={styles.securityNote}>
            <MaterialCommunityIcons
              color={posColors.green}
              name="shield-lock-outline"
              size={24}
            />
            <Text style={styles.securityCopy}>
              {t("registration.securityNote")}
            </Text>
          </View>
        </View>

        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.formPanelContent}
          keyboardRevealOffset={112}
          style={styles.formPanel}
        >
          <Text style={styles.formTitle}>
            {runtime.state.phase === "locked"
              ? t("registration.lockedTitle")
              : runtime.state.phase === "pending-approval"
                ? t("registration.pendingTitle")
                : t("registration.formTitle")}
          </Text>
          <Text style={styles.formHint}>
            {runtime.state.phase === "locked"
              ? t("registration.lockedHint")
              : runtime.state.phase === "pending-approval"
                ? t("registration.pendingHint")
                : t("registration.formHint")}
          </Text>

          {runtime.state.phase === "locked" ? (
            <View
              accessibilityRole="alert"
              style={styles.pendingCard}
              testID="registration-recovery-readonly"
            >
              <MaterialCommunityIcons
                color={posColors.red}
                name="shield-alert-outline"
                size={34}
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
          ) : runtime.state.phase === "pending-approval" ? (
            <View style={styles.pendingCard}>
              <MaterialCommunityIcons
                color={posColors.orange}
                name="clock-outline"
                size={34}
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
                accessibilityState={{
                  disabled: registrationBusy || !scannerRouter,
                }}
                disabled={registrationBusy || !scannerRouter}
                onPress={() => setCameraVisible(true)}
                style={({ pressed }) => [
                  styles.primaryButton,
                  pressed && styles.primaryButtonPressed,
                  (!scannerRouter || registrationBusy) &&
                    styles.primaryButtonDisabled,
                ]}
                testID="registration-scan"
              >
                <MaterialCommunityIcons
                  color="#FFFFFF"
                  name="qrcode-scan"
                  size={22}
                />
                <Text style={styles.primaryButtonLabel}>
                  {t("registration.scan")}
                </Text>
              </PosPressable>

              <FieldLabel>{t("registration.activationCode")}</FieldLabel>
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("registration.activationCode")}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!registrationBusy}
                maxLength={128}
                onChangeText={changeRegistrationCode}
                onSubmitEditing={() =>
                  identifyRegistrationCode(registrationCode)
                }
                placeholder={t("registration.activationCodePlaceholder")}
                placeholderTextColor={posColors.mutedInk}
                secureTextEntry
                style={styles.provisioningCodeInput}
                testID="registration-activation-code"
                textContentType="oneTimeCode"
                returnKeyType="go"
                value={registrationCode}
              />
              <Text style={styles.provisioningCodeHint}>
                {t("registration.activationCodeHint")}
              </Text>

              {registrationKind === "app-review" ? (
                <>
                  <View style={styles.compatibilityNote}>
                    <Text style={styles.compatibilityTitle}>
                      {t("registration.appReviewTitle")}
                    </Text>
                    <Text style={styles.compatibilityHint}>
                      {t("registration.appReviewHint")}
                    </Text>
                  </View>

                  <FieldLabel>{t("registration.storeCode")}</FieldLabel>
                  <PosPressable
                    accessibilityRole="button"
                    accessibilityState={{
                      disabled:
                        storeLoadState !== "ready" || stores.length === 0,
                      expanded: pickerVisible,
                    }}
                    disabled={
                      storeLoadState !== "ready" || stores.length === 0
                    }
                    onPress={() => setPickerVisible(true)}
                    sound="navigate"
                    style={({ pressed }) => [
                      styles.storePicker,
                      pressed && styles.storePickerPressed,
                    ]}
                    testID="registration-store-picker"
                  >
                    <View style={styles.storePickerCopy}>
                      <Text
                        style={[
                          styles.storePickerValue,
                          !selectedStore && styles.storePickerPlaceholder,
                        ]}
                      >
                        {selectedStore
                          ? storeDisplayName(selectedStore)
                          : storePickerMessage(
                              storeLoadState,
                              stores.length,
                              t,
                            )}
                      </Text>
                    </View>
                    <MaterialCommunityIcons
                      color={posColors.ink}
                      name="chevron-down"
                      size={24}
                    />
                  </PosPressable>

                  {storeLoadState === "failed" ||
                  (storeLoadState === "ready" && stores.length === 0) ? (
                    <View style={styles.storeStatus}>
                      <Text style={styles.storeStatusText}>
                        {storeLoadError ?? (
                          storeLoadState === "failed"
                            ? t("registration.storeLoadFailed")
                            : t("registration.storeEmpty")
                        )}
                      </Text>
                      <PosPressable
                        accessibilityRole="button"
                        onPress={() => void loadStores()}
                        style={({ pressed }) => [
                          styles.retryButton,
                          pressed && styles.retryButtonPressed,
                        ]}
                        testID="registration-store-retry"
                      >
                        <Text style={styles.retryButtonLabel}>
                          {t("registration.storeRetry")}
                        </Text>
                      </PosPressable>
                    </View>
                  ) : null}
                </>
              ) : null}

              {activationPreview ? (
                <View style={styles.activationPreview}>
                  <MaterialCommunityIcons
                    color={posColors.green}
                    name="store-check-outline"
                    size={28}
                  />
                  <View style={styles.pendingCopy}>
                    <Text style={styles.activationPreviewLabel}>
                      {t("registration.activationPreviewTitle")}
                    </Text>
                    <Text style={styles.activationPreviewStore}>
                      {activationPreview.response.storeName} ·{" "}
                      {activationPreview.response.storeCode}
                    </Text>
                    <Text style={styles.activationPreviewExpiry}>
                      {t("registration.activationPlatform", {
                        value: activationPreview.response.deviceSystem,
                      })}
                    </Text>
                    <Text style={styles.activationPreviewExpiry}>
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

          {runtime.state.phase === "locked" ? (
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
          ) : runtime.state.phase === "pending-approval" ? (
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
          ) : registrationKind === "app-review" ? (
            <PosPressable
              accessibilityRole="button"
              accessibilityState={{
                disabled:
                  operation === "app-review" ||
                  !selectedStore ||
                  !registrationCode.trim(),
              }}
              disabled={
                operation === "app-review" ||
                !selectedStore ||
                !registrationCode.trim()
              }
              onPress={() => void submitAppReview()}
              style={({ pressed }) => [
                styles.primaryButton,
                pressed && styles.primaryButtonPressed,
                (operation === "app-review" ||
                  !selectedStore ||
                  !registrationCode.trim()) && styles.primaryButtonDisabled,
              ]}
              testID="registration-app-review-submit"
            >
              <Text style={styles.primaryButtonLabel}>
                {operation === "app-review"
                  ? t("registration.appReviewSubmitting")
                  : t("registration.appReviewSubmit")}
              </Text>
            </PosPressable>
          ) : activationPreview ? (
            <PosPressable
              accessibilityRole="button"
              accessibilityState={{ disabled: registrationBusy }}
              disabled={registrationBusy}
              onPress={() => void redeemActivation()}
              style={({ pressed }) => [
                styles.primaryButton,
                pressed && styles.primaryButtonPressed,
                registrationBusy && styles.primaryButtonDisabled,
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
                  registrationBusy ||
                  runtime.state.database !== "ready" ||
                  !registrationReady,
              }}
              disabled={
                registrationBusy ||
                runtime.state.database !== "ready" ||
                !registrationReady
              }
              onPress={() => identifyRegistrationCode(registrationCode)}
              style={({ pressed }) => [
                styles.secondaryButton,
                pressed && styles.secondaryButtonPressed,
                (registrationBusy || !registrationReady) &&
                  styles.primaryButtonDisabled,
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

          {serverConnection && runtime.state.phase !== "locked" ? (
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
                  if (result.status !== "completed") {
                    throw new Error(result.reason);
                  }
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
          {onOpenNetworkCheck ? (
            <PosPressable
              accessibilityRole="button"
              onPress={onOpenNetworkCheck}
              sound="navigate"
              style={({ pressed }) => [
                styles.secondaryButton,
                pressed && styles.secondaryButtonPressed,
              ]}
              testID="registration-network-check"
            >
              <Text style={styles.secondaryButtonLabel}>
                {t("registration.networkCheck")}
              </Text>
            </PosPressable>
          ) : null}
        </PosKeyboardAwareScrollView>
      </View>
      <StorePickerOverlay
        onClose={() => setPickerVisible(false)}
        onSelect={(storeCode) => {
          setSelectedStoreCode(storeCode);
          setRequestError(null);
          setPickerVisible(false);
        }}
        selectedStoreCode={selectedStoreCode}
        stores={stores}
        visible={registrationKind === "app-review" && pickerVisible}
      />
      {scannerRouter ? (
        <CameraScannerModal
          context="device-activation"
          onClose={() => setCameraVisible(false)}
          onScan={(value) => {
            setCameraVisible(false);
            identifyRegistrationCode(value);
          }}
          scanner={scannerRouter}
          visible={cameraVisible}
        />
      ) : null}
    </SafeAreaView>
  );
}

function StorePickerOverlay({
  onClose,
  onSelect,
  selectedStoreCode,
  stores,
  visible,
}: Readonly<{
  onClose(): void;
  onSelect(storeCode: string): void;
  selectedStoreCode: string;
  stores: readonly DeviceRegistrationStore[];
  visible: boolean;
}>) {
  const { t } = useTranslation();
  if (!visible) {
    return null;
  }

  return (
    <View style={styles.modalBackdrop}>
      <PosPressable
        accessible={false}
        onPress={onClose}
        sound="navigate"
        style={styles.backdropDismissArea}
        testID="registration-store-backdrop"
      />
      <View
        accessibilityViewIsModal
        style={styles.modalCard}
        testID="registration-store-modal"
      >
        <View style={styles.modalHeader}>
          <Text style={styles.modalTitle}>
            {t("registration.storePickerTitle")}
          </Text>
          <PosPressable
            accessibilityRole="button"
            onPress={onClose}
            sound="navigate"
            style={({ pressed }) => [
              styles.modalClose,
              pressed && styles.modalClosePressed,
            ]}
          >
            <Text style={styles.modalCloseLabel}>
              {t("registration.storePickerClose")}
            </Text>
          </PosPressable>
        </View>
        <ScrollView
          contentContainerStyle={styles.storeList}
          keyboardShouldPersistTaps="handled"
        >
          {stores.map((store) => {
            const selected = store.storeCode === selectedStoreCode;
            return (
              <PosPressable
                accessibilityRole="button"
                accessibilityState={{ selected }}
                key={store.storeCode}
                onPress={() => onSelect(store.storeCode)}
                style={({ pressed }) => [
                  styles.storeOption,
                  selected && styles.storeOptionSelected,
                  pressed && styles.storeOptionPressed,
                ]}
                testID={`registration-store-${store.storeCode}`}
              >
                <View style={styles.storeOptionCopy}>
                  <Text style={styles.storeOptionName}>
                    {store.storeName}
                  </Text>
                  <Text style={styles.storeOptionCode}>
                    {store.storeCode}
                  </Text>
                </View>
                {selected ? (
                  <MaterialCommunityIcons
                    color={posColors.green}
                    name="check-circle"
                    size={22}
                  />
                ) : null}
              </PosPressable>
            );
          })}
        </ScrollView>
      </View>
    </View>
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
      runtime.updateOperationalState({
        backend: "rejected",
        device: "locked",
      });
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

function FieldLabel({ children }: { children: string }) {
  return <Text style={styles.fieldLabel}>{children}</Text>;
}

function storeDisplayName(store: DeviceRegistrationStore): string {
  return `${store.storeName} · ${store.storeCode}`;
}

function storePickerMessage(
  state: StoreLoadState,
  storeCount: number,
  t: ReturnType<typeof useTranslation>["t"],
): string {
  if (state === "loading" || state === "idle") {
    return t("registration.storeLoading");
  }
  if (state === "failed") {
    return t("registration.storeLoadFailed");
  }
  if (storeCount === 0) {
    return t("registration.storeEmpty");
  }
  return t("registration.storePlaceholder");
}

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: posColors.canvas },
  languageBar: {
    minHeight: 48,
    alignItems: "flex-end",
    justifyContent: "center",
    paddingHorizontal: 48,
  },
  languageButton: {
    minWidth: 108,
    minHeight: 44,
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
  languageButtonLabel: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  page: {
    flex: 1,
    flexDirection: "row",
    paddingHorizontal: 48,
    paddingTop: 8,
    paddingBottom: 34,
    gap: 56,
  },
  contextPanel: { flex: 1, justifyContent: "center", maxWidth: 520 },
  brandMark: {
    borderRadius: 12,
    height: 52,
    width: 52,
  },
  eyebrow: {
    marginTop: 28,
    color: posColors.orange,
    fontSize: 11,
    fontWeight: "900",
    letterSpacing: 1.8,
  },
  title: {
    marginTop: 14,
    color: posColors.ink,
    fontSize: 42,
    fontWeight: "900",
    lineHeight: 48,
  },
  subtitle: {
    marginTop: 18,
    color: posColors.mutedInk,
    fontSize: 17,
    lineHeight: 27,
  },
  securityNote: {
    marginTop: 36,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 16,
    borderLeftWidth: 4,
    borderLeftColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  securityCopy: {
    flex: 1,
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 20,
  },
  formPanel: {
    width: 430,
    alignSelf: "center",
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  formPanelContent: {
    padding: 30,
  },
  formTitle: { color: posColors.ink, fontSize: 25, fontWeight: "900" },
  formHint: {
    marginTop: 8,
    marginBottom: 24,
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 21,
  },
  compatibilityNote: {
    marginTop: 18,
    marginBottom: 4,
    padding: 14,
    borderLeftWidth: 3,
    borderLeftColor: posColors.orange,
    backgroundColor: "#FFF7ED",
  },
  compatibilityTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "900",
  },
  compatibilityHint: {
    marginTop: 5,
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
  },
  fieldLabel: {
    marginTop: 14,
    marginBottom: 8,
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  storePicker: {
    minHeight: 50,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 15,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  storePickerPressed: { backgroundColor: posColors.blueSoft },
  storePickerCopy: { flex: 1, paddingRight: 12 },
  storePickerValue: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "700",
  },
  storePickerPlaceholder: {
    color: posColors.mutedInk,
    fontWeight: "500",
  },
  provisioningCodeInput: {
    minHeight: 50,
    paddingHorizontal: 15,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "700",
  },
  provisioningCodeHint: {
    marginTop: 7,
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
  },
  activationPreview: {
    marginTop: 18,
    padding: 14,
    borderLeftWidth: 4,
    borderLeftColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  activationPreviewLabel: {
    color: posColors.green,
    fontSize: 11,
    fontWeight: "900",
    letterSpacing: 1.1,
  },
  activationPreviewStore: {
    marginTop: 6,
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  activationPreviewExpiry: {
    marginTop: 4,
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
  },
  storeStatus: {
    marginTop: 10,
    padding: 12,
    borderLeftWidth: 3,
    borderLeftColor: posColors.orange,
    backgroundColor: "#FFF7ED",
  },
  storeStatusText: {
    color: posColors.ink,
    fontSize: 13,
    lineHeight: 19,
  },
  retryButton: {
    minHeight: 44,
    marginTop: 8,
    alignSelf: "flex-start",
    justifyContent: "center",
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: posColors.ink,
  },
  retryButtonPressed: { backgroundColor: posColors.blueSoft },
  retryButtonLabel: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  errorBanner: {
    marginTop: 18,
    padding: 12,
    backgroundColor: "#FEE4E2",
  },
  errorBannerText: { color: "#912018", fontSize: 13, fontWeight: "700" },
  primaryButton: {
    minHeight: 52,
    marginTop: 24,
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
    marginTop: 20,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    borderColor: posColors.ink,
  },
  secondaryButtonPressed: { backgroundColor: posColors.blueSoft },
  secondaryButtonLabel: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  pendingCard: {
    flexDirection: "row",
    alignItems: "center",
    gap: 16,
    padding: 20,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFF7ED",
  },
  pendingCopy: { flex: 1 },
  pendingCode: { color: posColors.ink, fontSize: 18, fontWeight: "900" },
  pendingStore: {
    marginTop: 4,
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  serverConnectionPanel: {
    marginTop: 22,
  },
  modalBackdrop: {
    ...StyleSheet.absoluteFillObject,
    zIndex: 20,
    alignItems: "center",
    justifyContent: "center",
    padding: 32,
    backgroundColor: "rgba(10, 31, 50, 0.42)",
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  modalCard: {
    width: "100%",
    maxWidth: 560,
    maxHeight: "76%",
    padding: 24,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  modalHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 16,
    paddingBottom: 16,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
  },
  modalTitle: {
    flex: 1,
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "900",
  },
  modalClose: {
    minWidth: 72,
    minHeight: 44,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: posColors.border,
  },
  modalClosePressed: { backgroundColor: posColors.blueSoft },
  modalCloseLabel: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  storeList: { paddingTop: 12, gap: 8 },
  storeOption: {
    minHeight: 58,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 10,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  storeOptionSelected: {
    borderColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  storeOptionPressed: { opacity: 0.72 },
  storeOptionCopy: { flex: 1 },
  storeOptionName: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  storeOptionCode: {
    marginTop: 3,
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
});
