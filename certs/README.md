# Extra trusted CA certificates

If your network inspects HTTPS (Zscaler, Netskope, a corporate proxy…), the containers reject GitHub / Copilot
(and possibly Azure DevOps) with `certificate verify failed: unable to get local issuer certificate`.

Export your company's **root** CA certificate (and any intermediate) and drop it here as `.crt`, `.pem` or `.cer`
(PEM or DER). Then `docker compose up -d`. Files here are git-ignored.
