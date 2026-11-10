# EdgeOne 独立回源网关（隔离验证）

两个 Worker 分别绑定现有 `kimlulu-guitar` 和 `kimlulu-note`，不创建账号、数据库或新业务服务。这里只是本地候选配置，未创建域名、密钥、部署或切换 DNS。

回源域名计划为 `origin-guitar.kimlulu.com` 和 `origin-note.kimlulu.com`。回源流量需要 HTTPS，分别设置不同的高熵 `ORIGIN_SECRET`；EdgeOne 必须使用“设置”覆盖客户端提供的 `x-kim-origin-auth`，不能只追加。不要将真实密钥写入文件或日志。

**未解决的上线条件：** EdgeOne 免费套餐能否设置并保护该回源头、完整 TLS 源站证书校验能力，以及 EdgeOne 到 Cloudflare 的真实客户端 IP 安全传递。当前应用按 `CF-Connecting-IP` 做限流、人机验证，经过双 CDN 后可能变成边缘节点 IP，不能信任未经验证的客户端 `X-Forwarded-For`。这些问题未通过验证前禁止用于正式登录或流量切换。不得缓存任何认证、私有 API、Cookie 或 `Set-Cookie` 响应；缓存规则须逐项确认。

2026-11-09 对已有 `guitar-test.kimlulu.com` 使用带唯一标记的不含账号的 `/healthz` 请求，并在现有吉他 Worker 上按标记观察：Cloudflare 的 `CF-Connecting-IP` 与 EdgeOne 的 `EO-Connecting-IP` 不同；尝试伪造 `EO-Connecting-IP` 和 `X-Forwarded-For` 后，前者在本次实验被 EdgeOne 覆盖，后者仍含伪造内容。仅为一次路径测试，不能证明所有节点或配置都覆盖，不能单独作为信任依据。匿名 `/auth/me` 返回 401，`private, no-store`、EdgeOne MISS。未验证真实登录、图片同步或两名用户之间的缓存隔离。

运行本地测试：`node --test edgeone-origins/origin-gateway.test.mjs edgeone-origins/workerd.test.mjs`。部署前应使用隔离 Worker 与合成账号完整验证登录、写入、图片、旧桌面客户端、证书和回退；正式网站现有 Worker 有未提交改动，不能直接从工作区部署覆盖。
