"""Run server/registration in the isolated GPU interpreter, never log credentials."""

import argparse
import json
import os
import queue
import re
import subprocess
import threading

from colab.registration import RegistrationError, start_registration, wait_for_model_ready


def register() -> None:
    key = os.environ.get("COLAB_API_KEY", "")
    backend = os.environ.get("ASPNET_BACKEND_URL", "")
    secret = os.environ.get("COLAB_REGISTRATION_SECRET", "")
    if not key or not backend or not secret:
        raise RuntimeError("Required Colab Secrets are missing or notebook access is disabled.")
    print("Waiting for genuine GPU model readiness (up to 15 minutes)...", flush=True)
    status = wait_for_model_ready(key)
    tunnel = subprocess.Popen(["cloudflared", "tunnel", "--url", "http://127.0.0.1:8000"],
                              stderr=subprocess.PIPE, stdout=subprocess.DEVNULL, text=True)
    urls: queue.Queue[str] = queue.Queue(maxsize=1)

    def drain() -> None:
        for line in tunnel.stderr:
            match = re.search(r"https://[-a-z0-9]+\.trycloudflare\.com", line)
            if match and urls.empty():
                try:
                    urls.put_nowait(match.group(0))
                except queue.Full:
                    pass

    threading.Thread(target=drain, daemon=True).start()
    handle = None
    try:
        try:
            url = urls.get(timeout=60)
        except queue.Empty:
            raise RuntimeError("The model tunnel did not become available within 60 seconds.") from None
        handle = start_registration(backend, url, key, secret)
        print("COLAB REGISTERED — genuine models ready; heartbeat is running.", flush=True)
        print(json.dumps({"model": status.get("model"), "embeddingModel": status.get("embeddingModel"),
                          "embeddingDimensions": status.get("embeddingDimensions"), "gpu": status.get("gpu")}), flush=True)
        while handle.thread is not None and handle.thread.is_alive():
            handle.thread.join(timeout=5)
            if tunnel.poll() is not None:
                raise RuntimeError("The model tunnel stopped. Rerun registration explicitly.")
    finally:
        if handle is not None:
            handle.stop()
        if tunnel.poll() is None:
            tunnel.terminate()
            try:
                tunnel.wait(timeout=10)
            except subprocess.TimeoutExpired:
                tunnel.kill()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("serve", "register"))
    args = parser.parse_args()
    if args.mode == "serve":
        import uvicorn
        uvicorn.run("colab.server:app", host="127.0.0.1", port=8000,
                    log_level="warning", access_log=False)
    else:
        try:
            register()
        except RegistrationError as error:
            # RegistrationError contains only a fixed, sanitized description.
            print(f"REGISTRATION FAILED — {error}", flush=True)
            raise SystemExit(1) from None
        except RuntimeError as error:
            print(f"REGISTRATION FAILED — {error}", flush=True)
            raise SystemExit(1) from None
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
