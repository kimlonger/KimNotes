# 小羊便签 KimNotes v1.4.20

适用于 Windows 10 / 11 的免安装便签、截图贴图与待办提醒工具。

## 本次更新

- 更新桌面程序图标，统一便签程序与官网的小羊形象。
- 修正便签列表仍显示旧图标的问题。
- 优化跨屏切换时的布局和重绘，修复设置页快捷键文字拥挤，并将官网改为可点击链接。
- 优化更新提醒：每天提示一次，可选择跳过当前版本。

## 下载与升级

下载 `KimNotes-v1.4.20.zip` 并完整解压，从系统托盘退出旧版后运行 `KimNotes.exe`，保留同目录的配置文件和依赖。需要 .NET Framework 4.8。正常升级沿用原有数据目录；建议升级前备份便签。

已安装的 v1.4.18 没有更新检查功能，需要手动到官网下载新版。v1.4.19 支持定期检查和在托盘菜单手动检查；发现新版后会提示前往官网下载，不会自动替换程序。

- [官网下载](https://note.kimlulu.com/downloads/KimNotes-v1.4.20.zip)
- [GitHub Release](https://github.com/kimlonger/KimNotes/releases/tag/v1.4.20)
- [Gitee 项目](https://gitee.com/xiaoyangkim/KimNotes)

ZIP 大小：560323 字节。SHA-256：`180fc05b58c6885bfd75f494c1f66889085b2402c8d5a6e784eca390d46edeac`。

本次候选包已通过 413 项桌面回归；EXE 文件版本为 1.4.20。已在 125% 和 150% 缩放的双屏环境检查布局，并进行实际鼠标跨屏拖动检查；长期使用仍需持续验证。

本次仅替换同版本下载资源；已安装 1.4.20 的用户需重新下载，更新检查不会因版本号相同而再次提醒。

原有 v1.4.20 标签保持不变，标签自动生成的源码归档对应原始发布；本次修复的源码见 [master 分支](https://github.com/kimlonger/KimNotes/tree/master)。
