import { useEffect, useState, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { useQuery } from "@tanstack/react-query";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { OptionChipWrap } from "@/modules/people/people-frame";
import { NoticeBox, SegmentTabs } from "@/modules/people/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  ADMIN_SORT_ORDER,
  buildAdminFilterKey,
  buildAdminUserQuery,
  isAdminSheetDefault,
  resetAdminSheetFilters,
  type AdminSortKey,
  type AdminStatusFilter,
  type AdminUserFilters,
} from "./admin-user-list-logic";
import { fetchIdentityUsers } from "./api";

export interface FilterOption {
  value: string;
  label: string;
}

const STATUS_ORDER: readonly AdminStatusFilter[] = ["all", "active", "inactive"];

/**
 * 「筛选与排序」底部弹层（设计稿 A1④）：分店 / 角色 / 状态 / 排序一次选好，底部按钮直接显示结果人数。
 * 取代原来的两个居中单选框。选项（分店、角色）加载失败时，错误显示在弹层里，不会被弹层挡住。
 * 弹层里的修改先放在草稿里，点「查看 N 人」才生效；搜索关键词在页面上，不属于弹层，重置时保留。
 */
export function AdminUserFilterSheet({
  visible,
  filters,
  actorKey,
  allowed,
  storeOptions,
  roleOptions,
  showStoreSection,
  showRoleSection,
  storesError,
  rolesError,
  onRetry,
  onApply,
  onDismiss,
}: {
  visible: boolean;
  /** 当前已生效的筛选（含搜索关键词）。 */
  filters: AdminUserFilters;
  actorKey: string;
  allowed: boolean;
  storeOptions: FilterOption[];
  roleOptions: FilterOption[];
  showStoreSection: boolean;
  showRoleSection: boolean;
  storesError: boolean;
  rolesError: boolean;
  onRetry: () => void;
  onApply: (filters: AdminUserFilters) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["people", "common"]);
  const [draft, setDraft] = useState<AdminUserFilters>(filters);

  // 每次打开都从当前已生效的筛选开始，上次没点「查看」的草稿不保留。
  useEffect(() => {
    if (visible) setDraft(filters);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 只在打开时同步；打开期间 filters 不会变化
  }, [visible]);

  // 按草稿预取结果人数（pageSize=1 只取 total）。
  const previewQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "users-count-preview", ...buildAdminFilterKey(draft)],
    enabled: visible && allowed,
    retry: false,
    staleTime: 15_000,
    queryFn: ({ signal }) => fetchIdentityUsers(buildAdminUserQuery(draft, 1, 1), actorKey, signal),
    select: (result) => result.total,
  });

  const applyLabel =
    typeof previewQuery.data === "number"
      ? t("people:sheet.apply", { count: previewQuery.data })
      : t("people:sheet.applyUnknown");

  return (
    <BusinessSheet
      visible={visible}
      title={t("people:sheet.title")}
      onDismiss={onDismiss}
      footer={
        <View style={styles.footer}>
          <Button
            mode="outlined"
            style={styles.footerButton}
            disabled={isAdminSheetDefault(draft)}
            onPress={() => setDraft((current) => resetAdminSheetFilters(current))}
          >
            {t("people:sheet.reset")}
          </Button>
          <Button mode="contained" style={styles.footerButton} onPress={() => onApply(draft)}>
            {applyLabel}
          </Button>
        </View>
      }
    >
      {storesError || rolesError ? (
        <View style={styles.errorBlock}>
          <NoticeBox tone="red" icon="alert-circle-outline">{t("people:sheet.optionsFailed")}</NoticeBox>
          <Button mode="text" compact onPress={onRetry} style={styles.retry}>
            {t("common:actions.retry")}
          </Button>
        </View>
      ) : null}

      {showStoreSection ? (
        <Section title={t("people:sheet.store")}>
          <OptionChipWrap
            items={[{ key: "", label: t("people:sheet.all") }, ...storeOptions.map((option) => ({ key: option.value, label: option.label }))]}
            value={draft.storeGuid}
            onChange={(storeGuid) => setDraft((current) => ({ ...current, storeGuid }))}
          />
        </Section>
      ) : null}

      {showRoleSection ? (
        <Section title={t("people:sheet.role")}>
          <OptionChipWrap
            items={[{ key: "", label: t("people:sheet.all") }, ...roleOptions.map((option) => ({ key: option.value, label: option.label }))]}
            value={draft.roleGuid}
            onChange={(roleGuid) => setDraft((current) => ({ ...current, roleGuid }))}
          />
        </Section>
      ) : null}

      <Section title={t("people:sheet.status")}>
        <SegmentTabs
          items={STATUS_ORDER.map((key) => ({ key, label: t(`people:admin.status.${key}`) }))}
          value={draft.status}
          onChange={(status) => setDraft((current) => ({ ...current, status }))}
        />
      </Section>

      <Section title={t("people:sheet.sort")}>
        <SegmentTabs
          items={ADMIN_SORT_ORDER.map((key: AdminSortKey) => ({ key, label: t(`people:admin.sort.${key}`) }))}
          value={draft.sort}
          onChange={(sort) => setDraft((current) => ({ ...current, sort }))}
        />
      </Section>
    </BusinessSheet>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  footer: { flexDirection: "row", gap: 10, paddingBottom: HB_SPACING.xs },
  footerButton: { flex: 1 },
  errorBlock: { gap: 4 },
  retry: { alignSelf: "flex-start" },
  section: { gap: 8 },
  sectionTitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
});
