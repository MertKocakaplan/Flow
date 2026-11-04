// Writes panel.css's palette blocks from source/Theme.cs. Usage: node palette.mjs <body.css>
// The body (everything after the palette) is kept as given; palette.test.js checks the result.
import {readFileSync, writeFileSync} from 'node:fs';

const theme = readFileSync(new URL('../../Theme.cs', import.meta.url), 'utf8');
const strings = name => [...theme.match(new RegExp(name + '\\s*=\\s*\\{([^}]*)\\}'))[1].matchAll(/"([^"]+)"/g)].map(m => m[1]);
const keys = strings('Keys'), light = strings('Light'), dark = strings('Dark');
const kebab = key => '--' + key.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
const used = ['Bg', 'Surface', 'Surface2', 'SurfaceHover', 'Border', 'BorderStrong', 'Text', 'TextMuted', 'TextFaint',
  'OnAccent', 'Accent', 'AccentHover', 'AccentSoft', 'AccentText', 'Danger', 'DangerSoft', 'Success', 'SuccessSoft',
  'Warning', 'WarningSoft', 'Info', 'InfoSoft', 'Sidebar', 'SidebarText', 'SidebarMuted', 'SidebarActive', 'SidebarHover',
  'Focus', 'InputBg', 'InputBorder', 'Track', 'SourceYouTube', 'SourceInstagram', 'SourceTikTok', 'SourceOther'];
const vars = values => used.map(k => kebab(k) + ': ' + values[keys.indexOf(k)].toLowerCase() + ';').join(' ');
const head = '/* Palette: generated from source/Theme.cs by browser-tests/shots/palette.mjs; palette.test.js keeps them equal. */\n'
  + ':root { color-scheme: light; ' + vars(light) + ' }\n'
  + '@media (prefers-color-scheme: dark) { :root { color-scheme: dark; ' + vars(dark) + ' } }\n'
  + ':root[data-theme="light"] { color-scheme: light; ' + vars(light) + ' }\n'
  + ':root[data-theme="dark"] { color-scheme: dark; ' + vars(dark) + ' }\n';
const body = readFileSync(process.argv[2], 'utf8');
writeFileSync(new URL('../../../chrome-extension/panel.css', import.meta.url), head + body);
console.log('wrote panel.css palette for ' + used.length + ' keys');
