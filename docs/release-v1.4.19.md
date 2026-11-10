# 小羊便签 KimNotes v1.4.19

适用于 Windows 10 / 11 的免安装桌面便签、截图贴图与待办提醒工具。

## 本次更新

- 新增版本更新提示，可在托盘菜单中手动检查，也会定期检查；发现新版后可打开官网下载，不会自动替换程序。
- 优化云同步的登录状态处理。遇到无法识别的临时 401 响应时，保留本机登录凭证并稍后重试；明确失效的凭证仍需重新登录。
- 网站和云服务已切换到新的加速入口，客户端仍使用域名连接。

## 下载与升级

下载 `KimNotes-v1.4.19.zip` 并完整解压，从托盘退出旧版后运行 `KimNotes.exe`，保留同目录的配置文件和依赖。需要 .NET Framework 4.8。正常升级沿用原有数据目录；建议升级前备份便签。

已安装的 v1.4.18 没有更新检查功能，用户需手动到官网下载本版；从 v1.4.19 起才能收到后续版本的更新提示。

- [官网下载](https://note.kimlulu.com/downloads/KimNotes-v1.4.19.zip)
- [GitHub Release](https://github.com/kimlonger/KimNotes/releases/tag/v1.4.19)
- [Gitee 项目](https://gitee.com/xiaoyangkim/KimNotes)

ZIP 大小：474726 字节。SHA-256：`6031ac1375c0d7ffefc64b6ffc07e47b46e90c3a67f1697d9b3ef2630ddbbb0a`。

本次候选包来自当前代码的独立 Release 构建；390 项桌面回归和 75 项云同步回归通过。不同显示器和长期使用场景仍需实际验证。
