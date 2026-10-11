import { useCallback, useEffect, useState } from "react";
import type { TFunction } from "i18next";
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
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
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
const returnFieldKeys = [
  "parentContact",
  "parent",
  "emergencyContact",
  "backupContact",
  "schoolCalendar",
  "education",
  "workAvailability",
  "rosterEvidence",
  "otherWork",
  "commutePlan",
  "formData",
];
export function MinorEmploymentScreen() {
  const { t } = useAppTranslation("minorEmployment");
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
    error instanceof Error ? error.message : t("form.errors.server");
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
    onError: (error) =>
      setError(t("form.errors.saveFailed", { message: serverMessage(error) })),
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
    onError: (error) =>
      setError(
        t("form.errors.inviteFailed", { message: serverMessage(error) }),
      ),
  });
  const submit = useMutation({
    mutationFn: () => minorEmploymentApi.submit(query.data?.version ?? 0),
    onSuccess: (x) => {
      qc.setQueryData(key, x);
      setError("");
    },
    onError: (error) =>
      setError(
        t("form.errors.submitFailed", { message: serverMessage(error) }),
      ),
  });
  const validateBeforeInvite = () => {
    if (
      !draft.parentPhone.trim() ||
      !draft.parentEmail.trim() ||
      !draft.emergencyContact.fullName.trim() ||
      !draft.emergencyContact.phone.trim()
    ) {
      setError(t("form.errors.inviteRequired"));
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
          title={t("form.loadFailed")}
          primaryAction={{
            label: t("common.retry"),
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
          {t("common.back")}
        </Button>
        <Text variant="headlineSmall">{t("title")}</Text>
        <Text style={styles.muted}>{t("form.intro")}</Text>
        {(requests.data ?? []).map((request) => (
          <Surface key={request.id} style={styles.requestBanner} elevation={0}>
            <Text variant="titleSmall" style={styles.requestTitle}>
              {request.requestedByName
                ? t("form.requestFrom", { name: request.requestedByName })
                : t("form.requestFromManager")}
            </Text>
            {request.note ? <Text>{request.note}</Text> : null}
            <Text style={styles.muted}>{t("form.requestHint")}</Text>
          </Surface>
        ))}
        {query.data ? (
          <View style={styles.rowBetween}>
            <Text style={styles.status}>
              {t("form.statusLine", {
                status: t(`statuses.${query.data.status}`, {
                  defaultValue: query.data.status,
                }),
                version: query.data.version,
              })}
            </Text>
            <Button compact icon="refresh" onPress={() => void query.refetch()}>
              {t("form.refreshStatus")}
            </Button>
          </View>
        ) : null}
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.stateIdentity")}</Text>
          <SegmentedButtons
            value={draft.state}
            onValueChange={(v) => set({ state: v as "NSW" | "QLD" })}
            buttons={[
              { value: "QLD", label: "QLD", disabled: locked },
              { value: "NSW", label: "NSW", disabled: locked },
            ]}
          />
          {field(
            t("form.fields.dateOfBirth"),
            draft.dateOfBirth,
            (v) => set({ dateOfBirth: v }),
            { placeholder: "YYYY-MM-DD" },
          )}
          {field(t("form.fields.schoolName"), draft.schoolName, (v) =>
            set({ schoolName: v }),
          )}
          {field(t("form.fields.schoolYear"), draft.schoolYear, (v) =>
            set({ schoolYear: v }),
          )}
          {field(t("form.fields.educationStatus"), draft.educationStatus, (v) =>
            set({ educationStatus: v }),
          )}
          <Text>{t("form.fields.completedYear10")}</Text>
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
              { value: "yes", label: t("options.yes"), disabled: locked },
              { value: "no", label: t("options.no"), disabled: locked },
              {
                value: "unknown",
                label: t("options.unknown"),
                disabled: locked,
              },
            ]}
          />
          <Text>{t("form.fields.requiredToBeEnrolled")}</Text>
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
              { value: "yes", label: t("options.yes"), disabled: locked },
              { value: "no", label: t("options.no"), disabled: locked },
              {
                value: "unknown",
                label: t("options.unknown"),
                disabled: locked,
              },
            ]}
          />
          {field(
            t("form.fields.participationEndDate"),
            draft.participationEndDate,
            (v) => set({ participationEndDate: v }),
            { placeholder: "YYYY-MM-DD" },
          )}
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.childGuardian")}</Text>
          {field(
            t("form.fields.childGivenName"),
            draft.formData.childGivenName,
            (v) => set({ formData: { ...draft.formData, childGivenName: v } }),
          )}
          {field(
            t("form.fields.childFamilyName"),
            draft.formData.childFamilyName,
            (v) => set({ formData: { ...draft.formData, childFamilyName: v } }),
          )}
          {field(t("form.fields.childAddress"), draft.childAddress, (v) =>
            set({ childAddress: v }),
          )}
          {field(
            t("form.fields.childPostcode"),
            draft.childPostcode,
            (v) => set({ childPostcode: v }),
            { keyboardType: "number-pad" },
          )}
          {field(
            t("form.fields.childPhone"),
            draft.childPhone,
            (v) => set({ childPhone: v }),
            {
              keyboardType: "phone-pad",
            },
          )}
          {field(t("form.fields.parentName"), draft.parentName, (v) =>
            set({ parentName: v }),
          )}
          {field(
            t("form.fields.parentRelationship"),
            draft.parentRelationship,
            (v) => set({ parentRelationship: v }),
          )}
          {field(
            t("form.fields.parentPhone"),
            draft.parentPhone,
            (v) => set({ parentPhone: v }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            t("form.fields.parentEmail"),
            draft.parentEmail,
            (v) => set({ parentEmail: v }),
            { keyboardType: "email-address", autoCapitalize: "none" },
          )}
          {field(t("form.fields.parentAddress"), draft.parentAddress, (v) =>
            set({ parentAddress: v }),
          )}
          {field(
            t("form.fields.parentPostcode"),
            draft.parentPostcode,
            (v) => set({ parentPostcode: v }),
            { keyboardType: "number-pad" },
          )}
        </Surface>
        <ContactCard
          title={t("form.sections.emergencyContact")}
          value={draft.emergencyContact}
          disabled={locked}
          onChange={(v) => set({ emergencyContact: v })}
        />
        <ContactCard
          title={t("form.sections.extraContact")}
          value={draft.backupContact}
          disabled={locked}
          onChange={(v) => set({ backupContact: v })}
        />
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.schoolCalendar")}</Text>
          {field(
            t("form.school.provider"),
            draft.schoolCalendar.schoolProvider,
            (v) => setCalendar({ schoolProvider: v }),
          )}
          {field(
            t("form.school.timeZone"),
            draft.schoolCalendar.timeZoneId,
            (v) => setCalendar({ timeZoneId: v }),
          )}
          {field(
            t("form.school.contactName"),
            draft.schoolCalendar.schoolContactName,
            (v) => setCalendar({ schoolContactName: v }),
          )}
          {field(
            t("form.school.contactPosition"),
            draft.schoolCalendar.schoolContactPosition,
            (v) => setCalendar({ schoolContactPosition: v }),
          )}
          {field(
            t("form.school.contactPhone"),
            draft.schoolCalendar.schoolContactPhone,
            (v) => setCalendar({ schoolContactPhone: v }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            t("form.school.contactEmail"),
            draft.schoolCalendar.schoolContactEmail,
            (v) => setCalendar({ schoolContactEmail: v }),
            { keyboardType: "email-address" },
          )}
          {field(
            t("form.school.contactMobile"),
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
                    {
                      value: "yes",
                      label: t("form.school.dayAttend", { day: d }),
                      disabled: locked,
                    },
                    {
                      value: "no",
                      label: t("options.notApplicable"),
                      disabled: locked,
                    },
                  ]}
                />
                <Text style={styles.muted}>
                  {t("form.school.classDuration")}
                  {old?.mustAttend
                    ? durationLabel(old.startLocalTime, old.endLocalTime, t)
                    : "—"}
                </Text>
                {field(
                  t("form.school.start"),
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
                  t("form.school.end"),
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
              {field(t("form.school.termStart"), x.startDate, (v) =>
                setCalendar({
                  termRanges: draft.schoolCalendar.termRanges.map((z, j) =>
                    j === i ? { ...z, startDate: v } : z,
                  ),
                }),
              )}
              {field(t("form.school.termEnd"), x.endDate, (v) =>
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
                {t("form.school.removeTerm")}
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
            {t("form.school.addTerm")}
          </Button>
          {draft.schoolCalendar.holidays.map((x, i) => (
            <View key={`holiday-${i}`} style={styles.info}>
              {field(t("form.school.holidayStart"), x.startDate, (v) =>
                setCalendar({
                  holidays: draft.schoolCalendar.holidays.map((z, j) =>
                    j === i ? { ...z, startDate: v } : z,
                  ),
                }),
              )}
              {field(t("form.school.holidayEnd"), x.endDate, (v) =>
                setCalendar({
                  holidays: draft.schoolCalendar.holidays.map((z, j) =>
                    j === i ? { ...z, endDate: v } : z,
                  ),
                }),
              )}
              {field(t("form.school.holidayLabel"), x.label ?? "", (v) =>
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
                {t("form.school.removeHoliday")}
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
            {t("form.school.addHoliday")}
          </Button>
          {draft.schoolCalendar.pupilFreeDays.map((x, i) => (
            <View key={`pupil-${i}`} style={styles.info}>
              {field(t("form.school.pupilFreeDate"), x.startDate, (v) =>
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
                {t("form.school.removePupilFree")}
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
            {t("form.school.addPupilFree")}
          </Button>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.flexible")}</Text>
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
              {
                value: "yes",
                label: t("form.flexible.confirmed"),
                disabled: locked,
              },
              {
                value: "no",
                label: t("form.flexible.notConfirmed"),
                disabled: locked,
              },
              {
                value: "unknown",
                label: t("options.notApplicable"),
                disabled: locked,
              },
            ]}
          />
          <Text style={styles.muted}>{t("form.flexible.note")}</Text>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.employer")}</Text>
          {field(
            t("form.employer.companyName"),
            draft.formData.employerCompanyName,
            (v) =>
              set({ formData: { ...draft.formData, employerCompanyName: v } }),
          )}
          {field(
            t("form.employer.tradingName"),
            draft.formData.employerTradingName,
            (v) =>
              set({ formData: { ...draft.formData, employerTradingName: v } }),
          )}
          {field(
            t("form.employer.address"),
            draft.formData.employerAddress,
            (v) => set({ formData: { ...draft.formData, employerAddress: v } }),
          )}
          {field(
            t("form.employer.postcode"),
            draft.formData.employerPostcode,
            (v) =>
              set({ formData: { ...draft.formData, employerPostcode: v } }),
          )}
          {field(
            t("form.employer.phone"),
            draft.formData.employerPhone,
            (v) => set({ formData: { ...draft.formData, employerPhone: v } }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            t("form.employer.mobile"),
            draft.formData.employerMobile,
            (v) => set({ formData: { ...draft.formData, employerMobile: v } }),
            { keyboardType: "phone-pad" },
          )}
          {field(
            t("form.employer.email"),
            draft.formData.employerEmail,
            (v) => set({ formData: { ...draft.formData, employerEmail: v } }),
            { keyboardType: "email-address" },
          )}
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.otherWork")}</Text>
          <SegmentedButtons
            value={draft.otherWork.hasOtherWork ? "yes" : "no"}
            onValueChange={(v) => setOther({ hasOtherWork: v === "yes" })}
            buttons={[
              { value: "no", label: t("options.haveNot"), disabled: locked },
              { value: "yes", label: t("options.have"), disabled: locked },
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
              {t("form.otherWork.addEmployer")}
            </Button>
          ) : null}
          {draft.otherWork.plannedIntervals.map((x, i) => (
            <Surface key={`planned-${i}`} style={styles.info} elevation={0}>
              {field(t("form.otherWork.plannedStart"), x.startUtc, (v) =>
                setOther({
                  plannedIntervals: draft.otherWork.plannedIntervals.map(
                    (z, j) => (j === i ? { ...z, startUtc: v } : z),
                  ),
                }),
              )}
              {field(t("form.otherWork.plannedEnd"), x.endUtc, (v) =>
                setOther({
                  plannedIntervals: draft.otherWork.plannedIntervals.map(
                    (z, j) => (j === i ? { ...z, endUtc: v } : z),
                  ),
                }),
              )}
              {field(
                t("form.otherWork.plannedHours"),
                String(x.hours),
                (v) =>
                  setOther({
                    plannedIntervals: draft.otherWork.plannedIntervals.map(
                      (z, j) => (j === i ? { ...z, hours: Number(v) || 0 } : z),
                    ),
                  }),
                { keyboardType: "decimal-pad" },
              )}
              {field(t("form.otherWork.source"), x.source, (v) =>
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
                {t("form.otherWork.removePlanned")}
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
            {t("form.otherWork.addPlanned")}
          </Button>
          {draft.otherWork.actualIntervals.map((x, i) => (
            <Surface key={`actual-${i}`} style={styles.info} elevation={0}>
              {field(t("form.otherWork.actualStart"), x.startUtc, (v) =>
                setOther({
                  actualIntervals: draft.otherWork.actualIntervals.map(
                    (z, j) => (j === i ? { ...z, startUtc: v } : z),
                  ),
                }),
              )}
              {field(t("form.otherWork.actualEnd"), x.endUtc, (v) =>
                setOther({
                  actualIntervals: draft.otherWork.actualIntervals.map(
                    (z, j) => (j === i ? { ...z, endUtc: v } : z),
                  ),
                }),
              )}
              {field(
                t("form.otherWork.actualHours"),
                String(x.hours),
                (v) =>
                  setOther({
                    actualIntervals: draft.otherWork.actualIntervals.map(
                      (z, j) => (j === i ? { ...z, hours: Number(v) || 0 } : z),
                    ),
                  }),
                { keyboardType: "decimal-pad" },
              )}
              {field(t("form.otherWork.source"), x.source, (v) =>
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
                {t("form.otherWork.removeActual")}
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
            {t("form.otherWork.addActual")}
          </Button>
        </Surface>
        <Surface style={styles.card} elevation={0}>
          <Text variant="titleMedium">{t("form.sections.commute")}</Text>
          {field(
            t("form.commute.afterSchoolToStore"),
            draft.commute.afterSchoolToStoreMinutes,
            (v) => setCommute({ afterSchoolToStoreMinutes: v }),
            { keyboardType: "number-pad" },
          )}
          {field(
            t("form.commute.homeward"),
            draft.commute.homewardMinutes,
            (v) => setCommute({ homewardMinutes: v }),
            { keyboardType: "number-pad" },
          )}
          {field(
            t("form.commute.transportMode"),
            draft.commute.transportMode,
            (v) => setCommute({ transportMode: v }),
          )}
          {field(
            t("form.commute.pickupPerson"),
            draft.commute.pickupPerson,
            (v) => setCommute({ pickupPerson: v }),
          )}
          {field(
            t("form.commute.latestTransport"),
            draft.commute.latestTransportLocalTime,
            (v) => setCommute({ latestTransportLocalTime: v }),
            { placeholder: "21:30" },
          )}
          {field(
            t("form.commute.latestWorkEnd"),
            draft.commute.latestWorkEndLocalTime,
            (v) => setCommute({ latestWorkEndLocalTime: v }),
            { placeholder: "21:00" },
          )}
        </Surface>
        {query.data?.returnFields?.length ? (
          <Surface style={styles.info} elevation={0}>
            <Text variant="titleMedium">{t("returnFields.heading")}</Text>
            <Text>
              {query.data.returnFields
                .map((x) => {
                  const name = x.replace(/^returnFields\./, "");
                  const known = returnFieldKeys.find(
                    (k) => k === x || k === name,
                  );
                  return known
                    ? t(`returnFields.${known}`)
                    : t("returnFields.fallback");
                })
                .filter((x, i, a) => a.indexOf(x) === i)
                .join(t("common.listSeparator"))}
            </Text>
          </Surface>
        ) : null}
        {query.data?.reviewComment ? (
          <HelperText type="info">
            {t("form.reviewComment", { comment: query.data.reviewComment })}
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
          {t("form.actions.saveDraft")}
        </Button>
        {query.data?.parentSignedAt &&
        query.data.guardianAmendedFields.length ? (
          <Surface style={styles.requestBanner} elevation={0}>
            <Text variant="titleSmall" style={styles.requestTitle}>
              {t("form.amended.title", {
                fields: describeGuardianAmendments(
                  query.data.guardianAmendedFields,
                  (key) => t(`guardianAmended.${key}`, { defaultValue: key }),
                  t("common.listSeparator"),
                ),
              })}
            </Text>
            <Text style={styles.muted}>{t("form.amended.note")}</Text>
          </Surface>
        ) : null}
        {query.data?.status === "AwaitingParentSignature" ? (
          <Surface style={styles.info} elevation={0}>
            <Text variant="titleMedium">{t("form.awaiting.title")}</Text>
            <Text>
              {query.data.guardianInviteChannel === "email"
                ? query.data.guardianInviteEmailSentAt
                  ? t("form.awaiting.emailSent")
                  : t("form.awaiting.emailFailed")
                : t("form.awaiting.linkGenerated")}
            </Text>
            <Text style={styles.muted}>
              {query.data.guardianEmailVerifiedAt
                ? t("form.awaiting.verified")
                : t("form.awaiting.notVerified")}
            </Text>
            {!query.data.guardianLinkActive ? (
              <HelperText type="error">{t("form.awaiting.expired")}</HelperText>
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
                ? t("form.actions.resendEmail")
                : t("form.actions.sendEmail")}
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
              {t("form.actions.shareLink")}
            </Button>
            {inviteResult?.emailSent ? (
              <HelperText type="info">
                {t("form.invite.emailSent", {
                  email:
                    inviteResult.maskedGuardianEmail ??
                    t("form.invite.guardianEmail"),
                })}
              </HelperText>
            ) : null}
            {inviteResult &&
            !inviteResult.emailSent &&
            inviteResult.emailError ? (
              <HelperText type="error">
                {t("form.invite.emailFailed", {
                  error: inviteResult.emailError,
                })}
              </HelperText>
            ) : null}
            {link ? (
              <>
                <Text selectable>{link}</Text>
                <Text style={styles.muted}>{t("form.invite.linkNote")}</Text>
                <Button
                  icon="share-variant"
                  onPress={() => void Share.share({ message: link })}
                >
                  {t("form.actions.shareLinkButton")}
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
            {dirty ? t("form.actions.saveFirst") : t("form.actions.submit")}
          </Button>
        ) : null}
        {error ? <HelperText type="error">{error}</HelperText> : null}
        <Text style={styles.muted}>{t("form.footer")}</Text>
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
  const { t } = useAppTranslation("minorEmployment");
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
      {f("fullName", t("form.contact.name"))}
      {f("relationship", t("form.contact.relationship"))}
      {f("phone", t("form.contact.phone"), "phone-pad")}
      {f("mobile", t("form.contact.mobile"), "phone-pad")}
      {f("email", t("form.contact.email"), "email-address")}
      {f("address", t("form.contact.address"))}
      {f("postcode", t("form.contact.postcode"))}
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
  const { t } = useAppTranslation("minorEmployment");
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
      {f("companyName", t("form.otherEmployer.companyName"))}
      {f("tradingName", t("form.otherEmployer.tradingName"))}
      {f("address", t("form.contact.address"))}
      {f("postcode", t("form.contact.postcode"))}
      {f("phone", t("form.contact.phone"))}
      {f("email", t("form.contact.email"))}
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
          label={t("form.otherEmployer.dayHours", { day: d })}
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
function durationLabel(start: string, end: string, t: TFunction) {
  const toMinutes = (value: string) => {
    const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim());
    return match ? Number(match[1]) * 60 + Number(match[2]) : NaN;
  };
  const a = toMinutes(start),
    b = toMinutes(end);
  if (!Number.isFinite(a) || !Number.isFinite(b) || b <= a) return "—";
  const minutes = b - a;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m ? t("duration.hoursMinutes", { h, m }) : t("duration.hours", { h });
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
