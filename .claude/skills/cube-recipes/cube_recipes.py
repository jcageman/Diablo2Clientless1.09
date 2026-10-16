"""Lists the realm's Horadric Cube recipes with every input code resolved to the items it accepts.

Reads CubeRecipes, ItemTypes and BaseItems from <bot folder>/data/item-affixes.json (written by
tools/MpqData from patch_d2.mpq).

  python cube_recipes.py --bot-folder <bot folder> list [--all]
  python cube_recipes.py --bot-folder <bot folder> show <index>...
  python cube_recipes.py --bot-folder <bot folder> uses <item name or code>
  python cube_recipes.py --bot-folder <bot folder> makes <item name or code>
"""
import argparse
import json
import os
import re
import sys

GEM_TIERS = {"gem0": "Chipped ", "gem1": "Flawed ", "gem3": "Flawless ", "gem4": "Perfect "}
QUALIFIERS = {"mag": "magic", "rar": "rare", "nor": "normal", "uni": "unique", "crf": "crafted", "set": "set",
              "upg": "or its exceptional/elite version", "nos": "without sockets", "eth": "ethereal", "noe": "not ethereal"}


def load(bot_folder):
    path = os.path.join(bot_folder, "data", "item-affixes.json")
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    types = {t["Code"]: t for t in data["ItemTypes"] if t.get("Code")}
    bases = {b["Code"].lower(): b for b in data["BaseItems"]}
    return data["CubeRecipes"], types, bases


def ancestors(type_code, types):
    seen, todo = set(), [type_code]
    while todo:
        code = todo.pop()
        if code in seen or code not in types:
            continue
        seen.add(code)
        todo += [types[code].get(k) for k in ("Equivalent1", "Equivalent2") if types[code].get(k)]
    return seen


def gem_tier(base):
    name = base["Name"]
    if base.get("Type") not in ("gema", "gemd", "geme", "gemr", "gems", "gemt", "gemz"):
        return None
    for code, prefix in GEM_TIERS.items():
        if name.startswith(prefix):
            return code
    return "gem2"


def matches(code, types, bases):
    """Every base item an input code accepts."""
    code_l = code.lower()
    if code_l in bases:
        return [bases[code_l]]
    if code == "any":
        return None
    if code in GEM_TIERS or code == "gem2":
        return [b for b in bases.values() if gem_tier(b) == code]
    if code in types:
        return [b for b in bases.values() if b.get("Type") and code in ancestors(b["Type"], types)]
    return []


def describe_input(spec, types, bases, names=True):
    parts = spec.split(",")
    code, rest = parts[0].strip(), [p.strip() for p in parts[1:]]
    qty = next((int(p[4:]) for p in rest if p.startswith("qty=")), 1)
    quals = [QUALIFIERS.get(p, p) for p in rest if not p.startswith("qty=") and p]
    found = matches(code, types, bases)
    if found is None:
        what = "any item"
    elif not found:
        what = f"'{code}' (not an item in the tables: a realm item or a named unique/set)"
    elif len(found) == 1:
        what = found[0]["Name"].strip()
    elif not names:
        what = f"{code} ({len(found)} bases)"
    else:
        listed = ", ".join(sorted({b["Name"].strip() for b in found}))
        what = f"{code}: {listed}"
    return f"{qty}x " + what + (f" [{', '.join(quals)}]" if quals else "")


def describe_output(spec, bases):
    parts = [p.strip() for p in spec.split(",")]
    head = parts[0]
    if head.lower() in bases:
        head = bases[head.lower()]["Name"].strip()
    elif head == "usetype":
        head = "same base type as the input"
    elif head == "useitem":
        head = "the input item itself"
    extra = []
    for p in parts[1:]:
        if p in QUALIFIERS:
            extra.append(QUALIFIERS[p])
        elif p.startswith("sock="):
            extra.append(f"up to {p[5:]} sockets")
        elif p.startswith("pre=") or p.startswith("suf="):
            extra.append(f"fixed {'prefix' if p.startswith('pre') else 'suffix'} #{p[4:]}")
        elif p:
            extra.append(p)
    return head + (f" ({', '.join(extra)})" if extra else "")


def print_recipe(r, types, bases, names=True):
    inputs = [i for i in r["Inputs"] if i]
    state = "" if r["Enabled"] else " DISABLED"
    mode = "expansion" if r["Version"] == 100 else "both"
    print(f"#{r['Index']} ({mode}){state}")
    for i in inputs:
        print("   + " + describe_input(i, types, bases, names))
    print("   = " + describe_output(str(r["Output"]), bases))
    for m in r.get("Mods") or []:
        print(f"     mod {m['Property']} {m['Min']}-{m['Max']}" + (f" (param {m['Param']})" if m.get("Param") else ""))


def find_codes(term, bases):
    term_l = term.lower().replace(" ", "")
    if term_l in bases:
        return {term_l}
    return {c for c, b in bases.items() if b["Name"].lower().replace(" ", "").replace("'", "") == term_l.replace("'", "")}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bot-folder", default=os.environ.get("D2_BOT_FOLDER"))
    sub = parser.add_subparsers(dest="command", required=True)
    lst = sub.add_parser("list", help="one line per recipe")
    lst.add_argument("--all", action="store_true", help="include the set-item reverts (127-215)")
    show = sub.add_parser("show", help="recipes in full, with every accepted item named")
    show.add_argument("index", type=int, nargs="+")
    uses = sub.add_parser("uses", help="recipes that take this item as an input")
    uses.add_argument("item")
    makes = sub.add_parser("makes", help="recipes that produce this item")
    makes.add_argument("item")
    args = parser.parse_args()
    if not args.bot_folder:
        sys.exit("pass --bot-folder or set D2_BOT_FOLDER")

    recipes, types, bases = load(args.bot_folder)
    by_index = {r["Index"]: r for r in recipes}

    if args.command == "list":
        for r in recipes:
            if not args.all and 127 <= r["Index"] <= 215:
                continue
            inputs = " + ".join(describe_input(i, types, bases, names=False) for i in r["Inputs"] if i)
            print(f"#{r['Index']}{'' if r['Enabled'] else ' DISABLED'}: {inputs} = {describe_output(str(r['Output']), bases)}")
    elif args.command == "show":
        for i in args.index:
            print_recipe(by_index[i], types, bases)
    else:
        codes = find_codes(args.item, bases)
        if not codes:
            sys.exit(f"no base item named or coded '{args.item}'")
        for r in recipes:
            if args.command == "uses":
                hit = any(
                    (found := matches(i.split(",")[0].strip(), types, bases)) is None
                    or any(b["Code"].lower() in codes for b in found)
                    for i in r["Inputs"] if i)
                hit = hit and not all(matches(i.split(",")[0].strip(), types, bases) is None for i in r["Inputs"] if i)
            else:
                hit = str(r["Output"]).split(",")[0].strip().lower() in codes
            if hit:
                print_recipe(r, types, bases, names=False)


if __name__ == "__main__":
    main()
