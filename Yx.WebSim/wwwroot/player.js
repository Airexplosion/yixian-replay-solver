const $=id=>document.getElementById(id);
const initial=new URLSearchParams(location.search),playerId=initial.get('id')||'',fallbackName=(initial.get('name')||'玩家').slice(0,80);
let api='',roster={},offset=0,generation=0,nextRequestAt=0,earliest=null,currentName=fallbackName;
const limit=50,localInput=ms=>new Date(ms+8*3600000).toISOString().slice(0,16);
const inputTime=id=>Date.parse($(id).value+':00+08:00');
const date=ms=>ms?new Date(ms).toLocaleString('zh-CN',{timeZone:'Asia/Hong_Kong',hour12:false}):'暂无';
const num=(v,digits=0)=>v==null?'—':Number(v).toLocaleString('zh-CN',{minimumFractionDigits:digits,maximumFractionDigits:digits});
const points=v=>v==null?'—':`${v>0?'+':''}${num(v,1)}`;
const roleName=id=>roster[id]?.name||`角色 ${id}`;
function cell(text,className=''){const td=document.createElement('td');td.textContent=text;td.className=className;return td;}
function range(days){const end=Math.ceil(Date.now()/60000)*60000;let from=days?end-days*86400000:earliest||end-7*86400000;
 if(!days)from=Math.floor((from+8*3600000)/86400000)*86400000-8*3600000;
 $('from').value=localInput(from);$('to').value=localInput(end);}
function filters(){const from=inputTime('from'),to=inputTime('to'),minScore=Number($('min-score').value),maxScore=Number($('max-score').value);
 if(!Number.isFinite(from)||!Number.isFinite(to)||from>=to)throw Error('结束时间必须晚于开始时间。');
 if(minScore>=maxScore)throw Error('积分上限必须大于下限。');
 return {from,to,minScore,maxScore,character:$('character').value};}
async function request(values){const at=Math.max(Date.now(),nextRequestAt);nextRequestAt=at+1100;await new Promise(r=>setTimeout(r,Math.max(0,at-Date.now())));
 const url=`${api}/players/${playerId}/matches?${new URLSearchParams(values)}`;
 let response=await fetch(url,{signal:AbortSignal.timeout(30000)});
 if(response.status===429){await new Promise(r=>setTimeout(r,1100));response=await fetch(url,{signal:AbortSignal.timeout(30000)});nextRequestAt=Date.now()+1100;}
 const data=await response.json();if(!response.ok)throw Error(data.error||`获取失败（${response.status}）`);return data;}
function render(data){const s=data.summary,p=data.player;currentName=p.name||fallbackName;earliest=p.earliestEnd;
 $('player-name').textContent=currentName;document.title=`${currentName} · 逐场战绩`;
 $('coverage').textContent=`这名玩家累计已采集 ${num(p.totalCollected)} 场。结算时间：${date(p.earliestEnd)} 至 ${date(p.latestEnd)}。当前筛选为 ${num(s.matches)} 场。`;
 const metrics=[['本范围已采集',num(s.matches),'一场对局计一次'],['累计净分',points(s.net),'包含扣分和 0 分'],['场均净分',points(s.average),'净分 ÷ 已采集场数'],['每小时净分',points(s.hourly),`${num(s.minutes/60,1)} 小时对局耗时`]];
 $('summary').replaceChildren(...metrics.map(([name,value,note])=>{const box=document.createElement('article');box.className='metric';const label=document.createElement('span');label.textContent=name;const strong=document.createElement('strong');strong.textContent=value;const small=document.createElement('small');small.textContent=note;box.append(label,strong,small);return box;}));
 $('matches').replaceChildren(...data.matches.map(m=>{const tr=document.createElement('tr');
  const timeCell=cell(''),details=document.createElement('details');details.className='player-detail';const title=document.createElement('summary');title.textContent=date(m.endTs);details.append(title);
  const info=document.createElement('p');info.className='match-detail';info.textContent=`开始：${date(m.beginTs)}\n对局编号：${m.codeId}\n随机选角：${m.randomCharacter?'是':'否'}\n版本：${m.version||'未提供'}`;details.append(info);timeCell.append(details);
  const role=cell('');const roleLabel=document.createElement('span');roleLabel.className='history-role';const img=document.createElement('img');img.src=`assets/characters/${m.characterId}-portrait.webp`;img.alt='';img.loading='lazy';img.width=32;img.height=32;img.onerror=()=>{img.hidden=true;};roleLabel.append(img,document.createTextNode(roleName(m.characterId)));role.append(roleLabel);
  const replay=cell('');if(m.replayCode){const link=document.createElement('a');link.className='player-link';link.href=`./?code=${encodeURIComponent(m.replayCode)}`;link.textContent='打开盘面 ↗';replay.append(link);const code=document.createElement('small');code.className='muted replay-code';code.textContent=m.replayCode;replay.append(code);}else{replay.textContent='未保存入口';replay.className='muted';}
  tr.append(timeCell,role,cell(num(m.scoreBefore)),cell(points(m.scoreChange),m.scoreChange>0?'win':m.scoreChange<0?'loss':'muted'),cell(num(m.scoreAfter)),cell(`${num(m.minutes,1)} 分钟`),replay);return tr;}));
 $('empty').hidden=s.matches>0;$('empty').textContent=p.totalCollected?'这个筛选范围内没有已采集战绩，可以扩大时间或选择全部角色。':'目前还没有采集到这名玩家的战绩；这不表示他没有打过对局。';
 $('page-info').textContent=s.matches?`第 ${offset+1}–${offset+data.matches.length} 场 / ${num(s.matches)} 场`:'0 场';$('previous').disabled=offset===0;$('next').disabled=offset+limit>=s.matches;
}
async function load(first=false){let values;try{values=filters();}catch(e){$('history-content').hidden=true;$('message').hidden=false;$('message').className='error';$('message').textContent=e.message;return;}
 const own=++generation;$('apply').disabled=true;$('history-content').hidden=true;$('message').hidden=false;$('message').className='';$('message').textContent='正在获取玩家战绩…';
 try{const data=await request({...values,...(first&&!initial.has('from')?{from:0}:{}),offset,limit});if(own!==generation)return;render(data);
  if(first&&!initial.has('from')&&earliest){$('from').value=localInput(Math.floor((earliest+8*3600000)/86400000)*86400000-8*3600000);}
  $('history-content').hidden=false;$('message').hidden=true;history.replaceState(null,'',`?${new URLSearchParams({id:playerId,name:currentName,...(initial.has('season')?{season:initial.get('season')}:{ }),...filters()})}`);
 }catch(e){if(own!==generation)return;$('message').className='error';$('message').textContent=e instanceof TypeError?'暂时连不上战绩服务，请稍后点击“查看战绩”重试。':e.message;}
 finally{if(own===generation)$('apply').disabled=false;}}
$('filters').onsubmit=e=>{e.preventDefault();offset=0;load();};$('character').onchange=()=>{offset=0;load();};
$('all-time').onclick=()=>{range();offset=0;load();};$('last-week').onclick=()=>{range(7);offset=0;load();};
$('previous').onclick=()=>{offset=Math.max(0,offset-limit);load();};$('next').onclick=()=>{offset+=limit;load();};
$('player-name').textContent=fallbackName;range(7);
const season=initial.get('season');if(season&&/^\d{1,3}$/.test(season)){$('source').textContent=`来自第 ${season} 赛季天梯榜 · 下方为该玩家已采集的逐场战绩。`;$('back-ladder').href=`ladder.html?season=${season}`;}
try{if(!/^[a-f0-9]{20}$/.test(playerId))throw Error('请从天梯榜或角色统计页点击玩家进入。');
 const [settings,names]=await Promise.all([fetch('settings.json').then(r=>r.json()),fetch('data/characters.json').then(r=>r.json())]);api=settings.replayApi.replace(/\/$/,'');roster=names;
 $('character').append(...Object.entries(roster).map(([id,r])=>{const option=document.createElement('option');option.value=id;option.textContent=r.name;return option;}));
 for(const [param,id] of [['minScore','min-score'],['maxScore','max-score'],['character','character']])if(initial.has(param))$(id).value=initial.get(param);
 for(const id of ['from','to'])if(initial.has(id)&&/^\d{1,13}$/.test(initial.get(id))&&Number(initial.get(id))<4102444800000)$(id).value=localInput(Number(initial.get(id)));
 await load(true);
}catch(e){$('message').textContent=e.message;$('message').className='error';}
