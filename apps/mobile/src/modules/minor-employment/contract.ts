import type {
  MinorContact,
  MinorDateRange,
  MinorEmploymentDraft,
  MinorEmploymentProfile,
  MinorEmploymentState,
  MinorOtherEmployer,
} from "./types";
type Raw = Record<string, unknown>;
const obj = (v: unknown): Raw =>
  v && typeof v === "object" && !Array.isArray(v) ? (v as Raw) : {};
const arr = (v: unknown): Raw[] => (Array.isArray(v) ? v.map(obj) : []);
const str = (v: unknown) =>
  typeof v === "string" ? v : v == null ? "" : String(v);
const bool = (v: unknown) => Boolean(v);
const num = (v: unknown) => (Number.isFinite(Number(v)) ? Number(v) : 0);
const get = (v: Raw, key: string) =>
  v[key] ?? v[key[0].toUpperCase() + key.slice(1)];
const contact = (v: unknown): MinorContact => {
  const x = obj(v);
  const fullName = str(get(x, "fullName") ?? get(x, "name"));
  return {
    fullName,
    name: fullName,
    phone: str(get(x, "phone")),
    email: str(get(x, "email")),
    address: str(get(x, "address")),
    postcode: str(get(x, "postcode")),
    mobile: str(get(x, "mobile")),
    relationship: str(get(x, "relationship")),
  };
};
const dates = (v: unknown): MinorDateRange[] =>
  arr(v).map((x) => ({
    startDate: str(get(x, "startDate")).slice(0, 10),
    endDate: str(get(x, "endDate")).slice(0, 10),
    label: str(get(x, "label")) || undefined,
  }));
function status(v: unknown): MinorEmploymentState {
  const s = str(v).toLowerCase();
  if (s === "pending_hr_review") return "Submitted";
  if (s === "signed_pending_employee_submit") return "Signed";
  if (s === "awaiting_guardian_signature") return "AwaitingParentSignature";
  if (s === "approved") return "Approved";
  if (s.includes("returned")) return "Returned";
  return "Draft";
}
export function mapProfile(payload: unknown): MinorEmploymentProfile {
  const v = obj(payload);
  const cal = obj(get(v, "schoolCalendar"));
  const ow = obj(get(v, "otherWork"));
  const co = obj(get(v, "commute"));
  const fd = obj(get(v, "formData"));
  const contacts = arr(get(v, "contacts"));
  const c1 = contact(contacts[0]);
  const c2 = contact(contacts[1]);
  const weekly = arr(get(cal, "weeklySchedule")).map((x) => ({
    dayOfWeek: num(get(x, "dayOfWeek")) as 0 | 1 | 2 | 3 | 4 | 5 | 6,
    mustAttend: bool(get(x, "mustAttend")),
    startLocalTime: str(get(x, "startLocalTime")),
    endLocalTime: str(get(x, "endLocalTime")),
  }));
  const employers: MinorOtherEmployer[] = arr(get(ow, "employers")).map(
    (x) => ({
      companyName: str(get(x, "companyName")),
      tradingName: str(get(x, "tradingName")),
      address: str(get(x, "address")),
      postcode: str(get(x, "postcode")),
      phone: str(get(x, "phone")),
      email: str(get(x, "email")),
      weeklyHours: Object.fromEntries(
        Object.entries(obj(get(x, "weeklyHours"))).map(([k, val]) => [
          k,
          str(val),
        ]),
      ),
    }),
  );
  const formData = {
    guardianAddress: str(get(fd, "guardianAddress")),
    guardianPostcode: str(get(fd, "guardianPostcode")),
    childGivenName: str(get(fd, "childGivenName")),
    childFamilyName: str(get(fd, "childFamilyName")),
    childAddress: str(get(fd, "childAddress")),
    childPostcode: str(get(fd, "childPostcode")),
    childPhone: str(get(fd, "childPhone")),
    childEmail: str(get(fd, "childEmail")),
    employerCompanyName: str(get(fd, "employerCompanyName")),
    employerTradingName: str(get(fd, "employerTradingName")),
    employerAddress: str(get(fd, "employerAddress")),
    employerPostcode: str(get(fd, "employerPostcode")),
    employerPhone: str(get(fd, "employerPhone")),
    employerMobile: str(get(fd, "employerMobile")),
    employerEmail: str(get(fd, "employerEmail")),
    flexibleSchoolingQualifiedTeacher: get(
      fd,
      "flexibleSchoolingQualifiedTeacher",
    ) as boolean | undefined,
    fields: Object.fromEntries(
      Object.entries(obj(get(fd, "fields"))).map(([k, val]) => [
        k,
        val == null ? "" : str(val),
      ]),
    ),
  };
  return {
    id: str(get(v, "id")),
    employeeId: str(get(v, "userGUID") ?? get(v, "employeeId")),
    storeCode: str(get(v, "storeCode")) || undefined,
    storeTimeZoneId: str(get(v, "storeTimeZoneId")) || undefined,
    state:
      str(get(v, "stateCode") ?? get(v, "state")) === "NSW" ? "NSW" : "QLD",
    formType: str(get(v, "formType")) || undefined,
    dateOfBirth: str(get(v, "dateOfBirth")).slice(0, 10),
    schoolName: str(get(v, "schoolName")),
    schoolYear: str(get(v, "yearLevel") ?? get(v, "schoolYear")),
    completedYear10: get(v, "completedYear10") as boolean | undefined,
    educationStatus: str(get(v, "educationStatus")),
    requiredToBeEnrolled: get(v, "requiredToBeEnrolled") as boolean | undefined,
    educationExemptionVerified: get(v, "educationExemptionVerified") as
      | boolean
      | undefined,
    participationEndDate: str(get(v, "participationEndDate")).slice(0, 10),
    parentName: str(get(v, "guardianName") ?? get(v, "parentName")),
    parentPhone: str(get(v, "guardianPhone") ?? get(v, "parentPhone")),
    parentEmail: str(get(v, "guardianEmail") ?? get(v, "parentEmail")),
    parentRelationship: str(get(v, "guardianRelationship")),
    parentAddress: formData.guardianAddress,
    parentPostcode: formData.guardianPostcode,
    childAddress: formData.childAddress,
    childPostcode: formData.childPostcode,
    childPhone: formData.childPhone,
    childEmail: formData.childEmail,
    emergencyContact: c1,
    backupContact: c2,
    schoolCalendar: {
      schoolProvider: str(get(cal, "schoolProvider")),
      timeZoneId: str(get(cal, "timeZoneId")),
      schoolContactName: str(get(cal, "schoolContactName")),
      schoolContactEmail: str(get(cal, "schoolContactEmail")),
      schoolContactPhone: str(get(cal, "schoolContactPhone")),
      schoolContactPosition: str(get(cal, "schoolContactPosition")),
      schoolContactMobile: str(get(cal, "schoolContactMobile")),
      termRanges: dates(get(cal, "termRanges")),
      holidays: dates(get(cal, "holidays")),
      pupilFreeDays: dates(get(cal, "pupilFreeDays")),
      weeklySchedule: weekly,
    },
    otherWork: {
      hasOtherWork: bool(get(ow, "hasOtherWork")),
      hoursUnknown: bool(get(ow, "hoursUnknown")),
      employers,
      plannedIntervals: arr(get(ow, "plannedIntervals")).map(interval),
      actualIntervals: arr(get(ow, "actualIntervals")).map(interval),
    },
    commute: {
      afterSchoolToStoreMinutes: str(get(co, "afterSchoolToStoreMinutes")),
      homewardMinutes: str(get(co, "homewardMinutes")),
      transportMode: str(get(co, "transportMode")),
      pickupPerson: str(get(co, "pickupPerson")),
      latestTransportLocalTime: str(get(co, "latestTransportLocalTime")),
      latestWorkEndLocalTime: str(get(co, "latestWorkEndLocalTime")),
    },
    formData,
    schoolDays: weekly
      .filter((x) => x.mustAttend)
      .map((x) => String(x.dayOfWeek)),
    schoolHolidayDates: dates(get(cal, "holidays"))
      .map((x) => `${x.startDate}-${x.endDate}`)
      .join(", "),
    commuteMinutes: str(get(co, "afterSchoolToStoreMinutes")),
    commuteMethod: str(get(co, "transportMode")),
    pickupPlan: str(get(co, "pickupPerson")),
    parentSignedAt: str(get(v, "guardianSignedAtUtc")) || undefined,
    parentSignatureName: str(get(v, "guardianSignedName")) || undefined,
    status: status(get(v, "status")),
    version: num(get(v, "version")) || 1,
    revision: num(get(v, "revision") ?? get(v, "rowVersion")) || undefined,
    submittedAt: str(get(v, "submittedAtUtc")) || undefined,
    reviewedAt: str(get(v, "reviewedAtUtc")) || undefined,
    reviewComment: str(get(v, "reviewComment")) || undefined,
    returnFields: Array.isArray(get(v, "returnFields"))
      ? (get(v, "returnFields") as string[])
      : [],
    warningCodes: Array.isArray(get(v, "warningCodes"))
      ? (get(v, "warningCodes") as string[])
      : [],
  };
}
function interval(x: Raw) {
  return {
    startUtc: str(get(x, "startUtc")),
    endUtc: str(get(x, "endUtc")),
    hours: num(get(x, "hours")),
    source: str(get(x, "source")),
  };
}
const day = (v: number) => Math.max(0, Math.min(6, Math.trunc(v)));
export function toPayload(
  d: MinorEmploymentDraft & { version?: number; revision?: number },
) {
  const c = (x: MinorContact) => ({
    fullName: x.fullName,
    phone: x.phone,
    email: x.email || null,
    address: x.address || null,
    postcode: x.postcode || null,
    mobile: x.mobile || null,
    relationship: x.relationship || null,
  });
  const cal = d.schoolCalendar;
  return {
    expectedVersion: d.version,
    expectedRevision: d.revision,
    stateCode: d.state,
    formType: d.state === "NSW" ? "NSW_CONSENT" : "QLD_CE1",
    dateOfBirth: d.dateOfBirth || null,
    schoolName: d.schoolName || null,
    yearLevel: d.schoolYear || null,
    completedYear10: d.completedYear10 ?? null,
    educationStatus: d.educationStatus || null,
    requiredToBeEnrolled: d.requiredToBeEnrolled ?? null,
    educationExemptionVerified: d.educationExemptionVerified ?? null,
    participationEndDate: d.participationEndDate || null,
    schoolCalendar: {
      ...cal,
      weeklySchedule: cal.weeklySchedule.map((x) => ({
        dayOfWeek: day(x.dayOfWeek),
        mustAttend: x.mustAttend,
        startLocalTime: x.startLocalTime || null,
        endLocalTime: x.endLocalTime || null,
      })),
    },
    otherWork: {
      ...d.otherWork,
      employers: d.otherWork.employers.map((x) => ({
        ...x,
        weeklyHours: Object.fromEntries(
          Object.entries(x.weeklyHours).map(([k, v]) => [k, Number(v) || 0]),
        ),
      })),
    },
    commute: {
      ...d.commute,
      afterSchoolToStoreMinutes: num(d.commute.afterSchoolToStoreMinutes),
      homewardMinutes: num(d.commute.homewardMinutes),
    },
    formData: {
      ...d.formData,
      guardianAddress: d.parentAddress,
      guardianPostcode: d.parentPostcode,
      childAddress: d.childAddress,
      childPostcode: d.childPostcode,
      childPhone: d.childPhone,
      childEmail: d.childEmail,
    },
    guardianName: d.parentName,
    guardianPhone: d.parentPhone || null,
    guardianEmail: d.parentEmail || null,
    guardianRelationship: d.parentRelationship || null,
    contacts: [d.emergencyContact, d.backupContact]
      .filter((x) => x.fullName.trim() || x.phone.trim())
      .map(c),
  };
}

export function mapSummary(payload: unknown) {
  const v = obj(payload);
  return {
    id: str(get(v, "id")),
    employeeId: str(get(v, "userGUID") ?? get(v, "employeeId")),
    employeeName: str(get(v, "employeeName") ?? get(v, "userGUID")),
    state:
      str(get(v, "stateCode")) === "NSW" ? ("NSW" as const) : ("QLD" as const),
    status: status(get(v, "status")),
    version: num(get(v, "version")) || 1,
    submittedAt: str(get(v, "submittedAtUtc")) || undefined,
    warningCodes: Array.isArray(get(v, "warningCodes"))
      ? (get(v, "warningCodes") as string[])
      : [],
  };
}
