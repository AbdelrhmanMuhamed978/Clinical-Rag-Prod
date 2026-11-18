"""Test-session path setup for the isolated Python backend."""

import sys
from pathlib import Path

BACKEND = Path(__file__).resolve().parent / "python-backend"
if str(BACKEND) not in sys.path:
    sys.path.insert(0, str(BACKEND))
