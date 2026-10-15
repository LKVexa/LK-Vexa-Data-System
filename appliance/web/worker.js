import {createRuntime} from './runtime.js';
import {BrowserStore} from './storage.js';
let runtime=null,release;
async function boot(){
 if(!navigator.locks)throw new Error('Web Locks unavailable; cannot safely coordinate this browser volume');
 let startedResolve,startedReject;const started=new Promise((a,b)=>{startedResolve=a;startedReject=b;});
 navigator.locks.request('ja21-dc-volume-v2',{ifAvailable:true},async lock=>{
  if(!lock){startedReject(new Error('Browser volume is open in another page. Close that page or use its terminal tabs.'));return;}
  try{runtime=await createRuntime(new BrowserStore());postMessage({type:'ready',...runtime.info});startedResolve();await new Promise(r=>{release=r;});}catch(e){startedReject(e);}
 }).catch(startedReject);
 await started;
}
self.onmessage=ev=>{
 if(ev.data?.type==='attach'&&ev.ports?.[0]){
  const port=ev.ports[0];try{if(!runtime)throw new Error('not ready');const conn=runtime.attach({send:m=>port.postMessage(m),close:()=>port.close()});port.onmessage=e=>conn.receive(e.data);port.onmessageerror=()=>conn.close('MESSAGE_ERROR');}catch(e){port.postMessage(JSON.stringify({v:2,type:'error',payload:{message:e.message}}));port.close();}
 }else if(ev.data?.type==='stop'){Promise.resolve(runtime?.stop()).then(()=>{release?.();postMessage({type:'stopped'});}).catch(e=>postMessage({type:'error',message:e.message}));}
};
boot().catch(e=>postMessage({type:'error',message:e.message}));
