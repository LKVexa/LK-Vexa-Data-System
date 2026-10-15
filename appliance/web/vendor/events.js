// @ts-nocheck
/** Tiny emitter for the ported SPIRAL kernel. No Node dependency. */
export class EventEmitter {
  constructor() {
    this._l = new Map();
  }
  on(ev, fn) {
    const a = this._l.get(ev) || [];
    a.push(fn);
    this._l.set(ev, a);
    return this;
  }
  emit(ev, ...args) {
    const a = this._l.get(ev);
    if (!a) return false;
    for (const fn of a.slice()) fn(...args);
    return true;
  }
}
