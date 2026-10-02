import assert from "node:assert/strict";
import {
  AvailabilityBatchSaveError,
  createAvailabilityBatch,
  verifyAvailabilityBatch,
  type AvailabilityBatchDependencies,
  type AvailabilityBatchWeekPayload,
} from "./availability-batch";
import type {
  AttendanceAvailability,
  AttendanceAvailabilityBatchPayload,
} from "./types";

const basePayload: AttendanceAvailabilityBatchPayload = {
  storeCode: "S001",
  workDates: ["2026-03-02"],
  startTime: "09:00",
  endTime: "17:00",
  note: "front counter",
};

function availability(
  date: string,
  overrides: Partial<AttendanceAvailability> = {},
): AttendanceAvailability {
  return {
    availabilityGuid: `guid-${date}`,
    storeCode: "S001",
    workDate: date,
    startTime: "09:00:00",
    endTime: "17:00:00",
    note: "front counter",
    status: "Submitted",
    ...overrides,
  };
}

function dependencySet(options: {
  getWeek?: AvailabilityBatchDependencies["getWeek"];
  createWeek?: AvailabilityBatchDependencies["createWeek"];
}) {
  const getCalls: [string | undefined, string | undefined][] = [];
  const createCalls: AvailabilityBatchWeekPayload[] = [];
  return {
    getCalls,
    createCalls,
    dependencies: {
      getWeek: async (storeCode, weekStartDate) => {
        getCalls.push([storeCode, weekStartDate]);
        return options.getWeek ? options.getWeek(storeCode, weekStartDate) : [];
      },
      createWeek: async (payload) => {
        createCalls.push(payload);
        return options.createWeek ? options.createWeek(payload) : payload.segments.map((segment) => availability(segment.availableDate));
      },
    } satisfies AvailabilityBatchDependencies,
  };
}

async function testSingleRequestAndCanonicalMatching() {
  const calls = dependencySet({
    getWeek: async () => [availability("2026-03-02", { note: " front counter " })],
  });
  const result = await createAvailabilityBatch(basePayload, calls.dependencies);
  assert.deepEqual(result.map((row) => row.workDate), ["2026-03-02"]);
  assert.equal(calls.createCalls.length, 0, "精确已有条目必须跳过 POST");
}

async function testCrossWeekGroupingAndOrdering() {
  const calls = dependencySet({});
  const payload = {
    ...basePayload,
    workDates: ["2026-03-10", "2026-03-02", "2026-03-10", "2026-03-08"],
  };
  await createAvailabilityBatch(payload, calls.dependencies);
  assert.deepEqual(calls.createCalls.map((call) => call.weekStartDate), ["2026-03-02", "2026-03-09"]);
  assert.deepEqual(calls.createCalls[0]?.segments.map((segment) => segment.availableDate), ["2026-03-02", "2026-03-08"]);
  assert.deepEqual(calls.createCalls[1]?.segments.map((segment) => segment.availableDate), ["2026-03-10"]);
}

async function testPost500ReadbackConfirmsWithoutRetry() {
  let readCount = 0;
  const calls = dependencySet({
    createWeek: async () => {
      throw new Error("500");
    },
    getWeek: async () => {
      readCount += 1;
      return readCount === 1 ? [] : [availability("2026-03-02")];
    },
  });
  const result = await createAvailabilityBatch(basePayload, calls.dependencies);
  assert.deepEqual(result.map((row) => row.workDate), ["2026-03-02"]);
  assert.equal(calls.createCalls.length, 1);
  assert.equal(calls.getCalls.length, 2, "POST 异常后只允许一次回查");
}

async function testPartialConfirmationStopsFutureWeeks() {
  const calls = dependencySet({
    createWeek: async () => {
      throw new Error("network timeout");
    },
    getWeek: async (_storeCode, weekStartDate) =>
      weekStartDate === "2026-03-02" ? [availability("2026-03-02")] : [],
  });
  const payload = { ...basePayload, workDates: ["2026-03-02", "2026-03-03", "2026-03-10"] };
  await assert.rejects(
    createAvailabilityBatch(payload, calls.dependencies),
    (error: unknown) => {
      assert(error instanceof AvailabilityBatchSaveError);
      assert.deepEqual(error.savedDates, ["2026-03-02"]);
      assert.deepEqual(error.uncertainDates, ["2026-03-03"]);
      assert.deepEqual(error.remainingDates, ["2026-03-03", "2026-03-10"]);
      return true;
    },
  );
  assert.equal(calls.createCalls.length, 1, "未知结果必须停止未来周");
  assert.deepEqual(calls.getCalls.map(([, week]) => week), ["2026-03-02", "2026-03-02"]);
}

async function testPreflightFailureDoesNotPost() {
  const calls = dependencySet({
    getWeek: async () => {
      throw new Error("GET failed");
    },
  });
  await assert.rejects(
    createAvailabilityBatch(basePayload, calls.dependencies),
    (error: unknown) => {
      assert(error instanceof AvailabilityBatchSaveError);
      assert.deepEqual(error.savedDates, []);
      assert.deepEqual(error.remainingDates, ["2026-03-02"]);
      assert.deepEqual(error.uncertainDates, []);
      return true;
    },
  );
  assert.equal(calls.createCalls.length, 0, "预检失败时不能 POST");
}

async function testFalseSuccessIsRejectedAndReconciledOnce() {
  const calls = dependencySet({
    createWeek: async () => [availability("2026-03-02", { availabilityGuid: "" })],
    getWeek: async () => [],
  });
  await assert.rejects(
    createAvailabilityBatch(basePayload, calls.dependencies),
    (error: unknown) => {
      assert(error instanceof AvailabilityBatchSaveError);
      assert.deepEqual(error.savedDates, []);
      assert.deepEqual(error.remainingDates, ["2026-03-02"]);
      assert.deepEqual(error.uncertainDates, ["2026-03-02"]);
      return true;
    },
  );
  assert.equal(calls.createCalls.length, 1);
  assert.equal(calls.getCalls.length, 2);
}

async function testReadOnlyVerify() {
  const calls = dependencySet({
    getWeek: async (_storeCode, weekStartDate) =>
      weekStartDate === "2026-03-02" ? [availability("2026-03-03")] : [availability("2026-03-10")],
  });
  const dates = await verifyAvailabilityBatch(
    { ...basePayload, workDates: ["2026-03-10", "2026-03-03"] },
    calls.dependencies,
  );
  assert.deepEqual(dates, ["2026-03-03", "2026-03-10"]);
  assert.equal(calls.createCalls.length, 0, "verify 只能 GET，不能写入");
}

// 不能上班：请求带类型标记；后端回读类型不一致（例如旧后端存成可上班）时不能当作已保存。
async function testUnavailableTypeMustMatch() {
  const unavailablePayload = { ...basePayload, isUnavailable: true };
  const saved = dependencySet({
    createWeek: async (payload) => payload.segments.map((segment) =>
      availability(segment.availableDate, { isUnavailable: true })),
  });
  await createAvailabilityBatch(unavailablePayload, saved.dependencies);
  assert.equal(saved.createCalls[0].segments[0].isUnavailable, true, "提交请求必须带不能上班标记");

  const verified = await verifyAvailabilityBatch(unavailablePayload, dependencySet({
    getWeek: async () => [availability("2026-03-02", { isUnavailable: false })],
  }).dependencies);
  assert.deepEqual(verified, [], "回读到同时段的可上班记录不能确认为不能上班已保存");
}

async function main() {
  await testUnavailableTypeMustMatch();
  await testSingleRequestAndCanonicalMatching();
  await testCrossWeekGroupingAndOrdering();
  await testPost500ReadbackConfirmsWithoutRetry();
  await testPartialConfirmationStopsFutureWeeks();
  await testPreflightFailureDoesNotPost();
  await testFalseSuccessIsRejectedAndReconciledOnce();
  await testReadOnlyVerify();
  console.log("availability-batch.test.ts: ok");
}

void main();
