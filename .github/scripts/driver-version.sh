#!/usr/bin/env bash
# Prints the MongoDB.Driver version to test with:
#   floor       the lowest supported version (MongoDriverVersion in Directory.Build.props)
#   latest      the newest stable release on nuget.org
#   prerelease  the newest version on nuget.org, prereleases included
# Anything else is printed as it is.
set -euo pipefail

case "${1:?usage: driver-version.sh floor|latest|prerelease|<version>}" in
  floor)
    sed -nE 's#.*<MongoDriverVersion[^>]*>([^<]+)</MongoDriverVersion>.*#\1#p' Directory.Build.props
    ;;
  latest | prerelease)
    # The flat container lists versions in ascending order.
    filter='.versions | last'
    if [ "$1" = latest ]; then filter='[.versions[] | select(contains("-") | not)] | last'; fi
    curl -fsSL --retry 3 https://api.nuget.org/v3-flatcontainer/mongodb.driver/index.json | jq -r "$filter"
    ;;
  *)
    echo "$1"
    ;;
esac
