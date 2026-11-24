"""Usage: python tools/generate-pinyin-initials.py path/to/Unihan.zip"""

import argparse
from pathlib import Path
import string
import unicodedata
from zipfile import ZipFile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("unihan", type=Path)
    args = parser.parse_args()
    with ZipFile(args.unihan) as archive:
        readings = archive.read("Unihan_Readings.txt").decode("utf-8-sig")

    groups = {letter: set() for letter in string.ascii_lowercase}
    for line in readings.splitlines():
        if not line.startswith("U+"):
            continue
        code, field, value = line.split("\t")
        if field == "kMandarin":
            syllables = value.split()
        elif field == "kXHC1983":
            syllables = [syllable for entry in value.split()
                         for syllable in entry.split(":", 1)[-1].split(",")]
        else:
            continue
        for syllable in syllables:
            initial = unicodedata.normalize("NFD", syllable.lower())[:1]
            if initial in groups:
                groups[initial].add(int(code[2:], 16))

    target = Path(__file__).resolve().parents[1] / "Assets/Search/UnihanPinyinInitials.txt"
    target.parent.mkdir(parents=True, exist_ok=True)
    largest = max(max(points) for points in groups.values() if points)
    rows = [f"{largest:X}"] + [
        letter + "".join(map(chr, sorted(points))) for letter, points in groups.items()
    ]
    target.write_text("\n".join(rows) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
