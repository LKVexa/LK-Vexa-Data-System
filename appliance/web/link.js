import {requestId} from './vcmd/client.js';
import {encodeData,decodeData,KIND_INPUT,KIND_OUTPUT,PROTOCOL} from './vendor/vws/frame.js';
const WINDOW=262144;
export class ConsoleLink{
 constructor({mode='worker',token='',endpoint=''}={}){this.mode=mode;this.token=token;this.endpoint=endpoint;this.worker=null;this.ready=null;this.sessions=new Map();this.pending=new Map();this.connections=new Set();this.onData=()=>{};this.onExit=()=>{};this.onHalt=()=>{};this.onBrowser=()=>{};this.onComplete=()=>{};}
 async boot(){
  if(this.mode==='websocket')return;
  if(this.ready)return this.ready;
  this.ready=new Promise((resolve,reject)=>{
   const worker=new Worker(new URL('./worker.js',import.meta.url),{type:'module'});this.worker=worker;
   const timer=setTimeout(()=>{worker.terminate();reject(new Error('Worker startup timed out'));},10000);
   worker.onmessage=ev=>{if(ev.data?.type==='ready'){clearTimeout(timer);resolve();}else if(ev.data?.type==='error'){clearTimeout(timer);reject(new Error(ev.data.message));}else if(ev.data?.type==='stopped')this.stopResolve?.();};
   worker.onerror=()=>{clearTimeout(timer);reject(new Error('Worker failed. Check the browser console.'));for(const finish of [...this.connections])finish('Worker failed');};
  });return this.ready;
 }
 async open(cols,rows){
  await this.boot();if(this.sessions.size>=8)throw new Error('Maximum 8 terminal sessions');
  return new Promise((resolve,reject)=>{
   const rid=requestId();let io,sid=null,settled=false,seqIn=0,seqOut=0,consumed=0,reportedCredit=0,helloSeen=false;const decoder=new TextDecoder('utf-8',{fatal:true});
   const timer=setTimeout(()=>fail(new Error('Session open timed out')),7000);
   let closed=false;
   const finish=(reason,code=1)=>{
    if(closed)return;closed=true;clearTimeout(timer);this.connections.delete(finish);
    if(!settled){settled=true;reject(new Error(reason));}
    if(sid){this.sessions.delete(sid);for(const [id,p] of this.pending)if(p.sid===sid){clearTimeout(p.timer);this.pending.delete(id);p.reject(new Error(reason));}this.onExit({sid,reason,code});}
    io?.close();
   };
   const fail=err=>finish(err.message||'Connection failed');
   this.connections.add(finish);
   const receive=raw=>{
    if(closed)return;
    try{
     if(typeof raw!=='string'){
      if(!sid)throw new Error('Output before session open');const f=decodeData(raw);if(f.kind!==KIND_OUTPUT||f.seq!==seqOut+1)throw new Error('Invalid output sequence');seqOut=f.seq;consumed+=f.payload.length;
      const text=decoder.decode(f.payload,{stream:true});if(text)this.onData({sid,text});if(consumed-reportedCredit>=WINDOW/4){reportedCredit=consumed;io.send(JSON.stringify({v:2,type:'flow.credit',sid,payload:{creditBytes:WINDOW+consumed}}));}return;
     }
     if(raw.length>16777216)throw new Error('Oversized response');const m=JSON.parse(raw);if(!m||m.v!==2)throw new Error('Invalid response');
     if(m.type==='hello'){if(helloSeen)throw new Error('Duplicate hello');helloSeen=true;if(m.payload?.protocol!==PROTOCOL)throw new Error('Protocol mismatch');io.send(JSON.stringify({v:2,type:'session.open',rid,payload:{cols,rows,creditBytes:WINDOW}}));return;}
     if(m.type==='session.opened'&&m.rid===rid&&typeof m.sid==='string'){
      if(settled||!m.sid||m.sid.length>80)throw new Error('Duplicate or invalid session open');
      sid=m.sid;settled=true;clearTimeout(timer);this.sessions.set(sid,{io,sendText:text=>{const b=new TextEncoder().encode(text);if(b.length>8192)throw new Error('Input exceeds 8192 bytes');io.send(encodeData(KIND_INPUT,++seqIn,b));}});resolve(sid);return;
     }
     if(m.sid&&m.sid!==sid)throw new Error('Cross-session response');
     if(m.type==='browser'){this.onBrowser(m.payload);return;}
     if(m.type==='runtime.halt'){this.onHalt(m.payload);return;}
     if(m.type==='session.exit'){finish(m.payload?.reason||'Session closed',m.payload?.code??0);return;}
     if(m.type==='error')throw new Error(m.payload?.message||'Server error');
     if(m.sid&&m.sid!==sid)throw new Error('Cross-session response');
     if(m.type==='command.complete'){this.onComplete({sid,...m.payload});return;}
     if(m.rid&&new TextEncoder().encode(raw).length>32768)io.send(JSON.stringify({v:2,type:'control.ack',sid,payload:{rid:m.rid}}));
     if(m.rid&&this.pending.has(m.rid)){const p=this.pending.get(m.rid);if(p.sid!==sid||p.type!==m.type)throw new Error('Mismatched response');clearTimeout(p.timer);this.pending.delete(m.rid);p.resolve(m.payload);}
    }catch(e){fail(e);}
   };
   if(this.mode==='worker'){
    const channel=new MessageChannel();io={send:m=>channel.port2.postMessage(m),close:()=>channel.port2.close()};channel.port2.onmessage=e=>receive(e.data);channel.port2.onmessageerror=()=>fail(new Error('MessagePort error'));this.worker.postMessage({type:'attach'},[channel.port1]);
   }else{
    let address;try{address=resolveWebSocketUrl(this.endpoint,location.href);}catch(e){return fail(e);}
    const ws=new WebSocket(address);ws.binaryType='arraybuffer';
    io={send:m=>{if(ws.readyState!==WebSocket.OPEN)throw new Error('Socket is not open');if(ws.bufferedAmount>262144)throw new Error('Socket input backlog');ws.send(m);},close:()=>{if(ws.readyState<2)ws.close();}};
    ws.onopen=()=>ws.send(JSON.stringify({type:'auth',token:this.token}));ws.onmessage=e=>receive(e.data);
    ws.onerror=()=>fail(new Error('WebSocket connection failed'));
    ws.onclose=ev=>finish(ev.reason||'WebSocket disconnected');
   }
  });
 }
 control(sid,type,payload={},rid){const s=this.sessions.get(sid);if(!s)throw new Error('Session is closed');s.io.send(JSON.stringify({v:2,type,sid,payload,...(rid?{rid}:{} )}));}
 start(sid){this.control(sid,'session.start');}
 write(sid,text){if(typeof text!=='string'||!text)return;const s=this.sessions.get(sid);if(!s)throw new Error('Session is closed');s.sendText(text);}
 resize(sid,cols,rows){this.control(sid,'terminal.resize',{cols,rows});}
 interrupt(sid){this.control(sid,'terminal.signal',{signal:'SIGINT'});}
 close(sid){if(this.sessions.has(sid))this.control(sid,'session.close');}
 request(sid,type,payload){
  const session=this.sessions.get(sid);if(!session)return Promise.reject(new Error('Session is closed'));
  if(!session.requests)session.requests=new Map();const key=payload===undefined?type:requestId();if(session.requests.has(key))return session.requests.get(key);
  if(session.requests.size>=8)return Promise.reject(new Error('Too many queued requests'));
  const work=(session.requestTail||Promise.resolve()).catch(()=>{}).then(()=>this._requestNow(sid,type,payload||{}));
  session.requestTail=work;session.requests.set(key,work);
  work.then(()=>{if(session.requests.get(key)===work)session.requests.delete(key);},()=>{if(session.requests.get(key)===work)session.requests.delete(key);});return work;
 }
 _requestNow(sid,type,payload={}){if(this.pending.size>=8)return Promise.reject(new Error('Too many pending requests'));const rid=requestId();return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{this.pending.delete(rid);reject(new Error('Request timed out'));},5000);this.pending.set(rid,{sid,type,resolve,reject,timer});try{this.control(sid,type,payload,rid);}catch(e){clearTimeout(timer);this.pending.delete(rid);reject(e);}});}
 async shutdown(){
  let shutdownError;
  if(this.worker){try{await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Storage flush timed out; last changes may not be saved')),6000);this.stopResolve=()=>{clearTimeout(timer);resolve();};this.worker.postMessage({type:'stop'});});}catch(e){shutdownError=e;}finally{this.worker.terminate();this.worker=null;}}
  for(const finish of [...this.connections])finish('Disconnected');
  for(const s of this.sessions.values())s.io.close();this.sessions.clear();for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(new Error('Disconnected'));}this.pending.clear();this.ready=null;this.token='';if(shutdownError)throw shutdownError;
 }
}

/** Same-origin default or a user-selected endpoint, also subject to the serving page CSP. */
export function resolveWebSocketUrl(endpoint,pageUrl) {
 const page=new URL(pageUrl);let url;
 try {url=new URL(endpoint||page.origin.replace(/^http/,'ws')+'/socket');}catch{throw new Error('Use a complete ws:// or wss:// endpoint ending in /socket');}
 if(!['ws:','wss:'].includes(url.protocol)||url.username||url.password||url.pathname!=='/socket'||url.search||url.hash)throw new Error('Endpoint must use ws(s)://HOST[:PORT]/socket without credentials, query or fragment');
 if(page.protocol==='https:'&&url.protocol!=='wss:')throw new Error('An HTTPS page requires a secure wss:// endpoint');
 return url.href;
}
