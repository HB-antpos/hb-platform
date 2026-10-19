import { FlatList, StyleSheet, View } from "react-native";
import { Divider, Icon, Text } from "react-native-paper";
import { ContainerNumberRevealButton, useContainerNumberDisplay } from "@/components/container-number";
import type { WarehouseInsightContainer } from "@/modules/warehouse-product-insights/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { InsightSheet } from "./InsightSheet";

export interface InsightContainerSheetProps {
  visible: boolean;
  onClose: () => void;
  subtitle: string;
  rangeLabel: string;
  containers: WarehouseInsightContainer[];
  inboundQuantity: number;
  containerCount: number;
}

function dateLabel(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : "—";
}

/** 货柜进货明细：在途货柜单列并明示未计入合计，避免与仓库实收对不上账。 */
export function InsightContainerSheet({
  visible,
  onClose,
  subtitle,
  rangeLabel,
  containers,
  inboundQuantity,
  containerCount,
}: InsightContainerSheetProps) {
  const { t } = useAppTranslation("warehouseProductInsights");
  const { format: formatContainerNumber } = useContainerNumberDisplay();
  return (
    <InsightSheet
      visible={visible}
      title={t("containerSheet.title")}
      subtitle={subtitle}
      closeLabel={t("actions.close")}
      onClose={onClose}
    >
      <View style={styles.summary}>
        <Text style={styles.rangeText}>{rangeLabel}</Text>
        <Text style={styles.summaryText}>
          {t("containerSheet.summary", {
            count: containerCount,
            quantity: inboundQuantity.toLocaleString(),
          })}
        </Text>
        <View style={styles.notice}>
          <Icon
            source="information-outline"
            size={16}
            color={HB_COLORS.textSecondary}
          />
          <Text style={styles.noticeText}>{t("containerSheet.arrivalRule")}</Text>
        </View>
      </View>
      <View style={styles.tableHeader}>
        <View style={[styles.numberCell, styles.numberHeader]}>
          <Text style={styles.column}>{t("containerSheet.container")}</Text>
          <ContainerNumberRevealButton size={16} />
        </View>
        <Text style={[styles.column, styles.piecesColumn]}>
          {t("containerSheet.pieces")}
        </Text>
        <Text style={[styles.column, styles.quantityColumn]}>
          {t("labels.quantity")}
        </Text>
        <Text style={[styles.column, styles.statusColumn]}>
          {t("containerSheet.status")}
        </Text>
      </View>
      <FlatList
        style={styles.list}
        data={containers}
        keyExtractor={(item, index) => `${item.containerNumber}:${index}`}
        ItemSeparatorComponent={Divider}
        ListEmptyComponent={
          <View style={styles.empty}>
            <Text style={styles.emptyText}>{t("containerSheet.empty")}</Text>
          </View>
        }
        renderItem={({ item }) => (
          <View style={[styles.row, item.isEstimatedArrival ? styles.transitRow : null]}>
            <View style={styles.numberCell}>
              <Text numberOfLines={1} style={styles.containerNumber}>
                {item.containerNumber ? formatContainerNumber(item.containerNumber) : "—"}
              </Text>
              <Text
                numberOfLines={1}
                style={item.isEstimatedArrival ? styles.transitMeta : styles.meta}
              >
                {item.isEstimatedArrival
                  ? t("containerSheet.estimatedArrival", {
                      date: dateLabel(item.arrivalDate),
                    })
                  : t("containerSheet.actualArrival", {
                      date: dateLabel(item.arrivalDate),
                    })}
              </Text>
            </View>
            <Text style={[styles.value, styles.piecesColumn]}>
              {item.pieces.toLocaleString()}
            </Text>
            <Text style={[styles.value, styles.quantityColumn]}>
              {item.quantity.toLocaleString()}
            </Text>
            <Text
              style={[
                styles.statusColumn,
                styles.status,
                item.isEstimatedArrival ? styles.statusTransit : styles.statusArrived,
              ]}
            >
              {t(`containerSheet.statuses.${item.status}`, {
                defaultValue: t("containerSheet.statuses.unknown"),
              })}
            </Text>
          </View>
        )}
      />
    </InsightSheet>
  );
}

const styles = StyleSheet.create({
  summary: { padding: HB_SPACING.md, gap: HB_SPACING.xs },
  summaryText: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "600" },
  rangeText: { color: HB_COLORS.action, fontSize: 12 },
  notice: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  noticeText: {
    flex: 1,
    fontSize: 12,
    lineHeight: 18,
    color: HB_COLORS.textSecondary,
  },
  tableHeader: {
    flexDirection: "row",
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  list: { flex: 1 },
  row: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 10,
    paddingHorizontal: HB_SPACING.md,
  },
  transitRow: { backgroundColor: "#FEF6EE" },
  column: { color: HB_COLORS.textSecondary, fontSize: 12 },
  numberCell: { flex: 1, minWidth: 0, gap: 2 },
  numberHeader: { flexDirection: "row", alignItems: "center" },
  piecesColumn: { width: 52, textAlign: "right" },
  quantityColumn: { width: 60, textAlign: "right" },
  statusColumn: { width: 56, textAlign: "right" },
  containerNumber: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "600" },
  meta: { color: HB_COLORS.textSecondary, fontSize: 11 },
  transitMeta: { color: HB_COLORS.warning, fontSize: 11, fontWeight: "600" },
  value: { color: HB_COLORS.textPrimary, fontSize: 13 },
  status: { fontSize: 11 },
  statusArrived: { color: HB_COLORS.success },
  statusTransit: { color: HB_COLORS.warning },
  empty: { padding: HB_SPACING.lg, alignItems: "center" },
  emptyText: { color: HB_COLORS.textSecondary },
});
