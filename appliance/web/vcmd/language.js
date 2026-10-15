/** VCMD/1: bounded, pre-parsed SPIRAL scripts. No JavaScript or native shell evaluation. */
import {parse, tokenize, expandWord} from '../vendor/spiral/pipeline.js';
export const VCMD_LIMITS = Object.freeze({fileBytes:524288,totalBytes:1048576,files:32,depth:8,lines:8192,commands:2048,milliseconds:60000,outputBytes:1048576});
const enc = new TextEncoder();
export function vcmdText(value) {
  let text = typeof value === 'string' ? value : new TextDecoder('utf-8',{fatal:true}).decode(value);
  if (typeof text.isWellFormed === 'function' && !text.isWellFormed()) throw new Error('VCMD contains unpaired Unicode surrogates');
  if (enc.encode(text).length > VCMD_LIMITS.fileBytes) throw new Error('VCMD file exceeds 512 KiB');
  text = text.replace(/^\uFEFF/,'').replace(/\r\n/g,'\n');
  if (/[\x00-\x08\x0b-\x1f\x7f]/.test(text)) throw new Error('VCMD contains a forbidden control character');
  if (text.split('\n')[0].trim() !== '#!vcmd 1') throw new Error('VCMD requires #!vcmd 1 on the first line');
  return text;
}
export function quoteWord(text) { return "'" + String(text).replace(/'/g,"'\\''") + "'"; }
function literalInclude(line) {
  const tokens = tokenize(line);
  if(tokens.length!==2 || tokens.some(t=>t.type!=='word') || tokens[0].value!=='@include') throw new Error('Use @include "relative/file.vcmd"');
  const target = tokens[1].value;
  if (!target || target.includes('$') || !/\.vcmd$/i.test(target) || /[\x00-\x1f]/.test(target)) throw new Error('Include must be a literal .vcmd path without expansion');
  return target;
}
/** Read the complete include graph into an immutable plan before running its first command. */
export async function compileVcmd(root, read, {signal}={}) {
  const files=[], commands=[], active=new Set(), seen=new Map(); let totalBytes=0, lines=0, onError='stop';
  async function visit(request, parent=null, depth=0) {
    if(signal?.aborted)throw new Error('VCMD interrupted');
    if(depth>=VCMD_LIMITS.depth)throw new Error('VCMD include depth exceeds 8');
    if(files.length>=VCMD_LIMITS.files)throw new Error('VCMD includes exceed 32 file visits');
    const file=await read(request,parent); if(!file || typeof file.id!=='string')throw new Error('Invalid VCMD reader response');
    if(active.has(file.id))throw new Error('VCMD include cycle: '+file.id);
    const text=vcmdText(file.text), size=enc.encode(text).length;
    totalBytes+=size;if(totalBytes>VCMD_LIMITS.totalBytes)throw new Error('VCMD aggregate source exceeds 1 MiB');
    if(signal?.aborted)throw new Error('VCMD interrupted');
    if(seen.has(file.id)&&seen.get(file.id)!==text)throw new Error('VCMD source changed between include visits: '+file.id);
    seen.set(file.id,text);
    const source=Object.freeze({id:file.id,text,size}); files.push(source);active.add(file.id);
    let codeSeen=false;
    try {
      for(const [i,raw] of text.split('\n').entries()) {
        if(++lines>VCMD_LIMITS.lines)throw new Error('VCMD source exceeds 8192 lines');
        if(raw.length>4096)throw new Error(file.id+':'+(i+1)+': line exceeds 4096 characters');
        const line=raw.trim();if(!line || line.startsWith('#'))continue;
        try {
          if(line.startsWith('@')) {
            if(/^@(?:name|description)\s+\S/.test(line)) { if(codeSeen)throw new Error('Metadata directives must precede commands'); continue; }
            if(/^@on-error\s+(stop|continue)$/.test(line)) { if(depth || codeSeen)throw new Error('@on-error is allowed only in the root preamble');onError=line.split(/\s+/)[1];continue; }
            if(line.startsWith('@include')) {codeSeen=true;await visit(literalInclude(line),file.id,depth+1);continue;}
            throw new Error('Unknown VCMD directive');
          }
          codeSeen=true;
          const segments=parse(raw);
          // Expansion values stay deferred, but unsupported parameter syntax is an error now.
          for(const segment of segments)for(const command of segment.pipeline){
            for(const token of [...command.argv,...command.redirects.map(r=>r.target)])expandWord(token,{get:()=>''});
          }
          const freeze=value=>{if(value&&typeof value==='object'&&!Object.isFrozen(value)){for(const child of Object.values(value))freeze(child);Object.freeze(value);}return value;};
          freeze(segments);
          if(segments.length) {
            if(commands.length>=VCMD_LIMITS.commands)throw new Error('VCMD exceeds 2048 command lines');
            commands.push(Object.freeze({source:file.id,line:i+1,text:raw,segments}));
          }
        } catch(e) { throw new Error(file.id+':'+(i+1)+': '+e.message); }
      }
    } finally {active.delete(file.id);}
  }
  await visit(root);
  return Object.freeze({schema:'VCMD-PLAN/1',files:Object.freeze(files),commands:Object.freeze(commands),onError,totalBytes});
}
