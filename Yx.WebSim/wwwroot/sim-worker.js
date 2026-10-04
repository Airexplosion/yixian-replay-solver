import { dotnet } from './_framework/dotnet.js';
let api;
let initializing;
self.onmessage = async ({data}) => {
  try {
    if (data.type === 'initialize') {
      if(!initializing)initializing=(async()=>{
        const runtime = await dotnet.withDiagnosticTracing(false).create();
        const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
        api = exports.Yx.WebSim.BrowserApi;
        const result = JSON.parse(api.Initialize(data.cards, data.effects));
        if (!result.ok) throw Error(result.error);
      })();
      await initializing;
      self.postMessage({type:'ready'});
    } else if (data.type === 'solve') {
      if (!api) throw Error('sim 未加载');
      self.postMessage({type:'result', report:JSON.parse(api.Solve(JSON.stringify(data.request)))});
    }
  } catch (error) { self.postMessage({type:'error', message:String(error.message || error)}); }
};
