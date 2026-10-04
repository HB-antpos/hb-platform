import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import {
  PosPermissionHighRiskPill,
} from "@/modules/users/pos-permission-group-card";
import { POS_PERMISSION_COLORS, PosPermissionSheet } from "@/modules/users/pos-permission-sheet";
import type {
  PosPermissionChanges,
  PosPermissionDisplayGroupKey,
} from "@/modules/users/pos-terminal-permission-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface PosPermissionChangesSheetProps {
  changes: PosPermissionChanges;
  userName: string;
  storeName: string;
  busy: boolean;
  getGroupLabel: (key: PosPermissionDisplayGroupKey) => string;
  onDismiss: () => void;
  onConfirm: () => void;
}

/** 保存前确认：逐项列出开启/关闭，新开高风险项时额外警告，并说明只影响当前分店。 */
export function PosPermissionChangesSheet({
  changes,
  userName,
  storeName,
  busy,
  getGroupLabel,
  onDismiss,
  onConfirm,
}: PosPermissionChangesSheetProps) {
  const { t } = useAppTranslation(["userManagement", "common"]);
  const total = changes.ordered.length;

  return (
    <PosPermissionSheet
      title={t("posPermissions.confirm.title", { value: total })}
      subtitle={t("posPermissions.confirm.subtitle", { user: userName, store: storeName })}
      closeLabel={t("posPermissions.actions.close")}
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
            {t("posPermissions.actions.backToEdit")}
          </Button>
          <Button
            mode="contained"
            onPress={onConfirm}
            disabled={busy || total === 0}
            loading={busy}
            style={[BUSINESS_UI.button, styles.footerButton]}
            contentStyle={BUSINESS_UI.buttonContent}
          >
            {t("posPermissions.actions.confirmSave")}
          </Button>
        </>
      }
    >
      {changes.addedHighRiskCount > 0 ? (
        <View style={[styles.notice, styles.warningNotice]} accessibilityRole="alert">
          <Icon source="alert-outline" size={18} color={HB_COLORS.warning} />
          <Text style={[styles.noticeText, { color: HB_COLORS.warning }]}>
            {t("posPermissions.confirm.highRiskWarning", { value: changes.addedHighRiskCount })}
          </Text>
        </View>
      ) : null}

      <View style={styles.list}>
        {changes.ordered.map(({ item, kind }, index) => {
          const added = kind === "added";
          return (
            <View
              key={item.code}
              style={[styles.row, index > 0 && styles.rowDivider]}
              accessible
              accessibilityLabel={[
                t(added ? "posPermissions.confirm.added" : "posPermissions.confirm.removed"),
                item.name,
                getGroupLabel(item.groupKey),
                item.highRisk ? t("posPermissions.badges.highRisk") : null,
              ]
                .filter(Boolean)
                .join("，")}
            >
              <View style={[styles.sign, added ? styles.signAdded : styles.signRemoved]}>
                <Icon
                  source={added ? "plus" : "minus"}
                  size={16}
                  color={added ? HB_COLORS.success : HB_COLORS.textSecondary}
                />
              </View>
              <View style={styles.rowText}>
                <Text
                  style={[styles.rowName, !added && styles.rowNameRemoved]}
                  numberOfLines={2}
                >
                  {item.name}
                </Text>
                <Text style={styles.rowMeta}>
                  {t(added ? "posPermissions.confirm.added" : "posPermissions.confirm.removed")}
                  {" · "}
                  {getGroupLabel(item.groupKey)}
                </Text>
              </View>
              {item.highRisk ? (
                <PosPermissionHighRiskPill label={t("posPermissions.badges.highRisk")} />
              ) : null}
            </View>
          );
        })}
      </View>

      <View style={[styles.notice, styles.infoNotice]}>
        <Icon source="information-outline" size={18} color={HB_COLORS.action} />
        <Text style={[styles.noticeText, { color: HB_COLORS.action }]}>
          {t("posPermissions.confirm.scopeNotice", { store: storeName })}
        </Text>
      </View>
    </PosPermissionSheet>
  );
}

const styles = StyleSheet.create({
  footerButton: { flex: 1 },
  notice: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
  },
  warningNotice: {
    backgroundColor: POS_PERMISSION_COLORS.warningSoft,
    borderColor: POS_PERMISSION_COLORS.warningBorder,
  },
  infoNotice: {
    backgroundColor: POS_PERMISSION_COLORS.infoSoft,
    borderColor: POS_PERMISSION_COLORS.infoBorder,
  },
  noticeText: { flex: 1, fontSize: 13, lineHeight: 19 },
  list: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  row: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  sign: { width: 24, height: 24, borderRadius: 12, alignItems: "center", justifyContent: "center" },
  signAdded: { backgroundColor: POS_PERMISSION_COLORS.successSoft },
  signRemoved: { backgroundColor: HB_COLORS.surfaceMuted },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  rowName: { fontSize: 15, lineHeight: 21, color: HB_COLORS.textPrimary },
  rowNameRemoved: {
    color: HB_COLORS.textSecondary,
    textDecorationLine: "line-through",
  },
  rowMeta: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
});
