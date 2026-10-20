import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, AppState, View } from "react-native";
import { Redirect, Stack, usePathname, useRouter } from "expo-router";
import { PrimaryTabBar } from "@/components/navigation";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { useAppDeviceStatusHeartbeat } from "@/modules/device-management/use-app-device-heartbeat";
import {
  filterAccountTabRouteNames,
  getVisibleTabRouteNames,
  resolvePreferredDefaultTabRoute,
  resolveTabRouteCorrection,
  TAB_PATHS,
} from "@/modules/navigation/default-route";
import { prepareStoredDeviceSession } from "@/modules/auth/device-login-session";
import { shouldForcePasswordChange } from "@/modules/auth/password-change";
import {
  EMPLOYEE_PROFILE_REVIEW_ROUTE,
  filterEmployeeProfileReviewRouteNames,
  getEmployeeProfileReviewAccess,
} from "@/modules/employee-profile-review/access";
import { getEmployeeProfileReviewRequestsApi } from "@/modules/employee-profile-review/api";
import { AppNavigationAccessProvider } from "@/modules/navigation/access-context";
import { FACE_HUB_ROUTE_NAME, shouldFetchFaceHitUnread } from "@/modules/navigation/face-hub";
import { FACE_HIT_NOTIFICATION_TYPE, faceHitUnreadQueryKey } from "@/modules/face-match/hit-unread";
import { FACE_MATCH_PERMISSIONS } from "@/modules/face-match/constants";
import { getUnreadCount } from "@/modules/notifications/api";
import { fetchLogBadgeSummary } from "@/modules/legacy-employee-logs/api";
import { legacyLogBadgeQueryKey, resolveLogBadgeLevel, shouldFetchLogBadge } from "@/modules/legacy-employee-logs/badge";
import { subscribeLegacyLogReviewed } from "@/modules/legacy-employee-logs/review-events";
import { usePushRegistration } from "@/modules/push/use-push-registration";
import { shouldHidePrimaryTabBar } from "@/modules/navigation/primary-navigation";
import { usePriceTaskOverview } from "@/modules/price-updates/use-price-task-overview";
import { PriceReminderHost } from "@/modules/price-reminder/PriceReminderHost";
import { FloatingLanguageButton } from "@/modules/floating-language";
import { AccountSwitchHost } from "@/modules/account-switch";
import { canViewContainerNewProducts } from "@/modules/container-new-products/access";
import { containerNewProductsQueryKey, getContainerNewProducts } from "@/modules/container-new-products/api";
import { countNewProductKinds } from "@/modules/container-new-products/summary";
import { useCartStore } from "@/store/cart-store";
import { canAccessVersionManagement, filterVersionManagementRoutes } from "@/modules/navigation/version-management-access";
import { resolveIdentityAdminRouteNames } from "@/modules/navigation/identity-admin-access";
import { isNetworkUnavailableError } from "@/shared/network/network-error";

export const unstable_settings = {
  initialRouteName: "workbench",
};

/** 离线设备会话补校验的重试间隔：网络恢复前不断重试，恢复后立刻补上设备校验。 */
const OFFLINE_DEVICE_REVALIDATE_INTERVAL_MS = 60_000;
const PRICE_UPDATES_ROUTE = "price-updates";
const CONTAINER_NEW_PRODUCTS_ROUTE = "container-new-products";
/** 「黑名单命中未读数」（红色数字）：30 秒内视为新鲜，前台每 60 秒轮询一次，回到前台立即刷新。 */
const FACE_HIT_UNREAD_STALE_MS = 30_000;
const FACE_HIT_UNREAD_REFETCH_INTERVAL_MS = 60_000;
const LEGACY_LOG_ROUTE = "legacy-employee-logs";
/** 员工操作日志圆点：与人脸命中未读同一节奏（30 秒新鲜、前台每 60 秒轮询、回前台立即刷新）。 */
const LEGACY_LOG_BADGE_STALE_MS = 30_000;
const LEGACY_LOG_BADGE_REFETCH_INTERVAL_MS = 60_000;

export default function ShellLayout() {
  const router = useRouter();
  const pathname = usePathname();
  const currentRouteName = pathname.split("/").filter(Boolean).pop();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const currentUser = useAuthStore((state) => state.user);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const isLoading = useAuthStore((state) => state.isLoading);
  const restoreSession = useAuthStore((state) => state.restoreSession);
  const clearLocalAuthSession = useAuthStore((state) => state.clearLocalSession);
  const setSessionKind = useAuthStore((state) => state.setSessionKind);
  const deviceSession = useDeviceStore((state) => state.session);
  const selectedStoreCode = useCartStore((state) => state.selectedStore?.storeCode);
  const accountBinding = useDeviceStore((state) => state.accountBinding);
  const deviceHydrated = useDeviceStore((state) => state.isReady);
  const validateDevice = useDeviceStore((state) => state.validate);
  const navigationItems = useAppNavigationStore((state) => state.items);
  const navigationReady = useAppNavigationStore((state) => state.isReady);
  const navigationLoading = useAppNavigationStore((state) => state.isLoading);
  const navigationErrorMessage = useAppNavigationStore((state) => state.errorMessage);
  const canViewAttendanceManagement = useAuthStore(
    (state) => state.access.canViewAttendanceManagement
  );
  const canCreateOrder = useAuthStore((state) => state.access.canCreateOrder);
  const isAdmin = useAuthStore((state) => state.access.isAdmin);
  const isWarehouseManager = useAuthStore((state) => state.access.isWarehouseManager);
  const isOrderUser = useAuthStore((state) => state.access.onlyOrder);
  const canReadUsers = useAuthStore((state) => state.access.hasPermission("Users.View"));
  const canReadRoles = useAuthStore((state) => state.access.hasPermission("Roles.View"));
  // 黑名单命中通知只发给持有 FaceMatch.Unwelcome.Review 的人：没有它的人没有红色数字，也不必请求。
  const hasFaceUnwelcomeReview = useAuthStore((state) =>
    state.access.hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeReview)
  );
  // 员工操作日志圆点：老收银 / 新收银任一来源有查看权限才请求，且只请求有权限的来源。
  const canViewLegacyLogs = useAuthStore((state) => state.access.canViewLegacyEmployeeLogs);
  const canViewPosAudits = useAuthStore((state) => state.access.canViewPosOperationAudits);
  const iosReviewOfflineGuardActive = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const versionManagementAllowed = canAccessVersionManagement({ isAdmin, isAuthenticated, sessionKind });
  const isWarehouseStaffOnly = useAuthStore((state) => state.access.isWarehouseStaffOnly);
  const hasRestored = useRef(false);
  const shellMounted = useRef(true);
  const hasAppliedDefaultRoute = useRef(false);
  const awaitingPreferredDefaultRoute = useRef(false);
  const [heartbeatReady, setHeartbeatReady] = useState(false);
  const [heartbeatUsesDeviceSession, setHeartbeatUsesDeviceSession] = useState(false);
  const [boundAccountRestorePending, setBoundAccountRestorePending] = useState(false);
  // 断网冷启动进入的、尚未通过服务器校验的设备会话。
  const [offlineDeviceFallback, setOfflineDeviceFallback] = useState(false);
  const hasUserSession = Boolean(isAuthenticated && userGuid);
  const hasStoredDeviceSession = Boolean(
    deviceSession?.hardwareId && deviceSession.authCode && deviceSession.storeCode
  );
  const hasStoredDeviceAccountBinding = Boolean(
    accountBinding?.hardwareId && accountBinding.credential
  );
  const isIosReviewSession = sessionKind === "iosReview";
  // 店长新建或重置密码的账号：改成自己的密码前不进入任何业务页面。
  const mustChangePassword = shouldForcePasswordChange({ user: currentUser, sessionKind, isAuthenticated });
  useAppDeviceStatusHeartbeat({
    enabled:
      !isIosReviewSession &&
      heartbeatReady &&
      (hasUserSession || hasStoredDeviceSession),
    useDeviceSession: heartbeatUsesDeviceSession,
  });

  useEffect(() => {
    shellMounted.current = true;
    return () => {
      shellMounted.current = false;
    };
  }, []);

  useEffect(() => {
    if (hasRestored.current) {
      return;
    }

    if (!deviceHydrated) {
      return;
    }

    if (isIosReviewSession) {
      // 审核会话完全离线，不校验已保存设备，也不启动设备状态心跳。
      hasRestored.current = true;
      setHeartbeatReady(false);
      setHeartbeatUsesDeviceSession(false);
      return;
    }

    if (hasUserSession) {
      console.info("[startup-auth] using existing user session");
      hasRestored.current = true;
      setHeartbeatReady(false);
      setHeartbeatUsesDeviceSession(false);
      if (hasStoredDeviceSession) {
        let cancelled = false;
        async function validateStoredDeviceForHeartbeat() {
          try {
            const isValidDeviceSession = await validateDevice();
            if (!cancelled) {
              setHeartbeatUsesDeviceSession(isValidDeviceSession);
              setHeartbeatReady(true);
            }
          } catch {
            if (!cancelled) {
              setHeartbeatUsesDeviceSession(false);
              setHeartbeatReady(true);
            }
          }
        }

        void validateStoredDeviceForHeartbeat();
        return () => {
          cancelled = true;
        };
      }

      setHeartbeatReady(true);
      return;
    }

    if (hasStoredDeviceAccountBinding) {
      hasRestored.current = true;
      setBoundAccountRestorePending(true);

      async function ensureDeviceAccountSession() {
        const restored = await restoreSession();
        if (!shellMounted.current) {
          return;
        }
        setBoundAccountRestorePending(false);
        setHeartbeatUsesDeviceSession(restored && hasStoredDeviceSession);
        setHeartbeatReady(restored);
        if (!restored) {
          router.replace("/(auth)/login");
        }
      }

      void ensureDeviceAccountSession();
      return;
    }

    if (hasStoredDeviceSession) {
      let cancelled = false;
      const currentDeviceSession = deviceSession!;
      hasRestored.current = true;

      async function ensureDeviceSession() {
        try {
          console.info("[startup-auth] validating device session", {
            hardwareId: currentDeviceSession.hardwareId,
            storeCode: currentDeviceSession.storeCode,
            status: currentDeviceSession.status ?? null,
          });
          const isReady = await prepareStoredDeviceSession({
            clearAccountSession: clearLocalAuthSession,
            validateDevice,
          });
          if (isReady && !cancelled) {
            // 设备完成在线校验后才解除 review 构建的 Root 副作用守卫。
            setSessionKind("device");
            setHeartbeatUsesDeviceSession(true);
            setHeartbeatReady(true);
          }
          if (!isReady && !cancelled) {
            console.warn("[startup-auth] device session not ready, attempting account session restore", {
              hardwareId: currentDeviceSession.hardwareId,
              storeCode: currentDeviceSession.storeCode,
              status: currentDeviceSession.status ?? null,
            });
            const restored = await restoreSession();
            if (restored && !cancelled) {
              setHeartbeatUsesDeviceSession(false);
              setHeartbeatReady(true);
            }
            if (!restored && !cancelled) {
              console.warn("[startup-auth] no account session available after device validation rejection, redirecting to login");
              router.replace("/(auth)/login");
            }
          }
        } catch (error) {
          if (
            !cancelled &&
            isNetworkUnavailableError(error) &&
            currentDeviceSession.status === 1 &&
            currentDeviceSession.storeCode
          ) {
            // 冷启动断网：本地已有启用且绑定分店的设备会话，允许离线进入（心跳保持关闭），
            // 菜单走本地缓存。这里的设备会话尚未经服务器确认，必须标记为待补校验：
            // 启动 effect 有 hasRestored 闸门不会重跑，只能靠下面那个重试 effect 在
            // 网络恢复后补上校验并开启心跳，否则被远程停用的设备会一直可用。
            console.warn("[startup-auth] device validation unreachable, entering offline device session", {
              hardwareId: currentDeviceSession.hardwareId,
              storeCode: currentDeviceSession.storeCode,
            });
            setSessionKind("device");
            setHeartbeatUsesDeviceSession(false);
            setHeartbeatReady(false);
            setOfflineDeviceFallback(true);
            await useAppNavigationStore.getState().fetchMenu();
            return;
          }
          if (!cancelled) {
            console.warn("[startup-auth] device validation failed, attempting account session restore");
            const restored = await restoreSession();
            if (restored && !cancelled) {
              setHeartbeatUsesDeviceSession(false);
              setHeartbeatReady(true);
            }
            if (!restored && !cancelled) {
              console.warn("[startup-auth] device validation failed and no account session restored, redirecting to login");
              router.replace("/(auth)/login");
            }
          }
        }
      }

      void ensureDeviceSession();

      return () => {
        cancelled = true;
      };
    }

    let cancelled = false;

    async function ensureAuthenticated() {
      console.info("[startup-auth] restoring account session");
      const restored = await restoreSession();
      hasRestored.current = true;
      if (!cancelled) {
        setHeartbeatUsesDeviceSession(false);
        setHeartbeatReady(restored);
      }
      if (!restored && !cancelled) {
        console.warn("[startup-auth] no device session and no account session, redirecting to login");
        router.replace("/(auth)/login");
      }
    }

    void ensureAuthenticated();

    return () => {
      cancelled = true;
    };
  }, [
    clearLocalAuthSession,
    deviceHydrated,
    deviceSession,
    hasStoredDeviceSession,
    hasStoredDeviceAccountBinding,
    hasUserSession,
    isIosReviewSession,
    restoreSession,
    router,
    setSessionKind,
    validateDevice,
  ]);

  useEffect(() => {
    // 离线冷启动进入的设备会话必须补上服务器校验：App 回到前台时立刻试一次，
    // 之后按固定间隔重试，直到校验通过（开启心跳）或被服务端拒绝（回登录页）。
    if (!offlineDeviceFallback) {
      return;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const scheduleRetry = () => {
      if (cancelled || timer) {
        return;
      }
      timer = setTimeout(() => {
        timer = null;
        void retryValidation();
      }, OFFLINE_DEVICE_REVALIDATE_INTERVAL_MS);
    };

    async function retryValidation() {
      if (cancelled) {
        return;
      }
      try {
        const isReady = await validateDevice();
        if (cancelled) {
          return;
        }
        setOfflineDeviceFallback(false);
        if (isReady) {
          setHeartbeatUsesDeviceSession(true);
          setHeartbeatReady(true);
          return;
        }
        // 校验明确被拒：设备已被停用或解绑，不能再继续使用离线数据。
        console.warn("[startup-auth] offline device session rejected after reconnect, redirecting to login");
        router.replace("/(auth)/login");
        return;
      } catch (error) {
        if (!cancelled && !isNetworkUnavailableError(error)) {
          console.warn("[startup-auth] offline device revalidation failed", error);
        }
      }
      scheduleRetry();
    }

    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") {
        void retryValidation();
      }
    });
    scheduleRetry();

    return () => {
      cancelled = true;
      if (timer) {
        clearTimeout(timer);
      }
      subscription.remove();
    };
  }, [offlineDeviceFallback, router, validateDevice]);

  const isDeviceMode = Boolean(
    hasStoredDeviceSession && !hasStoredDeviceAccountBinding && !hasUserSession
  );
  const accountRouteNames = useMemo(
    () =>
      filterAccountTabRouteNames(
        resolveIdentityAdminRouteNames(navigationItems.map((item) => item.routeName), {
          isAuthenticated,
          sessionKind,
          iosReviewOfflineGuardActive,
          isAdmin,
          canReadUsers,
          canReadRoles,
          menuReady: navigationReady && !navigationLoading && !navigationErrorMessage,
        }),
        { canCreateOrder, isWarehouseStaffOnly }
      ),
    [canCreateOrder, isWarehouseStaffOnly, navigationItems, isAuthenticated, sessionKind,
      iosReviewOfflineGuardActive, isAdmin, canReadUsers, canReadRoles,
      navigationReady, navigationLoading, navigationErrorMessage]
  );
  const employeeProfileReviewAccess = useMemo(
    () => getEmployeeProfileReviewAccess({
      roleNames: currentUser?.roleNames,
      permissions: currentUser?.permissions,
      menuRouteNames: navigationItems.map((item) => item.routeName),
      sessionKind,
    }),
    [currentUser?.permissions, currentUser?.roleNames, navigationItems, sessionKind]
  );
  const orderedVisibleRouteNames = useMemo(
    () => filterVersionManagementRoutes(filterEmployeeProfileReviewRouteNames(
      getVisibleTabRouteNames({
        routeNames: accountRouteNames,
        isDeviceMode,
        canViewAttendanceManagement,
      }),
      employeeProfileReviewAccess.allowed
    ), versionManagementAllowed),
    [
      accountRouteNames,
      canViewAttendanceManagement,
      employeeProfileReviewAccess.allowed,
      versionManagementAllowed,
      isDeviceMode,
    ]
  );
  const visibleRouteNames = useMemo(
    () => new Set(orderedVisibleRouteNames),
    [orderedVisibleRouteNames]
  );
  const pendingReviewQuery = useQuery({
    queryKey: ["employeeProfileReview", "requests", "Pending", "count"],
    enabled:
      navigationReady
      && employeeProfileReviewAccess.allowed
      && visibleRouteNames.has(EMPLOYEE_PROFILE_REVIEW_ROUTE),
    queryFn: () => getEmployeeProfileReviewRequestsApi({
      page: 1,
      pageSize: 1,
      status: "Pending",
    }),
    staleTime: 30_000,
  });
  // 设备模式固定为绑定分店，账号模式取全局选中的分店（HB新品等按当前分店查询的入口仍用它）。
  const priceUpdateStoreCode = isDeviceMode
    ? deviceSession?.storeCode ?? null
    : selectedStoreCode ?? null;
  // 价格和标签：一次按分店汇总，角标 = 名下全部分店的合计（不再只看当前分店），
  // 同时算出「管理分店有超期 → 报表锁定」，经导航上下文分发给工作台、报表页和每小时提醒。
  const accountStores = useCartStore((state) => state.userStores);
  const deviceBoundStore = useMemo(
    () =>
      deviceSession?.storeCode
        ? {
            storeCode: deviceSession.storeCode,
            storeName: deviceSession.storeName || deviceSession.storeCode,
          }
        : null,
    [deviceSession?.storeCode, deviceSession?.storeName]
  );
  const { overview: priceTaskOverview, refresh: refreshPriceTasks } = usePriceTaskOverview({
    enabled: navigationReady && visibleRouteNames.has(PRICE_UPDATES_ROUTE),
    scopeKey: isDeviceMode
      ? `device:${deviceSession?.hardwareId ?? ""}:${deviceSession?.storeCode ?? ""}`
      : `account:${userGuid ?? ""}`,
    stores: accountStores,
    isDeviceMode,
    deviceBoundStore,
    isAdmin,
    isWarehouseManager,
    isOrderUser,
  });
  // HB新品角标与页面共用查询和缓存；清单一天内变化很少，5 分钟内回到工作台不重复拉取
  const hasNewProductsPermission = useAuthStore((state) => state.access.hasPermission);
  const newProductsQuery = useQuery({
    queryKey: containerNewProductsQueryKey(priceUpdateStoreCode),
    enabled:
      navigationReady
      && Boolean(priceUpdateStoreCode)
      && visibleRouteNames.has(CONTAINER_NEW_PRODUCTS_ROUTE)
      && canViewContainerNewProducts(isAuthenticated, hasNewProductsPermission, iosReviewOfflineGuardActive),
    queryFn: () => getContainerNewProducts(priceUpdateStoreCode!),
    staleTime: 5 * 60_000,
  });
  const newProductKindCount = useMemo(
    () => countNewProductKinds(newProductsQuery.data?.items ?? []),
    [newProductsQuery.data?.items]
  );
  // 红色数字：当前登录人还没看的黑名单命中通知数。仅当导航就绪、face-hub 可见且持有 Review 时才取；
  // 失败时 react-query 保留上一次的数据，数字不会闪成 0；账号切换靠查询键里的账号编号隔离。
  const faceHitUnreadEnabled = shouldFetchFaceHitUnread({
    navigationReady,
    isAuthenticated,
    isReviewSession: isIosReviewSession || iosReviewOfflineGuardActive,
    isDeviceMode,
    faceHubVisible: visibleRouteNames.has(FACE_HUB_ROUTE_NAME),
    hasUnwelcomeReview: hasFaceUnwelcomeReview,
  });
  const [appActive, setAppActive] = useState(() => AppState.currentState === "active");
  const wasAppActiveRef = useRef(appActive);
  const legacyLogBadgeWasActive = useRef(appActive);
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => setAppActive(next === "active"));
    return () => subscription.remove();
  }, []);
  const faceHitUnreadQuery = useQuery({
    queryKey: faceHitUnreadQueryKey(userGuid),
    enabled: faceHitUnreadEnabled,
    queryFn: ({ signal }) => getUnreadCount(FACE_HIT_NOTIFICATION_TYPE, signal),
    staleTime: FACE_HIT_UNREAD_STALE_MS,
    // 只在前台轮询：切到后台后停止，回前台由下面的 effect 立即刷新。
    refetchInterval: appActive ? FACE_HIT_UNREAD_REFETCH_INTERVAL_MS : false,
  });
  const refetchFaceHitUnread = faceHitUnreadQuery.refetch;
  useEffect(() => {
    const wasActive = wasAppActiveRef.current;
    wasAppActiveRef.current = appActive;
    // 只在「后台 → 前台」的那一刻刷新；首次挂载由 useQuery 自己取，不重复请求。
    if (appActive && !wasActive && faceHitUnreadEnabled) {
      void refetchFaceHitUnread();
    }
  }, [appActive, faceHitUnreadEnabled, refetchFaceHitUnread]);
  // 员工操作日志圆点（红 / 橙，无数字）：失败时 react-query 保留上一次的数据；账号切换靠查询键里的账号编号隔离。
  const legacyLogBadgeEnabled = shouldFetchLogBadge({
    isAuthenticated,
    isReviewSession: isIosReviewSession || iosReviewOfflineGuardActive,
    isDeviceMode,
    navigationReady,
    logsRouteVisible: visibleRouteNames.has(LEGACY_LOG_ROUTE),
    canViewLegacy: canViewLegacyLogs,
    canViewPos: canViewPosAudits,
  });
  const queryClient = useQueryClient();
  const legacyLogBadgeQuery = useQuery({
    queryKey: legacyLogBadgeQueryKey(userGuid),
    enabled: legacyLogBadgeEnabled,
    queryFn: ({ signal }) => fetchLogBadgeSummary({ legacy: canViewLegacyLogs, pos: canViewPosAudits }, signal),
    staleTime: LEGACY_LOG_BADGE_STALE_MS,
    refetchInterval: appActive ? LEGACY_LOG_BADGE_REFETCH_INTERVAL_MS : false,
  });
  const refetchLegacyLogBadge = legacyLogBadgeQuery.refetch;
  useEffect(() => {
    // 只在「后台 → 前台」的那一刻刷新；用独立的 ref 记录上一次前台状态，不依赖人脸未读 effect 的先后顺序。
    if (appActive && legacyLogBadgeEnabled && legacyLogBadgeWasActive.current === false) {
      void refetchLegacyLogBadge();
    }
    legacyLogBadgeWasActive.current = appActive;
  }, [appActive, legacyLogBadgeEnabled, refetchLegacyLogBadge]);
  // 日志页里提交核查后立刻重取圆点，不必等 60 秒轮询。
  useEffect(
    () => subscribeLegacyLogReviewed(() => void queryClient.invalidateQueries({ queryKey: legacyLogBadgeQueryKey(userGuid) })),
    [queryClient, userGuid],
  );
  const shouldWaitForNavigation =
    (hasUserSession || isDeviceMode) && (!navigationReady || navigationLoading);
  const preferredDefaultRoute = resolvePreferredDefaultTabRoute({
    isDeviceMode,
    isWarehouseStaffOnly,
    routeNames: orderedVisibleRouteNames,
  });
  const shouldAwaitPreferredDefaultRouteRecovery = Boolean(
    navigationErrorMessage
      && (isDeviceMode || isWarehouseStaffOnly)
      && !preferredDefaultRoute
  );

  useEffect(() => {
    // 须改密时本次渲染会重定向到改密页，默认入口纠偏不能在同一轮把页面又导回壳层。
    if (mustChangePassword || shouldWaitForNavigation || visibleRouteNames.size === 0) {
      return;
    }

    if (shouldAwaitPreferredDefaultRouteRecovery) {
      awaitingPreferredDefaultRoute.current = true;
    }

    const preferredDefaultStillUnavailable = Boolean(
      awaitingPreferredDefaultRoute.current && !preferredDefaultRoute
    );
    const nextPath = resolveTabRouteCorrection({
      currentRouteName,
      hasAppliedDefaultRoute: awaitingPreferredDefaultRoute.current
        ? false
        : hasAppliedDefaultRoute.current,
      isDeviceMode,
      isWarehouseStaffOnly,
      routeNames: orderedVisibleRouteNames,
    });

    if (!nextPath) {
      if (preferredDefaultStillUnavailable) {
        hasAppliedDefaultRoute.current = false;
        return;
      }

      awaitingPreferredDefaultRoute.current = false;
      hasAppliedDefaultRoute.current = true;
      return;
    }

    if (preferredDefaultStillUnavailable) {
      hasAppliedDefaultRoute.current = false;
    } else {
      awaitingPreferredDefaultRoute.current = false;
      hasAppliedDefaultRoute.current = true;
    }
    if (nextPath === TAB_PATHS.workbench) {
      // 深链会自动把工作台放在栈底；回工作台时弹到锚点，避免 replace 生成重复根页。
      router.dismissTo(nextPath as Parameters<typeof router.dismissTo>[0]);
      return;
    }

    // 其他权限纠偏和默认入口使用 replace，确保被撤权页面不会留在返回历史中。
    router.replace(nextPath as Parameters<typeof router.replace>[0], { withAnchor: true });
  }, [
    isDeviceMode,
    isWarehouseStaffOnly,
    mustChangePassword,
    orderedVisibleRouteNames,
    currentRouteName,
    preferredDefaultRoute,
    router,
    shouldAwaitPreferredDefaultRouteRecovery,
    shouldWaitForNavigation,
    visibleRouteNames,
  ]);

  // 系统推送（Expo 推送服务）：只有个人账号会话、非 iOS 审核态才处理；主界面就绪（菜单加载完、不在改密流程）后才注册令牌和响应点击。
  // 必须放在上面的「默认入口纠偏」effect 之后：冷启动点击推送要导航到目标页，不能被默认入口的 replace 抢回去。
  // 旧安装包 / PDA / 模拟器上这里什么都不会发生（见 push-native.ts）。
  usePushRegistration({
    eligible: hasUserSession && sessionKind === "account" && !isIosReviewSession && !iosReviewOfflineGuardActive,
    ready:
      hasUserSession &&
      navigationReady &&
      !shouldWaitForNavigation &&
      !boundAccountRestorePending &&
      !mustChangePassword,
    userGuid,
  });

  if (
    boundAccountRestorePending ||
    shouldWaitForNavigation ||
    ((!deviceHydrated || !hasRestored.current) &&
      (isLoading || (isDeviceMode ? true : !isAuthenticated && !userGuid)))
  ) {
    return (
      <View
        style={{
          flex: 1,
          justifyContent: "center",
          alignItems: "center",
          backgroundColor: "#fff",
        }}
      >
        <ActivityIndicator size="large" color="#1677FF" />
      </View>
    );
  }

  if (mustChangePassword) {
    return <Redirect href={"/(auth)/change-password" as unknown as Parameters<typeof router.replace>[0]} />;
  }

  return (
    <AppNavigationAccessProvider
      value={{
        orderedVisibleRouteNames,
        navigationErrorMessage,
        navigationLoading,
        pendingProfileReviewCount: pendingReviewQuery.data?.total ?? 0,
        pendingPriceUpdateCount: priceTaskOverview.visiblePending,
        priceTasks: priceTaskOverview,
        refreshPriceTasks,
        newProductKindCount,
        faceHitUnreadCount: faceHitUnreadEnabled ? (faceHitUnreadQuery.data ?? 0) : 0,
        legacyLogBadgeLevel: legacyLogBadgeEnabled ? resolveLogBadgeLevel(legacyLogBadgeQuery.data) : null,
        isDeviceMode,
        isWarehouseStaffOnly,
      }}
    >
      <View style={{ flex: 1 }}>
        <View style={{ flex: 1 }}>
          <Stack
            screenOptions={{
              headerShown: false,
              gestureEnabled: true,
            }}
          />
          {/* 语言悬浮按钮（设置页开启，默认关闭）：盖在页面上、底部导航栏之上；按住 2 秒打开快速跳转。 */}
          <FloatingLanguageButton pathname={pathname} currentRouteName={currentRouteName} />
        </View>
        {shouldHidePrimaryTabBar(pathname) ? null : <PrimaryTabBar activeRouteName={currentRouteName} />}
        {/* 每小时提醒：放在导航上下文里，随时读到最新的待办与超期数。iOS 审核态不提醒。 */}
        <PriceReminderHost
          suppressed={isIosReviewSession || iosReviewOfflineGuardActive}
          isAdmin={isAdmin}
          isDeviceMode={isDeviceMode}
        />
        {/* 多账号：账号切换器 / 重新登录面板 / 切换中遮罩；放在最后盖住底部导航栏，切换期间挡住所有操作。 */}
        <AccountSwitchHost />
      </View>
    </AppNavigationAccessProvider>
  );
}
