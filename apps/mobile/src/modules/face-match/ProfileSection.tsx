// 「门店档案」区（需要 FaceMatch.Manage）：读取并修改一家门店的人脸比对档案。
// 手机只编辑：运行模式、四个开关、命中自动确认阈值、允许拍摄对象、时区。其余合规记录（PIA、告示、律师确认、跨境审查等）不在手机上编辑，
// 页面只展示服务端返回的「合规记录未补齐」警告条数并提示到网页后台补齐。
// PUT 是整体替换：请求体由 buildProfileUpdateRequest 以服务端刚返回的档案为底构造，不编辑的字段原样带回，绝不清空。
import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Dialog, HelperText, Portal, Switch, Text, TextInput } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { isWriteBlocked } from "./access";
import { faceMatchApi } from "./api";
import { ALLOWED_SUBJECTS_MAX_LENGTH, AUTO_CONFIRM_SCORE_OFF, PROFILE_MODE_CAPTURE, PROFILE_MODE_MATCH } from "./constants";
import { formatLocalDateTime } from "./dates";
import { getDisabledReasonKey, getGateReportFromError } from "./errors";
import {
  PROFILE_MODE_OPTIONS,
  PROFILE_TIME_ZONE_OPTIONS,
  buildProfileUpdateRequest,
  countWarnings,
  gateKeyLabelKey,
  isProfileDirty,
  profileToEditValues,
  summarizeGates,
  summarizeProfileChange,
  validateProfileEdits,
  type ProfileEditValues,
} from "./profile-form";
import { useFaceErrorMessage, useIssueText } from "./shared";
import type { FaceGateReport, FaceStoreProfile } from "./types";
import { faceMatchKeys, useFaceMatchConfig, useFaceProfile } from "./use-face-match";
import { FaceCard, FilterChip, Notice, SectionTitle, Tag, faceStyles } from "./ui";

interface Props {
  storeCode: string;
  storeLabel: string;
  /** 有未保存的修改时通知父页面（切换门店、离开页面前要确认）。 */
  onDirtyChange: (dirty: boolean) => void;
  onBusyChange: (busy: boolean) => void;
  /** 保存结果的轻提示交给页面级 Snackbar（保存后本组件会因 key 变化重建，不能自己持有）。 */
  onNotify: (message: string) => void;
}

export function ProfileSection({ storeCode, storeLabel, onDirtyChange, onBusyChange, onNotify }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const configQuery = useFaceMatchConfig(true);
  const profileQuery = useFaceProfile(storeCode, true);

  // 门店变化 / 离开时清掉父页面记录的脏状态。
  useEffect(
    () => () => {
      onDirtyChange(false);
      onBusyChange(false);
    },
    [onBusyChange, onDirtyChange, storeCode],
  );

  let body;
  if (configQuery.isPending || profileQuery.isPending) {
    body = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (!configQuery.data || !profileQuery.data) {
    const error = configQuery.error ?? profileQuery.error;
    body = (
      <View style={faceStyles.state}>
        <EmptyState
          title={t("settings.profile.loadFailed")}
          description={describeError(error)}
          actionLabel={t("common:actions.retry")}
          onAction={() => {
            void configQuery.refetch();
            void profileQuery.refetch();
          }}
        />
      </View>
    );
  } else {
    body = (
      // 以门店 + 服务端更新时间作 key：保存或换店后整体重建，编辑值回到服务端最新的档案。
      <ProfileForm
        key={`${storeCode}:${profileQuery.data.updatedAtUtc ?? "new"}`}
        profile={profileQuery.data}
        writeBlocked={isWriteBlocked(configQuery.data)}
        disabledReasonKey={getDisabledReasonKey(configQuery.data.disabledReason)}
        storeLabel={storeLabel}
        onDirtyChange={onDirtyChange}
        onBusyChange={onBusyChange}
        onNotify={onNotify}
      />
    );
  }

  return (
    <View>
      <SectionTitle title={t("settings.profile.title")} />
      {body}
    </View>
  );
}

function ProfileForm({
  profile,
  writeBlocked,
  disabledReasonKey,
  storeLabel,
  onDirtyChange,
  onBusyChange,
  onNotify,
}: {
  profile: FaceStoreProfile;
  writeBlocked: boolean;
  disabledReasonKey: string;
  storeLabel: string;
  onDirtyChange: (dirty: boolean) => void;
  onBusyChange: (busy: boolean) => void;
  onNotify: (message: string) => void;
}) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const issueText = useIssueText();
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const [edits, setEdits] = useState<ProfileEditValues>(() => profileToEditValues(profile));
  const [showErrors, setShowErrors] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [gateOverride, setGateOverride] = useState<FaceGateReport | null>(null);

  const dirty = isProfileDirty(profile, edits);
  const errors = validateProfileEdits(edits);
  const patch = (next: Partial<ProfileEditValues>) => {
    setEdits((current) => ({ ...current, ...next }));
    setSaveError(null);
    setGateOverride(null);
  };

  const mutation = useMutation({
    mutationFn: () => faceMatchApi.updateProfile(profile.storeCode, buildProfileUpdateRequest(profile, edits)),
    onSuccess: (saved) => {
      setConfirmOpen(false);
      queryClient.setQueryData(faceMatchKeys.profile(userGuid, saved.storeCode), saved);
      void queryClient.invalidateQueries({ queryKey: faceMatchKeys.profile(userGuid, saved.storeCode) });
      // 保存成功后的闸门报告同样要显示警告：合规记录缺口不阻塞保存，但保存后必须继续可见。
      const warnings = countWarnings(summarizeGates(saved.gates));
      onNotify(warnings > 0 ? t("settings.profile.savedWithWarnings", { count: warnings }) : t("settings.profile.saved"));
    },
    onError: (error) => {
      setConfirmOpen(false);
      // 409 FACE_MATCH_GATE_NOT_MET：details 就是闸门报告，与保存前的缺项展示在同一处。
      setGateOverride(getGateReportFromError(error));
      setSaveError(describeError(error));
    },
  });

  useEffect(() => {
    onDirtyChange(dirty);
  }, [dirty, onDirtyChange]);
  useEffect(() => {
    onBusyChange(mutation.isPending);
  }, [mutation.isPending, onBusyChange]);

  const readOnly = writeBlocked || mutation.isPending;
  const gates = gateOverride ?? profile.gates;
  const rows = useMemo(() => summarizeGates(gates), [gates]);
  const warningCount = countWarnings(rows);
  const change = summarizeProfileChange(profile, edits);

  const requestSave = () => {
    setShowErrors(true);
    setSaveError(null);
    if (Object.keys(errors).length > 0 || !dirty || writeBlocked) return;
    setConfirmOpen(true);
  };

  // 阈值在确认框里的写法：0 = 「不自动确认」，其余带百分号。
  const autoConfirmLabel = (score: number) => (score === AUTO_CONFIRM_SCORE_OFF ? t("settings.profile.autoConfirmOff") : `${score}%`);

  const modeLabel = (mode: number) => {
    const option = PROFILE_MODE_OPTIONS.find((candidate) => candidate.value === mode);
    return option ? t(option.labelKey) : String(mode);
  };

  return (
    <FaceCard>
      <View style={faceStyles.rowBetween}>
        <Text style={faceStyles.strong} numberOfLines={1}>
          {storeLabel}
        </Text>
        {dirty ? <Tag tone="warning" label={t("settings.profile.unsaved")} /> : null}
      </View>
      <Text style={faceStyles.muted}>
        {profile.updatedAtUtc
          ? t("settings.profile.updatedAt", { time: formatLocalDateTime(profile.updatedAtUtc), by: profile.updatedBy ?? "-" })
          : t("settings.profile.neverSaved")}
      </Text>
      {writeBlocked ? <Notice tone="warning" text={t("disabled.notice", { reason: t(disabledReasonKey) })} /> : null}

      <Text style={styles.fieldLabel}>{t("settings.profile.fields.mode")}</Text>
      {PROFILE_MODE_OPTIONS.map((option) => {
        const selected = edits.mode === option.value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="radio"
            accessibilityState={{ selected, disabled: readOnly }}
            disabled={readOnly}
            onPress={() => patch({ mode: option.value })}
            style={[styles.radio, selected ? styles.radioOn : null, readOnly ? styles.disabled : null]}
          >
            <MaterialCommunityIcons name={selected ? "radiobox-marked" : "radiobox-blank"} size={22} color={selected ? HB_COLORS.action : HB_COLORS.textSecondary} />
            <View style={styles.radioBody}>
              <Text style={styles.radioTitle}>{t(option.labelKey)}</Text>
              <Text style={faceStyles.muted}>{t(option.hintKey)}</Text>
            </View>
          </Pressable>
        );
      })}

      <Text style={styles.fieldLabel}>{t("settings.profile.sections.switches")}</Text>
      <SwitchRow label={t("settings.profile.fields.evaluateMatch")} hint={t("settings.profile.hints.evaluateMatch")} value={edits.evaluateMatch} disabled={readOnly} onChange={(evaluateMatch) => patch({ evaluateMatch })} />
      <SwitchRow label={t("settings.profile.fields.staffExclusionEnabled")} hint={t("settings.profile.hints.staffExclusion")} value={edits.staffExclusionEnabled} disabled={readOnly} onChange={(staffExclusionEnabled) => patch({ staffExclusionEnabled })} />
      <SwitchRow label={t("settings.profile.fields.trafficDedupEnabled")} hint={t("settings.profile.hints.trafficDedup")} value={edits.trafficDedupEnabled} disabled={readOnly} onChange={(trafficDedupEnabled) => patch({ trafficDedupEnabled })} />
      <SwitchRow label={t("settings.profile.fields.unwelcomeListEnabled")} hint={t("settings.profile.hints.unwelcomeList")} value={edits.unwelcomeListEnabled} disabled={readOnly} onChange={(unwelcomeListEnabled) => patch({ unwelcomeListEnabled })} />

      {/* 命中自动确认阈值：紧跟「启用不受欢迎人员名单」开关。输入框存文本，校验与解析见 profile-form；填 0 表示不自动确认。 */}
      <TextInput
        mode="outlined"
        label={t("settings.profile.fields.unwelcomeAutoConfirmScore")}
        value={edits.unwelcomeAutoConfirmScore}
        onChangeText={(unwelcomeAutoConfirmScore) => patch({ unwelcomeAutoConfirmScore })}
        keyboardType="decimal-pad"
        maxLength={6}
        right={<TextInput.Affix text="%" />}
        error={showErrors && Boolean(errors.unwelcomeAutoConfirmScore)}
        disabled={readOnly}
        style={styles.input}
      />
      <HelperText type={showErrors && errors.unwelcomeAutoConfirmScore ? "error" : "info"} visible>
        {showErrors && errors.unwelcomeAutoConfirmScore ? issueText(errors.unwelcomeAutoConfirmScore) : t("settings.profile.hints.unwelcomeAutoConfirmScore")}
      </HelperText>

      <TextInput
        mode="outlined"
        label={edits.mode === PROFILE_MODE_CAPTURE ? `${t("settings.profile.fields.allowedSubjects")} *` : t("settings.profile.fields.allowedSubjects")}
        value={edits.allowedSubjects}
        onChangeText={(allowedSubjects) => patch({ allowedSubjects })}
        maxLength={ALLOWED_SUBJECTS_MAX_LENGTH}
        multiline
        error={showErrors && Boolean(errors.allowedSubjects)}
        disabled={readOnly}
        style={styles.input}
      />
      <HelperText type={showErrors && errors.allowedSubjects ? "error" : "info"} visible>
        {showErrors && errors.allowedSubjects ? issueText(errors.allowedSubjects) : t("settings.profile.hints.allowedSubjects")}
      </HelperText>

      <Text style={styles.fieldLabel}>{t("settings.profile.fields.timeZoneId")}</Text>
      <View style={faceStyles.rowWrap}>
        {PROFILE_TIME_ZONE_OPTIONS.map((option) => (
          <FilterChip key={option.value || "auto"} label={t(option.labelKey)} selected={edits.timeZoneId === option.value} disabled={readOnly} onPress={() => patch({ timeZoneId: option.value })} />
        ))}
      </View>
      <Text style={faceStyles.muted}>
        {t("settings.profile.hints.timeZone", { zone: profile.effectiveTimeZoneId || "-", jurisdiction: profile.jurisdiction ?? "-" })}
      </Text>

      {/* 启用闸门：仍阻塞保存的缺项 + 不阻塞但必须持续可见的合规记录缺口。 */}
      <Text style={styles.fieldLabel}>{t("settings.gate.title")}</Text>
      {rows.length === 0 ? (
        <Notice text={t("settings.gate.nothingRequested")} />
      ) : (
        rows.map((row) => (
          <View key={row.facet} style={styles.gateRow}>
            <Notice
              tone={row.missing.length === 0 ? "success" : "warning"}
              text={t("settings.gate.facetLine", {
                facet: row.facetLabelKey ? t(row.facetLabelKey) : row.facet,
                status: row.missing.length === 0 ? t("settings.gate.satisfied") : t("settings.gate.missingTitle", { count: row.missing.length }),
              })}
            />
            {row.missing.map((key) => (
              <Text key={`m-${key}`} style={styles.gateKey}>{`· ${gateKeyLabelKey(key) ? t(gateKeyLabelKey(key) as string) : key}`}</Text>
            ))}
            {row.warnings.length > 0 ? (
              <>
                <Notice tone="danger" text={t("settings.gate.warningTitle", { count: row.warnings.length })} />
                {row.warnings.map((key) => (
                  <Text key={`w-${key}`} style={styles.gateKey}>{`· ${gateKeyLabelKey(key) ? t(gateKeyLabelKey(key) as string) : key}`}</Text>
                ))}
              </>
            ) : null}
          </View>
        ))
      )}
      {warningCount > 0 ? (
        <Notice tone="danger" icon="alert-octagon-outline" text={t("settings.profile.complianceWarning", { count: warningCount })} />
      ) : null}
      <Text style={faceStyles.muted}>{t("settings.profile.webOnly")}</Text>
      {/* 闸门按「已保存的档案」由服务端算出，未保存的修改不会改变它；提示用户以保存结果为准，不暗示「现在保存会失败」。 */}
      {rows.length > 0 && dirty ? <Text style={faceStyles.muted}>{t("settings.gate.savedStateHint")}</Text> : null}
      {saveError ? <Notice tone="danger" text={saveError} /> : null}

      <View style={styles.actions}>
        <Button mode="outlined" disabled={!dirty || mutation.isPending} onPress={() => { setEdits(profileToEditValues(profile)); setShowErrors(false); setSaveError(null); setGateOverride(null); }}>
          {t("settings.profile.reset")}
        </Button>
        <Button mode="contained" loading={mutation.isPending} disabled={readOnly || !dirty} onPress={requestSave} style={styles.saveButton}>
          {t("settings.profile.save")}
        </Button>
      </View>

      <Portal>
        <Dialog visible={confirmOpen} onDismiss={mutation.isPending ? undefined : () => setConfirmOpen(false)} dismissable={!mutation.isPending}>
          <Dialog.Title>{t("settings.profile.confirmTitle", { store: storeLabel })}</Dialog.Title>
          <Dialog.Content>
            {change.modeChanged ? (
              <Text style={styles.confirmLine}>{t("settings.profile.confirmMode", { from: modeLabel(change.fromMode), to: modeLabel(change.toMode) })}</Text>
            ) : null}
            {change.switchesChanged.map((key) => (
              <Text key={key} style={styles.confirmLine}>
                {t("settings.profile.confirmSwitch", {
                  name: t(`settings.profile.fields.${key}`),
                  state: t(edits[key] ? "settings.profile.on" : "settings.profile.off"),
                })}
              </Text>
            ))}
            {change.autoConfirmChange ? (
              <Text style={styles.confirmLine}>
                {t("settings.profile.confirmAutoConfirm", {
                  from: autoConfirmLabel(change.autoConfirmChange.from),
                  to: autoConfirmLabel(change.autoConfirmChange.to),
                })}
              </Text>
            ) : null}
            {change.startsProcessing ? (
              <Text style={styles.confirmWarn}>
                {t(edits.mode === PROFILE_MODE_MATCH ? "settings.profile.confirmMatchWarning" : "settings.profile.confirmCaptureWarning")}
              </Text>
            ) : null}
            <Text style={styles.confirmLine}>{t("settings.profile.confirmKeep")}</Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setConfirmOpen(false)} disabled={mutation.isPending}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" loading={mutation.isPending} disabled={mutation.isPending} onPress={() => mutation.mutate()}>
              {t("settings.profile.confirmOk")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    </FaceCard>
  );
}

function SwitchRow({
  label,
  hint,
  value,
  disabled,
  onChange,
}: {
  label: string;
  hint: string;
  value: boolean;
  disabled: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <View style={styles.switchRow}>
      <View style={styles.switchText}>
        <Text style={styles.switchLabel}>{label}</Text>
        <Text style={faceStyles.muted}>{hint}</Text>
      </View>
      <Switch value={value} disabled={disabled} onValueChange={onChange} accessibilityLabel={label} />
    </View>
  );
}

const styles = StyleSheet.create({
  fieldLabel: { marginTop: HB_SPACING.xs, fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  radio: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  radioOn: { borderColor: HB_COLORS.action, backgroundColor: "#EAF2FF" },
  radioBody: { flex: 1, gap: 2 },
  radioTitle: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  disabled: { opacity: 0.55 },
  switchRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingVertical: 4 },
  switchText: { flex: 1, gap: 2 },
  switchLabel: { fontSize: 14, color: HB_COLORS.textPrimary },
  input: { backgroundColor: HB_COLORS.white },
  gateRow: { gap: 4 },
  gateKey: { fontSize: 13, color: HB_COLORS.textPrimary, paddingLeft: HB_SPACING.xs },
  actions: { flexDirection: "row", gap: HB_SPACING.xs, marginTop: HB_SPACING.xs },
  saveButton: { flex: 1 },
  confirmLine: { fontSize: 14, color: HB_COLORS.textPrimary, marginBottom: 6 },
  confirmWarn: { fontSize: 14, fontWeight: "700", color: HB_COLORS.danger, marginBottom: 6 },
});
