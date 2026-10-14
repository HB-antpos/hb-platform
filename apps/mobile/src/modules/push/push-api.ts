// 生产用的接口实例：把 apiClient 注入 createPushDevicesApi（apiClient 带登录态与基础地址）。
// 逻辑都在 push-api-core.ts，那里不依赖 React Native，可以在 Node 下用假客户端测试。
import { apiClient } from "@/shared/api/client";
import { createPushDevicesApi, type PushDevicesHttp } from "./push-api-core";

export const pushDevicesApi = createPushDevicesApi(apiClient as unknown as PushDevicesHttp);
