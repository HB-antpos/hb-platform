import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
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
import { shouldHidePrimaryTabBar } from "@/modules/navigation/primary-navigation";
import { getPriceUpdatePendingCount } from "@/modules/price-updates/api";
import { priceUpdateCountQueryKey } from "@/modules/price-updates/query-keys";
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
  const canReadUsers = useAuthStore((state) => state.access.hasPermission("Users.View"));
  const canReadRoles = useAuthStore((state) => state.access.hasPermission("Roles.View"));
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
  // 角标跟随当前分店：设备模式固定为绑定分店，账号模式取全局选中的分店。
  const priceUpdateStoreCode = isDeviceMode
    ? deviceSession?.storeCode ?? null
    : selectedStoreCode ?? null;
  const pendingPriceUpdateQuery = useQuery({
    queryKey: priceUpdateCountQueryKey(priceUpdateStoreCode),
    enabled:
      navigationReady
      && Boolean(priceUpdateStoreCode)
      && visibleRouteNames.has(PRICE_UPDATES_ROUTE),
    queryFn: () => getPriceUpdatePendingCount(priceUpdateStoreCode!),
    staleTime: 30_000,
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
        pendingPriceUpdateCount: pendingPriceUpdateQuery.data ?? 0,
        newProductKindCount,
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
        </View>
        {shouldHidePrimaryTabBar(pathname) ? null : <PrimaryTabBar activeRouteName={currentRouteName} />}
      </View>
    </AppNavigationAccessProvider>
  );
}
