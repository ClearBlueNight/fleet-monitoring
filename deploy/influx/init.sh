#!/bin/sh
# Kreira baze sa retention-om (retention se u Core-u zadaje samo pri kreiranju).
# Auth will be done (servis slusa samo na 127.0.0.1). Token bootstrap to be done.
set -eu
HOST="${INFLUX_HOST:-http://influxdb3:8181}"

echo "Cekam InfluxDB na $HOST ..."
i=0
until curl -fsS "$HOST/health" >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -gt 60 ]; then
    echo "InfluxDB nije postao spreman na vreme."
    exit 1
  fi
  sleep 2
done

create_db() {
  name="$1"; retention="$2"
  code=$(curl -s -o /tmp/resp -w '%{http_code}' -X POST "$HOST/api/v3/configure/database" \
    -H 'Content-Type: application/json' \
    -d "{\"db\":\"$name\",\"retention_period\":\"$retention\"}")
  case "$code" in
    2*) echo "Kreirana baza $name (retention $retention)" ;;
    *)
      if grep -qi 'already exists' /tmp/resp; then
        echo "Baza $name vec postoji"
      else
        echo "Greska pri kreiranju $name (HTTP $code):"; cat /tmp/resp; exit 1
      fi ;;
  esac
}

create_db telemetry 30d
create_db telemetry_1m 365d
echo "Influx init zavrsen."
