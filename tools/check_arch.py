#!/usr/bin/env python3
"""Проверка модульной архитектуры проекта facing-mist.

Скрипт читает architecture/modules.json и для каждого модуля:
  1. находит .asmdef-файл по пути из поля "path";
  2. проверяет, что все references из .asmdef (кроме пакетов Unity,
     т.е. имён вида "Unity.*") объявлены в deps модуля;
  3. проверяет, что ни одна зависимость из deps не входит в forbidden.

Используется только стандартная библиотека Python.
Код выхода: 0 — всё хорошо, 1 — найдены нарушения.
"""

import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
MODULES_JSON = PROJECT_ROOT / "architecture" / "modules.json"


def is_unity_package(name: str) -> bool:
    """Ссылки на пакеты Unity (Unity.Entities, Unity.Collections и т.п.) игнорируются."""
    return name.startswith("Unity.")


def check_module(module: dict) -> list[str]:
    """Проверяет один модуль, возвращает список нарушений (пустой — если всё хорошо)."""
    errors: list[str] = []
    name = module.get("name", "<unknown>")
    deps = list(module.get("deps", []))
    forbidden = {entry.get("module") for entry in module.get("forbidden", [])}

    # 1. Ищем asmdef-файл по пути из modules.json.
    module_dir = PROJECT_ROOT / module.get("path", "")
    asmdef_path = module_dir / f"{name}.asmdef"
    if not asmdef_path.is_file():
        errors.append(f"{name}: asmdef-файл не найден: {asmdef_path.relative_to(PROJECT_ROOT)}")
        return errors

    try:
        asmdef = json.loads(asmdef_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        errors.append(f"{name}: не удалось прочитать {asmdef_path.relative_to(PROJECT_ROOT)}: {exc}")
        return errors

    references = asmdef.get("references", [])

    # 2. Все project-ссылки из asmdef должны быть объявлены в deps.
    declared = set(deps)
    for ref in references:
        if is_unity_package(ref):
            continue
        if ref not in declared:
            errors.append(f"{name}: ссылка '{ref}' из asmdef не объявлена в deps")

    # 3. Никакая зависимость не должна входить в forbidden.
    for dep in deps:
        if dep in forbidden:
            errors.append(f"{name}: зависимость '{dep}' входит в forbidden")

    return errors


def main() -> int:
    if not MODULES_JSON.is_file():
        print(f"ERROR: файл не найден: {MODULES_JSON}")
        return 1

    try:
        data = json.loads(MODULES_JSON.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"ERROR: не удалось разобрать {MODULES_JSON.name}: {exc}")
        return 1

    modules = data.get("modules", [])
    if not modules:
        print(f"ERROR: в {MODULES_JSON.name} не найдено ни одного модуля")
        return 1

    all_errors: list[str] = []
    for module in modules:
        all_errors.extend(check_module(module))

    if all_errors:
        print("ARCH CHECK FAILED:")
        for error in all_errors:
            print(f"  - {error}")
        return 1

    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
