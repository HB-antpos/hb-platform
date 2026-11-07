import { useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, Switch, Text } from "react-native-paper";
import { ActionRow, InfoCard, PEOPLE_TONES, PersonAvatar, resolvePersonName } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ACCOUNT_ACTIVATED_TEMPLATE_KEY, resolveRowOutcome, templateSupportsSms, type StaffNoticeRowOutcome } from "./logic";
import type {
  StaffNoticeChannelStatus,
  StaffNoticePreflight,
  StaffNoticePreflightRecipient,
  StaffNoticeRecipientResult,
  StaffNoticeSmsSender,
  StaffNoticeTemplate,
} from "./types";

/**
 * 模板的显示名与说明。模板名存在服务端、是中文；系统模板「账号已开通」按 App 语言显示，
 * 管理员自建的模板只有一份，原样显示。
 */
export function templateLabel(
  template: Pick<StaffNoticeTemplate, "key" | "name" | "description">,
  t: (key: string) => string,
): { name: string; description: string } {
  return template.key === ACCOUNT_ACTIVATED_TEMPLATE_KEY
    ? { name: t("templates.accountActivated.name"), description: t("templates.accountActivated.description") }
    : { name: template.name, description: template.description };
}

/** 只有一个模板时就是一行说明；有多个时点开就地展开选择，不再叠一层弹层。 */
export function TemplatePicker({
  templates,
  value,
  onChange,
}: {
  templates: readonly StaffNoticeTemplate[];
  value: StaffNoticeTemplate;
  onChange: (template: StaffNoticeTemplate) => void;
}) {
  const { t } = useAppTranslation("staffNotices");
  const [expanded, setExpanded] = useState(false);
  const canChoose = templates.length > 1;
  const current = templateLabel(value, t);
  return (
    <InfoCard>
      <ActionRow
        first
        icon="file-document-outline"
        tone="violet"
        title={current.name}
        subtitle={current.description}
        chevron={false}
        right={canChoose ? <Icon source={expanded ? "chevron-up" : "unfold-more-horizontal"} size={20} color="#98A2B3" /> : undefined}
        onPress={canChoose ? () => setExpanded((current) => !current) : undefined}
      />
      {expanded
        ? templates.filter((template) => template.key !== value.key).map((template) => (
            <ActionRow
              key={template.key}
              icon="file-document-outline"
              tone="gray"
              title={templateLabel(template, t).name}
              subtitle={templateLabel(template, t).description}
              chevron={false}
              onPress={() => {
                setExpanded(false);
                onChange(template);
              }}
            />
          ))
        : null}
    </InfoCard>
  );
}

/** 短信这一行的说明：能发时写手机号，不能发时写原因。 */
export function smsSubtitle(
  recipient: StaffNoticePreflightRecipient | null,
  preflight: StaffNoticePreflight | undefined,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  if (preflight && !preflight.smsAvailable) return t("channels.smsSkip.sms_unavailable");
  if (!recipient) return "";
  if (recipient.smsSkipReason) return t(`channels.smsSkip.${recipient.smsSkipReason}`);
  return recipient.phone ?? "";
}

/** 邮件 / 短信两行开关。邮件被模板锁定时开关置灰；短信不可用时开关置灰并写明原因。 */
export function ChannelCard({
  template,
  sendEmail,
  sendSms,
  emailSubtitle,
  smsText,
  smsAvailable,
  smsSenders,
  smsSenderId,
  onChangeEmail,
  onChangeSms,
  onChangeSmsSender,
}: {
  template: StaffNoticeTemplate;
  sendEmail: boolean;
  sendSms: boolean;
  emailSubtitle: string;
  smsText: string;
  smsAvailable: boolean;
  /** 可选发件名与当前选中项；只有多于 1 项且短信开着时才露出这一行。 */
  smsSenders?: readonly StaffNoticeSmsSender[];
  smsSenderId?: string;
  onChangeEmail: (value: boolean) => void;
  onChangeSms: (value: boolean) => void;
  onChangeSmsSender?: (id: string) => void;
}) {
  const { t } = useAppTranslation("staffNotices");
  const [senderOpen, setSenderOpen] = useState(false);
  const smsEnabled = smsAvailable && templateSupportsSms(template);
  const showSender = sendSms && smsEnabled && (smsSenders?.length ?? 0) > 1 && smsSenderId !== undefined && Boolean(onChangeSmsSender);
  return (
    <InfoCard>
      <ActionRow
        first
        icon="email-outline"
        tone="blue"
        title={t("channels.email")}
        subtitle={emailSubtitle}
        chevron={false}
        right={<Switch value={sendEmail} onValueChange={onChangeEmail} disabled={template.requiresEmail} />}
      />
      <ActionRow
        icon="message-text-outline"
        tone="green"
        title={t("channels.sms")}
        subtitle={smsText}
        chevron={false}
        right={<Switch value={sendSms && smsEnabled} onValueChange={onChangeSms} disabled={!smsEnabled} />}
      />
      {showSender ? (
        <>
          <ActionRow
            icon="tag-text-outline"
            tone="violet"
            title={t("channels.smsSender")}
            subtitle={t("channels.smsSenderHint")}
            chevron={false}
            right={(
              <View style={styles.senderValue}>
                <Text style={styles.senderText}>{smsSenderId}</Text>
                <Icon source={senderOpen ? "chevron-up" : "unfold-more-horizontal"} size={20} color="#98A2B3" />
              </View>
            )}
            onPress={() => setSenderOpen((current) => !current)}
          />
          {senderOpen
            ? (smsSenders ?? []).map((sender) => (
                <ActionRow
                  key={sender.id}
                  icon={sender.id === smsSenderId ? "check-circle" : "circle-outline"}
                  tone={sender.id === smsSenderId ? "green" : "gray"}
                  title={sender.id}
                  subtitle={sender.isDefault ? t("channels.smsSenderDefault") : undefined}
                  chevron={false}
                  onPress={() => {
                    setSenderOpen(false);
                    onChangeSmsSender?.(sender.id);
                  }}
                />
              ))
            : null}
        </>
      ) : null}
    </InfoCard>
  );
}

/** 必须勾选才能继续的确认行。 */
export function ConfirmCheckRow({
  checked,
  title,
  subtitle,
  onToggle,
}: {
  checked: boolean;
  title: string;
  subtitle?: string;
  onToggle: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked }}
      accessibilityLabel={title}
      onPress={onToggle}
      style={[styles.check, checked && styles.checkOn]}
      testID="staff-notice-confirm-check"
    >
      <Icon source={checked ? "checkbox-marked" : "checkbox-blank-outline"} size={24} color={checked ? HB_COLORS.action : HB_COLORS.textSecondary} />
      <View style={styles.checkText}>
        <Text style={styles.checkTitle}>{title}</Text>
        {subtitle ? <Text style={styles.checkSubtitle}>{subtitle}</Text> : null}
      </View>
    </Pressable>
  );
}

const OUTCOME_ICONS: Record<Exclude<StaffNoticeRowOutcome, "sending">, { icon: string; color: string }> = {
  waiting: { icon: "clock-outline", color: "#98A2B3" },
  done: { icon: "check-circle", color: PEOPLE_TONES.green.fg },
  smsFailed: { icon: "alert-circle", color: PEOPLE_TONES.amber.fg },
  failed: { icon: "close-circle", color: PEOPLE_TONES.red.fg },
};

function channelText(
  channel: "email" | "sms",
  status: StaffNoticeChannelStatus,
  t: (key: string) => string,
): string | null {
  if (status === "none") return null;
  return `${t(`channels.${channel}`)} ${t(`result.channel.${status}`)}`;
}

/** 发送进度 / 结果里的一个人：渠道状态 + 失败原因 + 右侧结果图标。 */
export function RecipientResultRow({ row, first }: { row: StaffNoticeRecipientResult; first: boolean }) {
  const { t } = useAppTranslation("staffNotices");
  const name = resolvePersonName({ fullName: row.displayName });
  const outcome = resolveRowOutcome(row);
  const pieces = [channelText("email", row.emailStatus, t), channelText("sms", row.smsStatus, t)].filter(Boolean);
  return (
    <View style={[styles.result, !first && styles.divider]}>
      <PersonAvatar initials={name.initials} seed={row.userGuid} />
      <View style={styles.resultMain}>
        <Text style={styles.resultName} numberOfLines={1}>{name.primary}</Text>
        <Text style={styles.resultMeta} numberOfLines={2}>{outcome === "waiting" ? t("result.waiting") : pieces.join(" · ")}</Text>
        {row.note ? <Text style={[styles.resultNote, outcome === "done" && styles.resultNoteMuted]}>{row.note}</Text> : null}
      </View>
      {outcome === "sending"
        ? <ActivityIndicator size={18} color={HB_COLORS.action} />
        : <Icon source={OUTCOME_ICONS[outcome].icon} size={22} color={OUTCOME_ICONS[outcome].color} />}
    </View>
  );
}

const styles = StyleSheet.create({
  check: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    minHeight: 52,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  checkOn: { borderColor: "#B2CCFF" },
  checkText: { flex: 1, minWidth: 0 },
  checkTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  checkSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  divider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  result: { alignItems: "center", flexDirection: "row", gap: 12, paddingHorizontal: 14, paddingVertical: 10 },
  resultMain: { flex: 1, minWidth: 0 },
  resultName: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  resultMeta: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  resultNote: { color: HB_COLORS.danger, fontSize: 12.5, marginTop: 2 },
  resultNoteMuted: { color: HB_COLORS.textSecondary },
  senderText: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  senderValue: { alignItems: "center", flexDirection: "row", gap: 4 },
});
