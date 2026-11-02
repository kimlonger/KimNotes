# 小羊账号与便签服务

Go 1.26 + PostgreSQL 17，使用标准库 HTTP 服务、pgx、Argon2id。个人主站与小羊便签分前端仍由 Cloudflare Worker 托管，同源 API 转发至同一 InstaCloud `kim / main / kim-api` 服务，继续使用原有 `main / db` PostgreSQL。2026-11-01 因原区域测试实例资源池故障，经隔离验证后将相同已测试镜像部署至 us-east 替换实例；没有建立第二套业务数据库或用户体系。

## 数据与权限

- `kimcloud.users` 为多个应用共享的账号；邮箱统一小写。`applications` 登记应用，`user_apps` 关联用户。
- 会话限定应用、用户和 web/desktop 类型。Web 使用 Secure + HttpOnly + SameSite=Lax Cookie；桌面使用 Bearer，数据库只存令牌摘要。修改/重置密码使全部应用会话失效。
- 便签、版本、操作、同步流和图片全部使用 `app_id + user_id` 限定；客户端不能指定 user_id。
- 独立 `kimcloud` schema，不修改既有其他应用的表。新增应用通过迁移登记，不能在请求中临时创建。
- 第一版图片保存在 PostgreSQL 私有 bytea 字段。没有公开图片桶；同一账号与应用内按 SHA-256 复用。以后可在同一对象 API 后迁往私有对象存储。
- 本地窗口位置、大小、显示器等布局不进入云端文档。
- `main` 是个人主站的应用 ID，与 `kimnotes` 共用 `users`、邮箱密码和密码撤销策略；分别签发应用限定会话。已有便签账号可直接登录主站，主站注册的账号也可登录便签，不复制或另建用户。
- 主站关注、入口明细和累计使用独立的 `main_account_*` 表，仍在同一 PostgreSQL 和 Go 服务中；不写入便签，不依赖 D1 或前端本地数据作为云端记录。

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

主站使用同一服务的 `/api/v1/apps/main`，沿用通用 `/config`、`/auth/*`。主站 Worker 是无状态同源网关，校验主站真实 Origin 后再规范为后端 `PUBLIC_ORIGIN`，并映射自己的主机限定 Cookie；便签官网和桌面原有认证行为保持不变。一个应用的会话不能调用另一个应用的接口。

### 主站账户接口

以下路径均位于 `/api/v1/apps/main/account`，必须登录并发送 `X-Kim-Expected-User`，值来自已验证 `/auth/me` 的 `user.id`。缺失或与会话不符返回 409；客户端不能通过请求体、查询参数选择其他用户。`kimnotes/account/*` 不提供这些能力。

| 方法 | 路径 | 响应 |
| --- | --- | --- |
| GET | `/follows` | `{projectIds:[],updatedAt:null}`；保存后为服务器时间 |
| PUT | `/follows` | 请求 `{projectIds:["notes","wincodex"]}`，整体替换并返回同结构；空数组清空 |
| POST | `/entries` | 请求 `{projectId:"notes",action:"open"}`；201 `{recorded:true,entry:{projectId,action,createdAt}}` |
| GET | `/overview` | `{follows:{count,projectIds,updatedAt},activity:{total,byProject,recent,kind:"site_entries"}}` |
| GET | `/notes-summary` | `{noteCount,imageNoteCount,countIsExact,lastUpdatedAt,checkedAt,website}` |

入口 action 仅接受 `open/download/source/details`，是主站入口记录，不表示在应用内真实使用或下载完成。按作品保留累计数量，明细仅保存最近 100 条，`recent` 返回最新 20 条。计数、写入与裁剪在同一用户应用锁和事务内执行；限流失败不增加累计。只保留作品 ID、操作及服务器时间，不记录 IP、URL、设备指纹或文章内容。

作品 ID 的权威来源是 `content_works` 的已发布记录，新增作品无需修改 Go 硬编码或创建认证应用。`MAIN_PROJECT_IDS` 默认留空；显式配置时是最多 64 个互不重复的小写标识组成的收窄允许列表，仍必须是已发布作品。关注最多 64 件，未知/重复 ID、未知操作和多余写入字段均返回 400。目录缩减不会删除既有记录。

账户读取合并限流为每用户 90 次/分钟，关注写入 30 次/分钟，入口写入 120 次/小时；都在统一 PostgreSQL 中限流，Worker 不维护第二份状态。超限返回 429。接口继承私有 no-store、网关保护、Origin 检查、会话轮换/撤销及账号切换保护。

便签摘要仅对当前 `user.id` 聚合 `kimnotes` 未删除便签，SQL 只返回计数与更新时间，不选出便签正文、标题、附件引用或私有地址。`noteCount` 是精确总数；图片数包含附件、内联图片引用及明文 RTF 图像。如果旧压缩 RTF 没有图片引用、无法在不读取解压正文的情况下判定图片，`countIsExact:false` 提示图片数可能少计。无便签时计数为 0、`lastUpdatedAt:null`。`website` 是固定便签入口 `https://note.kimlulu.com/notes/`。

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

主站扩展使用 `004_main_account.sql`：登记 `main` 应用、新建三张表和索引，并给 `applications` 增加可空的 `public_origin`。无便签、用户或会话数据清理；表外键指向既有 `user_apps`，未来明确删除账号关联时相应主站数据一并删除。迁移受既有事务和 advisory lock 保护，版本记录后不重复运行，SQL 自身也可重复执行。`migration-status` 现在验证至 004 及三张表。先在隔离分支完成旧表兼容与主站集成测试，再对正式数据库单独迁移、检查、部署同一 API 镜像；不将测试指向生产 `DATABASE_URL`。

密码重置邮件的落地页来自已登记应用的 `public_origin`（main 默认 `https://kimlulu.com`）；空值回退到原有 `PUBLIC_ORIGIN`，保留便签官网行为。必须是 HTTPS origin，不含用户信息、路径、查询参数或 fragment；非法配置拒绝发信。请求体不能指定邮件域名。重置挑战仍按 app 隔离，密码更新仍撤销这个统一账号的所有应用会话。主站承接 `/login/?mode=reset#challengeId:token` 并使用 `main/auth/reset/complete`，不把主站挑战交给便签页面处理。

## 统一内容目录

`005_content_catalog.sql` 在同一 PostgreSQL 新建 `content_works`、`content_articles`、`content_updates`，不新增用户、认证应用或按作品拆分数据表。作品的 `kind` 是 `app/plugin/tool`；三表均有 ID、`draft/published/archived` 状态、顺序、服务器更新时间及兼容前端的公开 JSON payload，文章和动态另有作品/文章外键。它们与登录用 `applications` 的职责不同。

`GET /api/v1/content/catalog` 经过现有可信网关、不要求登录，返回 `{projects,articles,updates,version}`。只读取已发布记录，未发布作品关联的文章和动态、未发布关联文章的动态也会隐藏，避免悬空链接。三组读取共享一致快照，全部响应最多 1 MiB；超出预算或遇到无效公开字段时返回 503，绝不返回截断目录。响应是 `public,max-age=60,must-revalidate`、没有会话 Cookie；强 ETag 是实际响应字节的 SHA-256，支持匹配的 `If-None-Match` 返回无正文 304。

内嵌 `content-seed.json` 是首次导入材料（3 件作品、16 篇原始技术文章、11 条过程记录），不是运行时静态目录。公开 API 始终从 PostgreSQL 读取。维护命令只需要既有数据库权限，没有新增后台管理员账号、公开写入接口或浏览器数据库密钥：

```powershell
kimcloud migrate
kimcloud migration-status
kimcloud content-import
# 只有明确需要覆盖同 ID 的内容及发布状态时才使用：
kimcloud content-import --replace
```

导入在事务及 advisory lock 内批量执行，预先验证全部 ID、重复项、字段类型、HTTPS 链接、真实日期及作品/文章引用；未知字段（含意外私有数据）拒绝导入。默认 `INSERT ... ON CONFLICT DO NOTHING` 保留数据库已有内容、顺序和发布状态；`--replace` 才覆盖这些字段并将对应种子记录发布，未列入种子的记录不会删除。失败整体回滚。目录最多 256 件作品、500 篇文章及 500 条动态，单记录 128 KiB；正文最多 64 段，段落最多 12000 个 UTF-16 单元。公开字段限制与 Worker wire contract 一致。启动 HTTP 服务不执行 DDL 或导入。

正式顺序是迁移至 005、检查、首次 `content-import`、再发布同一中央 API 镜像及主站。`Dockerfile` 和 `.dockerignore` 已包含供 `go:embed` 使用的种子。隔离测试使用随机 schema，覆盖默认导入保护、显式替换、整事务回滚、发布状态和关联过滤、公开字段隐私、ETag、响应预算、动态作品关注权限及旧便签兼容。

`website/tests/live-api.mjs` 可对明确指定的站点执行合成数据验收，记录临时清理 journal，不记录密码或会话。使用同一数据库凭证运行 `go run ./tools/cleanup-e2e --journal <路径>`，只清理 journal 中 ID、邮箱及创建时间全部匹配的 `kimnotes-e2e-…@example.com` 账号；工具不进入部署镜像。
