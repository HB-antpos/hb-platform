import { useEffect, useMemo } from "react";
import { BackHandler, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { buildIdentityMeta, resolveAvatarInitial, resolveRoleTag } from "@/modules/settings/settings-summary";
import type { CurrentUser } from "@/modules/auth/types";
import type { AppLanguage } from "@/shared/i18n/types";

import { describeRow, resolveRoleTone, type RoleTone, type WhenLabel } from "./presentation";
import { VAULT_MAX_ENTRIES, findEntry, normalizeGuid, sortForDisplay, type VaultEntry } from "./vault";

const TONE_COLORS: Record<RoleTone, { bg: string; fg: string }> = {
  admin: { bg: "#F4EBFF", fg: "#6941C6" },
  manager: { bg: "#EFF6FF", fg: HB_COLORS.action },
  neutral: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary },
};

interface AccountSwitcherSheetProps {
  user: CurrentUser;
  accounts: readonly VaultEntry[];
  apiHost: string;
  language: AppLanguage;
  onClose: () => void;
  onSelect: (entry: VaultEntry) => void;
  onRelogin: (entry: VaultEntry) => void;
  onRemove: (entry: VaultEntry) => void;
  onSaveCurrent: () => void;
  onAdd: () => void;
  onSignOutCurrent: () => void;
  onSignOutAll: () => void;
}

/**
 * 账号切换器（底部弹层）：当前账号置顶，其余已保存账号按最近使用排序，最后是「添加账号」与退出。
 * 自绘遮罩 + 面板，不用原生 Modal（原生 Modal 是独立窗口，iOS 上连续开关会被系统忽略）。
 */
export function AccountSwitcherSheet({
  user,
  accounts,
  apiHost,
  language,
  onClose,
  onSelect,
  onRelogin,
  onRemove,
  onSaveCurrent,
  onAdd,
  onSignOutCurrent,
  onSignOutAll,
}: AccountSwitcherSheetProps) {
  const { t } = useAppTranslation("accountSwitch");
  const insets = useSafeAreaInsets();
  const now = Date.now();

  useEffect(() => {
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      onClose();
      return true;
    });
    return () => subscription.remove();
  }, [onClose]);

  const currentSaved = Boolean(findEntry(accounts, user.userGUID));
  const others = useMemo(
    () => sortForDisplay(accounts.filter((entry) => normalizeGuid(entry.userGuid) !== normalizeGuid(user.userGUID))),
    [accounts, user.userGUID]
  );

  const whenText = (when: WhenLabel) =>
    when.key === "daysAgo" ? t("when.daysAgo", { days: when.days }) : t(`when.${when.key}`);

  return (
    <View style={styles.root} testID="account-switcher">
      <Pressable
        style={styles.scrim}
        onPress={onClose}
        accessibilityRole="button"
        accessibilityLabel={t("sheet.close")}
      />
      <View style={[styles.sheet, { paddingBottom: Math.max(insets.bottom, 12) + 4 }]}>
        <View style={styles.grabber} />
        <View style={styles.header}>
          <View style={styles.headerText}>
            <Text style={styles.title} maxFontSizeMultiplier={1.4}>
              {t("sheet.title")}
            </Text>
            <Text style={styles.subtitle} maxFontSizeMultiplier={1.4}>
              {t("sheet.subtitle", { max: VAULT_MAX_ENTRIES })}
            </Text>
          </View>
          <Pressable onPress={onClose} accessibilityRole="button" accessibilityLabel={t("sheet.close")} hitSlop={10}>
            <MaterialCommunityIcons name="close" size={24} color={HB_COLORS.textSecondary} />
          </Pressable>
        </View>

        <ScrollView style={styles.list} showsVerticalScrollIndicator={false}>
          {/* 当前账号：没保存则标「未保存」并给「保存」按钮。 */}
          <View style={[styles.row, styles.rowCurrent]} testID="account-switcher-current">
            <Avatar name={user.fullName || user.username} tone={resolveRoleTone(user.roleNames)} />
            <View style={styles.rowBody}>
              <View style={styles.nameLine}>
                <Text style={styles.name} maxFontSizeMultiplier={1.4}>
                  {user.fullName || user.username}
                </Text>
                <Tag label={t("sheet.current")} tone="blue" />
                {currentSaved ? null : <Tag label={t("sheet.unsaved")} tone="amber" />}
              </View>
              <Text style={styles.meta} maxFontSizeMultiplier={1.4}>
                {buildIdentityMeta([resolveRoleTag(user.roleNames, language)?.label, user.storeNames?.[0]])}
              </Text>
            </View>
            {currentSaved ? (
              <MaterialCommunityIcons name="check-circle" size={22} color={HB_COLORS.action} />
            ) : (
              <Pressable
                onPress={onSaveCurrent}
                accessibilityRole="button"
                accessibilityLabel={t("sheet.save")}
                hitSlop={8}
                style={styles.saveButton}
                testID="account-switcher-save-current"
              >
                <Text style={styles.saveButtonText}>{t("sheet.save")}</Text>
              </Pressable>
            )}
          </View>

          {others.map((entry) => {
            const row = describeRow(entry, now, apiHost);
            const tone = resolveRoleTone(entry.roleNames);
            const needsRelogin = row.status === "needsRelogin";
            const meta =
              row.meta.key === "lastUsed"
                ? t("sheet.lastUsed", { when: whenText(row.meta.when) })
                : row.meta.key === "expiredMeta"
                  ? t("sheet.expiredMeta", { days: row.meta.days })
                  : t(`sheet.${row.meta.key}`);
            const roleLabel = resolveRoleTag(entry.roleNames, language)?.label;
            return (
              <Pressable
                key={entry.userGuid}
                onPress={() => (needsRelogin ? onRelogin(entry) : onSelect(entry))}
                onLongPress={() => onRemove(entry)}
                accessibilityRole="button"
                accessibilityLabel={`${entry.fullName || entry.username}. ${meta}`}
                accessibilityHint={t("sheet.rowHint")}
                style={({ pressed }) => [styles.row, pressed && styles.rowPressed]}
                testID={`account-switcher-row-${entry.userGuid}`}
              >
                <Avatar name={entry.fullName || entry.username} tone={tone} />
                <View style={styles.rowBody}>
                  <View style={styles.nameLine}>
                    <Text style={styles.name} maxFontSizeMultiplier={1.4}>
                      {entry.fullName || entry.username}
                    </Text>
                    {roleLabel ? <RoleTag label={roleLabel} tone={tone} /> : null}
                    {needsRelogin ? <Tag label={t("sheet.needsRelogin")} tone="amber" icon="clock-alert-outline" /> : null}
                  </View>
                  <Text style={styles.meta} maxFontSizeMultiplier={1.4}>
                    {buildIdentityMeta([entry.storeLabel, meta])}
                  </Text>
                </View>
                {needsRelogin ? (
                  <Text style={styles.saveButtonText}>{t("sheet.enterPassword")}</Text>
                ) : (
                  <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} />
                )}
              </Pressable>
            );
          })}

          <Pressable
            onPress={onAdd}
            accessibilityRole="button"
            accessibilityLabel={t("sheet.addTitle")}
            style={({ pressed }) => [styles.row, pressed && styles.rowPressed]}
            testID="account-switcher-add"
          >
            <View style={[styles.avatar, styles.addAvatar]}>
              <MaterialCommunityIcons name="plus" size={22} color={HB_COLORS.action} />
            </View>
            <View style={styles.rowBody}>
              <Text style={styles.addTitle} maxFontSizeMultiplier={1.4}>
                {t("sheet.addTitle")}
              </Text>
              <Text style={styles.meta} maxFontSizeMultiplier={1.4}>
                {t("sheet.addSubtitle")}
              </Text>
            </View>
          </Pressable>
        </ScrollView>

        <View style={styles.note}>
          <MaterialCommunityIcons name="information-outline" size={15} color={HB_COLORS.textSecondary} />
          <Text style={styles.noteText} maxFontSizeMultiplier={1.4}>
            {t("sheet.unsavedHint")}
          </Text>
        </View>
        <View style={styles.footer}>
          <Pressable
            onPress={onSignOutCurrent}
            accessibilityRole="button"
            style={[styles.footerButton, styles.footerOutline]}
            testID="account-switcher-sign-out-current"
          >
            <MaterialCommunityIcons name="logout" size={18} color={HB_COLORS.textPrimary} />
            <Text style={styles.footerOutlineText}>{t("sheet.signOutCurrent")}</Text>
          </Pressable>
          <Pressable
            onPress={onSignOutAll}
            accessibilityRole="button"
            style={[styles.footerButton, styles.footerDanger]}
            testID="account-switcher-sign-out-all"
          >
            <Text style={styles.footerDangerText}>{t("sheet.signOutAll")}</Text>
          </Pressable>
        </View>
      </View>
    </View>
  );
}

function Avatar({ name, tone }: { name: string; tone: RoleTone }) {
  const colors = TONE_COLORS[tone];
  return (
    <View style={[styles.avatar, { backgroundColor: tone === "neutral" ? "#E8F7EE" : colors.bg }]}>
      <Text style={[styles.avatarText, { color: tone === "neutral" ? HB_COLORS.success : colors.fg }]}>
        {resolveAvatarInitial(name)}
      </Text>
    </View>
  );
}

function RoleTag({ label, tone }: { label: string; tone: RoleTone }) {
  const colors = TONE_COLORS[tone];
  return (
    <View style={[styles.tag, { backgroundColor: colors.bg }]}>
      <Text style={[styles.tagText, { color: colors.fg }]}>{label}</Text>
    </View>
  );
}

function Tag({ label, tone, icon }: { label: string; tone: "blue" | "amber"; icon?: string }) {
  const blue = tone === "blue";
  return (
    <View style={[styles.tag, { backgroundColor: blue ? "#EFF6FF" : "#FFFAEB" }]}>
      {icon ? (
        <MaterialCommunityIcons name={icon as never} size={12} color={HB_COLORS.warning} style={styles.tagIcon} />
      ) : null}
      <Text style={[styles.tagText, { color: blue ? HB_COLORS.action : HB_COLORS.warning }]}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { ...StyleSheet.absoluteFillObject, zIndex: 900, elevation: 900, justifyContent: "flex-end" },
  scrim: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(16,24,40,0.45)" },
  sheet: {
    maxHeight: "88%",
    borderTopLeftRadius: HB_RADIUS.sheet + 4,
    borderTopRightRadius: HB_RADIUS.sheet + 4,
    backgroundColor: HB_COLORS.surface,
    paddingHorizontal: 16,
    paddingTop: 8,
    gap: 8,
  },
  grabber: { alignSelf: "center", width: 38, height: 4, borderRadius: 2, backgroundColor: HB_COLORS.outline },
  header: { flexDirection: "row", alignItems: "flex-start", gap: 8, paddingTop: 6 },
  headerText: { flex: 1, gap: 2 },
  title: { fontSize: 19, fontWeight: "800", color: HB_COLORS.textPrimary },
  subtitle: { fontSize: 13, color: HB_COLORS.textSecondary },
  list: { flexGrow: 0 },
  row: { flexDirection: "row", alignItems: "center", gap: 12, minHeight: 64, paddingVertical: 10, paddingHorizontal: 8, borderRadius: 12 },
  rowCurrent: { backgroundColor: "#F0F6FF" },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowBody: { flex: 1, minWidth: 0, gap: 3 },
  nameLine: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 6 },
  name: { fontSize: 15.5, fontWeight: "800", color: HB_COLORS.textPrimary, flexShrink: 1 },
  meta: { fontSize: 13, color: HB_COLORS.textSecondary },
  avatar: { width: 42, height: 42, borderRadius: 21, alignItems: "center", justifyContent: "center" },
  avatarText: { fontSize: 17, fontWeight: "800" },
  addAvatar: { backgroundColor: "#EFF6FF" },
  addTitle: { fontSize: 15.5, fontWeight: "800", color: HB_COLORS.action },
  tag: { flexDirection: "row", alignItems: "center", paddingHorizontal: 7, paddingVertical: 2, borderRadius: 6 },
  tagIcon: { marginRight: 3 },
  tagText: { fontSize: 11.5, fontWeight: "700" },
  saveButton: { paddingHorizontal: 10, paddingVertical: 8 },
  saveButtonText: { fontSize: 14, fontWeight: "800", color: HB_COLORS.action },
  note: { flexDirection: "row", alignItems: "flex-start", gap: 6, paddingHorizontal: 4, paddingTop: 2 },
  noteText: { flex: 1, fontSize: 12.5, lineHeight: 17, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: 8, paddingTop: 2 },
  footerButton: { minHeight: 46, borderRadius: 10, alignItems: "center", justifyContent: "center", flexDirection: "row", gap: 6, paddingHorizontal: 14 },
  footerOutline: { flex: 1, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  footerOutlineText: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  footerDanger: { borderWidth: 1, borderColor: "#FDA29B", backgroundColor: "#FEF3F2" },
  footerDangerText: { fontSize: 15, fontWeight: "700", color: HB_COLORS.danger },
});
