import {ALL_SITES} from './discovery.js';
import {errorText} from './i18n.js';
export async function initDiscovery({translate,isOnline,openDraft}) {
  const $=id=>document.getElementById(id),nodes=new Map(),selected=new Set();
  let tabId=Number(new URLSearchParams(location.search).get('tab'))||null,page=null,batch=null,config={enabled:false,overlays:true},loading=false;
  const t=key=>translate(key);
  function feedback(text,error=false){$('scanFeedback').textContent=text;$('scanFeedback').hidden=!text;$('scanFeedback').classList.toggle('error',error);}
  async function command(action,args={}){try{const result=await chrome.runtime.sendMessage({type:'discovery',action,tabId,...args});if(!result?.ok)throw result?.error||{code:'HOST_DISCONNECTED'};return result.data;}catch(error){feedback(errorText(error,t),true);throw error;}}
  function visible(item){const filter=$('mediaFilter').value;return filter==='all'||filter==='av'&&item.kind!=='image'||filter===item.kind;}
  function buttons(){
    $('sendSelected').disabled=!isOnline()||!selected.size||loading||batch?.state==='running';
    $('selectedCount').textContent=selected.size+' / 20 '+t('selected');
    const selectable=(page?.items||[]).filter(i=>visible(i)&&!i.protected&&!i.unsupported);
    $('selectAll').checked=selectable.length>0&&selectable.every(i=>selected.has(i.id));
    $('selectAll').indeterminate=selectable.some(i=>selected.has(i.id))&&!$('selectAll').checked;
    $('autoScan').textContent=t(config.enabled?'disableScan':'enableScan');$('autoScan').setAttribute('aria-pressed',String(config.enabled));
    $('scanMode').textContent=t(config.enabled?'autoScanOn':'autoScanOff');$('overlays').checked=config.overlays;
  }
  function showBatch(){
    $('batchSection').hidden=!batch;if(!batch)return;
    const done=batch.items.filter(i=>i.state==='added').length,failed=batch.items.filter(i=>i.state==='failed').length;
    $('batchProgress').textContent=`${done} / ${batch.items.length} ${t('batchAdded')} · ${failed} ${t('batchFailed')} · ${t(batch.state==='running'?'batchRunning':batch.state==='stopped'?'batchStopped':'batchDone')}`;
    $('batchMeter').value=(done+failed)*100/Math.max(1,batch.items.length);$('stopBatch').hidden=batch.state!=='running';$('retryBatch').hidden=batch.state==='running'||done===batch.items.length;
    $('batchResults').replaceChildren();
    for(const item of batch.items){const p=document.createElement('p');p.textContent=item.title+' — '+(item.error?errorText(item.error,t):t('batch_'+item.state));if(item.error)p.className='error';$('batchResults').append(p);}
  }
  function render(){
    $('scanPage').textContent=page?.url?new URL(page.url).hostname:t('noPage');$('mediaCount').textContent=String(page?.items?.length||0);
    $('mediaLimit').hidden=!page?.limited;let count=0;const keep=new Set();
    for(const item of page?.items||[]){
      keep.add(item.id);let row=nodes.get(item.id);
      if(!row){
        const root=document.createElement('article');root.className='media-row';
        const label=document.createElement('label');label.className='check';const check=document.createElement('input');check.type='checkbox';const title=document.createElement('h2');label.append(check,title);
        const meta=document.createElement('p');meta.className='muted meta';const status=document.createElement('p');status.className='small error';const actions=document.createElement('div');actions.className='actions';const inspect=document.createElement('button');inspect.type='button';const source=document.createElement('button');source.type='button';actions.append(inspect,source);
        const details=document.createElement('details'),summary=document.createElement('summary');details.append(summary);let loaded=false;
        details.addEventListener('toggle',()=>{if(!details.open||loaded)return;loaded=true;
          if(item.source==='page'&&item.kind!=='image'||item.kind==='stream'){const p=document.createElement('p');p.textContent=t('previewOnPage');details.append(p);return;}
          const preview=document.createElement(item.kind==='image'?'img':item.kind==='audio'?'audio':'video');preview.referrerPolicy='no-referrer';
          if(item.kind==='image')preview.alt=item.title;else{preview.controls=true;preview.preload='none';}
          preview.addEventListener('error',()=>{const p=document.createElement('p');p.textContent=t('previewUnavailable');details.append(p);},{once:true});preview.src=item.url;details.append(preview);
        });
        check.addEventListener('change',()=>{if(check.checked){if(selected.size>=20){check.checked=false;feedback(t('batchLimit'),true);return;}selected.add(item.id);}else selected.delete(item.id);buttons();});
        inspect.addEventListener('click',async()=>{try{await openDraft(await command('select',{id:item.id}));}catch{}});
        source.addEventListener('click',()=>chrome.tabs.create({url:item.pageUrl}));
        root.append(label,meta,status,actions,details);$('mediaItems').append(root);row={root,check,title,meta,status,inspect,source,summary,details};nodes.set(item.id,row);
      }
      row.root.hidden=!visible(item);if(!row.root.hidden)count++;
      row.title.textContent=item.title;row.check.checked=selected.has(item.id);row.check.disabled=item.protected||item.unsupported;
      row.meta.textContent=[t('kind_'+item.kind),new URL(item.url).hostname,item.width&&item.height?`${item.width}×${item.height}`:'',item.bytes?`${(item.bytes/1048576).toFixed(1)} MB`:''].filter(Boolean).join(' · ');
      row.status.textContent=item.protected?t('protected'):item.unsupported?t('imageUnsupported'):'';row.status.hidden=!row.status.textContent;
      row.inspect.textContent=t('inspectMedia');row.inspect.disabled=!isOnline()||item.protected||item.unsupported;row.source.textContent=t('sourcePage');row.summary.textContent=t('previewMedia');row.details.hidden=item.protected;
    }
    for(const [id,row] of nodes)if(!keep.has(id)){row.root.remove();nodes.delete(id);selected.delete(id);}
    $('mediaEmpty').hidden=count>0;buttons();showBatch();
  }
  async function refresh(){if(loading)return;try{
    if(!new URLSearchParams(location.search).has('tab')){const [active]=await chrome.tabs.query({active:true,currentWindow:true});if(active?.id!=null&&active.id!==tabId){tabId=active.id;selected.clear();}}
    if(tabId==null){render();return;}
    const state=await command('snapshot');if(page?.url!==state.page?.url)selected.clear();page=state.page;batch=state.batch;config=state.config;render();
  }catch{}}
  $('scanNow').addEventListener('click',async()=>{if(tabId==null)return;loading=true;buttons();feedback(t('scanning'));try{await command('scan');feedback(t('scanFinished'));}catch{}finally{loading=false;await refresh();}});
  $('autoScan').addEventListener('click',async()=>{
    try{if(!config.enabled && !await chrome.permissions.request({origins:ALL_SITES})){feedback(t('permissionDeclined'));return;}
      const enabled=!config.enabled;await command('configure',{enabled,overlays:$('overlays').checked});
      if(!enabled)await chrome.permissions.remove({origins:ALL_SITES});feedback(t(enabled?'autoScanOn':'autoScanOff'));await refresh();
    }catch(error){feedback(errorText(error,t),true);}
  });
  $('overlays').addEventListener('change',async()=>{try{await command('configure',{enabled:config.enabled,overlays:$('overlays').checked});await refresh();}catch{}});
  $('mediaFilter').addEventListener('change',render);
  $('selectAll').addEventListener('change',()=>{const items=(page?.items||[]).filter(i=>visible(i)&&!i.protected&&!i.unsupported);for(const item of items){if($('selectAll').checked){if(selected.size<20)selected.add(item.id);}else selected.delete(item.id);}render();});
  $('sendSelected').addEventListener('click',async()=>{loading=true;buttons();try{batch=await command('batch',{ids:[...selected],hold:$('batchHold').checked});selected.clear();feedback(t('batchStarted'));}catch{}finally{loading=false;render();}});
  $('stopBatch').addEventListener('click',async()=>{try{batch=await command('stop');showBatch();}catch{}});
  $('retryBatch').addEventListener('click',async()=>{try{batch=await command('retry');render();}catch{}});
  chrome.storage.onChanged.addListener((changes,area)=>{if(area==='session'&&(changes.discovery||changes.batch))refresh();});
  chrome.tabs.onActivated?.addListener(()=>refresh());
  setInterval(()=>{if(document.visibilityState==='visible'&&!$('mediaView').hidden)refresh();},2500);
  await refresh();return {refresh,translate:render,refreshButtons:buttons};
}
