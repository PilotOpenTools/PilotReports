#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Подключает статистику Reports к существующим отчётам (.repx).

В начало PilotReport_DataSourceDemanded вставляется «мягкая» строка: она находит модуль
через рефлексию и ничего не делает, если модуль не установлен. Поэтому отчёт не сломается
ни у кого - в отличие от `using Reports;`, который без модуля не скомпилируется.

Меняется только место вставки, остальной текст .repx остаётся байт в байт.
Повторный запуск безопасен: уже подключённые отчёты пропускаются (по метке Reports:stats).

Примеры:
    python add_report_stats.py "Реестр НОПРИЗ.repx"                 # имя отчёта = имя файла
    python add_report_stats.py report.repx --name "Реестр НОПРИЗ"   # имя отчёта явно
    python add_report_stats.py ./reports --dry-run                  # все .repx в папке, без записи
"""
import argparse
import html
import pathlib
import re
import sys

MARKER = "Reports:stats"

SNIPPET = (
    "    // {marker} - статистика построений (Grafana). Без модуля Reports строка ничего не делает.\n"
    "    try {{ foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) {{ var t = a.GetType(\"Reports.Report\"); "
    "if (t != null) {{ t.GetMethod(\"Begin\").Invoke(null, new object[] {{ this, \"{name}\", null }}); break; }} }} }} catch {{ }}\n"
)

# Обработчик ищем прямо в XML-экранированном тексте атрибута ScriptsSource.
WS = r"(?:\s|&#xD;|&#xA;|&#x9;)"
HANDLER = re.compile(
    r"void" + WS + r"+PilotReport_DataSourceDemanded" + WS + r"*\([^)]*\)" + WS
    + r"*\{(?:[ \t]|&#x9;)*(?:&#xD;)?(?:&#xA;)?")
ATTR = re.compile(r'ScriptsSource="([^"]*)"', re.S)

CR, LF, TAB = chr(13), chr(10), chr(9)


def csharp_string(value):
    return value.replace("\\", "\\\\").replace('"', '\\"')


def xml_attr(value):
    """Экранирование как у сериализатора DevExpress."""
    return (html.escape(value, quote=True).replace("&#x27;", "'")
            .replace(CR, "&#xD;").replace(LF, "&#xA;").replace(TAB, "&#x9;"))


def patch_repx(text, report_name):
    """Возвращает (новый текст .repx, статус)."""
    attr = ATTR.search(text)
    if not attr:
        return text, "в файле нет ScriptsSource"
    raw = attr.group(1)
    if MARKER in raw:
        return text, "уже подключено"
    m = HANDLER.search(raw)
    if not m:
        return text, "нет PilotReport_DataSourceDemanded"

    newline = CR + LF if "&#xD;&#xA;" in raw else LF
    snippet = SNIPPET.format(marker=MARKER, name=csharp_string(report_name)).replace(LF, newline)
    head = raw[:m.end()]
    if not head.endswith("&#xA;"):
        head += xml_attr(newline)
    new_raw = head + xml_attr(snippet) + raw[m.end():]
    return text[:attr.start(1)] + new_raw + text[attr.end(1):], "подключено"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="+", help=".repx файлы или папки")
    parser.add_argument("--name", help="Имя отчёта для статистики (только для одного файла)")
    parser.add_argument("--dry-run", action="store_true", help="Показать, что будет сделано, не меняя файлы")
    args = parser.parse_args()

    files = []
    for p in map(pathlib.Path, args.paths):
        files.extend(sorted(p.rglob("*.repx")) if p.is_dir() else [p])
    if args.name and len(files) != 1:
        parser.error("--name можно указать только для одного файла")

    changed = 0
    for f in files:
        raw_bytes = f.read_bytes()
        bom = raw_bytes.startswith(b"\xef\xbb\xbf")
        text = raw_bytes.decode("utf-8-sig")
        new_text, status = patch_repx(text, args.name or f.stem)
        if new_text != text:
            changed += 1
            if not args.dry_run:
                f.write_bytes((b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8"))
        print(f"{status:34} {f}")
    print(f"\nИзменено файлов: {changed}{' (dry-run, ничего не записано)' if args.dry_run else ''}")


if __name__ == "__main__":
    sys.exit(main())
