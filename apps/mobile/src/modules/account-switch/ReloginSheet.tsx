import { useEffect, useState } from "react";
import { BackHandler, KeyboardAvoidingView, Platform, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Text, TextInput } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { describeRow } from "./presentation";
import type { VaultEntry } from "./vault";

interface ReloginSheetProps {
  entry: VaultEntry;
  apiHost: string;
  onClose: () => void;
  /** 成功返回 null；失败返回要显示在面板里的文案。 */
  onSubmit: (input: { username: string; password: string; save: boolean }) => Promise<string | null>;
}

/** 重新登录面板：保存的登录过期 / 失效后，只需再输一次密码（用户名固定），成功后继续保存在列表里。 */
export function ReloginSheet({ entry, apiHost, onClose, onSubmit }: ReloginSheetProps) {
  const { t } = useAppTranslation("accountSwitch");
  const insets = useSafeAreaInsets();
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const name = entry.fullName || entry.username;
  const reason = describeRow(entry, Date.now(), apiHost).reloginReason;

  useEffect(() => {
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      if (!busy) onClose();
      return true;
    });
    return () => subscription.remove();
  }, [busy, onClose]);

  const submit = async () => {
    if (!password || busy) return;
    setBusy(true);
    setError("");
    try {
      const message = await onSubmit({ username: entry.username, password, save: true });
      if (message) setError(message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.root} testID="account-relogin">
      <Pressable style={styles.scrim} onPress={busy ? undefined : onClose} accessibilityRole="button" />
      <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <View style={[styles.sheet, { paddingBottom: Math.max(insets.bottom, 12) + 8 }]}>
          <View style={styles.grabber} />
          <View style={styles.header}>
            <View style={styles.headerText}>
              <Text variant="titleMedium" style={styles.title}>
                {t("relogin.title", { name })}
              </Text>
              <Text variant="bodySmall" style={styles.subtitle}>
                {t(reason === "expired" ? "relogin.subtitleExpired" : "relogin.subtitleRejected")}
              </Text>
            </View>
            <Pressable onPress={onClose} disabled={busy} hitSlop={10} accessibilityRole="button">
              <MaterialCommunityIcons name="close" size={24} color={HB_COLORS.textSecondary} />
            </Pressable>
          </View>
          <TextInput mode="outlined" label={t("relogin.username")} value={entry.username} disabled />
          <TextInput
            mode="outlined"
            label={t("relogin.password")}
            value={password}
            onChangeText={setPassword}
            secureTextEntry={!showPassword}
            autoCapitalize="none"
            autoCorrect={false}
            textContentType="password"
            returnKeyType="go"
            onSubmitEditing={submit}
            disabled={busy}
            autoFocus
            testID="relogin-password"
            right={
              <TextInput.Icon
                icon={showPassword ? "eye-off-outline" : "eye-outline"}
                onPress={() => setShowPassword((visible) => !visible)}
              />
            }
          />
          {error ? (
            <View style={styles.errorBox} accessibilityLiveRegion="polite">
              <MaterialCommunityIcons name="alert-circle-outline" size={18} color={HB_COLORS.danger} />
              <Text variant="bodySmall" style={styles.errorText}>
                {error}
              </Text>
            </View>
          ) : null}
          <Button
            mode="contained"
            onPress={submit}
            loading={busy}
            disabled={!password || busy}
            contentStyle={styles.submitContent}
            style={styles.submit}
            testID="relogin-submit"
          >
            {t("relogin.submit")}
          </Button>
          <View style={styles.noticeBox}>
            <MaterialCommunityIcons name="information-outline" size={16} color={HB_COLORS.action} />
            <Text variant="bodySmall" style={styles.noticeText}>
              {t("relogin.notice")}
            </Text>
          </View>
        </View>
      </KeyboardAvoidingView>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { ...StyleSheet.absoluteFillObject, zIndex: 950, elevation: 950, justifyContent: "flex-end" },
  scrim: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(16,24,40,0.45)" },
  sheet: {
    borderTopLeftRadius: HB_RADIUS.sheet + 4,
    borderTopRightRadius: HB_RADIUS.sheet + 4,
    backgroundColor: HB_COLORS.surface,
    paddingHorizontal: 16,
    paddingTop: 8,
    gap: 10,
  },
  grabber: { alignSelf: "center", width: 38, height: 4, borderRadius: 2, backgroundColor: HB_COLORS.outline },
  header: { flexDirection: "row", alignItems: "flex-start", gap: 8, paddingTop: 6 },
  headerText: { flex: 1, gap: 2 },
  title: { fontWeight: "800", color: HB_COLORS.textPrimary },
  subtitle: { color: HB_COLORS.textSecondary },
  submit: { borderRadius: 10 },
  submitContent: { minHeight: 48 },
  errorBox: { flexDirection: "row", alignItems: "flex-start", gap: 8, padding: 10, borderRadius: 10, backgroundColor: "#FEF3F2" },
  errorText: { flex: 1, color: HB_COLORS.danger },
  noticeBox: { flexDirection: "row", alignItems: "flex-start", gap: 8, padding: 10, borderRadius: 10, backgroundColor: "#EFF6FF" },
  noticeText: { flex: 1, color: HB_COLORS.textSecondary },
});
