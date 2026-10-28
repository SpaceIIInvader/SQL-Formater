(function (root) {
  'use strict';
  const keywords = new Set(('select from where into join left right inner outer full cross natural on as and or not null is distinct all first skip rows offset fetch next only union intersect except order by group having plan asc desc nulls last insert update delete merge using when matched then else end case values returning matching set begin do if for while execute procedure function returns return suspend exit leave continue declare variable type of column integer int bigint smallint varchar char boolean numeric decimal double precision float date time timestamp blob text true false exists in between like containing starting with recursive create alter recreate package body trigger view table exception break coalesce sum count min max avg list cast iif trim substring upper lower char_length ascii_char new old current_timestamp current_date current_time current_user current_role row_count sqlcode gdscode sqlstate count over partition window to value default constraint primary key foreign references check unique collate character charset domain computed generated always identity before after active inactive position transaction autonomous in_autonomous_transaction external statement block password user caller privileges returning_values with lock wait no action cascade coalesce extract year month day hour minute second grant revoke term names commit rollback connect database savepoint release asc descending ascending boolean deterministic routine parameter nullif replace gen_id increment bin_and bin_or rdb$get_context rdb$set_context').split(/\s+/));

  function parseIni(text) {
    const result = {}, warnings = []; let section = '';
    String(text).replace(/^\uFEFF/, '').split(/\r?\n/).forEach((line, index) => {
      line = line.trim();
      if (!line || /^[;#]/.test(line)) return;
      const header = line.match(/^\[([^\]]+)\]\s*(?:[;#].*)?$/);
      if (header) { section = header[1]; result[section] ||= {}; return; }
      const pair = line.match(/^([^=]+)=(.*)$/);
      if (!section || !pair) { warnings.push(`Строка ${index + 1}: ожидается секция или ключ=значение`); return; }
      const key = pair[1].trim(), raw = pair[2].trim();
      result[section][key] = /^-?\d+$/.test(raw) ? Number(raw) : raw;
    });
    if (!result.General) throw new Error('В INI отсутствует секция [General].');
    for (const [section, entries] of Object.entries(result)) for (const [key, value] of Object.entries(entries)) {
      if (typeof value !== 'number') throw new Error(`[${section}] ${key}: требуется целое число.`);
      if (value < 0 || value > 1000) throw new Error(`[${section}] ${key}: значение вне диапазона 0–1000.`);
    }
    return { settings: result, warnings };
  }

  function lex(sql) {
    const tokens = []; let i = 0, terminator = ';';
    const push = (kind, start, end) => tokens.push({ kind, value: sql.slice(start, end), upper: sql.slice(start, end).toUpperCase(), start, end });
    while (i < sql.length) {
      if (/\s/.test(sql[i])) { i++; continue; }
      const s = i;
      if (/^set\s+term\b/i.test(sql.slice(i)) && (i === 0 || /^[\t ]*$/.test(sql.slice(sql.lastIndexOf('\n', i - 1) + 1, i)))) {
        while (i < sql.length && !/[\r\n]/.test(sql[i])) i++;
        push('directive', s, i);
        const term = sql.slice(s, i).match(/^set\s+term\s+(\S+)/i);
        if (term) terminator = term[1];
        continue;
      }
      if (sql.startsWith('--', i)) { while (i < sql.length && !/[\r\n]/.test(sql[i])) i++; push('comment', s, i); continue; }
      if (sql.startsWith('/*', i)) {
        let depth = 1; i += 2;
        while (i < sql.length && depth) {
          if (sql.startsWith('/*', i)) { depth++; i += 2; }
          else if (sql.startsWith('*/', i)) { depth--; i += 2; }
          else i++;
        }
        if (depth) throw new Error('Не закрыт комментарий /* … */.');
        push('comment', s, i); continue;
      }
      if ((sql[i] === 'q' || sql[i] === 'Q') && sql[i + 1] === "'" && sql[i + 2]) {
        const closing = ({ '[': ']', '{': '}', '(': ')', '<': '>' })[sql[i + 2]] || sql[i + 2];
        const end = sql.indexOf(closing + "'", i + 3);
        if (end < 0) throw new Error('Не закрыта строка Q-литерала.');
        i = end + 2; push('string', s, i); continue;
      }
      if (sql[i] === "'" || sql[i] === '"') {
        const quote = sql[i++]; let closed = false;
        while (i < sql.length) {
          if (sql[i++] === quote) { if (sql[i] === quote) i++; else { closed = true; break; } }
        }
        if (!closed) throw new Error('Не закрыта строка или идентификатор в кавычках.');
        push(quote === "'" ? 'string' : 'quoted', s, i); continue;
      }
      if (terminator !== ';' && sql.startsWith(terminator, i)) { i += terminator.length; push('terminator', s, i); continue; }
      const word = sql.slice(i).match(/^[\p{L}_$][\p{L}\p{N}_$]*/u);
      if (word) { i += word[0].length; push('word', s, i); continue; }
      const number = sql.slice(i).match(/^(?:0x[\da-f]+|\d+(?:\.\d+)?(?:e[+-]?\d+)?)/i);
      if (number) { i += number[0].length; push('number', s, i); continue; }
      const op = sql.slice(i).match(/^(?:>=|<=|<>|!=|\|\||:=|!<|!>|<<|>>)/);
      i += op ? op[0].length : 1; push('symbol', s, i);
    }
    return tokens;
  }

  const supported = {
    General: 'BlockIndent IdentCharCase KeywordCharCase KeepEmptyLines EmptyLinesNumber SpaceAfterComma RightMargin NoSpaceAfterCommaInDatatypes NoSpaceBetweenProcNameAndBracket StartSingleLineCommentsFromFirstColumn SpaceAfterOpeningBracket SpaceBeforeClosingBracket',
    Select: 'CRLFBeforeColumnsList CRLFBeforeCommaInColumnList CRLFBeforeCommaInVarsList IndentBeforeColumn OneColumnInLine OneVariableInLine OneOrderByItemInLine CRLFBeforeCommaInOrderByList',
    InputOutput: 'Indent CRLFBeforeRETURNS AlignTypeDefinitions',
    DeclareSection: 'IndentBeforeDECLARE CRLFBeforeSELECT IndentBeforeSELECT AlignTypeDefinitions',
    Insert: 'CRLFBeforeListOfInsertedColumns OneInsertedColumnInLine OneIntoVariableInLine OneValuesExpressionInLine OneMatchingColumnInLine OneReturningColumnInLine ColumnValueIndent IndentBeforeMATCHINGColumns IndentBeforeRETURNINGColumns',
    Update: 'CRLFAfterUPDATE IndentBeforeTableName CRLFBeforeCommaInColumnList IndentBeforeFieldValue OneColumnValueExpressionInLine OneIntoVariableInLine OneReturningColumnInLine IndentBeforeRETURNINGColumns',
    Delete: 'OneIntoVariableInLine OneReturningColumnInLine IndentBeforeTableName IndentBeforeRETURNINGColumns',
    For: 'CRLFBeforeSELECT CRLFBeforeEXECUTE CRLFBeforeWITH CRLFBeforeDO CRLFBeforeBEGIN SelectExecuteIndent',
    IfThenElse: 'CRLFAfterTHEN NoCRLFBetweenTHEN_BEGIN CRLFAfterELSE NoCRLFBetweenELSE_IF NoCRLFBetweenELSE_BEGIN IndentBeforeELSE CRLFBeforeAND CRLFAfterAND CRLFBeforeOR CRLFAfterOR',
    While: 'CRLFBeforeDO CRLFBeforeBEGIN', When: 'CRLFBeforeDO CRLFBeforeBEGIN',
    Where: 'CRLFBeforeAND CRLFAfterAND CRLFBeforeOR CRLFAfterOR IndentBeforeOR IndentBeforeAND',
    JoinOn: 'CRLFBeforeAND CRLFAfterAND CRLFBeforeOR CRLFAfterOR CRLFBeforeON CRLFAfterON IndentBeforeOR IndentBeforeAND IndentBeforeON',
    MergeOn: 'CRLFBeforeAND CRLFAfterAND CRLFBeforeOR CRLFAfterOR IndentBeforeOR IndentBeforeAND',
    Case: 'CRLFBeforeWHEN CRLFBeforeELSE CRLFBeforeTHEN CRLFBeforeEND CRLFAfterTHEN IndentBeforeWHEN IndentBeforeTHEN IndentBeforeELSE IndentBeforeTHENExpression',
    Merge: 'CRLFBeforeUSING CRLFBeforeON CRLFBeforeWHEN CRLFBeforeINSERT_UPDATE IndentBeforeINSERT_UPDATE'
  };
  const clauses = ['FROM', 'WHERE', 'INTO', 'JOIN', 'UNION', 'ORDER_BY', 'GROUP_BY', 'HAVING', 'PLAN', 'ROWS', 'OFFSET', 'FETCH', 'VALUES', 'MATCHING', 'RETURNING', 'SET', 'SELECT'];
  for (const section of ['Select', 'Insert', 'Update', 'Delete']) {
    supported[section] += ' ' + clauses.flatMap(c => [`CRLFBefore${c}`, `CRLFAfter${c}`, `IndentBefore${c}`, `IndentBefore${c}Items`]).join(' ');
  }
  function unsupported(settings) {
    const ignored = [];
    for (const [s, entries] of Object.entries(settings)) for (const k of Object.keys(entries))
      if (!(supported[s] || '').toLowerCase().split(' ').includes(k.toLowerCase())) ignored.push(`[${s}] ${k}`);
    return ignored;
  }

  function format(sql, settings, options = {}) {
    if (sql.length > 2000000) throw new Error('Максимальный размер скрипта — 2 млн символов. Разделите файл на части.');
    const tokens = lex(sql), lines = []; let line = '', depth = 0, indent = 0, statement = '', clause = '', clauseIndent = 0, statementBase = 0;
    let prev = null, pendingIndent = null, routine = false, signature = false, forLoop = false, condition = false, declaration = false, declarationIndent = 0;
    const frames = [], blocks = [], queryStack = [];
    const normalized = Object.fromEntries(Object.entries(settings).map(([s, entries]) => [s, Object.fromEntries(Object.entries(entries).map(([k, v]) => [k.toLowerCase(), v]))]));
    const val = (s, k, fallback = 0) => normalized[s]?.[k.toLowerCase()] ?? fallback;
    const step = Math.min(val('General', 'BlockIndent', 2), 16);
    const margin = Math.max(40, Math.min(val('General', 'RightMargin', 120), 500));
    const blanks = Math.min(val('General', 'EmptyLinesNumber', 0), 10);
    // A bare, indented procedure can be pasted from a package body.
    if (['PROCEDURE', 'FUNCTION'].includes(tokens[0]?.upper)) {
      const prefix = sql.slice(0, tokens[0].start);
      indent = /^[\t ]*$/.test(prefix) ? prefix.replace(/\t/g, ' '.repeat(step)).length : 0;
    }
    let lineIndent = indent;
    const typePadding = new Map();
    function alignTypes(entries) {
      if (!entries.length) return;
      entries = entries.filter(([name, type]) => tokens[name] && tokens[type]);
      const width = entries.reduce((maximum, [name]) => Math.max(maximum, tokens[name].value.length), 0);
      for (const [name, type] of entries) typePadding.set(type, width - tokens[name].value.length);
    }
    if (val('InputOutput', 'AlignTypeDefinitions')) {
      let header = false, level = 0, entries = [], first = true, name = -1;
      for (let k = 0; k < tokens.length; k++) {
        const token = tokens[k];
        if (token.kind === 'comment') continue;
        if (['PROCEDURE', 'FUNCTION'].includes(token.upper) && tokens[k - 1]?.upper !== 'EXECUTE') header = true;
        if (!header) continue;
        if (['AS', ';', 'BEGIN'].includes(token.upper) && level === 0) { header = false; continue; }
        if (token.value === '(') { level++; if (level === 1) { entries = []; first = true; name = -1; } continue; }
        if (token.value === ')') { if (level === 1) alignTypes(entries); level--; continue; }
        if (level !== 1) continue;
        if (token.value === ',') { first = true; name = -1; continue; }
        if (first) { name = k; first = false; }
        else if (name >= 0) { entries.push([name, k]); name = -1; }
      }
    }
    if (val('DeclareSection', 'AlignTypeDefinitions')) {
      let entries = [];
      for (let k = 0; k < tokens.length; k++) {
        if (tokens[k].upper === 'DECLARE' && tokens[k + 1]?.upper === 'VARIABLE') entries.push([k + 2, k + 3]);
        if (['BEGIN', 'PROCEDURE', 'FUNCTION'].includes(tokens[k].upper) || k === tokens.length - 1) { alignTypes(entries); entries = []; }
      }
    }
    function flush(nextIndent = indent) {
      if (line.trim()) lines.push(' '.repeat(Math.max(0, lineIndent)) + line.trimEnd());
      line = ''; lineIndent = Math.max(0, nextIndent);
    }
    function write(text, space = true) {
      if (!line) { if (pendingIndent !== null) { lineIndent = pendingIndent; pendingIndent = null; } }
      const sep = line && space ? ' ' : '';
      line += sep + text;
    }
    const wordCase = token => {
      if (token.kind !== 'word') return token.value;
      let mode = val('General', keywords.has(token.value.toLowerCase()) ? 'KeywordCharCase' : 'IdentCharCase', 1);
      if (options.caseMode !== undefined) mode = options.caseMode;
      return mode === 1 ? token.value.toLowerCase() : mode === 2 ? token.value.toUpperCase() : mode === 3 ? token.value[0].toUpperCase() + token.value.slice(1).toLowerCase() : token.value;
    };
    const rootSpacingScope = {};
    const spacingScope = () => blocks.findLast(block => block.kind === 'begin') || rootSpacingScope;
    function finishStatement() {
      const scope = spacingScope();
      if (scope.currentKind) { scope.lastKind = scope.currentKind; scope.currentKind = null; }
    }
    function startStatement(index) {
      const scope = spacingScope();
      if (scope.currentKind) return;
      let first = index;
      while (tokens[first]?.kind === 'comment') first++;
      const token = tokens[first];
      if (!token || ['BEGIN', 'END', 'ELSE', ';'].includes(token.upper) || token.kind === 'terminator') return;
      const kind = token.upper === 'IF' ? 'if' : ['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'MERGE', 'EXECUTE'].includes(token.upper) ? 'query' :
        token.value === ':' || ['=', ':='].includes(tokens[first + 1]?.value) ? 'assignment' : 'other';
      const separate = scope.lastKind && (
        kind === 'if' && ['if', 'query'].includes(scope.lastKind) ||
        kind === 'assignment' && ['if', 'query'].includes(scope.lastKind) ||
        kind === 'query' && ['assignment', 'if', 'query'].includes(scope.lastKind));
      if (blanks && separate) {
        flush();
        let existing = 0;
        for (let k = lines.length - 1; k >= 0 && lines[k] === ''; k--) existing++;
        for (; existing < blanks; existing++) lines.push('');
      }
      scope.currentKind = kind;
    }
    function previousCode(index) {
      do { index--; } while (index >= 0 && tokens[index].kind === 'comment');
      return index;
    }
    function distinctFrom(index) {
      let previous = previousCode(index);
      if (tokens[previous]?.upper !== 'DISTINCT') return false;
      previous = previousCode(previous);
      if (tokens[previous]?.upper === 'NOT') previous = previousCode(previous);
      return tokens[previous]?.upper === 'IS';
    }
    let pendingBlank = false;
    for (let i = 0; i < tokens.length; i++) {
      const t = tokens[i], u = t.upper, v = t.value, next = tokens[i + 1], n = next?.upper, p = prev?.upper;
      if (val('General', 'KeepEmptyLines', 1) && prev && /\n[\t \r]*\n/.test(sql.slice(prev.end, t.start))) {
        flush(); if (lines.length && lines[lines.length - 1] !== '') lines.push('');
      }
      if (pendingBlank) {
        // A comment on the terminator's line belongs to the completed statement.
        if (t.kind === 'comment' && prev && !/[\r\n]/.test(sql.slice(prev.end, t.start))) {
          lines[lines.length - 1] += ' ' + v;
          prev = t; continue;
        }
        pendingBlank = false;
      }
      startStatement(i);
      if (t.kind === 'directive') { flush(0); lines.push(t.value); prev = t; continue; }
      if (t.kind === 'comment') {
        const savedPendingIndent = pendingIndent;
        if (v.startsWith('--')) {
          if (val('General', 'StartSingleLineCommentsFromFirstColumn')) { flush(0); lineIndent = 0; }
          const parameterIndent = frames.at(-1)?.params ? indent + val('InputOutput', 'Indent', 4) : clauseIndent || indent;
          write(v); flush(parameterIndent);
        } else { write(v); if (/[\r\n]/.test(v)) flush(); }
        pendingIndent = savedPendingIndent;
        prev = t; continue;
      }
      if (t.kind === 'word' && (u === 'PROCEDURE' || u === 'FUNCTION')) {
        if (p !== 'EXECUTE' && p !== 'GRANT' && p !== 'ALTER' && p !== 'CREATE' && p !== 'RECREATE') flush();
        routine = p !== 'EXECUTE'; signature = routine; statement = '';
      }
      if (t.kind === 'word' && u === 'BEGIN') {
        const section = forLoop ? 'For' : condition ? 'IfThenElse' : 'While';
        const noBreak = p === 'THEN' && val(section, 'NoCRLFBetweenTHEN_BEGIN') || p === 'ELSE' && val(section, 'NoCRLFBetweenELSE_BEGIN');
        if (!noBreak) flush(indent);
        pendingIndent = null; blocks.push({ kind: 'begin', indent }); write(wordCase(t)); flush(indent + step);
        indent += step; clauseIndent = indent; signature = false; statement = ''; clause = ''; condition = false; forLoop = false; prev = t; continue;
      }
      if (t.kind === 'word' && u === 'CASE') {
        write(wordCase(t)); blocks.push({ kind: 'case', indent: lineIndent }); prev = t; continue;
      }
      if (t.kind === 'word' && u === 'END') {
        const block = blocks.pop();
        if (block?.kind === 'case') {
          if (val('Case', 'CRLFBeforeEND', 1)) flush(block.indent);
          write(wordCase(t));
        } else {
          indent = block?.indent ?? Math.max(0, indent - step); pendingIndent = null; flush(indent); write(wordCase(t));
          statement = ''; clause = ''; clauseIndent = indent; routine = false;
          if (n && n !== ';' && n !== '^' && n !== 'ELSE' && n !== ')' && next.kind !== 'terminator') flush(indent);
          if (n !== 'ELSE') finishStatement();
        }
        prev = t; continue;
      }
      if (u === '(') {
        const query = n === 'SELECT' || n === 'WITH';
        const params = signature && frames.length === 0;
        const listKind = p === 'VALUES' ? 'values' : p === 'MATCHING' ? 'matching' : statement === 'Insert' && clause === 'INTO' && frames.length === 0 ? 'insert' : '';
        const frame = { clause, statement, clauseIndent, statementBase, indent, query, params, depth, listKind, type: /^(NUMERIC|DECIMAL|VARCHAR|CHAR|FLOAT|DOUBLE)$/.test(p || '') };
        frames.push(frame); depth++;
        const functionCall = prev?.kind === 'word' && !['IF', 'WHILE', 'FOR', 'SELECT', 'IN', 'VALUES', 'MATCHING', 'RETURNS', 'AS', 'ON', 'AND', 'OR', 'NOT'].includes(p);
        write('(', /\($/.test(line) ? false : params || listKind === 'insert' ? true : functionCall ? !val('General', 'NoSpaceBetweenProcNameAndBracket', 1) : true);
        if (listKind === 'insert' && val('Insert', 'CRLFBeforeListOfInsertedColumns', 1)) flush(statementBase + val('Insert', 'ColumnValueIndent', 4));
        if (params) { flush(indent + val('InputOutput', 'Indent', 4)); }
        else if (query) {
          queryStack.push(frame); indent = lineIndent + step; clauseIndent = indent; statementBase = indent; statement = ''; clause = '';
        }
        if (val('General', 'SpaceAfterOpeningBracket') && !params && n !== ')') line += ' ';
        prev = t; continue;
      }
      if (u === ')') {
        const frame = frames.pop(); if (!frame) throw new Error('Лишняя закрывающая скобка.');
        depth--;
        if (frame.params && !line.trim()) lineIndent = frame.indent;
        if (frame.query) { queryStack.pop(); indent = frame.indent; clause = frame.clause; statement = frame.statement; clauseIndent = frame.clauseIndent; statementBase = frame.statementBase; }
        line = line.trimEnd(); write(')', !!val('General', 'SpaceBeforeClosingBracket') && p !== '(');
        prev = t; continue;
      }
      if (u === ',') {
        const frame = frames.at(-1), atListLevel = !frame || frame.query;
        let one = false, before = false, target = clauseIndent;
        if (frame?.params) { one = true; target = indent + val('InputOutput', 'Indent', 4); }
        else if (frame?.listKind) {
          one = !!val('Insert', { insert: 'OneInsertedColumnInLine', values: 'OneValuesExpressionInLine', matching: 'OneMatchingColumnInLine' }[frame.listKind]);
          target = statementBase + (frame.listKind === 'matching' ? val('Insert', 'IndentBeforeMATCHINGColumns', val('Insert', 'ColumnValueIndent', 4)) : val('Insert', 'ColumnValueIndent', 4));
        }
        else if (atListLevel) {
          if (clause === 'SELECT') { one = !!val('Select', 'OneColumnInLine', 1); before = !!val('Select', 'CRLFBeforeCommaInColumnList', 1); target = statementBase + val('Select', 'IndentBeforeColumn', 7) - 2; }
          else if (clause === 'ORDER_BY' || clause === 'GROUP_BY') { one = !!val('Select', 'OneOrderByItemInLine', 1); before = !!val('Select', 'CRLFBeforeCommaInOrderByList', 1); target = statementBase + val('Select', `IndentBefore${clause}Items`, 9) - 2; }
          else if (clause === 'SET') { one = !!val('Update', 'OneColumnValueExpressionInLine', 1); before = !!val('Update', 'CRLFBeforeCommaInColumnList', 1); target = statementBase + val('Update', 'IndentBeforeFieldValue', 4); }
          else if (clause === 'INTO') { one = !!val(statement || 'Select', statement === 'Select' ? 'OneVariableInLine' : 'OneIntoVariableInLine', 0); before = statement === 'Select' && !!val('Select', 'CRLFBeforeCommaInVarsList'); }
          else if (clause === 'RETURNING') { one = !!val(statement, 'OneReturningColumnInLine'); target = statementBase + val(statement, 'IndentBeforeRETURNINGColumns', 4); }
        }
        if (one && before) { flush(target); write(','); line += val('General', 'SpaceAfterComma', 1) ? ' ' : ''; }
        else { write(',', false); if (one) flush(target); else if (val('General', 'SpaceAfterComma', 1) && !(frame?.type && val('General', 'NoSpaceAfterCommaInDatatypes', 1))) line += ' '; }
        prev = t; continue;
      }
      if (u === ';' || u === '^' || t.kind === 'terminator') {
        write(v, false); flush(indent); pendingIndent = null; statement = ''; clause = ''; clauseIndent = indent; signature = false; condition = false; forLoop = false; declaration = false;
        pendingBlank = true;
        finishStatement();
        prev = t; continue;
      }
      if (t.kind === 'word') {
        const inExpression = frames.length && !frames.at(-1).query;
        if (u === 'RETURNS' && routine) { if (val('InputOutput', 'CRLFBeforeRETURNS', 1)) flush(indent); signature = true; }
        if (u === 'AS' && routine && frames.length === 0) { flush(indent); signature = false; }
        if (u === 'DECLARE') { flush(indent + val('DeclareSection', 'IndentBeforeDECLARE')); signature = false; declaration = true; declarationIndent = indent; }
        if (u === 'FOR' || u === 'WHILE' || u === 'IF') {
          if (!inExpression && !declaration) { flush(indent); condition = u === 'IF'; forLoop = u === 'FOR'; statement = ''; clause = ''; }
        }
        const caseBlock = blocks.at(-1)?.kind === 'case' ? blocks.at(-1) : null;
        if (caseBlock && ['WHEN', 'ELSE', 'THEN'].includes(u)) {
          if (val('Case', `CRLFBefore${u}`, u === 'THEN' ? 0 : 1)) flush(caseBlock.indent + val('Case', `IndentBefore${u}`, step));
          write(wordCase(t)); if (val('Case', `CRLFAfter${u}`)) flush(caseBlock.indent + val('Case', 'IndentBeforeTHENExpression', step * 2));
          prev = t; continue;
        }
        if (!inExpression && ['THEN', 'ELSE', 'DO'].includes(u) && statement !== 'Merge') {
          if (u === 'ELSE') flush(indent + val('IfThenElse', 'IndentBeforeELSE'));
          if (u === 'DO' && val(forLoop ? 'For' : 'While', 'CRLFBeforeDO', forLoop ? 1 : 0)) flush(indent);
          write(wordCase(t));
          if (u !== 'DO' && val('IfThenElse', `CRLFAfter${u}`, 1) && !(n === 'BEGIN' && val('IfThenElse', `NoCRLFBetween${u}_BEGIN`)) && !(n === 'IF' && u === 'ELSE' && val('IfThenElse', 'NoCRLFBetweenELSE_IF'))) flush(indent + step);
          else if (u === 'DO') flush(indent + step);
          if (n !== 'BEGIN') pendingIndent = indent + step;
          prev = t; continue;
        }
        if (!inExpression) {
          if (u === 'UPDATE' && n === 'OR' && tokens[i + 2]?.upper === 'INSERT') {
            statement = 'Insert'; clause = ''; statementBase = pendingIndent ?? indent;
            write([t, next, tokens[i + 2]].map(wordCase).join(' '));
            i += 2; prev = tokens[i]; continue;
          }
          if (u === 'SELECT') {
            const previousStatement = statement;
            statement = 'Select'; clause = 'SELECT';
            statementBase = pendingIndent ?? (forLoop ? indent + val('For', 'SelectExecuteIndent', 4) : indent);
            if (declaration) {
              statementBase = declarationIndent + val('DeclareSection', 'IndentBeforeSELECT', 4);
              if (val('DeclareSection', 'CRLFBeforeSELECT', 1)) flush(statementBase);
            } else if (previousStatement === 'Insert') {
              statementBase = indent + val('Insert', 'IndentBeforeSELECT');
              if (val('Insert', 'CRLFBeforeSELECT', 1)) flush(statementBase);
            } else if (forLoop) {
              if (val('For', 'CRLFBeforeSELECT', 1)) flush(statementBase);
            } else if (line.trim() && p !== '(') flush(statementBase);
          }
          else if (['INSERT', 'UPDATE', 'DELETE', 'MERGE'].includes(u) && !(u === 'UPDATE' && p === 'OR')) {
            statement = u[0] + u.slice(1).toLowerCase(); clause = ''; statementBase = pendingIndent ?? indent;
            if (u === 'UPDATE' && val('Update', 'CRLFAfterUPDATE', 1)) {
              write(wordCase(t)); pendingIndent = null; flush(statementBase + val('Update', 'IndentBeforeTableName', 4)); prev = t; continue;
            }
          }
          let c = u, consumed = false;
          if ((u === 'ORDER' || u === 'GROUP') && n === 'BY') { c = u + '_BY'; consumed = true; }
          let joinPrefix = false;
          if (['LEFT', 'RIGHT', 'INNER', 'OUTER', 'FULL', 'CROSS', 'NATURAL'].includes(u) && (n === 'JOIN' || n === 'OUTER')) { c = 'JOIN'; joinPrefix = true; }
          const isClause = clauses.includes(c) && c !== 'SELECT' && !(c === 'FROM' && distinctFrom(i)) && !(c === 'SET' && !statement) && !(u === 'JOIN' && /^(LEFT|RIGHT|INNER|OUTER|FULL|CROSS|NATURAL)$/.test(p || ''));
          if (isClause && statement) {
            const sec = statement;
            const initialInsertInto = sec === 'Insert' && c === 'INTO' && !clause;
            clause = c;
            if (!initialInsertInto && val(sec, `CRLFBefore${c}`, 1)) flush(statementBase + val(sec, `IndentBefore${c}`));
            write(wordCase(t));
            if (consumed) { write(wordCase(next)); i++; }
            clauseIndent = statementBase + (c === 'RETURNING' ? val(sec, 'IndentBeforeRETURNINGColumns', 4) : c === 'MATCHING' ? val(sec, 'IndentBeforeMATCHINGColumns', 4) : val(sec, `IndentBefore${c}Items`, c === 'WHERE' || c === 'JOIN' ? 4 : 0));
            if (val(sec, `CRLFAfter${c}`) && !joinPrefix && !initialInsertInto) flush(clauseIndent);
            prev = consumed ? next : t; continue;
          }
          if ((u === 'AND' || u === 'OR') && !frames.some(f => f.params)) {
            const sec = clause === 'JOIN' || clause === 'ON' ? 'JoinOn' : condition ? 'IfThenElse' : 'Where';
            // BETWEEN ... AND is an expression separator, not a boolean conjunction.
            let between = false;
            if (u === 'AND') for (let k = i - 1; k >= 0; k--) { const w = tokens[k].upper; if (w === 'BETWEEN') { between = true; break; } if (['AND', 'OR', 'WHERE', ';'].includes(w)) break; }
            if (!between) {
              if (val(sec, `CRLFBefore${u}`)) flush(clauseIndent + val(sec, `IndentBefore${u}`));
              write(wordCase(t));
              if (val(sec, `CRLFAfter${u}`, u === 'AND' ? 1 : 0)) flush(clauseIndent + val(sec, `IndentBefore${u}`));
              prev = t; continue;
            }
          }
          if (u === 'ON' && clause === 'JOIN') { clause = 'ON'; if (val('JoinOn', 'CRLFBeforeON')) flush(clauseIndent); write(wordCase(t)); if (val('JoinOn', 'CRLFAfterON')) flush(clauseIndent); prev = t; continue; }
          if (u === 'SELECT' && val('Select', 'CRLFBeforeColumnsList')) { write(wordCase(t)); flush(indent + val('Select', 'IndentBeforeColumn', 7)); prev = t; continue; }
        }
      }
      // IF expressions are parenthesized; apply only at their outer boolean level,
      // keeping operators inside functions, subqueries and BETWEEN untouched.
      if (t.kind === 'word' && condition && !queryStack.length && frames.length <= 1 && ['AND', 'OR'].includes(u)) {
        let between = false;
        if (u === 'AND') for (let k = i - 1; k >= 0; k--) {
          const w = tokens[k].upper;
          if (w === 'BETWEEN') { between = true; break; }
          if (['AND', 'OR', 'IF', ';'].includes(w)) break;
        }
        if (!between) {
          if (val('IfThenElse', `CRLFBefore${u}`)) flush(indent + step);
          write(wordCase(t));
          if (val('IfThenElse', `CRLFAfter${u}`)) flush(indent + step);
          prev = t; continue;
        }
      }
      let space = !!line && !/[ (:.]$/.test(line) && v !== '.' && v !== ':' && p !== ':';
      if (p === ',') space = false;
      if (u === '.' || p === '.') space = false;
      if (u === ':' && prev && !['(', ','].includes(p)) space = !/[\s(]$/.test(line);
      const value = wordCase(t);
      if (lineIndent + line.length + value.length > margin && space && (p === ',' || ['AND', 'OR', '||'].includes(p))) flush(clauseIndent || indent + step);
      write(value, space); if (typePadding.has(i) && space) line = line.slice(0, -value.length) + ' '.repeat(typePadding.get(i)) + value;
      prev = t;
    }
    if (frames.length) throw new Error('Не закрыта круглая скобка.');
    flush();
    const result = lines.join('\n').trimEnd() + (tokens.length ? '\n' : '');
    const signatureOf = ts => ts.map(t => t.kind === 'word' ? t.value.toUpperCase() : t.value).join('\0');
    if (signatureOf(tokens) !== signatureOf(lex(result))) throw new Error('Проверка сохранности SQL не пройдена. Результат не выдан.');
    return { sql: result, unsupported: unsupported(settings), tokens: tokens.length, lines: lines.length };
  }
  const api = { parseIni, lex, format, unsupported };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  root.SqlFormatter = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
