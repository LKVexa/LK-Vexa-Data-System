/** Per-connection bounded upload. Imports never execute and cannot replace existing virtual files. */
import {vcmdText,VCMD_LIMITS} from './language.js';
const enc=new TextEncoder();
export function createVcmdImporter(vfs) {
 let current=null;
 const drop=()=>{if(current)clearTimeout(current.timer);current=null;};
 return {drop, async handle(type,p) {
  if(type==='vcmd.import.begin') {
   if(current)throw new Error('An import is already in progress');
   if(typeof p.name!=='string'||!/^[A-Za-z0-9][A-Za-z0-9._ -]{0,100}\.vcmd$/i.test(p.name)||!Number.isInteger(p.bytes)||p.bytes<9||p.bytes>VCMD_LIMITS.fileBytes||(p.sha256!==null&&(typeof p.sha256!=='string'||!/^[a-f0-9]{64}$/.test(p.sha256))))throw new Error('Invalid .vcmd name, size, or SHA-256');
   if(p.bundle!==undefined&&(typeof p.bundle!=='string'||!/^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/.test(p.bundle)))throw new Error('Invalid VCMD bundle identifier');
   current={id:crypto.randomUUID(),bundle:p.bundle,name:p.name,bytes:p.bytes,sha256:p.sha256,chunks:[],received:0,index:0,timer:setTimeout(drop,120000)};
   return {id:current.id,chunkBytes:3072};
  }
  if(!current||p.id!==current.id)throw new Error('Unknown or expired VCMD import');
  if(type==='vcmd.import.cancel'){drop();return {cancelled:true};}
  if(type==='vcmd.import.chunk') {
   if(p.index!==current.index||typeof p.base64!=='string'||p.base64.length>4096||p.base64.length%4!==0||!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(p.base64))throw new Error('Invalid or out-of-order VCMD chunk');
   const bytes=Uint8Array.from(atob(p.base64),c=>c.charCodeAt(0));
   if(!bytes.length||current.received+bytes.length>current.bytes)throw new Error('VCMD upload exceeds declared size');
   current.chunks.push(bytes);current.received+=bytes.length;current.index++;
   return {received:current.received,index:current.index};
  }
  if(type!=='vcmd.import.commit')throw new Error('Unknown VCMD import operation');
  if(current.received!==current.bytes)throw new Error('Incomplete VCMD import');
  const item=current,bytes=new Uint8Array(item.bytes);let at=0;for(const b of item.chunks){bytes.set(b,at);at+=b.length;}
  const digest=await crypto.subtle.digest('SHA-256',bytes),sha256=Array.from(new Uint8Array(digest),x=>x.toString(16).padStart(2,'0')).join('');
  if(current!==item)throw new Error('VCMD import was cancelled or expired');
  if(item.sha256!==null&&sha256!==item.sha256){drop();throw new Error('VCMD digest mismatch');}
  let text;try{text=vcmdText(bytes);}catch(e){drop();throw e;}
  const dir='/scripts/imported/'+(item.bundle||item.id),target=dir+'/'+item.name;
  // Quota validation happens in VFS. A rejected import never overwrites an existing source.
  if(vfs.exists(target))throw new Error('VCMD import collision');
  let prefix='',missing=0;
  for(const part of dir.split('/').filter(Boolean)){prefix+='/'+part;if(vfs.exists(prefix)){if(!vfs.isDir(prefix))throw new Error('VCMD import parent is not a directory');}else missing++;}
  const usage=vfs.usage();
  if(usage.nodes+missing+1>usage.maxNodes||usage.bytes+enc.encode(text).length>usage.maxBytes)throw new Error('VCMD import exceeds virtual volume quota');
  // All checks and mutations in this section are synchronous: another JS task cannot interleave.
  vfs.mkdir(dir,{recursive:true});
  vfs.writeFile(target,text);drop();
  const storedBytes=enc.encode(text),storedDigest=await crypto.subtle.digest('SHA-256',storedBytes);
  return {path:target,bytes:storedBytes.length,sha256:Array.from(new Uint8Array(storedDigest),x=>x.toString(16).padStart(2,'0')).join(''),sourceSha256:sha256,clientDigestVerified:item.sha256!==null,executed:false};
 }};
}
