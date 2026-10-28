'use strict';
function decodePreset(bytes) {
  if (bytes.byteLength > 240000) throw new Error('INI-пресет больше 240 КБ.');
  let text;
  if (bytes[0] === 255 && bytes[1] === 254) text = new TextDecoder('utf-16le', { fatal: true }).decode(bytes);
  else if (bytes[0] === 254 && bytes[1] === 255) text = new TextDecoder('utf-16be', { fatal: true }).decode(bytes);
  else {
    try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch { text = new TextDecoder('windows-1251', { fatal: true }).decode(bytes); }
  }
  text = text.replace(/^\uFEFF/, '');
  if (text.length > 60000) throw new Error('INI-пресет больше 60 000 символов.');
  return text;
}
module.exports = { decodePreset };
