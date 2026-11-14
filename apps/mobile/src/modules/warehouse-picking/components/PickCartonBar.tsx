import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { OrderCartonController } from "../use-order-carton";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";

/**
 * 拣货页顶部的「当前箱」条（边拣边装）：
 * - 有当前箱：显示箱号与已装几品几件，「封箱换箱」封箱后换下一只；
 * - 没有当前箱：一行「开始装箱」入口——不点就完全按原来的流程只拣货，不装箱；
 * - 后端没有装箱接口（available = false）或第一次拉取还没结束时不显示，页面与旧版一致。
 * writing：还有拣货写入在途，此时不能封箱（避免排队中的写入落到已封的箱上）。
 */
export function PickCartonBar({ pack, writing }: { pack: OrderCartonController; writing: boolean }) {
  const { t } = useAppTranslation("warehousePicking");
  const { carton } = pack;
  if (!pack.available || (!pack.loaded && !carton)) return null;

  if (carton) {
    const disabled = writing || pack.sealing;
    return (
      <View style={styles.bar} accessibilityLabel={t("carton.barLabel", { cartonNo: carton.cartonNo })}>
        <View style={styles.icon}>
          <MaterialCommunityIcons name="package-variant-closed" size={22} color={PICK_COLORS.white} />
        </View>
        <View style={styles.text}>
          <Text numberOfLines={1} style={styles.caption}>
            {t("carton.current", { sku: carton.skuCount, pieces: carton.pieceCount })}
          </Text>
          <Text numberOfLines={1} style={styles.number}>
            {carton.cartonNo}
          </Text>
        </View>
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ disabled }}
          disabled={disabled}
          onPress={() => pack.setSealVisible(true)}
          style={[styles.sealButton, disabled ? styles.disabled : null]}
        >
          <MaterialCommunityIcons name="lock-outline" size={16} color={PICK_COLORS.action} />
          <Text style={styles.sealText}>{t("carton.sealSwap")}</Text>
        </Pressable>
      </View>
    );
  }

  return (
    <View style={styles.startRow}>
      <MaterialCommunityIcons name="package-variant-closed" size={20} color={PICK_COLORS.textSecondary} />
      <Text numberOfLines={2} style={styles.startHint}>
        {t("carton.startHint")}
      </Text>
      <Pressable
        accessibilityRole="button"
        disabled={pack.creating}
        onPress={() => void pack.startCarton()}
        style={[styles.startButton, pack.creating ? styles.disabled : null]}
      >
        {pack.creating ? <ActivityIndicator size="small" color={PICK_COLORS.action} /> : <Text style={styles.startText}>{t("carton.start")}</Text>}
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    minHeight: 60,
    marginHorizontal: 12,
    marginTop: 8,
    paddingLeft: 10,
    paddingRight: 8,
    paddingVertical: 8,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    borderRadius: 12,
    backgroundColor: PICK_COLORS.action,
  },
  icon: { width: 36, height: 36, borderRadius: 8, alignItems: "center", justifyContent: "center", backgroundColor: "rgba(255,255,255,0.18)" },
  text: { flex: 1, minWidth: 0 },
  caption: { fontSize: 12, lineHeight: 16, color: "#D6E4FF", fontVariant: ["tabular-nums"] },
  number: { fontFamily: MONO_FONT, fontSize: 17, lineHeight: 22, fontWeight: "700", color: PICK_COLORS.white },
  sealButton: {
    minHeight: 40,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 10,
    borderRadius: 8,
    backgroundColor: PICK_COLORS.white,
  },
  sealText: { fontSize: 14, fontWeight: "700", color: PICK_COLORS.action },
  disabled: { opacity: 0.45 },
  startRow: {
    minHeight: 48,
    marginHorizontal: 12,
    marginTop: 8,
    paddingLeft: 10,
    paddingRight: 6,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: PICK_COLORS.outline,
    backgroundColor: PICK_COLORS.white,
  },
  startHint: { flex: 1, minWidth: 0, fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  startButton: {
    minWidth: 88,
    minHeight: 40,
    paddingHorizontal: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PICK_COLORS.infoBorder,
    backgroundColor: PICK_COLORS.infoBg,
    alignItems: "center",
    justifyContent: "center",
  },
  startText: { fontSize: 14, fontWeight: "700", color: PICK_COLORS.action },
});
