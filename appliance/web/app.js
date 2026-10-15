import {startPhotonEngine} from './photon-client.js';
import {DESKTOP_MARKER,requestDesktopSession} from './desktop.js';
import {quoteWord} from './vcmd/language.js';
import {BUNDLED_VCMD} from './vcmd/bundled.js';
import {importVcmdFiles} from './vcmd/client.js';
import {VERSION} from './version.js';
import {ConsoleLink} from './link.js';
import {Screen} from './vendor/vt/screen.js';
import {Parser} from './vendor/vt/parser.js';
import {CanvasRenderer} from './vendor/vt/renderer-canvas.js';
import {CommandRegistry} from './vendor/spiral/registry.js';
import builtins from './vendor/spiral/commands/index.js';
import {describeRegistry,searchCatalog} from './catalog.js';
const $=id=>document.getElementById(id),registry=new CommandRegistry().install(builtins);
let entries=describeRegistry(registry),category='',activePanel='terminal',link=null,current=null,sequence=0,connecting=false,refreshing=false,lastStatus=null,detailEntry=null,history=[],histPos=0;
const sessions=new Map();
const painter=new CanvasRenderer($('canvas'),new Screen(100,30),{fontSize:innerWidth<700?12:14,lineHeight:1.45});
let paintPending=false,importing=false;
function notice(message){$('notice').textContent=message;$('notice').hidden=!message;}
function state(text,status='live'){$('connection-status').textContent=text;$('dot').className='dot '+status;}
function element(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e;}
function setPanel(name){
 activePanel=name;for(const p of ['terminal','commands','scripts','system']){const active=p===name;$('panel-'+p).hidden=!active;$('tab-'+p).setAttribute('aria-selected',String(active));$('tab-'+p).tabIndex=active?0:-1;}
 if(name==='terminal')requestAnimationFrame(layout);
 if(name==='commands')void updateCatalog();
}
for(const name of ['terminal','commands','scripts','system']){
 $('tab-'+name).onclick=()=>setPanel(name);
 $('tab-'+name).onkeydown=e=>{const all=['terminal','commands','scripts','system'];let i=all.indexOf(name);if(e.key==='ArrowRight')i=(i+1)%all.length;else if(e.key==='ArrowLeft')i=(i+all.length-1)%all.length;else if(e.key==='Home')i=0;else if(e.key==='End')i=all.length-1;else return;e.preventDefault();setPanel(all[i]);$('tab-'+all[i]).focus();};
}
function stage(command){setPanel('terminal');$('command').value=command;$('command').focus();notice('Command inserted. Review it, then press Execute.');}
$('browse-commands').onclick=()=>{setPanel('commands');$('search').focus();};
for(const command of ['help','commands','dc status','disk','image verify']){const b=element('button','ghost',command);b.onclick=()=>stage(command);$('quick-commands').append(b);}
function renderCategories(){
 const categories=['All categories',...new Set(entries.map(e=>e.category))];$('categories').replaceChildren();
 for(const c of categories){const b=element('button',(!category&&c==='All categories')||category===c?'selected':'',c);b.onclick=()=>{category=c==='All categories'?'':c;renderCategories();renderResults();};$('categories').append(b);}
}
function renderResults(){
 const selected=searchCatalog(entries,{query:$('search').value,kind:$('kind').value,risk:$('risk').value,availability:$('availability').value}).filter(e=>!category||e.category===category);
 $('result-count').textContent=`${selected.length} of ${entries.length} entries`;$('empty').hidden=selected.length!==0;$('command-results').replaceChildren();
 const frag=document.createDocumentFragment();
 for(const e of selected){
  const row=element('article','command-entry');row.dataset.id=e.id;
  const name=element('div');name.append(element('strong','entry-name',e.kind==='Data center'?e.syntax.split('\n')[0]:e.name));
  name.append(element('div','entry-id',e.id+' · '+(e.section||e.category)));
  if(e.kind!=='Data center'||e.syntax.includes('\n'))name.append(element('pre','entry-syntax',e.syntax));
  const summary=element('div','entry-summary');summary.append(element('p','entry-description',e.description));
  const badges=element('div','badges');badges.append(element('span','badge '+(e.risk==='X'?'danger':e.risk==='C'?'change':'query'),e.risk+' · '+({Q:'Query',D:'Diagnostic',C:'Change',X:'Destructive'}[e.risk])),element('span','badge',e.availability));summary.append(badges);
  const button=element('button','entry-button','View');button.setAttribute('aria-label','View '+e.id);button.onclick=()=>showDetails(e);
  row.append(name,summary,button);frag.append(row);
 }
 $('command-results').append(frag);
}
function catalogUpdated(){
 $('tab-count').textContent=String(entries.length);$('catalog-total').textContent=String(entries.length);
 const counts=[['terminal tools',entries.filter(e=>e.kind==='Terminal').length],['aliases',entries.filter(e=>e.kind==='Alias').length],['audited source commands',90],['added reference examples',67]];
 $('library-stats').replaceChildren();for(const [label,n] of counts){const span=element('span','stat-pill');span.append(element('strong','',n),document.createTextNode(label));$('library-stats').append(span);}
 renderCategories();renderResults();
}
for(const id of ['search','kind','risk','availability'])$(id).addEventListener(id==='search'?'input':'change',renderResults);
$('reset-filters').onclick=()=>{for(const id of ['search','kind','risk','availability'])$(id).value='';category='';renderCategories();renderResults();};
function showDetails(e){
 detailEntry=e;$('detail-id').textContent=e.id+' / '+e.source;$('detail-title').textContent=e.name;$('detail-description').textContent=e.description;$('detail-syntax').textContent=e.syntax;$('detail-note').textContent=e.note;
 $('detail-meta').replaceChildren();for(const [key,value] of [['Risk',e.risk+' (basic invocation; flags and redirection can change risk)'],['Availability',e.availability],['Plane',e.plane],['Aliases',(e.aliases||[]).join(', ')||'—'],['Section',e.section||'terminal']])$('detail-meta').append(element('dt','',key),element('dd','',value));
 $('copy-command').disabled=e.availability==='Removed';$('copy-command').textContent=e.kind==='Data center'?'Copy reference syntax':'Copy syntax';
 $('insert-command').textContent=e.kind==='Data center'?'Show reference in terminal':e.executable?'Insert command name':'Show tool help in terminal';
 $('details').showModal();
}
$('close-details').onclick=()=>$('details').close();
$('copy-command').onclick=async()=>{try{await navigator.clipboard.writeText(detailEntry.syntax);$('copy-command').textContent='Copied';}catch{notice('Clipboard permission was denied. Select and copy the displayed syntax manually.');}};
$('insert-command').onclick=()=>{const e=detailEntry;$('details').close();stage(e.kind==='Data center'?'dc show '+e.id:e.executable?e.name:'help '+e.name);};
function mirror(){const s=sessions.get(current)?.screen;if(!s)return;$('text-output').textContent=[...s.scrollback,...s.grid].slice(-500).map(row=>row.map(c=>c.ch).join('').trimEnd()).join('\n');}
function paint(){if(paintPending)return;paintPending=true;requestAnimationFrame(()=>{paintPending=false;if(activePanel==='terminal')painter.render();mirror();});}
function geometry(){if(activePanel!=='terminal')return {cols:100,rows:30};const box=$('terminal'),pad=innerWidth<700?20:36;const g=painter.fit(Math.max(160,box.clientWidth-pad),Math.max(120,box.clientHeight-pad));return {cols:Math.max(2,Math.min(400,g.cols)),rows:Math.max(1,Math.min(200,g.rows))};}
function layout(){if(activePanel!=='terminal')return;const {cols,rows}=geometry();$('geometry').textContent=`${cols} × ${rows}`;for(const [id,s] of sessions){if(s.screen.cols===cols&&s.screen.rows===rows)continue;s.screen.resize(cols,rows);if(link?.sessions.has(id))try{link.resize(id,cols,rows);}catch{}}paint();}
new ResizeObserver(layout).observe($('terminal'));
function renderSessions(){
 $('sessions').replaceChildren();for(const [id,s] of sessions){const group=element('span','session-tab'+(id===current?' active':''));const b=element('button','',`0${s.n} · ${s.closed?'closed':'session'}`);b.setAttribute('aria-label','Activate session '+s.n);b.onclick=()=>activate(id);const close=element('button','close','×');close.setAttribute('aria-label','Close session '+s.n);close.onclick=()=>closeSession(id);group.append(b,close);$('sessions').append(group);}
 $('new-session').disabled=connecting||sessions.size>=8;
}
function activate(id){const s=sessions.get(id);if(!s)return;current=id;painter.screen=s.screen;painter.scrollToBottom();renderSessions();paint();$('footer-session').textContent='Session '+id;void refreshStatus();}
async function openSession(){
 if(!link||connecting&&sessions.size)return;
 try{const {cols,rows}=geometry(),id=await link.open(cols,rows);const screen=new Screen(cols,rows),parser=new Parser(screen);sessions.set(id,{screen,parser,n:++sequence,closed:false});activate(id);link.start(id);state(link.mode==='worker'?'Browser Worker connected':'Authenticated WebSocket connected');void updateCatalog();}catch(e){state('Connection failed','error');notice(e.message);}
}
function closeSession(id){try{link?.close(id);}catch{}sessions.delete(id);if(current===id){current=[...sessions.keys()].at(-1)||null;if(current)activate(current);else{$('footer-session').textContent='No session';lastStatus=null;}}renderSessions();}
async function connect(){
 if(importing){notice('Finish the file import before changing transports.');return;}
 if(connecting)return;connecting=true;notice('');state('Connecting virtual server','');
 try{
  const mode=$('mode').value,token=$('token').value.trim();if(mode==='worker'&&!globalThis.isSecureContext)throw new Error('Browser Worker requires HTTPS (or a browser-recognized trustworthy local origin). Use network WebSocket mode for an explicitly unencrypted test LAN.');if(mode==='websocket'&&!/^[a-f0-9]{64}$/.test(token))throw new Error('Paste the 64-character token printed in the launcher window.');
  if(link)await link.shutdown();sessions.clear();current=null;lastStatus=null;renderSessions();
  link=new ConsoleLink({mode,token,endpoint:$('endpoint').value.trim()});
  link.onData=({sid,text})=>{const s=sessions.get(sid);if(s){s.parser.write(text);if(sid===current){painter.scrollToBottom();paint();}}};
  link.onExit=({sid,reason,code})=>{const s=sessions.get(sid);if(s){s.closed=true;s.parser.write(`\r\n[session closed: ${reason}; code ${code??0}]\r\n`);if(sid===current){state('Session closed','');paint();}}renderSessions();};
  link.onComplete=({sid,code})=>{if(sid===current){$('footer-session').textContent=`Session ${sid} · exit ${code}`;void updateCatalog();setTimeout(refreshStatus,50);}};
  link.onHalt=({reason})=>{if(reason==='reboot'){notice('Virtual sessions are restarting.');setTimeout(()=>void connect(),100);}else notice('Virtual runtime powered off. Use Connect / restart. In WebSocket mode, restart BOOT.cmd.');};
  link.onBrowser=b=>{setPanel('system');$('surface-url').value=b.url;$('surface-output').textContent=b.visible?(b.url==='ram://status'?JSON.stringify(lastStatus,null,2):'about:blank'):'Surface hidden';};
  $('footer-scope').textContent=mode==='worker'?'MessagePort · browser volume':'RFC6455 · server volume';
  await openSession();
 }catch(e){state('Connection failed','error');notice(e.message);}finally{connecting=false;renderSessions();}
}
async function refreshStatus(){if(refreshing||!current||!link?.sessions.has(current))return;refreshing=true;try{const sid=current,st=await link.request(sid,'session.status');if(current!==sid)return;lastStatus=st;$('system-status').textContent=JSON.stringify(st,null,2);$('mini-status').replaceChildren();for(const [k,v] of [['Execution',st.profile],['Storage',st.disk?.fs],['Volume',st.disk?.synced?'Committed':'Unsaved / error'],['Virtual files',`${st.ram?.nodes??0} nodes · ${((st.ram?.bytes??0)/1024).toFixed(1)} KiB`],['Hardware','Not exposed']])$('mini-status').append(element('dt','',k),element('dd','',v||'Unavailable'));if(st.disk?.error)notice('Storage error: '+st.disk.error);}catch(e){if(link?.sessions.has(current))notice(e.message);}finally{refreshing=false;}}
async function updateCatalog(){if(!current||!link?.sessions.has(current))return;try{const sid=current,data=await link.request(sid,'catalog.get');if(sid===current&&Array.isArray(data.entries)){entries=data.entries;catalogUpdated();}}catch{/* Commands remain available from the same local runtime registry when a session is closed. */}}
function send(text){try{if(!current)throw new Error('Open a terminal session first');link.write(current,text);return true;}catch(e){notice(e.message);return false;}}
$('command-form').onsubmit=e=>{e.preventDefault();const command=$('command').value;if(!command.trim())return;if(/[\x00-\x1f\x7f]/.test(command)){notice('Use one command line without control characters.');return;}if(command.length>4096){notice('Command exceeds the 4096-character limit; nothing was sent.');return;}if(new TextEncoder().encode(command).length>8000){notice('Command exceeds the input byte limit.');return;}if(lastStatus?.busy){notice('The current command is busy. Use direct terminal input or interrupt it first.');return;}notice('');if(!send('\x15'+command+'\r'))return;history.push(command);history=history.slice(-200);histPos=history.length;$('command').value='';};
$('command').onkeydown=e=>{if(e.key==='ArrowUp'){e.preventDefault();histPos=Math.max(0,histPos-1);$('command').value=history[histPos]||'';}else if(e.key==='ArrowDown'){e.preventDefault();histPos=Math.min(history.length,histPos+1);$('command').value=history[histPos]||'';}};
$('new-session').onclick=()=>void openSession();$('connect').onclick=()=>void connect();$('interrupt').onclick=()=>{try{link?.interrupt(current);}catch(e){notice(e.message);}};
$('toggle-text').onclick=()=>{$('text-output').hidden=!$('text-output').hidden;$('toggle-text').textContent=$('text-output').hidden?'Text view':'Hide text';};
$('mode').onchange=()=>{$('token-group').hidden=$('mode').value!=='websocket';};
$('terminal').onmousedown=e=>{e.preventDefault();$('direct-input').focus({preventScroll:true});};
$('terminal').onwheel=e=>{e.preventDefault();painter.scrollBy(e.deltaY>0?3:-3);paint();};
const keys={Enter:'\r',Backspace:'\x7f',Tab:'\t',ArrowUp:'\x1b[A',ArrowDown:'\x1b[B',ArrowRight:'\x1b[C',ArrowLeft:'\x1b[D',Home:'\x1b[H',End:'\x1b[F',Delete:'\x1b[3~'};
$('direct-input').onkeydown=e=>{if(e.isComposing)return;let code=keys[e.key];if(e.ctrlKey&&!e.shiftKey&&/^[a-z]$/i.test(e.key))code=String.fromCharCode(e.key.toUpperCase().charCodeAt(0)-64);if(code){e.preventDefault();send(code);}};
$('direct-input').oninput=e=>{if(e.isComposing)return;const value=e.target.value;e.target.value='';if(value)send(value);};
$('direct-input').oncompositionend=e=>{if(e.data)send(e.data);$('direct-input').value='';};
$('direct-input').onpaste=e=>{e.preventDefault();const text=e.clipboardData?.getData('text/plain')||'';if(/[\r\n]/.test(text)){notice('Multiline paste is not auto-executed. Paste and review one command at a time in the command field.');return;}if(text.length>4096){notice('Paste exceeds the 4096-character limit.');return;}stage(text);};
async function exportVolume(){if(!current||!link?.sessions.has(current)){notice('An active session is required for export.');return;}try{while(refreshing)await new Promise(r=>setTimeout(r,30));refreshing=true;const record=await link.request(current,'volume.export');const blob=new Blob([JSON.stringify(record,null,2)],{type:'application/json'}),url=URL.createObjectURL(blob),a=element('a');a.href=url;a.download='JA21-'+link.mode+'-volume-'+new Date().toISOString().replace(/[:.]/g,'-')+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);notice('Virtual volume exported. Store it securely; it may contain sensitive data.');}catch(e){notice(e.message);}finally{refreshing=false;}}
$('export').onclick=exportVolume;$('export-system').onclick=exportVolume;
$('persist').onclick=async()=>{try{const ok=await navigator.storage?.persist?.();$('persistence-status').textContent=ok?'Persistence granted by the browser. Explicit deletion still removes data.':'Persistence not granted. Export important files.';}catch(e){$('persistence-status').textContent=e.message;}};
$('surface-form').onsubmit=e=>{e.preventDefault();const url=$('surface-url').value.trim();$('surface-output').textContent=url==='about:blank'?'about:blank':url==='ram://status'?JSON.stringify(lastStatus,null,2):'Navigation rejected. No external origins are admitted.';};

$('endpoint').value=/^https?:$/.test(location.protocol)?location.origin.replace(/^http/,'ws')+'/socket':'';
if(globalThis.isSecureContext===false){$('mode').value='websocket';$('token-group').hidden=false;}
for(const [id,verb] of [['vcmd-check','check'],['vcmd-show','show'],['vcmd-run','run']])$(id).onclick=()=>{
 const value=$('vcmd-path').value.trim();
 if(!/\.vcmd$/i.test(value)||/[\x00-\x1f\x7f]/.test(value)){notice('Enter a .vcmd path without control characters.');return;}
 if($('vcmd-local').checked&&link?.mode!=='websocket'){notice('Server-local paths require an authenticated WebSocket session. Use Import for this browser computer.');return;}
 stage('vcmd '+verb+($('vcmd-local').checked?' --local':'')+' '+quoteWord(value));
};
$('vcmd-use-corpus').onclick=()=>{$('vcmd-path').value='/corpus/command-language.vcmd';$('vcmd-local').checked=false;notice('Corpus path selected. Check or stage a run to view the reference tour.');};
$('vcmd-download-corpus').onclick=()=>{const url=URL.createObjectURL(new Blob([BUNDLED_VCMD['/corpus/command-language.vcmd']],{type:'text/plain;charset=utf-8'})),a=element('a');a.href=url;a.download='command-language.vcmd';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
for(const name of ['hello','filesystem-demo','datacenter-triage','workflow','fragment','continue-on-error']){const b=element('button','ghost',name+'.vcmd');b.onclick=()=>{$('vcmd-path').value='/scripts/examples/'+name+'.vcmd';$('vcmd-local').checked=false;notice('Example path selected; nothing has run.');};$('vcmd-examples').append(b);}
$('vcmd-import').onclick=async()=>{
 if(importing)return;
 if(!current||!link?.sessions.has(current)){notice('Connect a terminal session before importing.');return;}
 if(link.mode==='websocket'&&!$('vcmd-upload-consent').checked){notice('Approve sending the selected sources to the WebSocket server first.');return;}
 const files=[...$('vcmd-files').files];if(!files.length){notice('Choose .vcmd files from this computer first.');return;}
 importing=true;$('vcmd-import').disabled=true;$('vcmd-imported').replaceChildren();
 try {
  const imported=await importVcmdFiles(link,current,files,(name,done,total,result)=>{
   $('vcmd-import-status').textContent=`${name}: ${done}/${total} bytes`+(result?' — saved, not executed':'');
   if(result){const b=element('button','ghost',result.path);b.onclick=()=>{$('vcmd-path').value=result.path;$('vcmd-local').checked=false;};$('vcmd-imported').append(b);}
  });
  $('vcmd-path').value=imported[0].path;$('vcmd-local').checked=false;
  $('vcmd-import-status').textContent=`Imported ${imported.length} file(s), not executed. Select the root path and stage a check.\n`+imported.map(x=>x.path+'\nSHA-256 (stored UTF-8): '+x.sha256).join('\n');
  notice('VCMD files imported into the connected virtual volume. No script was executed.');
 }catch(e){$('vcmd-import-status').textContent='Import failed: '+e.message+(e.importedPaths?.length?'\nEarlier successful imports remain: '+e.importedPaths.join(', '):'');}
 finally{importing=false;$('vcmd-import').disabled=false;}
};

async function initializeTransport(){
 if(location.hash!==DESKTOP_MARKER){await connect();return;}
 // In desktop mode do not create an unrelated Worker volume before the server handoff.
 $('mode').value='websocket';$('token-group').hidden=false;
 state('Waiting for native JA21 session','');
 try{
  const session=await requestDesktopSession({bridge:globalThis.chrome?.webview,origin:location.origin});
  await startPhotonEngine(session,{onFailure:error=>{state('Engine disconnected','bad');notice(error.message);}});
  $('endpoint').value=session.endpoint;$('token').value=session.token;
  await connect();
 }catch(e){state('Desktop handoff failed','error');notice(e.message);}
}
$('version').textContent=VERSION;catalogUpdated();void initializeTransport();setInterval(refreshStatus,3000);
