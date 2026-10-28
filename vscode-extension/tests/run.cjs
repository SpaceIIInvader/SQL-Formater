'use strict';
const { spawnSync } = require('node:child_process');
const path = require('node:path');
for (const args of [['core.test.cjs'], ['--test', 'service.test.cjs', 'session.test.cjs']]) {
  const result = spawnSync(process.execPath, args, { cwd: __dirname, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
console.log('All SQL Atelier extension checks passed.');
