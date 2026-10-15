// Development-only adapter for the retained createPhotonEngine implementation.
// JSON lines carry the same owner messages that photon-worker.js transports.
// No commands, filesystem, store, or Photon semantics are stubbed here: every
// capability call crosses the actual Go broker's authenticated WebSocket.
import {createInterface} from 'node:readline';
import {pathToFileURL} from 'node:url';
import {resolve, join} from 'node:path';
import {webcrypto} from 'node:crypto';

if (!globalThis.crypto) globalThis.crypto = webcrypto;
const web = resolve(process.argv[2] || '');
const {createPhotonEngine} = await import(pathToFileURL(join(web, 'photon-engine.js')).href);
const pending = new Map();
let serial = 0, engine, queue = Promise.resolve(), starting = false, stopped = false;
const emit = message => process.stdout.write(JSON.stringify(message) + '\n');
const fail = error => {
  process.stderr.write(String(error?.stack || error) + '\n');
  process.exitCode = 1;
  for (const request of pending.values()) {
    clearTimeout(request.timer);
    request.reject(new Error('Development owner bridge failed'));
  }
  pending.clear();
  process.exit(1);
};
const rpc = (op, args) => new Promise((resolveRequest, reject) => {
  const rid = 'node-test-' + (++serial);
  const timer = setTimeout(() => {
    pending.delete(rid);
    reject(new Error('Native broker capability timed out: ' + op));
  }, 10000);
  pending.set(rid, {resolve: resolveRequest, reject, timer});
  emit({type: 'rpc', rid, op, args});
});
const lines = createInterface({input: process.stdin, crlfDelay: Infinity});
lines.on('line', text => {
  try {
    if (text.length > 10 * 1024 * 1024) throw new Error('Development bridge input limit');
    const message = JSON.parse(text);
    if (message.type === 'rpc.result') {
      const request = pending.get(message.rid);
      if (!request) throw new Error('Unknown native capability response');
      pending.delete(message.rid);
      clearTimeout(request.timer);
      message.ok ? request.resolve(message.result) : request.reject(new Error(message.error || 'Native capability denied'));
      return;
    }
    if (message.type === 'engine.authenticated') {
      if (starting) throw new Error('Duplicate native owner authentication');
      starting = true;
      queue = queue.then(async () => {
        engine = await createPhotonEngine({rpc, send: emit, onState: type => { if (type === 'stopped') stopped = true; }});
      }).catch(fail);
      return;
    }
    queue = queue.then(async () => {
      if (!engine) throw new Error('Owner event before runtime creation');
      await engine.receive(message);
    }).catch(fail);
  } catch (error) { fail(error); }
});
lines.on('close', () => {
  queue.then(() => {
    if (!stopped || pending.size) throw new Error('Native broker disconnected before the real runtime acknowledged shutdown');
  }).catch(fail);
});
process.on('unhandledRejection', fail);
