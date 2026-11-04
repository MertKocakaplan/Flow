// Pure detection helpers. This file also forms the first part of the isolated content script.
export const MEDIA_LIMIT = 250;
export function webUrl(value, base) {
  try {
    if (typeof value !== 'string' || value.length > 8192 || /[\s\\]/.test(value)) return null;
    const u = new URL(value, base);
    if (!/^https?:$/.test(u.protocol) || u.username || u.password || u.port) return null;
    const host=u.hostname.toLowerCase().replace(/\.$/,'');
    if (!host.includes('.') || /^[\d.]+$/.test(host) || host.includes(':') || /\.(local|localhost|internal|lan|home|arpa)$/.test(host)) return null;
    u.hash=''; return u.href;
  } catch { return null; }
}
export function pageMedia(value) {
  const url=webUrl(value); if(!url)return null; const u=new URL(url),host=u.hostname;
  if (/(^|\.)youtube(?:-nocookie)?\.com$/.test(host) || host==='youtu.be') {
    const id=host==='youtu.be'?u.pathname.split('/')[1]:u.searchParams.get('v') || /^\/(?:shorts|embed|live)\/([^/]+)/.exec(u.pathname)?.[1];
    return /^[\w-]{11}$/.test(id||'')?'https://www.youtube.com/watch?v='+id:null;
  }
  if (/(^|\.)instagram\.com$/.test(host) && /^\/(?:p|reel|tv)\/[^/]+/.test(u.pathname)) return u.origin+u.pathname;
  if (/(^|\.)tiktok\.com$/.test(host) && /\/@[^/]+\/video\/\d+/.test(u.pathname)) return u.origin+u.pathname;
  return null;
}
export function mediaType(url, mime='') {
  const path=(()=>{try{return new URL(url).pathname.toLowerCase();}catch{return '';}})();
  mime=mime.split(';')[0].trim().toLowerCase();
  if(mime==='video/mp2t')return null;
  // Fragments, encryption keys and initialization segments are not standalone downloads.
  if (/\.(m4s|ts|cmfv|cmfa|key)$/.test(path) || /(?:^|\/)(?:init|segment|chunk)[-_.\d]/i.test(path)) return null;
  if (/\.(m3u8|mpd)$/.test(path) || /(?:mpegurl|dash\+xml)/.test(mime)) return 'stream';
  if (/^image\//.test(mime) || /\.(jpe?g|png|gif|webp|avif|bmp|svg|ico)$/.test(path)) return 'image';
  if (/^audio\//.test(mime) || /\.(mp3|m4a|aac|ogg|oga|opus|wav|flac)$/.test(path)) return 'audio';
  if (/^video\//.test(mime) || /\.(mp4|webm|mkv|mov|m4v|ogv)$/.test(path)) return 'video';
  return null;
}
export function candidate(raw, pageUrl) {
  if(!raw || typeof raw!=='object')return null;
  const page=webUrl(pageUrl),url=webUrl(raw.url,page); if(!page || !url)return null;
  const detected=mediaType(url,typeof raw.mime==='string'?raw.mime:'');
  let kind=detected || (['video','audio','image','stream','page'].includes(raw.kind)?raw.kind:null);
  if(!kind)return null;
  const extractor=kind!=='image' ? pageMedia(url) || pageMedia(page) : null;
  const finalUrl=extractor || url; if(extractor)kind='video';
  const unsupported=kind==='image' && (/[.](svg|ico)(?:$|\?)/i.test(url) || /svg|icon/i.test(raw.mime||''));
  return {url:finalUrl,pageUrl:page,kind,source:extractor || finalUrl===page&&!detected?'page':raw.source==='network'?'network':'element',
    title:String(raw.title||new URL(finalUrl).pathname.split('/').pop()||new URL(finalUrl).hostname).slice(0,220),
    mime:String(raw.mime||'').slice(0,100),bytes:Math.max(0,Number(raw.bytes)||0),
    width:Math.max(0,Math.min(32768,Number(raw.width)||0)),height:Math.max(0,Math.min(32768,Number(raw.height)||0)),
    protected:raw.protected===true,unsupported};
}
export function collectMedia(doc, pageUrl, resources=[]) {
  const found=[],seen=new Set();
  const add=raw=>{const item=candidate(raw,pageUrl);if(!item||seen.has(item.url))return;if(found.length>=MEDIA_LIMIT){const imageIndex=found.findIndex(i=>i.kind==='image');if(item.kind==='image'||imageIndex<0)return;found.splice(imageIndex,1);}seen.add(item.url);found.push(item);};
  for(const el of [...doc.querySelectorAll('video,audio')].slice(0,100)) {
    const source=el.currentSrc || el.getAttribute('src');
    const raw={kind:el.tagName.toLowerCase(),title:el.getAttribute('title')||el.getAttribute('aria-label')||doc.title,width:el.videoWidth,height:el.videoHeight,protected:!!el.mediaKeys || el.dataset.flowEncrypted==='true'};
    if(source && !source.startsWith('blob:')) add({...raw,url:source});
    for(const node of el.querySelectorAll('source[src]'))add({...raw,url:node.getAttribute('src'),mime:node.getAttribute('type')});
    if(!webUrl(source,pageUrl) && ![...el.querySelectorAll('source[src]')].some(node=>webUrl(node.getAttribute('src'),pageUrl)))add({...raw,url:pageMedia(pageUrl)||pageUrl});
  }
  for(const el of [...doc.querySelectorAll('img')].slice(0,2000)) {
    const width=el.naturalWidth||Number(el.getAttribute('width'))||0,height=el.naturalHeight||Number(el.getAttribute('height'))||0;
    if(width>0 && height>0 && (width<8 || height<8))continue;
    const url=el.currentSrc||el.getAttribute('src'); if(url)add({url,kind:'image',title:el.getAttribute('alt')||el.getAttribute('title'),width,height});
  }
  for(const el of [...doc.querySelectorAll('a[href]')].slice(0,2000)) {
    const url=webUrl(el.getAttribute('href'),pageUrl); if(url && mediaType(url))add({url,kind:mediaType(url),title:el.textContent?.trim()});
  }
  const canonical=pageMedia(pageUrl); if(canonical)add({url:canonical,kind:'video',title:doc.title});
  for(const resource of resources.slice(-2000)){const url=webUrl(resource.name,pageUrl);const kind=url&&mediaType(url);if(kind)add({url,kind,source:'network'});}
  return found;
}
