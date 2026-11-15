"""Resolve rotating Colab credentials through a fixed, authenticated ASP.NET service endpoint."""

import ipaddress
import os
from urllib.parse import urlsplit

import httpx

from operations import Step, request_id


def validate_base_url(value: object) -> str:
    if (not isinstance(value, str) or not value or len(value) > 2048 or "\\" in value
            or any(ord(character) <= 32 or 127 <= ord(character) <= 159 for character in value)):
        raise ValueError("Invalid Colab base URL")
    parsed = urlsplit(value)
    host = parsed.hostname or ""
    if not host or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in {"", "/"}:
        raise ValueError("Invalid Colab origin")
    loopback = host == "localhost"
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        if ":" in host or host.replace(".", "").isdigit():
            raise ValueError("Refused Colab address") from None
    else:
        if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped is not None:
            address = address.ipv4_mapped
        loopback = address.is_loopback
        if not address.is_global and not loopback:
            raise ValueError("Refused Colab address")
    allow_local = os.getenv("COLAB_ALLOW_INSECURE_LOOPBACK", "").lower() == "true"
    if loopback and allow_local and parsed.scheme in {"http", "https"}:
        return value.rstrip("/") + "/"
    if (parsed.scheme != "https" or parsed.port not in {None, 443} or loopback or "." not in host
            or host.endswith((".local", ".localhost"))):
        raise ValueError("Colab requires a public HTTPS origin")
    return value.rstrip("/") + "/"


class ColabConnectionResolver:
    def __init__(self, backend_base_url: str, service_key: str | None) -> None:
        self.base_url, self.service_key = backend_base_url.rstrip("/"), service_key

    async def __call__(self) -> tuple[str, str]:
        # Imported here to keep the pipeline module independent of the FastAPI boundary.
        from pipeline import ServiceUnavailable
        with Step("pipeline", "pipeline.colab_configuration"):
            try:
                parsed = urlsplit(self.base_url)
                if (not self.service_key or parsed.scheme not in {"https", "http"} or not parsed.hostname
                        or parsed.username or parsed.password or parsed.query or parsed.fragment):
                    raise ValueError("Missing fixed service configuration")
                async with httpx.AsyncClient(timeout=2.0, follow_redirects=False, trust_env=False) as client:
                    response = await client.get(self.base_url + "/api/internal/colab-connection", headers={
                        "X-Pipeline-Key": self.service_key, "X-Request-Id": request_id(),
                    })
                    response.raise_for_status()
                    if len(response.content) > 16384:
                        raise ValueError("Oversized connection response")
                    body = response.json()
                    base_url = validate_base_url(body.get("baseUrl"))
                    key = body.get("apiKey")
                    if not isinstance(key, str) or not 16 <= len(key) <= 4096 or any(not 33 <= ord(character) <= 126 for character in key):
                        raise ValueError("Invalid connection credential")
                    return base_url + "generate", key
            except (httpx.HTTPError, ValueError, TypeError, AttributeError):
                raise ServiceUnavailable("colab") from None
