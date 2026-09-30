import assert from "node:assert/strict";
import test from "node:test";

import { PrinterNativeUnavailableError } from "../peripherals/printer/native";

import { createLazyHbPrinterAdapter } from "./lazy-printer-adapter";

test("Expo 打印加载器延迟解析 HbPrinter，缺少原生模块不会使启动崩溃", async () => {
  let loaderCalls = 0;
  let moduleName: string | undefined;
  const adapter = createLazyHbPrinterAdapter((name) => {
    loaderCalls += 1;
    moduleName = name;
    throw new Error("HbPrinter is unavailable on this build.");
  });

  assert.equal(loaderCalls, 0, "创建运行时适配器不得加载或操作硬件");
  assert.equal(await adapter.getStatus(), "unavailable");
  assert.equal(loaderCalls, 1);
  await assert.rejects(
    () => adapter.scan(5_000),
    PrinterNativeUnavailableError,
  );
  await assert.rejects(
    () => adapter.connect("printer-1"),
    PrinterNativeUnavailableError,
  );
  assert.equal(loaderCalls, 1);
  assert.equal(moduleName, "HbPrinter");
});

test("每次扫描前先执行 beforeScan（Android 授权框），再调用原生扫描", async () => {
  const events: string[] = [];
  const adapter = createLazyHbPrinterAdapter(
    () => {
      events.push("load-native");
      throw new Error("HbPrinter is unavailable on this build.");
    },
    async () => {
      events.push("before-scan");
    },
  );

  await assert.rejects(() => adapter.scan(5_000), PrinterNativeUnavailableError);
  await assert.rejects(() => adapter.connect("printer-1"), PrinterNativeUnavailableError);
  await assert.rejects(() => adapter.scan(5_000), PrinterNativeUnavailableError);

  // 连接不触发授权框，只有用户主动扫描时才请求。
  assert.deepEqual(events, ["before-scan", "load-native", "before-scan"]);
});
