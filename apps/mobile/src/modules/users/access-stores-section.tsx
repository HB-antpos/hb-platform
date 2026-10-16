import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { InfoCard, NoticeBox } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import {
  filterAccessStores,
  resolveStoreStateOptions,
  type AccessStoreStateOption,
} from "./access-page-logic";
import { getUserAccessStoreState } from "./access-management";
import type {
  AccessStoreOption,
  UserAccessStoreAssignment,
  UserAccessStoreState,
} from "./access-management-types";
import { AccessEmptyText, AccessSearchBox, accessSectionStyles } from "./access-section-ui";

/** 分店多到值得搜索时才显示搜索框（设计稿 5 家分店就有）。 */
const STORE_SEARCH_MIN = 5;

interface AccessStoresSectionProps {
  stores: AccessStoreOption[];
  assignments: UserAccessStoreAssignment[];
  /** 已保存的分配；「收银权限」入口只对已保存的分店开放。 */
  baselineAssignments: UserAccessStoreAssignment[];
  /** 分店分段可编辑且当前没有保存 / 复制在进行。 */
  editable: boolean;
  /** 只有管理员能授予「管理」；店长看不到这一档。 */
  canGrantManage: boolean;
  canManagePos: boolean;
  isAdmin: boolean;
  onChangeState: (storeGuid: string, state: UserAccessStoreState) => void;
  onOpenPos: (store: AccessStoreOption) => void;
}

/**
 * 分店分段：每家分店一行，右侧「未分配 | 查看 | 管理」三档；已分配的分店下方是「收银权限 ›」入口。
 * 店长只会拿到自己管理的分店，并且没有「管理」档。
 */
export function AccessStoresSection({
  stores,
  assignments,
  baselineAssignments,
  editable,
  canGrantManage,
  canManagePos,
  isAdmin,
  onChangeState,
  onOpenPos,
}: AccessStoresSectionProps) {
  const { t } = useAppTranslation(["peopleAccess", "common"]);
  const [query, setQuery] = useState("");
  const filtered = useMemo(() => filterAccessStores(stores, query), [query, stores]);

  if (!stores.length) {
    return (
      <EmptyState
        title={t("stores.emptyTitle")}
        description={t("stores.emptyDescription")}
      />
    );
  }

  const labelOf = (state: UserAccessStoreState) =>
    t(`stores.${state}`);

  return (
    <View style={accessSectionStyles.stack}>
      {stores.length >= STORE_SEARCH_MIN ? (
        <AccessSearchBox
          value={query}
          onChangeText={setQuery}
          placeholder={t("stores.searchPlaceholder")}
        />
      ) : null}
      {filtered.length === 0 ? (
        <AccessEmptyText>{t("stores.noMatch")}</AccessEmptyText>
      ) : (
        <InfoCard>
          {filtered.map((store, index) => {
            const state = getUserAccessStoreState(assignments, store.storeGUID);
            const savedState = getUserAccessStoreState(baselineAssignments, store.storeGUID);
            const options = resolveStoreStateOptions({
              currentState: state,
              canGrantManage,
              editable,
            });
            const storeLabel = store.storeName || store.storeCode;
            return (
              <View
                key={store.storeGUID}
                style={[styles.row, index > 0 && styles.rowDivider]}
                testID={`access-store-row-${store.storeCode}`}
              >
                <View style={styles.head}>
                  <View style={styles.text}>
                    <Text style={styles.name} numberOfLines={1}>{storeLabel}</Text>
                    <Text style={styles.meta} numberOfLines={1}>
                      {store.isActive === false
                        ? `${store.storeCode} · ${t("stores.disabledStore")}`
                        : store.storeCode}
                    </Text>
                  </View>
                  <StoreStateSwitch
                    options={options}
                    value={state}
                    labelOf={labelOf}
                    storeLabel={storeLabel}
                    onChange={(next) => onChangeState(store.storeGUID, next)}
                  />
                </View>
                {canManagePos && state !== "unassigned" ? (
                  savedState === "unassigned" ? (
                    // 还没保存的分配后端不认识这个人，先保存才能设置收银权限。
                    <Text style={styles.posPending}>{t("stores.posAfterSave")}</Text>
                  ) : (
                    <Pressable
                      accessibilityRole="button"
                      accessibilityLabel={`${t("stores.posEntry")} ${storeLabel}`}
                      onPress={() => onOpenPos(store)}
                      hitSlop={8}
                      style={styles.posEntry}
                    >
                      <Text style={styles.posEntryText}>{`${t("stores.posEntry")} ›`}</Text>
                    </Pressable>
                  )
                ) : null}
              </View>
            );
          })}
        </InfoCard>
      )}
      <NoticeBox tone="blue" icon="source-branch">
        {isAdmin ? t("stores.managerNotice") : t("stores.scopeNotice")}
      </NoticeBox>
    </View>
  );
}

/** 「未分配 | 查看 | 管理」三档分段按钮：选中「管理」用主色实心，其余白底。 */
function StoreStateSwitch({
  options,
  value,
  labelOf,
  storeLabel,
  onChange,
}: {
  options: AccessStoreStateOption[];
  value: UserAccessStoreState;
  labelOf: (state: UserAccessStoreState) => string;
  storeLabel: string;
  onChange: (state: UserAccessStoreState) => void;
}) {
  return (
    <View style={styles.switch} accessibilityRole="radiogroup">
      {options.map((option) => {
        const selected = option.state === value;
        const isManage = option.state === "manage";
        return (
          <Pressable
            key={option.state}
            accessibilityRole="radio"
            accessibilityLabel={`${storeLabel} ${labelOf(option.state)}`}
            accessibilityState={{ selected, disabled: option.disabled }}
            disabled={option.disabled}
            onPress={() => {
              if (!selected) onChange(option.state);
            }}
            style={[
              styles.pill,
              selected && (isManage ? styles.pillManage : styles.pillSelected),
              option.disabled && !selected && styles.pillDisabled,
            ]}
          >
            <Text
              style={[
                styles.pillText,
                selected && (isManage ? styles.pillTextManage : styles.pillTextSelected),
                option.disabled && !selected && styles.pillTextDisabled,
              ]}
              numberOfLines={1}
            >
              {labelOf(option.state)}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { gap: 6, paddingHorizontal: 14, paddingVertical: 12 },
  rowDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  head: { alignItems: "center", flexDirection: "row", gap: 10 },
  text: { flex: 1, minWidth: 0 },
  name: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  meta: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  posEntry: { alignSelf: "flex-start", minHeight: 28, justifyContent: "center" },
  posEntryText: { color: HB_COLORS.action, fontSize: 13, fontWeight: "700" },
  posPending: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  switch: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 9,
    flexDirection: "row",
    flexShrink: 0,
    gap: 2,
    padding: 2,
  },
  pill: {
    alignItems: "center",
    borderRadius: 7,
    justifyContent: "center",
    minHeight: 32,
    minWidth: 44,
    paddingHorizontal: 9,
  },
  pillSelected: {
    backgroundColor: HB_COLORS.white,
    elevation: 1,
    shadowColor: HB_COLORS.textPrimary,
    shadowOffset: { height: 1, width: 0 },
    shadowOpacity: 0.12,
    shadowRadius: 2,
  },
  pillManage: { backgroundColor: HB_COLORS.brand },
  pillDisabled: { opacity: 0.45 },
  pillText: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "600" },
  pillTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  pillTextManage: { color: HB_COLORS.white, fontWeight: "800" },
  pillTextDisabled: { color: HB_COLORS.textSecondary },
});
