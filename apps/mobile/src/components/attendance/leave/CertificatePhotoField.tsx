// 医嘱照片字段：缩略图 + 拍照 / 相册入口，选完立即上传（签名直传），提交时只带已上传的地址。
// 权限与调用方式参考 face-match/PhotoPickerField；expo-image-picker 原生包已带，这里按需动态加载。
import { useCallback, useRef, useState } from "react";
import { ActivityIndicator, Image, Linking, Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, Text } from "react-native-paper";
import { uploadLeaveCertificatePhoto } from "@/modules/attendance/leave-api";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const TILE = 96;

export interface CertificatePhoto {
  key: string;
  uri: string;
  mimeType?: string;
  status: "uploading" | "uploaded" | "failed";
  url?: string;
}

/** 照片列表与上传状态；上传按张独立进行，个别失败可单独重试。 */
export function useCertificatePhotos() {
  const [photos, setPhotos] = useState<CertificatePhoto[]>([]);
  const counter = useRef(0);

  const upload = useCallback(async (photo: Pick<CertificatePhoto, "key" | "uri" | "mimeType">) => {
    const update = (patch: Partial<CertificatePhoto>) =>
      setPhotos((current) => current.map((item) => (item.key === photo.key ? { ...item, ...patch } : item)));
    try {
      const url = await uploadLeaveCertificatePhoto(photo.uri, photo.mimeType);
      update({ status: "uploaded", url });
    } catch {
      update({ status: "failed" });
    }
  }, []);

  const add = useCallback(
    (assets: { uri: string; mimeType?: string }[]) => {
      const created = assets.map((asset) => {
        counter.current += 1;
        return { key: `certificate-${Date.now()}-${counter.current}`, uri: asset.uri, mimeType: asset.mimeType, status: "uploading" as const };
      });
      setPhotos((current) => [...current, ...created]);
      created.forEach((photo) => void upload(photo));
    },
    [upload],
  );

  const remove = useCallback((key: string) => setPhotos((current) => current.filter((item) => item.key !== key)), []);

  const retry = useCallback(
    (key: string) => {
      const target = photos.find((item) => item.key === key);
      if (!target) return;
      setPhotos((current) => current.map((item) => (item.key === key ? { ...item, status: "uploading" } : item)));
      void upload(target);
    },
    [photos, upload],
  );

  return {
    photos,
    add,
    remove,
    retry,
    uploadedUrls: photos.filter((item) => item.status === "uploaded" && item.url).map((item) => item.url as string),
    isUploading: photos.some((item) => item.status === "uploading"),
    hasFailed: photos.some((item) => item.status === "failed"),
  };
}

export function CertificatePhotoField({
  photos,
  max,
  disabled,
  onAdd,
  onRemove,
  onRetry,
}: {
  photos: CertificatePhoto[];
  max: number;
  disabled?: boolean;
  onAdd: (assets: { uri: string; mimeType?: string }[]) => void;
  onRemove: (key: string) => void;
  onRetry: (key: string) => void;
}) {
  const { t } = useAppTranslation("attendanceLeave");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [blocked, setBlocked] = useState(false);
  const remaining = Math.max(0, max - photos.length);
  const locked = disabled || busy;

  const pick = async (source: "camera" | "library") => {
    if (locked || remaining <= 0) return;
    setMessage("");
    setBlocked(false);
    setBusy(true);
    try {
      const ImagePicker = await import("expo-image-picker");
      const permission = source === "camera"
        ? await ImagePicker.requestCameraPermissionsAsync()
        : await ImagePicker.requestMediaLibraryPermissionsAsync();
      if (!permission.granted) {
        setMessage(t(source === "camera" ? "photo.cameraPermission" : "photo.libraryPermission"));
        setBlocked(!permission.canAskAgain);
        return;
      }
      // 医嘱只需看清字：适度压缩、不要 EXIF（含定位）。
      const common = { mediaTypes: ["images" as const], allowsEditing: false, quality: 0.6, exif: false, base64: false };
      const result = source === "camera"
        ? await ImagePicker.launchCameraAsync(common)
        : await ImagePicker.launchImageLibraryAsync({
            ...common,
            allowsMultipleSelection: remaining > 1,
            selectionLimit: remaining,
            orderedSelection: true,
            // iOS：HEIC 在选取时转成 JPEG，签名接口只收常见图片类型。
            preferredAssetRepresentationMode: ImagePicker.UIImagePickerPreferredAssetRepresentationMode.Compatible,
          });
      if (result.canceled || result.assets.length === 0) return;
      if (result.assets.length > remaining) setMessage(t("photo.tooMany", { max }));
      onAdd(result.assets.slice(0, remaining).map((asset) => ({ uri: asset.uri, mimeType: asset.mimeType ?? undefined })));
    } catch {
      setMessage(t("photo.pickFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.field}>
      <View style={styles.grid}>
        {photos.map((photo, index) => (
          <View key={photo.key} style={styles.tile}>
            <Image
              source={{ uri: photo.uri }}
              style={styles.thumb}
              resizeMode="cover"
              accessibilityLabel={t("photo.thumb", { index: index + 1 })}
            />
            {photo.status === "uploading" ? (
              <View style={styles.overlay} accessibilityLabel={t("photo.uploading")}>
                <ActivityIndicator color={HB_COLORS.white} />
              </View>
            ) : null}
            {photo.status === "failed" ? (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("photo.failed")}
                disabled={disabled}
                onPress={() => onRetry(photo.key)}
                style={[styles.overlay, styles.failedOverlay]}
              >
                <MaterialCommunityIcons name="refresh" size={22} color={HB_COLORS.white} />
                <Text style={styles.failedText} numberOfLines={2}>{t("photo.failed")}</Text>
              </Pressable>
            ) : null}
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
            <AddTile icon="camera-outline" label={t("photo.camera")} disabled={locked} onPress={() => void pick("camera")} />
            <AddTile icon="image-outline" label={t("photo.library")} disabled={locked} loading={busy} onPress={() => void pick("library")} />
          </>
        ) : null}
      </View>
      {message ? <HelperText type="info" visible style={styles.helper}>{message}</HelperText> : null}
      {blocked ? (
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
  icon: "camera-outline" | "image-outline";
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
      style={({ pressed }) => [styles.tile, styles.addTile, pressed ? styles.pressed : null, disabled ? styles.disabled : null]}
    >
      {loading ? <ActivityIndicator size="small" color={HB_COLORS.textSecondary} /> : <MaterialCommunityIcons name={icon} size={26} color={HB_COLORS.textPrimary} />}
      <Text style={styles.addLabel} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  field: { gap: HB_SPACING.xxs },
  grid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  tile: { borderRadius: HB_RADIUS.control, height: TILE, overflow: "hidden", width: TILE },
  thumb: { backgroundColor: HB_COLORS.surfaceMuted, height: TILE, width: TILE },
  overlay: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    backgroundColor: "rgba(16,24,40,0.45)",
    gap: 2,
    justifyContent: "center",
    padding: HB_SPACING.xxs,
  },
  failedOverlay: { backgroundColor: "rgba(180,35,24,0.72)" },
  failedText: { color: HB_COLORS.white, fontSize: 11, textAlign: "center" },
  remove: {
    alignItems: "center",
    backgroundColor: "rgba(16,24,40,0.72)",
    borderRadius: 11,
    height: 22,
    justifyContent: "center",
    position: "absolute",
    right: 4,
    top: 4,
    width: 22,
  },
  addTile: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderStyle: "dashed",
    borderWidth: 1,
    gap: 4,
    justifyContent: "center",
  },
  addLabel: { color: HB_COLORS.textSecondary, fontSize: 12 },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  disabled: { opacity: 0.5 },
  helper: { paddingHorizontal: 0 },
  settings: { alignSelf: "flex-start" },
});
