import { useEffect, useState } from "react";
import { StyleSheet, useWindowDimensions, View } from "react-native";
import { ActivityIndicator, Button, ProgressBar, Text } from "react-native-paper";
import { InfoCard, NoticeBox } from "@/modules/people";
import { PeopleSheet, SHEET_BUTTON_STYLE, SheetActions } from "@/modules/people/detail/people-sheet";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useRefreshPeopleAfterNotice, useStaffNoticeBatch, useStaffNoticeRetry } from "./hooks";
import { countFailedRecipients, countProcessed, isBatchFinished } from "./logic";
import { RecipientResultRow } from "./ui";

/**
 * 发送进度与结果（同一个弹层）。发送在后台逐人进行，这里只是看进度：
 * 关掉弹层、切后台、锁屏都不影响发送；弹层关闭即停止轮询，结果随时可在「发送记录」里看。
 */
export function StaffNoticeProgressSheet({
  batchId,
  templateName,
  onClose,
}: {
  batchId: string;
  templateName: string;
  onClose: () => void;
}) {
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  const { fontScale, width } = useWindowDimensions();
  const stacked = fontScale > 1.2 || width < 380;
  const batchQuery = useStaffNoticeBatch(batchId, { poll: true });
  const retryMutation = useStaffNoticeRetry();
  const refreshPeople = useRefreshPeopleAfterNotice();
  const [retryError, setRetryError] = useState<string | null>(null);
  const detail = batchQuery.data;
  const finished = isBatchFinished(detail);

  useEffect(() => {
    if (finished) void refreshPeople();
  }, [finished, refreshPeople]);

  const handleRetry = async () => {
    setRetryError(null);
    try {
      await retryMutation.mutateAsync(batchId);
    } catch (error) {
      setRetryError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "result.retryFailed" }));
      // 不能重试的原因写在每个人的说明里，刷新后就地可见。
      void batchQuery.refetch();
    }
  };

  const total = detail?.totalCount ?? 0;
  const processed = detail ? countProcessed(detail) : 0;
  const failed = detail ? countFailedRecipients(detail) : 0;
  const smsSummary = detail && detail.smsRequested > 0
    ? ` · ${t("result.smsCount", { sent: detail.smsSent, total: detail.smsRequested })}`
    : "";

  return (
    <PeopleSheet
      title={finished ? t("result.title") : t("progress.title")}
      subtitle={finished && detail
        ? `${t("result.emailCount", { sent: detail.emailSent, total: detail.emailRequested })}${smsSummary}`
        : t("progress.subtitle", { name: templateName, count: total })}
      closeLabel={t("common:actions.close")}
      onDismiss={onClose}
      dismissable={!retryMutation.isPending}
      testID="staff-notice-progress-sheet"
      footer={(
        <SheetActions stacked={stacked}>
          {!finished ? (
            <Button mode="outlined" onPress={onClose} style={SHEET_BUTTON_STYLE}>{t("progress.background")}</Button>
          ) : (
            <>
              {detail?.canRetry ? (
                <Button
                  mode="outlined"
                  icon="refresh"
                  onPress={() => void handleRetry()}
                  loading={retryMutation.isPending}
                  disabled={retryMutation.isPending}
                  style={SHEET_BUTTON_STYLE}
                >
                  {t("result.retry", { count: failed })}
                </Button>
              ) : null}
              <Button mode="contained" onPress={onClose} disabled={retryMutation.isPending} style={SHEET_BUTTON_STYLE}>
                {t("result.done")}
              </Button>
            </>
          )}
        </SheetActions>
      )}
    >
      {!detail ? (
        batchQuery.isError ? (
          <NoticeBox tone="amber" icon="wifi-off">{t("progress.offline")}</NoticeBox>
        ) : (
          <View style={styles.loading}><ActivityIndicator /></View>
        )
      ) : (
        <>
          {!finished ? (
            <View style={styles.progress}>
              <Text style={styles.progressText}>{t("progress.processed", { done: processed, total })}</Text>
              <ProgressBar progress={total ? processed / total : 0} color={HB_COLORS.action} style={styles.bar} />
            </View>
          ) : null}
          <InfoCard>
            {detail.recipients.map((row, index) => <RecipientResultRow key={row.id} row={row} first={index === 0} />)}
          </InfoCard>
          {batchQuery.isError && !finished ? <NoticeBox tone="amber" icon="wifi-off">{t("progress.offline")}</NoticeBox> : null}
          {retryError ? <NoticeBox tone="red" icon="alert-circle-outline">{retryError}</NoticeBox> : null}
          {finished
            ? <NoticeBox>{t("result.receiptHint")}</NoticeBox>
            : <Text style={styles.hint}>{t("progress.hint")}</Text>}
        </>
      )}
    </PeopleSheet>
  );
}

const styles = StyleSheet.create({
  bar: { borderRadius: 3, height: 6 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12.5, textAlign: "center" },
  loading: { alignItems: "center", paddingVertical: HB_SPACING.lg },
  progress: { gap: 6 },
  progressText: { color: HB_COLORS.textPrimary, fontSize: 13.5, fontWeight: "700" },
});
