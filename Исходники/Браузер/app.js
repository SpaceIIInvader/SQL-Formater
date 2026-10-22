'use strict';
const $ = id => document.getElementById(id);
let settings, currentName = 'script.sql', iniName = 'preset_ibeexpert.ini', hasResult = false;
const demo = `SET TERM ^ ;
create or alter procedure check_dyte_stock(iddp type of column docplace.id, iddyte type of column stockgrp.id) returns(fact_exists boolean) as declare variable state_executed integer; begin state_executed=pkdpstate.get_executed(); fact_exists=exists(select 1 from docplaceexeitem dpei where dpei.iddocplace=:iddp and dpei.dyte=:iddyte and dpei.state_docplaceexeitem<=:state_executed); suspend; end^
SET TERM ; ^

select g.id,g.sarticul,g.scaption,sum(s.qty) as qty from good g left join stock s on s.idgood=g.id and s.qty>0 where g.id>0 and g.scaption is not null group by g.id,g.sarticul,g.scaption order by g.sarticul,g.id;`;
function status(message, mode = '') { $('status').textContent = message; $('status').className = 'status ' + mode; }
function count(id) {
  const el = $(id), n = el.value.split('\n').length;
  $(id + 'Lines').textContent = Array.from({ length: n }, (_, i) => i + 1).join('\n');
  $(id + 'Info').textContent = `${n.toLocaleString('ru')} строк · ${el.value.length.toLocaleString('ru')} символов`;
}
function stale() {
  if (hasResult) { hasResult = false; $('copy').disabled = $('download').disabled = true; $('output').value = ''; count('output'); $('timing').textContent = '—'; status('Исходные данные изменены. Нажмите «Форматировать».', 'warn'); }
}
function apply(text, name) {
  const parsed = SqlFormatter.parseIni(text);
  settings = parsed.settings; iniName = name; $('presetText').value = text; $('presetName').textContent = name;
  const sections = Object.keys(settings).length, count = Object.values(settings).reduce((n, s) => n + Object.keys(s).length, 0);
  $('presetSummary').textContent = `${sections} секций · ${count} параметров`;
  const ignored = SqlFormatter.unsupported(settings);
  $('ignored').textContent = ignored.length ? `${ignored.length} параметров:\n` + ignored.join('\n') : 'Все указанные параметры распознаны.';
  stale(); if (parsed.warnings.length) status(parsed.warnings.join(' · '), 'warn');
}
function save(text, name, type = 'text/plain;charset=utf-8') {
  const url = URL.createObjectURL(new Blob([text], { type })), a = document.createElement('a');
  a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function run() {
  if ($('formatBtn').disabled) { status('Сначала примените изменения INI.', 'warn'); return; }
  if (!$('input').value.trim()) { status('Добавьте SQL для форматирования.', 'warn'); $('input').focus(); return; }
  try {
    const start = performance.now(), mode = $('caseMode').value;
    const result = SqlFormatter.format($('input').value, settings, mode === 'preset' ? {} : { caseMode: Number(mode) });
    $('output').value = result.sql; count('output'); $('timing').textContent = Math.round(performance.now() - start) + ' мс';
    $('copy').disabled = $('download').disabled = false; hasResult = true;
    status(`Готово · сохранность ${result.tokens.toLocaleString('ru')} SQL-токенов проверена.`, 'ok');
  } catch (error) { $('output').value = ''; count('output'); hasResult = false; $('copy').disabled = $('download').disabled = true; status(error.message, 'error'); }
}
async function loadFile(file, isIni) {
  if (!file) return;
  if (file.size > 8000000) { status('Файл больше 8 МБ. Откройте часть скрипта.', 'error'); return; }
  try {
    const text = new TextDecoder($('encoding').value, { fatal: true }).decode(await file.arrayBuffer()).replace(/^\uFEFF/, '');
    if (isIni) { apply(text, file.name); status('Пресет загружен.'); }
    else { stale(); $('input').value = text; currentName = file.name; $('fileName').textContent = file.name; count('input'); status('SQL загружен. Нажмите «Форматировать».'); }
  } catch (error) { status('Не удалось прочитать файл: ' + error.message + ' Проверьте выбранную кодировку.', 'error'); }
}
$('formatBtn').onclick = run;
$('demo').onclick = () => { stale(); $('input').value = demo; currentName = 'example.sql'; $('fileName').textContent = currentName; count('input'); run(); };
$('openSql').onclick = () => $('sqlFile').click();
$('loadPreset').onclick = () => $('iniFile').click();
$('sqlFile').onchange = e => { loadFile(e.target.files[0], false); e.target.value = ''; };
$('iniFile').onchange = e => { loadFile(e.target.files[0], true); e.target.value = ''; };
$('resetPreset').onclick = () => { apply(DEFAULT_PRESET, 'preset_ibeexpert.ini'); status('Встроенный пресет восстановлен.'); };
$('applyPreset').onclick = () => { try { apply($('presetText').value, iniName); status('Изменённый INI применён.'); } catch (e) { status(e.message, 'error'); } };
$('savePreset').onclick = () => save($('presetText').value, iniName);
$('download').onclick = () => save($('crlf').checked ? $('output').value.replace(/\r?\n/g, '\r\n') : $('output').value, currentName.replace(/\.[^.]+$/, '') + '.formatted.sql');
$('copy').onclick = async () => {
  try { await navigator.clipboard.writeText($('output').value); status('SQL скопирован в буфер обмена.', 'ok'); }
  catch { $('output').focus(); $('output').select(); status('Нажмите Ctrl+C, чтобы скопировать выделенный результат.'); }
};
$('caseMode').onchange = stale;
$('presetText').oninput = () => { stale(); $('formatBtn').disabled = true; status('INI изменён. Нажмите «Применить INI».', 'warn'); };
$('applyPreset').addEventListener('click', () => { try { SqlFormatter.parseIni($('presetText').value); $('formatBtn').disabled = false; } catch {} });
$('resetPreset').addEventListener('click', () => { $('formatBtn').disabled = false; });
$('iniFile').addEventListener('change', () => { $('formatBtn').disabled = false; });
$('input').addEventListener('input', () => { stale(); count('input'); });
for (const id of ['input', 'output']) $(id).addEventListener('scroll', () => { $(id + 'Lines').scrollTop = $(id).scrollTop; });
$('input').addEventListener('keydown', e => {
  if (e.key === 'Tab') { e.preventDefault(); const el = e.target, start = el.selectionStart, end = el.selectionEnd; el.setRangeText(' '.repeat(Math.min(settings.General.BlockIndent ?? 2, 16)), start, end, 'end'); stale(); count('input'); }
});
document.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); if (!$('formatBtn').disabled) run(); } });
apply(DEFAULT_PRESET, iniName); count('input');
