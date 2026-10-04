import assert from "node:assert/strict";
import {
  mapHistoryPayload,
  mapHrPagePayload,
  toBackendHrStatus,
} from "./hr-contract";

assert.equal(toBackendHrStatus("Submitted"), "pending_hr_review");
assert.equal(toBackendHrStatus("Approved"), "approved");
assert.equal(toBackendHrStatus("Returned"), "returned");

const page = mapHrPagePayload(
  {
    data: {
      Items: [
        {
          Id: 7,
          UserGUID: "employee-7",
          StateCode: "NSW",
          Status: "pending_hr_review",
          Version: 3,
          WarningCodes: ["SCHOOL_HOURS"],
        },
      ],
      Total: 11,
      Page: 2,
      PageSize: 20,
    },
  },
  1,
  20,
);
assert.equal(page.items[0]?.id, "7");
assert.equal(page.items[0]?.status, "Submitted");
assert.equal(page.total, 11);
assert.equal(page.page, 2);

const history = mapHistoryPayload({
  data: {
    Versions: [
      {
        Version: 3,
        Status: "approved",
        CreatedAt: "2026-09-20T00:00:00Z",
        Audits: [
          {
            Action: "hr_approved",
            ActorLabel: "HR",
            CreatedAt: "2026-09-20T00:01:00Z",
            Metadata: { Comment: "ok" },
          },
        ],
      },
    ],
  },
});
assert.equal(history.length, 1);
assert.equal((history[0] as { Audits: unknown[] }).Audits.length, 1);
assert.equal(
  (history[0] as { Audits: { ActorLabel: string }[] }).Audits[0]?.ActorLabel,
  "HR",
);
console.log("minor employment HR API contract mapping passed");
