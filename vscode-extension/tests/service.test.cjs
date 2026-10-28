'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { formatSql } = require('../lib/service.cjs');
const { decodePreset } = require('../lib/preset.cjs');
const { lex } = require('../vendor/formatter.js');
const presetText = fs.readFileSync(path.join(__dirname, '../presets/preset_ibeexpert.ini'), 'utf8');
const request = sql => ({ sql, presetText, caseMode: 'preset', eol: '\n' });
test('worker formats SQL, applies options, preserves tokens and is idempotent', async () => {
  const result = await formatSql({ ...request('select a from t; select b from t;'), caseMode: 'upper', emptyLinesNumber: 1 });
  assert.ok(result.sql.includes('SELECT A'));
  assert.ok(result.sql.includes(';\n\nSELECT B'));
  assert.equal((await formatSql({ ...request(result.sql), caseMode: 'upper', emptyLinesNumber: 1 })).sql, result.sql);
});
test('CRLF conversion changes whitespace only, preserving multiline literals and comments', async () => {
  const source = "select 'line1\nline2', q'[other\r\nline]' from t /* block\ncomment */ where x=1;";
  const result = await formatSql({ ...request(source), eol: '\r\n' });
  const signatures = sql => lex(sql).map(t => t.kind === 'word' ? t.upper : t.value);
  assert.deepEqual(signatures(result.sql), signatures(source));
  assert.ok(result.sql.includes('\r\nfrom t'));
});
test('invalid SQL, invalid preset and invalid overrides reject without a replacement', async () => {
  await assert.rejects(formatSql(request("select 'broken")), /Не закрыта/);
  await assert.rejects(formatSql({ ...request('select 1;'), presetText: '[General]\nBlockIndent=bad' }), /целое/);
  await assert.rejects(formatSql({ ...request('select 1;'), emptyLinesNumber: -1 }), /emptyLinesNumber/);
  await assert.rejects(formatSql(request(' '.repeat(2000001))), /2 млн/);
});
test('worker cancels before launch and during execution, and enforces a timeout', async () => {
  await assert.rejects(formatSql(request('select 1;'), { isCancellationRequested: true }), { code: 'CANCELLED' });
  let cancel;
  const token = { isCancellationRequested: false, onCancellationRequested(callback) { cancel = callback; return { dispose() {} }; } };
  const pending = formatSql(request('select a from t;'.repeat(10000)), token);
  cancel();
  await assert.rejects(pending, { code: 'CANCELLED' });
  await assert.rejects(formatSql(request('select 1;'), undefined, 1), /превысило/);
});
test('INI decodes UTF-8, UTF-16 BOM and Windows-1251, with a size bound', () => {
  assert.equal(decodePreset(Buffer.from(presetText)), presetText);
  const text = '; Русский\n[General]\nBlockIndent=2';
  assert.equal(decodePreset(Buffer.concat([Buffer.from([255, 254]), Buffer.from(text, 'utf16le')])), text);
  assert.equal(decodePreset(Buffer.from([59, 32, 207, 240, 232, 226, 229, 242, 10])), '; Привет\n');
  assert.throws(() => decodePreset(Buffer.alloc(240001)), /240/);
});
