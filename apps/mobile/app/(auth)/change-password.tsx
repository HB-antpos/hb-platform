import { useState } from "react";
import { Alert, KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { Redirect, useLocalSearchParams, useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, IconButton, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";

import { changePasswordApi } from "@/modules/auth/api";
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  canSubmitPasswordChange,
  getPasswordRuleState,
  normalizePasswordChangeForm,
  resolvePasswordChangeErrorKey,
  shouldForcePasswordChange,
  type PasswordChangeForm,
  type PasswordChangeMode,
} from "@/modules/auth/password-change";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

const EMPTY_FORM: PasswordChangeForm = { currentPassword: "", newPassword: "", confirmPassword: "" };

/**
 * 修改密码页。
 * forced：店长新建账号或重置密码后，壳层把员工重定向到这里，改完才能进入 App，只能改密或退出登录。
 * voluntary：员工从设置页主动进入，可以返回。
 */
export default function ChangePasswordScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ mode?: string }>();
  const mode: PasswordChangeMode = params.mode === "voluntary" ? "voluntary" : "forced";
  const { t, language } = useAppTranslation(["login", "common"]);
  const user = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const refreshCurrentUser = useAuthStore((state) => state.refreshCurrentUser);
  const logout = useAuthStore((state) => state.logout);
  const [form, setForm] = useState<PasswordChangeForm>(EMPTY_FORM);
  const [visible, setVisible] = useState({ current: false, next: false });
  const [submitting, setSubmitting] = useState(false);
  const [errorMessage, setErrorMessage] = useState("");

  if (!isAuthenticated || !user) {
    return <Redirect href="/(auth)/login" />;
  }
  // 强制模式只服务须改密的个人账号；标记已清除（如另一台设备已改密）时直接回到 App。
  if (mode === "forced" && !shouldForcePasswordChange({ user, sessionKind, isAuthenticated })) {
    return <Redirect href="/" />;
  }

  const rules = getPasswordRuleState(form);
  const canSubmit = canSubmitPasswordChange(form) && !submitting;
  const setField = (key: keyof PasswordChangeForm, value: string) => {
    setErrorMessage("");
    setForm((current) => ({ ...current, [key]: value }));
  };

  const handleSubmit = async () => {
    if (!canSubmitPasswordChange(form)) return;
    setSubmitting(true);
    setErrorMessage("");
    try {
      await changePasswordApi(normalizePasswordChangeForm(form));
      // 后端已清除须改密标记；刷新当前用户后壳层不再拦截。刷新失败不影响改密结果。
      await refreshCurrentUser().catch(() => false);
      setForm(EMPTY_FORM);
      if (mode === "forced") {
        router.replace("/");
        return;
      }
      Alert.alert(t("changePassword.successTitle"), t("changePassword.successVoluntary"), [
        { text: t("common:actions.confirm"), onPress: () => router.back() },
      ]);
    } catch (error) {
      const knownKey = resolvePasswordChangeErrorKey(error);
      setErrorMessage(knownKey
        ? t(knownKey)
        : resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "changePassword.failed" }));
    } finally {
      setSubmitting(false);
    }
  };

  const handleLogout = async () => {
    await logout().catch(() => undefined);
    router.replace("/(auth)/login");
  };

  const ruleItems: { key: keyof typeof rules; label: string }[] = [
    { key: "minLength", label: t("changePassword.rules.minLength", { min: PASSWORD_MIN_LENGTH }) },
    { key: "differentFromCurrent", label: t("changePassword.rules.differentFromCurrent") },
    { key: "confirmed", label: t("changePassword.rules.confirmed") },
  ];

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right", "bottom"]}>
      {mode === "voluntary" ? (
        <View style={styles.header}>
          <IconButton icon="arrow-left" size={22} accessibilityLabel={t("common:actions.back")} onPress={() => router.back()} />
          <Text variant="titleLarge" style={styles.headerTitle}>{t("changePassword.voluntaryTitle")}</Text>
          <View style={styles.headerSpacer} />
        </View>
      ) : null}
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          {mode === "forced" ? (
            <View style={styles.intro}>
              <View style={styles.introIcon}>
                <MaterialCommunityIcons name="lock-outline" size={28} color={HB_COLORS.action} />
              </View>
              <Text variant="headlineSmall" style={styles.introTitle}>{t("changePassword.forcedTitle")}</Text>
              <Text variant="bodyMedium" style={styles.secondary}>{t("changePassword.forcedDescription")}</Text>
              <View style={styles.accountPill}>
                <MaterialCommunityIcons name="account-outline" size={16} color={HB_COLORS.textSecondary} />
                <Text variant="labelMedium" style={styles.secondary}>{user.username}</Text>
              </View>
            </View>
          ) : null}

          <TextInput
            mode="outlined"
            label={t(mode === "forced" ? "changePassword.initialPassword" : "changePassword.currentPassword")}
            value={form.currentPassword}
            onChangeText={(value) => setField("currentPassword", value)}
            secureTextEntry={!visible.current}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="password"
            maxLength={PASSWORD_MAX_LENGTH}
            right={<TextInput.Icon icon={visible.current ? "eye-off-outline" : "eye-outline"} accessibilityLabel={t("changePassword.togglePassword")} onPress={() => setVisible((v) => ({ ...v, current: !v.current }))} />}
          />
          <TextInput
            mode="outlined"
            label={t("changePassword.newPassword")}
            value={form.newPassword}
            onChangeText={(value) => setField("newPassword", value)}
            secureTextEntry={!visible.next}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="newPassword"
            maxLength={PASSWORD_MAX_LENGTH}
            right={<TextInput.Icon icon={visible.next ? "eye-off-outline" : "eye-outline"} accessibilityLabel={t("changePassword.togglePassword")} onPress={() => setVisible((v) => ({ ...v, next: !v.next }))} />}
          />
          <TextInput
            mode="outlined"
            label={t("changePassword.confirmPassword")}
            value={form.confirmPassword}
            onChangeText={(value) => setField("confirmPassword", value)}
            secureTextEntry={!visible.next}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="newPassword"
            maxLength={PASSWORD_MAX_LENGTH}
          />

          {/* 规则逐条实时打勾，用户不用提交就知道还差什么。 */}
          <View style={styles.rules} accessibilityRole="summary">
            {ruleItems.map((item) => (
              <View key={item.key} style={styles.ruleRow}>
                <MaterialCommunityIcons
                  name={rules[item.key] ? "check-circle" : "circle-outline"}
                  size={20}
                  color={rules[item.key] ? HB_COLORS.success : HB_COLORS.outline}
                />
                <Text variant="bodyMedium" style={styles.ruleText}>{item.label}</Text>
              </View>
            ))}
          </View>

          <HelperText type="error" visible={Boolean(errorMessage)}>{errorMessage}</HelperText>

          <Button
            mode="contained"
            buttonColor={HB_COLORS.action}
            onPress={() => void handleSubmit()}
            loading={submitting}
            disabled={!canSubmit}
            contentStyle={styles.primaryContent}
            style={styles.primaryButton}
          >
            {t(mode === "forced" ? "changePassword.submitForced" : "changePassword.submitVoluntary")}
          </Button>
          {mode === "forced" ? (
            <Button mode="text" textColor={HB_COLORS.textSecondary} onPress={() => void handleLogout()} disabled={submitting}>
              {t("changePassword.logout")}
            </Button>
          ) : null}
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
  intro: { gap: HB_SPACING.xs, paddingTop: HB_SPACING.lg, paddingBottom: HB_SPACING.xs },
  introIcon: {
    width: 56,
    height: 56,
    borderRadius: HB_RADIUS.sheet,
    backgroundColor: "#EAF2FF",
    alignItems: "center",
    justifyContent: "center",
    marginBottom: HB_SPACING.xs,
  },
  introTitle: { fontWeight: "700", color: HB_COLORS.textPrimary },
  secondary: { color: HB_COLORS.textSecondary },
  accountPill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    alignSelf: "flex-start",
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 12,
    backgroundColor: HB_COLORS.surfaceMuted,
    marginTop: HB_SPACING.xxs,
  },
  rules: { gap: HB_SPACING.xs },
  ruleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  ruleText: { color: HB_COLORS.textPrimary },
  primaryButton: { borderRadius: HB_RADIUS.control },
  primaryContent: { minHeight: 48 },
});
