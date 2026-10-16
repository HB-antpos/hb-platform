import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, ScrollView, StyleSheet, View } from "react-native";
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
  Button,
  IconButton,
  Searchbar,
  Snackbar,
  Surface,
  Text,
} from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { FilterChipRow, InfoCard } from "@/modules/people/ui";
import { resolvePersonName } from "@/modules/people/person-name";
import { PosPermissionChangesSheet } from "@/modules/users/pos-permission-changes-sheet";
import { PosPermissionCopySheet } from "@/modules/users/pos-permission-copy-sheet";
import { PosPermissionGroupCard } from "@/modules/users/pos-permission-group-card";
import { usePosPermissionTranslation } from "@/modules/users/pos-permission-i18n";
import { PosPermissionSummaryCard } from "@/modules/users/pos-permission-summary-card";
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
  buildPosPermissionDisplayGroups,
  buildPosPermissionPresetCodes,
  computePosPermissionChanges,
  filterPosPermissionGroups,
  getDefaultExpandedPosPermissionGroups,
  getMatchingPosPermissionPresets,
  isPosPermissionFilterActive,
  type PosPermissionDisplayGroup,
  type PosPermissionDisplayGroupKey,
  type PosPermissionListFilter,
  type PosPermissionPresetKey,
} from "@/modules/users/pos-terminal-permission-presentation";
import { formatPosFooterSummary } from "@/modules/users/pos-terminal-permissions-format";
import {
  buildPosPermissionFilterChips,
  buildPosPermissionOverview,
  buildPosStoreLabel,
  getPosPermissionEmptyKind,
  normalizePosPermissionMode,
  resolvePosPermissionStoreTarget,
} from "@/modules/users/pos-terminal-permissions-view";
import type { StoreUserPosTerminalPermissions } from "@/modules/users/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

export default function PosTerminalPermissionsScreen() {
  const router = useRouter();
  const navigation = useNavigation();
  const { t, translate, labels } = usePosPermissionTranslation();
  // 路由参数保持原样：userGuid / storeGuid / storeCode / userName / storeName（roleNames 旧入口还会带，不再使用）。
  const params = useLocalSearchParams<{
    userGuid?: string | string[];
    storeGuid?: string | string[];
    storeCode?: string | string[];
    userName?: string | string[];
    storeName?: string | string[];
  }>();
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const userGuid = firstParam(params.userGuid)?.trim() ?? "";
  // 入口可能只带 storeGuid 或只带 storeCode，缺的一个用当前会话的分店列表补齐。
  const sessionStores = currentUser?.stores;
  const storeTarget = useMemo(
    () =>
      resolvePosPermissionStoreTarget(
        {
          storeGuid: firstParam(params.storeGuid),
          storeCode: firstParam(params.storeCode),
          storeName: firstParam(params.storeName),
        },
        sessionStores ?? []
      ),
    [params.storeCode, params.storeGuid, params.storeName, sessionStores]
  );
  const { storeGuid, storeCode } = storeTarget;
  const userName = firstParam(params.userName)?.trim() || t("unknownUser");
  // 副标题里的姓名与人员管理其他页面一致：走 resolvePersonName（路由只传全名，回落到全名）。
  const personName = useMemo(() => resolvePersonName({ fullName: userName }).primary, [userName]);
  const storeLabel = storeTarget.storeName
    ? buildPosStoreLabel(storeTarget.storeName, storeCode)
    : storeCode || t("unknownStore");
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
  const [changesVisible, setChangesVisible] = useState(false);
  const [copyVisible, setCopyVisible] = useState(false);
  const [searchVisible, setSearchVisible] = useState(false);
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
          t("busy.title"),
          t("busy.description"),
          [{ text: t("common:actions.confirm") }]
        );
        return;
      }

      Alert.alert(
        t("unsaved.title"),
        t("unsaved.description"),
        [
          { text: t("common:actions.cancel"), style: "cancel" },
          {
            text: t("unsaved.discard"),
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
  const mode = normalizePosPermissionMode(permissionsQuery.data?.mode);
  const overview = useMemo(
    () => buildPosPermissionOverview({ groups: displayGroups, selectedCodeSet, changes, mode }),
    [changes, displayGroups, mode, selectedCodeSet]
  );
  const matchingPresets = useMemo(
    () => new Set(getMatchingPosPermissionPresets(selectedCodes, assignablePermissions)),
    [assignablePermissions, selectedCodes]
  );
  const getGroupLabel = labels.group;
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
  const filterChips = useMemo(
    () =>
      buildPosPermissionFilterChips(overview).map((chip) => ({
        key: chip.key,
        label: t(`filters.${chip.key}`),
        count: chip.count,
      })),
    [overview, t]
  );

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
      setSnackbarMessage(t("messages.presetApplied", { name: t(`presets.${preset}`) }));
    },
    [assignablePermissions, busy, replaceDraft, t]
  );

  const handleApplyCopy = useCallback(
    (colleagueName: string, codes: string[]) => {
      if (busy) return;
      setCopyVisible(false);
      // 再过一次当前白名单，确保复制结果不越过本页可分配范围。
      replaceDraft(buildGrantedPosPermissionCodes(codes, assignablePermissions));
      setSnackbarMessage(t("messages.copyApplied", { name: colleagueName }));
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
      setSnackbarMessage(t("messages.saved"));
    } catch (error) {
      console.warn("[pos-terminal-permissions] 保存失败", error);
      const errorKind = classifyPosPermissionError(error);
      if (errorKind === "network") {
        setSnackbarMessage(t("messages.saveFailed"));
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
      t("restore.title"),
      t("restore.description"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("actions.restore"),
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
              setSnackbarMessage(t("messages.restored"));
            } catch (error) {
              console.warn("[pos-terminal-permissions] 恢复继承失败", error);
              const errorKind = classifyPosPermissionError(error);
              if (errorKind === "network") {
                setSnackbarMessage(t("messages.restoreFailed"));
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
  const footerSummary = formatPosFooterSummary(
    overview.changedCount,
    overview.addedHighRiskCount,
    translate
  );

  const handleToggleSearch = () => {
    // 收起搜索时清掉关键字，避免列表被一个看不见的搜索词过滤。
    if (searchVisible) setSearchQuery("");
    setSearchVisible(!searchVisible);
  };

  const renderHeader = () => (
    <Surface style={styles.header} elevation={0}>
      <IconButton
        icon="arrow-left"
        accessibilityLabel={t("actions.back")}
        disabled={busy}
        onPress={handleBack}
      />
      <View style={styles.headerText}>
        <Text style={styles.headerTitle} numberOfLines={1}>
          {t("title")}
        </Text>
        {hasRequiredParams ? (
          <Text style={styles.headerSubtitle} numberOfLines={1}>
            {t("subtitle", { user: personName, store: storeLabel })}
          </Text>
        ) : null}
      </View>
      {editorReady && displayGroups.length > 0 ? (
        <IconButton
          icon={searchVisible ? "close" : "magnify"}
          accessibilityLabel={t(searchVisible ? "actions.closeSearch" : "actions.search")}
          onPress={handleToggleSearch}
        />
      ) : null}
    </Surface>
  );

  const renderErrorState = (explicitKind?: PosPermissionErrorKind) => {
    const errorKind = explicitKind ?? classifyPosPermissionError(permissionsQuery.error);
    const canReturn = errorKind === "forbidden" || errorKind === "notFound";
    const canRetry = errorKind === "network";

    return (
      <EmptyState
        title={t(`errors.${errorKind}Title`)}
        description={t(`errors.${errorKind}Description`)}
        primaryAction={
          canReturn
            ? { label: t("actions.back"), icon: "arrow-left", onPress: handleBack }
            : canRetry
              ? { label: t("common:actions.retry"), icon: "refresh", onPress: () => void permissionsQuery.refetch() }
              : undefined
        }
      />
    );
  };

  // 吸底只有一个按钮「查看并保存」：左侧写「N 项改动（含 M 项高风险）」，没有改动时按钮灰掉。
  const renderFooter = () => (
    <View style={styles.footer}>
      <Text style={styles.footerText} numberOfLines={2}>
        <Text style={overview.changedCount > 0 ? styles.footerMain : styles.footerIdle}>
          {footerSummary.main}
        </Text>
        {footerSummary.note}
      </Text>
      <Button
        mode="contained"
        loading={updateMutation.isPending}
        disabled={!dirty || busy}
        onPress={() => setChangesVisible(true)}
        style={[BUSINESS_UI.button, styles.saveButton]}
        contentStyle={BUSINESS_UI.buttonContent}
      >
        {t("actions.reviewAndSave")}
      </Button>
    </View>
  );

  let content;
  if (!hasRequiredParams) {
    content = (
      <EmptyState
        title={t("errors.invalidTitle")}
        description={t("errors.invalidDescription")}
        primaryAction={{ label: t("actions.back"), icon: "arrow-left", onPress: handleBack }}
      />
    );
  } else if (!canManage || isCurrentUser) {
    content = (
      <EmptyState
        title={t("errors.forbiddenTitle")}
        description={t("errors.forbiddenDescription")}
        primaryAction={{ label: t("actions.back"), icon: "arrow-left", onPress: handleBack }}
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
          {t("loading")}
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
          <PosPermissionSummaryCard
            overview={overview}
            matchingPresets={matchingPresets}
            busy={busy}
            onApplyPreset={handleApplyPreset}
            onOpenCopy={() => setCopyVisible(true)}
            onRestore={handleRestore}
          />
          {displayGroups.length === 0 ? (
            <EmptyState title={t("emptyTitle")} description={t("emptyDescription")} />
          ) : (
            <>
              {searchVisible ? (
                <Searchbar
                  value={searchQuery}
                  onChangeText={setSearchQuery}
                  placeholder={t("search.placeholder")}
                  style={styles.search}
                  inputStyle={styles.searchInput}
                  autoFocus
                  autoCorrect={false}
                  autoCapitalize="none"
                />
              ) : null}
              <FilterChipRow items={filterChips} value={listFilter} onChange={setListFilter} />
              {visibleGroups.length === 0 ? (
                <Text style={styles.noResults}>
                  {t(getPosPermissionEmptyKind(searchQuery, listFilter))}
                </Text>
              ) : (
                <InfoCard>
                  {visibleGroups.map((group, index) => (
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
                      first={index === 0}
                      onToggleExpanded={handleToggleExpanded}
                      onToggleCode={handleToggleCode}
                      onSetGroupChecked={handleSetGroupChecked}
                    />
                  ))}
                </InfoCard>
              )}
            </>
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
          userName={personName}
          storeLabel={storeLabel}
          busy={busy}
          onDismiss={() => setChangesVisible(false)}
          onConfirm={handleConfirmSave}
        />
      ) : null}
      {editorReady && copyVisible ? (
        <PosPermissionCopySheet
          storeCode={storeCode}
          storeGuid={storeGuid}
          storeName={storeLabel}
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
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    paddingRight: HB_SPACING.xxs,
  },
  headerText: { flex: 1, minWidth: 0, justifyContent: "center" },
  headerTitle: { fontSize: 18, lineHeight: 24, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSubtitle: { fontSize: 12.5, lineHeight: 17, color: HB_COLORS.textSecondary },
  centerState: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  scrollContent: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.lg,
    gap: HB_SPACING.xs,
  },
  secondaryText: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  search: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    height: 44,
    elevation: 0,
  },
  searchInput: { minHeight: 44, fontSize: 15 },
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
  footerText: {
    flex: 1,
    minWidth: 0,
    fontSize: 13,
    lineHeight: 18,
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  footerMain: { fontWeight: "800", color: HB_COLORS.textPrimary },
  footerIdle: { color: HB_COLORS.textSecondary },
  saveButton: { minWidth: 128 },
  snackbar: { bottom: 72 },
});
