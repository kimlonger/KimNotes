# 小羊便签 EdgeOne 网关

此目录保存新加坡便签网关的 Node 源码与单元测试。正式流量经 EdgeOne 到独立的 InstaCloud 网关，再到现有共享 Go 后端；它不创建新账号库，也不更改数据库。`src/note-worker.mjs` 是 Node 网关使用的适配实现，`../worker.js` 是原 Cloudflare Worker，两者均存在，需要同步维护行为，不应把旧 Worker 直接发布到当前正式域名。

## 本地构建和验证

在 `KimNotes/website` 目录执行：

```powershell
node --test edge-gateway/tests/*.test.mjs
docker build -f edge-gateway/Dockerfile -t kimnotes-edge-gateway:local .
```

镜像在 Node 22 构建阶段运行网关测试，并从本项目 `public/` 构建网站，不依赖临时工作区或线上抓取。Dockerfile 专用 ignore 文件仅允许源码、测试和公开资源进入构建上下文，避免把 `.dev.vars`、`node_modules` 等本地文件传给构建器。最终镜像排除 `public/_headers`（旧 Cloudflare 专用配置），包含 `public/update.json`。构建阶段校验更新清单与下载清单版本、文件名、ZIP 大小和 SHA-256、首页版本的一致性；发布前仍需先发布并验证正式下载包，再让更新通知面向用户。

上面的 `-f` 是 **Docker CLI 的本地构建命令**，不是 InstaCloud 的直接源码发布命令。当前 InstaCloud CLI 的 `deploy` 没有选择嵌套 Dockerfile 的选项，`insta --agent build .` 在 `website` 目录不会识别此文件，并可能将 `node_modules` 等不必要内容纳入源码上下文。**不要直接对 `website` 或本目录执行 `insta deploy`。** 如果使用远程源码构建，需先准备独立、已核对的发布目录，让适配后的 Dockerfile 位于其根目录，并只放入所需的 `public`、网关源码和测试，排除更新清单、`_headers`、`.dev.vars`、密钥及其他无关文件；先运行 `insta --agent build <发布目录> --port 4174` 和隔离环境验收。也可通过受控容器镜像仓库提供经验证的镜像摘要，再使用 `insta --agent deploy --image ...`；先确认该仓库和目标项目可用，不要把本地 Docker 标签当成云端可访问镜像。

这些测试不会连接正式数据库，也不证明正式网页的人机验证或真实账号登录成功。发布前需要在隔离环境验证，特别是登录、人机验证、桌面登录与令牌续期、同步、图片上传下载、私有 API 的 `private, no-store`、EdgeOne 回源鉴权和 HTTPS；不要使用真实用户数据作为测试样本。

## 生产配置与发布

正式网关使用 InstaCloud `kim` 项目的 `main / compute/note-edge-gateway`，监听端口 `4174`。使用 `NODE_ENV`、`PORT`、`PUBLIC_ORIGIN`、`BACKEND_ORIGIN`、`BACKEND_GATEWAY_SECRET`、`EDGE_ORIGIN_SECRET`、`TURNSTILE_SITE_KEY` 和 `TURNSTILE_SECRET_KEY` 配置服务；这些值由服务环境管理，密钥不得写进源码、镜像、日志或聊天。EdgeOne 需为便签域名覆盖回源鉴权头并提供可信的客户端 IP。`/healthz` 是单独的存活探针。为兼容平台 robots 探测，精确路径 `/robots.txt` 的 GET 和 HEAD 可不带回源鉴权；其他方法和路径仍须鉴权。私有接口必须保持不缓存，并验证无凭据直接访问源站被拒绝；平台缓存可能直接提供公开静态文件，不能将进程鉴权等同于禁止所有公网静态访问。

便签站 EdgeOne 针对 `note.kimlulu.com` 的 `/robots.txt` 将回源路径重写到 `public/edge-robots-snapshot.txt`，其他站点和路径不适用。该静态快照保存迁移前的完整 robots 政策与站点 sitemap、私有路径规则；不会自动跟随 Cloudflare 的后续政策更新，需要主动核对维护。快照目前仍保留 `Baiduspider` 禁抓规则。发布构建时必须包含这个文件，发布后从公网实际验证 `/robots.txt` 正文及私有路径规则，不能只检查状态码。

先在隔离项目以同一份源码和独立凭据构建、测试，记录镜像摘要及线上可回退摘要；通过后再按现行 InstaCloud 发布流程将相同镜像部署到正式服务，检查公开资源、TLS、跳转、缓存和登录同步。切换正式域名和回退的具体环境记录见工作区 `docs/guitar-edgeone-migration.md`，部署前重新核对实际配置。不得将测试密钥用于正式环境，不能覆盖正式数据库或导入测试数据。

服务在 Node 内处理单段 Range；公网平台可能将 HEAD + Range 响应改成 206，发布验收应测试实际外网行为。真实的网页 Turnstile 需要由用户完成验证，不能用测试站点密钥或绕过流程代替。
