import { memo, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Card, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface LabelPrintCardProps {
  isPrintingProduct?: boolean;
  isPrintingSmall?: boolean;
  isPrintingDiscount?: boolean;
  isPrintingBigDiscount?: boolean;
  canPrintDiscount?: boolean;
  canPrintBigDiscount?: boolean;
  onPrintProduct?: () => void;
  /** 小标签（60mm）：固定按小标签打印，不受打印设置里「小标签」开关影响。 */
  onPrintSmall?: () => void;
  onPrintDiscount?: () => void;
  onPrintBigDiscount?: () => void;
  /** 行首槽位（打印机连接状态 chip）；不传时显示"标签"文字标题。 */
  leading?: ReactNode;
  /** 与标签合并在同一张卡里的下一行（清货价行）。 */
  footer?: ReactNode;
}

export const LabelPrintCard = memo(function LabelPrintCard({
  isPrintingProduct = false,
  isPrintingSmall = false,
  isPrintingDiscount = false,
  isPrintingBigDiscount = false,
  canPrintDiscount = false,
  canPrintBigDiscount = false,
  onPrintProduct,
  onPrintSmall,
  onPrintDiscount,
  onPrintBigDiscount,
  leading,
  footer,
}: LabelPrintCardProps) {
  const { t } = useAppTranslation(["productQuery"]);

  return (
    <Card style={styles.card} mode="contained">
      <View style={styles.row}>
        {leading ?? <Text style={styles.rowTitle} numberOfLines={1}>{t("print.label")}</Text>}
        <View style={styles.actions}>
          <Button
            compact
            mode="contained"
            onPress={onPrintProduct}
            loading={isPrintingProduct}
            disabled={!onPrintProduct || isPrintingProduct}
            // 按钮上的文字是为小屏做的简写；读屏朗读完整名称。
            accessibilityLabel={t("print.product")}
            style={styles.button}
            labelStyle={styles.buttonLabel}
          >
            {t("print.productShort")}
          </Button>
          <Button
            compact
            mode="contained"
            onPress={onPrintSmall}
            loading={isPrintingSmall}
            disabled={!onPrintSmall || isPrintingSmall}
            accessibilityLabel={t("print.smallLabel")}
            style={styles.button}
            labelStyle={styles.buttonLabel}
          >
            {t("print.smallShort")}
          </Button>
          <Button
            compact
            mode="contained-tonal"
            onPress={onPrintDiscount}
            loading={isPrintingDiscount}
            disabled={!onPrintDiscount || !canPrintDiscount || isPrintingDiscount}
            accessibilityLabel={t("print.discount")}
            style={styles.button}
            labelStyle={styles.buttonLabel}
          >
            {t("print.discountShort")}
          </Button>
          <Button
            compact
            mode="outlined"
            onPress={onPrintBigDiscount}
            loading={isPrintingBigDiscount}
            disabled={!onPrintBigDiscount || !canPrintBigDiscount || isPrintingBigDiscount}
            accessibilityLabel={t("print.bigDiscount")}
            style={styles.button}
            labelStyle={styles.buttonLabel}
          >
            {t("print.bigDiscountShort")}
          </Button>
        </View>
      </View>
      {footer ? <View style={styles.footer}>{footer}</View> : null}
    </Card>
  );
});

const styles = StyleSheet.create({
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    // 360dp 屏的预算：容器内边距 16×2 → 行内边距 8×2 → 状态图标 36 + 间距 6 → 四个按钮各约 64dp。
    // 真机 TC26 字体缩放 1.15 倍：13sp 的 “Discount” 实测约 60dp，几乎零余量，所以英文按钮用简写
    // （Reg / Small / Disc / Large，最长约 38dp），读屏仍读完整名称。
    gap: 6,
    minHeight: 52,
    paddingHorizontal: HB_SPACING.xs,
  },
  rowTitle: {
    minWidth: 32,
    flexShrink: 0,
    fontSize: 13,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  actions: {
    flex: 1,
    flexDirection: "row",
    // 状态图标去掉文字后，四个打印按钮要在 Zebra 小屏（约 360dp）一行排下：间距与标签边距都收紧一点。
    gap: 4,
  },
  button: {
    flex: 1,
    // 去掉 Paper 紧凑按钮默认的最小宽度，否则四个按钮会撑出一行。
    minWidth: 0,
    borderRadius: HB_RADIUS.control,
  },
  buttonLabel: {
    marginHorizontal: 2,
    fontSize: 13,
  },
  footer: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
});
