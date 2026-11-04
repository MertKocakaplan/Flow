import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {parseHTML} from 'linkedom';
const core=readFileSync(new URL('../../chrome-extension/detection.js',import.meta.url),'utf8').replace(/^export /gm,'');
const body=readFileSync(new URL('../../chrome-extension/content-body.js',import.meta.url),'utf8');
const tick=()=>new Promise(r=>setImmediate(r));
test('content scanner attaches one accessible overlay and stops when disabled',async()=>{
  const {document,window}=parseHTML('<html><head><title>Demo</title></head><body><video src="https://cdn.example.com/a.mp4"></video></body></html>');
  document.querySelector('video').getBoundingClientRect=()=>({top:30,right:650,width:640,height:360,bottom:390});
  let rootShadow,receive;const messages=[];
  const create=document.createElement.bind(document);document.createElement=(tag)=>{const el=create(tag);if(tag==='div'){const attach=el.attachShadow.bind(el);el.attachShadow=options=>rootShadow=attach(options);}return el;};
  const chrome={runtime:{sendMessage:async message=>{messages.push(message);return {enabled:true,overlays:true,language:'en'};},onMessage:{addListener:f=>receive=f}}};
  const context=vm.createContext({window,document,chrome,URL,location:{href:'https://example.com/watch'},performance:{getEntriesByType:()=>[]},innerWidth:1000,innerHeight:700,MutationObserver:window.MutationObserver,setTimeout:()=>1,clearTimeout:()=>{},addEventListener:()=>{},removeEventListener:()=>{}});
  const script=new vm.Script('(()=>{'+core+'\n'+body+'\n})();');script.runInContext(context);await tick();
  assert.ok(messages.some(m=>m.type==='scan-found'&&m.items[0].url.endsWith('a.mp4')));const button=rootShadow.querySelector('button');assert.equal(button.textContent,'Download with Flow');assert.equal(button.hidden,false);assert.equal(button.type,'button');
  button.dispatchEvent(new window.Event('click'));assert.ok(!messages.some(m=>m.type==='scan-open'),'scripted page clicks are not user gestures');
  script.runInContext(context);assert.equal(document.querySelectorAll('#flow-media-tools').length,1);
  receive({type:'scan-config',enabled:false});assert.equal(document.getElementById('flow-media-tools'),null);
  receive({type:'scan-config',enabled:true,overlays:false,language:'tr'});assert.equal(button.hidden,true);
  receive({type:'scan-config',enabled:true,overlays:true,language:'tr'});assert.equal(button.textContent,'Flow ile indir');assert.equal(button.hidden,false);
});
test('resource and lazy-image scanning starts only when enabled and excludes prior-page timings',async()=>{
  const {document,window}=parseHTML('<html><head><title>Resources</title></head><body></body></html>');
  delete window.__flowScanner; // Linkedom window globals share the Node global object between fixtures.
  const location={href:'https://example.com/first'},messages=[],timers=new Map();
  let receive,resourceCallback,observing=false,now=20,nextTimer=0;
  const resources=[{name:'https://cdn.example.com/old.mp4',startTime:10}];
  class ResourceObserver {
    constructor(callback){resourceCallback=callback;}
    observe(){observing=true;}
    disconnect(){observing=false;}
  }
  const chrome={runtime:{sendMessage:async message=>{messages.push(message);return {enabled:false};},onMessage:{addListener:f=>receive=f}}};
  const context=vm.createContext({window,document,chrome,URL,location,performance:{getEntriesByType:()=>resources,now:()=>now},PerformanceObserver:ResourceObserver,innerWidth:1000,innerHeight:700,MutationObserver:window.MutationObserver,setTimeout:f=>{const id=++nextTimer;timers.set(id,f);return id;},clearTimeout:id=>timers.delete(id),addEventListener:()=>{},removeEventListener:()=>{}});
  new vm.Script('(()=>{'+core+'\n'+body+'\n})();').runInContext(context);await tick();
  assert.equal(observing,false);assert.equal(messages.some(m=>m.type==='scan-found'),false);
  receive({type:'scan-config',enabled:true,overlays:false});
  assert.equal(observing,true);assert.equal(messages.at(-1).items[0].url,resources[0].name);
  location.href='https://example.com/second';now=100;receive({type:'scan-now'});
  assert.equal(messages.at(-1).items.length,0,'old page resource timings do not repopulate the new list');
  resources.push({name:'https://cdn.example.com/new.mp4',startTime:110});resourceCallback();
  assert.equal(timers.size,1);for(const [id,f] of [...timers]){timers.delete(id);f();}
  assert.deepEqual(Array.from(messages.at(-1).items,x=>x.url),['https://cdn.example.com/new.mp4']);
  const img=document.createElement('img');img.src='https://cdn.example.com/lazy.jpg';document.body.append(img);img.dispatchEvent(new window.Event('load',{bubbles:true}));
  assert.equal(timers.size,1,'image load schedules detection even when the responsive source changes');
  receive({type:'scan-config',enabled:false});assert.equal(observing,false);assert.equal(timers.size,0);
  resourceCallback();img.dispatchEvent(new window.Event('load',{bubbles:true}));assert.equal(timers.size,0,'disabled scanners ignore further resource and image events');
});
