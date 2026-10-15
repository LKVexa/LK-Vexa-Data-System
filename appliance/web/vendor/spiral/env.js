// @ts-nocheck


/**
 * SPIRAL — Environment
 * ---------------------------------------------------------------------------
 * A scoped variable store with copy-on-write child scopes, used for both shell
 * variables and per-command environments. Values are always strings, mirroring
 * POSIX environment semantics.
 */

class Environment {
  /** @param {Record<string,string>} [seed] @param {Environment|null} [parent] */
  constructor(seed = {}, parent = null) {
    this.parent = parent;
    /** @type {Map<string,string>} */
    this.vars = new Map();
    for (const [k, v] of Object.entries(seed)) this.vars.set(k, String(v));
  }

  get(name) {
    if (this.vars.has(name)) return this.vars.get(name);
    return this.parent ? this.parent.get(name) : undefined;
  }

  set(name, value) {
    if(!/^(?:[A-Za-z_][A-Za-z0-9_]*|\?)$/.test(name))throw new Error('invalid environment name');
    if(String(value??'').length>4096||(!this.vars.has(name)&&this.vars.size>=128))throw new Error('environment quota');
    this.vars.set(name, value == null ? '' : String(value));
    return this;
  }

  unset(name) {
    return this.vars.delete(name);
  }

  has(name) {
    return this.vars.has(name) || (this.parent ? this.parent.has(name) : false);
  }

  /** Flattened view (child values shadow parent). */
  toObject() {
    const base = this.parent ? this.parent.toObject() : Object.create(null);
    for (const [k, v] of this.vars) base[k] = v;
    return base;
  }

  /** A child scope for a subshell / command invocation. */
  child(seed = {}) {
    return new Environment({...this.toObject(),...seed});
  }
}

export { Environment };
