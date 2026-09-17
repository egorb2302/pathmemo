# pathmemo

**Disk space screener for Windows.** Один `.exe`, без установки. Находит, куда ушло место, и безопасно освобождает его.

**Версия спецификации:** 3.0
**Целевая платформа:** Windows 10 1809+ / Windows 11 (x64, arm64 — отдельные бинарники)
**Язык интерфейса приложения:** английский (CLI, TUI, логи, экспорт). Этот документ — внутренняя спецификация на русском.
**Статус реализации:**

| Фаза | Содержание | Состояние |
|---|---|---|
| P0 | Каркас, манифест, single-file publish, `doctor` | **готово** — 16.1 МБ exe с trimming |
| P1 | Walk-сканер, формат `.pmsnap`, `scan` / `tree` / `top` / `history` | **готово** — 1.22 млн файлов за 41 с, 50 тестов зелёные |
| P1+ | Интерактивный запуск двойным кликом: статус томов, меню, построчная навигация по дереву | **готово** — временная замена экранам Overview и Tree до P5 |
| P2 | Space Audit: 21 проба, `audit` / `--id` / `--copy` / `--json`, пункт `[A]` в интерактивном меню, перезапуск с правами администратора `[E]` | **готово** — 4.1 с без прав администратора (16 проб отвечают без них), 76 тестов зелёные |
| P3 | MFT-сканер (разбор `$MFT` напрямую), hardlink-дедупликация в обоих сканерах, ADS, сверка с томом, `--scanner`, предложение перезапуска с правами | **готово** — 1.27 млн файлов за 9.6 с, unaccounted 1.4 %, 92 теста зелёные |
| P4 | SQLite (схема §11, `user_version`, WAL), история, `diff`, `--format json\|csv`, `--data-dir` | **готово** — импорт 4 снимков за 1.6 с, diff двух снимков по 1.6 млн узлов за 105 мс, 126 тестов зелёные |
| P5 | TUI: Overview + Tree, свой рендерер, `status` | **готово** — кадр 0.1–0.6 мс, вход в каталог с 26 тыс. детей 10 мс, поиск по 1.58 млн узлов 56 мс, 155 тестов зелёные |
| P6 | Удаление: PathGuard, HandleTreeDeleter, карантин | не начато |
| P7 | Reclaim-правила | не начато |
| P8 | Дубликаты | не начато |
| P9 | USN-инкремент, расписание | не начато |
| P10 | Полировка, NativeAOT | не начато |

**Известные ограничения текущей сборки:** без прав администратора работает walk-сканер: часть путей недоступна, hardlink-дедупликация только для файлов ≥ 1 МБ (WinSxS завышен примерно на 1.5 ГБ), ADS не учитываются. С правами администратора всё три ограничения снимаются MFT-сканером. Инкрементального USN-скана пока нет (P9). `diff` требует двух полных снимков и предупреждает, когда они сняты разными сканерами: часть разницы тогда — не изменения на диске, а разница в том, что сканеры видят. TUI показывает и размечает, но ничего не удаляет: `d` и `K` называют фазу, в которой появятся (P6, P7); открытый снимок держит около 200 МБ памяти при бюджете 150 — лечится ленивой загрузкой секций в P10 (§20).

---

## Содержание

1. [Назначение и принципы](#1-назначение-и-принципы)
2. [Не-цели](#2-не-цели)
3. [Модель дискового места](#3-модель-дискового-места)
4. [Сканирование](#4-сканирование)
5. [Формат снимка](#5-формат-снимка)
6. [Space Audit — невидимое место](#6-space-audit--невидимое-место)
7. [Reclaim — рекомендации по очистке](#7-reclaim--рекомендации-по-очистке)
8. [Дубликаты](#8-дубликаты)
9. [Удаление](#9-удаление)
10. [История и diff](#10-история-и-diff)
11. [Схема базы данных](#11-схема-базы-данных)
12. [Конфигурация](#12-конфигурация)
13. [CLI](#13-cli)
14. [TUI](#14-tui)
15. [Интеграция с ОС](#15-интеграция-с-ос)
16. [Модель угроз и защитные меры](#16-модель-угроз-и-защитные-меры)
17. [Архитектура кода](#17-архитектура-кода)
18. [Технологический стек](#18-технологический-стек)
19. [Формат поставки и сборка](#19-формат-поставки-и-сборка)
20. [Бюджеты производительности](#20-бюджеты-производительности)
21. [Критерии приёмки](#21-критерии-приёмки)
22. [Тестирование](#22-тестирование)
23. [Глоссарий](#23-глоссарий)

---

## 1. Назначение и принципы

pathmemo отвечает на два вопроса:

1. **Куда ушло место?** — включая то, что не видно обходом файловой системы.
2. **Что и как безопасно освободить?** — с честной оценкой, сколько байт вернётся.

### 1.1. Принципы

| № | Принцип | Следствие |
|---|---|---|
| P1 | **Честные цифры.** Сумма скана должна сходиться с занятым местом тома, а расхождение — объясняться. | Учитываем allocated size, hardlinks, sparse, кластеры. Показываем «unaccounted» дельту с причинами. |
| P2 | **Сканируем всё, защищаем выборочно.** | Исключение из учёта ≠ защита от удаления. Это два независимых списка. Список исключений по умолчанию почти пуст. |
| P3 | **Освобождение места измеряется фактически.** | До/после каждой операции читаем свободное место тома и показываем реальную дельту. |
| P4 | **Правильный способ очистки важнее удаления файлов.** | Для кэшей и системных хранилищ выдаём команду штатного механизма (`DISM`, `git gc`, `docker prune`), а не путь на `rm`. |
| P5 | **Ничего не удаляется без явного действия пользователя.** | Нет автоочистки, нет «оптимизации в один клик». |
| P6 | **Удаление проверяемо и откатываемо там, где это возможно.** | Карантин + журнал + dry-run. Корзина — только когда она уместна. |
| P7 | **Не навредить диску, который чистим.** | Свои данные ограничены по размеру и не растут бесконечно. Инструмент никогда не занимает больше 500 МБ. |
| P8 | **Скрипт-дружелюбность на равных с интерактивом.** | Каждое действие доступно из CLI, со стабильным JSON и осмысленными exit-кодами. |
| P9 | **Работает в любом терминале.** | Никаких биндингов, которые перехватывает терминал. Минимум 80×24. |
| P10 | **Недоверенные данные — это данные.** Имена файлов, содержимое конфига, пути — вход, а не команды. | Санитизация при отрисовке, никакого исполнения из конфига, отказ от исполнения найденных файлов по умолчанию. |

### 1.2. Чем отличается от WizTree / TreeSize / ncdu

- **История и diff.** «Что выросло с прошлой недели на 18 ГБ» — автоматически, по расписанию.
- **Space Audit.** VSS, WinSxS, WSL/Docker vhdx, Windows Update cache, hiberfil, корзина — то, что обход ФС физически не видит.
- **Знание экосистем разработки.** npm / pnpm / nuget / gradle / maven / pip / cargo / go / Unity / Unreal / Docker / WSL — с правильной командой очистки для каждого.
- **Один exe, который одинаково хорош в скрипте и в интерактиве.**

---

## 2. Не-цели

Явно **вне** области:

- GUI (WPF/WinUI), веб-приложение как основной интерфейс. *(Локальный treemap-просмотрщик на `localhost` — опциональная фича Phase 3, не замена TUI.)*
- Автоматическое удаление без подтверждения, «ускорение системы», чистка реестра, любые «оптимизаторы».
- Управление настройками безопасности (Defender exclusions, UAC, политики). Мы **предлагаем** команду, пользователь выполняет сам.
- Сервер, многопользовательский режим, облачная синхронизация истории.
- Perceptual hash для медиа, поиск похожих (не идентичных) файлов.
- Сетевые и съёмные диски как основной сценарий. Поддерживаются в degraded-режиме, не оптимизируются.
- Linux / macOS. Абстракции для этого **не** закладываются заранее (см. §17.1).

---

## 2.1. Режимы запуска

| Условие | Поведение |
|---|---|
| Двойной клик из Проводника (консоль создана под нас) | TUI, окно держится открытым |
| `--interactive` / `-i` | TUI принудительно, из любого терминала |
| Без аргументов из терминала | справка |
| Есть аргументы | соответствующая команда, не-интерактивно |
| stdout перенаправлен, аргументов нет | `status` — сводка текстом |
| stdout перенаправлен | всегда не-интерактивно, вывод UTF-8 |
| Терминал не умеет ANSI | построчное меню вместо TUI (см. ниже) |

Запуск из Проводника отличается от запуска из оболочки через `GetConsoleProcessList`: ровно один процесс, подключённый к консоли, означает, что окно создано для нас и исчезнет вместе с нами. Без этой проверки двойной клик по консольному приложению даёт вспышку окна на долю секунды.

*С P5* интерактивная сессия — это настоящие экраны (§14): `TuiHost` включает VT-обработку, уходит в альтернативный буфер и рисует кадры. Если включить VT не удалось или поток перенаправлен, запускается прежняя построчная сессия — не как формальность: в хосте без VT кадр вывелся бы escape-последовательностями текстом, что хуже, чем отсутствие TUI.

Построчное меню (fallback): `[A]` audit, `[B]` browse, `[S]` scan C:, `[V]` scan all volumes, `[L]` largest files, `[C]` compare the last two scans (показывается, когда снимков хотя бы два), `[H]` scan history, `[D]` diagnostics, `[E]` restart as administrator (показывается только без прав), `[Q]` quit. Пункт `[E]` перезапускает тот же exe через `ShellExecute` с глаголом `runas` и аргументом `--interactive`: манифест остаётся `asInvoker`, права запрашиваются только когда пользователь их попросил — ради проб VSS и DISM. Повышение прав из TUI отдельной клавишей не вынесено: его предлагает сам скан, а скан из TUI идёт в обычной консоли (§14.7).

---

## 3. Модель дискового места

Фундаментальный раздел: без него цифры не сойдутся, и весь инструмент бесполезен.

### 3.1. Четыре разных «размера»

| Термин | Что это | Откуда берём |
|---|---|---|
| **Logical size** | Размер потока данных (то, что показывает `FileInfo.Length`). | MFT `DataSize` / `FILE_STANDARD_INFO.EndOfFile` |
| **Allocated size** | Сколько байт реально занято на томе: округление до кластера, NTFS-сжатие, sparse-дырки. | MFT `AllocatedSize` / `GetCompressedFileSize` |
| **Unique allocated size** | Allocated с учётом hardlink-дедупликации: каждый физический файл посчитан один раз. | Дедупликация по `(VolumeSerial, FileReferenceNumber)` |
| **Reclaimable size** | Сколько освободится при удалении **этого конкретного набора** путей. | `Unique allocated`, но только для файлов, у которых **все** hardlink-ссылки входят в набор |

**Правила отображения:**
- Все размеры в дашборде, дереве и отчётах — **unique allocated** по умолчанию. Это единственная величина, которая складывается в занятое место тома.
- В деталях файла показываем и logical, и allocated, и число hardlink-ссылок.
- В диалоге удаления показываем **reclaimable**, а не сумму размеров. Если они различаются — объясняем почему («12 GB selected, 3.1 GB reclaimable: 8.9 GB is shared via hard links with files outside the selection»).
- Переключение режима отображения — клавиша `m` в TUI, `--size logical|allocated|unique` в CLI.

### 3.2. Hardlinks

`C:\Windows\WinSxS` почти целиком состоит из жёстких ссылок на файлы в `System32`. Без дедупликации `C:\Windows` будет показан в 2–3 раза больше реального.

**Алгоритм:**
1. Каждый файл получает ключ `(VolumeSerialNumber, FileReferenceNumber)` — 128 бит на NTFS.
2. Первое вхождение в порядке обхода (детерминированный DFS) становится **владельцем** байтов, остальные — **ссылками** (`NodeFlags.HardlinkAlias`).
3. `Unique allocated` агрегируется только по владельцам.
4. Число ссылок (`LinkCount` из MFT) хранится в узле — позволяет мгновенно ответить «освободит ли удаление место».
5. В UI ссылки помечены, и их размер показан серым: `1.2 GB (link)`.

**Degraded-режим (fallback-сканер):** получить `FileReferenceNumber` без открытия handle нельзя. Компромисс: открываем handle и читаем `FILE_ID_INFO` только для файлов **больше 1 МБ** (их единицы процентов от общего числа, а hardlink-дубли меньшего размера дают пренебрежимую ошибку). Флаг снимка `SnapshotFlags.PartialHardlinkResolution` сигнализирует UI показать предупреждение.

*Реализовано (P3):* handle открывается с `FILE_READ_ATTRIBUTES` + `FILE_FLAG_OPEN_REPARSE_POINT`, сначала читается `FILE_STANDARD_INFO.NumberOfLinks`, и только при `> 1` — `FILE_ID_INFO`. Идентификаторы не хранятся в `RawEntry` (это добавило бы 16 байт на каждый из 1.5 млн элементов), а лежат в боковом списке каталога. На C: файлов ≥ 1 МБ 16 тыс., цена — около 3 с из 44. Владелец — первое вхождение в **BFS-порядке** сборки снимка: у файла с именами `System32\foo.dll` и `WinSxS\amd64_…\foo.dll` владельцем становится более короткий путь, что и ожидает пользователь. То же правило действует в MFT-сканере.

### 3.3. Sparse и NTFS-compressed

- Sparse: `AllocatedSize` < `LogicalSize`. Типично для `ext4.vhdx` (WSL), баз данных, образов. **Критично**: WSL-диск на 80 ГБ logical может занимать 12 ГБ allocated, а может и все 80.
- NTFS-compressed: то же, но по другой причине. Обрабатывается одинаково — используем `AllocatedSize`.
- В UI: если `allocated / logical < 0.9`, рядом с размером бейдж `sparse` или `compressed`.

### 3.4. Alternate Data Streams

Учитываем суммарно: при MFT-скане все `$DATA`-атрибуты файла суммируются автоматически. В fallback-режиме ADS не учитываются (ошибка < 0.1% на типичной системе, кроме `Zone.Identifier` по 100 байт на файл) — это документированное ограничение.

### 3.5. Сверка с томом (reconciliation)

После каждого скана:

```
Volume C:  total 476.1 GB   free 21.3 GB   used 454.8 GB
  Scanned files                          381.2 GB
  Inaccessible paths (147)                 ~?  GB
  Shadow copies (VSS)                      38.4 GB
  Recycle Bin                               4.1 GB
  NTFS metadata ($MFT, $LogFile, ...)       2.9 GB
  Reserved for system                       1.1 GB
  ─────────────────────────────────────────────────
  Unaccounted                              27.1 GB   [?] explain
```

Кнопка `[?] explain` открывает список возможных причин с командами проверки. Это превращает «цифры не сходятся» из бага в фичу.

Источники:
- `GetDiskFreeSpaceExW` — total / free.
- `FSCTL_GET_NTFS_VOLUME_DATA` — `MftValidDataLength`, размеры метаданных, размер кластера.
- `SHQueryRecycleBinW` — корзина по тому.
- WMI `Win32_ShadowStorage` — теневые копии.

---

## 4. Сканирование

### 4.1. Два пути, один результат

| | **MFT scanner** (основной) | **Walk scanner** (fallback) |
|---|---|---|
| Механизм | чтение `\\.\C:` + `FSCTL_GET_NTFS_VOLUME_DATA` + прямой разбор записей `$MFT` (§4.3.1; `FSCTL_ENUM_USN_DATA` отвергнут — одно имя на файл) | `FileSystemEnumerator<T>` |
| Требует админа | **да** | нет |
| ФС | только NTFS | любая |
| 1 млн файлов | **3–8 с** | 45 с – 5 мин |
| Allocated size | да, бесплатно | `GetCompressedFileSize` (+1 syscall) |
| FileReferenceNumber / LinkCount | да, бесплатно | только для файлов > 1 МБ |
| ADS | да | нет |
| Видит недоступные по ACL пути | **да** | нет |

**MFT-путь — основной.** Он даёт на порядок лучшие цифры, на два порядка большую скорость и видит то, куда обычному обходу нет доступа. Это не оптимизация «на потом», это ядро продукта.

### 4.2. Выбор пути

```
для каждого целевого тома:
    если ФС == NTFS и процесс elevated:
        MFT scanner
    иначе если ФС == NTFS и не elevated:
        предложить релонч с правами администратора;
        при отказе — Walk scanner с флагом Degraded
    иначе:
        Walk scanner
```

Релонч: `ShellExecuteEx` с `lpVerb = "runas"`, тот же командной строкой + `--elevated-relaunch`. Для CLI — только если есть TTY; в скриптовом режиме печатаем предупреждение и работаем в degraded.

**Сообщение пользователю (English, как в приложении):**
```
Running without administrator rights.
  · Fast MFT scan unavailable (falling back to directory walk: ~40x slower)
  · Files in other user profiles and protected folders will be missed
  · Hard link detection limited to files over 1 MB

  [R] Restart as administrator    [C] Continue anyway    [Q] Quit
```

### 4.3. MFT scanner

1. Открыть том: `CreateFileW(@"\\.\C:", GENERIC_READ, FILE_SHARE_READ|WRITE, OPEN_EXISTING, 0)`.
2. `DeviceIoControl(FSCTL_GET_NTFS_VOLUME_DATA)` → размер кластера, размер `$MFT`, число записей.
3. `DeviceIoControl(FSCTL_ENUM_USN_DATA)` в цикле, буфер 1 МБ, версия записи `USN_RECORD_V3` (128-битные FileId) с fallback на V2.
4. Из каждой записи: `FileReferenceNumber`, `ParentFileReferenceNumber`, `FileName`, `FileAttributes`.
5. `ENUM_USN_DATA` **не возвращает размеры**. Размеры берём одним из двух способов:
   - **(предпочтительно)** парсинг `$MFT` напрямую: читаем `$MFT` через `FSCTL_GET_RETRIEVAL_POINTERS` + raw-чтение тома, разбираем атрибуты `$STANDARD_INFORMATION` и `$DATA` (resident/non-resident, `AllocatedSize`, `RealSize`, `TotalAllocated` для sparse). Это то, что делает WizTree. Даёт всё за один последовательный проход.
   - **(упрощённо, для первой итерации)** `GetFileInformationByHandleEx(FileIdInfo)` не нужен, но размер — нужен; открывать 1 млн handle'ов недопустимо. Поэтому: **первая итерация сразу делает парсинг `$MFT`.** Компромисс «USN для дерева + handle для размеров» отвергнут как нежизнеспособный.
6. Построение путей: `parentId → id` образует граф; корень тома имеет известный `FileReferenceNumber` (5). Один проход снизу вверх даёт полные пути без строковой конкатенации.
7. Записи с флагом «удалён» и записи-расширения (`$ATTRIBUTE_LIST`) отбрасываются.

**Обработка ошибок:** любая ошибка парсинга `$MFT` (нестандартный том, шифрование BitLocker в состоянии locked, повреждение) → откат на Walk scanner с записью причины в `scan_errors`.

#### 4.3.1. Как это реализовано (P3)

- **`FSCTL_ENUM_USN_DATA` не используется вовсе.** Он даёт по одному имени на файл, а hardlink-дедупликации нужны все `$FILE_NAME`. Полный разбор `$MFT` даёт имена, родителей, размеры, атрибуты, время и число ссылок за один последовательный проход — `MftParser` (чистая функция над байтами, покрыта синтетическими записями) и `MftScanner`.
- **Где лежит `$MFT`:** запись 0 читается по `MftStartLcn` из `NTFS_VOLUME_DATA_BUFFER`, её `$DATA` data runs дают экстенты; при сильной фрагментации продолжения runs лежат в extension-записях, на которые указывает `$ATTRIBUTE_LIST` записи 0 — они тоже читаются. `FSCTL_GET_RETRIEVAL_POINTERS` не нужен.
- **Чтение:** `\\.\C:` открывается с `GENERIC_READ` (это единственное место, требующее elevated), чтение позиционное через `ReadFile` с `OVERLAPPED.Offset`, кусками по 8 МБ, с двойной буферизацией: следующий кусок читается, пока разбирается текущий. Всё чтение — одно-два ГБ последовательного I/O, ни одного открытого файла.
- **Fixup:** update sequence array применяется к каждой записи; несовпадение — запись пропускается и считается в `scan_errors` («N записей не прошли проверку целостности»). Нулевые (никогда не записанные) слоты — норма.
- **Extension-записи** (у файла с десятками hardlink-ссылок `$FILE_NAME` не помещаются в одну запись, у фрагментированного — `$DATA`) относятся к базовой записи по `BaseRecord`, с проверкой sequence number: устаревшая ссылка на переиспользованный слот отбрасывается.
- **Размеры:** logical — `RealSize` из заголовка первой части `$DATA` (`StartVcn == 0`); **allocated — сумма реальных data runs всех частей, дыры не считаются.** Это единственный способ, который одинаково верен для обычных, sparse, NTFS-compressed потоков и для `$BadClus:$Bad` — потока размером с весь том без флага sparse (в первом прогоне он дал «−220 ГБ unaccounted»). Все `$DATA`-потоки суммируются (ADS учтены), `$INDEX_ALLOCATION` каталога — его собственные кластеры. Resident-данные стоят 0 кластеров: они внутри записи `$MFT`, которую считает проба `ntfs.metadata`.
- **Атрибуты:** в сыром `$STANDARD_INFORMATION` бит `0x40000` означает «есть extended attributes» (все файлы из WIM-образа и сам `C:\Windows`), а Win32 использует то же значение для `RECALL_ON_OPEN`. Без маски половина Windows показывалась как `[cloud]`. Правило: recall-биты значимы только у reparse point, `OFFLINE` — всегда.
- **Дерево:** ссылки индексируются по родителю counting sort'ом, обход BFS от записи 5 (или от записи каталога, если сканируется подкаталог). Первое вхождение записи — владелец, последующие — `HardlinkAlias`; каталог, встреченный дважды (повреждение), второй раз не обходится. Записи, чей родитель удалён или переиспользован, — сироты: считаются и сообщаются, в дерево не подшиваются.
- **Что попадает в дерево:** всё, что есть в `$MFT`, включая метафайлы `$MFT`, `$LogFile`, `$Bitmap`, `$Extend\$UsnJrnl` — это реальные байты тома, и WizTree показывает их так же. Поэтому в MFT-режиме сверка с томом не добавляет метаданные второй раз.
- **Смешанные наборы томов:** NTFS с правами — MFT, остальные (exFAT, FAT32, отказ MFT) — walk одним вызовом с work-stealing; деревья склеиваются `TreeAssembly.Concat` со сдвигом индексов. Причина отката записана в `scan_errors`.
- **Память:** массивы по числу записей (~30 байт на запись) + список ссылок (20 байт) + итоговый `NodeStore`; 1.8 млн записей C: укладываются в бюджет 250 МБ.

### 4.4. Walk scanner

```csharp
// Ключевое: НЕ Directory.EnumerateFileSystemEntries (аллоцирует полный путь на запись
// и требует отдельного stat для размера), а свой FileSystemEnumerator.
sealed class FastEnumerator : FileSystemEnumerator<RawEntry>
{
    protected override RawEntry TransformEntry(ref FileSystemEntry entry) => new()
    {
        Name       = entry.FileName,          // ReadOnlySpan<char>, копируем в общий блоб
        Logical    = entry.Length,            // из FILE_FULL_DIR_INFO, без syscall
        Attributes = entry.Attributes,        // без syscall
        MTime      = entry.LastWriteTimeUtc,  // без syscall
        IsDir      = entry.IsDirectory,
    };

    protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        => entry.IsDirectory
        && (entry.Attributes & FileAttributes.ReparsePoint) == 0;   // точку учитываем, внутрь не идём
}
```

`EnumerationOptions`:
```csharp
new EnumerationOptions {
    RecurseSubdirectories = false,          // рекурсию ведём сами, work-stealing очередью
    IgnoreInaccessible    = false,          // ошибки нужны в отчёт, не в тишину
    AttributesToSkip      = 0,              // НИЧЕГО не скрываем: ни Hidden, ни System
    BufferSize            = 64 * 1024,      // FIND_FIRST_EX_LARGE_FETCH
    MatchType             = MatchType.Win32,
}
```

**Параллелизм — work-stealing, не партиционирование по уровню.**
`ConcurrentQueue<DirTask>`, N воркеров, каждый берёт каталог → перечисляет → подкаталоги обратно в очередь. Партиционирование «по папкам 2-го уровня» отвергнуто: `C:\Windows\WinSxS` — это ~40% всех файлов диска, один воркер молотил бы его в одиночку.

**N по типу носителя:**
```
IOCTL_STORAGE_QUERY_PROPERTY → StorageDeviceSeekPenaltyProperty
  IncursSeekPenalty == true  (HDD)  → N = 2
  IncursSeekPenalty == false (SSD)  → N = min(8, CPU)
  неизвестно / сетевой               → N = 4
```
`MaxDegreeOfParallelism = CPU count` на HDD делает скан **в 2–3 раза медленнее** однопоточного. Дефолт `0` в конфиге означает «автоопределение», а не «по числу ядер».

### 4.5. Incremental rescan через USN Journal

Второй и последующие сканы того же тома:

1. В снимке сохранён `(VolumeSerial, UsnJournalId, NextUsn)`.
2. `FSCTL_QUERY_USN_JOURNAL` → если `UsnJournalId` совпадает и `FirstUsn <= сохранённый NextUsn` — журнал не обнулялся, инкремент возможен.
3. `FSCTL_READ_USN_JOURNAL` с сохранённого USN → список изменённых `FileReferenceNumber`.
4. Применяем изменения к предыдущему снимку: для затронутых файлов перечитываем записи MFT, пересчитываем агрегаты вверх по дереву.
5. Иначе — полный скан.

Результат: повторный скан **0.2–2 с** вместо 5. Это делает историю и diff практичными и позволяет запускать скан по расписанию хоть каждый час.

Если журнал выключен — предложить включить: `fsutil usn createjournal m=32000000 a=8000000 C:` (пользователь выполняет сам, §P10).

### 4.6. Что пропускаем и что учитываем

| Сущность | Рекурсия | Учёт размера | Почему |
|---|---|---|---|
| Reparse point (junction, symlink, mount point) | **нет** | сама точка = 0 байт, помечена `Reparse` | Предотвращает бесконечную рекурсию и двойной учёт. Цель показана в деталях. |
| Cloud placeholder (`RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, `OFFLINE`) | — | **allocated (обычно ~0)**, помечен `CloudOnly` | Cloud-only файл не занимает места. Это важно показать: «50 GB in cloud, 0 GB local». |
| Hidden / System файлы | да | **да** | Пропуск скрывает `hiberfil.sys`, `pagefile.sys`, `AppData` — то есть самое важное. |
| `$Recycle.Bin` | да | да, отдельной категорией | Это освобождаемое место. |
| `System Volume Information` | нет доступа даже у админа | учитывается через VSS-пробу | |
| Собственные данные pathmemo | да | да, помечены `SelfData` | Честность: инструмент показывает и себя. |
| Точки монтирования другого тома | **нет** | 0 | Иначе диск посчитается дважды. Том сканируется отдельно. |

### 4.7. Прогресс

Обновление раз в 250 мс, из потока-рендерера, читающего `volatile`-счётчики (без блокировок на горячем пути).

```
Scanning C:\  ·  MFT mode
  1,284,391 records   ·   412.8 GB   ·   198k rec/s   ·   6.4 s elapsed
  $MFT: 78%  ████████████████████░░░░░
```
Для Walk-режима — путь текущего каталога (обрезанный по ширине) и файлы/с. **ETA не показываем** в Walk-режиме: общее число файлов заранее неизвестно, а фальшивый процент хуже его отсутствия. В MFT-режиме процент честный (число записей известно из `FSCTL_GET_NTFS_VOLUME_DATA`).

### 4.8. Отмена

- `Ctrl+C` / `Esc`: сигнал `CancellationToken`.
- Частичный результат **сохраняется** со статусом `cancelled` и флагом `Partial` — он пригоден для просмотра, но не для diff и не для reclaim-рекомендаций.
- Второй `Ctrl+C` в течение 3 с — немедленный выход без сохранения (защита от зависшего сохранения на сетевом диске).
- Обработчик `PosixSignalRegistration`/`Console.CancelKeyPress` только выставляет токен; запись делает основной поток с таймаутом 10 с.

### 4.9. Ошибки

Каждая ошибка → запись `ScanError { Path, Kind, Win32Code, Message }`, сгруппированная по `Kind`:

```
AccessDenied          104 paths    (~? GB)
SharingViolation       12 paths
PathTooLong             3 paths
NameInvalid             1 path     (created by WSL: contains ':')
IoError                 0 paths
```

Для `AccessDenied` в MFT-режиме размер **известен** (мы читаем метаданные, минуя ACL) — показываем точную цифру. Это важное преимущество MFT-пути.

---

## 5. Формат снимка

### 5.1. Почему не SQLite построчно

Хранить 1 млн файлов строками в SQLite — это 150–250 МБ на скан. С историей это десятки гигабайт: **инструмент для освобождения места сам съест диск** (нарушение P7). Плюс вставка миллиона строк — секунды, а чтение для дерева — снова секунды.

Решение: **бинарный снимок на скан + SQLite только для метаданных и журналов.**

```
%LOCALAPPDATA%\pathmemo\
├── pathmemo.db              # SQLite (WAL): истории, журнал удалений, правила, кэш хешей
├── pathmemo.db-wal
├── config.json              # пользовательская конфигурация
├── snapshots\
│   ├── 0000000042.pmsnap    # ~10–25 МБ на 1 млн файлов
│   └── 0000000043.pmsnap
├── quarantine\
│   └── 0000000043\          # см. §9.4
├── logs\
│   └── pathmemo-20260917.log
└── exports\
```

### 5.2. Структура `.pmsnap` v1

```
HEADER (64 байта, несжатый)
    magic          u8[8]    "PMSNAP\x01\x00"
    formatVersion  u16      1
    toolVersion    u32      packed semver
    createdAtUtc   i64      unix seconds
    nodeCount      u32
    volumeCount    u16
    flags          u32      Partial | Degraded | PartialHardlinkResolution | Elevated | Incremental
    sectionCount   u16
    reserved       u8[...]

SECTION TABLE (sectionCount × 16 байт)
    kind u32, compression u32 (0=raw,1=deflate), rawLen u64, storedLen u64, offset u64

SECTIONS (каждая сжимается независимо)
    NAMES     : len-prefixed UTF-8 сегменты имён, дедуплицированные (не полные пути)
    NAME_IDX  : u32[] смещения в NAMES
    NODES     : Struct-of-Arrays, см. ниже
    VOLUMES   : на каждый том: letter, label, fs, serial, clusterSize, total, free,
                mftSize, metadataSize, usnJournalId, nextUsn
    ERRORS    : ScanError[]
    AGGREGATES: предрасчитанные топы (по расширениям, по категориям) — опционально,
                считаются из NODES за десятки мс, кэш ради мгновенного открытия
```

### 5.3. NODES: Struct-of-Arrays

Узлы упорядочены так, что **дети каждого каталога идут непрерывным диапазоном** (порядок эмиссии — BFS). Это даёт O(1) навигацию и линейное чтение при отрисовке.

Сортировка по размеру **не запекается в снимок**: суммы поддеревьев известны только после агрегирующего прохода снизу вверх, поэтому упорядочивание хранимых массивов потребовало бы второго прохода-перестановки, переписывающего каждый дочерний указатель. Ненадёжный обмен ради того, что на отрисовке стоит миллисекунды. Дети сортируются в `TreeQuery.ChildrenBySize` при показе.

| Массив | Тип | Байт | Назначение |
|---|---|---|---|
| `parent` | `i32[]` | 4 | индекс родителя, `-1` для корня тома |
| `nameIdx` | `i32[]` | 4 | индекс в `NAME_IDX` |
| `firstChild` | `i32[]` | 4 | индекс первого ребёнка, `-1` если нет |
| `childCount` | `i32[]` | 4 | число прямых детей |
| `allocated` | `i64[]` | 8 | для файла — своё; для каталога — сумма поддерева (unique) |
| `logical` | `i64[]` | 8 | то же в логических байтах |
| `fileCount` | `i32[]` | 4 | для каталога — файлов в поддереве |
| `mtime` | `u32[]` | 4 | секунды от 2000-01-01 UTC (хватает до 2136) |
| `attributes` | `u32[]` | 4 | Win32 `FileAttributes` |
| `flags` | `u8[]` | 1 | `IsDir, Reparse, HardlinkAlias, CloudOnly, Sparse, SelfData, Encrypted, HasAds` |
| `linkCount` | `u8[]` | 1 | число hardlink-ссылок, `255` = «255 и более» |
| | | **46** | |

1 млн узлов = **46 МБ** массивов + ~14 МБ блоб имён (сегменты дедуплицированы: `Microsoft`, `bin`, `node_modules` встречаются десятки тысяч раз) ≈ **60 МБ в памяти**, ~12–20 МБ на диске после deflate.

Загрузка: `MemoryMappedFile` + распаковка секций по требованию. Для дерева нужны только `parent/nameIdx/firstChild/childCount/allocated/flags` — можно не грузить остальное, пока не открыли детали.

**Отсутствуют намеренно:**
- `createdAt` — почти никогда не нужен для решения «удалять ли», экономит 4 МБ.
- `accessedAt` — **на Windows это мёртвые данные**: `NtfsDisableLastAccessUpdate` включён по умолчанию с Vista. Показывать «last opened 3 years ago» на файле, открытом вчера, — вводить пользователя в заблуждение. Поле не собирается и не отображается.
- Полные пути — восстанавливаются подъёмом по `parent` за микросекунды.

### 5.4. Retention

Жёсткое правило вместо тройной политики из v2:

```
Keep:  последние 20 снимков
   +   по одному на каждый календарный месяц за последние 12 месяцев
Hard cap: суммарный размер snapshots\ ≤ 400 МБ; при превышении удаляем
          самые старые «месячные», затем самые старые из 20 последних (кроме 3 свежих)
```

Метаданные скана (строка в `scans`) живут вечно — это ~200 байт, и они нужны для графика «занято место во времени» на годы назад. Снимок при этом может быть уже удалён: тогда строка помечена `snapshot_available = 0`, скан можно смотреть только как агрегаты.

*Реализовано в P4:* удалённый retention'ом снимок обнуляет `snapshot_path` в своей строке, `history` показывает такой скан со словом `deleted` вместо размера, `diff` отказывается его открывать с объяснением. Цифры (файлы, байты, тома, категории) остаются.

---

## 6. Space Audit — невидимое место

Обход файловой системы физически не может увидеть половину того, что забивает диск. Это **отдельная подсистема проб**, и для сценария «диск жёстко забивается» она даёт больше ценности, чем скан.

### 6.1. Пробы

Каждая проба возвращает `AuditFinding`:

```csharp
record AuditFinding(
    string   Id,                 // "vss.shadow-storage"
    string   Title,              // "Volume Shadow Copies"
    string   Volume,             // "C:"
    long     UsedBytes,
    long     ReclaimableBytes,   // сколько реально вернётся
    Risk     Risk,               // Safe | Caution | Danger
    Recoverability Recoverability,
    string   Explanation,        // English, 1–3 предложения
    Remedy[] Remedies            // способы устранить
);

record Remedy(
    RemedyKind Kind,             // RunCommand | DeletePaths | OpenSettings | Manual
    string     Display,          // "dism /Online /Cleanup-Image /StartComponentCleanup"
    bool       NeedsElevation,
    bool       NeedsReboot,
    string?    Caveat            // "Removes ability to uninstall installed updates"
);
```

| ID | Что измеряет | Как | Типичный размер | Способ очистки |
|---|---|---|---|---|
| `vss.shadow-storage` | Теневые копии / точки восстановления | CIM `Win32_ShadowStorage`, `Win32_ShadowCopy` через `powershell.exe -NoProfile` из System32, ответ в JSON — байты целыми числами в любой локали; **только elevated** | 5–60 ГБ | `vssadmin delete shadows /for=C: /oldest`, либо уменьшить квоту `vssadmin resize shadowstorage` |
| `winsxs.component-store` | Component store, реально удаляемая часть | `DISM /English /Online /Cleanup-Image /AnalyzeComponentStore`; парсинг по позиции строк `label : value`, не по словам; **только elevated**, без прав показывает размер из снимка с пометкой о hardlink | 2–12 ГБ | `DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase` ⚠️ |
| `windows.old` | Предыдущая установка Windows | наличие + размер из снимка | 10–30 ГБ | `cleanmgr` handler `Previous Installations`, или удаление с `takeown` |
| `wu.softwaredistribution` | Кэш Windows Update | размер `%WINDIR%\SoftwareDistribution\Download` | 1–20 ГБ | stop `wuauserv`+`bits` → clear → start |
| `wu.delivery-optimization` | Кэш P2P-доставки обновлений | `Get-DeliveryOptimizationStatus`, `%WINDIR%\SoftwareDistribution\DeliveryOptimization` | 1–10 ГБ | `Delete-DeliveryOptimizationCache` |
| `windows.installer-orphans` | Осиротевшие MSI/MSP в `%WINDIR%\Installer` | сверка с `HKLM\...\Uninstall` и `Installer\Products` | 2–15 ГБ | ⚠️ только отчёт + ручная проверка; авто-удаление ломает деинсталляцию |
| `hiberfil` | Файл гибернации | размер из листинга корня (без открытия файла) + реестр `Power\HiberFileType` (1 = reduced, 2 = full); `powercfg` не вызывается | 0.4 × RAM | `powercfg /h /type reduced` или `powercfg /h off` |
| `pagefile` | Файл подкачки | размер + `Win32_PageFileSetting` | 1–32 ГБ | System Properties → Advanced → Virtual Memory (`OpenSettings`) |
| `swapfile` | Swapfile для UWP | размер | 256 МБ | вместе с pagefile |
| `recyclebin` | Корзина по каждому тому | `SHQueryRecycleBinW` (`SHQUERYRBINFO` — 24 байта с естественным выравниванием, при `Pack=1` Shell отвечает `E_INVALIDARG`) | 0–50 ГБ | `SHEmptyRecycleBin` (в приложении, P6); до этого `Clear-RecycleBin` |
| `wsl.vhdx` | Виртуальные диски WSL2 | реестр `Lxss` → `BasePath\ext4.vhdx`, logical vs allocated | 10–100 ГБ | `wsl --manage <d> --set-sparse true`, либо `diskpart` → `compact vdisk` |
| `docker.vhdx` | Диски Docker Desktop | `%LOCALAPPDATA%\Docker\wsl\*\*.vhdx` | 10–80 ГБ | `docker system prune -a --volumes` затем compact |
| `hyperv.vhdx` | VHD/VHDX вне Docker/WSL | из снимка по расширению | варьируется | `Optimize-VHD`, отчёт |
| `dumps` | Crash dumps | `MEMORY.DMP`, `%LOCALAPPDATA%\CrashDumps`, `LiveKernelReports`, `Minidump` | 1–20 ГБ | удаление файлов (safe) |
| `onedrive.local` | Локально материализованные облачные файлы | обход + `CloudOnly`-флаги | 10–200 ГБ | `attrib +U -P` (free up space) per-folder |
| `fastboot.reserved` | Reserved storage (Win10 1903+) | `DISM /Online /Get-ReservedStorageState` | 4–7 ГБ | `DISM /Online /Set-ReservedStorageState /State:Disabled` |
| `logs.cbs-panther` | `CBS.log`, `Panther`, `%WINDIR%\Logs` | размер | 0.5–5 ГБ | удаление (safe) |
| `browser.caches` | Кэши Chrome/Edge/Firefox/Brave | известные пути | 1–15 ГБ | удаление при закрытом браузере (safe) |
| `store.temp` | `%WINDIR%\Temp`, `%TEMP%`, `%LOCALAPPDATA%\Temp` | обход | 0.5–20 ГБ | удаление с пропуском занятых (safe) |
| `defender.history` | `%ProgramData%\Microsoft\Windows Defender\Scans\History` | размер | 0.1–3 ГБ | удаление (safe) |
| `ntfs.metadata` | `$MFT` + зарезервированные под метаданные кластеры | `FSCTL_GET_NTFS_VOLUME_DATA` через handle **корневого каталога** (`C:\` + `FILE_FLAG_BACKUP_SEMANTICS`), не через `\\.\C:`: том без `GENERIC_READ` открывается «напрямую в устройство», минуя NTFS, и FSCTL возвращает `ERROR_INVALID_FUNCTION`; `GENERIC_READ` на том требует прав. Каталог — нет. | 1–5 ГБ | **не освобождается**, только объяснение |

### 6.2. Развёрнутый вывод

```
$ pathmemo audit

Volume C:   476.1 GB total   ·   21.3 GB free   ·   95.5% used

  RECLAIMABLE                                             38.4 GB
  ──────────────────────────────────────────────────────────────
  Volume Shadow Copies                        38.4 GB   safe
    12 restore points, oldest 2026-02-11. Windows keeps these for
    System Restore and Previous Versions.
    → vssadmin delete shadows /for=C: /oldest        [admin]
    → vssadmin resize shadowstorage /for=C: /maxsize=10GB   [admin]

  WSL2 virtual disks                          31.2 GB   caution
    Ubuntu-22.04: 44.1 GB on disk, 12.9 GB used inside. WSL disks
    never shrink automatically.
    → wsl --manage Ubuntu-22.04 --set-sparse true
    ⚠ Shut down WSL first: wsl --shutdown

  Component store (WinSxS)                     6.8 GB   caution
    DISM reports 6.8 GB of superseded components.
    → dism /Online /Cleanup-Image /StartComponentCleanup   [admin]
    ⚠ /ResetBase additionally blocks uninstalling current updates

  Hibernation file                             6.4 GB   caution
    hiberfil.sys. Disabling removes Fast Startup and hibernate.
    → powercfg /h off                                 [admin]
    → powercfg /h /size 40    (reduce to 40% instead)  [admin]

  Recycle Bin                                  4.1 GB   safe
    1,204 items.
    → [Empty now]

  Windows Update cache                         2.9 GB   safe
  Crash dumps                                  1.2 GB   safe
  Temp directories                             0.8 GB   safe

  NOT RECLAIMABLE                                         3.9 GB
  ──────────────────────────────────────────────────────────────
  NTFS metadata ($MFT 2.4 GB, $LogFile 0.1 GB, other 1.4 GB)

  Run `pathmemo audit --id vss.shadow-storage` for details.
  Run `pathmemo scan` to find large files and folders.
```

### 6.3. Правила для проб

- Проба **не выполняет** ничего изменяющего систему. Только чтение.
- Внешние утилиты вызываются только для **чтения** (`DISM /Analyze...`, `vssadmin list`, `powercfg /a`) — через `Process.Start` с `ArgumentList`, без shell, с таймаутом 30 с, из `%WINDIR%\System32` по полному пути (защита от PATH hijacking).
- Парсинг вывода `DISM`/`vssadmin` завязан на локаль. Поэтому: `CultureInfo.InvariantCulture` в окружении процесса, парсинг по числам и структуре, а не по английским словам; при неудаче парсинга — проба возвращает `Unknown`, а не ноль.
- Устранение (`Remedy`) по умолчанию **только показывается и копируется в буфер**. Выполнение — только по явной команде `pathmemo audit --apply <id>` или кнопке в TUI, всегда с подтверждением, всегда с логированием. `RemedyKind.RunCommand` с `NeedsElevation` требует отдельного elevated-процесса.

### 6.4. Как это реализовано (P2)

- **Изоляция проб.** `AuditRunner` запускает пробы параллельно (`Parallel.For`, ≤ 8 потоков); каждая ограничена 45 с, исключение или таймаут превращаются в finding со `status=error` и текстом причины — отчёт не пропадает из-за одной пробы. Проба, которая не может измерить, возвращает `NeedsElevation` / `NoSnapshot` / `Unknown` / `NotApplicable`, но никогда не «0 байт».
- **Два числа, оба nullable.** `UsedBytes` — сколько занято, `ReclaimableBytes` — сколько вернётся. WSL и Docker знают первое и не знают второго (сколько свободно внутри vhdx видно только изнутри, а запуск дистрибутива ради вопроса — не read-only действие). Такие пробы попадают в секцию `WORTH A LOOK`, а не в сумму `RECLAIMABLE`. `pagefile` — особый случай: reclaimable равен размеру, если есть другой фиксированный том с > 20 ГБ свободно, и проба прямо советует перенести файл подкачки туда; иначе 0.
- **Измерение каталогов** (`DirectoryMeasure`): размер на диске — логический, округлённый вверх до кластера, и только для файлов с атрибутами `Compressed` или `SparseFile` вызывается `GetCompressedFileSize`. Атрибуты уже лежат в буфере перечисления, поэтому это бесплатно; замер на `%TEMP%` из 175 тыс. файлов: 20 с с вызовом на каждый файл, ~3 с без. Reparse points не считаются и не обходятся, cloud-плейсхолдеры — 0 байт на диске. Ошибки доступа считаются, а не бросаются: «нижняя граница + пометка» полезнее пустого результата.
- **Внешние утилиты** (`ExternalTool`): только `%WINDIR%\System32\*` по абсолютному пути, `ArgumentList`, без окна, без `__COMPAT_LAYER`, таймаут с `Kill(entireProcessTree)`. Вывод декодируется в OEM-кодовой странице консоли (`CodePagesEncodingProvider` регистрируется один раз). Два вызова во всём аудите, оба только elevated: DISM (`/English` фиксирует формат) и `powershell.exe` для CIM.
- **`--apply` отложен до P6**: команда, изменяющая систему, должна попадать в журнал операций (§9.7), а журнала ещё нет. Сейчас есть `--copy` / `--copy-index <n>` (буфер обмена через `OpenClipboard`/`SetClipboardData` на STA-потоке, §15.1) и `c<n>` в интерактивном режиме.
- **Что пробы не умеют без прав администратора:** VSS, DISM (WinSxS, точный статус reserved storage), `Minidump`/`LiveKernelReports`, `Defender\Scans\History`, часть `Windows\Logs`. На реальной машине без прав отвечают 16 проб из 21, за 4.1 с.
- *P4:* результаты полного прогона пишутся в `audit_findings` (§11) с привязкой к скану, который проба использовала. На настоящей машине это 17 строк из 21 пробы: `not applicable` не записывается. Отдельная проба (`--id`) не пишет ничего — это разовая проверка, а не точка истории.

---

## 7. Reclaim — рекомендации по очистке

### 7.1. Две независимые оси вместо одного «риска»

v2 имела одну ось (safe/caution/danger), что смешивало несовместимые вещи: удаление `node_modules` («потеряю 10 минут») и удаление `.git\objects` («потеряю работу») попадали в одну корзину.

**Ось 1 — Risk: что сломается.**

| Уровень | Смысл |
|---|---|
| `Safe` | Ничего не сломается. Приложение пересоздаст при необходимости. |
| `Caution` | Потеряется функциональность или возможность откатиться (uninstall обновления, hibernate, история). Обратимо, но требует действий. |
| `Danger` | Может сломать систему или приложение. Требует ввода подтверждающего слова. |

**Ось 2 — Recoverability: чего будет стоить вернуть.**

| Уровень | Смысл | Примеры |
|---|---|---|
| `Instant` | Пересоздастся автоматически при следующем использовании | browser cache, thumbnail cache, shader cache |
| `Redownload` | Скачается из сети | `node_modules`, `.nuget\packages`, pip cache, Docker images |
| `Rebuild` | Пересоберётся локально, стоит времени CPU | `obj/`, `bin/`, Unity `Library/`, Unreal `DerivedDataCache/`, `.venv` |
| `Irreversible` | Восстановить нельзя | личные файлы, единственная копия установщика, `.git` objects |

UI показывает обе: `safe · redownload` — можно смело; `caution · irreversible` — думать.

### 7.2. Правила по умолчанию

Формат — **глобы**, не regex (см. §12.2). Все правила — данные, не код.

#### Кэши разработки

| Rule | Pattern | Risk | Recov. | Правильный способ |
|---|---|---|---|---|
| `dev.node_modules` | `**\node_modules` | Safe | Redownload | delete; `npm ci` восстановит |
| `dev.npm_cache` | `%LOCALAPPDATA%\npm-cache`, `~\.npm\_cacache` | Safe | Redownload | `npm cache clean --force` |
| `dev.pnpm_store` | `%LOCALAPPDATA%\pnpm\store` | Safe | Redownload | `pnpm store prune` |
| `dev.yarn_cache` | `%LOCALAPPDATA%\Yarn\Cache` | Safe | Redownload | `yarn cache clean` |
| `dev.nuget` | `~\.nuget\packages`, `%LOCALAPPDATA%\NuGet\v3-cache` | Safe | Redownload | `dotnet nuget locals all --clear` |
| `dev.dotnet_artifacts` | `**\bin\Debug`, `**\bin\Release`, `**\obj` | Safe | Rebuild | delete |
| `dev.gradle` | `~\.gradle\caches` | Safe | Redownload | `gradle --stop` затем delete |
| `dev.maven` | `~\.m2\repository` | Safe | Redownload | delete |
| `dev.pip_cache` | `%LOCALAPPDATA%\pip\Cache` | Safe | Redownload | `pip cache purge` |
| `dev.pycache` | `**\__pycache__`, `**\*.pyc` | Safe | Rebuild | delete |
| `dev.venv` | `**\.venv`, `**\venv`, `**\env\Scripts\python.exe` → родитель | Safe | **Rebuild** | delete; `pip install -r` восстановит |
| `dev.cargo` | `~\.cargo\registry`, `**\target\debug`, `**\target\release` | Safe | Rebuild | `cargo clean` |
| `dev.go_modcache` | `~\go\pkg\mod` | Safe | Redownload | `go clean -modcache` |
| `dev.conda_pkgs` | `**\anaconda3\pkgs`, `**\miniconda3\pkgs` | Safe | Redownload | `conda clean --all` |
| `dev.unity_library` | `**\Library\ArtifactDB` + сиблинг `Assets\` | Safe | Rebuild | delete (долгий реимпорт) |
| `dev.unreal_ddc` | `**\DerivedDataCache`, `**\Intermediate`, `**\Saved\Autosaves` | Safe | Rebuild | delete |
| `dev.git_gc` | `**\.git` где `objects` > 500 МБ | **Caution** | **Irreversible** | **`git gc --prune=now --aggressive`** — никогда не удалять `objects` напрямую |
| `dev.docker` | Docker vhdx | Caution | Redownload | `docker system prune -a --volumes` |
| `dev.vs_artifacts` | `**\.vs`, `**\.vscode-server\data\CachedExtensionVSIXs` | Safe | Instant | delete |

#### Приложения и система

| Rule | Pattern | Risk | Recov. |
|---|---|---|---|
| `app.browser_cache` | Chrome/Edge/Brave `**\User Data\*\Cache*`, `**\Code Cache`, `**\GPUCache`; Firefox `**\cache2` | Safe | Instant |
| `app.electron_cache` | `%APPDATA%\{Slack,discord,Teams,...}\Cache`, `**\GPUCache`, `**\ShaderCache` | Safe | Instant |
| `app.shader_cache` | `%LOCALAPPDATA%\{NVIDIA,AMD,D3DSCache}`, Steam `shadercache` | Safe | Instant |
| `app.steam_downloading` | `**\steamapps\downloading`, `**\steamapps\temp` | Safe | Redownload |
| `app.adobe_media_cache` | `%APPDATA%\Adobe\Common\Media Cache*` | Safe | Rebuild |
| `app.apple_backups` | `%APPDATA%\Apple Computer\MobileSync\Backup` | **Caution** | **Irreversible** |
| `sys.temp` | `%TEMP%`, `%WINDIR%\Temp`, `%LOCALAPPDATA%\Temp` | Safe | Instant |
| `sys.thumbnails` | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db` | Safe | Instant |
| `sys.old_logs` | `*.log`, `*.etl` старше 30 дней вне `%ProgramData%` | Safe | Irreversible |
| `sys.dumps` | `**\CrashDumps`, `MEMORY.DMP`, `**\Minidump` | Safe | Irreversible |
| `user.old_installers` | `%USERPROFILE%\Downloads\*.{msi,exe,iso,dmg,pkg}` старше 90 дней | Caution | Redownload* |
| `user.large_media` | `*.{iso,vhd,vhdx,img,bak,vmdk}` больше 1 ГБ | Caution | Irreversible |

\* помечается `Redownload` с оговоркой «unless it's a license-bound installer».

#### Явно исключено из правил

| Не является правилом | Почему |
|---|---|
| `hiberfil.sys`, `pagefile.sys`, `swapfile.sys` | **Файлы заняты, их нельзя удалить.** Это `AuditFinding` с командой `powercfg`, а не цель удаления. |
| `C:\Windows\WinSxS` | Удаление руками ломает систему необратимо. Только через `DISM`. |
| `C:\Windows\Installer` | Удаление ломает деинсталляцию и обновление приложений. Только отчёт. |
| `System Volume Information` | Управляется только через VSS API. |
| `.git\objects` | Уничтожает репозиторий. Только `git gc`. |

### 7.3. Оценка выгоды

Для каждой найденной цели считаем **reclaimable** (§3.1), а не сумму размеров, и группируем:

```
$ pathmemo reclaim --scan 43

  RULE                        MATCHES      SIZE   RECLAIM   RISK     RECOVERY
  ─────────────────────────────────────────────────────────────────────────────
  dev.node_modules                 47   18.2 GB   18.2 GB   safe     redownload
  dev.unity_library                 3   12.4 GB   12.4 GB   safe     rebuild
  app.browser_cache                 6    4.1 GB    4.1 GB   safe     instant
  dev.dotnet_artifacts            212    3.8 GB    3.8 GB   safe     rebuild
  dev.nuget                         1    3.1 GB    3.1 GB   safe     redownload
  sys.temp                          4    1.9 GB    1.4 GB   safe     instant     (0.5 GB locked)
  dev.git_gc                        8    2.2 GB    1.6 GB   caution  irreversible → use git gc
  user.old_installers              23    6.7 GB    6.7 GB   caution  redownload
  ─────────────────────────────────────────────────────────────────────────────
  safe only                       273   43.5 GB   43.0 GB
  including caution               304   52.4 GB   51.3 GB

  pathmemo reclaim --scan 43 --risk safe --dry-run    preview
  pathmemo reclaim --scan 43 --risk safe --apply      execute
```

### 7.4. Пользовательские правила и исключения

Всё в `config.json` — **единственный источник правды** (в v2 правила дублировались между БД и конфигом). Управление из TUI/CLI редактирует этот файл.

```json
{
  "rules": {
    "disabled": ["dev.venv", "user.old_installers"],
    "custom": [
      { "id": "my.render_output", "patterns": ["D:\\renders\\**\\frames"],
        "risk": "safe", "recoverability": "rebuild", "minSizeBytes": 104857600 }
    ]
  },
  "keep": [
    "C:\\Users\\me\\projects\\important\\node_modules",
    "D:\\archive\\**"
  ]
}
```

`keep` — пути и глобы, которые **никогда** не попадут в рекомендации и не могут быть удалены через pathmemo. Добавляется клавишей `K` в TUI.

---

## 8. Дубликаты

### 8.1. Алгоритм

```
Stage 0  filter
         · размер >= minSize (по умолчанию 1 МБ)
         · размер != 0
         · не CloudOnly, не Reparse, не Encrypted
         · не в `keep`
         · группировка по размеру → уникальные размеры отброшены

Stage 1  hardlink collapse
         · файлы с одинаковым (VolumeSerial, FileReferenceNumber) — это ОДИН файл.
           Группируем их как `HardlinkSet`, экономия от удаления = 0.
           Показываются отдельно от настоящих дубликатов.

Stage 2  partial hash — XxHash128 от первых 64 КБ + последних 64 КБ
         (или всего файла, если <= 128 КБ). Одиночки отброшены.

Stage 3  full hash — XxHash128 потоково, буфер 1 МБ, ArrayPool.

Stage 4  byte-for-byte verification финальных кандидатов
         · попарное сравнение внутри группы, 1 МБ блоками
         · даёт МАТЕМАТИЧЕСКУЮ гарантию вместо вероятностной, стоит того же IO
         · групп-кандидатов после stage 3 обычно единицы — дёшево
```

### 8.2. Почему XxHash128, а не BLAKE3

- Узкое место — **IO, а не хеш**: 100 МБ/с на HDD, 500–3000 МБ/с на NVMe. XxHash3 делает 10+ ГБ/с, BLAKE3 — 2–5 ГБ/с. Разницы на практике нет.
- `System.IO.Hashing.XxHash128` — **в коробке, managed, нулевая нативная зависимость**. BLAKE3.NET — ещё одна нативная DLL в self-extract, ещё +200 мс старта, проблемы с trimming/AOT и отдельная сборка под arm64.
- Криптостойкость **не нужна**: мы не защищаемся от злонамеренной коллизии, и после stage 4 гарантия абсолютная.
- **MD5 удалён из выбора.** Он медленнее, слабее и создаёт ложное впечатление «это же криптохеш».
- Опция `--hash sha256` оставлена для случая «хочу хеш, который можно сверить с чужим инструментом», но не как дефолт.

### 8.3. Кэш хешей

Ключ — **не путь** (в v2 переименование сбрасывало кэш):

```sql
PRIMARY KEY (volume_serial, file_id_low, file_id_high, size_bytes, mtime_unix)
```

Вытеснение: LRU по `last_used_at`, жёсткий лимит 200 тыс. записей (~30 МБ). Инвалидация целиком при смене `hash_algo`.

### 8.4. Безопасность операций

- **В группе всегда остаётся минимум один файл.** UI не позволяет снять отметку с последнего; CLI отказывается с exit-кодом `EX_UNSAFE`.
- **Выбор «оригинала» никогда не автоматический без показа.** Эвристика предлагает, пользователь подтверждает. Приоритет: (1) не в `Temp`/`Downloads`/кэшах, (2) меньшая глубина пути, (3) в пути есть `keep`-совпадение → всегда оригинал, (4) старший `mtime`. «Самый старый файл» как единственный критерий отвергнут: старший обычно как раз во `Downloads\tmp`.
- **Перед удалением — повторная проверка.** Размер, mtime и полный хеш **оставляемого** и **удаляемого** пересчитываются заново. Расхождение → отмена всей операции.
- Дубликаты ищутся **между томами** (типичный случай: бэкап фото на D: и оригиналы на C:). Группа не привязана к одному корню скана.
- Файлы открываются с `FileShare.ReadWrite | FileShare.Delete` (иначе половина `AppData` нечитаема) и `FILE_FLAG_SEQUENTIAL_SCAN`.
- **Cloud placeholders никогда не хешируются.** Чтение placeholder'а вызывает скачивание — «поиск дубликатов» мог бы утянуть 200 ГБ трафика и **забить** диск. Проверяем `FILE_ATTRIBUTE_RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS | OFFLINE` до открытия и открываем с `FILE_FLAG_OPEN_NO_RECALL` как второй барьер.

### 8.5. Предупреждение про антивирус

Полный проход чтения заставляет Defender просканировать всё прочитанное. Перед стартом:

```
Hashing will read 84.2 GB from disk. Real-time antivirus scanning may
slow this down 2-5x and use significant CPU.

You can exclude pathmemo from Defender yourself (run as admin):
  Add-MpPreference -ExclusionProcess pathmemo.exe
pathmemo will not change your security settings.

  [Enter] Continue    [S] Skip files over 1 GB    [Esc] Cancel
```

---

## 9. Удаление

Самый опасный модуль. Спроектирован как «ничто не удаляется, пока три независимые проверки не согласились».

### 9.1. Корзина не освобождает место

Главное исправление относительно v2. `$Recycle.Bin` находится **на том же томе**: перемещение 40 ГБ в корзину освобождает **0 байт**. Плюс:

- у корзины есть квота (по умолчанию ~5% тома); файл больше квоты Shell **удалит навсегда, молча** — то есть «безопасный» путь оказывается опаснее прямого;
- перемещение 200 тыс. мелких файлов (`node_modules`) в корзину занимает минуты, создавая `$I`-запись на каждый;
- корзины нет на сетевых дисках, часто нет на съёмных, поведение на ReFS иное;
- `recycle_bin_id` через Shell API **получить нечем** — поле в схеме v2 было нереализуемо.

### 9.2. Три режима, выбираемые по контексту

| Режим | Когда | Освобождает место сразу | Откат |
|---|---|---|---|
| `Recycle` | ≤ 100 файлов, суммарно ≤ 500 МБ, том имеет корзину, `Recoverability != Instant` | **нет** | Проводник → Восстановить |
| `Quarantine` | по умолчанию для всего остального | нет (до `purge`) | `pathmemo restore <op-id>` |
| `Permanent` | явный `--permanent` / клавиша `Shift+D`, либо `Recoverability == Instant` по умолчанию | **да** | нет |

UI всегда показывает, что произойдёт, без эвфемизмов:

```
Delete 47 items · 18.2 GB

  Mode:   quarantine  (moved aside, disk space freed after purge)
  Frees now:      0 bytes
  Frees on purge: 18.2 GB
  Undo:   pathmemo restore op-118    (until purged)

  [Tab] change mode: quarantine / permanent
  [Enter] proceed   [L] list items   [Esc] cancel
```

Для `Permanent` при суммарном размере > 1 ГБ или Risk ≥ `Caution` — подтверждение вводом:
```
Type  delete 47  to confirm permanent deletion:  _
```

### 9.3. Канонизация пути и защита

**Строковое сравнение путей отвергнуто как небезопасное.** Защитный список из v2 обходился двенадцатью способами:

```
c:\windows                      регистр
C:\Windows\                     завершающий слеш
C:\WINDOW~1                     короткое имя 8.3
\\?\C:\Windows                  Win32-префикс
\\.\C:\Windows                  device-префикс
\\localhost\c$\Windows          UNC на себя
\\127.0.0.1\c$\Windows          то же
C:\Users\..\Windows             обход через ..
C:\Documents and Settings\...   junction → C:\Users
C:\Users\All Users\...          junction → C:\ProgramData
D:\mnt\sys\...                  mount point на системный том
```
плюс жёстко зашитый `C:\` неверен: системный том может быть не C:.

**Алгоритм проверки перед любым удалением:**

```
1. Открыть handle:
     CreateFileW(path, 0 /* query only */,
                 FILE_SHARE_READ|WRITE|DELETE, NULL, OPEN_EXISTING,
                 FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL)
   Не удалось открыть → отказ. Никогда не удаляем то, что не смогли открыть.

2. canonical = GetFinalPathNameByHandleW(h, VOLUME_NAME_GUID)
   → \\?\Volume{GUID}\Windows\System32  — устраняет всё из списка выше

3. Проверить canonical против ProtectedSet (тоже канонизированного при старте):
     · KnownFolders: FOLDERID_Windows, System, SystemX86, ProgramFiles,
       ProgramFilesX86, ProgramFilesCommon, ProgramData, UserProfiles,
       Profile, Public, Fonts, Startup, StartMenu, RoamingAppData
       (через SHGetKnownFolderPath — не хардкод «C:\»)
     · Корень любого тома
     · Любой путь глубиной <= 1 от корня тома
     · Любая точка монтирования тома
     · System Volume Information, $Recycle.Bin (кроме операции «empty»)
     · Пути из `keep` в конфиге
     · Собственный каталог данных pathmemo (кроме операции `purge`)
   Совпадение или canonical является ПРЕФИКСОМ защищённого пути → отказ.
   Совпадение по префиксу с защищённым путём (т.е. внутри него) → отказ,
   КРОМЕ явно разрешённых подпутей (C:\Windows\Temp, SoftwareDistribution\Download,
   Logs, CrashDumps) — белый список внутри чёрного, задан правилами.

4. Проверить, что том canonical совпадает с томом, для которого запрошена операция.

5. Перепроверить размер и mtime через FILE_BASIC_INFO / FILE_STANDARD_INFO
   на ЭТОМ ЖЕ handle. Расхождение со снимком → отказ, требуется рескан.

6. Удалять по этому же handle (см. 9.5), не по пути.
```

### 9.4. Карантин

```
%LOCALAPPDATA%\pathmemo\quarantine\
└── op-000118\
    ├── manifest.json        # op-id, время, режим, список (orig path → stored name), размеры, хеши
    └── data\
        ├── 000001\          # оригинальное дерево под числовым именем (обход лимита пути)
        └── 000002\
```

- Перемещение — `MoveFileWithProgressW` с `MOVEFILE_WRITE_THROUGH` **без** `MOVEFILE_COPY_ALLOWED`: в пределах тома это переименование, мгновенное и атомарное. Если карантин на другом томе — операция **отклоняется** (копирование 18 ГБ вместо переименования недопустимо), предлагается `Permanent` или карантин на том же томе.
- Карантин создаётся **на каждом задействованном томе**: `D:\pathmemo-quarantine\` (скрытая системная папка) для файлов с D:.
- `pathmemo restore op-118` возвращает всё по манифесту, проверяя, что целевые пути свободны.
- `pathmemo purge` физически удаляет. Автоматический purge — по возрасту: карантин старше `quarantineRetentionDays` (по умолчанию 7) удаляется при старте приложения, с записью в журнал. **С предупреждением при первом запуске**, чтобы это не было сюрпризом.
- Дашборд всегда показывает: `Quarantine: 18.2 GB in 2 operations — purge to reclaim`.

### 9.5. Рекурсивное удаление без follow-the-symlink

Классическая уязвимость: между проверкой и удалением подкаталог подменяется на junction на `C:\Windows\System32`, и рекурсивный удалятель уходит туда.

**Защита — обход по handle, а не по пути:**

```
DeleteTree(parentHandle):
    для каждой записи в NtQueryDirectoryFile(parentHandle):
        childHandle = NtCreateFile(name,
                          RootDirectory = parentHandle,        // относительное открытие!
                          FILE_OPEN_REPARSE_POINT)             // не следуем за точкой
        если childHandle это reparse point:
            удалить саму точку (FILE_DISPOSITION_INFORMATION), внутрь НЕ входить
        иначе если каталог:
            DeleteTree(childHandle)     // рекурсия по handle
            удалить каталог по handle
        иначе:
            SetFileInformationByHandle(childHandle, FileDispositionInfoEx,
                                       FILE_DISPOSITION_DELETE | POSIX_SEMANTICS)
```

`RootDirectory = parentHandle` означает, что имя разрешается **относительно уже открытого каталога**. Подмена пути в середине операции физически не может перенаправить нас в другое место дерева.

`FILE_DISPOSITION_POSIX_SEMANTICS` (Win10 1709+) позволяет удалить файл, который открыт другим процессом (имя исчезает сразу, данные — при закрытии последнего handle). Это важно для кэшей в работающих приложениях.

### 9.6. Recycle Bin через `IFileOperation`

`SHFileOperation` — deprecated, глотает ошибки и возвращает «успех» при нулевом результате. Только `IFileOperation`:

```csharp
op.SetOperationFlags(FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
                   | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING);
op.SetOwnerWindow(IntPtr.Zero);
op.Advise(new ProgressSink());   // ОБЯЗАТЕЛЬНО: иначе не узнаем, что именно не удалилось
```

- COM требует STA → выделенный поток с `[STAThread]`-эквивалентом (`Thread.SetApartmentState`).
- `FOF_WANTNUKEWARNING` **без** `FOF_NOCONFIRMATION` для больших файлов дал бы диалог; мы вместо этого **сами** проверяем размер против квоты корзины (`SHQueryRecycleBinW` + `MaxCapacity` из реестра) и переключаем режим на `Quarantine`, не доводя до тихого уничтожения.
- `IFileOperationProgressSink.PostDeleteItem` даёт per-item `HRESULT` — пишем в журнал каждый элемент.

### 9.7. Журнал операций

```
op-000118  2026-09-17T14:22:08Z  quarantine  47 items  18.2 GB  reclaimed 0 B
  status: completed   ·   restorable until 2026-09-24
  47 succeeded, 0 failed
```

Каждый элемент — строка в `deletion_items`. `dry_run` операции пишутся **в отдельную таблицу** `dryrun_log`, а не в общую с флагом: смешивание реальных и пробных удалений в одном журнале делает аудит ненадёжным.

### 9.8. Замер фактического освобождения

До операции и после (с задержкой 500 мс на завершение отложенных операций ФС) читаем `GetDiskFreeSpaceExW`:

```
Done in 1.4 s
  47 items moved to quarantine
  Predicted:  18.2 GB     Actual free space change:  0 bytes  (as expected)

  pathmemo purge op-118    to reclaim 18.2 GB
```
и после purge:
```
  Predicted:  18.2 GB     Actual:  18.4 GB      C: free  21.3 GB → 39.7 GB
```
Расхождение > 10% логируется и показывается: это единственный способ заметить, что модель размеров врёт.

---

## 10. История и diff

### 10.1. Автоматический скан по расписанию

**Без этого вся история мертва** — человек не будет сканировать вручную дважды в неделю.

```
$ pathmemo schedule --weekly --time 03:00
Registered scheduled task "pathmemo weekly scan".
  Runs: every Monday at 03:00, only when the computer is idle and on AC power.
  Command: pathmemo scan --all-volumes --quiet
  Remove with: pathmemo schedule --off
```

Реализация — `ITaskService` (COM) или `schtasks.exe` с `ArgumentList`. Настройки задачи: `RunOnlyIfIdle`, `DontStopOnIdleEnd = false`, `StartWhenAvailable = true`, `DisallowStartIfOnBatteries = true`, приоритет `BELOW_NORMAL`. Регистрация в контексте текущего пользователя (без пароля) — значит скан пройдёт без прав админа и в degraded-режиме; при elevated-установке предлагаем `RunLevel = Highest` для MFT-скана.

### 10.2. Diff

```
$ pathmemo diff 41 43

C:   +18.4 GB     2026-09-10 03:00 → 2026-09-17 03:00

  GREW                                                    +24.1 GB
    C:\Users\me\AppData\Local\Docker                       +9.8 GB   12.1 → 21.9
    C:\Users\me\projects\bigapp\node_modules               +4.2 GB   0.1 → 4.3
    C:\Program Files\Epic Games\Fortnite                   +3.9 GB
    C:\Windows\SoftwareDistribution                        +2.8 GB
    ... 42 more

  SHRANK                                                   -5.7 GB
    C:\Users\me\Downloads                                  -4.1 GB
    ... 8 more

  APPEARED                                                 +6.2 GB   (118 paths)
  DISAPPEARED                                              -6.2 GB   (204 paths)

  Largest single new file:
    C:\Users\me\Downloads\Win11_24H2.iso                    5.8 GB
```

Алгоритм: оба снимка загружаются, обходятся синхронно по отсортированным именам детей (merge-join на уровне каталога), порог «интересного» изменения — `max(64 МБ, 1% размера родителя)`, глубина ограничена до первого «объясняющего» уровня (не показываем `node_modules\.bin\x`, если вырос весь `node_modules`).

Diff требует, чтобы **оба снимка были доступны** и оба не `Partial`.

#### 10.2.1. Как это реализовано (P4)

- **Порог считается от изменения, а не от размера родителя.** 1% от корня тома на 500 ГБ — это 5 ГБ: при формулировке выше diff молчал бы обо всём, что меньше, то есть почти обо всём. Реализовано `max(64 МБ, 1% изменения этого уровня)`: на диске, который вырос на 18 ГБ, порог верхнего уровня — 180 МБ, а на диске, который сдвинулся на 200 МБ, — те же 64 МБ, и мелочь всё равно видна. Порог сужается на спуске сам, как и задумано.
- **Строки не пересекаются, а их сумма и остаток равны изменению тома.** Если дети объясняют только часть изменения каталога, остаток выводится одной строкой `residual` для самого каталога (`C:\mix  +6.0 GB  other entries here`) — это честный способ сказать «здесь тысяча мелких файлов». Если же ни один ребёнок не прошёл порог, каталог показывается целиком (`8 GB -> 16 GB`), и спуск на этом останавливается: именно так `C:\Windows\SoftwareDistribution` попадает в отчёт одной строкой вместо 400.
- **`UNEXPLAINED` закрывает арифметику.** Остаток меньше порога своего уровня строкой не становится — в этом и смысл порога, — поэтому `GREW + SHRANK` может не сойтись с изменением тома. Разница печатается отдельной строкой, а не остаётся загадкой: на настоящей паре сканов это было 92 МБ из 917 МБ. Читатель, который складывает два столбца и получает другое число, прав, когда перестаёт доверять инструменту.
- **Appeared/disappeared показываются двумя способами**, в отличие от примера выше, где они только счётчики: крупные новые (или исчезнувшие) записи попадают в `GREW`/`SHRANK` с пометкой `new`/`gone` — иначе новый 10-гигабайтный каталог не имел бы в отчёте пути, — а сводка по всем, включая мелкие, остаётся отдельным блоком со счётчиком путей и крупнейшим новым файлом. Счётчик собирается там, куда отчёт спускался, то есть в каталогах, изменение которых прошло порог, и строка под ним это прямо говорит: число без границ применимости — ровно то, из-за чего дисковым инструментам не верят.
- **Тома сопоставляются по букве**; том, который есть только в одном снимке, и несовпадение volume serial (та же буква, другая файловая система) выводятся строкой `!`.
- **Несравнимость сканеров называется вслух.** Разные сканеры, разный elevated-статус, разная политика hardlink'ов, разный учёт ADS — четыре отдельных предупреждения перед цифрами. На настоящих снимках это видно сразу: walk без прав против MFT даёт `+1.71 ГБ C:\$MFT (new)` и `-1.6 ГБ C:\Windows\WinSxS` — не изменения диска, а разница видимости.
- **Отказы:** нет снимка (retention оставил только цифры), снимок не читается, снимок `Partial` — каждый со своим сообщением и кодом `EX_NO_DATA`. Порядок аргументов не важен: снимки сортируются по времени, чтобы рост никогда не показался убылью.
- **Без аргументов** — два последних снимка; с одним — он против последнего. `--limit`, `--min`, `--size`, `--json`.
- **Цена:** загрузка двух снимков по 1.6 млн узлов — 405 мс, сам merge-join — 105 мс на настоящей паре walk/MFT (958 появившихся путей, около 60 строк роста и убыли) и 5 мс на паре, где изменился один каталог. `PATHMEMO_DIAG=1` печатает этот разбор в stderr.

---

## 11. Схема базы данных

SQLite хранит **только** то, что должно быть запрашиваемым и долгоживущим. Дерево файлов — в снимках (§5).

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store   = MEMORY;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size   = -16384;     -- 16 МБ, не 64: БД маленькая

-- Версионирование: PRAGMA user_version, без DbUp
-- integrity_check выполняется ТОЛЬКО если предыдущий выход был некорректным
-- (маркерный файл .clean удаляется при старте, создаётся при чистом выходе)

CREATE TABLE scans (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at          TEXT    NOT NULL,          -- ISO-8601 UTC
    finished_at         TEXT,
    status              TEXT    NOT NULL,          -- running|completed|cancelled|failed
    scanner             TEXT    NOT NULL,          -- mft|walk|incremental
    flags               INTEGER NOT NULL DEFAULT 0,-- Partial|Degraded|Elevated|...
    roots               TEXT    NOT NULL,          -- JSON: ["C:\\","D:\\"]
    total_files         INTEGER NOT NULL DEFAULT 0,
    total_dirs          INTEGER NOT NULL DEFAULT 0,
    allocated_bytes     INTEGER NOT NULL DEFAULT 0,  -- unique allocated
    logical_bytes       INTEGER NOT NULL DEFAULT 0,
    duration_ms         INTEGER,
    error_count         INTEGER NOT NULL DEFAULT 0,
    tool_version        TEXT    NOT NULL,
    snapshot_path       TEXT,                       -- NULL если снимок удалён retention
    snapshot_bytes      INTEGER,
    note                TEXT
);
CREATE INDEX idx_scans_started ON scans(started_at DESC);

CREATE TABLE scan_volumes (
    scan_id         INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    letter          TEXT    NOT NULL,
    label           TEXT,
    filesystem      TEXT,
    volume_serial   INTEGER NOT NULL,
    volume_guid     TEXT,
    cluster_bytes   INTEGER NOT NULL,
    total_bytes     INTEGER NOT NULL,
    free_bytes      INTEGER NOT NULL,
    scanned_bytes   INTEGER NOT NULL,
    metadata_bytes  INTEGER,
    unaccounted_bytes INTEGER,
    usn_journal_id  INTEGER,
    next_usn        INTEGER,
    PRIMARY KEY (scan_id, letter)
);

-- Агрегаты для графиков за годы (переживают удаление снимка)
CREATE TABLE scan_category_totals (
    scan_id       INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    category      TEXT    NOT NULL,   -- media|archive|cache|source|document|app|system|other
    allocated_bytes INTEGER NOT NULL,
    file_count    INTEGER NOT NULL,
    PRIMARY KEY (scan_id, category)
);

CREATE TABLE scan_extension_totals (
    scan_id       INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    extension     TEXT    NOT NULL,
    allocated_bytes INTEGER NOT NULL,
    file_count    INTEGER NOT NULL,
    PRIMARY KEY (scan_id, extension)
);

-- Результаты Space Audit
CREATE TABLE audit_findings (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_id       INTEGER REFERENCES scans(id) ON DELETE CASCADE,
    probed_at     TEXT    NOT NULL,
    finding_id    TEXT    NOT NULL,   -- "vss.shadow-storage"
    volume        TEXT,
    used_bytes    INTEGER,
    reclaimable_bytes INTEGER,
    risk          TEXT    NOT NULL,
    recoverability TEXT   NOT NULL,
    status        TEXT    NOT NULL,   -- ok|unknown|probe_failed
    detail_json   TEXT
);
CREATE INDEX idx_audit_scan ON audit_findings(scan_id, reclaimable_bytes DESC);

-- Реальные операции удаления
CREATE TABLE delete_ops (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at        TEXT    NOT NULL,
    finished_at       TEXT,
    mode              TEXT    NOT NULL,   -- recycle|quarantine|permanent
    source            TEXT    NOT NULL,   -- tree|reclaim|dupes|audit|cli
    scan_id           INTEGER REFERENCES scans(id) ON DELETE SET NULL,
    item_count        INTEGER NOT NULL,
    predicted_bytes   INTEGER NOT NULL,
    actual_freed_bytes INTEGER,
    status            TEXT    NOT NULL,   -- completed|partial|failed|restored|purged
    quarantine_path   TEXT,
    purge_after       TEXT,
    reason            TEXT
);
CREATE INDEX idx_delete_ops_date ON delete_ops(started_at DESC);

CREATE TABLE delete_items (
    op_id         INTEGER NOT NULL REFERENCES delete_ops(id) ON DELETE CASCADE,
    seq           INTEGER NOT NULL,
    original_path TEXT    NOT NULL,
    stored_name   TEXT,                   -- имя внутри карантина
    size_bytes    INTEGER NOT NULL,
    content_hash  TEXT,                   -- для проверки при restore
    result        TEXT    NOT NULL,       -- ok|skipped|failed
    hresult       INTEGER,
    message       TEXT,
    PRIMARY KEY (op_id, seq)
);

-- Dry-run — ОТДЕЛЬНО от реальных операций
CREATE TABLE dryrun_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ran_at      TEXT    NOT NULL,
    source      TEXT    NOT NULL,
    item_count  INTEGER NOT NULL,
    total_bytes INTEGER NOT NULL,
    detail_json TEXT
);

-- Кэш хешей: ключ по file id, не по пути
CREATE TABLE file_hashes (
    volume_serial INTEGER NOT NULL,
    file_id_low   INTEGER NOT NULL,
    file_id_high  INTEGER NOT NULL,
    size_bytes    INTEGER NOT NULL,
    mtime_unix    INTEGER NOT NULL,
    hash_algo     TEXT    NOT NULL,
    full_hash     TEXT    NOT NULL,
    computed_at   TEXT    NOT NULL,
    last_used_at  TEXT    NOT NULL,
    PRIMARY KEY (volume_serial, file_id_low, file_id_high, size_bytes, mtime_unix, hash_algo)
);
CREATE INDEX idx_file_hashes_hash ON file_hashes(hash_algo, full_hash);
CREATE INDEX idx_file_hashes_lru  ON file_hashes(last_used_at);

-- Найденные группы дубликатов последнего прогона (кэш, не история)
CREATE TABLE dupe_runs (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    ran_at        TEXT    NOT NULL,
    scan_id       INTEGER REFERENCES scans(id) ON DELETE CASCADE,
    min_size      INTEGER NOT NULL,
    hash_algo     TEXT    NOT NULL,
    group_count   INTEGER NOT NULL,
    wasted_bytes  INTEGER NOT NULL
);

CREATE TABLE dupe_groups (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id        INTEGER NOT NULL REFERENCES dupe_runs(id) ON DELETE CASCADE,
    size_bytes    INTEGER NOT NULL,
    file_count    INTEGER NOT NULL,
    wasted_bytes  INTEGER NOT NULL,
    kind          TEXT    NOT NULL,     -- duplicate|hardlink_set
    full_hash     TEXT    NOT NULL
);
CREATE INDEX idx_dupe_groups ON dupe_groups(run_id, wasted_bytes DESC);

CREATE TABLE dupe_files (
    group_id      INTEGER NOT NULL REFERENCES dupe_groups(id) ON DELETE CASCADE,
    seq           INTEGER NOT NULL,
    path          TEXT    NOT NULL,
    mtime_unix    INTEGER,
    link_count    INTEGER NOT NULL DEFAULT 1,
    suggested_keep INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (group_id, seq)
);
```

**Удалено по сравнению с v2:** `top_files`, `folder_stats` (дерево теперь в снимке), `junk_rules` / `user_exclusions` / `user_keep` (единственный источник — `config.json`), `recycle_bin_id` / `restored` в журнале (нереализуемо / заменено на `delete_ops.status`), `accessed_at` (мёртвые данные), `schema_version` (нативный `PRAGMA user_version`).

**Запись снимка:** батчи по 20 тыс. строк для агрегатов, `PRAGMA wal_checkpoint(PASSIVE)` после каждой операции скана. Одна транзакция на миллион вставок раздула бы WAL до размера данных.

### 11.1. Как это реализовано (P4)

- **Схема создаётся целиком, одной версией.** `PRAGMA user_version` = 1, `Storage/Schema.sql` как embedded resource, применение в одной транзакции. Таблицы будущих фаз (`delete_ops`, `dupe_*`, `file_hashes`) создаются сразу и стоят пустыми несколько сотен байт: одна версия схемы проще, чем шесть, и P6 не начнётся с миграции.
- **`scans.id` — это номер файла снимка.** Id выделяется до старта скана одной вставкой с `AUTOINCREMENT`, снимок пишется под полученным номером. Иначе `history` и `tree --scan 42` говорили бы о разных сканах. Вставка, а не «максимум плюс один», выбрана ради конкурентности: два pathmemo, запущенные одновременно, получают разные номера и не перетирают снимки друг друга. Удаление БД при этом ничего не ломает — снимки на диске импортируются обратно со своими номерами (4 настоящих снимка за 1.6 с), а `AUTOINCREMENT` продолжает нумерацию выше них. Если база недоступна вовсе, номер берётся из каталога снимков — единственный случай, когда нумерация не атомарна.
- **Файл — факт, строка — индекс.** Перед чтением истории выполняется сверка: снимок без строки импортируется, строка без снимка (retention, удаление руками, обрезанный или чужой файл — проверяется заголовок) теряет `snapshot_path`, но сохраняет цифры. Так `snapshot_available = 0` из §5.4 становится наблюдаемым состоянием, а не обещанием.
- **БД — удобство, а не условие работы.** Занятая или сломанная база не стоит потерянного скана: снимок всё равно пишется, в stderr идёт одна строка «history not updated», и до конца запуска база не трогается. `EX_LOCKED` (8) возвращают только команды, которым база обязательна (`history`, `diff`). В WAL-режиме читатель при этом не блокируется вовсе — второй процесс спокойно смотрит историю, пока первый пишет.
- **`integrity_check` — только после грязного выхода.** Маркер `.clean` удаляется при старте и пишется при закрытии соединения; на настоящей базе проверка занимает миллисекунды и не выполняется вовсе при нормальном цикле.
- **`--no-save` не пишет и строки.** «Проанализировать, не сохраняя» — значит не сохранять ничего, иначе экспорт в JSON из скрипта незаметно набивал бы историю.
- **Агрегаты.** `scan_category_totals` — восемь корзин, `scan_extension_totals` — 250 самых крупных расширений плюс строка `(rest)` с остатком: на полном диске десятки тысяч уникальных суффиксов, и хранить их все означало бы, что база переживёт снимки за свой счёт. Категория наследуется от каталога (`C:\Windows` → system, `node_modules` → cache) и только потом определяется по расширению: в `C:\Windows` полно `.wav`, который не «медиа пользователя». Метафайлы NTFS в корне тома (`$MFT` на 1.7 ГБ и соседи, которые видит только MFT-сканер) — тоже system, но именно в корне: пользовательский файл имеет право называться `$draft`. Считаются один раз за скан и идут и в базу, и в блок `By category` итогового вывода, и в JSON: цифры, которые некому прочитать, — половина функции. На настоящем C: (walk, 1.22 млн файлов, 197 ГБ): system 62.5 ГБ, other 49.8, cache 34.5 (613 тыс. файлов), app 34.2, archive 12, source 2.6, media 1.7, document 0.2.
- **`audit_findings` пишутся** при полном прогоне `audit` (не при `--id`, это разовая проверка), findings со статусом `not applicable` пропускаются: «на этой машине нет WSL» — не точка данных.
- **Пока NULL:** `scan_volumes.metadata_bytes`, `usn_journal_id`, `next_usn` — появятся с USN-инкрементом (P9). `dryrun_log`, `delete_*`, `dupe_*`, `file_hashes` — с P6 и P8.

---

## 12. Конфигурация

`%LOCALAPPDATA%\pathmemo\config.json`. Все ключи — camelCase, все размеры принимают человекочитаемый вид (`"1GB"`, `"512MB"`, `1048576`), все длительности — `"30d"`, `"12h"`.

```json
{
  "$schema": "https://pathmemo.dev/schema/config-1.json",

  "scan": {
    "preferMftScanner": true,
    "promptForElevation": true,
    "parallelism": "auto",
    "excludeFromScan": [],
    "doNotRecurse": ["\\\\*"],
    "includeCloudOnlyFiles": true,
    "progressIntervalMs": 250,
    "useUsnIncremental": true
  },

  "protect": {
    "keep": [],
    "allowInsideProtected": [
      "%WINDIR%\\Temp\\**",
      "%WINDIR%\\SoftwareDistribution\\Download\\**",
      "%WINDIR%\\Logs\\**",
      "%WINDIR%\\Prefetch\\**"
    ]
  },

  "delete": {
    "defaultMode": "quarantine",
    "recycleMaxItems": 100,
    "recycleMaxBytes": "500MB",
    "quarantineRetentionDays": 7,
    "autoPurgeExpired": true,
    "requireTypedConfirmationOverBytes": "1GB",
    "verifyBeforeDelete": true
  },

  "duplicates": {
    "minSize": "1MB",
    "hashAlgorithm": "xxh128",
    "bufferSize": "1MB",
    "partialHashBytes": "64KB",
    "byteForByteVerify": true,
    "crossVolume": true,
    "skipCloudOnly": true,
    "hashCacheMaxEntries": 200000
  },

  "rules": {
    "disabled": [],
    "custom": []
  },

  "storage": {
    "dataDirectory": "%LOCALAPPDATA%\\pathmemo",
    "keepRecentSnapshots": 20,
    "keepMonthlySnapshots": 12,
    "snapshotsMaxBytes": "400MB"
  },

  "logging": {
    "level": "Information",
    "retentionDays": 14,
    "logFilePaths": false
  },

  "ui": {
    "sizeMode": "unique",
    "minWidth": 80,
    "minHeight": 24,
    "mouse": true,
    "confirmOpenExecutables": true
  },

  "export": {
    "redactPaths": false
  }
}
```

### 12.1. Elevated-режим игнорирует пользовательский конфиг для путей

**Уязвимость v2:** конфиг лежит в `%LOCALAPPDATA%` (пишется обычным пользователем), а для MFT-скана процесс запускается от админа. Значит `logging.filePath` и `database.path` из конфига дали бы **произвольную запись файла от имени администратора** — локальное повышение привилегий.

**Правило:** при `IsProcessElevated()`:
- `storage.dataDirectory` и путь логов **не берутся из конфига**, только из фиксированного `%LOCALAPPDATA%` вызывающего пользователя (через `WTSQueryUserToken`/`SHGetKnownFolderPath` с токеном сессии);
- проверяется DACL файла конфига: если на него есть write у групп шире `Administrators`/`SYSTEM`/владельца — выводится предупреждение, а `rules.custom`, `protect.*` и всё, что влияет на удаление, **игнорируется**, работают только дефолты и CLI-аргументы;
- **никогда** не поддерживается конструкция вида «выполнить команду после очистки». Такого ключа нет и не будет.

*Реализовано в P4:* то же правило распространено на `--data-dir` — единственный способ переопределить каталог данных, который сейчас существует. При elevated опция печатает предупреждение и игнорируется: аргумент командной строки от админа — такой же примитив произвольной записи, как ключ конфига. `config.json` пока не читается вовсе.

### 12.2. Глобы, а не regex

v2 смешивала два синтаксиса в одном документе (`**\node_modules` рядом с `.*\\\\Temp$`). Выбран **один**: глобы.

- Матчинг — `FileSystemName.MatchesSimpleExpression` (встроенный, аллокаций 0) с расширением для `**` (любое число сегментов).
- Раскрываются переменные: `%WINDIR%`, `%TEMP%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%`, `%ProgramData%`, `%ProgramFiles%`, `~` — через `SHGetKnownFolderPath`, не через хардкод `C:\`.
- Матчинг по **канонизированным** путям (нижний регистр инвариантный, разрешённые reparse points).
- Regex доступен как опт-ин: `{ "regex": "..." }` вместо `"pattern"`, **обязательно** с `RegexOptions.NonBacktracking | CultureInvariant` и `matchTimeout = 50 ms`. Без `NonBacktracking` один плохой паттерн вешал бы скан миллиона путей катастрофическим бэктрекингом (ReDoS).
- Правило матчится **только по имени и суффиксу пути**, не по всему пути целиком, где это возможно — быстрый префильтр по последнему сегменту через хеш-сет даёт O(1) отсев 99% узлов.

---

## 13. CLI

Все команды, весь вывод — английский. Читаемый вывод по умолчанию, `--json` для скриптов.

```
pathmemo                                   TUI (если TTY), иначе `status`

pathmemo scan [<path>...] [options]        скан; без пути — все локальные фиксированные тома
pathmemo status                            сводка последнего скана + свободное место
pathmemo tree [<path>] [--scan <id>]       дерево, крупнейшее сверху (неинтерактивно)
pathmemo top [options]                     крупнейшие файлы/папки с фильтрами
pathmemo audit [--id <finding>] [--apply <finding>]
pathmemo reclaim [options]                 рекомендации по очистке
pathmemo dupes [options]                   поиск дубликатов
pathmemo rm <path>... [options]            удаление (карантин по умолчанию)
pathmemo restore <op-id>                   восстановление из карантина
pathmemo purge [<op-id>|--expired|--all]   физическое удаление карантина
pathmemo ops [--limit N]                   журнал операций удаления
pathmemo history [--limit N] [--since <date>]
pathmemo diff <id-a> <id-b>
pathmemo errors <scan-id>
pathmemo export <scan-id> --format json|csv --output <file> [--redact]
pathmemo schedule [--weekly|--daily] [--time HH:MM] | --off | --status
pathmemo config [--path | --edit | --reset]
pathmemo doctor                            диагностика: права, USN, ФС, БД, версия
```

### 13.1. Общие опции

```
--json                  стабильный машиночитаемый вывод в stdout, логи в stderr
--quiet                 только ошибки
--no-color              отключить ANSI (также уважается NO_COLOR и не-TTY)
--size logical|allocated|unique      режим отображения размеров (default: unique)
--yes                   не спрашивать подтверждений (НЕ действует на permanent delete
                        и на Risk=Danger — они всегда требуют --force и типизированного
                        подтверждения либо --confirm-token)
--data-dir <path>       альтернативный каталог данных (игнорируется при elevated)
--verbose / -v
```

*Реализовано в P4:* `--data-dir` — снимается из аргументов до разбора команды, поэтому работает с любой из них; при elevated печатает предупреждение и игнорируется (§12.1). `--json` есть у `scan`, `diff`, `history` и `audit`. `--size` — у `tree`, `top`, `diff`. Остальное — по мере фаз.

*Реализовано в P5:* `status` — те же данные, что на экране Overview, но текстом: тома со свободным местом и unaccounted, последний скан, размер хранилища. Это же печатается, когда `pathmemo` запускают без аргументов с перенаправленным stdout (§14.5). `--no-color` пока не разбирается как опция, но `NO_COLOR` в окружении уважается: TUI рисуется без палитры, инверсия выделенной строки остаётся.

### 13.2. `scan`

```
pathmemo scan [<path>...]
  --all-volumes             все локальные фиксированные тома
  --scanner mft|walk|auto   default: auto
  --no-elevate              не предлагать релонч, сразу degraded
  --full                    игнорировать USN и сканировать с нуля
  --exclude <glob>          можно несколько; только для ЭТОГО скана
  --parallelism <n|auto>
  --note <text>
  --no-audit                не запускать Space Audit после скана
  --format console|json|csv
  --output <file>
```

*Реализовано в P3:* `--scanner mft|walk` (без значения — auto), `--no-elevate`, `--parallelism`, `--note`, `--top`, `--no-save`, `--quiet`; добавлен `--pause` — его подставляет сам инструмент при перезапуске с правами, чтобы новое окно консоли не закрылось до того, как пользователь увидит итог.

*Реализовано в P4:* `--format console|json|csv` и `--output <file>`; `--json` — синоним `--format json`. JSON содержит `schemaVersion`, метаданные скана, тома со сверкой, категории, топы и список `limitations` — машиночитаемую версию того же предупреждения о точности, которое печатает консоль. CSV — таблица крупнейших записей по `--top` (полный экспорт дерева — это `export`, отдельная команда). При не-консольном формате прогресс не печатается, а подтверждение записи файла идёт в stderr: в stdout только данные (§13.6). Консольный итог получил блок `By category` — те же восемь корзин, что уходят в базу. Остальное — по мере фаз (`--full` в P9, `--exclude` в P7, `--no-audit` вместе с автозапуском audit).

### 13.3. `top`

Основной «полу-CLI» сценарий — быстро найти жирное с фильтрами.

```
pathmemo top
  --scan <id>            default: последний
  --files | --dirs       default: --files
  --limit <n>            default: 40
  --under <path>         только внутри пути
  --min <size>           например 1GB
  --ext <a,b,c>          iso,vhdx,zip
  --older-than <dur>     180d
  --newer-than <dur>
  --category <c>
  --sort size|mtime|count
  --paths-only           один путь на строку — для пайпа
```

```
$ pathmemo top --min 1GB --ext iso,vhdx --older-than 90d

     SIZE  MODIFIED    PATH
  ───────────────────────────────────────────────────────────────────────
  44.1 GB  2026-01-04  C:\Users\me\AppData\Local\Packages\…\ext4.vhdx
   5.8 GB  2026-03-11  C:\Users\me\Downloads\Win11_24H2.iso
   4.2 GB  2025-11-28  D:\vm\win10-test.vhdx
  ───────────────────────────────────────────────────────────────────────
  3 files · 54.1 GB
```

### 13.4. `rm`

```
pathmemo rm <path>...
  --mode quarantine|recycle|permanent     default: из конфига
  --dry-run
  --reason <text>
  --from-stdin                            пути с stdin, по одному на строку
  --scan <id>                             сверять размеры со снимком
  --force                                 требуется для Risk>=Caution
  --confirm-token <token>                 неинтерактивная замена типизированного
                                          подтверждения; токен печатается в dry-run
```

`--confirm-token` — решение проблемы «как автоматизировать опасную операцию, не делая `--yes` универсальной отмычкой»: dry-run печатает токен, зависящий от точного списка путей и размеров; если что-то изменилось — токен невалиден.

### 13.5. Exit-коды

| Код | Константа | Смысл |
|---|---|---|
| 0 | `EX_OK` | успех |
| 1 | `EX_FAILURE` | общая ошибка |
| 2 | `EX_USAGE` | неверные аргументы |
| 3 | `EX_PARTIAL` | выполнено частично (часть путей недоступна) |
| 4 | `EX_CANCELLED` | отменено пользователем |
| 5 | `EX_NEEDS_ELEVATION` | требуются права администратора |
| 6 | `EX_UNSAFE` | операция отклонена защитой (protected path, последняя копия, verify failed) |
| 7 | `EX_NO_DATA` | нет снимка/скана для запроса |
| 8 | `EX_LOCKED` | БД занята другим процессом — возвращают только команды, которым БД обязательна (`history`, `diff`); скан при занятой БД сохраняет снимок и предупреждает (§11.1) |

### 13.6. Стабильность JSON

`--json` выводит объект с полем `"schemaVersion": 1`. Поля только добавляются в рамках мажорной версии. Всё, что не является данными (прогресс, предупреждения), идёт в stderr. Числа — байты в виде целых, не строк. Времена — ISO-8601 UTC с `Z`.

*Реализовано в P4:* контракт закреплён тестами на `scan --format json` (версия схемы, байты числами, время с `Z`, `limitations`) и на CSV-кавычки. Писатель — `Utf8JsonWriter` вручную, без сериализатора: рефлексия — это то, из-за чего trimmed-сборка падает в рантайме, а не на билде (§18).

---

## 14. TUI

### 14.1. Пять экранов вместо одиннадцати

v2 описывала 11 экранов — это месяцы работы и размазанная ценность. Оставлены те, что несут её:

| # | Экран | Зачем |
|---|---|---|
| 1 | **Overview** | Тома, свободно/занято, unaccounted, карантин, последний скан, ссылки на остальное |
| 2 | **Tree** | **Главный экран.** Навигация по дереву сверху вниз, как `ncdu`. 80% ценности продукта. |
| 3 | **Audit** | Findings из §6 с командами устранения |
| 4 | **Reclaim** | Рекомендации §7, группировка по правилу, массовое выделение |
| 5 | **Duplicates** | Группы, выбор оригинала |

Переключение — `1`…`5`. Модальные: `Details`, `Confirm delete`, `Search`, `Help`, `Sort`.

*Реализовано в P5:* экраны 1 и 2 и модальные `Details`, `Search`, `Help`, `Sort`, плюс общий `Confirm` (пока его единственный потребитель — открытие файла, но он же обслужит удаление в P6). Клавиша `3` открывает построчный экран audit из P2 — тот же вывод, что у `pathmemo audit`, показанный в обычной консоли; `4` и `5` честно говорят, в какой фазе появятся. Диалог подтверждения удаления и сам экран Reclaim — P6 и P7.

Экраны **Settings** (редактор JSON в терминале — дни работы, нулевая ценность; вместо него `c` = открыть конфиг во внешнем редакторе + `F5` перечитать), **Errors** (счётчик на Overview + `pathmemo errors` в CLI), **History/Diff** (в CLI; на Overview — спарклайн занятого места) в TUI не выносятся.

### 14.2. Главный экран — Tree

```
 pathmemo   C:\Users\me\AppData\Local                      unique  ·  scan 43  ·  03:00
 ─────────────────────────────────────────────────────────────────────────────────────
  Total 84.2 GB                                             ../  C:\Users\me
 ─────────────────────────────────────────────────────────────────────────────────────
   44.1 GB  ████████████████████░░░░░░  52.4%  Packages/                    12,481
   21.9 GB  ██████████░░░░░░░░░░░░░░░░  26.0%  Docker/                       8,102
    6.2 GB  ███░░░░░░░░░░░░░░░░░░░░░░░   7.4%  Temp/                        41,209
    4.1 GB  ██░░░░░░░░░░░░░░░░░░░░░░░░   4.9%  Google/                      19,884
    2.8 GB  █░░░░░░░░░░░░░░░░░░░░░░░░░   3.3%  Programs/                     4,102
    1.9 GB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   2.3%  npm-cache/          redownload 9,441
    1.2 GB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   1.4%  CrashDumps/              safe     14
▸ 940.2 MB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   1.1%  NVIDIA/                  safe  2,014
   512.0 MB ░░░░░░░░░░░░░░░░░░░░░░░░░░   0.6%  thumbcache_1024.db  link  sparse
 ─────────────────────────────────────────────────────────────────────────────────────
 j/k move  l/Enter in  h out  y copy  e explorer  x mark  d delete  / search  ? help
```

- Бар и процент — от размера текущего каталога.
- Бейджи справа: правило reclaim (`safe`, `redownload`), `link` (hardlink alias), `sparse`, `cloud`, `reparse`.
- Каталоги и файлы в одном списке, отсортированы по размеру. `Tab` — только каталоги / только файлы / всё.
- Виртуализация обязательна: рендерим только видимые строки; каталог с 200 тыс. детей не должен тормозить.

*Реализовано в P5:* всё перечисленное, кроме бейджей reclaim — они появятся с правилами в P7; сейчас показываются `link`, `sparse`, `cloud`, `reparse`, `self` и `partial` (каталог, который не удалось дочитать). Переключение «каталоги / файлы / всё» повешено на `t`, а не на `Tab`: `Tab` в терминале — это переход фокуса, и хост его перехватывает. Ширина колонки бейджей считается по видимым строкам: там, где ссылок и sparse нет, все колонки достаются именам.

### 14.3. Горячие клавиши

Раскладка в духе `ncdu` / `lazygit`: **одиночные символы**, работают везде.

```
NAVIGATION                       ACTIONS
  j / ↓        down                y      copy path
  k / ↑        up                  Y      copy full details
  l / → / ⏎    enter dir           e      reveal in Explorer
  h / ←        parent dir          o      open file (confirm required)
  g / G        top / bottom        x / ␣  mark
  Ctrl+D/U     page down/up        a      mark all in view
  ~            volume root         X      clear marks
  1 … 5        switch screen       d      delete marked (or current)
                                   Shift+D  delete permanently
VIEW                               K      add to keep-list
  s            sort menu           i / ⏎  details (on file)
  m            size mode           r      recompute (dupes/audit)
  t            filter dirs/files
  /            search              MISC
  n / N        next / prev match   ?      help
  F5           rescan              c      open config in editor
                                   q/Esc  back
                                   Q      quit
  Ctrl+C       cancel / quit  (standard behaviour, NOT hijacked)
```

**Что исправлено относительно v2:**

| v2 | Проблема | v3 |
|---|---|---|
| `Ctrl+C` = «копировать путь» | В терминале это SIGINT. Пользователь с зависшим сканом сетевого диска не сможет выйти. Плюс Windows Terminal сам перехватывает `Ctrl+C` при наличии выделения — событие до приложения не дойдёт. | `y` = copy (как `yank` в vim). `Ctrl+C` = стандартная отмена/выход. |
| `Ctrl+Shift+C` | **Перехватывается терминалом** (Windows Terminal, VS Code, ConEmu). Приложение не получит событие никогда. | `Y` |
| `Ctrl+1..6` = сортировка | `Ctrl`+цифра не имеет VT-последовательности, в cmd.exe не передаётся. | `s` → меню сортировки |
| `Ctrl+/` | Передаётся как `0x1F` не везде, в cmd.exe отсутствует. | `?` |
| `Ctrl+D` = удалить | `Ctrl+D` — это EOF и «page down» в большинстве TUI. Опасный биндинг для деструктивной операции. | `d` + обязательный диалог; `Ctrl+D` = page down |
| `F10` = выход | Перехватывается conhost как меню; в tmux/screen F-клавиши уходят мультиплексору. | `Q`; F-клавиши только как дублёры |

### 14.4. Отрисовка недоверенных имён

Имена файлов — **вход от недоверенного источника**. Перед отрисовкой:

1. **Bidi-override вырезается:** `U+202A..U+202E`, `U+2066..U+2069`, `U+200E/200F`. Иначе файл `annexe[U+202E]txt.exe` отображается как `annexe.txt` — классический спуфинг, а мы бы показывали его пользователю, у которого рядом клавиша «открыть».
2. **Control-символы** `U+0000..U+001F`, `U+007F` → `·`. WSL и Samba создают такие имена, и они ломают вёрстку ANSI.
3. **Ширина считается по East Asian Width + Emoji**, не по `string.Length`: CJK и эмодзи занимают 2 колонки. Без этого таблица разъезжается на первом же китайском имени файла или названии с эмодзи.
4. **Сокращение по середине**, а не по концу: `C:\Users\me\…\node_modules\.bin` информативнее, чем `C:\Users\me\projects\bigap…`.
5. **Zero-width** (`U+200B..U+200D`, `U+FEFF`) вырезается.

*Реализовано в P5:* `Sanitizer` (пункты 1, 2, 5) и `TextWidth` (пункт 3) — чистые функции над строками, проверенные юнитами, включая тот самый `annexe\u202Etxt.exe`. Ширина считается по таблице диапазонов East Asian Width W/F плюс эмодзи-блоки, с бинарным поиском; комбинирующие знаки и `U+FE0F` дают ноль. ZWJ-последовательности (эмодзи-семья) меряются по частям: ligature'ит ли их терминал, отсюда не узнать — Windows Terminal да, conhost нет, и выбор в пользу conhost сохраняет выравнивание там, где оно ломается заметнее. На экран уходит санитизированное имя, в буфер обмена и в Проводник — настоящее.

### 14.5. Размер терминала и ресайз

- Минимум **80×24** (не 100×30 из v2 — 80 колонок это дефолт половины окон).
- Колонки адаптивные: при < 100 колонок скрываются бар и счётчик файлов, остаётся размер + процент + имя.
- `SIGWINCH`-эквивалент (`Console.WindowWidth` polling раз в 200 мс или `ReadConsoleInput` `WINDOW_BUFFER_SIZE_EVENT`) → перерисовка.
- При выводе в не-TTY (`Console.IsOutputRedirected`) TUI не запускается вообще; `pathmemo` без аргументов работает как `pathmemo status`.

### 14.6. Логи и TUI

Логи **только в файл**, никогда в stdout/stderr при активном TUI. При `--json` — данные в stdout, диагностика в stderr, TUI отключён.

### 14.7. Как это реализовано (P5)

**Рендерер — кадровый буфер со строчным diff'ом.** `Screen` держит две копии кадра и отправляет в терминал только изменившиеся строки: `ESC[y;1H`, очистка строки, содержимое. Перерисовка всего экрана на каждое нажатие — это мерцание в conhost и неработоспособность через SSH. Диффом по строкам, а не по ячейкам: побайтовый рендер требует буфера атрибутов и склейки прогонов ради выигрыша, которого на 24 строках никто не увидит.

**Порядок «сначала стереть, потом писать» — не косметика.** Первая версия писала строку и добавляла `ESC[K` после неё; на живом терминале у каждой строки, заполненной до правого края, пропадал последний символ. Причина: после записи в последнюю колонку курсор остаётся на ней в состоянии отложенного переноса, и очистка до конца строки стирает только что написанную ячейку. Найдено скриншотом настоящего окна (`937,09` вместо `937,094`), закрыто тестом на точную последовательность.

`Line` — строитель строки, который обрезает по **колонкам**, а не по символам, поэтому ни один вызывающий код не может разъехать таблицу. Выделенная строка рисуется инверсией (`ESC[7m`) на всю ширину: инверсия — не цвет, поэтому она работает и при `NO_COLOR`, который отключает только палитру.

**Ввод — `Console.ReadKey(intercept: true)` в опросе, а не `ReadConsoleInputW`.** BCL уже разбирает и VT-последовательности Windows Terminal, и virtual-key-записи conhost в один `ConsoleKeyInfo`; переписывать это — неделя краевых случаев (§18.1 допускает оба пути). Опрос `KeyAvailable` короткими срезами нужен потому, что цикл обязан ещё и замечать ресайз. `TreatControlCAsInput` не трогается вовсе — Ctrl+C остаётся стандартной отменой и приходит через `CancelKeyPress` (§14.3).

**Терминал возвращается в исходное состояние тремя путями:** `finally` в хосте, `ProcessExit` (второй Ctrl+C, падение) и явный `Restore`. Альтернативный буфер (`ESC[?1049h`) — это то, что сохраняет пользователю его скроллбэк: дерево не попадает в историю, а выход возвращает оболочку ровно такой, какой она была.

**Всё, что печатает, выходит из альтернативного буфера.** Скан по `F5`, экран audit, открытие конфига — хост отдаёт терминал, выполняет работу в обычной консоли с её прогрессом и вопросами (включая предложение перезапуска с правами), ждёт Enter и возвращается. Это и есть способ выполнить правило §14.6 без протаскивания логгера через каждый вызов. После возврата снимок и сводка перечитываются: пока нас не было, мог появиться новый скан.

**Экран Tree не хранит стек уровней.** Состояние — каталог, выделение и смещение прокрутки; подъём вверх пересобирает список родителя и ставит курсор на тот узел, из которого вышли. Кэш списков по уровням пришлось бы инвалидировать при смене сортировки, фильтра и режима размеров — именно там дерево начинает показывать вчерашний порядок. Пересортировка 26 тыс. детей стоит единицы миллисекунд, так что платить за это нечем.

**Поиск идёт по всему снимку, а не по текущему каталогу** — иначе он бесполезен: искомое лежит на пять уровней ниже. Имена декодируются в стековый буфер (`Encoding.UTF8.GetChars` в `stackalloc`), а не материализуются строками: 1.58 млн коротких строк стоили бы больше в сборках мусора, чем сам поиск (§17.3). До 2000 совпадений, отсортированных по размеру; `n`/`N` прыгают по ним, попутно снимая фильтр, если он прячет найденное.

**Пометки (`x`, `a`, `X`) реализованы, удаление — нет.** `d` и `K` называют фазу, в которой появятся (P6 и P7), вместо того чтобы молча ничего не делать. Пометки хранятся индексами узлов и сбрасываются при любой смене снимка: индекс, указывающий в другое дерево, — худший вид ошибки для списка на удаление.

**`o` отказывается запускать исполняемые файлы** (§15.3) — до всякого диалога, а не «с предупреждением». Для остального показывается подтверждение с санитизированным именем, реальным расширением и пометкой mark-of-the-web. Подтверждает `y`, а **не** Enter: диалог, возникающий под пальцем, который уже летит к Enter, — это не подтверждение. То же правило унаследует диалог удаления в P6. `e` открывает Проводник через `SHParseDisplayName` + `SHOpenFolderAndSelectItems` (§15.2), без командной строки.

**Overview и `pathmemo status` собирают одни и те же данные** (`StatusReport`): свободное место берётся у тома и всегда актуально, всё остальное — из последнего скана и потому датировано, поэтому каждое сохранённое число на экране несёт номер скана. Занятая база стоит блока истории и ничего больше. Спарклайн масштабируется между минимумом и максимумом, а не от нуля: диск, прошедший путь с 401 до 409 ГБ, иначе рисовался бы плоской линией — ровно то, чего строка не должна допускать.

**Отклонения от буквы §14:** переключение фильтра на `t` вместо `Tab`; экран audit (`3`) — построчный из P2, а не новый; `Enter` в диалогах не подтверждает; бейджи reclaim ждут P7.

**Проверено вживую** (conhost, 118×30): переключение экранов, спуск, help, поиск («402 совпадения по 1.58 млн узлов»), Details, выход по `Q`; Ctrl+C внутри TUI возвращает оболочку с нетронутым скроллбэком и работающей командной строкой; окно, уменьшенное до 60×17, показывает `pathmemo needs 80x24; this window is 60x17`, а после увеличения кадр перерисовывается целиком.

---

## 15. Интеграция с ОС

### 15.1. Буфер обмена

Три пути, в порядке попытки:

1. **P/Invoke** `OpenClipboard` / `EmptyClipboard` / `SetClipboardData(CF_UNICODETEXT)` на выделенном STA-потоке. ~40 строк, нулевые зависимости.
2. **OSC 52** (`ESC ] 52 ; c ; <base64> BEL`) — работает в Windows Terminal, WezTerm, kitty и, главное, **через SSH**. Включается, если `WT_SESSION`/`TERM_PROGRAM` известны или явно `--clipboard osc52`.
3. Отказ с сообщением и выводом пути в stdout, чтобы пользователь скопировал сам.

**`System.Windows.Forms.Clipboard` отвергнут:** требует `<UseWindowsForms>true</UseWindowsForms>`, +10 МБ к бинарнику, STA + работающий message pump в консольном приложении. `clip.exe` отвергнут как основной путь: кодировка через пайп капризна (нужен UTF-16LE с BOM), плюс лишний процесс.

### 15.2. Показать в Проводнике

**Не через командную строку.** `Process.Start("explorer.exe", $"/select,\"{path}\"")` уязвим: путь с кавычкой ломает парсинг, а такие имена существуют (созданы WSL/Cygwin/Samba через `\\?\`), и `ArgumentList` тут не помогает — `explorer /select` требует именно склеенную форму.

```
SHParseDisplayName(path, null, out pidl, 0, out _)
SHOpenFolderAndSelectItems(parentPidl, 1, &childPidl, 0)
```
Без командной строки, без лишнего процесса, быстрее.

### 15.3. Открыть файл — под подтверждением

`Process.Start(UseShellExecute = true)` на произвольном найденном файле = **запуск недоверенного кода одной клавишей**. Инструмент сканирует весь диск, включая `Downloads`, где лежат `invoice.pdf.exe`, `photo.scr`, `update.hta`.

Правила:
- Клавиша `o`, **не** соседняя с деструктивными.
- Расширение в списке исполняемых (`exe com scr bat cmd ps1 psm1 vbs vbe js jse wsf wsh hta msi msp msc reg lnk url jar appx cpl pif inf`) → **отказ** с текстом:
  `"Refusing to launch executable files. Press e to open the containing folder instead."`
- Остальное — подтверждение с показом **санитизированного** имени, реального расширения и наличия Mark-of-the-Web (`Zone.Identifier` ADS → «downloaded from the internet»).
- `ui.confirmOpenExecutables: false` не снимает запрет на исполняемые, только убирает диалог для документов.

### 15.4. Внешние утилиты

Вызываются только для **чтения** (§6.3):
- полный путь из `%WINDIR%\System32\...` — защита от PATH hijacking;
- `ProcessStartInfo.ArgumentList` (никогда не склеенная строка), `UseShellExecute = false`, `CreateNoWindow = true`;
- таймаут 30 с + kill process tree;
- `Environment["__COMPAT_LAYER"]` не наследуется; рабочий каталог — `%WINDIR%\System32`.

### 15.5. Long paths

- Манифест: `<longPathAware>true</longPathAware>`.
- `AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false)`.
- Все Win32-вызовы — с `\\?\`-префиксом при длине > 250.
- MFT-сканер лимита путей **не имеет** вовсе: он не строит строки при обходе.

---

## 16. Модель угроз и защитные меры

Сводная таблица. Каждая строка — реальный сценарий, не теоретический.

| # | Угроза | Механизм | Мера |
|---|---|---|---|
| T1 | Удаление системных файлов | Строковый защитный список обходится 12 способами (регистр, 8.3, `\\?\`, UNC `c$`, junction, mount point, `..`), плюс `C:\` захардкожен | Канонизация через `GetFinalPathNameByHandle(VOLUME_NAME_GUID)` + `SHGetKnownFolderPath` (§9.3) |
| T2 | Выход рекурсивного удаления за пределы дерева | Подмена подкаталога на junction между проверкой и удалением (TOCTOU) | Обход по handle с `RootDirectory` + `FILE_OPEN_REPARSE_POINT` (§9.5) |
| T3 | Удаление не того, что показали | Между сканом и удалением прошли часы | Повторная проверка size+mtime на том же handle; расхождение → отказ (§9.3 шаг 5) |
| T4 | Запуск малвари | «Открыть файл» на `invoice.pdf.exe` из `Downloads` | Запрет исполняемых расширений, подтверждение для остальных, MotW-предупреждение (§15.3) |
| T5 | Спуфинг имени файла | RTL-override `U+202E` в имени | Вырезание bidi/control/zero-width при отрисовке (§14.4) |
| T6 | Инъекция в командную строку | Кавычка в пути → `explorer.exe /select,"..."` | `SHOpenFolderAndSelectItems`, командная строка не строится (§15.2) |
| T7 | Локальное повышение привилегий | Конфиг в `%LOCALAPPDATA%` (user-writable) читается elevated-процессом; `logFilePath` → произвольная запись от админа | Elevated-режим не берёт пути из конфига; проверка DACL; нет ключей «выполнить команду» (§12.1) |
| T8 | PATH hijacking | `vssadmin`/`dism` подменены в `PATH` | Абсолютные пути из `%WINDIR%\System32`, `UseShellExecute=false` (§15.4) |
| T9 | ReDoS | Regex из конфига + миллион глубоких путей = катастрофический бэктрекинг, скан вешается | Глобы по умолчанию; regex только с `NonBacktracking` + 50 мс таймаут (§12.2) |
| T10 | Гидратация облачных файлов | Хеширование placeholder'а OneDrive **скачивает** его: «поиск дубликатов» утягивает 200 ГБ и забивает диск | Проверка `RECALL_ON_*`/`OFFLINE` до открытия + `FILE_FLAG_OPEN_NO_RECALL` (§8.4) |
| T11 | Потеря единственной копии | В группе дубликатов сняты отметки со всех файлов | Инвариант «минимум один остаётся», проверяемый в ядре, не в UI (§8.4) |
| T12 | Тихое безвозвратное удаление | Файл больше квоты корзины → Shell удаляет навсегда молча | Своя проверка квоты (`SHQueryRecycleBin` + реестр) → переключение на Quarantine (§9.6) |
| T13 | Утечка приватных данных | Экспорт/лог содержит полную карту диска: имена проектов, ФИО в документах | `--redact` (хеширование имён с сохранением структуры и размеров); `logging.logFilePaths: false` по умолчанию |
| T14 | Повреждение своей БД | Отключение питания при записи | WAL + `synchronous=NORMAL` + `integrity_check` **только после нечистого выхода** (маркерный файл), а не на каждом старте |
| T15 | Инструмент забивает диск | Retention 30/180/730 дней × 150 МБ снимков = 15+ ГБ | Бинарные снимки 10–25 МБ + жёсткий лимит 400 МБ (§5.4) |
| T16 | Рекурсивный рост | pathmemo сканирует свой каталог данных, растёт, снова сканирует | Каталог помечен `SelfData`, исключён из reclaim, не может быть удалён (кроме `purge`) |
| T17 | Убийство процесса при сохранении | `Console.CancelKeyPress` имеет лимит времени; процесс убивают посередине записи | Обработчик только ставит токен; запись в основном потоке с таймаутом; второй `Ctrl+C` = жёсткий выход (§4.8) |
| T18 | Симлинк-петля | `AppData\Local\Application Data` → сам себя | `ShouldRecurseIntoEntry` запрещает reparse points; дополнительно лимит глубины 512 |
| T19 | Удаление занятых файлов ломает приложение | Удаление активного кэша работающего браузера | Проверка `NumberOfLinks`/sharing; `FILE_DISPOSITION_POSIX_SEMANTICS` для корректного отложенного удаления; предупреждение «close <app> first» для известных правил |
| T20 | Конкурентные процессы pathmemo | Два скана пишут в одну БД | Именованный mutex на каталог данных + `busy_timeout`; второй экземпляр читает, но не пишет, exit `EX_LOCKED` для операций записи. *P4:* реализованы WAL + `busy_timeout 5000` + отказ от записи с предупреждением (снимок сохраняется); mutex не понадобился — WAL сериализует писателей сам, а снимки пишутся под разными номерами |

---

## 17. Архитектура кода

### 17.1. Два проекта, не шесть

```
pathmemo/
├── pathmemo.sln
├── Directory.Build.props
├── src/
│   └── PathMemo/                      ← весь код, разделение папками
│       ├── PathMemo.csproj
│       ├── Program.cs
│       ├── app.manifest               longPathAware, requestedExecutionLevel=asInvoker
│       ├── Resources/app.ico
│       │
│       ├── Cli/
│       │   ├── ArgParse.cs            разборщики значений: размеры, длительности, даты, режимы
│       │   ├── Commands/              ScanCommand, TreeCommand, TopCommand, AuditCommand,
│       │   │                          HistoryCommand, DiffCommand, DoctorCommand,
│       │   │                          StatusCommand (+ StatusReport — общий с Overview)
│       │   ├── Interactive/           Launcher, Browser, AuditView, ElevationPrompt
│       │   │                          (построчный fallback для терминалов без VT)
│       │   └── Output/                SizeFormat, PathDisplay, ScanExport (json/csv)
│       │
│       ├── Tui/
│       │   ├── TuiHost.cs             цикл ввода + рендер, suspend на время скана
│       │   ├── TuiSession.cs          снимок, режим размеров, пометки, ITuiView
│       │   ├── Terminal/              Screen (кадровый буфер + строчный diff), Line,
│       │   │                          KeyReader, VirtualTerminal, TextWidth, Sanitizer, Draw
│       │   ├── Screens/               OverviewScreen, TreeScreen
│       │   │                          (AuditScreen — P2-построчный, Reclaim P7, Duplicates P8)
│       │   └── Dialogs/               DetailsDialog, ConfirmDialog, SortMenu, HelpDialog
│       │                              (поиск — состояние TreeScreen, не отдельный файл)
│       │
│       ├── Scanning/
│       │   ├── IScanner.cs            ← ОДИН из немногих оправданных интерфейсов: 2 реализации
│       │   ├── Mft/MftScanner.cs      дерево из записей $MFT, BFS, владельцы hardlink'ов
│       │   ├── Mft/MftParser.cs       разбор записей $MFT: fixup, атрибуты, data runs
│       │   ├── Mft/MftVolume.cs       \\.\C:, экстенты $MFT из записи 0, позиционное чтение
│       │   ├── WalkScanner.cs
│       │   ├── FastEnumerator.cs
│       │   ├── UsnIncrementalScanner.cs
│       │   ├── DirectoryWorkQueue.cs  work-stealing
│       │   └── MediaTypeDetector.cs   HDD/SSD → parallelism
│       │
│       ├── Snapshots/
│       │   ├── SnapshotBuilder.cs     дерево из результатов walk-обхода
│       │   ├── SnapshotFile.cs        формат .pmsnap: секции, deflate, заголовок
│       │   ├── SnapshotStore.cs       нумерация, retention, поиск
│       │   ├── NodeStore.cs           SoA-массивы
│       │   ├── NameBlob.cs            интернирование сегментов
│       │   └── TreeAssembly.cs        агрегация с учётом hardlink-ссылок, self-data, склейка томов
│       │
│       ├── Analysis/
│       │   ├── TreeQuery.cs           агрегаты, топы, фильтры по снимку
│       │   ├── SnapshotDiff.cs        merge-join двух снимков, «объясняющий» уровень
│       │   ├── FileCategory.cs        восемь корзин: каталог важнее расширения
│       │   ├── ScanAggregates.cs      итоги по категориям и расширениям для БД
│       │   ├── RuleEngine.cs          глобы + матчинг
│       │   ├── ReclaimPlanner.cs
│       │   └── Reconciler.cs          сверка с томом, unaccounted
│       │
│       ├── Audit/
│       │   ├── IAuditProbe.cs         ← оправданный интерфейс: ~20 реализаций
│       │   ├── AuditRunner.cs
│       │   └── Probes/                VssProbe, WinSxSProbe, WslProbe, DockerProbe,
│       │                              HibernationProbe, RecycleBinProbe, DumpsProbe, …
│       │
│       ├── Duplicates/
│       │   ├── DuplicateFinder.cs
│       │   ├── HashPipeline.cs
│       │   ├── ByteComparer.cs
│       │   └── HashCache.cs
│       │
│       ├── Deletion/
│       │   ├── IDeleteBackend.cs      ← оправданный: recycle|quarantine|permanent + no-op для тестов
│       │   ├── PathGuard.cs           КРИТИЧЕСКИЙ: канонизация + protected set
│       │   ├── HandleTreeDeleter.cs   КРИТИЧЕСКИЙ: рекурсия по handle
│       │   ├── QuarantineStore.cs
│       │   ├── RecycleBinBackend.cs   IFileOperation + ProgressSink
│       │   ├── PermanentBackend.cs
│       │   └── FreeSpaceVerifier.cs
│       │
│       ├── Platform/
│       │   ├── Native/                P/Invoke, сгруппированные по DLL
│       │   ├── Clipboard.cs
│       │   ├── ShellReveal.cs         SHParseDisplayName + SHOpenFolderAndSelectItems
│       │   ├── FileLaunch.cs          отказ для исполняемых, mark-of-the-web
│       │   ├── Elevation.cs
│       │   ├── KnownFolders.cs
│       │   ├── VolumeInfo.cs
│       │   └── TaskScheduler.cs
│       │
│       ├── Storage/                   (в v3-спецификации назывался Data/)
│       │   ├── Database.cs            открытие, PRAGMA, миграция через user_version,
│       │   │                          integrity_check после грязного выхода
│       │   ├── Schema.sql             embedded, вся схема §11 одной версией
│       │   ├── ScanCatalog.cs         сверка БД с каталогом снимков, выделение id
│       │   ├── ScanRecords.cs         строки scans / scan_volumes
│       │   ├── ScanRepository.cs      история, тома, агрегаты
│       │   └── AuditRepository.cs     findings с detail_json
│       │
│       └── Config/
│           ├── AppConfig.cs
│           ├── ConfigLoader.cs        + DACL-проверка при elevated
│           └── DefaultRules.cs
└── tests/
    └── PathMemo.Tests/
```

**Почему не шесть проектов:** v2 предлагала `Cli / Core / Data / Platform / Reporting / Tests`. Для инструмента, который пишет один человек и который никогда не будет библиотекой, это трение без выгоды: дольше сборка, `InternalsVisibleTo`-церемонии, DI ради DI, и невозможность просто вызвать функцию из соседней папки. Границы поддерживаются папками и код-ревью. Разделять — когда появится второй консьюмер.

### 17.2. Интерфейсы только там, где есть 2+ реализации

```csharp
// ОПРАВДАНЫ: реальная полиморфия
interface IScanner        { ... }   // MftScanner, WalkScanner, UsnIncrementalScanner
interface IAuditProbe     { ... }   // ~20 проб
interface IDeleteBackend  { ... }   // recycle, quarantine, permanent, no-op (тесты)

// ОТВЕРГНУТЫ: одна реализация, интерфейс — чистый оверхед
// IClipboardService, IShellService, IReportWriter, IScanRepository
// → статические классы / конкретные типы. Тестируются интеграционно.
```

`IScanService.ScanAsync` из v2, возвращавший `long` (id в БД), **разделён**: сканер ничего не знает о персистентности.

```csharp
interface IScanner
{
    ScannerKind Kind { get; }
    bool CanScan(VolumeInfo volume, bool elevated);

    Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress> progress,
        CancellationToken ct);
}

// Персистентность — отдельно и явно
sealed class ScanStore
{
    long Save(ScanResult result);                   // пишет .pmsnap + строки в БД
    SnapshotReader OpenSnapshot(long scanId);
}
```

### 17.3. Правила аллокаций на горячем пути

Требование RSS < 250 МБ при 1 млн файлов достижимо **только** при соблюдении:

- **Никаких классов на файл.** Ни `List<FileEntry>` из ссылочных типов, ни `string` полного пути на запись. Только SoA-массивы (§5.3).
- Имена — в общий `byte[]`-блоб, узел хранит смещение.
- Буферы IO — `ArrayPool<byte>.Shared`, всегда с `try/finally { Return(clearArray: false) }`.
- Перечисление каталогов — `ReadOnlySpan<char>` без материализации.
- Счётчики прогресса — `Interlocked`/`volatile` поля, не события на файл.
- Массивы > 85 КБ идут в LOH → выделяем **один раз** на ожидаемое число узлов (оценка из `FSCTL_GET_NTFS_VOLUME_DATA`), а не растим `List<T>` удвоением. При недооценке — chunked-массивы по 1 млн элементов, не `Array.Resize`.
- `ServerGarbageCollector = false`, `ConcurrentGarbageCollection = false`, `TieredPGO = true` в csproj: для короткоживущего CLI workstation GC даёт меньший RSS.

---

## 18. Технологический стек

| Компонент | Выбор | Почему именно так |
|---|---|---|
| Runtime | **.NET 9** | `FileSystemEnumerator`, `System.IO.Hashing`, `NonBacktracking` regex, лучший single-file |
| CLI-парсер | ~~System.CommandLine~~ → **свой разбор в `Program.cs`** | **Не понадобился.** Около десяти команд с плоскими опциями — это `switch` на 40 строк плюс `ArgParse` для значений; пакет дал бы генерацию help, который у нас написан руками и точнее, и остался бы главным блокером NativeAOT (§19.3) |
| Статичный вывод | ~~Spectre.Console~~ → **свой** | **Не понадобился.** Таблицы у нас — три-четыре формата строк с фиксированными колонками, цвет в статичном выводе не используется вовсе, а прогресс — одна перезаписываемая строка. Пакет добавил бы зависимость и вес ради `string.Format`, который уже написан (`SizeFormat`, `PathDisplay`) |
| TUI | **свой рендерер: кадровый буфер со строчным diff'ом + `Console.ReadKey` в опросе** | см. 18.1 и 14.7. `ReadConsoleInputW` не понадобился: BCL разбирает и VT-последовательности, и virtual-key-записи |
| SQLite | **Microsoft.Data.Sqlite** + ручной маппинг | Dapper — рефлексия, враждебен trimming/AOT; у нас ~15 запросов, ручной `SqliteDataReader` короче и быстрее. **Добавлен в P4 — единственный PackageReference приложения; trimmed single-file вырос с 19.5 до 22.5 МБ и работает** |
| Миграции | **`PRAGMA user_version` + embedded .sql** | DbUp — оверкилл (25 строк своего кода) и ломает trimming |
| Хеш | **System.IO.Hashing (XxHash128)** + `SHA256` из BCL | нулевые нативные зависимости; см. §8.2 |
| Сжатие снимков | **`System.IO.Compression.DeflateStream`** | в коробке; Zstd дал бы на 30% меньше, но это нативная DLL |
| Логи | **свой `FileLogger`** (~80 строк) или **Serilog** | Serilog тянет 4 пакета и рефлексию ради структурных логов, которые мы никуда не отправляем; решение — при реализации Phase 1 |
| Буфер обмена | **P/Invoke + OSC 52** | никаких WinForms (§15.1) |
| Корзина | **`IFileOperation` через ручной COM-интероп** | без `Microsoft.WindowsAPICodePack` |
| Планировщик | **`ITaskService` COM** или `schtasks` с `ArgumentList` | |
| Тесты | **xUnit** + **интеграционные на реальной ФС** | см. §22 |
| Публикация | **self-contained, single-file, trimmed** | §19 |

**Удалено из v2:** Terminal.Gui, Dapper, DbUp, Blake3.NET, System.Windows.Forms, FluentAssertions (xUnit-ассерты достаточны, у FluentAssertions сменилась лицензия).

### 18.1. Почему не Terminal.Gui

- v1 — legacy; v2 долгое время был prerelease с меняющимся API.
- `TableView` ограничен, отрисовка больших списков требует ручной виртуализации всё равно.
- Активная рефлексия → **теряется trimming, а с ним шанс на 20 МБ вместо 70**; NativeAOT недостижим.
- Своя модель фокуса/событий конфликтует с одноэкранным приложением, где нужен полный контроль над рендером дерева.

Для v3 нужен **один** сложный экран (виртуализированное дерево) и 4 простых + модалки. Это 600–900 строк своего рендера (буфер символов + diff-отрисовка + `ReadConsoleInputW` для клавиш и мыши) с полным контролем, нулевой зависимостью и корректной обработкой ширины CJK/эмодзи, которой у Terminal.Gui всё равно нет.

Если через два экрана окажется, что своё писать дороже — Terminal.Gui v2 остаётся планом B; `Tui/Terminal/` спроектирован так, что рендер-бэкенд заменяем.

---

## 19. Формат поставки и сборка

### 19.1. Артефакты

| Файл | Размер | Примечание |
|---|---|---|
| `pathmemo-win-x64.exe` | **16–30 МБ** | основной. Измерено: 16.1 МБ на P0-скелете (77.3 МБ без trimming), 19.5 МБ на P3, 22.5 МБ на P4 (плюс 3 МБ дал `Microsoft.Data.Sqlite` с нативным `e_sqlite3`), **22.8 МБ на P5** — весь TUI поместился в 0.3 МБ, потому что зависимостей у него нет. |
| `pathmemo-win-arm64.exe` | 16–30 МБ | отдельный бинарник |
| `pathmemo-win-x64.zip` | | exe + LICENSE + README |

«Один exe, x64 и arm64» из v2 — противоречие: это **два** файла. Формулировка: «один файл на архитектуру, без установки, без зависимостей».

### 19.2. csproj

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <OutputType>Exe</OutputType>
  <Nullable>enable</Nullable>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <InvariantGlobalization>true</InvariantGlobalization>
  <ApplicationManifest>app.manifest</ApplicationManifest>
  <ApplicationIcon>Resources\app.ico</ApplicationIcon>
  <ServerGarbageCollection>false</ServerGarbageCollection>
  <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
  <TieredPGO>true</TieredPGO>
  <EnableWindowsTargeting>true</EnableWindowsTargeting>
</PropertyGroup>
```

```bash
dotnet publish src/PathMemo/PathMemo.csproj \
  -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -p:TrimMode=partial \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false \
  -p:PublishReadyToRun=true \
  -p:DebugType=embedded \
  -o dist/win-x64
```

**`EnableCompressionInSingleFile=false` — сознательно.** Сжатие даёт −40% размера, но добавляет 200–400 мс на **каждый** запуск (декомпрессия), а pathmemo — инструмент, который дёргают из консоли десятки раз. 28 МБ без сжатия лучше 17 МБ с задержкой.

`e_sqlite3.dll` — единственная нативная зависимость, вызывающая самораспаковку в `%TEMP%` при первом запуске каждой версии (~150 мс однократно). Приемлемо. Альтернатива на будущее — статическая линковка SQLite или `PublishAot`.

*Измерено в P4:* распаковка идёт в `%TEMP%\.net\pathmemo\<hash>\e_sqlite3.dll` (1.72 МБ) и стоит **15 мс однократно**, не 150: `pathmemo --version` — 77 мс на первом запуске после удаления кэша и 62 мс на последующих. Оценка была пессимистичной в нашу пользу.

### 19.3. NativeAOT — цель Phase 3

С устранением Dapper/DbUp/Terminal.Gui/Blake3 путь к AOT открыт: `PublishAot=true` даст **~12 МБ и старт за 15 мс** вместо 120. Блокеры на момент v3: `System.CommandLine` (частично AOT-совместим), COM-интероп `IFileOperation` (нужны source-generated wrappers `[GeneratedComInterface]`). Обе задачи решаемы, но не в MVP.

*Состояние на P5:* блокер `System.CommandLine` снят — его никогда и не добавили, разбор аргументов свой. TUI тоже написан без единой зависимости и на AOT переносится как есть. Остаётся `Microsoft.Data.Sqlite` (AOT-совместим с `SQLitePCLRaw`, требует проверки) и COM-интероп, который появится с удалением в P6.

---

## 20. Бюджеты производительности

Измеряется на: Ryzen 7, 32 ГБ, NVMe, Windows 11, 1.2 млн файлов / 420 ГБ на C:, Defender включён.

| Операция | Бюджет | Комментарий |
|---|---|---|
| Холодный старт до первого кадра | **< 250 мс** | single-file без сжатия, R2R; **измерено 62 мс** на `--version`, 77 мс при распаковке `e_sqlite3` (однократно на версию) |
| MFT-скан C:, 1.2 млн записей | **< 10 с** | **измерено 9.1–9.6 с** (1.79 млн записей `$MFT`, 1.27 млн файлов / 399k каталогов / 209 ГБ, 175–184k записей/с, NVMe, Defender включён); целевое 5 с — резерв в параллельном разборе кусков |
| Walk-скан C:, 1.2 млн файлов | < 150 с | **измерено 40–51 с** (P4, разброс от состояния кэша ФС) с hardlink-дедупликацией для файлов ≥ 1 МБ (16 тыс. handle'ов ≈ 3 с); 41 с без неё на P3. Тот же прогон по C: и D: вместе — 932-гигабайтный HDD добавляет к этому больше пятнадцати минут, поэтому все цифры здесь про C: |
| Инкрементальный USN-скан | **< 2 с** | обычный случай — 0.3 с |
| Запись снимка на диск | < 1.5 с | **измерено: 26 МБ на 1.58 млн узлов** |
| Открытие существующего снимка | **< 300 мс** | **измерено ~250 мс** (полная распаковка; ленивая загрузка секций ещё не включена) |
| Навигация в дереве (кадр) | **< 16 мс** | 60 FPS, виртуализация. **Измерено 0.08 мс** в обычном каталоге и **0.6 мс** при прокрутке каталога на 26 тыс. записей, где меняется каждая строка (рендер 0.40 мс + запись в терминал 0.19 мс). Первый кадр после загрузки снимка — 4–7 мс |
| Вход в каталог с 200 тыс. детей | < 50 мс | дети непрерывны в массиве; платится только сортировка. **Измерено 10–15 мс** на самом широком каталоге настоящего диска — `C:\Windows\WinSxS\Manifests`, 26 205 записей; на синтетическом каталоге в 200 тыс. записей — в пределах того же бюджета |
| Поиск по имени во всём снимке | — | **измерено 56–69 мс** на 1.58 млн узлов (декодирование имён в стековый буфер, без строки на узел); 402 совпадения по `node_modules` |
| Сбор данных для Overview / `status` | — | **измерено 48–70 мс**: тома, последний скан, ряд «занято» по 40 сканам |
| Space Audit (все пробы) | < 8 с | **измерено 4.1 с** без прав администратора (21 проба, 175 тыс. файлов в temp); elevated добавляет DISM, 3–6 с |
| Diff двух снимков | < 500 мс | **измерено 105 мс** сам merge-join на паре снимков 1.58 и 1.67 млн узлов (walk против MFT, 1.4 тыс. изменившихся путей) и 5 мс на паре, где изменился один каталог; плюс 405 мс на загрузку обоих снимков — полная команда 0.66–0.77 с |
| Импорт снимков в новую БД | — | **измерено 1.6 с** на 4 снимка (106 МБ, 6.5 млн узлов): случай «БД удалили, снимки остались» |
| Агрегаты по категориям и расширениям | < 500 мс | **измерено 330 мс** на 1.58 млн узлов. Первая версия давала 600 мс и +80 МБ пика: линейный поиск по 200 расширениям на каждый файл и строка на имя. Стало — хеш-поиск по спану и стековые буферы (§17.3) |
| RSS при MFT-скане 1.2 млн | **< 250 МБ** | SoA-массивы обязательны; elevated-замер пика ещё не снят (скан запускается в отдельном окне) |
| RSS при walk-скане 1.2 млн | < 400 МБ | **измерено 373 МБ** пик working set (P4; 380 МБ на P3 без агрегатов, 478 МБ с их первой версией, 535 МБ до выноса file id из `RawEntry` в боковой список). Живой снимок при этом 94 МБ (62 байта/узел) — остальное транзиентный GC-хип фазы обхода. |
| RSS в TUI со загруженным снимком | < 150 МБ | **не выполняется: измерено 182–201 МБ** пикового working set процесса с открытым снимком на 1.58 млн узлов. Само дерево — 94 МБ (SoA-массивы плюс блоб имён); ещё столько же даёт распаковка: `SnapshotFile.Read` материализует каждую секцию в `byte[]` целиком, прежде чем скопировать в массивы. Лечится ленивой загрузкой секций (см. строку «Открытие существующего снимка»), это работа P10. Ранняя оценка 103 МБ снималась на снимке вдвое меньше и без учёта транзиента |
| Размер каталога данных | **< 500 МБ всегда** | жёсткий лимит |
| Поиск дубликатов, 200 ГБ кандидатов | IO-bound | ~стоимость чтения 200 ГБ |

---

## 21. Критерии приёмки

### Корректность цифр
- [x] `Сумма скана + audit findings + метаданные + unaccounted` = `used bytes` тома, при `unaccounted` < 2% на тестовой машине — **MFT-скан: 209 ГБ из 212 занятых, unaccounted 3.06 ГБ (1.4 %)**; walk без прав: 7.5 %.
- [ ] Размер `C:\Windows` совпадает с WizTree (режим allocated, с hardlink-дедупликацией) **с точностью 1%** — сверка с WizTree ещё не проводилась; MFT даёт 42.3 ГБ (WinSxS 6.94 ГБ) против 43.4 ГБ (WinSxS 8.47 ГБ) у walk.
- [ ] Размер `C:\` совпадает с WizTree с точностью 1%.
- [ ] Sparse `ext4.vhdx` показан по allocated, не по logical, с бейджем `sparse`.
- [ ] Cloud-only папка OneDrive показана с размером ~0 и бейджем `cloud`, при этом её logical виден в деталях.
- [ ] Переключение `unique`/`allocated`/`logical` меняет цифры предсказуемо и объяснимо.

### Сканирование
- [x] MFT-скан 1 млн записей завершается за < 10 с — 1.79 млн записей за 9.6 с.
- [x] Без прав админа предлагается релонч; при отказе скан идёт в degraded с явным предупреждением — `[R]/[C]/[Q]` только при TTY, в скриптах предупреждение в stderr и degraded; `--quiet` пропускает вопрос.
- [x] На exFAT/FAT32/сетевом диске автоматически используется Walk-сканер — выбор по тому, деревья склеиваются.
- [ ] Повторный скан того же тома использует USN и завершается за < 2 с.
- [ ] `Ctrl+C` во время скана прерывает, сохраняет частичный результат со статусом `cancelled`, второй `Ctrl+C` выходит немедленно.
- [ ] Junction/symlink не вызывает рекурсию; точка видна в дереве с бейджем `reparse` и размером 0.
- [x] Недоступные пути попадают в `scan_errors` с группировкой; в MFT-режиме их размер известен и показан — MFT видит на 53 тыс. файлов и 38 тыс. каталогов больше, чем walk без прав, ошибок доступа нет по построению.
- [ ] Каталог данных pathmemo виден в дереве с бейджем `self`.

### Space Audit
- [ ] Обнаруживается и оценивается: VSS, WinSxS, hiberfil, корзина, WSL vhdx, Docker vhdx, Windows Update cache, dumps, Windows.old (если есть).
- [ ] Для каждого finding показана команда устранения с пометкой `[admin]` / `[reboot]`, копируемая в буфер.
- [ ] Ни одна проба не изменяет систему.
- [ ] Неудачный парсинг вывода `DISM` даёт `status=unknown`, а не `0 bytes`.

### Удаление
- [ ] Ни один путь из protected set не удаляется, включая варианты `c:\windows`, `C:\WINDOW~1`, `\\?\C:\Windows`, `\\localhost\c$\Windows`, `C:\Users\All Users\...`, `C:\Users\..\Windows`.
- [ ] Подмена подкаталога на junction во время рекурсивного удаления не приводит к удалению вне дерева (тест с гонкой).
- [ ] Изменение файла между сканом и удалением приводит к отказу операции.
- [ ] Карантин: перемещение 18 ГБ занимает < 3 с (переименование, не копирование).
- [ ] `pathmemo restore <op>` полностью восстанавливает дерево на исходные пути.
- [ ] `pathmemo purge` освобождает место; фактическая дельта свободного места совпадает с предсказанной ±10%.
- [ ] Попытка карантина на другом томе отклоняется с понятным сообщением.
- [ ] Файл больше квоты корзины не попадает в режим `recycle` (переключение на quarantine), не удаляется молча.
- [ ] `--dry-run` не создаёт и не перемещает ничего; пишется в `dryrun_log`, не в `delete_ops`.
- [ ] Permanent-удаление > 1 ГБ требует типизированного подтверждения; `--yes` его не обходит.
- [ ] Каждая операция имеет запись в журнале с per-item результатом.

### Дубликаты
- [ ] Hardlink-набор показан отдельно от дубликатов, с экономией 0.
- [ ] Cloud-only файлы не хешируются (проверяется отсутствием сетевого трафика).
- [ ] Невозможно снять отметку с последнего файла в группе (UI и CLI).
- [ ] Byte-for-byte верификация выполняется перед удалением; подмена файла между хешем и удалением отменяет операцию.
- [ ] Дубликаты находятся между C: и D:.

### CLI
- [x] Перенаправление stdout отключает TUI автоматически — при перенаправлении без аргументов печатается `status`.
- [x] `--json` даёт валидный JSON в stdout и ничего кроме; прогресс/предупреждения в stderr — `scan`, `diff`, `history`, `audit`; тесты на контракт §13.6.
- [ ] Exit-коды соответствуют §13.5; `EX_UNSAFE` на попытке удалить protected path — `EX_LOCKED`, `EX_NO_DATA`, `EX_USAGE`, `EX_PARTIAL`, `EX_CANCELLED` работают; `EX_UNSAFE` появится с удалением (P6).
- [ ] `pathmemo top --paths-only | pathmemo rm --from-stdin --dry-run` работает как пайп.
- [ ] `pathmemo doctor` сообщает: elevated, ФС томов, состояние USN, целостность БД, версию, свободное место — всё, кроме состояния USN (P9): версия схемы, размер БД, число сканов, результат сверки со снимками, `integrity_check` после грязного выхода.

### TUI
- [x] Работает в Windows Terminal, conhost (cmd.exe), ConEmu, VS Code terminal, PowerShell ISE-эквиваленте — проверено в conhost (запуск из Проводника и из `cmd.exe`); остальные хосты получают тот же ANSI-поток, а хост без VT-обработки откатывается на построчное меню.
- [x] `Ctrl+C` работает как стандартная отмена/выход, не как копирование — проверено внутри `cmd.exe`: TUI выходит, скроллбэк цел, следующая команда выполняется.
- [x] Все действия достижимы одиночными клавишами; ни одно не требует `Ctrl+Shift+*` или `Ctrl+digit` — единственные сочетания с Ctrl это `Ctrl+D`/`Ctrl+U` (страница), у них есть дублёры PageDown/PageUp.
- [x] Терминал 80×24 полностью пригоден; при меньшем размере — сообщение, а не поломанный вывод — на 80 колонках скрываются бар и счётчик файлов; на 60×17 печатается размер окна и что делать.
- [x] Ресайз окна перерисовывает корректно — опрос размера в цикле кадров, при изменении весь кадр помечается грязным.
- [x] Имя файла с `U+202E` отображается без переворота текста.
- [x] Имя файла из CJK/эмодзи не ломает выравнивание таблицы — тест утверждает, что ни одна строка кадра не шире экрана в колонках.
- [x] Каталог с 200 тыс. детей открывается за < 50 мс и скроллится плавно — 10–15 мс на 26 тыс. записей настоящего диска, 0.6 мс на кадр при прокрутке.
- [x] Логи не попадают в stdout при активном TUI — всё, что печатает (скан, audit), выходит из альтернативного буфера; сообщение Ctrl+C из `Main` при активном TUI подавлено.
- [x] `o` отказывается открывать `.exe`/`.lnk`/`.hta` — отказ до диалога, список из §15.3, тест на `invoice.pdf.exe`.

### Гигиена
- [ ] Каталог данных никогда не превышает 500 МБ (тест: 100 сканов подряд) — правило retention реализовано (§5.4), прогон на 100 сканов не делался.
- [x] Два экземпляра pathmemo одновременно: второй читает, отказывается писать с `EX_LOCKED` — проверено на настоящей БД с удержанным `BEGIN EXCLUSIVE`: в WAL-режиме чтение вообще не блокируется, а запись отказывается с одним предупреждением, **сохраняя снимок**; `EX_LOCKED` возвращают команды, которым БД обязательна. Терять 10-секундный скан из-за занятой строки истории было бы хуже (§11.1).
- [x] Некорректное завершение → на следующем старте `integrity_check`, при чистом — не запускается — маркер `.clean`, юнит-тест на оба пути.
- [x] Повреждённый `.pmsnap` не ломает приложение: скан помечается `snapshot_available=0` — заголовок проверяется при каждой сверке; `history` печатает `deleted`, `diff` отказывается с объяснением.
- [ ] Повреждённый `config.json` → предупреждение + дефолты, не крэш.
- [ ] Elevated-запуск игнорирует пути из пользовательского конфига — для `--data-dir` реализовано (предупреждение + игнорирование); `config.json` ещё не читается.

---

## 22. Тестирование

### 22.1. Что тестируем юнитами

Только чистую логику без ФС:
- `RuleEngine` — матчинг глобов, раскрытие переменных, приоритет `keep`.
- `PathGuard.IsProtected` — на **заранее канонизированных** строках, все 12 вариантов обхода.
- `NodeStore` / `SnapshotFormat` — round-trip, границы, повреждённые данные.
- `DiffEngine` — на синтетических снимках.
- `HardlinkResolver` — на синтетических наборах.
- *P4:* `SnapshotDiff` — 13 тестов на синтетических деревьях (`TestTree` строит `NodeStore` из списка путей): «объясняющий» уровень, разделение на несколько детей, residual, порог, appeared/disappeared, смена типа имени, hardlink-алиасы, сумма строк плюс остаток = изменению тома. `ScanAggregates` — наследование категории от каталога, срез расширений. `ScanExport` — контракт JSON и кавычки CSV. Хранилище (`Storage`) тестируется на настоящем файле SQLite в `%TEMP%`, а не in-memory: WAL, маркер `.clean` и сверка «файл против строки» существуют только на диске — это и есть предмет тестов. Набор прогоняется последовательно (`DisableTestParallelization`): каталог данных — один статический путь на процесс.
- Парсеры вывода `DISM`/`vssadmin` — на зафиксированных примерах, включая не-английскую локаль.
- Форматирование размеров, обрезка путей, расчёт ширины CJK/эмодзи, санитизация bidi.
- *P5:* терминальный слой — 15 тестов: ширина CJK/эмодзи/комбинирующих знаков, `Fit` ровно в N колонок, санитизация bidi/control/zero-width, строчный diff кадра (повторный кадр не пишет ничего, изменённая строка пишет только себя), порядок «стереть до записи», отключение цвета, масштаб спарклайна. Экран Tree — 14 тестов, которые нажимают клавиши и читают текст кадра: порядок строк, спуск и возврат на ту же строку, смена режима размеров на hardlink-алиасе, фильтр, поиск с переходом в каталог находки, пометки, отказ открывать `.exe`, детали, выравнивание при `U+202E` и CJK, каталог на 200 тыс. записей (кадр рисует ровно страницу), `g`/`G`, сужение до 80 колонок. Кадр проверяется по неокрашенному тексту: утверждения об escape-последовательностях тестировали бы цветовую схему, а не поведение.

### 22.2. Что тестируем интеграционно на настоящей ФС

**Абстракция `IFileSystem` намеренно не вводится.** Вся ценность продукта — в обработке краевых случаев Win32 (reparse points, hardlinks, sparse, long paths, ACL, sharing violations, POSIX-delete), которые фейковая ФС **не может** воспроизвести. Фейк дал бы зелёные тесты и красный прод.

Тестовый харнесс создаёт в `%TEMP%` реальное дерево:
```
CreateJunction, CreateSymbolicLink, CreateHardLink
FSCTL_SET_SPARSE + FSCTL_SET_ZERO_DATA        → sparse-файл
файл с путём длиной 400 символов через \\?\
файл, открытый с FileShare.None                → sharing violation
каталог с DENY-ACE для текущего пользователя   → access denied
имя с U+202E, с CJK, с эмодзи, с control-char
рекурсивная junction-петля
файл размером 0
две ссылки на один файл в разных каталогах
```
Проверяется: обход, размеры, дедупликация, ошибки, и главное — **что удаление не выходит за пределы дерева**.

### 22.3. Тест гонки для T2

Отдельный тест: поток A рекурсивно удаляет дерево, поток B в цикле подменяет подкаталог на junction на охраняемый каталог-канарейку. После 10 тыс. итераций канарейка должна быть цела. Без этого теста `HandleTreeDeleter` нельзя считать готовым.

### 22.4. Сверка с эталоном

Скрипт сравнения с WizTree (CSV-экспорт) по 50 крупнейшим каталогам, допуск 1%. Запускается вручную перед релизом на 2–3 реальных машинах — синтетика здесь бесполезна.

### 22.5. Чего не тестируем автоматически

Вызов реальных `vssadmin delete shadows`, `DISM /StartComponentCleanup`, `powercfg /h off` — только вручную на ВМ со снапшотом. Такие тесты в CI недопустимы.

---

## 23. Глоссарий

| Термин | Значение |
|---|---|
| **MFT** | Master File Table — метаданные всех файлов тома NTFS в одном файле. Чтение напрямую даёт полный список файлов на два порядка быстрее обхода. |
| **USN Journal** | Журнал изменений NTFS. Позволяет узнать, что изменилось с прошлого раза, без обхода. |
| **Allocated size** | Реально занятое на томе (кластеры, сжатие, sparse). Единственная величина, складывающаяся в «занято». |
| **Logical size** | Размер потока данных. То, что показывает Проводник в свойствах файла. |
| **Unique allocated** | Allocated с дедупликацией жёстких ссылок. |
| **Reclaimable** | Сколько байт реально освободится при удалении данного набора. |
| **Hard link** | Несколько имён одного физического файла. `WinSxS` состоит из них почти целиком. |
| **Reparse point** | Junction, symlink, mount point, cloud placeholder. Требует особой обработки при обходе и при удалении. |
| **Sparse file** | Файл с «дырками»: logical > allocated. Типично для `.vhdx`. |
| **Cloud placeholder** | Файл OneDrive/Dropbox, данные которого в облаке. Чтение = скачивание. |
| **VSS / Shadow copy** | Теневая копия тома. Точки восстановления. Обходом ФС не видна. |
| **WinSxS** | Component store Windows. Чистится только через DISM. |
| **Snapshot (`.pmsnap`)** | Бинарный слепок дерева файлов одного скана. |
| **Quarantine** | Промежуточное хранилище удаляемого на том же томе. Освобождает место только после purge. |
| **Purge** | Физическое удаление карантина — момент, когда место реально освобождается. |
| **Risk** | Что сломается при удалении: Safe / Caution / Danger. |
| **Recoverability** | Чего будет стоить вернуть: Instant / Redownload / Rebuild / Irreversible. |
| **Finding** | Результат одной пробы Space Audit. |
| **Remedy** | Способ устранить finding: команда, удаление путей, системная настройка. |
| **Degraded scan** | Скан без прав администратора или на не-NTFS: медленнее и с неполными данными. |
| **Unaccounted** | Разница между занятым местом тома и всем, что pathmemo сумел объяснить. |

---

## Приложение A: сводка изменений относительно спецификации v2

| Область | v2 | v3 | Причина |
|---|---|---|---|
| **Удаление** | только корзина | quarantine / recycle / permanent, с явным показом «освободит сейчас / после purge» | Корзина на том же томе **не освобождает место** — то есть механизм противоречил цели продукта |
| **Исключения скана** | `C:\Windows`, `Program Files`, `ProgramData`, `node_modules` исключены по умолчанию | список пуст; исключение из учёта отделено от защиты от удаления | Там живёт большинство того, что мы ищем; иначе цифры не сходятся и критерий «±1% к WizTree» невыполним |
| **Модель данных** | топ-100 файлов и папок в SQLite | полное дерево в бинарном снимке | Без полного дерева нет drill-down — основного способа чистить диск. SQLite построчно дал бы 15+ ГБ истории |
| **Сканер** | `EnumerateFileSystemEntries`, MFT в Phase 3 | MFT основной, Walk fallback, USN-инкремент | 5 с против 5 мин; MFT видит недоступные по ACL пути и даёт allocated size бесплатно |
| **Параллелизм** | `Parallel.ForEachAsync` по папкам 2-го уровня, `MaxDOP = CPU` | work-stealing очередь, N по типу носителя | `WinSxS` = 40% файлов → один воркер; на HDD `MaxDOP=CPU` **замедляет** в 2–3 раза |
| **Размеры** | один «размер» | logical / allocated / unique / reclaimable | Без различения цифры не сходятся с Проводником никогда; `WinSxS` считался бы втрое |
| **Space Audit** | отсутствует | 20+ проб | VSS, WSL, Docker, WinSxS, Windows Update — обходом ФС не видны, но это самые крупные куски |
| **Способ очистки** | путь → удалить | Remedy: команда штатного механизма | Удаление `.git\objects`, `WinSxS`, `Windows\Installer` руками ломает систему/репозиторий |
| **Классификация** | одна ось risk | risk + recoverability | `node_modules` и `.git\objects` не могут быть в одной категории |
| **Защита путей** | строковый список, хардкод `C:\` | канонизация `GetFinalPathNameByHandle` + KnownFolders | Обходился 12 способами; системный том может быть не C: |
| **Рекурсивное удаление** | не описано | обход по handle с `RootDirectory` | Подмена на junction в процессе = удаление вне дерева |
| **`Ctrl+C`** | перехвачен как «копировать» | стандартная отмена; `y` = копировать | SIGINT; плюс Windows Terminal перехватывает сам — событие не дойдёт |
| **`Ctrl+Shift+C`, `Ctrl+1..6`, `Ctrl+/`** | биндинги | убраны | Перехватываются терминалом или не имеют VT-последовательности |
| **Хеш** | BLAKE3 + MD5 | XxHash128 + byte-for-byte | IO — узкое место, не хеш; XxHash в коробке; byte-compare даёт абсолютную гарантию |
| **Кэш хешей** | ключ по пути | ключ по `(volume, fileId, size, mtime)` + LRU | Переименование сбрасывало кэш; не было вытеснения |
| **Cloud-файлы** | «пропускать» | не хешировать, но учитывать с allocated≈0 | Хеширование placeholder'а **скачивает** его — 200 ГБ трафика вместо освобождения места |
| **Retention** | 30/180/730 дней | 20 последних + 12 месячных, cap 400 МБ | Инструмент для освобождения места занимал бы 15 ГБ |
| **`accessed_at`** | хранится и показывается | удалено | `NtfsDisableLastAccessUpdate` включён по умолчанию — данные ложные |
| **Правила** | regex в конфиге + дубль в БД | глобы, единственный источник — конфиг | Два синтаксиса в одном документе; ReDoS; два источника правды |
| **Elevated + конфиг** | не рассмотрено | пути не берутся из конфига, проверка DACL | Локальное повышение привилегий через `logFilePath` |
| **Открыть файл** | `Ctrl+O`, без ограничений | `o`, запрет исполняемых, подтверждение | Запуск малвари из `Downloads` одной клавишей |
| **Проводник** | `explorer.exe /select,"path"` | `SHOpenFolderAndSelectItems` | Инъекция через кавычку в имени |
| **Экраны TUI** | 11 | 5 | Размазанная ценность; Settings/Themes/L10n вырезаны |
| **TUI-библиотека** | Terminal.Gui | свой рендерер | Trimming/AOT, ширина CJK, контроль над деревом |
| **Проекты** | 6 | 2 | Трение без выгоды для одиночного инструмента |
| **Зависимости** | Terminal.Gui, Dapper, DbUp, Blake3, WinForms, Serilog, FluentAssertions | по факту на P5 — **только** Microsoft.Data.Sqlite (ни Spectre.Console, ни System.CommandLine не понадобились) | Размер, trimming, путь к AOT |
| **Размер exe** | ~70 МБ со сжатием | 18–28 МБ без сжатия | Сжатие = +300 мс на каждый запуск CLI-утилиты |
| **Расписание** | отсутствует | `pathmemo schedule` | Без автоскана история и diff мертвы |
| **Замер результата** | отсутствует | free space до/после | Единственный способ проверить, что модель размеров не врёт |
