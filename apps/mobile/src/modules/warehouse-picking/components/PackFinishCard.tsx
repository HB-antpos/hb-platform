import { useEffect, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchPackProgress } from "../pack-api";
import { summarizeCartons } from "../pack-carton-logic";
import { openFilledCartons } from "../pack-review-logic";
import { packLaterRoute, packReviewRoute } from "../pack-routes";
import type { PackProgress } from "../pack-types";
import { PICK_COLORS } from "./pick-theme";

/**
 * 完成拣货页里的「装箱」卡：显示这张订单已装箱几只几件，并给出「先拣后装」与「核对装箱」入口。
 * 自己拉装箱进度，拉不到（后端没有二期接口 / 断网）就不显示，不影响完成拣货。
 */
export function PackFinishCard({ orderGuid }: { orderGuid: string }) {
  const { t } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const [progress, setProgress] = useState<PackProgress | null>(null);

  // 回到本页（从先拣后装 / 核对页返回）时重新拉一次。
  useEffect(() => {
    if (!focused) return;
    let cancelled = false;
    fetchPackProgress(orderGuid)
      .then((result) => {
        if (!cancelled) setProgress(result);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [focused, orderGuid]);

  if (!progress) return null;
  const summary = summarizeCartons(progress.cartons, null);
  const openCount = openFilledCartons(progress).length;

  return (
    <View style={styles.card}>
      <View style={styles.head}>
        <MaterialCommunityIcons name="package-variant-closed" size={18} color={PICK_COLORS.action} />
        <Text style={styles.title}>{t("finish.packTitle")}</Text>
      </View>
      <Text style={styles.text}>
        {summary.cartonCount > 0
          ? t("finish.packSummary", { cartons: summary.cartonCount, pieces: summary.pieceCount })
          : t("finish.packNone")}
      </Text>
      {openCount > 0 ? <Text style={styles.warning}>{t("finish.packOpen", { count: openCount })}</Text> : null}
      <View style={styles.actions}>
        <Pressable accessibilityRole="button" onPress={() => router.push(packLaterRoute(orderGuid))} style={styles.button}>
          <Text style={styles.buttonText}>{t("finish.packLater")}</Text>
        </Pressable>
        {progress.cartons.length > 0 ? (
          <Pressable accessibilityRole="button" onPress={() => router.push(packReviewRoute(orderGuid))} style={styles.button}>
            <Text style={styles.buttonText}>{t("finish.packReview")}</Text>
          </Pressable>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 12, paddingHorizontal: 14, paddingVertical: 12, gap: 8 },
  head: { flexDirection: "row", alignItems: "center", gap: 6 },
  title: { fontSize: 14, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.ink },
  text: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  warning: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: PICK_COLORS.warning },
  actions: { flexDirection: "row", gap: 8 },
  button: { flex: 1, minHeight: 44, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.infoBorder, backgroundColor: PICK_COLORS.infoBg, alignItems: "center", justifyContent: "center", paddingHorizontal: 8 },
  buttonText: { fontSize: 14, fontWeight: "700", color: PICK_COLORS.action, textAlign: "center" },
});
