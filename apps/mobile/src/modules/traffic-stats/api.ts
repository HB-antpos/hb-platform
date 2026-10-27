// 生产用的接口实例：把 apiClient 注入 createTrafficStatsApi（apiClient 带登录态、基础地址与 401 刷新）。
import { apiClient } from "@/shared/api/client";
import { createTrafficStatsApi, type TrafficHttp } from "./api-core";

export const trafficStatsApi = createTrafficStatsApi(apiClient as unknown as TrafficHttp);
