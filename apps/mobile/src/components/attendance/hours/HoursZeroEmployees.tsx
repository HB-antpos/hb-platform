// 工时统计列表末尾的「N 人本周 0 工时，已收起 ＋添加」条，以及点「添加」弹出的勾选清单。
// 只负责展示与勾选，名单存取在 use-pinned-hours-employees.ts。
import { useEffect, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { hasScheduledDay } from "@/modules/attendance/payroll-hours-visibility";
import type { PayrollHoursEmployee } from "@/modules/attendance/payroll-hours-types";
import { listSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { employeeBadges } from "./HoursEmployeeCard";
import { HoursAvatar } from "./hours-ui";

/** 头像最多叠 4 个，多的用「+N」。 */
const STACK_LIMIT = 4;

function firstName(name: string) {
  return name.trim().split(/\s+/)[0] || name;
}

export function HoursHiddenEmployeesBar({ employees, weeks, onAdd }: { employees: PayrollHoursEmployee[]; weeks: 1 | 2; onAdd: () => void }) {
  const { t, language } = useAppTranslation(["attendanceHours", "common"]);
  const period = weeks === 1 ? t("zeroHours.periodWeek") : t("zeroHours.periodFortnight");
  const shown = employees.slice(0, STACK_LIMIT);
  const extra = employees.length - shown.length;
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={t("zeroHours.addA11y")} onPress={onAdd} style={({ pressed }) => [styles.bar, pressed ? styles.barPressed : null]}>
      <View style={styles.stack}>
        {shown.map((employee, index) => (
          <View key={employee.userGuid} style={[styles.stackItem, index > 0 ? styles.stackOverlap : null]}>
            <HoursAvatar name={employee.name} seed={employee.userGuid} size={26} />
          </View>
        ))}
        {extra > 0 ? (
          <View style={[styles.stackItem, styles.stackOverlap, styles.stackMore]}>
            <Text style={styles.stackMoreText}>{`+${extra}`}</Text>
          </View>
        ) : null}
      </View>
      <View style={styles.barText}>
        <Text style={styles.barTitle} numberOfLines={1}>
          {t("zeroHours.hiddenTitle", { count: employees.length, period })}
        </Text>
        <Text style={styles.barNames} numberOfLines={2}>
          {employees.map((employee) => firstName(employee.name)).join(listSeparator(language))}
        </Text>
      </View>
      <View style={styles.addPill}>
        <MaterialCommunityIcons name="plus" size={16} color={HB_COLORS.action} />
        <Text style={styles.addPillText}>{t("zeroHours.add")}</Text>
      </View>
    </Pressable>
  );
}

export function HoursAddZeroEmployeesSheet({
  visible,
  employees,
  weeks,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  employees: PayrollHoursEmployee[];
  weeks: 1 | 2;
  onDismiss: () => void;
  onConfirm: (userGuids: string[]) => void;
}) {
  const { t } = useAppTranslation(["attendanceHours", "common"]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // 每次打开都从空选开始。
  useEffect(() => {
    if (visible) setSelected(new Set());
  }, [visible]);

  const period = weeks === 1 ? t("zeroHours.periodWeek") : t("zeroHours.periodFortnight");
  const allSelected = employees.length > 0 && employees.every((employee) => selected.has(employee.userGuid));
  const toggle = (guid: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(guid)) next.delete(guid);
      else next.add(guid);
      return next;
    });

  return (
    <BusinessSheet
      visible={visible}
      title={t("zeroHours.sheetTitle")}
      subtitle={t("zeroHours.sheetHint", { period })}
      onDismiss={onDismiss}
      footer={
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerButton} onPress={onDismiss}>
            {t("common:actions.cancel")}
          </Button>
          <Button
            mode="contained"
            style={styles.footerButton}
            disabled={selected.size === 0}
            onPress={() => onConfirm(employees.filter((employee) => selected.has(employee.userGuid)).map((employee) => employee.userGuid))}
          >
            {selected.size ? t("zeroHours.confirm", { count: selected.size }) : t("zeroHours.confirmNone")}
          </Button>
        </View>
      }
    >
      <View style={styles.selectRow}>
        <Text style={styles.selectCount}>{t("zeroHours.selected", { count: selected.size })}</Text>
        <Pressable
          accessibilityRole="button"
          hitSlop={8}
          onPress={() => setSelected(allSelected ? new Set() : new Set(employees.map((employee) => employee.userGuid)))}
        >
          <Text style={styles.selectAll}>{allSelected ? t("zeroHours.clearAll") : t("zeroHours.selectAll")}</Text>
        </Pressable>
      </View>
      {employees.map((employee) => {
        const checked = selected.has(employee.userGuid);
        const scheduled = hasScheduledDay(employee);
        const badges = employeeBadges(employee, t).map((badge) => badge.label);
        const sub = [...badges, scheduled ? t("zeroHours.scheduledNoPunch") : t("zeroHours.noActivity")].join(" · ");
        return (
          <Pressable
            key={employee.userGuid}
            accessibilityRole="checkbox"
            accessibilityState={{ checked }}
            accessibilityLabel={`${employee.name} ${sub}`}
            onPress={() => toggle(employee.userGuid)}
            style={styles.item}
          >
            <View style={[styles.checkbox, checked ? styles.checkboxOn : null]}>
              {checked ? <MaterialCommunityIcons name="check" size={15} color={HB_COLORS.white} /> : null}
            </View>
            <HoursAvatar name={employee.name} seed={employee.userGuid} size={34} />
            <View style={styles.itemText}>
              <Text style={styles.itemName} numberOfLines={1}>
                {employee.name}
              </Text>
              <Text style={[styles.itemSub, scheduled ? styles.itemSubScheduled : null]} numberOfLines={1}>
                {sub}
              </Text>
            </View>
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  bar: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  barPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  stack: { flexDirection: "row", alignItems: "center" },
  stackItem: { borderRadius: 15, borderWidth: 2, borderColor: HB_COLORS.white },
  stackOverlap: { marginLeft: -9 },
  stackMore: { width: 30, height: 30, alignItems: "center", justifyContent: "center", backgroundColor: HB_COLORS.surfaceMuted },
  stackMoreText: { fontSize: 11, fontWeight: "700", color: HB_COLORS.textSecondary },
  barText: { flex: 1, minWidth: 0 },
  barTitle: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  barNames: { fontSize: 12.5, color: HB_COLORS.textSecondary, marginTop: 1 },
  addPill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    paddingHorizontal: 10,
    height: 32,
    borderRadius: 8,
    backgroundColor: "#EFF8FF",
  },
  addPillText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.action },
  selectRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingBottom: 6,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  selectCount: { fontSize: 13.5, color: HB_COLORS.textSecondary },
  selectAll: { fontSize: 13.5, fontWeight: "600", color: HB_COLORS.action },
  item: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  checkbox: {
    width: 22,
    height: 22,
    borderRadius: 6,
    borderWidth: 2,
    borderColor: HB_COLORS.outline,
    alignItems: "center",
    justifyContent: "center",
  },
  checkboxOn: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  itemText: { flex: 1, minWidth: 0 },
  itemName: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  itemSub: { fontSize: 12.5, color: HB_COLORS.textSecondary, marginTop: 1 },
  itemSubScheduled: { color: HB_COLORS.action },
  footer: { flexDirection: "row", gap: HB_SPACING.sm },
  footerButton: { flex: 1 },
});
