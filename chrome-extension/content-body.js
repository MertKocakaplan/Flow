// Appended to detection.js inside an IIFE by source/build-discovery.ps1.
if (window.__flowScanner) { window.__flowScanner.scan(); return; }
let enabled=false,timer=null,lastUrl=location.href,lastPayload='',overlays=true,language='tr',resourceEpoch=0;
const buttons=new Map();
const root=document.createElement('div'); root.id='flow-media-tools';
const shadow=root.attachShadow({mode:'closed'});
const style=document.createElement('style');
style.textContent=':host{all:initial}button{position:fixed;z-index:2147483646;font:600 12px/1.4 system-ui,sans-serif;color:#fff;background:#086b62;border:1px solid #fff;border-radius:6px;padding:9px 12px;cursor:pointer;box-shadow:0 2px 8px #0005;max-width:170px}button:focus-visible{outline:3px solid #fff;outline-offset:2px}button[hidden]{display:none!important}';
shadow.append(style);
function send(message){try{chrome.runtime.sendMessage(message).catch(()=>{});}catch{stop();}}
function position(){for(const [video,button] of buttons){if(!video.isConnected){button.remove();buttons.delete(video);continue;}const r=video.getBoundingClientRect();button.hidden=!enabled||!overlays||r.width<180||r.height<100||r.bottom<0||r.top>innerHeight;button.style.left=Math.max(4,Math.min(innerWidth-174,r.right-174))+'px';button.style.top=Math.max(4,r.top+10)+'px';}}
function updateButtons(){
  if(!overlays){for(const b of buttons.values())b.hidden=true;return;}
  if(!root.isConnected)(document.body||document.documentElement).append(root);
  for(const video of [...document.querySelectorAll('video')].slice(0,30))if(!buttons.has(video)){
    const b=document.createElement('button');b.type='button';b.textContent=language==='en'?'Download with Flow':'Flow ile indir';
    b.addEventListener('click',event=>{if(!event.isTrusted)return;event.preventDefault();event.stopPropagation();const src=video.currentSrc||video.src;send({type:'scan-open',pageUrl:location.href,item:{url:src?.startsWith('blob:')?location.href:src||location.href,kind:'video',title:document.title,protected:!!video.mediaKeys||video.dataset.flowEncrypted==='true'}});});
    shadow.append(b);buttons.set(video,b);
  }
  for(const b of buttons.values())b.textContent=language==='en'?'Download with Flow':'Flow ile indir';position();
}
function scan(){timer=null;if(!enabled)return;
  if(location.href!==lastUrl){lastUrl=location.href;lastPayload='';resourceEpoch=performance.now();}
  const items=collectMedia(document,location.href,performance.getEntriesByType('resource').filter(r=>r.startTime>=resourceEpoch));const payload=JSON.stringify(items);
  if(payload!==lastPayload){lastPayload=payload;send({type:'scan-found',pageUrl:location.href,items,limited:items.length>=MEDIA_LIMIT});}
  updateButtons();
}
function schedule(){if(enabled&&!timer)timer=setTimeout(scan,500);}
const observer=new MutationObserver(records=>{if(records.some(r=>r.target!==root && !root.contains(r.target)))schedule();});
const resourceObserver=typeof PerformanceObserver==='function'?new PerformanceObserver(schedule):null;
function imageLoaded(event){if(event.target?.tagName==='IMG')schedule();}
function encrypted(event){if(event.target?.tagName==='VIDEO'){event.target.dataset.flowEncrypted='true';schedule();}}
function startObservers(){
  observer.observe(document.documentElement,{subtree:true,childList:true,attributes:true,attributeFilter:['src','srcset','href','poster']});
  resourceObserver?.observe({type:'resource',buffered:true});
  addEventListener('scroll',position,{capture:true,passive:true});
  addEventListener('resize',position,{passive:true});
  document.addEventListener('loadedmetadata',schedule,true);
  document.addEventListener('load',imageLoaded,true);
  document.addEventListener('encrypted',encrypted,true);
}
function stop(){
  enabled=false;observer.disconnect();resourceObserver?.disconnect();clearTimeout(timer);timer=null;root.remove();
  removeEventListener('scroll',position,true);removeEventListener('resize',position);
  document.removeEventListener('loadedmetadata',schedule,true);
  document.removeEventListener('load',imageLoaded,true);
  document.removeEventListener('encrypted',encrypted,true);
}
function configure(message){
  if(message?.enabled!==true){stop();return;}
  if(!enabled){enabled=true;startObservers();}
  overlays=message.overlays!==false;language=message.language||language;lastPayload='';scan();
}
chrome.runtime.onMessage.addListener(message=>{
  if(message?.type==='scan-config')configure(message);
  if(message?.type==='scan-now'){lastPayload='';scan();}
});
window.__flowScanner={scan,stop};
chrome.runtime.sendMessage({type:'scan-ready'}).then(configure).catch(stop);
