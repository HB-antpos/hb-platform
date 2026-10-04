import assert from "node:assert/strict";
import {
  mapCandidate,
  mapInviteResult,
  mapProfile,
  mapSummary,
  toPayload,
} from "./contract";

const fixture = {
  Id: 42,
  UserGUID: "employee-42",
  Version: 3,
  Revision: "r3",
  Status: "pending_hr_review",
  StateCode: "NSW",
  FormType: "NSW_CONSENT",
  GuardianName: "Parent",
  GuardianPhone: "0400000000",
  GuardianEmail: "parent@example.com",
  Contacts: [
    {
      FullName: "Backup",
      Phone: "0411111111",
      Relationship: "Aunt",
      Email: "aunt@example.com",
      Address: "3 Main St",
    },
    { FullName: "Second", Phone: "0422222222", Relationship: "Uncle" },
  ],
  OtherWork: {
    HasOtherWork: true,
    Employers: [
      {
        TradingName: "Other Shop",
        Phone: "0733333333",
        WeeklyHours: { Tuesday: 3, Saturday: 4 },
      },
    ],
    PlannedIntervals: [
      {
        StartUtc: "2026-09-22T00:00:00Z",
        EndUtc: "2026-09-22T03:00:00Z",
        Hours: 3,
        Source: "declared",
      },
    ],
    ActualIntervals: [],
  },
  Commute: { AfterSchoolToStoreMinutes: 35, TransportMode: "Train" },
  SchoolCalendar: {
    SchoolContactName: "School office",
    WeeklySchedule: [
      {
        DayOfWeek: 2,
        MustAttend: true,
        StartLocalTime: "09:00",
        EndLocalTime: "15:00",
        Hours: "6",
      },
    ],
    Holidays: [
      { StartDate: "2026-09-21", EndDate: "2026-09-25", Label: "Term break" },
    ],
  },
  FormData: {
    ChildAddress: "1 Main St",
    GuardianAddress: "2 Main St",
    Fields: { consentScope: "retail" },
  },
};
const profile = mapProfile(fixture);
assert.equal(profile.id, "42");
assert.equal(profile.employeeId, "employee-42");
assert.equal(profile.status, "Submitted");
assert.equal(profile.otherWork.hasOtherWork, true);
assert.equal(profile.emergencyContact.name, "Backup");
assert.equal(profile.backupContact.fullName, "Second");
assert.equal(profile.commuteMinutes, "35");
assert.deepEqual(profile.schoolDays, ["2"]);
assert.equal(profile.schoolCalendar.schoolContactName, "School office");
assert.equal(profile.formData.fields.consentScope, "retail");
assert.equal(profile.otherWork.employers[0]?.weeklyHours.Tuesday, "3");
assert.equal(profile.otherWork.plannedIntervals[0]?.hours, 3);
const payload = toPayload(profile);
assert.equal(payload.schoolCalendar.weeklySchedule[0]?.startLocalTime, "09:00");
assert.equal(payload.otherWork.employers[0]?.weeklyHours.Tuesday, 3);
assert.equal(payload.contacts[1]?.address, null);
assert.equal(mapSummary(fixture).state, "NSW");
// 邮件直发与验证状态字段（后端 PascalCase）。
const signing = mapProfile({
  ...fixture,
  Status: "awaiting_guardian_signature",
  GuardianInviteChannel: "Email",
  GuardianInviteEmailSentAtUtc: "2026-10-04T01:00:00Z",
  GuardianEmailVerifiedAtUtc: "2026-10-04T02:00:00Z",
  GuardianTokenActive: true,
});
assert.equal(signing.status, "AwaitingParentSignature");
assert.equal(signing.guardianInviteChannel, "email");
assert.equal(signing.guardianEmailVerifiedAt, "2026-10-04T02:00:00Z");
assert.equal(signing.guardianLinkActive, true);
assert.equal(mapProfile(fixture).guardianLinkActive, false);
// 邮件已发：不回传链接；失败：带回链接与原因。
const emailed = mapInviteResult({
  Version: 2,
  DeliveryChannel: "email",
  EmailSent: true,
  MaskedGuardianEmail: "p***@example.com",
  SigningUrl: "",
});
assert.equal(emailed.emailSent, true);
assert.equal(emailed.signingUrl, undefined);
assert.equal(emailed.maskedGuardianEmail, "p***@example.com");
const failed = mapInviteResult({
  deliveryChannel: "email",
  emailSent: false,
  emailError: "邮件发送失败",
  signingUrl: "https://hotbargain.vip/minor-employment/sign#token=abc",
});
assert.equal(failed.emailSent, false);
assert.equal(failed.emailError, "邮件发送失败");
assert.ok(failed.signingUrl?.includes("#token="));
const candidate = mapCandidate({
  UserGUID: "u1",
  EmployeeName: "Teen",
  Age: 15,
  ComplianceStatus: "draft",
  OpenRequest: { Id: 7, UserGUID: "u1", Status: "open", Note: "本周内" },
});
assert.equal(candidate.age, 15);
assert.equal(candidate.openRequest?.id, 7);
assert.equal(candidate.openRequest?.status, "open");
assert.equal(mapCandidate({ UserGUID: "u2", Age: 16 }).openRequest, undefined);
console.log("minor employment API contract mapping passed");
