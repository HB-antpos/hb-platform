// 「摄像头」区（需要 FaceCapture.Cameras.Manage）：所选门店的摄像头列表，可启用 / 停用。
// 不做：新建摄像头、重置令牌、查看或修改令牌（安全原因，只能在网页后台做）。
// 摄像机没有心跳：状态写「近期有推送 / 暂无推送」，不写在线 / 离线。
import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Dialog, Portal, Switch, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { faceMatchApi } from "./api";
import {
  CAMERA_STATUS_LABEL_KEYS,
  buildCameraEnabledRequest,
  cameraDisplayName,
  countCameraStatuses,
  filterCamerasByStore,
  getCameraStatus,
  hasParseFailures,
  type CameraStatus,
} from "./cameras-logic";
import { formatLocalDateTime } from "./dates";
import { useFaceErrorMessage } from "./shared";
import type { FaceCamera, FaceCameraList } from "./types";
import { faceMatchKeys, useFaceCameras } from "./use-face-match";
import { FaceCard, MONO, Notice, SectionTitle, Tag, faceStyles, type FaceTone } from "./ui";

const STATUS_TONES: Record<CameraStatus, FaceTone> = {
  recentlyActive: "success",
  noRecentPush: "neutral",
  disabled: "neutral",
};

export function CamerasSection({
  storeCode,
  onNotify,
}: {
  storeCode: string;
  onNotify: (message: string) => void;
}) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const camerasQuery = useFaceCameras(true);
  const [pendingDisable, setPendingDisable] = useState<FaceCamera | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: ({ camera, enabled }: { camera: FaceCamera; enabled: boolean }) =>
      faceMatchApi.updateCamera(camera.cameraId, buildCameraEnabledRequest(camera, enabled)),
    onMutate: ({ camera }) => {
      setBusyId(camera.cameraId);
      setActionError(null);
    },
    onSuccess: (updated, { enabled }) => {
      setPendingDisable(null);
      // 就地替换这一台，列表其余数据保持不动。
      queryClient.setQueryData<FaceCameraList>(faceMatchKeys.cameras(userGuid), (current) =>
        current ? { ...current, items: current.items.map((item) => (item.cameraId === updated.cameraId ? updated : item)) } : current,
      );
      onNotify(t(enabled ? "settings.cameras.enabledDone" : "settings.cameras.disabledDone"));
    },
    onError: (error) => {
      setPendingDisable(null);
      setActionError(describeError(error));
      void queryClient.invalidateQueries({ queryKey: faceMatchKeys.cameras(userGuid) });
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
              <CameraCard key={camera.cameraId} camera={camera} busy={busyId === camera.cameraId} onToggle={(enabled) => toggle(camera, enabled)} />
            ))}
          </>
        )}
        <Text style={faceStyles.muted}>{t("settings.cameras.webOnly")}</Text>
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
      </Portal>
    </View>
  );
}

function CameraCard({ camera, busy, onToggle }: { camera: FaceCamera; busy: boolean; onToggle: (enabled: boolean) => void }) {
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
  confirmText: { color: HB_COLORS.textSecondary },
});
