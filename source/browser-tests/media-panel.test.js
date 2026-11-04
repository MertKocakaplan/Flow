import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {parseHTML} from 'linkedom';
import {translator,errorText} from '../../chrome-extension/i18n.js';
const html=readFileSync(new URL('../../chrome-extension/panel.html',import.meta.url),'utf8');
const script=readFileSync(new URL('../../chrome-extension/media-panel.js',import.meta.url),'utf8').replace(/^import[^\n]*\n/gm,'').replace(/^export /gm,'');
const tick=async()=>{for(let i=0;i<10;i++)await new Promise(r=>setImmediate(r));};
async function fixture(){
  const {document,window}=parseHTML(html);const calls=[],opened=[],events=[];let grants=0;
  for(const select of document.querySelectorAll('select'))Object.defineProperty(select,'value',{get(){return [...this.options].find(o=>o.selected)?.value??this.options[0]?.value??'';},set(value){for(const o of this.options)o.removeAttribute('selected');const match=[...this.options].find(o=>o.value===value);if(match)match.selected=true;}});
  const state={config:{enabled:false,overlays:true},page:{url:'https://example.com/watch',items:[
    {id:'v',url:'https://cdn.example.com/a.mp4',pageUrl:'https://example.com/watch',kind:'video',source:'element',title:'<b>Video</b>',width:1280,height:720},
    {id:'i',url:'https://cdn.example.com/a.png',pageUrl:'https://example.com/watch',kind:'image',source:'element',title:'Photo'},
    {id:'p',url:'https://cdn.example.com/protected.mp4',pageUrl:'https://example.com/watch',kind:'video',source:'element',title:'Protected',protected:true}
  ]},batch:null};
  const chrome={tabs:{query:async()=>[{id:9}],onActivated:{addListener:()=>{}},create:async r=>opened.push(r)},permissions:{request:async()=>{grants++;return true;},remove:async()=>true},storage:{onChanged:{addListener:f=>events.push(f)}},runtime:{sendMessage:async r=>{calls.push(r);if(r.action==='snapshot')return {ok:true,data:structuredClone(state)};if(r.action==='configure'){state.config={enabled:r.enabled,overlays:r.overlays};return {ok:true,data:state.config};}if(r.action==='select')return {ok:true,data:{url:state.page.items.find(i=>i.id===r.id).url}};if(r.action==='batch'){state.batch={state:'running',items:r.ids.map(id=>({title:id,state:'waiting'}))};return {ok:true,data:state.batch};}return {ok:true,data:{}};}}};
  const context=vm.createContext({document,chrome,location:{search:''},URL,URLSearchParams,errorText,ALL_SITES:['http://*/*','https://*/*'],setInterval:()=>0});
  vm.runInContext(script,context);await context.initDiscovery({translate:translator('en'),isOnline:()=>true,openDraft:async d=>opened.push(d)});
  const get=id=>document.getElementById(id),event=async(id,type='click')=>{get(id).dispatchEvent(new window.Event(type,{bubbles:true}));await tick();};
  return {document,window,get,event,calls,opened,state,events,get grants(){return grants;}};
}
test('media list filters types, counts all detections and leaves protected media unselectable',async()=>{
  const p=await fixture();assert.equal(p.get('mediaCount').textContent,'3');
  const rows=[...p.get('mediaItems').children];assert.equal(rows[0].querySelector('h2').textContent,'<b>Video</b>');assert.equal(rows[0].querySelector('h2').children.length,0);assert.equal(rows[1].hidden,true);assert.equal(rows[2].querySelector('input').disabled,true);
  p.get('mediaFilter').value='image';await p.event('mediaFilter','change');assert.equal(rows[0].hidden,true);assert.equal(rows[1].hidden,false);
});
test('explicit media preview, selection and batch hold send only the selected IDs',async()=>{
  const p=await fixture();const row=p.get('mediaItems').firstElementChild;
  assert.equal(row.querySelector('video'),null);const details=row.querySelector('details');details.open=true;details.dispatchEvent(new p.window.Event('toggle'));assert.equal(row.querySelector('video').src,'https://cdn.example.com/a.mp4');
  p.get('selectAll').checked=true;await p.event('selectAll','change');assert.equal(p.get('selectedCount').textContent,'1 / 20 selected');
  p.get('batchHold').checked=true;await p.event('sendSelected');const request=p.calls.find(r=>r.action==='batch');assert.deepEqual([...request.ids],['v']);assert.equal(request.hold,true);assert.equal(p.get('batchSection').hidden,false);assert.equal(p.get('sendSelected').disabled,true);
});
test('automatic scanning asks optional permission and can be disabled',async()=>{
  const p=await fixture();await p.event('autoScan');assert.equal(p.grants,1);assert.equal(p.state.config.enabled,true);assert.match(p.get('autoScan').textContent,/Disable/);await p.event('autoScan');assert.equal(p.state.config.enabled,false);assert.equal(p.grants,1);
});
