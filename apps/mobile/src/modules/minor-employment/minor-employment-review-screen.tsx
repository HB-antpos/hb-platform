import { useState } from "react";
import {
  Alert,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import {
  ActivityIndicator,
  Button,
  Checkbox,
  Chip,
  Dialog,
  Divider,
  HelperText,
  Portal,
  Surface,
  Text,
  TextInput,
  TouchableRipple,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { i18n } from "@/shared/i18n/i18n";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { downloadMinorDocument } from "./api";
import { minorEmploymentHrApi, type HrReviewStatus } from "./hr-api";
import {
  describeGuardianAmendments,
  MINOR_RETURN_FIELDS,
  type MinorReturnField,
} from "./types";

const tabs: HrReviewStatus[] = ["Submitted", "Approved", "Returned"];
const get = (value: unknown, key: string): unknown => {
  if (!value || typeof value !== "object") return undefined;
  const x = value as Record<string, unknown>;
  return x[key] ?? x[key[0].toUpperCase() + key.slice(1)];
};
// 模块级辅助函数不能用 hook，直接读当前语言；使用它们的组件都已订阅语言变化。
const tr = (key: string, options?: Record<string, unknown>): string =>
  String(i18n.t(key, { ns: "minorEmployment", ...options }));
const display = (value: unknown) =>
  value == null || value === "" ? tr("common.notFilled") : String(value);
const range = (value: unknown) =>
  tr("review.lines.range", {
    start: display(get(value, "startDate")),
    end: display(get(value, "endDate")),
  });
const statusLabel = (status: string) =>
  tr(`statuses.${status}`, { defaultValue: status });
// 后端 dayOfWeek 为 0（周日）~6（周六），显示为语言包里的星期名。
const WEEKDAY_KEYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const weekdayName = (dayOfWeek: number) =>
  tr(`weekdays.${WEEKDAY_KEYS[dayOfWeek] ?? dayOfWeek}`, { defaultValue: String(dayOfWeek) });

export function MinorEmploymentReviewScreen() {
  const { t } = useAppTranslation("minorEmployment");
  const router = useRouter();
  const queryClient = useQueryClient();
  const actor = useAuthStore(
    (state) => state.user?.userGuid ?? state.user?.userGUID ?? "anonymous",
  );
  const [status, setStatus] = useState<HrReviewStatus>("Submitted");
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string>();
  const [returnOpen, setReturnOpen] = useState(false);
  const [returnFields, setReturnFields] = useState<MinorReturnField[]>([]);
  const [comment, setComment] = useState("");
  const [message, setMessage] = useState("");
  const wide = useWindowDimensions().width >= 760;
  const listKey = ["minorEmployment", "hr", actor, status, page] as const;
  const list = useQuery({
    queryKey: listKey,
    queryFn: () => minorEmploymentHrApi.getReviews(status, page),
    gcTime: 0,
    staleTime: 0,
  });
  const detail = useQuery({
    queryKey: ["minorEmployment", "hr", actor, selectedId],
    queryFn: () => minorEmploymentHrApi.getReview(selectedId!),
    enabled: Boolean(selectedId),
    gcTime: 0,
    staleTime: 0,
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({
      queryKey: ["minorEmployment", "hr", actor],
    });
    await detail.refetch();
  };
  const approve = useMutation({
    mutationFn: () =>
      minorEmploymentHrApi.approve(selectedId!, detail.data!.version),
    onSuccess: refresh,
    onError: (e) =>
      Alert.alert(
        t("review.alerts.approveFailed"),
        e instanceof Error ? e.message : t("review.alerts.versionChanged"),
      ),
  });
  const reject = useMutation({
    mutationFn: () =>
      minorEmploymentHrApi.returnForCorrection(
        selectedId!,
        detail.data!.version,
        returnFields,
        comment.trim(),
      ),
    onSuccess: async () => {
      setReturnOpen(false);
      setComment("");
      setReturnFields([]);
      await refresh();
    },
    onError: (e) =>
      Alert.alert(
        t("review.alerts.returnFailed"),
        e instanceof Error ? e.message : t("review.alerts.versionChanged"),
      ),
  });
  const document = async () => {
    if (!selectedId) return;
    let cleanup: (() => Promise<void>) | undefined;
    try {
      cleanup = await downloadMinorDocument(
        selectedId,
        t("review.documentDialogTitle"),
      );
    } catch (e) {
      Alert.alert(
        t("review.alerts.downloadFailed"),
        e instanceof Error ? e.message : t("review.alerts.tryLater"),
      );
    } finally {
      if (cleanup) await cleanup().catch(() => undefined);
    }
  };
  const returnDialog = (
    <ReturnDialog
      open={returnOpen}
      fields={returnFields}
      setFields={setReturnFields}
      comment={comment}
      setComment={setComment}
      message={message}
      setMessage={setMessage}
      pending={reject.isPending}
      onDismiss={() => setReturnOpen(false)}
      onSubmit={() => reject.mutate()}
    />
  );
  if (!wide && selectedId)
    return (
      <SafeAreaView style={styles.screen} edges={["top"]}>
        <Button icon="arrow-left" onPress={() => setSelectedId(undefined)}>
          {t("review.backToList")}
        </Button>
        <ReviewPane
          actor={actor}
          detail={detail.data}
          loading={detail.isLoading}
          error={detail.isError}
          onApprove={() => approve.mutate()}
          onReturn={() => setReturnOpen(true)}
          onDocument={() => void document()}
          pending={approve.isPending || reject.isPending}
        />
        {returnDialog}
      </SafeAreaView>
    );
  if (list.isLoading)
    return (
      <SafeAreaView style={styles.center}>
        <ActivityIndicator size="large" />
      </SafeAreaView>
    );
  if (list.isError)
    return (
      <SafeAreaView style={styles.center}>
        <EmptyState
          title={t("review.loadFailed")}
          primaryAction={{
            label: t("common.retry"),
            icon: "refresh",
            onPress: () => void list.refetch(),
          }}
        />
      </SafeAreaView>
    );
  const rows = list.data?.items ?? [];
  return (
    <SafeAreaView style={styles.screen} edges={["top"]}>
      <View style={styles.header}>
        <View style={styles.titleRow}>
          <Button icon="arrow-left" onPress={() => router.back()}>
            {t("common.back")}
          </Button>
          <Text variant="headlineSmall">{t("review.title")}</Text>
        </View>
        <Text style={styles.muted}>{t("review.subtitle")}</Text>
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.tabs}
        >
          {tabs.map((item) => (
            <Chip
              key={item}
              selected={status === item}
              onPress={() => {
                setStatus(item);
                setPage(1);
                setSelectedId(undefined);
              }}
            >
              {t(`review.tabs.${item}`)}
              {status === item ? `  ${list.data?.total ?? 0}` : ""}
            </Chip>
          ))}
        </ScrollView>
      </View>
      <View style={[styles.body, wide && styles.bodyWide]}>
        <Surface style={[styles.queue, wide && styles.queueWide]} elevation={0}>
          <View style={styles.rowBetween}>
            <Text variant="titleMedium">{t("review.queueTitle")}</Text>
            <Chip compact>{list.data?.total ?? 0}</Chip>
          </View>
          <ScrollView>
            {rows.length ? (
              rows.map((row) => (
                <Surface
                  key={row.id}
                  style={[
                    styles.rowCard,
                    row.id === selectedId && styles.selected,
                  ]}
                  elevation={0}
                >
                  <TouchableRipple
                    style={styles.rowButton}
                    accessibilityRole="button"
                    accessibilityLabel={t("review.viewAria", {
                      name: row.employeeName || row.employeeId,
                    })}
                    onPress={() => setSelectedId(row.id)}
                  >
                    <View style={styles.rowContent}>
                      <View style={{ flex: 1 }}>
                        <Text variant="titleMedium">
                          {row.employeeName || row.employeeId}
                        </Text>
                        <Text style={styles.muted}>
                          {row.state} · v{row.version}
                        </Text>
                      </View>
                      <Chip compact>
                        {row.warningCodes.length
                          ? t("review.warningCount", {
                              count: row.warningCodes.length,
                            })
                          : statusLabel(row.status)}
                      </Chip>
                    </View>
                  </TouchableRipple>
                </Surface>
              ))
            ) : (
              <EmptyState title={t("review.empty")} />
            )}
          </ScrollView>
          <View style={styles.pagination}>
            <Button
              disabled={page <= 1 || list.isFetching}
              onPress={() => setPage((p) => p - 1)}
            >
              {t("review.previous")}
            </Button>
            <Text style={styles.muted}>{t("review.page", { page })}</Text>
            <Button
              disabled={
                !rows.length ||
                rows.length < (list.data?.pageSize ?? 20) ||
                list.isFetching
              }
              onPress={() => setPage((p) => p + 1)}
            >
              {t("review.loadMore")}
            </Button>
          </View>
        </Surface>
        {wide ? (
          <Surface style={styles.detail} elevation={0}>
            <ReviewPane
              actor={actor}
              detail={detail.data}
              loading={detail.isLoading}
              error={detail.isError}
              onApprove={() => approve.mutate()}
              onReturn={() => setReturnOpen(true)}
              onDocument={() => void document()}
              pending={approve.isPending || reject.isPending}
            />
          </Surface>
        ) : null}
      </View>
      {returnDialog}
    </SafeAreaView>
  );
}

function ReviewPane({
  actor,
  detail,
  loading,
  error,
  onApprove,
  onReturn,
  onDocument,
  pending,
}: {
  actor: string;
  detail?: Awaited<ReturnType<typeof minorEmploymentHrApi.getReview>>;
  loading: boolean;
  error: boolean;
  onApprove: () => void;
  onReturn: () => void;
  onDocument: () => void;
  pending: boolean;
}) {
  const { t } = useAppTranslation("minorEmployment");
  if (loading)
    return (
      <View style={styles.center}>
        <ActivityIndicator />
      </View>
    );
  if (error || !detail)
    return (
      <EmptyState
        title={t("review.selectTitle")}
        description={t("review.selectDescription")}
      />
    );
  return (
    <ReviewDetail
      actor={actor}
      detail={detail}
      onApprove={onApprove}
      onReturn={onReturn}
      onDocument={onDocument}
      pending={pending}
    />
  );
}
function Section({
  title,
  children,
  warning = false,
}: {
  title: string;
  children: React.ReactNode;
  warning?: boolean;
}) {
  return (
    <Surface style={[styles.info, warning && styles.warning]} elevation={0}>
      <Text variant="titleMedium">{title}</Text>
      {children}
    </Surface>
  );
}
function Line({ label, value }: { label: string; value: unknown }) {
  const { t } = useAppTranslation("minorEmployment");
  return (
    <Text selectable>
      <Text style={styles.label}>
        {label}
        {t("common.labelSuffix")}
      </Text>
      {display(value)}
    </Text>
  );
}
function ReviewDetail({
  actor,
  detail,
  onApprove,
  onReturn,
  onDocument,
  pending,
}: {
  actor: string;
  detail: Awaited<ReturnType<typeof minorEmploymentHrApi.getReview>>;
  onApprove: () => void;
  onReturn: () => void;
  onDocument: () => void;
  pending: boolean;
}) {
  const { t } = useAppTranslation("minorEmployment");
  const history = useQuery({
    queryKey: ["minorEmployment", "history", actor, detail.id],
    queryFn: () => minorEmploymentHrApi.getHistory(detail.id),
    gcTime: 0,
    staleTime: 0,
  });
  const cal = detail.schoolCalendar;
  const form = detail.formData;
  const other = detail.otherWork;
  return (
    <ScrollView contentContainerStyle={styles.detailContent}>
      <View style={styles.rowBetween}>
        <View style={{ flex: 1 }}>
          <Text variant="headlineSmall">
            {detail.employeeName || detail.employeeId}
          </Text>
          <Text style={styles.muted}>
            {detail.state} · {detail.formType ?? "CE1"} · v{detail.version}
            {detail.revision
              ? t("review.revision", { revision: detail.revision })
              : ""}
          </Text>
        </View>
        <Chip>{statusLabel(detail.status)}</Chip>
      </View>
      <Section title={t("review.sections.childEmployer")}>
        <Line
          label={t("review.lines.childName")}
          value={`${form.childGivenName} ${form.childFamilyName}`}
        />
        <Line
          label={t("review.lines.dateOfBirth")}
          value={detail.dateOfBirth}
        />
        <Line
          label={t("review.lines.address")}
          value={`${form.childAddress} ${form.childPostcode}`}
        />
        <Line
          label={t("review.lines.employer")}
          value={`${form.employerCompanyName} / ${form.employerTradingName}`}
        />
        <Line
          label={t("review.lines.employerContact")}
          value={`${form.employerPhone} ${form.employerMobile} ${form.employerEmail}`}
        />
      </Section>
      <Section title={t("review.sections.parentContacts")}>
        <Line
          label={t("review.lines.parent")}
          value={`${detail.parentName} · ${detail.parentPhone} · ${detail.parentEmail}`}
        />
        <Line
          label={t("review.lines.parentSigned")}
          value={
            detail.parentSignedAt
              ? t("review.lines.signedWithName", {
                  at: detail.parentSignedAt,
                  name:
                    detail.parentSignatureName ||
                    t("review.lines.signedDefault"),
                })
              : t("review.lines.notSigned")
          }
        />
        {detail.guardianAmendedFields.length ? (
          <Line
            label={t("review.lines.guardianAmended")}
            value={t("review.lines.guardianAmendedValue", {
              fields: describeGuardianAmendments(
                detail.guardianAmendedFields,
                (key) => t(`guardianAmended.${key}`, { defaultValue: key }),
                t("common.listSeparator"),
              ),
            })}
          />
        ) : null}
        <Line
          label={t("review.lines.backupContact")}
          value={`${detail.backupContact.fullName} · ${detail.backupContact.relationship} · ${detail.backupContact.phone} · ${detail.backupContact.email}`}
        />
        <Line
          label={t("review.lines.contactCompleteness")}
          value={
            detail.parentPhone &&
            detail.parentEmail &&
            detail.backupContact.phone &&
            detail.backupContact.email
              ? t("review.lines.complete")
              : t("review.lines.incomplete")
          }
        />
      </Section>
      <Section title={t("review.sections.schoolCalendar")}>
        <Line
          label={t("review.lines.school")}
          value={`${detail.schoolName} · ${detail.schoolYear}`}
        />
        <Line
          label={t("review.lines.schoolContact")}
          value={`${cal.schoolContactName} ${cal.schoolContactPhone} ${cal.schoolContactEmail}`}
        />
        <Text style={styles.subheading}>
          {t("review.lines.weeklySchedule")}
        </Text>
        {cal.weeklySchedule.length ? (
          cal.weeklySchedule.map((item, i) => (
            <Line
              key={i}
              label={
                item.mustAttend
                  ? t("review.lines.dayAttend", { day: weekdayName(item.dayOfWeek) })
                  : t("review.lines.day", { day: weekdayName(item.dayOfWeek) })
              }
              value={`${item.startLocalTime}–${item.endLocalTime}`}
            />
          ))
        ) : (
          <Line
            label={t("review.lines.timetable")}
            value={t("common.notFilled")}
          />
        )}
        <Text style={styles.subheading}>{t("review.lines.termsHeading")}</Text>
        {[
          ...cal.termRanges.map((x) =>
            t("review.lines.term", { range: range(x) }),
          ),
          ...cal.holidays.map((x) =>
            t("review.lines.holiday", { range: range(x) }),
          ),
          ...cal.pupilFreeDays.map((x) =>
            t("review.lines.pupilFree", { range: range(x) }),
          ),
        ].map((x, i) => (
          <Text key={i}>{x}</Text>
        ))}
      </Section>
      <Section title={t("review.sections.otherWork")}>
        <Line
          label={t("review.lines.hasOtherWork")}
          value={other.hasOtherWork ? t("options.have") : t("options.haveNone")}
        />
        <Line
          label={t("review.lines.hoursUnknown")}
          value={other.hoursUnknown ? t("options.yes") : t("options.no")}
        />
        {other.employers.map((x, i) => (
          <View key={i} style={styles.nested}>
            <Line
              label={t("review.lines.otherEmployer", { n: i + 1 })}
              value={`${x.companyName} ${x.phone} ${x.email}`}
            />
            <Line
              label={t("review.lines.weeklyHours")}
              value={Object.entries(x.weeklyHours)
                .map(([d, h]) =>
                  t("review.lines.dayHours", { day: t(`weekdays.${d}`, { defaultValue: d }), hours: h }),
                )
                .join(t("review.lines.dayHoursSeparator"))}
            />
          </View>
        ))}
        <Text style={styles.subheading}>{t("review.lines.planned")}</Text>
        {other.plannedIntervals.map((x, i) => (
          <Line
            key={i}
            label={t("review.lines.plannedN", { n: i + 1 })}
            value={t("review.lines.interval", {
              start: x.startUtc,
              end: x.endUtc,
              hours: x.hours,
            })}
          />
        ))}
        <Text style={styles.subheading}>{t("review.lines.actual")}</Text>
        {other.actualIntervals.map((x, i) => (
          <Line
            key={i}
            label={t("review.lines.actualN", { n: i + 1 })}
            value={t("review.lines.interval", {
              start: x.startUtc,
              end: x.endUtc,
              hours: x.hours,
            })}
          />
        ))}
      </Section>
      <Section title={t("review.sections.commute")}>
        <Line
          label={t("review.lines.afterSchoolToStore")}
          value={t("review.lines.minutes", {
            n: detail.commute.afterSchoolToStoreMinutes,
          })}
        />
        <Line
          label={t("review.lines.homeward")}
          value={t("review.lines.minutes", {
            n: detail.commute.homewardMinutes,
          })}
        />
        <Line
          label={t("review.lines.transportMode")}
          value={detail.commute.transportMode}
        />
        <Line
          label={t("review.lines.pickupPerson")}
          value={detail.commute.pickupPerson}
        />
        <Line
          label={t("review.lines.latestTransport")}
          value={detail.commute.latestTransportLocalTime}
        />
        <Line
          label={t("review.lines.latestWorkEnd")}
          value={detail.commute.latestWorkEndLocalTime}
        />
      </Section>
      {detail.warningCodes.length ? (
        <Section title={t("review.sections.warnings")} warning>
          <Text>{detail.warningCodes.join(t("common.listSeparator"))}</Text>
          <Text style={styles.muted}>{t("review.warningNote")}</Text>
        </Section>
      ) : null}
      <Section title={t("review.sections.history")}>
        <Line
          label={t("review.lines.submittedAt")}
          value={detail.submittedAt}
        />
        <Line label={t("review.lines.reviewedAt")} value={detail.reviewedAt} />
        <Line
          label={t("review.lines.reviewComment")}
          value={detail.reviewComment}
        />
        {history.isLoading ? (
          <ActivityIndicator />
        ) : history.isError ? (
          <Text style={styles.muted}>{t("review.historyFailed")}</Text>
        ) : (
          history.data?.map((item, i) => (
            <HistoryItem
              key={`${String(get(item, "version"))}-${i}`}
              item={item}
            />
          ))
        )}
      </Section>
      <Divider />
      <View style={styles.actions}>
        <Button
          mode="outlined"
          icon="file-eye-outline"
          onPress={onDocument}
          disabled={pending}
        >
          {t("review.actions.document")}
        </Button>
        {detail.status === "Submitted" ? (
          <>
            <Button
              mode="contained"
              onPress={onApprove}
              loading={pending}
              disabled={pending}
            >
              {t("review.actions.approve")}
            </Button>
            <Button mode="outlined" onPress={onReturn} disabled={pending}>
              {t("review.actions.returnForCorrection")}
            </Button>
          </>
        ) : null}
      </View>
    </ScrollView>
  );
}
function HistoryItem({ item }: { item: unknown }) {
  const { t } = useAppTranslation("minorEmployment");
  const metadata = get(item, "metadata");
  const audits = get(item, "audits");
  return (
    <Surface style={styles.historyItem} elevation={0}>
      <Text>
        v{display(get(item, "version"))} · {display(get(item, "status"))}
      </Text>
      <Text style={styles.muted}>
        {display(get(item, "createdAtUtc") ?? get(item, "createdAt"))} ·{" "}
        {display(get(item, "actorLabel") ?? get(item, "actor"))}
      </Text>
      <Text>{display(get(item, "comment") ?? get(metadata, "comment"))}</Text>
      {Array.isArray(audits)
        ? audits.map((audit, i) => (
            <Text key={i} style={styles.muted}>
              {t("review.audit", {
                action: display(get(audit, "action")),
                actor: display(get(audit, "actorLabel")),
                at: display(get(audit, "createdAt")),
                comment: display(get(get(audit, "metadata"), "comment")),
              })}
            </Text>
          ))
        : null}
    </Surface>
  );
}
function ReturnDialog({
  open,
  onDismiss,
  fields,
  setFields,
  comment,
  setComment,
  message,
  setMessage,
  pending,
  onSubmit,
}: {
  open: boolean;
  onDismiss: () => void;
  fields: MinorReturnField[];
  setFields: (x: MinorReturnField[]) => void;
  comment: string;
  setComment: (x: string) => void;
  message: string;
  setMessage: (x: string) => void;
  pending: boolean;
  onSubmit: () => void;
}) {
  const { t } = useAppTranslation("minorEmployment");
  return (
    <Portal>
      <Dialog visible={open} onDismiss={onDismiss}>
        <Dialog.Title>{t("review.dialog.title")}</Dialog.Title>
        <Dialog.Content>
          <ScrollView style={styles.returnScroll}>
            <Text>{t("review.dialog.hint")}</Text>
            {MINOR_RETURN_FIELDS.map((field) => (
              <Checkbox.Item
                key={field}
                label={t(`returnFields.${field}`)}
                status={fields.includes(field) ? "checked" : "unchecked"}
                onPress={() =>
                  setFields(
                    fields.includes(field)
                      ? fields.filter((x) => x !== field)
                      : [...fields, field],
                  )
                }
              />
            ))}
            <TextInput
              mode="outlined"
              label={t("review.dialog.comment")}
              value={comment}
              onChangeText={setComment}
              multiline
            />
            <HelperText type="error" visible={Boolean(message)}>
              {message}
            </HelperText>
          </ScrollView>
        </Dialog.Content>
        <Dialog.Actions>
          <Button onPress={onDismiss}>{t("review.dialog.cancel")}</Button>
          <Button
            onPress={() => {
              if (!fields.length || !comment.trim()) {
                setMessage(t("review.dialog.validation"));
                return;
              }
              setMessage("");
              onSubmit();
            }}
            loading={pending}
            disabled={pending}
          >
            {t("review.dialog.confirm")}
          </Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}
const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { padding: HB_SPACING.md, gap: 6, backgroundColor: HB_COLORS.white },
  titleRow: { flexDirection: "row", alignItems: "center" },
  tabs: { gap: 8, paddingVertical: 4 },
  body: { flex: 1, gap: HB_SPACING.sm, padding: HB_SPACING.sm },
  bodyWide: { flexDirection: "row" },
  queue: { flex: 1, padding: HB_SPACING.sm, backgroundColor: HB_COLORS.white },
  queueWide: { flex: 0, width: 340 },
  detail: { flex: 1, padding: HB_SPACING.md, backgroundColor: HB_COLORS.white },
  detailContent: { gap: HB_SPACING.md, paddingBottom: 40 },
  rowBetween: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  rowCard: {
    borderWidth: 1,
    borderColor: "#E4E7EC",
    marginTop: 6,
    borderRadius: 10,
  },
  selected: { borderColor: "#1677FF", backgroundColor: "#EFF6FF" },
  rowButton: { padding: HB_SPACING.sm },
  rowContent: { flexDirection: "row", alignItems: "center", width: "100%" },
  info: {
    padding: HB_SPACING.md,
    gap: 6,
    backgroundColor: "#F8FAFC",
    borderRadius: 10,
  },
  warning: { backgroundColor: "#FFF7E6" },
  nested: { gap: 4, paddingTop: 5 },
  historyItem: {
    padding: HB_SPACING.sm,
    gap: 3,
    backgroundColor: "#F8FAFC",
    borderRadius: 8,
  },
  pagination: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingTop: 6,
  },
  actions: { gap: HB_SPACING.sm },
  subheading: { color: "#344054", fontWeight: "600", marginTop: 5 },
  label: { fontWeight: "600", color: "#344054" },
  muted: { color: "#667085" },
  returnScroll: { maxHeight: 420 },
  center: { flex: 1, alignItems: "center", justifyContent: "center" },
});
