#!/usr/bin/env bash
#
# Install the Typst CLI locally into ./.typst/bin so the service and tests can
# shell out to `typst` without it being committed to the repo. The container
# image installs Typst the same way (see src/TypstRender.Service/Dockerfile).
#
# Usage: ./scripts/install-typst.sh && source ./scripts/install-typst.sh.env
#
set -euo pipefail

TYPST_VERSION="${TYPST_VERSION:-0.15.0}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_DIR="${REPO_ROOT}/.typst/bin"

if command -v typst >/dev/null 2>&1 && typst --version 2>/dev/null | grep -q "${TYPST_VERSION}"; then
  echo "typst ${TYPST_VERSION} already on PATH: $(command -v typst)"
  exit 0
fi
if [ -x "${INSTALL_DIR}/typst" ] && "${INSTALL_DIR}/typst" --version 2>/dev/null | grep -q "${TYPST_VERSION}"; then
  echo "typst ${TYPST_VERSION} already installed at ${INSTALL_DIR}/typst"
else
  case "$(uname -m)" in
    x86_64|amd64) ARCH="x86_64" ;;
    aarch64|arm64) ARCH="aarch64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
  TARGET="typst-${ARCH}-unknown-linux-musl"
  URL="https://github.com/typst/typst/releases/download/v${TYPST_VERSION}/${TARGET}.tar.xz"

  mkdir -p "${INSTALL_DIR}"
  echo "Downloading ${URL} ..."
  curl -fsSL "${URL}" | tar -xJ --strip-components=1 -C "${INSTALL_DIR}" "${TARGET}/typst"
  chmod +x "${INSTALL_DIR}/typst"
  echo "Installed: $("${INSTALL_DIR}/typst" --version)"
fi

cat > "${REPO_ROOT}/scripts/install-typst.sh.env" <<EOF
export PATH="${INSTALL_DIR}:\${PATH}"
EOF

echo
echo "Activate in your current shell with:"
echo "  source ./scripts/install-typst.sh.env"
