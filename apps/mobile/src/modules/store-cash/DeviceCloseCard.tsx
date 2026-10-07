// 单台设备当天的日结存档：默认纳入最新一份，可手选多份求和（必填原因），也可恢复默认。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useMutation } from "@tanstack/react-query";
import { Button, Checkbox, HelperText, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { putCashCloseSelection } from "./api";
import {
  buildDefaultSelectionRequest,
  buildManualSelectionRequest,
  getIncludedCloseIds,
  hasOverlappingPeriods,
  hasSelectableArchives,
  resolveDeviceNotices,
  sumSelectedCountedCash,
  toggleCloseId,
  validateManualSelection,
} from "./close-selection";
import { formatUtcInZone } from "./dates";
import { resolveCashErrorMessage } from "./errors";
import { formatAud, formatSignedAud } from "./money";
import type { CashDailyDevice } from "./types";
import { useInvalidateCashData } from "./use-store-cash";
import { NoticeBanner, StatusChip, ValueRow } from "./ui";

export function DeviceCloseCard({
  storeCode,
  businessDate,
  timeZoneId,
  device,
  canManage,
}: {
  storeCode: string;
  businessDate: string;
  timeZoneId: string;
  device: CashDailyDevice;
  /** Cash.Deposit.Create：日结选择与存款同属一个权限。 */
  canManage: boolean;
}) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const invalidate = useInvalidateCashData();
  const [editing, setEditing] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState("");

  const notices = resolveDeviceNotices(device);
  const selectable = canManage && hasSelectableArchives(device);
  const manual = device.selectionMode === "Manual";
  const issues = useMemo(
    () => (editing ? validateManualSelection({ device, selected, reason }) : []),
    [device, editing, reason, selected],
  );
  const selectedSum = useMemo(() => sumSelectedCountedCash(device.archives, selected), [device.archives, selected]);
  const localOverlap = editing && hasOverlappingPeriods(device.archives, selected);

  const mutation = useMutation({
    mutationFn: putCashCloseSelection,
    onSuccess: async () => {
      setEditing(false);
      setSubmitted(false);
      setReason("");
      setError("");
      await invalidate();
    },
    onError: (cause) => setError(resolveCashErrorMessage(cause, { t, language })),
  });

  const startEditing = () => {
    setSelected(getIncludedCloseIds(device));
    setReason(device.selectionReason ?? "");
    setSubmitted(false);
    setError("");
    setEditing(true);
  };

  const confirm = () => {
    setSubmitted(true);
    if (issues.length > 0 || mutation.isPending) return;
    const request = buildManualSelectionRequest({ storeCode, businessDate, deviceCode: device.deviceCode, selected, reason });
    if (request) mutation.mutate(request);
  };

  const restoreDefault = () => {
    if (mutation.isPending) return;
    setError("");
    mutation.mutate(buildDefaultSelectionRequest({ storeCode, businessDate, deviceCode: device.deviceCode }));
  };

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.deviceCode} numberOfLines={1}>{t("daily.device", { code: device.deviceCode })}</Text>
          <Text style={styles.caption}>{t("daily.included", { amount: formatAud(device.includedCash) })}</Text>
        </View>
        <StatusChip tone={manual ? "info" : "neutral"} label={manual ? t("daily.modeManual") : t("daily.modeDefault")} />
      </View>

      {notices.stale ? <NoticeBanner tone="warning" icon="alert-outline" text={t("daily.stale")} /> : null}
      {notices.overlap ? <NoticeBanner tone="warning" icon="layers-outline" text={t("daily.overlap")} /> : null}

      {manual && !editing && device.selectionReason ? (
        <Text style={styles.caption}>
          {t("daily.manualBy", {
            name: device.selectedByName ?? "--",
            time: device.selectedAtUtc ? formatUtcInZone(device.selectedAtUtc, timeZoneId) : "--",
            reason: device.selectionReason,
          })}
        </Text>
      ) : null}

      {device.archives.length === 0 ? <Text style={styles.caption}>{t("daily.noArchives")}</Text> : null}

      {device.archives.map((archive) => {
        const checked = selected.includes(archive.closeId);
        const body = (
          <View style={styles.archiveBody}>
            <View style={styles.archiveTop}>
              <Text style={styles.closeId} numberOfLines={1}>{archive.closeId}</Text>
              {archive.included ? <StatusChip tone="success" label={t("daily.archiveIncluded")} /> : null}
            </View>
            <Text style={styles.caption}>{t("daily.savedAt", { time: formatUtcInZone(archive.savedAtUtc, timeZoneId) })}</Text>
            <Text style={styles.caption}>
              {t("daily.period", {
                from: formatUtcInZone(archive.periodFromUtc, timeZoneId),
                to: formatUtcInZone(archive.periodToUtc, timeZoneId),
              })}
            </Text>
            <ValueRow label={t("daily.counted")} value={formatAud(archive.countedCash)} strong />
            <ValueRow label={t("daily.expected")} value={formatAud(archive.expectedCash)} muted />
            <ValueRow
              label={t("daily.variance")}
              value={formatSignedAud(archive.variance)}
              muted={archive.variance === 0}
              valueStyle={archive.variance === 0 ? undefined : styles.varianceValue}
            />
          </View>
        );
        return editing ? (
          <Pressable
            key={archive.closeId}
            accessibilityRole="checkbox"
            accessibilityState={{ checked, disabled: mutation.isPending }}
            disabled={mutation.isPending}
            onPress={() => setSelected((current) => toggleCloseId(current, archive.closeId))}
            style={[styles.archive, styles.archiveSelectable, checked ? styles.archiveChecked : null]}
          >
            <Checkbox status={checked ? "checked" : "unchecked"} disabled={mutation.isPending} />
            {body}
          </Pressable>
        ) : (
          <View key={archive.closeId} style={[styles.archive, archive.included ? styles.archiveIncluded : null]}>
            {body}
          </View>
        );
      })}

      {editing ? (
        <View style={styles.editor}>
          <ValueRow label={t("daily.selectedSum", { count: selected.length })} value={formatAud(selectedSum)} strong />
          {localOverlap ? <NoticeBanner tone="warning" icon="layers-outline" text={t("daily.overlapLocal")} /> : null}
          <TextInput
            mode="outlined"
            label={t("daily.reasonLabel")}
            placeholder={t("daily.reasonPlaceholder")}
            value={reason}
            onChangeText={setReason}
            multiline
            numberOfLines={2}
            maxLength={500}
            disabled={mutation.isPending}
            error={submitted && (issues.includes("reasonRequired") || issues.includes("reasonTooLong"))}
            style={styles.input}
          />
          {submitted && issues.includes("noneSelected") ? (
            <HelperText type="error" visible>{t("daily.errors.noneSelected")}</HelperText>
          ) : null}
          {submitted && issues.includes("reasonRequired") ? (
            <HelperText type="error" visible>{t("daily.errors.reasonRequired")}</HelperText>
          ) : null}
          {submitted && issues.includes("reasonTooLong") ? (
            <HelperText type="error" visible>{t("daily.errors.reasonTooLong")}</HelperText>
          ) : null}
          {submitted && issues.includes("unchanged") ? (
            <HelperText type="error" visible>{t("daily.errors.unchanged")}</HelperText>
          ) : null}
          {error ? <HelperText type="error" visible>{error}</HelperText> : null}
          <View style={styles.actions}>
            <Button onPress={() => setEditing(false)} disabled={mutation.isPending}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" onPress={confirm} loading={mutation.isPending} disabled={mutation.isPending}>
              {t("daily.confirmSelection")}
            </Button>
          </View>
        </View>
      ) : selectable || (canManage && manual) ? (
        <View style={styles.actions}>
          {canManage && manual ? (
            <Button onPress={restoreDefault} loading={mutation.isPending} disabled={mutation.isPending} compact>
              {t("daily.restoreDefault")}
            </Button>
          ) : null}
          {selectable ? (
            <Button mode="outlined" onPress={startEditing} disabled={mutation.isPending} compact>
              {t("daily.manualSelect")}
            </Button>
          ) : null}
        </View>
      ) : null}
      {!editing && error ? <HelperText type="error" visible>{error}</HelperText> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  header: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  headerText: { flex: 1, minWidth: 0 },
  deviceCode: { color: HB_COLORS.textPrimary, fontWeight: "700", fontSize: 15 },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  archive: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
  },
  archiveSelectable: { flexDirection: "row", alignItems: "flex-start", gap: 4, paddingLeft: 4, minHeight: 56 },
  archiveChecked: { borderColor: HB_COLORS.action },
  archiveIncluded: { borderColor: HB_COLORS.success },
  archiveBody: { flex: 1, minWidth: 0, gap: 2 },
  archiveTop: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  closeId: { flex: 1, minWidth: 0, color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "600" },
  varianceValue: { color: HB_COLORS.danger },
  editor: { gap: HB_SPACING.xs },
  input: { backgroundColor: HB_COLORS.white },
  actions: { flexDirection: "row", justifyContent: "flex-end", gap: HB_SPACING.xs, flexWrap: "wrap" },
});
