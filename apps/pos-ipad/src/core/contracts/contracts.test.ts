import assert from "node:assert/strict";
import test from "node:test";

import {
  canTransitionOrder,
  canTransitionPaymentAttempt,
  createAud,
  CustomerDisplaySnapshotSchema,
  normalizeCardSyncEvidence,
  normalizeLineSyncProvenance,
  parseAud,
} from "./index";

test("money only accepts safe integer cents", () => {
  assert.deepEqual(createAud(785), { currency: "AUD", cents: 785 });
  assert.equal(parseAud("7.85").cents, 785);
  assert.throws(() => createAud(7.85), /integer cents/);
  assert.throws(() => parseAud("7.851"), /two decimal places/);
});

test("customer display snapshot rejects sensitive and stale-shaped fields", () => {
  const validSnapshot = {
    revision: 2,
    mode: "cart",
    items: [
      {
        name: "Parity item",
        quantity: "1",
        amount: { currency: "AUD", cents: 785 },
      },
    ],
    gst: { currency: "AUD", cents: 71 },
    discount: { currency: "AUD", cents: 0 },
    total: { currency: "AUD", cents: 785 },
    change: { currency: "AUD", cents: 0 },
    advert: null,
  };

  assert.equal(CustomerDisplaySnapshotSchema.parse(validSnapshot).revision, 2);
  assert.throws(
    () => CustomerDisplaySnapshotSchema.parse({ ...validSnapshot, cashierToken: "secret" }),
    /unrecognized key/i,
  );
  assert.throws(
    () => CustomerDisplaySnapshotSchema.parse({ ...validSnapshot, revision: 1.5 }),
    /expected int/i,
  );
});

test("order state machine preserves recoverable and blocked states", () => {
  assert.equal(canTransitionOrder("Draft", "Completing"), true);
  assert.equal(canTransitionOrder("Completing", "PendingSync"), true);
  assert.equal(canTransitionOrder("Syncing", "Blocked403"), true);
  assert.equal(canTransitionOrder("Blocked403", "Syncing"), false);
  assert.equal(canTransitionOrder("Synced", "PendingSync"), false);
});

test("payment state machine never permits Unknown to create a fresh attempt", () => {
  assert.equal(canTransitionPaymentAttempt("Created", "Submitted"), true);
  assert.equal(canTransitionPaymentAttempt("Submitted", "Unknown"), true);
  assert.equal(canTransitionPaymentAttempt("Unknown", "Pending"), true);
  assert.equal(canTransitionPaymentAttempt("Unknown", "Submitted"), false);
  assert.equal(canTransitionPaymentAttempt("Approved", "Submitted"), false);
});

test("card sync evidence：zeller 通道只能配 Zeller 这个 Processor，反过来也一样", () => {
  const zeller = {
    version: 1,
    provider: "zeller",
    operation: "purchase",
    processor: "Zeller",
    txnRef: "65c4b343-03e6-49ef-a54d-012e3f2c222c",
    authCode: "456321",
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "1234",
    merchantId: "CAID-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: null,
    bankDateTimeIso: "2026-11-05T11:24:01+10:00",
    amountCents: 500,
    refundReference: null,
  } as const;

  const evidence = normalizeCardSyncEvidence(zeller);
  assert.equal(evidence.provider, "zeller");
  assert.equal(evidence.processor, "Zeller");
  assert.equal(evidence.bankDateTimeIso, "2026-11-05T01:24:01.000Z");

  // 不能让 Zeller 的交易以别家的名义同步，也不能让别家的交易冒用 Zeller。
  for (const processor of ["Square", "ANZ", "Manual"]) {
    assert.throws(() => normalizeCardSyncEvidence({ ...zeller, processor }), /provider processor/i);
  }
  for (const provider of ["square", "linkly-cloud"]) {
    assert.throws(() => normalizeCardSyncEvidence({ ...zeller, provider }), /provider processor/i);
  }
  // 刷卡机时间没有时区时不能进证据（适配层要先按 UTC 补上）。
  assert.throws(
    () => normalizeCardSyncEvidence({ ...zeller, bankDateTimeIso: "2026-11-05T11:24:01" }),
    /timezone/i,
  );
});

test("card sync evidence freezes a strict protected whitelist and rejects PAN or raw payload", () => {
  const evidence = normalizeCardSyncEvidence({
    version: 1,
    provider: "linkly-cloud",
    operation: "refund",
    processor: "ANZ",
    txnRef: "TXN-1",
    authCode: "AUTH-1",
    cardType: "VISA",
    cardBin: null,
    maskedCardNumber: "411111******1234",
    merchantId: "MID-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: "123456",
    bankDateTimeIso: "2026-07-28T10:11:12+10:00",
    amountCents: 800,
    refundReference: "RFN-1",
  });

  assert.equal(evidence.bankDateTimeIso, "2026-07-28T00:11:12.000Z");
  assert.equal(evidence.amountCents, 800);
  assert.equal(Object.isFrozen(evidence), true);
  assert.throws(
    () =>
      normalizeCardSyncEvidence({
        ...evidence,
        maskedCardNumber: "4111111111111234",
      }),
    /masked card number/i,
  );
  assert.throws(
    () =>
      normalizeCardSyncEvidence({
        ...evidence,
        processor: "Square",
      }),
    /provider processor/i,
  );
  assert.throws(
    () =>
      normalizeCardSyncEvidence({
        ...evidence,
        amountCents: -800,
      }),
    /positive integer cents/i,
  );
  assert.throws(
    () =>
      normalizeCardSyncEvidence({
        ...evidence,
        rawPayload: "{\"pan\":\"4111111111111234\"}",
      }),
    /unsupported field/i,
  );
});

test("line sync provenance preserves the backend sale identity without accepting inferred values", () => {
  const provenance = normalizeLineSyncProvenance({
    referenceCode: " SET-001 ",
    priceSource: 2,
  });

  assert.deepEqual(provenance, {
    referenceCode: "SET-001",
    priceSource: 2,
  });
  assert.equal(Object.isFrozen(provenance), true);
  assert.deepEqual(
    normalizeLineSyncProvenance({
      referenceCode: null,
      priceSource: 0,
    }),
    { referenceCode: null, priceSource: 0 },
  );
  assert.throws(
    () =>
      normalizeLineSyncProvenance({
        referenceCode: "SET-001",
        priceSource: 5,
      }),
    /backend price source/i,
  );
  assert.throws(
    () =>
      normalizeLineSyncProvenance({
        referenceCode: "   ",
        priceSource: 2,
      }),
    /reference code/i,
  );
  assert.throws(
    () =>
      normalizeLineSyncProvenance({
        referenceCode: "SET-001",
        priceSource: 2,
        inferredFromCurrentCatalog: true,
      }),
    /unsupported field/i,
  );
});
