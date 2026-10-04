# 已验证的榜单与战绩入口

核验时间：2026-10-04。这里只保存接口结构与非个人化测试结果，不包含账户凭据或原始玩家 UID。

## 天梯榜

游戏客户端 `RankLeaderboardPart.Refresh` 调用 `LeaderboardPanel.Fetch(seasonId, 0, RealTimeLeaderboard)`，后者请求 `gameData/fetchLeaderboard`，将第二个参数写入 `category`。

第 11 赛季实测：`category=0` 返回 100 条天梯记录；`category=1` 返回成功但为空；`category=-1` 返回随机角色道心记录；`category=1000001` 返回对应角色道心记录。天梯数据包含 `rank` 和 `rankScore`。

## 战斗过程和每场积分

`gameStat/fetchPlayerBattleInfo` 接受普通复盘码。游戏复盘码的数值包含 `codeId * 1000 + round * 10 + battleRank`；分享码另做反转和异或编码。游戏客户端 `BattleInfoExtension.GetRecordCode` 与 `ProjectUtils` 中可以核对该结构。

有限抽查已取得榜单之外的排位记录。已验证样本 `gma6ttbzte` 返回开局积分 3522、积分变化 +72、18 轮战斗，属于排位模式。这证明战绩接口的覆盖范围大于榜单前 100 名。数据中有 `beginRankScore`、`diffRankScore`、`roundStats`；每轮含双方开战状态及卡组。

需要区分：已知对局编号可取记录，不等于已经找到全量索引。抽查中有不存在的编号、非排位模式、无轮次记录。暂未确认历史保留范围、最新编号入口、编号连续性、全部赛季完整覆盖；不能声称已抓全游戏全部战绩，也不能用部分战绩重建官方全服积分榜。
