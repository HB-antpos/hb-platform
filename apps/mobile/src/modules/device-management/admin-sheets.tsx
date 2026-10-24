import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, ScrollView, StyleSheet, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import * as FileSystem from "expo-file-system/legacy";
import * as Sharing from "expo-sharing";
import QRCode from "react-native-qrcode-svg";
import { Button, Chip, HelperText, SegmentedButtons, Switch, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import {
  createEmergencyLoginGrant,
  createMobileActivationCode,
  createPosActivationCode,
  getDeviceRegistrationDetail,
  getEmergencyLoginGrant,
  getMobileActivationGrants,
  getMobileManageableAccounts,
  getMobileManageableStores,
  getPosActivationGrants,
  getPosManageableStores,
  revokeEmergencyLoginGrant,
  revokeMobileActivationCode,
  revokePosActivationCode,
  updateDeviceRegistration,
} from "@/modules/device-management/admin-api";
import {
  isDeviceRegistrationId,
  supportsTransactionControl,
  type ActivationCodeCreateResult,
  type DeviceActivationGrant,
  type DeviceActivationStatus,
  type DeviceActivationSystem,
  type DeviceRegistrationDetail,
  type EmergencyLoginGrant,
  type ManageableAccount,
  type ManageableStore,
  type MobileActivationSystem,
} from "@/modules/device-management/admin-types";
import { getDeviceStatusKey } from "@/modules/device-management/status";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import { RequestGenerationGate, activationCreateGate, emergencyCreateGate } from "@/modules/device-management/request-generation-gate";
import { isResultUnknownCreateError } from "@/modules/device-management/create-outcome";

type Translate = (key: string, options?: Record<string, unknown>) => string;
type ActivationKind = "POS" | "Mobile";

function formatDateTime(value: string | null | undefined, language: string) {
  if (!value) return "–";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(parsed);
}

function KeyValue({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.keyValue}>
      <Text variant="bodySmall" style={BUSINESS_UI.fieldLabel}>{label}</Text>
      <Text selectable variant="bodyMedium" style={BUSINESS_UI.fieldValue}>{value || "–"}</Text>
    </View>
  );
}

function SheetFooter({ children }: { children: React.ReactNode }) {
  return <View style={styles.sheetFooter}>{children}</View>;
}

function getSafeErrorMessage(error: unknown, t: Translate, language: string, fallbackKey: string) {
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey });
}

function reasonIsValid(reason: string) {
  const length = reason.trim().length;
  return length >= 1 && length <= 200;
}

export function DeviceEditSheet({
  visible,
  registrationId,
  language,
  t,
  onDismiss,
  onSaved,
  onMessage,
}: {
  visible: boolean;
  registrationId: number | null;
  language: string;
  t: Translate;
  onDismiss: () => void;
  onSaved: () => Promise<void> | void;
  onMessage: (message: string) => void;
}) {
  const [detail, setDetail] = useState<DeviceRegistrationDetail | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [deviceType, setDeviceType] = useState("POS");
  const [deviceSystem, setDeviceSystem] = useState("Windows");
  const [allowTransactions, setAllowTransactions] = useState(true);
  const [remark, setRemark] = useState("");
  const [feedback, setFeedback] = useState("");

  const clear = useCallback(() => {
    setDetail(null);
    setLoading(false);
    setSaving(false);
    setRemark("");
    setFeedback("");
  }, []);

  const dismiss = useCallback(() => {
    clear();
    onDismiss();
  }, [clear, onDismiss]);

  useEffect(() => {
    if (!visible || !isDeviceRegistrationId(registrationId)) return;
    let active = true;
    setLoading(true);
    void getDeviceRegistrationDetail(registrationId)
      .then((next) => {
        if (!active) return;
        setDetail(next);
        setDeviceType(next.deviceType || "POS");
        setDeviceSystem(next.deviceSystem || "Windows");
        setAllowTransactions(next.allowTransactions);
        setRemark(next.remark ?? "");
      })
      .catch((error) => {
        if (active) {
          onMessage(getSafeErrorMessage(error, t, language, "deviceManagement:messages.detailLoadFailed"));
          dismiss();
        }
      })
      .finally(() => active && setLoading(false));
    return () => { active = false; };
  }, [dismiss, language, onMessage, registrationId, t, visible]);

  const legacyIosPos = detail?.status === 1 && detail.deviceSystem === "iOS" && detail.deviceType === "POS";
  const systemEditable = detail?.status === -1 || legacyIosPos;
  const transactionSupported = supportsTransactionControl(deviceType, deviceSystem);

  const save = useCallback(async () => {
    if (!detail || !isDeviceRegistrationId(detail.id)) return;
    setSaving(true);
    try {
      await updateDeviceRegistration(detail.id, {
        deviceType,
        deviceSystem,
        allowTransactions: transactionSupported ? allowTransactions : detail.allowTransactions,
        remark: remark.trim(),
      });
      // 写后重新读取详情和列表，避免仅凭 PUT 回包认定已生效。
      const readback = await getDeviceRegistrationDetail(detail.id);
      setDetail(readback);
      await onSaved();
      onMessage(t("deviceManagement:messages.updateSuccess"));
      dismiss();
    } catch (error) {
      const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.updateFailed");
      setFeedback(message); onMessage(message);
    } finally {
      setSaving(false);
    }
  }, [allowTransactions, detail, deviceSystem, deviceType, dismiss, language, onMessage, onSaved, remark, t, transactionSupported]);

  return (
    <BusinessSheet visible={visible} title={t("deviceManagement:edit.title")} onDismiss={dismiss} dismissable={!saving}
      footer={<SheetFooter><Button mode="contained" onPress={() => void save()} loading={saving} disabled={loading || !detail}>{t("deviceManagement:actions.save")}</Button></SheetFooter>}>
      {feedback ? <HelperText type="error" visible>{feedback}</HelperText> : null}
      {loading || !detail ? <Text>{t("deviceManagement:messages.detailLoading")}</Text> : <>
        <View style={styles.detailGrid}>
          <KeyValue label={t("deviceManagement:fields.deviceNumber")} value={detail.systemDeviceNumber} />
          <KeyValue label={t("deviceManagement:fields.hardwareId")} value={detail.hardwareId} />
          <KeyValue label={t("deviceManagement:fields.store")} value={detail.storeName || detail.storeCode || "–"} />
          <KeyValue label={t("deviceManagement:fields.status")} value={t(`deviceManagement:statuses.${getDeviceStatusKey(detail.status)}`)} />
          <KeyValue label={t("deviceManagement:fields.audit")} value={`${detail.createdBy || "–"} / ${detail.lastModifiedBy || "–"}`} />
          <KeyValue label={t("deviceManagement:fields.lastHeartbeat")} value={formatDateTime(detail.lastHeartbeatAt, language)} />
        </View>
        <Text variant="labelLarge">{t("deviceManagement:edit.deviceType")}</Text>
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.segmentedScroll}>
          <SegmentedButtons value={deviceType} onValueChange={setDeviceType} buttons={["Mobile", "PDA", "POS", "Admin"].map((value) => ({ value, label: value, disabled: legacyIosPos }))} />
        </ScrollView>
        <Text variant="labelLarge">{t("deviceManagement:edit.deviceSystem")}</Text>
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.segmentedScroll}>
          <SegmentedButtons value={deviceSystem} onValueChange={setDeviceSystem} buttons={(legacyIosPos ? ["iOS", "iPadOS"] : ["Android", "iOS", "iPadOS", "Windows", "Mac"]).map((value) => ({ value, label: value, disabled: !systemEditable }))} />
        </ScrollView>
        <View style={BUSINESS_UI.row}>
          <View style={styles.grow}><Text variant="labelLarge">{t("deviceManagement:edit.allowTransactions")}</Text><Text variant="bodySmall" style={BUSINESS_UI.fieldLabel}>{transactionSupported ? t("deviceManagement:edit.transactionApplicable") : t("deviceManagement:edit.transactionUnavailable")}</Text></View>
          <Switch value={transactionSupported && allowTransactions} onValueChange={setAllowTransactions} disabled={!transactionSupported} />
        </View>
        {!systemEditable ? <HelperText type="info">{t("deviceManagement:edit.systemReadOnly")}</HelperText> : null}
        <TextInput label={t("deviceManagement:edit.remark")} value={remark} onChangeText={setRemark} multiline maxLength={500} />
      </>}
    </BusinessSheet>
  );
}

function GrantStatusChip({ status, t }: { status: DeviceActivationStatus; t: Translate }) {
  const danger = status === "Revoked" || status === "Expired";
  return <Chip compact style={danger ? styles.dangerChip : status === "Consumed" ? styles.neutralChip : styles.successChip}>{t(`deviceManagement:activation.statuses.${status}`)}</Chip>;
}

function OneTimeResultSheet({
  result,
  title,
  language,
  t,
  onDismiss,
  onMessage,
}: {
  result: { secret: string; grantLabel: string; expiresAt: string } | null;
  title: string;
  language: string;
  t: Translate;
  onDismiss: () => void;
  onMessage: (message: string) => void;
}) {
  const qrRef = useRef<{ toDataURL: (callback: (data: string) => void) => void }>(null);
  const [feedback, setFeedback] = useState("");
  useEffect(() => { setFeedback(""); }, [result]);
  const close = useCallback(() => onDismiss(), [onDismiss]);
  const copy = useCallback(async () => {
    if (!result) return;
    try { await Clipboard.setStringAsync(result.secret); const message = t("deviceManagement:messages.copySuccess"); setFeedback(message); onMessage(message); }
    catch { const message = t("deviceManagement:messages.copyFailed"); setFeedback(message); onMessage(message); }
  }, [onMessage, result, t]);
  const exportQr = useCallback(async () => {
    if (!result || !qrRef.current) return;
    try {
      if (!(await Sharing.isAvailableAsync())) throw new Error("SHARING_UNAVAILABLE");
      qrRef.current.toDataURL(async (base64) => {
        let file: string | null = null;
        try {
          file = `${FileSystem.cacheDirectory}hb-device-access-${Date.now()}.png`;
          await FileSystem.writeAsStringAsync(file, base64, { encoding: FileSystem.EncodingType.Base64 });
          await Sharing.shareAsync(file, { mimeType: "image/png", dialogTitle: t("deviceManagement:actions.exportQr") });
        } catch { const message = t("deviceManagement:messages.exportFailed"); setFeedback(message); onMessage(message); }
        finally { if (file) await FileSystem.deleteAsync(file, { idempotent: true }).catch(() => undefined); }
      });
    } catch { const message = t("deviceManagement:messages.exportFailed"); setFeedback(message); onMessage(message); }
  }, [onMessage, result, t]);
  return <BusinessSheet visible={Boolean(result)} title={title} subtitle={t("deviceManagement:activation.oneTimeWarning")} onDismiss={close}
    footer={<SheetFooter><Button mode="contained" onPress={close}>{t("deviceManagement:actions.done")}</Button></SheetFooter>}>
    {feedback ? <HelperText type="info" visible>{feedback}</HelperText> : null}{result ? <>
      <View style={styles.qrWrap}><QRCode getRef={(ref) => { qrRef.current = ref as unknown as { toDataURL: (callback: (data: string) => void) => void }; }} value={result.secret} size={212} /></View>
      <Text selectable style={styles.secret}>{result.secret}</Text>
      <KeyValue label={t("deviceManagement:fields.grant")} value={result.grantLabel} />
      <KeyValue label={t("deviceManagement:fields.expiresAt")} value={formatDateTime(result.expiresAt, language)} />
      <Button icon="content-copy" mode="outlined" onPress={() => void copy()}>{t("deviceManagement:actions.copy")}</Button>
      <Button icon="download" mode="outlined" onPress={() => void exportQr()}>{t("deviceManagement:actions.exportQr")}</Button>
    </> : null}
  </BusinessSheet>;
}

export function ActivationCodePanel({
  canManagePos,
  canManageMobile,
  userGuid,
  language,
  t,
  onMessage,
}: {
  canManagePos: boolean;
  canManageMobile: boolean;
  userGuid: string | null | undefined;
  language: string;
  t: Translate;
  onMessage: (message: string) => void;
}) {
  const initialKind: ActivationKind = canManagePos ? "POS" : "Mobile";
  const [kind, setKind] = useState<ActivationKind>(initialKind);
  const [grants, setGrants] = useState<DeviceActivationGrant[]>([]);
  const [stores, setStores] = useState<ManageableStore[]>([]);
  // 分店筛选弹窗与开通分店芯片统一按分店名字排序（接口原序不保证）。
  const sortedStores = useMemo(() => sortStoresByName(stores, language), [stores, language]);
  const [storesLoading, setStoresLoading] = useState(false);
  const [storesError, setStoresError] = useState("");
  const [loading, setLoading] = useState(false);
  const [createVisible, setCreateVisible] = useState(false);
  const [listQuery, setListQuery] = useState<{ page: number; storeCode?: string; deviceSystem?: DeviceActivationSystem; status?: DeviceActivationStatus }>({ page: 1 });
  const [total, setTotal] = useState(0);
  const [totalPages, setTotalPages] = useState(1);
  const [listError, setListError] = useState("");
  const [storeFilterVisible, setStoreFilterVisible] = useState(false);
  const [storeSearch, setStoreSearch] = useState("");
  const [revokeGrant, setRevokeGrant] = useState<DeviceActivationGrant | null>(null);
  const [created, setCreated] = useState<{ secret: string; grantLabel: string; expiresAt: string } | null>(null);
  const [storeCode, setStoreCode] = useState("");
  const [system, setSystem] = useState<DeviceActivationSystem>("Windows");
  const [minutes, setMinutes] = useState<30 | 120 | 1440>(1440);
  const [reason, setReason] = useState("");
  const [accounts, setAccounts] = useState<ManageableAccount[]>([]);
  const [targetUserGuid, setTargetUserGuid] = useState("");
  const [revokeReason, setRevokeReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [sheetMessage, setSheetMessage] = useState("");
  const [createOutcomeUnknown, setCreateOutcomeUnknown] = useState(false);
  const createContextKey = JSON.stringify([userGuid, kind]);
  const loadGate = useRef(new RequestGenerationGate());
  const storesGate = useRef(new RequestGenerationGate());
  const mutationGate = useRef(new RequestGenerationGate());
  const createInFlight = useRef(false);

  const load = useCallback(async () => {
    const requestGeneration = loadGate.current.begin();
    const requestKind = kind;
    const requestAllowed = requestKind === "POS" ? canManagePos : canManageMobile;
    if (!requestAllowed) { setGrants([]); setTotal(0); setTotalPages(1); setLoading(false); return false; }
    setLoading(true);
    setListError("");
    try {
      const nextGrants = await (kind === "POS" ? getPosActivationGrants({ ...listQuery, pageSize: 30 }) : getMobileActivationGrants({ ...listQuery, pageSize: 30 }));
      if (!loadGate.current.isCurrent(requestGeneration) || requestKind !== kind) return false;
      const nextTotalPages = Math.max(1, nextGrants.totalPages);
      if (listQuery.page > nextTotalPages) {
        setListQuery((current) => ({ ...current, page: nextTotalPages }));
        return false;
      }
      setGrants(nextGrants.items);
      setTotal(nextGrants.total);
      setTotalPages(nextTotalPages);
      return true;
    } catch (error) {
      if (!loadGate.current.isCurrent(requestGeneration)) return false;
      onMessage(getSafeErrorMessage(error, t, language, "deviceManagement:messages.activationLoadFailed"));
      setListError(getSafeErrorMessage(error, t, language, "deviceManagement:messages.activationLoadFailed"));
      return false;
    } finally { if (loadGate.current.isCurrent(requestGeneration)) setLoading(false); }
  }, [canManageMobile, canManagePos, kind, language, listQuery, onMessage, t]);

  // 门店选项与授权记录独立加载，选项接口失败不能遮住结果核对和撤销入口。
  const loadStores = useCallback(async () => {
    const requestGeneration = storesGate.current.begin();
    if (!(kind === "POS" ? canManagePos : canManageMobile)) {
      setStores([]); setStoresLoading(false); return;
    }
    setStoresLoading(true); setStoresError("");
    try {
      const nextStores = await (kind === "POS" ? getPosManageableStores() : getMobileManageableStores());
      if (storesGate.current.isCurrent(requestGeneration)) setStores(nextStores);
    } catch (error) {
      if (!storesGate.current.isCurrent(requestGeneration)) return;
      setStoresError(getSafeErrorMessage(error, t, language, "deviceManagement:messages.activationStoresLoadFailed"));
    } finally {
      if (storesGate.current.isCurrent(requestGeneration)) setStoresLoading(false);
    }
  }, [canManageMobile, canManagePos, kind, language, t]);

  useEffect(() => {
    setKind((current) => {
      if (current === "POS" && !canManagePos && canManageMobile) return "Mobile";
      if (current === "Mobile" && !canManageMobile && canManagePos) return "POS";
      return current;
    });
  }, [canManageMobile, canManagePos]);
  // 筛选/翻页只更新列表；账号、权限或类型改变才销毁一次性结果与表单。
  useEffect(() => {
    loadGate.current.invalidate(); setGrants([]); setStores([]); setCreated(null); setAccounts([]); setTargetUserGuid(""); setStoreCode(""); setCreateVisible(false); setRevokeGrant(null); setSheetMessage("");
    storesGate.current.invalidate(); setStoresError("");
    mutationGate.current.invalidate(); createInFlight.current = false; setSaving(false);
    setListQuery({ page: 1 }); setTotal(0); setTotalPages(1); setListError(""); setStoreFilterVisible(false); setStoreSearch("");
  }, [canManageMobile, canManagePos, kind, userGuid]);
  useEffect(() => {
    const gate = loadGate.current;
    void load();
    return () => { gate.invalidate(); };
  }, [load, userGuid]);
  useEffect(() => {
    const gate = storesGate.current;
    void loadStores();
    return () => { gate.invalidate(); };
  }, [loadStores, userGuid]);
  useEffect(() => { setCreateOutcomeUnknown(!activationCreateGate.canSubmit(createContextKey)); }, [canManageMobile, canManagePos, createContextKey]);
  useEffect(() => {
    // 新门店必须重新选择账号，不能在名单加载期间沿用上一门店的目标。
    setAccounts([]); setTargetUserGuid("");
    if (!createVisible || kind !== "Mobile" || !storeCode) return;
    let active = true;
    void getMobileManageableAccounts(storeCode).then((items) => {
      if (active) { setAccounts(items); setTargetUserGuid(""); }
    }).catch((error) => { if (active) { const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.accountLoadFailed"); setSheetMessage(message); onMessage(message); } });
    return () => { active = false; };
  }, [createVisible, kind, language, onMessage, storeCode, t]);

  const openCreate = () => {
    setCreateOutcomeUnknown(!activationCreateGate.canSubmit(createContextKey));
    setStoreCode("");
    setSystem(kind === "Mobile" ? "Android" : "Windows");
    setMinutes(1440); setReason(""); setTargetUserGuid(""); setSheetMessage(""); setCreateVisible(true);
  };
  const closeCreate = () => { setCreateVisible(false); setReason(""); setAccounts([]); setTargetUserGuid(""); setSheetMessage(""); };
  const submitCreate = async () => {
    if (!storeCode || !reasonIsValid(reason) || (kind === "Mobile" && !targetUserGuid) || createInFlight.current || createOutcomeUnknown) return;
    if (!activationCreateGate.begin(createContextKey)) { setCreateOutcomeUnknown(true); return; }
    createInFlight.current = true;
    const mutationGeneration = mutationGate.current.begin();
    const requestGeneration = loadGate.current.begin();
    setSaving(true);
    try {
      const result: ActivationCodeCreateResult = kind === "Mobile"
        ? await createMobileActivationCode({ storeCode, deviceSystem: system as MobileActivationSystem, targetUserGuid, validForMinutes: minutes, reason })
        : await createPosActivationCode({ storeCode, deviceSystem: system, validForMinutes: minutes, reason });
      if (!loadGate.current.isCurrent(requestGeneration)) { activationCreateGate.markUnknown(createContextKey); return; }
      setCreated({ secret: result.activationCode, grantLabel: `${kind} · ${result.grant.storeCode}`, expiresAt: result.grant.expiresAtUtc });
      activationCreateGate.clearForNewContext(createContextKey);
      closeCreate();
      void load();
    } catch (error) {
      // 超时等结果未知时只回读，绝不自动重复创建。
      if (isResultUnknownCreateError(error)) activationCreateGate.markUnknown(createContextKey);
      else activationCreateGate.clearForNewContext(createContextKey);
      if (!loadGate.current.isCurrent(requestGeneration)) return;
      const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.activationCreateFailed");
      setSheetMessage(message); setCreateOutcomeUnknown(isResultUnknownCreateError(error)); onMessage(message);
      void load();
    } finally { if (mutationGate.current.isCurrent(mutationGeneration)) { createInFlight.current = false; setSaving(false); } }
  };
  const recoverCreate = async () => {
    if (saving || activationCreateGate.isPending(createContextKey)) { setSheetMessage(t("deviceManagement:activation.pendingHint")); return; }
    if (!(await load())) { setSheetMessage(t("deviceManagement:messages.activationLoadFailed")); return; }
    const requestGeneration = loadGate.current.begin();
    Alert.alert(t("deviceManagement:activation.recoveryTitle"), t("deviceManagement:activation.recoveryHint"), [
      { text: t("deviceManagement:actions.cancel"), style: "cancel" },
      { text: t("deviceManagement:activation.recoveryConfirm"), onPress: () => {
        if (!loadGate.current.isCurrent(requestGeneration) || activationCreateGate.isPending(createContextKey)) return;
        activationCreateGate.clearForNewContext(createContextKey);
        setCreateOutcomeUnknown(false); setSheetMessage(t("deviceManagement:activation.recoveredHint"));
      } },
    ]);
  };
  const submitRevoke = async () => {
    if (!revokeGrant || !reasonIsValid(revokeReason) || createInFlight.current) return;
    createInFlight.current = true;
    const mutationGeneration = mutationGate.current.begin();
    const requestGeneration = loadGate.current.begin();
    setSaving(true);
    try {
      if (kind === "POS") await revokePosActivationCode(revokeGrant.grantId, revokeReason);
      else await revokeMobileActivationCode(revokeGrant.grantId, revokeReason);
      if (!loadGate.current.isCurrent(requestGeneration)) return;
      // 不能因撤销任意旧记录就解除另一次结果未知的创建锁。
      await load(); setRevokeGrant(null); setRevokeReason(""); onMessage(t("deviceManagement:messages.activationRevokeSuccess"));
    } catch (error) { if (!loadGate.current.isCurrent(requestGeneration)) return; const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.activationRevokeFailed"); setSheetMessage(message); onMessage(message); }
    finally { if (mutationGate.current.isCurrent(mutationGeneration)) { createInFlight.current = false; setSaving(false); } }
  };
  const eligibleSystems = kind === "Mobile" ? ["Android", "iOS"] : ["Windows", "iPadOS", "Android", "iOS"];
  return <View style={styles.activationPanel}>
    <SegmentedButtons value={kind} onValueChange={(value) => setKind(value as ActivationKind)} buttons={[{ value: "POS", label: t("deviceManagement:activation.pos"), disabled: !canManagePos }, { value: "Mobile", label: t("deviceManagement:activation.mobile"), disabled: !canManageMobile }]} />
    <ScrollView style={styles.activationList} contentContainerStyle={styles.activationListContent} keyboardShouldPersistTaps="handled">
    <View style={BUSINESS_UI.section}><View style={BUSINESS_UI.sectionContent}>
      <Button mode="outlined" icon="store-outline" onPress={() => { setStoreSearch(""); setStoreFilterVisible(true); }}>{listQuery.storeCode ? `${listQuery.storeCode} · ${stores.find((store) => store.storeCode === listQuery.storeCode)?.storeName ?? ""}` : t("deviceManagement:activation.allStores")}</Button>
      <Text variant="labelLarge">{t("deviceManagement:activation.system")}</Text>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.filterChips}>
        <Chip selected={!listQuery.deviceSystem} onPress={() => setListQuery((current) => ({ ...current, page: 1, deviceSystem: undefined }))}>{t("deviceManagement:activation.allSystems")}</Chip>
        {eligibleSystems.map((value) => <Chip key={value} selected={listQuery.deviceSystem === value} onPress={() => setListQuery((current) => ({ ...current, page: 1, deviceSystem: value as DeviceActivationSystem }))}>{value}</Chip>)}
      </ScrollView>
      <Text variant="labelLarge">{t("deviceManagement:fields.status")}</Text>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.filterChips}>
        <Chip selected={!listQuery.status} onPress={() => setListQuery((current) => ({ ...current, page: 1, status: undefined }))}>{t("deviceManagement:activation.allStatuses")}</Chip>
        {(["Available", "Consumed", "Expired", "Revoked"] as const).map((value) => <Chip key={value} selected={listQuery.status === value} onPress={() => setListQuery((current) => ({ ...current, page: 1, status: value }))}>{t(`deviceManagement:activation.statuses.${value}`)}</Chip>)}
      </ScrollView>
      <Button icon="refresh" loading={loading} disabled={loading} onPress={() => void load()}>{t("deviceManagement:activation.refresh")}</Button>
    </View></View>
    {storesError ? <View><HelperText type="error" visible>{storesError}</HelperText><Button onPress={() => void loadStores()}>{t("deviceManagement:activation.retryStores")}</Button></View> : null}
    <Button mode="contained" icon="plus" loading={storesLoading} disabled={storesLoading || !stores.length} onPress={openCreate}>{t("deviceManagement:activation.create")}</Button>
    {loading ? <Text>{t("deviceManagement:messages.loading")}</Text> : listError ? <View><HelperText type="error" visible>{listError}</HelperText><Button onPress={() => void load()}>{t("deviceManagement:activation.retry")}</Button></View> : grants.length === 0 ? <Text style={BUSINESS_UI.subtitle}>{t("deviceManagement:activation.empty")}</Text> : grants.map((grant) => <View key={grant.grantId} style={BUSINESS_UI.section}><View style={BUSINESS_UI.sectionContent}>
      <View style={BUSINESS_UI.headerRow}><View style={styles.grow}><Text variant="titleSmall">{grant.deviceSystem} · {grant.storeCode} {grant.storeName || ""}</Text><Text variant="bodySmall" style={BUSINESS_UI.fieldLabel}>{t(kind === "Mobile" ? "deviceManagement:activation.mobile" : "deviceManagement:activation.pos")}</Text></View><GrantStatusChip status={grant.status} t={t} /></View>
      {kind === "Mobile" ? <KeyValue label={t("deviceManagement:activation.account")} value={[grant.targetFullName, grant.targetUsername].filter(Boolean).join(" · ")} /> : null}
      <KeyValue label={t("deviceManagement:fields.expiresAt")} value={formatDateTime(grant.expiresAtUtc, language)} />
      <KeyValue label={t("deviceManagement:fields.createdAudit")} value={`${grant.createdBy || "–"} · ${formatDateTime(grant.createdAtUtc, language)}`} />
      <KeyValue label={t("deviceManagement:fields.usedBy")} value={grant.consumedDeviceCode || grant.consumedHardwareId || t("deviceManagement:activation.notUsed")} />
      <KeyValue label={t("deviceManagement:activation.consumedAt")} value={formatDateTime(grant.consumedAtUtc, language)} />
      <KeyValue label={t("deviceManagement:activation.consumptionKind")} value={grant.consumptionKind || "–"} />
      <KeyValue label={t("deviceManagement:fields.reason")} value={grant.reason} />
      {grant.status === "Available" || grant.status === "Expired" ? <Button mode="outlined" textColor={HB_COLORS.danger} onPress={() => { setRevokeGrant(grant); setRevokeReason(""); setSheetMessage(""); }}>{t("deviceManagement:actions.revoke")}</Button> : null}
    </View></View>)}
    </ScrollView>
    <View style={styles.pagination}>
      <Text variant="bodySmall">{t("deviceManagement:activation.pageSummary", { page: listQuery.page, pages: totalPages, total })}</Text>
      <View style={BUSINESS_UI.row}>
        <Button disabled={loading || listQuery.page <= 1} onPress={() => setListQuery((current) => ({ ...current, page: current.page - 1 }))}>{t("deviceManagement:activation.previous")}</Button>
        <Button disabled={loading || listQuery.page >= totalPages} onPress={() => setListQuery((current) => ({ ...current, page: current.page + 1 }))}>{t("deviceManagement:activation.next")}</Button>
      </View>
    </View>
    <BusinessSheet visible={storeFilterVisible} title={t("deviceManagement:activation.store")} onDismiss={() => setStoreFilterVisible(false)}>
      {storesError ? <View><HelperText type="error" visible>{storesError}</HelperText><Button onPress={() => void loadStores()}>{t("deviceManagement:activation.retryStores")}</Button></View> : null}
      {storesLoading ? <Text>{t("deviceManagement:messages.loading")}</Text> : null}
      <TextInput mode="outlined" label={t("deviceManagement:activation.searchStore")} value={storeSearch} onChangeText={setStoreSearch} />
      <Button onPress={() => { setListQuery((current) => ({ ...current, page: 1, storeCode: undefined })); setStoreFilterVisible(false); }}>{t("deviceManagement:activation.allStores")}</Button>
      {sortedStores.filter((store) => `${store.storeCode} ${store.storeName}`.toLocaleLowerCase().includes(storeSearch.trim().toLocaleLowerCase())).map((store) => <Button key={store.storeCode} mode={listQuery.storeCode === store.storeCode ? "contained-tonal" : "text"} onPress={() => { setListQuery((current) => ({ ...current, page: 1, storeCode: store.storeCode })); setStoreFilterVisible(false); }}>{store.storeCode} · {store.storeName}</Button>)}
    </BusinessSheet>
    <BusinessSheet visible={createVisible} title={t("deviceManagement:activation.create")} onDismiss={closeCreate} dismissable={!saving} footer={<SheetFooter>{createOutcomeUnknown ? <><Button mode="outlined" disabled={loading || saving} onPress={() => void recoverCreate()}>{t("deviceManagement:activation.recoveryAction")}</Button><Button onPress={closeCreate}>{t("deviceManagement:actions.done")}</Button></> : <Button mode="contained" loading={saving} disabled={saving || !storeCode || !reasonIsValid(reason) || (kind === "Mobile" && !targetUserGuid)} onPress={() => void submitCreate()}>{t("deviceManagement:actions.create")}</Button>}</SheetFooter>}>
      {sheetMessage ? <HelperText type="error" visible>{sheetMessage}</HelperText> : null}
      {createOutcomeUnknown ? <HelperText type="error" visible>{t("deviceManagement:activation.unknownOutcomeHint")}</HelperText> : null}
      <Text variant="labelLarge">{t("deviceManagement:activation.store")}</Text><View style={styles.chips}>{sortedStores.map((store) => <Chip key={store.storeCode} selected={store.storeCode === storeCode} onPress={() => { setStoreCode(store.storeCode); setAccounts([]); setTargetUserGuid(""); }}>{store.storeCode} · {store.storeName}</Chip>)}</View>
      <Text variant="labelLarge">{t("deviceManagement:activation.system")}</Text><ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.segmentedScroll}><SegmentedButtons value={system} onValueChange={(value) => setSystem(value as DeviceActivationSystem)} buttons={eligibleSystems.map((value) => ({ value, label: value }))} /></ScrollView>
      {kind === "Mobile" ? <><Text variant="labelLarge">{t("deviceManagement:activation.account")}</Text><View style={styles.chips}>{accounts.map((account) => <Chip key={account.userGuid} selected={account.userGuid === targetUserGuid} onPress={() => setTargetUserGuid(account.userGuid)}>{account.fullName || account.username}</Chip>)}</View><HelperText type="info">{t("deviceManagement:activation.accountHint")}</HelperText></> : null}
      <Text variant="labelLarge">{t("deviceManagement:activation.validity")}</Text><SegmentedButtons value={String(minutes)} onValueChange={(value) => setMinutes(Number(value) as 30 | 120 | 1440)} buttons={[{ value: "30", label: t("deviceManagement:activation.minutes30") }, { value: "120", label: t("deviceManagement:activation.minutes120") }, { value: "1440", label: t("deviceManagement:activation.minutes1440") }]} />
      <TextInput label={t("deviceManagement:activation.reason")} value={reason} onChangeText={setReason} multiline maxLength={200} /><HelperText type={reasonIsValid(reason) ? "info" : "error"}>{t("deviceManagement:activation.reasonHint")}</HelperText>
    </BusinessSheet>
    <BusinessSheet visible={Boolean(revokeGrant)} title={t("deviceManagement:activation.revokeTitle")} onDismiss={() => setRevokeGrant(null)} dismissable={!saving} footer={<SheetFooter><Button mode="contained" buttonColor={HB_COLORS.danger} loading={saving} disabled={saving || !reasonIsValid(revokeReason)} onPress={() => void submitRevoke()}>{t("deviceManagement:actions.revoke")}</Button></SheetFooter>}>
      {sheetMessage ? <HelperText type="error" visible>{sheetMessage}</HelperText> : null}<Text>{revokeGrant ? `${revokeGrant.deviceSystem} · ${revokeGrant.storeCode}` : ""}</Text><TextInput label={t("deviceManagement:activation.revokeReason")} value={revokeReason} onChangeText={setRevokeReason} multiline maxLength={200} /><HelperText type={reasonIsValid(revokeReason) ? "info" : "error"}>{t("deviceManagement:activation.reasonHint")}</HelperText>
    </BusinessSheet>
    <OneTimeResultSheet result={created} title={t("deviceManagement:activation.createdTitle")} language={language} t={t} onMessage={onMessage} onDismiss={() => setCreated(null)} />
  </View>;
}

export function EmergencyLoginSheet({ visible, storeCode, userGuid, language, t, onDismiss, onMessage }: { visible: boolean; storeCode: string | null; userGuid: string | null; language: string; t: Translate; onDismiss: () => void; onMessage: (message: string) => void; }) {
  const [grant, setGrant] = useState<EmergencyLoginGrant | null>(null);
  const [, setToken] = useState<string | null>(null);
  const [oneTimeResult, setOneTimeResult] = useState<{ secret: string; grantLabel: string; expiresAt: string } | null>(null);
  const [reason, setReason] = useState("");
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [feedback, setFeedback] = useState("");
  const [createOutcomeUnknown, setCreateOutcomeUnknown] = useState(false);
  const createContextKey = JSON.stringify([userGuid, storeCode]);
  const createInFlight = useRef(false);
  const generation = useRef(0);
  const close = useCallback(() => { generation.current += 1; setGrant(null); setToken(null); setOneTimeResult(null); setReason(""); setFeedback(""); onDismiss(); }, [onDismiss]);
  const read = useCallback(async (silent = false, expectedGeneration = generation.current) => {
    if (!storeCode) return false;
    setLoading(true);
    try {
      const next = await getEmergencyLoginGrant(storeCode);
      if (generation.current !== expectedGeneration) return false;
      setGrant(next);
      return true;
    } catch (error) {
      if (!silent && generation.current === expectedGeneration) {
        const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.emergencyLoadFailed");
        setFeedback(message); onMessage(message);
      }
      return false;
    } finally { if (generation.current === expectedGeneration) setLoading(false); }
  }, [language, onMessage, storeCode, t]);
  // 切回之前的账号/门店时恢复其未知结果，关窗和切换都不能解除限制。
  useEffect(() => {
    setCreateOutcomeUnknown(!emergencyCreateGate.canSubmit(createContextKey));
  }, [createContextKey, visible]);
  useEffect(() => {
    const requestGeneration = ++generation.current;
    setGrant(null); setToken(null); setOneTimeResult(null); setReason(""); setFeedback("");
    setSaving(false); createInFlight.current = false;
    if (visible) void read(false, requestGeneration);
    return () => { generation.current += 1; };
  }, [read, userGuid, visible]);
  const create = async () => {
    if (!storeCode || !reasonIsValid(reason) || createInFlight.current || !emergencyCreateGate.begin(createContextKey)) return;
    createInFlight.current = true;
    const requestGeneration = ++generation.current;
    setSaving(true); setFeedback("");
    try {
      const result = await createEmergencyLoginGrant(storeCode, reason);
      if (generation.current !== requestGeneration) { emergencyCreateGate.markUnknown(createContextKey); return; }
      setGrant(result.grant); setToken(result.token);
      setOneTimeResult({ secret: result.token, grantLabel: result.grant.grantId, expiresAt: result.grant.expiresAtUtc });
      emergencyCreateGate.clearForNewContext(createContextKey);
      void read(true, requestGeneration);
    } catch (error) {
      const unknown = isResultUnknownCreateError(error);
      // 即使页面已经切走，也必须记住这一次请求的结果，供返回原门店时恢复。
      if (unknown) emergencyCreateGate.markUnknown(createContextKey);
      else emergencyCreateGate.clearForNewContext(createContextKey);
      if (generation.current === requestGeneration) {
        const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.emergencyCreateFailed");
        setFeedback(message); setCreateOutcomeUnknown(unknown); onMessage(message); void read(true, requestGeneration);
      }
    } finally {
      if (generation.current === requestGeneration) { createInFlight.current = false; setSaving(false); }
    }
  };
  const recoverCreate = async () => {
    if (saving || emergencyCreateGate.isPending(createContextKey)) { setFeedback(t("deviceManagement:activation.pendingHint")); return; }
    const requestGeneration = generation.current;
    if (!(await read(false, requestGeneration)) || generation.current !== requestGeneration) return;
    Alert.alert(t("deviceManagement:activation.recoveryTitle"), t("deviceManagement:activation.recoveryHint"), [
      { text: t("deviceManagement:actions.cancel"), style: "cancel" },
      { text: t("deviceManagement:activation.recoveryConfirm"), onPress: () => {
        if (generation.current !== requestGeneration || emergencyCreateGate.isPending(createContextKey)) return;
        emergencyCreateGate.clearForNewContext(createContextKey);
        setCreateOutcomeUnknown(false); setFeedback(t("deviceManagement:activation.recoveredHint"));
      } },
    ]);
  };
  const revoke = async () => {
    if (!grant) return;
    const requestGeneration = generation.current;
    Alert.alert(t("deviceManagement:emergency.revokeTitle"), t("deviceManagement:emergency.revokeMessage"), [
      { text: t("deviceManagement:actions.cancel"), style: "cancel" },
      { text: t("deviceManagement:actions.revoke"), style: "destructive", onPress: () => {
        if (generation.current !== requestGeneration || createInFlight.current) return;
        createInFlight.current = true;
        void (async () => {
          setSaving(true);
          try {
            await revokeEmergencyLoginGrant(grant.grantId, t("deviceManagement:emergency.defaultRevokeReason"));
            if (generation.current !== requestGeneration) return;
            setGrant(null); setToken(null); setOneTimeResult(null);
            await read(false, requestGeneration);
          } catch (error) {
            if (generation.current !== requestGeneration) return;
            const message = getSafeErrorMessage(error, t, language, "deviceManagement:messages.emergencyRevokeFailed");
            setFeedback(message); onMessage(message);
          } finally {
            if (generation.current === requestGeneration) { createInFlight.current = false; setSaving(false); }
          }
        })();
      } },
    ]);
  };
  return <BusinessSheet visible={visible} title={t("deviceManagement:emergency.title")} subtitle={storeCode ? t("deviceManagement:emergency.store", { store: storeCode }) : t("deviceManagement:emergency.selectStore")} onDismiss={close} dismissable={!saving} footer={<SheetFooter>{createOutcomeUnknown ? <Button mode="outlined" disabled={loading || saving} onPress={() => void recoverCreate()}>{t("deviceManagement:activation.recoveryAction")}</Button> : null}{grant?.status === "Active" ? <Button mode="outlined" textColor={HB_COLORS.danger} loading={saving} onPress={revoke}>{t("deviceManagement:actions.revoke")}</Button> : <Button mode="contained" loading={saving} disabled={saving || createOutcomeUnknown || !storeCode || !reasonIsValid(reason)} onPress={() => void create()}>{t("deviceManagement:actions.create")}</Button>}</SheetFooter>}>
    {feedback ? <HelperText type="error" visible>{feedback}</HelperText> : null}{createOutcomeUnknown ? <HelperText type="error" visible>{t("deviceManagement:activation.unknownOutcomeHint")}</HelperText> : null}{loading ? <Text>{t("deviceManagement:messages.loading")}</Text> : grant?.status === "Active" ? <><KeyValue label={t("deviceManagement:fields.status")} value={grant.status} /><KeyValue label={t("deviceManagement:fields.expiresAt")} value={formatDateTime(grant.expiresAtUtc, language)} /><KeyValue label={t("deviceManagement:fields.reason")} value={grant.reason} /><KeyValue label={t("deviceManagement:fields.grant")} value={grant.grantId} /><HelperText type="info">{t("deviceManagement:emergency.existingHint")}</HelperText></> : <><HelperText type="info">{t("deviceManagement:emergency.createHint")}</HelperText><TextInput label={t("deviceManagement:emergency.reason")} value={reason} onChangeText={setReason} multiline maxLength={200} /><HelperText type={reasonIsValid(reason) ? "info" : "error"}>{t("deviceManagement:activation.reasonHint")}</HelperText></>}
    <OneTimeResultSheet result={oneTimeResult} title={t("deviceManagement:emergency.createdTitle")} language={language} t={t} onMessage={onMessage} onDismiss={() => { setToken(null); setOneTimeResult(null); }} />
  </BusinessSheet>;
}

const styles = StyleSheet.create({
  activationPanel: { flex: 1, minHeight: 0, gap: 12 },
  activationList: { flex: 1 },
  activationListContent: { gap: 12, paddingBottom: 16 },
  filterChips: { gap: 8 },
  pagination: { alignItems: "center", borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outline, paddingTop: 8 },
  grow: { flex: 1, minWidth: 0 },
  detailGrid: { gap: 10 },
  keyValue: { gap: 2 },
  sheetFooter: { gap: 8, paddingBottom: 8 },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  segmentedScroll: { minWidth: "100%" },
  successChip: { backgroundColor: "#DCFCE7" },
  neutralChip: { backgroundColor: "#E2E8F0" },
  dangerChip: { backgroundColor: "#FEE2E2" },
  qrWrap: { alignItems: "center", paddingVertical: 12, backgroundColor: "#FFFFFF" },
  secret: { textAlign: "center", fontVariant: ["tabular-nums"], fontWeight: "700", color: HB_COLORS.textPrimary },
});
