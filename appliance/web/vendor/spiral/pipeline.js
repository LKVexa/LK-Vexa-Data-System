/** Bounded SPIRAL grammar. This is not Bash; no substitution, globbing, jobs or host shell. */
const OPS=['&&','||','>>',';','|','>','<'];
export function tokenize(line) {
  if(typeof line!=='string'||line.length>4096) throw new Error('line exceeds 4096 characters');
  if(/[\0\r\n]/.test(line)) throw new Error('one command line only');
  const tokens=[];let i=0;
  while(i<line.length){
    if(/\s/.test(line[i])){i++;continue;}
    if(line[i]==='#')break;
    const op=OPS.find(x=>line.startsWith(x,i));
    if(op){tokens.push({type:'op',value:op});if(tokens.length>512)throw new Error('too many tokens');i+=op.length;continue;}
    const parts=[];let quoted=false;
    const add=(value,expand)=>{if(parts.length&&parts.at(-1).expand===expand)parts.at(-1).value+=value;else parts.push({value,expand});};
    while(i<line.length&&!/\s/.test(line[i])&&!OPS.some(x=>line.startsWith(x,i))){
      const c=line[i++];
      if(c==='`'||(c==='$'&&line[i]==='(')||c==='&')throw new Error('unsupported shell construct');
      if(c==='\\'){if(i>=line.length)throw new Error('trailing escape');add(line[i++],false);quoted=true;continue;}
      if(c==="'"||c==='"'){
        quoted=true;let closed=false;
        while(i<line.length){const q=line[i++];if(q===c){closed=true;break;}
          if(c==='"'&&q==='\\'&&['"','\\','$','`'].includes(line[i]))add(line[i++],false);
          else{if(c==='"'&&(q==='`'||(q==='$'&&line[i]==='(')))throw new Error('command substitution is unsupported');add(q,c==='"');}
        }
        if(!closed)throw new Error('unclosed quote');
        if(!parts.length)add('',false);
      }else add(c,true);
    }
    tokens.push({type:'word',value:parts.map(p=>p.value).join(''),parts,quoted});
    if(tokens.length>512)throw new Error('too many tokens');
  }
  return tokens;
}
export function expandWord(token,env){
 const parts=token.parts||[{value:token.value,expand:!token.quoted}];
 return parts.map((p,i)=>{
  let s=p.value;if(!p.expand)return s;
  if(i===0&&!token.quoted&&(s==='~'||s.startsWith('~/')))s=(env.get('HOME')||'/home/operator')+s.slice(1);
  return s.replace(/\$\{([^}]+)\}|\$([A-Za-z_][A-Za-z0-9_]*)|\$\?/g,(m,bare1,bare2)=>{
   if(m==='$?')return String(env.get('?')??'0');
   const k=bare1||bare2;if(!/^[A-Za-z_][A-Za-z0-9_]*$/.test(k))throw new Error('unsupported parameter expansion; use the host reference outside SPIRAL');
   return String(env.get(k)??'');
  });
 }).join('');
}
export function parse(line,env){
 const tokens=tokenize(line),segments=[];let seg={op:null,pipeline:[]},cmd={argv:[],redirects:[]};
 const command=()=>{if(!cmd.argv.length)throw new Error('missing command');seg.pipeline.push(cmd);cmd={argv:[],redirects:[]};};
 for(let i=0;i<tokens.length;i++){
  const t=tokens[i];if(t.type==='word'){cmd.argv.push(t);continue;}
  if(['>','>>','<'].includes(t.value)){
   const next=tokens[++i];if(!next||next.type!=='word')throw new Error('missing redirection target');
   const type=t.value==='<'?'in':t.value==='>>'?'append':'out';
   if(cmd.redirects.some(r=>(r.type==='in')===(type==='in')))throw new Error('duplicate stream redirection');
   cmd.redirects.push({type,target:next});continue;
  }
  command();if(t.value==='|')continue;
  segments.push(seg);seg={op:t.value===';'?null:t.value,pipeline:[]};
  if(i===tokens.length-1&&t.value!==';')throw new Error('trailing operator');
 }
 if(cmd.argv.length||cmd.redirects.length){command();segments.push(seg);}else if(seg.pipeline.length)throw new Error('trailing pipe');
 // Expansion is deliberately deferred by the kernel until each command executes ($? and export work across joins).
 if(env) return segments.map(s=>({...s,pipeline:s.pipeline.map(c=>({argv:c.argv.map(t=>expandWord(t,env)),redirects:c.redirects.map(r=>({...r,target:expandWord(r.target,env)}))}))}));
 return segments;
}
