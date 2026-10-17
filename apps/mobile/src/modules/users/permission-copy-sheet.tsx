import { useCallback, useMemo, useRef, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Searchbar, Text } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { FilterChipRow, NoticeBox, PersonAvatar, PeopleTag, SegmentTabs } from "@/modules/people";
import { resolvePersonName } from "@/modules/people/person-name";
import { useStoreUsers } from "@/modules/users/hooks";
import { PosPermissionSheet, POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  buildCopyConfirmRows,
  buildPermissionCopyInput,
  filterPermissionCopyCandidates,
  isPermissionCopyPreviewCurrent,
  listCopySkippedPermissionNames,
  normalizeCopyGuid,
  resolveCopyTargetName,
  summarizePermissionCopyPreview,
  togglePermissionCopySelection,
  type CopyStoreFilter,
} from "./access-permission-copy";
import type {
  CopyPermissionDirection,
  CopyPermissionMode,
  CopyUserDirectPermissionsInput,
  UserDirectPermissionCopyResult,
} from "./access-management-types";

interface PermissionCopySheetProps {
  /** 打开时默认的方向；弹层里可以切换。 */
  initialDirection: CopyPermissionDirection;
  /** 授权页正在编辑的员工。 */
  currentEmployeeGuid: string;
  currentEmployeeName: string;
  /** 候选同事的分店范围：店长只能选自己管理的分店，管理员可以切分店 / 看全部分店。 */
  storeFilter: CopyStoreFilter;
  actorUserGuid?: string | null;
  /** 把权限 code 翻译成展示名（找不到时由调用方回退为 code）。 */
  resolvePermissionName: (code: string) => string;
  onPreview: (input: CopyUserDirectPermissionsInput) => Promise<UserDirectPermissionCopyResult>;
  onCommit: (input: CopyUserDirectPermissionsInput) => Promise<UserDirectPermissionCopyResult>;
  onDismiss: () => void;
  onCopied: (result: UserDirectPermissionCopyResult) => void;
}

type Step = "pick" | "confirm";

/**
 * 复制直接权限（设计稿 A4④）：两步「选人 → 确认」。
 * 选人：方向（复制给他人 / 从同事复制）、分店范围、搜索、勾选；
 * 确认：逐人列出会增加（+3）或移除（−2）的权限、被跳过的项及原因，合并 / 覆盖在确认页切换（默认合并，
 * 切换会立刻重新向后端预览，旧预览作废）。只复制「直接权限」，写入由后端原子完成。
 */
export function PermissionCopySheet({
  initialDirection,
  currentEmployeeGuid,
  currentEmployeeName,
  storeFilter,
  actorUserGuid,
  resolvePermissionName,
  onPreview,
  onCommit,
  onDismiss,
  onCopied,
}: PermissionCopySheetProps) {
  const { t, language } = useAppTranslation(["peopleAccess", "common"]);
  const [direction, setDirection] = useState<CopyPermissionDirection>(initialDirection);
  const [step, setStep] = useState<Step>("pick");
  const [query, setQuery] = useState("");
  const [storeCode, setStoreCode] = useState(storeFilter.initialStoreCode);
  const [selectedGuids, setSelectedGuids] = useState<string[]>([]);
  const [mode, setMode] = useState<CopyPermissionMode>("merge");
  const [preview, setPreview] = useState<UserDirectPermissionCopyResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [errorText, setErrorText] = useState("");
  // 同步锁：避免快速连点在 state 更新前重复提交。
  const busyRef = useRef(false);

  // 无分店且不是「管理员看全部分店」时不发请求；绝不能让店长查全部分店。
  const queryStoreCode = storeCode.trim()
    ? storeCode
    : storeFilter.allowAll
      ? null
      : undefined;
  const hasScope = queryStoreCode !== undefined;
  const usersQuery = useStoreUsers(queryStoreCode, "");
  const allUsers = useMemo(() => usersQuery.data ?? [], [usersQuery.data]);
  const candidates = useMemo(
    () => filterPermissionCopyCandidates(allUsers, { currentEmployeeGuid, actorUserGuid, query }),
    [actorUserGuid, allUsers, currentEmployeeGuid, query],
  );
  const candidateTotal = useMemo(
    () => filterPermissionCopyCandidates(allUsers, { currentEmployeeGuid, actorUserGuid }).length,
    [actorUserGuid, allUsers, currentEmployeeGuid],
  );
  const nameByGuid = useMemo(() => {
    const map = new Map<string, string>();
    allUsers.forEach((user) => map.set(normalizeCopyGuid(user.userGUID), resolvePersonName(user).primary));
    map.set(normalizeCopyGuid(currentEmployeeGuid), currentEmployeeName);
    return map;
  }, [allUsers, currentEmployeeGuid, currentEmployeeName]);

  const input = useMemo(
    () => buildPermissionCopyInput(direction, currentEmployeeGuid, selectedGuids, mode),
    [currentEmployeeGuid, direction, mode, selectedGuids],
  );
  const summary = useMemo(() => (preview ? summarizePermissionCopyPreview(preview) : null), [preview]);
  const previewCurrent = isPermissionCopyPreviewCurrent(preview, input);
  const isToOthers = direction === "toOthers";
  const selectedStore = storeFilter.options.find(
    (item) => item.storeCode.toLowerCase() === storeCode.toLowerCase(),
  );
  const scopeLabel = storeCode.trim()
    ? (selectedStore?.storeName || storeCode)
    : t("copy.allStores");

  const failure = useCallback(
    (error: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(error, { t, language, fallbackKey }),
    [language, t],
  );

  const switchDirection = (next: CopyPermissionDirection) => {
    if (busy || next === direction) return;
    // 方向变了来源与目标整个对调，旧选择与旧预览都不再适用；「覆盖」会删除对方已有权限，
    // 只对用户亲眼看过的那一组人有效，所以每次换人都回到默认的「合并」。
    setDirection(next);
    setSelectedGuids([]);
    setPreview(null);
    setMode("merge");
    setErrorText("");
  };

  const switchStore = (next: string) => {
    if (busy || next === storeCode) return;
    setStoreCode(next);
    setSelectedGuids([]);
    setPreview(null);
    setMode("merge");
    setErrorText("");
  };

  const toggleUser = (userGuid: string) => {
    if (busy) return;
    setErrorText("");
    setMode("merge");
    setSelectedGuids((current) => togglePermissionCopySelection(current, userGuid, direction));
  };

  /** 向后端预览一次；成功后写入预览结果。 */
  const runPreview = useCallback(
    async (request: CopyUserDirectPermissionsInput, onSuccess?: () => void) => {
      if (busyRef.current) return;
      busyRef.current = true;
      setBusy(true);
      setErrorText("");
      try {
        const result = await onPreview(request);
        setPreview(result);
        onSuccess?.();
      } catch (error) {
        setErrorText(failure(error, "copy.previewFailed"));
      } finally {
        busyRef.current = false;
        setBusy(false);
      }
    },
    [failure, onPreview],
  );

  const goNext = () => {
    if (!input) return;
    void runPreview(input, () => setStep("confirm"));
  };

  const changeMode = (next: CopyPermissionMode) => {
    // 预览失败后旧预览已作废：再点一次当前方式要能重新计算，所以只在预览仍有效时才忽略重复点击。
    if (busy || (next === mode && previewCurrent)) return;
    setMode(next);
    const request = buildPermissionCopyInput(direction, currentEmployeeGuid, selectedGuids, next);
    // 方式变了旧预览立刻作废（previewCurrent 会变 false），并重新向后端计算差异。
    if (request) void runPreview(request);
  };

  const confirmCopy = useCallback(async () => {
    // 预览必须与当前选择一致且确有变化，才允许写入。
    if (!input || !previewCurrent || !summary?.hasChanges || busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setErrorText("");
    try {
      const result = await onCommit(input);
      onCopied(result);
    } catch (error) {
      setErrorText(failure(error, "copy.copyFailed"));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, [failure, input, onCommit, onCopied, previewCurrent, summary?.hasChanges]);

  const renderPickList = () => {
    if (!hasScope) {
      return <Text style={styles.stateText}>{t("copy.unavailable")}</Text>;
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
          <Text style={styles.stateText}>{t("copy.listFailed")}</Text>
          <Button compact mode="text" onPress={() => void usersQuery.refetch()}>
            {t("common:actions.retry")}
          </Button>
        </View>
      );
    }
    if (candidates.length === 0) {
      return (
        <Text style={styles.stateText}>
          {t(candidateTotal === 0 ? "copy.empty" : "copy.noMatch")}
        </Text>
      );
    }
    return (
      <View style={styles.list}>
        {candidates.map((user, index) => {
          const person = resolvePersonName(user);
          const selected = selectedGuids.some(
            (guid) => normalizeCopyGuid(guid) === normalizeCopyGuid(user.userGUID),
          );
          return (
            <Pressable
              key={user.userGUID}
              accessibilityRole={isToOthers ? "checkbox" : "radio"}
              accessibilityState={{ checked: selected }}
              accessibilityLabel={[person.primary, person.nickname, `@${user.username}`].filter(Boolean).join("，")}
              onPress={() => toggleUser(user.userGUID)}
              style={({ pressed }) => [
                styles.row,
                index > 0 && styles.rowDivider,
                selected && styles.rowSelected,
                pressed && styles.pressed,
              ]}
            >
              <PersonAvatar initials={person.initials} seed={user.userGUID} size={36} />
              <View style={styles.rowText}>
                <View style={styles.nameLine}>
                  <Text style={styles.rowName} numberOfLines={1}>
                    {person.primary}
                  </Text>
                  {person.nickname ? (
                    <Text style={styles.rowNickname} numberOfLines={1}>{`「${person.nickname}」`}</Text>
                  ) : null}
                </View>
                <Text style={styles.rowMeta} numberOfLines={1}>
                  @{user.username}
                </Text>
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

  const renderPickStep = () => (
    <>
      {renderPickList()}
      <Text style={styles.help}>{t("copy.help")}</Text>
      {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
    </>
  );

  const renderConfirmStep = () => {
    if (!preview || !summary) return null;
    const sourceName = resolveCopyTargetName(nameByGuid, preview.sourceUserGuid);
    const rows = buildCopyConfirmRows(
      preview,
      (guid) => resolveCopyTargetName(nameByGuid, guid),
      resolvePermissionName,
    );
    const skippedNames = listCopySkippedPermissionNames(preview, resolvePermissionName);
    return (
      <>
        <View style={styles.list}>
          {rows.map((row, index) => (
            <View
              key={row.userGuid}
              style={[styles.confirmRow, index > 0 && styles.rowDivider]}
              accessibilityLabel={[
                row.name,
                row.added.length ? `+${row.added.length}` : null,
                row.removed.length ? `−${row.removed.length}` : null,
                row.noChange ? t("copy.confirm.noChange") : null,
              ].filter(Boolean).join("，")}
            >
              <PersonAvatar
                initials={resolvePersonName({ fullName: row.name }).initials}
                seed={row.userGuid}
                size={36}
              />
              <View style={styles.rowText}>
                <Text style={styles.rowName} numberOfLines={1}>
                  {row.name}
                </Text>
                {row.added.length > 0 ? (
                  <Text style={styles.changeLine} numberOfLines={2}>
                    {`+ ${row.added.join("、")}`}
                  </Text>
                ) : null}
                {row.removed.length > 0 ? (
                  <Text style={[styles.changeLine, styles.removedLine]} numberOfLines={2}>
                    {`− ${row.removed.join("、")}`}
                  </Text>
                ) : null}
                {row.noChange ? (
                  <Text style={styles.rowMeta} numberOfLines={1}>
                    {row.unchangedCount > 0
                      ? t("copy.confirm.unchanged", { count: row.unchangedCount })
                      : t("copy.confirm.noChange")}
                  </Text>
                ) : null}
              </View>
              <View style={styles.badges}>
                {row.added.length > 0 ? <PeopleTag label={`+${row.added.length}`} tone="green" /> : null}
                {row.removed.length > 0 ? <PeopleTag label={`−${row.removed.length}`} tone="red" /> : null}
                {row.noChange ? <PeopleTag label="0" tone="gray" /> : null}
              </View>
            </View>
          ))}
        </View>

        {skippedNames.length > 0 ? (
          <NoticeBox tone="amber" icon="alert-outline">
            {t("copy.confirm.skipped", {
              count: skippedNames.length,
              names: skippedNames.map((name) => `「${name}」`).join("、"),
            })}
          </NoticeBox>
        ) : null}

        <SegmentTabs
          items={[
            { key: "merge" as CopyPermissionMode, label: t("copy.mode.merge") },
            { key: "replace" as CopyPermissionMode, label: t("copy.mode.replace") },
          ]}
          value={mode}
          onChange={changeMode}
        />
        <Text style={styles.hint}>
          {busy && !previewCurrent
            ? t("copy.confirm.recalculating")
            : t("copy.confirm.modeHint", { source: sourceName })}
        </Text>

        {!summary.hasChanges && previewCurrent ? (
          <NoticeBox tone="blue">{t("copy.confirm.nothingToCopy")}</NoticeBox>
        ) : null}
        {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
      </>
    );
  };

  const confirmSourceName = preview ? resolveCopyTargetName(nameByGuid, preview.sourceUserGuid) : "";
  const title =
    step === "confirm"
      ? t("copy.confirm.title")
      : isToOthers
        ? t("copy.toOthersTitle", { name: currentEmployeeName })
        : t("copy.fromColleagueTitle", { name: currentEmployeeName });
  const subtitle =
    step === "confirm"
      ? preview
        ? t("copy.confirm.subtitle", {
            source: confirmSourceName,
            mode: t(`copy.mode.${mode}Short`),
          })
        : undefined
      : hasScope
        ? t("copy.storeSubtitle", { store: scopeLabel })
        : undefined;

  const changedCount = summary?.changedTargets.length ?? 0;
  const footer =
    step === "confirm" ? (
      <>
        <Button
          mode="outlined"
          disabled={busy}
          onPress={() => {
            setErrorText("");
            setStep("pick");
          }}
          style={BUSINESS_UI.button}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t("copy.confirm.back")}
        </Button>
        <Button
          mode="contained"
          loading={busy}
          disabled={busy || !previewCurrent || !summary?.hasChanges}
          onPress={() => void confirmCopy()}
          style={[BUSINESS_UI.button, styles.primaryButton]}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {isToOthers
            ? t("copy.confirm.submit", { count: changedCount })
            : t("copy.confirm.submitSelf")}
        </Button>
      </>
    ) : (
      <Button
        mode="contained"
        loading={busy}
        disabled={busy || !input || !hasScope}
        onPress={goNext}
        style={[BUSINESS_UI.button, styles.primaryButton]}
        contentStyle={BUSINESS_UI.buttonContent}
      >
        {input
          ? isToOthers
            ? t("copy.next", { count: input.targetUserGuids.length })
            : t("copy.nextFromColleague")
          : t("copy.nextIdle")}
      </Button>
    );

  const storeChips = [
    ...(storeFilter.allowAll ? [{ key: "", label: t("copy.allStores") }] : []),
    ...storeFilter.options.map((item) => ({ key: item.storeCode, label: item.storeName || item.storeCode })),
  ];

  return (
    <PosPermissionSheet
      title={title}
      subtitle={subtitle}
      closeLabel={t("copy.close")}
      onDismiss={onDismiss}
      dismissable={!busy}
      header={
        step === "pick" ? (
          <View style={styles.headerStack}>
            <SegmentTabs
              items={[
                { key: "toOthers" as CopyPermissionDirection, label: t("copy.direction.toOthers") },
                { key: "fromColleague" as CopyPermissionDirection, label: t("copy.direction.fromColleague") },
              ]}
              value={direction}
              onChange={switchDirection}
            />
            {storeChips.length > 1 ? (
              <FilterChipRow items={storeChips} value={storeCode} onChange={switchStore} />
            ) : null}
            {hasScope ? (
              <Searchbar
                value={query}
                onChangeText={setQuery}
                placeholder={t("copy.searchPlaceholder")}
                style={styles.search}
                inputStyle={styles.searchInput}
                autoCorrect={false}
                autoCapitalize="none"
              />
            ) : null}
          </View>
        ) : undefined
      }
      footer={footer}
    >
      {step === "pick" ? renderPickStep() : renderConfirmStep()}
    </PosPermissionSheet>
  );
}

const styles = StyleSheet.create({
  headerStack: { gap: HB_SPACING.xs },
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
  confirmRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  rowSelected: { backgroundColor: POS_PERMISSION_COLORS.brandSoft },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  nameLine: { flexDirection: "row", alignItems: "center", gap: 6 },
  rowName: { flexShrink: 1, fontSize: 15, lineHeight: 21, fontWeight: "700", color: HB_COLORS.textPrimary },
  rowNickname: { fontSize: 12.5, fontWeight: "600", color: HB_COLORS.textSecondary },
  rowMeta: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  changeLine: { fontSize: 12.5, lineHeight: 18, color: HB_COLORS.textSecondary },
  removedLine: { color: HB_COLORS.danger },
  badges: { alignItems: "flex-end", gap: 4 },
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
  hint: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  errorText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.danger },
  primaryButton: { flex: 1 },
});
