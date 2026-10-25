"""The mock host against the kernel host it stands in for (Lane P's CommandHost.cs and creation_authority.gd).

Run 2 swaps the mock for the kernel host behind the same link, so every number both of them fix must be
the same. These tests read the kernel's own constants (read-only) and fail when either side drifts, then
check the mock's default policy at those boundaries.
"""
from __future__ import annotations

import re
import unittest

from support import COMPANION, PLAYER, REPO, FakeClock, HostPolicy, command, new_host, query

from garage_game_companion.mock_host import Receipt

COMMAND_HOST = REPO / "game" / "scripts" / "native" / "Kernel" / "CommandHost.cs"
AUTHORITY = REPO / "game" / "scripts" / "creation_authority.gd"


def _constant(path, pattern: str) -> int:
    match = re.search(pattern, path.read_text(encoding="utf-8"))
    if match is None:
        raise AssertionError(f"{path.name} no longer declares {pattern!r}; update this test with the kernel")
    return int(match.group(1))


@unittest.skipUnless(COMMAND_HOST.is_file() and AUTHORITY.is_file(), "the kernel host is not in this checkout")
class PolicyMatchesTheKernel(unittest.TestCase):
    def test_the_mock_hover_and_default_name_match_the_real_gubble(self):
        from garage_game_companion.mock_host import HOVER_M
        body = (REPO / "game/scripts/native/SmallPlayerController.cs").read_text(encoding="utf-8")
        avatar = (REPO / "game/scripts/native/CompanionAvatar.cs").read_text(encoding="utf-8")
        hover = re.search(r"\bfloat HoverHeightM \{ get; set; \} = ([0-9.]+)f;", body)
        self.assertIsNotNone(hover)
        self.assertEqual(HOVER_M, float(hover.group(1)))
        name = re.search(r'\bconst string DefaultName = "([^"]+)";', avatar)
        self.assertIsNotNone(name)
        self.assertEqual(new_host().entities["avatar:companion"].display_name, name.group(1))

    def test_the_default_policy_uses_the_kernel_hosts_numbers(self):
        policy = HostPolicy()
        cs = lambda name: _constant(COMMAND_HOST, rf"\bconst int {name} = (\d+);")  # noqa: E731
        gd = lambda name: _constant(AUTHORITY, rf"\bconst {name} := (\d+)\b")  # noqa: E731
        expected = {
            "max_pending_approvals": cs("MaxPendingApprovals"),
            "companion_messages_per_s": cs("CompanionMessagesPerSecond"),
            "max_transient_receipts_per_principal": cs("MaxTransientPerPrincipal"),
            "perception_memory_entries": cs("PerceptionMemoryEntries"),
            "perception_memory_stale_after_s": cs("PerceptionMemoryStaleAfterS"),
            "max_jobs_per_principal": cs("MaxJobsPerPrincipal"),
            "max_durable_receipts": gd("MAX_RECEIPTS"),
            "player_receipt_reserve": gd("PLAYER_RECEIPT_RESERVE"),
            "max_compacted_receipts": gd("MAX_COMPACTED"),
            "max_checkpoints": gd("MAX_CHECKPOINTS"),
            "approval_ttl_s": 60 * _constant(COMMAND_HOST, r"ApprovalLifetime = TimeSpan\.FromMinutes\((\d+)\)"),
        }
        self.assertEqual({name: getattr(policy, name) for name in expected}, expected)
        # Run 2: the walking goals' timeout, the team's shared sight and the journal's bounds.
        journal = REPO / "game" / "scripts" / "native" / "Kernel" / "CommandHostJournal.cs"
        self.assertEqual(policy.unreachable_after_s,
                         float(_constant(COMMAND_HOST, r"\bconst double UnreachableAfterS = (\d+)\.0;")))
        self.assertIn("public static bool DefaultSharedSight { get; set; } = true;", COMMAND_HOST.read_text(encoding="utf-8"))
        self.assertTrue(policy.shared_sight)
        from garage_game_companion import mock_journal
        self.assertEqual((mock_journal.MAX_OPEN_TASKS, mock_journal.MAX_HISTORY, mock_journal.MAX_NOTES),
                         tuple(_constant(journal, rf"\bconst int {name} = (\d+);") for name in ("MaxOpenTasks", "MaxHistory", "MaxNotes")))
        from garage_game_companion.mock_host import WALKING_GOALS
        self.assertIn('WalkingGoals = new() { "come", "go_to", "fetch" }', COMMAND_HOST.read_text(encoding="utf-8"))
        self.assertEqual(WALKING_GOALS, {"come", "go_to", "fetch"})
        # The activation receipts the authority keeps are bounded the same way as the host's transient ones.
        self.assertEqual(gd("MAX_TRANSIENT_PER_PRINCIPAL"), policy.max_transient_receipts_per_principal)

    def test_observe_sees_the_whole_island_and_bounds_a_given_radius_as_the_contract_does(self):
        """The 10x world (S2): observe has no distance cut-off unless radius_m narrows it, and the contract, the kernel
        and the mock bound radius_m alike."""
        import json
        import math
        args = json.loads((REPO / "contracts" / "game-command.schema.json").read_bytes())["$defs"]["args"]["observe"]
        maximum = _constant(COMMAND_HOST, r"\bconst int ObserveRadiusMaxM = (\d+);")
        self.assertEqual((args["properties"]["radius_m"]["maximum"], maximum), (150, 150))
        self.assertNotIn("radius_m", args["required"])
        source = COMMAND_HOST.read_text(encoding="utf-8")
        self.assertIn('var radius = args.TryGetProperty("radius_m", out var r) ? (float)CanonicalJson.ReadNumber(r) : float.PositiveInfinity;', source)
        self.assertIn('radius.GetDouble() is <= 0 or > ObserveRadiusMaxM', source)
        self.assertEqual(HostPolicy().observe_max_radius_m, math.inf)

    def test_near_reaches_as_far_in_the_contract_the_kernel_and_the_mock(self):
        """Play distances (S8): entities.list's near.radius_m is at most 150 m (was 50) in the contract, the kernel and the mock."""
        import json
        near = json.loads((REPO / "contracts" / "game-command.schema.json").read_bytes())["$defs"]["args"]["entities.list"]
        maximum = near["properties"]["filter"]["properties"]["near"]["properties"]["radius_m"]["maximum"]
        self.assertEqual((maximum, _constant(COMMAND_HOST, r"\bconst int NearRadiusMaxM = (\d+);")), (150, 150))
        host = new_host()
        reach = {"filter": {"near": {"center_m": [0, 0, 0], "radius_m": 150}}}
        self.assertTrue(host.handle(COMPANION, query("entities.list", reach))["ok"])
        past = host.handle(COMPANION, query("entities.list", {"filter": {"near": {"center_m": [0, 0, 0], "radius_m": 151}}}, "q-2"))
        self.assertEqual((past["ok"], past["error"]["field_path"]), (False, "$.args.filter.near.radius_m"))

    def test_entity_ids_are_the_contracts_in_the_kernel(self):
        """A plant copy's id (plant:<set>-<row>) is well formed in both hosts, so both answer target_not_found for a copy
        they do not know, and both refuse a second spelling of the same copy."""
        import json
        common = json.loads((REPO / "contracts" / "common.schema.json").read_bytes())["$defs"]
        kernel = re.search(r'Regex EntityId = new\(@"\\A(.+)\\z", RegexOptions\.Compiled\);', COMMAND_HOST.read_text(encoding="utf-8"))
        self.assertIsNotNone(kernel)
        self.assertEqual(kernel.group(1), common["entity_id"]["pattern"].removeprefix("^").removesuffix("$"))
        self.assertTrue(common["plant_id"]["pattern"].removeprefix("^").removesuffix("$") in kernel.group(1))
        host = new_host()
        known = host.handle(COMPANION, query("entity.inspect", {"target": "plant:broadleaf-0"}))
        self.assertEqual(known["error"]["code"], "target_not_found")
        # A second spelling of the same copy is malformed in both (the kernel's CommandHostTest checks request_invalid;
        # the mock maps any argument the schema refuses to invalid_args, for every malformed id alike).
        second = host.handle(COMPANION, query("entity.inspect", {"target": "plant:broadleaf-007"}, "q-2"))
        self.assertEqual((second["ok"], second["error"]["field_path"]), (False, "$.args.target"))
        self.assertNotEqual(second["error"]["code"], "target_not_found")


KERNEL = REPO / "game" / "scripts" / "native"
EFFECTS = KERNEL / "Kernel" / "CommandHostEffects.cs"
RULES = KERNEL / "Kernel" / "IslandRules.cs"


def _text(path) -> str:
    return path.read_text(encoding="utf-8")


def _match(path, pattern: str) -> str:
    match = re.search(pattern, _text(path))
    if match is None:
        raise AssertionError(f"{path.name} no longer declares {pattern!r}; update this test with the kernel")
    return match.group(1)


def _method(source: str, signature: str) -> str:
    """The body of one C# method: from its signature to the next member at the same indentation."""
    start = source.index(signature)
    end = re.search(r"\n    (?:private|public|internal)\b", source[start + len(signature):])
    return source[start:start + len(signature) + (end.start() if end else len(source))]


@unittest.skipUnless(EFFECTS.is_file() and RULES.is_file(), "the kernel host's abilities are not in this checkout")
class GlowMatchesTheKernel(unittest.TestCase):
    """The island's abilities (Run 2, Glow): the mock's effect.start and capabilities.list against the kernel's."""

    def test_the_effect_numbers_are_the_kernels(self):
        from garage_game_companion import mock_host as m
        expected = {
            "max_active_effects": _constant(COMMAND_HOST, r"\bconst int MaxActiveEffects = (\d+);"),
            "CAPABILITIES_DEFAULT_LIMIT": _constant(COMMAND_HOST, r"\bconst int CapabilitiesDefaultLimit = (\d+);"),
            "EFFECT_SIGHT_HALF_WIDTH_M": float(_match(COMMAND_HOST, r"\bconst float EffectSightHalfWidthM = ([0-9.]+)f;")),
            "WISP_LIFT_M": float(_match(KERNEL / "Look" / "LookDirector.Glow.cs", r"\bconst float WispLiftM = ([0-9.]+)f;")),
            "MAX_RULES_PACK_BYTES": _constant(RULES, r"\bconst int MaxPackBytes = (\d+);"),
            "DEFAULT_RULES_ID": _match(KERNEL / "RoomWorld.cs", r'\bconst string DefaultRulesId = "([a-z0-9_]+)";'),
            "DEFAULT_RULES_VERSION": _constant(KERNEL / "RoomWorld.cs", r"\bconst int DefaultRulesVersion = (\d+);"),
            "SEA_EXTENSION": _match(KERNEL / "Room" / "RoomSea.cs", r'\bconst string ExtensionName = "([a-z0-9_]+)";'),
            "GUBBLE": _match(COMMAND_HOST, r'\bconst string CompanionAvatarId = "([a-z:]+)";'),
            "EFFECT_ID_ALPHABET": _match(EFFECTS, r'"effect:" \+ System\.Security\.Cryptography\.RandomNumberGenerator\.GetString\("([a-z2-7]+)", \d+\)'),
            "EFFECT_ID_LENGTH": int(_match(EFFECTS, r'RandomNumberGenerator\.GetString\("[a-z2-7]+", (\d+)\)')),
        }
        actual = {name: getattr(HostPolicy(), name) if name == "max_active_effects" else getattr(m, name) for name in expected}
        self.assertEqual(actual, expected)

    def test_the_mock_reads_the_pack_the_kernel_loads(self):
        import hashlib
        from garage_game_companion.mock_host import rules_path
        self.assertIn('=> $"res://rules/{rulesId}/v{rulesVersion}.json";', _text(RULES))
        # The kernel's default (RoomWorld.DefaultRulesVersion; v4, then v5 once the mechanics round's M1 lands it).
        version = _constant(KERNEL / "RoomWorld.cs", r"\bconst int DefaultRulesVersion = (\d+);")
        path = rules_path()
        self.assertEqual(path, REPO / "game" / "rules" / "storybook_wild" / f"v{version}.json")
        host = new_host()
        self.assertEqual((host.rules.rules_id, host.rules.rules_version, host.rules.sha256),
                         ("storybook_wild", version, hashlib.sha256(path.read_bytes()).hexdigest()))
        self.assertEqual([(a.capability, a.primitive) for a in host.rules.abilities],
                         [("glow", "light.emit"), ("bubbles", "particles.float"), ("fireworks", "particles.burst"),
                          ("grow", "growth.grow"), ("fire", "element.ignite")])

    def test_the_mock_loads_the_primitives_the_kernel_carries_out(self):
        """The mock checks a pack against the island-rules schema; the kernel against its own table of primitives
        (IslandRules.Primitives) and then hands each to the look by primitive (StartLook). All three must name the same
        primitives with the same outer limits, or one host would load a pack the other refuses."""
        import json
        schema = json.loads((REPO / "contracts" / "island-rules.schema.json").read_bytes())
        in_schema = {}
        for rule in schema["$defs"]["ability"].get("allOf", []):
            primitive = rule.get("if", {}).get("properties", {}).get("primitive", {}).get("const")
            if primitive is None:
                continue
            then = rule["then"]["properties"]
            in_schema[primitive] = (
                then["category"]["const"],
                {name: (p["properties"]["min"]["minimum"], p["properties"]["max"]["maximum"])
                 for name, p in then["params"]["properties"].items()},
                then["reach_m"]["maximum"], then["area_radius_max_m"]["maximum"], then["duration_max_s"]["maximum"],
                then["max_active"]["maximum"])
        table = _match(RULES, r"Primitives = new Dictionary<string, PrimitiveLimits>\(StringComparer\.Ordinal\)\s*\{([\s\S]*?)\n    \};")
        in_kernel = {}
        for entry in re.finditer(r'\["([a-z.]+)"\] = new\("([a-z]+)", new Dictionary<string, \(double, double\)>\(StringComparer\.Ordinal\) '
                                 r'\{ ([^}]*) \}, ([0-9.]+), ([0-9.]+), ([0-9.]+), (\d+)\)', table):
            params = {name: (float(low), float(high))
                      for name, low, high in re.findall(r'\["([a-z_]+)"\] = \(([0-9.]+), ([0-9.]+)\)', entry.group(3))}
            in_kernel[entry.group(1)] = (entry.group(2), params, float(entry.group(4)), float(entry.group(5)),
                                         float(entry.group(6)), int(entry.group(7)))
        self.assertEqual(len(in_kernel), table.count("] = new("), "a kernel primitive this test could not read")
        self.assertEqual(in_kernel, in_schema)
        self.assertEqual(sorted(in_kernel), ["element.ignite", "growth.grow", "growth.seed", "light.emit", "particles.burst", "particles.float"])
        start_look = _method(_text(EFFECTS), "private bool StartLook(")
        self.assertEqual(sorted(re.findall(r'"([a-z]+\.[a-z]+)" => look\.Start\w+\(', start_look)),
                         ["light.emit", "particles.burst"])
        # Bubbles (particles.float) is no effect the look starts since the founder's redesign: it is the one bubble, routed to
        # the Bubble before the look is asked (Kernel/Bubble/CommandHostBubble.cs BubbleEffectStart), the stream of small bubbles gone.
        start = _method(_text(EFFECTS), "private JsonObject EffectStart(")
        self.assertIn('if (plan.Ability.Primitive == "particles.float") return BubbleEffectStart(', start)
        self.assertLess(start.index("BubbleEffectStart("), start.index("StartLook("))
        # Persistent terrain primitives use their own authority path, never a cosmetic effect receipt.
        plan = _method(_text(EFFECTS), "private EffectPlan PlanEffect(")
        self.assertIn('capability is "grow" or "fire"', plan)
        self.assertIn("Use nature.cast for this ability; preview Fire first.", plan)
        nature = _text(KERNEL / "Kernel" / "CommandHostNature.cs")
        self.assertIn('if (op == "nature.cast")', nature)
        self.assertIn('Rules?.Ability(capability)', nature)

    def test_reserved_param_names_are_the_contracts_everywhere(self):
        import json
        command_schema = json.loads((REPO / "contracts" / "game-command.schema.json").read_bytes())
        rules_schema = json.loads((REPO / "contracts" / "island-rules.schema.json").read_bytes())
        contract = command_schema["$defs"]["args"]["effect.start"]["properties"]["params"]["propertyNames"]["not"]["enum"]
        pack = rules_schema["$defs"]["ability"]["properties"]["params"]["propertyNames"]["not"]["enum"]
        pattern = r"ReservedParams = \{ ([^}]+) \};"
        kernel = [re.findall(r'"([a-z_]+)"', _match(path, pattern)) for path in (COMMAND_HOST, RULES)]
        self.assertEqual([contract, kernel[0], kernel[1]], [pack, pack, pack])

    def test_the_mock_checks_effect_start_in_the_kernels_order_and_words(self):
        """The codes PlanEffect throws, in order, are the mock's _plan_effect's, the water surface's two included; and every
        refusal message and field path the mock gives for the abilities is the kernel's, word for word."""
        import ast
        import inspect
        import textwrap
        from garage_game_companion.mock_host import MockHost
        kernel = _method(_text(EFFECTS), "private EffectPlan PlanEffect(")
        mock = inspect.getsource(MockHost._plan_effect)
        self.assertEqual(re.findall(r'HostError\("(\w+)"', mock), re.findall(r'new Refusal\("(\w+)"', kernel))
        words = set()
        for function in (MockHost._plan_effect, MockHost._op_effect_start, MockHost._op_effect_stop):
            tree = ast.parse(textwrap.dedent(inspect.getsource(function)))
            for node in ast.walk(tree):
                if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "HostError":
                    for value in list(node.args[1:2]) + [k.value for k in node.keywords if k.arg == "field_path"]:
                        for constant in ast.walk(value):
                            if isinstance(constant, ast.Constant) and isinstance(constant.value, str):
                                words.add(constant.value)
        source = _text(EFFECTS)
        self.assertGreater(len(words), 20)
        self.assertEqual(sorted(w for w in words if w not in source), [])
        # capabilities.list's one refusal, a cursor it did not give.
        cursor = "That cursor is not from this list."
        self.assertIn(cursor, _method(source, "private JsonObject Capabilities("))
        self.assertIn(cursor, inspect.getsource(MockHost._query))


NATURE = KERNEL / "Kernel" / "CommandHostNature.cs"
GROW_KINDS = [KERNEL / "Kernel" / name for name in ("CommandHostGrowKinds.cs", "CommandHostGrowSprouts.cs", "CommandHostGrowReplant.cs",
                                                     "CommandHostGrowBridge.cs", "CommandHostGrowIvy.cs")]
NATURE_SOURCES = [KERNEL / "Kernel" / name for name in ("CommandHostNature.cs", "CommandHostGrow.cs", "CommandHostFireKinds.cs",
                                                         "CommandHostCamps.cs", "CommandHostConsequences.cs", "CommandHostEnergy.cs",
                                                         # P-Inventory-2: a repair's spend and the Gubble's ask for one (RepairSpend, RepairAsk).
                                                         "CommandHostInventory.cs")] + GROW_KINDS


def _nature_text() -> str:
    """The kernel's nature sources as one text, its long strings joined where C# splits them over two lines."""
    return re.sub(r'"\s*\+\s*\n\s*"', "", "".join(_text(path) for path in NATURE_SOURCES))


def _codes(source: str, pattern: str = r'new Refusal\("(\w+)"') -> list[str]:
    return re.findall(pattern, source)


def _with_preview_check(source: str) -> str:
    """A handler's source with its call to the shared preview check standing for that check's refusal (revision_conflict), so
    the codes read in their order whichever host keeps the check in a helper."""
    for call in ("UseFirePreview(root", "self._use_preview("):
        source = source.replace(call, 'new Refusal("revision_conflict" HostError("revision_conflict"')
    return source


@unittest.skipUnless(NATURE.is_file(), "the kernel host's nature is not in this checkout")
class NatureMatchesTheKernel(unittest.TestCase):
    """Nature (contracts/README.md "Nature"), hosted by the mock since the nature packet (mock_nature.py): its args checked as the
    kernel's CheckNatureArgs checks them, its refusals in the kernel's order and words, its numbers the kernel's laws, the fire rule
    the kernel's table, and its prices and their guard the kernel's. A kernel change that moves a word, a number or a check's order
    fails here, wherever the game can or cannot run."""

    def test_the_mock_checks_the_nature_commands_the_kernel_has(self):
        from garage_game_companion.mock_host import NATURE_OPS
        check = _method(_text(NATURE), "private static void CheckNatureArgs(")
        self.assertEqual(set(re.findall(r'"(nature\.[a-z]+)" =>', check)), set(NATURE_OPS))
        # Its refusals are field_unknown and request_invalid only, which is what the mock's mapping gives (refusals.py).
        self.assertEqual(set(re.findall(r'new Refusal\("(\w+)"', check)), {"field_unknown", "request_invalid"})

    def test_the_contract_bounds_the_args_as_the_kernel_does(self):
        import json
        defs = json.loads((REPO / "contracts" / "game-command.schema.json").read_bytes())["$defs"]
        cast = defs["args"]["nature.cast"]["properties"]
        check = _method(_text(NATURE), "private static void CheckNatureArgs(")
        self.assertIn('is not ("grow" or "fire" or "bubble_carry" or "bubble_boat" or "bubble_lift")', check)
        self.assertEqual(cast["capability"]["enum"], ["grow", "fire", "bubble_carry", "bubble_boat", "bubble_lift"])
        self.assertIn('("radius_m", NatureBlock.MaxFireRadiusM), ("duration_s", 600f)', check)
        radius = float(_match(KERNEL / "Kernel" / "NatureStore.cs", r"\bconst float MaxFireRadiusM = ([0-9.]+)f;"))
        self.assertEqual((cast["radius_m"]["maximum"], cast["duration_s"]["maximum"]), (radius, 600))
        self.assertEqual(_match(NATURE, r'PreviewIdPattern = new\(@"\\A([^"]+)\\z"'), defs["preview_id"]["pattern"].strip("^$"))
        self.assertEqual(_match(COMMAND_HOST, r'CreationId = new\(@"\\A([^"]+)\\z"'), defs["creation_id"]["pattern"].strip("^$"))
        for op in ("nature.cast", "nature.trail", "nature.repair"):
            with self.subTest(op=op):
                allowed = re.search(rf'"{re.escape(op)}" => new\[\] \{{([^}}]*)\}}', check).group(1)
                self.assertEqual(set(re.findall(r'"([a-z_]+)"', allowed)), set(defs["args"][op]["properties"]))

    def test_room_undo_answers_the_kernels_codes_and_words_in_its_order(self):
        import inspect
        from garage_game_companion import mock_host, mock_nature
        undo = _method(_text(NATURE), "private JsonObject NatureUndo(")
        for words in (mock_host.UNDO_NOT_EARLIER, mock_host.UNDO_ONLY_NATURE, mock_nature.UNDO_SEALED, mock_nature.UNDO_NOT_REACHABLE,
                      mock_nature.UNDO_LOCKED, mock_nature.UNDO_HOLD):
            self.assertIn(f'"{words}"', undo)
        # After the kernel's nature and role checks (RequireNature and RequireChange: the mock's room is always loaded and both of
        # its principals build), every refusal the kernel's NatureUndo gives, in its order.
        in_kernel = _codes(undo)
        self.assertEqual(in_kernel, ["invalid_args", "unsupported_capability", "unsupported_capability", "invalid_args", "target_protected"])
        self.assertEqual(_codes(inspect.getsource(mock_nature.NatureMixin._op_room_undo), r'HostError\("(\w+)"'), in_kernel)
        self.assertIn('"room.undo" when !NatureConfigured', _text(COMMAND_HOST))
        # The undo chain: a step's base is the revision it started from, a change of another kind between breaks the chain, and
        # at most the kernel's MaxUndoSteps are kept.
        state = _text(KERNEL / "Kernel" / "NatureState.cs")
        self.assertIn("if (Undo.Count > 0 && Undo[^1].Revision != step.Base) Undo.Clear();", state)
        self.assertIn("if (Undo[i].Revision != expected || !Undo[i].Undoable) return null;", state)

    def test_the_nature_words_are_the_kernels_word_for_word(self):
        """Every refusal, hold and rest word the mock's nature gives is a string of the kernel's nature (its templates' fixed
        parts where the kernel fills a number in), so a word the kernel moves fails here."""
        import inspect
        from garage_game_companion import mock_nature
        source = _nature_text()
        words = {name: value for name, value in vars(mock_nature).items() if name.isupper() and isinstance(value, str) and " " in value}
        self.assertGreater(len(words), 50)
        missing = []
        for name, value in sorted(words.items()):
            escaped = value.replace('"', '\\"')  # the kernel's C# escapes a quote inside its words
            pieces = [piece for piece in re.split(r"\{[a-z_]+\}", escaped) if piece]
            if not all(piece in source for piece in pieces) or ("{" not in value and f'"{escaped}"' not in source and f'"{escaped}' not in source):
                missing.append(name)
        self.assertEqual(missing, [])
        # The templates are the kernel's own: the number goes where the kernel puts it.
        self.assertIn('$"Wait about {Math.Ceiling(wait):0} seconds after your last fire before lighting another."', source)
        self.assertIn('$"The island already keeps {NatureState.MaxGrownPlants} grown plants, the most it can: none more can grow."', source)
        self.assertIn('$"A Fireline is at most {FireKindLaws.FirelineMaxM:0.#} m long."', source)
        self.assertIn('$"The Gubble asks to repair {id}, which you made."', source)
        self.assertIn('$"This repair needs {words}, more than your pockets and the chest hold."', source)
        self.assertIn('$"{whose} would reach your builds.', source)
        # And every word the mock's handlers raise is one of these constants (none typed in place).
        for function in (mock_nature.NatureMixin._op_nature_cast, mock_nature.NatureMixin._check_cast, mock_nature.NatureMixin._cast_grow,
                         mock_nature.NatureMixin._cast_fire, mock_nature.NatureMixin._check_fireline, mock_nature.NatureMixin._cast_snuff,
                         mock_nature.NatureMixin._light_firepit, mock_nature.NatureMixin._op_nature_repair, mock_nature.NatureMixin._op_room_undo,
                         mock_nature.NatureMixin._check_companion_fire, mock_nature.NatureMixin._check_nature_remains,
                         mock_nature.NatureMixin._cast_sprouts, mock_nature.NatureMixin._cast_replant, mock_nature.NatureMixin._check_bridge,
                         mock_nature.NatureMixin._void_span):
            with self.subTest(function=function.__name__):
                self.assertIsNone(re.search(r'HostError\("\w+", "', inspect.getsource(function)), "a word typed in place")
        self.assertEqual((mock_nature.FIRE_NOT_LEARNED, mock_nature.FIRELINE_NOT_LEARNED, mock_nature.FIRE_OFF, mock_nature.GUBBLE_FIRE_BURNING,
                          mock_nature.GUBBLE_FIRE_BURNING_SNUFF, mock_nature.POP_FALL_PREFIX),
                         (_cs_string(KERNEL / "Kernel" / "CommandHostCamps.cs", "FireNotLearned"),
                          _cs_string(KERNEL / "Kernel" / "CommandHostFireKinds.cs", "FirelineNotLearned"),
                          _cs_string(KERNEL / "Kernel" / "CommandHostConsequences.cs", "FireOffMessage"),
                          _cs_string(NATURE, "GubbleFireBurningWords"), _cs_string(NATURE, "GubbleFireBurningSnuffWords"),
                          _cs_string(KERNEL / "Kernel" / "CommandHostConsequences.cs", "PopFallPrefix")))
        for mock, kernel in (("REST_NEAR_FIRE", "RestNearFire"), ("REST_UNLIT", "RestUnlit"), ("REST_OUT", "RestOut"), ("REST_IN_FLAMES", "RestInFlames")):
            with self.subTest(words=mock):
                self.assertEqual(getattr(mock_nature, mock), _cs_string(KERNEL / "Kernel" / "CommandHostCamps.cs", kernel))

    def test_the_nature_numbers_are_the_kernels(self):
        from garage_game_companion import mock_nature as m
        state = KERNEL / "Kernel" / "NatureState.cs"
        grow = KERNEL / "Kernel" / "CommandHostGrow.cs"
        kinds = KERNEL / "Kernel" / "CommandHostFireKinds.cs"
        camps = KERNEL / "Kernel" / "CommandHostCamps.cs"
        unlocks = KERNEL / "Kernel" / "CommandHostGrowKinds.cs"

        def number(path, name: str) -> float:
            raw = _match(path, rf"\bconst (?:float|double|int) (?:\w+ = [^,;]+, )*{name} = ([^,;]+)[,;]")
            raw = raw.replace("f", "").strip()
            if "/" in raw:
                a, b = raw.split("/")
                return float(a) / float(b)
            if raw == "2 * RestRadiusM":
                return 2 * number(camps, "RestRadiusM")
            return float(raw)

        laws = {
            "STEP_S": (state, "StepS"), "WEATHER_CYCLE_S": (state, "WeatherCycleS"), "WEATHER_DRY_S": (state, "WeatherDryS"),
            "WEATHER_RAMP_S": (state, "WeatherRampS"), "WEATHER_WET_UNTIL_S": (state, "WeatherWetUntilS"), "WIND_TURN_S": (state, "WindTurnS"),
            "WIND_CALM_MPS": (state, "WindCalmMps"), "WIND_RAIN_MPS": (state, "WindRainMps"), "WEATHER_RAIN01": (state, "WeatherRain01"),
            "WEATHER_SHOWERS01": (state, "WeatherShowers01"), "FRESH_MOISTURE": (state, "FreshMoisture"), "RAIN_MOISTURE": (state, "RainMoisture"),
            "GROUND_GRASS_FUEL": (state, "GrassFuel"), "GROUND_WOOD_FUEL": (state, "WoodFuel"), "GROUND_LIGHT_FUEL": (state, "LightFuel"),
            "FLOWER_FUEL": (state, "FlowerFuel"), "IGNITION_RADIUS_M": (state, "IgnitionRadiusM"), "IGNITION_MAX_MOISTURE": (state, "IgnitionMaxMoisture"),
            "OBJECT_BURN_PER_INTENSITY_S": (state, "ObjectBurnPerIntensityS"), "OBJECT_CATCH_M": (state, "ObjectCatchM"),
            "SHELTER_MARGIN_M": (state, "ShelterMarginM"), "PLAYER_BURN_M": (state, "PlayerBurnM"),
            "COMPANION_FIRE_COOLDOWN_S": (state, "CompanionFireCooldownS"), "CELL_M": (state, "CellM"), "MAX_BURNING": (state, "MaxBurning"),
            "MAX_GROWN_PLANTS": (state, "MaxGrownPlants"), "MAX_BURNT_PLANTS": (state, "MaxBurntPlants"), "MAX_UNDO_STEPS": (state, "MaxUndoSteps"),
            "MAX_FIRE_TARGETS": (NATURE, "MaxFireTargets"), "MAX_PREVIEW_CELLS": (NATURE, "MaxPreviewCells"),
            "MAX_FIRE_RADIUS_M": (KERNEL / "Kernel" / "NatureStore.cs", "MaxFireRadiusM"),
            "CAP_FACTOR": (grow, "CapFactor"), "CAP_EXTRA_M": (grow, "CapExtraM"), "LEGACY_GROW_SHARE": (grow, "LegacyGrowShare"),
            "ROOT_HEIGHT_TOLERANCE_M": (grow, "RootHeightToleranceM"), "FIRE_REACH_MARGIN_M": (grow, "FireReachMarginM"),
            "GROWN_FUEL_FACTOR": (grow, "GrownFuelFactor"), "ASH_LEFT": (grow, "AshLeft"), "RAIN_GARDEN_FACTOR": (grow, "RainGardenFactor"),
            "THICKET_SPAN_M": (grow, "ThicketSpanM"), "THICKET_LOOKS_AT": (grow, "ThicketLooksAt"),
            "FIRELINE_LEARN_WINDOW_S": (kinds, "FirelineLearnWindowS"), "FIRELINE_LEARN_APART_M": (kinds, "FirelineLearnApartM"),
            "FIRELINE_LEARN_OFF_LINE_M": (kinds, "FirelineLearnOffLineM"), "FIRELINE_MAX_M": (kinds, "FirelineMaxM"),
            "FIRELINE_MIN_M": (kinds, "FirelineMinM"), "FIRELINE_HALF_WIDTH_M": (kinds, "FirelineHalfWidthM"),
            "FIRELINE_SPREAD_M": (kinds, "FirelineSpreadM"), "SNUFF_RADIUS_M": (kinds, "SnuffRadiusM"), "SNUFF_DAMP_S": (kinds, "SnuffDampS"),
            "SNUFF_AIM_M": (kinds, "SnuffAimM"), "SNUFF_AIM_RISE_M": (kinds, "SnuffAimRiseM"), "SNUFF_RISE_M": (kinds, "SnuffRiseM"),
            "STARTING_FIRE_AHEAD_M": (camps, "StartingFireAheadM"), "REST_RADIUS_M": (camps, "RestRadiusM"), "REST_RISE_M": (camps, "RestRiseM"),
            "FLAMES_M": (camps, "FlamesM"), "FLAMES_RISE_M": (camps, "FlamesRiseM"), "SAME_CAMP_M": (camps, "SameCampM"),
            "FIREPIT_GROUND_M": (camps, "FirepitGroundM"), "FIREPIT_PROBE_M": (camps, "FirepitProbeM"), "RAIN_OUT_S": (camps, "RainOutS"),
            "FIREPIT_CATCH_M": (camps, "FirepitCatchM"), "FIREPIT_CATCH_RISE_M": (camps, "FirepitCatchRiseM"), "MAX_FIREPITS": (camps, "MaxFirepits"),
            # Grow's unlocks (decisions 76 and 104 to 107).
            "SEEDLESS_AT": (unlocks, "SeedlessAt"), "BRIDGE_AT": (unlocks, "BridgeAt"), "IVY_AT": (unlocks, "IvyAt"),
            "SEEDLING_START": (unlocks, "SeedlingStart"), "SEEDLING_PHASES": (unlocks, "SeedlingPhases"), "SEEDLING_PHASE_H": (unlocks, "SeedlingPhaseH"),
            "SPROUTS_PER_CAST": (unlocks, "SproutsPerCast"), "SPROUT_APART_M": (unlocks, "SproutApartM"), "SPROUT_LATTICE_M": (unlocks, "SproutLatticeM"),
            "SPROUT_JITTER_M": (unlocks, "SproutJitterM"), "SPROUT_SETS_MIXED": (unlocks, "SproutSetsMixed"), "SPROUT_SET_SEARCH_M": (unlocks, "SproutSetSearchM"),
            "MAX_SPROUTS": (state, "MaxSprouts"), "MAX_BRIDGES": (state, "MaxBridges"),
            "BRIDGE_MIN_M": (unlocks, "BridgeMinM"), "BRIDGE_MAX_M": (unlocks, "BridgeMaxM"), "BRIDGE_WIDTH_M": (unlocks, "BridgeWidthM"),
            "BRIDGE_LEVEL_M": (unlocks, "BridgeLevelM"), "BRIDGE_LIP_M": (unlocks, "BridgeLipM"), "BRIDGE_VOID_DROP_M": (unlocks, "BridgeVoidDropM"),
            "BRIDGE_SAMPLE_M": (unlocks, "BridgeSampleM"), "BRIDGE_EDGE_M": (unlocks, "BridgeEdgeM"), "BRIDGE_CLEAR_M": (unlocks, "BridgeClearM"),
        }
        for name, (path, kernel) in laws.items():
            with self.subTest(number=name):
                self.assertAlmostEqual(getattr(m, name), number(path, kernel), places=6)
        self.assertEqual({k: v for k, v in m.PLANT_FUEL.items()}, {"grass": number(grow, "GrassFuel"), "herb": number(grow, "HerbFuel"),
                         "shrub": number(grow, "ShrubFuel"), "broadleaf": number(grow, "TreeFuel"), "conifer": number(grow, "TreeFuel")})
        self.assertIn('"grass" => GrassFuel, "herb" => HerbFuel, "shrub" => ShrubFuel, "broadleaf" or "conifer" => TreeFuel, _ => 0,', _text(grow))
        self.assertEqual((m.STARTING_ID, m.FIREPIT_PREFIX), (_match(camps, r'StartingId = "([^"]+)"'), _match(camps, r'FirepitPrefix = "([^"]+)"')))
        self.assertEqual(sorted(m.FLAMMABLE_MATERIALS), sorted(re.findall(r'"([a-z]+)"', _match(NATURE, r"FlammableMaterials = new\(StringComparer\.Ordinal\) \{([^}]*)\}"))))
        # The materials wallet is retired (P-Inventory-2): a new island's is empty on both hosts.
        self.assertEqual(m.FRESH_WALLET, {})
        self.assertIn("public static Dictionary<string, int> FreshWallet() => new(StringComparer.Ordinal);", _text(state))
        # The preview handles and their life: 32 kept at most, each for the approval lifetime, 128 random bits.
        self.assertIn("while (_naturePreviews.Count >= 32)", _text(NATURE))
        self.assertEqual(m.MAX_NATURE_PREVIEWS, 32)
        self.assertEqual(m.PREVIEW_LIFETIME_S, 60 * _constant(COMMAND_HOST, r"ApprovalLifetime = TimeSpan\.FromMinutes\((\d+)\)"))
        # The weather is one formula: the same rain and pattern at every point of the cycle.
        self.assertIn('var pattern = rain > NatureLaws.WeatherRain01 ? "rain" : rain > NatureLaws.WeatherShowers01 ? "showers" : "fair";', _text(state))
        self.assertEqual([m.weather_at(t)[2] for t in (0, 119, 130, 160, 230, 260, 300)], ["fair", "fair", "showers", "rain", "rain", "showers", "fair"])

    def test_the_fire_rule_and_the_expertise_are_the_kernels(self):
        from garage_game_companion import mock_nature as m
        table = _match(NATURE, r"FireRule = new Dictionary<\(string, string\), FireRuling>\s*\{([\s\S]*?)\n    \};")
        words = {"Fireproof": "fireproof", "Scorches": "scorches", "BurnsDown": "burns_down"}
        in_kernel = {(kind, mode): (words[outcome], ask == "true")
                     for kind, mode, outcome, ask in re.findall(r'\[\("([a-z]+)", "([a-z]+)"\)\] = new\(FireOutcome\.(\w+), AskFirst: (true|false)\)', table)}
        self.assertEqual(len(in_kernel), 12)
        self.assertEqual(m.FIRE_RULE, in_kernel)
        record = KERNEL / "Kernel" / "ExpertiseRecord.cs"
        consts = dict(re.findall(r'public const string (\w+) = "([a-z_]+)";', _text(record)))
        self.assertEqual(list(m.FIRE_EXPERTISE), [consts[n] for n in re.findall(r"\w+", _match(record, r"FireOrder = \{ ([^}]*) \};"))])
        self.assertEqual(list(m.GROW_EXPERTISE), [consts[n] for n in re.findall(r"\w+", _match(record, r"GrowOrder = \{ ([^}]*) \};"))])
        kinds = dict(re.findall(r'public const string (\w+) = "([a-z_]+)";', _match(KERNEL / "Kernel" / "CommandHostFireKinds.cs",
                                                                                   r"class FireKindWords\s*\{([^}]*)\}")))
        self.assertEqual((m.KIND_FIRE, m.KIND_FIREPIT, m.KIND_FIRELINE, m.KIND_SNUFF), (kinds["Fire"], kinds["Firepit"], kinds["Fireline"], kinds["Snuff"]))
        grows = dict(re.findall(r'public const string (\w+) = "([a-z_]+)";', _match(GROW_KINDS[0], r"class GrowKindWords\s*\{([^}]*)\}")))
        self.assertEqual((m.KIND_SPROUTS, m.KIND_REPLANT, m.KIND_BRIDGE, m.KIND_IVY), (grows["Sprouts"], grows["Replant"], grows["Bridge"], grows["Ivy"]))

    def test_nature_checks_in_the_kernels_order(self):
        """The refusals each nature handler gives, in the kernel's order, and the kernel's routing: the Bubbles first, the island's
        ability, no Fire before the first rest (before any other check of Fire), the place deciding Fire's kind, the Gubble's fire
        guardrails for Fire and a Fireline only (Snuff lights nothing), then the cast's checks."""
        import inspect
        from garage_game_companion import mock_nature as m
        kinds, camps, grow = (_text(KERNEL / "Kernel" / name) for name in ("CommandHostFireKinds.cs", "CommandHostCamps.cs", "CommandHostGrow.cs"))
        sprouts, replant, bridge = (_text(path) for path in GROW_KINDS[1:4])
        consequences = _text(KERNEL / "Kernel" / "CommandHostConsequences.cs")
        nature = _text(NATURE)
        mixin = m.NatureMixin
        pairs = {
            "_check_cast": _method(nature, "private Cast CheckCast("),
            "_cast_grow": _method(grow, "private JsonObject CastGrow("),
            # Grow's unlocks (decisions 104 to 107): the box world has no faces, so the mock has no ivy (CastIvy needs the room's physics).
            "_cast_sprouts": _method(sprouts, "private JsonObject CastSprouts("),
            "_cast_replant": _method(replant, "private JsonObject CastReplant("),
            "_check_bridge": _method(bridge, "private BridgePlan CheckBridge("),
            "_void_span": _method(bridge, "private (float From, float To) VoidSpan("),
            "_check_fireline": _method(kinds, "private Vector3 CheckFireline("),
            "_cast_snuff": _method(kinds, "private JsonObject CastSnuff("),
            "_light_firepit": _method(camps, "private JsonObject LightFirepit("),
            "_cast_fire": _method(nature, "private JsonObject CastFire("),
            "_check_companion_fire": _method(consequences, "private void CheckCompanionFire("),
        }
        self.assertEqual(_codes(inspect.getsource(mixin._use_preview), r'HostError\("(\w+)"'), ["revision_conflict"])
        for name, kernel in pairs.items():
            with self.subTest(handler=name):
                mock = _with_preview_check(inspect.getsource(getattr(mixin, name)))
                self.assertEqual(_codes(mock, r'HostError\("(\w+)"'), _codes(_with_preview_check(kernel)))
        fire = pairs["_cast_fire"]
        self.assertLess(fire.index("UnlitFirepitUnder(cast.Center, cast.Radius)"), fire.index("FireTargets("))
        self.assertLess(inspect.getsource(mixin._cast_fire).index("self._unlit_firepit_under("), inspect.getsource(mixin._cast_fire).index("self._fire_targets("))
        # The repair: the kernel's own refusals after its look-up (the mock's CheckPerceived for the companion comes first, as the
        # kernel's Execute runs CheckPerceived before the op), but its design check: a scorched build can be neither revised nor
        # locked (target_busy, _check_nature_remains), so on the mock its design cannot change before it is repaired.
        # Since P-Inventory-2 its spend (too few logs and stones) is RepairSpend's, called where the wallet's check was.
        repair = _codes(_method(nature, "private JsonObject Repair(")) + _codes(_method(_text(KERNEL / "Kernel" / "CommandHostInventory.cs"), "private RepairBill RepairSpend("))
        self.assertIn("var bill = free || item.RepairCost.Count == 0 ? null : RepairSpend(item.RepairCost);", _method(nature, "private JsonObject Repair("))
        self.assertIn('"This creation\'s design changed; its old repair quote no longer applies."', _method(nature, "private JsonObject Repair("))
        self.assertEqual(_codes(inspect.getsource(mixin._op_nature_repair), r'HostError\("(\w+)"'), [c for c in repair if c != "revision_conflict"])
        command = _method(nature, "private JsonObject NatureCommand(")
        order = ["IsBubbleCapability(Str(args, \"capability\"))", 'Rules?.Ability(capability)', "RequireFireLearned();", "RequireGrowDrag(args);",
                 "FireKindOf(args)", "CheckCompanionFire();", "CheckCast(ability, args, principal"]
        self.assertEqual([command.index(step) for step in order], sorted(command.index(step) for step in order))
        self.assertIn('if (capability == "fire" && principal != PlayerPrincipal && kind is FireKind.Fire or FireKind.Fireline) CheckCompanionFire();', command)
        self.assertIn("oneFire: kind is FireKind.Fire or FireKind.Fireline", command)
        mock = inspect.getsource(mixin._op_nature_cast)
        steps = ['"bubble_carry", "bubble_boat", "bubble_lift"', "self.rules.ability(capability)", "FIRE_NOT_LEARNED", "BRIDGE_LOCKED",
                 "self._fire_kind_of(args)", "self._check_companion_fire()", "self._check_cast("]
        self.assertEqual([mock.index(step) for step in steps], sorted(mock.index(step) for step in steps))
        self.assertIn("kind in (KIND_FIRE, KIND_FIRELINE)", mock)
        self.assertIn("one_fire=kind in (KIND_FIRE, KIND_FIRELINE)", mock)
        # The place decides: a drag a Fireline (refused until learned), water a wisp (the box world has none), something burning
        # Snuff once learned, else Fire.
        kind_of = _method(kinds, "private FireKind FireKindOf(")
        self.assertLess(kind_of.index('TryGetProperty("end_m"'), kind_of.index("SnuffLearned && BurningAt(center)"))
        self.assertEqual(_codes(kind_of), _codes(inspect.getsource(mixin._fire_kind_of), r'HostError\("(\w+)"'))

    def test_nature_is_priced_by_principal_where_the_kernel_guards(self):
        """Every nature cast passes the guard after its checks and any hold, by its kind's price key, and is charged once committed
        (the kernel's GuardEnergy and ChargeEnergy); the pool's rule and words are the kernel's."""
        import inspect
        from garage_game_companion import mock_energy, mock_nature
        kinds, camps, grow = (_text(KERNEL / "Kernel" / name) for name in ("CommandHostFireKinds.cs", "CommandHostCamps.cs", "CommandHostGrow.cs"))
        nature = _text(NATURE)
        keys = {
            "_cast_grow": ("GuardEnergy(ability.Capability, principal)", "self._guard_energy(ability.capability, principal)", _method(grow, "private JsonObject CastGrow(")),
            "_cast_fire": ("GuardEnergy(lineCells != null ? FireKindWords.Fireline : ability.Capability, principal)",
                           "self._guard_energy(KIND_FIRELINE if line_end is not None else ability.capability, principal)", _method(nature, "private JsonObject CastFire(")),
            "_cast_snuff": ("GuardEnergy(FireKindWords.Snuff, principal)", "self._guard_energy(KIND_SNUFF, principal)", _method(kinds, "private JsonObject CastSnuff(")),
            "_light_firepit": ("GuardEnergy(ability.Capability, principal)", "self._guard_energy(ability.capability, principal)", _method(camps, "private JsonObject LightFirepit(")),
            # Grow's unlocks share one commit, priced as any Grow (decision 107).
            "_commit_grow_kind": ("GuardEnergy(ability.Capability, principal)", "self._guard_energy(ability.capability, principal)",
                                  _method(_text(GROW_KINDS[0]), "private JsonObject CommitGrowKind(")),
        }
        for name, (kernel_guard, mock_guard, kernel) in keys.items():
            with self.subTest(handler=name):
                mock = inspect.getsource(getattr(mock_nature.NatureMixin, name))
                self.assertIn(kernel_guard, kernel)
                self.assertIn(mock_guard, mock)
                self.assertIn("ChargeEnergy(price);", kernel)
                self.assertIn("self._charge_energy(price)", mock)
                # The hold comes before the guard: a held cast is guarded when the player's click commits it.
                held = "return Hold(" if "return Hold(" in kernel else None
                if held is not None:
                    self.assertLess(kernel.index(held), kernel.index(kernel_guard))
                    self.assertLess(mock.index('"_ask"'), mock.index(mock_guard))
        energy = _text(ENERGY)
        self.assertEqual(mock_energy.ENERGY_EXHAUSTED_MESSAGE, _cs_string(ENERGY, "EnergyExhaustedMessage"))
        guard = _method(energy, "internal int GuardEnergy(")
        self.assertIn("if (price <= 0 || _vitalsRules.ShadowPool) return price;", guard)
        self.assertIn('throw new Refusal("energy_exhausted", EnergyExhaustedMessage, "$.args.capability", retryable: true) { Balance = balance, Price = price };', guard)
        mock_guard = inspect.getsource(mock_energy.EnergyMixin._guard_energy)
        self.assertIn('if price <= 0 or self.mode == "creative":', mock_guard)
        self.assertIn("var pips = Math.Max(0, Math.Max(0, Vitals.Pips) - price);", _method(energy, "private void ChargeEnergy("))
        # The refill's rule: near within refill_near_within_m, else apart, one pip at a time up to the maximum.
        self.assertIn("(distanceM <= Pips.RefillNearWithinM ? Pips.RefillNearEveryS : Pips.RefillFarEveryS) / RefillMultiplier",
                      _text(KERNEL / "Kernel" / "VitalsRules.cs"))
        self.assertIn("MaxPips = Pips == null ? 0 : Math.Min(Pips.Ceiling, Pips.Start + Math.Max(0, expertise) / Pips.ExpertisePerPip);",
                      _text(KERNEL / "Kernel" / "VitalsRules.cs"))


    def test_outside_creative_undo_is_refused_first_in_the_kernels_words(self):
        """CommandHost.Unsupported refuses room.undo outside Creative before CheckExpectations; the mock's _check_unsupported
        does the same before _check_revisions, with the same words."""
        import inspect
        from garage_game_companion import mock_host
        host = _text(COMMAND_HOST)
        self.assertIn(f'"room.undo" when Mode != "creative" => "{mock_host.UNDO_NOT_IN_THIS_MODE}",', host)
        execute = _method(host, "private JsonObject Execute(")
        self.assertLess(execute.index("Unsupported(op, args, principal)"), execute.index("CheckExpectations(root, op, args, principal)"))
        self.assertIn('throw new Refusal("unsupported_capability", unsupported, "$.op")', execute)
        command = inspect.getsource(mock_host.MockHost._command)
        self.assertLess(command.index("self._check_unsupported(message)"), command.index("self._check_revisions(principal, message)"))
        # The mechanics round's stubs (M0) refuse there too, with the same code (MechanicsMatchTheKernel holds their words).
        self.assertEqual(set(re.findall(r'HostError\("(\w+)"', inspect.getsource(mock_host.MockHost._check_unsupported))), {"unsupported_capability"})


@unittest.skipUnless(COMMAND_HOST.is_file(), "the kernel host is not in this checkout")
class MechanicsMatchTheKernel(unittest.TestCase):
    """The mechanics round's contract (M0): the stubs both hosts answer until their packets land, in the kernel's words, and
    the Fractal the companion may never gather, refused by its id's namespace in both; the Bubble (M9) with the kernel's
    numbers and words."""

    def test_the_stubs_and_the_fractal_refusal_are_the_kernels_word_for_word(self):
        from garage_game_companion import mock_host
        for mock, kernel in (("MECHANICS_NOT_YET", "MechanicsNotYet"), ("DIFFICULTY_NOT_YET", "DifficultyNotYet"),
                             ("FRACTAL_REFUSAL", "FractalRefusal")):
            with self.subTest(words=mock):
                self.assertEqual(getattr(mock_host, mock), _cs_string(COMMAND_HOST, kernel))
        self.assertEqual(mock_host.MECHANICS_PLAYER_OPS, set(re.findall(r'"([a-z_.]+)"', _match(COMMAND_HOST, r"MechanicsPlayerOps = new\(\) \{([^}]*)\};"))))
        source = _text(COMMAND_HOST)
        self.assertIn('(op is "entity.grab" or "entity.place" or "entity.push" || (op == "goal.set" && Str(args, "goal") == "fetch"))', source)
        self.assertIn('target.StartsWith("fractal:", StringComparison.Ordinal)', source)
        execute = _method(source, "private JsonObject Execute(")
        # The Fractal refusal comes after the player-only check and before anything looks the target up.
        self.assertLess(execute.index("PlayerOnlyOps.Contains(op)"), execute.index("NamesAFractal(op, args)"))
        self.assertLess(execute.index("NamesAFractal(op, args)"), execute.index("CheckPerceived(op, args, principal)"))

    def test_the_bubble_is_hosted_with_the_kernels_numbers_and_words(self):
        """M9: the Bubbles casts past the stub on both hosts (no stub words left), the mock's numbers the kernel's BubbleLaws, its
        refusals the kernel's words, a bubble transient on both, and the kernel's Bubbles route ahead of nature's own checks."""
        from garage_game_companion import mock_host
        self.assertFalse(hasattr(mock_host, "BUBBLES_NOT_YET"))
        self.assertNotIn("BubblesNotYet", _text(COMMAND_HOST))
        laws = KERNEL / "Kernel" / "Bubble" / "BubbleLaws.cs"

        def law(name: str) -> float:
            return float(_match(laws, rf"\bconst (?:float|double|int) {name} = ([0-9.]+)f?;"))

        for mock, kernel in ((mock_host.BUBBLE_CARRY_RADIUS_M, ("CarryRadiusMinM", "CarryRadiusMaxM")),
                             (mock_host.BUBBLE_BOAT_RADIUS_M, ("BoatRadiusMinM", "BoatRadiusMaxM"))):
            with self.subTest(numbers=kernel):
                self.assertEqual(mock, tuple(law(name) for name in kernel))
        for mock, kernel in (("BUBBLE_CARRY_DURATION_DEFAULT_S", "CarryDurationDefaultS"), ("BUBBLE_BOAT_DURATION_DEFAULT_S", "BoatDurationDefaultS"),
                             ("BUBBLE_GHOST_LEAD_S", "GhostLeadS"), ("BUBBLE_GHOST_LIFETIME_S", "GhostLifetimeS"),
                             ("BUBBLE_ENVELOP_REACH_M", "EnvelopReachM"), ("BUBBLE_CARRY_RADIUS_DEFAULT_M", "CarryRadiusDefaultM"),
                             ("BUBBLE_STEP_IN_CLEARANCE_M", "StepInClearanceM"), ("BUBBLE_MAX", "MaxBubbles"),
                             ("BUBBLE_MAX_GHOSTS", "MaxGhosts"), ("BUBBLE_LIFT_ABOVE_DRIFT_M", "LiftAboveDriftM")):
            with self.subTest(number=kernel):
                self.assertEqual(getattr(mock_host, mock), law(kernel))
        # One rule for the island's bounds (the W2 review's minor 8): the mock's margin is the kernel's InsideIsland's.
        self.assertEqual(mock_host.ISLAND_SLACK_M, float(_match(KERNEL / "Kernel" / "Bubble" / "CommandHostBubble.cs", r"\bconst float IslandSlackM = ([0-9.]+)f;")))
        # One bubble at a time and no pause after a pop (decision 59): the pause's number and words are gone from both hosts.
        self.assertEqual(mock_host.BUBBLE_MAX, 1)
        self.assertFalse(hasattr(mock_host, "BUBBLE_POP_RESPECT_S") or hasattr(mock_host, "BUBBLE_POP_RESPECT"))
        self.assertNotIn("PopRespectS", _text(laws))
        bubble = (_text(KERNEL / "Kernel" / "Bubble" / "CommandHostBubble.cs") + _text(KERNEL / "Kernel" / "Bubble" / "CommandHostBubbleLead.cs")
                  + _text(COMMAND_HOST)).replace('" +\n            "', "")
        for words in ("BUBBLE_GHOST_LEAD", "BUBBLE_PREVIEW_AGAIN", "BUBBLE_ON_THE_PLAYER", "BUBBLE_NO_WATER", "BUBBLE_GHOST_FIRST",
                      "BUBBLE_AT_A_SPOT", "BUBBLE_LIFT_IS_A_LEAD", "LEAD_POPPED", "LEAD_BLOCKED"):
            with self.subTest(words=words):
                self.assertIn('"' + getattr(mock_host, words) + '"', bubble)
        # The lead's argument rule, the adapter's and the mock's words the kernel's CheckArgs (request_invalid at its paths).
        from garage_game_companion import refusals
        for words in (refusals.LEAD_NAMES_A_BUBBLE, refusals.LEAD_NAMES_A_SPOT):
            with self.subTest(words=words):
                self.assertIn('"' + words + '"', _text(COMMAND_HOST))
        # Both hosts: a committed bubble is transient (the kernel's own receipt, never the durable nature path), its cast routed
        # before nature's RequireNature, and its pops in goal.stop and effect.stop.
        nature = _method(_text(KERNEL / "Kernel" / "CommandHostNature.cs"), "private JsonObject NatureCommand(")
        self.assertLess(nature.index("BubbleCast(root, principal, fingerprint, approvedBy)"), nature.index("RequireNature();"))
        self.assertIn('result["transient"] = true;', _method(bubble, "private JsonObject BubbleReceipt("))
        host = _text(COMMAND_HOST)
        self.assertIn('PopBubbles(principal, "all")', _method(host, "private JsonObject GoalStop("))
        self.assertIn("PopBubbles(principal, effect)", _method(host, "private JsonObject EffectStop("))

    def test_the_mock_reads_its_prices_from_the_pack_the_kernel_will_read(self):
        """The cost block is one JSON both hosts read (the kernel from M1): the mock prices from it and nothing else."""
        import json
        from garage_game_companion.mock_host import load_rules, rules_path
        from support import contracts
        v5 = load_rules(contracts(), rules_path(rules_version=5))
        pack = json.loads(rules_path(rules_version=5).read_bytes())
        for name, price in pack["cost"]["prices"].items():
            with self.subTest(ability=name):
                self.assertEqual(v5.price(name, "player:local"), price["ordered"])
                self.assertEqual(v5.price(name, "companion:local"), round(price["ordered"] * price["own_idea_factor"]))
        self.assertEqual({n: (v5.price(n, "player:local"), v5.price(n, "companion:local")) for n in pack["cost"]["prices"]},
                         {"glow": (2, 1), "bubbles": (2, 1), "fireworks": (2, 1), "grow": (4, 2), "fire": (2, 1),
                          "bubble_carry": (4, 2), "bubble_boat": (4, 2), "bubble_lift": (6, 3),
                          "fireline": (8, 4), "snuff": (2, 1), "will_o_wisp": (4, 2)})
        self.assertEqual(v5.price("teleport", "companion:local"), 0, "a price the pack does not set costs nothing")
        today = load_rules(contracts(), rules_path(rules_version=4))
        self.assertEqual({a.capability: today.price(a.capability, "companion:local") for a in today.abilities},
                         {"glow": 0, "bubbles": 0, "fireworks": 0, "grow": 0, "fire": 0}, "v4 has no cost block: nothing is charged (its Bloom read as Grow)")

    def test_a_price_moved_in_the_pack_moves_the_mocks(self):
        import json
        import tempfile
        from pathlib import Path
        from garage_game_companion.mock_host import load_rules, rules_path
        from support import contracts
        pack = json.loads(rules_path(rules_version=5).read_bytes())
        pack["cost"]["prices"]["grow"]["ordered"] = 8
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "storybook_wild" / "v5.json"
            path.parent.mkdir()
            path.write_bytes((json.dumps(pack, indent=2) + "\n").encode("utf-8"))
            moved = load_rules(contracts(), path)
        self.assertEqual((moved.price("grow", "player:local"), moved.price("grow", "companion:local")), (8, 4))


EYES = KERNEL / "Kernel" / "CommandHostEyes.cs"


def _cs_string(path, name: str) -> str:
    """A C# string constant's value (the kernel's words use only the escapes \\" and \\\\)."""
    raw = _match(path, rf'\bpublic const string {name} = "((?:[^"\\]|\\.)*)";')
    return raw.replace('\\"', '"').replace("\\\\", "\\")


TRUST = KERNEL / "Kernel" / "Trust.cs"
TRUST_HOST = KERNEL / "Kernel" / "CommandHostTrust.cs"
ENERGY = KERNEL / "Kernel" / "CommandHostEnergy.cs"
BUBBLE_HOST = KERNEL / "Kernel" / "Bubble" / "CommandHostBubble.cs"
BUBBLE_LEAD = KERNEL / "Kernel" / "Bubble" / "CommandHostBubbleLead.cs"


@unittest.skipUnless(TRUST.is_file() and TRUST_HOST.is_file(), "the kernel's Trust is not in this checkout")
class TrustMatchesTheKernel(unittest.TestCase):
    """M8: the mock's Trust numbers, words and journal lines are the kernel's (Kernel/Trust.cs, Kernel/CommandHostTrust.cs), and the
    kernel routes the two player's ops past the stubs and observes the Gubble's commits once."""

    def test_the_numbers_and_words_are_the_kernels(self):
        from garage_game_companion import mock_trust
        numbers = {
            "CLEAN_INITIATIVE": _constant(TRUST, r"\bconst int CleanInitiative = (-?\d+);"),
            "CLEAN_WINDOW_S": float(_match(TRUST, r"\bconst double CleanWindowS = ([0-9.]+);")),
            "GAIN_SPACING_S": float(_match(TRUST, r"\bconst double GainSpacingS = ([0-9.]+);")),
            "PER_CONSEQUENCE": _constant(TRUST, r"\bconst int PerConsequence = (-?\d+);"),
            "MARK_YES": _constant(TRUST, r"\bconst int MarkYes = (-?\d+);"),
            "MARK_NOT_THAT": _constant(TRUST, r"\bconst int MarkNotThat = (-?\d+);"),
            "MAX_REMEMBERED": _constant(TRUST, r"\bconst int MaxRemembered = (\d+);"),
            "TRUST_BOUND": int(_match(TRUST, r"\bconst int Bound = ([0-9_]+);").replace("_", "")),
        }
        for name, value in numbers.items():
            with self.subTest(name=name):
                self.assertEqual(getattr(mock_trust, name), value)
        self.assertEqual(list(mock_trust.BAND_AT), [int(n) for n in _match(TRUST, r"BandAt = \{ ([^}]*) \};").split(",")])
        self.assertEqual(list(mock_trust.OWN_IDEA_SHARE), [float(n) for n in _match(TRUST, r"OwnIdeaShare = \{ ([^}]*) \};").split(",")])
        for mock, kernel in (("MARK_NOT_FOUND", "MarkNotFound"), ("MARKED_ONCE", "MarkedOnce")):
            with self.subTest(words=mock):
                self.assertEqual(getattr(mock_trust, mock), _cs_string(TRUST_HOST, kernel))
        trust = _text(TRUST)
        for level in (-1, 0, 1, 2, 3):
            self.assertIn(f'"{mock_trust.phrase(level)}"', trust)
        host = _text(TRUST_HOST)
        for line in ("You marked the Gubble's ", ": yes, like that.", ": not that.", "You reset the Gubble's Trust: "):
            self.assertIn(line, host)

    def test_both_hosts_earn_from_the_charge_never_a_list(self):
        """Decision 69 ("Only paid actions and consequences directly related to those actions earn trust"): the same rule on both
        hosts, read from what the guard charges, never a list of ops (decision 68's lists are gone from both)."""
        import inspect
        from garage_game_companion import mock_trust
        from garage_game_companion.mock_host import MockHost
        trust = _text(TRUST)
        self.assertIsNone(re.search(r"HashSet<string> World(Ops|Goals)\b", trust), "the kernel keeps no op list for Trust")
        for name in ("WORLD_OPS", "WORLD_GOALS", "changes_the_world"):
            self.assertFalse(hasattr(mock_trust, name), f"the mock keeps no {name}")
        # The rule itself: paid when the charge is above the kernel's threshold, the same answers on both hosts.
        threshold = int(_match(TRUST, r"public static bool Paid\(int chargedPips\) => chargedPips > (-?\d+);"))
        self.assertEqual([mock_trust.paid(pips) for pips in range(-2, 7)], [pips > threshold for pips in range(-2, 7)])
        # The kernel reads the charge where the guard makes it, takes it first at a commit, and pays a lead at its Lift's rise.
        charge = _method(_text(ENERGY), "private void ChargeEnergy(")
        self.assertLess(charge.index("NoteTrustCharge(price);"), charge.index("if (price <= 0"))
        host = _text(TRUST_HOST)
        observe = _method(host, "private void ObserveTrustCommand(")
        self.assertLess(observe.index("var charged = TakeTrustCharge();"), observe.index("if (principal != CompanionPrincipal"))
        self.assertIn("Paid = Trust.Paid(charged)", _method(host, "private void RememberGubbleCommand("))
        self.assertRegex(_method(_text(BUBBLE_LEAD), "private bool StartLift("), r"ChargeEnergy\(price\);\s*TrustLiftCharged\(lead\);")
        # The mock guards and charges wherever the kernel's guard charges a command the mock hosts (nature's: NatureMatchesTheKernel).
        self.assertEqual(_text(BUBBLE_HOST).count("ChargeEnergy(price);"), 2, "the kernel charges a bubble at effect.start and nature.cast")
        self.assertIn("ChargeEnergy(price);", _method(_text(EFFECTS), "private JsonObject EffectStart("))
        for method in (MockHost._op_effect_start, MockHost._bubble_effect_start, MockHost._bubble_cast):
            with self.subTest(method=method.__name__):
                self.assertIn("self._guard_energy(", inspect.getsource(method))
                self.assertIn("self._charge_energy(price)", inspect.getsource(method))
        self.assertIn("self._trust_lift_charged(", inspect.getsource(MockHost._lead_arrived))
        self.assertIn('self._guard_energy("bubble_lift", bubble.lead["principal"])', inspect.getsource(MockHost._lead_arrived))
        observed = inspect.getsource(mock_trust.TrustMixin._observe_trust)
        self.assertLess(observed.index("charged = self._take_trust_charge()"), observed.index("if principal.startswith"))
        self.assertIn("paid(charged)", observed)

    def test_the_kernel_hosts_the_marks_and_the_reset(self):
        source = _text(COMMAND_HOST)
        unsupported = source[source.index("private string? Unsupported("):]
        self.assertNotIn('"receipt.mark" or "trust.reset" => MechanicsNotYet', unsupported)
        self.assertIn('"receipt.mark" => MarkReceipt(', source)
        self.assertIn('"trust.reset" => ResetTrust(', source)
        execute = _method(source, "private JsonObject Execute(")
        self.assertEqual(execute.count("ObserveTrustCommand(root, result, principal);"), 1)
        # The player-only refusal comes before either op's handler: the companion never reaches them.
        self.assertLess(execute.index("PlayerOnlyOps.Contains(op)"), execute.index('"receipt.mark" => MarkReceipt('))


@unittest.skipUnless(EYES.is_file(), "the kernel host's eyes are not in this checkout")
class EyesMatchTheKernel(unittest.TestCase):
    """The Gubble's eyes (view.look): the mock's numbers and words are the kernel's, and the contract's bounds hold them."""

    def test_the_numbers_are_the_kernels(self):
        from garage_game_companion import eyes
        expected = {
            "LOOK_BURST": _constant(EYES, r"\bconst int LookBurst = (\d+);"),
            "LOOK_REFILL_S": float(_match(EYES, r"\bconst double LookRefillS = ([0-9.]+);")),
            "LOOK_IMAGE_MAX_BYTES": _constant(EYES, r"\bconst int LookImageMaxBytes = (\d+);"),
            "LOOK_WIDTH_PX": _constant(EYES, r"\bconst int LookWidthPx = (\d+);"),
            "LOOK_HEIGHT_PX": _constant(EYES, r"\bconst int LookHeightPx = (\d+);"),
            "LOOK_MAX_YAW_DEG": float(_match(EYES, r"\bconst double LookMaxYawDeg = ([0-9.]+);")),
            "LOOK_MAX_PITCH_DEG": float(_match(EYES, r"\bconst double LookMaxPitchDeg = ([0-9.]+);")),
            "EYES_PROFILE_KEY": _cs_string(EYES, "EyesProfileKey"),
            "DEFAULT_GUBBLE_EYES": _match(EYES, r"\bconst bool DefaultGubbleEyes = (true|false);") == "true",
            "EYE_VERTICAL_FOV_DEG": float(_match(KERNEL / "SmallPlayerController.cs",
                                                 r'Name = "EyeCamera",[^\n]*\n[^\n]*\bFov = ([0-9.]+)f')),
        }
        self.assertEqual({name: getattr(eyes, name) for name in expected}, expected)
        self.assertEqual((HostPolicy().look_burst, HostPolicy().look_refill_s), (expected["LOOK_BURST"], expected["LOOK_REFILL_S"]))

    def test_the_refusals_are_the_kernels_word_for_word_and_in_its_order(self):
        import inspect
        from garage_game_companion import eyes
        from garage_game_companion.mock_host import MockHost
        for mock, kernel in (("EYES_OFF_MESSAGE", "EyesOffMessage"), ("NO_RENDERER_MESSAGE", "NoRendererMessage"),
                             ("LOOK_RATE_MESSAGE", "LookRateMessage"), ("LOOK_ACTOR_MESSAGE", "LookActorMessage")):
            with self.subTest(message=mock):
                self.assertEqual(getattr(eyes, mock), _cs_string(EYES, kernel))
        view_look = _method(_text(EYES), "private JsonObject ViewLook(")
        in_kernel = [code for code in re.findall(r'new Refusal\("(\w+)"', view_look) if code != "not_ready"]
        in_mock = re.findall(r'HostError\("(\w+)"', inspect.getsource(MockHost._view_look))
        # The kernel also refuses a caller that cannot wait for a frame (its synchronous path), which the mock never needs.
        self.assertEqual(in_kernel, in_mock + ["unavailable"])
        self.assertEqual(in_mock, ["actor_denied", "permission_denied", "rate_limited", "unavailable"])

    def test_the_contract_bounds_are_the_kernels(self):
        import json
        from garage_game_companion import eyes
        defs = json.loads((REPO / "contracts" / "game-command.schema.json").read_bytes())["$defs"]
        if "view.look" not in defs["query_op"]["enum"]:
            self.skipTest("the contract has no view.look yet")
        args, image = defs["args"]["view.look"]["properties"], defs["data"]["view.look"]["properties"]["image"]["properties"]
        self.assertEqual((args["yaw_deg"]["minimum"], args["yaw_deg"]["maximum"]), (-eyes.LOOK_MAX_YAW_DEG, eyes.LOOK_MAX_YAW_DEG))
        self.assertEqual((args["pitch_deg"]["minimum"], args["pitch_deg"]["maximum"]), (-eyes.LOOK_MAX_PITCH_DEG, eyes.LOOK_MAX_PITCH_DEG))
        self.assertEqual(image["bytes"]["maximum"], eyes.LOOK_IMAGE_MAX_BYTES)
        self.assertEqual(image["data_base64"]["maxLength"], 4 * -(-eyes.LOOK_IMAGE_MAX_BYTES // 3))
        self.assertGreaterEqual(image["width_px"]["maximum"], max(eyes.LOOK_WIDTH_PX, eyes.LOOK_HEIGHT_PX))
        self.assertIn("unavailable", defs["error_code"]["enum"])
        # The largest picture leaves the result well inside the kernel's 256 KiB answer (and so the link's response frame).
        self.assertLess(image["data_base64"]["maxLength"] + 8192, _constant(COMMAND_HOST, r"\bconst int MaxResultBytes = (\d+);"))


class DefaultPolicyBoundaries(unittest.TestCase):
    """The default policy at the kernel's boundaries (the mechanisms are tested with small numbers elsewhere)."""

    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(clock=self.clock)

    def tearDown(self):
        from support import contract_problems
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def fill_ledger(self, count: int) -> None:
        """Durable receipts committed earlier (contents do not matter for the bound)."""
        for i in range(len(self.host.receipts), count):
            self.host.receipts[(PLAYER, f"earlier-{i}")] = Receipt("0" * 64, {"revision": 0, "op": "protect.lock"}, True)

    def lock(self, target, action_id, principal=COMPANION):
        self.clock.advance(1.1)  # keep the companion well inside its rate budget
        return self.host.handle(principal, command("protect.lock", {"targets": [target]}, action_id,
                                                   expected_entities={target: self.host.entities[target].revision}))

    def test_the_companion_fills_1792_receipts_and_the_player_the_last_256(self):
        self.fill_ledger(2048 - 256 - 1)
        self.assertTrue(self.lock("obj:box", "lock-1")["ok"])
        refused = self.lock("obj:book", "lock-2")
        self.assertFalse(refused["ok"])
        self.assertEqual(refused["error"]["code"], "receipt_limit")
        self.assertTrue(self.lock("obj:book", "p-lock-1", PLAYER)["ok"])
        self.fill_ledger(2048 - 1)
        self.assertTrue(self.lock("obj:rug", "p-lock-2", PLAYER)["ok"])
        self.assertEqual(self.lock("obj:doorstop", "p-lock-3", PLAYER)["error"]["code"], "receipt_limit")

    def test_a_companion_gets_30_messages_a_second(self):
        results = [self.host.handle(COMPANION, query("room.describe", {}, f"q-{i}")) for i in range(31)]
        self.assertTrue(all(r["ok"] for r in results[:30]))
        self.assertEqual(results[30]["error"]["code"], "rate_limited")
        self.clock.advance(1.01)
        self.assertTrue(self.host.handle(COMPANION, query("room.describe", {}, "q-late"))["ok"])


if __name__ == "__main__":
    unittest.main()
