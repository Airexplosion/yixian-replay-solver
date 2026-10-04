const $ = id => document.getElementById(id);
let api = '', roster = {}, lastData = null, offset = 0, generation = 0, nextRequestAt = 0;
const pageSize = 50;
const roleName = id => roster[id]?.name || `角色 ${id}`;
const dateText = ms => ms ? new Date(ms).toLocaleString('zh-CN', {timeZone:'Asia/Hong_Kong', hour12:false}) : '暂无';
const localInput = ms => new Date(ms + 8 * 3600000).toISOString().slice(0,16);
const parseInput = id => Date.parse($(`${id}`).value + ':00+08:00');
const number = (n, digits=0) => n == null ? '—' : Number(n).toLocaleString('zh-CN', {maximumFractionDigits:digits, minimumFractionDigits:digits});
const points = n => n == null ? '—' : `${n > 0 ? '+' : ''}${number(n, 1)}`;
const tone = n => n > 0 ? 'win' : n < 0 ? 'loss' : 'muted';
function cell(text, className='') {const td=document.createElement('td'); td.textContent=text; td.className=className; return td;}
function pointCell(n) {return cell(points(n), tone(n));}
function roleImage(id) {const img=document.createElement('img'); img.src=`assets/characters/${id}-portrait.webp`; img.alt=''; img.loading='lazy'; img.width=48; img.height=48; img.onerror=()=>{img.hidden=true;}; return img;}
function preset(days) {
  const end = Math.ceil(Date.now()/60000)*60000;
  const start = days==='today' ? Math.floor((Date.now()+8*3600000)/86400000)*86400000-8*3600000 : end-Number(days)*86400000;
  $('from').value=localInput(start); $('to').value=localInput(end);
  document.querySelectorAll('[data-days]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.days===String(days))));
}
function values() {
  const from=parseInput('from'), to=parseInput('to'), minScore=Number($('min-score').value), maxScore=Number($('max-score').value);
  if (!Number.isFinite(from)||!Number.isFinite(to)||from>=to) throw Error('结束时间必须晚于开始时间。');
  if (minScore>=maxScore) throw Error('积分上限必须大于下限。');
  return {from,to,minScore,maxScore,minGames:$('min-games').value,character:$('character').value,q:$('player-search').value.trim(),sort:$('player-sort').value};
}
async function request(params) {
  const callAt=Math.max(Date.now(),nextRequestAt);nextRequestAt=callAt+1100;
  await new Promise(resolve=>setTimeout(resolve,Math.max(0,callAt-Date.now())));
  const response=await fetch(`${api}/stats?${params}`, {signal:AbortSignal.timeout(30000)});
  const data=await response.json();
  if (!response.ok) throw Error(data.error||`读取失败（${response.status}）`);
  return data;
}
function summary(data) {
  const s=data.summary;
  const items=[['已采集玩家战绩',number(s.matches),`${number(s.games)} 场对局 · ${number(s.players)} 名玩家`],
    ['累计净分',points(s.net),'包含扣分和 0 分'],['场均净分',points(s.average),'净分 ÷ 玩家战绩数'],
    ['每小时净分',points(s.hourly),`${number(s.minutes/60,1)} 小时对局耗时`]];
  $('summary').replaceChildren(...items.map(([label,value,note])=>{
    const card=document.createElement('article');card.className='metric';
    const title=document.createElement('span');title.textContent=label;
    const strong=document.createElement('strong');strong.textContent=value;
    const small=document.createElement('small');small.textContent=note;card.append(title,strong,small);return card;
  }));
  const c=data.coverage, run=c.lastRun;
  $('coverage').textContent=`累计保存 ${number(c.playerMatches)} 条玩家战绩 / ${number(c.games)} 场对局。样本结算时间：${dateText(c.earliestEnd)} 至 ${dateText(c.latestEnd)}（香港时间）。`;
  $('run-state').textContent=!run?'等待首次采集':run.status==='running'?'正在采集':run.status==='error'?'本轮取数失败，等待重试':`上次采集 ${dateText(run.finishedAt)}`;
  const states=c.states||{};
  $('audit-progress').textContent=`真人已齐 ${number(c.verifiedGames)} 场 · 名单待核验 ${number(c.rosterUnknownGames)} 场 · 已知真人战绩待补 ${number(c.missingPlayerMatches)} 条`;
  $('throughput').textContent=run?`${run.workers||1} 路并发 · 本轮 ${number(run.calls)} 次请求 · ${number(run.requestsPerSecond||0,2)} 次/秒 · 新增 ${number(run.added)} 条战绩`:'等待采集';
  $('scan-detail').textContent=`${c.scope} 编号范围 ${c.scanStartCode||'—'}–${c.scanThroughCode||'—'}：尚未检查 ${number(c.unscannedCodes)} 个，已检查但暂不可取 ${number(c.unavailableCodes)} 个，处理中 ${number(c.pendingCodes)} 个。历史补查剩余 ${number(c.backfillRemaining)} 个编号（含其他模式）。${c.nextCode?`新增扫描位置 ${c.nextCode}。`:''}最近扫描到的对局开始于 ${dateText(c.latestObservedBegin)}。${run?.error||''}`;
}
function roles(data) {
  const metric=$('role-sort').value;
  const sorted=[...data.characters].sort((a,b)=>{
    if (['average','hourly'].includes(metric) && a.eligible!==b.eligible) return a.eligible?-1:1;
    return b[metric]-a[metric] || b.matches-a.matches || a.id-b.id;
  });
  $('roles').replaceChildren(...sorted.map(r=>{
    const tr=document.createElement('tr');if(String(r.id)===$('character').value)tr.className='selected-role';
    const identity=cell('');const button=document.createElement('button');button.type='button';button.className='role-button';button.setAttribute('aria-label',`查看${roleName(r.id)}的玩家`);
    const name=document.createElement('span');name.textContent=roleName(r.id);button.append(roleImage(r.id),name);button.onclick=()=>{$('character').value=String(r.id);offset=0;load();};identity.append(button);
    const usage=cell('');const text=document.createElement('span');text.textContent=`${number(r.matches)} 场 · ${number(r.share*100,1)}%`;
    const bar=document.createElement('div');bar.className='usage-track';const fill=document.createElement('i');fill.style.width=`${r.share*100}%`;bar.append(fill);usage.append(text,bar);
    const badge=cell(r.eligible?'可比较':'样本不足',r.eligible?'muted':'small-sample');
    tr.append(identity,usage,pointCell(r.net),pointCell(r.average),pointCell(r.hourly),badge);return tr;
  }));
  $('role-empty').hidden=sorted.length>0;
}
function roleDetails(p,filters) {
  const details=document.createElement('details');details.className='player-detail';
  const title=document.createElement('summary');title.textContent=p.name;details.append(title);
  const list=document.createElement('ul');
  p.characters.forEach(r=>{const li=document.createElement('li');li.textContent=`${roleName(r.id)}：${r.matches} 场（${number(r.matches/p.matches*100,1)}%） · 场均 ${points(r.average)} · 每小时 ${points(r.hourly)}`;list.append(li);});
  const link=document.createElement('a');link.className='player-link';link.textContent='查看逐场战绩 ↗';link.href=`player.html?${new URLSearchParams({id:p.id,name:p.name,...filters})}`;
  details.append(list,link);return details;
}
function players(data) {
  const char=Number($('character').value);$('player-title').textContent=char?`${roleName(char)} · 玩家战绩`:'玩家战绩 · 全部角色';
  $('players').replaceChildren(...data.players.map(p=>{
    const tr=document.createElement('tr'), identity=cell('');identity.append(roleDetails(p,data.filters));
    const count=cell(number(p.matches));if(!p.eligible){const badge=document.createElement('small');badge.className='small-sample';badge.textContent='样本不足';count.append(document.createElement('br'),badge);}
    const most=p.characters[0];tr.append(identity,count,cell(most?`${roleName(most.id)} · ${most.matches} 场`:'—'),pointCell(p.net),pointCell(p.average),pointCell(p.hourly));return tr;
  }));
  $('player-empty').hidden=data.players.length>0;
  $('page-info').textContent=data.playerCount?`第 ${data.offset+1}–${data.offset+data.players.length} 名 / ${number(data.playerCount)} 名玩家`:'0 名玩家';
  $('previous').disabled=data.offset===0;$('next').disabled=data.offset+data.limit>=data.playerCount;
}
async function load() {
  let filters;try{filters=values();}catch(e){$('stats-content').hidden=true;$('message').textContent=e.message;$('message').hidden=false;$('message').className='error';return;}
  const own=++generation;$('message').textContent='正在读取统计…';$('message').hidden=false;$('message').className='';$('stats-content').hidden=true;
  $('apply').disabled=true;$('player-apply').disabled=true;
  try {
    const data=await request(new URLSearchParams({...filters,offset,limit:pageSize}));if(own!==generation)return;
    lastData=data;summary(data);roles(data);players(data);$('stats-content').hidden=false;$('message').hidden=true;
    history.replaceState(null,'',`?${new URLSearchParams(filters)}`);
  } catch(e) {
    if(own!==generation)return;
    $('message').className='error';$('message').textContent=e instanceof TypeError?'暂时连不上统计服务，请稍后点击“查看统计”重试。':e.message;
  } finally {if(own===generation){$('apply').disabled=false;$('player-apply').disabled=false;}}
}
$('filters').onsubmit=e=>{e.preventDefault();offset=0;load();};
$('player-filter').onsubmit=e=>{e.preventDefault();offset=0;load();};
$('character').onchange=()=>{offset=0;load();};
$('role-sort').onchange=()=>{if(lastData)roles(lastData);};
$('previous').onclick=()=>{offset=Math.max(0,offset-pageSize);load();};
$('next').onclick=()=>{offset+=pageSize;load();};
document.querySelectorAll('[data-days]').forEach(button=>button.onclick=()=>{preset(button.dataset.days);offset=0;load();});
['from','to'].forEach(id=>$(id).oninput=()=>document.querySelectorAll('[data-days]').forEach(b=>b.setAttribute('aria-pressed','false')));
preset('7');
try {
  const [settings,names]=await Promise.all([fetch('settings.json').then(r=>r.json()),fetch('data/characters.json').then(r=>r.json())]);
  api=settings.replayApi.replace(/\/$/,'');roster=names;
  $('character').append(...Object.entries(roster).map(([id,c])=>{const option=document.createElement('option');option.value=id;option.textContent=c.name;return option;}));
  const initial=new URLSearchParams(location.search);
  for(const [param,id] of [['minScore','min-score'],['maxScore','max-score'],['minGames','min-games'],['character','character'],['sort','player-sort'],['q','player-search']]) if(initial.has(param))$(id).value=initial.get(param);
  for(const id of ['from','to'])if(initial.has(id)&&/^\d{1,13}$/.test(initial.get(id))&&Number(initial.get(id))<4102444800000)$(id).value=localInput(Number(initial.get(id)));
  if(initial.size)document.querySelectorAll('[data-days]').forEach(b=>b.setAttribute('aria-pressed','false'));
  await load();
} catch(e){$('message').textContent='统计页加载失败，请刷新后重试。';$('message').className='error';}
