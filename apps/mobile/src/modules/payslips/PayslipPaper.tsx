// MYOB 默认 Pay Slip 样式的「纸面」卡片：白底黑线，字段与顺序沿用 MYOB 原样；
// 窄屏下把六列明细表重排成「两行一条」。数值逐项取自后端，不在客户端计算。
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { buildLineViews, buildPaperFields } from "./detail-rows";
import { formatMoney } from "./format";
import { MONO, PAYSLIP_COLORS } from "./payslip-ui";
import type { PayslipDetail } from "./types";

export function PayslipPaper({ detail }: { detail: PayslipDetail }) {
  const { t } = useAppTranslation("payslips");
  const fields = buildPaperFields(detail);
  const lines = buildLineViews(detail.lines);
  return (
    <View style={styles.paper} accessibilityLabel={t("a11y.paper")}>
      <Text style={styles.company}>{detail.companyName}</Text>

      <View style={styles.fields}>
        {fields.map((field) => (
          <View key={field.label} style={styles.fieldRow}>
            <Text style={styles.fieldLabel}>{field.label}</Text>
            <Text style={[styles.fieldValue, MONO]}>{field.value}</Text>
          </View>
        ))}
      </View>

      <View style={styles.totals}>
        <View style={styles.grossCell}>
          <Text style={styles.totalLabel}>GROSS PAY</Text>
          <Text style={[styles.totalValue, MONO]} numberOfLines={1} adjustsFontSizeToFit>
            {formatMoney(detail.grossPay)}
          </Text>
        </View>
        <View style={styles.netCell}>
          <Text style={[styles.totalLabel, styles.netText]}>NET PAY</Text>
          <Text style={[styles.totalValue, styles.netText, MONO]} numberOfLines={1} adjustsFontSizeToFit>
            {formatMoney(detail.netPay)}
          </Text>
        </View>
      </View>

      <View style={styles.tableHead}>
        <Text style={styles.tableHeadText}>DESCRIPTION / TYPE</Text>
        <Text style={styles.tableHeadText}>AMOUNT</Text>
      </View>
      {lines.map((line) => (
        <View key={line.key} style={styles.line}>
          <View style={styles.lineTop}>
            <View style={styles.lineTitle}>
              <Text style={styles.lineDescription}>{line.description}</Text>
              <View style={styles.typeTag}>
                <Text style={styles.typeTagText}>{line.typeLabel}</Text>
              </View>
            </View>
            <Text style={[styles.lineAmount, MONO]}>{line.amountText}</Text>
          </View>
          {line.calcText || line.ytdText ? (
            <View style={styles.lineBottom}>
              <Text style={[styles.lineMeta, MONO]}>{line.calcText}</Text>
              <Text style={[styles.lineMeta, MONO]}>{line.ytdText}</Text>
            </View>
          ) : null}
        </View>
      ))}

      <Text style={styles.memo}>Memo/payslip message:</Text>
      <Text style={styles.page}>Page 1 of 1</Text>
    </View>
  );
}

const LINE = PAYSLIP_COLORS.paperLine;

const styles = StyleSheet.create({
  paper: { padding: HB_SPACING.md, borderRadius: 12, backgroundColor: PAYSLIP_COLORS.paper, borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  company: { fontSize: 18, lineHeight: 26, fontWeight: "700", color: HB_COLORS.textPrimary, marginBottom: HB_SPACING.sm },
  fields: { gap: 6, marginBottom: HB_SPACING.md },
  fieldRow: { flexDirection: "row", justifyContent: "space-between", gap: HB_SPACING.sm },
  fieldLabel: { fontSize: 15, lineHeight: 22, color: HB_COLORS.textSecondary },
  fieldValue: { flexShrink: 1, fontSize: 15, lineHeight: 22, color: HB_COLORS.textPrimary, textAlign: "right" },
  totals: { flexDirection: "row", borderWidth: 1, borderColor: LINE, marginBottom: HB_SPACING.md },
  grossCell: { flex: 1, padding: HB_SPACING.sm, gap: 2, backgroundColor: PAYSLIP_COLORS.paper },
  netCell: { flex: 1, padding: HB_SPACING.sm, gap: 2, backgroundColor: PAYSLIP_COLORS.netBg },
  totalLabel: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary, letterSpacing: 0.4 },
  totalValue: { fontSize: 22, lineHeight: 30, fontWeight: "700", color: HB_COLORS.textPrimary },
  netText: { color: HB_COLORS.white },
  tableHead: { flexDirection: "row", justifyContent: "space-between", paddingHorizontal: HB_SPACING.xs, paddingVertical: 6, backgroundColor: PAYSLIP_COLORS.tableHeadBg, borderWidth: 1, borderColor: LINE },
  tableHeadText: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textPrimary },
  line: { gap: 4, paddingHorizontal: HB_SPACING.xs, paddingVertical: HB_SPACING.xs + 2, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outline },
  lineTop: { flexDirection: "row", justifyContent: "space-between", alignItems: "flex-start", gap: HB_SPACING.xs },
  lineTitle: { flex: 1, flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 6 },
  lineDescription: { fontSize: 15, lineHeight: 21, color: HB_COLORS.textPrimary },
  typeTag: { paddingHorizontal: 6, paddingVertical: 1, borderRadius: 4, borderWidth: 1, borderColor: PAYSLIP_COLORS.tagBorder },
  typeTagText: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary },
  lineAmount: { fontSize: 15, lineHeight: 21, fontWeight: "700", color: HB_COLORS.textPrimary },
  lineBottom: { flexDirection: "row", justifyContent: "space-between", gap: HB_SPACING.xs },
  lineMeta: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  memo: { marginTop: HB_SPACING.md, fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  page: { marginTop: HB_SPACING.md, fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary, textAlign: "right" },
});
