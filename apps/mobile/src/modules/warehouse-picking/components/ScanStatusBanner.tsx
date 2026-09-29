import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { ProductThumb } from "./ProductThumb";

export type ScanBannerState =
  | { kind: "ready" }
  | { kind: "success"; title: string; message?: string | null }
  | { kind: "info"; title: string; message?: string | null; actionLabel?: string; onAction?: () => void }
  | { kind: "warning"; title: string; message?: string | null }
  | {
      kind: "error";
      title: string;
      message?: string | null;
      code?: string | null;
      product?: { name: string | null; image: string | null; location: string | null } | null;
    };

/**
 * 扫码后的即时反馈条：就绪（蓝）、计入成功（绿）、需要处理（橙）、不在本单（红）。
 * 不弹窗打断连续扫码，下一次扫码时被新结果替换。
 */
export function ScanStatusBanner({
  state,
  readyTitle,
  readyHint,
  cameraLabel,
  onCamera,
  locationLabel,
}: {
  state: ScanBannerState;
  readyTitle: string;
  readyHint: string;
  cameraLabel: string;
  onCamera: () => void;
  locationLabel: (location: string) => string;
}) {
  if (state.kind === "ready") {
    return (
      <View accessibilityRole="text" accessibilityLiveRegion="polite" style={[styles.bar, styles.ready]}>
        <View style={styles.dot} />
        <Text style={styles.readyText}>
          <Text style={styles.bold}>{readyTitle}</Text> · {readyHint}
        </Text>
        <Pressable accessibilityRole="button" accessibilityLabel={cameraLabel} onPress={onCamera} style={styles.iconButton}>
          <MaterialCommunityIcons name="camera-outline" size={22} color={PICK_COLORS.action} />
        </Pressable>
      </View>
    );
  }

  if (state.kind === "error") {
    return (
      <View accessibilityRole="alert" accessibilityLiveRegion="assertive" style={[styles.card, styles.error]}>
        <View style={styles.headline}>
          <View style={[styles.badge, { backgroundColor: PICK_COLORS.danger }]}>
            <MaterialCommunityIcons name="close" size={14} color={PICK_COLORS.white} />
          </View>
          <Text style={[styles.title, { color: PICK_COLORS.dangerText }]}>{state.title}</Text>
        </View>
        {state.code || state.product ? (
          <View style={styles.productRow}>
            <ProductThumb uri={state.product?.image} size={40} />
            <View style={styles.productText}>
              {state.product?.name ? <Text style={styles.productName}>{state.product.name}</Text> : null}
              <Text style={styles.productMeta}>
                {state.code ? <Text style={styles.mono}>{state.code}</Text> : null}
                {state.product?.location ? ` · ${locationLabel(state.product.location)}` : ""}
              </Text>
            </View>
          </View>
        ) : null}
        {state.message ? <Text style={[styles.message, { color: PICK_COLORS.dangerText }]}>{state.message}</Text> : null}
      </View>
    );
  }

  const palette = {
    success: { bg: PICK_COLORS.successBg, border: PICK_COLORS.successBorder, text: PICK_COLORS.successText, badge: PICK_COLORS.success, icon: "check" as const },
    warning: { bg: PICK_COLORS.warningBg, border: PICK_COLORS.warningBorder, text: PICK_COLORS.warningText, badge: PICK_COLORS.warning, icon: "alert-outline" as const },
    info: { bg: PICK_COLORS.infoBg, border: PICK_COLORS.infoBorder, text: PICK_COLORS.infoText, badge: PICK_COLORS.action, icon: "information-variant" as const },
  }[state.kind];

  return (
    <View
      accessibilityRole="text"
      accessibilityLiveRegion="polite"
      style={[styles.card, { backgroundColor: palette.bg, borderColor: palette.border }]}
    >
      <View style={styles.headline}>
        <View style={[styles.badge, { backgroundColor: palette.badge }]}>
          <MaterialCommunityIcons name={palette.icon} size={14} color={PICK_COLORS.white} />
        </View>
        <View style={styles.flex}>
          <Text style={[styles.title, { color: palette.text }]}>{state.title}</Text>
          {state.message ? <Text style={[styles.message, { color: palette.text }]}>{state.message}</Text> : null}
        </View>
      </View>
      {state.kind === "info" && state.actionLabel && state.onAction ? (
        <Pressable accessibilityRole="button" onPress={state.onAction} style={styles.action}>
          <Text style={styles.actionText}>{state.actionLabel}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    minHeight: 48,
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingLeft: 14,
    paddingRight: 2,
  },
  ready: { backgroundColor: PICK_COLORS.infoBg, borderColor: PICK_COLORS.infoBorder },
  dot: {
    width: 10,
    height: 10,
    borderRadius: 5,
    backgroundColor: PICK_COLORS.action,
    borderWidth: 3,
    borderColor: "#C9DBFA",
  },
  readyText: { flex: 1, fontSize: 13, lineHeight: 18, color: PICK_COLORS.infoText },
  bold: { fontWeight: "700", color: PICK_COLORS.infoText },
  iconButton: { width: 44, height: 44, borderRadius: 8, alignItems: "center", justifyContent: "center" },
  card: { borderRadius: 10, borderWidth: 1, paddingHorizontal: 12, paddingVertical: 8, gap: 8 },
  error: { backgroundColor: PICK_COLORS.dangerBg, borderColor: PICK_COLORS.dangerBorder, paddingVertical: 10 },
  headline: { flexDirection: "row", alignItems: "flex-start", gap: 10 },
  badge: { width: 22, height: 22, borderRadius: 11, alignItems: "center", justifyContent: "center", marginTop: 1 },
  flex: { flex: 1, minWidth: 0, gap: 2 },
  title: { flexShrink: 1, fontSize: 14, lineHeight: 20, fontWeight: "700" },
  message: { fontSize: 12, lineHeight: 17 },
  productRow: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PICK_COLORS.white, borderRadius: 8, padding: 8 },
  productText: { flex: 1, minWidth: 0 },
  productName: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: PICK_COLORS.ink },
  productMeta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT },
  action: { minHeight: 40, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.infoBorder, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  actionText: { fontSize: 14, fontWeight: "700", color: PICK_COLORS.action },
});
