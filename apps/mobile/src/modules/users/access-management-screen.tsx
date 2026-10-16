import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
  type LayoutChangeEvent,
} from "react-native";
import { Stack, useLocalSearchParams, useNavigation, useRouter } from "expo-router";
import { usePreventRemove, type NavigationAction } from "@react-navigation/native";
import { SafeAreaView } from "react-native-safe-area-context";
import { ActivityIndicator, Button, IconButton, Snackbar, Text } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { NoticeBox, SegmentTabs, StickyActionBar } from "@/modules/people";
import { getRoleMenuDefinitions } from "@/modules/identity-admin/role-menu-catalog";
import { isIdentitySessionAllowed } from "@/modules/identity-admin/user-logic";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { classifyUserAccessError } from "./access-management";
import {
  accessTargetFromIdentityUser,
  accessTargetFromStoreUser,
  buildAccessMenuModel,
  buildAccessSaveReportLines,
  classifyAccessTargetError,
  describeAccessChanges,
  listAccessSections,
  mergeAccessTarget,
  parseAccessCopyParam,
  parseAccessSectionParam,
  pickAccessSection,
  type AccessMenuItem,
  type AccessSectionKey,
} from "./access-page-logic";
import { useAccessEditor, type AccessSaveReport } from "./access-editor";
import { resolveCopyStoreFilter } from "./access-permission-copy";
import { localizeAccessRoleName } from "./access-permission-presentation";
import { useAccessIdentityTarget, useAccessStoreUserTarget } from "./access-target-hooks";
import { AccessMenuSection } from "./access-menu-section";
import { AccessPermissionsSection } from "./access-permissions-section";
import { AccessRolesSection } from "./access-roles-section";
import { AccessStoresSection } from "./access-stores-section";
import { PermissionCopySheet } from "./permission-copy-sheet";
import type {
  AccessStoreOption,
  CopyPermissionDirection,
  UserAccessErrorKind,
} from "./access-management-types";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}

function parseRoleNames(value: string) {
  return value
    .split("|")
    .map((item) => item.trim())
    .filter(Boolean);
}

/** 四个分段 + 窄屏时「直接权限」缩成「权限」，避免 4 个分段在 360 宽下被截断。 */
const NARROW_WIDTH = 400;

/**
 * 统一账号授权页（路由 /users/[userGuid]/access；旧管理员路由只是重定向壳）。
 * 一页分段（分店 / 角色 / 直接权限，管理员多一个菜单）+ 一个吸底保存栏。
 * 目标用户由页面按 userGuid 自己拉取，路由参数只作首屏预填。
 */
export default function AccessManagementScreen() {
  const params = useLocalSearchParams<{
    userGuid?: string | string[];
    storeCode?: string | string[];
    userName?: string | string[];
    username?: string | string[];
    targetStatus?: string | string[];
    roleNames?: string | string[];
    section?: string | string[];
    copy?: string | string[];
  }>();
  const actorKey = useAuthStore((state) => state.user?.userGUID ?? "");
  const userGuid = firstParam(params.userGuid)?.trim() ?? "";
  // 切换操作者或目标用户时销毁整份草稿，禁止跨身份复用授权状态。
  return (
    <AccessManagementPage
      key={`${actorKey}:${userGuid}`}
      userGuid={userGuid}
      routeStoreCode={firstParam(params.storeCode)?.trim() ?? ""}
      hintUserName={firstParam(params.userName)?.trim() ?? ""}
      hintUsername={firstParam(params.username)?.trim() ?? ""}
      hintStatus={firstParam(params.targetStatus) ?? ""}
      hintRoleNames={firstParam(params.roleNames) ?? ""}
      sectionParam={firstParam(params.section) ?? ""}
      copyParam={firstParam(params.copy) ?? ""}
    />
  );
}

interface AccessManagementPageProps {
  userGuid: string;
  routeStoreCode: string;
  hintUserName: string;
  hintUsername: string;
  hintStatus: string;
  hintRoleNames: string;
  sectionParam: string;
  copyParam: string;
}

function AccessManagementPage({
  userGuid,
  routeStoreCode,
  hintUserName,
  hintUsername,
  hintStatus,
  hintRoleNames,
  sectionParam,
  copyParam,
}: AccessManagementPageProps) {
  const router = useRouter();
  const navigation = useNavigation();
  const { width: windowWidth } = useWindowDimensions();
  const { t, language } = useAppTranslation(["peopleAccess", "common"]);
  const currentUser = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const iosReviewOfflineGuardActive = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const actorKey = currentUser?.userGUID ?? "";

  // ───────────── 目标用户：自己拉取，路由参数只作首屏预填 ─────────────
  // 管理员账号会话走管理员接口（能看到任何账号）；店长 / 设备账号 / iOS 审核会话走店员接口。
  const useIdentitySource = isIdentitySessionAllowed(
    { isAuthenticated, sessionKind, iosReviewOfflineGuardActive },
    access.hasPermission("Users.View"),
  );
  const identityTargetQuery = useAccessIdentityTarget({
    userGuid,
    actorKey,
    enabled: useIdentitySource,
  });
  const storeUserTargetQuery = useAccessStoreUserTarget({
    userGuid,
    storeCode: routeStoreCode,
    enabled: !useIdentitySource && isAuthenticated && sessionKind !== "device",
  });
  const targetQuery = useIdentitySource ? identityTargetQuery : storeUserTargetQuery;
  const targetSnapshot = useMemo(() => {
    if (useIdentitySource) {
      return identityTargetQuery.data ? accessTargetFromIdentityUser(identityTargetQuery.data) : null;
    }
    return storeUserTargetQuery.data ? accessTargetFromStoreUser(storeUserTargetQuery.data) : null;
  }, [identityTargetQuery.data, storeUserTargetQuery.data, useIdentitySource]);
  const hintRoles = useMemo(() => parseRoleNames(hintRoleNames), [hintRoleNames]);
  const target = useMemo(
    () =>
      mergeAccessTarget(
        userGuid,
        { userName: hintUserName, username: hintUsername, targetStatus: hintStatus, roleNames: hintRoles },
        targetSnapshot,
      ),
    [hintRoles, hintStatus, hintUserName, hintUsername, targetSnapshot, userGuid],
  );
  const targetErrorKind: UserAccessErrorKind | null = targetQuery.error
    ? classifyAccessTargetError(targetQuery.error)
    : null;

  const editor = useAccessEditor({ userGuid, target, language });
  const { eligibility, failTerminal } = editor;

  useEffect(() => {
    // 目标拉取遇到 401：和其它查询一样优先退出失效的编辑上下文。
    if (targetErrorKind === "unauthorized") failTerminal("unauthorized");
  }, [failTerminal, targetErrorKind]);

  // ───────────── 页面状态 ─────────────
  const [selectedSection, setSelectedSection] = useState<AccessSectionKey | null>(null);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [copyDirection, setCopyDirection] = useState<CopyPermissionDirection | null>(null);
  const [saveNotice, setSaveNotice] = useState<string[] | null>(null);
  const [barHeight, setBarHeight] = useState(0);
  const pendingActionRef = useRef<NavigationAction | null>(null);
  const copyRequest = parseAccessCopyParam(copyParam);
  const copyAutoOpenedRef = useRef(false);

  const sections = useMemo(
    () =>
      listAccessSections({
        storesMode: eligibility.storesMode,
        rolesMode: eligibility.rolesMode,
        permissionsMode: eligibility.permissionsMode,
        isAdmin: editor.isAdmin,
      }),
    [eligibility.permissionsMode, eligibility.rolesMode, eligibility.storesMode, editor.isAdmin],
  );
  // 想看的分段：用户点过的 > 路由 section > copy=1 时落在直接权限；不可见就落到第一个可见分段。
  const requestedSection =
    selectedSection ?? parseAccessSectionParam(sectionParam) ?? (copyRequest ? "permissions" : null);
  const activeSection = pickAccessSection(sections, requestedSection);

  const menuDefinitions = useMemo(() => getRoleMenuDefinitions(language), [language]);
  const menuModel = useMemo(
    () =>
      buildAccessMenuModel({
        definitions: menuDefinitions,
        draft: editor.permissionDraft,
        assignableCodes: editor.assignablePermissionCodes,
        state: editor.permissionAccessState,
      }),
    [
      editor.assignablePermissionCodes,
      editor.permissionAccessState,
      editor.permissionDraft,
      menuDefinitions,
    ],
  );

  const copyStoreFilter = useMemo(
    () =>
      resolveCopyStoreFilter({
        isAdmin: editor.isAdmin,
        storeOptions: editor.storeOptions,
        targetStoreCodes: editor.assignedStoreCodes,
        // 管理员从详情进入时没有 storeCode；店长兜底取自己管理的第一家分店。
        routeStoreCode: routeStoreCode || (editor.managedStoreCodes?.[0] ?? ""),
      }),
    [
      editor.assignedStoreCodes,
      editor.isAdmin,
      editor.managedStoreCodes,
      editor.storeOptions,
      routeStoreCode,
    ],
  );

  const handleBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/users");
  }, [router]);

  // ───────────── 离页保护（有未保存草稿或保存进行中） ─────────────
  usePreventRemove(
    isAuthenticated &&
      editor.terminalErrorKind !== "unauthorized" &&
      (editor.dirty || editor.busy) &&
      !editor.allowRemove,
    ({ data }) => {
      if (
        !useAuthStore.getState().isAuthenticated ||
        editor.terminalErrorKind === "unauthorized"
      ) {
        pendingActionRef.current = data.action;
        editor.setAllowRemove(true);
        return;
      }
      if (editor.busy) {
        Alert.alert(t("busy.title"), t("busy.description"), [
          { text: t("common:actions.confirm") },
        ]);
        return;
      }
      Alert.alert(t("unsaved.title"), t("unsaved.description"), [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("unsaved.discard"),
          style: "destructive",
          onPress: () => {
            pendingActionRef.current = data.action;
            editor.setAllowRemove(true);
          },
        },
      ]);
    },
  );

  useEffect(() => {
    if (!editor.allowRemove) return;
    if (pendingActionRef.current) {
      const action = pendingActionRef.current;
      pendingActionRef.current = null;
      navigation.dispatch(action);
      return;
    }
    if (editor.authRedirectRequested) router.replace("/(auth)/login");
  }, [editor.allowRemove, editor.authRedirectRequested, navigation, router]);

  // 改动全部保存（或被撤回）后，上一次保存失败的提示不再适用。
  useEffect(() => {
    if (!editor.dirty) setSaveNotice(null);
  }, [editor.dirty]);

  // ───────────── 保存 ─────────────
  const presentSaveReport = useCallback(
    (report: AccessSaveReport) => {
      if (report.verdict === "success") {
        setSaveNotice(null);
        setSnackbarMessage(t("save.success"));
        return;
      }
      if (report.verdict === "aborted") {
        Alert.alert(t("save.abortedTitle"), t("save.abortedDescription"), [
          { text: t("common:actions.confirm") },
        ]);
        return;
      }
      if (report.verdict === "nothing") return;
      // 部分成功 / 全部失败：逐项说明哪些已保存、哪一步失败、哪些没执行，草稿仍保留。
      const lines = buildAccessSaveReportLines(report.outcome, {
        sectionLabel: (key) => t(`save.section.${key}`),
        join: (items) => items.join(t("save.separator")),
        savedLine: (sections) => t("save.savedLine", { sections }),
        failedLine: (section, reason) => t("save.failedLine", { section, reason }),
        notRunLine: (sections) => t("save.notRunLine", { sections }),
        retryHint: t("save.retryHint"),
        reasonOf: (error) =>
          resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "save.reasonFallback" }),
      });
      if (
        report.failedKind === "forbidden" &&
        !editor.isAdmin &&
        report.outcome.failed &&
        report.outcome.failed.key !== "stores"
      ) {
        // 局部授权失效：对应分段已被关闭，相关草稿已撤回。
        lines.splice(lines.length - 1, 0, t("save.sectionForbidden"));
      }
      setSaveNotice(lines);
      if (report.failedKind !== "unauthorized") {
        Alert.alert(
          t(report.verdict === "partial" ? "save.partialTitle" : "save.failedTitle"),
          lines.join("\n"),
          [{ text: t("common:actions.confirm") }],
        );
      }
    },
    [editor.isAdmin, language, t],
  );

  const { save: saveEditor } = editor;
  const handleSave = useCallback(async () => {
    const report = await saveEditor();
    if (report) presentSaveReport(report);
  }, [presentSaveReport, saveEditor]);

  // ───────────── 复制权限 ─────────────
  const openCopySheet = useCallback(
    (direction: CopyPermissionDirection) => {
      if (editor.busy) return;
      if (editor.permissionsDirty) {
        // 复制成功后会重新拉取权限覆盖界面，存在未保存草稿时必须先处理，避免被静默覆盖。
        setSnackbarMessage(t("copy.dirtyBlocked"));
        return;
      }
      setCopyDirection(direction);
    },
    [editor.busy, editor.permissionsDirty, t],
  );

  const handleCopied = useCallback(
    (result: { targets: { added: string[]; removed: string[] }[] }) => {
      const changed = result.targets.filter(
        (item) => item.added.length > 0 || item.removed.length > 0,
      ).length;
      setCopyDirection(null);
      setSnackbarMessage(t("copy.success", { count: changed }));
    },
    [t],
  );

  const openPosPermissions = useCallback(
    (store: AccessStoreOption) => {
      const storeGuid = store.storeGUID.trim();
      if (!storeGuid) {
        setSnackbarMessage(t("stores.missingStoreGuid"));
        return;
      }
      router.push({
        pathname: "/users/[userGuid]/pos-terminal-permissions",
        params: {
          userGuid,
          storeGuid,
          // storeCode 必填：收银权限页用它按分店列出同事、复制权限。
          storeCode: store.storeCode,
          userName: target.name.primary,
          storeName: store.storeName || store.storeCode,
          roleNames: target.roleNames.join("|"),
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [router, t, target.name.primary, target.roleNames, userGuid],
  );

  // ───────────── 管理员点「店长」行：引导去分店设为「管理」（PR #840） ─────────────
  const { guideManagerGrant } = editor;
  const handleGuideManagerGrant = useCallback(() => {
    const { plan, storeName } = guideManagerGrant();
    switch (plan.kind) {
      case "denied":
        return;
      case "already":
        setSnackbarMessage(t("roles.grantManager.already"));
        return;
      case "noStore":
        setSelectedSection("stores");
        setSnackbarMessage(t("roles.grantManager.noStore"));
        return;
      case "choose":
        setSelectedSection("stores");
        setSnackbarMessage(t("roles.grantManager.choose"));
        return;
      case "set":
        // 只有一家分店：已在草稿里设为「管理」，切到分店分段让管理员看到并点「保存」。
        setSelectedSection("stores");
        setSnackbarMessage(t("roles.grantManager.set", { store: storeName }));
        return;
    }
  }, [guideManagerGrant, t]);

  // ───────────── 页面状态：内容区该显示什么 ─────────────
  const targetPending = targetQuery.isLoading || (!target.resolved && !targetErrorKind);
  const ready =
    Boolean(userGuid) &&
    eligibility.canOpen &&
    !editor.terminalErrorKind &&
    !targetErrorKind &&
    !targetPending &&
    !editor.initialLoading &&
    !editor.initialError;

  // copy=1：数据就绪后直接打开「复制权限」，只开一次。
  useEffect(() => {
    if (!copyRequest || copyAutoOpenedRef.current || !ready) return;
    if (eligibility.permissionsMode === "hidden" || editor.implicitAll || !editor.permissionDraft) return;
    copyAutoOpenedRef.current = true;
    setCopyDirection(copyRequest);
  }, [copyRequest, editor.implicitAll, editor.permissionDraft, eligibility.permissionsMode, ready]);

  const { retryQueries } = editor;
  const refetchTarget = targetQuery.refetch;
  const retryAll = useCallback(() => {
    retryQueries();
    void refetchTarget();
  }, [refetchTarget, retryQueries]);

  const renderErrorState = (errorKind: UserAccessErrorKind) => (
    <EmptyState
      title={t(`errors.${errorKind}Title`)}
      description={t(`errors.${errorKind}Description`)}
      primaryAction={
        errorKind === "network"
          ? { label: t("common:actions.retry"), icon: "refresh", onPress: retryAll }
          : errorKind === "forbidden" || errorKind === "notFound"
            ? { label: t("actions.back"), icon: "arrow-left", onPress: handleBack }
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
        title={t(`errors.${key}Title`)}
        description={t(`errors.${key}Description`)}
        primaryAction={{ label: t("actions.back"), icon: "arrow-left", onPress: handleBack }}
      />
    );
  };

  const storesEditable = eligibility.storesMode === "editable" && !editor.busy;
  const tabLabel = (key: AccessSectionKey) => {
    const narrow = sections.length >= 4 && windowWidth <= NARROW_WIDTH;
    const label =
      key === "permissions" && narrow ? t("tabs.permissionsShort") : t(`tabs.${key}`);
    const count = key === "menu" ? menuModel.openCount : editor.counts[key];
    return `${label} · ${count}`;
  };

  const renderSection = () => {
    switch (activeSection) {
      case "stores":
        return (
          <AccessStoresSection
            stores={editor.storeOptions}
            assignments={editor.storeAssignments}
            baselineAssignments={editor.baselineStoreAssignments}
            editable={storesEditable}
            canGrantManage={eligibility.canGrantStoreManagement}
            canManagePos={eligibility.canManagePos}
            isAdmin={editor.isAdmin}
            onChangeState={editor.setStoreState}
            onOpenPos={openPosPermissions}
          />
        );
      case "roles":
        return (
          <AccessRolesSection
            roles={editor.roleOptions}
            selectedRoleGuids={editor.selectedRoleGuids}
            hasManagedStoreAssignment={editor.hasManagedStoreAssignment}
            isAdmin={editor.isAdmin}
            rolesEditable={eligibility.rolesMode === "editable"}
            storesEditable={eligibility.storesMode === "editable"}
            canGrantStoreManagement={eligibility.canGrantStoreManagement}
            busy={editor.busy}
            onToggleRole={editor.toggleRole}
            onGuideManagerGrant={handleGuideManagerGrant}
          />
        );
      case "permissions":
        return editor.permissionDraft ? (
          <AccessPermissionsSection
            categories={editor.visiblePermissionCategories}
            posCategories={editor.posPermissionCategories}
            draft={editor.permissionDraft}
            counts={editor.permissionCounts}
            implicitAll={editor.implicitAll}
            isAdmin={editor.isAdmin}
            inheritedSourceMap={editor.inheritedSourceMap}
            editable={eligibility.permissionsMode === "editable"}
            busy={editor.busy}
            onToggle={editor.setPermissionChecked}
            onCopy={openCopySheet}
          />
        ) : (
          <ActivityIndicator style={styles.sectionSpinner} />
        );
      case "menu":
        return editor.permissionDraft ? (
          <AccessMenuSection
            model={menuModel}
            readOnly={
              editor.busy || editor.implicitAll || eligibility.permissionsMode !== "editable"
            }
            onToggle={(item: AccessMenuItem, visible: boolean) =>
              editor.setMenuPermissionCodes(item.permissionCodes, visible)
            }
          />
        ) : (
          <ActivityIndicator style={styles.sectionSpinner} />
        );
      default:
        return <Text style={styles.sectionNotice}>{t("errors.sectionNoAccess")}</Text>;
    }
  };

  let content;
  if (!userGuid) {
    content = (
      <EmptyState
        title={t("errors.invalidTitle")}
        description={t("errors.invalidDescription")}
        primaryAction={{ label: t("actions.back"), icon: "arrow-left", onPress: handleBack }}
      />
    );
  } else if (!eligibility.canOpen) {
    content = renderEligibilityError();
  } else if (editor.terminalErrorKind) {
    content = renderErrorState(editor.terminalErrorKind);
  } else if (targetErrorKind) {
    content = renderErrorState(targetErrorKind);
  } else if (targetPending || editor.initialLoading) {
    content = (
      <View style={styles.centerState}>
        <ActivityIndicator />
        <Text style={styles.secondaryText}>{t("loading")}</Text>
      </View>
    );
  } else if (editor.initialError) {
    content = renderErrorState(classifyUserAccessError(editor.initialError));
  } else {
    content = (
      <>
        {sections.length > 0 ? (
          <View style={styles.tabsWrap}>
            <SegmentTabs
              items={sections.map((key) => ({ key, label: tabLabel(key) }))}
              value={activeSection ?? sections[0]}
              onChange={setSelectedSection}
            />
          </View>
        ) : null}
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
        >
          {saveNotice ? (
            <NoticeBox tone="red" icon="alert-circle-outline">
              {saveNotice.join("\n")}
            </NoticeBox>
          ) : null}
          {renderSection()}
        </ScrollView>
      </>
    );
  }

  // ───────────── 吸底保存栏：写明改了什么，没改动时灰掉 ─────────────
  const summary = editor.changeSummary;
  const { permissionNameResolver, storeOptions } = editor;
  const changeLine = useMemo(() => {
    const storeName = (guid: string) => {
      const store = storeOptions.find((item) => item.storeGUID === guid);
      return store?.storeName || store?.storeCode || guid;
    };
    const described = describeAccessChanges(
      summary.items,
      {
        store: (item) =>
          t("changes.store", {
            store: storeName(item.storeGuid),
            from: t(`stores.${item.from}`),
            to: t(`stores.${item.to}`),
          }),
        role: (item) =>
          t(item.change === "added" ? "changes.roleAdded" : "changes.roleRemoved", {
            name: localizeAccessRoleName(item.roleName, language),
          }),
        permission: (item) =>
          t(item.change === "added" ? "changes.permissionAdded" : "changes.permissionRemoved", {
            name: permissionNameResolver(item.code),
          }),
      },
      2,
    );
    const parts = [...described.parts];
    if (summary.managerDerived) {
      parts.push(t(summary.managerDerived === "gain" ? "saveBar.managerGain" : "saveBar.managerLose"));
    }
    const line = parts.join(t("saveBar.partSeparator"));
    return described.hiddenCount > 0 ? t("saveBar.moreChanges", { parts: line }) : line;
  }, [language, permissionNameResolver, storeOptions, summary, t]);

  const dirty = editor.dirty;
  const barSummary = editor.saving
    ? t("saveBar.saving")
    : dirty
      ? t("saveBar.dirty", { count: Math.max(summary.total, 1) })
      : t("saveBar.clean");
  const barHint = dirty
    ? changeLine
    : activeSection === "permissions"
      ? t("saveBar.hintPermissions")
      : t("saveBar.hintClean");

  const onBarLayout = (event: LayoutChangeEvent) => setBarHeight(event.nativeEvent.layout.height);

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right", "bottom"]}>
      <Stack.Screen options={{ headerBackButtonMenuEnabled: false }} />
      <View style={styles.header}>
        <IconButton
          icon="chevron-left"
          size={26}
          accessibilityLabel={t("actions.back")}
          disabled={editor.busy}
          onPress={handleBack}
        />
        <View style={styles.headerText}>
          <Text accessibilityRole="header" style={styles.title} numberOfLines={1}>
            {t("title")}
          </Text>
          <Text style={styles.subtitle} numberOfLines={1}>
            {target.name.primary || target.username || t("unknownUser")}
            {target.name.nickname ? (
              <Text style={styles.subtitleNickname}>{` 「${target.name.nickname}」`}</Text>
            ) : null}
            {target.username ? ` · @${target.username}` : ""}
          </Text>
        </View>
      </View>
      <View style={styles.body}>{content}</View>
      {ready && sections.length > 0 ? (
        <View onLayout={onBarLayout}>
          <StickyActionBar summary={barSummary} hint={barHint}>
            <Button
              mode="contained"
              loading={editor.saving}
              disabled={!dirty || editor.busy}
              onPress={() => void handleSave()}
              style={styles.saveButton}
              contentStyle={BUSINESS_UI.buttonContent}
              accessibilityLabel={t("actions.save")}
              testID="access-save-button"
            >
              {t("actions.save")}
            </Button>
          </StickyActionBar>
        </View>
      ) : null}
      {copyDirection ? (
        <PermissionCopySheet
          initialDirection={copyDirection}
          currentEmployeeGuid={userGuid}
          currentEmployeeName={target.name.primary || target.username}
          storeFilter={copyStoreFilter}
          actorUserGuid={editor.actorUserGuid}
          resolvePermissionName={editor.permissionNameResolver}
          onPreview={editor.previewCopy}
          onCommit={editor.commitCopy}
          onDismiss={() => setCopyDirection(null)}
          onCopied={handleCopied}
        />
      ) : null}
      <Snackbar
        visible={Boolean(snackbarMessage)}
        duration={2800}
        wrapperStyle={{ bottom: ready ? barHeight : 0 }}
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
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    minHeight: 60,
    paddingRight: HB_SPACING.md,
  },
  headerText: { flex: 1, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 19, fontWeight: "800", lineHeight: 25 },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 17 },
  subtitleNickname: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "600" },
  tabsWrap: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, paddingBottom: HB_SPACING.xxs },
  scrollContent: {
    gap: 10,
    paddingBottom: HB_SPACING.lg,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
  },
  centerState: { alignItems: "center", flex: 1, gap: 12, justifyContent: "center", padding: 24 },
  sectionSpinner: { paddingVertical: HB_SPACING.lg },
  sectionNotice: {
    color: HB_COLORS.textSecondary,
    paddingVertical: HB_SPACING.lg,
    textAlign: "center",
  },
  secondaryText: { color: HB_COLORS.textSecondary },
  saveButton: { ...BUSINESS_UI.button, minWidth: 104 },
});
