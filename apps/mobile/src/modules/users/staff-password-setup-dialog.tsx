import { useEffect, useState } from "react";
import { StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Dialog, HelperText, Text, TextInput } from "react-native-paper";

import { getDisplayableEmail, hasDeliverableEmail } from "@/modules/users/staff-email-username";
import { validateStaffRecoveryEmail } from "@/modules/users/validation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface StaffPasswordSetupDialogProps {
  visible: boolean;
  staffName: string;
  loginName: string;
  /** 员工当前邮箱，可能是 xxx@分店.store.local 占位邮箱。 */
  email: string | null | undefined;
  pending: boolean;
  onDismiss: () => void;
  /** 有可用邮箱时 email 为 undefined（直接重发）；补邮箱时传入店长填写的邮箱。 */
  onSubmit: (email?: string) => void;
}

/**
 * 店长「重置密码」弹窗（员工列表与员工详情共用）：
 * - 员工已有可用邮箱：说明会把设置密码验证码发到哪个邮箱；
 * - 老账号只有分店占位邮箱：就地填写员工邮箱，「保存邮箱并发送」一步完成。
 * 店长写入邮箱不另做验证——验证码只发到这个邮箱，只有邮箱本人能用它设密码。
 */
export function StaffPasswordSetupDialog({
  visible,
  staffName,
  loginName,
  email,
  pending,
  onDismiss,
  onSubmit,
}: StaffPasswordSetupDialogProps) {
  const { t } = useAppTranslation(["userManagement", "common"]);
  const needsEmail = !hasDeliverableEmail(email);
  const [draftEmail, setDraftEmail] = useState("");
  const [touched, setTouched] = useState(false);

  // 每次打开都从空白开始，避免把上一位员工的草稿带到下一位。
  useEffect(() => {
    if (visible) {
      setDraftEmail("");
      setTouched(false);
    }
  }, [visible]);

  const emailError = needsEmail ? validateStaffRecoveryEmail(draftEmail, t) : null;

  const handleSubmit = () => {
    if (!needsEmail) {
      onSubmit();
      return;
    }
    setTouched(true);
    if (emailError) return;
    onSubmit(draftEmail.trim());
  };

  return (
    <Dialog visible={visible} onDismiss={pending ? undefined : onDismiss}>
      <Dialog.Title>{t("dialogs.resetPasswordTitle")}</Dialog.Title>
      <Dialog.Content style={styles.content}>
        {needsEmail ? (
          <>
            <View style={styles.notice}>
              <MaterialCommunityIcons name="alert-outline" size={20} color={HB_COLORS.warning} />
              <View style={styles.noticeText}>
                <Text variant="titleSmall" style={styles.noticeTitle}>{t("dialogs.resetPasswordNoEmailTitle")}</Text>
                <Text variant="bodySmall" style={styles.noticeBody}>{t("dialogs.resetPasswordNoEmail")}</Text>
              </View>
            </View>
            <TextInput
              mode="outlined"
              label={t("dialogs.staffEmailLabel")}
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
              {touched && emailError ? emailError : t("dialogs.staffEmailHelper", { username: loginName })}
            </HelperText>
            <View style={styles.steps}>
              {[t("dialogs.setupStepSave"), t("dialogs.setupStepEmployee")].map((label, index) => (
                <View key={label} style={styles.step}>
                  <Text variant="labelMedium" style={styles.stepIndex}>{index + 1}</Text>
                  <Text variant="bodySmall" style={styles.stepText}>{label}</Text>
                </View>
              ))}
            </View>
          </>
        ) : (
          <Text variant="bodyMedium">
            {t("dialogs.resetPasswordEmailDescription", {
              username: staffName,
              email: getDisplayableEmail(email),
            })}
          </Text>
        )}
      </Dialog.Content>
      <Dialog.Actions>
        <Button onPress={onDismiss} disabled={pending}>
          {t("actions.cancel")}
        </Button>
        <Button onPress={handleSubmit} loading={pending} disabled={pending}>
          {needsEmail ? t("actions.saveEmailAndSend") : t("actions.sendPasswordSetupEmail")}
        </Button>
      </Dialog.Actions>
    </Dialog>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.sm },
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
