import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

// The extension shares the desktop palette: every CSS colour variable is a Theme.cs key in kebab case
// (Bg → --bg, TextMuted → --text-muted) with the same light and dark value.
const theme=readFileSync(new URL('../Theme.cs',import.meta.url),'utf8');
const css=readFileSync(new URL('../../chrome-extension/panel.css',import.meta.url),'utf8');
const strings=name=>[...theme.match(new RegExp(name+'\\s*=\\s*\\{([^}]*)\\}'))[1].matchAll(/"([^"]+)"/g)].map(m=>m[1]);
const keys=strings('Keys'),light=strings('Light'),dark=strings('Dark');
const palette={light:Object.fromEntries(keys.map((k,i)=>[k,light[i].toLowerCase()])),dark:Object.fromEntries(keys.map((k,i)=>[k,dark[i].toLowerCase()]))};
const kebab=key=>'--'+key.replace(/([a-z0-9])([A-Z])/g,'$1-$2').toLowerCase();
const byVar=Object.fromEntries(keys.map(k=>[kebab(k),k]));
const block=selector=>{
  const at=css.indexOf(selector);
  if(at<0)return null;
  const open=css.indexOf('{',at+selector.length-1),close=css.indexOf('}',open);
  return css.slice(open+1,close);
};
const variables=body=>Object.fromEntries([...body.matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)].map(m=>[m[1],m[2].trim().toLowerCase()]));
const required=['Bg','Surface','Text','TextMuted','Border','Accent','OnAccent','AccentSoft','AccentText','Danger','DangerSoft','Success','Warning','Focus'];
function matches(body,name){
  assert.ok(body,'missing palette block: '+name);
  const vars=variables(body);
  for(const key of required)assert.ok(kebab(key) in vars,name+' defines '+kebab(key));
  for(const [variable,value] of Object.entries(vars)){
    const key=byVar[variable];
    assert.ok(key,name+': '+variable+' is not a Theme.cs key');
    assert.equal(value,palette[name.includes('dark')?'dark':'light'][key],name+': '+variable);
  }
}

test('panel.css light palette equals Theme.cs Light',()=>{
  matches(block(':root {'),'light :root');
  matches(block(':root[data-theme="light"] {'),'light [data-theme]');
});
test('panel.css dark palette equals Theme.cs Dark',()=>{
  const media=css.slice(css.indexOf('@media (prefers-color-scheme: dark)'));
  assert.ok(css.includes('@media (prefers-color-scheme: dark)'),'dark media block present');
  matches(block.call(null,'@media (prefers-color-scheme: dark) { :root {')??variablesBlock(media),'dark media');
  matches(block(':root[data-theme="dark"] {'),'dark [data-theme]');
});
function variablesBlock(text){const open=text.indexOf(':root {');return open<0?null:text.slice(open+7,text.indexOf('}',open));}
test('panel.css has no colour literals outside the palette blocks',()=>{
  let rest=css;
  for(const selector of [':root[data-theme="light"] {',':root[data-theme="dark"] {']){const body=block(selector);if(body)rest=rest.replace(body,'');}
  const first=block(':root {');if(first)rest=rest.replace(first,'');
  const media=variablesBlock(css.slice(css.indexOf('@media (prefers-color-scheme: dark)')));if(media)rest=rest.replace(media,'');
  const literals=[...rest.matchAll(/#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/gi)].map(m=>m[0]);
  assert.deepEqual(literals,[]);
});
test('panel.css follows Windows high contrast',()=>{
  assert.match(css,/@media \(forced-colors: active\)/);
});
test('video button colours come from the Theme palette',()=>{
  const body=readFileSync(new URL('../../chrome-extension/content-body.js',import.meta.url),'utf8');
  const all=new Set([...light,...dark].map(v=>v.toLowerCase()));
  const expand=h=>h.length===4||h.length===5?'#'+[...h.slice(1)].map(c=>c+c).join(''):h;
  const foreign=[...body.matchAll(/#[0-9a-f]{3,8}\b/gi)].map(m=>expand(m[0].toLowerCase())).filter(h=>!all.has(h));
  assert.deepEqual(foreign,[]);
  assert.doesNotMatch(body,/rgba?\(|hsla?\(/);
});
test('panel.css turns motion off for reduced motion and never transitions everything',()=>{
  assert.match(css,/@media \(prefers-reduced-motion: reduce\)/);
  assert.doesNotMatch(css,/transition\s*:\s*all|transition-property\s*:\s*all/);
  assert.match(css,/transition-property:/);
});
