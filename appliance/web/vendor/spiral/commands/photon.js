export default [{name:'photon',summary:'inspect or verify the supplied Photon identity; snapshot its VEC state',usage:'photon [status|verify|snapshot]',async run(ctx){
 const action=ctx.args[0]||'status';
 if(ctx.args.length>1||!['status','verify','snapshot'].includes(action)){ctx.stderr.write('photon: usage: photon [status|verify|snapshot]\n');return 64;}
 if(!ctx.host.photon){ctx.stderr.write('photon: UNAVAILABLE: use the native Photon console host; no operation executed\n');return 69;}
 try{const result=await ctx.host.photon(action);ctx.stdout.write(JSON.stringify(result,null,2)+'\n');return result?.ok===false?1:0;}
 catch(e){ctx.stderr.write('photon: '+String(e.message||e)+'\n');return 1;}
}}];
