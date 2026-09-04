# Proving ACME issuance without spending a rate limit

Pebble is the ACME test CA the Let's Encrypt team publishes: a real ACME v2 server that
issues from a throwaway root. It exists so the issuance path can be driven end to end —
account registration, order, HTTP-01 challenge, finalize, and the certificate coming back —
against something that will refuse a malformed request exactly as Let's Encrypt would, and
without touching an account whose failures are counted for a week.

Off by default, behind a compose profile, so nothing about production changes.

```
docker compose --profile acmetest up -d pebble
```

## What the config does

`httpPort: 8080` points Pebble's validation fetch at the port the edge listens on inside the
compose network, so `http://<domain>:8080/.well-known/acme-challenge/<token>` reaches the
edge's challenge endpoint over Docker DNS. Pebble serves its own API over a self-signed
certificate by design, which is why `Edge__Certificates__AcceptInsecureAcmeDirectory` exists
— and why `CertesAcmeIssuer` refuses that switch outright when the directory is a Let's
Encrypt one.

## Running an issuance

1. Seed a domain the edge will accept. The allow-list is the real one: site-host answers 200
   only for a verified domain linked to a site, so the test domain needs a `tenancy.domains`
   row with `verified_at` set and a `site_id`. Use a name Docker DNS can resolve to the edge
   container — add it as a network alias on the `edge` service.
2. Point the edge at Pebble and turn TLS on:

   ```
   Edge__Certificates__AcmeDirectory=https://pebble:14000/dir
   Edge__Certificates__AcceptInsecureAcmeDirectory=true
   Edge__Certificates__TlsEnabled=true
   Edge__Certificates__ContactEmail=acme-test@example.invalid
   ```

3. Publish `tenant.domain.verified` for the hostname, or connect to the TLS port with that
   SNI name and let on-demand issuance run.
4. Check `edge.certificates`: a row with a `not_after` about five years out (Pebble's default)
   and `last_error` null. A failure leaves the CA's own explanation in `last_error`, which is
   the sentence worth reading.

## What this does not prove

That Let's Encrypt will issue. Pebble does not enforce rate limits, does not check CAA
records, and validates from inside the compose network rather than from the internet. It
proves our half of the conversation is correct; the first real issuance still happens against
the Let's Encrypt **staging** directory, which is what `ACME_DIRECTORY` defaults to.
