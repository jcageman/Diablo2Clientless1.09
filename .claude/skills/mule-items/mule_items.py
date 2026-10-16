"""Search the mule database for items and compare them with what a character wears.

    mule_items.py items --type Ring --quality Rare,Magical --realm classic \
        --has Dexterity,MinimumLifeStolenPerHit --weights Dexterity=2,MinimumLifeStolenPerHit=5,Res=0.5
    mule_items.py gear <character> [--skill "Guided Arrow"] [--difficulty hell]
    mule_items.py calc <character> --replace "left ring=<item id>" --replace "boots=none" [--skill ...]
    mule_items.py combo <character> --slot "left ring=Ring" --slot boots=Boots \
        --weights Dexterity=2,FasterRunWalk=1,Damage=0.2 --skill "Guided Arrow"
    mule_items.py account <account>
    mule_items.py realm <character> [<character> ...]

Stat names are the StatType names the database stores (Dexterity, Life, AttackRating,
MinimumDamage, MinimumLifeStolenPerHit, FasterRunWalk, FireResistance, ExtraGold,
BetterChanceOfGettingMagicItem, ...). `Res` in --has, --min and --weights is the sum of the four
resists. With --skill, `Damage` (average per hit, crits included) and `<Skill> Level` can be
weighted too. Leech is only ever shown and weighted as a percentage: what it returns depends on the
damage the target actually takes (capped at its remaining life, after its damage reduction). Slots are the armory's: head, amulet, body, right hand,
left hand, right ring, left ring, belt, boots, gloves.

Resists are judged after the difficulty penalty (classic 0/-20/-50, expansion 0/-40/-100) and
capped at 75 plus any maximum-resist bonus. `combo` keeps fire, cold and lightning at least where
they stand, values each point under the cap (--res-weights), values cold above the cap
(--overflow) and ignores poison: the PvP consensus. Base requirements come from the game tables,
so every combination is checked for the strength and dexterity its gear needs.

Game data comes from <bot folder>/data (item-affixes.json and skills.json, written by
tools/MpqData). The connection string comes from muleconfig.json in the bot folder (--bot-folder,
or the D2_BOT_FOLDER environment variable); accounts listed in muleconfig-expansion.json count as
expansion, every other account as classic. `realm` asks the armory, which is the authority when a
character's realm matters. The armory re-reads a character at every login, so its parse date is
the last login and the gear shown is current.
"""

import argparse
import glob
import itertools
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request
from collections import namedtuple
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
ARMORY = "https://armory.diablo09.com/armory/api"
RESISTS = ("FireResistance", "ColdResistance", "LightningResistance", "PoisonResistance")
MAX_RESISTS = {r: "Maximum" + r for r in RESISTS}
RESIST_CAP = 75
PENALTY = {False: {"normal": 0, "nightmare": -20, "hell": -50},
           True: {"normal": 0, "nightmare": -40, "hell": -100}}
CLASSES = ("Amazon", "Sorceress", "Necromancer", "Paladin", "Barbarian", "Druid", "Assassin")
CLASS_SKILLS = tuple(c + "Skills" for c in CLASSES)
SLOTS = {1: "head", 2: "amulet", 3: "body", 4: "right hand", 5: "left hand", 6: "right ring",
         7: "left ring", 8: "belt", 9: "boots", 10: "gloves"}
BASE_ATTRIBUTES = {"Strength": "strength", "Dexterity": "dexterity", "Vitality": "vitality",
                   "Energy": "energy", "Life": "max_hp", "Mana": "max_mana"}
# StatType has four entries the game does not from id 88 on, so ids 88-160 sit four places further
# down the enum. Past 160 it drifts again and the armory's label is used instead.
SHIFTED_STAT_IDS = range(88, 161)
ARMORY_STAT_IDS = {-2: "MinimumDamage", -3: "MaximumDamage"}
ADDS_DAMAGE = re.compile(r"Adds (\d+)-(\d+) (Fire|Lightning|Cold|Magic|Poison) Damage")
TAB_LABEL = re.compile(r"to (.+?)(?: Skills)? \((\w+) Only\)")
PER_LEVEL = re.compile(r"^\+(\d+) to (Life|Mana|Attack Rating|Defense) \(Based on Character Level\)")
PER_LEVEL_STATS = {"Attack Rating": "AttackRating"}
INVENTORY = 1
CHARM_TYPES = ("cm1", "cm2", "cm3")
# One skill per tree tab, to find which page number is which tab, and the word in the tab's name.
TAB_SKILLS = {
    "Amazon": {"Magic Arrow": "Bow", "Critical Strike": "Passive", "Jab": "Javelin"},
    "Sorceress": {"Fire Bolt": "Fire", "Charged Bolt": "Lightning", "Ice Bolt": "Cold"},
    "Necromancer": {"Amplify Damage": "Curses", "Teeth": "Poison", "Raise Skeleton": "Summoning"},
    "Paladin": {"Might": "Offensive", "Prayer": "Defensive", "Sacrifice": "Combat"},
    "Barbarian": {"Bash": "Combat", "Sword Mastery": "Masteries", "Howl": "Warcries"},
    "Druid": {"Raven": "Summoning", "Werewolf": "Shape", "Firestorm": "Elemental"},
    "Assassin": {"Fire Blast": "Traps", "Claw Mastery": "Shadow", "Tiger Strike": "Martial"},
}
SHOWN_TOTALS = ("Strength", "Dexterity", "Life", "Mana", "MinimumDamage", "MaximumDamage",
                "AttackRating", "MinimumLifeStolenPerHit", "MinimumManaStolenPerHit", "IncreasedAttackSpeed",
                "FasterRunWalk", "FasterHitRecovery", "FasterCastRate", "ExtraGold",
                "BetterChanceOfGettingMagicItem")

Item = namedtuple("Item", "label stats base ethereal defense", defaults=(None,))
QUIVERS = {"bowq", "xboq"}
HAND_ONLY_STATS = {"EnhancedMaximumDamage", "EnhancedDamage", "MinimumDamage", "MaximumDamage",
                   "AttackRating", "ToHitPercent"}


# --- Sources -------------------------------------------------------------------------------------

def bot_folder():
    folder = os.environ.get("D2_BOT_FOLDER")
    if not folder:
        sys.exit("set --bot-folder or D2_BOT_FOLDER to the folder holding muleconfig.json")
    return Path(folder)


def psql():
    found = shutil.which("psql")
    if found:
        return found
    pattern = os.path.join(os.environ.get("ProgramFiles", "C:/Program Files"), "PostgreSQL", "*", "bin", "psql.exe")
    installed = sorted(glob.glob(pattern), key=lambda p: int(Path(p).parents[1].name.split(".")[0]))
    if not installed:
        sys.exit("psql not found on PATH or under Program Files/PostgreSQL")
    return installed[-1]


def connection():
    config = json.loads((bot_folder() / "muleconfig.json").read_text())
    parts = dict(p.split("=", 1) for p in config["mule"]["connectionString"].split(";") if p)
    return {k.lower(): v for k, v in parts.items()}


def expansion_accounts():
    path = bot_folder() / "muleconfig-expansion.json"
    if not path.exists():
        return set()
    return {a["username"].lower() for a in json.loads(path.read_text())["mule"]["accounts"]}


def query(sql):
    c = connection()
    env = {**os.environ, "PGPASSWORD": c.get("password", "")}
    out = subprocess.run([psql(), "-h", c.get("host", "localhost"), "-p", c.get("port", "5432"),
                          "-U", c.get("username", "postgres"), "-d", c.get("database", "d2ng"),
                          "-At", "-c", sql], capture_output=True, text=True, env=env, check=True)
    return [json.loads(line) for line in out.stdout.splitlines() if line]


def armory(path):
    with urllib.request.urlopen(f"{ARMORY}/{path}", timeout=30) as response:
        return json.load(response)


def stat_names():
    source = REPO / "src/D2NG.Core/D2GS/Items/StatType.cs"
    names = []
    for line in source.read_text(encoding="utf-8-sig").splitlines():
        match = re.match(r"\s*([A-Za-z0-9_]+)\s*(=\s*[^,]+)?,", line)
        if match:
            names.append(match.group(1))
    return names


def normalized(name):
    return re.sub(r"[^a-z0-9]", "", name.lower())


class GameData:
    """Bases and skills from the tables tools/MpqData writes into <bot folder>/data."""

    def __init__(self):
        data = bot_folder() / "data"
        affixes = data / "item-affixes.json"
        skills = data / "skills.json"
        if not affixes.exists() or not skills.exists():
            sys.exit(f"{affixes} or {skills} is missing: run tools/MpqData --out <bot folder>/data")
        bases = json.loads(affixes.read_text())["BaseItems"]
        self.base_by_code = {b["Code"].lower(): b for b in bases}
        self.base_by_name = {}
        for b in bases:
            self.base_by_name.setdefault(normalized(b["Name"]), b)
        self.skills = json.loads(skills.read_text())["Skills"]

    def skill(self, name, class_name=None):
        key = name.lower()[:16]
        for s in self.skills:
            if s["Name"].lower() == key and (class_name is None or s.get("Class") == class_name):
                return s
        return None


# --- Items ---------------------------------------------------------------------------------------

def tidy(stats):
    """The database repeats damage as plain, Throwing and Secondary (the two-handed stat); one of
    them carries the real value on a weapon, all of them the same value elsewhere."""
    stats = dict(stats)
    for bound in ("Minimum", "Maximum"):
        variants = [stats.pop(k, 0) for k in (f"{bound}Damage", f"{bound}ThrowingDamage", f"Secondary{bound}Damage")]
        if any(variants):
            stats[f"{bound}Damage"] = max(variants)
    if "AllSkills" in stats:
        stats = {k: v for k, v in stats.items() if k not in CLASS_SKILLS}
    return stats


def value(stats, key):
    if key == "Res":
        return sum(stats.get(r, 0) for r in RESISTS)
    return stats.get(key, 0)


def pairs(text):
    result = {}
    for part in (text or "").split(","):
        if part:
            key, number = part.split("=")
            result[key.strip()] = float(number)
    return result


def parse_weights(text):
    """Stat=W, or Stat=W@cap to value the stat only up to cap (a little mana leech is enough)."""
    result = {}
    for part in (text or "").split(","):
        if part:
            key, spec = part.split("=")
            weight, _, cap = spec.partition("@")
            result[key.strip()] = (float(weight), float(cap) if cap else None)
    return result


def score(stats, weights):
    return sum(w * (value(stats, k) if cap is None else min(value(stats, k), cap))
               for k, (w, cap) in weights.items())


def describe(stats):
    return ", ".join(f"{k} {v:g}" for k, v in stats.items())


def short(resist):
    return resist.replace("Resistance", "")


def db_items(where, realm, expansion_set, game):
    sql = "select data from mt_doc_muleitemdb" + (" where " + " and ".join(where) if where else "")
    for item in query(sql):
        is_expansion = item["AccountName"].lower() in expansion_set
        if realm == "classic" and is_expansion or realm == "expansion" and not is_expansion:
            continue
        base = game.base_by_name.get(normalized(item["ItemName"])) if game else None
        yield item, Item(holder(item), tidy({s["Type"]: s["Value"] for s in item["Stats"]}),
                         base["Code"].lower() if base else None, item.get("Ethereal", False))


def holder(item):
    return f"{item['AccountName']}/{item['CharacterName']} {item['QualityType']} {item['ItemName']} [{item['Id']}]"


# --- Characters ----------------------------------------------------------------------------------

class Character:
    def __init__(self, name):
        self.names = stat_names()
        d2s = armory(f"armory?character={name}")["character"]["d2s"]
        self.header = d2s["header"]
        self.class_name = self.header["class"]
        self.level = self.header["level"]
        self.account = d2s["account_name"]
        self.parsed = d2s.get("last_parsed")
        self.attributes = d2s["attributes"]
        self.expansion = self.header["expansion"]
        self.hard_points = {k["name"].lower(): k["points"] for k in d2s.get("skills", [])}
        self.armory_bonus = {k["name"].lower(): k["main_hand_bonus_points"] for k in d2s.get("skills", [])}
        self.slots = {}
        self.set_slots = set()
        self.set_bonus = self.stats(d2s.get("main_set_bonuses", {}).values())
        self.charms = self.stats(item.get("magic_attributes", []) for item in d2s["items"]
                                 if item.get("location_id") == 0 and item.get("position_id") == INVENTORY
                                 and item.get("type", "").lower() in CHARM_TYPES)
        scrolls = sum(1 for difficulty in self.header.get("quests", {}).values()
                      for quest in difficulty.get("act_v", {}).get("quests", [])
                      if quest["name"] == "Prison of Ice" and quest.get("consumed_scroll"))
        self.permanent = {r: 10 * scrolls for r in RESISTS} if scrolls else {}
        for item in d2s["items"]:
            slot = SLOTS.get(item.get("equipped_id"))
            if not slot:
                continue
            self.slots[slot] = Item(f"{item.get('name') or ''} {item['type_name']}".strip(),
                                    self.stats([item.get("magic_attributes", [])]),
                                    item.get("type", "").lower() or None, item.get("ethereal", False),
                                    item.get("defense_rating"))
            if item.get("set_id") is not None:
                self.set_slots.add(slot)
                for stat, amount in self.stats([item.get("main_set_partial_bonuses", [])]).items():
                    self.set_bonus[stat] = self.set_bonus.get(stat, 0) + amount

    def stats(self, groups):
        """Armory properties to StatType names. Set bonuses live outside magic_attributes, res-all
        arrives as one property (id -1) instead of four, "adds X-Y damage" as one property holding
        only X, a +tab label becomes <Class><Tab>Skills like the database's, and a per-level stat's
        value is the per-level rate while its label carries the amount at the character's level."""
        stats = {}

        def add(key, amount):
            stats[key] = stats.get(key, 0) + amount

        for properties in groups:
            for p in properties:
                label = p["name"]
                adds = ADDS_DAMAGE.search(label)
                tab = TAB_LABEL.search(label)
                per_level = PER_LEVEL.search(label)
                if p["id"] == -1 and "All Resistances" in label:
                    for r in RESISTS:
                        add(r, p["value"])
                elif adds:
                    element = "Magical" if adds.group(3) == "Magic" else adds.group(3)
                    add(f"Minimum{element}Damage", int(adds.group(1)))
                    add(f"Maximum{element}Damage", int(adds.group(2)))
                elif tab and tab.group(2) in CLASSES:
                    add(tab.group(2) + "".join(w.capitalize() for w in tab.group(1).split()) + "Skills", p["value"])
                elif per_level:
                    add(PER_LEVEL_STATS.get(per_level.group(2), per_level.group(2)), int(per_level.group(1)))
                elif p["id"] in ARMORY_STAT_IDS:
                    add(ARMORY_STAT_IDS[p["id"]], p["value"])
                elif 0 <= p["id"] < SHIFTED_STAT_IDS.start:
                    add(self.names[p["id"]], p["value"])
                elif p["id"] in SHIFTED_STAT_IDS:
                    add(self.names[p["id"] + 4], p["value"])
                else:
                    add(re.sub(r"[+-]?\d+%?\s*", "", label).strip(), p["value"])
        return tidy(stats)

    def set_intact(self, slots):
        return all(slot in slots and slots[slot].stats is self.slots[slot].stats for slot in self.set_slots)

    def describe(self):
        return (f"{self.header['name']} on {self.account}: level {self.level} {self.class_name}, "
                f"{'expansion' if self.expansion else 'classic'}, armory parsed {self.parsed}")


def frames(percent, table):
    """Frames an action takes at this percent: the base frames less one per breakpoint reached."""
    first, breakpoints = table
    return first - (sum(1 for b in breakpoints if percent >= b) - 1)


def diminishing(level, low, high):
    """The game's integer diminishing-returns curve, used by Critical Strike, Natural Resistance,
    Dodge, Avoid, Evade and the like."""
    return (110 * level // (level + 6)) * (high - low) // 100 + low if level > 0 else 0


class Evaluation:
    """Everything one set of gear adds up to: totals, skill levels, resists and damage."""

    def __init__(self, character, slots, game, skill_name=None, skill_bonus=0, levels=(), auras=()):
        self.character = character
        self.slots = slots
        self.game = game
        self.auras = auras
        self.total = self.sum_items()
        self.notes = []
        if character.hard_points.get("natural resistance"):
            nr = self.skill_level("Natural Resistance")
            params = self.skill_params("Natural Resistance", (0, 80))
            for r in RESISTS:
                self.total[r] = self.total.get(r, 0) + diminishing(nr, *params)
            self.total["Natural Resistance Level"] = nr
        self.defense()
        self.speeds_and_block()
        for name in levels:
            self.total[f"{name} Level"] = self.skill_level(name)
        if skill_name:
            self.damage(skill_name, skill_bonus)

    def sum_items(self):
        """Inventory charms and Anya's scrolls (+10 all resists per difficulty) always count. Set
        bonuses count only while every set piece stays on; swapping one drops them all, which is
        exact for a two-piece set and pessimistic for larger ones."""
        c = self.character
        result = {stat: c.attributes[key] for stat, key in BASE_ATTRIBUTES.items()}
        worn = [item.stats for item in self.slots.values()] + [c.charms, c.permanent]
        if c.set_intact(self.slots):
            worn.append(c.set_bonus)
        for stats in worn:
            for stat, amount in stats.items():
                result[stat] = result.get(stat, 0) + amount
        return result

    def skill_params(self, name, fallback):
        skill = self.game.skill(name, self.character.class_name) if self.game else None
        return (skill["Params"][0], skill["Params"][1]) if skill else fallback

    def skill_level(self, name, total=None):
        """Hard points plus all-skills, class-skills and the skill's tab. +skills never grant a
        skill without a hard point."""
        c = self.character
        total = self.total if total is None else total
        points = c.hard_points.get(name.lower(), 0)
        if not points:
            return 0
        bonus = total.get("AllSkills", 0) + total.get(c.class_name + "Skills", 0)
        skill = self.game.skill(name, c.class_name) if self.game else None
        if skill:
            word = self.tab_word(skill["Page"])
            bonus += sum(v for k, v in total.items()
                         if word and k.startswith(c.class_name) and k.endswith("Skills") and word in k)
        return points + bonus

    def tab_word(self, page):
        for rep, word in TAB_SKILLS.get(self.character.class_name, {}).items():
            skill = self.game.skill(rep, self.character.class_name)
            if skill and skill["Page"] == page:
                return word
        return None

    def requirements_met(self):
        """Strength and dexterity against every worn base, after reduced-requirement mods and the
        ethereal -10."""
        if not self.game:
            return True, None
        for slot in ("right hand", "left hand"):
            item, other = self.slots.get(slot), self.slots.get("left hand" if slot == "right hand" else "right hand")
            base = self.game.base_by_code.get(item.base or "") if item else None
            other_base = self.game.base_by_code.get(other.base or "", {}) if other else {}
            if (base and other and base["Kind"] == "Weapon" and base["TwoHandMinDamage"] and not base["MinDamage"]
                    and other_base.get("Type") not in QUIVERS):
                return False, f"{slot} {base['Name']} is two-handed and the other hand is not empty"
        for slot, item in self.slots.items():
            base = self.game.base_by_code.get(item.base or "")
            if not base:
                continue
            reduction = abs(item.stats.get("ReducedRequirements", 0))
            for stat, key in (("Strength", "RequiredStrength"), ("Dexterity", "RequiredDexterity")):
                need = base.get(key, 0) * (100 - reduction) // 100 - (10 if item.ethereal else 0)
                if need > 0 and self.total.get(stat, 0) < need:
                    return False, f"{slot} {base['Name']} needs {need} {stat.lower()}"
        return True, None

    def defense(self):
        """Each armor piece's defense times its enhanced defense, plus every flat defense bonus,
        plus a quarter of dexterity, times Iron Skin. An item with enhanced defense rolls at the
        base's maximum + 1 (every armory item checked); a mule item without one counts at the middle
        of the range, since the database does not keep its roll. Matched a screen to the point."""
        total = 0
        for item in self.slots.values():
            base = self.game.base_by_code.get(item.base or "") if self.game else None
            if not base or base["Kind"] != "Armor":
                continue
            ed = item.stats.get("EnhancedDefense", 0)
            rolled = item.defense if item.defense is not None else (
                base["MaxDefense"] + 1 if ed else (base["MinDefense"] + base["MaxDefense"]) // 2)
            total += rolled * (100 + ed) // 100
        total += self.total.get("Defense", 0) + self.total.get("Dexterity", 0) // 4
        iron_skin = self.skill_level("Iron Skin")
        bonus = 0
        if iron_skin:
            low, per = self.skill_params("Iron Skin", (30, 10))
            bonus = low + per * (iron_skin - 1)
        self.total["Defense Total"] = total * (100 + bonus) // 100

    def speeds_and_block(self):
        """Cast and hit recovery frames from the class tables, and the chance to block: (shield block
        + increased blocking + class block factor) x (dexterity - 15) / (2 x level), at most 75%.
        The block formula is 1.10's and not yet checked on a 1.09 screen."""
        c, t = self.character, self.total
        if c.class_name in CAST_BREAKPOINTS:
            t["Cast Frames"] = frames(t.get("FasterCastRate", 0), CAST_BREAKPOINTS[c.class_name])
            t["Hit Recovery Frames"] = frames(t.get("FasterHitRecovery", 0), HIT_RECOVERY_BREAKPOINTS[c.class_name])
        shield = next((item for slot, item in self.slots.items() if "hand" in slot and self.game
                       and self.game.base_by_code.get(item.base or "", {}).get("Block")), None)
        if shield:
            block = (self.game.base_by_code[shield.base]["Block"] + t.get("IncreasedBlocking", 0)
                     + BLOCK_FACTOR.get(c.class_name, 0))
            t["Block %"] = max(0, min(75, block * (t.get("Dexterity", 0) - 15) // (2 * c.level)))
        else:
            t["Block %"] = 0

    def aura_percent(self):
        """Damage percent from the paladin auras named with --aura, at the level the gear gives them."""
        total = 0
        for name in self.auras:
            rule = AURA_DAMAGE.get(name.lower())
            level = self.skill_level(name)
            if rule and level:
                params = (self.game.skill(name, self.character.class_name) or {}).get("Params", [0] * 6)
                total += rule(params, level)
                self.total[f"{name} Level"] = level
        return total

    def aura_to_hit(self):
        """Attack rating percent of the --aura auras: the skill's ToHit + LevelToHit per level after
        the first (Fanaticism 40 + 5/level, 180% at 29, confirmed on a screen; Blessed Aim's is in its
        parameters instead). Fanaticism's attack speed (10-40% on the diminishing curve, 37% at 29)
        is shown as Aura IAS."""
        total = 0
        for name in self.auras:
            skill = self.game.skill(name, self.character.class_name)
            level = self.skill_level(name)
            if not skill or not level:
                continue
            if name.lower() == "blessed aim":
                total += skill["Params"][2] + skill["Params"][3] * (level - 1)
            else:
                total += skill["ToHit"] + skill["LevelToHit"] * (level - 1)
            if name.lower() == "fanaticism":
                self.total["Aura IAS"] = diminishing(level, skill["Params"][2], skill["Params"][3])
        return total

    def shield_damage(self, skill_name, skill_bonus, weapon_base):
        """Smite hits with the shield's own damage (patched armor.bin), times 100 + the skill's percent
        + auras; strength and the weapon's damage do not count. Matched a screen to the point with
        Fanaticism running."""
        shield = next((item for slot, item in self.slots.items() if "hand" in slot
                       and self.game.base_by_code.get(item.base or "", {}).get("Block")), None)
        if not shield:
            self.notes.append(f"{skill_name} needs a shield")
            return
        base = self.game.base_by_code[shield.base]
        level = self.skill_level(skill_name)
        skill = self.game.skill(skill_name, self.character.class_name)
        skill_pct, _ = skill_physical(skill, level, self.notes)
        multiplier = 100 + skill_pct + self.aura_percent() + skill_bonus
        low, high = base["MinDamage"] * multiplier // 100, base["MaxDamage"] * multiplier // 100
        self.total[f"{skill_name} Level"] = level
        self.total["Physical"] = f"{low}-{high}"
        self.total["Damage"] = round((low + high) / 2, 1)
        self.total["Weapon Speed"] = weapon_base["Speed"]

    def hand_totals(self, other_hand):
        """Totals as one weapon sees them. With a weapon in each hand, the other weapon's damage
        and attack rating belong to its own swing; its +skills, strength and the rest count for both
        (screens confirmed both hands' attack rating and the main hand's damage)."""
        other = self.slots.get(other_hand)
        if not other or self.game.base_by_code.get(other.base or "", {}).get("Kind") != "Weapon":
            return self.total
        t = dict(self.total)
        for key, amount in other.stats.items():
            if key in HAND_ONLY_STATS:
                t[key] = t.get(key, 0) - amount
        return t

    def damage(self, skill_name, skill_bonus):
        """Damage per hit of one skill, as the character screen shows it, then averaged with crits
        and the elemental damage the gear adds. The 1.10 formula: weapon ED on the base, flat damage
        added, then stat bonus, off-weapon ED and the skill's percent on the lot."""
        c = self.character
        weapon_slot = next((s for s in ("right hand", "left hand")
                            if s in self.slots and self.game.base_by_code.get(self.slots[s].base or "", {}).get("Kind") == "Weapon"), None)
        if not weapon_slot:
            self.notes.append("no weapon found in either hand")
            return
        weapon = self.slots[weapon_slot]
        base = self.game.base_by_code[weapon.base]
        other_hand = "left hand" if weapon_slot == "right hand" else "right hand"
        t = self.hand_totals(other_hand)
        if skill_name.lower() in SHIELD_SKILLS:
            return self.shield_damage(skill_name, skill_bonus, base)
        both_hands = base["TwoHandMinDamage"] and other_hand not in self.slots
        # A barbarian sword held with the other hand empty swings with its two-handed damage,
        # confirmed on a character screen.
        low, high = ((base["TwoHandMinDamage"], base["TwoHandMaxDamage"]) if both_hands or not base["MinDamage"]
                     else (base["MinDamage"], base["MaxDamage"]))
        if weapon.ethereal:
            low, high = low * 3 // 2, high * 3 // 2
        weapon_ed = weapon.stats.get("EnhancedMaximumDamage", 0) + weapon.stats.get("EnhancedDamage", 0)
        if weapon_ed:
            # The game enhances base + 1, not the base: this matched 98 of 99 ladder weapons whose
            # damage the armory computes, and a character screen to the point.
            low, high = (low + 1) * (100 + weapon_ed) // 100, (high + 1) * (100 + weapon_ed) // 100
        low += t.get("MinimumDamage", 0)
        high += t.get("MaximumDamage", 0)
        high += t.get("MaximumDamagePerLevel", 0) * c.level // 8
        if low >= high:
            high = low + 1
        off_weapon_ed = (t.get("EnhancedMaximumDamage", 0) + t.get("EnhancedDamage", 0)) - weapon_ed
        stat_bonus = (t.get("Strength", 0) * base["StrengthBonus"] + t.get("Dexterity", 0) * base["DexterityBonus"]) // 100

        level = self.skill_level(skill_name, t) if skill_name.lower() not in ("attack", "normal attack") else 1
        skill = self.game.skill(skill_name, c.class_name) or self.game.skill(skill_name)
        if skill_name.lower() not in ("attack", "normal attack") and level == 0:
            self.notes.append(f"{skill_name} has no hard points")
        skill_pct, share = skill_physical(skill, level, self.notes)
        mastery_pct = mastery_ar = 0
        for name, (types, damage_rule, ar_rule) in WEAPON_MASTERIES.items():
            if c.hard_points.get(name) and base["Type"] in types:
                mastery_level = self.skill_level(name, t)
                mastery_pct, mastery_ar = damage_rule(mastery_level), ar_rule(mastery_level)
                self.total[f"{name.title()} Level"] = mastery_level
        unknown = [n for n, p in c.hard_points.items() if p and "mastery" in n and n not in WEAPON_MASTERIES
                   and not any((self.game.skill(n, c.class_name) or {}).get("Params", [1]))]
        if unknown and not skill_bonus:
            self.notes.append(f"{', '.join(unknown)}: damage rule unknown, so not counted (pass --skill-bonus)")
        multiplier = 100 + stat_bonus + off_weapon_ed + skill_pct + mastery_pct + self.aura_percent() + skill_bonus
        phys = (low * multiplier // 100 * share // 8, high * multiplier // 100 * share // 8)

        crit = diminishing(self.skill_level("Critical Strike"), *self.skill_params("Critical Strike", (5, 80))) \
            if c.hard_points.get("critical strike") else 0
        deadly = t.get("DeadlyStrike", 0) + t.get("DeadlyStrikePerLevel", 0) * c.level // 8
        double_chance = 1 - (1 - crit / 100) * (1 - min(deadly, 100) / 100)
        phys_avg = (phys[0] + phys[1]) / 2 * (1 + double_chance)

        elements = {}
        for element in ("Fire", "Lightning", "Cold", "Magical", "Poison"):
            lo, hi = t.get(f"Minimum{element}Damage", 0), t.get(f"Maximum{element}Damage", 0)
            if lo or hi:
                elements[element] = (lo, max(lo, hi))
        if skill and skill.get("Element") not in (None, "None"):
            lo, hi = skill_elemental(skill, level)
            name = {"Magic": "Magical"}.get(skill["Element"], skill["Element"])
            old = elements.get(name, (0, 0))
            elements[name] = (old[0] + lo, old[1] + hi)
        element_avg = sum((lo + hi) / 2 for name, (lo, hi) in elements.items() if name != "Poison")

        self.total[f"{skill_name} Level"] = level
        self.total["Critical Strike %"] = crit
        self.total["Double Damage %"] = round(double_chance * 100)
        self.total["Physical"] = f"{phys[0]}-{phys[1]}"
        self.total["Damage"] = round(phys_avg + element_avg, 1)
        self.total["Weapon Speed"] = base["Speed"]
        skill_to_hit = (skill["ToHit"] + skill["LevelToHit"] * (level - 1)) if skill and level > 0 and skill_name.lower() not in ("attack", "normal attack") else 0
        ar_base = t.get("AttackRating", 0) + 4 * (t.get("Dexterity", 0) - 7) + TO_HIT_FACTOR.get(c.class_name, 0)
        self.total["Attack Rating"] = ar_base * (100 + mastery_ar + t.get("ToHitPercent", 0) + skill_to_hit
                                                 + self.aura_to_hit()) // 100
        if elements:
            self.total["Elemental"] = ", ".join(f"{k} {lo}-{hi}" for k, (lo, hi) in elements.items())


# 1.09 keeps each skill's damage rule in the game code; these follow its parameter descriptions,
# as base + per level after the first. Guided Arrow is confirmed on a character screen, the rest
# are not. Unlisted attack skills count as a plain attack.
SKILL_PERCENT = {
    "Guided Arrow": lambda p, l: p[3] * (l - 1),
    "Strafe": lambda p, l: p[0] + p[1] * (l - 1),
    "Jab": lambda p, l: p[2] + p[3] * (l - 1),
    "Fend": lambda p, l: p[2] + p[3] * (l - 1),
    "Bash": lambda p, l: p[0] + p[1] * (l - 1),
    "Concentrate": lambda p, l: p[0] + p[1] * (l - 1),
    "Frenzy": lambda p, l: p[0] + p[1] * (l - 1),
    "Whirlwind": lambda p, l: p[0] + p[1] * (l - 1),
    "Berserk": lambda p, l: p[0] + p[1] * (l - 1),
    "Sacrifice": lambda p, l: p[0] + p[1] * (l - 1),
    "Smite": lambda p, l: p[2] + p[3] * (l - 1),
    "Charge": lambda p, l: p[2] + p[3] * (l - 1),
    "Fury": lambda p, l: p[2] + p[3] * (l - 1),
    "Dragon Claw": lambda p, l: p[0] + p[1] * (l - 1),
    "Dragon Flight": lambda p, l: p[0] + p[1] * (l - 1),
}
# Weapon masteries carry no numbers in skills.bin. Damage is 28% at level 1 plus 5% per level after,
# confirmed on screens for Sword 22, Mace 21 and Axe 23. Attack rating is 28% (sword, axe, mace) or
# 30% (spear, pole arm) at level 1 plus 8% per level after, confirmed for Sword 24, Mace 21, Axe 23
# and Spear 22. Throwing Mastery is left out.
WEAPON_MASTERIES = {
    "sword mastery": ({"swor"}, lambda l: 28 + 5 * (l - 1), lambda l: 28 + 8 * (l - 1)),
    "axe mastery": ({"axe"}, lambda l: 28 + 5 * (l - 1), lambda l: 28 + 8 * (l - 1)),
    "mace mastery": ({"mace", "club", "hamm"}, lambda l: 28 + 5 * (l - 1), lambda l: 28 + 8 * (l - 1)),
    "pole arm mastery": ({"pole"}, lambda l: 28 + 5 * (l - 1), lambda l: 30 + 8 * (l - 1)),
    "spear mastery": ({"spea"}, lambda l: 28 + 5 * (l - 1), lambda l: 30 + 8 * (l - 1)),
}
# Attack rating in 1.09 counts 4 per dexterity point above 7 (the 1.10 code says 5; four screens
# say 4), plus charstats ToHitFactor, plus flat attack rating, times 100 + the percent bonuses.
# Frame breakpoints: the percent that buys each frame, from the first (0%) to the last. These are the
# published 1.10 tables, which use the same animation data as 1.09; not yet checked on a 1.09 screen.
CAST_BREAKPOINTS = {
    "Amazon": (19, [0, 7, 14, 22, 32, 48, 68, 99, 152]),
    "Sorceress": (13, [0, 9, 20, 37, 63, 105, 200]),
    "Necromancer": (15, [0, 9, 18, 30, 48, 75, 125]),
    "Paladin": (15, [0, 9, 18, 30, 48, 75, 125]),
    "Barbarian": (13, [0, 9, 20, 37, 63, 105, 200]),
    "Druid": (18, [0, 4, 10, 19, 30, 46, 68, 99, 163]),
    "Assassin": (16, [0, 8, 16, 27, 42, 65, 102, 174]),
}
HIT_RECOVERY_BREAKPOINTS = {
    "Amazon": (11, [0, 6, 13, 20, 32, 52, 86, 174, 600]),
    "Sorceress": (15, [0, 5, 9, 14, 20, 30, 42, 60, 86, 142, 280]),
    "Necromancer": (13, [0, 5, 10, 16, 26, 39, 56, 86, 152, 377]),
    "Paladin": (9, [0, 7, 15, 27, 48, 86, 200]),
    "Barbarian": (9, [0, 7, 15, 27, 48, 86, 200]),
    "Druid": (13, [0, 5, 10, 16, 26, 39, 56, 86, 152, 377]),
    "Assassin": (9, [0, 7, 15, 27, 48, 86, 200]),
}
# charstats BlockFactor, added to the shield's own block.
BLOCK_FACTOR = {"Amazon": 25, "Sorceress": 20, "Necromancer": 20, "Paladin": 30, "Barbarian": 25,
                "Druid": 20, "Assassin": 25}
TO_HIT_FACTOR = {"Amazon": 5, "Sorceress": -15, "Necromancer": -10, "Paladin": 20, "Barbarian": 20,
                 "Druid": 5, "Assassin": 15}
SHIELD_SKILLS = {"smite"}
# Paladin auras that add damage percent, base + per level after the first, from their skills.bin
# parameter descriptions. Fanaticism is confirmed on a screen (with Smite); Might and Concentration
# are not.
AURA_DAMAGE = {
    "might": lambda p, l: p[2] + p[3] * (l - 1),
    "concentration": lambda p, l: p[2] + p[3] * (l - 1),
    "fanaticism": lambda p, l: p[4] + p[5] * (l - 1),
}
PLAIN_ATTACKS = {"attack", "normal attack", "multiple shot", "zeal", "double swing", "stun", "double throw"}


def skill_physical(skill, level, notes):
    """The skill's damage percent, and the eighths of weapon damage it keeps (Multiple Shot 6)."""
    if not skill:
        return 0, 8
    name = skill["Name"]
    share = skill["SourceDamage"] if 0 < skill["SourceDamage"] < 8 else 8
    rule = next((f for k, f in SKILL_PERCENT.items() if k.lower()[:16] == name.lower()), None)
    if rule:
        return rule(skill["Params"], max(level, 1)), share
    if name.lower() not in PLAIN_ATTACKS and skill.get("Element") in (None, "None"):
        notes.append(f"{name}: damage rule unknown, counted as a plain attack (pass --skill-bonus)")
    return 0, share


def skill_elemental(skill, level):
    """EMin/EMax plus ELev per level for levels 2-8, 9-16 and 17 on, in 1/256ths << HitShift."""
    added = 0
    for lvl in range(2, level + 1):
        added += skill["ElementLevel"][0 if lvl <= 8 else 1 if lvl <= 16 else 2]
    shift = skill.get("HitShift", 8)
    return ((skill["ElementMin"] + added) << shift) // 256, ((skill["ElementMax"] + added) << shift) // 256


def resists(total, expansion, difficulty):
    penalty = PENALTY[expansion][difficulty]
    result = {}
    for r in RESISTS:
        raw = total.get(r, 0) + penalty
        cap = RESIST_CAP + total.get(MAX_RESISTS[r], 0)
        result[r] = (raw, min(raw, cap), cap)
    return result


def resist_line(table):
    return ", ".join(f"{short(r)} {eff}" + (f" (+{raw - cap} over)" if raw > cap else "")
                     for r, (raw, eff, cap) in table.items())


# --- Commands ------------------------------------------------------------------------------------

def load_game():
    return GameData()


def evaluate(character, slots, game, args):
    levels = [k[:-len(" Level")] for k in parse_weights(getattr(args, "weights", None)) if k.endswith(" Level")]
    auras = [a.strip() for a in (getattr(args, "aura", None) or "").split(",") if a.strip()]
    return Evaluation(character, slots, game, getattr(args, "skill", None), getattr(args, "skill_bonus", 0), levels, auras)


DERIVED = ("Natural Resistance Level", "Aura IAS", "Cast Frames", "Hit Recovery Frames", "Block %", "Defense Total", "Attack Rating", "Critical Strike %", "Double Damage %", "Physical", "Damage",
           "Elemental", "Weapon Speed")


def derived_keys(total, skill_name):
    keys = [k for k in total if k.endswith(" Level")]
    return keys + [k for k in DERIVED if k in total and k not in keys]


def cmd_items(args):
    where = []
    if args.type:
        where.append("data->>'ClassificationType' in (%s)" % ",".join(f"'{t}'" for t in args.type.split(",")))
    if args.quality:
        where.append("data->>'QualityType' in (%s)" % ",".join(f"'{q}'" for q in args.quality.split(",")))
    if args.name:
        where.append(f"data->>'ItemName' = '{args.name}'")
    has = [h for h in (args.has or "").split(",") if h]
    minimum = pairs(args.min)
    weights = parse_weights(args.weights)
    rows = []
    for item, entry in db_items(where, args.realm, expansion_accounts(), None):
        stats = entry.stats
        if any(value(stats, h) <= 0 for h in has) or any(value(stats, k) < v for k, v in minimum.items()):
            continue
        rows.append((score(stats, weights), item, stats))
    rows.sort(key=lambda r: -r[0])
    print(f"{len(rows)} items")
    for points, item, stats in rows[:args.top]:
        print(f"{points:7.1f}  {holder(item)} ilvl {item['Level']}: {describe(stats)}")


def cmd_gear(args):
    game = load_game()
    character = Character(args.character)
    print(character.describe())
    a = character.attributes
    print(f"base: str {a['strength']} dex {a['dexterity']} vit {a['vitality']} enr {a['energy']} "
          f"life {a['max_hp']} mana {a['max_mana']}")
    weights = parse_weights(args.weights)
    for slot, item in character.slots.items():
        print(f"{score(item.stats, weights):7.1f}  {slot}: {item.label}: {describe(item.stats)}")
    e = evaluate(character, character.slots, game, args)
    if character.permanent:
        print(f"         Anya's scrolls: all resists +{character.permanent[RESISTS[0]]}")
    if character.charms:
        print(f"         inventory charms: {describe(character.charms)}")
    if character.set_bonus:
        print(f"         set bonus ({', '.join(sorted(character.set_slots))}): {describe(character.set_bonus)}")
    for name in ("Natural Resistance", "Critical Strike", args.skill):
        if name and character.hard_points.get(name.lower()):
            level = e.skill_level(name)
            armory_level = character.hard_points[name.lower()] + character.armory_bonus.get(name.lower(), 0)
            print(f"         {name} level {level} (armory says {armory_level})")
    shown = derived_keys(e.total, args.skill) + list(LABELS)
    print("totals: " + ", ".join(f"{LABELS.get(k, k)} {e.total.get(k, 0)}" for k in shown))
    for note in e.notes:
        print(f"         note: {note}")
    print(f"{args.difficulty} resists: {resist_line(resists(e.total, character.expansion, args.difficulty))}")


def replaced_slots(character, replacements):
    slots = dict(character.slots)
    for slot, item in replacements.items():
        if item is None:
            slots.pop(slot, None)
        else:
            slots[slot] = item
    return slots


LABELS = {"MinimumLifeStolenPerHit": "Life Leech %", "MinimumManaStolenPerHit": "Mana Leech %"}


def compare(before, after, difficulty, skill_name):
    lines = []
    for stat in SHOWN_TOTALS + tuple(derived_keys(after.total, skill_name)):
        old, new = before.total.get(stat, 0), after.total.get(stat, 0)
        if old != new:
            label = LABELS.get(stat, stat)
            lines.append(f"{label} {old:g} -> {new:g}" if isinstance(new, (int, float)) and isinstance(old, (int, float))
                         else f"{label} {old} -> {new}")
    r_before = resists(before.total, before.character.expansion, difficulty)
    r_after = resists(after.total, after.character.expansion, difficulty)
    lines.append(f"{difficulty} resists: " + ", ".join(
        f"{short(r)} {r_before[r][1]} -> {r_after[r][1]}" + (f" (+{r_after[r][0] - r_after[r][2]} over)"
                                                            if r_after[r][0] > r_after[r][2] else "")
        for r in RESISTS))
    ok, reason = after.requirements_met()
    if not ok:
        lines.append(f"REQUIREMENT NOT MET: {reason}")
    lines.extend(f"note: {n}" for n in after.notes if n not in before.notes)
    return lines


def parse_replacements(specs, game):
    replacements = {}
    for spec in specs:
        slot, item_id = (s.strip() for s in spec.split("=", 1))
        if slot not in SLOTS.values():
            sys.exit(f"unknown slot {slot}")
        if item_id.lower() == "none":
            replacements[slot] = None
            continue
        rows = list(db_items([f"id = '{item_id}'"], "any", set(), game))
        if not rows:
            sys.exit(f"no mule item {item_id}")
        replacements[slot] = rows[0][1]
    return replacements


def cmd_calc(args):
    game = load_game()
    character = Character(args.character)
    print(character.describe())
    replacements = parse_replacements(args.replace, game)
    for slot, item in replacements.items():
        old = character.slots.get(slot)
        print(f"{slot}: {old.label + ': ' + describe(old.stats) if old else '(empty)'}")
        print(f"  -> {item.label + ': ' + describe(item.stats) if item else '(empty)'}")
    before = evaluate(character, character.slots, game, args)
    after = evaluate(character, replaced_slots(character, replacements), game, args)
    for line in compare(before, after, args.difficulty, args.skill):
        print(line)


def resist_score(table, res_weights, overflow):
    """Each effective point under the cap at its weight, and points over the cap at --overflow."""
    total = 0
    for r, (raw, effective, cap) in table.items():
        total += res_weights.get(short(r), 0) * effective
        total += overflow.get(short(r), 0) * max(0, raw - cap)
    return total


def cmd_combo(args):
    game = load_game()
    character = Character(args.character)
    print(character.describe())
    weights = parse_weights(args.weights)
    res_weights = pairs(args.res_weights)
    overflow = pairs(args.overflow)
    floor_override = pairs(args.floor)
    minimum_total = pairs(args.min_total)
    realm = "expansion" if character.expansion else "classic"
    current = evaluate(character, character.slots, game, args)
    current_res = resists(current.total, character.expansion, args.difficulty)
    floors = {r: floor_override.get(short(r), current_res[r][1] if res_weights.get(short(r), 0) > 0 else -999)
              for r in RESISTS}
    print("resist floors: " + ", ".join(f"{short(r)} {v:g}" for r, v in floors.items() if v > -999))
    expansion_set = expansion_accounts()
    fixed = parse_replacements(args.replace, game)
    for slot, item in fixed.items():
        print(f"fixed {slot}: {item.label + ': ' + describe(item.stats) if item else '(empty)'}")
    candidates = {}
    for spec in args.slot:
        slot, types = (s.strip() for s in spec.split("=", 1))
        where = ["data->>'ClassificationType' in (%s)" % ",".join(f"'{t}'" for t in types.split(",")),
                 "jsonb_array_length(data->'Stats') > 0"]
        excluded = {i.strip() for i in (args.exclude or "").split(",") if i.strip()}
        found = [entry for item, entry in db_items(where, realm, expansion_set, game) if item["Id"] not in excluded]
        found.sort(key=lambda c: -(score(c.stats, weights) + value(c.stats, "Res") * args.res_prune
                                   + c.stats.get("EnhancedMaximumDamage", 0) * weights.get("Damage", (0, None))[0]))
        keep = character.slots.get(slot)
        keep = keep._replace(label="(keep current) " + keep.label) if keep else Item("(keep empty)", {}, None, False)
        candidates[slot] = [keep] + found[:args.per_slot]
        print(f"{slot}: {len(found)} candidates, best {args.per_slot} kept")
    rows = []
    slot_names = list(candidates)
    for chosen in itertools.product(*(candidates[s] for s in slot_names)):
        labels = [c.label for c in chosen]
        if len(set(labels)) < len(labels):
            continue
        slots = replaced_slots(character, {**fixed, **dict(zip(slot_names, chosen))})
        e = evaluate(character, slots, game, args)
        if any(e.total.get(k, 0) < v for k, v in minimum_total.items()) or not e.requirements_met()[0]:
            continue
        table = resists(e.total, character.expansion, args.difficulty)
        if any(table[r][1] < floors[r] for r in RESISTS):
            continue
        rows.append((score(e.total, weights) + resist_score(table, res_weights, overflow), chosen, e))
    rows.sort(key=lambda r: -r[0])
    base = score(current.total, weights) + resist_score(current_res, res_weights, overflow)
    print(f"{len(rows)} combinations meet the floors and requirements; current score {base:.1f}")
    for points, chosen, e in rows[:args.top]:
        print(f"\n{points - base:+.1f}")
        for slot, item in zip(slot_names, chosen):
            print(f"  {slot}: {item.label}: {describe(item.stats)}")
        for line in compare(current, e, args.difficulty, args.skill):
            print(f"    {line}")


def cmd_account(args):
    print(", ".join(armory(f"account?account={args.account}")["characters"]))


def cmd_realm(args):
    for name in args.characters:
        d2s = armory(f"armory?character={name}")["character"]["d2s"]
        header = d2s["header"]
        print(f"{name}: {d2s['account_name']}, {'expansion' if header['expansion'] else 'classic'}, "
              f"level {header['level']}, armory parsed {d2s.get('last_parsed')}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bot-folder", help="folder holding muleconfig.json; default $D2_BOT_FOLDER")
    sub = parser.add_subparsers(dest="command", required=True)

    def with_character(name, run):
        command = sub.add_parser(name)
        command.add_argument("character")
        command.add_argument("--difficulty", choices=("normal", "nightmare", "hell"), default="hell")
        command.add_argument("--weights", help="Stat=W weights for the score; Stat=W@cap stops counting above cap")
        command.add_argument("--skill", help='attack or spell to compute damage for, e.g. "Guided Arrow" or Attack')
        command.add_argument("--aura", help="paladin damage auras running, e.g. Fanaticism or Might")
        command.add_argument("--skill-bonus", type=int, default=0,
                             help="extra damage percent the tables do not carry (weapon masteries, auras)")
        command.set_defaults(run=run)
        return command

    items = sub.add_parser("items")
    items.add_argument("--type", help="ClassificationType list, e.g. Ring,Amulet")
    items.add_argument("--quality", help="QualityType list: Magical, Rare, Crafted, Set, Unique, Normal, Superior")
    items.add_argument("--name", help="ItemName, e.g. RuneBow")
    items.add_argument("--realm", choices=("classic", "expansion", "any"), default="any")
    items.add_argument("--has", help="stats that must be present")
    items.add_argument("--min", help="Stat=N minimums")
    items.add_argument("--weights", help="Stat=W weights for the score; Stat=W@cap stops counting above cap")
    items.add_argument("--top", type=int, default=25)
    items.set_defaults(run=cmd_items)
    with_character("gear", cmd_gear)
    calc = with_character("calc", cmd_calc)
    calc.add_argument("--replace", action="append", default=[], help='"<slot>=<mule item id>" or "<slot>=none"')
    combo = with_character("combo", cmd_combo)
    combo.add_argument("--replace", action="append", default=[], help='fixed "<slot>=<mule item id>" or "<slot>=none"')
    combo.add_argument("--slot", action="append", required=True, help='"<slot>=<ClassificationType>[,...]"')
    combo.add_argument("--floor", help="Fire=75,... resist floors after the penalty; default: where the character stands now, for every weighted resist")
    combo.add_argument("--res-weights", default="Fire=1,Cold=1,Lightning=1,Poison=0",
                       help="score per effective (capped) resist point; the default is the PvP consensus")
    combo.add_argument("--overflow", default="Cold=0.3", help="score per resist point above the cap")
    combo.add_argument("--min-total", help="Strength=63,... totals beyond the base requirements")
    combo.add_argument("--res-prune", type=float, default=0.3, help="resist weight used to pick each slot's candidates")
    combo.add_argument("--exclude", help="mule item ids to leave out, e.g. ones already given to another character")
    combo.add_argument("--per-slot", type=int, default=150)
    combo.add_argument("--top", type=int, default=8)
    account = sub.add_parser("account")
    account.add_argument("account")
    account.set_defaults(run=cmd_account)
    realm = sub.add_parser("realm")
    realm.add_argument("characters", nargs="+")
    realm.set_defaults(run=cmd_realm)
    args = parser.parse_args()
    if args.bot_folder:
        os.environ["D2_BOT_FOLDER"] = args.bot_folder
    args.run(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
