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
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { downloadMinorDocument } from "./api";
import { minorEmploymentHrApi, type HrReviewStatus } from "./hr-api";
import {
  describeGuardianAmendments,
  MINOR_RETURN_FIELDS,
  type MinorReturnField,
} from "./types";

const tabs: { value: HrReviewStatus; label: string }[] = [
  { value: "Submitted", label: "待审核" },
  { value: "Approved", label: "已通过" },
  { value: "Returned", label: "已退回" },
];
const returnLabels: Record<MinorReturnField, string> = {
  parentContact: "家长联系方式",
  emergencyContact: "备用紧急联系人",
  schoolCalendar: "学校日历",
  education: "教育状态",
  workAvailability: "工作时段",
  otherWork: "其他工作",
  commutePlan: "通勤与接送",
  formData: "CE1 表单",
};
const get = (value: unknown, key: string): unknown => {
  if (!value || typeof value !== "object") return undefined;
  const x = value as Record<string, unknown>;
  return x[key] ?? x[key[0].toUpperCase() + key.slice(1)];
};
const display = (value: unknown) =>
  value == null || value === "" ? "未填写" : String(value);
const range = (value: unknown) =>
  `${display(get(value, "startDate"))} 至 ${display(get(value, "endDate"))}`;

export function MinorEmploymentReviewScreen() {
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
        "审核失败",
        e instanceof Error ? e.message : "版本已变化，请刷新",
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
        "退回失败",
        e instanceof Error ? e.message : "版本已变化，请刷新",
      ),
  });
  const document = async () => {
    if (!selectedId) return;
    let cleanup: (() => Promise<void>) | undefined;
    try {
      cleanup = await downloadMinorDocument(selectedId);
    } catch (e) {
      Alert.alert(
        "文件下载失败",
        e instanceof Error ? e.message : "请稍后重试",
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
          返回审核列表
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
          title="无法加载审核列表"
          primaryAction={{
            label: "重试",
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
            返回
          </Button>
          <Text variant="headlineSmall">未成年用工合规审核</Text>
        </View>
        <Text style={styles.muted}>
          审核只产生记录和提醒，不阻断排班、发布或考勤。
        </Text>
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.tabs}
        >
          {tabs.map((item) => (
            <Chip
              key={item.value}
              selected={status === item.value}
              onPress={() => {
                setStatus(item.value);
                setPage(1);
                setSelectedId(undefined);
              }}
            >
              {item.label}
              {status === item.value ? `  ${list.data?.total ?? 0}` : ""}
            </Chip>
          ))}
        </ScrollView>
      </View>
      <View style={[styles.body, wide && styles.bodyWide]}>
        <Surface style={[styles.queue, wide && styles.queueWide]} elevation={0}>
          <View style={styles.rowBetween}>
            <Text variant="titleMedium">审核资料</Text>
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
                    accessibilityLabel={`查看 ${row.employeeName || row.employeeId} 的未成年用工资料`}
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
                          ? `提醒 ${row.warningCodes.length}`
                          : row.status}
                      </Chip>
                    </View>
                  </TouchableRipple>
                </Surface>
              ))
            ) : (
              <EmptyState title="暂无资料" />
            )}
          </ScrollView>
          <View style={styles.pagination}>
            <Button
              disabled={page <= 1 || list.isFetching}
              onPress={() => setPage((p) => p - 1)}
            >
              上一页
            </Button>
            <Text style={styles.muted}>第 {page} 页</Text>
            <Button
              disabled={
                !rows.length ||
                rows.length < (list.data?.pageSize ?? 20) ||
                list.isFetching
              }
              onPress={() => setPage((p) => p + 1)}
            >
              加载更多
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
  if (loading)
    return (
      <View style={styles.center}>
        <ActivityIndicator />
      </View>
    );
  if (error || !detail)
    return (
      <EmptyState
        title="请选择一条资料查看详情"
        description="可核对家长、课表、假期、其他工作和通勤信息。"
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
  return (
    <Text selectable>
      <Text style={styles.label}>{label}：</Text>
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
            {detail.revision ? ` · 修订 ${detail.revision}` : ""}
          </Text>
        </View>
        <Chip>{detail.status}</Chip>
      </View>
      <Section title="孩子与雇主">
        <Line
          label="孩子姓名"
          value={`${form.childGivenName} ${form.childFamilyName}`}
        />
        <Line label="出生日期" value={detail.dateOfBirth} />
        <Line
          label="地址"
          value={`${form.childAddress} ${form.childPostcode}`}
        />
        <Line
          label="雇主"
          value={`${form.employerCompanyName} / ${form.employerTradingName}`}
        />
        <Line
          label="雇主联系方式"
          value={`${form.employerPhone} ${form.employerMobile} ${form.employerEmail}`}
        />
      </Section>
      <Section title="家长与紧急联系人">
        <Line
          label="家长"
          value={`${detail.parentName} · ${detail.parentPhone} · ${detail.parentEmail}`}
        />
        <Line
          label="家长签署"
          value={
            detail.parentSignedAt
              ? `${detail.parentSignedAt}（${detail.parentSignatureName || "已签署"}）`
              : "未签署"
          }
        />
        {detail.guardianAmendedFields.length ? (
          <Line
            label="监护人现场修改"
            value={`${describeGuardianAmendments(detail.guardianAmendedFields)}（修改前后内容见历史记录）`}
          />
        ) : null}
        <Line
          label="备用联系人"
          value={`${detail.backupContact.fullName} · ${detail.backupContact.relationship} · ${detail.backupContact.phone} · ${detail.backupContact.email}`}
        />
        <Line
          label="联系方式完整性"
          value={
            detail.parentPhone &&
            detail.parentEmail &&
            detail.backupContact.phone &&
            detail.backupContact.email
              ? "完整"
              : "需要补充"
          }
        />
      </Section>
      <Section title="学校课表与假期">
        <Line
          label="学校"
          value={`${detail.schoolName} · ${detail.schoolYear}`}
        />
        <Line
          label="学校联系人"
          value={`${cal.schoolContactName} ${cal.schoolContactPhone} ${cal.schoolContactEmail}`}
        />
        <Text style={styles.subheading}>每周上学安排</Text>
        {cal.weeklySchedule.length ? (
          cal.weeklySchedule.map((item, i) => (
            <Line
              key={i}
              label={
                item.mustAttend
                  ? `星期 ${item.dayOfWeek}（上学）`
                  : `星期 ${item.dayOfWeek}`
              }
              value={`${item.startLocalTime}–${item.endLocalTime}`}
            />
          ))
        ) : (
          <Line label="课表" value="未填写" />
        )}
        <Text style={styles.subheading}>学期、学校假期和无课日</Text>
        {[
          ...cal.termRanges.map((x) => `学期：${range(x)}`),
          ...cal.holidays.map((x) => `学校假期：${range(x)}`),
          ...cal.pupilFreeDays.map((x) => `无课日：${range(x)}`),
        ].map((x, i) => (
          <Text key={i}>{x}</Text>
        ))}
      </Section>
      <Section title="其他工作与实际工时">
        <Line
          label="是否有其他工作"
          value={other.hasOtherWork ? "有" : "没有"}
        />
        <Line label="工时是否未知" value={other.hoursUnknown ? "是" : "否"} />
        {other.employers.map((x, i) => (
          <View key={i} style={styles.nested}>
            <Line
              label={`其他雇主 ${i + 1}`}
              value={`${x.companyName} ${x.phone} ${x.email}`}
            />
            <Line
              label="每周工时"
              value={Object.entries(x.weeklyHours)
                .map(([d, h]) => `${d}: ${h} 小时`)
                .join("；")}
            />
          </View>
        ))}
        <Text style={styles.subheading}>计划时段</Text>
        {other.plannedIntervals.map((x, i) => (
          <Line
            key={i}
            label={`计划 ${i + 1}`}
            value={`${x.startUtc}–${x.endUtc}（${x.hours} 小时）`}
          />
        ))}
        <Text style={styles.subheading}>实际时段</Text>
        {other.actualIntervals.map((x, i) => (
          <Line
            key={i}
            label={`实际 ${i + 1}`}
            value={`${x.startUtc}–${x.endUtc}（${x.hours} 小时）`}
          />
        ))}
      </Section>
      <Section title="通勤与接送">
        <Line
          label="放学到门店"
          value={`${detail.commute.afterSchoolToStoreMinutes} 分钟`}
        />
        <Line
          label="回家用时"
          value={`${detail.commute.homewardMinutes} 分钟`}
        />
        <Line label="交通方式" value={detail.commute.transportMode} />
        <Line label="接送人" value={detail.commute.pickupPerson} />
        <Line
          label="最晚交通"
          value={detail.commute.latestTransportLocalTime}
        />
        <Line
          label="最晚工作结束"
          value={detail.commute.latestWorkEndLocalTime}
        />
      </Section>
      {detail.warningCodes.length ? (
        <Section title="合规提醒" warning>
          <Text>{detail.warningCodes.join("、")}</Text>
          <Text style={styles.muted}>提醒不会阻止排班、发布或考勤。</Text>
        </Section>
      ) : null}
      <Section title="审核历史">
        <Line label="提交时间" value={detail.submittedAt} />
        <Line label="审核时间" value={detail.reviewedAt} />
        <Line label="审核意见" value={detail.reviewComment} />
        {history.isLoading ? (
          <ActivityIndicator />
        ) : history.isError ? (
          <Text style={styles.muted}>历史加载失败，可稍后刷新。</Text>
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
          查看只读原件
        </Button>
        {detail.status === "Submitted" ? (
          <>
            <Button
              mode="contained"
              onPress={onApprove}
              loading={pending}
              disabled={pending}
            >
              通过
            </Button>
            <Button mode="outlined" onPress={onReturn} disabled={pending}>
              退回补充
            </Button>
          </>
        ) : null}
      </View>
    </ScrollView>
  );
}
function HistoryItem({ item }: { item: unknown }) {
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
              审计：{display(get(audit, "action"))} ·{" "}
              {display(get(audit, "actorLabel"))} ·{" "}
              {display(get(audit, "createdAt"))} ·{" "}
              {display(get(get(audit, "metadata"), "comment"))}
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
  return (
    <Portal>
      <Dialog visible={open} onDismiss={onDismiss}>
        <Dialog.Title>退回补充</Dialog.Title>
        <Dialog.Content>
          <ScrollView style={styles.returnScroll}>
            <Text>选择需要补充的字段，并填写必填意见。</Text>
            {MINOR_RETURN_FIELDS.map((field) => (
              <Checkbox.Item
                key={field}
                label={returnLabels[field]}
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
              label="退回意见（必填）"
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
          <Button onPress={onDismiss}>取消</Button>
          <Button
            onPress={() => {
              if (!fields.length || !comment.trim()) {
                setMessage("请选择字段并填写退回意见");
                return;
              }
              setMessage("");
              onSubmit();
            }}
            loading={pending}
            disabled={pending}
          >
            确认退回
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
