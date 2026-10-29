# v1.4.18 官网下载更新 · 2026-10-29

deployment result: deployed and verified

- Worker 当前版本：`60165784-7334-434e-bcfe-74885e542ce2`。
- 沿用现有域名和 Go 服务，以 `npm run deploy -- --keep-vars` 保留已有配置。
- 首页下载、结构化数据和 `downloads/release.json` 已更新到 v1.4.18。
- ZIP：464969 字节；SHA-256：
  `ddadcf85178b8b1961daa1f317a2e2e07ec22ccec33301c3b25057ba4c51d0f7`。
- SHA-256 文件与安装包同时提供，v1.4.10 旧下载继续可用。
- 42 项网页测试通过；更新后的 `tests/live-design.mjs` 在线只读校验全部通过：
  页面和资源字节一致、版本清单一致、ZIP 大小和哈希一致、附件响应头正确、
  校验文件一致、登录会话配置和网关隔离正常、主域名跳转正常。
- LINUX DO 链接仍在首页社区区域。未部署 Go 服务或变更数据库。

以下保留首轮官网视觉改版的发布与验收记录。

---

# 官网改版发布记录 · 2026-10-29

deployment result: deployed and verified

- 正式地址：https://note.kimlulu.com/
- Worker：`kimlulu-note`，沿用 `website/wrangler.jsonc` 的现有域名和网关。
- 当前版本：`18d6102e-60f5-4c03-8c2b-48e92447afa9`。
- 上一版本：`1fde6e71-30bf-47b5-b732-4e190b8cdc47`，保留作回退依据。
- 执行 `npm run deploy -- --keep-vars`，保留已有服务器变量与密钥。
  本轮未部署 Go 服务、修改数据库、更新安装包或推送 Git。

## 发布前检查

- `npm test`：42 项全部通过。
- `node --check`：主脚本与时间分组模块通过。
- 将本轮变更资源的 URL 版本统一更新为 `20261029-white1`，避免已经缓存旧版
  `notes-time.js` 的用户加载不到新增的 heading 方法。
- Worker 将 `notes-time.js/css`、`refined.css` 与既有共享脚本统一设为短缓存，
  账号页保持 no-store；缓存回归检查已通过。

## 发布后只读检查

运行 `node tests/live-design.mjs`，所有检查通过：

- 首页、便签页、登录页与四个样式/脚本、三张新展示素材返回 200，SHA-256
  与发布目录文件一致，资源版本及缓存策略正确。
- 首页仍包含 LINUX DO 链接，并可继续在社区链接区添加其他链接。
- 账号配置网关正常，人机验证公钥可用；网页会话仍是 30 分钟空闲、8 小时上限。
  匿名 me 返回 401，未创建测试账号、提交验证码或写入用户便签。
- Windows 下载响应、附件头和文件哈希与本地现有发布包一致。
- kimlulu.com 与 www.kimlulu.com 的同路径请求仍 301 到正式域名，说明未开启
  生产 LOCAL_PREVIEW。
- 正式首页真实浏览器截图 `live-home.jpg` 已打开检查，显示新标题、下载入口
  与三张便签展示。线上 DOM 自动检查超时，未将其算作完整交互或登录验收；
  便签交互已有本地浏览器及 DOM 回归证据。

首次尝试 Cloudflare 登录刷新遇到网络不可达，使用本机已有的 127.0.0.1:7892
代理后成功。只在命令进程内设置代理，没有改变持久系统代理或认证策略。
