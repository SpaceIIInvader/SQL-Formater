'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vscode = require('vscode');
exports.run = async () => {
  const report = path.join(__dirname, '../.test-integration-result.json');
  const checks = [];
  try {
    const extension = vscode.extensions.getExtension('sql-atelier-local.sql-atelier');
    assert.ok(extension, 'Installed extension not found');
    await extension.activate(); checks.push('Extension activation');
    const input = 'select a from t;';
    const document = await vscode.workspace.openTextDocument({ language: 'sql', content: input });
    await vscode.window.showTextDocument(document);
    const edits = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { tabSize: 2, insertSpaces: true });
    assert.ok(edits.length > 0);
    let formatted = input;
    for (const edit of edits.slice().sort((a, b) => document.offsetAt(b.range.start) - document.offsetAt(a.range.start))) {
      formatted = formatted.slice(0, document.offsetAt(edit.range.start)) + edit.newText + formatted.slice(document.offsetAt(edit.range.end));
    }
    assert.ok(formatted.includes('\nfrom t'));
    assert.equal(document.getText(), input); checks.push('Native formatting provider');
    await vscode.commands.executeCommand('sqlAtelier.format');
    assert.ok(document.getText().includes('\nfrom t')); checks.push('Format command applies edits');
    await vscode.commands.executeCommand('undo');
    assert.equal(document.getText(), input); checks.push('Native undo');
    await vscode.commands.executeCommand('sqlAtelier.preview');
    const previews = vscode.workspace.textDocuments.filter(doc => doc.uri.scheme === 'sql-atelier');
    assert.equal(previews.length, 2);
    assert.ok(previews.some(doc => doc.getText() === input));
    assert.ok(previews.some(doc => doc.getText().includes('\nfrom t')));
    assert.equal(document.getText(), input); checks.push('Native diff with source preservation');
    const invalid = await vscode.workspace.openTextDocument({ language: 'sql', content: "select 'broken" });
    await vscode.window.showTextDocument(invalid);
    const rejected = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', invalid.uri, { tabSize: 2, insertSpaces: true });
    assert.equal(rejected?.length || 0, 0); assert.equal(invalid.getText(), "select 'broken");
    checks.push('Invalid SQL cannot modify input');
    fs.writeFileSync(report, JSON.stringify({ ok: true, vscode: vscode.version, checks }, null, 2));
  } catch (error) {
    fs.writeFileSync(report, JSON.stringify({ ok: false, checks, error: error.stack }, null, 2));
    throw error;
  }
};
