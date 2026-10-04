import { useCallback, useEffect, useState } from "react";
import { ScrollView, Share, StyleSheet, View } from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFocusEffect, useRouter } from "expo-router";
import {
  ActivityIndicator,
  Button,
  HelperText,
  SegmentedButtons,
  Surface,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { getStoredApiHost } from "@/shared/api/config";
import { useAuthStore } from "@/store/auth-store";
import { minorEmploymentApi } from "./api";
import {
  describeGuardianAmendments,
  emptyDraft,
  type MinorContact,
  type MinorEmploymentDraft,
  type MinorGuardianInviteResult,
  type MinorOtherEmployer,
} from "./types";
const days = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
];
const returnFieldLabels: Record<string, string> = {
  parentContact: "家长联系方式",
  parent: "家长联系方式",
  emergencyContact: "备用紧急联系人",
  backupContact: "备用紧急联系人",
  schoolCalendar: "学校日历",
  education: "教育状态",
  workAvailability: "工作时段",
  rosterEvidence: "排班证明",
  otherWork: "其他工作",
  commutePlan: "通勤与接送",
  formData: "CE1 表单",
};
export function MinorEmploymentScreen() {
  const router = useRouter();
  const qc = useQueryClient();
  const actor = useAuthStore(
    (state) => state.user?.userGuid ?? state.user?.userGUID ?? "anonymous",
  );
  const key = ["minorEmployment", "me", actor] as const;
  const query = useQuery({
    queryKey: key,
    queryFn: minorEmploymentApi.getMine,
    gcTime: 0,
    staleTime: 0,
  });
  const { refetch } = query;
  const [draft, setDraft] = useState(emptyDraft);
  const [error, setError] = useState("");
  const [link, setLink] = useState("");
  const [inviteResult, setInviteResult] =
    useState<MinorGuardianInviteResult | null>(null);
  const [dirty, setDirty] = useState(false);
  // 店长发来的填写请求，用于页面顶部提示。
  const requests = useQuery({
    queryKey: ["minorEmployment", "me", "requests", actor] as const,
    queryFn: minorEmploymentApi.getMyRequests,
    staleTime: 30_000,
  });
  useEffect(() => {
    setDraft(emptyDraft());
    setLink("");
    setInviteResult(null);
    setError("");
    setDirty(false);
  }, [actor]);
  useEffect(() => {
    if (query.data && !dirty) setDraft(query.data);
  }, [query.data, dirty]);
  useFocusEffect(
    useCallback(() => {
      void refetch();
    }, [refetch]),
  );
  const serverMessage = (error: unknown) =>
    error instanceof Error ? error.message : "服务器未能完成操作，请刷新后重试";
  const save = useMutation({
    mutationFn: () =>
      minorEmploymentApi.saveDraft({
        ...draft,
        version: query.data?.version,
        revision: query.data?.revision,
      }),
    onSuccess: (x) => {
      qc.setQueryData(key, x);
      setDraft(x);
      setDirty(false);
      setError("");
    },
    onError: (error) => setError(`保存失败：${serverMessage(error)}`),
  });
  // 发起签署：先保存草稿（等待签署中再发起会生成新版本、旧链接作废），再由后端发邮件或返回转发链接。
  const invite = useMutation({
    mutationFn: async (deliverByEmail: boolean) => {
      const saved = await minorEmploymentApi.saveDraft({
        ...draft,
        version: query.data?.version,
        revision: query.data?.revision,
      });
      qc.setQueryData(key, saved);
      setDraft(saved);
      setDirty(false);
      return minorEmploymentApi.inviteParent(
        saved.version,
        saved.revision,
        deliverByEmail,
      );
    },
    onSuccess: async (x) => {
      setInviteResult(x);
      setError("");
      const raw = x.signingUrl ?? "";
      if (raw) {
        const host = await getStoredApiHost();
        setLink(
          /^https?:\/\//i.test(raw)
            ? raw
            : `https://${host}${raw.startsWith("/") ? raw : `/${raw}`}`,
        );
      } else {
        // 邮件已直达监护人，员工手机上不保留链接。
        setLink("");
      }
      await qc.invalidateQueries({ queryKey: key });
      await query.refetch();
    },
    onError: (error) => setError(`发起签署失败：${serverMessage(error)}`),
  });
  const submit = useMutation({
    mutationFn: () => minorEmploymentApi.submit(query.data?.version ?? 0),
    onSuccess: (x) => {
      qc.setQueryData(key, x);
      setError("");
    },
    onError: (error) => setError(`提交失败：${serverMessage(error)}`),
  });
  const validateBeforeInvite = () => {
    if (
      !draft.parentPhone.trim() ||
      !draft.parentEmail.trim() ||
      !draft.emergencyContact.fullName.trim() ||
      !draft.emergencyContact.phone.trim()
    ) {
      setError("签署前必须填写家长电话、邮箱及一名完整备用联系人");
      return false;
    }
    return true;
  };
  const set = (patch: Partial<MinorEmploymentDraft>) => {
    setDirty(true);
    setDraft((d) => ({ ...d, ...patch }));
  };
  const setCalendar = (
    patch: Partial<MinorEmploymentDraft["schoolCalendar"]>,
  ) => set({ schoolCalendar: { ...draft.schoolCalendar, ...patch } });
  const setOther = (patch: Partial<MinorEmploymentDraft["otherWork"]>) =>
    set({ otherWork: { ...draft.otherWork, ...patch } });
  const setCommute = (patch: Partial<MinorEmploymentDraft["commute"]>) =>
    set({ commute: { ...draft.commute, ...patch } });
  if (query.isLoading)
    return (
      <SafeAreaView style={styles.center}>
        <ActivityIndicator size="large" />
      </SafeAreaView>
    );
  if (query.isError)
    return (
      <SafeAreaView style={styles.center}>
        <EmptyState
          title="无法加载资料"
          primaryAction={{
            label: "重试",
            icon: "refresh",
            onPress: () => void query.refetch(),
          }}
        />
      </SafeAreaView>
    );
  const locked = query.data?.status === "Submitted";
  const field = (
    label: string,
    value: string,
    onChange: (v: string) => void,
    opts?: Partial<React.ComponentProps<typeof TextInput>>,
  ) => (
    <TextInput
      mode="outlined"
      label={label}
      value={value}
      onChangeText={onChange}
      disabled={locked}
      {...opts}
    />
  );
  return (
    <SafeAreaView style={styles.screen} edges={["top"]}>
      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
      >
        <Button icon="arrow-left" onPress={() => router.back()}>
          返回
        </Button>
        <Text variant="headlineSmall">未成年用工合规</Text>
        <Text style={styles.muted}>
          完整填写 CE1 / NSW
          企业同意书资料。草稿可保存不完整，家长电话和邮箱在签署前必填。
        </Text>
        {(requests.data ?? []).map((request) => (
          <Surface key={request.id} style={styles.requestBanner} elevation={0}>
            <Text variant="titleSmall" style={styles.requestTitle}>
              {request.requestedByName
                ? `${request.requestedByName} 请你填写未成年用工资料`
                : "店长请你填写未成年用工资料"}
            </Text>
            {request.note ? <Text>{request.note}</Text> : null}
            <Text style={styles.muted}>
              填好后发送签署邮件给监护人，监护人签字后再提交 HR
              审核，这条待办会自动完成。
            </Text>
          </Surface>
        ))}
        {query.data ? (
          <View style={styles.rowBetween}>
            <Text style={styles.status}>
              状态：{query.data.status} · 版本 {query.data.version}
            </Text>
            <Button compact icon="refresh" onPress={() => void query.refetch()}>
              刷新状态
            </Button>
          </View>
        ) : null}
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">适用州与身份</Text>
          <SegmentedButtons
            value={draft.state}
            onValueChange={(v) => set({ state: v as "NSW" | "QLD" })}
            buttons={[
              { value: "QLD", label: "QLD", disabled: locked },
              { value: "NSW", label: "NSW", disabled: locked },
            ]}
          />
          {field(
            "出生日期",
            draft.dateOfBirth,
            (v) => set({ dateOfBirth: v }),
            { placeholder: "YYYY-MM-DD" },
          )}
          {field("学校名称", draft.schoolName, (v) => set({ schoolName: v }))}
          {field("年级", draft.schoolYear, (v) => set({ schoolYear: v }))}
          {field(
            "教育状态（在校/培训/完成 Year 10 等）",
            draft.educationStatus,
            (v) => set({ educationStatus: v }),
          )}
          <Text>是否完成 Year 10</Text>
          <SegmentedButtons
            value={
              draft.completedYear10 === true
                ? "yes"
                : draft.completedYear10 === false
                  ? "no"
                  : "unknown"
            }
            onValueChange={(v) =>
              set({
                completedYear10:
                  v === "yes" ? true : v === "no" ? false : undefined,
              })
            }
            buttons={[
              { value: "yes", label: "是", disabled: locked },
              { value: "no", label: "否", disabled: locked },
              { value: "unknown", label: "待确认", disabled: locked },
            ]}
          />
          <Text>是否必须继续注册入学</Text>
          <SegmentedButtons
            value={
              draft.requiredToBeEnrolled === true
                ? "yes"
                : draft.requiredToBeEnrolled === false
                  ? "no"
                  : "unknown"
            }
            onValueChange={(v) =>
              set({
                requiredToBeEnrolled:
                  v === "yes" ? true : v === "no" ? false : undefined,
              })
            }
            buttons={[
              { value: "yes", label: "是", disabled: locked },
              { value: "no", label: "否", disabled: locked },
              { value: "unknown", label: "待确认", disabled: locked },
            ]}
          />
          {field(
            "教育参与结束日",
            draft.participationEndDate,
            (v) => set({ participationEndDate: v }),
            { placeholder: "YYYY-MM-DD" },
          )}
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">孩子与家长 / 监护人</Text>
          {field("孩子名", draft.formData.childGivenName, (v) =>
            set({ formData: { ...draft.formData, childGivenName: v } }),
          )}
          {field("孩子姓", draft.formData.childFamilyName, (v) =>
            set({ formData: { ...draft.formData, childFamilyName: v } }),
          )}
          {field("孩子住址", draft.childAddress, (v) =>
            set({ childAddress: v }),
          )}
          {field(
            "孩子邮编",
            draft.childPostcode,
            (v) => set({ childPostcode: v }),
            { keyboardType: "number-pad" },
          )}
          {field("孩子电话", draft.childPhone, (v) => set({ childPhone: v }), {
            keyboardType: "phone-pad",
          })}
          {field("家长姓名", draft.parentName, (v) => set({ parentName: v }))}
          {field("家长关系", draft.parentRelationship, (v) =>
            set({ parentRelationship: v }),
          )}
          {field(
            "家长电话（签署前必填）",
            draft.parentPhone,
            (v) => set({ parentPhone: v }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            "家长邮箱（签署前必填）",
            draft.parentEmail,
            (v) => set({ parentEmail: v }),
            { keyboardType: "email-address", autoCapitalize: "none" },
          )}
          {field("家长地址", draft.parentAddress, (v) =>
            set({ parentAddress: v }),
          )}
          {field(
            "家长邮编",
            draft.parentPostcode,
            (v) => set({ parentPostcode: v }),
            { keyboardType: "number-pad" },
          )}
        </Surface>
        <ContactCard
          title="备用紧急联系人（必填）"
          value={draft.emergencyContact}
          disabled={locked}
          onChange={(v) => set({ emergencyContact: v })}
        />
        <ContactCard
          title="额外联系人（选填）"
          value={draft.backupContact}
          disabled={locked}
          onChange={(v) => set({ backupContact: v })}
        />
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">学校联系人、每周上学日与日期</Text>
          {field("教育提供者", draft.schoolCalendar.schoolProvider, (v) =>
            setCalendar({ schoolProvider: v }),
          )}
          {field("学校时区", draft.schoolCalendar.timeZoneId, (v) =>
            setCalendar({ timeZoneId: v }),
          )}
          {field(
            "学校联系人姓名",
            draft.schoolCalendar.schoolContactName,
            (v) => setCalendar({ schoolContactName: v }),
          )}
          {field(
            "学校联系人职位",
            draft.schoolCalendar.schoolContactPosition,
            (v) => setCalendar({ schoolContactPosition: v }),
          )}
          {field(
            "学校联系人电话",
            draft.schoolCalendar.schoolContactPhone,
            (v) => setCalendar({ schoolContactPhone: v }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            "学校联系人邮箱",
            draft.schoolCalendar.schoolContactEmail,
            (v) => setCalendar({ schoolContactEmail: v }),
            { keyboardType: "email-address" },
          )}
          {field(
            "学校联系人手机",
            draft.schoolCalendar.schoolContactMobile,
            (v) => setCalendar({ schoolContactMobile: v }),
            { keyboardType: "phone-pad" },
          )}
          {days.map((d, i) => {
            const old = draft.schoolCalendar.weeklySchedule.find(
              (x) => x.dayOfWeek === i,
            );
            return (
              <View key={d} style={styles.line}>
                <SegmentedButtons
                  value={old?.mustAttend ? "yes" : "no"}
                  onValueChange={(v) =>
                    setCalendar({
                      weeklySchedule: [
                        ...draft.schoolCalendar.weeklySchedule.filter(
                          (x) => x.dayOfWeek !== i,
                        ),
                        {
                          dayOfWeek: i as 0 | 1 | 2 | 3 | 4 | 5 | 6,
                          mustAttend: v === "yes",
                          startLocalTime: old?.startLocalTime ?? "",
                          endLocalTime: old?.endLocalTime ?? "",
                        },
                      ],
                    })
                  }
                  buttons={[
                    { value: "yes", label: `${d} 上学`, disabled: locked },
                    { value: "no", label: "不适用", disabled: locked },
                  ]}
                />
                <Text style={styles.muted}>
                  上课时长（根据开始和结束自动计算）：
                  {old?.mustAttend
                    ? durationLabel(old.startLocalTime, old.endLocalTime)
                    : "—"}
                </Text>
                {field(
                  "开始",
                  old?.startLocalTime ?? "",
                  (v) =>
                    setCalendar({
                      weeklySchedule: [
                        ...draft.schoolCalendar.weeklySchedule.filter(
                          (x) => x.dayOfWeek !== i,
                        ),
                        {
                          dayOfWeek: i as 0 | 1 | 2 | 3 | 4 | 5 | 6,
                          mustAttend: old?.mustAttend ?? true,
                          startLocalTime: v,
                          endLocalTime: old?.endLocalTime ?? "",
                        },
                      ],
                    }),
                  { placeholder: "09:00" },
                )}
                {field(
                  "结束",
                  old?.endLocalTime ?? "",
                  (v) =>
                    setCalendar({
                      weeklySchedule: [
                        ...draft.schoolCalendar.weeklySchedule.filter(
                          (x) => x.dayOfWeek !== i,
                        ),
                        {
                          dayOfWeek: i as 0 | 1 | 2 | 3 | 4 | 5 | 6,
                          mustAttend: old?.mustAttend ?? true,
                          startLocalTime: old?.startLocalTime ?? "",
                          endLocalTime: v,
                        },
                      ],
                    }),
                  { placeholder: "15:00" },
                )}
              </View>
            );
          })}
          {draft.schoolCalendar.termRanges.map((x, i) => (
            <View key={`term-${i}`} style={styles.info}>
              {field("学期开始", x.startDate, (v) =>
                setCalendar({
                  termRanges: draft.schoolCalendar.termRanges.map((z, j) =>
                    j === i ? { ...z, startDate: v } : z,
                  ),
                }),
              )}
              {field("学期结束", x.endDate, (v) =>
                setCalendar({
                  termRanges: draft.schoolCalendar.termRanges.map((z, j) =>
                    j === i ? { ...z, endDate: v } : z,
                  ),
                }),
              )}
              <Button
                onPress={() =>
                  setCalendar({
                    termRanges: draft.schoolCalendar.termRanges.filter(
                      (_, j) => j !== i,
                    ),
                  })
                }
              >
                删除学期
              </Button>
            </View>
          ))}
          <Button
            onPress={() =>
              setCalendar({
                termRanges: [
                  ...draft.schoolCalendar.termRanges,
                  { startDate: "", endDate: "" },
                ],
              })
            }
          >
            添加学期区间
          </Button>
          {draft.schoolCalendar.holidays.map((x, i) => (
            <View key={`holiday-${i}`} style={styles.info}>
              {field("学校假期开始", x.startDate, (v) =>
                setCalendar({
                  holidays: draft.schoolCalendar.holidays.map((z, j) =>
                    j === i ? { ...z, startDate: v } : z,
                  ),
                }),
              )}
              {field("学校假期结束", x.endDate, (v) =>
                setCalendar({
                  holidays: draft.schoolCalendar.holidays.map((z, j) =>
                    j === i ? { ...z, endDate: v } : z,
                  ),
                }),
              )}
              {field("假期标签", x.label ?? "", (v) =>
                setCalendar({
                  holidays: draft.schoolCalendar.holidays.map((z, j) =>
                    j === i ? { ...z, label: v } : z,
                  ),
                }),
              )}
              <Button
                onPress={() =>
                  setCalendar({
                    holidays: draft.schoolCalendar.holidays.filter(
                      (_, j) => j !== i,
                    ),
                  })
                }
              >
                删除假期
              </Button>
            </View>
          ))}
          <Button
            onPress={() =>
              setCalendar({
                holidays: [
                  ...draft.schoolCalendar.holidays,
                  { startDate: "", endDate: "", label: "" },
                ],
              })
            }
          >
            添加学校假期
          </Button>
          {draft.schoolCalendar.pupilFreeDays.map((x, i) => (
            <View key={`pupil-${i}`} style={styles.info}>
              {field("Pupil-free 日期", x.startDate, (v) =>
                setCalendar({
                  pupilFreeDays: draft.schoolCalendar.pupilFreeDays.map(
                    (z, j) =>
                      j === i ? { ...z, startDate: v, endDate: v } : z,
                  ),
                }),
              )}
              <Button
                onPress={() =>
                  setCalendar({
                    pupilFreeDays: draft.schoolCalendar.pupilFreeDays.filter(
                      (_, j) => j !== i,
                    ),
                  })
                }
              >
                删除日期
              </Button>
            </View>
          ))}
          <Button
            onPress={() =>
              setCalendar({
                pupilFreeDays: [
                  ...draft.schoolCalendar.pupilFreeDays,
                  { startDate: "", endDate: "", label: "" },
                ],
              })
            }
          >
            添加 Pupil-free 日期
          </Button>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">灵活学习与教师核实</Text>
          <SegmentedButtons
            value={
              draft.formData.flexibleSchoolingQualifiedTeacher === true
                ? "yes"
                : draft.formData.flexibleSchoolingQualifiedTeacher === false
                  ? "no"
                  : "unknown"
            }
            onValueChange={(v) =>
              set({
                formData: {
                  ...draft.formData,
                  flexibleSchoolingQualifiedTeacher:
                    v === "yes" ? true : v === "no" ? false : undefined,
                },
              })
            }
            buttons={[
              { value: "yes", label: "教师已确认", disabled: locked },
              { value: "no", label: "尚未确认", disabled: locked },
              { value: "unknown", label: "不适用", disabled: locked },
            ]}
          />
          <Text style={styles.muted}>
            请由学校或灵活学习负责教师核对工作安排与上课安排。
          </Text>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">实际雇主（CE1）</Text>
          {field("雇主法定名称", draft.formData.employerCompanyName, (v) =>
            set({ formData: { ...draft.formData, employerCompanyName: v } }),
          )}
          {field("雇主交易名称", draft.formData.employerTradingName, (v) =>
            set({ formData: { ...draft.formData, employerTradingName: v } }),
          )}
          {field("雇主地址", draft.formData.employerAddress, (v) =>
            set({ formData: { ...draft.formData, employerAddress: v } }),
          )}
          {field("雇主邮编", draft.formData.employerPostcode, (v) =>
            set({ formData: { ...draft.formData, employerPostcode: v } }),
          )}
          {field(
            "雇主电话",
            draft.formData.employerPhone,
            (v) => set({ formData: { ...draft.formData, employerPhone: v } }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            "雇主手机",
            draft.formData.employerMobile,
            (v) => set({ formData: { ...draft.formData, employerMobile: v } }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            "雇主邮箱",
            draft.formData.employerEmail,
            (v) => set({ formData: { ...draft.formData, employerEmail: v } }),
            { keyboardType: "email-address" },
          )}
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">其他雇主与计划 / 实际工时</Text>
          <SegmentedButtons
            value={draft.otherWork.hasOtherWork ? "yes" : "no"}
            onValueChange={(v) => setOther({ hasOtherWork: v === "yes" })}
            buttons={[
              { value: "no", label: "无", disabled: locked },
              { value: "yes", label: "有", disabled: locked },
            ]}
          />
          {draft.otherWork.employers.map((e, i) => (
            <EmployerCard
              key={i}
              employer={e}
              disabled={locked}
              onChange={(x) =>
                setOther({
                  employers: draft.otherWork.employers.map((z, j) =>
                    j === i ? x : z,
                  ),
                })
              }
            />
          ))}
          {draft.otherWork.hasOtherWork ? (
            <Button
              icon="plus"
              onPress={() =>
                setOther({
                  employers: [...draft.otherWork.employers, newEmployer()],
                })
              }
            >
              添加其他雇主
            </Button>
          ) : null}
          {draft.otherWork.plannedIntervals.map((x, i) => (
            <Surface key={`planned-${i}`} style={styles.info} elevation={0}>
              {field("计划开始（ISO，含时区偏移）", x.startUtc, (v) =>
                setOther({
                  plannedIntervals: draft.otherWork.plannedIntervals.map(
                    (z, j) => (j === i ? { ...z, startUtc: v } : z),
                  ),
                }),
              )}
              {field("计划结束（ISO，含时区偏移）", x.endUtc, (v) =>
                setOther({
                  plannedIntervals: draft.otherWork.plannedIntervals.map(
                    (z, j) => (j === i ? { ...z, endUtc: v } : z),
                  ),
                }),
              )}
              {field(
                "计划小时",
                String(x.hours),
                (v) =>
                  setOther({
                    plannedIntervals: draft.otherWork.plannedIntervals.map(
                      (z, j) => (j === i ? { ...z, hours: Number(v) || 0 } : z),
                    ),
                  }),
                { keyboardType: "decimal-pad" },
              )}
              {field("来源", x.source, (v) =>
                setOther({
                  plannedIntervals: draft.otherWork.plannedIntervals.map(
                    (z, j) => (j === i ? { ...z, source: v } : z),
                  ),
                }),
              )}
              <Button
                onPress={() =>
                  setOther({
                    plannedIntervals: draft.otherWork.plannedIntervals.filter(
                      (_, j) => j !== i,
                    ),
                  })
                }
              >
                删除计划区间
              </Button>
            </Surface>
          ))}
          <Button
            onPress={() =>
              setOther({
                plannedIntervals: [
                  ...draft.otherWork.plannedIntervals,
                  { startUtc: "", endUtc: "", hours: 0, source: "declared" },
                ],
              })
            }
          >
            添加计划区间
          </Button>
          {draft.otherWork.actualIntervals.map((x, i) => (
            <Surface key={`actual-${i}`} style={styles.info} elevation={0}>
              {field("实际开始（ISO，含时区偏移）", x.startUtc, (v) =>
                setOther({
                  actualIntervals: draft.otherWork.actualIntervals.map(
                    (z, j) => (j === i ? { ...z, startUtc: v } : z),
                  ),
                }),
              )}
              {field("实际结束（ISO，含时区偏移）", x.endUtc, (v) =>
                setOther({
                  actualIntervals: draft.otherWork.actualIntervals.map(
                    (z, j) => (j === i ? { ...z, endUtc: v } : z),
                  ),
                }),
              )}
              {field(
                "实际小时",
                String(x.hours),
                (v) =>
                  setOther({
                    actualIntervals: draft.otherWork.actualIntervals.map(
                      (z, j) => (j === i ? { ...z, hours: Number(v) || 0 } : z),
                    ),
                  }),
                { keyboardType: "decimal-pad" },
              )}
              {field("来源", x.source, (v) =>
                setOther({
                  actualIntervals: draft.otherWork.actualIntervals.map(
                    (z, j) => (j === i ? { ...z, source: v } : z),
                  ),
                }),
              )}
              <Button
                onPress={() =>
                  setOther({
                    actualIntervals: draft.otherWork.actualIntervals.filter(
                      (_, j) => j !== i,
                    ),
                  })
                }
              >
                删除实际区间
              </Button>
            </Surface>
          ))}
          <Button
            onPress={() =>
              setOther({
                actualIntervals: [
                  ...draft.otherWork.actualIntervals,
                  { startUtc: "", endUtc: "", hours: 0, source: "actual" },
                ],
              })
            }
          >
            添加实际区间
          </Button>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">通勤、接送与最晚时间</Text>
          {field(
            "放学到门店分钟",
            draft.commute.afterSchoolToStoreMinutes,
            (v) => setCommute({ afterSchoolToStoreMinutes: v }),
            { keyboardType: "number-pad" },
          )}
          {field(
            "下班回家分钟",
            draft.commute.homewardMinutes,
            (v) => setCommute({ homewardMinutes: v }),
            { keyboardType: "number-pad" },
          )}
          {field("交通方式", draft.commute.transportMode, (v) =>
            setCommute({ transportMode: v }),
          )}
          {field("接送人", draft.commute.pickupPerson, (v) =>
            setCommute({ pickupPerson: v }),
          )}
          {field(
            "最晚可用交通时间",
            draft.commute.latestTransportLocalTime,
            (v) => setCommute({ latestTransportLocalTime: v }),
            { placeholder: "21:30" },
          )}
          {field(
            "本人最晚可工作时间",
            draft.commute.latestWorkEndLocalTime,
            (v) => setCommute({ latestWorkEndLocalTime: v }),
            { placeholder: "21:00" },
          )}
        </Surface>
        {query.data?.returnFields?.length ? (
          <Surface style={styles.info} elevation={0}>
            <Text variant="titleMedium">待补充字段</Text>
            <Text>
              {query.data.returnFields
                .map(
                  (x) =>
                    returnFieldLabels[x] ??
                    returnFieldLabels[x.replace(/^returnFields\./, "")] ??
                    "待补充资料",
                )
                .filter((x, i, a) => a.indexOf(x) === i)
                .join("、")}
            </Text>
          </Surface>
        ) : null}
        {query.data?.reviewComment ? (
          <HelperText type="info">
            HR 备注：{query.data.reviewComment}
          </HelperText>
        ) : null}
        <Button
          mode="outlined"
          onPress={() => save.mutate()}
          loading={save.isPending}
          disabled={
            locked || save.isPending || invite.isPending || submit.isPending
          }
        >
          保存草稿
        </Button>
        {query.data?.parentSignedAt &&
        query.data.guardianAmendedFields.length ? (
          <Surface style={styles.requestBanner} elevation={0}>
            <Text variant="titleSmall" style={styles.requestTitle}>
              监护人签署时修改了：
              {describeGuardianAmendments(query.data.guardianAmendedFields)}
            </Text>
            <Text style={styles.muted}>
              下面的资料已更新为监护人修改后的内容，不需要你重新填写。
            </Text>
          </Surface>
        ) : null}
        {query.data?.status === "AwaitingParentSignature" ? (
          <Surface style={styles.info} elevation={0}>
            <Text variant="titleMedium">等待监护人签署</Text>
            <Text>
              {query.data.guardianInviteChannel === "email"
                ? query.data.guardianInviteEmailSentAt
                  ? "签署邮件已发送到监护人邮箱。"
                  : "签署邮件未能发出，请改为转发链接或重新发送。"
                : "已生成转发链接，请发给监护人。"}
            </Text>
            <Text style={styles.muted}>
              {query.data.guardianEmailVerifiedAt
                ? "监护人已打开链接并通过邮箱验证码核验，等待签字。"
                : "监护人打开链接后，需要输入发到其邮箱的验证码才能查看和签署。"}
            </Text>
            {!query.data.guardianLinkActive ? (
              <HelperText type="error">链接已过期，请重新发送。</HelperText>
            ) : null}
          </Surface>
        ) : null}
        {!locked &&
        (!query.data ||
          query.data.status === "Draft" ||
          query.data.status === "Returned" ||
          query.data.status === "Signed" ||
          query.data.status === "AwaitingParentSignature") ? (
          <>
            <Button
              mode="contained"
              icon="email-fast-outline"
              onPress={() => {
                if (!validateBeforeInvite()) return;
                invite.mutate(true);
              }}
              loading={invite.isPending && invite.variables === true}
              disabled={invite.isPending || save.isPending}
            >
              {query.data?.status === "AwaitingParentSignature"
                ? "重新发送签署邮件（旧链接作废）"
                : "发送签署邮件给监护人"}
            </Button>
            <Button
              mode="text"
              icon="share-variant"
              onPress={() => {
                if (!validateBeforeInvite()) return;
                invite.mutate(false);
              }}
              loading={invite.isPending && invite.variables === false}
              disabled={invite.isPending || save.isPending}
            >
              监护人收不到邮件？改为转发链接
            </Button>
            {inviteResult?.emailSent ? (
              <HelperText type="info">
                {`签署邮件已发送到 ${inviteResult.maskedGuardianEmail ?? "监护人邮箱"}。请提醒监护人查收（可能在垃圾邮件里），打开链接后会再收到一封验证码邮件。`}
              </HelperText>
            ) : null}
            {inviteResult &&
            !inviteResult.emailSent &&
            inviteResult.emailError ? (
              <HelperText type="error">
                {`邮件发送失败：${inviteResult.emailError}。可以把下面的链接转发给监护人。`}
              </HelperText>
            ) : null}
            {link ? (
              <>
                <Text selectable>{link}</Text>
                <Text style={styles.muted}>
                  监护人打开后仍需输入发到其邮箱的验证码，只有监护人本人能完成签署。
                </Text>
                <Button
                  icon="share-variant"
                  onPress={() => void Share.share({ message: link })}
                >
                  分享签署链接
                </Button>
              </>
            ) : null}
          </>
        ) : null}
        {query.data?.status === "Signed" ? (
          <Button
            mode="contained"
            onPress={() => submit.mutate()}
            loading={submit.isPending}
            disabled={
              dirty || submit.isPending || save.isPending || invite.isPending
            }
          >
            {dirty ? "请先保存新版本" : "提交 HR 审核"}
          </Button>
        ) : null}
        {error ? <HelperText type="error">{error}</HelperText> : null}
        <Text style={styles.muted}>
          合规提醒不阻断排班发布；审核通过后修改会生成新版本，旧版本保留只读历史。
        </Text>
      </ScrollView>
    </SafeAreaView>
  );
}
function ContactCard({
  title,
  value,
  disabled,
  onChange,
}: {
  title: string;
  value: MinorContact;
  disabled: boolean;
  onChange: (x: MinorContact) => void;
}) {
  const f = (
    label: keyof MinorContact,
    caption: string,
    keyboardType?: "phone-pad" | "email-address",
  ) => (
    <TextInput
      mode="outlined"
      label={caption}
      value={value[label]}
      onChangeText={(v) => onChange({ ...value, [label]: v })}
      disabled={disabled}
      keyboardType={keyboardType}
    />
  );
  return (
    <Surface style={styles.card} elevation={0}>
      <Text variant="titleMedium">{title}</Text>
      {f("fullName", "姓名")}
      {f("relationship", "关系")}
      {f("phone", "电话", "phone-pad")}
      {f("mobile", "手机", "phone-pad")}
      {f("email", "邮箱", "email-address")}
      {f("address", "地址")}
      {f("postcode", "邮编")}
    </Surface>
  );
}
function EmployerCard({
  employer,
  disabled,
  onChange,
}: {
  employer: MinorOtherEmployer;
  disabled: boolean;
  onChange: (x: MinorOtherEmployer) => void;
}) {
  const f = (key: keyof MinorOtherEmployer, label: string) => (
    <TextInput
      mode="outlined"
      label={label}
      value={typeof employer[key] === "string" ? (employer[key] as string) : ""}
      onChangeText={(v) => onChange({ ...employer, [key]: v })}
      disabled={disabled}
    />
  );
  return (
    <Surface style={styles.info} elevation={0}>
      {f("companyName", "公司法定名称")}
      {f("tradingName", "交易名称")}
      {f("address", "地址")}
      {f("postcode", "邮编")}
      {f("phone", "电话")}
      {f("email", "邮箱")}
      {Object.keys({
        Sunday: 0,
        Monday: 0,
        Tuesday: 0,
        Wednesday: 0,
        Thursday: 0,
        Friday: 0,
        Saturday: 0,
      }).map((d) => (
        <TextInput
          key={d}
          mode="outlined"
          label={`${d} 小时`}
          value={employer.weeklyHours[d] ?? ""}
          onChangeText={(v) =>
            onChange({
              ...employer,
              weeklyHours: { ...employer.weeklyHours, [d]: v },
            })
          }
          keyboardType="decimal-pad"
          disabled={disabled}
        />
      ))}
    </Surface>
  );
}
const newEmployer = (): MinorOtherEmployer => ({
  companyName: "",
  tradingName: "",
  address: "",
  postcode: "",
  phone: "",
  email: "",
  weeklyHours: {},
});
function durationLabel(start: string, end: string) {
  const toMinutes = (value: string) => {
    const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim());
    return match ? Number(match[1]) * 60 + Number(match[2]) : NaN;
  };
  const a = toMinutes(start),
    b = toMinutes(end);
  if (!Number.isFinite(a) || !Number.isFinite(b) || b <= a) return "—";
  const minutes = b - a;
  return `${Math.floor(minutes / 60)}小时${minutes % 60 ? ` ${minutes % 60}分钟` : ""}`;
}
const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: HB_COLORS.background },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.md, paddingBottom: 48 },
  center: { flex: 1, justifyContent: "center", alignItems: "center" },
  card: {
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderRadius: 12,
  },
  info: {
    padding: HB_SPACING.sm,
    gap: HB_SPACING.sm,
    backgroundColor: "#F8FAFC",
    borderRadius: 10,
  },
  line: { gap: 4 },
  // 店长请求提示：浅黄底 + 警示色标题，与提醒类卡片保持一致。
  requestBanner: {
    padding: HB_SPACING.sm,
    gap: 4,
    backgroundColor: "#FFFAEB",
    borderRadius: 10,
  },
  requestTitle: { color: HB_COLORS.warning },
  muted: { color: "#667085" },
  status: { color: "#1677FF" },
  rowBetween: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
});
