import photon from './photon.js';
import vcmd from './vcmd.js';
import datacenter from './datacenter.js';
// @ts-nocheck
import system from "./system.js";
import fsCmds from "./fs.js";
import coreutils from "./coreutils.js";
import browser from "./browser.js";
import ram from "./ram.js";

const flags={ls:['l','a','h'],mkdir:['p'],rm:['r','f'],write:['a'],cat:['n'],wc:['l','w','c'],sort:['r','n','u'],uniq:['c'],tr:['d'],history:['c'],uname:['a']};
export default [...system,...fsCmds,...coreutils,...browser,...ram,...datacenter,...vcmd,...photon].map(d=>({...d,parse:{...d.parse,...(d.name==='grep'?{}:{allowed:flags[d.name]||[]}),...(d.name==='echo'?{echo:true}:{}),...(['printf','seq','sleep','commands'].includes(d.name)||d.hostOnly?{raw:true}:{})}}));
