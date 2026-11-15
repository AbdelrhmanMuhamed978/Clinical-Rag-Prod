"""Bound request buffering without interfering with SSE disconnect handling."""

from typing import Any

from starlette.responses import JSONResponse


class NoStorePreviewMiddleware:
    """Preview inputs/results must not enter HTTP caches, including rejected requests."""
    def __init__(self, app: Any) -> None:
        self.app = app

    async def __call__(self, scope: dict[str, Any], receive: Any, send: Any) -> None:
        if scope["type"] != "http" or scope.get("path") != "/rules/preview":
            await self.app(scope, receive, send)
            return

        async def private_send(message: dict[str, Any]) -> None:
            if message["type"] == "http.response.start":
                headers = [(key, value) for key, value in message.get("headers", []) if key.lower() != b"cache-control"]
                message = {**message, "headers": headers + [(b"cache-control", b"no-store")]}
            await send(message)

        await self.app(scope, receive, private_send)


class BodySizeLimitMiddleware:
    def __init__(self, app: Any, max_bytes: int = 1024 * 1024) -> None:
        self.app = app
        self.max_bytes = max_bytes

    async def __call__(self, scope: dict[str, Any], receive: Any, send: Any) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        headers = dict(scope.get("headers", []))
        try:
            declared_length = int(headers.get(b"content-length", b"0"))
        except ValueError:
            await JSONResponse({"detail": "invalid content length"}, status_code=400)(scope, receive, send)
            return
        if declared_length < 0 or declared_length > self.max_bytes:
            await JSONResponse({"detail": "request body too large"}, status_code=413)(scope, receive, send)
            return

        body = bytearray()
        while True:
            message = await receive()
            if message["type"] == "http.disconnect":
                return
            body.extend(message.get("body", b""))
            if len(body) > self.max_bytes:
                await JSONResponse({"detail": "request body too large"}, status_code=413)(scope, receive, send)
                return
            if not message.get("more_body", False):
                break

        replayed = False

        async def replay() -> dict[str, Any]:
            nonlocal replayed
            if replayed:
                # StreamingResponse must still observe a genuine client disconnect.
                return await receive()
            replayed = True
            return {"type": "http.request", "body": bytes(body), "more_body": False}

        await self.app(scope, replay, send)
