import { useCallback, useEffect, useRef, useState } from "react";
import { StyleSheet, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Text } from "react-native-paper";
import { InfoCard, KvRow, NoticeBox, PeopleTag, SectionTitle } from "@/modules/people";
import { minorEmploymentStaffApi, minorStaffQueryKey } from "@/modules/minor-employment/staff-api";
import {
  MINOR_STAGE_TONES,
  MINOR_VERSION_CONFLICT_CODE,
  formatSydneyShort,
  hasDeliveryFailure,
  isForbiddenError,
  isResendStage,
  readErrorCode,
  resolveRelationshipOption,
  type GuardianLinkSendResult,
  type MinorStaffComplianceDetail,
  type MinorStaffComplianceItem,
} from "@/modules/minor-employment/staff-contract";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { detailStyles } from "./detail-ui";
import { MinorGuardianSheet, resolveMinorGuardianError } from "./minor-guardian-sheet";
import { ConfirmDialog } from "./people-sheet";

/**
 * 人员详情「未成年用工合规」一节（店长版、管理员版共用）。
 *
 * - 只对未满 18 岁的员工显示：后端 item 为 null（成年 / 没有出生日期 / 没有主门店）或 403 时整节不显示。
 * - 读取需要 Attendance.Schedule.ViewStore；发送 / 作废需要 Attendance.Schedule.EditManagedStore（后端再按门店范围裁决）。
 * - 员工本人不再填写或邀请监护人：由店长 / HR 在这里给家长发英文邮件 / 短信，家长在 Web 补全资料并签字。
 */
export function MinorGuardianSection({
  userGuid,
  onMessage,
}: {
  userGuid: string;
  /** 结果提示走详情页的 Snackbar。 */
  onMessage: (message: string) => void;
}) {
  const { t, language } = useAppTranslation(["peopleDetail", "common"]);
  const access = useAuthStore((state) => state.access);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const actorKey = useAuthStore((state) => state.user?.userGUID || state.user?.userGuid || "anonymous");
  const queryClient = useQueryClient();

  // iOS 审核模式不请求真实业务接口；没有排班查看权限时不发请求（避免无谓的 403）。
  const canView = sessionKind !== "iosReview" && access.hasPermission(PERMISSIONS.Attendance.ScheduleViewStore);
  const canEdit = access.hasPermission(PERMISSIONS.Attendance.ScheduleEditManagedStore);
  const queryKey = minorStaffQueryKey(actorKey, userGuid);
  const query = useQuery({
    queryKey,
    queryFn: ({ signal }) => minorEmploymentStaffApi.getStaff(userGuid, signal),
    enabled: Boolean(userGuid) && canView,
    staleTime: 30_000,
    retry: (failureCount, error) => !isForbiddenError(error) && failureCount < 1,
  });

  /**
   * 提示条要在弹层 / 确认框关闭之后再挂：同一次渲染里关弹层又挂 Snackbar，Paper 的出场动画会被打断，
   * 提示条一直不可见也不会自动消失，之后换文案也不再弹出。所以先清空（卸载旧提示条），等关闭动画结束再挂新的。
   */
  const messageTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (messageTimer.current) clearTimeout(messageTimer.current); }, []);
  const notify = useCallback((message: string) => {
    if (messageTimer.current) clearTimeout(messageTimer.current);
    onMessage("");
    messageTimer.current = setTimeout(() => onMessage(message), 350);
  }, [onMessage]);

  const [sheetOpen, setSheetOpen] = useState(false);
  const [revokeOpen, setRevokeOpen] = useState(false);
  /** 最近一次发送有渠道失败时保留结果（含可复制的链接），直到下次发送 / 作废或离开页面。 */
  const [delivery, setDelivery] = useState<GuardianLinkSendResult | null>(null);

  const applyItem = useCallback((item: MinorStaffComplianceItem) => {
    queryClient.setQueryData<MinorStaffComplianceDetail>(queryKey, (current) =>
      current && item.userGUID ? { ...current, item } : current);
    void queryClient.invalidateQueries({ queryKey });
  }, [queryClient, queryKey]);
  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey });
  }, [queryClient, queryKey]);

  const handleSent = useCallback((result: GuardianLinkSendResult) => {
    applyItem(result.item);
    setDelivery(hasDeliveryFailure(result) ? result : null);
    setSheetOpen(false);
    const anySent = result.emailSent || result.smsSent;
    notify(
      !anySent
        ? t("peopleDetail:minor.messages.noneSent")
        : hasDeliveryFailure(result)
          ? t("peopleDetail:minor.messages.partial")
          : result.emailSent && result.smsSent
            ? t("peopleDetail:minor.messages.sentBoth")
            : result.smsSent
              ? t("peopleDetail:minor.messages.sentSms")
              : t("peopleDetail:minor.messages.sentEmail"),
    );
  }, [applyItem, notify, t]);

  const revoke = useMutation({
    mutationFn: (target: MinorStaffComplianceItem) => minorEmploymentStaffApi.revokeGuardianLink(userGuid, target),
    onSuccess: (item) => {
      applyItem(item);
      setDelivery(null);
      setRevokeOpen(false);
      notify(t("peopleDetail:minor.messages.revoked"));
    },
    onError: (error) => {
      // 先关确认框再提示，Snackbar 才不会被还没卸载的对话框挡住。
      setRevokeOpen(false);
      if (readErrorCode(error) === MINOR_VERSION_CONFLICT_CODE) refresh();
      notify(resolveMinorGuardianError(error, t, language, "peopleDetail:minor.errors.revokeFailed"));
    },
  });

  const handleCopy = useCallback(async (url: string) => {
    try {
      await Clipboard.setStringAsync(url);
      notify(t("peopleDetail:minor.messages.linkCopied"));
    } catch {
      notify(t("peopleDetail:minor.messages.copyFailed"));
    }
  }, [notify, t]);

  const detail = query.data;
  const item = detail?.item;
  if (!canView || !detail || !item) return null;

  const notFilled = <Text style={detailStyles.rowMuted}>{t("peopleDetail:minor.notFilled")}</Text>;
  const relationOption = resolveRelationshipOption(item.guardianRelationship);
  const relationLabel = relationOption
    ? t(`peopleDetail:minor.relationships.${relationOption}`)
    : item.guardianRelationship;
  const guardianText = item.guardianName
    ? relationLabel
      ? t("peopleDetail:minor.guardianWithRelation", { name: item.guardianName, relation: relationLabel })
      : item.guardianName
    : null;
  const contactText = [item.maskedGuardianEmail, item.maskedGuardianPhone].filter(Boolean).join(" · ");
  const linkStage = item.stage === "awaiting_guardian" || item.stage === "link_expired";
  const showSent = linkStage && (item.lastSentEmail !== null || item.lastSentSms !== null);
  const sentAt = formatSydneyShort(item.lastSentAtUtc, language);
  const expiresAt = formatSydneyShort(item.linkExpiresAtUtc, language);
  const signedAt = formatSydneyShort(item.guardianSignedAtUtc, language);
  const showSend = canEdit && item.canSend;
  const showRevoke = canEdit && item.canRevoke;
  const deliveryUrl = delivery?.signingUrl;

  const channelTag = (channel: "email" | "sms", state: boolean | null) =>
    state === null ? null : (
      <PeopleTag
        key={channel}
        icon={state ? "check" : "close"}
        label={t(`peopleDetail:minor.channels.${channel}`)}
        tone={state ? "green" : "red"}
      />
    );

  // 失败原因来自后端 / 短信服务商，通常是中文；英文界面不拼原因，避免中英混排。
  const failureReason = (reason: string | undefined) => {
    if (!reason || !language.toLowerCase().startsWith("zh")) return "";
    return t("peopleDetail:minor.partial.reasonPrefix", { reason: reason.replace(/[。.]+$/, "") });
  };
  const partialText = delivery
    ? [
        delivery.emailRequested && !delivery.emailSent
          ? t("peopleDetail:minor.partial.emailFailed", { reason: failureReason(delivery.emailError) })
          : null,
        delivery.smsRequested && !delivery.smsSent
          ? t("peopleDetail:minor.partial.smsFailed", { reason: failureReason(delivery.smsError) })
          : null,
        delivery.emailSent ? t("peopleDetail:minor.partial.emailDelivered") : null,
        delivery.smsSent ? t("peopleDetail:minor.partial.smsDelivered") : null,
        t("peopleDetail:minor.partial.linkStillValid"),
      ].filter(Boolean).join(language.toLowerCase().startsWith("zh") ? "" : " ")
    : "";

  return (
    <>
      <SectionTitle title={t("peopleDetail:minor.section")} />
      <InfoCard>
        <KvRow
          first
          label={t("peopleDetail:minor.rows.form")}
          value={<Text style={detailStyles.rowValueText} numberOfLines={2}>{t(`peopleDetail:minor.forms.${item.formType}`)}</Text>}
        />
        <KvRow
          label={t("peopleDetail:minor.rows.status")}
          value={<PeopleTag label={t(`peopleDetail:minor.stages.${item.stage}`)} tone={MINOR_STAGE_TONES[item.stage]} />}
        />
        <KvRow
          label={t("peopleDetail:minor.rows.guardian")}
          value={guardianText ? <Text style={detailStyles.rowValueText} numberOfLines={2}>{guardianText}</Text> : notFilled}
        />
        <KvRow
          label={t("peopleDetail:minor.rows.contact")}
          value={contactText ? <Text style={detailStyles.rowValueText} numberOfLines={2}>{contactText}</Text> : notFilled}
        />
        {showSent ? (
          <KvRow
            label={t("peopleDetail:minor.rows.sent")}
            value={(
              <>
                <Text style={detailStyles.rowValueText}>{sentAt ?? t("common:na")}</Text>
                {channelTag("email", item.lastSentEmail)}
                {channelTag("sms", item.lastSentSms)}
              </>
            )}
          />
        ) : null}
        {linkStage && expiresAt ? (
          <KvRow
            label={t("peopleDetail:minor.rows.expires")}
            value={(
              <Text style={[detailStyles.rowValueText, item.stage === "link_expired" && styles.expired]} numberOfLines={1}>
                {t("peopleDetail:minor.sydneyTime", { time: expiresAt })}
              </Text>
            )}
          />
        ) : null}
        {(item.stage === "pending_review" || item.stage === "approved") && signedAt ? (
          <KvRow
            label={t("peopleDetail:minor.rows.signed")}
            value={<Text style={detailStyles.rowValueText}>{t("peopleDetail:minor.sydneyTime", { time: signedAt })}</Text>}
          />
        ) : null}
        {item.stage === "returned" && item.reviewComment ? (
          <KvRow
            label={t("peopleDetail:minor.rows.reviewComment")}
            value={<Text style={[detailStyles.rowValueText, styles.returned]} numberOfLines={4}>{item.reviewComment}</Text>}
          />
        ) : null}

        {deliveryUrl ? (
          <View style={styles.block} testID="minor-guardian-partial">
            <NoticeBox tone="amber" icon="alert-outline">{partialText}</NoticeBox>
            <Button
              mode="outlined"
              icon="content-copy"
              onPress={() => void handleCopy(deliveryUrl)}
              style={styles.button}
              testID="minor-guardian-copy-link"
            >
              {t("peopleDetail:minor.actions.copyLink")}
            </Button>
            {/* 剪贴板不可用时可长按选中手动复制。 */}
            <Text selectable style={styles.link} numberOfLines={2}>{deliveryUrl}</Text>
          </View>
        ) : null}

        {canEdit ? (
          <View style={styles.block}>
            {showSend || showRevoke ? (
              <View style={styles.actions}>
                {showSend ? (
                  <Button
                    mode={showRevoke ? "outlined" : "contained"}
                    icon={isResendStage(item.stage) ? "refresh" : "send"}
                    buttonColor={showRevoke ? undefined : HB_COLORS.action}
                    textColor={showRevoke ? HB_COLORS.action : HB_COLORS.white}
                    onPress={() => setSheetOpen(true)}
                    style={[styles.button, styles.actionButton]}
                    testID="minor-guardian-send"
                  >
                    {isResendStage(item.stage) ? t("peopleDetail:minor.actions.resend") : t("peopleDetail:minor.actions.send")}
                  </Button>
                ) : null}
                {showRevoke ? (
                  <Button
                    mode="outlined"
                    textColor={HB_COLORS.danger}
                    onPress={() => setRevokeOpen(true)}
                    loading={revoke.isPending}
                    disabled={revoke.isPending}
                    style={[styles.button, styles.actionButton, styles.dangerButton]}
                    testID="minor-guardian-revoke"
                  >
                    {t("peopleDetail:minor.actions.revoke")}
                  </Button>
                ) : null}
              </View>
            ) : null}
            <Text style={styles.hint}>{t(`peopleDetail:minor.hints.${item.stage}`)}</Text>
          </View>
        ) : null}
      </InfoCard>

      {sheetOpen && showSend ? (
        <MinorGuardianSheet
          userGuid={userGuid}
          item={item}
          smsAvailable={detail.smsAvailable}
          onDismiss={() => setSheetOpen(false)}
          onSent={handleSent}
          onConflict={refresh}
        />
      ) : null}
      {revokeOpen ? (
        <ConfirmDialog
          title={t("peopleDetail:minor.confirmRevoke.title")}
          message={t("peopleDetail:minor.confirmRevoke.message")}
          cancelLabel={t("peopleDetail:minor.sheet.cancel")}
          confirmLabel={t("peopleDetail:minor.confirmRevoke.confirm")}
          danger
          pending={revoke.isPending}
          onCancel={() => setRevokeOpen(false)}
          onConfirm={() => revoke.mutate(item)}
          testID="minor-guardian-revoke-confirm"
        />
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  block: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.xs,
    padding: 12,
  },
  actions: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  button: { borderRadius: HB_RADIUS.control },
  // flex: 1（基准宽 0）让「重新发送」和「作废链接」等宽，不受图标宽度影响。
  actionButton: { flex: 1, minWidth: 120 },
  dangerButton: { borderColor: "#FDA29B" },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
  link: { color: HB_COLORS.textSecondary, fontFamily: "Menlo", fontSize: 11 },
  expired: { color: HB_COLORS.warning },
  returned: { color: HB_COLORS.danger },
});
