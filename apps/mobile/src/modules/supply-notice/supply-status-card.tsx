import { Image, StyleSheet, View } from "react-native";
import { Button, Chip, Text } from "react-native-paper";
import { labelColon } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { formatSupplyDay, formatSupplyExpected } from "./format-expected";
import type { StoreSupplyStatus } from "./types";

export interface SupplyStatusCardProps {
  status: StoreSupplyStatus;
  busy?: boolean;
  onWatch?: (status: StoreSupplyStatus) => void;
  onUnwatch?: (status: StoreSupplyStatus) => void;
  /** 已恢复订货时：确认提醒。 */
  onAcknowledge?: (status: StoreSupplyStatus) => void;
  /** 已恢复订货时：去订货。 */
  onOrder?: (status: StoreSupplyStatus) => void;
}

/**
 * 分店端的商品供货状态卡：搜索 / 扫码零结果、关注列表共用。
 * 回答三个问题——还会不会有、什么时候能订、现在能做什么（关注 / 去订货）。
 */
export function SupplyStatusCard({ status, busy, onWatch, onUnwatch, onAcknowledge, onOrder }: SupplyStatusCardProps) {
  const { t } = useAppTranslation("supplyNotice");
  const restocked = status.isOrderable;
  const showExpected = !restocked && status.supplyPlan !== "Discontinued";
  const title = restocked ? t("restocked") : t(`storeTitle.${status.supplyPlan}`);
  const chipStyle = restocked
    ? styles.chipRestocked
    : status.supplyPlan === "Discontinued"
      ? styles.chipMuted
      : status.supplyPlan === "WillRestock"
        ? styles.chipInfo
        : styles.chipWarn;
  const updatedAt = formatSupplyDay(status.noticeUpdatedAtUtc, t);

  return (
    <View style={styles.card} testID={`supply-status-card-${status.productCode}`}>
      <View style={styles.row}>
        {status.productImage ? (
          <Image source={{ uri: status.productImage }} style={styles.image} resizeMode="cover" />
        ) : (
          <View style={[styles.image, styles.imagePlaceholder]} />
        )}
        <View style={styles.body}>
          <Text variant="titleSmall" numberOfLines={2} style={styles.name}>{status.productName || status.productCode}</Text>
          <Text variant="bodySmall" style={styles.muted}>{status.itemNumber || status.productCode}</Text>
          <Chip compact style={[styles.chip, chipStyle]} textStyle={styles.chipText}>{title}</Chip>
          {showExpected ? (
            <Text variant="bodySmall" style={styles.line}>
              <Text style={styles.muted}>{t("expectedLabel")}{labelColon()}</Text>
              <Text style={status.isOverdue ? styles.overdue : undefined}>{formatSupplyExpected(status, t)}</Text>
            </Text>
          ) : null}
          {!restocked && status.storeFacingNote ? (
            <Text variant="bodySmall" style={styles.line}>
              <Text style={styles.muted}>{t("noteLabel")}{labelColon()}</Text>
              {status.storeFacingNote}
            </Text>
          ) : null}
          {!restocked && updatedAt ? (
            <Text variant="bodySmall" style={styles.muted}>{t("updatedAt", { date: updatedAt })}</Text>
          ) : null}
        </View>
      </View>
      <View style={styles.actions}>
        {restocked ? (
          <>
            {onOrder ? (
              <Button compact mode="contained" icon="cart-outline" onPress={() => onOrder(status)} contentStyle={styles.buttonContent}>
                {t("goOrder")}
              </Button>
            ) : null}
            {onAcknowledge ? (
              <Button compact mode="outlined" icon="check" loading={busy} disabled={busy} onPress={() => onAcknowledge(status)} contentStyle={styles.buttonContent}>
                {t("acknowledge")}
              </Button>
            ) : null}
          </>
        ) : status.isWatching ? (
          onUnwatch ? (
            <Button compact mode="outlined" loading={busy} disabled={busy} onPress={() => onUnwatch(status)} contentStyle={styles.buttonContent}>
              {t("watching")}
            </Button>
          ) : null
        ) : onWatch ? (
          <Button compact mode="contained-tonal" icon="bell-outline" loading={busy} disabled={busy} onPress={() => onWatch(status)} contentStyle={styles.buttonContent}>
            {t("watch")}
          </Button>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
  },
  row: { flexDirection: "row", gap: HB_SPACING.md },
  image: { width: 64, height: 64, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  imagePlaceholder: { borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  body: { flex: 1, gap: 4 },
  name: { fontWeight: "700" },
  muted: { color: HB_COLORS.textSecondary },
  line: { lineHeight: 18 },
  overdue: { color: HB_COLORS.warning },
  chip: { alignSelf: "flex-start", height: 26 },
  chipText: { fontSize: 12, lineHeight: 16, marginVertical: 0 },
  chipRestocked: { backgroundColor: "#E6F7EC" },
  chipInfo: { backgroundColor: "#E8F1FF" },
  chipWarn: { backgroundColor: "#FFF4E0" },
  chipMuted: { backgroundColor: "#EEEEEE" },
  actions: { flexDirection: "row", gap: HB_SPACING.sm, flexWrap: "wrap" },
  buttonContent: { minHeight: 40 },
});
