# 小羊便签 KimNotes v1.4.21

修复 Windows 11 拼音输入时，尚未选择候选词就自动变成拼音字母的问题。

## 本次更新

- 拼音组词期间暂缓自动保存，避免 RichEdit 导出文档时提前结束组词。
- 云同步等待当前组词完成，不读取或替换尚未确认的输入内容。
- 完成选词后重新计算空闲保存时间，继续保存和同步；主动关闭窗口时正常结束输入并保存。

## 下载与升级

- [官网下载](https://note.kimlulu.com/downloads/KimNotes-v1.4.21.zip)
- [GitHub Release](https://github.com/kimlonger/KimNotes/releases/tag/v1.4.21)
- [Gitee](https://gitee.com/xiaoyangkim/KimNotes/releases/tag/v1.4.21)

适用于 Windows 10 / 11，需要 .NET Framework 4.8。完整解压，从系统托盘退出旧版后运行 `KimNotes.exe`，保留同目录的配置文件和依赖。正常升级沿用原有便签、设置和登录凭证；建议升级前备份便签数据。

ZIP 大小：567,214 字节，约 554 KB。SHA-256：`761804f75fdf1c3610c4293cd62089eb3fa4a4ef552197ec910f21545a611c1a`。

此版本可被已有更新检查识别。更新通知引导用户到官网下载，不会自动替换程序。

## 验证

在原版 1.4.20 上记录到：输入法开始组词后，自动保存发送 `EM_STREAMOUT`，紧接着收到 `WM_IME_ENDCOMPOSITION`，尚未选词的拼音被提交到正文。

修复版本通过 422 项桌面回归、26 项同步策略检查、75 项云同步检查和 12 项同步边界检查。覆盖未选词时超过保存时限、原生 `Modified` 尚未触发 `TextChanged`、同步暂缓与恢复、选词后保存，以及取消未改动的组词。用户已验证修复测试包的实际输入效果；正式构建将程序版本更新为 1.4.21。

此次更新不涉及服务器数据库迁移。
