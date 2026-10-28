# SQL Atelier — приложение Windows x64

Запустите `SQLAtelier.exe`. Установка и отдельный Node.js не нужны.
Эта папка самостоятельная: её можно скопировать отдельно от сайта.

Доступны четыре вкладки:

- «SQL-код» — вставка и форматирование SQL.
- «Обработка папки» — предварительный просмотр и пакетное сохранение с резервными копиями.
- «Сравнение SQL» — построчные различия и сворачивание совпадающих участков.
- «Настройки пресета» — редактирование, применение и экспорт INI.

Настройки сохраняются рядом с EXE в `SQLAtelier.settings.json`.
Полная инструкция — `docs/windows-guide.txt`.

## Файлы

- `SQLAtelier.exe` — готовое приложение.
- `src/Desktop.cs` — интерфейс Windows.
- `src/formatter.js` — движок форматирования.
- `src/desktop-worker.cjs` — форматирование текста и обработка файлов.
- `presets/` — встроенный INI-пресет.
- `build/` — скрипт и промежуточные файлы сборки.
- `tests/`, `docs/`, `licenses/` — проверки, инструкция и лицензии Node.js.

## Проверки и сборка

Из этой папки, при установленном Node.js:

```powershell
node tests/test.cjs
node tests/test-desktop.cjs
node tests/test-text.cjs
```

Для сборки нужны Windows x64, Node.js и компилятор .NET Framework:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/build-desktop.ps1
```

Скрипт обновляет `SQLAtelier.exe`, используя только файлы этой папки.
При смене встроенной версии Node.js проверьте `licenses/node-license.txt`.
