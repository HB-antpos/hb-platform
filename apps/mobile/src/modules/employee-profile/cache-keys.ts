type EmployeeProfileIdentitySource = {
  userGuid?: string | null;
  userGUID?: string | null;
  username?: string | null;
} | null;

export function resolveEmployeeProfileIdentity(user: EmployeeProfileIdentitySource) {
  const identity = user?.userGuid || user?.userGUID || user?.username || "anonymous";
  return identity.trim().toLowerCase();
}

export function getEmployeeProfileQueryKey(identity: string) {
  return ["employee-profile", "me", identity] as const;
}

export function getCashierBarcodeQueryKey(identity: string) {
  return ["employee-profile", "cashier-barcode", "me", identity] as const;
}

export function getEmployeeSensitiveChangeQueryKey(identity: string) {
  return ["employee-profile", "sensitive-change-request", "me", identity] as const;
}

export function getEmployeeSensitiveHistoryQueryKey(identity: string) {
  return ["employee-profile", "sensitive-change-requests", "me", identity] as const;
}

export function shouldResetEmployeeProfileDraft(previousIdentity: string, nextIdentity: string) {
  return previousIdentity !== nextIdentity;
}
