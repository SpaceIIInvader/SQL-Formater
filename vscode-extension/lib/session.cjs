'use strict';
const path = require('node:path');
const { formatSql } = require('./service.cjs');
const { decodePreset } = require('./preset.cjs');

function createSession(vscode, context, formatter = formatSql) {
  const snapshots = new Map();
  let sequence = 0, unsupportedCount;
  const builtinUri = vscode.Uri.joinPath(context.extensionUri, 'presets', 'preset_ibeexpert.ini');
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  status.name = 'SQL Atelier'; status.command = 'sqlAtelier.menu';
  const currentDocument = () => vscode.window.activeTextEditor?.document;
  const scopeUri = document => document?.uri || vscode.workspace.workspaceFolders?.[0]?.uri;
  const config = document => vscode.workspace.getConfiguration('sqlAtelier', scopeUri(document));
  function resolvePreset(document) {
    const configured = config(document).get('presetPath', '').trim();
    if (!configured) return { uri: builtinUri, builtin: true };
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(configured)) return { uri: vscode.Uri.parse(configured), builtin: false };
    if (path.isAbsolute(configured)) return { uri: vscode.Uri.file(configured), builtin: false };
    const folder = vscode.workspace.getWorkspaceFolder(scopeUri(document));
    if (!folder) throw new Error('Для относительного пути к INI откройте папку проекта или укажите абсолютный путь.');
    return { uri: vscode.Uri.joinPath(folder.uri, configured), builtin: false };
  }
  async function loadPreset(document) {
    const selected = resolvePreset(document);
    try { return { ...selected, text: decodePreset(await vscode.workspace.fs.readFile(selected.uri)) }; }
    catch (error) { throw new Error('Не удалось прочитать INI-пресет: ' + error.message); }
  }
  function refreshStatus() {
    const document = currentDocument();
    if (!document || document.languageId !== 'sql') { status.hide(); return; }
    const selected = config(document).get('presetPath', '');
    status.text = '$(symbol-key) SQL Atelier';
    status.tooltip = 'Пресет: ' + (selected || 'встроенный') + '\nРегистр: ' + config(document).get('caseMode', 'preset') +
      (unsupportedCount === undefined ? '' : '\nПараметров INI вне поддержки: ' + unsupportedCount) + '\nНажмите для действий';
    status.show();
  }
  function requireEditor() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'sql') throw new Error('Откройте SQL-файл или выберите язык SQL для нового документа.');
    if (editor.document.uri.scheme === 'sql-atelier') throw new Error('Предпросмотр доступен только для чтения. Откройте исходный SQL.');
    return editor;
  }
  async function compute(document, token) {
    const original = document.getText(), version = document.version;
    const settings = config(document);
    // Capture all options before asynchronous reading/formatting.
    const caseMode = settings.get('caseMode', 'preset'), emptyLinesNumber = settings.get('emptyLinesNumber', null);
    const selected = await loadPreset(document);
    const result = await formatter({ sql: original, presetText: selected.text, caseMode, emptyLinesNumber, eol: document.eol === vscode.EndOfLine.CRLF ? '\r\n' : '\n' }, token);
    unsupportedCount = result.unsupported.length; refreshStatus();
    return { original, version, result };
  }
  function rangeFor(document) { return new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)); }
  function report(error) { if (error?.code !== 'CANCELLED') void vscode.window.showErrorMessage('SQL Atelier: ' + error.message); }
  async function formatDocument() {
    const editor = requireEditor(), document = editor.document;
    const { original, version, result } = await compute(document);
    if (document.isClosed || document.version !== version) throw new Error('SQL изменён во время форматирования. Повторите команду.');
    if (original === result.sql) return;
    if (!await editor.edit(edit => edit.replace(rangeFor(document), result.sql), { undoStopBefore: true, undoStopAfter: true })) throw new Error('Не удалось применить результат. Документ мог измениться.');
  }
  async function preview() {
    const editor = requireEditor();
    if (snapshots.size >= 32) throw new Error('Закройте ненужные окна предпросмотра перед созданием нового.');
    const document = editor.document;
    const { original, result } = await compute(document);
    const key = String(++sequence), name = path.posix.basename(document.uri.path) || 'script.sql';
    const left = vscode.Uri.parse('sql-atelier:/preview-' + key + '/before.sql');
    const right = vscode.Uri.parse('sql-atelier:/preview-' + key + '/after.sql');
    snapshots.set(left.toString(), original); snapshots.set(right.toString(), result.sql);
    try { await vscode.commands.executeCommand('vscode.diff', left, right, 'SQL Atelier · ' + name + ' · исходник ↔ результат'); }
    catch (error) { snapshots.delete(left.toString()); snapshots.delete(right.toString()); throw error; }
  }
  async function setPreset(uri, document = currentDocument()) {
    const scope = scopeUri(document), folder = scope && vscode.workspace.getWorkspaceFolder(scope);
    await config(document).update('presetPath', uri ? (uri.scheme === 'file' ? uri.fsPath : uri.toString()) : '', folder ? vscode.ConfigurationTarget.WorkspaceFolder : vscode.ConfigurationTarget.Global);
    unsupportedCount = undefined; refreshStatus();
  }
  async function selectPreset() {
    const document = currentDocument();
    const chosen = await vscode.window.showOpenDialog({ title: 'SQL Atelier — выбрать INI-пресет', canSelectMany: false, filters: { 'INI-пресеты': ['ini'] } });
    if (!chosen?.length) return;
    const text = decodePreset(await vscode.workspace.fs.readFile(chosen[0]));
    await formatter({ sql: '', presetText: text, caseMode: 'preset', eol: '\n' });
    await setPreset(chosen[0], document);
  }
  async function openPreset() {
    const document = currentDocument();
    let selected = resolvePreset(document);
    if (selected.builtin) {
      const folder = vscode.workspace.getWorkspaceFolder(scopeUri(document));
      const destination = await vscode.window.showSaveDialog({ title: 'SQL Atelier — сохранить копию встроенного пресета', defaultUri: folder ? vscode.Uri.joinPath(folder.uri, 'sql-atelier.ini') : undefined, filters: { 'INI-пресеты': ['ini'] } });
      if (!destination) return;
      await vscode.workspace.fs.writeFile(destination, await vscode.workspace.fs.readFile(builtinUri));
      await setPreset(destination, document); selected = { uri: destination };
    }
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(selected.uri));
  }
  const actions = {
    'sqlAtelier.format': formatDocument,
    'sqlAtelier.preview': preview,
    'sqlAtelier.selectPreset': selectPreset,
    'sqlAtelier.openPreset': openPreset,
    'sqlAtelier.resetPreset': () => setPreset(null),
    'sqlAtelier.settings': () => vscode.commands.executeCommand('workbench.action.openSettings', '@ext:sql-atelier-local.sql-atelier')
  };
  actions['sqlAtelier.menu'] = async () => {
    const choice = await vscode.window.showQuickPick([
      { label: '$(symbol-key) Форматировать SQL', command: 'sqlAtelier.format' },
      { label: '$(diff) Предпросмотр различий', command: 'sqlAtelier.preview' },
      { label: '$(folder-opened) Выбрать INI-пресет', command: 'sqlAtelier.selectPreset' },
      { label: '$(edit) Открыть / создать копию пресета', command: 'sqlAtelier.openPreset' },
      { label: '$(settings-gear) Настройки', command: 'sqlAtelier.settings' }
    ], { placeHolder: 'SQL Atelier — действие' });
    if (choice) await actions[choice.command]();
  };
  const provider = {
    async provideDocumentFormattingEdits(document, options, token) {
      if (document.uri.scheme === 'sql-atelier') return [];
      try {
        const { original, version, result } = await compute(document, token);
        if (token?.isCancellationRequested || document.isClosed || document.version !== version || result.sql === original) return [];
        return [vscode.TextEdit.replace(rangeFor(document), result.sql)];
      } catch (error) { await report(error); return []; }
    }
  };
  context.subscriptions.push(status,
    vscode.languages.registerDocumentFormattingEditProvider({ language: 'sql' }, provider),
    vscode.workspace.registerTextDocumentContentProvider('sql-atelier', { provideTextDocumentContent: uri => snapshots.get(uri.toString()) ?? '' }),
    vscode.workspace.onDidCloseTextDocument(document => { if (document.uri.scheme === 'sql-atelier') snapshots.delete(document.uri.toString()); }),
    vscode.window.onDidChangeActiveTextEditor(refreshStatus),
    vscode.workspace.onDidChangeConfiguration(event => { if (event.affectsConfiguration('sqlAtelier')) { unsupportedCount = undefined; refreshStatus(); } }),
    { dispose() { snapshots.clear(); } });
  for (const [command, action] of Object.entries(actions)) context.subscriptions.push(vscode.commands.registerCommand(command, async () => { try { await action(); } catch (error) { await report(error); } }));
  refreshStatus();
  return { provider };
}
module.exports = { createSession };
