"""Colab model server package."""

import sys
from pathlib import Path

# The deterministic pipeline modules live in python-backend/ so the local
# service and the Colab model server share one source of truth.
_BACKEND = Path(__file__).resolve().parents[1] / "python-backend"
if _BACKEND.is_dir() and str(_BACKEND) not in sys.path:
    sys.path.insert(0, str(_BACKEND))
