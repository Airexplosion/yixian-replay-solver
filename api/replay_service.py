"""Standalone replay, leaderboard and statistics API. No credentials in responses."""
import argparse, collections, contextlib, datetime as dt, hashlib, json, os, pathlib, re, ssl, sys, threading, time
import rank_stats
import ladder_history
import metadata_catalog
import solver_jobs
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit, parse_qs

ROOT = pathlib.Path(os.environ.get('YX_COLLECTOR_ROOT', '/root/zongmen-dabi'))
STORE = pathlib.Path(os.environ.get('YX_REPLAY_STORE', '/var/lib/yx-replay'))
ORIGINS = set(os.environ.get('YX_REPLAY_ORIGINS', 'https://airexplosion.github.io,http://127.0.0.1:8765').split(','))
UPSTREAM_LOCK = threading.Lock()
CACHE = collections.OrderedDict()
CACHE_LOCK = threading.Lock()
RATE_LOCK = threading.Lock()
RATE = {}

def decode_code(code):
    if not re.fullmatch(r'[a-zA-Z0-9]{5,14}',code): raise ValueError('无效复盘代码')
    value = int(code,36) ^ 0x5FF17843B6B1F
    text=str(value)
    raw=int(text[0]+text[:0:-1])
    if len(str(raw))>=19 or raw<=0: raise ValueError('无效复盘代码')
    alphabet='0123456789abcdefghijklmnopqrstuvwxyz'; plain=''; n=raw
    while n: n,r=divmod(n,36);plain=alphabet[r]+plain
    return plain, (raw%1000)//10

def gameplay_side(player, pool=False):
    public=player['publicData'];private=player.get('privateData') or {}
    snap=public.get('lastRoundData')
    if not isinstance(snap,dict): raise ValueError('所选轮次缺少开战快照')
    board=list(snap.get('usedCards') or [])
    hand=list(snap.get('handCards') or [])
    slots=int(snap.get('unlockGrids') or 0)
    if slots not in range(1,9) or len(board)>8: raise ValueError('复盘牌位数据不完整')
    if len(board)>slots:raise ValueError('复盘牌桌超过已解锁格数')
    board.extend([0]*(slots-len(board)))
    def params(data):
        return {k:list(v.get('commonParams') or []) for k,v in (data or {}).items() if isinstance(v,dict)}
    resonance=((snap.get('talentResonanceData') or {}).get('selectionData') or {}).get('selected') or 0
    result={
        'hp':int(snap.get('extraMaxHp') or 0),'level':int(snap.get('level') or public.get('level') or 0),
        'life':int(snap.get('life') or 0),'exp':int(snap.get('exp') or 0),
        'characterId':public.get('characterId') or 0,'sect':public.get('sect') or 0,'career':public.get('career') or 0,
        'talents':list(snap.get('talents') or []),'keyin':list(snap.get('usedKeYinCards') or []),
        'fate':list(snap.get('fateStrategies') or []),'xianMoStrategies':list(snap.get('xianMoStrategies') or []),
        'xianMoTempDatas':dict((private.get('xianMoData') or {}).get('tempDatas') or {}),
        'resonance':resonance,'permanentBuffs':dict(snap.get('permanentBuffTempDatas') or {}),
        'talentTempDatas':dict(snap.get('talentTempDatas') or {}),'resonanceFlags':dict(public.get('resonanceTalentFlags') or {}),
        'talentDatas':params(snap.get('talentDatas')),'privateTalentDatas':params(snap.get('privateTalentDatas')),
        'maxKeYin':int((private.get('keYinData') or {}).get('maxKeYin') or 0),
        'handCards':hand,'unlockGrids':slots,'cards':board+hand if pool else board,
    }
    if pool: result['initial']=board
    return result

def normalize_replay(data, selected=0):
    rounds=[]
    for battle in data.get('roundStats') or []:
        first=battle['p1'];second=battle['p2']
        view=battle.get('mainViewId') or data.get('uid')
        if second['publicData'].get('uid')==view: first,second=second,first
        me=gameplay_side(first,True);foe=gameplay_side(second)
        req={'round':int(battle['round']),'slots':me['unlockGrids'],
             'first':'me' if battle.get('firstPlayerId')==first['publicData'].get('uid') else 'foe',
             'maxConsumeSustain':2+sum((int(c)%10000)==64 for c in me['keyin'])+int(me['permanentBuffs'].get('10049',0)),
             'me':me,'foe':foe}
        rounds.append({'round':req['round'],'request':req})
    if not rounds: raise ValueError('这份战绩没有可用的轮次')
    before=int(data.get('beginRankScore') or 0)
    change=int(data.get('diffRankScore') or 0)
    return {'rounds':rounds,'selectedRound':selected or rounds[-1]['round'],'version':data.get('version',''),
            'match':{'gameMode':data.get('gameMode'),'beginTs':data.get('beginTs'),'endTs':data.get('endTs'),
                     'rankBefore':before,'rankChange':change,'rankAfter':before+change,
                     'placement':int(data.get('battleRank') or 0)+1}}

def reserve_upstream_slot():
    """Reserve request starts across processes; never hold a lock during HTTP."""
    with UPSTREAM_LOCK:
        STORE.mkdir(parents=True,exist_ok=True)
        with (STORE/'upstream.lock').open('a+') as lock:
            if sys.platform!='win32':
                import fcntl
                fcntl.flock(lock,fcntl.LOCK_EX)
            lock.seek(0);saved=lock.read().strip()
            state=json.loads(saved) if saved else {}
            if isinstance(state,(int,float)):state={'next':state}
            now=time.time()
            when=max(now,state.get('next',0),state.get('cooldown',0))
            gap=max(0.1,float(os.environ.get('YX_UPSTREAM_GAP','0.2')))
            state['next']=when+gap
            lock.seek(0);lock.truncate();lock.write(json.dumps(state));lock.flush()
    return when


def upstream_cooldown(seconds=30):
    with UPSTREAM_LOCK:
        with (STORE/'upstream.lock').open('a+') as lock:
            if sys.platform!='win32':
                import fcntl
                fcntl.flock(lock,fcntl.LOCK_EX)
            lock.seek(0);saved=lock.read().strip()
            state=json.loads(saved) if saved else {}
            if not isinstance(state,dict):state={}
            state['cooldown']=max(state.get('cooldown',0),time.time()+seconds)
            lock.seek(0);lock.truncate();lock.write(json.dumps(state));lock.flush()


def upstream(path,params,allow_missing=False):
    if str(ROOT) not in sys.path:sys.path.insert(0,str(ROOT))
    from collector import api
    when=reserve_upstream_slot()
    wait=when-time.time()
    if wait>0:time.sleep(wait)
    # Reuse the collector's refreshed token; never create concurrent Steam sessions.
    cached=json.loads((ROOT/'collector/.token_cache.json').read_text())
    try:
        response=api.api_post(path,params,cached['token'],timeout=25)
        if response.get('code') not in (0,1):raise RuntimeError('upstream unavailable')
    except Exception:
        upstream_cooldown()
        raise
    if allow_missing and response.get('code')==0: return None
    if response.get('code')!=1: raise RuntimeError('游戏服务暂时不可用（%s）'%response.get('code'))
    return response['data']

def replay(code):
    plain,selected=decode_code(code)
    with CACHE_LOCK:
        saved=CACHE.get(plain)
        if saved and time.monotonic()-saved[0]<600:
            CACHE.move_to_end(plain); return saved[1]
    data=upstream('/gameStat/fetchPlayerBattleInfo',{'code':plain})
    if data.get('version')!='001.0008.0002':raise ValueError('当前求解器仅支持战绩版本 001.0008.0002')
    result=metadata_catalog.decorate_replay(normalize_replay(data,selected))
    try: rank_stats.record_replay(STORE,data,lookup_rank=int(plain,36)%10)
    except (ValueError,OSError): pass
    data=None
    with CACHE_LOCK:
        CACHE[plain]=(time.monotonic(),result)
        while len(CACHE)>64: CACHE.popitem(last=False)
    return result

def ladder_path(season): return STORE/'ladder'/str(season)

def collection_status():
    """Read collection coverage in one SQLite snapshot, without calling upstream."""
    with contextlib.closing(rank_stats.connect(STORE)) as db:
        db.execute('BEGIN')
        return {'generatedAt':int(time.time()*1000),'coverage':rank_stats.coverage(db)}

def api_index():
    return {'service':'yx-replay','version':'v1','mode':'api-only',
            'basePath':'/api/v1','collectionIntervalSeconds':600,
            'minimumRequestIntervalSeconds':1,
            'endpoints':[
                {'method':'GET','path':'/api/v1/health','description':'服务健康状态'},
                {'method':'GET','path':'/api/v1/collection','description':'战绩采集进度、覆盖范围与缺口'},
                {'method':'GET','path':'/api/v1/ladder/seasons','description':'已采集赛季'},
                {'method':'GET','path':'/api/v1/ladder','description':'天梯榜；season、from、to 筛选'},
                {'method':'GET','path':'/api/v1/ladder/history','description':'赛季快照索引；season 筛选'},
                {'method':'GET','path':'/api/v1/stats','description':'角色与玩家上分分析、名称搜索和分页'},
                {'method':'GET','path':'/api/v1/players/{id}/matches','description':'匿名玩家标识对应的逐场战绩'},
                {'method':'GET','path':'/api/v1/replays/{code}','description':'复盘盘面与开战状态'}],
            'documentation':'https://github.com/Airexplosion/yixian-replay-solver/blob/main/API.md',
            'solver':{'available':True,'description':'百家之道多核求解，支持完整遍历、找赢法与限时推荐'},
            'solverEndpoints':[
                {'method':'GET','path':'/api/v1/solver','description':'求解版本、可用核心数与搜索模式'},
                {'method':'POST','path':'/api/v1/solve','description':'提交盘面，返回异步任务'},
                {'method':'GET','path':'/api/v1/solve/{job}','description':'进度、当前最优摆法与终态，可重复查询'},
                {'method':'DELETE','path':'/api/v1/solve/{job}','description':'取消任务'},
                {'method':'GET','path':'/api/v1/catalog','description':'编号分类、名称和效果搜索'},
                {'method':'GET','path':'/api/v1/catalog/{kind}/{id}','description':'指定编号的名称与详细效果'}]}

def collect_ladder():
    sys.path.insert(0,str(ROOT));from collector import config
    seasons=[config.SEASON_ID]
    if config.SEASON_ID>1: seasons.append(config.SEASON_ID-1)
    for season in seasons:
        data=upstream('/gameData/fetchLeaderboard',{'seasonId':season,'category':0,'type':0})
        if not isinstance(data,list) or not data: continue
        directory=ladder_path(season);directory.mkdir(parents=True,exist_ok=True)
        previous={}
        latest=directory/'latest.json'
        baseline=None
        if latest.exists():
            prior=json.loads(latest.read_text());baseline=prior['generatedAt']
            previous={p['id']:p for p in prior['players']}
        now=dt.datetime.now(dt.timezone.utc)
        players=[]
        for p in data:
            key=hashlib.sha256(str(p['uid']).encode()).hexdigest()[:20]
            old=previous.get(key)
            players.append({'id':key,'rank':int(p['rank']),'score':int(p['rankScore']),
                'name':p.get('username') or '未命名玩家','delta':int(p['rankScore'])-old['score'] if old else None,
                'rankDelta':old['rank']-int(p['rank']) if old else None})
        snapshot={'seasonId':season,'generatedAt':now.isoformat(),'coverage':'官方接口返回的赛季前 100 名',
                  'intervalSeconds':600,'baselineAt':baseline,'players':players,'count':len(players)}
        encoded=json.dumps(snapshot,ensure_ascii=False)
        # Preserve a stable baseline for historical seasons, avoiding duplicate archives.
        same=previous and all(p['id'] in previous and p['score']==previous[p['id']]['score'] and p['rank']==previous[p['id']]['rank'] for p in players)
        if not same or season==config.SEASON_ID:
            archive=directory/(now.strftime('%Y%m%dT%H%M%SZ')+'.json')
            staged=archive.with_suffix('.tmp');staged.write_text(encoded);staged.replace(archive)
        temp=directory/'latest.tmp';temp.write_text(encoded);temp.replace(latest)
        print('rank snapshot season=%s count=%s'%(season,len(players)),flush=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args): pass  # Do not log user-supplied replay identifiers.
    def origin(self): return self.headers.get('Origin')
    def send(self,status,data):
        payload=json.dumps(data,ensure_ascii=False).encode()
        self.send_response(status);self.send_header('Content-Type','application/json; charset=utf-8')
        self.send_header('Content-Length',str(len(payload)));self.send_header('Cache-Control','no-store')
        self.send_header('X-Content-Type-Options','nosniff');self.send_header('Vary','Origin')
        if self.origin() in ORIGINS: self.send_header('Access-Control-Allow-Origin',self.origin())
        self.end_headers();self.wfile.write(payload)
    def do_OPTIONS(self):
        if self.origin() not in ORIGINS: return self.send(403,{'error':'来源未允许'})
        self.send_response(204);self.send_header('Access-Control-Allow-Origin',self.origin())
        self.send_header('Access-Control-Allow-Methods','GET, POST, DELETE, OPTIONS')
        self.send_header('Access-Control-Allow-Headers','Content-Type');self.send_header('Vary','Origin');self.end_headers()
    def authorize(self):
        if self.origin() and self.origin() not in ORIGINS:
            self.send(403,{'error':'来源未允许'});return False
        ip=self.client_address[0]
        with RATE_LOCK:
            now=time.monotonic()
            if now-RATE.get(ip,0)<1:
                self.send(429,{'error':'请求太快，请稍后重试'});return False
            if len(RATE)>4096: RATE.clear()
            RATE[ip]=now
        return True
    def do_POST(self):
        if not self.authorize():return
        if urlsplit(self.path).path!='/api/v1/solve':return self.send(404,{'error':'接口不存在'})
        try:
            length=int(self.headers.get('Content-Length','0'))
            if length<=0 or length>262144:return self.send(413,{'error':'盘面 JSON 须为 1–262144 字节'})
            request=json.loads(self.rfile.read(length).decode('utf-8'))
            return self.send(202,solver_jobs.JOBS.start(request))
        except UnicodeDecodeError:self.send(400,{'error':'盘面 JSON 须使用 UTF-8 编码'})
        except ValueError as error:self.send(400,{'error':str(error)})
        except solver_jobs.BusyError as error:self.send(409,{'error':str(error)})
        except Exception:self.send(503,{'error':'求解服务暂时不可用'})
    def do_DELETE(self):
        if not self.authorize():return
        match=re.fullmatch(r'/api/v1/solve/([A-Za-z0-9_-]{32})',urlsplit(self.path).path)
        if not match:return self.send(404,{'error':'求解任务不存在'})
        try:self.send(200,solver_jobs.JOBS.get(match[1]).cancel())
        except KeyError:self.send(404,{'error':'求解任务不存在或已过期'})
        except Exception:self.send(503,{'error':'取消任务失败，请查询任务状态后重试'})
    def do_GET(self):
        if not self.authorize():return
        url=urlsplit(self.path)
        try:
            if url.path in ('/api/v1','/api/v1/'): return self.send(200,api_index())
            if url.path=='/api/v1/health': return self.send(200,{'ok':True,'service':'yx-replay','sim':'browser','mode':'api-only'})
            if url.path=='/api/v1/collection': return self.send(200,collection_status())
            if url.path=='/api/v1/solver':return self.send(200,solver_jobs.JOBS.capabilities())
            if url.path=='/api/v1/catalog':return self.send(200,metadata_catalog.query(parse_qs(url.query)))
            catalog_match=re.fullmatch(r'/api/v1/catalog/([a-z]+)/(-?\d+)',url.path)
            if catalog_match:
                item=metadata_catalog.lookup(catalog_match[1],int(catalog_match[2]))
                return self.send(200 if item['known'] else 404,item)
            job_match=re.fullmatch(r'/api/v1/solve/([A-Za-z0-9_-]{32})',url.path)
            if job_match:
                try:job=solver_jobs.JOBS.get(job_match[1])
                except KeyError:return self.send(404,{'error':'求解任务不存在或已过期'})
                return self.send(200,solver_jobs.decorate_report(job.snapshot()))
            match=re.fullmatch(r'/api/v1/replays/([a-zA-Z0-9]{5,14})',url.path)
            if match: return self.send(200,replay(match[1]))
            if url.path=='/api/v1/stats':
                return self.send(200,rank_stats.query(STORE,parse_qs(url.query)))
            player_match=re.fullmatch(r'/api/v1/players/([a-f0-9]{20})/matches',url.path)
            if player_match:
                return self.send(200,rank_stats.player_history(STORE,player_match[1],parse_qs(url.query)))
            if url.path=='/api/v1/ladder':
                params=parse_qs(url.query)
                season=int(params.get('season',['11'])[0]);path=ladder_path(season)/'latest.json'
                if not path.exists(): return self.send(404,{'error':'这个赛季还没有采集快照'})
                if 'from' in params or 'to' in params:
                    return self.send(200,ladder_history.compare(ladder_path(season),season,params))
                return self.send(200,json.loads(path.read_text()))
            if url.path=='/api/v1/ladder/seasons':
                paths=sorted((STORE/'ladder').glob('*/latest.json'))
                return self.send(200,{'seasons':[int(p.parent.name) for p in paths]})
            if url.path=='/api/v1/ladder/history':
                season=int(parse_qs(url.query).get('season',['11'])[0]);files=sorted(ladder_path(season).glob('*Z.json'))
                return self.send(200,{'seasonId':season,'snapshots':[p.name for p in files]})
            return self.send(404,{'error':'接口不存在'})
        except ValueError as e: self.send(400,{'error':str(e)})
        except Exception: self.send(503,{'error':'取数服务暂时不可用，请稍后重试'})

class ReloadingTLS(ThreadingHTTPServer):
    def __init__(self,address,handler,cert,key):
        self.cert,self.key=cert,key;self.cert_mtime=0;self.tls=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.tls.minimum_version=ssl.TLSVersion.TLSv1_2
        super().__init__(address,handler)
    def get_request(self):
        sock,address=super().get_request();sock.settimeout(10)
        try:
            mt=os.stat(self.cert).st_mtime_ns
            if mt!=self.cert_mtime:self.tls.load_cert_chain(self.cert,self.key);self.cert_mtime=mt
            return self.tls.wrap_socket(sock,server_side=True),address
        except Exception:sock.close();raise

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--collect-ladder',action='store_true')
    parser.add_argument('--collect-stats',action='store_true')
    parser.add_argument('--port',type=int,default=8443);args=parser.parse_args()
    if args.collect_ladder:collect_ladder()
    elif args.collect_stats:
        result=rank_stats.collect(STORE,lambda path,params:upstream(path,params,allow_missing=True),
            start_code=int(os.environ.get('YX_STATS_START_CODE','0')),
            max_calls=int(os.environ.get('YX_STATS_MAX_CALLS','2400')),
            seconds=int(os.environ.get('YX_STATS_SECONDS','480')),
            workers=int(os.environ.get('YX_STATS_WORKERS','4')))
        print(json.dumps(result,ensure_ascii=False),flush=True)
        if result.get('status')=='error':sys.exit(1)
    else:
        cert=os.environ.get('YX_TLS_CERT','/etc/letsencrypt/live/grok-public-ip/fullchain.pem')
        key=os.environ.get('YX_TLS_KEY','/etc/letsencrypt/live/grok-public-ip/privkey.pem')
        ReloadingTLS(('0.0.0.0',args.port),Handler,cert,key).serve_forever()
