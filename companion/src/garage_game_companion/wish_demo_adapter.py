"""Local deterministic demonstration adapter. No AI provider, account, network or shell calls.

Recognizes only wishes containing 'glow', 'light' or 'lantern'; every other wish is unsupported.
Run through wish_runner's generated demo configuration, not as the real AI companion.
"""
import json
import sys


def send(value):
    print(json.dumps(value, ensure_ascii=True), flush=True)


def main():
    hello = json.loads(sys.stdin.readline())
    if hello.get("protocol") != "enfractal.wish_adapter" or "effect_start" not in [tool["name"] for tool in hello.get("tools", [])]:
        return 1
    send({"type": "ready", "protocol": "enfractal.wish_adapter", "version": 1, "exclusive": True, "tools": ["effect_start"]})
    wish = json.loads(sys.stdin.readline())
    if not any(word in wish.get("wish", {}).get("text", "").lower() for word in ("glow", "light", "lantern")):
        send({"type": "done", "status": "unsupported"})
        return 0
    send({"type": "tool_call", "id": "lantern", "name": "effect_start", "arguments": {
        "capability": "glow", "targets": ["avatar:companion"], "params": {},
        "area": {"center_m": [0, 0, 0], "radius_m": 0.5}, "duration_s": 30}})
    result = json.loads(sys.stdin.readline())
    send({"type": "done", "status": "completed" if result.get("result", {}).get("ok") else "failed"})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
