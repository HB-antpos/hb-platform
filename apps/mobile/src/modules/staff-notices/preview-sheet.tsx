import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { FilterChipRow, InfoCard, NoticeBox, SegmentTabs } from "@/modules/people";
import { PeopleSheet } from "@/modules/people/detail/people-sheet";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useStaffNoticePreview } from "./hooks";

export interface StaffNoticePreviewRecipient {
  userGuid: string;
  displayName: string;
  email: string | null;
}

/** 预览里最多能切换的员工数：看几个样本就够了，不用把 100 个人都列出来。 */
const MAX_PREVIEW_CHIPS = 8;

/**
 * 邮件 / 短信预览。内容由服务端用同一份模板渲染，密码永远是圆点。
 * 显示的是邮件的纯文本版：App 里没有 WebView（加原生依赖就不能走 OTA），版式以真实邮件为准。
 * 做成底部弹层而不是新页面：从单人发送弹层点进来再返回时，开关和确认状态都还在。
 */
export function StaffNoticePreviewSheet({
  templateKey,
  templateName,
  recipients,
  onClose,
}: {
  templateKey: string;
  templateName: string;
  recipients: readonly StaffNoticePreviewRecipient[];
  onClose: () => void;
}) {
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  const [tab, setTab] = useState<"email" | "sms">("email");
  const samples = recipients.slice(0, MAX_PREVIEW_CHIPS);
  const [userGuid, setUserGuid] = useState(samples[0]?.userGuid ?? "");
  const current = samples.find((item) => item.userGuid === userGuid) ?? samples[0];
  const previewQuery = useStaffNoticePreview(templateKey, current?.userGuid);
  const preview = previewQuery.data;

  return (
    <PeopleSheet
      title={t("preview.title")}
      subtitle={templateName}
      closeLabel={t("common:actions.close")}
      onDismiss={onClose}
      testID="staff-notice-preview-sheet"
    >
      <SegmentTabs
        items={[
          { key: "email" as const, label: t("channels.email") },
          { key: "sms" as const, label: t("channels.sms") },
        ]}
        value={tab}
        onChange={setTab}
      />
      {samples.length > 1 ? (
        <FilterChipRow
          items={samples.map((item) => ({ key: item.userGuid, label: item.displayName }))}
          value={current?.userGuid ?? ""}
          onChange={setUserGuid}
        />
      ) : null}

      {previewQuery.isError ? (
        <NoticeBox tone="red" icon="alert-circle-outline">
          {resolveLocalizedErrorMessage(previewQuery.error, { t, language, fallbackKey: "preview.loadFailed" })}
        </NoticeBox>
      ) : !preview ? (
        <View style={styles.loading}><ActivityIndicator /></View>
      ) : tab === "email" ? (
        <InfoCard>
          <View style={styles.header}>
            <Text style={styles.subject}>{preview.emailSubject}</Text>
            {current?.email ? <Text style={styles.meta}>{t("preview.to", { email: current.email })}</Text> : null}
          </View>
          <Text selectable style={styles.body}>{preview.emailText}</Text>
        </InfoCard>
      ) : preview.smsText ? (
        <>
          <View style={styles.bubble}>
            <Text selectable style={styles.bubbleText}>{preview.smsText}</Text>
          </View>
          <Text style={styles.meta}>{t("preview.smsInfo", { length: preview.smsLength, segments: preview.smsSegments })}</Text>
          {!preview.smsValid ? <NoticeBox tone="red" icon="alert-circle-outline">{t("preview.smsInvalid")}</NoticeBox> : null}
        </>
      ) : (
        <NoticeBox>{t("preview.noSms")}</NoticeBox>
      )}
      {tab === "email" && preview ? <Text style={styles.meta}>{t("preview.textHint")}</Text> : null}
    </PeopleSheet>
  );
}

const styles = StyleSheet.create({
  body: { color: HB_COLORS.textPrimary, fontSize: 13.5, lineHeight: 20, padding: 14 },
  bubble: { alignSelf: "flex-start", backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.surface, maxWidth: "92%", padding: 12 },
  bubbleText: { color: HB_COLORS.textPrimary, fontSize: 14, lineHeight: 20 },
  header: { borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, gap: 2, paddingHorizontal: 14, paddingVertical: 10 },
  loading: { alignItems: "center", paddingVertical: HB_SPACING.lg },
  meta: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
  subject: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
});
