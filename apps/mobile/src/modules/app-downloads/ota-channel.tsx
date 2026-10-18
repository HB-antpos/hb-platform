import { useEffect, useMemo, useState, type ReactNode } from "react";
import { StyleSheet, Text as NativeText, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  confirmationLabels,
  fieldLabels,
  fmt,
  formatDateTime,
  type Copy,
  type SaveResult,
} from "./copy";
import {
  buildConfirmationSummary,
  diffOtaPolicyForm,
  validateHandheldPolicy,
  validateOtaPolicy,
  validateTargetScope,
  type AppDownloadsApp,
} from "./logic";
import {
  ConfirmSheet,
  RevisionsSheet,
  StoreScopeFields,
  candidateLabel,
} from "./policy-sheets";
import type { SectionData } from "./section-data";
import type {
  AppDownloadEnvironment,
  AppDownloadPlatform,
  HandheldPolicy,
  NativePolicyForm,
  OtaPolicyForm,
  Revision,
  StoreOption,
} from "./types";
import {
  KeyValueList,
  LabeledInput,
  NavRow,
  Panel,
  Pill,
  RadioCard,
  SaveBar,
  ScreenFrame,
  SectionHeader,
  SegmentedControl,
  StatusDot,
  SwitchField,
  TextLink,
  ui,
  type Tone,
} from "./ui";
import { listSeparator } from "@/shared/i18n/punctuation";

type OtaData = Extract<
  SectionData,
  { kind: "mobile-ota" | "ipad-ota" | "handheld" }
>;

interface OtaOption {
  id: string;
  title: string;
  meta: string;
  detail?: string;
  disabled?: boolean;
  badge?: { label: string; tone: Tone };
}

/** 三种 OTA 数据统一成同一个界面模型，页面只关心展示和表单。 */
interface OtaModel {
  resetKey: string;
  metaLine: string;
  enabled: boolean;
  required: boolean;
  live: { title: string; published: string; content: string | null } | null;
  currentId: string | null;
  options: OtaOption[];
  baseline: OtaPolicyForm;
  targeted: boolean;
  stores: StoreOption[];
  revisions: Revision[] | null;
  warning: string | null;
  handheldPolicy: HandheldPolicy | null;
}

const PREVIEW_COUNT = 5;

function shortId(value: string | null | undefined) {
  return value ? value.slice(0, 8) : "—";
}

function runtimeMeta(time: string, runtime: string | null | undefined) {
  return runtime ? `${time} · runtime ${runtime}` : time;
}

function buildModel(
  data: OtaData,
  profile: AppDownloadEnvironment,
  platform: AppDownloadPlatform,
  copy: Copy,
): OtaModel | null {
  const platformLabel = copy.platforms[platform];
  if (data.kind === "mobile-ota") {
    const { policy } = data;
    const target =
      policy.targetRelease ??
      data.releases.find((item) => item.id === policy.targetReleaseId) ??
      null;
    const baseline: OtaPolicyForm = {
      enabled: policy.enabled,
      required: policy.required,
      targetReleaseId: policy.targetReleaseId ?? "",
      releaseMessage: policy.releaseMessage ?? "",
    };
    return {
      resetKey: `mobile|${profile}|${platform}|${policy.policyVersion}|${JSON.stringify(baseline)}`,
      metaLine: fmt(copy.ota.liveMeta, {
        environment: copy.environments[profile],
        platform: platformLabel,
        version: policy.policyVersion,
      }),
      enabled: policy.enabled,
      required: policy.required,
      live: target
        ? {
            title: runtimeMeta(
              shortId(target.updateGroupId),
              policy.targetRuntimeVersion ?? target.runtimeVersion,
            ),
            published: formatDateTime(target.publishedAtUtc),
            content: target.message ?? policy.releaseMessage,
          }
        : null,
      currentId: policy.targetReleaseId,
      options: data.releases.map((item) => ({
        id: item.id,
        title: shortId(item.updateGroupId),
        meta: runtimeMeta(
          formatDateTime(item.publishedAtUtc),
          item.runtimeVersion,
        ),
        detail: item.message ?? undefined,
        badge: item.isRollback
          ? { label: copy.ota.rollback, tone: "warning" }
          : undefined,
      })),
      baseline,
      targeted: false,
      stores: [],
      revisions: data.revisions,
      warning: null,
      handheldPolicy: null,
    };
  }
  if (data.kind === "ipad-ota") {
    const { rollout } = data;
    const target =
      rollout.release ??
      data.releases.find((item) => item.id === rollout.releaseId) ??
      null;
    const baseline: OtaPolicyForm = {
      enabled: rollout.enabled,
      required: rollout.forceUpdate,
      targetReleaseId: rollout.releaseId ?? "",
      releaseMessage: rollout.releaseMessage ?? "",
      targetScope: rollout.targetScope,
      targetStoreGuids: rollout.targetStoreGuids,
    };
    return {
      resetKey: `ipad|${rollout.policyVersion}|${JSON.stringify(baseline)}`,
      metaLine: `${copy.platforms.ipad} · ${fmt(copy.policy.version, { version: rollout.policyVersion })}`,
      enabled: rollout.enabled,
      required: rollout.forceUpdate,
      live: target
        ? {
            title: runtimeMeta(
              shortId(target.updateGroupId),
              target.runtimeVersion,
            ),
            published: formatDateTime(target.publishedAtUtc),
            content: rollout.releaseMessage,
          }
        : null,
      currentId: rollout.releaseId,
      options: data.releases.map((item) => ({
        id: item.id,
        title: shortId(item.updateGroupId),
        meta: runtimeMeta(
          formatDateTime(item.publishedAtUtc),
          item.runtimeVersion,
        ),
        badge: item.isRollback
          ? { label: copy.ota.rollback, tone: "warning" }
          : undefined,
      })),
      baseline,
      targeted: true,
      stores: data.stores,
      revisions: null,
      warning: null,
      handheldPolicy: null,
    };
  }
  const lane = `${platform}-ota` as const;
  const policy = data.policies.find((item) => item.lane === lane);
  if (!policy) return null;
  const candidates = data.candidates.filter((item) => item.lane === lane);
  const target =
    candidates.find((item) => item.id === policy.candidateId) ??
    policy.candidate;
  const baseline: OtaPolicyForm = {
    enabled: policy.enabled,
    required: policy.required,
    targetReleaseId: policy.candidateId ?? "",
    releaseMessage: policy.releaseMessage ?? "",
  };
  return {
    resetKey: `handheld|${lane}|${policy.policyVersion}|${JSON.stringify(baseline)}`,
    metaLine: `${platformLabel} OTA · ${fmt(copy.policy.version, { version: policy.policyVersion })}`,
    enabled: policy.enabled,
    required: policy.required,
    live: target
      ? {
          title: runtimeMeta(candidateLabel(target), target.runtimeVersion),
          published: formatDateTime(target.createdAt),
          content: target.message ?? policy.releaseMessage,
        }
      : null,
    currentId: policy.candidateId,
    options: candidates.map((item) => ({
      id: item.id,
      title: candidateLabel(item),
      meta: runtimeMeta(formatDateTime(item.createdAt), item.runtimeVersion),
      detail: item.message ?? undefined,
      disabled: !item.activatable,
      badge: item.activatable
        ? undefined
        : { label: copy.editor.blocked, tone: "warning" },
    })),
    baseline,
    targeted: false,
    stores: [],
    revisions: data.revisions.filter((item) => item.lane === lane),
    warning:
      policy.candidateId && !policy.candidateValid ? copy.policy.blocked : null,
    handheldPolicy: policy,
  };
}

function handheldForm(form: OtaPolicyForm): NativePolicyForm {
  return {
    enabled: form.enabled,
    required: form.required,
    releaseId: form.targetReleaseId,
    minimumSupportedVersion: "",
    minimumSupportedBuildNumber: "",
    releaseMessage: form.releaseMessage,
    targetScope: "all",
    targetStoreGuids: [],
  };
}

export function OtaChannel({
  header,
  app,
  data,
  profile,
  onProfileChange,
  platform,
  onPlatformChange,
  copy,
  saving,
  refreshing,
  onRefresh,
  onSaveMobile,
  onSaveIpad,
  onSaveHandheld,
}: {
  header: ReactNode;
  app: AppDownloadsApp;
  data: OtaData;
  profile: AppDownloadEnvironment;
  onProfileChange: (profile: AppDownloadEnvironment) => void;
  platform: AppDownloadPlatform;
  onPlatformChange: (platform: AppDownloadPlatform) => void;
  copy: Copy;
  saving: boolean;
  refreshing: boolean;
  onRefresh: () => void;
  onSaveMobile: (form: OtaPolicyForm) => Promise<SaveResult>;
  onSaveIpad: (form: OtaPolicyForm) => Promise<SaveResult>;
  onSaveHandheld: (
    policy: HandheldPolicy,
    form: NativePolicyForm,
  ) => Promise<SaveResult>;
}) {
  const model = useMemo(
    () => buildModel(data, profile, platform, copy),
    [data, profile, platform, copy],
  );
  const [form, setForm] = useState<OtaPolicyForm | null>(
    model?.baseline ?? null,
  );
  const [error, setError] = useState("");
  const [confirming, setConfirming] = useState(false);
  const [showAll, setShowAll] = useState(false);
  const [revisionsOpen, setRevisionsOpen] = useState(false);
  // 切换平台/环境或重新读取到新的权威状态时，表单回到服务端数据
  useEffect(() => {
    setForm(model?.baseline ?? null);
    setError("");
    setShowAll(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model?.resetKey]);

  const filters =
    app === "ipad" ? null : (
      <Panel style={styles.filters}>
        <View style={styles.filterRow}>
          <NativeText style={styles.filterLabel}>
            {copy.platforms.label}
          </NativeText>
          <SegmentedControl
            options={[
              { value: "ios", label: copy.platforms.ios },
              { value: "android", label: copy.platforms.android },
            ]}
            value={platform}
            onChange={onPlatformChange}
            disabled={saving}
            style={styles.filterControl}
          />
        </View>
        {app === "mobile" ? (
          <View style={styles.filterRow}>
            <NativeText style={styles.filterLabel}>
              {copy.environments.label}
            </NativeText>
            <SegmentedControl
              options={[
                { value: "production", label: copy.environments.production },
                { value: "preview", label: copy.environments.preview },
              ]}
              value={profile}
              onChange={onProfileChange}
              disabled={saving}
              style={styles.filterControl}
            />
          </View>
        ) : null}
      </Panel>
    );

  if (!model || !form) {
    return (
      <ScreenFrame
        header={header}
        refreshing={refreshing}
        onRefresh={onRefresh}
      >
        {filters}
        <Text style={ui.muted}>{copy.empty}</Text>
      </ScreenFrame>
    );
  }

  const changes = diffOtaPolicyForm(model.baseline, form, model.targeted);
  const selected = model.options.find(
    (item) => item.id === form.targetReleaseId,
  );
  const reviewLines = buildConfirmationSummary(
    form,
    {
      releaseLabel: selected?.title ?? form.targetReleaseId,
      targetLabel:
        model.targeted && form.targetScope === "stores"
          ? fmt(copy.policy.stores, {
              count: form.targetStoreGuids?.length ?? 0,
            })
          : copy.policy.allDevices,
    },
    confirmationLabels(copy),
  );
  const validate = () => {
    if (model.handheldPolicy) {
      const policy = model.handheldPolicy;
      const candidate =
        data.kind === "handheld"
          ? (data.candidates.find(
              (item) =>
                item.id === form.targetReleaseId && item.lane === policy.lane,
            ) ?? null)
          : null;
      const candidateValid =
        form.targetReleaseId !== policy.candidateId || policy.candidateValid;
      return validateHandheldPolicy(
        handheldForm(form),
        policy.lane,
        candidate,
        candidateValid,
        policy.blockedReason,
      );
    }
    return (
      validateOtaPolicy(form) ??
      (model.targeted
        ? validateTargetScope(form.targetScope, form.targetStoreGuids)
        : null)
    );
  };
  const save = (): Promise<SaveResult> => {
    if (model.handheldPolicy)
      return onSaveHandheld(model.handheldPolicy, handheldForm(form));
    return app === "ipad" ? onSaveIpad(form) : onSaveMobile(form);
  };
  const visibleOptions = showAll
    ? model.options
    : model.options.slice(0, PREVIEW_COUNT);
  const latestRevision = model.revisions
    ? [...model.revisions].sort((a, b) =>
        b.createdAt.localeCompare(a.createdAt),
      )[0]
    : null;

  return (
    <>
      <ScreenFrame
        header={header}
        refreshing={refreshing}
        onRefresh={onRefresh}
        footer={
          <SaveBar
            title={
              changes.length
                ? fmt(copy.editor.changes, { count: changes.length })
                : copy.editor.noChanges
            }
            detail={
              changes.length
                ? fieldLabels(copy, changes).join(listSeparator())
                : copy.editor.saveHint
            }
            actionLabel={copy.editor.save}
            disabled={changes.length === 0 || saving}
            error={error}
            onPress={() => {
              const validation = validate();
              if (validation) setError(copy.validation[validation]);
              else {
                setError("");
                setConfirming(true);
              }
            }}
          />
        }
      >
        {filters}

        <View style={styles.group}>
          <SectionHeader title={copy.ota.live} meta={model.metaLine} />
          <Panel>
            <View style={[ui.cardBody, styles.liveBody]}>
              <View style={styles.statusRow}>
                <StatusDot tone={model.enabled ? "success" : "neutral"} />
                <Text style={ui.cardTitle}>
                  {model.enabled ? copy.policy.enabled : copy.policy.disabled}
                </Text>
                {model.enabled ? (
                  <Text style={ui.muted}>
                    {model.required
                      ? copy.policy.required
                      : copy.policy.optional}
                  </Text>
                ) : null}
              </View>
              {model.warning ? (
                <Text style={ui.error}>{model.warning}</Text>
              ) : null}
              <KeyValueList
                rows={[
                  {
                    label: copy.ota.target,
                    value: model.live?.title ?? copy.policy.notSet,
                    strong: Boolean(model.live),
                    muted: !model.live,
                  },
                  ...(model.live
                    ? [
                        {
                          label: copy.ota.published,
                          value: model.live.published,
                        },
                        {
                          label: copy.ota.content,
                          value: model.live.content ?? copy.policy.notFilled,
                          muted: !model.live.content,
                        },
                      ]
                    : []),
                ]}
              />
            </View>
          </Panel>
        </View>

        <View accessibilityRole="radiogroup" style={styles.list}>
          <SectionHeader title={copy.ota.switchTarget} />
          {visibleOptions.length ? (
            visibleOptions.map((option) => (
              <RadioCard
                key={option.id}
                selected={form.targetReleaseId === option.id}
                disabled={option.disabled || saving}
                onPress={() =>
                  setForm(
                    (old) => old && { ...old, targetReleaseId: option.id },
                  )
                }
                title={option.title}
                meta={option.meta}
                detail={option.detail}
                badge={
                  option.id === model.currentId ? (
                    <Pill label={copy.editor.current} tone="success" />
                  ) : option.badge ? (
                    <Pill label={option.badge.label} tone={option.badge.tone} />
                  ) : null
                }
              />
            ))
          ) : (
            <Text style={ui.muted}>{copy.ota.empty}</Text>
          )}
          {model.options.length > PREVIEW_COUNT && !showAll ? (
            <TextLink
              label={`${copy.actions.more} (${model.options.length - PREVIEW_COUNT})`}
              icon="chevron-down"
              onPress={() => setShowAll(true)}
            />
          ) : null}
        </View>

        <Panel style={styles.switches}>
          <SwitchField
            label={copy.editor.enabled}
            hint={copy.editor.enabledOtaHint}
            value={form.enabled}
            onChange={(enabled) => setForm((old) => old && { ...old, enabled })}
            disabled={saving}
          />
          <SwitchField
            label={copy.editor.required}
            hint={copy.editor.requiredHint}
            value={form.required}
            onChange={(required) =>
              setForm((old) => old && { ...old, required })
            }
            disabled={saving}
            warning
            divider
          />
        </Panel>

        <LabeledInput
          label={copy.editor.message}
          value={form.releaseMessage}
          onChangeText={(releaseMessage) =>
            setForm((old) => old && { ...old, releaseMessage })
          }
          placeholder={copy.editor.messagePlaceholder}
          multiline
        />

        {model.targeted ? (
          <StoreScopeFields
            scope={form.targetScope ?? "all"}
            stores={model.stores}
            selected={form.targetStoreGuids ?? []}
            onScope={(targetScope) =>
              setForm((old) => old && { ...old, targetScope })
            }
            onToggle={(guid) =>
              setForm(
                (old) =>
                  old && {
                    ...old,
                    targetStoreGuids: (old.targetStoreGuids ?? []).includes(
                      guid,
                    )
                      ? (old.targetStoreGuids ?? []).filter(
                          (item) => item !== guid,
                        )
                      : [...(old.targetStoreGuids ?? []), guid],
                  },
              )
            }
            copy={copy}
          />
        ) : null}

        {model.revisions ? (
          <NavRow
            icon="clock-outline"
            title={copy.ota.revisions}
            subtitle={
              latestRevision
                ? fmt(copy.ota.revisionsLatest, {
                    version: latestRevision.policyVersion,
                    time: formatDateTime(latestRevision.createdAt),
                  })
                : copy.ota.noRevisions
            }
            onPress={() => setRevisionsOpen(true)}
          />
        ) : null}
      </ScreenFrame>

      <ConfirmSheet
        visible={confirming}
        onDismiss={() => setConfirming(false)}
        lines={reviewLines}
        onConfirm={save}
        copy={copy}
      />
      {model.revisions ? (
        <RevisionsSheet
          visible={revisionsOpen}
          onDismiss={() => setRevisionsOpen(false)}
          revisions={model.revisions}
          copy={copy}
        />
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  group: { gap: HB_SPACING.sm },
  list: { gap: HB_SPACING.xs },
  filters: { padding: HB_SPACING.sm, gap: HB_SPACING.xs },
  filterRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  filterLabel: {
    width: 40,
    fontSize: 13,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
  },
  filterControl: { flex: 1 },
  liveBody: { gap: 14 },
  statusRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  switches: { paddingHorizontal: HB_SPACING.md },
});
