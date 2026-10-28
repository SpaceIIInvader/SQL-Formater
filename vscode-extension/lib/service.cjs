'use strict';
const path = require('node:path');
const { Worker } = require('node:worker_threads');
function cancelled() { return Object.assign(new Error('Форматирование отменено.'), { code: 'CANCELLED' }); }
function formatSql(request, token, timeoutMs = 15000) {
  if (typeof request.sql !== 'string' || request.sql.length > 2000000) return Promise.reject(new Error('Максимальный размер SQL — 2 млн символов.'));
  if (token?.isCancellationRequested) return Promise.reject(cancelled());
  return new Promise((resolve, reject) => {
    const worker = new Worker(path.join(__dirname, 'format-worker.cjs'), { workerData: request });
    let done = false, listener;
    const timer = setTimeout(() => finish(new Error('Форматирование превысило 15 секунд. Разделите скрипт на части.')), timeoutMs);
    function finish(error, result) {
      if (done) return;
      done = true; clearTimeout(timer); listener?.dispose();
      worker.terminate().catch(() => {});
      if (error) reject(error); else resolve(result);
    }
    worker.once('message', message => message.ok ? finish(null, message.result) : finish(new Error(message.error)));
    worker.once('error', error => finish(error));
    worker.once('exit', code => { if (!done) finish(new Error('Движок завершился без результата (код ' + code + ').')); });
    listener = token?.onCancellationRequested?.(() => finish(cancelled()));
    if (done) listener?.dispose();
    if (token?.isCancellationRequested) finish(cancelled());
  });
}
module.exports = { formatSql };
