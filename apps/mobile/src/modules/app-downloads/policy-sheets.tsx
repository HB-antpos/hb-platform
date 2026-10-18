import { useEffect, useMemo, useState, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { Chip, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
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
  diffNativePolicyForm,
  nativePolicyFormFrom,
  validateHandheldPolicy,
  validateNativePolicy,
  validateRegistration,
} from "./logic";
import type {
  AppUpdateApp,
  HandheldCandidate,
  HandheldPolicy,
  NativePolicy,
  NativePolicyForm,
  NativeRelease,
  Revision,
  StoreOption,
} from "./types";
import {
  ConfirmLines,
  KeyValueList,
  LabeledInput,
  Panel,
  Pill,
  PrimaryButton,
  RadioCard,
  SaveBar,
  SecondaryButton,
  SectionHeader,
  SegmentedControl,
  StatusDot,
  SwitchField,
  TextLink,
  ui,
} from "./ui";
import { listSeparator } from "@/shared/i18n/punctuation";

// 各原生应用在 EAS 配置中使用的公开 App Store Connect ID；登记时预填，仍允许管理员核对后修改。
export const defaultAppStoreIds: Record<AppUpdateApp, string> = {
  "mobile-ios": "6786073002",
  "pos-ipad": "6802176079",
  "pos-handheld": "6802182045",
};

export function PolicySummaryCard({
  title,
  version,
  enabled,
  mode,
  rows,
  warning,
  onEdit,
  copy,
  disabled,
}: {
  title: string;
  version: number;
  enabled: boolean;
  mode: string;
  rows: { label: string; value: string; muted?: boolean; strong?: boolean }[];
  warning?: string | null;
  onEdit: () => void;
  copy: Copy;
  disabled?: boolean;
}) {
  return (
    <View style={styles.section}>
      <SectionHeader
        title={title}
        meta={version > 0 ? fmt(copy.policy.version, { version }) : undefined}
      />
      <Panel>
        <View style={[ui.cardBody, styles.summaryBody]}>
          <View style={styles.summaryHead}>
            <View style={styles.summaryStatus}>
              <StatusDot tone={enabled ? "success" : "neutral"} />
              <Text style={ui.cardTitle}>
                {enabled ? copy.policy.enabled : copy.policy.disabled}
              </Text>
              {enabled ? <Text style={ui.muted}>{mode}</Text> : null}
            </View>
            <SecondaryButton
              label={copy.actions.edit}
              onPress={onEdit}
              disabled={disabled}
            />
          </View>
          {warning ? <Text style={ui.error}>{warning}</Text> : null}
          <KeyValueList rows={rows} />
        </View>
      </Panel>
    </View>
  );
}

/**
 * 策略编辑弹层：编辑 → 确认两步都在同一个弹层里完成。
 * 原生 Modal 之上无法再叠加 Paper Portal，所以确认步骤不能复用页面级确认框。
 */
export function PolicyEditorSheet({
  visible,
  title,
  subtitle,
  onDismiss,
  changes,
  reviewLines,
  validate,
  onConfirm,
  copy,
  children,
}: {
  visible: boolean;
  title: string;
  subtitle?: string;
  onDismiss: () => void;
  changes: string[];
  reviewLines: string[];
  validate: () => string | null;
  onConfirm: () => Promise<SaveResult>;
  copy: Copy;
  children: ReactNode;
}) {
  const [step, setStep] = useState<"edit" | "review">("edit");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (visible) {
      setStep("edit");
      setError("");
    }
  }, [visible]);
  const confirm = async () => {
    setBusy(true);
    const result = await onConfirm();
    setBusy(false);
    if (result.ok) onDismiss();
    else {
      setError(result.message);
      setStep("edit");
    }
  };
  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={
        step === "edit" ? (
          <SaveBar
            title={
              changes.length
                ? fmt(copy.editor.changes, { count: changes.length })
                : copy.editor.noChanges
            }
            detail={changes.length ? changes.join(listSeparator()) : copy.editor.saveHint}
            actionLabel={copy.editor.save}
            disabled={changes.length === 0}
            error={error}
            onPress={() => {
              const message = validate();
              if (message) setError(message);
              else {
                setError("");
                setStep("review");
              }
            }}
          />
        ) : (
          <View style={styles.row}>
            <SecondaryButton
              label={copy.editor.back}
              onPress={() => setStep("edit")}
              disabled={busy}
              style={styles.flex}
            />
            <PrimaryButton
              label={copy.editor.confirm}
              onPress={() => void confirm()}
              loading={busy}
              style={styles.flex}
            />
          </View>
        )
      }
    >
      {step === "edit" ? (
        children
      ) : (
        <ConfirmLines title={copy.editor.reviewTitle} lines={reviewLines} />
      )}
    </BusinessSheet>
  );
}

function MinimumVersionFields({
  form,
  setForm,
  copy,
}: {
  form: NativePolicyForm;
  setForm: (updater: (form: NativePolicyForm) => NativePolicyForm) => void;
  copy: Copy;
}) {
  return (
    <View style={styles.group}>
      <Text style={styles.groupTitle}>
        {copy.editor.minimumTitle}{" "}
        <Text style={ui.caption}>{copy.editor.optional}</Text>
      </Text>
      <View style={styles.row}>
        <LabeledInput
          label={copy.editor.version}
          value={form.minimumSupportedVersion}
          onChangeText={(value) =>
            setForm((old) => ({ ...old, minimumSupportedVersion: value }))
          }
          placeholder={copy.editor.versionPlaceholder}
          keyboardType="decimal-pad"
          style={styles.flex}
        />
        <LabeledInput
          label={copy.editor.build}
          value={form.minimumSupportedBuildNumber}
          onChangeText={(value) =>
            setForm((old) => ({ ...old, minimumSupportedBuildNumber: value }))
          }
          placeholder={copy.editor.buildPlaceholder}
          keyboardType="number-pad"
          style={styles.flex}
        />
      </View>
      <Text style={ui.caption}>{copy.editor.minimumHint}</Text>
    </View>
  );
}

export function StoreScopeFields({
  scope,
  stores,
  selected,
  onScope,
  onToggle,
  copy,
}: {
  scope: "all" | "stores";
  stores: StoreOption[];
  selected: string[];
  onScope: (scope: "all" | "stores") => void;
  onToggle: (guid: string) => void;
  copy: Copy;
}) {
  return (
    <View style={styles.group}>
      <Text style={styles.groupTitle}>{copy.editor.scope}</Text>
      <SegmentedControl
        options={[
          { value: "all", label: copy.editor.allDevices },
          { value: "stores", label: copy.editor.stores },
        ]}
        value={scope}
        onChange={onScope}
      />
      {scope === "stores" ? (
        <View style={styles.chips}>
          {stores.map((store) => (
            <Chip
              key={store.storeGuid}
              selected={selected.includes(store.storeGuid)}
              showSelectedCheck
              onPress={() => onToggle(store.storeGuid)}
              style={styles.chip}
            >
              {store.storeCode} · {store.storeName}
            </Chip>
          ))}
        </View>
      ) : null}
    </View>
  );
}

export function releaseLabel(
  release: Pick<NativeRelease, "version" | "buildNumber">,
) {
  return `${release.version} (${release.buildNumber})`;
}

/** 员工端 / iPad 原生策略编辑；iPad 额外支持按门店发布。 */
export function NativePolicyEditor({
  visible,
  onDismiss,
  title,
  app,
  policy,
  releases,
  stores,
  copy,
  onSave,
}: {
  visible: boolean;
  onDismiss: () => void;
  title: string;
  app: "mobile-ios" | "pos-ipad";
  policy: NativePolicy;
  releases: NativeRelease[];
  stores: StoreOption[];
  copy: Copy;
  onSave: (form: NativePolicyForm) => Promise<SaveResult>;
}) {
  const targeted = app === "pos-ipad";
  const baseline = useMemo(() => nativePolicyFormFrom(policy), [policy]);
  const [form, setFormState] = useState(baseline);
  // 打开时或冲突后重新读取到新的权威状态时，表单回到服务端数据
  useEffect(() => {
    if (visible) setFormState(baseline);
  }, [visible, baseline]);
  const setForm = (updater: (form: NativePolicyForm) => NativePolicyForm) =>
    setFormState((old) => updater(old));
  const selected = releases.find((item) => item.id === form.releaseId);
  const changes = fieldLabels(
    copy,
    diffNativePolicyForm(baseline, form, targeted),
  );
  const reviewLines = buildConfirmationSummary(
    form,
    {
      releaseLabel: selected ? releaseLabel(selected) : form.releaseId,
      targetLabel:
        targeted && form.targetScope === "stores"
          ? fmt(copy.policy.stores, { count: form.targetStoreGuids.length })
          : copy.policy.allDevices,
    },
    confirmationLabels(copy),
  );
  return (
    <PolicyEditorSheet
      visible={visible}
      title={title}
      subtitle={fmt(copy.policy.version, { version: policy.policyVersion })}
      onDismiss={onDismiss}
      changes={changes}
      reviewLines={reviewLines}
      validate={() => {
        const error = validateNativePolicy(form, targeted);
        return error ? copy.validation[error] : null;
      }}
      onConfirm={() => onSave(form)}
      copy={copy}
    >
      <Panel style={styles.switchPanel}>
        <SwitchField
          label={copy.editor.enabled}
          hint={copy.editor.enabledNativeHint}
          value={form.enabled}
          onChange={(enabled) => setForm((old) => ({ ...old, enabled }))}
        />
      </Panel>
      <View accessibilityRole="radiogroup" style={styles.group}>
        <SectionHeader
          title={copy.editor.release}
          meta={copy.editor.releaseHint}
        />
        {releases.length ? (
          releases.map((release) => (
            <RadioCard
              key={release.id}
              selected={form.releaseId === release.id}
              onPress={() =>
                setForm((old) => ({ ...old, releaseId: release.id }))
              }
              title={`${release.version} · Build ${release.buildNumber}`}
              meta={fmt(copy.download.verifiedAt, {
                time: formatDateTime(release.appleVerifiedAtUtc, false),
              })}
              badge={
                release.id === policy.releaseId ? (
                  <Pill label={copy.editor.current} tone="success" />
                ) : null
              }
            />
          ))
        ) : (
          <Text style={ui.muted}>{copy.releases.empty}</Text>
        )}
      </View>
      <MinimumVersionFields form={form} setForm={setForm} copy={copy} />
      <LabeledInput
        label={copy.editor.message}
        value={form.releaseMessage}
        onChangeText={(releaseMessage) =>
          setForm((old) => ({ ...old, releaseMessage }))
        }
        placeholder={copy.editor.messagePlaceholder}
        multiline
      />
      {targeted ? (
        <StoreScopeFields
          scope={form.targetScope}
          stores={stores}
          selected={form.targetStoreGuids}
          onScope={(targetScope) => setForm((old) => ({ ...old, targetScope }))}
          onToggle={(guid) =>
            setForm((old) => ({
              ...old,
              targetStoreGuids: old.targetStoreGuids.includes(guid)
                ? old.targetStoreGuids.filter((item) => item !== guid)
                : [...old.targetStoreGuids, guid],
            }))
          }
          copy={copy}
        />
      ) : null}
    </PolicyEditorSheet>
  );
}

export function handheldFormFrom(policy: HandheldPolicy): NativePolicyForm {
  return {
    enabled: policy.enabled,
    required: policy.required,
    releaseId: policy.candidateId ?? "",
    minimumSupportedVersion: policy.minimumSupportedVersion ?? "",
    minimumSupportedBuildNumber:
      policy.minimumSupportedBuildNumber == null
        ? ""
        : String(policy.minimumSupportedBuildNumber),
    releaseMessage: policy.releaseMessage ?? "",
    targetScope: "all",
    targetStoreGuids: [],
  };
}

export function candidateLabel(candidate: HandheldCandidate) {
  if (candidate.kind === "ota")
    return candidate.updateGroupId?.slice(0, 8) ?? candidate.id.slice(0, 8);
  return `${candidate.version ?? "—"} (${candidate.buildNumber ?? "—"})`;
}

/** 手持原生通道（Android / iOS）的策略编辑；候选不可激活时不能选。 */
export function HandheldNativeEditor({
  visible,
  onDismiss,
  title,
  policy,
  candidates,
  copy,
  onSave,
}: {
  visible: boolean;
  onDismiss: () => void;
  title: string;
  policy: HandheldPolicy;
  candidates: HandheldCandidate[];
  copy: Copy;
  onSave: (form: NativePolicyForm) => Promise<SaveResult>;
}) {
  const baseline = useMemo(() => handheldFormFrom(policy), [policy]);
  const [form, setFormState] = useState(baseline);
  useEffect(() => {
    if (visible) setFormState(baseline);
  }, [visible, baseline]);
  const setForm = (updater: (form: NativePolicyForm) => NativePolicyForm) =>
    setFormState((old) => updater(old));
  const selected =
    candidates.find((item) => item.id === form.releaseId) ?? null;
  const changes = fieldLabels(copy, diffNativePolicyForm(baseline, form));
  const reviewLines = buildConfirmationSummary(
    form,
    { releaseLabel: selected ? candidateLabel(selected) : form.releaseId },
    confirmationLabels(copy),
  );
  return (
    <PolicyEditorSheet
      visible={visible}
      title={title}
      subtitle={fmt(copy.policy.version, { version: policy.policyVersion })}
      onDismiss={onDismiss}
      changes={changes}
      reviewLines={reviewLines}
      validate={() => {
        const candidateValid =
          form.releaseId !== policy.candidateId || policy.candidateValid;
        const error = validateHandheldPolicy(
          form,
          policy.lane,
          selected,
          candidateValid,
          policy.blockedReason,
        );
        return error ? copy.validation[error] : null;
      }}
      onConfirm={() => onSave(form)}
      copy={copy}
    >
      <Panel style={styles.switchPanel}>
        <SwitchField
          label={copy.editor.enabled}
          hint={copy.editor.enabledNativeHint}
          value={form.enabled}
          onChange={(enabled) => setForm((old) => ({ ...old, enabled }))}
        />
        <SwitchField
          label={copy.editor.required}
          hint={copy.editor.requiredHint}
          value={form.required ?? false}
          onChange={(required) => setForm((old) => ({ ...old, required }))}
          warning
          divider
        />
      </Panel>
      <View accessibilityRole="radiogroup" style={styles.group}>
        <SectionHeader
          title={copy.editor.release}
          meta={copy.editor.candidateHint}
        />
        {candidates.length ? (
          candidates.map((item) => (
            <RadioCard
              key={item.id}
              selected={form.releaseId === item.id}
              disabled={!item.activatable}
              onPress={() => setForm((old) => ({ ...old, releaseId: item.id }))}
              title={candidateLabel(item)}
              meta={formatDateTime(item.createdAt)}
              badge={
                item.id === policy.candidateId ? (
                  <Pill label={copy.editor.current} tone="success" />
                ) : !item.activatable ? (
                  <Pill label={copy.editor.blocked} tone="warning" />
                ) : null
              }
            />
          ))
        ) : (
          <Text style={ui.muted}>{copy.empty}</Text>
        )}
      </View>
      <MinimumVersionFields form={form} setForm={setForm} copy={copy} />
      <LabeledInput
        label={copy.editor.message}
        value={form.releaseMessage}
        onChangeText={(releaseMessage) =>
          setForm((old) => ({ ...old, releaseMessage }))
        }
        placeholder={copy.editor.messagePlaceholder}
        multiline
      />
    </PolicyEditorSheet>
  );
}

export function ReleaseListCard({
  rows,
  copy,
  onRegister,
  disabled,
}: {
  rows: {
    id: string;
    version: string;
    build: string | null;
    meta: string;
    current: boolean;
  }[];
  copy: Copy;
  onRegister: () => void;
  disabled?: boolean;
}) {
  return (
    <View style={styles.sectionTight}>
      <SectionHeader
        title={copy.releases.title}
        action={
          <TextLink
            icon="plus"
            label={copy.actions.register}
            onPress={onRegister}
            disabled={disabled}
          />
        }
      />
      <Panel>
        {rows.length ? (
          rows.map((row, index) => (
            <View
              key={row.id}
              style={[styles.releaseRow, index > 0 && styles.divider]}
            >
              <View style={ui.flexText}>
                <Text style={styles.releaseTitle}>
                  {row.version}
                  {row.build ? (
                    <Text style={styles.releaseBuild}> Build {row.build}</Text>
                  ) : null}
                </Text>
                <Text style={ui.caption}>{row.meta}</Text>
              </View>
              {row.current ? (
                <Pill label={copy.releases.current} tone="success" />
              ) : null}
            </View>
          ))
        ) : (
          <View style={ui.cardBody}>
            <Text style={ui.muted}>{copy.releases.empty}</Text>
          </View>
        )}
      </Panel>
      <Text style={ui.caption}>{copy.releases.note}</Text>
    </View>
  );
}

export function RegisterSheet({
  visible,
  onDismiss,
  app,
  appName,
  latestLabel,
  copy,
  onSubmit,
}: {
  visible: boolean;
  onDismiss: () => void;
  app: AppUpdateApp;
  appName: string;
  latestLabel: string | null;
  copy: Copy;
  onSubmit: (value: {
    appStoreId: string;
    buildNumber: string;
    storefront: string;
  }) => Promise<SaveResult>;
}) {
  const [value, setValue] = useState({
    appStoreId: defaultAppStoreIds[app],
    buildNumber: "",
    storefront: "au",
  });
  const [editingStore, setEditingStore] = useState(false);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (visible) {
      setValue({
        appStoreId: defaultAppStoreIds[app],
        buildNumber: "",
        storefront: "au",
      });
      setEditingStore(false);
      setError("");
    }
  }, [visible, app]);
  const submit = async () => {
    const validation = validateRegistration(app, value);
    if (validation) {
      setError(copy.validation[validation]);
      // App Store ID 或商店区域出错时展开对应输入框
      if (validation !== "buildNumberInvalid") setEditingStore(true);
      return;
    }
    setError("");
    setBusy(true);
    const result = await onSubmit(value);
    setBusy(false);
    if (result.ok) onDismiss();
    else setError(result.message);
  };
  return (
    <BusinessSheet
      visible={visible}
      title={copy.register.title}
      subtitle={copy.register.subtitle}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={
        <PrimaryButton
          label={copy.register.submit}
          onPress={() => void submit()}
          loading={busy}
        />
      }
    >
      {editingStore ? (
        <View style={styles.row}>
          <LabeledInput
            label={copy.register.appStoreId}
            value={value.appStoreId}
            onChangeText={(appStoreId) =>
              setValue((old) => ({ ...old, appStoreId }))
            }
            keyboardType="number-pad"
            style={styles.flex}
          />
          <LabeledInput
            label={copy.register.storefront}
            value={value.storefront}
            onChangeText={(storefront) =>
              setValue((old) => ({ ...old, storefront }))
            }
            style={styles.storefront}
          />
        </View>
      ) : (
        <View style={styles.idBlock}>
          <View style={ui.flexText}>
            <Text style={ui.caption}>{copy.register.appStoreId}</Text>
            <Text style={styles.idValue}>
              {value.appStoreId} · {value.storefront.toUpperCase()}
            </Text>
            <Text style={ui.caption}>{appName}</Text>
          </View>
          <TextLink
            label={copy.actions.change}
            onPress={() => setEditingStore(true)}
          />
        </View>
      )}
      <LabeledInput
        label={copy.register.build}
        value={value.buildNumber}
        onChangeText={(buildNumber) =>
          setValue((old) => ({ ...old, buildNumber }))
        }
        placeholder={copy.register.buildPlaceholder}
        keyboardType="number-pad"
        helper={
          latestLabel
            ? fmt(copy.register.latest, { label: latestLabel })
            : undefined
        }
        autoFocus
      />
      {error ? <Text style={ui.error}>{error}</Text> : null}
    </BusinessSheet>
  );
}

export function RevisionsSheet({
  visible,
  onDismiss,
  revisions,
  laneLabels,
  copy,
}: {
  visible: boolean;
  onDismiss: () => void;
  revisions: Revision[];
  laneLabels?: Partial<Record<string, string>>;
  copy: Copy;
}) {
  const sorted = [...revisions].sort((a, b) =>
    b.createdAt.localeCompare(a.createdAt),
  );
  return (
    <BusinessSheet
      visible={visible}
      title={copy.ota.revisions}
      onDismiss={onDismiss}
    >
      {sorted.length ? (
        <Panel>
          {sorted.map((item, index) => (
            <View
              key={item.id}
              style={[styles.releaseRow, index > 0 && styles.divider]}
            >
              <View style={ui.flexText}>
                <Text style={styles.releaseTitle}>
                  {fmt(copy.policy.version, { version: item.policyVersion })}
                  <Text style={styles.releaseBuild}>
                    {" "}
                    {[
                      item.lane ? laneLabels?.[item.lane] : null,
                      item.operation || null,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </Text>
                </Text>
                <Text style={ui.caption}>
                  {formatDateTime(item.createdAt)} · {item.createdBy ?? "—"}
                </Text>
              </View>
            </View>
          ))}
        </Panel>
      ) : (
        <Text style={ui.muted}>{copy.ota.noRevisions}</Text>
      )}
    </BusinessSheet>
  );
}

/** 页面内表单（OTA）的保存确认弹层。 */
export function ConfirmSheet({
  visible,
  onDismiss,
  lines,
  onConfirm,
  copy,
}: {
  visible: boolean;
  onDismiss: () => void;
  lines: string[];
  onConfirm: () => Promise<SaveResult>;
  copy: Copy;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    if (visible) setError("");
  }, [visible]);
  return (
    <BusinessSheet
      visible={visible}
      title={copy.editor.reviewTitle}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={
        <View style={styles.row}>
          <SecondaryButton
            label={copy.editor.back}
            onPress={onDismiss}
            disabled={busy}
            style={styles.flex}
          />
          <PrimaryButton
            label={copy.editor.confirm}
            loading={busy}
            onPress={() =>
              void (async () => {
                setBusy(true);
                const result = await onConfirm();
                setBusy(false);
                if (result.ok) onDismiss();
                else setError(result.message);
              })()
            }
            style={styles.flex}
          />
        </View>
      }
    >
      <ConfirmLines lines={lines} />
      {error ? <Text style={ui.error}>{error}</Text> : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  section: { gap: HB_SPACING.sm },
  sectionTight: { gap: HB_SPACING.xs },
  summaryBody: { gap: 14 },
  summaryHead: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  summaryStatus: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  row: { flexDirection: "row", gap: HB_SPACING.sm },
  flex: { flex: 1 },
  storefront: { width: 96 },
  group: { gap: HB_SPACING.xs },
  groupTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  switchPanel: { paddingHorizontal: HB_SPACING.md },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  chip: { backgroundColor: HB_COLORS.white },
  releaseRow: {
    minHeight: 60,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: 10,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
  },
  releaseTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  releaseBuild: {
    fontSize: 13,
    fontWeight: "400",
    color: HB_COLORS.textSecondary,
  },
  divider: { borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  idBlock: {
    padding: 14,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    borderRadius: 10,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  idValue: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
});
