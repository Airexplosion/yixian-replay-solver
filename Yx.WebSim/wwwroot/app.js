const $ = id => document.getElementById(id);
const state = {ready:false, busy:false, worker:null, cards:null, effects:null, config:null, rounds:[], request:null, timer:null, api:null};
const copy = value => structuredClone(value);
function message(text,error=false){ $('message').hidden=!text; $('message').textContent=text; $('message').className=error?'error':''; }
function controls(){
  for(const id of ['import','demo','open-file']) $(id).disabled=!state.ready||state.busy;
  $('solve').disabled=!state.ready||!state.request||state.busy;
  for(const id of ['round','perspective','first','duration']) $(id).disabled=state.busy;
}
function loading(title, detail, progress){ $('load-title').textContent=title; $('load-detail').textContent=detail; $('load-progress').value=progress; }
async function checkedFetch(url){ const response=await fetch(url); if(!response.ok) throw Error(`资源加载失败（HTTP ${response.status}）`); return response; }
async function startWorker(){
  state.ready=false; controls(); $('engine-pill').textContent='sim 加载中'; $('engine-pill').className='engine-pill';
  if(state.worker) state.worker.terminate();
  state.worker=new Worker(new URL('sim-worker.js',import.meta.url),{type:'module'});
  const worker=state.worker;
  return new Promise((resolve,reject)=>{
    const timeout=setTimeout(()=>{ worker.terminate(); reject(Error('sim 加载超时，请检查网络后重试。')); },120000);
    worker.onerror=event=>{
      if(worker!==state.worker)return;
      clearTimeout(timeout);const error=Error(event.message||'浏览器求解器运行失败');
      if(!state.ready)reject(error);
      else{state.ready=false;resetSearch();$('retry').hidden=false;$('engine-pill').textContent='sim 需要重载';message(error.message,true);}
    };
    worker.onmessage=({data})=>{
      if(worker!==state.worker)return;
      if(data.type==='ready'){
        clearTimeout(timeout); state.ready=true;
        $('engine-pill').textContent='sim 已就绪'; $('engine-pill').classList.add('ready');
        loading('求解器已准备好','sim 已在浏览器加载。导入复盘后即可开始求解。',100); controls(); resolve();
      } else if(data.type==='result'){
        resetSearch();
        if(!data.report.ok){ message(data.report.error||'求解失败',true); return; }
        showResults(data.report);
      } else if(data.type==='error'){
        clearTimeout(timeout);
        if(!state.ready)reject(Error(data.message));
        else {resetSearch();message(data.message,true);}
      }
    };
    worker.postMessage({type:'initialize',cards:state.cards,effects:state.effects});
  });
}
async function boot(){
  $('retry').hidden=true; message('');
  try{
    loading('正在下载卡牌规则','首次加载稍久，后续访问会使用浏览器缓存。',10);
    const [cards,effects,settings]=await Promise.all([
      checkedFetch('data/battle_config.json').then(r=>r.text()),checkedFetch('data/card_effects.json').then(r=>r.text()),
      checkedFetch('settings.json').then(r=>r.json())]);
    state.cards=cards; state.effects=effects;state.config=JSON.parse(cards);state.api=settings.replayApi;
    loading('正在加载 sim 运行环境','计算使用你的设备；无需在电脑上安装游戏或 mod。',45);
    await startWorker();
  }catch(error){ loading('加载没有完成',error.message,0); $('retry').hidden=false; $('engine-pill').textContent='加载失败'; }
}
function validateRequest(req){
  if(!req||!req.me||!req.foe||!Array.isArray(req.me.cards)||!Array.isArray(req.foe.cards))throw Error('缺少双方完整盘面；请粘贴复盘导出数据或有效的游戏复盘码。');
  if(!Number.isInteger(req.slots)||req.slots<1||req.slots>8||req.me.cards.length>24||req.foe.cards.length>8)throw Error('支持 1–8 个牌位、最多 24 张我方可用牌。');
  for(const side of [req.me,req.foe]){
    for(const id of side.cards)if(!Number.isInteger(id)||id<0||(!state.config.card[id]&&id!==0))throw Error(`当前卡表没有收录卡牌 ${id}。`);
    if(side.xianMoStrategies?.length || Object.keys(side.xianMoTempDatas||{}).length)throw Error('当前浏览器 sim 尚未移植完整仙魔策略，暂不能可靠求解这份盘面。');
  }
  const pool=[...req.me.cards];
  for(const id of req.me.initial||[]){ if(id===0)continue; const at=pool.indexOf(id); if(at<0)throw Error('初始牌桌与可用牌池不一致。');pool.splice(at,1); }
  return req;
}
function importData(data){
  const rounds=data.rounds||[{round:data.round,request:data.request||data}];
  if(!Array.isArray(rounds)||!rounds.length)throw Error('复盘没有可导入的轮次。');
  state.rounds=rounds; $('round').replaceChildren(...rounds.map((r,i)=>{const op=new Option(`第 ${r.round||r.request.round} 轮`,String(i));return op;}));
  const selected=rounds.findIndex(r=>r.round===data.selectedRound);$('round').value=String(selected<0?rounds.length-1:selected);
  $('perspective').value='0'; selectRound();
  $('match-score').hidden=!data.match;
  if(data.match){const m=data.match;$('match-score').textContent=`原复盘玩家：开局排位积分 ${m.rankBefore} · 结算 ${m.rankChange>0?'+':''}${m.rankChange} · 结算后 ${m.rankAfter} · 第 ${m.placement} 名`;}
  message(`已还原第 ${state.request.round} 轮的双方盘面。请核对仙命与先手后求解。`);
}
function swap(req){
  const out=copy(req), me=out.me,foe=out.foe;
  out.me={...foe,cards:[...foe.cards,...(foe.handCards||[])],initial:[...foe.cards]};
  out.foe={...me,cards:[...(me.initial||me.cards.slice(0,req.slots))]};
  out.slots=foe.unlockGrids||foe.cards.length||req.slots;
  out.first=req.first==='me'?'foe':req.first==='foe'?'me':'both'; return out;
}
function selectRound(){
  const round=state.rounds[Number($('round').value)]; if(!round)return;
  let req=copy(round.request||round); if($('perspective').value==='1')req=swap(req);
  state.request=validateRequest(req); $('first').value=req.first||'both';$('results').hidden=true; renderBoard();controls();
}
const cardName=id=>state.config.card[id]?.n||(id===0?'普通攻击':`卡牌 ${id}`);
function card(id,index){
  const el=document.createElement('div');el.className='card';const fig=document.createElement('figure');
  if(index!=null){const slot=document.createElement('span');slot.className='slot-index';slot.textContent=index+1;el.append(slot);}
  if(id===0){const empty=document.createElement('div');empty.className='card-empty';empty.textContent='普通攻击';fig.append(empty);}
  else {const img=document.createElement('img');img.alt=cardName(id);img.loading='lazy';img.dataset.cardKey=`${id}_zh`;img.src=window.YxpCards.source(`${id}_zh`);fig.append(img);}
  const caption=document.createElement('figcaption');caption.textContent=cardName(id);fig.append(caption);el.title=`${cardName(id)}\nID: ${id}\n${(state.config.card[id]?.ds||'').replace(/<[^>]*>/g,'')}`;el.append(fig);return el;
}
function drawCards(id, cards, slots){ const list=cards.slice(); if(slots)while(list.length<slots)list.push(0); $(id).replaceChildren(...list.map((c,i)=>card(c,slots?i:null))); }
function stats(id,side){
  const levels=['未定','炼气','筑基','金丹','元婴','化神','返虚'];
  const total=(side.hp||0)+(state.config.level[side.level]||0)+(side.permanentBuffs?.[10023]||0);
  $(id).replaceChildren(...[['血量',total],['命元',side.life??10],['境界',levels[side.level]||side.level],['体魄',side.permanentBuffs?.[10023]||0]].map(([k,v])=>{
    const el=document.createElement('span');el.textContent=k;const b=document.createElement('b');b.textContent=v;el.append(b);return el;}));
}
function details(id,side){
  const fields=[['仙命',side.talents],['天衍仙命',side.fate],['刻印',side.keyin],['共鸣',[side.resonance||0]],['永久状态',Object.entries(side.permanentBuffs||{}).map(([k,v])=>`${k} × ${v}`)],['仙命计数',Object.entries(side.talentTempDatas||{}).map(([k,v])=>`${k} × ${v}`)]];
  $(id).replaceChildren(...fields.map(([label,values])=>{const box=document.createElement('div');box.textContent=label+'：';const chips=document.createElement('div');chips.className='chips';for(const value of values?.length?values:['无']){const span=document.createElement('span');span.className='chip';span.textContent=value;chips.append(span);}box.append(chips);return box;}));
}
function renderBoard(){
  const req=state.request;$('workspace').hidden=false;$('empty').hidden=true;$('match-title').textContent=`第 ${req.round} 轮 · ${req.slots} 个牌位`;
  const initial=req.me.initial?.length?req.me.initial:req.me.cards.slice(0,req.slots);const hand=[...req.me.cards];for(const id of initial){const i=hand.indexOf(id);if(i>=0)hand.splice(i,1);}
  drawCards('me-board',initial,req.slots);drawCards('foe-board',req.foe.cards,req.foe.unlockGrids||req.foe.cards.length||8);drawCards('hand',hand);
  stats('me-stats',req.me);stats('foe-stats',req.foe);details('me-details',req.me);details('foe-details',req.foe);
  $('me-name').textContent=`角色 ${req.me.characterId}`;$('foe-name').textContent=`角色 ${req.foe.characterId}`;
}
async function importInput(){
  try{
    const text=$('replay-input').value.trim(); if(!text)throw Error('先粘贴一串复盘代码。');
    if(text.startsWith('{')){importData(JSON.parse(text));return;}
    if(!/^[a-z0-9]{5,14}$/i.test(text))throw Error('复盘代码应为字母和数字，或粘贴完整 JSON。');
    if(!state.api)throw Error('复盘 API 尚未配置，请先导入完整盘面数据。');
    state.busy=true; controls();message('正在获取复盘各轮盘面…');
    const response=await fetch(`${state.api}/replays/${encodeURIComponent(text)}`,{signal:AbortSignal.timeout(60000)});
    const data=await response.json();if(!response.ok)throw Error(data.error||`复盘获取失败（${response.status}）`);
    importData(data);
  }catch(error){message(error instanceof TypeError?'暂时连不上复盘取数服务，请稍后重试。你的复盘代码还没有被判定为无效。':error.message,true);}
  finally{state.busy=false;controls();}
}
function showResults(report){
  $('results').hidden=false;
  const terminal=report.exhaustive?'已穷举完成':'限时搜索结果';
  $('search-status').textContent=`${terminal} · ${report.evaluated.toLocaleString()} 场 · ${(report.elapsedMs/1000).toFixed(1)} 秒`;
  $('result-list').replaceChildren(...report.best.map((entry,i)=>{
    const box=document.createElement('article');box.className='result';
    const head=document.createElement('div');head.className='result-header';const title=document.createElement('strong');title.textContent=`方案 ${String(i+1).padStart(2,'0')}`;head.append(title);
    const outcomes=document.createElement('div');outcomes.className='outcome';for(const o of entry.orders){const el=document.createElement('span');el.className=o.result==='win'?'win':'loss';const result=o.result==='win'?'胜':o.result==='loss'?'负':o.result;el.textContent=`${o.first==='me'?'我方先手':'对手先手'}：${result}  ${o.myHp} / ${o.foeHp}${o.hadRandom?` · 随机抽样 ${o.randomWins}/${o.randomRuns}`:''}`;outcomes.append(el);}head.append(outcomes);box.append(head);
    const cards=document.createElement('div');cards.className='cards board';cards.append(...entry.cards.map((id,j)=>card(id,j)));box.append(cards);
    const actions=document.createElement('div');actions.className='result-actions';const hint=document.createElement('span');hint.textContent='剩余血量：我方 / 对手';const apply=document.createElement('button');apply.textContent='应用这套摆法';apply.onclick=()=>{state.request.me.initial=[...entry.cards];renderBoard();message(`已应用方案 ${i+1}，可以继续求解。`);};actions.append(hint,apply);box.append(actions);return box;
  }));
  message(report.exhaustive?'搜索已完成，推荐结果如下。':'已得到当前较好的摆法；限时结果不保证全局最优。');
}
function solve(){
  try{
    const request=copy(validateRequest(state.request));request.first=$('first').value;request.timeLimitMs=Number($('duration').value);request.threads=1;request.topN=5;request.exhaustiveLimit=100000;request.stallMs=0;request.samples=3;request.deduplicateResults=true;
    state.busy=true;controls();$('cancel').hidden=false;$('solve').textContent='正在求解…';$('results').hidden=true;message('正在试算牌序；可以继续查看盘面，或停止本次搜索。');
    const began=performance.now();state.timer=setInterval(()=>$('solve-note').textContent=`已计算 ${((performance.now()-began)/1000).toFixed(1)} 秒 · 求解在独立后台线程中运行`,200);
    state.worker.postMessage({type:'solve',request});
  }catch(error){message(error.message,true);}
}
function resetSearch(){state.busy=false;clearInterval(state.timer);$('cancel').hidden=true;$('solve').textContent='开始求解 ✦';$('solve-note').textContent='按胜负与剩余血量排序；限时搜索不保证全局最优。复杂单场试算可能超过搜索预算。';controls();}
$('import').onclick=importInput;$('solve').onclick=solve;$('retry').onclick=boot;
$('round').onchange=$('perspective').onchange=()=>{try{selectRound();}catch(e){state.request=null;controls();message(e.message,true);}};
$('demo').onclick=async()=>{try{importData(await checkedFetch('data/demo.json').then(r=>r.json()));}catch(e){message(e.message,true);}};
$('open-file').onclick=()=>$('file').click();$('file').onchange=async()=>{try{const f=$('file').files[0];if(!f)return;if(f.size>5_000_000)throw Error('文件超过 5 MB。');$('replay-input').value=await f.text();await importInput();}catch(e){message(e.message,true);}finally{$('file').value='';}};
$('cancel').onclick=async()=>{state.worker.terminate();state.ready=false;resetSearch();message('已停止求解，正在重新准备 sim。');try{await startWorker();message('已停止求解，可以重新开始。');}catch(e){message(e.message,true);$('retry').hidden=false;}};
boot();
