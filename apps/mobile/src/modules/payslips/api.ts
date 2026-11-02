// 生产用的接口实例：把 apiClient 注入 createPayslipsApi（apiClient 带登录态、基础地址与 401 刷新）。
import { apiClient } from "@/shared/api/client";
import { createPayslipsApi, type PayslipsHttp } from "./api-core";

export const payslipsApi = createPayslipsApi(apiClient as unknown as PayslipsHttp);
