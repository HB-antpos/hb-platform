import { useMemo, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Avatar, Button, Icon, Searchbar, Text } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useStoreUsers } from "@/modules/users/hooks";
import { POS_PERMISSION_COLORS, PosPermissionSheet } from "@/modules/users/pos-permission-sheet";
import { useStoreUserPosTerminalPermissions } from "@/modules/users/pos-terminal-permissions-hooks";
import { buildGrantedPosPermissionCodes } from "@/modules/users/pos-terminal-permissions";
import {
  filterPosPermissionCopyCandidates,
  getPosPermissionCopyCandidateName,
  getPosPermissionInitials,
} from "@/modules/users/pos-terminal-permission-presentation";
import type { PosTerminalPermissionOption } from "@/modules/users/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface PosPermissionCopySheetProps {
  storeCode: string;
  storeGuid: string;
  storeName: string;
  targetUserGuid: string;
  actorUserGuid?: string | null;
  /** 当前员工在本店的可分配白名单；复制结果必须与它取交集。 */
  assignablePermissions: PosTerminalPermissionOption[];
  onDismiss: () => void;
  onApply: (colleagueName: string, codes: string[]) => void;
}

/**
 * 复制同事：列出本店有资格做 POS 授权的同事，选中某人后才请求其权限（避免列表 N 次请求）。
 * 套用只改草稿，不直接保存。
 */
export function PosPermissionCopySheet({
  storeCode,
  storeGuid,
  storeName,
  targetUserGuid,
  actorUserGuid,
  assignablePermissions,
  onDismiss,
  onApply,
}: PosPermissionCopySheetProps) {
  const { t } = useAppTranslation(["userManagement", "common"]);
  const [query, setQuery] = useState("");
  const [selectedGuid, setSelectedGuid] = useState<string | null>(null);
  const hasStoreCode = storeCode.trim().length > 0;
  // storeCode 为 undefined 时 useStoreUsers 不发请求；绝不能传 null（会查全部分店）。
  const usersQuery = useStoreUsers(hasStoreCode ? storeCode : undefined, "");
  const candidates = useMemo(
    () =>
      filterPosPermissionCopyCandidates(usersQuery.data ?? [], {
        targetUserGuid,
        actorUserGuid,
        query,
      }),
    [actorUserGuid, query, targetUserGuid, usersQuery.data]
  );
  const allCandidateCount = useMemo(
    () =>
      filterPosPermissionCopyCandidates(usersQuery.data ?? [], { targetUserGuid, actorUserGuid })
        .length,
    [actorUserGuid, targetUserGuid, usersQuery.data]
  );
  const selectedUser = useMemo(
    () => (usersQuery.data ?? []).find((user) => user.userGUID === selectedGuid) ?? null,
    [selectedGuid, usersQuery.data]
  );
  // 同一 storeGuid 下读取同事的 effective 权限，沿用现有 GET 接口与缓存键。
  const colleagueQuery = useStoreUserPosTerminalPermissions(
    selectedGuid,
    storeGuid,
    Boolean(selectedGuid)
  );
  const copyCodes = useMemo(
    () =>
      colleagueQuery.data
        ? buildGrantedPosPermissionCodes(
            colleagueQuery.data.effectivePermissionCodes,
            assignablePermissions
          )
        : null,
    [assignablePermissions, colleagueQuery.data]
  );
  const selectedName = selectedUser ? getPosPermissionCopyCandidateName(selectedUser) : "";
  const canApply = Boolean(selectedUser && copyCodes && !colleagueQuery.isFetching);

  const renderList = () => {
    if (!hasStoreCode) {
      return <Text style={styles.stateText}>{t("posPermissions.copy.unavailable")}</Text>;
    }
    if (usersQuery.isLoading) {
      return (
        <View style={styles.stateRow}>
          <ActivityIndicator color={HB_COLORS.brand} />
        </View>
      );
    }
    if (usersQuery.isError) {
      return (
        <View style={styles.stateRow}>
          <Text style={styles.stateText}>{t("posPermissions.copy.listFailed")}</Text>
          <Button compact mode="text" onPress={() => void usersQuery.refetch()}>
            {t("common:actions.retry")}
          </Button>
        </View>
      );
    }
    if (candidates.length === 0) {
      return (
        <Text style={styles.stateText}>
          {t(allCandidateCount === 0 ? "posPermissions.copy.empty" : "posPermissions.copy.noMatch")}
        </Text>
      );
    }

    return (
      <View style={styles.list}>
        {candidates.map((user, index) => {
          const name = getPosPermissionCopyCandidateName(user);
          const selected = user.userGUID === selectedGuid;
          let detail: string | null = null;
          if (selected) {
            if (colleagueQuery.isFetching) detail = t("posPermissions.copy.loadingColleague");
            else if (colleagueQuery.isError) detail = t("posPermissions.copy.loadFailed");
            else if (copyCodes) detail = t("posPermissions.copy.permissionCount", { value: copyCodes.length });
          }
          return (
            <Pressable
              key={user.userGUID}
              accessibilityRole="radio"
              accessibilityState={{ checked: selected }}
              accessibilityLabel={[name, user.username !== name ? user.username : null, detail]
                .filter(Boolean)
                .join("，")}
              onPress={() => setSelectedGuid(user.userGUID)}
              style={({ pressed }) => [
                styles.row,
                index > 0 && styles.rowDivider,
                selected && styles.rowSelected,
                pressed && styles.pressed,
              ]}
            >
              <Avatar.Text
                size={36}
                label={getPosPermissionInitials(name)}
                style={styles.avatar}
                labelStyle={styles.avatarLabel}
              />
              <View style={styles.rowText}>
                <Text style={styles.rowName} numberOfLines={1}>
                  {name}
                </Text>
                {detail ? (
                  <Text
                    style={[styles.rowMeta, selected && colleagueQuery.isError && styles.rowMetaError]}
                    numberOfLines={2}
                  >
                    {detail}
                  </Text>
                ) : user.username !== name ? (
                  <Text style={styles.rowMeta} numberOfLines={1}>
                    {user.username}
                  </Text>
                ) : null}
              </View>
              {selected && colleagueQuery.isFetching ? (
                <ActivityIndicator size="small" color={HB_COLORS.brand} />
              ) : selected && colleagueQuery.isError ? (
                <Button compact mode="text" onPress={() => void colleagueQuery.refetch()}>
                  {t("common:actions.retry")}
                </Button>
              ) : (
                <Icon
                  source={selected ? "radiobox-marked" : "radiobox-blank"}
                  size={22}
                  color={selected ? HB_COLORS.brand : HB_COLORS.outline}
                />
              )}
            </Pressable>
          );
        })}
      </View>
    );
  };

  return (
    <PosPermissionSheet
      title={t("posPermissions.copy.title")}
      subtitle={t("posPermissions.copy.subtitle", { store: storeName })}
      closeLabel={t("posPermissions.actions.close")}
      onDismiss={onDismiss}
      header={
        hasStoreCode ? (
          <Searchbar
            value={query}
            onChangeText={setQuery}
            placeholder={t("posPermissions.copy.searchPlaceholder")}
            style={styles.search}
            inputStyle={styles.searchInput}
            autoCorrect={false}
            autoCapitalize="none"
          />
        ) : undefined
      }
      footer={
        <Button
          mode="contained"
          disabled={!canApply}
          onPress={() => {
            if (selectedUser && copyCodes) onApply(selectedName, copyCodes);
          }}
          style={[BUSINESS_UI.button, styles.applyButton]}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {selectedUser && copyCodes
            ? t("posPermissions.copy.apply", { name: selectedName, value: copyCodes.length })
            : t("posPermissions.copy.applyIdle")}
        </Button>
      }
    >
      {renderList()}
      <Text style={styles.help}>{t("posPermissions.copy.help")}</Text>
    </PosPermissionSheet>
  );
}

const styles = StyleSheet.create({
  search: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    height: 44,
    elevation: 0,
  },
  searchInput: { minHeight: 44, fontSize: 15 },
  list: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  row: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  rowSelected: { backgroundColor: POS_PERMISSION_COLORS.brandSoft },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  avatar: { backgroundColor: HB_COLORS.surfaceMuted },
  avatarLabel: { color: HB_COLORS.textSecondary, fontSize: 14, fontWeight: "700" },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  rowName: { fontSize: 15, lineHeight: 21, fontWeight: "600", color: HB_COLORS.textPrimary },
  rowMeta: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  rowMetaError: { color: HB_COLORS.danger },
  stateRow: { minHeight: 64, alignItems: "center", justifyContent: "center", gap: HB_SPACING.xs },
  stateText: {
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
    textAlign: "center",
    paddingVertical: HB_SPACING.md,
  },
  help: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  applyButton: { flex: 1 },
});
