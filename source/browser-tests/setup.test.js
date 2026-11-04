import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {parseHTML} from 'linkedom';
import {translator} from '../../chrome-extension/i18n.js';

// The setup page follows the panel's language: every visible text comes from i18n.js. File names,
// paths and product names stay as they are and are marked translate="no".
const html=readFileSync(new URL('../../chrome-extension/setup.html',import.meta.url),'utf8');
const {document}=parseHTML(html);

test('setup.html carries no bare text',()=>{
  const bare=[];
  const walk=node=>{for(const child of node.childNodes){
    if(child.nodeType===3){if(child.textContent.trim()&&!child.parentElement.closest('[data-i18n],[translate="no"]'))bare.push(child.textContent.trim().slice(0,40));}
    else if(child.nodeType===1)walk(child);
  }};
  walk(document.body);
  assert.deepEqual(bare,[]);
});
test('setup.html keys exist in Turkish and English',()=>{
  const tr=translator('tr'),en=translator('en');
  const keys=[...document.querySelectorAll('[data-i18n]')].map(n=>n.getAttribute('data-i18n'));
  assert.ok(keys.length>20,'page is translated ('+keys.length+' keys)');
  for(const key of keys){assert.notEqual(tr(key),key,key);assert.notEqual(en(key),key,key);}
});
test('setup.js loads as a module',()=>{
  assert.ok(document.querySelector('script[type="module"][src="setup.js"]'));
});
test('no setup text is empty before translation',()=>{
  const empty=[...document.querySelectorAll('[data-i18n]')].filter(n=>!n.textContent.trim()).map(n=>n.getAttribute('data-i18n'));
  assert.deepEqual(empty,[]);
});
test('setup defaults match the Turkish strings',()=>{
  const tr=translator('tr');
  for(const n of document.querySelectorAll('strong[data-i18n]'))assert.equal(n.textContent,tr(n.getAttribute('data-i18n')));
});
const run=async saved=>{
  const page=parseHTML(html).document;
  const source=readFileSync(new URL('../../chrome-extension/setup.js',import.meta.url),'utf8').replace(/^import[^\n]*\n/gm,'');
  const context=vm.createContext({document:page,translator,navigator:{language:'tr'},chrome:{i18n:{getUILanguage:()=>'tr'},storage:{local:{get:async()=>saved}}}});
  await new vm.Script('(async()=>{'+source+'\n})()').runInContext(context);
  return page.documentElement;
};
test('the setup page follows the desktop unless the panel chose a language',async()=>{
  let root=await run({desktopLanguage:'en',desktopTheme:'dark'});
  assert.equal(root.getAttribute('lang'),'en');assert.equal(root.dataset.theme,'dark');
  root=await run({language:'tr',desktopLanguage:'en'});
  assert.equal(root.getAttribute('lang'),'tr');
});
