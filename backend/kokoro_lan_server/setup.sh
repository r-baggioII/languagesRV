#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

# Kokoro 0.9.4 requiere Python 3.10, 3.11 o 3.12.
PYTHON=""
for candidate in python3.12 python3.11 python3.10 python3; do
  if command -v "$candidate" >/dev/null 2>&1 && \
     "$candidate" -c 'import sys; assert (3,10) <= sys.version_info[:2] <= (3,12)' >/dev/null 2>&1; then
    PYTHON="$candidate"
    break
  fi
done
if [[ -z "$PYTHON" ]]; then
  echo "Se necesita Python 3.10, 3.11 o 3.12; instalá uno antes de ejecutar setup.sh" >&2
  exit 1
fi

"$PYTHON" -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip
# Primera prueba prioriza PyTorch para CPU: no hace falta GPU NVIDIA.
python -m pip install torch --index-url https://download.pytorch.org/whl/cpu
python -m pip install -r requirements.txt
printf '\nInstalacion completada. Iniciar: ./run.sh\n'
