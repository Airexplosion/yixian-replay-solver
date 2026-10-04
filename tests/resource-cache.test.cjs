const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const {createHash,webcrypto}=require('node:crypto');
const path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../Yx.WebSim/wwwroot/sw.js'),'utf8');
const hash=value=>createHash('sha256').update(value).digest('hex');
function setup({body='wasm-v1',expected=body,quota=false}={}){
 const scope='https://example.test/yixian-replay-solver/',events={},entries=new Map(),calls=[];
 const cache={
  match:async key=>entries.get(key.url||key)?.clone(),
  put:async(key,response)=>{if(quota)throw Error('quota');entries.set(key.url||key,response.clone());},
  keys:async()=>[...entries.keys()].map(url=>({url})),delete:async key=>entries.delete(key.url||key)
 };
 const manifest={version:'__CACHE_VERSION__',files:{'_framework/engine.wasm':hash(expected)},core:['_framework/engine.wasm']};
 const context=vm.createContext({URL,Response,crypto:webcrypto,console,
  self:{registration:{scope},clients:{claim:async()=>{}},skipWaiting:async()=>{},addEventListener:(name,fn)=>events[name]=fn},
  caches:{open:async()=>cache},
  fetch:async url=>{calls.push(String(url));return String(url).includes('asset-manifest.json')?Response.json(manifest):new Response(body);}
 });
 vm.runInContext(source,context);
 return {context,events,entries,calls,manifest,scope};
}
test('same sim bytes are downloaded once, including concurrent requests',async()=>{
 const env=setup(),key=env.manifest.files['_framework/engine.wasm'];
 const responses=await Promise.all(Array.from({length:6},()=>env.context.resource('_framework/engine.wasm',key)));
 for(const response of responses)assert.equal(await response.text(),'wasm-v1');
 await env.context.resource('_framework/engine.wasm',key);
 assert.equal(env.calls.filter(url=>url.endsWith('engine.wasm')).length,1);
});
test('changing content invalidates bytes and stale content is never stored',async()=>{
 const env=setup({body:'old-version',expected:'new-version'});
 await assert.rejects(env.context.resource('_framework/engine.wasm',hash('new-version')),/版本/);
 assert.equal(env.entries.size,0);
});
test('API requests, pages and settings never enter the static cache',()=>{
 const env=setup();
 for(const url of [env.scope+'api/v1/stats',env.scope+'ladder.html',env.scope+'settings.json','https://154.9.252.167:8443/api/v1/ladder']){
  let intercepted=false;
  env.events.fetch({request:{url,method:'GET'},respondWith:()=>intercepted=true});
  assert.equal(intercepted,false);
 }
});
test('cache preparation reports reuse on the next page and stores no API data',async()=>{
 const env=setup();
 async function prepare(){
  const messages=[];let finished;
  env.events.message({data:{type:'PREPARE_SIM'},ports:[{postMessage:message=>messages.push(message)}],waitUntil:promise=>finished=promise});
  await finished;
  return messages.find(message=>message.type==='ready');
 }
 assert.equal((await prepare()).cached,false);
 assert.equal((await prepare()).cached,true);
 assert.equal(env.calls.filter(url=>url.endsWith('engine.wasm')).length,1);
});
test('storage quota failures still allow normal sim downloads',async()=>{
 const env=setup({quota:true});
 const response=await env.context.resource('_framework/engine.wasm',hash('wasm-v1'));
 assert.equal(await response.text(),'wasm-v1');
 let finished;const messages=[];
 env.events.message({data:{type:'PREPARE_SIM'},ports:[{postMessage:message=>messages.push(message)}],waitUntil:promise=>finished=promise});
 await finished;
 assert.equal(messages.at(-1).type,'error');
 assert.equal(env.entries.size,0);
});
