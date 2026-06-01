# Parser Fixtures

Save small HTML samples here when a parser rule is added or fixed. These files are not loaded by the plugin at runtime; they are development fixtures for repeatable scraper checks.

Recommended files:

- `lodestone-special-make-it-rain.html`
- `lodestone-topic-duty-commenced.html`
- `lodestone-topic-producer-live.html`
- `lodestone-topic-eternal-bonding-restricted.html`
- `lodestone-maintenance.html`
- `gamerescape-you-otter-be-there.html`

Each fixture should be trimmed to the smallest useful HTML that still includes dates, title, summary body, images, and reward or location data. Keep the source URL in a comment near the top.

When parser tests are added, they should assert:

- entry kind
- title
- start/end dates
- selected hero image
- parsed summary
- rewards, requirements, or location fields when available
