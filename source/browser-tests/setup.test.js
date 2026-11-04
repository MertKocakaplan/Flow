import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
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
