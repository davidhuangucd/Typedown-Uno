#!/usr/bin/env python3
"""Catch editor UI strings the host forgot to provide.

The editor asks the host to draw its floating tools and tooltips (the page draws them via uno-bridge.js,
looking each label up in the `menuStrings` map the host posts as ContextMenuStrings). If the host does not
provide a key the bridge asks for, the label falls back to the raw English key — the class of bug that left
the copy tooltip and the table-tool tips untranslated. This script fails when that gap reappears, so a new
editor string cannot silently ship untranslated.

  python3 Tools/check-editor-strings.py     # exit 1 if the host is missing any string the editor needs

Sources (all in this repo, so it runs without the editor's source tree):
  - Assets/Editor/uno-bridge.js  : the static `menuStrings.X` keys the bridge reads.
  - Assets/Editor/static/js/main.*.js : every `data-tooltip` value -> a `tip_<value>` key the bridge needs.
  - Typedown.Uno/MainPage.xaml.cs : the keys the host actually posts in PostContextMenuStrings.
  - the names the page asks for with getStringResources (its placeholders and notices, the array in main.*.js that
    starts with InputFootnoteDefine): the host answers each with Loc.Get, which falls back to the key itself, so
    each must be in the English table (Typedown.Uno/Services/Loc.cs). FirstEditWarning was missing: the notice
    would have shown its key.
"""
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
BRIDGE = os.path.join(ROOT, "Typedown.Uno", "Assets", "Editor", "uno-bridge.js")
MAIN = os.path.join(ROOT, "Typedown.Uno", "Assets", "Editor", "static", "js")
HOST = os.path.join(ROOT, "Typedown.Uno", "MainPage.xaml.cs")
LOC = os.path.join(ROOT, "Typedown.Uno", "Services", "Loc.cs")


def read(p):
    return open(p, encoding="utf-8").read()


def needed_menu_keys(bridge_src):
    # menuStrings.copy , menuStrings.insertRowAbove , ...  (skip the dynamic menuStrings['tip_' + ...])
    return set(re.findall(r"menuStrings\.([A-Za-z][A-Za-z0-9_]*)", bridge_src))


def needed_tip_keys(bundle_src):
    # data-tooltip values become tip_<value>; matches "data-tooltip":"X" and data-tooltip='X' and attrs form
    vals = set(re.findall(r"""data-tooltip['"]?\s*[:=]\s*['"]([A-Za-z][A-Za-z0-9_]*)['"]""", bundle_src))
    return {"tip_" + v for v in vals}


def needed_resource_names(bundle_src):
    # Every getStringResources({names: [...]}) in the bundle, the array written in the call or held in a variable
    # (localization.ts: const names = [...], minified to xh=[...] ... getStringResources({names:xh})).
    names = set()
    calls = re.findall(r"getStringResources\(\{names:(\[[^\]]*\]|[A-Za-z_$][A-Za-z0-9_$]*)\}", bundle_src)
    if not calls:
        print("check-editor-strings: no getStringResources call found in the bundle — update the parser", file=sys.stderr)
        sys.exit(2)
    for arg in calls:
        if not arg.startswith("["):
            m = re.search(r"(?:\b|,)" + re.escape(arg) + r"=(\[[^\]]*\])", bundle_src)
            if not m:
                print(f"check-editor-strings: the names array {arg} was not found — update the parser", file=sys.stderr)
                sys.exit(2)
            arg = m.group(1)
        names |= set(re.findall(r"""['"]([A-Za-z][A-Za-z0-9_]*)['"]""", arg))
    return names


def english_keys(loc_src):
    return set(re.findall(r'\["([A-Za-z_][A-Za-z0-9_]*)"\]\s*=', loc_src))


def provided_keys(host_src):
    # the anonymous object inside PostContextMenuStrings: field = Loc.Get("...") / field = ...,
    m = re.search(r"PostContextMenuStrings\(\)\s*=>\s*Post\(\"ContextMenuStrings\",\s*new\s*\{(.*?)\}\s*\)\s*;", host_src, re.S)
    if not m:
        print("check-editor-strings: could not find PostContextMenuStrings — update the parser", file=sys.stderr)
        sys.exit(2)
    return set(re.findall(r"([A-Za-z_][A-Za-z0-9_]*)\s*=", m.group(1)))


def main():
    bridge = read(BRIDGE)
    bundles = glob.glob(os.path.join(MAIN, "main.*.js"))
    bundle = "".join(read(p) for p in bundles)
    host = read(HOST)

    needed = needed_menu_keys(bridge) | needed_tip_keys(bundle)
    provided = provided_keys(host)
    missing = sorted(needed - provided)

    resources = needed_resource_names(bundle)
    unknown = sorted(resources - english_keys(read(LOC)))
    if unknown:
        print("The editor asks the host for these strings (getStringResources) but Loc.cs has no English text for them:")
        for k in unknown:
            print("  -", k)
        print("\nAdd them to Loc.cs (and the translation tables in Services/Locales).")
        sys.exit(1)

    if missing:
        print("The editor needs these string keys but the host's PostContextMenuStrings does not provide them:")
        for k in missing:
            print("  -", k)
        print("\nAdd `%s = Loc.Get(\"...\")` to PostContextMenuStrings (and the string to the translation tables)." % missing[0])
        sys.exit(1)
    print(f"editor strings ok: {len(needed)} keys, all provided by the host; {len(resources)} resource strings in Loc.cs")


if __name__ == "__main__":
    main()
