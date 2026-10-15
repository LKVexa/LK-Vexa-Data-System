export const PROTOCOL='hermit.vws.v2';
export const KIND_INPUT=1,KIND_OUTPUT=2,MAX_SEQUENCE=2**48-1,MAX_DATA=16384;
export function encodeData(kind,seq,payload){
 const body=payload instanceof Uint8Array?payload:new Uint8Array(payload);
 if(![1,2].includes(kind)||!Number.isSafeInteger(seq)||seq<1||seq>MAX_SEQUENCE||!body.length||body.length>MAX_DATA)throw new Error('invalid frame');
 const m=new Uint8Array(7+body.length);m[0]=kind;let n=seq;for(let i=6;i>=1;i--){m[i]=n%256;n=Math.floor(n/256);}m.set(body,7);return m;
}
export function decodeData(data){
 const b=data instanceof Uint8Array?data:new Uint8Array(data);if(b.length<8||b.length>MAX_DATA+7||![1,2].includes(b[0]))throw new Error('invalid frame');
 let seq=0;for(let i=1;i<=6;i++)seq=seq*256+b[i];if(seq<1)throw new Error('invalid sequence');return {kind:b[0],seq,payload:b.subarray(7)};
}
