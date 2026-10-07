import * as Crypto from "expo-crypto";
import { createClientRequestIdHolder, type ClientRequestIdHolder } from "./client-request-id";

/** 运行时 UUID 生成：放在独立文件，让纯逻辑测试不必加载原生模块。 */
export function generateClientRequestId(): string {
  return Crypto.randomUUID();
}

export function createRuntimeClientRequestIdHolder(): ClientRequestIdHolder {
  return createClientRequestIdHolder(generateClientRequestId);
}
