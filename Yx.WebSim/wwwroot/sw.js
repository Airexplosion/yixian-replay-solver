// Only explicitly listed, public static resources enter this cache. API data stays live.
const VERSION='__CACHE_VERSION__';
const CACHE='yx-replay-static-v1:'+new URL(self.registration.scope).pathname;
const inflight=new Map();
let storageFailed=false;
const manifest=fetch(new URL(`asset-manifest.json?v=${VERSION}`,self.registration.scope),{cache:'no-store'}).then(async response=>{
 if(!response.ok)throw Error('缓存清单暂不可用');
 const value=await response.json();
 if(value.version!==VERSION)throw Error('缓存版本尚未同步，请稍后重试');
 return value;
});
const assetURL=path=>new URL(path,self.registration.scope).href;
const cacheKey=(path,hash)=>`${assetURL(path)}?yx-content=${hash}`;
async function resource(path,hash){
 const key=cacheKey(path,hash);let cache=null;
 try{cache=await caches.open(CACHE);}catch{storageFailed=true;}
 const saved=cache?await cache.match(key):null;
 if(saved)return saved;
 if(!inflight.has(key))inflight.set(key,(async()=>{
  let response=await fetch(assetURL(path),{cache:'no-cache'});
  if(!response.ok)throw Error(`资源下载失败（${response.status}）`);
  const digest=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await response.clone().arrayBuffer())),v=>v.toString(16).padStart(2,'0')).join('');
  if(digest!==hash)throw Error('资源版本尚未同步，请稍后重试');
  try{if(cache)await cache.put(key,response.clone());}catch{storageFailed=true;}
  return response;
 })().finally(()=>inflight.delete(key)));
 return (await inflight.get(key)).clone();
}
self.addEventListener('install',event=>event.waitUntil(manifest.then(()=>self.skipWaiting())));
self.addEventListener('activate',event=>event.waitUntil(self.clients.claim()));
self.addEventListener('fetch',event=>{
 const url=new URL(event.request.url),scope=new URL(self.registration.scope);
 if(event.request.method!=='GET'||url.origin!==scope.origin||!url.pathname.startsWith(scope.pathname))return;
 const path=url.pathname.slice(scope.pathname.length);
 // Restrict synchronously so pages, settings and remote game APIs never depend on the cache.
 if(!(path.startsWith('_framework/')||path.startsWith('assets/')||['sim-worker.js','data/battle_config.json','data/card_effects.json'].includes(path)))return;
 event.respondWith(manifest.then(value=>value.files[path]?resource(path,value.files[path]):fetch(event.request)));
});
self.addEventListener('message',event=>{
 if(event.data?.type!=='PREPARE_SIM'||!event.ports[0])return;
 const port=event.ports[0];
 event.waitUntil((async()=>{
  try{
   const value=await manifest,cache=await caches.open(CACHE),core=value.core;
   const present=await Promise.all(core.map(path=>cache.match(cacheKey(path,value.files[path])).then(Boolean)));
   const cached=present.every(Boolean);let done=present.filter(Boolean).length;
   port.postMessage({type:'progress',done,total:core.length,cached});
   const pending=core.filter((path,index)=>!present[index]);let index=0;
   await Promise.all(Array.from({length:Math.min(6,pending.length)},async()=>{
    while(index<pending.length){const path=pending[index++];await resource(path,value.files[path]);done++;port.postMessage({type:'progress',done,total:core.length,cached});}
   }));
   if(storageFailed)throw Error('浏览器未能保存缓存，仍可正常加载 sim');
   // Drop only this app's obsolete static content; rendered card faces have their own cache.
   const valid=new Set(Object.entries(value.files).map(([path,hash])=>cacheKey(path,hash)));
   for(const key of await cache.keys())if(!valid.has(key.url))await cache.delete(key);
   port.postMessage({type:'ready',cached,total:core.length,version:VERSION});
  }catch(error){port.postMessage({type:'error',message:error.message});}
 })());
});
