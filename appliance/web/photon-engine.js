/** Owner-only engine adapter. The compiled native broker transports bytes and stores snapshots;
 * the unchanged SPIRAL execution model runs in this WebView2 JavaScript context.
 */
import {createRuntime} from './runtime.js';
import {prepareCrypto} from './photon-crypto.js';
export async function createPhotonEngine({rpc,send,onState=()=>{}}){
 prepareCrypto();
 const peers=new Map();let closed=false,jobBusy=false;
 const store={kind:'Photon/native atomic snapshot store',read:()=>rpc('store.read',{}),write:record=>rpc('store.write',{record})};
 const runtime=await createRuntime(store,{transport:'Photon broker + native WebView2 Worker',onLifecycle:reason=>send({type:'engine.halted',reason}),readVcmd:(path,parent)=>rpc('local.read',{path,...(parent?{parent}:{})}),photon:action=>rpc('photon',{action})});
 const b64=bytes=>{let text='';for(let i=0;i<bytes.length;i+=8192)text+=String.fromCharCode(...bytes.subarray(i,i+8192));return btoa(text);};
 const stop=async()=>{if(!closed)await runtime.stop();closed=true;await send({type:'engine.stopped',ok:true});onState('stopped');};
 const receive=async m=>{
  if(!m||typeof m!=='object')throw new Error('Invalid engine event');
  if(m.type==='engine.stop'){await stop();return;}
  if(closed)return;
  if(m.type==='peer.open'){
   if(typeof m.id!=='string'||!/^\w{24}$/.test(m.id)||peers.has(m.id))throw new Error('Peer identity');
   const io={send:data=>{const binary=typeof data!=='string';send({type:'peer.send',id:m.id,binary,data:binary?b64(new Uint8Array(data)):data});},close:()=>{peers.delete(m.id);send({type:'peer.close',id:m.id});}};
   peers.set(m.id,runtime.attach(io));
  }else if(m.type==='peer.data'){
   const peer=peers.get(m.id);if(!peer)return;
   if(typeof m.data!=='string'||m.data.length>24000||typeof m.binary!=='boolean')throw new Error('Peer input shape');
   peer.receive(m.binary?Uint8Array.from(atob(m.data),c=>c.charCodeAt(0)):m.data);
  }else if(m.type==='peer.close'){
   const p=peers.get(m.id);peers.delete(m.id);p?.close('network-close');
  }else if(m.type==='job'){
   if(jobBusy)throw new Error('A standalone job is already active');jobBusy=true;
   const j=m.job;if(!j||typeof j.path!=='string'||!['check','run'].includes(j.mode))throw new Error('Job shape');
   const opened=runtime.kernel.openSession({deferStart:true}),session=runtime.kernel.sessions.get(opened.sessionId),controller=new AbortController();
   let code=1,error='';
   try{code=await runtime.kernel.executeVcmd(session,j.mode,j.path,{local:true,trace:!!j.trace,controller,stdout:{write:data=>send({type:'job.output',stream:'stdout',data})},stderr:{write:data=>send({type:'job.output',stream:'stderr',data})}});await runtime.flush();}
   catch(e){error=String(e.message||e);code=74;}
   finally{runtime.kernel.closeSession(session.id);}
   await send({type:'job.done',report:{schema:'JA21-PHOTON-VCMD/1',code,error,path:j.path,mode:j.mode,isolated:true,hostExecution:false}});
  }else if(m.type!=='engine.pong')throw new Error('Unknown engine event');
 };
 await send({type:'engine.ready'});onState('ready');return {runtime,receive,stop};
}
