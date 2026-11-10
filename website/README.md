# 官网与账号网关

2026-10-29：首页与便签空间已采用新的白色布局。首页社区链接区保留 LINUX DO、
GitHub 和 Gitee，后续可在 `public/index.html` 的 `.friend-links` 中添加链接，
区域会自动换行。发布与只读验证记录见 `design/redesign-20261029/deployment.md`。

正式站点：https://note.kimlulu.com/ 。Cloudflare 提供权威 DNS，域名经 EdgeOne 接入 InstaCloud 新加坡的 `kim / main / note-edge-gateway`，再连接同区域共享 Go 服务 `kim-api-singapore` 和数据库。各站点使用独立前端网关、共用账号和业务后端。原 Cloudflare Worker `kimlulu-note` 保留为回退，不要将它直接发布到正式域名。

桌面更新通知读取 `public/update.json` 的 `version`。1.4.20 已正式发布；以后发布新版本时，应先确认官网下载包和 GitHub 备用下载可用，再部署包含清单的生产网关。网关构建会校验清单、ZIP 大小及 SHA-256 与首页版本一致；仅使用数字版本号。通知只打开官网，不自动下载或替换程序。已安装的 1.4.18 不含更新检查，需用户自行下载新版。

## 环境

- `BACKEND_ORIGIN`：Go 服务的 HTTPS 根地址，不得在生产指回官网。
- `BACKEND_GATEWAY_SECRET`：与 Go 一致的网关密钥，由 InstaCloud 服务环境注入。
- `EDGE_ORIGIN_SECRET`：EdgeOne 向网关回源时使用的独立鉴权密钥。
2026-11-11：网页登录、注册及密码重置不再加载人机验证，也不添加图形验证码。请求直接交由共享 Go 后端校验账号密码、邮箱或重置凭证；保留 Origin 检查、登录限流和会话规则。网关不再依赖 Turnstile 配置。

网页登录使用 HttpOnly、Secure、SameSite=Lax Cookie；30 分钟无操作、或首次登录满 8 小时后失效，由 Go 后端校验。浏览器不保存密码或令牌。仅真实的键盘、触摸、点击或滚动操作节流发送 `/auth/activity`，隐藏标签页和后台同步不会延长空闲期限。登录页首先检查会话；官网、返回导航和跨标签页变动都会重新识别账号。401 结束当前会话，网络错误保留状态并提供重试。

未保存便签暂存在当前标签页的 sessionStorage，按已验证的账号 ID 隔离；重新登录原账号才恢复，且不会自动提交。所有已登录 API 与私有缩略图发送 `X-Kim-Expected-User`，由服务端拒绝跨标签页账号切换时的写入竞态。Cookie 仍是唯一网页登录凭证。存储不可用时，过期页保留可复制的草稿，避免声称暂存成功。

便签页采用主题色卡片网格，点击打开右侧阅读抽屉；手机全屏阅读。图片通过私有接口按可见范围加载，重绘、关闭阅读或离开页面时释放临时 URL。富文本仍在桌面编辑。账号菜单的「登录设备」支持单设备与全部设备退出；`/notes/?panel=devices` 可直达。公开示例 `/notes/?demo=1` 不访问登录或私有内容。

## 验证和部署

```powershell
npm ci
npm test
node --check public/assets/site-v2.js
node --test edge-gateway/tests/*.test.mjs
```

DOM 行为测试使用 jsdom，仅用于开发，不会打包到网页。生产网关的构建、隔离验收、密钥管理和发布说明见 [edge-gateway/README.md](edge-gateway/README.md)。不要使用旧 Worker 的 `npm run deploy` 发布当前正式站点。

认证回归覆盖无验证码登录和注册、错误密码、限流、会话恢复及私有缓存。真实账号验证使用隔离测试环境和合成数据；不要自动向真实用户邮箱发送测试邮件。

本地 `npm run dev -- --ip 127.0.0.1 --port 8787 --local` 提供 `LOCAL_PREVIEW`。配置只写入忽略的 `.dev.vars`，完整认证测试接入独立 Go 测试环境。无需配置外部验证组件。
