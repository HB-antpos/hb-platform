import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Card, IconButton, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface CodeTableRow {
  id: string;
  barcode?: string | null;
  /** 已格式化的价格（不含 $）；null 表示无价格。 */
  price: string | null;
  /** 价格跟随主条码（多码零售价为空）时显示灰底「跟随」。 */
  followsMain?: boolean;
  dirty?: boolean;
}

interface CodeTableCardProps {
  title: string;
  priceColumnLabel: string;
  loadingText: string;
  rows: CodeTableRow[];
  totalCount?: number;
  savingItemId?: string | null;
  printingItemId?: string | null;
  adding?: boolean;
  loading?: boolean;
  loadingMore?: boolean;
  hasMore?: boolean;
  onEditItemBarcode: (id: string) => void;
  onEditItemRetailPrice: (id: string) => void;
  onSaveItem: (id: string) => void;
  onPrintItem: (id: string) => void;
  onAddItem: () => void;
  onLoadMore?: () => void;
  /**
   * 离线态只读：隐藏新增与整行保存、关闭「加载更多」（离线快照已含全部码），
   * 打印保留 —— 打标签是纯本地的蓝牙操作，断网时正是最需要的功能。
   */
  readOnly?: boolean;
}

/** 单行可容纳的条码最大长度：16 位在 PDA 放大字号下缩到约 80% 仍清晰，更长的改两行。 */
const BARCODE_SINGLE_LINE_MAX_LENGTH = 16;

function getBarcodeLineCount(barcode?: string | null): 1 | 2 {
  return (barcode?.length ?? 0) > BARCODE_SINGLE_LINE_MAX_LENGTH ? 2 : 1;
}

/** 套装 / 多码共用的「条码 | 价格 | 操作」表格卡。 */
export function CodeTableCard({
  title,
  priceColumnLabel,
  loadingText,
  rows,
  totalCount,
  savingItemId,
  printingItemId,
  adding = false,
  loading,
  loadingMore,
  hasMore,
  onEditItemBarcode,
  onEditItemRetailPrice,
  onSaveItem,
  onPrintItem,
  onAddItem,
  onLoadMore,
  readOnly = false,
}: CodeTableCardProps) {
  const { t } = useAppTranslation("productQuery");
  const remaining = totalCount != null ? Math.max(totalCount - rows.length, 0) : 0;

  return (
    <Card style={styles.card} mode="contained">
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.title} numberOfLines={1}>{title}</Text>
          {totalCount != null ? (
            <Text style={styles.loaded}>
              {t("codes.loaded", { loaded: rows.length, total: totalCount })}
            </Text>
          ) : null}
        </View>
        {readOnly ? null : (
          <Button
            compact
            mode="contained-tonal"
            icon="plus"
            onPress={onAddItem}
            loading={adding}
            disabled={adding}
          >
            {t("setCode.add")}
          </Button>
        )}
      </View>

      <View style={styles.columns}>
        <Text style={[styles.columnLabel, styles.barcodeColumn]}>{t("codes.barcodeColumn")}</Text>
        <Text style={[styles.columnLabel, styles.priceColumn]}>{priceColumnLabel}</Text>
        <Text style={[styles.columnLabel, styles.actionsColumn]}>{t("codes.actionsColumn")}</Text>
      </View>

      {loading ? (
        <View style={styles.loadingRow}>
          <ActivityIndicator size="small" />
          <Text variant="bodySmall" style={styles.loadingText}>{loadingText}</Text>
        </View>
      ) : null}

      {rows.map((row) => {
        const saving = savingItemId === row.id;
        const printing = printingItemId === row.id;
        return (
          <View key={row.id} style={[styles.row, row.dirty ? styles.rowDirty : null]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${t("codes.barcodeColumn")} ${row.barcode ?? "--"}`}
              onPress={readOnly ? undefined : () => onEditItemBarcode(row.id)}
              style={[styles.cell, styles.barcodeColumn, row.dirty ? styles.cellDirty : null]}
            >
              {/*
                条码任何情况下都要完整显示（不出现省略号）：斑马 PDA（360dp 宽、系统字号放大）上
                13 位 EAN 曾被尾部省略成「9527815000…」，而同一商品的多个码往往只差最后几位。
                常规长度单行 + 自动缩字号放下整串；超长码直接两行原字号显示。
                Android 的 adjustsFontSizeToFit 只在行数超过 numberOfLines 时才缩字号，
                若统一给 2 行会先折行而不是先缩字号，所以按码长分档。
              */}
              <Text
                style={styles.barcodeText}
                numberOfLines={getBarcodeLineCount(row.barcode)}
                adjustsFontSizeToFit
                minimumFontScale={0.6}
              >
                {row.barcode ?? "--"}
              </Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${priceColumnLabel} ${row.followsMain ? t("codes.follow") : row.price ?? "--"}`}
              onPress={readOnly ? undefined : () => onEditItemRetailPrice(row.id)}
              style={[
                styles.cell,
                styles.priceColumn,
                row.followsMain ? styles.cellFollow : null,
                row.dirty ? styles.cellDirty : null,
              ]}
            >
              <Text
                style={[styles.priceText, row.followsMain ? styles.followText : null]}
                numberOfLines={1}
                adjustsFontSizeToFit
                minimumFontScale={0.75}
              >
                {row.followsMain ? t("codes.follow") : row.price != null ? `$${row.price}` : "--"}
              </Text>
            </Pressable>
            <View style={[styles.actionsColumn, styles.actions]}>
              {/* 编码为单行即时保存：无改动时保存按钮置灰禁用，有改动才可点。 */}
              {readOnly ? null : (
              <IconButton
                icon="content-save-outline"
                accessibilityLabel={t("codes.saveRow")}
                size={18}
                mode={row.dirty ? "contained" : undefined}
                containerColor={row.dirty ? HB_COLORS.brand : undefined}
                iconColor={row.dirty ? HB_COLORS.white : "#98A2B3"}
                onPress={() => onSaveItem(row.id)}
                loading={saving}
                disabled={!row.dirty || saving}
                style={styles.actionButton}
              />
              )}
              <IconButton
                icon="printer-outline"
                accessibilityLabel={t("codes.printRow")}
                size={18}
                onPress={() => onPrintItem(row.id)}
                loading={printing}
                disabled={printing}
                style={styles.actionButton}
              />
            </View>
          </View>
        );
      })}

      {hasMore && onLoadMore && !readOnly ? (
        <Button
          compact
          mode="text"
          onPress={onLoadMore}
          loading={loadingMore}
          disabled={loadingMore}
          style={styles.loadMore}
        >
          {t("codes.loadMore", { count: remaining })}
        </Button>
      ) : null}
    </Card>
  );
}

const styles = StyleSheet.create({
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
    paddingBottom: HB_SPACING.xxs,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.xs,
  },
  headerText: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "baseline",
    gap: HB_SPACING.xs,
  },
  title: {
    fontSize: 15,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    flexShrink: 1,
  },
  loaded: {
    fontSize: 12,
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  columns: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  columnLabel: {
    fontSize: 11,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
  },
  barcodeColumn: {
    flex: 1,
    minWidth: 0,
  },
  // 价格列收窄让位给条码列；「$123.99」这类长价格靠 adjustsFontSizeToFit 缩字号放下。
  priceColumn: {
    width: 72,
  },
  actionsColumn: {
    width: 76,
    textAlign: "center",
  },
  loadingRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  loadingText: {
    color: HB_COLORS.textSecondary,
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  rowDirty: {
    backgroundColor: "#EAF2FF",
  },
  cell: {
    minHeight: 36,
    justifyContent: "center",
    paddingHorizontal: 6,
    borderRadius: 6,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  cellFollow: {
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  cellDirty: {
    borderWidth: 2,
    borderColor: HB_COLORS.brand,
    // 边框 1→2 时内边距同步减 1，保持文字位置不跳动。
    paddingHorizontal: 5,
  },
  barcodeText: {
    fontSize: 14,
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  priceText: {
    fontSize: 14,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  followText: {
    fontWeight: "500",
    color: HB_COLORS.textSecondary,
    textAlign: "center",
  },
  actions: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 2,
  },
  actionButton: {
    width: 34,
    height: 34,
    margin: 0,
  },
  loadMore: {
    alignSelf: "center",
    marginTop: HB_SPACING.xxs,
  },
});
