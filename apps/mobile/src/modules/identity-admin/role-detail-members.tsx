import { useState } from "react";
import { FlatList, Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, IconButton, Text } from "react-native-paper";
import type zhRoleCopy from "@/locales/zh/identityRoles.json";
import { NoticeBox, PeopleTag, PersonAvatar, PersonRow, resolvePersonName } from "@/modules/people";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { AdminBottomSheet } from "./admin-bottom-sheet";
import { AdminCheckBox } from "./admin-visuals";
import { fillCopy, type PeopleRolesCopy } from "./people-roles-copy";
import type { IdentityRoleUser, IdentityUser } from "./types";
import { AdminEmpty, AdminError, SearchField } from "./ui";
import { quoteName } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { UserPickerFilterBar, UserPickerPage, useUserPickerPageTitle, type PickerOption } from "./user-picker-filter-bar";
import {
  USER_PICKER_SEARCH_DELAY_MS,
  hasActiveUserPickerFilters,
  type UserPickerFilters,
  type UserPickerPageKind,
} from "./user-picker-logic";

type RoleCopy = typeof zhRoleCopy;

type PersonLike = Pick<IdentityUser, "userGUID" | "username" | "email" | "fullName" | "givenName" | "middleName" | "familyName" | "nickname" | "isActive">;

function resolveMember(member: PersonLike) {
  return resolvePersonName({
    fullName: member.fullName,
    givenName: member.givenName,
    middleName: member.middleName,
    familyName: member.familyName,
    nickname: member.nickname,
    username: member.username,
  });
}

/** 「@账号 · 邮箱」：没有邮箱只显示账号。 */
function memberMeta(member: PersonLike) {
  return [member.username ? `@${member.username}` : "", member.email].filter(Boolean).join(" · ");
}

// ───────────────────────── 成员段 ─────────────────────────

export function RoleMembersSection({
  legacy,
  p,
  search,
  onSearch,
  members,
  total,
  loading,
  loadingMore,
  hasMore,
  onLoadMore,
  error,
  onRetry,
  canManage,
  derived,
  onAdd,
  onRemove,
  onOpen,
}: {
  legacy: RoleCopy;
  p: PeopleRolesCopy;
  search: string;
  onSearch: (value: string) => void;
  members: IdentityRoleUser[];
  total: number;
  loading: boolean;
  loadingMore: boolean;
  hasMore: boolean;
  onLoadMore: () => void;
  error: unknown;
  onRetry: () => void;
  /** 有 Roles.ManageUsers、写入未冻结、未在保存，且不是店长派生角色 */
  canManage: boolean;
  /** 店长角色：成员由「管理」分店派生，只读并说明原因 */
  derived: boolean;
  onAdd: () => void;
  onRemove: (member: IdentityRoleUser) => void;
  /** 点成员进入人员详情 */
  onOpen: (member: IdentityRoleUser) => void;
}) {
  return (
    <View style={styles.flex}>
      <View style={styles.tools}>
        <View style={styles.flex}>
          <SearchField value={search} onChange={onSearch} placeholder={p.membersSearch} />
        </View>
        {canManage ? (
          <IconButton icon="account-plus-outline" accessibilityLabel={legacy.addMembers} onPress={onAdd} />
        ) : null}
      </View>
      <View style={styles.hints}>
        {derived ? <NoticeBox tone="violet" icon="source-branch">{p.derivedMembersHint}</NoticeBox> : null}
        {!derived ? <NoticeBox tone="blue">{p.membersImmediateHint}</NoticeBox> : null}
        <Text style={styles.count}>{fillCopy(p.membersCount, { count: total })}</Text>
      </View>
      {loading ? (
        <ActivityIndicator style={styles.loader} />
      ) : error ? (
        <AdminError error={error} onRetry={onRetry} />
      ) : members.length === 0 ? (
        <AdminEmpty text={legacy.noMembers} />
      ) : (
        <View style={styles.card}>
          <FlatList
            style={styles.list}
            data={members}
            keyExtractor={(member) => member.userGUID}
            keyboardShouldPersistTaps="handled"
            ListFooterComponent={hasMore ? (
              <Button loading={loadingMore} disabled={loadingMore} onPress={onLoadMore}>{legacy.loadMore}</Button>
            ) : null}
            renderItem={({ item, index }) => {
              const name = resolveMember(item);
              return (
                <PersonRow
                  first={index === 0}
                  avatar={<PersonAvatar initials={name.initials} seed={item.userGUID} size={38} disabled={!item.isActive} />}
                  name={name.primary}
                  nickname={name.nickname}
                  meta={memberMeta(item)}
                  tags={item.isActive ? undefined : <PeopleTag label={legacy.disabled} tone="gray" />}
                  accessibilityLabel={name.primary}
                  onPress={() => onOpen(item)}
                  right={canManage ? (
                    <IconButton
                      icon="account-minus-outline"
                      size={20}
                      iconColor={C.danger}
                      accessibilityLabel={`${legacy.remove} ${name.primary}`}
                      onPress={() => onRemove(item)}
                    />
                  ) : undefined}
                />
              );
            }}
          />
        </View>
      )}
    </View>
  );
}

// ───────────────────────── 添加成员弹层 ─────────────────────────

export function RoleAddMembersSheet({
  legacy,
  p,
  roleName,
  search,
  onSearch,
  users,
  loading,
  error,
  onRetry,
  hasMore,
  loadingMore,
  onLoadMore,
  selected,
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
  onCancel,
  onSubmit,
  submitting,
  notice,
}: {
  legacy: RoleCopy;
  p: PeopleRolesCopy;
  roleName: string;
  search: string;
  onSearch: (value: string) => void;
  users: PersonLike[];
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  hasMore: boolean;
  loadingMore: boolean;
  onLoadMore: () => void;
  selected: readonly string[];
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
  onCancel: () => void;
  onSubmit: () => void;
  submitting: boolean;
  /** 添加失败的原因：原生 Snackbar 在弹层打开时会被盖住，必须就地显示 */
  notice?: string;
}) {
  const { t } = useAppTranslation("people");
  // 二级选择页（分店 / 角色 / 排序）在同一个弹层里原地切换，不叠第二个弹层。
  const [page, setPage] = useState<UserPickerPageKind>("main");
  const pageTitle = useUserPickerPageTitle(page === "main" ? "store" : page);
  const filtered = hasActiveUserPickerFilters(filters) || search.trim() !== "";
  if (page !== "main") {
    return (
      <AdminBottomSheet
        title={pageTitle}
        closeLabel={legacy.cancel}
        onDismiss={onCancel}
        dismissable={!submitting}
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
  return (
    <AdminBottomSheet
      title={legacy.addMembers}
      subtitle={roleName}
      closeLabel={legacy.cancel}
      onDismiss={onCancel}
      dismissable={!submitting}
      fullHeight
      header={(
        <>
          <SearchField value={search} onChange={onSearch} placeholder={legacy.availableSearch} emitDelayMs={USER_PICKER_SEARCH_DELAY_MS} />
          <UserPickerFilterBar
            filters={filters}
            storeOptions={storeOptions}
            roleOptions={roleOptions}
            showStore={showStoreFilter}
            showRole={showRoleFilter}
            disabled={false}
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
      notice={notice ? <NoticeBox tone="red" icon="alert-circle-outline">{notice}</NoticeBox> : undefined}
      footer={(
        <>
          <Button mode="outlined" style={styles.footerCancel} disabled={submitting} onPress={onCancel}>{legacy.cancel}</Button>
          <Button mode="contained" style={styles.footerSubmit} disabled={selected.length === 0 || submitting} loading={submitting} onPress={onSubmit}>
            {fillCopy(legacy.addSelected, { count: selected.length })}
          </Button>
        </>
      )}
    >
      {loading ? (
        <ActivityIndicator style={styles.loader} />
      ) : error ? (
        <AdminError error={error} onRetry={onRetry} />
      ) : users.length === 0 && !hasMore ? (
        <>
          <AdminEmpty text={legacy.noAvailableUsers} />
          {filtered ? (
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
          ) : null}
        </>
      ) : (
        <>
          <View style={styles.sheetCard}>
            {users.map((user, index) => {
              const checked = selected.includes(user.userGUID);
              const name = resolveMember(user);
              return (
                <Pressable
                  key={user.userGUID}
                  accessibilityRole="checkbox"
                  accessibilityLabel={name.primary}
                  accessibilityState={{ checked, disabled: submitting }}
                  disabled={submitting}
                  onPress={() => onToggle(user.userGUID)}
                  style={[styles.sheetRow, index > 0 && styles.divider]}
                >
                  <View style={styles.sheetText}>
                    <Text style={styles.sheetName} numberOfLines={1}>
                      {name.primary}
                      {name.nickname ? <Text style={styles.sheetNickname}>{`  ${quoteName(name.nickname)}`}</Text> : null}
                    </Text>
                    <Text style={styles.sheetMeta} numberOfLines={1}>{memberMeta(user)}</Text>
                  </View>
                  <AdminCheckBox checked={checked} disabled={submitting} />
                </Pressable>
              );
            })}
          </View>
          {hasMore ? <Button loading={loadingMore} disabled={loadingMore} onPress={onLoadMore}>{legacy.loadMore}</Button> : null}
        </>
      )}
    </AdminBottomSheet>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  tools: { alignItems: "center", flexDirection: "row", paddingHorizontal: 16, paddingTop: 12 },
  hints: { gap: 8, paddingBottom: 8, paddingHorizontal: 16 },
  count: { color: C.textSecondary, fontSize: 12.5, fontWeight: "700", paddingHorizontal: 4 },
  loader: { marginTop: 32 },
  card: {
    backgroundColor: C.white,
    borderColor: C.outlineMuted,
    borderRadius: 12,
    borderWidth: 1,
    flexShrink: 1,
    marginBottom: 16,
    marginHorizontal: 16,
    overflow: "hidden",
  },
  list: { flexGrow: 0 },
  divider: { borderTopColor: C.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  footerCancel: { borderRadius: 8, flex: 1 },
  footerSubmit: { borderRadius: 8, flex: 2 },
  sheetCard: { backgroundColor: C.white, borderColor: C.outlineMuted, borderRadius: 12, borderWidth: 1, overflow: "hidden" },
  sheetRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 8 },
  sheetText: { flex: 1, gap: 2, minWidth: 0 },
  sheetName: { color: C.textPrimary, fontSize: 14.5, fontWeight: "700" },
  sheetNickname: { color: "#667085", fontSize: 12.5, fontWeight: "600" },
  sheetMeta: { color: C.textSecondary, fontSize: 12.5 },
  optionsFailed: { color: C.danger, fontSize: 12.5 },
});
