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
import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

type StoreLoadState = "idle" | "loading" | "ready" | "failed";
type RegistrationOperation = "preview" | "redeem" | "app-review" | null;
type RegistrationKind = "unresolved" | "activation" | "app-review";
type ActivationPreview = Readonly<{
  activationCode: string;
  response: DeviceActivationPreviewResponse;
}>;

/**
 * 新布局才有的版式文案（全局语言包里没有对应键）；中英文两套键保持一致。
 */
const layoutCopy = {
  en: { manualEntryDivider: "or enter it manually" },
  zh: { manualEntryDivider: "或手动输入" },
} as const;

export function DeviceRegistrationScreen({
  onOpenNetworkCheck,
}: Readonly<{
  /** 开通 / 待审批时也能打开只读网络检测，排查连不上服务器的问题。 */
  onOpenNetworkCheck?: () => void;
}> = {}) {
  usePosThemeVersion();
  const { i18n, t } = useTranslation();
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

  // 语言切换并入外壳顶栏；切换只改界面语言，已输入的注册码留在本页状态里。
  const switchLanguage = useCallback(() => {
    void toggleAppLanguage();
  }, []);

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
  const phase = runtime.state.phase;
  // 已识别出注册码类型（App Review 选店 / 服务端已确认分店）后，主按钮让给下一步操作，
  // 「扫描」降为普通按钮，保证一屏只有一个主按钮。
  const staged = registrationKind === "app-review" || activationPreview !== null;
  const appReviewSubmitDisabled =
    operation === "app-review" || !selectedStore || !registrationCode.trim();
  const storePickerDisabled =
    storeLoadState !== "ready" || stores.length === 0;
  const text =
    layoutCopy[
      (i18n.resolvedLanguage ?? i18n.language ?? "zh")
        .toLowerCase()
        .startsWith("zh")
        ? "zh"
        : "en"
    ];
  const networkCheckButton = onOpenNetworkCheck ? (
    <PosButton
      icon="access-point-network"
      label={t("registration.networkCheck")}
      onPress={onOpenNetworkCheck}
      size="medium"
      sound="navigate"
      testID="registration-network-check"
      tone="quiet"
    />
  ) : null;

  return (
    <PosScreenFrame
      onSwitchLanguage={switchLanguage}
      // 注册完成前不允许跳去其他页面，这一页不显示左侧栏。
      rail="none"
    >
      <StatusBar style="light" />
      <View style={styles.page}>
        {/* 左侧深色品牌区：Logo、眉标、标题、说明；安全提示压在底部。 */}
        <View style={styles.brandPanel}>
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
          <View style={styles.brandSpacer} />
          <View style={styles.securityNote}>
            <MaterialCommunityIcons
              color={posColors.onInverse}
              name="shield-lock-outline"
              size={20}
              style={styles.securityIcon}
            />
            <Text style={styles.securityCopy}>
              {t("registration.securityNote")}
            </Text>
          </View>
        </View>

        {/* 右侧表单：所有状态（注册 / 等待审批 / 需要恢复 / 确认分店 / App Review 选店）都排在这一栏。 */}
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.formPanelContent}
          keyboardRevealOffset={112}
          showsVerticalScrollIndicator={false}
          style={styles.formPanel}
        >
          <View>
            <Text accessibilityRole="header" style={styles.formTitle}>
              {phase === "locked"
                ? t("registration.lockedTitle")
                : phase === "pending-approval"
                  ? t("registration.pendingTitle")
                  : t("registration.formTitle")}
            </Text>
            {/* 需要恢复时，说明文字由下方红色卡片承担，这里不再重复一遍。 */}
            {phase === "locked" ? null : (
              <Text style={styles.formHint}>
                {phase === "pending-approval"
                  ? t("registration.pendingHint")
                  : t("registration.formHint")}
              </Text>
            )}
          </View>

          {phase === "locked" ? (
            <View
              accessibilityRole="alert"
              style={[styles.stateCard, styles.stateCardDanger]}
              testID="registration-recovery-readonly"
            >
              <MaterialCommunityIcons
                color={posColors.red}
                name="shield-alert-outline"
                size={40}
              />
              <View style={styles.stateCopy}>
                <Text style={styles.stateTitle}>
                  {t("registration.lockedTitle")}
                </Text>
                <Text style={styles.stateDetail}>
                  {t("registration.lockedHint")}
                </Text>
              </View>
            </View>
          ) : phase === "pending-approval" ? (
            <View style={styles.stateCard}>
              <MaterialCommunityIcons
                color={posColors.accent}
                name="clock-outline"
                size={40}
              />
              <View style={styles.stateCopy}>
                <Text style={styles.stateTitle}>
                  {session?.deviceCode ?? t("registration.pendingDeviceCode")}
                </Text>
                <Text style={styles.stateDetail}>
                  {session?.storeCode ?? t("registration.pendingStore")}
                </Text>
              </View>
            </View>
          ) : (
            <>
              <PosButton
                disabled={registrationBusy || !scannerRouter}
                icon="qrcode-scan"
                label={t("registration.scan")}
                onPress={() => setCameraVisible(true)}
                size={staged ? "button" : "primary"}
                testID="registration-scan"
                tone={staged ? "secondary" : "primary"}
              />

              <View style={styles.divider}>
                <View style={styles.dividerLine} />
                <Text style={styles.dividerLabel}>
                  {text.manualEntryDivider}
                </Text>
                <View style={styles.dividerLine} />
              </View>

              <View>
                <FieldLabel>{t("registration.activationCode")}</FieldLabel>
                <View style={styles.codeRow}>
                  <View style={styles.codeInputContainer}>
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
                      placeholderTextColor={posColors.placeholder}
                      secureTextEntry
                      style={[
                        styles.codeInput,
                        // 占位提示用正文字号；有内容后放大遮罩圆点。
                        registrationCode.length > 0 && styles.codeInputFilled,
                        registrationBusy && styles.codeInputDisabled,
                      ]}
                      testID="registration-activation-code"
                      textContentType="oneTimeCode"
                      returnKeyType="go"
                      value={registrationCode}
                    />
                    <View pointerEvents="none" style={styles.codeInputIcon}>
                      <MaterialCommunityIcons
                        color={
                          registrationBusy
                            ? posColors.disabledInk
                            : posColors.mutedInk
                        }
                        name="key-outline"
                        size={32}
                      />
                    </View>
                  </View>
                  {staged ? null : (
                    <PosButton
                      disabled={
                        registrationBusy ||
                        runtime.state.database !== "ready" ||
                        !registrationReady
                      }
                      label={
                        operation === "preview"
                          ? t("registration.previewing")
                          : t("registration.preview")
                      }
                      loading={operation === "preview"}
                      numberOfLines={2}
                      onPress={() => identifyRegistrationCode(registrationCode)}
                      style={styles.continueButton}
                      testID="registration-preview"
                    />
                  )}
                </View>
                <Text style={styles.codeHint}>
                  {t("registration.activationCodeHint")}
                </Text>
              </View>

              {registrationKind === "app-review" ? (
                <>
                  <View style={styles.infoNote}>
                    <Text style={styles.infoNoteTitle}>
                      {t("registration.appReviewTitle")}
                    </Text>
                    <Text style={styles.infoNoteHint}>
                      {t("registration.appReviewHint")}
                    </Text>
                  </View>

                  <View>
                    <FieldLabel>{t("registration.storeCode")}</FieldLabel>
                    <PosPressable
                      accessibilityRole="button"
                      accessibilityState={{
                        disabled: storePickerDisabled,
                        expanded: pickerVisible,
                      }}
                      disabled={storePickerDisabled}
                      onPress={() => setPickerVisible(true)}
                      sound="navigate"
                      style={({ pressed }) => [
                        styles.storePicker,
                        storePickerDisabled && styles.storePickerDisabled,
                        pressed && !storePickerDisabled && styles.pressed,
                      ]}
                      testID="registration-store-picker"
                    >
                      <View style={styles.storePickerCopy}>
                        <Text
                          numberOfLines={1}
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
                        color={
                          storePickerDisabled
                            ? posColors.disabledInk
                            : posColors.ink
                        }
                        name="chevron-down"
                        size={28}
                      />
                    </PosPressable>
                  </View>

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
                      <PosButton
                        label={t("registration.storeRetry")}
                        onPress={() => void loadStores()}
                        size="small"
                        testID="registration-store-retry"
                      />
                    </View>
                  ) : null}
                </>
              ) : null}

              {activationPreview ? (
                <View style={styles.activationPreview}>
                  <MaterialCommunityIcons
                    color={posColors.green}
                    name="store-check-outline"
                    size={40}
                  />
                  <View style={styles.stateCopy}>
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
              <MaterialCommunityIcons
                color={posColors.red}
                name="alert-circle-outline"
                size={24}
              />
              <Text style={styles.errorBannerText}>{requestError}</Text>
            </View>
          ) : null}

          {/* 各状态的下一步操作：同一位置、同一尺寸（主按钮 88）。 */}
          {phase === "locked" ? (
            <PosButton
              icon="restart"
              label={t("registration.recoveryRetry")}
              onPress={() => void runtime.retry()}
              size="primary"
              testID="registration-recovery-retry"
              tone="primary"
            />
          ) : phase === "pending-approval" ? (
            <PosButton
              icon="refresh"
              label={t("registration.checkNow")}
              onPress={() => void poll()}
              size="primary"
              tone="primary"
            />
          ) : registrationKind === "app-review" ? (
            <PosButton
              disabled={appReviewSubmitDisabled}
              label={
                operation === "app-review"
                  ? t("registration.appReviewSubmitting")
                  : t("registration.appReviewSubmit")
              }
              loading={operation === "app-review"}
              onPress={() => void submitAppReview()}
              size="primary"
              testID="registration-app-review-submit"
              tone="primary"
            />
          ) : activationPreview ? (
            <PosButton
              disabled={registrationBusy}
              icon="shield-check-outline"
              label={
                operation === "redeem"
                  ? t("registration.redeeming")
                  : t("registration.confirmActivation")
              }
              loading={operation === "redeem"}
              onPress={() => void redeemActivation()}
              size="primary"
              testID="registration-redeem"
              tone="primary"
            />
          ) : null}

          {serverConnection && phase !== "locked" ? (
            <ServerConnectionPanel
              actions={networkCheckButton}
              canSave={phase === "registration-required"}
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
          ) : networkCheckButton ? (
            // 服务器面板不显示时（需要恢复 / 运行环境未就绪），网络检测入口单独保留。
            <View style={styles.standaloneNetworkCheck}>
              {networkCheckButton}
            </View>
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
    </PosScreenFrame>
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
  usePosThemeVersion();
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
          <PosButton
            label={t("registration.storePickerClose")}
            onPress={onClose}
            size="medium"
            sound="navigate"
          />
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
                  pressed && styles.pressed,
                ]}
                testID={`registration-store-${store.storeCode}`}
              >
                <View style={styles.storeOptionCopy}>
                  <Text numberOfLines={1} style={styles.storeOptionName}>
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
                    size={28}
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
  usePosThemeVersion();
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

/** 注册码输入框与「继续」按钮的统一高度。 */
const CODE_FIELD_HEIGHT = 76;

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
  securityCopy: {
    color: colors.onInverse,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 20,
    opacity: 0.82,
  },
  formPanel: {
    flex: 1,
    minWidth: 0,
  },
  formPanelContent: {
    flexGrow: 1,
    gap: 12,
    justifyContent: "center",
    paddingHorizontal: 44,
    paddingVertical: 12,
  },
  formTitle: { color: colors.ink, fontSize: 24, fontWeight: "900" },
  formHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    marginTop: 4,
  },
  divider: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  dividerLine: {
    backgroundColor: colors.border,
    flex: 1,
    height: 1,
  },
  dividerLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  fieldLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    marginBottom: 5,
  },
  codeRow: { flexDirection: "row", gap: 10 },
  codeInputContainer: { flex: 1, minWidth: 0 },
  codeInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 2.5,
    color: colors.ink,
    fontSize: 19,
    fontWeight: "600",
    height: CODE_FIELD_HEIGHT,
    paddingLeft: 62,
    paddingRight: 18,
  },
  codeInputFilled: { fontSize: 26, fontWeight: "900", letterSpacing: 3 },
  codeInputDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
    color: colors.disabledInk,
  },
  codeInputIcon: {
    bottom: 0,
    justifyContent: "center",
    left: 18,
    position: "absolute",
    top: 0,
  },
  continueButton: {
    minHeight: CODE_FIELD_HEIGHT,
    width: 150,
  },
  codeHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
    marginTop: 6,
  },
  infoNote: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  infoNoteTitle: {
    color: colors.blue,
    fontSize: 15,
    fontWeight: "900",
  },
  infoNoteHint: {
    color: colors.blue,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    marginTop: 3,
  },
  storePicker: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 64,
    paddingHorizontal: 14,
  },
  storePickerDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  storePickerCopy: { flex: 1, minWidth: 0, paddingRight: 12 },
  storePickerValue: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "700",
  },
  storePickerPlaceholder: {
    color: colors.placeholder,
    fontWeight: "600",
  },
  storeStatus: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  storeStatusText: {
    color: colors.yellowInk,
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  stateCard: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 14,
    paddingHorizontal: 18,
    paddingVertical: 16,
  },
  stateCardDanger: {
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
  },
  stateCopy: { flex: 1, minWidth: 0 },
  stateTitle: { color: colors.ink, fontSize: 19, fontWeight: "900" },
  stateDetail: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
    marginTop: 3,
  },
  activationPreview: {
    alignItems: "center",
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
    borderRadius: 12,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 14,
    paddingHorizontal: 18,
    paddingVertical: 14,
  },
  activationPreviewLabel: {
    color: colors.green,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  activationPreviewStore: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
    marginTop: 4,
  },
  activationPreviewExpiry: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    marginTop: 3,
  },
  errorBanner: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  errorBannerText: {
    color: colors.red,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  standaloneNetworkCheck: {
    alignSelf: "center",
    minWidth: 240,
  },
  pressed: { opacity: 0.72 },
  modalBackdrop: {
    ...StyleSheet.absoluteFillObject,
    zIndex: 20,
    alignItems: "center",
    justifyContent: "center",
    padding: 32,
    backgroundColor: colors.overlay,
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  modalCard: {
    backgroundColor: colors.canvas,
    borderRadius: 16,
    maxHeight: "82%",
    maxWidth: 640,
    padding: 22,
    width: "100%",
  },
  modalHeader: {
    alignItems: "center",
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 16,
    justifyContent: "space-between",
    paddingBottom: 14,
  },
  modalTitle: {
    color: colors.ink,
    flex: 1,
    fontSize: 22,
    fontWeight: "900",
  },
  storeList: { gap: 6, paddingTop: 12 },
  storeOption: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: 78,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  storeOptionSelected: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.green,
  },
  storeOptionCopy: { flex: 1, minWidth: 0 },
  storeOptionName: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  storeOptionCode: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
    marginTop: 3,
  },
}));
