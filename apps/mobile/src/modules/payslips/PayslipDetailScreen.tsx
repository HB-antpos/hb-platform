// 「工资单明细」页（设计图 ②③）：头卡 + 「查看 PDF 原样」行 + MYOB 样式纸面卡片 + 分店归属与我方考勤工时 + 免责声明；
// 底部固定操作条「下载 PDF / 发送邮件」，没有对应权限（canDownloadPdf / canSendEmail）时按钮不渲染。
// 「查看 PDF 原样」：仓库没有可用的 PDF 内嵌预览（无 WebView / PDF 依赖，且不允许新增原生依赖），
// 所以先下载到缓存，再交给系统 PDF 查看器（安卓）或分享面板的快速查看（iOS）；用完删除缓存文件。
import { useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useLocalSearchParams, useRouter } from "expo-router";
import { Button, Snackbar, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { buildHoursView, parsePayslipKeyParams } from "./detail-model";
import { describePayslipError } from "./errors";
import { formatAbn } from "./format";
import { MONO, NoticeBar, PAYSLIP_COLORS, PayslipErrorCard, PayslipFrame, PayslipLoading, PayslipMessage, Tag } from "./payslip-ui";
import { PayslipEmailSheet } from "./PayslipEmailSheet";
import { PayslipPaper } from "./PayslipPaper";
import { downloadAndOpenPayslipPdf } from "./pdf";
import type { PayslipDetail, PayslipEmailResult, PayslipKey } from "./types";
import { usePayslipDetail, usePayslipHours } from "./use-payslips";
import { usePayslipGuard } from "./use-payslip-guard";

/** 路由外壳：/(shell)/payslips/detail?connectionGuid&employeeUid&paymentDate&periodStart */
export function PayslipDetailScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ connectionGuid?: string; employeeUid?: string; paymentDate?: string; periodStart?: string }>();
  const { t } = useAppTranslation(["payslips", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = usePayslipGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/payslips"));
  const key = parsePayslipKeyParams(params);
  if (blocked) return <PayslipMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  if (!key) return <PayslipMessage message={t("messages.missingKey")} backLabel={t("common:actions.back")} onBack={goBack} />;
  return <PayslipDetailContent key={userGuid} payslipKey={key} onBack={goBack} />;
}

export function PayslipDetailContent({ payslipKey, onBack }: { payslipKey: PayslipKey; onBack: () => void }) {
  const { t, language } = useAppTranslation(["payslips", "common"]);
  const insets = useSafeAreaInsets();
  const query = usePayslipDetail(payslipKey);
  const detail = query.data;
  const [hoursRequested, setHoursRequested] = useState(false);
  const [emailOpen, setEmailOpen] = useState(false);
  const [busy, setBusy] = useState<"share" | "view" | null>(null);
  const [snackbar, setSnackbar] = useState("");

  const runPdf = async (mode: "share" | "view") => {
    if (!detail || busy) return;
    setBusy(mode);
    try {
      await downloadAndOpenPayslipPdf(payslipKey, detail.employeeName, mode);
    } catch (error) {
      setSnackbar(describePayslipError(t, language, error));
    } finally {
      setBusy(null);
    }
  };

  const onSent = (result: PayslipEmailResult, copyRequested: boolean) => {
    setEmailOpen(false);
    const key = copyRequested && !result.copiedToMe ? "email.sentCopyFailed" : "email.sent";
    setSnackbar(t(key, { to: result.sentTo }));
  };

  const showBar = Boolean(detail && (detail.canDownloadPdf || detail.canSendEmail));

  return (
    <PayslipFrame onBack={onBack} title={t("detailTitle")}>
      {!detail ? (
        query.isError ? (
          <PayslipErrorCard
            title={t("detail.error.title")}
            detail={describePayslipError(t, language, query.error)}
            retryLabel={t("detail.error.retry")}
            onRetry={() => void query.refetch()}
          />
        ) : (
          <PayslipLoading label={t("detail.loading")} />
        )
      ) : (
        <>
          <ScrollView contentContainerStyle={[styles.content, { paddingBottom: showBar ? HB_SPACING.lg : HB_SPACING.xl + insets.bottom }]}>
            <HeadCard detail={detail} />
            {detail.canDownloadPdf ? <ViewPdfRow busy={busy === "view"} disabled={busy !== null} onPress={() => void runPdf("view")} /> : null}
            <PayslipPaper detail={detail} />
            <PlacementCard payslipKey={payslipKey} detail={detail} requested={hoursRequested} onRequest={() => setHoursRequested(true)} />
            <View style={styles.disclaimer}>
              <Text style={styles.disclaimerText}>{t("detail.disclaimer")}</Text>
              {language === "zh" ? <Text style={styles.disclaimerText}>{t("detail.disclaimerEn")}</Text> : null}
            </View>
          </ScrollView>
          {showBar ? (
            <View style={[styles.bar, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
              {detail.canDownloadPdf ? (
                <Button
                  mode="contained"
                  icon="download-outline"
                  onPress={() => void runPdf("share")}
                  loading={busy === "share"}
                  disabled={busy !== null}
                  style={styles.barButton}
                  contentStyle={styles.barButtonContent}
                  labelStyle={styles.barLabel}
                >
                  {busy === "share" ? t("detail.downloading") : t("detail.downloadPdf")}
                </Button>
              ) : null}
              {detail.canSendEmail ? (
                <Button
                  mode="outlined"
                  icon="email-outline"
                  onPress={() => setEmailOpen(true)}
                  disabled={busy !== null}
                  style={styles.barButton}
                  contentStyle={styles.barButtonContent}
                  labelStyle={styles.barLabel}
                >
                  {t("detail.sendEmail")}
                </Button>
              ) : null}
            </View>
          ) : null}
          {detail.canSendEmail ? (
            <PayslipEmailSheet visible={emailOpen} payslipKey={payslipKey} detail={detail} canDownload={detail.canDownloadPdf} onClose={() => setEmailOpen(false)} onSent={onSent} />
          ) : null}
        </>
      )}
      <Snackbar visible={snackbar.length > 0} onDismiss={() => setSnackbar("")} duration={5000} wrapperStyle={styles.snackbarWrap}>
        {snackbar}
      </Snackbar>
    </PayslipFrame>
  );
}

function HeadCard({ detail }: { detail: PayslipDetail }) {
  const { t } = useAppTranslation("payslips");
  const abn = formatAbn(detail.companyAbn);
  return (
    <View style={styles.card}>
      <Text style={styles.name}>
        {detail.employeeName}
        {detail.displayId ? <Text style={[styles.displayId, MONO]}>{`  ${detail.displayId}`}</Text> : null}
      </Text>
      <Text style={styles.company}>{abn ? `${detail.companyName} · ABN ${abn}` : detail.companyName}</Text>
      <View style={styles.tags}>
        <Tag label={t("detail.paid")} tone="success" />
        {detail.payFrequency ? <Tag label={detail.payFrequency} /> : null}
        {detail.stores.map((store) => (
          <Tag key={store.storeCode} label={store.storeLabel} tone="info" />
        ))}
        {detail.placement === "Unbound" ? <Tag label={t("list.row.unbound")} tone="warning" /> : null}
      </View>
      {detail.samePaymentDateCount > 1 ? <Text style={styles.sameDate}>{t("detail.samePaymentDate", { count: detail.samePaymentDateCount })}</Text> : null}
    </View>
  );
}

function ViewPdfRow({ busy, disabled, onPress }: { busy: boolean; disabled: boolean; onPress: () => void }) {
  const { t } = useAppTranslation("payslips");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("detail.viewPdf")}
      accessibilityState={{ disabled, busy }}
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [styles.pdfRow, pressed ? styles.pressed : null]}
    >
      <MaterialCommunityIcons name="file-document-outline" size={22} color={HB_COLORS.action} />
      <Text style={styles.pdfRowTitle}>{busy ? t("detail.downloading") : t("detail.viewPdf")}</Text>
      <Text style={styles.pdfRowHint}>{t("detail.viewPdfHint")}</Text>
      <MaterialCommunityIcons name="chevron-right" size={20} color={HB_COLORS.action} />
    </Pressable>
  );
}

/** 分店归属与我方考勤工时：在纸面之外，不进 PDF / 邮件；工时点开才请求。 */
function PlacementCard({ payslipKey, detail, requested, onRequest }: { payslipKey: PayslipKey; detail: PayslipDetail; requested: boolean; onRequest: () => void }) {
  const { t, language } = useAppTranslation("payslips");
  const hoursQuery = usePayslipHours(payslipKey, requested);
  const hoursView = hoursQuery.data ? buildHoursView(hoursQuery.data) : null;
  return (
    <View style={styles.card}>
      <View style={styles.placementHead}>
        <Text style={styles.placementTitle}>{t("detail.placement.title")}</Text>
        <Text style={styles.reference}>{t("detail.placement.reference")}</Text>
      </View>
      <Text style={styles.placementNote}>{t(`detail.placement.${detail.placement}`)}</Text>
      <View style={styles.tags}>
        {detail.stores.length === 0 ? <Tag label={t("detail.placement.noStore")} /> : null}
        {detail.stores.map((store) => (
          <Tag key={store.storeCode} label={store.storeLabel} tone="info" />
        ))}
      </View>
      {!requested ? (
        <Pressable accessibilityRole="button" onPress={onRequest} style={styles.linkRow}>
          <Text style={styles.link}>{t("detail.placement.viewHours")}</Text>
          <MaterialCommunityIcons name="chevron-right" size={18} color={HB_COLORS.action} />
        </Pressable>
      ) : hoursQuery.isLoading ? (
        <PayslipLoading label={t("detail.placement.hoursLoading")} />
      ) : hoursQuery.isError ? (
        <NoticeBar
          tone="danger"
          icon="alert-circle-outline"
          text={t("detail.placement.hoursError")}
          action={{ label: t("detail.placement.retry"), onPress: () => void hoursQuery.refetch() }}
        />
      ) : hoursView ? (
        <View style={styles.hours}>
          {hoursView.periodFrom && hoursView.periodTo ? <Text style={styles.placementNote}>{t("detail.placement.hoursPeriod", { from: hoursView.periodFrom, to: hoursView.periodTo })}</Text> : null}
          {hoursView.rows.length === 0 ? <Text style={styles.placementNote}>{t("detail.placement.hoursEmpty")}</Text> : null}
          {hoursView.rows.map((row) => (
            <View key={row.key} style={styles.hoursRow}>
              <Text style={styles.hoursStore} numberOfLines={1}>
                {row.label}
              </Text>
              <Text style={[styles.hoursValue, MONO, row.failed ? styles.hoursFailed : null]}>
                {row.failed ? t("detail.placement.failed") : `${t("detail.placement.work", { hours: row.workText })}${clauseSeparator(language)}${t("detail.placement.leave", { hours: row.leaveText })}`}
              </Text>
            </View>
          ))}
          {hoursView.rows.length > 1 ? (
            <View style={[styles.hoursRow, styles.hoursTotal]}>
              <Text style={[styles.hoursStore, styles.bold]}>{t("detail.placement.total")}</Text>
              <Text style={[styles.hoursValue, MONO, styles.bold]}>{`${t("detail.placement.work", { hours: hoursView.totalWorkText })}${clauseSeparator(language)}${t("detail.placement.leave", { hours: hoursView.totalLeaveText })}`}</Text>
            </View>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  card: { gap: HB_SPACING.xs, padding: HB_SPACING.md, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  name: { fontSize: 22, lineHeight: 30, fontWeight: "700", color: HB_COLORS.textPrimary },
  displayId: { fontSize: 14, fontWeight: "400", color: HB_COLORS.textSecondary },
  company: { fontSize: 15, lineHeight: 22, color: HB_COLORS.textSecondary },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  sameDate: { fontSize: 13, lineHeight: 18, color: HB_COLORS.warning },
  pdfRow: { minHeight: 52, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, borderRadius: HB_RADIUS.surface, backgroundColor: PAYSLIP_COLORS.infoBg, borderWidth: 1, borderColor: PAYSLIP_COLORS.infoBorder },
  pdfRowTitle: { fontSize: 16, fontWeight: "600", color: HB_COLORS.action },
  pdfRowHint: { flex: 1, textAlign: "right", fontSize: 13, color: HB_COLORS.action },
  pressed: { opacity: 0.7 },
  placementHead: { flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  placementTitle: { flex: 1, fontSize: 17, fontWeight: "700", color: HB_COLORS.textPrimary },
  reference: { fontSize: 13, color: HB_COLORS.textSecondary },
  placementNote: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  linkRow: { minHeight: 44, flexDirection: "row", alignItems: "center", gap: 2 },
  link: { fontSize: 15, fontWeight: "600", color: HB_COLORS.action },
  hours: { gap: 6 },
  hoursRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "center", gap: HB_SPACING.xs, minHeight: 32 },
  hoursTotal: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outline, paddingTop: 6 },
  hoursStore: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  hoursValue: { fontSize: 14, color: HB_COLORS.textPrimary },
  hoursFailed: { color: HB_COLORS.danger },
  bold: { fontWeight: "700" },
  disclaimer: { gap: 2, paddingHorizontal: HB_SPACING.xxs },
  disclaimerText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textSecondary },
  bar: { flexDirection: "row", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outline },
  barButton: { flex: 1, borderRadius: HB_RADIUS.control },
  barButtonContent: { minHeight: 52 },
  barLabel: { fontSize: 16, fontWeight: "700" },
  snackbarWrap: { top: 0, bottom: undefined },
});
