# Licensing of the youth intake dataset

**The MIT licence in `/LICENSE` covers the source code of this project. It does
not cover the dataset described here.**

## What the dataset is

The youth intake windows and nation youth ratings shipped with this tool are
**third-party factual data**, compiled from public community sources, corrected,
and redistributed with attribution. No rights are asserted over it by this
project.

The canonical copy lives at
[`src/FootballManagerRegenNotifier.Core/Catalog/countries.seed.psv`](../src/FootballManagerRegenNotifier.Core/Catalog/countries.seed.psv)
in plain text so that changes to it are reviewable in a diff. The application
writes it out to `data/countries.xlsx` on first run, and the spreadsheet is the
source of truth from then on.

## Attribution

Youth intake windows compiled from Passion4FM, ["Football Manager Youth Intake
Dates"](https://www.passion4fm.com/football-manager-youth-intake-dates/),
independently re-derived and corrected.

Nation youth ratings (0–200 scale) from FM Scout, ["All Nations Youth Ratings in
FM22"](https://www.fmscout.com/a-fm22-youth-ratings-all-nations.html).

## Accuracy

The source data is **FM24-era and unverified against FM26**. It is a cold-start
prior, not fact. Every row carries a `Confidence` value, and the application
shows it:

| Confidence | Meaning |
|---|---|
| `High` | Corroborated, and the nation is playable with a plausible multi-day window. |
| `Medium` | Sourced but uncorroborated, or the nation is inactive and carries a shared default date. |
| `Low` | Known to be ambiguous or stale. Listed so it can be corrected, not because it is trusted. |

Known limitations, all of them documented rather than hidden:

- Nine African and Middle-Eastern nations share a single `08/04` date. That is an
  inactive-nation default in the source, not nine independent observations.
- Egypt and Lithuania became playable in FM26 but still carry the inactive
  default, so their dates are probably wrong.
- Afghanistan's source entry literally reads "21/09 **or** 22/09".
- Six playable nations (Canada, Australia, China, India, South Korea, Indonesia)
  are recorded with single-day windows, which contradicts the source's own rule
  that more active divisions mean longer intakes. Suspect the end date.
- **Absent from the source entirely**, though playable in FM26: U.A.E.,
  Hong Kong, Malaysia, Singapore.
- **Absent and significant**: Nigeria (youth rating 120) and DR Congo (101) both
  out-rank nations that are listed.

Correct it against your own save and, if you would, open a pull request against
the `.psv` so everyone benefits.

## Trademarks

Football Manager is a trademark of Sports Interactive and SEGA. This project is
unofficial and is **not** affiliated with, endorsed by or sponsored by Sports
Interactive, SEGA, Passion4FM or FM Scout.

## If you are a rights holder

If you object to the inclusion of this data, please open an issue and it will be
removed. The application is built to run without it: with no dataset present it
starts normally, reports an empty country list, and continues to read the game
clock.
