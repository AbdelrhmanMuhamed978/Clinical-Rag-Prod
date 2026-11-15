"""Notebook startup registration and bounded heartbeats, without printing secrets or response bodies."""

import logging
import random
import threading
import time
import uuid
from urllib.parse import urlsplit

import httpx
from operations import configure_logging

LOGGER = logging.getLogger("clinical_rag.colab")


class RegistrationError(Exception):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__({
            "unauthorized": "Colab registration was not authorized. Check the shared secret in both services.",
            "manual_override": "Automatic registration is blocked by the administrator's manual override.",
            "superseded": "This notebook runtime was superseded. Its heartbeat has stopped.",
            "invalid": "Colab registration was rejected. Check the base URL and use an API key of at least 16 characters.",
            "offline": "The ASP.NET registration endpoint is unavailable. Retry startup when the backend is online.",
        }.get(code, "Colab registration could not be completed."))


def registration_request(backend_url: str, base_url: str, api_key: str, secret: str,
                         instance_id: str, heartbeat: bool = False) -> None:
    try:
        backend = urlsplit(backend_url)
        if (backend.scheme != "https" or not backend.hostname or backend.username or backend.password
                or backend.query or backend.fragment or backend.path not in {"", "/"} or backend.port not in {None, 443}
                or any(ord(character) <= 32 for character in backend_url) or "\\" in backend_url
                or not secret or len(secret) > 4096 or any(not 33 <= ord(character) <= 126 for character in secret)
                or not 16 <= len(api_key) <= 4096 or any(not 33 <= ord(character) <= 126 for character in api_key)):
            raise RegistrationError("invalid")
        with httpx.Client(timeout=5.0, follow_redirects=False, trust_env=False) as client:
            response = client.post(backend_url.rstrip("/") + "/api/admin/colab-registration",
                                   headers={"X-Colab-Registration-Secret": secret}, json={
                                       "baseUrl": base_url, "apiKey": api_key, "instanceId": instance_id, "heartbeat": heartbeat,
                                   })
        if response.status_code == 409:
            try:
                code = response.json().get("code")
            except (ValueError, AttributeError):
                code = "invalid"
            raise RegistrationError(code if code in {"manual_override", "superseded"} else "offline")
        if response.status_code in {401, 403}:
            raise RegistrationError("unauthorized")
        if response.status_code >= 500:
            raise RegistrationError("offline")
        if response.status_code != 200:
            raise RegistrationError("invalid")
        try:
            if len(response.content) > 16384 or response.json().get("registered") is not True:
                raise RegistrationError("invalid")
        except (ValueError, AttributeError):
            raise RegistrationError("invalid") from None
    except httpx.HTTPError:
        raise RegistrationError("offline") from None
    except (ValueError, TypeError):
        raise RegistrationError("invalid") from None


class RegistrationHandle:
    def __init__(self, instance_id: str, stopped: threading.Event) -> None:
        self.instance_id, self._stopped = instance_id, stopped
        self.thread: threading.Thread | None = None

    def stop(self) -> None:
        self._stopped.set()
        if self.thread is not None:
            self.thread.join(timeout=6)


def start_registration(backend_url: str, base_url: str, api_key: str, secret: str, interval_seconds: float = 30) -> RegistrationHandle:
    configure_logging("colab")
    instance_id = uuid.uuid4().hex
    # Duplicate startup attempts use the same instance ID, so a lost success response cannot rotate again.
    for attempt in range(4):
        try:
            registration_request(backend_url, base_url, api_key, secret, instance_id)
            break
        except RegistrationError as error:
            if error.code != "offline" or attempt == 3:
                raise
            time.sleep(min(8, 1 << attempt) * (0.5 + random.random()))
    stopped = threading.Event()
    handle = RegistrationHandle(instance_id, stopped)

    def heartbeat_loop() -> None:
        while not stopped.wait(max(10, min(60, interval_seconds)) * (0.9 + random.random() * 0.2)):
            try:
                registration_request(backend_url, base_url, api_key, secret, instance_id, heartbeat=True)
            except RegistrationError as error:
                LOGGER.warning("registration_failed", extra={"event": "registration_failed"})
                if error.code != "offline":
                    stopped.set()  # Never let an old runtime overwrite a newer registration or manual setting.
    handle.thread = threading.Thread(target=heartbeat_loop, name="colab-heartbeat", daemon=True)
    handle.thread.start()
    return handle


def wait_for_model_ready(api_key: str, timeout_seconds: float = 900) -> dict:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with httpx.Client(timeout=2.0, trust_env=False) as client:
                response = client.get("http://127.0.0.1:8000/ready", headers={"X-Colab-Key": api_key})
                if response.status_code == 200 and response.json().get("ready") is True:
                    return response.json()
        except (httpx.HTTPError, ValueError, AttributeError):
            pass
        time.sleep(2)
    raise RuntimeError("The local Colab models did not become ready. Check the GPU runtime and restart the server cell.")
