import { useMemo, useState } from "react";
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { InfoCard, NoticeBox, PeopleTag } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { filterUserAccessRoles } from "./access-management";
import { resolveAccessRoleRow } from "./access-page-logic";
import {
  localizeAccessRoleDescription,
  localizeAccessRoleName,
} from "./access-permission-presentation";
import type { UserAccessRole } from "./access-management-types";
import {
  AccessCheckbox,
  AccessEmptyText,
  AccessRowFrame,
  AccessSearchBox,
  accessSectionStyles,
} from "./access-section-ui";

/** 角色多到值得搜索时才显示搜索框；店长只有「店员」一行，不显示。 */
const ROLE_SEARCH_MIN = 7;

interface AccessRolesSectionProps {
  roles: UserAccessRole[];
  selectedRoleGuids: string[];
  hasManagedStoreAssignment: boolean;
  isAdmin: boolean;
  rolesEditable: boolean;
  storesEditable: boolean;
  canGrantStoreManagement: boolean;
  busy: boolean;
  onToggleRole: (roleGuid: string) => void;
  /** 管理员点「店长」行：引导去分店分段把某家分店设为「管理」。 */
  onGuideManagerGrant: () => void;
}

/**
 * 角色分段：卡片列表，勾选框放右侧。
 * 「店长」行不可勾选（由「管理」分店派生）；高权限角色带红色标签；店长只看得到「店员」。
 */
export function AccessRolesSection({
  roles,
  selectedRoleGuids,
  hasManagedStoreAssignment,
  isAdmin,
  rolesEditable,
  storesEditable,
  canGrantStoreManagement,
  busy,
  onToggleRole,
  onGuideManagerGrant,
}: AccessRolesSectionProps) {
  const { t, language } = useAppTranslation(["peopleAccess", "common"]);
  const [query, setQuery] = useState("");
  const filtered = useMemo(() => filterUserAccessRoles(roles, query), [query, roles]);

  if (!roles.length) {
    return (
      <EmptyState title={t("roles.emptyTitle")} description={t("roles.emptyDescription")} />
    );
  }

  return (
    <View style={accessSectionStyles.stack}>
      {!isAdmin ? <NoticeBox tone="blue">{t("roles.managerLimit")}</NoticeBox> : null}
      {roles.length >= ROLE_SEARCH_MIN ? (
        <AccessSearchBox
          value={query}
          onChangeText={setQuery}
          placeholder={t("roles.searchPlaceholder")}
        />
      ) : null}
      {filtered.length === 0 ? (
        <AccessEmptyText>{t("roles.noMatch")}</AccessEmptyText>
      ) : (
        <InfoCard>
          {filtered.map((role, index) => {
            const row = resolveAccessRoleRow({
              role,
              selectedRoleGuids,
              hasManagedStoreAssignment,
              isAdmin,
              rolesEditable,
              storesEditable,
              canGrantStoreManagement,
              busy,
            });
            const name = localizeAccessRoleName(role.roleName, language);
            // 店长行的说明告诉用户怎么得到 / 取消这个角色；其余角色用角色说明。
            const description = row.derived
              ? t(row.selected ? "roles.derivedHintOn" : "roles.derivedHintOff")
              : localizeAccessRoleDescription(role.roleName, role.description, language);
            const onPress = () => {
              if (row.derived) {
                // 店长行不能勾选；管理员点它是引导，其他人保持锁定。
                if (!busy && row.canGuideManagerGrant) onGuideManagerGrant();
                return;
              }
              if (row.disabled) return;
              onToggleRole(role.roleGUID);
            };
            return (
              <AccessRowFrame
                key={role.roleGUID}
                first={index === 0}
                onPress={onPress}
                disabled={row.derived ? !row.canGuideManagerGrant || busy : row.disabled}
                accessibilityRole="checkbox"
                accessibilityLabel={name}
                accessibilityHint={description}
                accessibilityState={{ checked: row.selected, disabled: row.disabled }}
                testID={`access-role-row-${role.roleGUID}`}
              >
                <View style={styles.text}>
                  <View style={styles.nameLine}>
                    <Text style={styles.name} numberOfLines={1}>{name}</Text>
                    {row.derived ? (
                      <PeopleTag label={t("roles.derived")} tone="violet" icon="source-branch" />
                    ) : null}
                    {row.highPrivilege ? (
                      <PeopleTag label={t("roles.highPrivilege")} tone="red" />
                    ) : null}
                  </View>
                  <Text style={styles.desc} numberOfLines={2}>{description}</Text>
                </View>
                {/* 派生角色的复选框本身永远不可勾选：已派生出来显示为勾选，没有就显示锁。 */}
                <AccessCheckbox
                  checked={row.selected}
                  locked={row.derived}
                  disabled={row.disabled || row.derived}
                />
              </AccessRowFrame>
            );
          })}
        </InfoCard>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  text: { flex: 1, gap: 2, minWidth: 0 },
  nameLine: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  name: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 15, fontWeight: "700" },
  desc: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
});
