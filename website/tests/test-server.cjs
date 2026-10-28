'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
for (const match of html.matchAll(/<script src="([^"]+)"/g)) {
  const asset = path.resolve(root, match[1]);
  assert.ok(asset.startsWith(root + path.sep), 'Script must stay inside website');
  assert.ok(fs.existsSync(asset), 'Missing script: ' + match[1]);
}
const server = spawn(process.execPath, [path.join(root, 'server.cjs')], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
const timeout = setTimeout(() => { server.kill(); console.error('Server startup/check timeout'); process.exitCode = 1; }, 10000);
server.on('error', error => { clearTimeout(timeout); console.error(error); process.exitCode = 1; });
server.stderr.on('data', data => { clearTimeout(timeout); console.error(String(data)); process.exitCode = 1; server.kill(); });
server.stdout.once('data', async () => {
  try {
    for (const route of ['/', '/app.js', '/preset.js', '/formatter.js', '/presets/preset_ibeexpert.ini']) {
      const response = await fetch('http://127.0.0.1:8765' + route);
      assert.equal(response.status, 200, route);
      assert.ok((await response.text()).length > 0, route);
    }
    assert.equal((await fetch('http://127.0.0.1:8765/windows-app/SQLAtelier.exe')).status, 404);
    console.log('PASS: standalone HTML assets, local server routes, external paths blocked');
  } catch (error) { console.error(error); process.exitCode = 1; }
  finally { clearTimeout(timeout); server.kill(); }
});
