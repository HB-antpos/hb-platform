/**
 * 店长版「新增员工」整页（设计稿 A3①）+ 创建完成页（A3③）。
 *
 * 账号固定为本店店员（StoreStaff）、员工类型 casual，与本店的关系是「仅查看」——
 * 页面只做只读说明，不提供角色或分店选择；其他角色 / 分店请找管理员。
 * 邮箱必填，默认作为账号；「允许登录 App 和 Web」默认关闭——关闭时只建账号与资料（考勤、排班等照常使用），
 * 不发密码邮件；开启时系统给员工邮箱发初始密码，员工用它登录并自己设密码，店长全程不经手密码。
 */
import { useCallback, useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import * as Clipboard from "expo-clipboard";
import { useQueryClient } from "@tanstack/react-query";
import { Icon, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { CreatedResult, type CreatedNotice } from "./created-result";
import {
  DiscardChangesDialog,
  FormField,
  FormFooter,
  FormScreen,
  NameFields,
  SwitchRow,
  useLeaveGuard,
} from "./form-ui";
import {
  buildStaffCreatePayload,
  EMPTY_STAFF_CREATE_FORM,
  hasStaffCreateErrors,
  isStaffCreateFormDirty,
  validateStaffCreateForm,
  type StaffCreateErrors,
  type StaffCreateFormValues,
} from "./staff-create-form";
import { resolveIdentityConflictField } from "./identity-conflict";
import { resolvePersonName } from "../person-name";
import { InfoCard, NoticeBox, PeopleTag, SectionTitle } from "../ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, getPosEnabledStores, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import {
  toSafeStoreUserErrorLog,
  useStoreUserMutations,
  type PasswordSetupEmailResult,
  type StoreUserDetail,
} from "@/modules/users";
import { resolveUsernameFromEmail } from "@/modules/users/staff-email-username";
import { StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { applyCreateNameChange } from "./person-form";

const NO_ERRORS: StaffCreateErrors = { names: {} };

export function StaffCreateScreen({ storeCode: requestedStoreCode }: { storeCode: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t, language } = useAppTranslation(["peopleForm", "userManagement", "common"]);
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  const { stores, selectedStoreCode, isDeviceMode } = useStores();

  const [values, setValues] = useState<StaffCreateFormValues>(EMPTY_STAFF_CREATE_FORM);
  // 店长手动改过账号后，不再跟随邮箱自动覆盖。
  const [usernameEdited, setUsernameEdited] = useState(false);
  const [errors, setErrors] = useState<StaffCreateErrors>(NO_ERRORS);
  const [formError, setFormError] = useState("");
  const [errorTick, setErrorTick] = useState(0);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [created, setCreated] = useState<StoreUserDetail | null>(null);
  // 成功页要按「提交那一刻」的开关值展示，不能跟随之后可能变化的状态。
  const [createdWithLogin, setCreatedWithLogin] = useState(false);
  const [inviteResult, setInviteResult] = useState<PasswordSetupEmailResult | null>(null);
  const [inviteError, setInviteError] = useState("");
  const [barcodeVisible, setBarcodeVisible] = useState(false);

  const canCreateUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Create)
    || access.hasPermission(PERMISSIONS.Users.CreateStoreStaff);
  const canEditUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Edit)
    || access.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });
  const manageableStores = useMemo(() => getManageableStoresForSession({
    stores,
    isDeviceMode,
    deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
    isAdmin: access.isAdmin,
  }), [access.isAdmin, deviceBoundStoreCode, isDeviceMode, stores]);
  const posEnabledStores = useMemo(() => getPosEnabledStores(stores), [stores]);
  const store = stores.find((item) => item.storeCode === requestedStoreCode) ?? null;
  const storeAllowed = canCreateUsers && Boolean(requestedStoreCode) && isStoreManageable(requestedStoreCode, manageableStores);
  const { createMutation, setupEmailMutation } = useStoreUserMutations(requestedStoreCode, "");

  const leaveGuard = useLeaveGuard(isStaffCreateFormDirty(values) && !created);

  const handleClose = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/users" as unknown as Parameters<typeof router.replace>[0]);
  }, [router]);

  const patch = (next: Partial<StaffCreateFormValues>) => setValues((current) => ({ ...current, ...next }));

  const handleEmailChange = (value: string) => {
    patch({ email: value, ...(usernameEdited ? {} : { username: resolveUsernameFromEmail(value) }) });
    setErrors((current) => ({ ...current, email: undefined, ...(usernameEdited ? {} : { username: undefined }) }));
  };

  const handleSubmit = async () => {
    setFormError("");
    // 邮箱 / 账号 / 电话的文案来自 userManagement.messages.*；姓名错误返回错误码，由 NameFields 按字段翻译。
    const nextErrors = validateStaffCreateForm(values, (key) => t(`userManagement:${key}`));
    setErrors(nextErrors);
    if (hasStaffCreateErrors(nextErrors)) {
      setFormError(t("form.fixErrors"));
      setErrorTick((tick) => tick + 1);
      return;
    }
    try {
      const user = await createMutation.mutateAsync(buildStaffCreatePayload(values, requestedStoreCode));
      // 员工侧与管理员侧各有自己的缓存键，建号后两边都要失效，管理员侧列表才能看到新账号。
      void queryClient.invalidateQueries({ queryKey: ["identity-admin"] });
      setCreated(user);
      setCreatedWithLogin(values.loginEnabled);
      setInviteResult(user.passwordSetupEmail ?? null);
      setInviteError(user.passwordSetupEmail ? "" : user.passwordSetupEmailError || t("created.inviteFailedFallback"));
    } catch (error) {
      console.warn("[store-users] save failed", toSafeStoreUserErrorLog(error));
      // 账号 / 邮箱被其他人占用：标红对应字段，而不是只在表单顶部给一句通用失败。
      const conflictField = resolveIdentityConflictField(error);
      if (conflictField) {
        setErrors((current) => ({
          ...current,
          [conflictField]: conflictField === "username" ? t("form.usernameTaken") : t("form.emailTaken"),
        }));
        setFormError(t("form.fixErrors"));
      } else {
        setFormError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "userManagement:messages.saveFailed" }));
      }
      setErrorTick((tick) => tick + 1);
    }
  };

  const copyAccount = async () => {
    if (!created) return;
    await Clipboard.setStringAsync(created.username);
    setSnackbarMessage(t("created.copiedAccount"));
  };

  const resendInvite = async () => {
    if (!created) return;
    try {
      // 重发同样发初始密码（会重新生成），与首次创建的邮件保持一致，不混发验证码。
      const result = await setupEmailMutation.mutateAsync({
        userGuid: created.userGUID,
        storeCode: requestedStoreCode,
        initialPassword: true,
      });
      setInviteResult(result);
      setInviteError("");
      setSnackbarMessage(t("created.inviteResent"));
    } catch (error) {
      setInviteError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "created.inviteFailedFallback" }));
    }
  };

  /** 继续新增：回到空白表单（仍是同一家分店）。 */
  const continueAdding = () => {
    setCreated(null);
    setValues(EMPTY_STAFF_CREATE_FORM);
    setUsernameEdited(false);
    setErrors(NO_ERRORS);
    setFormError("");
    setInviteResult(null);
    setInviteError("");
    setCreatedWithLogin(false);
    setBarcodeVisible(false);
  };

  if (!storeAllowed) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState
          title={t("userManagement:messages.storeReadOnly")}
          description={t("userManagement:currentStore.readOnlyHelper")}
          primaryAction={{ label: t("common:actions.back"), icon: "arrow-left", onPress: handleClose }}
        />
      </SafeAreaView>
    );
  }

  const storeLabel = store ? [store.storeName, store.storeCode].filter(Boolean).join(" ") : requestedStoreCode;

  if (created) {
    const createdName = resolvePersonName({
      fullName: created.fullName,
      givenName: created.givenName,
      middleName: created.middleName,
      familyName: created.familyName,
      username: created.username,
    }).primary;
    const canPrintBarcode = canManageStaffBarcode({
      authenticated,
      deviceOnly: isDeviceMode,
      canEditUsers,
      canManagePosStore: isStoreManageable(requestedStoreCode, manageableStores)
        && posEnabledStores.some((item) => item.storeCode === requestedStoreCode),
      actorGuid,
      actorRoles: currentUser?.roleNames ?? [],
      targetGuid: created.userGUID,
      targetStatus: created.status,
      targetRoles: created.roleNames,
    });

    // 邮件状态：发送成功显示打码邮箱；失败显示原因，可重发；不允许登录时不发邮件，只提示暂不能登录。
    const notices: CreatedNotice[] = [];
    if (!createdWithLogin) {
      notices.push({ tone: "amber", icon: "lock-outline", text: t("created.loginDisabledNotice") });
    } else if (inviteResult) {
      notices.push({ tone: "green", icon: "email-check-outline", text: t("created.inviteSent", { email: inviteResult.maskedEmail }) });
    } else {
      notices.push({
        tone: "red",
        icon: "alert-circle-outline",
        text: t("created.inviteFailed", { reason: inviteError }),
        action: { label: t("created.resendInvite"), icon: "email-sync-outline", onPress: () => void resendInvite(), loading: setupEmailMutation.isPending },
      });
    }

    return (
      <CreatedResult
        title={t("created.title")}
        headline={t("created.headline")}
        description={t(created.status === 1 ? "created.description" : "created.descriptionDisabled", { name: createdName })}
        accountLabel={t("created.account")}
        username={created.username}
        assignmentRows={[{ label: t("created.assignment"), value: t("created.assignmentValue", { store: store?.storeName || requestedStoreCode }) }]}
        copyLabel={t("created.copyAccount")}
        onCopy={() => void copyAccount()}
        notices={notices}
        steps={createdWithLogin ? { title: t("created.stepsTitle"), items: [t("created.stepOpenEmail"), t("created.stepLogin"), t("created.stepSetPassword")] } : undefined}
        links={[
          ...(createdWithLogin && inviteResult
            ? [{ label: t("created.resendInvite"), onPress: () => void resendInvite(), loading: setupEmailMutation.isPending, testID: "created-resend" }]
            : []),
          { label: t("created.continueAdding"), onPress: continueAdding, testID: "created-continue" },
        ]}
        print={canPrintBarcode ? { label: t("created.printBarcode"), onPress: () => setBarcodeVisible(true) } : undefined}
        doneLabel={t("created.done")}
        onDone={handleClose}
        closeLabel={t("created.done")}
        snackbarMessage={snackbarMessage}
        onDismissSnackbar={() => setSnackbarMessage("")}
        overlay={(
          <StaffBarcodeDialog
            actorGuid={actorGuid}
            storeCode={requestedStoreCode}
            user={barcodeVisible ? created : null}
            visible={barcodeVisible}
            onDismiss={() => setBarcodeVisible(false)}
          />
        )}
      />
    );
  }

  const pending = createMutation.isPending;
  return (
    <>
      <FormScreen
        title={t("staff.title")}
        subtitle={storeLabel}
        onClose={handleClose}
        closeLabel={t("form.cancel")}
        scrollToTopKey={errorTick}
        footer={(
          <FormFooter
            cancelLabel={t("form.cancel")}
            submitLabel={t("staff.submit")}
            onCancel={handleClose}
            onSubmit={() => void handleSubmit()}
            loading={pending}
          />
        )}
      >
        {formError ? <NoticeBox tone="red" icon="alert-circle-outline">{formError}</NoticeBox> : null}

        {/* 账号归属收成一行带锁：分店 + 「仅查看」（新员工与本店的关系，不是你的权限）；角色、员工类型固定。 */}
        <InfoCard>
          <View style={styles.assignment} testID="staff-assignment">
            <View style={styles.assignmentIcon}>
              <Icon source="storefront-outline" size={20} color={HB_COLORS.brand} />
            </View>
            <View style={styles.assignmentText}>
              <View style={styles.assignmentTitleRow}>
                <Text style={styles.assignmentTitle} numberOfLines={2}>
                  {t("staff.assignmentTitle", { store: storeLabel })}
                </Text>
                {/* 新员工与这家店的关系是「仅查看」（后端 IsPrimary=false）；「可管理」是列表页里表示操作者能管这家店的徽标，不能放在这里误导成新员工是管理者。 */}
                <PeopleTag label={t("userManagement:currentStore.viewOnlyBadge")} tone="gray" icon="eye-outline" />
              </View>
              <Text style={styles.assignmentHint}>{t("staff.assignmentHint")}</Text>
            </View>
            <Icon source="lock-outline" size={18} color="#98A2B3" />
          </View>
        </InfoCard>

        <SectionTitle title={t("form.sectionName")} />
        <NameFields
          values={values.names}
          onChange={(key, value) => {
            // 新增账号：昵称默认跟名一致（见 applyCreateNameChange）。
            patch({ names: applyCreateNameChange(values.names, key, value) });
            setErrors((current) => ({ ...current, names: { ...current.names, [key]: undefined, nickname: undefined, fullName: undefined } }));
          }}
          errors={errors.names}
          phone={{
            value: values.phone,
            onChange: (value) => { patch({ phone: value }); setErrors((current) => ({ ...current, phone: undefined })); },
            error: errors.phone,
          }}
          disabled={pending}
        />

        <SectionTitle title={t("form.sectionLogin")} />
        <FormField
          label={t("staff.email")}
          required
          value={values.email}
          onChangeText={handleEmailChange}
          keyboardType="email-address"
          textContentType="emailAddress"
          maxLength={254}
          error={errors.email}
          hint={values.loginEnabled ? t("staff.emailHelper") : t("staff.emailHelperNoLogin")}
          disabled={pending}
          testID="staff-email"
        />
        <FormField
          label={t("staff.username")}
          required
          value={values.username}
          onChangeText={(value) => { setUsernameEdited(true); patch({ username: value }); setErrors((current) => ({ ...current, username: undefined })); }}
          maxLength={50}
          error={errors.username}
          hint={t("staff.usernameHelper")}
          disabled={pending}
          testID="staff-username"
        />

        <InfoCard>
          <SwitchRow
            first
            title={t("staff.enableNow")}
            description={t("staff.enableNowHelper")}
            value={values.enabled}
            onValueChange={(value) => patch({ enabled: value })}
            disabled={pending}
          />
          <SwitchRow
            title={t("form.loginEnabled")}
            description={values.loginEnabled ? t("staff.loginOnHelper") : t("staff.loginOffHelper")}
            value={values.loginEnabled}
            onValueChange={(value) => patch({ loginEnabled: value })}
            disabled={pending}
            testID="staff-login-switch"
          />
        </InfoCard>
      </FormScreen>
      <DiscardChangesDialog visible={leaveGuard.confirmVisible} onKeepEditing={leaveGuard.keepEditing} onDiscard={leaveGuard.discard} />
    </>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  assignment: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm, padding: 14 },
  assignmentIcon: { alignItems: "center", backgroundColor: "#EFF6FF", borderRadius: 10, height: 40, justifyContent: "center", width: 40 },
  assignmentText: { flex: 1, gap: 3, minWidth: 0 },
  assignmentTitleRow: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  assignmentTitle: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14.5, fontWeight: "800" },
  assignmentHint: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
});
