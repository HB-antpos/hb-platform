import { useCallback, useEffect, useMemo, useState } from "react";
import { BackHandler, StyleSheet, View } from "react-native";
import { useNavigation, useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { getEmployeeProfileReviewAccess } from "@/modules/employee-profile-review/access";
import { getEmployeeProfileReviewRequestsApi } from "@/modules/employee-profile-review/api";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { InfoCard, KvRow, PeopleTag, SectionTitle, resolvePersonName } from "@/modules/people";
import type { Store } from "@/modules/shop/types";
import { isStoreManageable } from "@/modules/shop/store-scope";
import {
  toSafeStoreUserErrorLog,
  useStoreUserMutations,
  useStoreUserPosTerminalPermissions,
  useStoreUserProfile,
} from "@/modules/users";
import { useUserAccessPermissionState, useUserAccessStores } from "@/modules/users/access-management-hooks";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { getEffectivePosPermissionCodes } from "@/modules/users/pos-terminal-permissions";
import { calculateAge, maskTrailingFour } from "@/modules/users/profile-display";
import { getDisplayableEmail } from "@/modules/users/staff-email-username";
import { StaffPasswordSetupDialog } from "@/modules/users/staff-password-setup-dialog";
import { StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { usePrinterStore } from "@/modules/printer/state";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import {
  buildAccessRoute,
  buildEditRoute,
  buildPosRoute,
  type DetailRouteTarget,
} from "./detail-navigation";
import {
  buildDetailLayout,
  buildNameDisplayParts,
  resolveDetailPermissions,
  resolveScheduleEntries,
  summarizeRecordCount,
  summarizeWeekSchedule,
  type MoreItemKey,
  type StatusIntent,
} from "./detail-rules";
import { DetailScaffold } from "./detail-scaffold";
import { DetailAppBar, DetailStateScreen, detailStyles } from "./detail-ui";
import { MinorGuardianSection } from "./minor-guardian-section";
import { MoreActionsSheet, type MoreRowModel } from "./more-actions-sheet";
import { ConfirmDialog } from "./people-sheet";
import { PersonHeader } from "./person-header";
import { ManagedProfileEntry } from "../profiles/managed-profile-entry";
import { buildPrefillTitle, personFromStoreProfile, type DetailRouteParams } from "./person-view";
import { ScheduleRecordsView } from "./schedule-records-view";
import {
  STAFF_RECORDS_LIMIT,
  getCurrentWeekStartDate,
  useStaffRecordsQuery,
  useStaffWeekQuery,
} from "./staff-attendance-hooks";

/** 页面外壳（PersonDetailScreen）按会话算好后传进来的分店范围。 */
export interface DetailEnvironment {
  isDeviceMode: boolean;
  stores: Store[];
  manageableStores: Store[];
  posEnabledStores: Store[];
}

type DetailView = "main" | "schedule" | "records";

/**
 * 店长版人员详情（A2①）：走 store-users 接口，必须带 storeCode。
 * 头部 4 个快捷操作 → 分店与权限 → 员工资料 → 排班与记录；没有权限的入口直接不显示。
 */
export function StorePersonDetail({
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
  const navigation = useNavigation();
  const { t, language } = useAppTranslation(["userManagement", "peopleDetail", "attendance", "common"]);
  const locale = useMemo(() => resolveLocaleTag(language), [language]);
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const printer = usePrinterStore((state) => state.savedPrinter);
  const printerStatus = usePrinterStore((state) => state.status);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";

  const profileQuery = useStoreUserProfile(userGuid, storeCode);
  const profile = profileQuery.data;
  const person = useMemo(() => (profile ? personFromStoreProfile(profile) : null), [profile]);
  const { statusMutation, setupEmailMutation } = useStoreUserMutations(storeCode, undefined);

  const [view, setView] = useState<DetailView>("main");
  const [moreVisible, setMoreVisible] = useState(false);
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [barcodeVisible, setBarcodeVisible] = useState(false);
  const [statusIntent, setStatusIntent] = useState<StatusIntent | null>(null);
  const [snackbar, setSnackbar] = useState("");
  const [showSensitive, setShowSensitive] = useState(false);
  const [showOtherProfile, setShowOtherProfile] = useState(false);

  // ───────── 权限（所有「能不能做」都由纯函数 resolveDetailPermissions 决定）─────────
  const storeManageable = isStoreManageable(storeCode, env.manageableStores);
  const permissions = useMemo(() => {
    if (!person) return null;
    return resolveDetailPermissions({
      variant: "store",
      actor: {
        userGuid: actorGuid,
        roleNames: currentUser?.roleNames ?? [],
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        isWarehouseManager: access.isWarehouseManager,
        isStoreLevelManager: access.isStoreLevelManager,
        hasPermission: access.hasPermission,
      },
      target: { userGuid: person.userGuid, active: person.active, roleNames: person.roleNames },
      scope: {
        authenticated,
        isDeviceMode: env.isDeviceMode,
        storeManageable,
        barcodeStoreAllowed: storeManageable && env.posEnabledStores.some((store) => store.storeCode === storeCode),
        hasManageableStores: env.manageableStores.length > 0,
        hasStoreContext: Boolean(storeCode),
      },
    });
  }, [access, actorGuid, authenticated, currentUser?.roleNames, env.isDeviceMode, env.manageableStores.length, env.posEnabledStores, person, storeCode, storeManageable]);
  const layout = useMemo(() => (permissions && person ? buildDetailLayout(permissions, person.active) : null), [permissions, person]);

  // ───────── 分店与权限摘要（只在有授权资格时才请求，避免无谓的 403）─────────
  const accessSummaryEnabled = Boolean(permissions?.canOpenAccess);
  const accessStoresQuery = useUserAccessStores(userGuid, accessSummaryEnabled);
  const permissionStateQuery = useUserAccessPermissionState(
    userGuid,
    accessSummaryEnabled && permissions?.access.permissionsMode !== "hidden",
  );
  const storeGuid = env.stores.find((store) => store.storeCode === storeCode)?.storeGUID;
  const posQuery = useStoreUserPosTerminalPermissions(userGuid, storeGuid, Boolean(permissions?.canOpenPos));

  // ───────── 排班与记录（摘要与完整视图共用缓存）─────────
  const scheduleEntries = resolveScheduleEntries({ variant: "store", hasStoreCode: Boolean(storeCode) });
  const weekStartDate = useMemo(() => getCurrentWeekStartDate(), []);
  const attendanceEnabled = Boolean(person) && scheduleEntries.length > 0;
  const weekQuery = useStaffWeekQuery(userGuid, storeCode, weekStartDate, attendanceEnabled);
  const recordsQuery = useStaffRecordsQuery(userGuid, storeCode, attendanceEnabled);
  const weekSummary = useMemo(() => (weekQuery.data ? summarizeWeekSchedule(weekQuery.data, new Date()) : null), [weekQuery.data]);
  const recordSummary = useMemo(
    () => (recordsQuery.data ? summarizeRecordCount(recordsQuery.data, new Date(), { limit: STAFF_RECORDS_LIMIT }) : null),
    [recordsQuery.data],
  );

  // ───────── 敏感资料待审核（与审核列表同一套前端门禁；最终范围由后端裁决）─────────
  const navigationItems = useAppNavigationStore((state) => state.items);
  const navigationReady = useAppNavigationStore((state) => state.isReady);
  const reviewAccess = useMemo(
    () => getEmployeeProfileReviewAccess({
      roleNames: currentUser?.roleNames,
      permissions: currentUser?.permissions,
      menuRouteNames: navigationItems.map((item) => item.routeName),
      sessionKind,
    }),
    [currentUser?.permissions, currentUser?.roleNames, navigationItems, sessionKind],
  );
  const pendingSensitiveQuery = useQuery({
    queryKey: ["employeeProfileReview", "requests", "byUser", userGuid],
    enabled: Boolean(userGuid) && navigationReady && reviewAccess.allowed,
    // 只取一条摘要：列表接口不含任何敏感值，超出审核范围时后端返回空列表。
    queryFn: () => getEmployeeProfileReviewRequestsApi({ page: 1, pageSize: 1, status: "Pending", userGuid }),
    staleTime: 30_000,
  });
  const pendingSensitiveRequest = pendingSensitiveQuery.data?.items[0];

  // ───────── 页内视图切换：Android 返回键先回主视图；子视图期间关闭 iOS 侧滑返回 ─────────
  useEffect(() => {
    if (view === "main") return undefined;
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      setView("main");
      return true;
    });
    return () => subscription.remove();
  }, [view]);
  useEffect(() => {
    navigation.setOptions({ gestureEnabled: view === "main" });
  }, [navigation, view]);

  const handleBack = useCallback(() => {
    if (view !== "main") {
      setView("main");
      return;
    }
    if (router.canGoBack()) {
      router.back();
      return;
    }
    router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router, view]);

  const name = person ? resolvePersonName(person) : null;
  const primaryName = name?.primary;
  // 下游页面（授权 / 收银权限）只拿它做首屏预填，权限判断仍由它们自己拉取的目标数据决定。
  const routeTarget = useMemo<DetailRouteTarget>(() => ({
    userGuid,
    storeCode,
    userName: primaryName,
    username: person?.username,
    active: person?.active,
    roleNames: person?.roleNames,
  }), [person?.active, person?.roleNames, person?.username, primaryName, storeCode, userGuid]);
  const push = useCallback(
    (route: { pathname: string; params: Record<string, string> }) => router.push(route as unknown as Parameters<typeof router.push>[0]),
    [router],
  );

  const openAccess = useCallback((section: "stores" | "roles" | "permissions", copy = false) => {
    if (!permissions?.canOpenAccess) return;
    push(buildAccessRoute(routeTarget, { section, copy }));
  }, [permissions?.canOpenAccess, push, routeTarget]);
  const openPos = useCallback(() => {
    if (!permissions?.canOpenPos || !storeCode) return;
    push(buildPosRoute({ ...routeTarget, storeCode }, {
      storeGuid,
      storeName: env.stores.find((store) => store.storeCode === storeCode)?.storeName,
    }));
  }, [env.stores, permissions?.canOpenPos, push, routeTarget, storeCode, storeGuid]);
  const openEdit = useCallback(() => {
    if (!permissions?.canEdit) return;
    push(buildEditRoute({ userGuid, storeCode }));
  }, [permissions?.canEdit, push, storeCode, userGuid]);

  const handleMoreSelect = useCallback((key: MoreItemKey) => {
    // 先关「更多」弹层，再打开下一个：都是 Paper Portal 弹层，不存在 iOS 原生 Modal 连续切换被吞的问题。
    setMoreVisible(false);
    if (key === "barcode") setBarcodeVisible(true);
    else if (key === "access") openAccess("stores");
    else if (key === "pos") openPos();
    else if (key === "copyPermissions") openAccess("permissions", true);
  }, [openAccess, openPos]);

  const handleSendSetupEmail = useCallback(async (email?: string) => {
    if (!storeCode || !permissions?.passwordModes.email) return;
    setPasswordError(null);
    // 店长不再手动设新密码：给员工邮箱发验证码，员工在登录页自己设置。
    // 老账号没有可用邮箱时 email 为就地补填的邮箱，后端先保存再发送。
    try {
      const result = await setupEmailMutation.mutateAsync({ userGuid, storeCode, email });
      setPasswordVisible(false);
      setSnackbar(t("messages.passwordSetupEmailSent", { email: result.maskedEmail }));
    } catch (error) {
      console.warn("[people-detail] password setup email failed", toSafeStoreUserErrorLog(error));
      // 弹层保持打开，原因就地显示（Snackbar 会被底部弹层盖住）。
      setPasswordError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.passwordResetFailed" }));
    }
  }, [language, permissions?.passwordModes.email, setupEmailMutation, storeCode, t, userGuid]);

  const handleConfirmStatus = useCallback(async () => {
    const intent = statusIntent;
    if (!intent || !storeCode || !permissions?.canToggleStatus) return;
    try {
      await statusMutation.mutateAsync({ userGuid, storeCode, status: intent === "enable" ? 1 : 0 });
      setStatusIntent(null);
      setSnackbar(intent === "enable" ? t("messages.userEnabled") : t("messages.userDisabled"));
    } catch (error) {
      console.warn("[people-detail] status failed", toSafeStoreUserErrorLog(error));
      setStatusIntent(null);
      setSnackbar(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.statusFailed" }));
    }
  }, [language, permissions?.canToggleStatus, statusIntent, statusMutation, storeCode, t, userGuid]);

  const prefillTitle = buildPrefillTitle(prefill);
  const backLabel = t("common:actions.back");
  const stateAppBar = (
    <DetailAppBar title={prefillTitle?.name ?? t("peopleDetail:title.store")} backLabel={backLabel} onBack={handleBack} />
  );

  // ───────── 整页状态 ─────────
  if (!userGuid || !storeCode) {
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <EmptyState
          title={t("peopleDetail:missing.title")}
          description={storeCode ? t("peopleDetail:missing.description") : t("peopleDetail:missing.storeDescription")}
          primaryAction={{ label: backLabel, icon: "arrow-left", onPress: handleBack }}
        />
      </DetailStateScreen>
    );
  }
  if (profileQuery.isLoading && !profile) {
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <ActivityIndicator size="large" />
      </DetailStateScreen>
    );
  }
  if (!profile || !person || !permissions || !layout || !name) {
    return (
      <DetailStateScreen appBar={stateAppBar}>
        <EmptyState
          title={t("peopleDetail:loadFailed.title")}
          description={resolveLocalizedErrorMessage(profileQuery.error, { t, language, fallbackKey: "messages.loadFailedDescription" })}
          primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: () => void profileQuery.refetch() }}
          secondaryAction={{ label: backLabel, icon: "arrow-left", onPress: handleBack }}
        />
      </DetailStateScreen>
    );
  }

  // ───────── 子视图：排班 / 考勤记录完整页 ─────────
  if (view !== "main") {
    return (
      <DetailStateScreen
        appBar={(
          <DetailAppBar
            title={view === "schedule" ? t("detail.schedule.title") : t("detail.records.title")}
            subtitle={name.primary}
            backLabel={backLabel}
            onBack={handleBack}
          />
        )}
      >
        <View style={styles.subViewBody}>
          <ScheduleRecordsView mode={view} weekQuery={weekQuery} recordsQuery={recordsQuery} />
        </View>
      </DetailStateScreen>
    );
  }

  // ───────── 主视图 ─────────
  const emptyValue = t("common:na");
  const employmentLabel = profile.employmentType
    ? t(`detail.employmentTypes.${profile.employmentType}`, profile.employmentType)
    : t("fields.positionValue");
  const roleLabels = person.roleNames.map((roleName) => localizeAccessRoleName(roleName, language));
  const relationStore = accessStoresQuery.data?.find((store) => store.storeCode === storeCode);
  const storeTitle = profile.storeName || profile.storeCode || storeCode;
  const nameParts = buildNameDisplayParts(person)
    .map((part) => (part.kind === "full" ? part.value : `${t(`peopleDetail:profile.nameParts.${part.kind}`)} ${part.value}`))
    .join(" · ");
  const displayEmail = getDisplayableEmail(profile.email);
  const age = calculateAge(profile.birthday);
  const otherSummary = [
    age === null ? null : t("peopleDetail:profile.age", { count: age }),
    profile.gender ? t(`detail.genders.${profile.gender}`, profile.gender) : null,
  ].filter(Boolean).join(" · ");

  const posData = posQuery.data;
  const posValue = posData ? (
    <>
      <Text style={detailStyles.rowValueText}>
        {t("peopleDetail:rows.posCount", { enabled: getEffectivePosPermissionCodes(posData).length, total: posData.assignablePermissions.length })}
      </Text>
      <PeopleTag
        label={posData.mode.toLowerCase() === "override" ? t("peopleDetail:rows.posCustom") : t("peopleDetail:rows.posInherited")}
        tone={posData.mode.toLowerCase() === "override" ? "blue" : "gray"}
      />
    </>
  ) : (
    <Text style={detailStyles.rowMuted}>{posQuery.isError ? t("peopleDetail:rows.posOpen") : emptyValue}</Text>
  );
  const permissionState = permissionStateQuery.data;
  const directValue = permissionState
    ? t("peopleDetail:rows.directCount", { count: permissionState.directPermissionCodes.length })
    : permissionStateQuery.isError ? t("peopleDetail:rows.directOpen") : emptyValue;

  const weekdayLabel = (index: number) => t(`attendance:weekdays.${index}`);
  const scheduleValue = weekSummary
    ? weekSummary.shiftCount === 0
      ? t("peopleDetail:schedule.weekEmpty")
      : [
          t("peopleDetail:schedule.weekCount", { count: weekSummary.shiftCount }),
          weekSummary.next
            ? t("peopleDetail:schedule.next", { weekday: weekdayLabel(weekSummary.next.weekdayIndex), time: weekSummary.next.time })
            : null,
        ].filter(Boolean).join(" · ")
    : weekQuery.isError ? t("peopleDetail:schedule.open") : emptyValue;
  const recordsValue = recordSummary
    ? recordSummary.count === 0
      ? t("peopleDetail:schedule.recordsEmpty")
      : t(recordSummary.capped ? "peopleDetail:schedule.recordsCountCapped" : "peopleDetail:schedule.recordsCount", { count: recordSummary.count })
    : recordsQuery.isError ? t("peopleDetail:schedule.open") : emptyValue;

  const printerSubtitle = printer?.name
    ? t("peopleDetail:more.barcodeConnected", { status: t(`staffBarcode.printerStatuses.${printerStatus}`, printerStatus) })
    : t("peopleDetail:more.barcodeNoPrinter");
  const moreSubtitles: Record<MoreItemKey, string> = {
    barcode: printerSubtitle,
    access: t("peopleDetail:more.accessSubtitle"),
    pos: posData
      ? t("peopleDetail:more.posSubtitle", {
          store: storeTitle,
          enabled: getEffectivePosPermissionCodes(posData).length,
          total: posData.assignablePermissions.length,
        })
      : t("peopleDetail:more.posSubtitleFallback", { store: storeTitle }),
    copyPermissions: t("peopleDetail:more.copySubtitle"),
    loginRecords: "",
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

  const canNavigateAccess = permissions.canOpenAccess;
  const personName = name.primary;

  return (
    <DetailScaffold
      title={t("peopleDetail:title.store")}
      scrolledTitle={personName}
      scrolledSubtitle={`@${person.username}`}
      backLabel={backLabel}
      onBack={handleBack}
      appBarActions={layout.showMore
        ? [{ key: "more", icon: "dots-vertical", label: t("actions.more"), onPress: () => setMoreVisible(true), testID: "person-more-button" }]
        : []}
      snackbarMessage={snackbar}
      onSnackbarDismiss={() => setSnackbar("")}
      overlays={(
        <>
          {moreVisible ? (
            <MoreActionsSheet
              title={personName}
              subtitle={`@${person.username} · ${storeTitle}`}
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
          <StaffPasswordSetupDialog
            visible={passwordVisible}
            staffName={personName}
            loginName={person.username}
            email={profile.email}
            pending={setupEmailMutation.isPending}
            onDismiss={() => setPasswordVisible(false)}
            onSubmit={(email) => void handleSendSetupEmail(email)}
            errorMessage={passwordError}
          />
          <StaffBarcodeDialog
            actorGuid={actorGuid}
            storeCode={storeCode}
            user={barcodeVisible && permissions.canPrintBarcode ? profile : null}
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
              pending={statusMutation.isPending}
              onCancel={() => setStatusIntent(null)}
              onConfirm={() => void handleConfirmStatus()}
              testID="person-status-confirm"
            />
          ) : null}
        </>
      )}
    >
      <PersonHeader
        person={person}
        meta={[`@${person.username}`, employmentLabel].join(" · ")}
        extraTags={(
          <>
            {roleLabels.map((label) => <PeopleTag key={label} label={label} tone="gray" />)}
            <PeopleTag
              icon="storefront-outline"
              label={relationStore ? `${storeTitle} ${relationStore.isManageable ? t("peopleDetail:relation.manage") : t("peopleDetail:relation.view")}` : storeTitle}
              tone="blue"
            />
          </>
        )}
        quick={{
          layout,
          statusPending: statusMutation.isPending,
          onEdit: openEdit,
          onPassword: () => {
            setPasswordError(null);
            setPasswordVisible(true);
          },
          onBarcode: () => setBarcodeVisible(true),
          onStatus: setStatusIntent,
        }}
      />

      <SectionTitle title={t("peopleDetail:sections.access")} />
      <InfoCard>
        <KvRow
          first
          label={t("peopleDetail:rows.store")}
          value={(
            <>
              <Text style={detailStyles.rowValueText} numberOfLines={1}>{storeTitle}</Text>
              {relationStore ? (
                <PeopleTag label={relationStore.isManageable ? t("peopleDetail:relation.manage") : t("peopleDetail:relation.view")} tone={relationStore.isManageable ? "blue" : "gray"} />
              ) : null}
            </>
          )}
          onPress={canNavigateAccess ? () => openAccess("stores") : undefined}
        />
        <KvRow
          label={t("peopleDetail:rows.role")}
          value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{roleLabels.join(" / ") || emptyValue}</Text>}
          onPress={canNavigateAccess ? () => openAccess("roles") : undefined}
        />
        {permissions.canOpenPos ? (
          <KvRow label={t("peopleDetail:rows.pos")} value={posValue} onPress={openPos} />
        ) : null}
        {canNavigateAccess && permissions.access.permissionsMode !== "hidden" ? (
          <KvRow label={t("peopleDetail:rows.direct")} value={<Text style={detailStyles.rowValueText}>{directValue}</Text>} onPress={() => openAccess("permissions")} />
        ) : null}
      </InfoCard>

      <SectionTitle title={t("peopleDetail:sections.profile")} />
      <InfoCard>
        <KvRow first label={t("peopleDetail:profile.name")} value={<Text style={detailStyles.rowValueText} numberOfLines={2}>{nameParts || emptyValue}</Text>} />
        <KvRow
          label={t("peopleDetail:profile.contact")}
          value={(
            <Text style={detailStyles.rowValueText} numberOfLines={2}>
              {[profile.phone || null, displayEmail || t("detail.emailNotSetCannotRecover")].filter(Boolean).join(" · ")}
            </Text>
          )}
        />
        <KvRow
          label={t("peopleDetail:profile.other")}
          value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{otherSummary || emptyValue}</Text>}
          onPress={() => setShowOtherProfile((current) => !current)}
        />
        {showOtherProfile ? (
          <>
            <KvRow label={t("detail.fields.joinDate")} value={formatDate(profile.createdAt, locale) ?? emptyValue} />
            <KvRow label={t("detail.fields.birthday")} value={formatDate(profile.birthday, locale) ?? emptyValue} />
            <KvRow label={t("detail.fields.address")} value={<Text style={detailStyles.rowValueText} numberOfLines={3}>{profile.address || emptyValue}</Text>} />
          </>
        ) : null}
        <KvRow
          label={t("peopleDetail:profile.sensitive")}
          value={(
            <>
              <Text style={detailStyles.rowValueText} numberOfLines={1}>{t("peopleDetail:profile.sensitiveMasked")}</Text>
              {pendingSensitiveRequest ? <PeopleTag label={t("peopleDetail:profile.pending")} tone="amber" /> : null}
            </>
          )}
          onPress={() => setShowSensitive((current) => !current)}
        />
        {showSensitive ? (
          <>
            <KvRow label={t("detail.fields.identityId")} value={maskTrailingFour(profile.identityId, emptyValue)} mono />
            <KvRow label={t("detail.fields.bankBsb")} value={profile.bankBsb || emptyValue} mono />
            <KvRow label={t("detail.fields.bankAccount")} value={maskTrailingFour(profile.bankAccountNumber, emptyValue)} mono />
            <KvRow label={t("detail.fields.superCompany")} value={profile.superannuationCompanyName || emptyValue} />
            <KvRow label={t("detail.fields.superAccount")} value={maskTrailingFour(profile.superannuationAccountNumber, emptyValue)} mono />
          </>
        ) : null}
        {/* 待审敏感资料入口与门禁保持不变：无审核权限不显示该行，也不发请求。 */}
        {reviewAccess.allowed && pendingSensitiveRequest ? (
          <KvRow
            label={t("detail.profileStatus.sensitive")}
            value={<Text style={styles.reviewText} numberOfLines={1}>{t("detail.profileStatus.sensitivePending")}</Text>}
            trailing={<Text style={styles.reviewAction}>{t("detail.profileStatus.goReview")}</Text>}
            onPress={() => router.push({
              pathname: "/employee-profile-review/[requestId]",
              params: { requestId: String(pendingSensitiveRequest.requestId) },
            })}
          />
        ) : null}
        {reviewAccess.allowed && pendingSensitiveQuery.isError ? (
          <KvRow
            label={t("detail.profileStatus.sensitive")}
            value={<Text style={detailStyles.rowMuted}>{t("detail.profileStatus.sensitiveFailed")}</Text>}
            trailing={<Text style={styles.reviewAction}>{t("common:actions.retry")}</Text>}
            onPress={() => void pendingSensitiveQuery.refetch()}
          />
        ) : null}
      </InfoCard>

      {/* 个人信息（员工个人信息维护）入口：四个条件由 resolveDetailPermissions 统一判断，不满足整行隐藏。 */}
      {permissions.canOpenProfile ? <ManagedProfileEntry userGuid={userGuid} /> : null}

      {/* 未成年用工合规：只对未满 18 岁的员工显示，由店长 / HR 给家长发送签字链接（员工本人不再操作）。 */}
      <MinorGuardianSection userGuid={userGuid} onMessage={setSnackbar} />

      {scheduleEntries.length ? (
        <>
          <SectionTitle title={t("peopleDetail:sections.schedule")} />
          <InfoCard>
            <KvRow first label={t("peopleDetail:schedule.row")} value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{scheduleValue}</Text>} onPress={() => setView("schedule")} />
            <KvRow label={t("peopleDetail:schedule.recordsRow")} value={<Text style={detailStyles.rowValueText} numberOfLines={1}>{recordsValue}</Text>} onPress={() => setView("records")} />
          </InfoCard>
        </>
      ) : null}
    </DetailScaffold>
  );
}

function formatDate(value: string | undefined, locale: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value.slice(0, 10);
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(parsed);
}

const styles = StyleSheet.create({
  subViewBody: { flex: 1 },
  reviewText: { color: HB_COLORS.warning, flexShrink: 1, fontSize: 14 },
  reviewAction: { color: HB_COLORS.action, fontSize: 13, fontWeight: "800" },
});
