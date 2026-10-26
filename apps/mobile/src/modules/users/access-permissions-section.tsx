import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Switch, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { InfoCard, NoticeBox, PeopleTag, type PeopleTone } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import {
  countEnabledPermissions,
  filterPermissionCategories,
  resolveAccessPermissionRow,
  type AccessPermissionCounts,
  type AccessPermissionRowState,
} from "./access-page-logic";
import {
  localizeAccessPermission,
  localizeAccessPermissionCategory,
} from "./access-permission-presentation";
import type {
  AccessPermissionCategory,
  CopyPermissionDirection,
  DirectPermissionDraft,
} from "./access-management-types";
import {
  AccessEmptyText,
  AccessRowFrame,
  AccessSearchBox,
  accessSectionStyles,
} from "./access-section-ui";

interface AccessPermissionsSectionProps {
  /** 已去掉收银（POS）权限的分类目录。 */
  categories: AccessPermissionCategory[];
  /** 只含收银（POS）权限的分类：底部单独成组「收银权限 · 账号默认值」（账号级，对所有分店生效）。 */
  posCategories?: AccessPermissionCategory[];
  draft: DirectPermissionDraft;
  counts: AccessPermissionCounts;
  /** 目标是管理员：默认拥有全部权限，开关全部锁定，也不提供复制。 */
  implicitAll: boolean;
  isAdmin: boolean;
  /** 仓库管理：提示文案不同（只能授予自己拥有的、非角色/系统管理类权限）。 */
  isWarehouseManager?: boolean;
  inheritedSourceMap: Map<string, string[]>;
  editable: boolean;
  busy: boolean;
  onToggle: (code: string, checked: boolean) => void;
  onCopy: (direction: CopyPermissionDirection) => void;
}

const SOURCE_TONES: Record<AccessPermissionRowState["kind"], PeopleTone> = {
  implicit: "violet",
  inherited: "gray",
  direct: "blue",
  none: "gray",
};

/** 默认展开：有「直接授权」的分类；一个都没有就展开第一个，避免进来是一排折叠的空白。 */
function defaultExpandedCategories(
  categories: AccessPermissionCategory[],
  draft: DirectPermissionDraft,
) {
  const inherited = new Set(draft.inheritedPermissionCodes);
  const withDirect = categories
    .filter((category) =>
      category.permissions.some(
        (permission) =>
          draft.selectedCodes.includes(permission.name) && !inherited.has(permission.name),
      ),
    )
    .map((category) => category.category);
  const keys = withDirect.length ? withDirect : categories.slice(0, 1).map((item) => item.category);
  return new Set(keys);
}

/**
 * 直接权限分段：顶部「有效 / 角色继承 / 直接授权」三个数字 + 复制入口，
 * 权限按分类折叠分组，每行带来源标签（继承自 X / 直接授权 / 未开放）；继承来的开关置灰，要去角色里改。
 */
export function AccessPermissionsSection({
  categories,
  posCategories = [],
  draft,
  counts,
  implicitAll,
  isAdmin,
  isWarehouseManager = false,
  inheritedSourceMap,
  editable,
  busy,
  onToggle,
  onCopy,
}: AccessPermissionsSectionProps) {
  const { t, language } = useAppTranslation(["peopleAccess", "common"]);
  const [query, setQuery] = useState("");
  const [expanded, setExpanded] = useState<Set<string>>(() =>
    defaultExpandedCategories(categories, draft),
  );
  const searching = query.trim().length > 0;
  const posRows = useMemo(
    () => posCategories.flatMap((category) => category.permissions),
    [posCategories],
  );
  const [posOpen, setPosOpen] = useState(() => {
    const inherited = new Set(draft.inheritedPermissionCodes);
    return posCategories.some((category) =>
      category.permissions.some(
        (permission) => draft.selectedCodes.includes(permission.name) && !inherited.has(permission.name),
      ),
    );
  });
  const separator = t("save.separator");

  const rowOf = useMemo(
    () => (code: string) =>
      resolveAccessPermissionRow({
        draft,
        code,
        implicitAll,
        inheritedRoleNames: inheritedSourceMap.get(code),
      }),
    [draft, implicitAll, inheritedSourceMap],
  );
  const categoryLabel = (category: AccessPermissionCategory) =>
    localizeAccessPermissionCategory(category.category || category.displayName, language);

  const filtered = useMemo(
    () =>
      filterPermissionCategories(categories, query, (permission, category) => {
        const text = localizeAccessPermission(permission, language);
        return `${text.name} ${text.description} ${permission.name} ${localizeAccessPermissionCategory(
          category.category || category.displayName,
          language,
        )}`;
      }),
    [categories, language, query],
  );

  const filteredPosRows = useMemo(
    () =>
      filterPermissionCategories(
        [{ category: "pos", displayName: "pos", permissions: posRows } as AccessPermissionCategory],
        query,
        (permission) => {
          const text = localizeAccessPermission(permission, language);
          return `${text.name} ${text.description} ${permission.name}`;
        },
      ).flatMap((category) => category.permissions),
    [language, posRows, query],
  );

  if (!categories.length && !posCategories.length) {
    return (
      <EmptyState
        title={t("permissions.emptyTitle")}
        description={t("permissions.emptyDescription")}
      />
    );
  }

  const sourceLabel = (row: AccessPermissionRowState) => {
    if (row.kind === "implicit") return t("permissions.source.implicit");
    if (row.kind === "inherited") {
      return row.roleNames.length
        ? t("permissions.source.inherited", { roles: row.roleNames.join(separator) })
        : t("permissions.source.inheritedFallback");
    }
    return t(row.kind === "direct" ? "permissions.source.direct" : "permissions.source.none");
  };

  const toggleCategory = (key: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  return (
    <View style={accessSectionStyles.stack}>
      <InfoCard>
        <View style={styles.summary}>
          <View style={styles.stats}>
            <Stat value={counts.effective} label={t("permissions.summary.effective")} />
            <Stat value={counts.inherited} label={t("permissions.summary.inherited")} />
            <Stat value={counts.direct} label={t("permissions.summary.direct")} accent />
          </View>
          {!implicitAll ? (
            <View style={styles.copyButtons}>
              <Button
                mode="outlined"
                compact
                icon="account-arrow-left-outline"
                disabled={busy}
                onPress={() => onCopy("fromColleague")}
                accessibilityLabel={t("permissions.copyFromColleague")}
              >
                {t("permissions.copyFromColleague")}
              </Button>
              <Button
                mode="outlined"
                compact
                icon="account-arrow-right-outline"
                disabled={busy}
                onPress={() => onCopy("toOthers")}
                accessibilityLabel={t("permissions.copyToOthers")}
              >
                {t("permissions.copyToOthers")}
              </Button>
            </View>
          ) : null}
        </View>
      </InfoCard>

      {!isAdmin ? (
        <NoticeBox tone="blue">
          {t(isWarehouseManager ? "permissions.warehouseLimit" : "permissions.managerLimit")}
        </NoticeBox>
      ) : null}
      {implicitAll ? <NoticeBox tone="violet">{t("permissions.implicitNotice")}</NoticeBox> : null}

      <AccessSearchBox
        value={query}
        onChangeText={setQuery}
        placeholder={t("permissions.searchPlaceholder")}
      />

      {filtered.length === 0 ? (
        <AccessEmptyText>{t("permissions.noMatch")}</AccessEmptyText>
      ) : (
        filtered.map((category) => {
          const full = categories.find((item) => item.category === category.category) ?? category;
          const { enabled, total } = countEnabledPermissions(full.permissions, rowOf);
          const isOpen = searching || expanded.has(category.category);
          const label = categoryLabel(category);
          const title = t("permissions.categoryTitle", { name: label, enabled, total });
          return (
            <View key={category.category} style={styles.group}>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={title}
                accessibilityState={{ expanded: isOpen }}
                onPress={() => toggleCategory(category.category)}
                disabled={searching}
                style={styles.groupHeader}
              >
                <Text style={styles.groupTitle}>{title}</Text>
                {searching ? null : (
                  <Icon
                    source={isOpen ? "chevron-up" : "chevron-down"}
                    size={20}
                    color={HB_COLORS.textSecondary}
                  />
                )}
              </Pressable>
              {isOpen ? (
                <InfoCard>
                  {category.permissions.map((permission, index) => {
                    const row = rowOf(permission.name);
                    const text = localizeAccessPermission(permission, language);
                    const disabled = busy || !editable || row.locked;
                    return (
                      <AccessRowFrame
                        key={permission.name}
                        first={index === 0}
                        testID={`access-permission-row-${permission.name}`}
                      >
                        <View style={styles.rowText}>
                          <Text style={styles.rowName}>{text.name}</Text>
                          <PeopleTag label={sourceLabel(row)} tone={SOURCE_TONES[row.kind]} />
                        </View>
                        <Switch
                          value={row.checked}
                          disabled={disabled}
                          accessibilityLabel={text.name}
                          accessibilityHint={`${sourceLabel(row)}。${text.description}`}
                          onValueChange={(checked) => onToggle(permission.name, checked)}
                        />
                      </AccessRowFrame>
                    );
                  })}
                </InfoCard>
              ) : null}
            </View>
          );
        })
      )}

      {filteredPosRows.length > 0 ? (
        <View style={styles.group} testID="access-pos-account-defaults">
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("permissions.posDefaults.title")}
            accessibilityState={{ expanded: searching || posOpen }}
            onPress={() => setPosOpen((current) => !current)}
            disabled={searching}
            style={styles.groupHeader}
          >
            <Text style={styles.groupTitle}>
              {t("permissions.posDefaults.titleWithCount", {
                enabled: countEnabledPermissions(posRows, rowOf).enabled,
                total: posRows.length,
              })}
            </Text>
            {searching ? null : (
              <Icon
                source={posOpen ? "chevron-up" : "chevron-down"}
                size={20}
                color={HB_COLORS.textSecondary}
              />
            )}
          </Pressable>
          {searching || posOpen ? (
            <>
              <NoticeBox tone="gray" icon="cash-register">{t("permissions.posDefaults.notice")}</NoticeBox>
              <InfoCard>
                {filteredPosRows.map((permission, index) => {
                  const row = rowOf(permission.name);
                  const text = localizeAccessPermission(permission, language);
                  const disabled = busy || !editable || row.locked;
                  return (
                    <AccessRowFrame
                      key={permission.name}
                      first={index === 0}
                      testID={`access-permission-row-${permission.name}`}
                    >
                      <View style={styles.rowText}>
                        <Text style={styles.rowName}>{text.name}</Text>
                        <PeopleTag label={sourceLabel(row)} tone={SOURCE_TONES[row.kind]} />
                      </View>
                      <Switch
                        value={row.checked}
                        disabled={disabled}
                        accessibilityLabel={text.name}
                        accessibilityHint={`${sourceLabel(row)}。${text.description}`}
                        onValueChange={(checked) => onToggle(permission.name, checked)}
                      />
                    </AccessRowFrame>
                  );
                })}
              </InfoCard>
            </>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

function Stat({ value, label, accent = false }: { value: number; label: string; accent?: boolean }) {
  return (
    <View style={styles.stat} accessible accessibilityLabel={`${label} ${value}`}>
      <Text style={[styles.statValue, accent && styles.statAccent]}>{value}</Text>
      <Text style={styles.statLabel} numberOfLines={1}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  summary: { alignItems: "center", flexDirection: "row", gap: 10, padding: 12 },
  stats: { alignItems: "flex-start", flex: 1, flexDirection: "row", gap: 14 },
  stat: { minWidth: 44 },
  statValue: { color: HB_COLORS.textPrimary, fontSize: 21, fontVariant: ["tabular-nums"], fontWeight: "800" },
  statAccent: { color: HB_COLORS.action },
  statLabel: { color: HB_COLORS.textSecondary, fontSize: 11.5 },
  copyButtons: { flexShrink: 0, gap: 6 },
  group: { gap: 6 },
  groupHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 32,
    paddingHorizontal: 4,
  },
  groupTitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "700" },
  rowText: { flex: 1, gap: 4, minWidth: 0 },
  rowName: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "600" },
});
