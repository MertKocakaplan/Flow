import {callNative} from './transport.js';
import {Discovery} from './discovery.js';
import {candidate,webUrl} from './detection.js';

const panelUrl = chrome.runtime.getURL('panel.html');
const menuId = 'flow-send-link';
export function openPanel(tabId) {
  const fallback=()=>chrome.tabs.create({url:panelUrl+'?tab='+tabId});
  try { if(chrome.sidePanel?.open)return chrome.sidePanel.open({tabId}).catch(fallback); } catch {}
  return fallback();
}
// From the side panel to the toolbar popup. The panel closes first, because a popup that opens while focus is still
// moving closes at once. Browsers without sidePanel.close close it by switching it off and on again; where the popup
// cannot be opened by the extension, Flow opens in a tab instead.
export async function sidePanelToPopup(windowId) {
  const [active] = await chrome.tabs.query({active:true, windowId}).catch(() => []);
  try {
    if (chrome.sidePanel?.close) await chrome.sidePanel.close({windowId});
    else {
      if (active?.id != null) { await chrome.sidePanel.setOptions({tabId:active.id, enabled:false}); await chrome.sidePanel.setOptions({tabId:active.id, enabled:true, path:'panel.html'}); }
      await chrome.sidePanel.setOptions({enabled:false}); await chrome.sidePanel.setOptions({enabled:true});
    }
  } catch {}
  try { if (!chrome.action?.openPopup) throw Error('no popup'); await chrome.action.openPopup({windowId}); }
  catch { await chrome.tabs.create({windowId, url:panelUrl+(active?.id!=null?'?tab='+active.id:'')}); }
}
const discovery=new Discovery(chrome,openPanel);
const backgroundWork=fn=>discovery.serial(fn).catch(()=>{});
// Network responses are only watched once the user granted site access for automatic scanning. Without that grant
// Chrome would not deliver them anyway and lists a warning on the extensions page; a manual scan reads the page's
// own resource list instead.
const watchedTypes={urls:['http://*/*','https://*/*'],types:['media','xmlhttprequest','image','other']};
function onNetwork(details){backgroundWork(()=>discovery.network(details));}
function watchNetwork(on){
  const event=chrome.webRequest?.onHeadersReceived;if(!event)return;
  const listening=event.hasListener?.(onNetwork)===true;
  if(on&&!listening)event.addListener(onNetwork,watchedTypes,['responseHeaders']);
  else if(!on&&listening)event.removeListener(onNetwork);
}
chrome.permissions.contains({origins:['http://*/*','https://*/*']}).then(watchNetwork).catch(()=>{});
chrome.permissions.onAdded?.addListener(()=>chrome.permissions.contains({origins:['http://*/*','https://*/*']}).then(watchNetwork).catch(()=>{}));
chrome.webNavigation.onCommitted.addListener(details=>backgroundWork(()=>discovery.navigate(details)));
chrome.webNavigation.onHistoryStateUpdated.addListener(details=>backgroundWork(()=>discovery.navigate(details,true)));
chrome.tabs.onRemoved.addListener(tabId=>backgroundWork(async()=>{delete discovery.tabs[tabId];await discovery.persist();}));
chrome.permissions.onRemoved.addListener(()=>backgroundWork(async()=>{if(!await chrome.permissions.contains({origins:['http://*/*','https://*/*']})){watchNetwork(false);await discovery.configure(false,discovery.config.overlays);}}));
discovery.ready.then(()=>discovery.syncRegistration()).catch(()=>{});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==='local'&&changes.language)backgroundWork(async()=>{discovery.config.language=changes.language.newValue||'tr';for(const id of Object.keys(discovery.tabs)){try{await chrome.tabs.sendMessage(Number(id),{type:'scan-config',...discovery.config,enabled:discovery.config.enabled||discovery.tabs[id].manual});}catch{}}});});
async function createMenu() {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({id: menuId, title: 'Flow ile indir / Download with Flow', contexts: ['link', 'page', 'video', 'audio'], documentUrlPatterns: ['http://*/*', 'https://*/*']});
}
// Once a minute the extension tells Flow it is here and which version it is; Flow answers with the version in its
// package and, when the user asked for it, a request to reload from the (updated) folder. Flow is never started.
const heartbeatAlarm = 'flow-heartbeat';
export async function heartbeat() {
  const result = await callNative(chrome, {v: 1, action: 'ping', ext: chrome.runtime.getManifest?.().version, heartbeat: true}, 15000);
  if (!result?.ok) return result;
  await chrome.storage.local.set({desktopExtension: result.data?.extension || null}).catch(() => {});
  if (result.data?.extension?.reload === true) chrome.runtime.reload?.();
  return result;
}
function keepHeartbeat() {
  // Creating the alarm again would restart its period on every wake-up, so it is made only when missing.
  chrome.alarms?.get(heartbeatAlarm).then(alarm => { if (!alarm) chrome.alarms.create(heartbeatAlarm, {periodInMinutes: 1}); }).catch(() => {});
}
chrome.alarms?.onAlarm.addListener(alarm => { if (alarm.name === heartbeatAlarm) heartbeat().catch(() => {}); });
keepHeartbeat();
chrome.runtime.onInstalled.addListener(() => { createMenu().catch(() => {}); heartbeat().catch(() => {}); });
chrome.runtime.onStartup.addListener(() => { createMenu().catch(() => {}); heartbeat().catch(() => {}); });

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== menuId || tab?.id == null) return;
  // Keep the side-panel call in the original user gesture; do not await storage first.
  const opening = openPanel(tab.id);
  const url = info.linkUrl || info.pageUrl || tab.url || info.srcUrl || '';
  chrome.storage.session.set({draft: {url, preview: null, choice: null, submission: null, autoInspect: true, requestedAt: Date.now()}}).catch(() => {});
  opening.catch(() => {});
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if(sender.id!==chrome.runtime.id)return false;
  if(message?.type==='scan-open') {
    if(sender.tab?.id==null || !webUrl(sender.url))return false;
    // sidePanel.open only works inside the click's user gesture, so nothing is awaited before it.
    const item=sender.frameId===0 && sender.tab.url && webUrl(sender.url)!==webUrl(sender.tab.url)?null:candidate(message.item,sender.url);
    if(!item || item.protected){sendResponse({ok:false});return false;}
    const opening=openPanel(sender.tab.id);
    discovery.serial(async()=>{
      // The button means "this video": the panel fetches its details at once instead of waiting for a second click.
      await chrome.storage.session.set({draft:{url:item.url,referrer:item.pageUrl,title:item.title,preview:null,submission:null,autoInspect:true,requestedAt:Date.now()}});
      await opening;return {ok:true};
    }).then(sendResponse).catch(()=>sendResponse({ok:false}));return true;
  }
  if(['scan-found','scan-ready'].includes(message?.type)) {
    if(sender.tab?.id==null || !webUrl(sender.url))return false;
    discovery.serial(async()=>{
      if(message.type==='scan-found'){await discovery.found(message,sender);return {ok:true};}
      return {...discovery.config,enabled:discovery.config.enabled || discovery.tabs[sender.tab.id]?.manual===true};
    }).then(sendResponse).catch(()=>sendResponse({ok:false}));return true;
  }
  if (sender.id !== chrome.runtime.id || sender.url?.split('?')[0] !== panelUrl || sender.tab?.url && sender.tab.url.split('?')[0] !== panelUrl) return false;
  if(message?.type==='layout'&&message.to==='popup'&&Number.isInteger(message.windowId)) {
    // Answer first: the side panel that asked is closed next and could not receive a later answer.
    sendResponse({ok:true});
    setTimeout(()=>sidePanelToPopup(message.windowId).catch(()=>{}),30);
    return false;
  }
  if(message?.type==='discovery') {
    discovery.serial(async()=>{
      const tabId=Number(message.tabId);
      if(message.action==='snapshot'){if(discovery.batch?.state==='running'&&!discovery.running)discovery.runBatch().catch(()=>{});return discovery.snapshot(tabId);}
      if(message.action==='scan')return discovery.scan(tabId);
      if(message.action==='configure')return discovery.configure(message.enabled===true,message.overlays!==false);
      if(message.action==='select')return discovery.select(tabId,message.id);
      if(message.action==='batch')return discovery.startBatch(tabId,Array.isArray(message.ids)?message.ids.slice(0,21):[],message.hold);
      if(message.action==='stop'){if(discovery.batch){discovery.batch.stop=true;await discovery.saveBatch();}return discovery.batch;}
      if(message.action==='retry')return discovery.retryBatch();
      throw Error('INVALID_REQUEST');
    }).then(data=>sendResponse({ok:true,data})).catch(error=>sendResponse({ok:false,error:{code:error.message||'INVALID_REQUEST'}}));return true;
  }
  if (message?.type !== 'native') return false;
  callNative(chrome, message.request).then(async result => {
    if (message.request.action === 'inspect' && result.ok) {
      const {draft} = await chrome.storage.session.get('draft');
      if (draft?.url === message.request.url) await chrome.storage.session.set({draft: {...draft, preview: result.data, inspectedAt: Date.now(), submission: null}});
    }
    sendResponse(result);
  }).catch(() => sendResponse({v: 1, ok: false, error: {code: 'HOST_DISCONNECTED'}}));
  return true;
});
