/** Origin-scoped IndexedDB. Never a physical device; no automatic legacy import. */
const DB='ja21-dc-console-v2',KEY='volume0',STORE='volumes';
function open(){return new Promise((resolve,reject)=>{const q=indexedDB.open(DB,1);let failed=false;const fail=e=>{failed=true;clearTimeout(timer);reject(e);};const timer=setTimeout(()=>fail(new Error('storage open timed out')),5000);q.onupgradeneeded=()=>{if(!q.result.objectStoreNames.contains(STORE))q.result.createObjectStore(STORE);};q.onerror=()=>fail(q.error);q.onblocked=()=>fail(new Error('storage upgrade blocked'));q.onsuccess=()=>{clearTimeout(timer);if(failed){q.result.close();return;}resolve(q.result);};});}
export class BrowserStore{
 kind='IndexedDB (browser-managed, best-effort)';
 async read(){const db=await open();try{return await new Promise((resolve,reject)=>{const tx=db.transaction(STORE,'readonly');let value=null;const q=tx.objectStore(STORE).get(KEY);q.onsuccess=()=>{value=q.result||null;};tx.oncomplete=()=>resolve(value);tx.onerror=tx.onabort=()=>reject(tx.error||new Error('storage read failed'));});}finally{db.close();}}
 async write(record){const db=await open();try{await new Promise((resolve,reject)=>{const tx=db.transaction(STORE,'readwrite');tx.objectStore(STORE).put(record,KEY);tx.oncomplete=()=>resolve();tx.onerror=tx.onabort=()=>reject(tx.error||new Error('storage commit failed'));});}finally{db.close();}}
}
export class MemoryStore{kind='Memory only (not persistent)';record=null;async read(){return this.record;}async write(r){this.record=structuredClone(r);}}
