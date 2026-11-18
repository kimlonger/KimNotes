# 小羊便签 Node 网关

此目录保存上海统一 Node 网关的便签适配源码与测试。正式流量由上海 Nginx 接入，经过统一网关的便签适配器到共享 Go 后端。今后正式部署统一在上海服务器，见 [三站正式接入记录](../../../项目公共资料/note-guitar-domestic-20261116/便签正式接入记录.md)。`../worker.js` 是供本地预览和历史兼容检查使用的旧 Cloudflare Worker。

## 本地构建和验证

在 `KimNotes/website` 目录执行：

```powershell
npm run prepare:site
node --test edge-gateway/tests/*.test.mjs
docker build -f edge-gateway/Dockerfile -t kimnotes-edge-gateway:local .
```

镜像在 Node 22 构建阶段运行网关测试，并从本项目 `public/` 构建网站，不依赖临时工作区或线上抓取。Dockerfile 专用 ignore 文件仅允许源码、测试和公开资源进入构建上下文，避免把 `.dev.vars`、`node_modules` 等本地文件传给构建器。最终镜像排除 `public/_headers`（旧 Cloudflare 专用配置），包含 `public/update.json`。发布前的 `npm run prepare:site` 按内容哈希更新公开页面的 sitemap 日期和静态资源文件名；构建阶段校验 sitemap 未过期，并校验更新清单与下载清单版本、文件名、ZIP 大小和 SHA-256、首页版本的一致性；发布前仍需先发布并验证正式下载包，再让更新通知面向用户。

Docker 命令供本地验证。准备上海发布产物时，只纳入已核对的公开资源与网关源码。保留与正式下载包一致的 `public/update.json` 和下载清单，排除开发环境配置、密钥及依赖目录。

这些测试使用本地模拟上游。发布前在隔离环境验证网页登录、桌面登录与令牌续期、同步、图片上传下载、私有 API 的 `private, no-store`、可信网关鉴权和 HTTPS。测试使用合成数据。

## 生产配置与发布

正式 Nginx 使用本机 Unix socket 连接统一 Node 网关，并覆盖站点标识和客户端地址。统一网关将 `PUBLIC_ORIGIN`、`BACKEND_ORIGIN`、`BACKEND_GATEWAY_SECRET` 和 `EDGE_ORIGIN_SECRET` 传给便签适配器，密钥由运行环境管理。`/healthz` 为存活探针，`/robots.txt` 的 GET 和 HEAD 对应公开静态文件。私有接口保持不缓存。

上海正式站直接提供 `public/robots.txt`。原 EdgeOne 重写所用的 `public/edge-robots-snapshot.txt` 保留为历史资源。发布后从公网验证 `/robots.txt` 正文、sitemap 和私有路径规则。

网关对截图贴图、OCR、提醒、更新日志和隐私等公开页面设置 `X-Robots-Tag: index, follow`。登录页、便签页和 API 使用 `noindex`。新增公开页面时维护允许列表与回归测试。

以同一份源码在隔离环境构建、测试，记录制品摘要及回退版本。验证通过后更新上海统一网关的便签适配器和静态制品，复测公开资源、TLS、跳转、缓存和登录同步。正式数据库保留现有业务数据。

服务在 Node 内处理单段 Range；公网平台可能将 HEAD + Range 响应改成 206，发布验收应测试实际外网行为。网页登录、注册及密码重置已移除 Turnstile，不增加图形验证码；账号密码、重置凭证、限流和会话仍由 Go 后端校验。
