import { useState } from "react";
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, IconButton, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { VAULT_MAX_ENTRIES } from "./vault";

interface AddAccountViewProps {
  /** 当前账号显示名（「当前仍登录：…」）。 */
  currentName: string;
  /** 当前账号是否已在切换列表里：没保存时，登录新账号 = 当前账号退出，要给黄色提示。 */
  currentSaved: boolean;
  /** 切换列表里已有几个账号：满了就不能勾选保存。 */
  savedCount: number;
  onBack: () => void;
  /** 成功返回 null；失败返回要显示在表单里的文案。 */
  onSubmit: (input: { username: string; password: string; save: boolean }) => Promise<string | null>;
}

/** 添加账号页：用户名 + 密码，「保存到切换列表」默认不勾选。 */
export function AddAccountView({ currentName, currentSaved, savedCount, onBack, onSubmit }: AddAccountViewProps) {
  const { t } = useAppTranslation("accountSwitch");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [save, setSave] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const listFull = savedCount >= VAULT_MAX_ENTRIES;
  const blockedByFull = save && listFull;
  const canSubmit = username.trim().length > 0 && password.length > 0 && !busy && !blockedByFull;

  const submit = async () => {
    if (!canSubmit) return;
    setBusy(true);
    setError("");
    try {
      const message = await onSubmit({ username: username.trim(), password, save });
      if (message) setError(message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container} testID="settings-add-account">
      <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined} style={styles.flex}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <View style={styles.header}>
            <IconButton icon="close" accessibilityLabel={t("add.back")} onPress={onBack} disabled={busy} />
            <View style={styles.headerText}>
              <Text variant="titleLarge" style={styles.title}>
                {t("add.title")}
              </Text>
              <Text variant="bodySmall" style={styles.subtitle}>
                {t("add.currentAs", { name: currentName })}
              </Text>
            </View>
          </View>

          <View style={[styles.notice, styles.noticeInfo]}>
            <MaterialCommunityIcons name="account-plus-outline" size={18} color={HB_COLORS.action} />
            <Text variant="bodySmall" style={styles.noticeText}>
              {t("add.intro")}
            </Text>
          </View>

          <TextInput
            mode="outlined"
            label={t("add.username")}
            value={username}
            onChangeText={setUsername}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="username"
            returnKeyType="next"
            disabled={busy}
            testID="add-account-username"
          />
          <TextInput
            mode="outlined"
            label={t("add.password")}
            value={password}
            onChangeText={setPassword}
            secureTextEntry={!showPassword}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="password"
            returnKeyType="go"
            onSubmitEditing={submit}
            disabled={busy}
            testID="add-account-password"
            right={
              <TextInput.Icon
                icon={showPassword ? "eye-off-outline" : "eye-outline"}
                accessibilityLabel={t(showPassword ? "add.hidePassword" : "add.showPassword")}
                onPress={() => setShowPassword((visible) => !visible)}
              />
            }
          />
          <View style={styles.helperRow}>
            <MaterialCommunityIcons name="information-outline" size={15} color={HB_COLORS.textSecondary} />
            <Text variant="bodySmall" style={styles.helperText}>
              {t("add.forgot")}
            </Text>
          </View>

          {/* 默认不勾选：勾上才进切换列表。 */}
          <Pressable
            onPress={() => setSave((value) => !value)}
            disabled={busy}
            accessibilityRole="checkbox"
            accessibilityState={{ checked: save, disabled: busy }}
            accessibilityLabel={t("add.saveTitle")}
            style={styles.checkRow}
            testID="add-account-save"
          >
            <MaterialCommunityIcons
              name={save ? "checkbox-marked" : "checkbox-blank-outline"}
              size={24}
              color={save ? HB_COLORS.action : HB_COLORS.textSecondary}
            />
            <View style={styles.checkBody}>
              <Text variant="bodyLarge" style={styles.checkTitle}>
                {t("add.saveTitle")}
              </Text>
              <Text variant="bodySmall" style={styles.helperText}>
                {t("add.saveHelper")}
              </Text>
            </View>
          </Pressable>

          {currentSaved ? null : (
            <View style={[styles.notice, styles.noticeWarn]} testID="add-account-unsaved-warning">
              <MaterialCommunityIcons name="alert-outline" size={18} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={[styles.noticeText, styles.noticeWarnText]}>
                {t("add.unsavedWarning", { name: currentName })}
              </Text>
            </View>
          )}
          {blockedByFull ? (
            <View style={[styles.notice, styles.noticeWarn]}>
              <MaterialCommunityIcons name="alert-outline" size={18} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={[styles.noticeText, styles.noticeWarnText]}>
                {t("add.listFull", { max: VAULT_MAX_ENTRIES })}
              </Text>
            </View>
          ) : null}
          {error ? (
            <View style={[styles.notice, styles.noticeError]} accessibilityLiveRegion="polite">
              <MaterialCommunityIcons name="alert-circle-outline" size={18} color={HB_COLORS.danger} />
              <Text variant="bodySmall" style={[styles.noticeText, styles.noticeErrorText]}>
                {error}
              </Text>
            </View>
          ) : null}

          <Button
            mode="contained"
            icon="login"
            onPress={submit}
            loading={busy}
            disabled={!canSubmit}
            style={styles.submit}
            contentStyle={styles.submitContent}
            testID="add-account-submit"
          >
            {t("add.submit")}
          </Button>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: HB_COLORS.background },
  flex: { flex: 1 },
  content: { padding: 16, gap: 12, paddingBottom: 32 },
  header: { flexDirection: "row", alignItems: "center", gap: 4, marginLeft: -8 },
  headerText: { flex: 1, gap: 2 },
  title: { fontWeight: "800", color: HB_COLORS.textPrimary },
  subtitle: { color: HB_COLORS.textSecondary },
  notice: { flexDirection: "row", alignItems: "flex-start", gap: 8, padding: 12, borderRadius: 10 },
  noticeInfo: { backgroundColor: "#EFF6FF" },
  noticeWarn: { backgroundColor: "#FFFAEB" },
  noticeError: { backgroundColor: "#FEF3F2" },
  noticeText: { flex: 1, color: HB_COLORS.textPrimary },
  noticeWarnText: { color: HB_COLORS.warning },
  noticeErrorText: { color: HB_COLORS.danger },
  helperRow: { flexDirection: "row", alignItems: "flex-start", gap: 6, paddingHorizontal: 2 },
  helperText: { flex: 1, color: HB_COLORS.textSecondary },
  checkRow: { flexDirection: "row", alignItems: "flex-start", gap: 10, paddingVertical: 6, paddingHorizontal: 2 },
  checkBody: { flex: 1, gap: 2 },
  checkTitle: { fontWeight: "700", color: HB_COLORS.textPrimary },
  submit: { marginTop: 4, borderRadius: 10 },
  submitContent: { minHeight: 48 },
});
