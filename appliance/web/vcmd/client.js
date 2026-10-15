/** File-picker upload client. User-selected bytes only; never reads a typed native path. */
import {vcmdText,VCMD_LIMITS} from './language.js';
export function requestId() {
 if(globalThis.crypto?.randomUUID)return crypto.randomUUID();
 const b=new Uint8Array(16);crypto.getRandomValues(b);b[6]=(b[6]&15)|64;b[8]=(b[8]&63)|128;
 const h=Array.from(b,x=>x.toString(16).padStart(2,'0')).join('');return h.slice(0,8)+'-'+h.slice(8,12)+'-'+h.slice(12,16)+'-'+h.slice(16,20)+'-'+h.slice(20);
}
export async function importVcmdFiles(link,sid,files,onProgress=()=>{}) {
 if(!files.length||files.length>32)throw new Error('Select between one and 32 .vcmd files');
 const selected=[],names=new Set();let total=0;
 for(const file of files){
  if(!/^[A-Za-z0-9][A-Za-z0-9._ -]{0,100}\.vcmd$/i.test(file.name))throw new Error('Use a simple .vcmd basename; spaces are allowed');
  if(names.has(file.name.toLowerCase()))throw new Error('Duplicate selected filename');names.add(file.name.toLowerCase());
  if(file.size>VCMD_LIMITS.fileBytes)throw new Error(file.name+': larger than 512 KiB');
  total+=file.size;if(total>VCMD_LIMITS.totalBytes)throw new Error('Selected source files exceed 1 MiB');
  const bytes=new Uint8Array(await file.arrayBuffer());vcmdText(bytes);
  // Web Crypto SHA-256 is available in secure contexts. On explicitly opted-in HTTP LAN,
  // the authenticated server computes the source hash; no pretend client-side hash is supplied.
  const digest=crypto.subtle?await crypto.subtle.digest('SHA-256',bytes):null;
  const sha256=digest?Array.from(new Uint8Array(digest),x=>x.toString(16).padStart(2,'0')).join(''):null;
  selected.push({file,bytes,sha256});
 }
 const bundle=requestId(),paths=[];
 const action=async(type,payload)=>{const result=await link.request(sid,type,payload);if(!result?.ok)throw new Error(result?.error||'VCMD import failed');return result;};
 try {
  for(const {file,bytes,sha256} of selected){
   let transfer;
   try {
    transfer=await action('vcmd.import.begin',{name:file.name,bytes:bytes.length,sha256,bundle});
    for(let at=0,index=0;at<bytes.length;at+=3072,index++){
     const part=bytes.subarray(at,at+3072),base64=btoa(String.fromCharCode(...part));
     await action('vcmd.import.chunk',{id:transfer.id,index,base64});onProgress(file.name,Math.min(at+part.length,bytes.length),bytes.length);
     await new Promise(resolve=>setTimeout(resolve,100)); // below the protocol's 64 KiB/s input budget
    }
    const result=await action('vcmd.import.commit',{id:transfer.id});paths.push(result);onProgress(file.name,bytes.length,bytes.length,result);
   } catch(e) {if(transfer)await action('vcmd.import.cancel',{id:transfer.id}).catch(()=>{});throw e;}
  }
 } catch(e) {e.importedPaths=paths.map(x=>x.path);throw e;}
 return paths;
}
