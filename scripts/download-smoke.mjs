// Uses only the explicitly supplied test folder. Never prints the API key.
// node scripts/download-smoke.mjs C:\path\to\dedicated-test-folder
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createHash, randomBytes, createCipheriv, createDecipheriv } from 'node:crypto';
import { readFile, writeFile, mkdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';

if (!process.argv[2] || !path.isAbsolute(process.argv[2])) throw Error('Pass an absolute dedicated test folder.');
const root = path.resolve(process.argv[2]); await mkdir(root, { recursive: true });
const fixtures=path.join(root,'fixtures');await mkdir(fixtures,{recursive:true});
const key=(await readFile(path.join(process.env.LOCALAPPDATA,'MediaHub','api.key'),'utf8')).trim();
const base=process.env.MEDIAHUB_TEST_URL || 'http://127.0.0.1:8765';
async function api(route, body, expected=200, method='POST') {
  const r=await fetch(base+route,{method,headers:{'X-MediaHub-Key':key,'Content-Type':'application/json'},...(body===undefined?{}:{body:JSON.stringify(body)})});
  const data=await r.json();assert.equal(r.status,expected,JSON.stringify(data));return data;
}
const plain=randomBytes(1024*1024), slow=randomBytes(8*1024*1024);
let activeSegments=0, peakSegments=0, activeFiles=0, peakFiles=0;
const segmentRequests=new Map();
const server=createServer(async(req,res)=>{
  try{
    const name=new URL(req.url,'http://localhost').pathname.slice(1);
    if(name==='forbidden.m3u8'||(name==='video.m3u8'&&req.headers['user-agent']!=='Mozilla/5.0')){res.statusCode=403;res.end('Forbidden');return;}
    if(name.endsWith('.ts')){segmentRequests.set(name,(segmentRequests.get(name)||0)+1);activeSegments++;peakSegments=Math.max(peakSegments,activeSegments);res.once('close',()=>activeSegments--);await sleep(300);}
    if(name==='slow.bin'&&req.method!=='HEAD'){activeFiles++;peakFiles=Math.max(peakFiles,activeFiles);res.once('close',()=>activeFiles--);}
    const data=name==='file.bin'?plain:name==='slow.bin'?slow:await readFile(path.join(fixtures,path.basename(name)));
    let start=0,end=data.length-1;
    if(req.headers.range){const range=/bytes=(\d+)-(\d*)/.exec(req.headers.range);start=Number(range[1]);if(range[2])end=Math.min(end,Number(range[2]));res.statusCode=206;res.setHeader('Content-Range',`bytes ${start}-${end}/${data.length}`);}
    res.setHeader('Accept-Ranges','bytes');res.setHeader('Content-Length',end-start+1);
    if(name.endsWith('.m3u8'))res.setHeader('Content-Type','application/vnd.apple.mpegurl');
    if(req.method==='HEAD'){res.end();return;}
    if(name==='slow.bin'){for(let i=start;i<=end&&!res.destroyed;i+=32768){res.write(data.subarray(i,Math.min(i+32768,end+1)));await sleep(80);}res.end();}
    else res.end(data.subarray(start,end+1));
  }catch{res.statusCode=404;res.end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const origin='http://127.0.0.1:'+server.address().port;
const ids=[];
async function create(source,extra={}){const {id}=await api('/api/downloads',{source,directory:root,autoIndex:true,...extra});ids.push(id);return id;}
async function task(id){return (await api('/api/downloads',undefined,200,'GET')).tasks.find(t=>t.id===id);}
async function wait(id,status='completed',seconds=40){for(let i=0;i<seconds*2;i++){const t=await task(id);if(t.status===status)return t;if(t.status==='error')throw Error(t.error);await sleep(500);}throw Error('Task timed out: '+id);}
function bencode(value){if(Buffer.isBuffer(value))return Buffer.concat([Buffer.from(value.length+':'),value]);if(typeof value==='string')return bencode(Buffer.from(value));if(typeof value==='number')return Buffer.from('i'+value+'e');if(Array.isArray(value))return Buffer.concat([Buffer.from('l'),...value.map(bencode),Buffer.from('e')]);return Buffer.concat([Buffer.from('d'),...Object.keys(value).sort().flatMap(k=>[bencode(k),bencode(value[k])]),Buffer.from('e')]);}
const originalSettings=await api('/api/downloads/settings',undefined,200,'GET');
try{
  await api('/api/downloads/settings',{maxConcurrentTasks:0,connectionsPerFile:2,hlsThreads:2},400,'PUT');
  const settings={maxConcurrentTasks:1,connectionsPerFile:2,hlsThreads:2};
  assert.deepEqual(await api('/api/downloads/settings',settings,200,'PUT'),settings);
  assert.deepEqual(await api('/api/downloads/settings',undefined,200,'GET'),settings);
  assert.equal((await fetch(base+'/api/downloads')).status,401);
  await api('/api/downloads',{source:'file:///C:/Windows/win.ini',directory:root},400);
  await api('/api/downloads',{source:origin+'/file.bin',directory:root,name:'../escape'},400);
  const direct=await create(origin+'/file.bin');const done=await wait(direct);assert.equal(done.indexed,true);assert.deepEqual(await readFile(done.files[0]),plain);
  const paused=await create(origin+'/slow.bin');await sleep(1500);await api(`/api/downloads/${paused}/pause`);const pausedTask=await task(paused);assert.equal(pausedTask.status,'paused');
  await api('/api/index',{path:pausedTask.saveDir,recursive:true},400);
  if(process.env.MEDIAHUB_TEST_RESTART==='1'){
    const exe=path.resolve('artifacts/verified/MediaHub.exe');
    const restart=spawnSync('powershell.exe',['-NoProfile','-Command','Get-Process MediaHub | Where-Object { $_.Path -eq $env:MEDIAHUB_TEST_EXE } | Stop-Process -Force; Start-Process -FilePath $env:MEDIAHUB_TEST_EXE -WorkingDirectory (Split-Path $env:MEDIAHUB_TEST_EXE) -ArgumentList --background -WindowStyle Hidden'],{env:{...process.env,MEDIAHUB_TEST_EXE:exe},encoding:'utf8'});assert.equal(restart.status,0,restart.stderr);
    for(let i=0;i<40;i++){try{const r=await fetch(base+'/api/health');if(r.ok)break;}catch{}await sleep(250);}
    assert.equal((await task(paused)).status,'paused');assert.ok(await stat(pausedTask.saveDir));
    console.log('PASS: owned process restart and persisted paused task.');
  }
  await api(`/api/downloads/${paused}/resume`);
  const queued=await create(origin+'/file.bin');assert.equal((await task(queued)).status,'queued');
  const cancelledQueue=await create(origin+'/file.bin');assert.equal((await task(cancelledQueue)).status,'queued');await api(`/api/downloads/${cancelledQueue}/remove`);
  const resumed=await wait(paused);assert.deepEqual(await readFile(resumed.files[0]),slow);await wait(queued);assert.ok(peakFiles>=2,'Expected parallel file connections');
  // A real .torrent with a local HTTP web seed, no external swarm required.
  const length=262144,pieces=[];for(let i=0;i<plain.length;i+=length)pieces.push(createHash('sha1').update(plain.subarray(i,i+length)).digest());
  const torrent=bencode({'url-list':[origin+'/file.bin'],info:{length:plain.length,name:'file.bin','piece length':length,pieces:Buffer.concat(pieces)}});
  const r=await fetch(base+'/api/downloads/torrent?'+new URLSearchParams({directory:root,name:'Local torrent'}),{method:'POST',headers:{'X-MediaHub-Key':key,'Content-Type':'application/octet-stream'},body:torrent});assert.equal(r.status,200);const bt=(await r.json()).id;ids.push(bt);const btDone=await wait(bt);assert.deepEqual(await readFile(btDone.files[0]),plain);
  // Generate standard AES-128 HLS, then download/decrypt/remux through HTTP.
  const ffmpeg=path.resolve('artifacts/download-tools/ffmpeg.exe');
  await writeFile(path.join(fixtures,'key.bin'),randomBytes(16));await writeFile(path.join(fixtures,'keyinfo'),origin+'/key.bin\n'+path.join(fixtures,'key.bin')+'\n');
  const generated=spawnSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-f','lavfi','-i','testsrc=size=160x90:rate=10','-t','8','-pix_fmt','yuv420p','-c:v','libx264','-g','10','-hls_time','1','-hls_list_size','0','-hls_key_info_file',path.join(fixtures,'keyinfo'),path.join(fixtures,'video.m3u8')],{encoding:'utf8'});assert.equal(generated.status,0,generated.stderr);
  const hls=await create(origin+'/video.m3u8',{name:'Encrypted HLS'});const hlsDone=await wait(hls);assert.equal(hlsDone.indexed,true);assert.equal(hlsDone.downloadMode,'parallel-hls');assert.equal(hlsDone.completedSegments,8);assert.ok((await stat(hlsDone.files[0])).size>1000);assert.equal(peakSegments,2,'Expected exactly two simultaneous segment requests');
  const forbidden=await create(origin+'/forbidden.m3u8');assert.match((await wait(forbidden,'error')).error,/HTTP 403/);
  const probe=spawnSync(ffmpeg,['-v','error','-i',hlsDone.files[0],'-f','null','-'],{encoding:'utf8'});assert.equal(probe.status,0,probe.stderr);
  // Cancellation must preserve complete segments and avoid re-fetching them.
  const cached=await create(origin+'/video.m3u8',{name:'Cached HLS'});
  for(let i=0;i<20;i++){if((await task(cached)).completedSegments>=2)break;await sleep(50);}
  await api(`/api/downloads/${cached}/pause`);const firstSegmentCount=segmentRequests.get('video0.ts');await sleep(400);
  await api(`/api/downloads/${cached}/resume`);await wait(cached);assert.equal(segmentRequests.get('video0.ts'),firstSegmentCount,'Complete cached segment was re-downloaded');
  // Verify key rotation and sequence-derived IVs with decoded outputs.
  const keyA=await readFile(path.join(fixtures,'key.bin')),keyB=randomBytes(16);await writeFile(path.join(fixtures,'key2.bin'),keyB);
  const plaintext=[];for(let i=0;i<2;i++){const d=createDecipheriv('aes-128-cbc',keyA,Buffer.alloc(16));plaintext.push(Buffer.concat([d.update(await readFile(path.join(fixtures,`video${i}.ts`))),d.final()]));}
  const ivFor=n=>{const iv=Buffer.alloc(16);iv.writeBigUInt64BE(BigInt(n),8);return iv;};
  const encrypt=(data,key,iv)=>{const c=createCipheriv('aes-128-cbc',key,iv);return Buffer.concat([c.update(data),c.final()]);};
  await writeFile(path.join(fixtures,'rotate.ts'),encrypt(plaintext[1],keyB,ivFor(1)));
  await writeFile(path.join(fixtures,'rotation.m3u8'),`#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x0\n#EXTINF:1,\nvideo0.ts\n#EXT-X-KEY:METHOD=AES-128,URI="key2.bin",IV=0x1\n#EXTINF:1,\nrotate.ts\n#EXT-X-ENDLIST\n`);
  for(let i=0;i<2;i++)await writeFile(path.join(fixtures,`implicit${i}.ts`),encrypt(plaintext[i],keyA,ivFor(i+7)));
  await writeFile(path.join(fixtures,'implicit.m3u8'),'#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI="key.bin"\n#EXTINF:1,\nimplicit0.ts\n#EXTINF:1,\nimplicit1.ts\n#EXT-X-ENDLIST\n');
  for(const source of ['rotation.m3u8','implicit.m3u8']){const result=await wait(await create(origin+'/'+source));const decoded=spawnSync(ffmpeg,['-v','error','-i',result.files[0],'-f','null','-'],{encoding:'utf8'});assert.equal(decoded.status,0,decoded.stderr);}
  const fragmented=spawnSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-f','lavfi','-i','testsrc=size=160x90:rate=10','-t','2','-pix_fmt','yuv420p','-c:v','libx264','-g','10','-hls_time','1','-hls_segment_type','fmp4','-hls_fmp4_init_filename','init.mp4',path.join(fixtures,'fragmented.m3u8')],{encoding:'utf8',cwd:fixtures});assert.equal(fragmented.status,0,fragmented.stderr);
  const compatible=await wait(await create(origin+'/fragmented.m3u8'));assert.equal(compatible.downloadMode,'ffmpeg');
  const cancelled=await create(origin+'/slow.bin');await sleep(1200);const cancelledTask=await task(cancelled);await api(`/api/downloads/${cancelled}/remove`);assert.ok(await stat(cancelledTask.saveDir));await api('/api/index',{path:cancelledTask.saveDir,recursive:true},400);
  const library=await api('/api/items?search='+encodeURIComponent(root),undefined,200,'GET');assert.ok(library.items.some(i=>i.path===done.files[0]));
  console.log('PASS: API auth, settings validation, shared queue, parallel file connections, BT webseed, two concurrent AES-128 HLS segments, decode/remux, indexing and cancellation preservation.');
}finally{
  await api('/api/downloads/settings',originalSettings,200,'PUT');
  const snapshot=await api('/api/downloads',undefined,200,'GET');
  const testTasks=snapshot.tasks.filter(t=>{const relative=path.relative(root,t.saveDir);return relative&&!relative.startsWith('..')&&!path.isAbsolute(relative);});
  for(const t of testTasks){try{await api(`/api/downloads/${t.id}/remove`);}catch{}}
  // Remove only indexes belonging to the explicitly selected test folder.
  await api('/api/index',{path:root},200,'DELETE');
  server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
}
