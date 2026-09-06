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

## Wildcards and DNS-01

**This one is automated.** `tests/Dcms.IntegrationTests/Edge/WildcardIssuanceTests.cs` starts
Pebble and `pebble-challtestsrv` with Testcontainers and drives a real order through
`CertesAcmeIssuer`, so it runs on every `dotnet test` with a Docker daemon. The compose profile
below is for driving it by hand against the whole stack. Two things that cost time when it was
written, both now encoded in the test:

- Both images are built `FROM scratch`, so Testcontainers' `UntilInternalTcpPortIsAvailable`
  wait strategy — which runs a command *inside* the container — hangs forever. Readiness is
  polled from the host instead.
- challtestsrv's management endpoint is `/set-txt`, and **it appends** despite the name. That is
  the property the apex+wildcard case needs; `/add-txt` does not exist and answers 404.


A wildcard identifier can only be validated over DNS-01 — Let's Encrypt refuses every other
challenge type for one — so the wildcard path shares almost nothing with the flow above and
needs its own run.

`pebble-challtestsrv` is the mock DNS server that makes it possible. The property that matters
is that its `/set-txt` endpoint **appends** TXT values at a name instead of replacing them —
despite the name — exactly as a real zone does. That is what lets it reproduce the one mistake this path is most likely to contain:

> An order for `example.test` **and** `*.example.test` produces two authorizations. ACME strips
> the wildcard label, so both publish at `_acme-challenge.example.test` — two different values,
> at one name, live at the same time. An implementation that "sets" the record satisfies the
> second authorization and fails the first, and the failure appears only on the
> apex-plus-wildcard combination. No unit test catches it; a real CA validating both does.

```
PEBBLE_DNS_SERVER=challtestsrv:8053 docker compose --profile acmetest up -d pebble challtestsrv
```

`PEBBLE_DNS_SERVER` is load-bearing. Pebble defaults to Docker's resolver (`127.0.0.11:53`),
which resolves container names and nothing else — enough for HTTP-01, where validation is an
HTTP fetch, and useless for DNS-01, where the TXT record *is* the proof. The two harnesses are
separate runs, not one configuration.

Then point the edge at both:

```
Edge__Certificates__AcmeDirectory=https://pebble:14000/dir
Edge__Certificates__AcceptInsecureAcmeDirectory=true
Edge__Certificates__TlsEnabled=true
Edge__Certificates__ContactEmail=acme-test@example.invalid
Edge__Certificates__ManagedIdentifiers=example.test,*.example.test
Edge__Dns__ChallTestSrvUrl=http://challtestsrv:8055
```

`Edge__Dns__ChallTestSrvUrl` redirects where the platform proves it controls a domain, so it is
refused unless `AcceptInsecureAcmeDirectory` is set as well — which is itself refused for a
Let's Encrypt directory. Two switches, because a deployment that had this on by accident would
obtain certificates whose validation nobody performed.

The managed certificate is seeded on start and ordered by the first renewal sweep. What to look
for, in `docker compose logs edge`:

1. `Requesting a certificate for example.test, *.example.test … over DNS-01`
2. **Two** `Published DNS-01 challenge record _acme-challenge.example.test` lines — one per
   authorization. One line here is the bug.
3. `2 value(s) at _acme-challenge.example.test are live on all 1 authoritative nameserver(s)`
4. `Issuance of managed certificate 'Platform wildcard' succeeded`

And confirm both records really coexisted, rather than the second having replaced the first:

```sh
docker compose exec challtestsrv sh -c 'wget -qO- http://localhost:8055/print-request-history' || true
dig @127.0.0.1 -p 8053 TXT _acme-challenge.example.test   # while an order is in flight
```

Afterwards, `edge.certificates` should hold one row whose `subject_alternative_names` carries
both names, and `edge.managed_certificate_attempts` one successful row. Ask the store for a
subdomain it has never seen — `sub.example.test` — and it should answer with that same
certificate; that is the wildcard fallback doing its job.

## What this does not prove

That Let's Encrypt will issue. Pebble does not enforce rate limits, does not check CAA
records, and validates from inside the compose network rather than from the internet. It
proves our half of the conversation is correct; the first real issuance still happens against
the Let's Encrypt **staging** directory, which is what `ACME_DIRECTORY` defaults to.
