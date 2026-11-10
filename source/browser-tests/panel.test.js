import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {webcrypto} from 'node:crypto';
import {parseHTML} from 'linkedom';
import {translator,errorText} from '../../chrome-extension/i18n.js';

const html=readFileSync(new URL('../../chrome-extension/panel.html',import.meta.url),'utf8');
const script=readFileSync(new URL('../../chrome-extension/panel.js',import.meta.url),'utf8').replace(/^import[^\n]*\n/gm,'');
const tick=async()=>{for(let i=0;i<15;i++)await new Promise(resolve=>setImmediate(resolve));};
const success=data=>({v:1,ok:true,data});
const failure=code=>({v:1,ok:false,error:{code}});
const media=()=>({token:'preview-token',title:'<img src=x onerror=alert(1)> İstanbul',platform:'YouTube',duration:65,folder:'C:\\Downloads',choices:[
  {key:'mp4:720',format:'mp4',height:720,summary:'1280×720 · h264',bytes:1048576,sizeExact:true},
  {key:'mp4:360',format:'mp4',height:360,summary:'640×360 · h264'},
  {key:'mp3:0',format:'mp3',height:0,conversion:true}
]});

async function panel({reply,draft,language,search='?side',api={}}={}) {
  const {document,window}=parseHTML(html);
  // Linkedom intentionally omits single-select setters/default selection; emulate HTML semantics.
  for(const select of document.querySelectorAll('select'))Object.defineProperty(select,'value',{
    get(){return [...this.options].find(o=>o.selected)?.value ?? this.options[0]?.value ?? '';},
    set(value){for(const option of this.options)option.removeAttribute('selected');const match=[...this.options].find(o=>o.value===value);if(match)match.selected=true;}
  });
  const requests=[],changes=[],reloads=[],layouts=[];
  const state={session:{draft},local:{language}};
  const storage=Object.fromEntries(['session','local'].map(area=>[area,{
    async get(key){return {[key]:state[area][key]};},
    async set(values){for(const [key,value] of Object.entries(values)){const oldValue=state[area][key];state[area][key]=structuredClone(value);queueMicrotask(()=>changes.forEach(f=>f({[key]:{newValue:state[area][key],oldValue}},area)));}}
  }]));
  storage.onChanged={addListener:f=>changes.push(f)};
  const chrome={storage,i18n:{getUILanguage:()=> 'tr'},windows:{getCurrent:async()=>({id:7})},
    sidePanel:api.sidePanel||{open:async()=>{}},tabs:{query:async()=>[{url:search.includes('tab=')?'chrome-extension://id/panel.html':'https://example.com/video'}],get:async id=>({id,url:'https://example.com/page-'+id})},
    runtime:{...api.runtime,getURL:path=>'chrome-extension://id/'+path,getManifest:()=>({version:'1.2.0'}),reload:()=>reloads.push('reload'),getContexts:async()=>[],sendMessage:async(message)=>{
      if(message.type==='layout'){layouts.push(message);return api.layoutReply===undefined?{ok:true}:api.layoutReply;}
      const {request}=message;
      requests.push(structuredClone(request));
      const custom=await reply?.(request,storage); if(custom!==undefined)return custom;
      if(request.action==='ping')return success({language:'tr',theme:'dark',folder:'C:\\Downloads',capabilities:['image-original']});
      if(request.action==='queue')return success({items:[],total:0,active:0});
      if(request.action==='inspect')return success(media());
      return success({jobId:'job-1'});
    }}
  };
  Object.assign(chrome,api.extra||{});const closed=[];const context=vm.createContext({document,chrome,close:()=>closed.push('window'),location:{search,href:'chrome-extension://id/panel.html'+search},translator,errorText,crypto:webcrypto,URLSearchParams,Intl,confirm:()=>true,setInterval:()=>0,initDiscovery:async()=>({translate(){},refreshButtons(){},refresh(){}})});
  await new vm.Script('(async()=>{'+script+'\n})()').runInContext(context);
  await tick();
  const get=id=>document.getElementById(id);
  const event=async(id,type='click')=>{get(id).dispatchEvent(new window.Event(type,{bubbles:true,cancelable:true}));await tick();};
  return {get,event,requests,state,storage,document,window,reloads,closed,layouts};
}

test('missing installation explains setup and disables download',async()=>{
  const p=await panel({reply:r=>r.action==='ping'?failure('HOST_MISSING'):undefined});
  assert.equal(p.get('setup').hidden,false);assert.match(p.get('setupText').textContent,/kurulmamış/);
  assert.equal(p.get('inspect').disabled,true);assert.equal(p.get('download').disabled,true);
  assert.equal(p.requests[0].launch,false);
  await p.event('connect');assert.equal(p.requests.at(-1).launch,true);
});
test('older desktop remains disconnected with an explicit upgrade message',async()=>{
  const p=await panel({reply:r=>r.action==='ping'?success({version:'2.1',language:'tr'}):undefined});
  assert.match(p.get('setupText').textContent,/2\.2/);assert.equal(p.get('inspect').disabled,true);
});

test('current page, real qualities, audio mode and untrusted title render safely',async()=>{
  const p=await panel();await p.event('currentTab');await p.event('inspectForm','submit');
  assert.equal(p.get('preview').hidden,false);assert.equal(p.get('title').textContent,media().title);
  assert.equal(p.get('title').children.length,0);assert.equal(p.get('duration').textContent,'1:05');
  assert.deepEqual([...p.get('quality').options].map(o=>o.textContent),['720p','360p']);
  assert.equal(p.document.documentElement.dataset.theme,'dark');
  p.get('format').value='mp3';await p.event('format','change');
  assert.equal(p.get('quality').value,'mp3:0');assert.equal(p.get('quality').disabled,true);
  assert.match(p.get('warning').textContent,/dönüştürme/);
  await p.event('hold');const sent=p.requests.find(r=>r.action==='enqueue');
  assert.equal(sent.choice,'mp3:0');assert.equal(sent.hold,true);assert.equal(p.get('queueView').hidden,false);
});

test('lost enqueue acknowledgement reuses persisted request ID after reconnect',async()=>{
  let attempts=0;
  const p=await panel({reply:r=>r.action==='enqueue'?++attempts===1?failure('HOST_DISCONNECTED'):success({jobId:'same-job',duplicate:true}):undefined});
  await p.event('currentTab');await p.event('inspectForm','submit');await p.event('download');
  const first=p.requests.find(r=>r.action==='enqueue');assert.equal(p.state.session.draft.submission.requestId,first.requestId);
  await p.event('connect');await p.event('download');
  assert.equal(p.requests.filter(r=>r.action==='enqueue')[1].requestId,first.requestId);
  assert.equal(p.state.session.draft.preview,null);assert.match(p.get('queueFeedback').textContent,/zaten/);
});

test('editing URL during inspection prevents a stale download choice',async()=>{
  let finish;
  const p=await panel({reply:r=>r.action==='inspect'?new Promise(resolve=>finish=resolve):undefined});
  await p.event('currentTab');await p.event('inspectForm','submit');
  p.get('url').value='https://example.com/another';await p.event('url','input');
  finish(success(media()));await tick();
  assert.equal(p.get('preview').hidden,true);assert.equal(p.get('download').disabled,true);
});

test('worker inspection result restores a reopened panel without changing URL',async()=>{
  const url='https://example.com/video';
  const p=await panel({draft:{url,preview:null}});
  await p.storage.session.set({draft:{url,preview:media(),inspectedAt:Date.now()}});await tick();
  assert.equal(p.get('preview').hidden,false);assert.equal(p.get('quality').value,'mp4:720');assert.equal(p.get('download').disabled,false);
});

test('saved language and queue actions remain usable',async()=>{
  let paused=false;
  const p=await panel({language:'en',reply:r=>{
    if(r.action==='pause'){paused=true;return success({jobId:'q1'});}
    if(r.action==='queue')return success({total:1,active:paused?0:1,items:[{id:'q1',title:'Film',state:paused?'paused':'downloading',stateText:paused?'Paused':'Downloading',detail:'1 MB/s',error:'',progress:43,canPause:!paused,canResume:paused}]});
  }});
  assert.equal(p.get('download').textContent,'Download');await p.event('queueTab');
  const job=p.get('jobs').firstElementChild;assert.equal(job.querySelector('progress').value,43);
  const pause=[...job.querySelectorAll('button')].find(b=>b.textContent==='Pause');
  pause.dispatchEvent(new p.window.Event('click'));await tick();
  assert.equal(p.requests.at(-2).action,'pause');assert.equal(p.requests.at(-2).jobId,'q1');
  assert.equal(job.querySelector('progress').hidden,true);assert.equal([...job.querySelectorAll('button')].find(b=>b.textContent==='Resume').hidden,false);
});

test('queue rows carry their state and the running percentage in the numeral cell',async()=>{
  const items=[
    {id:'a',title:'Running',state:'downloading',stateText:'Downloading',detail:'',error:'',progress:62.4,canPause:true},
    {id:'b',title:'Waiting',state:'queued',stateText:'Queued',detail:'',error:''},
    {id:'c',title:'Held',state:'paused',stateText:'Paused',detail:'',error:'',progress:40,canResume:true},
    {id:'d',title:'Done',state:'completed',stateText:'Completed',detail:'',error:''},
    {id:'e',title:'Broken',state:'failed',stateText:'Failed',detail:'',error:'Gone',canRetry:true}];
  const p=await panel({reply:r=>r.action==='queue'?success({total:5,active:1,items}):undefined});
  await p.event('queueTab');
  const jobs=[...p.get('jobs').children];
  assert.deepEqual(jobs.map(j=>j.dataset.state),items.map(i=>i.state));
  assert.deepEqual(jobs.map(j=>j.querySelector('.figure')?.textContent),['62%','','','','']);
});
test('a panel opened as a tab uses the page it was opened for',async()=>{
  const p=await panel({search:'?tab=42'});await p.event('currentTab');
  assert.equal(p.get('url').value,'https://example.com/page-42');assert.equal(p.get('feedback').hidden,true);
});
test('durations of an hour or more show hours',async()=>{
  const long={...media(),duration:3725};
  const p=await panel({reply:request=>request.action==='inspect'?success(long):undefined});
  p.get('url').value='https://example.com/long';await p.event('inspectForm','submit');
  assert.equal(p.get('duration').textContent,'1:02:05');
});
test('a link arriving while connecting keeps the download view',async()=>{
  const p=await panel({reply:async(request,storage)=>{if(request.action==='ping')await storage.session.set({draft:{url:'https://example.com/late'}});}});
  await tick();
  assert.equal(p.get('url').value,'https://example.com/late');
  assert.equal(p.get('downloadView').hidden,false);assert.equal(p.get('mediaView').hidden,true);
});
test('the numeral cell is hidden from screen readers and shrinks at 100%',async()=>{
  const job={id:'j',title:'T',state:'processing',stateText:'',detail:'',error:'',progress:100};
  const p=await panel({reply:request=>request.action==='queue'?success({items:[job],total:1,active:1}):undefined});
  const figure=p.document.querySelector('.figure');
  assert.equal(figure.getAttribute('aria-hidden'),'true');assert.equal(figure.textContent,'100%');assert.ok(figure.classList.contains('full'));
});
test('the desktop language and theme are saved for the setup page',async()=>{
  const p=await panel();assert.equal(p.state.local.desktopLanguage,'tr');assert.equal(p.state.local.desktopTheme,'dark');
});

test('the panel reports its version and offers an update when Flow packages a newer one',async()=>{
  const newer=await panel({reply:r=>r.action==='ping'?success({language:'tr',capabilities:['image-original'],extension:{expected:'1.10.0',reload:false}}):undefined});
  assert.equal(newer.requests[0].ext,'1.2.0');
  assert.equal(newer.get('extUpdate').hidden,false);assert.match(newer.get('extUpdateText').textContent,/1\.2\.0.*1\.10\.0/);
  await newer.event('extReload');assert.deepEqual(newer.reloads,['reload']);
  for(const expected of ['1.2.0','1.1.9',null]){
    const same=await panel({reply:r=>r.action==='ping'?success({language:'tr',capabilities:['image-original'],extension:{expected,reload:false}}):undefined});
    assert.equal(same.get('extUpdate').hidden,true,String(expected));assert.equal(same.reloads.length,0);
  }
  const old=await panel({reply:r=>r.action==='ping'?success({language:'tr',capabilities:['image-original']}):undefined});
  assert.equal(old.get('extUpdate').hidden,true,'an older Flow without version data shows nothing');
});
test('a reload Flow asked for is carried out when the panel connects',async()=>{
  const p=await panel({reply:r=>r.action==='ping'?success({language:'tr',capabilities:['image-original'],extension:{expected:'1.3.0',reload:true}}):undefined});
  assert.deepEqual(p.reloads,['reload']);
});

test('in the side panel the footer button hands the switch to the worker, which closes the panel first',async()=>{
  const opened=[];
  const p=await panel({api:{extra:{action:{openPopup:async()=>opened.push('popup')}}}});
  assert.equal(p.get('sidePanel').textContent,'Açılır pencereye geç');
  await p.event('sidePanel');
  assert.equal(p.layouts.length,1);assert.equal(p.layouts[0].to,'popup');assert.equal(p.layouts[0].windowId,7);
  assert.deepEqual(opened,[],'the page itself never opens the popup (it would close again at once)');assert.deepEqual(p.closed,[]);
});
test('if the worker cannot switch, the side panel opens Flow in a tab and closes',async()=>{
  const created=[];
  const p=await panel({api:{layoutReply:null,extra:{tabs:{query:async()=>[{id:12,url:'https://example.com/video'}],create:async o=>created.push(o.url),get:async id=>({id})}}}});
  await p.event('sidePanel');
  assert.equal(created.length,1);assert.match(created[0],/panel\.html\?tab=12$/);assert.deepEqual(p.closed,['window']);
});
test('the side panel knows it is the side panel while the popup that opened it is still open',async()=>{
  const both=async()=>[{contextType:'POPUP',documentUrl:'chrome-extension://id/panel.html'},{contextType:'SIDE_PANEL',documentUrl:'chrome-extension://id/panel.html'}];
  const side=await panel({search:'',api:{runtime:{getContexts:both},extra:{extension:{getViews:()=>({includes:()=>false})}}}});
  assert.equal(side.get('sidePanel').textContent,'Açılır pencereye geç');assert.equal(side.document.body.classList.contains('popup'),false);
  const popup=await panel({search:'',api:{runtime:{getContexts:both},extra:{extension:{getViews:()=>({includes:()=>true})}}}});
  assert.equal(popup.get('sidePanel').textContent,'Yan panel');assert.equal(popup.document.body.classList.contains('popup'),true);
});
test('a popup or a tab still offers the side panel',async()=>{
  const p=await panel({search:'?tab=5'});
  assert.equal(p.get('sidePanel').textContent,'Yan panel');
});
test('a link sent from the video button is inspected at once, once',async()=>{
  const draft={url:'https://example.com/movie.mp4',referrer:'https://example.com/page',title:'Movie',preview:null,autoInspect:true,requestedAt:1};
  const p=await panel({draft});
  assert.equal(p.requests.filter(r=>r.action==='inspect').length,1);
  assert.equal(p.requests.find(r=>r.action==='inspect').url,'https://example.com/movie.mp4');
  assert.equal(p.get('preview').hidden,false);assert.equal(p.get('downloadView').hidden,false);
  await p.storage.session.set({draft:{...draft}});await new Promise(r=>setTimeout(r,20));
  assert.equal(p.requests.filter(r=>r.action==='inspect').length,1,'the same request is not fetched twice');
  await p.storage.session.set({draft:{...draft,url:'https://example.com/next.mp4',requestedAt:2}});await new Promise(r=>setTimeout(r,50));
  assert.equal(p.requests.filter(r=>r.action==='inspect').length,2,'a new click while the panel is open is fetched too');
});
test('a draft typed or restored without the button is not inspected by itself',async()=>{
  const p=await panel({draft:{url:'https://example.com/movie.mp4',preview:null}});
  assert.equal(p.requests.filter(r=>r.action==='inspect').length,0);
});

test('the queue page opens the download folder through Flow',async()=>{
  const p=await panel({reply:r=>r.action==='ping'?success({language:'tr',folder:'C:\Downloads\Flow',capabilities:['image-original','open-folder']}):r.action==='folder'?success({opened:true}):undefined});
  await p.event('queueTab');
  assert.equal(p.get('openFolder').hidden,false);assert.equal(p.get('openFolder').textContent,'Klasörü aç');assert.equal(p.get('openFolder').title,'C:\Downloads\Flow');
  await p.event('openFolder');
  const sent=p.requests.find(r=>r.action==='folder');assert.ok(sent);assert.equal(Object.keys(sent).sort().join(),'action,v','no path is sent');
  assert.equal(p.get('queueFeedback').hidden,true);
});
test('a missing folder is explained, and an older Flow shows no folder button',async()=>{
  const p=await panel({reply:r=>r.action==='ping'?success({language:'tr',capabilities:['image-original','open-folder']}):r.action==='folder'?failure('FOLDER_MISSING'):undefined});
  await p.event('queueTab');await p.event('openFolder');
  assert.equal(p.get('queueFeedback').hidden,false);assert.match(p.get('queueFeedback').textContent,/henüz yok/);
  const old=await panel();
  assert.equal(old.get('openFolder').hidden,true);
});
