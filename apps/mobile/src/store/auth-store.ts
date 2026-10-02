import { create } from "zustand";
import Constants from "expo-constants";
import { Platform } from "react-native";
import type { CurrentUser, AccessControl, LoginRequest } from "@/modules/auth/types";
import { buildAccess } from "@/shared/utils/access";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { SecureStorage } from "@/shared/storage/secure";
import { queryClient } from "@/shared/api/query-client";
import { clearSensitiveQueryCache } from "@/modules/auth/sensitive-query-cache";
import { subscribeUnauthenticatedSession } from "@/modules/auth/auth-session-events";
import {
  LOGIN_LOCAL_STEP_TIMEOUT_MS,
  LOGIN_MENU_TIMEOUT_MS,
  LOGIN_NETWORK_STEP_TIMEOUT_MS,
  LoginStepTimeoutError,
  withLoginStepTimeout,
} from "@/modules/auth/login-diagnostics";
import {
  flushLoginDiagnostics,
  startLoginTrace,
  type LoginTraceRecorder,
} from "@/modules/auth/login-diagnostics-runtime";
import { stopAttendanceLocationTracking } from "@/modules/attendance/location-tracking-control";
import {
  loginApi,
  getCurrentUserApi,
  logoutApi,
} from "@/modules/auth/api";
import { STORE_SELECTION_STORAGE_KEY } from "@/modules/shop/types";
import { useCartStore } from "@/store/cart-store";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { DeviceStorage } from "@/modules/device/storage";
import { closeOfflineCatalogRuntime } from "@/modules/product-maintenance/offline-catalog/offline-catalog-teardown";
import { useDeviceStore } from "@/store/device-store";
import {
  isIosReviewBuildEnabled,
  isIosReviewUsername,
  tryAuthenticateIosReview,
  type IosReviewBuildContext,
} from "@/modules/ios-review/config";
import {
  beginStandardAuthentication,
  clearIosReviewSession,
  configureIosReviewBuildGate,
  isIosReviewSessionActive,
  restoreIosReviewSession,
  setIosReviewSessionActive,
  type AuthSessionKind,
} from "@/modules/ios-review/session";
import { resetReviewData } from "@/modules/ios-review/data-store";
import { createIosReviewUser } from "@/modules/ios-review/identity";
import { IOS_REVIEW_MENU_ITEMS } from "@/modules/ios-review/menu";
import { DeviceAccountStorage } from "@/modules/device-activation/device-account-storage-runtime";
import { exchangeMobileDeviceSessionApi } from "@/modules/device-activation/device-activation-api";
import { establishDeviceAccountSession } from "@/modules/device-activation/device-account-session";
import { loadDeviceAccountBindingForLogin } from "@/modules/device-activation/device-account-login";
import { recoverStoredMobileDeviceActivation } from "@/modules/device-activation/device-activation-runtime";
import {
  clearAuthSessionMarker,
  getAuthSessionMarker,
  setAuthSessionMarker,
} from "@/modules/device-activation/auth-session-marker";
import { deriveEffectiveAuthSessionKind } from "@/modules/device-activation/device-account-request-policy";
import { resolveOfflineSessionRestore } from "@/modules/auth/offline-session-restore";

export function getIosReviewBuildContext(): IosReviewBuildContext {
  return {
    platform: Platform.OS,
    buildProfile: Constants.expoConfig?.extra?.nativeAppBuildProfile,
    enabled: process.env.EXPO_PUBLIC_IOS_REVIEW_MODE_ENABLED,
    passwordSha256: process.env.EXPO_PUBLIC_IOS_REVIEW_PASSWORD_SHA256,
  };
}

const iosReviewBuildEnabled = isIosReviewBuildEnabled(
  getIosReviewBuildContext()
);
// 模块加载即建立同步 pre-auth 守卫，所有 React effect 和 API 请求都只能在其后运行。
configureIosReviewBuildGate(iosReviewBuildEnabled);
let clearLocalSessionInFlight: Promise<void> | null = null;

export async function waitForLocalSessionClear() {
  // 清理期间不允许新认证写入 token/marker，避免旧 finally 删除刚建立的会话。
  while (clearLocalSessionInFlight) {
    await clearLocalSessionInFlight;
  }
}

function setIosReviewMenu() {
  useAppNavigationStore.getState().replaceMenu([...IOS_REVIEW_MENU_ITEMS]);
}

function createInvalidIosReviewCredentialsError() {
  const error = new Error("Invalid username or password") as Error & {
    code?: string;
  };
  error.code = "IOS_REVIEW_INVALID_CREDENTIALS";
  return error;
}

interface AuthState {
  user: CurrentUser | null;
  access: AccessControl;
  sessionKind: AuthSessionKind;
  iosReviewOfflineGuardActive: boolean;
  standardAuthIntent: "account" | "device" | "deviceAccount" | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (payload: LoginRequest) => Promise<void>;
  loginDeviceAccount: () => Promise<void>;
  logout: () => Promise<void>;
  hydrateIosReviewSession: () => Promise<boolean>;
  restoreSession: () => Promise<boolean>;
  refreshCurrentUser: () => Promise<boolean>;
  clearLocalSession: () => Promise<void>;
  clearAccountSessionForDeviceLogin: () => Promise<void>;
  beginStandardAuth: (kind?: "account" | "device" | "deviceAccount") => void;
  rearmIosReviewPreAuth: () => void;
  performLocalSessionClear: () => Promise<void>;
  setSessionKind: (kind: AuthSessionKind) => void;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  access: buildAccess(null),
  sessionKind: "account",
  iosReviewOfflineGuardActive: isIosReviewSessionActive(),
  standardAuthIntent: null,
  isAuthenticated: false,
  isLoading: false,

  async login(payload) {
    const isReviewUsername = isIosReviewUsername(payload.username);
    if (isReviewUsername) {
      // 必须在第一个 await 前同步恢复 fail-closed；设备模式残留不能放行审核凭据。
      get().rearmIosReviewPreAuth();
    }
    // 普通账号登录全程记录各阶段（审核账号走本地离线认证，不记录）：卡住时下次登录可补传定位。
    const trace: LoginTraceRecorder | null = isReviewUsername ? null : startLoginTrace(payload.username);
    await waitForLocalSessionClear();
    trace?.mark("localClear");
    if (isReviewUsername) {
      // 在途清理可能于等待期间改变 gate，认证判断前再次同步隔离。
      get().rearmIosReviewPreAuth();
    }
    // 关键位置：审核账号必须在真实认证请求之前完成本地分流。
    const reviewAuthentication = tryAuthenticateIosReview({
      username: payload.username,
      password: payload.password,
      buildContext: getIosReviewBuildContext(),
    });
    if (reviewAuthentication.status === "invalid-password") {
      set({ isLoading: false });
      throw createInvalidIosReviewCredentialsError();
    }
    if (reviewAuthentication.status === "authenticated") {
      set({ isLoading: true });
      // 仅清审核页面可见的设备态，保留 DeviceStorage 中真实设备绑定。
      useDeviceStore.setState({
        session: null,
        isReady: true,
        isLoading: false,
      });
      try {
        // 切入离线审核会话前先停止普通账号可能遗留的后台定位任务。
        await stopAttendanceLocationTracking({ force: true }).catch((error) => {
          console.warn("[ios-review] 停止遗留后台定位失败", error);
        });
        // 审核会话只保留独立 marker，先清除可能残留的真实账号令牌。
        await SecureStorage.clearAll();
        await clearAuthSessionMarker();
        queryClient.clear();
        useCartStore.getState().reset();
        resetReviewData();
        await setIosReviewSessionActive();
        const user = createIosReviewUser();
        setIosReviewMenu();
        set({
          user,
          access: buildAccess(user),
          sessionKind: "iosReview",
          iosReviewOfflineGuardActive: true,
          standardAuthIntent: null,
          isAuthenticated: true,
          isLoading: false,
        });
        return;
      } catch (error) {
        await clearIosReviewSession().catch(() => undefined);
        set({ isLoading: false });
        throw error;
      }
    }

    get().beginStandardAuth();
    set({ isLoading: true });
    try {
      // 每一步都加超时：本机 Keychain 读写或请求拦截器若卡住，登录会报错恢复按钮，而不是一直转圈到重启 App。
      const tokenRes = await withLoginStepTimeout(
        loginApi({
          ...payload,
          // 登录密码输入框容易混入首尾空格，提交前统一按用户实际输入意图归一化。
          password: payload.password.trim(),
          passwordFormat: "raw",
        }),
        "loginApi",
        LOGIN_NETWORK_STEP_TIMEOUT_MS,
      );
      trace?.mark("loginApi");
      await withLoginStepTimeout(
        SecureStorage.setToken(tokenRes.accessToken),
        "saveAccessToken",
        LOGIN_LOCAL_STEP_TIMEOUT_MS,
      );
      trace?.mark("saveAccessToken");
      await withLoginStepTimeout(
        SecureStorage.setRefreshToken(tokenRes.refreshToken),
        "saveRefreshToken",
        LOGIN_LOCAL_STEP_TIMEOUT_MS,
      );
      trace?.mark("saveRefreshToken");

      const user = await withLoginStepTimeout(
        getCurrentUserApi(),
        "currentUser",
        LOGIN_NETWORK_STEP_TIMEOUT_MS,
      );
      trace?.mark("currentUser");
      await withLoginStepTimeout(SecureStorage.setUser(user), "saveUser", LOGIN_LOCAL_STEP_TIMEOUT_MS);
      trace?.mark("saveUser");
      await withLoginStepTimeout(
        setAuthSessionMarker("account"),
        "sessionMarker",
        LOGIN_LOCAL_STEP_TIMEOUT_MS,
      );
      trace?.mark("sessionMarker");

      set({
        user,
        access: buildAccess(user),
        sessionKind: "account",
        iosReviewOfflineGuardActive: false,
        standardAuthIntent: null,
        isAuthenticated: true,
        isLoading: false,
      });
      try {
        await withLoginStepTimeout(
          useAppNavigationStore.getState().fetchMenu(),
          "menu",
          LOGIN_MENU_TIMEOUT_MS,
        );
      } catch (menuError) {
        // 菜单自带降级与后台重试：超时不拦登录，继续进入首页，只记一笔诊断。
        if (!(menuError instanceof LoginStepTimeoutError)) throw menuError;
        trace?.markMenuTimedOut();
      }
      trace?.mark("menu");
      trace?.finish("success");
      void flushLoginDiagnostics();
    } catch (error) {
      trace?.finish(error instanceof LoginStepTimeoutError ? "timeout" : "failed", error);
      // 清理同样可能卡在 Keychain：超时就放弃等待，保证登录页能恢复。
      await withLoginStepTimeout(
        SecureStorage.clearAll(),
        "localClear",
        LOGIN_LOCAL_STEP_TIMEOUT_MS,
      ).catch(() => undefined);
      get().rearmIosReviewPreAuth();
      set({
        user: null,
        access: buildAccess(null),
        sessionKind: "account",
        isAuthenticated: false,
        isLoading: false,
      });
      throw error;
    }
  },

  async loginDeviceAccount() {
    await waitForLocalSessionClear();
    get().beginStandardAuth("deviceAccount");
    set({ isLoading: true });
    try {
      const binding = await loadDeviceAccountBindingForLogin({
        recoverPendingActivation: recoverStoredMobileDeviceActivation,
        loadBinding: DeviceAccountStorage.loadBinding,
      });

      clearSensitiveQueryCache(queryClient);
      useCartStore.getState().reset();
      // 同一设备同一绑定重建会话：保留菜单缓存。冷启动断网时这里会先跑到再失败，
      // 若把缓存清掉，随后的离线恢复就只剩设置页，商品查询 tab 消失。
      useAppNavigationStore.getState().reset({ keepMenuCache: true });
      await SecureStorage.clearAll();
      await clearAuthSessionMarker();

      const user = await establishDeviceAccountSession(binding, {
        exchange: exchangeMobileDeviceSessionApi,
        saveAccessToken: SecureStorage.setToken,
        removeRefreshToken: SecureStorage.removeRefreshToken,
        loadCurrentUser: getCurrentUserApi,
        saveCurrentUser: SecureStorage.setUser,
        markDeviceAccountSession: () => setAuthSessionMarker("deviceAccount"),
        loadNavigationMenu: () => useAppNavigationStore.getState().fetchMenu(),
      });

      set({
        user,
        access: buildAccess(user),
        sessionKind: "deviceAccount",
        iosReviewOfflineGuardActive: false,
        standardAuthIntent: null,
        isAuthenticated: true,
        isLoading: false,
      });
    } catch (error) {
      await SecureStorage.clearAll().catch(() => undefined);
      await clearAuthSessionMarker().catch(() => undefined);
      get().rearmIosReviewPreAuth();
      set({
        user: null,
        access: buildAccess(null),
        sessionKind: "account",
        isAuthenticated: false,
        isLoading: false,
      });
      throw error;
    }
  },

  async logout() {
    if (get().sessionKind === "iosReview") {
      await get().clearLocalSession();
      return;
    }

    if (get().sessionKind === "deviceAccount") {
      // 设备账号没有 refresh token；退出只清账号会话，保留设备绑定供再次登录。
      await get().clearLocalSession();
      return;
    }

    try {
      const rt = await SecureStorage.getRefreshToken();
      if (rt) {
        await logoutApi(rt);
      }
    } finally {
      await get().clearLocalSession();
    }
  },

  async hydrateIosReviewSession() {
    const buildContext = getIosReviewBuildContext();
    const buildEnabled = isIosReviewBuildEnabled(buildContext);
    configureIosReviewBuildGate(buildEnabled);
    set({ iosReviewOfflineGuardActive: isIosReviewSessionActive() });
    if (!buildEnabled) {
      // 非目标构建主动删除 marker，避免审核会话跨构建配置泄漏。
      await clearIosReviewSession().catch((error) => {
        console.warn("[ios-review] 清理非目标构建 marker 失败", error);
      });
      set({ iosReviewOfflineGuardActive: false });
      return false;
    }

    const restored = await restoreIosReviewSession();
    if (!restored) {
      // 仅本地检查既有普通 token/device；存在候选会话时才允许原恢复链路触网。
      const [storedToken, storedDevice, storedDeviceAccount, storedSessionKind] = await Promise.all([
        SecureStorage.getToken().catch(() => null),
        DeviceStorage.getSession().catch(() => null),
        DeviceAccountStorage.loadBinding().catch(() => null),
        getAuthSessionMarker().catch(() => null),
      ]);
      const hasStoredDeviceSession = Boolean(
        storedDevice?.hardwareId && storedDevice.authCode && storedDevice.storeCode
      ) && !storedDeviceAccount;
      const hasStoredDeviceAccountSession = Boolean(
        storedToken &&
          storedSessionKind === "deviceAccount" &&
          storedDeviceAccount?.hardwareId &&
          storedDeviceAccount.credential
      );
      if (storedToken || hasStoredDeviceSession) {
        get().beginStandardAuth(
          hasStoredDeviceAccountSession
            ? "deviceAccount"
            : hasStoredDeviceSession
              ? "device"
              : "account"
        );
      } else {
        set({ iosReviewOfflineGuardActive: true });
      }
      set({ isLoading: false });
      return false;
    }

    // marker 已确认后立即标记审核会话，后续任一步失败都会走 fail-closed 清理分支。
    set({
      sessionKind: "iosReview",
      iosReviewOfflineGuardActive: true,
      standardAuthIntent: null,
    });
    // marker 恢复后 active 已为 true，因此这里显式 force 清理上次普通会话遗留任务。
    await stopAttendanceLocationTracking({ force: true }).catch((error) => {
      console.warn("[ios-review] 恢复会话时停止遗留后台定位失败", error);
    });
    await SecureStorage.clearAll();
    await clearAuthSessionMarker();
    const user = createIosReviewUser();
    setIosReviewMenu();
    set({
      user,
      access: buildAccess(user),
      sessionKind: "iosReview",
      iosReviewOfflineGuardActive: true,
      standardAuthIntent: null,
      isAuthenticated: true,
      isLoading: false,
    });
    return true;
  },

  async restoreSession() {
    await waitForLocalSessionClear();
    set({ isLoading: true });
    try {
      if (await get().hydrateIosReviewSession()) {
        return true;
      }
      if (isIosReviewSessionActive()) {
        set({ isLoading: false });
        return false;
      }

      const [token, refreshToken, storedSessionKind, deviceAccountBinding] = await Promise.all([
        SecureStorage.getToken(),
        SecureStorage.getRefreshToken(),
        getAuthSessionMarker(),
        DeviceAccountStorage.loadBinding().catch(() => null),
      ]);
      const effectiveSessionKind = deriveEffectiveAuthSessionKind({
        persistedKind: storedSessionKind,
        hasAccessToken: Boolean(token),
        hasRefreshToken: Boolean(refreshToken),
        hasBinding: Boolean(deviceAccountBinding),
      });
      if (effectiveSessionKind === "deviceAccount" && deviceAccountBinding) {
        // loginDeviceAccount 会先清空本地令牌与用户缓存再联网交换；冷启动断网时
        // 需要先读出缓存用户，网络类失败才允许用它恢复设备绑定账号会话。
        const [cachedUser, storedDeviceSession] = await Promise.all([
          SecureStorage.getUser<CurrentUser>().catch(() => null),
          DeviceStorage.getSession().catch(() => null),
        ]);
        try {
          await get().loginDeviceAccount();
          return true;
        } catch (error) {
          const decision = resolveOfflineSessionRestore({
            error,
            sessionKind: "deviceAccount",
            hasStoredDeviceSession: Boolean(
              storedDeviceSession?.hardwareId && storedDeviceSession.authCode
            ),
            cachedUser,
          });
          if (decision !== "restore-from-cache" || !cachedUser) {
            throw error;
          }
          // 离线恢复：重新落盘缓存用户与会话标记，联网后 401 刷新链路会用设备绑定重新换取 token。
          await SecureStorage.setUser(cachedUser);
          await setAuthSessionMarker("deviceAccount");
          set({
            user: cachedUser,
            access: buildAccess(cachedUser),
            sessionKind: "deviceAccount",
            iosReviewOfflineGuardActive: false,
            standardAuthIntent: null,
            isAuthenticated: true,
            isLoading: false,
          });
          await useAppNavigationStore.getState().fetchMenu();
          return true;
        }
      }
      if (!token) {
        // 已保存设备验证失败后可能已放行全局 gate；无账号可恢复时必须重新 fail-closed。
        get().rearmIosReviewPreAuth();
        set({ isLoading: false });
        return false;
      }

      const user = await getCurrentUserApi();
      await SecureStorage.setUser(user);
      await setAuthSessionMarker("account");

      set({
        user,
        access: buildAccess(user),
        sessionKind: "account",
        iosReviewOfflineGuardActive: false,
        standardAuthIntent: null,
        isAuthenticated: true,
        isLoading: false,
      });
      await useAppNavigationStore.getState().fetchMenu();
      // 已保存账号会话恢复成功：顺带补传上次异常登录（如登录途中被关闭的 App）的诊断记录。
      void flushLoginDiagnostics();
      return true;
    } catch {
      await get().clearLocalSession();
      return false;
    }
  },

  /**
   * 重新拉取当前账号（角色、权限、分店）并重建 access，用于后台改权限后免重新登录生效。
   * 只刷新已登录的账号会话；审核会话与纯设备会话不处理。失败时保留原会话，由调用方提示。
   */
  async refreshCurrentUser() {
    const before = get();
    if (!before.isAuthenticated || !before.user || isIosReviewSessionActive()) {
      return false;
    }
    const user = await getCurrentUserApi();
    // 请求期间可能已退出或切换账号：只在同一账号仍在线时覆盖，避免把旧账号数据写回。
    const current = get();
    const sameUser = (current.user?.userGUID || current.user?.userGuid) === (user.userGUID || user.userGuid);
    if (!current.isAuthenticated || !sameUser) {
      return false;
    }
    await SecureStorage.setUser(user);
    set({ user, access: buildAccess(user) });
    return true;
  },

  async clearLocalSession() {
    if (clearLocalSessionInFlight) {
      return clearLocalSessionInFlight;
    }

    const clearOperation = get().performLocalSessionClear();
    clearLocalSessionInFlight = clearOperation;
    try {
      await clearOperation;
    } finally {
      if (clearLocalSessionInFlight === clearOperation) {
        clearLocalSessionInFlight = null;
      }
    }
  },

  async performLocalSessionClear() {
    const wasIosReview = get().sessionKind === "iosReview";
    const preservesStoredDeviceAuthentication =
      !wasIosReview && get().standardAuthIntent === "device";
    if (preservesStoredDeviceAuthentication) {
      set({ standardAuthIntent: null });
    } else {
      get().rearmIosReviewPreAuth();
    }

    // 先同步清空查询与认证 UI，整个异步清理窗口都保持 fail-closed。
    if (wasIosReview) {
      queryClient.clear();
    } else {
      clearSensitiveQueryCache(queryClient);
    }
    useCartStore.getState().reset();
    // 释放离线目录：中止进行中的下载并关闭 SQLite，否则登出后仍持有句柄、
    // 仍在用新会话的设备头继续拉旧门店数据。
    void closeOfflineCatalogRuntime();
    // 保留菜单缓存：它按 hardwareId + 账号 GUID 隔离，换账号或换绑都不会命中旧菜单，
    // 而纯设备模式冷启动会先经过这里再做设备校验，清掉就等于断网时没有商品查询入口。
    // 真正需要清除的是设备解绑与设备校验被拒，那两条路径在 device-store 里无参调用 reset()。
    useAppNavigationStore.getState().reset({ keepMenuCache: true });
    set({
      user: null,
      access: buildAccess(null),
      sessionKind: preservesStoredDeviceAuthentication ? "device" : "account",
      iosReviewOfflineGuardActive:
        iosReviewBuildEnabled || isIosReviewSessionActive(),
      standardAuthIntent: null,
      isAuthenticated: false,
      isLoading: false,
    });

    await stopAttendanceLocationTracking(
      wasIosReview ? { force: true } : undefined
    ).catch((error) => {
      console.warn("[attendance-location] 停止后台定位失败", error);
    });

    try {
      await clearIosReviewSession();
    } catch (error) {
      console.warn("[ios-review] 删除审核会话 marker 失败", error);
    } finally {
      // marker 清理失败也不能阻断真实账号令牌和本地认证残留清理。
      await SecureStorage.clearAll().catch((error) => {
        console.warn("[auth] 清理真实账号令牌失败", error);
      });
      await clearAuthSessionMarker().catch((error) => {
        console.warn("[auth] 清理认证会话类型失败", error);
      });
      await AppAsyncStorage.removeItem(STORE_SELECTION_STORAGE_KEY).catch(
        (error) => {
          console.warn("[auth] 清理门店选择失败", error);
        }
      );
      if (wasIosReview) {
        // 只清审核态的内存展示，保留用户原有真实设备绑定供普通设备登录恢复。
        useDeviceStore.setState({
          session: null,
          isReady: true,
          isLoading: false,
        });
      }
    }

    if (preservesStoredDeviceAuthentication) {
      // 清理审核 marker 会重新启用 pre-auth 守卫；设备会话恢复前必须再次显式放行。
      beginStandardAuthentication();
      set({
        sessionKind: "device",
        iosReviewOfflineGuardActive:
          iosReviewBuildEnabled || isIosReviewSessionActive(),
        standardAuthIntent: null,
      });
    }
  },

  async clearAccountSessionForDeviceLogin() {
    await waitForLocalSessionClear();
    // 专用于设备登录切换：清账号 token，但不重置审核 pre-auth gate 或持久设备绑定。
    clearSensitiveQueryCache(queryClient);
    useCartStore.getState().reset();
    // 同上：设备登录切换保留同一台设备的菜单缓存，隔离交给 scopeKey。
    useAppNavigationStore.getState().reset({ keepMenuCache: true });
    await SecureStorage.clearAll();
    await clearAuthSessionMarker();
    await AppAsyncStorage.removeItem(STORE_SELECTION_STORAGE_KEY);
    set({
      user: null,
      access: buildAccess(null),
      sessionKind: "device",
      iosReviewOfflineGuardActive:
        iosReviewBuildEnabled || isIosReviewSessionActive(),
      standardAuthIntent: null,
      isAuthenticated: false,
      isLoading: false,
    });
  },

  beginStandardAuth(kind = "account") {
    beginStandardAuthentication();
    set({
      // review 构建只放行显式认证请求；Root 副作用等待账号或设备真正认证成功。
      iosReviewOfflineGuardActive:
        iosReviewBuildEnabled || isIosReviewSessionActive(),
      standardAuthIntent: kind,
    });
  },

  rearmIosReviewPreAuth() {
    configureIosReviewBuildGate(iosReviewBuildEnabled);
    set({
      iosReviewOfflineGuardActive: isIosReviewSessionActive(),
      standardAuthIntent: null,
    });
  },

  setSessionKind(kind) {
    set({
      sessionKind: kind,
      ...(kind === "device" || kind === "deviceAccount"
        ? {
            iosReviewOfflineGuardActive: false,
            standardAuthIntent: null,
          }
        : {}),
    });
  },
}));

subscribeUnauthenticatedSession(() => {
  void useAuthStore.getState().clearLocalSession();
});
