import type { LocalCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";

/** 仅供测试使用的本地现金进出记录夹具。 */
export const CASH_MOVEMENT_FIXTURE_GUID = "6f9619ff-8b86-4011-b42d-00c04fc964aa";
export const CASH_MOVEMENT_FIXTURE_SCOPE = Object.freeze({
  storeCode: "S001",
  deviceCode: "DEV-1",
});

export function cashMovementFixture(
  overrides: Partial<LocalCashMovement> = {},
): LocalCashMovement {
  return {
    movementGuid: CASH_MOVEMENT_FIXTURE_GUID,
    direction: "In",
    reasonCode: "ChangeTopUp",
    amountCents: 5_000,
    note: null,
    voidsMovementGuid: null,
    businessDate: "2026-11-14",
    occurredAtIso: "2026-11-14T02:30:00.000Z",
    cashierId: "cashier-1",
    cashierName: "Cashier One",
    authorizedById: null,
    authorizedByName: null,
    storeCode: CASH_MOVEMENT_FIXTURE_SCOPE.storeCode,
    deviceCode: CASH_MOVEMENT_FIXTURE_SCOPE.deviceCode,
    displayNumber: "CM-0001",
    voidedByMovementGuid: null,
    auditEventId: "7f9619ff-8b86-4011-b42d-00c04fc964bb",
    uploadState: "pending",
    uploadAttempts: 0,
    uploadErrorCode: null,
    uploadedAtIso: null,
    createdAtIso: "2026-11-14T02:30:00.000Z",
    ...overrides,
  };
}
