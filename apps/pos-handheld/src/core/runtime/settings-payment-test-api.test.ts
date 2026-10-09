import assert from "node:assert/strict";
import test from "node:test";

import type {
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "../api/hbpos-api";

import { HbposSettingsPaymentTestApi } from "./settings-payment-test-api";

class QueueTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];

  public constructor(private readonly responses: unknown[]) {}

  public async request<T>(
    request: HbposTransportRequest,
  ): Promise<HbposTransportResponse<T>> {
    this.requests.push(request);
    const data = this.responses.shift();
    if (data instanceof Error) throw data;
    return {
      status: 200,
      data: data as T,
    };
  }
}

test("Square Production 测试读取候选门店的设备列表并匹配公开 device id", async () => {
  const transport = new QueueTransport([
    {
      success: true,
      data: [
        {
          id: "SQ-1",
          name: "Counter",
          status: "PAIRED",
          locationId: "LOC-1",
        },
      ],
    },
  ]);
  const subject = new HbposSettingsPaymentTestApi(transport);
  const signal = new AbortController().signal;

  await subject.test(
    "square",
    {
      provider: "square",
      square: {
        environment: "Production",
        deviceId: "SQ-1",
        locationId: "LOC-1",
      },
      linkly: null,
    },
    signal,
  );

  assert.deepEqual(transport.requests, [
    {
      method: "GET",
      url: "/api/v1/square/devices",
      params: {
        environment: "Production",
        locationId: "LOC-1",
      },
      signal,
    },
  ]);
});

const linklyInput = { provider: "linkly" as const, square: null,
  linkly: { environment: "Production" as const } };
const resultEnvelope = (data: Record<string, unknown>) => ({ success: true, data });

test("Linkly 已签到时只查询 Status，不重复 Logon", async () => {
  const transport = new QueueTransport([resultEnvelope({ httpStatus: 200,
    succeeded: true, loggedOn: true, responseCode: "T0" })]);
  await new HbposSettingsPaymentTestApi(transport).test("linkly", linklyInput, new AbortController().signal);
  assert.equal(transport.requests.length, 1);
  assert.equal(transport.requests[0]?.url, "/api/v1/linkly/cloud-backend/status-test");
  assert.ok((transport.requests[0]?.timeoutMs ?? 0) > 240_000);
});

test("Linkly 明确未签到时等待 Logon 最终响应，不能被默认 15 秒截断", async () => {
  const transport = new QueueTransport([
    resultEnvelope({ httpStatus: 200, succeeded: false, loggedOn: false, responseCode: "TF" }),
    resultEnvelope({ httpStatus: 200, succeeded: true, responseCode: "00" }),
  ]);
  await new HbposSettingsPaymentTestApi(transport).test("linkly", linklyInput, new AbortController().signal);
  assert.deepEqual(transport.requests.map(request => request.url), [
    "/api/v1/linkly/cloud-backend/status-test", "/api/v1/linkly/cloud-backend/logon-test",
  ]);
  assert.ok((transport.requests[1]?.timeoutMs ?? 0) > 240_000);
});

for (const status of [408, 202, 503]) {
  test(`Linkly Status ${status} 结果未确认，不重发 Logon 或启用`, async () => {
    const transport = new QueueTransport([resultEnvelope({ httpStatus: status, succeeded: false })]);
    await assert.rejects(() => new HbposSettingsPaymentTestApi(transport)
      .test("linkly", linklyInput, new AbortController().signal), { code: "LINKLY_TEST_UNCONFIRMED" });
    assert.equal(transport.requests.length, 1);
  });
}

test("Linkly Logon 超时保持未确认，不自动重放", async () => {
  const transport = new QueueTransport([
    resultEnvelope({ httpStatus: 200, succeeded: false, loggedOn: false, responseCode: "TF" }),
    Object.assign(new Error("private transport details"), { code: "ECONNABORTED" }),
  ]);
  await assert.rejects(() => new HbposSettingsPaymentTestApi(transport)
    .test("linkly", linklyInput, new AbortController().signal), { code: "LINKLY_TEST_UNCONFIRMED" });
  assert.equal(transport.requests.length, 2);
});

test("Linkly 明确离线不会通过或自动发送 Logon", async () => {
  const transport = new QueueTransport([
    resultEnvelope({ httpStatus: 200, succeeded: false, loggedOn: false, responseCode: "PF" }),
  ]);
  await assert.rejects(() => new HbposSettingsPaymentTestApi(transport)
    .test("linkly", linklyInput, new AbortController().signal));
  assert.equal(transport.requests.length, 1);
});

test("Square 候选设备不存在或门店不符时失败关闭", async () => {
  const subject = new HbposSettingsPaymentTestApi(
    new QueueTransport([
      {
        success: true,
        data: [
          {
            id: "SQ-1",
            status: "PAIRED",
            locationId: "OTHER",
          },
        ],
      },
    ]),
  );

  await assert.rejects(
    () =>
      subject.test(
        "square",
        {
          provider: "square",
          square: {
            environment: "Production",
            deviceId: "SQ-1",
            locationId: "LOC-1",
          },
          linkly: null,
        },
        new AbortController().signal,
      ),
    /not available/i,
  );
});

test("Linkly 测试调用 Backend Async logon-test 且只接受 succeeded", async () => {
  const transport = new QueueTransport([
    resultEnvelope({ httpStatus: 200, succeeded: true, responseCode: "T0" }),
    {
      success: true,
      data: {
        httpStatus: 200,
        succeeded: true,
        responseCode: "00",
      },
    },
  ]);
  const subject = new HbposSettingsPaymentTestApi(transport);
  const signal = new AbortController().signal;

  await subject.test(
    "linkly",
    {
      provider: "linkly",
      square: null,
      linkly: { environment: "Production" },
    },
    signal,
  );
  assert.deepEqual(transport.requests.slice(1), [
    {
      method: "POST",
      url: "/api/v1/linkly/cloud-backend/logon-test",
      params: { environment: "Production" },
      signal,
      timeoutMs: 270_000,
    },
  ]);
});

test("Linkly Active logon-test 携带当前终端和选择 revision", async () => {
  const transport = new QueueTransport([
    resultEnvelope({ httpStatus: 200, succeeded: true, responseCode: "T0" }),
    {
      success: true,
      data: { httpStatus: 200, succeeded: true, responseCode: "00" },
    },
  ]);
  const subject = new HbposSettingsPaymentTestApi(transport);
  const signal = new AbortController().signal;

  await subject.test(
    "linkly",
    {
      provider: "linkly",
      square: null,
      linkly: { environment: "Production" },
    },
    signal,
    {
      environment: "Production",
      mode: "Active",
      selectedTerminalId: "terminal-2",
      selectionRevision: 11,
      terminals: [],
    },
  );

  assert.deepEqual(transport.requests.slice(1), [
    {
      method: "POST",
      url: "/api/v1/linkly/cloud-backend/logon-test",
      params: {
        environment: "Production",
        terminalId: "terminal-2",
        selectionRevision: 11,
      },
      signal,
      timeoutMs: 270_000,
    },
  ]);
});

test("支付测试不接受未提供的 provider 配置", async () => {
  const subject = new HbposSettingsPaymentTestApi(
    new QueueTransport([]),
  );
  await assert.rejects(
    () =>
      subject.test(
        "square",
        {
          provider: "linkly",
          square: null,
          linkly: { environment: "Production" },
        },
        new AbortController().signal,
      ),
    /configuration/i,
  );
});

test("Square Sandbox 测试复用官方设备合并和 device: 规范化规则", async () => {
  const transport = new QueueTransport([]);
  const subject = new HbposSettingsPaymentTestApi(transport);

  await subject.test(
    "square",
    {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: " device:9FA747A2-25FF-48EE-B078-04381F7C828F ",
        locationId: " LOC-1 ",
      },
      linkly: null,
    },
    new AbortController().signal,
  );

  assert.deepEqual(transport.requests, []);
});

test("Square Sandbox 官方测试终端不依赖不受支持的 Devices API", async () => {
  const transport = new QueueTransport([
    new Error("Square Sandbox Devices API is unsupported"),
  ]);
  const subject = new HbposSettingsPaymentTestApi(transport);

  await subject.test(
    "square",
    {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: "device:9FA747A2-25FF-48EE-B078-04381F7C828F",
        locationId: "LOC-1",
      },
      linkly: null,
    },
    new AbortController().signal,
  );

  assert.deepEqual(transport.requests, []);
});

test("Square Sandbox 本地测试仍响应已中止的操作", async () => {
  const transport = new QueueTransport([]);
  const subject = new HbposSettingsPaymentTestApi(transport);
  const controller = new AbortController();
  controller.abort();

  await assert.rejects(
    () =>
      subject.test(
        "square",
        {
          provider: "square",
          square: {
            environment: "Sandbox",
            deviceId: "9fa747a2-25ff-48ee-b078-04381f7c828f",
            locationId: "LOC-1",
          },
          linkly: null,
        },
        controller.signal,
      ),
    { name: "AbortError" },
  );
  assert.deepEqual(transport.requests, []);
});

test("Square Sandbox 空门店或非官方测试终端失败关闭", async () => {
  const transport = new QueueTransport([]);
  const subject = new HbposSettingsPaymentTestApi(transport);

  for (const square of [
    {
      environment: "Sandbox" as const,
      deviceId: "9fa747a2-25ff-48ee-b078-04381f7c828f",
      locationId: " ",
    },
    {
      environment: "Sandbox" as const,
      deviceId: "ordinary-production-device",
      locationId: "LOC-1",
    },
  ]) {
    await assert.rejects(
      () =>
        subject.test(
          "square",
          {
            provider: "square",
            square,
            linkly: null,
          },
          new AbortController().signal,
        ),
      /location|required|not available/i,
    );
  }
  assert.deepEqual(transport.requests, []);
});
