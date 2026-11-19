import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { setup, about } from '../src/server/plugins/claude-code.plugin.js';
import { must } from './support.js';
import type { Engine, EngineCompleted, EngineEmitter } from '../src/shared/contract.js';

// Claude Code sessions started in a terminal: read from ~/.claude/projects, and a turn run by the
// CLI itself (`claude -p --resume`). The CLI here is a stand-in script; no real agent is contacted.

const human = (uuid: string, text: string, cwd: string) => ({ type: 'user', uuid, cwd, timestamp: '2026-11-08T10:00:00Z', message: { role: 'user', content: text } });
const say = (uuid: string, text: string, model = 'claude-haiku-5-5') => ({ type: 'assistant', uuid, message: { model, stop_reason: 'end_turn', content: [{ type: 'text', text }] } });
const ID_A = '11111111-1111-4111-8111-111111111111', ID_B = '22222222-2222-4222-8222-222222222222', ID_DESKTOP = '33333333-3333-4333-8333-333333333333';

function home(t: { after(fn: () => void): void }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-code-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const work = path.join(root, 'work'), other = path.join(root, 'other');
  fs.mkdirSync(work); fs.mkdirSync(other);
  const folder = (dir: string) => path.join(root, '.claude/projects', dir.replace(/[^a-zA-Z0-9]/g, '-'));
  for (const dir of [work, other]) fs.mkdirSync(folder(dir), { recursive: true });
  const write = (dir: string, id: string, records: object[]) => fs.writeFileSync(path.join(folder(dir), `${id}.jsonl`), records.map(record => JSON.stringify(record)).join('\n') + '\n');
  write(work, ID_A, [human('u1', 'first question', work), say('a1', 'first answer')]);
  write(other, ID_B, [human('u2', 'in the other folder', other), say('a2', 'ok', 'claude-opus-5-5')]);
  // A session Claude desktop lists stays with the Claude agent.
  write(work, ID_DESKTOP, [human('u3', 'desktop one', work), say('a3', 'hi')]);
  const desktopHome = path.join(root, 'desktop'), index = path.join(desktopHome, 'claude-code-sessions/acct/org');
  fs.mkdirSync(index, { recursive: true });
  fs.writeFileSync(path.join(index, 'local_x.json'), JSON.stringify({ sessionId: 'local_x', cliSessionId: ID_DESKTOP, cwd: work, title: 'Desktop' }));
  return { root, work, other, desktopHome, claudeHome: path.join(root, '.claude') };
}

// A stand-in for the claude CLI: it records its arguments and prompt, then plays a stream-json script.
function cli(root: string, script: string): string {
  const file = path.join(root, 'fake-claude.mjs');
  fs.writeFileSync(file, `#!/usr/bin/env node
import fs from 'node:fs';
const prompt = fs.readFileSync(0, 'utf8');
fs.writeFileSync(${JSON.stringify(path.join(root, 'call.json'))}, JSON.stringify({ args: process.argv.slice(2), prompt, cwd: process.cwd() }));
const out = record => process.stdout.write(JSON.stringify(record) + '\\n');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
${script}
`);
  fs.chmodSync(file, 0o755);
  return file;
}

async function engineFor(config: object): Promise<Engine> {
  let engine: Engine | undefined;
  await setup({ config, registerEngine: registered => { engine = registered; return () => {}; }, registerAsset: () => {} });
  return must(engine, 'engine');
}
const completion = (emitter: EngineEmitter) => new Promise<EngineCompleted>((resolve, reject) => { emitter.on('completed', resolve); emitter.on('error', reject); });

test('Claude Code: found by its projects folder, and lists terminal sessions only, by the folder they started in', async t => {
  const { claudeHome, desktopHome, work, other } = home(t);
  assert.deepEqual(await must(about.detect)({ claudeHome }), { installed: true, where: path.join(claudeHome, 'projects') });
  assert.equal((await must(about.detect)({ claudeHome: path.join(claudeHome, 'missing') })).installed, false);
  const engine = await engineFor({ claudeHome, claudeDesktopHome: desktopHome, claudeBin: '/nowhere/claude' });
  assert.equal(engine.capabilities.explicitSettings, true);
  const sessions = await engine.list();
  assert.deepEqual(sessions.map(session => session.id).sort(), [ID_A, ID_B], 'the desktop\'s session is left to the Claude agent');
  const a = must(sessions.find(session => session.id === ID_A));
  assert.equal(a.engine, 'claude-code'); assert.equal(a.title, 'first question'); assert.equal(a.metadata.group?.cwd, work);
  assert.equal(must(sessions.find(session => session.id === ID_B)).metadata.group?.cwd, other);
  const history = await engine.read(ID_A);
  assert.equal(history.session?.engine, 'claude-code');
  assert.equal(history.turns.length, 1); assert.equal(history.turns[0]?.userPrompt, 'first question'); assert.equal(history.turns[0]?.status, 'completed');
});

test('Claude Code: a message resumes the session with the CLI, in its folder, with the picked model and effort; the turn ends when the CLI does', { skip: process.platform === 'win32' }, async t => {
  const { root, claudeHome, desktopHome, work } = home(t);
  // A background task: the CLI reports a result, goes on when the task ends, and reports again.
  const bin = cli(root, `
out({ type: 'system', subtype: 'init', session_id: '${ID_A}' });
out({ type: 'assistant', uuid: 'b1', message: { content: [{ type: 'text', text: 'started it in the background' }] } });
out({ type: 'result', subtype: 'success', result: 'started it in the background' });
await wait(150);
out({ type: 'assistant', uuid: 'b2', message: { content: [{ type: 'text', text: 'done' }] } });
out({ type: 'result', subtype: 'success', result: 'done' });`);
  const engine = await engineFor({ claudeHome, claudeDesktopHome: desktopHome, claudeBin: bin });
  const emitter = engine.send(ID_A, { prompt: 'hello', model: 'haiku', reasoningEffort: 'high', attachments: [] });
  const messages: string[] = [];
  emitter.on('item_completed', ({ item }) => { if (item.type === 'agentMessage') messages.push(String(item.text)); });
  const ended = await completion(emitter);
  assert.equal(ended.turn?.status, 'completed');
  assert.deepEqual([...new Set(messages)], ['started it in the background', 'done'], 'what came after the first result belongs to the turn');
  const call = JSON.parse(fs.readFileSync(path.join(root, 'call.json'), 'utf8'));
  assert.deepEqual(call.args.slice(0, 3), ['-p', '--resume', ID_A]);
  assert.ok(call.args.join(' ').includes('--model haiku') && call.args.join(' ').includes('--effort high'));
  assert.equal(fs.realpathSync(call.cwd), fs.realpathSync(work)); assert.equal(call.prompt, 'hello');
});

test('Claude Code: a new session starts with the id it is given, and Stop ends the CLI\'s turn as interrupted', { skip: process.platform === 'win32' }, async t => {
  const { root, claudeHome, desktopHome, work } = home(t);
  const bin = cli(root, `
out({ type: 'system', subtype: 'init' });
out({ type: 'assistant', uuid: 'c1', message: { content: [{ type: 'text', text: 'working' }] } });
process.on('SIGINT', () => process.exit(130));
await wait(10000);`);
  const engine = await engineFor({ claudeHome, claudeDesktopHome: desktopHome, claudeBin: bin });
  const created = await engine.create({ prompt: 'start', cwd: work, attachments: [] });
  assert.equal(created.started, true);
  const emitter = must(engine.observe)(created.id);
  const said = new Promise<void>(resolve => emitter.on('item_completed', () => resolve()));
  await said;
  const call = JSON.parse(fs.readFileSync(path.join(root, 'call.json'), 'utf8'));
  assert.deepEqual(call.args.slice(0, 3), ['-p', '--session-id', created.id]);
  assert.deepEqual(await must(engine.interrupt)(created.id, emitter), { pending: true });
  assert.equal((await completion(emitter)).turn?.status, 'interrupted');
});
