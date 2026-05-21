# curl examples — `POST /fraud-score` and `GET /ready`

The stack listens on **`http://localhost:9999`** (HAProxy → API). Bring it up with `docker compose up -d` and wait for `/ready` to return 200 before sending `/fraud-score` traffic.

All payloads below are taken from `resources/example-payloads.json` and from `docs/br/REGRAS_DE_DETECCAO.md` (the legit / fraud canonical examples).

---

## `GET /ready`

```bash
curl -i http://localhost:9999/ready
```

- `200 OK` once the loader has populated the Qdrant collection with ≥3M points.
- `503 Service Unavailable` otherwise.

Show only the status code:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:9999/ready
```

---

## `POST /fraud-score` — legit (no prior transaction)

The canonical "low-value, near home, known merchant" example. Expected: `approved=true`, `fraud_score=0.0`.

```bash
curl -s -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{
    "id": "tx-1329056812",
    "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
    "customer":    { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
    "merchant":    { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
    "terminal":    { "is_online": false, "card_present": true, "km_from_home": 29.23 },
    "last_transaction": null
  }'
```

---

## `POST /fraud-score` — fraud (high-value, far from home, unknown merchant)

Expected: `approved=false`, `fraud_score=1.0`.

```bash
curl -s -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{
    "id": "tx-3330991687",
    "transaction": { "amount": 9505.97, "installments": 10, "requested_at": "2026-03-14T05:15:12Z" },
    "customer":    { "avg_amount": 81.28, "tx_count_24h": 20, "known_merchants": ["MERC-008", "MERC-007", "MERC-005"] },
    "merchant":    { "id": "MERC-068", "mcc": "7802", "avg_amount": 54.86 },
    "terminal":    { "is_online": false, "card_present": true, "km_from_home": 952.27 },
    "last_transaction": null
  }'
```

---

## `POST /fraud-score` — with `last_transaction` present

Travels indices 5 and 6 of the vector (otherwise sentinel `-1`).

```bash
curl -s -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{
    "id": "tx-3576980410",
    "transaction": { "amount": 384.88, "installments": 3, "requested_at": "2026-03-11T20:23:35Z" },
    "customer":    { "avg_amount": 769.76, "tx_count_24h": 3, "known_merchants": ["MERC-009", "MERC-001"] },
    "merchant":    { "id": "MERC-001", "mcc": "5912", "avg_amount": 298.95 },
    "terminal":    { "is_online": false, "card_present": true, "km_from_home": 13.71 },
    "last_transaction": { "timestamp": "2026-03-11T14:58:35Z", "km_from_current": 18.86 }
  }'
```

---

## Reading the response

A successful response always has the fixed schema below; `fraud_score` is a multiple of `0.2`:

```json
{ "approved": true, "fraud_score": 0.0 }
```

Pretty-print with `jq` and include the HTTP status:

```bash
curl -s -w '\n[%{http_code}] %{time_total}s\n' -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d @resources/example-payloads.json \
  | jq .
```

> Note: `example-payloads.json` is an array. To loop over each element, see the "batch" snippet below.

---

## Error cases

Malformed JSON → `400 Bad Request`:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{ "id": "broken'
```

Missing required field (here, no `customer`) → `400 Bad Request`:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{
    "id": "tx-bad",
    "transaction": { "amount": 10, "installments": 1, "requested_at": "2026-03-11T18:45:53Z" },
    "merchant":    { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
    "terminal":    { "is_online": false, "card_present": true, "km_from_home": 1.0 },
    "last_transaction": null
  }'
```

Qdrant unreachable → `503 Service Unavailable` (try `docker compose stop qdrant`, then re-issue any of the success payloads above).

---

## Batch — replay all example payloads

```bash
jq -c '.[]' resources/example-payloads.json \
  | while read -r payload; do
      curl -s -X POST http://localhost:9999/fraud-score \
        -H 'Content-Type: application/json' \
        -d "$payload" \
      | jq -c '{id: input_filename, response: .}' --arg id "$(echo "$payload" | jq -r .id)" --slurpfile _ /dev/null
    done
```

A simpler variant that just prints the response per `id`:

```bash
jq -c '.[]' resources/example-payloads.json \
  | while read -r payload; do
      id=$(echo "$payload" | jq -r .id)
      printf '%-18s ' "$id"
      curl -s -X POST http://localhost:9999/fraud-score \
        -H 'Content-Type: application/json' \
        -d "$payload"
      printf '\n'
    done
```

---

## PowerShell equivalents

For Windows shells without curl:

```powershell
# /ready
(Invoke-WebRequest -Uri http://localhost:9999/ready -UseBasicParsing).StatusCode

# /fraud-score (legit example)
$body = @'
{
  "id": "tx-1329056812",
  "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
  "customer":    { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
  "merchant":    { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
  "terminal":    { "is_online": false, "card_present": true, "km_from_home": 29.23 },
  "last_transaction": null
}
'@
Invoke-RestMethod -Uri http://localhost:9999/fraud-score -Method POST -ContentType 'application/json' -Body $body
```
