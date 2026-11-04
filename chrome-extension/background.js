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
const discovery=new Discovery(chrome,openPanel);
const backgroundWork=fn=>discovery.serial(fn).catch(()=>{});
chrome.webRequest.onHeadersReceived.addListener(details=>backgroundWork(()=>discovery.network(details)),{urls:['http://*/*','https://*/*'],types:['media','xmlhttprequest','image','other']},['responseHeaders']);
chrome.webNavigation.onCommitted.addListener(details=>backgroundWork(()=>discovery.navigate(details)));
chrome.webNavigation.onHistoryStateUpdated.addListener(details=>backgroundWork(()=>discovery.navigate(details,true)));
chrome.tabs.onRemoved.addListener(tabId=>backgroundWork(async()=>{delete discovery.tabs[tabId];await discovery.persist();}));
chrome.permissions.onRemoved.addListener(()=>backgroundWork(async()=>{if(!await chrome.permissions.contains({origins:['http://*/*','https://*/*']}))await discovery.configure(false,discovery.config.overlays);}));
discovery.ready.then(()=>discovery.syncRegistration()).catch(()=>{});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==='local'&&changes.language)backgroundWork(async()=>{discovery.config.language=changes.language.newValue||'tr';for(const id of Object.keys(discovery.tabs)){try{await chrome.tabs.sendMessage(Number(id),{type:'scan-config',...discovery.config,enabled:discovery.config.enabled||discovery.tabs[id].manual});}catch{}}});});
async function createMenu() {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({id: menuId, title: 'Flow ile indir / Download with Flow', contexts: ['link', 'page', 'video', 'audio'], documentUrlPatterns: ['http://*/*', 'https://*/*']});
}
chrome.runtime.onInstalled.addListener(() => { createMenu().catch(() => {}); });
chrome.runtime.onStartup.addListener(() => { createMenu().catch(() => {}); });

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== menuId || tab?.id == null) return;
  // Keep the side-panel call in the original user gesture; do not await storage first.
  const opening = openPanel(tab.id);
  const url = info.linkUrl || info.pageUrl || tab.url || info.srcUrl || '';
  chrome.storage.session.set({draft: {url, preview: null, choice: null, submission: null}}).catch(() => {});
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
      await chrome.storage.session.set({draft:{url:item.url,referrer:item.pageUrl,title:item.title,preview:null,submission:null}});
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
