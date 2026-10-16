// 「摄像头」区（需要 FaceCapture.Cameras.Manage）：所选门店的摄像头列表，可启用 / 停用、新增、编辑、重新生成令牌、查看最近抓拍。
// 与网页后台同一套规则（字段规则见 cameras-logic.ts）。令牌相关的安全约束：
// 1. 新增 / 重新生成令牌的响应里有一次性地址（含 token）：不走 useMutation（它的结果会留在 react-query 的 mutation 缓存里），
//    而是在事件处理函数里直接调接口，地址只放局部 state（SecretRevealState），关闭 / 离开前台 / 卸载即清空；
// 2. 列表接口本来就不返回令牌，归一化也不保留它们；
// 3. 看抓拍图片需要另外的 FaceCapture.Images.View 权限，由服务端判定（见 CameraCapturesModal）。
// 摄像机没有心跳：状态写「近期有推送 / 暂无推送」，不写在线 / 离线。
import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Dialog, Portal, Switch, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { faceMatchApi } from "./api";
import { CameraCapturesModal } from "./CameraCapturesModal";
import { CameraFormModal } from "./CameraFormModal";
import { CameraSecretModal, CameraSecretPanel } from "./CameraSecretDialog";
import {
  SECRET_REVEAL_CLOSED,
  closeSecretReveal,
  openSecretReveal,
  type SecretRevealState,
} from "./camera-secret";
import {
  CAMERA_STATUS_LABEL_KEYS,
  buildCameraEnabledRequest,
  buildCreateCameraRequest,
  buildEditCameraRequest,
  cameraDisplayName,
  countCameraStatuses,
  filterCamerasByStore,
  getCameraStatus,
  hasParseFailures,
  type CameraFormValues,
  type CameraStatus,
} from "./cameras-logic";
import { formatLocalDateTime } from "./dates";
import { formatStoreLabel, useFaceErrorMessage, useStoreNameMap } from "./shared";
import type { FaceCamera, FaceCameraList } from "./types";
import { faceMatchKeys, useFaceCameras } from "./use-face-match";
import { FaceCard, MONO, Notice, SectionTitle, Tag, faceStyles, type FaceTone } from "./ui";

const STATUS_TONES: Record<CameraStatus, FaceTone> = {
  recentlyActive: "success",
  noRecentPush: "neutral",
  disabled: "neutral",
};

type FormTarget = { mode: "create" } | { mode: "edit"; camera: FaceCamera };

export function CamerasSection({
  storeCode,
  stores,
  onNotify,
}: {
  storeCode: string;
  /** 新增 / 编辑摄像头时可选的门店（设置页已有的门店选项）。 */
  stores: readonly Store[];
  onNotify: (message: string) => void;
}) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const storeNames = useStoreNameMap(stores);
  const camerasQuery = useFaceCameras(true);
  const [pendingDisable, setPendingDisable] = useState<FaceCamera | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const [form, setForm] = useState<FormTarget | null>(null);
  const [formBusy, setFormBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [pendingRotate, setPendingRotate] = useState<FaceCamera | null>(null);
  const [rotating, setRotating] = useState(false);
  const [secret, setSecret] = useState<SecretRevealState>(SECRET_REVEAL_CLOSED);
  const [capturesTarget, setCapturesTarget] = useState<FaceCamera | null>(null);

  const replaceInList = (updated: FaceCamera) => {
    // 就地替换这一台，列表其余数据保持不动。
    queryClient.setQueryData<FaceCameraList>(faceMatchKeys.cameras(userGuid), (current) =>
      current ? { ...current, items: current.items.map((item) => (item.cameraId === updated.cameraId ? updated : item)) } : current,
    );
  };
  const refreshList = () => queryClient.invalidateQueries({ queryKey: faceMatchKeys.cameras(userGuid) });

  const mutation = useMutation({
    mutationFn: ({ camera, enabled }: { camera: FaceCamera; enabled: boolean }) =>
      faceMatchApi.updateCamera(camera.cameraId, buildCameraEnabledRequest(camera, enabled)),
    onMutate: ({ camera }) => {
      setBusyId(camera.cameraId);
      setActionError(null);
    },
    onSuccess: (updated, { enabled }) => {
      setPendingDisable(null);
      replaceInList(updated);
      onNotify(t(enabled ? "settings.cameras.enabledDone" : "settings.cameras.disabledDone"));
    },
    onError: (error) => {
      setPendingDisable(null);
      setActionError(describeError(error));
      void refreshList();
    },
    onSettled: () => setBusyId(null),
  });

  const list = camerasQuery.data;
  const cameras = list ? filterCamerasByStore(list.items, storeCode) : [];
  const counts = countCameraStatuses(cameras);

  const toggle = (camera: FaceCamera, enabled: boolean) => {
    if (enabled) {
      mutation.mutate({ camera, enabled: true });
      return;
    }
    // 停用前二次确认；启用直接生效。
    setActionError(null);
    setPendingDisable(camera);
  };

  const openForm = (target: FormTarget) => {
    setFormError(null);
    setForm(target);
  };

  // 新增 / 编辑：用事件处理函数直接调接口（新增的响应带一次性地址，不能进 useMutation 的结果缓存）。
  const submitForm = async (values: CameraFormValues) => {
    if (!form || formBusy) return;
    setFormBusy(true);
    setFormError(null);
    try {
      if (form.mode === "create") {
        const result = await faceMatchApi.createCamera(buildCreateCameraRequest(values));
        // 不关表单：一次性地址作为覆盖层叠在表单这个原生 Modal 里，关地址时再一起收起。
        // 若先关表单 Modal 再开地址 Modal，iOS 会因前一个还在收起而忽略后一个，令牌就丢了。
        setSecret(openSecretReveal("created", result));
        void refreshList();
      } else {
        const updated = await faceMatchApi.updateCamera(form.camera.cameraId, buildEditCameraRequest(values, form.camera.enabled));
        replaceInList(updated);
        setForm(null);
        onNotify(t("settings.cameras.messages.saved"));
        void refreshList();
      }
    } catch (error) {
      // 报错信息只来自服务端错误码映射，不含请求体 / 令牌。
      setFormError(describeError(error));
      // 响应丢了 / 缺地址（FACE_CAMERA_SECRET_MISSING）时服务端可能已经建好：无条件刷新，让用户能在列表里找到它再重新生成令牌。
      void refreshList();
    } finally {
      setFormBusy(false);
    }
  };

  const confirmRotate = async () => {
    const camera = pendingRotate;
    if (!camera || rotating) return;
    setRotating(true);
    setActionError(null);
    try {
      const result = await faceMatchApi.rotateCameraToken(camera.cameraId);
      setPendingRotate(null);
      setSecret(openSecretReveal("rotated", result));
      void refreshList();
    } catch (error) {
      setPendingRotate(null);
      setActionError(describeError(error));
      void refreshList();
    } finally {
      setRotating(false);
    }
  };

  let body;
  if (camerasQuery.isPending) {
    body = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (!list) {
    body = (
      <View style={faceStyles.state}>
        <EmptyState
          title={t("settings.cameras.loadFailed")}
          description={describeError(camerasQuery.error)}
          actionLabel={t("common:actions.retry")}
          onAction={() => void camerasQuery.refetch()}
        />
      </View>
    );
  } else {
    body = (
      <>
        <Notice text={t("settings.cameras.pushNotice", { minutes: list.onlineWindowMinutes, hours: list.statsWindowHours })} />
        {actionError ? <Notice tone="danger" text={actionError} /> : null}
        <Button mode="contained-tonal" icon="plus" onPress={() => openForm({ mode: "create" })} style={styles.addButton}>
          {t("settings.cameras.add")}
        </Button>
        {cameras.length === 0 ? (
          <View style={faceStyles.state}>
            <EmptyState title={t("settings.cameras.empty")} description={t("settings.cameras.emptyHint")} />
          </View>
        ) : (
          <>
            <Text style={faceStyles.muted}>
              {t("settings.cameras.summary", { total: counts.total, active: counts.recentlyActive, quiet: counts.noRecentPush, disabled: counts.disabled })}
            </Text>
            {cameras.map((camera) => (
              <CameraCard
                key={camera.cameraId}
                camera={camera}
                busy={busyId === camera.cameraId}
                onToggle={(enabled) => toggle(camera, enabled)}
                onEdit={() => openForm({ mode: "edit", camera })}
                onRotate={() => {
                  setActionError(null);
                  setPendingRotate(camera);
                }}
                onViewCaptures={() => setCapturesTarget(camera)}
              />
            ))}
          </>
        )}
      </>
    );
  }

  return (
    <View>
      <SectionTitle title={t("settings.cameras.title")} />
      <FaceCard>{body}</FaceCard>
      <Portal>
        <Dialog visible={pendingDisable !== null} onDismiss={mutation.isPending ? undefined : () => setPendingDisable(null)} dismissable={!mutation.isPending}>
          <Dialog.Title>{t("settings.cameras.confirmDisableTitle", { name: pendingDisable ? cameraDisplayName(pendingDisable) : "" })}</Dialog.Title>
          <Dialog.Content>
            <Text style={styles.confirmText}>{t("settings.cameras.confirmDisableContent")}</Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setPendingDisable(null)} disabled={mutation.isPending}>{t("common:actions.cancel")}</Button>
            <Button
              mode="contained"
              buttonColor={HB_COLORS.danger}
              loading={mutation.isPending}
              disabled={mutation.isPending}
              onPress={() => pendingDisable && mutation.mutate({ camera: pendingDisable, enabled: false })}
            >
              {t("settings.cameras.confirmDisableOk")}
            </Button>
          </Dialog.Actions>
        </Dialog>

        <Dialog visible={pendingRotate !== null} onDismiss={rotating ? undefined : () => setPendingRotate(null)} dismissable={!rotating}>
          <Dialog.Title>{t("settings.cameras.confirmRotate.title", { name: pendingRotate ? cameraDisplayName(pendingRotate) : "" })}</Dialog.Title>
          <Dialog.Content>
            <Text style={styles.confirmText}>{t("settings.cameras.confirmRotate.content")}</Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setPendingRotate(null)} disabled={rotating}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" buttonColor={HB_COLORS.danger} loading={rotating} disabled={rotating} onPress={() => void confirmRotate()}>
              {t("settings.cameras.confirmRotate.ok")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>

      {/* 下面三个是原生 Modal，仅在需要时挂载：关闭即卸载，表单 / 抓拍列表 / 一次性地址都不会留在内存里。 */}
      {form ? (
        <CameraFormModal
          // 以模式 + 摄像头编号作 key：换一个目标就重新挂载，不残留上一次的输入。
          key={form.mode === "edit" ? `edit:${form.camera.cameraId}` : "create"}
          mode={form.mode}
          camera={form.mode === "edit" ? form.camera : null}
          stores={stores}
          defaultStoreCode={storeCode}
          busy={formBusy}
          errorText={formError}
          onSubmit={(values) => void submitForm(values)}
          onClose={() => setForm(null)}
          overlay={
            secret.open ? (
              <CameraSecretPanel
                state={secret}
                onClose={() => {
                  // 地址与表单同时收起：地址状态先换成已清空常量。
                  setSecret(closeSecretReveal());
                  setForm(null);
                }}
              />
            ) : null
          }
        />
      ) : null}
      {capturesTarget ? (
        <CameraCapturesModal
          key={capturesTarget.cameraId}
          camera={capturesTarget}
          storeLabel={formatStoreLabel(capturesTarget.storeCode, storeNames)}
          onClose={() => setCapturesTarget(null)}
        />
      ) : null}
      {/* 重新生成令牌：没有表单 Modal 可叠，单独开一个原生 Modal。 */}
      {secret.open && !form ? <CameraSecretModal state={secret} onClose={() => setSecret(closeSecretReveal())} /> : null}
    </View>
  );
}

function CameraCard({
  camera,
  busy,
  onToggle,
  onEdit,
  onRotate,
  onViewCaptures,
}: {
  camera: FaceCamera;
  busy: boolean;
  onToggle: (enabled: boolean) => void;
  onEdit: () => void;
  onRotate: () => void;
  onViewCaptures: () => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const status = getCameraStatus(camera);
  const name = cameraDisplayName(camera);
  return (
    <View style={styles.card}>
      <View style={faceStyles.rowBetween}>
        <View style={styles.cardTitle}>
          <Text style={faceStyles.strong} numberOfLines={1}>
            {name}
          </Text>
          {name !== camera.cameraId ? <Text style={[faceStyles.muted, MONO]} numberOfLines={1}>{camera.cameraId}</Text> : null}
        </View>
        <Switch value={camera.enabled} disabled={busy} onValueChange={onToggle} accessibilityLabel={`${name} ${t("settings.cameras.enabled")}`} />
      </View>
      <View style={faceStyles.rowWrap}>
        <Tag label={t(CAMERA_STATUS_LABEL_KEYS[status])} tone={STATUS_TONES[status]} />
        {hasParseFailures(camera) ? <Tag tone="danger" icon="alert-outline" label={t("settings.cameras.parseFailures", { count: camera.parseFailureCount24h })} /> : null}
      </View>
      {status === "noRecentPush" ? <Text style={faceStyles.muted}>{t("settings.cameras.noRecentPushTip")}</Text> : null}
      <Text style={[faceStyles.muted, MONO]}>{t("settings.cameras.lastSeenAt", { time: formatLocalDateTime(camera.lastSeenAtUtc) })}</Text>
      <Text style={[faceStyles.muted, MONO]}>{t("settings.cameras.captures24h", { count: camera.captureCount24h })}</Text>
      <View style={styles.actions}>
        <Button compact mode="text" icon="pencil-outline" disabled={busy} onPress={onEdit}>
          {t("settings.cameras.edit")}
        </Button>
        <Button compact mode="text" icon="image-multiple-outline" disabled={busy} onPress={onViewCaptures}>
          {t("settings.cameras.viewCaptures")}
        </Button>
        <Button compact mode="text" icon="key-change" textColor={HB_COLORS.danger} disabled={busy} onPress={onRotate}>
          {t("settings.cameras.rotate")}
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    gap: 6,
    padding: HB_SPACING.sm,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.surface,
  },
  cardTitle: { flex: 1, gap: 2 },
  actions: { flexDirection: "row", flexWrap: "wrap", marginHorizontal: -HB_SPACING.xs },
  addButton: { alignSelf: "flex-start" },
  confirmText: { color: HB_COLORS.textSecondary },
});
