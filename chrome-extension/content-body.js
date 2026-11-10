// Appended to detection.js inside an IIFE by source/build-discovery.ps1.
if (window.__flowScanner) { window.__flowScanner.scan(); return; }
let enabled=false,timer=null,lastUrl=location.href,lastPayload='',overlays=true,language='tr',resourceEpoch=0;
const buttons=new Map();
const root=document.createElement('div'); root.id='flow-media-tools';
const shadow=root.attachShadow({mode:'closed'});
const style=document.createElement('style');
// On the page's video only Flow's mark: a small square cobalt plate (Theme.cs Accent) with a white frame and the
// wave-and-arrow drawn in white, so it reads on any picture without covering it. Its name is said by screen readers
// and shown on hover.
style.textContent=':host{all:initial}button{position:fixed;z-index:2147483646;width:30px;height:30px;padding:0;display:grid;place-items:center;background:#1f3fd1;border:1px solid #ffffff;border-radius:0;cursor:pointer;opacity:.86;transition:opacity 120ms}button:hover{opacity:1;background:#1a35b3}button:active{background:#152b91}button:focus-visible{opacity:1;outline:3px solid #ffffff;outline-offset:2px}button[hidden]{display:none!important}svg{width:22px;height:22px;display:block}@media (prefers-reduced-motion:reduce){button{transition:none}}';
// Built element by element: pages with Trusted Types (YouTube) refuse markup strings.
function mark(){const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),path=document.createElementNS(ns,'path');
  svg.setAttribute('viewBox','0 0 34 34');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');
  for(const [k,v] of [['d','M 8,12 C 12,8 15,16 19,12 C 22,9 24,11 26,12 M 17,15 L 17,25 M 13,21 L 17,25 21,21'],['fill','none'],['stroke','#ffffff'],['stroke-width','2.6'],['stroke-linecap','round'],['stroke-linejoin','round']])path.setAttribute(k,v);
  svg.append(path);return svg;}
const label=()=>language==='en'?'Download with Flow':'Flow ile indir';
shadow.append(style);
function send(message){try{chrome.runtime.sendMessage(message).catch(()=>{});}catch{stop();}}
function position(){for(const [video,button] of buttons){if(!video.isConnected){button.remove();buttons.delete(video);continue;}const r=video.getBoundingClientRect();button.hidden=!enabled||!overlays||r.width<140||r.height<80||r.bottom<0||r.top>innerHeight;button.style.left=Math.max(4,Math.min(innerWidth-38,r.right-38))+'px';button.style.top=Math.max(4,r.top+8)+'px';}}
function updateButtons(){
  if(!overlays){for(const b of buttons.values())b.hidden=true;return;}
  if(!root.isConnected)(document.body||document.documentElement).append(root);
  for(const video of [...document.querySelectorAll('video')].slice(0,30))if(!buttons.has(video)){
    const b=document.createElement('button');b.type='button';b.append(mark());b.setAttribute('aria-label',label());b.title=label();
    b.addEventListener('click',event=>{if(!event.isTrusted)return;event.preventDefault();event.stopPropagation();const src=video.currentSrc||video.src;send({type:'scan-open',pageUrl:location.href,item:{url:src?.startsWith('blob:')?location.href:src||location.href,kind:'video',title:document.title,protected:!!video.mediaKeys||video.dataset.flowEncrypted==='true'}});});
    shadow.append(b);buttons.set(video,b);
  }
  for(const b of buttons.values()){b.setAttribute('aria-label',label());b.title=label();}position();
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
