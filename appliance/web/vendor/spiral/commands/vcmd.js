/** Terminal entry points for VCMD/1. All execution stays in the virtual kernel. */
const help = `VCMD/1 — virtual command language
vcmd run [--local] [--trace] [--dry-run] PATH.vcmd
vcmd check [--local] PATH.vcmd
vcmd show [--local] PATH.vcmd
vcmd corpus
run [--local] [--trace] [--dry-run] PATH.vcmd

Use #!vcmd 1 as the first line. One SPIRAL command line per line.
Comments: # ...   Includes: @include "relative.vcmd"
Root policy: @on-error stop (default) or @on-error continue.
Virtual path example: run '/scripts/examples/hello.vcmd'
Server-local example: run --local 'D:\\VCMD\\job.vcmd'
--local reads only roots authorized by the server administrator.
Browser files must first be imported using the Scripts tab.
Checks parse syntax and includes; they do not authorize host commands.
No native shell, Bash loops, JavaScript evaluation or command substitution.
`;
export default [
 {name:'vcmd',summary:'check, inspect, or run a VCMD/1 script from a virtual or authorized local path',usage:'vcmd run|check|show [--local] [--trace] [--dry-run] PATH.vcmd | vcmd corpus | vcmd help',parse:{raw:true},async run(ctx){
  const args=[...ctx.args], mode=args.shift()||'help';
  if(mode==='help'){ctx.stdout.write(help);return 0;}
  if(mode==='corpus'){if(args.length){ctx.stderr.write('vcmd corpus takes no arguments\n');return 2;}ctx.stdout.write('Corpus: /corpus/command-language.vcmd\nExamples: /scripts/examples/\nCheck: vcmd check /corpus/command-language.vcmd\nRun safe reference tour: vcmd run /corpus/command-language.vcmd\n');return 0;}
  if(!['run','check','show'].includes(mode)){ctx.stderr.write(help);return 2;}
  return invoke(ctx,mode,args);
 }},
 {name:'run',summary:'run a .vcmd file by virtual path or an explicitly authorized server-local file path',usage:'run [--local] [--trace] [--dry-run] PATH.vcmd',parse:{raw:true},async run(ctx){return invoke(ctx,'run',ctx.args);}}
];
async function invoke(ctx,mode,args) {
 const opts={local:false,trace:false,dryRun:false};let target=null,end=false;
 for(const arg of args){
  if(!end&&arg==='--'){end=true;continue;}
  if(!end&&['--local','--trace','--dry-run'].includes(arg)){opts[{'--local':'local','--trace':'trace','--dry-run':'dryRun'}[arg]]=true;continue;}
  if(!end&&arg.startsWith('-')){ctx.stderr.write('vcmd: unknown option '+arg+'\n');return 2;}
  if(target!==null){ctx.stderr.write('vcmd: exactly one .vcmd path is required; quote paths with spaces\n');return 2;}target=arg;
 }
 if(!target || !/\.vcmd$/i.test(target)){ctx.stderr.write('vcmd: a .vcmd file path is required\n');return 2;}
 if(!ctx.vcmd){ctx.stderr.write('vcmd: script engine unavailable\n');return 69;}
 return ctx.vcmd(opts.dryRun?'check':mode,target,opts);
}
