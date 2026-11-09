"""How change_prepare echoes a payload in its preview: source/XML text that the worker persisted as the prepared
input file is shown by UTF-8 byte count and SHA-256 only (Classic screen edits keep their full text)."""
from __future__ import annotations

import hashlib


def preview_payload(payload: dict) -> dict:
    shown = dict(payload)
    for name in ("xml", "source"):
        if isinstance(shown.get(name), str):
            data = shown[name].encode("utf-8")
            shown[name] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    return shown
