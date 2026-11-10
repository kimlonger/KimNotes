// Isolated synthetic test service: no real accounts, credentials, or storage.
const root='/__kim-sandbox/';
function response(body,status=200,extra={}) { return Response.json(body,{status,headers:{'Cache-Control':'private, no-store','Pragma':'no-cache','X-Content-Type-Options':'nosniff',...extra}}); }
async function smallBody(req) {
 if(Number(req.headers.get('content-length'))>256)throw new Error('large');
 const reader=req.body?.getReader();let size=0;const parts=[];
 if(reader)while(true){const {value,done}=await reader.read();if(done)break;size+=value.length;if(size>256){await reader.cancel();throw new Error('large');}parts.push(value);}
 const joined=new Uint8Array(size);let offset=0;for(const part of parts){joined.set(part,offset);offset+=part.length;}return JSON.parse(new TextDecoder().decode(joined));
}
export default {
 async fetch(req) {
  const url=new URL(req.url);
  if(!url.pathname.startsWith(root)) return response({error:'not_found'},404);
  if(req.method==='POST' && url.pathname===root+'login') {
   let body;try { body=await smallBody(req); } catch { return response({error:'invalid'},400); }
   if(!['test-a','test-b'].includes(body.user) || body.password!=='synthetic-only') return response({error:'invalid'},401);
   return response({user:body.user},200,{'Set-Cookie':`kim_synthetic=${body.user}; Path=${root}; Secure; HttpOnly; SameSite=Strict`});
  }
  if(req.method==='POST' && url.pathname===root+'sync') {
   const cookie=req.headers.get('cookie')||'';const match=/(?:^|;\s*)kim_synthetic=(test-a|test-b)(?:;|$)/.exec(cookie);
   if(!match)return response({error:'not_authenticated'},401);
   let body;try {body=await smallBody(req);}catch{return response({error:'invalid'},400);}
   if(body.note!==`synthetic-${match[1]}`)return response({error:'invalid'},400);
   return response({user:match[1],accepted:body.note},200,{'Set-Cookie':`kim_synthetic_note=${body.note}; Path=${root}; Secure; HttpOnly; SameSite=Strict`});
  }
  if(req.method==='GET' && url.pathname===root+'sync') {
   const cookie=req.headers.get('cookie')||'';const match=/(?:^|;\s*)kim_synthetic=(test-a|test-b)(?:;|$)/.exec(cookie);
   if(!match)return response({error:'not_authenticated'},401);
   const expected=`synthetic-${match[1]}`;const stored=/(?:^|;\s*)kim_synthetic_note=(synthetic-test-[ab])(?:;|$)/.exec(cookie);
   return response({user:match[1],notes:stored?.[1]===expected?[{text:stored[1]}]:[]});
  }
  if(req.method==='GET' && url.pathname===root+'headers') {
   const spoof='203.0.113.87', eo=req.headers.get('EO-Connecting-IP'), cf=req.headers.get('CF-Connecting-IP'), xff=req.headers.get('X-Forwarded-For');
   return response({eoPresent:!!eo,eoIsSpoof:eo===spoof,cfEqualsEo:!!eo&&cf===eo,xffContainsSpoof:!!xff&&xff.includes(spoof)});
  }
  return response({error:'not_found'},404);
 }
};
