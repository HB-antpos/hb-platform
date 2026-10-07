import { useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import {
  formatDailyCloseDifference,
  formatDailyCloseMoney,
  formatDenomination,
  hasCashReconciliation,
  orderTenders,
  splitCashCounts,
  sumTenders,
  type DailyCloseNotice,
} from "@/modules/daily-closes/logic";
import type { DailyCloseCashCount, DailyCloseDetail, DailyCloseTender } from "@/modules/daily-closes/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { DifferenceTag, MarkChip } from "./DailyCloseTags";
import { AMOUNT_COLORS, DAILY_UI, DIM_TEXT, NOTICE } from "./ui";

/** 明细第一块：身份行 + 状态 + 现金对账（应有 / 实点 / 差额，差额最大最醒目）+ 公式说明。 */
export function ReconciliationCard({
  detail,
  identity,
  dateLine,
  nth,
  backfill,
}: {
  detail: DailyCloseDetail;
  identity: string;
  dateLine: string;
  nth: number | null;
  backfill: boolean;
}) {
  const { t } = useAppTranslation("dailyCloses");
  const kind = detail.differenceKind;
  const reconciled = hasCashReconciliation(detail);
  return (
    <View style={[DAILY_UI.card, styles.hero]}>
      <Text style={styles.who}>{identity}</Text>
      <Text style={styles.who}>{dateLine}</Text>
      <View style={styles.statusRow}>
        <DifferenceTag kind={kind} label={t(`status.${kind}`)} />
        {/* 无金额时标签已经说明了状态，不再重复一遍大字 */}
        {kind !== "none" ? <Text style={[styles.statusText, { color: AMOUNT_COLORS[kind] }]}>{t(`statusLine.${kind}`)}</Text> : null}
        {nth !== null ? <MarkChip label={t("mark.nth", { n: nth })} /> : null}
        {backfill ? <MarkChip label={t("mark.backfill")} emphasis /> : null}
      </View>
      {reconciled ? (
        <View>
          <ReconRow label={t("detail.reconExpected")} value={formatDailyCloseMoney(detail.expectedCashAmount)} />
          <ReconRow label={t("detail.reconCounted")} value={formatDailyCloseMoney(detail.countedCashAmount)} />
          <ReconRow label={t("detail.reconDifference")} value={formatDailyCloseDifference(detail.cashDifference)} color={AMOUNT_COLORS[kind]} big />
          <Text style={styles.formula}>{t("detail.reconFormula")}</Text>
        </View>
      ) : (
        // TraceOnly：三格都没有数据，说明「无金额」，而不是显示三个 $0.00 误导成已平
        <View style={styles.noAmount}>
          <Text style={styles.noAmountText}>{t("detail.reconNoAmount")}</Text>
        </View>
      )}
    </View>
  );
}

function ReconRow({ label, value, color, big = false }: { label: string; value: string; color?: string; big?: boolean }) {
  return (
    <View style={[styles.reconRow, big ? styles.reconRowLast : null]}>
      <Text style={styles.reconLabel}>{label}</Text>
      <Text style={[big ? styles.reconBig : styles.reconValue, DAILY_UI.mono, color ? { color } : null]}>{value}</Text>
    </View>
  );
}

/** 历史补录提示条（蓝色）：说明保存于收银端升级之前、缺了什么、营业日是否推算、升级后会自动补全。 */
export function BackfillNotice({ notice }: { notice: DailyCloseNotice }) {
  const { t, language } = useAppTranslation("dailyCloses");
  // 中文句号后不加空格，英文句子之间要空一格
  const body = [t(`detail.notice.${notice.kind}`), notice.inferred ? t("detail.notice.inferred") : null, t("detail.notice.upgrade")]
    .filter(Boolean)
    .join(language.startsWith("zh") ? "" : " ");
  return (
    <View style={styles.notice} accessibilityRole="alert">
      <Icon source="information-outline" size={18} color={NOTICE.text} />
      <Text style={styles.noticeText}>
        <Text style={styles.noticeTitle}>{t("detail.notice.title")}</Text>
        {` ${body}`}
      </Text>
    </View>
  );
}

/** 没有数据的区块：虚线占位「暂无数据 · 待该终端补传」，不整块隐藏。 */
export function DataPlaceholder() {
  const { t } = useAppTranslation("dailyCloses");
  return (
    <View style={DAILY_UI.placeholder}>
      <Text style={DAILY_UI.placeholderText}>{t("detail.placeholder")}</Text>
    </View>
  );
}

export function SectionTitle({ children }: { children: ReactNode }) {
  return <Text style={DAILY_UI.sectionLabel}>{children}</Text>;
}

const TENDER_KEYS: Record<string, "cash" | "card" | "voucher"> = { Cash: "cash", Card: "card", Voucher: "voucher" };

/** 支付方式汇总：现金 / 刷卡 / 代金券的销售、退款、净额 + 合计行。没有笔数（客户端本地不保存）。 */
export function TenderTable({ tenders }: { tenders: DailyCloseTender[] }) {
  const { t } = useAppTranslation("dailyCloses");
  const ordered = orderTenders(tenders);
  const total = sumTenders(ordered);
  return (
    <View style={DAILY_UI.card}>
      <View style={styles.tenderRow}>
        <Text style={[styles.tenderHead, styles.tenderName]}>{t("detail.tenders.method")}</Text>
        <Text style={styles.tenderHead}>{t("detail.tenders.sales")}</Text>
        <Text style={styles.tenderHead}>{t("detail.tenders.refund")}</Text>
        <Text style={styles.tenderHead}>{t("detail.tenders.net")}</Text>
      </View>
      {ordered.map((tender) => {
        const key = TENDER_KEYS[tender.method];
        return (
          <View key={tender.method} style={[styles.tenderRow, styles.tenderBorder]}>
            <Text style={[styles.tenderCell, styles.tenderName, styles.tenderNameText]} numberOfLines={1}>
              {key ? t(`detail.tenders.${key}`) : tender.method}
            </Text>
            <Text style={[styles.tenderCell, DAILY_UI.mono]}>{formatDailyCloseMoney(tender.salesAmount)}</Text>
            <Text style={[styles.tenderCell, DAILY_UI.mono]}>{formatDailyCloseMoney(tender.refundAmount)}</Text>
            <Text style={[styles.tenderCell, DAILY_UI.mono]}>{formatDailyCloseMoney(tender.netAmount)}</Text>
          </View>
        );
      })}
      <View style={[styles.tenderRow, styles.tenderBorder]}>
        <Text style={[styles.tenderCell, styles.tenderTotal, styles.tenderName]}>{t("detail.tenders.total")}</Text>
        <Text style={[styles.tenderCell, styles.tenderTotal, DAILY_UI.mono]}>{formatDailyCloseMoney(total.salesAmount)}</Text>
        <Text style={[styles.tenderCell, styles.tenderTotal, DAILY_UI.mono]}>{formatDailyCloseMoney(total.refundAmount)}</Text>
        <Text style={[styles.tenderCell, styles.tenderTotal, DAILY_UI.mono]}>{formatDailyCloseMoney(total.netAmount)}</Text>
      </View>
    </View>
  );
}

/**
 * 现金盘点明细：纸币 / 硬币分段切换（避免一屏堆 11 行），每行「面额 × 张数 = 小计」，下方小计。
 * 0 张的档位灰显但保留，能看出这一档确实点过。
 */
export function CashCountPanel({ detail }: { detail: Pick<DailyCloseDetail, "cashCounts" | "noteSubtotal" | "coinSubtotal"> }) {
  const { t } = useAppTranslation("dailyCloses");
  const groups = splitCashCounts(detail.cashCounts, detail.noteSubtotal, detail.coinSubtotal);
  const [tab, setTab] = useState<"note" | "coin">("note");
  const rows: DailyCloseCashCount[] = tab === "note" ? groups.notes : groups.coins;
  const subtotal = tab === "note" ? groups.noteSubtotal : groups.coinSubtotal;
  return (
    <View style={[DAILY_UI.card, styles.count]}>
      <View style={styles.segments} accessibilityRole="tablist">
        {(["note", "coin"] as const).map((key) => {
          const active = tab === key;
          const amount = formatDailyCloseMoney(key === "note" ? groups.noteSubtotal : groups.coinSubtotal);
          return (
            <Pressable
              key={key}
              accessibilityRole="tab"
              accessibilityState={{ selected: active }}
              onPress={() => setTab(key)}
              style={[styles.segment, active ? styles.segmentOn : null]}
            >
              <Text style={[styles.segmentText, active ? styles.segmentTextOn : null]} numberOfLines={1}>
                {t(key === "note" ? "detail.cashCount.note" : "detail.cashCount.coin", { amount })}
              </Text>
            </Pressable>
          );
        })}
      </View>
      {rows.length === 0 ? (
        <Text style={styles.emptyRows}>{t("detail.cashCount.empty")}</Text>
      ) : (
        rows.map((row) => (
          <View key={row.denominationCents} style={[styles.countRow, styles.tenderBorder]}>
            <Text style={[styles.countDenomination, row.quantity === 0 ? styles.dim : null, DAILY_UI.mono]}>{formatDenomination(row.denominationCents)}</Text>
            <Text style={[styles.countQuantity, row.quantity === 0 ? styles.dim : null, DAILY_UI.mono]}>{t("detail.cashCount.row", { quantity: row.quantity })}</Text>
            <Text style={[styles.countSubtotal, row.quantity === 0 ? styles.dim : null, DAILY_UI.mono]}>{formatDailyCloseMoney(row.subtotalAmount)}</Text>
          </View>
        ))
      )}
      <View style={styles.subtotalRow}>
        <Text style={styles.subtotalText}>{t(tab === "note" ? "detail.cashCount.noteSubtotal" : "detail.cashCount.coinSubtotal")}</Text>
        <Text style={[styles.subtotalText, DAILY_UI.mono]}>{formatDailyCloseMoney(subtotal)}</Text>
      </View>
    </View>
  );
}

/** 基本信息（默认折叠）：折叠态一行摘要，展开后是完整的键值列表。 */
export function InfoPanel({ summary, rows }: { summary: string; rows: { label: string; value: string; selectable?: boolean }[] }) {
  const { t } = useAppTranslation("dailyCloses");
  const [open, setOpen] = useState(false);
  return (
    <View style={DAILY_UI.card}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t(open ? "detail.info.collapse" : "detail.info.expand")}
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen((value) => !value)}
        style={styles.foldHead}
      >
        <View style={styles.foldTitleBox}>
          <Text style={styles.foldTitle}>{t("detail.info.title")}</Text>
          {!open ? (
            <Text style={styles.foldSummary} numberOfLines={1}>
              {summary}
            </Text>
          ) : null}
        </View>
        <Icon source={open ? "chevron-up" : "chevron-down"} size={22} color={HB_COLORS.textSecondary} />
      </Pressable>
      {open
        ? rows.map((row) => (
            <View key={row.label} style={styles.infoRow}>
              <Text style={styles.infoLabel}>{row.label}</Text>
              <Text style={styles.infoValue} selectable={row.selectable}>
                {row.value}
              </Text>
            </View>
          ))
        : null}
    </View>
  );
}

const styles = StyleSheet.create({
  hero: { gap: 6 },
  who: { fontSize: 12, color: HB_COLORS.textSecondary },
  statusRow: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 6, marginTop: 2, marginBottom: 4 },
  statusText: { fontSize: 18, fontWeight: "700" },
  reconRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingVertical: 9, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  reconRowLast: { paddingTop: 11 },
  reconLabel: { fontSize: 14, color: HB_COLORS.textSecondary },
  reconValue: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  reconBig: { fontSize: 26, fontWeight: "700" },
  formula: { fontSize: 11, color: HB_COLORS.textSecondary },
  noAmount: { paddingTop: 4 },
  noAmountText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textSecondary },
  notice: {
    flexDirection: "row",
    gap: 8,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: NOTICE.border,
    backgroundColor: NOTICE.background,
  },
  noticeText: { flex: 1, fontSize: 12, lineHeight: 19, color: NOTICE.text },
  noticeTitle: { fontWeight: "700" },
  tenderRow: { flexDirection: "row", alignItems: "center", gap: 4, paddingVertical: 7 },
  tenderBorder: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  tenderHead: { flex: 1, textAlign: "right", fontSize: 12, color: HB_COLORS.textSecondary },
  tenderName: { flex: 0, width: 58, textAlign: "left" },
  tenderCell: { flex: 1, textAlign: "right", fontSize: 12, color: HB_COLORS.textPrimary },
  tenderNameText: { fontWeight: "600" },
  tenderTotal: { fontWeight: "700" },
  count: { gap: 2 },
  segments: { flexDirection: "row", gap: 6, marginBottom: 8 },
  segment: { flex: 1, minHeight: 36, alignItems: "center", justifyContent: "center", borderRadius: 8, backgroundColor: HB_COLORS.surfaceMuted, paddingHorizontal: 4 },
  segmentOn: { backgroundColor: "#E6F0FF" },
  segmentText: { fontSize: 13, color: HB_COLORS.textSecondary },
  segmentTextOn: { fontWeight: "700", color: HB_COLORS.action },
  emptyRows: { fontSize: 12, color: HB_COLORS.textSecondary, paddingVertical: 8 },
  countRow: { flexDirection: "row", alignItems: "center", paddingVertical: 7 },
  countDenomination: { width: 56, fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary },
  countQuantity: { flex: 1, fontSize: 13, color: HB_COLORS.textSecondary },
  countSubtotal: { width: 92, textAlign: "right", fontSize: 13, color: HB_COLORS.textPrimary },
  dim: { color: DIM_TEXT, fontWeight: "400" },
  subtotalRow: { flexDirection: "row", justifyContent: "space-between", paddingTop: 9, borderTopWidth: 1, borderTopColor: HB_COLORS.outline },
  subtotalText: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  foldHead: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 44 },
  foldTitleBox: { flex: 1, minWidth: 0 },
  foldTitle: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  foldSummary: { fontSize: 12, color: HB_COLORS.textSecondary },
  infoRow: { flexDirection: "row", gap: HB_SPACING.sm, paddingVertical: 7, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  infoLabel: { width: 84, fontSize: 13, color: HB_COLORS.textSecondary },
  infoValue: { flex: 1, fontSize: 13, color: HB_COLORS.textPrimary },
});
