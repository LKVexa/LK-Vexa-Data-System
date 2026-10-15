import {compileVcmd, VCMD_LIMITS} from '../../vcmd/language.js';
// @ts-nocheck
import { EventEmitter } from '../events.js';
import { VFS, resolvePath, basename, dirname } from './vfs.js';
import { CommandRegistry, parseFlags } from './registry.js';
import { Environment } from './env.js';
import { parse, expandWord } from './pipeline.js';
import { LineReader } from './line-reader.js';
import builtins from './commands/index.js';

/**
 * SPIRAL — Kernel
 * ---------------------------------------------------------------------------
 * The session supervisor and shell interpreter. Each session is an independent
 * REPL: it prompts, reads a line through the interactive line reader, parses it
 * into a pipeline plan, executes the plan against the command registry and the
 * virtual filesystem, and streams the result back as ANSI/VT bytes.
 *
 * Emits:
 *   'data'  { sessionId, chunk }          output bytes destined for the renderer
 *   'exit'  { sessionId, code, reason }   a session ended (exactly once per session)
 *
 * VWS candidate repairs (series findings F04/F05/F09/F10/F17, packs I028-I030, I033):
 *   - geometry is validated before it is stored or reaches a command context
 *   - closeSession settles the active line reader, aborts the running pipeline
 *     and emits exactly one 'exit'
 *   - openSession({deferStart:true}) + startSession() form an explicit open
 *     barrier so no output can precede the consumer's registration
 *   - pipeline captures, input lines and history are bounded
 */


const defer = (fn) => setTimeout(fn, 0);








let SESSION_SEQ = 0;

const GEOMETRY = Object.freeze({ minCols: 2, maxCols: 400, minRows: 1, maxRows: 200 });
const DEFAULT_LIMITS = Object.freeze({
  captureBytes: 128 * 1024, // per pipeline stage capture (string length, UTF-16 units as a proxy)
  lineChars: 4096,               // interactive input line
  historyEntries: 1000,
  seqItems: 20000,             // `seq` element count
  regexChars: 256,               // user-supplied RegExp source length
  regexLineChars: 16384          // longest line a user RegExp is applied to
});

function validGeometry(cols, rows) {
  return Number.isInteger(cols) && Number.isInteger(rows) &&
    cols >= GEOMETRY.minCols && cols <= GEOMETRY.maxCols && rows >= GEOMETRY.minRows && rows <= GEOMETRY.maxRows;
}

class CaptureLimitError extends Error {
  constructor(limit) { super(`pipeline capture limit exceeded (${limit} chars)`); this.code = 'ECAPTURE'; }
}

// LK/Vexa house prompt: cyan identity, ice path, cyan chevron.
// palette idx 44 = cyan core, 252 = ice, 245 = graphite text.
const DEFAULT_PS1 = '\x1b[38;5;44m\\u\x1b[38;5;245m@\\h\x1b[0m \x1b[38;5;252m\\w\x1b[0m \x1b[38;5;44m›\x1b[0m ';

/* --------------------------------------------------------------------------
 * stdin adapters
 * ------------------------------------------------------------------------ */

class StaticInput {
  constructor(text) {
    this.isTTY = false;
    this._text = String(text || '');
    this._lines = this._text.length ? this._text.split('\n') : [];
    // A trailing newline yields an empty final element; drop it.
    if (this._lines.length && this._lines[this._lines.length - 1] === '') this._lines.pop();
    this._i = 0;
    this._consumed = false;
  }
  readAll() { this._consumed = true; return this._text; }
  read() { if (this._consumed) return ''; this._consumed = true; return this._text; }
  async readLine() { return this._i < this._lines.length ? this._lines[this._i++] : null; }
}

class InteractiveInput {
  constructor(session) { this.isTTY = true; this.session = session; }
  read() { return ''; }
  readAll() { return ''; }
  async readLine(prompt = '') {
    const v = await this.session.readLine(prompt, { history: false });
    if (v === null) return null;
    if (v && typeof v === 'object' && v.aborted) return null;
    return v;
  }
}

/* --------------------------------------------------------------------------
 * kernel
 * ------------------------------------------------------------------------ */

class SpiralKernel extends EventEmitter {
  /**
   * @param {object} [o]
   * @param {object} [o.hostBridge]      capability callbacks (browser pane, version, fabricPolicy…)
   * @param {object} [o.limits]          overrides for DEFAULT_LIMITS
   * @param {object} [o.vfsLimits]       { maxBytes, maxNodes } — unlimited when omitted (LOCAL)
   * @param {(d:object)=>boolean} [o.commandFilter]  descriptors returning false are not installed
   * @param {object} [o.baseEnv]         extra/override base environment (already allowlisted by the caller)
   */
  constructor({ hostBridge = {}, limits = {}, vfsLimits = {}, commandFilter = null, baseEnv = {} } = {}) {
    super();
    this.host = hostBridge;
    this.limits = Object.assign({}, DEFAULT_LIMITS, limits);
    this.vfs = new VFS(vfsLimits);
    this.registry = new CommandRegistry();
    this.registry.install(commandFilter ? builtins.filter((d) => commandFilter(d)) : builtins);
    this.sessions = new Map();
    this.tasks = new Set();
    this.baseEnv = new Environment({
      SHELL: 'spiral',
      TERM: 'hermit-256color',
      HOME: '/home/operator',
      USER: 'operator',
      HOST: 'hermit',
      PS1: DEFAULT_PS1,
      PATH: '/bin:/usr/bin',
      '?': '0',
      ...baseEnv
    });
  }

  /* ---- session lifecycle ---------------------------------------------- */

  openSession(opts = {}) {
    const cols = opts.cols === undefined ? 80 : opts.cols;
    const rows = opts.rows === undefined ? 24 : opts.rows;
    if (!validGeometry(cols, rows)) throw new RangeError(`invalid geometry ${String(cols).slice(0, 12)}x${String(rows).slice(0, 12)}`);
    if(this.sessions.size>=8)throw new Error('maximum 8 sessions');
    const id = `s${++SESSION_SEQ}`;
    const env = this.baseEnv.child();
    const session = {
      id,
      env,
      cwd: env.get('HOME'),
      cols,
      rows,
      history: [],
      aliases: Object.create(null), // user-chosen names: `constructor`, `__proto__`, `toString` must be ordinary keys
      alive: true,
      started: false,  // REPL launched (open barrier released)
      exited: false,   // 'exit' already emitted
      pending: null,   // active LineReader, if any
      running: null,   // AbortController while a command runs
      readLine: null   // bound below
    };
    session.readLine = (prompt, o) => this._readLine(session, prompt, o);
    this.sessions.set(id, session);
    // Open barrier: with deferStart the REPL (banner, prompt) is not launched until the
    // consumer has registered the session and calls startSession().
    if (!opts.deferStart) queueMicrotask(() => this.startSession(id));
    return { sessionId: id, cols: session.cols, rows: session.rows };
  }

  startSession(sessionId) {
    const s = this.sessions.get(sessionId);
    if (!s || s.started || !s.alive) return false;
    s.started = true;
    this._track(this._repl(s));
    return true;
  }

  _track(task) {
    this.tasks.add(task);
    task.then(()=>this.tasks.delete(task),()=>this.tasks.delete(task));
    return task;
  }

  /** Close/cancel sessions first; this waits for cooperative work before final save. */
  async settle() { while(this.tasks.size) await Promise.allSettled([...this.tasks]); }

  /** The single exit path. Idempotent: socket close, worker stop and REPL end may all race here. */
  _finishSession(session, code, reason) {
    if (session.exited) return false;
    session.exited = true;
    session.alive = false;
    this.sessions.delete(session.id);
    const reader = session.pending;
    session.pending = null;
    if (reader) reader._finish(null);                       // settle an active prompt (F04)
    if (session.running) { try { session.running.abort('SESSION_CLOSED'); } catch { /* noop */ } }
    this.emit('exit', { sessionId: session.id, code: code | 0, reason: reason || 'closed' });
    return true;
  }

  closeSession(sessionId, reason = 'closed') {
    const s = this.sessions.get(sessionId);
    if (!s) return false;
    return this._finishSession(s, 0, reason);
  }

  resize(sessionId, cols, rows) {
    const s = this.sessions.get(sessionId);
    if (!s || !s.alive) return false;
    if (!validGeometry(cols, rows)) return false;          // F05: never store unvalidated geometry
    s.cols = cols; s.rows = rows;
    return true;
  }

  /* ---- io routing ------------------------------------------------------ */

  write(sessionId, data) {
    const s = this.sessions.get(sessionId);
    if (!s || !s.alive || typeof data !== 'string') return;
    if (s.pending) {
      const rest = s.pending.feed(data);
      if (rest) this._typeahead(s, rest);
      return;
    }
    if (s.running && data.indexOf('\x03') !== -1) { s.typeahead = ''; s.running.abort('SIGINT'); return; }
    this._typeahead(s, data);
  }

  /** Bounded type-ahead: input that arrives between prompts is kept for the next reader, never silently reordered. */
  _typeahead(session, data) {
    const room = this.limits.lineChars * 2 - (session.typeahead || '').length;
    if (data.length > room) {
      session.typeahead = '';
      this._emit(session, '\r\n[input queue limit: session closed; queued commands were not executed]\r\n');
      this._finishSession(session, 2, 'INPUT_QUEUE_LIMIT');
      return;
    }
    session.typeahead = (session.typeahead || '') + data;
  }

  signal(sessionId, signal) {
    const s = this.sessions.get(sessionId);
    if (!s) return;
    if (signal !== 'SIGINT' && signal !== 'INT') return;
    s.typeahead = '';
    if (s.running) s.running.abort('SIGINT');
    else if (s.pending) s.pending.feed('\x03');
  }

  _emit(session, chunk) {
    if (session.alive) this.emit('data', { sessionId: session.id, chunk });
  }

  /* ---- REPL ------------------------------------------------------------ */

  async _repl(session) {
    this._emit(session, this._banner(session));
    while (session.alive) {
      const prompt = this._buildPrompt(session);
      let line;
      try {
        line = await session.readLine(prompt, { history: true });
      } catch {
        break;
      }
      if (line === null) { this._emit(session, 'logout\r\n'); break; } // Ctrl-D
      if (line && typeof line === 'object' && line.aborted) continue;   // Ctrl-C
      const text = line;
      if (!text.trim()) continue;
      if (session.history[session.history.length - 1] !== text) {
        session.history.push(text);
        if (session.history.length > this.limits.historyEntries) session.history.splice(0, session.history.length - this.limits.historyEntries);
      }
      try {
        await this._executeLine(session, text);
      } catch (err) {
        session.env.set('?', '2');
        this._emit(session, `\x1b[31mspiral: ${err.message}\x1b[0m\r\n`);
      }
      const executionCode = Number(session.env.get('?')) | 0;
      let persistence = this.host.afterCommand ? 'committed' : 'not-configured';
      if (this.host.afterCommand) {
        try { await this.host.afterCommand(); }
        catch (err) {
          persistence = 'failed';
          if (executionCode === 0) session.env.set('?', '74');
          this._emit(session, `\x1b[31mstorage: ${err.message}; changes are not confirmed saved\x1b[0m\r\n`);
        }
      }
      // 74 denotes a failed persistence commit when virtual execution otherwise succeeded.
      this.emit('command-complete', { sessionId: session.id, code: Number(session.env.get('?')) | 0, executionCode, persistence });
    }
    this._finishSession(session, session._exitCode || 0, 'logout');
  }

  _readLine(session, prompt, opts = {}) {
    const reader = new LineReader({
      write: (s) => this._emit(session, s),
      prompt: prompt || '',
      history: opts.history ? session.history : [],
      maxChars: this.limits.lineChars,
      completer: (buf, cur) => this._complete(session, buf, cur)
    });
    if (!session.alive) return Promise.resolve(null);
    session.pending = reader;
    if (session.typeahead) {
      queueMicrotask(() => {
        if (session.pending !== reader || !session.typeahead) return;
        const data = session.typeahead; session.typeahead = '';
        const rest = reader.feed(data);
        if (rest) this._typeahead(session, rest);
      });
    }
    return reader.start().then((v) => {
      if (session.pending === reader) session.pending = null;
      return v;
    });
  }

  /* ---- prompt ---------------------------------------------------------- */

  _buildPrompt(session) {
    const raw = session.env.get('PS1') || DEFAULT_PS1;
    return this._expandPrompt(session, raw);
  }

  _expandPrompt(session, ps1) {
    const home = session.env.get('HOME');
    let cwd = session.cwd;
    if (home && (cwd === home || cwd.startsWith(home + '/'))) cwd = '~' + cwd.slice(home.length);
    return ps1
      .replace(/\\e/g, '\x1b')
      .replace(/\\u/g, session.env.get('USER') || 'operator')
      .replace(/\\h/g, session.env.get('HOST') || 'hermit')
      .replace(/\\w/g, cwd)
      .replace(/\\W/g, basename(cwd) || '/')
      .replace(/\\n/g, '\r\n')
      .replace(/\\\$/g, session.env.get('USER') === 'root' ? '#' : '$');
  }

  _banner(session) {
    const v = (this.host.version && this.host.version()) || '1.0.0';
    const C = '\x1b[38;5;44m', G = '\x1b[38;5;240m', I = '\x1b[38;5;252m', D = '\x1b[2m', R = '\x1b[0m', B = '\x1b[1m';
    // Hub-and-spoke mark in text (graphite nodes, cyan core), mirroring the LK/Vexa logo.
    return [
      '',
      `  ${C}⌜${R}                                                        ${C}⌝${R}`,
      `        ${G}▪${R}      ${G}▪${R}      ${G}▪${R}        ${B}${I}HERMIT${R}  ${G}/${R}  ${C}${B}JA21${R}`,
      `          ${G}╲  ┆  ╱${R}              ${D}virtual server · hermit.vws.v2 · v${v}${R}`,
      `        ${G}▪ ┄┄ ${R}${C}◆${R}${G} ┄┄ ▪${R}            ${D}SPIRAL sandbox · no host command execution${R}`,
      `          ${G}╱  ┆  ╲${R}              ${D}JA21 command console · virtual filesystem${R}`,
      `        ${G}▪${R}      ${G}▪${R}      ${G}▪${R}`,
      `  ${C}⌞${R}                                                        ${C}⌟${R}`,
      '',
      `  ${D}Type${R} ${C}help${R} ${D}·${R} ${C}disk${R} ${D}·${R} ${C}image verify${R} ${D}·${R} ${C}poweroff${R} ${D}keeps this volume${R}`,
      '', ''
    ].join('\r\n');
  }

  /* ---- status (for the renderer status bar / hub widget) --------------- */

  status(sessionId) {
    const s = this.sessions.get(sessionId);
    if (!s) return null;
    const nodes = this.host.fabricRoster ? this.host.fabricRoster() : [];
    const home = s.env.get('HOME');
    let cwd = s.cwd;
    if (home && (cwd === home || cwd.startsWith(home + '/'))) cwd = '~' + cwd.slice(home.length);
    return {
      sessionId: s.id,
      cwd,
      cols: s.cols,
      rows: s.rows,
      busy: !!s.running,
      user: s.env.get('USER'),
      host: s.env.get('HOST'),
      dfRoot: '/df',
      photon: { bound: !!this.host.photon, url: null, token: false },
      capabilities: this.host.capabilities ? this.host.capabilities() : {hostShell:false},
      nodes,
      commands: this.registry.list().length,
      ram: this.vfs.usage(),
      disk: this.host.disk ? this.host.disk() : null,
      profile: (this.host.profile && this.host.profile()) || 'VIRTUAL_SANDBOX'
    };
  }

  /* ---- execution ------------------------------------------------------- */

  async _executeLine(session, line, options = {}) {
    const segments = options.segments || parse(line);
    const controller = options.controller || new AbortController();
    const previousController = session.running;
    session.running = controller;
    const execution = {...options, controller};
    let lastCode = 0;
    try {
      for (const seg of segments) {
        if (controller.signal.aborted) { lastCode = 130; break; }
        if (seg.op === '&&' && lastCode !== 0) continue;
        if (seg.op === '||' && lastCode === 0) continue;
        if (this.host.flow) {
          await new Promise(res => defer(res));
          if (this.host.flow.paused()) await new Promise(res => this.host.flow.onDrain(res));
        }
        if (!session.alive || controller.signal.aborted) { lastCode = 130; break; }
        try { lastCode = await this._runPipeline(session, seg.pipeline, execution); }
        catch (error) {
          // Batch operational errors are line failures, not a bypass of @on-error.
          // Preserve the existing throwing API for direct/interactive invalid operations.
          if (!options.batch) throw error;
          options.stderr.write(`spiral: ${error.message}\n`);
          lastCode = controller.signal.aborted ? 130 : 2;
        }
        session.env.set('?', String(lastCode));
      }
      if (controller.signal.aborted) lastCode = 130;
      session.env.set('?', String(lastCode));
      return lastCode;
    } finally { session.running = previousController; }
  }

  async _runPipeline(session, pipeline, options = {}) {
    if (!pipeline.length) return 0;
    const controller = options.controller || new AbortController();
    const previousController = session.running;
    session.running = controller;
    let pipeInput = '';
    let code = 0;

    try {
      for (let k = 0; k < pipeline.length; k++) {
        if(controller.signal.aborted){code=130;break;}
        const raw = pipeline[k];
        const cmd = {argv: raw.argv.map(t=>expandWord(t,session.env)), redirects: raw.redirects.map(r=>({...r,target:expandWord(r.target,session.env)}))};
        const isLast = k === pipeline.length - 1;
        let argv = cmd.argv.slice();
        let name = argv.shift();
        if (name == null) continue;

        // alias expansion (guarded against recursion)
        const seen = new Set();
        while (name != null && session.aliases[name] && !seen.has(name)) {
          seen.add(name);
          const plans = parse(session.aliases[name], session.env);
          if(plans.length!==1||plans[0].pipeline.length!==1||plans[0].pipeline[0].redirects.length)throw new Error('alias must be a single simple command');
          const exp = plans[0].pipeline[0].argv.slice();
          name = exp.shift();
          argv = exp.concat(argv);
        }

        const descriptor = this.registry.resolve(name);
        if (!descriptor) {
          (options.stderr || {write: text => this._emit(session, text.replace(/\r?\n/g, '\r\n'))}).write(`spiral: ${name}: command not found\n`);
          code = 127;
          break;
        }

        if(options.batch && (['run','exit','poweroff','reboot'].includes(descriptor.name) ||
          (descriptor.name==='vcmd' && !['help','corpus'].includes(argv[0]||'help')))) {
          (options.stderr || {write:s=>this._emit(session,s)}).write('vcmd: nested execution/session-control refused; use @include\n');code=2;break;
        }
        // Host-only descriptors must never apply SPIRAL redirections.
        if (descriptor.hostOnly && cmd.redirects.length) throw new Error('host-only command: redirection refused');

        // redirects
        const outRe = cmd.redirects.find((r) => r.type === 'out' || r.type === 'append');
        const inRe = cmd.redirects.find((r) => r.type === 'in');

        // stdin
        let stdin;
        if (inRe) {
          const p = resolvePath(session.cwd, inRe.target);
          stdin = new StaticInput(this.vfs.readFile(p));
        } else if (k > 0) {
          stdin = new StaticInput(pipeInput);
        } else if (pipeline.length === 1 && !options.batch) {
          stdin = new InteractiveInput(session);
        } else {
          stdin = new StaticInput('');
        }

        // stdout
        let capture = '';
        const limit = this.limits.captureBytes;
        const toCapture = { write: (s) => {
          s = String(s);
          if (capture.length + s.length > limit) { controller.abort('CAPTURE_LIMIT'); throw new CaptureLimitError(limit); }
          capture += s;
        } };
        let emitted=0;
        const toTerminal = { write: (s) => {s=String(s);emitted+=new TextEncoder().encode(s).length;if(emitted>(['vcmd','run'].includes(descriptor.name)?VCMD_LIMITS.outputBytes:262144))throw new CaptureLimitError(262144);if(options.stdout)options.stdout.write(s);else this._emit(session,s.replace(/\r?\n/g,'\r\n'));} };
        const stdout = outRe ? toCapture : (isLast ? toTerminal : toCapture);
        const stderr = options.stderr || { write: (s) => this._emit(session, String(s).replace(/\r?\n/g, '\r\n')) };

        let ctx;try{ctx=this._makeContext(session,descriptor,argv,{stdin,stdout,stderr,signal:controller.signal,controller});}catch(e){stderr.write(name+': '+e.message+'\n');code=2;break;}

        try {
          const rc = await descriptor.run(ctx);
          code = Number.isInteger(rc) ? rc : 0;
        } catch (err) {
          if (err && err.code === 'ECAPTURE') { stderr.write(`\x1b[31m${name}: ${err.message}\x1b[0m\n`); code = 1; break; }
          if (controller.signal.aborted) { code = 130; break; }
          stderr.write(`\x1b[31m${name}: ${err.message}\x1b[0m\n`);
          code = 1;
        }

        if (controller.signal.aborted) { code = controller.signal.reason === 'CAPTURE_LIMIT' ? 1 : 130; break; }

        if (outRe) {
          const p = resolvePath(session.cwd, outRe.target);
          this.vfs.writeFile(p, capture, { append: outRe.type === 'append' });
        } else if (!isLast) {
          pipeInput = capture;
        }
      }
    } finally {
      session.running = previousController;
      // A producer that overflowed the bounded output stage has now stopped: output may flow again
      // (the prompt that follows must not be swallowed by the gap).
      if (this.host.flow && typeof this.host.flow.clearOverflow === 'function') this.host.flow.clearOverflow();
    }
    return code;
  }

  /** Run a fully read/parsed VCMD graph with shared cancellation, finite work and no native process adapter. */
  executeVcmd(session, mode, target, options = {}) { return this._track(this._executeVcmd(session,mode,target,options)); }

  async _executeVcmd(session, mode, target, options = {}) {
    const controller=options.controller || new AbortController();
    const rawOut=options.stdout || {write:s=>this._emit(session,s.replace(/\r?\n/g,'\r\n'))};
    const rawErr=options.stderr || {write:s=>this._emit(session,s.replace(/\r?\n/g,'\r\n'))};
    let output=0;
    const bounded=dest=>({write:s=>{s=String(s);output+=new TextEncoder().encode(s).length;if(output>VCMD_LIMITS.outputBytes){controller.abort('VCMD_OUTPUT_LIMIT');throw new Error('VCMD output exceeds 1 MiB');}dest.write(s);}});
    const stdout=bounded(rawOut),stderr=bounded(rawErr);
    if(session.vcmdActive) {stderr.write('vcmd: recursive execution refused; use @include\n');return 2;}
    const previousController=session.running;session.running=controller;session.vcmdActive=true;
    let timeout;
    try {
      timeout=setTimeout(()=>controller.abort('VCMD_TIMEOUT'),VCMD_LIMITS.milliseconds);
      const read=options.local ? async (p,parent) => {
        if(!this.host.readVcmd)throw new Error('UNAVAILABLE: server-local paths require WebSocket mode and --vcmd-root; use Scripts import in a browser');
        return this.host.readVcmd(p,parent);
      } : async (p,parent) => {
        if(/^[A-Za-z]:|^\\\\|^file:/i.test(p))throw new Error('Native paths need --local or the Scripts file picker');
        const id=resolvePath(parent?dirname(parent):session.cwd,p);
        if(!/\.vcmd$/i.test(id))throw new Error('Only .vcmd sources are admitted');
        return {id,text:this.vfs.readFile(id)};
      };
      const plan=await compileVcmd(target,read,{signal:controller.signal});
      if(controller.signal.aborted)return 130;
      if(mode==='check') {stdout.write(`VCMD VALID: ${plan.commands.length} command lines; ${plan.files.length} source files; policy=${plan.onError}. Syntax only, not execution authorization.\n`);return 0;}
      if(mode==='show') {for(const c of plan.commands)stdout.write(`${c.source}:${c.line}  ${c.text}\n`);return 0;}
      if(mode!=='run')throw new Error('Unknown VCMD operation');
      let failures=0,firstFailure=0,completed=0;
      const out=stdout,err=stderr;
      for(const c of plan.commands) {
        if(controller.signal.aborted || !session.alive)return 130;
        if(options.trace)err.write(`[vcmd ${c.source}:${c.line}] ${c.text}\n`);
        const code=await this._executeLine(session,c.text,{controller,stdout:out,stderr:err,batch:true,segments:c.segments});completed++;
        if(code!==0){failures++;firstFailure ||= code;err.write(`vcmd: ${c.source}:${c.line}: exit ${code}\n`);if(plan.onError==='stop' || controller.signal.aborted)break;}
        await new Promise(resolve=>setTimeout(resolve,0));
      }
      if(controller.signal.aborted){stderr.write('vcmd: '+String(controller.signal.reason||'interrupted')+'\n');return 130;}
      stdout.write(`VCMD complete: ${completed}/${plan.commands.length} lines; ${failures} failures.\n`);
      return firstFailure;
    } catch(e) {rawErr.write('vcmd: '+String(e.message).slice(0,500)+'\n');return controller.signal.aborted?130:2;}
    finally {clearTimeout(timeout);session.vcmdActive=false;session.running=previousController;}
  }

  _makeContext(session, descriptor, argv, io) {
    const spec = descriptor.parse || {};
    const { flags, args } = parseFlags(argv, spec);
    return {
      argv,
      args,
      vcmd: (mode,target,opts) => this.executeVcmd(session,mode,target,{...opts,controller:io.controller,stdout:io.stdout,stderr:io.stderr}),
      flags,
      stdin: io.stdin,
      stdout: io.stdout,
      stderr: io.stderr,
      signal: io.signal,
      env: session.env,
      session,
      vfs: this.vfs,
      host: this.host,
      registry: this.registry,
      limits: this.limits,
      cwd: session.cwd,
      // convenience: resolve a path arg against the session cwd
      resolve: (p) => resolvePath(session.cwd, p),
      // convenience: change the session cwd (used by `cd`)
      chdir: (abs) => { session.cwd = abs; },
      aliases: session.aliases,
      // RAMWS: a streaming producer calls this between batches; it resolves at once unless the bounded output
      // stage is congested (slow consumer / no credit), in which case the producer waits instead of allocating.
      // Always a MACROtask so the worker's event loop can receive control records (pause/consumed) between batches.
      yield: () => new Promise((res) => defer(() => { if (this.host.flow && this.host.flow.paused()) this.host.flow.onDrain(res); else res(); })),
      exit: (code) => { session.alive = false; session._exitCode = code || 0; },
      cols: session.cols,
      rows: session.rows
    };
  }

  /* ---- completion ------------------------------------------------------ */

  _complete(session, buf, cur) {
    const head = buf.slice(0, cur);
    const tokens = head.split(/\s+/);
    const token = tokens[tokens.length - 1];
    const isCommandPos = tokens.length === 1;

    let candidates;
    let replaceToken;

    if (isCommandPos) {
      candidates = this.registry.names().filter((n) => n.startsWith(token));
    } else {
      // Let a command supply its own argument completion (e.g. subcommands).
      let name = tokens[0];
      if (session.aliases[name]) name = session.aliases[name].split(/\s+/)[0];
      const d = this.registry.resolve(name);
      if (d && typeof d.complete === 'function' && tokens.length === 2) {
        try {
          const c = d.complete({ session, env: session.env }, token) || [];
          candidates = c.filter((x) => x.startsWith(token));
        } catch { candidates = []; }
      }
      if (!candidates || !candidates.length) candidates = this._pathCandidates(session, token);
    }
    if (!candidates.length) return null;

    if (candidates.length === 1) {
      replaceToken = candidates[0];
      const suffix = replaceToken.endsWith('/') ? '' : ' ';
      const newHead = head.slice(0, head.length - token.length) + replaceToken + suffix;
      return { replace: newHead + buf.slice(cur), cursor: newHead.length, candidates };
    }

    const common = commonPrefix(candidates);
    if (common.length > token.length) {
      const newHead = head.slice(0, head.length - token.length) + common;
      return { replace: newHead + buf.slice(cur), cursor: newHead.length, candidates };
    }
    return { candidates };
  }

  _pathCandidates(session, token) {
    let dirPart = token.includes('/') ? token.slice(0, token.lastIndexOf('/') + 1) : '';
    const basePart = token.slice(dirPart.length);
    const absDir = resolvePath(session.cwd, dirPart || '.');
    let entries;
    try { entries = this.vfs.readdir(absDir); } catch { return []; }
    return entries
      .filter((e) => e.name.startsWith(basePart))
      .map((e) => dirPart + e.name + (e.type === 'dir' ? '/' : ''));
  }
}

function commonPrefix(list) {
  if (!list.length) return '';
  let p = list[0];
  for (const s of list) {
    while (!s.startsWith(p)) p = p.slice(0, -1);
    if (!p) break;
  }
  return p;
}

export { SpiralKernel, validGeometry, GEOMETRY, DEFAULT_LIMITS };
