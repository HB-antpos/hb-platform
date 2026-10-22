# RustDesk 公司账号与设备通讯录

官方 RustDesk iOS/macOS 在“ID/中继服务器”中配置：

- ID 服务器：`hotbargain.vip:21116`（**RustDesk 迁移到专用域名并确认全部设备收敛之后**，改为 `rd.hotbargain.vip:21116`；在那之前控制端连不上已切到新服务器的设备，也连不上仍在旧服务器的设备，过渡期按设备所在服务器各配一套）
- 中继服务器（客户端显示此项时）：`hotbargain.vip:21117`（同上，迁移完成后为 `rd.hotbargain.vip:21117`）
- API 服务器：`https://hotbargain.vip/api/rustdesk`（始终是业务主域名的 HTTPS 接口，不随 RustDesk 服务器迁移）
- Key：沿用服务器当前公钥，不更换私钥。

保存后通过“登录”输入现有 HBWeb 系统管理员账号。在“通讯录”中选择共享的“公司设备”：它包含已安装并回传 RustDesk ID 的有效 Windows POS，以及管理员显式登记的 Mac。公司通讯录只读，个人通讯录为空。已有后台加密密码的有效 POS 会同步连接密码，点击设备时由官方客户端自动完成密码认证。未回传密码的设备仍需输入密码或由被控端接受。

更新后刷新通讯录并选择“公司设备”；若客户端仍把公司设备显示为个人通讯录，退出公司账号后重新登录。请从联系人图标的共享通讯录连接；“设备组”使用不同协议，不读取通讯录的密码字段。

桌面端的“设备组”（电脑和手机图标）与“通讯录”（联系人图标）使用两套接口。设备组页依次读取 `GET /api/device-group/accessible`、`GET /api/users`、`GET /api/peers`，相对上述 API 地址追加路径。三者均需 RustDesk 专用会话并返回 `{ total, data }` 分页对象。公司受管设备统一放在“公司设备”组；用户目录为空，不虚构设备账号归属。设备组的设备数据使用 `info.os`、`info.device_name`、`info.username`，其中 Mac 平台值为 `macos`。

若客户端登录成功而设备组页显示“获取组信息失败 HTTP 404”，先核对上述三个 GET 路由，不能只验证 `/api/ab/peers` 通讯录路由。修复部署后在设备组页点击刷新；在线状态仍由 hbbs 查询。

## 状态与登记范围

在线状态由官方客户端向 `hbbs TCP 21115` 发送 `OnlineRequest` 查询，设备通过 `UDP 21116` 注册/保活。API 的心跳接口不会新增设备，不把陌生 hbbs 注册者自动视为公司设备。

POS 每次读取通讯录均核对 POSM 中该硬件的当前合法登记，要求仍启用且类型为 POS、系统为 Windows。通常要求硬件最大登记 ID 与远程维护安装快照一致；开通码换店按下面的完整证据链核验，普通旧登记或已停用设备不返回。Mac 采用 `HBweb_RustDeskManagedDevice` 显式登记，停用标记为 `IsDisabled`。本次部署仅纳入用户确认的现有 Mac，不自动扫描或导入其他电脑。

POS 的设备组和通讯录统一显示 `分店名称 完整设备代码`，例如 `TestStore POS_1042_0200`。名称每次从当前 POSM 登记联查 `Store.StoreCode → StoreName`；分店记录缺失时保留分店代码。返回的 `hostname`、`alias` 使用完整显示名，`username` 留空，避免客户端拼出重复的 `设备代码@计算机名`。

WPF 通过开通码重新绑定分店后，服务器从现有安装时间开始核对已消费 Rebind 记录：旧分店/设备代码必须唯一对应链中上一身份，新登记 ID、硬件号、分店、设备代码和系统必须一致；最后一条记录的授权哈希须与仍启用的新身份一致。撤销、歧义、缺少证据、未关联的新登记或授权重置均拒绝映射。通过后，下次刷新设备组或通讯录自动使用新名称，既有状态代理心跳和安装密码继续适用于这台物理 POS，无需重新安装或改写旧安装快照。普通 prepare/commit/下载的设备鉴权保持原规则。

多个有效 POS 登记报出同一个 RustDesk ID 时，整组不返回，避免任意挑选名称或密码；先修复设备身份冲突再恢复同步。

## 连接密码同步

`POST /api/ab/shared/profiles` 返回只读公司通讯录（`rule=1`），`POST /api/ab/peers?ab=hb-company-devices` 在专用管理员会话通过核验后返回设备的顶层 `password` 字段。仅此接口请求密码；设备组、标签和旧版通讯录均不读取或返回密码。响应禁止 HTTP 缓存，服务端存储仍使用现有独立 DataProtection purpose，审计只记录设备 ID 和管理员标识。

官方 RustDesk 1.4.9 的共享通讯录使用原始密码，控制端在收到被控端的 salt/challenge 后计算认证响应；不能把个人通讯录的 Base64 `hash` 填进 `password`。协议依据：[共享设备连接](https://github.com/rustdesk/rustdesk/blob/1.4.9/flutter/lib/common/widgets/peer_card.dart#L1488-L1504)、[客户端认证](https://github.com/rustdesk/rustdesk/blob/1.4.9/src/client.rs#L3475-L3590)。传输须使用上述 HTTPS API 地址。

关闭 `RemoteMaintenance.Enabled` 时停止下发密码；已停用或无法证明合法换店的旧 POS 登记、无密码或无法解密的记录不下发凭据。手动纳管的 Mac 当前没有密码存储入口，不能仅凭设备 ID 自动补齐。被控端若要求点击接受或其他验证，仍需满足该策略。管理员失去公司账号权限会停止后续同步；已交付到客户端的设备密码需要在目标设备轮换才能撤销。

验收时检查公司共享通讯录可见、已登记 POS 可直接连接，普通设备组响应无凭据；同时验证未授权会话被拒绝、无凭据设备保持可见、目标端原有验证策略有效。不要在诊断输出中打印响应体、密码或加密凭据。

## 会话与部署

RustDesk 会话使用 32 字节随机 opaque bearer，只持久化 SHA-256 哈希，与 HBWeb JWT 隔离。会话最长 30 天；退出、密码变化、账号停用/删除或管理员角色撤销后不再授权。

仅显式执行 `--schema=rustdesk-client` 创建两张专用表，随后执行 `--schema=rustdesk-client-check`。普通 HTTP 启动不建表。部署保留当前主 API 镜像、源码和所有配置/密钥；失败时恢复主 API 镜像和对应任务源码，新表保留为兼容性回滚数据，不删除。

官方协议参照 RustDesk 1.4.9 的 `user_model.dart`、`ab_model.dart`、`peer_model.dart` 以及 `src/client.rs::peer_online`。API 地址包含路径前缀，客户端会追加 `/api/login` 等路径。
