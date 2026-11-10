# Guitar 应用扩展

应用 ID 为 `guitar`，使用现有 `users`、`user_apps`、应用会话与网关。迁移 `006_guitar.sql` 建立公共课程/章节/课时/曲谱，以及个人上传、收藏、进度、访问日和留言/问答数据。

公共资源不依赖个人上传记录。审核批准时把文件复制到 `guitar_scores`，个人记录仅引用公共 ID；删除个人上传不会级联删除公共曲谱。

```sh
go run . migrate
go run . guitar-import
go run . guitar-import --file course.json --replace
go run . guitar-review list
go run . guitar-review export UPLOAD_UUID review.png
go run . guitar-review approve UPLOAD_UUID
go run . guitar-review reject UPLOAD_UUID "审核说明"
go run . guitar-review message MESSAGE_UUID accepted "已加入需求清单"
```

默认导入不覆盖已有记录；`--replace` 明确更新同 ID 内容，事务保证整体成功，不删除缺省记录，禁止现有章节/课时 ID 改变父级。内置数据由独立前端的 `content/beginner.mjs` 导出为 `guitar-seed.json`，Docker 构建须包含该文件。

API 前缀 `/api/v1/apps/guitar/`。课程介绍/目录、留言、问题列表公开；课时、曲谱、个人数据和问答发布要求登录。匿名留言有服务端频率及长度限制。每日打开按 Asia/Shanghai 日期去重。

当前审核入口为操作者 CLI，没有公开管理 HTTP 接口。持有数据库权限的维护者负责检查上传内容和分享权限。

集成测试需设置 `KIMCLOUD_TEST_DATABASE_URL`，使用已有测试环境隔离 schema。独立前端 README 提供本地开发数据库和测试脚本。
