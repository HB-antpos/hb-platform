import { useCallback, useMemo, useRef, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Avatar, Button, Icon, Searchbar, Text } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useStoreUsers } from "@/modules/users/hooks";
import { PosPermissionSheet, POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  buildPermissionCopyInput,
  filterPermissionCopyCandidates,
  isPermissionCopyPreviewCurrent,
  normalizeCopyGuid,
  resolveCopyTargetName,
  summarizePermissionCopyPreview,
  togglePermissionCopySelection,
} from "./access-permission-copy";
import type {
  CopyPermissionDirection,
  CopyPermissionMode,
  CopyUserDirectPermissionsInput,
  UserDirectPermissionCopyResult,
} from "./access-management-types";
import type { StoreUserListItem } from "./types";

interface PermissionCopySheetProps {
  direction: CopyPermissionDirection;
  /** 权限页正在编辑的员工。 */
  currentEmployeeGuid: string;
  currentEmployeeName: string;
  /** 候选同事所在的分店；为空时无法列出同事。 */
  storeCode: string;
  actorUserGuid?: string | null;
  /** 把权限 code 翻译成展示名（找不到时由调用方回退为 code）。 */
  resolvePermissionName: (code: string) => string;
  onPreview: (input: CopyUserDirectPermissionsInput) => Promise<UserDirectPermissionCopyResult>;
  onCommit: (input: CopyUserDirectPermissionsInput) => Promise<UserDirectPermissionCopyResult>;
  onDismiss: () => void;
  onCopied: (result: UserDirectPermissionCopyResult) => void;
}

type Step = "pick" | "confirm";

function getUserName(user: StoreUserListItem) {
  return user.fullName?.trim() || user.username;
}

function getInitials(value: string) {
  const words = value.trim().split(/\s+/).filter(Boolean);
  if (!words.length) return "?";
  return words.length === 1
    ? words[0].slice(0, 2).toUpperCase()
    : `${words[0][0]}${words[1][0]}`.toUpperCase();
}

/**
 * 复制直接权限：选人 + 选方式 → 预览每人差异 → 确认写入。
 * 只复制「直接权限」；角色继承的权限不复制。写入由后端原子完成。
 */
export function PermissionCopySheet({
  direction,
  currentEmployeeGuid,
  currentEmployeeName,
  storeCode,
  actorUserGuid,
  resolvePermissionName,
  onPreview,
  onCommit,
  onDismiss,
  onCopied,
}: PermissionCopySheetProps) {
  const { t, language } = useAppTranslation(["userManagement", "common"]);
  const [step, setStep] = useState<Step>("pick");
  const [query, setQuery] = useState("");
  const [selectedGuids, setSelectedGuids] = useState<string[]>([]);
  const [mode, setMode] = useState<CopyPermissionMode>("merge");
  const [preview, setPreview] = useState<UserDirectPermissionCopyResult | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [errorText, setErrorText] = useState("");
  // 同步锁：避免快速连点在 state 更新前重复提交。
  const submittingRef = useRef(false);
  const hasStoreCode = storeCode.trim().length > 0;
  // storeCode 为 undefined 时 useStoreUsers 不发请求；绝不能传 null（会查全部分店）。
  const usersQuery = useStoreUsers(hasStoreCode ? storeCode : undefined, "");
  const allUsers = useMemo(() => usersQuery.data ?? [], [usersQuery.data]);
  const candidates = useMemo(
    () => filterPermissionCopyCandidates(allUsers, { currentEmployeeGuid, actorUserGuid, query }),
    [actorUserGuid, allUsers, currentEmployeeGuid, query]
  );
  const candidateTotal = useMemo(
    () => filterPermissionCopyCandidates(allUsers, { currentEmployeeGuid, actorUserGuid }).length,
    [actorUserGuid, allUsers, currentEmployeeGuid]
  );
  const nameByGuid = useMemo(() => {
    const map = new Map<string, string>();
    allUsers.forEach((user) => map.set(normalizeCopyGuid(user.userGUID), getUserName(user)));
    map.set(normalizeCopyGuid(currentEmployeeGuid), currentEmployeeName);
    return map;
  }, [allUsers, currentEmployeeGuid, currentEmployeeName]);

  const input = useMemo(
    () => buildPermissionCopyInput(direction, currentEmployeeGuid, selectedGuids, mode),
    [currentEmployeeGuid, direction, mode, selectedGuids]
  );
  const summary = useMemo(() => (preview ? summarizePermissionCopyPreview(preview) : null), [preview]);
  const previewCurrent = isPermissionCopyPreviewCurrent(preview, input);
  const isToOthers = direction === "toOthers";

  const toggleUser = (userGuid: string) => {
    if (submitting) return;
    setErrorText("");
    setSelectedGuids((current) => togglePermissionCopySelection(current, userGuid, direction));
  };

  const goNext = useCallback(async () => {
    if (!input || submittingRef.current) return;
    submittingRef.current = true;
    setSubmitting(true);
    setErrorText("");
    try {
      const result = await onPreview(input);
      setPreview(result);
      setStep("confirm");
    } catch (error) {
      setErrorText(
        resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "accessManagement.copy.previewFailed" })
      );
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  }, [input, language, onPreview, t]);

  const confirmCopy = useCallback(async () => {
    // 预览必须与当前选择一致且确有变化，才允许写入。
    if (!input || !previewCurrent || !summary?.hasChanges || submittingRef.current) return;
    submittingRef.current = true;
    setSubmitting(true);
    setErrorText("");
    try {
      const result = await onCommit(input);
      onCopied(result);
    } catch (error) {
      setErrorText(
        resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "accessManagement.copy.copyFailed" })
      );
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  }, [input, language, onCommit, onCopied, previewCurrent, summary?.hasChanges, t]);

  const renderPickList = () => {
    if (!hasStoreCode) {
      return <Text style={styles.stateText}>{t("accessManagement.copy.unavailable")}</Text>;
    }
    if (usersQuery.isLoading) {
      return (
        <View style={styles.stateRow}>
          <ActivityIndicator color={HB_COLORS.brand} />
        </View>
      );
    }
    if (usersQuery.isError) {
      return (
        <View style={styles.stateRow}>
          <Text style={styles.stateText}>{t("accessManagement.copy.listFailed")}</Text>
          <Button compact mode="text" onPress={() => void usersQuery.refetch()}>
            {t("common:actions.retry")}
          </Button>
        </View>
      );
    }
    if (candidates.length === 0) {
      return (
        <Text style={styles.stateText}>
          {t(candidateTotal === 0 ? "accessManagement.copy.empty" : "accessManagement.copy.noMatch")}
        </Text>
      );
    }
    return (
      <View style={styles.list}>
        {candidates.map((user, index) => {
          const name = getUserName(user);
          const selected = selectedGuids.some((guid) => normalizeCopyGuid(guid) === normalizeCopyGuid(user.userGUID));
          return (
            <Pressable
              key={user.userGUID}
              accessibilityRole={isToOthers ? "checkbox" : "radio"}
              accessibilityState={{ checked: selected }}
              accessibilityLabel={[name, user.username !== name ? user.username : null].filter(Boolean).join("，")}
              onPress={() => toggleUser(user.userGUID)}
              style={({ pressed }) => [
                styles.row,
                index > 0 && styles.rowDivider,
                selected && styles.rowSelected,
                pressed && styles.pressed,
              ]}
            >
              <Avatar.Text size={36} label={getInitials(name)} style={styles.avatar} labelStyle={styles.avatarLabel} />
              <View style={styles.rowText}>
                <Text style={styles.rowName} numberOfLines={1}>
                  {name}
                </Text>
                {user.username !== name ? (
                  <Text style={styles.rowMeta} numberOfLines={1}>
                    @{user.username}
                  </Text>
                ) : null}
              </View>
              <Icon
                source={
                  isToOthers
                    ? selected ? "checkbox-marked" : "checkbox-blank-outline"
                    : selected ? "radiobox-marked" : "radiobox-blank"
                }
                size={22}
                color={selected ? HB_COLORS.brand : HB_COLORS.outline}
              />
            </Pressable>
          );
        })}
      </View>
    );
  };

  const renderModeOption = (value: CopyPermissionMode) => {
    const selected = mode === value;
    return (
      <Pressable
        key={value}
        accessibilityRole="radio"
        accessibilityState={{ checked: selected }}
        accessibilityLabel={t(`accessManagement.copy.mode.${value}`)}
        onPress={() => {
          if (submitting) return;
          setErrorText("");
          setMode(value);
        }}
        style={({ pressed }) => [
          styles.row,
          value === "replace" && styles.rowDivider,
          selected && styles.rowSelected,
          pressed && styles.pressed,
        ]}
      >
        <Icon
          source={selected ? "radiobox-marked" : "radiobox-blank"}
          size={22}
          color={selected ? HB_COLORS.brand : HB_COLORS.outline}
        />
        <View style={styles.rowText}>
          <Text style={styles.rowName}>{t(`accessManagement.copy.mode.${value}`)}</Text>
          <Text style={styles.rowMeta}>{t(`accessManagement.copy.mode.${value}Description`)}</Text>
        </View>
      </Pressable>
    );
  };

  const renderPickStep = () => (
    <>
      {renderPickList()}
      <Text style={styles.sectionLabel}>{t("accessManagement.copy.modeTitle")}</Text>
      <View style={styles.list}>
        {renderModeOption("merge")}
        {renderModeOption("replace")}
      </View>
      <Text style={styles.help}>{t("accessManagement.copy.help")}</Text>
      {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
    </>
  );

  const renderConfirmStep = () => {
    if (!preview || !summary) return null;
    const sourceName = resolveCopyTargetName(nameByGuid, preview.sourceUserGuid);
    return (
      <>
        <View style={styles.card}>
          <Text style={styles.cardTitle}>{t("accessManagement.copy.confirm.source", { name: sourceName })}</Text>
          <Text style={styles.rowMeta}>
            {t("accessManagement.copy.confirm.sourceMeta", {
              count: summary.sourceCount,
              mode: t(`accessManagement.copy.mode.${preview.mode}Short`),
            })}
          </Text>
        </View>

        <Text style={styles.sectionLabel}>
          {t("accessManagement.copy.confirm.changesTitle", { count: preview.targets.length })}
        </Text>
        <View style={styles.list}>
          {preview.targets.map((target, index) => {
            const name = resolveCopyTargetName(nameByGuid, target.userGuid);
            const noChange = target.added.length === 0 && target.removed.length === 0;
            return (
              <View key={target.userGuid} style={[styles.targetBlock, index > 0 && styles.rowDivider]}>
                <View style={styles.targetHeader}>
                  <Avatar.Text size={32} label={getInitials(name)} style={styles.avatar} labelStyle={styles.avatarLabel} />
                  <Text style={[styles.rowName, styles.targetName]} numberOfLines={1}>
                    {name}
                  </Text>
                  {target.added.length > 0 ? (
                    <Text style={styles.addedBadge}>+{target.added.length}</Text>
                  ) : null}
                  {target.removed.length > 0 ? (
                    <Text style={styles.removedBadge}>−{target.removed.length}</Text>
                  ) : null}
                </View>
                {noChange ? (
                  <Text style={styles.rowMeta}>{t("accessManagement.copy.confirm.noChange")}</Text>
                ) : null}
                {target.added.length > 0 ? (
                  <Text style={styles.changeLine}>
                    <Text style={styles.addedMark}>＋ </Text>
                    {target.added.map(resolvePermissionName).join("、")}
                  </Text>
                ) : null}
                {target.removed.length > 0 ? (
                  <Text style={styles.changeLine}>
                    <Text style={styles.removedMark}>－ </Text>
                    {target.removed.map(resolvePermissionName).join("、")}
                  </Text>
                ) : null}
                {target.unchangedCount > 0 ? (
                  <Text style={styles.rowMeta}>
                    {t("accessManagement.copy.confirm.unchanged", { count: target.unchangedCount })}
                  </Text>
                ) : null}
              </View>
            );
          })}
        </View>

        {!summary.hasChanges ? (
          <View style={styles.infoBox}>
            <Text style={styles.infoText}>{t("accessManagement.copy.confirm.nothingToCopy")}</Text>
          </View>
        ) : null}

        {preview.skippedPermissionCodes.length > 0 ? (
          <>
            <Text style={styles.sectionLabel}>{t("accessManagement.copy.confirm.skippedTitle")}</Text>
            <View style={styles.warningBox}>
              <Text style={styles.warningText}>
                {t("accessManagement.copy.confirm.skipped", {
                  names: preview.skippedPermissionCodes.map(resolvePermissionName).join("、"),
                })}
              </Text>
            </View>
          </>
        ) : null}
        {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
      </>
    );
  };

  const title =
    step === "confirm"
      ? t("accessManagement.copy.confirm.title")
      : isToOthers
        ? t("accessManagement.copy.toOthersTitle", { name: currentEmployeeName })
        : t("accessManagement.copy.fromColleagueTitle", { name: currentEmployeeName });

  const footer =
    step === "confirm" ? (
      <>
        <Button
          mode="outlined"
          disabled={submitting}
          onPress={() => {
            setErrorText("");
            setStep("pick");
          }}
          style={BUSINESS_UI.button}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t("accessManagement.copy.confirm.back")}
        </Button>
        <Button
          mode="contained"
          loading={submitting}
          disabled={submitting || !previewCurrent || !summary?.hasChanges}
          onPress={() => void confirmCopy()}
          style={[BUSINESS_UI.button, styles.primaryButton]}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t("accessManagement.copy.confirm.submit")}
        </Button>
      </>
    ) : (
      <Button
        mode="contained"
        loading={submitting}
        disabled={submitting || !input || !hasStoreCode}
        onPress={() => void goNext()}
        style={[BUSINESS_UI.button, styles.primaryButton]}
        contentStyle={BUSINESS_UI.buttonContent}
      >
        {input
          ? isToOthers
            ? t("accessManagement.copy.nextToOthers", { count: input.targetUserGuids.length })
            : t("accessManagement.copy.nextFromColleague")
          : t("accessManagement.copy.nextIdle")}
      </Button>
    );

  return (
    <PosPermissionSheet
      title={title}
      subtitle={step === "pick" && hasStoreCode ? t("accessManagement.copy.subtitle", { store: storeCode }) : undefined}
      closeLabel={t("accessManagement.copy.close")}
      onDismiss={onDismiss}
      dismissable={!submitting}
      header={
        step === "pick" && hasStoreCode ? (
          <Searchbar
            value={query}
            onChangeText={setQuery}
            placeholder={t("accessManagement.copy.searchPlaceholder")}
            style={styles.search}
            inputStyle={styles.searchInput}
            autoCorrect={false}
            autoCapitalize="none"
          />
        ) : undefined
      }
      footer={footer}
    >
      {step === "pick" ? renderPickStep() : renderConfirmStep()}
    </PosPermissionSheet>
  );
}

const styles = StyleSheet.create({
  search: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    height: 44,
    elevation: 0,
  },
  searchInput: { minHeight: 44, fontSize: 15 },
  list: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  row: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  rowSelected: { backgroundColor: POS_PERMISSION_COLORS.brandSoft },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  avatar: { backgroundColor: HB_COLORS.surfaceMuted },
  avatarLabel: { color: HB_COLORS.textSecondary, fontSize: 14, fontWeight: "700" },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  rowName: { fontSize: 15, lineHeight: 21, fontWeight: "600", color: HB_COLORS.textPrimary },
  rowMeta: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  sectionLabel: { fontSize: 13, lineHeight: 18, fontWeight: "700", color: HB_COLORS.textSecondary },
  stateRow: { minHeight: 64, alignItems: "center", justifyContent: "center", gap: HB_SPACING.xs },
  stateText: {
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
    textAlign: "center",
    paddingVertical: HB_SPACING.md,
  },
  help: {
    fontSize: 12,
    lineHeight: 18,
    color: HB_COLORS.textSecondary,
    backgroundColor: POS_PERMISSION_COLORS.infoSoft,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
  },
  errorText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.danger },
  primaryButton: { flex: 1 },
  card: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.sm,
    gap: 2,
  },
  cardTitle: { fontSize: 15, lineHeight: 21, fontWeight: "700", color: HB_COLORS.textPrimary },
  targetBlock: { padding: HB_SPACING.sm, gap: HB_SPACING.xxs },
  targetHeader: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  targetName: { flex: 1 },
  addedBadge: { fontSize: 13, fontWeight: "700", color: HB_COLORS.success },
  removedBadge: { fontSize: 13, fontWeight: "700", color: HB_COLORS.danger },
  addedMark: { color: HB_COLORS.success, fontWeight: "700" },
  removedMark: { color: HB_COLORS.danger, fontWeight: "700" },
  changeLine: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textPrimary },
  infoBox: {
    backgroundColor: POS_PERMISSION_COLORS.infoSoft,
    borderColor: POS_PERMISSION_COLORS.infoBorder,
    borderWidth: 1,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
  },
  infoText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textPrimary },
  warningBox: {
    backgroundColor: POS_PERMISSION_COLORS.warningSoft,
    borderColor: POS_PERMISSION_COLORS.warningBorder,
    borderWidth: 1,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
  },
  warningText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.warning },
});
