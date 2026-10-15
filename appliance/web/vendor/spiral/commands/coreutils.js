// @ts-nocheck
import { Buffer } from '../buffer.js';
import { gatherInput, toLines, fail } from './util.js';

/** SPIRAL built-ins — text & stream utilities (the coreutils family). */



export default [
  {
    name: 'echo',
    summary: 'write arguments to standard output',
    usage: 'echo [-n] [-e] [args...]',
    parse: {},
    async run(ctx) {
      const noNewline = ctx.flags.n === true;
      let text = ctx.args.join(' ');
      if (ctx.flags.e) {
        text = text
          .replace(/\\n/g, '\n').replace(/\\t/g, '\t')
          .replace(/\\r/g, '\r').replace(/\\e/g, '\x1b').replace(/\\\\/g, '\\');
      }
      ctx.stdout.write(text + (noNewline ? '' : '\n'));
      return 0;
    }
  },

  {
    name: 'printf',
    summary: 'format and print data',
    usage: 'printf FORMAT [args...]',
    async run(ctx) {
      if (!ctx.args.length) return fail(ctx, 'printf', 'usage: printf FORMAT [args...]');
      const fmt = ctx.args[0]
        .replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\e/g, '\x1b').replace(/\\r/g, '\r');
      const rest = ctx.args.slice(1);
      let i = 0;
      const out = fmt.replace(/%[-0-9.]*[sdxfc%]/g, (spec) => {
        if (spec === '%%') return '%';
        const v = rest[i++];
        const conv = spec[spec.length - 1];
        if (conv === 'd') return String(parseInt(v, 10) || 0);
        if (conv === 'x') return (parseInt(v, 10) || 0).toString(16);
        if (conv === 'f') return String(Number(v) || 0);
        if(conv==='c')return [...String(v??'')][0]||'';
        return v == null ? '' : String(v);
      });
      ctx.stdout.write(out);
      return 0;
    }
  },

  {
    name: 'cat',
    summary: 'concatenate files and print',
    usage: 'cat [-n] [file...]',
    parse: {},
    async run(ctx) {
      const text = await gatherInput(ctx);
      if (ctx.flags.n) {
        const lines = toLines(text);
        const w = String(lines.length).length;
        ctx.stdout.write(lines.map((l, i) =>
          `\x1b[2m${String(i + 1).padStart(w)}\x1b[0m  ${l}`).join('\n') + (lines.length ? '\n' : ''));
      } else {
        ctx.stdout.write(text);
      }
      return 0;
    }
  },

  {
    name: 'head',
    summary: 'output the first part of files',
    usage: 'head [-n count] [file...]',
    parse: { valued: ['n'] },
    async run(ctx) {
      const n=ctx.flags.n===undefined?10:Number(ctx.flags.n);
      if(!Number.isInteger(n)||n<0||n>20000)return fail(ctx,'count','expected integer 0..20000',2);
      const lines = toLines(await gatherInput(ctx)).slice(0, n);
      ctx.stdout.write(lines.join('\n') + (lines.length ? '\n' : ''));
      return 0;
    }
  },

  {
    name: 'tail',
    summary: 'output the last part of files',
    usage: 'tail [-n count] [file...]',
    parse: { valued: ['n'] },
    async run(ctx) {
      const n=ctx.flags.n===undefined?10:Number(ctx.flags.n);
      if(!Number.isInteger(n)||n<0||n>20000)return fail(ctx,'count','expected integer 0..20000',2);
      const all = toLines(await gatherInput(ctx));
      const lines = all.slice(Math.max(0, all.length - n));
      ctx.stdout.write(lines.join('\n') + (lines.length ? '\n' : ''));
      return 0;
    }
  },

  {
    name: 'wc',
    summary: 'count lines, words and bytes',
    usage: 'wc [-l] [-w] [-c] [file...]',
    async run(ctx) {
      const text = await gatherInput(ctx);
      const lines = (text.match(/\n/g)||[]).length;
      const words = (text.match(/\S+/g) || []).length;
      const bytes = Buffer.byteLength(text);
      const only = ctx.flags.l || ctx.flags.w || ctx.flags.c;
      const parts = [];
      if (!only || ctx.flags.l) parts.push(String(lines).padStart(6));
      if (!only || ctx.flags.w) parts.push(String(words).padStart(6));
      if (!only || ctx.flags.c) parts.push(String(bytes).padStart(6));
      ctx.stdout.write(parts.join(' ').trim() + '\n');
      return 0;
    }
  },

  {
    name: 'grep',
    summary: 'search literal text (not regular expressions)' ,
    usage: 'grep [-F] [-i] [-v] [-n] [-c] TEXT [file...]',
    parse: {},
    async run(ctx) {
      if (!ctx.args.length) return fail(ctx, 'grep', 'usage: grep PATTERN [file...]');
      const pattern = ctx.args[0];
      ctx.args = ctx.args.slice(1); // remaining are files
      if(pattern.length>256)return fail(ctx,'grep','pattern too long',2);
      if(Object.keys(ctx.flags).some(k=>!['F','i','v','n','c'].includes(k)))return fail(ctx,'grep','only -F -i -v -n -c are supported; patterns are literal',2);
      const invert=!!ctx.flags.v, needle=ctx.flags.i?pattern.toLowerCase():pattern;
      const lines=toLines(await gatherInput(ctx));let matched=0;const out=[];
      for(let idx=0;idx<lines.length;idx++){
        const line=lines[idx],hit=(ctx.flags.i?line.toLowerCase():line).includes(needle);
        if(hit!==invert){matched++;if(!ctx.flags.c)out.push((ctx.flags.n?`${idx+1}:`:'')+line);}
      }
      if(ctx.flags.c)ctx.stdout.write(matched+'\n');else if(out.length)ctx.stdout.write(out.join('\n')+'\n');
      return matched ? 0 : 1;
    }
  },

  {
    name: 'sort',
    summary: 'sort lines of text',
    usage: 'sort [-r] [-n] [-u] [file...]',
    async run(ctx) {
      let lines = toLines(await gatherInput(ctx));
      lines.sort(ctx.flags.n
        ? (a, b) => parseFloat(a) - parseFloat(b)
        : (a, b) => a.localeCompare(b));
      if (ctx.flags.r) lines.reverse();
      if (ctx.flags.u) lines = [...new Set(lines)];
      ctx.stdout.write(lines.join('\n') + (lines.length ? '\n' : ''));
      return 0;
    }
  },

  {
    name: 'uniq',
    summary: 'report or omit repeated adjacent lines',
    usage: 'uniq [-c] [file...]',
    async run(ctx) {
      const lines = toLines(await gatherInput(ctx));
      const out = [];
      let prev = null, count = 0;
      const flush = () => { if (prev !== null) out.push(ctx.flags.c ? `${String(count).padStart(4)} ${prev}` : prev); };
      for (const l of lines) {
        if (l === prev) count++;
        else { flush(); prev = l; count = 1; }
      }
      flush();
      ctx.stdout.write(out.join('\n') + (out.length ? '\n' : ''));
      return 0;
    }
  },

  {
    name: 'rev',
    summary: 'reverse characters of each line',
    usage: 'rev [file...]',
    async run(ctx) {
      const lines = toLines(await gatherInput(ctx)).map((l) => [...l].reverse().join(''));
      ctx.stdout.write(lines.join('\n') + (lines.length ? '\n' : ''));
      return 0;
    }
  },

  {
    name: 'tr',
    summary: 'translate or delete characters',
    usage: 'tr [-d] SET1 [SET2]',
    async run(ctx) {
      const text = await gatherInput_stdinOnly(ctx);
      if (ctx.flags.d) {
        const set = new Set(ctx.args[0] || '');
        ctx.stdout.write([...text].filter((c) => !set.has(c)).join(''));
        return 0;
      }
      const from = ctx.args[0] || '', to = ctx.args[1] || '';
      const map = Object.create(null);
      for (let i = 0; i < from.length; i++) map[from[i]] = to[i] != null ? to[i] : to[to.length - 1] || '';
      ctx.stdout.write([...text].map((c) => (c in map ? map[c] : c)).join(''));
      return 0;
    }
  },

  {
    name: 'seq',
    summary: 'print a sequence of numbers',
    usage: 'seq [first [incr]] last',
    async run(ctx) {
      const nums = ctx.args.map(Number);
      let first = 1, incr = 1, last;
      if (nums.length === 1) [last] = nums;
      else if (nums.length === 2) [first, last] = nums;
      else if (nums.length >= 3) [first, incr, last] = nums;
      else return fail(ctx, 'seq', 'usage: seq [first [incr]] last');
      if (![first, incr, last].every(Number.isFinite)) return fail(ctx, 'seq', 'arguments must be finite numbers');
      const max = (ctx.limits && ctx.limits.seqItems) || 1000000;
      const count = incr === 0 ? 0 : Math.floor((last - first) / incr) + 1;
      if (count > max) return fail(ctx, 'seq', `sequence of ${count} items exceeds the limit of ${max}`);
      if(incr===0)return fail(ctx,'seq','increment must be nonzero',2);
      if(nums.length>3)return fail(ctx,'seq','too many arguments',2);
      let out=[];for(let k=0;k<Math.max(0,count);k++){
        out.push(first+k*incr);
        if(out.length>=256){ctx.stdout.write(out.join('\n')+'\n');out=[];await ctx.yield();if(ctx.signal.aborted)throw new Error('interrupted');}
      }
      if(out.length)ctx.stdout.write(out.join('\n')+'\n');
      return 0;
    }
  },

  { name: 'true', summary: 'do nothing, successfully', usage: 'true', async run() { return 0; } },
  { name: 'false', summary: 'do nothing, unsuccessfully', usage: 'false', async run() { return 1; } },

  {
    name: 'sleep',
    summary: 'delay for a number of seconds',
    usage: 'sleep SECONDS',
    async run(ctx) {
      const secs=Number(ctx.args[0]);
      if(ctx.args.length!==1||!Number.isFinite(secs)||secs<0||secs>60)return fail(ctx,'sleep','seconds must be 0..60',2);
      if(ctx.signal.aborted)throw new Error('interrupted');
      await new Promise((res, rej) => {
        const abort=()=>{clearTimeout(t);rej(new Error('interrupted'));};
        const t=setTimeout(()=>{ctx.signal.removeEventListener('abort',abort);res();},secs*1000);
        ctx.signal.addEventListener('abort',abort,{once:true});
      });
      return 0;
    }
  }
];

// tr should only read stdin (its args are the SETs, not files)
async function gatherInput_stdinOnly(ctx) {
  if (ctx.stdin && !ctx.stdin.isTTY) return ctx.stdin.readAll();
  let out = '';
  for (;;) { const l = await ctx.stdin.readLine(''); if (l === null) break; out += l + '\n';if(out.length>ctx.limits.captureBytes)throw new Error('input capture limit'); }
  return out;
}
