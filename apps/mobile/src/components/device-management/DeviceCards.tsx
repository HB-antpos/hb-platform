import { memo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, IconButton, Text } from "react-native-paper";
import { CARD_STYLE, formatAgeText, KeyValueGrid, StatusTag, VersionBlock, type Translate } from "@/components/device-management/primitives";
import { getDeviceStatusKey, type DeviceStatusKey } from "@/modules/device-management/status";
import type { AppDeviceStatus, DeviceManagementDevice } from "@/modules/device-management/types";
import { getAppVersionBadge, getRegisteredVersionBadge } from "@/modules/device-management/version-state";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

const STATUS_TONES: Record<DeviceStatusKey, "ok" | "warn" | "info" | "muted" | "danger"> = {
  active: "ok",
  disabled: "danger",
  locked: "info",
  pendingConfirmation: "warn",
  unregistered: "muted",
};

function getHardwareTail(hardwareId?: string | null) {
  const value = hardwareId?.trim();
  if (!value) return null;
  return value.length <= 12 ? value : `...${value.slice(-8)}`;
}

function getUpdateTail(updateId?: string | null) {
  const value = updateId?.trim();
  if (!value) return null;
  return value.length <= 10 ? value : `...${value.slice(-10)}`;
}

function formatDateTime(value: string | undefined | null, locale: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(parsed);
}

/** 「详情」折叠行：展开后显示低频字段，默认收起让列表更紧凑。 */
function ExpandToggle({ expanded, onPress, t }: { expanded: boolean; onPress: () => void; t: Translate }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded }}
      onPress={onPress}
      hitSlop={8}
      style={styles.toggle}
    >
      <Icon source={expanded ? "chevron-up" : "chevron-down"} size={18} color={HB_COLORS.textSecondary} />
      <Text style={styles.toggleText}>{t("deviceManagement:card.details")}</Text>
    </Pressable>
  );
}

/** 标题下的分店行：带门店图标，名称过长时单行截断。 */
function StoreLine({ label }: { label: string }) {
  return (
    <View style={styles.storeLine}>
      <Icon source="store-outline" size={14} color={HB_COLORS.textSecondary} />
      <Text style={styles.subtitle} numberOfLines={1}>{label}</Text>
    </View>
  );
}

function DetailList({ rows }: { rows: { label: string; value: string | null | undefined }[] }) {
  const visible = rows.filter((row) => row.value);
  if (visible.length === 0) return null;
  return (
    <View style={styles.details}>
      {visible.map((row) => (
        <View key={row.label} style={styles.detailRow}>
          <Text style={styles.detailLabel}>{row.label}</Text>
          <Text style={styles.detailValue} selectable>{row.value}</Text>
        </View>
      ))}
    </View>
  );
}

export interface RegisteredDeviceCardProps {
  item: DeviceManagementDevice;
  title: string;
  /** 分店名称 · 代码，由页面统一算好（含本地分店列表兜底）。 */
  storeLabel: string;
  t: Translate;
  language: string;
  canManage: boolean;
  busy: boolean;
  hasRegistrationId: boolean;
  onEdit: () => void;
  onMore: () => void;
}

export const RegisteredDeviceCard = memo(function RegisteredDeviceCard({
  item,
  title,
  storeLabel,
  t,
  language,
  canManage,
  busy,
  hasRegistrationId,
  onEdit,
  onMore,
}: RegisteredDeviceCardProps) {
  const [expanded, setExpanded] = useState(false);
  const statusKey = getDeviceStatusKey(item.status);
  const system = [item.deviceSystem || item.platform, item.deviceType].filter(Boolean).join(" · ");
  const age = formatAgeText(item.lastHeartbeatAt, t);
  const onlineText = item.isOnline ? t("deviceManagement:appUsage.online") : t("deviceManagement:appUsage.offline");
  const badge = getRegisteredVersionBadge(item);
  const showVersion = Boolean(item.appVersion || badge);

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <View style={styles.titleWrap}>
          <Text style={styles.title} numberOfLines={1}>{title}</Text>
          <StoreLine label={storeLabel} />
        </View>
        <StatusTag tone={STATUS_TONES[statusKey]} label={t(`deviceManagement:statuses.${statusKey}`)} />
      </View>

      <View style={styles.metaLine}>
        {system ? (
          <>
            <Icon source={item.deviceSystem === "Android" || item.deviceSystem === "iOS" ? "cellphone" : "monitor"} size={14} color={HB_COLORS.textSecondary} />
            <Text style={styles.metaText}>{system}</Text>
            <Text style={styles.metaDivider}>|</Text>
          </>
        ) : null}
        <View style={[styles.dot, !item.isOnline && styles.dotOff]} />
        <Text style={styles.metaText}>{age ? `${onlineText} · ${age}` : onlineText}</Text>
      </View>

      {showVersion ? (
        <VersionBlock label={t("deviceManagement:card.currentVersion")} version={item.appVersion} badge={badge} t={t} />
      ) : null}

      <KeyValueGrid
        items={[
          { label: t("deviceManagement:card.cashier"), value: item.currentCashierName || t("common:na") },
          {
            label: t("deviceManagement:card.transaction"),
            value: item.allowTransactions ? t("deviceManagement:fields.transactionAllowed") : t("deviceManagement:fields.transactionBlocked"),
          },
        ]}
      />

      {expanded ? (
        <DetailList
          rows={[
            { label: t("deviceManagement:fields.hardwareId"), value: getHardwareTail(item.hardwareId) },
            { label: t("deviceManagement:fields.remark"), value: item.remark },
            { label: t("deviceManagement:card.updatedAt"), value: formatDateTime(item.updatedAt, language) },
            { label: t("deviceManagement:card.createdAt"), value: formatDateTime(item.createdAt, language) },
          ]}
        />
      ) : null}

      <View style={styles.footer}>
        <ExpandToggle expanded={expanded} onPress={() => setExpanded((value) => !value)} t={t} />
        {canManage ? (
          <View style={styles.actions}>
            <Button compact mode="outlined" icon="pencil-outline" disabled={!hasRegistrationId} onPress={onEdit} style={styles.actionButton}>
              {t("deviceManagement:actions.edit")}
            </Button>
            <IconButton
              mode="outlined"
              icon="dots-horizontal"
              size={20}
              disabled={busy || !hasRegistrationId}
              accessibilityLabel={t("deviceManagement:actions.more")}
              onPress={onMore}
              style={styles.moreButton}
            />
          </View>
        ) : null}
      </View>
    </View>
  );
});

export interface AppDeviceCardProps {
  item: AppDeviceStatus;
  title: string;
  /** 分店名称 · 代码，由页面统一算好（含本地分店列表兜底）。 */
  storeLabel: string;
  t: Translate;
  language: string;
}

export const AppDeviceCard = memo(function AppDeviceCard({ item, title, storeLabel, t, language }: AppDeviceCardProps) {
  const [expanded, setExpanded] = useState(false);
  const system = item.deviceSystem || item.platform || t("common:na");
  const user = item.lastSeenUserFullName || item.lastSeenUsername || t("deviceManagement:appUsage.noRecentUser");
  const age = formatAgeText(item.lastSeenAtUtc, t);
  const badge = getAppVersionBadge(item);
  const updateTail = getUpdateTail(item.updateId);

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <View style={styles.titleWrap}>
          <Text style={styles.title} numberOfLines={1}>{title}</Text>
          <StoreLine label={storeLabel} />
          <View style={styles.metaLine}>
            <Text style={styles.subtitle} numberOfLines={1}>{system}</Text>
            <Text style={styles.metaDivider}>|</Text>
            <Icon source="account-outline" size={14} color={HB_COLORS.textSecondary} />
            <Text style={[styles.subtitle, styles.userText]} numberOfLines={1}>{user}</Text>
          </View>
        </View>
        <StatusTag
          tone={item.isOnline ? "ok" : "muted"}
          label={item.isOnline ? t("deviceManagement:appUsage.online") : t("deviceManagement:appUsage.offline")}
        />
      </View>

      <VersionBlock
        label={t("deviceManagement:card.appVersion")}
        version={item.appVersion}
        build={item.appBuildVersion}
        badge={badge}
        t={t}
      />

      <KeyValueGrid
        items={[
          {
            label: t("deviceManagement:card.runtimeChannel"),
            value: [item.runtimeVersion, item.channel].filter(Boolean).join(" · ") || t("common:na"),
          },
          { label: t("deviceManagement:card.updateId"), value: updateTail || t("common:na"), mono: true },
        ]}
      />

      {expanded ? (
        <DetailList
          rows={[
            { label: t("deviceManagement:fields.hardwareId"), value: getHardwareTail(item.hardwareId) },
            { label: t("deviceManagement:card.updateSource"), value: item.updateSource },
            { label: t("deviceManagement:card.lastSeenAt"), value: formatDateTime(item.lastSeenAtUtc, language) },
          ]}
        />
      ) : null}

      <View style={styles.footer}>
        <View style={styles.seenWrap}>
          <Icon source="clock-outline" size={16} color={HB_COLORS.textSecondary} />
          <Text style={styles.metaText}>{age ?? t("common:na")}</Text>
        </View>
        <ExpandToggle expanded={expanded} onPress={() => setExpanded((value) => !value)} t={t} />
      </View>
    </View>
  );
});

const styles = StyleSheet.create({
  card: { ...CARD_STYLE },
  header: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: HB_SPACING.sm },
  titleWrap: { flex: 1, minWidth: 0, gap: 2 },
  title: { fontSize: 16, lineHeight: 22, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  subtitle: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary, flexShrink: 1 },
  userText: { flexShrink: 1 },
  storeLine: { flexDirection: "row", alignItems: "center", gap: 4, minWidth: 0 },
  metaLine: { flexDirection: "row", alignItems: "center", gap: 6, flexWrap: "wrap" },
  metaText: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  metaDivider: { fontSize: 12, color: HB_COLORS.outline },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: HB_COLORS.success },
  dotOff: { backgroundColor: HB_COLORS.outline },
  footer: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, paddingTop: HB_SPACING.xs, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  toggle: { flexDirection: "row", alignItems: "center", gap: 4, minHeight: 44 },
  toggleText: { fontSize: 13, color: HB_COLORS.textSecondary },
  seenWrap: { flexDirection: "row", alignItems: "center", gap: 6 },
  actions: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  actionButton: { borderRadius: 8 },
  moreButton: { margin: 0, borderRadius: 8, borderColor: HB_COLORS.outline },
  details: { gap: 6, paddingTop: 2 },
  detailRow: { flexDirection: "row", gap: HB_SPACING.sm },
  detailLabel: { width: 76, fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  detailValue: { flex: 1, fontSize: 12, lineHeight: 18, color: HB_COLORS.textPrimary },
});
