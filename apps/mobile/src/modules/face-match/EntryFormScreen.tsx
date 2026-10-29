// 提交不受欢迎人员名单条目。硬条件（前端先校验，后端强制）：
// 事件类别是枚举（没有「一般偷窃」）；Other 必须说明；证据编号必填（不要填姓名或隐私细节）；至少一家生效门店；
// 到期日必填且最晚今天起 12 个月；参考照片 1–5 张（JPG/PNG，≤5MB，只用于复核时并排比对）。
// 批准人数等规则（管理员一个人即可、非管理员须提交人≠批准人、Other 需两位）全部由服务端强制，这里只如实告知。
import { useMemo, useRef, useState } from "react";
import { Alert, KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useMutation } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { Button, Dialog, HelperText, Portal, Text, TextInput } from "react-native-paper";
import { MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { sortStoresByName } from "@/shared/utils/name-sort";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canSubmitEntries } from "./access";
import { faceMatchApi } from "./api";
import { CATEGORY_NOTE_MAX_LENGTH, CATEGORY_OTHER, EVIDENCE_REF_MAX_LENGTH, UNWELCOME_MAX_EXPIRY_MONTHS } from "./constants";
import { addMonthsYmd, maxExpiryDay, minExpiryDay, todayYmd } from "./dates";
import { INCIDENT_CATEGORY_OPTIONS } from "./entries-logic";
import {
  buildCreateEntryInput,
  createEmptyEntryForm,
  hasEntryFormErrors,
  isEntryFormDirty,
  validateEntryForm,
  type EntryFormValues,
} from "./entry-form";
import { extractHttpStatus } from "./errors";
import { PhotoPickerField } from "./PhotoPickerField";
import { formatStoreLabel, useFaceErrorMessage, useFaceUnwelcomeGuard, useIssueText, useStoreNameMap } from "./shared";
import type { FacePhotoDraft } from "./types";
import { useFaceMatchConfig, useFaceStoreOptions, useInvalidateUnwelcome } from "./use-face-match";
import { useLeaveGuard } from "./use-leave-guard";
import { FaceScreenFrame, FaceScreenMessage, FilterChip, Notice, SectionTitle } from "./ui";

export function EntryFormScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceUnwelcomeGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/face-unwelcome"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  return <EntryFormContent key={userGuid} onBack={goBack} />;
}

function EntryFormContent({ onBack }: { onBack: () => void }) {
  const { t, language } = useAppTranslation(["faceMatch", "common"]);
  const router = useRouter();
  const describeError = useFaceErrorMessage();
  const issueText = useIssueText();
  const invalidate = useInvalidateUnwelcome();
  const configQuery = useFaceMatchConfig(true);
  const { stores, isLoading: storesLoading } = useFaceStoreOptions();
  const storeNames = useStoreNameMap(stores);
  // 适用分店多选芯片统一按分店名字排序。
  const sortedStores = useMemo(() => sortStoresByName(stores, language), [stores, language]);
  const [values, setValues] = useState<EntryFormValues>(createEmptyEntryForm);
  const [photos, setPhotos] = useState<FacePhotoDraft[]>([]);
  const [showErrors, setShowErrors] = useState(false);
  const [dateOpen, setDateOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const allowLeaveRef = useRef(false);

  const config = configQuery.data;
  const now = useMemo(() => new Date(), []);
  const errors = validateEntryForm(values, photos, now);
  const show = (field: keyof typeof errors) => (showErrors ? issueText(errors[field]) : undefined);
  const patch = (next: Partial<EntryFormValues>) => setValues((current) => ({ ...current, ...next }));
  const minDate = minExpiryDay(now);
  const maxDate = maxExpiryDay(now);

  const mutation = useMutation({
    mutationFn: () => faceMatchApi.createEntry(buildCreateEntryInput(values, photos, new Date())),
    onSuccess: async () => {
      allowLeaveRef.current = true;
      setConfirmOpen(false);
      setPhotos([]);
      await invalidate();
      Alert.alert(t("form.submittedTitle"), t("form.submittedMessage"), [{ text: t("common:actions.confirm"), onPress: () => router.back() }], {
        onDismiss: () => router.back(),
      });
    },
    onError: (error) => {
      setConfirmOpen(false);
      const text = describeError(error);
      // 没有 HTTP 响应（超时 / 断网）时服务端可能已经收到并创建了条目，提醒先到列表确认，避免重复提交。
      setSubmitError(extractHttpStatus(error) === null ? `${text} ${t("form.uncertainNote")}` : text);
    },
  });
  const submitting = mutation.isPending;

  useLeaveGuard({ dirty: isEntryFormDirty(values, photos.length), busy: submitting, allowLeaveRef });

  const submitBlockedReason = !config
    ? null
    : !canSubmitEntries(config)
      ? config.objectStoreConfigured
        ? t("unwelcome.writeBlocked")
        : t("unwelcome.objectStoreMissing")
      : null;

  const requestSubmit = () => {
    setShowErrors(true);
    setSubmitError(null);
    if (hasEntryFormErrors(errors) || submitBlockedReason) return;
    setConfirmOpen(true);
  };

  const toggleStore = (code: string) =>
    patch({ storeScope: values.storeScope.includes(code) ? values.storeScope.filter((item) => item !== code) : [...values.storeScope, code] });

  return (
    <FaceScreenFrame title={t("form.title")} onBack={onBack}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled" keyboardDismissMode="on-drag">
          <Notice tone="warning" text={t("form.scopeNotice")} />
          {submitBlockedReason ? <Notice tone="warning" text={submitBlockedReason} /> : null}

          <SectionTitle flush title={t("form.category")} />
          <View style={styles.group}>
            {INCIDENT_CATEGORY_OPTIONS.map((option) => {
              const selected = values.incidentCategory === option.value;
              return (
                <Pressable
                  key={option.value}
                  accessibilityRole="radio"
                  accessibilityState={{ selected, disabled: submitting }}
                  disabled={submitting}
                  onPress={() => patch({ incidentCategory: option.value })}
                  style={[styles.radio, selected ? styles.radioOn : null]}
                >
                  <MaterialCommunityIcons name={selected ? "radiobox-marked" : "radiobox-blank"} size={22} color={selected ? HB_COLORS.action : HB_COLORS.textSecondary} />
                  <Text style={styles.radioText}>{t(option.labelKey)}</Text>
                </Pressable>
              );
            })}
            {show("incidentCategory") ? <HelperText type="error" visible>{show("incidentCategory")}</HelperText> : null}
          </View>

          {values.incidentCategory === CATEGORY_OTHER ? (
            <View style={styles.group}>
              <Notice text={t("form.otherNeedsApproval")} />
              <TextInput
                mode="outlined"
                label={`${t("form.categoryNote")} *`}
                value={values.categoryNote}
                onChangeText={(categoryNote) => patch({ categoryNote })}
                multiline
                numberOfLines={3}
                maxLength={CATEGORY_NOTE_MAX_LENGTH}
                error={Boolean(show("categoryNote"))}
                disabled={submitting}
                style={styles.input}
              />
              <HelperText type={show("categoryNote") ? "error" : "info"} visible>
                {show("categoryNote") ?? `${values.categoryNote.length} / ${CATEGORY_NOTE_MAX_LENGTH}`}
              </HelperText>
            </View>
          ) : null}

          <View style={styles.group}>
            <TextInput
              mode="outlined"
              label={`${t("form.evidenceRef")} *`}
              value={values.evidenceRef}
              onChangeText={(evidenceRef) => patch({ evidenceRef })}
              maxLength={EVIDENCE_REF_MAX_LENGTH}
              autoCapitalize="characters"
              autoCorrect={false}
              error={Boolean(show("evidenceRef"))}
              disabled={submitting}
              style={styles.input}
            />
            <HelperText type={show("evidenceRef") ? "error" : "info"} visible>
              {show("evidenceRef") ?? t("form.evidenceRefHint")}
            </HelperText>
          </View>

          <SectionTitle flush title={`${t("form.storeScope")} *`} hint={t("form.storeScopeHint", { count: values.storeScope.length })} />
          <View style={styles.group}>
            {stores.length === 0 ? (
              <Text style={styles.muted}>{storesLoading ? t("states.loading") : t("form.noStores")}</Text>
            ) : (
              <View style={styles.chips}>
                {sortedStores.map((store) => (
                  <FilterChip
                    key={store.storeCode}
                    label={formatStoreLabel(store.storeCode, storeNames)}
                    selected={values.storeScope.includes(store.storeCode)}
                    disabled={submitting}
                    onPress={() => toggleStore(store.storeCode)}
                  />
                ))}
              </View>
            )}
            {show("storeScope") ? <HelperText type="error" visible>{show("storeScope")}</HelperText> : null}
          </View>

          <SectionTitle flush title={`${t("form.expiresOn")} *`} hint={t("form.expiresHint", { months: UNWELCOME_MAX_EXPIRY_MONTHS, latest: maxDate })} />
          <View style={styles.group}>
            <View style={styles.chips}>
              {[3, 6, 12].map((months) => {
                const target = addMonthsYmd(todayYmd(now), months);
                return (
                  <FilterChip
                    key={months}
                    label={t("form.expiresPreset", { months })}
                    selected={values.expiresOn === target}
                    disabled={submitting}
                    onPress={() => patch({ expiresOn: target > maxDate ? maxDate : target })}
                  />
                );
              })}
            </View>
            <Pressable
              accessibilityRole="button"
              accessibilityState={{ expanded: dateOpen, disabled: submitting }}
              disabled={submitting}
              onPress={() => setDateOpen((open) => !open)}
              style={[styles.dateField, show("expiresOn") ? styles.dateFieldError : null]}
            >
              <Text style={values.expiresOn ? styles.dateValue : styles.datePlaceholder}>{values.expiresOn || t("form.expiresPlaceholder")}</Text>
              <MaterialCommunityIcons name="calendar-month-outline" size={20} color={HB_COLORS.textSecondary} />
            </Pressable>
            {dateOpen ? (
              <MonthDatePicker
                allowEmpty
                value={values.expiresOn}
                minDate={minDate}
                maxDate={maxDate}
                disabled={submitting}
                onChange={(expiresOn) => {
                  patch({ expiresOn });
                  setDateOpen(false);
                }}
              />
            ) : null}
            {show("expiresOn") ? <HelperText type="error" visible>{show("expiresOn")}</HelperText> : null}
          </View>

          <View style={styles.group}>
            <PhotoPickerField
              photos={photos}
              disabled={submitting}
              error={show("photos")}
              onAppend={(added) => setPhotos((current) => [...current, ...added])}
              onRemove={(key) => setPhotos((current) => current.filter((photo) => photo.key !== key))}
            />
          </View>

          {submitError ? <Notice tone="danger" text={submitError} /> : null}
          <Button mode="contained" icon="send-outline" loading={submitting} disabled={submitting} onPress={requestSubmit} style={styles.submit}>
            {t("form.submit")}
          </Button>
        </ScrollView>
      </KeyboardAvoidingView>

      <Portal>
        <Dialog visible={confirmOpen} onDismiss={submitting ? undefined : () => setConfirmOpen(false)} dismissable={!submitting}>
          <Dialog.Title>{t("form.confirmTitle")}</Dialog.Title>
          <Dialog.Content>
            <Text style={styles.muted}>
              {t("form.confirmContent", { stores: values.storeScope.length, photos: photos.length, expiresOn: values.expiresOn })}
            </Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setConfirmOpen(false)} disabled={submitting}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" loading={submitting} disabled={submitting} onPress={() => mutation.mutate()}>
              {t("form.confirmOk")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    </FaceScreenFrame>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl, gap: HB_SPACING.xs },
  group: { gap: 6 },
  input: { backgroundColor: HB_COLORS.white },
  muted: { fontSize: 13, color: HB_COLORS.textSecondary },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  radio: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  radioOn: { borderColor: HB_COLORS.action, backgroundColor: "#EAF2FF" },
  radioText: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  dateField: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  dateFieldError: { borderColor: HB_COLORS.danger },
  dateValue: { fontSize: 15, color: HB_COLORS.textPrimary },
  datePlaceholder: { fontSize: 15, color: HB_COLORS.textSecondary },
  submit: { marginTop: HB_SPACING.sm },
});
