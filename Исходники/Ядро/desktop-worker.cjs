'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { format, parseIni } = require('./formatter.js');
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const skipped = new Set(['.git', '.sql-atelier-backup', 'node_modules']);
function inside(root, target) { const rel = path.relative(root, target); return rel !== '' && !rel.startsWith('..' + path.sep) && rel !== '..' && !path.isAbsolute(rel); }
let cpMap;
function decode(bytes) {
  let encoding = 'utf-8', bom = Buffer.alloc(0), payload = bytes;
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) { bom = bytes.subarray(0, 3); payload = bytes.subarray(3); }
  else if (bytes[0] === 0xff && bytes[1] === 0xfe) { encoding = 'utf-16le'; bom = bytes.subarray(0, 2); payload = bytes.subarray(2); }
  else if (bytes[0] === 0xfe && bytes[1] === 0xff) { encoding = 'utf-16be'; bom = bytes.subarray(0, 2); payload = bytes.subarray(2); }
  let text;
  try { text = new TextDecoder(encoding, { fatal: true }).decode(payload); }
  catch (e) {
    if (bom.length) throw e;
    encoding = 'windows-1251'; text = new TextDecoder(encoding, { fatal: true }).decode(payload);
  }
  if (text.includes('\0')) throw new Error('Обнаружены нулевые байты: проверьте кодировку файла.');
  return { text, encoding, bom };
}
function encode(text, info) {
  let body;
  if (info.encoding === 'windows-1251') {
    if (!cpMap) {
      cpMap = new Map(); const decoder = new TextDecoder('windows-1251');
      for (let i = 0; i < 256; i++) { const ch = decoder.decode(Uint8Array.of(i)); if (ch !== '\ufffd') cpMap.set(ch, i); }
    }
    body = Buffer.from(Array.from(text, c => { const b = cpMap.get(c); if (b === undefined) throw new Error('Символ не представим в Windows-1251: ' + c); return b; }));
  } else if (info.encoding === 'utf-16be') {
    body = Buffer.from(text, 'utf16le'); body.swap16();
  } else body = Buffer.from(text, info.encoding === 'utf-16le' ? 'utf16le' : 'utf8');
  return Buffer.concat([info.bom, body]);
}
async function analyze(request) {
  const root = await fs.realpath(path.resolve(request.folder));
  if (!(await fs.stat(root)).isDirectory()) throw new Error('Укажите папку с SQL-файлами.');
  const { settings, warnings } = parseIni(request.presetText);
  const rows = []; let total = 0;
  async function walk(dir) {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    entries.sort((a, b) => a.name.localeCompare(b.name));
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isSymbolicLink()) continue;
      if (entry.isDirectory()) { if (request.recursive && !skipped.has(entry.name.toLowerCase())) await walk(full); continue; }
      if (!entry.isFile() || !/\.sql$/i.test(entry.name)) continue;
      if (rows.length >= 5000) throw new Error('Больше 5000 SQL-файлов. Выберите папку меньшего размера.');
      const row = { relative: path.relative(root, full), status: 'error', message: '', encoding: '' };
      rows.push(row);
      try {
        const stat = await fs.stat(full);
        if (stat.size > 8000000) throw new Error('Файл больше 8 МБ.');
        total += stat.size;
        if (total > 64000000) throw new Error('Объём анализа превышает 64 МБ. Выберите папку меньшего размера.');
        const original = await fs.readFile(full), info = decode(original);
        const options = request.caseMode === undefined || request.caseMode === null ? {} : { caseMode: request.caseMode };
        const formatted = format(info.text, settings, options);
        let text = formatted.sql;
        const newline = info.text.includes('\r\n') ? '\r\n' : info.text.includes('\r') && !info.text.includes('\n') ? '\r' : '\n';
        text = text.replace(/\n/g, newline);
        if (!/[\r\n]$/.test(info.text)) text = text.replace(/[\r\n]+$/, '');
        const bytes = encode(text, info);
        row.encoding = info.encoding;
        row.status = original.equals(bytes) ? 'unchanged' : 'changed';
        row.message = row.status === 'changed' ? 'Готов к сохранению' : 'Уже выровнен';
        row.originalHash = hash(original);
        if (row.status === 'changed') { row.data = bytes.toString('base64'); row.source = info.text; row.preview = text; }
      } catch (e) { row.message = e.message; }
      if (total > 64000000) throw new Error('Объём анализа превышает 64 МБ. Выберите папку меньшего размера.');
    }
  }
  await walk(root);
  return { root, rows, warnings, unsupported: require('./formatter.js').unsupported(settings) };
}
async function save(request) {
  const root = await fs.realpath(path.resolve(request.root));
  const changed = request.rows.filter(r => r.status === 'changed');
  if (!changed.length) return { rows: [], backup: '', saved: 0 };
  const backupName = new Date().toISOString().replace(/[:.]/g, '-') + '-' + crypto.randomUUID().slice(0, 8);
  const backup = path.join(root, '.sql-atelier-backup', backupName);
  // Reject a pre-existing linked backup directory before creating anything inside it.
  try { if ((await fs.lstat(path.join(root, '.sql-atelier-backup'))).isSymbolicLink()) throw new Error('Папка резервных копий не должна быть ссылкой.'); }
  catch (e) { if (e.code !== 'ENOENT') throw e; }
  await fs.mkdir(backup, { recursive: true });
  const rows = []; let saved = 0;
  for (const row of changed) {
    let temporary;
    const result = { relative: row.relative, status: 'error', message: '' }; rows.push(result);
    try {
      if (path.isAbsolute(row.relative)) throw new Error('Ожидается относительный путь.');
      const target = path.resolve(root, row.relative);
      if (!inside(root, target)) throw new Error('Путь вне выбранной папки.');
      const canonical = await fs.realpath(target);
      if (!inside(root, canonical)) throw new Error('Файл ссылается за пределы выбранной папки.');
      if ((await fs.lstat(target)).isSymbolicLink()) throw new Error('Файл стал символической ссылкой.');
      const original = await fs.readFile(target);
      if (hash(original) !== row.originalHash) throw new Error('Файл изменился после проверки. Повторите анализ.');
      const destination = path.resolve(backup, row.relative);
      if (!inside(backup, destination)) throw new Error('Некорректный путь резервной копии.');
      await fs.mkdir(path.dirname(destination), { recursive: true });
      await fs.writeFile(destination, original, { flag: 'wx' });
      temporary = path.join(path.dirname(target), '.' + path.basename(target) + '.sqlatelier-' + crypto.randomUUID() + '.tmp');
      await fs.writeFile(temporary, Buffer.from(row.data, 'base64'), { flag: 'wx', mode: (await fs.stat(target)).mode });
      if (hash(await fs.readFile(target)) !== row.originalHash) throw new Error('Файл изменился во время сохранения. Исходник не заменён.');
      await fs.rename(temporary, target); temporary = null;
      result.status = 'saved'; result.message = 'Сохранён; оригинал в резервной копии'; saved++;
    } catch (e) { result.message = e.message; }
    finally { if (temporary) await fs.unlink(temporary).catch(() => {}); }
  }
  return { rows, backup, saved };
}
module.exports = { analyze, save, decode, encode };
if (require.main === module) {
  let input = ''; process.stdin.setEncoding('utf8');
  process.stdin.on('data', chunk => { input += chunk; });
  process.stdin.on('end', async () => {
    try {
      const request = JSON.parse(input);
      const result = request.operation === 'analyze' ? await analyze(request) : request.operation === 'save' ? await save(request) : (() => { throw new Error('Неизвестная операция.'); })();
      process.stdout.write(JSON.stringify({ ok: true, result }));
    } catch (e) { process.stdout.write(JSON.stringify({ ok: false, error: e.message })); process.exitCode = 1; }
  });
}
