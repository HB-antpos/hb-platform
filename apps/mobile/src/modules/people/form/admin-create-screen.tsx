/**
 * 管理员版「新增账号」整页（设计稿 A3②）+ 创建完成页（A3③）。
 *
 * 与店长版的区别：可选角色与多家分店（每家「查看 / 管理」）、可设初始密码；账号默认取邮箱 @ 前的部分。
 * 选了「管理」分店就由后端自动派生店长角色，表单里直接提示，避免再去角色里找「店长」勾不上。
 *
 * 初始密码：现有管理员创建接口要求明文密码，不支持「不给密码并发设置密码邮件」，
 * 所以只提供「直接设置」，并在界面上说明邮件方式需要后端支持（见 admin-create-form.ts）。
 *
 * 创建分两步（建号 → 写入「管理」分店）：第一步成功后账号已存在、无法回滚，因此第二步失败时
 * 停在本页提示「第 2 步失败」，只重试第二步，绝不重复创建；建号结果未知（超时 / 5xx）时冻结表单，先核对。
 */
import { useCallback, useMemo, useRef, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import * as Clipboard from "expo-clipboard";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  buildAdminCreateInput,
  defaultUsernameFromEmail,
  EMPTY_ADMIN_CREATE_FORM,
  hasAdminCreateErrors,
  isAdminCreateFormDirty,
  retryAdminStoreAssignment,
  runAdminCreate,
  summarizeAssignments,
  validateAdminCreateForm,
  type AdminCreateDeps,
  type AdminCreateErrors,
  type AdminCreateFormValues,
} from "./admin-create-form";
import { RolePickerSheet, StoreAssignmentSheet } from "./admin-pickers";
import { CreatedResult, type CreatedNotice } from "./created-result";
import {
  DiscardChangesDialog,
  FormField,
  FormFooter,
  FormScreen,
  NameFields,
  SwitchRow,
  useLeaveGuard,
  VisibilityToggle,
} from "./form-ui";
import { resolvePersonName } from "../person-name";
import { InfoCard, KvRow, NoticeBox, PeopleTag, SectionTitle, StickyActionBar } from "../ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { getPosEnabledStores } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import {
  assignUserAccessStores,
  createIdentityUser,
  fetchAccessRoleCatalog,
  fetchAccessStoreCatalog,
  fetchIdentityUsers,
} from "@/modules/identity-admin/api";
import { isUncertainIdentityWrite } from "@/modules/identity-admin/user-logic";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import type { IdentityUserDetail } from "@/modules/identity-admin/types";
import { isStoreManagerRoleName } from "@/modules/users/access-management";
import { toSafeStoreUserErrorLog } from "@/modules/users";
import { StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import type { StoreUserListItem } from "@/modules/users/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

const NO_ERRORS: AdminCreateErrors = { names: {} };

type Stage =
  | { kind: "editing" }
  /** 建号结果未知：冻结表单；`match` 是核对时找到的同名账号。 */
  | { kind: "uncertain"; match?: { userGUID: string } }
  /** 账号已建成，但「管理」分店没写入。 */
  | { kind: "storesFailed"; user: IdentityUserDetail }
  | { kind: "done"; user: IdentityUserDetail; storesPending: boolean };

export function AdminCreateScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["peopleForm", "userManagement", "common"]);
  const { user: actor, access, allowed, actorKey, isAuthenticated } = useIdentitySession("Users.Create");
  const { stores: posStores, isDeviceMode } = useStores();
  const canCreate = allowed && access.isAdmin && access.hasPermission("Users.Create");

  const [values, setValues] = useState<AdminCreateFormValues>(EMPTY_ADMIN_CREATE_FORM);
  const [usernameEdited, setUsernameEdited] = useState(false);
  const [errors, setErrors] = useState<AdminCreateErrors>(NO_ERRORS);
  const [formError, setFormError] = useState("");
  const [errorTick, setErrorTick] = useState(0);
  const [stage, setStage] = useState<Stage>({ kind: "editing" });
  const [submitting, setSubmitting] = useState(false);
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [rolesOpen, setRolesOpen] = useState(false);
  const [storesOpen, setStoresOpen] = useState(false);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [barcodeVisible, setBarcodeVisible] = useState(false);
  const inFlight = useRef(false);

  const catalogRoles = useQuery({
    queryKey: ["identity-admin", actorKey, "role-options"],
    queryFn: ({ signal }) => fetchAccessRoleCatalog(actorKey, signal),
    enabled: canCreate && (access.canReadRole || access.hasPermission("Users.ManageRoles")),
    retry: false,
  });
  const catalogStores = useQuery({
    queryKey: ["identity-admin", actorKey, "store-options"],
    queryFn: ({ signal }) => fetchAccessStoreCatalog(actorKey, signal),
    enabled: canCreate,
    retry: false,
  });
  // 店长角色由「管理」分店派生，不能在角色里直接勾选。
  const roleOptions = useMemo(
    () => (catalogRoles.data ?? []).filter((role) => !isStoreManagerRoleName(role.roleName)),
    [catalogRoles.data]
  );
  const storeOptions = useMemo(() => catalogStores.data ?? [], [catalogStores.data]);

  const frozen = stage.kind !== "editing" || submitting;
  const leaveGuard = useLeaveGuard(isAdminCreateFormDirty(values) && stage.kind !== "done");

  const handleClose = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router]);

  const patch = (next: Partial<AdminCreateFormValues>) => setValues((current) => ({ ...current, ...next }));

  /** 会话在异步等待期间被切换 / 降权 / 退出时，丢弃迟到的结果，避免写进别人的会话。 */
  const isCurrentSession = () => {
    const state = useAuthStore.getState();
    return state.user?.userGUID === actorKey
      && state.sessionKind === "account"
      && state.isAuthenticated
      && !state.iosReviewOfflineGuardActive
      && state.access.isAdmin
      && state.access.hasPermission("Users.Create");
  };

  const invalidateEverywhere = async () => {
    // 管理员侧与员工侧各有自己的缓存键，建号 / 改分店后两边都要失效。
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey] }),
      queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
    ]);
  };

  const deps: Pick<AdminCreateDeps, "create" | "assignStores" | "isUncertain"> = {
    create: (input) => createIdentityUser(input, actorKey),
    assignStores: (input) => assignUserAccessStores(input, actorKey),
    isUncertain: isUncertainIdentityWrite,
  };

  const fail = (error: unknown, message: string) => {
    // 请求里带着密码：只记录脱敏后的错误元数据，不记录 Axios 错误对象。
    console.warn("[identity-admin] create failed", toSafeStoreUserErrorLog(error));
    setFormError(message);
    setErrorTick((tick) => tick + 1);
  };
  const failWith = (error: unknown, fallbackKey: string) =>
    fail(error, resolveLocalizedErrorMessage(error, { t, language, fallbackKey }));

  const handleSubmit = async () => {
    if (inFlight.current || stage.kind !== "editing" || !canCreate || !isCurrentSession()) return;
    setFormError("");
    const nextErrors = validateAdminCreateForm(values, (key) => t(`userManagement:${key}`));
    setErrors(nextErrors);
    if (hasAdminCreateErrors(nextErrors)) {
      setFormError(t("form.fixErrors"));
      setErrorTick((tick) => tick + 1);
      return;
    }

    inFlight.current = true;
    setSubmitting(true);
    try {
      const outcome = await runAdminCreate(deps, buildAdminCreateInput(values), values.assignments);
      if (!isCurrentSession()) return;
      if (outcome.kind === "ok") {
        await invalidateEverywhere();
        setStage({ kind: "done", user: outcome.user, storesPending: false });
      } else if (outcome.kind === "createFailed") {
        failWith(outcome.error, "adminCreate.createFailed");
      } else if (outcome.kind === "createUncertain") {
        // 结果未知：冻结表单，先核对是否已建成，禁止直接重发（会重复创建）。
        fail(outcome.error, t("adminCreate.uncertain"));
        setStage({ kind: "uncertain" });
      } else {
        await invalidateEverywhere();
        console.warn("[identity-admin] store assignment failed", toSafeStoreUserErrorLog(outcome.error));
        setStage({ kind: "storesFailed", user: outcome.user });
        setFormError(t("adminCreate.storesFailed"));
        setErrorTick((tick) => tick + 1);
      }
    } finally {
      inFlight.current = false;
      setSubmitting(false);
    }
  };

  /** 第二步失败后的重试：只重新写入分店，不会再次创建账号。 */
  const retryStores = async () => {
    if (inFlight.current || stage.kind !== "storesFailed" || !isCurrentSession()) return;
    inFlight.current = true;
    setSubmitting(true);
    setFormError("");
    try {
      const result = await retryAdminStoreAssignment(deps, stage.user, values.assignments);
      if (!isCurrentSession()) return;
      if (result.kind === "ok") {
        await invalidateEverywhere();
        setStage({ kind: "done", user: stage.user, storesPending: false });
      } else {
        console.warn("[identity-admin] store assignment retry failed", toSafeStoreUserErrorLog(result.error));
        setFormError(t("adminCreate.storesFailed"));
        setErrorTick((tick) => tick + 1);
      }
    } finally {
      inFlight.current = false;
      setSubmitting(false);
    }
  };

  /** 建号结果未知时的核对：按账号搜索；只证明「存在同名账号」，不能证明它就是这次创建的，所以只引导去详情核对。 */
  const verifyCreate = async () => {
    if (inFlight.current || stage.kind !== "uncertain" || !isCurrentSession()) return;
    inFlight.current = true;
    setSubmitting(true);
    try {
      const username = values.username.trim().toLowerCase();
      const result = await fetchIdentityUsers({ search: values.username.trim(), page: 1, pageSize: 50 }, actorKey);
      if (!isCurrentSession()) return;
      const matches = result.items.filter((item) => item.username.toLowerCase() === username);
      if (matches.length === 1) {
        setStage({ kind: "uncertain", match: { userGUID: matches[0].userGUID } });
        setFormError(t("adminCreate.verifyFound"));
      } else {
        setFormError(t("adminCreate.verifyNotConfirmed"));
      }
    } catch (error) {
      if (isCurrentSession()) failWith(error, "adminCreate.verifyFailed");
    } finally {
      inFlight.current = false;
      setSubmitting(false);
    }
  };

  const openAccount = (userGuid: string, section?: string) => {
    router.push({
      pathname: "/users/[userGuid]",
      params: { userGuid, ...(section ? { section } : {}) },
    } as unknown as Parameters<typeof router.push>[0]);
  };

  const openAccess = (userGuid: string) => {
    router.push({
      pathname: "/users/[userGuid]/access",
      params: { userGuid, section: "stores" },
    } as unknown as Parameters<typeof router.push>[0]);
  };

  const continueAdding = () => {
    setStage({ kind: "editing" });
    setValues(EMPTY_ADMIN_CREATE_FORM);
    setUsernameEdited(false);
    setErrors(NO_ERRORS);
    setFormError("");
    setPasswordVisible(false);
    setBarcodeVisible(false);
  };

  if (!canCreate) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState
          title={t("adminCreate.noAccess")}
          primaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: handleClose }}
        />
      </SafeAreaView>
    );
  }

  const roleNameByGuid = new Map(roleOptions.map((role) => [role.roleGUID, role.roleName]));
  const selectedRoleNames = values.roleGuids.map((guid) => roleNameByGuid.get(guid)).filter((name): name is string => Boolean(name));
  const storeNameByGuid = new Map(storeOptions.map((store) => [store.storeGUID, store.storeName]));
  const summary = summarizeAssignments(values.assignments);
  const manageStoreNames = values.assignments
    .filter((assignment) => assignment.isPrimary)
    .map((assignment) => storeNameByGuid.get(assignment.storeGUID))
    .filter((name): name is string => Boolean(name));

  // ───────── 创建完成页 ─────────
  if (stage.kind === "done") {
    const created = stage.user;
    const createdName = resolvePersonName({
      fullName: created.fullName,
      givenName: created.givenName,
      middleName: created.middleName,
      familyName: created.familyName,
      username: created.username,
    }).primary;
    const storeText = values.assignments.length
      ? values.assignments
        .map((assignment) => {
          const name = storeNameByGuid.get(assignment.storeGUID) ?? "";
          return assignment.isPrimary ? t("adminCreate.storeWithManage", { name }) : name;
        })
        .filter(Boolean)
        .join("、")
      : t("adminCreate.noneSelected");

    // 管理员接口不发邮件：不出现「初始密码已发送」，只提示由你把初始密码告知本人。
    const notices: CreatedNotice[] = [{ tone: "blue", icon: "information-outline", text: t("adminCreate.passwordNotice") }];
    if (stage.storesPending) {
      notices.push({
        tone: "amber",
        icon: "alert-outline",
        text: t("adminCreate.storesPendingNotice"),
        action: { label: t("adminCreate.openStoresAccess"), icon: "store-cog-outline", onPress: () => openAccess(created.userGUID) },
      });
    }

    // 个人码：仅当账号是本店店员、只关联一家分店、该店可开 POS 且操作者有资格时才出现，其余直接不显示。
    const singleStore = values.assignments.length === 1
      ? storeOptions.find((store) => store.storeGUID === values.assignments[0].storeGUID) ?? null
      : null;
    const printTarget: StoreUserListItem | null = singleStore
      ? {
        userGUID: created.userGUID,
        username: created.username,
        fullName: created.fullName,
        status: created.isActive ? 1 : 0,
        storeCode: singleStore.storeCode,
        storeName: singleStore.storeName,
        roleNames: created.roleNames.length ? created.roleNames : selectedRoleNames,
      }
      : null;
    const canPrintBarcode = Boolean(printTarget && singleStore) && canManageStaffBarcode({
      authenticated: isAuthenticated,
      deviceOnly: isDeviceMode,
      canEditUsers: true,
      canManagePosStore: getPosEnabledStores(posStores).some((store) => store.storeCode === singleStore?.storeCode),
      actorGuid: actorKey,
      actorRoles: actor?.roleNames ?? [],
      targetGuid: created.userGUID,
      targetStatus: printTarget?.status ?? 0,
      targetRoles: printTarget?.roleNames ?? [],
    });

    return (
      <CreatedResult
        title={t("created.title")}
        headline={t("created.headline")}
        description={t("adminCreate.createdDescription", { name: createdName })}
        accountLabel={t("created.account")}
        username={created.username}
        assignmentRows={[
          { label: t("adminCreate.rolesLabel"), value: selectedRoleNames.length ? selectedRoleNames.join("、") : t("adminCreate.noneSelected") },
          { label: t("adminCreate.storesLabel"), value: storeText },
        ]}
        copyLabel={t("created.copyAccount")}
        onCopy={() => { void Clipboard.setStringAsync(created.username).then(() => setSnackbarMessage(t("created.copiedAccount"))); }}
        notices={notices}
        steps={{ title: t("adminCreate.stepsTitle"), items: [t("adminCreate.stepLogin"), t("adminCreate.stepChangePassword")] }}
        links={[
          { label: t("adminCreate.openDetail"), onPress: () => openAccount(created.userGUID), testID: "created-open-detail" },
          { label: t("created.continueAdding"), onPress: continueAdding, testID: "created-continue" },
        ]}
        print={canPrintBarcode ? { label: t("created.printBarcode"), onPress: () => setBarcodeVisible(true) } : undefined}
        doneLabel={t("created.done")}
        onDone={handleClose}
        closeLabel={t("created.done")}
        snackbarMessage={snackbarMessage}
        onDismissSnackbar={() => setSnackbarMessage("")}
        overlay={printTarget ? (
          <StaffBarcodeDialog
            actorGuid={actorKey}
            storeCode={printTarget.storeCode ?? ""}
            user={barcodeVisible ? printTarget : null}
            visible={barcodeVisible}
            onDismiss={() => setBarcodeVisible(false)}
          />
        ) : null}
      />
    );
  }

  // ───────── 表单 ─────────
  const matchedGuid = stage.kind === "uncertain" ? stage.match?.userGUID : undefined;
  const footer = stage.kind === "storesFailed" ? (
    <StickyActionBar>
      <Button mode="outlined" onPress={() => setStage({ kind: "done", user: stage.user, storesPending: true })} disabled={submitting} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
        {t("adminCreate.storesLater")}
      </Button>
      <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => void retryStores()} loading={submitting} disabled={submitting} style={styles.footerButtonPrimary} contentStyle={styles.footerButtonContent}>
        {t("adminCreate.storesRetry")}
      </Button>
    </StickyActionBar>
  ) : stage.kind === "uncertain" ? (
    <StickyActionBar>
      <Button mode="outlined" onPress={handleClose} disabled={submitting} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
        {t("form.cancel")}
      </Button>
      {matchedGuid ? (
        <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => openAccount(matchedGuid)} style={styles.footerButtonPrimary} contentStyle={styles.footerButtonContent}>
          {t("adminCreate.openDetail")}
        </Button>
      ) : (
        <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => void verifyCreate()} loading={submitting} disabled={submitting} style={styles.footerButtonPrimary} contentStyle={styles.footerButtonContent}>
          {t("adminCreate.verifyCreate")}
        </Button>
      )}
    </StickyActionBar>
  ) : (
    <FormFooter
      cancelLabel={t("form.cancel")}
      submitLabel={t("adminCreate.submit")}
      onCancel={handleClose}
      onSubmit={() => void handleSubmit()}
      loading={submitting}
    />
  );

  return (
    <>
      <FormScreen
        title={t("adminCreate.title")}
        onClose={handleClose}
        closeLabel={t("form.cancel")}
        scrollToTopKey={errorTick}
        footer={footer}
      >
        {formError ? <NoticeBox tone={stage.kind === "editing" ? "red" : "amber"} icon="alert-circle-outline">{formError}</NoticeBox> : null}

        <FormField
          label={t("adminCreate.email")}
          required
          value={values.email}
          onChangeText={(value) => {
            patch({ email: value, ...(usernameEdited ? {} : { username: defaultUsernameFromEmail(value) }) });
            setErrors((current) => ({ ...current, email: undefined, ...(usernameEdited ? {} : { username: undefined }) }));
          }}
          keyboardType="email-address"
          textContentType="emailAddress"
          maxLength={254}
          error={errors.email}
          disabled={frozen}
          testID="admin-email"
        />
        <FormField
          label={t("adminCreate.username")}
          required
          value={values.username}
          onChangeText={(value) => { setUsernameEdited(true); patch({ username: value }); setErrors((current) => ({ ...current, username: undefined })); }}
          maxLength={50}
          error={errors.username}
          hint={t("adminCreate.usernameHelper")}
          disabled={frozen}
          testID="admin-username"
        />

        <SectionTitle title={t("form.sectionName")} />
        <NameFields
          values={values.names}
          onChange={(key, value) => {
            patch({ names: { ...values.names, [key]: value } });
            setErrors((current) => ({ ...current, names: { ...current.names, [key]: undefined, fullName: undefined } }));
          }}
          errors={errors.names}
          disabled={frozen}
        />

        <SectionTitle title={t("adminCreate.sectionRolesStores")} />
        <InfoCard>
          <KvRow
            first
            label={t("adminCreate.rolesLabel")}
            onPress={frozen ? undefined : () => setRolesOpen(true)}
            value={selectedRoleNames.length
              ? <View style={styles.tags}>{selectedRoleNames.map((name) => <PeopleTag key={name} label={name} />)}</View>
              : <Text style={styles.placeholder}>{t("adminCreate.rolesPlaceholder")}</Text>}
          />
          <KvRow
            label={t("adminCreate.storesLabel")}
            onPress={frozen ? undefined : () => setStoresOpen(true)}
            value={summary.total
              ? (
                <View style={styles.tags}>
                  <Text style={styles.storesText}>{t("adminCreate.storesSummary", { count: summary.total })}</Text>
                  {manageStoreNames.slice(0, 2).map((name) => (
                    <View key={name} style={styles.manageStore}>
                      <Text style={styles.storesText} numberOfLines={1}>{name}</Text>
                      <PeopleTag label={t("adminCreate.storeState.manage")} tone="blue" />
                    </View>
                  ))}
                  {manageStoreNames.length > 2 ? <Text style={styles.placeholder}>{`+${manageStoreNames.length - 2}`}</Text> : null}
                </View>
              )
              : <Text style={styles.placeholder}>{t("adminCreate.storesPlaceholder")}</Text>}
          />
          <SwitchRow
            title={t("staff.enableNow")}
            description={t("staff.enableNowHelper")}
            value={values.enabled}
            onValueChange={(value) => patch({ enabled: value })}
            disabled={frozen}
          />
        </InfoCard>
        <NoticeBox tone="blue" icon="source-branch">
          {summary.manageCount > 0 ? t("adminCreate.managerAuto", { count: summary.manageCount }) : t("adminCreate.managerTip")}
        </NoticeBox>

        <SectionTitle title={t("adminCreate.sectionPassword")} />
        <NoticeBox tone="gray" icon="email-off-outline">{t("adminCreate.passwordEmailUnsupported")}</NoticeBox>
        <FormField
          label={t("adminCreate.password")}
          required
          value={values.password}
          onChangeText={(value) => { patch({ password: value }); setErrors((current) => ({ ...current, password: undefined, confirmPassword: undefined })); }}
          secureTextEntry={!passwordVisible}
          textContentType="newPassword"
          maxLength={100}
          error={errors.password ? t(`adminCreate.passwordErrors.${errors.password}`) : undefined}
          hint={t("adminCreate.passwordHelper")}
          disabled={frozen}
          trailing={<VisibilityToggle visible={passwordVisible} onToggle={() => setPasswordVisible((current) => !current)} showLabel={t("adminCreate.showPassword")} hideLabel={t("adminCreate.hidePassword")} />}
          testID="admin-password"
        />
        <FormField
          label={t("adminCreate.confirmPassword")}
          required
          value={values.confirmPassword}
          onChangeText={(value) => { patch({ confirmPassword: value }); setErrors((current) => ({ ...current, confirmPassword: undefined })); }}
          secureTextEntry={!passwordVisible}
          textContentType="newPassword"
          maxLength={100}
          error={errors.confirmPassword ? t("adminCreate.passwordErrors.mismatch") : undefined}
          disabled={frozen}
          testID="admin-confirm-password"
        />
      </FormScreen>

      <RolePickerSheet
        visible={rolesOpen}
        title={t("adminCreate.rolesSheetTitle")}
        roles={roleOptions}
        selected={values.roleGuids}
        onChange={(roleGuids) => patch({ roleGuids })}
        onDismiss={() => setRolesOpen(false)}
        loading={catalogRoles.isPending && catalogRoles.fetchStatus !== "idle"}
        errorText={catalogRoles.isError ? t("adminCreate.optionsLoadFailed") : undefined}
      />
      <StoreAssignmentSheet
        visible={storesOpen}
        title={t("adminCreate.storesSheetTitle")}
        stores={storeOptions}
        assignments={values.assignments}
        onChange={(assignments) => patch({ assignments })}
        onDismiss={() => setStoresOpen(false)}
        loading={catalogStores.isPending && catalogStores.fetchStatus !== "idle"}
        errorText={catalogStores.isError ? t("adminCreate.optionsLoadFailed") : undefined}
      />
      <DiscardChangesDialog visible={leaveGuard.confirmVisible} onKeepEditing={leaveGuard.keepEditing} onDiscard={leaveGuard.discard} />
    </>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  footerButton: { flex: 1 },
  footerButtonPrimary: { flex: 1.4 },
  footerButtonContent: { minHeight: 48 },
  tags: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 4 },
  placeholder: { color: "#98A2B3", fontSize: 14 },
  storesText: { color: HB_COLORS.textPrimary, fontSize: 14 },
  manageStore: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xxs },
});
