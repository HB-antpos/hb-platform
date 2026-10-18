import { useMemo } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { InfoCard, NoticeBox, PEOPLE_TONES, PeopleTag } from "@/modules/people/ui";
import { usePosPermissionTranslation } from "@/modules/users/pos-permission-i18n";
import { PosPermissionSheet } from "@/modules/users/pos-permission-sheet";
import type { PosPermissionChanges } from "@/modules/users/pos-terminal-permission-presentation";
import { formatPosHighRiskWarning } from "@/modules/users/pos-terminal-permissions-format";
import {
  buildPosHighRiskWarning,
  buildPosPermissionChangeRows,
} from "@/modules/users/pos-terminal-permissions-view";
import { HB_COLORS } from "@/shared/theme/tokens";
import { clauseSeparator } from "@/shared/i18n/punctuation";

interface PosPermissionChangesSheetProps {
  changes: PosPermissionChanges;
  userName: string;
  /** 已拼好的分店展示名（店名 + 编号），副标题写明「只对它生效」。 */
  storeLabel: string;
  busy: boolean;
  onDismiss: () => void;
  onConfirm: () => void;
}

/**
 * 保存前确认（设计稿 A5②）：开启 / 关闭分开列；本次新开了高风险权限时顶部红色警示条，
 * 写明开启后对收银的实际影响；副标题写明只对当前分店生效。
 * 仍用 PosPermissionSheet（Paper Portal + Modal），不能改回原生 Modal，
 * 否则保存失败的 Snackbar 与二次确认 Dialog 会被它压住。
 */
export function PosPermissionChangesSheet({
  changes,
  userName,
  storeLabel,
  busy,
  onDismiss,
  onConfirm,
}: PosPermissionChangesSheetProps) {
  const { t, translate, labels } = usePosPermissionTranslation();
  const rows = useMemo(() => buildPosPermissionChangeRows(changes, labels), [changes, labels]);
  const warning = useMemo(() => {
    const raw = buildPosHighRiskWarning(changes, labels);
    return raw ? formatPosHighRiskWarning(raw, translate) : null;
  }, [changes, labels, translate]);
  const total = rows.length;

  return (
    <PosPermissionSheet
      title={t("confirm.title", { value: total })}
      subtitle={t("confirm.subtitle", { user: userName, store: storeLabel })}
      closeLabel={t("actions.close")}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={
        <>
          <Button
            mode="outlined"
            onPress={onDismiss}
            disabled={busy}
            style={[BUSINESS_UI.button, styles.footerButton]}
            contentStyle={BUSINESS_UI.buttonContent}
          >
            {t("actions.backToEdit")}
          </Button>
          <Button
            mode="contained"
            onPress={onConfirm}
            disabled={busy || total === 0}
            loading={busy}
            style={[BUSINESS_UI.button, styles.footerButton]}
            contentStyle={BUSINESS_UI.buttonContent}
          >
            {t("actions.confirmSave")}
          </Button>
        </>
      }
    >
      {warning ? (
        <NoticeBox tone="red" icon="alert-octagon-outline">
          <Text style={styles.warningLead}>{warning.summary}</Text>
          {warning.impact ? ` ${warning.impact}` : ""}
        </NoticeBox>
      ) : null}

      <InfoCard>
        {rows.map((row, index) => {
          const added = row.kind === "added";
          const tone = PEOPLE_TONES[added ? "green" : "red"];
          const title = t(added ? "confirm.rowAdded" : "confirm.rowRemoved", { name: row.name });
          // 只有「新开」高风险才打标签；关掉高风险权限是收紧，不需要再红色提醒。
          const showRisk = added && row.highRisk;
          return (
            <View
              key={row.code}
              style={[styles.row, index > 0 && styles.rowDivider]}
              accessible
              accessibilityLabel={[title, row.groupLabel, showRisk ? t("badges.highRisk") : null]
                .filter(Boolean)
                .join(clauseSeparator())}
            >
              <View style={[styles.sign, { backgroundColor: tone.bg }]}>
                <Icon
                  source={added ? "plus-circle-outline" : "minus-circle-outline"}
                  size={19}
                  color={tone.fg}
                />
              </View>
              <View style={styles.rowText}>
                <Text style={styles.rowTitle} numberOfLines={2}>
                  {title}
                </Text>
                <Text style={styles.rowMeta} numberOfLines={1}>
                  {row.groupLabel}
                </Text>
              </View>
              {showRisk ? <PeopleTag label={t("badges.highRisk")} tone="red" /> : null}
            </View>
          );
        })}
      </InfoCard>
    </PosPermissionSheet>
  );
}

const styles = StyleSheet.create({
  footerButton: { flex: 1 },
  warningLead: { fontWeight: "700" },
  row: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  sign: { width: 34, height: 34, borderRadius: 10, alignItems: "center", justifyContent: "center" },
  rowText: { flex: 1, minWidth: 0 },
  rowTitle: { fontSize: 14.5, lineHeight: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  rowMeta: { fontSize: 12.5, lineHeight: 17, color: HB_COLORS.textSecondary, marginTop: 1 },
});
