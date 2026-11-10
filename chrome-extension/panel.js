import {translator, errorText} from './i18n.js';
import {initDiscovery} from './media-panel.js';
const $ = id => document.getElementById(id);
let lang = (chrome.i18n.getUILanguage?.() || navigator.language || 'en').startsWith('tr') ? 'tr' : 'en';
let t = translator(lang), online = false, busy = false, polling = false, tab = 'download', preview = null, generation = 0, submission = null;
const nodes = new Map();
const sourceTab = Number(new URLSearchParams(location.search).get('tab')) || null;
// Opened as a tab (Opera, or when the side panel is refused) the active tab is the panel itself.
async function pageTab() {
  if (sourceTab != null) { try { return await chrome.tabs.get(sourceTab); } catch {} }
  const [active] = await chrome.tabs.query({active:true, currentWindow:true});
  return active;
}
let discoveryPanel=null,mediaContext={};
const currentWindow = await chrome.windows.getCurrent();
// Where this page is shown: the toolbar popup, the side panel or a tab (Opera, or when the side panel is refused).
// The popup and the side panel load the same address and are often open together for a moment (the popup opens the
// side panel, then closes), so the address cannot tell them apart. The popup finds its own window among the popup
// views; the side panel does not. A tab has a tab.
async function surface() {
  if (new URLSearchParams(location.search).has('side')) return 'side';
  if (sourceTab != null) return 'tab';
  try { if (await chrome.tabs.getCurrent?.()) return 'tab'; } catch {}
  let popups = null;
  try { popups = chrome.extension?.getViews?.({type:'popup'}) || null; } catch {}
  if (popups) return popups.includes(globalThis) ? 'popup' : 'side';
  // Without that list, only an address no side panel shares is taken as the popup.
  const contexts = chrome.runtime.getContexts ? await chrome.runtime.getContexts({contextTypes:['POPUP','SIDE_PANEL']}) : [];
  const mine = contexts.filter(c => c.documentUrl === location.href);
  return mine.length > 0 && mine.every(c => c.contextType === 'POPUP') ? 'popup' : 'side';
}
const mode = await surface();
if (mode === 'popup') document.body.classList.add('popup');
function translate() {
  t = translator(lang); document.documentElement.lang = lang;
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.getAttribute('data-i18n'));
  $('language').value = lang; $('language').title = t('languageHint');
  if (extensionInfo) showExtensionUpdate(extensionInfo);
  layoutButton();
  $('connection').textContent = t(online ? 'connected' : 'offline');
  discoveryPanel?.translate();
}
function feedback(text, error = false, queue = false) {
  const box = $(queue ? 'queueFeedback' : 'feedback'); box.textContent = text; box.hidden = !text; box.classList.toggle('error', error); box.setAttribute('role', error ? 'alert' : 'status');
}
function setOnline(value, error) {
  online = value; $('connection').textContent = t(value ? 'connected' : 'offline'); $('connection').classList.toggle('online', value);
  $('connect').hidden = value; $('setup').hidden = value || !error;
  if (error) $('setupText').textContent = errorText(error, t);
  updateButtons();
}
function updateButtons() {
  for (const id of ['inspect','download','hold']) $(id).disabled = busy || !online || (id !== 'inspect' && !preview);
  $('currentTab').disabled = busy;
  discoveryPanel?.refreshButtons();
}
async function send(action, args = {}) {
  try { return await chrome.runtime.sendMessage({type:'native', request:{v:1, action, ...args}}); }
  catch { return {v:1, ok:false, error:{code:'HOST_DISCONNECTED'}}; }
}
function accept(result, queue = false) {
  if (result?.ok) return true;
  const error = result?.error || {code:'HOST_DISCONNECTED'};
  if (/^(HOST_|APP_|PROTOCOL_)/.test(error.code)) setOnline(false, error);
  feedback(errorText(error, t), true, queue);
  return false;
}
// "1.10.0" is newer than "1.9.2": compared part by part.
function newerVersion(a, b) {
  const pa = String(a || '').split('.').map(Number), pb = String(b || '').split('.').map(Number);
  if (!pa.length || pa.some(Number.isNaN)) return false;
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) { const x = pa[i] || 0, y = pb[i] || 0; if (x !== y) return x > y; }
  return false;
}
const ownVersion = chrome.runtime.getManifest?.().version || '';
// Flow names the version in its package folder; the extension loaded from that folder reloads to pick it up.
var extensionInfo = null;
function showExtensionUpdate(extension) {
  extensionInfo = extension || null;
  const expected = extension?.expected;
  const stale = !!expected && newerVersion(expected, ownVersion);
  $('extUpdate').hidden = !stale;
  if (stale) $('extUpdateText').textContent = t('extUpdate').replace('{0}', ownVersion).replace('{1}', expected);
}
async function connect(launch = false) {
  $('connect').disabled = true;
  const result = await send('ping', {launch, ext: ownVersion});
  $('connect').disabled = false;
  if (!result?.ok) { setOnline(false, result?.error || {code:'HOST_DISCONNECTED'}); return; }
  if(!result.data.capabilities?.includes('image-original')){setOnline(false,{code:'APP_UPGRADE'});return;}
  const preferences = await chrome.storage.local.get('language');
  lang = preferences.language || result.data.language || lang;
  const theme = result.data.theme === 'dark' ? 'dark' : 'light';
  translate(); document.documentElement.dataset.theme = theme;
  await chrome.storage.local.set({desktopLanguage:result.data.language, desktopTheme:theme});
  $('folder').textContent = result.data.folder || ''; setOnline(true); feedback('');
  // Older Flow versions cannot open the folder; the button only appears where it works.
  $('openFolder').hidden = !result.data.capabilities?.includes('open-folder');
  $('openFolder').title = result.data.folder || '';
  showExtensionUpdate(result.data.extension);
  if (result.data.extension?.reload === true) { chrome.runtime.reload?.(); return; }
  await refreshQueue();
}
function view(name) {
  tab = name; for(const n of ['download','queue','media'])$(n+'View').hidden=name!==n;
  for (const n of ['download','queue','media']) { $(n+'Tab').classList.toggle('selected', name === n); $(n+'Tab').setAttribute('aria-pressed', String(name === n)); }
  if (name === 'queue') refreshQueue();
  if(name==='media')discoveryPanel?.refresh();
}
async function saveDraft() {
  await chrome.storage.session.set({draft:{url:$('url').value.trim(),...mediaContext, preview, choice:$('quality').value, submission, inspectedAt:preview?.inspectedAt || Date.now()}});
}
function clearPreview() { preview = null; submission = null; $('preview').hidden = true; updateButtons(); }
function formats(selected) {
  $('format').replaceChildren();
  for (const f of [...new Set(preview.choices.map(c => c.format))]) { const o = document.createElement('option'); o.value = f; o.textContent = f==='original'?t('original'):f.toUpperCase() + (f === 'mp3' || f === 'm4a' ? ' · '+t('audio') : ''); $('format').append(o); }
  if ([...$('format').options].some(o=>o.value===selected)) $('format').value = selected;
}
function qualities(selected) {
  $('quality').replaceChildren();
  for (const c of preview.choices.filter(c=>c.format===$('format').value)) { const o = document.createElement('option'); o.value = c.key; o.textContent = c.height ? `${c.height}p` : c.format==='original'?t('original'):/mp3|m4a/.test(c.format) ? t('audio') : t('best'); $('quality').append(o); }
  if ([...$('quality').options].some(o=>o.value===selected)) $('quality').value = selected;
  $('quality').disabled = /mp3|m4a|original/.test($('format').value);
  choiceSummary();
}
function choiceSummary() {
  const c = preview?.choices.find(c=>c.key===$('quality').value); if (!c) return;
  const size = c.bytes > 0 ? `${c.sizeExact ? '' : '≈ '}${new Intl.NumberFormat(lang,{maximumFractionDigits:1}).format(c.bytes/1048576)} MB` : t('unknownSize');
  $('summary').textContent = [c.summary,size].filter(Boolean).join(' · ');
  $('warning').textContent = c.warning || (c.conversion ? t('conversion') : ''); $('warning').hidden = !$('warning').textContent;
}
function clock(seconds) {
  const h = Math.floor(seconds/3600), m = Math.floor(seconds%3600/60), s = String(Math.floor(seconds%60)).padStart(2,'0');
  return h ? `${h}:${String(m).padStart(2,'0')}:${s}` : `${m}:${s}`;
}
function showPreview(data, choice) {
  if (!data?.choices?.length) return;
  preview = data; $('preview').hidden = false; $('title').textContent = data.title; $('platform').textContent = data.platform;
  $('duration').textContent = data.duration > 0 ? clock(data.duration) : '';
  $('folder').textContent = data.folder || '';
  formats(choice?.split(':')[0]); qualities(choice); updateButtons();
}
async function inspect(event) {
  event?.preventDefault();
  const url = $('url').value.trim();
  if (!/^https?:\/\//i.test(url)) { feedback(t('enterUrl'),true); return; }
  clearPreview(); const mine = ++generation; busy = true; updateButtons(); feedback(t('inspecting'));
  await saveDraft();
  const result = await send('inspect',{url,...mediaContext});
  busy = false; updateButtons();
  if (mine !== generation || url !== $('url').value.trim()) return;
  if (!accept(result)) return;
  result.data.inspectedAt = Date.now(); showPreview(result.data); await saveDraft(); feedback('');
}
async function enqueue(hold) {
  if (busy || !preview) return;
  const choice = $('quality').value;
  if (!submission || submission.token !== preview.token || submission.choice !== choice || submission.hold !== hold) submission = {token:preview.token, choice, hold, requestId:crypto.randomUUID()};
  const request = {...submission};
  busy = true; updateButtons(); feedback(t('sent'));
  await saveDraft();
  const result = await send('enqueue',request);
  busy = false; updateButtons();
  if (!accept(result)) return;
  // Reset only after an acknowledged enqueue. Lost acknowledgements reuse the saved request ID.
  if (preview?.token === request.token) { clearPreview(); $('url').value = ''; await saveDraft(); }
  view('queue'); feedback(t(result.data.duplicate ? 'alreadyAdded' : 'added'),false,true);
}
function element(tag, className) { const e = document.createElement(tag); if (className) e.className = className; return e; }
function renderQueue(data) {
  $('count').textContent = String(data.active); $('empty').hidden = data.total > 0; $('queueLimit').hidden = data.total <= 100;
  const keep = new Set();
  for (const job of data.items) {
    keep.add(job.id); let n = nodes.get(job.id);
    if (!n) {
      const root = element('article','job'), figure = element('span','figure'), title = element('h2'), state = element('span','state'), progress = element('progress'), detail = element('p','muted'), error = element('p','error'), actions = element('div','actions');
      progress.max = 100; figure.setAttribute('aria-hidden','true');
      n = {root,figure,title,state,progress,detail,error,actions,buttons:{}};
      for (const action of ['pause','resume','retry','cancel']) {
        const button = element('button'); button.type = 'button'; button.addEventListener('click', async () => {
          if (action === 'cancel' && !confirm(t('cancelConfirm'))) return;
          button.disabled = true; const result = await send(action,{jobId:job.id}); accept(result,true); await refreshQueue(); button.disabled = false;
        }); actions.append(button); n.buttons[action] = button;
      }
      root.append(figure,title,state,progress,detail,error,actions); nodes.set(job.id,n);
    }
    n.title.textContent = job.title; n.state.textContent = job.stateText; n.detail.textContent = job.detail; n.error.textContent = job.error; n.error.hidden = !job.error;
    const running = ['inspecting','downloading','processing','verifying'].includes(job.state);
    // The numeral cell shows the running percentage; other states draw their mark from data-state in CSS.
    n.root.dataset.state = job.state; n.figure.textContent = running && job.progress != null ? Math.floor(job.progress) + '%' : '';
    n.figure.classList.toggle('full', n.figure.textContent === '100%');
    n.progress.hidden = !running;
    n.progress.setAttribute('aria-label',job.title);
    if (job.progress == null) n.progress.removeAttribute('value'); else n.progress.value = job.progress;
    for (const action of ['pause','resume','retry','cancel']) { n.buttons[action].textContent = t(action); n.buttons[action].hidden = !job['can'+action[0].toUpperCase()+action.slice(1)]; }
    if (n.root.parentElement !== $('jobs')) $('jobs').append(n.root);
  }
  for (const [id,n] of nodes) if (!keep.has(id)) { n.root.remove(); nodes.delete(id); }
}
async function refreshQueue() {
  if (polling || !online) return;
  polling = true;
  try { const result = await send('queue'); if (accept(result,true)) renderQueue(result.data); }
  finally { polling = false; }
}
$('inspectForm').addEventListener('submit', inspect);
$('url').addEventListener('input', () => { generation++; mediaContext={}; clearPreview(); feedback(''); saveDraft(); });
$('currentTab').addEventListener('click', async () => {
  const active = await pageTab();
  if (!active?.url || !/^https?:\/\//i.test(active.url)) { feedback(t('noPage'),true); return; }
  $('url').value = active.url; generation++;mediaContext={}; clearPreview(); await saveDraft(); feedback('');
});
$('format').addEventListener('change',()=>{ submission=null; qualities(); saveDraft(); });
$('quality').addEventListener('change',()=>{ submission=null; choiceSummary(); saveDraft(); });
$('download').addEventListener('click',()=>enqueue(false)); $('hold').addEventListener('click',()=>enqueue(true));
$('downloadTab').addEventListener('click',()=>view('download')); $('queueTab').addEventListener('click',()=>view('queue'));
$('mediaTab').addEventListener('click',()=>view('media'));
$('connect').addEventListener('click',()=>connect(true));
$('extReload').addEventListener('click',()=>chrome.runtime.reload?.()); $('refresh').addEventListener('click',refreshQueue);
$('openFolder').addEventListener('click',async()=>{ const result = await send('folder'); if (accept(result,true)) feedback('',false,true); });
$('show').addEventListener('click',async()=>{ accept(await send('show',{launch:true})); });
// The footer button moves Flow to the other layout: from the popup or a tab into the side panel, and from the side
// panel back to the popup (a tab where the browser cannot open the popup itself).
function layoutButton() {
  const button = $('sidePanel');
  if (mode === 'side') { button.textContent = t('popupMode'); button.title = t('popupModeHint'); }
  else { button.textContent = t(chrome.sidePanel?.open ? 'sidePanel' : 'openPanel'); button.title = ''; }
}
async function openAsTab() {
  const [active] = await chrome.tabs.query({active:true, currentWindow:true});
  await chrome.tabs.create({url:chrome.runtime.getURL('panel.html')+(active?.id!=null?'?tab='+active.id:'')});
}
$('sidePanel').addEventListener('click',async()=>{
  if (mode === 'side') {
    // A popup closes as soon as focus moves, and closing the side panel moves it; so the side panel must close first
    // and the popup open after. This page ends when the panel closes, so the extension's worker does both.
    let result = null;
    try { result = await chrome.runtime.sendMessage({type:'layout', to:'popup', windowId:currentWindow.id}); } catch {}
    if (!result?.ok) { try { await openAsTab(); globalThis.close(); } catch { feedback(t('invalid'),true); } }
    return;
  }
  if (chrome.sidePanel?.open) { chrome.sidePanel.open({windowId:currentWindow.id}).then(()=>{ if (mode === 'popup') globalThis.close(); }).catch(()=>feedback(t('invalid'),true)); }
  else openAsTab().catch(()=>feedback(t('invalid'),true));
});
$('language').addEventListener('change',async()=>{ lang=$('language').value; await chrome.storage.local.set({language:lang}); translate(); if(preview){const choice=$('quality').value;formats($('format').value);qualities(choice);} refreshQueue(); });
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'session' || !changes.draft) return;
  const draft = changes.draft.newValue;
  if (!draft) return;
  const changedUrl = draft.url !== $('url').value.trim();
  // Inspection may finish in the worker after the popup was closed and reopened.
  if (changedUrl || !busy && draft.preview?.token !== preview?.token) {
    generation++; $('url').value = draft.url; clearPreview(); submission = draft.submission;mediaContext={kind:draft.kind,referrer:draft.referrer,title:draft.title};
    if (draft.preview && Date.now() - draft.inspectedAt < 600000) showPreview(draft.preview,draft.choice);
    feedback(''); view('download');
  }
  autoInspect(draft);
});
// A link sent from the video button or the context menu is fetched at once: the click already said "this one".
// Each request is handled once, whether the panel was open already or opened by that click.
const handledRequests = new Set();
async function autoInspect(draft) {
  if (!draft?.autoInspect || draft.preview || !draft.requestedAt || handledRequests.has(draft.requestedAt)) return;
  handledRequests.add(draft.requestedAt);
  if (!online) await connect();
  if (online && $('url').value.trim() === draft.url) inspect();
}
const stored = await chrome.storage.session.get('draft');
const localPreferences = await chrome.storage.local.get('language');
if (localPreferences.language) lang = localPreferences.language;
translate();
if (stored.draft) { $('url').value = stored.draft.url || ''; submission = stored.draft.submission;mediaContext={kind:stored.draft.kind,referrer:stored.draft.referrer,title:stored.draft.title}; if (stored.draft.preview && Date.now() - stored.draft.inspectedAt < 600000) showPreview(stored.draft.preview,stored.draft.choice); }
updateButtons(); await connect();
discoveryPanel=await initDiscovery({translate:key=>t(key),isOnline:()=>online,openDraft:async draft=>{generation++;clearPreview();mediaContext={kind:draft.kind,referrer:draft.referrer,title:draft.title};$('url').value=draft.url;view('download');await inspect();}});
if(!$('url').value.trim())view('media');
else if(stored.draft?.autoInspect)view('download');
await autoInspect(stored.draft);
layoutButton();
setInterval(()=>{ if(tab==='queue' && document.visibilityState==='visible') refreshQueue(); },2500);
