'use strict';
const { parentPort, workerData } = require('node:worker_threads');
const { parseIni, format, lex } = require('../vendor/formatter.js');
// Normalize only whitespace between tokens; multiline literals/comments stay exact.
function normalizeEol(sql, eol) {
  let position = 0, result = '';
  for (const token of lex(sql)) {
    result += sql.slice(position, token.start).replace(/\r?\n/g, eol) + token.value;
    position = token.end;
  }
  return result + sql.slice(position).replace(/\r?\n/g, eol);
}
function run(request) {
  const { settings, warnings } = parseIni(request.presetText);
  if (request.emptyLinesNumber !== null && request.emptyLinesNumber !== undefined) {
    if (!Number.isInteger(request.emptyLinesNumber) || request.emptyLinesNumber < 0 || request.emptyLinesNumber > 10) throw new Error('emptyLinesNumber должен быть от 0 до 10 или null.');
    for (const key of Object.keys(settings.General)) if (key.toLowerCase() === 'emptylinesnumber') delete settings.General[key];
    settings.General.EmptyLinesNumber = request.emptyLinesNumber;
  }
  const modes = { preserve: 0, lower: 1, upper: 2, title: 3 };
  if (request.caseMode !== undefined && request.caseMode !== 'preset' && !Object.hasOwn(modes, request.caseMode)) throw new Error('Неизвестный режим регистра.');
  const options = Object.hasOwn(modes, request.caseMode) ? { caseMode: modes[request.caseMode] } : {};
  const result = format(request.sql, settings, options);
  return { ...result, warnings, sql: normalizeEol(result.sql, request.eol === '\r\n' ? '\r\n' : '\n') };
}
if (parentPort) {
  try { parentPort.postMessage({ ok: true, result: run(workerData) }); }
  catch (error) { parentPort.postMessage({ ok: false, error: error.message }); }
}
module.exports = { run, normalizeEol };
