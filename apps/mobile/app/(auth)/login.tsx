import { useRouter } from "expo-router";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { HB_COLORS } from "@/shared/theme/tokens";
import {
  View,
  StyleSheet,
  Animated,
  StatusBar,
  Dimensions,
  KeyboardAvoidingView,
  ScrollView,
  Platform,
  TouchableOpacity,
} from "react-native";
import { isAxiosError } from "axios";
import {
  TextInput,
  Button,
  Text,
  Checkbox,
  Snackbar,
} from "react-native-paper";
import { useState, useEffect, useRef } from "react";
import {
  getIosReviewBuildContext,
  useAuthStore,
  waitForLocalSessionClear,
} from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import { getDeviceProfileApi } from "@/modules/device/api";
import { DeviceStorage } from "@/modules/device/storage";
import type { DeviceProfile } from "@/modules/device/types";
import { DeviceActivationDialog } from "@/modules/device-activation/DeviceActivationDialog";
import type { MobileDeviceActivationMode } from "@/modules/device-activation/types";
import {
  getFriendlyDeviceLoginErrorDescriptor,
  getFriendlyLoginErrorDescriptor,
  type LoginErrorDescriptor,
} from "@/modules/auth/login-errors";
import { prepareDeviceLoginSession } from "@/modules/auth/device-login-session";
import {
  collectLoginDeviceLocation,
  collectOptionalLoginDeviceLocation,
  isRequiredLocationError,
} from "@/modules/attendance/required-location";
import { resolveDefaultTabRoute } from "@/modules/navigation/default-route";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { i18n, setAppLanguage } from "@/shared/i18n/i18n";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  API_HOST_PRESETS,
  getCurrentApiHost,
  getStoredApiHost,
  normalizeApiHost,
  setStoredApiHost,
} from "@/shared/api/config";
import {
  isIosReviewBuildEnabled,
  isIosReviewUsername,
} from "@/modules/ios-review/config";

const REMEMBERED_USERNAME_KEY = "remembered_username";
// 复用登录页现有颜色入口，使三种登录方式保持相同的业务蓝色。
const BRAND_RED = HB_COLORS.brand;
const BRAND_BG = HB_COLORS.background;
type LoginMode = "device" | "deviceAccount" | "user";

const { height: SCREEN_HEIGHT } = Dimensions.get("window");
const IS_SMALL_SCREEN = SCREEN_HEIGHT < 640;

function getApiOriginLabel() {
  return getCurrentApiHost();
}

function translateLoginError(descriptor: LoginErrorDescriptor): string {
  return i18n.t(`login:${descriptor.key}`, descriptor.values);
}

function getFriendlyDeviceLookupErrorMessage(error: unknown): string {
  if (isAxiosError(error)) {
    if (error.response?.status === 404) {
      return "";
    }
    if (error.code === "ECONNABORTED") {
      return i18n.t("login:device.lookupTimeout", { origin: getApiOriginLabel() });
    }
    if (error.message === "Network Error") {
      return i18n.t("login:device.lookupNetwork", { origin: getApiOriginLabel() });
    }
    if (error.response?.status) {
      return i18n.t("login:device.lookupHttp", { status: error.response.status });
    }
  }

  return i18n.t("login:device.lookupFailed");
}

function resolveDeviceStatusText(status: number | undefined, fallback?: string | null) {
  switch (status) {
    case -1:
      return i18n.t("login:device.status.pending");
    case 0:
      return i18n.t("login:device.status.disabled");
    case 1:
      return i18n.t("login:device.status.enabled");
    case 2:
      return i18n.t("login:device.status.locked");
    case 3:
      return i18n.t("login:device.status.unregistered");
    default:
      return fallback || i18n.t("login:device.status.unknown");
  }
}

function getVisibleRouteNames() {
  return useAppNavigationStore.getState().items.map((item) => item.routeName);
}

export default function Login() {
  const router = useRouter();
  const { t } = useAppTranslation(["login", "common"]);
  const loginFn = useAuthStore((s) => s.login);
  const loginDeviceAccount = useAuthStore((s) => s.loginDeviceAccount);
  const clearAccountSessionForDeviceLogin = useAuthStore(
    (s) => s.clearAccountSessionForDeviceLogin
  );
  const authSessionKind = useAuthStore((s) => s.sessionKind);
  const iosReviewOfflineGuardActive = useAuthStore(
    (s) => s.iosReviewOfflineGuardActive
  );
  const beginStandardAuth = useAuthStore((s) => s.beginStandardAuth);
  const rearmIosReviewPreAuth = useAuthStore(
    (s) => s.rearmIosReviewPreAuth
  );
  const setSessionKind = useAuthStore((s) => s.setSessionKind);
  const syncDeviceFromProfile = useDeviceStore((s) => s.syncFromProfile);
  const validateDevice = useDeviceStore((s) => s.validate);
  const legacyDeviceSession = useDeviceStore((s) => s.session);
  const accountBinding = useDeviceStore((s) => s.accountBinding);
  const refreshAccountBinding = useDeviceStore((s) => s.refreshAccountBinding);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [deviceLoginLoading, setDeviceLoginLoading] = useState(false);
  const [deviceLookupLoading, setDeviceLookupLoading] = useState(false);
  const [loginMode, setLoginMode] = useState<LoginMode>("user");
  const [registeredDevice, setRegisteredDevice] = useState<DeviceProfile | null>(null);
  const [detectedHardwareId, setDetectedHardwareId] = useState("");
  const [error, setError] = useState("");
  const [rememberUsername, setRememberUsername] = useState(false);
  const [snackbarVisible, setSnackbarVisible] = useState(false);
  const [rememberReady, setRememberReady] = useState(false);
  const [apiHost, setApiHost] = useState(getCurrentApiHost());
  const [apiHostDraft, setApiHostDraft] = useState(getCurrentApiHost());
  const [apiHostModalVisible, setApiHostModalVisible] = useState(false);
  const [apiHostError, setApiHostError] = useState("");
  const [activationVisible, setActivationVisible] = useState(false);
  const [activationMode, setActivationMode] = useState<MobileDeviceActivationMode>("redeem");
  const reviewBuildEnabled = isIosReviewBuildEnabled(getIosReviewBuildContext());
  const shouldRunLoginSideEffects = Boolean(
    rememberReady &&
      !iosReviewOfflineGuardActive &&
      authSessionKind !== "iosReview"
  );

  // 动画 refs
  const logoScale = useRef(new Animated.Value(0)).current;
  const logoOpacity = useRef(new Animated.Value(0)).current;
  const titleOpacity = useRef(new Animated.Value(0)).current;
  const titleTranslateY = useRef(new Animated.Value(20)).current;
  const usernameSlide = useRef(new Animated.Value(0)).current;
  const passwordSlide = useRef(new Animated.Value(0)).current;
  const buttonOpacity = useRef(new Animated.Value(0)).current;
  const buttonPulse = useRef(new Animated.Value(1)).current;
  const deviceLookupGeneration = useRef(0);

  useEffect(() => {
    Animated.sequence([
      Animated.parallel([
        Animated.spring(logoScale, { toValue: 1, tension: 70, friction: 7, useNativeDriver: true }),
        Animated.timing(logoOpacity, { toValue: 1, duration: 600, useNativeDriver: true }),
      ]),
      Animated.parallel([
        Animated.timing(titleOpacity, { toValue: 1, duration: 450, useNativeDriver: true }),
        Animated.timing(titleTranslateY, { toValue: 0, duration: 450, useNativeDriver: true }),
      ]),
      Animated.timing(usernameSlide, { toValue: 1, duration: 400, useNativeDriver: true }),
      Animated.timing(passwordSlide, { toValue: 1, duration: 400, useNativeDriver: true }),
      Animated.timing(buttonOpacity, { toValue: 1, duration: 400, useNativeDriver: true }),
    ]).start();

    const timer = setTimeout(() => {
      Animated.loop(
        Animated.sequence([
          Animated.timing(buttonPulse, { toValue: 1.03, duration: 1200, useNativeDriver: true }),
          Animated.timing(buttonPulse, { toValue: 1, duration: 1200, useNativeDriver: true }),
        ]),
      ).start();
    }, 2200);
    return () => clearTimeout(timer);
  }, []);

  useEffect(() => {
    void loadRememberedUsername();
    void loadApiHost();
  }, []);

  useEffect(() => {
    // 审核用户名和空用户名都不能触发设备探测，保证错误密码路径同样零网络。
    if (!shouldRunLoginSideEffects) {
      return;
    }
    void identifyRegisteredDevice();
  }, [shouldRunLoginSideEffects]);

  useEffect(() => {
    if (accountBinding) {
      // 绑定晚于旧设备探测返回时，先使在途结果失效，禁止界面降级回固定设备登录。
      deviceLookupGeneration.current += 1;
      setDetectedHardwareId(accountBinding.hardwareId);
      setRegisteredDevice(null);
      setLoginMode("deviceAccount");
      setDeviceLookupLoading(false);
    }
  }, [accountBinding]);

  useEffect(() => {
    if (!rememberReady) return;
    if (!rememberUsername) {
      void AppAsyncStorage.removeItem(REMEMBERED_USERNAME_KEY);
      return;
    }
    if (username) {
      void AppAsyncStorage.setString(REMEMBERED_USERNAME_KEY, username);
    }
  }, [rememberReady, rememberUsername, username]);

  async function loadRememberedUsername() {
    const saved = await AppAsyncStorage.getString(REMEMBERED_USERNAME_KEY);
    if (saved) {
      setUsername(saved);
      setRememberUsername(true);
    }
    setRememberReady(true);
  }

  async function loadApiHost() {
    const host = await getStoredApiHost();
    setApiHost(host);
    setApiHostDraft(host);
  }

  async function identifyRegisteredDevice() {
    const requestGeneration = ++deviceLookupGeneration.current;
    if (accountBinding) {
      setDetectedHardwareId(accountBinding.hardwareId);
      setRegisteredDevice(null);
      setLoginMode("deviceAccount");
      setDeviceLookupLoading(false);
      return;
    }
    const session = await DeviceStorage.getSession();
    if (requestGeneration !== deviceLookupGeneration.current) {
      return;
    }
    if (!session?.systemDeviceNumber || !session.hardwareId) {
      setDetectedHardwareId("");
      setRegisteredDevice(null);
      setLoginMode(accountBinding ? "deviceAccount" : "user");
      setDeviceLookupLoading(false);
      return;
    }

    setDeviceLookupLoading(true);
    try {
      setDetectedHardwareId(session.hardwareId);
      const profile = await getDeviceProfileApi(session.hardwareId);
      if (requestGeneration !== deviceLookupGeneration.current) {
        return;
      }
      setRegisteredDevice(profile);
      setLoginMode(accountBinding ? "deviceAccount" : "device");
    } catch (err) {
      if (requestGeneration !== deviceLookupGeneration.current) {
        return;
      }
      setRegisteredDevice(null);
      setLoginMode(accountBinding ? "deviceAccount" : "user");

      if (isAxiosError(err) && err.response?.status === 404) {
        return;
      }

      const message = getFriendlyDeviceLookupErrorMessage(err);
      if (message) {
        setError(message);
        setSnackbarVisible(true);
      }
    } finally {
      if (requestGeneration === deviceLookupGeneration.current) {
        setDeviceLookupLoading(false);
      }
    }
  }

  async function handleSaveApiHost() {
    const normalizedHost = normalizeApiHost(apiHostDraft);
    if (!normalizedHost) {
      // 原生弹窗内显示校验提示，避免被弹窗遮住页面 Snackbar。
      setApiHostError(t("apiHost.empty"));
      return;
    }

    const host = await setStoredApiHost(normalizedHost);
    setApiHost(host);
    setApiHostDraft(host);
    setApiHostModalVisible(false);
    setError(t("apiHost.saved", { host }));
    setSnackbarVisible(true);
    await identifyRegisteredDevice();
  }

  async function handleLogin() {
    const isReviewLoginAttempt =
      reviewBuildEnabled && isIosReviewUsername(username);
    if (isReviewLoginAttempt) {
      // 审核用户名一经提交便同步恢复离线守卫，并让所有设备查询结果失效。
      rearmIosReviewPreAuth();
      deviceLookupGeneration.current += 1;
      setDeviceLookupLoading(false);
    }
    setError("");
    setLoading(true);
    try {
      await waitForLocalSessionClear();
      if (isReviewLoginAttempt) {
        rearmIosReviewPreAuth();
        // 关键位置：审核登录先走本地认证，密码错误也不会采集定位或回落真实 API。
        await loginFn({ username, password });
      } else {
        // 只有用户实际提交普通账号时才解除 pre-auth 守卫；逐字输入不会触网。
        beginStandardAuth();
        const auditPayload = await collectOptionalLoginDeviceLocation(
          registeredDevice
        );
        await loginFn({ username, password, ...auditPayload });
      }
      router.replace(
        resolveDefaultTabRoute({
          isDeviceMode: false,
          isWarehouseStaffOnly: useAuthStore.getState().access.isWarehouseStaffOnly,
          routeNames: getVisibleRouteNames(),
        }) as Parameters<typeof router.replace>[0],
        { withAnchor: true }
      );
    } catch (err) {
      if (!isReviewLoginAttempt) {
        rearmIosReviewPreAuth();
      }
      setError(
        isRequiredLocationError(err)
          ? t("errors.locationRequired")
          : translateLoginError(getFriendlyLoginErrorDescriptor(err)),
      );
      setSnackbarVisible(true);
    } finally {
      setLoading(false);
    }
  }

  async function handleDeviceLogin() {
    if (!registeredDevice) {
      return;
    }

    setError("");
    setDeviceLoginLoading(true);
    try {
      await waitForLocalSessionClear();
      beginStandardAuth("device");
      const auditPayload = await collectLoginDeviceLocation(registeredDevice);
      const isReady = await prepareDeviceLoginSession(registeredDevice, {
        clearAccountSession: clearAccountSessionForDeviceLogin,
        syncDeviceFromProfile,
        validateDevice: () => validateDevice(auditPayload),
      });
      if (isReady) {
        setSessionKind("device");
        router.replace(
          resolveDefaultTabRoute({
            isDeviceMode: true,
            routeNames: getVisibleRouteNames(),
          }) as Parameters<typeof router.replace>[0],
          { withAnchor: true }
        );
        return;
      }

      rearmIosReviewPreAuth();
      setError(t("device.notReadyMessage"));
      setSnackbarVisible(true);
    } catch (err) {
      rearmIosReviewPreAuth();
      setError(
        isRequiredLocationError(err)
          ? t("errors.locationRequired")
          : translateLoginError(getFriendlyDeviceLoginErrorDescriptor(err)),
      );
      setSnackbarVisible(true);
    } finally {
      setDeviceLoginLoading(false);
    }
  }

  async function handleDeviceAccountLogin() {
    setError("");
    setDeviceLoginLoading(true);
    try {
      await waitForLocalSessionClear();
      beginStandardAuth("deviceAccount");
      await loginDeviceAccount();
      router.replace(
        resolveDefaultTabRoute({
          isDeviceMode: false,
          isWarehouseStaffOnly: useAuthStore.getState().access.isWarehouseStaffOnly,
          routeNames: getVisibleRouteNames(),
        }) as Parameters<typeof router.replace>[0],
        { withAnchor: true },
      );
    } catch {
      rearmIosReviewPreAuth();
      setError(t("activation.sessionFailed"));
      setSnackbarVisible(true);
    } finally {
      setDeviceLoginLoading(false);
    }
  }

  const openActivation = (mode: MobileDeviceActivationMode) => {
    // 开通入口是审核构建允许真实网络的显式用户动作；输入和页面浏览仍保持零网络。
    beginStandardAuth("deviceAccount");
    setActivationMode(mode);
    setActivationVisible(true);
  };

  const handleActivationCompleted = async () => {
    try {
      await refreshAccountBinding();
      await loginDeviceAccount();
      router.replace(
        resolveDefaultTabRoute({
          isDeviceMode: false,
          isWarehouseStaffOnly: useAuthStore.getState().access.isWarehouseStaffOnly,
          routeNames: getVisibleRouteNames(),
        }) as Parameters<typeof router.replace>[0],
        { withAnchor: true },
      );
    } catch {
      rearmIosReviewPreAuth();
      setError(t("activation.sessionFailed"));
      setSnackbarVisible(true);
    }
  };

  const deviceStatusText = resolveDeviceStatusText(
    registeredDevice?.status,
    registeredDevice?.statusDescription
  );
  const canUseDeviceLogin = Boolean(
    registeredDevice?.status === 1 &&
      registeredDevice.storeCode &&
      legacyDeviceSession?.authCode
  );
  const openApiHostSettings = () => {
    setApiHostDraft(apiHost);
    setApiHostError("");
    setApiHostModalVisible(true);
  };
  const handleUsernameChange = (value: string) => {
    setUsername(value);
    if (reviewBuildEnabled && isIosReviewUsername(value)) {
      // 输入命中审核账号时同步回到 fail-closed，拒绝稍后返回的设备查询覆盖页面状态。
      rearmIosReviewPreAuth();
      deviceLookupGeneration.current += 1;
      setDeviceLookupLoading(false);
      setRegisteredDevice(null);
      setLoginMode("user");
    }
  };
  const handleSelectUserMode = () => {
    // 切回账号登录立即关闭普通网络通道，并废弃所有在途设备查询。
    rearmIosReviewPreAuth();
    deviceLookupGeneration.current += 1;
    setDeviceLookupLoading(false);
    setLoginMode("user");
  };
  const handleSelectDeviceMode = () => {
    const selectionGeneration = ++deviceLookupGeneration.current;
    const selectedSessionKind = accountBinding ? "deviceAccount" : "device";
    setLoginMode(selectedSessionKind);
    // 设备模式按钮是审核构建解除网络守卫的显式动作，但仍等待旧会话清理结束。
    void waitForLocalSessionClear().then(() => {
      if (selectionGeneration !== deviceLookupGeneration.current) {
        return;
      }
      beginStandardAuth(selectedSessionKind);
      if (selectedSessionKind === "deviceAccount") {
        return;
      }
      void identifyRegisteredDevice();
    });
  };
  return (
    <KeyboardAvoidingView
      style={styles.root}
      behavior={Platform.OS === "ios" ? "padding" : "height"}
      keyboardVerticalOffset={0}
    >
      <StatusBar barStyle="light-content" backgroundColor={BRAND_RED} />

      {/* 软键盘弹出时让品牌区和表单一起滚动，避免固定品牌区把输入框挤到键盘后面。 */}
      <ScrollView
        contentContainerStyle={styles.pageScrollContent}
        keyboardShouldPersistTaps="handled"
        showsVerticalScrollIndicator={false}
      >
        {/* ── 品牌区 ── */}
        <View style={styles.brandSection}>
          <View style={styles.topActions}>
            <TouchableOpacity
              style={styles.langSwitch}
              onPress={() => setAppLanguage(i18n.language === "zh" ? "en" : "zh")}
              activeOpacity={0.7}
            >
              <Text style={styles.langSwitchText}>
                {t(i18n.language === "zh" ? "languageSwitch.toEnglish" : "languageSwitch.toChinese")}
              </Text>
            </TouchableOpacity>
          </View>
          <Animated.View
            style={{
              opacity: logoOpacity,
              transform: [{ scale: logoScale }],
            }}
          >
            <View style={styles.logoCircle}>
              <Text style={styles.logoText}>HB</Text>
            </View>
          </Animated.View>

          <Animated.View
            style={{
              alignItems: "center",
              opacity: titleOpacity,
              transform: [{ translateY: titleTranslateY }],
            }}
          >
            <Text style={styles.brandTitle}>{t("common:appName")}</Text>
            <Text style={styles.brandSubtitle}>{t("subtitle")}</Text>
          </Animated.View>
        </View>

        {/* ── 表单区 ── */}
        <View style={styles.formSection}>
          <View style={styles.formCard}>
            {registeredDevice || accountBinding ? (
              <View style={styles.loginModeRow}>
                <Button
                  compact
                  mode={loginMode !== "user" ? "contained" : "outlined"}
                  buttonColor={loginMode !== "user" ? BRAND_RED : undefined}
                  textColor={loginMode !== "user" ? "#FFFFFF" : BRAND_RED}
                  onPress={handleSelectDeviceMode}
                  style={styles.loginModeButton}
                >
                  {accountBinding ? t("activation.deviceAccountTitle") : t("device.mode")}
                </Button>
                <Button
                  compact
                  mode={loginMode === "user" ? "contained" : "outlined"}
                  buttonColor={loginMode === "user" ? BRAND_RED : undefined}
                  textColor={loginMode === "user" ? "#FFFFFF" : BRAND_RED}
                  onPress={handleSelectUserMode}
                  style={styles.loginModeButton}
                >
                  {t("userMode")}
                </Button>
              </View>
            ) : null}

            {deviceLookupLoading ? (
              <Text style={styles.deviceLookupText}>{t("device.lookupInProgress")}</Text>
            ) : null}

            {accountBinding && loginMode === "deviceAccount" ? (
              <View style={styles.deviceCard}>
                <Text style={styles.deviceTitle}>{t("activation.deviceAccountTitle")}</Text>
                <Text style={styles.deviceDescription}>
                  {t("activation.deviceAccountDescription")}
                </Text>
                <View style={styles.deviceInfoBox}>
                  <Text style={styles.deviceInfoText}>
                    {t("activation.bindingAccount", {
                      value:
                        accountBinding.binding.targetFullName ||
                        accountBinding.binding.targetUsername,
                    })}
                  </Text>
                  <Text style={styles.deviceInfoText}>
                    {t("device.store", { value: accountBinding.binding.storeName })}
                  </Text>
                  <Text style={styles.deviceInfoText}>
                    {t("device.deviceNumber", { value: accountBinding.binding.deviceCode })}
                  </Text>
                </View>
                <Button
                  mode="contained"
                  onPress={handleDeviceAccountLogin}
                  loading={deviceLoginLoading}
                  disabled={deviceLoginLoading}
                  buttonColor={BRAND_RED}
                  textColor="#FFFFFF"
                  style={styles.button}
                  contentStyle={styles.buttonContent}
                  labelStyle={styles.buttonLabel}
                >
                  {t("activation.deviceAccountLogin")}
                </Button>
                <Button mode="text" textColor={BRAND_RED} onPress={() => openActivation("rebind")}>
                  {t("activation.rebind")}
                </Button>
                <Button mode="text" textColor={BRAND_RED} onPress={handleSelectUserMode}>
                  {t("device.switchToUser")}
                </Button>
              </View>
            ) : registeredDevice && loginMode === "device" ? (
              <View style={styles.deviceCard}>
                <Text style={styles.deviceTitle}>{t("device.title")}</Text>
                <Text style={styles.deviceDescription}>{t("device.description")}</Text>
                <View style={styles.deviceInfoBox}>
                  <Text style={styles.deviceInfoText}>
                    {t("device.deviceNumber", {
                      value: registeredDevice.systemDeviceNumber || t("common:na"),
                    })}
                  </Text>
                  <Text style={styles.deviceInfoText}>
                    {t("device.store", {
                      value: registeredDevice.storeCode || t("common:na"),
                    })}
                  </Text>
                  <Text style={styles.deviceInfoText}>
                    {t("device.statusLabel", { status: deviceStatusText })}
                  </Text>
                  <Text style={styles.deviceHardwareText} numberOfLines={1}>
                    {t("device.hardwareId", {
                      value: registeredDevice.hardwareId || detectedHardwareId || t("common:na"),
                    })}
                  </Text>
                </View>
                {!canUseDeviceLogin ? (
                  <Text style={styles.deviceUnavailableText}>{t("device.unavailable")}</Text>
                ) : null}
                <Button
                  mode="contained"
                  onPress={handleDeviceLogin}
                  loading={deviceLoginLoading}
                  disabled={deviceLoginLoading || !canUseDeviceLogin}
                  buttonColor={BRAND_RED}
                  textColor="#FFFFFF"
                  style={styles.button}
                  contentStyle={styles.buttonContent}
                  labelStyle={styles.buttonLabel}
                >
                  {t("device.login")}
                </Button>
                <Button mode="text" textColor={BRAND_RED} onPress={handleSelectUserMode}>
                  {t("device.switchToUser")}
                </Button>
                <Button mode="text" icon="qrcode-scan" textColor={BRAND_RED} onPress={() => openActivation("redeem")}>
                  {t("activation.upgrade")}
                </Button>
                <Button mode="text" icon="cog-outline" textColor={BRAND_RED} onPress={openApiHostSettings}>
                  {t("apiHost.settingsButton")}
                </Button>
              </View>
            ) : (
              <>
                <Animated.View
                  style={{
                    opacity: usernameSlide,
                    transform: [{ translateX: usernameSlide.interpolate({ inputRange: [0, 1], outputRange: [-60, 0] }) }],
                  }}
                >
                  <TextInput
                    label={t("username")}
                    value={username}
                    onChangeText={handleUsernameChange}
                    mode="outlined"
                    autoCapitalize="none"
                    style={styles.input}
                    outlineColor="#E0E0E0"
                    activeOutlineColor={BRAND_RED}
                  />
                </Animated.View>

                <Animated.View
                  style={{
                    opacity: passwordSlide,
                    transform: [{ translateX: passwordSlide.interpolate({ inputRange: [0, 1], outputRange: [60, 0] }) }],
                  }}
                >
                  <TextInput
                    label={t("password")}
                    value={password}
                    onChangeText={setPassword}
                    mode="outlined"
                    secureTextEntry={!showPassword}
                    style={styles.input}
                    outlineColor="#E0E0E0"
                    activeOutlineColor={BRAND_RED}
                    right={
                      <TextInput.Icon
                        icon={showPassword ? "eye-off" : "eye"}
                        onPress={() => setShowPassword((v) => !v)}
                        forceTextInputFocus={false}
                      />
                    }
                  />
                </Animated.View>

                <Animated.View style={{ opacity: buttonOpacity, transform: [{ scale: buttonPulse }] }}>
                  <Checkbox.Item
                    label={t("rememberUsername")}
                    status={rememberUsername ? "checked" : "unchecked"}
                    onPress={() => setRememberUsername(!rememberUsername)}
                    color={BRAND_RED}
                    labelStyle={styles.checkboxLabel}
                  />
                  <Button
                    mode="contained"
                    onPress={handleLogin}
                    loading={loading}
                    disabled={loading || !username || !password}
                    buttonColor={BRAND_RED}
                    textColor="#FFFFFF"
                    style={styles.button}
                    contentStyle={styles.buttonContent}
                    labelStyle={styles.buttonLabel}
                  >
                    {t("common:actions.login")}
                  </Button>
                  {/* 店长新建账号后员工用邮件验证码设置密码；忘记密码也走同一页面。 */}
                  <Button
                    mode="text"
                    textColor={BRAND_RED}
                    onPress={() => router.push({
                      pathname: "/(auth)/reset-password",
                      params: username.includes("@") ? { email: username.trim() } : {},
                    } as unknown as Parameters<typeof router.push>[0])}
                  >
                    {t("resetPassword.entry")}
                  </Button>
                  {registeredDevice ? (
                    <Button mode="text" textColor={BRAND_RED} onPress={handleSelectDeviceMode}>
                      {t("device.switchToDevice")}
                    </Button>
                  ) : reviewBuildEnabled && iosReviewOfflineGuardActive ? (
                    <Button mode="text" textColor={BRAND_RED} onPress={handleSelectDeviceMode}>
                      {t("device.mode")}
                    </Button>
                  ) : null}
                  <Button
                    mode="text"
                    icon="qrcode-scan"
                    textColor={BRAND_RED}
                    onPress={() => openActivation(accountBinding ? "rebind" : "redeem")}
                  >
                    {accountBinding ? t("activation.rebind") : t("activation.open")}
                  </Button>
                  <Button mode="text" icon="cog-outline" textColor={BRAND_RED} onPress={openApiHostSettings}>
                    {t("apiHost.settingsButton")}
                  </Button>
                </Animated.View>
              </>
            )}
          </View>
          <View style={styles.privacyFooter}>
            <Text style={styles.privacyNotice}>
              {t(loginMode === "device" ? "privacy.deviceNotice" : "privacy.accountNotice")}
            </Text>
            <Button
              compact
              mode="text"
              icon="shield-check-outline"
              textColor={BRAND_RED}
              onPress={() => router.push("/privacy")}
            >
              {t("privacy.openPolicy")}
            </Button>
          </View>
        </View>
      </ScrollView>

      <BusinessSheet
        visible={apiHostModalVisible}
        title={t("apiHost.title")}
        subtitle={t("apiHost.description")}
        onDismiss={() => setApiHostModalVisible(false)}
        footer={
          <View style={styles.apiHostModalActions}>
            <Button mode="text" textColor="#555" onPress={() => setApiHostModalVisible(false)}>
              {t("common:actions.cancel")}
            </Button>
            <Button mode="contained" buttonColor={BRAND_RED} onPress={handleSaveApiHost}>
              {t("common:actions.save")}
            </Button>
          </View>
        }
      >
          <View style={styles.apiHostCurrentBox}>
            <Text style={styles.apiHostLabel}>{t("apiHost.current")}</Text>
            <Text style={styles.apiHostValue} numberOfLines={1}>{apiHost}</Text>
          </View>
          <Text style={styles.apiHostLabel}>{t("apiHost.presets.label")}</Text>
          <View style={styles.apiHostPresetList}>
            {API_HOST_PRESETS.map((preset) => {
              const selected = normalizeApiHost(apiHostDraft) === preset.host;
              return (
                <Button
                  key={preset.key}
                  compact
                  mode={selected ? "contained" : "outlined"}
                  buttonColor={selected ? BRAND_RED : undefined}
                  textColor={selected ? "#FFFFFF" : BRAND_RED}
                  style={styles.apiHostPresetButton}
                  onPress={() => setApiHostDraft(preset.host)}
                >
                  {t(preset.labelKey)}
                </Button>
              );
            })}
          </View>
          <TextInput
            label={t("apiHost.inputLabel")}
            value={apiHostDraft}
            onChangeText={(value) => {
              setApiHostDraft(value);
              setApiHostError("");
            }}
            mode="outlined"
            autoCapitalize="none"
            autoCorrect={false}
            style={styles.input}
            outlineColor="#E0E0E0"
            activeOutlineColor={BRAND_RED}
          />
          {apiHostError ? <Text accessibilityRole="alert" style={{ color: HB_COLORS.danger }}>{apiHostError}</Text> : null}
      </BusinessSheet>

      <DeviceActivationDialog
        visible={activationVisible}
        mode={activationMode}
        onDismiss={(reason) => {
          setActivationVisible(false);
          if (reason === "cancelled") {
            rearmIosReviewPreAuth();
          }
        }}
        onCompleted={handleActivationCompleted}
      />

      <Snackbar
        visible={snackbarVisible}
        onDismiss={() => setSnackbarVisible(false)}
        duration={3000}
        wrapperStyle={styles.snackbar}
      >
        {error}
      </Snackbar>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: BRAND_BG },
  topActions: {
    alignItems: "center",
    alignSelf: "stretch",
    flexDirection: "row",
    justifyContent: "flex-end",
    marginBottom: IS_SMALL_SCREEN ? 14 : 20,
    paddingHorizontal: 14,
  },
  // 语言切换
  langSwitch: {
    backgroundColor: "rgba(255,255,255,0.16)",
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderWidth: 1,
    borderColor: "rgba(255,255,255,0.4)",
  },
  langSwitchText: {
    color: "#FFFFFF",
    fontSize: 13,
    fontWeight: "700",
    letterSpacing: 1,
  },
  // 品牌区
  brandSection: {
    backgroundColor: BRAND_RED,
    paddingTop: IS_SMALL_SCREEN ? 40 : 54,
    paddingBottom: 28,
    borderBottomLeftRadius: 18,
    borderBottomRightRadius: 18,
    alignItems: "center",
  },
  logoCircle: {
    width: 64,
    height: 64,
    borderRadius: 16,
    backgroundColor: "#FFFFFF",
    justifyContent: "center",
    alignItems: "center",
    marginBottom: IS_SMALL_SCREEN ? 10 : 16,
  },
  logoText: {
    fontSize: 28,
    fontWeight: "800",
    color: BRAND_RED,
    letterSpacing: 3,
  },
  brandTitle: {
    fontSize: 24,
    lineHeight: 32,
    fontWeight: "700",
    color: "#FFFFFF",
    marginBottom: 6,
  },
  brandSubtitle: {
    fontSize: IS_SMALL_SCREEN ? 12 : 14,
    color: "rgba(255,255,255,0.85)",
    letterSpacing: 1,
  },
  pageScrollContent: { flexGrow: 1 },
  // 表单区
  formSection: { flexGrow: 1, justifyContent: "center", alignSelf: "center", width: "100%", maxWidth: 560, paddingBottom: 20, paddingHorizontal: 16, paddingTop: 20 },
  formCard: {
    ...BUSINESS_UI.section,
    padding: 16,
  },
  privacyFooter: {
    alignItems: "center",
    gap: 2,
    marginTop: 12,
    paddingHorizontal: 4,
  },
  privacyNotice: {
    color: "#616873",
    fontSize: 12,
    lineHeight: 17,
    textAlign: "center",
  },
  loginModeRow: {
    flexDirection: "row",
    gap: 10,
    marginBottom: 18,
  },
  loginModeButton: {
    flex: 1,
    borderColor: BRAND_RED,
  },
  input: { marginBottom: 14, backgroundColor: HB_COLORS.white },
  deviceCard: { gap: 12 },
  deviceTitle: {
    color: "#222",
    fontSize: 18,
    fontWeight: "800",
  },
  deviceDescription: {
    color: "#666",
    fontSize: 13,
    lineHeight: 19,
  },
  deviceInfoBox: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 12,
    borderWidth: 1,
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 12,
  },
  deviceInfoText: {
    color: "#333",
    fontSize: 14,
    fontWeight: "600",
  },
  deviceHardwareText: {
    color: "#777",
    fontSize: 12,
  },
  deviceLookupText: {
    color: "#777",
    fontSize: 13,
    marginBottom: 12,
    textAlign: "center",
  },
  deviceUnavailableText: {
    color: "#A8071A",
    fontSize: 13,
    lineHeight: 19,
  },
  apiHostCurrentBox: {
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 12,
    borderWidth: 1,
    marginBottom: 14,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  apiHostLabel: { color: "#777", fontSize: 12, marginBottom: 2 },
  apiHostValue: { color: "#333", fontSize: 14, fontWeight: "700" },
  apiHostPresetList: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    marginBottom: 14,
    marginTop: 8,
  },
  apiHostPresetButton: {
    borderColor: BRAND_RED,
    borderRadius: 12,
  },
  apiHostModalActions: {
    flexDirection: "row",
    gap: 8,
    justifyContent: "flex-end",
  },
  checkboxLabel: { fontSize: 14, color: "#555" },
  button: { marginTop: 6, borderRadius: 8 },
  buttonContent: { height: 50 },
  buttonLabel: { fontSize: 16, fontWeight: "700" },
  snackbar: { bottom: 40 },
});
