// 「发送工资单 PDF」底部弹层（设计图 ⑤⑥）：沿用 BusinessSheet。
// 状态：读取收件信息 / 默认（档案邮箱）/ 无档案邮箱 / 改收件人需勾选「我已核对收件人」/ 发送中 / 发送失败（内容保留，可重试或先下载 PDF）。
// 弹层是原生 Modal，页面的 Snackbar 会被压住：所有错误都显示在弹层内，成功才关闭并交给页面提示。
import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, IconButton, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { labelColon } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { payslipsApi } from "./api";
import { buildPayslipFileName } from "./api-core";
import { buildEmailRequest, canSubmitEmail, evaluateRecipient, PAYSLIP_EMAIL_NOTE_MAX } from "./email-rules";
import { describePayslipError } from "./errors";
import { formatPeriodFull } from "./format";
import { NoticeBar, PAYSLIP_COLORS, PayslipLoading } from "./payslip-ui";
import { downloadAndOpenPayslipPdf } from "./pdf";
import type { PayslipDetail, PayslipEmailResult, PayslipKey } from "./types";
import { usePayslipEmailTarget } from "./use-payslips";

export interface PayslipEmailSheetProps {
  visible: boolean;
  payslipKey: PayslipKey;
  detail: PayslipDetail;
  /** 有下载权限时，发送失败后给「下载 PDF」兜底。 */
  canDownload: boolean;
  onClose: () => void;
  /** 发送成功：父页面关弹层并显示「已发送至 …，已记录」。 */
  onSent: (result: PayslipEmailResult, copyRequested: boolean) => void;
}

/** 关闭即卸载：每次打开都是全新的表单状态，也会重新读取档案邮箱。 */
export function PayslipEmailSheet(props: PayslipEmailSheetProps) {
  if (!props.visible) return null;
  return <PayslipEmailSheetBody {...props} />;
}

function PayslipEmailSheetBody({ payslipKey, detail, canDownload, onClose, onSent }: PayslipEmailSheetProps) {
  const { t, language } = useAppTranslation(["payslips", "common"]);
  const targetQuery = usePayslipEmailTarget(payslipKey, true);
  const target = targetQuery.data ?? null;

  const [to, setTo] = useState("");
  const [copyToMe, setCopyToMe] = useState(true);
  const [note, setNote] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const [sending, setSending] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);
  const initialised = useRef(false);
  const mounted = useRef(true);
  useEffect(() => () => void (mounted.current = false), []);

  // 收件信息首次读到时带入档案邮箱与「抄送给我」默认值；之后用户的编辑不再被覆盖。
  useEffect(() => {
    if (!target || initialised.current) return;
    initialised.current = true;
    setTo(target.email ?? "");
    setCopyToMe(Boolean(target.myEmail));
  }, [target]);

  const evaluation = useMemo(() => evaluateRecipient(to, target), [target, to]);
  const canCopy = Boolean(target?.myEmail);
  const submittable = Boolean(target) && canSubmitEmail(evaluation, confirmed, sending || downloading);
  const fileName = target?.filename || buildPayslipFileName(detail.employeeName, detail.paymentDate);
  const busy = sending || downloading;

  const changeRecipient = (value: string) => {
    setTo(value);
    // 收件人一变，之前的核对作废。
    setConfirmed(false);
    setErrorText(null);
  };

  const send = async () => {
    if (!target || !submittable) return;
    setSending(true);
    setErrorText(null);
    const copyRequested = copyToMe && canCopy;
    try {
      const result = await payslipsApi.sendEmail(buildEmailRequest(payslipKey, { to, copyToMe: copyRequested, note, confirmed }, evaluation));
      onSent({ sentTo: result.sentTo || target.maskedEmail || evaluation.address, copiedToMe: result.copiedToMe }, copyRequested);
    } catch (error) {
      if (mounted.current) setErrorText(describePayslipError(t, language, error));
    } finally {
      if (mounted.current) setSending(false);
    }
  };

  const download = async () => {
    if (busy) return;
    setDownloading(true);
    try {
      await downloadAndOpenPayslipPdf(payslipKey, detail.employeeName, "share");
    } catch (error) {
      if (mounted.current) setErrorText(describePayslipError(t, language, error));
    } finally {
      if (mounted.current) setDownloading(false);
    }
  };

  const showIssue = evaluation.issue !== null && evaluation.address.length > 0;
  const failed = errorText !== null;

  const footer = !target ? (
    <View style={styles.footerRow}>
      <Button mode="outlined" onPress={onClose} style={styles.footerButton} contentStyle={styles.buttonContent}>
        {t("email.cancel")}
      </Button>
    </View>
  ) : failed ? (
    <View style={styles.footerRow}>
      {canDownload ? (
        <Button mode="outlined" onPress={() => void download()} loading={downloading} disabled={busy} style={styles.footerButton} contentStyle={styles.buttonContent}>
          {t("email.downloadPdf")}
        </Button>
      ) : null}
      <Button mode="contained" icon="send-outline" onPress={() => void send()} loading={sending} disabled={!submittable} style={styles.footerButton} contentStyle={styles.buttonContent}>
        {t("email.retry")}
      </Button>
    </View>
  ) : (
    <View style={styles.footerRow}>
      <Button mode="outlined" onPress={onClose} disabled={busy} style={styles.footerButton} contentStyle={styles.buttonContent}>
        {t("email.cancel")}
      </Button>
      <Button mode="contained" icon="send-outline" onPress={() => void send()} loading={sending} disabled={!submittable} style={styles.footerButton} contentStyle={styles.buttonContent}>
        {sending ? t("email.sending") : t("email.send")}
      </Button>
    </View>
  );

  return (
    <BusinessSheet
      visible
      title={t("email.title")}
      subtitle={`${detail.employeeName} · ${formatPeriodFull(detail.periodStart, detail.periodEnd) || detail.paymentDate}`}
      onDismiss={onClose}
      dismissable={!busy}
      footer={footer}
    >
      {targetQuery.isLoading ? (
        <PayslipLoading label={t("email.loadingTarget")} />
      ) : !target ? (
        <View style={styles.block}>
          <NoticeBar
            tone="danger"
            icon="alert-circle-outline"
            text={`${t("email.targetError")}${labelColon(language)}${describePayslipError(t, language, targetQuery.error)}`}
            action={{ label: t("email.targetRetry"), onPress: () => void targetQuery.refetch() }}
          />
        </View>
      ) : (
        <>
          <View style={styles.field}>
            <Text style={styles.label}>{t("email.recipient")}</Text>
            <View style={[styles.inputWrap, showIssue ? styles.inputError : null]}>
              <TextInput
                value={to}
                onChangeText={changeRecipient}
                placeholder={t("email.recipientPlaceholder")}
                placeholderTextColor={HB_COLORS.textSecondary}
                accessibilityLabel={t("email.recipient")}
                keyboardType="email-address"
                autoCapitalize="none"
                autoCorrect={false}
                editable={!busy}
                style={styles.input}
              />
              {to.length > 0 && !busy ? (
                <IconButton icon="close-circle" size={20} accessibilityLabel={t("email.clear")} onPress={() => changeRecipient("")} style={styles.clear} />
              ) : null}
            </View>
            {showIssue && evaluation.issue ? <Text style={styles.issue}>{t(`email.issue.${evaluation.issue}`)}</Text> : null}
            {!showIssue && evaluation.matchesProfile ? <Text style={styles.matches}>{t("email.matchesProfile")}</Text> : null}
            {!showIssue && !evaluation.hasProfileEmail ? <Text style={styles.hint}>{t("email.noProfile")}</Text> : null}
          </View>

          {evaluation.requiresConfirm ? (
            <View style={styles.block}>
              <NoticeBar tone="warning" icon="alert-outline" text={t("email.differsFromProfile", { masked: target.maskedEmail ?? "—" })} />
              <CheckRow checked={confirmed} label={t("email.confirmRecipient")} onToggle={() => setConfirmed((value) => !value)} disabled={busy} />
            </View>
          ) : null}

          <View>
            <CheckRow checked={copyToMe && canCopy} label={t("email.copyToMe")} onToggle={() => setCopyToMe((value) => !value)} disabled={busy || !canCopy} />
            <Text style={styles.hintIndented}>{canCopy ? t("email.copyToMeTo", { email: target.myEmail ?? "" }) : t("email.noMyEmail")}</Text>
          </View>

          <View style={styles.field}>
            <Text style={styles.label}>{t("email.note")}</Text>
            <TextInput
              value={note}
              onChangeText={setNote}
              placeholder={t("email.notePlaceholder")}
              placeholderTextColor={HB_COLORS.textSecondary}
              accessibilityLabel={t("email.note")}
              multiline
              maxLength={PAYSLIP_EMAIL_NOTE_MAX}
              editable={!busy}
              style={[styles.inputWrap, styles.noteInput]}
              textAlignVertical="top"
            />
          </View>

          <View style={styles.field}>
            <Text style={styles.label}>{t("email.attachment")}</Text>
            <View style={styles.attachment}>
              <View style={styles.attachmentIcon}>
                <MaterialCommunityIcons name="file-pdf-box" size={24} color={HB_COLORS.danger} />
              </View>
              <Text style={styles.attachmentName} numberOfLines={2}>
                {fileName}
              </Text>
            </View>
          </View>

          {failed ? (
            <NoticeBar tone="danger" icon="alert-outline" text={`${t("email.failedPrefix")}${labelColon(language)}${errorText}`} />
          ) : (
            <NoticeBar tone="info" icon="information-outline" text={t("email.notice")} />
          )}
        </>
      )}
    </BusinessSheet>
  );
}

function CheckRow({ checked, label, onToggle, disabled }: { checked: boolean; label: string; onToggle: () => void; disabled?: boolean }) {
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked, disabled: Boolean(disabled) }}
      accessibilityLabel={label}
      onPress={onToggle}
      disabled={disabled}
      style={[styles.checkRow, disabled ? styles.disabled : null]}
    >
      <MaterialCommunityIcons name={checked ? "checkbox-marked" : "checkbox-blank-outline"} size={26} color={checked ? HB_COLORS.action : HB_COLORS.textSecondary} />
      <Text style={styles.checkLabel}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  block: { gap: HB_SPACING.xs },
  field: { gap: 6 },
  label: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
  inputWrap: { borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.white, flexDirection: "row", alignItems: "center" },
  inputError: { borderColor: HB_COLORS.danger },
  input: { flex: 1, minHeight: 48, paddingHorizontal: HB_SPACING.sm, fontSize: 16, color: HB_COLORS.textPrimary },
  clear: { margin: 0 },
  noteInput: { minHeight: 80, padding: HB_SPACING.sm, fontSize: 16, color: HB_COLORS.textPrimary, alignItems: "flex-start" },
  issue: { fontSize: 13, lineHeight: 18, color: HB_COLORS.danger },
  matches: { fontSize: 13, lineHeight: 18, color: HB_COLORS.success },
  hint: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  hintIndented: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary, marginLeft: 34, marginTop: -6 },
  checkRow: { minHeight: 44, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  checkLabel: { flex: 1, fontSize: 16, lineHeight: 22, color: HB_COLORS.textPrimary },
  disabled: { opacity: 0.5 },
  attachment: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, backgroundColor: HB_COLORS.surface },
  attachmentIcon: { width: 40, height: 40, borderRadius: HB_RADIUS.control, alignItems: "center", justifyContent: "center", backgroundColor: PAYSLIP_COLORS.dangerBg },
  attachmentName: { flex: 1, fontSize: 15, lineHeight: 20, color: HB_COLORS.textPrimary },
  footerRow: { flexDirection: "row", gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  footerButton: { flex: 1 },
  buttonContent: { minHeight: 48 },
});
