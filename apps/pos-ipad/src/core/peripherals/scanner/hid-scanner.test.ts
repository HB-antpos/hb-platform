import assert from "node:assert/strict";

import { createExpoCameraResultAdapter } from "./expo-camera-adapter";
import { HidScannerRouter } from "./hid-scanner";

let now = 1_000;
const events: { value: string; context: string; source: string; category: string }[] = [];
const router = new HidScannerRouter({
  idleMs: 80,
  maxLength: 32,
  now: () => now,
});
router.subscribeRouted((event) => events.push(event));
router.setCaptureActive(true);

router.setContext("product");
for (const character of "1234567890123") {
  router.acceptHidText(character);
}
router.acceptHidText("\r\n");
assert.deepEqual(scanSummary(events.at(-1)), {
  value: "1234567890123",
  context: "product",
  source: "hid",
  category: "product-code",
});

for (let index = 0; index < 500; index += 1) {
  router.acceptHidText(`SKU${index.toString().padStart(4, "0")}\n`);
}
assert.equal(events.length, 501, "500 次连续 HID 扫码不应串码或丢失");
assert.equal(events.at(-1)?.value, "SKU0499");

router.setContext("cashier-login");
router.acceptHidText("CASHIER-01\r\n");
assert.equal(events.at(-1)?.category, "cashier-code");

router.setContext("supervisor-authorization");
router.acceptHidText("SUPERVISOR-01\n");
assert.equal(events.at(-1)?.category, "supervisor-code");

router.setContext("product-search");
router.acceptHidText("PART");
now += 81;
router.resetPartialIfIdle();
router.acceptHidText("NEW-CODE\n");
assert.equal(events.at(-1)?.value, "NEW-CODE", "超时后的半段扫码不得与后续扫码拼接");

router.setContext("product");
router.acceptHidText("OLD");
router.setCaptureActive(false);
assert.equal(router.getCaptureStatus(), "inactive");
router.setCaptureActive(true);
router.acceptHidText("NEW\n");
assert.equal(events.at(-1)?.value, "NEW", "焦点恢复后不得携带旧缓冲");

router.setContext("product");
router.acceptHidText("HALF");
router.pushContext("dialog");
router.acceptHidText("DIALOG-1\n");
assert.deepEqual(scanSummary(events.at(-1)), {
  value: "DIALOG-1",
  context: "dialog",
  source: "hid",
  category: "dialog-code",
});
router.popContext();
router.acceptHidText("PRODUCT-2\n");
assert.equal(events.at(-1)?.context, "product", "弹窗 pop 后应恢复原上下文");
assert.equal(events.at(-1)?.value, "PRODUCT-2");

const releaseProductRoute = router.acquireContext("product");
const releaseSupervisorDialog = router.acquireContext("supervisor-authorization");
releaseProductRoute();
router.acceptHidText("SUPERVISOR-LEASE\n");
assert.equal(
  events.at(-1)?.context,
  "supervisor-authorization",
  "路由卸载不得错误弹出仍显示中的主管授权弹窗上下文",
);
releaseSupervisorDialog();
router.acceptHidText("PRODUCT-AFTER-LEASE\n");
assert.equal(
  events.at(-1)?.context,
  "product",
  "主管授权弹窗关闭后必须恢复可扫码路由的上下文",
);

router.setCaptureContext("emergency-qr");
router.acceptHidText("EMERGENCY.SIGNED.PAYLOAD\n");
assert.equal(events.at(-1)?.category, "emergency-qr", "紧急 QR 必须路由为独立类别");

const privateActivationEventCount = events.length;
router.setCaptureContext("device-activation");
assert.equal(
  router.acceptHidText("HBDEV1-PRIVATE-CODE\n").emitted,
  true,
  "设备开通专用上下文仍须接收 HID 原文",
);
assert.equal(
  events.length,
  privateActivationEventCount,
  "设备开通码不得进入共享 routed 扫码总线",
);

const camera = createExpoCameraResultAdapter(router);
void router.startCamera();
assert.equal(
  camera.onBarcodeScanned({ data: "HBDEV1-CAMERA-PRIVATE" }),
  true,
  "设备开通专用上下文仍须接收相机原文",
);
assert.equal(
  events.length,
  privateActivationEventCount,
  "设备开通码相机结果也不得进入共享 routed 扫码总线",
);
void router.stopCamera();

router.setContext("product");
const quarantinedActivationEventCount = events.length;
assert.equal(
  router.acceptHidText(" h b d e v 1 -DO-NOT-SELL\n").emitted,
  false,
  "商品上下文的 HBDEV1 前缀必须被 HID 隔离",
);
assert.equal(
  router.acceptHidText("HBDEV1-ſECRET\n").emitted,
  false,
  "前缀已经确认后，后续 Unicode 也不得使开通码逃逸到商品总线",
);
void router.startCamera();
assert.equal(
  camera.onBarcodeScanned({ data: "\thb dev1 -do-not-sell\r\n" }),
  false,
  "商品上下文的 HBDEV1 前缀必须被相机隔离",
);
void router.stopCamera();
assert.equal(
  events.length,
  quarantinedActivationEventCount,
  "隔离的开通码不得进入任何商品扫码订阅",
);

router.acceptHidText("A".repeat(33));
router.acceptHidText("GOOD\n");
assert.equal(events.at(-1)?.value, "GOOD", "超长缓冲必须丢弃，不能污染下一码");

/* ---------------- BLE 来源：按场景租约投递 ---------------- */
{
  let bleNow = 50_000;
  const bleEvents: { value: string; context: string; source: string; category: string }[] = [];
  const bleRouter = new HidScannerRouter({ now: () => bleNow });
  bleRouter.subscribeRouted((event) => bleEvents.push(event));
  const portEvents: string[] = [];
  bleRouter.subscribe((event) => portEvents.push(`${event.context}:${event.value}`));

  assert.equal(
    bleRouter.acceptBleText("9313559558795"),
    false,
    "栈顶是无人占用的默认 product 时 BLE 码不得投递（当前页面不接收扫码）",
  );
  assert.equal(bleEvents.length, 0);

  bleRouter.pushContext("dialog");
  assert.equal(
    bleRouter.acceptBleText("PUSHED-DIALOG"),
    false,
    "pushContext 不是租约，BLE 不投递",
  );
  bleRouter.popContext();

  const releaseSales = bleRouter.acquireContext("product");
  bleNow += 1_000;
  assert.equal(
    bleRouter.acceptBleText(" 9313559558795\r"),
    true,
    "页面占用 product 租约时 BLE 码不需要隐藏输入框焦点也能投递",
  );
  assert.deepEqual(scanSummary(bleEvents.at(-1)), {
    value: "9313559558795",
    context: "product",
    source: "ble",
    category: "product-code",
  });
  assert.equal(portEvents.at(-1), "product:9313559558795");

  bleNow += 50;
  assert.equal(bleRouter.acceptBleText("9313559558795"), true);
  assert.equal(
    bleEvents.length,
    2,
    "同一来源的连续重复不得去重（连扫两件同款）",
  );

  // 跨来源去重：同一把枪键盘模式与 BLE 模式都开着，300ms 内同码只算一次。
  bleRouter.setCaptureActive(true);
  bleNow += 1_000;
  bleRouter.acceptHidText("DUAL-MODE-1\n", bleNow);
  assert.equal(bleRouter.acceptBleText("DUAL-MODE-1", bleNow + 120), true);
  assert.equal(
    bleEvents.filter((event) => event.value === "DUAL-MODE-1").length,
    1,
    "300ms 内 HID 与 BLE 的同码只投递一次",
  );
  assert.equal(bleRouter.acceptBleText("DUAL-MODE-1", bleNow + 800), true);
  assert.equal(
    bleEvents.filter((event) => event.value === "DUAL-MODE-1").length,
    2,
    "超过 300ms 的同码是新的一次扫码",
  );
  bleNow += 2_000;
  assert.equal(bleRouter.acceptBleText("CAM-DUP", bleNow), true);
  void bleRouter.startCamera();
  assert.equal(
    bleRouter.acceptCameraText("CAM-DUP", bleNow + 100),
    true,
    "被去重的相机码按已处理返回 true",
  );
  void bleRouter.stopCamera();
  assert.equal(
    bleEvents.filter((event) => event.value === "CAM-DUP").length,
    1,
    "300ms 内 BLE 与相机的同码只投递一次",
  );

  // 主管授权弹窗压在页面上时，BLE 码进入授权场景而不是商品场景。
  const releaseSupervisor = bleRouter.acquireContext("supervisor-authorization");
  bleNow += 1_000;
  bleRouter.acceptBleText("SUPERVISOR-BLE");
  assert.equal(bleEvents.at(-1)?.context, "supervisor-authorization");
  releaseSupervisor();

  // 敏感码：HBDEV1- 不进任何共享订阅；device-activation 场景静默吞掉。
  const beforeSensitive = bleEvents.length;
  assert.equal(bleRouter.acceptBleText("hbdev1-SECRET"), false);
  const releaseActivation = bleRouter.acquireContext("device-activation");
  assert.equal(bleRouter.acceptBleText("HBDEV1-SECRET-2"), true);
  releaseActivation();
  assert.equal(bleEvents.length, beforeSensitive, "开通码不得进入 routed 总线");

  // 注册页直接收注册码：只交给持有租约的私有接收器，原文不做 Unicode trim，也不进任何订阅。
  const sinkValues: string[] = [];
  const portBeforeSink = portEvents.length;
  const releaseCapture = bleRouter.captureDeviceActivation((raw) => sinkValues.push(raw));
  assert.equal(bleRouter.acceptBleText(" HBDEV1-SINK-1\r\n"), true);
  assert.deepEqual(sinkValues, [" HBDEV1-SINK-1"], "交出 BLE 原文，只去回车换行");
  assert.equal(bleRouter.acceptBleText("\r\n"), false, "空码不交给接收器");
  // 相机弹窗后申请的 device-activation 租约压在上面：BLE 码让给相机场景（静默吞掉），注册页收不到。
  const releaseCameraLease = bleRouter.acquireContext("device-activation");
  assert.equal(bleRouter.acceptBleText("HBDEV1-SINK-2"), true);
  assert.deepEqual(sinkValues, [" HBDEV1-SINK-1"]);
  releaseCameraLease();
  assert.equal(bleRouter.acceptBleText("HBDEV1-SINK-3"), true);
  assert.equal(sinkValues.at(-1), "HBDEV1-SINK-3");
  releaseCapture();
  assert.equal(
    bleRouter.acceptBleText("HBDEV1-SINK-4"),
    false,
    "释放后栈顶回到无人占用的默认场景，注册码不再投递",
  );
  assert.equal(sinkValues.length, 2);
  assert.equal(bleEvents.length, beforeSensitive, "注册页接收器不得把注册码放进 routed 总线");
  assert.equal(portEvents.length, portBeforeSink, "注册页接收器不得把注册码放进 ScannerPort 订阅");

  // 测试独占：scanner-test 租约下所有来源的码只以 scanner-test 场景投递，且不去重。
  const releaseTest = bleRouter.acquireContext("scanner-test");
  bleNow += 1_000;
  const portBeforeTest = portEvents.length;
  bleRouter.acceptHidText("TEST-1\n", bleNow);
  bleRouter.acceptBleText("TEST-1", bleNow + 50);
  void bleRouter.startCamera();
  bleRouter.acceptCameraText("TEST-CAM", bleNow + 60);
  void bleRouter.stopCamera();
  const testEvents = bleEvents.filter((event) => event.context === "scanner-test");
  assert.deepEqual(
    testEvents.map((event) => `${event.source}:${event.value}:${event.category}`),
    [
      "hid:TEST-1:scanner-test",
      "ble:TEST-1:scanner-test",
      "camera:TEST-CAM:scanner-test",
    ],
    "测试独占时三种来源都只进 scanner-test，且如实保留双模式重复",
  );
  assert.equal(
    portEvents.length,
    portBeforeTest,
    "测试场景的码不得广播给通用 ScannerPort 订阅者",
  );
  releaseTest();
  bleNow += 1_000;
  bleRouter.acceptBleText("AFTER-TEST");
  assert.equal(bleEvents.at(-1)?.context, "product", "测试关闭后恢复页面场景");

  releaseSales();
  assert.equal(
    bleRouter.acceptBleText("NO-PAGE"),
    false,
    "页面释放租约后 BLE 码不再投递",
  );
}

console.log("hid-scanner.test.ts: ok");

function scanSummary(event: (typeof events)[number] | undefined) {
  if (!event) {
    return undefined;
  }
  return {
    value: event.value,
    context: event.context,
    source: event.source,
    category: event.category,
  };
}
