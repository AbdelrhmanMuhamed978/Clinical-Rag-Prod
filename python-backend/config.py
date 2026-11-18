"""Request-scoped configuration for the verified Python pipeline."""

import copy
import json
import math
import os
from pathlib import Path
from typing import Any, Mapping, Optional

_CONFIG_DIR = Path(__file__).resolve().parent / "config"

SCHEMA = json.loads((_CONFIG_DIR / "config.schema.json").read_text(encoding="utf-8"))
PROMPT_PLACEHOLDERS: dict[str, set[str]] = {
    name: set(rule["x-requiredPlaceholders"])
    for name, rule in SCHEMA["properties"]["prompts"]["properties"].items()
}


def _schema_defaults(schema: dict[str, Any]) -> Any:
    if "default" in schema:
        return copy.deepcopy(schema["default"])
    if schema["type"] == "object":
        return {key: _schema_defaults(rule) for key, rule in schema["properties"].items()
                if "default" in rule or rule["type"] == "object"}
    return None


def _load_default_prompts() -> dict[str, str]:
    path = _CONFIG_DIR / "default_prompts.json"
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


DEFAULTS: dict[str, Any] = _schema_defaults(SCHEMA)
DEFAULTS["prompts"] = _load_default_prompts()


def render_prompt(name: str, text: str, **values: Any) -> str:
    """Substitute known placeholders only; preserve all other braces verbatim."""
    output = text
    for key in sorted(PROMPT_PLACEHOLDERS.get(name, set()), key=len, reverse=True):
        if key in values:
            output = output.replace("{" + key + "}", str(values[key]))
    return output


def validate_prompt(name: str, text: str) -> Optional[str]:
    required = PROMPT_PLACEHOLDERS.get(name)
    if required is None:
        return f"unknown prompt name: {name}"
    missing = sorted(key for key in required if "{" + key + "}" not in text)
    if missing:
        placeholders = ", ".join("{" + key + "}" for key in missing)
        return f"prompt '{name}' missing required placeholder(s): {placeholders}"
    return None


def _merge(base: dict[str, Any], override: Mapping[str, Any]) -> dict[str, Any]:
    merged = copy.deepcopy(base)
    for key, value in override.items():
        if isinstance(value, Mapping) and isinstance(merged.get(key), dict):
            merged[key] = _merge(merged[key], value)
        else:
            merged[key] = copy.deepcopy(value)
    return merged


def _validate_schema(value: Any, schema: dict[str, Any], path: str = "configuration") -> Any:
    """Evaluate the same focused JSON Schema keywords as the ASP.NET config validator."""
    kind = schema["type"]
    try:
        numeric = type(value) in (int, float) and math.isfinite(value)
    except OverflowError:
        numeric = False
    matches = {
        "object": isinstance(value, dict), "string": isinstance(value, str), "boolean": type(value) is bool,
        "number": numeric, "integer": numeric and int(value) == value,
    }.get(kind)
    if not matches:
        raise ValueError(f"invalid {path} setting: expected {kind}")
    if kind == "object":
        for key in schema.get("required", []):
            if key not in value:
                raise ValueError(f"invalid {path} setting: required key missing")
        for key, rule in schema["properties"].items():
            if key in value:
                child_path = key if path == "configuration" else f"{path}.{key}"
                value[key] = _validate_schema(value[key], rule, child_path)
    elif kind in ("number", "integer"):
        if not schema["minimum"] <= value <= schema["maximum"]:
            raise ValueError(f"invalid {path} setting")
        if kind == "integer":
            value = int(value)
    elif kind == "string" and not schema["minLength"] <= len(value) <= schema["maxLength"]:
        raise ValueError(f"invalid {path} setting")
    if "const" in schema and value != schema["const"]:
        raise ValueError(f"invalid {path} setting: locked")
    return value


class Config:
    """Immutable-by-convention request configuration.

    A fresh instance is built per analysis request. No process-global mutable
    configuration exists, so concurrent workspaces cannot leak settings.
    """

    def __init__(self, data: Mapping[str, Any]):
        self._data = copy.deepcopy(dict(data))

    @classmethod
    def from_dict(cls, cfg: Optional[Mapping[str, Any]] = None) -> "Config":
        if cfg is not None and not isinstance(cfg, Mapping):
            raise ValueError("configuration must be an object")
        merged = _merge(DEFAULTS, cfg or {})
        # The deterministic verifier is a mandatory safety boundary and cannot
        # be disabled by workspace configuration.
        if isinstance(merged.get("safety"), dict):
            merged["safety"]["verifierLocked"] = True
        merged = _validate_schema(merged, SCHEMA)
        for name in PROMPT_PLACEHOLDERS:
            error = validate_prompt(name, merged["prompts"][name])
            if error:
                raise ValueError(error)
        return cls(merged)

    def get(self, key: str, default: Any = None) -> Any:
        current: Any = self._data
        for part in key.split("."):
            if not isinstance(current, Mapping) or part not in current:
                return default
            current = current[part]
        return copy.deepcopy(current)

    def prompt(self, name: str, fallback: str) -> str:
        value = self.get(f"prompts.{name}")
        return value if value else fallback

    def as_dict(self) -> dict[str, Any]:
        return copy.deepcopy(self._data)


def load_from_dict(cfg: Optional[Mapping[str, Any]]) -> Config:
    return Config.from_dict(cfg)


def load_from_env() -> Config:
    path = os.getenv("CONFIG_PATH")
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as handle:
            return Config.from_dict(json.load(handle))
    return Config.from_dict()
