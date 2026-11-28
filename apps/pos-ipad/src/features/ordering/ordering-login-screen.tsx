import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import { useRef, useState } from "react";
import { Keyboard, Text, View, type TextInput } from "react-native";

import { OrderingAuthError, type OrderingUser } from "./ordering-auth";
import { orderingText, resolveOrderingLocale, type OrderingCopyKey } from "./ordering-copy";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

export type OrderingLoginScreenProps = Readonly<{
  language: string;
  signIn(credentials: Readonly<{ username: string; password: string }>): Promise<OrderingUser>;
  onSignedIn(user: OrderingUser): void;
  onBack(): void;
  onSwitchLanguage?: () => void;
}>;

const ERROR_KEYS: Readonly<Record<string, OrderingCopyKey>> = {
  NOT_ALLOWED: "login.error.notAllowed",
  NETWORK: "login.error.network",
  SERVER: "login.error.server",
};

/**
 * 订货登录：员工用 hotbargain.vip 账号登录，登录后只进订货工作台。
 *
 * 中文注释：账号、密码输入框不受控（只在 ref 里记值），扫码枪 / 外接键盘整段输入不会被受控回写截断；
 * 密码只在提交那一下交给 signIn，不存状态、不落盘。
 */
export function OrderingLoginScreen({
  language,
  onBack,
  onSignedIn,
  onSwitchLanguage,
  signIn,
}: OrderingLoginScreenProps) {
  usePosThemeVersion();
  const locale = resolveOrderingLocale(language);
  const t = (key: OrderingCopyKey) => orderingText(locale, key);
  const usernameRef = useRef("");
  const passwordRef = useRef("");
  const passwordInputRef = useRef<TextInput>(null);
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (submitting) return;
    const username = usernameRef.current.trim();
    const password = passwordRef.current;
    if (!username || !password) {
      setError(t("login.error.required"));
      return;
    }
    Keyboard.dismiss();
    setSubmitting(true);
    setError(null);
    try {
      const user = await signIn({ username, password });
      onSignedIn(user);
    } catch (caught) {
      if (caught instanceof OrderingAuthError && caught.code === "INVALID_CREDENTIALS") {
        // 账号停用、登录受限等，后端给的原话比笼统的「账号或密码不对」更有用。
        setError(caught.serverMessage ?? t("login.error.invalid"));
      } else {
        const code = caught instanceof OrderingAuthError ? caught.code : "SERVER";
        setError(t(ERROR_KEYS[code] ?? "login.error.server"));
      }
      setSubmitting(false);
    }
  };

  return (
    <PosScreenFrame
      language={locale}
      {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      rail="none"
      testID="ordering-login-screen"
    >
      <StatusBar style="light" />
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.scroll}
        keyboardRevealOffset={96}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.card}>
          <PosButton
            disabled={submitting}
            icon="chevron-left"
            label={t("login.back")}
            onPress={onBack}
            size="small"
            sound="navigate"
            style={styles.back}
            testID="ordering-login-back"
            tone="quiet"
          />
          <View style={styles.head}>
            <View style={styles.headIcon}>
              <MaterialCommunityIcons color={posColors.blue} name="cart-outline" size={28} />
            </View>
            <View style={styles.headCopy}>
              <Text accessibilityRole="header" style={styles.title}>
                {t("login.title")}
              </Text>
              <Text style={styles.subtitle}>{t("login.subtitle")}</Text>
            </View>
          </View>

          <View>
            <Text style={styles.label}>{t("login.username")}</Text>
            <PosKeyboardAwareTextInput
              accessibilityLabel={t("login.username")}
              autoCapitalize="none"
              autoComplete="username"
              autoCorrect={false}
              editable={!submitting}
              onChangeText={(value) => {
                usernameRef.current = value;
              }}
              onSubmitEditing={() => passwordInputRef.current?.focus()}
              returnKeyType="next"
              style={styles.input}
              testID="ordering-login-username"
              textContentType="username"
            />
          </View>
          <View>
            <Text style={styles.label}>{t("login.password")}</Text>
            <View style={styles.passwordRow}>
              <PosKeyboardAwareTextInput
                ref={passwordInputRef}
                accessibilityLabel={t("login.password")}
                autoCapitalize="none"
                autoComplete="password"
                autoCorrect={false}
                editable={!submitting}
                onChangeText={(value) => {
                  passwordRef.current = value;
                }}
                onSubmitEditing={() => void submit()}
                returnKeyType="go"
                secureTextEntry={!showPassword}
                style={[styles.input, styles.passwordInput]}
                testID="ordering-login-password"
                textContentType="password"
              />
              <PosPressable
                accessibilityLabel={showPassword ? t("login.hidePassword") : t("login.showPassword")}
                accessibilityRole="button"
                onPress={() => setShowPassword((value) => !value)}
                style={styles.eye}
                testID="ordering-login-toggle-password"
              >
                <MaterialCommunityIcons
                  color={posColors.mutedInk}
                  name={showPassword ? "eye-off-outline" : "eye-outline"}
                  size={24}
                />
              </PosPressable>
            </View>
          </View>

          {error ? (
            <View accessibilityRole="alert" style={styles.error} testID="ordering-login-error">
              <MaterialCommunityIcons color={posColors.red} name="alert-circle-outline" size={20} />
              <Text style={styles.errorText}>{error}</Text>
            </View>
          ) : null}

          <PosButton
            icon="login"
            label={submitting ? t("login.submitting") : t("login.submit")}
            loading={submitting}
            onPress={() => void submit()}
            size="button"
            testID="ordering-login-submit"
            tone="primary"
          />
          <View style={styles.note}>
            <MaterialCommunityIcons color={posColors.green} name="lock-outline" size={18} />
            <Text style={styles.noteText}>{t("login.note")}</Text>
          </View>
        </View>
      </PosKeyboardAwareScrollView>
    </PosScreenFrame>
  );
}

const styles = createPosStyles((colors) => ({
  scroll: { alignItems: "center", flexGrow: 1, justifyContent: "center", padding: 24 },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 20,
    borderWidth: 1.5,
    gap: 14,
    maxWidth: 520,
    paddingHorizontal: 34,
    paddingVertical: 28,
    width: "100%",
  },
  back: { alignSelf: "flex-start" },
  head: { alignItems: "center", flexDirection: "row", gap: 14 },
  headIcon: {
    alignItems: "center",
    backgroundColor: colors.blueSoft,
    borderRadius: 14,
    height: 52,
    justifyContent: "center",
    width: 52,
  },
  headCopy: { flex: 1, gap: 2 },
  title: { color: colors.ink, fontSize: 26, fontWeight: "900" },
  subtitle: { color: colors.mutedInk, fontSize: 13, fontWeight: "600" },
  label: { color: colors.mutedInk, fontSize: 14, fontWeight: "800", marginBottom: 5 },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 2.5,
    color: colors.ink,
    fontSize: 18,
    fontWeight: "600",
    height: 60,
    paddingHorizontal: 18,
  },
  passwordRow: { justifyContent: "center" },
  passwordInput: { paddingRight: 60 },
  eye: {
    alignItems: "center",
    bottom: 0,
    justifyContent: "center",
    position: "absolute",
    right: 6,
    top: 0,
    width: 52,
  },
  error: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  errorText: { color: colors.red, flex: 1, fontSize: 14, fontWeight: "700" },
  note: {
    backgroundColor: colors.surfaceAlt,
    borderRadius: 10,
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  noteText: { color: colors.mutedInk, flex: 1, fontSize: 13, fontWeight: "600", lineHeight: 19 },
}));
