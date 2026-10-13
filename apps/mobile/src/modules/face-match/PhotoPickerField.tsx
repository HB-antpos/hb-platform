// 参考照片字段：缩略图网格 + 相机 / 相册入口（1–5 张）。
// 照片只存在于内存：选图器 / 相机留下的临时文件在压缩后立即删除，字段里只保留 base64 缩略图与压缩后的 JPEG 字节，
// 不写磁盘缓存、不进日志。选图后当场校验类型（JPG/PNG，iOS 的 HEIC 自动转 JPEG）与大小（≤5MB），不合规当场提示。
import { useState } from "react";
import { Image, Linking, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, HelperText, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { UNWELCOME_PHOTO_MAX } from "./constants";
import { FacePhotoError, remainingPhotoSlots, type PickedPhotoAsset } from "./photo-processing";
import { discardPickedAsset, processPhotoForUpload } from "./photo-runtime";
import type { FacePhotoDraft } from "./types";

const TILE = 84;

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

export function PhotoPickerField({
  photos,
  disabled,
  error,
  onAppend,
  onRemove,
}: {
  photos: FacePhotoDraft[];
  disabled?: boolean;
  error?: string;
  onAppend: (photos: FacePhotoDraft[]) => void;
  onRemove: (key: string) => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [blockedKind, setBlockedKind] = useState<"camera" | "library" | null>(null);
  const remaining = remainingPhotoSlots(photos.length, UNWELCOME_PHOTO_MAX);
  const locked = disabled || busy;

  /** 压缩一批选中资源并加入列表；个别失败不影响其余，最后统一提示第一条原因。 */
  const processAssets = async (assets: PickedPhotoAsset[]) => {
    const accepted: FacePhotoDraft[] = [];
    let failure = "";
    for (const asset of assets) {
      if (accepted.length >= remaining) {
        // 超出张数上限的资源不用，但选图器留下的临时文件要删。
        await discardPickedAsset(asset);
        failure = failure || t("photo.errors.tooMany", { max: UNWELCOME_PHOTO_MAX });
        continue;
      }
      try {
        accepted.push(await processPhotoForUpload(asset));
      } catch (cause) {
        const code = cause instanceof FacePhotoError ? cause.code : "decodeFailed";
        failure = failure || t(`photo.errors.${code}`);
      }
    }
    if (accepted.length > 0) onAppend(accepted);
    setMessage(failure);
  };

  const pick = async (source: "camera" | "library") => {
    if (locked || remaining <= 0) return;
    setMessage("");
    setBlockedKind(null);
    setBusy(true);
    try {
      const ImagePicker = await import("expo-image-picker");
      const permission =
        source === "camera"
          ? await ImagePicker.requestCameraPermissionsAsync()
          : await ImagePicker.requestMediaLibraryPermissionsAsync();
      if (!permission.granted) {
        setMessage(t(source === "camera" ? "photo.errors.cameraPermission" : "photo.errors.libraryPermission"));
        setBlockedKind(permission.canAskAgain ? null : source);
        return;
      }
      const common = {
        mediaTypes: ["images" as const],
        allowsEditing: false,
        quality: 1,
        // 不要 EXIF（含定位）、不要选图器自己编码 base64（压缩流程会重新编码并带回 base64）。
        exif: false,
        base64: false,
      };
      const result =
        source === "camera"
          ? await ImagePicker.launchCameraAsync(common)
          : await ImagePicker.launchImageLibraryAsync({
              ...common,
              allowsMultipleSelection: remaining > 1,
              selectionLimit: remaining,
              orderedSelection: true,
              // iOS：HEIC 在选取时就转成兼容格式（JPEG），后面的压缩流程再统一输出 JPEG。
              preferredAssetRepresentationMode: ImagePicker.UIImagePickerPreferredAssetRepresentationMode.Compatible,
            });
      if (result.canceled || result.assets.length === 0) return;
      await processAssets(
        result.assets.map((asset) => ({
          uri: asset.uri,
          width: asset.width,
          height: asset.height,
          mimeType: asset.mimeType,
          fileName: asset.fileName,
          fileSize: asset.fileSize,
        })),
      );
    } catch {
      setMessage(t("photo.errors.decodeFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.field}>
      <View style={styles.labelRow}>
        <Text style={styles.label}>
          {t("form.photos")}
          <Text style={styles.required}>{" *"}</Text>
        </Text>
        <Text style={styles.count}>{t("photo.count", { count: photos.length, max: UNWELCOME_PHOTO_MAX })}</Text>
      </View>
      <Text style={styles.hint}>{t("form.photosHint")}</Text>
      <View style={styles.grid}>
        {photos.map((photo, index) => (
          <View key={photo.key} style={styles.tile}>
            <Image
              source={{ uri: photo.previewDataUri }}
              style={styles.thumb}
              resizeMode="cover"
              accessibilityLabel={t("photo.thumbLabel", { index: index + 1 })}
            />
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("photo.remove", { index: index + 1 })}
              disabled={disabled}
              onPress={() => onRemove(photo.key)}
              style={styles.remove}
              hitSlop={6}
            >
              <MaterialCommunityIcons name="close" size={16} color={HB_COLORS.white} />
            </Pressable>
          </View>
        ))}
        {remaining > 0 ? (
          <>
            <AddTile icon="camera-outline" label={t("photo.add.camera")} disabled={locked} onPress={() => void pick("camera")} />
            <AddTile icon="image-multiple-outline" label={t("photo.add.library")} disabled={locked} loading={busy} onPress={() => void pick("library")} />
          </>
        ) : null}
      </View>
      {error ? <HelperText type="error" visible>{error}</HelperText> : null}
      {message ? <HelperText type="info" visible>{message}</HelperText> : null}
      {blockedKind ? (
        <Button mode="text" icon="cog" compact onPress={() => void Linking.openSettings()} style={styles.settings}>
          {t("photo.openSettings")}
        </Button>
      ) : null}
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
  icon: IconName;
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
      {loading ? <ActivityIndicator size="small" color={HB_COLORS.action} /> : <MaterialCommunityIcons name={icon} size={26} color={HB_COLORS.action} />}
      <Text style={styles.addLabel} numberOfLines={1}>
        {label}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  field: { gap: 6 },
  labelRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  label: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  required: { color: HB_COLORS.danger },
  count: { fontSize: 12, color: HB_COLORS.textSecondary },
  hint: { fontSize: 12, color: HB_COLORS.textSecondary },
  grid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  tile: { width: TILE, height: TILE, borderRadius: HB_RADIUS.control, overflow: "hidden" },
  thumb: { width: TILE, height: TILE, backgroundColor: HB_COLORS.surfaceMuted },
  remove: {
    position: "absolute",
    top: 4,
    right: 4,
    width: 22,
    height: 22,
    borderRadius: 11,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(16,24,40,0.72)",
  },
  addTile: {
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  addLabel: { fontSize: 11, color: HB_COLORS.action },
  disabled: { opacity: 0.5 },
  settings: { alignSelf: "flex-start" },
});
