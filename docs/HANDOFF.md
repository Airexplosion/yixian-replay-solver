# 当前服务状态（2026-10-16）

原 GitHub Pages 网站保持关闭。HTTPS API 为 `https://154.9.252.167:8443/api/v1`，服务 `yx-replay.service`，代码目录 `/root/yx-replay`。参数和客户端示例见根目录 `API.md`。

## 求解与说明数据

- 新增 `/solver`、POST `/solve`、GET/DELETE `/solve/{job}`；C# 自包含 Linux 进程执行，默认全部可用逻辑核，服务器当前 4 核，可指定较少线程。
- 默认 `searchMode=exhaustive` 遍历完整空间并持续排名，不设 30 秒总时限；`existence` 找到赢法即停；`rank` 使用调用方设置的时间。支持取消、进度和重复读取终态。
- 当前规则与主项目 `sim/Yx.BattleSim` 同步，百家之道适配版本，战绩 `001.0008.0002`。主项目的全量模式修改也单向同步至 `yisim_C#`，133 个源文件无漂移。
- `/catalog` 和 `/catalog/{kind}/{id}` 返回仙命、天衍、百家、刻印、共鸣、状态、卡牌与角色名称和效果。仙命 10005：善假于物，抽2张副职牌。描述内关键词与关联仙命也已经转成名称。
- 复盘保留数字字段并增加 `details`；百家开关从每轮私有快照的 `xianMoData.tempDatas` 读取。实际复盘 gma7a8r862 已验证版本匹配、16 轮导入、带百家与详细说明。
- 规则与本地化数据位于被忽略的 `solver-data/`，仅部署至服务器 `/root/yx-replay/solver/data`。没有提交或部署游戏 DLL、原始反编译代码。生成脚本 `tools/export_catalog.py`，同步脚本 `tools/sync_engine.py`。
- 新目录版本 `f297b03091c885e88fcd`；配置来源 `ade95cebce08a93ab2f651307ede2a5e.bundle`。服务器自包含宿主在 `/root/yx-replay/solver/host`。API 回滚备份 `/root/yx-replay/solver-update-20261016/backup`。

## 验证

主项目 518 项 sim 测试通过；服务器隔离目录 36 项 API/进程测试全部通过，后续增加描述链接渲染回归。GitHub Actions 构建并运行集成测试，使用合成配置，不依赖厂商数据。

实服小空间使用 4 核，34/34 摆法完整遍历；另一个真实任务使用 2 核，运行超过 38 秒仍在遍历（77416 / 76751233），随后取消成功。终态重复读取一致。证据在主项目 `.smoke/solver-api-1016/live-verification.json`。没有启动或附加游戏。

## 同服务器道心榜

`https://airexplosion.github.io/yixian-daoxinbang/` 已恢复；它和关闭的求解网站是两个站点。采集器真实 Steam 服务名 `daoxin-steam.service`。Steam 票证和账号节点正常，故障是 GitHub HTTPS 推送凭据失效，已改用仅授权该站点仓库的服务器部署密钥。

已补回近两天 104 份被降采样移除的原始快照，近 48 小时共 257 份逐一匿名核验通过。精细保留窗口现为 72 小时。详情见相邻 `zongmen-dabi/docs/2026-10-16-site-recovery.md`。
