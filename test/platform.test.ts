// Where the desktop apps and their data are, per platform (services/desktop-places.ts), and the
// pieces of the bridges that depend on it. Each case names its platform, so all of them run on any machine.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  antigravityApp, appAsar, appIconFile, bundledCodexCli, claudeCliIn, claudeDesktopHome, findExecutable,
  helperCacheDir, linkCommand, openLink, packageRoot, setPackageLookup, PACKAGES,
} from '../src/server/services/desktop-places.js';
import { readAppIcon } from '../src/server/services/app-assets.js';
import * as codexDesktop from '../src/server/codex-desktop.js';

const temp = (): string => fs.mkdtempSync(path.join(os.tmpdir(), 'frontdesk-places-'));

test('Claude desktop data: ~/Library on macOS; on Windows the Claude-Data folder that holds the sessions index', () => {
  assert.equal(claudeDesktopHome({ platform: 'darwin', home: '/Users/a', env: {} }), '/Users/a/Library/Application Support/Claude');
  const local = temp(), roaming = temp();
  // No Claude-Data yet: the older %APPDATA%\Claude.
  assert.equal(claudeDesktopHome({ platform: 'win32', home: '/h', env: { LOCALAPPDATA: local, APPDATA: roaming } }), path.join(roaming, 'Claude'));
  // A publisher folder without the index is not it; the one with it is.
  fs.mkdirSync(path.join(local, 'Claude-Data', 'claude_empty'), { recursive: true });
  fs.mkdirSync(path.join(local, 'Claude-Data', 'claude_abc', 'claude-code-sessions'), { recursive: true });
  fs.writeFileSync(path.join(local, 'Claude-Data', 'README.txt'), 'not a folder');
  assert.equal(claudeDesktopHome({ platform: 'win32', home: '/h', env: { LOCALAPPDATA: local, APPDATA: roaming } }), path.join(local, 'Claude-Data', 'claude_abc'));
});

test('Claude desktop\'s own CLI inside one of its version folders', () => {
  assert.equal(claudeCliIn('/c/2.1.3/abc', { platform: 'darwin' }), '/c/2.1.3/abc/claude.app/Contents/MacOS/claude');
  assert.equal(claudeCliIn('C:\\c\\2.1.3\\abc', { platform: 'win32' }), 'C:\\c\\2.1.3\\abc\\claude.exe');
});

test('the apps\' bundles: under /Applications on macOS, inside the MSIX package folder on Windows', () => {
  const asked: string[] = [];
  setPackageLookup(name => { asked.push(name); return name === PACKAGES.codex ? 'C:\\Program Files\\WindowsApps\\OpenAI.Codex_1_x64__x' : null; });
  try {
    assert.equal(appAsar('codex', { platform: 'darwin' }), '/Applications/ChatGPT.app/Contents/Resources/app.asar');
    assert.equal(appAsar('claude', { platform: 'darwin' }), '/Applications/Claude.app/Contents/Resources/app.asar');
    assert.equal(appIconFile('codex', { platform: 'darwin' }), '/Applications/ChatGPT.app/Contents/Resources/electron.icns');
    assert.equal(bundledCodexCli({ platform: 'darwin' }), '/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex');
    assert.equal(asked.length, 0, 'macOS asks no package manager');
    assert.equal(appAsar('codex', { platform: 'win32' }), 'C:\\Program Files\\WindowsApps\\OpenAI.Codex_1_x64__x\\app\\resources\\app.asar');
    assert.equal(bundledCodexCli({ platform: 'win32' }), 'C:\\Program Files\\WindowsApps\\OpenAI.Codex_1_x64__x\\app\\resources\\codex.exe');
    // Not installed: no path at all, so `detect` finds nothing rather than a Mac path.
    assert.equal(appAsar('claude', { platform: 'win32' }), '');
    assert.equal(packageRoot('Claude', { platform: 'linux' }), null);
  } finally { setPackageLookup(null); }
});

test('a Windows app\'s icon is its package\'s largest tile logo, served as PNG', () => {
  const root = temp(), assets = path.join(root, 'Assets');
  fs.mkdirSync(assets);
  const png = (size: number) => Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), Buffer.alloc(size)]);
  fs.writeFileSync(path.join(assets, 'Square150x150Logo.scale-100.png'), png(30));
  fs.writeFileSync(path.join(assets, 'Square150x150Logo.scale-200.png'), png(90));
  fs.writeFileSync(path.join(assets, 'StoreLogo.png'), png(200));
  setPackageLookup(() => root);
  try {
    const file = appIconFile('claude', { platform: 'win32' });
    assert.equal(path.basename(file), 'Square150x150Logo.scale-200.png');
    assert.deepEqual(readAppIcon(file), { type: 'image/png', data: fs.readFileSync(file) });
  } finally { setPackageLookup(null); }
});

test('Antigravity, the helper cache and the Codex router, per platform', () => {
  assert.equal(antigravityApp({ platform: 'darwin' }), '/Applications/Antigravity.app/');
  assert.equal(antigravityApp({ platform: 'win32', home: 'C:\\Users\\a', env: { LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' } }), 'C:\\Users\\a\\AppData\\Local\\Programs\\Antigravity\\Antigravity.exe');
  assert.equal(helperCacheDir({ platform: 'darwin', home: '/Users/a', env: {} }), '/Users/a/Library/Caches/agent-frontdesk');
  assert.equal(helperCacheDir({ platform: 'win32', home: 'C:\\Users\\a', env: { LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' } }), 'C:\\Users\\a\\AppData\\Local\\agent-frontdesk');
  assert.equal(codexDesktop.socketPath('/Users/a/.codex', 'darwin'), path.join('/Users/a/.codex', 'ipc', 'ipc.sock'));
  assert.equal(codexDesktop.socketPath('C:\\Users\\a\\.codex', 'win32'), '\\\\.\\pipe\\codex-ipc');
});

test('a deep link goes to the app in the background on macOS, and to the shell\'s protocol handler on Windows with no cmd.exe', async () => {
  const link = 'codex://threads/new?path=C%3A%5Cw&prompt=a%20%26%20"b"';
  assert.deepEqual(linkCommand(link, { platform: 'darwin' }), ['/usr/bin/open', ['-g', link]]);
  assert.deepEqual(linkCommand(link, { platform: 'win32' }), ['rundll32.exe', ['url.dll,FileProtocolHandler', link]]);
  const ran: Array<[string, string[]]> = [];
  await openLink(link, { platform: 'win32', run: (file, args, _options, done) => { ran.push([file, args]); done(null); } });
  assert.deepEqual(ran, [['rundll32.exe', ['url.dll,FileProtocolHandler', link]]]);
  await assert.rejects(openLink(link, { platform: 'win32', run: (_file, _args, _options, done) => done(new Error('no handler')) }), /no handler/);
});

test('finding a program: PATH with PATHEXT on Windows, where any existing file runs', () => {
  const dir = temp();
  fs.writeFileSync(path.join(dir, 'codex.exe'), '');
  assert.equal(findExecutable(['codex'], { platform: 'win32', env: { PATH: dir, PATHEXT: '.COM;.EXE' } }), path.join(dir, 'codex.exe'));
  assert.equal(findExecutable(['missing', null, ''], { platform: 'win32', env: { PATH: dir } }), null);
  // An absolute candidate is taken as it is.
  assert.equal(findExecutable([path.join(dir, 'codex.exe')], { platform: 'win32', env: {} }), path.join(dir, 'codex.exe'));
});
