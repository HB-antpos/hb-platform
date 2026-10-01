import { useEffect, useMemo, useState, type ComponentType } from "react";
import { Alert, Pressable, StyleSheet, View } from "react-native";
import { Button, Card, HelperText, IconButton, SegmentedButtons, Text, TextInput } from "react-native-paper";
import { type AttendanceStatusTone, StatusPill } from "@/components/attendance/AdjustmentFormControls";
import { MonthDatePicker as RawMonthDatePicker } from "@/components/attendance/MonthDatePicker";
import type {
  AttendanceHolidayBusinessStatus,
  AttendanceStoreHoliday,
  AttendanceStoreHolidayPayload,
} from "@/modules/attendance/types";
import { resolveLocaleTag, type AppLanguage } from "@/shared/i18n/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const BUSINESS_STATUSES: AttendanceHolidayBusinessStatus[] = ["Open", "Closed", "Partial"];
const EMPTY_TIME = "";

interface MonthDatePickerProps {
  value?: string;
  date?: string;
  selectedDate?: string;
  disabled?: boolean;
  onChange?: (date: string) => void;
  onDateChange?: (date: string) => void;
  onSelectDate?: (date: string) => void;
}

const MonthDatePicker = RawMonthDatePicker as ComponentType<MonthDatePickerProps>;

interface HolidayDraft {
  storeCode?: string;
  holidayDate: string;
  holidayName: string;
  businessStatus: AttendanceHolidayBusinessStatus;
  openTime: string;
  closeTime: string;
  isPaidHoliday: boolean;
  remark: string;
}

interface HolidayManagementCardProps {
  holidays: AttendanceStoreHoliday[];
  storeCode?: string;
  storeName?: string;
  isBusy: boolean;
  isSyncBusy?: boolean;
  canSync?: boolean;
  syncDisabledReason?: string;
  selectedDate?: string;
  onCreate: (payload: AttendanceStoreHolidayPayload) => void;
  onUpdate: (holidayGuid: string, payload: AttendanceStoreHolidayPayload) => void;
  onDelete: (holidayGuid: string) => void;
  onSync?: () => void;
}

function createEmptyDraft(storeCode?: string, selectedDate?: string): HolidayDraft {
  return {
    storeCode,
    holidayDate: selectedDate ?? "",
    holidayName: "",
    businessStatus: "Open",
    openTime: EMPTY_TIME,
    closeTime: EMPTY_TIME,
    isPaidHoliday: false,
    remark: "",
  };
}

function normalizeTime(value?: string) {
  return value ? value.slice(0, 5) : EMPTY_TIME;
}

function cleanPayload(form: HolidayDraft): AttendanceStoreHolidayPayload | null {
  const storeCode = form.storeCode?.trim();
  if (!storeCode) {
    return null;
  }

  return {
    storeCode,
    holidayDate: form.holidayDate.trim(),
    holidayName: form.holidayName.trim(),
    businessStatus: form.businessStatus,
    openTime: form.businessStatus === "Closed" ? undefined : form.openTime.trim() || undefined,
    closeTime: form.businessStatus === "Closed" ? undefined : form.closeTime.trim() || undefined,
    isPaidHoliday: form.isPaidHoliday,
    remark: form.remark.trim() || undefined,
  };
}

function sortHolidays(holidays: AttendanceStoreHoliday[]) {
  return [...holidays].sort((left, right) => {
    const byDate = left.holidayDate.localeCompare(right.holidayDate);
    return byDate || left.holidayName.localeCompare(right.holidayName);
  });
}

interface HolidayDateParts {
  year: number;
  month: number;
  day: number;
  /** 与 attendance.weekdays 文案下标一致：0 = 周一 … 6 = 周日 */
  weekdayIndex: number;
}

interface HolidayMonthGroup {
  key: string;
  parts: HolidayDateParts | null;
  items: AttendanceStoreHoliday[];
}

/** 只解析 YYYY-MM-DD 前缀并按 UTC 计算星期，避免设备时区把日期推前或推后一天。 */
function parseHolidayDate(value: string): HolidayDateParts | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) {
    return null;
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  return { year, month, day, weekdayIndex: (date.getUTCDay() + 6) % 7 };
}

/** 已排序列表按「年-月」分组；无法解析日期的记录单独归到末尾一组，仍可点开编辑。 */
function groupHolidaysByMonth(sorted: AttendanceStoreHoliday[]): HolidayMonthGroup[] {
  const groups: HolidayMonthGroup[] = [];
  const byKey = new Map<string, HolidayMonthGroup>();
  for (const holiday of sorted) {
    const parts = parseHolidayDate(holiday.holidayDate);
    const key = parts ? `${parts.year}-${String(parts.month).padStart(2, "0")}` : "unknown";
    let group = byKey.get(key);
    if (!group) {
      group = { key, parts, items: [] };
      byKey.set(key, group);
      groups.push(group);
    }
    group.items.push(holiday);
  }
  return groups;
}

/** 英文用 Intl 取月份全称（失败回退数字）；中文直接用数字，配合文案拼成「10 月」。 */
function formatMonthName(month: number, language: AppLanguage) {
  if (language !== "en") {
    return String(month);
  }
  try {
    return new Intl.DateTimeFormat(resolveLocaleTag(language), { month: "long", timeZone: "UTC" }).format(
      new Date(Date.UTC(2000, month - 1, 1))
    );
  } catch {
    return String(month);
  }
}

const BUSINESS_STATUS_TONES: Record<AttendanceHolidayBusinessStatus, AttendanceStatusTone> = {
  Closed: "warning",
  Partial: "success",
  Open: "neutral",
};

export function HolidayManagementCard({
  holidays,
  storeCode,
  storeName,
  isBusy,
  isSyncBusy = false,
  canSync = false,
  syncDisabledReason,
  selectedDate,
  onCreate,
  onUpdate,
  onDelete,
  onSync,
}: HolidayManagementCardProps) {
  const { t, language } = useAppTranslation(["attendance", "common"]);
  const [editingGuid, setEditingGuid] = useState<string | null>(null);
  // 编辑区以页内面板展开（替换列表），不再套原生 Modal，避免与外层弹层互相遮挡。
  const [isEditorOpen, setIsEditorOpen] = useState(false);
  const [form, setForm] = useState<HolidayDraft>(() => createEmptyDraft(storeCode, selectedDate));

  const sortedHolidays = useMemo(() => sortHolidays(holidays), [holidays]);
  const monthGroups = useMemo(() => groupHolidaysByMonth(sortedHolidays), [sortedHolidays]);
  const currentYear = new Date().getFullYear();
  const canEdit = Boolean(storeCode || form.storeCode);
  const canSubmit = Boolean(canEdit && form.holidayDate.trim() && form.holidayName.trim());
  const isClosed = form.businessStatus === "Closed";
  const editingHoliday = editingGuid
    ? sortedHolidays.find((holiday) => holiday.holidayGuid === editingGuid)
    : undefined;

  useEffect(() => {
    if (editingGuid) {
      return;
    }
    setForm((current) => ({
      ...current,
      storeCode,
      holidayDate: selectedDate ?? current.holidayDate,
    }));
  }, [editingGuid, selectedDate, storeCode]);

  const setField = <K extends keyof HolidayDraft>(key: K, value: HolidayDraft[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const setHolidayDate = (date: string) => {
    setField("holidayDate", date);
  };

  const resetForm = () => {
    setEditingGuid(null);
    setForm(createEmptyDraft(storeCode, selectedDate));
  };

  const closeEditor = () => {
    resetForm();
    setIsEditorOpen(false);
  };

  const beginCreate = () => {
    resetForm();
    setIsEditorOpen(true);
  };

  const beginEdit = (holiday: AttendanceStoreHoliday) => {
    setEditingGuid(holiday.holidayGuid);
    setForm({
      storeCode: holiday.storeCode || storeCode,
      holidayDate: holiday.holidayDate,
      holidayName: holiday.holidayName,
      businessStatus: holiday.businessStatus || "Open",
      openTime: normalizeTime(holiday.openTime),
      closeTime: normalizeTime(holiday.closeTime),
      isPaidHoliday: holiday.isPaidHoliday,
      remark: holiday.remark ?? "",
    });
    setIsEditorOpen(true);
  };

  const submit = () => {
    const payload = cleanPayload({
      ...form,
      storeCode: form.storeCode || storeCode,
    });
    if (!payload) {
      return;
    }

    if (editingGuid) {
      onUpdate(editingGuid, payload);
    } else {
      onCreate(payload);
    }
    closeEditor();
  };

  const confirmDelete = (holiday: AttendanceStoreHoliday) => {
    Alert.alert(
      t("holidayManagement.deleteTitle", { defaultValue: "Delete holiday" }),
      t("holidayManagement.deleteMessage", { defaultValue: "Delete this public holiday?" }),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("holidayManagement.deleteConfirm", { defaultValue: "Delete" }),
          style: "destructive",
          onPress: () => {
            onDelete(holiday.holidayGuid);
            if (editingGuid === holiday.holidayGuid) {
              closeEditor();
            }
          },
        },
      ]
    );
  };

  const businessStatusLabel = (status: AttendanceHolidayBusinessStatus) =>
    t(`holidayManagement.businessStatuses.${status}`, { defaultValue: status });

  /** 次要信息：营业时段 / 全天休业 · 带薪 · 备注 */
  const describeHoliday = (holiday: AttendanceStoreHoliday) => {
    const parts: string[] = [];
    if (holiday.businessStatus === "Closed") {
      parts.push(t("holidayManagement.closedAllDay", { defaultValue: "Closed all day" }));
    } else {
      const openTime = normalizeTime(holiday.openTime);
      const closeTime = normalizeTime(holiday.closeTime);
      parts.push(
        openTime || closeTime
          ? `${openTime || "--:--"}–${closeTime || "--:--"}`
          : t("holidayManagement.regularHours", { defaultValue: "Regular hours" })
      );
    }
    if (holiday.remark) {
      parts.push(holiday.remark);
    }
    return parts.join(" · ");
  };

  const monthGroupLabel = (group: HolidayMonthGroup) => {
    if (!group.parts) {
      return t("holidayManagement.unknownMonth", { defaultValue: "Other" });
    }
    const month = formatMonthName(group.parts.month, language);
    return group.parts.year === currentYear
      ? t("holidayManagement.monthGroup", { month, defaultValue: month })
      : t("holidayManagement.monthGroupWithYear", { month, year: group.parts.year, defaultValue: `${month} ${group.parts.year}` });
  };

  const storeLabel = storeName || storeCode;

  if (isEditorOpen) {
    return (
      <Card mode="outlined" style={styles.card}>
        <Card.Content style={styles.content}>
          <View style={styles.editorHeader}>
            <View style={styles.flexText}>
              <Text variant="titleMedium">
                {editingGuid
                  ? t("holidayManagement.editTitle", { defaultValue: "Edit holiday" })
                  : t("holidayManagement.addAction", { defaultValue: "Add holiday" })}
              </Text>
              {storeLabel ? (
                <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
                  {storeLabel}
                </Text>
              ) : null}
            </View>
            <IconButton
              icon="close"
              size={20}
              accessibilityLabel={t("common:actions.close")}
              onPress={closeEditor}
              disabled={isBusy}
            />
          </View>

          <MonthDatePicker
            value={form.holidayDate}
            date={form.holidayDate}
            selectedDate={form.holidayDate}
            disabled={isBusy}
            onChange={setHolidayDate}
            onDateChange={setHolidayDate}
            onSelectDate={setHolidayDate}
          />

          <TextInput
            mode="outlined"
            dense
            label={t("holidayManagement.fields.name", { defaultValue: "Holiday name" })}
            value={form.holidayName}
            onChangeText={(value) => setField("holidayName", value)}
            disabled={isBusy}
          />

          <SegmentedButtons
            value={form.businessStatus}
            density="small"
            onValueChange={(value) =>
              setForm((current) => ({
                ...current,
                businessStatus: value as AttendanceHolidayBusinessStatus,
                openTime: value === "Closed" ? EMPTY_TIME : current.openTime,
                closeTime: value === "Closed" ? EMPTY_TIME : current.closeTime,
              }))
            }
            buttons={BUSINESS_STATUSES.map((status) => ({
              value: status,
              label: businessStatusLabel(status),
              disabled: isBusy,
            }))}
          />

          {/* 休业时营业时段无意义（提交时也会清空），直接收起输入框 */}
          {!isClosed ? (
            <View style={styles.timeRow}>
              <TextInput
                mode="outlined"
                dense
                label={t("fields.startTime")}
                value={form.openTime}
                placeholder={t("common:placeholders.time")}
                style={styles.flexText}
                onChangeText={(value) => setField("openTime", value)}
                disabled={isBusy}
              />
              <TextInput
                mode="outlined"
                dense
                label={t("fields.endTime")}
                value={form.closeTime}
                placeholder={t("common:placeholders.time")}
                style={styles.flexText}
                onChangeText={(value) => setField("closeTime", value)}
                disabled={isBusy}
              />
            </View>
          ) : null}

          {/* 门店不区分带薪，界面不展示该字段；编辑时沿用原值提交，避免改动已有数据。 */}
          <TextInput
            mode="outlined"
            dense
            label={t("fields.note")}
            value={form.remark}
            multiline
            onChangeText={(value) => setField("remark", value)}
            disabled={isBusy}
          />

          {!canEdit ? (
            <HelperText type="error" visible>
              {t("holidayManagement.noStore", { defaultValue: "Select a store first" })}
            </HelperText>
          ) : null}

          <View style={styles.editorActions}>
            {editingHoliday ? (
              <Button
                mode="text"
                icon="delete-outline"
                textColor={HB_COLORS.danger}
                onPress={() => confirmDelete(editingHoliday)}
                disabled={isBusy}
              >
                {t("holidayManagement.deleteConfirm", { defaultValue: "Delete" })}
              </Button>
            ) : null}
            <View style={styles.flexText} />
            <Button mode="outlined" onPress={closeEditor} disabled={isBusy}>
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              icon={editingGuid ? "content-save-outline" : "plus"}
              onPress={submit}
              disabled={!canSubmit || isBusy}
              loading={isBusy}
            >
              {editingGuid
                ? t("common:actions.save")
                : t("holidayManagement.addAction", { defaultValue: "Add holiday" })}
            </Button>
          </View>
        </Card.Content>
      </Card>
    );
  }

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Content style={styles.content}>
        <View style={styles.syncPanel}>
          <View style={styles.syncText}>
            <Text variant="titleSmall" numberOfLines={1}>
              {storeLabel
                ? t("holidayManagement.syncPanelTitle", {
                    store: storeLabel,
                    defaultValue: `${storeLabel} public holidays`,
                  })
                : t("sections.holidayManagement", { defaultValue: "Public holidays" })}
            </Text>
            <Text variant="bodySmall" style={styles.muted}>
              {syncDisabledReason ||
                t("holidayManagement.syncHint", {
                  defaultValue: "Imports official public holidays for the store's state (NSW / QLD).",
                })}
            </Text>
          </View>
          {onSync ? (
            <Button
              mode="outlined"
              compact
              icon="refresh"
              style={styles.syncButton}
              labelStyle={styles.syncButtonLabel}
              onPress={onSync}
              disabled={!canSync || isBusy || isSyncBusy}
              loading={isSyncBusy}
            >
              {t("holidayManagement.syncAction", {
                defaultValue: "Sync next 30 days",
              })}
            </Button>
          ) : null}
        </View>

        {monthGroups.length ? (
          monthGroups.map((group) => (
            <View key={group.key}>
              <Text variant="labelMedium" style={styles.groupTitle}>
                {monthGroupLabel(group)}
              </Text>
              {group.items.map((holiday, index) => {
                const parts = parseHolidayDate(holiday.holidayDate);
                const status = holiday.businessStatus || "Open";
                return (
                  <Pressable
                    key={holiday.holidayGuid || `${holiday.storeCode}-${holiday.holidayDate}`}
                    accessibilityRole="button"
                    accessibilityLabel={`${holiday.holidayDate} ${holiday.holidayName}`}
                    disabled={isBusy}
                    onPress={() => beginEdit(holiday)}
                    style={({ pressed }) => [
                      styles.row,
                      index === group.items.length - 1 ? styles.rowLast : null,
                      pressed ? styles.rowPressed : null,
                    ]}
                  >
                    <View style={styles.dateBlock}>
                      <Text variant="labelSmall" style={styles.muted}>
                        {parts ? t(`weekdays.${parts.weekdayIndex}`) : ""}
                      </Text>
                      <Text variant="titleLarge" style={styles.dateDay}>
                        {parts ? String(parts.day) : "--"}
                      </Text>
                    </View>
                    <View style={styles.rowBody}>
                      <Text variant="bodyLarge" numberOfLines={1}>
                        {holiday.holidayName}
                      </Text>
                      <Text variant="bodySmall" style={[styles.muted, styles.tabular]} numberOfLines={2}>
                        {describeHoliday(holiday)}
                      </Text>
                    </View>
                    <StatusPill label={businessStatusLabel(status)} tone={BUSINESS_STATUS_TONES[status] ?? "neutral"} />
                  </Pressable>
                );
              })}
            </View>
          ))
        ) : (
          <Text variant="bodyMedium" style={[styles.muted, styles.emptyText]}>
            {storeCode
              ? t("holidayManagement.empty", { defaultValue: "No public holidays for this store." })
              : t("holidayManagement.noStore", { defaultValue: "Select a store first" })}
          </Text>
        )}

        <Button mode="outlined" icon="plus" onPress={beginCreate} disabled={!storeCode || isBusy}>
          {t("holidayManagement.addAction", { defaultValue: "Add holiday" })}
        </Button>
      </Card.Content>
    </Card>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  content: {
    gap: HB_SPACING.sm,
  },
  dateBlock: {
    alignItems: "center",
    width: 40,
  },
  dateDay: {
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    lineHeight: 26,
  },
  editorActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  editorHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  emptyText: {
    paddingVertical: HB_SPACING.md,
    textAlign: "center",
  },
  flexText: {
    flex: 1,
  },
  groupTitle: {
    color: HB_COLORS.textSecondary,
    paddingBottom: HB_SPACING.xxs,
    paddingTop: HB_SPACING.xs,
  },
  muted: {
    color: HB_COLORS.textSecondary,
  },
  row: {
    alignItems: "center",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    minHeight: 56,
    paddingVertical: HB_SPACING.xs,
  },
  rowBody: {
    flex: 1,
    gap: 2,
  },
  rowLast: {
    borderBottomWidth: 0,
  },
  rowPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  syncButton: {
    alignSelf: "flex-start",
  },
  syncButtonLabel: {
    fontSize: 13,
    marginVertical: 6,
  },
  // 窄屏上标题、说明与按钮并排会把说明挤成多行，改为上下排列。
  syncPanel: {
    alignItems: "stretch",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    flexDirection: "column",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
  },
  syncText: {
    gap: 2,
  },
  tabular: {
    fontVariant: ["tabular-nums"],
  },
  timeRow: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
});
