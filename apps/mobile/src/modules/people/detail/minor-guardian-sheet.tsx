import type { ReactNode } from "react";
import { useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { useMutation } from "@tanstack/react-query";
import { Button, Icon, Switch, Text } from "react-native-paper";
import { FilterChipRow, NoticeBox, PeopleTag, SegmentTabs } from "@/modules/people";
import { FormField } from "@/modules/people/form/form-ui";
import { minorEmploymentStaffApi } from "@/modules/minor-employment/staff-api";
import {
  GUARDIAN_LINK_ERROR_KEYS,
  GUARDIAN_LINK_EXPIRY_OPTIONS,
  GUARDIAN_RELATIONSHIPS,
  MINOR_VERSION_CONFLICT_CODE,
  hasGuardianLinkFormErrors,
  readErrorCode,
  resolveGuardianLinkDefaults,
  resolveRelationshipOption,
  resolveSmsBlockReason,
  validateGuardianLinkForm,
  type GuardianLinkExpiryDays,
  type GuardianLinkForm,
  type GuardianLinkSendResult,
  type MinorStaffComplianceItem,
} from "@/modules/minor-employment/staff-contract";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { PeopleSheet, SHEET_BUTTON_STYLE, SheetActions } from "./people-sheet";

type Translate = (key: string, options?: Record<string, unknown>) => string;

/**
 * 发送 / 撤销家长签字链接失败时的提示：英文界面按错误码给英文文案；
 * 中文界面直接显示后端中文原文（网络、超时、未登录等通用错误仍走 resolveLocalizedErrorMessage）。
 */
export function resolveMinorGuardianError(error: unknown, t: Translate, language: string, fallbackKey: string) {
  const code = readErrorCode(error);
  const key = code ? GUARDIAN_LINK_ERROR_KEYS[code] : undefined;
  if (key && !language.toLowerCase().startsWith("zh")) return t(`peopleDetail:minor.errors.${key}`);
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey });
}

/**
 * 发送家长签字链接的底部弹层（设计图 1 第 2 屏）：监护人姓名 / 关系、邮件与短信各一个开关、有效期 1/3/7 天。
 * 条件渲染：关闭即卸载，每次打开都按最新档案重新预填。
 */
export function MinorGuardianSheet({
  userGuid,
  item,
  smsAvailable,
  onDismiss,
  onSent,
  onConflict,
}: {
  userGuid: string;
  /** 由父级查询实时传入：版本冲突刷新后，下一次提交自动带上新的版本号。 */
  item: MinorStaffComplianceItem;
  smsAvailable: boolean;
  onDismiss: () => void;
  onSent: (result: GuardianLinkSendResult) => void;
  onConflict: () => void;
}) {
  const { t, language } = useAppTranslation(["peopleDetail", "common"]);
  const [form, setForm] = useState<GuardianLinkForm>(() => resolveGuardianLinkDefaults(item, smsAvailable));
  const [submitted, setSubmitted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const smsBlock = resolveSmsBlockReason(smsAvailable, form.guardianPhone);
  // 短信不可用时开关强制视为关闭：提交、提示文案都按「实际会不会发短信」来算。
  const smsOn = form.sendSms && smsBlock === null;
  const effectiveForm = useMemo<GuardianLinkForm>(() => ({ ...form, sendSms: smsOn }), [form, smsOn]);
  const errors = useMemo(() => validateGuardianLinkForm(effectiveForm, smsAvailable), [effectiveForm, smsAvailable]);
  const isResend = item.canRevoke || item.stage === "awaiting_guardian";

  const send = useMutation({
    mutationFn: () => minorEmploymentStaffApi.sendGuardianLink(userGuid, item, effectiveForm),
    onSuccess: (result) => onSent(result),
    onError: (error) => {
      // 别人抢先改过：刷新档案（父级查询会把新版本号传进来），提示核对后重发。
      if (readErrorCode(error) === MINOR_VERSION_CONFLICT_CODE) onConflict();
      setServerError(resolveMinorGuardianError(error, t, language, "peopleDetail:minor.errors.sendFailed"));
    },
  });

  const update = (patch: Partial<GuardianLinkForm>) => {
    setServerError(null);
    setForm((current) => ({ ...current, ...patch }));
  };
  const handleSubmit = () => {
    setSubmitted(true);
    setServerError(null);
    if (hasGuardianLinkFormErrors(errors)) return;
    send.mutate();
  };
  const fieldError = (code: string | undefined) => (submitted && code ? t(`peopleDetail:minor.validation.${code}`) : undefined);

  // 关系：四个固定选项提交英文值；已有值不在列表里时作为额外选项原样保留。
  const relationshipOption = resolveRelationshipOption(form.guardianRelationship);
  const customRelationship = form.guardianRelationship.trim() && !relationshipOption ? form.guardianRelationship.trim() : null;
  const relationshipItems = [
    ...(customRelationship ? [{ key: customRelationship, label: customRelationship }] : []),
    ...GUARDIAN_RELATIONSHIPS.map((option) => ({ key: option as string, label: t(`peopleDetail:minor.relationships.${option}`) })),
  ];
  const expiryItems = GUARDIAN_LINK_EXPIRY_OPTIONS.map((days) => ({
    key: String(days),
    label: t("peopleDetail:minor.sheet.expiryDays", { count: days }),
  }));

  const codeNote = form.sendEmail && smsOn
    ? t("peopleDetail:minor.sheet.codeBoth")
    : smsOn
      ? t("peopleDetail:minor.sheet.codeSms")
      : form.sendEmail
        ? t("peopleDetail:minor.sheet.codeEmail")
        : "";
  const stateLabel = item.stateCode === "UNKNOWN" ? t("common:na") : item.stateCode;
  const pending = send.isPending;

  return (
    <PeopleSheet
      title={t("peopleDetail:minor.sheet.title")}
      subtitle={t("peopleDetail:minor.sheet.subtitle", { name: item.employeeName, age: item.age, state: stateLabel })}
      closeLabel={t("common:actions.close")}
      onDismiss={onDismiss}
      dismissable={!pending}
      testID="minor-guardian-sheet"
      footer={(
        <SheetActions>
          <Button mode="outlined" onPress={onDismiss} disabled={pending} style={SHEET_BUTTON_STYLE}>
            {t("peopleDetail:minor.sheet.cancel")}
          </Button>
          <Button
            mode="contained"
            icon="send"
            buttonColor={HB_COLORS.action}
            textColor={HB_COLORS.white}
            onPress={handleSubmit}
            loading={pending}
            disabled={pending}
            style={SHEET_BUTTON_STYLE}
            testID="minor-guardian-submit"
          >
            {t("peopleDetail:minor.sheet.submit")}
          </Button>
        </SheetActions>
      )}
    >
      <FormField
        label={t("peopleDetail:minor.sheet.guardianName")}
        required
        value={form.guardianName}
        onChangeText={(value) => update({ guardianName: value })}
        autoCapitalize="words"
        maxLength={200}
        error={fieldError(errors.name)}
        disabled={pending}
      />
      <View style={styles.block}>
        <Text style={styles.label}>{t("peopleDetail:minor.sheet.relationship")}</Text>
        <FilterChipRow
          items={relationshipItems}
          value={relationshipOption ?? customRelationship ?? ""}
          onChange={(key) => update({ guardianRelationship: key })}
        />
      </View>

      <ChannelToggle
        icon="email-outline"
        title={t("peopleDetail:minor.sheet.sendEmail")}
        value={form.sendEmail}
        onValueChange={(value) => update({ sendEmail: value })}
        disabled={pending}
        testID="minor-guardian-email-switch"
      >
        <FormField
          label={t("peopleDetail:minor.sheet.emailPlaceholder")}
          required={form.sendEmail}
          value={form.guardianEmail}
          onChangeText={(value) => update({ guardianEmail: value })}
          placeholder="name@example.com"
          keyboardType="email-address"
          autoComplete="email"
          maxLength={254}
          error={fieldError(errors.email)}
          disabled={pending}
        />
      </ChannelToggle>

      <ChannelToggle
        icon="message-text-outline"
        title={t("peopleDetail:minor.sheet.sendSms")}
        tag={smsAvailable
          ? <PeopleTag label={t("peopleDetail:minor.sheet.smsOn")} tone="green" />
          : <PeopleTag label={t("peopleDetail:minor.sheet.smsOff")} tone="gray" />}
        value={smsOn}
        onValueChange={(value) => update({ sendSms: value })}
        disabled={pending || smsBlock !== null}
        hint={smsBlock === "serviceOff"
          ? t("peopleDetail:minor.sheet.smsServiceOffHint")
          : smsBlock === "notMobile" ? t("peopleDetail:minor.sheet.smsNotMobileHint") : undefined}
        testID="minor-guardian-sms-switch"
      >
        <FormField
          label={t("peopleDetail:minor.sheet.phone")}
          required
          value={form.guardianPhone}
          onChangeText={(value) => update({ guardianPhone: value })}
          placeholder={t("peopleDetail:minor.sheet.phonePlaceholder")}
          keyboardType="phone-pad"
          autoComplete="tel"
          maxLength={50}
          error={fieldError(errors.phone)}
          disabled={pending}
        />
      </ChannelToggle>

      <View style={styles.block}>
        <Text style={styles.label}>{t("peopleDetail:minor.sheet.expiry")}</Text>
        <SegmentTabs
          items={expiryItems}
          value={String(form.expiryDays)}
          onChange={(key) => update({ expiryDays: Number(key) as GuardianLinkExpiryDays })}
        />
      </View>

      <NoticeBox tone="blue" icon="information-outline">
        {[codeNote, t("peopleDetail:minor.sheet.englishNote")].filter(Boolean).join(language.toLowerCase().startsWith("zh") ? "" : " ")}
      </NoticeBox>
      {isResend ? (
        <NoticeBox tone="amber" icon="alert-outline">{t("peopleDetail:minor.sheet.resendWarning")}</NoticeBox>
      ) : null}
      {submitted && errors.channel ? (
        <NoticeBox tone="red" icon="alert-circle-outline">{t(`peopleDetail:minor.validation.${errors.channel}`)}</NoticeBox>
      ) : null}
      {serverError ? <NoticeBox tone="red" icon="alert-circle-outline">{serverError}</NoticeBox> : null}
    </PeopleSheet>
  );
}

/** 邮件 / 短信开关行：图标 + 标题 + 状态小标签 + 开关；不可用原因写在开关下方；下面跟对应的输入框。 */
function ChannelToggle({
  icon,
  title,
  tag,
  value,
  onValueChange,
  disabled,
  hint,
  children,
  testID,
}: {
  icon: string;
  title: string;
  tag?: ReactNode;
  value: boolean;
  onValueChange: (value: boolean) => void;
  disabled: boolean;
  hint?: string;
  children: ReactNode;
  testID?: string;
}) {
  return (
    <View style={styles.block}>
      <View style={[styles.channelHead, value && styles.channelHeadOn]}>
        <Icon source={icon} size={18} color={value ? HB_COLORS.action : HB_COLORS.textSecondary} />
        <Text style={styles.channelTitle} numberOfLines={1}>{title}</Text>
        {/* PeopleTag 自带 alignSelf: flex-start，包一层让它跟图标、标题、开关垂直居中。 */}
        {tag ? <View style={styles.tagWrap}>{tag}</View> : null}
        <View style={styles.spacer} />
        {/* RN 的 iOS Switch 默认自带 alignSelf: flex-start，会贴到行顶；显式居中才和图标、标题对齐。 */}
        <Switch value={value} onValueChange={onValueChange} disabled={disabled} accessibilityLabel={title} testID={testID} style={styles.switch} />
      </View>
      {hint ? <Text style={styles.channelHint}>{hint}</Text> : null}
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  block: { gap: 6 },
  label: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
  channelHead: {
    alignItems: "center",
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: 8,
    minHeight: 46,
    paddingLeft: 12,
    paddingRight: 4,
  },
  channelHeadOn: { backgroundColor: "#F5F8FF", borderColor: "#B2CCFF" },
  channelTitle: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14.5, fontWeight: "700" },
  channelHint: { color: HB_COLORS.warning, fontSize: 12, lineHeight: 17, paddingHorizontal: 2 },
  spacer: { flex: 1, minWidth: HB_SPACING.xs },
  tagWrap: { justifyContent: "center" },
  switch: { alignSelf: "center" },
});
