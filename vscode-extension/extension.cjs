'use strict';
const { createSession } = require('./lib/session.cjs');
exports.activate = context => createSession(require('vscode'), context);
exports.deactivate = () => {};
