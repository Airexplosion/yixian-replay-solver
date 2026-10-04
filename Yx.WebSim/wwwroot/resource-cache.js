const VERSION='__CACHE_VERSION__';
export async function prepareCache(progress){
 if(!('serviceWorker'in navigator)||!globalThis.caches)return {available:false,cached:false};
 const url=new URL(`sw.js?v=${VERSION}`,import.meta.url);
 try{
  const registration=await navigator.serviceWorker.register(url,{scope:'./',updateViaCache:'none'});
  const worker=registration.installing||registration.waiting||registration.active;
  if(worker.state!=='activated')await new Promise((resolve,reject)=>{
   const timeout=setTimeout(()=>reject(Error('缓存准备超时')),30000);
   worker.addEventListener('statechange',()=>{
    if(worker.state==='activated'){clearTimeout(timeout);resolve();}
    if(worker.state==='redundant'){clearTimeout(timeout);reject(Error('缓存未能启用'));}
   });
  });
  if(navigator.serviceWorker.controller?.scriptURL!==url.href)await new Promise((resolve,reject)=>{
   const timeout=setTimeout(()=>reject(Error('缓存未能接管页面')),10000);
   navigator.serviceWorker.addEventListener('controllerchange',()=>{if(navigator.serviceWorker.controller?.scriptURL===url.href){clearTimeout(timeout);resolve();}},{once:true});
  });
  return await new Promise((resolve,reject)=>{
   const channel=new MessageChannel();
   let timeout=setTimeout(()=>reject(Error('资源缓存超时')),120000);
   channel.port1.onmessage=({data})=>{
    if(data.type==='progress'){clearTimeout(timeout);timeout=setTimeout(()=>reject(Error('资源缓存超时')),120000);progress?.(data);}
    if(data.type==='ready'){clearTimeout(timeout);channel.port1.close();resolve({available:true,...data});}
    if(data.type==='error'){clearTimeout(timeout);channel.port1.close();reject(Error(data.message));}
   };
   navigator.serviceWorker.controller.postMessage({type:'PREPARE_SIM'},[channel.port2]);
  });
 }catch(error){return {available:false,cached:false,error:error.message};}
}
