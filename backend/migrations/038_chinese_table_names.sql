-- Chinese names are table comments so existing database identifiers still work.
COMMENT ON TABLE kimcloud.users IS '统一用户账号';
COMMENT ON TABLE kimcloud.applications IS '应用登记';
COMMENT ON TABLE kimcloud.user_apps IS '用户应用关系';
COMMENT ON TABLE kimcloud.sessions IS '登录会话';
COMMENT ON TABLE kimcloud.access_tokens IS '访问令牌';
COMMENT ON TABLE kimcloud.refresh_tokens IS '刷新令牌';
COMMENT ON TABLE kimcloud.auth_challenges IS '账号验证请求';
COMMENT ON TABLE kimcloud.auth_events IS '账号认证事件';
COMMENT ON TABLE kimcloud.desktop_upgrades IS '旧设备登录迁移记录';
COMMENT ON TABLE kimcloud.web_cookie_aliases IS '网页会话旧凭证映射';
COMMENT ON TABLE kimcloud.rate_limits IS '请求限流计数';

COMMENT ON TABLE kimcloud.account_point_wallets IS '账号积分钱包';
COMMENT ON TABLE kimcloud.account_point_events IS '账号积分流水';

COMMENT ON TABLE kimcloud.guitar_members IS '吉他用户资料。含每周练习目标和佩戴称号。';
COMMENT ON TABLE kimcloud.guitar_artists IS '曲谱歌手。按署名归并，一位歌手可对应多份曲谱。';
COMMENT ON TABLE kimcloud.guitar_scores IS '吉他曲谱。含公开、私有和归档曲谱，公开谱独立于个人投稿。';
COMMENT ON TABLE kimcloud.guitar_score_files IS '吉他谱面文件。保存图片谱页和 PDF。';
COMMENT ON TABLE kimcloud.guitar_uploads IS '吉他曲谱投稿。保存上传者、审核信息及关联求谱。';
COMMENT ON TABLE kimcloud.guitar_reading_progress IS '吉他用户曲谱状态。含收藏、读谱进度及已学会标记。';
COMMENT ON TABLE kimcloud.guitar_practice_tasks IS '吉他每日练习清单';
COMMENT ON TABLE kimcloud.guitar_practice_sessions IS '吉他练习记录。含看谱计时、任务计时、手动补记和计划估算。';
COMMENT ON TABLE kimcloud.guitar_visits IS '吉他每日打开记录。按北京时间每日去重。';
COMMENT ON TABLE kimcloud.guitar_messages IS '吉他交流帖子。涵盖问答、求谱与建议，隐藏帖子不公开。';
COMMENT ON TABLE kimcloud.guitar_community_answers IS '吉他问答回复';
COMMENT ON TABLE kimcloud.guitar_request_answers IS '吉他求谱应答。保存求谱与所提供曲谱的关联。';

COMMENT ON TABLE kimcloud.notes IS '便签主记录。保存便签状态和当前版本指针。';
COMMENT ON TABLE kimcloud.note_versions IS '便签历史版本。保存正文快照和同步序号。';
COMMENT ON TABLE kimcloud.operations IS '便签同步操作回执';
COMMENT ON TABLE kimcloud.attachments IS '便签附件';

COMMENT ON TABLE kimcloud.content_works IS '主站作品目录';
COMMENT ON TABLE kimcloud.content_articles IS '主站文章';
COMMENT ON TABLE kimcloud.content_updates IS '主站更新动态';
COMMENT ON TABLE kimcloud.main_account_entries IS '主站作品访问记录';
COMMENT ON TABLE kimcloud.main_account_follows IS '主站作品关注记录';
COMMENT ON TABLE kimcloud.main_account_totals IS '主站作品累计访问次数';

COMMENT ON TABLE kimcloud.schema_migrations IS '数据库迁移记录';

COMMENT ON TABLE kimcloud_archive.guitar_courses IS '吉他课程（历史归档）';
COMMENT ON TABLE kimcloud_archive.guitar_chapters IS '吉他课程章节（历史归档）';
COMMENT ON TABLE kimcloud_archive.guitar_lessons IS '吉他课时（历史归档）';
COMMENT ON TABLE kimcloud_archive.guitar_progress IS '吉他课程学习进度（历史归档）';
COMMENT ON TABLE kimcloud_archive.guitar_questions IS '吉他课程问题（历史归档）';
COMMENT ON TABLE kimcloud_archive.guitar_replies IS '吉他课程回复（历史归档）';
