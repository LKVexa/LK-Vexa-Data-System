import {createPhotonEngine} from './photon-engine.js';
let socket,engine,heartbeat,starting=false,dead=false;const pending=new Map();let serial=0,events=Promise.resolve(),queued=0;
function emit(value){postMessage(value);}
function send(m){
 if(socket?.readyState!==WebSocket.OPEN||dead)throw new Error('Photon broker disconnected');
 const text=JSON.stringify(m);if(text.length>8*1024*1024||socket.bufferedAmount+text.length>16*1024*1024)throw new Error('Engine transport backpressure');socket.send(text);
}
function fail(error){if(dead)return;dead=true;clearInterval(heartbeat);for(const p of pending.values()){clearTimeout(p.timer);p.reject(new Error('Engine connection lost'));}pending.clear();try{socket?.close();}catch{}emit({type:'error',error:String(error?.message||error)});}
function rpc(op,args){
 if(pending.size>=16)return Promise.reject(new Error('Capability concurrency limit'));
 const rid='p'+(++serial);return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(rid);reject(new Error('Capability timeout'));},15000);pending.set(rid,{resolve,reject,timer});try{send({type:'rpc',rid,op,args});}catch(e){clearTimeout(timer);pending.delete(rid);reject(e);}});
}
onmessage=async event=>{
 const m=event.data;
 if(m?.type==='stop'){try{if(!engine)throw new Error('Engine not ready');await engine.stop();clearInterval(heartbeat);}catch(e){emit({type:'stop.failed',error:String(e.message||e)});}return;}
 if(m?.type!=='start'||starting)return;starting=true;
 try{
  const endpoint=new URL(m.endpoint);if(!['ws:','wss:'].includes(endpoint.protocol)||endpoint.pathname!=='/engine'||endpoint.search||endpoint.hash||endpoint.username||endpoint.password||!/^([a-f0-9]{64})$/.test(m.token))throw new Error('Engine endpoint/credential');
  socket=new WebSocket(endpoint.href);socket.onopen=()=>{send({type:'auth',token:m.token});m.token=null;};
  socket.onmessage=event=>{
   try{
    if(typeof event.data!=='string'||event.data.length>8*1024*1024)throw new Error('Engine response limit');
    const msg=JSON.parse(event.data);
    if(msg.type==='rpc.result'){
     const p=pending.get(msg.rid);if(!p)throw new Error('Unknown capability result');pending.delete(msg.rid);clearTimeout(p.timer);msg.ok?p.resolve(msg.result):p.reject(new Error(msg.error||'Capability refused'));return;
    }
    if(msg.type==='engine.authenticated'){
     if(engine)throw new Error('Duplicate engine authorization');
     events=events.then(async()=>{engine=await createPhotonEngine({rpc,send,onState:type=>emit({type})});heartbeat=setInterval(()=>{try{send({type:'engine.ping'});}catch(e){fail(e);}},20000);}).catch(fail);return;
    }
    if(++queued>512)throw new Error('Engine event queue limit');events=events.then(()=>engine.receive(msg)).finally(()=>queued--).catch(fail);
   }catch(e){fail(e);}
  };
  socket.onclose=()=>{if(!dead)fail(new Error('Photon engine socket closed'));};socket.onerror=()=>fail(new Error('Photon engine connection failed'));
 }catch(e){fail(e);}
};
