/**
 * 「个人信息」编辑页（路由 /users/[userGuid]/profile）：员工个人信息维护。
 *
 * - 可编辑（需 EmployeeProfiles.EditManagedStore，没有则整页只读、不显示保存按钮）：手机号、性别、居住地址、员工类型；
 *   员工类型仅当详情里 canEditEmploymentType === true 才可改，否则置灰带锁图标。
 * - 「敏感资料（只读）」：银行 / 公积金 Super / 生日·证件号只展示服务端返回的脱敏摘要或「未填写」，
 *   手机端不开放直接修改（需员工本人在「我的资料」提交，经审核后生效）。
 * - 保存只提交改动过的字段（后端用「请求里带没带」区分未改与清空；清空要显式传空字符串）。
 * - 数据范围（管理员全部分店 / 店长限管理分店、排除本人与受保护角色）与职位类型权限由服务端强制，
 *   越权返回 403（errorCode 见 managed-profile-logic.classifyManagedProfileFailure），客户端只给固定文案，不展示服务端原文。
 */
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Icon, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { DiscardChangesDialog, FormField, FormFooter, FormScreen, useLeaveGuard } from "../form/form-ui";
import { resolvePersonName } from "../person-name";
import { InfoCard, KvRow, NoticeBox, SectionTitle, SegmentTabs } from "../ui";
import { fetchManagedProfileDetail, updateManagedProfile } from "./managed-profile-api";
import {
  MANAGED_PROFILE_ADDRESS_MAX,
  MANAGED_PROFILE_EMPLOYMENT_TYPES,
  MANAGED_PROFILE_GENDERS,
  MANAGED_PROFILE_PHONE_MAX,
  buildManagedProfileEditBaseline,
  buildManagedProfileUpdatePayload,
  buildSensitiveSummary,
  classifyManagedProfileFailure,
  listManagedProfileChanges,
  readManagedProfileErrorMeta,
  resolveEditableFields,
  validateManagedProfileEdit,
  type ManagedProfileEditErrors,
  type ManagedProfileEditValues,
  type ManagedProfileEmploymentType,
  type ManagedProfileGender,
} from "./managed-profile-logic";

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] ?? "" : value ?? "");

export default function ManagedProfileEditScreen() {
  const params = useLocalSearchParams<{ userGuid?: string | string[] }>();
  const actorKey = useAuthStore((state) => state.user?.userGUID) ?? "";
  const userGuid = first(params.userGuid);
  // 更换账号或目标用户时销毁整份草稿，避免上一个编辑会话继续提交。
  return <ManagedProfileEditor key={`${actorKey}:${userGuid}`} userGuid={userGuid} />;
}

function ManagedProfileEditor({ userGuid }: { userGuid: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { t } = useAppTranslation(["peopleProfiles", "common"]);
  // 查看门禁：account 会话 + ViewManagedStore（设备模式 / iOS 审核会话没有该接口）。
  const session = useIdentitySession(PERMISSIONS.EmployeeProfiles.ViewManagedStore);
  const { actorKey, access } = session;
  const hasEditPermission = session.allowed && access.hasPermission(PERMISSIONS.EmployeeProfiles.EditManagedStore);

  const detailKey = useMemo(() => ["managed-profiles", actorKey, "detail", userGuid] as const, [actorKey, userGuid]);
  const detailQuery = useQuery({
    queryKey: detailKey,
    enabled: session.allowed && Boolean(userGuid),
    retry: false,
    queryFn: ({ signal }) => fetchManagedProfileDetail(userGuid, actorKey, signal),
  });
  const detail = detailQuery.data;
  const editAccess = useMemo(
    () => ({ canEdit: hasEditPermission, canEditEmploymentType: detail?.canEditEmploymentType === true }),
    [detail?.canEditEmploymentType, hasEditPermission],
  );

  const [baseline, setBaseline] = useState<ManagedProfileEditValues | null>(null);
  const [values, setValues] = useState<ManagedProfileEditValues | null>(null);
  const [errors, setErrors] = useState<ManagedProfileEditErrors>({});
  const [formError, setFormError] = useState("");
  const [errorTick, setErrorTick] = useState(0);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const inFlight = useRef(false);
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // 只用首次加载到的详情初始化表单：之后后台刷新不能覆盖用户正在编辑的内容。
  useEffect(() => {
    if (baseline || !detail) return;
    const next = buildManagedProfileEditBaseline(detail);
    setBaseline(next);
    setValues(next);
  }, [baseline, detail]);
  useEffect(() => () => {
    if (leaveTimer.current) clearTimeout(leaveTimer.current);
  }, []);

  const changes = useMemo(
    () => (baseline && values ? listManagedProfileChanges(baseline, values, editAccess) : []),
    [baseline, editAccess, values],
  );
  const dirty = changes.length > 0 && !saved;
  const leaveGuard = useLeaveGuard(dirty);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else {
      router.replace({ pathname: "/users/[userGuid]", params: { userGuid } } as unknown as Parameters<typeof router.replace>[0]);
    }
  }, [router, userGuid]);

  /** 提交前 / 返回后核对：会话仍是发起保存的账号，且仍持有 EditManagedStore（换账号 / 被撤权后不能继续写）。 */
  const isCurrentEditSession = () => {
    const state = useAuthStore.getState();
    return state.user?.userGUID === actorKey
      && state.sessionKind === "account"
      && state.isAuthenticated
      && !state.iosReviewOfflineGuardActive
      && state.access.hasPermission(PERMISSIONS.EmployeeProfiles.EditManagedStore);
  };

  const handleSubmit = async () => {
    if (inFlight.current || !baseline || !values || !detail || !hasEditPermission || changes.length === 0) return;
    setFormError("");
    const nextErrors = validateManagedProfileEdit(values);
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) {
      setFormError(t("edit.errors.fixErrors"));
      setErrorTick((tick) => tick + 1);
      return;
    }
    if (!isCurrentEditSession()) return;

    inFlight.current = true;
    setSaving(true);
    try {
      // 只提交改动过的字段；员工类型不可改时 buildManagedProfileUpdatePayload 不会带它。
      const updated = await updateManagedProfile(userGuid, buildManagedProfileUpdatePayload(baseline, values, editAccess), actorKey);
      if (!isCurrentEditSession()) return;
      queryClient.setQueryData(detailKey, updated);
      // 列表（行上的待补全状态）与详情页入口的副标题都要刷新。
      void queryClient.invalidateQueries({ queryKey: ["managed-profiles", actorKey, "list"] });
      setSaved(true);
      setSnackbarMessage(t("edit.saved"));
      // 先解除离开拦截再返回，避免弹出「放弃修改？」。
      leaveGuard.leaveWith(() => {
        leaveTimer.current = setTimeout(goBack, 700);
      });
    } catch (error) {
      // 日志只留状态码与错误码，不记服务端原文。
      const meta = readManagedProfileErrorMeta(error);
      console.warn("[managed-profile] save failed", meta);
      if (!isCurrentEditSession()) return;
      setFormError(t(`edit.failures.${classifyManagedProfileFailure(meta)}`));
      setErrorTick((tick) => tick + 1);
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  };

  // ───────── 非表单状态 ─────────
  const shell = (body: ReactNode) => (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>{body}</SafeAreaView>
  );
  const backAction = { label: t("common:actions.back"), icon: "arrow-left", onPress: goBack };
  if (!session.allowed || !userGuid) {
    return shell(<EmptyState title={t("edit.noAccess")} primaryAction={backAction} />);
  }
  if (detailQuery.isError) {
    const failure = classifyManagedProfileFailure(readManagedProfileErrorMeta(detailQuery.error));
    return shell(
      <EmptyState
        title={t("edit.loadFailed")}
        description={
          failure === "scope" || failure === "forbidden"
            ? t("edit.loadForbidden")
            : failure === "notFound" ? t("edit.failures.notFound") : undefined
        }
        primaryAction={{ label: t("edit.retry"), icon: "refresh", onPress: () => void detailQuery.refetch() }}
        secondaryAction={{ ...backAction, mode: "outlined" }}
      />,
    );
  }
  if (!detail || !baseline || !values) {
    return (
      <FormScreen title={t("edit.title")} onClose={goBack} closeLabel={t("edit.cancel")}>
        <ActivityIndicator style={styles.loading} />
      </FormScreen>
    );
  }

  const fields = resolveEditableFields(editAccess);
  const locked = saving || saved;
  const displayName = resolvePersonName({ fullName: detail.fullName, username: detail.username }).primary || detail.username;
  const submitLabel = changes.length > 0 ? t("edit.saveWithCount", { count: changes.length }) : t("edit.save");
  const employmentLocked = hasEditPermission && !editAccess.canEditEmploymentType;
  const sensitive = buildSensitiveSummary(detail);
  const set = (patch: Partial<ManagedProfileEditValues>) => {
    setValues({ ...values, ...patch });
    setErrors({});
  };

  return (
    <>
      <FormScreen
        title={t("edit.title")}
        subtitle={displayName}
        onClose={goBack}
        closeLabel={t("edit.cancel")}
        scrollToTopKey={errorTick}
        footer={hasEditPermission ? (
          <FormFooter
            cancelLabel={t("edit.cancel")}
            submitLabel={submitLabel}
            onCancel={goBack}
            onSubmit={() => void handleSubmit()}
            submitDisabled={changes.length === 0 || saved}
            loading={saving}
          />
        ) : undefined}
        overlay={<Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2000}>{snackbarMessage}</Snackbar>}
      >
        {formError ? <NoticeBox tone="red" icon="alert-circle-outline">{formError}</NoticeBox> : null}
        {!hasEditPermission ? <NoticeBox tone="blue">{t("edit.readOnlyNotice")}</NoticeBox> : null}

        <FormField
          label={t("edit.phone")}
          value={values.phone}
          onChangeText={(phone) => set({ phone })}
          placeholder={t("edit.phonePlaceholder")}
          keyboardType="phone-pad"
          maxLength={MANAGED_PROFILE_PHONE_MAX}
          readOnly={!fields.includes("phone")}
          disabled={locked}
          error={errors.phone ? t("edit.errors.phoneTooLong", { max: MANAGED_PROFILE_PHONE_MAX }) : undefined}
          testID="profile-phone"
        />

        <ChoiceBlock label={t("edit.gender")}>
          {fields.includes("gender") ? (
            <SegmentTabs<ManagedProfileGender>
              items={MANAGED_PROFILE_GENDERS.map((key) => ({ key, label: t(`edit.genders.${key}`) }))}
              value={values.gender}
              onChange={(gender) => !locked && set({ gender })}
            />
          ) : (
            <ReadOnlyBox text={t(`edit.genders.${values.gender}`)} />
          )}
        </ChoiceBlock>

        <ChoiceBlock label={t("edit.employmentType")}>
          {fields.includes("employmentType") ? (
            <SegmentTabs<ManagedProfileEmploymentType | "">
              items={MANAGED_PROFILE_EMPLOYMENT_TYPES.map((key) => ({ key, label: t(`edit.employmentTypes.${key}`) }))}
              value={values.employmentType}
              onChange={(employmentType) => !locked && set({ employmentType })}
            />
          ) : (
            <ReadOnlyBox
              text={values.employmentType ? t(`edit.employmentTypes.${values.employmentType}`) : t("edit.employmentUnset")}
              locked={employmentLocked}
              hint={employmentLocked ? t("edit.employmentLocked") : undefined}
            />
          )}
        </ChoiceBlock>

        <FormField
          label={t("edit.address")}
          value={values.address}
          onChangeText={(address) => set({ address })}
          placeholder={t("edit.addressPlaceholder")}
          maxLength={MANAGED_PROFILE_ADDRESS_MAX}
          autoCapitalize="words"
          readOnly={!fields.includes("address")}
          disabled={locked}
          error={errors.address ? t("edit.errors.addressTooLong", { max: MANAGED_PROFILE_ADDRESS_MAX }) : undefined}
          testID="profile-address"
        />

        <SectionTitle title={t("edit.sensitive.title")} />
        <InfoCard>
          {sensitive.map((row, index) => (
            <KvRow
              key={row.key}
              first={index === 0}
              label={t(`edit.sensitive.${row.key}`)}
              value={row.value ?? t("edit.sensitive.notProvided")}
              mono={row.value !== null}
            />
          ))}
        </InfoCard>
        <NoticeBox tone="amber" icon="lock-outline">{t("edit.sensitive.notice")}</NoticeBox>
      </FormScreen>
      <DiscardChangesDialog visible={leaveGuard.confirmVisible} onKeepEditing={leaveGuard.keepEditing} onDiscard={leaveGuard.discard} />
    </>
  );
}

/** 带标签的非文本输入块（分段选择 / 只读值），标签样式与 FormField 一致。 */
function ChoiceBlock({ label, children }: { label: string; children: ReactNode }) {
  return (
    <View style={styles.choice}>
      <Text style={styles.choiceLabel} numberOfLines={1}>{label}</Text>
      {children}
    </View>
  );
}

/** 置灰的只读值框；locked 时右侧带锁图标，hint 说明解锁条件。 */
function ReadOnlyBox({ text, locked = false, hint }: { text: string; locked?: boolean; hint?: string }) {
  return (
    <View style={styles.readOnlyWrap}>
      <View style={styles.readOnlyBox} accessibilityState={{ disabled: true }}>
        <Text style={styles.readOnlyText}>{text}</Text>
        {locked ? <Icon source="lock-outline" size={18} color={HB_COLORS.textSecondary} /> : null}
      </View>
      {hint ? <Text style={styles.readOnlyHint}>{hint}</Text> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  loading: { marginTop: 32 },
  choice: { gap: 4 },
  choiceLabel: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
  readOnlyWrap: { gap: 4 },
  readOnlyBox: {
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    justifyContent: "space-between",
    minHeight: 44,
    paddingHorizontal: 12,
  },
  readOnlyText: { color: HB_COLORS.textSecondary, flex: 1, fontSize: 15 },
  readOnlyHint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 16 },
});
