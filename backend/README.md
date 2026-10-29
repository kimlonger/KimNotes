# 小羊账号与便签服务

Go 1.26 + PostgreSQL 17，使用标准库 HTTP 服务、pgx、Argon2id。官网仍由 Cloudflare Worker 托管，`/api/` 同源转发至 InstaCloud `kim / main / kimnotes-api`。

## 数据与权限

- `kimcloud.users` 为多个应用共享的账号；邮箱统一小写。`applications` 登记应用，`user_apps` 关联用户。
- 会话限定应用、用户和 web/desktop 类型。Web 使用 Secure + HttpOnly + SameSite=Lax Cookie；桌面使用 Bearer，数据库只存令牌摘要。修改/重置密码使全部应用会话失效。
- 便签、版本、操作、同步流和图片全部使用 `app_id + user_id` 限定；客户端不能指定 user_id。
- 独立 `kimcloud` schema，不修改既有其他应用的表。新增应用通过迁移登记，不能在请求中临时创建。
- 第一版图片保存在 PostgreSQL 私有 bytea 字段。没有公开图片桶；同一账号与应用内按 SHA-256 复用。以后可在同一对象 API 后迁往私有对象存储。
- 本地窗口位置、大小、显示器等布局不进入云端文档。

## 启动与迁移

配置项见 `.env.example`。生产必须设置 `DATABASE_URL`、`GATEWAY_SECRET`（32 字节以上随机值）、`PUBLIC_ORIGIN`。密钥绑定到 compute 环境，不能放进镜像或 Git。

```powershell
# 使用指定分支凭证，不落盘、不输出凭证。
insta --agent run --branch kimnotes-backend --service compute/kimnotes-api -- go test -count=1 -v ./...
insta --agent run --branch kimnotes-backend --service compute/kimnotes-api -- go run . migrate
insta --agent build . --port 8080
insta --agent deploy . --branch kimnotes-backend --group kimnotes-api --port 8080
```

迁移文件位于 `migrations/`，通过事务和迁移锁执行；记录 `schema_migrations`。`kimcloud migrate` 是单独命令，启动 HTTP 服务不会执行 DDL。健康检查 `/healthz`，数据库检查 `/readyz`。官网接通前必须先验证迁移和服务。

`SMTP_HOST/PORT/USER/PASSWORD/MAIL_FROM` 配置后可发送注册验证码、密码重置邮件。支持 465 隐式 TLS 或 587 STARTTLS，均校验证书。`REQUIRE_EMAIL_VERIFICATION=true` 要求邮箱验证；尚无 SMTP 的第一阶段设置为 false，账号的 `emailVerified` 保持 false，找回密码接口返回明确不可用状态，界面隐藏对应入口，绝不伪报发信成功。

## API

所有业务接口前缀 `/api/v1/apps/kimnotes`。浏览器 POST 要求匹配 Origin，JSON 请求必须正确声明 Content-Type。网关设置 `X-Kim-Gateway` 并覆盖可信客户端 IP。直连业务接口无网关密钥返回 403；不向浏览器暴露网关密钥或数据库凭证。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| GET | `/config` | 邮件能力及同步周期 |
| POST | `/auth/register` | 邮箱密码注册；按配置即时登录或发验证码 |
| POST | `/auth/verify` | challengeId + 6 位验证码，10 分钟、最多 5 次尝试 |
| POST | `/auth/login` | 邮箱密码登录；桌面加 `X-Kim-Client: desktop`，响应返回 token |
| GET | `/auth/me` | 当前应用会话 |
| POST | `/auth/logout` | 撤销当前会话 |
| POST | `/auth/password` | currentPassword + newPassword，撤销所有会话 |
| POST | `/auth/reset` | 统一提示，不泄露邮箱是否注册 |
| POST | `/auth/reset/complete` | challengeId + token + password，30 分钟、单次有效 |
| GET | `/sync/pull?cursor=0&limit=100` | 增量版本，游标为十进制字符串 |
| POST | `/sync/push` | 最多 50 个操作，4 MiB 请求、8 MiB 响应上限，超限整批回滚 |
| GET | `/sync/bootstrap?limit=20&cursor=检查点&after=便签UUID` | 首次下载当前便签；第一页固定检查点，后续分页复用；响应约 4 MiB |
| GET | `/notes/{uuid}/versions?before=版本号` | 旧客户端兼容接口，官网不再展示版本入口 |
| POST | `/attachments` | 原始图片字节，10 MiB、4000 万像素上限 |
| GET | `/attachments/{uuid}` | 当前账号和应用的私有图片 |

同步操作示例（ID 必须为小写 UUID）：

```json
{
  "operations": [{
    "operationId": "edbe8ddd-8bee-4bbb-9c60-cf8c8d6d53bb",
    "noteId": "996d1d68-4330-4540-93bb-1f6fe8fd7607",
    "baseRevision": 0,
    "action": "upsert",
    "document": {"title":"计划","text":"今天完成一件小事。","themeId":2,"attachments":[]}
  }]
}
```

相同 operationId + 相同内容重试得到原结果；复用编号提交不同内容返回 409。新建 baseRevision=0，更新带已知 revision。旧版本更新保留为 `conflict_copy`，原文保留；旧版本删除/恢复返回 `conflict`，不覆盖新内容。`delete` 为软删除，`restore` 可在 30 天内恢复；当前版本不自动物理清理过期数据。不会以客户端时间戳决定胜负。

同步流在用户应用行锁下分配游标并提交，避免全局 sequence 的乱序提交漏事件。拉取使用一致快照并按序分页，约 4 MiB 单页。**push 响应游标不能直接替换客户端 pull 游标**，必须补读中间其他设备的事件。

新设备使用 `/sync/bootstrap` 按 UUID 读取当前未删除便签，跳过旧修订和已删除便签；第一页固定当前增量游标，后续页与中断重试沿用该值。全部下载完成后，从此游标调用 `/sync/pull` 接收分页期间的变化。后续页面可能已包含较新内容，客户端通过修订号去重，保证早先事件不会覆盖新内容。`/config` 的 `syncBootstrapVersion: 1` 标识支持此流程；旧服务器仍可回退到原有增量协议。服务端同步事件和旧兼容接口的数据保留，不执行历史数据清理。

主题 ID 与桌面一致：0 浅蓝、1 柠檬黄、2 薄荷绿、3 樱花粉、4 淡紫、5 天蓝、6 纸白、7 暖橙。RTF 原文可随文档保存，网页不解析执行 RTF，也暂不编辑富文本便签，避免丢格式。

初期限额：每用户每应用 1000 张便签，版本快照＋图片共 256 MiB；正文 256 KiB、RTF 2 MiB、标题 240 字、每张 32 个图片引用。登录/注册/找回密码按 IP 与邮箱限流，写入和图片另行限流。客户端收到失败必须保留本地原文和未确认操作。

## 当前交付边界

已接入桌面账号、离线 outbox、账号本地缓存隔离及自动双向同步。v1.4.12 桌面使用 15 分钟访问凭证与 180 天滚动刷新凭证；网页执行 30 分钟空闲 / 8 小时绝对期限。设备撤销、刷新幂等与重放检测见 `../docs/session-design-2026-10-23.md`。邮件逻辑已测试，真实发信仍需配置 SMTP。

`002_sessions.sql`、`003_auth_events.sql` 为兼容既有数据的扩展迁移。正式升级前使用独立 runner 执行 `kimcloud migrate`、`kimcloud migration-status`，确认后再部署 API。`kimcloud maintenance` 仅在短时 runner 中提供健康检查，不开放业务接口；迁移后移除 runner。启动服务不自动执行迁移。

`website/tests/live-api.mjs` 可对明确指定的站点执行合成数据验收，记录临时清理 journal，不记录密码或会话。使用同一数据库凭证运行 `go run ./tools/cleanup-e2e --journal <路径>`，只清理 journal 中 ID、邮箱及创建时间全部匹配的 `kimnotes-e2e-…@example.com` 账号；工具不进入部署镜像。
