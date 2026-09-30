# HB POS Handheld

独立的小屏幕收银前端，目标为 iPhone iOS 17+ 和 Android PDA 10/API 29+。它复用现有 Hbpos POS 后端与 iPad POS 的业务技术栈，但应用身份、原生工程、更新策略和发布通道独立，并且不包含客显。

## 本地启动

```bash
cd ../..
npm ci
npm run prebuild:ios --workspace=@hb/pos-handheld
npm run ios --workspace=@hb/pos-handheld
```

```bash
cd ../..
npm ci
npm run prebuild:android --workspace=@hb/pos-handheld
npm run android --workspace=@hb/pos-handheld
```

根目录 `package-lock.json` 是两个 POS App 的唯一依赖锁，根 `postinstall` 是
React Native Scheduler 与 expo-audio 补丁的唯一应用入口。不要在 App 目录建立
独立 lockfile、patch 或执行 app-only install。EAS CLI 仍从本 App 目录运行；npm
workspace discovery 会把依赖安装锚定到仓库根并执行根补丁门禁。

`EXPO_PUBLIC_HBPOS_API_URL` 可覆盖 POS API 地址；生产默认值为 `https://hotbargain.vip/pos-api`。不要把设备凭据、支付密钥或服务令牌写入 Expo 公共环境变量。

## 验证

```bash
npm run test:project
npm run typecheck
npm run lint
```

完整测试使用 `npm test`。Android BLE/SPP 打印、钱箱和 APK 安装仍须在 API 29/30 与 API 31+ 真机分别验收。

## 设计与架构

- [设计约束](./docs/design/prompt-set.md)
- [46 状态矩阵](./test-fixtures/handheld-design/state-matrix.json)
- [架构边界](./docs/architecture.md)

## 发布边界

- 原生生产构建的主命令为：

```bash
npx eas-cli@latest build --profile production --platform all --non-interactive
```

- iOS：`production` 产生 Store 分发构建，并提交到独立的 App Store Connect App `HB POS Mobile`（Apple ID `6802182045`）；构建完成不等于已进入 TestFlight，必须使用明确的 EAS Build ID 执行 `npx eas-cli@latest submit --profile production --platform ios --id <build-id> --wait --non-interactive`。
- Android：`production` 和兼容的 `android-internal` 都产生签名 APK，仅用于受控安装；它们不是 Google Play 发布或 iOS/TestFlight 提交。
- `production` 依赖独立的 EAS project 环境变量；未同时配置 Project ID 和 updates URL 时，Expo 配置会主动失败，避免将更新连接到错误项目。

手持 OTA 统一发布到固定频道 `pos-handheld-production`。默认发布 iOS；Android 必须显式指定平台：

```bash
npm run ota:publish -- --runtime-version 0.1.0 --platform ios --message "发布说明" --dry-run
npm run ota:publish -- --runtime-version 0.1.0 --platform android --message "发布说明" --dry-run
```

正式发布前配置 Center 地址，并通过标准输入传入管理员 JWT（也可使用 `HBPOS_OTA_CENTER_ACCESS_TOKEN`）：

```bash
export HBPOS_OTA_CENTER_BASE_URL="https://hotbargain.vip"
printf '%s' "$HBPOS_OTA_ADMIN_JWT" | npm run ota:publish -- --runtime-version 0.1.0 --platform ios --message "发布说明" --access-token-stdin
```

脚本只执行一次 `eas update`，随后调用已有受保护接口 `POST /api/mobile-app-builds/ota-updates`，以 Expo slug `hb-pos-handheld` 登记真实 group/update/platform/runtime 数据；不要创建 `pos-handheld-release-*` 频道，也不要调用已废弃的 `/api/pos-handheld/ota-releases`。若 EAS 已成功而登记失败，不得重复发布；没有登记记录时客户端更新决策保持 fail closed。

构建配置不代表已发布；发布、提交或推送均需单独授权。
