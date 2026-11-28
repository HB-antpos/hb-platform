import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import { Image, Text, View } from "react-native";

import { orderingText, resolveOrderingLocale, type OrderingCopyKey } from "./ordering-copy";

import { PosButton } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

export type PosEntryScreenProps = Readonly<{
  /** 设备是否已注册成收银机：决定「收银」卡片去收银员登录还是去注册。 */
  registered: boolean;
  language: string;
  onCashier(): void;
  onOrdering(): void;
  onOpenNetworkCheck?: () => void;
  onSwitchLanguage?: () => void;
}>;

/**
 * App 首页：只有「收银」「订货」两个入口。
 *
 * 中文注释：
 * - 未注册设备：收银 → 设备注册流程；订货 → 员工账号登录（不用注册设备）。
 * - 已注册设备：收银 → 收银员登录；订货同上。
 * - 收银员已登录时不经过这里，直接进收银页（见 app/index.tsx）。
 */
export function PosEntryScreen({
  language,
  onCashier,
  onOpenNetworkCheck,
  onOrdering,
  onSwitchLanguage,
  registered,
}: PosEntryScreenProps) {
  usePosThemeVersion();
  const locale = resolveOrderingLocale(language);
  const t = (key: OrderingCopyKey) => orderingText(locale, key);
  const mode = registered ? "registered" : "unregistered";

  return (
    <PosScreenFrame
      language={locale}
      {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      // 首页只有两个入口，不显示左侧栏。
      rail="none"
      testID="pos-entry-screen"
    >
      <StatusBar style="light" />
      <View style={styles.page}>
        <View style={styles.hello}>
          <Image
            accessibilityIgnoresInvertColors
            accessible={false}
            resizeMode="contain"
            source={require("../../../assets/icon.png")}
            style={styles.mark}
          />
          <Text accessibilityRole="header" style={styles.title}>
            {t(`entry.title.${mode}`)}
          </Text>
          <Text style={styles.subtitle}>{t(`entry.subtitle.${mode}`)}</Text>
        </View>
        <View style={styles.entries}>
          <EntryCard
            action={t(`entry.pos.action.${mode}`)}
            description={t(`entry.pos.desc.${mode}`)}
            icon="cash-register"
            onPress={onCashier}
            testID="pos-entry-cashier"
            title={t("entry.pos.title")}
            tone="accent"
          />
          <EntryCard
            action={t("entry.order.action")}
            description={t("entry.order.desc")}
            icon="cart-outline"
            onPress={onOrdering}
            testID="pos-entry-ordering"
            title={t("entry.order.title")}
            tone="blue"
          />
        </View>
        {onOpenNetworkCheck ? (
          <PosButton
            icon="access-point-network"
            label={t("entry.networkCheck")}
            onPress={onOpenNetworkCheck}
            size="medium"
            sound="navigate"
            testID="pos-entry-network-check"
            tone="quiet"
          />
        ) : null}
      </View>
    </PosScreenFrame>
  );
}

function EntryCard({
  action,
  description,
  icon,
  onPress,
  testID,
  title,
  tone,
}: Readonly<{
  title: string;
  description: string;
  action: string;
  icon: "cash-register" | "cart-outline";
  tone: "accent" | "blue";
  onPress(): void;
  testID: string;
}>) {
  const color = tone === "accent" ? posColors.accent : posColors.blue;
  return (
    <PosPressable
      accessibilityHint={action}
      accessibilityLabel={title}
      accessibilityRole="button"
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [styles.card, pressed && styles.cardPressed]}
      testID={testID}
    >
      <View style={[styles.cardIcon, tone === "accent" ? styles.cardIconAccent : styles.cardIconBlue]}>
        <MaterialCommunityIcons color={color} name={icon} size={40} />
      </View>
      <Text style={styles.cardTitle}>{title}</Text>
      <Text style={styles.cardDescription}>{description}</Text>
      <View style={styles.cardFooter}>
        <Text style={[styles.cardAction, { color }]}>{action}</Text>
        <MaterialCommunityIcons color={color} name="arrow-right" size={24} />
      </View>
    </PosPressable>
  );
}

const styles = createPosStyles((colors) => ({
  page: {
    alignItems: "center",
    flex: 1,
    gap: 32,
    justifyContent: "center",
    padding: 24,
  },
  hello: { alignItems: "center", gap: 8, maxWidth: 720 },
  mark: { borderRadius: 16, height: 64, marginBottom: 8, width: 64 },
  title: { color: colors.ink, fontSize: 28, fontWeight: "900", textAlign: "center" },
  subtitle: { color: colors.mutedInk, fontSize: 15, fontWeight: "600", lineHeight: 22, textAlign: "center" },
  entries: { flexDirection: "row", gap: 24, maxWidth: 840, width: "100%" },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 20,
    borderWidth: 1.5,
    flex: 1,
    minHeight: 300,
    padding: 30,
  },
  cardPressed: { backgroundColor: colors.surfaceAlt, borderColor: colors.borderStrong },
  cardIcon: { alignItems: "center", borderRadius: 18, height: 72, justifyContent: "center", width: 72 },
  cardIconAccent: { backgroundColor: colors.accentSoft },
  cardIconBlue: { backgroundColor: colors.blueSoft },
  cardTitle: { color: colors.ink, fontSize: 30, fontWeight: "900", marginTop: 22 },
  cardDescription: { color: colors.mutedInk, fontSize: 15, fontWeight: "600", lineHeight: 22, marginTop: 8 },
  cardFooter: {
    alignItems: "center",
    borderTopColor: colors.border,
    borderTopWidth: 1,
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: "auto",
    paddingTop: 16,
  },
  cardAction: { fontSize: 16, fontWeight: "900" },
}));
