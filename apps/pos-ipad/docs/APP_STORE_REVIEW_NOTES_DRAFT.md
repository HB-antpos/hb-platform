# HB POS App Review Notes 草稿（真实账号）

本草稿只适用于真实后端、真实设备注册和真实收银员账号。不得在提交二进制中加入
合成商品、模拟交易、DEMO 小票或绕过设备/权限门禁的审核模式。

收银员凭据只填写到 App Store Connect 的 App Review Information，不得写入仓库、
构建日志或本文。

## App Review Information

- Review store: `1042 · testStore`
- Device activation code: `[enter only in App Store Connect]`
- Cashier credential type: employee cashier barcode
- Cashier barcode: `[enter only in App Store Connect]`
- Device approval contact: `[release owner and monitored contact]`

## Notes for App Review

以下英文内容必须在专用审核门店、账号和设备审批流程全部验证后才可提交：

`HB POS is an iPad-only point-of-sale application for authorised Hot Bargain stores. It has no public account registration. The submitted build connects to the real HB POS service and uses a dedicated, database-backed App Review store and cashier account. It does not use simulated products, demo transactions or a separate review-only runtime.`

`On first launch, select the App Review store listed above, enter the one-time Device activation code supplied in App Review Information, and submit the device registration request. A valid code authorises only one iPad for the dedicated review store during the configured review window. The application then opens the cashier sign-in screen.`

`At the cashier sign-in screen, enter the employee cashier barcode supplied in App Review Information. The account is active only for the dedicated App Review store and has only the permissions required for the review steps below.`

`Suggested review flow:`

`1. Register the review iPad against the dedicated App Review store with the supplied Device activation code.`

`2. Sign in with the supplied employee cashier barcode.`

`3. Browse the real store catalogue and add one or more products to the current sale.`

`4. Continue only through the payment step explicitly enabled for the dedicated review store. No live customer card or production payment terminal is required.`

`The camera permission is used only as a barcode-scanning fallback. Bluetooth access is used only while the app is in the foreground, to connect supported receipt printers (including their cash drawers) and Bluetooth barcode scanners; the app does not use Bluetooth in the background. Local-network access is used only for supported store payment terminals. The app does not request microphone permission.`

## 提交前证据门槛

- [ ] 已建立与营业门店隔离的真实审核门店；商品、税率和收银配置均来自后端真实记录。
- [ ] 已建立有效真实用户、`UserStore` 归属和唯一启用的员工收银条码，不使用超级管理员。
- [ ] 审核账号只具备既定审核流程所需 POS 权限，不具备后台管理或跨店权限。
- [ ] Apple 的新 iPad 能在无人临时值守的情况下完成设备审批；若仍需人工审批，不得提交审核。
- [ ] 设备开通码为高强度一次性随机值；服务端只配置 SHA-256，明文只填入 App Review Information。
- [ ] `PosIpadAppReview` 只启用专用审核门店、单设备上限和覆盖完整审核周期的 UTC 到期时间；审核完成后立即停用。
- [ ] 审核门店的订单、支付、库存、报表和财务数据已与营业数据隔离，并有可审计清理流程。
- [ ] 审核流程不会调用真实客户卡、真实支付终端、打印机、钱箱或营业门店外屏。
- [ ] 使用最终 Production 二进制在干净真机完成注册、审批、收银员登录和约定审核步骤。
- [ ] App Review Notes 与最终二进制实际行为逐句一致，账号在整个审核周期内保持有效。
