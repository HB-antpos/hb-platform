import { memo } from "react";
import { FlatList, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Switch, Text, TextInput } from "react-native-paper";
import type zhRoleCopy from "@/locales/zh/identityRoles.json";
import { FilterChipRow, InfoCard, KvRow, NoticeBox, SectionTitle, SegmentTabs } from "@/modules/people";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { ADMIN_MONO_FONT, AdminCheckBox } from "./admin-visuals";
import type { PeopleRolesCopy } from "./people-roles-copy";
import { isGroupFullySelected, type PermissionView, type RoleKind } from "./role-detail-logic";
import type { RoleDraftErrors, RoleDraft, RoleMenuPreviewItem } from "./role-logic";
import type { IdentityRoleDetail } from "./types";
import { AdminEmpty, AdminError, SearchField } from "./ui";
import { identityDate } from "./user-hooks";

type RoleCopy = typeof zhRoleCopy;

// ───────────────────────── 资料 ─────────────────────────

export function RoleProfileSection({
  legacy,
  p,
  draft,
  onChange,
  errors,
  readOnly,
  nameLocked,
  kind,
  role,
}: {
  legacy: RoleCopy;
  p: PeopleRolesCopy;
  draft: RoleDraft;
  onChange: (draft: RoleDraft) => void;
  errors: RoleDraftErrors;
  /** 保存中 / 写入冻结 / 没有编辑权限 */
  readOnly: boolean;
  /** 系统角色与店长角色的名称只读（后端靠名称识别它们） */
  nameLocked: boolean;
  kind: RoleKind;
  role?: IdentityRoleDetail;
}) {
  const nameError = errors.roleName === "required" ? legacy.validationRequired
    : errors.roleName === "tooShort" ? legacy.validationTooShort
    : errors.roleName === "tooLong" ? legacy.validationNameTooLong : "";
  return (
    <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
      {kind.isSystem ? <NoticeBox tone="violet" icon="shield-lock-outline">{p.systemRoleHint}</NoticeBox> : null}
      {kind.isDerived ? <NoticeBox tone="violet" icon="source-branch">{p.derivedRoleHint}</NoticeBox> : null}
      <InfoCard style={styles.formCard}>
        <TextInput
          mode="outlined"
          label={legacy.roleName}
          placeholder={legacy.roleNamePlaceholder}
          value={draft.roleName}
          disabled={readOnly || nameLocked}
          maxLength={50}
          error={Boolean(nameError)}
          onChangeText={(roleName) => onChange({ ...draft, roleName })}
        />
        {nameError ? <Text style={styles.errorText}>{nameError}</Text> : null}
        <TextInput
          mode="outlined"
          label={legacy.description}
          placeholder={legacy.descriptionPlaceholder}
          value={draft.description}
          disabled={readOnly}
          multiline
          maxLength={200}
          error={Boolean(errors.description)}
          onChangeText={(description) => onChange({ ...draft, description })}
        />
        {errors.description ? <Text style={styles.errorText}>{legacy.validationDescriptionTooLong}</Text> : null}
      </InfoCard>
      <InfoCard>
        <View style={styles.switchRow}>
          <View style={styles.switchText}>
            <Text style={styles.switchTitle}>{legacy.status}</Text>
            <Text style={styles.muted}>{draft.isActive ? legacy.active : legacy.disabled}</Text>
          </View>
          <Switch
            value={draft.isActive}
            disabled={readOnly}
            accessibilityLabel={legacy.status}
            onValueChange={(isActive) => onChange({ ...draft, isActive })}
          />
        </View>
      </InfoCard>
      {role ? (
        <InfoCard>
          <KvRow first label={legacy.createdAt} value={identityDate(role.createdAt)} />
          <KvRow label={legacy.updatedAt} value={identityDate(role.updatedAt)} />
        </InfoCard>
      ) : null}
    </ScrollView>
  );
}

// ───────────────────────── 权限 ─────────────────────────

/** 单行权限：名称 + 等宽编码 + 右侧勾选框。memo 化后勾选一行只重绘这一行，312 项也不卡。 */
const PermissionRow = memo(function PermissionRow({
  code,
  name,
  checked,
  readOnly,
  first,
  onToggle,
}: {
  code: string;
  name: string;
  checked: boolean;
  readOnly: boolean;
  first: boolean;
  onToggle: (code: string) => void;
}) {
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityLabel={`${name} ${code}`}
      accessibilityState={{ checked, disabled: readOnly }}
      disabled={readOnly}
      onPress={() => onToggle(code)}
      style={[styles.permRow, !first && styles.divider]}
    >
      <View style={styles.permText}>
        {name && name !== code ? <Text style={styles.permName} numberOfLines={2}>{name}</Text> : null}
        <Text style={styles.permCode} numberOfLines={1}>{code}</Text>
      </View>
      <AdminCheckBox checked={checked} disabled={readOnly} />
    </Pressable>
  );
});

export function RolePermissionsSection({
  p,
  legacy,
  view,
  loading,
  error,
  onRetry,
  selected,
  readOnly,
  readOnlyNotice,
  search,
  onSearch,
  onlySelected,
  onOnlySelected,
  onToggle,
  onToggleGroup,
}: {
  p: PeopleRolesCopy;
  legacy: RoleCopy;
  view: PermissionView<{ name: string; displayName: string }> | null;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  selected: ReadonlySet<string>;
  /** 没有权限 / 保存中 / 冻结 / 系统角色（隐式拥有全部权限） */
  readOnly: boolean;
  /** 只读时的说明文案（系统角色、没有权限等） */
  readOnlyNotice?: string;
  search: string;
  onSearch: (value: string) => void;
  onlySelected: boolean;
  onOnlySelected: (value: boolean) => void;
  onToggle: (code: string) => void;
  /** 传入的是「当前可见」的权限码：搜索后只全选命中的那几项 */
  onToggleGroup: (codes: string[]) => void;
}) {
  return (
    <View style={styles.flex}>
      <View style={styles.permToolbar}>
        <View style={styles.permCountRow}>
          <Text style={styles.permCount}>
            {p.selectedPrefix}{" "}
            <Text style={styles.permCountStrong}>{view?.selected ?? 0}</Text>
            {` / ${view?.total ?? 0}`}
          </Text>
          <Text style={styles.onlySelectedLabel}>{p.onlySelected}</Text>
          <Switch
            value={onlySelected}
            onValueChange={onOnlySelected}
            accessibilityLabel={p.onlySelected}
            style={styles.smallSwitch}
          />
        </View>
        <SearchField value={search} onChange={onSearch} placeholder={p.permissionSearch} />
        {readOnlyNotice ? <NoticeBox tone="violet" icon="shield-lock-outline">{readOnlyNotice}</NoticeBox> : null}
      </View>
      {loading ? (
        <ActivityIndicator style={styles.loader} />
      ) : error ? (
        <AdminError error={error} onRetry={onRetry} />
      ) : (
        <FlatList
          data={view?.groups ?? []}
          keyExtractor={(group) => group.category}
          contentContainerStyle={styles.permList}
          keyboardShouldPersistTaps="handled"
          initialNumToRender={4}
          windowSize={7}
          ListEmptyComponent={<AdminEmpty text={onlySelected && !search.trim() ? p.noSelectedPermissions : legacy.noPermissions} />}
          renderItem={({ item: group }) => {
            const codes = group.permissions.map((permission) => permission.name);
            const full = isGroupFullySelected(codes, selected);
            return (
              <View style={styles.group}>
                <SectionTitle
                  title={`${group.displayName} · ${group.selected} / ${group.total}`}
                  actionLabel={readOnly ? undefined : full ? legacy.clearAll : legacy.selectAll}
                  onAction={readOnly ? undefined : () => onToggleGroup(codes)}
                />
                <InfoCard>
                  {group.permissions.map((permission, index) => (
                    <PermissionRow
                      key={permission.name}
                      code={permission.name}
                      name={permission.displayName}
                      checked={selected.has(permission.name)}
                      readOnly={readOnly}
                      first={index === 0}
                      onToggle={onToggle}
                    />
                  ))}
                </InfoCard>
              </View>
            );
          }}
        />
      )}
    </View>
  );
}

// ───────────────────────── 菜单 ─────────────────────────

export type MenuPlatform = "web" | "mobile";
export type MenuFilter = "all" | "visible" | "hidden";

export function RoleMenusSection({
  p,
  legacy,
  items,
  platform,
  onPlatform,
  filter,
  onFilter,
  loading,
  error,
  onRetry,
  readOnly,
  onToggle,
}: {
  p: PeopleRolesCopy;
  legacy: RoleCopy;
  /** 当前平台下、尚未按可见性过滤的全部菜单（用于给筛选芯片计数） */
  items: RoleMenuPreviewItem[];
  platform: MenuPlatform;
  onPlatform: (platform: MenuPlatform) => void;
  filter: MenuFilter;
  onFilter: (filter: MenuFilter) => void;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  /** 没有权限 / 保存中 / 冻结 */
  readOnly: boolean;
  onToggle: (item: RoleMenuPreviewItem, visible: boolean) => void;
}) {
  const visibleCount = items.filter((item) => item.visible).length;
  const shown = items.filter((item) => filter === "all" || (filter === "visible") === item.visible);
  return (
    <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
      {loading ? (
        <ActivityIndicator style={styles.loader} />
      ) : error ? (
        <AdminError error={error} onRetry={onRetry} />
      ) : (
        <>
          <SegmentTabs
            items={[{ key: "web", label: legacy.menuPlatformWeb }, { key: "mobile", label: legacy.menuPlatformMobile }]}
            value={platform}
            onChange={onPlatform}
          />
          <FilterChipRow
            items={[
              { key: "all", label: legacy.menuFilterAll, count: items.length },
              { key: "visible", label: legacy.menuFilterVisible, count: visibleCount },
              { key: "hidden", label: legacy.menuFilterHidden, count: items.length - visibleCount },
            ]}
            value={filter}
            onChange={onFilter}
          />
          <NoticeBox tone="blue">{p.menuSaveHint}</NoticeBox>
          {shown.length === 0 ? (
            <AdminEmpty text={p.noMenus} />
          ) : (
            <InfoCard>
              {shown.map((item, index) => (
                <View key={`${item.platform}:${item.key}`} style={[styles.menuRow, index > 0 && styles.divider]}>
                  <View style={styles.permText}>
                    <Text style={styles.permName} numberOfLines={2}>{item.title}</Text>
                    <Text style={styles.muted}>
                      {item.fixed ? legacy.menuFixed : item.requireAdmin ? legacy.menuAdminOnly : item.visible ? legacy.menuFilterVisible : legacy.menuFilterHidden}
                    </Text>
                  </View>
                  <Switch
                    value={item.visible}
                    disabled={item.readOnly || readOnly}
                    accessibilityLabel={item.title}
                    onValueChange={(visible) => onToggle(item, visible)}
                  />
                </View>
              ))}
            </InfoCard>
          )}
        </>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { gap: 12, padding: 16, paddingBottom: 24 },
  formCard: { gap: 12, padding: 14 },
  errorText: { color: C.danger, fontSize: 12, marginTop: -6 },
  muted: { color: C.textSecondary, fontSize: 12.5, lineHeight: 18 },
  switchRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 60, paddingHorizontal: 14, paddingVertical: 8 },
  switchText: { flex: 1, gap: 2 },
  switchTitle: { color: C.textPrimary, fontSize: 15, fontWeight: "700" },
  divider: { borderTopColor: C.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  loader: { marginTop: 32 },
  permToolbar: { backgroundColor: C.background, gap: 8, paddingBottom: 6, paddingHorizontal: 16, paddingTop: 12 },
  permCountRow: { alignItems: "center", flexDirection: "row", gap: 8, paddingHorizontal: 4 },
  permCount: { color: C.textSecondary, flex: 1, fontSize: 13 },
  permCountStrong: { color: C.textPrimary, fontWeight: "800" },
  onlySelectedLabel: { color: C.textSecondary, fontSize: 13 },
  smallSwitch: { transform: [{ scale: 0.85 }] },
  permList: { gap: 8, paddingBottom: 24, paddingHorizontal: 16 },
  group: { gap: 4 },
  permRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 54, paddingHorizontal: 14, paddingVertical: 8 },
  permText: { flex: 1, gap: 2, minWidth: 0 },
  permName: { color: C.textPrimary, fontSize: 14.5, fontWeight: "600" },
  permCode: { color: C.textSecondary, fontFamily: ADMIN_MONO_FONT, fontSize: 12 },
  menuRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 8 },
});
