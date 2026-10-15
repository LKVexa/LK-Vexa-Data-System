/** Narrow native bootstrap only. No host shell/filesystem/execute API is exposed. */
export const DESKTOP_MARKER='#ja21-desktop';
export function validateBootstrap(value,origin,nonce){
 if(!value||typeof value!=='object'||Array.isArray(value))throw new Error('Invalid native session response');
 const keys=Object.keys(value).sort().join(',');
 if(keys!=='endpoint,engineToken,nonce,origin,protocol,token,type'||value.type!=='ja21.session.bootstrap'||value.protocol!==2||value.nonce!==nonce||value.origin!==origin||typeof value.engineToken!=='string'||!/^[a-f0-9]{64}$/.test(value.engineToken)||value.engineToken===value.token||typeof value.token!=='string'||value.token.length!==64||!/^[a-f0-9]{64}$/.test(value.token))throw new Error('Native session identity mismatch');
 const expected=origin.replace(/^http/,'ws')+'/socket';if(value.endpoint!==expected)throw new Error('Native session endpoint mismatch');
 return {endpoint:value.endpoint,token:value.token,engineToken:value.engineToken};
}
export function requestDesktopSession({bridge,origin,random=globalThis.crypto,timeoutMs=10000}={}){
 return new Promise((resolve,reject)=>{
  if(!bridge?.addEventListener||!bridge?.removeEventListener||!bridge?.postMessage){reject(new Error('Native JA21 bridge is unavailable. Launch using BOOT.cmd, or remove #ja21-desktop to use an ordinary browser.'));return;}
  if(!/^https?:\/\//.test(origin)){reject(new Error('The native session requires an HTTP(S) origin'));return;}
  let nonce;try{nonce=Array.from(random.getRandomValues(new Uint8Array(16)),x=>x.toString(16).padStart(2,'0')).join('');}catch{reject(new Error('Secure random bytes are unavailable'));return;}
  let timer,done=false;
  const finish=(error,value)=>{if(done)return;done=true;clearTimeout(timer);bridge.removeEventListener('message',onMessage);if(error)reject(error);else resolve(value);};
  const onMessage=event=>{if(event.data?.type!=='ja21.session.bootstrap')return;try{finish(null,validateBootstrap(event.data,origin,nonce));}catch(e){finish(e);}};
  bridge.addEventListener('message',onMessage);
  timer=setTimeout(()=>finish(new Error('Native session handoff timed out. The token was not persisted. Restart the console and inspect its launcher log.')),timeoutMs);
  try{bridge.postMessage({type:'ja21.session.request',protocol:2,nonce});}catch(e){finish(new Error('Native session request failed'));}
 });
}
