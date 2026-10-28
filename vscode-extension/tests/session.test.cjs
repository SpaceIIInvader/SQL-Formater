'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { pathToFileURL, fileURLToPath } = require('node:url');
const { createSession } = require('../lib/session.cjs');
const root = path.resolve(__dirname, '..');
class Uri {
  constructor(value) { this.url = new URL(value); this.scheme = this.url.protocol.slice(0, -1); this.path = decodeURIComponent(this.url.pathname); }
  get fsPath() { return this.scheme === 'file' ? fileURLToPath(this.url) : this.path; }
  toString() { return this.url.toString(); }
  static file(value) { return new Uri(pathToFileURL(value).toString()); }
  static parse(value) { return new Uri(value); }
  static joinPath(uri, ...parts) { const url = new URL(uri.toString()); url.pathname = path.posix.join(url.pathname, ...parts); return new Uri(url.toString()); }
}
function mock(text = 'select a from t;') {
  const state = { text, errors: [], calls: [], updates: [], edits: 0, files: new Map(), values: { presetPath: '', caseMode: 'preset', emptyLinesNumber: null } };
  const document = { uri: Uri.file(path.join(root, 'sample.sql')), languageId: 'sql', version: 1, eol: 1, isClosed: false, getText: () => state.text, positionAt: offset => ({ offset }) };
  const folder = { uri: Uri.file(root), name: 'project', index: 0 };
  const configuration = { get: (key, fallback) => Object.hasOwn(state.values, key) ? state.values[key] : fallback, async update(key, value, target) { state.values[key] = value; state.updates.push({ key, value, target }); } };
  const disposable = () => ({ dispose() {} });
  const editor = { document, async edit(callback, options) { callback({ replace(range, newText) { state.text = newText; document.version++; } }); state.edits++; state.undoOptions = options; return true; } };
  const api = {
    Uri, Range: class { constructor(start, end) { this.start = start; this.end = end; } }, TextEdit: { replace: (range, newText) => ({ range, newText }) },
    EndOfLine: { LF: 1, CRLF: 2 }, StatusBarAlignment: { Right: 2 }, ConfigurationTarget: { Global: 1, WorkspaceFolder: 3 },
    window: {
      activeTextEditor: editor,
      createStatusBarItem() { state.status = { show() { this.visible = true; }, hide() { this.visible = false; }, dispose() {} }; return state.status; },
      onDidChangeActiveTextEditor(callback) { state.editorChanged = callback; return disposable(); },
      async showErrorMessage(message) { state.errors.push(message); },
      async showOpenDialog() { return state.openChoice; }, async showSaveDialog() { return state.saveChoice; },
      async showQuickPick() { return state.quickPick; }, async showTextDocument(doc) { state.shown = doc; }
    },
    workspace: {
      workspaceFolders: [folder], getWorkspaceFolder: uri => uri?.toString().startsWith(folder.uri.toString()) ? folder : undefined,
      getConfiguration: () => configuration,
      fs: { async readFile(uri) { return state.files.get(uri.toString()) ?? await fs.readFile(uri.fsPath); }, async writeFile(uri, bytes) { state.files.set(uri.toString(), bytes); } },
      registerTextDocumentContentProvider(scheme, provider) { state.contentProvider = provider; return disposable(); },
      onDidCloseTextDocument(callback) { state.closeDocument = callback; return disposable(); },
      onDidChangeConfiguration(callback) { state.configChanged = callback; return disposable(); },
      async openTextDocument(uri) { return { uri }; }
    },
    languages: { registerDocumentFormattingEditProvider(selector, provider) { state.selector = selector; state.provider = provider; return disposable(); } },
    commands: {
      registerCommand(command, callback) { state.commands ??= {}; state.commands[command] = callback; return disposable(); },
      async executeCommand(...args) { state.calls.push(args); }
    }
  };
  const context = { extensionUri: Uri.file(root), subscriptions: [] };
  return { state, api, context, document };
}
test('registers native SQL formatting, commands and status menu', () => {
  const m = mock(); createSession(m.api, m.context);
  assert.equal(m.state.selector.language, 'sql');
  assert.equal(m.state.status.command, 'sqlAtelier.menu');
  assert.equal(m.state.status.visible, true);
  assert.ok(m.state.commands['sqlAtelier.preview']);
  m.document.languageId = 'plaintext'; m.state.editorChanged();
  assert.equal(m.state.status.visible, false);
});
test('provider returns edits; explicit command updates the editor with undo stops', async () => {
  const m = mock(); createSession(m.api, m.context);
  const edits = await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {});
  assert.equal(edits.length, 1); assert.ok(edits[0].newText.includes('\nfrom'));
  assert.equal(m.state.edits, 0); assert.equal(m.state.text, 'select a from t;');
  await m.state.commands['sqlAtelier.format']();
  assert.equal(m.state.edits, 1); assert.ok(m.state.text.includes('\nfrom'));
  assert.deepEqual(m.state.undoOptions, { undoStopBefore: true, undoStopAfter: true });
});
test('format errors preserve input and return no provider edit', async () => {
  const m = mock("select 'broken"); createSession(m.api, m.context);
  assert.deepEqual(await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {}), []);
  assert.equal(m.state.text, "select 'broken"); assert.equal(m.state.edits, 0);
  assert.match(m.state.errors[0], /Не закрыта/);
});
test('preview opens native diff with immutable snapshots and no source edits', async () => {
  const m = mock(); createSession(m.api, m.context);
  await m.state.commands['sqlAtelier.preview']();
  const [command, left, right] = m.state.calls[0];
  assert.equal(command, 'vscode.diff');
  assert.equal(m.state.contentProvider.provideTextDocumentContent(left), 'select a from t;');
  assert.ok(m.state.contentProvider.provideTextDocumentContent(right).includes('\nfrom'));
  m.state.text = 'select changed;'; m.document.version++;
  assert.equal(m.state.contentProvider.provideTextDocumentContent(left), 'select a from t;');
  assert.equal(m.state.edits, 0);
  m.state.closeDocument({ uri: left });
  assert.equal(m.state.contentProvider.provideTextDocumentContent(left), '');
});
test('invalid preset selection does not replace current config; valid selection uses folder scope', async () => {
  const m = mock(); createSession(m.api, m.context);
  const uri = Uri.file(path.join(root, 'custom.ini'));
  m.state.openChoice = [uri]; m.state.files.set(uri.toString(), Buffer.from('[General]\nBlockIndent=no'));
  await m.state.commands['sqlAtelier.selectPreset']();
  assert.equal(m.state.updates.length, 0);
  m.state.files.set(uri.toString(), Buffer.from('[General]\nBlockIndent=4\nKeywordCharCase=2'));
  await m.state.commands['sqlAtelier.selectPreset']();
  assert.equal(m.state.updates[0].target, m.api.ConfigurationTarget.WorkspaceFolder);
  await m.state.commands['sqlAtelier.format']();
  assert.ok(m.state.text.includes('SELECT'));
});
test('relative preset resolution, saved INI changes and override values are used', async () => {
  const m = mock('update t set a=1; update t set a=2;');
  m.state.values.presetPath = 'custom.ini';
  const uri = Uri.joinPath(m.context.extensionUri, 'custom.ini');
  m.state.files.set(uri.toString(), Buffer.from('[General]\nEmptyLinesNumber=0'));
  createSession(m.api, m.context);
  let edits = await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {});
  assert.ok(!edits[0].newText.includes('\n\n'));
  m.state.files.set(uri.toString(), Buffer.from('[General]\nEmptyLinesNumber=1'));
  edits = await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {});
  assert.ok(edits[0].newText.includes('\n\n'));
  m.state.values.emptyLinesNumber = 0;
  edits = await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {});
  assert.ok(!edits[0].newText.includes('\n\n'));
});
test('built-in preset is copied before editing; original built-in bytes remain unchanged', async () => {
  const m = mock(); createSession(m.api, m.context);
  const builtin = await fs.readFile(path.join(root, 'presets/preset_ibeexpert.ini'));
  m.state.saveChoice = Uri.file(path.join(root, 'copy.ini'));
  await m.state.commands['sqlAtelier.openPreset']();
  assert.deepEqual(m.state.files.get(m.state.saveChoice.toString()), builtin);
  assert.equal(m.state.shown.uri.toString(), m.state.saveChoice.toString());
  assert.deepEqual(await fs.readFile(path.join(root, 'presets/preset_ibeexpert.ini')), builtin);
});
test('changed document versions are never overwritten by stale results', async () => {
  const m = mock();
  createSession(m.api, m.context, async () => { m.document.version++; m.state.text = 'new user text'; return { sql: 'old formatted text', unsupported: [] }; });
  assert.deepEqual(await m.state.provider.provideDocumentFormattingEdits(m.document, {}, {}), []);
  await m.state.commands['sqlAtelier.format']();
  assert.equal(m.state.edits, 0); assert.equal(m.state.text, 'new user text');
  assert.ok(m.state.errors.some(message => message.includes('изменён')));
});
