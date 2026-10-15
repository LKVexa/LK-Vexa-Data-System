// @ts-nocheck
/**
 * The few Buffer operations the ported VFS and coreutils actually call.
 * Payloads live in the VFS page cache. The worker flushes them to the configured storage.
 */
function mark(u) {
  Object.defineProperty(u, "_buf", { value: true });
  u.toString = function toString(enc) {
    if (enc === "base64") {
      let s = "";
      const chunk = 0x8000;
      for (let i = 0; i < this.length; i += chunk) {
        const part = this.subarray(i, i + chunk);
        s += String.fromCharCode.apply(null, part);
      }
      return btoa(s);
    }
    return new TextDecoder().decode(this);
  };
  return u;
}

export const Buffer = {
  isBuffer(b) {
    return b instanceof Uint8Array && Object.hasOwn(b,"_buf") && b._buf === true;
  },
  byteLength(s) {
    return new TextEncoder().encode(String(s)).length;
  },
  from(data, enc) {
    if (enc === "base64") {
      const bin = atob(String(data));
      const u = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i) & 255;
      return mark(u);
    }
    if (typeof data === "string") return mark(new TextEncoder().encode(data));
    if (data instanceof Uint8Array) return mark(new Uint8Array(data));
    if (typeof data === "number") return mark(new Uint8Array(data));
    return mark(Uint8Array.from(data));
  },
  concat(list) {
    let n = 0;
    for (const b of list) n += b.length;
    const out = new Uint8Array(n);
    let o = 0;
    for (const b of list) {
      out.set(b, o);
      o += b.length;
    }
    return mark(out);
  },
};
