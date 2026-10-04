import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, View } from "react-native";
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
  IconButton,
  Menu,
  Searchbar,
  Snackbar,
  Surface,
  Text,
} from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { PosPermissionChangesSheet } from "@/modules/users/pos-permission-changes-sheet";
import { PosPermissionCopySheet } from "@/modules/users/pos-permission-copy-sheet";
import { PosPermissionGroupCard } from "@/modules/users/pos-permission-group-card";
import { POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import {
  useRestoreStoreUserPosTerminalPermissions,
  useStoreUserPosTerminalPermissions,
  useUpdateStoreUserPosTerminalPermissions,
} from "@/modules/users/pos-terminal-permissions-hooks";
import {
  arePermissionCodeSetsEqual,
  buildGrantedPosPermissionCodes,
  buildPosPermissionDraft,
  classifyPosPermissionError,
  setPosPermissionGroupSelection,
  shouldApplyPosPermissionResponse,
  shouldBypassPosPermissionRemovalGuard,
  shouldPreventPosPermissionRemoval,
  togglePosPermissionCode,
} from "@/modules/users/pos-terminal-permissions";
import type { PosPermissionErrorKind } from "@/modules/users/pos-terminal-permissions";
import {
  POS_PERMISSION_PRESET_ORDER,
  buildPosPermissionDisplayGroups,
  buildPosPermissionPresetCodes,
  computePosPermissionChanges,
  filterPosPermissionGroups,
  getDefaultExpandedPosPermissionGroups,
  getMatchingPosPermissionPresets,
  getPosPermissionInitials,
  getPosPermissionStats,
  isPosPermissionFilterActive,
  type PosPermissionDisplayGroup,
  type PosPermissionDisplayGroupKey,
  type PosPermissionListFilter,
  type PosPermissionPresetKey,
} from "@/modules/users/pos-terminal-permission-presentation";
import type { StoreUserPosTerminalPermissions } from "@/modules/users/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

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

const LIST_FILTERS: PosPermissionListFilter[] = ["all", "enabled", "highRisk", "changed"];

export default function PosTerminalPermissionsScreen() {
  const router = useRouter();
  const navigation = useNavigation();
  const { t } = useAppTranslation(["userManagement", "common"]);
  const params = useLocalSearchParams<{
    userGuid?: string | string[];
    storeGuid?: string | string[];
    storeCode?: string | string[];
    userName?: string | string[];
    storeName?: string | string[];
    roleNames?: string | string[];
  }>();
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const userGuid = firstParam(params.userGuid)?.trim() ?? "";
  const storeGuid = firstParam(params.storeGuid)?.trim() ?? "";
  const storeCode = firstParam(params.storeCode)?.trim() ?? "";
  const userName = firstParam(params.userName)?.trim() || t("posPermissions.unknownUser");
  const storeName = firstParam(params.storeName)?.trim() || t("posPermissions.unknownStore");
  const roleNames = useMemo(() => parseRoleNames(params.roleNames), [params.roleNames]);
  const hasRequiredParams = Boolean(userGuid && storeGuid);
  const actorUserGuid = currentUser?.userGUID?.trim() ?? "";
  const isCurrentUser = Boolean(
    userGuid &&
      currentUser?.userGUID?.trim().toLowerCase() === userGuid.toLowerCase()
  );
  const canManage = access.hasPermission(
    PERMISSIONS.Users.ManagePosTerminalPermissions
  );
  const queryEnabled = hasRequiredParams && canManage && !isCurrentUser;
  const scopeKey = `${storeGuid}:${userGuid}`;

  const permissionsQuery = useStoreUserPosTerminalPermissions(
    userGuid,
    storeGuid,
    queryEnabled
  );
  const updateMutation = useUpdateStoreUserPosTerminalPermissions();
  const restoreMutation = useRestoreStoreUserPosTerminalPermissions();
  const [selectedCodes, setSelectedCodes] = useState<string[]>([]);
  const [baselineCodes, setBaselineCodes] = useState<string[]>([]);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [allowRemove, setAllowRemove] = useState(false);
  const [authRedirectRequested, setAuthRedirectRequested] = useState(false);
  const [terminalErrorKind, setTerminalErrorKind] = useState<
    Exclude<PosPermissionErrorKind, "network"> | null
  >(null);
  const [menuVisible, setMenuVisible] = useState(false);
  const [changesVisible, setChangesVisible] = useState(false);
  const [copyVisible, setCopyVisible] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [listFilter, setListFilter] = useState<PosPermissionListFilter>("all");
  const [expandedGroups, setExpandedGroups] = useState<Set<PosPermissionDisplayGroupKey>>(
    () => new Set()
  );
  // 搜索/筛选时命中组默认展开；用户在筛选中手动收起的组单独记录，筛选条件变化即重置。
  const [filterCollapsedGroups, setFilterCollapsedGroups] = useState<
    Set<PosPermissionDisplayGroupKey>
  >(() => new Set());
  const initializedScopeKeyRef = useRef<string | null>(null);
  const appliedDataUpdatedAtRef = useRef(0);
  const expansionScopeKeyRef = useRef<string | null>(null);
  const operationInFlightRef = useRef(false);
  const pendingActionRef = useRef<NavigationAction | null>(null);
  const busy = updateMutation.isPending || restoreMutation.isPending;
  const dirty = !arePermissionCodeSetsEqual(selectedCodes, baselineCodes);
  const bypassRemovalGuard = shouldBypassPosPermissionRemovalGuard({
    isAuthenticated,
    terminalErrorKind,
  });

  const applyServerPermissions = useCallback(
    (
      permissions: StoreUserPosTerminalPermissions,
      dataUpdatedAt = Date.now()
    ) => {
      // 服务端响应是保存后的权威状态，必须同时重建选择与基线。
      const draft = buildPosPermissionDraft(permissions);
      setSelectedCodes(draft.selectedCodes);
      setBaselineCodes(draft.baselineCodes);
      if (expansionScopeKeyRef.current !== scopeKey) {
        // 每个员工/分店首次拿到草稿时计算默认展开（此时无改动，即部分勾选的组展开）；
        // 直接用响应数据计算，避免在 effect 里读到尚未更新的旧草稿。
        expansionScopeKeyRef.current = scopeKey;
        setExpandedGroups(
          getDefaultExpandedPosPermissionGroups(
            buildPosPermissionDisplayGroups(permissions.assignablePermissions),
            new Set(draft.selectedCodes),
            new Set()
          )
        );
      }
      initializedScopeKeyRef.current = scopeKey;
      appliedDataUpdatedAtRef.current = dataUpdatedAt;
    },
    [scopeKey]
  );

  useEffect(() => {
    if (initializedScopeKeyRef.current !== scopeKey) {
      initializedScopeKeyRef.current = null;
      appliedDataUpdatedAtRef.current = 0;
      expansionScopeKeyRef.current = null;
      setSelectedCodes([]);
      setBaselineCodes([]);
      setTerminalErrorKind(null);
    }
  }, [scopeKey]);

  useEffect(() => {
    if (
      permissionsQuery.data &&
      shouldApplyPosPermissionResponse({
        initializedScopeKey: initializedScopeKeyRef.current,
        nextScopeKey: scopeKey,
        dirty,
        busy,
        appliedDataUpdatedAt: appliedDataUpdatedAtRef.current,
        nextDataUpdatedAt: permissionsQuery.dataUpdatedAt,
      })
    ) {
      applyServerPermissions(
        permissionsQuery.data,
        permissionsQuery.dataUpdatedAt
      );
    }
  }, [
    applyServerPermissions,
    busy,
    dirty,
    permissionsQuery.data,
    permissionsQuery.dataUpdatedAt,
    scopeKey,
  ]);

  usePreventRemove(
    !bypassRemovalGuard &&
      shouldPreventPosPermissionRemoval({ dirty, busy, allowRemove }),
    ({ data }) => {
      if (
        shouldBypassPosPermissionRemovalGuard({
          // Zustand 可能已在拦截发生前更新，必须读取当前值而不是闭包快照。
          isAuthenticated: useAuthStore.getState().isAuthenticated,
          terminalErrorKind,
        })
      ) {
        pendingActionRef.current = data.action;
        setAuthRedirectRequested(true);
        setAllowRemove(true);
        return;
      }

      if (busy) {
        Alert.alert(
          t("posPermissions.busy.title"),
          t("posPermissions.busy.description"),
          [{ text: t("common:actions.confirm") }]
        );
        return;
      }

      Alert.alert(
        t("posPermissions.unsaved.title"),
        t("posPermissions.unsaved.description"),
        [
          { text: t("common:actions.cancel"), style: "cancel" },
          {
            text: t("posPermissions.unsaved.discard"),
            style: "destructive",
            onPress: () => {
              // 保存原始 action，下一次渲染先解除阻止再统一派发，兼容返回键与手势。
              pendingActionRef.current = data.action;
              setAllowRemove(true);
            },
          },
        ]
      );
    }
  );

  useEffect(() => {
    if (!allowRemove) return;

    if (pendingActionRef.current) {
      const pendingAction = pendingActionRef.current;
      pendingActionRef.current = null;
      // usePreventRemove 已在本次渲染关闭阻止，优先重放原始登录或离页 action。
      navigation.dispatch(pendingAction);
      return;
    }

    if (authRedirectRequested) {
      // 某些 401 路径没有触发导航 action，显式兜底离开失效的编辑上下文。
      router.replace("/(auth)/login");
    }
  }, [allowRemove, authRedirectRequested, navigation, router]);

  const handleBack = useCallback(() => {
    if (router.canGoBack()) {
      router.back();
      return;
    }
    router.replace("/(shell)/users");
  }, [router]);

  const assignablePermissions = useMemo(
    () => permissionsQuery.data?.assignablePermissions ?? [],
    [permissionsQuery.data?.assignablePermissions]
  );
  const displayGroups = useMemo(
    () => buildPosPermissionDisplayGroups(assignablePermissions),
    [assignablePermissions]
  );
  const selectedCodeSet = useMemo(() => new Set(selectedCodes), [selectedCodes]);
  const changes = useMemo(
    () => computePosPermissionChanges(displayGroups, baselineCodes, selectedCodes),
    [baselineCodes, displayGroups, selectedCodes]
  );
  const stats = useMemo(
    () => getPosPermissionStats(displayGroups, selectedCodeSet),
    [displayGroups, selectedCodeSet]
  );
  const matchingPresets = useMemo(
    () => new Set(getMatchingPosPermissionPresets(selectedCodes, assignablePermissions)),
    [assignablePermissions, selectedCodes]
  );
  const getGroupLabel = useCallback(
    (key: PosPermissionDisplayGroupKey) => t(`posPermissions.groups.${key}`),
    [t]
  );
  const filterActive = isPosPermissionFilterActive(searchQuery, listFilter);
  const visibleGroups = useMemo(
    () =>
      filterPosPermissionGroups(displayGroups, {
        query: searchQuery,
        filter: listFilter,
        selectedCodeSet,
        changedCodeSet: changes.changedCodes,
        getGroupLabel,
      }),
    [changes.changedCodes, displayGroups, getGroupLabel, listFilter, searchQuery, selectedCodeSet]
  );
  const fullGroupByKey = useMemo(
    () => new Map(displayGroups.map((group) => [group.key, group])),
    [displayGroups]
  );
  const mode = permissionsQuery.data?.mode?.toLowerCase() === "override"
    ? "override"
    : "inherited";
  const roleLabel = roleNames
    .map((roleName) =>
      roleName.toLowerCase() === "storestaff" ? t("posPermissions.roleLabels.storeStaff") : roleName
    )
    .join(" / ");

  useEffect(() => {
    setFilterCollapsedGroups(new Set());
  }, [listFilter, searchQuery]);

  /** 整体替换草稿（模板/复制同事），并把有改动的组展开，便于马上核对。 */
  const replaceDraft = useCallback(
    (nextCodes: string[]) => {
      const nextSet = new Set(nextCodes);
      const nextChanges = computePosPermissionChanges(displayGroups, baselineCodes, nextCodes);
      setSelectedCodes(nextCodes);
      setExpandedGroups(
        getDefaultExpandedPosPermissionGroups(displayGroups, nextSet, nextChanges.changedCodes)
      );
    },
    [baselineCodes, displayGroups]
  );

  const handleApplyPreset = useCallback(
    (preset: PosPermissionPresetKey) => {
      if (busy) return;
      replaceDraft(buildPosPermissionPresetCodes(preset, assignablePermissions));
      setSnackbarMessage(
        t("posPermissions.messages.presetApplied", { name: t(`posPermissions.presets.${preset}`) })
      );
    },
    [assignablePermissions, busy, replaceDraft, t]
  );

  const handleApplyCopy = useCallback(
    (colleagueName: string, codes: string[]) => {
      if (busy) return;
      setCopyVisible(false);
      // 再过一次当前白名单，确保复制结果不越过本页可分配范围。
      replaceDraft(buildGrantedPosPermissionCodes(codes, assignablePermissions));
      setSnackbarMessage(t("posPermissions.messages.copyApplied", { name: colleagueName }));
    },
    [assignablePermissions, busy, replaceDraft, t]
  );

  const handleToggleCode = useCallback((code: string) => {
    setSelectedCodes((current) => togglePosPermissionCode(current, code));
  }, []);

  const handleSetGroupChecked = useCallback((codes: string[], checked: boolean) => {
    setSelectedCodes((current) => setPosPermissionGroupSelection(current, codes, checked));
  }, []);

  const handleToggleExpanded = useCallback(
    (group: PosPermissionDisplayGroup) => {
      const toggle = (current: Set<PosPermissionDisplayGroupKey>) => {
        const next = new Set(current);
        if (next.has(group.key)) next.delete(group.key);
        else next.add(group.key);
        return next;
      };
      if (filterActive) setFilterCollapsedGroups(toggle);
      else setExpandedGroups(toggle);
    },
    [filterActive]
  );

  const handleSave = useCallback(async () => {
    if (
      !permissionsQuery.data ||
      !dirty ||
      busy ||
      operationInFlightRef.current
    ) return;

    // ref 在等待 React Query 更新 isPending 前同步占位，封住快速双击窗口。
    operationInFlightRef.current = true;
    try {
      const response = await updateMutation.mutateAsync({
        userGuid,
        storeGuid,
        grantedPermissionCodes: buildGrantedPosPermissionCodes(
          selectedCodes,
          permissionsQuery.data.assignablePermissions
        ),
      });
      setTerminalErrorKind(null);
      setAuthRedirectRequested(false);
      applyServerPermissions(response);
      setSnackbarMessage(t("posPermissions.messages.saved"));
    } catch (error) {
      console.warn("[pos-terminal-permissions] 保存失败", error);
      const errorKind = classifyPosPermissionError(error);
      if (errorKind === "network") {
        setSnackbarMessage(t("posPermissions.messages.saveFailed"));
      } else {
        setTerminalErrorKind(errorKind);
        if (errorKind === "unauthorized") {
          setAuthRedirectRequested(true);
          setAllowRemove(true);
        }
      }
    } finally {
      operationInFlightRef.current = false;
    }
  }, [
    applyServerPermissions,
    busy,
    dirty,
    permissionsQuery.data,
    selectedCodes,
    storeGuid,
    t,
    updateMutation,
    userGuid,
  ]);

  const handleConfirmSave = useCallback(() => {
    // 先关闭确认弹层再保存：保存失败的 Snackbar 在 Paper Portal 弹层下方会被遮住。
    setChangesVisible(false);
    void handleSave();
  }, [handleSave]);

  const handleRestore = useCallback(() => {
    if (busy || mode !== "override" || operationInFlightRef.current) return;

    Alert.alert(
      t("posPermissions.restore.title"),
      t("posPermissions.restore.description"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("posPermissions.actions.restore"),
          style: "destructive",
          onPress: async () => {
            if (
              operationInFlightRef.current ||
              updateMutation.isPending ||
              restoreMutation.isPending
            ) return;

            // Alert 回调同样同步占位，防止确认按钮被连续触发。
            operationInFlightRef.current = true;
            try {
              const response = await restoreMutation.mutateAsync({
                userGuid,
                storeGuid,
              });
              setTerminalErrorKind(null);
              setAuthRedirectRequested(false);
              applyServerPermissions(response);
              setSnackbarMessage(t("posPermissions.messages.restored"));
            } catch (error) {
              console.warn("[pos-terminal-permissions] 恢复继承失败", error);
              const errorKind = classifyPosPermissionError(error);
              if (errorKind === "network") {
                setSnackbarMessage(t("posPermissions.messages.restoreFailed"));
              } else {
                setTerminalErrorKind(errorKind);
                if (errorKind === "unauthorized") {
                  setAuthRedirectRequested(true);
                  setAllowRemove(true);
                }
              }
            } finally {
              operationInFlightRef.current = false;
            }
          },
        },
      ]
    );
  }, [
    applyServerPermissions,
    busy,
    mode,
    restoreMutation,
    storeGuid,
    t,
    updateMutation.isPending,
    userGuid,
  ]);

  const editorReady = Boolean(permissionsQuery.data) && !terminalErrorKind && queryEnabled;

  const renderHeader = () => (
    <Surface style={styles.header} elevation={0}>
      <IconButton
        icon="arrow-left"
        accessibilityLabel={t("posPermissions.actions.back")}
        disabled={busy}
        onPress={handleBack}
      />
      <Text variant="titleLarge" numberOfLines={1} style={styles.headerTitle}>
        {t("posPermissions.title")}
      </Text>
      {editorReady ? (
        <Menu
          visible={menuVisible}
          onDismiss={() => setMenuVisible(false)}
          anchorPosition="bottom"
          anchor={
            <IconButton
              icon="dots-vertical"
              accessibilityLabel={t("posPermissions.actions.more")}
              onPress={() => setMenuVisible(true)}
            />
          }
        >
          <Menu.Item
            leadingIcon="backup-restore"
            title={t("posPermissions.actions.restore")}
            disabled={busy || mode !== "override"}
            onPress={() => {
              setMenuVisible(false);
              handleRestore();
            }}
          />
        </Menu>
      ) : null}
    </Surface>
  );

  const renderErrorState = (explicitKind?: PosPermissionErrorKind) => {
    const errorKind = explicitKind ?? classifyPosPermissionError(permissionsQuery.error);
    const canReturn = errorKind === "forbidden" || errorKind === "notFound";
    const canRetry = errorKind === "network";

    return (
      <EmptyState
        title={t(`posPermissions.errors.${errorKind}Title`)}
        description={t(`posPermissions.errors.${errorKind}Description`)}
        primaryAction={
          canReturn
            ? { label: t("posPermissions.actions.back"), icon: "arrow-left", onPress: handleBack }
            : canRetry
              ? { label: t("common:actions.retry"), icon: "refresh", onPress: () => void permissionsQuery.refetch() }
              : undefined
        }
      />
    );
  };

  const renderSummaryPanel = () => (
    <View style={styles.panel}>
      <View style={styles.identityRow}>
        <Avatar.Text
          size={44}
          label={getPosPermissionInitials(userName)}
          style={styles.avatar}
          labelStyle={styles.avatarLabel}
        />
        <View style={styles.identityText}>
          <Text style={styles.identityName} numberOfLines={1}>
            {userName}
          </Text>
          <Text style={styles.secondaryText} numberOfLines={1}>
            {roleLabel ? t("posPermissions.storeRole", { store: storeName, role: roleLabel }) : storeName}
          </Text>
        </View>
        <View style={[styles.modePill, mode === "override" && styles.modePillOverride]}>
          <Text style={[styles.modePillText, mode === "override" && styles.modePillTextOverride]}>
            {t(`posPermissions.mode.${mode}`)}
          </Text>
        </View>
      </View>

      <View style={styles.panelDivider} />

      <Text style={styles.panelLabel}>{t("posPermissions.presets.title")}</Text>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={styles.presetRow}
        keyboardShouldPersistTaps="handled"
      >
        {POS_PERMISSION_PRESET_ORDER.map((preset) => {
          const active = matchingPresets.has(preset);
          return (
            <Pressable
              key={preset}
              accessibilityRole="button"
              accessibilityState={{ selected: active, disabled: busy }}
              disabled={busy}
              hitSlop={4}
              onPress={() => handleApplyPreset(preset)}
              style={({ pressed }) => [
                styles.presetChip,
                active && styles.presetChipActive,
                pressed && styles.pressed,
              ]}
            >
              <Text style={[styles.presetChipText, active && styles.presetChipTextActive]}>
                {t(`posPermissions.presets.${preset}`)}
              </Text>
            </Pressable>
          );
        })}
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ disabled: busy }}
          disabled={busy}
          hitSlop={4}
          onPress={() => setCopyVisible(true)}
          style={({ pressed }) => [styles.presetChip, pressed && styles.pressed]}
        >
          <Text style={styles.presetChipText}>{t("posPermissions.presets.copy")}</Text>
        </Pressable>
      </ScrollView>

      <View style={styles.panelDivider} />

      <View
        style={styles.statsRow}
        accessible
        accessibilityLabel={t("posPermissions.stats.progressLabel", {
          selected: stats.enabledCount,
          total: stats.total,
        })}
      >
        <View style={styles.statsNumberRow}>
          <Text style={styles.statsLabel}>{t("posPermissions.stats.enabledLabel")}</Text>
          <Text style={styles.statsNumber}>{stats.enabledCount}</Text>
          <Text style={styles.statsTotal}>
            {t("posPermissions.stats.ofTotal", { total: stats.total })}
          </Text>
        </View>
        {stats.highRiskTotal > 0 ? (
          <View
            style={[
              styles.riskSummaryPill,
              stats.highRiskEnabledCount > 0 && styles.riskSummaryPillOn,
            ]}
          >
            <Text
              style={[
                styles.riskSummaryText,
                stats.highRiskEnabledCount > 0 && styles.riskSummaryTextOn,
              ]}
            >
              {t("posPermissions.stats.highRiskEnabled", { value: stats.highRiskEnabledCount })}
            </Text>
          </View>
        ) : null}
      </View>
      <View style={styles.progressTrack} importantForAccessibility="no-hide-descendants">
        {stats.segments.map((segment) => (
          <View key={segment.key} style={[styles.progressSegment, { flex: segment.total }]}>
            <View
              style={[
                styles.progressFill,
                { width: `${segment.total ? (segment.selected / segment.total) * 100 : 0}%` },
              ]}
            />
          </View>
        ))}
      </View>
    </View>
  );

  const renderFilters = () => {
    const filterCounts: Record<PosPermissionListFilter, number | null> = {
      all: null,
      enabled: stats.enabledCount,
      highRisk: stats.highRiskTotal,
      changed: changes.ordered.length,
    };
    return (
      <View style={styles.filterBlock}>
        <Searchbar
          value={searchQuery}
          onChangeText={setSearchQuery}
          placeholder={t("posPermissions.search.placeholder")}
          style={styles.search}
          inputStyle={styles.searchInput}
          autoCorrect={false}
          autoCapitalize="none"
        />
        <View style={styles.segmented} accessibilityRole="tablist">
          {LIST_FILTERS.map((filter) => {
            const active = listFilter === filter;
            const count = filterCounts[filter];
            return (
              <Pressable
                key={filter}
                accessibilityRole="tab"
                accessibilityState={{ selected: active }}
                onPress={() => setListFilter(filter)}
                style={[styles.segment, active && styles.segmentActive]}
              >
                <Text
                  numberOfLines={1}
                  style={[styles.segmentText, active && styles.segmentTextActive]}
                >
                  {count === null
                    ? t(`posPermissions.filters.${filter}`)
                    : t(`posPermissions.filters.${filter}`, { value: count })}
                </Text>
              </Pressable>
            );
          })}
        </View>
      </View>
    );
  };

  const renderFooter = () => {
    const pendingCount = changes.ordered.length;
    return (
      <Surface style={styles.footer} elevation={3}>
        <View style={styles.footerText}>
          {dirty ? (
            <>
              <Text style={styles.footerPending}>
                {t("posPermissions.footer.pending", { value: pendingCount })}
              </Text>
              <Pressable
                accessibilityRole="button"
                disabled={busy}
                hitSlop={8}
                onPress={() => setChangesVisible(true)}
              >
                <Text style={styles.footerLink}>{t("posPermissions.actions.reviewChanges")}</Text>
              </Pressable>
            </>
          ) : (
            <Text style={styles.footerIdle}>{t("posPermissions.footer.upToDate")}</Text>
          )}
        </View>
        <Button
          mode="contained"
          icon="content-save-outline"
          loading={updateMutation.isPending}
          disabled={!dirty || busy}
          onPress={() => setChangesVisible(true)}
          style={[BUSINESS_UI.button, styles.saveButton]}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t("posPermissions.actions.save")}
        </Button>
      </Surface>
    );
  };

  let content;
  if (!hasRequiredParams) {
    content = (
      <EmptyState
        title={t("posPermissions.errors.invalidTitle")}
        description={t("posPermissions.errors.invalidDescription")}
        primaryAction={{ label: t("posPermissions.actions.back"), icon: "arrow-left", onPress: handleBack }}
      />
    );
  } else if (!canManage || isCurrentUser) {
    content = (
      <EmptyState
        title={t("posPermissions.errors.forbiddenTitle")}
        description={t("posPermissions.errors.forbiddenDescription")}
        primaryAction={{ label: t("posPermissions.actions.back"), icon: "arrow-left", onPress: handleBack }}
      />
    );
  } else if (terminalErrorKind) {
    // 401/403/404 表示当前编辑上下文已失效，停止继续展示或操作旧草稿。
    content = renderErrorState(terminalErrorKind);
  } else if (permissionsQuery.isLoading) {
    content = (
      <View style={styles.centerState}>
        <ActivityIndicator />
        <Text variant="bodyMedium" style={styles.secondaryText}>
          {t("posPermissions.loading")}
        </Text>
      </View>
    );
  } else if (permissionsQuery.isError) {
    content = renderErrorState();
  } else if (permissionsQuery.data) {
    content = (
      <>
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
          keyboardDismissMode="on-drag"
        >
          {renderSummaryPanel()}
          {displayGroups.length === 0 ? (
            <EmptyState
              title={t("posPermissions.emptyTitle")}
              description={t("posPermissions.emptyDescription")}
            />
          ) : (
            <View style={styles.listArea}>
              {renderFilters()}
              {visibleGroups.length === 0 ? (
                <Text style={styles.noResults}>{t("posPermissions.noResults")}</Text>
              ) : (
                visibleGroups.map((group) => (
                  <PosPermissionGroupCard
                    key={group.key}
                    group={group}
                    fullGroup={fullGroupByKey.get(group.key) ?? group}
                    groupLabel={getGroupLabel(group.key)}
                    selectedCodeSet={selectedCodeSet}
                    changedCodeSet={changes.changedCodes}
                    expanded={
                      filterActive
                        ? !filterCollapsedGroups.has(group.key)
                        : expandedGroups.has(group.key)
                    }
                    disabled={busy}
                    onToggleExpanded={handleToggleExpanded}
                    onToggleCode={handleToggleCode}
                    onSetGroupChecked={handleSetGroupChecked}
                  />
                ))
              )}
            </View>
          )}
        </ScrollView>
        {renderFooter()}
      </>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right", "bottom"]}>
      <Stack.Screen options={{ headerBackButtonMenuEnabled: false }} />
      {renderHeader()}
      <View style={styles.body}>{content}</View>
      <Snackbar
        visible={Boolean(snackbarMessage)}
        duration={2800}
        onDismiss={() => setSnackbarMessage("")}
        wrapperStyle={styles.snackbar}
      >
        {snackbarMessage}
      </Snackbar>
      {editorReady && changesVisible ? (
        <PosPermissionChangesSheet
          changes={changes}
          userName={userName}
          storeName={storeName}
          busy={busy}
          getGroupLabel={getGroupLabel}
          onDismiss={() => setChangesVisible(false)}
          onConfirm={handleConfirmSave}
        />
      ) : null}
      {editorReady && copyVisible ? (
        <PosPermissionCopySheet
          storeCode={storeCode}
          storeGuid={storeGuid}
          storeName={storeName}
          targetUserGuid={userGuid}
          actorUserGuid={actorUserGuid}
          assignablePermissions={assignablePermissions}
          onDismiss={() => setCopyVisible(false)}
          onApply={handleApplyCopy}
        />
      ) : null}
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
    paddingRight: HB_SPACING.xxs,
  },
  headerTitle: { ...BUSINESS_UI.sectionTitle, fontSize: 18, flex: 1 },
  centerState: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  scrollContent: { paddingBottom: HB_SPACING.lg },
  secondaryText: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  pressed: { opacity: 0.7 },
  // 顶部信息区：整块白底，用分隔线区分身份、快速套用与统计，不做卡片堆叠。
  panel: {
    backgroundColor: HB_COLORS.white,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.md,
    paddingBottom: HB_SPACING.md,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  identityRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  avatar: { backgroundColor: POS_PERMISSION_COLORS.brandSoft },
  avatarLabel: { color: HB_COLORS.action, fontSize: 16, fontWeight: "700" },
  identityText: { flex: 1, minWidth: 0, gap: 2 },
  identityName: { fontSize: 17, lineHeight: 24, fontWeight: "700", color: HB_COLORS.textPrimary },
  modePill: {
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 999,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  modePillOverride: { backgroundColor: POS_PERMISSION_COLORS.brandSoft },
  modePillText: { fontSize: 12, lineHeight: 16, fontWeight: "600", color: HB_COLORS.textSecondary },
  modePillTextOverride: { color: HB_COLORS.action },
  panelDivider: {
    height: StyleSheet.hairlineWidth,
    backgroundColor: HB_COLORS.outlineMuted,
    marginVertical: HB_SPACING.sm,
  },
  panelLabel: { ...BUSINESS_UI.fieldLabel, marginBottom: HB_SPACING.xs },
  presetRow: { gap: HB_SPACING.xs, paddingRight: HB_SPACING.md },
  presetChip: {
    minHeight: 36,
    paddingHorizontal: 14,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
  },
  presetChipActive: { backgroundColor: POS_PERMISSION_COLORS.brandSoft, borderColor: HB_COLORS.brand },
  presetChipText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  presetChipTextActive: { color: HB_COLORS.action },
  statsRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  statsNumberRow: { flexDirection: "row", alignItems: "baseline", gap: 6 },
  statsLabel: { fontSize: 13, color: HB_COLORS.textSecondary },
  statsNumber: {
    fontSize: 28,
    lineHeight: 34,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  statsTotal: {
    fontSize: 15,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  riskSummaryPill: {
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 999,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  riskSummaryPillOn: { backgroundColor: POS_PERMISSION_COLORS.dangerSoft },
  riskSummaryText: {
    fontSize: 12,
    lineHeight: 16,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  riskSummaryTextOn: { color: HB_COLORS.danger },
  progressTrack: { flexDirection: "row", gap: 3, marginTop: HB_SPACING.xs, height: 6 },
  progressSegment: {
    height: 6,
    borderRadius: 3,
    backgroundColor: HB_COLORS.outlineMuted,
    overflow: "hidden",
  },
  progressFill: { height: 6, backgroundColor: HB_COLORS.brand },
  listArea: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, gap: HB_SPACING.sm },
  filterBlock: { gap: HB_SPACING.xs },
  search: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    height: 44,
    elevation: 0,
  },
  searchInput: { minHeight: 44, fontSize: 15 },
  segmented: {
    flexDirection: "row",
    padding: 3,
    borderRadius: HB_RADIUS.control + 2,
    backgroundColor: HB_COLORS.surfaceMuted,
    gap: 2,
  },
  segment: {
    flex: 1,
    minHeight: 38,
    borderRadius: HB_RADIUS.control,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 4,
  },
  segmentActive: {
    backgroundColor: HB_COLORS.white,
    shadowColor: "#101828",
    shadowOpacity: 0.08,
    shadowRadius: 2,
    shadowOffset: { width: 0, height: 1 },
    elevation: 1,
  },
  segmentText: {
    fontSize: 13,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  segmentTextActive: { color: HB_COLORS.textPrimary },
  noResults: {
    textAlign: "center",
    color: HB_COLORS.textSecondary,
    paddingVertical: HB_SPACING.lg,
  },
  footer: {
    ...BUSINESS_UI.footer,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
  },
  footerText: { flex: 1, minWidth: 0, gap: 2 },
  footerPending: {
    fontSize: 14,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  footerLink: { fontSize: 13, fontWeight: "600", color: HB_COLORS.action },
  footerIdle: { fontSize: 13, color: HB_COLORS.textSecondary },
  saveButton: { minWidth: 112 },
  snackbar: { bottom: 72 },
});
