import test from 'node:test';
import assert from 'node:assert/strict';
import {parseHTML} from 'linkedom';
import {webUrl,pageMedia,mediaType,candidate,collectMedia} from '../../chrome-extension/detection.js';
import {Discovery} from '../../chrome-extension/discovery.js';
const tick=async()=>{for(let i=0;i<12;i++)await new Promise(r=>setImmediate(r));};
const page='https://example.com/watch';
function harness(enabled=false,native){
  const values={local:{scanEnabled:enabled},session:{}},badges=[],registrations=[],injections=[];
  const storage={};for(const area of ['local','session'])storage[area]={get:async keys=>typeof keys==='string'?{[keys]:values[area][keys]}:structuredClone(values[area]),set:async v=>Object.assign(values[area],structuredClone(v))};
  const tab={id:9,url:page,title:'Test page'};
  const api={storage,tabs:{get:async()=>({...tab}),query:async()=>[{...tab}],sendMessage:async()=>{}},permissions:{contains:async()=>enabled},action:{setBadgeText:async v=>badges.push(v),setBadgeBackgroundColor:async()=>{}},scripting:{unregisterContentScripts:async()=>{},registerContentScripts:async v=>registrations.push(v),executeScript:async v=>injections.push(v)},webNavigation:{getFrame:async()=>({url:page,documentId:'new-doc'})}};
  return {d:new Discovery(api,()=>{},native),values,badges,registrations,injections,tab,api};
}
const sender=()=>({id:'flow',tab:{id:9},frameId:0,url:page});
async function add(h,items){await h.d.ready;await h.d.found({pageUrl:page,items},sender());}

test('detection accepts signed public URLs without stripping tokens and rejects private or executable URLs',()=>{
  assert.equal(webUrl('/v.mp4?sig=a%2Fb&range=5#t=3',page),'https://example.com/v.mp4?sig=a%2Fb&range=5');
  for(const url of ['file:///a','javascript:alert(1)','data:image/png,a','blob:https://example.com/a','http://127.0.0.1/a','https://192.168.1.1/a','https://internal.local/a','https://user:pass@example.com/a','https://example.com:8443/a'])assert.equal(webUrl(url),null,url);
});
test('HLS and DASH manifests are detected but segments and encryption keys are excluded',()=>{
  assert.equal(mediaType('https://cdn.example.com/master.m3u8'),'stream');assert.equal(mediaType('https://cdn.example.com/id','application/dash+xml'),'stream');
  for(const path of ['segment-18.mp4','chunk_1.m4s','init.mp4','file.ts','secret.key'])assert.equal(mediaType('https://cdn.example.com/'+path,'video/mp4'),null,path);
});
test('DOM scanning finds responsive images, nested audio sources, video, file links and loaded resources',()=>{
  const {document}=parseHTML('<html><head><title>Demo</title></head><body><video src="/clip.mp4"></video><audio><source src="/sound.mp3" type="audio/mpeg"></audio><img src="/photo.jpg" alt="A photo" width="640" height="480"><img src="/pixel.gif" width="1" height="1"><a href="/download.webm">Film</a><a href="/account">Account</a></body></html>');
  const img=document.querySelector('img');Object.defineProperty(img,'currentSrc',{value:'https://cdn.example.com/photo-large.webp'});
  const items=collectMedia(document,page,[{name:'https://cdn.example.com/master.m3u8'}]);
  assert.equal(items.length,5);assert.ok(items.some(i=>i.url.endsWith('photo-large.webp')&&i.kind==='image'));assert.ok(items.some(i=>i.kind==='audio'));assert.ok(items.some(i=>i.kind==='stream'));assert.ok(!items.some(i=>i.url.includes('pixel')||i.url.includes('account')));
});
test('YouTube audio/video fragments resolve to a single source page; DRM and SVG remain labelled',()=>{
  const youtube='https://www.youtube.com/watch?v=jNQXAC9IVRw&list=test';
  assert.equal(pageMedia(youtube),'https://www.youtube.com/watch?v=jNQXAC9IVRw');
  assert.equal(candidate({url:'https://rr.googlevideo.com/videoplayback?id=x',mime:'audio/mp4'},youtube).url,pageMedia(youtube));
  assert.equal(candidate({url:'https://example.com/a.svg',kind:'image'},page).unsupported,true);
  assert.equal(candidate({url:'https://example.com/v.mp4',kind:'video',protected:true},page).protected,true);
});
test('scanning stays off until manual scan or explicit optional site permission',async()=>{
  const h=harness();await h.d.ready;await h.d.syncRegistration();assert.equal(h.registrations.length,0);
  await h.d.navigate({tabId:9,frameId:0,url:page,timeStamp:Date.now()});assert.deepEqual(h.d.tabs,{});
  await h.d.network({tabId:9,url:'https://cdn.example.com/a.mp4',statusCode:200,responseHeaders:[],timeStamp:Date.now()});assert.deepEqual(h.d.tabs,{});
  await assert.rejects(h.d.configure(true,true),/SITE_PERMISSION/);
  await h.d.scan(9);await add(h,[{url:'/movie.mp4',kind:'video'}]);assert.equal(h.d.tabs[9].items.length,1);assert.equal(h.injections.length,1);
});
test('media from DOM and network deduplicates, updates badge and skips stale documents',async()=>{
  const h=harness(true);await add(h,[{url:'/movie.mp4',kind:'video'}]);
  await h.d.network({tabId:9,url:'https://example.com/movie.mp4',statusCode:206,responseHeaders:[{name:'Content-Type',value:'video/mp4'},{name:'Content-Length',value:'1024'}],timeStamp:Date.now()});
  assert.equal(h.d.tabs[9].items.length,1);assert.equal(h.d.tabs[9].items[0].bytes,0);assert.equal(h.badges.at(-1).text,'1');
  h.tab.url='https://example.com/new';await h.d.found({pageUrl:page,items:[{url:'/old.mp4',kind:'video'}]},sender());assert.equal(h.d.tabs[9].items.length,1);
  await h.d.navigate({tabId:9,frameId:0,url:h.tab.url,timeStamp:Date.now()});assert.equal(h.d.tabs[9].items.length,0);assert.equal(h.badges.at(-1).text,'');
});
test('bounded discovery survives worker restart and disable clears session data',async()=>{
  const h=harness(true);await add(h,Array.from({length:250},(_,i)=>({url:`/image-${i}.png`,kind:'image'})));await add(h,[{url:'/extra.png',kind:'image'}]);
  assert.equal(h.d.tabs[9].items.length,250);assert.equal(h.d.tabs[9].limited,true);
  const reopened=new Discovery(h.api,()=>{});await reopened.ready;assert.equal(reopened.tabs[9].items.length,250);
  await reopened.configure(false,false);assert.deepEqual(h.values.session.discovery,{});
});
test('late video remains discoverable when image results reach the page limit',async()=>{
  const h=harness(true);await add(h,Array.from({length:250},(_,i)=>({url:`/image-${i}.png`,kind:'image'})));
  await add(h,[{url:'/late.mp4',kind:'video'}]);assert.equal(h.d.tabs[9].items.length,250);assert.ok(h.d.tabs[9].items.some(i=>i.kind==='video'));
});
test('turning off video buttons preserves an explicitly requested manual scan',async()=>{
  const h=harness();await h.d.scan(9);await add(h,[{url:'/movie.mp4',kind:'video'}]);await h.d.configure(false,false);
  assert.equal(h.d.tabs[9].manual,true);assert.equal(h.d.tabs[9].items.length,1);
});
test('a delayed iframe cannot inject media from a previous document',async()=>{
  const h=harness(true);await h.d.ready;
  await h.d.found({pageUrl:page,items:[{url:'/old.mp4',kind:'video'}]},{...sender(),frameId:4,documentId:'old-doc'});
  assert.equal(h.d.tabs[9],undefined);
});
test('batch owns progress outside popup, reports individual failures and retries with same request ID',async()=>{
  const calls=[];let fail=true;
  const h=harness(true,async(api,r)=>{calls.push(structuredClone(r));if(r.action==='inspect')return {ok:true,data:{token:r.url,choices:[{format:'mp4',key:'mp4:720'}]}};if(fail&&r.token.endsWith('b.mp4'))return {ok:false,error:{code:'HOST_DISCONNECTED'}};return {ok:true,data:{jobId:r.requestId}};});
  await add(h,[{url:'/a.mp4',kind:'video'},{url:'/b.mp4',kind:'video'}]);
  await h.d.startBatch(9,h.d.tabs[9].items.map(i=>i.id),true);await tick();
  assert.deepEqual(h.d.batch.items.map(i=>i.state),['added','failed']);assert.equal(h.values.session.batch.state,'done');
  const id=h.d.batch.items[1].requestId;fail=false;await h.d.retryBatch();await tick();
  assert.deepEqual(h.d.batch.items.map(i=>i.state),['added','added']);assert.equal(calls.filter(r=>r.action==='enqueue'&&r.requestId===id).length,2);assert.ok(calls.filter(r=>r.action==='enqueue').every(r=>r.hold));
});
test('stopping a batch prevents enqueue after an in-flight inspection',async()=>{
  let finish;const requests=[];const h=harness(true,async(api,r)=>{requests.push(r);return new Promise(resolve=>finish=resolve);});
  await add(h,[{url:'/a.mp4',kind:'video'}]);await h.d.startBatch(9,[h.d.tabs[9].items[0].id],false);await tick();h.d.batch.stop=true;
  finish({ok:true,data:{token:'a',choices:[{format:'mp4',key:'mp4:720'}]}});await tick();assert.equal(h.d.batch.state,'stopped');assert.equal(requests.length,1);
});
test('a history update on the same address keeps the list and a manual scan',async()=>{
  const h=harness(false);await h.d.scan(9);await add(h,[{url:'/movie.mp4',kind:'video'}]);
  await h.d.navigate({tabId:9,frameId:0,url:page+'#chapter',timeStamp:Date.now()},true);
  assert.equal(h.d.tabs[9].items.length,1);assert.equal(h.d.tabs[9].manual,true);
  await h.d.navigate({tabId:9,frameId:0,url:'https://example.com/next',timeStamp:Date.now()},true);
  assert.equal(h.d.tabs[9],undefined,'a new address on a single-page site is a new page');
});
