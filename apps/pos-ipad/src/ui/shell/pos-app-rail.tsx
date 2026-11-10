import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useCallback, useContext, useState } from "react";
import { useTranslation } from "react-i18next";
import { Modal, ScrollView, Text, View } from "react-native";
import { SafeAreaInsetsContext } from "react-native-safe-area-context";

import {
  MAX_PINNED_DESTINATIONS,
  POS_DESTINATION_META,
  POS_NAV_GROUP_ORDER,
  togglePinnedDestination,
  type PosActiveDestination,
  type PosDestinationId,
  type PosNavGroup,
} from "./pos-navigation";
import { PosThemePicker } from "./pos-theme-picker";
import { POS_TOP_BAR_HEIGHT } from "./status-strip";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

/** 侧栏宽度：页面布局需要按它留位，「全部功能」面板也贴着它展开。 */
export const POS_RAIL_WIDTH = 92;

export type PosNavEntry = Readonly<{
  id: PosDestinationId;
  label: string;
  description?: string;
  onPress(): void;
  /** 需要处理的事项（例如有待恢复的支付），用红色提醒。 */
  attention?: boolean;
  badge?: number;
  disabled?: boolean;
  /** 侧栏按钮的 testID；缺省为 pos-rail-<id>。面板里的同一入口固定用 pos-more-<id>。 */
  testID?: string;
}>;

export type PosNavModel = Readonly<{
  /** 当前收银员可用的入口；没权限的入口不要放进来。 */
  entries: readonly PosNavEntry[];
  pinned: readonly PosDestinationId[];
  onPinnedChange?(pinned: PosDestinationId[]): void;
  /** 回到收银页；在收银页本身不传。 */
  onOpenSales?(): void;
  onSwitchLanguage?(): void;
  onLock?(): void;
  lockDisabled?: boolean;
  lockTestID?: string;
}>;

export type PosAppRailProps = Readonly<{
  active: PosActiveDestination;
  model: PosNavModel;
  /** 页面正在处理不能中断的操作（退款中、补传中等）时锁住所有跳转。 */
  locked?: boolean;
  /** 界面语言；不传时取全局 i18n 的当前语言。 */
  language?: string | undefined;
  testID?: string;
}>;

/** 左侧功能栏：收银、固定的常用入口、更多、锁屏。 */
export function PosAppRail({
  active,
  language,
  locked = false,
  model,
  testID = "pos-rail",
}: PosAppRailProps) {
  usePosThemeVersion();
  const { i18n, t } = useTranslation();
  const [moreVisible, setMoreVisible] = useState(false);
  const closeMore = useCallback(() => setMoreVisible(false), []);

  const entryById = new Map(model.entries.map((entry) => [entry.id, entry]));
  const pinnedEntries = model.pinned.flatMap((id) => {
    const entry = entryById.get(id);
    return entry ? [entry] : [];
  });
  // 当前页不在侧栏里时，高亮「更多」，让收银员知道自己是从哪里进来的。
  const activeIsPinned = pinnedEntries.some((entry) => entry.id === active);
  const moreActive =
    moreVisible || (active !== "sales" && active !== "none" && !activeIsPinned);
  const needsAttention = model.entries.some(
    (entry) => entry.attention && !model.pinned.includes(entry.id),
  );

  return (
    <View style={styles.rail} testID={testID}>
      <ScrollView
        contentContainerStyle={styles.railScrollContent}
        showsVerticalScrollIndicator={false}
        style={styles.railScroll}
      >
        <RailTile
          active={active === "sales"}
          disabled={locked || (active !== "sales" && !model.onOpenSales)}
          icon="cash-register"
          label={t("nav.sales")}
          onPress={() => {
            if (active !== "sales") model.onOpenSales?.();
          }}
          testID={`${testID}-sales`}
        />
        {pinnedEntries.map((entry) => (
          <RailTile
            active={entry.id === active}
            attention={entry.attention === true}
            badge={entry.badge}
            disabled={locked || entry.disabled === true}
            icon={POS_DESTINATION_META[entry.id].icon as PosIconName}
            key={entry.id}
            label={entry.label}
            onPress={() => {
              if (entry.id !== active) entry.onPress();
            }}
            testID={entry.testID ?? `${testID}-${entry.id}`}
          />
        ))}
      </ScrollView>
      <View style={styles.railDivider} />
      <RailTile
        active={moreActive}
        attention={needsAttention}
        disabled={locked}
        icon="dots-grid"
        label={t("nav.more")}
        onPress={() => setMoreVisible(true)}
        testID={`${testID}-more`}
      />
      {model.onLock ? (
        <RailTile
          active={false}
          disabled={locked || model.lockDisabled === true}
          icon="lock-outline"
          label={t("nav.lock")}
          onPress={model.onLock}
          testID={model.lockTestID ?? `${testID}-lock`}
        />
      ) : null}
      <PosMorePanel
        active={active}
        // i18n 尚未初始化（独立渲染）时没有语言，退回英文名称而不是抛错。
        language={language ?? i18n.resolvedLanguage ?? i18n.language ?? "en"}
        model={model}
        onClose={closeMore}
        visible={moreVisible}
      />
    </View>
  );
}

type RailTileProps = Readonly<{
  active: boolean;
  attention?: boolean;
  badge?: number | undefined;
  disabled: boolean;
  icon: PosIconName;
  label: string;
  onPress(): void;
  testID: string;
}>;

function RailTile({
  active,
  attention = false,
  badge,
  disabled,
  icon,
  label,
  onPress,
  testID,
}: RailTileProps) {
  const color = disabled
    ? posColors.disabledInk
    : active
      ? posColors.accent
      : posColors.ink;
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ disabled, selected: active }}
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [
        styles.tile,
        active && styles.tileActive,
        pressed && !disabled && styles.tilePressed,
      ]}
      testID={testID}
    >
      {active ? <View style={styles.tileActiveBar} /> : null}
      <MaterialCommunityIcons
        accessible={false}
        color={color}
        name={icon}
        size={28}
      />
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.8}
        numberOfLines={2}
        style={[styles.tileLabel, { color }]}
      >
        {label}
      </Text>
      {badge ? (
        <View style={styles.badge}>
          <Text style={styles.badgeText}>{badge > 99 ? "99+" : badge}</Text>
        </View>
      ) : attention ? (
        <View style={styles.attentionDot} />
      ) : null}
    </PosPressable>
  );
}

type PosMorePanelProps = Readonly<{
  active: PosActiveDestination;
  language: string;
  model: PosNavModel;
  onClose(): void;
  visible: boolean;
}>;

/** 「全部功能」面板：所有入口按用途分组一屏展开，并在这里固定常用项、换语言和主题。 */
function PosMorePanel({
  active,
  language,
  model,
  onClose,
  visible,
}: PosMorePanelProps) {
  const { t } = useTranslation();
  const [pinLimitReached, setPinLimitReached] = useState(false);
  // 面板从顶栏下面开始：Modal 铺满整个屏幕，需要自己让出安全区和顶栏的高度。
  const topInset = useContext(SafeAreaInsetsContext)?.top ?? 0;
  const bottomInset = useContext(SafeAreaInsetsContext)?.bottom ?? 0;

  const close = useCallback(() => {
    setPinLimitReached(false);
    onClose();
  }, [onClose]);

  const togglePin = (destinationId: PosDestinationId) => {
    const result = togglePinnedDestination(model.pinned, destinationId);
    setPinLimitReached(result.outcome === "limit");
    if (result.outcome !== "limit") model.onPinnedChange?.(result.pinned);
  };

  const groups = POS_NAV_GROUP_ORDER.map((group) => ({
    entries: model.entries.filter(
      (entry) => POS_DESTINATION_META[entry.id].group === group,
    ),
    group,
  }));

  return (
    <Modal
      animationType="fade"
      onRequestClose={close}
      presentationStyle="overFullScreen"
      supportedOrientations={["landscape-left", "landscape-right"]}
      transparent
      visible={visible}
    >
      <View style={styles.backdrop}>
        <PosPressable
          accessibilityLabel={t("nav.close")}
          accessible={false}
          onPress={close}
          sound="navigate"
          style={styles.backdropDismiss}
          testID="pos-more-backdrop"
        />
        <View
          accessibilityViewIsModal
          style={[
            styles.sheet,
            {
              marginTop: topInset + POS_TOP_BAR_HEIGHT,
              paddingBottom: 18 + bottomInset,
            },
          ]}
          testID="pos-more"
        >
          <View style={styles.sheetHeader}>
            <Text style={styles.sheetTitle}>{t("nav.allFunctions")}</Text>
            <PosButton
              icon="close"
              label={t("nav.close")}
              onPress={close}
              size="small"
              sound="navigate"
              testID="pos-more-close"
            />
          </View>
          {model.onPinnedChange ? (
            <Text
              style={[styles.sheetHint, pinLimitReached && styles.sheetHintWarn]}
              testID="pos-more-pin-hint"
            >
              {pinLimitReached
                ? t("nav.pinLimit", { max: MAX_PINNED_DESTINATIONS })
                : t("nav.pinHint", { max: MAX_PINNED_DESTINATIONS })}
            </Text>
          ) : null}
          <ScrollView
            contentContainerStyle={styles.sheetScrollContent}
            showsVerticalScrollIndicator={false}
          >
            {groups.map(({ entries, group }) => {
              const deviceGroup = group === "device";
              if (entries.length === 0 && !deviceGroup) return null;
              return (
                <View key={group} style={styles.group}>
                  <Text style={styles.groupTitle}>{groupLabel(t, group)}</Text>
                  <View style={styles.grid}>
                    {entries.map((entry) => (
                      <MoreTile
                        active={entry.id === active}
                        attention={entry.attention === true}
                        description={entry.description}
                        disabled={entry.disabled === true}
                        icon={POS_DESTINATION_META[entry.id].icon as PosIconName}
                        key={entry.id}
                        label={entry.label}
                        onPress={() => {
                          close();
                          if (entry.id !== active) entry.onPress();
                        }}
                        pin={
                          model.onPinnedChange
                            ? {
                                label: model.pinned.includes(entry.id)
                                  ? t("nav.unpin")
                                  : t("nav.pin"),
                                onToggle: () => togglePin(entry.id),
                                pinned: model.pinned.includes(entry.id),
                                testID: `pos-more-pin-${entry.id}`,
                              }
                            : null
                        }
                        testID={`pos-more-${entry.id}`}
                      />
                    ))}
                    {deviceGroup && model.onSwitchLanguage ? (
                      <MoreTile
                        active={false}
                        description={t("nav.languageValue")}
                        disabled={false}
                        icon="translate"
                        label={t("nav.language")}
                        onPress={model.onSwitchLanguage}
                        pin={null}
                        testID="pos-more-language"
                      />
                    ) : null}
                    {deviceGroup && model.onLock ? (
                      <MoreTile
                        active={false}
                        description={t("nav.desc.lock")}
                        disabled={model.lockDisabled === true}
                        icon="lock-outline"
                        label={t("nav.lock")}
                        onPress={() => {
                          close();
                          model.onLock?.();
                        }}
                        pin={null}
                        testID="pos-more-lock"
                      />
                    ) : null}
                  </View>
                </View>
              );
            })}
            <View style={styles.group}>
              <Text style={styles.groupTitle}>{t("theme.title")}</Text>
              <PosThemePicker language={language} testID="pos-more-theme" />
              <Text style={styles.themeHint}>{t("theme.hint")}</Text>
            </View>
          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}

function groupLabel(
  t: (key: string) => string,
  group: PosNavGroup,
): string {
  switch (group) {
    case "transaction":
      return t("nav.group.transaction");
    case "records":
      return t("nav.group.records");
    case "store":
      return t("nav.group.store");
    case "device":
      return t("nav.group.device");
  }
}

type MoreTileProps = Readonly<{
  active: boolean;
  attention?: boolean;
  description?: string | undefined;
  disabled: boolean;
  icon: PosIconName;
  label: string;
  onPress(): void;
  pin: Readonly<{
    label: string;
    onToggle(): void;
    pinned: boolean;
    testID: string;
  }> | null;
  testID: string;
}>;

function MoreTile({
  active,
  attention = false,
  description,
  disabled,
  icon,
  label,
  onPress,
  pin,
  testID,
}: MoreTileProps) {
  return (
    <View
      style={[
        styles.moreTile,
        attention && styles.moreTileAttention,
        active && styles.moreTileActive,
      ]}
    >
      <PosPressable
        accessibilityHint={description}
        accessibilityLabel={label}
        accessibilityRole="button"
        accessibilityState={{ disabled, selected: active }}
        disabled={disabled}
        onPress={onPress}
        sound="navigate"
        style={({ pressed }) => [
          styles.moreTileMain,
          pressed && !disabled && styles.tilePressed,
        ]}
        testID={testID}
      >
        <View
          style={[
            styles.moreTileIcon,
            attention && styles.moreTileIconAttention,
          ]}
        >
          <MaterialCommunityIcons
            accessible={false}
            color={
              disabled
                ? posColors.disabledInk
                : attention
                  ? posColors.red
                  : posColors.ink
            }
            name={icon}
            size={27}
          />
        </View>
        <View style={styles.moreTileCopy}>
          <Text
            numberOfLines={1}
            style={[styles.moreTileLabel, disabled && styles.textDisabled]}
          >
            {label}
          </Text>
          {description ? (
            <Text numberOfLines={2} style={styles.moreTileDescription}>
              {description}
            </Text>
          ) : null}
        </View>
      </PosPressable>
      {pin ? (
        <PosPressable
          accessibilityLabel={`${pin.label}: ${label}`}
          accessibilityRole="switch"
          accessibilityState={{ checked: pin.pinned }}
          onPress={pin.onToggle}
          style={({ pressed }) => [
            styles.pinButton,
            pressed && styles.tilePressed,
          ]}
          testID={pin.testID}
        >
          <MaterialCommunityIcons
            accessible={false}
            color={pin.pinned ? posColors.accent : posColors.mutedInk}
            name={pin.pinned ? "pin" : "pin-outline"}
            size={24}
          />
        </PosPressable>
      ) : null}
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  attentionDot: {
    backgroundColor: colors.red,
    borderRadius: 6,
    height: 12,
    position: "absolute",
    right: 12,
    top: 10,
    width: 12,
  },
  backdrop: {
    backgroundColor: colors.overlay,
    flex: 1,
    flexDirection: "row",
  },
  backdropDismiss: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  badge: {
    alignItems: "center",
    backgroundColor: colors.accent,
    borderRadius: 11,
    height: 22,
    justifyContent: "center",
    minWidth: 22,
    paddingHorizontal: 6,
    position: "absolute",
    right: 8,
    top: 6,
  },
  badgeText: {
    color: colors.onAccent,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  grid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  group: {
    gap: 8,
  },
  groupTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  moreTile: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    flexBasis: "31%",
    flexDirection: "row",
    flexGrow: 1,
    maxWidth: "49%",
    minHeight: 84,
  },
  moreTileActive: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
  },
  moreTileAttention: {
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
  },
  moreTileCopy: {
    flex: 1,
    minWidth: 0,
  },
  moreTileDescription: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 16,
    marginTop: 3,
  },
  moreTileIcon: {
    alignItems: "center",
    backgroundColor: colors.surfaceSunken,
    borderRadius: posRadius.medium,
    height: 48,
    justifyContent: "center",
    width: 48,
  },
  moreTileIconAttention: {
    backgroundColor: colors.surface,
  },
  moreTileLabel: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  moreTileMain: {
    alignItems: "center",
    alignSelf: "stretch",
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minWidth: 0,
    paddingLeft: 14,
    paddingRight: 6,
    paddingVertical: 8,
  },
  pinButton: {
    alignItems: "center",
    alignSelf: "stretch",
    justifyContent: "center",
    minHeight: posTouch.small,
    width: posTouch.small,
  },
  rail: {
    backgroundColor: colors.surface,
    borderRightColor: colors.border,
    borderRightWidth: 1,
    gap: 8,
    paddingHorizontal: 8,
    paddingVertical: 10,
    width: POS_RAIL_WIDTH,
  },
  railDivider: {
    backgroundColor: colors.border,
    height: 1,
    marginHorizontal: 6,
  },
  railScroll: {
    flex: 1,
  },
  railScrollContent: {
    gap: 8,
  },
  sheet: {
    backgroundColor: colors.canvas,
    borderRightColor: colors.border,
    borderRightWidth: 1,
    gap: 10,
    marginLeft: POS_RAIL_WIDTH,
    maxWidth: 790,
    paddingHorizontal: 20,
    paddingVertical: 18,
    width: "72%",
  },
  sheetHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  sheetHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  sheetHintWarn: {
    color: colors.red,
    fontWeight: "800",
  },
  sheetScrollContent: {
    gap: 14,
    paddingBottom: 12,
  },
  sheetTitle: {
    color: colors.ink,
    fontSize: 22,
    fontWeight: "900",
  },
  textDisabled: {
    color: colors.disabledInk,
  },
  themeHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
  },
  tile: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    gap: 4,
    justifyContent: "center",
    minHeight: 76,
    paddingHorizontal: 2,
  },
  tileActive: {
    backgroundColor: colors.accentSoft,
  },
  tileActiveBar: {
    backgroundColor: colors.accent,
    borderBottomRightRadius: 4,
    borderTopRightRadius: 4,
    bottom: 12,
    left: -8,
    position: "absolute",
    top: 12,
    width: 4,
  },
  tileLabel: {
    fontSize: 12.5,
    fontWeight: "800",
    textAlign: "center",
  },
  tilePressed: {
    opacity: 0.72,
  },
}));
