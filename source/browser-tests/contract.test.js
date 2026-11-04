import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

// panel.js, media-panel.js and the DOM tests look elements up by id; the redesign keeps every one.
const baseline=[
  'show', 'connection', 'connect', 'setup', 'setupText', 'mediaTab', 'mediaCount', 'downloadTab',
  'queueTab', 'count', 'mediaView', 'scanPage', 'autoScan', 'scanNow', 'overlays', 'scanMode',
  'scanFeedback', 'mediaFilter', 'selectAll', 'selectedCount', 'mediaEmpty', 'mediaItems', 'mediaLimit', 'batchHold',
  'sendSelected', 'batchSection', 'batchProgress', 'batchMeter', 'stopBatch', 'retryBatch', 'batchResults', 'downloadView',
  'inspectForm', 'url', 'currentTab', 'inspect', 'feedback', 'preview', 'platform', 'title',
  'duration', 'format', 'quality', 'summary', 'warning', 'folder', 'hold', 'download',
  'queueView', 'refresh', 'queueFeedback', 'empty', 'jobs', 'queueLimit', 'language', 'sidePanel'
];
const html=readFileSync(new URL('../../chrome-extension/panel.html',import.meta.url),'utf8');

test('panel.html keeps every baseline id exactly once',()=>{
  for(const id of baseline){
    const count=[...html.matchAll(/\sid="([^"]+)"/g)].filter(m=>m[1]===id).length;
    assert.equal(count,1,id);
  }
});
