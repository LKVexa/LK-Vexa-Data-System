/** A separate engine credential never enters the normal terminal client or DOM.
 * Worker construction is injectable for offline lifecycle tests, not an execution adapter.
 */
export function startPhotonEngine(session,{bridge=globalThis.chrome?.webview,onFailure=()=>{},WorkerClass=globalThis.Worker,startTimeoutMs=30000,stopTimeoutMs=8000}={}){
 return new Promise((resolve,reject)=>{
  let worker,ready=false,dead=false,stopped=false,stopPending=null,timer;
  const detach=()=>bridge?.removeEventListener?.('message',nativeStop);
  const fail=error=>{
   if(dead)return;dead=true;clearTimeout(timer);detach();session.engineToken=null;
   try{worker?.terminate();}catch{}
   if(stopPending){clearTimeout(stopPending.timer);stopPending.reject(error);stopPending=null;}
   if(!ready)reject(error);else onFailure(error);
  };
  const stop=()=>{
   if(dead)return Promise.reject(new Error('Photon engine is unavailable; persistence was not confirmed'));
   if(stopped)return Promise.resolve(true);
   if(stopPending)return stopPending.promise;
   let done,bad;const promise=new Promise((a,b)=>{done=a;bad=b;});
   const timeout=setTimeout(()=>{const p=stopPending;stopPending=null;p?.reject(new Error('Engine did not confirm persistence'));},stopTimeoutMs);
   stopPending={promise,resolve:done,reject:bad,timer:timeout};
   try{worker.postMessage({type:'stop'});}catch(e){fail(e);}
   return promise;
  };
  async function nativeStop(e){
   if(e.data?.type!=='ja21.engine.stop'||!/^[a-f0-9]{32}$/.test(e.data.nonce||''))return;
   let ok=false;try{ok=await stop();}catch{}
   try{bridge.postMessage({type:'ja21.engine.stopped',nonce:e.data.nonce,ok});}catch{}
  }
  try{
   worker=new WorkerClass(new URL('./photon-worker.js',import.meta.url),{type:'module'});
   timer=setTimeout(()=>fail(new Error('Photon engine did not become ready within '+startTimeoutMs+' ms; check WebView2, worker policy and broker/TLS diagnostics')),startTimeoutMs);
   worker.onmessage=e=>{
    if(dead)return;
    if(e.data?.type==='ready'){
     if(ready){fail(new Error('Duplicate engine readiness'));return;}
     clearTimeout(timer);ready=true;resolve({worker,stop});
    }else if(e.data?.type==='stopped'){
     stopped=true;detach();
     if(stopPending){clearTimeout(stopPending.timer);stopPending.resolve(true);stopPending=null;}
    }else if(e.data?.type==='error')fail(new Error(String(e.data.error||'Engine error').slice(0,500)));
    else if(e.data?.type==='stop.failed'){
     const error=new Error(String(e.data.error||'Engine save failed').slice(0,500));
     if(stopPending){clearTimeout(stopPending.timer);stopPending.reject(error);stopPending=null;}else onFailure(error);
    }
   };
   worker.onerror=e=>{e?.preventDefault?.();fail(new Error('Native engine Worker failed: '+String(e?.message||'no detailed browser diagnostic; inspect WebView2 and policy').slice(0,500)));};
   worker.onmessageerror=()=>fail(new Error('Native engine Worker message could not be decoded'));
   bridge?.addEventListener('message',nativeStop);
   worker.postMessage({type:'start',endpoint:session.endpoint.replace(/\/socket$/,'/engine'),token:session.engineToken});
   session.engineToken=null;
  }catch(e){fail(e);}
 });
}
