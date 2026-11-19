import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { ClaudeBridge, PERMISSION_MODES, createStreamHandler, recentMessages, type StreamRun } from '../claude-bridge.js';
import { withAttachments } from '../core/attachments.js';
import { EngineEvents, UnifiedSession } from '../types.js';
import { get, parseJson } from '../../shared/guards.js';
import type { CreateInput, EngineEmitter, History, PluginAbout, PluginSetup, SendInput } from '../../shared/contract.js';

// Claude Code sessions started in a terminal (the `claude` CLI), not in Claude desktop: one
// transcript per session under ~/.claude/projects/<project folder>/<session id>.jsonl. They are
// read with the Claude bridge's own transcript parser. A message goes to the session through the
// CLI itself (`claude -p --resume <id>`, stream-json), which appends to the same transcript: for
// these sessions the CLI is the session's own channel. Sessions Claude desktop lists stay with
// the Claude agent and are left out here.

const ENGINE = 'claude-code';
const IDENTITY = { id: ENGINE, label: 'Claude Code', description: 'Claude Code CLI', priority: 25, accent: '#D97757' } as const;
/** How many of the newest transcripts are listed (each is parsed, then kept until it changes). */
const LISTED = 200;
/** A transcript written this recently, whose last reply has not ended, is taken as running in a terminal. */
const LIVE_MS = 30_000;

const projectsDirOf = (claudeHome?: string) => path.join(claudeHome || path.join(process.env['HOME'] || '', '.claude'), 'projects');

export const about: PluginAbout = { ...IDENTITY, character: { kind: 'image', url: '/assets/characters/claude.svg', pixelated: true },
  detect: config => { const dir = projectsDirOf(config.claudeHome); return fs.existsSync(dir) ? { installed: true, where: dir } : { installed: false }; } };

interface CliRun extends StreamRun { child: ChildProcess; done: boolean }

// Claude Code names a project's folder after the directory it started in, every character that is
// not a letter or a digit made a dash. The session is resumed from that directory.
const folderName = (dir: string) => dir.replace(/[^a-zA-Z0-9]/g, '-');

export const setup: PluginSetup = ({ config = {}, registerEngine }) => {
  const bridge = new ClaudeBridge({ claudeHome: config.claudeHome, desktopHome: config.claudeDesktopHome, ...(config.claudeBin ? { bin: config.claudeBin } : {}) });
  const runs = new Map<string, CliRun>(), starts = new Map<string, EngineEmitter>();

  // Every transcript of a session started in a terminal, newest first: { id, file, mtimeMs }.
  const transcripts = () => {
    const desktop = new Set(bridge.desktopIndex().map(meta => meta.id));
    const found: Array<{ id: string; file: string; mtimeMs: number }> = [];
    let dirs: string[] = [];
    try { dirs = fs.readdirSync(bridge.projectsDir); } catch { return found; }
    for (const dir of dirs) {
      let files: string[] = [];
      try { files = fs.readdirSync(path.join(bridge.projectsDir, dir)).filter(file => /^[0-9a-f-]{36}\.jsonl$/.test(file)); } catch { continue; }
      for (const file of files) {
        const id = file.slice(0, -6);
        if (desktop.has(id)) continue;
        try { found.push({ id, file: path.join(bridge.projectsDir, dir, file), mtimeMs: fs.statSync(path.join(bridge.projectsDir, dir, file)).mtimeMs }); } catch { /* gone meanwhile */ }
      }
    }
    return found.sort((a, b) => b.mtimeMs - a.mtimeMs).slice(0, LISTED);
  };
  // Running in a terminal (not from here): written moments ago, and the last reply has not ended.
  const liveElsewhere = (id: string): boolean => {
    if (runs.has(id)) return false;
    const parsed = bridge.parse(id);
    return !!parsed && Date.now() - parsed.mtimeMs < LIVE_MS && parsed.lastStop !== 'end_turn';
  };
  // The directory the session was started in: the one its project folder is named after.
  const startDir = (id: string): string => {
    const parsed = bridge.parse(id), file = bridge.transcriptPath(id);
    const cwds = parsed?.cwds || [], folder = file ? path.basename(path.dirname(file)) : '';
    return cwds.find(dir => folderName(dir) === folder) || cwds.at(-1) || parsed?.cwd || '';
  };
  // The model the session last answered with, as the model menu names it ('claude-haiku-5-5' → 'haiku').
  const lastModel = (id: string): string => {
    const file = bridge.transcriptPath(id);
    if (!file) return '';
    let tail = '';
    try { const fd = fs.openSync(file, 'r'), size = fs.fstatSync(fd).size, length = Math.min(size, 256 * 1024), buffer = Buffer.alloc(length); fs.readSync(fd, buffer, 0, length, size - length); fs.closeSync(fd); tail = buffer.toString('utf8'); } catch { return ''; }
    const found = [...tail.matchAll(/"model":"([^"]+)"/g)].map(match => match[1] || '').filter(model => model && model !== '<synthetic>').at(-1) || '';
    const models = bridge.modelCache?.models || [];
    const bare = found.replace(/\[.*\]$/, '');
    return models.find(model => model.id === found)?.id || models.find(model => model.id.replace(/\[.*\]$/, '') === bare)?.id
      || models.find(model => /^[a-z]+$/.test(model.id) && bare.includes(`-${model.id}-`))?.id || '';
  };
  // Pictures in a transcript are served by this engine (the bridge writes Claude's address).
  const retarget = <T>(value: T): T => parseJson(JSON.stringify(value).split('/api/sessions/claude/').join(`/api/sessions/${ENGINE}/`)) as T;

  // One turn of the CLI in the session: `resume` an existing one, or start it with this id.
  const runTurn = (id: string, input: Partial<SendInput>, { resume, cwd }: { resume: boolean; cwd: string }): EngineEvents => {
    const emitter = new EngineEvents();
    queueMicrotask(() => {
      if (!bridge.bin) { emitter.emit('error', new Error('没有找到 claude 命令行，装好 Claude Code 后再试')); return; }
      if (!cwd || !fs.existsSync(cwd)) { emitter.emit('error', new Error(`这个会话的目录已不存在：${cwd || '（未知）'}`)); return; }
      const args = ['-p', resume ? '--resume' : '--session-id', id, '--output-format', 'stream-json', '--verbose'];
      if (input.model) args.push('--model', input.model);
      if (input.reasoningEffort) args.push('--effort', input.reasoningEffort);
      if (input.permissionMode) args.push('--permission-mode', input.permissionMode);
      let child: ChildProcess;
      try { child = spawn(bridge.bin, args, { cwd, env: process.env, stdio: ['pipe', 'pipe', 'pipe'] }); }
      catch (err) { emitter.emit('error', err instanceof Error ? err : new Error(String(err))); return; }
      // Tool pictures come with this engine's address; everything else goes as the bridge reads it.
      const relay = new EngineEvents();
      const run: CliRun = { emitter: relay, child, done: false, interrupted: false, lastMessage: null, plan: (bridge.parse(id)?.turns || []).flatMap(turn => turn.steps).filter(step => step.plan).at(-1)?.plan || null };
      runs.set(id, run);
      relay.on('item_started', event => emitter.emit('item_started', event));
      relay.on('item_completed', event => emitter.emit('item_completed', event));
      relay.on('delta', event => emitter.emit('delta', event.type === 'tool_result' && event.images ? retarget(event) : event));
      relay.on('approval', event => emitter.emit('approval', event));
      relay.on('approval_resolved', event => emitter.emit('approval_resolved', event));
      relay.on('completed', event => { run.done = true; emitter.emit('completed', event); });
      const handle = createStreamHandler(id, run);
      // A print run reports a `result` each time Claude stops, and goes on when a background task
      // it started finishes (then it reports again): the turn ends when the process does, with its last result.
      let buffered = '', stderr = '', lastResult: unknown = null;
      child.stdout?.on('data', (chunk: Buffer) => {
        const lines = (buffered + chunk.toString('utf8')).split('\n'); buffered = lines.pop() ?? '';
        for (const line of lines) {
          if (!line.trim()) continue;
          let record: unknown; try { record = parseJson(line); } catch { continue; }
          if (get(record, 'type') === 'system' && get(record, 'subtype') === 'init') emitter.turnId = null;
          if (get(record, 'type') === 'result') { lastResult = record; continue; }
          handle(record);
        }
      });
      child.stderr?.on('data', (chunk: Buffer) => { stderr = (stderr + chunk.toString('utf8')).slice(-4000); });
      child.on('error', err => { runs.delete(id); if (!run.done) { run.done = true; emitter.emit('error', err); } });
      child.on('close', code => {
        if (runs.get(id) === run) runs.delete(id);
        if (run.done) return;
        if (!run.interrupted && lastResult) { handle(lastResult); return; }
        run.done = true;
        if (run.interrupted) emitter.emit('completed', { turn: { status: 'interrupted' } });
        else emitter.emit('completed', { code: code ?? undefined, turn: { status: 'failed', error: { message: stderr.trim().split('\n').slice(-3).join('\n') || `claude 退出（代码 ${code}）` } } });
      });
      emitter.on('interrupt', () => { if (!run.done) { run.interrupted = true; child.kill('SIGINT'); } });
      child.stdin?.end(withAttachments(input.prompt, input.attachments, 'mention'));
    });
    return emitter;
  };

  const sessionOf = (id: string): UnifiedSession | null => {
    const parsed = bridge.parse(id), latest = parsed?.turns.at(-1);
    if (!parsed || !latest) return null;
    const running = runs.has(id) || liveElsewhere(id), cwd = startDir(id);
    return new UnifiedSession({
      id, engine: ENGINE, title: parsed.title || latest.userPrompt.slice(0, 60) || parsed.turns[0]?.userPrompt.slice(0, 60) || '会话',
      updatedAt: new Date(parsed.mtimeMs).toISOString(), cwd: parsed.cwd || cwd, status: running ? 'busy' : 'idle', isDesktopActive: running,
      metadata: {
        recentMessages: recentMessages(parsed.turns), turnStartedAt: Date.parse(String(latest.timestamp)) || 0,
        group: { id: `dir:${cwd}`, name: path.basename(cwd) || cwd || '其他', cwd },
      },
    });
  };

  registerEngine({
    ...IDENTITY, prefix: 'CC',
    character: { kind: 'image', url: '/assets/characters/claude.svg', pixelated: true },
    permissionModes: PERMISSION_MODES,
    look: {},
    // Every turn is the CLI's own run: no window to drive, no approvals to answer (a print run
    // asks nothing: what the permission mode does not allow is refused), nothing to steer.
    capabilities: { attachments: true, cwd: true, models: true, reasoning: true, interrupt: true, send: true, explicitSettings: true, steer: false, approvals: false, createRequiresPrompt: true },
    status: () => {
      if (!bridge.bin) return { connected: false, error: '没有找到 claude 命令行' };
      const auth = bridge.authStatus();
      return { connected: auth.loggedIn, ...(auth.loggedIn ? {} : { error: auth.error || 'Claude Code 未登录，在终端运行 claude 登录' }) };
    },
    models: () => bridge.getModels(), usage: () => bridge.getUsage(),
    list: () => transcripts().map(item => sessionOf(item.id)).filter((session): session is UnifiedSession => !!session),
    projects: () => {
      const seen = new Map<string, string>();
      for (const item of transcripts()) { const dir = startDir(item.id); if (dir && !seen.has(dir)) seen.set(dir, path.basename(dir) || dir); }
      return [...seen].map(([cwd, name]) => ({ id: `dir:${cwd}`, name, cwd }));
    },
    read: (id): History => {
      const history = retarget(bridge.read(id));
      const session = sessionOf(id);
      if (session && history.session) {
        history.session.engine = ENGINE; history.session.status = session.status; history.session.isDesktopActive = session.isDesktopActive;
        // The menus start on the model this session last used (capabilities.explicitSettings); the CLI keeps no effort in the record.
        const model = lastModel(id);
        history.session.metadata = { ...history.session.metadata, desktop: model ? { model } : {}, model: model || null };
      }
      const last = history.turns.at(-1);
      if (last && !last.statusEvidence && !runs.has(id) && !liveElsewhere(id)) last.status = 'completed';
      return history;
    },
    media: (id, key) => bridge.media(id, key),
    // A session running in a terminal is not refused: it reads as busy, so a message waits in the queue until that turn ends.
    blocked: () => (bridge.bin ? '' : '没有找到 claude 命令行，装好 Claude Code 后再试'),
    send: (id, input) => runTurn(id, input, { resume: true, cwd: startDir(id) }),
    // A new session in a folder: its first turn starts with the id given here, and is followed by observe().
    create: (input: CreateInput) => {
      const id = randomUUID(), cwd = input.cwd || config.defaultCwd || process.cwd();
      starts.set(id, runTurn(id, input, { resume: false, cwd }));
      return { id, cwd, started: true };
    },
    observe: id => {
      const emitter = starts.get(id);
      if (!emitter) throw Object.assign(new Error('没有正在执行的任务'), { status: 409 });
      starts.delete(id);
      return emitter;
    },
    interrupt: (id, emitter) => {
      const run = runs.get(id);
      if (run && !run.done) { run.interrupted = true; run.child.kill('SIGINT'); return { pending: true }; }
      emitter?.emit('interrupt');
      return { pending: false };
    },
    fileRoots: id => { const dir = startDir(id); return dir ? [dir] : []; },
  });
  return () => { for (const run of runs.values()) if (!run.done) run.child.kill('SIGINT'); };
};
