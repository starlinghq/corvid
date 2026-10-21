Fixture: 50 texts, 57 labelled values (person 16, email 8, phone number 9, address 7, account number 8, credential 9); 10 texts with no PII. Labels: person, email, phone number, address, account number, credential.

### Threshold 0.3

| Label | Gold | Recall, same label | Recall, any label (exact) | Recall, any label (covered) | Precision, same label |
| --- | --- | --- | --- | --- | --- |
| person | 16 | 0.938 (15/16) | 0.938 (15/16) | 1.000 (16/16) | 0.615 (16/26) (26 spans) |
| email | 8 | 1.000 (8/8) | 1.000 (8/8) | 1.000 (8/8) | 0.800 (8/10) (10 spans) |
| phone number | 9 | 1.000 (9/9) | 1.000 (9/9) | 1.000 (9/9) | 0.900 (9/10) (10 spans) |
| address | 7 | 0.429 (3/7) | 0.429 (3/7) | 0.429 (3/7) | 0.750 (6/8) (8 spans) |
| account number | 8 | 0.875 (7/8) | 1.000 (8/8) | 1.000 (8/8) | 0.438 (7/16) (16 spans) |
| credential | 9 | 0.000 (0/9) | 0.556 (5/9) | 0.667 (6/9) | 0.000 (0/11) (11 spans) |
| all | 57 | 0.737 (42/57) | 0.842 (48/57) | 0.877 (50/57) | 0.568 (46/81) (81 spans) |

### Threshold 0.4 (the gate's default)

| Label | Gold | Recall, same label | Recall, any label (exact) | Recall, any label (covered) | Precision, same label |
| --- | --- | --- | --- | --- | --- |
| person | 16 | 0.938 (15/16) | 0.938 (15/16) | 1.000 (16/16) | 0.615 (16/26) (26 spans) |
| email | 8 | 1.000 (8/8) | 1.000 (8/8) | 1.000 (8/8) | 0.800 (8/10) (10 spans) |
| phone number | 9 | 1.000 (9/9) | 1.000 (9/9) | 1.000 (9/9) | 0.900 (9/10) (10 spans) |
| address | 7 | 0.429 (3/7) | 0.429 (3/7) | 0.429 (3/7) | 0.750 (6/8) (8 spans) |
| account number | 8 | 0.875 (7/8) | 1.000 (8/8) | 1.000 (8/8) | 0.467 (7/15) (15 spans) |
| credential | 9 | 0.000 (0/9) | 0.444 (4/9) | 0.556 (5/9) | 0.000 (0/10) (10 spans) |
| all | 57 | 0.737 (42/57) | 0.825 (47/57) | 0.860 (49/57) | 0.582 (46/79) (79 spans) |

Values the gate would send at its default threshold (not covered by any span):

- address: `221B Baker Street, London NW1 6XE` (text 3)
- credential: `hunter2-Staging!` (text 5)
- address: `1600 Pennsylvania Avenue NW, Washington, DC 20500` (text 11)
- credential: `Tr0ub4dor&3` (text 14)
- credential: `ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8` (text 15)
- address: `742 Evergreen Terrace, Springfield` (text 22)
- credential: `correct-horse-battery-staple` (text 36)
- address: `Flat 4, 18 Rue de Rivoli, 75004 Paris` (text 38)

Spans with no labelled value behind them (redacted needlessly):

- person: `you` 0.61 (text 2)
- person: `me` 0.45 (text 2)
- credential: `staging database password` 0.94 (text 5)
- credential: `API key` 1.00 (text 6)
- person: `her` 0.45 (text 10)
- person: `We` 0.43 (text 11)
- person: `IBAN` 0.87 (text 12)
- account number: `4242` 0.98 (text 13)
- person: `admin` 0.83 (text 14)
- credential: `password` 0.94 (text 14)
- credential: `CI` 0.96 (text 15)
- credential: `AWS secret access key` 0.96 (text 19)
- account number: `social security number` 0.47 (text 31)
- address: `cabin` 0.44 (text 32)
- credential: `wifi password` 1.00 (text 32)
- credential: `passphrase` 0.90 (text 36)
- credential: `SSH private key` 0.87 (text 36)
- credential: `keys` 1.00 (text 41)
- person: `platform team` 0.94 (text 42)
- account number: `main` 0.69 (text 43)
- person: `Grandma` 1.00 (text 45)
- account number: `5432/prod` 0.96 (text 47)
- credential: `postgres` 0.43 (text 47)
- email: `@devops` 1.00 (text 49)

### Threshold 0.5

| Label | Gold | Recall, same label | Recall, any label (exact) | Recall, any label (covered) | Precision, same label |
| --- | --- | --- | --- | --- | --- |
| person | 16 | 0.938 (15/16) | 0.938 (15/16) | 1.000 (16/16) | 0.727 (16/22) (22 spans) |
| email | 8 | 1.000 (8/8) | 1.000 (8/8) | 1.000 (8/8) | 0.800 (8/10) (10 spans) |
| phone number | 9 | 1.000 (9/9) | 1.000 (9/9) | 1.000 (9/9) | 0.900 (9/10) (10 spans) |
| address | 7 | 0.429 (3/7) | 0.429 (3/7) | 0.429 (3/7) | 0.857 (6/7) (7 spans) |
| account number | 8 | 0.875 (7/8) | 1.000 (8/8) | 1.000 (8/8) | 0.538 (7/13) (13 spans) |
| credential | 9 | 0.000 (0/9) | 0.333 (3/9) | 0.444 (4/9) | 0.000 (0/9) (9 spans) |
| all | 57 | 0.737 (42/57) | 0.807 (46/57) | 0.842 (48/57) | 0.648 (46/71) (71 spans) |
