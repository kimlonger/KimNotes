# Windows 桌面长期登录实现与验收（2026-10-23）

桌面采用协议版本 2：访问凭证有效 15 分钟，刷新凭证成功换领后滚动有效 180 天。正常使用会静默续期；离线、本机保存、换显示器不会要求反复输入密码。桌面仍是原生邮箱和密码登录，不增加人机验证、内嵌浏览器或服务器密钥；注册打开系统浏览器的官网注册页。

## 客户端行为

- 所有请求通过 `https://note.kimlulu.com/api/v1/apps/kimnotes`，发送 `X-Kim-Client: desktop` 和 `X-Kim-Auth-Version: 2`。
- 访问凭证只在内存。当前 Windows 用户的 DPAPI 保护账号身份、刷新凭证、设备标识及未确认的换领请求。v2 `session.dat` 的解密 JSON 本身也不包含访问凭证；程序退出后重新用刷新凭证换领。
- 换领前原子保存 requestId 与原刷新凭证；新凭证对先原子保存，再替换内存状态、重放原请求。同一进程的并发请求共用一次续期，续期失败也会交付给已排队的请求，不形成连续请求突发。
- 丢包、进程重启、凭证文件写入失败，均继续使用原 requestId 重试。服务器须在规定的 5 分钟幂等期限内返回相同凭证对；超过期限按服务器撤销或失效结果重新登录，不无限恢复旧凭证。
- 请求访问凭证提前 45 秒续期；业务接口明确返回 `access_expired` 时，最多续期并重试一次。JSON、上传图片、下载私有附件都使用同一机制，重试重建 HttpContent。
- 网络、超时、429、5xx 保留账号和本地待同步队列。一般同步 30 秒再试，429 等待 120 秒，不循环发送密码。
- `session_revoked`、`refresh_expired`、`refresh_revoked`、`refresh_reused`、`account_disabled` 等明确失效结果停止同步并移除本机失效凭证。身份和便签所有权保留，原账号本地副本仍可查看；重新登录后继续同步。
- 旧版尚有效的 30 天 Bearer 通过 `/auth/desktop/upgrade` 一次换领。已过期旧凭证不能升级；升级接口暂时 404 时保留有效旧身份、使用原有访问能力，并将迁移重试节流到 5 分钟。
- 退出先清除本机凭证与备份，随后用刷新凭证调用 `/auth/desktop/logout` 撤销该设备；远程请求最多等待 3 秒。离线时明确提示云端撤销尚未确认，可在官网设备列表撤销。

## 接口约定

`POST /auth/desktop/login`：邮箱、密码、deviceName、deviceId。

`POST /auth/desktop/refresh`：refreshToken、requestId。

`POST /auth/desktop/upgrade`：旧 Bearer，requestId、deviceName、deviceId。

以上成功响应均包含 `user`、`token`、`refreshToken`、`expiresIn:900`、`refreshExpiresIn:15552000`、`sessionId`、`protocolVersion:2`。刷新响应必须匹配原账号和设备会话，防止身份串用。

`POST /auth/desktop/logout`：refreshToken，成功 `{ok:true}`。明确失效响应使用 `code`，访问失效与刷新失效不能混为一类。

## 本机隔离验收

测试均使用临时数据目录和进程内模拟 HTTP 服务，不访问用户真实账号、便签或正式服务器。服务器真实轮换、幂等缓存期限、撤销及网关透传，需后端部署验收另外确认。

- `tests/cloud.ps1`：61 项通过。包括原富文本、图片、同步冲突、账号隔离和新增的旧凭证迁移、启动续期、访问到期、并发成功与失败、图片重放、丢包重启、原子写入失败、429/5xx、离线退出、身份不匹配、明确撤销及重启后仍保持重新验证状态。
- `tests/cloud.ps1 -UI`：真实 WinForms 消息循环通过。实际双屏 DPI 为 120 / 144，反复跨屏时字段、眼睛按钮、注册入口与同步状态均完整；慢验证、首次同步等待、失效、退出切换正常。邮箱密码原生登录、密码清空、无 WebView2 引用均通过。
- 改动仅涉及桌面会话实现、原生账号窗口响应解析、对应测试及本文；账号窗口原有原子视图切换和 DPI 布局、图片交互均保留。

