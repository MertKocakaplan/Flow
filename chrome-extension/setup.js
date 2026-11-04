import {translator} from './i18n.js';
// The setup page speaks the panel's language: the one stored by the panel, else the browser's.
const stored = chrome.storage?.local ? (await chrome.storage.local.get('language')).language : null;
const language = stored === 'tr' || stored === 'en' ? stored : (chrome.i18n?.getUILanguage?.() || navigator.language || 'en').startsWith('tr') ? 'tr' : 'en';
const t = translator(language);
document.documentElement.lang = language;
document.title = 'Flow · ' + t('setup');
for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.getAttribute('data-i18n'));
