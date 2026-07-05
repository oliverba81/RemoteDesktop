#!/usr/bin/env bash
#
# Builds the Synology .spk for the RemoteDesktop relay (arch x86_64, DSM 7).
# Runs entirely on Linux — no Synology toolkit required (a .spk is just a tar).
#
# Usage: packaging/synology/build-spk.sh [version]
#
set -euo pipefail

VERSION="${1:-1.0.0-0001}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "${HERE}/../.." && pwd)"
DIST="${REPO}/dist"
STAGE="${DIST}/spk-stage"
PAYLOAD="${DIST}/payload"

RELAY_CSPROJ="${REPO}/src/RemoteDesktop.Relay/RemoteDesktop.Relay.csproj"

echo ">> Cleaning ${DIST}"
rm -rf "${STAGE}" "${PAYLOAD}"
mkdir -p "${STAGE}" "${PAYLOAD}/bin" "${PAYLOAD}/ui/images"

echo ">> Publishing relay (linux-x64, self-contained, invariant globalization)"
dotnet publish "${RELAY_CSPROJ}" -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -p:InvariantGlobalization=true \
    -o "${PAYLOAD}/bin"

echo ">> Assembling DSM UI (client download page)"
cp "${HERE}/ui/config" "${PAYLOAD}/ui/config"
cp "${HERE}/ui/index.html" "${PAYLOAD}/ui/index.html"
# The port-config resource worker resolves protocol-file relative to the
# package target dir, so the firewall .sc must ship inside package.tgz.
cp "${HERE}/RemoteDesktopRelay.sc" "${PAYLOAD}/ui/RemoteDesktopRelay.sc"
for s in 24 32 48 64 72; do
    python3 "${HERE}/make-icons.py" "${PAYLOAD}/ui/images/icon_${s}.png" "${s}"
done

echo ">> Building package.tgz (extracted to target/ on the NAS)"
tar --format=gnu --owner=root --group=root -C "${PAYLOAD}" -czf "${STAGE}/package.tgz" bin ui

echo ">> Package icons"
python3 "${HERE}/make-icons.py" "${STAGE}/PACKAGE_ICON.PNG" 72
python3 "${HERE}/make-icons.py" "${STAGE}/PACKAGE_ICON_256.PNG" 256

echo ">> INFO"
sed "s/@VERSION@/${VERSION}/" "${HERE}/INFO.in" > "${STAGE}/INFO"
# Fields the DSM package validator expects, computed at pack time (same as the
# official pkgscripts-ng toolkit and SynoCommunity's spksrc do).
echo "checksum=\"$(md5sum "${STAGE}/package.tgz" | cut -d' ' -f1)\"" >> "${STAGE}/INFO"
echo "extractsize=\"$(du -sk "${PAYLOAD}" | cut -f1)\"" >> "${STAGE}/INFO"
echo "create_time=\"$(date +%Y%m%d-%H:%M:%S)\"" >> "${STAGE}/INFO"

echo ">> scripts + conf"
cp -r "${HERE}/scripts" "${STAGE}/scripts"
cp -r "${HERE}/conf" "${STAGE}/conf"
chmod 755 "${STAGE}/scripts/"*

echo ">> Assembling .spk (INFO must be the first archive entry)"
OUT="${DIST}/RemoteDesktopRelay-x86_64-${VERSION}.spk"
( cd "${STAGE}" && tar --format=gnu --owner=root --group=root -cf "${OUT}" \
    INFO package.tgz scripts conf PACKAGE_ICON.PNG PACKAGE_ICON_256.PNG )

echo ">> Done: ${OUT}"
echo ">> Contents:"
tar -tf "${OUT}"
