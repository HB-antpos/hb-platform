# iPad POS / 移动端：BLE 蓝牙扫码枪连接与测试（方案）

> 状态：方案 + 设计图，未开始实现。范围只含 **iPad POS（apps/pos-ipad）** 和 **移动端 App（apps/mobile）**。
> WPF 收银端不做 BLE（2026-11-21 用户确认）；手持 POS（pos-handheld）自带扫码头，不在范围内。

## 1. 目标

1. 在设置里的设备区（iPad「设置 → 外设」，手机「设置 → 设备与打印」）新增「蓝牙扫码枪」：搜索、连接、断开、更换，以及**测试扫码**。
2. 连接方式是 **BLE 数据通道（GATT notify）**：App 自己连接扫码枪，不靠系统把它当键盘。
3. 连好后**所有扫码页面直接可用**，并且**不和现有扫码冲突**：键盘模式扫码枪、相机扫码、蓝牙打印机都照常工作。

## 2. 先讲清两种「蓝牙扫码枪」

| | 键盘模式（HID） | BLE 数据模式（本方案新增） |
|---|---|---|
| 怎么连 | 系统「设置 → 蓝牙」配对 | App 内搜索连接 |
| App 怎么收 | 当成键盘打字（iPad 屏幕外隐藏输入框；手机用 ExpoKeyEvent 或隐藏输入框） | 订阅 GATT 通知，直接收到整码 |
| 现状 | **两端都已支持** | 两端都没有 |
| 副作用 | iPad 连上外接键盘后屏幕键盘会被系统收起；必须有输入框聚焦才能收到 | 不占输入框、不影响屏幕键盘、在原生 Modal 里也能收到 |

多数蓝牙扫码枪出厂是键盘模式，要扫说明书上的设置码才能切到 BLE 模式。**同一把枪不要两种模式同时开**（会进两次码，靠去重兜底，见 §5）。

各厂商 BLE 模式的数据通道 UUID 不统一，常见的有：

| 名称 | Service | Notify 特征 |
|---|---|---|
| Nordic UART（NUS） | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` | `6E400003-…` |
| HM-10 / 通用透传 | `FFE0` | `FFE1` |
| ISSC / Microchip 透传 | `49535343-FE7D-4AE5-8FA9-9FAFD205E455` | `49535343-1E4D-4BD9-BA61-23C647249616` |
| 怡芯（yichip）方案，门店背夹实测 | `FEEA` | `2AA1` |
| 其他厂商私有 | 不固定 | 用「嗅探」自动学会（见 §3.1） |

> 斑马（Zebra）、霍尼韦尔（Honeywell）部分型号的 BLE 只走自家 SDK 协议，不在本期范围；这类枪用键盘模式即可。

### 2.1 已知型号：二维无线蓝牙手机背夹（说明书编号 NO. S2DES3050HFV0）

2026-11-21 用户提供的门店型号说明书，要点和设置码（Code128 原文由说明书照片解码得到，屏幕重画后再反解核对一致）：

| 项目 | 说明书内容 | 对方案的影响 |
|---|---|---|
| 通讯接口 | HID、BLE、SPP 三种，**出厂默认 HID** | 必须先扫「BLE模式」设置码；SPP 是经典蓝牙串口，iOS 不支持（需 MFi），本方案不用 |
| BLE 广播名 | 固定 `BarCode Scanner BLE` | **多把枪同名**：列表按设备 ID 尾号区分，连接后可起备注名（如「1 号背夹」） |
| 数据通道 | 说明书未写 UUID | **已实测**：Service `FEEA` / Notify `2AA1`（见下方「Mac 实测结果」），写进型号档案；其他型号仍走「嗅探学习」 |
| 休眠时间 | 说明书写出厂 **1 分钟**，可选 2 / 5 / 10 分钟 / 不休眠 | 休眠即断开 BLE：要做「挂起式重连」（见 §3.1），界面上休眠显示灰色「休眠中」，**不算异常**；推荐改成 10 分钟。实测这把枪约 5 分钟才断（可能被改过设置），所以休眠判断不能写死 1 分钟 |
| 结束符 | 回车 / 回车换行 / 不添加 | 推荐「回车」；「不添加」时靠空闲超时判断一个码结束 |
| 传输速度 | 快速 / 中速 / 低速 / 超低速 | 低速时分包间隔变大，空闲超时不能写死 60ms（见 §3.1） |
| 扫描模式 | 手动（默认）/ 连续 / 感应 | 推荐手动；连续 / 感应会反复读同一个码 |
| iOS 系统键盘控制 | 「弹起 iOS 系统键盘」设置码 | 这正是 HID 模式在 iPad 上收起屏幕键盘的老问题；BLE 模式下不存在 |
| 外形 | 手机背夹，扫码窗口朝外 | **扫不到装着它的那台手机的屏幕**：手机端引导扫纸质说明书；iPad 或另一台设备可以在屏幕上显示设置码给它扫 |

| 设置码 | Code128 内容 |
|---|---|
| 恢复出厂设置 | `>JD9FF55<` |
| HID 模式（默认） | `%%SpecCodeAA` |
| **BLE 模式** | `%%SpecCodeAC` |
| HID 配对 / 断开 | `%%SpecCode99` |
| 弹起 iOS 系统键盘 | `%%SpecCode1A` |
| 扫描：手动（默认） / 连续 / 感应 | `>RUNMOD0<` / `>RUNMOD2<` / `>RUNMOD3<` |
| 结束符：回车 / 回车换行 / 不添加 | `>J606101<` / `>J606121<` / `>J606100<` |
| 大小写：不转换（默认）/ 强制大写 / 强制小写 / 互换 | `%%SpecCodeA5` / `A4` / `A3` / `A6`（前缀同为 `%%SpecCode`） |
| 休眠：1（默认）/ 2 / 5 / 10 分钟 / 不休眠 | `%%SpecCode31` / `32` / `33` / `34` / `36` |
| 传输速度：快 / 中 / 低 / 超低 | `%%SpecCodeB0` / `B1` / `B2` / `B3` |
| 显示版本信息 | `>VERSION<` |

> SPP 模式的设置码照片里没解出来，本方案也用不到。

#### Mac 实测结果（2026-11-21，CoreBluetooth 只读探测，未写任何特征）

| 项目 | 实测 |
|---|---|
| 广播 | 名字 `BarCode Scanner BLE`，广播服务 `FEEA`，厂商数据 `59 43 AA A8 02 05 26 F1`（前两字节 `5943` = "YC"，疑似每台不同，可用来区分同名枪，待第二把枪核对） |
| 芯片 / 设备信息（`180A`） | 厂商 `Flagtrip`、型号 `yichip`（怡芯）、序列号 `com3`、固件 / 软件 `1.0`；System ID 全 0，**不能用来区分多把枪** |
| 版本码回复 | `HW:V1.00;SW:HD210_V2Nx_V1.8.7.0;Date:2025.12.29;` |
| 服务 | `1804`（`2A07` 发射功率）、`180F` 电池、`180A` 设备信息、`FEEA` 私有 |
| **数据通道** | `FEEA` → `2AA1`（read / notify / indicate），订阅成功；扫码数据全部从这里来 |
| 命令通道（不用） | `FEEA` → `2AA2`（write）；`FEC9`（read，全 0）。本方案不往枪里写东西 |
| 电量 | `180F/2A19` 可读（读到 70%、之后 65%）；**订阅推送失败**（没有 CCCD），只能定时读，建议连上时读一次、之后每 5 分钟读一次 |
| 每包长度 | MTU 23，**每包最多 20 字节** |
| 商品码 | `9528503822012` 一包到齐，结尾 **`0D 0D`（两个回车）** |
| 长二维码 | 127 字符的网址拆成 8 包、共 119ms；包间最长 **62ms** 空档（旧的 60ms 空闲超时会把它切断） |
| 版本码回复结尾 | `0A 0D`（换行 + 回车），和扫码时的 `0D 0D` 不一样 |
| 休眠 / 唤醒 | 最后一次扫码约 307 秒后断开（错误码 6「连接超时」= 枪关了射频）；**挂起的 `connect()` 在按扳机后自动连回**，无需重新扫描；连上约 2 秒内完成服务发现与重新订阅，之后扫码正常 |

结论：这款按「型号档案」直连 `FEEA/2AA1`，不需要嗅探；电量走定时读；拼帧必须丢弃空码，并容忍结尾 `CR CR` / `LF CR`。

**型号档案**：共享包里为这款建一个档案（匹配广播名 + 学到的数据通道 UUID），内含上表设置码、出厂休眠时间（用于判断「断开是不是正常休眠」）和推荐设置。认出这个型号时，App 才显示「设置码」页：必扫 BLE 模式，推荐扫回车、10 分钟休眠、手动模式。其他型号只提示「扫各自说明书」。

## 3. 总体架构

### 3.1 共享纯 TS 包 `packages/ble-scanner-core`（新增，不依赖 React Native）

两端共用，放协议无关的逻辑，用 `node:test` 直接测。命名不用 `pos-*` 前缀，避免触发 `migration-state` 哈希锁。

> **实现时的调整（2026-11-21）**：`apps/mobile` 不在根 npm workspaces 里，没法引用 `packages/*`。所以没有建共享包，改成两端各放一份逐字相同的文件：`ble-scanner-core.ts`（纯逻辑）、`ble-scanner-controller.ts`（连接控制器，蓝牙库通过接口注入）、`ble-plx-transport.ts`（react-native-ble-plx 适配层）。iPad 放在 `apps/pos-ipad/src/core/peripherals/scanner/ble/`，手机放在 `apps/mobile/src/modules/scanner/ble/`。由 `apps/mobile/src/modules/scanner/ble/ble-shared-sync.test.ts` 校验两份一致；单元测试放在 iPad 一侧。

- **profiles**：已知数据通道表（上表），按 service UUID 识别并标「推荐」。
- **帧拼接 `BleScanFrameAssembler`**：BLE 每包通常只有 20 字节，一个码会被拆成几包。规则：
  - 遇 CR / LF / CRLF / Tab 结束一帧；
  - 没有结束符时靠空闲超时结束：默认 120ms；**一旦见过结束符，就只认结束符**，空闲超时放宽到 500ms 只用来清残码（避免「低速」传输下分包间隔大，把一个码切成两段）；
  - **连续结束符之间的空码直接丢弃**（实测背夹每个码结尾是 `CR CR`，版本回复结尾是 `LF CR`，不丢会多出一次「空扫码」）；
  - 最长 256 字符，超长丢弃；去掉控制字符；按 UTF-8 解码，失败退回 Latin-1；
  - 记录分包数、首包到结束的耗时，供测试页显示。
- **嗅探学习**：未知协议的设备连接后订阅所有可通知的特征，**第一次收到像条码的数据的那个特征**就是数据通道，写进保存的配置，下次直接订阅它。
- **重连状态机**：`idle → connecting → discovering → ready`；断开后**挂起一个不超时的连接请求**：iOS 的 `connect()` 本身不会超时，Android 用 `autoConnect=true`。扫码枪按扳机唤醒、重新广播时，系统会立刻连上，约 1 秒内就绪，不用 App 轮询扫描。App 回到前台时重新挂一次。
  - 断开时如果距离上次扫码已经超过型号档案里的休眠时间，就标为 `sleeping`（灰色「休眠中」，不提示、不计入需处理）；否则标为 `reconnecting`；超过休眠时间仍然连不上，才标「需处理」。
  - 唤醒的那一下扳机多半不出码，测试页和说明里提示「唤醒后再扫一次」。
- **设置码过滤**：正常情况下设置码由扫码枪自己处理，不会发出来。为防万一，路由丢弃 `%%SpecCode…` 和 `>…<` 形式的码，不进业务页面；测试页照常显示，并标注「设置码」。**实测**：扫「版本」码时枪会回一串 `HW:…;SW:…;Date:…;`，这类设备回复同样只在测试页显示，不进业务页面。
- **跨来源去重**：同一个码 300ms 内从不同来源（hid / camera / ble）各来一次只保留第一次。

### 3.2 原生蓝牙层

**推荐：两端都接 `react-native-ble-plx`**（iOS + Android 一套 API，自带 Expo 配置插件）。现有的 `hb-printer`（iPad）和 `HbPrinterModule`（手机）只会「写」，没有 notify 订阅，改造它们会把打印机和扫码枪绑在一起。

- 先做 **1 天技术验证**：确认 ble-plx 当前版本在 Expo 54 / RN 0.81 / 新架构下能正常编译、扫描、订阅，并且与打印机模块各自的 `CBCentralManager` 同时存在不互相干扰。
- 验证不过的备选：仿照 `hb-printer` 自研 Expo 模块 `hb-ble-scanner`（iOS CoreBluetooth + Android BluetoothGatt），只做「扫描 / 连接 / 订阅 / 读电量和 RSSI」。

两种做法都**必须重建原生包**；OTA 推到旧包时要先探测原生能力，没有就隐藏这一行并提示「请更新 App」（参照手机端 `isBlePrintingSupported` 的写法）。

## 4. 各端改动

### 4.1 iPad POS（apps/pos-ipad）

| 内容 | 位置 / 做法 |
|---|---|
| 路由入口 | `src/core/peripherals/scanner/hid-scanner.ts` 新增 `acceptBleText(value, receivedAt)`，与 `acceptCameraText` 并列；`src/core/contracts/scanner.ts` 的 `ScanSource` 加 `"ble"`。场景栈、`HBDEV1-` 拦截、`device-activation` 静默全部复用 |
| 路由桥 | `src/ui/scanner/scanner-route-bridge.tsx`：现在只在隐藏输入框聚焦时才占用场景。改为**场景归属与 HID 捕获分开**：页面在前台就占场景（BLE 码按场景投递），隐藏输入框照旧只管键盘模式 |
| BLE 服务 | 新增 `src/core/peripherals/scanner/ble/`：`ble-scanner-service.ts`（连接、重连、读电量和 RSSI、拼帧后调 `acceptBleText`），在 `src/core/runtime/expo-pos-runtime.ts` 里组装，挂到 `services.scanner.ble` |
| 退货页 | `src/features/returns/return-screen.tsx` 现在靠可见输入框收字，要补一个 `return` 场景接收 BLE 码（填入并提交，和手动回车同一路径） |
| 设置页 | `src/features/settings/settings-screen.tsx` 的 `PeripheralsPane`：原「扫描器」小卡升级为「蓝牙扫码枪」卡；搜索弹窗照抄打印机选择弹窗（`:3233-3383`）；新增测试弹窗。文案在 `settings-copy.ts`（中英文要同时加，否则类型检查不过） |
| 测试 | `src/core/runtime/settings-scanner-test.ts` 改为占用 `scanner-test` 场景的独占会话，持续收码直到关闭，不再只等一次。**顺带修现有问题**：外设页的「测一次扫码」收不到键盘模式输入（隐藏输入框只在「硬件测试」分区才聚焦），并把设置页的 `pushContext/popContext` 改成租约 |
| 状态 | `pos-shell-store.ts` 的 `scanner` 字段加上 BLE 连接状态和电量；`status-strip.tsx` 显示「扫码枪 82%」，断开时变黄；把 `expo-pos-runtime.ts:1213` 写死的 `scannerStatus: "ready"` 换成真实状态 |
| 存储 | SQLCipher `app_settings` 新 key `ble_scanner_v1`：`{ peripheralId, name, profileId, serviceUuid, notifyUuid, autoReconnect, beep, lowBatteryAlert, savedAt }`，参照 `receipt_printer_v1` 做字段白名单校验 |
| 权限文案 | `app.config.ts:205` 和 `plugins/with-hb-printer.js` 都会写 `NSBluetoothAlwaysUsageDescription`（插件会覆盖前者），统一改成「连接小票打印机、钱箱和蓝牙扫码枪」。**不开**后台蓝牙（`backgroundBle: false` 保持） |
| 审核文档 | `docs/APP_STORE_REVIEW_NOTES_DRAFT.md:37` 里写的「蓝牙只用于小票打印机」要同步改 |

### 4.2 移动端 App（apps/mobile）

| 内容 | 位置 / 做法 |
|---|---|
| 分发 | 移动端没有全局扫码总线，各页自己调用 `src/modules/scanner/use-hid-barcode-scanner.ts`，用 `enabled` 表示归属。新增 `src/modules/scanner/ble/ble-scan-hub.ts`，在 **`useHidBarcodeScanner` 内部订阅它**：只有「最后一个 `enabled` 的 hook」收到 BLE 码（租约栈，参照 `hid-native-listener-lifecycle.ts`）。约 25 处现有扫码页面**不用逐页改**，自动可用 |
| 来源标记 | `src/modules/scanner/types.ts` 的 `ScanSource` 加 `"ble"`，`use-scan-result.ts` 按需区分（例如统计） |
| 弹层内扫码 | BLE 不受原生 Modal 限制，`textInputOnly` 只影响键盘模式，不影响 BLE |
| BLE 服务 | 新增 `src/modules/scanner/ble/`：服务 + zustand 状态（参照 `src/modules/printer/state.ts`），App 启动和回到前台时自动重连 |
| 设置页 | `app/(shell)/settings.tsx` 的「设备与打印」分区（`:1434-1534`）在小票打印机下面加一行「蓝牙扫码枪」；详情、搜索、测试放在**同一个 BusinessSheet 的二级页**（`onBack`），不要连开两个原生 Modal（iOS 同一帧换 Modal 会丢弃第二个）。**不新开路由**，避免与后台「设备管理」页（`app/(shell)/device-management.tsx`）重名，也不用做三端菜单登记 |
| 需处理计数 | `src/modules/settings/settings-summary.ts` 的 `countAttentionItems` 加上「已保存但断开 / 电量低于 20%」；同步更新 `settings-redesign-contract.test.ts`、`settings-screen-contract.test.ts` |
| 存储 | AsyncStorage `hbweb_ble_scanner`（与打印机的 `hbweb_label_printer` 同一套封装） |
| 文案 | `src/locales/{zh,en}/screens/settings.json` 加 `bleScanner.*`，`check-locale-parity.js` 会校验中英文一致 |
| iOS 审核态 | 扫描 / 连接前检查 `isIosReviewSessionActive()`，和打印机一样（`src/modules/printer/api.ts:303`），并补 `ios-review/side-effects.test.ts` 的契约 |
| 权限 | iOS `app.json` 的 `NSBluetoothAlwaysUsageDescription` 文案加上扫码枪；Android 权限（`BLUETOOTH_SCAN` / `BLUETOOTH_CONNECT` 等）已在 `app.json` 和已提交的 `android/app/src/main/AndroidManifest.xml` 里，无需新增 |
| 只用相机的页面 | 考勤二维码、设备激活二维码、特殊商品分类加商品：本期**不接** BLE（考勤和激活是安全相关的扫码，保持只用相机） |

## 5. 防冲突规则（两端一致）

1. **不模拟键盘**：BLE 码整码交给路由，永远不写进任何输入框，也不影响屏幕键盘。
2. **测试独占**：设置页测试打开时，所有来源的码只进测试面板，不触发业务。
3. **敏感码屏蔽**：`HBDEV1-` 开通码和设备激活场景沿用现有拦截。
4. **跨来源去重**：同码 300ms 内不同来源只算一次（枪两种模式都开、相机和枪同时对着同一件商品）。**同一来源**的连续重复仍按页面原规则处理，避免误吞「连扫两件同款」。
5. **单一归属**：只投给当前前台、正在接收扫码的那一个页面。iPad 看场景栈顶（主管授权 > 登录 > 当前页），手机看最后一个 `enabled` 的 hook。
6. **没人接就丢**：当前页不收扫码（付款中、设置、普通表单）就丢弃并轻提示一次，不缓存，不延迟投递。
7. **与打印机共存**：搜索时排除已保存的打印机；扫码枪和打印机的扫描、连接操作排队执行，不同时扫描。
8. **一台设备只保存一把 BLE 扫码枪**（本期）；BLE 同一时刻只能连一台主机，换设备用要先在原设备上断开。

## 6. 测试功能

- **自检**：蓝牙开启和授权 → 已连接 → 已订阅数据通道 → 收到扫码 → 有没有键盘模式重复输入。哪一步没过就停在那一步，并给出处理按钮（打开系统设置 / 重新连接）。
- **最新一次**：大字显示整码、长度、码型推测（按长度和校验位判断 EAN-13 / UPC-A / EAN-8，其余显示「其他」）、分包数、结束符、耗时；可以展开十六进制原始数据，用来排查「码被拆成两段」「结束符不对」。
- **最近 20 条**：只在内存里，关闭即清；敏感码屏蔽显示。
- **也能测键盘模式扫码枪**：在同一个面板里，来源标「键盘」。
- 卡片 / 设置行上只显示最近一次扫码的尾部 6 位。

## 7. 发布

- 两端都**需要重建原生包**，PR 标题要写明（见 `AGENTS.md`「发布与 PR 规范」）。
  - iPad：TestFlight。
  - 手机：iOS + Android 都要重建。Android 的 EAS production 构建会自动推给全体安卓用户，先用 preview 包在真机上验证。
- 新原生包之前的 OTA：探测不到原生能力就隐藏「蓝牙扫码枪」行，其余功能不受影响。
- 后端、数据库、Web 都不涉及。

## 8. 验证计划

- `packages/ble-scanner-core`：拼帧（分包、各种结束符、超时结束、超长、乱码）、嗅探、退避、跨来源去重的单元测试。
- iPad：`hid-scanner.test.ts` 加 `acceptBleText` 用例；`scanner-route-bridge.rntl.test.tsx` 加「BLE 只投给聚焦路由」「主管授权弹窗优先」「相机开着时不重复投递」；设置页测试独占的 RNTL 测试。
- 手机：`useHidBarcodeScanner` 的 BLE 归属用例（两个 hook 同时 `enabled` 时只投给最后一个）；设置页契约测试更新。
- **真机矩阵**（必须）：至少 2 款 BLE 扫码枪（一款 Nordic UART、一款 FFE0 透传） × iPad / iPhone / Android；覆盖连续扫 200 次不丢码不重码、断电重连、App 退后台再回来、与蓝牙打印机同时使用、与相机扫码同时使用、同一把枪键盘模式和 BLE 模式同时开时的去重。

## 9. 工作量估计

| 阶段 | 内容 | 估计 |
|---|---|---|
| 0 | ble-plx 技术验证 + 共享核心包 | 2–3 天 |
| 1 | iPad POS（含修现有测扫码问题、退货页场景） | 4–5 天 |
| 2 | 移动端 App | 4–5 天 |
| 3 | 真机矩阵与修正 | 2 天 |

## 10. 待确认

1. **门店扫码枪型号**：已拿到一款（§2.1 手机背夹），2026-11-21 已用 Mac 实测数据通道、包长、结束符、电量和休眠重连（见 §2.1「Mac 实测结果」）。还差：① 门店是否还有其他型号；② 第二把同型号枪的厂商数据是否不同（决定能否靠它区分同名枪）；③ iPhone / iPad / Android 真机上的重连表现（Mac 只验证了 CoreBluetooth 一侧）。
2. 入口名称：iPad 放在现有「外设」分区、手机放在「设备与打印」分区（都不叫「设备管理」，因为手机端「设备管理」已经是后台管理设备的页面）。
3. 只用相机的页面（考勤二维码、设备激活）是否保持不接 BLE。

## 设计图

设计图 PNG 已单独发送：iPad 外设卡片、iPad 搜索连接弹窗、iPad 屏幕设置码、iPad 测试弹窗、手机四屏、扫码路由示意图（2026-11-21 按手机背夹说明书更新过一版）。
