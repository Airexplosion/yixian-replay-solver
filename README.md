# 弈仙复盘

粘贴游戏复盘代码，自动还原各轮双方的牌桌、手牌和开战仙命，在浏览器里限时搜索摆法。另有按赛季显示的天梯总榜。

- [复盘求解](https://airexplosion.github.io/yixian-replay-solver/)
- [赛季天梯榜](https://airexplosion.github.io/yixian-replay-solver/ladder.html)

网页由 GitHub Pages 托管。首次打开先下载 .NET WebAssembly sim 和卡牌规则，加载完成才开放操作。求解在 Web Worker 中执行，可以停止；不需要安装游戏。复盘代码通过取数服务换成盘面数据，服务只返回求解所需状态，不返回账户令牌或原始玩家 UID。

## 当前范围

这是 C# sim 的浏览器实验版，基于目前拿到的源码移植，并非原生求解器 v0.2.6 的完整源码。部分机制仍有偏差；含尚未移植的仙魔策略时会拒绝求解。限时推荐也不保证全局最优。请用游戏实战复核。

天梯页面使用游戏天梯总榜入口 `gameData/fetchLeaderboard` 的 `category=0`，按赛季保存官方返回的前 100 名。随机角色道心榜在这个接口里用 `category=-1`。服务器约每 10 分钟采集，页面增减值是相邻快照的净变化；新上榜玩家无前一次基线时显示“—”。它不覆盖所有玩家，也不提供每场加减分。

## 本地构建

需要 .NET SDK 8.0.100 和 Python 3：

```powershell
dotnet workload install wasm-tools --skip-manifest-update
python -m unittest discover -s api -p 'test_*.py'
dotnet run --project tests/SimSmoke -c Release
./tools/build.ps1
python -m http.server 8765 --bind 127.0.0.1 --directory dist
```

`dist/` 是可独立托管的静态站点。`wwwroot/settings.json` 配置只读取数服务地址。推送 `main` 后，GitHub Actions 构建和发布。

## 取数服务

`api/replay_service.py` 在 Linux 服务器运行，复用既有采集器的令牌缓存，不在浏览器登录 Steam，不启动额外 Steam 会话。服务器本地需要已有 `collector.api` 模块、`requests` 和令牌缓存；它们不包含在仓库内。通过 `YX_COLLECTOR_ROOT` 配置既有采集器路径，`YX_REPLAY_STORE` 配置快照目录，`YX_TLS_CERT`、`YX_TLS_KEY` 配置证书，`YX_REPLAY_ORIGINS` 配置允许的网页来源。

```sh
python3 api/replay_service.py --collect-ladder
python3 api/replay_service.py --port 8443
```

提供 `/api/v1/replays/{code}`、`/api/v1/ladder?season=11`、`/api/v1/ladder/seasons` 和快照索引 `/api/v1/ladder/history?season=11`。systemd 示例在 `api/`，部署时按实际机器修改路径。接口只允许 GET，带请求频率限制。HTTPS IP 证书的续期由服务器既有证书任务负责，服务会重新载入更新后的证书。

## 来源

- sim：本地取得的 `yisim_CSharp / Yx.BattleSim`，浏览器移植改为单线程搜索，并修复早期牌位数量和体魄开局恢复。
- 卡片渲染与图片：[sharpobject/yxp_wiki](https://sharpobject.github.io/yxp_wiki/assets/card-components/atlas-gallery.html)，保留其卡片组件结构。
- 《弈仙牌》及游戏卡牌、配置素材版权归相应权利人。本项目与游戏官方无关。
