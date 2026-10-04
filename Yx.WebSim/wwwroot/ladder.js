const $=id=>document.getElementById(id);
let api='',generation=0,lastData=null,period='24';
const formatDate=value=>value?new Date(value).toLocaleString('zh-CN',{timeZone:'Asia/Hong_Kong',hour12:false}):'暂无';
const localInput=ms=>new Date(ms+8*3600000).toISOString().slice(0,16);
const parseInput=id=>Date.parse($(id).value+':00+08:00');
const statusName={both:'两端在榜',entered:'新入榜',left:'已离榜',uncompared:'缺少对比快照'};
async function request(path){let response=await fetch(api+path,{signal:AbortSignal.timeout(30000)});if(response.status===429){await new Promise(r=>setTimeout(r,1100));response=await fetch(api+path,{signal:AbortSignal.timeout(30000)});}const data=await response.json();if(!response.ok)throw Error(data.error||`获取失败（${response.status}）`);return data;}
function cell(text,className=''){const td=document.createElement('td');td.textContent=text;td.className=className;return td;}
function delta(value,rank=false){if(value==null)return cell('—');return cell(value===0?'0':rank?`${value>0?'↑':'↓'} ${Math.abs(value)}`:`${value>0?'+':''}${value}`,value>0?'win':value<0?'loss':'muted');}
function preset(value){
 period=value;
 if(value!=='latest'){
  const to=Math.ceil(Date.now()/60000)*60000;
  const from=value==='today'?Math.floor((Date.now()+8*3600000)/86400000)*86400000-8*3600000:to-Number(value)*3600000;
  $('from').value=localInput(from);$('to').value=localInput(to);
 }
 document.querySelectorAll('[data-period]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.period===value)));
}
function render(data){
 const rows=[...data.players];
 rows.sort((a,b)=>{
  if($('sort').value==='gain'){
   if((a.delta==null)!==(b.delta==null))return a.delta==null?1:-1;
   if(a.delta!=null&&a.delta!==b.delta)return b.delta-a.delta;
  }
  return (a.rank==null)-(b.rank==null)||(a.rank??a.startRank)-(b.rank??b.startRank)||a.id.localeCompare(b.id);
 });
 $('players').replaceChildren(...rows.map(p=>{
  const tr=document.createElement('tr'),player=cell(''),link=document.createElement('a');
  const params={id:p.id,name:p.name,season:data.seasonId};
  if(data.comparison){params.from=data.comparison.requestedFrom;params.to=data.comparison.requestedTo;}
  link.href=`player.html?${new URLSearchParams(params)}`;link.textContent=p.name;link.className='player-link';link.title='查看选定时间内已采集的逐场战绩';player.append(link);
  tr.append(cell(`${p.startRank??'—'} → ${p.rank??'—'}`),player,cell(p.startScore??'—'),cell(p.score??'—'),delta(p.delta),delta(p.rankDelta,true),cell(statusName[p.status]||'缺少基线','muted'));return tr;
 }));
}
function summarize(data){
 const c=data.comparison;
 if(c){
  $('available').textContent=`本赛季已保存快照：${formatDate(c.availableFrom)} 至 ${formatDate(c.availableTo)}。`;
  $('stamp').textContent=c.canCompare?`实际对比：${formatDate(data.baselineAt)} → ${formatDate(data.generatedAt)} · 区间内 ${c.snapshotCount} 份快照 · 期末 ${data.count} 名`:
   c.snapshotCount?`区间内仅 1 份快照：${formatDate(data.generatedAt)}，需至少 2 份才能计算变化。`:'所选时间内没有已保存的榜单快照，请调整时间范围。';
  const notes=[];
  if(c.startTruncated)notes.push(c.canCompare?'开始时间早于采集起点，实际对比从首份可用快照开始。':'开始时间早于已保存快照范围。');
  if(c.endTruncated)notes.push(c.canCompare?'结束时间晚于最新快照，实际对比截至最新已保存快照。':'结束时间晚于已保存快照范围。');
  if(c.canCompare)notes.unshift(`两端均在榜 ${c.matched} 人 · 新入榜 ${c.entered} 人 · 已离榜 ${c.left} 人。`);
  $('comparison-note').textContent=notes.join(' ');
 }else{
  $('stamp').textContent=`最近两次快照：${formatDate(data.baselineAt)} → ${formatDate(data.generatedAt)} · ${data.count} 名`;
  $('comparison-note').textContent=data.baselineAt?'当前显示相邻两次快照的净变化，可在上方改为任意时间段。':'首次快照，尚无变化基线。';
  $('available').textContent='选择时间段后，会显示本赛季历史快照的可用范围。';
  $('from').value=localInput(data.baselineAt?Date.parse(data.baselineAt):Date.parse(data.generatedAt)-600000);
  $('to').value=localInput(Math.ceil(Date.parse(data.generatedAt)/60000)*60000);
 }
}
async function load(){
 const own=++generation;
 let from,to;
 if(period!=='latest'){
  from=parseInput('from');to=parseInput('to');
  if(!Number.isFinite(from)||!Number.isFinite(to)||from>=to){$('ladder-panel').hidden=true;$('stamp').textContent='';$('comparison-note').textContent='';$('message').textContent='结束时间必须晚于开始时间。';$('message').hidden=false;$('refresh').disabled=false;$('apply').disabled=false;return;}
 }
 $('refresh').disabled=true;$('apply').disabled=true;$('ladder-panel').hidden=true;$('stamp').textContent='';$('comparison-note').textContent='';$('message').hidden=false;$('message').textContent='正在读取期间榜单…';
 try{
  const params=new URLSearchParams({season:$('season').value});
  if(period!=='latest'){params.set('from',from);params.set('to',to);}
  const data=await request(`/ladder?${params}`);if(own!==generation)return;
  if(!data.comparison)data.players=data.players.map(p=>({...p,startScore:p.delta==null?null:p.score-p.delta,startRank:p.rankDelta==null?null:p.rank+p.rankDelta,status:p.delta==null?'uncompared':'both'}));
  lastData=data;summarize(data);render(data);
  $('ladder-panel').hidden=!data.players.length;$('message').hidden=true;
  params.set('sort',$('sort').value);if(period==='latest')params.set('mode','latest');
  history.replaceState(null,'',`?${params}`);
 }catch(e){if(own!==generation)return;$('message').hidden=false;$('message').textContent=e instanceof TypeError?'暂时连不上榜单服务，请稍后重试。':e.message;}
 finally{if(own===generation){$('refresh').disabled=false;$('apply').disabled=false;}}
}
$('time-filter').onsubmit=e=>{e.preventDefault();period=null;document.querySelectorAll('[data-period]').forEach(b=>b.setAttribute('aria-pressed','false'));load();};
$('season').onchange=load;
$('refresh').onclick=()=>{if(period&&period!=='latest')preset(period);load();};
$('sort').onchange=()=>{if(lastData){render(lastData);const params=new URLSearchParams(location.search);params.set('sort',$('sort').value);history.replaceState(null,'',`?${params}`);}};
document.querySelectorAll('[data-period]').forEach(b=>b.onclick=()=>{preset(b.dataset.period);load();});
['from','to'].forEach(id=>$(id).oninput=()=>{period=null;document.querySelectorAll('[data-period]').forEach(b=>b.setAttribute('aria-pressed','false'));});
preset('24');
try{
 const settings=await fetch('settings.json').then(r=>r.json());api=settings.replayApi.replace(/\/$/,'');
 const data=await request('/ladder/seasons'),seasons=data.seasons.sort((a,b)=>b-a);
 if(!seasons.length)throw Error('尚无赛季快照');
 $('season').replaceChildren(...seasons.map(s=>{const o=document.createElement('option');o.value=s;o.textContent=`第 ${s} 赛季`;return o;}));
 const initial=new URLSearchParams(location.search),selected=initial.get('season');
 if(seasons.includes(Number(selected)))$('season').value=selected;
 if(initial.get('mode')==='latest')preset('latest');
 else if(initial.has('from')&&initial.has('to')&&['from','to'].every(k=>/^\d{1,13}$/.test(initial.get(k))&&Number(initial.get(k))<4102444800000)){
  ['from','to'].forEach(k=>$(k).value=localInput(Number(initial.get(k))));period=null;
  document.querySelectorAll('[data-period]').forEach(b=>b.setAttribute('aria-pressed','false'));
 }
 if(['rank','gain'].includes(initial.get('sort')))$('sort').value=initial.get('sort');
 $('season').disabled=false;await load();
}catch(e){$('message').textContent=e instanceof TypeError?'暂时连不上榜单服务，请稍后重新打开。':e.message;}
