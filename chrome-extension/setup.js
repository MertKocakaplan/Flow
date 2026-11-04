import {translator} from './i18n.js';
// The setup page speaks the panel's language: the one chosen there, else the desktop's, else the browser's.
const saved = chrome.storage?.local ? await chrome.storage.local.get(['language','desktopLanguage','desktopTheme']) : {};
const known = value => value === 'tr' || value === 'en';
const language = known(saved.language) ? saved.language : known(saved.desktopLanguage) ? saved.desktopLanguage : (chrome.i18n?.getUILanguage?.() || navigator.language || 'en').startsWith('tr') ? 'tr' : 'en';
if (saved.desktopTheme === 'dark' || saved.desktopTheme === 'light') document.documentElement.dataset.theme = saved.desktopTheme;
const t = translator(language);
document.documentElement.lang = language;
document.title = 'Flow · ' + t('setup');
for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.getAttribute('data-i18n'));
