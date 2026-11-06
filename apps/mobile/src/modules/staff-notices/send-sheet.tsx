import { useMemo, useState } from "react";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { ActionRow, InfoCard, NoticeBox } from "@/modules/people";
import { PeopleSheet, SHEET_BUTTON_STYLE, SheetActions } from "@/modules/people/detail/people-sheet";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useStaffNoticePreflight, useStaffNoticeSend, useStaffNoticeTemplates } from "./hooks";
import { formatAud, formatNoticeTime, primaryReason, resolveDefaultTemplate, templateSupportsSms } from "./logic";
import { StaffNoticePreviewSheet } from "./preview-sheet";
import { StaffNoticeProgressSheet } from "./progress-sheet";
import type { StaffNoticePreflightRecipient, StaffNoticeTemplate } from "./types";
import { ChannelCard, ConfirmCheckRow, TemplatePicker, smsSubtitle, templateLabel } from "./ui";

export interface StaffNoticeSendSheetProps {
  userGuid: string;
  staffName: string;
  loginName: string;
  onDismiss: () => void;
  /** 缺邮箱时的「补充员工邮箱」。没有编辑权限时不传，这一行就不出现。 */
  onEditEmail?: () => void;
}

/**
 * 给一个员工发通知的底部弹层。打开即预检，三种状态都由服务端的预检结果决定：
 * 可发送 / 需确认（必须勾选才能发）/ 不可发送（发送按钮禁用并写明原因）。
 * 调用方用条件渲染控制显示：关闭即卸载，下次打开是干净状态，也一定会重新预检。
 */
export function StaffNoticeSendSheet({ userGuid, staffName, loginName, onDismiss, onEditEmail }: StaffNoticeSendSheetProps) {
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  const { fontScale, width } = useWindowDimensions();
  const stacked = fontScale > 1.2 || width < 380;

  const templatesQuery = useStaffNoticeTemplates();
  const templates = useMemo(() => templatesQuery.data ?? [], [templatesQuery.data]);
  const [templateKey, setTemplateKey] = useState<string | null>(null);
  const template = templates.find((item) => item.key === templateKey) ?? resolveDefaultTemplate(templates);

  const [sendEmail, setSendEmail] = useState(true);
  const [sendSms, setSendSms] = useState(true);
  const [confirmed, setConfirmed] = useState(false);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [batchId, setBatchId] = useState<string | null>(null);
  const [sendError, setSendError] = useState<string | null>(null);

  const effectiveEmail = sendEmail || Boolean(template?.requiresEmail);
  const effectiveSms = sendSms && templateSupportsSms(template);
  const request = useMemo(
    () => (template
      ? { templateKey: template.key, userGuids: [userGuid], sendEmail: effectiveEmail, sendSms: effectiveSms }
      : null),
    [effectiveEmail, effectiveSms, template, userGuid],
  );
  const preflightQuery = useStaffNoticePreflight(request);
  const preflight = preflightQuery.data;
  const recipient = preflight?.recipients[0] ?? null;
  const sendMutation = useStaffNoticeSend();

  const handleTemplateChange = (next: StaffNoticeTemplate) => {
    setTemplateKey(next.key);
    setConfirmed(false);
    setSendError(null);
  };

  const handleSend = async () => {
    if (!request) return;
    setSendError(null);
    try {
      const result = await sendMutation.mutateAsync({ ...request, confirmReissue: confirmed });
      setBatchId(result.batchId);
    } catch (error) {
      setSendError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "sheet.sendFailed" }));
      // 预检之后情况可能变了（员工刚登录、已在别的批次里），按最新结果重新显示。
      void preflightQuery.refetch();
    }
  };

  if (batchId) {
    return <StaffNoticeProgressSheet batchId={batchId} templateName={template ? templateLabel(template, t).name : ""} onClose={onDismiss} />;
  }

  if (previewOpen && template) {
    return (
      <StaffNoticePreviewSheet
        templateKey={template.key}
        templateName={templateLabel(template, t).name}
        recipients={[{ userGuid, displayName: staffName, email: recipient?.email ?? null }]}
        onClose={() => setPreviewOpen(false)}
      />
    );
  }

  const checking = preflightQuery.isFetching || templatesQuery.isLoading;
  const status = recipient?.status;
  const blockedByEmail = status === "blocked" && recipient?.reasons.includes("no_email");
  const canSend = Boolean(recipient)
    && status !== "blocked"
    && !checking
    && !sendMutation.isPending
    && (status !== "confirm" || confirmed);

  return (
    <PeopleSheet
      title={t("sheet.title")}
      subtitle={`${staffName} · @${loginName}`}
      closeLabel={t("common:actions.close")}
      onDismiss={onDismiss}
      dismissable={!sendMutation.isPending}
      testID="staff-notice-send-sheet"
      footer={(
        <SheetActions stacked={stacked}>
          <Button mode="outlined" onPress={onDismiss} disabled={sendMutation.isPending} style={SHEET_BUTTON_STYLE}>
            {t("sheet.cancel")}
          </Button>
          <Button
            mode="contained"
            icon="send-outline"
            onPress={() => void handleSend()}
            loading={sendMutation.isPending}
            disabled={!canSend}
            style={SHEET_BUTTON_STYLE}
          >
            {t("sheet.send")}
          </Button>
        </SheetActions>
      )}
    >
      {templatesQuery.isError ? (
        <NoticeBox tone="red" icon="alert-circle-outline">
          {resolveLocalizedErrorMessage(templatesQuery.error, { t, language, fallbackKey: "sheet.loadFailed" })}
        </NoticeBox>
      ) : !template ? (
        templatesQuery.isLoading
          ? <View style={styles.loading}><ActivityIndicator /></View>
          : <NoticeBox tone="amber" icon="alert-outline">{t("sheet.noTemplates")}</NoticeBox>
      ) : (
        <>
          <TemplatePicker templates={templates} value={template} onChange={handleTemplateChange} />
          <ChannelCard
            template={template}
            sendEmail={effectiveEmail}
            sendSms={sendSms}
            emailSubtitle={recipient?.email ?? (recipient ? t("channels.emailNotSet") : "")}
            smsText={smsSubtitle(recipient, preflight, t)}
            smsAvailable={preflight?.smsAvailable ?? true}
            onChangeEmail={setSendEmail}
            onChangeSms={setSendSms}
          />

          {preflightQuery.isError && !recipient ? (
            <NoticeBox tone="red" icon="alert-circle-outline">
              {resolveLocalizedErrorMessage(preflightQuery.error, { t, language, fallbackKey: "sheet.checkFailed" })}
            </NoticeBox>
          ) : !recipient ? (
            <Text style={styles.checking}>{t("sheet.checking")}</Text>
          ) : (
            <RecipientNotice recipient={recipient} template={template} unitCost={preflight?.smsUnitCostAud ?? 0} />
          )}

          {status === "confirm" && recipient ? (
            <ConfirmCheckRow
              checked={confirmed}
              title={t(recipient.reasons.includes("already_logged_in") ? "confirmCheck.reissue" : "confirmCheck.email")}
              subtitle={t("confirmCheck.required")}
              onToggle={() => setConfirmed((current) => !current)}
            />
          ) : null}

          {sendError ? <NoticeBox tone="red" icon="alert-circle-outline">{sendError}</NoticeBox> : null}

          {blockedByEmail && onEditEmail ? (
            <InfoCard>
              <ActionRow
                first
                icon="email-edit-outline"
                tone="amber"
                title={t("sheet.addEmail")}
                subtitle={t("sheet.addEmailHint")}
                onPress={onEditEmail}
              />
            </InfoCard>
          ) : status !== "blocked" ? (
            <InfoCard>
              <ActionRow
                first
                icon="eye-outline"
                tone="gray"
                title={t("sheet.preview")}
                subtitle={template.issuesPassword ? t("sheet.previewHint") : undefined}
                onPress={() => setPreviewOpen(true)}
              />
            </InfoCard>
          ) : null}
        </>
      )}
    </PeopleSheet>
  );
}

/** 预检结论：绿 = 可发送，琥珀 = 要确认的后果，红 = 发不了的原因。 */
function RecipientNotice({
  recipient,
  template,
  unitCost,
}: {
  recipient: StaffNoticePreflightRecipient;
  template: StaffNoticeTemplate;
  unitCost: number;
}) {
  const { t } = useAppTranslation("staffNotices");
  const reason = primaryReason(recipient);

  if (recipient.status === "blocked") {
    return <NoticeBox tone="red" icon="alert-circle-outline">{t(`blocked.${reason ?? "not_found"}`)}</NoticeBox>;
  }

  if (recipient.status === "confirm") {
    const lines: string[] = [];
    if (recipient.reasons.includes("already_logged_in")) {
      const time = formatNoticeTime(recipient.lastLoginAtUtc, { today: t("time.today"), yesterday: t("time.yesterday") });
      lines.push(t("confirm.alreadyLoggedIn", { time }));
    }
    if (recipient.reasons.includes("suspicious_email")) {
      lines.push(t("confirm.suspiciousEmail", { email: recipient.email ?? "" }));
    }
    return <NoticeBox tone="amber" icon="alert-outline">{lines.join("\n")}</NoticeBox>;
  }

  const cost = formatAud(recipient.smsSegments * unitCost);
  const key = template.issuesPassword
    ? (recipient.willSendSms ? "ready.passwordSms" : "ready.password")
    : (recipient.willSendSms ? "ready.generalSms" : "ready.general");
  return <NoticeBox tone="green" icon="check-circle-outline">{t(key, { cost })}</NoticeBox>;
}

const styles = StyleSheet.create({
  checking: { color: HB_COLORS.textSecondary, fontSize: 12.5, textAlign: "center" },
  loading: { alignItems: "center", paddingVertical: HB_SPACING.lg },
});
