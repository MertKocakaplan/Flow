import {translator, errorText} from './i18n.js';
import {initDiscovery} from './media-panel.js';
const $ = id => document.getElementById(id);
let lang = (chrome.i18n.getUILanguage?.() || navigator.language || 'en').startsWith('tr') ? 'tr' : 'en';
let t = translator(lang), online = false, busy = false, polling = false, tab = 'download', preview = null, generation = 0, submission = null;
const nodes = new Map();
let discoveryPanel=null,mediaContext={};
const currentWindow = await chrome.windows.getCurrent();
if (!new URLSearchParams(location.search).has('side')) {
  // Chrome marks a side panel with its own extension context. A popup has a fixed compact width.
  const contexts = chrome.runtime.getContexts ? await chrome.runtime.getContexts({contextTypes:['POPUP']}) : [];
  if (contexts.some(c => c.documentId && c.documentUrl === location.href)) document.body.classList.add('popup');
}
function translate() {
  t = translator(lang); document.documentElement.lang = lang;
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.getAttribute('data-i18n'));
  $('language').value = lang; $('language').title = t('languageHint');
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
async function connect(launch = false) {
  $('connect').disabled = true;
  const result = await send('ping', {launch});
  $('connect').disabled = false;
  if (!result?.ok) { setOnline(false, result?.error || {code:'HOST_DISCONNECTED'}); return; }
  if(!result.data.capabilities?.includes('image-original')){setOnline(false,{code:'APP_UPGRADE'});return;}
  const preferences = await chrome.storage.local.get('language');
  lang = preferences.language || result.data.language || lang;
  translate(); document.documentElement.dataset.theme = result.data.theme === 'dark' ? 'dark' : 'light';
  $('folder').textContent = result.data.folder || ''; setOnline(true); feedback('');
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
function showPreview(data, choice) {
  if (!data?.choices?.length) return;
  preview = data; $('preview').hidden = false; $('title').textContent = data.title; $('platform').textContent = data.platform;
  $('duration').textContent = data.duration > 0 ? `${Math.floor(data.duration/60)}:${String(Math.floor(data.duration%60)).padStart(2,'0')}` : '';
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
      const root = element('article','job'), title = element('h2'), state = element('span','state'), progress = element('progress'), detail = element('p','muted'), error = element('p','error'), actions = element('div','actions');
      progress.max = 100;
      n = {root,title,state,progress,detail,error,actions,buttons:{}};
      for (const action of ['pause','resume','retry','cancel']) {
        const button = element('button'); button.type = 'button'; button.addEventListener('click', async () => {
          if (action === 'cancel' && !confirm(t('cancelConfirm'))) return;
          button.disabled = true; const result = await send(action,{jobId:job.id}); accept(result,true); await refreshQueue(); button.disabled = false;
        }); actions.append(button); n.buttons[action] = button;
      }
      root.append(title,state,progress,detail,error,actions); nodes.set(job.id,n);
    }
    n.title.textContent = job.title; n.state.textContent = job.stateText; n.detail.textContent = job.detail; n.error.textContent = job.error; n.error.hidden = !job.error;
    n.progress.hidden = !['inspecting','downloading','processing','verifying'].includes(job.state);
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
  const [active] = await chrome.tabs.query({active:true, currentWindow:true});
  if (!active?.url || !/^https?:\/\//i.test(active.url)) { feedback(t('noPage'),true); return; }
  $('url').value = active.url; generation++;mediaContext={}; clearPreview(); await saveDraft(); feedback('');
});
$('format').addEventListener('change',()=>{ submission=null; qualities(); saveDraft(); });
$('quality').addEventListener('change',()=>{ submission=null; choiceSummary(); saveDraft(); });
$('download').addEventListener('click',()=>enqueue(false)); $('hold').addEventListener('click',()=>enqueue(true));
$('downloadTab').addEventListener('click',()=>view('download')); $('queueTab').addEventListener('click',()=>view('queue'));
$('mediaTab').addEventListener('click',()=>view('media'));
$('connect').addEventListener('click',()=>connect(true)); $('refresh').addEventListener('click',refreshQueue);
$('show').addEventListener('click',async()=>{ accept(await send('show',{launch:true})); });
$('sidePanel').addEventListener('click',()=>{ if(chrome.sidePanel?.open)chrome.sidePanel.open({windowId:currentWindow.id}).catch(()=>feedback(t('invalid'),true));else chrome.tabs.query({active:true,currentWindow:true}).then(([active])=>chrome.tabs.create({url:chrome.runtime.getURL('panel.html')+'?tab='+active.id})); });
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
});
const stored = await chrome.storage.session.get('draft');
const localPreferences = await chrome.storage.local.get('language');
if (localPreferences.language) lang = localPreferences.language;
translate();
if (stored.draft) { $('url').value = stored.draft.url || ''; submission = stored.draft.submission;mediaContext={kind:stored.draft.kind,referrer:stored.draft.referrer,title:stored.draft.title}; if (stored.draft.preview && Date.now() - stored.draft.inspectedAt < 600000) showPreview(stored.draft.preview,stored.draft.choice); }
updateButtons(); await connect();
discoveryPanel=await initDiscovery({translate:key=>t(key),isOnline:()=>online,openDraft:async draft=>{generation++;clearPreview();mediaContext={kind:draft.kind,referrer:draft.referrer,title:draft.title};$('url').value=draft.url;view('download');await inspect();}});
if(!stored.draft?.url)view('media');
if(!chrome.sidePanel?.open)$('sidePanel').textContent=t('openPanel');
setInterval(()=>{ if(tab==='queue' && document.visibilityState==='visible') refreshQueue(); },2500);
