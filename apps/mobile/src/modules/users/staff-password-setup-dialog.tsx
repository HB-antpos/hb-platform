import { useState } from "react";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, Text, TextInput } from "react-native-paper";

import { InfoCard, KvRow, NoticeBox, SegmentTabs } from "@/modules/people";
import { PeopleSheet, SHEET_BUTTON_STYLE, SheetActions } from "@/modules/people/detail/people-sheet";
import { getDisplayableEmail, hasDeliverableEmail } from "@/modules/users/staff-email-username";
import { validatePasswordValue, validateStaffRecoveryEmail } from "@/modules/users/validation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface StaffPasswordSetupDialogProps {
  visible: boolean;
  staffName: string;
  loginName: string;
  /** 员工当前邮箱，可能是 xxx@分店.store.local 占位邮箱。 */
  email: string | null | undefined;
  pending: boolean;
  onDismiss: () => void;
  /** 有可用邮箱时 email 为 undefined（直接重发）；补邮箱时传入店长填写的邮箱。 */
  onSubmit: (email?: string) => void;
  /**
   * 可选：重置方式。默认只有邮件（店长版）；管理员版传 `{ email: true, direct: true }` 多出「直接设置新密码」，
   * 目标不是店员（发不了店员邮件）时传 `{ email: false, direct: true }`。
   */
  modes?: { email: boolean; direct: boolean };
  /** 可选：直接设置新密码（≥ 6 位）。仅管理员版传入。 */
  onSubmitDirectPassword?: (password: string) => void;
  /**
   * 可选：失败原因。弹层是 Paper Portal 底部弹层，会盖住页面里的 Snackbar，
   * 提交失败且弹层保持打开时，调用方用它在弹层内就地显示原因。
   */
  errorMessage?: string | null;
}

type PasswordMode = "email" | "direct";

/**
 * 「重置密码」底部弹层（员工列表、员工详情、编辑页共用，导出名与既有 props 保持兼容）：
 * - 发设置密码邮件（店长与管理员都可用）：员工已有可用邮箱 → 说明发到哪里；
 *   老账号只有分店占位邮箱 → 就地填写员工邮箱，「保存邮箱并发送」一步完成；
 * - 直接设置新密码（仅管理员版，≥ 6 位）。
 * 店长写入邮箱不另做验证——验证码只发到这个邮箱，只有邮箱本人能用它设密码。
 * 条件渲染：关闭即卸载，每次打开都从空白开始，不会把上一位员工的草稿带到下一位。
 */
export function StaffPasswordSetupDialog(props: StaffPasswordSetupDialogProps) {
  if (!props.visible) return null;
  return <PasswordSheet {...props} />;
}

function PasswordSheet({
  staffName,
  loginName,
  email,
  pending,
  onDismiss,
  onSubmit,
  modes = { email: true, direct: false },
  onSubmitDirectPassword,
  errorMessage,
}: StaffPasswordSetupDialogProps) {
  const { t } = useAppTranslation(["userManagement", "peopleDetail", "common"]);
  const { fontScale, width } = useWindowDimensions();
  // 大字号或窄屏：底部按钮改纵向，避免文字被截断。
  const stacked = fontScale > 1.2 || width < 380;
  const availableModes = [
    ...(modes.email ? (["email"] as const) : []),
    ...(modes.direct && onSubmitDirectPassword ? (["direct"] as const) : []),
  ];
  const [mode, setMode] = useState<PasswordMode>(availableModes[0] ?? "email");
  const needsEmail = !hasDeliverableEmail(email);
  const [draftEmail, setDraftEmail] = useState("");
  const [touched, setTouched] = useState(false);
  const [draftPassword, setDraftPassword] = useState("");
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [passwordTouched, setPasswordTouched] = useState(false);

  const emailError = needsEmail ? validateStaffRecoveryEmail(draftEmail, t) : null;
  const passwordError = validatePasswordValue(draftPassword, t);

  const handleSubmit = () => {
    if (mode === "direct") {
      setPasswordTouched(true);
      if (passwordError || !onSubmitDirectPassword) return;
      onSubmitDirectPassword(draftPassword);
      return;
    }
    if (!needsEmail) {
      onSubmit();
      return;
    }
    setTouched(true);
    if (emailError) return;
    onSubmit(draftEmail.trim());
  };

  const submitLabel = mode === "direct"
    ? t("peopleDetail:password.directSubmit")
    : needsEmail
      ? t("userManagement:actions.saveEmailAndSend")
      : t("userManagement:actions.sendPasswordSetupEmail");

  return (
    <PeopleSheet
      title={t("peopleDetail:password.title")}
      subtitle={`${staffName} · @${loginName}`}
      closeLabel={t("common:actions.close")}
      onDismiss={onDismiss}
      dismissable={!pending}
      testID="staff-password-sheet"
      footer={(
        <SheetActions stacked={stacked}>
          <Button mode="outlined" onPress={onDismiss} disabled={pending} style={SHEET_BUTTON_STYLE}>
            {t("userManagement:actions.cancel")}
          </Button>
          <Button mode="contained" onPress={handleSubmit} loading={pending} disabled={pending} style={SHEET_BUTTON_STYLE}>
            {submitLabel}
          </Button>
        </SheetActions>
      )}
    >
      {availableModes.length > 1 ? (
        <SegmentTabs
          items={[
            { key: "email" as const, label: t("peopleDetail:password.modeEmail") },
            { key: "direct" as const, label: t("peopleDetail:password.modeDirect") },
          ]}
          value={mode}
          onChange={setMode}
        />
      ) : null}

      {mode === "direct" ? (
        <>
          <TextInput
            mode="outlined"
            label={t("userManagement:fields.newPassword")}
            value={draftPassword}
            onChangeText={setDraftPassword}
            onBlur={() => setPasswordTouched(true)}
            secureTextEntry={!passwordVisible}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="newPassword"
            maxLength={100}
            disabled={pending}
            error={passwordTouched && Boolean(passwordError)}
            right={<TextInput.Icon icon={passwordVisible ? "eye-off-outline" : "eye-outline"} onPress={() => setPasswordVisible((value) => !value)} />}
          />
          <HelperText type={passwordTouched && passwordError ? "error" : "info"} visible style={styles.helper}>
            {passwordTouched && passwordError ? passwordError : t("peopleDetail:password.directHelper")}
          </HelperText>
        </>
      ) : needsEmail ? (
        <>
          <View style={styles.notice}>
            <MaterialCommunityIcons name="alert-outline" size={20} color={HB_COLORS.warning} />
            <View style={styles.noticeText}>
              <Text variant="titleSmall" style={styles.noticeTitle}>{t("userManagement:dialogs.resetPasswordNoEmailTitle")}</Text>
              <Text variant="bodySmall" style={styles.noticeBody}>{t("userManagement:dialogs.resetPasswordNoEmail")}</Text>
            </View>
          </View>
          <TextInput
            mode="outlined"
            label={t("userManagement:dialogs.staffEmailLabel")}
            value={draftEmail}
            onChangeText={setDraftEmail}
            onBlur={() => setTouched(true)}
            keyboardType="email-address"
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="emailAddress"
            maxLength={254}
            disabled={pending}
            error={touched && Boolean(emailError)}
            left={<TextInput.Icon icon="email-outline" />}
          />
          <HelperText type={touched && emailError ? "error" : "info"} visible style={styles.helper}>
            {touched && emailError ? emailError : t("userManagement:dialogs.staffEmailHelper", { username: loginName })}
          </HelperText>
          <View style={styles.steps}>
            {[t("userManagement:dialogs.setupStepSave"), t("userManagement:dialogs.setupStepEmployee")].map((label, index) => (
              <View key={label} style={styles.step}>
                <Text variant="labelMedium" style={styles.stepIndex}>{index + 1}</Text>
                <Text variant="bodySmall" style={styles.stepText}>{label}</Text>
              </View>
            ))}
          </View>
        </>
      ) : (
        <InfoCard>
          <KvRow first label={t("peopleDetail:password.sendTo")} value={getDisplayableEmail(email)} />
          <Text style={styles.cardHint}>{t("peopleDetail:password.emailHint")}</Text>
        </InfoCard>
      )}

      {errorMessage ? <NoticeBox tone="red" icon="alert-circle-outline">{errorMessage}</NoticeBox> : null}
      {availableModes.length > 1 ? <Text style={styles.footnote}>{t("peopleDetail:password.modesFootnote")}</Text> : null}
    </PeopleSheet>
  );
}

const styles = StyleSheet.create({
  notice: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#FFFAEB",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#FEDF89",
  },
  noticeText: { flex: 1, gap: 2 },
  noticeTitle: { color: HB_COLORS.warning, fontWeight: "700" },
  noticeBody: { color: HB_COLORS.warning },
  helper: { paddingHorizontal: 0, marginTop: -HB_SPACING.xs },
  cardHint: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18, paddingBottom: 10, paddingHorizontal: 14 },
  footnote: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
  steps: {
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  step: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "flex-start" },
  stepIndex: {
    width: 20,
    height: 20,
    borderRadius: 10,
    textAlign: "center",
    lineHeight: 20,
    backgroundColor: HB_COLORS.white,
    color: HB_COLORS.textPrimary,
    overflow: "hidden",
  },
  stepText: { flex: 1, color: HB_COLORS.textSecondary },
});
