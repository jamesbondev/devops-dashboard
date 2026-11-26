#!/bin/sh
# Trust extra root CAs (e.g. a corporate TLS-inspection proxy such as Zscaler) placed in ./certs on the host,
# mounted read-only at /certs. Builds a combined bundle and points OpenSSL, .NET, Node, git and curl at it,
# then execs the real command. With no certs it does nothing.
set -eu
extra=$(find /certs -maxdepth 1 -type f \( -name '*.crt' -o -name '*.pem' -o -name '*.cer' \) 2>/dev/null | sort || true)
if [ -n "$extra" ]; then
  bundle=/tmp/ca-bundle.pem
  extra_only=/tmp/ca-extra.pem
  : > "$extra_only"
  for f in $extra; do
    if grep -q "BEGIN CERTIFICATE" "$f"; then cat "$f" >> "$extra_only"
    else openssl x509 -inform DER -in "$f" -outform PEM >> "$extra_only" 2>/dev/null || echo "with-ca: could not read $f (expect PEM or DER)" >&2
    fi
    echo >> "$extra_only"
  done
  cat /etc/ssl/certs/ca-certificates.crt "$extra_only" > "$bundle"
  export SSL_CERT_FILE="$bundle" NODE_EXTRA_CA_CERTS="$extra_only" REQUESTS_CA_BUNDLE="$bundle" \
         CURL_CA_BUNDLE="$bundle" GIT_SSL_CAINFO="$bundle"
  echo "with-ca: trusting $(grep -c 'BEGIN CERTIFICATE' "$extra_only") extra CA certificate(s) from /certs" >&2
fi
exec "$@"
