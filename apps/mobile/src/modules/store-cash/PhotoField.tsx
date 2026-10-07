// 单据照片字段：缩略图网格（上传状态、就地重试、删除、点开预览）+ 拍照 / 相册多选入口。
// 照片先压缩成长边 ≤ 2048 的 JPEG（≤ 5 MiB）再进入列表，真正上传在提交时统一进行（见 photo-upload.ts）。
import { useState } from "react";
import { Image, Linking, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, HelperText, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { CashCameraModal, type CapturedPhotoSource } from "./CashCameraModal";
import { ImageViewerModal } from "./ImageViewerModal";
import { CashImageProcessingError, processCashPhoto, type CashImageSource } from "./image-processing";
import { createPhotoDraft, createPhotoKey, remainingPhotoSlots, type PhotoDraft } from "./photo-drafts";
import { CASH_SOFT } from "./ui";

const TILE = 84;

export function PhotoField({
  label,
  hint,
  required,
  photos,
  maxCount,
  disabled,
  error,
  onAppend,
  onRemove,
  onRetry,
}: {
  label: string;
  hint?: string;
  required?: boolean;
  photos: PhotoDraft[];
  maxCount: number;
  disabled?: boolean;
  error?: string;
  onAppend: (photos: PhotoDraft[]) => void;
  onRemove: (key: string) => void;
  /** 单张上传失败后就地重试；只会重传这一张，已成功的不重复上传。 */
  onRetry: (key: string) => void;
}) {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [libraryBlocked, setLibraryBlocked] = useState(false);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const remaining = remainingPhotoSlots(photos.length, maxCount);
  const locked = disabled || busy;

  const describeProcessingError = (cause: unknown) => {
    const code = cause instanceof CashImageProcessingError ? cause.code : "decode_failed";
    return t(`photo.errors.${code}`);
  };

  /** 压缩一批来源图并加入列表；个别失败不影响其余，最后统一提示。 */
  const processAndAppend = async (sources: CashImageSource[]) => {
    const drafts: PhotoDraft[] = [];
    let failure = "";
    for (const source of sources) {
      try {
        drafts.push(createPhotoDraft(createPhotoKey(), await processCashPhoto(source)));
      } catch (cause) {
        failure = describeProcessingError(cause);
      }
    }
    if (drafts.length > 0) onAppend(drafts);
    return { added: drafts.length, failure };
  };

  const chooseFromLibrary = async () => {
    if (locked || remaining <= 0) return;
    setMessage("");
    setLibraryBlocked(false);
    setBusy(true);
    try {
      const ImagePicker = await import("expo-image-picker");
      const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
      if (!permission.granted) {
        setMessage(t("photo.errors.libraryPermission"));
        setLibraryBlocked(!permission.canAskAgain);
        return;
      }
      const result = await ImagePicker.launchImageLibraryAsync({
        mediaTypes: ["images"],
        allowsMultipleSelection: remaining > 1,
        selectionLimit: remaining,
        orderedSelection: true,
        allowsEditing: false,
        quality: 1,
      });
      if (result.canceled || result.assets.length === 0) return;
      const picked = result.assets.slice(0, remaining);
      const { failure } = await processAndAppend(
        picked.map((asset) => ({ uri: asset.uri, width: asset.width, height: asset.height })),
      );
      if (failure) setMessage(failure);
      else if (result.assets.length > remaining) setMessage(t("photo.limitReached", { max: maxCount }));
    } catch {
      setMessage(t("photo.errors.decode_failed"));
    } finally {
      setBusy(false);
    }
  };

  const handleCaptured = async (source: CapturedPhotoSource) => {
    const { added, failure } = await processAndAppend([source]);
    if (added === 0) throw new Error(failure || "capture failed");
  };

  return (
    <View style={styles.field}>
      <View style={styles.labelRow}>
        <Text style={styles.label}>
          {label}
          {required ? <Text style={styles.required}>{" *"}</Text> : null}
        </Text>
        <Text style={styles.count}>{t("photo.count", { count: photos.length, max: maxCount })}</Text>
      </View>
      {hint ? <Text style={styles.hint}>{hint}</Text> : null}

      <View style={styles.grid}>
        {photos.map((photo, index) => (
          <PhotoTile
            key={photo.key}
            photo={photo}
            disabled={disabled}
            onPreview={() => setViewerIndex(index)}
            onRemove={() => onRemove(photo.key)}
            onRetry={() => onRetry(photo.key)}
          />
        ))}
        {remaining > 0 ? (
          <>
            <AddTile
              icon="camera-outline"
              label={t("photo.add.camera")}
              disabled={locked}
              onPress={() => {
                setMessage("");
                setCameraVisible(true);
              }}
            />
            <AddTile
              icon="image-multiple-outline"
              label={t("photo.add.library")}
              disabled={locked}
              loading={busy}
              onPress={() => void chooseFromLibrary()}
            />
          </>
        ) : null}
      </View>

      {error ? <HelperText type="error" visible>{error}</HelperText> : null}
      {message ? <HelperText type="info" visible>{message}</HelperText> : null}
      {libraryBlocked ? (
        <Button mode="text" icon="cog" compact onPress={() => void Linking.openSettings()} style={styles.settingsButton}>
          {t("camera.openSettings")}
        </Button>
      ) : null}

      <CashCameraModal
        visible={cameraVisible}
        maxCount={maxCount}
        currentCount={photos.length}
        onDismiss={() => setCameraVisible(false)}
        onCaptured={handleCaptured}
      />
      <ImageViewerModal
        visible={viewerIndex !== null}
        images={photos.map((photo) => ({ key: photo.key, uri: photo.uri }))}
        initialIndex={viewerIndex ?? 0}
        onClose={() => setViewerIndex(null)}
      />
    </View>
  );
}

function AddTile({
  icon,
  label,
  disabled,
  loading,
  onPress,
}: {
  icon: keyof typeof MaterialCommunityIcons.glyphMap;
  label: string;
  disabled?: boolean;
  loading?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.tile, styles.addTile, disabled ? styles.disabled : null]}
    >
      {loading ? (
        <ActivityIndicator size="small" />
      ) : (
        <MaterialCommunityIcons name={icon} size={26} color={HB_COLORS.action} />
      )}
      <Text style={styles.addLabel}>{label}</Text>
    </Pressable>
  );
}

function PhotoTile({
  photo,
  disabled,
  onPreview,
  onRemove,
  onRetry,
}: {
  photo: PhotoDraft;
  disabled?: boolean;
  onPreview: () => void;
  onRemove: () => void;
  onRetry: () => void;
}) {
  const { t } = useAppTranslation("storeCash");
  const uploading = photo.status === "uploading";
  const failed = photo.status === "failed";
  const uploaded = photo.status === "uploaded";
  return (
    <View style={styles.tile}>
      <Pressable
        accessibilityRole="imagebutton"
        accessibilityLabel={t("photo.preview")}
        onPress={onPreview}
        style={styles.tileFill}
      >
        <Image source={{ uri: photo.uri }} style={styles.thumb} resizeMode="cover" />
      </Pressable>
      {uploading ? (
        <View style={styles.overlay} pointerEvents="none">
          <ActivityIndicator color="#FFFFFF" />
          <Text style={styles.overlayText}>{t("photo.status.uploading")}</Text>
        </View>
      ) : null}
      {failed ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("photo.status.failed")}
          onPress={onRetry}
          disabled={disabled}
          style={[styles.overlay, styles.overlayFailed]}
        >
          <MaterialCommunityIcons name="refresh" size={24} color="#FFFFFF" />
          <Text style={styles.overlayText}>{t("photo.status.retry")}</Text>
        </Pressable>
      ) : null}
      {uploaded ? (
        <View style={styles.doneBadge} pointerEvents="none">
          <MaterialCommunityIcons name="check" size={14} color="#FFFFFF" />
        </View>
      ) : null}
      {!uploading ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("photo.remove")}
          disabled={disabled}
          onPress={onRemove}
          hitSlop={8}
          style={styles.removeButton}
        >
          <MaterialCommunityIcons name="close" size={16} color="#FFFFFF" />
        </Pressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  field: { gap: HB_SPACING.xs },
  labelRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  label: { color: HB_COLORS.textPrimary, fontWeight: "600", fontSize: 14, flexShrink: 1 },
  required: { color: HB_COLORS.danger },
  count: { color: HB_COLORS.textSecondary, fontSize: 12, fontVariant: ["tabular-nums"] },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  grid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  tile: { width: TILE, height: TILE, borderRadius: HB_RADIUS.control },
  tileFill: { width: TILE, height: TILE, borderRadius: HB_RADIUS.control, overflow: "hidden" },
  thumb: { width: TILE, height: TILE, backgroundColor: HB_COLORS.surfaceMuted },
  addTile: {
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: HB_COLORS.action,
    backgroundColor: CASH_SOFT.accent,
    alignItems: "center",
    justifyContent: "center",
    gap: 2,
  },
  addLabel: { color: HB_COLORS.action, fontSize: 12, fontWeight: "600" },
  disabled: { opacity: 0.5 },
  overlay: {
    ...StyleSheet.absoluteFillObject,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "rgba(16,24,40,0.55)",
    alignItems: "center",
    justifyContent: "center",
    gap: 2,
  },
  overlayFailed: { backgroundColor: "rgba(180,35,24,0.85)" },
  overlayText: { color: "#FFFFFF", fontSize: 12, fontWeight: "600" },
  doneBadge: {
    position: "absolute",
    left: 4,
    bottom: 4,
    width: 20,
    height: 20,
    borderRadius: 10,
    backgroundColor: HB_COLORS.success,
    alignItems: "center",
    justifyContent: "center",
  },
  removeButton: {
    // 放在缩略图内部角落：Android 上超出父视图边界的区域接收不到触摸
    position: "absolute",
    top: 3,
    right: 3,
    width: 26,
    height: 26,
    borderRadius: 13,
    backgroundColor: "rgba(16,24,40,0.8)",
    alignItems: "center",
    justifyContent: "center",
  },
  settingsButton: { alignSelf: "flex-start" },
});
