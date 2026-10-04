# 弈仙复盘

粘贴游戏复盘代码，自动还原各轮双方的牌桌、手牌和开战仙命，在浏览器里限时搜索摆法。另有赛季天梯总榜与角色上分统计。

- [复盘求解](https://airexplosion.github.io/yixian-replay-solver/)
- [赛季天梯榜](https://airexplosion.github.io/yixian-replay-solver/ladder.html)
- [角色上分统计](https://airexplosion.github.io/yixian-replay-solver/characters.html)

网页由 GitHub Pages 托管。首次打开先下载 .NET WebAssembly sim 和卡牌规则，加载完成才开放操作。求解在 Web Worker 中执行，可以停止；不需要安装游戏。复盘代码通过取数服务换成盘面数据，服务只返回求解所需状态，不返回账户令牌或原始玩家 UID。

## 当前范围

这是 C# sim 的浏览器实验版，基于目前拿到的源码移植，并非原生求解器 v0.2.6 的完整源码。部分机制仍有偏差；含尚未移植的仙魔策略时会拒绝求解。限时推荐也不保证全局最优。请用游戏实战复核。

天梯页面使用游戏天梯总榜入口 `gameData/fetchLeaderboard` 的 `category=0`，按赛季保存官方返回的前 100 名。随机角色道心榜在这个接口里用 `category=-1`。服务器约每 10 分钟采集；页面默认比较近 24 小时，也可自选起止时间，或选择今天、近 7 天、最近两次快照。

时间段模式使用区间内第一份与最后一份已保存快照，展示期初和期末积分、期间净分、名次变化，并可按期间净上分排序。页面显示实际对比时间和可用快照范围；不足两份快照不计算变化，超出采集范围会提示截断。起点与终点榜单取并集，新入榜或已离榜玩家缺少一端积分时显示“—”。只覆盖官方前 100 名快照，不能由此推算全服或每场加减分。点击玩家名称会把所选时间传给逐场战绩页。

## 角色战绩统计

角色统计页支持自选起止时间、开局积分范围、角色、玩家名称和样本门槛。显示已采集场数、角色使用占比、累计净分、场均净分和每小时净分；展开玩家可以查看各角色使用情况。时间统一为香港时间（UTC+8），按结算时间筛选，包含开始、不包含结束；积分范围按每局开局积分筛选，上限不含。

天梯榜可以点击玩家名称进入逐场战绩页。角色统计页展开玩家后也有这个入口，并保留当前筛选条件。逐场明细显示结算时间、角色、开局积分、净加减分、结算后积分与耗时；展开对局时间可看开始时间、编号、随机选角标记和版本。未采到战绩的玩家显示空状态，不把缺少样本解释成零场实战。来源赛季用于返回榜单，战绩按时间范围筛选。

新采集记录会保存实际请求的复盘入口；经验证后可以从明细一键打开盘面，浏览器 sim 就绪后自动导入。早期没有保存入口的记录仍显示统计明细，不猜测名次生成链接。回放能否重新打开取决于游戏服务是否仍可取该记录。

每小时净分用总净分除以总对局耗时计算，不含排队时间。扣分和 0 分也计入。场数按复盘编号＋玩家去重，回放的多轮不会被计成多局。样本不足时会显示提示；各分段的样本数量及采集缺失会影响比较结果。

后台每 10 分钟并发扫描复盘编号，保存断点，同时补查旧编号、暂不可取的编号和缺少玩家的对局。默认 4 路并发，每轮最多 2400 次请求 / 480 秒。新编号、玩家与编号缺口、历史补查轮流推进；中断的任务优先续取。连续 12 个编号不可取时暂停向前扩展，下一轮重新检查边界；已不可取的编号补查时也检查其他玩家入口。已有较早战绩时，从最早已知编号补查到原扫描起点。网页显示请求速度、未检查编号、不可取编号及真人战绩缺口。尚未验证全服最新对局索引或按时间批量查询接口，不能保证全服、完整赛季或任意历史时段覆盖。

完整性按回放中实际出现的参赛名单核验。仅在核实 8 名参赛者且所有非 AI 玩家均有有效结算战绩时，标为“真人已齐”；7 名真人加 1 名 AI 的对局无需保存 8 条真人战绩。名单尚未核齐的对局继续补查。完整性仅针对已检查的编号范围，不据此宣称全服完整。

精简数据保存在 `YX_REPLAY_STORE/rank-stats.sqlite3`：匿名玩家标识、公开名称、角色、起止时间、开局积分、净加减分、游戏版本和随机角色标记。整份原始回放只在内存中短暂处理，不写入统计存储；提取后释放。这个统计库不能用于还原完整盘面，复盘求解仍需重新从游戏服务取回回放。

## 本地构建

构建会生成静态资源的 SHA256 清单。浏览器通过 Service Worker 保存 sim、卡表与效果数据；同一版本再次打开从本机缓存读取，页面显示是否复用了缓存。同一页面内不会重复启动已经就绪的 sim；刷新页面仍需重新初始化运行环境。卡图的图集、渲染资源及生成的卡面按需持久缓存，图片资源变化时更新，sim 与图片文件均验证内容哈希。历史未使用的卡图不会一次全部下载。浏览器不支持缓存或存储空间不足时继续普通加载；清理浏览器站点数据后需重新下载。缓存只包含公开静态文件，不保存复盘 API 数据或榜单请求。

需要 .NET SDK 8.0.100 和 Python 3：

```powershell
dotnet workload install wasm-tools --skip-manifest-update
python -m unittest discover -s api -p 'test_*.py'
node --test tests/resource-cache.test.cjs
dotnet run --project tests/SimSmoke -c Release
./tools/build.ps1
python -m http.server 8765 --bind 127.0.0.1 --directory dist
```

`dist/` 是可独立托管的静态站点。`wwwroot/settings.json` 配置只读取数服务地址。推送 `main` 后，GitHub Actions 构建和发布。

## 取数服务

`api/replay_service.py` 在 Linux 服务器运行，复用既有采集器的令牌缓存，不在浏览器登录 Steam，不启动额外 Steam 会话。服务器本地需要已有 `collector.api` 模块、`requests` 和令牌缓存；它们不包含在仓库内。通过 `YX_COLLECTOR_ROOT` 配置既有采集器路径，`YX_REPLAY_STORE` 配置快照目录，`YX_TLS_CERT`、`YX_TLS_KEY` 配置证书，`YX_REPLAY_ORIGINS` 配置允许的网页来源。

```sh
python3 api/replay_service.py --collect-ladder
YX_STATS_START_CODE=<已验证的起始编号> python3 api/replay_service.py --collect-stats
python3 api/replay_service.py --port 8443
```

提供 `/api/v1/replays/{code}`、`/api/v1/ladder?season=11`、`/api/v1/ladder/seasons` 和快照索引 `/api/v1/ladder/history?season=11`。systemd 示例在 `api/`，部署时按实际机器修改路径。接口只允许 GET，带请求频率限制。HTTPS IP 证书的续期由服务器既有证书任务负责，服务会重新载入更新后的证书。

`/api/v1/ladder?season=11&from=<毫秒时间戳>&to=<毫秒时间戳>` 返回所选区间的快照对比，边界包含起止时间。`comparison` 提供请求时间、实际快照数量、可用时间范围、是否可比较及新入/离榜人数；`baselineAt` 和 `generatedAt` 是实际采用的快照时间。没有起止参数时继续返回最近快照及相邻快照变化。

### 统计接口与调度

统计接口为 `/api/v1/stats?from=<毫秒时间戳>&to=<毫秒时间戳>&minScore=3000&maxScore=4000&character=0&minGames=5`。`character=0` 表示玩家列表含全部角色；角色汇总始终覆盖所选时间与分段内的全部角色。支持 `q` 搜索名称、`sort=count|average|hourly|net`、`offset`、`limit` 分页（每页最多 100 人）。返回采集范围与缺失状态，不暴露原始 UID。

`yx-rank-stats.timer` 使用整十分钟调度；部署前在 `/root/yx-replay/rank-stats.env` 中设置 `YX_STATS_START_CODE`，可选设置 `YX_STATS_MAX_CALLS`、`YX_STATS_SECONDS`、`YX_STATS_WORKERS`（1–8）和 `YX_UPSTREAM_GAP`（默认 0.2 秒，最少 0.1 秒）。采集器使用进程锁防止重叠；统计、回放和榜单采集跨进程协调请求开始时间，网络请求不占用调度锁。上游服务错误触发共享冷却并停止本轮新增请求。失败不会删除已提交的精简战绩，断点和未完成任务均保留。

玩家明细接口为 `/api/v1/players/{匿名玩家标识}/matches`，支持 `from`、`to`、`minScore`、`maxScore`、`character`、`offset`、`limit`。匿名标识与天梯快照保持一致，按标识查询，不按名称猜测身份。结果只包含该玩家的精简统计和已验证的复盘码，不包含原始 UID、卡牌或手牌。全局采集范围、该玩家已采集范围和当前筛选场数分别返回。

## 来源

- sim：本地取得的 `yisim_CSharp / Yx.BattleSim`，浏览器移植改为单线程搜索，并修复早期牌位数量和体魄开局恢复。
- 卡片渲染与图片：[sharpobject/yxp_wiki](https://sharpobject.github.io/yxp_wiki/assets/card-components/atlas-gallery.html)，保留其卡片组件结构。
- 《弈仙牌》及游戏卡牌、配置素材版权归相应权利人。本项目与游戏官方无关。
