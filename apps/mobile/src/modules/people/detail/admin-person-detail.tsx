import { useCallback, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import { useRouter } from "expo-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { fetchIdentityUserDetail, fetchUserAccessStores, getIdentityAdminErrorMeta, updateIdentityUserPassword } from "@/modules/identity-admin/api";
import { isUncertainIdentityWrite } from "@/modules/identity-admin/user-logic";
import { useIdentitySession, useSetIdentityUserLoginEnabled } from "@/modules/identity-admin/user-hooks";
import { SwitchRow } from "@/modules/people/form/form-ui";
import { ActionRow, InfoCard, KvRow, NoticeBox, PeopleTag, SectionTitle, resolvePersonName } from "@/modules/people";
import { isStoreManageable } from "@/modules/shop/store-scope";
import { useStoreUserMutations, toSafeStoreUserErrorLog } from "@/modules/users";
import { isStoreManagerRoleName } from "@/modules/users/access-management";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { StaffPasswordSetupDialog } from "@/modules/users/staff-password-setup-dialog";
import { StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { usePrinterStore } from "@/modules/printer/state";
import type { StoreUserListItem } from "@/modules/users/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { fetchActiveSessionSummary, fetchDirectPermissionSummary, updateIdentityUserStatus } from "./admin-detail-api";
import {
  buildAccessRoute,
  buildEditRoute,
  buildLoginRecordsRoute,
  buildPosRoute,
  type DetailRouteTarget,
} from "./detail-navigation";
import {
  buildDetailLayout,
  classifyLoginToggleFailure,
  formatLocalDate,
  formatLocalDateTime,
  isStoreManagerRoleDerived,
  resolveDetailPermissions,
  resolveEmailStoreCode,
  resolveLoginSwitchIntent,
  resolveLoginToggle,
  selectPosStores,
  shortenGuid,
  shouldHintSetPassword,
  summarizeStoreRelations,
  type MoreItemKey,
  type StatusIntent,
} from "./detail-rules";
import { DetailScaffold } from "./detail-scaffold";
import { DetailAppBar, DetailStateScreen, detailStyles } from "./detail-ui";
import type { DetailEnvironment } from "./store-person-detail";
import { MoreActionsSheet, type MoreRowModel } from "./more-actions-sheet";
import { ConfirmDialog, PeopleSheet } from "./people-sheet";
import { PersonHeader } from "./person-header";
import { buildPrefillTitle, personFromIdentityUser, type DetailRouteParams } from "./person-view";

/**
 * 管理员版人员详情（A2②）：走 identity-admin 接口（需要 account 会话 + Users.View，设备模式屏蔽）。
 * 头部同店长版，右上角 ✎ 编辑 + ⋮ 更多；分店与权限 → 账号与安全 → 底部「停用账号」。
 *
 * 「允许登录」：GET /Users/guid/{guid} 返回 loginEnabled，PUT /Users/guid/{guid}/login-enabled（Users.Edit）开关它，
 * 这样被设成店长（员工管理看不到）又被限制登录的账号，管理员也能在这里解开。
 * 行的形态（隐藏 / 只读 / 本人置灰 / 可操作）全部由 detail-rules.resolveLoginToggle 决定；
 * 接口没返回 loginEnabled（旧后端）时整行不出现。关闭先弹确认框，开启直接提交；
 * 开启成功且账号从没登录过时，在卡内提示「可能没有可用密码」并带「设置密码」入口。
 */
export function AdminPersonDetail({
  userGuid,
  storeCode,
  prefill,
  env,
}: {
  userGuid: string;
  storeCode?: string;
  prefill: DetailRouteParams["prefill"];
  env: DetailEnvironment;
}) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["userManagement", "peopleDetail", "common"]);
  const { actorKey, access, user: currentUser, isAuthenticated } = useIdentitySession();
  const printer = usePrinterStore((state) => state.savedPrinter);
  const printerStatus = usePrinterStore((state) => state.status);

  const detailQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "user", userGuid],
    queryFn: ({ signal }) => fetchIdentityUserDetail(userGuid, actorKey, signal),
    enabled: Boolean(userGuid),
    retry: false,
  });
  const target = detailQuery.data;
  const person = useMemo(() => (target ? personFromIdentityUser(target) : null), [target]);
  const linkedStoresQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "user-stores", userGuid],
    queryFn: ({ signal }) => fetchUserAccessStores(userGuid, actorKey, signal),
    enabled: Boolean(userGuid) && Boolean(target),
    retry: false,
  });
  const linkedStores = useMemo(() => linkedStoresQuery.data ?? [], [linkedStoresQuery.data]);

  const emailStoreCode = resolveEmailStoreCode(storeCode, linkedStores);
  const { setupEmailMutation } = useStoreUserMutations(emailStoreCode, undefined);

  const [moreVisible, setMoreVisible] = useState(false);
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [passwordBusy, setPasswordBusy] = useState(false);
  const [barcodeVisible, setBarcodeVisible] = useState(false);
  const [statusIntent, setStatusIntent] = useState<StatusIntent | null>(null);
  const [statusBusy, setStatusBusy] = useState(false);
  const loginMutation = useSetIdentityUserLoginEnabled(actorKey);
  // 「允许登录」关闭前的确认框；开启不需要确认。
  const [loginIntent, setLoginIntent] = useState<"disable" | null>(null);
  // 开启登录后的「设置密码」提示：只在这一次开启成功且账号没登录过时置位，页面内有效。
  const [passwordHint, setPasswordHint] = useState(false);
  const [posPickerVisible, setPosPickerVisible] = useState(false);
  const [snackbar, setSnackbar] = useState("");

  const barcodeStoreCode = emailStoreCode ?? "";
  const permissions = useMemo(() => {
    if (!person) return null;
    return resolveDetailPermissions({
      variant: "admin",
      actor: {
        userGuid: actorKey,
        roleNames: currentUser?.roleNames ?? [],
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        isStoreLevelManager: access.isStoreLevelManager,
        hasPermission: access.hasPermission,
      },
      target: { userGuid: person.userGuid, active: person.active, roleNames: person.roleNames },
      scope: {
        authenticated: isAuthenticated,
        isDeviceMode: env.isDeviceMode,
        // 管理员版走全局接口：范围由全局码与受保护目标规则裁决，不再按「当前选中的分店」限制。
        storeManageable: true,
        barcodeStoreAllowed: Boolean(barcodeStoreCode)
          && isStoreManageable(barcodeStoreCode, env.manageableStores)
          && env.posEnabledStores.some((store) => store.storeCode === barcodeStoreCode),
        hasManageableStores: env.manageableStores.length > 0,
        hasStoreContext: Boolean(emailStoreCode),
      },
    });
  }, [access, actorKey, barcodeStoreCode, currentUser?.roleNames, emailStoreCode, env.isDeviceMode, env.manageableStores, env.posEnabledStores, isAuthenticated, person]);
  const layout = useMemo(() => (permissions && person ? buildDetailLayout(permissions, person.active) : null), [permissions, person]);
  // 「允许登录」行的形态：复用上面的 canEdit / isSelf，不另起权限判断。
  const loginToggle = useMemo(
    () => (permissions ? resolveLoginToggle(permissions, { loginEnabled: person?.loginEnabled, isDeviceMode: env.isDeviceMode }) : "hidden"),
    [env.isDeviceMode, permissions, person?.loginEnabled],
  );

  const directQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "user-direct-permissions", userGuid],
    queryFn: ({ signal }) => fetchDirectPermissionSummary(userGuid, actorKey, signal),
    enabled: Boolean(permissions?.canOpenAccess) && permissions?.access.permissionsMode !== "hidden",
    retry: false,
  });
  const sessionsQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "login-summary", userGuid],
    queryFn: ({ signal }) => fetchActiveSessionSummary(userGuid, actorKey, signal),
    enabled: Boolean(permissions?.canViewLoginRecords),
    retry: false,
  });

  const handleBack = useCallback(() => {
    if (router.canGoBack()) {
      router.back();
      return;
    }
    router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router]);
  const push = useCallback(
    (route: { pathname: string; params: Record<string, string> }) => router.push(route as unknown as Parameters<typeof router.push>[0]),
    [router],
  );

  const name = person ? resolvePersonName(person) : null;
  const primaryName = name?.primary;
  // 下游页面只拿它做首屏预填，权限判断仍由它们自己拉取的目标数据决定。
  const routeTarget = useMemo<DetailRouteTarget>(() => ({
    userGuid,
    storeCode,
    userName: primaryName,
    username: person?.username,
    active: person?.active,
    roleNames: person?.roleNames,
  }), [person?.active, person?.roleNames, person?.username, primaryName, storeCode, userGuid]);
  const storeSummary = useMemo(() => summarizeStoreRelations(linkedStores), [linkedStores]);
  // 收银权限按分店设置：店长级管理者只能选自己管理的分店；其他人可选目标的全部关联分店。
  const posStores = useMemo(
    () => selectPosStores(linkedStores, {
      restrictToGuids: access.isStoreLevelManager
        ? (currentUser?.stores ?? []).filter((store) => store.isPrimary === true && store.storeGUID).map((store) => store.storeGUID as string)
        : null,
    }),
    [access.isStoreLevelManager, currentUser?.stores, linkedStores],
  );

  const openAccess = useCallback((section: "stores" | "roles" | "permissions", copy = false) => {
    if (!permissions?.canOpenAccess) return;
    push(buildAccessRoute(routeTarget, { section, copy }));
  }, [permissions?.canOpenAccess, push, routeTarget]);
  const openPosForStore = useCallback((store: { storeCode: string; storeGUID: string; storeName: string }) => {
    setPosPickerVisible(false);
    push(buildPosRoute({ ...routeTarget, storeCode: store.storeCode }, { storeGuid: store.storeGUID, storeName: store.storeName }));
  }, [push, routeTarget]);
  const openPos = useCallback(() => {
    if (!permissions?.canOpenPos) return;
    if (posStores.length === 1) openPosForStore(posStores[0]);
    else if (posStores.length > 1) setPosPickerVisible(true);
    else setSnackbar(t("peopleDetail:account.noPosStore"));
  }, [openPosForStore, permissions?.canOpenPos, posStores, t]);
  const openEdit = useCallback(() => {
    if (!permissions?.canEdit) return;
    push(buildEditRoute({ userGuid, storeCode }));
  }, [permissions?.canEdit, push, storeCode, userGuid]);

  const handleMoreSelect = useCallback((key: MoreItemKey) => {
    setMoreVisible(false);
    if (key === "barcode") setBarcodeVisible(true);
    else if (key === "access") openAccess("stores");
    else if (key === "pos") openPos();
    else if (key === "copyPermissions") openAccess("permissions", true);
    else if (key === "loginRecords") push(buildLoginRecordsRoute(userGuid));
  }, [openAccess, openPos, push, userGuid]);

  /** 提交前核对：会话仍是发起操作时的那个账号，且仍持有所需权限（换账号 / 被撤权后不能继续写）。 */
  const isCurrentSession = useCallback((permission: string) => {
    const state = useAuthStore.getState();
    return state.user?.userGUID === actorKey
      && state.sessionKind === "account"
      && state.isAuthenticated
      && !state.iosReviewOfflineGuardActive
      && state.access.hasPermission(permission);
  }, [actorKey]);

  const invalidateIdentity = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey] }),
      // 员工侧列表 / 详情也要同步（员工侧 query key 以 storeUsers / storeUserProfile 开头）。
      queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
      queryClient.invalidateQueries({ queryKey: ["storeUserProfile"] }),
    ]);
  }, [actorKey, queryClient]);

  const handleSendSetupEmail = useCallback(async (email?: string) => {
    if (!emailStoreCode || !permissions?.passwordModes.email) return;
    setPasswordError(null);
    try {
      const result = await setupEmailMutation.mutateAsync({ userGuid, storeCode: emailStoreCode, email });
      setPasswordVisible(false);
      setPasswordHint(false);
      setSnackbar(t("messages.passwordSetupEmailSent", { email: result.maskedEmail }));
      if (email) await invalidateIdentity();
    } catch (error) {
      console.warn("[people-detail] password setup email failed", toSafeStoreUserErrorLog(error));
      setPasswordError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.passwordResetFailed" }));
    }
  }, [emailStoreCode, invalidateIdentity, language, permissions?.passwordModes.email, setupEmailMutation, t, userGuid]);

  const handleDirectPassword = useCallback(async (password: string) => {
    if (!permissions?.passwordModes.direct || !isCurrentSession("Users.ResetPassword")) return;
    setPasswordError(null);
    setPasswordBusy(true);
    try {
      await updateIdentityUserPassword(userGuid, { newPassword: password, passwordFormat: "raw" }, actorKey);
      if (!isCurrentSession("Users.ResetPassword")) return;
      setPasswordVisible(false);
      setPasswordHint(false);
      setSnackbar(t("messages.passwordReset"));
    } catch (error) {
      if (!isCurrentSession("Users.ResetPassword")) return;
      if (isUncertainIdentityWrite(error)) {
        // 请求结果未知（超时 / 5xx）：不让用户立刻重复提交，关闭弹层并提示先核对。
        setPasswordVisible(false);
        setSnackbar(t("peopleDetail:password.uncertain"));
      } else {
        const status = getIdentityAdminErrorMeta(error).status;
        setPasswordError(status === 403
          ? t("peopleDetail:password.forbidden")
          : resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.passwordResetFailed" }));
      }
    } finally {
      setPasswordBusy(false);
    }
  }, [actorKey, isCurrentSession, language, permissions?.passwordModes.direct, t, userGuid]);

  const handleConfirmStatus = useCallback(async () => {
    const intent = statusIntent;
    if (!intent || !permissions?.canToggleStatus || !isCurrentSession("Users.Edit")) return;
    setStatusBusy(true);
    try {
      await updateIdentityUserStatus(userGuid, intent === "enable", actorKey);
      setStatusIntent(null);
      await invalidateIdentity();
      setSnackbar(intent === "enable" ? t("messages.userEnabled") : t("messages.userDisabled"));
    } catch (error) {
      console.warn("[people-detail] identity status failed", getIdentityAdminErrorMeta(error));
      setStatusIntent(null);
      // 结果可能未知：刷新详情让页面显示真实状态，再提示。
      await invalidateIdentity();
      setSnackbar(isUncertainIdentityWrite(error)
        ? t("peopleDetail:confirm.uncertain")
        : resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.statusFailed" }));
    } finally {
      setStatusBusy(false);
    }
  }, [actorKey, invalidateIdentity, isCurrentSession, language, permissions?.canToggleStatus, statusIntent, t, userGuid]);

  const openPasswordSetup = useCallback(() => {
    setPasswordError(null);
    setPasswordVisible(true);
  }, []);

  /**
   * 提交「允许登录」开关。只在界面形态为 editable 时放行，并核对会话仍是发起账号且仍持有 Users.Edit。
   * 失败只显示固定的中英文文案（按 errorCode 归类，不展示服务端原文），日志只记脱敏后的错误元数据；
   * 结果未知（超时 / 5xx）时 mutation 已刷新详情，提示用户核对当前状态再处理。
   */
  const submitLoginEnabled = useCallback(async (next: boolean) => {
    if (loginToggle !== "editable" || !isCurrentSession("Users.Edit")) {
      setLoginIntent(null);
      return;
    }
    try {
      await loginMutation.mutateAsync({ userGuid, loginEnabled: next });
      setLoginIntent(null);
      if (!isCurrentSession("Users.Edit")) return;
      setPasswordHint(next && shouldHintSetPassword({ loginEnabled: true, lastLoginAt: person?.lastLoginAt }));
      setSnackbar(next ? t("peopleDetail:loginToggle.enabledDone") : t("peopleDetail:loginToggle.disabledDone"));
    } catch (error) {
      const meta = getIdentityAdminErrorMeta(error);
      // 日志只留状态码与错误码：错误元数据里的文字说明可能是服务端原文（200 + success=false 的业务信封），不进日志。
      console.warn("[people-detail] login enabled failed", { status: meta.status, code: meta.code });
      // 先关确认框再提示，Snackbar 才不会被还没卸载的对话框挡住。
      setLoginIntent(null);
      if (!isCurrentSession("Users.Edit")) return;
      const failure = classifyLoginToggleFailure({ code: meta.code, status: meta.status, uncertain: isUncertainIdentityWrite(error) });
      setSnackbar(t(`peopleDetail:loginToggle.errors.${failure}`));
    }
  }, [isCurrentSession, loginMutation, loginToggle, person?.lastLoginAt, t, userGuid]);

  const handleLoginSwitch = useCallback((next: boolean) => {
    if (loginToggle !== "editable" || loginMutation.isPending) return;
    if (resolveLoginSwitchIntent(next) === "confirmDisable") setLoginIntent("disable");
    else void submitLoginEnabled(true);
  }, [loginMutation.isPending, loginToggle, submitLoginEnabled]);

  const handleCopyId = useCallback(async () => {
    try {
      await Clipboard.setStringAsync(userGuid);
      setSnackbar(t("peopleDetail:account.idCopied"));
    } catch {
      setSnackbar(t("peopleDetail:account.idCopyFailed"));
    }
  }, [t, userGuid]);

  const prefillTitle = buildPrefillTitle(prefill);
  const backLabel = t("common:actions.back");
  const stateAppBar = (
    <DetailAppBar title={prefillTitle?.name ?? t("peopleDetail:title.admin")} backLabel={backLabel} onBack={handleBack} />
  );

  if (!userGuid) {
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <EmptyState
          title={t("peopleDetail:missing.title")}
          description={t("peopleDetail:missing.description")}
          primaryAction={{ label: backLabel, icon: "arrow-left", onPress: handleBack }}
        />
      </DetailStateScreen>
    );
  }
  if (detailQuery.isPending) {
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <ActivityIndicator size="large" />
      </DetailStateScreen>
    );
  }
  if (!target || !person || !permissions || !layout || !name) {
    const status = getIdentityAdminErrorMeta(detailQuery.error).status;
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <EmptyState
          title={t("peopleDetail:loadFailed.title")}
          description={status === 404 ? t("peopleDetail:loadFailed.notFound") : status === 403 ? t("peopleDetail:loadFailed.forbidden") : t("peopleDetail:loadFailed.description")}
          primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: () => void detailQuery.refetch() }}
          secondaryAction={{ label: backLabel, icon: "arrow-left", onPress: handleBack }}
        />
      </DetailStateScreen>
    );
  }

  const emptyValue = "—";
  const roleLabels = person.roleNames.map((roleName) => localizeAccessRoleName(roleName, language));
  const roleDerived = isStoreManagerRoleDerived(person.roleNames, linkedStores, isStoreManagerRoleName);
  const direct = directQuery.data;
  const directValue = direct
    ? direct.implicitAll
      ? t("peopleDetail:rows.directAll")
      : t("peopleDetail:rows.directWithEffective", { count: direct.directCount, effective: direct.effectiveCount })
    : directQuery.isError ? t("peopleDetail:rows.directOpen") : emptyValue;
  const sessions = sessionsQuery.data;
  const sessionsValue = sessions
    ? t(sessions.capped ? "peopleDetail:account.sessionsCapped" : "peopleDetail:account.sessions", { count: sessions.active })
    : sessionsQuery.isError ? t("peopleDetail:account.sessionsOpen") : emptyValue;
  const lastLogin = formatLocalDateTime(person.lastLoginAt);
  const lastLoginText = lastLogin ? [lastLogin, person.lastLoginIp].filter(Boolean).join(" · ") : t("peopleDetail:account.neverLoggedIn");
  const created = formatLocalDate(person.createdAt);
  const updated = formatLocalDate(person.updatedAt);

  const printerSubtitle = printer?.name
    ? t("peopleDetail:more.barcodeConnected", { status: t(`staffBarcode.printerStatuses.${printerStatus}`, printerStatus) })
    : t("peopleDetail:more.barcodeNoPrinter");
  const moreSubtitles: Record<MoreItemKey, string> = {
    barcode: printerSubtitle,
    access: t("peopleDetail:more.accessSubtitle"),
    pos: t("peopleDetail:more.posSubtitleAdmin"),
    copyPermissions: t("peopleDetail:more.copySubtitle"),
    loginRecords: lastLogin ? t("peopleDetail:more.loginRecordsSubtitle", { time: lastLogin }) : t("peopleDetail:account.neverLoggedIn"),
  };
  const moreRows: MoreRowModel[] = layout.moreItems.map((key) => ({
    key,
    title: t(`peopleDetail:more.titles.${key}`),
    subtitle: moreSubtitles[key],
  }));
  const moreStatus = layout.moreStatus
    ? {
        intent: layout.moreStatus,
        title: layout.moreStatus === "disable" ? t("peopleDetail:more.disableTitle") : t("peopleDetail:more.enableTitle"),
        subtitle: layout.moreStatus === "disable" ? t("peopleDetail:more.disableSubtitle") : t("peopleDetail:more.enableSubtitle"),
      }
    : null;

  const roleSubtitle = roleLabels[0] ?? "";
  const appBarActions = [
    ...(layout.headerEdit
      ? [{ key: "edit", icon: "pencil-outline", label: t("peopleDetail:quick.edit"), onPress: openEdit, testID: "person-edit-button" }]
      : []),
    ...(layout.showMore
      ? [{ key: "more", icon: "dots-vertical", label: t("actions.more"), onPress: () => setMoreVisible(true), testID: "person-more-button" }]
      : []),
  ];

  // 条码弹层要的是员工侧条目形状：用管理员接口的数据拼一个（只读，不回写）。
  const barcodeUser: StoreUserListItem = {
    userGUID: person.userGuid,
    username: person.username,
    fullName: person.fullName,
    givenName: person.givenName,
    middleName: person.middleName,
    familyName: person.familyName,
    nickname: person.nickname,
    email: person.email,
    phone: person.phone,
    status: person.active ? 1 : 0,
    roleNames: person.roleNames,
  };

  return (
    <DetailScaffold
      title={t("peopleDetail:title.admin")}
      scrolledTitle={name.primary}
      scrolledSubtitle={[`@${person.username}`, roleSubtitle].filter(Boolean).join(" · ")}
      backLabel={backLabel}
      onBack={handleBack}
      appBarActions={appBarActions}
      snackbarMessage={snackbar}
      onSnackbarDismiss={() => setSnackbar("")}
      overlays={(
        <>
          {moreVisible ? (
            <MoreActionsSheet
              title={name.primary}
              subtitle={[`@${person.username}`, roleSubtitle].filter(Boolean).join(" · ")}
              closeLabel={t("common:actions.close")}
              rows={moreRows}
              status={moreStatus}
              hint={t("peopleDetail:more.hint")}
              onSelect={handleMoreSelect}
              onSelectStatus={(intent) => {
                setMoreVisible(false);
                setStatusIntent(intent);
              }}
              onDismiss={() => setMoreVisible(false)}
            />
          ) : null}
          {posPickerVisible ? (
            <PeopleSheet
              title={t("peopleDetail:account.posPickerTitle")}
              subtitle={name.primary}
              closeLabel={t("common:actions.close")}
              onDismiss={() => setPosPickerVisible(false)}
              testID="person-pos-store-picker"
            >
              <InfoCard>
                {posStores.map((store, index) => (
                  <ActionRow
                    key={store.storeGUID}
                    first={index === 0}
                    icon="storefront-outline"
                    tone="green"
                    title={store.storeName || store.storeCode}
                    subtitle={store.storeCode}
                    onPress={() => openPosForStore(store)}
                  />
                ))}
              </InfoCard>
            </PeopleSheet>
          ) : null}
          <StaffPasswordSetupDialog
            visible={passwordVisible}
            staffName={name.primary}
            loginName={person.username}
            email={person.email}
            pending={setupEmailMutation.isPending || passwordBusy}
            modes={permissions.passwordModes}
            onDismiss={() => setPasswordVisible(false)}
            onSubmit={(email) => void handleSendSetupEmail(email)}
            onSubmitDirectPassword={(password) => void handleDirectPassword(password)}
            errorMessage={passwordError}
          />
          <StaffBarcodeDialog
            actorGuid={actorKey}
            storeCode={barcodeStoreCode}
            user={barcodeVisible && permissions.canPrintBarcode ? barcodeUser : null}
            visible={barcodeVisible && permissions.canPrintBarcode}
            onDismiss={() => setBarcodeVisible(false)}
          />
          {statusIntent ? (
            <ConfirmDialog
              title={t(statusIntent === "disable" ? "peopleDetail:confirm.disableTitle" : "peopleDetail:confirm.enableTitle", { name: name.primary })}
              message={t(statusIntent === "disable" ? "peopleDetail:confirm.disableMessage" : "peopleDetail:confirm.enableMessage", { name: name.primary })}
              cancelLabel={t("actions.cancel")}
              confirmLabel={statusIntent === "disable" ? t("actions.disable") : t("actions.enable")}
              danger={statusIntent === "disable"}
              pending={statusBusy}
              onCancel={() => setStatusIntent(null)}
              onConfirm={() => void handleConfirmStatus()}
              testID="person-status-confirm"
            />
          ) : null}
          {loginIntent === "disable" ? (
            <ConfirmDialog
              title={t("peopleDetail:loginToggle.confirmTitle", { name: name.primary })}
              message={t("peopleDetail:loginToggle.confirmMessage")}
              cancelLabel={t("actions.cancel")}
              confirmLabel={t("peopleDetail:loginToggle.confirmAction")}
              danger
              pending={loginMutation.isPending}
              onCancel={() => setLoginIntent(null)}
              onConfirm={() => void submitLoginEnabled(false)}
              testID="person-login-confirm"
            />
          ) : null}
        </>
      )}
    >
      <PersonHeader
        person={person}
        meta={`@${person.username}`}
        extraTags={(
          <>
            {roleLabels.map((label) => <PeopleTag key={label} label={label} tone="gray" />)}
            {storeSummary.count > 0 ? (
              <PeopleTag
                icon="storefront-outline"
                label={`${storeSummary.firstName}${storeSummary.firstIsManageable ? ` ${t("peopleDetail:relation.manage")}` : ""}`}
                tone="blue"
              />
            ) : null}
          </>
        )}
      />

      <SectionTitle
        title={t("peopleDetail:sections.access")}
        actionLabel={permissions.canOpenAccess ? t("peopleDetail:goAuthorize") : undefined}
        onAction={permissions.canOpenAccess ? () => openAccess("stores") : undefined}
      />
      <InfoCard>
        <KvRow
          first
          label={t("peopleDetail:rows.store")}
          value={(
            <>
              <Text style={detailStyles.rowValueText} numberOfLines={1}>
                {storeSummary.count > 0
                  ? t("peopleDetail:rows.storeSummary", { count: storeSummary.count, name: storeSummary.firstName })
                  : linkedStoresQuery.isLoading ? emptyValue : t("peopleDetail:rows.noStore")}
              </Text>
              {storeSummary.firstIsManageable ? <PeopleTag label={t("peopleDetail:relation.manage")} tone="blue" /> : null}
            </>
          )}
          onPress={permissions.canOpenAccess ? () => openAccess("stores") : undefined}
        />
        <KvRow
          label={t("peopleDetail:rows.role")}
          value={(
            <>
              <Text style={detailStyles.rowValueText} numberOfLines={1}>{roleLabels.join(" / ") || emptyValue}</Text>
              {roleDerived ? <PeopleTag icon="source-branch" label={t("peopleDetail:roleDerived")} tone="violet" /> : null}
            </>
          )}
          onPress={permissions.canOpenAccess ? () => openAccess("roles") : undefined}
        />
        {permissions.canOpenAccess && permissions.access.permissionsMode !== "hidden" ? (
          <KvRow label={t("peopleDetail:rows.direct")} value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{directValue}</Text>} onPress={() => openAccess("permissions")} />
        ) : null}
        {permissions.canOpenPos ? (
          <KvRow label={t("peopleDetail:rows.pos")} value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{t("peopleDetail:rows.posByStore")}</Text>} onPress={openPos} />
        ) : null}
      </InfoCard>

      <SectionTitle title={t("peopleDetail:sections.account")} />
      <InfoCard>
        <KvRow first label={t("peopleDetail:account.lastLogin")} value={<Text style={[detailStyles.rowValueText, lastLogin ? detailStyles.mono : null]} numberOfLines={2}>{lastLoginText}</Text>} />
        {permissions.canViewLoginRecords ? (
          <KvRow
            label={t("peopleDetail:account.loginRecords")}
            value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{sessionsValue}</Text>}
            onPress={() => push(buildLoginRecordsRoute(userGuid))}
          />
        ) : null}
        {loginToggle === "readonly" ? (
          <KvRow
            label={t("peopleDetail:loginToggle.label")}
            value={(
              <Text style={detailStyles.rowValueText} numberOfLines={1} testID="person-login-readonly">
                {person.loginEnabled ? t("peopleDetail:loginToggle.allowedValue") : t("peopleDetail:loginToggle.restrictedValue")}
              </Text>
            )}
          />
        ) : null}
        {loginToggle === "editable" || loginToggle === "selfLocked" ? (
          <SwitchRow
            title={t("peopleDetail:loginToggle.label")}
            description={
              loginToggle === "selfLocked"
                ? t("peopleDetail:loginToggle.selfHint")
                : person.loginEnabled ? t("peopleDetail:loginToggle.onHint") : t("peopleDetail:loginToggle.offHint")
            }
            value={person.loginEnabled === true}
            onValueChange={handleLoginSwitch}
            disabled={loginToggle === "selfLocked" || loginMutation.isPending}
            testID="person-login-switch"
          />
        ) : null}
        {passwordHint && shouldHintSetPassword({ loginEnabled: person.loginEnabled, lastLoginAt: person.lastLoginAt }) ? (
          <View style={styles.passwordHint} testID="person-login-password-hint">
            <NoticeBox tone="amber" icon="information-outline">{t("peopleDetail:loginToggle.noPasswordNotice")}</NoticeBox>
            {permissions.canResetPassword ? (
              <Button
                mode="contained-tonal"
                compact
                onPress={openPasswordSetup}
                style={styles.passwordHintButton}
                testID="person-login-set-password"
              >
                {t("peopleDetail:loginToggle.setPassword")}
              </Button>
            ) : null}
          </View>
        ) : null}
        {permissions.canResetPassword ? (
          <KvRow
            label={t("peopleDetail:account.resetPassword")}
            value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{t("peopleDetail:account.resetPasswordHint")}</Text>}
            onPress={openPasswordSetup}
          />
        ) : null}
        <KvRow
          label={t("peopleDetail:account.id")}
          value={<Text style={[detailStyles.rowValueText, detailStyles.mono]} numberOfLines={1}>{shortenGuid(userGuid)}</Text>}
          trailing={(
            <Pressable accessibilityRole="button" accessibilityLabel={t("peopleDetail:account.copyId")} hitSlop={10} onPress={() => void handleCopyId()} testID="person-copy-id">
              <Icon source="content-copy" size={18} color={HB_COLORS.action} />
            </Pressable>
          )}
        />
        <KvRow
          label={t("peopleDetail:account.createdUpdated")}
          value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{[created ?? emptyValue, updated ?? emptyValue].join(" · ")}</Text>}
        />
      </InfoCard>

      {permissions.canToggleStatus ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={person.active ? t("peopleDetail:account.disableAccount") : t("peopleDetail:account.enableAccount")}
          onPress={() => setStatusIntent(person.active ? "disable" : "enable")}
          style={[styles.statusButton, person.active ? styles.statusButtonDanger : styles.statusButtonSafe]}
          testID="person-footer-status"
        >
          <Icon source={person.active ? "account-off-outline" : "account-check-outline"} size={19} color={person.active ? HB_COLORS.danger : HB_COLORS.success} />
          <Text style={person.active ? detailStyles.dangerButtonText : styles.enableButtonText}>
            {person.active ? t("peopleDetail:account.disableAccount") : t("peopleDetail:account.enableAccount")}
          </Text>
        </Pressable>
      ) : null}
    </DetailScaffold>
  );
}

const styles = StyleSheet.create({
  passwordHint: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, gap: HB_SPACING.xs, padding: 12 },
  passwordHintButton: { alignSelf: "flex-start", borderRadius: HB_RADIUS.control },
  statusButton: { alignItems: "center", borderRadius: 12, borderWidth: 1, flexDirection: "row", gap: 8, justifyContent: "center", marginTop: 4, minHeight: 48 },
  statusButtonDanger: { backgroundColor: "#FEF3F2", borderColor: "#FDA29B" },
  statusButtonSafe: { backgroundColor: "#ECFDF3", borderColor: "#ABEFC6" },
  enableButtonText: { color: HB_COLORS.success, fontSize: 15, fontWeight: "800" },
});
