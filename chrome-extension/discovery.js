import {candidate,webUrl,mediaType,MEDIA_LIMIT} from './detection.js';
import {callNative} from './transport.js';
export const ALL_SITES=['http://*/*','https://*/*'];

export class Discovery {
  constructor(api,openPanel,native=callNative){
    this.api=api;this.openPanel=openPanel;this.native=native;this.tabs={};this.config={enabled:false,overlays:true,language:'tr'};this.batch=null;this.running=false;
    this.chain=Promise.resolve();
    this.ready=Promise.all([api.storage.local.get(['scanEnabled','overlays','language']),api.storage.session.get(['discovery','batch'])]).then(([local,session])=>{
      this.config={enabled:local.scanEnabled===true,overlays:local.overlays!==false,language:local.language||'tr'};
      this.tabs=Object.fromEntries(Object.entries(session.discovery||{}).filter(([,s])=>Date.now()-s.updated<1800000).slice(-30));
      this.batch=session.batch||null;
    });
  }
  serial(fn){const result=this.chain.then(()=>this.ready).then(fn);this.chain=result.catch(()=>{});return result;}
  async persist(){
    // storage.session is bounded; signed URLs can be much larger than ordinary page links.
    while(new TextEncoder().encode(JSON.stringify(this.tabs)).length>6000000){const oldest=Object.keys(this.tabs).sort((a,b)=>this.tabs[a].updated-this.tabs[b].updated)[0];if(!oldest)break;delete this.tabs[oldest];await this.badge(oldest);}
    await this.api.storage.session.set({discovery:this.tabs});
  }
  async badge(tabId){const state=this.tabs[tabId],n=state?.items.length||0;try{await this.api.action.setBadgeText({tabId:Number(tabId),text:n?String(n):''});await this.api.action.setBadgeBackgroundColor({tabId:Number(tabId),color:'#0a7a70'});}catch{}}
  async ensure(tabId,pageUrl,stamp=Date.now()){
    if(tabId<0 || !webUrl(pageUrl))return null;
    let state=this.tabs[tabId];
    if(!state || state.url!==webUrl(pageUrl)){state={url:webUrl(pageUrl),since:stamp,updated:Date.now(),items:[],manual:false,limited:false};this.tabs[tabId]=state;}
    const keys=Object.keys(this.tabs).sort((a,b)=>this.tabs[a].updated-this.tabs[b].updated);
    while(keys.length>30){const key=keys.shift();delete this.tabs[key];await this.badge(key);}
    return state;
  }
  async syncRegistration(){
    await this.ready;
    try{await this.api.scripting.unregisterContentScripts({ids:['flow-scanner']});}catch{}
    const granted=await this.api.permissions.contains({origins:ALL_SITES});
    if(this.config.enabled && granted)await this.api.scripting.registerContentScripts([{id:'flow-scanner',matches:ALL_SITES,js:['scan-content.js'],runAt:'document_idle',allFrames:true,persistAcrossSessions:true}]);
    else if(this.config.enabled){this.config.enabled=false;await this.api.storage.local.set({scanEnabled:false});}
  }
  async configure(enabled,overlays){
    await this.ready;
    if(enabled && !await this.api.permissions.contains({origins:ALL_SITES}))throw Error('SITE_PERMISSION');
    const disabling=this.config.enabled&&!enabled;
    this.config.enabled=enabled;this.config.overlays=overlays;
    await this.api.storage.local.set({scanEnabled:enabled,overlays});await this.syncRegistration();
    const tabs=await this.api.tabs.query({});
    if(disabling){for(const id of Object.keys(this.tabs)){delete this.tabs[id];await this.badge(id);}await this.persist();}
    for(const tab of tabs){
      try{await this.api.tabs.sendMessage(tab.id,{type:'scan-config',...this.config,enabled:enabled||this.tabs[tab.id]?.manual===true});}catch{}
      if(enabled&&webUrl(tab.url))this.scan(tab.id).catch(()=>{});
    }
    return this.config;
  }
  async scan(tabId){
    await this.ready;
    const tab=await this.api.tabs.get(tabId);const state=await this.ensure(tabId,tab.url);
    if(!state)throw Error('NO_PAGE');state.manual=true;
    try{await this.api.scripting.executeScript({target:{tabId,allFrames:true},files:['scan-content.js']});}
    catch{try{await this.api.scripting.executeScript({target:{tabId},files:['scan-content.js']});}catch{state.manual=false;throw Error('SITE_PERMISSION');}}
    try{await this.api.tabs.sendMessage(tabId,{type:'scan-config',...this.config,enabled:true});await this.api.tabs.sendMessage(tabId,{type:'scan-now'});}catch{}
    await this.persist();return {url:state.url};
  }
  async found(message,sender){
    if(sender.tab?.id==null || !webUrl(sender.url) || !Array.isArray(message.items))return;
    const tab=await this.api.tabs.get(sender.tab.id);
    // Reject delayed messages from documents that navigated away, including same-tab races.
    if(sender.frameId===0 && webUrl(sender.url)!==webUrl(tab.url))return;
    if(sender.frameId>0 && this.api.webNavigation.getFrame){const frame=await this.api.webNavigation.getFrame({tabId:tab.id,frameId:sender.frameId});if(!frame||webUrl(frame.url)!==webUrl(sender.url)||sender.documentId&&frame.documentId&&sender.documentId!==frame.documentId)return;}
    if(webUrl(message.pageUrl)!==webUrl(sender.url))return;
    if(!this.config.enabled && !this.tabs[tab.id]?.manual)return;
    const state=await this.ensure(tab.id,tab.url);if(!state || !this.config.enabled&&!state.manual)return;
    let changed=false;
    for(const raw of message.items.slice(0,MEDIA_LIMIT))changed=this.merge(state,candidate(raw,sender.url))||changed;
    if(!changed && (!message.limited||state.limited))return;
    state.limited=state.limited||message.limited===true;
    state.updated=Date.now();await this.persist();await this.badge(tab.id);
  }
  merge(state,item){
    if(!item)return false;const previous=state.items.find(x=>x.url===item.url);
    if(previous){const before=JSON.stringify(previous);previous.protected=previous.protected||item.protected;previous.bytes=Math.max(previous.bytes,item.bytes);previous.width=Math.max(previous.width,item.width);previous.height=Math.max(previous.height,item.height);if(item.title && item.source!=='network')previous.title=item.title;return before!==JSON.stringify(previous);}
    if(state.items.length>=MEDIA_LIMIT){const limited=state.limited;state.limited=true;const imageIndex=state.items.findIndex(i=>i.kind==='image');if(item.kind==='image'||imageIndex<0)return !limited;state.items.splice(imageIndex,1);}
    state.items.push({...item,id:crypto.randomUUID()});return true;
  }
  async network(details){
    if(!this.config.enabled && !this.tabs[details.tabId]?.manual)return;
    if(details.tabId<0 || details.statusCode<200 || details.statusCode>=300)return;
    const type=details.responseHeaders?.find(h=>h.name.toLowerCase()==='content-type')?.value||'';
    if(!mediaType(details.url,type))return;
    let tab;try{tab=await this.api.tabs.get(details.tabId);}catch{return;}
    const state=await this.ensure(tab.id,tab.url);if(!state || !this.config.enabled&&!state.manual || details.timeStamp<state.since)return;
    const mime=type.toLowerCase();
    if(/(?:mpegurl|dash\+xml)/.test(mime)||mediaType(details.url,type)!==null){
      let sourcePage=tab.url;
      if(details.frameId>0 && this.api.webNavigation.getFrame){const frame=await this.api.webNavigation.getFrame({tabId:tab.id,frameId:details.frameId});if(!frame || details.documentId&&frame.documentId&&details.documentId!==frame.documentId)return;if(webUrl(frame.url))sourcePage=frame.url;}
      const bytes=Number(details.responseHeaders?.find(h=>h.name.toLowerCase()==='content-length')?.value)||0;
      // Range response size is the fragment size, not the complete file size.
      const item=candidate({url:details.url,mime:type,bytes:details.statusCode===206?0:bytes,source:'network',title:tab.title},sourcePage);
      if(this.merge(state,item)){state.updated=Date.now();await this.persist();await this.badge(tab.id);}
    }
  }
  async navigate(details,sameDocument=false){
    if(details.frameId!==0)return;
    // Single-page sites rewrite their history without moving; the list and a manual scan stay. webUrl drops the hash.
    if(sameDocument&&this.tabs[details.tabId]&&this.tabs[details.tabId].url===webUrl(details.url))return;
    delete this.tabs[details.tabId];if(this.config.enabled)await this.ensure(details.tabId,details.url,details.timeStamp);await this.persist();await this.badge(details.tabId);
    try{await this.api.tabs.sendMessage(details.tabId,{type:'scan-config',...this.config});}catch{}
  }
  async snapshot(tabId){
    await this.ready;let tab;try{tab=await this.api.tabs.get(tabId);}catch{}
    const state=tab&&await this.ensure(tabId,tab.url);
    return {config:this.config,page:state||null,batch:this.batch};
  }
  async select(tabId,id){
    const state=this.tabs[tabId],item=state?.items.find(x=>x.id===id);if(!item)throw Error('MEDIA_MISSING');
    if(item.protected)throw Error('MEDIA_PROTECTED');if(item.unsupported)throw Error('MEDIA_UNSUPPORTED');
    const draft={url:item.url,kind:item.kind==='image'?'image':undefined,referrer:item.pageUrl,title:item.title,preview:null,submission:null};
    await this.api.storage.session.set({draft});return draft;
  }
  async startBatch(tabId,ids,hold){
    if(this.running || this.batch?.state==='running')throw Error('BATCH_BUSY');
    const state=this.tabs[tabId];const items=state?.items.filter(x=>ids.includes(x.id)&&!x.protected&&!x.unsupported)||[];
    if(!items.length || items.length>20)throw Error('BATCH_LIMIT');
    this.batch={id:crypto.randomUUID(),state:'running',stop:false,hold:!!hold,items:items.map(item=>({...item,requestId:crypto.randomUUID(),state:'waiting',error:null}))};
    await this.saveBatch();this.runBatch();return this.batch;
  }
  async saveBatch(){await this.api.storage.session.set({batch:this.batch});}
  async runBatch(){
    if(this.running || !this.batch || this.batch.state!=='running')return;
    this.running=true;
    try{
      for(const item of this.batch.items){
        if(item.state==='added'||item.state==='failed')continue;
        if(this.batch.stop)break;
        item.state='inspecting';await this.saveBatch();
        try{
          let result=await this.native(this.api,{v:1,action:'inspect',url:item.url,kind:item.kind==='image'?'image':undefined,referrer:item.pageUrl,title:item.title});
          if(!result.ok)throw result.error;
          if(this.batch.stop){item.state='waiting';break;}
          const choices=result.data.choices;
          const choice=choices.find(c=>c.format==='original') || choices.find(c=>c.format===(item.kind==='audio'?'m4a':'mp4')) || choices[0];
          if(!choice)throw {code:'INVALID_CHOICE'};
          item.state='sending';await this.saveBatch();
          result=await this.native(this.api,{v:1,action:'enqueue',token:result.data.token,choice:choice.key,hold:this.batch.hold,requestId:item.requestId});
          if(!result.ok)throw result.error;
          item.state='added';item.jobId=result.data.jobId;
        }catch(error){item.state='failed';item.error={code:error?.code||'HOST_DISCONNECTED',message:error?.message};}
        await this.saveBatch();
      }
      this.batch.state=this.batch.stop?'stopped':'done';await this.saveBatch();
    }finally{this.running=false;}
  }
  async retryBatch(){if(this.running)throw Error('BATCH_BUSY');for(const item of this.batch?.items||[])if(item.state!=='added'){item.state='waiting';item.error=null;}if(this.batch){this.batch.state='running';this.batch.stop=false;await this.saveBatch();this.runBatch();}return this.batch;}
}
