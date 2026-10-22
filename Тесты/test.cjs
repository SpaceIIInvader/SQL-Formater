const assert = require('node:assert/strict');
const fs = require('node:fs');
const { format, lex, parseIni } = require('../Исходники/Ядро/formatter.js');
const preset = parseIni(fs.readFileSync(__dirname + '/../Пресеты/preset_ibeexpert.ini', 'utf8')).settings;
let count = 0;
function test(name, fn) { fn(); count++; console.log('OK ' + name); }
function fmt(sql, settings = preset) { return format(sql, settings).sql; }
function clone() { return structuredClone(preset); }
test('SELECT: comma placement and WHERE conjunction', () => {
  assert.equal(fmt('select a,b from t where x=1 and y=2;'), 'select a\n     , b\nfrom t\nwhere x = 1 and\n    y = 2;\n');
});
test('Quoted identifiers and string content remain exact', () => {
  const result = fmt('SELECT "MixedCase",\'a,b;-- X\' FROM "Table" WHERE "X"=\'a\'\'b\';');
  for (const literal of ['"MixedCase"', "'a,b;-- X'", '"Table"', "'a''b'"]) assert.ok(result.includes(literal));
});
test('Q-literals and nested comments', () => {
  const sql = "select q'[a'b;--]',q'{a,b}' from t /* outer /* inner */ end */;";
  assert.ok(fmt(sql).includes("q'[a'b;--]'"));
  assert.ok(fmt(sql).includes('/* outer /* inner */ end */'));
});
test('Comments cannot swallow following code', () => {
  const result = fmt('select a,-- keep\nb from t;');
  assert.ok(/-- keep\n/.test(result));
  assert.equal(lex(result).filter(t => t.value === 'b').length, 1);
});
test('BETWEEN separator stays on its line', () => {
  assert.ok(fmt('select a from t where a between 1 and 2 and b=3;').includes('between 1 and 2 and\n'));
});
test('Decimals and scientific notation', () => {
  assert.ok(fmt('select cast(1.25e-3 as numeric(10,2)) from rdb$database;').includes('numeric(10,2)'));
});
test('Preset changes indent and comma mode', () => {
  const s = clone(); s.Select.IndentBeforeColumn = 10; s.Select.CRLFBeforeCommaInColumnList = 0;
  assert.equal(fmt('select a,b from t;', s), 'select a,\n        b\nfrom t;\n');
});
test('OneColumnInLine=0', () => {
  const s = clone(); s.Select.OneColumnInLine = 0;
  assert.equal(fmt('select a,b from t;', s), 'select a, b\nfrom t;\n');
});
test('Keyword and identifier case are independent', () => {
  const s = clone(); s.General.KeywordCharCase = 2; s.General.IdentCharCase = 0;
  assert.equal(fmt('select MixedCase from MyTable;', s), 'SELECT MixedCase\nFROM MyTable;\n');
});
test('Case override', () => {
  assert.equal(format('select a from t;', preset, { caseMode: 2 }).sql, 'SELECT A\nFROM T;\n');
});
test('Invalid INI is rejected', () => {
  assert.throws(() => parseIni('[Select]\nOneColumnInLine=1'), /General/);
  assert.throws(() => parseIni('[General]\nBlockIndent=abc'), /целое/);
  assert.throws(() => parseIni('[General]\nBlockIndent=-2'), /диапазона/);
});
test('Unclosed source is rejected', () => {
  for (const sql of ["select 'oops", 'select (a from t;', 'select a);', '/* oops', "select q'[oops"]) assert.throws(() => fmt(sql));
});
test('Empty source', () => assert.equal(fmt(' \n '), ''));
test('Custom SET TERM delimiter is preserved atomically', () => {
  const sql = 'SET TERM !! ;\ncreate procedure p as begin suspend; end!!\nSET TERM ; !!';
  assert.ok(fmt(sql).includes('end!!\n'));
  assert.equal(fmt(sql), fmt(fmt(sql)));
});
test('DML, CASE, nested SELECT, PSQL: idempotent', () => {
  const examples = [
    'insert into t(a,b) values(1,2) returning a into :x;',
    'update t set a=2,b=3 where id=1 returning a into :x;',
    'delete from t where a=2 and b=3 returning a into :x;',
    'select case when x=1 then 2 else 3 end as result from t;',
    'select a,(select sum(x) from t2 where t2.id=t.id and x>0) as n from t;',
    'begin if (a>0) then begin x=1; end else x=2; suspend; end;',
    'create or alter package p as begin procedure a(x integer) returns(y integer); end^',
    'recreate package body p as begin procedure a(x integer) returns(y integer) as begin for select a,b from t into :x,:y do begin suspend; end end end^'
  ];
  for (const sql of examples) assert.equal(fmt(sql), fmt(fmt(sql)));
});
if (process.argv[2]) test('Supplied SQL file: token preservation and idempotence', () => {
  const sql = fs.readFileSync(process.argv[2], 'utf8');
  const result = fmt(sql); assert.equal(result, fmt(result));
  assert.equal(lex(sql).length, lex(result).length);
});
console.log(`${count} checks passed.`);
