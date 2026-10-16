# 弈仙数据 API

基础地址：`https://154.9.252.167:8443/api/v1`

原 GitHub Pages 网站已关闭，后台采集、分析及多核求解独立运行。接口返回 UTF-8 JSON，无需 API 密钥。同一 IP 的请求间隔至少 1 秒，建议 1.2 秒；收到 429 时等待后重试。爬虫约每 10 分钟执行一轮，统计接口读取已采集数据，复盘接口按需请求游戏服务。求解使用 `POST` 提交、`GET` 轮询、`DELETE` 取消。

## 接口目录

以下路径均相对于基础地址。

| 路径 | 用途 | 主要返回字段 |
| --- | --- | --- |
| `/` | 可用接口与调用限制 | `endpoints`, `collectionIntervalSeconds`, `solver` |
| `/health` | API 服务健康检查 | `ok`, `service`, `mode` |
| `/collection` | 采集任务、数据量和完整性 | `generatedAt`, `coverage` |
| `/ladder/seasons` | 已采集的赛季 | `seasons` |
| `/ladder?season=11` | 最近天梯快照 | `seasonId`, `generatedAt`, `players` |
| `/ladder?season=11&from=...&to=...` | 时间区间内的积分、排名变化 | `baselineAt`, `generatedAt`, `comparison`, `players` |
| `/ladder/history?season=11` | 历史快照文件名索引 | `seasonId`, `snapshots` |
| `/stats` | 角色与玩家上分分析 | `filters`, `summary`, `characters`, `players`, `coverage` |
| `/players/{id}/matches` | 指定玩家逐场战绩 | `player`, `summary`, `characters`, `matches`, `coverage` |
| `/replays/{code}` | 复盘盘面、手牌与开战状态 | `version`, `selectedRound`, `rounds`, `match`, `catalogVersion` |
| `/solver` | 引擎版本、百家支持和可用核心数 | `version`, `cores`, `defaultThreads`, `modes`, `baijia` |
| `/catalog` | 编号种类与数量；可按 `kind`、`q`、`offset`、`limit` 搜索 | `kinds` 或 `items` |
| `/catalog/{kind}/{id}` | 指定编号的名称、详细效果与参数 | `id`, `kind`, `name`, `description`, `descriptionTemplate`, `otherParams` |

## 百家之道摆牌求解

当前使用与游戏内 2026-10-15 适配版相同的计算核心和规则数据，战绩版本 `001.0008.0002`。支持百家（`xianMoStrategies`、`xianMoTempDatas`）、天衍、仙命、刻印、共鸣、速度和死战规则。原 30 秒上限与单线程限制已移除。

1. `GET /replays/{code}` 导入复盘，取需要轮次的 `rounds[i].request`，也可使用 MOD 导出的完整盘面 JSON。
2. 对盘面设置 `threads`、`searchMode`、`first` 等参数。
3. `POST /solve`，请求正文为盘面 JSON。成功返回 `202`、不可猜测的字符串 `job`、实际 `threads`、可用 `cores` 和轮询路径。
4. `GET /solve/{job}` 查询进度与当前结果，建议间隔 2 秒。终态为 `done`、`cancelled` 或 `error`；终态可重复读取，不会取一次就删除。
5. `DELETE /solve/{job}` 取消；随后继续查询至 `cancelled`。任务 ID 同时用于访问结果和取消，请自行保管。

| 参数 | 说明 |
| --- | --- |
| `threads` | `0` 默认用全部可用服务端逻辑核；正数指定线程数，超过服务端可用核数时自动压到上限。实际值随任务返回。 |
| `searchMode` | 默认 `exhaustive`：遍历全部合法摆法并持续排名，找到赢法仍继续；`existence`：找到任意赢法即停，否则遍历到底；`rank`：按时限和收敛条件给推荐。 |
| `timeLimitMs` | 仅 `rank` 模式生效，默认 8000；可设为 60000、300000 等，不再限制 30 秒。完整遍历模式忽略此参数和 `exhaustiveLimit`。 |
| `first` | 默认 `both`，双算先后手，等权排名；也支持 `me`、`foe`、`auto`（按每套摆法的速度判断）。 |
| `deathFightMode` | `record` 保留历史战绩死战，默认；编辑盘面并按当前修为重算时用 `auto`，也支持 `tie`、`off`。 |
| `topN` | 返回前几名，1–20，默认 5；双先后手时可能附加某一顺序独有的赢法。 |
| `samples` | 随机参数抽样次数，1–100，默认 3；没有随机参数时每个先后手只算一次。 |

`enumerated / candidates` 是完整遍历进度，`evaluated` 是实际战斗评估次数，双先后手和多个对手会增加此数。`best` 含牌序、排序分和各先后手战果，另带 `cardDetails`（卡名与完整效果）。`exhaustive=true` 才表示摆法空间已经遍历完；取消或找到赢法即停不会冒充全量完成。存在随机机制时，完整遍历不等于穷举所有随机分支。

同一服务同时只运行一个求解任务，繁忙时返回 `409`。任务保持轮询即可一直运行，没有 30 秒总时限；连续 15 分钟没有查询的运行任务会自动取消以回收算力。完成结果在当前进程内保留至少一小时，API 重启会清理任务。采集器不因求解任务被停止。

直接使用命令行客户端：

```powershell
python tools/solve_client.py --replay <复盘码> --round 15 --threads 0 --mode exhaustive --output result.json
python tools/solve_client.py --input puzzle.json --threads 2 --mode rank --seconds 120
```

客户端会显示仙命与策略名称、详细效果、实际线程数、遍历进度和最终摆牌名称，Ctrl+C 发送取消请求。

## 编号名称与详细效果

编号需要带类型，避免同号不同义：`talent` 仙命、`fate` 天衍策略、`xianmo` 百家策略、`keyin` 刻印、`resonance` 共鸣、`buff` 状态、`card` 卡牌、`character` 角色。

例如 `GET /catalog/talent/10005` 返回“善假于物”，效果为“抽2张副职牌”。`GET /catalog?kind=xianmo&q=追击&limit=20` 可按名称或效果搜索百家策略。名称与描述来自当前本地游戏配置及本地化，配置参数已代入；原描述保留在 `descriptionTemplate`。

未知编号返回 `known=false`（单项查询 HTTP 404）。游戏没有提供名称或描述的内部项分别标记 `nameAvailable=false`、`descriptionAvailable=false`；需要局面参数但未能静态代入时列在 `unresolvedFields`，不会猜测效果。

`/replays/{code}` 保留所有数字字段，同时为每一方增加 `details`，包含仙命、天衍、百家、刻印、共鸣、角色和常驻状态的名称与效果。百家开关参数从该轮私有快照读取；未采到的参数保留为空，不按名称猜测开关。

`{id}` 是 `/ladder` 或 `/stats` 返回的 20 位十六进制匿名玩家 ID。同一玩家跨接口使用同一 ID，查询时无需原始 UID。`{code}` 是游戏分享的复盘码；战绩返回的 `replayCode` 非空时也可直接使用。

## 统计筛选

`/stats` 支持下列参数。`/players/{id}/matches` 支持其中的时间、积分、角色和分页参数。

| 参数 | 含义 | `/stats` 默认值 |
| --- | --- | --- |
| `from` | 结算时间起点，Unix 毫秒，包含 | 当前时间往前 7 天 |
| `to` | 结算时间终点，Unix 毫秒，不包含 | 当前时间往后 1 分钟 |
| `minScore` | 每局开局积分下限，包含 | `3000` |
| `maxScore` | 每局开局积分上限，不包含 | `4000` |
| `character` | 角色数字 ID，`0` 表示全部 | `0` |
| `minGames` | 样本门槛，仅标记 `eligible`，不会删除低样本行 | `5` |
| `q` | 玩家名称关键字，需 URL 编码 | 空 |
| `sort` | `count` 场数、`net` 净分、`average` 场均净分、`hourly` 每小时净分 | `count` |
| `offset` | 玩家列表起始位置 | `0` |
| `limit` | 返回玩家数，`1`–`100` | `50` |

玩家战绩接口默认查询该玩家全部已采集历史、全部积分范围、全部角色，每页 50 条，按结算时间倒序。其 `offset`、`limit` 用于逐场战绩分页。

查询全部积分范围时显式传 `minScore=0&maxScore=100001`，避免落入默认 3000–4000 分段。统计中的 `matches` 指玩家战绩条数，`games` 指去重对局数；一个对局可含多条不同玩家的战绩。

`/stats` 的 `summary` 和顶层 `characters` 汇总所选时间与积分范围内的全部角色；`character`、`q` 只进一步筛选 `players` 列表。`playerCount` 是分页前总人数。

分析字段：`net` 累计净分，`average` 场均净分，`minutes` 对局总分钟数，`hourly` 按总净分除以总对局耗时换算每小时净分，`share` 是角色使用占比。每小时净分不包含排队时间。无样本时部分汇总值为 `null`。

逐场战绩包括 `codeId`、`characterId`、`beginTs`、`endTs`、`scoreBefore`、`scoreChange`、`scoreAfter`、`minutes`、`randomCharacter`、`version`、`replayCode`。早期未保存有效入口的战绩 `replayCode` 为 `null`。

## 天梯区间分析

`season` 默认 `11`，建议先请求 `/ladder/seasons` 取得可用赛季。`from`、`to` 必须同时传入，单位为 Unix 毫秒；天梯快照的区间包含起止时间。

区间内第一份和最后一份快照作为比较端点。`comparison.canCompare` 表示是否至少有两份有效快照，`availableFrom`、`availableTo` 表示已保存范围，`startTruncated`、`endTruncated` 表示请求区间超出采集范围。

每位玩家返回 `startScore`、`score`、`delta`、`startRank`、`rank`、`rankDelta` 和 `status`（`both`、`entered`、`left`、`uncompared`）。缺少比较端点的数值为 `null`，不能按零处理。结果按期末排名排列，需要按净上分排序时调用方按 `delta` 排序。

未传时间范围时，`delta` 与 `rankDelta` 是最近一次采集和上一份快照的差值。天梯数据仅覆盖官方返回的前 100 名。`/ladder/history` 返回文件名索引，不提供任意文件下载入口；历史变化通过带时间参数的 `/ladder` 查询。

## 采集进度

`/collection` 返回全局 `coverage`，统计与玩家战绩响应中也带同一结构：

- `playerMatches`、`games`：已保存玩家战绩条数与去重对局数。
- `earliestEnd`、`latestEnd`：已采集战绩的最早、最近结算时间，Unix 毫秒。
- `lastRun.status`、`startedAt`、`finishedAt`、`calls`、`added`、`workers`：最近采集轮次的状态和进度；运行中 `finishedAt` 为 `null`。
- `verifiedGames`、`missingPlayerMatches`、`rosterUnknownGames`：参赛名单核验和缺失人数。
- `unscannedCodes`、`unavailableCodes`、`pendingCodes`、`partialCodes`：编号扫描及待补查缺口。
- `intervalComplete`：已检查编号范围内是否完整；`complete` 当前始终为 `false`，不宣称覆盖全服或全赛季。

`/health` 只说明 HTTP 服务可用。判断采集是否推进，应比较多次 `/collection` 的 `latestEnd`、`lastRun` 与数据量；建议每 10 分钟查询一次。

## 调用示例

PowerShell：

```powershell
$api = 'https://154.9.252.167:8443/api/v1'
Invoke-RestMethod "$api/collection"
Start-Sleep -Milliseconds 1200
Invoke-RestMethod "$api/ladder?season=11"
Start-Sleep -Milliseconds 1200
$stats = Invoke-RestMethod "$api/stats?minScore=0&maxScore=100001&minGames=1&sort=net&limit=10"
$stats.characters
$stats.players
if ($stats.players.Count -gt 0) {
    Start-Sleep -Milliseconds 1200
    $id = $stats.players[0].id
    Invoke-RestMethod "$api/players/$id/matches?limit=20"
}
```

Python（仅标准库）：

```python
import json
import time
from urllib.parse import urlencode
from urllib.request import urlopen

BASE = 'https://154.9.252.167:8443/api/v1'

def get(path, **params):
    query = '?' + urlencode(params) if params else ''
    with urlopen(BASE + path + query, timeout=60) as response:
        return json.load(response)

stats = get('/stats', minScore=0, maxScore=100001,
            minGames=1, q='玩家名称', sort='net', limit=10)
for player in stats['players']:
    print(player['id'], player['name'], player['net'])
    time.sleep(1.2)
    history = get('/players/' + player['id'] + '/matches', limit=20)
    print(history['matches'])
```

时间参数可用 `int(time.time() * 1000)` 生成，所有时间筛选与本地显示时区无关。

## HTTP 状态与范围

`200` 成功，`202` 求解已提交，`400` 参数错误，`403` 浏览器 Origin 未允许，`404` 接口、编号、任务或赛季快照不存在，`409` 求解繁忙，`413` 盘面正文过大，`429` 请求过快，`503` 服务暂时不可用。错误正文为 `{"error":"说明"}`。现有服务不保证返回 `Retry-After`，调用方应自行等待并有限重试。

当前可从 Python、curl、PowerShell 或服务端直接调用。浏览器跨域调用仍受既有 Origin 白名单限制。匿名玩家 ID 只能关联已采集样本；未采到的数据不代表零场。复盘是否能取回取决于游戏服务保存期限。

`/replays/{code}` 只负责导入局面；摆牌搜索通过 `/solve` 异步执行。仓库保留原浏览器实验源码，线上仍不发布该网站。
