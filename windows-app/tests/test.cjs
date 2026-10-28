const assert = require('node:assert/strict');
const fs = require('node:fs');
const { format, lex, parseIni } = require('../src/formatter.js');
const preset = parseIni(fs.readFileSync(__dirname + '/../presets/preset_ibeexpert.ini', 'utf8')).settings;
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
test('Parameter and return type alignment can be enabled independently of declarations', () => {
  const sql = 'create procedure p(a int, longer varchar(4)) returns(x int, longname int) as declare variable a int; declare variable longer int; begin suspend; end';
  const s = clone();
  const plain = fmt(sql, s);
  assert.ok(plain.includes('    a int,\n'));
  s.InputOutput.AlignTypeDefinitions = 1;
  const aligned = fmt(sql, s);
  assert.ok(aligned.includes('    a      int,\n'));
  assert.ok(aligned.includes('    x        int,\n'));
  assert.ok(aligned.includes('declare variable a int;'));
  assert.equal(fmt(aligned, s), aligned);
  s.InputOutput.AlignTypeDefinitions = 0;
  assert.equal(fmt(aligned, s), plain);
});

test('Declaration type alignment respects routine boundaries and nested datatype brackets', () => {
  const s = clone(); s.DeclareSection.AlignTypeDefinitions = 1;
  const result = fmt('create procedure p as declare variable a numeric(10,2); declare variable longer type of column t.id; begin suspend; end', s);
  assert.ok(result.includes('declare variable a      numeric(10,2);'));
  assert.ok(result.includes('declare variable longer type of column t.id;'));
  assert.equal(result, fmt(result, s));
  s.DeclareSection.AlignTypeDefinitions = 0;
  assert.ok(fmt(result, s).includes('declare variable a numeric(10,2);'));
});

test('Cursor declaration SELECT newline and indent are effective without changing ordinary SELECT', () => {
  const sql = 'declare c cursor for (select a from t);';
  const s = clone(); s.DeclareSection.IndentBeforeSELECT = 7;
  assert.ok(fmt(sql, s).includes('for (\n       select a\n'));
  s.DeclareSection.CRLFBeforeSELECT = 0;
  assert.ok(fmt(sql, s).includes('for (select a\n'));
  const normal = fmt('select a from t;');
  assert.equal(fmt('select a from t;', s), normal);
  assert.equal(fmt(sql, s), fmt(fmt(sql, s), s));
});

test('INSERT SELECT uses INSERT settings, not declaration or FOR settings', () => {
  const s = clone(); s.Insert.CRLFBeforeSELECT = 0;
  assert.ok(fmt('insert into t select a from q;', s).includes('insert into t select a'));
  s.Insert.CRLFBeforeSELECT = 1; s.Insert.IndentBeforeSELECT = 6;
  assert.ok(fmt('insert into t select a from q;', s).includes('insert into t\n      select a'));
});

test('Matching INI spelling and matching-column indentation are applied', () => {
  const s = clone(); s.Insert.CRLFAfterMatching = 1; s.Insert.OneMatchingColumnInLine = 1;
  s.Insert.IndentBeforeMATCHINGColumns = 8;
  const result = fmt('update or insert into t(a,b) values(1,2) matching(a,b);', s);
  assert.ok(result.includes('matching\n        (a,\n        b)'));
  assert.equal(result, fmt(result, s));
});

test('RETURNING and INTO lists use their own settings for each DML statement', () => {
  for (const [section, sql] of [['Insert', 'insert into t(a,b) values(1,2) returning a,b into :x,:y;'], ['Update', 'update t set a=1 returning a,b into :x,:y;'], ['Delete', 'delete from t returning a,b into :x,:y;']]) {
    const s = clone(); s[section].OneReturningColumnInLine = 1; s[section].IndentBeforeRETURNINGColumns = 9; s[section].OneIntoVariableInLine = 1;
    const result = fmt(sql, s);
    assert.ok(result.includes('returning a,\n         b'));
    assert.ok(result.includes('into :x,\n    :y'));
    assert.equal(result, fmt(result, s));
    s[section].OneReturningColumnInLine = 0;
    assert.ok(fmt(sql, s).includes('returning a, b'));
  }
});

test('IF boolean line breaks work inside outer parentheses and preserve BETWEEN', () => {
  const s = clone();
  Object.assign(s.IfThenElse, { CRLFBeforeAND: 0, CRLFAfterAND: 0, CRLFBeforeOR: 0, CRLFAfterOR: 0 });
  const sql = 'begin if ((a=1) and (b=2) or (c=3)) then x=1; end';
  assert.ok(fmt(sql, s).includes('(a = 1) and (b = 2) or (c = 3)'));
  for (const op of ['AND', 'OR']) for (const placement of ['Before', 'After']) {
    const c = structuredClone(s); c.IfThenElse['CRLF' + placement + op] = 1;
    const result = fmt(sql, c);
    assert.ok(new RegExp(placement === 'Before' ? '\\n +'+op.toLowerCase()+' ' : op.toLowerCase()+'\\n +').test(result));
    assert.equal(result, fmt(result, c));
  }
  s.IfThenElse.CRLFAfterAND = 1;
  assert.ok(fmt('begin if (a between 1 and 2 and b=3) then x=1; end', s).includes('between 1 and 2 and\n'));
});

test('CASE expression indentation follows the preset', () => {
  const s = clone(); s.Case.CRLFAfterTHEN = 1; s.Case.IndentBeforeTHENExpression = 11;
  const result = fmt('select case when a=1 then 2 else 3 end from t;', s);
  assert.ok(result.includes('then\n           2'));
  assert.equal(result, fmt(result, s));
});

test('User procedure preserves tokens, header layout, INSERT list, comments and stable formatting', () => {
  const sql = fs.readFileSync(__dirname + '/fixtures/set-item-good.sql', 'utf8');
  const result = fmt(sql);
  assert.equal(result.split('  begin\n')[0], sql.split('  begin\n')[0]);
  assert.ok(result.includes('      insert into taskitem_datamatrix (\n          idtaskitem,'));
  assert.ok(result.includes('if ((:id_dpi is null) or (:id_dpi = 0)) then'));
  assert.equal(result, fmt(result));
  const signature = source => lex(source).map(t => t.kind === 'word' ? t.upper : t.value);
  assert.deepEqual(signature(result), signature(sql));
});

test('Logical spacing separates queries, guards and assignments without splitting every statement', () => {
  const sql = `create procedure p as
declare variable saved_istage integer;
declare variable n integer;
begin
select count(*) from t into :n;
if (n <> 1) then exception e_error 'stage';
if (n is null) then exception e_error 'quantity';
saved_istage = null;
select x from t into :saved_istage;
if (saved_istage is null) then
-- attached to the guarded update
update or insert into t(x) values(1) matching(x);
update t set x=0;
update t set x=1;
success=1;
message='done';
suspend;
end`;
  const s = clone(); s.General.KeepEmptyLines = 0; s.General.EmptyLinesNumber = 1;
  const result = fmt(sql, s);
  for (const pattern of ['into :n;\n\n  if', "'stage';\n\n  if", "'quantity';\n\n  saved_istage", 'saved_istage = null;\n\n  select', 'into :saved_istage;\n\n  if']) assert.ok(result.includes(pattern), pattern);
  assert.ok(!result.includes('integer;\n\ndeclare'));
  assert.ok(result.includes('success = 1;\n  message'));
  assert.ok(result.includes("message = 'done';\n  suspend;\nend"));
  assert.ok(result.includes('-- attached to the guarded update\n'));
  assert.ok(!result.includes('-- attached to the guarded update\n\n'));
  assert.equal(result, fmt(result, s));
  s.General.EmptyLinesNumber = 0;
  assert.ok(!fmt(sql, s).includes('\n\n'));
  s.General.EmptyLinesNumber = 2;
  const two = fmt(sql, s);
  assert.ok(two.includes('into :n;\n\n\n  if'));
  assert.equal(two, fmt(two, s));
});

test('Logical spacing respects nested BEGIN/END, ELSE and CASE boundaries', () => {
  const s = clone(); s.General.KeepEmptyLines = 0; s.General.EmptyLinesNumber = 1;
  const sql = 'begin if (x=1) then begin select a from t into :n; if (n=0) then exception e_error; end else begin x=2; y=3; end select case when n=1 then 2 else 3 end from t into :x; if (x=1) then x=2; end';
  const result = fmt(sql, s);
  assert.ok(result.includes('end\n  else\n'));
  assert.ok(result.includes('x = 2;\n    y = 3;'));
  assert.ok(result.includes('into :x;\n\n  if'));
  assert.equal(result, fmt(result, s));
});

test('Replan procedure gets requested logical gaps and keeps guarded comments attached', () => {
  const sql = fs.readFileSync(__dirname + '/fixtures/block-source-and-replan-dpi.sql', 'utf8');
  const result = fmt(sql);
  assert.ok(result.includes('into :stage_count, :idstage, :istage;\n\n        if'));
  assert.ok(result.includes("exactly one planning stage';\n\n        if"));
  assert.ok(result.includes("known TASKITEM quantity';\n\n        saved_istage = null;\n\n        select"));
  assert.ok(result.includes('into :saved_istage;\n\n        if'));
  assert.ok(result.includes('-- GET_STAGE_QTY_TARGET recalculates the target from corrected DPI.\n          update'));
  assert.ok(result.includes("success = 1;\n    message = 'Товар заблокирован. Разница отправлена в планирование';\n    suspend;\n  end"));
  assert.equal(result, fmt(result));
  const signature = text => lex(text).map(t => t.kind === 'word' ? t.upper : t.value);
  assert.deepEqual(signature(result), signature(sql));
});

if (process.argv[2]) test('Supplied SQL file: token preservation and idempotence', () => {
  const sql = fs.readFileSync(process.argv[2], 'utf8');
  const result = fmt(sql); assert.equal(result, fmt(result));
  assert.equal(lex(sql).length, lex(result).length);
});
console.log(`${count} checks passed.`);
