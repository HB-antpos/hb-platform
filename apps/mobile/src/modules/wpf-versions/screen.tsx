import * as Clipboard from "expo-clipboard";
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { Linking, Pressable, ScrollView, StyleSheet, View } from "react-native";
import QRCode from "react-native-qrcode-svg";
import {
  ActivityIndicator,
  Button,
  Checkbox,
  Divider,
  HelperText,
  IconButton,
  Menu,
  Modal,
  Portal,
  Searchbar,
  SegmentedButtons,
  Snackbar,
  Switch,
  Text,
  TextInput,
} from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { formatDateTime } from "@/modules/app-downloads/copy";
import type { WpfChannel } from "@/modules/app-downloads/release-center-nav";
import {
  ConfirmLines,
  Panel,
  Pill,
  PrimaryButton,
  ScreenFrame,
  SecondaryButton,
  SectionHeader,
  TextLink,
  ui,
} from "@/modules/app-downloads/ui";
import {
  getWpfReleases,
  getWpfTargetDevices,
  getWpfTargetStores,
  saveWpfPolicy,
  updateWpfRelease,
} from "./api";
import {
  buildWpfDecisionLadder,
  canSavePolicy,
  compareVersions,
  createLatestRequestGuard,
  formatFileSize,
  getErrorMessage,
  getPolicySummary,
  getPolicyValidationError,
  getWpfNewerActiveVersions,
  getWpfPolicyMode,
  inferRollback,
  maskSha256,
  normalizeTargetScope,
  normalizeVersion,
  policySummaryMatchesRequest,
  type WpfDecisionSegment,
  type WpfPolicyMode,
} from "./logic";
import type {
  WpfDeviceOption,
  WpfPolicySummary,
  WpfRelease,
  WpfReleasePolicyRequest,
  WpfReleaseQuery,
  WpfStoreOption,
} from "./types";

const PAGE_SIZE = 10;
const DEVICE_PAGE_SIZE = 30;
/** 策略弹层里最多列出多少个可点选的已启用版本。 */
const QUICK_VERSION_LIMIT = 8;

type DetailField = [string, string];

/** 服务器上的策略快照：摘要 + 全量版本（算更新方式、待投放版本都要用全量）。 */
interface PolicySnapshot {
  summary: WpfPolicySummary | null;
  releases: WpfRelease[];
}

function deviceLabel(device: WpfDeviceOption) {
  return [
    device.storeCode ? `[${device.storeCode}]` : "",
    device.systemDeviceNumber || `#${device.deviceRegistrationId}`,
    device.storeName,
    device.remarks,
  ]
    .filter(Boolean)
    .join(" · ");
}

function isSafeDownloadUrl(value: string | null | undefined): value is string {
  if (!value?.trim()) return false;
  try {
    const protocol = new URL(value.trim()).protocol.toLowerCase();
    return protocol === "https:" || protocol === "http:";
  } catch {
    return false;
  }
}

function isValidSha256(value: string) {
  return !value.trim() || /^[a-f\d]{64}$/i.test(value.trim());
}

function normalizeOptionalText(value: string | null | undefined) {
  return value?.trim() || null;
}

function normalizeSha256(value: string | null | undefined) {
  return normalizeOptionalText(value)?.toLowerCase() ?? null;
}

const EMPTY_POLICY = (channel: string): WpfReleasePolicyRequest => ({
  channel,
  targetVersion: "",
  minimumSupportedVersion: "",
  forceUpdate: false,
  isRollback: false,
  targetScope: "all",
  targetStoreGuids: [],
  targetDeviceRegistrationIds: [],
});

/** 把服务器策略摘要还原成编辑表单；无策略时给空表单，允许管理员创建首条策略。 */
function policyFormFromSummary(
  channel: string,
  summary: WpfPolicySummary | null,
): WpfReleasePolicyRequest {
  if (!summary) return EMPTY_POLICY(channel);
  return {
    channel: summary.channel,
    targetVersion: summary.targetVersion,
    minimumSupportedVersion: summary.minimumSupportedVersion,
    forceUpdate: summary.forceUpdate,
    targetScope: summary.targetScope,
    targetStoreGuids: summary.targetStoreGuids,
    targetDeviceRegistrationIds: summary.targetDeviceRegistrationIds,
    isRollback: false,
    rollbackConfirmed: undefined,
  };
}

export interface WpfReleaseViewProps {
  /** 合并页的页头（标题、终端切换、生产 / 预览页签），由版本发布中心传入。 */
  header: ReactNode;
  /** 当前通道由页头页签控制。 */
  channel: WpfChannel;
  /** 保存中通知父级锁住终端与通道切换，避免写到一半切走。 */
  onBusyChange?: (busy: boolean) => void;
  /** 策略或版本状态写入后通知父级刷新投放总览缓存。 */
  onChanged?: () => void;
}

/**
 * 版本发布中心的「WPF 收银端」视图（原 WPF 版本管理页）：
 * 当前生效策略卡 + 「调整策略」弹层（编辑 → 确认两步）+ 已登记版本列表。
 * 读取、保存后回读核验、回滚判断的逻辑沿用原页面。
 */
export function WpfReleaseView({
  header,
  channel,
  onBusyChange,
  onChanged,
}: WpfReleaseViewProps) {
  const { t } = useAppTranslation(["wpfVersions", "common"]);
  const isAdmin = useAuthStore((state) => state.access.isAdmin);
  const [query, setQuery] = useState<WpfReleaseQuery>({
    channel,
    includeDisabled: false,
    page: 1,
    pageSize: PAGE_SIZE,
  });
  const [releases, setReleases] = useState<WpfRelease[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [policyReady, setPolicyReady] = useState(false);
  const [snapshot, setSnapshot] = useState<PolicySnapshot | null>(null);
  const [activeVersions, setActiveVersions] = useState<string[]>([]);
  const [detail, setDetail] = useState<WpfRelease | null>(null);
  const [editRelease, setEditRelease] = useState<WpfRelease | null>(null);
  const [editError, setEditError] = useState("");
  const [editDraft, setEditDraft] = useState<{
    downloadUrl: string;
    sha256: string;
    installerType: "exe" | "msi" | null;
    installerArguments: string;
    releaseNotes: string;
  }>({
    downloadUrl: "",
    sha256: "",
    installerType: null,
    installerArguments: "",
    releaseNotes: "",
  });
  const [qrRelease, setQrRelease] = useState<WpfRelease | null>(null);
  const [menuReleaseId, setMenuReleaseId] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const [updatingId, setUpdatingId] = useState("");
  const [policySaving, setPolicySaving] = useState(false);
  const [policySheetOpen, setPolicySheetOpen] = useState(false);
  const [policyStep, setPolicyStep] = useState<"edit" | "confirm">("edit");
  const [policySheetError, setPolicySheetError] = useState("");
  const [policy, setPolicy] = useState<WpfReleasePolicyRequest>(
    EMPTY_POLICY(channel),
  );
  const [stores, setStores] = useState<WpfStoreOption[]>([]);
  const [devices, setDevices] = useState<WpfDeviceOption[]>([]);
  const [deviceKeywordDraft, setDeviceKeywordDraft] = useState("");
  const [deviceKeywordApplied, setDeviceKeywordApplied] = useState("");
  const [devicePage, setDevicePage] = useState(1);
  const [deviceTotal, setDeviceTotal] = useState(0);
  const [targetLoading, setTargetLoading] = useState(false);
  const [targetError, setTargetError] = useState("");
  const releasesGuard = useRef(createLatestRequestGuard());
  const policyGuard = useRef(createLatestRequestGuard());
  const targetGuard = useRef(createLatestRequestGuard());
  const storesLoadedRef = useRef(false);
  const devicesLoadedRef = useRef(false);
  const deviceKeywordAppliedRef = useRef("");

  // 通道由页头页签驱动：切换时回到第一页。
  useEffect(() => {
    setQuery((current) =>
      current.channel === channel ? current : { ...current, channel, page: 1 },
    );
  }, [channel]);

  const loadReleases = useCallback(
    async (nextQuery: WpfReleaseQuery = query) => {
      const requestId = releasesGuard.current.next();
      setLoading(true);
      setLoadError("");
      try {
        const result = await getWpfReleases(nextQuery);
        if (!releasesGuard.current.isCurrent(requestId)) return null;
        setReleases(result.items);
        setTotal(result.total);
        return result;
      } catch (error) {
        if (releasesGuard.current.isCurrent(requestId)) {
          setReleases([]);
          setTotal(0);
          setLoadError(getErrorMessage(error, t("loadFailed")));
        }
        return null;
      } finally {
        if (releasesGuard.current.isCurrent(requestId)) setLoading(false);
      }
    },
    [query, t],
  );

  const loadPolicySnapshot = useCallback(
    async (snapshotChannel: string) => {
      const requestId = policyGuard.current.next();
      setPolicyReady(false);
      try {
        // 策略 lane 独立读取第一页全量视图，避免分页列表或编辑草稿覆盖当前服务器策略。
        const firstPage = await getWpfReleases({
          channel: snapshotChannel,
          includeDisabled: true,
          page: 1,
          pageSize: 100,
        });
        if (!policyGuard.current.isCurrent(requestId))
          return { ok: false as const, summary: null };
        const allReleases = [...firstPage.items];
        const pageCount = Math.max(
          1,
          Math.ceil(firstPage.total / Math.max(1, firstPage.pageSize)),
        );
        for (let page = 2; page <= pageCount; page += 1) {
          const nextPage = await getWpfReleases({
            channel: snapshotChannel,
            includeDisabled: true,
            page,
            pageSize: firstPage.pageSize,
          });
          if (!policyGuard.current.isCurrent(requestId))
            return { ok: false as const, summary: null };
          allReleases.push(...nextPage.items);
        }
        const summary = getPolicySummary(allReleases);
        const availableVersions = [
          ...new Set(
            allReleases
              .filter((item) => item.isActive)
              .map((item) => normalizeVersion(item.version))
              .filter(Boolean),
          ),
        ].sort();
        setActiveVersions(availableVersions);
        setSnapshot({ summary, releases: allReleases });
        // 成功读取但尚无策略时允许管理员创建首条策略；保存后的核验仍要求服务端返回完整摘要。
        setPolicyReady(true);
        return { ok: true as const, summary };
      } catch (error) {
        if (policyGuard.current.isCurrent(requestId)) {
          setPolicyReady(false);
          setSnackbar(getErrorMessage(error, t("policyLoadFailed")));
        }
        return { ok: false as const, summary: null };
      }
    },
    [t],
  );

  useEffect(() => {
    void loadReleases(query);
  }, [loadReleases, query]);

  useEffect(() => {
    setReleases([]);
    setTotal(0);
    setLoadError("");
  }, [query.channel, query.includeDisabled, query.page]);

  useEffect(() => {
    setPolicyReady(false);
    setSnapshot(null);
    setActiveVersions([]);
    setPolicySheetOpen(false);
    setPolicy(EMPTY_POLICY(query.channel));
    void loadPolicySnapshot(query.channel);
  }, [loadPolicySnapshot, query.channel]);

  useEffect(
    () => () => {
      releasesGuard.current.invalidate();
      policyGuard.current.invalidate();
      targetGuard.current.invalidate();
    },
    [],
  );

  const loadStores = useCallback(async () => {
    const requestId = targetGuard.current.next();
    setTargetLoading(true);
    setTargetError("");
    try {
      const result = await getWpfTargetStores();
      if (targetGuard.current.isCurrent(requestId)) {
        setStores(result);
        storesLoadedRef.current = true;
      }
    } catch (error) {
      if (targetGuard.current.isCurrent(requestId))
        setTargetError(getErrorMessage(error, t("targetLoadFailed")));
    } finally {
      if (targetGuard.current.isCurrent(requestId)) setTargetLoading(false);
    }
  }, [t]);

  const loadDevices = useCallback(
    async (
      keyword = deviceKeywordAppliedRef.current,
      nextPage = 1,
      append = false,
    ) => {
      const requestId = targetGuard.current.next();
      setTargetLoading(true);
      setTargetError("");
      try {
        const result = await getWpfTargetDevices({
          keyword,
          page: nextPage,
          pageSize: DEVICE_PAGE_SIZE,
        });
        if (!targetGuard.current.isCurrent(requestId)) return;
        setDevices((current) => {
          const merged = append ? [...current, ...result.items] : result.items;
          return [
            ...new Map(
              merged.map((item) => [item.deviceRegistrationId, item]),
            ).values(),
          ];
        });
        setDevicePage(result.page);
        setDeviceTotal(result.total);
        const appliedKeyword = keyword.trim();
        setDeviceKeywordApplied(appliedKeyword);
        deviceKeywordAppliedRef.current = appliedKeyword;
        devicesLoadedRef.current = true;
      } catch (error) {
        if (targetGuard.current.isCurrent(requestId))
          setTargetError(getErrorMessage(error, t("targetLoadFailed")));
      } finally {
        if (targetGuard.current.isCurrent(requestId)) setTargetLoading(false);
      }
    },
    [t],
  );

  // 只在策略弹层打开时按范围懒加载门店 / 设备选项。
  useEffect(() => {
    if (!policySheetOpen) return;
    targetGuard.current.invalidate();
    setTargetError("");
    if (policy.targetScope === "stores" && !storesLoadedRef.current)
      void loadStores();
    if (policy.targetScope === "devices" && !devicesLoadedRef.current)
      void loadDevices("", 1, false);
  }, [loadDevices, loadStores, policy.targetScope, policySheetOpen]);

  const summary = snapshot?.summary ?? null;
  const currentTargetVersion = summary?.targetVersion ?? null;
  const policyError = getPolicyValidationError({ ...policy, activeVersions });
  const mutationBusy = policySaving || Boolean(updatingId);
  const pages = Math.max(1, Math.ceil(total / query.pageSize));

  useEffect(() => {
    onBusyChange?.(mutationBusy);
  }, [mutationBusy, onBusyChange]);
  // 离开 WPF 终端时解除父级的切换锁，避免卸载瞬间残留「保存中」。
  useEffect(() => () => onBusyChange?.(false), [onBusyChange]);

  const updateQuery = (patch: Partial<WpfReleaseQuery>) =>
    setQuery((current) => ({
      ...current,
      ...patch,
      ...(patch.channel || patch.includeDisabled !== undefined
        ? { page: 1 }
        : {}),
    }));

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([
        loadReleases(query),
        loadPolicySnapshot(query.channel),
      ]);
    } finally {
      setRefreshing(false);
    }
  };

  const readReleaseById = useCallback(
    async (releaseId: string, releaseChannel: string) => {
      let page = 1;
      const pageSize = 100;
      while (true) {
        const result = await getWpfReleases({
          channel: releaseChannel,
          includeDisabled: true,
          page,
          pageSize,
        });
        const match = result.items.find((item) => item.id === releaseId);
        if (match) return match;
        const effectivePageSize = Math.max(1, result.pageSize || pageSize);
        if (
          result.items.length === 0 ||
          page * effectivePageSize >= result.total
        )
          break;
        page += 1;
      }
      return null;
    },
    [],
  );

  const openUrl = useCallback(
    async (url: string | null) => {
      if (!url) {
        setSnackbar(t("noDownloadUrl"));
        return;
      }
      try {
        if (!isSafeDownloadUrl(url)) throw new Error("URL_NOT_SUPPORTED");
        if (!(await Linking.canOpenURL(url)))
          throw new Error("URL_NOT_SUPPORTED");
        await Linking.openURL(url);
      } catch (error) {
        setSnackbar(getErrorMessage(error, t("openFailed")));
      }
    },
    [t],
  );

  const copyUrl = useCallback(
    async (url: string | null) => {
      if (!isSafeDownloadUrl(url)) {
        setSnackbar(t("noDownloadUrl"));
        return;
      }
      try {
        await Clipboard.setStringAsync(url);
        setSnackbar(t("copySuccess"));
      } catch (error) {
        setSnackbar(getErrorMessage(error, t("copyFailed")));
      }
    },
    [t],
  );

  const toggleRelease = async (release: WpfRelease) => {
    setUpdatingId(release.id);
    try {
      await updateWpfRelease(release.id, { isActive: !release.isActive });
      // 状态写入完成后独立重新读取，避免把本地乐观状态当作服务器事实。
      let readBack: WpfRelease | null = null;
      let readBackFailed = false;
      try {
        readBack = await readReleaseById(release.id, query.channel);
      } catch {
        readBackFailed = true;
      }
      const refreshed = await loadReleases(query);
      let policyReadBack: { ok: boolean; summary: WpfPolicySummary | null } = {
        ok: false,
        summary: null,
      };
      try {
        policyReadBack = await loadPolicySnapshot(query.channel);
      } catch {
        readBackFailed = true;
      }
      onChanged?.();
      if (
        readBackFailed ||
        readBack === null ||
        refreshed === null ||
        !policyReadBack.ok ||
        readBack.isActive !== !release.isActive
      ) {
        setSnackbar(t("savedButReadFailed"));
      } else {
        setSnackbar(
          release.isActive ? t("disabledSuccess") : t("enabledSuccess"),
        );
      }
    } catch (error) {
      setSnackbar(getErrorMessage(error, t("saveFailed")));
    } finally {
      setUpdatingId("");
    }
  };

  const openEditor = (release: WpfRelease) => {
    setEditError("");
    setEditRelease(release);
    setEditDraft({
      downloadUrl: release.downloadUrl ?? "",
      sha256: release.sha256 ?? "",
      installerType: release.installerType,
      installerArguments: release.installerArguments ?? "",
      releaseNotes: release.releaseNotes ?? "",
    });
  };

  const saveReleaseMetadata = async () => {
    if (!editRelease) return;
    // 弹层是原生 Modal，页面 Snackbar 会被压住：校验与失败信息显示在弹层内。
    if (
      editDraft.downloadUrl.trim() &&
      !isSafeDownloadUrl(editDraft.downloadUrl)
    ) {
      setEditError(t("invalidUrl"));
      return;
    }
    if (!isValidSha256(editDraft.sha256)) {
      setEditError(t("invalidSha256"));
      return;
    }
    setEditError("");
    setUpdatingId(editRelease.id);
    try {
      await updateWpfRelease(editRelease.id, editDraft);
      setEditRelease(null);
      let readBack: WpfRelease | null = null;
      let readBackFailed = false;
      try {
        readBack = await readReleaseById(editRelease.id, query.channel);
      } catch {
        readBackFailed = true;
      }
      const refreshed = await loadReleases(query);
      const metadataMatches =
        readBack !== null &&
        normalizeOptionalText(readBack.downloadUrl) ===
          normalizeOptionalText(editDraft.downloadUrl) &&
        normalizeSha256(readBack.sha256) ===
          normalizeSha256(editDraft.sha256) &&
        readBack.installerType === editDraft.installerType &&
        normalizeOptionalText(readBack.installerArguments) ===
          normalizeOptionalText(editDraft.installerArguments) &&
        normalizeOptionalText(readBack.releaseNotes) ===
          normalizeOptionalText(editDraft.releaseNotes);
      setSnackbar(
        !readBackFailed && refreshed !== null && metadataMatches
          ? t("metadataSaved")
          : t("savedButReadFailed"),
      );
    } catch (error) {
      setEditError(getErrorMessage(error, t("saveFailed")));
    } finally {
      setUpdatingId("");
    }
  };

  // 每次打开都从服务器策略重新填表，避免上次没保存的草稿冒充当前策略。
  const openPolicySheet = () => {
    if (!policyReady) return;
    setPolicy(policyFormFromSummary(query.channel, summary));
    setPolicyStep("edit");
    setPolicySheetError("");
    setPolicySheetOpen(true);
  };

  const closePolicySheet = () => {
    if (policySaving) return;
    setPolicySheetOpen(false);
  };

  const performSavePolicy = async (rollbackConfirmed: boolean) => {
    if (!policyReady || !canSavePolicy({ ...policy, activeVersions })) return;
    setPolicySaving(true);
    setPolicySheetError("");
    const targetVersion = normalizeVersion(policy.targetVersion);
    const payload: WpfReleasePolicyRequest = {
      ...policy,
      channel: query.channel,
      targetVersion,
      minimumSupportedVersion: normalizeVersion(policy.minimumSupportedVersion),
      isRollback: inferRollback(targetVersion, currentTargetVersion),
      rollbackConfirmed: rollbackConfirmed || undefined,
    };
    try {
      await saveWpfPolicy(payload);
    } catch (error) {
      // 写入失败：留在弹层确认步，错误显示在弹层内。
      setPolicySheetError(getErrorMessage(error, t("saveFailed")));
      setPolicySaving(false);
      return;
    }
    // 写入已成功：关闭弹层，再独立重新请求版本与策略摘要核验服务端最终状态。
    setPolicySheetOpen(false);
    try {
      const verifiedList = await loadReleases(query);
      const verifiedPolicy = await loadPolicySnapshot(query.channel);
      setSnackbar(
        verifiedList !== null &&
          verifiedPolicy.ok &&
          verifiedPolicy.summary &&
          policySummaryMatchesRequest(payload, verifiedPolicy.summary)
          ? t("policyVerified")
          : t("savedButReadFailed"),
      );
    } catch {
      setSnackbar(t("savedButReadFailed"));
    } finally {
      onChanged?.();
      setPolicySaving(false);
    }
  };

  const policyIsRollback = inferRollback(
    policy.targetVersion,
    currentTargetVersion,
  );
  const storeLabel = (storeGuid: string) => {
    const store =
      stores.find((item) => item.storeGuid === storeGuid) ??
      summary?.targetStoreSummaries.find((item) => item.storeGuid === storeGuid);
    return store
      ? [store.storeCode, store.storeName].filter(Boolean).join(" · ") ||
          store.storeGuid
      : storeGuid;
  };
  const selectedDeviceLabels = policy.targetDeviceRegistrationIds.map(
    (deviceId) => {
      const device =
        devices.find((item) => item.deviceRegistrationId === deviceId) ??
        summary?.targetDeviceSummaries.find(
          (item) => item.deviceRegistrationId === deviceId,
        );
      return device ? deviceLabel(device) : `#${deviceId}`;
    },
  );

  const scopeText = (target: WpfPolicySummary) =>
    target.targetScope === "stores"
      ? t("scopeStores", { count: target.targetStoreGuids.length })
      : target.targetScope === "devices"
        ? t("scopeDevices", { count: target.targetDeviceRegistrationIds.length })
        : t("scopeAll");
  const modeText = (mode: WpfPolicyMode) =>
    mode === "required"
      ? t("modeRequired")
      : mode === "minimum"
        ? t("modeMinimum")
        : t("modeOptional");
  const ladderRange = (segment: WpfDecisionSegment) => {
    switch (segment.kind) {
      case "force":
      case "optional":
        return segment.variant === "between"
          ? t("ladder.between", { bound: segment.bound })
          : t("ladder.below", { bound: segment.bound });
      case "latest":
        return t("ladder.equal", { bound: segment.bound });
      case "rollback":
        return t("ladder.above", { bound: segment.bound });
    }
  };

  if (!isAdmin) {
    return (
      <ScreenFrame header={header} refreshing={false} onRefresh={() => {}}>
        <Panel>
          <View style={ui.cardBody}>
            <Text style={ui.muted}>{t("adminOnly")}</Text>
          </View>
        </Panel>
      </ScreenFrame>
    );
  }

  const modeInfo = summary
    ? getWpfPolicyMode(snapshot?.releases ?? [], summary)
    : null;
  const newerVersions = summary
    ? getWpfNewerActiveVersions(snapshot?.releases ?? [], summary.targetVersion)
    : [];
  const scopedLabels = summary
    ? summary.targetScope === "stores"
      ? summary.targetStoreSummaries.map(
          (store) => store.storeCode || store.storeName || store.storeGuid,
        )
      : summary.targetDeviceSummaries.map(
          (device) =>
            device.systemDeviceNumber || `#${device.deviceRegistrationId}`,
        )
    : [];

  const policyCard = (
    <Panel>
      <View style={styles.policyBody}>
        <View style={ui.headRow}>
          <Text accessibilityRole="header" style={[ui.cardTitle, ui.flexText]}>
            {t("policyCardTitle")}
          </Text>
          {policyReady ? (
            <Pill
              label={summary ? t("statusActive") : t("statusInactive")}
              tone={summary ? "success" : "neutral"}
            />
          ) : null}
        </View>
        {!policyReady ? (
          <ActivityIndicator color={HB_COLORS.action} style={styles.loader} />
        ) : summary && modeInfo ? (
          <>
            <View style={styles.factGrid}>
              <View style={styles.fact}>
                <Text style={ui.caption}>{t("targetVersion")}</Text>
                <Text style={styles.factValue}>{summary.targetVersion}</Text>
              </View>
              <View style={styles.fact}>
                <Text style={ui.caption}>{t("minimumVersion")}</Text>
                <Text style={styles.factValue}>
                  {summary.minimumSupportedVersion}
                </Text>
              </View>
              <View style={styles.fact}>
                <Text style={ui.caption}>{t("updateMode")}</Text>
                <View style={styles.factPill}>
                  <Pill
                    label={modeText(modeInfo.mode)}
                    tone={
                      modeInfo.mode === "required"
                        ? "danger"
                        : modeInfo.mode === "minimum"
                          ? "warning"
                          : "neutral"
                    }
                  />
                </View>
              </View>
              <View style={styles.fact}>
                <Text style={ui.caption}>{t("scopeLabel")}</Text>
                <Text style={styles.factValueSmall}>{scopeText(summary)}</Text>
              </View>
              <View style={[styles.fact, styles.factWide]}>
                <Text style={ui.caption}>{t("lastUpdated")}</Text>
                <Text style={ui.body}>
                  {[
                    formatDateTime(summary.policyUpdatedAt),
                    summary.policyUpdatedBy,
                  ]
                    .filter(Boolean)
                    .join(" · ")}
                </Text>
              </View>
            </View>
            {summary.targetScope !== "all" ? (
              <View style={styles.notice}>
                <Text style={styles.noticeText}>
                  {t("partialRollout", {
                    scope: scopedLabels.length
                      ? scopedLabels.join("、")
                      : scopeText(summary),
                    target: summary.targetVersion,
                  })}
                </Text>
              </View>
            ) : null}
            {newerVersions.length ? (
              <View style={styles.notice}>
                <Text style={styles.noticeText}>
                  {t("pendingRelease", {
                    count: newerVersions.length,
                    latest: newerVersions[0],
                  })}
                </Text>
              </View>
            ) : null}
            <View style={styles.ladder}>
              <Text style={ui.caption}>
                {t("ladderTitle")} · {t("ladderHint")}
              </Text>
              <View style={styles.ladderGrid}>
                {buildWpfDecisionLadder(
                  modeInfo.mode,
                  summary.targetVersion,
                  modeInfo.minimum,
                ).map((segment) => (
                  <View
                    key={`${segment.kind}-${segment.bound}`}
                    style={[styles.ladderCell, ladderTone[segment.kind].box]}
                  >
                    <Text
                      style={[styles.ladderKind, ladderTone[segment.kind].text]}
                    >
                      {t(`ladder.${segment.kind}`)}
                    </Text>
                    <Text style={styles.ladderRange}>{ladderRange(segment)}</Text>
                  </View>
                ))}
              </View>
            </View>
          </>
        ) : (
          <Text style={ui.muted}>{t("noPolicy")}</Text>
        )}
        <SecondaryButton
          label={summary ? t("adjustPolicy") : t("createPolicy")}
          icon="tune-variant"
          onPress={openPolicySheet}
          disabled={!policyReady || mutationBusy}
        />
      </View>
    </Panel>
  );

  const releaseList = (
    <View style={styles.section}>
      <SectionHeader
        title={`${t("releaseList")} · ${t("count", { count: total })}`}
        action={
          <View style={styles.inlineSwitch}>
            <Text style={ui.muted}>{t("showDisabled")}</Text>
            <Switch
              value={query.includeDisabled}
              onValueChange={(value) => updateQuery({ includeDisabled: value })}
              disabled={mutationBusy}
              accessibilityLabel={t("includeDisabled")}
            />
          </View>
        }
      />
      {loading && releases.length === 0 ? (
        <ActivityIndicator color={HB_COLORS.action} style={styles.loader} />
      ) : null}
      {loadError ? (
        <Panel>
          <View style={ui.cardBody}>
            <Text style={ui.error}>{loadError}</Text>
            <TextLink
              icon="refresh"
              label={t("refresh")}
              onPress={() => void loadReleases(query)}
            />
          </View>
        </Panel>
      ) : null}
      {!loading && releases.length === 0 && !loadError ? (
        <Text style={ui.muted}>{t("empty")}</Text>
      ) : null}
      {releases.map((release) => {
        const canUseUrl = isSafeDownloadUrl(release.downloadUrl);
        return (
          <Panel
            key={
              release.id ||
              `${release.channel}-${release.version}-${release.fileName}`
            }
            style={release.isCurrent ? styles.currentCard : undefined}
          >
            <View style={styles.releaseBody}>
              <View style={styles.releaseTitleRow}>
                <Text style={styles.releaseVersion}>{release.version}</Text>
                {release.isCurrent ? (
                  <Pill label={t("current")} tone="success" />
                ) : null}
                {!release.isActive ? (
                  <Pill label={t("disabledBadge")} tone="neutral" />
                ) : null}
                {release.forceUpdate ? (
                  <Pill label={t("forced")} tone="danger" />
                ) : null}
                {release.isRollback ? (
                  <Pill label={t("rollbackBadge")} tone="warning" />
                ) : null}
              </View>
              <Text style={ui.muted} numberOfLines={1}>
                {release.fileName || "-"} · {formatFileSize(release.fileSize)} ·{" "}
                {release.installerType?.toUpperCase() || "-"}
              </Text>
              <Text style={ui.caption}>
                {t("registeredAt", {
                  time: formatDateTime(release.updatedAt || release.createdAt),
                })}{" "}
                · SHA {maskSha256(release.sha256)}
              </Text>
              <View style={styles.releaseActions}>
                <TextLink
                  icon="qrcode"
                  label={t("qrCode")}
                  onPress={() => setQrRelease(release)}
                  disabled={!canUseUrl || mutationBusy}
                />
                <TextLink
                  icon="content-copy"
                  label={t("copyLink")}
                  onPress={() => void copyUrl(release.downloadUrl)}
                  disabled={!canUseUrl || mutationBusy}
                />
                <View style={ui.flexText} />
                {/* 次要操作收进 Paper 菜单（页面级 Portal），不叠加原生弹层 */}
                <Menu
                  visible={menuReleaseId === release.id}
                  onDismiss={() => setMenuReleaseId("")}
                  anchor={
                    <IconButton
                      icon="dots-horizontal"
                      accessibilityLabel={t("moreActions")}
                      onPress={() => setMenuReleaseId(release.id)}
                      disabled={mutationBusy || updatingId === release.id}
                      style={styles.moreButton}
                    />
                  }
                >
                  <Menu.Item
                    leadingIcon="open-in-new"
                    title={t("openDownload")}
                    disabled={!canUseUrl}
                    onPress={() => {
                      setMenuReleaseId("");
                      void openUrl(release.downloadUrl);
                    }}
                  />
                  <Menu.Item
                    leadingIcon="information-outline"
                    title={t("details")}
                    onPress={() => {
                      setMenuReleaseId("");
                      setDetail(release);
                    }}
                  />
                  <Menu.Item
                    leadingIcon="pencil"
                    title={t("editMetadata")}
                    onPress={() => {
                      setMenuReleaseId("");
                      openEditor(release);
                    }}
                  />
                  <Menu.Item
                    leadingIcon={
                      release.isActive ? "eye-off-outline" : "eye-outline"
                    }
                    title={release.isActive ? t("disable") : t("enable")}
                    onPress={() => {
                      setMenuReleaseId("");
                      void toggleRelease(release);
                    }}
                  />
                </Menu>
              </View>
            </View>
          </Panel>
        );
      })}
      {total > query.pageSize ? (
        <View style={styles.pagination}>
          <Button
            mode="outlined"
            onPress={() => updateQuery({ page: Math.max(1, query.page - 1) })}
            disabled={query.page <= 1 || loading}
          >
            {t("previous")}
          </Button>
          <Text style={ui.muted}>
            {t("page", { page: query.page, total: pages })}
          </Text>
          <Button
            mode="outlined"
            onPress={() =>
              updateQuery({ page: Math.min(pages, query.page + 1) })
            }
            disabled={query.page >= pages || loading}
          >
            {t("next")}
          </Button>
        </View>
      ) : null}
    </View>
  );

  // 按版本号从新到旧（字符串排序会把 1.0.100 排在 1.0.99 前面）。
  const quickVersions = [...activeVersions]
    .sort((left, right) => compareVersions(right, left) ?? 0)
    .slice(0, QUICK_VERSION_LIMIT);
  const scopeConfirmLine =
    policy.targetScope === "stores"
      ? policy.targetStoreGuids.map(storeLabel).join("、")
      : policy.targetScope === "devices"
        ? selectedDeviceLabels.join("、")
        : "";

  const policyEditor = (
    <View style={styles.sheetBody}>
      <TextInput
        label={t("targetVersion")}
        value={policy.targetVersion}
        onChangeText={(value) =>
          setPolicy((current) => ({ ...current, targetVersion: value }))
        }
        mode="outlined"
        autoCapitalize="none"
      />
      {quickVersions.length ? (
        <View style={styles.quickVersions}>
          <Text style={ui.caption}>{t("activeVersionsHint")}</Text>
          <View style={styles.chipRow}>
            {quickVersions.map((version) => {
              const selected =
                normalizeVersion(policy.targetVersion) === version;
              return (
                <Pressable
                  key={version}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  onPress={() =>
                    setPolicy((current) => ({
                      ...current,
                      targetVersion: version,
                    }))
                  }
                  style={[styles.chip, selected && styles.chipSelected]}
                >
                  <Text
                    style={[styles.chipLabel, selected && styles.chipLabelSelected]}
                  >
                    {version}
                  </Text>
                </Pressable>
              );
            })}
          </View>
        </View>
      ) : null}
      <TextInput
        label={t("minimumVersion")}
        value={policy.minimumSupportedVersion}
        onChangeText={(value) =>
          setPolicy((current) => ({
            ...current,
            minimumSupportedVersion: value,
          }))
        }
        mode="outlined"
        autoCapitalize="none"
      />
      {policyError ? (
        <HelperText type="error">{t(`validation.${policyError}`)}</HelperText>
      ) : null}
      <View style={styles.switchRow}>
        <Text style={ui.body}>{t("forceUpdate")}</Text>
        <Switch
          value={policy.forceUpdate}
          onValueChange={(value) =>
            setPolicy((current) => ({ ...current, forceUpdate: value }))
          }
        />
      </View>
      <Text style={styles.fieldLabel}>{t("targetScope")}</Text>
      <SegmentedButtons
        value={normalizeTargetScope(policy.targetScope)}
        onValueChange={(value) =>
          setPolicy((current) => ({
            ...current,
            targetScope: normalizeTargetScope(value),
            targetStoreGuids: [],
            targetDeviceRegistrationIds: [],
          }))
        }
        buttons={[
          { value: "all", label: t("allTargets"), disabled: policySaving },
          { value: "stores", label: t("stores"), disabled: policySaving },
          { value: "devices", label: t("devices"), disabled: policySaving },
        ]}
      />
      {targetError ? <HelperText type="error">{targetError}</HelperText> : null}
      {policy.targetScope === "stores" ? (
        <View style={styles.targetList}>
          {targetLoading && stores.length === 0 ? (
            <ActivityIndicator />
          ) : (
            stores.map((store) => (
              <Checkbox.Item
                key={store.storeGuid}
                label={
                  [store.storeCode, store.storeName]
                    .filter(Boolean)
                    .join(" · ") || store.storeGuid
                }
                status={
                  policy.targetStoreGuids.includes(store.storeGuid)
                    ? "checked"
                    : "unchecked"
                }
                onPress={() =>
                  setPolicy((current) => ({
                    ...current,
                    targetStoreGuids: current.targetStoreGuids.includes(
                      store.storeGuid,
                    )
                      ? current.targetStoreGuids.filter(
                          (value) => value !== store.storeGuid,
                        )
                      : [...current.targetStoreGuids, store.storeGuid],
                  }))
                }
              />
            ))
          )}
        </View>
      ) : null}
      {policy.targetScope === "devices" ? (
        <View style={styles.targetList}>
          <Searchbar
            value={deviceKeywordDraft}
            onChangeText={setDeviceKeywordDraft}
            onSubmitEditing={() =>
              !targetLoading && void loadDevices(deviceKeywordDraft, 1, false)
            }
            placeholder={t("searchDevices")}
          />
          <View style={styles.searchActions}>
            <Button
              mode="text"
              onPress={() => void loadDevices(deviceKeywordDraft, 1, false)}
              disabled={targetLoading}
            >
              {t("search")}
            </Button>
            {deviceTotal > devices.length ? (
              <Button
                mode="text"
                onPress={() =>
                  void loadDevices(deviceKeywordApplied, devicePage + 1, true)
                }
                loading={targetLoading}
                disabled={targetLoading}
              >
                {t("loadMore")}
              </Button>
            ) : null}
          </View>
          {devices.map((device) => (
            <Checkbox.Item
              key={device.deviceRegistrationId}
              label={deviceLabel(device)}
              status={
                policy.targetDeviceRegistrationIds.includes(
                  device.deviceRegistrationId,
                )
                  ? "checked"
                  : "unchecked"
              }
              onPress={() =>
                setPolicy((current) => ({
                  ...current,
                  targetDeviceRegistrationIds:
                    current.targetDeviceRegistrationIds.includes(
                      device.deviceRegistrationId,
                    )
                      ? current.targetDeviceRegistrationIds.filter(
                          (value) => value !== device.deviceRegistrationId,
                        )
                      : [
                          ...current.targetDeviceRegistrationIds,
                          device.deviceRegistrationId,
                        ],
                }))
              }
            />
          ))}
        </View>
      ) : null}
    </View>
  );

  const policyConfirm = (
    <View style={styles.sheetBody}>
      <Text style={ui.body}>
        {policyIsRollback ? t("rollbackMessage") : t("confirmPolicyMessage")}
      </Text>
      <ConfirmLines
        lines={[
          `${t("targetVersion")}: ${normalizeVersion(policy.targetVersion)}`,
          `${t("minimumVersion")}: ${normalizeVersion(policy.minimumSupportedVersion)}`,
          `${t("forceUpdate")}: ${policy.forceUpdate ? t("yes") : t("no")}`,
          `${t("targetScope")}: ${t(
            policy.targetScope === "stores"
              ? "stores"
              : policy.targetScope === "devices"
                ? "devices"
                : "allTargets",
          )}`,
          ...(scopeConfirmLine ? [scopeConfirmLine] : []),
        ]}
      />
    </View>
  );

  return (
    <View style={styles.root}>
      <ScreenFrame
        header={header}
        refreshing={refreshing}
        onRefresh={() => void refresh()}
      >
        {policyCard}
        {releaseList}
      </ScreenFrame>

      <BusinessSheet
        visible={policySheetOpen}
        title={
          policyStep === "confirm"
            ? policyIsRollback
              ? t("rollbackTitle")
              : t("confirmPolicyTitle")
            : t("adjustPolicy")
        }
        subtitle={t("policySheetSubtitle", {
          channel:
            query.channel === "preview" ? t("preview") : t("production"),
        })}
        dismissable={!policySaving}
        onDismiss={closePolicySheet}
        footer={
          <View style={styles.sheetFooter}>
            {policySheetError ? (
              <Text accessibilityRole="alert" style={ui.error}>
                {policySheetError}
              </Text>
            ) : null}
            {policyStep === "edit" ? (
              <PrimaryButton
                label={t("savePolicy")}
                icon="content-save"
                onPress={() => {
                  setPolicySheetError("");
                  setPolicyStep("confirm");
                }}
                disabled={!policyReady || Boolean(policyError) || loading}
              />
            ) : (
              <View style={styles.confirmButtons}>
                <SecondaryButton
                  label={t("backToEdit")}
                  onPress={() => setPolicyStep("edit")}
                  disabled={policySaving}
                  style={ui.flexText}
                />
                <PrimaryButton
                  label={
                    policyIsRollback ? t("confirmRollback") : t("confirmSave")
                  }
                  onPress={() => void performSavePolicy(policyIsRollback)}
                  loading={policySaving}
                  style={ui.flexText}
                />
              </View>
            )}
          </View>
        }
      >
        {policyStep === "edit" ? policyEditor : policyConfirm}
      </BusinessSheet>

      <Portal>
        <Modal
          visible={Boolean(detail)}
          style={styles.sheetOverlay}
          onDismiss={() => setDetail(null)}
          contentContainerStyle={styles.modal}
        >
          <View style={styles.modalHeader}>
            <Text variant="titleLarge">{detail?.version || t("details")}</Text>
            <IconButton
              icon="close"
              accessibilityLabel={t("cancel")}
              onPress={() => setDetail(null)}
            />
          </View>
          <Divider />
          {detail ? (
            <ScrollView>
              {(
                [
                  [t("channel"), detail.channel],
                  [t("fileName"), detail.fileName],
                  [t("fileSize"), formatFileSize(detail.fileSize)],
                  [t("installerArguments"), detail.installerArguments || "-"],
                  [t("releaseNotes"), detail.releaseNotes || "-"],
                  [t("downloadUrl"), detail.downloadUrl || "-"],
                  [t("createdAt"), formatDateTime(detail.createdAt)],
                  [t("updatedAt"), formatDateTime(detail.updatedAt)],
                ] as DetailField[]
              ).map(([label, value]) => (
                <View key={label} style={styles.detailRow}>
                  <Text variant="labelMedium" style={styles.detailLabel}>
                    {label}
                  </Text>
                  <Text selectable>{value}</Text>
                </View>
              ))}
            </ScrollView>
          ) : null}
        </Modal>
      </Portal>
      <Portal>
        <Modal
          visible={Boolean(qrRelease)}
          onDismiss={() => setQrRelease(null)}
          contentContainerStyle={styles.qrModal}
        >
          <Text variant="titleLarge">{qrRelease?.version || t("qrCode")}</Text>
          {qrRelease?.downloadUrl ? (
            <QRCode
              value={qrRelease.downloadUrl}
              size={220}
              backgroundColor="white"
              color="black"
            />
          ) : null}
          <Text selectable style={styles.qrUrl}>
            {qrRelease?.downloadUrl || ""}
          </Text>
          <Button
            mode="contained"
            icon="content-copy"
            onPress={() => void copyUrl(qrRelease?.downloadUrl ?? null)}
          >
            {t("copy")}
          </Button>
        </Modal>
      </Portal>
      <BusinessSheet
        visible={Boolean(editRelease)}
        title={t("editMetadata")}
        subtitle={editRelease?.version}
        dismissable={!updatingId}
        onDismiss={() => setEditRelease(null)}
        footer={
          <View style={styles.sheetFooter}>
            {editError ? (
              <Text accessibilityRole="alert" style={ui.error}>
                {editError}
              </Text>
            ) : null}
            <Button
              mode="contained"
              icon="content-save"
              onPress={() => void saveReleaseMetadata()}
              loading={Boolean(updatingId)}
              disabled={Boolean(updatingId)}
              contentStyle={BUSINESS_UI.buttonContent}
            >
              {t("saveMetadata")}
            </Button>
          </View>
        }
      >
        <View style={styles.sheetBody}>
          <TextInput
            label={t("downloadUrl")}
            value={editDraft.downloadUrl}
            onChangeText={(value) =>
              setEditDraft((current) => ({ ...current, downloadUrl: value }))
            }
            mode="outlined"
            autoCapitalize="none"
          />
          <TextInput
            label={t("sha256")}
            value={editDraft.sha256}
            onChangeText={(value) =>
              setEditDraft((current) => ({ ...current, sha256: value }))
            }
            mode="outlined"
            autoCapitalize="none"
          />
          <Text style={styles.fieldLabel}>{t("installerType")}</Text>
          <SegmentedButtons
            value={editDraft.installerType ?? "exe"}
            onValueChange={(value) =>
              setEditDraft((current) => ({
                ...current,
                installerType: value === "msi" ? "msi" : "exe",
              }))
            }
            buttons={[
              { value: "exe", label: "EXE" },
              { value: "msi", label: "MSI" },
            ]}
          />
          <TextInput
            label={t("installerArguments")}
            value={editDraft.installerArguments}
            onChangeText={(value) =>
              setEditDraft((current) => ({
                ...current,
                installerArguments: value,
              }))
            }
            mode="outlined"
          />
          <TextInput
            label={t("releaseNotes")}
            value={editDraft.releaseNotes}
            onChangeText={(value) =>
              setEditDraft((current) => ({ ...current, releaseNotes: value }))
            }
            mode="outlined"
            multiline
          />
        </View>
      </BusinessSheet>
      <Portal>
        <Snackbar
          visible={Boolean(snackbar)}
          onDismiss={() => setSnackbar("")}
          duration={3500}
        >
          {snackbar}
        </Snackbar>
      </Portal>
    </View>
  );
}

// 决策预览配色：强制 = 警示，可选 = 信息，无需更新 = 成功，回退 = 中性（与 Web 版本发布中心一致）。
const ladderTone = {
  force: StyleSheet.create({
    box: { backgroundColor: "#FFFAEB" },
    text: { color: "#93370D" },
  }),
  optional: StyleSheet.create({
    box: { backgroundColor: "#EEF4FF" },
    text: { color: HB_COLORS.action },
  }),
  latest: StyleSheet.create({
    box: { backgroundColor: "#ECFDF3" },
    text: { color: HB_COLORS.success },
  }),
  rollback: StyleSheet.create({
    box: { backgroundColor: HB_COLORS.surfaceMuted },
    text: { color: "#344054" },
  }),
};

const styles = StyleSheet.create({
  root: { flex: 1 },
  section: { gap: HB_SPACING.sm },
  loader: { marginVertical: HB_SPACING.lg },
  policyBody: { padding: HB_SPACING.md, gap: 14 },
  factGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    rowGap: 14,
  },
  fact: { width: "50%", gap: 4, paddingRight: HB_SPACING.xs },
  factWide: { width: "100%" },
  factValue: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  factValueSmall: {
    fontSize: 14,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  factPill: { flexDirection: "row" },
  notice: {
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#FFFAEB",
  },
  noticeText: { fontSize: 13, lineHeight: 20, color: "#93370D" },
  ladder: { gap: 6 },
  ladderGrid: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  ladderCell: {
    flexGrow: 1,
    flexBasis: "45%",
    padding: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    gap: 2,
  },
  ladderKind: { fontSize: 12, lineHeight: 18, fontWeight: "600" },
  ladderRange: { fontSize: 12, lineHeight: 18, color: "#344054" },
  inlineSwitch: { flexDirection: "row", alignItems: "center", gap: 6 },
  currentCard: { borderColor: "#B2CCFF" },
  releaseBody: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 14,
    paddingBottom: 6,
    gap: 6,
  },
  releaseTitleRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  releaseVersion: {
    fontSize: 18,
    lineHeight: 26,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  releaseActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  moreButton: { margin: 0 },
  pagination: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sheetBody: { gap: HB_SPACING.sm },
  sheetFooter: { gap: HB_SPACING.xs },
  confirmButtons: { flexDirection: "row", gap: HB_SPACING.sm },
  fieldLabel: {
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "600",
    color: "#344054",
  },
  switchRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    minHeight: 48,
  },
  quickVersions: { gap: 6 },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  chip: {
    minHeight: 36,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    justifyContent: "center",
    backgroundColor: HB_COLORS.white,
  },
  chipSelected: { borderColor: HB_COLORS.action, backgroundColor: "#EEF4FF" },
  chipLabel: {
    fontSize: 13,
    color: "#344054",
    fontVariant: ["tabular-nums"],
  },
  chipLabelSelected: { color: HB_COLORS.action, fontWeight: "600" },
  targetList: { borderRadius: HB_RADIUS.control, overflow: "hidden" },
  searchActions: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
  },
  modal: {
    alignSelf: "center",
    width: "100%",
    maxWidth: 680,
    padding: 16,
    paddingBottom: 28,
    backgroundColor: "white",
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    maxHeight: "92%",
  },
  sheetOverlay: { justifyContent: "flex-end" },
  modalHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  detailRow: {
    paddingVertical: 12,
    gap: 4,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  detailLabel: { color: HB_COLORS.textSecondary },
  qrModal: {
    margin: 20,
    padding: 24,
    alignItems: "center",
    gap: 16,
    backgroundColor: "white",
    borderRadius: 12,
  },
  qrUrl: { textAlign: "center", opacity: 0.72 },
});
