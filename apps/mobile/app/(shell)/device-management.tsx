import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  ActivityIndicator,
  Icon,
  IconButton,
  Searchbar,
  SegmentedButtons,
  Snackbar,
  Text,
} from "react-native-paper";
import { AppDeviceCard, RegisteredDeviceCard } from "@/components/device-management/DeviceCards";
import {
  AppFilterSheet,
  RegisteredFilterSheet,
  VersionPickerSheet,
  type Option,
} from "@/components/device-management/DeviceFilterSheets";
import { FilterChip, RemovableChip, StatusTag, type Translate } from "@/components/device-management/primitives";
import { AppVersionDistributionCard, RegisteredVersionDistribution } from "@/components/device-management/VersionDistribution";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import {
  useAppDeviceStatuses,
  useAppDeviceStatusSummary,
  useAppVersionDistribution,
  useDeviceManagementDevices,
  useDeviceManagementMutations,
  useDeviceVersionSummary,
} from "@/modules/device-management/hooks";
import { DEVICE_STATUS } from "@/modules/device-management/status";
import { ActivationCodePanel, DeviceEditSheet, EmergencyLoginSheet } from "@/modules/device-management/admin-sheets";
import { isDeviceRegistrationId } from "@/modules/device-management/admin-types";
import { buildStoreNameLookup, formatDeviceStoreLabel } from "@/modules/device-management/store-label";
import { PERMISSIONS } from "@/shared/utils/access";
import type {
  AppDeviceOnlineState,
  AppDeviceStatus,
  AppDeviceStatusQuery,
  AppVersionState,
  DeviceManagementDevice,
  DeviceManagementQuery,
  DeviceVersionState,
} from "@/modules/device-management/types";
import {
  countAppVersionStates,
  groupAppVersionDistribution,
  type AppVersionOption,
} from "@/modules/device-management/version-state";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

type StatusFilter = "all" | "pendingConfirmation" | "active" | "disabled" | "locked" | "unregistered";
type DeviceSystemFilter = "all" | "Android" | "iOS" | "Windows" | "Mac";
type DeviceTypeFilter = "all" | "Mobile" | "PDA" | "POS" | "Admin";
type DeviceAction = "activate" | "disable" | "lock";
type DeviceManagementViewMode = "registered" | "appUsage" | "activation";
type RegisteredVersionStateFilter = "all" | DeviceVersionState;
type AppVersionStateFilter = "all" | AppVersionState;
type DeviceRow = DeviceManagementDevice & {
  systemDeviceNumber?: string | null;
  deviceNumber?: string | null;
  deviceType?: string | null;
  deviceSystem?: string | null;
};
type DeviceManagementListRow = DeviceRow | AppDeviceStatus;

const STATUS_FILTERS: StatusFilter[] = ["all", "pendingConfirmation", "active", "disabled", "locked", "unregistered"];
const DEVICE_SYSTEM_FILTERS: DeviceSystemFilter[] = ["all", "Android", "iOS", "Windows", "Mac"];
const DEVICE_TYPE_FILTERS: DeviceTypeFilter[] = ["all", "Mobile", "PDA", "POS", "Admin"];
const APP_ONLINE_FILTERS: AppDeviceOnlineState[] = ["all", "online", "offline"];
const PAGE_SIZE = 20;
const APP_VERSION_STATE_LABEL_KEYS: Record<AppVersionStateFilter, string> = {
  all: "versionFilter.all",
  latest: "versionFilter.appLatest",
  behind: "versionFilter.appBehind",
  unreported: "versionFilter.unreported",
};

function getDeviceKey(item: DeviceRow) {
  return String(item.id);
}

function getRegistrationId(item: DeviceRow) {
  return isDeviceRegistrationId(item.registrationId) ? item.registrationId : null;
}

function getDeviceTitle(item: DeviceRow, fallback: string) {
  return item.systemDeviceNumber || item.deviceNumber || item.deviceName || item.hardwareId || fallback;
}

function getAppDeviceKey(item: AppDeviceStatus) {
  return item.id || item.hardwareId;
}

function getAppDeviceTitle(item: AppDeviceStatus, fallback: string) {
  return item.systemDeviceNumber || item.hardwareId || fallback;
}

function statusFilterToQueryValue(status: StatusFilter) {
  switch (status) {
    case "pendingConfirmation":
      return DEVICE_STATUS.PENDING_CONFIRMATION;
    case "disabled":
      return DEVICE_STATUS.DISABLED;
    case "active":
      return DEVICE_STATUS.ACTIVE;
    case "locked":
      return DEVICE_STATUS.LOCKED;
    case "unregistered":
      return DEVICE_STATUS.UNREGISTERED;
    case "all":
    default:
      return undefined;
  }
}

function deviceSystemFilterToQueryValue(deviceSystem: DeviceSystemFilter) {
  // 全部选项只存在于前端 UI，避免把 all 传给后端枚举筛选。
  return deviceSystem === "all" ? undefined : deviceSystem;
}

function deviceTypeFilterToQueryValue(deviceType: DeviceTypeFilter) {
  // 全部选项只存在于前端 UI，避免把 all 传给后端枚举筛选。
  return deviceType === "all" ? undefined : deviceType;
}

export default function DeviceManagementScreen() {
  const { t, language } = useAppTranslation(["deviceManagement", "common"]);
  const access = useAuthStore((state) => state.access);
  const userGuid = useAuthStore((state) => state.user?.userGuid ?? state.user?.userGUID ?? null);

  if (!access.canViewDeviceRegistration) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState title={t("messages.noAccessTitle")} description={t("messages.noAccessDescription")} />
      </SafeAreaView>
    );
  }

  return (
    <DeviceManagementAdminContent
      canManageDeviceRegistration={access.canManageDeviceRegistration}
      canViewLegacyDeviceRegistration={access.canManageDeviceRegistration || access.hasPermission(PERMISSIONS.DeviceRegistration.View)}
      canManageDeviceActivationCodes={access.canManageDeviceActivationCodes}
      canManageMobileDeviceActivationCodes={access.canManageMobileDeviceActivationCodes}
      canManageEmergencyLoginGrants={access.canManageEmergencyLoginGrants}
      userGuid={userGuid}
      language={language}
      t={t}
    />
  );
}

function DeviceManagementAdminContent({
  canManageDeviceRegistration,
  canViewLegacyDeviceRegistration,
  canManageDeviceActivationCodes,
  canManageMobileDeviceActivationCodes,
  canManageEmergencyLoginGrants,
  userGuid,
  language,
  t,
}: {
  canManageDeviceRegistration: boolean;
  canViewLegacyDeviceRegistration: boolean;
  canManageDeviceActivationCodes: boolean;
  canManageMobileDeviceActivationCodes: boolean;
  canManageEmergencyLoginGrants: boolean;
  userGuid: string | null;
  language: string;
  t: ReturnType<typeof useAppTranslation>["t"];
}) {
  const { stores, isLoading: storesLoading } = useStores();
  // 后端（旧版）没回填分店名称时，用已加载的分店列表把代码翻成名称。
  const storeNameLookup = useMemo(() => buildStoreNameLookup(stores), [stores]);
  const [selectedViewMode, setViewMode] = useState<DeviceManagementViewMode>(canViewLegacyDeviceRegistration ? "registered" : "activation");
  const [filtersVisible, setFiltersVisible] = useState(false);
  const [versionPickerVisible, setVersionPickerVisible] = useState(false);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [resumeFiltersAfterStorePicker, setResumeFiltersAfterStorePicker] = useState(false);
  const [managedStoreCode, setManagedStoreCode] = useState<string | null>(null);
  const [keywordInput, setKeywordInput] = useState("");
  const [keyword, setKeyword] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
  const [deviceSystemFilter, setDeviceSystemFilter] = useState<DeviceSystemFilter>("all");
  const [deviceTypeFilter, setDeviceTypeFilter] = useState<DeviceTypeFilter>("all");
  // 版本筛选：设备注册按「指定版本（多选）或状态预设」二选一；App 按「指定版本（单选，含构建号）或状态预设」二选一。
  const [appVersionsFilter, setAppVersionsFilter] = useState<string[]>([]);
  const [versionStateFilter, setVersionStateFilter] = useState<RegisteredVersionStateFilter>("all");
  const [selectedAppVersion, setSelectedAppVersion] = useState<AppVersionOption | null>(null);
  const [appVersionStateFilter, setAppVersionStateFilter] = useState<AppVersionStateFilter>("all");
  const canManageActivation = canManageDeviceActivationCodes || canManageMobileDeviceActivationCodes;
  // 权限变化当帧即切换有效视图，不能先请求一次无权访问的设备列表。
  const viewMode: DeviceManagementViewMode = !canViewLegacyDeviceRegistration
    ? "activation"
    : selectedViewMode === "activation" && !canManageActivation ? "registered" : selectedViewMode;
  const [pageNumber, setPageNumber] = useState(1);
  const [pagedDevices, setPagedDevices] = useState<DeviceRow[]>([]);
  const [appOnlineFilter, setAppOnlineFilter] = useState<AppDeviceOnlineState>("all");
  const [appPageNumber, setAppPageNumber] = useState(1);
  const [pagedAppDevices, setPagedAppDevices] = useState<AppDeviceStatus[]>([]);
  const [busyDeviceKey, setBusyDeviceKey] = useState<string | null>(null);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [editingRegistrationId, setEditingRegistrationId] = useState<number | null>(null);
  const [actionDevice, setActionDevice] = useState<DeviceRow | null>(null);
  const [emergencyVisible, setEmergencyVisible] = useState(false);
  useEffect(() => {
    if (!canManageDeviceRegistration) {
      setEditingRegistrationId(null);
      setActionDevice(null);
    }
    if (!canManageEmergencyLoginGrants) setEmergencyVisible(false);
  }, [canManageDeviceRegistration, canManageEmergencyLoginGrants]);

  const managedStore = useMemo(
    () => stores.find((store) => store.storeCode === managedStoreCode) ?? null,
    [managedStoreCode, stores]
  );
  const trimmedKeyword = keyword.trim();
  // 版本汇总与列表用同一套范围条件，但不含版本条件，点选某个版本后其余版本仍可切换。
  const registeredScope = useMemo(
    () => ({
      keyword: trimmedKeyword || undefined,
      status: statusFilterToQueryValue(statusFilter),
      deviceSystem: deviceSystemFilterToQueryValue(deviceSystemFilter),
      deviceType: deviceTypeFilterToQueryValue(deviceTypeFilter),
      storeCode: managedStoreCode || null,
    }),
    [deviceSystemFilter, deviceTypeFilter, managedStoreCode, statusFilter, trimmedKeyword]
  );
  const query = useMemo<DeviceManagementQuery>(
    () => ({
      ...registeredScope,
      appVersions: appVersionsFilter,
      versionState: versionStateFilter,
      pageNumber,
      pageSize: PAGE_SIZE,
    }),
    [appVersionsFilter, pageNumber, registeredScope, versionStateFilter]
  );
  const appStatusQueryParams = useMemo<AppDeviceStatusQuery>(
    () => ({
      keyword: trimmedKeyword || undefined,
      deviceSystem: deviceSystemFilterToQueryValue(deviceSystemFilter),
      storeCode: managedStoreCode || null,
      onlineState: appOnlineFilter,
      appVersion: selectedAppVersion?.appVersion ?? null,
      appBuildVersion: selectedAppVersion?.appBuildVersion ?? null,
      versionState: appVersionStateFilter,
      pageNumber: appPageNumber,
      pageSize: PAGE_SIZE,
    }),
    [appOnlineFilter, appPageNumber, appVersionStateFilter, deviceSystemFilter, managedStoreCode, selectedAppVersion, trimmedKeyword]
  );
  const appSummaryQueryParams = useMemo(
    () => ({
      keyword: trimmedKeyword || undefined,
      deviceSystem: deviceSystemFilterToQueryValue(deviceSystemFilter),
      storeCode: managedStoreCode || null,
      appVersion: selectedAppVersion?.appVersion ?? null,
      appBuildVersion: selectedAppVersion?.appBuildVersion ?? null,
      versionState: appVersionStateFilter,
    }),
    [appVersionStateFilter, deviceSystemFilter, managedStoreCode, selectedAppVersion, trimmedKeyword]
  );
  // 分布不带系统条件：选了某个系统的版本后，其他系统的版本仍要能点选切换。
  const appDistributionQueryParams = useMemo(
    () => ({ keyword: trimmedKeyword || undefined, storeCode: managedStoreCode || null }),
    [managedStoreCode, trimmedKeyword]
  );

  const devicesQuery = useDeviceManagementDevices(query, canViewLegacyDeviceRegistration && viewMode === "registered");
  const versionSummaryQuery = useDeviceVersionSummary(registeredScope, canViewLegacyDeviceRegistration && viewMode === "registered");
  const appStatusesQuery = useAppDeviceStatuses(appStatusQueryParams, canViewLegacyDeviceRegistration && viewMode === "appUsage");
  const appSummaryQuery = useAppDeviceStatusSummary(appSummaryQueryParams, canViewLegacyDeviceRegistration && viewMode === "appUsage");
  const appDistributionQuery = useAppVersionDistribution(appDistributionQueryParams, canViewLegacyDeviceRegistration && viewMode === "appUsage");
  const { activateMutation, disableMutation, lockMutation } = useDeviceManagementMutations();
  const devices = pagedDevices;
  const appDevices = pagedAppDevices;
  const registrationTotal = devicesQuery.data?.pagination.totalCount ?? devices.length;
  const appTotal = appStatusesQuery.data?.pagination.totalCount ?? appDevices.length;
  const total = viewMode === "appUsage" ? appSummaryQuery.data?.total ?? appTotal : registrationTotal;
  const hasNextPage =
    viewMode === "appUsage"
      ? appPageNumber < (appStatusesQuery.data?.pagination.totalPages ?? 1)
      : pageNumber < (devicesQuery.data?.pagination.totalPages ?? 1);

  const appVersionGroups = useMemo(() => groupAppVersionDistribution(appDistributionQuery.data), [appDistributionQuery.data]);
  const appStateCounts = useMemo(
    () => countAppVersionStates(appDistributionQuery.data, deviceSystemFilterToQueryValue(deviceSystemFilter)),
    [appDistributionQuery.data, deviceSystemFilter]
  );

  const registeredVersionActive = appVersionsFilter.length > 0 || versionStateFilter !== "all";
  const appVersionActive = Boolean(selectedAppVersion) || appVersionStateFilter !== "all";
  const activeFilterCount =
    viewMode === "appUsage"
      ? [trimmedKeyword, managedStoreCode, deviceSystemFilter !== "all", appOnlineFilter !== "all", appVersionActive].filter(Boolean).length
      : [
          trimmedKeyword,
          managedStoreCode,
          statusFilter !== "all",
          deviceSystemFilter !== "all",
          deviceTypeFilter !== "all",
          registeredVersionActive,
        ].filter(Boolean).length;
  const hasActiveFilters = activeFilterCount > 0;

  useEffect(() => {
    setPageNumber(1);
    setPagedDevices([]);
  }, [appVersionsFilter, deviceSystemFilter, deviceTypeFilter, managedStoreCode, statusFilter, trimmedKeyword, versionStateFilter]);

  useEffect(() => {
    setAppPageNumber(1);
    setPagedAppDevices([]);
  }, [appOnlineFilter, appVersionStateFilter, deviceSystemFilter, managedStoreCode, selectedAppVersion, trimmedKeyword]);

  useEffect(() => {
    if (!devicesQuery.data?.devices) {
      return;
    }

    const nextDevices = devicesQuery.data.devices as DeviceRow[];
    setPagedDevices((current) => {
      if (pageNumber === 1) {
        return nextDevices;
      }

      const seen = new Set(current.map((item) => getDeviceKey(item)));
      const appended = nextDevices.filter((item) => !seen.has(getDeviceKey(item)));
      return [...current, ...appended];
    });
  }, [devicesQuery.data?.devices, pageNumber]);

  useEffect(() => {
    if (!appStatusesQuery.data?.devices) {
      return;
    }

    const nextDevices = appStatusesQuery.data.devices;
    setPagedAppDevices((current) => {
      if (appPageNumber === 1) {
        return nextDevices;
      }

      const seen = new Set(current.map((item) => getAppDeviceKey(item)));
      const appended = nextDevices.filter((item) => !seen.has(getAppDeviceKey(item)));
      return [...current, ...appended];
    });
  }, [appPageNumber, appStatusesQuery.data?.devices]);

  const submitKeyword = useCallback(() => {
    setKeyword(keywordInput.trim());
  }, [keywordInput]);

  const clearKeyword = useCallback(() => {
    setKeywordInput("");
    setKeyword("");
  }, []);

  const clearFilters = useCallback(() => {
    setManagedStoreCode(null);
    setStatusFilter("all");
    setDeviceSystemFilter("all");
    setDeviceTypeFilter("all");
    setAppOnlineFilter("all");
    setAppVersionsFilter([]);
    setVersionStateFilter("all");
    setSelectedAppVersion(null);
    setAppVersionStateFilter("all");
    clearKeyword();
  }, [clearKeyword]);

  const openStorePickerFromFilters = useCallback(() => {
    setResumeFiltersAfterStorePicker(true);
    setFiltersVisible(false);
    setStorePickerVisible(true);
  }, []);

  const closeStorePicker = useCallback(() => {
    setStorePickerVisible(false);
    if (resumeFiltersAfterStorePicker) {
      setResumeFiltersAfterStorePicker(false);
      setFiltersVisible(true);
    }
  }, [resumeFiltersAfterStorePicker]);

  const handleSelectStore = useCallback((store: Store | null) => {
    setManagedStoreCode(store?.storeCode ?? null);
    closeStorePicker();
  }, [closeStorePicker]);

  // 设备注册的版本条件：指定版本与状态预设互斥，选一边就清掉另一边。
  const toggleRegisteredVersion = useCallback((version: string) => {
    setVersionStateFilter("all");
    setAppVersionsFilter((current) => (current.includes(version) ? current.filter((item) => item !== version) : [...current, version]));
  }, []);

  const chooseRegisteredVersionState = useCallback((value: string) => {
    setAppVersionsFilter([]);
    // 再点一次已选中的预设等于取消。
    setVersionStateFilter((current) => (current === value ? "all" : (value as RegisteredVersionStateFilter)));
  }, []);

  const chooseAppVersionState = useCallback((value: string) => {
    setSelectedAppVersion(null);
    setAppVersionStateFilter((current) => (current === value ? "all" : (value as AppVersionStateFilter)));
  }, []);

  const selectAppVersion = useCallback((key: string) => {
    const option = appVersionGroups.flatMap((group) => group.options).find((item) => item.key === key) ?? null;
    setAppVersionStateFilter("all");
    // 再点一次已选中的版本等于取消。
    if (!option || selectedAppVersion?.key === key) {
      setSelectedAppVersion(null);
      return;
    }
    // 版本号只在同一系统内有意义（1.0.11 在 iOS 与 Android 是两回事），选版本时同步系统条件。
    if (option.deviceSystem === "Android" || option.deviceSystem === "iOS") setDeviceSystemFilter(option.deviceSystem);
    setSelectedAppVersion(option);
  }, [appVersionGroups, selectedAppVersion]);

  const changeDeviceSystem = useCallback((value: string) => {
    const next = value as DeviceSystemFilter;
    setDeviceSystemFilter(next);
    // 换到别的系统后，之前选的版本不再属于该系统，一并清掉。
    setSelectedAppVersion((current) => (current && next !== "all" && current.deviceSystem !== next ? null : current));
  }, []);

  const handleRefresh = useCallback(async () => {
    try {
      if (viewMode === "appUsage") {
        if (appPageNumber !== 1) {
          setAppPageNumber(1);
          return;
        }
        await Promise.all([appStatusesQuery.refetch(), appSummaryQuery.refetch(), appDistributionQuery.refetch()]);
        return;
      }

      if (pageNumber !== 1) {
        setPageNumber(1);
        return;
      }
      await Promise.all([devicesQuery.refetch(), versionSummaryQuery.refetch()]);
    } catch (error) {
      console.warn("[device-management] refresh failed", error);
      setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.refreshFailed" }));
    }
  }, [appDistributionQuery, appPageNumber, appStatusesQuery, appSummaryQuery, devicesQuery, language, pageNumber, t, versionSummaryQuery, viewMode]);

  const handleLoadMore = useCallback(() => {
    if (viewMode === "appUsage") {
      if (!hasNextPage || appStatusesQuery.isFetching) {
        return;
      }
      setAppPageNumber((current) => current + 1);
      return;
    }

    if (!hasNextPage || devicesQuery.isFetching) {
      return;
    }
    setPageNumber((current) => current + 1);
  }, [appStatusesQuery.isFetching, devicesQuery.isFetching, hasNextPage, viewMode]);

  const runDeviceAction = useCallback(
    (action: DeviceAction, item: DeviceRow) => {
      const deviceKey = getDeviceKey(item);
      const registrationId = getRegistrationId(item);
      if (!registrationId) {
        setSnackbarMessage(t("messages.invalidRegistrationId"));
        return;
      }
      const deviceTitle = getDeviceTitle(item, t("fields.unnamedDevice"));
      const actionLabel = t(`actions.${action}`);
      const mutation =
        action === "activate" ? activateMutation : action === "disable" ? disableMutation : lockMutation;

      Alert.alert(
        t("dialogs.confirmTitle", { action: actionLabel }),
        t("dialogs.confirmMessage", { action: actionLabel, device: deviceTitle }),
        [
          { text: t("actions.cancel"), style: "cancel" },
          {
            text: actionLabel,
            style: action === "activate" ? "default" : "destructive",
            onPress: () => {
              void (async () => {
                try {
                  setBusyDeviceKey(deviceKey);
                  setPageNumber(1);
                  await mutation.mutateAsync({
                    id: registrationId,
                  });
                  setSnackbarMessage(t(`messages.${action}Success`));
                } catch (error) {
                  console.warn(`[device-management] ${action} failed`, error);
                  setSnackbarMessage(resolveLocalizedErrorMessage(error, {
                    t,
                    language,
                    fallbackKey: `messages.${action}Failed`,
                  }));
                } finally {
                  setBusyDeviceKey(null);
                }
              })();
            },
          },
        ]
      );
    },
    [activateMutation, disableMutation, language, lockMutation, t]
  );

  // 更多操作抽屉先关闭再弹确认框：iOS 上原生 Modal 之上叠 Alert 会丢失其中一个。
  const chooseDeviceAction = useCallback(
    (action: DeviceAction) => {
      const target = actionDevice;
      setActionDevice(null);
      if (target) setTimeout(() => runDeviceAction(action, target), 250);
    },
    [actionDevice, runDeviceAction]
  );

  const renderDeviceCard = useCallback(
    ({ item }: { item: DeviceRow }) => (
      <RegisteredDeviceCard
        item={item}
        title={getDeviceTitle(item, t("fields.unnamedDevice"))}
        storeLabel={formatDeviceStoreLabel(item, storeNameLookup, t("fields.noStore"))}
        t={t}
        language={language}
        canManage={canManageDeviceRegistration}
        busy={busyDeviceKey === getDeviceKey(item)}
        hasRegistrationId={getRegistrationId(item) !== null}
        onEdit={() => setEditingRegistrationId(getRegistrationId(item))}
        onMore={() => setActionDevice(item)}
      />
    ),
    [busyDeviceKey, canManageDeviceRegistration, language, storeNameLookup, t]
  );

  const renderAppDeviceCard = useCallback(
    ({ item }: { item: AppDeviceStatus }) => (
      <AppDeviceCard
        item={item}
        title={getAppDeviceTitle(item, t("fields.unnamedDevice"))}
        storeLabel={formatDeviceStoreLabel(item, storeNameLookup, t("fields.noStore"))}
        t={t}
        language={language}
      />
    ),
    [language, storeNameLookup, t]
  );

  const isAppUsageView = viewMode === "appUsage";
  const listData: DeviceManagementListRow[] = isAppUsageView ? appDevices : devices;
  const isCurrentLoading = isAppUsageView ? appStatusesQuery.isLoading : devicesQuery.isLoading;
  const isCurrentFetching = isAppUsageView ? appStatusesQuery.isFetching : devicesQuery.isFetching;
  const isCurrentError = isAppUsageView ? appStatusesQuery.isError : devicesQuery.isError;
  const currentError = isAppUsageView ? appStatusesQuery.error : devicesQuery.error;
  const currentPageNumber = isAppUsageView ? appPageNumber : pageNumber;

  // 窄屏上英文标签容易被截断：隐藏选中对勾腾出宽度，字号收一档（Paper 的横向内边距写死在内部，改不了）。
  const tabLabelStyle = { fontSize: 13 };
  const viewButtons = [
    ...(canViewLegacyDeviceRegistration
      ? [
          { value: "registered", label: t("views.registered"), showSelectedCheck: false, labelStyle: tabLabelStyle },
          { value: "appUsage", label: t("views.appUsage"), showSelectedCheck: false, labelStyle: tabLabelStyle },
        ]
      : []),
    ...(canManageActivation ? [{ value: "activation", label: t("views.activation"), showSelectedCheck: false, labelStyle: tabLabelStyle }] : []),
  ];

  const header = (subtitle: string, showRefresh: boolean) => (
    <View style={styles.titleBlock}>
      <View style={styles.titleRow}>
        <View style={styles.titleWrap}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{subtitle}</Text>
        </View>
        {showRefresh ? (
          <IconButton
            mode="outlined"
            icon="refresh"
            size={22}
            accessibilityLabel={t("actions.refresh")}
            disabled={isCurrentFetching}
            onPress={handleRefresh}
            style={styles.refreshButton}
          />
        ) : null}
      </View>
      <SegmentedButtons value={viewMode} onValueChange={(value) => setViewMode(value as DeviceManagementViewMode)} buttons={viewButtons} />
    </View>
  );

  if (viewMode === "activation") {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <View style={styles.activationScreen}>
          {header(t("activation.subtitle"), false)}
          {canManageDeviceActivationCodes || canManageMobileDeviceActivationCodes ? (
            <ActivationCodePanel
              canManagePos={canManageDeviceActivationCodes}
              canManageMobile={canManageMobileDeviceActivationCodes}
              userGuid={userGuid}
              language={language}
              t={t}
              onMessage={setSnackbarMessage}
            />
          ) : (
            <EmptyState title={t("activation.noAccessTitle")} description={t("activation.noAccessDescription")} />
          )}
        </View>
        <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={3000}>
          {snackbarMessage}
        </Snackbar>
      </SafeAreaView>
    );
  }

  const systemOptions: Option[] = DEVICE_SYSTEM_FILTERS.map((value) => ({ value, label: t(`filters.systemOptions.${value}`) }));
  const storeLabel = managedStore?.storeName || t("currentStore.allStores");
  const registeredVersionLabel =
    appVersionsFilter.length > 0
      ? appVersionsFilter.length === 1
        ? appVersionsFilter[0]
        : t("versionFilter.versionsSelected", { count: appVersionsFilter.length })
      : t(`versionFilter.${versionStateFilter}`);
  const appVersionLabel = selectedAppVersion
    ? selectedAppVersion.appBuildVersion
      ? `${selectedAppVersion.appVersion} (${selectedAppVersion.appBuildVersion})`
      : selectedAppVersion.appVersion ?? ""
    : t(APP_VERSION_STATE_LABEL_KEYS[appVersionStateFilter]);
  const removeLabel = t("filters.remove");

  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      <FlatList<DeviceManagementListRow>
        data={listData}
        keyExtractor={(item) =>
          isAppUsageView
            ? getAppDeviceKey(item as AppDeviceStatus)
            : getDeviceKey(item as unknown as DeviceRow)
        }
        renderItem={({ item }) =>
          isAppUsageView
            ? renderAppDeviceCard({ item: item as AppDeviceStatus })
            : renderDeviceCard({ item: item as unknown as DeviceRow })
        }
        refreshControl={
          <RefreshControl refreshing={isCurrentFetching && !isCurrentLoading} onRefresh={handleRefresh} />
        }
        contentContainerStyle={styles.listContent}
        onEndReached={handleLoadMore}
        onEndReachedThreshold={0.35}
        ListHeaderComponent={
          <View style={styles.headerWrap}>
            {header(t(isAppUsageView ? "appUsage.subtitle" : "subtitle", { count: total }), true)}

            {isAppUsageView ? (
              <View style={styles.statsRow}>
                <View style={styles.statCell}>
                  <Text style={styles.statLabel}>{t("appUsage.summary.total")}</Text>
                  <Text style={styles.statValue}>{appSummaryQuery.data?.total ?? 0}</Text>
                </View>
                <View style={[styles.statCell, styles.statCellBorder]}>
                  <Text style={styles.statLabel}>{t("appUsage.summary.online")}</Text>
                  <Text style={[styles.statValue, { color: HB_COLORS.success }]}>{appSummaryQuery.data?.online ?? 0}</Text>
                </View>
                <View style={[styles.statCell, styles.statCellBorder]}>
                  <Text style={styles.statLabel}>{t("appUsage.summary.offline")}</Text>
                  <Text style={styles.statValue}>{appSummaryQuery.data?.offline ?? 0}</Text>
                </View>
              </View>
            ) : null}

            {isAppUsageView ? (
              <AppVersionDistributionCard groups={appVersionGroups} selectedKey={selectedAppVersion?.key ?? null} onSelect={selectAppVersion} t={t} />
            ) : (
              <RegisteredVersionDistribution
                summary={versionSummaryQuery.data}
                selectedVersions={appVersionsFilter}
                unreportedSelected={versionStateFilter === "unreported" && appVersionsFilter.length === 0}
                onToggleVersion={toggleRegisteredVersion}
                onSelectUnreported={() => chooseRegisteredVersionState("unreported")}
                t={t}
              />
            )}

            <View style={styles.searchRow}>
              <Searchbar
                placeholder={t("searchPlaceholder")}
                value={keywordInput}
                onChangeText={setKeywordInput}
                onSubmitEditing={submitKeyword}
                onIconPress={submitKeyword}
                onClearIconPress={clearKeyword}
                style={styles.searchbar}
                inputStyle={styles.searchInput}
              />
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={activeFilterCount > 0 ? t("filters.buttonWithCount", { count: activeFilterCount }) : t("filters.button")}
                onPress={() => setFiltersVisible(true)}
                style={[styles.filterButton, hasActiveFilters && styles.filterButtonActive]}
              >
                <Icon source="filter-variant" size={22} color={hasActiveFilters ? HB_COLORS.action : HB_COLORS.textSecondary} />
                {activeFilterCount > 0 ? (
                  <View style={styles.badge}>
                    <Text style={styles.badgeText}>{activeFilterCount}</Text>
                  </View>
                ) : null}
              </Pressable>
            </View>

            {hasActiveFilters ? (
              <View style={styles.activeFilterChips}>
                {managedStoreCode ? (
                  <RemovableChip icon="store-outline" label={managedStore?.storeName || managedStoreCode} removeLabel={removeLabel} onRemove={() => setManagedStoreCode(null)} />
                ) : null}
                {!isAppUsageView && statusFilter !== "all" ? (
                  <RemovableChip label={t(`filters.${statusFilter}`)} removeLabel={removeLabel} onRemove={() => setStatusFilter("all")} />
                ) : null}
                {deviceSystemFilter !== "all" ? (
                  <RemovableChip label={t(`filters.systemOptions.${deviceSystemFilter}`)} removeLabel={removeLabel} onRemove={() => changeDeviceSystem("all")} />
                ) : null}
                {!isAppUsageView && deviceTypeFilter !== "all" ? (
                  <RemovableChip label={t(`filters.typeOptions.${deviceTypeFilter}`)} removeLabel={removeLabel} onRemove={() => setDeviceTypeFilter("all")} />
                ) : null}
                {isAppUsageView && appOnlineFilter !== "all" ? (
                  <RemovableChip label={t(`appUsage.onlineFilters.${appOnlineFilter}`)} removeLabel={removeLabel} onRemove={() => setAppOnlineFilter("all")} />
                ) : null}
                {!isAppUsageView && registeredVersionActive ? (
                  <RemovableChip
                    icon="tag-outline"
                    label={registeredVersionLabel}
                    removeLabel={removeLabel}
                    onRemove={() => {
                      setAppVersionsFilter([]);
                      setVersionStateFilter("all");
                    }}
                  />
                ) : null}
                {isAppUsageView && appVersionActive ? (
                  <RemovableChip
                    icon="tag-outline"
                    label={appVersionLabel}
                    removeLabel={removeLabel}
                    onRemove={() => {
                      setSelectedAppVersion(null);
                      setAppVersionStateFilter("all");
                    }}
                  />
                ) : null}
                {trimmedKeyword ? (
                  <RemovableChip icon="magnify" label={trimmedKeyword} removeLabel={removeLabel} onRemove={clearKeyword} />
                ) : null}
              </View>
            ) : null}

            {isAppUsageView ? (
              <View style={styles.quickChips}>
                {APP_ONLINE_FILTERS.map((onlineState) => (
                  <FilterChip
                    key={onlineState}
                    label={t(`appUsage.onlineFilters.${onlineState}`)}
                    count={onlineState === "online" ? appSummaryQuery.data?.online : onlineState === "offline" ? appSummaryQuery.data?.offline : undefined}
                    selected={appOnlineFilter === onlineState}
                    onPress={() => setAppOnlineFilter(onlineState)}
                  />
                ))}
                <FilterChip
                  label={t("versionFilter.appBehind")}
                  count={appStateCounts.behind}
                  selected={appVersionStateFilter === "behind" && !selectedAppVersion}
                  onPress={() => chooseAppVersionState("behind")}
                />
              </View>
            ) : null}

            {isCurrentError ? (
              <EmptyState
                title={t("messages.loadFailedTitle")}
                description={resolveLocalizedErrorMessage(currentError, {
                  t,
                  language,
                  fallbackKey: "messages.loadFailedDescription",
                })}
                primaryAction={{
                  label: t("common:actions.retry"),
                  icon: "refresh",
                  onPress: () => void handleRefresh(),
                }}
              />
            ) : null}

            {!isCurrentLoading && !isCurrentError && listData.length === 0 ? (
              <EmptyState
                title={hasActiveFilters ? t("messages.emptySearchTitle") : t("messages.emptyTitle")}
                description={
                  hasActiveFilters ? t("messages.emptySearchDescription") : t("messages.emptyDescription")
                }
              />
            ) : null}
          </View>
        }
        ListFooterComponent={
          isCurrentLoading || (isCurrentFetching && currentPageNumber > 1) ? (
            <View style={styles.loadingWrap}>
              <ActivityIndicator />
            </View>
          ) : null
        }
      />

      <StorePickerModal
        visible={storePickerVisible}
        presentation="sheet"
        stores={stores}
        selectedStoreCode={managedStoreCode}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        includeAllOption
        allLabel={t("currentStore.allStores")}
        onDismiss={closeStorePicker}
        onSelectStore={handleSelectStore}
      />

      {canManageEmergencyLoginGrants && viewMode === "registered" ? (
        <View style={styles.emergencyTrigger}>
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ disabled: !managedStoreCode }}
            disabled={!managedStoreCode}
            onPress={() => setEmergencyVisible(true)}
            style={[styles.emergencyButton, !managedStoreCode && styles.emergencyButtonDisabled]}
          >
            <Icon source="shield-alert-outline" size={20} color={HB_COLORS.action} />
            <Text style={styles.emergencyText}>{t("emergency.title")}</Text>
          </Pressable>
          {!managedStoreCode ? <Text style={styles.emergencyHint}>{t("emergency.selectStore")}</Text> : null}
        </View>
      ) : null}

      <DeviceEditSheet
        visible={canManageDeviceRegistration && editingRegistrationId !== null}
        registrationId={editingRegistrationId}
        language={language}
        t={t}
        onDismiss={() => setEditingRegistrationId(null)}
        onSaved={handleRefresh}
        onMessage={setSnackbarMessage}
      />
      {canManageEmergencyLoginGrants ? <EmergencyLoginSheet
        visible={emergencyVisible && canManageEmergencyLoginGrants}
        storeCode={managedStoreCode}
        userGuid={userGuid}
        language={language}
        t={t}
        onDismiss={() => setEmergencyVisible(false)}
        onMessage={setSnackbarMessage}
      /> : null}

      <DeviceActionSheet device={actionDevice} t={t} onDismiss={() => setActionDevice(null)} onChoose={chooseDeviceAction} />

      {isAppUsageView ? (
        <AppFilterSheet
          visible={filtersVisible}
          t={t}
          onDismiss={() => setFiltersVisible(false)}
          onClear={clearFilters}
          storeLabel={storeLabel}
          storesLoading={storesLoading}
          onPickStore={openStorePickerFromFilters}
          onlineOptions={APP_ONLINE_FILTERS.map((value) => ({ value, label: t(`appUsage.onlineFilters.${value}`) }))}
          online={appOnlineFilter}
          onOnline={(value) => setAppOnlineFilter(value as AppDeviceOnlineState)}
          systemOptions={systemOptions.filter((option) => option.value === "all" || option.value === "Android" || option.value === "iOS")}
          system={deviceSystemFilter}
          onSystem={changeDeviceSystem}
          stateCounts={appStateCounts}
          versionState={appVersionStateFilter}
          onVersionState={chooseAppVersionState}
          groups={appVersionGroups}
          selectedKey={selectedAppVersion?.key ?? null}
          onSelectVersion={selectAppVersion}
          resultCount={total}
        />
      ) : (
        <RegisteredFilterSheet
          visible={filtersVisible}
          t={t}
          onDismiss={() => setFiltersVisible(false)}
          onClear={clearFilters}
          storeLabel={storeLabel}
          storesLoading={storesLoading}
          onPickStore={openStorePickerFromFilters}
          statusOptions={STATUS_FILTERS.map((value) => ({ value, label: t(`filters.${value}`) }))}
          status={statusFilter}
          onStatus={(value) => setStatusFilter(value as StatusFilter)}
          systemOptions={systemOptions}
          system={deviceSystemFilter}
          onSystem={changeDeviceSystem}
          typeOptions={DEVICE_TYPE_FILTERS.map((value) => ({ value, label: t(`filters.typeOptions.${value}`) }))}
          type={deviceTypeFilter}
          onType={(value) => setDeviceTypeFilter(value as DeviceTypeFilter)}
          summary={versionSummaryQuery.data}
          versionState={versionStateFilter}
          onVersionState={chooseRegisteredVersionState}
          appVersions={appVersionsFilter}
          onToggleVersion={toggleRegisteredVersion}
          onOpenVersionPicker={() => {
            setFiltersVisible(false);
            setVersionPickerVisible(true);
          }}
          resultCount={total}
        />
      )}

      <VersionPickerSheet
        visible={versionPickerVisible}
        t={t}
        summary={versionSummaryQuery.data}
        selected={appVersionsFilter}
        onCancel={() => {
          setVersionPickerVisible(false);
          setFiltersVisible(true);
        }}
        onConfirm={(versions) => {
          setVersionStateFilter("all");
          setAppVersionsFilter(versions);
          setVersionPickerVisible(false);
          setFiltersVisible(true);
        }}
      />

      <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2500}>
        {snackbarMessage}
      </Snackbar>
    </SafeAreaView>
  );
}

/** 「更多」操作：启用 / 禁用 / 锁定。危险操作的二次确认沿用原来的 Alert。 */
function DeviceActionSheet({
  device,
  t,
  onDismiss,
  onChoose,
}: {
  device: DeviceRow | null;
  t: Translate;
  onDismiss: () => void;
  onChoose: (action: DeviceAction) => void;
}) {
  const rows: { action: DeviceAction; icon: string; danger: boolean }[] = [
    { action: "activate", icon: "play-circle-outline", danger: false },
    { action: "disable", icon: "pause-circle-outline", danger: true },
    { action: "lock", icon: "lock-outline", danger: true },
  ];

  return (
    <BusinessSheet
      visible={device !== null}
      title={device ? getDeviceTitle(device, t("fields.unnamedDevice")) : ""}
      subtitle={t("card.actionsSubtitle")}
      onDismiss={onDismiss}
    >
      {rows.map(({ action, icon, danger }) => {
        const isCurrent = (action === "activate" && device?.status === DEVICE_STATUS.ACTIVE);
        return (
          <Pressable
            key={action}
            accessibilityRole="button"
            disabled={isCurrent}
            onPress={() => onChoose(action)}
            style={({ pressed }) => [styles.actionRow, pressed && styles.pressed, isCurrent && styles.actionRowDisabled]}
          >
            <Icon source={icon} size={22} color={danger ? HB_COLORS.danger : HB_COLORS.action} />
            <Text style={[styles.actionLabel, danger && { color: HB_COLORS.danger }]}>{t(`actions.${action}`)}</Text>
            {isCurrent ? <View><StatusTag tone="ok" label={t("card.currentStatus")} /></View> : null}
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  activationScreen: { flex: 1, gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs },
  headerWrap: { gap: HB_SPACING.sm, marginBottom: HB_SPACING.xs },
  listContent: { gap: HB_SPACING.sm, padding: HB_SPACING.md, paddingBottom: 32 },
  loadingWrap: { alignItems: "center", paddingVertical: HB_SPACING.lg },
  titleBlock: { gap: HB_SPACING.sm },
  titleRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm, justifyContent: "space-between" },
  titleWrap: { flex: 1, gap: 2 },
  title: { fontSize: 24, lineHeight: 32, fontWeight: "700", color: HB_COLORS.textPrimary },
  subtitle: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  refreshButton: { margin: 0, borderColor: HB_COLORS.outline },
  statsRow: { flexDirection: "row", backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  statCell: { flex: 1, paddingVertical: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  statCellBorder: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outlineMuted },
  statLabel: { fontSize: 11, lineHeight: 16, color: HB_COLORS.textSecondary },
  statValue: { fontSize: 22, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  searchRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  searchbar: { flex: 1, height: 44, borderRadius: 10, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, elevation: 0 },
  searchInput: { minHeight: 0, fontSize: 14 },
  filterButton: { width: 44, height: 44, borderRadius: 10, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, alignItems: "center", justifyContent: "center" },
  filterButtonActive: { backgroundColor: "#EAF2FF", borderColor: "#B2CCFF" },
  badge: { position: "absolute", top: -6, right: -6, minWidth: 18, height: 18, borderRadius: 9, backgroundColor: HB_COLORS.action, alignItems: "center", justifyContent: "center", paddingHorizontal: 4 },
  badgeText: { color: HB_COLORS.white, fontSize: 11, lineHeight: 14, fontWeight: "700" },
  activeFilterChips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  quickChips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  emergencyTrigger: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, borderWidth: StyleSheet.hairlineWidth, gap: 6, marginBottom: HB_SPACING.sm, marginHorizontal: HB_SPACING.md, padding: HB_SPACING.sm },
  emergencyButton: { minHeight: 44, borderRadius: 10, borderWidth: 1, borderColor: HB_COLORS.outline, flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 8 },
  emergencyButtonDisabled: { opacity: 0.5 },
  emergencyText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.action },
  emergencyHint: { fontSize: 12, color: HB_COLORS.textSecondary },
  pressed: { opacity: 0.7 },
  actionRow: { minHeight: 52, flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  actionRowDisabled: { opacity: 0.5 },
  actionLabel: { flex: 1, fontSize: 16, color: HB_COLORS.textPrimary },
});
