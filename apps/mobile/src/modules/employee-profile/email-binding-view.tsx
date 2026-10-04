import { useEffect, useState } from "react";
import { Platform, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, Surface, Text, TextInput } from "react-native-paper";

import { confirmEmailChangeApi, requestEmailChangeCodeApi } from "@/modules/auth/api";
import { isResetCodeFormatValid, isValidResetEmail, normalizeResetCode } from "@/modules/auth/password-reset";
import { getDisplayableEmail, hasDeliverableEmail } from "@/modules/users/staff-email-username";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const RESEND_SECONDS = 60;

interface EmailBindingViewProps {
  loginName: string;
  /** 当前账号邮箱，可能是分店占位邮箱（显示为「未设置」）。 */
  currentEmail: string | null | undefined;
  onBound: (email: string) => void;
  onCancel: () => void;
}

/**
 * 员工绑定 / 更换自己的账号邮箱（个人信息 → 账户与安全）。
 * 邮箱是找回密码的渠道，所以先给新邮箱发 6 位验证码，验证通过才替换；没验证前旧邮箱照常可用。
 */
export function EmailBindingView({ loginName, currentEmail, onBound, onCancel }: EmailBindingViewProps) {
  const { t, language } = useAppTranslation(["employeeProfile", "common"]);
  const [step, setStep] = useState<"email" | "code">("email");
  const [newEmail, setNewEmail] = useState("");
  const [maskedEmail, setMaskedEmail] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [errorMessage, setErrorMessage] = useState("");
  const [resendIn, setResendIn] = useState(0);
  const displayableCurrent = getDisplayableEmail(currentEmail);

  useEffect(() => {
    if (resendIn <= 0) return;
    const timer = setTimeout(() => setResendIn((value) => value - 1), 1000);
    return () => clearTimeout(timer);
  }, [resendIn]);

  const validateTarget = () => {
    const trimmed = newEmail.trim();
    if (!isValidResetEmail(trimmed) || !hasDeliverableEmail(trimmed)) return t("emailBinding.errors.email");
    if (trimmed.toLowerCase() === displayableCurrent.toLowerCase()) return t("emailBinding.errors.same");
    return "";
  };

  const sendCode = async () => {
    const validation = validateTarget();
    if (validation) {
      setErrorMessage(validation);
      return;
    }
    setBusy(true);
    setErrorMessage("");
    try {
      const result = await requestEmailChangeCodeApi(newEmail.trim());
      setMaskedEmail(result.maskedEmail || newEmail.trim());
      setStep("code");
      setCode("");
      setResendIn(RESEND_SECONDS);
    } catch (error) {
      setErrorMessage(resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "emailBinding.errors.sendFailed" }));
    } finally {
      setBusy(false);
    }
  };

  const confirm = async () => {
    if (!isResetCodeFormatValid(code) || busy) return;
    setBusy(true);
    setErrorMessage("");
    try {
      const result = await confirmEmailChangeApi({ newEmail: newEmail.trim(), code: normalizeResetCode(code) });
      onBound(result.email);
    } catch (error) {
      setErrorMessage(resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "emailBinding.errors.confirmFailed" }));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <View style={styles.stepper} accessibilityRole="progressbar" accessibilityLabel={t("emailBinding.stepLabel", { step: step === "email" ? 1 : 2 })}>
        <View style={styles.stepItem}>
          <View style={[styles.stepDot, step === "email" ? styles.stepDotCurrent : styles.stepDotDone]}>
            {step === "email"
              ? <Text style={styles.stepDotText}>1</Text>
              : <MaterialCommunityIcons name="check" size={14} color={HB_COLORS.success} />}
          </View>
          <Text variant="labelLarge" style={step === "email" ? styles.stepLabelCurrent : styles.stepLabel}>{t("emailBinding.stepEmail")}</Text>
        </View>
        <View style={styles.stepLine} />
        <View style={styles.stepItem}>
          <View style={[styles.stepDot, step === "code" ? styles.stepDotCurrent : styles.stepDotIdle]}>
            <Text style={step === "code" ? styles.stepDotText : styles.stepDotIdleText}>2</Text>
          </View>
          <Text variant="labelLarge" style={step === "code" ? styles.stepLabelCurrent : styles.stepLabel}>{t("emailBinding.stepVerify")}</Text>
        </View>
      </View>

      <Surface style={styles.card} elevation={0}>
        <View style={styles.row}>
          <Text variant="bodyMedium" style={styles.rowLabel}>{t("emailBinding.loginName")}</Text>
          <Text variant="bodyLarge" style={styles.mono}>{loginName}</Text>
        </View>
        <View style={[styles.row, styles.rowDivider]}>
          <Text variant="bodyMedium" style={styles.rowLabel}>{t("emailBinding.currentEmail")}</Text>
          <Text variant="bodyLarge" style={displayableCurrent ? undefined : styles.muted} numberOfLines={1}>
            {displayableCurrent || t("emailBinding.notSet")}
          </Text>
        </View>
      </Surface>

      <Surface style={styles.card} elevation={0}>
        <TextInput
          mode="outlined"
          label={t("emailBinding.newEmail")}
          value={newEmail}
          onChangeText={(value) => { setNewEmail(value); setErrorMessage(""); }}
          keyboardType="email-address"
          autoCapitalize="none"
          autoCorrect={false}
          textContentType="emailAddress"
          maxLength={254}
          editable={step === "email" && !busy}
          left={<TextInput.Icon icon="email-outline" />}
          right={step === "code"
            ? <TextInput.Icon icon="pencil-outline" accessibilityLabel={t("emailBinding.changeEmail")} onPress={() => { setStep("email"); setCode(""); setErrorMessage(""); }} />
            : undefined}
        />
        {step === "code" ? (
          <>
            <TextInput
              mode="outlined"
              label={t("emailBinding.code")}
              value={code}
              onChangeText={(value) => { setCode(normalizeResetCode(value)); setErrorMessage(""); }}
              keyboardType="number-pad"
              textContentType="oneTimeCode"
              autoComplete="one-time-code"
              maxLength={6}
              style={styles.codeInput}
            />
            <View style={styles.resendRow}>
              <Text variant="bodySmall" style={styles.resendText}>{t("emailBinding.codeSent", { email: maskedEmail })}</Text>
              <Button compact mode="text" onPress={() => void sendCode()} disabled={busy || resendIn > 0}>
                {resendIn > 0 ? t("emailBinding.resendIn", { seconds: resendIn }) : t("emailBinding.resend")}
              </Button>
            </View>
          </>
        ) : null}
        <HelperText type="error" visible={Boolean(errorMessage)} style={styles.helper}>{errorMessage}</HelperText>
        <View style={styles.notice}>
          <MaterialCommunityIcons name="information-outline" size={18} color={HB_COLORS.action} />
          <Text variant="bodySmall" style={styles.noticeText}>{t("emailBinding.notice")}</Text>
        </View>
        {step === "email" ? (
          <Button mode="contained" onPress={() => void sendCode()} loading={busy} disabled={busy} contentStyle={styles.buttonContent}>
            {t("emailBinding.sendCode")}
          </Button>
        ) : (
          <Button mode="contained" onPress={() => void confirm()} loading={busy} disabled={busy || !isResetCodeFormatValid(code)} contentStyle={styles.buttonContent}>
            {t("emailBinding.submit")}
          </Button>
        )}
        <Button mode="text" onPress={onCancel} disabled={busy}>{t("common:actions.cancel")}</Button>
      </Surface>
    </>
  );
}

const styles = StyleSheet.create({
  stepper: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.xxs },
  stepItem: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  stepDot: { width: 22, height: 22, borderRadius: 11, alignItems: "center", justifyContent: "center" },
  stepDotCurrent: { backgroundColor: HB_COLORS.action },
  stepDotDone: { backgroundColor: "#E7F6EC" },
  stepDotIdle: { borderWidth: 1.5, borderColor: HB_COLORS.outline },
  stepDotText: { color: HB_COLORS.white, fontSize: 12, fontWeight: "700" },
  stepDotIdleText: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700" },
  stepLabel: { color: HB_COLORS.textSecondary },
  stepLabelCurrent: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stepLine: { flex: 1, height: 2, borderRadius: 1, backgroundColor: HB_COLORS.outline },
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
  },
  row: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 36 },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted, paddingTop: HB_SPACING.sm },
  rowLabel: { color: HB_COLORS.textSecondary },
  mono: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }), color: HB_COLORS.textPrimary },
  muted: { color: HB_COLORS.textSecondary },
  codeInput: { letterSpacing: 6, fontSize: 20 },
  resendRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  resendText: { flex: 1, color: HB_COLORS.textSecondary },
  helper: { paddingHorizontal: 0, marginVertical: -HB_SPACING.xs },
  notice: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#EAF2FF",
  },
  noticeText: { flex: 1, color: "#073B83" },
  buttonContent: { minHeight: 48 },
});
