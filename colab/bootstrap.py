"""Source ZIP validation and a Python-3.12 GPU environment for modern Colab hosts.

No credentials are read here. The notebook obtains them from Colab Secrets only
when launching the model server/registration subprocess, via its environment.
"""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile


UV_VERSION = "0.9.12"
PYTHON_VERSION = "3.12"


def extract_source(archive_path: Path, destination: Path) -> None:
    """Validate the builder's exact manifest, reject traversal/links, extract once."""
    if destination.exists():
        raise ValueError("Destination already exists; use a new source directory.")
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or "colab-source-manifest.json" not in names:
            raise ValueError("Archive must contain a unique source manifest.")
        if len(names) > 1000 or sum(item.file_size for item in archive.infolist()) > 32 * 1024 * 1024:
            raise ValueError("Source archive exceeds the bounded file/size limits.")
        manifest = json.loads(archive.read("colab-source-manifest.json"))
        if manifest.get("format") != 1 or manifest.get("python") != PYTHON_VERSION:
            raise ValueError("Unsupported source archive manifest.")
        expected = {item["path"]: item for item in manifest["files"]}
        for member in list(expected) + names:
            name = PurePosixPath(member)
            if name.is_absolute() or ".." in name.parts or "\\" in member or ":" in member or not name.parts:
                raise ValueError("Archive contains an unsafe member path.")
        if len(expected) != len(manifest["files"]) or set(names) != set(expected) | {"colab-source-manifest.json"}:
            raise ValueError("Archive members do not match the source manifest.")
        for item in archive.infolist():
            name = PurePosixPath(item.filename)
            if name.is_absolute() or ".." in name.parts or "\\" in item.filename or ":" in item.filename or not name.parts:
                raise ValueError("Archive contains an unsafe member path.")
            if stat.S_ISLNK(item.external_attr >> 16):
                raise ValueError("Archive contains a symbolic link.")
            if item.filename != "colab-source-manifest.json":
                data = archive.read(item)
                info = expected[item.filename]
                if len(data) != info["bytes"] or hashlib.sha256(data).hexdigest() != info["sha256"]:
                    raise ValueError("Archive source integrity verification failed.")
        destination.parent.mkdir(parents=True, exist_ok=True)
        staging = Path(tempfile.mkdtemp(prefix="clinical-rag-source-", dir=destination.parent))
        try:
            archive.extractall(staging)
            staging.rename(destination)
        except Exception:
            shutil.rmtree(staging)
            raise


def install_environment(root: Path) -> Path:
    if sys.platform != "linux":
        raise ValueError("The GPU environment is intended for Linux/Colab, not the local Windows pipeline.")
    python = root / ".venv-colab" / "bin" / "python"
    print("Installing the Python 3.12 environment manager...", flush=True)
    subprocess.run([sys.executable, "-m", "pip", "install", f"uv=={UV_VERSION}"], check=True)
    subprocess.run([sys.executable, "-m", "uv", "python", "install", PYTHON_VERSION], check=True)
    if not python.exists():
        subprocess.run([sys.executable, "-m", "uv", "venv", "--python", PYTHON_VERSION,
                        str(root / ".venv-colab")], check=True)
    print("Installing GPU packages (several GB; progress below)...", flush=True)
    subprocess.run([sys.executable, "-m", "uv", "pip", "install", "--python", str(python),
                    "--only-binary", ":all:", "-r", str(root / "requirements.txt"),
                    "-r", str(root / "requirements-colab.txt")], check=True)
    subprocess.run([sys.executable, "-m", "uv", "pip", "check", "--python", str(python)], check=True)
    subprocess.run([str(python), "-c", "import torch, transformers, vllm; "
                    "assert torch.cuda.is_available(), 'No GPU detected'; "
                    "print('Python/GPU packages:', __import__('sys').version.split()[0], torch.__version__, "
                    "transformers.__version__, vllm.__version__); print('GPU:', torch.cuda.get_device_name(0))"], check=True)
    print("INSTALL COMPLETE — Python 3.12 and GPU packages validated. Run the next cell.", flush=True)
    return python


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, required=True)
    args = parser.parse_args()
    install_environment(args.install.resolve())


if __name__ == "__main__":
    main()
