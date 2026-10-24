"""Contract refusals: one error type and one mapping from schema violations to contract error codes.

Shared by the adapter (which refuses before sending) and the mock host (which refuses on receipt),
so both layers answer the same attack with the same code. Messages are fixed templates: nothing
the requester wrote is echoed back, so a refusal can never carry injected text.
"""
from __future__ import annotations

import re
from typing import Any

from . import ids, textsafety

RESULT_SCHEMA = ids.id_of("result")
_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}")


class HostError(Exception):
    def __init__(self, code: str, message: str, *, field_path: str | None = None, retryable: bool = False,
                 allowed: Any = None, actual: Any = None, has_allowed: bool = False, balance: int | None = None,
                 price: int | None = None):
        super().__init__(code)
        self.code = code
        self.message = message
        self.field_path = field_path
        self.retryable = retryable
        self.allowed = allowed
        self.actual = actual
        self.has_allowed = has_allowed or allowed is not None
        # energy_exhausted only (the contract: no other error carries them): the pips in the pool now and the price.
        self.balance = balance
        self.price = price


_KEYWORD_MESSAGES = {
    "required": "A required field is missing.",
    "maximum": "A value is above the allowed maximum.",
    "exclusiveMaximum": "A value is above the allowed maximum.",
    "minimum": "A value is below the allowed minimum.",
    "exclusiveMinimum": "A value is below the allowed minimum.",
    "pattern": "A value has the wrong format.",
    "enum": "A value is not one of the allowed choices.",
    "const": "A value is not the allowed value.",
    "type": "A value has the wrong type.",
    "maxLength": "A text value is too long.",
    "minLength": "A text value is too short.",
    "maxItems": "A list has too many items.",
    "minItems": "A list needs more items.",
    "maxProperties": "An object has too many fields.",
    "uniqueItems": "A list names the same item twice.",
    "anyOf": "The arguments are incomplete for this operation.",
    "not": "A parameter name is reserved for identity or authority and is refused.",
    "propertyNames": "A field name is not allowed here.",
}


# A Fractal is the player's to pick up (the mechanics round, M0): a companion's grab, place, push or fetch of one is refused by
# the id's namespace before anything is looked up, at the adapter and the host, in the kernel's words (CommandHost.FractalRefusal).
FRACTAL_REFUSAL = "Only the player can pick up a Fractal."


def names_a_fractal(message: Any) -> bool:
    """Whether a command would have its actor carry, place, push or fetch a Fractal (the kernel's NamesAFractal)."""
    if not isinstance(message, dict):
        return False
    op, args = message.get("op"), message.get("args")
    if not isinstance(args, dict) or not isinstance(args.get("target"), str):
        return False
    gathers = op in ("entity.grab", "entity.place", "entity.push") or (op == "goal.set" and args.get("goal") == "fetch")
    return gathers and args["target"].startswith("fractal:")


def map_schema_errors(errors) -> HostError:
    """Turn jsonschema errors into one contract error. Messages are templates: no requester text is echoed."""

    def rank(error) -> tuple:
        path = list(error.absolute_path)
        if error.validator == "additionalProperties":
            return (0, len(path))
        if path[:1] == ["version"]:
            return (1, 0)
        if path[:1] in (["action_id"], ["query_id"]):
            return (2, 0)
        if path[:1] == ["op"]:
            return (3, 0)
        return (4, len(path), str(path))

    error = sorted(errors, key=rank)[0]
    path = list(error.absolute_path)
    if error.validator == "additionalProperties":
        allowed = set(error.schema.get("properties", {}))
        extra = sorted(k for k in error.instance if k not in allowed) if isinstance(error.instance, dict) else []
        where = path + ([extra[0]] if extra else [])
        return HostError("field_unknown", "The request has a field the game does not accept.",
                         field_path=textsafety.field_path(where))
    if path[:1] == ["version"]:
        if isinstance(error.instance, int) and not isinstance(error.instance, bool):
            return HostError("version_unsupported", "This game speaks command version 1.", field_path="$.version", allowed=1)
        return HostError("request_invalid", "The version must be the integer 1.", field_path="$.version", allowed=1)
    if path[:1] in (["action_id"], ["query_id"]):
        return HostError("action_id_invalid", "An id must be 1 to 64 letters, digits, '-' or '_'.",
                         field_path=textsafety.field_path(path))
    if path[:1] == ["op"]:
        return HostError("request_invalid", "The game has no operation by that name.", field_path="$.op")
    message = _KEYWORD_MESSAGES.get(error.validator, "The request does not match the game's command contract.")
    code = "invalid_args" if path[:1] == ["args"] else "request_invalid"
    allowed = actual = None
    if error.validator in ("maximum", "minimum", "exclusiveMaximum", "exclusiveMinimum") and \
            isinstance(error.validator_value, (int, float)) and abs(error.validator_value) <= 1_000_000:
        allowed = error.validator_value
        if isinstance(error.instance, (int, float)) and not isinstance(error.instance, bool) and abs(error.instance) <= 1_000_000:
            actual = error.instance
    if error.validator == "required" and isinstance(error.validator_value, list):
        missing = [k for k in error.validator_value if isinstance(error.instance, dict) and k not in error.instance]
        if missing:
            path = path + [missing[0]]
    return HostError(code, message, field_path=textsafety.field_path(path), allowed=allowed, actual=actual)


def effect_args_error(error: HostError) -> HostError:
    """effect.start's arguments as the kernel host types them (its CheckArgs and CheckEffectArgs): a contract violation
    in them is request_invalid, and every problem in params is one, at $.args.params (reserved names included)."""
    path = error.field_path or ""
    if error.code != "invalid_args" or not path.startswith("$.args."):
        return error
    if path.startswith("$.args.params"):
        return HostError("request_invalid", "Effect parameters are bounded scalars under plain lowercase names; "
                         "identity and authority names are refused.", field_path="$.args.params")
    if path.startswith("$.args.area.center_m"):
        path = "$.args.area.center_m"
    return HostError("request_invalid", error.message, field_path=path)


LEAD_NAMES_A_BUBBLE = "A lead names the bubble it leads (its effect: id)."
LEAD_NAMES_A_SPOT = "A lead names the spot it leads the bubble to."


def lead_args_error(error: HostError, message: Any) -> HostError:
    """goal.set lead's arguments as the kernel host checks them (CheckArgs): a lead whose target is not a bubble (an effect: id;
    never an avatar or a thing), or that names no spot, is request_invalid, at the kernel's paths and in its words."""
    args = message.get("args") if isinstance(message, dict) else None
    if not (isinstance(message, dict) and message.get("op") == "goal.set" and isinstance(args, dict) and args.get("goal") == "lead"):
        return error
    if error.code != "invalid_args" or error.field_path not in ("$.args.target", "$.args.position_m"):
        return error
    target = args.get("target")
    if not isinstance(target, str) or not target.startswith("effect:"):
        return HostError("request_invalid", LEAD_NAMES_A_BUBBLE, field_path="$.args.target")
    return HostError("request_invalid", LEAD_NAMES_A_SPOT, field_path="$.args.position_m")


def nature_args_error(error: HostError) -> HostError:
    """The nature commands' arguments as the kernel host checks them (CommandHostNature.CheckNatureArgs): an argument it
    does not know is field_unknown, anything else wrong with them request_invalid, a position at the whole vector."""
    path = error.field_path or ""
    if error.code != "invalid_args" or not path.startswith("$.args."):
        return error
    if path.startswith("$.args.center_m"):
        path = "$.args.center_m"
    return HostError("request_invalid", error.message, field_path=path)


def base_result(*, principal: str, room_id: str, revision: int, at_utc: str, message: Any,
                known_ops: frozenset[str] | tuple[str, ...]) -> dict:
    """The fields every result carries. Echoes only what is well-formed: an unknown op becomes 'invalid'."""
    op = message.get("op") if isinstance(message, dict) else None
    result = {
        "schema": RESULT_SCHEMA, "version": 1, "ok": True,
        "op": op if isinstance(op, str) and op in known_ops else "invalid",
        "principal": principal, "room_id": room_id, "revision": revision,
        "replayed": False, "preview": False, "at_utc": at_utc,
    }
    if isinstance(message, dict):
        key = {"command": "action_id", "query": "query_id"}.get(ids.kind_of(message.get("schema")))
        value = message.get(key) if key else None
        if isinstance(value, str) and _ID.fullmatch(value):
            result[key] = value
    return result


def failure(result: dict, error: HostError) -> dict:
    """Turn a base result into a refusal carrying `error`."""
    result = dict(result)
    result["ok"] = False
    body: dict = {"code": error.code, "message": textsafety.display_text(error.message, 500),
                  "retryable": error.retryable}
    if error.field_path:
        body["field_path"] = error.field_path
    # `allowed` and `actual` are contract scalars (or short lists of them). A value that is not one,
    # for example a requester's revision of 10**30, is left out rather than breaking the result.
    if error.has_allowed and _echoable(error.allowed):
        body["allowed"] = error.allowed
    if error.actual is not None and _echoable(error.actual):
        body["actual"] = error.actual
    if error.code == "energy_exhausted" and error.balance is not None and error.price is not None:
        body["balance"], body["price"] = int(error.balance), int(error.price)
    result["error"] = body
    return result


def _scalar(value: Any) -> bool:
    if isinstance(value, bool) or value is None:
        return True
    if isinstance(value, (int, float)):
        return value == value and -1_000_000 <= value <= 1_000_000
    if isinstance(value, str):
        return len(value) <= 64 and not textsafety.hidden_characters(value)
    return False


def _echoable(value: Any) -> bool:
    if isinstance(value, list):
        return len(value) <= 16 and all(_scalar(item) and item is not None for item in value)
    return _scalar(value)


_VALUE_MESSAGES = {
    "integer_range": "A number is outside the 64-bit integer range.",
    "non_finite": "Numbers must be finite.",
    "hidden_text": "A text value contains invisible characters.",
    "key_type": "Field names must be text.",
    "type": "A value is not plain JSON.",
}


def value_error(problems: list) -> HostError:
    """One refusal for the first value problem (see contract.value_problems)."""
    path, kind = problems[0]
    code = "invalid_args" if path[:1] == ["args"] else "request_invalid"
    return HostError(code, _VALUE_MESSAGES.get(kind, "A value is not allowed here."), field_path=textsafety.field_path(path))


def forbidden_key_error(path: list, reason: str) -> HostError:
    if reason == "authority":
        return HostError("field_unknown", "Identity and approval fields are never accepted in a request.",
                         field_path=textsafety.field_path(path))
    return HostError("field_unknown", "Field names inside args are plain lowercase tokens.",
                     field_path=textsafety.field_path(path))
