import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import {
  buildKeyParams,
  buildListParams,
  buildPayslipFileName,
  createPayslipsApi,
  PAYSLIPS_BASE_PATH,
  type PayslipsHttp,
} from "./api-core";
import { FIXTURE_COMPANIES, FIXTURE_DETAIL_RESPONSE, FIXTURE_EMAIL_TARGET_RESPONSE, FIXTURE_HOURS_RESPONSE, FIXTURE_LIST_RESPONSE } from "./fixtures";

interface Call {
  method: "get" | "post";
  url: string;
  body?: unknown;
  config?: AxiosRequestConfig;
}

const key = { connectionGuid: "c1", employeeUid: "e1", paymentDate: "2026-10-29", periodStart: "2026-10-19" };

async function run() {
  const calls: Call[] = [];
  let pdfBody: unknown = new TextEncoder().encode("%PDF-1.4 fake").buffer;
  const http: PayslipsHttp = {
    get: async (url, config) => {
      calls.push({ method: "get", url, config });
      const envelope = (data: unknown) => ({ data: { success: true, data, message: "", errorCode: null } });
      if (url.endsWith("/companies")) return envelope(FIXTURE_COMPANIES);
      if (url.endsWith("/detail")) return envelope(FIXTURE_DETAIL_RESPONSE);
      if (url.endsWith("/hours")) return envelope(FIXTURE_HOURS_RESPONSE);
      if (url.endsWith("/email-target")) return envelope(FIXTURE_EMAIL_TARGET_RESPONSE);
      if (url.endsWith("/pdf")) return { data: pdfBody };
      return envelope(FIXTURE_LIST_RESPONSE);
    },
    post: async (url, body, config) => {
      calls.push({ method: "post", url, body, config });
      return { data: { success: true, data: { sentTo: "a***@example.com", copiedToMe: true } } };
    },
  };
  const api = createPayslipsApi(http);
  const controller = new AbortController();
  const noStore = (call: Call) => (call.config?.headers as Record<string, string>)["Cache-Control"] === "no-store";

  // 公司列表
  const companies = await api.fetchCompanies(controller.signal);
  assert.equal(calls[0].url, `${PAYSLIPS_BASE_PATH}/companies`);
  assert.equal(companies.length, 2);
  assert.equal(calls[0].config?.signal, controller.signal);
  assert.equal(noStore(calls[0]), true);

  // 列表：空筛选不传；onlyUnassigned / refresh 只在 true 时传
  const list = await api.fetchList({ connectionGuid: null, storeCode: null, from: "2026-08-01", to: "2026-10-29", keyword: "  ", onlyUnassigned: false, refresh: false });
  assert.equal(calls[1].url, PAYSLIPS_BASE_PATH);
  assert.deepEqual(calls[1].config?.params, { from: "2026-08-01", to: "2026-10-29" });
  assert.equal(list.rows.length, 5);
  assert.equal(noStore(calls[1]), true);
  assert.deepEqual(
    buildListParams({ connectionGuid: "c", storeCode: "1014", from: "2026-08-01", to: "2026-10-29", keyword: " alex ", onlyUnassigned: true, refresh: true }),
    { from: "2026-08-01", to: "2026-10-29", connectionGuid: "c", storeCode: "1014", keyword: "alex", onlyUnassigned: true, refresh: true },
  );

  // 定位参数：detail / hours / email-target / pdf 共用；日期规整，缺 periodStart 不传
  assert.deepEqual(buildKeyParams(key), { connectionGuid: "c1", employeeUid: "e1", paymentDate: "2026-10-29", periodStart: "2026-10-19" });
  assert.deepEqual(buildKeyParams({ ...key, paymentDate: "2026-10-29T00:00:00", periodStart: null }), { connectionGuid: "c1", employeeUid: "e1", paymentDate: "2026-10-29" });

  const detail = await api.fetchDetail(key);
  assert.equal(calls[2].url, `${PAYSLIPS_BASE_PATH}/detail`);
  assert.deepEqual(calls[2].config?.params, buildKeyParams(key));
  assert.equal(detail.employeeUid, "e1", "详情响应不带 employeeUid，用请求 key 补齐");
  assert.equal(detail.lines.length, 5);

  const hours = await api.fetchHours(key);
  assert.equal(calls[3].url, `${PAYSLIPS_BASE_PATH}/hours`);
  assert.equal(hours.totalWorkHours, 36);

  const target = await api.fetchEmailTarget(key);
  assert.equal(calls[4].url, `${PAYSLIPS_BASE_PATH}/email-target`);
  assert.equal(target.maskedEmail, "a***@example.com");

  // 发信：POST email，请求体原样
  const request = { connectionGuid: "c1", employeeUid: "e1", paymentDate: "2026-10-29", to: "x@example.com", copyToMe: true, recipientChangeConfirmed: false };
  const sent = await api.sendEmail(request);
  assert.equal(calls[5].method, "post");
  assert.equal(calls[5].url, `${PAYSLIPS_BASE_PATH}/email`);
  assert.deepEqual(calls[5].body, request);
  assert.deepEqual(sent, { sentTo: "a***@example.com", copiedToMe: true });

  // PDF：arraybuffer + 定位参数，校验 %PDF 魔数后返回 base64
  const base64 = await api.fetchPdfBase64(key, controller.signal);
  assert.equal(calls[6].url, `${PAYSLIPS_BASE_PATH}/pdf`);
  assert.equal(calls[6].config?.responseType, "arraybuffer");
  assert.deepEqual(calls[6].config?.params, buildKeyParams(key));
  assert.equal(Buffer.from(base64, "base64").toString("latin1"), "%PDF-1.4 fake");

  // 200 但不是 PDF（网关改写 / JSON 错误）：按 PAYSLIP_PDF_INVALID 处理
  pdfBody = new TextEncoder().encode('{"success":false}').buffer;
  await assert.rejects(() => api.fetchPdfBase64(key), (error: unknown) => (error as { code?: string }).code === "PAYSLIP_PDF_INVALID");
  pdfBody = "plain string";
  await assert.rejects(() => api.fetchPdfBase64(key), (error: unknown) => (error as { code?: string }).code === "PAYSLIP_PDF_INVALID");

  // success:false 的 200 信封被解成带错误码的异常
  const failing = createPayslipsApi({
    get: async () => ({ data: { success: false, message: "no", errorCode: "MYOB_PAYSLIP_FORBIDDEN" } }),
    post: async () => ({ data: { success: false, message: "no", errorCode: "MYOB_PAYSLIP_EMAIL_SEND_FAILED" } }),
  });
  await assert.rejects(() => failing.fetchDetail(key), (error: unknown) => (error as { code?: string }).code === "MYOB_PAYSLIP_FORBIDDEN");
  await assert.rejects(() => failing.sendEmail(request), (error: unknown) => (error as { code?: string }).code === "MYOB_PAYSLIP_EMAIL_SEND_FAILED");

  // 文件名：Payslip_<姓名>_<支付日>.pdf，空白折成短横线，去掉路径与保留字符
  assert.equal(buildPayslipFileName("ALEX MORGAN", "2026-10-29"), "Payslip_ALEX-MORGAN_2026-10-29.pdf");
  assert.equal(buildPayslipFileName("  Mary  Jane ", "2026-10-29T00:00:00"), "Payslip_Mary-Jane_2026-10-29.pdf");
  assert.equal(buildPayslipFileName("../../etc/passwd", "2026-10-29"), "Payslip_etcpasswd_2026-10-29.pdf");
  assert.equal(buildPayslipFileName("A:B*C?", "2026-10-29"), "Payslip_ABC_2026-10-29.pdf");
  assert.equal(buildPayslipFileName("张三", "2026-10-29"), "Payslip_张三_2026-10-29.pdf");
  assert.equal(buildPayslipFileName("", "2026-10-29"), "Payslip_Employee_2026-10-29.pdf");
}

run();
