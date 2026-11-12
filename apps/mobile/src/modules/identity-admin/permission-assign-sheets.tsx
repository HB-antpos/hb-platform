import { memo, useMemo, useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { NoticeBox, PeopleTag } from "@/modules/people";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { splitStoreLabel } from "@/shared/utils/store-picker-logic";
import { AdminBottomSheet } from "./admin-bottom-sheet";
import { AdminCheckBox } from "./admin-visuals";
import { fillCopy, type PeopleRolesCopy } from "./people-roles-copy";
import { interpolatePermissionCopy, type PermissionCopy } from "./permission-hooks";
import { formatPermissionUserName } from "./permission-logic";
import { formatChangeBadge } from "./role-detail-logic";
import type { IdentityRole } from "./types";
import { AdminEmpty, AdminError, SearchField } from "./ui";
import { UserPickerFilterBar, UserPickerPage, useUserPickerPageTitle, type PickerOption } from "./user-picker-filter-bar";
import {
  USER_PICKER_SEARCH_DELAY_MS,
  highlightParts,
  resolveUserPickerName,
  summarizeNames,
  type UserPickerFilters,
  type UserPickerPageKind,
} from "./user-picker-logic";

/** 「保存（+1）」「保存（+2 −1）」；没有改动时只写「保存」。 */
function saveLabel(p: PeopleRolesCopy, delta: { added: number; removed: number }) {
  const badge = formatChangeBadge(delta.added, delta.removed);
  return badge ? fillCopy(p.saveWithBadge, { badge }) : p.save;
}

/** 搜索框 + 「已选 4 / 12」+「仅看已选」：固定在弹层标题下方，不随列表滚动。 */
function SheetToolbar({
  search,
  onSearch,
  searchDelayMs,
  placeholder,
  countLabel,
  onlySelected,
  onToggleOnlySelected,
  filterSlot,
  afterSlot,
  p,
}: {
  search: string;
  onSearch: (value: string) => void;
  /** 停手多久才通知页面；不传 = 每个按键都通知（分配角色弹层是本地过滤，保持原样）。 */
  searchDelayMs?: number;
  placeholder: string;
  countLabel: ReactNode;
  onlySelected: boolean;
  onToggleOnlySelected: () => void;
  /** 搜索框与计数行之间的筛选条。 */
  filterSlot?: ReactNode;
  /** 计数行下面的提示条。 */
  afterSlot?: ReactNode;
  p: PeopleRolesCopy;
}) {
  return (
    <>
      <SearchField value={search} onChange={onSearch} placeholder={placeholder} emitDelayMs={searchDelayMs} />
      {filterSlot}
      <View style={styles.toolbar}>
        <Text style={styles.count}>{countLabel}</Text>
        <Pressable accessibilityRole="button" accessibilityState={{ selected: onlySelected }} hitSlop={8} onPress={onToggleOnlySelected}>
          <Text style={styles.onlySelected}>{onlySelected ? p.showAll : p.onlySelected}</Text>
        </Pressable>
      </View>
      {afterSlot}
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
  /** 列表接口随行返回的角色名 / 分店名；已选名单里只有 GUID 兜底的用户没有，行内退回显示邮箱。 */
  roleNames?: string[];
  storeNames?: string[];
}

/** 命中关键字的片段加底色（设计图 ③）。 */
function HighlightedText({ text, keyword }: { text: string; keyword: string }) {
  return (
    <>
      {highlightParts(text, keyword).map((part, index) => (
        part.match ? <Text key={index} style={styles.highlight}>{part.text}</Text> : part.text
      ))}
    </>
  );
}

const UserRow = memo(function UserRow({
  user,
  checked,
  canWrite,
  keyword,
  first,
  disabledLabel,
  onToggle,
}: {
  user: AssignUserRow;
  checked: boolean;
  canWrite: boolean;
  keyword: string;
  first: boolean;
  disabledLabel: string;
  onToggle: (userGuid: string) => void;
}) {
  const { t, language } = useAppTranslation("people");
  const name = resolveUserPickerName(user);
  const roles = summarizeNames(user.roleNames);
  const stores = summarizeNames(user.storeNames);
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityLabel={formatPermissionUserName(user)}
      accessibilityState={{ checked, disabled: !canWrite }}
      disabled={!canWrite}
      onPress={() => onToggle(user.userGUID)}
      style={[styles.row, !first && styles.divider]}
    >
      <View style={styles.rowText}>
        <View style={styles.nameRow}>
          <Text style={styles.name} numberOfLines={1}><HighlightedText text={name.primary} keyword={keyword} /></Text>
          {name.secondary ? <Text style={styles.username} numberOfLines={1}><HighlightedText text={name.secondary} keyword={keyword} /></Text> : null}
          {!user.isActive ? <PeopleTag label={disabledLabel} tone="gray" /> : null}
        </View>
        {roles || stores ? (
          <View style={styles.metaRow}>
            {roles ? <PeopleTag label={localizeAccessRoleName(roles.first, language)} tone="gray" /> : null}
            {roles && roles.more > 0 ? <Text style={styles.metaMore}>{t("picker.moreRoles", { count: roles.more })}</Text> : null}
            {roles && stores ? <Text style={styles.dot}>·</Text> : null}
            {stores ? <Text style={styles.sub} numberOfLines={1}>{stores.first}</Text> : null}
            {stores && stores.more > 0 ? <Text style={styles.metaMore}>{t("picker.moreStores", { count: stores.more })}</Text> : null}
          </View>
        ) : user.email ? <Text style={styles.sub} numberOfLines={1}>{user.email}</Text> : null}
      </View>
      <AdminCheckBox checked={checked} disabled={!canWrite} />
    </Pressable>
  );
});

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
  filters,
  onFiltersChange,
  storeOptions,
  roleOptions,
  myStoreGuids,
  showStoreFilter,
  showRoleFilter,
  filterOptionsFailed,
  onRetryFilterOptions,
  total,
  hiddenSelectedCount,
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
  /** 分店 / 角色 / 排序筛选（状态在页面里，因为它们是查询参数）。 */
  filters: UserPickerFilters;
  onFiltersChange: (next: UserPickerFilters) => void;
  storeOptions: readonly PickerOption[];
  roleOptions: readonly PickerOption[];
  myStoreGuids: readonly string[];
  showStoreFilter: boolean;
  showRoleFilter: boolean;
  filterOptionsFailed: boolean;
  onRetryFilterOptions: () => void;
  /** 服务端返回的当前筛选下的总人数；还没取到时为 undefined。 */
  total: number | undefined;
  /** 已勾选但不在当前结果里的人数（结果没加载完时页面传 0）。 */
  hiddenSelectedCount: number;
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
  const { t } = useAppTranslation("people");
  // 二级选择页（分店 / 角色 / 排序）在同一个弹层里原地切换，不叠第二个弹层。
  const [page, setPage] = useState<UserPickerPageKind>("main");
  const pageTitle = useUserPickerPageTitle(page === "main" ? "store" : page);
  const selectedSet = useMemo(() => new Set(selectedGuids), [selectedGuids]);
  const dirty = delta.added + delta.removed > 0;
  const keyword = search.trim();

  if (page !== "main") {
    return (
      <AdminBottomSheet
        title={pageTitle}
        closeLabel={copy.close}
        onDismiss={onDismiss}
        dismissable={!saving}
        onBack={() => setPage("main")}
        backLabel={t("picker.back")}
        fullHeight
      >
        <UserPickerPage
          kind={page}
          filters={filters}
          storeOptions={storeOptions}
          roleOptions={roleOptions}
          myStoreGuids={myStoreGuids}
          onChange={(next) => {
            onFiltersChange({ ...filters, ...next });
            setPage("main");
          }}
        />
      </AdminBottomSheet>
    );
  }

  const countLabel = onlySelected || total === undefined
    ? interpolatePermissionCopy(copy.selectedUsers, { count: selectedCount })
    : `${t("picker.total", { count: total })} · ${t("picker.selectedCount", { count: selectedCount })}`;
  const conditions = [
    filters.storeGuid ? t("picker.conditionStore", { name: pickStoreName(storeOptions, filters.storeGuid) }) : "",
    filters.roleGuid ? t("picker.conditionRole", { name: roleOptions.find((option) => option.value === filters.roleGuid)?.label ?? "" }) : "",
    keyword ? t("picker.conditionKeyword", { keyword }) : "",
  ].filter(Boolean);

  return (
    <AdminBottomSheet
      title={copy.assignUsers}
      subtitle={permissionName}
      subtitleCode={permissionCode}
      closeLabel={copy.close}
      onDismiss={onDismiss}
      dismissable={!saving}
      fullHeight
      header={(
        <SheetToolbar
          p={p}
          search={search}
          onSearch={onSearch}
          searchDelayMs={USER_PICKER_SEARCH_DELAY_MS}
          placeholder={copy.assignUsersSearch}
          onlySelected={onlySelected}
          onToggleOnlySelected={onToggleOnlySelected}
          countLabel={countLabel}
          filterSlot={(
            <>
              <UserPickerFilterBar
                filters={filters}
                storeOptions={storeOptions}
                roleOptions={roleOptions}
                showStore={showStoreFilter}
                showRole={showRoleFilter}
                disabled={onlySelected}
                onOpen={setPage}
                onClear={(kind) => onFiltersChange({ ...filters, ...(kind === "store" ? { storeGuid: "" } : { roleGuid: "" }) })}
              />
              {filterOptionsFailed ? (
                <Pressable accessibilityRole="button" onPress={onRetryFilterOptions}>
                  <Text style={styles.optionsFailed}>{t("sheet.optionsFailed")}</Text>
                </Pressable>
              ) : null}
            </>
          )}
          afterSlot={hiddenSelectedCount > 0 ? (
            <View style={styles.strip}>
              <Text style={styles.stripText} numberOfLines={1}>{t("picker.hiddenSelected", { count: hiddenSelectedCount })}</Text>
              <Pressable accessibilityRole="button" hitSlop={8} onPress={onToggleOnlySelected}>
                <Text style={styles.stripLink}>{t("picker.viewSelected")}</Text>
              </Pressable>
            </View>
          ) : null}
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
        conditions.length > 0 && !onlySelected ? (
          <View style={styles.empty}>
            <View style={styles.emptyIcon}><Icon source="magnify" size={26} color={C.textSecondary} /></View>
            <Text style={styles.emptyTitle}>{copy.noUsers}</Text>
            <Text style={styles.emptyText}>{t("picker.emptyConditions", { conditions: conditions.join(" · ") })}</Text>
            <Text style={styles.emptyText}>{t("picker.emptyHint")}</Text>
            <Button
              mode="outlined"
              compact
              onPress={() => {
                onFiltersChange({ ...filters, storeGuid: "", roleGuid: "" });
                onSearch("");
              }}
            >
              {t("picker.clearFilters")}
            </Button>
          </View>
        ) : (
          <AdminEmpty text={copy.noUsers} />
        )
      ) : (
        <View style={styles.card}>
          {users.map((user, index) => (
            <UserRow
              key={user.userGUID}
              user={user}
              checked={selectedSet.has(user.userGUID)}
              canWrite={canWrite}
              keyword={keyword}
              first={index === 0}
              disabledLabel={copy.disabled}
              onToggle={onToggle}
            />
          ))}
        </View>
      )}
      {!loading && !error && hasMore ? (
        <Button compact loading={loadingMore} disabled={loadingMore} onPress={onLoadMore}>{copy.loadMoreUsers}</Button>
      ) : null}
    </AdminBottomSheet>
  );
}

/** 空结果提示里写「分店 xxx」：选项标签是「名称 · 编号」，只要名称。 */
function pickStoreName(options: readonly PickerOption[], storeGuid: string) {
  const option = options.find((item) => item.value === storeGuid);
  return option ? splitStoreLabel(option.label).name : "";
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
  sub: { color: C.textSecondary, flexShrink: 1, fontSize: 12.5 },
  username: { color: C.textSecondary, flexShrink: 1, fontSize: 12.5 },
  highlight: { backgroundColor: "#FEF0C7" },
  metaRow: { alignItems: "center", flexDirection: "row", gap: 6, overflow: "hidden" },
  metaMore: { color: C.action, fontSize: 12.5, fontWeight: "700" },
  dot: { color: C.outline, fontSize: 12.5 },
  strip: { alignItems: "center", backgroundColor: "#EFF6FF", borderRadius: 8, flexDirection: "row", gap: 8, justifyContent: "space-between", paddingHorizontal: 10, paddingVertical: 7 },
  stripText: { color: C.action, flex: 1, fontSize: 12.5 },
  stripLink: { color: C.action, fontSize: 12.5, fontWeight: "700" },
  optionsFailed: { color: C.danger, fontSize: 12.5 },
  empty: { alignItems: "center", gap: 8, paddingHorizontal: 20, paddingTop: 32 },
  emptyIcon: { alignItems: "center", backgroundColor: C.surfaceMuted, borderRadius: 26, height: 52, justifyContent: "center", width: 52 },
  emptyTitle: { color: C.textPrimary, fontSize: 15, fontWeight: "700" },
  emptyText: { color: C.textSecondary, fontSize: 13, lineHeight: 20, textAlign: "center" },
});
