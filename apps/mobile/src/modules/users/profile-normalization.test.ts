import type { StoreUserProfile } from "./types";
import { normalizeStoreUserProfile } from "./profile-normalization";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

const profile: StoreUserProfile = normalizeStoreUserProfile({
  UserGuid: "user-1",
  UserName: "staff001",
  FullName: "Li Wei",
  Email: "li.wei@example.com",
  Phone: "0412000000",
  Status: 1,
  StoreCode: "S001",
  StoreName: "Downtown Central",
  EmploymentType: "partTime",
  Gender: "female",
  IdentityId: "SM-2049",
  Birthday: "1994-10-12",
  AvatarUrl: "https://example.com/avatar.jpg",
  Address: "123 Sample Street",
  BankBsb: "062000",
  BankAccountNumber: "12345678",
  SuperannuationCompanyName: "Aware Super",
  SuperannuationCompanyCode: "AWARE",
  SuperannuationAccountNumber: "SUPER-001",
  CreatedAt: "2024-01-01T08:00:00Z",
  LastLoginTime: "2024-05-01T09:15:00Z",
  LastLoginIp: "203.0.113.9",
  MustChangePassword: true,
});

assertEqual(profile.userGUID, "user-1", "normalizes user guid");
assertEqual(profile.username, "staff001", "normalizes username");
assertEqual(profile.fullName, "Li Wei", "normalizes full name");
assertEqual(profile.storeName, "Downtown Central", "normalizes store name");
assertEqual(profile.phone, "0412000000", "normalizes phone");
assertEqual(profile.employmentType, "partTime", "normalizes employment type");
assertEqual(profile.gender, "female", "normalizes gender");
assertEqual(profile.birthday, "1994-10-12", "normalizes birthday");
assertEqual(profile.identityId, "SM-2049", "normalizes identity id");
assertEqual(profile.bankBsb, "062000", "normalizes bank bsb");
assertEqual(profile.superannuationAccountNumber, "SUPER-001", "normalizes super account");
assertEqual(profile.lastLoginTime, "2024-05-01T09:15:00Z", "normalizes last login");
assertEqual(profile.lastLoginIp, "203.0.113.9", "normalizes last login ip");
assertEqual(profile.mustChangePassword, true, "normalizes must-change-password flag");
assertEqual(normalizeStoreUserProfile({ UserGuid: "user-2" }).mustChangePassword, false, "missing flag defaults to false");
