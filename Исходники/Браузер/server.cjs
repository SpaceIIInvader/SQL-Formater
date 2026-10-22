const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript; charset=utf-8', '.ini': 'text/plain; charset=utf-8' };
const routes = {
  'index.html': path.join(__dirname, 'index.html'),
  'app.js': path.join(__dirname, 'app.js'),
  'preset.js': path.join(__dirname, 'preset.js'),
  'Ядро/formatter.js': path.join(__dirname, '../Ядро/formatter.js'),
  'preset_ibeexpert.ini': path.join(__dirname, '../../Пресеты/preset_ibeexpert.ini')
};
const server = http.createServer((req, res) => {
  let pathname;
  try { pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname); }
  catch { res.writeHead(400); res.end('Bad URL'); return; }
  const name = pathname === '/' ? 'index.html' : pathname.slice(1);
  if (!Object.hasOwn(routes, name)) { res.writeHead(404); res.end('Not found'); return; }
  res.setHeader('Content-Type', types[path.extname(name)] || 'text/plain');
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'none'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'");
  fs.createReadStream(routes[name]).on('error', () => { res.statusCode = 500; res.end('Read error'); }).pipe(res);
});
server.listen(8765, '127.0.0.1', () => process.stdout.write('SQL Atelier: http://127.0.0.1:8765\n'));
server.on('error', err => { process.stderr.write(err.message + '\n'); process.exitCode = 1; });
