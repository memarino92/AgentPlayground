#!/bin/sh
set -eu
cd /work
tar --no-same-owner -xf -
cp /template/Program.csproj .
# Explicit, trusted project: source directives cannot add packages, imports or MSBuild properties.
if ! dotnet build Program.csproj --configfile /template/NuGet.Config --nologo -v:q -o /work/out > /work/build.log 2>&1; then
    head -c 30000 /work/build.log >&2
    exit 200
fi
# Container lifetime is also bounded if the runner dies. The external runner enforces output limits.
exec dotnet /work/out/Program.dll < /work/input.txt
