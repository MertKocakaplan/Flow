import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {callNative, connectionError, validRequest} from '../../chrome-extension/transport.js';
function mock(reply, disconnectError) {
  let received, disconnected = false;
  const listeners = {};
  const api = {runtime:{lastError:null,connectNative(name){ assert.equal(name,'com.flow.downloader'); return {
    onMessage:{addListener:f=>listeners.message=f}, onDisconnect:{addListener:f=>listeners.disconnect=f},
    disconnect(){disconnected=true;}, postMessage(request){received=request; queueMicrotask(()=>{
      if(disconnectError){api.runtime.lastError={message:disconnectError};listeners.disconnect();}
      else if(reply!==undefined)listeners.message(reply);
    });}
  };}}};
  return {api,get received(){return received;},get disconnected(){return disconnected;}};
}
test('manifest keeps its stable ID; automatic site access requires an optional grant',()=>{
  const m=JSON.parse(readFileSync(new URL('../../chrome-extension/manifest.json',import.meta.url)));
  const id=createHash('sha256').update(Buffer.from(m.key,'base64')).digest('hex').slice(0,32).replace(/[0-9a-f]/g,c=>String.fromCharCode(97+parseInt(c,16)));
  assert.equal(id,'eidibhbenooohggnhdeblgfdclahhdnd'); assert.equal(m.manifest_version,3);
  assert.deepEqual(m.permissions.sort(),['activeTab','contextMenus','nativeMessaging','sidePanel','storage','scripting','webRequest','webNavigation'].sort());
  assert.deepEqual(m.optional_host_permissions,['http://*/*','https://*/*']);assert.equal(m.permissions.includes('cookies'),false);
  assert.equal(m.host_permissions,undefined); assert.equal(m.content_scripts,undefined); assert.equal(m.externally_connectable,undefined);
});
test('Opera package keeps identity and excludes Chrome-specific sidebar declarations',()=>{
  const opera=JSON.parse(readFileSync(new URL('../../opera-extension/manifest.json',import.meta.url)));
  const chrome=JSON.parse(readFileSync(new URL('../../chrome-extension/manifest.json',import.meta.url)));
  assert.equal(opera.key,chrome.key);assert.equal(opera.version,chrome.version);assert.equal(opera.side_panel,undefined);assert.equal(opera.permissions.includes('sidePanel'),false);
  for(const file of ['background.js','discovery.js','media-panel.js','panel.js','scan-content.js'])assert.equal(readFileSync(new URL('../../opera-extension/'+file,import.meta.url),'utf8'),readFileSync(new URL('../../chrome-extension/'+file,import.meta.url),'utf8'));
});
test('only versioned, bounded, known actions cross the bridge',()=>{
  assert.equal(validRequest({v:1,action:'inspect',url:'https://example.com/video'}),true);
  assert.equal(validRequest({v:2,action:'ping'}),false);
  assert.equal(validRequest({v:1,action:'exec',command:'anything'}),false);
  assert.equal(validRequest({v:1,action:'inspect',url:'x'.repeat(8193)}),false);
});
test('response acknowledged, port closed, request unchanged',async()=>{
  const answer={v:1,ok:true,data:{jobId:'job'}};const m=mock(answer);
  const request={v:1,action:'enqueue',requestId:'persistent-id',token:'preview',choice:'mp4:720'};
  assert.deepEqual(await callNative(m.api,request),answer); assert.deepEqual(m.received,request); assert.equal(m.disconnected,true);
});
test('missing host produces actionable installation state',async()=>{
  const m=mock(undefined,'Specified native messaging host not found.');
  assert.equal((await callNative(m.api,{v:1,action:'ping'})).error.code,'HOST_MISSING');
  assert.equal(connectionError('Access to the specified native messaging host is forbidden.'),'HOST_FORBIDDEN');
});
test('old host protocol does not masquerade as a successful connection',async()=>{
  const m=mock({ok:true});assert.equal((await callNative(m.api,{v:1,action:'ping'})).error.code,'PROTOCOL_VERSION');
});
test('unresponsive host times out and releases its port',async()=>{
  const m=mock();assert.equal((await callNative(m.api,{v:1,action:'ping'},5)).error.code,'TIMEOUT');assert.equal(m.disconnected,true);
});
