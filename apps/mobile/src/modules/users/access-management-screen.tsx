import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import {
  Stack,
  useLocalSearchParams,
  useNavigation,
  useRouter,
} from "expo-router";
import {
  usePreventRemove,
  type NavigationAction,
} from "@react-navigation/native";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  ActivityIndicator,
  Avatar,
  Button,
  Card,
  Checkbox,
  Chip,
  IconButton,
  Searchbar,
  SegmentedButtons,
  Snackbar,
  Surface,
  Switch,
  Text,
  TouchableRipple,
} from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  useAccessRoleCatalog,
  useAccessStoreCatalog,
  useAssignUserAccessRoles,
  useAssignUserAccessStores,
  useAssignUserDirectPermissions,
  useUserAccessPermissionAccess,
  useUserAccessRoles,
  useUserAccessStores,
} from "./access-management-hooks";
import {
  applyUserAccessSectionRestrictions,
  areAccessCodeSetsEqual,
  buildDirectPermissionDraft,
  buildUserAccessStoreAssignments,
  classifyUserAccessError,
  filterUserAccessRoles,
  getAccessPermissionSelectionState,
  getAccessRoleSelectionState,
  getUserAccessEligibility,
  getUserAccessStoreState,
  hasPrivilegedAccessRole,
  isStoreManagerRoleName,
  limitUserAccessRolesForActor,
  setUserAccessStoreState,
  toggleDirectPermission,
} from "./access-management";
import {
  localizeAccessPermission,
  localizeAccessPermissionCategory,
  localizeAccessRoleDescription,
  localizeAccessRoleName,
} from "./access-permission-presentation";
import type {
  AccessStoreOption,
  DirectPermissionDraft,
  UserAccessErrorKind,
  UserAccessRole,
  UserAccessStoreAssignment,
  UserAccessStoreState,
} from "./access-management-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

type AccessSection = "stores" | "roles" | "permissions";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

function parseRoleNames(value: string | string[] | undefined) {
  const values = Array.isArray(value) ? value : value ? [value] : [];
  return values
    .flatMap((item) => item.split("|"))
    .map((item) => item.trim())
    .filter(Boolean);
}

function getInitials(value: string) {
  const words = value.trim().split(/\s+/).filter(Boolean);
  if (!words.length) return "?";
  return words.length === 1
    ? words[0].slice(0, 2).toUpperCase()
    : `${words[0][0]}${words[1][0]}`.toUpperCase();
}

function encodeStoreAssignments(assignments: UserAccessStoreAssignment[]) {
  return assignments.map(
    (item) => `${item.storeGUID}:${item.isPrimary ? "manage" : "view"}`,
  );
}

function mergeStores(
  catalog: AccessStoreOption[] = [],
  assigned: AccessStoreOption[] = [],
) {
  const byGuid = new Map<string, AccessStoreOption>();
  [...catalog, ...assigned].forEach((store) => {
    if (store.storeGUID.trim()) byGuid.set(store.storeGUID, store);
  });
  return Array.from(byGuid.values()).sort((left, right) =>
    (left.storeName || left.storeCode).localeCompare(
      right.storeName || right.storeCode,
      "en-AU",
    ),
  );
}

function mergeRoles(
  catalog: UserAccessRole[] = [],
  assigned: UserAccessRole[] = [],
) {
  const byGuid = new Map<string, UserAccessRole>();
  [...catalog, ...assigned].forEach((role) => {
    if (role.roleGUID.trim()) byGuid.set(role.roleGUID, role);
  });
  return Array.from(byGuid.values()).sort((left, right) =>
    left.roleName.localeCompare(right.roleName, "en-AU"),
  );
}

export default function AccessManagementScreen() {
  const router = useRouter();
  const navigation = useNavigation();
  const { width: windowWidth } = useWindowDimensions();
  const { t, language } = useAppTranslation(["userManagement", "common"]);
  const params = useLocalSearchParams<{
    userGuid?: string | string[];
    userName?: string | string[];
    username?: string | string[];
    targetStatus?: string | string[];
    roleNames?: string | string[];
  }>();
  const currentUser = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const userGuid = firstParam(params.userGuid)?.trim() ?? "";
  const username = firstParam(params.username)?.trim() ?? "";
  const userName =
    firstParam(params.userName)?.trim() ||
    username ||
    t("accessManagement.unknownUser");
  const parsedStatus = Number(firstParam(params.targetStatus));
  const targetStatus = Number.isFinite(parsedStatus) ? parsedStatus : 1;
  const routeRoleNames = useMemo(
    () => parseRoleNames(params.roleNames),
    [params.roleNames],
  );
  const isDeviceMode = sessionKind === "device";
  // 最小支持尺寸使用短标签，完整语义仍由分区标题和无障碍标签提供。
  const isCompactWidth = windowWidth <= 375;
  const managedStoreCodes = useMemo(
    () =>
      access.isAdmin
        ? null
        : (currentUser?.stores ?? [])
            .filter((store) => store.isPrimary === true)
            .map((store) => store.storeCode)
            .filter(Boolean),
    [access.isAdmin, currentUser?.stores],
  );
  const managedStoreCodeSet = useMemo(
    () => new Set(managedStoreCodes?.map((code) => code.toLowerCase()) ?? []),
    [managedStoreCodes],
  );
  const hasManageableStores =
    access.isAdmin || Boolean(managedStoreCodes?.length);
  const canManageStores = access.hasPermission(PERMISSIONS.Users.ManageStores);
  const canManageRoles = access.hasPermission(PERMISSIONS.Users.ManageRoles);
  const canManagePos = access.hasPermission(PERMISSIONS.Users.ManagePos);
  const [activeSection, setActiveSection] = useState<AccessSection>("stores");
  const [roleSearch, setRoleSearch] = useState("");
  const [terminalErrorKind, setTerminalErrorKind] = useState<Exclude<
    UserAccessErrorKind,
    "network"
  > | null>(null);
  const [allowRemove, setAllowRemove] = useState(false);
  const [authRedirectRequested, setAuthRedirectRequested] = useState(false);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [roleMutationForbidden, setRoleMutationForbidden] = useState(false);
  const [permissionMutationForbidden, setPermissionMutationForbidden] =
    useState(false);
  const pendingActionRef = useRef<NavigationAction | null>(null);
  const operationInFlightRef = useRef(false);

  const routeEligibility = useMemo(
    () =>
      getUserAccessEligibility({
        isDeviceMode,
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        canManageStores,
        canManageRoles,
        canManagePos,
        currentUserGuid: currentUser?.userGUID,
        targetUserGuid: userGuid,
        targetStatus,
        targetRoleNames: routeRoleNames,
        hasManageableStores,
      }),
    [
      access.isAdmin,
      access.isStoreManager,
      canManagePos,
      canManageRoles,
      canManageStores,
      currentUser?.userGUID,
      hasManageableStores,
      isDeviceMode,
      routeRoleNames,
      targetStatus,
      userGuid,
    ],
  );
  const queryEnabled = Boolean(
    userGuid &&
    isAuthenticated &&
    routeEligibility.canOpen &&
    !terminalErrorKind,
  );
  const storesEnabled =
    queryEnabled && routeEligibility.storesMode !== "hidden";
  // 受限操作者直接使用登录态中的可管理分店，避免 POS-only 账号被全分店目录接口拒绝。
  const storeCatalogEnabled = storesEnabled && access.isAdmin;
  const rolesEnabled = queryEnabled && routeEligibility.rolesMode !== "hidden";

  const storesQuery = useUserAccessStores(userGuid, storesEnabled);
  const storeCatalogQuery = useAccessStoreCatalog(storeCatalogEnabled);
  const rolesQuery = useUserAccessRoles(userGuid, rolesEnabled);
  const roleCatalogQuery = useAccessRoleCatalog(rolesEnabled);
  const targetRoleNames = useMemo(
    () => rolesQuery.data?.map((role) => role.roleName) ?? routeRoleNames,
    [rolesQuery.data, routeRoleNames],
  );
  const baseEligibility = useMemo(
    () =>
      getUserAccessEligibility({
        isDeviceMode,
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        canManageStores,
        canManageRoles,
        canManagePos,
        currentUserGuid: currentUser?.userGUID,
        targetUserGuid: userGuid,
        targetStatus,
        targetRoleNames,
        hasManageableStores,
      }),
    [
      access.isAdmin,
      access.isStoreManager,
      canManagePos,
      canManageRoles,
      canManageStores,
      currentUser?.userGUID,
      hasManageableStores,
      isDeviceMode,
      targetRoleNames,
      targetStatus,
      userGuid,
    ],
  );
  const roleAccessForbidden = Boolean(
    !access.isAdmin &&
      (roleMutationForbidden ||
        [rolesQuery.error, roleCatalogQuery.error].some(
          (error) => error && classifyUserAccessError(error) === "forbidden",
        )),
  );
  const permissionsEnabled = Boolean(
    queryEnabled &&
      !roleAccessForbidden &&
      baseEligibility.permissionsMode !== "hidden",
  );
  const permissionAccessQuery = useUserAccessPermissionAccess(
    userGuid,
    permissionsEnabled,
  );
  const permissionAccessForbidden = Boolean(
    !access.isAdmin &&
      (permissionMutationForbidden ||
        (permissionAccessQuery.error &&
          classifyUserAccessError(permissionAccessQuery.error) === "forbidden")),
  );
  const eligibility = useMemo(
    () =>
      applyUserAccessSectionRestrictions(baseEligibility, {
        rolesForbidden: roleAccessForbidden,
        permissionsForbidden: permissionAccessForbidden,
      }),
    [baseEligibility, permissionAccessForbidden, roleAccessForbidden],
  );
  const assignStoresMutation = useAssignUserAccessStores();
  const assignRolesMutation = useAssignUserAccessRoles();
  const assignPermissionsMutation = useAssignUserDirectPermissions();
  const [storeAssignments, setStoreAssignments] = useState<
    UserAccessStoreAssignment[]
  >([]);
  const [baselineStoreAssignments, setBaselineStoreAssignments] = useState<
    UserAccessStoreAssignment[]
  >([]);
  const [selectedRoleGuids, setSelectedRoleGuids] = useState<string[]>([]);
  const [baselineRoleGuids, setBaselineRoleGuids] = useState<string[]>([]);
  const [permissionDraft, setPermissionDraft] =
    useState<DirectPermissionDraft | null>(null);
  const storeAppliedAtRef = useRef(0);
  const roleAppliedAtRef = useRef(0);
  const permissionAppliedAtRef = useRef(0);
  const initializedScopeRef = useRef<string | null>(null);
  const busy =
    assignStoresMutation.isPending ||
    assignRolesMutation.isPending ||
    assignPermissionsMutation.isPending;
  const storesDirty = !areAccessCodeSetsEqual(
    encodeStoreAssignments(storeAssignments),
    encodeStoreAssignments(baselineStoreAssignments),
  );
  const rolesDirty = !areAccessCodeSetsEqual(
    selectedRoleGuids,
    baselineRoleGuids,
  );
  const permissionsDirty = Boolean(
    permissionDraft &&
    !areAccessCodeSetsEqual(
      permissionDraft.selectedCodes,
      permissionDraft.baselineCodes,
    ),
  );
  const dirty = storesDirty || rolesDirty || permissionsDirty;

  useEffect(() => {
    const activeMode =
      activeSection === "stores"
        ? eligibility.storesMode
        : activeSection === "roles"
          ? eligibility.rolesMode
          : eligibility.permissionsMode;
    if (activeMode !== "hidden") return;

    if (eligibility.storesMode !== "hidden") setActiveSection("stores");
    else if (eligibility.permissionsMode !== "hidden")
      setActiveSection("permissions");
    else if (eligibility.rolesMode !== "hidden") setActiveSection("roles");
  }, [
    activeSection,
    eligibility.permissionsMode,
    eligibility.rolesMode,
    eligibility.storesMode,
  ]);

  useEffect(() => {
    if (initializedScopeRef.current === userGuid) return;
    initializedScopeRef.current = userGuid;
    storeAppliedAtRef.current = 0;
    roleAppliedAtRef.current = 0;
    permissionAppliedAtRef.current = 0;
    setStoreAssignments([]);
    setBaselineStoreAssignments([]);
    setSelectedRoleGuids([]);
    setBaselineRoleGuids([]);
    setPermissionDraft(null);
    setTerminalErrorKind(null);
    setRoleMutationForbidden(false);
    setPermissionMutationForbidden(false);
    setAllowRemove(false);
  }, [userGuid]);

  useEffect(() => {
    if (!storesQuery.data) return;
    if (
      storeAppliedAtRef.current > 0 &&
      (storesDirty ||
        busy ||
        storesQuery.dataUpdatedAt <= storeAppliedAtRef.current)
    )
      return;
    const assignments = buildUserAccessStoreAssignments(storesQuery.data);
    setStoreAssignments(assignments);
    setBaselineStoreAssignments(assignments);
    storeAppliedAtRef.current = storesQuery.dataUpdatedAt;
  }, [busy, storesDirty, storesQuery.data, storesQuery.dataUpdatedAt]);

  useEffect(() => {
    if (!rolesQuery.data) return;
    if (
      roleAppliedAtRef.current > 0 &&
      (rolesDirty ||
        busy ||
        rolesQuery.dataUpdatedAt <= roleAppliedAtRef.current)
    )
      return;
    const roleGuids = rolesQuery.data
      .filter((role) => !isStoreManagerRoleName(role.roleName))
      .map((role) => role.roleGUID);
    setSelectedRoleGuids(roleGuids);
    setBaselineRoleGuids(roleGuids);
    roleAppliedAtRef.current = rolesQuery.dataUpdatedAt;
  }, [busy, rolesDirty, rolesQuery.data, rolesQuery.dataUpdatedAt]);

  useEffect(() => {
    if (!permissionAccessQuery.data) return;
    if (
      permissionAppliedAtRef.current > 0 &&
      (permissionsDirty ||
        busy ||
        permissionAccessQuery.dataUpdatedAt <= permissionAppliedAtRef.current)
    )
      return;
    setPermissionDraft(
      buildDirectPermissionDraft(permissionAccessQuery.data.state),
    );
    permissionAppliedAtRef.current = permissionAccessQuery.dataUpdatedAt;
  }, [
    busy,
    permissionAccessQuery.data,
    permissionAccessQuery.dataUpdatedAt,
    permissionsDirty,
  ]);

  const queryErrors = [
    storesEnabled ? storesQuery.error : null,
    storeCatalogEnabled ? storeCatalogQuery.error : null,
    rolesEnabled && !roleAccessForbidden ? rolesQuery.error : null,
    rolesEnabled && !roleAccessForbidden ? roleCatalogQuery.error : null,
    permissionsEnabled && !permissionAccessForbidden
      ? permissionAccessQuery.error
      : null,
  ].filter(Boolean);
  const firstTerminalQueryError = queryErrors.find(
    (error) => classifyUserAccessError(error) !== "network",
  );

  useEffect(() => {
    if (!firstTerminalQueryError) return;
    const errorKind = classifyUserAccessError(firstTerminalQueryError);
    if (errorKind === "network") return;
    setTerminalErrorKind(errorKind);
    if (errorKind === "unauthorized") {
      // 401 必须优先退出失效编辑上下文，不能被脏草稿离页保护卡住。
      setAuthRedirectRequested(true);
      setAllowRemove(true);
    }
  }, [firstTerminalQueryError]);

  useEffect(() => {
    if (!isAuthenticated && !isDeviceMode) {
      setAuthRedirectRequested(true);
      setAllowRemove(true);
    }
  }, [isAuthenticated, isDeviceMode]);

  usePreventRemove(
    isAuthenticated &&
      terminalErrorKind !== "unauthorized" &&
      (dirty || busy) &&
      !allowRemove,
    ({ data }) => {
      if (
        !useAuthStore.getState().isAuthenticated ||
        terminalErrorKind === "unauthorized"
      ) {
        pendingActionRef.current = data.action;
        setAuthRedirectRequested(true);
        setAllowRemove(true);
        return;
      }
      if (busy) {
        Alert.alert(
          t("accessManagement.busy.title"),
          t("accessManagement.busy.description"),
          [{ text: t("common:actions.confirm") }],
        );
        return;
      }
      Alert.alert(
        t("accessManagement.unsaved.title"),
        t("accessManagement.unsaved.description"),
        [
          { text: t("common:actions.cancel"), style: "cancel" },
          {
            text: t("accessManagement.unsaved.discard"),
            style: "destructive",
            onPress: () => {
              pendingActionRef.current = data.action;
              setAllowRemove(true);
            },
          },
        ],
      );
    },
  );

  useEffect(() => {
    if (!allowRemove) return;
    if (pendingActionRef.current) {
      const action = pendingActionRef.current;
      pendingActionRef.current = null;
      navigation.dispatch(action);
      return;
    }
    if (authRedirectRequested) router.replace("/(auth)/login");
  }, [allowRemove, authRedirectRequested, navigation, router]);

  const handleBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/users");
  }, [router]);

  const handleOperationError = useCallback(
    (
      error: unknown,
      fallbackKey: string,
      section?: Extract<AccessSection, "roles" | "permissions">,
    ) => {
      const errorKind = classifyUserAccessError(error);
      if (errorKind === "network") {
        setSnackbarMessage(t(fallbackKey));
        return;
      }
      if (errorKind === "forbidden" && !access.isAdmin && section) {
        // 局部授权失效只关闭对应分段，清除已不可提交的草稿并保留分店维护能力。
        if (section === "roles") {
          setRoleMutationForbidden(true);
          setSelectedRoleGuids([...baselineRoleGuids]);
          setPermissionDraft((current) =>
            current
              ? { ...current, selectedCodes: [...current.baselineCodes] }
              : current,
          );
        } else {
          setPermissionMutationForbidden(true);
          setPermissionDraft((current) =>
            current
              ? { ...current, selectedCodes: [...current.baselineCodes] }
              : current,
          );
        }
        setSnackbarMessage(t(fallbackKey));
        return;
      }
      setTerminalErrorKind(errorKind);
      if (errorKind === "unauthorized") {
        setAuthRedirectRequested(true);
        setAllowRemove(true);
      }
    },
    [access.isAdmin, baselineRoleGuids, t],
  );

  const actorManagedStoreOptions = useMemo<AccessStoreOption[]>(
    () =>
      access.isAdmin
        ? []
        : (currentUser?.stores ?? []).flatMap((store) => {
            const storeGUID = store.storeGUID?.trim();
            if (!store.isPrimary || !storeGUID) return [];
            return [
              {
                storeGUID,
                storeCode: store.storeCode,
                storeName: store.storeName,
              },
            ];
          }),
    [access.isAdmin, currentUser?.stores],
  );
  const storeOptions = useMemo(() => {
    const stores = mergeStores(
      access.isAdmin ? storeCatalogQuery.data : actorManagedStoreOptions,
      storesQuery.data,
    );
    if (access.isAdmin) return stores;
    // 非管理员只看到自己的管理范围，避免用全分店目录制造越权草稿。
    return stores.filter((store) =>
      managedStoreCodeSet.has(store.storeCode.toLowerCase()),
    );
  }, [
    access.isAdmin,
    actorManagedStoreOptions,
    managedStoreCodeSet,
    storeCatalogQuery.data,
    storesQuery.data,
  ]);
  const roleOptions = useMemo(
    () =>
      limitUserAccessRolesForActor(
        mergeRoles(roleCatalogQuery.data, rolesQuery.data),
        access.isAdmin,
      ),
    [access.isAdmin, roleCatalogQuery.data, rolesQuery.data],
  );
  const filteredRoleOptions = useMemo(
    () => filterUserAccessRoles(roleOptions, roleSearch),
    [roleOptions, roleSearch],
  );
  const hasManagedStoreAssignment = storeAssignments.some(
    (item) => item.isPrimary,
  );
  const inheritedSourceMap = useMemo(() => {
    const result = new Map<string, string[]>();
    permissionAccessQuery.data?.state.inheritedSources.forEach((source) => {
      source.permissionCodes.forEach((code) => {
        result.set(code, [
          ...(result.get(code) ?? []),
          localizeAccessRoleName(source.roleName, language),
        ]);
      });
    });
    return result;
  }, [language, permissionAccessQuery.data?.state.inheritedSources]);

  const handleSaveStores = useCallback(async () => {
    if (!storesDirty || busy || operationInFlightRef.current) return;
    operationInFlightRef.current = true;
    try {
      await assignStoresMutation.mutateAsync({
        userGuid,
        assignments: storeAssignments,
      });
      setBaselineStoreAssignments([...storeAssignments]);
      setSnackbarMessage(t("accessManagement.messages.storesSaved"));
      void Promise.allSettled([
        rolesQuery.refetch(),
        permissionAccessQuery.refetch(),
      ]);
    } catch (error) {
      handleOperationError(error, "accessManagement.messages.storesSaveFailed");
    } finally {
      operationInFlightRef.current = false;
    }
  }, [
    assignStoresMutation,
    busy,
    handleOperationError,
    permissionAccessQuery,
    rolesQuery,
    storeAssignments,
    storesDirty,
    t,
    userGuid,
  ]);

  const handleSaveRoles = useCallback(async () => {
    if (!rolesDirty || busy || operationInFlightRef.current) return;
    operationInFlightRef.current = true;
    try {
      await assignRolesMutation.mutateAsync({
        userGuid,
        roleGuids: selectedRoleGuids,
        roleCatalog: roleOptions,
      });
      setBaselineRoleGuids([...selectedRoleGuids]);
      setSnackbarMessage(t("accessManagement.messages.rolesSaved"));
    } catch (error) {
      handleOperationError(
        error,
        "accessManagement.messages.rolesSaveFailed",
        "roles",
      );
    } finally {
      operationInFlightRef.current = false;
    }
  }, [
    assignRolesMutation,
    busy,
    handleOperationError,
    roleOptions,
    rolesDirty,
    selectedRoleGuids,
    t,
    userGuid,
  ]);

  const handleSavePermissions = useCallback(async () => {
    if (
      !permissionDraft ||
      !permissionsDirty ||
      busy ||
      operationInFlightRef.current
    )
      return;
    operationInFlightRef.current = true;
    try {
      await assignPermissionsMutation.mutateAsync({
        userGuid,
        permissions: permissionDraft.selectedCodes,
      });
      setPermissionDraft((current) =>
        current
          ? { ...current, baselineCodes: [...current.selectedCodes] }
          : current,
      );
      setSnackbarMessage(t("accessManagement.messages.permissionsSaved"));
    } catch (error) {
      handleOperationError(
        error,
        "accessManagement.messages.permissionsSaveFailed",
        "permissions",
      );
    } finally {
      operationInFlightRef.current = false;
    }
  }, [
    assignPermissionsMutation,
    busy,
    handleOperationError,
    permissionDraft,
    permissionsDirty,
    t,
    userGuid,
  ]);

  const openPosPermissions = useCallback(
    (store: AccessStoreOption) => {
      const storeGuid = store.storeGUID.trim();
      if (!storeGuid) {
        setSnackbarMessage(t("accessManagement.stores.missingStoreGuid"));
        return;
      }
      router.push({
        pathname: "/users/[userGuid]/pos-terminal-permissions",
        params: {
          userGuid,
          storeGuid,
          // storeCode 供「复制同事」按分店列出同事，roleNames 用于顶部「分店 · 角色」展示。
          storeCode: store.storeCode,
          userName,
          storeName: store.storeName || store.storeCode,
          roleNames: routeRoleNames.join("|"),
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [routeRoleNames, router, t, userGuid, userName],
  );

  const retryQueries = useCallback(() => {
    const retries: Promise<unknown>[] = [];
    if (storesEnabled) retries.push(storesQuery.refetch());
    if (storeCatalogEnabled) retries.push(storeCatalogQuery.refetch());
    if (rolesEnabled && !roleAccessForbidden)
      retries.push(rolesQuery.refetch(), roleCatalogQuery.refetch());
    if (permissionsEnabled && !permissionAccessForbidden) {
      retries.push(permissionAccessQuery.refetch());
    }
    void Promise.allSettled(retries);
  }, [
    permissionAccessQuery,
    permissionAccessForbidden,
    permissionsEnabled,
    roleCatalogQuery,
    roleAccessForbidden,
    rolesEnabled,
    rolesQuery,
    storeCatalogQuery,
    storeCatalogEnabled,
    storesEnabled,
    storesQuery,
  ]);

  const requiredQueries = [
    ...(storesEnabled ? [storesQuery] : []),
    ...(storeCatalogEnabled ? [storeCatalogQuery] : []),
    ...(rolesEnabled && !roleAccessForbidden
      ? [rolesQuery, roleCatalogQuery]
      : []),
    ...(permissionsEnabled && !permissionAccessForbidden
      ? [permissionAccessQuery]
      : []),
  ];
  const initialLoading = requiredQueries.some(
    (query) =>
      query.data === undefined && (query.isLoading || query.isFetching),
  );
  const initialError = requiredQueries.find(
    (query) => query.data === undefined && query.error,
  )?.error;

  const renderErrorState = (errorKind: UserAccessErrorKind) => (
    <EmptyState
      title={t(`accessManagement.errors.${errorKind}Title`)}
      description={t(`accessManagement.errors.${errorKind}Description`)}
      primaryAction={
        errorKind === "network"
          ? {
              label: t("common:actions.retry"),
              icon: "refresh",
              onPress: retryQueries,
            }
          : errorKind === "forbidden" || errorKind === "notFound"
            ? {
                label: t("accessManagement.actions.back"),
                icon: "arrow-left",
                onPress: handleBack,
              }
            : undefined
      }
    />
  );

  const renderEligibilityError = () => {
    const key =
      eligibility.reason === "deviceMode"
        ? "device"
        : eligibility.reason === "self"
          ? "self"
          : eligibility.reason === "privilegedTarget"
            ? "privileged"
            : "noAccess";
    return (
      <EmptyState
        title={t(`accessManagement.errors.${key}Title`)}
        description={t(`accessManagement.errors.${key}Description`)}
        primaryAction={{
          label: t("accessManagement.actions.back"),
          icon: "arrow-left",
          onPress: handleBack,
        }}
      />
    );
  };

  const renderStoresSection = () => {
    if (eligibility.storesMode === "hidden") {
      return (
        <Text style={styles.sectionNotice}>
          {t("accessManagement.sections.noAccess")}
        </Text>
      );
    }
    if (!storeOptions.length) {
      return (
        <EmptyState
          title={t("accessManagement.stores.emptyTitle")}
          description={t("accessManagement.stores.emptyDescription")}
        />
      );
    }
    return (
      <View style={styles.sectionContent}>
        {!eligibility.canGrantStoreManagement &&
        eligibility.storesMode === "editable" ? (
          <Text variant="bodySmall" style={styles.helperText}>
            {t("accessManagement.stores.adminOnlyManage")}
          </Text>
        ) : null}
        {storeOptions.map((store) => {
          const state = getUserAccessStoreState(
            storeAssignments,
            store.storeGUID,
          );
          const editable = eligibility.storesMode === "editable" && !busy;
          return (
            <View key={store.storeGUID} style={styles.itemRow}>
              <View style={styles.itemHeader}>
                <View style={styles.itemText}>
                  <Text variant="titleSmall">
                    {store.storeName || store.storeCode}
                  </Text>
                  <Text variant="bodySmall" style={styles.secondaryText}>
                    {store.storeCode}
                  </Text>
                </View>
                {eligibility.canManagePos && state !== "unassigned" ? (
                  <Button
                    compact
                    mode="text"
                    icon="cash-register"
                    disabled={busy || !store.storeGUID.trim()}
                    accessibilityLabel={`${t("accessManagement.actions.openPos")} ${store.storeName}`}
                    onPress={() => openPosPermissions(store)}
                  >
                    {t("accessManagement.actions.openPos")}
                  </Button>
                ) : null}
              </View>
              <SegmentedButtons
                value={state}
                onValueChange={(value) =>
                  setStoreAssignments((current) =>
                    setUserAccessStoreState(
                      current,
                      store.storeGUID,
                      value as UserAccessStoreState,
                    ),
                  )
                }
                buttons={[
                  {
                    value: "unassigned",
                    label: t(
                      isCompactWidth
                        ? "accessManagement.stores.unassignedCompact"
                        : "accessManagement.stores.unassigned",
                    ),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    disabled: !editable,
                    accessibilityLabel: `${store.storeName} ${t("accessManagement.stores.unassigned")}`,
                  },
                  {
                    value: "view",
                    label: t("accessManagement.stores.view"),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    disabled: !editable,
                    accessibilityLabel: `${store.storeName} ${t("accessManagement.stores.view")}`,
                  },
                  {
                    value: "manage",
                    label: t("accessManagement.stores.manage"),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    disabled: !editable || !eligibility.canGrantStoreManagement,
                    accessibilityLabel: `${store.storeName} ${t("accessManagement.stores.manage")}`,
                  },
                ]}
                style={styles.storeStateButtons}
              />
            </View>
          );
        })}
        {eligibility.storesMode === "editable" ? (
          <Button
            mode="contained"
            icon="content-save-outline"
            loading={assignStoresMutation.isPending}
            disabled={!storesDirty || busy}
            onPress={handleSaveStores}
            style={styles.saveButton}
          >
            {t("accessManagement.actions.saveStores")}
          </Button>
        ) : null}
      </View>
    );
  };

  const renderRolesSection = () => {
    if (eligibility.rolesMode === "hidden") {
      return (
        <Text style={styles.sectionNotice}>
          {t("accessManagement.sections.noAccess")}
        </Text>
      );
    }
    if (!roleOptions.length) {
      return (
        <EmptyState
          title={t("accessManagement.roles.emptyTitle")}
          description={t("accessManagement.roles.emptyDescription")}
        />
      );
    }
    return (
      <View style={styles.sectionContent}>
        {!access.isAdmin ? (
          <Text variant="bodySmall" style={styles.helperText}>
            {t("accessManagement.roles.managerLimit")}
          </Text>
        ) : null}
        <Searchbar
          value={roleSearch}
          onChangeText={setRoleSearch}
          placeholder={t("accessManagement.roles.searchPlaceholder")}
          accessibilityLabel={t("accessManagement.roles.searchPlaceholder")}
          style={styles.roleSearch}
        />
        {!filteredRoleOptions.length ? (
          <Text style={styles.sectionNotice}>
            {t("accessManagement.roles.noSearchResults")}
          </Text>
        ) : null}
        {filteredRoleOptions.map((role) => {
          const localizedRoleName = localizeAccessRoleName(
            role.roleName,
            language,
          );
          const localizedRoleDescription = localizeAccessRoleDescription(
            role.roleName,
            role.description,
            language,
          );
          const selection = getAccessRoleSelectionState({
            role,
            selectedRoleGuids,
            hasManagedStoreAssignment,
          });
          const adminOnly =
            !access.isAdmin && hasPrivilegedAccessRole([role.roleName]);
          const disabled =
            busy ||
            selection.locked ||
            adminOnly ||
            eligibility.rolesMode !== "editable";
          const toggleRole = () => {
            if (disabled) return;
            setSelectedRoleGuids((current) =>
              selection.selected
                ? current.filter((roleGuid) => roleGuid !== role.roleGUID)
                : [...current, role.roleGUID],
            );
          };
          return (
            <TouchableRipple
              key={role.roleGUID}
              disabled={disabled}
              onPress={toggleRole}
              accessibilityRole="checkbox"
              accessibilityLabel={localizedRoleName}
              accessibilityHint={localizedRoleDescription}
              accessibilityState={{ checked: selection.selected, disabled }}
            >
              <View style={styles.selectionRow}>
                <View style={styles.itemText}>
                  <View style={styles.labelRow}>
                    <Text variant="bodyLarge">{localizedRoleName}</Text>
                    {selection.derived ? (
                      <Chip compact icon="source-branch">
                        {t("accessManagement.roles.derived")}
                      </Chip>
                    ) : adminOnly ? (
                      <Chip compact icon="shield-lock-outline">
                        {t("accessManagement.roles.adminOnly")}
                      </Chip>
                    ) : null}
                  </View>
                  <Text variant="bodySmall" style={styles.secondaryText}>
                    {localizedRoleDescription}
                  </Text>
                </View>
                <Checkbox
                  status={selection.selected ? "checked" : "unchecked"}
                  disabled={disabled}
                />
              </View>
            </TouchableRipple>
          );
        })}
        <Button
          mode="contained"
          icon="content-save-outline"
          loading={assignRolesMutation.isPending}
          disabled={!rolesDirty || busy}
          onPress={handleSaveRoles}
          style={styles.saveButton}
        >
          {t("accessManagement.actions.saveRoles")}
        </Button>
      </View>
    );
  };

  const renderPermissionsSection = () => {
    if (eligibility.permissionsMode === "hidden") {
      return (
        <Text style={styles.sectionNotice}>
          {t("accessManagement.sections.noAccess")}
        </Text>
      );
    }
    const categories = permissionAccessQuery.data?.categories ?? [];
    if (!permissionDraft || !categories.length) {
      return (
        <EmptyState
          title={t("accessManagement.permissions.emptyTitle")}
          description={t("accessManagement.permissions.emptyDescription")}
        />
      );
    }
    const implicitAll =
      permissionAccessQuery.data?.state.implicitAllPermissions === true;
    const effectiveCount = implicitAll
      ? categories.reduce(
          (count, category) => count + category.permissions.length,
          0,
        )
      : new Set([
          ...permissionDraft.inheritedPermissionCodes,
          ...permissionDraft.selectedCodes,
        ]).size;
    return (
      <View style={styles.sectionContent}>
        {!access.isAdmin ? (
          <Text variant="bodySmall" style={styles.helperText}>
            {t("accessManagement.permissions.managerLimit")}
          </Text>
        ) : null}
        <Text variant="bodySmall" style={styles.helperText}>
          {t("accessManagement.permissions.summary", {
            effective: effectiveCount,
            inherited: permissionDraft.inheritedPermissionCodes.length,
            direct: permissionDraft.selectedCodes.length,
          })}
        </Text>
        {categories.map((category) => {
          const localizedCategory = localizeAccessPermissionCategory(
            category.category || category.displayName,
            language,
          );
          return (
            <View key={category.category} style={styles.permissionGroup}>
              <Text variant="titleSmall" accessibilityRole="header">
                {localizedCategory}
              </Text>
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("accessManagement.permissions.categoryDescription", {
                  category: localizedCategory,
                })}
              </Text>
              {category.permissions.map((permission) => {
                const localizedPermission = localizeAccessPermission(
                  permission,
                  language,
                );
                const selection = getAccessPermissionSelectionState(
                  permissionDraft,
                  permission.name,
                );
                const sources = inheritedSourceMap.get(permission.name) ?? [];
                const isPosAccountDefault =
                  permission.name.startsWith("Permissions.PosTerminal.") ||
                  permission.name.startsWith("PosTerminal.");
                const disabled = busy || implicitAll || selection.locked;
                return (
                  <View key={permission.name} style={styles.permissionRow}>
                    <View style={styles.itemText}>
                      <View style={styles.labelRow}>
                        <Text variant="bodyLarge">
                          {localizedPermission.name}
                        </Text>
                        {implicitAll ? (
                          <Chip compact>
                            {t("accessManagement.permissions.implicit")}
                          </Chip>
                        ) : selection.inherited ? (
                          <Chip compact>
                            {t("accessManagement.permissions.inherited")}
                          </Chip>
                        ) : selection.direct ? (
                          <Chip compact>
                            {t("accessManagement.permissions.direct")}
                          </Chip>
                        ) : null}
                        {isPosAccountDefault ? (
                          <Chip compact icon="account-outline">
                            {t("accessManagement.permissions.accountDefault")}
                          </Chip>
                        ) : null}
                      </View>
                      {localizedPermission.description ? (
                        <Text variant="bodySmall" style={styles.secondaryText}>
                          {localizedPermission.description}
                        </Text>
                      ) : null}
                      {sources.length ? (
                        <Text variant="bodySmall" style={styles.secondaryText}>
                          {t("accessManagement.permissions.source", {
                            roles: sources.join(", "),
                          })}
                        </Text>
                      ) : null}
                    </View>
                    <Switch
                      value={implicitAll || selection.checked}
                      disabled={disabled}
                      accessibilityLabel={localizedPermission.name}
                      accessibilityHint={localizedPermission.description}
                      onValueChange={(checked) =>
                        setPermissionDraft((current) =>
                          current
                            ? toggleDirectPermission(
                                current,
                                permission.name,
                                checked,
                              )
                            : current,
                        )
                      }
                    />
                  </View>
                );
              })}
            </View>
          );
        })}
        {!permissionAccessQuery.data?.state.implicitAllPermissions ? (
          <Button
            mode="contained"
            icon="content-save-outline"
            loading={assignPermissionsMutation.isPending}
            disabled={!permissionsDirty || busy}
            onPress={handleSavePermissions}
            style={styles.saveButton}
          >
            {t("accessManagement.actions.savePermissions")}
          </Button>
        ) : null}
      </View>
    );
  };

  let content;
  if (!userGuid) {
    content = (
      <EmptyState
        title={t("accessManagement.errors.invalidTitle")}
        description={t("accessManagement.errors.invalidDescription")}
        primaryAction={{
          label: t("accessManagement.actions.back"),
          icon: "arrow-left",
          onPress: handleBack,
        }}
      />
    );
  } else if (!eligibility.canOpen) {
    content = renderEligibilityError();
  } else if (terminalErrorKind) {
    content = renderErrorState(terminalErrorKind);
  } else if (initialLoading) {
    content = (
      <View style={styles.centerState}>
        <ActivityIndicator />
        <Text style={styles.secondaryText}>
          {t("accessManagement.loading")}
        </Text>
      </View>
    );
  } else if (initialError) {
    content = renderErrorState(classifyUserAccessError(initialError));
  } else {
    const sectionTitle = t(`accessManagement.sections.${activeSection}`);
    const descriptionKey =
      activeSection === "stores"
        ? "storesDescription"
        : activeSection === "roles"
          ? "rolesDescription"
          : "permissionsDescription";
    content = (
      <ScrollView contentContainerStyle={styles.scrollContent}>
        <Card mode="outlined" style={styles.identityCard}>
          <Card.Content style={styles.identityContent}>
            <Avatar.Text
              size={44}
              label={getInitials(userName)}
              style={styles.avatar}
            />
            <View style={styles.identityText}>
              <Text variant="titleMedium" accessibilityRole="header">
                {userName}
              </Text>
              {username ? (
                <Text style={styles.secondaryText}>{username}</Text>
              ) : null}
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("accessManagement.identity.userId", { value: userGuid })}
              </Text>
            </View>
            <Chip compact icon="account-key-outline">
              {t("accessManagement.identity.roleCount", {
                count: targetRoleNames.length,
              })}
            </Chip>
          </Card.Content>
        </Card>

        <SegmentedButtons
          value={activeSection}
          onValueChange={(value) => setActiveSection(value as AccessSection)}
          buttons={[
            ...(eligibility.storesMode !== "hidden"
              ? [
                  {
                    value: "stores",
                    icon: isCompactWidth ? undefined : "store-outline",
                    label: t("accessManagement.sections.storesTab"),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    accessibilityLabel: t("accessManagement.sections.stores"),
                  },
                ]
              : []),
            ...(eligibility.rolesMode !== "hidden"
              ? [
                  {
                    value: "roles",
                    icon: isCompactWidth ? undefined : "account-group-outline",
                    label: t("accessManagement.sections.rolesTab"),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    accessibilityLabel: t("accessManagement.sections.roles"),
                  },
                ]
              : []),
            ...(eligibility.permissionsMode !== "hidden"
              ? [
                  {
                    value: "permissions",
                    icon: isCompactWidth ? undefined : "shield-key-outline",
                    label: t("accessManagement.sections.permissionsTab"),
                    labelStyle: isCompactWidth
                      ? styles.compactSegmentLabel
                      : undefined,
                    accessibilityLabel: t(
                      "accessManagement.sections.permissions",
                    ),
                  },
                ]
              : []),
          ]}
        />

        <Card mode="outlined" style={styles.sectionCard}>
          <Card.Content style={styles.cardContent}>
            <Text variant="titleMedium" accessibilityRole="header">
              {sectionTitle}
            </Text>
            <Text variant="bodySmall" style={styles.secondaryText}>
              {t(`accessManagement.sections.${descriptionKey}`)}
            </Text>
            {activeSection === "stores"
              ? renderStoresSection()
              : activeSection === "roles"
                ? renderRolesSection()
                : renderPermissionsSection()}
          </Card.Content>
        </Card>
      </ScrollView>
    );
  }

  return (
    <SafeAreaView
      style={styles.safeArea}
      edges={["top", "left", "right", "bottom"]}
    >
      <Stack.Screen options={{ headerBackButtonMenuEnabled: false }} />
      <Surface style={styles.header} elevation={1}>
        <IconButton
          icon="arrow-left"
          accessibilityLabel={t("accessManagement.actions.back")}
          disabled={busy}
          onPress={handleBack}
        />
        <Text variant="titleLarge" numberOfLines={1} style={styles.headerTitle}>
          {t("accessManagement.title")}
        </Text>
      </Surface>
      <View style={styles.body}>{content}</View>
      <Snackbar
        visible={Boolean(snackbarMessage)}
        duration={2800}
        onDismiss={() => setSnackbarMessage("")}
      >
        {snackbarMessage}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: BUSINESS_UI.screen,
  body: { flex: 1 },
  header: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    paddingRight: HB_SPACING.sm,
  },
  headerTitle: { ...BUSINESS_UI.sectionTitle, flex: 1 },
  centerState: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: 12,
    padding: 24,
  },
  scrollContent: { ...BUSINESS_UI.content, paddingBottom: HB_SPACING.xl },
  identityCard: BUSINESS_UI.section,
  identityContent: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  avatar: { backgroundColor: HB_COLORS.action },
  identityText: { flex: 1, gap: 2 },
  sectionCard: BUSINESS_UI.section,
  cardContent: { ...BUSINESS_UI.sectionContent, gap: HB_SPACING.xs },
  sectionContent: { gap: HB_SPACING.xs, paddingTop: HB_SPACING.xxs },
  roleSearch: { backgroundColor: HB_COLORS.surfaceMuted },
  sectionNotice: { color: HB_COLORS.textSecondary, paddingVertical: HB_SPACING.lg, textAlign: "center" },
  helperText: {
    color: HB_COLORS.textSecondary,
    backgroundColor: HB_COLORS.surfaceMuted,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
  },
  itemRow: {
    gap: HB_SPACING.xs,
    paddingVertical: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  itemHeader: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  itemText: { flex: 1, gap: 2 },
  labelRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  selectionRow: {
    minHeight: 58,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  storeStateButtons: { width: "100%" },
  compactSegmentLabel: { fontSize: 13 },
  permissionGroup: { gap: HB_SPACING.xs, paddingTop: HB_SPACING.xs },
  permissionRow: {
    minHeight: 58,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  saveButton: { ...BUSINESS_UI.button, alignSelf: "stretch", marginTop: HB_SPACING.xxs },
  secondaryText: { color: HB_COLORS.textSecondary },
});
