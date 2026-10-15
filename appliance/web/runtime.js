import {BUNDLED_VCMD} from './vcmd/bundled.js';
import {createVcmdImporter} from './vcmd/import.js';
import { SpiralKernel } from './vendor/spiral/kernel.js';
import { NODES } from './vendor/spiral/commands/ram.js';
import { VERSION } from './version.js';
import { describeRegistry } from './catalog.js';
import { PROTOCOL,KIND_INPUT,KIND_OUTPUT,encodeData,decodeData } from './vendor/vws/frame.js';
export const LIMITS=Object.freeze({sessions:8,inputBytes:8192,controlBytes:16384,queueBytes:262144,windowBytes:262144,messagesPerSecond:250,bytesPerSecond:65536});
const enc=new TextEncoder();
export async function digestTree(snap){
 // Canonical content baseline excludes volatile timestamps; sorted paths make ordering irrelevant.
 const norm=o=>o.k==='f'?{k:o.k,m:o.m,e:o.e,c:o.c}:{k:o.k,m:o.m,ch:[...o.ch].sort((a,b)=>a[0]<b[0]?-1:a[0]>b[0]?1:0).map(([n,c])=>[n,norm(c)])};
 const d=await crypto.subtle.digest('SHA-256',enc.encode(JSON.stringify(norm(snap.root))));return [...new Uint8Array(d)].map(b=>b.toString(16).padStart(2,'0')).join('');
}
export function normalizeSurface(raw){const s=String(raw||'about:blank').trim();if(['about:blank','ram://status'].includes(s))return s;throw new Error('Only about:blank and ram://status are admitted; no external navigation');}
export async function createRuntime(store,{transport='MessagePort',audit=()=>{},readVcmd=null,photon=null,onLifecycle=()=>{}}={}){
 let dirty=0,saved=0,pending=null,stopped=false;const epoch=crypto.randomUUID(),started=Date.now(),connections=new Set();
 const disk={device:'volume0',model:'Virtual filesystem',fs:store.kind,mount:'/',synced:false,restored:false,writtenAt:0,error:''};
 let browser={visible:false,url:'about:blank',engine:'Informational surface (not a browser engine)'};
 const broadcast=m=>{for(const c of connections)c.send(m);};
 const host={readVcmd,photon,capabilities:()=>({execution:'SPIRAL virtual commands',hostShell:false,physicalInfrastructure:false,headlessEngine:false,engineLocation:photon?'native JA21 WebView2 Worker':'browser Worker',photonBinding:!!photon,photonSnapshotIsVolumeBackup:false}),version:()=>VERSION,profile:()=>transport==='MessagePort'?'BROWSER_VIRTUAL':'WEBSOCKET_VIRTUAL',disk:()=>({...disk}),
  fabricRoster:()=>NODES.map(n=>({...n,present:kernel.vfs.exists('/df/nodes/'+n.node),built:false})),
  linkInfo:()=>({protocol:PROTOCOL,profile:host.profile(),epoch,transport}),
  openBrowser(url){browser={...browser,visible:true,url:normalizeSurface(url)};broadcast({v:2,type:'browser',payload:browser});return browser;},
  navigateBrowser(url){return host.openBrowser(url);},closeBrowser(){browser.visible=false;broadcast({v:2,type:'browser',payload:browser});},browserState:()=>({...browser}),
  image:async()=>({seal,digest:await digestTree(kernel.vfs.exportSnapshot()),epoch,profile:host.profile()}),sync:flush,afterCommand:flush,
  async poweroff(){await flush();stopped=true;onLifecycle('poweroff');broadcast({v:2,type:'runtime.halt',payload:{reason:'poweroff'}});for(const c of [...connections])c.close('poweroff');},
  async reboot(){await flush();broadcast({v:2,type:'runtime.halt',payload:{reason:'reboot'}});for(const c of [...connections])c.close('reboot');},
 };
 const kernel=new SpiralKernel({hostBridge:host,vfsLimits:{maxBytes:2097152,maxNodes:1500},limits:{historyEntries:200,captureBytes:131072,seqItems:20000},baseEnv:{USER:'operator',HOST:'virtual0',PROFILE:host.profile(),CONSOLE:'JA21/'+VERSION}});
 kernel.vfs.writeFile('/etc/os-release','NAME="SPIRAL virtual environment"\nVERSION="'+VERSION+'"\n');
 kernel.vfs.writeFile('/etc/console.json',JSON.stringify({version:VERSION,hostExecution:false,admittedSurfaces:['about:blank','ram://status']},null,2)+'\n');
 for(const n of NODES){kernel.vfs.mkdir('/df/nodes/'+n.node,{recursive:true});kernel.vfs.writeFile('/df/nodes/'+n.node+'/image.txt',n.role+'\nMetadata only; not an executing VM.\n');}
 for(const [p,text] of Object.entries(BUNDLED_VCMD)){kernel.vfs.mkdir(p.slice(0,p.lastIndexOf('/')),{recursive:true});kernel.vfs.writeFile(p,text);}
 const seal=await digestTree(kernel.vfs.exportSnapshot());
 // A read/parse/quota error fails startup. Never overwrite a corrupt volume with a factory seed.
 const record=await store.read();
 if(record){if(record.schema!=='JA21-VOLUME/1'||!record.snap)throw new Error('Unsupported saved volume; original file left unchanged');kernel.vfs.importSnapshot(record.snap);disk.restored=true;disk.writtenAt=record.writtenAt||0;disk.synced=true;}
 kernel.vfs.onMutate=()=>{dirty++;disk.synced=false;};
 if(record){
  const priorFactoryVersions=new Set(['2.1.0','2.2.0','2.2.1','2.3.0','2.3.1','2.4.0','2.4.1']);
  if(kernel.vfs.exists('/etc/os-release')){
   const old=kernel.vfs.readFile('/etc/os-release');
   const match=/^NAME="SPIRAL virtual environment"\nVERSION="([0-9]+\.[0-9]+\.[0-9]+)"\n$/.exec(old);
   if(match && priorFactoryVersions.has(match[1])){
    const text='NAME="SPIRAL virtual environment"\nVERSION="'+VERSION+'"\n';
    if(old!==text)kernel.vfs.writeFile('/etc/os-release',text);
   }
  }
  if(kernel.vfs.exists('/etc/console.json'))try{
   const old=JSON.parse(kernel.vfs.readFile('/etc/console.json'));
   if(Object.keys(old).sort().join(',')==='admittedSurfaces,hostExecution,version' && old.hostExecution===false &&
     priorFactoryVersions.has(old.version) && JSON.stringify(old.admittedSurfaces)==='["about:blank","ram://status"]' && old.version!==VERSION){
    kernel.vfs.writeFile('/etc/console.json',JSON.stringify({...old,version:VERSION},null,2)+'\n');
   }
  }catch{/* User metadata is not automatically repaired or replaced. */}
 }
 for(const [p,text] of Object.entries(BUNDLED_VCMD)){if(!kernel.vfs.exists(p)){kernel.vfs.mkdir(p.slice(0,p.lastIndexOf('/')),{recursive:true});kernel.vfs.writeFile(p,text);}}
 if(!record)dirty++;
 await flush();
 async function flush(){
  if(pending)return pending;
  if(saved===dirty)return;
  pending=Promise.resolve().then(async()=>{try{while(saved!==dirty){const at=dirty,snap=kernel.vfs.exportSnapshot(),writtenAt=Date.now();await store.write({schema:'JA21-VOLUME/1',version:VERSION,writtenAt,snap});saved=at;disk.writtenAt=writtenAt;}disk.synced=true;disk.error='';}catch(e){disk.synced=false;disk.error=String(e.message||e);throw e;}});
  try{await pending;}finally{pending=null;}
 }
 kernel.on('data',e=>{for(const c of connections)if(c.sid===e.sessionId)c.output(e.chunk);});
 kernel.on('exit',e=>{for(const c of connections)if(c.sid===e.sessionId)c.close(e.reason,e.code);});
 kernel.on('command-complete',e=>{for(const c of connections)if(c.sid===e.sessionId){c.send({v:2,type:'command.complete',sid:c.sid,payload:{code:e.code,executionCode:e.executionCode,persistence:e.persistence}});audit({event:'command-complete',exitCode:e.code,executionCode:e.executionCode,persistence:e.persistence});}});
 function attach(io){
  if(stopped)throw new Error('Virtual runtime is powered off; restart the server or Worker');
  if(connections.size>=LIMITS.sessions)throw new Error('connection limit');
  let unacked=null,largeTimer=null;const importer=createVcmdImporter(kernel.vfs);
  let closed=false,seqIn=0,seqOut=0,credit=0,sent=0,queued=0,queue=[],window=Date.now(),messages=0,bytes=0,busy=false;
  const decoder=new TextDecoder('utf-8',{fatal:true});
  const c={sid:null,
   send(obj){if(closed)return;const text=JSON.stringify(obj);if(enc.encode(text).length>32768){if(unacked){c.close('CONTROL_BACKPRESSURE',1);return;}if(!obj.rid){c.close('CONTROL_ID_REQUIRED',1);return;}unacked=obj.rid;largeTimer=setTimeout(()=>c.close('CONTROL_ACK_TIMEOUT',1),10000);}io.send(text);},
   close(reason='closed',code=0){if(closed)return;c.send({v:2,type:'session.exit',sid:c.sid,payload:{reason,code}});closed=true;importer.drop();clearTimeout(openTimer);clearTimeout(largeTimer);clearTimeout(expiry);clearInterval(idleTimer);queue=[];queued=0;connections.delete(c);if(c.sid)kernel.closeSession(c.sid,reason);io.close?.();audit({event:'session-close',reason});},
   output(text){if(closed)return;const data=enc.encode(text);if(queued+data.length>LIMITS.queueBytes){c.close('OUTPUT_LIMIT',1);return;}for(let i=0;i<data.length;i+=16384){const b=data.slice(i,i+16384);queue.push(b);queued+=b.length;}pump();},
   receive(raw){if(closed)return;
    try{
     const size=typeof raw==='string'?enc.encode(raw).length:raw.byteLength;
     if(Date.now()-window>=1000){window=Date.now();messages=0;bytes=0;}messages++;bytes+=size;
     if(messages>LIMITS.messagesPerSecond||bytes>LIMITS.bytesPerSecond)throw new Error('RATE_LIMIT');
     touched=Date.now();
     if(typeof raw!=='string'){
      const f=decodeData(raw);if(f.kind!==KIND_INPUT||f.payload.length>LIMITS.inputBytes||!c.sid||f.seq!==seqIn+1)throw new Error('BAD_INPUT_FRAME');
      seqIn=f.seq;const text=decoder.decode(f.payload,{stream:true});kernel.write(c.sid,text);return;
     }
     if(size>LIMITS.controlBytes)throw new Error('CONTROL_LIMIT');
     const m=JSON.parse(raw);if(!m||typeof m!=='object'||Array.isArray(m)||m.v!==2||typeof m.type!=='string')throw new Error('BAD_MESSAGE');
     if(m.rid!==undefined&&(typeof m.rid!=='string'||m.rid.length>80))throw new Error('BAD_REQUEST_ID');
     if(m.payload!==undefined&&(!m.payload||typeof m.payload!=='object'||Array.isArray(m.payload)))throw new Error('BAD_PAYLOAD');
     if(m.type!=='session.open'&&m.type!=='heartbeat.ping'&&(!c.sid||m.sid!==c.sid))throw new Error('SESSION_MISMATCH');
     const p=m.payload||{};
     switch(m.type){
      case 'session.open':{
       if(c.sid)throw new Error('ALREADY_OPEN');if(!Number.isSafeInteger(p.creditBytes)||p.creditBytes<1||p.creditBytes>LIMITS.windowBytes)throw new Error('BAD_CREDIT');
       const o=kernel.openSession({cols:p.cols,rows:p.rows,deferStart:true});c.sid=o.sessionId;credit=p.creditBytes;clearTimeout(openTimer);c.send({v:2,type:'session.opened',rid:m.rid,sid:c.sid,epoch,payload:o});audit({event:'session-open',transport});break;}
      case 'control.ack':if(p.rid!==unacked||!unacked)throw new Error('BAD_CONTROL_ACK');unacked=null;clearTimeout(largeTimer);break;
      case 'session.start':kernel.startSession(c.sid);break;
      case 'flow.credit':if(!Number.isSafeInteger(p.creditBytes)||p.creditBytes<credit||p.creditBytes>sent+LIMITS.windowBytes)throw new Error('BAD_CREDIT');credit=p.creditBytes;pump();break;
      case 'terminal.resize':if(!kernel.resize(c.sid,p.cols,p.rows))throw new Error('BAD_GEOMETRY');break;
      case 'terminal.signal':if(p.signal!=='SIGINT')throw new Error('BAD_SIGNAL');kernel.signal(c.sid,'SIGINT');break;
      case 'session.close':c.close('client-close');break;
      case 'session.status':if(busy)throw new Error('REQUEST_IN_PROGRESS');busy=true;host.image().then(i=>c.send({v:2,type:m.type,rid:m.rid,sid:c.sid,payload:{...kernel.status(c.sid),...i,uptimeMs:Date.now()-started,protocol:PROTOCOL,transport,version:VERSION}})).catch(()=>c.close('STATUS_ERROR')).finally(()=>{busy=false;});break;
      case 'catalog.get':c.send({v:2,type:m.type,rid:m.rid,sid:c.sid,payload:{entries:describeRegistry(kernel.registry,kernel.sessions.get(c.sid)?.aliases)}});break;
      case 'volume.export':if(busy)throw new Error('REQUEST_IN_PROGRESS');busy=true;flush().then(()=>c.send({v:2,type:m.type,rid:m.rid,sid:c.sid,payload:{schema:'JA21-VOLUME/1',version:VERSION,writtenAt:Date.now(),snap:kernel.vfs.exportSnapshot()}})).catch(()=>c.close('STORAGE_ERROR')).finally(()=>{busy=false;});break;
      case 'vcmd.import.begin':case 'vcmd.import.chunk':case 'vcmd.import.commit':case 'vcmd.import.cancel':{
       if(busy)throw new Error('REQUEST_IN_PROGRESS');
       if(!m.rid)throw new Error('BAD_REQUEST_ID');
       busy=true;importer.handle(m.type,p).then(async result=>{if(m.type==='vcmd.import.commit')await flush();c.send({v:2,type:m.type,rid:m.rid,sid:c.sid,payload:{ok:true,...result}});}).catch(e=>{importer.drop();c.send({v:2,type:m.type,rid:m.rid,sid:c.sid,payload:{ok:false,error:String(e.message).slice(0,500)}});}).finally(()=>{busy=false;});break;}
      case 'heartbeat.ping':c.send({v:2,type:'heartbeat.pong',rid:m.rid,sid:c.sid,payload:{}});break;
      default:throw new Error('UNKNOWN_MESSAGE');
     }
    }catch(e){const code=/^[A-Z0-9_]{1,50}$/.test(e.message)?e.message:'BAD_MESSAGE';audit({event:'protocol-rejected',code});c.close(code,1);}
   }
  };
  let touched=Date.now();
  function pump(){while(queue.length&&!closed){
   const room=credit-sent;if(room<=0)return;
   const first=queue[0],count=Math.min(first.length,room),b=first.subarray(0,count);
   if(count===first.length)queue.shift();else queue[0]=first.subarray(count);
   queued-=count;sent+=count;io.send(encodeData(KIND_OUTPUT,++seqOut,b));
  }}
  const openTimer=setTimeout(()=>c.close('OPEN_TIMEOUT',1),5000);
  const expiry=setTimeout(()=>c.close('SESSION_EXPIRED'),2*3600000);
  const idleTimer=setInterval(()=>{if(Date.now()-touched>15*60000)c.close('IDLE_TIMEOUT');},30000);
  connections.add(c);c.send({v:2,type:'hello',payload:{protocol:PROTOCOL,version:VERSION,epoch,limits:LIMITS,transport}});return c;
 }
 return {kernel,attach,flush,info:{epoch,seal,version:VERSION,transport},stop:async()=>{stopped=true;for(const c of [...connections])c.close('server-stop');for(const id of [...kernel.sessions.keys()])kernel.closeSession(id,'server-stop');await kernel.settle();await flush();},get connectionCount(){return connections.size;}};
}
