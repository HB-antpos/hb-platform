// 新增 / 编辑摄像头的表单（全屏弹框）。字段规则与后端、网页后台一致（见 cameras-logic.ts）：
// - 新增：摄像头编号（创建后不可改）+ 所属门店（必填）+ 型号 / MAC 地址 / 备注（选填）；
// - 编辑：编号只读；PUT 是整体替换，启停状态沿用当前值（启停只走列表里的开关）。
// 提交由父组件执行并回传 busy / errorText；本组件不接触令牌（新增成功后的一次性地址由 CameraSecretDialog 展示）。
// 用原生 Modal 承载，所以选门店也用原生 Modal 版的门店选择器（Paper 的 Portal 会被这个 Modal 盖住）。
import { useMemo, useState, type ReactNode } from "react";
import { KeyboardAvoidingView, Modal, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, IconButton, Text, TextInput } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  CAMERA_DEVICE_MODEL_MAX_LENGTH,
  CAMERA_ID_MAX_LENGTH,
  CAMERA_MAC_ADDRESS_MAX_LENGTH,
  CAMERA_NOTE_MAX_LENGTH,
  EMPTY_CAMERA_FORM,
  isCameraFormChanged,
  toCameraFormValues,
  validateCameraForm,
  type CameraFormValues,
} from "./cameras-logic";
import type { FaceCamera } from "./types";
import { Notice } from "./ui";

interface Props {
  mode: "create" | "edit";
  /** 编辑时的摄像头；新增为 null。 */
  camera: FaceCamera | null;
  /** 可选门店（设置页已有的门店选项）。 */
  stores: readonly Store[];
  /** 新增时默认选中的门店（设置页当前所选门店）。 */
  defaultStoreCode: string;
  busy: boolean;
  /** 提交失败的说明（来自服务端错误码映射）。 */
  errorText: string | null;
  onSubmit: (values: CameraFormValues) => void;
  onClose: () => void;
  /** 叠在表单上的覆盖层（新增成功后的一次性地址）。有覆盖层时表单不响应返回键，也不可操作。 */
  overlay?: ReactNode;
}

export function CameraFormModal({ mode, camera, stores, defaultStoreCode, busy, errorText, onSubmit, onClose, overlay }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const insets = useSafeAreaInsets();
  const initial = useMemo<CameraFormValues>(
    () => (camera ? toCameraFormValues(camera) : { ...EMPTY_CAMERA_FORM, storeCode: defaultStoreCode }),
    [camera, defaultStoreCode],
  );
  const [values, setValues] = useState<CameraFormValues>(initial);
  const [showErrors, setShowErrors] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);

  const errors = validateCameraForm(values, mode);
  const changed = mode === "create" || isCameraFormChanged(initial, values);
  const patch = (next: Partial<CameraFormValues>) => setValues((current) => ({ ...current, ...next }));
  const selectedStore = stores.find((store) => store.storeCode === values.storeCode) ?? null;
  const storeLabel = selectedStore ? selectedStore.storeName || selectedStore.storeCode : values.storeCode;

  const submit = () => {
    if (Object.keys(errors).length > 0) {
      setShowErrors(true);
      return;
    }
    onSubmit(values);
  };
  const errorText_ = (key: keyof CameraFormValues) => (showErrors && errors[key] ? t(errors[key] as string) : undefined);

  return (
    <Modal visible animationType="slide" presentationStyle="fullScreen" statusBarTranslucent onRequestClose={() => (busy || overlay ? undefined : onClose())}>
      <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined} style={styles.flex}>
        <View style={[styles.root, { paddingTop: insets.top }]}>
          <View style={styles.header}>
            <IconButton icon="close" accessibilityLabel={t("common:actions.close")} disabled={busy} onPress={onClose} />
            <Text style={styles.headerTitle} numberOfLines={1}>
              {mode === "create" ? t("settings.cameras.form.titleCreate") : t("settings.cameras.form.titleEdit")}
            </Text>
            <View style={styles.headerSpacer} />
          </View>

          <ScrollView contentContainerStyle={[styles.body, { paddingBottom: insets.bottom + HB_SPACING.lg }]} keyboardShouldPersistTaps="handled">
            {errorText ? <Notice tone="danger" text={errorText} /> : null}

            <View>
              <TextInput
                mode="outlined"
                label={`${t("settings.cameras.form.cameraId")}${mode === "create" ? " *" : ""}`}
                value={values.cameraId}
                onChangeText={(cameraId) => patch({ cameraId: cameraId.toLowerCase() })}
                placeholder={t("settings.cameras.form.cameraIdPlaceholder")}
                maxLength={CAMERA_ID_MAX_LENGTH}
                autoCapitalize="none"
                autoCorrect={false}
                editable={mode === "create" && !busy}
                disabled={mode === "edit"}
                error={Boolean(errorText_("cameraId"))}
              />
              <HelperText type={errorText_("cameraId") ? "error" : "info"} visible>
                {errorText_("cameraId") ?? t("settings.cameras.form.cameraIdHint")}
              </HelperText>
            </View>

            <View>
              <Text style={styles.fieldLabel}>{`${t("settings.cameras.form.storeCode")} *`}</Text>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("settings.cameras.form.pickStore")}
                disabled={busy}
                onPress={() => setPickerOpen(true)}
                style={[styles.select, errorText_("storeCode") ? styles.selectError : null]}
              >
                <Text style={[styles.selectText, !storeLabel ? styles.selectPlaceholder : null]} numberOfLines={1}>
                  {storeLabel || t("settings.cameras.form.pickStore")}
                </Text>
                <MaterialCommunityIcons name="chevron-down" size={20} color={HB_COLORS.textSecondary} />
              </Pressable>
              {errorText_("storeCode") ? <HelperText type="error" visible>{errorText_("storeCode")}</HelperText> : null}
            </View>

            <View>
              <TextInput
                mode="outlined"
                label={t("settings.cameras.form.deviceModel")}
                value={values.deviceModel}
                onChangeText={(deviceModel) => patch({ deviceModel })}
                maxLength={CAMERA_DEVICE_MODEL_MAX_LENGTH}
                editable={!busy}
                error={Boolean(errorText_("deviceModel"))}
              />
              {errorText_("deviceModel") ? <HelperText type="error" visible>{errorText_("deviceModel")}</HelperText> : null}
            </View>

            <View>
              <TextInput
                mode="outlined"
                label={t("settings.cameras.form.macAddress")}
                value={values.macAddress}
                onChangeText={(macAddress) => patch({ macAddress })}
                maxLength={CAMERA_MAC_ADDRESS_MAX_LENGTH}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!busy}
                error={Boolean(errorText_("macAddress"))}
              />
              {errorText_("macAddress") ? <HelperText type="error" visible>{errorText_("macAddress")}</HelperText> : null}
            </View>

            <View>
              <TextInput
                mode="outlined"
                label={t("settings.cameras.form.note")}
                value={values.note}
                onChangeText={(note) => patch({ note })}
                maxLength={CAMERA_NOTE_MAX_LENGTH}
                editable={!busy}
                error={Boolean(errorText_("note"))}
              />
              {errorText_("note") ? <HelperText type="error" visible>{errorText_("note")}</HelperText> : null}
            </View>

            <Button mode="contained" loading={busy} disabled={busy || !changed} onPress={submit}>
              {mode === "create" ? t("settings.cameras.form.submitCreate") : t("settings.cameras.form.submitSave")}
            </Button>
          </ScrollView>
        </View>
      </KeyboardAvoidingView>

      {overlay}

      <StorePickerModal
        host="native-modal"
        presentation="sheet"
        visible={pickerOpen}
        stores={[...stores]}
        selectedStoreCode={values.storeCode || null}
        title={t("settings.cameras.form.pickStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerOpen(false)}
        onSelectStore={(store) => {
          if (store) patch({ storeCode: store.storeCode });
          setPickerOpen(false);
        }}
      />
    </Modal>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  root: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.xs },
  headerTitle: { flex: 1, fontSize: 17, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  body: { gap: HB_SPACING.xs, padding: HB_SPACING.md },
  fieldLabel: { marginBottom: 4, fontSize: 12, color: HB_COLORS.textSecondary },
  select: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.surface,
  },
  selectError: { borderColor: HB_COLORS.danger },
  selectText: { flex: 1, fontSize: 15, color: HB_COLORS.textPrimary },
  selectPlaceholder: { color: HB_COLORS.textSecondary },
});
