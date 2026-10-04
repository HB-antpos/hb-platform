import { useEffect, useState } from "react";
import * as Clipboard from "expo-clipboard";
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, IconButton, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { confirmPasswordResetApi, requestPasswordResetCodeApi } from "@/modules/auth/api";
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  getPasswordResetRuleState,
  isResetCodeFormatValid,
  isValidResetEmail,
  normalizeResetCode,
} from "@/modules/auth/password-reset";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const RESEND_SECONDS = 60;

/**
 * 登录页「设置 / 忘记密码」：
 * 1) 输入邮箱 → 后端发 6 位验证码（未注册邮箱也返回同一提示，不透露账号是否存在）；
 * 2) 输入验证码 + 新密码 → 设置成功后回登录页用新密码登录。
 * 店长新建员工或重置密码后，员工收到的邀请验证码也在这里使用。
 */
export default function ResetPasswordScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ email?: string }>();
  const { t, language } = useAppTranslation(["login", "common"]);
  const [step, setStep] = useState<"email" | "code" | "done">("email");
  const [email, setEmail] = useState(typeof params.email === "string" ? params.email : "");
  const [code, setCode] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errorMessage, setErrorMessage] = useState("");
  const [resendIn, setResendIn] = useState(0);
  const [loginName, setLoginName] = useState("");
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (resendIn <= 0) return;
    const timer = setTimeout(() => setResendIn((value) => value - 1), 1000);
    return () => clearTimeout(timer);
  }, [resendIn]);

  const rules = getPasswordResetRuleState(newPassword, confirmPassword);
  const canConfirm = isResetCodeFormatValid(code) && rules.minLength && rules.confirmed && !busy;

  const goToLogin = () => {
    if (router.canGoBack()) router.back();
    else router.replace("/(auth)/login");
  };

  // 设好密码后回到登录页并预填登录名：navigate 会回到栈里已有的登录页并更新参数。
  const goToLoginWithName = () => {
    if (!loginName) {
      goToLogin();
      return;
    }
    router.navigate({ pathname: "/(auth)/login", params: { username: loginName } } as unknown as Parameters<typeof router.navigate>[0]);
  };

  const copyLoginName = async () => {
    if (!loginName) return;
    await Clipboard.setStringAsync(loginName);
    setCopied(true);
  };

  const sendCode = async () => {
    if (!isValidResetEmail(email)) {
      setErrorMessage(t("resetPassword.errors.email"));
      return;
    }
    setBusy(true);
    setErrorMessage("");
    try {
      await requestPasswordResetCodeApi(email.trim());
      setStep("code");
      setResendIn(RESEND_SECONDS);
    } catch (error) {
      setErrorMessage(resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "resetPassword.errors.sendFailed" }));
    } finally {
      setBusy(false);
    }
  };

  const confirm = async () => {
    if (!canConfirm) return;
    setBusy(true);
    setErrorMessage("");
    try {
      const result = await confirmPasswordResetApi({
        email: email.trim(),
        code: normalizeResetCode(code),
        newPassword: newPassword.trim(),
        confirmPassword: confirmPassword.trim(),
      });
      setLoginName(result.loginName);
      setStep("done");
    } catch (error) {
      setErrorMessage(resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "resetPassword.errors.confirmFailed" }));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right", "bottom"]}>
      <View style={styles.header}>
        <IconButton icon="arrow-left" size={22} accessibilityLabel={t("common:actions.back")} onPress={goToLogin} />
        <Text variant="titleLarge" style={styles.headerTitle}>{t("resetPassword.title")}</Text>
        <View style={styles.headerSpacer} />
      </View>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          {step === "done" ? (
            <View style={styles.doneBlock}>
              <View style={styles.doneIcon}>
                <MaterialCommunityIcons name="check" size={32} color={HB_COLORS.success} />
              </View>
              <Text variant="headlineSmall" style={styles.title}>{t("resetPassword.doneTitle")}</Text>
              <Text variant="bodyMedium" style={styles.centerSecondary}>{t("resetPassword.doneDescription")}</Text>
              {loginName ? (
                <>
                  <View style={styles.loginNameCard}>
                    <View style={styles.loginNameText}>
                      <Text variant="bodySmall" style={styles.secondary}>{t("resetPassword.loginNameLabel")}</Text>
                      <Text variant="titleLarge" style={styles.loginNameValue} selectable>{loginName}</Text>
                    </View>
                    <Button mode="outlined" compact icon={copied ? "check" : "content-copy"} onPress={() => void copyLoginName()}>
                      {copied ? t("resetPassword.copied") : t("resetPassword.copy")}
                    </Button>
                  </View>
                  <Text variant="bodySmall" style={styles.loginNameHint}>{t("resetPassword.loginNameHint")}</Text>
                </>
              ) : null}
              <Button mode="contained" buttonColor={HB_COLORS.action} onPress={goToLoginWithName} style={styles.fullButton} contentStyle={styles.buttonContent}>
                {loginName ? t("resetPassword.backToLoginPrefilled") : t("resetPassword.backToLogin")}
              </Button>
            </View>
          ) : (
            <>
              <View style={styles.intro}>
                <View style={styles.introIcon}>
                  <MaterialCommunityIcons name={step === "email" ? "email-outline" : "shield-key-outline"} size={28} color={HB_COLORS.action} />
                </View>
                <Text variant="bodyMedium" style={styles.secondary}>
                  {step === "email" ? t("resetPassword.emailDescription") : t("resetPassword.codeDescription", { email: email.trim() })}
                </Text>
              </View>

              <TextInput
                mode="outlined"
                label={t("resetPassword.email")}
                value={email}
                onChangeText={(value) => { setEmail(value); setErrorMessage(""); }}
                keyboardType="email-address"
                autoCapitalize="none"
                autoCorrect={false}
                textContentType="emailAddress"
                editable={step === "email" && !busy}
                right={step === "code" ? <TextInput.Icon icon="pencil-outline" accessibilityLabel={t("resetPassword.changeEmail")} onPress={() => { setStep("email"); setCode(""); }} /> : undefined}
              />

              {step === "code" ? (
                <>
                  <TextInput
                    mode="outlined"
                    label={t("resetPassword.code")}
                    value={code}
                    onChangeText={(value) => { setCode(normalizeResetCode(value)); setErrorMessage(""); }}
                    keyboardType="number-pad"
                    textContentType="oneTimeCode"
                    autoComplete="one-time-code"
                    maxLength={6}
                    style={styles.codeInput}
                  />
                  <View style={styles.resendRow}>
                    <HelperText type="info" visible style={styles.helper}>{t("resetPassword.codeHelper")}</HelperText>
                    <Button compact mode="text" onPress={() => void sendCode()} disabled={busy || resendIn > 0}>
                      {resendIn > 0 ? t("resetPassword.resendIn", { seconds: resendIn }) : t("resetPassword.resend")}
                    </Button>
                  </View>
                  <TextInput
                    mode="outlined"
                    label={t("changePassword.newPassword")}
                    value={newPassword}
                    onChangeText={(value) => { setNewPassword(value); setErrorMessage(""); }}
                    secureTextEntry={!passwordVisible}
                    autoCapitalize="none"
                    autoCorrect={false}
                    textContentType="newPassword"
                    maxLength={PASSWORD_MAX_LENGTH}
                    right={<TextInput.Icon icon={passwordVisible ? "eye-off-outline" : "eye-outline"} accessibilityLabel={t("changePassword.togglePassword")} onPress={() => setPasswordVisible((value) => !value)} />}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("changePassword.confirmPassword")}
                    value={confirmPassword}
                    onChangeText={(value) => { setConfirmPassword(value); setErrorMessage(""); }}
                    secureTextEntry={!passwordVisible}
                    autoCapitalize="none"
                    autoCorrect={false}
                    textContentType="newPassword"
                    maxLength={PASSWORD_MAX_LENGTH}
                  />
                  <View style={styles.rules}>
                    {([
                      { key: "minLength", label: t("changePassword.rules.minLength", { min: PASSWORD_MIN_LENGTH }) },
                      { key: "confirmed", label: t("changePassword.rules.confirmed") },
                    ] as const).map((item) => (
                      <View key={item.key} style={styles.ruleRow}>
                        <MaterialCommunityIcons
                          name={rules[item.key] ? "check-circle" : "circle-outline"}
                          size={20}
                          color={rules[item.key] ? HB_COLORS.success : HB_COLORS.outline}
                        />
                        <Text variant="bodyMedium">{item.label}</Text>
                      </View>
                    ))}
                  </View>
                </>
              ) : null}

              <HelperText type="error" visible={Boolean(errorMessage)}>{errorMessage}</HelperText>

              {step === "email" ? (
                <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => void sendCode()} loading={busy} disabled={busy} contentStyle={styles.buttonContent} style={styles.fullButton}>
                  {t("resetPassword.sendCode")}
                </Button>
              ) : (
                <Button mode="contained" buttonColor={HB_COLORS.action} onPress={() => void confirm()} loading={busy} disabled={!canConfirm} contentStyle={styles.buttonContent} style={styles.fullButton}>
                  {t("resetPassword.submit")}
                </Button>
              )}
            </>
          )}
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: HB_COLORS.background },
  flex: { flex: 1 },
  header: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  headerTitle: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  content: { padding: HB_SPACING.lg, gap: HB_SPACING.md },
  intro: { gap: HB_SPACING.sm },
  introIcon: {
    width: 56,
    height: 56,
    borderRadius: HB_RADIUS.sheet,
    backgroundColor: "#EAF2FF",
    alignItems: "center",
    justifyContent: "center",
  },
  title: { fontWeight: "700", color: HB_COLORS.textPrimary },
  secondary: { color: HB_COLORS.textSecondary },
  centerSecondary: { color: HB_COLORS.textSecondary, textAlign: "center" },
  codeInput: { letterSpacing: 6, fontSize: 20 },
  resendRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginTop: -HB_SPACING.xs },
  helper: { flex: 1, paddingHorizontal: 0 },
  rules: { gap: HB_SPACING.xs },
  ruleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  fullButton: { borderRadius: HB_RADIUS.control },
  buttonContent: { minHeight: 48 },
  doneBlock: { alignItems: "center", gap: HB_SPACING.sm, paddingTop: HB_SPACING.xl },
  loginNameCard: {
    alignSelf: "stretch",
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    marginTop: HB_SPACING.sm,
    padding: HB_SPACING.md,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  loginNameText: { flex: 1, gap: 2 },
  loginNameValue: { fontWeight: "700", fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }) },
  loginNameHint: { alignSelf: "stretch", color: HB_COLORS.textSecondary },
  doneIcon: {
    width: 64,
    height: 64,
    borderRadius: 32,
    backgroundColor: "#E7F6EC",
    alignItems: "center",
    justifyContent: "center",
  },
});
