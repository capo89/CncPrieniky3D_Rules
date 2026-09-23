#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Dump relevant sheets from CncExporter Excel (.xlsx) for debugging Prieniky3D.

Usage:
  python tools/dump-export.py path\\to\\Export_....xlsx
  python tools/dump-export.py path\\to\\folder          # najnovší Export_*.xlsx
  python tools/dump-export.py path\\to\\Export.xlsx --cnc-all

Výstup: Kusovník (WCS + AABB), os medzi bokmi, Znacenie CNC na bokoch.
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
REL_NS = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"


def cell_val(c, ss: list[str]) -> str:
    t = c.attrib.get("t")
    v = c.find("m:v", NS)
    if v is None:
        return ""
    if t == "s":
        return ss[int(v.text)]
    return v.text or ""


def colrow(ref: str) -> tuple[int, int]:
    m = re.match(r"([A-Z]+)(\d+)", ref)
    if not m:
        return 0, 0
    col = 0
    for ch in m.group(1):
        col = col * 26 + ord(ch) - 64
    return col, int(m.group(2))


def load_sheets(path: str) -> dict[str, dict[int, dict[int, str]]]:
    with zipfile.ZipFile(path) as z:
        ss: list[str] = []
        if "xl/sharedStrings.xml" in z.namelist():
            root = ET.fromstring(z.read("xl/sharedStrings.xml"))
            for si in root.findall("m:si", NS):
                ss.append("".join(t.text or "" for t in si.findall(".//m:t", NS)))

        wb = ET.fromstring(z.read("xl/workbook.xml"))
        rels = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
        rid = {rel.attrib["Id"]: rel.attrib["Target"] for rel in rels if "Id" in rel.attrib}

        sheets: dict[str, dict[int, dict[int, str]]] = {}
        for sh in wb.findall("m:sheets/m:sheet", NS):
            name = sh.attrib["name"]
            r_id = sh.attrib[REL_NS]
            target = "xl/" + rid[r_id].lstrip("/")
            if target.startswith("xl/xl/"):
                target = target[3:]
            root = ET.fromstring(z.read(target))
            grid: dict[int, dict[int, str]] = defaultdict(dict)
            for c in root.findall(".//m:c", NS):
                ref = c.attrib.get("r")
                if not ref:
                    continue
                col, row = colrow(ref)
                grid[row][col] = cell_val(c, ss)
            sheets[name] = grid
        return sheets


def find_sheet(sheets: dict, *needles: str):
    for name, grid in sheets.items():
        n = name.lower()
        for needle in needles:
            if needle.lower() in n:
                return name, grid
    return None, None


def find_header_row(grid: dict, *must: str) -> int | None:
    for r in sorted(grid)[:40]:
        vals = [str(grid[r].get(c, "")) for c in range(1, 30)]
        joined = " ".join(vals).lower()
        if any(m.lower() in joined for m in must):
            # prefer row that has "názov" as a cell
            for c in range(1, 30):
                h = (grid[r].get(c) or "").lower()
                if "názov" in h or "nazov" in h or "diel" in h or "značenie" in h or "znacenie" in h:
                    return r
            return r
    return None


def header_map(grid: dict, hdr: int) -> dict[int, str]:
    return {c: grid[hdr].get(c, "") for c in range(1, 40) if grid[hdr].get(c)}


def col_of(hm: dict[int, str], *names: str) -> int | None:
    for c, h in hm.items():
        hh = (h or "").lower()
        for n in names:
            if n.lower() in hh:
                return c
    return None


def fnum(s) -> float:
    try:
        return float(str(s).replace(",", ".") or 0)
    except ValueError:
        return 0.0


def resolve_xlsx(arg: str) -> str:
    if os.path.isfile(arg) and arg.lower().endswith(".xlsx"):
        return arg
    if os.path.isdir(arg):
        paths = sorted(
            glob.glob(os.path.join(arg, "Export_*.xlsx")),
            key=os.path.getmtime,
            reverse=True,
        )
        if not paths:
            paths = sorted(
                glob.glob(os.path.join(arg, "*.xlsx")),
                key=os.path.getmtime,
                reverse=True,
            )
        if not paths:
            raise SystemExit(f"V priečinku nie je žiadny .xlsx: {arg}")
        return paths[0]
    raise SystemExit(f"Súbor/priečinok neexistuje: {arg}")


def dump_kusovnik(grid: dict) -> list[dict]:
    hdr = find_header_row(grid, "Názov dielu", "Nazov")
    if hdr is None:
        print("  [!] Kusovník: nenašiel sa header")
        return []
    hm = header_map(grid, hdr)
    cC = col_of(hm, "Č.", "C.")
    cN = col_of(hm, "Názov")
    cRx = col_of(hm, "Rozmer X")
    cRy = col_of(hm, "Rozmer Y")
    cRz = col_of(hm, "Rozmer Z")
    cWx = col_of(hm, "WCS Min X")
    cWy = col_of(hm, "WCS Min Y")
    cWz = col_of(hm, "WCS Min Z")
    cMx = col_of(hm, "WCS Max X")
    cMy = col_of(hm, "WCS Max Y")
    cMz = col_of(hm, "WCS Max Z")

    parts: list[dict] = []
    print(f"=== Kusovník (header r{hdr}) ===")
    for r in sorted(grid):
        if r <= hdr:
            continue
        naz = grid[r].get(cN, "") if cN else ""
        if not naz:
            continue
        rx, ry, rz = fnum(grid[r].get(cRx)), fnum(grid[r].get(cRy)), fnum(grid[r].get(cRz))
        wx, wy, wz = fnum(grid[r].get(cWx)), fnum(grid[r].get(cWy)), fnum(grid[r].get(cWz))
        mx = fnum(grid[r].get(cMx)) if cMx else wx + rx
        my = fnum(grid[r].get(cMy)) if cMy else wy + ry
        mz = fnum(grid[r].get(cMz)) if cMz else wz + rz
        cis = grid[r].get(cC, "") if cC else ""
        thin = min(x for x in (rx, ry, rz) if x > 0) if min(rx, ry, rz) > 0 else 0
        p = {
            "cislo": cis,
            "nazov": naz,
            "rx": rx,
            "ry": ry,
            "rz": rz,
            "wx": wx,
            "wy": wy,
            "wz": wz,
            "mx": mx,
            "my": my,
            "mz": mz,
            "thin": thin,
        }
        parts.append(p)
        print(
            f"  č.{cis:>3} {naz!r}\n"
            f"       AABB {rx:.0f}×{ry:.0f}×{rz:.0f}  "
            f"WCS ({wx:.0f},{wy:.0f},{wz:.0f})–({mx:.0f},{my:.0f},{mz:.0f})"
        )
    return parts


def dump_bok_axis(parts: list[dict]) -> None:
    boks = [p for p in parts if "bok" in p["nazov"].lower()]
    if len(boks) < 1:
        print("\n=== Boky === (žiadne)")
        return
    print("\n=== Boky / os medzi nimi ===")
    for p in boks:
        thin = p["thin"]
        if abs(p["rx"] - thin) <= 0.51:
            axis = "X"
            lo, hi = p["wx"], p["mx"]
        elif abs(p["ry"] - thin) <= 0.51:
            axis = "Y"
            lo, hi = p["wy"], p["my"]
        else:
            axis = "?"
            lo, hi = 0.0, 0.0
        print(
            f"  {p['nazov']}: thin={thin:.0f} na {axis}, "
            f"vonkajšie rozpätie {lo:.0f}..{hi:.0f}"
        )

    if len(boks) < 2:
        return

    def cx(p):
        return (p["wx"] + p["mx"]) * 0.5

    def cy(p):
        return (p["wy"] + p["my"]) * 0.5

    span_x = max(cx(p) for p in boks) - min(cx(p) for p in boks)
    span_y = max(cy(p) for p in boks) - min(cy(p) for p in boks)
    axis = "X" if span_x >= span_y - 0.1 else "Y"
    print(f"  Δ center X={span_x:.1f}  Δ center Y={span_y:.1f}  → os = {axis}")

    if axis == "X":
        left = min(boks, key=lambda p: p["wx"])
        right = max(boks, key=lambda p: p["mx"])
        print(
            f"  L (min X) = {left['nazov']!r}  X={left['wx']:.0f}..{left['mx']:.0f}\n"
            f"  P (max X) = {right['nazov']!r}  X={right['wx']:.0f}..{right['mx']:.0f}"
        )
    else:
        left = max(boks, key=lambda p: p["my"])
        right = min(boks, key=lambda p: p["wy"])
        print(
            f"  L (max Y) = {left['nazov']!r}  Y={left['wy']:.0f}..{left['my']:.0f}\n"
            f"  P (min Y) = {right['nazov']!r}  Y={right['wy']:.0f}..{right['my']:.0f}"
        )


def dump_cnc(grid: dict, bok_only: bool = True) -> None:
    hdr = find_header_row(grid, "Pos X", "Značenie", "Znacenie", "Priemer")
    if hdr is None:
        print("\n=== Znacenie CNC === (nenašiel sa header)")
        return
    hm = header_map(grid, hdr)
    # reverse: name -> col
    by_name = {v: k for k, v in hm.items()}

    def g(row: dict, *names: str) -> str:
        for n in names:
            for k, v in hm.items():
                if n.lower() in (v or "").lower():
                    return row.get(k, "")
        return ""

    print(f"\n=== Znacenie CNC (header r{hdr}) ===")
    rows: list[dict] = []
    for r in sorted(grid):
        if r <= hdr:
            continue
        row = {c: grid[r].get(c, "") for c in hm}
        if not any(row.values()):
            continue
        naz = g(row, "Názov dielu", "Nazov")
        if bok_only and "bok" not in naz.lower():
            continue
        rows.append(
            {
                "diel": g(row, "Diel č.", "Diel"),
                "nazov": naz,
                "typ": g(row, "Typ"),
                "vrstva": g(row, "Vrstva"),
                "x": fnum(g(row, "Pos X (na ploche)", "Pos X")),
                "y": fnum(g(row, "Pos Y (na ploche)", "Pos Y")),
                "z": fnum(g(row, "Pos Z")),
                "dia": fnum(g(row, "Priemer")),
                "hlbka": fnum(g(row, "Hĺbka", "Hlbka")),
            }
        )

    if not rows:
        print("  (žiadne riadky" + (" na bokoch)" if bok_only else ")"))
        return

    # group by diel+nazov
    groups: dict[str, list] = defaultdict(list)
    for row in rows:
        groups[f"{row['diel']}|{row['nazov']}"].append(row)

    for key, grp in sorted(groups.items()):
        typ = Counter(r["typ"] or "?" for r in grp)
        vrst = Counter(r["vrstva"] or "?" for r in grp)
        xs = sorted({round(r["x"] * 2) / 2 for r in grp})
        ys = sorted({round(r["y"] * 2) / 2 for r in grp})

        def pair32(vals: list[float]) -> bool:
            return len(vals) == 2 and abs(vals[1] - vals[0] - 32.0) <= 2.5

        sys32 = pair32(xs) or pair32(ys)
        print(f"\n  {key}: {len(grp)} značiek")
        print(f"    Typ: {dict(typ)}")
        print(f"    Vrstva: {dict(vrst)}")
        print(f"    PosX unique: {xs[:12]}{'…' if len(xs) > 12 else ''}")
        print(f"    PosY unique: {ys[:12]}{'…' if len(ys) > 12 else ''}")
        print(f"    System32 pár (~32 mm): {sys32}")

        for vrst_name in sorted(vrst):
            sub = [r for r in grp if (r["vrstva"] or "?") == vrst_name]
            sxs = sorted({round(r["x"] * 2) / 2 for r in sub})
            sys = sorted({round(r["y"] * 2) / 2 for r in sub})
            print(
                f"    — {vrst_name}: {len(sub)} ks, "
                f"X={sxs[:8]}, Y×{len(sys)}, pair32={pair32(sxs) or pair32(sys)}"
            )


def main() -> None:
    ap = argparse.ArgumentParser(description="Dump CncExporter Excel pre Prieniky3D")
    ap.add_argument("path", help="Export_*.xlsx alebo priečinok s exportmi")
    ap.add_argument(
        "--cnc-all",
        action="store_true",
        help="Znacenie CNC pre všetky dielce (default: len boky)",
    )
    args = ap.parse_args()
    path = resolve_xlsx(args.path)
    print(f"Súbor: {path}")
    print(f"mtime: {os.path.getmtime(path):.0f}\n")

    sheets = load_sheets(path)
    print("Hárky:", ", ".join(sheets.keys()))

    _, kus = find_sheet(sheets, "kusovnik", "kusovník")
    parts: list[dict] = []
    if kus:
        parts = dump_kusovnik(kus)
        dump_bok_axis(parts)
    else:
        print("[!] Chýba hárok Kusovník")

    _, cnc = find_sheet(sheets, "znacenie", "cnc")
    if cnc:
        dump_cnc(cnc, bok_only=not args.cnc_all)
    else:
        print("\n[!] Chýba hárok Znacenie CNC")


if __name__ == "__main__":
    # Windows konzola
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass
    main()
