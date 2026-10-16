import { Pressable, StyleSheet, Text as NativeText, View } from "react-native";
import { Icon } from "react-native-paper";
import { ActionRow, InfoCard, PEOPLE_TONES, type PeopleTone } from "@/modules/people";
import { HB_COLORS } from "@/shared/theme/tokens";
import type { MoreItemKey, StatusIntent } from "./detail-rules";
import { PeopleSheet } from "./people-sheet";

export interface MoreRowModel {
  key: MoreItemKey;
  title: string;
  subtitle: string;
}

const MORE_ICONS: Record<MoreItemKey, { icon: string; tone: PeopleTone }> = {
  barcode: { icon: "printer-outline", tone: "blue" },
  access: { icon: "shield-key-outline", tone: "blue" },
  pos: { icon: "cash-register", tone: "green" },
  copyPermissions: { icon: "content-copy", tone: "violet" },
  loginRecords: { icon: "history", tone: "gray" },
};

/**
 * 「更多操作」底部弹层（A2③）：替换原来竖排 5 个按钮的居中弹窗。
 * 没有权限的项由 buildDetailLayout 直接剔除，这里只渲染传进来的行；每项带一行状态说明。
 * 「详情」已经是当前页，不再作为菜单项重复出现。
 */
export function MoreActionsSheet({
  title,
  subtitle,
  closeLabel,
  rows,
  status,
  hint,
  onSelect,
  onSelectStatus,
  onDismiss,
}: {
  title: string;
  subtitle: string;
  closeLabel: string;
  rows: MoreRowModel[];
  status: { intent: StatusIntent; title: string; subtitle: string } | null;
  hint: string;
  onSelect: (key: MoreItemKey) => void;
  onSelectStatus: (intent: StatusIntent) => void;
  onDismiss: () => void;
}) {
  return (
    <PeopleSheet title={title} subtitle={subtitle} closeLabel={closeLabel} onDismiss={onDismiss} testID="person-more-sheet">
      {rows.length ? (
        <InfoCard>
          {rows.map((row, index) => (
            <ActionRow
              key={row.key}
              first={index === 0}
              icon={MORE_ICONS[row.key].icon}
              tone={MORE_ICONS[row.key].tone}
              title={row.title}
              subtitle={row.subtitle}
              onPress={() => onSelect(row.key)}
            />
          ))}
        </InfoCard>
      ) : null}
      {status ? (
        <InfoCard>
          <StatusRow
            intent={status.intent}
            title={status.title}
            subtitle={status.subtitle}
            onPress={() => onSelectStatus(status.intent)}
          />
        </InfoCard>
      ) : null}
      <NativeText style={styles.hint}>{hint}</NativeText>
    </PeopleSheet>
  );
}

/** 停用 / 启用行：停用标题红色（ActionRow 的标题色固定，所以这里单独画一行）。 */
function StatusRow({ intent, title, subtitle, onPress }: { intent: StatusIntent; title: string; subtitle: string; onPress: () => void }) {
  const danger = intent === "disable";
  const color = PEOPLE_TONES[danger ? "red" : "green"];
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={title} onPress={onPress} style={styles.statusRow} testID="person-more-status">
      <View style={[styles.statusIcon, { backgroundColor: color.bg }]}>
        <Icon source={danger ? "account-off-outline" : "account-check-outline"} size={19} color={color.fg} />
      </View>
      <View style={styles.statusText}>
        <NativeText style={[styles.statusTitle, { color: danger ? HB_COLORS.danger : HB_COLORS.textPrimary }]} numberOfLines={1}>{title}</NativeText>
        <NativeText style={styles.statusSubtitle} numberOfLines={1}>{subtitle}</NativeText>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  statusRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 52, paddingHorizontal: 14, paddingVertical: 8 },
  statusIcon: { alignItems: "center", borderRadius: 10, height: 34, justifyContent: "center", width: 34 },
  statusText: { flex: 1, minWidth: 0 },
  statusTitle: { fontSize: 14.5, fontWeight: "700" },
  statusSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12.5, paddingBottom: 4, textAlign: "center" },
});
