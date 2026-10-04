export type MinorEmploymentState =
  | "Draft"
  | "AwaitingParentSignature"
  | "Signed"
  | "Submitted"
  | "Approved"
  | "Returned";
export type MinorState = "NSW" | "QLD";
export type MinorDay = 0 | 1 | 2 | 3 | 4 | 5 | 6;
export interface MinorContact {
  fullName: string;
  name?: string;
  phone: string;
  email: string;
  address: string;
  postcode: string;
  mobile: string;
  relationship: string;
}
export type MinorEmergencyContact = MinorContact;
export interface MinorDateRange {
  startDate: string;
  endDate: string;
  label?: string;
}
export interface MinorWeeklyEducation {
  dayOfWeek: MinorDay;
  mustAttend: boolean;
  startLocalTime: string;
  endLocalTime: string;
}
export interface MinorSchoolCalendar {
  schoolProvider: string;
  timeZoneId: string;
  schoolContactName: string;
  schoolContactEmail: string;
  schoolContactPhone: string;
  schoolContactPosition: string;
  schoolContactMobile: string;
  termRanges: MinorDateRange[];
  holidays: MinorDateRange[];
  pupilFreeDays: MinorDateRange[];
  weeklySchedule: MinorWeeklyEducation[];
}
export interface MinorOtherEmployer {
  companyName: string;
  tradingName: string;
  address: string;
  postcode: string;
  phone: string;
  email: string;
  weeklyHours: Record<string, string>;
}
export interface MinorWorkInterval {
  startUtc: string;
  endUtc: string;
  hours: number;
  source: string;
}
export interface MinorOtherWork {
  hasOtherWork: boolean;
  hoursUnknown: boolean;
  employers: MinorOtherEmployer[];
  plannedIntervals: MinorWorkInterval[];
  actualIntervals: MinorWorkInterval[];
}
export interface MinorCommute {
  afterSchoolToStoreMinutes: string;
  homewardMinutes: string;
  transportMode: string;
  pickupPerson: string;
  latestTransportLocalTime: string;
  latestWorkEndLocalTime: string;
}
export interface MinorFormData {
  guardianAddress: string;
  guardianPostcode: string;
  childGivenName: string;
  childFamilyName: string;
  childAddress: string;
  childPostcode: string;
  childPhone: string;
  childEmail: string;
  employerCompanyName: string;
  employerTradingName: string;
  employerAddress: string;
  employerPostcode: string;
  employerPhone: string;
  employerMobile: string;
  employerEmail: string;
  flexibleSchoolingQualifiedTeacher?: boolean;
  fields: Record<string, string>;
}
export interface MinorEmploymentDraft {
  state: MinorState;
  dateOfBirth: string;
  schoolName: string;
  schoolYear: string;
  completedYear10?: boolean;
  educationStatus: string;
  requiredToBeEnrolled?: boolean;
  educationExemptionVerified?: boolean;
  participationEndDate: string;
  parentName: string;
  parentPhone: string;
  parentEmail: string;
  parentRelationship: string;
  parentAddress: string;
  parentPostcode: string;
  childAddress: string;
  childPostcode: string;
  childPhone: string;
  childEmail: string;
  emergencyContact: MinorContact;
  backupContact: MinorContact;
  schoolCalendar: MinorSchoolCalendar;
  otherWork: MinorOtherWork;
  commute: MinorCommute;
  formData: MinorFormData;
}
export interface MinorEmploymentProfile extends MinorEmploymentDraft {
  id: string;
  employeeId: string;
  storeCode?: string;
  storeTimeZoneId?: string;
  formType?: string;
  parentSignedAt?: string;
  parentSignatureName?: string;
  /** 签署链接送达方式：email＝后端直发监护人邮箱，share＝员工转发。 */
  guardianInviteChannel?: "email" | "share";
  guardianInviteEmailSentAt?: string;
  /** 监护人用邮箱验证码核验身份的时间。 */
  guardianEmailVerifiedAt?: string;
  guardianLinkActive: boolean;
  status: MinorEmploymentState;
  version: number;
  revision?: number;
  submittedAt?: string;
  reviewedAt?: string;
  reviewComment?: string;
  returnFields: string[];
  warningCodes: string[];
  signedDocumentUrl?: string;
  schoolDays: string[];
  schoolHolidayDates: string;
  commuteMinutes: string;
  commuteMethod: string;
  pickupPlan: string;
}
export interface MinorEmploymentReviewSummary {
  id: string;
  employeeId: string;
  employeeName: string;
  state: MinorState;
  status: MinorEmploymentState;
  version: number;
  submittedAt?: string;
  warningCodes: string[];
}
export type MinorEmploymentReviewDetail = MinorEmploymentProfile & {
  employeeName: string;
};
export const MINOR_RETURN_FIELDS = [
  "parentContact",
  "emergencyContact",
  "schoolCalendar",
  "education",
  "workAvailability",
  "otherWork",
  "commutePlan",
  "formData",
] as const;
export type MinorReturnField = (typeof MINOR_RETURN_FIELDS)[number];
export const emptyContact = (): MinorContact => ({
  fullName: "",
  phone: "",
  email: "",
  address: "",
  postcode: "",
  mobile: "",
  relationship: "",
});
export const emptyDraft = (): MinorEmploymentDraft => ({
  state: "QLD",
  dateOfBirth: "",
  schoolName: "",
  schoolYear: "",
  educationStatus: "",
  participationEndDate: "",
  parentName: "",
  parentPhone: "",
  parentEmail: "",
  parentRelationship: "",
  parentAddress: "",
  parentPostcode: "",
  childAddress: "",
  childPostcode: "",
  childPhone: "",
  childEmail: "",
  emergencyContact: emptyContact(),
  backupContact: emptyContact(),
  schoolCalendar: {
    schoolProvider: "",
    timeZoneId: "",
    schoolContactName: "",
    schoolContactEmail: "",
    schoolContactPhone: "",
    schoolContactPosition: "",
    schoolContactMobile: "",
    termRanges: [],
    holidays: [],
    pupilFreeDays: [],
    weeklySchedule: [],
  },
  otherWork: {
    hasOtherWork: false,
    hoursUnknown: false,
    employers: [],
    plannedIntervals: [],
    actualIntervals: [],
  },
  commute: {
    afterSchoolToStoreMinutes: "",
    homewardMinutes: "",
    transportMode: "",
    pickupPerson: "",
    latestTransportLocalTime: "",
    latestWorkEndLocalTime: "",
  },
  formData: {
    guardianAddress: "",
    guardianPostcode: "",
    childGivenName: "",
    childFamilyName: "",
    childAddress: "",
    childPostcode: "",
    childPhone: "",
    childEmail: "",
    employerCompanyName: "",
    employerTradingName: "",
    employerAddress: "",
    employerPostcode: "",
    employerPhone: "",
    employerMobile: "",
    employerEmail: "",
    fields: {},
  },
});

/** 发起签署的结果：邮件直发成功时不回传链接，失败或选择转发时才有 signingUrl。 */
export interface MinorGuardianInviteResult {
  version: number;
  revision?: number;
  deliveryChannel: "email" | "share";
  emailSent: boolean;
  maskedGuardianEmail?: string;
  emailError?: string;
  signingUrl?: string;
  expiresAt?: string;
}
/** 店长发起的资料填写请求。 */
export interface MinorComplianceRequest {
  id: number;
  userGUID: string;
  employeeName?: string;
  storeCode?: string;
  status: "open" | "completed" | "cancelled";
  note?: string;
  dueDate?: string;
  requestedByName?: string;
  createdAt?: string;
}
/** 店长视角的未成年员工。 */
export interface MinorManagerCandidate {
  userGUID: string;
  employeeName: string;
  storeCode?: string;
  age: number;
  complianceStatus?: string;
  complianceVersion?: number;
  stateCode?: string;
  openRequest?: MinorComplianceRequest;
}
