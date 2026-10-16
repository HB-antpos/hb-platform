/**
 * 「编辑资料」整页（设计稿 A3④），替代原来员工列表里的编辑弹窗，与新增共用同一套整页表单。
 *
 * 数据源按身份切换：
 * - 路由带 `storeCode`（店长，或管理员在某家分店的上下文里）→ 店员接口（可改电话、登录开关）；
 * - 不带 `storeCode` 且是管理员账号会话 → 管理员接口（identity-admin，不含电话与登录开关）。
 *
 * 启用 / 停用不在这里改（只在详情页「停用」按钮，有单独确认）：请求里始终带回当前状态，避免保存资料时改动它。
 */
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { StyleSheet } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Portal, Snackbar } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  buildIdentityUpdateInput,
  buildPersonEditBaseline,
  buildStoreUserUpdatePayload,
  hasPersonEditErrors,
  listPersonEditChanges,
  resolvePersonEditSource,
  shouldPromptPasswordSetup,
  validatePersonEdit,
  type PersonEditBaseline,
  type PersonEditErrors,
  type PersonEditValues,
} from "./person-edit-form";
import {
  DiscardChangesDialog,
  FormField,
  FormFooter,
  FormScreen,
  NameFields,
  SwitchRow,
  useLeaveGuard,
} from "./form-ui";
import { resolvePersonName } from "../person-name";
import { InfoCard, NoticeBox, PeopleTag, SectionTitle } from "../ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import { fetchIdentityUserDetail, updateIdentityUser } from "@/modules/identity-admin/api";
import type { IdentityUserDetail } from "@/modules/identity-admin/types";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { canModifyIdentityUser } from "@/modules/identity-admin/user-logic";
import {
  toSafeStoreUserErrorLog,
  useStoreUserDetail,
  useStoreUserMutations,
  type StoreUserDetail,
} from "@/modules/users";
import { hasDeliverableEmail } from "@/modules/users/staff-email-username";
import { StaffPasswordSetupDialog } from "@/modules/users/staff-password-setup-dialog";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] ?? "" : value ?? "");

/** 两种数据源统一后的编辑目标。 */
interface EditTarget {
  userGuid: string;
  username: string;
  /** 数字状态：1 启用，其余停用（管理员接口的 isActive 折算而来）。 */
  status: number;
  isActive: boolean;
  roleNames: string[];
  fullName?: string;
  email?: string;
  phone?: string;
  loginEnabled?: boolean;
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
}

function fromStoreDetail(detail: StoreUserDetail): EditTarget {
  return {
    userGuid: detail.userGUID,
    username: detail.username,
    status: detail.status,
    isActive: detail.status === 1,
    roleNames: detail.roleNames,
    fullName: detail.fullName,
    email: detail.email,
    phone: detail.phone,
    loginEnabled: detail.loginEnabled,
    givenName: detail.givenName,
    middleName: detail.middleName,
    familyName: detail.familyName,
    nickname: detail.nickname,
  };
}

function fromIdentityDetail(detail: IdentityUserDetail): EditTarget {
  return {
    userGuid: detail.userGUID,
    username: detail.username,
    status: detail.isActive ? 1 : 0,
    isActive: detail.isActive,
    roleNames: detail.roleNames,
    fullName: detail.fullName,
    email: detail.email,
    phone: detail.phone,
    givenName: detail.givenName,
    middleName: detail.middleName,
    familyName: detail.familyName,
    nickname: detail.nickname,
  };
}

export default function PersonEditScreen() {
  const params = useLocalSearchParams<{ userGuid?: string | string[]; storeCode?: string | string[] }>();
  const actorKey = useAuthStore((state) => state.user?.userGUID) ?? "";
  const userGuid = first(params.userGuid);
  const storeCode = first(params.storeCode);
  // 更换账号或目标用户时销毁整份草稿，避免上一个编辑会话继续提交。
  return <PersonEditor key={`${actorKey}:${userGuid}:${storeCode}`} userGuid={userGuid} storeCode={storeCode} />;
}

function PersonEditor({ userGuid, storeCode }: { userGuid: string; storeCode: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["peopleForm", "userManagement", "common"]);
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  const adminSession = useIdentitySession("Users.Edit");
  const { stores, selectedStoreCode, isDeviceMode } = useStores();

  const source = resolvePersonEditSource({
    storeCode,
    adminSessionAllowed: adminSession.allowed,
    isStoreLevelManager: access.isStoreLevelManager,
  });
  const actorKey = adminSession.actorKey;

  const canEditUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Edit)
    || access.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const canResetPasswords = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.ResetPassword)
    || access.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);
  const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });
  const manageableStores = useMemo(() => getManageableStoresForSession({
    stores,
    isDeviceMode,
    deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
    isAdmin: access.isAdmin,
  }), [access.isAdmin, deviceBoundStoreCode, isDeviceMode, stores]);
  const storeEditable = source === "store" && canEditUsers && isStoreManageable(storeCode, manageableStores);
  const canResetUserPassword = canResetPasswords && isStoreManageable(storeCode, manageableStores);
  // 本人行不提供编辑（店员接口会拒绝）。
  const isSelf = actorGuid !== "" && actorGuid.toLowerCase() === userGuid.toLowerCase();

  const storeDetail = useStoreUserDetail(source === "store" ? userGuid : null, storeCode);
  const adminDetail = useQuery({
    queryKey: ["identity-admin", actorKey, "user", userGuid],
    queryFn: ({ signal }) => fetchIdentityUserDetail(userGuid, actorKey, signal),
    enabled: source === "admin" && Boolean(userGuid),
    retry: false,
  });
  const detailQuery = source === "store" ? storeDetail : adminDetail;
  const target = useMemo<EditTarget | null>(() => {
    if (source === "store" && storeDetail.data) return fromStoreDetail(storeDetail.data);
    if (source === "admin" && adminDetail.data) return fromIdentityDetail(adminDetail.data);
    return null;
  }, [adminDetail.data, source, storeDetail.data]);
  const adminEditable = source === "admin" && target !== null
    && canModifyIdentityUser(currentUser, access, { userGUID: target.userGuid, roleNames: target.roleNames }, "Users.Edit");
  const editable = source === "store" ? storeEditable && !isSelf : adminEditable;

  const { updateMutation, setupEmailMutation } = useStoreUserMutations(storeCode, "");

  const [baseline, setBaseline] = useState<PersonEditBaseline | null>(null);
  const [values, setValues] = useState<PersonEditValues | null>(null);
  const [errors, setErrors] = useState<PersonEditErrors>({ names: {} });
  const [formError, setFormError] = useState("");
  const [errorTick, setErrorTick] = useState(0);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [passwordUser, setPasswordUser] = useState<StoreUserDetail | null>(null);
  // 发送设置密码邮件失败的原因：底部弹层会盖住页面 Snackbar，所以就地显示在弹层里。
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // 只用首次加载到的详情初始化表单：之后后台刷新不能覆盖用户正在编辑的内容。
  useEffect(() => {
    if (baseline || !target) return;
    const next = buildPersonEditBaseline(target);
    setBaseline(next);
    setValues(next.values);
  }, [baseline, target]);
  useEffect(() => () => {
    if (leaveTimer.current) clearTimeout(leaveTimer.current);
  }, []);

  const changes = useMemo(
    () => (baseline && values
      ? listPersonEditChanges(baseline.values, values, { phoneAvailable: source === "store", loginAvailable: source === "store" })
      : []),
    [baseline, source, values]
  );
  const dirty = changes.length > 0 && !saved;
  const leaveGuard = useLeaveGuard(dirty);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else {
      router.replace({
        pathname: "/users/[userGuid]",
        params: { userGuid, ...(storeCode ? { storeCode } : {}) },
      } as unknown as Parameters<typeof router.replace>[0]);
    }
  }, [router, storeCode, userGuid]);

  /** 保存成功：给一句「已保存」，稍后返回详情（先解除离开拦截，避免弹出「放弃修改？」）。 */
  const finishSaved = useCallback((message: string) => {
    setSnackbarMessage(message);
    leaveGuard.leaveWith(() => {
      leaveTimer.current = setTimeout(goBack, 700);
    });
  }, [goBack, leaveGuard]);

  const isCurrentAdminSession = () => {
    const state = useAuthStore.getState();
    return state.user?.userGUID === actorKey
      && state.sessionKind === "account"
      && state.isAuthenticated
      && !state.iosReviewOfflineGuardActive
      && state.access.hasPermission("Users.Edit");
  };

  const handleSubmit = async () => {
    if (inFlight.current || !baseline || !values || !target || !editable) return;
    setFormError("");
    const nextErrors = validatePersonEdit(baseline.values, values, { requireEmail: source === "admin" });
    setErrors(nextErrors);
    if (hasPersonEditErrors(nextErrors)) {
      setFormError(t("form.fixErrors"));
      setErrorTick((tick) => tick + 1);
      return;
    }

    inFlight.current = true;
    setSaving(true);
    try {
      if (source === "store") {
        // 状态、用户名、全名取最新详情，保证保存资料不会改动启停状态。
        const latest = storeDetail.data ? fromStoreDetail(storeDetail.data) : target;
        const updated = await updateMutation.mutateAsync(buildStoreUserUpdatePayload({
          userGuid,
          storeCode,
          current: { username: latest.username, status: latest.status, fullName: latest.fullName },
          baseline: baseline.values,
          values,
        }));
        void queryClient.invalidateQueries({ queryKey: ["identity-admin"] });
        setSaved(true);
        // 从「不能登录」改为「能登录」：账号可能还没有可用密码，直接打开设置密码邮件弹窗，
        // 员工收到验证码后自己设密码（邮箱缺失时弹窗会让店长补填）。无重置密码权限则不弹。
        if (shouldPromptPasswordSetup({
          loginWasEnabled: baseline.values.loginEnabled,
          loginNow: values.loginEnabled,
          canResetPassword: canResetUserPassword,
        })) {
          setPasswordUser(updated);
        } else {
          finishSaved(t("edit.saved"));
        }
      } else {
        if (!isCurrentAdminSession()) return;
        const latest = adminDetail.data ? fromIdentityDetail(adminDetail.data) : target;
        // 更新是整体覆盖的幂等 PUT：即使这次结果未知，用户再点一次保存也不会产生重复数据，所以不需要冻结表单。
        await updateIdentityUser(userGuid, buildIdentityUpdateInput({
          current: { username: latest.username, isActive: latest.isActive, fullName: latest.fullName },
          baseline: baseline.values,
          values,
        }), actorKey);
        if (!isCurrentAdminSession()) return;
        const refreshed = await fetchIdentityUserDetail(userGuid, actorKey);
        queryClient.setQueryData(["identity-admin", actorKey, "user", userGuid], refreshed);
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey] }),
          queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
          queryClient.invalidateQueries({ queryKey: ["storeUserDetail"] }),
          queryClient.invalidateQueries({ queryKey: ["storeUserProfile"] }),
        ]);
        setSaved(true);
        finishSaved(t("edit.saved"));
      }
    } catch (error) {
      console.warn("[store-users] save failed", toSafeStoreUserErrorLog(error));
      setFormError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "userManagement:messages.saveFailed" }));
      setErrorTick((tick) => tick + 1);
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  };

  /** 保存后的「发送设置密码邮件」弹窗：发送成功或放弃都离开本页（资料已经保存）。 */
  const handleSendSetupEmail = async (email?: string) => {
    if (!passwordUser) return;
    if (!canResetPasswords || !isStoreManageable(storeCode, manageableStores)) {
      setPasswordError(t("userManagement:messages.storeReadOnly"));
      return;
    }
    setPasswordError(null);
    try {
      // 店长不再手动设新密码：给员工邮箱发验证码，员工在登录页自己设置；老账号没有可用邮箱时 email 为就地补填的邮箱。
      const result = await setupEmailMutation.mutateAsync({ userGuid: passwordUser.userGUID, storeCode, email });
      setPasswordUser(null);
      finishSaved(t("userManagement:messages.passwordSetupEmailSent", { email: result.maskedEmail }));
    } catch (error) {
      console.warn("[store-users] password reset failed", toSafeStoreUserErrorLog(error));
      setPasswordError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "userManagement:messages.passwordResetFailed" }));
    }
  };

  const dismissSetupEmail = () => {
    setPasswordError(null);
    setPasswordUser(null);
    finishSaved(t("edit.saved"));
  };

  // ───────── 各种非表单状态 ─────────
  const shell = (body: ReactNode) => (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>{body}</SafeAreaView>
  );
  if (source === "none" || !userGuid) {
    return shell(
      <EmptyState
        title={t("edit.noAccess")}
        primaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: goBack }}
      />
    );
  }
  if (detailQuery.isError) {
    return shell(
      <EmptyState
        title={t("edit.loadFailed")}
        primaryAction={{ label: t("edit.retry"), icon: "refresh", onPress: () => void detailQuery.refetch() }}
        secondaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: goBack }}
      />
    );
  }
  if (!target || !baseline || !values) {
    return (
      <FormScreen title={t("edit.title")} onClose={goBack} closeLabel={t("form.cancel")}>
        <ActivityIndicator style={styles.loading} />
      </FormScreen>
    );
  }
  if (!editable) {
    return shell(
      <EmptyState
        title={isSelf && source === "store" ? t("edit.selfBlocked") : t("userManagement:messages.storeReadOnly")}
        description={isSelf && source === "store" ? undefined : t("userManagement:currentStore.readOnlyHelper")}
        primaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: goBack }}
      />
    );
  }

  const displayName = resolvePersonName({
    fullName: target.fullName,
    givenName: target.givenName,
    middleName: target.middleName,
    familyName: target.familyName,
    username: target.username,
  }).primary;
  const nameTouched = changes.some((key) => key === "familyName" || key === "givenName" || key === "middleName");
  const locked = saving || saved;
  // 系统里没有「邮箱已验证」的数据，所以只标「可收信」：原有邮箱且不是分店占位邮箱。
  const emailUnchangedAndDeliverable = hasDeliverableEmail(values.email)
    && values.email.trim().toLowerCase() === baseline.values.email.trim().toLowerCase();
  const submitLabel = changes.length > 0 ? t("edit.saveWithCount", { count: changes.length }) : t("edit.save");

  return (
    <>
      <FormScreen
        title={t("edit.title")}
        subtitle={displayName}
        onClose={goBack}
        closeLabel={t("form.cancel")}
        scrollToTopKey={errorTick}
        footer={(
          <FormFooter
            cancelLabel={t("form.cancel")}
            submitLabel={submitLabel}
            onCancel={goBack}
            onSubmit={() => void handleSubmit()}
            submitDisabled={changes.length === 0 || saved}
            loading={saving}
          />
        )}
        overlay={<Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2000}>{snackbarMessage}</Snackbar>}
      >
        {formError ? <NoticeBox tone="red" icon="alert-circle-outline">{formError}</NoticeBox> : null}

        <FormField
          label={t("edit.account")}
          value={target.username}
          readOnly
          mono
          hint={t("edit.accountHint")}
          testID="edit-username"
        />

        <NameFields
          values={values.names}
          onChange={(key, value) => {
            setValues({ ...values, names: { ...values.names, [key]: value } });
            setErrors((current) => ({ ...current, names: { ...current.names, [key]: undefined, fullName: undefined } }));
          }}
          errors={errors.names}
          phone={source === "store" ? {
            value: values.phone,
            onChange: (value) => setValues({ ...values, phone: value }),
          } : undefined}
          disabled={locked}
          legacyHint={baseline.legacySplit && !nameTouched}
        />

        <FormField
          label={t("edit.email")}
          value={values.email}
          onChangeText={(value) => {
            setValues({ ...values, email: value });
            setErrors((current) => ({ ...current, email: undefined }));
          }}
          keyboardType="email-address"
          textContentType="emailAddress"
          maxLength={254}
          required={source === "admin"}
          error={errors.email ? t(`edit.emailErrors.${errors.email}`) : undefined}
          hint={values.email.trim() === "" ? t("edit.emailEmptyHint") : undefined}
          adornment={emailUnchangedAndDeliverable ? <PeopleTag label={t("edit.emailDeliverable")} tone="green" /> : undefined}
          disabled={locked}
          testID="edit-email"
        />

        {source === "store" ? (
          <>
            <SectionTitle title={t("edit.sectionLogin")} />
            <InfoCard>
              <SwitchRow
                first
                title={t("form.loginEnabled")}
                description={t("edit.loginHelper")}
                value={values.loginEnabled}
                onValueChange={(value) => setValues({ ...values, loginEnabled: value })}
                disabled={locked}
                testID="edit-login-switch"
              />
            </InfoCard>
          </>
        ) : null}

        <NoticeBox tone="blue" icon="information-outline">{t("edit.statusNotice")}</NoticeBox>
      </FormScreen>

      <Portal>
        <StaffPasswordSetupDialog
          visible={Boolean(passwordUser)}
          staffName={passwordUser?.fullName || passwordUser?.username || ""}
          loginName={passwordUser?.username ?? ""}
          email={passwordUser?.email}
          pending={setupEmailMutation.isPending}
          errorMessage={passwordError}
          onDismiss={dismissSetupEmail}
          onSubmit={(email) => void handleSendSetupEmail(email)}
        />
      </Portal>
      <DiscardChangesDialog visible={leaveGuard.confirmVisible} onKeepEditing={leaveGuard.keepEditing} onDiscard={leaveGuard.discard} />
    </>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  loading: { marginTop: 32 },
});
