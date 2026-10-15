// @ts-nocheck
import { fail, padRight, padLeft, humanSize } from './util.js';

/** SPIRAL built-ins — filesystem navigation and manipulation. */



function fmtMode(node) {
  const t = node.type === 'dir' ? 'd' : '-';
  const m = node.mode || (node.type === 'dir' ? 0o755 : 0o644);
  const bit = (b, ch) => (m & b ? ch : '-');
  return t +
    bit(0o400, 'r') + bit(0o200, 'w') + bit(0o100, 'x') +
    bit(0o040, 'r') + bit(0o020, 'w') + bit(0o010, 'x') +
    bit(0o004, 'r') + bit(0o002, 'w') + bit(0o001, 'x');
}

function colorName(entry) {
  if (entry.type === 'dir') return `\x1b[1;38;5;75m${entry.name}\x1b[0m`;
  if (entry.mode & 0o100) return `\x1b[1;38;5;114m${entry.name}\x1b[0m`;
  return entry.name;
}

export default [
  {
    name: 'pwd',
    summary: 'print working directory',
    usage: 'pwd',
    async run(ctx) { ctx.stdout.write(ctx.session.cwd + '\n'); return 0; }
  },

  {
    name: 'cd',
    summary: 'change the working directory',
    usage: 'cd [dir]',
    async run(ctx) {
      const target = ctx.args[0] || ctx.env.get('HOME') || '/';
      const abs = ctx.resolve(target === '-' ? (ctx.env.get('OLDPWD') || ctx.session.cwd) : target);
      if (!ctx.vfs.exists(abs)) return fail(ctx, 'cd', `no such file or directory: ${target}`);
      if (!ctx.vfs.isDir(abs)) return fail(ctx, 'cd', `not a directory: ${target}`);
      ctx.env.set('OLDPWD', ctx.session.cwd);
      ctx.chdir(abs);
      return 0;
    }
  },

  {
    name: 'ls',
    summary: 'list directory contents',
    usage: 'ls [-l] [-a] [-h] [path...]',
    aka: ['dir'],
    async run(ctx) {
      const paths = ctx.args.length ? ctx.args : ['.'];
      let rc = 0;
      const many = paths.length > 1;
      for (const p of paths) {
        const abs = ctx.resolve(p);
        if (!ctx.vfs.exists(abs)) { rc = fail(ctx, 'ls', `cannot access '${p}': no such file or directory`, 2); continue; }
        if (many) ctx.stdout.write(`\n${p}:\n`);
        if (!ctx.vfs.isDir(abs)) { ctx.stdout.write(p + '\n'); continue; }
        let entries = ctx.vfs.readdir(abs);
        if (!ctx.flags.a) entries = entries.filter((e) => !e.name.startsWith('.'));
        if (ctx.flags.l) {
          for (const e of entries) {
            const size = ctx.flags.h ? humanSize(e.size) : String(e.size);
            const when = new Date(e.mtime).toISOString().slice(0, 16).replace('T', ' ');
            ctx.stdout.write(
              `${fmtMode(e)}  ${padLeft(size, 7)}  \x1b[2m${when}\x1b[0m  ${colorName(e)}\n`);
          }
        } else {
          const cols = Math.max(1, Math.floor((ctx.cols || 80) / 22));
          let line = '';
          entries.forEach((e, i) => {
            line += padRight(colorName(e), 22 + (colorName(e).length - e.name.length));
            if ((i + 1) % cols === 0) { ctx.stdout.write(line.trimEnd() + '\n'); line = ''; }
          });
          if (line.trim()) ctx.stdout.write(line.trimEnd() + '\n');
        }
      }
      return rc;
    }
  },

  {
    name: 'tree',
    summary: 'list contents recursively as a tree',
    usage: 'tree [path]',
    async run(ctx) {
      const root = ctx.resolve(ctx.args[0] || '.');
      if (!ctx.vfs.isDir(root)) return fail(ctx, 'tree', `not a directory: ${ctx.args[0] || '.'}`);
      let dirs = 0, files = 0;
      const walk = (abs, prefix) => {
        const entries = ctx.vfs.readdir(abs).filter((e) => !e.name.startsWith('.'));
        entries.forEach((e, i) => {
          const last = i === entries.length - 1;
          ctx.stdout.write(prefix + (last ? '└── ' : '├── ') + colorName(e) + '\n');
          if (e.type === 'dir') { dirs++; walk(abs + '/' + e.name, prefix + (last ? '    ' : '│   ')); }
          else files++;
        });
      };
      ctx.stdout.write(`\x1b[1;38;5;75m${ctx.args[0] || '.'}\x1b[0m\n`);
      walk(root, '');
      ctx.stdout.write(`\n${dirs} directories, ${files} files\n`);
      return 0;
    }
  },

  {
    name: 'find',
    summary: 'search for files by name',
    usage: 'find [path] [-name PATTERN] [-type f|d]',
    parse: { valued: ['name', 'type'] },
    async run(ctx) {
      if(ctx.args.length>1)return fail(ctx,'find','one root path is supported',2);
      const root=ctx.resolve(ctx.args[0]||'.'),pattern=ctx.flags.name===undefined?null:String(ctx.flags.name),kind=ctx.flags.type;
      if(pattern!==null&&pattern.length>255)return fail(ctx,'find','pattern exceeds 255 characters',2);
      if(kind!==undefined&&!['f','d'].includes(kind))return fail(ctx,'find','type must be f or d',2);
      // Wildcards use bounded state, never a user-generated regular expression.
      const match=(name)=>{if(pattern===null)return true;let i=0,j=0,star=-1,mark=0;while(i<name.length){if(j<pattern.length&&(pattern[j]==='?'||pattern[j]===name[i])){i++;j++;}else if(pattern[j]==='*'){star=j++;mark=i;}else if(star>=0){j=star+1;i=++mark;}else return false;}while(pattern[j]==='*')j++;return j===pattern.length;};
      const results=[],pending=[root];let visited=0;
      while(pending.length){const abs=pending.pop(),info=ctx.vfs.stat(abs),name=abs.split('/').pop()||'/';
        if(match(name)&&(!kind||(kind==='d'?info.type==='dir':info.type==='file')))results.push(abs);
        if(info.type==='dir')for(const e of ctx.vfs.readdir(abs).reverse())pending.push(abs==='/'?'/'+e.name:abs+'/'+e.name);
        if(++visited%128===0){await ctx.yield();if(ctx.signal.aborted)throw new Error('interrupted');}
      }
      ctx.stdout.write(results.join('\n')+(results.length?'\n':''));
      return 0;
    }
  },

  {
    name: 'mkdir',
    summary: 'make directories',
    usage: 'mkdir [-p] dir...',
    async run(ctx) {
      if (!ctx.args.length) return fail(ctx, 'mkdir', 'missing operand');
      let rc = 0;
      for (const a of ctx.args) {
        try { ctx.vfs.mkdir(ctx.resolve(a), { recursive: !!ctx.flags.p }); }
        catch (e) { rc = fail(ctx, 'mkdir', e.message); }
      }
      return rc;
    }
  },

  {
    name: 'rmdir',
    summary: 'remove empty directories',
    usage: 'rmdir dir...',
    async run(ctx) {
      let rc = 0;
      for (const a of ctx.args) {
        const abs = ctx.resolve(a);
        if (!ctx.vfs.isDir(abs)) { rc = fail(ctx, 'rmdir', `not a directory: ${a}`); continue; }
        try { ctx.vfs.remove(abs, { recursive: false }); }
        catch (e) { rc = fail(ctx, 'rmdir', e.message); }
      }
      return rc;
    }
  },

  {
    name: 'rm',
    summary: 'remove files or directories',
    usage: 'rm [-r] [-f] path...',
    async run(ctx) {
      if (!ctx.args.length && !ctx.flags.f) return fail(ctx, 'rm', 'missing operand');
      let rc = 0;
      for (const a of ctx.args) {
        const abs = ctx.resolve(a);
        if (!ctx.vfs.exists(abs)) { if (!ctx.flags.f) rc = fail(ctx, 'rm', `cannot remove '${a}': no such file or directory`); continue; }
        try{if(ctx.vfs.isDir(abs)&&!ctx.flags.r)throw new Error('directory removal requires -r');ctx.vfs.remove(abs,{recursive:!!ctx.flags.r});}
        catch (e) { rc = fail(ctx, 'rm', e.message); }
      }
      return rc;
    }
  },

  {
    name: 'touch',
    summary: 'create empty files / update mtime',
    usage: 'touch file...',
    async run(ctx) {
      if (!ctx.args.length) return fail(ctx, 'touch', 'missing file operand');
      for (const a of ctx.args) {
        const abs = ctx.resolve(a);
        if (ctx.vfs.exists(abs)) { const n=ctx.vfs._walk(abs);n.mtime=Date.now();ctx.vfs.onMutate(); }
        else ctx.vfs.writeFile(abs, '');
      }
      return 0;
    }
  },

  {
    name: 'cp',
    summary: 'copy files',
    usage: 'cp src dst',
    async run(ctx) {
      if(ctx.args.length!==2)return fail(ctx,'cp','usage: cp src dst (exactly two paths)',2);
      const src = ctx.resolve(ctx.args[0]);
      const data = ctx.vfs.readFile(src);
      let dst = ctx.resolve(ctx.args[1]);
      if (ctx.vfs.isDir(dst)) dst = dst + '/' + ctx.args[0].split('/').pop();
      ctx.vfs.writeFile(dst, data);
      return 0;
    }
  },

  {
    name: 'mv',
    summary: 'move or rename files',
    usage: 'mv src dst',
    async run(ctx) {
      if(ctx.args.length!==2)return fail(ctx,'mv','usage: mv src dst (exactly two paths)',2);
      try { ctx.vfs.move(ctx.resolve(ctx.args[0]), ctx.resolve(ctx.args[1])); }
      catch (e) { return fail(ctx, 'mv', e.message); }
      return 0;
    }
  },

  {
    name: 'stat',
    summary: 'display file status',
    usage: 'stat file...',
    async run(ctx) {
      if (!ctx.args.length) return fail(ctx, 'stat', 'missing operand');
      let rc = 0;
      for (const a of ctx.args) {
        try {
          const s = ctx.vfs.stat(ctx.resolve(a));
          ctx.stdout.write(
            `  File: ${a}\n  Type: ${s.type}\n  Size: ${s.size}\n  Mode: ${(s.mode).toString(8)}\n  Modify: ${new Date(s.mtime).toISOString()}\n`);
        } catch (e) { rc = fail(ctx, 'stat', e.message); }
      }
      return rc;
    }
  },

  {
    name: 'write',
    summary: 'write text into a file (write FILE text...)',
    usage: 'write [-a] FILE text...',
    async run(ctx) {
      if (ctx.args.length < 1) return fail(ctx, 'write', 'usage: write [-a] FILE text...');
      const file = ctx.resolve(ctx.args[0]);
      const text = ctx.args.slice(1).join(' ') + '\n';
      ctx.vfs.writeFile(file, text, { append: !!ctx.flags.a });
      return 0;
    }
  }
];
