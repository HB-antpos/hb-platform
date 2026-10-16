import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { NoticeBox, PeopleTag } from "@/modules/people";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { AdminBottomSheet } from "./admin-bottom-sheet";
import { AdminCheckBox } from "./admin-visuals";
import { fillCopy, type PeopleRolesCopy } from "./people-roles-copy";
import { interpolatePermissionCopy, type PermissionCopy } from "./permission-hooks";
import { formatPermissionUserName } from "./permission-logic";
import { formatChangeBadge } from "./role-detail-logic";
import type { IdentityRole } from "./types";
import { AdminEmpty, AdminError, SearchField } from "./ui";

/** 「保存（+1）」「保存（+2 −1）」；没有改动时只写「保存」。 */
function saveLabel(p: PeopleRolesCopy, delta: { added: number; removed: number }) {
  const badge = formatChangeBadge(delta.added, delta.removed);
  return badge ? fillCopy(p.saveWithBadge, { badge }) : p.save;
}

/** 搜索框 + 「已选 4 / 12」+「仅看已选」：固定在弹层标题下方，不随列表滚动。 */
function SheetToolbar({
  search,
  onSearch,
  placeholder,
  countLabel,
  onlySelected,
  onToggleOnlySelected,
  p,
}: {
  search: string;
  onSearch: (value: string) => void;
  placeholder: string;
  countLabel: ReactNode;
  onlySelected: boolean;
  onToggleOnlySelected: () => void;
  p: PeopleRolesCopy;
}) {
  return (
    <>
      <SearchField value={search} onChange={onSearch} placeholder={placeholder} />
      <View style={styles.toolbar}>
        <Text style={styles.count}>{countLabel}</Text>
        <Pressable accessibilityRole="button" accessibilityState={{ selected: onlySelected }} hitSlop={8} onPress={onToggleOnlySelected}>
          <Text style={styles.onlySelected}>{onlySelected ? p.showAll : p.onlySelected}</Text>
        </Pressable>
      </View>
    </>
  );
}

function SheetFooter({
  p,
  saving,
  canSave,
  label,
  onCancel,
  onSave,
}: {
  p: PeopleRolesCopy;
  saving: boolean;
  canSave: boolean;
  label: string;
  onCancel: () => void;
  onSave: () => void;
}) {
  return (
    <>
      <Button mode="outlined" style={styles.cancelButton} disabled={saving} onPress={onCancel}>{p.cancel}</Button>
      <Button mode="contained" style={styles.saveButton} disabled={!canSave} loading={saving} onPress={onSave}>{label}</Button>
    </>
  );
}

// ───────────────────────── 分配角色 ─────────────────────────

export function AssignRolesSheet({
  p,
  copy,
  permissionName,
  permissionCode,
  roles,
  counts,
  selectedGuids,
  isLocked,
  localizedName,
  canWrite,
  search,
  onSearch,
  onlySelected,
  onToggleOnlySelected,
  onToggle,
  delta,
  saving,
  notice,
  onDismiss,
  onCancel,
  onSave,
}: {
  p: PeopleRolesCopy;
  copy: PermissionCopy;
  permissionName: string;
  permissionCode: string;
  /** 已按搜索与「仅看已选」过滤好的可见角色 */
  roles: IdentityRole[];
  counts: { selected: number; total: number };
  selectedGuids: readonly string[];
  /** 管理员等隐式拥有全部权限的角色：锁定为已选，不可取消 */
  isLocked: (role: IdentityRole) => boolean;
  localizedName: (role: IdentityRole) => string;
  canWrite: boolean;
  search: string;
  onSearch: (value: string) => void;
  onlySelected: boolean;
  onToggleOnlySelected: () => void;
  onToggle: (roleGuid: string) => void;
  delta: { added: number; removed: number };
  saving: boolean;
  /** 保存失败原因：Snackbar 会被弹层盖住，必须就地显示 */
  notice?: string;
  onDismiss: () => void;
  onCancel: () => void;
  onSave: () => void;
}) {
  const dirty = delta.added + delta.removed > 0;
  return (
    <AdminBottomSheet
      title={copy.assignRoles}
      subtitle={permissionName}
      subtitleCode={permissionCode}
      closeLabel={copy.close}
      onDismiss={onDismiss}
      dismissable={!saving}
      header={(
        <SheetToolbar
          p={p}
          search={search}
          onSearch={onSearch}
          placeholder={copy.assignRolesSearch}
          onlySelected={onlySelected}
          onToggleOnlySelected={onToggleOnlySelected}
          countLabel={fillCopy(p.assignSelected, { selected: counts.selected, total: counts.total })}
        />
      )}
      notice={notice ? <NoticeBox tone="red" icon="alert-circle-outline">{notice}</NoticeBox> : undefined}
      footer={<SheetFooter p={p} saving={saving} canSave={canWrite && dirty} label={saveLabel(p, delta)} onCancel={onCancel} onSave={onSave} />}
    >
      {roles.length === 0 ? (
        <AdminEmpty text={copy.noRoles} />
      ) : (
        <View style={styles.card}>
          {roles.map((role, index) => {
            const locked = isLocked(role);
            const checked = locked || selectedGuids.includes(role.roleGUID);
            const name = localizedName(role);
            return (
              <Pressable
                key={role.roleGUID}
                accessibilityRole="checkbox"
                accessibilityLabel={name}
                accessibilityState={{ checked, disabled: locked || !canWrite }}
                disabled={locked || !canWrite}
                onPress={() => onToggle(role.roleGUID)}
                style={[styles.row, index > 0 && styles.divider]}
              >
                <View style={styles.rowText}>
                  <View style={styles.nameRow}>
                    <Text style={styles.name} numberOfLines={1}>{name}</Text>
                    {!role.isActive ? <PeopleTag label={copy.disabled} tone="gray" /> : null}
                  </View>
                  <Text style={styles.sub} numberOfLines={1}>
                    {locked ? p.assignLockedHint : fillCopy(p.peopleCount, { count: role.userCount })}
                  </Text>
                </View>
                <AdminCheckBox checked={checked} locked={locked} disabled={!canWrite} />
              </Pressable>
            );
          })}
        </View>
      )}
    </AdminBottomSheet>
  );
}

// ───────────────────────── 分配用户（直接授权） ─────────────────────────

export interface AssignUserRow {
  userGUID: string;
  username: string;
  email: string;
  fullName?: string;
  isActive: boolean;
}

export function AssignUsersSheet({
  p,
  copy,
  permissionName,
  permissionCode,
  users,
  selectedGuids,
  selectedCount,
  canWrite,
  search,
  onSearch,
  onlySelected,
  onToggleOnlySelected,
  onToggle,
  loading,
  error,
  onRetry,
  hasMore,
  loadingMore,
  onLoadMore,
  delta,
  saving,
  notice,
  onDismiss,
  onCancel,
  onSave,
}: {
  p: PeopleRolesCopy;
  copy: PermissionCopy;
  permissionName: string;
  permissionCode: string;
  users: AssignUserRow[];
  selectedGuids: readonly string[];
  selectedCount: number;
  canWrite: boolean;
  search: string;
  onSearch: (value: string) => void;
  onlySelected: boolean;
  onToggleOnlySelected: () => void;
  onToggle: (userGuid: string) => void;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  hasMore: boolean;
  loadingMore: boolean;
  onLoadMore: () => void;
  delta: { added: number; removed: number };
  saving: boolean;
  notice?: string;
  onDismiss: () => void;
  onCancel: () => void;
  onSave: () => void;
}) {
  const dirty = delta.added + delta.removed > 0;
  return (
    <AdminBottomSheet
      title={copy.assignUsers}
      subtitle={permissionName}
      subtitleCode={permissionCode}
      closeLabel={copy.close}
      onDismiss={onDismiss}
      dismissable={!saving}
      header={(
        <SheetToolbar
          p={p}
          search={search}
          onSearch={onSearch}
          placeholder={copy.assignUsersSearch}
          onlySelected={onlySelected}
          onToggleOnlySelected={onToggleOnlySelected}
          countLabel={interpolatePermissionCopy(copy.selectedUsers, { count: selectedCount })}
        />
      )}
      notice={notice ? <NoticeBox tone="red" icon="alert-circle-outline">{notice}</NoticeBox> : undefined}
      footer={<SheetFooter p={p} saving={saving} canSave={canWrite && dirty} label={saveLabel(p, delta)} onCancel={onCancel} onSave={onSave} />}
    >
      {loading ? (
        <ActivityIndicator style={styles.loader} />
      ) : error ? (
        <AdminError error={error} onRetry={onRetry} />
      ) : users.length === 0 ? (
        <AdminEmpty text={copy.noUsers} />
      ) : (
        <View style={styles.card}>
          {users.map((user, index) => {
            const checked = selectedGuids.includes(user.userGUID);
            const name = formatPermissionUserName(user);
            return (
              <Pressable
                key={user.userGUID}
                accessibilityRole="checkbox"
                accessibilityLabel={name}
                accessibilityState={{ checked, disabled: !canWrite }}
                disabled={!canWrite}
                onPress={() => onToggle(user.userGUID)}
                style={[styles.row, index > 0 && styles.divider]}
              >
                <View style={styles.rowText}>
                  <View style={styles.nameRow}>
                    <Text style={styles.name} numberOfLines={1}>{name}</Text>
                    {!user.isActive ? <PeopleTag label={copy.disabled} tone="gray" /> : null}
                  </View>
                  {user.email ? <Text style={styles.sub} numberOfLines={1}>{user.email}</Text> : null}
                </View>
                <AdminCheckBox checked={checked} disabled={!canWrite} />
              </Pressable>
            );
          })}
        </View>
      )}
      {!loading && !error && hasMore ? (
        <Button compact loading={loadingMore} disabled={loadingMore} onPress={onLoadMore}>{copy.loadMoreUsers}</Button>
      ) : null}
    </AdminBottomSheet>
  );
}

const styles = StyleSheet.create({
  toolbar: { alignItems: "center", flexDirection: "row", justifyContent: "space-between", paddingHorizontal: 2 },
  count: { color: C.textSecondary, fontSize: 13 },
  onlySelected: { color: C.action, fontSize: 13, fontWeight: "700" },
  cancelButton: { borderRadius: 8, flex: 1 },
  saveButton: { borderRadius: 8, flex: 1 },
  loader: { marginVertical: 24 },
  card: { backgroundColor: C.white, borderColor: C.outlineMuted, borderRadius: 12, borderWidth: 1, overflow: "hidden" },
  row: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 9 },
  divider: { borderTopColor: C.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  rowText: { flex: 1, gap: 2, minWidth: 0 },
  nameRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  name: { color: C.textPrimary, flexShrink: 1, fontSize: 14.5, fontWeight: "700" },
  sub: { color: C.textSecondary, fontSize: 12.5 },
});
