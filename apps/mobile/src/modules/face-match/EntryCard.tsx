// 名单条目卡片：事件类别、证据编号、生效门店、状态、批准进度、到期日、即将到期标记。
// 条目里没有姓名与事件细节，也不展示照片（照片只在「命中复核」里并排查看）。
import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  ENTRY_STATUS_LABEL_KEYS,
  entryStatusTone,
  formatApprovalProgress,
  incidentCategoryLabelKey,
  summarizeStoreScope,
  type EntryActionAccess,
} from "./entries-logic";
import { formatLocalDate } from "./dates";
import { FaceCard, MONO, Tag } from "./ui";
import type { FaceUnwelcomeEntry } from "./types";

export function EntryCard({
  entry,
  actions,
  busy,
  onApprove,
  onDispute,
  onWithdraw,
}: {
  entry: FaceUnwelcomeEntry;
  actions: EntryActionAccess;
  busy: boolean;
  onApprove: () => void;
  onDispute: () => void;
  onWithdraw: () => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const categoryKey = incidentCategoryLabelKey(entry.incidentCategory);
  const statusKey = ENTRY_STATUS_LABEL_KEYS[entry.status];
  const scope = summarizeStoreScope(entry.storeScope);
  const hasActions = actions.approve || actions.dispute || actions.withdraw;
  return (
    <FaceCard>
      <View style={styles.titleRow}>
        <Text style={styles.category} numberOfLines={2}>
          {categoryKey ? t(categoryKey) : entry.incidentCategoryName}
        </Text>
        <Tag label={statusKey ? t(statusKey) : entry.statusName} tone={entryStatusTone(entry.status)} />
      </View>
      {entry.categoryNote ? (
        <Text style={styles.note} numberOfLines={2}>
          {entry.categoryNote}
        </Text>
      ) : null}
      <Text style={styles.line}>
        <Text style={styles.label}>{t("unwelcome.fields.evidenceRef")}</Text>
        <Text style={MONO}>{`  ${entry.evidenceRef}`}</Text>
      </Text>
      <View style={styles.tags}>
        {scope.visible.map((code) => (
          <Tag key={code} label={code} />
        ))}
        {scope.extra > 0 ? <Tag label={`+${scope.extra}`} /> : null}
      </View>
      <View style={styles.meta}>
        <Text style={[styles.metaText, MONO]}>{`${t("unwelcome.fields.approvals")} ${formatApprovalProgress(entry)}`}</Text>
        <Text style={[styles.metaText, MONO]}>{`${t("unwelcome.fields.expiresAt")} ${formatLocalDate(entry.expiresAtUtc)}`}</Text>
      </View>
      <View style={styles.tags}>
        {entry.expiringSoon ? <Tag tone="warning" icon="clock-alert-outline" label={t("unwelcome.expiringSoon", { days: entry.daysToExpiry })} /> : null}
        {/* 条目已失效但腾讯云侧人员 / 参考照片还没删干净：提示后台仍在清理（不是已经删除完毕）。 */}
        {entry.cleanupPending ? <Tag tone="info" icon="broom" label={t("unwelcome.cleanupPending")} /> : null}
      </View>
      <Text style={styles.submitted} numberOfLines={1}>
        {t("unwelcome.fields.submittedBy", { name: entry.submittedByName || "-", time: formatLocalDate(entry.submittedAtUtc) })}
      </Text>
      {entry.disputeNote ? (
        <Text style={styles.dispute} numberOfLines={2}>
          {t("unwelcome.fields.disputeNoteLine", { note: entry.disputeNote })}
        </Text>
      ) : null}
      {hasActions ? (
        <View style={styles.actions}>
          {actions.approve ? (
            <Button mode="contained" compact disabled={busy} onPress={onApprove}>
              {t("unwelcome.actions.approve")}
            </Button>
          ) : null}
          {actions.dispute ? (
            <Button mode="text" compact disabled={busy} onPress={onDispute}>
              {t("unwelcome.actions.dispute")}
            </Button>
          ) : null}
          {actions.withdraw ? (
            <Button mode="text" compact textColor={HB_COLORS.danger} disabled={busy} onPress={onWithdraw}>
              {t("unwelcome.actions.withdraw")}
            </Button>
          ) : null}
        </View>
      ) : null}
    </FaceCard>
  );
}

const styles = StyleSheet.create({
  titleRow: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: HB_SPACING.xs },
  category: { flex: 1, fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  note: { fontSize: 13, color: HB_COLORS.textSecondary },
  line: { fontSize: 13, color: HB_COLORS.textPrimary },
  label: { color: HB_COLORS.textSecondary },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  meta: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.sm },
  metaText: { fontSize: 13, color: HB_COLORS.textPrimary },
  submitted: { fontSize: 12, color: HB_COLORS.textSecondary },
  dispute: { fontSize: 12, color: HB_COLORS.textSecondary },
  actions: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xxs, marginTop: HB_SPACING.xxs },
});
