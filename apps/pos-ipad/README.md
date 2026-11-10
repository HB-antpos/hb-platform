# HB POS iPad

`apps/pos-ipad` 是独立的 iPad 收银应用，不复用 `apps/mobile` 的 Bundle ID、EAS Project 或审核配置。

## 本地启动

```bash
cd ../..
npm ci
npm run test --workspace=@hb/pos-ipad
npm run prebuild:ios --workspace=@hb/pos-ipad
npm run ios --workspace=@hb/pos-ipad
```

根目录 `package-lock.json` 是两个 POS App 的唯一依赖锁，根 `postinstall` 是
React Native Scheduler 与 expo-audio 补丁的唯一应用入口。不要在 App 目录建立
独立 lockfile、patch 或执行 app-only install。EAS CLI 仍从本 App 目录运行；npm
workspace discovery 会把依赖安装锚定到仓库根并执行根补丁门禁。

原生打印、SQLCipher 和外接客显必须使用 Development Build，Expo Go 不在支持范围内。

API 地址通过 `EXPO_PUBLIC_HBPOS_API_URL` 注入；可切换的备用 origin 必须在签名构建时通过
`EXPO_PUBLIC_HBPOS_TRUSTED_API_ORIGINS` 明确列出。设备注册成功后仍由 Hbpos.Api 的设备审批和门店权限控制，
持久化设置不能把设备或收银员凭据发送边界扩张到构建白名单之外。

银行卡终端必须通过 `EXPO_PUBLIC_HBPOS_CARD_PROVIDER=square|linkly`
显式选择。Square 与 Linkly 的公开配置可以同时保留，用于恢复切换前已经耐久化的
支付 attempt；未选择 provider 时禁止发起新的银行卡交易。token、密钥和 provider
交易引用不得写入 Expo public extra。

### Zeller 刷卡的开放范围

Zeller 收款还没有经过真机和刷卡机验证，所以设置页的 Zeller 页签（配对、启用）只在**非 production 构建**
（development、preview）里开放，production 构建默认不显示，也就没有办法启用。

要让测试员在 TestFlight 包里验证 Zeller，用 EAS 档位 `production-zeller`：它与 `production` 走同一个
bundle id、TestFlight 渠道、EAS 项目和 App Store Connect 记录，只多一个构建变量
`EXPO_PUBLIC_HBPOS_ENABLE_ZELLER=1`（只认字面量 `1`）。

```bash
npx eas-cli@21.3.0 build --platform ios --profile production-zeller --auto-submit --non-interactive
```

- `app.config.ts` 把 `production-*` 变体档位按 `production` 对待，生产保护（缺 EAS projectId 直接失败、
  日志中心环境）一视同仁。
- 日常正式构建仍用 `--profile production`，不带这个开关。
- 开关是构建时写进包里的：同一个包以后用 OTA 更新时，JS 由 OTA 脚本按 `production`（不带开关）打包，
  Zeller 页签会随之消失——iPad 的 OTA 目前全部关闭，不受影响，但开启 OTA 前要处理这一点。
- Zeller 验证页（`hbpos-ipad://zeller-probe`）会不建订单直接扣款，仍只在非 production 构建里开放，
  `production-zeller` 包里没有。
- Zeller 没有测试环境，任何测试扣款都是真实扣款（最低 AUD 1.00），测完要在 Zeller 后台退款。

## App Store 与 OTA 更新

原生版本策略与 OTA 策略分别检查、缓存和失败回退。生产构建必须显式提供独立
iPad EAS 项目，缺少以下任一配置都会在解析 Expo production config 时终止：

```bash
EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID=<dedicated-project-uuid>
EXPO_PUBLIC_HBPOS_UPDATES_URL=https://u.expo.dev/<dedicated-project-uuid>
```

启动自动检查固定为 `NEVER`；应用只在后台门店策略命中后覆盖受信 channel，
并在下载前后核验 `runtimeVersion` 和 iOS update ID。由于 Expo 的 request
header override 会持久化，Updates 启用时冷启动会先 best-effort 清除旧 channel，
再等待本次可信门店策略。正式发布必须为每个 release 使用独立的
`pos-ipad-release-*` channel；`pos-ipad-production` 只作为原生
bootstrap header，不承载发布内容，也不得复用 `apps/mobile` 的 `production`
channel。中央后台会拒绝重复登记已被其他 release 使用的 channel。

发布并登记 release 使用专用脚本：

```bash
read -rs HBPOS_OTA_ADMIN_JWT
printf '%s' "$HBPOS_OTA_ADMIN_JWT" | \
  HBPOS_OTA_CENTER_BASE_URL=https://<center-host> \
  EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID=<dedicated-project-uuid> \
  EXPO_PUBLIC_HBPOS_UPDATES_URL=https://u.expo.dev/<dedicated-project-uuid> \
  npm run ota:publish -- \
  --runtime-version <runtime-version> \
  --release-channel pos-ipad-release-<unique-release-key> \
  --message "<release-message>" \
  --access-token-stdin
unset HBPOS_OTA_ADMIN_JWT
```

管理员 JWT 必须保持为未 `export` 的 shell 变量并仅通过 stdin 交给脚本；脚本也会
从 EAS 子进程环境中显式剔除该变量以及兼容期 token 变量。

正式流程固定为：使用管理员 JWT 调用
`POST /api/pos-ipad/ota-releases/preflight`，通过后执行
`eas channel:create <channel> --json --non-interactive`，再执行 iOS
`eas update --json`，最后只登记 `/api/pos-ipad/ota-releases`。脚本不会创建或
激活 Center rollout。EAS channel 已存在或创建失败时会在发布内容前终止，也不会
自动删除 channel；因此每次重试发布必须换用全新的 release channel。
脚本与 `eas.json` 同时锁定 EAS CLI `21.3.0`，升级 CLI 前必须先复跑发布顺序和
重复 channel 的失败测试。

可先用 `--dry-run`，并用 `--mock-output-file <path>` 验证已保存的 EAS JSON；
它只打印预期的 channel 创建命令、update 命令和登记 payload，全程不调用 Center
或 EAS。正式流程需要具备 `System.ManageAppDownloads` 权限的三段 base64url
管理员 JWT。交互执行应使用无值参数 `--access-token-stdin` 从管道读取；
自动化仍可只设置 `HBPOS_OTA_CENTER_ACCESS_TOKEN`。两种来源不能同时使用，
`--access-token <token>` 会在任何网络/EAS 调用前被拒绝，避免凭据进入 shell
history 或进程参数。只读 `hbsvc_` service token、非 JWT、空值、多行或超过
4096 bytes 的输入同样会失败，管理员 token 也不会进入 EAS 子进程或命令日志。

如果 EAS update 已成功但 Center 登记失败，脚本会打印可重试登记 payload。
此时不得重新运行发布命令；应保留已经创建的 channel，并使用打印的 payload
单独重试管理员登记接口。
`branch` 与 channel 是不同概念，脚本不会把 EAS JSON 的 `branch` 当作登记 channel；
命令、登记 payload 及 JSON 中若存在的显式 channel 必须一致。
