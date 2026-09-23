# data/

`countries.xlsx` is **generated here on first run** and is intentionally not
committed. Edit it freely: it is the source of truth once it exists, and it is
how this tool is adapted to other Football Manager editions.

The canonical dataset lives in the repository as plain text at
[`../src/FootballManagerRegenNotifier.Core/Catalog/countries.seed.psv`](../src/FootballManagerRegenNotifier.Core/Catalog/countries.seed.psv),
so that dataset changes show up as a readable diff in a pull request rather than
as an opaque binary blob.

Delete `countries.xlsx` and restart to regenerate it from the seed.

See [LICENSE-DATA.md](LICENSE-DATA.md) for provenance, accuracy caveats and
licensing. The dataset is **not** covered by the project's MIT licence.

## Sheet layout

`Countries` sheet, one row per intake window:

| Column | Notes |
|---|---|
| `Code` | Unique. Your country selections are saved against this, not against the name, so renaming a country does not clear your picks. |
| `Country` | Display name. |
| `Continent` | Grouping header in the dashboard. Any value works; new ones create new groups. |
| `YouthRating` | 0–200. Drives sorting and the "Select Top Tier" bulk action. |
| `StartMonth`, `StartDay` | When the intake window opens. This is what raises the alert. |
| `EndMonth`, `EndDay` | When it closes. Leave blank for a single-day window. |
| `Confidence` | `High`, `Medium` or `Low`. Shown in the dashboard. |
| `EnabledByDefault` | Ticked on a fresh install. |
| `Notes` | Free text, shown as a tooltip. |

A row whose start date is missing or out of range is skipped with a warning in
the Activity Log; the rest of the file still loads.
