import { memo } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Card, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface ScannedCodeBannerProps {
  kind: "set" | "multi";
  barcode: string;
  /** 已格式化的该码价格（含 $）；null 显示 "--"。 */
  priceText: string | null;
  /** 多码跟随主档的折扣文案（如 "-20%"）；无折扣不传。 */
  discountText?: string | null;
  mainBarcode?: string | null;
  /** 已格式化的主档门店价（含 $）。 */
  mainPriceText: string | null;
  /** true：价格页签的打印入口暂时改打主条码 + 主档价。 */
  printMain: boolean;
  onTogglePrintMain: () => void;
}

/** 单行可容纳的条码最大长度，与码表卡同一档：更长的改两行原字号显示。 */
const BARCODE_SINGLE_LINE_MAX_LENGTH = 16;

/**
 * 「本次扫到」焦点码条：扫到套装 / 多码子码后，把该码的条码与应打价格提到首屏，
 * 并说明价格页签的打印按钮会打这个码；店员想打主档时在这里显式切换，而不是靠记忆。
 */
export const ScannedCodeBanner = memo(function ScannedCodeBanner({
  kind,
  barcode,
  priceText,
  discountText,
  mainBarcode,
  mainPriceText,
  printMain,
  onTogglePrintMain,
}: ScannedCodeBannerProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const chipText = printMain
    ? t("scanned.chipMain")
    : t(kind === "set" ? "scanned.chipSet" : "scanned.chipMulti");
  const codeText = printMain ? mainBarcode?.trim() || t("common:na") : barcode;
  const price = printMain ? mainPriceText : priceText;
  const note = printMain
    ? t("scanned.noteMain")
    : t(kind === "set" ? "scanned.noteSet" : "scanned.noteMulti");

  return (
    <Card style={[styles.card, printMain ? styles.cardMain : null]} mode="contained">
      {/* 扫码命中后读屏自动播报这一行，店员不用看屏也知道识别成了哪个码。 */}
      <View style={styles.row} accessibilityLiveRegion="polite">
        <View style={[styles.chip, printMain ? styles.chipMain : null]}>
          <Text style={[styles.chipText, printMain ? styles.chipTextMain : null]} numberOfLines={1}>
            {chipText}
          </Text>
        </View>
        <Text
          style={styles.barcode}
          numberOfLines={codeText.length > BARCODE_SINGLE_LINE_MAX_LENGTH ? 2 : 1}
          adjustsFontSizeToFit
          minimumFontScale={0.6}
        >
          {codeText}
        </Text>
        <View style={styles.priceBlock}>
          <Text style={styles.price} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.75}>
            {price ?? "--"}
          </Text>
          {!printMain && discountText ? (
            <Text style={styles.discount} numberOfLines={1}>{discountText}</Text>
          ) : null}
        </View>
      </View>
      <View style={styles.footer}>
        <Text style={styles.note} numberOfLines={1}>{note}</Text>
        <Button
          compact
          mode="text"
          onPress={onTogglePrintMain}
          style={styles.toggle}
          labelStyle={styles.toggleLabel}
        >
          {printMain ? t("scanned.toggleToScanned") : t("scanned.toggleToMain")}
        </Button>
      </View>
    </Card>
  );
});

const styles = StyleSheet.create({
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: "#A6F4C5",
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  // 改打主条码时整条转为中性灰，避免绿色仍暗示「正在打扫到的码」。
  cardMain: {
    borderColor: HB_COLORS.outlineMuted,
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.xs,
  },
  chip: {
    flexShrink: 0,
    borderRadius: 999,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 2,
    backgroundColor: "#DCFCE7",
  },
  chipMain: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  chipText: {
    fontSize: 12,
    lineHeight: 16,
    fontWeight: "700",
    color: "#166534",
  },
  chipTextMain: {
    color: HB_COLORS.textSecondary,
  },
  barcode: {
    flex: 1,
    fontSize: 16,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  priceBlock: {
    flexShrink: 0,
    alignItems: "flex-end",
  },
  price: {
    fontSize: 18,
    fontWeight: "800",
    color: HB_COLORS.action,
    fontVariant: ["tabular-nums"],
  },
  discount: {
    fontSize: 12,
    fontWeight: "700",
    color: HB_COLORS.danger,
  },
  footer: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.sm,
    paddingRight: HB_SPACING.xxs,
  },
  note: {
    flex: 1,
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  toggle: {
    flexShrink: 0,
    minWidth: 0,
  },
  toggleLabel: {
    marginHorizontal: HB_SPACING.xs,
    fontSize: 13,
  },
});
