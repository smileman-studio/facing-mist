#!/usr/bin/env python3
"""Генерация сырых GitHub-адресов всех файлов проекта facing-mist.

Скрипт собирает список файлов проекта (git ls-files — все отслеживаемые файлы)
и для каждого строит сырой адрес вида:
    https://raw.githubusercontent.com/smileman-studio/facing-mist/refs/heads/master/<путь файла>

Результат записывается в файл RAW_URLS.txt в корне проекта (старый файл
удаляется и пересоздаётся). Используется только стандартная библиотека.
"""

import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
RAW_BASE = "https://raw.githubusercontent.com/smileman-studio/facing-mist/refs/heads/master"
OUTPUT_FILE = PROJECT_ROOT / "RAW_URLS.txt"

# Расширения, которые не попадают в список сырых адресов.
EXCLUDED_EXTENSIONS = {".meta", ".mat", ".asset"}


def list_project_files() -> list[str]:
    """Возвращает пути всех отслеживаемых файлов относительно корня проекта.

    Исключает файлы с расширениями из EXCLUDED_EXTENSIONS (.meta, .mat, .asset).
    """
    result = subprocess.run(
        ["git", "ls-files"],
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True,
        check=True,
    )
    files = []
    for line in result.stdout.splitlines():
        path = line.strip()
        if not path:
            continue
        suffix = Path(path).suffix.lower()
        if suffix in EXCLUDED_EXTENSIONS:
            continue
        files.append(path)
    return files


def build_urls(files: list[str]) -> list[str]:
    """Строит сырые GitHub-адреса для списка файлов."""
    return [f"{RAW_BASE}/{path}" for path in sorted(files)]


def main() -> int:
    try:
        files = list_project_files()
    except subprocess.CalledProcessError as exc:
        print(f"ERROR: git ls-files завершился с ошибкой: {exc}")
        return 1

    if not files:
        print("ERROR: git ls-files не вернул ни одного файла")
        return 1

    urls = build_urls(files)

    # Старый файл удаляется (если есть) и записывается новый.
    if OUTPUT_FILE.exists():
        OUTPUT_FILE.unlink()
    OUTPUT_FILE.write_text("\n".join(urls) + "\n", encoding="utf-8")

    print(f"OK: записано {len(urls)} адресов в {OUTPUT_FILE.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
