import { useEffect, useMemo, useRef, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Button, Dialog, Icon, Portal, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  FilterChipRow,
  InfoCard,
  NoticeBox,
  PeopleTag,
  PersonAvatar,
  PersonRow,
  resolvePersonName,
  type PeopleTone,
} from "@/modules/people";
import { HeaderIconButton, PeopleScreenFrame } from "@/modules/people/people-frame";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useStaffNoticePreflight, useStaffNoticeSend, useStaffNoticeTemplates } from "./hooks";
import {
  countRecipients,
  decodeUserGuidsParam,
  filterRecipients,
  formatAud,
  formatNoticeTime,
  isRecipientSelectable,
  primaryReason,
  reconcileSelection,
  resolveDefaultSelection,
  resolveDefaultTemplate,
  summarizeSelection,
  templateSupportsSms,
  type StaffNoticeRecipientFilter,
  type StaffNoticeSendSummary,
} from "./logic";
import { StaffNoticePreviewSheet } from "./preview-sheet";
import { StaffNoticeProgressSheet } from "./progress-sheet";
import type { StaffNoticePreflight, StaffNoticePreflightRecipient } from "./types";
import { ChannelCard, ConfirmCheckRow, TemplatePicker, templateLabel } from "./ui";

/**
 * 批量发送的核对页（`/users/notices/send?userGuids=`）：人员列表选人进来。
 * 可发送的默认勾选；已登录过、邮箱存疑的默认不勾；不可发送的置灰且原因写在行里。
 * 最终能发给谁由服务端在发送时重新判断，这里的勾选只是操作人的意图。
 */
export function StaffNoticeReviewScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ userGuids?: string | string[] }>();
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  const access = useAuthStore((state) => state.access);
  // 客户端不认别名，必须写「全局码 OR 店员码」。
  const canResetPasswords = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.ResetPassword)
    || access.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);

  const userGuids = useMemo(() => decodeUserGuidsParam(params.userGuids), [params.userGuids]);
  const templatesQuery = useStaffNoticeTemplates(canResetPasswords);
  const templates = useMemo(() => templatesQuery.data ?? [], [templatesQuery.data]);
  const [templateKey, setTemplateKey] = useState<string | null>(null);
  const template = templates.find((item) => item.key === templateKey) ?? resolveDefaultTemplate(templates);

  const [sendEmail, setSendEmail] = useState(true);
  const [sendSms, setSendSms] = useState(true);
  const effectiveEmail = sendEmail || Boolean(template?.requiresEmail);
  const effectiveSms = sendSms && templateSupportsSms(template);
  const request = useMemo(
    () => (template && userGuids.length
      ? { templateKey: template.key, userGuids, sendEmail: effectiveEmail, sendSms: effectiveSms }
      : null),
    [effectiveEmail, effectiveSms, template, userGuids],
  );
  const preflightQuery = useStaffNoticePreflight(request);
  const preflight = preflightQuery.data;
  const recipients = useMemo(() => preflight?.recipients ?? [], [preflight]);

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const selectionReadyRef = useRef(false);
  const [filter, setFilter] = useState<StaffNoticeRecipientFilter>("all");
  const [confirmVisible, setConfirmVisible] = useState(false);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [batchId, setBatchId] = useState<string | null>(null);
  const [sendError, setSendError] = useState<string | null>(null);
  const sendMutation = useStaffNoticeSend();

  useEffect(() => {
    if (!preflight) return;
    // 第一次按默认规则勾选；之后重新预检只剔除不能发的人，不覆盖用户手动的勾选。
    // 必须在这里先读 ref：setState 的更新函数是延后执行的，放进去读到的已经是下面改过的值。
    const firstTime = !selectionReadyRef.current;
    selectionReadyRef.current = true;
    setSelected((current) => (firstTime ? resolveDefaultSelection(preflight.recipients) : reconcileSelection(current, preflight.recipients)));
  }, [preflight]);

  const counts = useMemo(() => countRecipients(recipients), [recipients]);
  const visible = useMemo(() => filterRecipients(recipients, filter), [filter, recipients]);
  const summary = useMemo(
    () => summarizeSelection({ recipients, smsUnitCostAud: preflight?.smsUnitCostAud ?? 0 }, selected, Boolean(template?.issuesPassword)),
    [preflight?.smsUnitCostAud, recipients, selected, template?.issuesPassword],
  );

  const toggle = (userGuid: string) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(userGuid)) next.delete(userGuid);
      else next.add(userGuid);
      return next;
    });
  };

  const handleSend = async (confirmReissue: boolean) => {
    if (!request) return;
    setSendError(null);
    try {
      const result = await sendMutation.mutateAsync({ ...request, userGuids: [...selected], confirmReissue });
      setConfirmVisible(false);
      setBatchId(result.batchId);
    } catch (error) {
      // 确认期间情况变了（账号被停用、员工刚登录）：关掉弹窗按最新结果重新核对，不会建空批次。
      setConfirmVisible(false);
      setSendError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "sheet.sendFailed" }));
      void preflightQuery.refetch();
    }
  };

  const chrome = useMemo(() => ({ tabBar: null, onBack: () => router.back() }), [router]);
  const title = template?.issuesPassword ? t("review.titleActivation") : t("review.title");

  if (!canResetPasswords || !userGuids.length) {
    return (
      <PeopleScreenFrame title={title} chrome={chrome}>
        <EmptyState
          title={canResetPasswords ? t("review.emptyTitle") : t("review.noAccessTitle")}
          description={canResetPasswords ? t("review.emptyDescription") : t("review.noAccessDescription")}
        />
      </PeopleScreenFrame>
    );
  }

  const previewRecipients = recipients
    .filter((item) => selected.has(item.userGuid))
    .map((item) => ({ userGuid: item.userGuid, displayName: item.displayName, email: item.email }));
  const hint = summary.smsMessages > 0
    ? t("review.summaryWithSms", { emails: summary.emails, sms: summary.smsMessages, cost: formatAud(summary.costAud) })
    : t("review.summaryEmailOnly", { emails: summary.emails });
  const canSend = summary.people > 0 && !preflightQuery.isFetching && !sendMutation.isPending;

  return (
    <PeopleScreenFrame
      title={title}
      subtitle={t("review.subtitle", { count: userGuids.length })}
      chrome={chrome}
      right={previewRecipients.length && template
        ? <HeaderIconButton icon="eye-outline" label={t("sheet.preview")} onPress={() => setPreviewOpen(true)} />
        : null}
    >
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {templatesQuery.isError ? (
          <NoticeBox tone="red" icon="alert-circle-outline">
            {resolveLocalizedErrorMessage(templatesQuery.error, { t, language, fallbackKey: "sheet.loadFailed" })}
          </NoticeBox>
        ) : template ? (
          <>
            <TemplatePicker
              templates={templates}
              value={template}
              onChange={(next) => {
                setTemplateKey(next.key);
                // 换模板后「默认勾谁」可能不同（比如普通模板不算已登录过），按新结果重新勾选。
                selectionReadyRef.current = false;
              }}
            />
            <ChannelCard
              template={template}
              sendEmail={effectiveEmail}
              sendSms={sendSms}
              emailSubtitle={template.requiresEmail ? t("channels.emailRequired") : ""}
              smsText={preflight?.smsAvailable === false ? t("channels.smsSkip.sms_unavailable") : t("channels.smsBatchHint")}
              smsAvailable={preflight?.smsAvailable ?? true}
              onChangeEmail={setSendEmail}
              onChangeSms={setSendSms}
            />
          </>
        ) : !templatesQuery.isLoading ? (
          <NoticeBox tone="amber" icon="alert-outline">{t("sheet.noTemplates")}</NoticeBox>
        ) : null}

        {sendError ? <NoticeBox tone="red" icon="alert-circle-outline">{sendError}</NoticeBox> : null}

        {preflightQuery.isError && !preflight ? (
          <NoticeBox tone="red" icon="alert-circle-outline">
            {resolveLocalizedErrorMessage(preflightQuery.error, { t, language, fallbackKey: "sheet.checkFailed" })}
          </NoticeBox>
        ) : !preflight ? (
          <View style={styles.loading}><ActivityIndicator /></View>
        ) : (
          <>
            <FilterChipRow
              items={(["all", "ready", "confirm", "blocked"] as const).map((key) => ({ key, label: t(`review.filters.${key}`), count: counts[key] }))}
              value={filter}
              onChange={setFilter}
            />
            {visible.length ? (
              <InfoCard>
                {visible.map((recipient, index) => (
                  <RecipientRow
                    key={recipient.userGuid}
                    recipient={recipient}
                    checked={selected.has(recipient.userGuid)}
                    smsOn={effectiveSms}
                    first={index === 0}
                    onToggle={() => toggle(recipient.userGuid)}
                  />
                ))}
              </InfoCard>
            ) : (
              <Text style={styles.emptyFilter}>{t("review.emptyFilter")}</Text>
            )}
          </>
        )}
      </ScrollView>

      <View style={styles.bottomBar}>
        <View style={styles.bottomSummary}>
          <Text style={styles.bottomTitle} numberOfLines={1}>{t("review.summary", { count: summary.people })}</Text>
          <Text style={styles.bottomHint} numberOfLines={2}>{hint}</Text>
        </View>
        <Button mode="contained" icon="send-outline" onPress={() => setConfirmVisible(true)} disabled={!canSend}>
          {t("review.send", { count: summary.people })}
        </Button>
      </View>

      {confirmVisible && template && preflight ? (
        <SendConfirmDialog
          templateName={templateLabel(template, t).name}
          summary={summary}
          skipped={userGuids.length - summary.people}
          preflight={preflight}
          requiresPassword={template.issuesPassword}
          pending={sendMutation.isPending}
          onCancel={() => setConfirmVisible(false)}
          onConfirm={(confirmReissue) => void handleSend(confirmReissue)}
        />
      ) : null}
      {previewOpen && template ? (
        <StaffNoticePreviewSheet
          templateKey={template.key}
          templateName={templateLabel(template, t).name}
          recipients={previewRecipients}
          onClose={() => setPreviewOpen(false)}
        />
      ) : null}
      {batchId ? (
        <StaffNoticeProgressSheet batchId={batchId} templateName={template ? templateLabel(template, t).name : ""} onClose={() => router.back()} />
      ) : null}
    </PeopleScreenFrame>
  );
}

function RecipientRow({
  recipient,
  checked,
  smsOn,
  first,
  onToggle,
}: {
  recipient: StaffNoticePreflightRecipient;
  checked: boolean;
  smsOn: boolean;
  first: boolean;
  onToggle: () => void;
}) {
  const { t } = useAppTranslation("staffNotices");
  const selectable = isRecipientSelectable(recipient);
  // 越权的人后端不返回任何资料（只有 GUID），行上只写「无权限」。
  const name = resolvePersonName({ fullName: recipient.displayName, username: recipient.loginName });
  const display = name.primary || t("review.unknownStaff");
  const reason = primaryReason(recipient);
  const tag = resolveRowTag(recipient, smsOn, t);

  return (
    <View style={!selectable && styles.rowDisabled}>
      <PersonRow
        first={first}
        avatar={(
          <View style={styles.leading}>
            {selectable ? (
              <Icon
                source={checked ? "checkbox-marked" : "checkbox-blank-outline"}
                size={24}
                color={checked ? HB_COLORS.action : HB_COLORS.textSecondary}
              />
            ) : (
              <Icon source="checkbox-blank-off-outline" size={24} color="#98A2B3" />
            )}
            <PersonAvatar initials={name.initials} seed={recipient.userGuid} />
          </View>
        )}
        name={display}
        meta={recipient.email ?? (reason === "forbidden" || reason === "not_found" ? "" : t("channels.emailNotSet"))}
        tags={(
          <>
            <PeopleTag label={tag.label} tone={tag.tone} />
            {tag.hint ? <Text style={styles.tagHint} numberOfLines={1}>{tag.hint}</Text> : null}
          </>
        )}
        onPress={selectable ? onToggle : undefined}
        accessibilityLabel={t("review.selectStaff", { name: display })}
      />
    </View>
  );
}

function resolveRowTag(
  recipient: StaffNoticePreflightRecipient,
  smsOn: boolean,
  t: (key: string, options?: Record<string, unknown>) => string,
): { label: string; tone: PeopleTone; hint?: string } {
  const reason = primaryReason(recipient);
  if (recipient.status === "blocked") {
    return { label: t(`reasonShort.${reason ?? "not_found"}`), tone: "red" };
  }
  if (recipient.status === "confirm") {
    if (reason === "already_logged_in") {
      const time = formatNoticeTime(recipient.lastLoginAtUtc, { today: t("time.today"), yesterday: t("time.yesterday") });
      return { label: t("reasonShort.already_logged_in"), tone: "amber", hint: t("review.loggedInHint", { time }) };
    }
    return { label: t("reasonShort.suspicious_email"), tone: "amber", hint: t("review.suspiciousHint") };
  }
  if (smsOn && !recipient.willSendSms) {
    const hint = recipient.smsSkipReason ? t(`channels.smsSkip.${recipient.smsSkipReason}`) : undefined;
    return { label: t("review.emailOnly"), tone: "green", hint };
  }
  return { label: t("review.filters.ready"), tone: "green", hint: smsOn ? recipient.phone ?? undefined : undefined };
}

/** 确认弹窗：只有选中的人里有登录过的才出现警告和必勾确认框，并点名是谁。 */
function SendConfirmDialog({
  templateName,
  summary,
  skipped,
  preflight,
  requiresPassword,
  pending,
  onCancel,
  onConfirm,
}: {
  templateName: string;
  summary: StaffNoticeSendSummary;
  skipped: number;
  preflight: StaffNoticePreflight;
  requiresPassword: boolean;
  pending: boolean;
  onCancel: () => void;
  onConfirm: (confirmReissue: boolean) => void;
}) {
  const { t } = useAppTranslation("staffNotices");
  const [checked, setChecked] = useState(false);
  const needsCheck = summary.reissued.length > 0;
  const names = summary.reissued.slice(0, 3).map((item) => item.displayName).join(t("confirmDialog.nameSeparator"));

  return (
    <Portal>
      <Dialog visible dismissable={!pending} onDismiss={onCancel} style={styles.dialog} testID="staff-notice-confirm-dialog">
        <Dialog.Title style={styles.dialogTitle}>{t("confirmDialog.title", { name: templateName })}</Dialog.Title>
        <Dialog.Content style={styles.dialogContent}>
          <Text style={styles.dialogLine}>
            {skipped > 0
              ? t("confirmDialog.recipientsSkipped", { count: summary.people, skipped })
              : t("confirmDialog.recipients", { count: summary.people })}
          </Text>
          <Text style={styles.dialogLine}>
            {requiresPassword ? t("confirmDialog.emailsPassword", { count: summary.emails }) : t("confirmDialog.emails", { count: summary.emails })}
          </Text>
          {summary.smsMessages > 0 ? (
            <Text style={styles.dialogLine}>
              {t("confirmDialog.sms", {
                count: summary.smsMessages,
                cost: formatAud(summary.costAud),
                used: preflight.smsUsedLast24h,
                limit: preflight.smsDailyLimit,
              })}
            </Text>
          ) : null}
          {needsCheck ? (
            <>
              <NoticeBox tone="amber" icon="alert-outline">
                {t("confirmDialog.reissueWarning", { count: summary.reissued.length, names })}
              </NoticeBox>
              <ConfirmCheckRow checked={checked} title={t("confirmCheck.reissue")} onToggle={() => setChecked((current) => !current)} />
            </>
          ) : null}
        </Dialog.Content>
        <Dialog.Actions style={styles.dialogActions}>
          <Button mode="text" onPress={onCancel} disabled={pending}>{t("confirmDialog.back")}</Button>
          <Button
            mode="contained"
            onPress={() => onConfirm(needsCheck && checked)}
            loading={pending}
            disabled={pending || (needsCheck && !checked)}
            style={styles.dialogButton}
          >
            {t("confirmDialog.confirm")}
          </Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

const styles = StyleSheet.create({
  bottomBar: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  bottomHint: { color: HB_COLORS.textSecondary, fontSize: 12, marginTop: 1 },
  bottomSummary: { flex: 1, minWidth: 0 },
  bottomTitle: { color: HB_COLORS.textPrimary, fontSize: 12.5, fontWeight: "800" },
  content: { gap: HB_SPACING.sm, padding: HB_SPACING.md, paddingBottom: HB_SPACING.lg },
  dialog: { alignSelf: "center", backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.sheet, maxWidth: 420, width: "90%" },
  dialogActions: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  dialogButton: { borderRadius: HB_RADIUS.control, minWidth: 96 },
  dialogContent: { gap: HB_SPACING.xs },
  dialogLine: { color: HB_COLORS.textSecondary, fontSize: 14, lineHeight: 21 },
  dialogTitle: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "800" },
  emptyFilter: { color: HB_COLORS.textSecondary, fontSize: 13, paddingVertical: HB_SPACING.md, textAlign: "center" },
  leading: { alignItems: "center", flexDirection: "row", gap: 10 },
  loading: { alignItems: "center", paddingVertical: HB_SPACING.lg },
  rowDisabled: { opacity: 0.55 },
  tagHint: { color: HB_COLORS.textSecondary, flexShrink: 1, fontSize: 12, lineHeight: 20 },
});
