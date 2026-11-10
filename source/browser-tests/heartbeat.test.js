import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../chrome-extension/background.js',import.meta.url),'utf8').replace(/^import[^\n]*\n/gm,'').replace(/^export /gm,'');
const settle=async()=>{for(let i=0;i<10;i++)await new Promise(resolve=>setImmediate(resolve));};
// The service worker with chrome.alarms; callNative answers with the given ping data.
function worker({data={},ok=true,existing=null,granted=false,side={},popup=true}={}){
  const events={},calls=[],alarms=[],reloads=[],local={},network=new Set(),session={},opened=[],steps=[];
  const listen=name=>({addListener:f=>events[name]=f});
  const chrome={runtime:{id:'flow',getURL:()=>'chrome-extension://flow/panel.html',getManifest:()=>({version:'1.2.0'}),reload:()=>reloads.push(1),onInstalled:listen('install'),onStartup:listen('startup'),onMessage:listen('message')},
    alarms:{get:async name=>existing,create:(name,info)=>alarms.push([name,info]),onAlarm:listen('alarm')},
    scripting:{unregisterContentScripts:async()=>{}},permissions:{contains:async()=>granted,onAdded:listen('permissionAdded'),onRemoved:listen('permissionRemoved')},webRequest:{onHeadersReceived:{addListener:f=>network.add(f),removeListener:f=>network.delete(f),hasListener:f=>network.has(f)}},webNavigation:{onCommitted:listen('navigation'),onHistoryStateUpdated:listen('history')},
    contextMenus:{onClicked:listen('click'),removeAll:async()=>{},create:()=>{}},sidePanel:{open:async o=>opened.push(o),...side},action:popup?{openPopup:async o=>steps.push(['popup',o.windowId])}:{},tabs:{create:async o=>steps.push(['tab',o.url]),query:async()=>[{id:21}],onRemoved:listen('removed')},
    storage:{onChanged:listen('storage'),local:{get:async()=>({}),set:async v=>Object.assign(local,v)},session:{get:async()=>({...session}),set:async v=>Object.assign(session,v)}}};
  const control={set granted(v){granted=v;}};
  class Discovery{constructor(){this.ready=Promise.resolve();this.tabs={};this.config={};}serial(f){return Promise.resolve().then(f);}syncRegistration(){return Promise.resolve();}}
  vm.runInNewContext(source,{chrome,Discovery,setTimeout,candidate:(raw,page)=>raw&&{...raw,pageUrl:page},webUrl:x=>x,callNative:async(api,r)=>{calls.push(r);return ok?{v:1,ok:true,data}:{v:1,ok:false,error:{code:'APP_UNAVAILABLE'}};}});
  return {events,calls,alarms,reloads,local,network,session,opened,control,steps};
}
test('a minute alarm is created once and never restarted on wake-up',async()=>{
  const fresh=worker();await settle();
  assert.equal(JSON.stringify(fresh.alarms),JSON.stringify([['flow-heartbeat',{periodInMinutes:1}]]));
  const running=worker({existing:{name:'flow-heartbeat'}});await settle();
  assert.equal(running.alarms.length,0);
});
test('the heartbeat reports the version without launching Flow and stores what Flow expects',async()=>{
  const w=worker({data:{extension:{expected:'1.3.0',reload:false}}});
  w.events.alarm({name:'flow-heartbeat'});await settle();
  assert.equal(w.calls.length,1);
  assert.equal(w.calls[0].action,'ping');assert.equal(w.calls[0].ext,'1.2.0');assert.equal(w.calls[0].launch,undefined);
  assert.equal(JSON.stringify(w.local.desktopExtension),JSON.stringify({expected:'1.3.0',reload:false}));assert.equal(w.reloads.length,0);
  w.events.alarm({name:'something-else'});await settle();assert.equal(w.calls.length,1);
});
test('a reload Flow asked for reloads the extension; Flow being closed changes nothing',async()=>{
  const w=worker({data:{extension:{expected:'1.3.0',reload:true}}});
  w.events.alarm({name:'flow-heartbeat'});await settle();assert.equal(w.reloads.length,1);
  const closed=worker({ok:false});
  closed.events.alarm({name:'flow-heartbeat'});await settle();
  assert.equal(closed.reloads.length,0);assert.equal(closed.local.desktopExtension,undefined);
});
test('starting the browser and installing or reloading the extension check in at once',async()=>{
  const w=worker();w.events.startup();w.events.install();await settle();
  assert.equal(w.calls.filter(c=>c.action==='ping').length,2);
});

test('network responses are watched only once site access is granted, so Chrome lists no warning',async()=>{
  const w=worker();await settle();
  assert.equal(w.network.size,0,'no webRequest listener without site access');
  w.control.granted=true;w.events.permissionAdded({origins:['http://*/*','https://*/*']});await settle();
  assert.equal(w.network.size,1,'listener added after the grant');
  w.events.permissionAdded({origins:['http://*/*','https://*/*']});await settle();assert.equal(w.network.size,1,'never added twice');
  w.control.granted=false;w.events.permissionRemoved({origins:['http://*/*']});await settle();
  assert.equal(w.network.size,0,'listener removed with the grant');
  const already=worker({granted:true});await settle();assert.equal(already.network.size,1,'a granted browser watches from the start');
});
test('the video button and the context menu ask the panel to fetch details at once',async()=>{
  const w=worker(),page='https://example.com/page';
  w.events.message({type:'scan-open',pageUrl:page,item:{url:'https://example.com/movie.mp4',kind:'video',title:'Movie'}},{id:'flow',url:page,frameId:0,tab:{id:4,url:page}},()=>{});
  await settle();
  assert.equal(w.session.draft.url,'https://example.com/movie.mp4');assert.equal(w.session.draft.autoInspect,true);assert.equal(typeof w.session.draft.requestedAt,'number');
  w.events.click({menuItemId:'flow-send-link',linkUrl:'https://example.com/other'},{id:5});await settle();
  assert.equal(w.session.draft.url,'https://example.com/other');assert.equal(w.session.draft.autoInspect,true);
});

const later=()=>new Promise(r=>setTimeout(r,80));
const fromPanel={id:'flow',url:'chrome-extension://flow/panel.html'};
test('switching to the popup closes the side panel first, then opens the popup',async()=>{
  const w=worker({side:{close:async o=>w.steps.push(['close',o.windowId])}});let answer;
  assert.equal(w.events.message({type:'layout',to:'popup',windowId:3},fromPanel,r=>answer=r),false);
  assert.equal(answer.ok,true,'answered before the panel is closed');
  await later();
  assert.equal(JSON.stringify(w.steps),JSON.stringify([['close',3],['popup',3]]));
});
test('without sidePanel.close the panel is switched off and on; without a popup Flow opens in a tab',async()=>{
  const options=[];
  const w=worker({popup:false,side:{setOptions:async o=>options.push(o)}});
  w.events.message({type:'layout',to:'popup',windowId:3},fromPanel,()=>{});await later();
  assert.equal(JSON.stringify(options),JSON.stringify([{tabId:21,enabled:false},{tabId:21,enabled:true,path:'panel.html'},{enabled:false},{enabled:true}]));
  assert.equal(JSON.stringify(w.steps),JSON.stringify([['tab','chrome-extension://flow/panel.html?tab=21']]));
});
test('only the packaged panel may switch the layout',async()=>{
  const w=worker({side:{close:async o=>w.steps.push(['close',o.windowId])}});
  assert.equal(w.events.message({type:'layout',to:'popup',windowId:3},{id:'flow',url:'https://example.com/'},()=>{}),false);
  await later();assert.equal(w.steps.length,0);
});
