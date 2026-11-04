import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {Discovery} from '../../chrome-extension/discovery.js';
import {candidate,webUrl} from '../../chrome-extension/detection.js';
const source=readFileSync(new URL('../../chrome-extension/background.js',import.meta.url),'utf8').replace(/^import[^\n]*\n/gm,'').replace(/^export /gm,'');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
function worker(){
  const events={},calls=[],stored={},order=[];
  const listen=name=>({addListener:f=>events[name]=f});
  const panel='chrome-extension://flow/panel.html';
  const chrome={runtime:{id:'flow',getURL:()=>panel,onInstalled:listen('install'),onStartup:listen('startup'),onMessage:listen('message')},
    scripting:{unregisterContentScripts:async()=>{}},permissions:{contains:async()=>false,onRemoved:listen('permissionRemoved')},webRequest:{onHeadersReceived:listen('network')},webNavigation:{onCommitted:listen('navigation'),onHistoryStateUpdated:listen('history')},
    contextMenus:{onClicked:listen('click'),removeAll:async()=>{},create:()=>{}},sidePanel:{open:async options=>{order.push(['open',options]);}},tabs:{create:async options=>order.push(['tab',options]),onRemoved:listen('removed')},
    storage:{onChanged:listen('storage'),local:{get:async()=>({})},session:{get:async()=>structuredClone(stored),set:async values=>{Object.assign(stored,structuredClone(values));order.push(['store']);}}}};
  vm.runInNewContext(source,{chrome,Discovery,candidate,webUrl,callNative:async(api,r)=>{calls.push(r);return {v:1,ok:true,data:{token:'fresh'}};}});
  return {events,calls,stored,order,panel,chrome};
}
test('only the packaged panel can forward native commands',async()=>{
  const w=worker(),message={type:'native',request:{v:1,action:'ping'}};
  for(const sender of [{id:'other',url:w.panel},{id:'flow',url:'https://example.com/'},{id:'flow',url:w.panel+'.evil'}])assert.equal(w.events.message(message,sender,()=>{}),false);
  assert.equal(w.calls.length,0);
  const result=await new Promise(resolve=>assert.equal(w.events.message(message,{id:'flow',url:w.panel},resolve),true));
  assert.equal(result.ok,true);assert.equal(w.calls.length,1);
});
test('context click opens within the gesture then stores the selected link',()=>{
  const w=worker();w.events.click({menuItemId:'flow-send-link',linkUrl:'https://example.com/selected',pageUrl:'https://example.com/page'},{id:9});
  assert.equal(w.order[0][0],'open');assert.equal(w.order[0][1].tabId,9);
  assert.equal(w.stored.draft.url,'https://example.com/selected');assert.equal(w.stored.draft.preview,null);assert.equal(w.calls.length,0);
});
test('completed inspection survives panel closure but never replaces a newer draft',async()=>{
  const w=worker(),url='https://example.com/video';w.stored.draft={url};
  const message={type:'native',request:{v:1,action:'inspect',url}};
  w.events.message(message,{id:'flow',url:w.panel},()=>{});await settle();
  assert.equal(w.stored.draft.preview.token,'fresh');
  w.stored.draft={url:'https://example.com/new'};
  w.events.message(message,{id:'flow',url:w.panel},()=>{});await settle();
  assert.equal(w.stored.draft.preview,undefined);
});
test('Opera-style missing sidePanel falls back to a tab and retains source tab ID',()=>{
  const w=worker();delete w.chrome.sidePanel;
  w.events.click({menuItemId:'flow-send-link',linkUrl:'https://example.com/video'},{id:9});
  assert.equal(w.order[0][0],'tab');assert.equal(w.order[0][1].url,w.panel+'?tab=9');assert.equal(w.stored.draft.url,'https://example.com/video');
});
